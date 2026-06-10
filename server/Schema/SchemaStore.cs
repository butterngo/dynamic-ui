using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicUi.Server.Data;
using DynamicUi.Server.Hubs;
using Json.Patch;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DynamicUi.Server.Schema;

/// <summary>
/// The single authoritative owner of the UI schema. Validate → persist → broadcast happens
/// here, atomically (serialized by a semaphore), so MCP tool calls and rollbacks can never
/// interleave into a half-applied state. Per-node last-write-wins falls out of the serialization.
/// </summary>
public class SchemaStore
{
    private readonly IDbContextFactory<UiSchemaDb> _dbFactory;
    private readonly IHubContext<UiHub> _hub;
    private readonly ILogger<SchemaStore> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SchemaStore(IDbContextFactory<UiSchemaDb> dbFactory, IHubContext<UiHub> hub, ILogger<SchemaStore> log)
    {
        _dbFactory = dbFactory;
        _hub = hub;
        _log = log;
    }

    public record SchemaSnapshot(int Version, string Schema);
    public record ApplyResult(bool Ok, int Version, string? Schema, string? Error)
    {
        public static ApplyResult Reject(string error) => new(false, 0, null, error);
    }
    public record HistoryItem(int Version, DateTimeOffset CreatedAt, string Op);

    /// <summary>Seeds version 1 with an empty Screen if the store is empty.</summary>
    public async Task EnsureSeededAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        if (await db.SchemaVersions.AnyAsync()) return;

        var seed = """
        {
          "id": "root",
          "type": "LauncherWindow",
          "props": { "dark": false },
          "children": [
            {
              "id": "titlebar",
              "type": "TitleBar",
              "props": { "title": "Mantu Apps Launcher", "subtitle": "HOP · v2.4", "icon": "M", "iconColor": "#7C3AED" }
            },
            {
              "id": "toolbar",
              "type": "Toolbar",
              "children": [
                {
                  "id": "env",
                  "type": "EnvSegment",
                  "props": {
                    "options": [
                      { "label": "DEV", "color": "#f0b429" },
                      { "label": "STAGING", "active": true, "color": "#6cdec4" },
                      { "label": "PROD", "color": "#e85d5d" }
                    ]
                  }
                },
                { "id": "search", "type": "SearchBar", "props": { "placeholder": "Search apps…  (Ctrl+K)" } },
                { "id": "refresh", "type": "ToolButton", "props": { "icon": "⟳" } },
                { "id": "density", "type": "ToolButton", "props": { "icon": "▦" } }
              ]
            },
            {
              "id": "body",
              "type": "Body",
              "children": [
                {
                  "id": "sidebar",
                  "type": "Sidebar",
                  "children": [
                    {
                      "id": "ws",
                      "type": "SideSection",
                      "props": {
                        "label": "Workspaces",
                        "items": [
                          { "icon": "▦", "label": "All apps", "count": 24, "active": true },
                          { "icon": "★", "label": "Pinned", "count": 5 },
                          { "icon": "◷", "label": "Recent", "count": 8 }
                        ]
                      }
                    },
                    {
                      "id": "cats",
                      "type": "SideSection",
                      "props": {
                        "label": "Categories",
                        "items": [
                          { "icon": "◆", "label": "Internal", "count": 12 },
                          { "icon": "◯", "label": "Web", "count": 7 },
                          { "icon": "⚙", "label": "DevOps", "count": 5 }
                        ]
                      }
                    },
                    {
                      "id": "envcard",
                      "type": "EnvCard",
                      "props": { "title": "STAGING", "subtitle": "eu-west-1 · healthy", "footer": "Last sync 2m ago", "dotColor": "#6cdec4" }
                    }
                  ]
                },
                {
                  "id": "mainpanel",
                  "type": "MainPanel",
                  "children": [
                    { "id": "mainhead", "type": "MainHeader", "props": { "title": "All apps", "count": 24, "subtitle": "Showing apps available for STAGING" } },
                    {
                      "id": "grid",
                      "type": "CardGrid",
                      "children": [
                        {
                          "id": "app-timesheet",
                          "type": "AppCard",
                          "props": {
                            "name": "Timesheet", "short": "Weekly time entry", "chip": "Internal", "pinned": true,
                            "rows": [ { "label": "Type", "value": "Web app" }, { "label": "URL", "value": "staging.timesheet.mantu.io" } ],
                            "actions": [ { "label": "Open", "primary": true }, { "label": "⋯" } ]
                          }
                        },
                        {
                          "id": "app-talentsoft",
                          "type": "AppCard",
                          "props": {
                            "name": "Talentsoft", "short": "HR & talent management", "chip": "HR",
                            "rows": [ { "label": "Type", "value": "SaaS" }, { "label": "URL", "value": "mantu.talent-soft.com" } ],
                            "actions": [ { "label": "Open", "primary": true }, { "label": "⋯" } ]
                          }
                        },
                        {
                          "id": "app-pipeline",
                          "type": "AppCard",
                          "props": {
                            "name": "Pipeline", "short": "CI/CD dashboard", "chip": "DevOps", "selected": true,
                            "rows": [ { "label": "Type", "value": "Internal" }, { "label": "URL", "value": "ci.mantu.io/pipeline" } ],
                            "actions": [ { "label": "Open", "primary": true }, { "label": "⋯" } ]
                          }
                        },
                        {
                          "id": "app-knowledge",
                          "type": "AppCard",
                          "props": {
                            "name": "Knowledge", "short": "Internal wiki & docs", "chip": "Docs",
                            "rows": [ { "label": "Type", "value": "Web app" }, { "label": "URL", "value": "wiki.mantu.io" } ],
                            "actions": [ { "label": "Open", "primary": true }, { "label": "⋯" } ]
                          }
                        }
                      ]
                    }
                  ]
                }
              ]
            },
            {
              "id": "statusbar",
              "type": "StatusBar",
              "props": { "left": "● Connected", "mid": "24 apps · 5 pinned", "right": "STAGING · eu-west-1" }
            }
          ]
        }
        """;
        db.SchemaVersions.Add(new SchemaVersion { Version = 1, Json = Compact(seed), CreatedAt = DateTimeOffset.UtcNow });
        db.PatchHistory.Add(new PatchEntry { Version = 1, Op = "seed", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    public async Task<SchemaSnapshot> GetCurrentAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var current = await db.SchemaVersions.OrderByDescending(s => s.Version).FirstAsync();
        return new SchemaSnapshot(current.Version, current.Json);
    }

    public async Task<IReadOnlyList<HistoryItem>> GetHistoryAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.PatchHistory
            .OrderBy(p => p.Version)
            .Select(p => new HistoryItem(p.Version, p.CreatedAt, p.Op))
            .ToListAsync();
    }

    /// <summary>Apply an RFC-6902 JSON Patch. Rejected patches leave the store and version untouched.</summary>
    public async Task<ApplyResult> ApplyPatchAsync(string patchJson)
    {
        JsonPatch? patch;
        try
        {
            patch = JsonSerializer.Deserialize<JsonPatch>(patchJson);
        }
        catch (Exception ex)
        {
            return ApplyResult.Reject($"Malformed JSON Patch: {ex.Message}");
        }
        if (patch is null) return ApplyResult.Reject("JSON Patch was empty or null.");

        await _gate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var current = await db.SchemaVersions.OrderByDescending(s => s.Version).FirstAsync();

            var root = JsonNode.Parse(current.Json);

            PatchResult patchResult;
            try
            {
                patchResult = patch.Apply(root);
            }
            catch (Exception ex)
            {
                return ApplyResult.Reject($"Patch could not be applied: {ex.Message}");
            }
            if (patchResult.Error is not null)
                return ApplyResult.Reject($"Patch rejected: {patchResult.Error}");

            var validation = PatchValidator.Validate(patchResult.Result);
            if (!validation.Ok)
                return ApplyResult.Reject(validation.Error!);

            var newVersion = current.Version + 1;
            var newJson = patchResult.Result!.ToJsonString();
            db.SchemaVersions.Add(new SchemaVersion { Version = newVersion, Json = newJson, CreatedAt = DateTimeOffset.UtcNow });
            db.PatchHistory.Add(new PatchEntry { Version = newVersion, Op = Compact(patchJson), CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();

            await BroadcastAsync(newVersion, newJson, patchJson);
            _log.LogInformation("Applied patch -> version {Version}", newVersion);
            return new ApplyResult(true, newVersion, newJson, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Restore a prior version's tree as a new version, then broadcast.</summary>
    public async Task<ApplyResult> RollbackAsync(int targetVersion)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var target = await db.SchemaVersions.FirstOrDefaultAsync(s => s.Version == targetVersion);
            if (target is null) return ApplyResult.Reject($"Version {targetVersion} not found in history.");

            var current = await db.SchemaVersions.OrderByDescending(s => s.Version).FirstAsync();
            var newVersion = current.Version + 1;

            db.SchemaVersions.Add(new SchemaVersion { Version = newVersion, Json = target.Json, CreatedAt = DateTimeOffset.UtcNow });
            db.PatchHistory.Add(new PatchEntry { Version = newVersion, Op = $"rollback to v{targetVersion}", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();

            await BroadcastAsync(newVersion, target.Json, patchJson: "[]");
            _log.LogInformation("Rolled back to v{Target} -> version {Version}", targetVersion, newVersion);
            return new ApplyResult(true, newVersion, target.Json, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task BroadcastAsync(int version, string schemaJson, string patchJson) =>
        _hub.Clients.All.SendAsync("SchemaChanged", new
        {
            version,
            schema = JsonNode.Parse(schemaJson),
            patch = JsonNode.Parse(patchJson),
        });

    private static string Compact(string json) => JsonNode.Parse(json)!.ToJsonString();
}

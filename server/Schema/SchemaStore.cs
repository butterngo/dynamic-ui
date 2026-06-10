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

    /// <summary>
    /// Seeds version 1 with the canonical dynamic-ui example (if the store is empty): the "Lumen"
    /// workspace members admin — a header + members table + footer, styled purely via Tailwind
    /// props.className (spec v1.0). System.Text.Json re-escapes any non-ASCII glyphs to \uXXXX on
    /// persist via Compact().
    /// </summary>
    public async Task EnsureSeededAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        if (await db.SchemaVersions.AnyAsync()) return;

        var seed = """
        {
          "id": "page-root",
          "type": "container",
          "props": { "className": "min-h-screen bg-gray-50 dark:bg-gray-950 text-gray-900 dark:text-gray-100 antialiased flex flex-col transition-colors" },
          "children": [
            {
              "id": "site-header",
              "type": "header",
              "props": { "className": "sticky top-0 z-10 bg-white/80 dark:bg-gray-900/80 backdrop-blur border-b border-gray-200 dark:border-gray-800 px-8 h-16 flex items-center justify-between" },
              "children": [
                {
                  "id": "brand",
                  "type": "container",
                  "props": { "className": "flex items-center gap-2.5" },
                  "children": [
                    { "id": "brand-mark", "type": "container", "props": { "className": "w-7 h-7 rounded-lg bg-blue-600 flex items-center justify-center text-white font-bold text-sm shadow-sm", "text": "L" }, "children": [] },
                    { "id": "brand-name", "type": "text", "props": { "as": "span", "text": "Lumen", "className": "font-semibold text-[17px] tracking-tight text-gray-900 dark:text-gray-100" } }
                  ]
                },
                {
                  "id": "main-nav",
                  "type": "container",
                  "props": { "className": "hidden md:flex items-center gap-7 text-sm font-medium text-gray-500 dark:text-gray-400" },
                  "children": [
                    { "id": "nav-dashboard", "type": "link", "props": { "href": "#", "text": "Dashboard", "className": "hover:text-gray-900 dark:hover:text-gray-100 transition-colors" } },
                    { "id": "nav-members", "type": "link", "props": { "href": "#", "text": "Members", "className": "text-gray-900 dark:text-gray-100" } },
                    { "id": "nav-billing", "type": "link", "props": { "href": "#", "text": "Billing", "className": "hover:text-gray-900 dark:hover:text-gray-100 transition-colors" } },
                    { "id": "nav-settings", "type": "link", "props": { "href": "#", "text": "Settings", "className": "hover:text-gray-900 dark:hover:text-gray-100 transition-colors" } }
                  ]
                },
                {
                  "id": "header-actions",
                  "type": "container",
                  "props": { "className": "flex items-center gap-2" },
                  "children": [
                    {
                      "id": "theme-toggle",
                      "type": "button",
                      "props": { "text": "☾ Dark mode", "onClick": { "action": "toggleTheme" }, "className": "inline-flex items-center gap-1.5 text-sm font-medium px-3 py-2 rounded-lg border border-gray-200 dark:border-gray-700 text-gray-600 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-800 transition-colors" }
                    },
                    {
                      "id": "header-cta",
                      "type": "button",
                      "props": { "text": "Invite member", "className": "inline-flex items-center gap-1.5 bg-blue-600 hover:bg-blue-700 text-white text-sm font-medium px-3.5 py-2 rounded-lg shadow-sm transition-colors" }
                    }
                  ]
                }
              ]
            },
            {
              "id": "main",
              "type": "container",
              "props": { "className": "flex-1 w-full max-w-6xl mx-auto px-8 py-10" },
              "children": [
                {
                  "id": "page-head",
                  "type": "container",
                  "props": { "className": "flex items-end justify-between gap-6 mb-7" },
                  "children": [
                    {
                      "id": "page-head-text",
                      "type": "container",
                      "props": { "className": "space-y-1.5" },
                      "children": [
                        { "id": "page-title", "type": "text", "props": { "as": "h1", "text": "Team members", "className": "text-2xl font-semibold tracking-tight text-gray-900 dark:text-gray-100" } },
                        { "id": "page-subtitle", "type": "text", "props": { "as": "p", "text": "Manage who has access to the Lumen workspace and their roles.", "className": "text-sm text-gray-500 dark:text-gray-400 max-w-xl" } }
                      ]
                    },
                    { "id": "members-count", "type": "text", "props": { "as": "span", "text": "8 members", "className": "shrink-0 text-xs font-medium text-gray-500 dark:text-gray-400 bg-gray-100 dark:bg-gray-800 border border-gray-200 dark:border-gray-700 px-2.5 py-1 rounded-full" } }
                  ]
                },
                {
                  "id": "users-table",
                  "type": "table",
                  "props": {
                    "className": "w-full border border-gray-200 dark:border-gray-800 rounded-xl overflow-hidden bg-white dark:bg-gray-900 text-sm shadow-sm",
                    "headerClassName": "bg-gray-50 dark:bg-gray-800/50 text-left text-gray-500 dark:text-gray-400 text-xs uppercase tracking-wide",
                    "thClassName": "px-4 py-3 font-medium",
                    "rowClassName": "border-t border-gray-100 dark:border-gray-800 hover:bg-gray-50/70 dark:hover:bg-gray-800/40 transition-colors",
                    "tdClassName": "px-4 py-3 align-middle",
                    "columns": [
                      { "key": "name", "label": "Name", "variant": "user" },
                      { "key": "email", "label": "Email", "tdClassName": "px-4 py-3 text-gray-500 dark:text-gray-400" },
                      { "key": "role", "label": "Role" },
                      { "key": "status", "label": "Status", "variant": "badge" }
                    ],
                    "rows": [
                      { "name": "Mara Whitfield", "initials": "MW", "email": "mara@lumen.app", "role": "Owner", "status": "Active", "lastActive": "2 min ago" },
                      { "name": "Devin Okafor", "initials": "DO", "email": "devin@lumen.app", "role": "Admin", "status": "Active", "lastActive": "1 hr ago" },
                      { "name": "Priya Raman", "initials": "PR", "email": "priya@lumen.app", "role": "Editor", "status": "Active", "lastActive": "3 hr ago" },
                      { "name": "Tomás Herrera", "initials": "TH", "email": "tomas@lumen.app", "role": "Editor", "status": "Invited", "lastActive": "—" },
                      { "name": "Sofia Nilsson", "initials": "SN", "email": "sofia@lumen.app", "role": "Viewer", "status": "Active", "lastActive": "Yesterday" },
                      { "name": "Jamal Carter", "initials": "JC", "email": "jamal@lumen.app", "role": "Viewer", "status": "Suspended", "lastActive": "12 days ago" },
                      { "name": "Aiko Tanaka", "initials": "AT", "email": "aiko@lumen.app", "role": "Editor", "status": "Invited", "lastActive": "—" },
                      { "name": "Lena Vogel", "initials": "LV", "email": "lena@lumen.app", "role": "Admin", "status": "Active", "lastActive": "5 min ago" }
                    ]
                  }
                }
              ]
            },
            {
              "id": "site-footer",
              "type": "footer",
              "props": { "className": "border-t border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-900 px-8 py-5 text-center text-xs text-gray-400 dark:text-gray-500", "text": "Lumen — rendered live from ui-schema.json · dynamic-ui PoC" }
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

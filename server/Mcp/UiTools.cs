using System.ComponentModel;
using System.Text.Json.Nodes;
using DynamicUi.Server.Schema;
using ModelContextProtocol.Server;

namespace DynamicUi.Server.Mcp;

/// <summary>
/// The ui_* MCP tool surface, callable from Claude Desktop over stdio. Every mutating tool routes
/// through <see cref="SchemaStore"/> (validate → persist → broadcast). Paths are RFC-6901 JSON
/// Pointers into the schema tree, e.g. "/children/0/props/label". SchemaStore is injected by DI.
/// </summary>
[McpServerToolType]
public static class UiTools
{
    [McpServerTool(Name = "ui_get_schema")]
    [Description("Return the current UI schema (the full JSON tree) and its version.")]
    public static async Task<object> GetSchema(SchemaStore store)
    {
        var snap = await store.GetCurrentAsync();
        return new { snap.Version, schema = JsonNode.Parse(snap.Schema) };
    }

    [McpServerTool(Name = "ui_history")]
    [Description("List the version history: each version, when it was created, and the op that produced it.")]
    public static async Task<object> History(SchemaStore store)
    {
        var history = await store.GetHistoryAsync();
        return new { count = history.Count, versions = history };
    }

    [McpServerTool(Name = "ui_apply_patch")]
    [Description("Apply an RFC-6902 JSON Patch (a JSON array of operations) to the UI schema. " +
                 "Rejected if malformed, if it targets a missing path, removes the root, introduces an " +
                 "unknown component type, or omits a component's required props.")]
    public static Task<object> ApplyPatch(
        SchemaStore store,
        [Description("RFC-6902 JSON Patch document, e.g. [{\"op\":\"replace\",\"path\":\"/props/title\",\"value\":\"Hi\"}]")] string patch)
        => Wrap(store.ApplyPatchAsync(patch));

    [McpServerTool(Name = "ui_set_prop")]
    [Description("Set a single prop on a component node. path is the JSON Pointer to the node " +
                 "(e.g. \"/children/0\"); value is a JSON literal (\"teal\", 42, true, or an object).")]
    public static Task<object> SetProp(
        SchemaStore store,
        [Description("JSON Pointer to the component node, e.g. \"/children/0\" or \"\" for the root")] string path,
        [Description("Prop name to set, e.g. \"label\" or \"color\"")] string name,
        [Description("JSON value, e.g. \"teal\", 42, true, {\"k\":\"v\"}")] string value)
    {
        var patch = new JsonArray
        {
            new JsonObject
            {
                ["op"] = "add",
                ["path"] = $"{path}/props/{name}",
                ["value"] = ParseValue(value),
            }
        };
        return Wrap(store.ApplyPatchAsync(patch.ToJsonString()));
    }

    [McpServerTool(Name = "ui_add_component")]
    [Description("Append a child component to a parent node. parentPath is the JSON Pointer to the " +
                 "parent (e.g. \"\" for the root). component is a JSON object with type/props/(children).")]
    public static Task<object> AddComponent(
        SchemaStore store,
        [Description("JSON Pointer to the parent node, e.g. \"\" for the root")] string parentPath,
        [Description("Component object JSON, e.g. {\"id\":\"b1\",\"type\":\"Button\",\"props\":{\"label\":\"Save\"}}")] string component)
    {
        var patch = new JsonArray
        {
            new JsonObject
            {
                ["op"] = "add",
                ["path"] = $"{parentPath}/children/-",
                ["value"] = ParseValue(component),
            }
        };
        return Wrap(store.ApplyPatchAsync(patch.ToJsonString()));
    }

    [McpServerTool(Name = "ui_remove_component")]
    [Description("Remove a component node by its JSON Pointer path, e.g. \"/children/1\".")]
    public static Task<object> RemoveComponent(
        SchemaStore store,
        [Description("JSON Pointer to the node to remove, e.g. \"/children/1\"")] string path)
    {
        var patch = new JsonArray
        {
            new JsonObject { ["op"] = "remove", ["path"] = path }
        };
        return Wrap(store.ApplyPatchAsync(patch.ToJsonString()));
    }

    [McpServerTool(Name = "ui_import_url")]
    [Description("Fetch a web page (static HTML only — no JS rendering) and extract it into the UI " +
                 "schema using the known component types. mode=\"replace\" (default) swaps the whole " +
                 "screen; mode=\"append\" adds the imported page under the root. Reversible via ui_rollback.")]
    public static async Task<object> ImportUrl(
        SchemaStore store,
        [Description("Absolute http(s) URL of the page to import, e.g. \"https://example.com\"")] string url,
        [Description("\"replace\" (default) or \"append\"")] string? mode = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new { ok = false, error = "url must be an absolute http(s) URL." };

        string html;
        try
        {
            using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
                return new { ok = false, error = $"Fetch failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}." };

            var mediaType = resp.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return new { ok = false, error = $"Expected an HTML page but got '{mediaType}'." };

            html = await ReadCappedAsync(resp, MaxPageBytes);
        }
        catch (Exception ex)
        {
            return new { ok = false, error = $"Fetch failed: {ex.Message}" };
        }

        var screen = await HtmlSchemaExtractor.ExtractAsync(html, uri);
        var append = string.Equals(mode, "append", StringComparison.OrdinalIgnoreCase);
        var patch = new JsonArray
        {
            new JsonObject
            {
                ["op"] = append ? "add" : "replace",
                ["path"] = append ? "/children/-" : "",
                ["value"] = screen,
            }
        };
        return await Wrap(store.ApplyPatchAsync(patch.ToJsonString()));
    }

    [McpServerTool(Name = "ui_drop_schema")]
    [Description("Clear the entire UI, resetting the schema to a blank Screen. Recorded as a new " +
                 "version and broadcast to all clients. Reversible via ui_rollback.")]
    public static Task<object> DropSchema(SchemaStore store)
    {
        var patch = new JsonArray
        {
            new JsonObject
            {
                ["op"] = "replace",
                ["path"] = "",
                ["value"] = new JsonObject { ["id"] = "root", ["type"] = "Screen" },
            }
        };
        return Wrap(store.ApplyPatchAsync(patch.ToJsonString()));
    }

    [McpServerTool(Name = "ui_rollback")]
    [Description("Restore a previous version of the schema (as a new version) and broadcast to all clients.")]
    public static Task<object> Rollback(
        SchemaStore store,
        [Description("The version number to roll back to")] int version)
        => Wrap(store.RollbackAsync(version));

    private static async Task<object> Wrap(Task<SchemaStore.ApplyResult> task)
    {
        var r = await task;
        return r.Ok
            ? new { ok = true, version = r.Version, schema = JsonNode.Parse(r.Schema!) }
            : new { ok = false, error = r.Error };
    }

    private const int MaxPageBytes = 2 * 1024 * 1024;

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("dynamic-ui-poc/1.0");
        return client;
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage resp, int maxBytes)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var buffered = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            buffered.Write(buffer, 0, read);
            if (buffered.Length > maxBytes)
                throw new InvalidOperationException($"Page exceeds the {maxBytes / 1024} KB import cap.");
        }
        return System.Text.Encoding.UTF8.GetString(buffered.ToArray());
    }

    private static JsonNode? ParseValue(string raw)
    {
        try { return JsonNode.Parse(raw); }
        catch { return JsonValue.Create(raw); } // fall back to treating it as a string literal
    }
}

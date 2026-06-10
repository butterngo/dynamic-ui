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

    private static JsonNode? ParseValue(string raw)
    {
        try { return JsonNode.Parse(raw); }
        catch { return JsonValue.Create(raw); } // fall back to treating it as a string literal
    }
}

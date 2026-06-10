namespace DynamicUi.Server.Schema;

/// <summary>
/// The fixed catalogue of renderable component types (the validation contract shared,
/// by convention, with the React renderer). A patch that introduces a type absent here,
/// or omits a type's required props, is rejected. Extend this to add components.
/// </summary>
public static class ComponentRegistry
{
    public static readonly IReadOnlyDictionary<string, string[]> Components =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // type           required prop names
            ["Screen"]    = Array.Empty<string>(),
            ["Container"] = Array.Empty<string>(),
            ["Stack"]     = Array.Empty<string>(),
            ["Text"]      = new[] { "text" },
            ["Heading"]   = new[] { "text" },
            ["Banner"]    = new[] { "text" },
            ["Button"]    = new[] { "label" },
            ["Input"]     = new[] { "name" },
            ["Image"]     = new[] { "src" },
        };

    public static bool IsKnown(string type) => Components.ContainsKey(type);

    public static string[] RequiredProps(string type) =>
        Components.TryGetValue(type, out var props) ? props : Array.Empty<string>();
}

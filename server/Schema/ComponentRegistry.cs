namespace DynamicUi.Server.Schema;

/// <summary>
/// The fixed catalogue of renderable component types (the validation contract shared,
/// by convention, with the React renderer). A patch that introduces a type absent here,
/// or omits a type's required props, is rejected. Extend this to add components.
///
/// This is the dynamic-ui spec v1.0 vocabulary: a small set of generic primitives whose
/// styling is carried entirely in <c>props.className</c> (Tailwind) — so a restyle is just
/// a className edit, never a new component type. See docs/knowledge/dynamic-ui-schema-spec.md.
/// </summary>
public static class ComponentRegistry
{
    public static readonly IReadOnlyDictionary<string, string[]> Components =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // type          required prop names        // notes
            ["container"] = Array.Empty<string>(),       // generic <div>; optional props.text; has children
            ["header"]    = Array.Empty<string>(),       // semantic <header>; has children
            ["footer"]    = Array.Empty<string>(),       // semantic <footer>; props.text or children
            ["text"]      = new[] { "text" },            // props.as selects tag (h1|h2|h3|p|span)
            ["link"]      = new[] { "text" },            // + href
            ["button"]    = new[] { "text" },            // + optional onClick={action,payload} (client-side, e.g. toggleTheme)
            ["table"]     = new[] { "columns" },         // columns[] + rows[] (static) or dataSource (API-bound)
        };

    public static bool IsKnown(string type) => Components.ContainsKey(type);

    public static string[] RequiredProps(string type) =>
        Components.TryGetValue(type, out var props) ? props : Array.Empty<string>();
}

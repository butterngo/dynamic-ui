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

            // --- App-launcher catalogue (Win11/WPF-look). Containers carry children;
            //     prop-driven composites use array props (options/items/rows/actions). ---
            ["LauncherWindow"] = Array.Empty<string>(),          // window chrome wrapper
            ["TitleBar"]       = new[] { "title" },              // + subtitle, icon, iconColor
            ["Toolbar"]        = Array.Empty<string>(),          // container
            ["EnvSegment"]     = new[] { "options" },            // options[]: {label, active?, color?}
            ["SearchBar"]      = Array.Empty<string>(),          // + placeholder
            ["ToolButton"]     = Array.Empty<string>(),          // + icon
            ["Body"]           = Array.Empty<string>(),          // container (sidebar | main)
            ["Sidebar"]        = Array.Empty<string>(),          // container
            ["SideSection"]    = new[] { "label" },              // items[]: {icon?, label, count?, active?}
            ["EnvCard"]        = new[] { "title" },              // + subtitle, footer, dotColor
            ["MainPanel"]      = Array.Empty<string>(),          // container
            ["MainHeader"]     = new[] { "title" },              // + count, subtitle
            ["CardGrid"]       = Array.Empty<string>(),          // container
            ["AppCard"]        = new[] { "name" },               // + short, chip, thumb, pinned, selected, rows[], actions[]
            ["StatusBar"]      = Array.Empty<string>(),          // + left, mid, right
        };

    public static bool IsKnown(string type) => Components.ContainsKey(type);

    public static string[] RequiredProps(string type) =>
        Components.TryGetValue(type, out var props) ? props : Array.Empty<string>();
}

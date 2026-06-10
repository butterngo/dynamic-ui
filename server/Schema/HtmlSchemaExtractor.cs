using System.Text;
using System.Text.Json.Nodes;
using AngleSharp;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;

namespace DynamicUi.Server.Schema;

/// <summary>
/// Maps static HTML to a Screen tree built only from <see cref="ComponentRegistry"/> types, so the
/// result always passes <see cref="PatchValidator"/>. Visual fidelity is preserved by resolving each
/// element's cascaded CSS (inline <c>style</c>, embedded <c>&lt;style&gt;</c>, and — best effort —
/// linked stylesheets) into a whitelisted, camelCased <c>style</c> prop the React renderer spreads
/// onto the element. Still lossy by design (PoC importer): scripts are skipped, the component
/// vocabulary is fixed, and depth/node caps keep real-world pages from producing megabyte schemas.
/// </summary>
public static class HtmlSchemaExtractor
{
    private const int MaxDepth = 12;
    private const int MaxNodes = 400;
    private const int MaxTextLength = 300;
    private const int MaxStyleProps = 30;

    private static readonly HashSet<string> SkipTags = new(StringComparer.Ordinal)
    {
        "script", "style", "noscript", "template", "svg", "iframe", "canvas",
        "object", "embed", "link", "meta", "br", "hr", "picture", "source", "audio", "video",
    };

    private static readonly HashSet<string> StackTags = new(StringComparer.Ordinal)
    {
        "ul", "ol", "nav", "menu",
    };

    // CSS properties worth carrying. Deliberately excludes width/height/position/top-left so imported
    // pages flow inside our shell instead of inheriting rigid computed pixel boxes; layout intent is
    // captured through flex/grid/gap/spacing/typography/borders, which travel safely.
    private static readonly HashSet<string> StyleWhitelist = new(StringComparer.Ordinal)
    {
        "display", "flex-direction", "flex-wrap", "justify-content", "align-items", "align-content",
        "gap", "row-gap", "column-gap",
        "grid-template-columns", "grid-template-rows", "grid-auto-flow",
        "max-width",
        "margin-top", "margin-right", "margin-bottom", "margin-left",
        "padding-top", "padding-right", "padding-bottom", "padding-left",
        "color", "background-color", "background-image",
        "font-family", "font-size", "font-weight", "font-style", "line-height",
        "text-align", "text-decoration", "text-transform", "letter-spacing", "white-space",
        "border-top-width", "border-right-width", "border-bottom-width", "border-left-width",
        "border-style", "border-color",
        "border-top-color", "border-right-color", "border-bottom-color", "border-left-color",
        "border-radius", "box-shadow", "opacity",
    };

    // Computed values that mean "nothing interesting was set" — dropped so every node doesn't carry
    // the browser defaults that ComputeCurrentStyle fills in.
    private static readonly HashSet<string> JunkValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "auto", "none", "normal", "static", "visible", "0px", "0", "0%", "0s",
        "transparent", "rgba(0, 0, 0, 0)", "currentcolor", "initial", "inherit", "unset",
        "rgb(0, 0, 0)", "medium", "repeat",
    };

    // Per-property defaults that aren't globally junk but are still the uninteresting initial value.
    private static readonly Dictionary<string, string> DefaultValues = new(StringComparer.Ordinal)
    {
        ["flex-direction"] = "row",
        ["flex-wrap"] = "nowrap",
        ["font-weight"] = "400",
        ["box-sizing"] = "content-box",
        ["opacity"] = "1",
        ["text-align"] = "start",
        ["border-style"] = "none",
    };

    private sealed class State(Uri baseUri)
    {
        public Uri BaseUri { get; } = baseUri;
        public int Nodes;
        private int _ids;
        public string NextId() => $"imp-{_ids++}";
    }

    public static async Task<JsonObject> ExtractAsync(string html, Uri baseUri)
    {
        // A CSS-aware browsing context resolves inline + <style> rules without network; the default
        // loader additionally fetches linked stylesheets (best effort — failures are swallowed).
        var config = Configuration.Default.WithCss().WithDefaultLoader();
        using var context = BrowsingContext.New(config);
        var doc = await context.OpenAsync(req => req.Content(html).Address(baseUri.ToString()));

        var state = new State(baseUri);

        var children = new JsonArray();
        if (doc.Body is not null)
        {
            foreach (var child in ExtractNodes(doc.Body, depth: 1, state))
                children.Add(child);
        }

        var title = Clean(doc.Title ?? "");
        return new JsonObject
        {
            ["id"] = "root",
            ["type"] = "Screen",
            ["props"] = new JsonObject
            {
                ["title"] = title.Length > 0 ? title : baseUri.Host,
                ["sourceUrl"] = baseUri.ToString(),
            },
            ["children"] = children,
        };
    }

    // Walks child *nodes* (not just elements) so significant bare text between elements survives as
    // Text leaves instead of being silently dropped.
    private static List<JsonObject> ExtractNodes(IElement parent, int depth, State s)
    {
        var result = new List<JsonObject>();
        foreach (var node in parent.ChildNodes)
        {
            if (s.Nodes >= MaxNodes) break;
            switch (node)
            {
                case IElement el:
                    if (ExtractElement(el, depth, s) is { } built) result.Add(built);
                    break;
                case IText t when Clean(t.Data) is { Length: > 0 } text:
                    if (Node(s, "Text", new JsonObject { ["text"] = text }) is { } leaf) result.Add(leaf);
                    break;
            }
        }
        return result;
    }

    private static JsonObject? ExtractElement(IElement el, int depth, State s)
    {
        var tag = el.LocalName;
        if (s.Nodes >= MaxNodes || SkipTags.Contains(tag) || el.HasAttribute("hidden")) return null;

        var style = StyleFor(el);
        JsonObject? Styled(JsonObject? node)
        {
            if (style is not null && node?["props"] is JsonObject pr) pr["style"] = style;
            return node;
        }

        if (tag is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            return Styled(TextLeaf(s, "Heading", el.TextContent));

        if (tag is "p" or "blockquote" or "pre" or "figcaption")
            return Styled(TextLeaf(s, "Text", el.TextContent));

        if (tag is "button")
            return Styled(LabelLeaf(s, el.TextContent, href: null));

        if (tag is "a")
        {
            var label = Clean(el.TextContent);
            if (label.Length > 0)
                return Styled(LabelLeaf(s, label, el.GetAttribute("href")));
            // e.g. an image wrapped in a link — fall through to the container path below.
        }

        if (tag is "img")
        {
            var src = el.GetAttribute("src");
            if (string.IsNullOrWhiteSpace(src) || !Uri.TryCreate(s.BaseUri, src, out var abs)) return null;
            var props = new JsonObject { ["src"] = abs.ToString() };
            if (Clean(el.GetAttribute("alt") ?? "") is { Length: > 0 } alt) props["alt"] = alt;
            return Styled(Node(s, "Image", props));
        }

        if (tag is "input" or "textarea" or "select")
        {
            var type = el.GetAttribute("type")?.ToLowerInvariant();
            if (type is "hidden") return null;
            if (type is "submit" or "button")
                return Styled(LabelLeaf(s, el.GetAttribute("value") ?? "Submit", href: null));

            var name = FirstNonEmpty(el.GetAttribute("name"), el.GetAttribute("id"), el.GetAttribute("placeholder"))
                       ?? s.NextId();
            var props = new JsonObject { ["name"] = name };
            if (Clean(el.GetAttribute("placeholder") ?? "") is { Length: > 0 } ph) props["placeholder"] = ph;
            return Styled(Node(s, "Input", props));
        }

        if (tag is "header" && Clean(el.TextContent) is { Length: > 0 and <= 120 } banner)
            return Styled(Node(s, "Banner", new JsonObject { ["text"] = banner }));

        // Structural / unknown element: recurse, or flatten to Text once the depth budget is spent.
        if (depth >= MaxDepth)
            return Styled(TextLeaf(s, "Text", el.TextContent));

        var children = ExtractNodes(el, depth + 1, s);
        if (children.Count == 0)
            return Styled(TextLeaf(s, "Text", el.TextContent));
        // Collapse a single-child wrapper only when it has no styling of its own to contribute;
        // a styled box is kept so its background/padding/border survive.
        if (children.Count == 1 && style is null)
            return children[0];

        var arr = new JsonArray();
        foreach (var child in children) arr.Add(child);
        return Styled(Node(s, StackTags.Contains(tag) ? "Stack" : "Container", new JsonObject(), arr));
    }

    /// <summary>Resolve an element's cascaded style into a whitelisted, camelCased prop object (or null).</summary>
    private static JsonObject? StyleFor(IElement el)
    {
        ICssStyleDeclaration decl;
        try { decl = el.ComputeCurrentStyle(); }
        catch { return null; }

        JsonObject? style = null;
        var count = 0;
        for (var i = 0; i < decl.Length && count < MaxStyleProps; i++)
        {
            var name = decl[i];
            if (!StyleWhitelist.Contains(name)) continue;

            var value = decl.GetPropertyValue(name)?.Trim() ?? "";
            if (JunkValues.Contains(value)) continue;
            if (DefaultValues.TryGetValue(name, out var def) && string.Equals(value, def, StringComparison.OrdinalIgnoreCase))
                continue;

            if (name == "background-image") value = ResolveUrls(value, el.BaseUri);

            (style ??= new JsonObject())[ToCamelCase(name)] = value;
            count++;
        }
        return style;
    }

    /// <summary>Rewrite relative url(...) targets in a CSS value to absolute, against the page base.</summary>
    private static string ResolveUrls(string value, string? baseHref)
    {
        if (!value.Contains("url(", StringComparison.OrdinalIgnoreCase)
            || baseHref is null || !Uri.TryCreate(baseHref, UriKind.Absolute, out var baseUri))
            return value;

        return System.Text.RegularExpressions.Regex.Replace(value, @"url\(\s*(['""]?)([^'""\)]+)\1\s*\)", m =>
        {
            var raw = m.Groups[2].Value.Trim();
            if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return m.Value;
            return Uri.TryCreate(baseUri, raw, out var abs) ? $"url(\"{abs}\")" : m.Value;
        });
    }

    private static string ToCamelCase(string kebab)
    {
        var idx = kebab.IndexOf('-');
        if (idx < 0) return kebab;
        var sb = new StringBuilder(kebab.Length);
        var upper = false;
        foreach (var ch in kebab)
        {
            if (ch == '-') { upper = true; continue; }
            sb.Append(upper ? char.ToUpperInvariant(ch) : ch);
            upper = false;
        }
        return sb.ToString();
    }

    private static JsonObject? TextLeaf(State s, string type, string rawText)
    {
        var text = Clean(rawText);
        return text.Length == 0 ? null : Node(s, type, new JsonObject { ["text"] = text });
    }

    private static JsonObject? LabelLeaf(State s, string rawLabel, string? href)
    {
        var label = Clean(rawLabel);
        if (label.Length == 0) return null;
        var props = new JsonObject { ["label"] = label };
        if (!string.IsNullOrWhiteSpace(href) && Uri.TryCreate(s.BaseUri, href, out var abs))
            props["href"] = abs.ToString();
        return Node(s, "Button", props);
    }

    private static JsonObject Node(State s, string type, JsonObject props, JsonArray? children = null)
    {
        s.Nodes++;
        var node = new JsonObject { ["id"] = s.NextId(), ["type"] = type, ["props"] = props };
        if (children is not null) node["children"] = children;
        return node;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string Clean(string raw)
    {
        var collapsed = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= MaxTextLength ? collapsed : collapsed[..MaxTextLength].TrimEnd() + "…";
    }
}

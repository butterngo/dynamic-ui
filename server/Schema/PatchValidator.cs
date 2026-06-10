using System.Text.Json.Nodes;

namespace DynamicUi.Server.Schema;

/// <summary>
/// Server-authoritative guard. A post-apply tree is "safe" only if: it is a non-null object,
/// every node has a known component <c>type</c>, and every node carries its type's required props.
/// </summary>
public static class PatchValidator
{
    public record Result(bool Ok, string? Error)
    {
        public static readonly Result Success = new(true, null);
        public static Result Fail(string msg) => new(false, msg);
    }

    public static Result Validate(JsonNode? root)
    {
        if (root is null)
            return Result.Fail("Schema root was removed or is null — the root node must always exist.");
        if (root is not JsonObject)
            return Result.Fail("Schema root must be a component object.");

        return ValidateNode(root.AsObject(), "$");
    }

    private static Result ValidateNode(JsonObject node, string path)
    {
        var type = node["type"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(type))
            return Result.Fail($"Node at {path} is missing a 'type'.");

        if (!ComponentRegistry.IsKnown(type))
            return Result.Fail($"Unknown component type '{type}' at {path}. Known types: {string.Join(", ", ComponentRegistry.Components.Keys)}.");

        var props = node["props"] as JsonObject;
        foreach (var required in ComponentRegistry.RequiredProps(type))
        {
            if (props is null || props[required] is null)
                return Result.Fail($"Component '{type}' at {path} is missing required prop '{required}'.");
        }

        if (node["children"] is JsonNode childrenNode)
        {
            if (childrenNode is not JsonArray children)
                return Result.Fail($"'children' at {path} must be an array.");

            for (var i = 0; i < children.Count; i++)
            {
                if (children[i] is not JsonObject childObj)
                    return Result.Fail($"Child {i} at {path} must be a component object.");

                var childResult = ValidateNode(childObj, $"{path}.children[{i}]");
                if (!childResult.Ok)
                    return childResult;
            }
        }

        return Result.Success;
    }
}

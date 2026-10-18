using System.Text.Json.Nodes;

namespace Agentic.AgentCore.Tools;

public sealed record ToolResult(bool Ok, string DataJson, string? Error = null)
{
    public static ToolResult Success(object data) =>
        new(true, System.Text.Json.JsonSerializer.Serialize(data));

    public static ToolResult SuccessJson(string dataJson) => new(true, dataJson);

    /// <summary>
    /// A structured error the model can read and recover from.
    /// "Error 500" teaches nothing; a hint teaches the model how to fix the call.
    /// </summary>
    public static ToolResult Fail(string error, string? hint = null) =>
        new(false, System.Text.Json.JsonSerializer.Serialize(new
        {
            ok = false,
            error,
            hint,
        }), error);
}

public sealed class ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>JSON Schema for the arguments object (type/properties/required).</summary>
    public required JsonObject InputSchema { get; init; }

    public required Func<JsonObject, Task<ToolResult>> Handler { get; init; }

    public OllamaTool ToOllamaTool() => new()
    {
        Function = new OllamaToolFunction
        {
            Name = Name,
            Description = Description,
            Parameters = InputSchema,
        },
    };
}

/// <summary>
/// Typed tool registry: validates arguments against the schema before the
/// handler runs and converts failures into structured errors the model can
/// act on — per the guide's tool design rules (one job, validated inputs,
/// informative errors).
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ToolDefinition> Tools => _tools.Values;

    public ToolRegistry Register(ToolDefinition tool)
    {
        if (_tools.ContainsKey(tool.Name))
            throw new InvalidOperationException($"Tool '{tool.Name}' is already registered.");
        _tools[tool.Name] = tool;
        return this;
    }

    public IReadOnlyList<OllamaTool> ToOllamaTools() =>
        _tools.Values.Select(t => t.ToOllamaTool()).ToList();

    public async Task<ToolResult> ExecuteAsync(string toolName, JsonObject arguments)
    {
        if (!_tools.TryGetValue(toolName, out var tool))
            return ToolResult.Fail(
                $"Unknown tool '{toolName}'.",
                $"Available tools: {string.Join(", ", _tools.Keys)}.");

        var validationError = ValidateArguments(tool.InputSchema, arguments);
        if (validationError is not null)
            return ToolResult.Fail(
                $"Invalid arguments for '{toolName}': {validationError}",
                $"Schema: {tool.InputSchema.ToJsonString()}");

        try
        {
            return await tool.Handler(arguments);
        }
        catch (Exception ex)
        {
            // Never let a thrown exception kill the loop — return it as data.
            return ToolResult.Fail(
                $"Tool '{toolName}' threw {ex.GetType().Name}: {ex.Message}",
                "Adjust the arguments or try a different approach; do not repeat the identical call.");
        }
    }

    /// <summary>Lightweight schema check: required properties present and of the declared type.</summary>
    internal static string? ValidateArguments(JsonObject schema, JsonObject args)
    {
        if (schema["required"] is JsonArray required)
        {
            foreach (var req in required)
            {
                var name = req?.GetValue<string>();
                if (name is null) continue;
                if (!args.ContainsKey(name) || args[name] is null)
                    return $"missing required property '{name}'";
            }
        }

        if (schema["properties"] is not JsonObject properties) return null;

        foreach (var (key, value) in args)
        {
            if (value is null) continue;
            if (!properties.TryGetPropertyValue(key, out var propSchema) || propSchema is not JsonObject ps)
                continue; // additional properties are allowed

            var type = ps["type"]?.GetValue<string>();
            var ok = type switch
            {
                "string" => value is JsonValue v && v.TryGetValue<string>(out _),
                "number" => value is JsonValue nv && (nv.TryGetValue<double>(out _) || nv.TryGetValue<int>(out _)),
                "integer" => value is JsonValue iv && (iv.TryGetValue<int>(out _) || iv.TryGetValue<double>(out var d) && d == Math.Floor(d)),
                "boolean" => value is JsonValue bv && bv.TryGetValue<bool>(out _),
                "array" => value is JsonArray,
                "object" => value is JsonObject,
                _ => true,
            };
            if (!ok)
                return $"property '{key}' should be of type {type}";

            // enum check
            if (ps["enum"] is JsonArray allowed && value is JsonValue ev && ev.TryGetValue<string>(out var s))
            {
                var allowedValues = allowed.Select(a => a?.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!allowedValues.Contains(s))
                    return $"property '{key}' must be one of: {string.Join(", ", allowedValues)}";
            }
        }

        return null;
    }
}

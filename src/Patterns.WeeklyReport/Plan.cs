using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentic.AgentCore;
using Agentic.AgentCore.Tools;

namespace Agentic.Patterns.WeeklyReport;

public sealed class PlanStep
{
    public int Step { get; set; }
    public string Description { get; set; } = "";
    public string Tool { get; set; } = "";
    public JsonObject? Args { get; set; }
    public string Section { get; set; } = "";
}

public sealed class ReportPlan
{
    public string Goal { get; set; } = "";
    public List<PlanStep> Steps { get; set; } = [];
    // Small models reliably copy the snake_case key from the prompt example,
    // which does not bind to the property by default — bind it explicitly.
    [JsonPropertyName("verification_criteria")]
    public string VerificationCriteria { get; set; } = "";
}

public sealed class ExecutionRecord
{
    public required PlanStep Step { get; init; }
    public required bool Ok { get; init; }
    public required string DataJson { get; init; }
    public string? Error { get; init; }
    public bool Repaired { get; init; }
    public long DurationMs { get; init; }
}

public sealed class Verification
{
    public bool Ok { get; set; }
    [JsonPropertyName("missing_sections")]
    [JsonConverter(typeof(StringOrObjectArrayConverter))]
    public string[] MissingSections { get; set; } = [];
    [JsonConverter(typeof(StringOrObjectArrayConverter))]
    public string[] Issues { get; set; } = [];
}

/// <summary>
/// Small models sometimes return objects where strings were asked for
/// (issues: [{"problem":"..."}]). Accept a string, or an object's first
/// string field, else the compact JSON of the element.
/// </summary>
internal sealed class StringOrObjectArrayConverter : JsonConverter<string[]>
{
    public override string[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("expected a JSON array of strings");

        var list = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var el = doc.RootElement;
            list.Add(el.ValueKind switch
            {
                JsonValueKind.String => el.GetString() ?? "",
                JsonValueKind.Object => FirstStringValue(el),
                _ => el.ToString(),
            });
        }
        return [.. list];
    }

    public override void Write(Utf8JsonWriter writer, string[] value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value);

    private static string FirstStringValue(JsonElement el)
    {
        foreach (var p in el.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } s)
                return s;
        return el.GetRawText();
    }
}

/// <summary>
/// Planning phase (pattern 2): the model decomposes the report request into
/// an ordered list of single-tool steps with arguments, sections and a
/// verification criterion. The plan is produced as validated JSON (Ollama
/// format-mode + retry-with-error) and rejected outright if it references a
/// tool that does not exist — the gate then shows it to a human before
/// anything executes.
/// </summary>
public static class Planner
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<ReportPlan> CreateAsync(
        IOllamaClient client,
        ToolRegistry tools,
        string reportRequest,
        string businessDate,
        CancellationToken ct = default)
    {
        var toolList = string.Join("\n", tools.Tools.OrderBy(t => t.Name).Select(t =>
            $"- {t.Name}: {t.Description} Schema: {t.InputSchema.ToJsonString()}"));

        var plan = await StructuredOutput.GetAsync<ReportPlan>(
            client,
            systemPrompt: """
                You are the planning engine for a weekly report generator at a small screen-printing shop.
                Decompose the report request into an ordered list of data-gathering steps.

                Rules:
                - Every step names exactly ONE tool from the available list, with valid arguments for it.
                - Each step names the report section its output belongs to.
                - Match each section to the tool whose description fits its purpose most directly —
                  a section about at-risk jobs needs the tool that finds at-risk jobs.
                - Optional filters (status, max_rating, weeks, size...) are OMITTED unless the report
                  request explicitly asks for a subset; the report needs the full picture.
                - Steps must be independent lookups; 3 to 6 steps is the usual range.
                - Do not invent tools. Do not put prose in "tool".
                - "goal" states the report's purpose in one sentence. "verification_criteria" states
                  what the finished report must contain.

                Return ONLY a JSON object, following this shape with your own values:
                {"goal":"...","steps":[{"step":1,"description":"...","tool":"...","args":{},"section":"..."},{"step":2,"description":"...","tool":"...","section":"..."}],"verification_criteria":"..."}
                """,
            userPrompt: $"""
                Business date: {businessDate} (frozen dataset snapshot)

                Report request: {reportRequest}

                Available tools:
                {toolList}
                """,
            validate: p => Validate(p, tools),
            ct: ct);

        // Small models sometimes drop verification_criteria; synthesise the
        // obvious criterion rather than failing the whole plan over it.
        if (string.IsNullOrWhiteSpace(plan.VerificationCriteria))
            plan.VerificationCriteria =
                "Every planned section appears in the report and cites concrete numbers from the gathered data.";

        return plan;
    }

    private static string? Validate(ReportPlan plan, ToolRegistry tools)
    {
        if (plan.Steps.Count == 0) return "plan has no steps; decompose the report into 1-6 tool steps";
        if (plan.Steps.Count > 8) return $"plan has {plan.Steps.Count} steps; keep it to 8 or fewer";
        var goal = plan.Goal.Trim();
        if (goal.Length == 0 || goal.Trim('.', '…', ' ', '"', '\'').Length == 0)
            return "'goal' must state the report's purpose in one real sentence (not a placeholder)";

        var known = tools.Tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var s = plan.Steps[i];
            if (string.IsNullOrWhiteSpace(s.Tool))
                return $"step {i + 1} has no 'tool'";
            if (!known.Contains(s.Tool))
                return $"step {i + 1} names unknown tool '{s.Tool}'. Available: {string.Join(", ", tools.Tools.Select(t => t.Name).OrderBy(n => n))}";
            // The invariant is that every step feeds some report section; small
            // models sometimes drop the field, so derive one from the tool
            // rather than failing the whole plan over it.
            if (string.IsNullOrWhiteSpace(s.Section))
                s.Section = TitleCase(s.Tool.Replace('_', ' '));
            if (s.Args is not null && s.Args.GetValueKind() != JsonValueKind.Object)
                return $"step {i + 1} 'args' must be a JSON object or omitted";
        }

        return null;
    }

    private static string TitleCase(string s) =>
        string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    public static string ToDisplay(ReportPlan plan)
    {
        var lines = new List<string> { $"Goal: {plan.Goal}", "" };
        foreach (var s in plan.Steps)
        {
            var args = s.Args is null || s.Args.Count == 0 ? "" : " " + s.Args.ToJsonString();
            lines.Add($"  {s.Step}. [{s.Section}] {s.Description}");
            lines.Add($"     → {s.Tool}{args}");
        }
        lines.Add("");
        lines.Add($"Verify: {plan.VerificationCriteria}");
        return string.Join(Environment.NewLine, lines);
    }

    public static ReportPlan? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ReportPlan>(json, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

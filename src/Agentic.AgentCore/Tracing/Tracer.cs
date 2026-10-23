using System.Diagnostics;
using System.Text.Json;

namespace Agentic.AgentCore.Tracing;

public enum SpanKind { Agent, Llm, Tool, Gate, Internal }

public sealed class Span : IDisposable
{
    private readonly Tracer _tracer;
    private readonly Span? _parent;
    private readonly Stopwatch _sw;
    private bool _ended;

    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    internal Span? Parent => _parent;
    public string? ParentId { get; }
    public string Name { get; }
    public SpanKind Kind { get; }
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public long DurationMs { get; private set; }
    public Dictionary<string, object?> Tags { get; } = new();
    public Dictionary<string, object?> Events { get; } = new();

    internal Span(Tracer tracer, string name, SpanKind kind, Span? parent)
    {
        _tracer = tracer;
        Name = name;
        Kind = kind;
        _parent = parent;
        ParentId = parent?.Id;
        _sw = Stopwatch.StartNew();
    }

    public void SetTag(string key, object? value) => Tags[key] = value;

    public void AddEvent(string name, object? data = null) => Events[name] = data;

    public void Dispose()
    {
        if (_ended) return;
        _ended = true;
        _sw.Stop();
        DurationMs = _sw.ElapsedMilliseconds;
        _tracer.Complete(this);
    }
}

/// <summary>
/// Lightweight OpenTelemetry-style tracer: nested spans with tags, emitted as
/// JSONL (one line per span) and rendered as a tree at the end of the run.
/// Swap in a real OTel exporter by adding an ISpanSink implementation.
/// </summary>
public sealed class Tracer
{
    private readonly List<Span> _spans = new();
    private readonly string? _jsonlPath;
    private readonly TextWriter? _console;
    private readonly AsyncLocal<Span?> _current = new();
    private readonly object _lock = new();

    public string RunId { get; } = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];

    public Tracer(string? jsonlPath = null, TextWriter? console = null)
    {
        _jsonlPath = jsonlPath;
        _console = console;
    }

    public IReadOnlyList<Span> Spans
    {
        get { lock (_lock) return _spans.ToList(); }
    }

    public Span StartSpan(string name, SpanKind kind = SpanKind.Internal)
    {
        var span = new Span(this, name, kind, _current.Value);
        _current.Value = span;
        return span;
    }

    /// <summary>Runs an action inside a span; the span closes automatically.</summary>
    public async Task<T> InSpanAsync<T>(string name, Func<Span, Task<T>> action, SpanKind kind = SpanKind.Internal)
    {
        using var span = StartSpan(name, kind);
        try
        {
            return await action(span);
        }
        catch (Exception ex)
        {
            span.SetTag("error", ex.Message);
            throw;
        }
    }

    internal void Complete(Span span)
    {
        // Fan-out patterns close spans from sibling tasks at once; the JSONL
        // sink is a single file, so append under the same lock that guards the
        // span list or concurrent runs collide on the handle.
        lock (_lock)
        {
            _spans.Add(span);

            if (_jsonlPath is not null)
            {
                var line = JsonSerializer.Serialize(new
                {
                    spanId = span.Id,
                    parentId = span.ParentId,
                    name = span.Name,
                    kind = span.Kind.ToString(),
                    startedUtc = span.StartedUtc,
                    durationMs = span.DurationMs,
                    span.Tags,
                    span.Events,
                });
                File.AppendAllText(_jsonlPath, line + Environment.NewLine);
            }
        }

        // pop ambient span if it is this one, restoring its parent
        if (_current.Value?.Id == span.Id)
            _current.Value = span.Parent;
    }

    /// <summary>Prints a tree of the run to the console. Call after the run finishes.</summary>
    public void WriteTree(TextWriter? writer = null)
    {
        writer ??= _console ?? Console.Out;
        var spans = Spans;
        var children = spans.Where(s => s.ParentId is not null)
            .GroupBy(s => s.ParentId!)
            .ToDictionary(g => g.Key, g => g.ToList());
        var roots = spans.Where(s => s.ParentId is null).ToList();

        writer.WriteLine($"trace run {RunId}");
        foreach (var root in roots.OrderBy(s => s.StartedUtc))
            WriteNode(root, children, writer, "");

        var tokens = spans.Where(s => s.Kind == SpanKind.Llm)
            .Sum(s => s.Tags.TryGetValue("promptTokens", out var p) ? Convert.ToInt32(p) : 0);
        var completion = spans.Where(s => s.Kind == SpanKind.Llm)
            .Sum(s => s.Tags.TryGetValue("completionTokens", out var c) ? Convert.ToInt32(c) : 0);
        writer.WriteLine($"total: {spans.Count} spans, llm tokens {tokens} prompt + {completion} completion");
    }

    private static void WriteNode(Span span, Dictionary<string, List<Span>> children, TextWriter writer, string indent)
    {
        var tagStr = span.Tags.Count > 0
            ? "  " + string.Join(" ", span.Tags.Select(kv => kv.Value is null ? kv.Key : $"{kv.Key}={kv.Value}"))
            : "";
        writer.WriteLine($"{indent}{KindGlyph(span.Kind)} {span.Name} ({span.DurationMs}ms){tagStr}");
        if (children.TryGetValue(span.Id, out var kids))
            foreach (var kid in kids.OrderBy(k => k.StartedUtc))
                WriteNode(kid, children, writer, indent + "  ");
    }

    private static string KindGlyph(SpanKind kind) => kind switch
    {
        SpanKind.Agent => "[agent]",
        SpanKind.Llm => "[llm]  ",
        SpanKind.Tool => "[tool] ",
        SpanKind.Gate => "[gate] ",
        _ => "[span] ",
    };
}

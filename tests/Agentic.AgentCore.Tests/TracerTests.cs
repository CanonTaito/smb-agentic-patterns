using Agentic.AgentCore.Tracing;
using Xunit;

namespace Agentic.AgentCore.Tests;

public class TracerTests
{
    [Fact]
    public void Spans_NestUnderAmbientParent()
    {
        var tracer = new Tracer();
        using (var parent = tracer.StartSpan("agent", SpanKind.Agent))
        using (tracer.StartSpan("llm", SpanKind.Llm))
        using (tracer.StartSpan("tool", SpanKind.Tool))
        {
            // all three created inside each other's scope
        }

        var spans = tracer.Spans;
        Assert.Equal(3, spans.Count);
        var llm = Assert.Single(spans, s => s.Name == "llm");
        var tool = Assert.Single(spans, s => s.Name == "tool");
        var agent = Assert.Single(spans, s => s.Name == "agent");

        Assert.Equal(agent.Id, llm.ParentId);
        Assert.Equal(llm.Id, tool.ParentId);
        Assert.Null(agent.ParentId);
    }

    [Fact]
    public void SiblingSpans_ShareTheSameParent()
    {
        var tracer = new Tracer();
        using (tracer.StartSpan("agent", SpanKind.Agent))
        {
            using (tracer.StartSpan("tool A", SpanKind.Tool)) { }
            using (tracer.StartSpan("tool B", SpanKind.Tool)) { }
        }

        var spans = tracer.Spans;
        var agentId = Assert.Single(spans, s => s.Name == "agent").Id;
        Assert.Equal(agentId, Assert.Single(spans, s => s.Name == "tool A").ParentId);
        Assert.Equal(agentId, Assert.Single(spans, s => s.Name == "tool B").ParentId);
    }

    [Fact]
    public void JsonlSink_WritesOneLinePerSpan()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trace-{Guid.NewGuid():N}.jsonl");
        try
        {
            var tracer = new Tracer(jsonlPath: path);
            using (tracer.StartSpan("agent", SpanKind.Agent))
            using (tracer.StartSpan("llm", SpanKind.Llm)) { }

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.All(lines, l => Assert.Contains("\"spanId\"", l));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Tags_AreRecordedOnSpan()
    {
        var tracer = new Tracer();
        using (var span = tracer.StartSpan("llm", SpanKind.Llm))
        {
            span.SetTag("promptTokens", 123);
            span.AddEvent("retry");
        }

        var llm = Assert.Single(tracer.Spans);
        Assert.Equal(123, llm.Tags["promptTokens"]);
        Assert.True(llm.Events.ContainsKey("retry"));
    }
}

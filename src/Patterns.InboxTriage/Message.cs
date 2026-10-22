using System.Text.Json.Serialization;

namespace Agentic.Patterns.InboxTriage;

/// <summary>
/// One customer email from data/inbox.jsonl (30 labelled messages — the eval
/// set and the demo input). Labels are eval-only and never shown to the agent.
/// </summary>
public sealed class InboxMessage
{
    public string Id { get; set; } = "";
    public string From { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";

    [JsonPropertyName("expected_category")]
    public string ExpectedCategory { get; set; } = "";

    public bool Ambiguous { get; set; }

    public string Text() => $"From: {From}\nSubject: {Subject}\n\n{Body}";

    public string ShortSubject => Subject.Length <= 50 ? Subject : Subject[..47] + "...";
}

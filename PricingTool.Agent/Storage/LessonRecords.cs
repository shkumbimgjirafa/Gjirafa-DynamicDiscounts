namespace PricingTool.Agent.Storage;

/// <summary>
/// Something an agent keeps in mind on every pass: a person's correction of an earlier answer, or a lesson code drew from
/// a result. The agent's prompt carries the active lessons; a person retires one that no longer holds.
/// </summary>
public class AgentLesson
{
    public long Id { get; set; }
    /// <summary>Nova or Alpha.</summary>
    public string Agent { get; set; } = "";
    /// <summary>Null: every layer.</summary>
    public int? LayerId { get; set; }
    public string Text { get; set; } = "";
    /// <summary>See <see cref="LessonSource"/>.</summary>
    public string Source { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime? RetiredUtc { get; set; }
    public string? RetiredBy { get; set; }
}

public static class LessonSource
{
    /// <summary>A person wrote it (in chat, the inbox or the command line).</summary>
    public const string Person = "person";
    /// <summary>Code wrote it when a person overrode the agent's answer (e.g. set a campaign goal Nova proposed differently).</summary>
    public const string Correction = "correction";
}

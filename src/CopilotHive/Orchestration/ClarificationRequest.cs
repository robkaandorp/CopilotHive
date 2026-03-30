namespace CopilotHive.Orchestration;

/// <summary>The resolution status of a clarification request.</summary>
public enum ClarificationStatus
{
    /// <summary>Waiting for the Composer LLM to auto-answer.</summary>
    AwaitingComposer,

    /// <summary>Composer could not answer; waiting for a human response.</summary>
    AwaitingHuman,

    /// <summary>The question has been answered (by Composer or human).</summary>
    Answered,

    /// <summary>No answer was received within the allowed timeout window.</summary>
    TimedOut,
}

/// <summary>
/// Represents a clarification question raised by a worker (via the Brain) that
/// could not be answered autonomously and may require human intervention.
/// Flows through the three-tier resolution chain: Brain → Composer → Human.
/// </summary>
public sealed class ClarificationRequest
{
    /// <summary>Unique identifier for this request.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>The goal that triggered the clarification.</summary>
    public string GoalId { get; set; } = "";

    /// <summary>The worker role that raised the question (e.g. "coder", "tester").</summary>
    public string WorkerRole { get; set; } = "";

    /// <summary>The question text that needs answering.</summary>
    public string Question { get; set; } = "";

    /// <summary>When the request was created.</summary>
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The answer provided, or <c>null</c> if not yet answered.</summary>
    public string? Answer { get; set; }

    /// <summary>Current resolution status.</summary>
    public ClarificationStatus Status { get; set; } = ClarificationStatus.AwaitingComposer;

    /// <summary>Who resolved the question: <c>"composer"</c> or <c>"human"</c>, or <c>null</c> if unresolved.</summary>
    public string? ResolvedBy { get; set; }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using CopilotHive.Workers;

namespace CopilotHive.Services;

/// <summary>
/// Phases a goal progresses through in the pipeline.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GoalPhase>))]
public enum GoalPhase
{
    /// <summary>Initial phase: the brain is planning the iteration.</summary>
    Planning,
    /// <summary>The coder worker is implementing the goal.</summary>
    Coding,
    /// <summary>The reviewer worker is reviewing the coder's changes.</summary>
    Review,
    /// <summary>The tester worker is running and writing tests.</summary>
    Testing,
    /// <summary>The doc-writer worker is updating documentation.</summary>
    DocWriting,
    /// <summary>The improver worker is improving AGENTS.md files.</summary>
    Improve,
    /// <summary>The feature branch is being merged to main.</summary>
    Merging,
    /// <summary>The goal has been completed successfully.</summary>
    Done,
    /// <summary>The goal has failed and will not be retried.</summary>
    Failed,
}

/// <summary>
/// Outcome of a single pipeline phase within one iteration.
/// Uses <see cref="JsonNamingPolicy.CamelCase"/> so values serialize as "pass", "fail", "skip"
/// matching existing stored data.
/// </summary>
[JsonConverter(typeof(CamelCasePhaseOutcomeConverter))]
public enum PhaseOutcome
{
    /// <summary>The phase completed successfully.</summary>
    Pass,
    /// <summary>The phase failed.</summary>
    Fail,
    /// <summary>The phase was skipped.</summary>
    Skip,
}

/// <summary>
/// JSON converter for <see cref="PhaseOutcome"/> that serializes values as camelCase strings
/// ("pass", "fail", "skip") for backward compatibility with existing stored data.
/// </summary>
internal sealed class CamelCasePhaseOutcomeConverter : JsonStringEnumConverter<PhaseOutcome>
{
    public CamelCasePhaseOutcomeConverter() : base(JsonNamingPolicy.CamelCase) { }
}

/// <summary>
/// Brain-determined workflow plan for a single iteration.
/// </summary>
public sealed class IterationPlan
{
    /// <summary>Ordered list of phases the Brain wants to execute this iteration.</summary>
    public List<GoalPhase> Phases { get; init; } = [];

    /// <summary>
    /// Per-phase instructions/context from the Brain.
    /// Keys are lowercase phase names: "coding", "review", "testing", "merging", etc.
    /// For multi-round plans, indexed keys like "coding-2" are used for the 2nd occurrence.
    /// </summary>
    public Dictionary<string, string> PhaseInstructions { get; init; } = [];

    /// <summary>Per-role model tier overrides from the Brain, keyed by phase (e.g. Coding → Premium).</summary>
    public Dictionary<GoalPhase, ModelTier> PhaseTiers { get; init; } = [];

    /// <summary>Brain's reasoning for this plan.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Returns the instruction string for the given phase and occurrence index (1-based).
    /// Tries the indexed key first (e.g. "coding-2" for occurrence 2), then falls back
    /// to the bare key (e.g. "coding") for backward compatibility.
    /// Returns null if neither key exists.
    /// </summary>
    /// <param name="phase">The phase to look up.</param>
    /// <param name="occurrenceIndex">The 1-based occurrence index within the plan's phase sequence.</param>
    /// <returns>The instruction string, or null if not found.</returns>
    public string? GetPhaseInstruction(GoalPhase phase, int occurrenceIndex)
    {
        var phaseName = phase.ToString().ToLowerInvariant();
        // Try indexed key first (e.g. "coding-2" for occurrence 2, "coding-1" for occurrence 1)
        var indexedKey = $"{phaseName}-{occurrenceIndex}";
        if (PhaseInstructions.TryGetValue(indexedKey, out var indexed))
            return indexed;
        // Fall back to bare key
        return PhaseInstructions.GetValueOrDefault(phaseName);
    }

    /// <summary>
    /// Creates a default plan with the standard phase order.
    /// Used as fallback when the Brain doesn't provide a plan.
    /// </summary>
    public static IterationPlan Default(bool includeImprove = false)
    {
        var phases = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.DocWriting, GoalPhase.Review };
        if (includeImprove) phases.Add(GoalPhase.Improve);
        phases.Add(GoalPhase.Merging);
        return new IterationPlan { Phases = phases, Reason = "Default plan" };
    }
}

/// <summary>
/// Records a single clarification Q&amp;A that occurred during goal execution.
/// </summary>
/// <param name="Timestamp">UTC time the clarification was created.</param>
/// <param name="GoalId">The goal this clarification belongs to.</param>
/// <param name="Iteration">Iteration number when the clarification was asked.</param>
/// <param name="Phase">Pipeline phase when the clarification was asked.</param>
/// <param name="WorkerRole">Worker role that triggered the question.</param>
/// <param name="Question">The question that was asked.</param>
/// <param name="Answer">The answer that was provided.</param>
/// <param name="AnsweredBy">Who answered: \"brain\", \"composer\", or \"human\".</param>
public sealed record ClarificationEntry(
    DateTime Timestamp,
    string GoalId,
    int Iteration,
    string Phase,
    string WorkerRole,
    string Question,
    string Answer,
    string AnsweredBy)
{
    /// <summary>
    /// 1-based occurrence index of the phase within the iteration plan.
    /// Defaults to 0 for entries created before per-occurrence tracking (backward compat).
    /// </summary>
    public int Occurrence { get; init; }
}

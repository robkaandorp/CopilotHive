using SharpCoder;

namespace CopilotHive.Orchestration;

/// <summary>
/// The Composer's LLM usage, split into four independent views of the same model calls.
/// <para>
/// Every member is a DETACHED snapshot taken where the value was produced: later model calls never
/// change a summary that was already handed out. The members deliberately overlap — <see cref="ManualCompactions"/>
/// is also counted inside <see cref="Session"/>, while <see cref="Clarifications"/> is not part of
/// <see cref="Session"/> at all — so a consumer must pick the member matching its question instead of
/// summing them.
/// </para>
/// </summary>
public record ComposerUsageSnapshot
{
    /// <summary>
    /// Usage of the most recent chat turn — one user message or notification stream, covering every
    /// model call of that execution (all tool rounds, compaction performed during the turn and
    /// forwarded sub-agent calls), exactly as SharpCoder reports it on the execution's
    /// <c>AgentResult.TokenUsage</c>.
    /// <para>
    /// <c>null</c> while no turn has completed since the current stream started, and <c>null</c>
    /// from orchestrator start until the first turn completes. A stream that ends by cancellation
    /// or by an exception without a Completed result reports nothing, so the previous value is never
    /// left visible for it: it is cleared when the new stream starts.
    /// </para>
    /// </summary>
    public UsageSummary? LastTurn { get; init; }

    /// <summary>
    /// Cumulative usage of the live Composer session (<c>AgentSession.Usage</c>), as a detached
    /// snapshot. It accumulates since the last session reset and is persisted with
    /// <c>composer-session.json</c>, so it survives an orchestrator restart. It includes chat turns,
    /// automatic and manual compaction, and sub-agent calls.
    /// </summary>
    public UsageSummary Session { get; init; } = new();

    /// <summary>
    /// Summed usage of every clarification answer the Composer produced since orchestrator start.
    /// <para>
    /// In memory only — never persisted and NOT cleared by a session reset. It is also NOT part of
    /// <see cref="Session"/>: clarifications run on forked sessions whose usage is reset, so their
    /// calls reach no session total. A clarification whose reply failed or escalated is counted too,
    /// because its usage is recorded before the outcome is judged.
    /// </para>
    /// </summary>
    public UsageSummary Clarifications { get; init; } = new();

    /// <summary>
    /// Summed usage of every MANUAL compaction (full or partial) run since orchestrator start.
    /// In memory only — never persisted and NOT cleared by a session reset.
    /// <para>
    /// A subset of <see cref="Session"/>: a manual compaction records each summary call into the
    /// session's cumulative usage AND into this accumulation, so the same calls appear in both.
    /// Automatic compaction performed inside a chat turn is NOT counted here.
    /// </para>
    /// </summary>
    public UsageSummary ManualCompactions { get; init; } = new();
}

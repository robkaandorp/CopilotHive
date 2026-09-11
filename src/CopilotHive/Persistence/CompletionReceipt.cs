using CopilotHive.Services;
using CopilotHive.Workers;

namespace CopilotHive.Persistence;

/// <summary>
/// An immutable, detached carrier for one worker's completion of a dispatched task: the goal it
/// belongs to, the worker that produced it, the role that worker was assigned, the EXISTING
/// <see cref="WorkSlot"/> identity of the dispatch, and the complete domain
/// <see cref="TaskResult"/> the worker returned.
/// <para>
/// It holds NO live pipeline or worker reference and does NOT copy the whole
/// <see cref="WorkTask"/>: the two domain values it carries (<see cref="WorkSlot"/>,
/// <see cref="TaskResult"/>) are the existing immutable ones, handed over as-is.
/// </para>
/// <para>
/// THE CONSTRUCTOR VALIDATES INTERNAL CONSISTENCY ONLY — that the identity strings are present,
/// that the slot and the result agree on the task ID, that iteration, occurrence and attempt are
/// positive, that the phase is one of the worker-backed phases, and that the assigned role matches
/// that phase's existing <see cref="GoalPhaseExtensions.ToWorkerRole"/> mapping. It is NOT durable
/// worker authorization: a constructed receipt proves nothing about whether the worker really owned
/// the attempt, whether the slot was still live, or whether the completion was already stored.
/// Those questions belong to the caller and to durable storage — never to this carrier.
/// </para>
/// </summary>
internal sealed class CompletionReceipt
{
    /// <summary>The goal the completed task belonged to. Never <c>null</c> or blank.</summary>
    public string GoalId { get; }

    /// <summary>The worker that produced this completion. Never <c>null</c> or blank.</summary>
    public string WorkerId { get; }

    /// <summary>
    /// The role the worker was assigned for this completion. Always a worker-backed role — one of
    /// <see cref="WorkerRole.Coder"/>, <see cref="WorkerRole.Tester"/>, <see cref="WorkerRole.Reviewer"/>,
    /// <see cref="WorkerRole.DocWriter"/> or <see cref="WorkerRole.Improver"/> — and always the one
    /// its phase maps to.
    /// </summary>
    public WorkerRole Role { get; }

    /// <summary>The existing work-slot identity of the completed dispatch. Never <c>null</c>.</summary>
    public WorkSlot Slot { get; }

    /// <summary>The complete domain result the worker produced. Never <c>null</c>.</summary>
    public TaskResult Result { get; }

    /// <summary>
    /// Creates a receipt after validating the supplied values' INTERNAL CONSISTENCY (see the type
    /// remarks — this is not durable worker authorization). Every refusal happens BEFORE any state
    /// exists, so a rejected construction yields no partially built receipt.
    /// </summary>
    /// <param name="goalId">The goal ID; must be non-blank.</param>
    /// <param name="workerId">The worker ID; must be non-blank.</param>
    /// <param name="role">
    /// The role the worker was assigned; must be a defined, worker-backed role and must equal
    /// <see cref="GoalPhaseExtensions.ToWorkerRole"/> of the slot's phase.
    /// </param>
    /// <param name="slot">
    /// The existing work-slot identity; must be non-<c>null</c>, carry a non-blank task ID, a
    /// position with a positive iteration and occurrence and a worker-backed phase, and a positive
    /// attempt. There is deliberately no slot-less receipt form.
    /// </param>
    /// <param name="result">
    /// The complete domain result; must be non-<c>null</c> and carry the SAME non-blank task ID as
    /// the slot.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="slot"/> or <paramref name="result"/>
    /// is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">
    /// The goal ID, worker ID or either task ID is null or blank; the slot's task ID differs from the
    /// result's; the slot has no position, a non-positive iteration, occurrence or attempt; the
    /// phase is not one of the worker-backed phases (including <see cref="GoalPhase.Planning"/>,
    /// <see cref="GoalPhase.Merging"/>, <see cref="GoalPhase.Done"/>, <see cref="GoalPhase.Failed"/>
    /// and undefined values); the role is undefined or is not the phase's mapped role (including
    /// <see cref="WorkerRole.Unspecified"/>, <see cref="WorkerRole.Orchestrator"/> and
    /// <see cref="WorkerRole.MergeWorker"/>).
    /// </exception>
    internal CompletionReceipt(string goalId, string workerId, WorkerRole role, WorkSlot slot, TaskResult result)
    {
        // ── THE NULL REFUSALS, first: nothing can be inspected until the carriers are present. ──
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(result);

        // ── THE SLOT'S OWN SHAPE. A slot without a position has no identity to validate. ──
        var position = slot.Position;
        if (position is null)
            throw new ArgumentException("Completion receipt slot must carry a position.", nameof(slot));

        // ── THE IDENTITY STRINGS. All three must be present; the two task IDs must also AGREE. ──
        if (string.IsNullOrWhiteSpace(goalId))
            throw new ArgumentException("Completion receipt goal ID must be a non-blank string.", nameof(goalId));
        if (string.IsNullOrWhiteSpace(workerId))
            throw new ArgumentException("Completion receipt worker ID must be a non-blank string.", nameof(workerId));
        if (string.IsNullOrWhiteSpace(slot.TaskId))
            throw new ArgumentException("Completion receipt slot task ID must be a non-blank string.", nameof(slot));
        if (string.IsNullOrWhiteSpace(result.TaskId))
            throw new ArgumentException("Completion receipt result task ID must be a non-blank string.", nameof(result));
        if (!string.Equals(slot.TaskId, result.TaskId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Completion receipt slot task ID '{slot.TaskId}' does not match result task ID '{result.TaskId}'.",
                nameof(result));
        }

        // ── THE POSITION NUMBERS: one-based iteration, occurrence and attempt. No inference, no
        //    parsing of the task ID, and no legacy "assume 1" fallback. ──
        if (position.Iteration <= 0)
        {
            throw new ArgumentException(
                $"Completion receipt iteration must be positive but was {position.Iteration}.", nameof(slot));
        }
        if (position.Occurrence <= 0)
        {
            throw new ArgumentException(
                $"Completion receipt occurrence must be positive but was {position.Occurrence}.", nameof(slot));
        }
        if (slot.Attempt <= 0)
        {
            throw new ArgumentException(
                $"Completion receipt attempt must be positive but was {slot.Attempt}.", nameof(slot));
        }

        // ── THE WORKER-BACKED PHASE. Planning/Merging/Done/Failed have no worker, and an undefined
        //    value has no mapping at all — both are refused rather than guessed at. ──
        switch (position.Phase)
        {
            case GoalPhase.Coding:
            case GoalPhase.Testing:
            case GoalPhase.Review:
            case GoalPhase.DocWriting:
            case GoalPhase.Improve:
                break;
            case GoalPhase.Planning:
            case GoalPhase.Merging:
            case GoalPhase.Done:
            case GoalPhase.Failed:
                throw new ArgumentException(
                    $"Completion receipt phase '{position.Phase}' has no worker and cannot complete a task.",
                    nameof(slot));
            default:
                throw new ArgumentException(
                    $"Completion receipt phase has undefined GoalPhase value {(int)position.Phase}.", nameof(slot));
        }

        // ── THE ROLE. It must be a DEFINED role AND the one the phase maps to; an undefined value
        //    is named as such, and every other role (Unspecified, Orchestrator, MergeWorker) simply
        //    fails the mapping comparison. ──
        if (!Enum.IsDefined(role))
            throw new ArgumentException($"Completion receipt role has undefined WorkerRole value {(int)role}.", nameof(role));

        var derivedRole = position.Phase.ToWorkerRole();
        if (role != derivedRole)
        {
            throw new ArgumentException(
                $"Completion receipt role '{role}' does not match the role '{derivedRole}' mapped from " +
                $"phase '{position.Phase}'.", nameof(role));
        }

        // ── ONLY NOW, with every rule satisfied, does the receipt come into being. ──
        GoalId = goalId;
        WorkerId = workerId;
        Role = role;
        Slot = slot;
        Result = result;
    }
}

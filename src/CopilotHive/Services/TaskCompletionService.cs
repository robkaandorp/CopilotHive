using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Services;

/// <summary>
/// Handles task completion callbacks by driving the pipeline to its next phase
/// and managing terminal lifecycle transitions.
/// Extracted from <see cref="GoalDispatcher"/> — all logic is identical.
/// </summary>
internal sealed class TaskCompletionService
{
    private readonly GoalPipelineManager _pipelineManager;
    private readonly IDistributedBrain? _brain;
    private readonly PipelineDriver _pipelineDriver;
    private readonly GoalLifecycleService _lifecycleService;
    private readonly DashboardNotifier? _dashboardNotifier;
    private readonly ILogger _logger;

    public TaskCompletionService(
        GoalPipelineManager pipelineManager,
        IDistributedBrain? brain,
        PipelineDriver pipelineDriver,
        GoalLifecycleService lifecycleService,
        DashboardNotifier? dashboardNotifier,
        ILogger logger)
    {
        _pipelineManager = pipelineManager;
        _brain = brain;
        _pipelineDriver = pipelineDriver;
        _lifecycleService = lifecycleService;
        _dashboardNotifier = dashboardNotifier;
        _logger = logger;
    }

    /// <summary>
    /// Called when a worker completes a task. Drives the pipeline to its next phase
    /// using the Brain, or marks the goal completed if no Brain is configured.
    /// </summary>
    public async Task HandleTaskCompletionAsync(TaskResult result, CancellationToken ct = default)
    {
        var pipeline = _pipelineManager.GetByTaskId(result.TaskId);
        if (pipeline is null)
        {
            _logger.LogWarning("No pipeline found for completed task {TaskId}", result.TaskId);
            return;
        }

        // Guard: ignore late-arriving completions for goals already finished
        if (pipeline.Phase is GoalPhase.Done or GoalPhase.Failed)
        {
            _logger.LogInformation(
                "Task {TaskId} completed but goal {GoalId} already {Phase} — ignoring duplicate",
                result.TaskId, pipeline.GoalId, pipeline.Phase);
            return;
        }

        // Guard: during the re-plan (Planning) window the state machine has an empty phase queue —
        // any completion arriving here (late duplicate, or a task cancelled by the previous
        // iteration) must drop cleanly instead of flowing into Transition and killing the goal.
        if (pipeline.StateMachine.Phase == GoalPhase.Planning)
        {
            _logger.LogWarning(
                "StaleCompletion goal={GoalId} task={TaskId} pipeline-phase={Phase} machine-phase={Phase} reason=planning-window",
                pipeline.GoalId, result.TaskId, pipeline.Phase, pipeline.StateMachine.Phase);
            return;
        }

        // Guard: ignore completions from tasks that are no longer the active task
        // (e.g., a stale task from a previous phase completing after the pipeline advanced)
        if (pipeline.ActiveTaskId is not null && pipeline.ActiveTaskId != result.TaskId)
        {
            _logger.LogWarning(
                "Task {TaskId} completed but pipeline {GoalId} active task is {ActiveTaskId} — ignoring stale completion",
                result.TaskId, pipeline.GoalId, pipeline.ActiveTaskId);
            return;
        }

        // THE ADMISSION: one atomic, lock-scoped decision that both classifies the completion and
        // — for a Pending slot — CLAIMS it. It replaces the earlier separate locked read, so a
        // retire can no longer interleave between the check and the claim. The guarantee is the
        // decision's atomicity: a retire landing AFTER the claim still proceeds, and the drive
        // below is not isolated from it.
        var admission = pipeline.AdmitCompletion(result.TaskId);
        switch (admission)
        {
            case AdmissionOutcome.SlotAbandoned:
                _logger.LogWarning(
                    "WorkSlotIntegrity: stale-completion goal={GoalId} task={TaskId} pipeline-phase={PipelinePhase} slot-state=abandoned — the completion is for a retired attempt; dropped",
                    pipeline.GoalId, result.TaskId, pipeline.Phase);
                return;
            case AdmissionOutcome.SlotAlreadyAdmitted:
                _logger.LogWarning(
                    "WorkSlotIntegrity: duplicate-completion goal={GoalId} task={TaskId} pipeline-phase={PipelinePhase} — the attempt was already admitted; dropped",
                    pipeline.GoalId, result.TaskId, pipeline.Phase);
                return;
            case AdmissionOutcome.Admitted:
            case AdmissionOutcome.NoSlot:
                // Admitted: this completion owns the attempt. NoSlot: the pre-registry
                // pass-through. Both proceed into the drive.
                break;
            default:
                throw new InvalidOperationException($"Unhandled AdmissionOutcome: {admission}");
        }

        // THE POINTER RELEASE — the completion path's half of the atomic-claim protocol.
        //
        // WHY IT EXISTS: dispatch used to call GoalPipeline.SetActiveTask, which OVERWROTE the
        // active-task pointer unconditionally, so sequential phase handoff worked implicitly —
        // the next phase's dispatch simply stamped over the finished phase's task id. The
        // admission-atomic-switch replaced that with the ownership-refusing
        // GoalPipeline.TrySetActiveTask, which claims the pointer ONLY when it is null (so two
        // overlapping dispatches can never both proceed). That refusal is the point of the
        // slice — but it means the pointer is no longer released implicitly. Without an explicit
        // release here, EVERY multi-phase goal would fail at its first phase boundary: the drive
        // below dispatches the next phase, whose claim would find this task's pointer still live
        // and refuse.
        //
        // WHY HERE: this is the first point at which the attempt's claim lifecycle is provably
        // finished — the admission above has already classified the completion and CLAIMED a
        // Pending slot, so every stale/duplicate/abandoned completion has returned. It is also
        // strictly BEFORE the drive, which is mandatory: DriveNextPhaseAsync dispatches the next
        // phase inline (PipelineDriver.DispatchPhaseAsync → DispatchToRole), so a release placed
        // after the drive would run too late to unblock that dispatch's claim.
        //
        // WHY IT IS SAFE: the clear is OWNERSHIP-CHECKED. It nulls the pointer only while it
        // still names THIS task; if a newer dispatch has already claimed the pointer for another
        // task, the call is a no-op and that live claim is preserved. Every guard/early-exit path
        // above (no pipeline, terminal goal, planning window, stale completion, abandoned or
        // already-admitted slot) returns before this line and therefore leaves the pointer
        // exactly as it was.
        pipeline.ClearActiveTaskIfCurrent(result.TaskId);

        _logger.LogInformation("Pipeline {GoalId} task completed (phase={Phase}, status={Status}, model={Model})",
            pipeline.GoalId, pipeline.Phase, result.Status,
            string.IsNullOrEmpty(result.Model) ? "unknown" : result.Model);

        if (_brain is null)
        {
            // THE NO-BRAIN OUTPUT COPY — the admitted completion's half of the mutation-ownership
            // split. The no-brain path bypasses PipelineDriver entirely, so nothing else would
            // record this worker's output. It sits HERE deliberately: after every guard and the
            // admission (so a stale/duplicate/abandoned/terminal/planning completion can never
            // write), after the pointer release, and before the terminal MarkGoalCompletedAsync.
            //
            // Summary-preferred, output otherwise, and UNTRUNCATED — the value transport used to
            // write directly. A Failed result never overwrites an existing entry; Cancelled stays
            // eligible (the predicate is non-Failed, not "success only"). No phase entry → no-op.
            //
            // NORMALIZATION, deliberately: this applies to EVERY admitted no-brain completion —
            // including Unspecified-role transport and direct domain callers — because a
            // TaskResult carries no worker role and the pipeline phase is not a stand-in for one.
            if (result.Status != TaskOutcome.Failed && pipeline.CurrentPhaseEntry is { } noBrainEntry)
            {
                noBrainEntry.WorkerOutput = !string.IsNullOrWhiteSpace(result.Metrics?.Summary)
                    ? result.Metrics.Summary
                    : result.Output;
            }

            // THE NO-BRAIN PATH — the degenerate single-phase mode. It completes the goal WITHOUT
            // recording the slot, deliberately: the goal reaches Done and its pipeline is removed,
            // so the admitted slot's terminal state is irrelevant. The terminal AdvanceTo abandons
            // PENDING slots only (the A1a in-flight exemption), so this slot simply stays Claimed.
            // This is an honest, chosen behaviour — do NOT "fix" it by adding a record call here.
            await _lifecycleService.MarkGoalCompletedAsync(pipeline, ct);
            return;
        }

        var phaseBefore = pipeline.Phase;
        try
        {
            await _pipelineDriver.DriveNextPhaseAsync(pipeline, result, ct);

            // THE RECORD, and it belongs HERE — inside the try, immediately after a SUCCESSFUL
            // drive. It must never move below the catch blocks: a FAILED drive has to leave the
            // slot Claimed, not Recorded. The terminal AbandonPendingSlots exempts in-flight
            // (Claimed/Recorded) slots per the A1a design, so a failed drive's slot stays Claimed;
            // reconciling that terminal residue is the E2 successor's job, not this path's.
            _ = pipeline.RecordSlot(result.TaskId);
        }
        catch (OperationCanceledException)
        {
            // Caller cancellation (service shutdown) is NOT a pipeline failure. Propagate it
            // instead of marking the goal Failed with an already-cancelled token — doing so
            // would mutate the pipeline to Failed and then fail to persist it.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error driving pipeline {GoalId} to next phase", pipeline.GoalId);
            await _lifecycleService.MarkGoalFailedAsync(pipeline, ex.Message, ct);
        }

        if (pipeline.Phase != phaseBefore && pipeline.Phase is not GoalPhase.Done and not GoalPhase.Failed)
            _dashboardNotifier?.NotifyStateChanged();

        _pipelineManager.PersistFull(pipeline);
    }
}

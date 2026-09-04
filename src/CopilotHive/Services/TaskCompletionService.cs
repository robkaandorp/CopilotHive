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

        _logger.LogInformation("Pipeline {GoalId} task completed (phase={Phase}, status={Status}, model={Model})",
            pipeline.GoalId, pipeline.Phase, result.Status,
            string.IsNullOrEmpty(result.Model) ? "unknown" : result.Model);

        if (_brain is null)
        {
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

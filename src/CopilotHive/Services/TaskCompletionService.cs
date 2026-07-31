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

        // Guard: ignore completions from tasks that are no longer the active task
        // (e.g., a stale task from a previous phase completing after the pipeline advanced)
        if (pipeline.ActiveTaskId is not null && pipeline.ActiveTaskId != result.TaskId)
        {
            _logger.LogWarning(
                "Task {TaskId} completed but pipeline {GoalId} active task is {ActiveTaskId} — ignoring stale completion",
                result.TaskId, pipeline.GoalId, pipeline.ActiveTaskId);
            return;
        }

        _logger.LogInformation("Pipeline {GoalId} task completed (phase={Phase}, status={Status}, model={Model})",
            pipeline.GoalId, pipeline.Phase, result.Status,
            string.IsNullOrEmpty(result.Model) ? "unknown" : result.Model);

        if (_brain is null)
        {
            await _lifecycleService.MarkGoalCompletedAsync(pipeline, ct);
            return;
        }

        var phaseBefore = pipeline.Phase;
        try
        {
            await _pipelineDriver.DriveNextPhaseAsync(pipeline, result, ct);
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

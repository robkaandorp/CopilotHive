using System.Collections.Concurrent;
using CopilotHive.Agents;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Improvement;
using CopilotHive.Knowledge;
using CopilotHive.Metrics;
using CopilotHive.Orchestration;
using CopilotHive.Workers;
using Microsoft.Extensions.AI;
using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Services;

/// <summary>
/// Handles per-role task dispatching and repository resolution for goal pipelines.
/// Extracted from <see cref="GoalDispatcher"/> — all logic is identical.
/// </summary>
internal sealed class TaskDispatchService
{
    private readonly TaskQueue _taskQueue;
    private readonly IWorkerGateway _workerGateway;
    private readonly TaskBuilder _taskBuilder;
    private readonly HiveConfigFile? _config;
    private readonly ILogger<TaskDispatchService> _logger;
    private readonly GoalPipelineManager _pipelineManager;
    private readonly GoalLifecycleService _lifecycleService;
    private readonly DispatcherMaintenance _maintenance;

    public TaskDispatchService(
        TaskQueue taskQueue,
        IWorkerGateway workerGateway,
        TaskBuilder taskBuilder,
        HiveConfigFile? config,
        ILogger<TaskDispatchService> logger,
        GoalPipelineManager pipelineManager,
        GoalLifecycleService lifecycleService,
        DispatcherMaintenance maintenance)
    {
        _taskQueue = taskQueue;
        _workerGateway = workerGateway;
        _taskBuilder = taskBuilder;
        _config = config;
        _logger = logger;
        _pipelineManager = pipelineManager;
        _lifecycleService = lifecycleService;
        _maintenance = maintenance;
    }

    internal async Task DispatchToRole(GoalPipeline pipeline, WorkerRole role, string? prompt, CancellationToken ct)
    {
        // Slice 3b refusal gate — compute the FINAL effective model FIRST and refuse when it is
        // null, BEFORE any task registration with the pipeline manager, any enqueue, or any
        // worker/LLM session creation. For a premium phase: premium_model if set, else the
        // standard role model. For a non-premium phase: the standard role model. A premium
        // phase with premium_model set and NO standard role model dispatches on the premium
        // model (NOT refused); a premium phase with no premium_model falls back to the standard
        // role model (preserved exemption — NOT refused).
        var roleName = role.ToRoleName();
        var currentPhase = pipeline.StateMachine.Phase;
        var phaseTier = pipeline.Plan?.PhaseTiers.GetValueOrDefault(currentPhase, ModelTier.Default) ?? ModelTier.Default;
        var model = _config?.GetModelForRole(roleName);
        if (phaseTier == ModelTier.Premium && _config is not null)
        {
            var premiumModel = _config.GetPremiumModelForRole(roleName);
            if (!string.IsNullOrWhiteSpace(premiumModel))
                model = premiumModel;
        }

        if (model is null)
            throw new InvalidOperationException($"role '{roleName}' has no configured model");

        prompt ??= $"Work on: {pipeline.Description}";

        // Log the prompt being sent to the worker
        var promptPreview = prompt.Length > 1500
            ? prompt[..1500] + $"... ({prompt.Length} chars total)"
            : prompt;
        _logger.LogDebug("Prompt for {Role} (goal={GoalId}):\n{Prompt}",
            role, pipeline.GoalId, promptPreview);

        var branchAction = pipeline.CoderBranch is null ? BranchAction.Create : BranchAction.Checkout;

        List<TargetRepository> repositories;
        try
        {
            repositories = ResolveRepositories(pipeline.Goal);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Repository configuration error for goal {GoalId}", pipeline.GoalId);
            await _lifecycleService.MarkGoalFailedAsync(pipeline, ex.Message, ct);
            return;
        }

        // Resolve the effective reasoning effort for this task.
        //
        // Precedence:
        //   1. WorkerConfig.PremiumReasoningEffort — when the phase requested the premium tier AND
        //      a premium model is actually configured for this role.
        //   2. WorkerConfig.ReasoningEffort — otherwise.
        //
        // The parsed enum is transported on the WorkTask and is authoritative for the worker.
        // The model name always stays plain — reasoning is never baked into it.
        ReasoningEffort? effectiveReasoning = null;
        if (_config is not null && model is not null)
        {
            var hasPremiumModel = !string.IsNullOrWhiteSpace(_config.GetPremiumModelForRole(roleName));
            _config.Workers.TryGetValue(roleName.ToLowerInvariant(), out var workerConfig);

            var effortString = phaseTier == ModelTier.Premium && hasPremiumModel
                ? workerConfig?.PremiumReasoningEffort
                : workerConfig?.ReasoningEffort;

            // Startup validates reasoning efforts, but dynamic config reloads deliberately do
            // not re-validate. An invalid value must degrade to "unset" rather than fail the
            // dispatch (and the goal) with an unhandled ArgumentException.
            try
            {
                effectiveReasoning = ReasoningEffortConverter.Parse(effortString);
            }
            catch (ArgumentException)
            {
                _logger.LogWarning(
                    "Invalid reasoning_effort '{Effort}' configured for role {Role}; dispatching with reasoning effort unset.",
                    effortString, roleName);
                effectiveReasoning = null;
            }
        }

        _logger.LogDebug("Model for {Role}: {Model} (tier={Tier}, configLoaded={ConfigLoaded})",
            roleName, model ?? "(null)", phaseTier, _config is not null);

        // Resolve context window: per-role override > global worker default > constant fallback
        var maxContextTokens = _config?.GetContextWindowForRole(roleName) ?? Constants.DefaultBrainContextWindow;

        // Populate sub-agent model catalog from config (no reasoning suffix — sub-agents inherit parent reasoning)
        var subAgentModels = new List<SubAgentModelDto>();
        var subAgentCatalog = _config?.GetSubAgentModels() ?? [];
        foreach (var entry in subAgentCatalog)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
                continue;
            var autoDescription = entry.ContextWindow is int cw && cw > 0
                ? $"Configured model, {cw / 1000}K context"
                : "Configured model";
            subAgentModels.Add(new SubAgentModelDto
            {
                Id = entry.Name,
                ContextWindow = entry.ContextWindow,
                Description = !string.IsNullOrWhiteSpace(entry.Description) ? entry.Description : autoDescription,
                SupportsVision = entry.SupportsVision ?? false,
            });
        }

        // ══════════════════════════════════════════════════════════════════════════════════
        //  THE ADMISSION TRANSACTION. Everything above is PREPARATION and touches no shared
        //  state; from here on the dispatch owns a work slot, a task→goal mapping and the
        //  pipeline's active-task pointer, and every failure vector releases them again.
        //
        //  LOCK ORDER: this path NEVER acquires the state machine's private lock. The captured
        //  snapshot APIs (CaptureDispatchPosition and friends) own that monitor internally.
        // ══════════════════════════════════════════════════════════════════════════════════

        // (1) THE CAPTURE. The position is validated and its slot allocated atomically. Every
        // integrity refusal is logged with its matching WorkSlotIntegrity template and
        // PROPAGATES: no slot, no mapping, no pointer, no task, no delivery.
        SlotBuildResult slot;
        try
        {
            slot = pipeline.CaptureDispatchPosition(role);
        }
        catch (WorkSlotException ex)
        {
            LogCaptureRefusal(pipeline.GoalId, ex);
            throw;
        }

        var taskId = slot.TaskId;

        // (2) THE BUILD, stamped with the captured (attempt-stamped) task ID VERBATIM. A build
        // failure releases the slot before the original failure propagates.
        WorkTask task;
        try
        {
            task = _taskBuilder.Build(
                goalId: pipeline.GoalId,
                goalDescription: pipeline.Description,
                role: role,
                iteration: pipeline.Iteration,
                repositories: repositories,
                prompt: prompt,
                branchAction: branchAction,
                model: model,
                maxContextTokens: maxContextTokens,
                subAgentModels: subAgentModels,
                reasoningEffort: effectiveReasoning,
                taskId: taskId);
        }
        catch
        {
            pipeline.AbandonSlot(taskId);
            LogAbandonedRegistration(pipeline.GoalId, taskId, slot.Position);
            throw;
        }

        // Improver operates read-only: it can see the feature branch but must not push.
        // Downgrade the action to Unspecified so the worker runtime skips push operations.
        if (role == WorkerRole.Improver && task.BranchInfo is not null)
        {
            task.BranchInfo.Action = BranchAction.Unspecified;
        }

        // Propagate the iteration start SHA to the worker via metadata so reviewers can
        // compute an iteration-scoped diff alongside the cumulative branch diff.
        if (pipeline.IterationStartSha is not null)
            task.Metadata["iteration_start_sha"] = pipeline.IterationStartSha;

        // Propagate the tester's structured report to the reviewer so it can be retrieved via get_test_report.
        if (role == WorkerRole.Reviewer)
        {
            var testerEntry = pipeline.PhaseLog
                .LastOrDefault(e => e.Name == GoalPhase.Testing && e.Iteration == pipeline.Iteration && e.WorkerOutput is not null);
            if (testerEntry?.WorkerOutput is not null)
            {
                task.Metadata["tester_report"] = testerEntry.WorkerOutput;
            }
        }

        // Propagate compaction model to the worker so it creates a separate IChatClient for context compaction.
        var compactionModel = _config?.Models?.CompactionModel;
        if (!string.IsNullOrEmpty(compactionModel))
        {
            var compactionCtx = _config?.TryGetContextWindowForModel(compactionModel);

            task.Metadata["compaction_model"] = compactionModel;
            if (compactionCtx is int ctx && ctx > 0)
                task.Metadata["compaction_max_tokens"] = ctx.ToString();
        }

        // (3) THE MAPPING. The ownership-checked registration is the only writer of the
        // task→goal mapping on this path: it refuses rather than stealing a mapping that
        // already belongs to someone else, and it carries the store exception when the
        // persisted write threw. Both refusal causes release the slot; the thrown
        // InvalidOperationException keeps the two DISTINGUISHABLE at the exception level —
        // a duplicate mapping has a NULL inner exception, a persistence failure carries it.
        var registration = _pipelineManager.TryRegisterTask(taskId, pipeline.GoalId);
        if (!registration.Success)
        {
            pipeline.AbandonSlot(taskId);
            LogAbandonedRegistration(pipeline.GoalId, taskId, slot.Position);
            throw new InvalidOperationException(
                $"Task mapping registration failed for {taskId} (goal {pipeline.GoalId}) — the mapping is occupied or the persistence failed",
                registration.PersistenceException);
        }

        // (4) THE POINTER. Non-fallible by contract (a sealed, lock-guarded assignment).
        pipeline.SetActiveTask(task.TaskId, task.BranchInfo?.FeatureBranch);

        // (5) THE ENQUEUE. The catch spans the TaskQueue.Enqueue call ONLY — the direct-push
        // path below stays OUTSIDE it, because the delivery transaction (a later goal) owns
        // that path and must not be pre-empted by this rollback.
        //
        // THE ORPHAN EDGE (accepted trade). An insert-then-throw inside Enqueue may leave the
        // task admitted to the queue while this rollback unregisters its mapping. The later
        // assignment then finds no pipeline for the task and hits the existing no-pipeline
        // drop — an orphaned queue entry, never a double-assigned slot.
        try
        {
            _taskQueue.Enqueue(task);
        }
        catch (Exception)
        {
            // THE BEST-EFFORT ROLLBACK, in exact order: slot → mapping → pointer → warning →
            // rethrow of the ORIGINAL exception (never a wrapper).

            // (a) Release the slot. Belt-and-braces: the call is sealed, non-virtual and has no
            // feasible failure vector, but a throw here must not abort the remaining rollback.
            try
            {
                pipeline.AbandonSlot(taskId);
            }
            catch (Exception ex)
            {
                LogRollbackFailure(pipeline.GoalId, taskId, "abandon", ex);
            }

            // (b) Remove OUR mapping. The result is ALWAYS logged; only the partial outcome
            // (our memory ownership removed but the row delete failed-or-was-not-ours) is a
            // WARNING. A raced (false, false) removed nothing of ours — DEBUG only.
            try
            {
                var unregister = _pipelineManager.TryUnregisterTask(taskId, pipeline.GoalId);
                _logger.LogDebug(
                    "WorkSlotIntegrity: unregister goal={GoalId} task={TaskId} memoryRemoved={MemoryRemoved} persistenceRemoved={PersistenceRemoved}",
                    pipeline.GoalId, taskId, unregister.MemoryRemoved, unregister.PersistenceRemoved);

                if (unregister.MemoryRemoved && !unregister.PersistenceRemoved)
                    LogRollbackFailure(pipeline.GoalId, taskId, "unregister-persist", null);
            }
            catch (Exception ex)
            {
                // TryUnregisterTask promises never to throw; an escape is a contract violation.
                LogRollbackFailure(pipeline.GoalId, taskId, "unregister", ex);
            }

            // (c) Clear the pointer ONLY when it still names this task — a newer dispatch's
            // pointer must never be erased. Belt-and-braces for the same reason as (a).
            try
            {
                pipeline.ClearActiveTaskIfCurrent(taskId);
            }
            catch (Exception ex)
            {
                LogRollbackFailure(pipeline.GoalId, taskId, "pointer", ex);
            }

            // (d) The slot-release record, then (e) the ORIGINAL failure.
            LogAbandonedRegistration(pipeline.GoalId, taskId, slot.Position);
            throw;
        }
        _logger.LogInformation("Dispatched {Role} task {TaskId} for goal {GoalId} (branch={Branch})",
            role, task.TaskId, pipeline.GoalId, task.BranchInfo?.FeatureBranch);

        // Try to push directly to an idle worker
        var idleWorker = _workerGateway.GetIdleWorker();
        if (idleWorker is not null)
        {
            var queuedTask = _taskQueue.TryDequeue(role);
            queuedTask ??= _taskQueue.TryDequeueAny();

            if (queuedTask is not null)
            {
                idleWorker.Role = queuedTask.Role;
                var taskRoleName = queuedTask.Role.ToRoleName();
                _logger.LogInformation("Worker {WorkerId} assigned role {Role} for task {TaskId}",
                    idleWorker.Id, taskRoleName, queuedTask.TaskId);
                await _maintenance.SendAgentsMdToWorkerAsync(idleWorker, queuedTask.Role, ct);

                _taskQueue.Activate(queuedTask, idleWorker.Id);
                _workerGateway.MarkBusy(idleWorker.Id, queuedTask.TaskId);
                idleWorker.CurrentModel = queuedTask.Model;
                await _workerGateway.SendTaskAsync(idleWorker.Id, queuedTask, ct);
                _logger.LogInformation("Task {TaskId} pushed to worker {WorkerId}", queuedTask.TaskId, idleWorker.Id);
            }
        }
    }

    /// <summary>
    /// Renders a structured-log field value, substituting <c>unknown</c> for <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Every dispatch-level template field is guaranteed non-null by the capture's contracts
    /// (<see cref="WorkSlotException.Position"/> is never null, and the role/phase fields are
    /// populated per event), so no dispatch-level <c>unknown</c> vector is reachable. The
    /// helper exists so the rendering can never produce a bare empty field, and its own unit
    /// tests are that rendering's coverage.
    /// </remarks>
    /// <param name="value">The value to render.</param>
    /// <returns><paramref name="value"/>'s <c>ToString()</c>, or <c>"unknown"</c> when null.</returns>
    private static string FormatLogValue(object? value) => value?.ToString() ?? "unknown";

    /// <summary>
    /// Logs the WorkSlotIntegrity refusal template matching <paramref name="ex"/>'s event.
    /// </summary>
    private void LogCaptureRefusal(string goalId, WorkSlotException ex)
    {
        var position = ex.Position;
        switch (ex.Event)
        {
            case WorkSlotEvent.DoubleAssignment:
                _logger.LogWarning(
                    "WorkSlotIntegrity: double-assignment goal={GoalId} position={Iteration}:{Phase}:{Occurrence} existing={ExistingTaskId} — the dispatch is refused",
                    goalId, position.Iteration, FormatLogValue(position.Phase), position.Occurrence,
                    FormatLogValue(ex.ExistingTaskId));
                break;
            case WorkSlotEvent.RoleMismatch:
                _logger.LogWarning(
                    "WorkSlotIntegrity: role-mismatch goal={GoalId} position={Iteration}:{Phase}:{Occurrence} passed={PassedRole} derived={DerivedRole} — the dispatch is refused",
                    goalId, position.Iteration, FormatLogValue(position.Phase), position.Occurrence,
                    FormatLogValue(ex.PassedRole), FormatLogValue(ex.DerivedRole));
                break;
            case WorkSlotEvent.InvalidPhase:
                _logger.LogWarning(
                    "WorkSlotIntegrity: invalid-phase goal={GoalId} position={Iteration}:{Phase}:{Occurrence} machine-phase={MachinePhase} — the dispatch is refused",
                    goalId, position.Iteration, FormatLogValue(position.Phase), position.Occurrence,
                    FormatLogValue(ex.MachinePhase));
                break;
            case WorkSlotEvent.PhaseDivergence:
                _logger.LogWarning(
                    "WorkSlotIntegrity: phase-divergence goal={GoalId} position={Iteration}:{Phase}:{Occurrence} pipeline-phase={PipelinePhase} machine-phase={MachinePhase} — the dispatch is refused",
                    goalId, position.Iteration, FormatLogValue(position.Phase), position.Occurrence,
                    FormatLogValue(ex.PipelinePhase), FormatLogValue(ex.MachinePhase));
                break;
            case WorkSlotEvent.PlanUnavailable:
                _logger.LogWarning(
                    "WorkSlotIntegrity: plan-unavailable goal={GoalId} position={Iteration}:{Phase}:{Occurrence} machine-phase={MachinePhase} — the dispatch is refused",
                    goalId, position.Iteration, FormatLogValue(position.Phase), position.Occurrence,
                    FormatLogValue(ex.MachinePhase));
                break;
            default:
                throw new InvalidOperationException($"Unhandled WorkSlotEvent: {ex.Event}");
        }
    }

    /// <summary>Logs that a dispatch failed before delivery and its slot was released.</summary>
    private void LogAbandonedRegistration(string goalId, string taskId, WorkSlotPosition position) =>
        _logger.LogWarning(
            "WorkSlotIntegrity: abandoned-registration goal={GoalId} task={TaskId} position={Iteration}:{Phase}:{Occurrence} — the dispatch failed before delivery; the slot is released",
            goalId, taskId, position.Iteration, FormatLogValue(position.Phase), position.Occurrence);

    /// <summary>
    /// Logs a failed rollback step. <paramref name="step"/> is one of
    /// <c>abandon</c>, <c>pointer</c>, <c>unregister</c>, <c>unregister-persist</c>.
    /// </summary>
    private void LogRollbackFailure(string goalId, string taskId, string step, Exception? ex) =>
        _logger.LogWarning(
            ex,
            "WorkSlotIntegrity: rollback-failure goal={GoalId} task={TaskId} step={Step} — the rollback step failed; continuing",
            goalId, taskId, step);

    /// <summary>
    /// Resolves the list of <see cref="TargetRepository"/> instances for the given goal by looking
    /// up each repository name in the hive configuration.
    /// </summary>
    /// <param name="goal">The goal whose <see cref="Goal.RepositoryNames"/> are to be resolved.</param>
    /// <returns>A list of resolved <see cref="TargetRepository"/> objects with injected credentials.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when any repository name referenced by the goal is not defined in hive-config.yaml.
    /// </exception>
    internal List<TargetRepository> ResolveRepositories(Goal goal)
    {
        var repos = new List<TargetRepository>();

        foreach (var repoName in goal.RepositoryNames)
        {
            var repoConfig = _config?.Repositories.FirstOrDefault(
                r => r.Name.Equals(repoName, StringComparison.OrdinalIgnoreCase));

            if (repoConfig is not null)
            {
                var url = PipelineHelpers.InjectTokenIntoUrl(repoConfig.Url);
                repos.Add(new TargetRepository
                {
                    Name = repoConfig.Name,
                    Url = url,
                    DefaultBranch = repoConfig.DefaultBranch,
                });
            }
            else
            {
                throw new InvalidOperationException(
                    $"Goal '{goal.Id}' references repository '{repoName}' which is not defined in hive-config.yaml. Add it to the repositories section or remove it from the goal.");
            }
        }

        return repos;
    }
}

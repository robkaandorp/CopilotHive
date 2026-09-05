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

    /// <summary>
    /// The PHASE-1 preparation product: everything resolved BEFORE the repository resolution.
    /// </summary>
    /// <param name="RoleName">The role's canonical config name.</param>
    /// <param name="Model">The FINAL effective model — never null (the refusal gate guarantees it).</param>
    /// <param name="PhaseTier">The phase's model tier; phase 2 needs it for the reasoning-effort
    /// selection and for the model debug log's <c>tier=</c> field.</param>
    /// <param name="Prompt">The prompt, defaulted when the caller passed none.</param>
    /// <param name="BranchAction">The derived branch action.</param>
    private sealed record DispatchContext(
        string RoleName,
        string Model,
        ModelTier PhaseTier,
        string Prompt,
        BranchAction BranchAction);

    /// <summary>
    /// The PHASE-2 preparation product: everything resolved AFTER the repository resolution.
    /// </summary>
    /// <param name="Reasoning">The effective reasoning effort, or null when unset/degraded.</param>
    /// <param name="MaxContextTokens">The resolved context window.</param>
    /// <param name="SubAgentModels">The sub-agent model catalog.</param>
    private sealed record DispatchTailContext(
        ReasoningEffort? Reasoning,
        int MaxContextTokens,
        IReadOnlyList<SubAgentModelDto> SubAgentModels);

    /// <summary>
    /// PHASE 1 of the dispatch preparation: the model/tier resolution and its refusal gate, the
    /// prompt defaulting and its debug preview, and the branch-action derivation.
    /// </summary>
    /// <remarks>
    /// The missing-model <see cref="InvalidOperationException"/> is deliberately NOT caught here:
    /// it propagates out of <see cref="DispatchToRole"/> exactly as it did inline.
    /// </remarks>
    /// <param name="pipeline">The dispatching pipeline.</param>
    /// <param name="role">The role being dispatched to.</param>
    /// <param name="prompt">The caller's prompt, or null to default it.</param>
    /// <returns>The phase-1 preparation product.</returns>
    /// <exception cref="InvalidOperationException">The role has no configured model.</exception>
    private DispatchContext BuildDispatchContext(GoalPipeline pipeline, WorkerRole role, string? prompt)
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

        return new DispatchContext(roleName, model, phaseTier, prompt, branchAction);
    }

    /// <summary>
    /// PHASE 2 of the dispatch preparation: the reasoning-effort resolution and its degradation,
    /// the model debug log, the context-window resolution and the sub-agent catalog construction.
    /// </summary>
    /// <param name="head">The phase-1 preparation product.</param>
    /// <returns>The phase-2 preparation product.</returns>
    private DispatchTailContext BuildDispatchTail(DispatchContext head)
    {
        var roleName = head.RoleName;
        var model = head.Model;
        var phaseTier = head.PhaseTier;

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

        return new DispatchTailContext(effectiveReasoning, maxContextTokens, subAgentModels);
    }

    internal async Task DispatchToRole(GoalPipeline pipeline, WorkerRole role, string? prompt, CancellationToken ct)
    {
        // PHASE 1 of the preparation. The missing-model refusal propagates from here UNCAUGHT.
        var head = BuildDispatchContext(pipeline, role, prompt);

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

        // PHASE 2 of the preparation.
        var tail = BuildDispatchTail(head);

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
                prompt: head.Prompt,
                branchAction: head.BranchAction,
                model: head.Model,
                maxContextTokens: tail.MaxContextTokens,
                subAgentModels: tail.SubAgentModels,
                reasoningEffort: tail.Reasoning,
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
                // GUARDED SITE (β-PREP-2): the dispatch-owned unregister-result record goes
                // through LogSafely, so a throwing logger is swallowed and the rollback
                // continues — the cleanup-before-log contract.
                LogSafely(() => _logger.LogDebug(
                    "WorkSlotIntegrity: unregister goal={GoalId} task={TaskId} memoryRemoved={MemoryRemoved} persistenceRemoved={PersistenceRemoved}",
                    pipeline.GoalId, taskId, unregister.MemoryRemoved, unregister.PersistenceRemoved));

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

        // ══════════════════════════════════════════════════════════════════════════════════
        //  THE DELIVERY TRANSACTION. The direct push to an idle worker, restructured into named
        //  stages, each with ONE authoritative failure classification:
        //
        //    G  (get-worker)   GetIdleWorker — runs BEFORE the dequeue, so nothing is touched:
        //                      every throw (ordinary or cancellation) PROPAGATES UNCAUGHT.
        //    D  (dequeue)      TryDequeue(role) ?? TryDequeueAny() — no throw path; a null result
        //                      is the honest no-op.
        //    A  (agents-md)    BEST-EFFORT: DispatcherMaintenance swallows its own failures, so
        //                      stage A is NOT OBSERVABLE from this transaction.
        //    cancel-check      The observation point: dequeued, NOT activated, worker NOT busy —
        //                      the ONE provably-safe recovery point, hence THE REQUEUE.
        //    P1 (activate)     NON-THROWING BY CONTRACT (see the two-mutation comment below).
        //    P2 (mark-busy)    RUNTIME-REACHABLE (the interface throw) → THE AMBIGUITY-PRESERVE.
        //    S  (send)         THE AMBIGUITY POINT → THE PRESERVE.
        //
        //  THE PROPAGATION RULE: every caught DELIVERY-OPERATION exception is RETHROWN UNCHANGED
        //  after its recovery. Every POST-DEQUEUE logger failure is swallowed by the logging
        //  guards; PRE-DEQUEUE logger failures propagate as infrastructure failures.
        //
        //  THE MISMATCH HANDOFF: the push delivers whatever the queue yields — which may be an
        //  EARLIER queued task of the requested role belonging to ANOTHER pipeline (the role-aware
        //  FIFO — correct, and now observable through the delivery-mismatch record). Every recovery
        //  below therefore acts on `queuedTask` ONLY: no pipeline-level operation appears anywhere
        //  in this transaction's recoveries.
        // ══════════════════════════════════════════════════════════════════════════════════

        // STAGE G. Nothing has been dequeued yet, so this call is deliberately NOT guarded.
        var idleWorker = _workerGateway.GetIdleWorker();
        if (idleWorker is null)
            return;

        // STAGE D. The role-aware dequeue first, then the role-agnostic fallback. Neither throws;
        // a null result simply means there is nothing to push right now.
        var queuedTask = _taskQueue.TryDequeue(role) ?? _taskQueue.TryDequeueAny();
        if (queuedTask is null)
            return;

        // THE CAPTURE AT D — everything the recoveries and the records need, read ONCE, before any
        // mutation. `deliveredGoalId` is the DELIVERED task's goal (the log owner); `registeredTaskId`
        // is the task THIS dispatch admitted; `workerRoleBeforeAssignment` is the worker's
        // PRE-MUTATION role, the only value the restore may write back.
        var deliveryWorkerId = idleWorker.Id;
        var deliveredTaskId = queuedTask.TaskId;
        var deliveredRole = queuedTask.Role;
        var deliveredModel = queuedTask.Model;
        var deliveredGoalId = queuedTask.GoalId;
        var registeredTaskId = task.TaskId;
        var workerRoleBeforeAssignment = idleWorker.Role;

        // ── THE POST-DEQUEUE LOGGING BOUNDARY ──────────────────────────────────────────────
        // From here on EVERY log call inside this transaction goes through a logging guard: a
        // diagnostic failure must NEVER strand a dequeued task. The dequeue is the boundary.
        // ───────────────────────────────────────────────────────────────────────────────────

        if (!string.Equals(deliveredTaskId, registeredTaskId, StringComparison.Ordinal))
            LogDeliveryMismatch(deliveredGoalId, registeredTaskId, deliveredTaskId);

        idleWorker.Role = deliveredRole;
        var taskRoleName = deliveredRole.ToRoleName();
        LogSafely(() => _logger.LogInformation("Worker {WorkerId} assigned role {Role} for task {TaskId}",
            deliveryWorkerId, taskRoleName, deliveredTaskId));

        // STAGE A — BEST-EFFORT, AND CONTAINED AT THIS BOUNDARY.
        //
        // SendAgentsMdToWorkerAsync already swallows its own gateway-send failure — but it does so
        // by LOGGING it, and that log is itself a POST-DEQUEUE diagnostic. If the logging
        // infrastructure throws from inside that catch, the exception escapes the awaited call at
        // the worst possible instant: the task is dequeued and the worker's Role is already
        // reassigned, yet the task is NOT activated and the cancel-check's requeue has not run —
        // the dequeued task would be STRANDED by a pure diagnostic failure.
        //
        // The boundary rule admits no exception: EVERY log call after the dequeue is best-effort,
        // INCLUDING the ones reached indirectly. The maintenance class is not ours to change, so
        // the containment lives here, wrapping the whole awaited call.
        //
        // THE CLASSIFICATION IS UNCHANGED, and this is exactly what makes the blanket catch
        // correct rather than a mask: the recovery table declares stage A NOT OBSERVABLE from this
        // transaction for BOTH an ordinary failure and a cancellation. Nothing observable is
        // therefore being swallowed that the table says should be seen — a cancellation is observed
        // one line later, at the cancel-check, from the TOKEN, never from this call's exception. So
        // control simply FALLS THROUGH: no rethrow, and no diversion into the cancel-check's
        // requeue (which is reserved for a genuinely cancelled token).
        try
        {
            await _maintenance.SendAgentsMdToWorkerAsync(idleWorker, deliveredRole, ct);
        }
        catch (Exception)
        {
            // Deliberately empty: stage A is non-observable by contract, and a diagnostic failure
            // escaping it must never strand the dequeued task. Re-reporting it here would need the
            // very logger that just threw.
        }

        // STAGE cancel-check — THE REQUEUE POINT. The task is dequeued, NOT activated, and the
        // worker is NOT busy: returning the task to the pending queue is provably safe here, and
        // ONLY here.
        try
        {
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            // THE REQUEUE SEQUENCE, in exact order: (1) enqueue → (2) the guard line →
            // (3) the role restore → (4) the failure line → (5) the ORIGINAL rethrow.
            //
            // THE BELT-AND-BRACES: the OPERATIONAL steps (1) and (3) are individually try/caught.
            // A step's own throw is recorded as delivery-rollback-failure and swallowed; the
            // remaining steps still run, and the ORIGINAL exception is always what leaves here.

            // (1) Return the task to the pending queue.
            var reEnqueued = false;
            try
            {
                _taskQueue.Enqueue(queuedTask);
                reEnqueued = true;
            }
            catch (Exception stepEx)
            {
                LogDeliveryRollbackFailure(deliveredGoalId, deliveredTaskId, "re-enqueue", stepEx);
            }

            // (2) THE GUARD LINE — emitted IFF the Enqueue call RETURNED NORMALLY. TaskQueue.Enqueue
            // inserts into its pending queue BEFORE invoking OnEnqueue (its only throwing seam), so
            // a throwing OnEnqueue means the task IS already pending — but the step did not complete,
            // and in that vector the delivery-rollback-failure record above is THE record instead.
            // The guard line's "completed through the re-enqueue" is therefore always literally true.
            if (reEnqueued)
                LogDeliveryRecovery(deliveredGoalId, deliveredTaskId);

            // (3) THE ROLE RESTORE — BEST-EFFORT and ROLE-ONLY. CurrentModel is NEVER assigned at
            // this stage (it is written only after MarkBusy returns), so there is nothing of it to
            // restore. The restore only writes back when the worker still holds THE VALUE WE
            // ASSIGNED; a third value means someone else has since claimed the worker.
            //
            // HONEST LIMIT: this check-then-write is NOT atomic. It avoids the common overwrite of
            // a concurrent assignment — it is not a concurrency guarantee.
            //
            // SEAM HONESTY: ConnectedWorker.Role is a plain auto-property on a sealed class, so no
            // throwing seam exists. The catch below is a CODE-REVIEW CRITERION (the structure of the
            // belt-and-braces), NOT a runtime vector: the only runtime-tested rollback-step failure
            // is `re-enqueue`.
            try
            {
                if (idleWorker.Role == deliveredRole)
                    idleWorker.Role = workerRoleBeforeAssignment;
            }
            catch (Exception stepEx)
            {
                LogDeliveryRollbackFailure(deliveredGoalId, deliveredTaskId, "role-model-restore", stepEx);
            }

            // (4) The failure record, then (5) the ORIGINAL cancellation.
            LogDeliveryFailure(
                deliveredGoalId, deliveredTaskId, deliveryWorkerId,
                DeliveryStage.CancelCheck, DeliveryRecovery.Requeue);
            throw;
        }

        // STAGE P1 — NON-THROWING BY CONTRACT. THE TWO-MUTATION COMMENT: TaskQueue.Activate performs
        // exactly two in-memory mutations — the ConcurrentDictionary indexer assignment that admits
        // the task to the active set, AND the task.Metadata["assigned_worker"] write. Both are
        // sealed, in-memory dictionary writes with no user-supplied seam and no failure vector, so
        // this stage has no recovery clause. A violation of that contract is outside EVERY
        // transaction's coverage — this one included.
        _taskQueue.Activate(queuedTask, deliveryWorkerId);

        // STAGE P2 — THE AMBIGUITY-PRESERVE. MarkBusy is an INTERFACE call, so a throw here is
        // runtime-reachable, and whether the busy mutation was applied is UNKNOWABLE from here. The
        // task is already active; re-enqueueing it could double-assign it. So: NO recovery steps —
        // the record fires and the ORIGINAL exception rethrows.
        //
        // THE DEFERRAL NOTE (honest scope):
        //   (a) MUTATED-then-threw — the worker IS busy with this task: the stale-cleanup's
        //       busy-task timeout reclaims it.
        //   (b) PRE-MUTATION throw — the task is active with an IDLE worker: NOT covered by the
        //       stale-cleanup's predicate. THE ORDERED SUCCESSOR `atomic-worker-reservation` owns
        //       this case; its subject is the reservation API AND the idle-worker-with-active-task
        //       reconciliation sweep. This goal does NOT claim that case's recovery — it DEFERS it.
        try
        {
            _workerGateway.MarkBusy(deliveryWorkerId, deliveredTaskId);
            idleWorker.CurrentModel = deliveredModel;
        }
        catch (Exception)
        {
            LogDeliveryFailure(
                deliveredGoalId, deliveredTaskId, deliveryWorkerId,
                DeliveryStage.Prepare, DeliveryRecovery.Preserve);
            throw;
        }

        // STAGE S — THE PRESERVE. The send's outcome is the ambiguity this whole transaction is
        // honest about: the worker may or may not have received the assignment. Undoing anything
        // here could deliver the same task twice, so the record fires and the ORIGINAL rethrows —
        // a caller cancellation at S takes exactly the same path.
        try
        {
            await _workerGateway.SendTaskAsync(deliveryWorkerId, queuedTask, ct);
        }
        catch (Exception)
        {
            LogDeliveryFailure(
                deliveredGoalId, deliveredTaskId, deliveryWorkerId,
                DeliveryStage.Send, DeliveryRecovery.Preserve);
            throw;
        }

        LogSafely(() => _logger.LogInformation(
            "Task {TaskId} pushed to worker {WorkerId}", deliveredTaskId, deliveryWorkerId));
    }

    /// <summary>The delivery stages that can appear in a <c>delivery-failure</c> record.</summary>
    private enum DeliveryStage
    {
        /// <summary>The explicit cancellation observation after the agents-md stage.</summary>
        CancelCheck,

        /// <summary>The MarkBusy/CurrentModel stage (P2).</summary>
        Prepare,

        /// <summary>The SendTaskAsync stage (S).</summary>
        Send,
    }

    /// <summary>The recovery classifications a <c>delivery-failure</c> record can report.</summary>
    private enum DeliveryRecovery
    {
        /// <summary>The task was returned to the pending queue (cancel-check only).</summary>
        Requeue,

        /// <summary>No recovery was attempted: the outcome is unknowable (P2 and S).</summary>
        Preserve,
    }

    /// <summary>Renders a <see cref="DeliveryStage"/> as its template token.</summary>
    private static string RenderStage(DeliveryStage stage) => stage switch
    {
        DeliveryStage.CancelCheck => "cancel-check",
        DeliveryStage.Prepare => "prepare",
        DeliveryStage.Send => "send",
        _ => throw new InvalidOperationException($"Unhandled DeliveryStage: {stage}"),
    };

    /// <summary>Renders a <see cref="DeliveryRecovery"/> as its template token.</summary>
    private static string RenderRecovery(DeliveryRecovery recovery) => recovery switch
    {
        DeliveryRecovery.Requeue => "requeue",
        DeliveryRecovery.Preserve => "preserve",
        _ => throw new InvalidOperationException($"Unhandled DeliveryRecovery: {recovery}"),
    };

    /// <summary>Renders the outcome clause matching a <see cref="DeliveryRecovery"/>.</summary>
    private static string RenderRecoveryOutcome(DeliveryRecovery recovery) => recovery switch
    {
        DeliveryRecovery.Requeue => "was returned to the pending queue",
        DeliveryRecovery.Preserve => "remains active; the outcome is unknowable; the recovery is deferred",
        _ => throw new InvalidOperationException($"Unhandled DeliveryRecovery: {recovery}"),
    };

    /// <summary>
    /// Runs a diagnostic emission best-effort: a logger's failure is swallowed so it can never
    /// affect the guarded operation.
    /// </summary>
    /// <remarks>
    /// TWO GUARDED ZONES:
    /// (1) THE DELIVERY ZONE — after the dequeue: a task is in flight and owned by nobody but this
    ///     transaction, so a logger's throw must never strand it — every record inside the delivery
    ///     transaction is best-effort.
    /// (2) THE ADMISSION-ROLLBACK ZONE — the rollback helpers (LogAbandonedRegistration,
    ///     LogRollbackFailure) and the unregister-result record: their callers run after the slot
    ///     capture, where a logger's throw must never abort the cleanup — the cleanup-before-log
    ///     contract.
    /// PRE-DEQUEUE preparation logging (the model/prompt diagnostics) deliberately stays UNGUARDED —
    /// a throw there propagates as an infrastructure failure (nothing has been touched).
    /// </remarks>
    /// <param name="emit">The guarded emission.</param>
    private static void LogSafely(Action emit)
    {
        try
        {
            emit();
        }
        catch (Exception)
        {
            // Best-effort by contract: a diagnostic failure may never affect the delivery.
        }
    }

    /// <summary>Logs a delivery-span failure and the recovery that was applied to it.</summary>
    private void LogDeliveryFailure(
        string goalId, string taskId, string workerId, DeliveryStage stage, DeliveryRecovery recovery)
    {
        var stageToken = RenderStage(stage);
        var recoveryToken = RenderRecovery(recovery);
        var outcome = RenderRecoveryOutcome(recovery);
        LogSafely(() => _logger.LogWarning(
            "WorkSlotIntegrity: delivery-failure goal={GoalId} task={TaskId} worker={WorkerId} stage={Stage} recovery={Recovery} — the task {Outcome}",
            goalId, taskId, workerId, stageToken, recoveryToken, outcome));
    }

    /// <summary>
    /// Logs that the push delivered a task OTHER than the one this dispatch admitted — the
    /// role-aware FIFO handing over an earlier queued task of the same role. Informational only.
    /// </summary>
    private void LogDeliveryMismatch(string deliveredGoalId, string registeredTaskId, string deliveredTaskId) =>
        LogSafely(() => _logger.LogDebug(
            "WorkSlotIntegrity: delivery-mismatch goal={GoalId} registered={RegisteredTaskId} delivered={DeliveredTaskId} — the push delivered an earlier queued task of the requested role (role-aware FIFO; no action)",
            deliveredGoalId, registeredTaskId, deliveredTaskId));

    /// <summary>
    /// Logs a failed DELIVERY rollback step. <paramref name="step"/> is one of
    /// <c>re-enqueue</c>, <c>role-model-restore</c>. Distinct from the admission's
    /// <c>rollback-failure</c> template.
    /// </summary>
    private void LogDeliveryRollbackFailure(string goalId, string taskId, string step, Exception ex) =>
        LogSafely(() => _logger.LogWarning(
            ex,
            "WorkSlotIntegrity: delivery-rollback-failure goal={GoalId} task={TaskId} step={Step} — the rollback step failed; continuing",
            goalId, taskId, step));

    /// <summary>
    /// Logs THE GUARD LINE: the cancel-check recovery got at least as far as a completed
    /// re-enqueue. Emitted IFF <see cref="TaskQueue.Enqueue"/> returned normally.
    /// </summary>
    private void LogDeliveryRecovery(string goalId, string taskId) =>
        LogSafely(() => _logger.LogDebug(
            "WorkSlotIntegrity: delivery-recovery goal={GoalId} task={TaskId} stage=cancel-check — the recovery steps completed through the re-enqueue",
            goalId, taskId));

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
    /// <remarks>
    /// GUARDED (β-PREP-2): the body runs inside <see cref="LogSafely"/>, so a throwing logger is
    /// swallowed and the caller's rollback/cleanup control flow is untouched — the
    /// cleanup-before-log contract.
    /// </remarks>
    private void LogAbandonedRegistration(string goalId, string taskId, WorkSlotPosition position) =>
        LogSafely(() => _logger.LogWarning(
            "WorkSlotIntegrity: abandoned-registration goal={GoalId} task={TaskId} position={Iteration}:{Phase}:{Occurrence} — the dispatch failed before delivery; the slot is released",
            goalId, taskId, position.Iteration, FormatLogValue(position.Phase), position.Occurrence));

    /// <summary>
    /// Logs a failed rollback step. <paramref name="step"/> is one of
    /// <c>abandon</c>, <c>pointer</c>, <c>unregister</c>, <c>unregister-persist</c>.
    /// </summary>
    /// <remarks>
    /// GUARDED (β-PREP-2): the body runs inside <see cref="LogSafely"/>, so a throwing logger is
    /// swallowed and the caller's rollback/cleanup control flow is untouched — the
    /// cleanup-before-log contract.
    /// </remarks>
    private void LogRollbackFailure(string goalId, string taskId, string step, Exception? ex) =>
        LogSafely(() => _logger.LogWarning(
            ex,
            "WorkSlotIntegrity: rollback-failure goal={GoalId} task={TaskId} step={Step} — the rollback step failed; continuing",
            goalId, taskId, step));

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

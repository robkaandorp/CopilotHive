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
using CopilotHive.Persistence;
using CopilotHive.Shared;
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

    /// <summary>
    /// Test seam: invoked between the dispatch's pointer claim and the PersistAdmission call —
    /// the deterministic synchronization point for the admission-escape vectors (the successor's
    /// escape-race test injects the concurrent clear here). Null in production: a no-op.
    /// </summary>
    internal Action<GoalPipeline, string>? AdmissionGateForTest;

    /// <summary>
    /// THE STORED-CREDENTIAL LOOKUP SEAM: the live, asynchronous stored admin OAuth token
    /// lookup (production: <c>UserService.GetActiveAccessTokenAsync</c>). Null when no user
    /// service is available — the dispatch then resolves the environment chain alone.
    /// </summary>
    private readonly Func<CancellationToken, Task<string?>>? _storedCredentialLookup;

    /// <summary>
    /// THE FIXED, CREDENTIAL-FREE DIAGNOSTIC for a failed stored-OAuth lookup. The lookup's own
    /// exception message is deliberately NEVER rendered: it can quote the credential it failed to
    /// hand back (a connection string, a token echoed in a provider error), and this record goes
    /// to the ordinary orchestrator log.
    /// </summary>
    internal const string StoredCredentialLookupFailedTemplate =
        "Stored OAuth credential lookup failed for goal {GoalId} (exceptionType={ExceptionType}); falling back to the environment credential chain — the lookup's own message is withheld because it can carry the credential";

    /// <summary>
    /// THE FIXED, CREDENTIAL-FREE CANCELLATION MESSAGE for the assignment-credential boundary.
    /// </summary>
    /// <remarks>
    /// A cancellation raised by (or observed around) the stored-credential lookup must never
    /// carry the provider's own message or inner exception: downstream sinks RENDER them —
    /// <c>PipelineDriver</c>'s improve-phase catch logs <c>ex.Message</c> and copies it into the
    /// phase verdict AND the goal-update notes — so a provider message quoting a connection
    /// string or an echoed token would be persisted in plain sight. This message is a constant,
    /// so it can never interpolate anything.
    /// </remarks>
    internal const string StoredCredentialLookupCancelledMessage =
        "The dispatch was cancelled while resolving the assignment credential; no work was admitted.";

    public TaskDispatchService(
        TaskQueue taskQueue,
        IWorkerGateway workerGateway,
        TaskBuilder taskBuilder,
        HiveConfigFile? config,
        ILogger<TaskDispatchService> logger,
        GoalPipelineManager pipelineManager,
        GoalLifecycleService lifecycleService,
        DispatcherMaintenance maintenance,
        Func<CancellationToken, Task<string?>>? storedCredentialLookup = null)
    {
        _taskQueue = taskQueue;
        _workerGateway = workerGateway;
        _taskBuilder = taskBuilder;
        _config = config;
        _logger = logger;
        _pipelineManager = pipelineManager;
        _lifecycleService = lifecycleService;
        _maintenance = maintenance;
        _storedCredentialLookup = storedCredentialLookup;
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

        // PHASE 1b — THE REPOSITORY CONFIGURATION, resolved FIRST. The AUTHORITATIVE configured
        // URLs are what the credential injection below rebuilds from, so a previously tokenized
        // URL can never be appended to: every dispatch starts from hive-config.yaml's value.
        List<RepositoryConfig> repositoryConfigs;
        try
        {
            repositoryConfigs = ResolveRepositoryConfigs(pipeline.Goal);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Repository configuration error for goal {GoalId}", pipeline.GoalId);
            await _lifecycleService.MarkGoalFailedAsync(pipeline, ex.Message, ct);
            return;
        }

        // PHASE 1c — THE ASSIGNMENT CREDENTIAL, resolved ONCE per assignment and STILL inside the
        // preparation: no work slot is captured, no pointer is claimed, nothing is admitted or
        // enqueued yet, so a cancellation observed here leaves NOTHING to roll back.
        var credential = await ResolveAssignmentCredentialAsync(pipeline.GoalId, ct);

        // The assignment's OWN repository list. HiveConfigFile's RepositoryConfig instances are
        // never written back to — the credential lives only on these freshly allocated objects.
        var repositories = BuildAssignmentRepositories(repositoryConfigs, credential);

        // PHASE 2 of the preparation.
        var tail = BuildDispatchTail(head);

        // ══════════════════════════════════════════════════════════════════════════════════
        //  THE ADMISSION TRANSACTION. Everything above is PREPARATION and touches no shared
        //  state; from here on the dispatch owns a work slot, a task→goal mapping and the
        //  pipeline's active-task pointer (in memory AND, when a store exists, persisted), and
        //  every failure vector releases them again.
        //
        //  THE ORDER: (1) capture → (2) build → (3) the atomic pointer claim → (4) the
        //  admission commit (mapping row + pointer row, one transaction) → (5) enqueue. The
        //  claim is the ordering point: two overlapping dispatches cannot both hold it.
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
        var compactionModel = _config?.GetCompactionModel();
        if (!string.IsNullOrEmpty(compactionModel))
        {
            var compactionCtx = _config?.TryGetContextWindowForModel(compactionModel);

            task.Metadata["compaction_model"] = compactionModel;
            if (compactionCtx is int ctx && ctx > 0)
                task.Metadata["compaction_max_tokens"] = ctx.ToString();
        }

        // (3) THE CLAIM. The atomic active-task claim comes FIRST and is the ONE ordering
        // point of the admission: TrySetActiveTask assigns the pointer IFF it is currently
        // null, so two overlapping dispatches for the same pipeline can never both proceed —
        // exactly one claims, the other is REFUSED here and releases its captured slot.
        //
        // The claim deliberately happens AFTER the capture (1) and the build (2): a refusal
        // must release a slot this dispatch already owns, which is only possible once the
        // slot exists.
        if (!pipeline.TrySetActiveTask(taskId, task.BranchInfo?.FeatureBranch))
        {
            pipeline.AbandonSlot(taskId);
            LogAbandonedRegistration(pipeline.GoalId, taskId, slot.Position);
            throw new InvalidOperationException(
                $"Task mapping registration failed for {taskId} (goal {pipeline.GoalId}) — the pipeline already has an active task (an overlapping dispatch refused)");
        }

        // THE TEST SEAM, deliberately OUTSIDE every try block: it is the deterministic
        // synchronization point between the claim and the admission, never a failure
        // injector. Because it sits outside the try, a throw from here would NOT be caught by
        // the admission's catch at all — it would escape past PersistAdmission and its entire
        // cleanup (no slot release, no unregister, no pointer clear), leaving the claim
        // stranded. That is why a sentinel must never be thrown from the gate. The
        // escaped-validation flow is instead reached by having the gate MUTATE state (clearing
        // the pointer), so PersistAdmission's own argument validation throws from INSIDE the
        // try and the catch below runs.
        AdmissionGateForTest?.Invoke(pipeline, taskId);

        // (4) THE ADMISSION. PersistAdmission is the exclusive writer of the task→goal
        // mapping on this path: it takes the in-memory claim and — when a store exists —
        // commits the task_mappings row AND the pipelines row's active_task_id pointer in
        // ONE database transaction, using the pointer snapshot validated at claim time.
        //
        // THE ACCEPTED CoderBranch RESIDUE. A successful TrySetActiveTask may have assigned
        // CoderBranch (first-assignment semantics) even when the admission below refuses and
        // the pointer is cleared again. That residue is INTENTIONALLY LEFT IN PLACE:
        // BranchCoordinator.GetFeatureBranch derives the branch deterministically as
        // copilothive/{goalId} and every later attempt for the SAME goal derives the SAME
        // value, so the retained branch is already correct — not stale. Restoring it would
        // add a mutation (and a race against a concurrent dispatch) for no benefit.
        AdmissionCommitResult admission;
        try
        {
            admission = _pipelineManager.PersistAdmission(pipeline, taskId);
        }
        catch (Exception)
        {
            // An ESCAPED VALIDATION (the pointer moved between the claim and the call, so the
            // active-task equality check threw) — release everything we took, then rethrow the
            // ORIGINAL exception BARE: never wrapped, never reconstructed.
            pipeline.AbandonSlot(taskId);
            _pipelineManager.TryUnregisterTask(taskId, pipeline.GoalId);
            pipeline.ClearActiveTaskIfCurrent(taskId);
            LogAbandonedRegistration(pipeline.GoalId, taskId, slot.Position);
            throw;
        }

        if (admission.Status is AdmissionCommitStatus.Committed or AdmissionCommitStatus.NoStore)
        {
            // ADMITTED — the claim stands (with a committed row, or in memory alone when no
            // store is configured). Fall through to the enqueue step (5).
        }
        else
        {
            // REFUSED (MemoryConflict / PersistConflict / PersistenceFailed) — the exact
            // R1–R5 rollback sequence.
            pipeline.AbandonSlot(taskId);                                    // R1

            // R2 — remove the mapping ONLY when THIS invocation claimed it. On MemoryConflict
            // a pre-existing claim (identical or foreign) is NOT ours to remove; on
            // PersistConflict/PersistenceFailed the admission already rolled our claim back,
            // so the ownership-checked unregister is a no-op.
            if (admission.ClaimedThisInvocation)
                _pipelineManager.TryUnregisterTask(taskId, pipeline.GoalId);

            pipeline.ClearActiveTaskIfCurrent(taskId);                        // R3
            LogAbandonedRegistration(pipeline.GoalId, taskId, slot.Position); // R4

            // R5 — the refusal causes stay DISTINGUISHABLE at the exception level: a
            // persistence failure carries the store's original exception as the inner
            // exception; every other refusal status has a NULL inner exception.
            throw new InvalidOperationException(
                $"Task mapping registration failed for {taskId} (goal {pipeline.GoalId}) — the mapping is occupied or the persistence failed",
                admission.Status == AdmissionCommitStatus.PersistenceFailed ? admission.PersistenceException : null);
        }

        // (5) THE ENQUEUE. The catch spans the TaskQueue.Enqueue call ONLY — the direct-push
        // path below stays OUTSIDE it, because the delivery transaction (a later goal) owns
        // that path and must not be pre-empted by this rollback.
        //
        // THE ORPHAN EDGE (accepted trade). An insert-then-throw inside Enqueue may leave the
        // task admitted to the queue while this rollback unregisters its mapping and rolls the
        // persisted pointer back. The later assignment then finds no pipeline for the task and
        // hits the existing no-pipeline drop — an orphaned queue entry, never a double-assigned
        // slot.
        try
        {
            _taskQueue.Enqueue(task);
        }
        catch (Exception)
        {
            // THE BEST-EFFORT ROLLBACK — THE ORIGINAL enqueue exception is what leaves this catch
            // on EVERY path below: bare, never wrapped and never aggregated with rollback evidence.
            //
            // THE ROUTE DECISION is made from THE EVIDENCE OF THIS ADMISSION, never from a fresh
            // eligibility guess: a CONFIRMED successful eligible admission carries its
            // invocation-local RollbackEvidence, and only such an admission takes the guarded
            // atomic-inverse route. Every other admission (the legacy/ineligible route, NoStore,
            // and every refusal/failure — where there is either nothing persisted or the admission
            // already rolled its own claim back) keeps the pre-existing sequence byte-identically.
            //
            // THE WHOLE NEW ROUTE — the manager invocation AND every diagnostic — is guarded, so a
            // throwing logger, or any unexpected escape from the manager operation, can neither
            // replace the original exception nor skip the remaining settlement.
            //
            // THE SLOT-RELEASE TRUTH travels back from the route: the legacy sequence ALWAYS
            // releases the slot (its AbandonSlot runs unconditionally), while the evidence route
            // releases it only when the Pending fence actually succeeded. A SKIPPED rollback
            // deliberately mutates NOTHING, so claiming "the slot is released" for it would be a
            // false diagnostic.
            var slotReleased = true;
            if (admission.RollbackEvidence is not null)
            {
                slotReleased = TryRollbackPendingAdmission(pipeline, taskId, admission.RollbackEvidence);
            }
            else
            {
                // ── THE PRE-EXISTING SEQUENCE for every admission without evidence ──

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

                // (b2) Roll the PERSISTED pointer back — ONLY when THIS dispatch committed it.
                // On NoStore nothing was persisted, so there is nothing to undo. The store's
                // clear is ownership-checked (a newer pointer is never erased) and never throws;
                // a Failed outcome is recorded as a rollback failure and the cleanup continues.
                if (admission.CommittedThisInvocation)
                {
                    var pointerRollback = _pipelineManager.RollbackPersistedPointer(pipeline, taskId);
                    if (pointerRollback == PointerRollbackResult.Failed)
                        LogRollbackFailure(pipeline.GoalId, taskId, "pointer-rollback", null);
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
            }

            // (d) The slot-release record — emitted ONLY when the slot really was released, so a
            // SKIPPED rollback (which mutated nothing) never carries a false "the slot is released"
            // claim next to its own outcome=skipped record. Then (e) the ORIGINAL failure, bare.
            if (slotReleased)
                LogAbandonedRegistration(pipeline.GoalId, taskId, slot.Position);
            throw;
        }
        _logger.LogInformation("Dispatched {Role} task {TaskId} for goal {GoalId} (branch={Branch})",
            role, task.TaskId, pipeline.GoalId, task.BranchInfo?.FeatureBranch);

        // ══════════════════════════════════════════════════════════════════════════════════
        //  THE EAGER DELIVERY, ON THE MERGED CHECKED CLAIM. The stages, in this exact order:
        //
        //    select      GetIdleWorker — a CANDIDATE, not a reservation. Nothing is touched yet,
        //                so every throw (ordinary or cancellation) PROPAGATES UNCAUGHT.
        //    dequeue     TryDequeue(role) ?? TryDequeueAny() — no throw path; a null result is the
        //                honest no-op. The result is THE ACTUAL queued task and may belong to
        //                ANOTHER pipeline (the role-aware FIFO), so EVERY recovery below acts on
        //                `queuedTask` alone — never on the task this dispatch just admitted.
        //    cancel      The caller's token, observed BEFORE the claim: the task is out of the
        //                queue but nothing is owned, so it goes back EXACTLY ONCE.
        //    claim       WorkerPool.TryClaimAndActivate through the gateway — the SINGLE
        //                publication point: the queue activation, the busy fields, the Role and
        //                the CurrentModel are published together, or nothing is. A `false` is a
        //                CONFIRMED no-mutation refusal; a THROW proves nothing either way.
        //    guidance    POST-CLAIM and BEST-EFFORT: it may never undo the claim or requeue.
        //    recheck     The caller token, observed explicitly before publication.
        //    publish     The recorded publication to the PINNED instance. A REPORTED refusal
        //                (WorkerTaskSendOutcome.Blocked) is NOT a failure: it retains everything
        //                and emits no success record.
        //
        //  NOTHING AWAITS DELIVERY AND NOTHING WRITES Role/CurrentModel BEFORE THE CLAIM, so a
        //  losing candidate never overwrites newer ownership.
        // ══════════════════════════════════════════════════════════════════════════════════

        // SELECT. Nothing has been dequeued yet, so this call is deliberately NOT guarded.
        var idleWorker = _workerGateway.GetIdleWorker();
        if (idleWorker is null)
            return;

        // DEQUEUE. The role-aware dequeue first, then the role-agnostic fallback. Neither throws;
        // a null result simply means there is nothing to push right now.
        var queuedTask = _taskQueue.TryDequeue(role) ?? _taskQueue.TryDequeueAny();
        if (queuedTask is null)
            return;

        // THE CAPTURE — everything the recoveries and the records need, read ONCE from THE ACTUAL
        // dequeued task. `deliveredGoalId` is the DELIVERED task's goal (the log owner);
        // `registeredTaskId` is the task THIS dispatch admitted.
        var deliveryWorkerId = idleWorker.Id;
        var deliveredTaskId = queuedTask.TaskId;
        var deliveredRole = queuedTask.Role;
        var deliveredGoalId = queuedTask.GoalId;
        var registeredTaskId = task.TaskId;

        // ── THE POST-DEQUEUE LOGGING BOUNDARY ──────────────────────────────────────────────
        // From here on EVERY log call inside this transaction goes through a logging guard: a
        // diagnostic failure must NEVER strand a dequeued task. The dequeue is the boundary.
        // ───────────────────────────────────────────────────────────────────────────────────

        if (!string.Equals(deliveredTaskId, registeredTaskId, StringComparison.Ordinal))
            LogDeliveryMismatch(deliveredGoalId, registeredTaskId, deliveredTaskId);

        // CANCEL — THE ONE REQUEUE POINT. The task is out of the queue, NOT activated, and no
        // ownership has been taken: returning it to the pending queue is provably safe here, and
        // ONLY here. No worker field, no metadata, no admission state is touched.
        if (ct.IsCancellationRequested)
        {
            // (1) THE SINGLE INSERT. TaskQueue.Enqueue inserts BEFORE invoking its OnEnqueue hook,
            // so a hook fault is POST-INSERT: the task really is pending again and a retry would
            // duplicate it. EVERY hook fault is CONTAINED — an OperationCanceledException included,
            // because a hook's exception (and its foreign token) must never become this path's
            // outcome.
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

            // (2) THE GUARD LINE — emitted IFF the Enqueue call RETURNED NORMALLY, so its
            // "completed through the re-enqueue" wording is always literally true. A faulting hook
            // has the delivery-rollback-failure record above instead.
            if (reEnqueued)
                LogDeliveryRecovery(deliveredGoalId, deliveredTaskId);

            // (3) The failure record, then (4) THE CALLER'S CANCELLATION, carrying the CALLER
            // token: only the token STATE was observed here, so the outcome is constructed from
            // that token rather than borrowed from a recovery hook.
            LogDeliveryFailure(
                deliveredGoalId, deliveredTaskId, deliveryWorkerId,
                DeliveryStage.CancelCheck, DeliveryRecovery.Requeue);
            throw new OperationCanceledException(ct);
        }

        // CLAIM — THE SINGLE PUBLICATION POINT, taken on the EXACT selected instance and the ACTUAL
        // dequeued task. A THROW from the interface is NOT a refusal: whether anything was mutated
        // is unknowable, so it is preserved and rethrown UNCHANGED with NO requeue and NO
        // diagnostic that claims the activation happened.
        bool claimed;
        try
        {
            claimed = _workerGateway.TryClaimAndActivate(idleWorker, queuedTask, _taskQueue);
        }
        catch (Exception)
        {
            LogDeliveryClaimThrew(deliveredGoalId, deliveredTaskId, deliveryWorkerId);
            throw;
        }

        if (!claimed)
        {
            // REFUSED — a CONFIRMED no-mutation outcome: the instance is gone or replaced (ABA), is
            // no longer idle with a null task, or is still holding its completion publication. The
            // ACTUAL dequeued task goes back EXACTLY ONCE and this delivery ends NORMALLY: no
            // guidance, no assignment, no role/model write, and no admission, mapping or slot
            // mutation of any kind.
            //
            // THE SINGLE INSERT IS THE WHOLE RECOVERY: Enqueue inserts BEFORE its OnEnqueue hook, so
            // a hook exception propagates UNCHANGED after the insert and is deliberately NOT retried.
            _taskQueue.Enqueue(queuedTask);
            LogDeliveryClaimRefused(deliveredGoalId, deliveredTaskId, deliveryWorkerId);
            return;
        }

        // The claim published the worker's Role and CurrentModel itself; this record merely reports
        // it.
        var taskRoleName = deliveredRole.ToRoleName();
        LogSafely(() => _logger.LogInformation("Worker {WorkerId} assigned role {Role} for task {TaskId}",
            deliveryWorkerId, taskRoleName, deliveredTaskId));

        // GUIDANCE — POST-CLAIM AND BEST-EFFORT. The assignment is already claimed, so an ordinary
        // guidance failure may never undo it, requeue anything or restore a field; it is recorded in
        // a guarded diagnostic and the delivery continues. An ACTUAL CAUGHT CALLER cancellation is
        // the one exception and propagates unchanged — and it must really be the caller's: the
        // filter demands the exception's OWN token be the caller's AND that token be cancelled, so
        // a logger-thrown OperationCanceledException carrying a foreign or default token stays a
        // best-effort failure and never becomes caller-cancellation evidence. The explicit token
        // observation below remains the authority either way.
        try
        {
            await _maintenance.SendAgentsMdToWorkerAsync(idleWorker, deliveredRole, ct);
        }
        catch (OperationCanceledException cancellation)
            when (ct.IsCancellationRequested && cancellation.CancellationToken == ct)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogGuidanceBestEffortFailed(deliveredGoalId, deliveredTaskId, deliveryWorkerId, ex);
        }

        // THE RECHECK. A helper that swallowed the caller's cancellation internally must not turn a
        // cancelled delivery into a published assignment. Post-claim cancellation NEVER requeues,
        // restores or clears ownership.
        ct.ThrowIfCancellationRequested();

        // PUBLISH — the recorded publication to the PINNED instance, never an ID-resolved
        // replacement. A THROW is the ambiguity point: undoing anything here could deliver the same
        // task twice, so the record fires and the ORIGINAL exception rethrows.
        //
        // THE PUBLISHER'S REFUSAL IS NOT A FAILURE. On WorkerTaskSendOutcome.Blocked the assignment
        // was deliberately NOT published; the pinned worker, the active queue entry, the pointer,
        // the Pending slot, the mapping, the worker's busy/role/model state and the stored row are
        // ALL retained, this method returns NORMALLY, and the gateway emits the disposition record.
        WorkerTaskSendOutcome sendOutcome;
        try
        {
            sendOutcome = await _workerGateway.SendTaskAsync(idleWorker, queuedTask, ct);
        }
        catch (Exception)
        {
            LogDeliveryFailure(
                deliveredGoalId, deliveredTaskId, deliveryWorkerId,
                DeliveryStage.Send, DeliveryRecovery.Preserve);
            throw;
        }

        switch (sendOutcome)
        {
            case WorkerTaskSendOutcome.Blocked:
                // RETURN NORMALLY, retaining everything and emitting NO publication-success record.
                return;

            case WorkerTaskSendOutcome.Published:
                break;

            default:
                // NO SILENT FALLBACK: an undefined outcome is a contract violation, and it must
                // never be mistaken for a successful publication.
                throw new InvalidOperationException($"Unhandled WorkerTaskSendOutcome: {sendOutcome}");
        }

        // THE PUBLICATION-SUCCESS RECORD — reached ONLY for WorkerTaskSendOutcome.Published. It
        // reports CHANNEL PUBLICATION, never confirmed receipt: the worker may never consume the
        // message.
        LogSafely(() => _logger.LogInformation(
            "Task {TaskId} pushed to worker {WorkerId}", deliveredTaskId, deliveryWorkerId));
    }

    /// <summary>The delivery stages that can appear in a <c>delivery-failure</c> record.</summary>
    private enum DeliveryStage
    {
        /// <summary>The pre-claim cancellation observation — the ONE requeue point.</summary>
        CancelCheck,

        /// <summary>
        /// The SendTaskAsync stage. Its THROW is the ambiguity-preserve; a returned
        /// <see cref="WorkerTaskSendOutcome.Blocked"/> is a reported refusal that retains
        /// everything and emits no <c>pushed to worker</c> record.
        /// </summary>
        Send,
    }

    /// <summary>The recovery classifications a <c>delivery-failure</c> record can report.</summary>
    private enum DeliveryRecovery
    {
        /// <summary>The task was returned to the pending queue (the pre-claim cancel only).</summary>
        Requeue,

        /// <summary>No recovery was attempted: the outcome is unknowable (the send).</summary>
        Preserve,
    }

    /// <summary>Renders a <see cref="DeliveryStage"/> as its template token.</summary>
    private static string RenderStage(DeliveryStage stage) => stage switch
    {
        DeliveryStage.CancelCheck => "cancel-check",
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
    /// THE GUARDED, ENQUEUE-ONLY CONSUMPTION of <see cref="GoalPipelineManager.RollbackPendingAdmission"/>:
    /// the eligible admission's atomic inverse plus the honest reporting of its outcome. NEVER
    /// THROWS — every step, including the manager invocation and EVERY diagnostic, is guarded so the
    /// ORIGINAL enqueue exception stays authoritative in the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE MEMORY SETTLEMENT already happened inside the manager operation (the pair-scoped removal
    /// plus <see cref="GoalPipeline.ClearActiveTaskIfCurrent"/>), so nothing here performs memory or
    /// durable cleanup of its own: there is NO durable fallback on refusal, uncertainty or throw —
    /// no separate mapping deletion, no pointer clear, no unconditional save, no read-back repair and
    /// no retry. This helper exists to REPORT the recorded outcome and to keep a diagnostic failure
    /// from ever escaping.
    /// </para>
    /// <para>
    /// HONEST CLASSIFICATION, with no invented outcome framework: a confirmed
    /// <see cref="PendingAdmissionRollbackStatus.Committed"/> is the ONLY outcome logged as a
    /// completed rollback; <c>Skipped</c>, <c>Refused</c>, <c>Indeterminate</c> and <c>Failed</c>
    /// are reported through the existing SAFE logging helpers (<see cref="LogSafely"/>). The store's
    /// exact evidence is reported WITHOUT wrapping or aggregation: the PRIMARY exception instance is
    /// passed to the logger as its exception argument, and — when the store also captured a
    /// ROLLBACK exception (a throwing transaction rollback, which may accompany a null primary) —
    /// a SECOND, independently guarded record carries THAT exact instance as its own exception
    /// argument. Two instances need two records because one <see cref="ILogger"/> call can carry
    /// only one; neither is ever wrapped, chained into an <c>InnerException</c>, aggregated, or
    /// combined with the caller's original enqueue exception, which stays the only exception that
    /// leaves the dispatch.
    /// </para>
    /// <para>
    /// THE RETURN VALUE is the SLOT-RELEASE TRUTH the caller's closing record depends on:
    /// <c>true</c> only when the Pending fence actually succeeded and the settlement ran (every
    /// outcome except <see cref="PendingAdmissionRollbackStatus.Skipped"/>). A skipped attempt
    /// mutated nothing, so the caller must NOT claim the slot was released. A contained escape
    /// reports <c>false</c> for the same reason: the fence cannot be assumed to have run.
    /// </para>
    /// </remarks>
    /// <param name="pipeline">The pipeline the admission was made for.</param>
    /// <param name="taskId">The task the admission was made for.</param>
    /// <param name="evidence">The invocation-local evidence of THAT admission.</param>
    /// <returns><c>true</c> when the Pending fence succeeded (the slot really was released).</returns>
    private bool TryRollbackPendingAdmission(
        GoalPipeline pipeline, string taskId, AdmissionRollbackEvidence evidence)
    {
        // THE OUTER GUARD — the LAST line of defence for the caller's exception story: nothing this
        // helper does (the manager invocation, the outcome switch, any diagnostic) may escape and
        // replace the ORIGINAL enqueue exception. The memory settlement happens inside the manager
        // operation and is idempotent, so an escape here needs no compensating cleanup.
        try
        {
            PendingAdmissionRollbackResult rollback;
            try
            {
                rollback = _pipelineManager.RollbackPendingAdmission(pipeline, taskId, evidence);
            }
            catch (Exception ex)
            {
                // The manager operation reports its store failures through its result and is not
                // expected to throw; an escape is contained here. The fence's fate is unknown, so
                // no slot-release claim is made.
                LogSafely(() => _logger.LogWarning(
                    ex,
                    "WorkSlotIntegrity: rollback-failure goal={GoalId} task={TaskId} step=pending-admission — the rollback operation threw; continuing",
                    pipeline.GoalId, taskId));
                return false;
            }

            switch (rollback.Status)
            {
                case PendingAdmissionRollbackStatus.Committed:
                    // THE ONLY outcome reported as a completed rollback: the store's transaction
                    // confirmed the Pending abandon, the pointer clear and the mapping delete together.
                    LogSafely(() => _logger.LogDebug(
                        "WorkSlotIntegrity: pending-admission-rollback goal={GoalId} task={TaskId} outcome=committed — the persisted Pending admission was abandoned, its pointer cleared and its mapping deleted in one transaction",
                        pipeline.GoalId, taskId));
                    return true;

                case PendingAdmissionRollbackStatus.Skipped:
                    // NOT a failure and NOT a success: the attempt was never made (the manager no
                    // longer owned the admission, or the slot was no longer Pending). NOTHING was
                    // mutated, so the caller must emit NO slot-release record for this path.
                    LogSafely(() => _logger.LogDebug(
                        "WorkSlotIntegrity: pending-admission-rollback goal={GoalId} task={TaskId} outcome=skipped — the admission was no longer this manager's to roll back (the slot was absent, claimed, recorded or already abandoned); no rollback was attempted",
                        pipeline.GoalId, taskId));
                    return false;

                case PendingAdmissionRollbackStatus.Refused:
                    // A confirmed NO-MUTATION outcome — which does NOT prove that earlier cleanup
                    // already happened. Reported honestly rather than as success.
                    LogSafely(() => _logger.LogWarning(
                        "WorkSlotIntegrity: pending-admission-rollback goal={GoalId} task={TaskId} outcome=refused — the rollback transaction confirmed no mutation (a missing row, a newer or null pointer, stale registry text, or a missing/foreign mapping); durable residue may remain",
                        pipeline.GoalId, taskId));
                    return true;

                case PendingAdmissionRollbackStatus.Indeterminate:
                    // MAY HAVE COMMITTED: neither success nor no-change is inferred. BOTH of the
                    // store's exact instances must survive to the diagnostic surface, and ONE
                    // ILogger call can carry only ONE exception argument — so the two truths are
                    // emitted as TWO independently guarded records, never wrapped, chained or
                    // aggregated (least of all with the caller's original enqueue exception).
                    //
                    // (1) THE OUTCOME RECORD carries the PRIMARY instance verbatim. Its exception
                    //     argument is null EXACTLY when there was no primary failure — a throwing
                    //     transaction rollback can accompany a clean body refusal — and the inline
                    //     type/message rendering keeps that case readable at a glance.
                    LogSafely(() => _logger.LogWarning(
                        rollback.Failure,
                        "WorkSlotIntegrity: pending-admission-rollback goal={GoalId} task={TaskId} outcome=indeterminate — the rollback transaction's fate is unknown; it may have committed (primary={Primary}; rollback={Rollback})",
                        pipeline.GoalId, taskId,
                        DescribeRollbackEvidence(rollback.Failure),
                        DescribeRollbackEvidence(rollback.RollbackFailure)));

                    // (2) THE ROLLBACK-EVIDENCE RECORD, emitted ONLY when the store actually
                    //     captured a rollback exception, carries THAT exact instance as its own
                    //     exception argument. Its own guard keeps a throwing logger here from
                    //     masking either the record above or the original enqueue exception.
                    if (rollback.RollbackFailure is { } rollbackFailure)
                    {
                        LogSafely(() => _logger.LogWarning(
                            rollbackFailure,
                            "WorkSlotIntegrity: pending-admission-rollback goal={GoalId} task={TaskId} step=transaction-rollback — the rollback transaction's own rollback threw; this record retains that exact exception",
                            pipeline.GoalId, taskId));
                    }

                    return true;

                case PendingAdmissionRollbackStatus.Failed:
                    // THE STORE THREW — distinguishable from a skipped attempt. The exact exception
                    // is the evidence argument; the local settlement already completed in the manager.
                    LogSafely(() => _logger.LogWarning(
                        rollback.Failure,
                        "WorkSlotIntegrity: pending-admission-rollback goal={GoalId} task={TaskId} outcome=failed — the rollback store call threw; durable residue may remain",
                        pipeline.GoalId, taskId));
                    return true;

                default:
                    // NO SILENT FALLBACK: an undefined outcome is a contract violation, and
                    // reporting it as anything else would be a false claim about durable state.
                    throw new InvalidOperationException(
                        $"Unhandled PendingAdmissionRollbackStatus: {rollback.Status}");
            }
        }
        catch (Exception ex)
        {
            // The outer guard: the switch's contract-violation throw (and anything else) is
            // contained so the caller's ORIGINAL enqueue exception remains authoritative. The
            // fence's fate is unknown here, so no slot-release claim is made.
            LogSafely(() => _logger.LogWarning(
                ex,
                "WorkSlotIntegrity: rollback-failure goal={GoalId} task={TaskId} step=pending-admission — the rollback reporting failed; continuing",
                pipeline.GoalId, taskId));
            return false;
        }
    }

    /// <summary>
    /// Renders ONE exception instance as a SAFE, bounded description (type plus message) for the
    /// indeterminate outcome record — never a stack dump, never a wrapper and never an aggregate.
    /// </summary>
    /// <remarks>
    /// THIS IS READABILITY, NOT EVIDENCE RETENTION. The exact instances are retained by the
    /// records' own exception arguments (the outcome record carries the primary; a second guarded
    /// record carries the rollback exception); this rendering merely puts both truths on ONE line
    /// so the uncertain outcome is legible without correlating records.
    /// <para>
    /// <see cref="Exception.Message"/> is a virtual property that can itself THROW, so the read is
    /// guarded and degrades to a static placeholder: a diagnostic must never become the reason a
    /// rollback report fails. The exception instance itself is never re-created or re-thrown —
    /// this is a description of evidence, not a replacement for it.
    /// </para>
    /// </remarks>
    /// <param name="exception">The exact instance to describe, or <c>null</c> when there was none.</param>
    /// <returns>A short <c>Type: message</c> description, or <c>"none"</c> for a null instance.</returns>
    private static string DescribeRollbackEvidence(Exception? exception)
    {
        if (exception is null)
            return "none";

        try
        {
            return $"{exception.GetType().Name}: {exception.Message}";
        }
        catch (Exception describeEx)
        {
            return $"{exception.GetType().Name}: <message getter threw: {describeEx.GetType().Name}>";
        }
    }

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
    /// Logs a failed DELIVERY rollback step. <paramref name="step"/> is <c>re-enqueue</c> — the
    /// delivery's only operational recovery step. Distinct from the admission's
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
    /// Logs a REFUSED checked claim: a CONFIRMED no-mutation outcome, so the ACTUAL dequeued task
    /// went back to the queue exactly once and nothing was published.
    /// </summary>
    private void LogDeliveryClaimRefused(string goalId, string taskId, string workerId) =>
        LogSafely(() => _logger.LogWarning(
            "WorkSlotIntegrity: delivery-claim-refused goal={GoalId} task={TaskId} worker={WorkerId} — the worker's ownership changed before the claim, or its completion publication is still in flight; no assignment was published and the task was requeued exactly once",
            goalId, taskId, workerId));

    /// <summary>
    /// Logs a THROWING checked claim. Nothing is asserted about what was mutated — in particular
    /// NOT that the activation happened — and the original exception is rethrown by the caller.
    /// </summary>
    private void LogDeliveryClaimThrew(string goalId, string taskId, string workerId) =>
        LogSafely(() => _logger.LogWarning(
            "WorkSlotIntegrity: delivery-claim-threw goal={GoalId} task={TaskId} worker={WorkerId} — the claim call threw; whether anything was mutated is unknowable, so the task was NOT requeued and the failure propagates",
            goalId, taskId, workerId));

    /// <summary>
    /// Logs a POST-CLAIM guidance failure. The assignment is already claimed and is retained: this
    /// records a degraded, best-effort step and can neither undo the claim nor cause a requeue.
    /// </summary>
    private void LogGuidanceBestEffortFailed(string goalId, string taskId, string workerId, Exception ex) =>
        LogSafely(() => _logger.LogWarning(
            ex,
            "WorkSlotIntegrity: delivery-guidance-failed goal={GoalId} task={TaskId} worker={WorkerId} — the agents.md update failed after the assignment was claimed; continuing to publish the claimed assignment",
            goalId, taskId, workerId));

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
        LogSafely(() => _logger.LogWarning(
            "WorkSlotIntegrity: abandoned-registration goal={GoalId} task={TaskId} position={Iteration}:{Phase}:{Occurrence} — the dispatch failed before delivery; the slot is released",
            goalId, taskId, position.Iteration, FormatLogValue(position.Phase), position.Occurrence));

    /// <summary>
    /// Logs a failed rollback step. <paramref name="step"/> is one of
    /// <c>abandon</c>, <c>pointer</c>, <c>pointer-rollback</c>, <c>unregister</c>,
    /// <c>unregister-persist</c>.
    /// </summary>
    private void LogRollbackFailure(string goalId, string taskId, string step, Exception? ex) =>
        LogSafely(() => _logger.LogWarning(
            ex,
            "WorkSlotIntegrity: rollback-failure goal={GoalId} task={TaskId} step={Step} — the rollback step failed; continuing",
            goalId, taskId, step));

    /// <summary>
    /// Resolves the list of <see cref="TargetRepository"/> instances for the given goal by looking
    /// up each repository name in the hive configuration.
    /// </summary>
    /// <remarks>
    /// THE SYNCHRONOUS LEGACY CONTRACT, unchanged: the pipeline-metadata delegates
    /// (<c>PipelineDriver.resolveRepositories</c> and friends) are synchronous and must never
    /// block on the asynchronous stored-OAuth lookup, so this path keeps the ENVIRONMENT-only
    /// fallback of <see cref="PipelineHelpers.InjectTokenIntoUrl(string)"/>. The DISPATCH path
    /// does NOT use it — see <see cref="ResolveAssignmentCredentialAsync"/>.
    /// </remarks>
    /// <param name="goal">The goal whose <see cref="Goal.RepositoryNames"/> are to be resolved.</param>
    /// <returns>A list of resolved <see cref="TargetRepository"/> objects with injected credentials.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when any repository name referenced by the goal is not defined in hive-config.yaml.
    /// </exception>
    internal List<TargetRepository> ResolveRepositories(Goal goal)
    {
        var repos = new List<TargetRepository>();

        foreach (var repoConfig in ResolveRepositoryConfigs(goal))
        {
            var url = PipelineHelpers.InjectTokenIntoUrl(repoConfig.Url);
            repos.Add(new TargetRepository
            {
                Name = repoConfig.Name,
                Url = url,
                DefaultBranch = repoConfig.DefaultBranch,
            });
        }

        return repos;
    }

    /// <summary>
    /// Resolves each of the goal's repository names to its AUTHORITATIVE
    /// <see cref="RepositoryConfig"/> from hive-config.yaml, in the goal's own order.
    /// </summary>
    /// <remarks>
    /// The returned instances are the CONFIGURATION's own objects and are never mutated by any
    /// caller: the credential injection always allocates fresh assignment data.
    /// </remarks>
    /// <param name="goal">The goal whose repository names are resolved.</param>
    /// <returns>The matching configuration entries.</returns>
    /// <exception cref="InvalidOperationException">A referenced repository is not configured.</exception>
    private List<RepositoryConfig> ResolveRepositoryConfigs(Goal goal)
    {
        var configs = new List<RepositoryConfig>();

        foreach (var repoName in goal.RepositoryNames)
        {
            var repoConfig = _config?.Repositories.FirstOrDefault(
                r => r.Name.Equals(repoName, StringComparison.OrdinalIgnoreCase));

            if (repoConfig is null)
            {
                throw new InvalidOperationException(
                    $"Goal '{goal.Id}' references repository '{repoName}' which is not defined in hive-config.yaml. Add it to the repositories section or remove it from the goal.");
            }

            configs.Add(repoConfig);
        }

        return configs;
    }

    /// <summary>
    /// Builds the assignment's OWN repository list from the authoritative configured URLs,
    /// injecting <paramref name="credential"/> into every eligible one.
    /// </summary>
    /// <remarks>
    /// REBUILD, NEVER APPEND: the injection always starts from
    /// <see cref="RepositoryConfig.Url"/>, so a credential is REPLACED rather than accumulated,
    /// and the configuration objects themselves are never written to.
    /// </remarks>
    /// <param name="repositoryConfigs">The resolved configuration entries.</param>
    /// <param name="credential">The assignment credential, or <c>null</c> when none resolved.</param>
    /// <returns>Freshly allocated target repositories for this assignment only.</returns>
    private static List<TargetRepository> BuildAssignmentRepositories(
        IReadOnlyList<RepositoryConfig> repositoryConfigs, string? credential)
    {
        var repos = new List<TargetRepository>(repositoryConfigs.Count);
        foreach (var repoConfig in repositoryConfigs)
        {
            repos.Add(new TargetRepository
            {
                Name = repoConfig.Name,
                Url = PipelineHelpers.InjectTokenIntoUrl(repoConfig.Url, credential),
                DefaultBranch = repoConfig.DefaultBranch,
            });
        }

        return repos;
    }

    /// <summary>
    /// Resolves THE ASSIGNMENT CREDENTIAL once: the CURRENT stored admin OAuth token (when a
    /// lookup is wired), then <c>GH_TOKEN</c>, then <c>GITHUB_TOKEN</c> — the first non-blank
    /// candidate, selected by <see cref="GitCredentialResolver.Resolve"/> and returned UNCHANGED.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lookup runs on EVERY dispatch (never cached), so a token rotated between two
    /// dispatches is observed by the second one.
    /// </para>
    /// <para>
    /// A CALLER CANCELLATION propagates: this runs entirely inside the preparation, before the
    /// work-slot capture, so nothing has been admitted, claimed or enqueued that would need
    /// rolling back. Any OTHER lookup failure degrades to the environment-only chain and is
    /// recorded with the FIXED, credential-free
    /// <see cref="StoredCredentialLookupFailedTemplate"/> — never the raw exception message.
    /// </para>
    /// <para>
    /// <b>THE THREE CANCELLATION OBSERVATION POINTS</b>, all of them BEFORE the work-slot
    /// capture, so every one of them leaves NOTHING to roll back:
    /// <list type="number">
    /// <item>the ENTRY check — a pre-cancelled caller never even reaches the lookup;</item>
    /// <item>the lookup's OWN cancellation — sanitized (see below) and rethrown;</item>
    /// <item>THE POST-LOOKUP RECHECK — the one that closes the window in which the caller is
    /// cancelled WHILE the lookup runs but the lookup itself neither observes nor reports it
    /// (it returns a token normally, or it faults with a NON-cancellation exception that the
    /// environment fallback swallows). Without it such a dispatch would sail on into the
    /// capture, the claim, the admission and the enqueue.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>THE SANITIZED CANCELLATION BOUNDARY.</b> The lookup's own
    /// <see cref="OperationCanceledException"/> is NEVER rethrown as-is: its message and inner
    /// exception come from the credential provider and can quote the credential (a connection
    /// string, a token echoed by a provider error). Downstream sinks render exactly those —
    /// <c>PipelineDriver</c>'s improve-phase catch logs <c>ex.Message</c> AND copies it into the
    /// phase verdict and the goal-update notes — so a fresh, fixed-message exception carrying
    /// the CALLER'S TOKEN and NO inner exception is raised instead.
    /// </para>
    /// </remarks>
    /// <param name="goalId">The dispatching goal, for the diagnostic.</param>
    /// <param name="ct">The caller's cancellation token.</param>
    /// <returns>The resolved credential, or <c>null</c> when no candidate is present.</returns>
    /// <exception cref="OperationCanceledException">
    /// The caller cancelled before, during or across the lookup. Always credential-free.
    /// </exception>
    private async Task<string?> ResolveAssignmentCredentialAsync(string goalId, CancellationToken ct)
    {
        // (1) THE ENTRY CHECK. Observed here — before the work-slot capture — a cancellation
        // costs nothing: no slot, no mapping, no pointer, no queue entry.
        ThrowSanitizedIfCancelled(ct);

        string? storedToken = null;
        if (_storedCredentialLookup is not null)
        {
            try
            {
                storedToken = await _storedCredentialLookup(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // (2) THE LOOKUP'S OWN CANCELLATION. A CALLER cancellation is not a lookup
                // failure — it propagates, but SANITIZED: the provider's exception is dropped
                // entirely (message AND inner) so nothing credential-bearing can reach a
                // downstream log, verdict or goal note.
                throw NewSanitizedCancellation(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(StoredCredentialLookupFailedTemplate, goalId, ex.GetType().Name);
                storedToken = null;
            }
        }

        // (3) THE POST-LOOKUP RECHECK — reached on BOTH non-cancelling outcomes: the lookup
        // RETURNED NORMALLY, or it FAULTED with a non-cancellation exception and fell back to
        // the environment chain. Either way the caller may have been cancelled while the lookup
        // was in flight, and that cancellation must refuse the dispatch HERE — still before the
        // capture, the claim, the admission and the enqueue.
        ThrowSanitizedIfCancelled(ct);

        return GitCredentialResolver.Resolve(
            storedToken,
            Environment.GetEnvironmentVariable("GH_TOKEN"),
            Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
    }

    /// <summary>
    /// Throws the SANITIZED cancellation when <paramref name="ct"/> is cancelled; otherwise a
    /// no-op. Used instead of <see cref="CancellationToken.ThrowIfCancellationRequested"/> at the
    /// credential boundary so every exception leaving it has ONE fixed, credential-free shape.
    /// </summary>
    /// <param name="ct">The caller's cancellation token.</param>
    private static void ThrowSanitizedIfCancelled(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            throw NewSanitizedCancellation(ct);
    }

    /// <summary>
    /// Builds THE SANITIZED CANCELLATION: a fresh <see cref="OperationCanceledException"/> with
    /// the fixed <see cref="StoredCredentialLookupCancelledMessage"/>, carrying the CALLER'S
    /// token and — deliberately — NO inner exception.
    /// </summary>
    /// <remarks>
    /// Dropping the provider exception is the whole point: retaining it as an inner exception
    /// would re-open the leak, because downstream renderers walk or format the chain.
    /// </remarks>
    /// <param name="ct">The caller's cancellation token, carried on the exception.</param>
    /// <returns>The credential-free cancellation to throw.</returns>
    private static OperationCanceledException NewSanitizedCancellation(CancellationToken ct) =>
        new(StoredCredentialLookupCancelledMessage, ct);
}

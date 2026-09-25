using System.Collections.Concurrent;
using CopilotHive.Agents;
using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Orchestration;
using CopilotHive.Workers;

namespace CopilotHive.Services;

/// <summary>
/// Handles maintenance operations for the dispatcher: agents sync, pipeline restoration,
/// and orphaned session cleanup.
/// Extracted from <see cref="GoalDispatcher"/> — all logic is identical.
/// </summary>
internal sealed class DispatcherMaintenance
{
    private readonly GoalPipelineManager _pipelineManager;
    private readonly GoalManager _goalManager;
    private readonly TaskQueue _taskQueue;
    private readonly IWorkerGateway _workerGateway;
    private readonly IDistributedBrain? _brain;
    private readonly AgentsManager? _agentsManager;
    private readonly ConfigRepoManager? _configRepo;
    private readonly KnowledgeGraph? _knowledgeGraph;
    private readonly ILogger _logger;
    private readonly IGoalStore? _goalStore;
    private readonly IBrainRepoManager? _repoManager;
    private readonly HiveConfigFile? _config;
    private readonly GoalReadyNotifier? _goalReadyNotifier;

    // Mutable state shared with GoalDispatcher via reference
    private readonly ConcurrentQueue<string> _redispatchQueue;

    /// <summary>Tracks when agents were last synced so callers can throttle.</summary>
    public DateTime LastAgentsSync { get; set; } = DateTime.MinValue;

    public DispatcherMaintenance(
        GoalPipelineManager pipelineManager,
        GoalManager goalManager,
        TaskQueue taskQueue,
        IWorkerGateway workerGateway,
        IDistributedBrain? brain,
        AgentsManager? agentsManager,
        ConfigRepoManager? configRepo,
        ConcurrentQueue<string> redispatchQueue,
        ILogger logger,
        KnowledgeGraph? knowledgeGraph = null,
        IGoalStore? goalStore = null,
        IBrainRepoManager? repoManager = null,
        HiveConfigFile? config = null,
        GoalReadyNotifier? goalReadyNotifier = null)
    {
        _pipelineManager = pipelineManager;
        _goalManager = goalManager;
        _taskQueue = taskQueue;
        _workerGateway = workerGateway;
        _brain = brain;
        _agentsManager = agentsManager;
        _configRepo = configRepo;
        _knowledgeGraph = knowledgeGraph;
        _redispatchQueue = redispatchQueue;
        _logger = logger;
        _goalStore = goalStore;
        _repoManager = repoManager;
        _config = config;
        _goalReadyNotifier = goalReadyNotifier;
    }

    /// <summary>
    /// Pulls the latest config repo and broadcasts any AGENTS.md changes to connected workers.
    /// Best-effort: failures are logged but do not block the main dispatch loop.
    /// </summary>
    public async Task SyncAgentsFromConfigRepoAsync(CancellationToken ct)
    {
        if (_configRepo is null || _agentsManager is null) return;

        try
        {
            await _configRepo.SyncRepoAsync(ct);

            foreach (var role in WorkerRoles.AgentRoles)
            {
                var roleName = role.ToRoleName();
                var repoContent = await _configRepo.LoadAgentsMdAsync(role, ct);
                if (string.IsNullOrEmpty(repoContent)) continue;

                var currentContent = _agentsManager.GetAgentsMd(role);
                if (repoContent == currentContent) continue;

                _agentsManager.UpdateAgentsMd(role, repoContent);

                // Broadcast to Docker workers via gRPC
                if (WorkerRoles.BroadcastableRoles.Contains(role))
                {
                    await BroadcastAgentsUpdateAsync(role, repoContent, ct);
                }

                // Inject updated orchestrator instructions into the Brain session
                if (role == WorkerRole.Orchestrator && _brain is not null)
                {
                    await _brain.InjectOrchestratorInstructionsAsync(repoContent, ct);
                }

                _logger.LogInformation("Synced {Role} AGENTS.md from config repo (changed)", roleName);
            }

            // Reload knowledge graph from the (now-synced) config repo
            if (_knowledgeGraph is not null)
            {
                try
                {
                    await _knowledgeGraph.ReloadFromConfigRepoAsync(_configRepo.LocalPath, ct);
                    _logger.LogInformation("Reloaded knowledge graph from config repo");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to reload knowledge graph from config repo — attempting reset");
                    try
                    {
                        await _configRepo.ResetToRemoteAsync(ct);
                        await _knowledgeGraph.ReloadFromConfigRepoAsync(_configRepo.LocalPath, ct);
                        _logger.LogInformation("Reloaded knowledge graph from config repo after reset");
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogError(ex2, "Failed to recover config repo after reset");
                    }
                }
            }

            if (_config is not null && _configRepo is not null)
            {
                var freshConfig = await _configRepo.LoadConfigAsync(ct);
                _config.ReloadFrom(freshConfig);
                _logger.LogInformation("Reloaded hive configuration from config repo");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync agents from config repo");
        }

        LastAgentsSync = DateTime.UtcNow;
    }

    /// <summary>
    /// Sends the role's AGENTS.md to the EXACT supplied worker instance. Best-effort for an
    /// ORDINARY failure; the caller's OWN cancellation is propagated UNCHANGED.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE REFERENCE OVERLOAD IS USED DELIBERATELY: the guidance goes to the instance the caller
    /// selected, never to a replacement an ID lookup might resolve.
    /// </para>
    /// <para>
    /// THE PROPAGATION CONTRACT, which the eager dispatch's post-claim guidance step relies on:
    /// the ONLY exception that can leave the send below is the CALLER'S OWN
    /// <see cref="OperationCanceledException"/> — the EXACT instance the gateway raised, so its
    /// identity survives end-to-end instead of being replaced by a later recheck. Every other
    /// outcome is contained here, THE DIAGNOSTIC INCLUDED: the warning is emitted through a
    /// no-throw guard, so a logger that itself throws (an
    /// <see cref="OperationCanceledException"/> carrying the now-cancelled caller token above all)
    /// can never escape and be mistaken for the caller's cancellation.
    /// </para>
    /// </remarks>
    public async Task SendAgentsMdToWorkerAsync(ConnectedWorker worker, WorkerRole role, CancellationToken ct)
    {
        if (_agentsManager is null) return;
        var content = _agentsManager.GetAgentsMd(role);
        if (string.IsNullOrEmpty(content)) return;

        var roleName = role.ToRoleName();
        try
        {
            await _workerGateway.SendAgentsUpdateAsync(worker, roleName, content, ct);
        }
        catch (OperationCanceledException cancellation)
            when (ct.IsCancellationRequested && cancellation.CancellationToken == ct)
        {
            // THE CALLER'S OWN CANCELLATION, raised by the send itself: the EXACT caught instance
            // propagates. No diagnostic is emitted for it — a diagnostic here could only replace it.
            throw;
        }
        catch (Exception ex)
        {
            // ORDINARY FAILURE — best-effort, and the emission is GUARDED so a throwing logger can
            // neither escape nor supply a cancellation the caller never made.
            LogSafely(() => _logger.LogWarning(ex, "Failed to send AGENTS.md to worker {WorkerId} for role {Role}",
                worker.Id, roleName));
        }
    }

    /// <summary>
    /// Runs a diagnostic emission best-effort: a logger's failure is swallowed so it can never
    /// replace the guarded operation's own outcome.
    /// </summary>
    /// <param name="emit">The guarded emission.</param>
    private static void LogSafely(Action emit)
    {
        try
        {
            emit();
        }
        catch (Exception)
        {
            // Best-effort by contract: a diagnostic failure may never become the reported outcome.
        }
    }

    /// <summary>
    /// Sends an UpdateAgents message to all connected workers whose role matches the given role string.
    /// Best-effort: failures are logged but do not block the pipeline.
    /// </summary>
    public async Task BroadcastAgentsUpdateAsync(WorkerRole role, string content, CancellationToken ct)
    {
        var workers = _workerGateway.GetAllWorkers()
            .Where(w => w.Role == role);

        var roleName = role.ToRoleName();

        foreach (var worker in workers)
        {
            try
            {
                await _workerGateway.SendAgentsUpdateAsync(worker.Id, roleName, content, ct);
                _logger.LogInformation("Sent updated AGENTS.md to worker {WorkerId} (role={Role})", worker.Id, roleName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send AGENTS.md update to worker {WorkerId}", worker.Id);
            }
        }
    }

    /// <summary>
    /// Restore active pipelines from the persistence store on startup.
    /// Re-primes Brain sessions so restored active pipelines are tracked by the pipeline manager.
    /// A RESTORED pipeline holding an active attempt
    /// (<see cref="GoalPipeline.IsRestoredActiveAttemptHold"/>) is reported and then given ONLY the
    /// NON-DESTRUCTIVE half of the setup: it is registered with the Brain and its goal session is
    /// forked or reattached, exactly as an unheld restoration is, because a surviving worker may
    /// reclaim the attempt at Register and the Brain must already know the goal for the
    /// <c>get_goal</c> tool and for its own session context. What it still skips is EVERYTHING
    /// DESTRUCTIVE: no goal-row cleanup or status change, no pointer clear, no
    /// <see cref="TaskQueue.MarkComplete"/>, no pipeline removal and no re-dispatch enqueue —
    /// orchestrator restart alone remains no permission to invalidate or replace the attempt. The
    /// report carries the restore-time classification VALUES the pipeline already holds
    /// (<see cref="GoalPipeline.RestoredRegistryClassification"/>,
    /// <see cref="GoalPipeline.RestoredActivePointerClassification"/> and
    /// <see cref="GoalPipeline.RestoredActiveTaskMappingPresent"/>) and never any registry
    /// payload, result content or parser text; the facts stay readable on the object itself, so
    /// observing them never requires startup logging.
    /// </summary>
    public async Task RestoreActivePipelinesAsync(CancellationToken ct)
    {
        var restored = _pipelineManager.RestoreFromStore();
        if (restored.Count == 0)
        {
            // Even when there are no active pipelines to restore, clean up any orphaned
            // session files that may have been left by a previous crash.
            await CleanupOrphanedGoalSessionsAsync(ct);
            return;
        }

        _logger.LogInformation("Restoring {Count} active pipeline(s) from persistence store", restored.Count);

        foreach (var pipeline in restored)
        {
            // THE RESTORE-ORIGIN HOLD GATE — recognised BEFORE anything else the loop does. An
            // instance restored with a NONTERMINAL phase and a NON-NULL captured active-task
            // pointer still owns the persisted attempt: orchestrator restart alone is not
            // permission to invalidate or replace it. What follows the report below is therefore
            // ONLY the NON-DESTRUCTIVE Brain setup — registration and the session fork/reattach —
            // and this iteration is then done. Every DESTRUCTIVE step stays skipped: no planning
            // or other LLM work, no goal-row status cleanup or status repair, no pointer clear, no
            // TaskQueue.MarkComplete, no pipeline removal and no redispatch enqueue. NOTHING is
            // mutated: the goal row and the pipeline are left exactly as restored, and the
            // instance is left in the manager so GetActivePipelines keeps naming it and
            // orphan-session cleanup retains its session files. The attempt waits for the
            // reconnecting worker's Register adoption, or for the reconciliation sweep.
            if (pipeline.IsRestoredActiveAttemptHold)
            {
                // THE ENRICHED HOLD REPORT — CLASSIFICATION VALUES ONLY. The three restore-time
                // facts are read straight off the pipeline object, where they are the AUTHORITATIVE
                // record: this Warning merely reports them, so a direct/on-demand restore that
                // never reaches startup logging can still read exactly the same values from
                // GoalPipeline.
                //
                // NOTHING THAT COULD LEAK IS EMITTED: no raw registry JSON, no capture or decode
                // result content, no slot/counter payload, no parser exception text and no
                // exception object (which could carry such text into the sink). The classification
                // enums and the mapping-present boolean are closed, fixed vocabularies; the goal id
                // and the active-task pointer were already reported before this enrichment. The
                // emission stays inside the no-throw LogSafely guard.
                LogSafely(() => _logger.LogWarning(
                    "Restored pipeline {GoalId} holds restored active attempt {TaskId} — awaiting reconciliation; the attempt is retained and no automatic re-dispatch is performed (registry evidence {RegistryClassification}, active slot {ActiveSlotClassification}, active mapping present {ActiveMappingPresent})",
                    pipeline.GoalId,
                    pipeline.ActiveTaskId,
                    pipeline.RestoredRegistryClassification,
                    pipeline.RestoredActivePointerClassification,
                    pipeline.RestoredActiveTaskMappingPresent));

                // THE NON-DESTRUCTIVE BRAIN SETUP, IDENTICAL TO THE UNHELD PATH BELOW — literally
                // the same shared call, so a held and an unheld restoration can never drift. A
                // held attempt's worker may reconnect and adopt it at Register, and the Brain must
                // already track the goal for get_goal and for the goal session. Registration and
                // session work are REVERSIBLE knowledge, not authority: neither dispatches,
                // replays nor releases anything, and the hold itself is untouched by both.
                await RegisterNonDestructiveBrainSetupAsync(pipeline, ct);
                continue;
            }

            // THE NON-DESTRUCTIVE BRAIN SETUP. The SAME shared call the held path above makes, so
            // a held and an unheld restoration can never drift apart: the Brain tracks the
            // restored pipeline for get_goal, and the goal session is forked from master (when it
            // is missing) or reattached. Neither step dispatches, releases or replays anything.
            await RegisterNonDestructiveBrainSetupAsync(pipeline, ct);

            if (pipeline.Phase is GoalPhase.Done or GoalPhase.Failed)
                continue;

            // Reconcile with DB status — a goal may have been marked Failed/Completed
            // in the DB while its pipeline record still shows an active phase.
            Goal? goal = null;
            if (_goalStore is not null)
            {
                goal = await _goalStore.GetGoalAsync(pipeline.GoalId, ct);
                if (goal is not null && goal.Status is (GoalStatus.Completed or GoalStatus.Failed))
                {
                    _logger.LogInformation("Pipeline {GoalId} has stale phase {Phase} but goal is {Status} — cleaning up",
                        pipeline.GoalId, pipeline.Phase, goal.Status);
                    _pipelineManager.RemovePipeline(pipeline.GoalId);
                    (_brain as DistributedBrain)?.DeregisterActivePipeline(pipeline.GoalId);
                    continue;
                }
                if (goal is null)
                {
                    _logger.LogWarning("Pipeline {GoalId} has no matching goal in DB — skipping reconciliation", pipeline.GoalId);
                }
            }

            // If the pipeline was mid-planning (no ActiveTaskId and at Planning/Merging phase),
            // discard it and reset the goal so DispatchNextGoalAsync picks it up fresh.
            if (pipeline.ActiveTaskId is null && pipeline.Phase is GoalPhase.Planning or GoalPhase.Merging)
            {
                _logger.LogInformation("Pipeline {GoalId} was mid-{Phase} — discarding stale pipeline for fresh dispatch",
                    pipeline.GoalId, pipeline.Phase);

                _pipelineManager.RemovePipeline(pipeline.GoalId);
                // Pipeline was registered above — clean it up from Brain's _activePipelines
                (_brain as DistributedBrain)?.DeregisterActivePipeline(pipeline.GoalId);
                await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, GoalStatus.Pending, null, ct);
                _goalReadyNotifier?.NotifyGoalReady();
                continue;
            }

            // An active pipeline means the goal is running. Persist that, otherwise a goal
            // restored mid-task keeps whatever status it had before the restart (often
            // Pending) — the re-dispatch path never sets InProgress, so the stale status
            // would stick forever and the API would disagree with the dashboard.
            if (goal is not null && goal.Status is not GoalStatus.InProgress)
            {
                _logger.LogInformation(
                    "Pipeline {GoalId} is active in phase {Phase} but goal status is {Status} — restoring to InProgress",
                    pipeline.GoalId, pipeline.Phase, goal.Status);

                var startedMeta = goal.StartedAt is null
                    ? new GoalUpdateMetadata { StartedAt = pipeline.GoalStartedAt ?? pipeline.CreatedAt }
                    : null;
                await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, GoalStatus.InProgress, startedMeta, ct);
            }

            // THE MID-TASK ACTIVE-TASK CLEAR/REDISPATCH BRANCH IS DELETED. It used to treat every
            // restored non-null pointer as a lost worker — clearing the queue entry, blanking the
            // pointer and enqueuing a redispatch. That is exactly the destructive automatic
            // replacement the restore-origin hold above now refuses, so the branch is gone rather
            // than left as contradictory, unreachable logic. A restored non-null pointer is
            // always held, so this point is reachable only for a restored NULL pointer.
        }

        _logger.LogInformation("Restored {Count} pipeline(s): {GoalIds}",
            restored.Count, string.Join(", ", restored.Select(p => p.GoalId)));

        // Clean up any orphaned goal session files whose goals are no longer active
        await CleanupOrphanedGoalSessionsAsync(ct);
    }

    /// <summary>
    /// THE ONE NON-DESTRUCTIVE BRAIN SETUP, shared by the UNHELD and the HELD restore paths so the
    /// two can never drift apart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHAT IT DOES, and nothing more: registers a nonterminal restored pipeline with the Brain (so
    /// the <c>get_goal</c> tool can report its iteration and phase) and makes sure its goal session
    /// exists on disk — forked from master when it is missing, reattached when it is already there.
    /// </para>
    /// <para>
    /// IT IS REVERSIBLE KNOWLEDGE, NOT AUTHORITY. It dispatches nothing, replays nothing, releases
    /// no hold, completes no queue entry, changes no goal row, removes no pipeline and enqueues no
    /// re-dispatch. That is exactly why a HELD pipeline may have it: the held attempt stays held
    /// and its durable evidence stays untouched, while a surviving worker that reconnects and
    /// adopts the attempt at Register finds a Brain that already knows the goal and its session.
    /// </para>
    /// <para>
    /// A TERMINAL pipeline gets neither step, matching the pre-existing unheld behavior exactly.
    /// </para>
    /// </remarks>
    /// <param name="pipeline">The restored pipeline to prime.</param>
    /// <param name="ct">The caller's cancellation token, passed through to the session work.</param>
    private async Task RegisterNonDestructiveBrainSetupAsync(GoalPipeline pipeline, CancellationToken ct)
    {
        if (pipeline.Phase is GoalPhase.Done or GoalPhase.Failed)
            return;

        (_brain as DistributedBrain)?.RegisterActivePipeline(pipeline);

        if (_brain is null)
            return;

        if (!_brain.GoalSessionExists(pipeline.GoalId))
        {
            await _brain.ForkSessionForGoalAsync(pipeline.GoalId, ct);
        }
        else
        {
            await _brain.RegisterExistingGoalSessionAsync(pipeline.GoalId, ct);
        }
    }

    /// <summary>
    /// Scans for Completed goals whose feature branches have been merged for longer
    /// than the configured delay, and deletes those branches from the remote.
    /// Best-effort: failures are logged but do not block other cleanups.
    /// </summary>
    public async Task CleanupMergedBranchesAsync(CancellationToken ct)
    {
        if (_repoManager is null) return;
        if (_goalStore is null) return;

        var delayHours = _config?.Orchestrator.BranchCleanupDelayHours ?? 48;
        var cutoff = DateTime.UtcNow.AddHours(-delayHours);

        var completedGoals = await _goalStore.GetGoalsByStatusAsync(GoalStatus.Completed, ct);
        var eligible = completedGoals
            .Where(g => g.CompletedAt.HasValue && g.CompletedAt.Value < cutoff)
            .Where(g => !string.IsNullOrEmpty(g.MergeCommitHash))
            .Where(g => !g.BranchCleanedUp)
            // THE HOLD IS ENFORCED HERE TOO: a held pipeline's restored attempt is retained, so its
            // branch is not cleanable evidence of finished work — the automatic cleanup skips it.
            .Where(g => _pipelineManager.GetByGoalId(g.Id)?.IsRestoredActiveAttemptHold != true)
            .ToList();

        if (eligible.Count == 0) return;

        _logger.LogInformation("Branch cleanup: found {Count} eligible goal(s) with merged branches older than {Hours}h",
            eligible.Count, delayHours);

        foreach (var goal in eligible)
        {
            var branchName = $"copilothive/{goal.Id}";
            var allSucceeded = true;
            foreach (var repoName in goal.RepositoryNames)
            {
                var deleteResult = await _repoManager.DeleteRemoteBranchAsync(repoName, branchName, ct);
                if (deleteResult == BranchDeleteResult.Success || deleteResult == BranchDeleteResult.NotFound)
                {
                    _logger.LogInformation("Branch cleanup: deleted {Branch} from {Repo} for goal {GoalId} (result: {Result})",
                        branchName, repoName, goal.Id, deleteResult);
                }
                else
                {
                    _logger.LogWarning("Branch cleanup: failed to delete {Branch} from {Repo} for goal {GoalId}",
                        branchName, repoName, goal.Id);
                    allSucceeded = false;
                }
            }

            if (allSucceeded)
            {
                goal.BranchCleanedUp = true;
                await _goalStore.UpdateGoalAsync(goal, ct);
            }
        }
    }

    /// <summary>
    /// Scans for orphaned brain-goal-*.json files and deletes any whose goalId is not
    /// in the active pipeline set. Called at the end of <see cref="RestoreActivePipelinesAsync"/>
    /// to remove stale session files left behind by crashed or interrupted runs.
    /// </summary>
    public async Task CleanupOrphanedGoalSessionsAsync(CancellationToken ct)
    {
        if (_brain is not DistributedBrain db)
            return;

        var stateDir = db.StateDirectory;
        if (string.IsNullOrEmpty(stateDir) || !Directory.Exists(stateDir))
            return;

        var activeGoalIds = _pipelineManager.GetActivePipelines()
            .Select(p => p.GoalId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.GetFiles(stateDir, "brain-goal-*.json"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var goalId = fileName.Replace("brain-goal-", "");
            if (!activeGoalIds.Contains(goalId))
            {
                File.Delete(file);
                _logger.LogInformation("Cleaned up orphaned goal session: {GoalId}", goalId);
            }
        }

        await Task.CompletedTask;
    }
}

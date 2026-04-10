using System.Collections.Concurrent;
using CopilotHive.Agents;
using CopilotHive.Configuration;
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

    // Mutable state shared with GoalDispatcher via reference
    private readonly ConcurrentDictionary<string, bool> _dispatchedGoals;
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
        ConcurrentDictionary<string, bool> dispatchedGoals,
        ConcurrentQueue<string> redispatchQueue,
        ILogger logger,
        KnowledgeGraph? knowledgeGraph = null)
    {
        _pipelineManager = pipelineManager;
        _goalManager = goalManager;
        _taskQueue = taskQueue;
        _workerGateway = workerGateway;
        _brain = brain;
        _agentsManager = agentsManager;
        _configRepo = configRepo;
        _knowledgeGraph = knowledgeGraph;
        _dispatchedGoals = dispatchedGoals;
        _redispatchQueue = redispatchQueue;
        _logger = logger;
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
                    _logger.LogWarning(ex, "Failed to reload knowledge graph from config repo");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync agents from config repo");
        }

        LastAgentsSync = DateTime.UtcNow;
    }

    public async Task SendAgentsMdToWorkerAsync(ConnectedWorker worker, WorkerRole role, CancellationToken ct)
    {
        if (_agentsManager is null) return;
        var content = _agentsManager.GetAgentsMd(role);
        if (string.IsNullOrEmpty(content)) return;

        var roleName = role.ToRoleName();
        try
        {
            await _workerGateway.SendAgentsUpdateAsync(worker.Id, roleName, content, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send AGENTS.md to worker {WorkerId} for role {Role}",
                worker.Id, roleName);
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
    /// Re-primes Brain sessions and marks goals as dispatched.
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
            _dispatchedGoals.TryAdd(pipeline.GoalId, true);

            // Register restored active pipelines with the Brain so get_goal tool works
            if (pipeline.Phase is not (GoalPhase.Done or GoalPhase.Failed))
                (_brain as DistributedBrain)?.RegisterActivePipeline(pipeline);

            // Ensure the goal session exists on disk. If the orchestrator restarted mid-dispatch,
            // the session may not have been forked yet. Fork from master to recover.
            if (_brain is not null && pipeline.Phase is not (GoalPhase.Done or GoalPhase.Failed))
            {
                if (!_brain.GoalSessionExists(pipeline.GoalId))
                {
                    await _brain.ForkSessionForGoalAsync(pipeline.GoalId, ct);
                }
            }

            if (pipeline.Phase is GoalPhase.Done or GoalPhase.Failed)
                continue;

            // If the pipeline was mid-planning (no ActiveTaskId and at Planning/Merging phase),
            // discard it and reset the goal so DispatchNextGoalAsync picks it up fresh.
            if (pipeline.ActiveTaskId is null && pipeline.Phase is GoalPhase.Planning or GoalPhase.Merging)
            {
                _logger.LogInformation("Pipeline {GoalId} was mid-{Phase} — discarding stale pipeline for fresh dispatch",
                    pipeline.GoalId, pipeline.Phase);

                _pipelineManager.RemovePipeline(pipeline.GoalId);
                _dispatchedGoals.TryRemove(pipeline.GoalId, out _);
                // Pipeline was registered above — clean it up from Brain's _activePipelines
                (_brain as DistributedBrain)?.DeregisterActivePipeline(pipeline.GoalId);
                await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, GoalStatus.Pending, null, ct);
                continue;
            }

            // If the pipeline was mid-task (has ActiveTaskId), the old worker is gone
            // after a restart. Clear the active task and enqueue for re-dispatch.
            if (pipeline.ActiveTaskId is not null)
            {
                _logger.LogInformation("Pipeline {GoalId} was mid-task ({TaskId}) — clearing stale task for re-dispatch",
                    pipeline.GoalId, pipeline.ActiveTaskId);

                var staleTaskId = pipeline.ActiveTaskId;
                _taskQueue.MarkComplete(staleTaskId);
                pipeline.ClearActiveTask();
                _redispatchQueue.Enqueue(pipeline.GoalId);
            }
        }

        _logger.LogInformation("Restored {Count} pipeline(s): {GoalIds}",
            restored.Count, string.Join(", ", restored.Select(p => p.GoalId)));

        // Clean up any orphaned goal session files whose goals are no longer active
        await CleanupOrphanedGoalSessionsAsync(ct);
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

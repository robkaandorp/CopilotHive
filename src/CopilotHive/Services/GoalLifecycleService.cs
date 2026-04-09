using CopilotHive.Agents;
using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Metrics;
using CopilotHive.Orchestration;
using CopilotHive.Workers;

namespace CopilotHive.Services;

/// <summary>
/// Handles goal lifecycle bookkeeping: completion, failure, metrics recording,
/// config repo persistence, and AGENTS.md version tracking.
/// Extracted from <see cref="GoalDispatcher"/> — all logic is identical.
/// </summary>
internal sealed class GoalLifecycleService
{
    private readonly GoalManager _goalManager;
    private readonly MetricsTracker? _metricsTracker;
    private readonly AgentsManager? _agentsManager;
    private readonly ConfigRepoManager? _configRepo;
    private readonly IDistributedBrain? _brain;
    private readonly ILogger _logger;

    public GoalLifecycleService(
        GoalManager goalManager,
        ILogger logger,
        MetricsTracker? metricsTracker = null,
        AgentsManager? agentsManager = null,
        ConfigRepoManager? configRepo = null,
        IDistributedBrain? brain = null)
    {
        _goalManager = goalManager;
        _logger = logger;
        _metricsTracker = metricsTracker;
        _agentsManager = agentsManager;
        _configRepo = configRepo;
        _brain = brain;
    }

    public async Task MarkGoalCompletedAsync(GoalPipeline pipeline, CancellationToken ct)
    {
        // Defensive: prevent double-completion
        if (pipeline.Phase == GoalPhase.Done)
        {
            _logger.LogWarning("Goal {GoalId} is already Done — skipping duplicate completion", pipeline.GoalId);
            return;
        }

        pipeline.AdvanceTo(GoalPhase.Done);
        await FinalizeGoalAsync(pipeline, GoalStatus.Completed, failureReason: null,
            mergeCommitHash: pipeline.MergeCommitHash, ct);

        // Check for regression after recording metrics
        if (_metricsTracker is not null && _agentsManager is not null)
        {
            if (pipeline.Metrics.TotalTests == 0)
                _logger.LogWarning("Test metrics not extracted (TotalTests=0); regression check will skip test comparison.");

            if (_metricsTracker.HasRegressed(pipeline.Metrics))
            {
                _logger.LogWarning("⚠️ REGRESSION DETECTED for goal {GoalId} — rolling back AGENTS.md", pipeline.GoalId);

                // Only rollback roles whose AGENTS.md version changed this iteration
                var modifiedRoles = GetModifiedRoles(pipeline.Metrics);
                if (modifiedRoles.Count == 0)
                {
                    _logger.LogInformation("No AGENTS.md files were modified this iteration — nothing to rollback");
                }

                foreach (var role in modifiedRoles)
                {
                    try
                    {
                        _agentsManager.RollbackAgentsMd(role);
                        _logger.LogInformation("Rolled back {Role} AGENTS.md", role);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to rollback {Role} AGENTS.md", role);
                    }
                }
            }
            else
            {
                var comparison = _metricsTracker.CompareWithPrevious(pipeline.Metrics);
                if (comparison is not null)
                {
                    _logger.LogInformation(
                        "Metrics comparison for {GoalId}: CoverageDelta={CovDelta:+0.0;-0.0}%, PassRateDelta={PRDelta:+0.00;-0.00}",
                        pipeline.GoalId, comparison.CoverageDelta, comparison.PassRateDelta);
                }
            }
        }

        await TryAutoTagReleaseAsync(pipeline, ct);
    }

    public async Task MarkGoalFailedAsync(GoalPipeline pipeline, string reason, CancellationToken ct)
    {
        pipeline.AdvanceTo(GoalPhase.Failed);
        await FinalizeGoalAsync(pipeline, GoalStatus.Failed, failureReason: reason,
            mergeCommitHash: null, ct);
    }

    internal async Task FinalizeGoalAsync(
        GoalPipeline pipeline,
        GoalStatus status,
        string? failureReason,
        string? mergeCommitHash,
        CancellationToken ct)
    {
        var iterationSummary = GoalDispatcher.BuildIterationSummary(pipeline);
        pipeline.CompletedIterationSummaries.Add(iterationSummary);

        var goalStartedAt = pipeline.GoalStartedAt ?? pipeline.Goal.StartedAt ?? pipeline.CreatedAt;
        var duration = pipeline.CompletedAt.HasValue
            ? pipeline.CompletedAt.Value - goalStartedAt
            : TimeSpan.Zero;

        var meta = new GoalUpdateMetadata
        {
            CompletedAt = pipeline.CompletedAt ?? DateTime.UtcNow,
            Iterations = pipeline.Iteration,
            IterationSummary = iterationSummary,
            TotalDurationSeconds = duration.TotalSeconds,
            FailureReason = failureReason,
            MergeCommitHash = mergeCommitHash,
        };

        await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, status, meta, ct);
        await CommitGoalsToConfigRepoAsync(
            failureReason is not null
                ? $"Goal '{pipeline.GoalId}' failed: {failureReason}"
                : $"Goal '{pipeline.GoalId}' completed",
            ct);

        pipeline.Metrics.Iteration = pipeline.Iteration;
        pipeline.Metrics.Duration = duration;
        pipeline.Metrics.Verdict = status == GoalStatus.Completed ? TaskVerdict.Pass : TaskVerdict.Fail;
        pipeline.Metrics.RetryCount = pipeline.ReviewRetries + pipeline.TestRetries;
        pipeline.Metrics.ReviewRetryCount = pipeline.ReviewRetries;
        pipeline.Metrics.TestRetryCount = pipeline.TestRetries;
        PopulateAgentsMdVersions(pipeline);
        _metricsTracker?.RecordIteration(pipeline.Metrics);
        await CommitMetricsToConfigRepoAsync(pipeline, ct);

        // Deregister from Brain — goal is no longer active
        (_brain as DistributedBrain)?.DeregisterActivePipeline(pipeline.GoalId);

        // Delete goal session on failure (no summary to merge)
        if (status == GoalStatus.Failed && _brain is not null)
            _brain.DeleteGoalSession(pipeline.GoalId);

        if (status == GoalStatus.Completed)
        {
            _logger.LogInformation("Goal {GoalId} completed in {Elapsed}", pipeline.GoalId, DurationFormatter.FormatDuration(duration));
            _logger.LogInformation(
                "\uD83C\uDF89 Goal {GoalId} completed! Iterations={Iterations}, Duration={Duration:F1}min, " +
                "Tests={Passed}/{Total}, Coverage={Coverage:F1}%",
                pipeline.GoalId, pipeline.Iteration, duration.TotalMinutes,
                pipeline.Metrics.PassedTests, pipeline.Metrics.TotalTests,
                pipeline.Metrics.CoveragePercent);
        }
        else
            _logger.LogWarning("Goal {GoalId} failed: {Reason}", pipeline.GoalId, failureReason);
    }

    internal async Task TryAutoTagReleaseAsync(GoalPipeline pipeline, CancellationToken ct)
    {
        try
        {
            var store = _goalManager.Sources.OfType<IGoalStore>().FirstOrDefault();
            if (store is null)
                return;

            var goalId = pipeline.GoalId;
            var goal = await store.GetGoalAsync(goalId, ct);
            if (goal is null || goal.ReleaseId is not null || goal.RepositoryNames.Count == 0)
                return;

            var releases = await store.GetReleasesAsync(ct);
            var planningReleases = releases.Where(r => r.Status == ReleaseStatus.Planning).ToList();

            if (planningReleases.Count != 1)
                return;

            var planningRelease = planningReleases[0];
            goal = await store.GetGoalAsync(goalId, ct);
            if (goal is null || goal.ReleaseId is not null) return; // already tagged or deleted
            goal.ReleaseId = planningRelease.Id;
            await store.UpdateGoalAsync(goal, ct);

            _logger.LogInformation(
                "Auto-tagged goal {GoalId} to release {ReleaseId}",
                pipeline.GoalId, goal.ReleaseId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-tag goal {GoalId} to a release", pipeline.GoalId);
        }
    }

    internal void PopulateAgentsMdVersions(GoalPipeline pipeline)
    {
        if (_agentsManager is not null)
        {
            foreach (var role in WorkerRoles.AgentRoles)
            {
                var roleName = role.ToRoleName();
                var history = _agentsManager.GetHistory(role);
                pipeline.Metrics.AgentsMdVersions[roleName] = $"v{history.Length:D3}";
            }
        }
    }

    internal List<WorkerRole> GetModifiedRoles(IterationMetrics current)
    {
        var modified = new List<WorkerRole>();
        var history = _metricsTracker!.History;

        // Need at least 2 entries: previous + current (just recorded)
        if (history.Count < 2)
            return modified;

        var previous = history[^2];

        foreach (var (roleName, currentVersion) in current.AgentsMdVersions)
        {
            if (!previous.AgentsMdVersions.TryGetValue(roleName, out var previousVersion)
                || currentVersion != previousVersion)
            {
                var role = WorkerRoleExtensions.ParseRole(roleName);
                if (role.HasValue)
                    modified.Add(role.Value);
            }
        }

        return modified;
    }

    internal async Task CommitGoalsToConfigRepoAsync(string commitMessage, CancellationToken ct)
    {
        if (_configRepo is null)
            return;

        var goalsPath = Path.Combine(_configRepo.LocalPath, "goals.yaml");
        if (!File.Exists(goalsPath))
            return;

        try
        {
            await _configRepo.CommitFileAsync(goalsPath, commitMessage, ct);
            _logger.LogInformation("Committed goals.yaml update: {Message}", commitMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to commit goals.yaml update to config repo");
        }
    }

    internal async Task CommitMetricsToConfigRepoAsync(GoalPipeline pipeline, CancellationToken ct)
    {
        if (_configRepo is null)
            return;

        try
        {
            var metricsDir = Path.Combine(_configRepo.LocalPath, "metrics");
            Directory.CreateDirectory(metricsDir);

            var metricsPath = Path.Combine(metricsDir, $"{pipeline.GoalId}.json");
            var json = System.Text.Json.JsonSerializer.Serialize(pipeline.Metrics, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });
            await File.WriteAllTextAsync(metricsPath, json, ct);

            await _configRepo.CommitFileAsync(metricsPath,
                $"Metrics for goal '{pipeline.GoalId}' ({pipeline.Metrics.Verdict})", ct);
            _logger.LogInformation("Committed metrics for {GoalId} to config repo", pipeline.GoalId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to commit metrics for {GoalId} to config repo", pipeline.GoalId);
        }
    }
}

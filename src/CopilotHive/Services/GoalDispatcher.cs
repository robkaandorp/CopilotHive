using System.Collections.Concurrent;
using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Shared.Grpc;

namespace CopilotHive.Services;

/// <summary>
/// Background service that converts pending goals into multi-phase pipeline tasks
/// using the Brain for intelligent prompt crafting and decision-making.
/// Handles both new goal dispatch and task completion callbacks.
/// </summary>
public sealed class GoalDispatcher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly GoalManager _goalManager;
    private readonly GoalPipelineManager _pipelineManager;
    private readonly TaskQueue _taskQueue;
    private readonly WorkerPool _workerPool;
    private readonly IDistributedBrain? _brain;
    private readonly ILogger<GoalDispatcher> _logger;
    private readonly HiveConfigFile? _config;

    private readonly BranchCoordinator _branchCoordinator = new();
    private readonly TaskBuilder _taskBuilder = new(new BranchCoordinator());
    private readonly ConcurrentDictionary<string, bool> _dispatchedGoals = new();

    public GoalDispatcher(
        GoalManager goalManager,
        GoalPipelineManager pipelineManager,
        TaskQueue taskQueue,
        WorkerPool workerPool,
        TaskCompletionNotifier completionNotifier,
        ILogger<GoalDispatcher> logger,
        IDistributedBrain? brain = null,
        HiveConfigFile? config = null)
    {
        _goalManager = goalManager;
        _pipelineManager = pipelineManager;
        _taskQueue = taskQueue;
        _workerPool = workerPool;
        _brain = brain;
        _logger = logger;
        _config = config;

        completionNotifier.OnTaskCompleted += complete => HandleTaskCompletionAsync(complete);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GoalDispatcher started — polling for goals every {Interval}s (Brain: {BrainEnabled})",
            PollInterval.TotalSeconds, _brain is not null ? "enabled" : "disabled");

        // Give workers time to connect before dispatching
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchNextGoalAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GoalDispatcher error");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }

        _logger.LogInformation("GoalDispatcher stopped");
    }

    /// <summary>
    /// Called by HiveOrchestratorService when a worker completes a task.
    /// Drives the pipeline to its next phase using the Brain.
    /// </summary>
    public async Task HandleTaskCompletionAsync(TaskComplete complete, CancellationToken ct = default)
    {
        var pipeline = _pipelineManager.GetByTaskId(complete.TaskId);
        if (pipeline is null)
        {
            _logger.LogWarning("No pipeline found for completed task {TaskId}", complete.TaskId);
            return;
        }

        _logger.LogInformation("Pipeline {GoalId} task completed (phase={Phase}, status={Status})",
            pipeline.GoalId, pipeline.Phase, complete.Status);

        if (_brain is null)
        {
            // Without Brain: just mark done after coding phase
            await MarkGoalCompleted(pipeline, ct);
            return;
        }

        try
        {
            await DriveNextPhaseAsync(pipeline, complete, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error driving pipeline {GoalId} to next phase", pipeline.GoalId);
            pipeline.AdvanceTo(GoalPhase.Failed);
            await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, GoalStatus.Failed, ct);
        }
    }

    private async Task DriveNextPhaseAsync(GoalPipeline pipeline, TaskComplete complete, CancellationToken ct)
    {
        // Ask the Brain to interpret the worker output
        var interpretation = await _brain!.InterpretOutputAsync(
            pipeline,
            pipeline.Phase.ToString().ToLowerInvariant(),
            complete.Output,
            ct);

        _logger.LogInformation("Brain interpretation for {GoalId}: Action={Action}, Verdict={Verdict}",
            pipeline.GoalId, interpretation.Action, interpretation.Verdict ?? interpretation.ReviewVerdict);

        // Update metrics if available
        if (interpretation.TestMetrics is not null)
        {
            pipeline.Metrics.TotalTests = interpretation.TestMetrics.TotalTests ?? 0;
            pipeline.Metrics.PassedTests = interpretation.TestMetrics.PassedTests ?? 0;
            pipeline.Metrics.FailedTests = interpretation.TestMetrics.FailedTests ?? 0;
            pipeline.Metrics.CoveragePercent = interpretation.TestMetrics.CoveragePercent ?? 0;
        }
        if (interpretation.Issues is not null)
            pipeline.Metrics.Issues.AddRange(interpretation.Issues);

        // Decide next step based on the action
        switch (interpretation.Action)
        {
            case OrchestratorActionType.SpawnCoder:
                await DispatchToRole(pipeline, WorkerRole.Coder, interpretation.Prompt, ct);
                break;

            case OrchestratorActionType.SpawnReviewer:
                pipeline.AdvanceTo(GoalPhase.Review);
                await DispatchToRole(pipeline, WorkerRole.Reviewer, interpretation.Prompt, ct);
                break;

            case OrchestratorActionType.SpawnTester:
                pipeline.AdvanceTo(GoalPhase.Testing);
                await DispatchToRole(pipeline, WorkerRole.Tester, interpretation.Prompt, ct);
                break;

            case OrchestratorActionType.RequestChanges:
                if (!pipeline.IncrementReviewRetry())
                {
                    _logger.LogWarning("Pipeline {GoalId} exceeded max review retries", pipeline.GoalId);
                    pipeline.AdvanceTo(GoalPhase.Failed);
                    await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, GoalStatus.Failed, ct);
                    return;
                }
                pipeline.IncrementIteration();
                pipeline.AdvanceTo(GoalPhase.Coding);
                var fixPrompt = await _brain.CraftPromptAsync(pipeline, "coder",
                    $"Reviewer feedback:\n{interpretation.Reason}", ct);
                await DispatchToRole(pipeline, WorkerRole.Coder, fixPrompt, ct);
                break;

            case OrchestratorActionType.Retry:
                if (!pipeline.IncrementTestRetry())
                {
                    _logger.LogWarning("Pipeline {GoalId} exceeded max test retries", pipeline.GoalId);
                    pipeline.AdvanceTo(GoalPhase.Failed);
                    await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, GoalStatus.Failed, ct);
                    return;
                }
                pipeline.IncrementIteration();
                pipeline.AdvanceTo(GoalPhase.Coding);
                var retryPrompt = await _brain.CraftPromptAsync(pipeline, "coder",
                    $"Test failures:\n{interpretation.Reason}", ct);
                await DispatchToRole(pipeline, WorkerRole.Coder, retryPrompt, ct);
                break;

            case OrchestratorActionType.Merge:
                pipeline.AdvanceTo(GoalPhase.Merging);
                await PerformMergeAsync(pipeline, ct);
                break;

            case OrchestratorActionType.Done:
            case OrchestratorActionType.Skip:
                await MarkGoalCompleted(pipeline, ct);
                break;

            default:
                // Ask the Brain explicitly for next step
                var decision = await _brain.DecideNextStepAsync(
                    pipeline, pipeline.BuildContextSummary(), ct);
                _logger.LogInformation("Brain decided: {Action} for {GoalId}", decision.Action, pipeline.GoalId);

                // Recurse with the decision as a synthetic completion
                var synthetic = new TaskComplete
                {
                    TaskId = complete.TaskId,
                    Status = complete.Status,
                    Output = decision.Reason ?? "",
                };
                await DriveNextPhaseAsync(pipeline, synthetic, ct);
                break;
        }
    }

    private async Task DispatchToRole(GoalPipeline pipeline, WorkerRole role, string? prompt, CancellationToken ct)
    {
        // If no prompt provided (Brain might have returned null), craft one
        if (string.IsNullOrWhiteSpace(prompt) && _brain is not null)
        {
            prompt = await _brain.CraftPromptAsync(pipeline, role.ToString().ToLowerInvariant(), null, ct);
        }

        prompt ??= $"Work on: {pipeline.Description}";

        var branchAction = pipeline.CoderBranch is null ? BranchAction.Create : BranchAction.Checkout;
        var repositories = ResolveRepositories(pipeline.Goal);

        var task = _taskBuilder.Build(
            goalId: pipeline.GoalId,
            goalDescription: pipeline.Description,
            role: role,
            iteration: pipeline.Iteration,
            repositories: repositories,
            prompt: prompt,
            branchAction: branchAction);

        pipeline.SetActiveTask(task.TaskId, task.BranchInfo?.FeatureBranch);
        _pipelineManager.RegisterTask(task.TaskId, pipeline.GoalId);

        _taskQueue.Enqueue(task);
        _logger.LogInformation("Dispatched {Role} task {TaskId} for goal {GoalId} (branch={Branch})",
            role, task.TaskId, pipeline.GoalId, task.BranchInfo?.FeatureBranch);

        // Try to push directly to an idle worker
        var idleWorker = _workerPool.GetIdleWorker(role);
        if (idleWorker is not null)
        {
            var queuedTask = _taskQueue.TryDequeue(role);
            if (queuedTask is not null)
            {
                _taskQueue.Activate(queuedTask, idleWorker.Id);
                _workerPool.MarkBusy(idleWorker.Id, queuedTask.TaskId);
                await idleWorker.MessageChannel.Writer.WriteAsync(
                    new OrchestratorMessage { Assignment = queuedTask }, ct);
                _logger.LogInformation("Task {TaskId} pushed to worker {WorkerId}", queuedTask.TaskId, idleWorker.Id);
            }
        }
    }

    private async Task PerformMergeAsync(GoalPipeline pipeline, CancellationToken ct)
    {
        if (pipeline.CoderBranch is null)
        {
            _logger.LogWarning("Cannot merge pipeline {GoalId} — no coder branch set", pipeline.GoalId);
            pipeline.AdvanceTo(GoalPhase.Failed);
            return;
        }

        _logger.LogInformation("Merging branch {Branch} for goal {GoalId}", pipeline.CoderBranch, pipeline.GoalId);

        try
        {
            var repos = ResolveRepositories(pipeline.Goal);
            foreach (var repo in repos)
            {
                var tempDir = Path.Combine(Path.GetTempPath(), $"hive-merge-{Guid.NewGuid():N}");
                try
                {
                    await RunGitAsync($"clone --depth=50 {repo.Url} {tempDir}", ct);
                    await RunGitAsync($"-C {tempDir} checkout {repo.DefaultBranch}", ct);
                    await RunGitAsync($"-C {tempDir} merge origin/{pipeline.CoderBranch} --no-edit", ct);
                    await RunGitAsync($"-C {tempDir} push origin {repo.DefaultBranch}", ct);

                    _logger.LogInformation("Merged {Branch} into {Base} for {Repo}",
                        pipeline.CoderBranch, repo.DefaultBranch, repo.Name);
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        try { Directory.Delete(tempDir, true); }
                        catch { /* Best effort cleanup */ }
                    }
                }
            }

            await MarkGoalCompleted(pipeline, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Merge failed for pipeline {GoalId}", pipeline.GoalId);
            pipeline.AdvanceTo(GoalPhase.Failed);
            await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, GoalStatus.Failed, ct);
        }
    }

    private static async Task RunGitAsync(string arguments, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"git {arguments} failed: {stderr}");
        }
    }

    private async Task MarkGoalCompleted(GoalPipeline pipeline, CancellationToken ct)
    {
        pipeline.AdvanceTo(GoalPhase.Done);
        await _goalManager.UpdateGoalStatusAsync(pipeline.GoalId, GoalStatus.Completed, ct);

        var duration = pipeline.CompletedAt.HasValue
            ? (pipeline.CompletedAt.Value - pipeline.CreatedAt).TotalMinutes
            : 0;

        _logger.LogInformation(
            "🎉 Goal {GoalId} completed! Iterations={Iterations}, Duration={Duration:F1}min, " +
            "Tests={Passed}/{Total}, Coverage={Coverage:F1}%",
            pipeline.GoalId, pipeline.Iteration, duration,
            pipeline.Metrics.PassedTests, pipeline.Metrics.TotalTests,
            pipeline.Metrics.CoveragePercent);
    }

    private async Task DispatchNextGoalAsync(CancellationToken ct)
    {
        var goal = await _goalManager.GetNextGoalAsync(ct);
        if (goal is null)
            return;

        if (!_dispatchedGoals.TryAdd(goal.Id, true))
            return;

        _logger.LogInformation("Dispatching goal '{GoalId}': {Description}", goal.Id, goal.Description);

        // Create a pipeline for this goal
        var pipeline = _pipelineManager.CreatePipeline(goal, maxRetries: 3);

        if (_brain is not null)
        {
            // Brain-powered: ask the Brain to plan the goal
            var plan = await _brain.PlanGoalAsync(pipeline, ct);
            var firstRole = plan.Action switch
            {
                OrchestratorActionType.SpawnReviewer => WorkerRole.Reviewer,
                OrchestratorActionType.SpawnTester => WorkerRole.Tester,
                _ => WorkerRole.Coder,
            };

            pipeline.AdvanceTo(firstRole == WorkerRole.Coder ? GoalPhase.Coding : GoalPhase.Review);
            await DispatchToRole(pipeline, firstRole, plan.Prompt, ct);
        }
        else
        {
            // Mechanical fallback: just dispatch to coder
            pipeline.AdvanceTo(GoalPhase.Coding);
            await DispatchToRole(pipeline, WorkerRole.Coder, BuildCoderPrompt(goal), ct);
        }
    }

    private List<TargetRepository> ResolveRepositories(Goal goal)
    {
        var repos = new List<TargetRepository>();

        foreach (var repoName in goal.RepositoryNames)
        {
            var repoConfig = _config?.Repositories.FirstOrDefault(
                r => r.Name.Equals(repoName, StringComparison.OrdinalIgnoreCase));

            if (repoConfig is not null)
            {
                var url = InjectTokenIntoUrl(repoConfig.Url);
                repos.Add(new TargetRepository
                {
                    Name = repoConfig.Name,
                    Url = url,
                    DefaultBranch = repoConfig.DefaultBranch,
                });
            }
            else
            {
                _logger.LogWarning("Repository '{RepoName}' from goal not found in config", repoName);
            }
        }

        return repos;
    }

    private static string BuildCoderPrompt(Goal goal)
    {
        return $"""
            You are a coder working on a software project. Your task:

            {goal.Description}

            Instructions:
            - Make the necessary code changes to accomplish this goal.
            - Follow existing code conventions and style.
            - Commit your changes with a clear, descriptive commit message.
            - Only make changes directly related to the goal.
            """;
    }

    private static string InjectTokenIntoUrl(string url)
    {
        var token = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrEmpty(token) || !url.StartsWith("https://github.com/"))
            return url;

        return url.Replace("https://github.com/", $"https://x-access-token:{token}@github.com/");
    }
}

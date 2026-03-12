using System.Collections.Concurrent;
using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Shared.Grpc;

namespace CopilotHive.Services;

/// <summary>
/// Background service that converts pending goals into task assignments
/// and dispatches them to idle workers via the task queue.
/// </summary>
public sealed class GoalDispatcher(
    GoalManager goalManager,
    TaskQueue taskQueue,
    WorkerPool workerPool,
    ILogger<GoalDispatcher> logger,
    HiveConfigFile? config = null) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly BranchCoordinator _branchCoordinator = new();
    private readonly TaskBuilder _taskBuilder = new(new BranchCoordinator());
    private readonly ConcurrentDictionary<string, bool> _dispatchedGoals = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("GoalDispatcher started — polling for goals every {Interval}s", PollInterval.TotalSeconds);

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
                logger.LogError(ex, "GoalDispatcher error");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }

        logger.LogInformation("GoalDispatcher stopped");
    }

    private async Task DispatchNextGoalAsync(CancellationToken ct)
    {
        var goal = await goalManager.GetNextGoalAsync(ct);
        if (goal is null)
            return;

        // Skip goals we've already dispatched
        if (!_dispatchedGoals.TryAdd(goal.Id, true))
            return;

        // Check if we have an idle coder to handle this
        var idleCoder = workerPool.GetIdleWorker(WorkerRole.Coder);
        if (idleCoder is null)
        {
            logger.LogWarning("Goal '{GoalId}' ready but no idle coder available — queueing", goal.Id);
            // Build and enqueue the task; it'll be picked up when a coder becomes ready
            EnqueueCoderTask(goal);
            return;
        }

        logger.LogInformation("Dispatching goal '{GoalId}': {Description}", goal.Id, goal.Description);

        var task = EnqueueCoderTask(goal);

        // Push directly to the idle worker's message channel
        var queuedTask = taskQueue.TryDequeue(WorkerRole.Coder);
        if (queuedTask is not null)
        {
            taskQueue.Activate(queuedTask, idleCoder.Id);
            workerPool.MarkBusy(idleCoder.Id, queuedTask.TaskId);

            await idleCoder.MessageChannel.Writer.WriteAsync(
                new OrchestratorMessage { Assignment = queuedTask }, ct);

            logger.LogInformation("Task {TaskId} pushed to worker {WorkerId}", queuedTask.TaskId, idleCoder.Id);
        }
    }

    private TaskAssignment EnqueueCoderTask(Goal goal)
    {
        var repositories = ResolveRepositories(goal);
        var prompt = BuildCoderPrompt(goal);
        var task = _taskBuilder.Build(
            goalId: goal.Id,
            goalDescription: goal.Description,
            role: WorkerRole.Coder,
            iteration: 1,
            repositories: repositories,
            prompt: prompt,
            branchAction: BranchAction.Create);

        taskQueue.Enqueue(task);
        logger.LogInformation("Task {TaskId} enqueued for goal '{GoalId}'", task.TaskId, goal.Id);

        return task;
    }

    private List<TargetRepository> ResolveRepositories(Goal goal)
    {
        var repos = new List<TargetRepository>();

        foreach (var repoName in goal.RepositoryNames)
        {
            var repoConfig = config?.Repositories.FirstOrDefault(
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
                logger.LogWarning("Repository '{RepoName}' from goal not found in config", repoName);
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

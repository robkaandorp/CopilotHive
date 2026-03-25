using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests for elapsed-time completion logging in <see cref="GoalDispatcher"/>.
/// </summary>
public sealed class GoalDispatcherElapsedTimeTests
{
    /// <summary>
    /// Verifies that MarkGoalCompleted logs a message containing "completed in" and the goal ID.
    /// </summary>
    [Fact]
    public async Task MarkGoalCompleted_LogsCompletedInWithGoalId()
    {
        // Arrange
        var logger = new CollectingLogger<GoalDispatcher>();
        var goalId = $"goal-{Guid.NewGuid():N}";
        var goal = new Goal { Id = goalId, Description = "Test goal" };
        var goalSource = new CapturingGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken); // Populate internal map

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        // Set started timestamp so elapsed time can be computed
        goal.StartedAt = DateTime.UtcNow - TimeSpan.FromSeconds(45);

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

        // Act - use reflection to call private MarkGoalCompleted
        var method = typeof(GoalDispatcher).GetMethod("MarkGoalCompleted",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await (Task)method.Invoke(dispatcher, [pipeline, cts.Token])!;

        // Assert
        Assert.Contains(logger.Logs, l =>
            l.Message.Contains("completed in") && l.Message.Contains(goalId));
    }

    /// <summary>
    /// Verifies that the log message format matches "Goal {goalId} completed in {elapsed}".
    /// </summary>
    [Fact]
    public async Task MarkGoalCompleted_LogMessageFormatMatchesExpectedPattern()
    {
        // Arrange
        var logger = new CollectingLogger<GoalDispatcher>();
        var goalId = $"goal-{Guid.NewGuid():N}";
        var goal = new Goal { Id = goalId, Description = "Test goal" };
        var goalSource = new CapturingGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        // Set started timestamp with a known elapsed duration
        goal.StartedAt = DateTime.UtcNow - TimeSpan.FromSeconds(90);

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

        // Act
        var method = typeof(GoalDispatcher).GetMethod("MarkGoalCompleted",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await (Task)method.Invoke(dispatcher, [pipeline, cts.Token])!;

        // Assert - verify the log message format with regex
        var completedLog = logger.Logs.FirstOrDefault(l => l.Message.Contains("completed in"));
        Assert.True(completedLog != default, $"Expected log with 'completed in'. Logs: {string.Join(", ", logger.Logs.Select(l => l.Message))}");

        // Pattern: "Goal {goalId} completed in {duration}"
        Assert.Contains(goalId, completedLog.Message);
        Assert.Matches(@"Goal .* completed in \d+[hms]\s*(\d+[hms]\s*)?(\d+[hms]\s*)?", completedLog.Message);
    }

    /// <summary>
    /// Verifies that GoalUpdateMetadata.TotalDurationSeconds is set correctly in the completion call.
    /// </summary>
    [Fact]
    public async Task MarkGoalCompleted_SetsTotalDurationSecondsInMetadata()
    {
        // Arrange
        var logger = new CollectingLogger<GoalDispatcher>();
        var goalId = $"goal-{Guid.NewGuid():N}";
        var goal = new Goal { Id = goalId, Description = "Test goal" };
        var goalSource = new CapturingGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        // Set started timestamp with a known elapsed duration
        var expectedDuration = TimeSpan.FromSeconds(127);
        goal.StartedAt = DateTime.UtcNow - expectedDuration;

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

        // Act
        var method = typeof(GoalDispatcher).GetMethod("MarkGoalCompleted",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await (Task)method.Invoke(dispatcher, [pipeline, cts.Token])!;

        // Assert - verify TotalDurationSeconds was set
        Assert.NotNull(goalSource.LastMetadata);
        Assert.NotNull(goalSource.LastMetadata.TotalDurationSeconds);

        // Allow for some variance due to timing (within 1 second tolerance)
        var actualDuration = TimeSpan.FromSeconds(goalSource.LastMetadata.TotalDurationSeconds.Value);
        var tolerance = TimeSpan.FromSeconds(1);
        Assert.True(Math.Abs((actualDuration - expectedDuration).TotalSeconds) <= tolerance.TotalSeconds,
            $"Expected duration around {expectedDuration.TotalSeconds}s, got {actualDuration.TotalSeconds}s");
    }

    /// <summary>
    /// Verifies that the elapsed time is computed correctly for zero duration (immediate completion).
    /// </summary>
    [Fact]
    public async Task MarkGoalCompleted_ZeroDuration_StillLogsCompletedIn()
    {
        // Arrange
        var logger = new CollectingLogger<GoalDispatcher>();
        var goalId = $"goal-{Guid.NewGuid():N}";
        var goal = new Goal { Id = goalId, Description = "Test goal" };
        var goalSource = new CapturingGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        // Set started timestamp just now - almost zero elapsed time
        goal.StartedAt = DateTime.UtcNow;

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

        // Act
        var method = typeof(GoalDispatcher).GetMethod("MarkGoalCompleted",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await (Task)method.Invoke(dispatcher, [pipeline, cts.Token])!;

        // Assert
        Assert.Contains(logger.Logs, l => l.Message.Contains("completed in"));
        Assert.Contains(logger.Logs, l => l.Message.Contains("0s") || l.Message.Contains("1s"));
    }

    /// <summary>
    /// Verifies that the duration formatter is used correctly for elapsed time display.
    /// </summary>
    [Fact]
    public async Task MarkGoalCompleted_UsesDurationFormatterForElapsedTime()
    {
        // Arrange
        var logger = new CollectingLogger<GoalDispatcher>();
        var goalId = $"goal-{Guid.NewGuid():N}";
        var goal = new Goal { Id = goalId, Description = "Test goal" };
        var goalSource = new CapturingGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        // Set a duration that exercises hours, minutes, and seconds
        // e.g., 1h 23m 45s = 5025 seconds
        var expectedDuration = TimeSpan.FromSeconds(5025); // 1h 23m 45s
        goal.StartedAt = DateTime.UtcNow - expectedDuration;

        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            logger,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

        // Act
        var method = typeof(GoalDispatcher).GetMethod("MarkGoalCompleted",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await (Task)method.Invoke(dispatcher, [pipeline, cts.Token])!;

        // Assert - verify the log message uses DurationFormatter output (e.g., "1h 23m 45s")
        var completedLog = logger.Logs.FirstOrDefault(l => l.Message.Contains("completed in"));
        Assert.True(completedLog != default, $"Expected log with 'completed in'. Logs: {string.Join(", ", logger.Logs.Select(l => l.Message))}");

        // The DurationFormatter would format ~5025 seconds as something like "1h 23m 45s"
        // Since exact timing may vary slightly, we verify the format contains hours/minutes/seconds pattern
        Assert.Contains("h", completedLog.Message); // Hours present
        Assert.Contains("m", completedLog.Message); // Minutes present
        Assert.Contains("s", completedLog.Message);  // Seconds present
    }
}

/// <summary>
/// A goal source that captures the last UpdateGoalStatusAsync metadata for verification.
/// </summary>
file sealed class CapturingGoalSource : IGoalSource
{
    private readonly Goal _goal;

    public CapturingGoalSource(Goal goal) => _goal = goal;

    public string Name => "capturing";

    /// <summary>
    /// The last metadata passed to <see cref="UpdateGoalStatusAsync"/>, or null if not called.
    /// </summary>
    public GoalUpdateMetadata? LastMetadata { get; private set; }

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>([_goal]);

    public Task UpdateGoalStatusAsync(
        string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
    {
        LastMetadata = metadata;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Collecting logger for verifying log output in tests.
/// </summary>
file sealed class CollectingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Logs { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Logs.Add((logLevel, formatter(state, exception)));
    }
}
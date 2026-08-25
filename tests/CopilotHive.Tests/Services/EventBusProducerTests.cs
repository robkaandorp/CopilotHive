using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using DomainWorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Tests for event publishing via <see cref="IEventBus"/>: goal lifecycle events
/// (<see cref="GoalLifecycleService"/>), API endpoint producers (<c>IssueRaised</c>,
/// <c>IssueResolved</c>, <c>ReleaseCompleted</c>) and goal dispatch (<c>GoalDispatched</c>).
/// Uses a real <see cref="GoalManager"/> with custom fakes and a recording
/// <see cref="IEventBus"/> to verify that events are published (or not) at the right times.
/// </summary>
[Collection("HiveIntegration")]
public sealed class EventBusProducerTests
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly HiveTestFactory _baseFactory;

    /// <summary>Receives the shared <see cref="HiveTestFactory"/> fixture for this collection.</summary>
    /// <param name="factory">The shared test factory.</param>
    public EventBusProducerTests(HiveTestFactory factory)
    {
        _baseFactory = factory;
    }
    /// <summary>
    /// A minimal <see cref="IGoalStore"/> fake that records status updates and can throw
    /// on demand. Only the methods used by <see cref="GoalManager"/> for status updates
    /// are implemented; the rest throw <see cref="NotImplementedException"/>.
    /// </summary>
    private sealed class FakeGoalStore : IGoalStore
    {
        public string Name => "fake";

        public List<(string GoalId, GoalStatus Status, GoalUpdateMetadata? Metadata)> Updates { get; } = [];

        public Exception? ThrowOnUpdateStatus { get; set; }

        private readonly Dictionary<string, Goal> _goals = [];

        public void AddGoal(Goal goal) => _goals[goal.Id] = goal;

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

        public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
        {
            if (ThrowOnUpdateStatus is not null)
                throw ThrowOnUpdateStatus;
            Updates.Add((goalId, status, metadata));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.ToList().AsReadOnly());

        public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default)
            => Task.FromResult(_goals.TryGetValue(goalId, out var g) ? g : null);

        public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default)
        {
            _goals[goal.Id] = goal;
            return Task.FromResult(goal);
        }

        public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default)
        {
            _goals[goal.Id] = goal;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default)
        {
            return Task.FromResult(_goals.Remove(goalId));
        }

        public Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

        public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

        public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IterationSummary>>(Array.Empty<IterationSummary>());

        public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default)
            => Task.FromResult<Release?>(null);

        public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Release>>(Array.Empty<Release>());

        public Task UpdateReleaseAsync(Release release, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

        public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConversationEntry>>(Array.Empty<ConversationEntry>());

        public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>(Array.Empty<(string, PersistedClarification)>());
    }

    /// <summary>
    /// A recording <see cref="IEventBus"/> that captures all published events.
    /// </summary>
    private sealed class RecordingEventBus : IEventBus
    {
        public List<SystemEvent> Published { get; } = [];
        public event Action<SystemEvent>? OnEvent;

        public void Publish(SystemEvent evt)
        {
            Published.Add(evt);
            OnEvent?.Invoke(evt);
        }
    }

    /// <summary>Minimal in-memory <see cref="IIssueStore"/> for the orchestrator raise_issue tool.</summary>
    private sealed class FakeIssueStore : IIssueStore
    {
        public Dictionary<string, Issue> Issues { get; } = new();

        public Task<IReadOnlyList<Issue>> GetAllIssuesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Issue>>(Issues.Values.ToList());

        public Task<IReadOnlyList<Issue>> GetIssuesAsync(
            IssueStatus? status = null, IssueType? type = null, IssueSeverity? severity = null,
            string? repository = null, string? sourceGoalId = null, string? linkedGoalId = null,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Issue>>(Issues.Values.ToList());

        public Task<Issue?> GetIssueAsync(string issueId, CancellationToken ct = default)
            => Task.FromResult(Issues.TryGetValue(issueId, out var issue) ? issue : null);

        public Task<Issue> CreateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            Issues[issue.Id] = issue;
            return Task.FromResult(issue);
        }

        public Task UpdateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            Issues[issue.Id] = issue;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteIssueAsync(string issueId, CancellationToken ct = default)
            => Task.FromResult(Issues.Remove(issueId));
    }

    private static GoalPipeline CreatePipeline(string goalId = "test-goal-1")
    {
        var goal = new Goal { Id = goalId, Description = "Test goal" };
        return new GoalPipeline(goal);
    }

    [Fact]
    public async Task FinalizeGoalAsync_Completed_PublishesGoalCompletedWithGoalId()
    {
        var store = new FakeGoalStore();
        store.AddGoal(new Goal { Id = "test-goal-1", Description = "Test goal" });
        var manager = new GoalManager();
        manager.AddSource(store);
        var eventBus = new RecordingEventBus();

        var service = new GoalLifecycleService(
            manager,
            NullLogger.Instance,
            eventBus: eventBus);

        var pipeline = CreatePipeline("test-goal-1");
        pipeline.AdvanceTo(GoalPhase.Done);

        await service.FinalizeGoalAsync(
            pipeline,
            GoalStatus.Completed,
            failureReason: null,
            mergeCommitHash: "abc123",
            TestContext.Current.CancellationToken);

        Assert.Single(eventBus.Published);
        var evt = eventBus.Published[0];
        Assert.Equal(EventType.GoalCompleted, evt.Type);
        Assert.Equal("test-goal-1", evt.GoalId);
        Assert.Equal("Goal merged successfully", evt.Message);
        // Status update must have been called (event published after persistence).
        Assert.Single(store.Updates);
        Assert.Equal(GoalStatus.Completed, store.Updates[0].Status);
    }

    [Fact]
    public async Task FinalizeGoalAsync_Failed_PublishesGoalFailedWithGoalIdAndReason()
    {
        var store = new FakeGoalStore();
        store.AddGoal(new Goal { Id = "failed-goal", Description = "Test goal" });
        var manager = new GoalManager();
        manager.AddSource(store);
        var eventBus = new RecordingEventBus();

        var service = new GoalLifecycleService(
            manager,
            NullLogger.Instance,
            eventBus: eventBus);

        var pipeline = CreatePipeline("failed-goal");
        pipeline.AdvanceTo(GoalPhase.Failed);

        await service.FinalizeGoalAsync(
            pipeline,
            GoalStatus.Failed,
            failureReason: "Build failed after 3 retries",
            mergeCommitHash: null,
            TestContext.Current.CancellationToken);

        Assert.Single(eventBus.Published);
        var evt = eventBus.Published[0];
        Assert.Equal(EventType.GoalFailed, evt.Type);
        Assert.Equal("failed-goal", evt.GoalId);
        Assert.Equal("Build failed after 3 retries", evt.Message);
        Assert.Single(store.Updates);
        Assert.Equal(GoalStatus.Failed, store.Updates[0].Status);
    }

    [Fact]
    public async Task FinalizeGoalAsync_WithNullEventBus_DoesNotPublishAndStillCompletes()
    {
        var store = new FakeGoalStore();
        store.AddGoal(new Goal { Id = "null-bus-goal", Description = "Test goal" });
        var manager = new GoalManager();
        manager.AddSource(store);

        // No eventBus — backward compatibility.
        var service = new GoalLifecycleService(manager, NullLogger.Instance);

        var pipeline = CreatePipeline("null-bus-goal");
        pipeline.AdvanceTo(GoalPhase.Done);

        await service.FinalizeGoalAsync(
            pipeline,
            GoalStatus.Completed,
            failureReason: null,
            mergeCommitHash: "abc123",
            TestContext.Current.CancellationToken);

        // The goal status must still be persisted.
        Assert.Single(store.Updates);
        Assert.Equal(GoalStatus.Completed, store.Updates[0].Status);
        // No event bus means no crash — the test passing proves backward compatibility.
    }

    [Fact]
    public async Task FinalizeGoalAsync_WhenUpdateGoalStatusThrows_DoesNotPublishEventAndPropagatesException()
    {
        var store = new FakeGoalStore();
        store.AddGoal(new Goal { Id = "throw-goal", Description = "Test goal" });
        var throwEx = new InvalidOperationException("DB connection lost");
        store.ThrowOnUpdateStatus = throwEx;

        var manager = new GoalManager();
        manager.AddSource(store);
        var eventBus = new RecordingEventBus();

        var service = new GoalLifecycleService(
            manager,
            NullLogger.Instance,
            eventBus: eventBus);

        var pipeline = CreatePipeline("throw-goal");
        pipeline.AdvanceTo(GoalPhase.Failed);

        // The exception must propagate (pre-existing semantics) — no silent swallow.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.FinalizeGoalAsync(
                pipeline,
                GoalStatus.Failed,
                failureReason: "some reason",
                mergeCommitHash: null,
                TestContext.Current.CancellationToken));

        Assert.Same(throwEx, ex);
        // No event must have been published since the status persistence failed.
        Assert.Empty(eventBus.Published);
    }

    // ── API producer: IssueRaised (POST /api/issues) ────────────────────────

    [Fact]
    public async Task PostIssue_PublishesIssueRaisedWithIdAndTitle()
    {
        var eventBus = new RecordingEventBus();
        using var factory = CreateIssueFactory(eventBus);
        using var client = factory.CreateClient();

        var issueId = "test-issue-" + Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsync("/api/issues",
            JsonBody(new
            {
                id = issueId,
                type = "bug",
                title = "My test issue",
                description = "Test description",
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.IssueRaised, evt.Type);
        Assert.Equal(issueId, evt.IssueId);
        Assert.Equal("My test issue", evt.Message);
    }

    // ── API producer: IssueResolved (PATCH /api/issues/{id}) ────────────────

    [Fact]
    public async Task PatchIssue_Resolved_FromOpen_PublishesIssueResolved()
    {
        var eventBus = new RecordingEventBus();
        using var factory = CreateIssueFactory(eventBus);
        using var client = factory.CreateClient();

        // Create an issue (open by default).
        var issueId = "test-issue-" + Guid.NewGuid().ToString("N")[..8];
        var createResponse = await client.PostAsync("/api/issues",
            JsonBody(new
            {
                id = issueId,
                type = "bug",
                title = "Test issue",
                description = "Test description",
            }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        eventBus.Published.Clear();

        // PATCH to resolved — should publish IssueResolved.
        var response = await client.PatchAsync($"/api/issues/{issueId}",
            JsonBody(new { status = "resolved" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.IssueResolved, evt.Type);
        Assert.Equal(issueId, evt.IssueId);
        Assert.Contains(issueId, evt.Message);
    }

    [Fact]
    public async Task PatchIssue_Resolved_FromAlreadyResolved_DoesNotPublishIssueResolved()
    {
        var eventBus = new RecordingEventBus();
        using var factory = CreateIssueFactory(eventBus);
        using var client = factory.CreateClient();

        // Create an issue (open by default).
        var issueId = "test-issue-" + Guid.NewGuid().ToString("N")[..8];
        var createResponse = await client.PostAsync("/api/issues",
            JsonBody(new
            {
                id = issueId,
                type = "bug",
                title = "Test issue",
                description = "Test description",
            }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // First transition: open → resolved → publishes IssueResolved.
        var firstPatch = await client.PatchAsync($"/api/issues/{issueId}",
            JsonBody(new { status = "resolved" }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, firstPatch.StatusCode);
        Assert.Single(eventBus.Published, e => e.Type == EventType.IssueResolved);

        // Second transition: resolved → resolved → must NOT publish again.
        eventBus.Published.Clear();
        var secondPatch = await client.PatchAsync($"/api/issues/{issueId}",
            JsonBody(new { status = "resolved" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, secondPatch.StatusCode);
        Assert.DoesNotContain(eventBus.Published, e => e.Type == EventType.IssueResolved);
    }

    [Fact]
    public async Task PatchIssue_StatusOpen_DoesNotPublishIssueResolved()
    {
        var eventBus = new RecordingEventBus();
        using var factory = CreateIssueFactory(eventBus);
        using var client = factory.CreateClient();

        // Create an issue (open by default).
        var issueId = "test-issue-" + Guid.NewGuid().ToString("N")[..8];
        var createResponse = await client.PostAsync("/api/issues",
            JsonBody(new
            {
                id = issueId,
                type = "bug",
                title = "Test issue",
                description = "Test description",
            }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        eventBus.Published.Clear();

        // PATCH with status=open — non-terminal, must NOT publish IssueResolved.
        var response = await client.PatchAsync($"/api/issues/{issueId}",
            JsonBody(new { status = "open" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(eventBus.Published, e => e.Type == EventType.IssueResolved);
    }

    [Fact]
    public async Task PatchIssue_Resolved_WithLinkedGoalId_PublishesIssueResolvedWithGoalId()
    {
        var eventBus = new RecordingEventBus();
        using var factory = CreateIssueFactory(eventBus);
        using var client = factory.CreateClient();

        // Create an issue (open by default, no linked goal).
        var issueId = "test-issue-" + Guid.NewGuid().ToString("N")[..8];
        var createResponse = await client.PostAsync("/api/issues",
            JsonBody(new
            {
                id = issueId,
                type = "bug",
                title = "Linked goal issue",
                description = "Test description",
            }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // Set a linked goal ID via PATCH (non-empty string sets it).
        var linkedGoalId = "goal-" + Guid.NewGuid().ToString("N")[..8];
        var linkResponse = await client.PatchAsync($"/api/issues/{issueId}",
            JsonBody(new { linkedGoalId }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, linkResponse.StatusCode);
        eventBus.Published.Clear();

        // Now transition to resolved — should publish IssueResolved with the GoalId set.
        var resolveResponse = await client.PatchAsync($"/api/issues/{issueId}",
            JsonBody(new { status = "resolved" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);

        var evt = Assert.Single(eventBus.Published, e => e.Type == EventType.IssueResolved);
        Assert.Equal(issueId, evt.IssueId);
        Assert.Equal(linkedGoalId, evt.GoalId);
    }

    // ── API producer: ReleaseCompleted (PATCH /api/releases/{id}/status) ────

    [Fact]
    public async Task PatchReleaseStatus_Released_PublishesReleaseCompleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
        var eventBus = new RecordingEventBus();
        var baseFactory = new HiveTestFactory { MockRepoManager = fake };

        var config = new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "repo1",
                    Url = "https://github.com/test/repo1",
                    DefaultBranch = "main",
                    Release = new ReleaseRepoConfig { MergeTo = "main", TagBranch = "main" },
                },
            ],
        };

        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace IEventBus with the recording bus.
                var existingBus = services.SingleOrDefault(d => d.ServiceType == typeof(IEventBus));
                if (existingBus is not null)
                    services.Remove(existingBus);
                services.AddSingleton<IEventBus>(eventBus);

                // Replace HiveConfigFile.
                var existingConfig = services.SingleOrDefault(d => d.ServiceType == typeof(HiveConfigFile));
                if (existingConfig is not null)
                    services.Remove(existingConfig);
                services.AddSingleton(config);

                // Register ReleaseExecutionService using the same config and mock repo manager.
                services.AddSingleton(sp => new ReleaseExecutionService(
                    sp.GetRequiredService<IGoalStore>(),
                    config,
                    sp.GetRequiredService<IBrainRepoManager>(),
                    sp.GetRequiredService<ILogger<ReleaseExecutionService>>()));
            });
        });
        using var client = factory.CreateClient();

        // Seed a release in Planning status with a completed goal.
        var releaseId = "test-release-" + Guid.NewGuid().ToString("N")[..8];
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateReleaseAsync(new Release
            {
                Id = releaseId,
                Tag = "v1.0.0",
                RepositoryNames = ["repo1"],
            }, ct);
            await store.CreateGoalAsync(
                new Goal
                {
                    Id = "goal-" + Guid.NewGuid().ToString("N")[..8],
                    Description = "Test",
                    ReleaseId = releaseId,
                    Status = GoalStatus.Completed,
                }, ct);
        }

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status",
            JsonBody(new { status = "Released" }),
            ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.ReleaseCompleted, evt.Type);
        Assert.Equal(releaseId, evt.ReleaseId);
        Assert.Contains("v1.0.0", evt.Message);
    }

    // ── GoalDispatchService producer: GoalDispatched ────────────────────────

    [Fact]
    public async Task DispatchNextGoal_WithActiveTask_PublishesGoalDispatched()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "test-repo",
                    Url = "https://github.com/test/repo",
                    DefaultBranch = "main",
                },
            ],
            // Slice 3b: worker roles need configured models to dispatch.
            Workers =
            {
                ["coder"] = new WorkerConfig { Model = "coder-model" },
            },
        };
        var goal = new Goal
        {
            Id = "goal-dispatch-" + Guid.NewGuid().ToString("N")[..8],
            Description = "Implement feature X",
            RepositoryNames = ["test-repo"],
        };
        var eventBus = new RecordingEventBus();
        var service = CreateDispatchService(goal, config, eventBus, out _);

        await service.DispatchNextGoalAsync(ct);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.GoalDispatched, evt.Type);
        Assert.Equal(goal.Id, evt.GoalId);
        Assert.Equal(goal.Description, evt.Message);
    }

    /// <summary>
    /// Removal-proof guard test for <c>if (!string.IsNullOrEmpty(pipeline.ActiveTaskId))</c>
    /// in <see cref="GoalDispatchService.DispatchNextGoalAsync"/>.
    /// <para>
    /// The dispatch must reach <c>TaskDispatchService.DispatchToRole</c> and have it return
    /// NORMALLY without creating a task. That is the only state in which the guard is load
    /// bearing: execution continues past <c>DispatchToRole</c> to the publish statement, and
    /// only the <c>ActiveTaskId</c> check stops a false <c>GoalDispatched</c>.
    /// </para>
    /// <para>
    /// Setup: the config initially contains the goal's repository so the PRELIMINARY
    /// <c>ResolveRepositories</c> call in <c>DispatchNextGoalAsync</c> (the Brain-repo-ensure
    /// step) succeeds and the pipeline is created. The repository is then removed from the
    /// config during prompt crafting — the last Brain hook before dispatch — so the SECOND
    /// <c>ResolveRepositories</c> call, made inside <c>DispatchToRole</c>, throws. The real
    /// <c>DispatchToRole</c> catches that <see cref="InvalidOperationException"/>, calls
    /// <c>MarkGoalFailedAsync</c> (publishing <c>GoalFailed</c>), and returns without ever
    /// calling <c>SetActiveTask</c>.
    /// </para>
    /// <para>
    /// Asserting <c>GoalFailed</c> IS published proves the run actually reached
    /// <c>DispatchToRole</c> (a vacuous early throw could not produce it). Asserting
    /// <c>GoalDispatched</c> is absent then proves the guard did the work: deleting the
    /// guard publishes <c>GoalDispatched</c> here and fails this test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task DispatchNextGoal_DispatchFailure_DoesNotPublishGoalDispatched()
    {
        var ct = TestContext.Current.CancellationToken;

        // The repository IS configured up front, so the preliminary ResolveRepositories
        // call in DispatchNextGoalAsync succeeds and the pipeline is created normally.
        var config = new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "test-repo",
                    Url = "https://github.com/test/repo",
                    DefaultBranch = "main",
                },
            ],
            // Slice 3b: worker roles need configured models to dispatch.
            Workers =
            {
                ["coder"] = new WorkerConfig { Model = "coder-model" },
            },
        };
        var goal = new Goal
        {
            Id = "goal-dispatch-fail-" + Guid.NewGuid().ToString("N")[..8],
            Description = "Implement feature X",
            RepositoryNames = ["test-repo"],
        };
        var eventBus = new RecordingEventBus();

        // Drop the repository from the config during prompt crafting — the last Brain call
        // before DispatchToRole. The second ResolveRepositories (inside DispatchToRole) then
        // throws, DispatchToRole catches it, marks the goal failed, and returns with no task.
        var service = CreateDispatchService(goal, config, eventBus, out var pipelineManager,
            onCraftPrompt: () => config.Repositories.Clear());

        // DispatchToRole swallows the repository error, so the dispatch returns normally.
        await service.DispatchNextGoalAsync(ct);

        // The pipeline exists and never received a task — DispatchToRole returned before
        // SetActiveTask. This is the exact state the guard exists to handle.
        var pipeline = pipelineManager.GetByGoalId(goal.Id);
        Assert.NotNull(pipeline);
        Assert.Null(pipeline.ActiveTaskId);

        // GoalFailed proves execution genuinely reached DispatchToRole and went through
        // lifecycle failure handling — it cannot be produced by an early throw.
        Assert.Contains(eventBus.Published, e => e.Type == EventType.GoalFailed);

        // The guard suppressed GoalDispatched even though execution continued past
        // DispatchToRole to the publish statement. Removing the guard fails this assertion.
        Assert.DoesNotContain(eventBus.Published, e => e.Type == EventType.GoalDispatched);
    }

    [Fact]
    public async Task DispatchNextGoal_GoalDispatched_MessageMatchesPipelineDescription()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "test-repo",
                    Url = "https://github.com/test/repo",
                    DefaultBranch = "main",
                },
            ],
            // Slice 3b: worker roles need configured models to dispatch.
            Workers =
            {
                ["coder"] = new WorkerConfig { Model = "coder-model" },
            },
        };
        // Use a distinctive description so we can verify it propagates verbatim.
        var description = "Distinctive goal description for message matching " + Guid.NewGuid().ToString("N")[..8];
        var goal = new Goal
        {
            Id = "goal-dispatch-msg-" + Guid.NewGuid().ToString("N")[..8],
            Description = description,
            RepositoryNames = ["test-repo"],
        };
        var eventBus = new RecordingEventBus();
        var service = CreateDispatchService(goal, config, eventBus, out var pipelineManager);

        await service.DispatchNextGoalAsync(ct);

        // Retrieve the pipeline so we can assert against the actual Description field.
        var pipeline = pipelineManager.GetByGoalId(goal.Id);
        Assert.NotNull(pipeline);

        var evt = Assert.Single(eventBus.Published, e => e.Type == EventType.GoalDispatched);
        Assert.Equal(pipeline.Description, evt.Message);
        Assert.Equal(description, evt.Message);
        Assert.Equal(goal.Id, evt.GoalId);
    }

    // ── HiveOrchestratorService producer: raise_issue ──────────────────────

    [Fact]
    public async Task RaiseIssueTool_PublishesIssueRaisedWithIssueIdAndGoalId()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventBus = new RecordingEventBus();
        var (service, pipelineManager, pool) = CreateOrchestratorService(eventBus);

        var worker = pool.RegisterWorker("worker-evt-1", []);
        worker.Role = DomainWorkerRole.Coder;
        var pipeline = CreatePipelineForTool(pipelineManager, "goal-evt-1", "task-evt-1");

        await service.HandleToolCallRequestAsync(
            worker,
            new ToolCallRequest
            {
                RequestId = "req-evt-1",
                TaskId = "task-evt-1",
                ToolName = "raise_issue",
                ArgumentsJson = """{"type":"bug","title":"Event Bus Title","description":"Event Bus Desc","severity":"low"}""",
            },
            ct);

        var response = await worker.MessageChannel.Reader.ReadAsync(ct);
        Assert.True(response.ToolResponse.Success);

        var evt = Assert.Single(eventBus.Published);
        Assert.Equal(EventType.IssueRaised, evt.Type);
        Assert.Equal("Event Bus Title", evt.Message);
        Assert.NotNull(evt.IssueId);
        Assert.Equal("goal-evt-1", evt.GoalId);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="HiveOrchestratorService"/> with a real <see cref="WorkerPool"/>,
    /// <see cref="GoalPipelineManager"/> and the given <see cref="RecordingEventBus"/>
    /// wired as the event bus.
    /// </summary>
    private static (HiveOrchestratorService Service, GoalPipelineManager PipelineManager, WorkerPool Pool)
        CreateOrchestratorService(RecordingEventBus eventBus)
    {
        var pool = new WorkerPool();
        var taskQueue = new TaskQueue();
        var pipelineManager = new GoalPipelineManager();
        var completionNotifier = new TaskCompletionNotifier();
        var goalManager = new GoalManager();
        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            taskQueue,
            new GrpcWorkerGateway(pool),
            completionNotifier,
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance));

        var service = new HiveOrchestratorService(
            pool,
            taskQueue,
            pipelineManager,
            completionNotifier,
            dispatcher,
            NullLogger<HiveOrchestratorService>.Instance,
            issueStore: new FakeIssueStore(),
            eventBus: eventBus);

        return (service, pipelineManager, pool);
    }

    private static GoalPipeline CreatePipelineForTool(
        GoalPipelineManager manager, string goalId, string taskId)
    {
        var goal = new Goal { Id = goalId, Description = "Test goal" };
        var pipeline = manager.CreatePipeline(goal);
        manager.RegisterTask(taskId, goalId);
        pipeline.SetActiveTask(taskId);
        return pipeline;
    }

    private static StringContent JsonBody(object data) =>
        new(JsonSerializer.Serialize(data, JsonOpts), Encoding.UTF8, "application/json");

    /// <summary>
    /// Creates a factory whose <see cref="IEventBus"/> is replaced with the given
    /// <see cref="RecordingEventBus"/> and whose <see cref="IIssueStore"/> is backed
    /// by a fresh in-memory SQLite database for test isolation.
    /// </summary>
    private WebApplicationFactory<Program> CreateIssueFactory(RecordingEventBus eventBus)
    {
        return _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace IEventBus with the recording bus.
                var existingBus = services.SingleOrDefault(d => d.ServiceType == typeof(IEventBus));
                if (existingBus is not null)
                    services.Remove(existingBus);
                services.AddSingleton<IEventBus>(eventBus);

                // Fresh in-memory IIssueStore.
                var issueStoreDescriptors = services
                    .Where(d => d.ServiceType == typeof(IIssueStore))
                    .ToList();
                foreach (var d in issueStoreDescriptors)
                    services.Remove(d);

                services.AddSingleton<IIssueStore>(_ =>
                    new IssueStore(
                        CopilotHiveDbContext.CreateInMemory(),
                        NullLogger<IssueStore>.Instance));
            });
        });
    }

    /// <summary>
    /// Builds a <see cref="GoalDispatchService"/> with a real <see cref="TaskDispatchService"/>
    /// and a fake Brain. The goal is registered with a <see cref="GoalManager"/> so status
    /// updates (InProgress / Failed) succeed. The pipeline manager is exposed for assertions.
    /// The recording bus is wired into BOTH the dispatch service and the
    /// <see cref="GoalLifecycleService"/>, so lifecycle events (<c>GoalFailed</c>) and dispatch
    /// events (<c>GoalDispatched</c>) land in the same list and can be asserted together.
    /// </summary>
    /// <param name="goal">The pending goal to dispatch.</param>
    /// <param name="config">Hive configuration used for repository resolution.</param>
    /// <param name="eventBus">Recording bus injected into the dispatch and lifecycle services.</param>
    /// <param name="pipelineManager">Receives the pipeline manager used by the service.</param>
    /// <param name="onCraftPrompt">
    /// Optional hook invoked by the fake Brain when the worker prompt is crafted — the last
    /// Brain call before <c>DispatchToRole</c>. Lets a test mutate state (e.g. the repository
    /// config) so the dispatch step behaves differently from the preliminary resolution step.
    /// </param>
    private static GoalDispatchService CreateDispatchService(
        Goal goal,
        HiveConfigFile config,
        RecordingEventBus eventBus,
        out GoalPipelineManager pipelineManager,
        Action? onCraftPrompt = null)
    {
        var goalSource = new ProducerFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        // Populate the internal goal→source map so UpdateGoalStatusAsync can find the goal.
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        pipelineManager = new GoalPipelineManager();
        var taskQueue = new TaskQueue();
        var workerGateway = new GrpcWorkerGateway(new WorkerPool());
        var brain = new ProducerFakeBrain(onCraftPrompt);

        var clarificationHandler = new ClarificationHandler(brain, null, null, NullLogger.Instance);

        var lifecycleService = new GoalLifecycleService(
            goalManager, NullLogger.Instance, eventBus: eventBus);
        var maintenance = new DispatcherMaintenance(
            pipelineManager, goalManager, taskQueue, workerGateway,
            brain: null,
            agentsManager: null,
            configRepo: null,
            new ConcurrentQueue<string>(),
            NullLogger.Instance,
            config: config);
        var taskBuilder = new TaskBuilder(new BranchCoordinator());

        var taskDispatchService = new TaskDispatchService(
            taskQueue, workerGateway, taskBuilder, config,
            NullLogger<TaskDispatchService>.Instance, pipelineManager, lifecycleService, maintenance);

        return new GoalDispatchService(
            goalManager, pipelineManager, brain, config,
            taskDispatchService, clarificationHandler, null,
            null, null, null, NullLogger.Instance, eventBus);
    }

    /// <summary>Minimal <see cref="IGoalSource"/> that returns a single pre-configured goal.</summary>
    private sealed class ProducerFakeGoalSource(Goal goal) : IGoalSource
    {
        public string Name => "producer-fake";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([goal]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Minimal <see cref="IDistributedBrain"/> stub that returns a default plan and prompt.
    /// <paramref name="onCraftPrompt"/> is invoked from <see cref="CraftPromptAsync"/> — the last
    /// Brain call before the dispatch step — so tests can mutate state between the preliminary
    /// repository resolution and the resolution performed inside <c>DispatchToRole</c>.
    /// </summary>
    private sealed class ProducerFakeBrain(Action? onCraftPrompt = null) : IDistributedBrain
    {
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default)
        {
            onCraftPrompt?.Invoke();
            return Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));
        }

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("proceed"));

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public bool GoalSessionExists(string goalId) => false;

        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

        public BrainStats? GetStats() => null;
    }
}
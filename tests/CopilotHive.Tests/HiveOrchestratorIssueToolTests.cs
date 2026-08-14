using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;

using DomainWorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests;

/// <summary>
/// Unit tests for the <c>raise_issue</c> tool handler on
/// <see cref="HiveOrchestratorService.HandleToolCallRequestAsync"/> and the
/// <see cref="IssueIdGenerator"/> helper. Verifies issue creation with source metadata,
/// input validation, graceful degradation when the store is unavailable, duplicate-ID
/// retry behaviour, and slug generation/collision handling.
/// </summary>
public sealed class HiveOrchestratorIssueToolTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (HiveOrchestratorService service, GoalPipelineManager pipelineManager, WorkerPool pool)
        CreateService(IIssueStore? issueStore = null)
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
            issueStore: issueStore);

        return (service, pipelineManager, pool);
    }

    private static GoalPipeline CreatePipelineForTask(
        GoalPipelineManager manager, string goalId, string taskId, params string[] repositoryNames)
    {
        var goal = new Goal { Id = goalId, Description = "Test goal" };
        goal.RepositoryNames.AddRange(repositoryNames);
        var pipeline = manager.CreatePipeline(goal);
        manager.RegisterTask(taskId, goalId);
        pipeline.SetActiveTask(taskId);
        return pipeline;
    }

    /// <summary>
    /// Builds arguments JSON including only the non-null fields. Pass <c>null</c> for a
    /// field to omit it entirely from the JSON (simulating an absent property).
    /// </summary>
    private static string SerializeArgs(string? type = null, string? title = null, string? description = null, string? severity = null)
    {
        var parts = new List<string>();
        if (type is not null) parts.Add($"\"type\":\"{type}\"");
        if (title is not null) parts.Add($"\"title\":\"{title}\"");
        if (description is not null) parts.Add($"\"description\":\"{description}\"");
        if (severity is not null) parts.Add($"\"severity\":\"{severity}\"");
        return "{" + string.Join(",", parts) + "}";
    }

    /// <summary>Builds arguments JSON with the given field explicitly set to JSON null.</summary>
    private static string SerializeArgsWithNullField(string field, string type, string title, string description, string? severity = null)
    {
        var parts = new List<string>
        {
            $"\"type\":\"{type}\"",
            $"\"title\":\"{title}\"",
            $"\"description\":\"{description}\"",
        };
        if (severity is not null)
            parts.Add($"\"severity\":\"{severity}\"");
        // Replace the target field's value with null.
        var idx = parts.FindIndex(p => p.StartsWith($"\"{field}\":", StringComparison.Ordinal));
        parts[idx] = $"\"{field}\":null";
        return "{" + string.Join(",", parts) + "}";
    }

    private static async Task<(bool Success, string ResultJson)> SendRaiseIssueAsync(
        HiveOrchestratorService service, ConnectedWorker worker, string requestId, string argsJson)
    {
        await service.HandleToolCallRequestAsync(
            worker,
            new ToolCallRequest
            {
                RequestId = requestId,
                TaskId = "task-1",
                ToolName = "raise_issue",
                ArgumentsJson = argsJson,
            },
            CancellationToken.None);

        var response = await worker.MessageChannel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        return (response.ToolResponse.Success, response.ToolResponse.ResultJson);
    }

    // ── Test 1: issue creation with source metadata ───────────────────────────

    [Fact]
    public async Task RaiseIssue_WithValidArgs_CreatesIssueWithSourceMetadata()
    {
        var store = new FakeIssueStore();
        var (service, pipelineManager, pool) = CreateService(store);
        var worker = pool.RegisterWorker("worker-1", []);
        worker.Role = DomainWorkerRole.Reviewer;
        var pipeline = CreatePipelineForTask(pipelineManager, "goal-1", "task-1", "RepoA", "RepoB");

        // Set a known non-default iteration: consume the budget twice → Iteration == 3.
        pipeline.IterationBudget.TryConsume();
        pipeline.IterationBudget.TryConsume();
        Assert.Equal(3, pipeline.Iteration);

        var (success, resultJson) = await SendRaiseIssueAsync(
            service, worker, "req-1",
            SerializeArgs("code_quality", "Code quality in parser", "The parser has naming issues", "high"));

        Assert.True(success);
        Assert.Contains("\"acknowledged\":true", resultJson);
        Assert.Contains("\"issue_id\":\"code-quality-in-parser\"", resultJson);

        var issue = Assert.Single(store.Issues.Values);
        Assert.Equal("code-quality-in-parser", issue.Id);
        Assert.Equal(IssueType.CodeQuality, issue.Type);
        Assert.Equal("Code quality in parser", issue.Title);
        Assert.Equal("The parser has naming issues", issue.Description);
        Assert.Equal(IssueSeverity.High, issue.Severity);
        Assert.Equal(IssueStatus.Open, issue.Status);
        Assert.Equal(["RepoA", "RepoB"], issue.RepositoryNames);
        Assert.Equal("goal-1", issue.SourceGoalId);
        Assert.Equal("reviewer", issue.SourceRole);
        Assert.Equal(3, issue.SourceIteration);
    }

    // ── Test 2: type validation ───────────────────────────────────────────────

    [Fact]
    public async Task RaiseIssue_WithInvalidType_ReturnsErrorJson()
    {
        var store = new FakeIssueStore();
        var (service, _, pool) = CreateService(store);
        var worker = pool.RegisterWorker("worker-1", []);
        worker.Role = DomainWorkerRole.Coder;

        var (success, resultJson) = await SendRaiseIssueAsync(
            service, worker, "req-1",
            SerializeArgs("invalid_type", "Some title", "Some description", "low"));

        Assert.True(success);
        using var json = System.Text.Json.JsonDocument.Parse(resultJson);
        Assert.Equal("Unknown issue type 'invalid_type'", json.RootElement.GetProperty("error").GetString());
        Assert.Empty(store.Issues);
    }

    // ── Test 3: severity validation ───────────────────────────────────────────

    [Fact]
    public async Task RaiseIssue_WithInvalidSeverity_ReturnsErrorJson()
    {
        var store = new FakeIssueStore();
        var (service, _, pool) = CreateService(store);
        var worker = pool.RegisterWorker("worker-1", []);
        worker.Role = DomainWorkerRole.Coder;

        var (success, resultJson) = await SendRaiseIssueAsync(
            service, worker, "req-1",
            SerializeArgs("bug", "Some title", "Some description", "critical"));

        Assert.True(success);
        using var json = System.Text.Json.JsonDocument.Parse(resultJson);
        Assert.Equal("Unknown severity 'critical'", json.RootElement.GetProperty("error").GetString());
        Assert.Empty(store.Issues);
    }

    // ── Test 4: missing/blank required fields ────────────────────────────────

    [Fact]
    public async Task RaiseIssue_TypeAbsentFromJson_ReturnsErrorMentioningType()
    {
        var store = new FakeIssueStore();
        var (service, _, pool) = CreateService(store);
        var worker = pool.RegisterWorker("worker-1", []);
        worker.Role = DomainWorkerRole.Coder;

        // JSON omits "type" entirely.
        var (success, resultJson) = await SendRaiseIssueAsync(
            service, worker, "req-1",
            SerializeArgs(type: null, title: "Some title", description: "Some description", severity: "low"));

        Assert.True(success);
        using var json = System.Text.Json.JsonDocument.Parse(resultJson);
        Assert.Equal("Missing required field: type", json.RootElement.GetProperty("error").GetString());
        Assert.Empty(store.Issues);
    }

    [Fact]
    public async Task RaiseIssue_TypeNullInJson_ReturnsErrorMentioningType()
    {
        var store = new FakeIssueStore();
        var (service, _, pool) = CreateService(store);
        var worker = pool.RegisterWorker("worker-1", []);
        worker.Role = DomainWorkerRole.Coder;

        var (success, resultJson) = await SendRaiseIssueAsync(
            service, worker, "req-1",
            SerializeArgsWithNullField("type", "bug", "Some title", "Some description", "low"));

        Assert.True(success);
        using var json = System.Text.Json.JsonDocument.Parse(resultJson);
        Assert.Equal("Missing required field: type", json.RootElement.GetProperty("error").GetString());
        Assert.Empty(store.Issues);
    }

    [Fact]
    public async Task RaiseIssue_TypeWhitespace_ReturnsErrorMentioningType()
    {
        var store = new FakeIssueStore();
        var (service, _, pool) = CreateService(store);
        var worker = pool.RegisterWorker("worker-1", []);
        worker.Role = DomainWorkerRole.Coder;

        var (success, resultJson) = await SendRaiseIssueAsync(
            service, worker, "req-1",
            SerializeArgs("   ", "Some title", "Some description", "low"));

        Assert.True(success);
        using var json = System.Text.Json.JsonDocument.Parse(resultJson);
        Assert.Equal("Missing required field: type", json.RootElement.GetProperty("error").GetString());
        Assert.Empty(store.Issues);
    }

    [Fact]
    public async Task RaiseIssue_TitleAbsentFromJson_ReturnsErrorMentioningTitle()
    {
        var store = new FakeIssueStore();
        var (service, _, pool) = CreateService(store);
        var worker = pool.RegisterWorker("worker-1", []);
        worker.Role = DomainWorkerRole.Coder;

        // JSON omits "title" entirely.
        var (success, resultJson) = await SendRaiseIssueAsync(
            service, worker, "req-1",
            SerializeArgs(type: "bug", title: null, description: "Some description", severity: "low"));

        Assert.True(success);
        using var json = System.Text.Json.JsonDocument.Parse(resultJson);
        Assert.Equal("Missing required field: title", json.RootElement.GetProperty("error").GetString());
        Assert.Empty(store.Issues);
    }

    [Fact]
    public async Task RaiseIssue_TitleNullInJson_ReturnsErrorMentioningTitle()
    {
        var store = new FakeIssueStore();
        var (service, _, pool) = CreateService(store);
        var worker = pool.RegisterWorker("worker-1", []);
        worker.Role = DomainWorkerRole.Coder;

        var (success, resultJson) = await SendRaiseIssueAsync(
            service, worker, "req-1",
            SerializeArgsWithNullField("title", "bug", "Some title", "Some description", "low"));

        Assert.True(success);
        using var json = System.Text.Json.JsonDocument.Parse(resultJson);
        Assert.Equal("Missing required field: title", json.RootElement.GetProperty("error").GetString());
        Assert.Empty(store.Issues);
    }

    [Fact]
    public async Task RaiseIssue_TitleWhitespace_ReturnsErrorMentioningTitle()
    {
        var store = new FakeIssueStore();
        var (service, _, pool) = CreateService(store);
        var worker = pool.RegisterWorker("worker-1", []);
        worker.Role = DomainWorkerRole.Coder;

        var (success, resultJson) = await SendRaiseIssueAsync(
            service, worker, "req-1",
            SerializeArgs("bug", "   ", "Some description", "low"));

        Assert.True(success);
        using var json = System.Text.Json.JsonDocument.Parse(resultJson);
        Assert.Equal("Missing required field: title", json.RootElement.GetProperty("error").GetString());
        Assert.Empty(store.Issues);
    }

    [Fact]
    public async Task RaiseIssue_DescriptionAbsentFromJson_ReturnsErrorMentioningDescription()
    {
        var store = new FakeIssueStore();
        var (service, _, pool) = CreateService(store);
        var worker = pool.RegisterWorker("worker-1", []);
        worker.Role = DomainWorkerRole.Coder;

        // JSON omits "description" entirely.
        var (success, resultJson) = await SendRaiseIssueAsync(
            service, worker, "req-1",
            SerializeArgs(type: "bug", title: "Some title", description: null, severity: "low"));

        Assert.True(success);
        using var json = System.Text.Json.JsonDocument.Parse(resultJson);
        Assert.Equal("Missing required field: description", json.RootElement.GetProperty("error").GetString());
        Assert.Empty(store.Issues);
    }

    [Fact]
    public async Task RaiseIssue_DescriptionNullInJson_ReturnsErrorMentioningDescription()
    {
        var store = new FakeIssueStore();
        var (service, _, pool) = CreateService(store);
        var worker = pool.RegisterWorker("worker-1", []);
        worker.Role = DomainWorkerRole.Coder;

        var (success, resultJson) = await SendRaiseIssueAsync(
            service, worker, "req-1",
            SerializeArgsWithNullField("description", "bug", "Some title", "Some description", "low"));

        Assert.True(success);
        using var json = System.Text.Json.JsonDocument.Parse(resultJson);
        Assert.Equal("Missing required field: description", json.RootElement.GetProperty("error").GetString());
        Assert.Empty(store.Issues);
    }

    [Fact]
    public async Task RaiseIssue_DescriptionWhitespace_ReturnsErrorMentioningDescription()
    {
        var store = new FakeIssueStore();
        var (service, _, pool) = CreateService(store);
        var worker = pool.RegisterWorker("worker-1", []);
        worker.Role = DomainWorkerRole.Coder;

        var (success, resultJson) = await SendRaiseIssueAsync(
            service, worker, "req-1",
            SerializeArgs("bug", "Some title", "   ", "low"));

        Assert.True(success);
        using var json = System.Text.Json.JsonDocument.Parse(resultJson);
        Assert.Equal("Missing required field: description", json.RootElement.GetProperty("error").GetString());
        Assert.Empty(store.Issues);
    }

    // ── Test 5: null store → graceful error ──────────────────────────────────

    [Fact]
    public async Task RaiseIssue_WithNullStore_ReturnsGracefulError()
    {
        var (service, _, pool) = CreateService(issueStore: null);
        var worker = pool.RegisterWorker("worker-1", []);
        worker.Role = DomainWorkerRole.Coder;

        var (success, resultJson) = await SendRaiseIssueAsync(
            service, worker, "req-1",
            SerializeArgs("bug", "Some title", "Some description", "low"));

        Assert.True(success);
        Assert.Contains("Issue tracking not available", resultJson);
    }

    // ── Test 6: duplicate ID collision retry ──────────────────────────────────

    [Fact]
    public async Task RaiseIssue_WithDuplicateId_RetyWithGuidIdSucceeds()
    {
        var store = new FakeIssueStore(throwOnCreateOnce: true);
        var (service, pipelineManager, pool) = CreateService(store);
        var worker = pool.RegisterWorker("worker-1", []);
        worker.Role = DomainWorkerRole.Coder;
        var pipeline = CreatePipelineForTask(pipelineManager, "goal-1", "task-1", "RepoA", "RepoB");

        // Set a known non-default iteration: consume the budget twice → Iteration == 3.
        pipeline.IterationBudget.TryConsume();
        pipeline.IterationBudget.TryConsume();
        Assert.Equal(3, pipeline.Iteration);

        var (success, resultJson) = await SendRaiseIssueAsync(
            service, worker, "req-1",
            SerializeArgs("bug", "Parser crashes", "It crashes on empty input", "high"));

        Assert.True(success);
        Assert.Contains("\"acknowledged\":true", resultJson);

        var issue = Assert.Single(store.Issues.Values);
        Assert.StartsWith("issue-", issue.Id);
        Assert.NotEqual("parser-crashes", issue.Id);

        // The response issue_id must equal the actually persisted GUID-based ID.
        using var json = System.Text.Json.JsonDocument.Parse(resultJson);
        Assert.Equal(issue.Id, json.RootElement.GetProperty("issue_id").GetString());

        // The retried Issue must carry ALL fields — proving the rebuild preserves metadata.
        Assert.Equal(IssueType.Bug, issue.Type);
        Assert.Equal("Parser crashes", issue.Title);
        Assert.Equal("It crashes on empty input", issue.Description);
        Assert.Equal(IssueSeverity.High, issue.Severity);
        Assert.Equal(IssueStatus.Open, issue.Status);
        Assert.Equal(["RepoA", "RepoB"], issue.RepositoryNames);
        Assert.Equal("goal-1", issue.SourceGoalId);
        Assert.Equal("coder", issue.SourceRole);
        Assert.Equal(3, issue.SourceIteration);
    }

    // ── Test 7: true concurrent calls with collision race ────────────────────

    [Fact]
    public async Task RaiseIssue_ConcurrentCalls_SameTitle_BothSucceedWithDifferentIds()
    {
        var store = new GatedIssueStore();
        var (service, pipelineManager, pool) = CreateService(store);
        var worker = pool.RegisterWorker("worker-1", []);
        worker.Role = DomainWorkerRole.Coder;
        CreatePipelineForTask(pipelineManager, "goal-1", "task-1", "RepoA");

        // Both calls use the SAME title to exercise the collision race.
        var argsJson = SerializeArgs("bug", "Same title", "Same description", "low");

        // Start the first handler; it blocks inside GetIssueAsync until the second call starts.
        var taskA = service.HandleToolCallRequestAsync(
            worker,
            new ToolCallRequest { RequestId = "req-a", TaskId = "task-1", ToolName = "raise_issue", ArgumentsJson = argsJson },
            CancellationToken.None);

        // Wait until the first handler's GetIssueAsync is blocked, proving overlap.
        await store.FirstGetIssueCalled.Task.WaitAsync(TestContext.Current.CancellationToken);

        var taskB = service.HandleToolCallRequestAsync(
            worker,
            new ToolCallRequest { RequestId = "req-b", TaskId = "task-1", ToolName = "raise_issue", ArgumentsJson = argsJson },
            CancellationToken.None);

        await Task.WhenAll(taskA, taskB);

        // Both responses must succeed and acknowledge.
        var responseA = await worker.MessageChannel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var responseB = await worker.MessageChannel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.True(responseA.ToolResponse.Success);
        Assert.True(responseB.ToolResponse.Success);
        Assert.Contains("\"acknowledged\":true", responseA.ToolResponse.ResultJson);
        Assert.Contains("\"acknowledged\":true", responseB.ToolResponse.ResultJson);

        // Both issues persisted with DIFFERENT IDs (one slug, one GUID retry).
        Assert.Equal(2, store.Issues.Count);
        var ids = store.Issues.Keys.ToList();
        Assert.Equal(2, ids.Distinct().Count());
        Assert.Contains(ids, id => id == "same-title");
        Assert.Contains(ids, id => id.StartsWith("issue-", StringComparison.Ordinal));
    }

    // ── Test 8: snake_case alias mapping ──────────────────────────────────────

    [Theory]
    [InlineData("code_quality")]
    [InlineData("codequality")]
    public async Task RaiseIssue_TypeAlias_MapsToCodeQuality(string type)
    {
        var store = new FakeIssueStore();
        var (service, pipelineManager, pool) = CreateService(store);
        var worker = pool.RegisterWorker("worker-1", []);
        worker.Role = DomainWorkerRole.Coder;
        CreatePipelineForTask(pipelineManager, "goal-1", "task-1", "RepoA");

        var (success, _) = await SendRaiseIssueAsync(
            service, worker, "req-1",
            SerializeArgs(type, "Naming issues", "Variables are poorly named", "low"));

        Assert.True(success);
        var issue = Assert.Single(store.Issues.Values);
        Assert.Equal(IssueType.CodeQuality, issue.Type);
    }

    // ── Test 9: optional/default severity ─────────────────────────────────────

    [Fact]
    public async Task RaiseIssue_SeverityOmitted_DefaultsToLow()
    {
        var store = new FakeIssueStore();
        var (service, pipelineManager, pool) = CreateService(store);
        var worker = pool.RegisterWorker("worker-1", []);
        worker.Role = DomainWorkerRole.Coder;
        CreatePipelineForTask(pipelineManager, "goal-1", "task-1", "RepoA");

        // Omit severity entirely from the arguments JSON.
        var (success, _) = await SendRaiseIssueAsync(
            service, worker, "req-1",
            SerializeArgs("bug", "Some title", "Some description"));

        Assert.True(success);
        var issue = Assert.Single(store.Issues.Values);
        Assert.Equal(IssueSeverity.Low, issue.Severity);
    }

    // ── Test 10: malformed JSON → outer catch → Success = false ───────────────

    [Fact]
    public async Task RaiseIssue_WithMalformedJson_ReturnsSuccessFalse()
    {
        var store = new FakeIssueStore();
        var (service, _, pool) = CreateService(store);
        var worker = pool.RegisterWorker("worker-1", []);
        worker.Role = DomainWorkerRole.Coder;

        await service.HandleToolCallRequestAsync(
            worker,
            new ToolCallRequest
            {
                RequestId = "req-1",
                TaskId = "task-1",
                ToolName = "raise_issue",
                ArgumentsJson = "this is not valid json",
            },
            CancellationToken.None);

        var response = await worker.MessageChannel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.False(response.ToolResponse.Success);
        Assert.NotEmpty(response.ToolResponse.Error);
        Assert.Empty(store.Issues);
    }

    // ── Test 11: unexpected persistence error → outer catch → Success = false ─

    [Fact]
    public async Task RaiseIssue_WithPersistenceErrorOnRetry_ReturnsSuccessFalse()
    {
        var store = new FakeIssueStore(throwOnEveryCreate: true);
        var (service, pipelineManager, pool) = CreateService(store);
        var worker = pool.RegisterWorker("worker-1", []);
        worker.Role = DomainWorkerRole.Coder;
        CreatePipelineForTask(pipelineManager, "goal-1", "task-1", "RepoA");

        await service.HandleToolCallRequestAsync(
            worker,
            new ToolCallRequest
            {
                RequestId = "req-1",
                TaskId = "task-1",
                ToolName = "raise_issue",
                ArgumentsJson = SerializeArgs("bug", "Parser crashes", "It crashes on empty input", "high"),
            },
            CancellationToken.None);

        var response = await worker.MessageChannel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.False(response.ToolResponse.Success);
        Assert.NotEmpty(response.ToolResponse.Error);
        Assert.Empty(store.Issues);
    }

    // ── IssueIdGenerator tests ────────────────────────────────────────────────

    [Theory]
    [InlineData("Code quality in parser", "code-quality-in-parser")]
    [InlineData("Bug!!!", "bug")]
    [InlineData("A B", "a-b")]
    public async Task IssueIdGenerator_Slugify_ProducesExpectedSlug(string title, string expected)
    {
        var store = new FakeIssueStore();
        var result = await IssueIdGenerator.GenerateAsync(title, store, CancellationToken.None);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task IssueIdGenerator_WhitespaceTitle_FallsBackToGuid()
    {
        var store = new FakeIssueStore();
        var result = await IssueIdGenerator.GenerateAsync("   ", store, CancellationToken.None);
        Assert.StartsWith("issue-", result);
        Assert.Equal(32 + 6, result.Length);
    }

    [Fact]
    public async Task IssueIdGenerator_EmptyTitle_FallsBackToGuid()
    {
        var store = new FakeIssueStore();
        var result = await IssueIdGenerator.GenerateAsync("", store, CancellationToken.None);
        Assert.StartsWith("issue-", result);
    }

    [Fact]
    public async Task IssueIdGenerator_NullTitle_FallsBackToGuid()
    {
        var store = new FakeIssueStore();
        var result = await IssueIdGenerator.GenerateAsync(null, store, CancellationToken.None);
        Assert.StartsWith("issue-", result);
    }

    /// <summary>
    /// For each occupied-candidate count N from 1 to 9, pre-populate the store with
    /// <c>slug</c>, <c>slug-2</c>, ..., <c>slug-N</c> and assert the generator returns
    /// <c>slug-{N+1}</c>. This proves every suffix from <c>slug-2</c> through
    /// <c>slug-10</c> is correctly generated.
    /// </summary>
    [Theory]
    [InlineData(1, "slug-2")]
    [InlineData(2, "slug-3")]
    [InlineData(3, "slug-4")]
    [InlineData(4, "slug-5")]
    [InlineData(5, "slug-6")]
    [InlineData(6, "slug-7")]
    [InlineData(7, "slug-8")]
    [InlineData(8, "slug-9")]
    [InlineData(9, "slug-10")]
    public async Task IssueIdGenerator_Collision_ProducesNextSuffix(int occupiedCount, string expected)
    {
        var store = new FakeIssueStore();
        for (var i = 0; i < occupiedCount; i++)
        {
            var id = i == 0 ? "slug" : $"slug-{i + 1}";
            store.Issues[id] = new Issue
            {
                Id = id,
                Title = "Existing",
                Description = "Existing",
                Type = IssueType.Bug,
                Severity = IssueSeverity.Low,
                Status = IssueStatus.Open,
            };
        }

        var result = await IssueIdGenerator.GenerateAsync("slug", store, CancellationToken.None);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task IssueIdGenerator_Exhaustion_FallsBackToGuid()
    {
        // Pre-populate "slug" and "slug-2" through "slug-10" — all 10 candidates are taken.
        var store = new FakeIssueStore();
        for (var i = 0; i < 10; i++)
        {
            var id = i == 0 ? "slug" : $"slug-{i + 1}";
            store.Issues[id] = new Issue
            {
                Id = id,
                Title = "Existing",
                Description = "Existing",
                Type = IssueType.Bug,
                Severity = IssueSeverity.Low,
                Status = IssueStatus.Open,
            };
        }

        var result = await IssueIdGenerator.GenerateAsync("slug", store, CancellationToken.None);
        Assert.StartsWith("issue-", result);
    }

    [Fact]
    public async Task IssueIdGenerator_UsesGetIssueAsyncForCollisionCheck_NotCreateIssueAsync()
    {
        // Removal-proof: if the generator ever probed collisions via CreateIssueAsync,
        // this store (which throws on every create) would make generation fail.
        var store = new FakeIssueStore(throwOnEveryCreate: true);

        var result = await IssueIdGenerator.GenerateAsync("fresh-title", store, CancellationToken.None);

        Assert.Equal("fresh-title", result);
        Assert.Empty(store.Issues);
    }

    // ── FakeIssueStore ────────────────────────────────────────────────────────

    /// <summary>
    /// In-memory <see cref="IIssueStore"/> fake for handler tests. Optionally throws
    /// <see cref="InvalidOperationException"/> on the first create (to simulate a
    /// duplicate-ID race between the slug check and the insert) or on every create
    /// (to simulate an unexpected persistence failure).
    /// </summary>
    private sealed class FakeIssueStore : IIssueStore
    {
        private readonly bool _throwOnCreateOnce;
        private readonly bool _throwOnEveryCreate;
        private bool _hasThrown;

        public Dictionary<string, Issue> Issues { get; } = new();

        public FakeIssueStore(bool throwOnCreateOnce = false, bool throwOnEveryCreate = false)
        {
            _throwOnCreateOnce = throwOnCreateOnce;
            _throwOnEveryCreate = throwOnEveryCreate;
        }

        public Task<IReadOnlyList<Issue>> GetAllIssuesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Issue>>(Issues.Values.ToList());

        public Task<IReadOnlyList<Issue>> GetIssuesAsync(
            IssueStatus? status = null,
            IssueType? type = null,
            IssueSeverity? severity = null,
            string? repository = null,
            string? sourceGoalId = null,
            CancellationToken ct = default)
        {
            var query = Issues.Values.AsEnumerable();
            if (status.HasValue) query = query.Where(i => i.Status == status.Value);
            if (type.HasValue) query = query.Where(i => i.Type == type.Value);
            if (severity.HasValue) query = query.Where(i => i.Severity == severity.Value);
            if (sourceGoalId is not null) query = query.Where(i => i.SourceGoalId == sourceGoalId);
            if (repository is not null)
                query = query.Where(i => i.RepositoryNames.Any(r => string.Equals(r, repository, StringComparison.OrdinalIgnoreCase)));
            return Task.FromResult<IReadOnlyList<Issue>>(query.ToList());
        }

        public Task<Issue?> GetIssueAsync(string issueId, CancellationToken ct = default)
            => Task.FromResult(Issues.TryGetValue(issueId, out var issue) ? issue : null);

        public Task<Issue> CreateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            if (_throwOnEveryCreate)
                throw new InvalidOperationException($"Failed to create issue '{issue.Id}'; an issue with the same ID may already exist.");

            if (_throwOnCreateOnce && !_hasThrown)
            {
                _hasThrown = true;
                throw new InvalidOperationException($"Failed to create issue '{issue.Id}'; an issue with the same ID may already exist.");
            }

            if (Issues.ContainsKey(issue.Id))
                throw new InvalidOperationException($"Failed to create issue '{issue.Id}'; an issue with the same ID may already exist.");

            Issues[issue.Id] = issue;
            return Task.FromResult(issue);
        }

        public Task UpdateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            if (!Issues.ContainsKey(issue.Id))
                throw new InvalidOperationException($"Issue '{issue.Id}' not found.");
            Issues[issue.Id] = issue;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteIssueAsync(string issueId, CancellationToken ct = default)
            => Task.FromResult(Issues.Remove(issueId));
    }

    /// <summary>
    /// Gated in-memory <see cref="IIssueStore"/> that forces the first handler's
    /// <c>GetIssueAsync</c> call to block until a second handler's <c>GetIssueAsync</c>
    /// arrives, and forces the first handler's <c>CreateIssueAsync</c> to block until a
    /// second handler's <c>CreateIssueAsync</c> arrives. This proves the two handler
    /// invocations genuinely overlap and makes the duplicate-ID race deterministic:
    /// the second handler creates the slug ID first, and the first handler's create
    /// throws <see cref="InvalidOperationException"/>, triggering the GUID retry.
    /// </summary>
    private sealed class GatedIssueStore : IIssueStore
    {
        private readonly TaskCompletionSource _firstGetIssueCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondGetIssueStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondCreateStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lock = new();
        private int _getIssueCount;
        private int _createCount;

        public Dictionary<string, Issue> Issues { get; } = new();

        /// <summary>Signalled when the first handler's GetIssueAsync is invoked (and blocked).</summary>
        public TaskCompletionSource FirstGetIssueCalled => _firstGetIssueCalled;

        public Task<IReadOnlyList<Issue>> GetAllIssuesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Issue>>(Issues.Values.ToList());

        public Task<IReadOnlyList<Issue>> GetIssuesAsync(
            IssueStatus? status = null,
            IssueType? type = null,
            IssueSeverity? severity = null,
            string? repository = null,
            string? sourceGoalId = null,
            CancellationToken ct = default)
        {
            var query = Issues.Values.AsEnumerable();
            if (status.HasValue) query = query.Where(i => i.Status == status.Value);
            if (type.HasValue) query = query.Where(i => i.Type == type.Value);
            if (severity.HasValue) query = query.Where(i => i.Severity == severity.Value);
            if (sourceGoalId is not null) query = query.Where(i => i.SourceGoalId == sourceGoalId);
            if (repository is not null)
                query = query.Where(i => i.RepositoryNames.Any(r => string.Equals(r, repository, StringComparison.OrdinalIgnoreCase)));
            return Task.FromResult<IReadOnlyList<Issue>>(query.ToList());
        }

        public Task<Issue?> GetIssueAsync(string issueId, CancellationToken ct = default)
        {
            var count = Interlocked.Increment(ref _getIssueCount);
            if (count == 1)
            {
                _firstGetIssueCalled.TrySetResult();
                return BlockUntilSecondGetIssueAsync(issueId, ct);
            }
            _secondGetIssueStarted.TrySetResult();
            lock (_lock)
            {
                return Task.FromResult(Issues.TryGetValue(issueId, out var issue) ? issue : null);
            }
        }

        private async Task<Issue?> BlockUntilSecondGetIssueAsync(string issueId, CancellationToken ct)
        {
            await _secondGetIssueStarted.Task.WaitAsync(ct);
            lock (_lock)
            {
                return Issues.TryGetValue(issueId, out var issue) ? issue : null;
            }
        }

        public Task<Issue> CreateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            var count = Interlocked.Increment(ref _createCount);
            if (count == 1)
            {
                return BlockUntilSecondCreateAsync(issue, ct);
            }
            _secondCreateStarted.TrySetResult();
            lock (_lock)
            {
                if (Issues.ContainsKey(issue.Id))
                    throw new InvalidOperationException($"Failed to create issue '{issue.Id}'; an issue with the same ID may already exist.");
                Issues[issue.Id] = issue;
            }
            return Task.FromResult(issue);
        }

        private async Task<Issue> BlockUntilSecondCreateAsync(Issue issue, CancellationToken ct)
        {
            await _secondCreateStarted.Task.WaitAsync(ct);
            lock (_lock)
            {
                if (Issues.ContainsKey(issue.Id))
                    throw new InvalidOperationException($"Failed to create issue '{issue.Id}'; an issue with the same ID may already exist.");
                Issues[issue.Id] = issue;
            }
            return issue;
        }

        public Task UpdateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            if (!Issues.ContainsKey(issue.Id))
                throw new InvalidOperationException($"Issue '{issue.Id}' not found.");
            Issues[issue.Id] = issue;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteIssueAsync(string issueId, CancellationToken ct = default)
            => Task.FromResult(Issues.Remove(issueId));
    }
}

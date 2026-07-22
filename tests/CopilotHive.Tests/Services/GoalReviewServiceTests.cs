using System.Runtime.CompilerServices;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Unit tests for <see cref="GoalReviewService"/>.
/// </summary>
public sealed class GoalReviewServiceTests
{
    private static string CreateWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "test-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Goal NewGoal(string id = "goal-1") => new()
    {
        Id = id,
        Description = "Add a null-guard to the Foo.Bar method in src/Foo.cs.",
        ReviewStatus = ReviewStatus.None,
    };

    private static GoalReviewService CreateService(
        string reply,
        KnowledgeGraph? knowledgeGraph = null,
        IGoalStore? goalStore = null,
        string? workDir = null,
        List<string>? capturedPrompts = null)
    {
        workDir ??= CreateWorkDir();
        return new GoalReviewService(
            knowledgeGraph,
            configRepo: null,
            config: null,
            goalStore,
            brainRepoManager: null,
            stateDir: workDir,
            logger: NullLogger<GoalReviewService>.Instance,
            chatClientFactory: _ => new CapturingStubChatClient(reply, capturedPrompts));
    }

    /// <summary>Creates a service whose chat client is produced by the supplied factory.</summary>
    private static GoalReviewService CreateServiceWithFactory(
        Func<string, IChatClient> chatClientFactory,
        KnowledgeGraph? knowledgeGraph = null,
        IGoalStore? goalStore = null,
        string? workDir = null)
    {
        workDir ??= CreateWorkDir();
        return new GoalReviewService(
            knowledgeGraph,
            configRepo: null,
            config: null,
            goalStore,
            brainRepoManager: null,
            stateDir: workDir,
            logger: NullLogger<GoalReviewService>.Instance,
            chatClientFactory: chatClientFactory);
    }

    [Fact]
    public async Task ReviewGoalAsync_ApprovedVerdict_SetsApproved()
    {
        var goal = NewGoal();
        var service = CreateService("""{"verdict":"Approved","issues":[],"verified":[],"recommendation":"Looks good"}""");

        var result = await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        Assert.Equal(ReviewStatus.Approved, goal.ReviewStatus);
        Assert.Equal("Approved", result.Verdict);
    }

    [Fact]
    public async Task ReviewGoalAsync_NegativeVerdict_SetsNeedsChanges()
    {
        var goal = NewGoal();
        var service = CreateService(
            """{"verdict":"NeedsChanges","issues":[{"severity":"MAJOR","description":"File does not exist"}],"verified":[],"recommendation":"Fix the file path"}""");

        var result = await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        Assert.Equal(ReviewStatus.NeedsChanges, goal.ReviewStatus);
        Assert.Equal("NeedsChanges", result.Verdict);
        Assert.Contains("File does not exist", result.Issues);
    }

    [Fact]
    public async Task ReviewGoalAsync_CreatesReviewDocumentInKnowledgeGraph()
    {
        var goal = NewGoal();
        var kg = new KnowledgeGraph();
        var service = CreateService(
            """{"verdict":"Approved","issues":[],"verified":[],"recommendation":"All good"}""",
            knowledgeGraph: kg);

        await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        var doc = kg.GetDocument($"review-{goal.Id}");
        Assert.NotNull(doc);
        Assert.Contains("Verdict: Approved", doc!.Content);
        Assert.Contains("All good", doc.Content);
    }

    [Fact]
    public async Task ReviewGoalAsync_LinksReviewDocumentToGoal()
    {
        var goal = NewGoal();
        var kg = new KnowledgeGraph();
        var service = CreateService(
            """{"verdict":"Approved","issues":[],"verified":[],"recommendation":"All good"}""",
            knowledgeGraph: kg);

        await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        Assert.Contains($"review-{goal.Id}", goal.Documents);
    }

    [Fact]
    public async Task ReviewGoalAsync_ReviewAlreadyPending_Throws()
    {
        var goal = NewGoal();
        goal.ReviewStatus = ReviewStatus.Pending;
        var service = CreateService("""{"verdict":"Approved","issues":[],"verified":[],"recommendation":"x"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReviewGoalAsync_UnparseableResponse_DefaultsToNeedsChanges()
    {
        var goal = NewGoal();
        var service = CreateService("this is not json at all");

        var result = await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        Assert.Equal(ReviewStatus.NeedsChanges, goal.ReviewStatus);
        Assert.Contains("could not be parsed", result.Summary);
    }

    [Fact]
    public async Task ReviewGoalAsync_NullKnowledgeGraph_Completes()
    {
        var goal = NewGoal();
        var service = CreateService(
            """{"verdict":"Approved","issues":[],"verified":[],"recommendation":"ok"}""",
            knowledgeGraph: null);

        var result = await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        Assert.Equal("Approved", result.Verdict);
        Assert.Equal(ReviewStatus.Approved, goal.ReviewStatus);
    }

    [Fact]
    public async Task ReviewGoalAsync_PromptIncludesGoalDescription()
    {
        var goal = NewGoal();
        var captured = new List<string>();
        var service = CreateService(
            """{"verdict":"Approved","issues":[],"verified":[],"recommendation":"ok"}""",
            capturedPrompts: captured);

        await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        Assert.Contains(captured, p => p.Contains(goal.Description));
    }

    [Fact]
    public async Task ReviewGoalAsync_PersistsPendingThenFinalViaGoalStore()
    {
        var goal = NewGoal();
        var store = new FakeGoalStore();
        store.AddGoal(goal);
        var service = CreateService(
            """{"verdict":"Approved","issues":[],"verified":[],"recommendation":"ok"}""",
            goalStore: store);

        await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        var persisted = await store.GetGoalAsync(goal.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal(ReviewStatus.Approved, persisted!.ReviewStatus);
    }

    [Fact]
    public async Task ReviewGoalAsync_PromptIncludesLinkedKnowledgeDocumentContents()
    {
        // Add a linked knowledge document and verify its title and content appear in the prompt.
        var goal = NewGoal();
        var kg = new KnowledgeGraph();
        await kg.CreateDocumentAsync(
            id: "design-doc",
            title: "Architecture Design",
            type: DocumentType.Scratch,
            content: "The system uses a distributed brain with persistent sessions.",
            topic: "architecture",
            author: "human",
            ct: TestContext.Current.CancellationToken);
        goal.Documents.Add("design-doc");

        var captured = new List<string>();
        var service = CreateService(
            """{"verdict":"Approved","issues":[],"verified":[],"recommendation":"ok"}""",
            knowledgeGraph: kg,
            capturedPrompts: captured);

        await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        var combinedPrompt = string.Join("\n", captured);
        Assert.Contains(goal.Description, combinedPrompt);
        Assert.Contains("Architecture Design", combinedPrompt);
        Assert.Contains("The system uses a distributed brain with persistent sessions.", combinedPrompt);
    }

    [Fact]
    public async Task ReviewGoalAsync_ExistingReviewDocument_UpdatedInPlace()
    {
        // Pre-create a review document so the service should update it, not create a new one.
        var goal = NewGoal();
        var kg = new KnowledgeGraph();
        var docId = $"review-{goal.Id}";

        await kg.CreateDocumentAsync(
            id: docId,
            title: $"Review: {goal.Id}",
            type: DocumentType.Scratch,
            content: "OLD CONTENT — this should be replaced.",
            topic: "review",
            author: "reviewer",
            ct: TestContext.Current.CancellationToken);

        var service = CreateService(
            """{"verdict":"Approved","issues":[],"verified":[],"recommendation":"New recommendation"}""",
            knowledgeGraph: kg);

        await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        var doc = kg.GetDocument(docId);
        Assert.NotNull(doc);
        // The content should be replaced with the new verdict — old content must not remain.
        Assert.DoesNotContain("OLD CONTENT", doc!.Content);
        Assert.Contains("Verdict: Approved", doc.Content);
        Assert.Contains("New recommendation", doc.Content);
        // The document should still have the same id (no duplicate created).
        Assert.Equal(docId, doc.Id);
        // Only one document with this id should exist (no duplication).
        var allDocs = kg.Search("*");
        var matching = allDocs.Where(d => d.Id == docId).ToList();
        Assert.Single(matching);
    }

    [Fact]
    public async Task ReviewGoalAsync_CallerCancellation_ResetsPendingAndRethrows()
    {
        // A chat client that throws OperationCanceledException when the token is cancelled.
        var goal = NewGoal();
        var store = new FakeGoalStore();
        store.AddGoal(goal);

        var service = CreateServiceWithFactory(
            _ => new CancellingStubChatClient(),
            goalStore: store);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ReviewGoalAsync(goal, cts.Token));

        // The goal must not be left Pending.
        Assert.NotEqual(ReviewStatus.Pending, goal.ReviewStatus);
        var persisted = await store.GetGoalAsync(goal.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(persisted);
        Assert.NotEqual(ReviewStatus.Pending, persisted!.ReviewStatus);
    }

    [Fact]
    public async Task ReviewGoalAsync_SetupFailure_ResetsPendingAndReturnsNeedsChanges()
    {
        // The chat client factory throws, simulating an unsupported model / missing provider.
        var goal = NewGoal();
        var store = new FakeGoalStore();
        store.AddGoal(goal);

        var service = CreateServiceWithFactory(
            _ => throw new InvalidOperationException("No provider configured"),
            goalStore: store);

        var result = await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        Assert.Equal("NeedsChanges", result.Verdict);
        Assert.Equal(ReviewStatus.NeedsChanges, goal.ReviewStatus);
        var persisted = await store.GetGoalAsync(goal.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal(ReviewStatus.NeedsChanges, persisted!.ReviewStatus);
    }

    [Fact]
    public async Task ReviewGoalAsync_ReviewDocument_IncludesVerifiedSection()
    {
        var goal = NewGoal();
        var kg = new KnowledgeGraph();
        var service = CreateService(
            """{"verdict":"Approved","issues":[],"verified":[{"item":"Services/GoalReviewService.cs","exists":true},{"item":"Nonexistent.cs","exists":false}],"recommendation":"Good"}""",
            knowledgeGraph: kg);

        await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        var doc = kg.GetDocument($"review-{goal.Id}");
        Assert.NotNull(doc);
        Assert.Contains("✅", doc!.Content);
        Assert.Contains("Services/GoalReviewService.cs", doc.Content);
        Assert.Contains("❌", doc.Content);
        Assert.Contains("Nonexistent.cs", doc.Content);
    }

    [Fact]
    public async Task ReviewGoalAsync_AgentFailure_CreatesReviewDocument()
    {
        var goal = NewGoal();
        var kg = new KnowledgeGraph();
        var service = CreateServiceWithFactory(
            _ => new ThrowingStubChatClient(new InvalidOperationException("Agent crashed")),
            knowledgeGraph: kg);

        var result = await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        Assert.Equal("NeedsChanges", result.Verdict);
        var doc = kg.GetDocument($"review-{goal.Id}");
        Assert.NotNull(doc);
        Assert.Contains("NeedsChanges", doc!.Content);
        Assert.Contains("Agent crashed", doc.Content);
    }

    [Fact]
    public async Task ReviewGoalAsync_PersistedGoalAlreadyPending_Throws()
    {
        // The in-memory instance shows None, but the persisted goal shows Pending —
        // simulating a concurrent review started by another caller/instance.
        var goal = NewGoal();
        var persistedPending = NewGoal();
        persistedPending.ReviewStatus = ReviewStatus.Pending;

        var store = new PendingReturningGoalStore(persistedPending);
        var service = CreateService(
            """{"verdict":"Approved","issues":[],"verified":[],"recommendation":"x"}""",
            goalStore: store);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReviewGoalAsync_ReviewDocument_IssuesRenderedAsHeadings()
    {
        // The goal spec requires issues rendered as "### [SEVERITY] description" headings in the
        // review document markdown. Verify both severity and description appear in heading format.
        var goal = NewGoal();
        var kg = new KnowledgeGraph();
        var service = CreateService(
            """{"verdict":"NeedsChanges","issues":[{"severity":"CRITICAL","description":"Missing tests"},{"severity":"MINOR","description":"Bad naming"}],"verified":[],"recommendation":"Fix issues"}""",
            knowledgeGraph: kg);

        await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        var doc = kg.GetDocument($"review-{goal.Id}");
        Assert.NotNull(doc);
        Assert.Contains("### [CRITICAL] Missing tests", doc!.Content);
        Assert.Contains("### [MINOR] Bad naming", doc.Content);
    }

    [Fact]
    public async Task ReviewGoalAsync_SameInstanceConcurrentCall_Throws()
    {
        // First call blocks in the agent; a second concurrent call for the same goal ID must be rejected.
        var goal = NewGoal();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var service = CreateServiceWithFactory(
            _ => new BlockingStubChatClient(tcs.Task, blockingEntered));

        var first = service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        // Wait until the first call has entered the blocking agent call (lock is held).
        await blockingEntered.Task;

        // A second call with the same goal ID (fresh instance) must be rejected.
        var secondGoal = NewGoal();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReviewGoalAsync(secondGoal, TestContext.Current.CancellationToken));

        // Release the first call so it can complete cleanly.
        tcs.SetResult("""{"verdict":"Approved","issues":[],"verified":[],"recommendation":"ok"}""");
        await first;
    }

    [Fact]
    public async Task ReviewGoalAsync_InitialPendingPersistenceFailure_ResetsReviewStatus()
    {
        // The store throws on the FIRST UpdateGoalAsync call (persisting Pending) but succeeds
        // afterwards (recovery persistence). The goal must not be left stuck in Pending.
        var goal = NewGoal();
        var store = new ThrowOnFirstUpdateGoalStore();
        store.AddGoal(goal);

        var service = CreateService(
            """{"verdict":"Approved","issues":[],"verified":[],"recommendation":"ok"}""",
            goalStore: store);

        var result = await service.ReviewGoalAsync(goal, TestContext.Current.CancellationToken);

        Assert.NotEqual(ReviewStatus.Pending, goal.ReviewStatus);
        Assert.Equal(ReviewStatus.NeedsChanges, goal.ReviewStatus);
        Assert.Equal("NeedsChanges", result.Verdict);

        var persisted = await store.GetGoalAsync(goal.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(persisted);
        Assert.NotEqual(ReviewStatus.Pending, persisted!.ReviewStatus);
    }
}

/// <summary>
/// A stub <see cref="IChatClient"/> that returns a canned reply and (optionally) records the
/// user prompts it receives so tests can assert on prompt contents. Finishes in one step.
/// </summary>
file sealed class CapturingStubChatClient(string replyText, List<string>? capturedPrompts) : IChatClient
{
    public ChatClientMetadata Metadata => new("stub", null, "stub-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Capture(messages);
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, replyText))
        {
            FinishReason = ChatFinishReason.Stop,
        };
        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Capture(messages);
        return GetStreamingUpdatesAsync(replyText, cancellationToken);
    }

    private void Capture(IEnumerable<ChatMessage> messages)
    {
        if (capturedPrompts is null)
            return;
        foreach (var m in messages)
            capturedPrompts.Add(m.Text);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> GetStreamingUpdatesAsync(
        string replyText, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var remaining = replyText.AsMemory();
        const int chunkSize = 16;

        while (!remaining.IsEmpty && !cancellationToken.IsCancellationRequested)
        {
            var chunk = remaining.Length <= chunkSize ? remaining : remaining[..chunkSize];
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(chunk.ToString())]);
            remaining = remaining.Slice(chunk.Length);
        }

        yield return new ChatResponseUpdate
        {
            FinishReason = ChatFinishReason.Stop,
            Role = ChatRole.Assistant,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>A stub <see cref="IChatClient"/> that throws <see cref="OperationCanceledException"/> when its token is cancelled.</summary>
file sealed class CancellingStubChatClient : IChatClient
{
    public ChatClientMetadata Metadata => new("stub", null, "stub-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // If the linked token was not yet cancelled, cancel explicitly to model an interrupted call.
        throw new OperationCanceledException();
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new OperationCanceledException();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>A stub <see cref="IChatClient"/> that throws a supplied exception on any call.</summary>
file sealed class ThrowingStubChatClient(Exception toThrow) : IChatClient
{
    public ChatClientMetadata Metadata => new("stub", null, "stub-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw toThrow;

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw toThrow;

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>
/// A stub <see cref="IChatClient"/> that signals when it is entered and then blocks until a
/// supplied task completes, returning that task's result as the assistant reply.
/// </summary>
file sealed class BlockingStubChatClient(Task<string> release, TaskCompletionSource<bool> entered) : IChatClient
{
    public ChatClientMetadata Metadata => new("stub", null, "stub-model");

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        entered.TrySetResult(true);
        var reply = await release.WaitAsync(cancellationToken);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, reply))
        {
            FinishReason = ChatFinishReason.Stop,
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        entered.TrySetResult(true);
        var reply = await release.WaitAsync(cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(reply)]);
        yield return new ChatResponseUpdate
        {
            FinishReason = ChatFinishReason.Stop,
            Role = ChatRole.Assistant,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>Minimal in-memory <see cref="IGoalStore"/> for review service tests.</summary>
file sealed class FakeGoalStore : IGoalStore
{
    private readonly Dictionary<string, Goal> _goals = new();

    public string Name => "FakeGoalStore";

    public void AddGoal(Goal goal) => _goals[goal.Id] = goal;

    public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.ToList().AsReadOnly());

    public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(_goals.TryGetValue(goalId, out var goal) ? goal : null);

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

    public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(_goals.Remove(goalId));

    public Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IterationSummary>>(Array.Empty<IterationSummary>());

    public Task<int> ImportGoalsAsync(IEnumerable<Goal> goals, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.FromResult(release);

    public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<Release?>(null);

    public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Release>>(Array.Empty<Release>());

    public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationEntry>>(Array.Empty<ConversationEntry>());

    public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>(Array.Empty<(string, PersistedClarification)>());
}

/// <summary>
/// An <see cref="IGoalStore"/> whose <see cref="GetGoalAsync"/> always returns a supplied goal
/// (typically with <see cref="ReviewStatus.Pending"/>), used to exercise the persisted-state
/// concurrency check.
/// </summary>
file sealed class PendingReturningGoalStore(Goal persisted) : IGoalStore
{
    public string Name => "PendingReturningGoalStore";

    public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<Goal?>(persisted);

    public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default) =>
        Task.FromResult(goal);

    public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IterationSummary>>(Array.Empty<IterationSummary>());

    public Task<int> ImportGoalsAsync(IEnumerable<Goal> goals, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.FromResult(release);

    public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<Release?>(null);

    public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Release>>(Array.Empty<Release>());

    public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationEntry>>(Array.Empty<ConversationEntry>());

    public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>(Array.Empty<(string, PersistedClarification)>());
}

/// <summary>
/// In-memory <see cref="IGoalStore"/> that throws on the FIRST <see cref="UpdateGoalAsync"/> call
/// (simulating a database error while persisting the initial Pending status), then persists
/// normally on subsequent calls (recovery persistence).
/// </summary>
file sealed class ThrowOnFirstUpdateGoalStore : IGoalStore
{
    private readonly Dictionary<string, Goal> _goals = new();
    private bool _firstUpdate = true;

    public string Name => "ThrowOnFirstUpdateGoalStore";

    public void AddGoal(Goal goal) => _goals[goal.Id] = goal;

    public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        if (_firstUpdate)
        {
            _firstUpdate = false;
            throw new InvalidOperationException("Database error");
        }

        _goals[goal.Id] = goal;
        return Task.CompletedTask;
    }

    public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(_goals.TryGetValue(goalId, out var goal) ? goal : null);

    public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(_goals.Values.ToList().AsReadOnly());

    public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default)
    {
        _goals[goal.Id] = goal;
        return Task.FromResult(goal);
    }

    public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(_goals.Remove(goalId));

    public Task<IReadOnlyList<Goal>> SearchGoalsAsync(string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IterationSummary>>(Array.Empty<IterationSummary>());

    public Task<int> ImportGoalsAsync(IEnumerable<Goal> goals, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.FromResult(release);

    public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<Release?>(null);

    public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Release>>(Array.Empty<Release>());

    public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());

    public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationEntry>>(Array.Empty<ConversationEntry>());

    public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(int? limit = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>>(Array.Empty<(string, PersistedClarification)>());
}

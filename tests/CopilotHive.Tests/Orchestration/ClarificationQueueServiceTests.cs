using CopilotHive.Orchestration;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Tests for <see cref="ClarificationQueueService"/> — the in-memory queue that stores
/// pending clarification requests and supports polling, human resolution, and timeout.
/// </summary>
public sealed class ClarificationQueueServiceTests
{
    [Fact]
    public void Enqueue_CreatesRequestAndReturnsWaiter()
    {
        var service = new ClarificationQueueService();
        var request = MakeRequest("goal-1", "How should I handle nulls?");

        var tcs = service.Enqueue(request);

        Assert.NotNull(tcs);
        Assert.False(tcs.Task.IsCompleted);
        // Initially AwaitingComposer, so not yet in the human queue
        Assert.Equal(0, service.PendingHumanCount);
        // But should be visible in GetAllRequests
        Assert.Single(service.GetAllRequests());
    }

    [Fact]
    public async Task SubmitAnswer_CompletesWaiterAndUpdatesRequest()
    {
        var service = new ClarificationQueueService();
        var request = MakeRequest("goal-2", "What format should the output use?");
        request.Status = ClarificationStatus.AwaitingHuman;
        var tcs = service.Enqueue(request);

        var answered = service.SubmitAnswer(request.Id, "Use JSON format", "human");

        Assert.True(answered);
        Assert.True(tcs.Task.IsCompleted);
        Assert.Equal("Use JSON format", await tcs.Task);
        Assert.Equal(ClarificationStatus.Answered, request.Status);
        Assert.Equal("human", request.ResolvedBy);
        Assert.Equal("Use JSON format", request.Answer);
    }

    [Fact]
    public void SubmitAnswer_UnknownId_ReturnsFalse()
    {
        var service = new ClarificationQueueService();

        var result = service.SubmitAnswer("nonexistent-id", "some answer", "human");

        Assert.False(result);
    }

    [Fact]
    public async Task MarkTimedOut_SetsStatusAndCompletesWithFallback()
    {
        var service = new ClarificationQueueService();
        var request = MakeRequest("goal-3", "Should I use tabs or spaces?");
        var tcs = service.Enqueue(request);

        service.MarkTimedOut(request.Id);

        Assert.True(tcs.Task.IsCompleted);
        Assert.Equal(ClarificationQueueService.TimeoutFallbackMessage, await tcs.Task);
        Assert.Equal(ClarificationStatus.TimedOut, request.Status);
        Assert.Equal(ClarificationQueueService.TimeoutFallbackMessage, request.Answer);
    }

    [Fact]
    public void GetPendingHumanRequests_ReturnsOnlyAwaitingHuman()
    {
        var service = new ClarificationQueueService();

        var r1 = MakeRequest("goal-a", "Q1");
        r1.Status = ClarificationStatus.AwaitingHuman;
        service.Enqueue(r1);

        var r2 = MakeRequest("goal-b", "Q2");
        r2.Status = ClarificationStatus.AwaitingComposer;
        service.Enqueue(r2);

        var r3 = MakeRequest("goal-c", "Q3");
        r3.Status = ClarificationStatus.AwaitingHuman;
        service.Enqueue(r3);

        var pending = service.GetPendingHumanRequests();

        Assert.Equal(2, pending.Count);
        Assert.All(pending, r => Assert.Equal(ClarificationStatus.AwaitingHuman, r.Status));
    }

    [Fact]
    public void PendingHumanCount_ReflectsOnlyAwaitingHumanStatus()
    {
        var service = new ClarificationQueueService();

        var r1 = MakeRequest("goal-x", "Q");
        r1.Status = ClarificationStatus.AwaitingHuman;
        service.Enqueue(r1);

        var r2 = MakeRequest("goal-y", "Q");
        service.Enqueue(r2); // AwaitingComposer by default

        Assert.Equal(1, service.PendingHumanCount);
    }

    [Fact]
    public void EscalateToHuman_ChangesStatusToAwaitingHuman()
    {
        var service = new ClarificationQueueService();
        var request = MakeRequest("goal-e", "A question");
        service.Enqueue(request);
        Assert.Equal(ClarificationStatus.AwaitingComposer, request.Status);

        service.EscalateToHuman(request.Id);

        Assert.Equal(ClarificationStatus.AwaitingHuman, request.Status);
    }

    [Fact]
    public void GetRequest_ReturnsRequest_WhenExists()
    {
        var service = new ClarificationQueueService();
        var request = MakeRequest("goal-f", "Q");
        service.Enqueue(request);

        var found = service.GetRequest(request.Id);

        Assert.NotNull(found);
        Assert.Equal(request.Id, found.Id);
    }

    [Fact]
    public void GetRequest_ReturnsNull_WhenNotFound()
    {
        var service = new ClarificationQueueService();

        var found = service.GetRequest("no-such-id");

        Assert.Null(found);
    }

    [Fact]
    public void GetAllRequests_ReturnsAllOrderedByNewestFirst()
    {
        var service = new ClarificationQueueService();
        var r1 = MakeRequest("g1", "Q1");
        r1.RequestedAt = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var r2 = MakeRequest("g2", "Q2");
        r2.RequestedAt = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        service.Enqueue(r1);
        service.Enqueue(r2);

        var all = service.GetAllRequests();
        Assert.Equal(2, all.Count);
        Assert.Equal("g2", all[0].GoalId); // newer first
        Assert.Equal("g1", all[1].GoalId);
    }

    [Fact]
    public void OnChanged_RaisedOnEnqueueSubmitEscalateAndTimeout()
    {
        var service = new ClarificationQueueService();
        var changeCount = 0;
        service.OnChanged += () => changeCount++;

        var r = MakeRequest("g", "Q");
        service.Enqueue(r);
        Assert.Equal(1, changeCount);

        service.EscalateToHuman(r.Id);
        Assert.Equal(2, changeCount);

        service.SubmitAnswer(r.Id, "answer", "human");
        Assert.Equal(3, changeCount);

        var r2 = MakeRequest("g2", "Q2");
        service.Enqueue(r2);
        Assert.Equal(4, changeCount);

        service.MarkTimedOut(r2.Id);
        Assert.Equal(5, changeCount);
    }

    [Fact]
    public async Task MultipleConcurrentRequests_AreIndependent()
    {
        var service = new ClarificationQueueService();
        var r1 = MakeRequest("goal-1", "Q1");
        r1.Status = ClarificationStatus.AwaitingHuman;
        var r2 = MakeRequest("goal-2", "Q2");
        r2.Status = ClarificationStatus.AwaitingHuman;

        var tcs1 = service.Enqueue(r1);
        var tcs2 = service.Enqueue(r2);

        service.SubmitAnswer(r1.Id, "Answer 1", "human");

        Assert.True(tcs1.Task.IsCompleted);
        Assert.False(tcs2.Task.IsCompleted);
        Assert.Equal("Answer 1", await tcs1.Task);
        Assert.Equal(1, service.PendingHumanCount); // r2 still pending
    }

    [Fact]
    public void TimeoutFallbackMessage_IsExpectedValue()
    {
        Assert.Equal("Please proceed with your best judgment.",
            ClarificationQueueService.TimeoutFallbackMessage);
    }

    [Fact]
    public void ComposerTimeout_Is30Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ClarificationQueueService.ComposerTimeout);
    }

    [Fact]
    public void HumanTimeout_Is5Minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), ClarificationQueueService.HumanTimeout);
    }

    // ── Helper ──

    private static ClarificationRequest MakeRequest(string goalId, string question) =>
        new()
        {
            GoalId = goalId,
            WorkerRole = "coder",
            Question = question,
        };
}

/// <summary>
/// Tests for the <see cref="ClarificationRequest"/> model.
/// </summary>
public sealed class ClarificationRequestTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var request = new ClarificationRequest();

        Assert.NotNull(request.Id);
        Assert.NotEmpty(request.Id);
        Assert.Equal("", request.GoalId);
        Assert.Equal("", request.WorkerRole);
        Assert.Equal("", request.Question);
        Assert.Null(request.Answer);
        Assert.Equal(ClarificationStatus.AwaitingComposer, request.Status);
        Assert.Null(request.ResolvedBy);
    }

    [Fact]
    public void AllProperties_AreSettable()
    {
        var now = DateTime.UtcNow;
        var request = new ClarificationRequest
        {
            Id = "custom-id",
            GoalId = "goal-42",
            WorkerRole = "tester",
            Question = "How many tests?",
            RequestedAt = now,
            Answer = "42",
            Status = ClarificationStatus.Answered,
            ResolvedBy = "human",
        };

        Assert.Equal("custom-id", request.Id);
        Assert.Equal("goal-42", request.GoalId);
        Assert.Equal("tester", request.WorkerRole);
        Assert.Equal("How many tests?", request.Question);
        Assert.Equal(now, request.RequestedAt);
        Assert.Equal("42", request.Answer);
        Assert.Equal(ClarificationStatus.Answered, request.Status);
        Assert.Equal("human", request.ResolvedBy);
    }

    [Fact]
    public void UniqueIds_GeneratedByDefault()
    {
        var r1 = new ClarificationRequest();
        var r2 = new ClarificationRequest();

        Assert.NotEqual(r1.Id, r2.Id);
    }
}

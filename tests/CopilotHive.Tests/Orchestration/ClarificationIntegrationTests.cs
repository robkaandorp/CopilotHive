using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Integration tests for the three-tier clarification resolution chain:
/// Brain → Composer → Human.
/// Tests verify that clarification requests flow correctly through each tier,
/// with proper timeout handling and state management.
/// </summary>
public sealed class ClarificationIntegrationTests
{
    // ── Scenario 1: Brain answers directly (no escalation) ────────────────

    [Fact]
    public async Task BrainAnswersDirectly_ReturnsAnswerWithoutEscalation()
    {
        // Arrange: Brain returns a confident answer (BrainResponse.Answer)
        var brain = new FakeClarificationBrain(BrainResponse.Answer("Use JSON format for the output."));
        var queue = new ClarificationQueueService();
        var (dispatcher, pipeline) = CreateDispatcherWithClarification(brain, queue, useComposer: false);

        // Act
        var answer = await dispatcher.AskBrainAsync(pipeline, "What format should the output use?", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Use JSON format for the output.", answer);
        Assert.Equal(0, queue.PendingHumanCount);
        Assert.Empty(queue.GetAllRequests());
    }

    [Fact]
    public async Task BrainAnswersDirectly_NoClarificationQueueNeeded()
    {
        // Arrange: Brain returns a confident answer, no queue/composer configured
        var brain = new FakeClarificationBrain(BrainResponse.Answer("The answer is 42."));
        var (dispatcher, pipeline) = CreateDispatcherWithClarification(brain, null, useComposer: false);

        // Act
        var answer = await dispatcher.AskBrainAsync(pipeline, "What is the meaning of life?", TestContext.Current.CancellationToken);

        // Assert - Answer returned directly, no queue interaction
        Assert.Equal("The answer is 42.", answer);
    }

    [Fact]
    public async Task BrainAnswers_TextContainsEscalateWord_NotTreatedAsEscalation()
    {
        // Arrange: Brain returns a non-escalating answer (BrainResponse.Answer)
        // A response like "I should escalate this" should NOT trigger escalation —
        // only BrainResponse.Escalated() signals escalation; text content is irrelevant.
        var brain = new FakeClarificationBrain(BrainResponse.Answer("I should escalate this to someone else."));
        var queue = new ClarificationQueueService();
        var (dispatcher, pipeline) = CreateDispatcherWithClarification(brain, queue, useComposer: false);

        // Act
        var answer = await dispatcher.AskBrainAsync(pipeline, "Question?", TestContext.Current.CancellationToken);

        // Assert - BrainResponse.Answer is returned as-is; IsEscalation=false
        Assert.Equal("I should escalate this to someone else.", answer);
    }

    // ── Scenario 2: Brain Escalation without Composer/Queue ──────────────────

    [Fact]
    public async Task BrainEscalates_NoComposerOrQueue_ReturnsFallback()
    {
        // Arrange: Brain escalates but no Composer/Queue available
        var brain = new FakeClarificationBrain(BrainResponse.Escalated("Cannot answer from codebase", "Cannot answer from codebase"));
        var (dispatcher, pipeline) = CreateDispatcherWithClarification(brain, null, useComposer: false);

        // Act
        var answer = await dispatcher.AskBrainAsync(pipeline, "What should I do?", TestContext.Current.CancellationToken);

        // Assert - Fallback returned immediately
        Assert.Equal(ClarificationQueueService.TimeoutFallbackMessage, answer);
    }

    [Fact]
    public async Task BrainEscalates_NoComposer_ReturnsFallback()
    {
        // Arrange: Brain escalates, queue exists but no composer
        var brain = new FakeClarificationBrain(BrainResponse.Escalated("Need clarification", "Need clarification"));
        var queue = new ClarificationQueueService();
        var (dispatcher, pipeline) = CreateDispatcherWithClarification(brain, queue, useComposer: false);

        // Act
        var answer = await dispatcher.AskBrainAsync(pipeline, "What format?", TestContext.Current.CancellationToken);

        // Assert - Fallback returned (no composer to try)
        Assert.Equal(ClarificationQueueService.TimeoutFallbackMessage, answer);
    }

    // ── Scenario 3: ClarificationQueueService direct tests ───────────────────

    [Fact]
    public void Enqueue_CreatesRequestWithAwaitingComposerStatus()
    {
        // Arrange
        var queue = new ClarificationQueueService();
        var request = new ClarificationRequest
        {
            GoalId = "goal-1",
            WorkerRole = "coder",
            Question = "What format should I use?",
        };

        // Act
        var tcs = queue.Enqueue(request);

        // Assert
        Assert.NotNull(tcs);
        Assert.False(tcs.Task.IsCompleted);
        Assert.Equal(ClarificationStatus.AwaitingComposer, request.Status);
        Assert.Equal(0, queue.PendingHumanCount); // Not yet escalated to human
        Assert.Single(queue.GetAllRequests());
    }

    [Fact]
    public void EscalateToHuman_UpdatesStatusAndRaisesEvent()
    {
        // Arrange
        var queue = new ClarificationQueueService();
        var request = new ClarificationRequest
        {
            GoalId = "goal-1",
            WorkerRole = "coder",
            Question = "Question?",
        };
        queue.Enqueue(request);
        
        var changeCount = 0;
        queue.OnChanged += () => changeCount++;

        // Act
        queue.EscalateToHuman(request.Id);

        // Assert
        Assert.Equal(ClarificationStatus.AwaitingHuman, request.Status);
        Assert.Equal(1, queue.PendingHumanCount);
        Assert.Equal(1, changeCount); // OnChanged raised once
    }

    [Fact]
    public async Task SubmitAnswer_CompletesWaiterAndUpdatesRequest()
    {
        // Arrange
        var queue = new ClarificationQueueService();
        var request = new ClarificationRequest
        {
            GoalId = "goal-1",
            WorkerRole = "coder",
            Question = "Question?",
            Status = ClarificationStatus.AwaitingHuman,
        };
        var tcs = queue.Enqueue(request);

        // Act
        var result = queue.SubmitAnswer(request.Id, "Here's the answer", "human");

        // Assert
        Assert.True(result);
        Assert.True(tcs.Task.IsCompleted);
        Assert.Equal("Here's the answer", await tcs.Task);
        Assert.Equal(ClarificationStatus.Answered, request.Status);
        Assert.Equal("human", request.ResolvedBy);
    }

    [Fact]
    public async Task ComposerAnswer_SubmitAnswer_ResolvesRequest()
    {
        // Arrange - Simulating Composer answering after escalation
        var queue = new ClarificationQueueService();
        var request = new ClarificationRequest
        {
            GoalId = "goal-1",
            WorkerRole = "coder",
            Question = "Question?",
        };
        var tcs = queue.Enqueue(request);

        // First escalate to human (simulating Composer can't answer)
        queue.EscalateToHuman(request.Id);
        Assert.Equal(1, queue.PendingHumanCount);

        // Act - Composer answers before human
        queue.SubmitAnswer(request.Id, "Actually I can answer this", "composer");

        // Assert
        Assert.Equal("Actually I can answer this", await tcs.Task);
        Assert.Equal(ClarificationStatus.Answered, request.Status);
        Assert.Equal("composer", request.ResolvedBy);
        Assert.Equal(0, queue.PendingHumanCount);
    }

    [Fact]
    public async Task MarkTimedOut_SetsStatusAndCompletesWithFallback()
    {
        // Arrange
        var queue = new ClarificationQueueService();
        var request = new ClarificationRequest
        {
            GoalId = "goal-1",
            WorkerRole = "coder",
            Question = "Question?",
        };
        var tcs = queue.Enqueue(request);

        // Act
        queue.MarkTimedOut(request.Id);

        // Assert
        Assert.True(tcs.Task.IsCompleted);
        Assert.Equal(ClarificationQueueService.TimeoutFallbackMessage, await tcs.Task);
        Assert.Equal(ClarificationStatus.TimedOut, request.Status);
        Assert.Equal(ClarificationQueueService.TimeoutFallbackMessage, request.Answer);
        Assert.Equal(0, queue.PendingHumanCount);
    }

    // ── Scenario 4: Timeout fallback via queue ───────────────────────────────

    [Fact]
    public async Task HumanTimeout_ReturnsFallbackMessage()
    {
        // Arrange
        var queue = new ClarificationQueueService();
        var request = new ClarificationRequest
        {
            GoalId = "goal-1",
            WorkerRole = "coder",
            Question = "Question?",
            Status = ClarificationStatus.AwaitingHuman,
        };
        var tcs = queue.Enqueue(request);

        // Simulate timeout by marking as timed out
        queue.MarkTimedOut(request.Id);

        // Act & Assert
        var answer = await tcs.Task;
        Assert.Equal(ClarificationQueueService.TimeoutFallbackMessage, answer);
    }

    // ── Scenario 5: Multiple simultaneous requests ──────────────────────────

    [Fact]
    public async Task MultipleRequests_AreTrackedIndependentlyInQueue()
    {
        // Arrange
        var queue = new ClarificationQueueService();

        var request1 = new ClarificationRequest
        {
            Id = "req-1",
            GoalId = "goal-1",
            WorkerRole = "coder",
            Question = "Question 1?",
            Status = ClarificationStatus.AwaitingHuman,
        };
        var request2 = new ClarificationRequest
        {
            Id = "req-2",
            GoalId = "goal-2",
            WorkerRole = "tester",
            Question = "Question 2?",
            Status = ClarificationStatus.AwaitingHuman,
        };
        var request3 = new ClarificationRequest
        {
            Id = "req-3",
            GoalId = "goal-3",
            WorkerRole = "coder",
            Question = "Question 3?",
            Status = ClarificationStatus.AwaitingHuman,
        };

        // Act
        var tcs1 = queue.Enqueue(request1);
        var tcs2 = queue.Enqueue(request2);
        var tcs3 = queue.Enqueue(request3);

        // Assert - All tracked independently
        Assert.Equal(3, queue.PendingHumanCount);
        var pending = queue.GetPendingHumanRequests();
        Assert.Equal(3, pending.Count);

        // Answer each separately
        queue.SubmitAnswer("req-1", "Answer 1", "human");
        Assert.True(tcs1.Task.IsCompleted);
        Assert.Equal("Answer 1", await tcs1.Task);
        Assert.Equal(2, queue.PendingHumanCount);

        queue.SubmitAnswer("req-2", "Answer 2", "human");
        Assert.True(tcs2.Task.IsCompleted);
        Assert.Equal("Answer 2", await tcs2.Task);
        Assert.Equal(1, queue.PendingHumanCount);

        queue.SubmitAnswer("req-3", "Answer 3", "human");
        Assert.True(tcs3.Task.IsCompleted);
        Assert.Equal("Answer 3", await tcs3.Task);
        Assert.Equal(0, queue.PendingHumanCount);
    }

    [Fact]
    public async Task MultipleRequests_DifferentTiers_ResolveCorrectly()
    {
        // Arrange - Test queue behavior for different resolution paths
        var queue = new ClarificationQueueService();

        // Request 1: Composer resolves
        var req1 = new ClarificationRequest
        {
            Id = "composer-req",
            GoalId = "goal-1",
            WorkerRole = "coder",
            Question = "Question for Composer",
        };
        var tcs1 = queue.Enqueue(req1);

        // Request 2: Human resolves
        var req2 = new ClarificationRequest
        {
            Id = "human-req",
            GoalId = "goal-2",
            WorkerRole = "coder",
            Question = "Question for Human",
            Status = ClarificationStatus.AwaitingHuman,
        };
        var tcs2 = queue.Enqueue(req2);

        // Request 3: Timeout
        var req3 = new ClarificationRequest
        {
            Id = "timeout-req",
            GoalId = "goal-3",
            WorkerRole = "coder",
            Question = "Timeout question",
            Status = ClarificationStatus.AwaitingHuman,
        };
        var tcs3 = queue.Enqueue(req3);

        // Act - Composer answers request 1
        queue.SubmitAnswer("composer-req", "Composer auto-answer", "composer");
        Assert.Equal("Composer auto-answer", await tcs1.Task);
        Assert.Equal(ClarificationStatus.Answered, req1.Status);
        Assert.Equal("composer", req1.ResolvedBy);

        // Act - Human answers request 2
        queue.SubmitAnswer("human-req", "Human answer", "human");
        Assert.Equal("Human answer", await tcs2.Task);
        Assert.Equal(ClarificationStatus.Answered, req2.Status);
        Assert.Equal("human", req2.ResolvedBy);

        // Act - Request 3 times out
        queue.MarkTimedOut("timeout-req");
        Assert.Equal(ClarificationQueueService.TimeoutFallbackMessage, await tcs3.Task);
        Assert.Equal(ClarificationStatus.TimedOut, req3.Status);

        // Assert - All resolved, none pending
        Assert.Equal(0, queue.PendingHumanCount);
        Assert.Equal(3, queue.GetAllRequests().Count);
    }

    // ── Scenario: Human escalation flow ────────────────────────────────────

    [Fact]
    public async Task HumanEscalation_QueueWaiter_ResolvesWhenAnswerSubmitted()
    {
        // Arrange
        var queue = new ClarificationQueueService();
        var request = new ClarificationRequest
        {
            GoalId = "goal-human",
            WorkerRole = "coder",
            Question = "What should the timeout be?",
            Status = ClarificationStatus.AwaitingHuman,
        };
        var tcs = queue.Enqueue(request);

        // Act - Submit answer from another "thread" (simulating human response)
        var answerTask = tcs.Task;
        _ = Task.Run(async () =>
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            queue.SubmitAnswer(request.Id, "Use 5 minutes", "human");
        }, TestContext.Current.CancellationToken);

        // Assert - Waiter receives answer
        var answer = await answerTask;
        Assert.Equal("Use 5 minutes", answer);
        Assert.Equal(ClarificationStatus.Answered, request.Status);
    }

    [Fact]
    public void OnChangedEvent_RaisedOnAllStateTransitions()
    {
        // Arrange
        var queue = new ClarificationQueueService();
        var events = new List<string>();
        queue.OnChanged += () => events.Add("changed");

        // Act - Enqueue
        var request = new ClarificationRequest { GoalId = "g1", Question = "Q1" };
        queue.Enqueue(request);
        Assert.Single(events);

        // Act - Escalate
        queue.EscalateToHuman(request.Id);
        Assert.Equal(2, events.Count);

        // Act - Submit
        queue.SubmitAnswer(request.Id, "Answer", "human");
        Assert.Equal(3, events.Count);

        // Act - Timeout another
        var request2 = new ClarificationRequest { GoalId = "g2", Question = "Q2" };
        queue.Enqueue(request2);
        Assert.Equal(4, events.Count);

        queue.MarkTimedOut(request2.Id);
        Assert.Equal(5, events.Count);
    }

    // ── Integration: Brain escalation triggers queue ───────────────────────

    [Fact]
    public async Task BrainEscalates_QueueCreated_RequestStatusCorrect()
    {
        // This test verifies that when Brain escalates AND both queue/composer are available,
        // a request IS created and then times out when no human answers.
        // Note: This test uses fake timeouts since we can't mock the Composer's AnswerClarificationAsync.
        
        // Arrange - Queue directly tests the timeout behavior
        var queue = new ClarificationQueueService();
        var request = new ClarificationRequest
        {
            GoalId = "goal-test",
            WorkerRole = "coder",
            Question = "What architecture?",
        };
        
        // Simulate what GoalDispatcher does when Brain escalates and Composer escalates
        var tcs = queue.Enqueue(request);
        queue.EscalateToHuman(request.Id); // Simulate Composer escalation
        
        // Assert - Request is now awaiting human
        Assert.Equal(1, queue.PendingHumanCount);
        Assert.Equal(ClarificationStatus.AwaitingHuman, request.Status);
        
        // Simulate timeout
        queue.MarkTimedOut(request.Id);
        
        // Assert - Timed out correctly
        Assert.Equal(0, queue.PendingHumanCount);
        Assert.Equal(ClarificationStatus.TimedOut, request.Status);
        Assert.Equal(ClarificationQueueService.TimeoutFallbackMessage, await tcs.Task);
    }

    // ── Integration: Full escalation chain with IClarificationRouter ────────

    [Fact]
    public async Task BrainEscalates_RouterAutoAnswers_ReturnsComposerAnswer()
    {
        // Arrange: Brain escalates, Router auto-answers successfully
        var brain = new FakeClarificationBrain(BrainResponse.Escalated("Cannot determine from codebase", "Cannot determine from codebase"));
        var queue = new ClarificationQueueService();
        var router = new FakeClarificationRouter("Use the Builder pattern.");
        var (dispatcher, pipeline) = CreateDispatcherWithClarification(brain, queue, router: router);

        // Act
        var answer = await dispatcher.AskBrainAsync(pipeline, "What design pattern?", TestContext.Current.CancellationToken);

        // Assert - Router auto-answered, no human escalation
        Assert.Equal("Use the Builder pattern.", answer);
        // Queue should have the request, resolved by composer
        var all = queue.GetAllRequests();
        Assert.Single(all);
        Assert.Equal(ClarificationStatus.Answered, all[0].Status);
        Assert.Equal("composer", all[0].ResolvedBy);
    }

    [Fact]
    public async Task BrainEscalates_RouterReturnsNull_EscalatesToHuman_HumanAnswers()
    {
        // Arrange: Brain escalates, Router escalates to human, human answers
        var brain = new FakeClarificationBrain(BrainResponse.Escalated("Need human input", "Need human input"));
        var queue = new ClarificationQueueService();
        var router = new FakeClarificationRouter(null); // null = escalate to human
        var (dispatcher, pipeline) = CreateDispatcherWithClarification(brain, queue, router: router);

        // Act - Start ask in background (it will wait for human answer)
        var askTask = dispatcher.AskBrainAsync(pipeline, "What timeout value?", TestContext.Current.CancellationToken);

        // Simulate human answering after a short delay
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var pending = queue.GetPendingHumanRequests();
        Assert.Single(pending);
        Assert.Equal(ClarificationStatus.AwaitingHuman, pending[0].Status);

        queue.SubmitAnswer(pending[0].Id, "Use 30 seconds", "human");

        // Assert
        var answer = await askTask;
        Assert.Equal("Use 30 seconds", answer);
    }

    [Fact]
    public async Task BrainEscalates_RouterReturnsNull_HumanTimesOut_ReturnsFallback()
    {
        // Arrange: Brain escalates, Router escalates to human, human never answers
        var brain = new FakeClarificationBrain(BrainResponse.Escalated("Need domain expertise", "Need domain expertise"));
        var queue = new ClarificationQueueService();
        var router = new FakeClarificationRouter(null); // null = escalate to human
        var (dispatcher, pipeline) = CreateDispatcherWithClarification(brain, queue, router: router);

        // Act - Start ask in background with a very short timeout
        // We can't wait 5 minutes in a test, so we simulate timeout by marking as timed out
        var askTask = dispatcher.AskBrainAsync(pipeline, "Domain-specific question?", TestContext.Current.CancellationToken);

        // Wait briefly for the request to be enqueued, then time it out
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var pending = queue.GetPendingHumanRequests();
        Assert.Single(pending);

        // Simulate timeout by marking the request and completing the TCS
        queue.MarkTimedOut(pending[0].Id);

        // Assert - askTask should complete with the fallback (or the MarkTimedOut completes the TCS)
        var answer = await askTask;
        Assert.Equal(ClarificationQueueService.TimeoutFallbackMessage, answer);
    }

    [Fact]
    public async Task BrainEscalates_EscalationResponse_TriggersComposerRouting()
    {
        // Arrange: Brain escalates to Composer
        var brain = new FakeClarificationBrain(BrainResponse.Escalated("I need the Composer's help", "I need the Composer's help"));
        var queue = new ClarificationQueueService();
        var router = new FakeClarificationRouter("Composer knows the answer.");
        var (dispatcher, pipeline) = CreateDispatcherWithClarification(brain, queue, router: router);

        // Act
        var answer = await dispatcher.AskBrainAsync(pipeline, "Complex question?", TestContext.Current.CancellationToken);

        // Assert - Router handled it
        Assert.Equal("Composer knows the answer.", answer);
    }

    // ── Composer exact string match for ESCALATE_TO_HUMAN ────────────────────

    [Fact]
    public async Task Composer_ReturnsEscalateToHuman_EscalatesToHuman()
    {
        // Arrange: Brain escalates, Composer returns exactly "ESCALATE_TO_HUMAN"
        var brain = new FakeClarificationBrain(BrainResponse.Escalated("Need human judgment", "Need human judgment"));
        var queue = new ClarificationQueueService();
        var router = new FakeClarificationRouter("ESCALATE_TO_HUMAN"); // Exact match triggers escalation
        var (dispatcher, pipeline) = CreateDispatcherWithClarification(brain, queue, router: router);

        // Act - Start ask in background
        var askTask = dispatcher.AskBrainAsync(pipeline, "What should the timeout be?", TestContext.Current.CancellationToken);

        // Wait briefly for request to be enqueued
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var pending = queue.GetPendingHumanRequests();

        // Assert - Request escalated to human (not answered by Composer)
        Assert.Single(pending);
        Assert.Equal(ClarificationStatus.AwaitingHuman, pending[0].Status);

        // Simulate human answering to complete the task
        queue.SubmitAnswer(pending[0].Id, "Use 5 minutes", "human");
        var answer = await askTask;
        Assert.Equal("Use 5 minutes", answer);
    }

    [Fact]
    public async Task Composer_ReturnsEmptyResponse_EscalatesToHuman()
    {
        // Arrange: Brain escalates, Composer returns empty string (should escalate)
        var brain = new FakeClarificationBrain(BrainResponse.Escalated("Cannot determine", "Cannot determine from codebase"));
        var queue = new ClarificationQueueService();
        var router = new FakeClarificationRouter(""); // Empty string should escalate
        var (dispatcher, pipeline) = CreateDispatcherWithClarification(brain, queue, router: router);

        // Act - Start ask in background
        var askTask = dispatcher.AskBrainAsync(pipeline, "What timeout?", TestContext.Current.CancellationToken);

        // Wait briefly for request to be enqueued
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var pending = queue.GetPendingHumanRequests();

        // Assert - Request escalated to human (empty response treated as escalation)
        Assert.Single(pending);
        Assert.Equal(ClarificationStatus.AwaitingHuman, pending[0].Status);

        // Complete the task
        queue.SubmitAnswer(pending[0].Id, "Use 30 seconds", "human");
        var answer = await askTask;
        Assert.Equal("Use 30 seconds", answer);
    }

    // ── Composer timeout simulation ───────────────────────────────────────────

    [Fact]
    public async Task Composer_TimesOut_EscalatesToHuman()
    {
        // Arrange: Brain escalates, Router simulates timeout by returning null (escalate to human)
        // This simulates the Composer catching OperationCanceledException internally and returning null
        var brain = new FakeClarificationBrain(BrainResponse.Escalated("Need clarification", "Need clarification"));
        var queue = new ClarificationQueueService();
        var router = new TimeoutClarificationRouter();
        var (dispatcher, pipeline) = CreateDispatcherWithClarification(brain, queue, router: router);

        // Act - Start ask in background (Router will time out immediately)
        var askTask = dispatcher.AskBrainAsync(pipeline, "What timeout?", TestContext.Current.CancellationToken);

        // Wait briefly for Router to time out and escalate to human
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var pending = queue.GetPendingHumanRequests();

        // Assert - Request escalated to human after timeout
        Assert.Single(pending);
        Assert.Equal(ClarificationStatus.AwaitingHuman, pending[0].Status);

        // Complete the task
        queue.SubmitAnswer(pending[0].Id, "Human answer after timeout", "human");
        var answer = await askTask;
        Assert.Equal("Human answer after timeout", answer);
    }

    // ── Human timeout (WaitAsync throws TimeoutException) ────────────────────

    [Fact]
    public async Task HumanWait_TimesOut_ReturnsFallbackMessage()
    {
        // This test exercises the TimeoutException catch block in AskBrainAsync (lines 247-252).
        // Since HumanTimeout is static readonly (5 minutes), we can't change it at runtime.
        // Instead, we directly test the ClarificationQueueService timeout behavior:
        // 1. Enqueue creates a TCS
        // 2. MarkTimedOut sets status and completes TCS with fallback message
        // This simulates what happens when WaitAsync(TimeSpan) throws TimeoutException
        // and the dispatcher calls MarkTimedOut.

        // Arrange: Enqueue a request and simulate timeout
        var queue = new ClarificationQueueService();
        var request = new ClarificationRequest
        {
            GoalId = "goal-timeout-test",
            WorkerRole = "coder",
            Question = "Question that times out?",
            Status = ClarificationStatus.AwaitingHuman,
        };
        var tcs = queue.Enqueue(request);

        // Act - Simulate timeout (this is what MarkTimedOut does when TimeoutException is caught)
        queue.MarkTimedOut(request.Id);

        // Assert - The TCS is completed with the fallback message
        Assert.True(tcs.Task.IsCompleted);
        Assert.Equal(ClarificationQueueService.TimeoutFallbackMessage, await tcs.Task);
        Assert.Equal(ClarificationStatus.TimedOut, request.Status);

        // This verifies the queue behavior: when MarkTimedOut is called (by the dispatcher's
        // TimeoutException catch block), the TCS completes with the fallback message.
    }

    [Fact]
    public async Task AskBrainAsync_WaitAsyncThrowsTimeoutException_ReturnsFallbackMessage()
    {
        // This test exercises the catch (TimeoutException) block in AskBrainAsync (lines 247-252).
        // We use a custom router that faults the TCS with TimeoutException after enqueue.
        // When WaitAsync is called on a faulted task, it throws the fault exception immediately,
        // triggering the dispatcher's catch block.

        // Arrange: Router escalates to human and then faults the TCS with TimeoutException
        var brain = new FakeClarificationBrain(BrainResponse.Escalated("Need human input", "Need human input"));
        var queue = new ClarificationQueueService();
        var router = new TimeoutFaultingClarificationRouter();
        var (dispatcher, pipeline) = CreateDispatcherWithClarification(brain, queue, router: router);

        // Act - AskBrainAsync will:
        // 1. Enqueue request (creates TCS)
        // 2. Call router.TryAutoAnswerAsync which faults the TCS with TimeoutException
        // 3. Router returns null (escalate to human)
        // 4. Dispatcher calls tcs.Task.WaitAsync() which throws TimeoutException
        // 5. Dispatcher catches TimeoutException and returns fallback
        var answer = await dispatcher.AskBrainAsync(pipeline, "What should I do?", TestContext.Current.CancellationToken);

        // Assert - Dispatcher caught TimeoutException and returned fallback
        Assert.Equal(ClarificationQueueService.TimeoutFallbackMessage, answer);

        // Verify the request was marked as timed out
        var allRequests = queue.GetAllRequests();
        Assert.Single(allRequests);
        Assert.Equal(ClarificationStatus.TimedOut, allRequests[0].Status);
    }

    [Fact]
    public async Task HumanWait_WithCancellation_ThrowsTaskCanceledException()
    {
        // This test verifies the cancellation path when waiting for human answer.
        // When the cancellation token is cancelled, WaitAsync throws TaskCanceledException
        // (a subclass of OperationCanceledException).
        // Note: This tests the cancellation path, not the timeout path.

        // Arrange
        var queue = new ClarificationQueueService();
        var request = new ClarificationRequest
        {
            GoalId = "goal-cancel-test",
            WorkerRole = "coder",
            Question = "Question?",
            Status = ClarificationStatus.AwaitingHuman,
        };
        var tcs = queue.Enqueue(request);

        // Create a cancellation token that's already cancelled
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert - WaitAsync throws TaskCanceledException when cancelled
        // (This is a different path than TimeoutException)
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => tcs.Task.WaitAsync(TimeSpan.FromMinutes(5), cts.Token));
    }

    [Fact]
    public async Task MarkTimedOut_SetsStatusAndCompletesTcsWithFallback()
    {
        // Direct unit test for MarkTimedOut behavior used by TimeoutException catch block.
        // Arrange
        var queue = new ClarificationQueueService();
        var request = new ClarificationRequest
        {
            GoalId = "goal-mark-test",
            WorkerRole = "coder",
            Question = "Question?",
        };
        var tcs = queue.Enqueue(request);
        queue.EscalateToHuman(request.Id);

        // Pre-condition
        Assert.Equal(ClarificationStatus.AwaitingHuman, request.Status);
        Assert.False(tcs.Task.IsCompleted);

        // Act
        queue.MarkTimedOut(request.Id);

        // Assert
        Assert.True(tcs.Task.IsCompleted);
        Assert.Equal(ClarificationStatus.TimedOut, request.Status);
        Assert.Equal(ClarificationQueueService.TimeoutFallbackMessage, await tcs.Task);
    }

    // ── Composer throws OperationCanceledException (unhandled in GoalDispatcher) ──────

    [Fact]
    public async Task Composer_ThrowsOperationCanceled_PropagatesUp()
    {
        // This test verifies that if the IClarificationRouter throws OperationCanceledException,
        // it propagates up unhandled (GoalDispatcher does NOT catch it for the router call).
        // Note: The real Composer catches OperationCanceledException internally and returns null,
        // so this test documents the expected behavior if a custom router throws.

        // Arrange: Router throws OperationCanceledException
        var brain = new FakeClarificationBrain(BrainResponse.Escalated("Need help", "Need help"));
        var queue = new ClarificationQueueService();
        var router = new ThrowingClarificationRouter(new OperationCanceledException());
        var (dispatcher, pipeline) = CreateDispatcherWithClarification(brain, queue, router: router);

        // Act & Assert - OperationCanceledException propagates up
        var ex = await Assert.ThrowsAsync<OperationCanceledException>(
            () => dispatcher.AskBrainAsync(pipeline, "Question?", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void GetPendingHumanRequests_ReturnsOnlyAwaitingHuman()
    {
        // Arrange
        var queue = new ClarificationQueueService();

        var req1 = new ClarificationRequest { GoalId = "g1", Question = "Q1" };
        req1.Status = ClarificationStatus.AwaitingHuman;
        queue.Enqueue(req1);

        var req2 = new ClarificationRequest { GoalId = "g2", Question = "Q2" };
        req2.Status = ClarificationStatus.AwaitingComposer;
        queue.Enqueue(req2);

        var req3 = new ClarificationRequest { GoalId = "g3", Question = "Q3" };
        req3.Status = ClarificationStatus.AwaitingHuman;
        queue.Enqueue(req3);

        // Act
        var pending = queue.GetPendingHumanRequests();

        // Assert
        Assert.Equal(2, pending.Count);
        Assert.All(pending, r => Assert.Equal(ClarificationStatus.AwaitingHuman, r.Status));
        Assert.Equal(2, queue.PendingHumanCount);
    }

    [Fact]
    public void GetAllRequests_ReturnsNewestFirst()
    {
        // Arrange
        var queue = new ClarificationQueueService();

        var req1 = new ClarificationRequest
        {
            GoalId = "g1",
            Question = "Q1",
            RequestedAt = new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc),
        };
        var req2 = new ClarificationRequest
        {
            GoalId = "g2",
            Question = "Q2",
            RequestedAt = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        };

        queue.Enqueue(req1);
        queue.Enqueue(req2);

        // Act
        var all = queue.GetAllRequests();

        // Assert
        Assert.Equal(2, all.Count);
        Assert.Equal("g2", all[0].GoalId); // Newer first
        Assert.Equal("g1", all[1].GoalId);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static (GoalDispatcher dispatcher, GoalPipeline pipeline) CreateDispatcherWithClarification(
        IDistributedBrain brain,
        ClarificationQueueService? queue,
        bool useComposer = false,
        bool passQueue = false,
        IClarificationRouter? router = null)
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
        var goalSource = new FakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult(); // Populate internal map

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);
        pipeline.AdvanceTo(GoalPhase.Coding);

        var notifier = new TaskCompletionNotifier();

        // passQueue/useComposer: legacy tests that check queue-only behavior
        // router: new tests that exercise the full escalation chain
        var dispatcher = new GoalDispatcher(
            goalManager,
            pipelineManager,
            new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            notifier,
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain,
            clarificationRouter: router,
            clarificationQueue: (useComposer || passQueue || router is not null) ? queue : null);

        return (dispatcher, pipeline);
    }

    // ── Fake implementations ───────────────────────────────────────────────

    /// <summary>
    /// Fake Brain that returns a configured BrainResponse for AskQuestionAsync calls.
    /// </summary>
    private sealed class FakeClarificationBrain : IDistributedBrain
    {
        private readonly BrainResponse _askBrainResponse;

        public FakeClarificationBrain(BrainResponse askBrainResponse)
        {
            _askBrainResponse = askBrainResponse;
        }

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success(_askBrainResponse.Text ?? string.Empty));

        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
            Task.FromResult(_askBrainResponse);

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
            Task.CompletedTask;

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public void DeleteGoalSession(string goalId) { }

    public bool GoalSessionExists(string goalId) => false;

        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

        public BrainStats? GetStats() => null;
    }

    /// <summary>
    /// Fake <see cref="IClarificationRouter"/> that returns a pre-configured answer
    /// or <c>null</c> to simulate escalation to human.
    /// </summary>
    private sealed class FakeClarificationRouter : IClarificationRouter
    {
        private readonly string? _autoAnswer;

        /// <param name="autoAnswer">
        /// If non-null and not "ESCALATE_TO_HUMAN", returned as the auto-answer.
        /// If null or "ESCALATE_TO_HUMAN", escalates to human.
        /// </param>
        public FakeClarificationRouter(string? autoAnswer)
        {
            _autoAnswer = autoAnswer;
        }

        public Task<string?> TryAutoAnswerAsync(
            string goalId,
            string question,
            string context,
            ClarificationQueueService clarificationQueue,
            ClarificationRequest request,
            CancellationToken ct = default)
        {
            // Match the Composer's behavior: empty or ESCALATE_TO_HUMAN means escalate
            if (_autoAnswer is null || _autoAnswer == "ESCALATE_TO_HUMAN" || string.IsNullOrEmpty(_autoAnswer))
            {
                clarificationQueue.EscalateToHuman(request.Id);
                return Task.FromResult<string?>(null);
            }

            return Task.FromResult<string?>(_autoAnswer);
        }
    }

    /// <summary>
    /// Fake <see cref="IClarificationRouter"/> that simulates a timeout by escalating to human.
    /// This matches the real Composer behavior when the 30-second timeout expires.
    /// </summary>
    private sealed class TimeoutClarificationRouter : IClarificationRouter
    {
        public Task<string?> TryAutoAnswerAsync(
            string goalId,
            string question,
            string context,
            ClarificationQueueService clarificationQueue,
            ClarificationRequest request,
            CancellationToken ct = default)
        {
            // Simulate the Composer timing out (as if ClarificationQueueService.ComposerTimeout elapsed)
            clarificationQueue.EscalateToHuman(request.Id);
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// Fake <see cref="IClarificationRouter"/> that throws an exception.
    /// Used to verify exception propagation behavior.
    /// </summary>
    private sealed class ThrowingClarificationRouter : IClarificationRouter
    {
        private readonly Exception _exception;

        public ThrowingClarificationRouter(Exception exception)
        {
            _exception = exception;
        }

        public Task<string?> TryAutoAnswerAsync(
            string goalId,
            string question,
            string context,
            ClarificationQueueService clarificationQueue,
            ClarificationRequest request,
            CancellationToken ct = default)
        {
            throw _exception;
        }
    }

    /// <summary>
    /// Router that escalates to human and faults the TCS with TimeoutException.
    /// When the dispatcher calls WaitAsync on the faulted task, it throws TimeoutException,
    /// exercising the catch block in AskBrainAsync.
    /// </summary>
    private sealed class TimeoutFaultingClarificationRouter : IClarificationRouter
    {
        public Task<string?> TryAutoAnswerAsync(
            string goalId,
            string question,
            string context,
            ClarificationQueueService clarificationQueue,
            ClarificationRequest request,
            CancellationToken ct = default)
        {
            // First, escalate to human (this is required for the flow)
            clarificationQueue.EscalateToHuman(request.Id);

            // Now use reflection to get the TCS and fault it with TimeoutException.
            // The TCS is stored in ClarificationQueueService._waiters ConcurrentDictionary.
            var waitersField = typeof(ClarificationQueueService)
                .GetField("_waiters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?? throw new InvalidOperationException("Could not find _waiters field");

            var waiters = waitersField.GetValue(clarificationQueue)
                as System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<string>>
                ?? throw new InvalidOperationException("_waiters is not a ConcurrentDictionary<string, TaskCompletionSource<string>>");

            if (waiters.TryGetValue(request.Id, out var tcs))
            {
                // Fault the TCS with TimeoutException - when WaitAsync is called on this,
                // it will throw TimeoutException immediately
                tcs.SetException(new TimeoutException("Simulated human timeout"));
            }

            return Task.FromResult<string?>(null);
        }
    }

    private sealed class FakeGoalSource : IGoalSource
    {
        private readonly Goal _goal;
        private bool _returned;

        public FakeGoalSource(Goal goal) => _goal = goal;

        public string Name => "fake";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default)
        {
            if (_returned) return Task.FromResult<IReadOnlyList<Goal>>(Array.Empty<Goal>());
            _returned = true;
            return Task.FromResult<IReadOnlyList<Goal>>(new[] { _goal });
        }

        public Task UpdateGoalStatusAsync(string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
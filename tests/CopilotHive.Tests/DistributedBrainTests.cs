using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

public sealed class DistributedBrainTests
{
    // ── TaskCompletionNotifier Tests ─────────────────────────────────────

    [Fact]
    public async Task NotifyAsync_NoSubscribers_DoesNotThrow()
    {
        var notifier = new TaskCompletionNotifier();

        var complete = new TaskComplete { TaskId = "t-1", Status = Shared.Grpc.TaskStatus.Completed, Output = "done" };

        await notifier.NotifyAsync(complete);
    }

    [Fact]
    public async Task NotifyAsync_SingleSubscriber_ReceivesCorrectTaskComplete()
    {
        var notifier = new TaskCompletionNotifier();
        TaskComplete? received = null;

        notifier.OnTaskCompleted += tc =>
        {
            received = tc;
            return Task.CompletedTask;
        };

        var complete = new TaskComplete { TaskId = "t-42", Status = Shared.Grpc.TaskStatus.Completed, Output = "all tests pass" };
        await notifier.NotifyAsync(complete);

        Assert.NotNull(received);
        Assert.Equal("t-42", received.TaskId);
        Assert.Equal(Shared.Grpc.TaskStatus.Completed, received.Status);
        Assert.Equal("all tests pass", received.Output);
    }

    [Fact]
    public async Task NotifyAsync_MultipleSubscribers_AllGetInvoked()
    {
        var notifier = new TaskCompletionNotifier();
        var invocations = new List<string>();

        notifier.OnTaskCompleted += tc => { invocations.Add("sub1"); return Task.CompletedTask; };
        notifier.OnTaskCompleted += tc => { invocations.Add("sub2"); return Task.CompletedTask; };
        notifier.OnTaskCompleted += tc => { invocations.Add("sub3"); return Task.CompletedTask; };

        var complete = new TaskComplete { TaskId = "t-99", Status = Shared.Grpc.TaskStatus.Completed, Output = "" };
        await notifier.NotifyAsync(complete);

        Assert.Equal(3, invocations.Count);
        Assert.Contains("sub1", invocations);
        Assert.Contains("sub2", invocations);
        Assert.Contains("sub3", invocations);
    }

    // ── DistributedBrain Constructor / Static Tests ─────────────────────

    [Fact]
    public void Constructor_ValidArgs_CreatesInstance()
    {
        var brain = new DistributedBrain(9999, NullLogger<DistributedBrain>.Instance);

        Assert.NotNull(brain);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8080)]
    [InlineData(65535)]
    public void Constructor_VariousPorts_CreatesInstance(int port)
    {
        var brain = new DistributedBrain(port, NullLogger<DistributedBrain>.Instance);

        Assert.NotNull(brain);
    }

    [Fact]
    public async Task DisposeAsync_BeforeConnect_DoesNotThrow()
    {
        var brain = new DistributedBrain(9999, NullLogger<DistributedBrain>.Instance);

        await brain.DisposeAsync();
    }

    // ── GetFallbackPrompt (via CraftPromptAsync without connection) ─────

    [Theory]
    [InlineData("coder", "Write the code and commit")]
    [InlineData("reviewer", "REVIEW_REPORT")]
    [InlineData("tester", "TEST_REPORT")]
    public void GetFallbackPrompt_KnownRoles_ContainsExpectedContent(string role, string expectedFragment)
    {
        // GetFallbackPrompt is private static, but we can invoke it via reflection
        var method = typeof(DistributedBrain).GetMethod(
            "GetFallbackPrompt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var pipeline = CreatePipeline("test-goal", "Implement feature X");
        var result = (string)method.Invoke(null, [role, pipeline])!;

        Assert.Contains(expectedFragment, result);
        Assert.Contains("Implement feature X", result);
    }

    [Fact]
    public void GetFallbackPrompt_UnknownRole_ReturnsGenericPrompt()
    {
        var method = typeof(DistributedBrain).GetMethod(
            "GetFallbackPrompt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var pipeline = CreatePipeline("g-1", "Fix the bug");
        var result = (string)method.Invoke(null, ["unknown-role", pipeline])!;

        Assert.Contains("Fix the bug", result);
    }

    // ── BuildContextualPrompt was removed (per-goal sessions handle context natively) ──

    // ── EnsureConnected fallback behavior ───────────────────────────────
    // With per-goal sessions, AskAsync catches the InvalidOperationException
    // and returns null, causing callers to use fallback logic instead of throwing.

    [Fact]
    public async Task PlanGoalAsync_WithoutConnect_ReturnsFallbackDecision()
    {
        var brain = new DistributedBrain(9999, NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-3", "Some goal");

        var decision = await brain.PlanGoalAsync(pipeline);

        Assert.Equal(OrchestratorActionType.SpawnCoder, decision.Action);
        Assert.Contains("Default", decision.Reason);
    }

    [Fact]
    public async Task CraftPromptAsync_WithoutConnect_ReturnsFallbackPrompt()
    {
        var brain = new DistributedBrain(9999, NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-4", "Some goal");

        var prompt = await brain.CraftPromptAsync(pipeline, "coder", null);

        Assert.Contains("Some goal", prompt);
    }

    [Fact]
    public async Task InformAsync_WithoutConnect_ThrowsInvalidOperation()
    {
        var brain = new DistributedBrain(9999, NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-5", "Some goal");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => brain.InformAsync(pipeline, "status update"));
    }

    // ── FakeDistributedBrain (IDistributedBrain stub) ───────────────────

    [Fact]
    public async Task FakeDistributedBrain_PlanGoalAsync_ReturnsConfiguredDecision()
    {
        var fake = new FakeDistributedBrain();
        fake.PlanGoalOverride = _ => new OrchestratorDecision
        {
            Action = OrchestratorActionType.SpawnReviewer,
            Reason = "Skip coding — docs only",
        };

        var pipeline = CreatePipeline("g-6", "Update README");
        var decision = await fake.PlanGoalAsync(pipeline);

        Assert.Equal(OrchestratorActionType.SpawnReviewer, decision.Action);
        Assert.Equal("Skip coding — docs only", decision.Reason);
    }

    [Fact]
    public async Task FakeDistributedBrain_CraftPromptAsync_ReturnsPrompt()
    {
        var fake = new FakeDistributedBrain();
        var pipeline = CreatePipeline("g-7", "Add tests");

        var prompt = await fake.CraftPromptAsync(pipeline, "tester", "extra context");

        Assert.Contains("Add tests", prompt);
        Assert.Contains("tester", prompt);
    }

    [Fact]
    public async Task FakeDistributedBrain_InterpretOutputAsync_DefaultReturnsDone()
    {
        var fake = new FakeDistributedBrain();
        var pipeline = CreatePipeline("g-8", "Fix bug");

        var decision = await fake.InterpretOutputAsync(pipeline, "coder", "all done");

        Assert.Equal(OrchestratorActionType.Done, decision.Action);
    }

    [Fact]
    public async Task FakeDistributedBrain_TracksAllCalls()
    {
        var fake = new FakeDistributedBrain();
        var pipeline = CreatePipeline("g-9", "Multi-step goal");

        await fake.ConnectAsync();
        await fake.PlanGoalAsync(pipeline);
        await fake.CraftPromptAsync(pipeline, "coder");
        await fake.InterpretOutputAsync(pipeline, "coder", "output");
        await fake.DecideNextStepAsync(pipeline, "review passed");
        await fake.InformAsync(pipeline, "merge complete");

        Assert.True(fake.Connected);
        Assert.Equal(1, fake.PlanCalls);
        Assert.Equal(1, fake.CraftCalls);
        Assert.Equal(1, fake.InterpretCalls);
        Assert.Equal(1, fake.DecideCalls);
        Assert.Single(fake.Informations);
        Assert.Equal("merge complete", fake.Informations[0]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static GoalPipeline CreatePipeline(string goalId, string description) =>
        new(new Goal { Id = goalId, Description = description });
}

/// <summary>
/// Minimal fake implementing <see cref="IDistributedBrain"/> for unit tests.
/// Mirrors the pattern in <c>FakeOrchestratorBrain</c>.
/// </summary>
file sealed class FakeDistributedBrain : IDistributedBrain
{
    public bool Connected { get; private set; }
    public int PlanCalls { get; private set; }
    public int CraftCalls { get; private set; }
    public int InterpretCalls { get; private set; }
    public int DecideCalls { get; private set; }
    public List<string> Informations { get; } = [];

    public Func<GoalPipeline, OrchestratorDecision>? PlanGoalOverride { get; set; }
    public Func<GoalPipeline, string, string?, string>? CraftPromptOverride { get; set; }
    public Func<GoalPipeline, string, string, OrchestratorDecision>? InterpretOutputOverride { get; set; }
    public Func<GoalPipeline, string, OrchestratorDecision>? DecideNextStepOverride { get; set; }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        Connected = true;
        return Task.CompletedTask;
    }

    public Task<OrchestratorDecision> PlanGoalAsync(GoalPipeline pipeline, CancellationToken ct = default)
    {
        PlanCalls++;
        var decision = PlanGoalOverride?.Invoke(pipeline)
            ?? new OrchestratorDecision { Action = OrchestratorActionType.SpawnCoder, Reason = "Default plan" };
        return Task.FromResult(decision);
    }

    public Task<string> CraftPromptAsync(
        GoalPipeline pipeline, string workerRole, string? additionalContext = null, CancellationToken ct = default)
    {
        CraftCalls++;
        var prompt = CraftPromptOverride?.Invoke(pipeline, workerRole, additionalContext)
            ?? $"Work on {pipeline.Description} as {workerRole}";
        return Task.FromResult(prompt);
    }

    public Task<OrchestratorDecision> InterpretOutputAsync(
        GoalPipeline pipeline, string workerRole, string workerOutput, CancellationToken ct = default)
    {
        InterpretCalls++;
        var decision = InterpretOutputOverride?.Invoke(pipeline, workerRole, workerOutput)
            ?? new OrchestratorDecision { Action = OrchestratorActionType.Done, Verdict = "PASS" };
        return Task.FromResult(decision);
    }

    public Task<OrchestratorDecision> DecideNextStepAsync(
        GoalPipeline pipeline, string context, CancellationToken ct = default)
    {
        DecideCalls++;
        var decision = DecideNextStepOverride?.Invoke(pipeline, context)
            ?? new OrchestratorDecision { Action = OrchestratorActionType.Done, Reason = "Default: done" };
        return Task.FromResult(decision);
    }

    public Task InformAsync(GoalPipeline pipeline, string information, CancellationToken ct = default)
    {
        Informations.Add(information);
        return Task.CompletedTask;
    }
}

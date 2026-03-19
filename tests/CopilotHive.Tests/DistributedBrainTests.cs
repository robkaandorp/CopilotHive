using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Shared.Grpc;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;

using WorkerRole = CopilotHive.Workers.WorkerRole;

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

    // ── GetFallbackPrompt removed — Brain failure = failed goal ─────────

    // ── EnsureConnected fallback behavior ───────────────────────────────
    // With per-goal sessions, AskAsync catches the InvalidOperationException
    // and returns null. CraftPromptAsync now throws instead of falling back.

    [Fact]
    public async Task PlanGoalAsync_WithoutConnect_ReturnsFallbackDecision()
    {
        var brain = new DistributedBrain(9999, NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-3", "Some goal");

        var decision = await brain.PlanGoalAsync(pipeline, TestContext.Current.CancellationToken);

        Assert.Equal(OrchestratorActionType.SpawnCoder, decision.Action);
        Assert.Contains("Default", decision.Reason);
    }

    [Fact]
    public async Task CraftPromptAsync_WithoutConnect_Throws()
    {
        var brain = new DistributedBrain(9999, NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-4", "Some goal");
        pipeline.SetActiveTask("task-1", "copilothive/g-4");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => brain.CraftPromptAsync(pipeline, WorkerRole.Coder, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InformAsync_WithoutConnect_ThrowsInvalidOperation()
    {
        var brain = new DistributedBrain(9999, NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-5", "Some goal");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => brain.InformAsync(pipeline, "status update", TestContext.Current.CancellationToken));
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
        var decision = await fake.PlanGoalAsync(pipeline, TestContext.Current.CancellationToken);

        Assert.Equal(OrchestratorActionType.SpawnReviewer, decision.Action);
        Assert.Equal("Skip coding — docs only", decision.Reason);
    }

    [Fact]
    public async Task FakeDistributedBrain_CraftPromptAsync_ReturnsPrompt()
    {
        var fake = new FakeDistributedBrain();
        var pipeline = CreatePipeline("g-7", "Add tests");

        var prompt = await fake.CraftPromptAsync(pipeline, WorkerRole.Tester, "extra context", TestContext.Current.CancellationToken);

        Assert.Contains("Add tests", prompt);
        Assert.Contains("tester", prompt);
    }

    [Fact]
    public async Task FakeDistributedBrain_InterpretOutputAsync_DefaultReturnsDone()
    {
        var fake = new FakeDistributedBrain();
        var pipeline = CreatePipeline("g-8", "Fix bug");

        var decision = await fake.InterpretOutputAsync(pipeline, GoalPhase.Coding, "all done", TestContext.Current.CancellationToken);

        Assert.Equal(OrchestratorActionType.Done, decision.Action);
    }

    [Fact]
    public async Task FakeDistributedBrain_TracksAllCalls()
    {
        var fake = new FakeDistributedBrain();
        var pipeline = CreatePipeline("g-9", "Multi-step goal");

        await fake.ConnectAsync(TestContext.Current.CancellationToken);
        await fake.PlanGoalAsync(pipeline, TestContext.Current.CancellationToken);
        await fake.CraftPromptAsync(pipeline, WorkerRole.Coder, null, TestContext.Current.CancellationToken);
        await fake.InterpretOutputAsync(pipeline, GoalPhase.Coding, "output", TestContext.Current.CancellationToken);
        await fake.DecideNextStepAsync(pipeline, "review passed", TestContext.Current.CancellationToken);
        await fake.InformAsync(pipeline, "merge complete", TestContext.Current.CancellationToken);

        Assert.True(fake.Connected);
        Assert.Equal(1, fake.PlanCalls);
        Assert.Equal(1, fake.CraftCalls);
        Assert.Equal(1, fake.InterpretCalls);
        Assert.Equal(1, fake.DecideCalls);
        Assert.Single(fake.Informations);
        Assert.Equal("merge complete", fake.Informations[0]);
    }

    // ── BuildDecisionFromToolCall Tests ────────────────────────────────

    [Fact]
    public void BuildDecisionFromToolCall_ReportPlan_BuildsCorrectDecision()
    {
        var toolCall = new DistributedBrain.BrainToolCallResult("report_plan", new Dictionary<string, object?>
        {
            ["action"] = "spawn_coder",
            ["prompt"] = "Implement the feature",
            ["reason"] = "Starting with coding phase",
            ["model_tier"] = "standard",
        });

        var decision = DistributedBrain.BuildDecisionFromToolCall(toolCall);

        Assert.Equal(OrchestratorActionType.SpawnCoder, decision.Action);
        Assert.Equal("Implement the feature", decision.Prompt);
        Assert.Equal("Starting with coding phase", decision.Reason);
        Assert.Equal("standard", decision.ModelTier);
    }

    [Fact]
    public void BuildDecisionFromToolCall_ReportInterpretation_BuildsCorrectDecision()
    {
        var toolCall = new DistributedBrain.BrainToolCallResult("report_interpretation", new Dictionary<string, object?>
        {
            ["verdict"] = "PASS",
            ["review_verdict"] = "",
            ["issues"] = new string[] { "minor style issue" },
            ["reason"] = "All tests passed, one minor style nit",
            ["model_tier"] = "standard",
        });

        var decision = DistributedBrain.BuildDecisionFromToolCall(toolCall);

        Assert.Equal(OrchestratorActionType.Done, decision.Action);
        Assert.Equal("PASS", decision.Verdict);
        Assert.Null(decision.ReviewVerdict);
        Assert.Single(decision.Issues!);
        Assert.Equal("minor style issue", decision.Issues![0]);
        Assert.Equal("All tests passed, one minor style nit", decision.Reason);
    }

    [Fact]
    public void BuildDecisionFromToolCall_ReportInterpretation_ReviewVerdict()
    {
        var toolCall = new DistributedBrain.BrainToolCallResult("report_interpretation", new Dictionary<string, object?>
        {
            ["verdict"] = "FAIL",
            ["review_verdict"] = "REQUEST_CHANGES",
            ["issues"] = new string[] { "missing error handling", "no tests" },
            ["reason"] = "Reviewer found critical issues",
            ["model_tier"] = "premium",
        });

        var decision = DistributedBrain.BuildDecisionFromToolCall(toolCall);

        Assert.Equal("FAIL", decision.Verdict);
        Assert.Equal("REQUEST_CHANGES", decision.ReviewVerdict);
        Assert.Equal(2, decision.Issues!.Count);
        Assert.Equal("Reviewer found critical issues", decision.Reason);
        Assert.Equal("premium", decision.ModelTier);
    }

    [Fact]
    public void BuildDecisionFromToolCall_UnknownTool_Throws()
    {
        var toolCall = new DistributedBrain.BrainToolCallResult("unknown_tool", new Dictionary<string, object?>());

        Assert.Throws<InvalidOperationException>(() => DistributedBrain.BuildDecisionFromToolCall(toolCall));
    }

    [Theory]
    [InlineData("spawn_coder", OrchestratorActionType.SpawnCoder)]
    [InlineData("spawn_tester", OrchestratorActionType.SpawnTester)]
    [InlineData("spawn_reviewer", OrchestratorActionType.SpawnReviewer)]
    [InlineData("spawn_doc_writer", OrchestratorActionType.SpawnDocWriter)]
    [InlineData("spawn_improver", OrchestratorActionType.SpawnImprover)]
    [InlineData("request_changes", OrchestratorActionType.RequestChanges)]
    [InlineData("retry", OrchestratorActionType.Retry)]
    [InlineData("merge", OrchestratorActionType.Merge)]
    [InlineData("done", OrchestratorActionType.Done)]
    [InlineData("skip", OrchestratorActionType.Skip)]
    public void BuildDecisionFromToolCall_AllActions_ParseCorrectly(string actionStr, OrchestratorActionType expected)
    {
        var toolCall = new DistributedBrain.BrainToolCallResult("report_plan", new Dictionary<string, object?>
        {
            ["action"] = actionStr, ["prompt"] = "", ["reason"] = "", ["model_tier"] = "standard",
        });

        var decision = DistributedBrain.BuildDecisionFromToolCall(toolCall);

        Assert.Equal(expected, decision.Action);
    }

    [Fact]
    public void BuildDecisionFromToolCall_InvalidAction_Throws()
    {
        var toolCall = new DistributedBrain.BrainToolCallResult("report_plan", new Dictionary<string, object?>
        {
            ["action"] = "invalid_action", ["prompt"] = "", ["reason"] = "", ["model_tier"] = "standard",
        });

        Assert.Throws<InvalidOperationException>(() => DistributedBrain.BuildDecisionFromToolCall(toolCall));
    }

    // ── BuildIterationPlanFromToolCall Tests ─────────────────────────────

    [Fact]
    public void BuildIterationPlanFromToolCall_ValidPhases_BuildsCorrectPlan()
    {
        var toolCall = new DistributedBrain.BrainToolCallResult("report_iteration_plan", new Dictionary<string, object?>
        {
            ["phases"] = new string[] { "coding", "testing", "review", "merging" },
            ["phase_instructions"] = """{"coding":"focus on tests","review":"check edge cases"}""",
            ["reason"] = "Standard workflow",
        });

        var plan = DistributedBrain.BuildIterationPlanFromToolCall(toolCall);

        Assert.Equal(4, plan.Phases.Count);
        Assert.Equal(GoalPhase.Coding, plan.Phases[0]);
        Assert.Equal(GoalPhase.Testing, plan.Phases[1]);
        Assert.Equal(GoalPhase.Review, plan.Phases[2]);
        Assert.Equal(GoalPhase.Merging, plan.Phases[3]);
        Assert.Equal("Standard workflow", plan.Reason);
        Assert.Equal("focus on tests", plan.PhaseInstructions[GoalPhase.Coding]);
    }

    [Fact]
    public void BuildIterationPlanFromToolCall_EmptyPhases_ReturnsEmptyPlan()
    {
        var toolCall = new DistributedBrain.BrainToolCallResult("report_iteration_plan", new Dictionary<string, object?>
        {
            ["phases"] = Array.Empty<string>(),
            ["phase_instructions"] = "{}",
            ["reason"] = "nothing to do",
        });

        var plan = DistributedBrain.BuildIterationPlanFromToolCall(toolCall);

        Assert.Empty(plan.Phases);
    }

    [Fact]
    public void BuildIterationPlanFromToolCall_InvalidPhaseNames_Skipped()
    {
        var toolCall = new DistributedBrain.BrainToolCallResult("report_iteration_plan", new Dictionary<string, object?>
        {
            ["phases"] = new string[] { "coding", "invalid_phase", "testing" },
            ["phase_instructions"] = "{}",
            ["reason"] = "test",
        });

        var plan = DistributedBrain.BuildIterationPlanFromToolCall(toolCall);

        Assert.Equal(2, plan.Phases.Count);
        Assert.Equal(GoalPhase.Coding, plan.Phases[0]);
        Assert.Equal(GoalPhase.Testing, plan.Phases[1]);
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
    public int PlanIterationCalls { get; private set; }
    public int CraftCalls { get; private set; }
    public int InterpretCalls { get; private set; }
    public int DecideCalls { get; private set; }
    public List<string> Informations { get; } = [];

    public Func<GoalPipeline, OrchestratorDecision>? PlanGoalOverride { get; set; }
    public Func<GoalPipeline, IterationPlan>? PlanIterationOverride { get; set; }
    public Func<GoalPipeline, WorkerRole, string?, string>? CraftPromptOverride { get; set; }
    public Func<GoalPipeline, GoalPhase, string, OrchestratorDecision>? InterpretOutputOverride { get; set; }
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

    public Task<IterationPlan> PlanIterationAsync(GoalPipeline pipeline, CancellationToken ct = default)
    {
        PlanIterationCalls++;
        var plan = PlanIterationOverride?.Invoke(pipeline) ?? IterationPlan.Default();
        return Task.FromResult(plan);
    }

    public Task<string> CraftPromptAsync(
        GoalPipeline pipeline, WorkerRole role, string? additionalContext = null, CancellationToken ct = default)
    {
        CraftCalls++;
        var prompt = CraftPromptOverride?.Invoke(pipeline, role, additionalContext)
            ?? $"Work on {pipeline.Description} as {role.ToRoleName()}";
        return Task.FromResult(prompt);
    }

    public Task<OrchestratorDecision> InterpretOutputAsync(GoalPipeline pipeline, GoalPhase phase, string workerOutput, CancellationToken ct = default)
    {
        InterpretCalls++;
        var decision = InterpretOutputOverride?.Invoke(pipeline, phase, workerOutput)
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

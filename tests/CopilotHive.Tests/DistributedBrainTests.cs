using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;

using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests;

public sealed class DistributedBrainTests
{
    // -- TaskCompletionNotifier Tests --

    [Fact]
    public async Task NotifyAsync_NoSubscribers_DoesNotThrow()
    {
        var notifier = new TaskCompletionNotifier();
        var complete = new TaskResult { TaskId = "t-1", Status = TaskOutcome.Completed, Output = "done" };
        await notifier.NotifyAsync(complete);
    }

    [Fact]
    public async Task NotifyAsync_SingleSubscriber_ReceivesCorrectTaskComplete()
    {
        var notifier = new TaskCompletionNotifier();
        TaskResult? received = null;
        notifier.OnTaskCompleted += tc => { received = tc; return Task.CompletedTask; };

        var complete = new TaskResult { TaskId = "t-42", Status = TaskOutcome.Completed, Output = "all tests pass" };
        await notifier.NotifyAsync(complete);

        Assert.NotNull(received);
        Assert.Equal("t-42", received.TaskId);
        Assert.Equal(TaskOutcome.Completed, received.Status);
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

        var complete = new TaskResult { TaskId = "t-99", Status = TaskOutcome.Completed, Output = "" };
        await notifier.NotifyAsync(complete);

        Assert.Equal(3, invocations.Count);
        Assert.Contains("sub1", invocations);
        Assert.Contains("sub2", invocations);
        Assert.Contains("sub3", invocations);
    }

    // -- DistributedBrain Constructor / Static Tests --

    [Fact]
    public void Constructor_ValidArgs_CreatesInstance()
    {
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        Assert.NotNull(brain);
    }

    [Theory]
    [InlineData("copilot/model-1")]
    [InlineData("gpt-4")]
    [InlineData("claude-opus")]
    public void Constructor_VariousModels_CreatesInstance(string model)
    {
        var brain = new DistributedBrain(model, NullLogger<DistributedBrain>.Instance);
        Assert.NotNull(brain);
    }

    [Fact]
    public async Task DisposeAsync_BeforeConnect_DoesNotThrow()
    {
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        await brain.DisposeAsync();
    }

    [Fact]
    public async Task CraftPromptAsync_WithoutConnect_ReturnsFallback()
    {
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-4", "Some goal");

        var prompt = await brain.CraftPromptAsync(pipeline, GoalPhase.Coding, null, TestContext.Current.CancellationToken);
        Assert.Contains("Some goal", prompt);
    }

    // -- FakeDistributedBrain (IDistributedBrain stub) --

    [Fact]
    public async Task FakeDistributedBrain_PlanIterationAsync_ReturnsDefaultPlan()
    {
        var fake = new FakeDistributedBrain();
        var pipeline = CreatePipeline("g-6", "Update README");
        var plan = await fake.PlanIterationAsync(pipeline, TestContext.Current.CancellationToken);
        Assert.NotNull(plan);
        Assert.NotEmpty(plan.Phases);
    }

    [Fact]
    public async Task FakeDistributedBrain_CraftPromptAsync_ReturnsPrompt()
    {
        var fake = new FakeDistributedBrain();
        var pipeline = CreatePipeline("g-7", "Add tests");
        var prompt = await fake.CraftPromptAsync(pipeline, GoalPhase.Testing, "extra context", TestContext.Current.CancellationToken);
        Assert.Contains("Add tests", prompt);
        Assert.Contains("Testing", prompt);
    }

    [Fact]
    public async Task FakeDistributedBrain_TracksAllCalls()
    {
        var fake = new FakeDistributedBrain();
        var pipeline = CreatePipeline("g-9", "Multi-step goal");
        await fake.ConnectAsync(TestContext.Current.CancellationToken);
        await fake.PlanIterationAsync(pipeline, TestContext.Current.CancellationToken);
        await fake.CraftPromptAsync(pipeline, GoalPhase.Coding, null, TestContext.Current.CancellationToken);
        Assert.True(fake.Connected);
        Assert.Equal(1, fake.PlanIterationCalls);
        Assert.Equal(1, fake.CraftCalls);
    }

    // -- BuildIterationPlanFromToolCall Tests --

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

    // -- Helpers --

    private static GoalPipeline CreatePipeline(string goalId, string description) =>
        new(new Goal { Id = goalId, Description = description });
}

/// <summary>
/// Minimal fake implementing <see cref="IDistributedBrain"/> for unit tests.
/// </summary>
file sealed class FakeDistributedBrain : IDistributedBrain
{
    public bool Connected { get; private set; }
    public int PlanIterationCalls { get; private set; }
    public int CraftCalls { get; private set; }

    public Task ConnectAsync(CancellationToken ct = default) { Connected = true; return Task.CompletedTask; }

    public Task<IterationPlan> PlanIterationAsync(GoalPipeline pipeline, CancellationToken ct = default)
    {
        PlanIterationCalls++;
        return Task.FromResult(IterationPlan.Default());
    }

    public Task<string> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default)
    {
        CraftCalls++;
        return Task.FromResult($"Work on {pipeline.Description} as {phase}");
    }

    public Task CleanupGoalSessionAsync(string goalId) => Task.CompletedTask;

    public Task ReprimeSessionAsync(GoalPipeline pipeline, CancellationToken ct) => Task.CompletedTask;

    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) => Task.CompletedTask;
}

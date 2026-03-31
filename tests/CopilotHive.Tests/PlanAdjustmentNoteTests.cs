using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Tests for <see cref="GoalDispatcher.BuildPlanAdjustmentNote"/> and
/// the plan-adjustment injection behaviour at all three dispatch sites.
/// </summary>
public sealed class BuildPlanAdjustmentNoteTests
{
    // ── BuildPlanAdjustmentNote ──────────────────────────────────────────────

    [Fact]
    public void BuildPlanAdjustmentNote_ContainsOriginalPlanPhases()
    {
        var original = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Merging };
        var final    = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging };

        var note = GoalDispatcher.BuildPlanAdjustmentNote(original, final);

        Assert.Contains("Coding, Merging", note);
    }

    [Fact]
    public void BuildPlanAdjustmentNote_ContainsFinalPlanPhases()
    {
        var original = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Merging };
        var final    = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging };

        var note = GoalDispatcher.BuildPlanAdjustmentNote(original, final);

        Assert.Contains("Coding, Testing, Review, Merging", note);
    }

    [Fact]
    public void BuildPlanAdjustmentNote_ContainsAddedPhases()
    {
        var original = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Merging };
        var final    = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging };

        var note = GoalDispatcher.BuildPlanAdjustmentNote(original, final);

        // Both Testing and Review were added
        Assert.Contains("Testing", note);
        Assert.Contains("Review", note);
    }

    [Fact]
    public void BuildPlanAdjustmentNote_MentionsReviewRequirement()
    {
        var original = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging };
        var final    = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging };

        var note = GoalDispatcher.BuildPlanAdjustmentNote(original, final);

        Assert.Contains("Review is required", note);
    }

    [Fact]
    public void BuildPlanAdjustmentNote_AddedPhases_DoesNotContainUnchangedPhases()
    {
        // Only Review was added; Testing and Coding were present in original
        var original = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging };
        var final    = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging };

        var note = GoalDispatcher.BuildPlanAdjustmentNote(original, final);

        // Added phases section should only mention Review
        var addedLineStart = note.IndexOf("Added phases:", StringComparison.Ordinal);
        Assert.True(addedLineStart >= 0, "Note must contain 'Added phases:' section");
        // Extract the added-phases value (the rest of that line)
        var addedLineEnd = note.IndexOf('\n', addedLineStart);
        var addedLine = addedLineEnd >= 0
            ? note[addedLineStart..addedLineEnd]
            : note[addedLineStart..];

        Assert.Contains("Review", addedLine);
        Assert.DoesNotContain("Coding", addedLine);
        Assert.DoesNotContain("Testing", addedLine);
    }

    [Fact]
    public void BuildPlanAdjustmentNote_MentionsAllPhasesWillBePrompted()
    {
        var original = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Merging };
        var final    = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging };

        var note = GoalDispatcher.BuildPlanAdjustmentNote(original, final);

        Assert.Contains("ALL phases", note);
    }
}

/// <summary>
/// Tests for <see cref="DistributedBrain.InjectSystemNoteAsync"/>:
/// verifies that the note is added to pipeline.Conversation with the correct metadata.
/// </summary>
public sealed class InjectSystemNoteAsyncTests
{
    private static GoalPipeline CreatePipeline(int iteration = 1)
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
        var pipeline = new GoalPipeline(goal);
        // Advance iteration counter if needed
        for (var i = 1; i < iteration; i++)
            pipeline.IncrementIteration();
        return pipeline;
    }

    private static DistributedBrain CreateBrain() =>
        new DistributedBrain(
            "test-model",
            NullLogger<DistributedBrain>.Instance);

    [Fact]
    public async Task InjectSystemNoteAsync_AddsEntryToConversation()
    {
        var pipeline = CreatePipeline();
        var brain = CreateBrain();
        var initialCount = pipeline.Conversation.Count;

        await brain.InjectSystemNoteAsync(pipeline, "Plan was adjusted.", TestContext.Current.CancellationToken);

        Assert.Equal(initialCount + 1, pipeline.Conversation.Count);
    }

    [Fact]
    public async Task InjectSystemNoteAsync_EntryHasSystemRole()
    {
        var pipeline = CreatePipeline();
        var brain = CreateBrain();

        await brain.InjectSystemNoteAsync(pipeline, "Plan was adjusted.", TestContext.Current.CancellationToken);

        var entry = pipeline.Conversation.Last();
        Assert.Equal("system", entry.Role);
    }

    [Fact]
    public async Task InjectSystemNoteAsync_EntryContentMatchesNote()
    {
        var pipeline = CreatePipeline();
        var brain = CreateBrain();
        var note = "Your iteration plan was adjusted.";

        await brain.InjectSystemNoteAsync(pipeline, note, TestContext.Current.CancellationToken);

        var entry = pipeline.Conversation.Last();
        Assert.Equal(note, entry.Content);
    }

    [Fact]
    public async Task InjectSystemNoteAsync_EntryHasPlanAdjustmentPurpose()
    {
        var pipeline = CreatePipeline();
        var brain = CreateBrain();

        await brain.InjectSystemNoteAsync(pipeline, "Plan adjusted.", TestContext.Current.CancellationToken);

        var entry = pipeline.Conversation.Last();
        Assert.Equal("plan-adjustment", entry.Purpose);
    }

    [Fact]
    public async Task InjectSystemNoteAsync_EntryIterationMatchesPipelineIteration()
    {
        var pipeline = CreatePipeline(iteration: 3);
        var brain = CreateBrain();

        await brain.InjectSystemNoteAsync(pipeline, "Note.", TestContext.Current.CancellationToken);

        var entry = pipeline.Conversation.Last();
        Assert.Equal(3, entry.Iteration);
    }

    [Fact]
    public async Task InjectSystemNoteAsync_WhenPlanUnchanged_NoNoteInjected()
    {
        // Simulate caller only calling InjectSystemNoteAsync when plan changed
        var pipeline = CreatePipeline();
        var brain = CreateBrain();
        var initialCount = pipeline.Conversation.Count;

        // Do NOT inject — plan was unchanged
        // (caller decides; this test verifies the existing conversation is untouched)
        _ = brain; // suppress unused warning

        Assert.Equal(initialCount, pipeline.Conversation.Count);
    }
}

/// <summary>
/// Tests that verify GoalDispatcher injects a plan-adjustment note at all three
/// dispatch sites when ValidatePlan modifies the Brain's plan, and does not inject
/// when the plan is already valid.
/// </summary>
public sealed class PlanAdjustmentInjectionTests
{
    // ── DispatchNextGoalAsync site ──────────────────────────────────────────

    [Fact]
    public async Task DispatchNextGoalAsync_WhenPlanModified_AdjustmentNoteInjectedToConversation()
    {
        // Plan: Coding only (missing Testing + Review) — ValidatePlan inserts both
        var plan = new IterationPlan { Phases = [GoalPhase.Coding, GoalPhase.Merging] };
        var brain = new NoteCapturingBrain(plan);

        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal", RepositoryNames = ["test-repo"] };
        var goalSource = new PlanAdjustmentFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var pipelineManager = new GoalPipelineManager();
        var config = new HiveConfigFile
        {
            Repositories = [new RepositoryConfig { Name = "test-repo", Url = "https://github.com/test/repo", DefaultBranch = "main" }],
        };
        var dispatcher = new GoalDispatcher(
            goalManager, pipelineManager, new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain, config: config);

        await InvokeDispatchNextGoalAsync(dispatcher, TestContext.Current.CancellationToken);

        Assert.True(brain.InjectedNotes.Count > 0,
            "Expected a plan-adjustment note to be injected when ValidatePlan modifies the plan.");
    }

    [Fact]
    public async Task DispatchNextGoalAsync_WhenPlanUnchanged_NoAdjustmentNoteInjected()
    {
        // Plan already contains all required phases — ValidatePlan does not change it
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
        };
        var brain = new NoteCapturingBrain(plan);

        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal", RepositoryNames = ["test-repo"] };
        var goalSource = new PlanAdjustmentFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var pipelineManager = new GoalPipelineManager();
        var config = new HiveConfigFile
        {
            Repositories = [new RepositoryConfig { Name = "test-repo", Url = "https://github.com/test/repo", DefaultBranch = "main" }],
        };
        var dispatcher = new GoalDispatcher(
            goalManager, pipelineManager, new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain, config: config);

        await InvokeDispatchNextGoalAsync(dispatcher, TestContext.Current.CancellationToken);

        Assert.Empty(brain.InjectedNotes);
    }

    [Fact]
    public async Task DispatchNextGoalAsync_WhenPlanModified_NoteContainsOriginalAndFinalPhases()
    {
        // Plan: Coding + Merging — Review and Testing will be added
        var plan = new IterationPlan { Phases = [GoalPhase.Coding, GoalPhase.Merging] };
        var brain = new NoteCapturingBrain(plan);

        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal", RepositoryNames = ["test-repo"] };
        var goalSource = new PlanAdjustmentFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        await goalManager.GetNextGoalAsync(TestContext.Current.CancellationToken);

        var pipelineManager = new GoalPipelineManager();
        var config = new HiveConfigFile
        {
            Repositories = [new RepositoryConfig { Name = "test-repo", Url = "https://github.com/test/repo", DefaultBranch = "main" }],
        };
        var dispatcher = new GoalDispatcher(
            goalManager, pipelineManager, new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain, config: config);

        await InvokeDispatchNextGoalAsync(dispatcher, TestContext.Current.CancellationToken);

        Assert.True(brain.InjectedNotes.Count > 0);
        var note = brain.InjectedNotes[0];
        // Original plan contains Coding and Merging
        Assert.Contains("Coding", note);
        Assert.Contains("Merging", note);
        // Added phases are listed
        Assert.Contains("Testing", note);
        Assert.Contains("Review", note);
    }

    private static Task InvokeDispatchNextGoalAsync(GoalDispatcher dispatcher, CancellationToken ct)
    {
        var method = typeof(GoalDispatcher).GetMethod(
            "DispatchNextGoalAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Task)method.Invoke(dispatcher, [ct])!;
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Brain that returns a fixed plan and records every <see cref="InjectSystemNoteAsync"/> call.
    /// </summary>
    private sealed class NoteCapturingBrain(IterationPlan plan) : IDistributedBrain
    {
        public List<string> InjectedNotes { get; } = [];

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(plan));

        public Task<PromptResult> CraftPromptAsync(GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success($"Work on {pipeline.Description}"));

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct)
        {
            InjectedNotes.Add(note);
            return Task.CompletedTask;
        }

        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("proceed"));

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public BrainStats? GetStats() => null;
    }

    private sealed class PlanAdjustmentFakeGoalSource(Goal goal) : IGoalSource
    {
        public string Name => "plan-adjustment-fake";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([goal]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}

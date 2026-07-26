using System.Reflection;
using CopilotHive.Actors;
using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.AI;

namespace CopilotHive.Tests;

/// <summary>
/// Tests for <see cref="IterationPlanValidator.BuildPlanAdjustmentNote"/> and
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

        var note = IterationPlanValidator.BuildPlanAdjustmentNote(original, final);

        Assert.Contains("Coding, Merging", note);
    }

    [Fact]
    public void BuildPlanAdjustmentNote_ContainsFinalPlanPhases()
    {
        var original = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Merging };
        var final    = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging };

        var note = IterationPlanValidator.BuildPlanAdjustmentNote(original, final);

        Assert.Contains("Coding, Testing, Review, Merging", note);
    }

    [Fact]
    public void BuildPlanAdjustmentNote_ContainsAddedPhases()
    {
        var original = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Merging };
        var final    = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging };

        var note = IterationPlanValidator.BuildPlanAdjustmentNote(original, final);

        // Both Testing and Review were added
        Assert.Contains("Testing", note);
        Assert.Contains("Review", note);
    }

    [Fact]
    public void BuildPlanAdjustmentNote_MentionsReviewRequirement()
    {
        var original = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging };
        var final    = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging };

        var note = IterationPlanValidator.BuildPlanAdjustmentNote(original, final);

        // The new per-change note describes Review insertion with the reason
        Assert.Contains("Review was inserted", note);
        Assert.Contains("required for code-change plans", note);
    }

    [Fact]
    public void BuildPlanAdjustmentNote_Adjustments_OnlyMentionsActuallyAddedPhases()
    {
        // Only Review was added; Testing and Coding were present in original
        var original = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging };
        var final    = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging };

        var note = IterationPlanValidator.BuildPlanAdjustmentNote(original, final);

        // Adjustments section should mention Review but NOT describe Coding or Testing being added
        var adjustmentsStart = note.IndexOf("Adjustments:", StringComparison.Ordinal);
        Assert.True(adjustmentsStart >= 0, "Note must contain 'Adjustments:' section");

        // Extract the adjustments section (from "Adjustments:" to "You will be asked")
        var youWillStart = note.IndexOf("You will be asked", StringComparison.Ordinal);
        var adjustmentsSection = youWillStart >= 0
            ? note[adjustmentsStart..youWillStart]
            : note[adjustmentsStart..];

        Assert.Contains("Review", adjustmentsSection);
        // Coding and Testing were NOT added — they should not appear in adjustment bullet lines
        Assert.DoesNotContain("Coding was inserted", adjustmentsSection);
        Assert.DoesNotContain("Testing was inserted", adjustmentsSection);
    }

    [Fact]
    public void BuildPlanAdjustmentNote_MentionsAllPhasesWillBePrompted()
    {
        var original = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Merging };
        var final    = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging };

        var note = IterationPlanValidator.BuildPlanAdjustmentNote(original, final);

        Assert.Contains("ALL phases", note);
    }

    [Fact]
    public void BuildPlanAdjustmentNote_CodingAsSafetyFallback_MentionsCodingRequired()
    {
        // Neither Coding nor DocWriting — Coding is inserted as safety fallback
        var original = new List<GoalPhase> { GoalPhase.Merging };
        var final    = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging };

        var note = IterationPlanValidator.BuildPlanAdjustmentNote(original, final);

        Assert.Contains("Coding was inserted", note);
    }

    [Fact]
    public void BuildPlanAdjustmentNote_TestingAddedToCodePlan_MentionsCodeChangePlans()
    {
        // Coding present but Testing missing
        var original = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Review, GoalPhase.Merging };
        var final    = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging };

        var note = IterationPlanValidator.BuildPlanAdjustmentNote(original, final);

        Assert.Contains("Testing was inserted", note);
        Assert.Contains("code-change plans", note);
    }

    [Fact]
    public void BuildPlanAdjustmentNote_MergingMoved_MentionsMergingMovedToEnd()
    {
        // Merging is misplaced in the middle
        var original = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Merging, GoalPhase.Testing, GoalPhase.Review };
        var final    = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging };

        var note = IterationPlanValidator.BuildPlanAdjustmentNote(original, final);

        Assert.Contains("Merging was moved", note);
    }

    [Fact]
    public void BuildPlanAdjustmentNote_ContainsAdjustmentsSection()
    {
        var original = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Merging };
        var final    = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging };

        var note = IterationPlanValidator.BuildPlanAdjustmentNote(original, final);

        Assert.Contains("Adjustments:", note);
    }

    [Fact]
    public void BuildPlanAdjustmentNote_TestingAddedToDocsPlan_MentionsAfterDocWriting()
    {
        // Docs-only plan with no Testing or Review — Testing inserted after DocWriting
        var original = new List<GoalPhase> { GoalPhase.DocWriting, GoalPhase.Merging };
        var final    = new List<GoalPhase> { GoalPhase.DocWriting, GoalPhase.Testing, GoalPhase.Merging };

        var note = IterationPlanValidator.BuildPlanAdjustmentNote(original, final);

        Assert.Contains("Testing was inserted after DocWriting", note);
        // Must NOT say "after Coding" for a docs-only plan
        Assert.DoesNotContain("after Coding", note);
    }

    [Fact]
    public void BuildPlanAdjustmentNote_MergingAbsent_MentionsMergingAppended()
    {
        // Merging is entirely missing from the original plan
        var original = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review };
        var final    = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging };

        var note = IterationPlanValidator.BuildPlanAdjustmentNote(original, final);

        Assert.Contains("Merging was appended", note);
        Assert.Contains("always required", note);
    }
}

/// <summary>
/// Tests for <see cref="DistributedBrain.InjectSystemNoteAsync"/>:
/// verifies that the note is added to pipeline.Conversation with the correct metadata
/// and injected into the Brain's live session.
/// </summary>
public sealed class InjectSystemNoteAsyncTests
{
    private static GoalPipeline CreatePipeline(int iteration = 1)
    {
        var goal = new Goal { Id = $"goal-{Guid.NewGuid():N}", Description = "Test goal" };
        var pipeline = new GoalPipeline(goal);
        // Advance iteration counter if needed
        for (var i = 1; i < iteration; i++)
            pipeline.IterationBudget.TryConsume();
        return pipeline;
    }

    /// <summary>
    /// Creates a fully wired <see cref="DistributedBrain"/> backed by fake chat clients and a fake
    /// BrainActor factory, connects it, and forks the goal session required by
    /// InjectSystemNoteAsync. The caller owns the returned brain and temp directory.
    /// </summary>
    private static async Task<(DistributedBrain Brain, string TempDir)> CreateBrainAsync(GoalPipeline pipeline)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-note-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var brain = new DistributedBrain(
            "test-model",
            NullLogger<DistributedBrain>.Instance,
            stateDir: tempDir,
            chatClient: new FakeChatClient());

        typeof(DistributedBrain).GetField("_actorFactory",
                BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(brain, (Func<string, BrainActor>)(stateDir =>
                new BrainActor("test-model", 100_000, stateDir, NullLogger.Instance,
                    chatClientFactory: _ => new FakeChatClient())));

        await brain.ConnectAsync(TestContext.Current.CancellationToken);
        await brain.ForkSessionForGoalAsync(pipeline.GoalId, TestContext.Current.CancellationToken);
        return (brain, tempDir);
    }

    [Fact]
    public async Task InjectSystemNoteAsync_AddsEntryToConversation()
    {
        var pipeline = CreatePipeline();
        var (brain, tempDir) = await CreateBrainAsync(pipeline);
        try
        {
            var initialCount = pipeline.Conversation.Count;

            await brain.InjectSystemNoteAsync(pipeline, "Plan was adjusted.", TestContext.Current.CancellationToken);

            Assert.Equal(initialCount + 1, pipeline.Conversation.Count);
        }
        finally
        {
            await brain.DisposeAsync();
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task InjectSystemNoteAsync_EntryHasSystemRole()
    {
        var pipeline = CreatePipeline();
        var (brain, tempDir) = await CreateBrainAsync(pipeline);
        try
        {
            await brain.InjectSystemNoteAsync(pipeline, "Plan was adjusted.", TestContext.Current.CancellationToken);

            var entry = pipeline.Conversation.Last();
            Assert.Equal("system", entry.Role);
        }
        finally
        {
            await brain.DisposeAsync();
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task InjectSystemNoteAsync_EntryContentMatchesNote()
    {
        var pipeline = CreatePipeline();
        var (brain, tempDir) = await CreateBrainAsync(pipeline);
        try
        {
            var note = "Your iteration plan was adjusted.";

            await brain.InjectSystemNoteAsync(pipeline, note, TestContext.Current.CancellationToken);

            var entry = pipeline.Conversation.Last();
            Assert.Equal(note, entry.Content);
        }
        finally
        {
            await brain.DisposeAsync();
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task InjectSystemNoteAsync_EntryHasPlanAdjustmentPurpose()
    {
        var pipeline = CreatePipeline();
        var (brain, tempDir) = await CreateBrainAsync(pipeline);
        try
        {
            await brain.InjectSystemNoteAsync(pipeline, "Plan adjusted.", TestContext.Current.CancellationToken);

            var entry = pipeline.Conversation.Last();
            Assert.Equal("plan-adjustment", entry.Purpose);
        }
        finally
        {
            await brain.DisposeAsync();
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task InjectSystemNoteAsync_EntryIterationMatchesPipelineIteration()
    {
        var pipeline = CreatePipeline(iteration: 3);
        var (brain, tempDir) = await CreateBrainAsync(pipeline);
        try
        {
            await brain.InjectSystemNoteAsync(pipeline, "Note.", TestContext.Current.CancellationToken);

            var entry = pipeline.Conversation.Last();
            Assert.Equal(3, entry.Iteration);
        }
        finally
        {
            await brain.DisposeAsync();
            TestHelpers.ForceDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task InjectSystemNoteAsync_WhenNotConnected_Throws()
    {
        // The EnsureConnected guard must reject notes before any conversation entry is recorded,
        // so an unconnected brain can never silently swallow a plan adjustment.
        var pipeline = CreatePipeline();
        var brain = new DistributedBrain("test-model", NullLogger<DistributedBrain>.Instance);
        await using (brain)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => brain.InjectSystemNoteAsync(pipeline, "Plan adjusted.", TestContext.Current.CancellationToken));
            Assert.Contains("not connected", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(pipeline.Conversation);
        }
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

    // ── HandleNewIterationAsync site ────────────────────────────────────────

    [Fact]
    public async Task HandleNewIterationAsync_WhenPlanModified_AdjustmentNoteInjected()
    {
        // Plan returned by brain is Coding+Merging — ValidatePlan inserts Testing+Review
        var plan = new IterationPlan { Phases = [GoalPhase.Coding, GoalPhase.Merging] };
        var brain = new NoteCapturingBrain(plan);

        var (dispatcher, pipeline) = CreateDispatcherWithPipeline(brain);

        // Set pipeline into a state where HandleNewIterationAsync can proceed:
        // it needs both TestRetryBudget.TryConsume() and IterationBudget.TryConsume() to return true.
        // With maxRetries=3 and maxIterations=10 (defaults), first calls return true.
        pipeline.StateMachine.StartIteration(
            [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging]);
        pipeline.AdvanceTo(GoalPhase.Testing);

        await InvokeHandleNewIterationAsync(dispatcher, pipeline, "FAIL",
            TestContext.Current.CancellationToken);

        Assert.True(brain.InjectedNotes.Count > 0,
            "Expected a plan-adjustment note when HandleNewIterationAsync re-plans with a modified plan.");
    }

    [Fact]
    public async Task HandleNewIterationAsync_WhenPlanUnchanged_NoAdjustmentNoteInjected()
    {
        // Plan already has all required phases — ValidatePlan does not modify it
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
        };
        var brain = new NoteCapturingBrain(plan);

        var (dispatcher, pipeline) = CreateDispatcherWithPipeline(brain);

        pipeline.StateMachine.StartIteration(
            [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging]);
        pipeline.AdvanceTo(GoalPhase.Testing);

        await InvokeHandleNewIterationAsync(dispatcher, pipeline, "FAIL",
            TestContext.Current.CancellationToken);

        Assert.Empty(brain.InjectedNotes);
    }

    // ── HandleMergeFailureAsync site ────────────────────────────────────────

    [Fact]
    public async Task HandleMergeFailureAsync_WhenPlanModified_AdjustmentNoteInjected()
    {
        // Plan returned by brain is Coding+Merging — ValidatePlan inserts Testing+Review
        var plan = new IterationPlan { Phases = [GoalPhase.Coding, GoalPhase.Merging] };
        var brain = new NoteCapturingBrain(plan);

        var (dispatcher, pipeline) = CreateDispatcherWithPipeline(brain);

        pipeline.StateMachine.StartIteration(
            [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging]);
        pipeline.AdvanceTo(GoalPhase.Merging);

        await InvokeHandleMergeFailureAsync(dispatcher, pipeline, "Merge conflict detected.",
            TestContext.Current.CancellationToken);

        Assert.True(brain.InjectedNotes.Count > 0,
            "Expected a plan-adjustment note when HandleMergeFailureAsync re-plans with a modified plan.");
    }

    [Fact]
    public async Task HandleMergeFailureAsync_WhenPlanUnchanged_NoAdjustmentNoteInjected()
    {
        // Plan already has all required phases — ValidatePlan does not modify it
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
        };
        var brain = new NoteCapturingBrain(plan);

        var (dispatcher, pipeline) = CreateDispatcherWithPipeline(brain);

        pipeline.StateMachine.StartIteration(
            [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging]);
        pipeline.AdvanceTo(GoalPhase.Merging);

        await InvokeHandleMergeFailureAsync(dispatcher, pipeline, "Merge conflict detected.",
            TestContext.Current.CancellationToken);

        Assert.Empty(brain.InjectedNotes);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static (GoalDispatcher dispatcher, GoalPipeline pipeline) CreateDispatcherWithPipeline(
        IDistributedBrain brain)
    {
        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Test goal",
            RepositoryNames = ["test-repo"],
        };
        var goalSource = new PlanAdjustmentFakeGoalSource(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(goalSource);
        goalManager.GetNextGoalAsync().GetAwaiter().GetResult();

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries: 3);

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);

        var config = new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig { Name = "test-repo", Url = "https://github.com/test/repo", DefaultBranch = "main" },
            ],
        };

        var dispatcher = new GoalDispatcher(
            goalManager, pipelineManager, new TaskQueue(),
            new GrpcWorkerGateway(new WorkerPool()),
            new TaskCompletionNotifier(),
            NullLogger<GoalDispatcher>.Instance,
            new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            brain, config: config);

        return (dispatcher, pipeline);
    }

    private static Task InvokeDispatchNextGoalAsync(GoalDispatcher dispatcher, CancellationToken ct)
    {
        var method = typeof(GoalDispatcher).GetMethod(
            "DispatchNextGoalAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(dispatcher, [ct])!;
    }

    private static Task InvokeHandleNewIterationAsync(
        GoalDispatcher dispatcher, GoalPipeline pipeline, string verdict, CancellationToken ct)
    {
        var method = typeof(GoalDispatcher).GetMethod(
            "HandleNewIterationAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(dispatcher, [pipeline, verdict, ct])!;
    }

    private static Task InvokeHandleMergeFailureAsync(
        GoalDispatcher dispatcher, GoalPipeline pipeline, string errorMessage, CancellationToken ct)
    {
        var method = typeof(GoalDispatcher).GetMethod(
            "HandleMergeFailureAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(dispatcher, [pipeline, errorMessage, ct])!;
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Brain that returns a fixed plan and records every <see cref="InjectSystemNoteAsync"/> call.
    /// </summary>
    private sealed class NoteCapturingBrain(IterationPlan plan) : IDistributedBrain
    {
        public List<string> InjectedNotes { get; } = [];

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateModelAsync(string model, int? maxContextTokens = null, CancellationToken ct = default) =>
            Task.CompletedTask;

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

        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public bool GoalSessionExists(string goalId) => false;

        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

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

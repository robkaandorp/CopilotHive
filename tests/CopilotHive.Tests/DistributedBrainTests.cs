using System.Reflection;

using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpCoder;

using WorkerRole = CopilotHive.Workers.WorkerRole;

namespace CopilotHive.Tests;

/// <summary>
/// Test helper to simulate the old RecordOutput/PhaseOutputs behavior using PhaseLog.
/// </summary>
internal static class PipelineTestHelpers
{
    private static readonly Dictionary<WorkerRole, GoalPhase> RoleToPhase = new()
    {
        [WorkerRole.Coder] = GoalPhase.Coding,
        [WorkerRole.Tester] = GoalPhase.Testing,
        [WorkerRole.Reviewer] = GoalPhase.Review,
        [WorkerRole.DocWriter] = GoalPhase.DocWriting,
        [WorkerRole.Improver] = GoalPhase.Improve,
    };

    /// <summary>
    /// Adds a PhaseResult to the pipeline's PhaseLog, simulating the old RecordOutput behavior.
    /// </summary>
    public static void RecordTestOutput(this GoalPipeline pipeline, WorkerRole role, int iteration, string output, int occurrence = 1)
    {
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = RoleToPhase[role],
            Iteration = iteration,
            Occurrence = occurrence,
            WorkerOutput = output,
            Result = PhaseOutcome.Pass,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
        });
    }

    /// <summary>
    /// Sets a PhaseOutputs-like entry in PhaseLog by adding a PhaseResult for the given role+iteration.
    /// </summary>
    public static void SetTestPhaseOutput(this GoalPipeline pipeline, WorkerRole role, int iteration, string output, int occurrence = 1)
    {
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = RoleToPhase[role],
            Iteration = iteration,
            Occurrence = occurrence,
            WorkerOutput = output,
            Result = PhaseOutcome.Pass,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
        });
    }
}

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
    public async Task CraftPromptAsync_WithoutConnect_Throws()
    {
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-4", "Some goal");

        // With per-goal contexts, methods require a connected Brain and no longer return a
        // pre-connect fallback — they must throw InvalidOperationException instead.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => brain.CraftPromptAsync(pipeline, GoalPhase.Coding, null, TestContext.Current.CancellationToken));
    }

    // -- FakeDistributedBrain (IDistributedBrain stub) --

    [Fact]
    public async Task FakeDistributedBrain_PlanIterationAsync_ReturnsDefaultPlan()
    {
        var fake = new FakeDistributedBrain();
        var pipeline = CreatePipeline("g-6", "Update README");
        var planResult = await fake.PlanIterationAsync(pipeline, null, TestContext.Current.CancellationToken);
        Assert.NotNull(planResult);
        Assert.False(planResult.IsEscalation);
        Assert.NotEmpty(planResult.Plan!.Phases);
    }

    [Fact]
    public async Task FakeDistributedBrain_CraftPromptAsync_ReturnsPrompt()
    {
        var fake = new FakeDistributedBrain();
        var pipeline = CreatePipeline("g-7", "Add tests");
        var promptResult = await fake.CraftPromptAsync(pipeline, GoalPhase.Testing, "extra context", TestContext.Current.CancellationToken);
        Assert.Contains("Add tests", promptResult.Prompt);
        Assert.Contains("Testing", promptResult.Prompt);
    }

    [Fact]
    public async Task FakeDistributedBrain_TracksAllCalls()
    {
        var fake = new FakeDistributedBrain();
        var pipeline = CreatePipeline("g-9", "Multi-step goal");
        await fake.ConnectAsync(TestContext.Current.CancellationToken);
        await fake.PlanIterationAsync(pipeline, null, TestContext.Current.CancellationToken);
        await fake.CraftPromptAsync(pipeline, GoalPhase.Coding, null, TestContext.Current.CancellationToken);
        Assert.True(fake.Connected);
        Assert.Equal(1, fake.PlanIterationCalls);
        Assert.Equal(1, fake.CraftCalls);
    }

    // -- BuildIterationPlanFromToolCall Tests --

    [Fact]
    public void BuildIterationPlanFromToolCall_ValidPhases_BuildsCorrectPlan()
    {
        var toolCall = new DistributedBrain.IterationPlanResult(
            Phases: ["coding", "testing", "review", "merging"],
            PhaseInstructions: """{"coding":"focus on tests","review":"check edge cases"}""",
            Reason: "Standard workflow",
            ModelTiers: null);
        var plan = BrainPlanParser.BuildIterationPlanFromToolCall(toolCall);
        Assert.Equal(4, plan.Phases.Count);
        Assert.Equal(GoalPhase.Coding, plan.Phases[0]);
        Assert.Equal(GoalPhase.Testing, plan.Phases[1]);
        Assert.Equal(GoalPhase.Review, plan.Phases[2]);
        Assert.Equal(GoalPhase.Merging, plan.Phases[3]);
        Assert.Equal("Standard workflow", plan.Reason);
        Assert.Equal("focus on tests", plan.PhaseInstructions["coding"]);
    }

    [Fact]
    public void BuildIterationPlanFromToolCall_EmptyPhases_ReturnsEmptyPlan()
    {
        var toolCall = new DistributedBrain.IterationPlanResult(
            Phases: [],
            PhaseInstructions: "{}",
            Reason: "nothing to do",
            ModelTiers: null);
        var plan = BrainPlanParser.BuildIterationPlanFromToolCall(toolCall);
        Assert.Empty(plan.Phases);
    }

    [Fact]
    public void BuildIterationPlanFromToolCall_UnknownPhaseName_SurfacedAsUnrecognized_NotSilentlyDropped()
    {
        var toolCall = new DistributedBrain.IterationPlanResult(
            Phases: ["coding", "GarbageName", "testing"],
            PhaseInstructions: "{}",
            Reason: "test",
            ModelTiers: null);

        var plan = BrainPlanParser.BuildIterationPlanFromToolCall(toolCall);

        // The typo is NOT silently dropped into a valid-looking [coding, testing] plan.
        Assert.Equal(["GarbageName"], plan.UnrecognizedPhases);
        Assert.Equal([GoalPhase.Coding, GoalPhase.Testing], plan.Phases);
    }

    [Theory]
    [InlineData("Planning")]
    [InlineData("Done")]
    [InlineData("Failed")]
    public void BuildIterationPlanFromToolCall_NonExecutableLifecyclePhase_SurfacedAsUnrecognized(string phaseName)
    {
        var toolCall = new DistributedBrain.IterationPlanResult(
            Phases: ["coding", phaseName, "merging"],
            PhaseInstructions: "{}",
            Reason: "test",
            ModelTiers: null);

        var plan = BrainPlanParser.BuildIterationPlanFromToolCall(toolCall);

        Assert.Equal([phaseName], plan.UnrecognizedPhases);
        Assert.Equal([GoalPhase.Coding, GoalPhase.Merging], plan.Phases);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("7")]
    public void BuildIterationPlanFromToolCall_NumericPhaseToken_IsUnrecognized(string numeric)
    {
        // Name-based membership, NOT Enum.TryParse — which would accept "1" as GoalPhase.Coding.
        var toolCall = new DistributedBrain.IterationPlanResult(
            Phases: ["coding", numeric],
            PhaseInstructions: "{}",
            Reason: "test",
            ModelTiers: null);

        var plan = BrainPlanParser.BuildIterationPlanFromToolCall(toolCall);

        Assert.Equal([numeric], plan.UnrecognizedPhases);
        Assert.Equal([GoalPhase.Coding], plan.Phases);
    }

    [Fact]
    public void BuildIterationPlanFromToolCall_AllSixValidPhases_ParseUnchanged()
    {
        var toolCall = new DistributedBrain.IterationPlanResult(
            Phases: ["coding", "docwriting", "testing", "review", "improve", "merging"],
            PhaseInstructions: "{}",
            Reason: "full",
            ModelTiers: null);

        var plan = BrainPlanParser.BuildIterationPlanFromToolCall(toolCall);

        Assert.Empty(plan.UnrecognizedPhases);
        Assert.Equal(
            [GoalPhase.Coding, GoalPhase.DocWriting, GoalPhase.Testing,
             GoalPhase.Review, GoalPhase.Improve, GoalPhase.Merging],
            plan.Phases);
    }

    [Fact]
    public void BuildIterationPlanFromToolCall_OccurrenceSuffixes_NormalizeButInstructionKeysStayIntact()
    {
        var toolCall = new DistributedBrain.IterationPlanResult(
            Phases: ["coding-1", "testing-1", "coding-2", "testing-2", "review", "merging"],
            PhaseInstructions: """{"coding-1":"first round","coding-2":"second round"}""",
            Reason: "multi-round",
            ModelTiers: null);

        var plan = BrainPlanParser.BuildIterationPlanFromToolCall(toolCall);

        Assert.Empty(plan.UnrecognizedPhases);
        Assert.Equal(
            [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Coding,
             GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
            plan.Phases);

        // Indexed instruction keys survive verbatim so GetPhaseInstruction can resolve them.
        Assert.Equal("first round", plan.PhaseInstructions["coding-1"]);
        Assert.Equal("second round", plan.PhaseInstructions["coding-2"]);
        Assert.Equal("first round", plan.GetPhaseInstruction(GoalPhase.Coding, 1));
        Assert.Equal("second round", plan.GetPhaseInstruction(GoalPhase.Coding, 2));
    }

    // -- FormatContextUsageMessage Tests --

    [Fact]
    public void FormatContextUsageMessage_ComputesCorrectPercentage()
    {
        var result = DistributedBrain.FormatContextUsageMessage(58000, 128000, "CraftPromptAsync");

        Assert.StartsWith("Brain context usage:", result);
        Assert.Contains("45.3%", result);
        Assert.Contains("58000/128000 tokens", result);
        Assert.EndsWith("after CraftPromptAsync", result);
    }

    [Fact]
    public void FormatContextUsageMessage_ZeroContextWindow_DoesNotThrow()
    {
        var result = DistributedBrain.FormatContextUsageMessage(1000, 0, "SomeMethod");

        Assert.Contains("Brain context usage:", result);
    }

    [Fact]
    public void FormatContextUsageMessage_ContainsCallerName()
    {
        var result = DistributedBrain.FormatContextUsageMessage(10000, 150000, "PlanIterationAsync");

        Assert.Contains("after PlanIterationAsync", result);
    }

    [Fact]
    public void FormatContextUsageMessage_ExactFormat_MatchesExpectedString()
    {
        var result = DistributedBrain.FormatContextUsageMessage(75000, 150000, "PlanIterationAsync");

        Assert.Equal("Brain context usage: 50.0% (75000/150000 tokens) after PlanIterationAsync", result);
    }

    // -- BuildPreviousIterationContext Tests --

    [Fact]
    public void BuildPreviousIterationContext_FirstIteration_ReturnsEmpty()
    {
        var pipeline = CreatePipeline("g-ctx-1", "First iteration goal");
        // Iteration defaults to 1
        var result = BrainPromptBuilder.BuildPreviousIterationContext(pipeline);
        Assert.Equal("", result);
    }

    [Fact]
    public void BuildPreviousIterationContext_SecondIteration_IncludesReviewerFeedback()
    {
        var pipeline = CreatePipeline("g-ctx-2", "Review rejected goal");
        pipeline.RecordTestOutput(WorkerRole.Reviewer, 1, "FAIL: Missing null check in UserService.GetById()");
        pipeline.IterationBudget.TryConsume(); // Now iteration 2

        var result = BrainPromptBuilder.BuildPreviousIterationContext(pipeline);

        Assert.Contains($"=== Previous iteration (1) feedback ===", result);
        Assert.Contains("=== Reviewer feedback (iteration 1) ===", result);
        Assert.Contains("Missing null check", result);
    }

    [Fact]
    public void BuildPreviousIterationContext_SecondIteration_IncludesTesterFeedback()
    {
        var pipeline = CreatePipeline("g-ctx-3", "Test failed goal");
        pipeline.RecordTestOutput(WorkerRole.Tester, 1, "3 tests failed: TestAuth, TestLogin, TestLogout");
        pipeline.IterationBudget.TryConsume();

        var result = BrainPromptBuilder.BuildPreviousIterationContext(pipeline);

        Assert.Contains("=== Tester feedback (iteration 1) ===", result);
        Assert.Contains("3 tests failed", result);
    }

    [Fact]
    public void BuildPreviousIterationContext_SecondIteration_IncludesCoderOutput()
    {
        var pipeline = CreatePipeline("g-ctx-4", "Coder context goal");
        pipeline.RecordTestOutput(WorkerRole.Coder, 1, "Added UserService with CRUD operations");
        pipeline.IterationBudget.TryConsume();

        var result = BrainPromptBuilder.BuildPreviousIterationContext(pipeline);

        Assert.Contains("=== Coder output round 1 (iteration 1) ===", result);
        Assert.Contains("Added UserService", result);
    }

    [Fact]
    public void BuildPreviousIterationContext_AllPhaseOutputs_IncludesAll()
    {
        var pipeline = CreatePipeline("g-ctx-5", "Full feedback goal");
        pipeline.RecordTestOutput(WorkerRole.Coder, 1, "Implemented feature X");
        pipeline.RecordTestOutput(WorkerRole.Tester, 1, "All 50 tests pass");
        pipeline.RecordTestOutput(WorkerRole.Reviewer, 1, "FAIL: Variable naming inconsistent");
        pipeline.IterationBudget.TryConsume();

        var result = BrainPromptBuilder.BuildPreviousIterationContext(pipeline);

        Assert.Contains("=== Reviewer feedback (iteration 1) ===", result);
        Assert.Contains("=== Tester feedback (iteration 1) ===", result);
        Assert.Contains("=== Coder output round 1 (iteration 1) ===", result);
    }

    [Fact]
    public void BuildPreviousIterationContext_NoOutputsRecorded_ShowsFallbackMessage()
    {
        var pipeline = CreatePipeline("g-ctx-6", "No outputs goal");
        pipeline.IterationBudget.TryConsume();

        var result = BrainPromptBuilder.BuildPreviousIterationContext(pipeline);

        Assert.Contains($"=== Previous iteration (1) feedback ===", result);
        Assert.Contains("No phase outputs recorded", result);
        Assert.Contains("=== End previous iteration feedback ===", result);
    }

    [Fact]
    public void BuildPreviousIterationContext_ThirdIteration_UsesIterationTwoOutputs()
    {
        var pipeline = CreatePipeline("g-ctx-7", "Multi-iteration goal");
        pipeline.RecordTestOutput(WorkerRole.Reviewer, 1, "FAIL: Iteration 1 issue");
        pipeline.IterationBudget.TryConsume(); // Now iteration 2
        pipeline.RecordTestOutput(WorkerRole.Reviewer, 2, "FAIL: Iteration 2 issue");
        pipeline.IterationBudget.TryConsume(); // Now iteration 3

        var result = BrainPromptBuilder.BuildPreviousIterationContext(pipeline);

        Assert.Contains($"=== Previous iteration (2) feedback ===", result);
        Assert.Contains("Iteration 2 issue", result);
        Assert.DoesNotContain("Iteration 1 issue", result);
    }

    [Fact]
    public void BuildPreviousIterationContext_LongOutput_TruncatesReviewer()
    {
        var pipeline = CreatePipeline("g-ctx-8", "Long output goal");
        var longOutput = new string('X', 5000);
        pipeline.RecordTestOutput(WorkerRole.Reviewer, 1, longOutput);
        pipeline.IterationBudget.TryConsume();

        var result = BrainPromptBuilder.BuildPreviousIterationContext(pipeline);

        // Reviewer uses TruncationConversationSummary (2000) so output should be truncated
        Assert.True(result.Length < 5000 + 200); // Some overhead for labels
        Assert.Contains("...", result);
    }

    // -- Helpers --

    private static GoalPipeline CreatePipeline(string goalId, string description) =>
        new(new Goal { Id = goalId, Description = description });

    // -- Single Session Tests --

    [Fact]
    public void Constructor_WithStateDir_CreatesInstance()
    {
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
            stateDir: "/tmp/test-state");
        Assert.NotNull(brain);
    }

    [Fact]
    public void Constructor_WithRepoManager_CreatesInstance()
    {
        var repoManager = new Git.BrainRepoManager("/tmp/test", NullLogger<Git.BrainRepoManager>.Instance);
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
            repoManager: repoManager);
        Assert.NotNull(brain);
    }

    [Fact]
    public async Task EnsureBrainRepoAsync_NoRepoManager_DoesNotThrow()
    {
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        await brain.EnsureBrainRepoAsync("myrepo", "https://example.com/repo.git", "main",
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PlanIterationAsync_WithoutConnect_ReturnsFailed()
    {
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-plan", "Test plan");

        // PlanIterationAsync must NEVER throw — every failure, including pre-connect misuse,
        // surfaces as PlanResult.Failed so the goal fails with an explicit reason instead of
        // silently receiving a default plan.
        var result = await brain.PlanIterationAsync(pipeline, null, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Null(result.Plan);
        Assert.StartsWith("Planning failed:", result.FailureReason);
    }

    [Fact]
    public async Task ResetSessionAsync_ReloadsOrchestratorInstructionsFromDisk()
    {
        var tempAgentsDir = Path.Combine(Path.GetTempPath(), $"agents-test-{Guid.NewGuid():N}");
        var tempStateDir = Path.Combine(Path.GetTempPath(), $"brain-reset-test-{Guid.NewGuid():N}");
        try
        {
            var agentsManager = new Agents.AgentsManager(tempAgentsDir);

            // Write initial orchestrator instructions
            var orchestratorFile = agentsManager.GetAgentsMdPath(WorkerRole.Orchestrator);
            File.WriteAllText(orchestratorFile, "INITIAL_INSTRUCTIONS_CONTENT");

            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                agentsManager: agentsManager, stateDir: tempStateDir);

            // Verify _systemPrompt contains the initial instructions after construction
            var systemPromptField = typeof(DistributedBrain)
                .GetField("_systemPrompt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var initialPrompt = (string)systemPromptField.GetValue(brain)!;
            Assert.Contains("INITIAL_INSTRUCTIONS_CONTENT", initialPrompt);

            // Update the orchestrator instructions on disk (simulating a change during the session)
            File.WriteAllText(orchestratorFile, "UPDATED_FRESH_INSTRUCTIONS_FROM_DISK");

            // ResetSessionAsync reloads the orchestrator instructions from disk and updates
            // _systemPrompt. With per-goal contexts there is no shared agent to recreate, so reset
            // completes without throwing — but _systemPrompt must reflect the fresh on-disk content.
            await brain.ResetSessionAsync(TestContext.Current.CancellationToken);

            // Verify _systemPrompt now contains the NEW content, not the original.
            // This test fails with the buggy implementation (stale _systemPrompt from construction)
            // and passes with the fix (which reloads from disk inside ResetSessionAsync).
            var updatedPrompt = (string)systemPromptField.GetValue(brain)!;
            Assert.Contains("UPDATED_FRESH_INSTRUCTIONS_FROM_DISK", updatedPrompt);
            Assert.DoesNotContain("INITIAL_INSTRUCTIONS_CONTENT", updatedPrompt);
        }
        finally
        {
            if (Directory.Exists(tempAgentsDir))
                Directory.Delete(tempAgentsDir, true);
            if (Directory.Exists(tempStateDir))
                Directory.Delete(tempStateDir, true);
        }
    }

    [Fact]
    public async Task ResetSessionAsync_WithoutAgentsManager_UsesDefaultSystemPrompt()
    {
        var tempStateDir = Path.Combine(Path.GetTempPath(), $"brain-reset-noagents-{Guid.NewGuid():N}");
        try
        {
            // No agentsManager provided. With per-goal contexts there is no shared agent to recreate,
            // so reset completes without throwing even when the Brain was never connected. Without an
            // agents manager, the reloaded system prompt falls back to the default.
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempStateDir);

            await brain.ResetSessionAsync(TestContext.Current.CancellationToken);

            var systemPromptField = typeof(DistributedBrain)
                .GetField("_systemPrompt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var prompt = (string)systemPromptField.GetValue(brain)!;
            Assert.False(string.IsNullOrWhiteSpace(prompt), "System prompt must fall back to a non-empty default");
        }
        finally
        {
            if (Directory.Exists(tempStateDir))
                Directory.Delete(tempStateDir, true);
        }
    }

    [Fact]
    public async Task InjectOrchestratorInstructionsAsync_UpdatesSystemPrompt_PreservesMessageHistory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-inject-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient());
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            var systemPromptField = typeof(DistributedBrain)
                .GetField("_systemPrompt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            var messageCountBefore = ActorMasterSession(brain).MessageHistory.Count;

            // Act: inject new orchestrator instructions
            await brain.InjectOrchestratorInstructionsAsync("NEW_ORCHESTRATOR_RULES", TestContext.Current.CancellationToken);

            // Assert: system prompt updated with new instructions
            var updatedPrompt = (string)systemPromptField.GetValue(brain)!;
            Assert.Contains("NEW_ORCHESTRATOR_RULES", updatedPrompt);
            Assert.Contains(BrainPromptBuilder.DefaultSystemPrompt, updatedPrompt);

            // Assert: the actor's master session history is preserved (no injected User/Assistant turns)
            var masterSession = ActorMasterSession(brain);
            Assert.Equal(messageCountBefore, masterSession.MessageHistory.Count);
            Assert.DoesNotContain(masterSession.MessageHistory, m =>
                m.Text.Contains("ORCHESTRATOR INSTRUCTIONS UPDATE", StringComparison.Ordinal));
            Assert.DoesNotContain(masterSession.MessageHistory, m =>
                m.Text.Contains("Acknowledged. I will follow the updated orchestrator instructions", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task InjectOrchestratorInstructionsAsync_EmptyInstructions_DoesNothing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-inject-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient());
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            var systemPromptField = typeof(DistributedBrain)
                .GetField("_systemPrompt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            var initialPrompt = (string)systemPromptField.GetValue(brain)!;
            var messageCountBefore = ActorMasterSession(brain).MessageHistory.Count;

            // Act: inject empty / whitespace instructions
            await brain.InjectOrchestratorInstructionsAsync("   ", TestContext.Current.CancellationToken);

            // Assert: nothing changed
            var updatedPrompt = (string)systemPromptField.GetValue(brain)!;
            Assert.Equal(initialPrompt, updatedPrompt);
            Assert.Equal(messageCountBefore, ActorMasterSession(brain).MessageHistory.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // -- Review Phase Guidance Static Verification --

    [Fact]
    public void ReviewPhaseGuidance_ContainsScopeClarificationGuidance()
    {
        // Verify that the source code contains the expected reviewer guidance strings.
        // This ensures the guidance is present in both Review phase branches (with and without docwriting).
        // Use environment variable or current directory to find the repo root
        var repoRoot = Environment.CurrentDirectory;
        // Navigate up until we find the solution file
        while (repoRoot != null && !Directory.GetFiles(repoRoot, "*.slnx").Any())
        {
            repoRoot = Directory.GetParent(repoRoot)?.FullName;
        }
        Assert.NotNull(repoRoot);
        
        // Read both DistributedBrain.cs (DefaultSystemPrompt) and BrainPromptBuilder.cs (BuildReviewFallbackPrompt, BuildCraftPromptText)
        var brainSourcePath = Path.Combine(repoRoot, "src", "CopilotHive", "Orchestration", "DistributedBrain.cs");
        Assert.True(File.Exists(brainSourcePath), $"Source file not found at {brainSourcePath}");

        var promptBuilderSourcePath = Path.Combine(repoRoot, "src", "CopilotHive", "Orchestration", "BrainPromptBuilder.cs");
        Assert.True(File.Exists(promptBuilderSourcePath), $"Source file not found at {promptBuilderSourcePath}");
        
        var source = File.ReadAllText(brainSourcePath) + File.ReadAllText(promptBuilderSourcePath);
        
        // Verify the guidance strings for "Files to change" are present
        Assert.Contains("\"Files to change\" in the goal is GUIDANCE", source);
        Assert.Contains("Test files and test changes that cover the modified code are ALWAYS acceptable and expected", source);
        
        // Verify the guidance for "Files NOT to change"
        Assert.Contains("\"Files NOT to change\" in the goal IS a strict prohibition", source);
        Assert.Contains("flag any changes to those files as MAJOR", source);
        
        // Verify goal description scope guidance
        Assert.Contains("The goal description defines WHAT to do. New behavior described in the goal is IN SCOPE", source);
        
        // Verify the focus guidance
        Assert.Contains("Only flag issues that are clearly bugs, security problems, or genuine scope violations", source);
        
        // Verify docwriting-specific guidance
        Assert.Contains("The docwriting phase already ran before this review", source);
        Assert.Contains("Changes to CHANGELOG.md, README.md, and XML doc comments are EXPECTED", source);
    }

    // -- BuildCraftPromptText / Review Phase Test Results Tests --

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_WithTesterOutput_ContainsTesterString()
    {
        // Arrange: pipeline with tester output recorded for iteration 1
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-rev-1", "Add null checks to UserService");
        pipeline.RecordTestOutput(WorkerRole.Tester, 1, "All 42 tests pass. No failures.");

        // Act: call the internal method directly to get the raw prompt text
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Review);

        // Assert: the tester output string appears verbatim in the prompt
        Assert.Contains("All 42 tests pass. No failures.", prompt);
        Assert.Contains("=== Tester output (iteration 1) ===", prompt);
        Assert.Contains("=== End tester output ===", prompt);
    }

    [Fact]
    public void BuildCraftPromptText_CodingPhase_TesterOutputPresent_OmitsTestResults()
    {
        // Arrange: even if tester output is present, a Coding phase prompt must not include it
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-coding-1", "Implement feature Y");
        pipeline.RecordTestOutput(WorkerRole.Tester, 1, "Some test output that should not appear");

        // Act
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Coding);

        // Assert: tester output is NOT in the prompt for Coding phase
        Assert.DoesNotContain("Some test output that should not appear", prompt);
        Assert.DoesNotContain("=== Tester output (iteration", prompt);
    }

    [Fact]
    public void BuildCraftPromptText_TestingPhase_TesterOutputPresent_OmitsTestResults()
    {
        // Arrange: tester output present but Testing phase should not include it
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-test-1", "Run integration tests");
        pipeline.RecordTestOutput(WorkerRole.Tester, 1, "Previous test output should not appear");

        // Act
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Testing);

        // Assert: tester output is NOT in the prompt for Testing phase
        Assert.DoesNotContain("Previous test output should not appear", prompt);
        Assert.DoesNotContain("=== Tester output (iteration", prompt);
    }

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_TesterOutputAppearsAfterAdditionalContext()
    {
        // Arrange: pipeline with tester output and additional context
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-rev-order", "Review ordering test");
        pipeline.RecordTestOutput(WorkerRole.Tester, 1, "UNIQUE_TESTER_MARKER_XYZ");

        // Act
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Review, "UNIQUE_CONTEXT_MARKER_ABC");

        // Assert: both markers are present
        Assert.Contains("UNIQUE_CONTEXT_MARKER_ABC", prompt);
        Assert.Contains("UNIQUE_TESTER_MARKER_XYZ", prompt);

        // Assert ordering: additionalContext appears BEFORE currentTestResults
        var contextIdx = prompt.IndexOf("=== Additional context ===", StringComparison.Ordinal);
        var contextEndIdx = prompt.IndexOf("=== End additional context ===", StringComparison.Ordinal);
        var testResultsIdx = prompt.IndexOf("=== Tester output (iteration", StringComparison.Ordinal);
        Assert.True(contextIdx >= 0, "Additional context header should be in prompt");
        Assert.True(contextEndIdx >= 0, "End additional context fence should be in prompt");
        Assert.True(contextIdx < contextEndIdx, "Opening fence should come before closing fence");
        Assert.True(contextEndIdx < testResultsIdx, "Closing fence should come before tester output");
    }

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_WithoutTesterOutput_OmitsTestResultsSection()
    {
        // Arrange: no tester output recorded
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-review-notest", "Review the implementation");

        // Act
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Review);

        // Assert: the tester output section header should NOT be present
        Assert.DoesNotContain("=== Tester output (iteration", prompt);
    }

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_WhitespaceOnlyTesterOutput_OmitsTestResultsSection()
    {
        // Arrange: tester output is only whitespace
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-review-ws", "Review with whitespace tester output");
        pipeline.RecordTestOutput(WorkerRole.Tester, 1, "   \n  \t  ");

        // Act
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Review);

        // Assert: whitespace-only output should be treated as absent
        Assert.DoesNotContain("=== Tester output (iteration", prompt);
    }

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_UsesCurrentIterationTesterOutput()
    {
        // Arrange: record tester output for two iterations, advance to iteration 2
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-review-iter", "Multi-iteration review");
        pipeline.RecordTestOutput(WorkerRole.Tester, 1, "ITER1_OUTPUT_SHOULD_NOT_APPEAR");
        pipeline.IterationBudget.TryConsume();
        pipeline.RecordTestOutput(WorkerRole.Tester, 2, "ITER2_OUTPUT_EXPECTED");

        // Act: at iteration 2, should use tester-2 key
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Review);

        // Assert: only iteration 2's output appears
        Assert.Contains("ITER2_OUTPUT_EXPECTED", prompt);
        Assert.DoesNotContain("ITER1_OUTPUT_SHOULD_NOT_APPEAR", prompt);
    }

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_ContainsReviewerInstructionText()
    {
        // With change D, reviewer-specific rules are now in DefaultSystemPrompt (the system prompt),
        // not in BuildCraftPromptText. Verify that:
        // 1. The system prompt (accessible via _systemPrompt field) contains the reviewer guidance.
        // 2. The craft prompt still includes the tester output section as expected.
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-rev-instr", "Review instruction test");
        pipeline.RecordTestOutput(WorkerRole.Tester, 1, "All tests pass.");

        // Verify the craft prompt includes tester output (key observable behavior)
        var craftPrompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Review);
        Assert.Contains("=== Tester output (iteration 1) ===", craftPrompt);
        Assert.Contains("All tests pass.", craftPrompt);

        // Verify the system prompt contains the reviewer guidance
        var systemPromptField = typeof(DistributedBrain)
            .GetField("_systemPrompt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var systemPrompt = (string)systemPromptField.GetValue(brain)!;
        Assert.Contains(
            "Use the testing phase results to verify that all tests pass",
            systemPrompt);
        Assert.Contains(
            "do NOT reject because you cannot run tests yourself",
            systemPrompt);
    }

    [Fact]
    public void ReviewPhaseGuidance_BothBranches_ContainExactInstruction()
    {
        // With change D, reviewer-specific rules are now in DefaultSystemPrompt (not in BuildCraftPromptText).
        // Verify that:
        // 1. The system prompt contains the reviewer guidance (at least once via DefaultSystemPrompt constant).
        // 2. BuildReviewFallbackPrompt still contains the guidance (for the no-brain fallback path).
        var repoRoot = Environment.CurrentDirectory;
        while (repoRoot != null && !Directory.GetFiles(repoRoot, "*.slnx").Any())
            repoRoot = Directory.GetParent(repoRoot)?.FullName;
        Assert.NotNull(repoRoot);

        var brainSourcePath = Path.Combine(repoRoot, "src", "CopilotHive", "Orchestration", "DistributedBrain.cs");
        Assert.True(File.Exists(brainSourcePath), $"Source file not found at {brainSourcePath}");

        var promptBuilderSourcePath = Path.Combine(repoRoot, "src", "CopilotHive", "Orchestration", "BrainPromptBuilder.cs");
        Assert.True(File.Exists(promptBuilderSourcePath), $"Source file not found at {promptBuilderSourcePath}");

        var source = File.ReadAllText(brainSourcePath) + File.ReadAllText(promptBuilderSourcePath);

        const string expectedInstruction =
            "Use the testing phase results to verify that all tests pass — do NOT reject because you cannot run tests yourself.";

        // Now the instruction appears in DefaultSystemPrompt (DistributedBrain.cs) + BuildReviewFallbackPrompt (BrainPromptBuilder.cs) (at least 2 locations)
        var occurrences = source.Split(expectedInstruction).Length - 1;
        Assert.True(occurrences >= 2,
            $"Expected the test-results instruction to appear in at least 2 locations (DefaultSystemPrompt + fallback), but found {occurrences}.");
    }

    // -- BuildReviewFallbackPrompt Tests --

    [Fact]
    public void BuildReviewFallbackPrompt_WithTesterOutput_ContainsTestResults()
    {
        var pipeline = CreatePipeline("g-fb-1", "Fix null reference in OrderService");
        pipeline.RecordTestOutput(WorkerRole.Tester, 1, "Passed: 87, Failed: 0");

        var prompt = BrainPromptBuilder.BuildReviewFallbackPrompt(pipeline);

        Assert.Contains("Passed: 87, Failed: 0", prompt);
        Assert.Contains("=== Tester output (iteration 1) ===", prompt);
        Assert.Contains("=== End tester output ===", prompt);
    }

    [Fact]
    public void BuildReviewFallbackPrompt_ContainsReviewerGuidance()
    {
        var pipeline = CreatePipeline("g-fb-2", "Update API controller");
        pipeline.RecordTestOutput(WorkerRole.Tester, 1, "All tests pass");

        var prompt = BrainPromptBuilder.BuildReviewFallbackPrompt(pipeline);

        Assert.Contains(
            "Use the testing phase results to verify that all tests pass",
            prompt);
        Assert.Contains(
            "do NOT reject because you cannot run tests yourself",
            prompt);
    }

    [Fact]
    public void BuildReviewFallbackPrompt_ContainsGoalDescription()
    {
        var pipeline = CreatePipeline("g-fb-3", "Refactor PaymentGateway module");

        var prompt = BrainPromptBuilder.BuildReviewFallbackPrompt(pipeline);

        Assert.Contains("Refactor PaymentGateway module", prompt);
    }

    [Fact]
    public void BuildReviewFallbackPrompt_WithAdditionalContext_ContainsContext()
    {
        var pipeline = CreatePipeline("g-fb-4", "Add logging");

        var prompt = BrainPromptBuilder.BuildReviewFallbackPrompt(pipeline, "EXTRA_CONTEXT_MARKER");

        Assert.Contains("=== Additional context ===", prompt);
        Assert.Contains("EXTRA_CONTEXT_MARKER", prompt);
        Assert.Contains("=== End additional context ===", prompt);
    }

    [Fact]
    public void BuildReviewFallbackPrompt_NoTesterOutput_OmitsTestResultsSection()
    {
        var pipeline = CreatePipeline("g-fb-5", "Remove deprecated endpoints");

        var prompt = BrainPromptBuilder.BuildReviewFallbackPrompt(pipeline);

        Assert.DoesNotContain("=== Tester output (iteration", prompt);
    }

    [Fact]
    public void BuildReviewFallbackPrompt_WhitespaceOnlyTesterOutput_OmitsTestResultsSection()
    {
        var pipeline = CreatePipeline("g-fb-6", "Clean up imports");
        pipeline.RecordTestOutput(WorkerRole.Tester, 1, "  \n\t  ");

        var prompt = BrainPromptBuilder.BuildReviewFallbackPrompt(pipeline);

        Assert.DoesNotContain("=== Tester output (iteration", prompt);
    }

    [Fact]
    public void BuildReviewFallbackPrompt_UsesCurrentIterationTesterOutput()
    {
        var pipeline = CreatePipeline("g-fb-7", "Multi-iteration fallback review");
        pipeline.RecordTestOutput(WorkerRole.Tester, 1, "ITER1_FALLBACK_SHOULD_NOT_APPEAR");
        pipeline.IterationBudget.TryConsume();
        pipeline.RecordTestOutput(WorkerRole.Tester, 2, "ITER2_FALLBACK_EXPECTED");

        var prompt = BrainPromptBuilder.BuildReviewFallbackPrompt(pipeline);

        Assert.Contains("ITER2_FALLBACK_EXPECTED", prompt);
        Assert.DoesNotContain("ITER1_FALLBACK_SHOULD_NOT_APPEAR", prompt);
    }

    [Fact]
    public async Task CraftPromptAsync_NotConnected_ReviewPhase_Throws()
    {
        // The inline null-agent fallback was removed; the review fallback now lives in
        // ClarificationHandler (covered by BuildReviewFallbackPrompt tests). CraftPromptAsync
        // now requires a connected Brain and throws when called before Connect.
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-fb-craft-1", "Fix authentication bug");
        pipeline.RecordTestOutput(WorkerRole.Tester, 1, "FALLBACK_TESTER_RESULTS_42");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => brain.CraftPromptAsync(pipeline, GoalPhase.Review, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CraftPromptAsync_NotConnected_CodingPhase_Throws()
    {
        // Non-review phases also require a connected Brain now; the generic inline fallback
        // was removed in favour of EnsureConnected.
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-fb-craft-2", "Implement caching layer");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => brain.CraftPromptAsync(pipeline, GoalPhase.Coding, null, TestContext.Current.CancellationToken));
    }

    // ── AskQuestionAsync — escalate_to_composer tool call ─────────────────

    /// <summary>
    /// Verifies the production path: when the AI client returns a response that triggers
    /// the <c>escalate_to_composer</c> tool call, <see cref="DistributedBrain.AskQuestionAsync"/>
    /// must return <see cref="BrainResponse.Escalated"/> with the correct question and reason.
    /// </summary>
    [Fact]
    public async Task AskQuestionAsync_EscalateToComposerToolCall_ReturnsBrainResponseEscalated()
    {
        // Arrange: create a DistributedBrain with a fake IChatClient that
        // returns a tool call for escalate_to_composer on the first request,
        // then a plain text response on the second (after the tool result is injected).
        var tmpDir = Path.Combine(Path.GetTempPath(), $"brain-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            const string ExpectedQuestion = "What is the retry limit?";
            const string ExpectedReason = "Requires domain knowledge outside the codebase";

            // Inject a fake IChatClient that drives the tool-call loop:
            // Call 1: returns escalate_to_composer tool call
            // Call 2: returns plain text (after tool result is processed by CodingAgent)
            var stubClient = new EscalateToolCallStubClient(
                callId: "call-escalate-1",
                toolName: "escalate_to_composer",
                toolArguments: new Dictionary<string, object?> { ["question"] = ExpectedQuestion, ["reason"] = ExpectedReason },
                finalReply: "Escalation recorded.");

            var brain = new DistributedBrain(
                "test-model",
                NullLogger<DistributedBrain>.Instance,
                stateDir: tmpDir,
                chatClient: stubClient);

            await brain.ConnectAsync(TestContext.Current.CancellationToken);
            await brain.ForkSessionForGoalAsync("goal-test-42", TestContext.Current.CancellationToken);

            // Act
            var response = await brain.AskQuestionAsync(
                "goal-test-42",
                iteration: 1,
                phase: "Coding",
                workerRole: "coder",
                question: ExpectedQuestion,
                ct: TestContext.Current.CancellationToken);

            // Assert: the discriminated union must be an escalation response
            Assert.True(response.IsEscalation,
                $"Expected IsEscalation=true but got Answer: '{response.Text}'");
            Assert.Equal(ExpectedQuestion, response.EscalationQuestion);
            Assert.Equal(ExpectedReason, response.EscalationReason);
            Assert.Null(response.Text);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // -- get_goal Tool Tests --

    [Fact]
    public void Constructor_WithGoalStore_CreatesInstance()
    {
        // Arrange & Act
        var goalStore = new FakeGoalStore();
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
            goalStore: goalStore);

        // Assert
        Assert.NotNull(brain);
    }

    [Fact]
    public void DeregisterActivePipeline_NonExistentGoalId_DoesNotThrow()
    {
        // Arrange
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);

        // Act & Assert - should not throw
        var exception = Record.Exception(() => brain.DeregisterActivePipeline("non-existent-goal"));
        Assert.Null(exception);
    }

    // -- get_goal Related Docs Tests --

    // -- search_knowledge Tool Tests --

    // -- read_document Tool Tests --

    // -- traverse_graph Tool Tests --

    // -- BuildCraftPromptText Goal ID Reference Tests (Change C) --

    [Fact]
    public void BuildCraftPromptText_ContainsGoalIdReference_NotFullDescription()
    {
        // Verify Change C: prompts reference goal ID, not full description
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("goal-12345", "This is a very long goal description that should not appear in the craft prompt header");

        // Act
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Coding);

        // Assert: Goal ID appears in the header
        Assert.Contains("Goal: goal-12345", prompt);
        // Assert: iteration and phase appear in the header
        Assert.Contains("iteration 1", prompt);
        Assert.Contains("phase Coding", prompt);
    }

    [Fact]
    public void BuildCraftPromptText_FullDescriptionNotInPrompt_WhenNotUsingGetGoalTool()
    {
        // Verify Change C: the full goal description should NOT appear directly in the prompt
        // The Brain should use get_goal tool to retrieve the description instead
        var longDescription = "This is a comprehensive goal description with many details about implementing user authentication, password hashing, session management, and token refresh logic that should NOT appear in the craft prompt header";
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("goal-token-optimization", longDescription);

        // Act
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Coding);

        // Assert: The full description should NOT appear in the prompt header
        // Only the goal ID should be present
        Assert.DoesNotContain(longDescription, prompt);
        Assert.Contains("Goal: goal-token-optimization", prompt);
    }

    [Fact]
    public void BuildCraftPromptText_IncludesIterationAndPhase()
    {
        // Verify the prompt includes iteration and phase information
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("goal-iter-phase", "Test iteration/phase display");

        // Act
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Testing);

        // Assert: Goal header format is "Goal: {id} (iteration {n}, phase {phase})"
        Assert.Contains("Goal: goal-iter-phase", prompt);
        Assert.Contains("iteration 1", prompt);
        Assert.Contains("phase Testing", prompt);
    }

    [Fact]
    public void BuildCraftPromptText_PromptsUseGetGoalToolForFullDescription()
    {
        // Verify that the prompt tells the Brain to use get_goal tool for full description
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("goal-getgoal", "Test get_goal tool reference");

        // Act
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Coding);

        // Assert: prompt instructs to use get_goal tool
        Assert.Contains("get_goal", prompt);
    }

    // -- No Duplicate Phase Instructions Tests (Change D) --

    [Fact]
    public void BuildCraftPromptText_DoesNotContainDuplicatePhaseInstructions()
    {
        // Verify Change D: role-specific instructions are not duplicated in craft prompt
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("goal-no-dupe", "Test for duplicate instructions");

        // Act
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Coding);

        // Assert: the craft prompt should NOT contain the full role instructions
        // (those are now in DefaultSystemPrompt, not in BuildCraftPromptText)
        Assert.DoesNotContain("For coders: Tell them to start implementing", prompt);
        Assert.DoesNotContain("For testers: tell them to build", prompt);
        Assert.DoesNotContain("For reviewers: Do NOT include any git diff", prompt);
        Assert.DoesNotContain("For docwriters: Do NOT include any git diff", prompt);
        Assert.DoesNotContain("For improvers: tell them to analyze", prompt);
    }

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_ContainsOnlyDocWritingNote_WhenApplicable()
    {
        // Review phase should include docwriting note only when docwriting preceded review
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("goal-review-note", "Test review note");

        // Test without docwriting phase (no note should appear)
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Review);
        Assert.DoesNotContain("The docwriting phase already ran before this review", prompt);
    }

    // -- System Prompt Contains Role Instructions Tests --

    [Fact]
    public void DefaultSystemPrompt_ContainsAllRoleInstructions()
    {
        // Verify that DefaultSystemPrompt contains all role-specific instructions
        var systemPromptField = typeof(DistributedBrain)
            .GetField("_systemPrompt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // Create a brain to get the _systemPrompt value
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var systemPrompt = (string)systemPromptField.GetValue(brain)!;

        // Assert all role instructions are present in system prompt
        Assert.Contains("Coders: Tell them to implement immediately", systemPrompt);
        Assert.Contains("Testers: Tell them to build, run test skill", systemPrompt);
        Assert.Contains("Reviewers: Do NOT include git diff commands", systemPrompt);
        Assert.Contains("DocWriters: Do NOT include git diff commands", systemPrompt);
        Assert.Contains("Improvers: Tell them to analyze results", systemPrompt);
        Assert.Contains("Use the testing phase results to verify that all tests pass", systemPrompt);
        Assert.Contains("progress-{goal-id}", systemPrompt);
        Assert.Contains("raise_issue", systemPrompt);
    }

    // -- Target Repositories in Prompt Tests --

    [Fact]
    public void BuildCraftPromptText_ContainsTargetRepositories()
    {
        // Verify that the prompt includes target repositories
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var goal = new Goal
        {
            Id = "goal-repos",
            Description = "Test repositories",
            RepositoryNames = ["repo-alpha", "repo-beta"]
        };
        var pipeline = new GoalPipeline(goal);

        // Act
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Coding);

        // Assert
        Assert.Contains("Target repositories:", prompt);
        Assert.Contains("repo-alpha", prompt);
        Assert.Contains("repo-beta", prompt);
    }

    // -- Tester Output Truncation Tests (Change E) --

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_TruncatesTesterOutputTo2000Chars()
    {
        // Arrange: create a pipeline with a very long tester output
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("goal-truncate", "Test tester output truncation");
        const int largeTesterOutputLength = 5000;
        var largeTesterOutput = new string('X', largeTesterOutputLength);
        pipeline.SetTestPhaseOutput(WorkerRole.Tester, pipeline.Iteration, largeTesterOutput);

        // Act: craft a Review-phase prompt
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Review);

        // Assert: the full tester output does NOT appear in the prompt
        Assert.DoesNotContain(largeTesterOutput, prompt);

        // Assert: the prompt contains an ellipsis truncation marker (truncated portion)
        Assert.Contains("...", prompt);

        // Assert: the truncated tester output (first 2000 chars) appears in the prompt
        var first2000Chars = largeTesterOutput[..2000];
        Assert.Contains(first2000Chars, prompt);
    }

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_DoesNotTruncate_WhenTesterOutputShort()
    {
        // Arrange: short tester output that should not be truncated
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("goal-short-output", "Test short tester output");
        var shortOutput = "All 42 tests passed. Build succeeded.";
        pipeline.SetTestPhaseOutput(WorkerRole.Tester, pipeline.Iteration, shortOutput);

        // Act
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Review);

        // Assert: full short output appears verbatim in the prompt (no truncation)
        Assert.Contains(shortOutput, prompt);
    }

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_WithExactly2000CharTesterOutput_NotTruncated()
    {
        // Arrange: exactly 2000 chars — should NOT be truncated
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("goal-exact-2000", "Test exact 2000 char boundary");
        var exactly2000 = new string('Y', 2000);
        pipeline.SetTestPhaseOutput(WorkerRole.Tester, pipeline.Iteration, exactly2000);

        // Act
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Review);

        // Assert: exactly 2000 chars should appear as-is (no truncation)
        Assert.Contains(exactly2000, prompt);
    }

    // -- Coder Output Tests --

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_WithCoderOutput_ContainsFencedCoderBlock()
    {
        // Arrange: pipeline with coder output recorded for iteration 1
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-coder-1", "Implement user service");
        pipeline.RecordTestOutput(WorkerRole.Coder, 1, "Added UserService.cs with GetById, Create, Update methods.");

        // Act
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Review);

        // Assert: coder output appears with fenced block format
        Assert.Contains("Added UserService.cs with GetById, Create, Update methods.", prompt);
        Assert.Contains("=== Coder output (iteration 1) ===", prompt);
        Assert.Contains("=== End coder output ===", prompt);
    }

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_WithoutCoderOutput_OmitsCoderBlock()
    {
        // Arrange: no coder output recorded
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-no-coder", "Review without coder output");

        // Act
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Review);

        // Assert: coder block should NOT be present
        Assert.DoesNotContain("=== Coder output (iteration", prompt);
        Assert.DoesNotContain("=== End coder output ===", prompt);
    }

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_WhitespaceOnlyCoderOutput_OmitsCoderBlock()
    {
        // Arrange: coder output is only whitespace
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-ws-coder", "Review with whitespace coder output");
        pipeline.RecordTestOutput(WorkerRole.Coder, 1, "   \n  \t  ");

        // Act
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Review);

        // Assert: whitespace-only output should be treated as absent
        Assert.DoesNotContain("=== Coder output (iteration", prompt);
    }

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_CoderOutputTruncatedAt2000Chars()
    {
        // Arrange: create a pipeline with a very long coder output
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-coder-truncate", "Test coder output truncation");
        const int largeCoderOutputLength = 5000;
        var largeCoderOutput = new string('Z', largeCoderOutputLength);
        pipeline.SetTestPhaseOutput(WorkerRole.Coder, pipeline.Iteration, largeCoderOutput);

        // Act
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Review);

        // Assert: the full coder output does NOT appear in the prompt
        Assert.DoesNotContain(largeCoderOutput, prompt);

        // Assert: the prompt contains an ellipsis truncation marker
        Assert.Contains("...", prompt);

        // Assert: the truncated coder output (first 2000 chars) appears in the prompt
        var first2000Chars = largeCoderOutput[..2000];
        Assert.Contains(first2000Chars, prompt);
    }

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_CoderOutputNotTruncated_WhenShort()
    {
        // Arrange: short coder output that should not be truncated
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-short-coder", "Test short coder output");
        var shortOutput = "Implemented feature X as requested.";
        pipeline.SetTestPhaseOutput(WorkerRole.Coder, pipeline.Iteration, shortOutput);

        // Act
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Review);

        // Assert: full short output appears verbatim in the prompt (no truncation)
        Assert.Contains(shortOutput, prompt);
    }

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_CoderOutputUsesCurrentIteration()
    {
        // Arrange: record coder output for two iterations, advance to iteration 2
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-coder-iter", "Multi-iteration coder review");
        pipeline.RecordTestOutput(WorkerRole.Coder, 1, "CODER_ITER1_SHOULD_NOT_APPEAR");
        pipeline.IterationBudget.TryConsume();
        pipeline.RecordTestOutput(WorkerRole.Coder, 2, "CODER_ITER2_EXPECTED");

        // Act: at iteration 2, should use coder-2 key
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Review);

        // Assert: only iteration 2's output appears
        Assert.Contains("CODER_ITER2_EXPECTED", prompt);
        Assert.DoesNotContain("CODER_ITER1_SHOULD_NOT_APPEAR", prompt);
    }

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_CoderOutputAppearsAfterTesterOutput()
    {
        // Arrange: pipeline with both tester and coder output
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-both-outputs", "Test both outputs ordering");
        pipeline.RecordTestOutput(WorkerRole.Tester, 1, "TESTER_MARKER_UNIQUE");
        pipeline.RecordTestOutput(WorkerRole.Coder, 1, "CODER_MARKER_UNIQUE");

        // Act
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Review);

        // Assert: both outputs appear
        Assert.Contains("TESTER_MARKER_UNIQUE", prompt);
        Assert.Contains("CODER_MARKER_UNIQUE", prompt);

        // Assert ordering: tester block appears BEFORE coder block
        var testerIdx = prompt.IndexOf("=== Tester output (iteration", StringComparison.Ordinal);
        var coderIdx = prompt.IndexOf("=== Coder output (iteration", StringComparison.Ordinal);
        Assert.True(testerIdx >= 0, "Tester output header should be in prompt");
        Assert.True(coderIdx >= 0, "Coder output header should be in prompt");
        Assert.True(testerIdx < coderIdx,
            $"Tester output (at {testerIdx}) should appear before coder output (at {coderIdx})");
    }

    [Fact]
    public void BuildCraftPromptText_CodingPhase_CoderOutputNotIncluded()
    {
        // Arrange: coder output present but Coding phase should not include it
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-coding-coder", "Coding phase with coder output");
        pipeline.RecordTestOutput(WorkerRole.Coder, 1, "CODER_OUTPUT_SHOULD_NOT_APPEAR");

        // Act
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, GoalPhase.Coding);

        // Assert: coder output is NOT in the prompt for Coding phase
        Assert.DoesNotContain("CODER_OUTPUT_SHOULD_NOT_APPEAR", prompt);
        Assert.DoesNotContain("=== Coder output (iteration", prompt);
    }

    [Fact]
    public void BuildReviewFallbackPrompt_WithCoderOutput_ContainsFencedCoderBlock()
    {
        // Arrange
        var pipeline = CreatePipeline("g-fb-coder-1", "Fallback review with coder output");
        pipeline.RecordTestOutput(WorkerRole.Coder, 1, "Added feature with async support.");

        // Act
        var prompt = BrainPromptBuilder.BuildReviewFallbackPrompt(pipeline);

        // Assert
        Assert.Contains("Added feature with async support.", prompt);
        Assert.Contains("=== Coder output (iteration 1) ===", prompt);
        Assert.Contains("=== End coder output ===", prompt);
    }

    [Fact]
    public void BuildReviewFallbackPrompt_WithoutCoderOutput_OmitsCoderBlock()
    {
        // Arrange
        var pipeline = CreatePipeline("g-fb-no-coder", "Fallback review without coder output");

        // Act
        var prompt = BrainPromptBuilder.BuildReviewFallbackPrompt(pipeline);

        // Assert
        Assert.DoesNotContain("=== Coder output (iteration", prompt);
        Assert.DoesNotContain("=== End coder output ===", prompt);
    }

    [Fact]
    public void BuildReviewFallbackPrompt_WhitespaceOnlyCoderOutput_OmitsCoderBlock()
    {
        // Arrange: coder output is only whitespace
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-fb-ws-coder", "Fallback review with whitespace coder output");
        pipeline.RecordTestOutput(WorkerRole.Coder, 1, "   \n  \t  ");

        // Act
        var prompt = BrainPromptBuilder.BuildReviewFallbackPrompt(pipeline);

        // Assert: whitespace-only output should be treated as absent
        Assert.DoesNotContain("=== Coder output (iteration", prompt);
    }

    [Fact]
    public void BuildReviewFallbackPrompt_CoderOutputTruncatedAt2000Chars()
    {
        // Arrange
        var pipeline = CreatePipeline("g-fb-coder-trunc", "Fallback coder truncation test");
        const int largeCoderOutputLength = 5000;
        var largeCoderOutput = new string('W', largeCoderOutputLength);
        pipeline.SetTestPhaseOutput(WorkerRole.Coder, pipeline.Iteration, largeCoderOutput);

        // Act
        var prompt = BrainPromptBuilder.BuildReviewFallbackPrompt(pipeline);

        // Assert: the full coder output does NOT appear
        Assert.DoesNotContain(largeCoderOutput, prompt);

        // Assert: truncated coder output appears with ellipsis
        var first2000Chars = largeCoderOutput[..2000];
        Assert.Contains(first2000Chars, prompt);
        Assert.Contains("...", prompt);
    }

    // -- ForkSessionForGoalAsync Tests --

    [Fact]
    public async Task ForkSessionForGoalAsync_CreatesSessionFileOnDisk()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient());

            // Need to connect to initialize the agent
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            // Act
            await brain.ForkSessionForGoalAsync("goal-123", TestContext.Current.CancellationToken);

            // Assert: session file exists
            var sessionFile = Path.Combine(tempDir, "actors", "brain-goal-goal-123.json");
            Assert.True(File.Exists(sessionFile), $"Session file should exist at {sessionFile}");

            // Assert: file contains valid JSON
            var content = await File.ReadAllTextAsync(sessionFile, TestContext.Current.CancellationToken);
            Assert.Contains("brain", content); // AgentSession JSON should contain agent name
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteGoalSession_RemovesFile()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient());
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            // Fork a goal session
            await brain.ForkSessionForGoalAsync("goal-delete-test", TestContext.Current.CancellationToken);

            var sessionFile = Path.Combine(tempDir, "actors", "brain-goal-goal-delete-test.json");

            // Assert: file exists before deletion
            Assert.True(File.Exists(sessionFile), "Session file should exist before deletion");

            // Act
            await brain.DeleteGoalSessionAsync("goal-delete-test", TestContext.Current.CancellationToken);

            // Assert: file no longer exists
            Assert.False(File.Exists(sessionFile), "Session file should not exist after deletion");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // -- Concurrency Tests --

    // -- SummarizeAndMergeAsync Tests --

    [Fact]
    public async Task SummarizeAndMergeAsync_AppendsSummaryToMasterSessionAndDeletesGoalSession()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Use a stub that returns a predictable summary
            var stubClient = new IterationPlanStubClient(
                callId: "call-summary-1",
                phases: ["coding", "testing"],
                reason: "Test summary");

            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: stubClient);

            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            var initialMasterMessageCount = ActorMasterSession(brain).MessageHistory.Count;

            // Create a goal session via ForkSessionForGoalAsync
            var goalId = "goal-summary-test";
            await brain.ForkSessionForGoalAsync(goalId, TestContext.Current.CancellationToken);

            // Verify goal session file exists
            var goalSessionFile = Path.Combine(tempDir, "actors", $"brain-goal-{goalId}.json");
            Assert.True(File.Exists(goalSessionFile), "Goal session file should exist after fork");

            var pipeline = CreatePipeline(goalId, "Test goal for summarization");

            // Act
            var summary = await brain.SummarizeAndMergeAsync(pipeline, TestContext.Current.CancellationToken);

            // Assert: summary was returned
            Assert.NotNull(summary);

            // Assert: the actor's master session has 2 new messages (user + assistant)
            var masterSession = ActorMasterSession(brain);
            Assert.Equal(initialMasterMessageCount + 2, masterSession.MessageHistory.Count);
            Assert.Contains(masterSession.MessageHistory, m =>
                m.Contents.Any(c => c is TextContent tc && tc.Text.Contains($"[Goal completed: {goalId}]")));
            Assert.Contains(masterSession.MessageHistory, m =>
                m.Role == ChatRole.Assistant && m.Contents.Any(c => c is TextContent tc && tc.Text.Contains(summary)));

            // Assert: goal session file is deleted
            Assert.False(File.Exists(goalSessionFile), "Goal session file should be deleted after merge");

            // Assert: the goal's child actor was removed after the merge
            Assert.False(brain.GoalSessionExists(goalId), "Goal session should no longer exist after merge");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SummarizeAndMergeAsync_FailedGoalSessionDeletedWithoutMerging()
    {
        // Arrange: simulate a failed goal - goal session exists but we call DeleteGoalSession directly
        // (mimicking what GoalDispatcher does when a goal fails)
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient());
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            var initialMasterMessageCount = ActorMasterSession(brain).MessageHistory.Count;

            // Create a goal session
            var goalId = "goal-failed-test";
            await brain.ForkSessionForGoalAsync(goalId, TestContext.Current.CancellationToken);

            // Verify goal session file exists
            var goalSessionFile = Path.Combine(tempDir, "actors", $"brain-goal-{goalId}.json");
            Assert.True(File.Exists(goalSessionFile), "Goal session file should exist after fork");

            // Act: delete goal session directly (as GoalDispatcher does for failed goals)
            // without calling SummarizeAndMergeAsync
            await brain.DeleteGoalSessionAsync(goalId, TestContext.Current.CancellationToken);

            // Assert: goal session file is deleted
            Assert.False(File.Exists(goalSessionFile), "Goal session file should be deleted");

            // Assert: the actor's master session history is unchanged (no summary messages added)
            Assert.Equal(initialMasterMessageCount, ActorMasterSession(brain).MessageHistory.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SummarizeAndMergeAsync_SummaryGenerationFailure_DoesNotPreventDeletion()
    {
        // Arrange: use a pre-cancelled token to force OperationCanceledException during execution
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Use a stub client for normal setup
            var stubClient = new IterationPlanStubClient(
                callId: "call-summary-fail",
                phases: ["coding"],
                reason: "Test");

            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: stubClient);

            // Connect
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            // Create a goal session
            var goalId = "goal-cancel-test";
            await brain.ForkSessionForGoalAsync(goalId, TestContext.Current.CancellationToken);

            // Verify goal session file exists
            var goalSessionFile = Path.Combine(tempDir, "actors", $"brain-goal-{goalId}.json");
            Assert.True(File.Exists(goalSessionFile), "Goal session file should exist after fork");

            var pipeline = CreatePipeline(goalId, "Test goal for cancellation scenario");

            // Create a pre-cancelled token to force cancellation during execution
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act: the method should throw TaskCanceledException due to the cancelled token
            // The context.Gate.WaitAsync(ct) should throw immediately
            await Assert.ThrowsAsync<TaskCanceledException>(
                () => brain.SummarizeAndMergeAsync(pipeline, cts.Token));

            // Assert: goal session file still exists (SummarizeAndMergeAsync didn't reach deletion)
            // because the cancellation happened before the operation started
            Assert.True(File.Exists(goalSessionFile),
                "Goal session file should still exist when exception is thrown before deletion");

            // Cleanup: simulate what GoalDispatcher does on exception
            await brain.DeleteGoalSessionAsync(goalId, TestContext.Current.CancellationToken);
            Assert.False(File.Exists(goalSessionFile), "Goal session file should be deleted after explicit cleanup");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── Compaction Model Tests ─────────────────────────────────────────────────

    /// <summary>
    /// <see cref="DistributedBrain"/> must store the <c>compactionModel</c> constructor
    /// parameter in its private <c>_compactionModel</c> field so that
    /// per-goal context creation can use it to create a separate compaction client.
    /// </summary>
    [Fact]
    public void Constructor_CompactionModel_StoresValue()
    {
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
            compactionModel: "copilot/gpt-5.4-mini");

        var field = typeof(DistributedBrain)
            .GetField("_compactionModel", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_compactionModel field not found on DistributedBrain");

        Assert.Equal("copilot/gpt-5.4-mini", field.GetValue(brain));
    }

    // ── UpdateModelAsync Tests ────────────────────────────────────────────────

    [Fact]
    public async Task UpdateModelAsync_UpdatesModelOverride_AndRecreatesAgent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var brain = new DistributedBrain("copilot/old-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(),
                chatClientFactory: _ => new FakeChatClient());
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            var modelField = typeof(DistributedBrain)
                .GetField("_modelOverride", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            Assert.Equal("copilot/old-model", modelField.GetValue(brain));

            await brain.UpdateModelAsync("copilot/new-model", null, null, TestContext.Current.CancellationToken);

            Assert.Equal("copilot/new-model", modelField.GetValue(brain));

            // Verify GetStats().Model also reflects the new model
            var stats = brain.GetStats();
            Assert.NotNull(stats);
            Assert.Equal("copilot/new-model", stats.Model);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateModelAsync_UpdatesMaxContextTokens_WhenProvided()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(), maxContextTokens: 64000,
                chatClientFactory: _ => new FakeChatClient());
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            var maxCtxField = typeof(DistributedBrain)
                .GetField("_maxContextTokens", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            Assert.Equal(64000, maxCtxField.GetValue(brain));

            await brain.UpdateModelAsync("copilot/other-model", 128000, null, TestContext.Current.CancellationToken);

            Assert.Equal(128000, maxCtxField.GetValue(brain));

            // Verify GetStats().MaxContextTokens also reflects the new value
            var stats = brain.GetStats();
            Assert.NotNull(stats);
            Assert.Equal(128000, stats.MaxContextTokens);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateModelAsync_PreservesMaxContextTokens_WhenNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(), maxContextTokens: 64000,
                chatClientFactory: _ => new FakeChatClient());
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            var maxCtxField = typeof(DistributedBrain)
                .GetField("_maxContextTokens", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            Assert.Equal(64000, maxCtxField.GetValue(brain));

            await brain.UpdateModelAsync("copilot/other-model", null, null, TestContext.Current.CancellationToken);

            Assert.Equal(64000, maxCtxField.GetValue(brain));

            // Verify GetStats().MaxContextTokens also retains the previous value
            var stats = brain.GetStats();
            Assert.NotNull(stats);
            Assert.Equal(64000, stats.MaxContextTokens);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateModelAsync_DoesNotDisposeInjectedChatClient()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var disposableClient = new DisposableCountingChatClient();
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: disposableClient,
                chatClientFactory: _ => new FakeChatClient());
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            // The injected chat client is owned by the Brain and disposed only at Brain disposal —
            // NOT on model update. With per-goal contexts there is no single shared chat client to
            // swap; each goal context creates its own client lazily when forked.
            Assert.Equal(0, disposableClient.DisposeCount);

            await brain.UpdateModelAsync("copilot/other-model", null, null, TestContext.Current.CancellationToken);

            // The injected client must NOT be disposed by a model update.
            Assert.Equal(0, disposableClient.DisposeCount);

            // Behavioural proof the model override was updated: a subsequent connect-time master
            // registration reports the new model rather than the original one.
            var stats = brain.GetStats();
            Assert.NotNull(stats);
            Assert.Equal("copilot/other-model", stats!.Model);

            // Disposal — and only disposal — releases the injected client, exactly once.
            await brain.DisposeAsync();
            Assert.Equal(1, disposableClient.DisposeCount);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── Configured ReasoningEffort (explicit enum) tests ──────────────────────

    private static readonly BindingFlags BrainNonPublic = BindingFlags.NonPublic | BindingFlags.Instance;

    private static ReasoningEffort? ConfiguredReasoning(DistributedBrain brain) =>
        (ReasoningEffort?)typeof(DistributedBrain)
            .GetField("_configuredReasoningEffort", BrainNonPublic)!.GetValue(brain);

    private static ReasoningEffort? EffectiveReasoning(DistributedBrain brain) =>
        (ReasoningEffort?)typeof(DistributedBrain)
            .GetField("_reasoningEffort", BrainNonPublic)!.GetValue(brain);

    private static ReasoningEffort? ActorReasoning(DistributedBrain brain)
    {
        var actor = typeof(DistributedBrain).GetField("_brainActor", BrainNonPublic)!.GetValue(brain);
        Assert.NotNull(actor);
        return (ReasoningEffort?)typeof(CopilotHive.Actors.BrainActor)
            .GetField("_reasoningEffort", BrainNonPublic)!.GetValue(actor);
    }

    [Fact]
    public async Task Constructor_ConfiguredReasoningEffort_IsTheOnlySource()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // The ':low' colon segment is part of the model name — only the configured
            // enum determines the reasoning effort, and it is what reaches the BrainActor.
            var brain = new DistributedBrain("copilot/gpt-5.4:low", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(),
                chatClientFactory: _ => new FakeChatClient(),
                reasoningEffort: ReasoningEffort.High);
            await using (brain)
            {
                Assert.Equal(ReasoningEffort.High, ConfiguredReasoning(brain));
                Assert.Equal(ReasoningEffort.High, EffectiveReasoning(brain));

                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                Assert.Equal(ReasoningEffort.High, ActorReasoning(brain));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Constructor_NoConfiguredReasoningEffort_LeavesReasoningUnset()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var brain = new DistributedBrain("copilot/gpt-5.4:low", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(),
                chatClientFactory: _ => new FakeChatClient());
            await using (brain)
            {
                Assert.Null(ConfiguredReasoning(brain));
                Assert.Null(EffectiveReasoning(brain));

                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                Assert.Null(ActorReasoning(brain));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateModelAsync_WithReasoningEffort_UpdatesConfiguredValueAndActor()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(),
                chatClientFactory: _ => new FakeChatClient());
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                Assert.Null(ConfiguredReasoning(brain));

                // The suffix says 'low' but the explicit enum says 'ExtraHigh' — the enum wins
                // both locally and in the actor.
                await brain.UpdateModelAsync("copilot/gpt-5.4:low", null,
                    ReasoningEffort.ExtraHigh, TestContext.Current.CancellationToken);

                Assert.Equal(ReasoningEffort.ExtraHigh, ConfiguredReasoning(brain));
                Assert.Equal(ReasoningEffort.ExtraHigh, EffectiveReasoning(brain));
                Assert.Equal(ReasoningEffort.ExtraHigh, ActorReasoning(brain));
                Assert.Equal("copilot/gpt-5.4:low", brain.GetStats()!.Model);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateModelAsync_WithNullReasoningEffort_PreservesConfiguredValue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(),
                chatClientFactory: _ => new FakeChatClient(),
                reasoningEffort: ReasoningEffort.Medium);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                // Null reasoning → the configured value must not be cleared, and it
                // must beat the ':high' suffix of the new model both locally and in the actor.
                await brain.UpdateModelAsync("copilot/gpt-5.4:high", null, null, TestContext.Current.CancellationToken);

                Assert.Equal(ReasoningEffort.Medium, ConfiguredReasoning(brain));
                Assert.Equal(ReasoningEffort.Medium, EffectiveReasoning(brain));
                Assert.Equal(ReasoningEffort.Medium, ActorReasoning(brain));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateModelAsync_WithNullReasoningEffort_AndNoConfiguredValue_LeavesReasoningUnset()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(),
                chatClientFactory: _ => new FakeChatClient());
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                await brain.UpdateModelAsync("copilot/gpt-5.4:high", null, null, TestContext.Current.CancellationToken);

                // No configured value and no suffix fallback — reasoning stays unset everywhere.
                Assert.Null(ConfiguredReasoning(brain));
                Assert.Null(EffectiveReasoning(brain));
                Assert.Null(ActorReasoning(brain));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateModelAsync_WithReasoningEffort_ActorFailure_DoesNotCommitConfiguredValue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var brain = new DistributedBrain("copilot/test-model:medium", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir, chatClient: new FakeChatClient(),
                chatClientFactory: _ => new FakeChatClient(),
                reasoningEffort: ReasoningEffort.Medium);
            await brain.ConnectAsync(TestContext.Current.CancellationToken);
            try
            {
                Assert.Equal(ReasoningEffort.Medium, ConfiguredReasoning(brain));
                Assert.Equal(ReasoningEffort.Medium, EffectiveReasoning(brain));

                // Dispose the brain so the actor is gone: the next AskActorAsync must throw,
                // simulating a failed actor update (mailbox unavailable/closed).
                await brain.DisposeAsync();

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    brain.UpdateModelAsync("copilot/gpt-5.4:low", null,
                        ReasoningEffort.ExtraHigh, TestContext.Current.CancellationToken));

                // The actor is the source of truth: a failed update must NOT commit the new value.
                Assert.Equal(ReasoningEffort.Medium, ConfiguredReasoning(brain));
                Assert.Equal(ReasoningEffort.Medium, EffectiveReasoning(brain));
            }
            finally
            {
                // DisposeAsync is idempotent — safe to await again for cleanup.
                await brain.DisposeAsync();
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateModelAsync_ConcurrentCalls_AreSerialized_ActorAndLocalStateAgree()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        // Gate that blocks the BrainActor's message loop: the first chat-client creation (triggered
        // by the fork below) parks the loop, so a subsequent UpdateModelMessage sits unprocessed in
        // the mailbox and both concurrent UpdateModelAsync calls are guaranteed to overlap.
        var factoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateArmed = 1;

        // No injected chat client: the actor must go through the factory when forking.
        var brain = new DistributedBrain("copilot/model-initial", NullLogger<DistributedBrain>.Instance,
            stateDir: tempDir,
            chatClientFactory: _ =>
            {
                if (Interlocked.Exchange(ref gateArmed, 0) == 1)
                {
                    factoryEntered.TrySetResult();
                    releaseGate.Task.GetAwaiter().GetResult();
                }

                return new FakeChatClient();
            },
            reasoningEffort: ReasoningEffort.Medium);

        try
        {
            await brain.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ReasoningEffort.Medium, ConfiguredReasoning(brain));

            var sessionLock = (SemaphoreSlim)typeof(DistributedBrain)
                .GetField("_sessionLock", BrainNonPublic)!.GetValue(brain)!;

            // Park the actor loop.
            var forkTask = brain.ForkSessionForGoalAsync("gate-goal", TestContext.Current.CancellationToken);
            await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Call A: explicit High. The uncontended lock is taken synchronously, so by the time the
            // task is returned A has already published its message to the (parked) actor.
            var taskA = brain.UpdateModelAsync("copilot/model-a", null,
                ReasoningEffort.High, TestContext.Current.CancellationToken);

            // A holds the session lock for the whole actor round-trip. Without the lock this is 1,
            // which is exactly the window in which B could read stale state.
            Assert.Equal(0, sessionLock.CurrentCount);

            // Call B: model-only update (null reasoning) — its effective reasoning is whatever
            // _configuredReasoningEffort holds when B runs. Serialization forces B to observe A's
            // committed High; without it B would capture the stale Medium and send it to the actor
            // after A, leaving the actor on Medium while the facade reports High.
            var taskB = brain.UpdateModelAsync("copilot/model-b", null,
                null, TestContext.Current.CancellationToken);

            Assert.False(taskA.IsCompleted);
            Assert.False(taskB.IsCompleted);

            releaseGate.TrySetResult();

            await taskA.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await taskB.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await forkTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // The lock is released on every path.
            Assert.Equal(1, sessionLock.CurrentCount);

            var actorModel = (string)typeof(CopilotHive.Actors.BrainActor)
                .GetField("_modelOverride", BrainNonPublic)!
                .GetValue(typeof(DistributedBrain).GetField("_brainActor", BrainNonPublic)!.GetValue(brain))!;

            // Last writer wins consistently: B is the last to complete, and the actor and the facade
            // agree on both the model and the reasoning effort.
            Assert.Equal("copilot/model-b", actorModel);
            Assert.Equal("copilot/model-b", brain.GetStats()!.Model);
            Assert.Equal(ReasoningEffort.High, ActorReasoning(brain));
            Assert.Equal(ReasoningEffort.High, ConfiguredReasoning(brain));
            Assert.Equal(ReasoningEffort.High, EffectiveReasoning(brain));
        }
        finally
        {
            // Never leave the actor loop parked, or disposal would hang.
            releaseGate.TrySetResult();
            await brain.DisposeAsync();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }


    // -- BrainActor lifecycle tests --

    private static readonly System.Reflection.BindingFlags NonPublicInstance =
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

    private static object? GetBrainActor(DistributedBrain brain) =>
        typeof(DistributedBrain).GetField("_brainActor", NonPublicInstance)!.GetValue(brain);

    /// <summary>Reads the master AgentSession owned by the brain's actor — the sole owner of session state.</summary>
    private static AgentSession ActorMasterSession(DistributedBrain brain)
    {
        var actor = GetBrainActor(brain);
        Assert.NotNull(actor);
        return (AgentSession)typeof(CopilotHive.Actors.BrainActor)
            .GetField("_masterSession", NonPublicInstance)!.GetValue(actor)!;
    }

    private static bool IsConnected(DistributedBrain brain) =>
        (bool)typeof(DistributedBrain).GetField("_connected", NonPublicInstance)!.GetValue(brain)!;

    private static bool IsDisposing(DistributedBrain brain) =>
        (bool)typeof(DistributedBrain).GetField("_disposing", NonPublicInstance)!.GetValue(brain)!;

    private static Task? GetDisposeTask(DistributedBrain brain) =>
        (Task?)typeof(DistributedBrain).GetField("_disposeTask", NonPublicInstance)!.GetValue(brain);

    private static void SetActorFactory(DistributedBrain brain, Func<string, CopilotHive.Actors.BrainActor> factory) =>
        typeof(DistributedBrain).GetField("_actorFactory", NonPublicInstance)!.SetValue(brain, factory);

    private static CopilotHive.Configuration.HiveConfigFile ActorConfig() =>
        new() { Orchestrator = new CopilotHive.Configuration.OrchestratorConfig() };

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"brain-actor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task ConnectAsync_CreatesActorAndConnects()
    {
        var dir = NewTempDir();
        try
        {
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: dir, chatClient: new FakeChatClient(), hiveConfig: ActorConfig());
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                var actor = (CopilotHive.Actors.BrainActor?)GetBrainActor(brain);
                Assert.NotNull(actor);
                Assert.True(Directory.Exists(Path.Combine(dir, "actors")));

                // The actor's loop must actually be running.
                var isStarted = (bool)typeof(CopilotHive.Actors.BrainActor)
                    .GetProperty("IsStarted", NonPublicInstance)!.GetValue(actor)!;
                Assert.True(isStarted);

                // The ConnectMessage must have been processed, not merely enqueued.
                var stats = CopilotHive.Actors.BrainActorMessages.CreateGetStatsMessage();
                Assert.True(actor!.Tell(stats));
                var reply = await stats.Reply.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.NotNull(reply);
                Assert.True(reply!.IsConnected);
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task ConnectAsync_PassesConfigRepoToBrainActor()
    {
        var dir = NewTempDir();
        var configDir = Path.Combine(Path.GetTempPath(), $"config-repo-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(configDir);
            var configRepo = new ConfigRepoManager("https://example.com/config.git", configDir);

            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: dir, chatClient: new FakeChatClient(), hiveConfig: ActorConfig(),
                configRepo: configRepo);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);

                var actor = (CopilotHive.Actors.BrainActor?)GetBrainActor(brain);
                Assert.NotNull(actor);

                var configRepoField = typeof(CopilotHive.Actors.BrainActor)
                    .GetField("_configRepo", NonPublicInstance)!;
                Assert.Same(configRepo, configRepoField.GetValue(actor));
            }
        }
        finally
        {
            DeleteDir(dir);
            try { Directory.Delete(configDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ForkSessionForGoalAsync_PassesConfigRepoToChildGoalBrainActor()
    {
        var dir = NewTempDir();
        var configDir = Path.Combine(Path.GetTempPath(), $"config-repo-child-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(configDir);
            var configRepo = new ConfigRepoManager("https://example.com/config.git", configDir);

            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: dir, chatClient: new FakeChatClient(), hiveConfig: ActorConfig(),
                configRepo: configRepo);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("g-child-1", TestContext.Current.CancellationToken);

                var actor = (CopilotHive.Actors.BrainActor?)GetBrainActor(brain);
                Assert.NotNull(actor);

                var childActorsField = typeof(CopilotHive.Actors.BrainActor)
                    .GetField("_childActors", NonPublicInstance)!;
                var children = (Dictionary<string, CopilotHive.Actors.GoalBrainActor>)childActorsField.GetValue(actor)!;
                Assert.True(children.TryGetValue("g-child-1", out var child));

                var childConfigRepoField = typeof(CopilotHive.Actors.GoalBrainActor)
                    .GetField("_configRepo", NonPublicInstance)!;
                Assert.Same(configRepo, childConfigRepoField.GetValue(child));
            }
        }
        finally
        {
            DeleteDir(dir);
            try { Directory.Delete(configDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ActorFactory_Throws_ConnectThrows_ActorNull()
    {
        var dir = NewTempDir();
        try
        {
            var logger = new TestLogger<DistributedBrain>();
            var brain = new DistributedBrain("copilot/test-model", logger,
                stateDir: dir, chatClient: new FakeChatClient(), hiveConfig: ActorConfig());
            await using (brain)
            {
                var factoryInvoked = false;
                SetActorFactory(brain, _ =>
                {
                    factoryInvoked = true;
                    throw new InvalidOperationException("factory boom");
                });

                // The actor is now the sole execution path, so its startup failure fails the connect.
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => brain.ConnectAsync(TestContext.Current.CancellationToken));
                Assert.Equal("factory boom", ex.Message);

                Assert.True(factoryInvoked);
                Assert.Null(GetBrainActor(brain));
                Assert.Contains(logger.LogEntries, e =>
                    e.LogLevel == LogLevel.Warning
                    && e.Message.Contains("BrainActor startup failed", StringComparison.Ordinal)
                    && e.Exception is InvalidOperationException
                    && e.Exception.Message.Contains("factory boom", StringComparison.Ordinal));

                // The connect was rolled back — the brain must NOT report itself as connected.
                Assert.False(IsConnected(brain));
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task ActorFactory_ReturnsDisposedActor_TellFalse_ConnectThrows()
    {
        var dir = NewTempDir();
        try
        {
            var logger = new TestLogger<DistributedBrain>();
            var brain = new DistributedBrain("copilot/test-model", logger,
                stateDir: dir, chatClient: new FakeChatClient(), hiveConfig: ActorConfig());
            await using (brain)
            {
                var factoryInvoked = false;
                SetActorFactory(brain, stateDir =>
                {
                    factoryInvoked = true;
                    var actor = new CopilotHive.Actors.BrainActor("test-model", 1000, stateDir, logger);
                    actor.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    return actor;
                });

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => brain.ConnectAsync(TestContext.Current.CancellationToken));
                Assert.Equal("BrainActor mailbox closed", ex.Message);

                Assert.True(factoryInvoked);
                Assert.Null(GetBrainActor(brain));
                // The failure must come from the Tell-false guard, not from the 5s connect timeout.
                // Without that guard the reply never completes and the captured exception would be
                // an OperationCanceledException instead.
                Assert.Contains(logger.LogEntries, e =>
                    e.LogLevel == LogLevel.Warning
                    && e.Message.Contains("BrainActor startup failed", StringComparison.Ordinal)
                    && e.Exception is InvalidOperationException
                    && e.Exception.Message.Contains("BrainActor mailbox closed", StringComparison.Ordinal));

                // The connect was rolled back — the brain must NOT report itself as connected.
                Assert.False(IsConnected(brain));
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task DisposeAsync_Idempotent_BothCallsComplete()
    {
        var dir = NewTempDir();
        try
        {
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: dir, chatClient: new FakeChatClient(), hiveConfig: ActorConfig());
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            var actorBefore = (CopilotHive.Actors.BrainActor?)GetBrainActor(brain);
            Assert.NotNull(actorBefore);

            var first = Task.Factory.StartNew(() => brain.DisposeAsync().AsTask(),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            var second = Task.Factory.StartNew(() => brain.DisposeAsync().AsTask(),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.True(IsDisposing(brain));
            Assert.NotNull(GetDisposeTask(brain));

            // The shadow actor was disposed, so its message loop has stopped.
            Assert.True(actorBefore!.IsCompleted);
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_ReusesTheSameDisposeTask()
    {
        var dir = NewTempDir();
        try
        {
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: dir, chatClient: new FakeChatClient(), hiveConfig: ActorConfig());
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            var actorBefore = (CopilotHive.Actors.BrainActor?)GetBrainActor(brain);
            Assert.NotNull(actorBefore);

            await brain.DisposeAsync();

            var firstTask = GetDisposeTask(brain);
            Assert.NotNull(firstTask);
            Assert.True(firstTask!.IsCompleted);

            await brain.DisposeAsync();

            var secondTask = GetDisposeTask(brain);

            // A second disposal must reuse the stored task rather than starting a fresh one.
            Assert.Same(firstTask, secondTask);

            Assert.True(IsDisposing(brain));
            Assert.True(actorBefore!.IsCompleted);
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task ConnectAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var dir = NewTempDir();
        try
        {
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: dir, chatClient: new FakeChatClient(), hiveConfig: ActorConfig());
            await brain.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => brain.ConnectAsync(TestContext.Current.CancellationToken));
        }
        finally { DeleteDir(dir); }
    }

    // -- BrainActor mirror lifecycle tests --


    private static DistributedBrain NewActorBrain(string dir, ILogger<DistributedBrain>? logger = null)
    {
        var brain = new DistributedBrain("copilot/test-model", logger ?? NullLogger<DistributedBrain>.Instance,
            stateDir: dir, chatClient: new FakeChatClient(), hiveConfig: ActorConfig());
        SetActorFactory(brain, stateDir =>
            new CopilotHive.Actors.BrainActor(
                "copilot/test-model", 100_000, stateDir, NullLogger.Instance,
                chatClientFactory: _ => new FakeChatClient()));
        return brain;
    }

    private static string ActorGoalFile(string dir, string goalId) =>
        Path.Combine(dir, "actors", $"brain-goal-{goalId}.json");

    [Fact]
    public async Task ForkSessionForGoalAsync_CreatesActorSessionFile()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewActorBrain(dir);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("g1", TestContext.Current.CancellationToken);

                Assert.True(File.Exists(ActorGoalFile(dir, "g1")), "Actor session file must exist after mirrored fork");
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task ResetSessionAsync_Connected_RecreatesShadowActorAndClearsActorState()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewActorBrain(dir);
            await using (brain)
            {
                await brain.ConnectAsync(TestContext.Current.CancellationToken);
                await brain.ForkSessionForGoalAsync("g7", TestContext.Current.CancellationToken);

                var original = GetBrainActor(brain);
                Assert.NotNull(original);
                Assert.True(File.Exists(ActorGoalFile(dir, "g7")));

                var originalActor = (CopilotHive.Actors.BrainActor)original!;

                await brain.ResetSessionAsync(TestContext.Current.CancellationToken);

                var recreated = GetBrainActor(brain);
                Assert.NotNull(recreated);
                Assert.NotSame(original, recreated);

                // The old actor was disposed — its mailbox loop is completed.
                Assert.True(originalActor.IsCompleted, "Old actor must be disposed during reset");

                // All actor goal session files were deleted during reset. The new shadow actor's
                // ConnectAsync immediately persists a fresh master session, so brain-master.json
                // is recreated by the time the actor reports connected.
                Assert.False(File.Exists(ActorGoalFile(dir, "g7")),
                    "Actor goal session files must be deleted during reset");
                Assert.True(File.Exists(Path.Combine(dir, "actors", "brain-master.json")),
                    "brain-master.json must be recreated by the new shadow actor's ConnectAsync");

                // The recreated shadow is live and connected.
                var stats = CopilotHive.Actors.BrainActorMessages.CreateGetStatsMessage();
                Assert.True(((CopilotHive.Actors.BrainActor)recreated!).Tell(stats));
                var reply = await stats.Reply.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.NotNull(reply);
                Assert.True(reply!.IsConnected);

                // The recreated actor can fork a new goal session (proves it works end-to-end).
                await brain.ForkSessionForGoalAsync("g8", TestContext.Current.CancellationToken);
                Assert.True(File.Exists(ActorGoalFile(dir, "g8")),
                    "Recreated actor must handle new fork operations");
            }
        }
        finally { DeleteDir(dir); }
    }

    [Fact]
    public async Task ResetSessionAsync_NotConnected_DoesNotCreateShadowActor()
    {
        var dir = NewTempDir();
        try
        {
            var brain = NewActorBrain(dir);
            await using (brain)
            {
                Assert.False(IsConnected(brain));

                await brain.ResetSessionAsync(TestContext.Current.CancellationToken);

                Assert.Null(GetBrainActor(brain));
                Assert.False(Directory.Exists(Path.Combine(dir, "actors")));
            }
        }
        finally { DeleteDir(dir); }
    }
}

/// <summary>
/// Minimal fake implementing <see cref="IDistributedBrain"/> for unit tests.
/// </summary>
file sealed class FakeDistributedBrain : IDistributedBrain
{
    public bool Connected { get; private set; }
    public int PlanIterationCalls { get; private set; }
    public int CraftCalls { get; private set; }
    public string? LastModel { get; private set; }
    public int? LastMaxContextTokens { get; private set; }

    public Task ConnectAsync(CancellationToken ct = default) { Connected = true; return Task.CompletedTask; }

    public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct)
    {
        LastModel = model;
        LastMaxContextTokens = maxContextTokens;
        return Task.CompletedTask;
    }

    public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default)
    {
        PlanIterationCalls++;
        return Task.FromResult(PlanResult.Success(IterationPlan.Default()));
    }

    public Task<PromptResult> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default)
    {
        CraftCalls++;
        return Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));
    }

    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) => Task.CompletedTask;

    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) => Task.CompletedTask;

    public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) => Task.CompletedTask;

    public Task<BrainResponse> AskQuestionAsync(
        string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
        Task.FromResult(BrainResponse.Answer("Brain is not available. Please proceed with your best judgment."));

    public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public bool GoalSessionExists(string goalId) => false;

    public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

    public BrainStats? GetStats() => null;
}

/// <summary>
/// Minimal <see cref="IChatClient"/> stub that, on its first call, returns a tool-call
/// response for <paramref name="toolName"/> with the given <paramref name="toolArguments"/>,
/// then on subsequent calls returns a plain assistant text reply.
/// This drives <see cref="SharpCoder.CodingAgent"/>'s tool-call loop without a real LLM.
/// </summary>
file sealed class EscalateToolCallStubClient(
    string callId,
    string toolName,
    Dictionary<string, object?> toolArguments,
    string finalReply) : IChatClient
{
    private int _callCount;

    /// <inheritdoc />
    public ChatClientMetadata Metadata => new("stub", null, "stub-model");

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _callCount);

        if (call == 1)
        {
            // First call: return the escalate_to_composer tool call
            var toolCallContent = new FunctionCallContent(callId, toolName, toolArguments);
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, [toolCallContent]))
            {
                FinishReason = ChatFinishReason.ToolCalls,
            };
            return Task.FromResult(response);
        }

        // Subsequent calls: final text response after tool invocation
        var finalResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, finalReply))
        {
            FinishReason = ChatFinishReason.Stop,
        };
        return Task.FromResult(finalResponse);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming is not used in this test.");

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose() { }
}

/// <summary>
/// Minimal fake implementing <see cref="IGoalStore"/> for unit tests.
/// </summary>
file sealed class FakeGoalStore : IGoalStore
{
    private readonly Dictionary<string, Goal> _goals = new();

    public string Name => "FakeGoalStore";

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

    /// <summary>Adds a goal to the in-memory store for testing.</summary>
    public void AddGoal(Goal goal) => _goals[goal.Id] = goal;
}

/// <summary>
/// Stub client that returns a valid <c>report_iteration_plan</c> tool call on first invocation,
/// then returns text responses on subsequent calls.
/// </summary>
file sealed class IterationPlanStubClient : IChatClient
{
    private int _callCount;
    private readonly string _callId;
    private readonly string[] _phases;
    private readonly string _reason;

    public IterationPlanStubClient(string callId, string[] phases, string reason)
    {
        _callId = callId;
        _phases = phases;
        _reason = reason;
    }

    public ChatClientMetadata Metadata => new("stub", null, "stub-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _callCount);

        if (call == 1)
        {
            // First call: return report_iteration_plan tool call
            var toolCallContent = new FunctionCallContent(_callId, "report_iteration_plan", new Dictionary<string, object?>
            {
                ["phases"] = _phases,
                ["phase_instructions"] = "{}",
                ["reason"] = _reason,
            });
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, [toolCallContent]))
            {
                FinishReason = ChatFinishReason.ToolCalls,
            };
            return Task.FromResult(response);
        }

        // Subsequent calls: return plain text
        var finalResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Iteration planned."))
        {
            FinishReason = ChatFinishReason.Stop,
        };
        return Task.FromResult(finalResponse);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming is not used in this test.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>
/// Stub client that blocks on entry to simulate concurrent execution.
/// Each call waits on the provided SemaphoreSlim before returning a response,
/// allowing callers to verify that multiple calls are inside the gate simultaneously.
/// Returns a tool call on first invocation, then text on subsequent calls.
/// </summary>
file sealed class BlockingStubClient : IChatClient
{
    private int _callCount;
    private readonly string _responseText;
    private readonly string _toolName;
    private readonly Dictionary<string, object?> _toolArguments;
    private readonly SemaphoreSlim _entryGate;
    private readonly Action? _onEnteredGate;

    public BlockingStubClient(
        string responseText,
        string toolName,
        Dictionary<string, object?> toolArguments,
        SemaphoreSlim entryGate,
        Action? onEnteredGate = null)
    {
        _responseText = responseText;
        _toolName = toolName;
        _toolArguments = toolArguments;
        _entryGate = entryGate;
        _onEnteredGate = onEnteredGate;
    }

    public ChatClientMetadata Metadata => new("stub", null, "stub-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _callCount);

        // Signal that this call has entered so the test can release both at the right moment
        _onEnteredGate?.Invoke();

        // Wait until the gate is released (both calls are inside the gate by now)
        _entryGate.Wait(cancellationToken);

        if (call == 1)
        {
            // First call: return tool call
            var toolCallContent = new FunctionCallContent($"call-{call}", _toolName, _toolArguments);
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, [toolCallContent]))
            {
                FinishReason = ChatFinishReason.ToolCalls,
            };
            return Task.FromResult(response);
        }

        // Subsequent calls: return plain text
        var finalResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, _responseText))
        {
            FinishReason = ChatFinishReason.Stop,
        };
        return Task.FromResult(finalResponse);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming is not used in this test.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>
/// Stub client that throws an exception on every call, used to test error handling.
/// </summary>
file sealed class ThrowingStubClient : IChatClient
{
    private readonly Exception _exception;

    public ThrowingStubClient(Exception exception)
    {
        _exception = exception;
    }

    public ChatClientMetadata Metadata => new("stub", null, "stub-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw _exception;

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw _exception;

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>
/// IChatClient stub that counts how many times <see cref="Dispose"/> is called,
/// used to verify that <see cref="DistributedBrain.UpdateModelAsync(string, int?, Microsoft.Extensions.AI.ReasoningEffort?, CancellationToken)"/> disposes the old client.
/// </summary>
file sealed class DisposableCountingChatClient : IChatClient
{
    public int DisposeCount { get; private set; }
    public ChatClientMetadata Metadata => new("disposable-stub", null, "stub-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming not used in disposable-counting stub.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => DisposeCount++;
}
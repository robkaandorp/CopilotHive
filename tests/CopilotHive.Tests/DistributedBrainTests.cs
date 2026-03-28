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
        var result = DistributedBrain.BuildPreviousIterationContext(pipeline);
        Assert.Equal("", result);
    }

    [Fact]
    public void BuildPreviousIterationContext_SecondIteration_IncludesReviewerFeedback()
    {
        var pipeline = CreatePipeline("g-ctx-2", "Review rejected goal");
        pipeline.RecordOutput(WorkerRole.Reviewer, 1, "FAIL: Missing null check in UserService.GetById()");
        pipeline.IncrementIteration(); // Now iteration 2

        var result = DistributedBrain.BuildPreviousIterationContext(pipeline);

        Assert.Contains("Previous iteration (1) feedback:", result);
        Assert.Contains("REVIEWER feedback", result);
        Assert.Contains("Missing null check", result);
    }

    [Fact]
    public void BuildPreviousIterationContext_SecondIteration_IncludesTesterFeedback()
    {
        var pipeline = CreatePipeline("g-ctx-3", "Test failed goal");
        pipeline.RecordOutput(WorkerRole.Tester, 1, "3 tests failed: TestAuth, TestLogin, TestLogout");
        pipeline.IncrementIteration();

        var result = DistributedBrain.BuildPreviousIterationContext(pipeline);

        Assert.Contains("TESTER feedback", result);
        Assert.Contains("3 tests failed", result);
    }

    [Fact]
    public void BuildPreviousIterationContext_SecondIteration_IncludesCoderOutput()
    {
        var pipeline = CreatePipeline("g-ctx-4", "Coder context goal");
        pipeline.RecordOutput(WorkerRole.Coder, 1, "Added UserService with CRUD operations");
        pipeline.IncrementIteration();

        var result = DistributedBrain.BuildPreviousIterationContext(pipeline);

        Assert.Contains("CODER output", result);
        Assert.Contains("Added UserService", result);
    }

    [Fact]
    public void BuildPreviousIterationContext_AllPhaseOutputs_IncludesAll()
    {
        var pipeline = CreatePipeline("g-ctx-5", "Full feedback goal");
        pipeline.RecordOutput(WorkerRole.Coder, 1, "Implemented feature X");
        pipeline.RecordOutput(WorkerRole.Tester, 1, "All 50 tests pass");
        pipeline.RecordOutput(WorkerRole.Reviewer, 1, "FAIL: Variable naming inconsistent");
        pipeline.IncrementIteration();

        var result = DistributedBrain.BuildPreviousIterationContext(pipeline);

        Assert.Contains("REVIEWER feedback", result);
        Assert.Contains("TESTER feedback", result);
        Assert.Contains("CODER output", result);
    }

    [Fact]
    public void BuildPreviousIterationContext_NoOutputsRecorded_ShowsFallbackMessage()
    {
        var pipeline = CreatePipeline("g-ctx-6", "No outputs goal");
        pipeline.IncrementIteration();

        var result = DistributedBrain.BuildPreviousIterationContext(pipeline);

        Assert.Contains("Previous iteration (1) feedback:", result);
        Assert.Contains("No phase outputs recorded", result);
    }

    [Fact]
    public void BuildPreviousIterationContext_ThirdIteration_UsesIterationTwoOutputs()
    {
        var pipeline = CreatePipeline("g-ctx-7", "Multi-iteration goal");
        pipeline.RecordOutput(WorkerRole.Reviewer, 1, "FAIL: Iteration 1 issue");
        pipeline.IncrementIteration(); // Now iteration 2
        pipeline.RecordOutput(WorkerRole.Reviewer, 2, "FAIL: Iteration 2 issue");
        pipeline.IncrementIteration(); // Now iteration 3

        var result = DistributedBrain.BuildPreviousIterationContext(pipeline);

        Assert.Contains("Previous iteration (2) feedback:", result);
        Assert.Contains("Iteration 2 issue", result);
        Assert.DoesNotContain("Iteration 1 issue", result);
    }

    [Fact]
    public void BuildPreviousIterationContext_LongOutput_TruncatesReviewer()
    {
        var pipeline = CreatePipeline("g-ctx-8", "Long output goal");
        var longOutput = new string('X', 5000);
        pipeline.RecordOutput(WorkerRole.Reviewer, 1, longOutput);
        pipeline.IncrementIteration();

        var result = DistributedBrain.BuildPreviousIterationContext(pipeline);

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
    public async Task SaveSessionAsync_CreatesFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"brain-test-{Guid.NewGuid():N}");
        try
        {
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempDir);

            await brain.SaveSessionAsync(TestContext.Current.CancellationToken);

            var sessionFile = Path.Combine(tempDir, "brain-session.json");
            Assert.True(File.Exists(sessionFile), $"Session file should exist at {sessionFile}");

            var content = await File.ReadAllTextAsync(sessionFile, TestContext.Current.CancellationToken);
            Assert.Contains("brain", content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task PlanIterationAsync_WithoutConnect_ReturnsDefaultPlan()
    {
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-plan", "Test plan");

        var plan = await brain.PlanIterationAsync(pipeline, TestContext.Current.CancellationToken);

        Assert.NotNull(plan);
        Assert.NotEmpty(plan.Phases);
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

            // ResetSessionAsync should reload from disk and update _systemPrompt
            // before calling RecreateAgent. The InvalidOperationException is thrown by
            // RecreateAgent because there's no real chat client — but _systemPrompt
            // must already have been updated at that point.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => brain.ResetSessionAsync(TestContext.Current.CancellationToken));

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
    public async Task ResetSessionAsync_WithoutAgentsManager_ThrowsWhenNotConnected()
    {
        var tempStateDir = Path.Combine(Path.GetTempPath(), $"brain-reset-noagents-{Guid.NewGuid():N}");
        try
        {
            // No agentsManager provided — reset should still try to rebuild and call RecreateAgent
            var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: tempStateDir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => brain.ResetSessionAsync(TestContext.Current.CancellationToken));
            Assert.Contains("Call ConnectAsync first", ex.Message);
        }
        finally
        {
            if (Directory.Exists(tempStateDir))
                Directory.Delete(tempStateDir, true);
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
        
        var sourcePath = Path.Combine(repoRoot, "src", "CopilotHive", "Orchestration", "DistributedBrain.cs");
        Assert.True(File.Exists(sourcePath), $"Source file not found at {sourcePath}");
        
        var source = File.ReadAllText(sourcePath);
        
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

    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) => Task.CompletedTask;

    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) => Task.CompletedTask;

    public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

    public BrainStats? GetStats() => null;
}
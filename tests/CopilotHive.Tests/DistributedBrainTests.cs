using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.AI;
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
        var plan = await fake.PlanIterationAsync(pipeline, null, TestContext.Current.CancellationToken);
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

        var plan = await brain.PlanIterationAsync(pipeline, null, TestContext.Current.CancellationToken);

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

    // -- BuildCraftPromptText / Review Phase Test Results Tests --

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_WithTesterOutput_ContainsTesterString()
    {
        // Arrange: pipeline with tester output recorded for iteration 1
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-rev-1", "Add null checks to UserService");
        pipeline.RecordOutput(WorkerRole.Tester, 1, "All 42 tests pass. No failures.");

        // Act: call the internal method directly to get the raw prompt text
        var prompt = brain.BuildCraftPromptText(pipeline, GoalPhase.Review);

        // Assert: the tester output string appears verbatim in the prompt
        Assert.Contains("All 42 tests pass. No failures.", prompt);
        Assert.Contains("Current iteration test results (from the tester phase):", prompt);
    }

    [Fact]
    public void BuildCraftPromptText_CodingPhase_TesterOutputPresent_OmitsTestResults()
    {
        // Arrange: even if tester output is present, a Coding phase prompt must not include it
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-coding-1", "Implement feature Y");
        pipeline.RecordOutput(WorkerRole.Tester, 1, "Some test output that should not appear");

        // Act
        var prompt = brain.BuildCraftPromptText(pipeline, GoalPhase.Coding);

        // Assert: tester output is NOT in the prompt for Coding phase
        Assert.DoesNotContain("Some test output that should not appear", prompt);
        Assert.DoesNotContain("Current iteration test results (from the tester phase):", prompt);
    }

    [Fact]
    public void BuildCraftPromptText_TestingPhase_TesterOutputPresent_OmitsTestResults()
    {
        // Arrange: tester output present but Testing phase should not include it
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-test-1", "Run integration tests");
        pipeline.RecordOutput(WorkerRole.Tester, 1, "Previous test output should not appear");

        // Act
        var prompt = brain.BuildCraftPromptText(pipeline, GoalPhase.Testing);

        // Assert: tester output is NOT in the prompt for Testing phase
        Assert.DoesNotContain("Previous test output should not appear", prompt);
        Assert.DoesNotContain("Current iteration test results (from the tester phase):", prompt);
    }

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_TesterOutputAppearsAfterAdditionalContext()
    {
        // Arrange: pipeline with tester output and additional context
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-rev-order", "Review ordering test");
        pipeline.RecordOutput(WorkerRole.Tester, 1, "UNIQUE_TESTER_MARKER_XYZ");

        // Act
        var prompt = brain.BuildCraftPromptText(pipeline, GoalPhase.Review, "UNIQUE_CONTEXT_MARKER_ABC");

        // Assert: both markers are present
        Assert.Contains("UNIQUE_CONTEXT_MARKER_ABC", prompt);
        Assert.Contains("UNIQUE_TESTER_MARKER_XYZ", prompt);

        // Assert ordering: additionalContext appears BEFORE currentTestResults
        var contextIdx = prompt.IndexOf("Additional context:", StringComparison.Ordinal);
        var testResultsIdx = prompt.IndexOf("Current iteration test results (from the tester phase):", StringComparison.Ordinal);
        Assert.True(contextIdx >= 0, "Additional context header should be in prompt");
        Assert.True(testResultsIdx >= 0, "Test results header should be in prompt");
        Assert.True(contextIdx < testResultsIdx,
            $"Additional context (at {contextIdx}) should appear before test results (at {testResultsIdx})");
    }

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_WithoutTesterOutput_OmitsTestResultsSection()
    {
        // Arrange: no tester output recorded
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-review-notest", "Review the implementation");

        // Act
        var prompt = brain.BuildCraftPromptText(pipeline, GoalPhase.Review);

        // Assert: the test results section header should NOT be present
        Assert.DoesNotContain("Current iteration test results (from the tester phase):", prompt);
    }

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_WhitespaceOnlyTesterOutput_OmitsTestResultsSection()
    {
        // Arrange: tester output is only whitespace
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-review-ws", "Review with whitespace tester output");
        pipeline.RecordOutput(WorkerRole.Tester, 1, "   \n  \t  ");

        // Act
        var prompt = brain.BuildCraftPromptText(pipeline, GoalPhase.Review);

        // Assert: whitespace-only output should be treated as absent
        Assert.DoesNotContain("Current iteration test results (from the tester phase):", prompt);
    }

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_UsesCurrentIterationTesterOutput()
    {
        // Arrange: record tester output for two iterations, advance to iteration 2
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-review-iter", "Multi-iteration review");
        pipeline.RecordOutput(WorkerRole.Tester, 1, "ITER1_OUTPUT_SHOULD_NOT_APPEAR");
        pipeline.IncrementIteration();
        pipeline.RecordOutput(WorkerRole.Tester, 2, "ITER2_OUTPUT_EXPECTED");

        // Act: at iteration 2, should use tester-2 key
        var prompt = brain.BuildCraftPromptText(pipeline, GoalPhase.Review);

        // Assert: only iteration 2's output appears
        Assert.Contains("ITER2_OUTPUT_EXPECTED", prompt);
        Assert.DoesNotContain("ITER1_OUTPUT_SHOULD_NOT_APPEAR", prompt);
    }

    [Fact]
    public void BuildCraftPromptText_ReviewPhase_ContainsReviewerInstructionText()
    {
        // Verify the reviewer role instruction includes the exact required text
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-rev-instr", "Review instruction test");
        pipeline.RecordOutput(WorkerRole.Tester, 1, "All tests pass.");

        var prompt = brain.BuildCraftPromptText(pipeline, GoalPhase.Review);

        Assert.Contains(
            "Use the testing phase results to verify that all tests pass",
            prompt);
        Assert.Contains(
            "do NOT reject because you cannot run tests yourself",
            prompt);
    }

    [Fact]
    public void ReviewPhaseGuidance_BothBranches_ContainExactInstruction()
    {
        // Verify the exact reviewer instruction appears in BOTH Review branches in source.
        var repoRoot = Environment.CurrentDirectory;
        while (repoRoot != null && !Directory.GetFiles(repoRoot, "*.slnx").Any())
            repoRoot = Directory.GetParent(repoRoot)?.FullName;
        Assert.NotNull(repoRoot);

        var sourcePath = Path.Combine(repoRoot, "src", "CopilotHive", "Orchestration", "DistributedBrain.cs");
        Assert.True(File.Exists(sourcePath), $"Source file not found at {sourcePath}");

        var source = File.ReadAllText(sourcePath);

        const string expectedInstruction =
            "Use the testing phase results to verify that all tests pass — do NOT reject because you cannot run tests yourself.";

        // Count occurrences — both GoalPhase.Review branches + BuildReviewFallbackPrompt should have it
        var occurrences = source.Split(expectedInstruction).Length - 1;
        Assert.True(occurrences >= 3,
            $"Expected the test-results instruction to appear in at least 3 locations (2 Review branches + fallback), but found {occurrences}.");
    }

    // -- BuildReviewFallbackPrompt Tests --

    [Fact]
    public void BuildReviewFallbackPrompt_WithTesterOutput_ContainsTestResults()
    {
        var pipeline = CreatePipeline("g-fb-1", "Fix null reference in OrderService");
        pipeline.RecordOutput(WorkerRole.Tester, 1, "Passed: 87, Failed: 0");

        var prompt = DistributedBrain.BuildReviewFallbackPrompt(pipeline);

        Assert.Contains("Passed: 87, Failed: 0", prompt);
        Assert.Contains("Current iteration test results (from the tester phase):", prompt);
    }

    [Fact]
    public void BuildReviewFallbackPrompt_ContainsReviewerGuidance()
    {
        var pipeline = CreatePipeline("g-fb-2", "Update API controller");
        pipeline.RecordOutput(WorkerRole.Tester, 1, "All tests pass");

        var prompt = DistributedBrain.BuildReviewFallbackPrompt(pipeline);

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

        var prompt = DistributedBrain.BuildReviewFallbackPrompt(pipeline);

        Assert.Contains("Refactor PaymentGateway module", prompt);
    }

    [Fact]
    public void BuildReviewFallbackPrompt_WithAdditionalContext_ContainsContext()
    {
        var pipeline = CreatePipeline("g-fb-4", "Add logging");

        var prompt = DistributedBrain.BuildReviewFallbackPrompt(pipeline, "EXTRA_CONTEXT_MARKER");

        Assert.Contains("Additional context:", prompt);
        Assert.Contains("EXTRA_CONTEXT_MARKER", prompt);
    }

    [Fact]
    public void BuildReviewFallbackPrompt_NoTesterOutput_OmitsTestResultsSection()
    {
        var pipeline = CreatePipeline("g-fb-5", "Remove deprecated endpoints");

        var prompt = DistributedBrain.BuildReviewFallbackPrompt(pipeline);

        Assert.DoesNotContain("Current iteration test results (from the tester phase):", prompt);
    }

    [Fact]
    public void BuildReviewFallbackPrompt_WhitespaceOnlyTesterOutput_OmitsTestResultsSection()
    {
        var pipeline = CreatePipeline("g-fb-6", "Clean up imports");
        pipeline.RecordOutput(WorkerRole.Tester, 1, "  \n\t  ");

        var prompt = DistributedBrain.BuildReviewFallbackPrompt(pipeline);

        Assert.DoesNotContain("Current iteration test results (from the tester phase):", prompt);
    }

    [Fact]
    public void BuildReviewFallbackPrompt_UsesCurrentIterationTesterOutput()
    {
        var pipeline = CreatePipeline("g-fb-7", "Multi-iteration fallback review");
        pipeline.RecordOutput(WorkerRole.Tester, 1, "ITER1_FALLBACK_SHOULD_NOT_APPEAR");
        pipeline.IncrementIteration();
        pipeline.RecordOutput(WorkerRole.Tester, 2, "ITER2_FALLBACK_EXPECTED");

        var prompt = DistributedBrain.BuildReviewFallbackPrompt(pipeline);

        Assert.Contains("ITER2_FALLBACK_EXPECTED", prompt);
        Assert.DoesNotContain("ITER1_FALLBACK_SHOULD_NOT_APPEAR", prompt);
    }

    [Fact]
    public async Task CraftPromptAsync_NullAgent_ReviewPhase_ContainsTesterOutput()
    {
        // When agent is null, Review phase must still get tester output and guidance
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-fb-craft-1", "Fix authentication bug");
        pipeline.RecordOutput(WorkerRole.Tester, 1, "FALLBACK_TESTER_RESULTS_42");

        var prompt = await brain.CraftPromptAsync(pipeline, GoalPhase.Review, null, TestContext.Current.CancellationToken);

        Assert.Contains("FALLBACK_TESTER_RESULTS_42", prompt);
        Assert.Contains("Current iteration test results (from the tester phase):", prompt);
        Assert.Contains("do NOT reject because you cannot run tests yourself", prompt);
    }

    [Fact]
    public async Task CraftPromptAsync_NullAgent_CodingPhase_ReturnsGenericFallback()
    {
        // Non-review phases should still get the generic fallback
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-fb-craft-2", "Implement caching layer");

        var prompt = await brain.CraftPromptAsync(pipeline, GoalPhase.Coding, null, TestContext.Current.CancellationToken);

        Assert.Equal("Work on: Implement caching layer", prompt);
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

            var brain = new DistributedBrain(
                "test-model",
                NullLogger<DistributedBrain>.Instance,
                stateDir: tmpDir);

            // Inject a fake IChatClient that drives the tool-call loop:
            // Call 1: returns escalate_to_composer tool call
            // Call 2: returns plain text (after tool result is processed by CodingAgent)
            var stubClient = new EscalateToolCallStubClient(
                callId: "call-escalate-1",
                toolName: "escalate_to_composer",
                toolArguments: new Dictionary<string, object?> { ["question"] = ExpectedQuestion, ["reason"] = ExpectedReason },
                finalReply: "Escalation recorded.");

            InjectFakeChatClient(brain, stubClient);

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

    /// <summary>
    /// Uses reflection to inject a fake <see cref="IChatClient"/> into a
    /// <see cref="DistributedBrain"/> and call <c>RecreateAgent()</c> so the
    /// internal <c>CodingAgent</c> is built without a real LLM endpoint.
    /// </summary>
    private static void InjectFakeChatClient(DistributedBrain brain, IChatClient fakeClient)
    {
        var brainType = typeof(DistributedBrain);

        var chatClientField = brainType.GetField("_chatClient",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_chatClient field not found on DistributedBrain");
        chatClientField.SetValue(brain, fakeClient);

        var recreateAgent = brainType.GetMethod("RecreateAgent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RecreateAgent method not found on DistributedBrain");
        recreateAgent.Invoke(brain, null);
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

    public Task<IterationPlan> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default)
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

    public Task<BrainResponse> AskQuestionAsync(
        string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
        Task.FromResult(BrainResponse.Answer("Brain is not available. Please proceed with your best judgment."));

    public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

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
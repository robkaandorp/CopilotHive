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

    // ── GetFallbackPrompt removed — Brain failure = failed goal ─────────

    // ── EnsureConnected fallback behavior ───────────────────────────────
    // With per-goal sessions, AskAsync catches the InvalidOperationException
    // and returns null. CraftPromptAsync now throws instead of falling back.

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
    public async Task CraftPromptAsync_WithoutConnect_Throws()
    {
        var brain = new DistributedBrain(9999, NullLogger<DistributedBrain>.Instance);
        var pipeline = CreatePipeline("g-4", "Some goal");
        pipeline.SetActiveTask("task-1", "copilothive/g-4/coder-001");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => brain.CraftPromptAsync(pipeline, "coder", null));
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

    // ── FallbackParseTestMetrics / ApplyTestMetricsFallback Tests ────────

    [Fact]
    public void FallbackParseTestMetrics_DotnetTestSummary_ExtractsCorrectCounts()
    {
        var output = "Passed!  - Failed:     0, Passed:   268, Skipped:     0, Total:   268";

        var metrics = DistributedBrain.FallbackParseTestMetrics(output);

        Assert.NotNull(metrics);
        Assert.Equal(268, metrics.TotalTests);
        Assert.Equal(268, metrics.PassedTests);
        Assert.Equal(0,   metrics.FailedTests ?? -1); // 0 was explicitly in the output
    }

    [Fact]
    public void FallbackParseTestMetrics_EmptyString_ReturnsNull()
    {
        var metrics = DistributedBrain.FallbackParseTestMetrics("");
        Assert.Null(metrics);
    }

    [Fact]
    public void FallbackParseTestMetrics_NoPatterns_ReturnsNull()
    {
        var metrics = DistributedBrain.FallbackParseTestMetrics("No test output here.");
        Assert.Null(metrics);
    }

    [Fact]
    public void FallbackParseTestMetrics_KeyValueLines_ExtractsCorrectCounts()
    {
        var output = "total: 8\npassed: 8\nfailed: 0";

        var metrics = DistributedBrain.FallbackParseTestMetrics(output);

        Assert.NotNull(metrics);
        Assert.Equal(8, metrics.TotalTests);
        Assert.Equal(8, metrics.PassedTests);
    }

    /// <summary>Test A — Brain returns valid test_metrics → those values are used.</summary>
    [Fact]
    public void ApplyTestMetricsFallback_BrainReturnsValidMetrics_BrainValuesUsed()
    {
        var logger = NullLogger<DistributedBrain>.Instance;
        var decision = new OrchestratorDecision
        {
            TestMetrics = new ExtractedTestMetrics { TotalTests = 10, PassedTests = 9, FailedTests = 1 },
        };

        var result = DistributedBrain.ApplyTestMetricsFallback(
            decision, "testing", "Passed: 5, Failed: 0, Total: 5", logger);

        Assert.Equal(10, result.TestMetrics!.TotalTests);
        Assert.Equal(9,  result.TestMetrics.PassedTests);
        Assert.Equal(1,  result.TestMetrics.FailedTests);
    }

    /// <summary>Test B — Brain returns null test_metrics for testing phase → fallback kicks in.</summary>
    [Fact]
    public void ApplyTestMetricsFallback_BrainReturnsNullMetrics_FallbackUsed()
    {
        var logger = NullLogger<DistributedBrain>.Instance;
        var decision = new OrchestratorDecision { TestMetrics = null };
        var rawOutput = "Passed: 8, Failed: 0, Total: 8";

        var result = DistributedBrain.ApplyTestMetricsFallback(
            decision, "testing", rawOutput, logger);

        Assert.NotNull(result.TestMetrics);
        Assert.Equal(8, result.TestMetrics.TotalTests);
        Assert.Equal(8, result.TestMetrics.PassedTests);
        Assert.Equal(0, result.TestMetrics.FailedTests ?? -1); // 0 was explicitly in the output
    }

    /// <summary>Test C — Brain values take priority unless Brain values are 0.</summary>
    [Fact]
    public void ApplyTestMetricsFallback_BrainNonZero_BrainWins()
    {
        var logger = NullLogger<DistributedBrain>.Instance;
        var decision = new OrchestratorDecision
        {
            TestMetrics = new ExtractedTestMetrics { TotalTests = 5, PassedTests = 5 },
        };
        // Raw output has 10 — Brain's 5 should win
        var rawOutput = "total: 10\npassed: 10";

        var result = DistributedBrain.ApplyTestMetricsFallback(
            decision, "testing", rawOutput, logger);

        Assert.Equal(5, result.TestMetrics!.TotalTests);
        Assert.Equal(5, result.TestMetrics.PassedTests);
    }

    /// <summary>Test C (variant) — Brain returns total_tests: 0 → fallback wins.</summary>
    [Fact]
    public void ApplyTestMetricsFallback_BrainReturnsZeroTotal_FallbackWins()
    {
        var logger = NullLogger<DistributedBrain>.Instance;
        var decision = new OrchestratorDecision
        {
            TestMetrics = new ExtractedTestMetrics { TotalTests = 0, PassedTests = 0 },
        };
        var rawOutput = "total: 8\npassed: 8";

        var result = DistributedBrain.ApplyTestMetricsFallback(
            decision, "testing", rawOutput, logger);

        Assert.Equal(8, result.TestMetrics!.TotalTests);
        Assert.Equal(8, result.TestMetrics.PassedTests);
    }

    [Fact]
    public void ApplyTestMetricsFallback_NonTesterRole_NoChange()
    {
        var logger = NullLogger<DistributedBrain>.Instance;
        var decision = new OrchestratorDecision { TestMetrics = null };
        var rawOutput = "total: 8\npassed: 8";

        var result = DistributedBrain.ApplyTestMetricsFallback(
            decision, "coding", rawOutput, logger);

        Assert.Null(result.TestMetrics);
    }

    // ── New: xUnit / fallback-merge Tests ────────────────────────────────

    [Fact]
    public void FallbackParseTestMetrics_XUnitRunnerFormat_ExtractsCorrectCounts()
    {
        // Real xUnit runner output with extra spaces
        var output = "Passed!  - Failed:     0, Passed:   322, Skipped:     0, Total:   322, Duration: 3s";

        var result = DistributedBrain.FallbackParseTestMetrics(output);

        Assert.NotNull(result);
        Assert.Equal(322, result.PassedTests);
        Assert.Equal(322, result.TotalTests);
        Assert.Equal(0,   result.FailedTests ?? -1);
        Assert.Equal(0,   result.SkippedTests ?? -1);
    }

    [Fact]
    public void FallbackParseTestMetrics_StandardDotnetFormat_ExtractsCorrectCounts()
    {
        var output = "Failed: 0, Passed: 322, Skipped: 0, Total: 322, Duration: 2s";

        var result = DistributedBrain.FallbackParseTestMetrics(output);

        Assert.NotNull(result);
        Assert.Equal(322, result.PassedTests);
        Assert.Equal(322, result.TotalTests);
    }

    /// <summary>Core bug: Brain extracted TotalTests but not PassedTests → fallback PassedTests used.</summary>
    [Fact]
    public void ApplyTestMetricsFallback_BrainPassedZeroFallbackNonZero_FallbackPassedUsed()
    {
        var logger = NullLogger<DistributedBrain>.Instance;
        var decision = new OrchestratorDecision
        {
            TestMetrics = new ExtractedTestMetrics { TotalTests = 331, PassedTests = 0 },
        };
        var testerOutput = "Passed!  - Failed:     9, Passed:   322, Skipped:     0, Total:   331";

        var result = DistributedBrain.ApplyTestMetricsFallback(
            decision, "testing", testerOutput, logger);

        Assert.NotNull(result.TestMetrics);
        Assert.Equal(322, result.TestMetrics.PassedTests);
        Assert.Equal(331, result.TestMetrics.TotalTests);
    }

    /// <summary>Both Brain and fallback have PassedTests=0 → keep 0.</summary>
    [Fact]
    public void ApplyTestMetricsFallback_BrainPassedZeroFallbackZero_KeepsZero()
    {
        var logger = NullLogger<DistributedBrain>.Instance;
        var decision = new OrchestratorDecision
        {
            TestMetrics = new ExtractedTestMetrics { TotalTests = 10, PassedTests = 0 },
        };
        var testerOutput = "Failed: 10, Passed: 0, Skipped: 0, Total: 10";

        var result = DistributedBrain.ApplyTestMetricsFallback(
            decision, "testing", testerOutput, logger);

        Assert.NotNull(result.TestMetrics);
        Assert.Equal(0, result.TestMetrics.PassedTests ?? -1);
    }

    /// <summary>Brain has PassedTests=5 → Brain wins even if fallback has 322.</summary>
    [Fact]
    public void ApplyTestMetricsFallback_BrainPassedNonZero_BrainPassedWins()
    {
        var logger = NullLogger<DistributedBrain>.Instance;
        var decision = new OrchestratorDecision
        {
            TestMetrics = new ExtractedTestMetrics { TotalTests = 5, PassedTests = 5 },
        };
        var testerOutput = "Passed!  - Failed:     0, Passed:   322, Skipped:     0, Total:   322";

        var result = DistributedBrain.ApplyTestMetricsFallback(
            decision, "testing", testerOutput, logger);

        Assert.NotNull(result.TestMetrics);
        Assert.Equal(5, result.TestMetrics.PassedTests);
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

    public Task<IterationPlan> PlanIterationAsync(GoalPipeline pipeline, CancellationToken ct = default)
    {
        PlanIterationCalls++;
        var plan = PlanIterationOverride?.Invoke(pipeline) ?? IterationPlan.Default();
        return Task.FromResult(plan);
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

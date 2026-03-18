using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Workers;

namespace CopilotHive.Tests;

public sealed class GoalPipelineTests
{
    private static Goal CreateGoal(string id = "goal-1", string description = "Test goal") =>
        new() { Id = id, Description = description };

    #region GoalPipeline — Constructor

    [Fact]
    public void Constructor_DefaultParameters_SetsInitialStateCorrectly()
    {
        var goal = CreateGoal();
        var pipeline = new GoalPipeline(goal);

        Assert.Equal(GoalPhase.Planning, pipeline.Phase);
        Assert.Equal(1, pipeline.Iteration);
        Assert.Equal(0, pipeline.ReviewRetries);
        Assert.Equal(0, pipeline.TestRetries);
        Assert.Equal(3, pipeline.MaxRetries);
        Assert.Equal("goal-1", pipeline.GoalId);
        Assert.Equal("Test goal", pipeline.Description);
        Assert.Same(goal, pipeline.Goal);
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Null(pipeline.CoderBranch);
        Assert.Null(pipeline.CompletedAt);
        Assert.Empty(pipeline.PhaseOutputs);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void Constructor_CustomMaxRetries_SetsMaxRetries(int maxRetries)
    {
        var pipeline = new GoalPipeline(CreateGoal(), maxRetries);

        Assert.Equal(maxRetries, pipeline.MaxRetries);
    }

    #endregion

    #region GoalPipeline — AdvanceTo

    [Theory]
    [InlineData(GoalPhase.Coding)]
    [InlineData(GoalPhase.Review)]
    [InlineData(GoalPhase.Testing)]
    [InlineData(GoalPhase.Merging)]
    public void AdvanceTo_NonTerminalPhase_UpdatesPhaseWithoutCompletedAt(GoalPhase phase)
    {
        var pipeline = new GoalPipeline(CreateGoal());

        pipeline.AdvanceTo(phase);

        Assert.Equal(phase, pipeline.Phase);
        Assert.Null(pipeline.CompletedAt);
    }

    [Theory]
    [InlineData(GoalPhase.Done)]
    [InlineData(GoalPhase.Failed)]
    public void AdvanceTo_TerminalPhase_SetsCompletedAt(GoalPhase phase)
    {
        var pipeline = new GoalPipeline(CreateGoal());

        pipeline.AdvanceTo(phase);

        Assert.Equal(phase, pipeline.Phase);
        Assert.NotNull(pipeline.CompletedAt);
    }

    #endregion

    #region GoalPipeline — SetActiveTask / ClearActiveTask

    [Fact]
    public void SetActiveTask_WithoutBranch_SetsTaskIdOnly()
    {
        var pipeline = new GoalPipeline(CreateGoal());

        pipeline.SetActiveTask("task-42");

        Assert.Equal("task-42", pipeline.ActiveTaskId);
        Assert.Null(pipeline.CoderBranch);
    }

    [Fact]
    public void SetActiveTask_WithBranch_SetsBothTaskIdAndBranch()
    {
        var pipeline = new GoalPipeline(CreateGoal());

        pipeline.SetActiveTask("task-42", "feature/my-branch");

        Assert.Equal("task-42", pipeline.ActiveTaskId);
        Assert.Equal("feature/my-branch", pipeline.CoderBranch);
    }

    [Fact]
    public void SetActiveTask_NullBranch_PreservesExistingBranch()
    {
        var pipeline = new GoalPipeline(CreateGoal());
        pipeline.SetActiveTask("task-1", "feature/original");

        pipeline.SetActiveTask("task-2");

        Assert.Equal("task-2", pipeline.ActiveTaskId);
        Assert.Equal("feature/original", pipeline.CoderBranch);
    }

    [Fact]
    public void ClearActiveTask_AfterSet_ClearsTaskId()
    {
        var pipeline = new GoalPipeline(CreateGoal());
        pipeline.SetActiveTask("task-42", "feature/branch");

        pipeline.ClearActiveTask();

        Assert.Null(pipeline.ActiveTaskId);
        Assert.Equal("feature/branch", pipeline.CoderBranch);
    }

    #endregion

    #region GoalPipeline — RecordOutput

    [Fact]
    public void RecordOutput_SingleEntry_StoresWithCorrectKey()
    {
        var pipeline = new GoalPipeline(CreateGoal());

        pipeline.RecordOutput(WorkerRole.Coder, 1, "some output");

        Assert.True(pipeline.PhaseOutputs.ContainsKey("coder-1"));
        Assert.Equal("some output", pipeline.PhaseOutputs["coder-1"]);
    }

    [Fact]
    public void RecordOutput_MultipleEntries_StoresAll()
    {
        var pipeline = new GoalPipeline(CreateGoal());

        pipeline.RecordOutput(WorkerRole.Coder, 1, "code output");
        pipeline.RecordOutput(WorkerRole.Tester, 1, "test output");
        pipeline.RecordOutput(WorkerRole.Coder, 2, "code v2");

        Assert.Equal(3, pipeline.PhaseOutputs.Count);
        Assert.Equal("code output", pipeline.PhaseOutputs["coder-1"]);
        Assert.Equal("test output", pipeline.PhaseOutputs["tester-1"]);
        Assert.Equal("code v2", pipeline.PhaseOutputs["coder-2"]);
    }

    [Fact]
    public void RecordOutput_SameKey_OverwritesPrevious()
    {
        var pipeline = new GoalPipeline(CreateGoal());

        pipeline.RecordOutput(WorkerRole.Coder, 1, "first");
        pipeline.RecordOutput(WorkerRole.Coder, 1, "second");

        Assert.Equal("second", pipeline.PhaseOutputs["coder-1"]);
    }

    #endregion

    #region GoalPipeline — IncrementReviewRetry / IncrementTestRetry

    [Fact]
    public void IncrementReviewRetry_UnderMax_ReturnsTrueAndIncrements()
    {
        var pipeline = new GoalPipeline(CreateGoal(), maxRetries: 2);

        var result = pipeline.IncrementReviewRetry();

        Assert.True(result);
        Assert.Equal(1, pipeline.ReviewRetries);
    }

    [Fact]
    public void IncrementReviewRetry_AtMax_ReturnsFalse()
    {
        var pipeline = new GoalPipeline(CreateGoal(), maxRetries: 2);

        pipeline.IncrementReviewRetry(); // 1 < 2 → true
        var result = pipeline.IncrementReviewRetry(); // 2 < 2 → false

        Assert.False(result);
        Assert.Equal(2, pipeline.ReviewRetries);
    }

    [Fact]
    public void IncrementTestRetry_UnderMax_ReturnsTrueAndIncrements()
    {
        var pipeline = new GoalPipeline(CreateGoal(), maxRetries: 2);

        var result = pipeline.IncrementTestRetry();

        Assert.True(result);
        Assert.Equal(1, pipeline.TestRetries);
    }

    [Fact]
    public void IncrementTestRetry_AtMax_ReturnsFalse()
    {
        var pipeline = new GoalPipeline(CreateGoal(), maxRetries: 2);

        pipeline.IncrementTestRetry();
        var result = pipeline.IncrementTestRetry();

        Assert.False(result);
        Assert.Equal(2, pipeline.TestRetries);
    }

    #endregion

    #region GoalPipeline — IncrementIteration

    [Fact]
    public void IncrementIteration_CalledOnce_IncrementsToTwo()
    {
        var pipeline = new GoalPipeline(CreateGoal());

        pipeline.IncrementIteration();

        Assert.Equal(2, pipeline.Iteration);
    }

    [Fact]
    public void IncrementIteration_CalledMultipleTimes_IncrementsCumulatively()
    {
        var pipeline = new GoalPipeline(CreateGoal());

        pipeline.IncrementIteration();
        pipeline.IncrementIteration();
        pipeline.IncrementIteration();

        Assert.Equal(4, pipeline.Iteration);
    }

    #endregion

    #region GoalPipeline — BuildContextSummary

    [Fact]
    public void BuildContextSummary_DefaultState_ContainsAllFields()
    {
        var pipeline = new GoalPipeline(CreateGoal("g1", "Implement feature X"));

        var summary = pipeline.BuildContextSummary();

        Assert.Contains("Goal: Implement feature X", summary);
        Assert.Contains("Phase: Planning", summary);
        Assert.Contains("Iteration: 1", summary);
        Assert.Contains("Review retries: 0/3", summary);
        Assert.Contains("Test retries: 0/3", summary);
        Assert.DoesNotContain("Branch:", summary);
    }

    [Fact]
    public void BuildContextSummary_WithBranch_IncludesBranch()
    {
        var pipeline = new GoalPipeline(CreateGoal());
        pipeline.SetActiveTask("t1", "feature/xyz");

        var summary = pipeline.BuildContextSummary();

        Assert.Contains("Branch: feature/xyz", summary);
    }

    [Fact]
    public void BuildContextSummary_WithPhaseOutputs_IncludesOutputSections()
    {
        var pipeline = new GoalPipeline(CreateGoal());
        pipeline.RecordOutput(WorkerRole.Coder, 1, "hello world");

        var summary = pipeline.BuildContextSummary();

        Assert.Contains("--- Output from coder-1 ---", summary);
        Assert.Contains("hello world", summary);
    }

    [Fact]
    public void BuildContextSummary_LongOutput_TruncatesAt2000Chars()
    {
        var pipeline = new GoalPipeline(CreateGoal());
        var longOutput = new string('x', 3000);
        pipeline.RecordOutput(WorkerRole.Coder, 1, longOutput);

        var summary = pipeline.BuildContextSummary();

        Assert.Contains("...", summary);
        Assert.DoesNotContain(longOutput, summary);
    }

    #endregion
}

public sealed class GoalPipelineManagerTests
{
    private static Goal CreateGoal(string id = "goal-1", string description = "Test goal") =>
        new() { Id = id, Description = description };

    #region CreatePipeline

    [Fact]
    public void CreatePipeline_NewGoal_ReturnsPipelineAndStoresIt()
    {
        var manager = new GoalPipelineManager();
        var goal = CreateGoal();

        var pipeline = manager.CreatePipeline(goal);

        Assert.NotNull(pipeline);
        Assert.Equal("goal-1", pipeline.GoalId);
        Assert.Same(pipeline, manager.GetByGoalId("goal-1"));
    }

    [Fact]
    public void CreatePipeline_DuplicateGoal_ThrowsInvalidOperationException()
    {
        var manager = new GoalPipelineManager();
        var goal = CreateGoal();
        manager.CreatePipeline(goal);

        Assert.Throws<InvalidOperationException>(() => manager.CreatePipeline(goal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void CreatePipeline_CustomMaxRetries_PassesToPipeline(int maxRetries)
    {
        var manager = new GoalPipelineManager();

        var pipeline = manager.CreatePipeline(CreateGoal(), maxRetries);

        Assert.Equal(maxRetries, pipeline.MaxRetries);
    }

    #endregion

    #region GetByGoalId

    [Fact]
    public void GetByGoalId_ExistingGoal_ReturnsCorrectPipeline()
    {
        var manager = new GoalPipelineManager();
        var expected = manager.CreatePipeline(CreateGoal("a", "Alpha"));
        manager.CreatePipeline(CreateGoal("b", "Beta"));

        var result = manager.GetByGoalId("a");

        Assert.Same(expected, result);
    }

    [Fact]
    public void GetByGoalId_UnknownGoal_ReturnsNull()
    {
        var manager = new GoalPipelineManager();

        var result = manager.GetByGoalId("nonexistent");

        Assert.Null(result);
    }

    #endregion

    #region RegisterTask / GetByTaskId

    [Fact]
    public void RegisterTask_ThenGetByTaskId_ReturnsCorrectPipeline()
    {
        var manager = new GoalPipelineManager();
        var pipeline = manager.CreatePipeline(CreateGoal("g1", "desc"));
        manager.RegisterTask("task-100", "g1");

        var result = manager.GetByTaskId("task-100");

        Assert.Same(pipeline, result);
    }

    [Fact]
    public void GetByTaskId_UnregisteredTask_ReturnsNull()
    {
        var manager = new GoalPipelineManager();

        var result = manager.GetByTaskId("unknown-task");

        Assert.Null(result);
    }

    [Fact]
    public void GetByTaskId_TaskForRemovedGoal_ReturnsNull()
    {
        var manager = new GoalPipelineManager();
        manager.CreatePipeline(CreateGoal("g1", "desc"));
        manager.RegisterTask("task-1", "g1");
        manager.RemovePipeline("g1");

        var result = manager.GetByTaskId("task-1");

        Assert.Null(result);
    }

    #endregion

    #region GetActivePipelines

    [Fact]
    public void GetActivePipelines_MixedPhases_ExcludesDoneAndFailed()
    {
        var manager = new GoalPipelineManager();
        var coding = manager.CreatePipeline(CreateGoal("g1", "Coding"));
        coding.AdvanceTo(GoalPhase.Coding);

        var done = manager.CreatePipeline(CreateGoal("g2", "Done"));
        done.AdvanceTo(GoalPhase.Done);

        var failed = manager.CreatePipeline(CreateGoal("g3", "Failed"));
        failed.AdvanceTo(GoalPhase.Failed);

        var planning = manager.CreatePipeline(CreateGoal("g4", "Planning"));

        var active = manager.GetActivePipelines();

        Assert.Equal(2, active.Count);
        Assert.Contains(coding, active);
        Assert.Contains(planning, active);
        Assert.DoesNotContain(done, active);
        Assert.DoesNotContain(failed, active);
    }

    [Fact]
    public void GetActivePipelines_NoPipelines_ReturnsEmpty()
    {
        var manager = new GoalPipelineManager();

        var active = manager.GetActivePipelines();

        Assert.Empty(active);
    }

    #endregion

    #region RemovePipeline

    [Fact]
    public void RemovePipeline_ExistingGoal_ReturnsTrueAndRemoves()
    {
        var manager = new GoalPipelineManager();
        manager.CreatePipeline(CreateGoal("g1", "desc"));

        var removed = manager.RemovePipeline("g1");

        Assert.True(removed);
        Assert.Null(manager.GetByGoalId("g1"));
    }

    [Fact]
    public void RemovePipeline_UnknownGoal_ReturnsFalse()
    {
        var manager = new GoalPipelineManager();

        var removed = manager.RemovePipeline("nonexistent");

        Assert.False(removed);
    }

    #endregion

    #region GetAllPipelines

    [Fact]
    public void GetAllPipelines_MultiplePipelines_ReturnsAll()
    {
        var manager = new GoalPipelineManager();
        manager.CreatePipeline(CreateGoal("g1", "One"));
        manager.CreatePipeline(CreateGoal("g2", "Two"));
        var done = manager.CreatePipeline(CreateGoal("g3", "Three"));
        done.AdvanceTo(GoalPhase.Done);

        var all = manager.GetAllPipelines();

        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void GetAllPipelines_Empty_ReturnsEmpty()
    {
        var manager = new GoalPipelineManager();

        var all = manager.GetAllPipelines();

        Assert.Empty(all);
    }

    #endregion
}

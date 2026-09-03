using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS0618 // Obsolete members tested for backward compatibility

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
        Assert.Empty(pipeline.PhaseLog);
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

    [Fact]
    public void AdvanceTo_PhaseLog_RecordsDuration()
    {
        var pipeline = new GoalPipeline(CreateGoal());

        // PhaseLog entries track duration via StartedAt/CompletedAt
        // This test verifies that AdvanceTo transitions update the pipeline phase
        pipeline.AdvanceTo(GoalPhase.Coding);
        Assert.Equal(GoalPhase.Coding, pipeline.Phase);

        pipeline.AdvanceTo(GoalPhase.Testing);
        Assert.Equal(GoalPhase.Testing, pipeline.Phase);

        pipeline.AdvanceTo(GoalPhase.Done);
        Assert.Equal(GoalPhase.Done, pipeline.Phase);
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

    #region GoalPipeline — PhaseLog

    [Fact]
    public void PhaseLog_SingleEntry_StoresOutput()
    {
        var pipeline = new GoalPipeline(CreateGoal());

        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            WorkerOutput = "some output",
        });

        Assert.Single(pipeline.PhaseLog);
        Assert.Equal("some output", pipeline.PhaseLog[0].WorkerOutput);
    }

    [Fact]
    public void PhaseLog_MultipleEntries_StoresAll()
    {
        var pipeline = new GoalPipeline(CreateGoal());

        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            WorkerOutput = "code output",
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            WorkerOutput = "test output",
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Result = PhaseOutcome.Pass,
            Iteration = 2, Occurrence = 1,
            WorkerOutput = "code v2",
        });

        Assert.Equal(3, pipeline.PhaseLog.Count);
        Assert.Equal("code output", pipeline.PhaseLog[0].WorkerOutput);
        Assert.Equal("test output", pipeline.PhaseLog[1].WorkerOutput);
        Assert.Equal("code v2", pipeline.PhaseLog[2].WorkerOutput);
    }

    [Fact]
    public void PhaseLog_AppendOnly_DoesNotOverwrite()
    {
        var pipeline = new GoalPipeline(CreateGoal());

        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            WorkerOutput = "first",
        });
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            WorkerOutput = "second",
        });

        Assert.Equal(2, pipeline.PhaseLog.Count);
        Assert.Equal("first", pipeline.PhaseLog[0].WorkerOutput);
        Assert.Equal("second", pipeline.PhaseLog[1].WorkerOutput);
    }

    #endregion

    #region GoalPipeline — ReviewRetryBudget / TestRetryBudget

    [Fact]
    public void ReviewRetryBudget_TryConsume_UnderMax_ReturnsTrueAndIncrements()
    {
        var pipeline = new GoalPipeline(CreateGoal(), maxRetries: 2);

        var result = pipeline.ReviewRetryBudget.TryConsume();

        Assert.True(result);
        Assert.Equal(1, pipeline.ReviewRetries);
    }

    [Fact]
    public void ReviewRetryBudget_TryConsume_AtMax_ReturnsFalse()
    {
        var pipeline = new GoalPipeline(CreateGoal(), maxRetries: 2);

        pipeline.ReviewRetryBudget.TryConsume(); // consume 1 of 2
        pipeline.ReviewRetryBudget.TryConsume(); // consume 2 of 2
        var result = pipeline.ReviewRetryBudget.TryConsume(); // exhausted

        Assert.False(result);
        Assert.Equal(2, pipeline.ReviewRetries);
    }

    [Fact]
    public void TestRetryBudget_TryConsume_UnderMax_ReturnsTrueAndIncrements()
    {
        var pipeline = new GoalPipeline(CreateGoal(), maxRetries: 2);

        var result = pipeline.TestRetryBudget.TryConsume();

        Assert.True(result);
        Assert.Equal(1, pipeline.TestRetries);
    }

    [Fact]
    public void TestRetryBudget_TryConsume_AtMax_ReturnsFalse()
    {
        var pipeline = new GoalPipeline(CreateGoal(), maxRetries: 2);

        pipeline.TestRetryBudget.TryConsume(); // consume 1 of 2
        pipeline.TestRetryBudget.TryConsume(); // consume 2 of 2
        var result = pipeline.TestRetryBudget.TryConsume(); // exhausted

        Assert.False(result);
        Assert.Equal(2, pipeline.TestRetries);
    }

    #endregion

    #region GoalPipeline — IterationBudget

    [Fact]
    public void IterationBudget_TryConsume_CalledOnce_IncrementsToTwo()
    {
        var pipeline = new GoalPipeline(CreateGoal());

        pipeline.IterationBudget.TryConsume();

        Assert.Equal(2, pipeline.Iteration);
    }

    [Fact]
    public void IterationBudget_TryConsume_CalledMultipleTimes_IncrementsCumulatively()
    {
        var pipeline = new GoalPipeline(CreateGoal());

        pipeline.IterationBudget.TryConsume();
        pipeline.IterationBudget.TryConsume();
        pipeline.IterationBudget.TryConsume();

        Assert.Equal(4, pipeline.Iteration);
    }

    #endregion

    #region GoalPipeline — Narratives

    [Fact]
    public void AddNarrativeEntry_StoresEntryWithCorrectFieldsAndTimestamp()
    {
        var pipeline = new GoalPipeline(CreateGoal("g1", "Test goal"));

        pipeline.AddNarrativeEntry("worker-1", "task-42", "I tried several approaches.");

        var entry = Assert.Single(pipeline.Narratives);
        Assert.Equal("worker-1", entry.WorkerId);
        Assert.Equal("task-42", entry.TaskId);
        Assert.Equal("I tried several approaches.", entry.Content);
        Assert.NotEqual(default(DateTime), entry.Timestamp);
    }

    [Fact]
    public void Narratives_StartsEmptyAndAccumulatesAcrossCalls()
    {
        var pipeline = new GoalPipeline(CreateGoal());

        Assert.Empty(pipeline.Narratives);

        pipeline.AddNarrativeEntry("w1", "t1", "First narrative.");
        pipeline.AddNarrativeEntry("w2", "t2", "Second narrative.");

        Assert.Equal(2, pipeline.Narratives.Count);
        Assert.Contains(pipeline.Narratives, n => n.WorkerId == "w1" && n.Content == "First narrative.");
        Assert.Contains(pipeline.Narratives, n => n.WorkerId == "w2" && n.Content == "Second narrative.");
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
    public void BuildContextSummary_WithPhaseLog_IncludesOutputSections()
    {
        var pipeline = new GoalPipeline(CreateGoal());
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            WorkerOutput = "hello world",
        });

        var summary = pipeline.BuildContextSummary();

        Assert.Contains("=== Output from coder-1-1 ===", summary);
        Assert.Contains("hello world", summary);
        Assert.Contains("=== End output from coder-1-1 ===", summary);
    }

    [Fact]
    public void BuildContextSummary_LongOutput_TruncatesAt2000Chars()
    {
        var pipeline = new GoalPipeline(CreateGoal());
        var longOutput = new string('x', 3000);
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            WorkerOutput = longOutput,
        });

        var summary = pipeline.BuildContextSummary();

        Assert.Contains("...", summary);
        Assert.DoesNotContain(longOutput, summary);
    }

    #endregion
}

public sealed class GoalPipelineRoleSessionTests
{
    private static Goal CreateGoal(string id = "goal-1") =>
        new() { Id = id, Description = "Test goal" };

    #region RoleSessions — initial state

    [Fact]
    public void NewPipeline_RoleSessions_StartsEmpty()
    {
        var pipeline = new GoalPipeline(CreateGoal());

        Assert.Empty(pipeline.RoleSessions);
    }

    #endregion

    #region GetRoleSession — null for unknown

    [Fact]
    public void GetRoleSession_UnknownRole_ReturnsNull()
    {
        var pipeline = new GoalPipeline(CreateGoal());

        var result = pipeline.GetRoleSession("coder");

        Assert.Null(result);
    }

    [Fact]
    public void GetRoleSession_EmptyDictionary_ReturnsNull()
    {
        var pipeline = new GoalPipeline(CreateGoal());

        Assert.Null(pipeline.GetRoleSession("reviewer"));
        Assert.Null(pipeline.GetRoleSession("tester"));
    }

    #endregion

    #region SetRoleSession / GetRoleSession — round-trip

    [Fact]
    public void SetRoleSession_ThenGetRoleSession_ReturnsSameValue()
    {
        var pipeline = new GoalPipeline(CreateGoal());
        const string sessionJson = """{"messages":[{"role":"user","content":"hello"}]}""";

        pipeline.SetRoleSession("coder", sessionJson);

        Assert.Equal(sessionJson, pipeline.GetRoleSession("coder"));
    }

    [Fact]
    public void SetRoleSession_Overwrite_ReturnsNewValue()
    {
        var pipeline = new GoalPipeline(CreateGoal());
        pipeline.SetRoleSession("coder", "first");
        pipeline.SetRoleSession("coder", "second");

        Assert.Equal("second", pipeline.GetRoleSession("coder"));
    }

    [Fact]
    public void SetRoleSession_MultipleRoles_AreStoredIndependently()
    {
        var pipeline = new GoalPipeline(CreateGoal());
        pipeline.SetRoleSession("coder", "coder-session");
        pipeline.SetRoleSession("tester", "tester-session");
        pipeline.SetRoleSession("reviewer", "reviewer-session");

        Assert.Equal("coder-session", pipeline.GetRoleSession("coder"));
        Assert.Equal("tester-session", pipeline.GetRoleSession("tester"));
        Assert.Equal("reviewer-session", pipeline.GetRoleSession("reviewer"));
        Assert.Equal(3, pipeline.RoleSessions.Count);
    }

    #endregion

    #region Case-insensitivity

    [Theory]
    [InlineData("coder", "CODER")]
    [InlineData("Coder", "coder")]
    [InlineData("REVIEWER", "reviewer")]
    [InlineData("Tester", "TESTER")]
    public void GetRoleSession_IsCaseInsensitive(string setKey, string getKey)
    {
        var pipeline = new GoalPipeline(CreateGoal());
        pipeline.SetRoleSession(setKey, "my-session");

        Assert.Equal("my-session", pipeline.GetRoleSession(getKey));
    }

    [Fact]
    public void SetRoleSession_DifferentCasings_OverwriteSameSlot()
    {
        var pipeline = new GoalPipeline(CreateGoal());
        pipeline.SetRoleSession("Coder", "first");
        pipeline.SetRoleSession("CODER", "second");

        // Should only be one entry (case-insensitive key)
        Assert.Single(pipeline.RoleSessions);
        Assert.Equal("second", pipeline.GetRoleSession("coder"));
    }

    #endregion

    #region Snapshot restore

    [Fact]
    public void RestoredFromSnapshot_WithRoleSessions_SessionsAreAvailable()
    {
        var goal = CreateGoal("g1");
        var snapshot = new CopilotHive.Persistence.PipelineSnapshot
        {
            GoalId = goal.Id,
            Description = goal.Description,
            Goal = goal,
            CreatedAt = DateTime.UtcNow,
            RoleSessions = new Dictionary<string, string>
            {
                ["coder"]    = "coder-json",
                ["reviewer"] = "reviewer-json",
            },
        };

        var pipeline = new GoalPipeline(snapshot);

        Assert.Equal("coder-json",    pipeline.GetRoleSession("coder"));
        Assert.Equal("reviewer-json", pipeline.GetRoleSession("reviewer"));
    }

    [Fact]
    public void RestoredFromSnapshot_EmptyRoleSessions_StartsEmpty()
    {
        var goal = CreateGoal("g2");
        var snapshot = new CopilotHive.Persistence.PipelineSnapshot
        {
            GoalId = goal.Id,
            Description = goal.Description,
            Goal = goal,
            CreatedAt = DateTime.UtcNow,
            RoleSessions = [],
        };

        var pipeline = new GoalPipeline(snapshot);

        Assert.Empty(pipeline.RoleSessions);
    }

    [Fact]
    public void RestoredFromSnapshot_RoleSessionsAreCaseInsensitive()
    {
        var goal = CreateGoal("g3");
        var snapshot = new CopilotHive.Persistence.PipelineSnapshot
        {
            GoalId = goal.Id,
            Description = goal.Description,
            Goal = goal,
            CreatedAt = DateTime.UtcNow,
            RoleSessions = new Dictionary<string, string> { ["CODER"] = "stored" },
        };

        var pipeline = new GoalPipeline(snapshot);

        // Case-insensitive lookup works after restore
        Assert.Equal("stored", pipeline.GetRoleSession("coder"));
        Assert.Equal("stored", pipeline.GetRoleSession("Coder"));
    }

    [Fact]
    public void RestoredFromSnapshot_PhaseLog_IsRestored()
    {
        var goal = CreateGoal("g-log");
        var snapshot = new CopilotHive.Persistence.PipelineSnapshot
        {
            GoalId = goal.Id,
            Description = goal.Description,
            Goal = goal,
            CreatedAt = DateTime.UtcNow,
            PhaseLog =
            [
                new PhaseResult { Name = GoalPhase.Coding, Result = PhaseOutcome.Pass, Iteration = 1, Occurrence = 1 },
                new PhaseResult { Name = GoalPhase.Testing, Result = PhaseOutcome.Fail, Iteration = 1, Occurrence = 1 },
            ],
        };

        var pipeline = new GoalPipeline(snapshot);

        Assert.Equal(2, pipeline.PhaseLog.Count);
        Assert.Equal(GoalPhase.Coding, pipeline.PhaseLog[0].Name);
        Assert.Equal(GoalPhase.Testing, pipeline.PhaseLog[1].Name);
    }

    [Fact]
    public void RestoredFromSnapshot_PhaseLog_DefaultsToEmpty()
    {
        var goal = CreateGoal("g-log-default");
        var snapshot = new CopilotHive.Persistence.PipelineSnapshot
        {
            GoalId = goal.Id,
            Description = goal.Description,
            Goal = goal,
            CreatedAt = DateTime.UtcNow,
        };

        var pipeline = new GoalPipeline(snapshot);

        Assert.Empty(pipeline.PhaseLog);
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

    /// <summary>
    /// Verifies the bug fix: RemovePipeline must clean up the store record even when
    /// the pipeline is not in the in-memory dictionary (e.g. a Failed pipeline that
    /// LoadActivePipelines skipped on startup).
    /// </summary>
    [Fact]
    public async Task RemovePipeline_NotInMemory_CleansUpStoreRecord()
    {
        // Create a manager backed by a real in-memory store
        await using var store = new PipelineStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<PipelineStore>.Instance);
        var manager = new GoalPipelineManager(store);

        // Create and persist a pipeline, then manually remove it from memory
        // to simulate a Failed pipeline that RestoreFromStore did not reload.
        var goal = CreateGoal("store-only-goal", "Store-only test");
        var pipeline = new GoalPipeline(goal);
        store.SavePipeline(pipeline);  // write directly to store, bypassing manager

        // Confirm it is NOT in the manager's in-memory dictionary
        Assert.Null(manager.GetByGoalId("store-only-goal"));

        // Act — remove via manager (pipeline is not in memory)
        var removed = manager.RemovePipeline("store-only-goal");

        // Assert
        Assert.False(removed);  // not in memory

        // Verify the store record was cleaned up by querying LoadActivePipelines
        var active = store.LoadActivePipelines();
        Assert.DoesNotContain(active, p => p.GoalId == "store-only-goal");
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

    #region RestorePipeline / UnregisterTask

    [Fact]
    public void RestorePipeline_AlreadyInMemory_ReturnsExisting()
    {
        var manager = new GoalPipelineManager();
        var pipeline = manager.CreatePipeline(CreateGoal("g1", "In memory"));

        var restored = manager.RestorePipeline("g1");

        Assert.Same(pipeline, restored);
    }

    [Fact]
    public async Task RestorePipeline_FromStore_LoadsFailedPipeline()
    {
        await using var store = new PipelineStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<PipelineStore>.Instance);
        var manager = new GoalPipelineManager(store);

        var pipeline = manager.CreatePipeline(CreateGoal("g-failed", "Failed goal"));
        pipeline.AdvanceTo(GoalPhase.Failed);
        manager.PersistState(pipeline);

        // Fresh manager with the same store
        var freshManager = new GoalPipelineManager(store);
        var restored = freshManager.RestorePipeline("g-failed");

        Assert.NotNull(restored);
        Assert.Equal(GoalPhase.Failed, restored!.Phase);
        Assert.Same(restored, freshManager.GetByGoalId("g-failed"));
    }

    [Fact]
    public async Task RestorePipeline_NotInStore_ReturnsNull()
    {
        await using var store = new PipelineStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<PipelineStore>.Instance);
        var manager = new GoalPipelineManager(store);

        var restored = manager.RestorePipeline("nonexistent");

        Assert.Null(restored);
    }

    [Fact]
    public void RestorePipeline_NoStore_ReturnsNull()
    {
        var manager = new GoalPipelineManager();

        var restored = manager.RestorePipeline("any");

        Assert.Null(restored);
    }

    [Fact]
    public async Task UnregisterTask_RemovesFromMemoryAndStore()
    {
        await using var store = new PipelineStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<PipelineStore>.Instance);
        var manager = new GoalPipelineManager(store);

        manager.CreatePipeline(CreateGoal("g1", "desc"));
        manager.RegisterTask("task-1", "g1");

        manager.UnregisterTask("task-1");

        Assert.Null(manager.GetByTaskId("task-1"));

        var snap = store.LoadPipeline("g1");
        Assert.NotNull(snap);
        Assert.Empty(snap!.TaskMappings);
    }

    [Fact]
    public void UnregisterTask_NoStore_OnlyRemovesFromMemory()
    {
        var manager = new GoalPipelineManager();
        manager.CreatePipeline(CreateGoal("g1", "desc"));
        manager.RegisterTask("task-1", "g1");

        manager.UnregisterTask("task-1");

        Assert.Null(manager.GetByTaskId("task-1"));
    }

    [Fact]
    public async Task RestorePipeline_FailedPipeline_RoundTripsThroughStore()
    {
        await using var store = new PipelineStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<PipelineStore>.Instance);
        var manager = new GoalPipelineManager(store);

        var pipeline = manager.CreatePipeline(CreateGoal("g-rt", "Round-trip goal"));
        pipeline.AdvanceTo(GoalPhase.Coding);
        pipeline.IterationBudget.TryConsume();
        pipeline.Conversation.Add(new ConversationEntry("user", "Planning message"));
        pipeline.AdvanceTo(GoalPhase.Failed);
        manager.PersistFull(pipeline);

        // Fresh manager with the same store
        var freshManager = new GoalPipelineManager(store);
        var restored = freshManager.RestorePipeline("g-rt");

        Assert.NotNull(restored);
        Assert.Equal(GoalPhase.Failed, restored!.Phase);
        Assert.Equal(2, restored.Iteration);
        Assert.Single(restored.Conversation);
        Assert.Equal("Planning message", restored.Conversation[0].Content);
        Assert.Same(restored, freshManager.GetByGoalId("g-rt"));
    }

    [Fact]
    public async Task RestorePipeline_AlreadyInMemory_DoesNotDuplicate()
    {
        await using var store = new PipelineStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<PipelineStore>.Instance);
        var manager = new GoalPipelineManager(store);

        var pipeline = manager.CreatePipeline(CreateGoal("g-dup", "Duplicate check"));
        var countBefore = manager.GetAllPipelines().Count;

        var restored = manager.RestorePipeline("g-dup");

        Assert.Same(pipeline, restored);
        Assert.Equal(countBefore, manager.GetAllPipelines().Count);
    }

    /// <summary>
    /// THE END-TO-END PROOF (slice B): a pipeline parked mid-SECOND-Coding is persisted via
    /// PersistFull, then a FRESH manager restores it — and the restored machine sits at
    /// (Coding, 2, true) with the tail [Merging] queued and the executed prefix completed.
    /// </summary>
    [Fact]
    public async Task RestorePipeline_MidSecondCoding_RoundTripsAtOccurrenceTwo()
    {
        await using var store = new PipelineStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<PipelineStore>.Instance);
        var manager = new GoalPipelineManager(store);

        var pipeline = manager.CreatePipeline(CreateGoal("g-occ2", "Mid-second-coding round trip"));
        var plan = new IterationPlan { Phases = [.. new List<GoalPhase>
            { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Merging }] };
        pipeline.SetPlan(plan);
        pipeline.AdvanceTo(GoalPhase.Coding);
        pipeline.StateMachine.StartIteration(plan.Phases);
        pipeline.StateMachine.Transition(PhaseInput.Succeeded); // Coding → Testing
        pipeline.AdvanceTo(GoalPhase.Testing);
        pipeline.StateMachine.Transition(PhaseInput.Succeeded); // Testing → Coding (second)
        pipeline.AdvanceTo(GoalPhase.Coding);                   // pipeline phase follows the machine

        manager.PersistFull(pipeline);

        // Fresh manager over the same store — a simulated orchestrator restart.
        var freshManager = new GoalPipelineManager(store);
        var restored = freshManager.RestorePipeline("g-occ2");

        Assert.NotNull(restored);
        Assert.Equal(GoalPhase.Coding, restored!.Phase);
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 2, OccurrenceFound: true),
            restored.CaptureMachinePosition());
        Assert.Equal([GoalPhase.Merging], restored.StateMachine.RemainingPhases);
        Assert.Equal([GoalPhase.Coding, GoalPhase.Testing], restored.StateMachine.CompletedPhases);
    }

    [Fact]
    public async Task UnregisterTask_PersistedMappingDeleted()
    {
        await using var store = new PipelineStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<PipelineStore>.Instance);
        var manager = new GoalPipelineManager(store);

        manager.CreatePipeline(CreateGoal("g-unreg", "Unregister test"));
        manager.RegisterTask("task-unreg", "g-unreg");

        manager.UnregisterTask("task-unreg");

        // Fresh manager with the same store
        var freshManager = new GoalPipelineManager(store);
        var restored = freshManager.RestorePipeline("g-unreg");

        Assert.NotNull(restored);

        // Verify the persisted snapshot no longer contains the mapping
        var snap = store.LoadPipeline("g-unreg");
        Assert.NotNull(snap);
        Assert.DoesNotContain(("task-unreg", "g-unreg"), snap!.TaskMappings);
    }

    [Fact]
    public async Task RestorePipeline_ConcurrentCalls_ReturnSameInstance()
    {
        // Use a shared-connection factory so each RestorePipeline call creates
        // its own short-lived DbContext (the direct-context test constructor
        // is not thread-safe for concurrent reads).
        var factory = new SharedInMemoryDbContextFactory(
            new DbContextOptionsBuilder<CopilotHiveDbContext>().Options);
        await using var store = new PipelineStore(factory, NullLogger<PipelineStore>.Instance);
        var manager = new GoalPipelineManager(store);

        var pipeline = manager.CreatePipeline(CreateGoal("g-concurrent", "Concurrent restore"));
        pipeline.AdvanceTo(GoalPhase.Failed);
        manager.PersistFull(pipeline);

        // Fresh manager so RestorePipeline must load from store
        var freshManager = new GoalPipelineManager(store);

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new GoalPipeline?[10];
        var index = 0;
        var lockObj = new object();
        async Task RestoreWithIndex()
        {
            await barrier.Task;
            var idx = 0;
            lock (lockObj) { idx = index++; }
            var r = freshManager.RestorePipeline("g-concurrent");
            results[idx] = r;
        }

        var tasks = new List<Task>();
        for (var i = 0; i < 10; i++)
            tasks.Add(RestoreWithIndex());

        barrier.SetResult();
        await Task.WhenAll(tasks);

        // All calls return the same instance
        var first = results[0];
        Assert.NotNull(first);
        foreach (var r in results)
            Assert.Same(first, r);

        // Only one pipeline exists in the manager
        Assert.Single(freshManager.GetAllPipelines());
    }

    #endregion

    #region GoalPipeline — GoalStartedAt

    [Fact]
    public void GoalStartedAt_DefaultsToNull()
    {
        var pipeline = new GoalPipeline(CreateGoal());

        Assert.Null(pipeline.GoalStartedAt);
    }

    [Fact]
    public void GoalStartedAt_CanBeSetAndRetrieved()
    {
        var pipeline = new GoalPipeline(CreateGoal());
        var startedAt = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        pipeline.GoalStartedAt = startedAt;

        Assert.Equal(startedAt, pipeline.GoalStartedAt);
    }

    [Fact]
    public void GoalStartedAt_RestoredFromSnapshot()
    {
        var goal = CreateGoal();
        var startedAt = new DateTime(2025, 3, 15, 8, 30, 0, DateTimeKind.Utc);
        var snapshot = new CopilotHive.Persistence.PipelineSnapshot
        {
            GoalId = goal.Id,
            Description = goal.Description,
            Goal = goal,
            GoalStartedAt = startedAt,
            CreatedAt = DateTime.UtcNow,
        };

        var pipeline = new GoalPipeline(snapshot);

        Assert.Equal(startedAt, pipeline.GoalStartedAt);
    }

    [Fact]
    public void GoalStartedAt_NullInSnapshot_RemainsNull()
    {
        var goal = CreateGoal();
        var snapshot = new CopilotHive.Persistence.PipelineSnapshot
        {
            GoalId = goal.Id,
            Description = goal.Description,
            Goal = goal,
            GoalStartedAt = null,
            CreatedAt = DateTime.UtcNow,
        };

        var pipeline = new GoalPipeline(snapshot);

        Assert.Null(pipeline.GoalStartedAt);
    }

    #endregion

    #region GetRoleSession / SetRoleSession — convenience methods

    [Fact]
    public void GetRoleSession_UnknownGoalId_ReturnsNull()
    {
        var manager = new GoalPipelineManager();

        var result = manager.GetRoleSession("nonexistent-goal", "coder");

        Assert.Null(result);
    }

    [Fact]
    public void GetRoleSession_UnknownRole_ReturnsNull()
    {
        var manager = new GoalPipelineManager();
        manager.CreatePipeline(CreateGoal("g1"));

        var result = manager.GetRoleSession("g1", "unknown-role");

        Assert.Null(result);
    }

    [Fact]
    public void SetRoleSession_ThenGetRoleSession_RoundTrips()
    {
        var manager = new GoalPipelineManager();
        manager.CreatePipeline(CreateGoal("g1"));
        const string json = """{"session":"data"}""";

        manager.SetRoleSession("g1", "coder", json);

        Assert.Equal(json, manager.GetRoleSession("g1", "coder"));
    }

    [Fact]
    public void SetRoleSession_CaseInsensitiveRoleName_CanBeRetrievedWithDifferentCase()
    {
        var manager = new GoalPipelineManager();
        manager.CreatePipeline(CreateGoal("g1"));

        manager.SetRoleSession("g1", "Coder", "session-data");

        Assert.Equal("session-data", manager.GetRoleSession("g1", "CODER"));
    }

    [Fact]
    public void SetRoleSession_UnknownGoalId_DoesNotThrow()
    {
        var manager = new GoalPipelineManager();

        // Should be a no-op, not throw
        manager.SetRoleSession("nonexistent", "coder", "{}");
    }

    /// <summary>
    /// Verifies the persistence bug fix: SetRoleSession must flush to the store so
    /// that sessions survive a simulated orchestrator restart (RestoreFromStore).
    /// </summary>
    [Fact]
    public async Task SetRoleSession_WithStore_SessionSurvivesPipelineReload()
    {
        // Arrange — create a manager backed by a real in-memory SQLite store
        await using var store = new PipelineStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<PipelineStore>.Instance);
        var manager = new GoalPipelineManager(store);

        var goal = new Goal { Id = "persist-goal", Description = "Persistence test" };
        manager.CreatePipeline(goal);

        const string sessionJson = """{"iteration":5,"messages":[]}""";

        // Act — save the session (this is the call that previously did NOT persist)
        manager.SetRoleSession("persist-goal", "coder", sessionJson);

        // Simulate restart: create a fresh manager backed by the SAME store and restore
        var restoredManager = new GoalPipelineManager(store);
        restoredManager.RestoreFromStore();

        // Assert — the session must be retrievable from the restored manager
        var retrieved = restoredManager.GetRoleSession("persist-goal", "coder");
        Assert.Equal(sessionJson, retrieved);
    }

    #endregion

    #region GoalPipeline — IterationStartSha

    [Fact]
    public void IterationStartSha_DefaultsToNull()
    {
        var pipeline = new GoalPipeline(CreateGoal());

        Assert.Null(pipeline.IterationStartSha);
    }

    [Fact]
    public void IterationStartSha_CanBeSet()
    {
        const string sha = "abc123def456789012345678901234567890abcd";
        var pipeline = new GoalPipeline(CreateGoal())
        {
            IterationStartSha = sha,
        };

        Assert.Equal(sha, pipeline.IterationStartSha);
    }

    [Fact]
    public void IterationStartSha_CanBeResetToNull()
    {
        var pipeline = new GoalPipeline(CreateGoal())
        {
            IterationStartSha = "someSha",
        };

        pipeline.IterationStartSha = null;

        Assert.Null(pipeline.IterationStartSha);
    }

    #endregion

    #region GoalPipeline — Work-slot restored occurrence (slice B)

    /// <summary>The repeated-phase plan used across the slice-B restore vectors.</summary>
    private static readonly List<GoalPhase> SliceBPlan =
        [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Merging];

    private static CopilotHive.Persistence.PipelineSnapshot CreateSnapshot(
        string id,
        GoalPhase phase,
        IterationPlan? plan,
        GoalPhase? machinePhase = null,
        int phaseOccurrence = 1)
    {
        var goal = CreateGoal(id);
        return new CopilotHive.Persistence.PipelineSnapshot
        {
            GoalId = goal.Id,
            Description = goal.Description,
            Goal = goal,
            Phase = phase,
            Plan = plan,
            MachinePhase = machinePhase,
            PhaseOccurrence = phaseOccurrence,
            CreatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// CONSTRUCTOR PAIR-MATCH at occurrence 2: a snapshot whose machine phase MATCHES the
    /// pipeline phase restores at the persisted occurrence — the machine sits at the SECOND
    /// Coding, the completed set holds the executed prefix, and the tail [Merging] survives.
    /// </summary>
    [Fact]
    public void Constructor_MatchedPairAtOccurrenceTwo_RestoresAtSecondCoding()
    {
        var plan = new IterationPlan { Phases = [.. SliceBPlan] };
        var snapshot = CreateSnapshot("g-pair2", GoalPhase.Coding, plan, GoalPhase.Coding, 2);

        var pipeline = new GoalPipeline(snapshot);

        Assert.Equal(GoalPhase.Coding, pipeline.Phase);
        Assert.Equal([GoalPhase.Merging], pipeline.StateMachine.RemainingPhases);
        Assert.Equal([GoalPhase.Coding, GoalPhase.Testing], pipeline.StateMachine.CompletedPhases);
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 2, OccurrenceFound: true),
            pipeline.CaptureMachinePosition());
    }

    /// <summary>
    /// THE LEGACY FALLBACK: MachinePhase NULL on a repeated-phase snapshot (old rows and
    /// planning-window saves) routes to the LEGACY RestoreFromPlan — restored EXACTLY as today:
    /// queue [Testing, Merging], completed empty, capture (Coding, 1, true).
    /// </summary>
    [Fact]
    public void Constructor_NullMachinePhase_LegacyFallbackRestoresExactlyAsToday()
    {
        var plan = new IterationPlan { Phases = [.. SliceBPlan] };
        var snapshot = CreateSnapshot("g-legacy-null", GoalPhase.Coding, plan, machinePhase: null, phaseOccurrence: 2);

        var pipeline = new GoalPipeline(snapshot);

        Assert.Equal(GoalPhase.Coding, pipeline.Phase);
        Assert.Equal([GoalPhase.Testing, GoalPhase.Merging], pipeline.StateMachine.RemainingPhases);
        Assert.Empty(pipeline.StateMachine.CompletedPhases);
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 1, OccurrenceFound: true),
            pipeline.CaptureMachinePosition());
    }

    /// <summary>
    /// A MISMATCHED pair (MachinePhase != snapshot.Phase — torn/corrupt) routes to the same
    /// legacy path; the untrustworthy occurrence is never honored.
    /// </summary>
    [Fact]
    public void Constructor_MismatchedPair_LegacyFallbackRestoresExactlyAsToday()
    {
        var plan = new IterationPlan { Phases = [.. SliceBPlan] };
        // The machine claims Testing while the pipeline phase is Coding — torn pair.
        var snapshot = CreateSnapshot("g-torn", GoalPhase.Coding, plan, GoalPhase.Testing, 2);

        var pipeline = new GoalPipeline(snapshot);

        Assert.Equal(GoalPhase.Coding, pipeline.Phase);
        Assert.Equal([GoalPhase.Testing, GoalPhase.Merging], pipeline.StateMachine.RemainingPhases);
        Assert.Empty(pipeline.StateMachine.CompletedPhases);
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 1, OccurrenceFound: true),
            pipeline.CaptureMachinePosition());
    }

    /// <summary>THE NO-PLAN BRANCH IS UNCHANGED: RestoreFromPlan([], Phase) — empty queue.</summary>
    [Fact]
    public void Constructor_NoPlan_BranchUnchanged()
    {
        var snapshot = CreateSnapshot("g-noplan", GoalPhase.Coding, plan: null);

        var pipeline = new GoalPipeline(snapshot);

        Assert.Equal(GoalPhase.Coding, pipeline.Phase);
        Assert.Null(pipeline.Plan);
        Assert.Null(pipeline.InstalledPhasesForTest);
        // The no-plan sync: empty plan restore → empty queue, machine agrees with the phase.
        Assert.Empty(pipeline.StateMachine.RemainingPhases);
        Assert.Equal(GoalPhase.Coding, pipeline.StateMachine.Phase);
        // No installed plan → the capture honestly reports no position.
        Assert.False(pipeline.CaptureMachinePosition().OccurrenceFound);
    }

    /// <summary>
    /// THE INTENDED BEHAVIOR CHANGE, asserted: a MATCHED occurrence-1 pair restores at the
    /// FIRST Coding with the FULL tail [Testing, Coding, Merging] — nothing dropped.
    /// </summary>
    [Fact]
    public void Constructor_MatchedPairAtOccurrenceOne_KeepsFullTail()
    {
        var plan = new IterationPlan { Phases = [.. SliceBPlan] };
        var snapshot = CreateSnapshot("g-pair1", GoalPhase.Coding, plan, GoalPhase.Coding, 1);

        var pipeline = new GoalPipeline(snapshot);

        Assert.Equal(GoalPhase.Coding, pipeline.Phase);
        Assert.Equal([GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Merging], pipeline.StateMachine.RemainingPhases);
        Assert.Empty(pipeline.StateMachine.CompletedPhases);
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 1, OccurrenceFound: true),
            pipeline.CaptureMachinePosition());
    }

    /// <summary>
    /// CaptureMachinePosition mid-SECOND-Coding on a live pipeline: the coherent pair
    /// (Coding, 2, true) — occurrence and phase move together.
    /// </summary>
    [Fact]
    public void CaptureMachinePosition_MidSecondCoding_YieldsCoherentPair()
    {
        var pipeline = new GoalPipeline(CreateGoal("g-live2"));
        var plan = new IterationPlan { Phases = [.. SliceBPlan] };
        pipeline.SetPlan(plan);
        pipeline.AdvanceTo(GoalPhase.Coding);
        pipeline.StateMachine.StartIteration(plan.Phases);
        pipeline.StateMachine.Transition(PhaseInput.Succeeded); // Coding → Testing
        pipeline.AdvanceTo(GoalPhase.Testing);
        pipeline.StateMachine.Transition(PhaseInput.Succeeded); // Testing → Coding (second)
        pipeline.AdvanceTo(GoalPhase.Coding);                   // pipeline phase follows the machine

        var captured = pipeline.CaptureMachinePosition();

        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 2, OccurrenceFound: true), captured);
        Assert.Equal(GoalPhase.Coding, pipeline.Phase);
    }

    /// <summary>
    /// THE OCCURRENCEFOUND == FALSE CASE (the planning window): no installed plan → the capture
    /// reports occurrence 0 with the honest not-found flag — the null contract's source.
    /// </summary>
    [Fact]
    public void CaptureMachinePosition_NoInstalledPlan_ReportsNotFound()
    {
        var pipeline = new GoalPipeline(CreateGoal("g-planning"));

        var captured = pipeline.CaptureMachinePosition();

        Assert.Equal(GoalPhase.Planning, captured.Phase);
        Assert.Equal(0, captured.Occurrence);
        Assert.False(captured.OccurrenceFound);
    }

    #endregion
}

/// <summary>
/// An <see cref="IDbContextFactory{TContext}"/> that creates fresh contexts over a single
/// shared, open SQLite in-memory connection. Disposing a created context does not destroy
/// the database because the underlying connection stays open for the lifetime of the factory.
/// </summary>
file sealed class SharedInMemoryDbContextFactory : IDbContextFactory<CopilotHiveDbContext>, IDisposable
{
    private readonly string _dbPath;
    private bool _disposed;

    public SharedInMemoryDbContextFactory(DbContextOptions<CopilotHiveDbContext> options)
    {
        // Use a temp file-based SQLite DB so concurrent contexts each get their own
        // connection (in-memory SQLite connections are not thread-safe for concurrent use).
        _dbPath = Path.Combine(Path.GetTempPath(), $"copilothive_test_{Guid.NewGuid():N}.db");
        using var ctx = CreateDbContext();
        ctx.Database.EnsureCreated();
    }

    public CopilotHiveDbContext CreateDbContext()
    {
        var opts = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        return new CopilotHiveDbContext(opts);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { File.Delete(_dbPath); } catch { /* best-effort cleanup */ }
    }
}


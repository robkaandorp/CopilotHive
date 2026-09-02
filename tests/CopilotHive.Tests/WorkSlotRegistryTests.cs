using System.Reflection;
using System.Reflection.Emit;
using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Workers;

namespace CopilotHive.Tests;

/// <summary>
/// Work-slot registry tests.
/// Tranche 1: the record contracts, the <see cref="WorkSlotException"/> constructor/event
/// matrix, and the message contract.
/// Tranche 2: the attempt-helper behaviour, the atomicity postconditions, the refusal
/// contracts, the 25-cell state matrix, and the test-seam contracts.
/// </summary>
public sealed class WorkSlotRegistryTests
{
    private static WorkSlotPosition Position(int iteration = 1, GoalPhase phase = GoalPhase.Coding, int occurrence = 1) =>
        new(iteration, phase, occurrence);

    // WorkSlotEvent is internal, so it cannot appear in a public test method signature.
    // Tests take the integer code and map it back through Ev().
    public const int EvDoubleAssignment = (int)WorkSlotEvent.DoubleAssignment;
    public const int EvRoleMismatch = (int)WorkSlotEvent.RoleMismatch;
    public const int EvInvalidPhase = (int)WorkSlotEvent.InvalidPhase;
    public const int EvPhaseDivergence = (int)WorkSlotEvent.PhaseDivergence;
    public const int EvPlanUnavailable = (int)WorkSlotEvent.PlanUnavailable;

    private static WorkSlotEvent Ev(int code) => (WorkSlotEvent)code;

    // WorkSlotState / SlotGuardResult / SlotRecordOutcome are internal too, so the
    // table-driven vectors below carry integer codes mapped back through St().
    public const int StPending = (int)WorkSlotState.Pending;
    public const int StClaimed = (int)WorkSlotState.Claimed;
    public const int StRecorded = (int)WorkSlotState.Recorded;
    public const int StAbandoned = (int)WorkSlotState.Abandoned;

    /// <summary>Sentinel meaning "no slot is registered for the task id" (the 5th matrix column).</summary>
    public const int StNone = -1;

    public const int GuardProceed = (int)SlotGuardResult.Proceed;
    public const int GuardAbandoned = (int)SlotGuardResult.Abandoned;
    public const int GuardUnknown = (int)SlotGuardResult.Unknown;

    public const int RecRecorded = (int)SlotRecordOutcome.Recorded;
    public const int RecNoOp = (int)SlotRecordOutcome.NoOp;

    private static WorkSlotState St(int code) => (WorkSlotState)code;

    private static GoalPipeline NewPipeline() =>
        new(new Goal { Id = "goal-1", Description = "Test goal" });

    /// <summary>Order-independent snapshot of the registry (records give value equality).</summary>
    private static HashSet<WorkSlotView> Snapshot(GoalPipeline pipeline) =>
        [.. pipeline.GetSlotsForTest()];

    #region (a) Record equality

    [Fact]
    public void WorkSlotPosition_SameValues_AreEqual()
    {
        var a = new WorkSlotPosition(2, GoalPhase.Testing, 3);
        var b = new WorkSlotPosition(2, GoalPhase.Testing, 3);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void WorkSlotPosition_DifferentIteration_AreNotEqual()
    {
        Assert.NotEqual(new WorkSlotPosition(1, GoalPhase.Coding, 1), new WorkSlotPosition(2, GoalPhase.Coding, 1));
    }

    [Fact]
    public void WorkSlotPosition_DifferentPhase_AreNotEqual()
    {
        Assert.NotEqual(new WorkSlotPosition(1, GoalPhase.Coding, 1), new WorkSlotPosition(1, GoalPhase.Review, 1));
    }

    [Fact]
    public void WorkSlotPosition_DifferentOccurrence_AreNotEqual()
    {
        Assert.NotEqual(new WorkSlotPosition(1, GoalPhase.Coding, 1), new WorkSlotPosition(1, GoalPhase.Coding, 2));
    }

    [Fact]
    public void WorkSlotPosition_FullTuple_IsUsableAsDictionaryKey()
    {
        var map = new Dictionary<WorkSlotPosition, int>
        {
            [new WorkSlotPosition(1, GoalPhase.Coding, 1)] = 7,
        };

        Assert.Equal(7, map[new WorkSlotPosition(1, GoalPhase.Coding, 1)]);
        Assert.False(map.ContainsKey(new WorkSlotPosition(1, GoalPhase.Coding, 2)));
        Assert.False(map.ContainsKey(new WorkSlotPosition(2, GoalPhase.Coding, 1)));
        Assert.False(map.ContainsKey(new WorkSlotPosition(1, GoalPhase.Review, 1)));
    }

    [Fact]
    public void WorkSlot_SameValues_AreEqual()
    {
        var a = new WorkSlot("task-1", Position(), 1);
        var b = new WorkSlot("task-1", Position(), 1);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void WorkSlot_DifferentTaskId_AreNotEqual()
    {
        Assert.NotEqual(new WorkSlot("task-1", Position(), 1), new WorkSlot("task-2", Position(), 1));
    }

    [Fact]
    public void WorkSlot_DifferentPosition_AreNotEqual()
    {
        Assert.NotEqual(
            new WorkSlot("task-1", Position(occurrence: 1), 1),
            new WorkSlot("task-1", Position(occurrence: 2), 1));
    }

    [Fact]
    public void WorkSlot_DifferentAttempt_AreNotEqual()
    {
        Assert.NotEqual(new WorkSlot("task-1", Position(), 1), new WorkSlot("task-1", Position(), 2));
    }

    [Fact]
    public void WorkSlotView_SameValues_AreEqual()
    {
        var a = new WorkSlotView(new WorkSlot("task-1", Position(), 1), WorkSlotState.Pending);
        var b = new WorkSlotView(new WorkSlot("task-1", Position(), 1), WorkSlotState.Pending);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void WorkSlotView_DifferentState_AreNotEqual()
    {
        var slot = new WorkSlot("task-1", Position(), 1);

        Assert.NotEqual(new WorkSlotView(slot, WorkSlotState.Pending), new WorkSlotView(slot, WorkSlotState.Claimed));
    }

    [Fact]
    public void WorkSlotView_DifferentSlot_AreNotEqual()
    {
        Assert.NotEqual(
            new WorkSlotView(new WorkSlot("task-1", Position(), 1), WorkSlotState.Pending),
            new WorkSlotView(new WorkSlot("task-2", Position(), 1), WorkSlotState.Pending));
    }

    [Fact]
    public void SlotBuildResult_SameValues_AreEqual()
    {
        var a = new SlotBuildResult("task-1", Position(), 1);
        var b = new SlotBuildResult("task-1", Position(), 1);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void SlotBuildResult_DifferingComponents_AreNotEqual()
    {
        var baseline = new SlotBuildResult("task-1", Position(), 1);

        Assert.NotEqual(baseline, new SlotBuildResult("task-2", Position(), 1));
        Assert.NotEqual(baseline, new SlotBuildResult("task-1", Position(iteration: 2), 1));
        Assert.NotEqual(baseline, new SlotBuildResult("task-1", Position(phase: GoalPhase.Review), 1));
        Assert.NotEqual(baseline, new SlotBuildResult("task-1", Position(occurrence: 5), 1));
        Assert.NotEqual(baseline, new SlotBuildResult("task-1", Position(), 2));
    }

    #endregion

    #region (b) Constructor / event matrix — valid vectors

    [Fact]
    public void DoubleAssignmentCtor_ValidVector_PopulatesStructuredFields()
    {
        var position = new WorkSlotPosition(3, GoalPhase.Testing, 2);

        var ex = new WorkSlotException(WorkSlotEvent.DoubleAssignment, position, "task-existing");

        Assert.Equal(WorkSlotEvent.DoubleAssignment, ex.Event);
        Assert.Same(position, ex.Position);
        Assert.NotNull(ex.ExistingTaskId);
        Assert.Equal("task-existing", ex.ExistingTaskId);
        Assert.Equal(GoalPhase.Testing, ex.MachinePhase);
        Assert.Null(ex.PassedRole);
        Assert.Null(ex.DerivedRole);
        Assert.Null(ex.PipelinePhase);
    }

    [Fact]
    public void RoleMismatchCtor_ValidVector_PopulatesStructuredFields()
    {
        var position = new WorkSlotPosition(1, GoalPhase.Review, 4);

        var ex = new WorkSlotException(WorkSlotEvent.RoleMismatch, position, WorkerRole.Coder, WorkerRole.Reviewer);

        Assert.Equal(WorkSlotEvent.RoleMismatch, ex.Event);
        Assert.Same(position, ex.Position);
        Assert.Equal(WorkerRole.Coder, ex.PassedRole);
        Assert.Equal(WorkerRole.Reviewer, ex.DerivedRole);
        Assert.Equal(GoalPhase.Review, ex.MachinePhase);
        Assert.Null(ex.ExistingTaskId);
        Assert.Null(ex.PipelinePhase);
    }

    [Fact]
    public void InvalidPhaseCtor_ValidVector_PopulatesStructuredFields()
    {
        var position = new WorkSlotPosition(2, GoalPhase.Merging, 1);

        var ex = new WorkSlotException(WorkSlotEvent.InvalidPhase, position, null, GoalPhase.Merging);

        Assert.Equal(WorkSlotEvent.InvalidPhase, ex.Event);
        Assert.Same(position, ex.Position);
        Assert.Equal(GoalPhase.Merging, ex.MachinePhase);
        Assert.Null(ex.PipelinePhase);
        Assert.Null(ex.ExistingTaskId);
        Assert.Null(ex.PassedRole);
        Assert.Null(ex.DerivedRole);
    }

    [Fact]
    public void PhaseDivergenceCtor_ValidVector_PopulatesStructuredFields()
    {
        var position = new WorkSlotPosition(2, GoalPhase.Coding, 1);

        var ex = new WorkSlotException(WorkSlotEvent.PhaseDivergence, position, GoalPhase.Review, GoalPhase.Coding);

        Assert.Equal(WorkSlotEvent.PhaseDivergence, ex.Event);
        Assert.Same(position, ex.Position);
        Assert.Equal(GoalPhase.Review, ex.PipelinePhase);
        Assert.Equal(GoalPhase.Coding, ex.MachinePhase);
        Assert.Null(ex.ExistingTaskId);
        Assert.Null(ex.PassedRole);
        Assert.Null(ex.DerivedRole);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PlanUnavailableCtor_AnyOccurrence_IsValid(int occurrence)
    {
        var position = new WorkSlotPosition(1, GoalPhase.Planning, occurrence);

        var ex = new WorkSlotException(WorkSlotEvent.PlanUnavailable, position, null, GoalPhase.Planning);

        Assert.Equal(WorkSlotEvent.PlanUnavailable, ex.Event);
        Assert.Same(position, ex.Position);
        Assert.Equal(occurrence, ex.Position.Occurrence);
        Assert.Equal(GoalPhase.Planning, ex.MachinePhase);
        Assert.Null(ex.PipelinePhase);
        Assert.Null(ex.ExistingTaskId);
        Assert.Null(ex.PassedRole);
        Assert.Null(ex.DerivedRole);
    }

    #endregion

    #region (b) Constructor / event matrix — precondition violations

    [Theory]
    [InlineData(EvRoleMismatch)]
    [InlineData(EvInvalidPhase)]
    [InlineData(EvPhaseDivergence)]
    [InlineData(EvPlanUnavailable)]
    public void DoubleAssignmentCtor_WrongEvent_Throws(int evCode)
    {
        Assert.Throws<ArgumentException>(() => new WorkSlotException(Ev(evCode), Position(), "task-existing"));
    }

    [Fact]
    public void DoubleAssignmentCtor_NullPosition_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => new WorkSlotException(WorkSlotEvent.DoubleAssignment, null!, "task-existing"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DoubleAssignmentCtor_BlankExistingTaskId_ThrowsArgumentException(string? existingTaskId)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new WorkSlotException(WorkSlotEvent.DoubleAssignment, Position(), existingTaskId));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Theory]
    [InlineData(EvDoubleAssignment)]
    [InlineData(EvInvalidPhase)]
    [InlineData(EvPhaseDivergence)]
    [InlineData(EvPlanUnavailable)]
    public void RoleMismatchCtor_WrongEvent_Throws(int evCode)
    {
        Assert.Throws<ArgumentException>(
            () => new WorkSlotException(Ev(evCode), Position(), WorkerRole.Coder, WorkerRole.Tester));
    }

    [Fact]
    public void RoleMismatchCtor_NullPosition_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => new WorkSlotException(WorkSlotEvent.RoleMismatch, null!, WorkerRole.Coder, WorkerRole.Tester));
    }

    [Theory]
    [InlineData(EvDoubleAssignment)]
    [InlineData(EvRoleMismatch)]
    public void PhaseCtor_NonPhaseEvent_ThrowsArgumentException(int evCode)
    {
        Assert.Throws<ArgumentException>(
            () => new WorkSlotException(Ev(evCode), Position(), null, GoalPhase.Coding));
    }

    [Theory]
    [InlineData(EvInvalidPhase, null)]
    [InlineData(EvPhaseDivergence, GoalPhase.Review)]
    [InlineData(EvPlanUnavailable, null)]
    public void PhaseCtor_NullPosition_ThrowsArgumentNull(int evCode, GoalPhase? pipelinePhase)
    {
        Assert.Throws<ArgumentNullException>(
            () => new WorkSlotException(Ev(evCode), null!, pipelinePhase, GoalPhase.Coding));
    }

    [Theory]
    [InlineData(EvInvalidPhase, null)]
    [InlineData(EvPhaseDivergence, GoalPhase.Review)]
    [InlineData(EvPlanUnavailable, null)]
    public void PhaseCtor_PositionPhaseDiffersFromMachinePhase_ThrowsArgumentException(
        int evCode, GoalPhase? pipelinePhase)
    {
        Assert.Throws<ArgumentException>(
            () => new WorkSlotException(Ev(evCode), Position(phase: GoalPhase.Coding), pipelinePhase, GoalPhase.Testing));
    }

    [Fact]
    public void PhaseCtor_InvalidPhaseWithPipelinePhase_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => new WorkSlotException(WorkSlotEvent.InvalidPhase, Position(), GoalPhase.Coding, GoalPhase.Coding));
    }

    [Fact]
    public void PhaseCtor_PhaseDivergenceWithoutPipelinePhase_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => new WorkSlotException(WorkSlotEvent.PhaseDivergence, Position(), null, GoalPhase.Coding));
    }

    [Fact]
    public void PhaseCtor_PlanUnavailableWithPipelinePhase_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => new WorkSlotException(WorkSlotEvent.PlanUnavailable, Position(), GoalPhase.Coding, GoalPhase.Coding));
    }

    #endregion

    #region (c) Message contract

    public static TheoryData<int> AllEvents =>
    [
        EvDoubleAssignment,
        EvRoleMismatch,
        EvInvalidPhase,
        EvPhaseDivergence,
        EvPlanUnavailable,
    ];

    [Theory]
    [MemberData(nameof(AllEvents))]
    public void Message_ForEveryEvent_IsNonBlankSingleLine(int evCode)
    {
        var ex = Create(Ev(evCode));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain('\r', ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
    }

    private static WorkSlotException Create(WorkSlotEvent ev) => ev switch
    {
        WorkSlotEvent.DoubleAssignment =>
            new WorkSlotException(ev, Position(), "task-existing"),
        WorkSlotEvent.RoleMismatch =>
            new WorkSlotException(ev, Position(), WorkerRole.Coder, WorkerRole.Reviewer),
        WorkSlotEvent.InvalidPhase =>
            new WorkSlotException(ev, Position(), null, GoalPhase.Coding),
        WorkSlotEvent.PhaseDivergence =>
            new WorkSlotException(ev, Position(), GoalPhase.Review, GoalPhase.Coding),
        WorkSlotEvent.PlanUnavailable =>
            new WorkSlotException(ev, Position(), null, GoalPhase.Coding),
        _ => throw new InvalidOperationException($"Unhandled WorkSlotEvent: {ev}"),
    };

    #endregion

    #region (a) Attempt helper

    [Fact]
    public void Allocate_FreshPosition_ReturnsAttemptOneAndRegistersPendingSlot()
    {
        var pipeline = NewPipeline();
        var pos = new WorkSlotPosition(2, GoalPhase.Testing, 3);

        var result = pipeline.AllocateAttemptAndRegisterSlot("t1", pos);

        Assert.Equal("t1", result.TaskId);
        Assert.Same(pos, result.Position);
        Assert.Equal(1, result.Attempt);
        Assert.Equal(new SlotBuildResult("t1", pos, 1), result);

        var view = Assert.Single(pipeline.GetSlotsForTest());
        Assert.Equal(new WorkSlotView(new WorkSlot("t1", pos, 1), WorkSlotState.Pending), view);
    }

    [Fact]
    public void Allocate_002Vector_RecordedDeadTransition_AdvancesAttemptPerPosition()
    {
        var pipeline = NewPipeline();
        var pos = Position();

        // (i) first allocation at the position.
        Assert.Equal(1, pipeline.AllocateAttemptAndRegisterSlot("t1", pos).Attempt);

        // (ii) THE DEAD TRANSITION — a successful allocation leaves the slot LIVE (Pending),
        // so without retiring it the next same-position allocation would be refused.
        Assert.True(pipeline.ForceSlotStateForTest("t1", WorkSlotState.Recorded));

        // (iii) the next allocation at the SAME position gets attempt 2.
        var second = pipeline.AllocateAttemptAndRegisterSlot("t2", pos);
        Assert.Equal(2, second.Attempt);
        Assert.Equal(new SlotBuildResult("t2", pos, 2), second);

        // (iv) the Abandoned variant retires the slot the same way and yields 3.
        Assert.True(pipeline.ForceSlotStateForTest("t2", WorkSlotState.Abandoned));
        Assert.Equal(3, pipeline.AllocateAttemptAndRegisterSlot("t3", pos).Attempt);

        Assert.Equal(
            [
                new WorkSlotView(new WorkSlot("t1", pos, 1), WorkSlotState.Recorded),
                new WorkSlotView(new WorkSlot("t2", pos, 2), WorkSlotState.Abandoned),
                new WorkSlotView(new WorkSlot("t3", pos, 3), WorkSlotState.Pending),
            ],
            Snapshot(pipeline));
    }

    [Fact]
    public void Allocate_DeadTransitionRequired_LiveSlotBlocksSamePositionReallocation()
    {
        var pipeline = NewPipeline();
        var pos = Position();

        pipeline.AllocateAttemptAndRegisterSlot("t1", pos);

        // Without the dead transition the slot is still LIVE (Pending) and the next
        // same-position allocation is refused — this is what step (ii) unblocks.
        Assert.Throws<WorkSlotException>(() => pipeline.AllocateAttemptAndRegisterSlot("t2", pos));
    }

    [Fact]
    public void Allocate_DistinctPositions_CountersAreIndependentPerFullTuple()
    {
        var pipeline = NewPipeline();

        Assert.Equal(1, pipeline.AllocateAttemptAndRegisterSlot("a", new WorkSlotPosition(1, GoalPhase.Coding, 1)).Attempt);
        // Differs by iteration only.
        Assert.Equal(1, pipeline.AllocateAttemptAndRegisterSlot("b", new WorkSlotPosition(2, GoalPhase.Coding, 1)).Attempt);
        // Differs by phase only.
        Assert.Equal(1, pipeline.AllocateAttemptAndRegisterSlot("c", new WorkSlotPosition(1, GoalPhase.Review, 1)).Attempt);
        // Differs by occurrence only.
        Assert.Equal(1, pipeline.AllocateAttemptAndRegisterSlot("d", new WorkSlotPosition(1, GoalPhase.Coding, 2)).Attempt);
    }

    #endregion

    #region (b) Atomicity postconditions

    [Fact]
    public void Allocate_Success_SlotViewAndCounterCommitTogether()
    {
        var pipeline = NewPipeline();
        var pos = Position();

        var first = pipeline.AllocateAttemptAndRegisterSlot("t1", pos);

        // The slot view reflects the allocation exactly.
        Assert.Equal(
            new WorkSlotView(new WorkSlot("t1", pos, first.Attempt), WorkSlotState.Pending),
            Assert.Single(pipeline.GetSlotsForTest()));

        // And the counter advanced with it.
        Assert.True(pipeline.ForceSlotStateForTest("t1", WorkSlotState.Recorded));
        Assert.Equal(first.Attempt + 1, pipeline.AllocateAttemptAndRegisterSlot("t2", pos).Attempt);
    }

    [Fact]
    public void Allocate_RefusedByLivePosition_LeavesCounterUnadvanced()
    {
        var pipeline = NewPipeline();
        var pos = Position();

        pipeline.AllocateAttemptAndRegisterSlot("t1", pos);

        Assert.Throws<WorkSlotException>(() => pipeline.AllocateAttemptAndRegisterSlot("t2", pos));

        // The refusal must not have consumed attempt 2 — the next success takes it.
        Assert.True(pipeline.ForceSlotStateForTest("t1", WorkSlotState.Recorded));
        Assert.Equal(2, pipeline.AllocateAttemptAndRegisterSlot("t3", pos).Attempt);
    }

    [Fact]
    public void Allocate_RefusedByDuplicateTaskId_LeavesCounterUnadvanced()
    {
        var pipeline = NewPipeline();
        var pos = Position();

        pipeline.AllocateAttemptAndRegisterSlot("t1", pos);
        Assert.True(pipeline.ForceSlotStateForTest("t1", WorkSlotState.Recorded));

        Assert.Throws<ArgumentException>(() => pipeline.AllocateAttemptAndRegisterSlot("t1", pos));

        Assert.Equal(2, pipeline.AllocateAttemptAndRegisterSlot("t2", pos).Attempt);
    }

    [Fact]
    public void Allocate_RefusedByInvalidArguments_LeavesCounterUnadvanced()
    {
        var pipeline = NewPipeline();
        var pos = Position();

        Assert.Throws<ArgumentException>(() => pipeline.AllocateAttemptAndRegisterSlot("", pos));
        Assert.Throws<ArgumentException>(() => pipeline.AllocateAttemptAndRegisterSlot("   ", pos));
        Assert.Throws<ArgumentException>(() => pipeline.AllocateAttemptAndRegisterSlot(null!, pos));
        Assert.Throws<ArgumentNullException>(() => pipeline.AllocateAttemptAndRegisterSlot("t1", null!));

        // No refusal consumed an attempt number.
        Assert.Equal(1, pipeline.AllocateAttemptAndRegisterSlot("t1", pos).Attempt);
    }

    [Fact]
    public void Allocate_ConcurrentDistinctPositions_AllCommitUnderTheLock()
    {
        var pipeline = NewPipeline();
        const int workers = 8;
        const int perWorker = 400;

        Parallel.For(0, workers, w =>
        {
            for (var i = 0; i < perWorker; i++)
                pipeline.AllocateAttemptAndRegisterSlot($"t-{w}-{i}", new WorkSlotPosition(w + 1, GoalPhase.Coding, i + 1));
        });

        var slots = pipeline.GetSlotsForTest();
        Assert.Equal(workers * perWorker, slots.Count);
        Assert.All(slots, v => Assert.Equal(1, v.Slot.Attempt));
        Assert.All(slots, v => Assert.Equal(WorkSlotState.Pending, v.State));
    }

    [Fact]
    public void Allocate_ConcurrentSamePosition_ExactlyOneWinner()
    {
        var pipeline = NewPipeline();
        var pos = Position();
        var successes = 0;

        Parallel.For(0, 32, i =>
        {
            try
            {
                pipeline.AllocateAttemptAndRegisterSlot($"t-{i}", pos);
                Interlocked.Increment(ref successes);
            }
            catch (WorkSlotException)
            {
                // Expected: the position was already taken by the winner.
            }
        });

        Assert.Equal(1, successes);
        var view = Assert.Single(pipeline.GetSlotsForTest());
        Assert.Equal(1, view.Slot.Attempt);
    }

    #endregion

    #region (c) Refusals without mutation

    [Theory]
    [InlineData(StPending)]
    [InlineData(StClaimed)]
    [InlineData(StRecorded)]
    [InlineData(StAbandoned)]
    public void Allocate_DuplicateTaskIdInAnyState_ThrowsArgumentExceptionWithoutMutation(int stateCode)
    {
        var pipeline = NewPipeline();
        var seeded = Position(occurrence: 9);
        Assert.True(pipeline.SeedSlotForTest("t1", seeded, 7, St(stateCode)));

        var before = Snapshot(pipeline);

        Assert.Throws<ArgumentException>(
            () => pipeline.AllocateAttemptAndRegisterSlot("t1", Position(occurrence: 42)));

        // The existing slot — including its state — is untouched.
        Assert.Equal(before, Snapshot(pipeline));
        Assert.Equal(
            new WorkSlotView(new WorkSlot("t1", seeded, 7), St(stateCode)),
            Assert.Single(pipeline.GetSlotsForTest()));

        // And the target position's counter never advanced.
        Assert.Equal(1, pipeline.AllocateAttemptAndRegisterSlot("t2", Position(occurrence: 42)).Attempt);
    }

    [Fact]
    public void Allocate_LivePositionFromHelperAllocatedPending_ThrowsDoubleAssignmentWithoutMutation()
    {
        var pipeline = NewPipeline();
        var pos = Position();
        pipeline.AllocateAttemptAndRegisterSlot("existing-task", pos);

        var before = Snapshot(pipeline);

        var ex = Assert.Throws<WorkSlotException>(() => pipeline.AllocateAttemptAndRegisterSlot("newcomer", pos));

        Assert.Equal(WorkSlotEvent.DoubleAssignment, ex.Event);
        Assert.Equal("existing-task", ex.ExistingTaskId);
        Assert.Same(pos, ex.Position);

        // No slot was added and none changed state.
        Assert.Equal(before, Snapshot(pipeline));
        Assert.Single(pipeline.GetSlotsForTest());
    }

    [Fact]
    public void Allocate_LivePositionFromSeededClaimed_ThrowsDoubleAssignmentWithoutMutation()
    {
        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("claimed-task", pos, 4, WorkSlotState.Claimed));

        var before = Snapshot(pipeline);

        var ex = Assert.Throws<WorkSlotException>(() => pipeline.AllocateAttemptAndRegisterSlot("newcomer", pos));

        Assert.Equal(WorkSlotEvent.DoubleAssignment, ex.Event);
        Assert.Equal("claimed-task", ex.ExistingTaskId);

        Assert.Equal(before, Snapshot(pipeline));
        Assert.Single(pipeline.GetSlotsForTest());

        // THE COUNTER PROOF (mirrors the Pending refusal). Retire the claimed slot so the
        // position is free, then allocate there: attempt 1 proves BOTH that seeding never
        // touched the counter AND that the Claimed-branch refusal did not advance it. An
        // implementation that consumes an attempt only on the Claimed branch would yield 2.
        Assert.True(pipeline.ForceSlotStateForTest("claimed-task", WorkSlotState.Recorded));
        Assert.Equal(1, pipeline.AllocateAttemptAndRegisterSlot("after-refusal", pos).Attempt);
    }

    [Theory]
    [InlineData(StRecorded)]
    [InlineData(StAbandoned)]
    public void Allocate_DeadSlotAtSamePosition_IsNotADoubleAssignment(int stateCode)
    {
        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("dead-task", pos, 1, St(stateCode)));

        // Only Pending|Claimed occupy a position; Recorded and Abandoned do not.
        var result = pipeline.AllocateAttemptAndRegisterSlot("newcomer", pos);

        Assert.Equal("newcomer", result.TaskId);
        Assert.Equal(2, pipeline.GetSlotsForTest().Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Allocate_NullOrBlankTaskId_ThrowsArgumentExceptionWithoutMutation(string? taskId)
    {
        var pipeline = NewPipeline();

        Assert.Throws<ArgumentException>(() => pipeline.AllocateAttemptAndRegisterSlot(taskId!, Position()));

        Assert.Empty(pipeline.GetSlotsForTest());
        Assert.Equal(1, pipeline.AllocateAttemptAndRegisterSlot("t1", Position()).Attempt);
    }

    [Fact]
    public void Allocate_NullPosition_ThrowsArgumentNullExceptionWithoutMutation()
    {
        var pipeline = NewPipeline();

        Assert.Throws<ArgumentNullException>(() => pipeline.AllocateAttemptAndRegisterSlot("t1", null!));

        // The task id was NOT registered by the refusal.
        Assert.Empty(pipeline.GetSlotsForTest());
        Assert.Equal(1, pipeline.AllocateAttemptAndRegisterSlot("t1", Position()).Attempt);
    }

    [Fact]
    public void Allocate_NullPosition_IsCheckedBeforeTheDuplicateTaskIdRefusal()
    {
        var pipeline = NewPipeline();
        Assert.True(pipeline.SeedSlotForTest("t1", Position(), 5, WorkSlotState.Recorded));
        var before = Snapshot(pipeline);

        // Both refusals apply: "t1" is a duplicate AND the position is null. The contract
        // orders the position guard first, so ArgumentNullException wins over the
        // ArgumentException the duplicate-task-id rule would raise. This pins the explicit
        // guard — without it the duplicate check would fire first and change the type.
        Assert.Throws<ArgumentNullException>(() => pipeline.AllocateAttemptAndRegisterSlot("t1", null!));

        Assert.Equal(before, Snapshot(pipeline));
    }

    [Fact]
    public void Allocate_BlankTaskId_IsCheckedBeforeTheNullPositionGuard()
    {
        var pipeline = NewPipeline();

        // Both are invalid; the blank task id is checked first, so ArgumentException
        // (not ArgumentNullException) is the observed type.
        var ex = Assert.Throws<ArgumentException>(() => pipeline.AllocateAttemptAndRegisterSlot("   ", null!));
        Assert.IsNotType<ArgumentNullException>(ex);

        Assert.Empty(pipeline.GetSlotsForTest());
    }

    #endregion

    #region (d) The 25-cell state matrix

    /// <summary>Seeds the matrix row's starting state, or nothing for the no-slot column.</summary>
    private static GoalPipeline SeedMatrixRow(int stateCode, WorkSlotPosition pos, string taskId = "t1")
    {
        var pipeline = NewPipeline();
        pipeline.ClearRegistryForTest();
        if (stateCode != StNone)
            Assert.True(pipeline.SeedSlotForTest(taskId, pos, 5, St(stateCode)));
        return pipeline;
    }

    private static void AssertMatrixOutcome(GoalPipeline pipeline, int startCode, int expectedCode, WorkSlotPosition pos)
    {
        if (startCode == StNone)
        {
            Assert.Empty(pipeline.GetSlotsForTest());
            return;
        }

        // The slot identity is preserved; only the state may change.
        Assert.Equal(
            new WorkSlotView(new WorkSlot("t1", pos, 5), St(expectedCode)),
            Assert.Single(pipeline.GetSlotsForTest()));
    }

    [Theory]
    [InlineData(StPending, GuardProceed, StClaimed)]
    [InlineData(StClaimed, GuardProceed, StClaimed)]
    [InlineData(StRecorded, GuardProceed, StRecorded)]
    [InlineData(StAbandoned, GuardAbandoned, StAbandoned)]
    [InlineData(StNone, GuardUnknown, StNone)]
    public void ResolveAndCheckSlot_Matrix(int startCode, int expectedResultCode, int expectedStateCode)
    {
        var pos = Position();
        var pipeline = SeedMatrixRow(startCode, pos);

        var result = pipeline.ResolveAndCheckSlot("t1");

        Assert.Equal((SlotGuardResult)expectedResultCode, result);
        AssertMatrixOutcome(pipeline, startCode, expectedStateCode, pos);
    }

    [Theory]
    [InlineData(StPending, RecNoOp, StPending)]
    [InlineData(StClaimed, RecRecorded, StRecorded)]
    [InlineData(StRecorded, RecNoOp, StRecorded)]
    [InlineData(StAbandoned, RecNoOp, StAbandoned)]
    [InlineData(StNone, RecNoOp, StNone)]
    public void RecordSlot_Matrix(int startCode, int expectedResultCode, int expectedStateCode)
    {
        var pos = Position();
        var pipeline = SeedMatrixRow(startCode, pos);

        var result = pipeline.RecordSlot("t1");

        Assert.Equal((SlotRecordOutcome)expectedResultCode, result);
        AssertMatrixOutcome(pipeline, startCode, expectedStateCode, pos);
    }

    [Theory]
    [InlineData(StPending, true, StAbandoned)]
    [InlineData(StClaimed, false, StClaimed)]
    [InlineData(StRecorded, false, StRecorded)]
    [InlineData(StAbandoned, false, StAbandoned)]
    [InlineData(StNone, false, StNone)]
    public void AbandonSlot_Matrix(int startCode, bool expectedResult, int expectedStateCode)
    {
        var pos = Position();
        var pipeline = SeedMatrixRow(startCode, pos);

        var result = pipeline.AbandonSlot("t1");

        Assert.Equal(expectedResult, result);
        AssertMatrixOutcome(pipeline, startCode, expectedStateCode, pos);
    }

    [Theory]
    [InlineData(StPending, false, StPending)]
    [InlineData(StClaimed, true, StAbandoned)]
    [InlineData(StRecorded, false, StRecorded)]
    [InlineData(StAbandoned, false, StAbandoned)]
    [InlineData(StNone, false, StNone)]
    public void FailSlot_Matrix(int startCode, bool expectedResult, int expectedStateCode)
    {
        var pos = Position();
        var pipeline = SeedMatrixRow(startCode, pos);

        var result = pipeline.FailSlot("t1");

        Assert.Equal(expectedResult, result);
        AssertMatrixOutcome(pipeline, startCode, expectedStateCode, pos);
    }

    [Theory]
    [InlineData(StPending, false, StPending)]
    [InlineData(StClaimed, true, StPending)]
    [InlineData(StRecorded, false, StRecorded)]
    [InlineData(StAbandoned, false, StAbandoned)]
    [InlineData(StNone, false, StNone)]
    public void ReleaseSlot_Matrix(int startCode, bool expectedResult, int expectedStateCode)
    {
        var pos = Position();
        var pipeline = SeedMatrixRow(startCode, pos);

        var result = pipeline.ReleaseSlot("t1");

        Assert.Equal(expectedResult, result);
        AssertMatrixOutcome(pipeline, startCode, expectedStateCode, pos);
    }

    [Fact]
    public void StateApis_UnknownTaskId_DoNotTouchOtherSlots()
    {
        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("other", pos, 5, WorkSlotState.Pending));
        var before = Snapshot(pipeline);

        Assert.Equal(SlotGuardResult.Unknown, pipeline.ResolveAndCheckSlot("missing"));
        Assert.Equal(SlotRecordOutcome.NoOp, pipeline.RecordSlot("missing"));
        Assert.False(pipeline.AbandonSlot("missing"));
        Assert.False(pipeline.FailSlot("missing"));
        Assert.False(pipeline.ReleaseSlot("missing"));

        Assert.Equal(before, Snapshot(pipeline));
    }

    #endregion

    #region (e) Null / blank inputs

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void StateApis_NullOrBlankTaskId_AreSafeNoOps(string? taskId)
    {
        var pipeline = NewPipeline();
        var pos = Position();
        // A live slot is present so a mutation would be observable.
        Assert.True(pipeline.SeedSlotForTest("t1", pos, 5, WorkSlotState.Claimed));
        var before = Snapshot(pipeline);

        Assert.Equal(SlotGuardResult.Unknown, pipeline.ResolveAndCheckSlot(taskId!));
        Assert.Equal(SlotRecordOutcome.NoOp, pipeline.RecordSlot(taskId!));
        Assert.False(pipeline.AbandonSlot(taskId!));
        Assert.False(pipeline.FailSlot(taskId!));
        Assert.False(pipeline.ReleaseSlot(taskId!));

        Assert.Equal(before, Snapshot(pipeline));
    }

    #endregion

    #region (f) Seam contracts

    /// <summary>
    /// The snapshot returned by <see cref="GoalPipeline.GetSlotsForTest"/> must be a detached
    /// copy, NOT a live view over the registry's own storage.
    /// <para>
    /// The proof is independent of the returned collection's concrete type: the registry is
    /// mutated AFTER the first snapshot is captured (a state transition plus a brand-new slot),
    /// and the first snapshot must still show the ORIGINAL entries. A read-only or custom wrapper
    /// over shared live storage would reflect those later mutations and fail here, even though it
    /// could satisfy a reference-inequality check.
    /// </para>
    /// </summary>
    [Fact]
    public void GetSlotsForTest_DoesNotAliasInternalStorage()
    {
        var pipeline = NewPipeline();
        var posA = Position(occurrence: 1);
        var posB = Position(occurrence: 2);
        Assert.True(pipeline.SeedSlotForTest("a", posA, 1, WorkSlotState.Pending));
        Assert.True(pipeline.SeedSlotForTest("b", posB, 2, WorkSlotState.Claimed));

        var expectedOriginal = new HashSet<WorkSlotView>
        {
            new(new WorkSlot("a", posA, 1), WorkSlotState.Pending),
            new(new WorkSlot("b", posB, 2), WorkSlotState.Claimed),
        };

        var first = pipeline.GetSlotsForTest();
        Assert.Equal(2, first.Count);
        Assert.Equal(expectedOriginal, new HashSet<WorkSlotView>(first));

        // MUTATE THE REGISTRY AFTER capturing `first`:
        //   (1) transition an existing slot's state,  (2) add an entirely new slot.
        Assert.Equal(SlotRecordOutcome.Recorded, pipeline.RecordSlot("b"));  // Claimed → Recorded
        pipeline.AllocateAttemptAndRegisterSlot("c", Position(occurrence: 3));

        // THE ALIASING PROOF — type-independent: the first snapshot is frozen at capture time.
        Assert.Equal(2, first.Count);
        Assert.Equal(expectedOriginal, new HashSet<WorkSlotView>(first));
        Assert.Contains(new WorkSlotView(new WorkSlot("b", posB, 2), WorkSlotState.Claimed), first);
        Assert.DoesNotContain(new WorkSlotView(new WorkSlot("b", posB, 2), WorkSlotState.Recorded), first);
        Assert.DoesNotContain(first, v => v.Slot.TaskId == "c");

        // Freshness: a later read is a NEW collection reflecting the CURRENT registry.
        var second = pipeline.GetSlotsForTest();
        Assert.NotSame(first, second);
        Assert.Equal(3, second.Count);
        Assert.Equal(
            [
                new WorkSlotView(new WorkSlot("a", posA, 1), WorkSlotState.Pending),
                new WorkSlotView(new WorkSlot("b", posB, 2), WorkSlotState.Recorded),
                new WorkSlotView(new WorkSlot("c", Position(occurrence: 3), 1), WorkSlotState.Pending),
            ],
            new HashSet<WorkSlotView>(second));
    }

    [Fact]
    public void GetSlotsForTest_IsOrderIndependentSetOfViews()
    {
        var pipeline = NewPipeline();
        Assert.True(pipeline.SeedSlotForTest("z", Position(occurrence: 3), 3, WorkSlotState.Abandoned));
        Assert.True(pipeline.SeedSlotForTest("a", Position(occurrence: 1), 1, WorkSlotState.Pending));
        Assert.True(pipeline.SeedSlotForTest("m", Position(occurrence: 2), 2, WorkSlotState.Recorded));

        Assert.Equal(
            [
                new WorkSlotView(new WorkSlot("a", Position(occurrence: 1), 1), WorkSlotState.Pending),
                new WorkSlotView(new WorkSlot("m", Position(occurrence: 2), 2), WorkSlotState.Recorded),
                new WorkSlotView(new WorkSlot("z", Position(occurrence: 3), 3), WorkSlotState.Abandoned),
            ],
            Snapshot(pipeline));
    }

    [Theory]
    [InlineData(StPending, StPending)]
    [InlineData(StPending, StClaimed)]
    [InlineData(StPending, StRecorded)]
    [InlineData(StPending, StAbandoned)]
    [InlineData(StClaimed, StPending)]
    [InlineData(StClaimed, StClaimed)]
    [InlineData(StClaimed, StRecorded)]
    [InlineData(StClaimed, StAbandoned)]
    [InlineData(StRecorded, StPending)]
    [InlineData(StRecorded, StClaimed)]
    [InlineData(StRecorded, StRecorded)]
    [InlineData(StRecorded, StAbandoned)]
    [InlineData(StAbandoned, StPending)]
    [InlineData(StAbandoned, StClaimed)]
    [InlineData(StAbandoned, StRecorded)]
    [InlineData(StAbandoned, StAbandoned)]
    public void ForceSlotStateForTest_ExistingSlot_ForcesEveryStateFromEveryState(int fromCode, int toCode)
    {
        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("t1", pos, 5, St(fromCode)));

        Assert.True(pipeline.ForceSlotStateForTest("t1", St(toCode)));

        Assert.Equal(
            new WorkSlotView(new WorkSlot("t1", pos, 5), St(toCode)),
            Assert.Single(pipeline.GetSlotsForTest()));
    }

    [Fact]
    public void ForceSlotStateForTest_AbsentTaskId_ReturnsFalseWithoutAddingASlot()
    {
        var pipeline = NewPipeline();

        Assert.False(pipeline.ForceSlotStateForTest("missing", WorkSlotState.Claimed));

        Assert.Empty(pipeline.GetSlotsForTest());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ForceSlotStateForTest_BlankTaskId_ReturnsFalseWithoutMutation(string? taskId)
    {
        var pipeline = NewPipeline();
        Assert.True(pipeline.SeedSlotForTest("t1", Position(), 5, WorkSlotState.Pending));
        var before = Snapshot(pipeline);

        Assert.False(pipeline.ForceSlotStateForTest(taskId!, WorkSlotState.Recorded));

        Assert.Equal(before, Snapshot(pipeline));
    }

    [Fact]
    public void ForceSlotStateForTest_UndefinedState_ThrowsArgumentException()
    {
        var pipeline = NewPipeline();
        Assert.True(pipeline.SeedSlotForTest("t1", Position(), 5, WorkSlotState.Pending));
        var before = Snapshot(pipeline);

        Assert.Throws<ArgumentException>(() => pipeline.ForceSlotStateForTest("t1", (WorkSlotState)99));

        Assert.Equal(before, Snapshot(pipeline));
    }

    [Theory]
    [InlineData(StPending)]
    [InlineData(StClaimed)]
    [InlineData(StRecorded)]
    [InlineData(StAbandoned)]
    public void SeedSlotForTest_ValidNoConflict_RegistersGivenValues(int stateCode)
    {
        var pipeline = NewPipeline();
        var pos = new WorkSlotPosition(4, GoalPhase.DocWriting, 6);

        Assert.True(pipeline.SeedSlotForTest("t1", pos, 17, St(stateCode)));

        Assert.Equal(
            new WorkSlotView(new WorkSlot("t1", pos, 17), St(stateCode)),
            Assert.Single(pipeline.GetSlotsForTest()));
    }

    [Fact]
    public void SeedSlotForTest_TaskIdConflict_ReturnsFalseWithoutOverwriting()
    {
        var pipeline = NewPipeline();
        var original = Position(occurrence: 1);
        Assert.True(pipeline.SeedSlotForTest("t1", original, 3, WorkSlotState.Claimed));

        Assert.False(pipeline.SeedSlotForTest("t1", Position(occurrence: 2), 9, WorkSlotState.Abandoned));

        Assert.Equal(
            new WorkSlotView(new WorkSlot("t1", original, 3), WorkSlotState.Claimed),
            Assert.Single(pipeline.GetSlotsForTest()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SeedSlotForTest_BlankOrNullTaskId_ReturnsFalse(string? taskId)
    {
        var pipeline = NewPipeline();

        Assert.False(pipeline.SeedSlotForTest(taskId!, Position(), 1, WorkSlotState.Pending));

        Assert.Empty(pipeline.GetSlotsForTest());
    }

    [Fact]
    public void SeedSlotForTest_NullPosition_ReturnsFalse()
    {
        var pipeline = NewPipeline();

        Assert.False(pipeline.SeedSlotForTest("t1", null!, 1, WorkSlotState.Pending));

        Assert.Empty(pipeline.GetSlotsForTest());
    }

    [Fact]
    public void SeedSlotForTest_UndefinedState_ThrowsArgumentException()
    {
        var pipeline = NewPipeline();

        Assert.Throws<ArgumentException>(() => pipeline.SeedSlotForTest("t1", Position(), 1, (WorkSlotState)99));

        Assert.Empty(pipeline.GetSlotsForTest());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-7)]
    public void SeedSlotForTest_NegativeAttempt_ThrowsArgumentException(int attempt)
    {
        var pipeline = NewPipeline();

        Assert.Throws<ArgumentException>(
            () => pipeline.SeedSlotForTest("t1", Position(), attempt, WorkSlotState.Pending));

        Assert.Empty(pipeline.GetSlotsForTest());
    }

    [Fact]
    public void SeedSlotForTest_ZeroAttempt_IsAllowed()
    {
        var pipeline = NewPipeline();

        Assert.True(pipeline.SeedSlotForTest("t1", Position(), 0, WorkSlotState.Pending));

        Assert.Equal(0, Assert.Single(pipeline.GetSlotsForTest()).Slot.Attempt);
    }

    [Fact]
    public void SeedSlotForTest_DoesNotTouchDispatchCounters()
    {
        var pipeline = NewPipeline();
        var pos = Position();

        // Seed a (dead) slot at P with a high attempt number.
        Assert.True(pipeline.SeedSlotForTest("seeded", pos, 12, WorkSlotState.Recorded));

        // The helper's counter for P is still untouched, so the next allocation is attempt 1.
        Assert.Equal(1, pipeline.AllocateAttemptAndRegisterSlot("t1", pos).Attempt);
    }

    [Fact]
    public void ClearRegistryForTest_ClearsSlotsAndResetsCountersThroughTheHelper()
    {
        var pipeline = NewPipeline();
        var pos = Position();

        // Advance the counter for `pos` to 3 through the helper.
        pipeline.AllocateAttemptAndRegisterSlot("t1", pos);
        Assert.True(pipeline.ForceSlotStateForTest("t1", WorkSlotState.Recorded));
        pipeline.AllocateAttemptAndRegisterSlot("t2", pos);
        Assert.True(pipeline.ForceSlotStateForTest("t2", WorkSlotState.Recorded));
        Assert.Equal(3, pipeline.AllocateAttemptAndRegisterSlot("t3", pos).Attempt);

        pipeline.ClearRegistryForTest();

        Assert.Empty(pipeline.GetSlotsForTest());
        // The counter reset is proven through the HELPER: a re-allocation starts at 1 again.
        Assert.Equal(1, pipeline.AllocateAttemptAndRegisterSlot("t4", pos).Attempt);
    }

    [Fact]
    public void ClearRegistryForTest_OnEmptyRegistry_IsSafe()
    {
        var pipeline = NewPipeline();

        pipeline.ClearRegistryForTest();
        pipeline.ClearRegistryForTest();

        Assert.Empty(pipeline.GetSlotsForTest());
    }

    #endregion

    #region (g) AbandonPendingSlots

    [Fact]
    public void AbandonPendingSlots_AbandonsPendingAndExemptsClaimed()
    {
        var pipeline = NewPipeline();
        var pendingPos = Position(occurrence: 1);
        var claimedPos = Position(occurrence: 2);
        Assert.True(pipeline.SeedSlotForTest("pending", pendingPos, 1, WorkSlotState.Pending));
        Assert.True(pipeline.SeedSlotForTest("claimed", claimedPos, 2, WorkSlotState.Claimed));

        pipeline.AbandonPendingSlots();

        Assert.Equal(
            [
                new WorkSlotView(new WorkSlot("pending", pendingPos, 1), WorkSlotState.Abandoned),
                // THE EXEMPTION: work already in flight keeps its slot.
                new WorkSlotView(new WorkSlot("claimed", claimedPos, 2), WorkSlotState.Claimed),
            ],
            Snapshot(pipeline));
    }

    [Fact]
    public void AbandonPendingSlots_LeavesRecordedAndAbandonedUntouched()
    {
        var pipeline = NewPipeline();
        var recordedPos = Position(occurrence: 3);
        var abandonedPos = Position(occurrence: 4);
        Assert.True(pipeline.SeedSlotForTest("recorded", recordedPos, 3, WorkSlotState.Recorded));
        Assert.True(pipeline.SeedSlotForTest("abandoned", abandonedPos, 4, WorkSlotState.Abandoned));

        pipeline.AbandonPendingSlots();

        Assert.Equal(
            [
                new WorkSlotView(new WorkSlot("recorded", recordedPos, 3), WorkSlotState.Recorded),
                new WorkSlotView(new WorkSlot("abandoned", abandonedPos, 4), WorkSlotState.Abandoned),
            ],
            Snapshot(pipeline));
    }

    [Fact]
    public void AbandonPendingSlots_AbandonsEveryPendingSlot()
    {
        var pipeline = NewPipeline();
        for (var i = 1; i <= 4; i++)
            Assert.True(pipeline.SeedSlotForTest($"p{i}", Position(occurrence: i), i, WorkSlotState.Pending));

        pipeline.AbandonPendingSlots();

        Assert.All(pipeline.GetSlotsForTest(), v => Assert.Equal(WorkSlotState.Abandoned, v.State));
        Assert.Equal(4, pipeline.GetSlotsForTest().Count);
    }

    [Fact]
    public void AbandonPendingSlots_OnEmptyRegistry_IsSafe()
    {
        var pipeline = NewPipeline();

        pipeline.AbandonPendingSlots();

        Assert.Empty(pipeline.GetSlotsForTest());
    }

    [Fact]
    public void AbandonPendingSlots_DoesNotResetDispatchCounters()
    {
        var pipeline = NewPipeline();
        var pos = Position();
        pipeline.AllocateAttemptAndRegisterSlot("t1", pos);

        pipeline.AbandonPendingSlots();

        // The slot is retired, so the position is free — but the counter kept advancing.
        Assert.Equal(2, pipeline.AllocateAttemptAndRegisterSlot("t2", pos).Attempt);
    }

    #endregion

    #region (h) Concurrency of the state APIs

    /// <summary>Generous upper bound for every wait/join — a hang is a failure, not a slow test.</summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Grace window granted to the worker thread AFTER it signalled its call attempt. A correctly
    /// locked registry method cannot complete inside it (the test thread still holds
    /// <c>_lock</c>); an UNSYNCHRONIZED one would comfortably finish, which is what makes the
    /// blocked-thread proof discriminating rather than vacuous.
    /// </summary>
    private static readonly TimeSpan BlockedGrace = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Reflects out the pipeline's private <c>_lock</c> monitor object. No production test seam
    /// exists for this, so the test reaches for the field directly — the precedent is
    /// <c>ComposerFacadeGateTests</c>, which reflects on private lock fields the same way.
    /// </summary>
    private static object GetPipelineLock(GoalPipeline pipeline) =>
        typeof(GoalPipeline)
            .GetField("_lock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(pipeline)
        ?? throw new InvalidOperationException("GoalPipeline no longer has a private '_lock' field.");

    /// <summary>
    /// A BEHAVIOURAL companion to the emitted-IL lock-structure backstop below (region (i)).
    /// <para>
    /// AUTHORITY NOTE: the IL test <see cref="RegistryEntryPoint_RunsWhollyUnderTheLock"/> is the
    /// deterministic authority for the lock-structure criterion — it inspects the compiled
    /// artifact, so removing or narrowing a <c>lock</c> fails it on 100% of runs. This test does
    /// NOT carry that burden: a worker can only signal that it is ABOUT to call the method, never
    /// that it reached <c>Monitor.Enter</c>, so its grace window is a timing assumption, not a
    /// proof. What it adds is the OBSERVABLE SEMANTICS the IL cannot show — that a caller
    /// genuinely blocks while the lock is held and then sees the complete after-state.
    /// </para>
    /// <para>
    /// The test thread takes the pipeline's own <c>_lock</c> monitor directly, then starts a
    /// worker that signals its attempt immediately before calling <c>ResolveAndCheckSlot</c> and
    /// therefore parks on that same lock. Only the two achievable facts are asserted:
    /// </para>
    /// <list type="number">
    ///   <item>the call did not complete while the lock was held;</item>
    ///   <item>once released, the worker reports the COMPLETE after-state (Proceed, with the slot
    ///     transitioned Pending → Claimed) — never a partial mixture.</item>
    /// </list>
    /// <para>
    /// Nothing is asserted about completion ORDERING after the release: the lock does not provide
    /// that fact. Every wait is BOUNDED, so a regression fails loudly instead of hanging.
    /// </para>
    /// </summary>
    [Fact]
    public void ResolveAndCheckSlot_WhileLockHeld_IsBlocked_ThenCompletesWithFullAfterState()
    {
        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("t1", pos, 5, WorkSlotState.Pending));

        var monitor = GetPipelineLock(pipeline);

        using var callAttempted = new ManualResetEventSlim(false);
        using var callCompleted = new ManualResetEventSlim(false);
        SlotGuardResult? workerResult = null;
        bool attemptObserved;
        bool completedWhileHeld;

        var worker = new Thread(() =>
        {
            callAttempted.Set();                                  // signalled IMMEDIATELY BEFORE the call…
            var guard = pipeline.ResolveAndCheckSlot("t1");       // …which parks on the pipeline lock
            // SlotGuardResult is a value type, so no Volatile.Write here; the Join below (and the
            // event Set/Wait pair) establishes the happens-before for the read.
            workerResult = guard;
            callCompleted.Set();
        })
        {
            IsBackground = true,
            Name = "work-slot-blocked-resolver",
        };

        Monitor.Enter(monitor);
        try
        {
            // Started from INSIDE the held region, so the lock is provably already held when the
            // worker makes its attempt — there is no start-order race to lose.
            worker.Start();

            // Bounded, timeout-ONLY waits. A cancellable wait would let an external cancellation
            // cut the grace window short and turn fact (i) into a false pass, so the wall-clock
            // bound is deliberate here.
#pragma warning disable xUnit1051 // Timeout-only waits are intentional: the fixed bound IS the proof
            // Wait on the ATTEMPT signal only. Waiting on completion here would be a
            // self-inflicted deadlock — this thread is the very thing holding the lock.
            attemptObserved = callAttempted.Wait(WaitTimeout);

            // (i) BLOCKED-WHILE-HELD. Under a correct lock this wait MUST time out.
            completedWhileHeld = callCompleted.Wait(BlockedGrace);
#pragma warning restore xUnit1051
        }
        finally
        {
            Monitor.Exit(monitor);
        }

        Assert.True(worker.Join(WaitTimeout), "The blocked resolve never completed after the lock was released.");

        Assert.True(attemptObserved, "The worker thread never signalled its call attempt.");
        Assert.False(
            completedWhileHeld,
            "ResolveAndCheckSlot completed while the pipeline lock was held — it is not running under the lock.");

        // (ii) Once unblocked: the COMPLETE after-state, never a partial one.
        // Read AFTER Join, which establishes the happens-before with the worker's write.
        Assert.Equal(SlotGuardResult.Proceed, workerResult);
        Assert.Equal(
            new WorkSlotView(new WorkSlot("t1", pos, 5), WorkSlotState.Claimed),
            Assert.Single(pipeline.GetSlotsForTest()));
    }

    /// <summary>
    /// The same BEHAVIOURAL companion applied to
    /// <see cref="GoalPipeline.AllocateAttemptAndRegisterSlot"/>: it demonstrates the observable
    /// blocked/after-state semantics (the committed slot AND the advanced counter), while
    /// <see cref="RegistryEntryPoint_RunsWhollyUnderTheLock"/> remains the deterministic authority
    /// for the whole-operation lock structure.
    /// </summary>
    [Fact]
    public void AllocateAttemptAndRegisterSlot_WhileLockHeld_IsBlocked_ThenCommitsSlotAndCounter()
    {
        var pipeline = NewPipeline();
        var pos = Position();

        var monitor = GetPipelineLock(pipeline);

        using var callAttempted = new ManualResetEventSlim(false);
        using var callCompleted = new ManualResetEventSlim(false);
        SlotBuildResult? workerResult = null;
        bool attemptObserved;
        bool completedWhileHeld;

        var worker = new Thread(() =>
        {
            callAttempted.Set();
            var built = pipeline.AllocateAttemptAndRegisterSlot("t1", pos);
            Volatile.Write(ref workerResult, built);
            callCompleted.Set();
        })
        {
            IsBackground = true,
            Name = "work-slot-blocked-allocator",
        };

        Monitor.Enter(monitor);
        try
        {
            worker.Start();
#pragma warning disable xUnit1051 // Timeout-only waits are intentional: the fixed bound IS the proof
            attemptObserved = callAttempted.Wait(WaitTimeout);
            completedWhileHeld = callCompleted.Wait(BlockedGrace);
#pragma warning restore xUnit1051
        }
        finally
        {
            Monitor.Exit(monitor);
        }

        Assert.True(worker.Join(WaitTimeout), "The blocked allocation never completed after the lock was released.");

        Assert.True(attemptObserved, "The worker thread never signalled its call attempt.");
        Assert.False(
            completedWhileHeld,
            "AllocateAttemptAndRegisterSlot completed while the pipeline lock was held — it is not running under the lock.");

        // The COMPLETE after-state: the slot committed AND the counter advanced with it.
        Assert.Equal(new SlotBuildResult("t1", pos, 1), Volatile.Read(ref workerResult));
        Assert.Equal(
            new WorkSlotView(new WorkSlot("t1", pos, 1), WorkSlotState.Pending),
            Assert.Single(pipeline.GetSlotsForTest()));
    }

    #endregion

    #region (i) The deterministic emitted-IL lock-structure backstop

    // ══════════════════════════════════════════════════════════════════════════════════
    //  THE LOAD-BEARING LOCK PROOF.
    //
    //  The blocked-while-held tests above are BEHAVIOURAL: a worker can only signal that
    //  it is ABOUT to call a method, never that it actually reached `Monitor.Enter` or
    //  any protected region, so their grace window is a timing assumption. Production is
    //  frozen and exposes no in-region test seam, so the second route is taken here:
    //  deterministically inspect the COMPILED ARTIFACT.
    //
    //  The IL of a built assembly is fixed at test time — there is no scheduler, no
    //  timing, and no flakiness anywhere in this region. Each assertion below is a pure
    //  function of the emitted bytes, so a structural regression fails 100% of runs.
    //
    //  Empirically discovered from THIS build (all 11 entry points):
    //    • the C# `lock` statement emits `Monitor.Enter(object, ref bool)` — the Roslyn
    //      two-argument form — NOT the single-argument `Monitor.Enter(object)`;
    //    • release is `Monitor.Exit(object)`, emitted inside a Finally handler;
    //    • the guarded accesses are the `Dictionary<,>` member calls on `_slots` /
    //      `_dispatchAttempts` (ContainsKey, TryGetValue, set_Item, get_Item, get_Keys,
    //      get_Count, Clear, GetEnumerator) plus the nested Enumerator's members.
    //
    //  NOTE ON DECODING: a naive "scan for 0x28/0x6F then read 4 bytes" walk produces
    //  FALSE POSITIVES, because those byte values also occur inside other instructions'
    //  operands. This region therefore walks the instruction stream properly using a
    //  real opcode table, so every reported call site is a genuine instruction boundary.
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The single source of truth: every registry entry point that must run wholly under
    /// <c>_lock</c>. Both the structural Theory and the drift guard read this list.
    /// </summary>
    private static readonly string[] LockedRegistryMethodNames =
    [
        "AllocateAttemptAndRegisterSlot",
        "ResolveAndCheckSlot",
        "RecordSlot",
        "AbandonSlot",
        "FailSlot",
        "ReleaseSlot",
        "AbandonPendingSlots",
        "GetSlotsForTest",
        "ForceSlotStateForTest",
        "ClearRegistryForTest",
        "SeedSlotForTest",
    ];

    /// <summary>Theory feed of <see cref="LockedRegistryMethodNames"/> (strings only — the
    /// registry types are internal and cannot appear in a public test signature).</summary>
    public static TheoryData<string> LockedRegistryMethods
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in LockedRegistryMethodNames)
                data.Add(name);
            return data;
        }
    }

    /// <summary>A single decoded call-ish instruction: its IL offset and resolved target.</summary>
    private sealed record CallSite(int Offset, MethodBase Target);

    /// <summary>
    /// One fully decoded IL instruction: its offset, opcode, the offset where its operand starts,
    /// and the offset of the following instruction. This is the model the provenance and
    /// control-flow analyses below are built on.
    /// </summary>
    private sealed record Instruction(int Offset, OpCode OpCode, int OperandOffset, int NextOffset);

    /// <summary>The fully decoded body of a method: its instruction stream and raw IL bytes.</summary>
    private sealed record DecodedBody(MethodInfo Method, byte[] Il, List<Instruction> Instructions)
    {
        /// <summary>Index of the instruction starting at <paramref name="offset"/>.</summary>
        public int IndexOf(int offset)
        {
            var index = Instructions.FindIndex(i => i.Offset == offset);
            return index >= 0
                ? index
                : throw new Xunit.Sdk.XunitException($"No instruction begins at IL offset {offset}.");
        }
    }

    /// <summary>Opcode lookup table built once from <see cref="OpCodes"/> reflection.</summary>
    private static readonly Dictionary<short, OpCode> OpCodeByValue = BuildOpCodeTable();

    private static Dictionary<short, OpCode> BuildOpCodeTable()
    {
        var table = new Dictionary<short, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(OpCode))
                continue;
            var opCode = (OpCode)field.GetValue(null)!;
            table[opCode.Value] = opCode;
        }
        return table;
    }

    private static MethodInfo RegistryMethod(string name) =>
        typeof(GoalPipeline).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new Xunit.Sdk.XunitException($"No method '{name}' on GoalPipeline.");

    /// <summary>
    /// Walks the method's emitted IL instruction-by-instruction (using the real opcode table,
    /// so operand bytes are never mistaken for opcodes) and returns every <c>call</c> /
    /// <c>callvirt</c> site with its offset and resolved target.
    /// </summary>
    private static List<CallSite> DecodeCallSites(MethodInfo method)
    {
        var body = method.GetMethodBody()
            ?? throw new Xunit.Sdk.XunitException($"'{method.Name}' has no method body.");
        var il = body.GetILAsByteArray()
            ?? throw new Xunit.Sdk.XunitException($"'{method.Name}' exposes no IL.");
        var module = method.Module;
        var genericTypeArgs = method.DeclaringType?.GetGenericArguments();
        var genericMethodArgs = method.GetGenericArguments();

        var sites = new List<CallSite>();
        var pos = 0;
        while (pos < il.Length)
        {
            var start = pos;
            short value;
            if (il[pos] == 0xFE)
            {
                value = (short)(0xFE00 | il[pos + 1]);
                pos += 2;
            }
            else
            {
                value = il[pos];
                pos += 1;
            }

            if (!OpCodeByValue.TryGetValue(value, out var opCode))
                throw new Xunit.Sdk.XunitException($"Unknown opcode 0x{value:X} at offset {start} in '{method.Name}'.");

            var operandSize = OperandSize(opCode, il, pos);

            if (opCode == OpCodes.Call || opCode == OpCodes.Callvirt)
            {
                var token = BitConverter.ToInt32(il, pos);
                MethodBase? target = null;
                try
                {
                    target = module.ResolveMethod(token, genericTypeArgs, genericMethodArgs);
                }
                catch (ArgumentException)
                {
                    // Not resolvable in this context; not a call we assert on.
                }

                if (target is not null)
                    sites.Add(new CallSite(start, target));
            }

            pos += operandSize;
        }

        return sites;
    }

    private static int OperandSize(OpCode opCode, byte[] il, int operandStart) => opCode.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
            or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
            or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, operandStart)),
        _ => throw new Xunit.Sdk.XunitException($"Unhandled operand type {opCode.OperandType}."),
    };

    private static bool IsMonitor(MethodBase m, string name) =>
        m.DeclaringType == typeof(Monitor) && m.Name == name;

    /// <summary>
    /// A guarded access: any call on the registry's backing <see cref="Dictionary{TKey,TValue}"/>
    /// storage (or its nested enumerator / key-collection), i.e. a read or write of
    /// <c>_slots</c> or <c>_dispatchAttempts</c> that MUST sit inside the lock.
    /// </summary>
    private static bool IsGuardedAccess(MethodBase m)
    {
        var declaring = m.DeclaringType;
        if (declaring is null)
            return false;

        // Walk out of nested types (Dictionary<,>.Enumerator, .KeyCollection) to the owner.
        for (var t = declaring; t is not null; t = t.DeclaringType)
        {
            if (!t.IsGenericType)
                continue;
            if (t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                return true;
        }

        return false;
    }

    // ── Full instruction decoding, operand provenance, and control flow ────────────────

    /// <summary>Decodes the complete instruction stream (offsets, opcodes, operand positions).</summary>
    private static DecodedBody DecodeBody(MethodInfo method)
    {
        var body = method.GetMethodBody()
            ?? throw new Xunit.Sdk.XunitException($"'{method.Name}' has no method body.");
        var il = body.GetILAsByteArray()
            ?? throw new Xunit.Sdk.XunitException($"'{method.Name}' exposes no IL.");

        var instructions = new List<Instruction>();
        var pos = 0;
        while (pos < il.Length)
        {
            var start = pos;
            short value;
            if (il[pos] == 0xFE)
            {
                value = (short)(0xFE00 | il[pos + 1]);
                pos += 2;
            }
            else
            {
                value = il[pos];
                pos += 1;
            }

            if (!OpCodeByValue.TryGetValue(value, out var opCode))
                throw new Xunit.Sdk.XunitException($"Unknown opcode 0x{value:X} at offset {start} in '{method.Name}'.");

            var operandOffset = pos;
            pos += OperandSize(opCode, il, pos);
            instructions.Add(new Instruction(start, opCode, operandOffset, pos));
        }

        return new DecodedBody(method, il, instructions);
    }

    /// <summary>Local-variable slot referenced by a ldloc/stloc/ldloca form, or -1.</summary>
    private static int LocalSlot(DecodedBody body, Instruction instruction)
    {
        var op = instruction.OpCode;
        if (op == OpCodes.Ldloc_0 || op == OpCodes.Stloc_0) return 0;
        if (op == OpCodes.Ldloc_1 || op == OpCodes.Stloc_1) return 1;
        if (op == OpCodes.Ldloc_2 || op == OpCodes.Stloc_2) return 2;
        if (op == OpCodes.Ldloc_3 || op == OpCodes.Stloc_3) return 3;
        if (op == OpCodes.Ldloc_S || op == OpCodes.Stloc_S || op == OpCodes.Ldloca_S)
            return body.Il[instruction.OperandOffset];
        if (op == OpCodes.Ldloc || op == OpCodes.Stloc || op == OpCodes.Ldloca)
            return BitConverter.ToInt16(body.Il, instruction.OperandOffset);
        return -1;
    }

    private static bool IsLoadLocal(OpCode op) =>
        op == OpCodes.Ldloc_0 || op == OpCodes.Ldloc_1 || op == OpCodes.Ldloc_2
        || op == OpCodes.Ldloc_3 || op == OpCodes.Ldloc_S || op == OpCodes.Ldloc;

    private static bool IsStoreLocal(OpCode op) =>
        op == OpCodes.Stloc_0 || op == OpCodes.Stloc_1 || op == OpCodes.Stloc_2
        || op == OpCodes.Stloc_3 || op == OpCodes.Stloc_S || op == OpCodes.Stloc;

    /// <summary>
    /// Resolves the PROVENANCE of the object argument feeding a <c>Monitor.Enter</c>/<c>Exit</c>
    /// call, and returns the metadata token of the field it ultimately came from.
    /// <para>
    /// This build lowers <c>lock (_lock)</c> to
    /// <c>ldarg.0; ldfld _lock; stloc.N</c> … <c>ldloc.N; [ldloca.s taken;] call Monitor.X</c>.
    /// The analysis therefore: (1) takes the instruction supplying the object argument — the one
    /// immediately before the call for <c>Exit</c>, two before for the two-argument <c>Enter</c>
    /// (whose last argument is the <c>ldloca.s</c> of the <c>bool taken</c> flag); (2) requires it
    /// to be a <c>ldloc</c>; (3) finds EVERY store to that slot in the whole method and requires
    /// each one to be fed by a <c>ldfld</c> — so a second, different assignment cannot hide; and
    /// (4) requires all those stores to name the SAME field.
    /// </para>
    /// <para>
    /// Anything else — a <c>newobj</c> (<c>lock (new object())</c>), a direct <c>ldfld</c> of a
    /// different field (<c>lock (_slots)</c>), an <c>ldarg</c>, or a literal — either fails the
    /// ldloc requirement or yields a different field token, so the assertion rejects it.
    /// </para>
    /// </summary>
    private static int ResolveMonitorArgumentFieldToken(DecodedBody body, int callIndex, bool isEnter)
    {
        var method = body.Method;
        var argIndex = callIndex - (isEnter ? 2 : 1);
        Assert.True(
            argIndex >= 0,
            $"'{method.Name}': the Monitor call at index {callIndex} has no preceding argument instruction.");

        var argInstruction = body.Instructions[argIndex];
        Assert.True(
            IsLoadLocal(argInstruction.OpCode),
            $"'{method.Name}': the object passed to Monitor.{(isEnter ? "Enter" : "Exit")} at IL offset " +
            $"{body.Instructions[callIndex].Offset} comes from '{argInstruction.OpCode.Name}', not a local " +
            "holding the lock field — the monitor is not GoalPipeline._lock.");

        var slot = LocalSlot(body, argInstruction);
        Assert.True(slot >= 0, $"'{method.Name}': could not resolve the local slot for the monitor argument.");

        // EVERY store to the slot must load the SAME field — a second assignment cannot hide.
        var tokens = new HashSet<int>();
        for (var i = 0; i < body.Instructions.Count; i++)
        {
            var instruction = body.Instructions[i];
            if (!IsStoreLocal(instruction.OpCode) || LocalSlot(body, instruction) != slot)
                continue;

            Assert.True(
                i > 0 && body.Instructions[i - 1].OpCode == OpCodes.Ldfld,
                $"'{method.Name}': local {slot} (the monitor object) is assigned at IL offset " +
                $"{instruction.Offset} from '{(i > 0 ? body.Instructions[i - 1].OpCode.Name : "<nothing>")}' " +
                "rather than a field load — the monitor is not GoalPipeline._lock.");

            tokens.Add(BitConverter.ToInt32(body.Il, body.Instructions[i - 1].OperandOffset));
        }

        Assert.True(
            tokens.Count == 1,
            $"'{method.Name}': the monitor local {slot} is fed by {tokens.Count} distinct field sources — " +
            "the lock object is not a single, fixed field.");

        return tokens.Single();
    }

    /// <summary>Branch targets of an instruction (empty for non-branching instructions).</summary>
    private static IEnumerable<int> BranchTargets(DecodedBody body, Instruction instruction)
    {
        switch (instruction.OpCode.OperandType)
        {
            case OperandType.ShortInlineBrTarget:
                yield return instruction.NextOffset + (sbyte)body.Il[instruction.OperandOffset];
                break;
            case OperandType.InlineBrTarget:
                yield return instruction.NextOffset + BitConverter.ToInt32(body.Il, instruction.OperandOffset);
                break;
            case OperandType.InlineSwitch:
                var count = BitConverter.ToInt32(body.Il, instruction.OperandOffset);
                for (var i = 0; i < count; i++)
                    yield return instruction.NextOffset + BitConverter.ToInt32(body.Il, instruction.OperandOffset + 4 + (4 * i));
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// True when control can fall through this instruction to the next one. Unconditional
    /// transfers and terminators cannot.
    /// </summary>
    private static bool FallsThrough(OpCode op) =>
        op.FlowControl is not (FlowControl.Branch or FlowControl.Return or FlowControl.Throw);

    /// <summary>
    /// THE DOMINANCE ANALYSIS. Computes every instruction reachable from the method's first
    /// instruction WITHOUT executing <paramref name="barrierOffset"/> (the <c>Monitor.Enter</c>).
    /// <para>
    /// The walk follows fall-through and ALL branch targets — the short and long unconditional
    /// forms (<c>br</c>/<c>br.s</c>), every conditional form (<c>brtrue</c>/<c>brfalse</c>/
    /// <c>beq</c>/<c>bge</c>/<c>bgt</c>/<c>ble</c>/<c>blt</c>/<c>bne.un</c> and their unsigned and
    /// <c>.s</c> variants) and <c>switch</c>, all handled generically via
    /// <see cref="OperandType"/> so no branch opcode can be missed by omission. It also enters
    /// exception handler and filter regions, since those are reachable without the barrier.
    /// </para>
    /// <para>
    /// If any guarded access is in the resulting set, some execution path reaches the registry
    /// state without taking the lock — exactly the conditional-acquisition defect.
    /// </para>
    /// </summary>
    private static HashSet<int> ReachableWithoutExecuting(DecodedBody body, int barrierOffset)
    {
        var reachable = new HashSet<int>();
        var queue = new Queue<int>();

        void Seed(int offset)
        {
            if (offset != barrierOffset && reachable.Add(offset))
                queue.Enqueue(offset);
        }

        Seed(body.Instructions[0].Offset);

        // Handler/filter regions are entered by the runtime, not by a branch: seed them too,
        // but ONLY those that can be reached without passing the barrier (their try region
        // starts before the barrier), so the analysis stays sound rather than over-approximating
        // the whole method.
        foreach (var clause in body.Method.GetMethodBody()!.ExceptionHandlingClauses)
        {
            if (clause.TryOffset >= barrierOffset)
                continue;
            Seed(clause.HandlerOffset);
            if (clause.Flags == ExceptionHandlingClauseOptions.Filter)
                Seed(clause.FilterOffset);
        }

        while (queue.Count > 0)
        {
            var offset = queue.Dequeue();
            var instruction = body.Instructions[body.IndexOf(offset)];

            foreach (var target in BranchTargets(body, instruction))
                Seed(target);

            if (FallsThrough(instruction.OpCode) && instruction.NextOffset < body.Il.Length)
                Seed(instruction.NextOffset);
        }

        return reachable;
    }

    /// <summary>
    /// Records the emitted <c>Monitor.Enter</c> OVERLOAD this build produces, so the backstop's
    /// premise is documented by an executable assertion rather than a comment: the C# <c>lock</c>
    /// statement lowers to the Roslyn two-argument <c>Monitor.Enter(object, ref bool)</c> form,
    /// not the single-argument <c>Monitor.Enter(object)</c>.
    /// <para>
    /// This is deliberately SEPARATE from
    /// <see cref="RegistryEntryPoint_RunsWhollyUnderTheLock"/>: keeping the overload check out of
    /// that test ensures a hand-rolled <c>Monitor.Enter</c>/<c>Exit</c> pair is caught by the
    /// finally-structure assertion (its true defect) instead of being short-circuited here.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(LockedRegistryMethods))]
    public void RegistryEntryPoint_EmitsTheRoslynLockEnterOverload(string methodName)
    {
        var enter = DecodeCallSites(RegistryMethod(methodName))
            .FirstOrDefault(c => IsMonitor(c.Target, nameof(Monitor.Enter)));

        Assert.True(enter is not null, $"'{methodName}' emits no Monitor.Enter call.");

        var parameters = enter!.Target.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(object), parameters[0].ParameterType);
        Assert.Equal(typeof(bool).MakeByRefType(), parameters[1].ParameterType);
    }

    /// <summary>
    /// (a) + (b) THE WHOLE-OPERATION LOCK STRUCTURE, asserted against the compiled artifact.
    /// <para>
    /// For every registry entry point this pins, with zero timing dependence:
    /// </para>
    /// <list type="number">
    ///   <item>a <c>Monitor.Enter</c> call is emitted at all, and its IL offset PRECEDES the
    ///     FIRST guarded storage access — so no part of the operation reads or mutates
    ///     <c>_slots</c>/<c>_dispatchAttempts</c> before the lock is taken;</item>
    ///   <item>a <c>Monitor.Exit</c> call is emitted whose offset FOLLOWS the LAST guarded
    ///     access — so the lock is not released mid-operation;</item>
    ///   <item>that <c>Monitor.Exit</c> sits inside a <c>finally</c> handler whose protected
    ///     <c>try</c> region spans the whole guarded span — so the release is exception-safe
    ///     and the guarded work is genuinely inside the protected region;</item>
    ///   <item>MONITOR IDENTITY — the object passed to BOTH the Enter and the Exit resolves,
    ///     through the <c>stloc</c>/<c>ldloc</c>/<c>ldfld</c> chain, to the very
    ///     <c>GoalPipeline._lock</c> field (and exactly ONE Enter and ONE Exit exist), so
    ///     <c>lock (new object())</c>, <c>lock (_slots)</c> or any other monitor is rejected;</item>
    ///   <item>DOMINANCE — no guarded access is reachable from the method entry without
    ///     executing the Enter, following fall-through and every branch form, so the lock
    ///     cannot be acquired conditionally or bypassed by a branch;</item>
    ///   <item>TRY-RANGE EXHAUSTIVENESS — EVERY guarded access (not just the first and last)
    ///     lies inside the protecting try region.</item>
    /// </list>
    /// <para>
    /// Deterministic kills: removing the <c>lock</c> leaves no Enter/Exit; narrowing it puts a
    /// guarded access before Enter or after Exit; a manual non-<c>finally</c> Enter/Exit pair
    /// leaves no covering Finally clause; locking a DIFFERENT object fails the identity check;
    /// and a conditional or branch-bypassed acquisition fails the reachability check.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(LockedRegistryMethods))]
    public void RegistryEntryPoint_RunsWhollyUnderTheLock(string methodName)
    {
        var method = RegistryMethod(methodName);
        var calls = DecodeCallSites(method);

        // ── (a) Monitor.Enter is emitted (ANY overload — the "is it locked at all" check) ──
        var enter = calls.FirstOrDefault(c => IsMonitor(c.Target, nameof(Monitor.Enter)));
        Assert.True(
            enter is not null,
            $"'{methodName}' emits no Monitor.Enter call — the operation is not locked at all.");

        // ── The guarded region: every access to the backing dictionaries ──────────────
        var guarded = calls.Where(c => IsGuardedAccess(c.Target)).ToList();
        Assert.True(
            guarded.Count > 0,
            $"'{methodName}' has no recognised guarded storage access — the probe would be vacuous.");

        var firstGuarded = guarded[0].Offset;
        var lastGuarded = guarded[^1].Offset;

        // ── (a) Enter PRECEDES the first guarded access ────────────────────────────────
        Assert.True(
            enter!.Offset < firstGuarded,
            $"'{methodName}': Monitor.Enter at IL offset {enter.Offset} does not precede the first " +
            $"guarded storage access at {firstGuarded} ({guarded[0].Target.Name}) — part of the " +
            "operation touches the registry outside the lock.");

        // ── (b) Exit FOLLOWS the last guarded access ───────────────────────────────────
        var exit = calls.FirstOrDefault(c =>
            IsMonitor(c.Target, nameof(Monitor.Exit)) && c.Offset > lastGuarded);
        Assert.True(
            exit is not null,
            $"'{methodName}' emits no Monitor.Exit after the last guarded storage access at " +
            $"{lastGuarded} ({guarded[^1].Target.Name}) — the lock is released mid-operation.");

        // ── (b) The Exit lives in a finally that protects the whole guarded span ───────
        var finallyClauses = method.GetMethodBody()!.ExceptionHandlingClauses
            .Where(c => c.Flags == ExceptionHandlingClauseOptions.Finally)
            .ToList();
        Assert.True(
            finallyClauses.Count > 0,
            $"'{methodName}' has no finally handler — the lock release is not exception-safe.");

        var covering = finallyClauses.FirstOrDefault(c =>
            exit!.Offset >= c.HandlerOffset
            && exit.Offset < c.HandlerOffset + c.HandlerLength
            && c.TryOffset <= firstGuarded
            && c.TryOffset + c.TryLength >= lastGuarded);

        Assert.True(
            covering is not null,
            $"'{methodName}': the Monitor.Exit at IL offset {exit!.Offset} is not inside a finally " +
            $"handler whose try region covers the guarded span [{firstGuarded}, {lastGuarded}] — " +
            "the lock is held by a manual, non-exception-safe Enter/Exit pair.");

        // ══════════════════════════════════════════════════════════════════════════════
        //  GAP A — MONITOR IDENTITY. Lexical ordering says nothing about WHICH object is
        //  locked: `lock (new object())` or `lock (_slots)` satisfies everything above
        //  while violating the shared-lock contract. Resolve the operand provenance and
        //  prove both calls take GoalPipeline._lock.
        // ══════════════════════════════════════════════════════════════════════════════

        var body = DecodeBody(method);
        var lockField = typeof(GoalPipeline).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException("GoalPipeline no longer has a private '_lock' field.");

        // EXACTLY ONE Enter and ONE Exit — so no second, unrelated monitor site exists and
        // the sites selected above cannot be two halves of different regions.
        var monitorCalls = new List<(int Index, bool IsEnter)>();
        for (var i = 0; i < body.Instructions.Count; i++)
        {
            var instruction = body.Instructions[i];
            if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
                continue;

            MethodBase? target = null;
            try
            {
                target = method.Module.ResolveMethod(BitConverter.ToInt32(body.Il, instruction.OperandOffset));
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (target is null || target.DeclaringType != typeof(Monitor))
                continue;
            if (target.Name is nameof(Monitor.Enter) or nameof(Monitor.Exit))
                monitorCalls.Add((i, target.Name == nameof(Monitor.Enter)));
        }

        var enterCalls = monitorCalls.Where(c => c.IsEnter).ToList();
        var exitCalls = monitorCalls.Where(c => !c.IsEnter).ToList();

        Assert.True(
            enterCalls.Count == 1,
            $"'{methodName}' emits {enterCalls.Count} Monitor.Enter calls — expected exactly one, so " +
            "the operation cannot acquire different monitors on different paths.");
        Assert.True(
            exitCalls.Count == 1,
            $"'{methodName}' emits {exitCalls.Count} Monitor.Exit calls — expected exactly one.");

        var enterFieldToken = ResolveMonitorArgumentFieldToken(body, enterCalls[0].Index, isEnter: true);
        var exitFieldToken = ResolveMonitorArgumentFieldToken(body, exitCalls[0].Index, isEnter: false);

        Assert.True(
            enterFieldToken == lockField.MetadataToken,
            $"'{methodName}': Monitor.Enter locks field " +
            $"'{method.Module.ResolveField(enterFieldToken)?.Name}', not GoalPipeline._lock — the " +
            "operation does not share the registry's monitor.");
        Assert.True(
            exitFieldToken == enterFieldToken,
            $"'{methodName}': Monitor.Exit releases a different field than Monitor.Enter acquired — " +
            "the Enter/Exit pair is not a matching protected region.");

        // ══════════════════════════════════════════════════════════════════════════════
        //  GAP B — ACQUISITION ON EVERY EXECUTION PATH. Lexical offsets do not prove
        //  dominance: `if (cond) Monitor.Enter(...); _slots.Clear();` inside a covering
        //  try/finally satisfies every ordering check above yet runs unlocked when the
        //  condition is false. Prove it by control flow instead of by offsets.
        // ══════════════════════════════════════════════════════════════════════════════

        var guardedOffsets = guarded.Select(g => g.Offset).ToHashSet();

        // (i) REACHABILITY-WITHOUT-ENTER: no guarded access may be reachable from the
        //     method's entry without executing the Monitor.Enter instruction.
        var withoutEnter = ReachableWithoutExecuting(body, enter.Offset);
        var unguardedReachable = guardedOffsets.Where(withoutEnter.Contains).OrderBy(o => o).ToList();

        Assert.True(
            unguardedReachable.Count == 0,
            $"'{methodName}': guarded storage access(es) at IL offset(s) " +
            $"[{string.Join(", ", unguardedReachable)}] are reachable from the method entry WITHOUT " +
            $"executing the Monitor.Enter at {enter.Offset} — the lock is acquired conditionally or " +
            "bypassed by a branch, so some execution path touches the registry unlocked.");

        // (ii) TRY-RANGE EXHAUSTIVENESS: EVERY guarded access — not merely the first and
        //      last — must lie inside the covering finally's try region, so the Exit
        //      protects the guarded work on every path out of the method.
        var outsideTry = guardedOffsets
            .Where(o => o < covering!.TryOffset || o >= covering.TryOffset + covering.TryLength)
            .OrderBy(o => o)
            .ToList();

        Assert.True(
            outsideTry.Count == 0,
            $"'{methodName}': guarded storage access(es) at IL offset(s) [{string.Join(", ", outsideTry)}] " +
            $"lie outside the protecting try range [{covering!.TryOffset}, " +
            $"{covering.TryOffset + covering.TryLength}) — the finally's Monitor.Exit does not cover " +
            "all guarded work.");
    }

    /// <summary>
    /// Guards the backstop itself against silent drift: if <see cref="GoalPipeline"/> ever gains a
    /// registry entry point that is not listed in <see cref="LockedRegistryMethods"/>, this fails,
    /// so a new unlocked method cannot slip past the structural proof unnoticed.
    /// </summary>
    [Fact]
    public void LockStructureBackstop_CoversEveryRegistryEntryPoint()
    {
        var expected = LockedRegistryMethodNames.ToHashSet(StringComparer.Ordinal);

        var actual = typeof(GoalPipeline)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Where(m => DecodeCallSites(m).Any(c => IsGuardedAccess(c.Target)))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }

    #endregion
}

using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
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

    // AdmissionOutcome is internal too — the admission matrix carries integer codes.
    public const int AdmAdmitted = (int)AdmissionOutcome.Admitted;
    public const int AdmNoSlot = (int)AdmissionOutcome.NoSlot;
    public const int AdmSlotAbandoned = (int)AdmissionOutcome.SlotAbandoned;
    public const int AdmAlreadyAdmitted = (int)AdmissionOutcome.SlotAlreadyAdmitted;

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
        "AllocateAttemptAndRegisterSlotWithId",
        "FindLiveSlotTaskIdAt",
        "ResolveAndCheckSlot",
        "AdmitCompletion",
        "RecordSlot",
        "AbandonSlot",
        "RetireSlotAndClearIfCurrent",
        "IsSlotAbandoned",
        "FailSlot",
        "ReleaseSlot",
        "AbandonPendingSlots",
        "GetSlotsForTest",
        "ForceSlotStateForTest",
        "ClearRegistryForTest",
        "SeedSlotForTest",
        // Added with the detached capture/restore building block: CaptureRegistry and
        // RestoreRegistry both touch _slots/_dispatchAttempts and must run wholly under _lock.
        "CaptureRegistry",
        "RestoreRegistry",
        // Added with the detached admission carrier: the ownership capture reads the pointer AND
        // the whole registry, and must do so in ONE _lock span.
        "CaptureAdmissionOwnership",
    ];

    /// <summary>
    /// The LOCK-HELD HELPERS: private methods that touch the backing dictionaries but take NO
    /// lock themselves, because their contract requires the CALLER to already hold <c>_lock</c>.
    /// They are deliberately excluded from <see cref="LockedRegistryMethodNames"/> (they emit no
    /// Monitor pair of their own) and are instead pinned by
    /// <see cref="LockHeldHelper_TakesNoLockAndIsCalledOnlyFromLockedEntryPoints"/>, while a CALL
    /// to one of them counts as a guarded access in every analysis below — so an entry point that
    /// delegates its storage reads to a helper is held to exactly the same lock structure.
    /// </summary>
    private static readonly string[] LockHeldRegistryHelperNames =
    [
        "CopyRegistrySnapshotUnderLock",
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
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
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

    // ══════════════════════════════════════════════════════════════════════════════════
    //  THE ACTIVE-TASK POINTER AS A GUARDED DATUM.
    //
    //  The dictionary-only notion of "guarded access" below is NOT sufficient for the
    //  methods that capture or mutate the ACTIVE-TASK POINTER alongside the registry.
    //  A method may take the lock, copy the registry entirely inside it, and still have
    //  read `ActiveTaskId` BEFORE `Monitor.Enter`:
    //
    //      var pointer = ActiveTaskId;                       // ← unsynchronized read
    //      lock (_lock) { return new …(GoalId, pointer, CopyRegistrySnapshotUnderLock()); }
    //
    //  That mutant satisfies every dictionary-based assertion and every behavioural
    //  blocked-while-held observation (the pointer can be committed before the worker is
    //  even started), yet it destroys the one-lock pointer+registry coherence the capture
    //  exists to provide. The pointer must therefore be treated as guarded state in its
    //  own right, with the SAME deterministic IL technique: provenance, dominance, and
    //  try-span containment, all resolved from the emitted bytes.
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The property accessors through which <see cref="GoalPipeline.ActiveTaskId"/> is reached.
    /// The property is auto-implemented, so C# lowers every in-class read/write to one of these
    /// calls; the direct backing-field forms are covered separately by
    /// <see cref="IsPointerAccessInstruction"/> so a hand-written field-backed property cannot
    /// slip past.
    /// </summary>
    private static readonly string[] PointerAccessorNames =
    [
        "get_ActiveTaskId",
        "set_ActiveTaskId",
    ];

    /// <summary>
    /// True when <paramref name="instruction"/> READS OR WRITES the active-task pointer — either
    /// through its property accessors or through a direct <c>ldfld</c>/<c>ldflda</c>/<c>stfld</c>
    /// of its backing field. Both forms are recognised, so neither an auto-property nor a
    /// hand-rolled field-backed property can hide an unsynchronized pointer access.
    /// </summary>
    private static bool IsPointerAccessInstruction(DecodedBody body, Instruction instruction)
    {
        var module = body.Method.Module;
        var op = instruction.OpCode;

        if (op == OpCodes.Call || op == OpCodes.Callvirt)
        {
            MethodBase? target;
            try
            {
                target = module.ResolveMethod(BitConverter.ToInt32(body.Il, instruction.OperandOffset));
            }
            catch (ArgumentException)
            {
                return false;
            }

            return target is not null
                && target.DeclaringType == typeof(GoalPipeline)
                && PointerAccessorNames.Contains(target.Name, StringComparer.Ordinal);
        }

        if (op == OpCodes.Ldfld || op == OpCodes.Ldflda || op == OpCodes.Stfld)
        {
            FieldInfo? field;
            try
            {
                field = module.ResolveField(BitConverter.ToInt32(body.Il, instruction.OperandOffset));
            }
            catch (ArgumentException)
            {
                return false;
            }

            return field is not null
                && field.DeclaringType == typeof(GoalPipeline)
                && field.Name.Contains("ActiveTaskId", StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>Offsets of every active-task-pointer access in the decoded body.</summary>
    private static List<int> PointerAccessOffsets(DecodedBody body) =>
        [.. body.Instructions.Where(i => IsPointerAccessInstruction(body, i)).Select(i => i.Offset)];

    /// <summary>
    /// A guarded access: any call on the registry's backing <see cref="Dictionary{TKey,TValue}"/>
    /// storage (or its nested enumerator / key-collection), i.e. a read or write of
    /// <c>_slots</c> or <c>_dispatchAttempts</c> that MUST sit inside the lock — PLUS a call to
    /// one of the <see cref="LockHeldRegistryHelperNames"/>, whose whole body is such storage
    /// work performed on the caller's behalf. Counting the delegation itself keeps a delegating
    /// entry point under exactly the same lock-structure proof as an inlined one.
    /// <para>
    /// The active-task POINTER is deliberately not folded in here: it is not reached through a
    /// call site at all in every form, so it gets its own instruction-level treatment via
    /// <see cref="PointerAccessOffsets"/> and
    /// <see cref="PointerAndRegistryMethod_ReadsThePointerInsideTheSameLockSpanAsTheRegistry"/>.
    /// </para>
    /// </summary>
    private static bool IsGuardedAccess(MethodBase m)
    {
        var declaring = m.DeclaringType;
        if (declaring is null)
            return false;

        if (declaring == typeof(GoalPipeline)
            && LockHeldRegistryHelperNames.Contains(m.Name, StringComparer.Ordinal))
        {
            return true;
        }

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
    /// The locked entry points that touch the ACTIVE-TASK POINTER as well as the registry, and
    /// therefore owe the one-lock-span pointer proof below. The drift guard
    /// <see cref="PointerProof_CoversEveryLockedMethodThatTouchesThePointer"/> keeps this list
    /// honest.
    /// </summary>
    private static readonly string[] PointerAndRegistryMethodNames =
    [
        // Captures the pointer AND the whole registry — the one-lock coherence contract.
        "CaptureAdmissionOwnership",
        // Retires the slot AND if-current-clears the pointer in a single acquisition.
        "RetireSlotAndClearIfCurrent",
    ];

    /// <summary>Theory feed of <see cref="PointerAndRegistryMethodNames"/> (strings only — the
    /// registry types are internal and cannot appear in a public test signature).</summary>
    public static TheoryData<string> PointerAndRegistryMethods
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in PointerAndRegistryMethodNames)
                data.Add(name);
            return data;
        }
    }

    /// <summary>
    /// THE POINTER-INSIDE-THE-SAME-LOCK-SPAN PROOF — the gap the dictionary-only structural test
    /// and the behavioural blocked-while-held test both leave open.
    /// <para>
    /// For every method that touches the ACTIVE-TASK POINTER alongside the registry, this pins,
    /// against the emitted bytes and with zero timing dependence:
    /// </para>
    /// <list type="number">
    ///   <item>at least one pointer access exists at all (so the probe is never vacuous) and at
    ///     least one guarded registry access exists — the method really does combine the two;</item>
    ///   <item>EVERY pointer access lies at an IL offset AFTER the <c>Monitor.Enter</c> and
    ///     BEFORE the <c>Monitor.Exit</c>, both re-resolved here to
    ///     <c>GoalPipeline._lock</c> through the operand-provenance analysis;</item>
    ///   <item>DOMINANCE — no pointer access is reachable from the method entry WITHOUT executing
    ///     that <c>Monitor.Enter</c>, following fall-through and every branch form, so the access
    ///     cannot be hoisted above the lock or reached by a conditional bypass;</item>
    ///   <item>EVERY pointer access lies inside the SAME <c>finally</c>-protected try region that
    ///     covers the guarded registry work — i.e. the pointer and the registry are touched in
    ///     ONE protected span, not two.</item>
    /// </list>
    /// <para>
    /// THE NAMED MUTANT THIS KILLS: caching the pointer before the lock —
    /// <c>var pointer = ActiveTaskId; lock (_lock) { … CopyRegistrySnapshotUnderLock() … }</c> —
    /// moves the <c>get_ActiveTaskId</c> call to an offset below <c>Monitor.Enter</c>, makes it
    /// reachable from the entry without executing the Enter, and puts it outside the try region.
    /// Three independent assertions fail, on 100% of runs. That mutant satisfies every
    /// dictionary-based assertion and the behavioural <c>WhileLockHeld</c> observation, which is
    /// precisely why this test exists; the behavioural companion stays supplemental only.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(PointerAndRegistryMethods))]
    public void PointerAndRegistryMethod_ReadsThePointerInsideTheSameLockSpanAsTheRegistry(string methodName)
    {
        var method = RegistryMethod(methodName);
        var body = DecodeBody(method);
        var calls = DecodeCallSites(method);

        // ── (1) NON-VACUITY: the method really does combine pointer and registry work ──
        var pointerOffsets = PointerAccessOffsets(body);
        Assert.True(
            pointerOffsets.Count > 0,
            $"'{methodName}' contains no active-task-pointer access — the pointer probe would be " +
            "vacuous, so this method no longer belongs in PointerAndRegistryMethodNames.");

        var guarded = calls.Where(c => IsGuardedAccess(c.Target)).ToList();
        Assert.True(
            guarded.Count > 0,
            $"'{methodName}' has no recognised guarded storage access — the one-span claim would be vacuous.");

        // ── The monitor pair: exactly one of each, resolved to GoalPipeline._lock here so
        //    this test stands on its own operand analysis rather than on another test's.
        var lockField = typeof(GoalPipeline).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException("GoalPipeline no longer has a private '_lock' field.");

        var monitorCalls = new List<(int Index, bool IsEnter)>();
        for (var i = 0; i < body.Instructions.Count; i++)
        {
            var instruction = body.Instructions[i];
            if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
                continue;

            MethodBase? target;
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
        Assert.True(enterCalls.Count == 1, $"'{methodName}' emits {enterCalls.Count} Monitor.Enter calls — expected exactly one.");
        Assert.True(exitCalls.Count == 1, $"'{methodName}' emits {exitCalls.Count} Monitor.Exit calls — expected exactly one.");

        Assert.True(
            ResolveMonitorArgumentFieldToken(body, enterCalls[0].Index, isEnter: true) == lockField.MetadataToken,
            $"'{methodName}': the pointer's protecting Monitor.Enter does not acquire GoalPipeline._lock.");

        var enterOffset = body.Instructions[enterCalls[0].Index].Offset;
        var exitOffset = body.Instructions[exitCalls[0].Index].Offset;

        // ── (2) ORDERING: every pointer access sits strictly between Enter and Exit ────
        var beforeEnter = pointerOffsets.Where(o => o < enterOffset).OrderBy(o => o).ToList();
        Assert.True(
            beforeEnter.Count == 0,
            $"'{methodName}': active-task-pointer access(es) at IL offset(s) [{string.Join(", ", beforeEnter)}] " +
            $"precede the Monitor.Enter at {enterOffset} — the pointer is read or written OUTSIDE the lock " +
            "(e.g. cached before the acquisition), so the pointer and the registry are not captured as one " +
            "coherent state.");

        var afterExit = pointerOffsets.Where(o => o > exitOffset).OrderBy(o => o).ToList();
        Assert.True(
            afterExit.Count == 0,
            $"'{methodName}': active-task-pointer access(es) at IL offset(s) [{string.Join(", ", afterExit)}] " +
            $"follow the Monitor.Exit at {exitOffset} — the pointer is touched after the lock was released.");

        // ── (3) DOMINANCE: no pointer access is reachable without executing the Enter ──
        var withoutEnter = ReachableWithoutExecuting(body, enterOffset);
        var unguardedReachable = pointerOffsets.Where(withoutEnter.Contains).OrderBy(o => o).ToList();
        Assert.True(
            unguardedReachable.Count == 0,
            $"'{methodName}': active-task-pointer access(es) at IL offset(s) " +
            $"[{string.Join(", ", unguardedReachable)}] are reachable from the method entry WITHOUT executing " +
            $"the Monitor.Enter at {enterOffset} — the pointer can be touched unlocked on some execution path.");

        // ── (4) ONE PROTECTED SPAN: the pointer and the registry share the SAME try ───
        var firstGuarded = guarded[0].Offset;
        var lastGuarded = guarded[^1].Offset;

        var covering = method.GetMethodBody()!.ExceptionHandlingClauses
            .Where(c => c.Flags == ExceptionHandlingClauseOptions.Finally)
            .FirstOrDefault(c =>
                exitOffset >= c.HandlerOffset
                && exitOffset < c.HandlerOffset + c.HandlerLength
                && c.TryOffset <= firstGuarded
                && c.TryOffset + c.TryLength >= lastGuarded);

        Assert.True(
            covering is not null,
            $"'{methodName}': no finally handler holds the Monitor.Exit while covering the guarded registry " +
            $"span [{firstGuarded}, {lastGuarded}].");

        var pointerOutsideTry = pointerOffsets
            .Where(o => o < covering!.TryOffset || o >= covering.TryOffset + covering.TryLength)
            .OrderBy(o => o)
            .ToList();

        Assert.True(
            pointerOutsideTry.Count == 0,
            $"'{methodName}': active-task-pointer access(es) at IL offset(s) " +
            $"[{string.Join(", ", pointerOutsideTry)}] lie outside the try range " +
            $"[{covering!.TryOffset}, {covering.TryOffset + covering.TryLength}) that protects the guarded " +
            "registry work — the pointer and the registry are not touched in ONE lock span.");
    }

    /// <summary>
    /// NON-VACUITY BACKSTOP for the pointer probe itself: <see cref="IsPointerAccessInstruction"/>
    /// must genuinely recognise the pointer in the shapes production uses.
    /// <para>
    /// Without this, a probe that silently stopped matching anything (a renamed accessor, a
    /// changed lowering) would make
    /// <see cref="PointerAndRegistryMethod_ReadsThePointerInsideTheSameLockSpanAsTheRegistry"/>
    /// pass vacuously on an empty offset set — except that its own non-vacuity assertion fires
    /// first. This test pins the recognition POSITIVELY and independently: the pointer-only
    /// methods must be detected, and a method that provably never touches the pointer must NOT
    /// be, so the predicate is neither blind nor indiscriminate.
    /// </para>
    /// </summary>
    [Fact]
    public void PointerAccessProbe_RecognisesRealPointerAccessesAndNothingElse()
    {
        // POSITIVE: the pointer's own entry points are detected in both directions.
        foreach (var name in new[] { "TrySetActiveTask", "ClearActiveTaskIfCurrent", "SetActiveTask", "ClearActiveTask" })
        {
            Assert.True(
                PointerAccessOffsets(DecodeBody(RegistryMethod(name))).Count > 0,
                $"The pointer probe does not recognise the active-task access inside '{name}' — it has gone " +
                "blind, so every pointer assertion built on it would be vacuous.");
        }

        // NEGATIVE: a registry method that provably never touches the pointer is not matched.
        foreach (var name in new[] { "CaptureRegistry", "RecordSlot", "AbandonPendingSlots" })
        {
            Assert.True(
                PointerAccessOffsets(DecodeBody(RegistryMethod(name))).Count == 0,
                $"The pointer probe reports an active-task access inside '{name}', which touches no pointer — " +
                "the predicate is matching indiscriminately.");
        }
    }

    /// <summary>
    /// DRIFT GUARD for the pointer proof, mirroring
    /// <see cref="LockStructureBackstop_CoversEveryRegistryEntryPoint"/>: every locked registry
    /// entry point that ALSO touches the active-task pointer must be listed in
    /// <see cref="PointerAndRegistryMethodNames"/>, so a future combined pointer+registry method
    /// cannot be added without inheriting the one-span proof above.
    /// </summary>
    [Fact]
    public void PointerProof_CoversEveryLockedMethodThatTouchesThePointer()
    {
        var expected = PointerAndRegistryMethodNames.ToHashSet(StringComparer.Ordinal);

        var actual = LockedRegistryMethodNames
            .Where(name => PointerAccessOffsets(DecodeBody(RegistryMethod(name))).Count > 0)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Guards the backstop itself against silent drift: if <see cref="GoalPipeline"/> ever gains a
    /// registry entry point that is not listed in <see cref="LockedRegistryMethods"/>, this fails,
    /// so a new unlocked method cannot slip past the structural proof unnoticed.
    /// <para>
    /// The <see cref="LockHeldRegistryHelperNames"/> are excluded here BY NAME and pinned instead
    /// by <see cref="LockHeldHelper_TakesNoLockAndIsCalledOnlyFromLockedEntryPoints"/> — they take
    /// no lock of their own by design, so listing them among the locked entry points would assert
    /// a Monitor pair they must not have. A brand-new method still fails this test.
    /// </para>
    /// </summary>
    [Fact]
    public void LockStructureBackstop_CoversEveryRegistryEntryPoint()
    {
        var expected = LockedRegistryMethodNames.ToHashSet(StringComparer.Ordinal);

        var actual = typeof(GoalPipeline)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Where(m => !LockHeldRegistryHelperNames.Contains(m.Name, StringComparer.Ordinal))
            .Where(m => DecodeCallSites(m).Any(c => IsGuardedAccess(c.Target)))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// THE LOCK-HELD HELPER CONTRACT, asserted against the compiled artifact (deterministic — no
    /// timing anywhere). For every <see cref="LockHeldRegistryHelperNames"/> entry:
    /// <list type="number">
    ///   <item>it is PRIVATE and touches the backing dictionaries, so it is genuinely registry
    ///     storage work;</item>
    ///   <item>it emits NO <c>Monitor.Enter</c>/<c>Exit</c> of its own — it neither re-enters nor
    ///     independently acquires the monitor, which is what lets a caller cover it plus other
    ///     reads with a SINGLE acquisition;</item>
    ///   <item>EVERY call site of it inside <see cref="GoalPipeline"/> is a method listed in
    ///     <see cref="LockedRegistryMethodNames"/> — so the helper is never reached from an
    ///     unlocked path.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void LockHeldHelper_TakesNoLockAndIsCalledOnlyFromLockedEntryPoints()
    {
        var lockedNames = LockedRegistryMethodNames.ToHashSet(StringComparer.Ordinal);

        foreach (var helperName in LockHeldRegistryHelperNames)
        {
            var helper = RegistryMethod(helperName);
            Assert.True(helper.IsPrivate, $"'{helperName}' must be private — it is a lock-held internal step.");

            var helperCalls = DecodeCallSites(helper);

            // (1) It really is storage work: it touches the backing dictionaries directly.
            Assert.True(
                helperCalls.Any(c => IsGuardedAccess(c.Target)),
                $"'{helperName}' performs no registry storage access — the helper contract would be vacuous.");

            // (2) No monitor traffic of its own: neither Enter nor Exit.
            Assert.True(
                !helperCalls.Any(c => IsMonitor(c.Target, nameof(Monitor.Enter))
                    || IsMonitor(c.Target, nameof(Monitor.Exit))),
                $"'{helperName}' emits Monitor traffic — a lock-held helper must rely on the caller's " +
                "single acquisition instead of taking or re-entering the monitor itself.");

            // (3) Every caller inside GoalPipeline is a locked entry point.
            var callers = typeof(GoalPipeline)
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic
                    | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(m => m.Name != helperName && m.GetMethodBody() is not null)
                .Where(m => DecodeCallSites(m).Any(c =>
                    c.Target.DeclaringType == typeof(GoalPipeline)
                    && string.Equals(c.Target.Name, helperName, StringComparison.Ordinal)))
                .Select(m => m.Name)
                .ToList();

            Assert.NotEmpty(callers);

            var unlockedCallers = callers.Where(n => !lockedNames.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.True(
                unlockedCallers.Count == 0,
                $"'{helperName}' is called from non-locked method(s) [{string.Join(", ", unlockedCallers)}] — " +
                "the lock-held helper would then run without the pipeline monitor.");
        }
    }

    #endregion

    #region (j) The capture — fixtures

    /// <summary>A plan containing every one of the five worker phases exactly once.</summary>
    private static readonly GoalPhase[] FullPlan =
    [
        GoalPhase.Coding, GoalPhase.DocWriting, GoalPhase.Testing,
        GoalPhase.Review, GoalPhase.Improve, GoalPhase.Merging,
    ];

    private static GoalPipeline NewPipeline(string goalId) =>
        new(new Goal { Id = goalId, Description = "Test goal" });

    /// <summary>
    /// Builds a pipeline whose INSTALLED plan, MACHINE phase, PIPELINE phase and ITERATION are
    /// each set independently, so every capture vector can be expressed declaratively.
    /// </summary>
    /// <param name="goalId">Goal ID (embedded verbatim in built task IDs).</param>
    /// <param name="installedPlan">The plan handed to <c>SetPlan</c>, or <c>null</c> for none.</param>
    /// <param name="machinePhase">The phase the state machine is restored at.</param>
    /// <param name="pipelinePhase">The pipeline's own phase; defaults to <paramref name="machinePhase"/>.</param>
    /// <param name="restorePlan">
    /// The plan the MACHINE is restored from when it must differ from the installed one
    /// (used to drive the queue length independently of the installed plan).
    /// </param>
    /// <param name="iteration">The one-based iteration the pipeline should report.</param>
    private static GoalPipeline CaptureFixture(
        string goalId = "goal-1",
        IReadOnlyList<GoalPhase>? installedPlan = null,
        GoalPhase machinePhase = GoalPhase.Coding,
        GoalPhase? pipelinePhase = null,
        IReadOnlyList<GoalPhase>? restorePlan = null,
        int iteration = 1)
    {
        var pipeline = NewPipeline(goalId);
        for (var i = 1; i < iteration; i++)
            Assert.True(pipeline.IterationBudget.TryConsume());
        Assert.Equal(iteration, pipeline.Iteration);

        if (installedPlan is not null)
            pipeline.SetPlan(new IterationPlan { Phases = [.. installedPlan] });

        pipeline.StateMachine.RestoreFromPlan(restorePlan ?? installedPlan ?? [], machinePhase);
        pipeline.AdvanceTo(pipelinePhase ?? machinePhase);
        return pipeline;
    }

    /// <summary>Allocates through A1a's untouched helper purely to READ the position's counter.</summary>
    private static int ProbeAttempt(GoalPipeline pipeline, WorkSlotPosition position, string probeId = "probe") =>
        pipeline.AllocateAttemptAndRegisterSlot(probeId, position).Attempt;

    /// <summary>The attempt number encoded in the LAST segment of a built task ID.</summary>
    private static int AttemptFromTaskId(string taskId) =>
        int.Parse(taskId[(taskId.LastIndexOf('-') + 1)..], CultureInfo.InvariantCulture);

    #endregion

    #region (k) The capture — classification refusals

    /// <summary>
    /// CLASSIFICATION FIRST: every phase outside {Coding, Testing, DocWriting, Review, Improve}
    /// is refused as <c>InvalidPhase</c> — including the terminal and non-worker phases, and
    /// including an UNDEFINED machine phase, which never reaches the role mapping.
    /// </summary>
    [Theory]
    [InlineData(GoalPhase.Planning)]
    [InlineData(GoalPhase.Merging)]
    [InlineData(GoalPhase.Done)]
    [InlineData(GoalPhase.Failed)]
    public void Capture_NonWorkerMachinePhase_ThrowsInvalidPhase(GoalPhase phase)
    {
        var pipeline = CaptureFixture(installedPlan: FullPlan, machinePhase: phase, restorePlan: []);

        var ex = Assert.Throws<WorkSlotException>(() => pipeline.CaptureDispatchPosition(WorkerRole.Coder));

        Assert.Equal(WorkSlotEvent.InvalidPhase, ex.Event);
        Assert.Equal(phase, ex.Position.Phase);
        Assert.Equal(phase, ex.MachinePhase);
        Assert.Null(ex.PipelinePhase);
        Assert.Equal(1, ex.Position.Iteration);

        // NO ATTEMPT CONSUMED anywhere near the refused position.
        Assert.Equal(1, ProbeAttempt(pipeline, new WorkSlotPosition(1, phase, 1)));
    }

    /// <summary>An UNDEFINED machine phase classifies as InvalidPhase, not as a role problem.</summary>
    [Fact]
    public void Capture_UndefinedMachinePhase_ThrowsInvalidPhase()
    {
        const GoalPhase undefined = (GoalPhase)999;
        var pipeline = CaptureFixture(
            installedPlan: FullPlan,
            machinePhase: undefined,
            pipelinePhase: GoalPhase.Coding,
            restorePlan: []);

        var ex = Assert.Throws<WorkSlotException>(() => pipeline.CaptureDispatchPosition(WorkerRole.Coder));

        Assert.Equal(WorkSlotEvent.InvalidPhase, ex.Event);
        Assert.Equal(undefined, ex.Position.Phase);
        Assert.Empty(pipeline.GetSlotsForTest());
    }

    /// <summary>
    /// An UNDEFINED PIPELINE phase is a DIVERGENCE, not an InvalidPhase: the classification reads
    /// the MACHINE's phase (a valid worker phase here), and only the divergence check compares the
    /// pipeline's own phase against it.
    /// </summary>
    [Fact]
    public void Capture_UndefinedPipelinePhase_ThrowsPhaseDivergence()
    {
        const GoalPhase undefined = (GoalPhase)999;
        var pipeline = CaptureFixture(
            installedPlan: FullPlan,
            machinePhase: GoalPhase.Coding,
            pipelinePhase: undefined);

        var ex = Assert.Throws<WorkSlotException>(() => pipeline.CaptureDispatchPosition(WorkerRole.Coder));

        Assert.Equal(WorkSlotEvent.PhaseDivergence, ex.Event);
        Assert.Equal(undefined, ex.PipelinePhase);
        Assert.Equal(GoalPhase.Coding, ex.MachinePhase);
        Assert.Equal(new WorkSlotPosition(1, GoalPhase.Coding, 1), ex.Position);

        Assert.Equal(1, ProbeAttempt(pipeline, new WorkSlotPosition(1, GoalPhase.Coding, 1)));
    }

    /// <summary>
    /// PLAN UNAVAILABLE, SOURCE 1: a plan IS installed — and it even contains the phase — but the
    /// executed prefix computed from the machine's queue does not, so the occurrence walk fails.
    /// </summary>
    [Fact]
    public void Capture_PlanInstalledButOccurrenceNotFound_ThrowsPlanUnavailable()
    {
        // Installed: [Coding, Testing, Review, Merging] (contains Review).
        // Machine restored from a 4-entry plan STARTING at Review, so its queue holds 3 entries
        // and the executed prefix of the installed plan is just [Coding] — Review is not there.
        var pipeline = CaptureFixture(
            installedPlan: [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
            machinePhase: GoalPhase.Review,
            restorePlan: [GoalPhase.Review, GoalPhase.Testing, GoalPhase.Improve, GoalPhase.Merging]);

        var ex = Assert.Throws<WorkSlotException>(() => pipeline.CaptureDispatchPosition(WorkerRole.Reviewer));

        Assert.Equal(WorkSlotEvent.PlanUnavailable, ex.Event);
        Assert.Equal(new WorkSlotPosition(1, GoalPhase.Review, 0), ex.Position);
        Assert.Null(ex.PipelinePhase);
        Assert.Equal(GoalPhase.Review, ex.MachinePhase);

        Assert.Empty(pipeline.GetSlotsForTest());
        Assert.Equal(1, ProbeAttempt(pipeline, new WorkSlotPosition(1, GoalPhase.Review, 1)));
    }

    /// <summary>
    /// PLAN UNAVAILABLE, SOURCE 2: the no-plan restoration. The snapshot constructor synced the
    /// machine to the restored worker phase with an EMPTY queue and installed nothing, so the
    /// capture reports the same refusal — never a fabricated occurrence.
    /// </summary>
    [Fact]
    public void Capture_NoPlanRestoration_ThrowsPlanUnavailable()
    {
        var pipeline = RestoredWithoutPlan(GoalPhase.Coding);

        var ex = Assert.Throws<WorkSlotException>(() => pipeline.CaptureDispatchPosition(WorkerRole.Coder));

        Assert.Equal(WorkSlotEvent.PlanUnavailable, ex.Event);
        Assert.Equal(new WorkSlotPosition(pipeline.Iteration, GoalPhase.Coding, 0), ex.Position);
        Assert.Empty(pipeline.GetSlotsForTest());
    }

    /// <summary>The pipeline phase disagreeing with the machine's is a coherent divergence.</summary>
    [Fact]
    public void Capture_PipelinePhaseDivergesFromMachinePhase_ThrowsPhaseDivergence()
    {
        var pipeline = CaptureFixture(
            installedPlan: FullPlan,
            machinePhase: GoalPhase.Coding,
            pipelinePhase: GoalPhase.Testing);

        var ex = Assert.Throws<WorkSlotException>(() => pipeline.CaptureDispatchPosition(WorkerRole.Coder));

        Assert.Equal(WorkSlotEvent.PhaseDivergence, ex.Event);
        Assert.Equal(GoalPhase.Testing, ex.PipelinePhase);
        Assert.Equal(GoalPhase.Coding, ex.MachinePhase);
        Assert.Equal(new WorkSlotPosition(1, GoalPhase.Coding, 1), ex.Position);
    }

    #endregion

    #region (l) The capture — the exact role mapping

    /// <summary>Theory feed of the EXACT phase→role mapping the capture must honour.</summary>
    public static TheoryData<GoalPhase, WorkerRole> ExactRoleMapping => new()
    {
        { GoalPhase.Coding, WorkerRole.Coder },
        { GoalPhase.Testing, WorkerRole.Tester },
        { GoalPhase.DocWriting, WorkerRole.DocWriter },
        { GoalPhase.Review, WorkerRole.Reviewer },
        { GoalPhase.Improve, WorkerRole.Improver },
    };

    /// <summary>The matching role is accepted for every one of the five worker phases.</summary>
    [Theory]
    [MemberData(nameof(ExactRoleMapping))]
    public void Capture_MatchingRole_Succeeds(GoalPhase phase, WorkerRole role)
    {
        var pipeline = CaptureFixture(installedPlan: FullPlan, machinePhase: phase);

        var built = pipeline.CaptureDispatchPosition(role);

        Assert.Equal(new WorkSlotPosition(1, phase, 1), built.Position);
        Assert.Equal(1, built.Attempt);
        Assert.Equal($"goal-1-{role.ToRoleName()}-001-01-001", built.TaskId);
    }

    /// <summary>
    /// Every NON-matching role — including the Coding-phase + Tester vector called out explicitly
    /// — is refused, and the exception carries both the passed and the derived role.
    /// </summary>
    [Theory]
    [InlineData(GoalPhase.Coding, WorkerRole.Tester)]
    [InlineData(GoalPhase.Coding, WorkerRole.DocWriter)]
    [InlineData(GoalPhase.Testing, WorkerRole.Coder)]
    [InlineData(GoalPhase.DocWriting, WorkerRole.Reviewer)]
    [InlineData(GoalPhase.Review, WorkerRole.Improver)]
    [InlineData(GoalPhase.Improve, WorkerRole.Coder)]
    [InlineData(GoalPhase.Coding, WorkerRole.Unspecified)]
    [InlineData(GoalPhase.Coding, WorkerRole.Orchestrator)]
    [InlineData(GoalPhase.Coding, WorkerRole.MergeWorker)]
    public void Capture_MismatchedRole_ThrowsRoleMismatch(GoalPhase phase, WorkerRole role)
    {
        var pipeline = CaptureFixture(installedPlan: FullPlan, machinePhase: phase);

        var ex = Assert.Throws<WorkSlotException>(() => pipeline.CaptureDispatchPosition(role));

        Assert.Equal(WorkSlotEvent.RoleMismatch, ex.Event);
        Assert.Equal(role, ex.PassedRole);
        Assert.Equal(phase.ToWorkerRole(), ex.DerivedRole);
        Assert.Equal(new WorkSlotPosition(1, phase, 1), ex.Position);

        // NO ATTEMPT CONSUMED by the refusal.
        Assert.Empty(pipeline.GetSlotsForTest());
        Assert.Equal(1, ProbeAttempt(pipeline, new WorkSlotPosition(1, phase, 1)));
    }

    /// <summary>An UNDEFINED role is simply a role that differs from the derived one.</summary>
    [Fact]
    public void Capture_UndefinedRole_ThrowsRoleMismatch()
    {
        const WorkerRole undefined = (WorkerRole)999;
        var pipeline = CaptureFixture(installedPlan: FullPlan, machinePhase: GoalPhase.Coding);

        var ex = Assert.Throws<WorkSlotException>(() => pipeline.CaptureDispatchPosition(undefined));

        Assert.Equal(WorkSlotEvent.RoleMismatch, ex.Event);
        Assert.Equal(undefined, ex.PassedRole);
        Assert.Equal(WorkerRole.Coder, ex.DerivedRole);
        Assert.Equal(1, ProbeAttempt(pipeline, new WorkSlotPosition(1, GoalPhase.Coding, 1)));
    }

    #endregion

    #region (m) The capture — the LIVE-position rule

    /// <summary>A LIVE slot (Pending or Claimed) at the position refuses the capture.</summary>
    [Theory]
    [InlineData(StPending)]
    [InlineData(StClaimed)]
    public void Capture_LiveSlotAtPosition_ThrowsDoubleAssignment(int stateCode)
    {
        var pipeline = CaptureFixture(installedPlan: FullPlan, machinePhase: GoalPhase.Coding);
        var pos = new WorkSlotPosition(1, GoalPhase.Coding, 1);
        Assert.True(pipeline.SeedSlotForTest("occupant", pos, 4, St(stateCode)));

        var before = Snapshot(pipeline);
        var ex = Assert.Throws<WorkSlotException>(() => pipeline.CaptureDispatchPosition(WorkerRole.Coder));

        Assert.Equal(WorkSlotEvent.DoubleAssignment, ex.Event);
        Assert.Equal("occupant", ex.ExistingTaskId);
        Assert.Equal(pos, ex.Position);

        // Nothing was registered and nothing changed state.
        Assert.Equal(before, Snapshot(pipeline));

        // NO ATTEMPT CONSUMED: retire the occupant, then probe — attempt 1, not 2.
        Assert.True(pipeline.ForceSlotStateForTest("occupant", WorkSlotState.Recorded));
        Assert.Equal(1, ProbeAttempt(pipeline, pos));
    }

    /// <summary>A DEAD slot (Recorded or Abandoned) does not occupy the position.</summary>
    [Theory]
    [InlineData(StRecorded)]
    [InlineData(StAbandoned)]
    public void Capture_DeadSlotAtPosition_PermitsTheCapture(int stateCode)
    {
        var pipeline = CaptureFixture(installedPlan: FullPlan, machinePhase: GoalPhase.Coding);
        var pos = new WorkSlotPosition(1, GoalPhase.Coding, 1);
        Assert.True(pipeline.SeedSlotForTest("dead", pos, 4, St(stateCode)));

        var built = pipeline.CaptureDispatchPosition(WorkerRole.Coder);

        Assert.Equal(pos, built.Position);
        Assert.Equal(1, built.Attempt);
        Assert.Equal("goal-1-coder-001-01-001", built.TaskId);
        Assert.Equal(2, pipeline.GetSlotsForTest().Count);
    }

    /// <summary>
    /// SEEDED-COLLISION, HONEST FORM 1 — THE DUPLICATE-ID ARBITER. A dead slot parked at a
    /// DIFFERENT position already owns the exact task ID the capture would build, so the position
    /// is free but the ID is taken. The refusal surfaces as the allocation helper's
    /// <see cref="ArgumentException"/> — a DIFFERENT exception type from the slot-integrity
    /// <see cref="WorkSlotException"/>, which is precisely what identifies the rejecting layer.
    /// </summary>
    [Fact]
    public void Capture_SeededCollidingTaskIdAtDifferentPosition_ThrowsTheHelpersArgumentException()
    {
        var pipeline = CaptureFixture("add-auth", FullPlan, GoalPhase.Coding, iteration: 2);
        const string collidingId = "add-auth-coder-002-01-001";

        // Parked at a DIFFERENT position (occurrence 7) and DEAD, so only the ID can collide.
        Assert.True(pipeline.SeedSlotForTest(
            collidingId, new WorkSlotPosition(2, GoalPhase.Coding, 7), 1, WorkSlotState.Recorded));

        var ex = Assert.Throws<ArgumentException>(() => pipeline.CaptureDispatchPosition(WorkerRole.Coder));

        // THE LAYER MARKER: an argument failure from the allocation, not a slot-integrity event.
        Assert.IsNotType<WorkSlotException>(ex);
        Assert.Contains(collidingId, ex.Message, StringComparison.Ordinal);

        // The refusal consumed no attempt at the target position.
        Assert.Single(pipeline.GetSlotsForTest());
        Assert.Equal(1, ProbeAttempt(pipeline, new WorkSlotPosition(2, GoalPhase.Coding, 1)));
    }

    /// <summary>
    /// SEEDED-COLLISION, HONEST FORM 2 — THE LIVE-POSITION REFUSAL. A live seeded slot both owns
    /// the colliding ID AND occupies the position. Only the OUTCOME is asserted (the capture is
    /// refused, nothing is registered); no claim is made about which layer rejected it.
    /// </summary>
    [Fact]
    public void Capture_SeededCollidingTaskIdAtSamePosition_IsRefusedWithoutMutation()
    {
        var pipeline = CaptureFixture("add-auth", FullPlan, GoalPhase.Coding, iteration: 2);
        var pos = new WorkSlotPosition(2, GoalPhase.Coding, 1);
        Assert.True(pipeline.SeedSlotForTest("add-auth-coder-002-01-001", pos, 1, WorkSlotState.Pending));

        var before = Snapshot(pipeline);

        Assert.ThrowsAny<Exception>(() => pipeline.CaptureDispatchPosition(WorkerRole.Coder));

        Assert.Equal(before, Snapshot(pipeline));
        Assert.Single(pipeline.GetSlotsForTest());
    }

    #endregion

    #region (n) The capture — success, IDs, counters and pointer independence

    /// <summary>
    /// THE LOWERCASE ID VECTOR: goal <c>add-auth</c>, iteration 2, occurrence 1, attempt 1 →
    /// <c>add-auth-coder-002-01-001</c>, with the goal ID embedded VERBATIM.
    /// </summary>
    [Fact]
    public void Capture_Success_BuildsTheExactLowercaseTaskId()
    {
        var pipeline = CaptureFixture("add-auth", FullPlan, GoalPhase.Coding, iteration: 2);

        var built = pipeline.CaptureDispatchPosition(WorkerRole.Coder);

        Assert.Equal("add-auth-coder-002-01-001", built.TaskId);
        Assert.StartsWith("add-auth-", built.TaskId, StringComparison.Ordinal);
        Assert.Equal(new WorkSlotPosition(2, GoalPhase.Coding, 1), built.Position);
        Assert.Equal(1, built.Attempt);
        Assert.Equal(
            new WorkSlotView(new WorkSlot(built.TaskId, built.Position, 1), WorkSlotState.Pending),
            Assert.Single(pipeline.GetSlotsForTest()));
    }

    /// <summary>A goal ID with mixed case and separators is embedded VERBATIM — never normalised.</summary>
    [Fact]
    public void Capture_Success_EmbedsTheGoalIdVerbatim()
    {
        var pipeline = CaptureFixture("Add_Auth.v2", FullPlan, GoalPhase.Review);

        var built = pipeline.CaptureDispatchPosition(WorkerRole.Reviewer);

        Assert.Equal("Add_Auth.v2-reviewer-001-01-001", built.TaskId);
    }

    /// <summary>
    /// THE ID-ATTEMPT CONSISTENCY PROOF (the atomic derivation). The attempt parsed out of the
    /// returned task ID equals the returned <c>SlotBuildResult.Attempt</c> equals the committed
    /// counter — for BOTH the 001 vector and the 002 vector. A predicted-then-allocated
    /// implementation could return <c>…-001</c> with Attempt 2; this pins that it cannot.
    /// </summary>
    [Fact]
    public void Capture_IdAttemptConsistency_HoldsForBothThe001AndThe002Vector()
    {
        var pipeline = CaptureFixture("add-auth", FullPlan, GoalPhase.Coding, iteration: 2);
        var pos = new WorkSlotPosition(2, GoalPhase.Coding, 1);

        // ── The 001 vector: a fresh position ──────────────────────────────────────────
        var first = pipeline.CaptureDispatchPosition(WorkerRole.Coder);
        Assert.Equal("add-auth-coder-002-01-001", first.TaskId);
        Assert.Equal(1, first.Attempt);
        Assert.Equal(first.Attempt, AttemptFromTaskId(first.TaskId));

        // ── The 002 vector: the helper-allocated 001 → the dead transition → the capture ──
        pipeline.ClearRegistryForTest();
        var helperAllocated = pipeline.AllocateAttemptAndRegisterSlot("add-auth-coder-002-01-001", pos);
        Assert.Equal(1, helperAllocated.Attempt);
        Assert.True(pipeline.ForceSlotStateForTest(helperAllocated.TaskId, WorkSlotState.Recorded));

        var second = pipeline.CaptureDispatchPosition(WorkerRole.Coder);
        Assert.Equal("add-auth-coder-002-01-002", second.TaskId);
        Assert.Equal(2, second.Attempt);
        Assert.Equal(second.Attempt, AttemptFromTaskId(second.TaskId));

        // The COMMITTED counter agrees with both: the next allocation takes 3.
        Assert.True(pipeline.ForceSlotStateForTest(second.TaskId, WorkSlotState.Recorded));
        Assert.Equal(3, ProbeAttempt(pipeline, pos));
    }

    /// <summary>Per-position counters stay independent across captures at different positions.</summary>
    [Fact]
    public void Capture_DistinctPositions_CountersAreIndependent()
    {
        var pipeline = CaptureFixture(installedPlan: FullPlan, machinePhase: GoalPhase.Coding);

        var coding = pipeline.CaptureDispatchPosition(WorkerRole.Coder);
        Assert.Equal(1, coding.Attempt);

        // Move BOTH the machine and the pipeline to Testing: a different position entirely.
        pipeline.StateMachine.RestoreFromPlan(FullPlan, GoalPhase.Testing);
        pipeline.AdvanceTo(GoalPhase.Testing);

        var testing = pipeline.CaptureDispatchPosition(WorkerRole.Tester);
        Assert.Equal(1, testing.Attempt);
        Assert.Equal("goal-1-tester-001-01-001", testing.TaskId);
        Assert.Equal(new WorkSlotPosition(1, GoalPhase.Testing, 1), testing.Position);
    }

    /// <summary>
    /// POINTER INDEPENDENCE: the capture neither reads nor writes <c>ActiveTaskId</c> — a success
    /// leaves a pre-existing pointer exactly as it was, and never installs its own.
    /// </summary>
    [Fact]
    public void Capture_DoesNotTouchActiveTaskId()
    {
        var pipeline = CaptureFixture(installedPlan: FullPlan, machinePhase: GoalPhase.Coding);
        Assert.Null(pipeline.ActiveTaskId);

        // (i) With no pointer set, a success does not install one.
        var built = pipeline.CaptureDispatchPosition(WorkerRole.Coder);
        Assert.Null(pipeline.ActiveTaskId);
        Assert.NotEqual("", built.TaskId);

        // (ii) With a pointer set, the next capture leaves it untouched.
        pipeline.SetActiveTask("some-other-task");
        Assert.True(pipeline.ForceSlotStateForTest(built.TaskId, WorkSlotState.Recorded));
        pipeline.CaptureDispatchPosition(WorkerRole.Coder);
        Assert.Equal("some-other-task", pipeline.ActiveTaskId);

        // (iii) And a refusal does not clear it either.
        Assert.Throws<WorkSlotException>(() => pipeline.CaptureDispatchPosition(WorkerRole.Tester));
        Assert.Equal("some-other-task", pipeline.ActiveTaskId);
    }

    #endregion

    #region (o) The _installedPhases lifecycle table

    /// <summary>Builds the snapshot the restoring constructor consumes.</summary>
    private static PipelineSnapshot SnapshotOf(GoalPhase phase, IterationPlan? plan, string goalId = "goal-1") =>
        new()
        {
            GoalId = goalId,
            Description = "Test goal",
            Goal = new Goal { Id = goalId, Description = "Test goal" },
            Phase = phase,
            Iteration = 1,
            Plan = plan,
        };

    private static GoalPipeline RestoredWithoutPlan(GoalPhase phase) =>
        new(SnapshotOf(phase, plan: null));

    /// <summary>ROW 1 — the fresh constructor installs nothing and registers nothing.</summary>
    [Fact]
    public void Lifecycle_FreshConstructor_InstallsNothingAndRegistersNothing()
    {
        var pipeline = NewPipeline();

        Assert.Null(pipeline.InstalledPhasesForTest);
        Assert.Empty(pipeline.GetSlotsForTest());
    }

    /// <summary>
    /// ROW 2 — the snapshot constructor WITH a plan installs a defensive COPY of the plan's phases
    /// (mutating the source list afterwards cannot reach it) and registers no slots.
    /// </summary>
    [Fact]
    public void Lifecycle_SnapshotWithPlan_InstallsADetachedCopy()
    {
        var sourcePhases = new List<GoalPhase>(FullPlan);
        var snapshot = SnapshotOf(GoalPhase.Coding, new IterationPlan { Phases = sourcePhases });

        var pipeline = new GoalPipeline(snapshot);

        Assert.Equal(FullPlan, pipeline.InstalledPhasesForTest);
        Assert.Empty(pipeline.GetSlotsForTest());

        // MUTATION PROOF: rewriting the SOURCE plan's list does not change the installed copy.
        sourcePhases[0] = GoalPhase.DocWriting;
        sourcePhases.Add(GoalPhase.Improve);
        Assert.Equal(FullPlan, pipeline.InstalledPhasesForTest);

        // …and the existing machine restore still happened.
        Assert.Equal(GoalPhase.Coding, pipeline.StateMachine.Phase);
    }

    /// <summary>
    /// ROW 3 — THE NO-PLAN SYNC. Without a plan nothing is installed, yet the machine is driven to
    /// the restored phase with an EMPTY queue, for every phase case.
    /// </summary>
    [Theory]
    [InlineData(GoalPhase.Coding)]
    [InlineData(GoalPhase.Planning)]
    [InlineData(GoalPhase.Merging)]
    [InlineData(GoalPhase.Done)]
    [InlineData(GoalPhase.Failed)]
    public void Lifecycle_SnapshotWithoutPlan_SyncsTheMachineWithAnEmptyQueue(GoalPhase phase)
    {
        var pipeline = RestoredWithoutPlan(phase);

        Assert.Null(pipeline.InstalledPhasesForTest);
        Assert.Null(pipeline.Plan);
        Assert.Equal(phase, pipeline.StateMachine.Phase);
        Assert.Equal(phase, pipeline.Phase);
        Assert.Empty(pipeline.StateMachine.RemainingPhases);
        Assert.Empty(pipeline.GetSlotsForTest());
    }

    /// <summary>
    /// ROW 3, THE CLASSIFICATIONS. A no-plan restoration at a WORKER phase yields PlanUnavailable;
    /// at any other phase it yields InvalidPhase.
    /// </summary>
    [Theory]
    [InlineData(GoalPhase.Coding, EvPlanUnavailable)]
    [InlineData(GoalPhase.Planning, EvInvalidPhase)]
    [InlineData(GoalPhase.Merging, EvInvalidPhase)]
    [InlineData(GoalPhase.Done, EvInvalidPhase)]
    [InlineData(GoalPhase.Failed, EvInvalidPhase)]
    public void Lifecycle_SnapshotWithoutPlan_CaptureClassifiesHonestly(GoalPhase phase, int expectedEvent)
    {
        var pipeline = RestoredWithoutPlan(phase);

        var ex = Assert.Throws<WorkSlotException>(() => pipeline.CaptureDispatchPosition(WorkerRole.Coder));

        Assert.Equal(Ev(expectedEvent), ex.Event);
        Assert.Equal(phase, ex.Position.Phase);
        Assert.Empty(pipeline.GetSlotsForTest());
    }

    /// <summary>
    /// ROW 4 — SetPlan: pendings are abandoned, CLAIMED work is exempt, the installed list is a
    /// detached copy, and the plan becomes visible.
    /// </summary>
    [Fact]
    public void Lifecycle_SetPlan_AbandonsPendingsKeepsClaimedAndInstallsACopy()
    {
        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("pending", pos, 1, WorkSlotState.Pending));
        Assert.True(pipeline.SeedSlotForTest("claimed", Position(occurrence: 2), 1, WorkSlotState.Claimed));
        Assert.True(pipeline.SeedSlotForTest("recorded", Position(occurrence: 3), 1, WorkSlotState.Recorded));

        var sourcePhases = new List<GoalPhase>(FullPlan);
        var plan = new IterationPlan { Phases = sourcePhases };

        pipeline.SetPlan(plan);

        Assert.Same(plan, pipeline.Plan);
        Assert.Equal(FullPlan, pipeline.InstalledPhasesForTest);

        // MUTATION PROOF on the SetPlan path too.
        sourcePhases.Clear();
        Assert.Equal(FullPlan, pipeline.InstalledPhasesForTest);

        Assert.Equal(
            [
                new WorkSlotView(new WorkSlot("pending", pos, 1), WorkSlotState.Abandoned),
                new WorkSlotView(new WorkSlot("claimed", Position(occurrence: 2), 1), WorkSlotState.Claimed),
                new WorkSlotView(new WorkSlot("recorded", Position(occurrence: 3), 1), WorkSlotState.Recorded),
            ],
            Snapshot(pipeline));
    }

    /// <summary>
    /// ROW 4, THE NULL CONTRACT: a null plan throws <see cref="ArgumentNullException"/> BEFORE any
    /// mutation — no slot is abandoned, the installed list is untouched, and Plan keeps its value.
    /// </summary>
    [Fact]
    public void Lifecycle_SetPlanNull_ThrowsBeforeAnyMutation()
    {
        var pipeline = NewPipeline();
        var original = new IterationPlan { Phases = [.. FullPlan] };
        pipeline.SetPlan(original);

        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("pending", pos, 1, WorkSlotState.Pending));
        var before = Snapshot(pipeline);

        Assert.Throws<ArgumentNullException>(() => pipeline.SetPlan(null!));

        // NO abandonment happened…
        Assert.Equal(before, Snapshot(pipeline));
        Assert.Equal(
            new WorkSlotView(new WorkSlot("pending", pos, 1), WorkSlotState.Pending),
            Assert.Single(pipeline.GetSlotsForTest()));

        // …and NO plan change happened.
        Assert.Same(original, pipeline.Plan);
        Assert.Equal(FullPlan, pipeline.InstalledPhasesForTest);
    }

    /// <summary>ROW 5 — ClearPlan drops both the plan and the installed list, and abandons pendings.</summary>
    [Fact]
    public void Lifecycle_ClearPlan_DropsPlanAndInstalledPhasesAndAbandonsPendings()
    {
        var pipeline = NewPipeline();
        pipeline.SetPlan(new IterationPlan { Phases = [.. FullPlan] });

        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("pending", pos, 1, WorkSlotState.Pending));
        Assert.True(pipeline.SeedSlotForTest("claimed", Position(occurrence: 2), 1, WorkSlotState.Claimed));

        pipeline.ClearPlan();

        Assert.Null(pipeline.Plan);
        Assert.Null(pipeline.InstalledPhasesForTest);
        Assert.Equal(
            [
                new WorkSlotView(new WorkSlot("pending", pos, 1), WorkSlotState.Abandoned),
                new WorkSlotView(new WorkSlot("claimed", Position(occurrence: 2), 1), WorkSlotState.Claimed),
            ],
            Snapshot(pipeline));
    }

    /// <summary>
    /// ROW 6 — the TERMINAL-ONLY rule, observable half: reaching Done or Failed abandons every
    /// pending slot while claimed work stays exempt, and the terminal phase is reached.
    /// </summary>
    [Theory]
    [InlineData(GoalPhase.Done)]
    [InlineData(GoalPhase.Failed)]
    public void Lifecycle_AdvanceToTerminal_AbandonsPendingsAndKeepsClaimed(GoalPhase terminal)
    {
        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("pending", pos, 1, WorkSlotState.Pending));
        Assert.True(pipeline.SeedSlotForTest("claimed", Position(occurrence: 2), 1, WorkSlotState.Claimed));

        pipeline.AdvanceTo(terminal);

        Assert.Equal(terminal, pipeline.Phase);
        Assert.NotNull(pipeline.CompletedAt);
        Assert.Equal(
            [
                new WorkSlotView(new WorkSlot("pending", pos, 1), WorkSlotState.Abandoned),
                new WorkSlotView(new WorkSlot("claimed", Position(occurrence: 2), 1), WorkSlotState.Claimed),
            ],
            Snapshot(pipeline));
    }

    /// <summary>ROW 6 — every NON-terminal AdvanceTo leaves the registry completely untouched.</summary>
    [Theory]
    [InlineData(GoalPhase.Planning)]
    [InlineData(GoalPhase.Coding)]
    [InlineData(GoalPhase.Testing)]
    [InlineData(GoalPhase.DocWriting)]
    [InlineData(GoalPhase.Review)]
    [InlineData(GoalPhase.Improve)]
    [InlineData(GoalPhase.Merging)]
    public void Lifecycle_AdvanceToNonTerminal_AbandonsNothing(GoalPhase phase)
    {
        var pipeline = NewPipeline();
        Assert.True(pipeline.SeedSlotForTest("pending", Position(), 1, WorkSlotState.Pending));
        var before = Snapshot(pipeline);

        pipeline.AdvanceTo(phase);

        Assert.Equal(phase, pipeline.Phase);
        Assert.Equal(before, Snapshot(pipeline));
    }

    /// <summary>
    /// ROW 6 — the CODE-STRUCTURE half of the terminal rule, read off the compiled artifact: the
    /// <c>AbandonPendingSlots</c> call site PRECEDES the <c>Phase</c> assignment in
    /// <c>AdvanceTo</c>'s emitted IL, so no observer can ever see a terminal pipeline that still
    /// carries pending slots.
    /// </summary>
    [Fact]
    public void Lifecycle_AdvanceTo_CallsAbandonPendingSlotsBeforeAssigningThePhase()
    {
        var calls = DecodeCallSites(RegistryMethod("AdvanceTo"));

        var abandon = calls.FirstOrDefault(c =>
            c.Target.DeclaringType == typeof(GoalPipeline) && c.Target.Name == "AbandonPendingSlots");
        var assignPhase = calls.FirstOrDefault(c =>
            c.Target.DeclaringType == typeof(GoalPipeline) && c.Target.Name == "set_Phase");

        Assert.True(abandon is not null, "AdvanceTo no longer calls AbandonPendingSlots at all.");
        Assert.True(assignPhase is not null, "AdvanceTo no longer assigns Phase through its setter.");
        Assert.True(
            abandon!.Offset < assignPhase!.Offset,
            $"AdvanceTo assigns Phase at IL offset {assignPhase.Offset} BEFORE abandoning pending slots at " +
            $"{abandon.Offset} — the terminal abandonment must precede the phase assignment.");
    }

    #endregion

    #region (p) THE LOCK ORDER — a capture parked behind an in-flight transition

    /// <summary>
    /// Runs one parked-transition round. The A0 <c>OnTransitionForTest</c> seam holds the machine
    /// lock while a capture thread is launched; the capture parks inside
    /// <c>StateMachine.CapturePosition</c> until the hook returns and the transition releases.
    /// <para>
    /// Only the two achievable facts are observed and returned: whether the capture completed
    /// while the transition was parked (it must not), and what it ultimately produced. NOTHING is
    /// claimed about what happens BETWEEN the release and the completion.
    /// </para>
    /// </summary>
    private static (bool AttemptObserved, bool CompletedWhileParked, SlotBuildResult? Result, Exception? Error)
        RunParkedCapture(bool aligned)
    {
        // Machine at Coding; the parked transition's post-state is Testing.
        // ALIGNED: the pipeline is already at Testing. DIVERGED: it stays at Coding.
        var pipeline = CaptureFixture(
            installedPlan: [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging],
            machinePhase: GoalPhase.Coding,
            pipelinePhase: aligned ? GoalPhase.Testing : GoalPhase.Coding);

        using var captureAttempted = new ManualResetEventSlim(false);
        using var captureCompleted = new ManualResetEventSlim(false);
        SlotBuildResult? result = null;
        Exception? error = null;

        var worker = new Thread(() =>
        {
            captureAttempted.Set();                                       // signalled immediately before…
            try
            {
                Volatile.Write(ref result, pipeline.CaptureDispatchPosition(WorkerRole.Tester));
            }
            catch (Exception ex)
            {
                Volatile.Write(ref error, ex);
            }
            captureCompleted.Set();
        })
        {
            IsBackground = true,
            Name = "work-slot-parked-capture",
        };

        var attemptObserved = false;
        var completedWhileParked = false;

        // The hook runs UNDER the machine lock, so the capture provably parks inside it.
        // No assertion is made here: anything the hook throws would escape through Transition.
        pipeline.StateMachine.OnTransitionForTest = () =>
        {
            worker.Start();
#pragma warning disable xUnit1051 // Timeout-only waits are intentional: the fixed bound IS the proof
            attemptObserved = captureAttempted.Wait(WaitTimeout);
            completedWhileParked = captureCompleted.Wait(BlockedGrace);
#pragma warning restore xUnit1051
        };

        pipeline.StateMachine.Transition(PhaseInput.Succeeded); // Coding → Testing; releases the lock
        pipeline.StateMachine.OnTransitionForTest = null;

        Assert.True(worker.Join(WaitTimeout), "The parked capture never completed after the transition released.");

        return (attemptObserved, completedWhileParked, Volatile.Read(ref result), Volatile.Read(ref error));
    }

    /// <summary>
    /// THE ALIGNED VARIANT: the pipeline's phase agrees with the parked transition's post-state,
    /// so once the machine lock is released the capture completes SUCCESSFULLY and coherently at
    /// the post-transition position. Repeated so a scheduler-dependent regression cannot pass by
    /// luck.
    /// </summary>
    [Fact]
    public void LockOrder_AlignedPipeline_CaptureBlocksWhileParkedThenSucceeds()
    {
        for (var round = 0; round < 5; round++)
        {
            var (attemptObserved, completedWhileParked, result, error) = RunParkedCapture(aligned: true);

            Assert.True(attemptObserved, $"Round {round}: the capture thread never signalled its attempt.");
            Assert.False(
                completedWhileParked,
                $"Round {round}: the capture completed while the transition held the machine lock.");

            Assert.Null(error);
            Assert.NotNull(result);
            Assert.Equal(new WorkSlotPosition(1, GoalPhase.Testing, 1), result.Position);
            Assert.Equal(1, result.Attempt);
            Assert.Equal("goal-1-tester-001-01-001", result.TaskId);
        }
    }

    /// <summary>
    /// THE DIVERGED VARIANT: the pipeline is left at the OLD phase, so after the release the
    /// capture reports a coherent <c>PhaseDivergence</c> naming both sides — the pipeline's Coding
    /// and the machine's post-transition Testing. Repeated for the same reason.
    /// </summary>
    [Fact]
    public void LockOrder_DivergedPipeline_CaptureBlocksWhileParkedThenThrowsPhaseDivergence()
    {
        for (var round = 0; round < 5; round++)
        {
            var (attemptObserved, completedWhileParked, result, error) = RunParkedCapture(aligned: false);

            Assert.True(attemptObserved, $"Round {round}: the capture thread never signalled its attempt.");
            Assert.False(
                completedWhileParked,
                $"Round {round}: the capture completed while the transition held the machine lock.");

            Assert.Null(result);
            var ex = Assert.IsType<WorkSlotException>(error);
            Assert.Equal(WorkSlotEvent.PhaseDivergence, ex.Event);
            Assert.Equal(GoalPhase.Coding, ex.PipelinePhase);
            Assert.Equal(GoalPhase.Testing, ex.MachinePhase);
            Assert.Equal(new WorkSlotPosition(1, GoalPhase.Testing, 1), ex.Position);
        }
    }

    #endregion

    #region (q) TaskBuilder — the taskId parameter

    private static WorkTask BuildTask(string? taskId) =>
        new TaskBuilder(new BranchCoordinator()).Build(
            "goal-1", "Test goal", WorkerRole.Coder, 1,
            [new TargetRepository { Name = "CopilotHive", Url = "https://example.invalid/r.git", DefaultBranch = "main" }],
            "Do the work.", BranchAction.Create,
            taskId: taskId);

    /// <summary>A null, empty or whitespace task ID falls back to the legacy generated form.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TaskBuilder_BlankTaskId_FallsBackToTheLegacyId(string? taskId)
    {
        Assert.Equal("goal-1-coder-001", BuildTask(taskId).TaskId);
    }

    /// <summary>The omitted parameter keeps every existing call site on the legacy behaviour.</summary>
    [Fact]
    public void TaskBuilder_OmittedTaskId_KeepsTheLegacyId()
    {
        var task = new TaskBuilder(new BranchCoordinator()).Build(
            "goal-1", "Test goal", WorkerRole.Coder, 1,
            [new TargetRepository { Name = "CopilotHive", Url = "https://example.invalid/r.git", DefaultBranch = "main" }],
            "Do the work.", BranchAction.Create);

        Assert.Equal("goal-1-coder-001", task.TaskId);
    }

    /// <summary>A non-blank task ID is used VERBATIM — never reformatted, never normalised.</summary>
    [Theory]
    [InlineData("add-auth-coder-002-01-001")]
    [InlineData("Add_Auth.v2-reviewer-001-01-007")]
    [InlineData("x")]
    public void TaskBuilder_NonBlankTaskId_IsUsedVerbatim(string taskId)
    {
        Assert.Equal(taskId, BuildTask(taskId).TaskId);
    }

    #endregion

    #region (r) THE ATOMIC-DERIVATION PROOF UNDER GENUINE CONCURRENCY

    // ══════════════════════════════════════════════════════════════════════════════════
    //  WHY THIS REGION EXISTS.
    //
    //  The sequential 001/002 vectors in region (n) pin the RESULT of the atomic
    //  derivation but cannot pin its ATOMICITY: an implementation that reads the next
    //  attempt in one short lock, builds the task ID from that PREDICTION outside the
    //  lock, and only later allocates through A1a's helper produces byte-identical
    //  results when nothing else is running. Single-threaded execution simply never
    //  opens the window between the prediction and the commit.
    //
    //  The decisive vector is therefore real interleaving at the allocation boundary:
    //  many threads racing the SAME position at once, so the prediction window is
    //  entered concurrently. Under the true in-lock derivation the attempt is read,
    //  stamped into the ID, committed to the counter and registered inside ONE lock
    //  span, so two threads can NEVER obtain the same attempt. Under the prediction
    //  race two threads read the same "next" value and then both try to use it, which
    //  surfaces as at least one of the invariants below breaking.
    //
    //  DETERMINISM: the start is a Barrier rendezvous on dedicated LongRunning threads
    //  — no Task.Delay, no polling, no sleep, and no assertion depends on WHICH thread
    //  wins. Every wait is bounded, so a regression fails loudly instead of hanging.
    //  The assertions are pure outcome invariants over the SET of results, which hold
    //  for every possible legal interleaving.
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>Threads used by the contention races below; comfortably oversubscribes the CI box.</summary>
    private const int RaceThreads = 16;

    /// <summary>Capture rounds each racing thread performs on the contended position.</summary>
    private const int RaceRoundsPerThread = 40;

    /// <summary>
    /// The outcome of a single racing capture: exactly one of these three is populated.
    /// </summary>
    /// <param name="Result">The successful build result, or <c>null</c>.</param>
    /// <param name="DoubleAssignment">
    /// <c>true</c> when the capture was refused because the position was momentarily LIVE —
    /// the expected, legal refusal in a contended race.
    /// </param>
    /// <param name="Unexpected">Any other exception; must never occur.</param>
    private sealed record RaceOutcome(SlotBuildResult? Result, bool DoubleAssignment, Exception? Unexpected);

    /// <summary>
    /// Runs <paramref name="threadCount"/> dedicated threads that all rendezvous on a
    /// <see cref="Barrier"/> and then hammer the SAME <paramref name="position"/> with captures.
    /// <para>
    /// Each successful capture immediately retires its own slot to
    /// <see cref="WorkSlotState.Recorded"/>, which frees the position for the next contender —
    /// that is what keeps the race going for many rounds instead of stopping after the first
    /// winner. A losing thread sees the winner's still-live slot and takes the legal
    /// <see cref="WorkSlotEvent.DoubleAssignment"/> refusal.
    /// </para>
    /// </summary>
    private static List<RaceOutcome> RaceCapturesAtOnePosition(
        GoalPipeline pipeline,
        WorkSlotPosition position,
        WorkerRole role,
        int threadCount = RaceThreads,
        int roundsPerThread = RaceRoundsPerThread)
    {
        var outcomes = new List<RaceOutcome>[threadCount];
        using var barrier = new Barrier(threadCount);
        var threads = new Thread[threadCount];

        for (var t = 0; t < threadCount; t++)
        {
            var index = t;
            outcomes[index] = new List<RaceOutcome>(roundsPerThread);

            threads[index] = new Thread(() =>
            {
                // THE RENDEZVOUS: every thread is released at the same instant, so the
                // prediction window is entered concurrently rather than one-at-a-time.
                barrier.SignalAndWait();

                for (var round = 0; round < roundsPerThread; round++)
                {
                    try
                    {
                        var built = pipeline.CaptureDispatchPosition(role);
                        outcomes[index].Add(new RaceOutcome(built, DoubleAssignment: false, Unexpected: null));

                        // Retire immediately so the position becomes contendable again.
                        pipeline.ForceSlotStateForTest(built.TaskId, WorkSlotState.Recorded);
                    }
                    catch (WorkSlotException ex) when (ex.Event == WorkSlotEvent.DoubleAssignment)
                    {
                        // The legal refusal: another thread held the position at that instant.
                        outcomes[index].Add(new RaceOutcome(null, DoubleAssignment: true, Unexpected: null));
                    }
                    catch (Exception ex)
                    {
                        outcomes[index].Add(new RaceOutcome(null, DoubleAssignment: false, Unexpected: ex));
                    }
                }
            })
            {
                IsBackground = true,
                Name = $"work-slot-race-{index}",
            };
        }

        foreach (var thread in threads)
            thread.Start();

        foreach (var thread in threads)
            Assert.True(thread.Join(RaceTimeout), "A racing capture thread never finished.");

        return [.. outcomes.SelectMany(o => o)];
    }

    /// <summary>Generous bound for the whole race — a hang is a failure, not a slow test.</summary>
    private static readonly TimeSpan RaceTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// THE ATOMIC-DERIVATION PROOF. Sixteen threads race captures at ONE position, released
    /// together by a barrier. Four outcome invariants are asserted INDIVIDUALLY, so a failure
    /// names the defect instead of reporting a generic mismatch:
    /// <list type="number">
    ///   <item>NO UNEXPECTED EXCEPTION — in particular no duplicate-ID
    ///     <see cref="ArgumentException"/>. The real implementation derives the ID from the
    ///     attempt it is committing inside the same lock span, so the ID is unique by
    ///     construction and that failure is unreachable. A prediction race builds the ID from a
    ///     value another thread may already have consumed, and because retired slots stay in the
    ///     registry the stale ID collides — the helper then throws ArgumentException.</item>
    ///   <item>SELF-CONSISTENCY — every returned result's parsed TaskId suffix equals its own
    ///     <see cref="SlotBuildResult.Attempt"/>. This is the 001-vs-2 divergence, asserted per
    ///     result.</item>
    ///   <item>UNIQUENESS — no two successful captures share an attempt, and no two share a
    ///     TaskId. Two threads reading the same "next attempt" outside the lock breaks this.</item>
    ///   <item>COUNTER AGREEMENT — the committed counter equals the number of successes exactly,
    ///     so the attempts form the contiguous run 1..N with nothing skipped or reused.</item>
    /// </list>
    /// <para>
    /// The race is also required to be REAL: at least one capture must succeed and the run must
    /// produce a meaningful number of successes, so the test can never pass vacuously by having
    /// every thread refused.
    /// </para>
    /// </summary>
    [Fact]
    public void Capture_ConcurrentSamePosition_IdAttemptAndCounterAreDerivedAtomically()
    {
        var pipeline = CaptureFixture("add-auth", FullPlan, GoalPhase.Coding, iteration: 2);
        var pos = new WorkSlotPosition(2, GoalPhase.Coding, 1);

        var outcomes = RaceCapturesAtOnePosition(pipeline, pos, WorkerRole.Coder);

        // ── (1) NO UNEXPECTED EXCEPTION ────────────────────────────────────────────────
        var unexpected = outcomes.Where(o => o.Unexpected is not null).Select(o => o.Unexpected!).ToList();
        Assert.True(
            unexpected.Count == 0,
            "A racing capture threw an exception that atomic derivation makes unreachable — " +
            $"{unexpected.Count} of {outcomes.Count} captures failed unexpectedly. First: " +
            $"{unexpected.FirstOrDefault()?.GetType().Name}: {unexpected.FirstOrDefault()?.Message}. " +
            "A duplicate-ID ArgumentException here means the task ID was built from a PREDICTED " +
            "attempt outside the allocation lock, so a stale ID collided with an already-registered slot.");

        var results = outcomes.Where(o => o.Result is not null).Select(o => o.Result!).ToList();

        // THE RACE MUST BE REAL — never a vacuous pass.
        Assert.True(results.Count > 0, "No capture succeeded — the race proved nothing.");

        // ── (2) SELF-CONSISTENCY, per result ───────────────────────────────────────────
        var inconsistent = results
            .Where(r => AttemptFromTaskId(r.TaskId) != r.Attempt)
            .ToList();
        Assert.True(
            inconsistent.Count == 0,
            $"{inconsistent.Count} result(s) carry a TaskId whose attempt suffix differs from the returned " +
            $"Attempt — e.g. TaskId '{inconsistent.FirstOrDefault()?.TaskId}' vs Attempt " +
            $"{inconsistent.FirstOrDefault()?.Attempt}. The ID was not built from the attempt that was " +
            "actually allocated, so the two were not born in one lock span.");

        // ── (3) UNIQUENESS of both the attempt and the ID ──────────────────────────────
        var duplicateAttempts = results
            .GroupBy(r => r.Attempt)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .OrderBy(a => a)
            .ToList();
        Assert.True(
            duplicateAttempts.Count == 0,
            $"Attempt number(s) [{string.Join(", ", duplicateAttempts)}] were handed to more than one " +
            "successful capture — two threads read the same next attempt outside the allocation lock.");

        var duplicateIds = results
            .GroupBy(r => r.TaskId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.True(
            duplicateIds.Count == 0,
            $"Task ID(s) [{string.Join(", ", duplicateIds)}] were produced by more than one successful " +
            "capture — the ID was derived from a predicted, non-exclusive attempt.");

        // ── (4) COUNTER AGREEMENT: the counter committed exactly one attempt per success ──
        // Every successful slot was retired by its own thread, so the position is free and the
        // probe's attempt reveals the committed counter: successes + 1.
        Assert.Equal(results.Count + 1, ProbeAttempt(pipeline, pos));

        // …which, with uniqueness above, means the attempts are exactly the contiguous run 1..N.
        Assert.Equal(
            Enumerable.Range(1, results.Count),
            results.Select(r => r.Attempt).OrderBy(a => a));
    }

    /// <summary>
    /// The same race run against a position seeded with a DEAD predecessor, so every thread is
    /// live-eligible from the very first round and the contention starts at attempt 2. This
    /// widens the prediction window (there is a non-zero counter to mis-predict from the outset)
    /// while pinning the same four invariants.
    /// </summary>
    [Fact]
    public void Capture_ConcurrentSamePositionWithDeadPredecessor_StillDerivesAtomically()
    {
        var pipeline = CaptureFixture("add-auth", FullPlan, GoalPhase.Coding, iteration: 2);
        var pos = new WorkSlotPosition(2, GoalPhase.Coding, 1);

        // A helper-allocated attempt 1, retired: the counter starts at 1 and the position is free.
        var seeded = pipeline.AllocateAttemptAndRegisterSlot("add-auth-coder-002-01-001", pos);
        Assert.Equal(1, seeded.Attempt);
        Assert.True(pipeline.ForceSlotStateForTest(seeded.TaskId, WorkSlotState.Recorded));

        var outcomes = RaceCapturesAtOnePosition(pipeline, pos, WorkerRole.Coder);

        var unexpected = outcomes.Where(o => o.Unexpected is not null).Select(o => o.Unexpected!).ToList();
        Assert.True(
            unexpected.Count == 0,
            $"{unexpected.Count} racing capture(s) threw unexpectedly. First: " +
            $"{unexpected.FirstOrDefault()?.GetType().Name}: {unexpected.FirstOrDefault()?.Message}.");

        var results = outcomes.Where(o => o.Result is not null).Select(o => o.Result!).ToList();
        Assert.True(results.Count > 0, "No capture succeeded — the race proved nothing.");

        var inconsistent = results.Where(r => AttemptFromTaskId(r.TaskId) != r.Attempt).ToList();
        Assert.True(
            inconsistent.Count == 0,
            $"{inconsistent.Count} result(s) carry a TaskId suffix differing from the returned Attempt — " +
            $"e.g. '{inconsistent.FirstOrDefault()?.TaskId}' vs {inconsistent.FirstOrDefault()?.Attempt}.");

        Assert.Distinct(results.Select(r => r.Attempt));
        Assert.Distinct(results.Select(r => r.TaskId), StringComparer.Ordinal);

        // The counter absorbed the seeded 1 plus exactly one per success.
        Assert.Equal(results.Count + 2, ProbeAttempt(pipeline, pos));

        // Contention genuinely began past the seeded attempt.
        Assert.DoesNotContain(1, results.Select(r => r.Attempt));
    }

    /// <summary>
    /// THE CONCURRENT HONEST-FORM RE-CHECK. Two threads race a capture at a position already held
    /// by a LIVE seeded slot whose task ID is exactly the one the capture would build — both the
    /// duplicate-ID arbiter and the LIVE-position rule apply at once.
    /// <para>
    /// Only the OUTCOME is asserted, per the goal's honest form: BOTH threads are refused, no
    /// third outcome ever appears, and the registry is byte-identical afterwards. No claim is made
    /// about WHICH layer rejected either thread.
    /// </para>
    /// </summary>
    [Fact]
    public void Capture_ConcurrentSeededLiveCollision_BothThreadsAreRefusedAndNothingChanges()
    {
        var pipeline = CaptureFixture("add-auth", FullPlan, GoalPhase.Coding, iteration: 2);
        var pos = new WorkSlotPosition(2, GoalPhase.Coding, 1);
        Assert.True(pipeline.SeedSlotForTest("add-auth-coder-002-01-001", pos, 1, WorkSlotState.Pending));

        var before = Snapshot(pipeline);

        const int threads = 2;
        var succeeded = 0;
        var refused = 0;
        using var barrier = new Barrier(threads);
        var workers = new Thread[threads];

        for (var t = 0; t < threads; t++)
        {
            workers[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    pipeline.CaptureDispatchPosition(WorkerRole.Coder);
                    Interlocked.Increment(ref succeeded);
                }
                catch (Exception)
                {
                    // The honest form: a refusal is a refusal — the layer is not claimed.
                    Interlocked.Increment(ref refused);
                }
            })
            {
                IsBackground = true,
                Name = "work-slot-collision-racer",
            };
        }

        foreach (var worker in workers)
            worker.Start();
        foreach (var worker in workers)
            Assert.True(worker.Join(RaceTimeout), "A colliding capture thread never finished.");

        Assert.Equal(0, Volatile.Read(ref succeeded));
        Assert.Equal(threads, Volatile.Read(ref refused));

        // No third outcome, and the registry is untouched: the seeded slot alone, unchanged.
        Assert.Equal(before, Snapshot(pipeline));
        Assert.Single(pipeline.GetSlotsForTest());

        // No attempt was consumed by either refusal.
        Assert.True(pipeline.ForceSlotStateForTest("add-auth-coder-002-01-001", WorkSlotState.Recorded));
        Assert.Equal(1, ProbeAttempt(pipeline, pos, "probe-after-collision"));
    }

    #endregion

    #region (s) The capture-level LIVE pre-check — the layer distinction

    /// <summary>
    /// THE CAPTURE-LEVEL LIVE PRE-CHECK IS OBSERVABLE, and this pins it.
    /// <para>
    /// A LIVE slot occupies the position AND already owns the exact task ID the capture would
    /// build. Both refusal layers are therefore armed, but they are reached in a fixed order and
    /// they throw DIFFERENT types:
    /// </para>
    /// <list type="bullet">
    ///   <item>the capture's step-6 pre-check runs FIRST and reports the slot-integrity event —
    ///     <see cref="WorkSlotException"/> carrying <see cref="WorkSlotEvent.DoubleAssignment"/>
    ///     and the occupant's task ID;</item>
    ///   <item>only if that pre-check is absent does control reach the allocation helper, whose
    ///     duplicate-ID guard is evaluated BEFORE its own live-position scan and therefore throws
    ///     a plain <see cref="ArgumentException"/> instead.</item>
    /// </list>
    /// <para>
    /// So this vector distinguishes the two layers by exception TYPE: removing the capture-level
    /// pre-check changes the observed type from <c>WorkSlotException</c> to <c>ArgumentException</c>.
    /// This is deliberately SEPARATE from the honest-form test
    /// <see cref="Capture_SeededCollidingTaskIdAtSamePosition_IsRefusedWithoutMutation"/>, which
    /// asserts only the outcome and stays as the goal specified — that test is not weakened, and
    /// this one adds the layer fact it deliberately declines to claim.
    /// </para>
    /// </summary>
    [Fact]
    public void Capture_LiveSlotOwningTheProspectiveId_IsRefusedByTheCaptureLevelPreCheck()
    {
        var pipeline = CaptureFixture("add-auth", FullPlan, GoalPhase.Coding, iteration: 2);
        var pos = new WorkSlotPosition(2, GoalPhase.Coding, 1);

        // The occupant is LIVE at the position AND owns the exact prospective ID.
        const string prospectiveId = "add-auth-coder-002-01-001";
        Assert.True(pipeline.SeedSlotForTest(prospectiveId, pos, 1, WorkSlotState.Pending));

        var before = Snapshot(pipeline);

        var ex = Assert.Throws<WorkSlotException>(() => pipeline.CaptureDispatchPosition(WorkerRole.Coder));

        // THE LAYER MARKER: the slot-integrity event, NOT the helper's argument failure.
        Assert.Equal(WorkSlotEvent.DoubleAssignment, ex.Event);
        Assert.Equal(prospectiveId, ex.ExistingTaskId);
        Assert.Equal(pos, ex.Position);

        Assert.Equal(before, Snapshot(pipeline));
    }

    /// <summary>
    /// The same layer distinction with the occupant CLAIMED rather than Pending — the pre-check
    /// treats both live states identically, so the type is still the slot-integrity one.
    /// </summary>
    [Fact]
    public void Capture_ClaimedSlotOwningTheProspectiveId_IsRefusedByTheCaptureLevelPreCheck()
    {
        var pipeline = CaptureFixture("add-auth", FullPlan, GoalPhase.Coding, iteration: 2);
        var pos = new WorkSlotPosition(2, GoalPhase.Coding, 1);
        const string prospectiveId = "add-auth-coder-002-01-001";
        Assert.True(pipeline.SeedSlotForTest(prospectiveId, pos, 1, WorkSlotState.Claimed));

        var ex = Assert.Throws<WorkSlotException>(() => pipeline.CaptureDispatchPosition(WorkerRole.Coder));

        Assert.Equal(WorkSlotEvent.DoubleAssignment, ex.Event);
        Assert.Equal(prospectiveId, ex.ExistingTaskId);
    }

    #endregion

    #region (t) RetireSlotAndClearIfCurrent — the atomic retirement primitive

    // SlotRetirementOutcome is internal, so table-driven vectors carry integer codes
    // mapped back through Ret().
    public const int RetRetired = (int)SlotRetirementOutcome.Retired;
    public const int RetSlotAbsent = (int)SlotRetirementOutcome.SlotAbsent;
    public const int RetAlreadyAbandoned = (int)SlotRetirementOutcome.AlreadyAbandoned;

    private static SlotRetirementOutcome Ret(int code) => (SlotRetirementOutcome)code;

    /// <summary>
    /// The three LIVE states (Pending — the allocated-but-unclaimed dispatch, plus the seeded
    /// Claimed and Recorded ones) all retire: the slot becomes Abandoned and the outcome is
    /// <see cref="SlotRetirementOutcome.Retired"/>. The primitive is deliberately WIDER than
    /// <c>AbandonSlot</c> (Pending only) and <c>FailSlot</c> (Claimed only) — it retires whatever
    /// state the attempt happens to be in.
    /// <para>
    /// The OWNED pointer is cleared in the same operation: the pipeline names exactly this task,
    /// so retiring the attempt must not leave a live-looking dispatch pointer behind.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(StPending)]
    [InlineData(StClaimed)]
    [InlineData(StRecorded)]
    public void RetireSlotAndClearIfCurrent_LiveSlotOwningThePointer_RetiresAndClears(int stateCode)
    {
        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("t1", pos, 5, St(stateCode)));
        pipeline.SetActiveTask("t1");

        var outcome = pipeline.RetireSlotAndClearIfCurrent("t1");

        Assert.Equal(SlotRetirementOutcome.Retired, outcome);
        Assert.Equal(
            new WorkSlotView(new WorkSlot("t1", pos, 5), WorkSlotState.Abandoned),
            Assert.Single(pipeline.GetSlotsForTest()));
        Assert.Null(pipeline.ActiveTaskId);
    }

    /// <summary>
    /// The IF-CURRENT half of the contract for a live slot: when the pointer names a DIFFERENT
    /// task the retire still succeeds, but the foreign pointer survives untouched.
    /// </summary>
    [Theory]
    [InlineData(StPending)]
    [InlineData(StClaimed)]
    [InlineData(StRecorded)]
    public void RetireSlotAndClearIfCurrent_LiveSlotNotOwningThePointer_RetiresWithoutClearing(int stateCode)
    {
        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("t1", pos, 5, St(stateCode)));
        pipeline.SetActiveTask("other-task");

        var outcome = pipeline.RetireSlotAndClearIfCurrent("t1");

        Assert.Equal(SlotRetirementOutcome.Retired, outcome);
        Assert.Equal(
            new WorkSlotView(new WorkSlot("t1", pos, 5), WorkSlotState.Abandoned),
            Assert.Single(pipeline.GetSlotsForTest()));
        Assert.Equal("other-task", pipeline.ActiveTaskId);
    }

    /// <summary>
    /// THE NEWER-POINTER RACE — the load-bearing safety vector. A late cleanup retires an OLD
    /// attempt while the pipeline has already dispatched, and pointed at, a NEWER one. The old
    /// slot must retire, and the newer pointer must NOT be erased: erasing it would make a live,
    /// in-flight dispatch look idle to every observer.
    /// </summary>
    [Fact]
    public void RetireSlotAndClearIfCurrent_OlderTaskWhilePointerNamesNewerTask_LeavesNewerPointerIntact()
    {
        var pipeline = NewPipeline();
        var oldPos = Position(occurrence: 1);
        var newPos = Position(occurrence: 2);
        Assert.True(pipeline.SeedSlotForTest("task-old", oldPos, 1, WorkSlotState.Claimed));
        Assert.True(pipeline.SeedSlotForTest("task-new", newPos, 2, WorkSlotState.Pending));
        pipeline.SetActiveTask("task-new");

        var outcome = pipeline.RetireSlotAndClearIfCurrent("task-old");

        Assert.Equal(SlotRetirementOutcome.Retired, outcome);
        Assert.Equal("task-new", pipeline.ActiveTaskId);

        // Only the OLD slot moved; the newer attempt is untouched and still live.
        Assert.Equal(
            new HashSet<WorkSlotView>
            {
                new(new WorkSlot("task-old", oldPos, 1), WorkSlotState.Abandoned),
                new(new WorkSlot("task-new", newPos, 2), WorkSlotState.Pending),
            },
            Snapshot(pipeline));
    }

    /// <summary>
    /// An ABSENT slot with a non-blank id: the registry has nothing to retire, so the outcome is
    /// <see cref="SlotRetirementOutcome.SlotAbsent"/> — but the uniform if-current rule still
    /// applies, so an owned pointer IS cleared.
    /// </summary>
    [Fact]
    public void RetireSlotAndClearIfCurrent_AbsentSlotOwningThePointer_ReportsAbsentAndClears()
    {
        var pipeline = NewPipeline();
        pipeline.ClearRegistryForTest();
        pipeline.SetActiveTask("ghost");

        var outcome = pipeline.RetireSlotAndClearIfCurrent("ghost");

        Assert.Equal(SlotRetirementOutcome.SlotAbsent, outcome);
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Empty(pipeline.GetSlotsForTest());
    }

    /// <summary>
    /// The same absent-slot path when the pointer names someone else: absent, and the foreign
    /// pointer survives (the clearing is if-current ONLY, never unconditional).
    /// </summary>
    [Fact]
    public void RetireSlotAndClearIfCurrent_AbsentSlotNotOwningThePointer_ReportsAbsentWithoutClearing()
    {
        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("live-task", pos, 3, WorkSlotState.Claimed));
        pipeline.SetActiveTask("live-task");
        var before = Snapshot(pipeline);

        var outcome = pipeline.RetireSlotAndClearIfCurrent("ghost");

        Assert.Equal(SlotRetirementOutcome.SlotAbsent, outcome);
        Assert.Equal("live-task", pipeline.ActiveTaskId);
        Assert.Equal(before, Snapshot(pipeline));
    }

    /// <summary>
    /// THE BLANK-ID SAFETY CONTRACT. A null/blank id names no attempt at all, so the call performs
    /// NO mutation whatsoever — the registry is untouched AND the pointer is never cleared, not
    /// even when the pointer itself is blank-ish. An implementation that let the blank path fall
    /// through to the if-current clearing would blank out a pointer no caller can own.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RetireSlotAndClearIfCurrent_NullOrBlankTaskId_IsAbsentWithoutAnyMutation(string? taskId)
    {
        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("t1", pos, 5, WorkSlotState.Pending));
        pipeline.SetActiveTask("t1");
        var before = Snapshot(pipeline);

        var outcome = pipeline.RetireSlotAndClearIfCurrent(taskId);

        Assert.Equal(SlotRetirementOutcome.SlotAbsent, outcome);
        Assert.Equal("t1", pipeline.ActiveTaskId);
        Assert.Equal(before, Snapshot(pipeline));
    }

    /// <summary>
    /// The blank id must not clear a pointer that happens to hold the very same blank-ish value:
    /// the refusal is on the ARGUMENT, so no ordinal match can smuggle a clearing through.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RetireSlotAndClearIfCurrent_BlankTaskId_DoesNotClearAnEqualBlankPointer(string taskId)
    {
        var pipeline = NewPipeline();
        pipeline.SetActiveTask(taskId);

        var outcome = pipeline.RetireSlotAndClearIfCurrent(taskId);

        Assert.Equal(SlotRetirementOutcome.SlotAbsent, outcome);
        Assert.Equal(taskId, pipeline.ActiveTaskId);
    }

    /// <summary>
    /// An ALREADY-Abandoned slot reports <see cref="SlotRetirementOutcome.AlreadyAbandoned"/> —
    /// the registry does not change — yet the uniform if-current rule STILL clears an owned
    /// pointer, because every non-blank path clears.
    /// </summary>
    [Fact]
    public void RetireSlotAndClearIfCurrent_AlreadyAbandonedSlot_ReportsAlreadyAbandonedAndStillClears()
    {
        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("t1", pos, 5, WorkSlotState.Abandoned));
        pipeline.SetActiveTask("t1");

        var outcome = pipeline.RetireSlotAndClearIfCurrent("t1");

        Assert.Equal(SlotRetirementOutcome.AlreadyAbandoned, outcome);
        Assert.Equal(
            new WorkSlotView(new WorkSlot("t1", pos, 5), WorkSlotState.Abandoned),
            Assert.Single(pipeline.GetSlotsForTest()));
        Assert.Null(pipeline.ActiveTaskId);
    }

    /// <summary>
    /// IDEMPOTENCE on re-retire: the first call retires a live slot, every later call reports
    /// <see cref="SlotRetirementOutcome.AlreadyAbandoned"/> and leaves the slot exactly as it was.
    /// </summary>
    [Fact]
    public void RetireSlotAndClearIfCurrent_RepeatedRetire_IsIdempotent()
    {
        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("t1", pos, 5, WorkSlotState.Pending));
        pipeline.SetActiveTask("t1");

        Assert.Equal(SlotRetirementOutcome.Retired, pipeline.RetireSlotAndClearIfCurrent("t1"));
        var afterFirst = Snapshot(pipeline);

        Assert.Equal(SlotRetirementOutcome.AlreadyAbandoned, pipeline.RetireSlotAndClearIfCurrent("t1"));
        Assert.Equal(SlotRetirementOutcome.AlreadyAbandoned, pipeline.RetireSlotAndClearIfCurrent("t1"));

        Assert.Equal(afterFirst, Snapshot(pipeline));
        Assert.Null(pipeline.ActiveTaskId);
    }

    /// <summary>
    /// The retire never touches OTHER slots — only the addressed one moves.
    /// </summary>
    [Fact]
    public void RetireSlotAndClearIfCurrent_DoesNotTouchOtherSlots()
    {
        var pipeline = NewPipeline();
        var target = Position(occurrence: 1);
        var bystander = Position(occurrence: 2);
        Assert.True(pipeline.SeedSlotForTest("t1", target, 1, WorkSlotState.Pending));
        Assert.True(pipeline.SeedSlotForTest("t2", bystander, 4, WorkSlotState.Claimed));

        Assert.Equal(SlotRetirementOutcome.Retired, pipeline.RetireSlotAndClearIfCurrent("t1"));

        Assert.Equal(
            new WorkSlotView(new WorkSlot("t2", bystander, 4), WorkSlotState.Claimed),
            Assert.Single(pipeline.GetSlotsForTest(), v => v.Slot.TaskId == "t2"));
    }

    /// <summary>
    /// <see cref="GoalPipeline.IsSlotAbandoned"/> is TRUE only for an Abandoned slot; every live
    /// state, an absent slot, and a blank id all read <c>false</c>.
    /// </summary>
    [Theory]
    [InlineData(StPending, false)]
    [InlineData(StClaimed, false)]
    [InlineData(StRecorded, false)]
    [InlineData(StAbandoned, true)]
    [InlineData(StNone, false)]
    public void IsSlotAbandoned_Matrix(int startCode, bool expected)
    {
        var pos = Position();
        var pipeline = SeedMatrixRow(startCode, pos);

        Assert.Equal(expected, pipeline.IsSlotAbandoned("t1"));

        // The read is a READ: nothing moved.
        AssertMatrixOutcome(pipeline, startCode, startCode, pos);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsSlotAbandoned_NullOrBlankTaskId_IsFalse(string? taskId)
    {
        var pipeline = NewPipeline();
        Assert.True(pipeline.SeedSlotForTest("t1", Position(), 5, WorkSlotState.Abandoned));

        Assert.False(pipeline.IsSlotAbandoned(taskId));
    }

    [Fact]
    public void IsSlotAbandoned_AfterRetire_FlipsToTrue()
    {
        var pipeline = NewPipeline();
        Assert.True(pipeline.SeedSlotForTest("t1", Position(), 5, WorkSlotState.Claimed));

        Assert.False(pipeline.IsSlotAbandoned("t1"));
        Assert.Equal(SlotRetirementOutcome.Retired, pipeline.RetireSlotAndClearIfCurrent("t1"));
        Assert.True(pipeline.IsSlotAbandoned("t1"));
    }

    /// <summary>
    /// The BEHAVIOURAL lock companion for the retirement primitive, following the existing
    /// <c>WhileLockHeld</c> pattern (the IL backstop in region (i) remains the deterministic
    /// authority for the lock STRUCTURE). Two facts only: the call cannot complete while the
    /// pipeline lock is held, and once released the worker observes the COMPLETE after-state —
    /// the retired slot AND the cleared pointer together, never one without the other.
    /// </summary>
    [Fact]
    public void RetireSlotAndClearIfCurrent_WhileLockHeld_IsBlocked_ThenCommitsSlotAndPointer()
    {
        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("t1", pos, 5, WorkSlotState.Claimed));
        pipeline.SetActiveTask("t1");

        var monitor = GetPipelineLock(pipeline);

        using var callAttempted = new ManualResetEventSlim(false);
        using var callCompleted = new ManualResetEventSlim(false);
        SlotRetirementOutcome? workerResult = null;
        bool attemptObserved;
        bool completedWhileHeld;

        var worker = new Thread(() =>
        {
            callAttempted.Set();                                          // signalled IMMEDIATELY BEFORE the call…
            var retired = pipeline.RetireSlotAndClearIfCurrent("t1");     // …which parks on the pipeline lock
            // SlotRetirementOutcome is a value type; the Join below establishes the happens-before.
            workerResult = retired;
            callCompleted.Set();
        })
        {
            IsBackground = true,
            Name = "work-slot-blocked-retirer",
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

        Assert.True(worker.Join(WaitTimeout), "The blocked retire never completed after the lock was released.");

        Assert.True(attemptObserved, "The worker thread never signalled its call attempt.");
        Assert.False(
            completedWhileHeld,
            "RetireSlotAndClearIfCurrent completed while the pipeline lock was held — it is not running under the lock.");

        Assert.Equal(SlotRetirementOutcome.Retired, workerResult);
        Assert.Equal(
            new WorkSlotView(new WorkSlot("t1", pos, 5), WorkSlotState.Abandoned),
            Assert.Single(pipeline.GetSlotsForTest()));
        Assert.Null(pipeline.ActiveTaskId);
    }

    /// <summary>
    /// The same blocked-while-held companion for the locked READ: it cannot observe the registry
    /// while another holder has the lock, and once released it reports the post-release truth.
    /// </summary>
    [Fact]
    public void IsSlotAbandoned_WhileLockHeld_IsBlocked_ThenReadsCommittedState()
    {
        var pipeline = NewPipeline();
        Assert.True(pipeline.SeedSlotForTest("t1", Position(), 5, WorkSlotState.Abandoned));

        var monitor = GetPipelineLock(pipeline);

        using var callAttempted = new ManualResetEventSlim(false);
        using var callCompleted = new ManualResetEventSlim(false);
        bool? workerResult = null;
        bool attemptObserved;
        bool completedWhileHeld;

        var worker = new Thread(() =>
        {
            callAttempted.Set();
            var abandoned = pipeline.IsSlotAbandoned("t1");
            workerResult = abandoned;
            callCompleted.Set();
        })
        {
            IsBackground = true,
            Name = "work-slot-blocked-reader",
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

        Assert.True(worker.Join(WaitTimeout), "The blocked read never completed after the lock was released.");

        Assert.True(attemptObserved, "The worker thread never signalled its call attempt.");
        Assert.False(
            completedWhileHeld,
            "IsSlotAbandoned completed while the pipeline lock was held — it is not running under the lock.");

        Assert.True(workerResult);
    }

    /// <summary>
    /// The neighbouring APIs keep their EXACT prior semantics — the new primitive is an addition,
    /// never a redefinition. <c>AbandonSlot</c> stays Pending-only and <c>ClearActiveTaskIfCurrent</c>
    /// stays a pointer-only, ownership-checked operation that never touches the registry.
    /// </summary>
    [Fact]
    public void RetirementPrimitive_DoesNotAlterAbandonSlotOrClearActiveTaskIfCurrentSemantics()
    {
        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("t1", pos, 5, WorkSlotState.Claimed));
        pipeline.SetActiveTask("t1");

        // AbandonSlot still refuses a Claimed slot and never touches the pointer.
        Assert.False(pipeline.AbandonSlot("t1"));
        Assert.Equal(
            new WorkSlotView(new WorkSlot("t1", pos, 5), WorkSlotState.Claimed),
            Assert.Single(pipeline.GetSlotsForTest()));
        Assert.Equal("t1", pipeline.ActiveTaskId);

        // ClearActiveTaskIfCurrent still clears only the pointer, leaving the slot live.
        Assert.True(pipeline.ClearActiveTaskIfCurrent("t1"));
        Assert.Null(pipeline.ActiveTaskId);
        Assert.Equal(
            new WorkSlotView(new WorkSlot("t1", pos, 5), WorkSlotState.Claimed),
            Assert.Single(pipeline.GetSlotsForTest()));
    }

    #endregion

    #region (u) AdmitCompletion — the atomic admission primitive

    // ══════════════════════════════════════════════════════════════════════════════════
    //  THE ADMISSION PRIMITIVE.
    //
    //  AdmitCompletion is ONE lock-scoped operation that both CLASSIFIES a completion and
    //  — for a Pending slot — CLAIMS it. The decision and the claim are a single
    //  linearized event, so a concurrent retire can no longer land between the state
    //  check and the claim.
    //
    //  Three things are pinned here:
    //    (1) the six-case input matrix (blank, absent, Abandoned, Pending, Claimed,
    //        Recorded);
    //    (2) the NESTED-LOCK AVOIDANCE contract — the Pending → Claimed transition is
    //        written out directly and never delegates to ResolveAndCheckSlot (or any
    //        other locked registry entry point), asserted against the compiled artifact;
    //    (3) the retire-vs-admit linearization: the two operations serialize, and an
    //        admitted claim is never overwritten by — nor overwrites — a retire.
    //
    //  The lock STRUCTURE itself is covered by region (i): "AdmitCompletion" is a member
    //  of LockedRegistryMethodNames, so RegistryEntryPoint_RunsWhollyUnderTheLock and
    //  RegistryEntryPoint_EmitsTheRoslynLockEnterOverload both run against it, and
    //  LockStructureBackstop_CoversEveryRegistryEntryPoint pins the list against drift.
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE SIX-CASE INPUT MATRIX (five here plus the blank-id theory below).
    /// <list type="bullet">
    ///   <item><description>absent slot → <c>NoSlot</c>, the pre-registry pass-through;</description></item>
    ///   <item><description><c>Abandoned</c> → <c>SlotAbandoned</c>;</description></item>
    ///   <item><description><c>Pending</c> → <c>Admitted</c> AND the slot is now <c>Claimed</c>;</description></item>
    ///   <item><description><c>Claimed</c> / <c>Recorded</c> → <c>SlotAlreadyAdmitted</c>, unmoved.</description></item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData(StPending, AdmAdmitted, StClaimed)]
    [InlineData(StClaimed, AdmAlreadyAdmitted, StClaimed)]
    [InlineData(StRecorded, AdmAlreadyAdmitted, StRecorded)]
    [InlineData(StAbandoned, AdmSlotAbandoned, StAbandoned)]
    [InlineData(StNone, AdmNoSlot, StNone)]
    public void AdmitCompletion_Matrix(int startCode, int expectedOutcomeCode, int expectedStateCode)
    {
        var pos = Position();
        var pipeline = SeedMatrixRow(startCode, pos);

        var outcome = pipeline.AdmitCompletion("t1");

        Assert.Equal((AdmissionOutcome)expectedOutcomeCode, outcome);
        AssertMatrixOutcome(pipeline, startCode, expectedStateCode, pos);
    }

    /// <summary>
    /// A blank/null id names no attempt: <c>NoSlot</c> with NO mutation anywhere in the registry.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AdmitCompletion_NullOrBlankTaskId_IsNoSlotWithoutMutation(string? taskId)
    {
        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("t1", pos, 5, WorkSlotState.Pending));
        var before = Snapshot(pipeline);

        Assert.Equal(AdmissionOutcome.NoSlot, pipeline.AdmitCompletion(taskId));

        Assert.Equal(before, Snapshot(pipeline));
    }

    /// <summary>
    /// The admission never touches slots other than the addressed one, and an unknown id is a
    /// pure read.
    /// </summary>
    [Fact]
    public void AdmitCompletion_UnknownTaskId_DoesNotTouchOtherSlots()
    {
        var pipeline = NewPipeline();
        Assert.True(pipeline.SeedSlotForTest("other", Position(), 5, WorkSlotState.Pending));
        var before = Snapshot(pipeline);

        Assert.Equal(AdmissionOutcome.NoSlot, pipeline.AdmitCompletion("missing"));

        Assert.Equal(before, Snapshot(pipeline));
    }

    /// <summary>
    /// The admission is a ONE-SHOT claim: the first call on a Pending slot is admitted, and every
    /// subsequent call for the same attempt is a duplicate. This is the registry-level shape of
    /// the completion path's duplicate drop.
    /// </summary>
    [Fact]
    public void AdmitCompletion_SecondCall_IsAlreadyAdmitted()
    {
        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("t1", pos, 5, WorkSlotState.Pending));

        Assert.Equal(AdmissionOutcome.Admitted, pipeline.AdmitCompletion("t1"));
        Assert.Equal(AdmissionOutcome.SlotAlreadyAdmitted, pipeline.AdmitCompletion("t1"));
        Assert.Equal(AdmissionOutcome.SlotAlreadyAdmitted, pipeline.AdmitCompletion("t1"));

        Assert.Equal(
            new WorkSlotView(new WorkSlot("t1", pos, 5), WorkSlotState.Claimed),
            Assert.Single(pipeline.GetSlotsForTest()));
    }

    /// <summary>
    /// <see cref="GoalPipeline.ResolveAndCheckSlot"/> keeps its EXACT prior contract — the new
    /// primitive is an addition, never a redefinition. In particular it still answers
    /// <c>Proceed</c> for a Claimed slot, which is precisely where the admission's
    /// <c>SlotAlreadyAdmitted</c> diverges from it.
    /// </summary>
    [Fact]
    public void AdmitCompletion_DoesNotAlterResolveAndCheckSlotSemantics()
    {
        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("t1", pos, 5, WorkSlotState.Claimed));

        Assert.Equal(SlotGuardResult.Proceed, pipeline.ResolveAndCheckSlot("t1"));
        Assert.Equal(AdmissionOutcome.SlotAlreadyAdmitted, pipeline.AdmitCompletion("t1"));

        Assert.Equal(
            new WorkSlotView(new WorkSlot("t1", pos, 5), WorkSlotState.Claimed),
            Assert.Single(pipeline.GetSlotsForTest()));
    }

    /// <summary>
    /// THE NESTED-LOCK AVOIDANCE CONTRACT, asserted against the COMPILED ARTIFACT (deterministic —
    /// no timing anywhere): <c>AdmitCompletion</c> must implement the Pending → Claimed transition
    /// DIRECTLY and must not call any other locked registry entry point — above all
    /// <c>ResolveAndCheckSlot</c> — which would re-enter the very monitor it already holds.
    /// <para>
    /// Deterministic kill: rewriting the body as
    /// <c>lock (_lock) { … ResolveAndCheckSlot(taskId) … }</c> puts that call in the emitted IL and
    /// fails here on 100% of runs.
    /// </para>
    /// </summary>
    [Fact]
    public void AdmitCompletion_DoesNotNestAnyOtherLockedRegistryEntryPoint()
    {
        var lockedNames = LockedRegistryMethodNames.ToHashSet(StringComparer.Ordinal);

        var nested = DecodeCallSites(RegistryMethod("AdmitCompletion"))
            .Where(c => c.Target.DeclaringType == typeof(GoalPipeline) && lockedNames.Contains(c.Target.Name))
            .Select(c => c.Target.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            nested.Count == 0,
            $"'AdmitCompletion' calls locked registry entry point(s) [{string.Join(", ", nested)}] — the " +
            "admission must perform its own direct Pending → Claimed transition rather than nesting a " +
            "second acquisition of the pipeline monitor.");
    }

    /// <summary>
    /// The BEHAVIOURAL lock companion for the admission, following the suite's established
    /// <c>WhileLockHeld</c> pattern (region (i)'s IL backstop remains the deterministic authority
    /// for the lock STRUCTURE). Two facts only: the call cannot complete while the pipeline lock is
    /// held, and once released the worker observes the COMPLETE after-state — the <c>Admitted</c>
    /// verdict AND the committed claim together, never one without the other.
    /// </summary>
    [Fact]
    public void AdmitCompletion_WhileLockHeld_IsBlocked_ThenCommitsDecisionAndClaim()
    {
        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("t1", pos, 5, WorkSlotState.Pending));

        var monitor = GetPipelineLock(pipeline);

        using var callAttempted = new ManualResetEventSlim(false);
        using var callCompleted = new ManualResetEventSlim(false);
        AdmissionOutcome? workerResult = null;
        bool attemptObserved;
        bool completedWhileHeld;

        var worker = new Thread(() =>
        {
            callAttempted.Set();                              // signalled IMMEDIATELY BEFORE the call…
            var admitted = pipeline.AdmitCompletion("t1");    // …which parks on the pipeline lock
            // AdmissionOutcome is a value type; the Join below establishes the happens-before.
            workerResult = admitted;
            callCompleted.Set();
        })
        {
            IsBackground = true,
            Name = "work-slot-blocked-admitter",
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

        Assert.True(worker.Join(WaitTimeout), "The blocked admission never completed after the lock was released.");

        Assert.True(attemptObserved, "The worker thread never signalled its call attempt.");
        Assert.False(
            completedWhileHeld,
            "AdmitCompletion completed while the pipeline lock was held — it is not running under the lock.");

        Assert.Equal(AdmissionOutcome.Admitted, workerResult);
        Assert.Equal(
            new WorkSlotView(new WorkSlot("t1", pos, 5), WorkSlotState.Claimed),
            Assert.Single(pipeline.GetSlotsForTest()));
    }

    // ══════════════════════════════════════════════════════════════════════════════════
    //  THE RETIRE-vs-ADMIT ORDERING PROOFS — DETERMINISTIC, NOT RACED.
    //
    //  An earlier form of this proof parked both operations on the monitor and then
    //  accepted EITHER serialization. That was not a proof: `Monitor` is not FIFO, so
    //  neither the parked variant nor a barrier race can pin WHICH operation acquires
    //  first, and a proof that accepts both outcomes can pass without ever observing the
    //  edge it claims to establish. Requiring both outcomes across many rounds is worse
    //  still — it makes a CORRECT implementation fail whenever the scheduler happens to
    //  favour one side.
    //
    //  Both vectors below therefore force the order instead of observing it, and each has
    //  a SINGLE admissible outcome:
    //
    //    • RETIRE-FIRST — the retire is executed BY THE TEST THREAD from inside the held
    //      `_lock` region, while the admission is provably parked on that same monitor.
    //      The lock — not a timing window — is what makes the retire first, so the
    //      admission MUST report `SlotAbandoned` and MUST NOT claim.
    //
    //    • ADMIT-FIRST — the admission is run to completion and its committed claim is
    //      OBSERVED (Admitted, slot Claimed) before the retiring thread is even started.
    //      Thread.Start after an observed return is a happens-before edge, so the retire
    //      demonstrably begins after the claim committed, and must proceed per the honest
    //      edge contract in AdmitCompletion's XML doc.
    //
    //  ORDERING EVIDENCE comes only from synchronization points: every event below is
    //  appended either from inside the contested `_lock` region or after a return that the
    //  asserting thread has already observed. Nothing is inferred from which thread won a
    //  race, and no assertion depends on a delay elapsing.
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Deadline for the blocked-state gate below. It bounds a WAIT, never a proof: the gate
    /// succeeds the instant the thread is observed blocked, and exceeding this deadline is a
    /// hard FAILURE (never a silent pass), so no assertion depends on time elapsing.
    /// </summary>
    private static readonly TimeSpan BlockedObservationDeadline = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Blocks until <paramref name="thread"/> is observably parked on a monitor
    /// (<see cref="ThreadState.WaitSleepJoin"/>), or the deadline expires.
    /// <para>
    /// THIS IS THE DISCRIMINATOR the retire-first proof needs, and it requires no production
    /// seam: a thread's blocked state is a runtime fact, observable from outside. Its power is
    /// entirely in WHERE the block sits relative to the implementation's read —
    /// <list type="bullet">
    ///   <item><description>correct implementation: the read is inside the lock span, so the
    ///     thread parks BEFORE reading anything;</description></item>
    ///   <item><description>check-then-claim mutant: the read is outside the lock, so the thread
    ///     can only park at the later claim-write, i.e. AFTER it has already read.</description></item>
    /// </list>
    /// Observing the park therefore establishes "the mutant has read" and "the correct
    /// implementation has not read" simultaneously — which is exactly what makes a retire issued
    /// afterwards kill the mutant on every legal schedule.
    /// </para>
    /// <para>
    /// The wait spins with <see cref="Thread.Yield"/> rather than sleeping, so it neither
    /// depends on nor consumes a fixed delay; the deadline exists only so a regression fails
    /// loudly instead of hanging.
    /// </para>
    /// </summary>
    /// <param name="thread">The worker thread expected to park on the monitor.</param>
    /// <param name="diagnosis">The terminal state observed, for the failure message.</param>
    /// <returns><c>true</c> once the thread is observed blocked; <c>false</c> on deadline.</returns>
    private static bool TryWaitUntilBlockedOnMonitor(Thread thread, out string diagnosis)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (deadline.Elapsed < BlockedObservationDeadline)
        {
            var state = thread.ThreadState;

            // The target state: parked on the monitor (or any wait), which is where an
            // unacquirable lock puts a runnable thread.
            if ((state & ThreadState.WaitSleepJoin) == ThreadState.WaitSleepJoin)
            {
                diagnosis = $"Observed state: {state}.";
                return true;
            }

            // A thread that RAN TO COMPLETION never parked. Under the correct implementation
            // that is impossible while this thread holds the monitor; under an unlocked
            // implementation it is exactly what happens — and the caller's assertions then fail
            // on the missing park, which is the intended kill.
            if ((state & ThreadState.Stopped) == ThreadState.Stopped)
            {
                diagnosis =
                    $"The thread ran to COMPLETION without ever parking (state: {state}) — it did not " +
                    "contend for the pipeline monitor at all, so the operation is not running under the lock.";
                return false;
            }

            Thread.Yield();
        }

        diagnosis = $"Timed out after {BlockedObservationDeadline.TotalSeconds:F0}s. Last state: {thread.ThreadState}.";
        return false;
    }

    /// <summary>
    /// Executes every registry path the retire-first proof will exercise, on a THROWAWAY
    /// pipeline, so class-loading and JIT compilation are complete before the timed vector runs.
    /// <para>
    /// This is a correctness precaution, not a performance one: a first-call JIT could park the
    /// worker thread in <see cref="ThreadState.WaitSleepJoin"/> for reasons unrelated to the
    /// monitor and BEFORE it reaches its read, which would let the blocked-state gate fire early
    /// and hand the check-then-claim mutant a survival window. Pre-warming removes that window.
    /// </para>
    /// </summary>
    private static void WarmRegistryPaths()
    {
        var warm = NewPipeline();
        Assert.True(warm.SeedSlotForTest("warm", Position(), 1, WorkSlotState.Pending));
        warm.SetActiveTask("warm");
        Assert.Equal(AdmissionOutcome.Admitted, warm.AdmitCompletion("warm"));
        Assert.Equal(SlotRetirementOutcome.Retired, warm.RetireSlotAndClearIfCurrent("warm"));
        Assert.Single(warm.GetSlotsForTest());
    }

    /// <summary>
    /// A monotonic, lock-protected event log. Entries are appended ONLY at synchronization
    /// points — from inside the pipeline's held `_lock` region, or after a return value the
    /// appending thread has already observed — so the recorded sequence is a happens-before
    /// order rather than a sampling of a race.
    /// </summary>
    private sealed class EventLog
    {
        private readonly object _gate = new();
        private readonly List<string> _events = [];

        public void Add(string entry)
        {
            lock (_gate)
                _events.Add(entry);
        }

        public IReadOnlyList<string> Snapshot()
        {
            lock (_gate)
                return [.. _events];
        }
    }

    /// <summary>
    /// THE RETIRE-FIRST ORDERING PROOF, and the vector that kills the check-then-claim race
    /// form outright.
    /// <para>
    /// The test thread takes the pipeline's own monitor and starts an
    /// <see cref="GoalPipeline.AdmitCompletion"/> that parks on it. While STILL HOLDING the
    /// lock, the test thread performs the retire itself: the mutual exclusion — not a timing
    /// window — is what places the retire strictly before the admission's entire lock span,
    /// so the ordering is forced rather than raced.
    /// </para>
    /// <para>
    /// Exactly ONE outcome is admissible. The admission runs on an already-Abandoned slot, so
    /// it must return <see cref="AdmissionOutcome.SlotAbandoned"/> and must claim NOTHING; the
    /// event log must read retire-then-admission. There is no "either order is fine" clause
    /// here, and no assertion depends on any delay elapsing: under a correct implementation
    /// the parked admission physically cannot proceed while the monitor is held, however long
    /// or briefly that is.
    /// </para>
    /// <para>
    /// REMOVAL PROOF against the check-then-claim form specifically — and the reason the gate
    /// below is a THREAD-STATE observation rather than a signal fired by the worker.
    /// </para>
    /// <para>
    /// The two worlds this vector must separate both end with the worker parked on the pipeline
    /// monitor, so "the worker is parked" alone decides nothing. What differs is WHEN the
    /// worker's decisive READ happened relative to that park:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>CORRECT implementation — the whole operation is one lock span, so the
    ///     thread blocks at <c>Monitor.Enter</c> on ENTRY and its read has NOT yet happened;</description></item>
    ///   <item><description>CHECK-THEN-CLAIM MUTANT — the <c>TryGetValue</c> and the Pending
    ///     decision run OUTSIDE the lock and only the claim-write is inside, so by the time the
    ///     thread blocks its read has ALREADY completed and it is holding a stale
    ///     <c>Pending</c> verdict.</description></item>
    /// </list>
    /// <para>
    /// A signal fired by the worker cannot express this. Any signal it can raise sits at method
    /// ENTRY — before the mutant's read — which leaves a legal mutant schedule alive: signal,
    /// stall before invoking, let the test's bounded wait expire, let the retire commit, then run
    /// and read <c>Abandoned</c>, returning <c>SlotAbandoned</c> and PASSING. Placing a signal
    /// after the read would require a production seam inside <c>AdmitCompletion</c>, which is not
    /// permitted. So the gate uses the one fact that needs no seam at all: the worker thread's
    /// own BLOCKED-ON-MONITOR state, which happens-AFTER the mutant's read and happens-BEFORE the
    /// correct implementation's read. Retiring only once that state is observed therefore kills
    /// the mutant on EVERY legal schedule, not merely on a favourable one.
    /// </para>
    /// </summary>
    [Fact]
    public void AdmitCompletion_RetireCommittedUnderTheLock_AdmissionObservesAbandonedAndNeverClaims()
    {
        // JIT PRE-WARM on a throwaway pipeline. Without it the worker's first-ever call could
        // enter WaitSleepJoin for class-loading/JIT reasons BEFORE reaching the mutant's read,
        // which would let the gate below fire too early and hand the mutant a survival window.
        // Warming every method the worker and this thread will execute removes that window.
        WarmRegistryPaths();

        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("t1", pos, 5, WorkSlotState.Pending));
        pipeline.SetActiveTask("t1");

        var monitor = GetPipelineLock(pipeline);
        var log = new EventLog();

        AdmissionOutcome? admission = null;
        SlotRetirementOutcome retirement;

        // NOTE the absence of any pre-call signal: the worker's body is ONLY the call and the
        // post-return recording. Nothing it does can be mistaken for evidence about its read.
        var admitter = new Thread(() =>
        {
            var outcome = pipeline.AdmitCompletion("t1");
            // Appended only AFTER the call returned, so this thread has already observed it.
            admission = outcome;
            log.Add($"admit-returned:{outcome}");
        })
        {
            IsBackground = true,
            Name = "work-slot-parked-admitter",
        };

        Monitor.Enter(monitor);
        try
        {
            // Started from INSIDE the held region, so the monitor is provably already held before
            // the worker can reach it — there is no start-order race to lose.
            admitter.Start();

            // ── THE GATE: wait until the worker is OBSERVABLY BLOCKED on the monitor ──────
            // Bounded by a deadline; no fixed sleep, no Task.Delay, no grace tolerance. In the
            // mutant this state is reached only AFTER its unserialized read; in the correct
            // implementation it is reached BEFORE any read. Everything below therefore
            // happens-after the mutant's read and happens-before the real read.
            Assert.True(
                TryWaitUntilBlockedOnMonitor(admitter, out var gateDiagnosis),
                $"The admitting thread never became observably blocked on the pipeline monitor. {gateDiagnosis}");
            log.Add("admitter-blocked-on-monitor");

            // THE FORCED ORDER: the retire is executed by THIS thread, which owns the monitor.
            // `RetireSlotAndClearIfCurrent` re-enters the same re-entrant lock, so it commits
            // here — while the worker is still parked and cannot interleave.
            retirement = pipeline.RetireSlotAndClearIfCurrent("t1");
            log.Add($"retire-returned:{retirement}");

            // Recorded from inside the still-held region: the retire is fully committed and
            // visible before the admission is permitted to proceed at all.
            var underLock = Assert.Single(pipeline.GetSlotsForTest());
            log.Add($"state-under-lock:{underLock.State}");
        }
        finally
        {
            Monitor.Exit(monitor);
        }

        Assert.True(admitter.Join(WaitTimeout), "The parked admission never completed after the lock was released.");

        // ── THE ORDER ITSELF, from the synchronization points ─────────────────────────
        // Asserted FIRST, because it is the claim this vector exists to make: the worker was
        // parked, the retire then committed, and only afterwards did the admission return.
        Assert.Equal(
            [
                "admitter-blocked-on-monitor",
                $"retire-returned:{SlotRetirementOutcome.Retired}",
                $"state-under-lock:{WorkSlotState.Abandoned}",
                $"admit-returned:{AdmissionOutcome.SlotAbandoned}",
            ],
            log.Snapshot());

        // ── THE SINGLE ADMISSIBLE OUTCOME ─────────────────────────────────────────────
        Assert.Equal(SlotRetirementOutcome.Retired, retirement);
        Assert.Equal(AdmissionOutcome.SlotAbandoned, admission);

        // The admission claimed NOTHING: the retired attempt was not resurrected. Under the
        // mutant the stale Pending verdict writes Claimed here instead.
        Assert.Equal(
            new WorkSlotView(new WorkSlot("t1", pos, 5), WorkSlotState.Abandoned),
            Assert.Single(pipeline.GetSlotsForTest()));
        Assert.Null(pipeline.ActiveTaskId);
    }

    /// <summary>
    /// THE ADMIT-FIRST ORDERING PROOF — the honest edge contract stated in
    /// <see cref="GoalPipeline.AdmitCompletion"/>'s documentation: a retire landing AFTER the
    /// admission's claim PROCEEDS. The guarantee is the atomicity of the admission decision,
    /// not isolation of whatever the admitted completion goes on to do.
    /// <para>
    /// The order is established by program order and a happens-before edge, not by a race: the
    /// admission is run to completion and its committed claim is OBSERVED (the
    /// <c>Admitted</c> return AND the <c>Claimed</c> slot, read back under the registry's own
    /// lock) BEFORE the retiring thread is created and started. <c>Thread.Start</c> happens
    /// after everything the starting thread has already done, so the retire demonstrably
    /// begins only after the claim committed.
    /// </para>
    /// <para>
    /// Again exactly one outcome is admissible: the retire reports
    /// <see cref="SlotRetirementOutcome.Retired"/>, the Claimed slot becomes Abandoned, and the
    /// if-current pointer clears. Nothing here is scheduler-dependent.
    /// </para>
    /// </summary>
    [Fact]
    public void AdmitCompletion_ClaimCommittedFirst_TheLaterRetireProceedsPerTheEdgeContract()
    {
        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("t1", pos, 5, WorkSlotState.Pending));
        pipeline.SetActiveTask("t1");

        var log = new EventLog();

        // ── (1) THE CLAIM COMMITS, AND IS OBSERVED ────────────────────────────────────
        var admission = pipeline.AdmitCompletion("t1");
        log.Add($"admit-returned:{admission}");
        Assert.Equal(AdmissionOutcome.Admitted, admission);

        var afterClaim = Assert.Single(pipeline.GetSlotsForTest());
        log.Add($"claim-observed:{afterClaim.State}");
        Assert.Equal(
            new WorkSlotView(new WorkSlot("t1", pos, 5), WorkSlotState.Claimed),
            afterClaim);

        // ── (2) ONLY NOW does the retire come into existence ──────────────────────────
        // Creating and starting the thread here — after the observations above — is the
        // happens-before edge that orders the claim before the retire's attempt.
        SlotRetirementOutcome retirement = default;
        var retirer = new Thread(() =>
        {
            log.Add("retire-attempted");
            var outcome = pipeline.RetireSlotAndClearIfCurrent("t1");
            retirement = outcome;
            log.Add($"retire-returned:{outcome}");
        })
        {
            IsBackground = true,
            Name = "work-slot-post-claim-retirer",
        };

        log.Add("retire-thread-started");
        retirer.Start();
        Assert.True(retirer.Join(WaitTimeout), "The post-claim retire never completed.");

        // ── (3) THE EDGE CONTRACT: the later retire PROCEEDS ──────────────────────────
        Assert.Equal(SlotRetirementOutcome.Retired, retirement);
        Assert.Equal(
            new WorkSlotView(new WorkSlot("t1", pos, 5), WorkSlotState.Abandoned),
            Assert.Single(pipeline.GetSlotsForTest()));
        Assert.Null(pipeline.ActiveTaskId);

        // ── THE ORDER ITSELF, from program order and the Join ─────────────────────────
        Assert.Equal(
            [
                $"admit-returned:{AdmissionOutcome.Admitted}",
                $"claim-observed:{WorkSlotState.Claimed}",
                "retire-thread-started",
                "retire-attempted",
                $"retire-returned:{SlotRetirementOutcome.Retired}",
            ],
            log.Snapshot());
    }

    #endregion

    #region (v) Capture/Restore — the detached snapshot building block

    // ══════════════════════════════════════════════════════════════════════════════════
    //  WHAT THIS REGION PROVES — and what it deliberately does NOT.
    //
    //  CaptureRegistry/RestoreRegistry are a DOMAIN-ONLY building block: a detached,
    //  validated copy of the in-memory work-slot registry and its dispatch-attempt
    //  high-water marks. The tests below prove value preservation, detachment of BOTH
    //  the captured and the restored-collection storage, validation and rejection
    //  contracts, late-invalid atomicity, nonempty-target refusals, and counter
    //  continuity/overflow. They do NOT claim restart continuity or crash-safe
    //  completion processing — wiring the snapshot into persistence and activating
    //  restoration at startup is a separate future slice.
    // ══════════════════════════════════════════════════════════════════════════════════

    private static WorkSlotView View(string id, WorkSlotPosition pos, int attempt, WorkSlotState state) =>
        new(new WorkSlot(id, pos, attempt), state);

    /// <summary>An empty pipeline to restore into — the "both dictionaries empty" target.</summary>
    private static GoalPipeline FreshTarget() => NewPipeline();

    private static WorkSlotRegistryAttemptEntry Entry(WorkSlotPosition pos, int highWater) =>
        new(pos, highWater);

    private static HashSet<WorkSlotView> ViewsOf(WorkSlotRegistrySnapshot snapshot) => [.. snapshot.Slots];

    /// <summary>The captured high-water entries as a comparable value set.</summary>
    private static HashSet<WorkSlotRegistryAttemptEntry> AttemptsOf(WorkSlotRegistrySnapshot snapshot) =>
        [.. snapshot.DispatchAttempts];

    private static void AssertTargetStillEmpty(GoalPipeline target)
    {
        var snapshot = target.CaptureRegistry();
        Assert.Empty(snapshot.Slots);
        Assert.Empty(snapshot.DispatchAttempts);
    }

    /// <summary>
    /// Builds a registry through REAL allocation/transition paths ONLY — never the
    /// <c>SeedSlotForTest</c> seam, which deliberately permits attempt zero and does not
    /// advance counters, and therefore cannot prove restore semantics. Every lifecycle
    /// state is reached through production transitions: Pending (fresh allocation), Claimed
    /// (<c>ResolveAndCheckSlot</c>), Recorded (<c>RecordSlot</c>), Abandoned
    /// (<c>AbandonSlot</c>), plus a second, higher attempt at a retried position.
    /// </summary>
    private static GoalPipeline RealPathSource()
    {
        var pipeline = NewPipeline();
        var posA = Position(occurrence: 1);
        var posB = Position(occurrence: 2);
        var posC = Position(occurrence: 3);
        var posD = Position(occurrence: 4);

        // Pending → Claimed (a real in-flight dispatch).
        pipeline.AllocateAttemptAndRegisterSlot("claimed-task", posA);
        Assert.Equal(SlotGuardResult.Proceed, pipeline.ResolveAndCheckSlot("claimed-task"));

        // Pending → Recorded, then a retry at the SAME position (attempt 2, Pending).
        // RecordSlot only transitions Claimed → Recorded, so the claim comes first —
        // ResolveAndCheckSlot then RecordSlot is the production completion path.
        pipeline.AllocateAttemptAndRegisterSlot("recorded-task", posB);
        Assert.Equal(SlotGuardResult.Proceed, pipeline.ResolveAndCheckSlot("recorded-task"));
        Assert.Equal(SlotRecordOutcome.Recorded, pipeline.RecordSlot("recorded-task"));
        pipeline.AllocateAttemptAndRegisterSlot("retried-task", posB);

        // Pending → Abandoned.
        pipeline.AllocateAttemptAndRegisterSlot("abandoned-task", posC);
        Assert.True(pipeline.AbandonSlot("abandoned-task"));

        // Plain Pending.
        pipeline.AllocateAttemptAndRegisterSlot("pending-task", posD);

        return pipeline;
    }

    // ── 1. All-state round trips ───────────────────────────────────────────────────────

    [Fact]
    public void Restore_AllStateRoundTrip_PreservesSlotsStatesAttemptsAndCounters()
    {
        var source = RealPathSource();
        var snapshot = source.CaptureRegistry();

        var target = FreshTarget();
        target.RestoreRegistry(snapshot);

        // Every slot, position, attempt and state survived intact.
        Assert.Equal(ViewsOf(source.CaptureRegistry()), ViewsOf(target.CaptureRegistry()));
        Assert.Equal(5, target.GetSlotsForTest().Count);

        // Lifecycle states preserved EXACTLY: a restored Claimed slot still answers Proceed
        // and STAYS Claimed…
        Assert.Equal(SlotGuardResult.Proceed, target.ResolveAndCheckSlot("claimed-task"));
        Assert.Contains(
            View("claimed-task", Position(occurrence: 1), 1, WorkSlotState.Claimed),
            ViewsOf(target.CaptureRegistry()));

        // …is NOT replayable through admission (it was never turned back into Pending)…
        Assert.Equal(AdmissionOutcome.SlotAlreadyAdmitted, target.AdmitCompletion("claimed-task"));

        // …while a restored Pending slot admits normally, and unknown IDs are still NoSlot.
        Assert.Equal(AdmissionOutcome.Admitted, target.AdmitCompletion("pending-task"));
        Assert.Equal(AdmissionOutcome.NoSlot, target.AdmitCompletion("never-registered"));

        // Counter continuity: posC holds only the Abandoned attempt-1 slot, and its restored
        // counter makes the next allocation there attempt 2 — not a restart at 1.
        Assert.Equal(2, target.AllocateAttemptAndRegisterSlot("after-restore", Position(occurrence: 3)).Attempt);
    }

    [Fact]
    public void Capture_EmptyRegistry_YieldsAnEmptySnapshotThatRestoresIntoAFreshTarget()
    {
        var source = FreshTarget();

        var snapshot = source.CaptureRegistry();
        Assert.Empty(snapshot.Slots);
        Assert.Empty(snapshot.DispatchAttempts);

        var target = FreshTarget();
        target.RestoreRegistry(snapshot);
        AssertTargetStillEmpty(target);
    }

    [Fact]
    public void Restore_EmptySnapshot_IsLegalIntoAnEmptyTarget()
    {
        var target = FreshTarget();

        target.RestoreRegistry(new WorkSlotRegistrySnapshot([], []));

        AssertTargetStillEmpty(target);
    }

    // ── 2. Detachment of the captured and the restored-collection storage ─────────────

    [Fact]
    public void Capture_RegistryMutationsAfterCapture_DoNotReachTheSnapshot()
    {
        var source = RealPathSource();
        var snapshot = source.CaptureRegistry();
        var frozen = ViewsOf(snapshot);

        // Mutate the registry AFTER the capture: a state transition plus a brand-new slot.
        // RecordSlot only moves Claimed → Recorded; "pending-task" must be claimed first.
        Assert.Equal(SlotGuardResult.Proceed, source.ResolveAndCheckSlot("pending-task"));
        Assert.Equal(SlotRecordOutcome.Recorded, source.RecordSlot("pending-task"));
        source.AllocateAttemptAndRegisterSlot("latecomer", Position(occurrence: 5));

        // The snapshot is frozen at capture time.
        Assert.Equal(frozen, ViewsOf(snapshot));
        Assert.DoesNotContain(snapshot.Slots, v => v.Slot.TaskId == "latecomer");
        Assert.Contains(View("pending-task", Position(occurrence: 4), 1, WorkSlotState.Pending), frozen);
    }

    [Fact]
    public void Capture_MutatingTheReturnedCollections_CannotTouchTheRegistry()
    {
        var source = RealPathSource();
        var before = ViewsOf(source.CaptureRegistry());

        var snapshot = source.CaptureRegistry();
        var slots = (List<WorkSlotView>)snapshot.Slots;
        var attempts = (List<WorkSlotRegistryAttemptEntry>)snapshot.DispatchAttempts;

        // Hostile mutation of the returned collections themselves.
        slots.Clear();
        slots.Add(View("injected", Position(occurrence: 99), 1, WorkSlotState.Claimed));
        attempts.Clear();
        attempts.Add(Entry(Position(occurrence: 99), 42));

        // The registry is untouched.
        Assert.Equal(before, ViewsOf(source.CaptureRegistry()));

        // And the counters still work: posC holds only the Abandoned attempt-1 slot with
        // counter 1, so the next allocation is attempt 2.
        Assert.Equal(2, source.AllocateAttemptAndRegisterSlot("probe", Position(occurrence: 3)).Attempt);
    }

    [Fact]
    public void Restore_MutatingTheInputCollectionsAfterwards_CannotTouchTheInstalledRegistry()
    {
        var target = FreshTarget();
        var posA = Position(occurrence: 1);
        var posB = Position(occurrence: 2);

        var slots = new List<WorkSlotView> { View("t1", posA, 2, WorkSlotState.Recorded) };
        var attempts = new List<WorkSlotRegistryAttemptEntry> { Entry(posA, 4), Entry(posB, 7) };

        target.RestoreRegistry(new WorkSlotRegistrySnapshot(slots, attempts));
        var installed = ViewsOf(target.CaptureRegistry());

        // Mutate the INPUT lists after the restore completed.
        slots.Clear();
        attempts.Clear();
        attempts.Add(Entry(posA, 100));

        // The installed registry is independent of the caller's collections.
        Assert.Equal(installed, ViewsOf(target.CaptureRegistry()));
        Assert.Equal(5, target.AllocateAttemptAndRegisterSlot("n1", posA).Attempt); // 4 + 1, never 101
        Assert.Equal(8, target.AllocateAttemptAndRegisterSlot("n2", posB).Attempt); // 7 + 1
    }

    // ── 3. Independent / counter-only / higher counters ───────────────────────────────

    [Fact]
    public void Restore_IndependentCounterOnlyAndHigherHighWaters_ArePreservedExactly()
    {
        // Hand-built fixture: real paths cannot produce counter-only or higher-than-slot
        // counters (retirement never removes slots), so the fixture carries them directly.
        var posA = Position(occurrence: 1);
        var posB = Position(occurrence: 2);
        var snapshot = new WorkSlotRegistrySnapshot(
            [View("t1", posA, 3, WorkSlotState.Recorded)],
            [Entry(posA, 9), Entry(posB, 5)]);   // 9 > 3: higher; 5: counter-only

        var first = FreshTarget();
        first.RestoreRegistry(snapshot);

        // THE ROUND TRIP: capture the restored registry, restore THAT into a fresh target.
        var second = FreshTarget();
        second.RestoreRegistry(first.CaptureRegistry());

        Assert.Equal(ViewsOf(first.CaptureRegistry()), ViewsOf(second.CaptureRegistry()));

        // The higher counter survived EXACTLY: the next allocation continues from 9, never
        // renumbered down to the slot's own attempt 3.
        Assert.Equal(10, second.AllocateAttemptAndRegisterSlot("n1", posA).Attempt);

        // The counter-only entry survived EXACTLY: 6, not inferred from anything else.
        Assert.Equal(6, second.AllocateAttemptAndRegisterSlot("n2", posB).Attempt);
    }

    // ── 4. Next-attempt continuity after restore ──────────────────────────────────────

    [Fact]
    public void Restore_NextAttemptContinuesFromTheHighWater_NotFromSlotHistoryOrAStartup()
    {
        var pos = Position(occurrence: 1);
        var target = FreshTarget();
        target.RestoreRegistry(new WorkSlotRegistrySnapshot(
            [View("t1", pos, 3, WorkSlotState.Recorded)],
            [Entry(pos, 7)]));

        // 8 — NOT 4 (inferred from the slot's history) and NOT 1 (a restart).
        Assert.Equal(8, target.AllocateAttemptAndRegisterSlot("next", pos).Attempt);
    }

    [Fact]
    public void Restore_NextAttemptContinuity_HoldsForTheWithIdAllocatorAndBuiltTaskId()
    {
        var pos = Position(occurrence: 2);
        var target = FreshTarget();
        target.RestoreRegistry(new WorkSlotRegistrySnapshot([], [Entry(pos, 5)]));

        var built = target.AllocateAttemptAndRegisterSlotWithId("goal-1", WorkerRole.Coder, pos);

        Assert.Equal(6, built.Attempt);
        Assert.Equal("goal-1-coder-001-02-006", built.TaskId);
    }

    // ── 5. Duplicate / invalid import rejections ──────────────────────────────────────

    /// <summary>
    /// Builds a single-slot snapshot that is malformed in exactly one way, named by
    /// <paramref name="kind"/>. The counter entry carries the SLOT'S OWN position (malformed
    /// or not) rather than a separate valid one, so the missing-matching-high-water cross-check
    /// can never be the guard that rejects a slot-position defect: removing the slot-position
    /// validation must change WHICH guard fires, which
    /// <see cref="Restore_MalformedSlotPosition_IsRejectedByTheSlotGuardItself"/> pins down.
    /// </summary>
    private static WorkSlotRegistrySnapshot MalformedSnapshot(string kind)
    {
        var pos = Position(occurrence: 1);

        WorkSlotRegistrySnapshot WithSlot(string? id, WorkSlotPosition? slotPos, int attempt, WorkSlotState state) =>
            new(
                [new WorkSlotView(new WorkSlot(id!, slotPos!, attempt), state)],
                [Entry(slotPos ?? pos, Math.Max(attempt, 1))]);

        return kind switch
        {
            "null-slot-entry" => new WorkSlotRegistrySnapshot([null!], [Entry(pos, 1)]),
            "null-slot" => new WorkSlotRegistrySnapshot(
                [new WorkSlotView(null!, WorkSlotState.Pending)], [Entry(pos, 1)]),
            "blank-task-id-null" => WithSlot(null, pos, 1, WorkSlotState.Pending),
            "blank-task-id-empty" => WithSlot("", pos, 1, WorkSlotState.Pending),
            "blank-task-id-whitespace" => WithSlot("   ", pos, 1, WorkSlotState.Pending),
            "null-slot-position" => WithSlot("t1", null!, 1, WorkSlotState.Pending),
            "zero-iteration" => WithSlot("t1", Position(iteration: 0), 1, WorkSlotState.Pending),
            "negative-iteration" => WithSlot("t1", Position(iteration: -3), 1, WorkSlotState.Pending),
            "zero-occurrence" => WithSlot("t1", Position(occurrence: 0), 1, WorkSlotState.Pending),
            "negative-occurrence" => WithSlot("t1", Position(occurrence: -2), 1, WorkSlotState.Pending),
            "zero-attempt" => WithSlot("t1", pos, 0, WorkSlotState.Pending),
            "negative-attempt" => WithSlot("t1", pos, -4, WorkSlotState.Pending),
            "undefined-phase" => WithSlot("t1", new WorkSlotPosition(1, (GoalPhase)999, 1), 1, WorkSlotState.Pending),
            "undefined-state" => WithSlot("t1", pos, 1, (WorkSlotState)99),
            "null-attempt-entry" => new WorkSlotRegistrySnapshot([], [null!]),
            "null-attempt-position" => new WorkSlotRegistrySnapshot([], [Entry(null!, 1)]),
            "zero-high-water" => new WorkSlotRegistrySnapshot([], [Entry(pos, 0)]),
            "negative-high-water" => new WorkSlotRegistrySnapshot([], [Entry(pos, -1)]),
            // ISOLATED counter-only positions: no slot references them at all, so only the
            // attempt-entry position guard can possibly reject these.
            "counter-only-zero-iteration" =>
                new WorkSlotRegistrySnapshot([], [Entry(Position(iteration: 0), 1)]),
            "counter-only-negative-iteration" =>
                new WorkSlotRegistrySnapshot([], [Entry(Position(iteration: -3), 1)]),
            "counter-only-zero-occurrence" =>
                new WorkSlotRegistrySnapshot([], [Entry(Position(occurrence: 0), 1)]),
            "counter-only-negative-occurrence" =>
                new WorkSlotRegistrySnapshot([], [Entry(Position(occurrence: -2), 1)]),
            "counter-only-undefined-phase" =>
                new WorkSlotRegistrySnapshot([], [Entry(new WorkSlotPosition(1, (GoalPhase)999, 1), 1)]),
            _ => throw new InvalidOperationException($"Unhandled malformed-snapshot kind '{kind}'."),
        };
    }

    /// <summary>
    /// Theory feed of <see cref="MalformedSnapshot"/> kinds whose rejection is provable by the
    /// throw ALONE. The slot-POSITION defects are deliberately absent: their fixtures' counter
    /// entry carries the same malformed position, so the attempt-entry guard would also reject
    /// them and a bare <c>Throws</c> could not tell the two guards apart. They live in
    /// <see cref="MalformedSlotPositionKinds"/>, whose test pins the message to the slot guard.
    /// </summary>
    public static TheoryData<string> MalformedSnapshotKinds => new()
    {
        "null-slot-entry",
        "null-slot",
        "blank-task-id-null",
        "blank-task-id-empty",
        "blank-task-id-whitespace",
        "null-slot-position",
        "zero-attempt",
        "negative-attempt",
        "undefined-state",
        "null-attempt-entry",
        "null-attempt-position",
        "zero-high-water",
        "negative-high-water",
        "counter-only-zero-iteration",
        "counter-only-negative-iteration",
        "counter-only-zero-occurrence",
        "counter-only-negative-occurrence",
        "counter-only-undefined-phase",
    };

    [Theory]
    [MemberData(nameof(MalformedSnapshotKinds))]
    public void Restore_MalformedEntry_ThrowsArgumentException_TargetUnchanged(string kind)
    {
        var target = FreshTarget();

        Assert.Throws<ArgumentException>(() => target.RestoreRegistry(MalformedSnapshot(kind)));

        AssertTargetStillEmpty(target);
    }

    /// <summary>The slot-position defects, named separately so the guard that rejects them can be
    /// pinned down.</summary>
    public static TheoryData<string> MalformedSlotPositionKinds => new()
    {
        "zero-iteration",
        "negative-iteration",
        "zero-occurrence",
        "negative-occurrence",
        "undefined-phase",
    };

    /// <summary>
    /// REMOVAL PROOF for the slot-position validation. Each fixture's high-water entry carries
    /// the slot's OWN (malformed) position, so no missing-matching-high-water cross-check can
    /// stand in for the slot guard; and the assertion pins the message to the SLOT guard, which
    /// alone names the offending task. Delete the slot-position checks and the rejection either
    /// disappears or comes from the attempt-entry guard, whose message names no task — either
    /// way this test fails.
    /// </summary>
    [Theory]
    [MemberData(nameof(MalformedSlotPositionKinds))]
    public void Restore_MalformedSlotPosition_IsRejectedByTheSlotGuardItself(string kind)
    {
        var target = FreshTarget();

        var ex = Assert.Throws<ArgumentException>(() => target.RestoreRegistry(MalformedSnapshot(kind)));

        Assert.Contains("Snapshot slot 't1'", ex.Message, StringComparison.Ordinal);

        AssertTargetStillEmpty(target);
    }

    /// <summary>
    /// The mirror proof for the ATTEMPT-ENTRY position guard: a counter-only entry no slot
    /// references can only be rejected by that guard, and its message names an attempt entry.
    /// </summary>
    [Theory]
    [InlineData("counter-only-zero-iteration")]
    [InlineData("counter-only-negative-iteration")]
    [InlineData("counter-only-zero-occurrence")]
    [InlineData("counter-only-negative-occurrence")]
    [InlineData("counter-only-undefined-phase")]
    public void Restore_MalformedCounterOnlyPosition_IsRejectedByTheAttemptEntryGuard(string kind)
    {
        var target = FreshTarget();

        var ex = Assert.Throws<ArgumentException>(() => target.RestoreRegistry(MalformedSnapshot(kind)));

        Assert.Contains("Snapshot attempt entry", ex.Message, StringComparison.Ordinal);

        AssertTargetStillEmpty(target);
    }

    [Fact]
    public void Restore_DuplicateSlotPositionAttemptPair_Throws_TargetUnchanged()
    {
        var pos = Position(occurrence: 1);
        var target = FreshTarget();

        // Two otherwise-legal DEAD slots (so the live-position guard cannot fire) with DISTINCT
        // task IDs (so the duplicate-ID guard cannot fire) claiming the SAME allocation: one
        // attempt at one position was stamped twice, which no allocator can ever produce.
        var snapshot = new WorkSlotRegistrySnapshot(
            [View("dead-1", pos, 2, WorkSlotState.Recorded), View("dead-2", pos, 2, WorkSlotState.Abandoned)],
            [Entry(pos, 2)]);

        Assert.Throws<ArgumentException>(() => target.RestoreRegistry(snapshot));

        AssertTargetStillEmpty(target);
    }

    [Fact]
    public void Restore_SamePositionDistinctAttempts_IsLegal()
    {
        var pos = Position(occurrence: 1);
        var target = FreshTarget();

        // The complement of the duplicate-allocation guard: distinct attempts at one position
        // are ordinary retry history and must still be accepted.
        target.RestoreRegistry(new WorkSlotRegistrySnapshot(
            [View("dead-1", pos, 1, WorkSlotState.Recorded), View("dead-2", pos, 2, WorkSlotState.Abandoned)],
            [Entry(pos, 2)]));

        Assert.Equal(2, target.GetSlotsForTest().Count);
    }

    [Fact]
    public void Restore_NullSnapshot_ThrowsArgumentNull_TargetUnchanged()
    {
        var target = FreshTarget();

        Assert.Throws<ArgumentNullException>(() => target.RestoreRegistry(null));

        AssertTargetStillEmpty(target);
    }

    [Fact]
    public void Restore_NullSlotsCollection_ThrowsArgumentNull_TargetUnchanged()
    {
        var target = FreshTarget();

        Assert.Throws<ArgumentNullException>(
            () => target.RestoreRegistry(new WorkSlotRegistrySnapshot(null!, [])));

        AssertTargetStillEmpty(target);
    }

    [Fact]
    public void Restore_NullAttemptsCollection_ThrowsArgumentNull_TargetUnchanged()
    {
        var target = FreshTarget();

        Assert.Throws<ArgumentNullException>(
            () => target.RestoreRegistry(new WorkSlotRegistrySnapshot([], null!)));

        AssertTargetStillEmpty(target);
    }

    [Fact]
    public void Restore_DuplicateOrdinalTaskId_Throws_TargetUnchanged()
    {
        var posA = Position(occurrence: 1);
        var posB = Position(occurrence: 2);
        var target = FreshTarget();

        var snapshot = new WorkSlotRegistrySnapshot(
            [View("t1", posA, 1, WorkSlotState.Recorded), View("t1", posB, 1, WorkSlotState.Recorded)],
            [Entry(posA, 1), Entry(posB, 1)]);

        Assert.Throws<ArgumentException>(() => target.RestoreRegistry(snapshot));

        AssertTargetStillEmpty(target);
    }

    [Fact]
    public void Restore_TaskIdComparisonIsOrdinal_DistinctCaseIsLegal()
    {
        var posA = Position(occurrence: 1);
        var posB = Position(occurrence: 2);
        var target = FreshTarget();

        // Ordinal comparison: "t1" and "T1" are DISTINCT ids, so this import is legal. A
        // case-insensitive duplicate check would reject it — the removal proof for ordinality.
        target.RestoreRegistry(new WorkSlotRegistrySnapshot(
            [View("t1", posA, 1, WorkSlotState.Recorded), View("T1", posB, 1, WorkSlotState.Recorded)],
            [Entry(posA, 1), Entry(posB, 1)]));

        Assert.Equal(2, target.GetSlotsForTest().Count);
    }

    [Fact]
    public void Restore_DuplicateHighWaterPosition_Throws_TargetUnchanged()
    {
        var pos = Position(occurrence: 1);
        var target = FreshTarget();

        var snapshot = new WorkSlotRegistrySnapshot([], [Entry(pos, 1), Entry(pos, 2)]);

        Assert.Throws<ArgumentException>(() => target.RestoreRegistry(snapshot));

        AssertTargetStillEmpty(target);
    }

    [Theory]
    [InlineData(StPending, StPending)]
    [InlineData(StPending, StClaimed)]
    [InlineData(StClaimed, StClaimed)]
    public void Restore_MultipleLiveSlotsAtOnePosition_Throws_TargetUnchanged(
        int firstStateCode, int secondStateCode)
    {
        var pos = Position(occurrence: 1);
        var target = FreshTarget();

        var snapshot = new WorkSlotRegistrySnapshot(
            [View("a", pos, 1, St(firstStateCode)), View("b", pos, 2, St(secondStateCode))],
            [Entry(pos, 2)]);

        Assert.Throws<ArgumentException>(() => target.RestoreRegistry(snapshot));

        AssertTargetStillEmpty(target);
    }

    [Fact]
    public void Restore_TwoDeadSlotsAtOnePosition_IsLegal()
    {
        var pos = Position(occurrence: 1);
        var target = FreshTarget();

        // Only Pending|Claimed occupy a position; two retired slots at one position are the
        // ordinary retry history and must be accepted.
        target.RestoreRegistry(new WorkSlotRegistrySnapshot(
            [View("dead-1", pos, 1, WorkSlotState.Recorded), View("dead-2", pos, 2, WorkSlotState.Abandoned)],
            [Entry(pos, 2)]));

        Assert.Equal(2, target.GetSlotsForTest().Count);
    }

    [Fact]
    public void Restore_SlotWithoutHighWaterEntryForItsPosition_Throws_TargetUnchanged()
    {
        var posA = Position(occurrence: 1);
        var posB = Position(occurrence: 2);
        var target = FreshTarget();

        var snapshot = new WorkSlotRegistrySnapshot(
            [View("t1", posA, 1, WorkSlotState.Recorded)],
            [Entry(posB, 1)]);   // posA's counter is missing entirely.

        Assert.Throws<ArgumentException>(() => target.RestoreRegistry(snapshot));

        AssertTargetStillEmpty(target);
    }

    [Fact]
    public void Restore_SlotAttemptExceedingItsPositionHighWater_Throws_TargetUnchanged()
    {
        var pos = Position(occurrence: 1);
        var target = FreshTarget();

        var snapshot = new WorkSlotRegistrySnapshot(
            [View("t1", pos, 5, WorkSlotState.Recorded)],
            [Entry(pos, 4)]);

        Assert.Throws<ArgumentException>(() => target.RestoreRegistry(snapshot));

        AssertTargetStillEmpty(target);
    }

    [Fact]
    public void Restore_SlotAttemptEqualToItsPositionHighWater_IsLegal()
    {
        var pos = Position(occurrence: 1);
        var target = FreshTarget();

        target.RestoreRegistry(new WorkSlotRegistrySnapshot(
            [View("t1", pos, 5, WorkSlotState.Recorded)],
            [Entry(pos, 5)]));

        Assert.Equal(6, target.AllocateAttemptAndRegisterSlot("next", pos).Attempt);
    }

    // ── 6. Late-invalid atomicity ─────────────────────────────────────────────────────

    [Fact]
    public void Restore_LateInvalidSlotEntry_LeavesTheTargetCompletelyUnchanged()
    {
        var posA = Position(occurrence: 1);
        var posB = Position(occurrence: 2);
        var posC = Position(occurrence: 3);
        var target = FreshTarget();

        // Two fully valid entries are followed by an invalid one — the duplicate task ID is
        // encountered LAST, after everything else has already validated.
        var snapshot = new WorkSlotRegistrySnapshot(
            [
                View("t1", posA, 2, WorkSlotState.Claimed),
                View("t2", posB, 1, WorkSlotState.Recorded),
                View("t1", posC, 1, WorkSlotState.Abandoned),   // duplicate, late
            ],
            [Entry(posA, 5), Entry(posB, 1), Entry(posC, 1)]);

        Assert.Throws<ArgumentException>(() => target.RestoreRegistry(snapshot));

        AssertTargetStillEmpty(target);
    }

    [Fact]
    public void Restore_LateInvalidAttemptEntry_LeavesTheTargetCompletelyUnchanged()
    {
        var posA = Position(occurrence: 1);
        var posB = Position(occurrence: 2);
        var target = FreshTarget();

        var snapshot = new WorkSlotRegistrySnapshot(
            [View("t1", posA, 2, WorkSlotState.Claimed)],
            [Entry(posA, 5), Entry(posB, 1), Entry(posB, 7)]);   // duplicate position, late

        Assert.Throws<ArgumentException>(() => target.RestoreRegistry(snapshot));

        AssertTargetStillEmpty(target);
    }

    // ── 7. Nonempty-target rejections ─────────────────────────────────────────────────

    [Fact]
    public void Restore_IntoRegistryWithExistingSlots_ThrowsInvalidOperation_PreservesTarget()
    {
        var target = RealPathSource();
        var beforeSlots = ViewsOf(target.CaptureRegistry());
        var beforeAttempts = AttemptsOf(target.CaptureRegistry());

        var incoming = new WorkSlotRegistrySnapshot(
            [View("other", Position(occurrence: 8), 1, WorkSlotState.Recorded)],
            [Entry(Position(occurrence: 8), 1)]);

        Assert.Throws<InvalidOperationException>(() => target.RestoreRegistry(incoming));

        // No clearing, no merging — the COMPLETE snapshot, counters included, is identical.
        var after = target.CaptureRegistry();
        Assert.Equal(beforeSlots, ViewsOf(after));
        Assert.Equal(beforeAttempts, AttemptsOf(after));

        // And the incoming counter was never inserted: its position still starts at attempt 1.
        Assert.Equal(1, target.AllocateAttemptAndRegisterSlot("probe", Position(occurrence: 8)).Attempt);
    }

    [Fact]
    public void Restore_IntoCounterOnlyRegistry_ThrowsInvalidOperation_PreservesCounters()
    {
        var pos = Position(occurrence: 1);
        var incomingPos = Position(occurrence: 9);
        var target = FreshTarget();
        target.RestoreRegistry(new WorkSlotRegistrySnapshot([], [Entry(pos, 5)]));

        var beforeSlots = ViewsOf(target.CaptureRegistry());
        var beforeAttempts = AttemptsOf(target.CaptureRegistry());

        // A counter-only target (attempts but no slots) is nonempty too.
        Assert.Throws<InvalidOperationException>(
            () => target.RestoreRegistry(new WorkSlotRegistrySnapshot([], [Entry(incomingPos, 4)])));

        // The complete snapshot is unchanged: nothing cleared, nothing merged.
        var after = target.CaptureRegistry();
        Assert.Equal(beforeSlots, ViewsOf(after));
        Assert.Equal(beforeAttempts, AttemptsOf(after));

        // THE INSERT PROBE: had the incoming counter been written before the refusal threw,
        // this position would continue from 4 (i.e. 5) instead of starting fresh at 1.
        Assert.Equal(1, target.AllocateAttemptAndRegisterSlot("incoming-probe", incomingPos).Attempt);

        // The existing counter was neither cleared nor merged: allocation continues from 5.
        Assert.Equal(6, target.AllocateAttemptAndRegisterSlot("n", pos).Attempt);
    }

    // ── 8. Overflow in BOTH allocators ────────────────────────────────────────────────

    [Fact]
    public void Restore_IntMaxHighWater_AllocateViaBothAllocators_RefusesOverflowWithoutMutation()
    {
        var pos = Position(occurrence: 1);
        var target = FreshTarget();

        // An int.MaxValue high-water is LEGAL to restore…
        target.RestoreRegistry(new WorkSlotRegistrySnapshot([], [Entry(pos, int.MaxValue)]));

        // …but exhausted: allocator 1 refuses with InvalidOperationException, never a wrap.
        Assert.Throws<InvalidOperationException>(
            () => target.AllocateAttemptAndRegisterSlot("overflow-1", pos));

        // …and so does allocator 2.
        Assert.Throws<InvalidOperationException>(
            () => target.AllocateAttemptAndRegisterSlotWithId("goal-1", WorkerRole.Coder, pos));

        // No negative attempt ever appeared: neither refusal registered a slot.
        Assert.Empty(target.GetSlotsForTest());

        // Neither dictionary mutated on overflow: the target is STILL a nonempty
        // (counter-only) registry, so a restore into it is refused again.
        Assert.Throws<InvalidOperationException>(
            () => target.RestoreRegistry(new WorkSlotRegistrySnapshot([], [])));

        // The rest of the registry is unimpaired: a different position allocates normally.
        Assert.Equal(1, target.AllocateAttemptAndRegisterSlot("fresh", Position(occurrence: 2)).Attempt);
    }

    #endregion

    #region (w) The pure validator, the ownership capture and the admission preflight

    // ══════════════════════════════════════════════════════════════════════════════════
    //  WHAT THIS REGION PROVES — and what it deliberately does NOT.
    //
    //  Three DOMAIN-ONLY additions, all built on the existing fixtures above:
    //    • ValidateDetachedRegistrySnapshot — the restore path's copy-and-validate step,
    //      extracted as a pure function: it installs nothing, locks nothing, and returns a
    //      detached snapshot preserving the supplied values AND their order;
    //    • CaptureAdmissionOwnership — goal identity + active-task pointer + the COMPLETE
    //      registry, taken in ONE _lock span;
    //    • PreflightAdmissionOwnership — a pure validation of such a carrier.
    //
    //  What is NOT claimed: nothing here persists anything, nothing changes the production
    //  restore or dispatch paths, and the ownership capture is NOT a coherent whole-pipeline
    //  snapshot — it carries no phase, plan or machine position, and capturing an observed
    //  intermediate state does not make that state a valid admission.
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>A detached carrier built from explicit values (no pipeline involved).</summary>
    private static AdmissionOwnershipSnapshot Ownership(
        string goalId,
        string? activeTaskId,
        IReadOnlyList<WorkSlotView> slots,
        IReadOnlyList<WorkSlotRegistryAttemptEntry> attempts) =>
        new(goalId, activeTaskId, new WorkSlotRegistrySnapshot(slots, attempts));

    // ── 1. The pure validator: no mutation, independent copies, order preserved ────────

    /// <summary>
    /// The validator is PURE: validating a live pipeline's captured registry changes neither the
    /// registry nor the counters, and the returned collections are FRESH — mutating them cannot
    /// reach the pipeline, and mutating the INPUT lists afterwards cannot reach the result.
    /// </summary>
    [Fact]
    public void Validate_IsNonMutating_AndReturnsIndependentCollectionCopies()
    {
        var source = RealPathSource();
        var beforeSlots = ViewsOf(source.CaptureRegistry());
        var beforeAttempts = AttemptsOf(source.CaptureRegistry());

        // The INPUT lists are the caller's own, so they can be mutated after the call.
        var inputSlots = new List<WorkSlotView>(source.CaptureRegistry().Slots);
        var inputAttempts = new List<WorkSlotRegistryAttemptEntry>(source.CaptureRegistry().DispatchAttempts);

        var validated = GoalPipeline.ValidateDetachedRegistrySnapshot(
            new WorkSlotRegistrySnapshot(inputSlots, inputAttempts));

        // (a) The source pipeline is untouched — validation installs nothing.
        var after = source.CaptureRegistry();
        Assert.Equal(beforeSlots, ViewsOf(after));
        Assert.Equal(beforeAttempts, AttemptsOf(after));

        // (b) The result holds exactly the supplied values, in the SUPPLIED ORDER.
        Assert.Equal(inputSlots, validated.Slots);
        Assert.Equal(inputAttempts, validated.DispatchAttempts);

        var frozenSlots = validated.Slots.ToList();
        var frozenAttempts = validated.DispatchAttempts.ToList();

        // (c) Mutating the INPUT lists afterwards cannot alter the validated result.
        inputSlots.Clear();
        inputAttempts.Clear();
        inputAttempts.Add(Entry(Position(occurrence: 99), 42));

        Assert.Equal(frozenSlots, validated.Slots);
        Assert.Equal(frozenAttempts, validated.DispatchAttempts);

        // (d) Mutating the RESULT's collections cannot reach the pipeline registry.
        ((List<WorkSlotView>)validated.Slots).Clear();
        ((List<WorkSlotRegistryAttemptEntry>)validated.DispatchAttempts).Clear();

        var stillThere = source.CaptureRegistry();
        Assert.Equal(beforeSlots, ViewsOf(stillThere));
        Assert.Equal(beforeAttempts, AttemptsOf(stillThere));
    }

    /// <summary>
    /// The validator preserves EVERY lifecycle state, every high-water entry (including ones
    /// HIGHER than any slot's attempt) and every counter-only entry — exactly as supplied, with
    /// nothing renumbered, normalized or reconstructed, and with no identity rebuilt from an ID.
    /// </summary>
    [Fact]
    public void Validate_PreservesEveryLifecycleStateHighWaterAndCounterOnlyEntry()
    {
        var posA = Position(occurrence: 1);
        var posB = Position(occurrence: 2);
        var posC = Position(occurrence: 3);
        var counterOnly = Position(occurrence: 4);

        List<WorkSlotView> slots =
        [
            View("pending-1", posA, 2, WorkSlotState.Pending),
            View("claimed-1", posB, 3, WorkSlotState.Claimed),
            View("recorded-1", posC, 1, WorkSlotState.Recorded),
            View("abandoned-1", posC, 2, WorkSlotState.Abandoned),
        ];
        List<WorkSlotRegistryAttemptEntry> attempts =
        [
            Entry(posA, 9),               // HIGHER than the slot's own attempt
            Entry(posB, 3),
            Entry(posC, 2),
            Entry(counterOnly, 7),        // counter-only: no slot references it
        ];

        var validated = GoalPipeline.ValidateDetachedRegistrySnapshot(
            new WorkSlotRegistrySnapshot(slots, attempts));

        // Sequence equality: same values, same order, same references — no reconstruction.
        Assert.Equal(slots, validated.Slots);
        Assert.Equal(attempts, validated.DispatchAttempts);

        // And the validated snapshot is still installable, preserving those exact values.
        var target = FreshTarget();
        target.RestoreRegistry(validated);
        Assert.Equal([.. slots], ViewsOf(target.CaptureRegistry()));
        Assert.Equal([.. attempts], AttemptsOf(target.CaptureRegistry()));
    }

    /// <summary>
    /// A LATE invalid entry still fails the whole validation: nothing partially valid is returned.
    /// Both a late slot defect and a late attempt-entry defect are covered.
    /// </summary>
    [Fact]
    public void Validate_LateInvalidEntry_FailsTheWholeValidation()
    {
        var posA = Position(occurrence: 1);
        var posB = Position(occurrence: 2);
        var posC = Position(occurrence: 3);

        // Late duplicate task ID, after two fully valid entries.
        Assert.Throws<ArgumentException>(() => GoalPipeline.ValidateDetachedRegistrySnapshot(
            new WorkSlotRegistrySnapshot(
                [
                    View("t1", posA, 2, WorkSlotState.Claimed),
                    View("t2", posB, 1, WorkSlotState.Recorded),
                    View("t1", posC, 1, WorkSlotState.Abandoned),
                ],
                [Entry(posA, 5), Entry(posB, 1), Entry(posC, 1)])));

        // Late duplicate high-water position.
        Assert.Throws<ArgumentException>(() => GoalPipeline.ValidateDetachedRegistrySnapshot(
            new WorkSlotRegistrySnapshot(
                [View("t1", posA, 2, WorkSlotState.Claimed)],
                [Entry(posA, 5), Entry(posB, 1), Entry(posB, 7)])));
    }

    /// <summary>
    /// The validator carries the restore path's ENTIRE rejection matrix — the same fixtures the
    /// restore tests feed, asserted directly against the extracted helper.
    /// </summary>
    [Theory]
    [MemberData(nameof(MalformedSnapshotKinds))]
    public void Validate_MalformedEntry_ThrowsArgumentException(string kind)
    {
        Assert.Throws<ArgumentException>(
            () => GoalPipeline.ValidateDetachedRegistrySnapshot(MalformedSnapshot(kind)));
    }

    [Theory]
    [MemberData(nameof(MalformedSlotPositionKinds))]
    public void Validate_MalformedSlotPosition_IsRejectedByTheSlotGuardItself(string kind)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => GoalPipeline.ValidateDetachedRegistrySnapshot(MalformedSnapshot(kind)));

        Assert.Contains("Snapshot slot 't1'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_NullSnapshotOrCollections_ThrowArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => GoalPipeline.ValidateDetachedRegistrySnapshot(null));
        Assert.Throws<ArgumentNullException>(
            () => GoalPipeline.ValidateDetachedRegistrySnapshot(new WorkSlotRegistrySnapshot(null!, [])));
        Assert.Throws<ArgumentNullException>(
            () => GoalPipeline.ValidateDetachedRegistrySnapshot(new WorkSlotRegistrySnapshot([], null!)));
    }

    /// <summary>
    /// The validator never compares historical positions against the CURRENT plan or machine
    /// phase: a registry full of positions from other iterations/phases than the pipeline's own
    /// validates and installs unchanged.
    /// </summary>
    [Fact]
    public void Validate_HistoricalPositions_AreNotComparedWithTheCurrentPlanOrMachine()
    {
        var stale = new WorkSlotPosition(7, GoalPhase.Improve, 3);
        var snapshot = new WorkSlotRegistrySnapshot(
            [View("old-1", stale, 4, WorkSlotState.Recorded)],
            [Entry(stale, 4)]);

        var validated = GoalPipeline.ValidateDetachedRegistrySnapshot(snapshot);

        // The pipeline sits at iteration 1 / Coding; the historical position is untouched.
        var target = CaptureFixture(installedPlan: FullPlan, machinePhase: GoalPhase.Coding);
        target.RestoreRegistry(validated);

        Assert.Equal(
            View("old-1", stale, 4, WorkSlotState.Recorded),
            Assert.Single(target.GetSlotsForTest()));
    }

    /// <summary>
    /// RestoreRegistry DELEGATES to the extracted validator instead of carrying a second copy of
    /// the rules — asserted against the compiled artifact, so a re-inlined duplicate validator
    /// fails deterministically.
    /// </summary>
    [Fact]
    public void Restore_DelegatesItsValidationToTheExtractedHelper()
    {
        var called = DecodeCallSites(RegistryMethod("RestoreRegistry"))
            .Any(c => c.Target.DeclaringType == typeof(GoalPipeline)
                && c.Target.Name == "ValidateDetachedRegistrySnapshot");

        Assert.True(
            called,
            "RestoreRegistry does not call ValidateDetachedRegistrySnapshot — the copy-and-validate " +
            "step must be the shared helper, not a second validator.");
    }

    // ── 2. The ownership capture ──────────────────────────────────────────────────────

    /// <summary>
    /// The capture, taken over a registry built by REAL allocation/transition paths and a REAL
    /// active-pointer operation: it reports the COMPLETE values (goal identity, pointer, every
    /// slot and every counter), and later pipeline mutations cannot reach the captured carrier.
    /// </summary>
    [Fact]
    public void CaptureAdmissionOwnership_RealPaths_ReportsCompleteValuesAndIsFrozenAtCapture()
    {
        var source = RealPathSource();
        source.SetActiveTask("pending-task");

        var captured = source.CaptureAdmissionOwnership();

        // COMPLETE values, compared against the registry itself — nothing reconstructed.
        Assert.Equal("goal-1", captured.GoalId);
        Assert.Equal("pending-task", captured.ActiveTaskId);
        Assert.Equal(ViewsOf(source.CaptureRegistry()), ViewsOf(captured.Registry));
        Assert.Equal(AttemptsOf(source.CaptureRegistry()), AttemptsOf(captured.Registry));
        Assert.Contains(View("pending-task", Position(occurrence: 4), 1, WorkSlotState.Pending), ViewsOf(captured.Registry));
        Assert.Contains(View("claimed-task", Position(occurrence: 1), 1, WorkSlotState.Claimed), ViewsOf(captured.Registry));

        var frozenSlots = ViewsOf(captured.Registry);
        var frozenAttempts = AttemptsOf(captured.Registry);

        // ── MUTATE THE SOURCE AFTERWARDS, through real production paths ───────────────
        Assert.Equal(SlotGuardResult.Proceed, source.ResolveAndCheckSlot("pending-task"));
        Assert.Equal(SlotRecordOutcome.Recorded, source.RecordSlot("pending-task"));
        source.AllocateAttemptAndRegisterSlot("latecomer", Position(occurrence: 5));
        Assert.True(source.ClearActiveTaskIfCurrent("pending-task"));

        // The carrier is frozen at the capture instant.
        Assert.Equal("pending-task", captured.ActiveTaskId);
        Assert.Equal(frozenSlots, ViewsOf(captured.Registry));
        Assert.Equal(frozenAttempts, AttemptsOf(captured.Registry));
        Assert.DoesNotContain(captured.Registry.Slots, v => v.Slot.TaskId == "latecomer");
    }

    /// <summary>
    /// The capture MUTATES NOTHING: no slot state changes, no attempt is allocated, no counter
    /// advances and the pointer is left exactly as it was (idle stays idle).
    /// </summary>
    [Fact]
    public void CaptureAdmissionOwnership_ChangesNoPointerRegistryOrCounter()
    {
        var source = RealPathSource();
        var beforeSlots = ViewsOf(source.CaptureRegistry());
        var beforeAttempts = AttemptsOf(source.CaptureRegistry());

        var idle = source.CaptureAdmissionOwnership();
        Assert.Null(idle.ActiveTaskId);   // an idle pipeline captures a null pointer honestly

        source.SetActiveTask("pending-task");
        var captured = source.CaptureAdmissionOwnership();
        Assert.Equal("pending-task", captured.ActiveTaskId);

        // Nothing moved: the same slots, the same states, the same counters, the same pointer.
        var after = source.CaptureRegistry();
        Assert.Equal(beforeSlots, ViewsOf(after));
        Assert.Equal(beforeAttempts, AttemptsOf(after));
        Assert.Equal("pending-task", source.ActiveTaskId);

        // The counters were not advanced by the capture: posC still continues from 1 → 2.
        Assert.Equal(2, source.AllocateAttemptAndRegisterSlot("after-capture", Position(occurrence: 3)).Attempt);
    }

    /// <summary>
    /// The carrier exposes NO live collection: mutating the returned lists cannot touch the
    /// registry it was taken from.
    /// </summary>
    [Fact]
    public void CaptureAdmissionOwnership_MutatingTheReturnedCollections_CannotTouchTheRegistry()
    {
        var source = RealPathSource();
        var before = ViewsOf(source.CaptureRegistry());
        var beforeAttempts = AttemptsOf(source.CaptureRegistry());

        var captured = source.CaptureAdmissionOwnership();
        ((List<WorkSlotView>)captured.Registry.Slots).Clear();
        ((List<WorkSlotRegistryAttemptEntry>)captured.Registry.DispatchAttempts).Clear();

        var after = source.CaptureRegistry();
        Assert.Equal(before, ViewsOf(after));
        Assert.Equal(beforeAttempts, AttemptsOf(after));
    }

    /// <summary>
    /// CaptureRegistry's own behaviour is UNCHANGED by the shared lock-held copy helper: it still
    /// returns the complete detached registry, and the two APIs agree value-for-value.
    /// </summary>
    [Fact]
    public void CaptureRegistry_BehaviourIsUnchangedAndAgreesWithTheOwnershipCapture()
    {
        var source = RealPathSource();
        source.SetActiveTask("pending-task");

        var registry = source.CaptureRegistry();
        var ownership = source.CaptureAdmissionOwnership();

        Assert.Equal(ViewsOf(registry), ViewsOf(ownership.Registry));
        Assert.Equal(AttemptsOf(registry), AttemptsOf(ownership.Registry));

        // Still a detached copy in its own right: hostile mutation cannot reach the registry.
        ((List<WorkSlotView>)registry.Slots).Clear();
        Assert.Equal(ViewsOf(ownership.Registry), ViewsOf(source.CaptureRegistry()));
    }

    /// <summary>
    /// THE SINGLE-ACQUISITION CONTRACT, asserted against the COMPILED ARTIFACT (deterministic —
    /// no timing anywhere): <c>CaptureAdmissionOwnership</c> must read the pointer AND the whole
    /// registry inside ONE <c>_lock</c> span, so it must not call any other locked registry entry
    /// point — above all <c>CaptureRegistry</c>, which would be a second, separate acquisition
    /// through which the pointer and the registry could drift apart.
    /// <para>
    /// The whole-operation lock structure itself (exactly one Enter/Exit on
    /// <c>GoalPipeline._lock</c>, covering every guarded access on every path) is pinned for this
    /// method by <see cref="RegistryEntryPoint_RunsWhollyUnderTheLock"/>, which now includes it.
    /// </para>
    /// </summary>
    [Fact]
    public void CaptureAdmissionOwnership_DoesNotNestAnyOtherLockedRegistryEntryPoint()
    {
        var lockedNames = LockedRegistryMethodNames.ToHashSet(StringComparer.Ordinal);

        var nested = DecodeCallSites(RegistryMethod("CaptureAdmissionOwnership"))
            .Where(c => c.Target.DeclaringType == typeof(GoalPipeline) && lockedNames.Contains(c.Target.Name))
            .Select(c => c.Target.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            nested.Count == 0,
            $"'CaptureAdmissionOwnership' calls locked registry entry point(s) [{string.Join(", ", nested)}] — " +
            "the pointer and the registry must be read under ONE acquisition, not two.");
    }

    /// <summary>
    /// The BEHAVIOURAL companion, using the suite's existing deterministic seam (the reflected
    /// <c>_lock</c> monitor) and its established <c>WhileLockHeld</c> pattern — no new harness,
    /// no stress, no sleep-based proof. Two facts only: the capture cannot complete while the
    /// pipeline lock is held, and once released it reports the COMPLETE coherent after-state.
    /// </summary>
    [Fact]
    public void CaptureAdmissionOwnership_WhileLockHeld_IsBlocked_ThenReportsCommittedState()
    {
        var pipeline = NewPipeline();
        var pos = Position();
        Assert.True(pipeline.SeedSlotForTest("t1", pos, 5, WorkSlotState.Pending));

        var monitor = GetPipelineLock(pipeline);

        using var callAttempted = new ManualResetEventSlim(false);
        using var callCompleted = new ManualResetEventSlim(false);
        AdmissionOwnershipSnapshot? workerResult = null;
        bool attemptObserved;
        bool completedWhileHeld;

        var worker = new Thread(() =>
        {
            callAttempted.Set();                                     // signalled IMMEDIATELY BEFORE the call…
            var captured = pipeline.CaptureAdmissionOwnership();     // …which parks on the pipeline lock
            Volatile.Write(ref workerResult, captured);
            callCompleted.Set();
        })
        {
            IsBackground = true,
            Name = "work-slot-blocked-ownership-capture",
        };

        Monitor.Enter(monitor);
        try
        {
            // The pointer is set from INSIDE the held region, so it is committed before the
            // parked capture can possibly observe anything.
            pipeline.SetActiveTask("t1");
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

        Assert.True(worker.Join(WaitTimeout), "The blocked capture never completed after the lock was released.");

        Assert.True(attemptObserved, "The worker thread never signalled its call attempt.");
        Assert.False(
            completedWhileHeld,
            "CaptureAdmissionOwnership completed while the pipeline lock was held — it is not running under the lock.");

        var result = Volatile.Read(ref workerResult);
        Assert.NotNull(result);
        Assert.Equal("goal-1", result!.GoalId);
        Assert.Equal("t1", result.ActiveTaskId);
        Assert.Equal(
            new WorkSlotView(new WorkSlot("t1", pos, 5), WorkSlotState.Pending),
            Assert.Single(result.Registry.Slots));
    }

    // ── 3. The admission preflight ────────────────────────────────────────────────────

    /// <summary>
    /// THE VALID VECTOR, built from real allocation and a real active-pointer operation: the
    /// active task owns a Pending slot, so the preflight succeeds and returns a carrier holding
    /// the COMPLETE values — identity, pointer, and every slot/counter of the registry.
    /// </summary>
    [Fact]
    public void Preflight_ValidPendingIdentity_ReturnsTheCompleteValidatedCarrier()
    {
        var source = RealPathSource();
        source.SetActiveTask("pending-task");
        var captured = source.CaptureAdmissionOwnership();

        var validated = GoalPipeline.PreflightAdmissionOwnership(captured);

        Assert.Equal("goal-1", validated.GoalId);
        Assert.Equal("pending-task", validated.ActiveTaskId);
        Assert.Equal(ViewsOf(captured.Registry), ViewsOf(validated.Registry));
        Assert.Equal(AttemptsOf(captured.Registry), AttemptsOf(validated.Registry));

        // The EXACT slot identity survives — never rebuilt from the task ID.
        Assert.Contains(View("pending-task", Position(occurrence: 4), 1, WorkSlotState.Pending), ViewsOf(validated.Registry));
    }

    /// <summary>
    /// The preflight preserves the COMPLETE history: historical slots in every state and
    /// counter-only high-water entries all survive, and NO requirement is imposed that every
    /// historical slot have a current mapping — only the ACTIVE task needs a Pending slot.
    /// </summary>
    [Fact]
    public void Preflight_PreservesHistoricalAndCounterOnlyEntries_WithoutRequiringAMappingForEach()
    {
        var posA = Position(occurrence: 1);
        var posB = Position(occurrence: 2);
        var posC = Position(occurrence: 3);
        var counterOnly = Position(occurrence: 4);

        List<WorkSlotView> slots =
        [
            View("recorded-1", posA, 1, WorkSlotState.Recorded),
            View("abandoned-1", posA, 2, WorkSlotState.Abandoned),
            View("claimed-1", posB, 1, WorkSlotState.Claimed),
            View("active-1", posC, 6, WorkSlotState.Pending),
        ];
        List<WorkSlotRegistryAttemptEntry> attempts =
        [
            Entry(posA, 2), Entry(posB, 1), Entry(posC, 9), Entry(counterOnly, 4),
        ];

        var validated = GoalPipeline.PreflightAdmissionOwnership(
            Ownership("goal-1", "active-1", slots, attempts));

        // Every entry survives, in the supplied order — including the counter-only one and the
        // high-water (9) that exceeds the active slot's own attempt (6).
        Assert.Equal(slots, validated.Registry.Slots);
        Assert.Equal(attempts, validated.Registry.DispatchAttempts);
    }

    // ── THE GUARD MESSAGES, quoted from production so each rejection test can pin the EXACT
    //    guard that must have fired rather than accepting any ArgumentException. Without these
    //    a rejection test passes through an unrelated fallback: e.g. deleting the blank-active
    //    guard still throws — from the later missing-slot check — so a bare Throws<> proves
    //    nothing about the guard it claims to cover.
    private const string BlankGoalGuard = "Admission goal ID must be a non-blank string";
    private const string BlankActiveTaskGuard = "Admission active task ID must be a non-blank string";
    private const string MissingSlotGuard = "has no matching slot in the registry";
    private const string NonPendingGuardFragment = "has a slot in state";

    /// <summary>
    /// Asserts that <paramref name="ex"/> came from <paramref name="expectedGuard"/> and from
    /// NONE of the other preflight guards — the isolation the reviewer's fallback finding
    /// requires. Each rejection test names one guard, so removing that guard changes the message
    /// (or removes the throw entirely) and the test fails.
    /// </summary>
    private static void AssertGuard(ArgumentException ex, string expectedGuard)
    {
        Assert.Contains(expectedGuard, ex.Message, StringComparison.Ordinal);

        foreach (var other in new[] { BlankGoalGuard, BlankActiveTaskGuard, MissingSlotGuard, NonPendingGuardFragment })
        {
            if (string.Equals(other, expectedGuard, StringComparison.Ordinal))
                continue;

            Assert.DoesNotContain(other, ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Preflight_MissingSlotForTheActiveTask_ThrowsArgumentException()
    {
        var pos = Position(occurrence: 1);

        var ex = Assert.Throws<ArgumentException>(() => GoalPipeline.PreflightAdmissionOwnership(
            Ownership("goal-1", "never-registered", [View("t1", pos, 1, WorkSlotState.Pending)], [Entry(pos, 1)])));

        Assert.Contains("never-registered", ex.Message, StringComparison.Ordinal);
        AssertGuard(ex, MissingSlotGuard);
    }

    /// <summary>
    /// An EMPTY registry is the degenerate missing-slot case: an active task can never match.
    /// </summary>
    [Fact]
    public void Preflight_EmptyRegistry_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => GoalPipeline.PreflightAdmissionOwnership(
            Ownership("goal-1", "t1", [], [])));

        AssertGuard(ex, MissingSlotGuard);
    }

    /// <summary>
    /// THE PENDING RULE, per state: a matching slot that is Claimed, Recorded or Abandoned is
    /// refused — only Pending is admissible. Each state is its own vector, and the message is
    /// pinned to the non-Pending guard so no other refusal can stand in for it.
    /// </summary>
    [Theory]
    [InlineData(StClaimed)]
    [InlineData(StRecorded)]
    [InlineData(StAbandoned)]
    public void Preflight_MatchingSlotNotPending_ThrowsArgumentException(int stateCode)
    {
        var pos = Position(occurrence: 1);

        var ex = Assert.Throws<ArgumentException>(() => GoalPipeline.PreflightAdmissionOwnership(
            Ownership("goal-1", "t1", [View("t1", pos, 1, St(stateCode))], [Entry(pos, 1)])));

        Assert.Contains(St(stateCode).ToString(), ex.Message, StringComparison.Ordinal);
        AssertGuard(ex, NonPendingGuardFragment);
    }

    /// <summary>
    /// The match is ORDINAL, exactly as the registry's own keying is: a case-different id is a
    /// DIFFERENT task and therefore has no matching slot — proved by the missing-slot guard
    /// firing, not merely by "something threw".
    /// </summary>
    [Fact]
    public void Preflight_TaskMatchIsOrdinal_CaseDifferenceIsNotAMatch()
    {
        var pos = Position(occurrence: 1);

        var ex = Assert.Throws<ArgumentException>(() => GoalPipeline.PreflightAdmissionOwnership(
            Ownership("goal-1", "T1", [View("t1", pos, 1, WorkSlotState.Pending)], [Entry(pos, 1)])));

        AssertGuard(ex, MissingSlotGuard);
    }

    /// <summary>
    /// THE BLANK-GOAL GUARD, isolated. The registry carries a fully valid active Pending mapping,
    /// so nothing else in the preflight can refuse this input: delete the blank-goal check and the
    /// call SUCCEEDS, failing the <c>Throws</c>. The message is pinned as well, so no other guard
    /// can be mistaken for this one.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Preflight_BlankGoalIdentity_ThrowsArgumentException(string? goalId)
    {
        var pos = Position(occurrence: 1);

        var ex = Assert.Throws<ArgumentException>(() => GoalPipeline.PreflightAdmissionOwnership(
            Ownership(goalId!, "t1", [View("t1", pos, 1, WorkSlotState.Pending)], [Entry(pos, 1)])));

        AssertGuard(ex, BlankGoalGuard);
    }

    /// <summary>
    /// THE BLANK-ACTIVE-TASK GUARD, isolated — the reviewer's named fallback.
    /// <para>
    /// A blank active task can NEVER match a slot, so removing the blank/null guard still yields
    /// an <see cref="ArgumentException"/>: the later missing-slot check fires instead. A bare
    /// <c>Throws&lt;ArgumentException&gt;</c> is therefore worthless here. This test pins the
    /// EXACT guard message and asserts the missing-slot wording is ABSENT, so deleting the
    /// blank/null active-task guard fails all three vectors deterministically.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Preflight_BlankOrNullActiveTask_ThrowsArgumentException(string? activeTaskId)
    {
        var pos = Position(occurrence: 1);

        var ex = Assert.Throws<ArgumentException>(() => GoalPipeline.PreflightAdmissionOwnership(
            Ownership("goal-1", activeTaskId, [View("t1", pos, 1, WorkSlotState.Pending)], [Entry(pos, 1)])));

        AssertGuard(ex, BlankActiveTaskGuard);
    }

    /// <summary>
    /// An IDLE pipeline captures a null pointer, and that carrier is refused by the BLANK-ACTIVE
    /// guard specifically — the same isolation as above, reached through a real capture rather
    /// than a hand-built carrier.
    /// </summary>
    [Fact]
    public void Preflight_IdlePipelineCapture_IsRefused()
    {
        var source = RealPathSource();

        var ex = Assert.Throws<ArgumentException>(
            () => GoalPipeline.PreflightAdmissionOwnership(source.CaptureAdmissionOwnership()));

        AssertGuard(ex, BlankActiveTaskGuard);
    }

    [Fact]
    public void Preflight_NullCarrierOrRegistryOrCollections_ThrowArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => GoalPipeline.PreflightAdmissionOwnership(null));
        Assert.Throws<ArgumentNullException>(() => GoalPipeline.PreflightAdmissionOwnership(
            new AdmissionOwnershipSnapshot("goal-1", "t1", null!)));
        Assert.Throws<ArgumentNullException>(() => GoalPipeline.PreflightAdmissionOwnership(
            Ownership("goal-1", "t1", null!, [])));
        Assert.Throws<ArgumentNullException>(() => GoalPipeline.PreflightAdmissionOwnership(
            Ownership("goal-1", "t1", [], null!)));
    }

    /// <summary>
    /// The position of the VALID active Pending slot injected into every malformed-registry
    /// preflight vector. It is distinct from every position <see cref="MalformedSnapshot"/> uses
    /// (which all sit at occurrence 1, or carry a malformed iteration/occurrence/phase), so it
    /// can never collide with the defect under test.
    /// </summary>
    private static WorkSlotPosition ActiveAdmissionPosition => Position(occurrence: 7);

    /// <summary>The task id of that valid active Pending slot.</summary>
    private const string ActiveAdmissionTaskId = "active-admission-task";

    /// <summary>
    /// THE ISOLATION FIXTURE for the malformed-registry preflight vectors.
    /// <para>
    /// It takes a <see cref="MalformedSnapshot"/> defect and prepends an otherwise-perfect ACTIVE
    /// PENDING slot with its own valid high-water entry. The resulting carrier is therefore valid
    /// in EVERY respect the admission-identity rules care about — the active task exists, it is
    /// Pending, and its counter is present — so the ONLY thing that can refuse it is the shared
    /// validator's inspection of the HISTORICAL / counter-only entries.
    /// </para>
    /// <para>
    /// WHY THIS MATTERS (the reviewer's named fallback): with the older fixtures the active task
    /// named the malformed slot itself, so bypassing entry validation still threw — from the
    /// missing-slot check, or (for the undefined-state kind) from the ordinary non-Pending check.
    /// A bare <c>Throws</c> therefore proved nothing about whether the entries were validated.
    /// With a valid active mapping present, skipping the entry validation makes the preflight
    /// SUCCEED, so every vector below genuinely proves the ENTIRE registry goes through the
    /// shared validator.
    /// </para>
    /// </summary>
    private static AdmissionOwnershipSnapshot MalformedRegistryWithValidActiveMapping(string kind)
    {
        var malformed = MalformedSnapshot(kind);

        List<WorkSlotView> slots =
        [
            View(ActiveAdmissionTaskId, ActiveAdmissionPosition, 1, WorkSlotState.Pending),
            .. malformed.Slots,
        ];
        List<WorkSlotRegistryAttemptEntry> attempts =
        [
            Entry(ActiveAdmissionPosition, 1),
            .. malformed.DispatchAttempts,
        ];

        return Ownership("goal-1", ActiveAdmissionTaskId, slots, attempts);
    }

    /// <summary>
    /// THE POSITIVE CONTROL that makes every malformed-registry vector below non-vacuous: the
    /// injected active mapping ON ITS OWN is fully valid and preflights SUCCESSFULLY. So when a
    /// vector below throws, the refusal is attributable to the HISTORICAL / counter-only entries
    /// alone — never to the active identity.
    /// </summary>
    [Fact]
    public void Preflight_TheInjectedActiveMappingAlone_IsValidAndSucceeds()
    {
        var validated = GoalPipeline.PreflightAdmissionOwnership(Ownership(
            "goal-1",
            ActiveAdmissionTaskId,
            [View(ActiveAdmissionTaskId, ActiveAdmissionPosition, 1, WorkSlotState.Pending)],
            [Entry(ActiveAdmissionPosition, 1)]));

        Assert.Equal(ActiveAdmissionTaskId, validated.ActiveTaskId);
        Assert.Equal(
            View(ActiveAdmissionTaskId, ActiveAdmissionPosition, 1, WorkSlotState.Pending),
            Assert.Single(validated.Registry.Slots));
    }

    /// <summary>
    /// A MALFORMED registry is refused by the preflight through the SHARED validator, even though
    /// the ADMISSION IDENTITY ITSELF IS PERFECTLY VALID (see
    /// <see cref="MalformedRegistryWithValidActiveMapping"/> and the positive control above).
    /// <para>
    /// The assertion also proves WHICH guard fired: the message must NOT be any of the
    /// admission-identity refusals, so a mutant that skipped entry validation could not pass by
    /// falling through to the missing-slot or non-Pending check — it would not throw at all.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(MalformedSnapshotKinds))]
    public void Preflight_MalformedRegistry_ThrowsArgumentException(string kind)
    {
        var ex = Assert.Throws<ArgumentException>(() => GoalPipeline.PreflightAdmissionOwnership(
            MalformedRegistryWithValidActiveMapping(kind)));

        // The refusal came from the registry validator, NOT from an admission-identity fallback.
        Assert.DoesNotContain(MissingSlotGuard, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(NonPendingGuardFragment, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(BlankGoalGuard, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(BlankActiveTaskGuard, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Snapshot", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The MIRROR of the restore path's slot-guard proof, applied to the preflight and with a
    /// valid active mapping present: the slot-POSITION defects must be rejected by the shared
    /// SLOT guard, whose message alone names the offending task.
    /// </summary>
    [Theory]
    [MemberData(nameof(MalformedSlotPositionKinds))]
    public void Preflight_MalformedRegistry_IsRejectedByTheSharedSlotGuard(string kind)
    {
        var ex = Assert.Throws<ArgumentException>(() => GoalPipeline.PreflightAdmissionOwnership(
            MalformedRegistryWithValidActiveMapping(kind)));

        Assert.Contains("Snapshot slot 't1'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(MissingSlotGuard, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(NonPendingGuardFragment, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The COUNTER-ONLY mirror, with a valid active mapping present: an isolated malformed
    /// high-water entry that NO slot references — the active one included — can only be rejected
    /// by the shared attempt-entry guard, whose message names an attempt entry.
    /// <para>
    /// This is the sharpest form of the reviewer's requirement: nothing about the admission
    /// identity is wrong here, and the defect lives purely in counter history, so the vector
    /// passes only if counter-only entries genuinely go through the shared validator.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("counter-only-zero-iteration")]
    [InlineData("counter-only-negative-iteration")]
    [InlineData("counter-only-zero-occurrence")]
    [InlineData("counter-only-negative-occurrence")]
    [InlineData("counter-only-undefined-phase")]
    public void Preflight_MalformedCounterOnlyEntry_IsRejectedByTheSharedAttemptEntryGuard(string kind)
    {
        var ex = Assert.Throws<ArgumentException>(() => GoalPipeline.PreflightAdmissionOwnership(
            MalformedRegistryWithValidActiveMapping(kind)));

        Assert.Contains("Snapshot attempt entry", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(MissingSlotGuard, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(NonPendingGuardFragment, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The HISTORICAL cross-entry invariants are enforced for the preflight too, again with a
    /// perfectly valid active mapping present. Each vector's defect is confined to historical
    /// slots/counters, so only the shared validator can produce the refusal.
    /// </summary>
    [Theory]
    [InlineData("historical-duplicate-task-id", "duplicate task ID")]
    [InlineData("historical-duplicate-allocation", "duplicate (position, attempt) allocation")]
    [InlineData("historical-duplicate-high-water", "duplicate attempt entries for position")]
    [InlineData("historical-multiple-live-slots", "multiple live slots at position")]
    [InlineData("historical-missing-counter", "has no attempt entry for its position")]
    [InlineData("historical-attempt-exceeds-high-water", "exceeding its")]
    public void Preflight_MalformedHistoricalCrossEntry_IsRejectedByTheSharedValidator(
        string kind, string expectedFragment)
    {
        var histA = Position(occurrence: 1);
        var histB = Position(occurrence: 2);

        // Every vector starts from the SAME valid active mapping; only the historical entries
        // differ, so nothing about the admission identity can be responsible for the refusal.
        List<WorkSlotView> slots = [View(ActiveAdmissionTaskId, ActiveAdmissionPosition, 1, WorkSlotState.Pending)];
        List<WorkSlotRegistryAttemptEntry> attempts = [Entry(ActiveAdmissionPosition, 1)];

        switch (kind)
        {
            case "historical-duplicate-task-id":
                slots.Add(View("hist", histA, 1, WorkSlotState.Recorded));
                slots.Add(View("hist", histB, 1, WorkSlotState.Recorded));
                attempts.Add(Entry(histA, 1));
                attempts.Add(Entry(histB, 1));
                break;
            case "historical-duplicate-allocation":
                slots.Add(View("hist-1", histA, 2, WorkSlotState.Recorded));
                slots.Add(View("hist-2", histA, 2, WorkSlotState.Abandoned));
                attempts.Add(Entry(histA, 2));
                break;
            case "historical-duplicate-high-water":
                attempts.Add(Entry(histA, 1));
                attempts.Add(Entry(histA, 2));
                break;
            case "historical-multiple-live-slots":
                slots.Add(View("hist-1", histA, 1, WorkSlotState.Claimed));
                slots.Add(View("hist-2", histA, 2, WorkSlotState.Claimed));
                attempts.Add(Entry(histA, 2));
                break;
            case "historical-missing-counter":
                slots.Add(View("hist-1", histA, 1, WorkSlotState.Recorded));
                break;   // histA's counter is deliberately absent
            case "historical-attempt-exceeds-high-water":
                slots.Add(View("hist-1", histA, 5, WorkSlotState.Recorded));
                attempts.Add(Entry(histA, 4));
                break;
            default:
                throw new InvalidOperationException($"Unhandled historical vector '{kind}'.");
        }

        var ex = Assert.Throws<ArgumentException>(() => GoalPipeline.PreflightAdmissionOwnership(
            Ownership("goal-1", ActiveAdmissionTaskId, slots, attempts)));

        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(MissingSlotGuard, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(NonPendingGuardFragment, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE DETACHMENT PROOF for the preflight result: after a SUCCESSFUL preflight, mutating the
    /// caller's own input lists cannot alter the validated carrier — the result holds copies, not
    /// the caller's collections.
    /// </summary>
    [Fact]
    public void Preflight_MutatingTheInputListsAfterwards_CannotAlterTheValidatedResult()
    {
        var posA = Position(occurrence: 1);
        var posB = Position(occurrence: 2);

        var slots = new List<WorkSlotView>
        {
            View("history-1", posA, 1, WorkSlotState.Recorded),
            View("active-1", posB, 2, WorkSlotState.Pending),
        };
        var attempts = new List<WorkSlotRegistryAttemptEntry> { Entry(posA, 1), Entry(posB, 2) };

        var validated = GoalPipeline.PreflightAdmissionOwnership(
            Ownership("goal-1", "active-1", slots, attempts));

        var frozenSlots = validated.Registry.Slots.ToList();
        var frozenAttempts = validated.Registry.DispatchAttempts.ToList();

        // Hostile mutation of the caller's lists AFTER the successful preflight.
        slots.Clear();
        slots.Add(View("injected", Position(occurrence: 99), 1, WorkSlotState.Claimed));
        attempts.Clear();
        attempts.Add(Entry(Position(occurrence: 99), 42));

        Assert.Equal(frozenSlots, validated.Registry.Slots);
        Assert.Equal(frozenAttempts, validated.Registry.DispatchAttempts);
        Assert.DoesNotContain(validated.Registry.Slots, v => v.Slot.TaskId == "injected");
        Assert.Equal("active-1", validated.ActiveTaskId);
    }

    /// <summary>
    /// The preflight is PURE with respect to the pipeline it was captured from: running it (in
    /// either the success or the refusal direction) leaves the source registry, counters and
    /// pointer exactly as they were.
    /// </summary>
    [Fact]
    public void Preflight_LeavesTheCapturedPipelineCompletelyUntouched()
    {
        var source = RealPathSource();
        source.SetActiveTask("pending-task");

        var beforeSlots = ViewsOf(source.CaptureRegistry());
        var beforeAttempts = AttemptsOf(source.CaptureRegistry());

        GoalPipeline.PreflightAdmissionOwnership(source.CaptureAdmissionOwnership());
        Assert.Throws<ArgumentException>(() => GoalPipeline.PreflightAdmissionOwnership(
            new AdmissionOwnershipSnapshot("goal-1", "claimed-task", source.CaptureAdmissionOwnership().Registry)));

        var after = source.CaptureRegistry();
        Assert.Equal(beforeSlots, ViewsOf(after));
        Assert.Equal(beforeAttempts, AttemptsOf(after));
        Assert.Equal("pending-task", source.ActiveTaskId);
    }

    /// <summary>
    /// The preflight REUSES the extracted validator rather than carrying its own copy — asserted
    /// against the compiled artifact, so a duplicated validation path fails deterministically.
    /// </summary>
    [Fact]
    public void Preflight_DelegatesRegistryValidationToTheSharedHelper()
    {
        var called = DecodeCallSites(RegistryMethod("PreflightAdmissionOwnership"))
            .Any(c => c.Target.DeclaringType == typeof(GoalPipeline)
                && c.Target.Name == "ValidateDetachedRegistrySnapshot");

        Assert.True(
            called,
            "PreflightAdmissionOwnership does not call ValidateDetachedRegistrySnapshot — the registry " +
            "rules must be reused, not duplicated.");
    }

    /// <summary>
    /// The preflight is a PURE domain operation: it takes NO lock at all (it is static and never
    /// touches a pipeline's storage), so it can never contend with, or nest inside, the registry
    /// monitor. Asserted against the compiled artifact.
    /// </summary>
    [Fact]
    public void Preflight_TakesNoLockAndTouchesNoPipelineStorage()
    {
        var method = RegistryMethod("PreflightAdmissionOwnership");
        Assert.True(method.IsStatic, "PreflightAdmissionOwnership must be static — it validates a detached carrier.");

        var calls = DecodeCallSites(method);
        Assert.DoesNotContain(calls, c => IsMonitor(c.Target, nameof(Monitor.Enter)));
        Assert.DoesNotContain(calls, c => IsMonitor(c.Target, nameof(Monitor.Exit)));
    }

    #endregion
}

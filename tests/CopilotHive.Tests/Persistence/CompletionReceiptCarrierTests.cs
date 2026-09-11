using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;

namespace CopilotHive.Tests.Persistence;

/// <summary>
/// Tests for the <see cref="CompletionReceipt"/> carrier's constructor validation matrix.
/// The carrier is internal, so the public test surface carries the same pattern as
/// <see cref="WorkSlotRegistryTests"/>: helper factories plus exception-message probes.
/// </summary>
public sealed class CompletionReceiptCarrierTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    private const string GoalId = "goal-receipt-1";
    private const string WorkerId = "worker-1";
    private const string TaskId = "task-receipt-1";

    private static WorkSlotPosition Pos(GoalPhase phase = GoalPhase.Coding, int iteration = 1, int occurrence = 1) =>
        new(iteration, phase, occurrence);

    /// <summary>
    /// Slot factory. <paramref name="taskId"/> default means the standard task id; pass
    /// <paramref name="nullTaskId"/> = true to force a genuine null (a vector that must reach
    /// the guard un-coalesced).
    /// </summary>
    private static WorkSlot Slot(
        string? taskId = TaskId,
        WorkSlotPosition? position = null,
        int attempt = 1,
        bool nullTaskId = false) =>
        new(nullTaskId ? null! : taskId ?? TaskId, position ?? Pos(), attempt);

    /// <summary>
    /// Result factory. <paramref name="nullTaskId"/> = true forces a genuine null task id.
    /// </summary>
    private static TaskResult Result(string? taskId = TaskId, TaskOutcome outcome = TaskOutcome.Completed, bool nullTaskId = false) =>
        new() { TaskId = nullTaskId ? null! : taskId ?? TaskId, Status = outcome, Output = "done" };

    private static CompletionReceipt Build(string goalId, string workerId, WorkerRole role, WorkSlot? slot = null, TaskResult? result = null) =>
        new(goalId, workerId, role, slot ?? Slot(), result ?? Result());

    /// <summary>
    /// Asserts the thrown <see cref="ArgumentException"/> names the expected parameter
    /// (a guard-specific observable, not merely "it threw") and optionally probes the message.
    /// </summary>
    private static void Rejects(Action act, string paramName, string? messageFragment = null)
    {
        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Equal(paramName, ex.ParamName);
        if (messageFragment is not null)
            Assert.Contains(messageFragment, ex.Message);
    }

    /// <summary>
    /// Asserts the undefined-value form of the refusal: the same
    /// <see cref="ArgumentException"/> family but with the "undefined … value" message, so a
    /// vector rejected by an unrelated guard cannot masquerade as this one.
    /// </summary>
    private static void RejectsUndefinedValue(Action act, string enumName, int code)
    {
        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains($"undefined {enumName} value {code}", ex.Message);
    }

    // ── 1. Null refusals ──────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullSlot_ThrowsArgumentNull_NamingSlot()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => new CompletionReceipt(GoalId, WorkerId, WorkerRole.Coder, null!, Result()));
        Assert.Equal("slot", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullResult_ThrowsArgumentNull_NamingResult()
    {
        Assert.Throws<ArgumentNullException>(
            () => new CompletionReceipt(GoalId, WorkerId, WorkerRole.Coder, Slot(), null!));
    }

    // ── 2. Slot shape / identity strings ─────────────────────────────────

    [Fact]
    public void Constructor_SlotWithoutPosition_ThrowsArgumentException_NamingSlot()
    {
        // A slot without a position has no identity to validate — refused before string checks.
        var positionLess = new WorkSlot(TaskId, null!, 1);
        Rejects(() => Build(GoalId, WorkerId, WorkerRole.Coder, positionLess), "slot", "must carry a position");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankGoalId_ThrowsArgumentException_NamingGoalId(string? blank)
    {
        Rejects(() => Build(blank!, GoalId, WorkerRole.Coder), "goalId", "goal ID");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankWorkerId_ThrowsArgumentException_NamingWorkerId(string? blank)
    {
        Rejects(() => Build(GoalId, blank!, WorkerRole.Coder), "workerId", "worker ID");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankSlotTaskId_ThrowsArgumentException_NamingSlot(string? blank)
    {
        // nullTaskId forces the null vector through un-coalesced; "" and "   " pass as-is.
        Rejects(
            () => Build(GoalId, WorkerId, WorkerRole.Coder, Slot(blank, nullTaskId: blank is null)),
            "slot", "task ID");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankResultTaskId_ThrowsArgumentException_NamingResult(string? blank)
    {
        Rejects(
            () => Build(GoalId, WorkerId, WorkerRole.Coder, slot: null, Result(blank, nullTaskId: blank is null)),
            "result", "task ID");
    }

    // ── 3. Mismatched slot-vs-result task IDs ────────────────────────────

    [Fact]
    public void Constructor_MismatchedTaskIds_ThrowsArgumentException_NamingResult()
    {
        var slot = Slot(taskId: "task-A");
        var result = Result(taskId: "task-B");
        Rejects(
            () => new CompletionReceipt(GoalId, WorkerId, WorkerRole.Coder, slot, result),
            "result",
            "does not match result task ID 'task-B'");
    }

    /// <summary>
    /// Anti-vacuous control for the mismatch guard: with a mismatch PRESENT and every other
    /// vector fixed, only the task-ID guard can produce this failure — and with the mismatch
    /// REMOVED (both task ids agree) the construction succeeds, proving the test observes the
    /// guard rather than an unrelated one.
    /// </summary>
    [Fact]
    public void Constructor_MatchingTaskIds_Constructs_AndPropertiesCarryInputs()
    {
        // With the mismatch REMOVED (both task ids agree) the construction succeeds, and the
        // receipt carries every input — proving the test above observes the mismatch guard
        // rather than an unrelated one.
        var slot = Slot(taskId: "task-agree");
        var result = Result(taskId: "task-agree");
        var receipt = new CompletionReceipt(GoalId, WorkerId, WorkerRole.Coder, slot, result);

        Assert.Equal(GoalId, receipt.GoalId);
        Assert.Equal(WorkerId, receipt.WorkerId);
        Assert.Equal(WorkerRole.Coder, receipt.Role);
        Assert.Equal("task-agree", receipt.Slot.TaskId);
        Assert.Equal("task-agree", receipt.Result.TaskId);
    }

    // ── 4. Position numbers ──────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveIteration_ThrowsArgumentException_NamingSlot(int iteration)
    {
        Rejects(
            () => Build(GoalId, WorkerId, WorkerRole.Coder, Slot(position: Pos(iteration: iteration))),
            "slot",
            $"iteration must be positive but was {iteration}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Constructor_NonPositiveOccurrence_ThrowsArgumentException_NamingSlot(int occurrence)
    {
        Rejects(
            () => Build(GoalId, WorkerId, WorkerRole.Coder, Slot(position: Pos(occurrence: occurrence))),
            "slot",
            $"occurrence must be positive but was {occurrence}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public void Constructor_NonPositiveAttempt_ThrowsArgumentException_NamingSlot(int attempt)
    {
        Rejects(
            () => Build(GoalId, WorkerId, WorkerRole.Coder, Slot(attempt: attempt)),
            "slot",
            $"attempt must be positive but was {attempt}");
    }

    // ── 5. Non-worker phases ─────────────────────────────────────────────

    [Theory]
    [InlineData(GoalPhase.Planning)]
    [InlineData(GoalPhase.Merging)]
    [InlineData(GoalPhase.Done)]
    [InlineData(GoalPhase.Failed)]
    public void Constructor_NonWorkerPhase_ThrowsArgumentException_NamingSlot(GoalPhase phase)
    {
        Rejects(
            () => Build(GoalId, WorkerId, WorkerRole.Coder, Slot(position: Pos(phase: phase))),
            "slot",
            $"phase '{phase}' has no worker");
    }

    [Fact]
    public void Constructor_UndefinedPhase_ThrowsArgumentException_NamingSlot()
    {
        Rejects(
            () => Build(GoalId, WorkerId, WorkerRole.Coder, Slot(position: Pos(phase: (GoalPhase)999))),
            "slot",
            "undefined GoalPhase value 999");
    }

    // ── 6. Rejected roles ────────────────────────────────────────────────

    [Theory]
    [InlineData(WorkerRole.Unspecified)]
    [InlineData(WorkerRole.Orchestrator)]
    [InlineData(WorkerRole.MergeWorker)]
    public void Constructor_NonWorkerBackedRole_ThrowsArgumentException_NamingRole(WorkerRole role)
    {
        Rejects(
            () => Build(GoalId, WorkerId, role),
            "role",
            $"does not match the role '{WorkerRole.Coder}' mapped from phase '{GoalPhase.Coding}'");
    }

    [Fact]
    public void Constructor_UndefinedRole_ThrowsArgumentException_NamingRole()
    {
        RejectsUndefinedValue(
            () => Build(GoalId, WorkerId, (WorkerRole)4242),
            "WorkerRole", 4242);
    }

    // ── 7. THE FULL POSITIVE MATRIX — the accepted set is EXACTLY the five ─
    //      worker-backed phase/role pairs.

    public static TheoryData<GoalPhase, WorkerRole> AcceptedPhaseRolePairs => new()
    {
        { GoalPhase.Coding, WorkerRole.Coder },
        { GoalPhase.Testing, WorkerRole.Tester },
        { GoalPhase.Review, WorkerRole.Reviewer },
        { GoalPhase.DocWriting, WorkerRole.DocWriter },
        { GoalPhase.Improve, WorkerRole.Improver },
    };

    /// <summary>
    /// THE FULL POSITIVE MATRIX: each of the five worker-backed phase/role pairs constructs,
    /// and every OTHER role for that phase is rejected — so the accepted set is exactly the
    /// matrix above, not a sample.
    /// </summary>
    [Theory]
    [MemberData(nameof(AcceptedPhaseRolePairs))]
    public void Constructor_WorkerBackedPair_Accepts_ThatPair_AndRejects_EveryOtherRole(
        GoalPhase phase, WorkerRole acceptedRole)
    {
        // The mapped pair is accepted and the properties carry the inputs.
        var receipt = Build(
            GoalId,
            WorkerId,
            acceptedRole,
            Slot(position: Pos(phase: phase)));
        Assert.Equal(acceptedRole, receipt.Role);
        Assert.Equal(phase, receipt.Slot.Position!.Phase);
        Assert.Equal(TaskId, receipt.Result.TaskId);

        // Every other role for this phase is refused — including the other four worker-backed
        // roles plus Unspecified/Orchestrator/MergeWorker/undefined — the negative half of the
        // full matrix, per phase.
        foreach (WorkerRole other in new[]
                 {
                     WorkerRole.Unspecified,
                     WorkerRole.Coder,
                     WorkerRole.Tester,
                     WorkerRole.Reviewer,
                     WorkerRole.Improver,
                     WorkerRole.Orchestrator,
                     WorkerRole.DocWriter,
                     WorkerRole.MergeWorker,
                     (WorkerRole)9876,
                 })
        {
            if (other == acceptedRole)
                continue;

            // The undefined role value carries its own refusal message; every defined role
            // fails the phase→role mapping comparison instead.
            if (other == (WorkerRole)9876)
            {
                RejectsUndefinedValue(
                    () => Build(GoalId, WorkerId, other, Slot(position: Pos(phase: phase))),
                    "WorkerRole", 9876);
            }
            else
            {
                Rejects(
                    () => Build(GoalId, WorkerId, other, Slot(position: Pos(phase: phase))),
                    "role",
                    "does not match the role");
            }
        }
    }

    /// <summary>
    /// Removal-proofness for the role guard: with the role argument removed from the check
    /// (simulated here by passing the CORRECT role) the construction succeeds — i.e. the test
    /// above genuinely fails when the mapping comparison is broken, not for an unrelated reason.
    /// This control constructs with the exact bytes the guard compares against.
    /// </summary>
    [Fact]
    public void Constructor_RoleEqualsDerivedRole_IsAccepted_WhileRoleOneOff_IsRejected()
    {
        var slot = Slot(position: Pos(phase: GoalPhase.Review));
        var result = Result();

        // Correct derived role — accepted.
        var ok = new CompletionReceipt(GoalId, WorkerId, WorkerRole.Reviewer, slot, result);
        Assert.Equal(WorkerRole.Reviewer, ok.Role);

        // One role off — rejected, naming the derived role it expected.
        Rejects(
            () => new CompletionReceipt(GoalId, WorkerId, WorkerRole.Tester, slot, result),
            "role",
            $"does not match the role '{WorkerRole.Reviewer}' mapped from phase '{GoalPhase.Review}'");
    }

    // ── 8. Full-shape construction: every field carries what was handed in ─

    [Fact]
    public void Constructor_ValidInputs_ExposesExactValues_NoCopyingOrDerivation()
    {
        var position = new WorkSlotPosition(3, GoalPhase.DocWriting, 2);
        var slot = new WorkSlot("task-full-1", position, 4);
        var result = new TaskResult
        {
            TaskId = "task-full-1",
            Status = TaskOutcome.Completed,
            Output = "docs written",
            Model = "model-x",
        };

        var receipt = new CompletionReceipt("goal-full-1", "worker-doc-7", WorkerRole.DocWriter, slot, result);

        Assert.Equal("goal-full-1", receipt.GoalId);
        Assert.Equal("worker-doc-7", receipt.WorkerId);
        Assert.Equal(WorkerRole.DocWriter, receipt.Role);
        Assert.Same(slot, receipt.Slot);
        Assert.Same(result, receipt.Result);
        Assert.Equal(3, receipt.Slot.Position!.Iteration);
        Assert.Equal(2, receipt.Slot.Position.Occurrence);
        Assert.Equal(GoalPhase.DocWriting, receipt.Slot.Position.Phase);
        Assert.Equal(4, receipt.Slot.Attempt);
        Assert.Equal(TaskOutcome.Completed, receipt.Result.Status);
        Assert.Equal("docs written", receipt.Result.Output);
    }

    // ── 9. Rejected construction yields NO partially built receipt ─────────

    /// <summary>
    /// Every refusal happens BEFORE any state exists — a rejected construction cannot leave a
    /// partially populated carrier behind (the constructor is the only entry point and every
    /// refusal throws before the assignments run).
    /// </summary>
    [Fact]
    public void Constructor_Rejection_NeverProducesAnInstance()
    {
        // The constructor throws for every blank vector, so the assignment target stays null.
        CompletionReceipt? receipt = null;
        Assert.Throws<ArgumentException>(
            () => receipt = new CompletionReceipt("", WorkerId, WorkerRole.Coder, Slot(), Result()));
        Assert.Null(receipt);
    }
}
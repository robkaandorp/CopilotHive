using System.Collections.Concurrent;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Metrics;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Workers;

namespace CopilotHive.Services;

/// <summary>
/// Tracks a single goal's progress through the multi-phase pipeline.
/// Thread-safe: state mutations are guarded by a lock.
/// </summary>
public sealed class GoalPipeline
{
    private readonly object _lock = new();

    /// <summary>
    /// Work-slot registry: every registered slot keyed by its task ID, paired with its
    /// current <see cref="WorkSlotState"/>. Guarded by <c>_lock</c>.
    /// </summary>
    private readonly Dictionary<string, (WorkSlot Slot, WorkSlotState State)> _slots = [];

    /// <summary>
    /// Per-position dispatch attempt counters keyed by the full
    /// <see cref="WorkSlotPosition"/> tuple (iteration, phase, occurrence).
    /// Guarded by <c>_lock</c>.
    /// </summary>
    private readonly Dictionary<WorkSlotPosition, int> _dispatchAttempts = [];

    /// <summary>
    /// The phase sequence INSTALLED on the state machine for the current iteration, or
    /// <c>null</c> when no plan is installed. It is a defensive COPY of the plan's phase list,
    /// taken at install time, and is only ever REPLACED — never mutated in place — so a reference
    /// handed to <see cref="PipelineStateMachine.CapturePosition"/> satisfies that method's
    /// stable-input covenant without holding any lock across the call. Guarded by <c>_lock</c>.
    /// </summary>
    private List<GoalPhase>? _installedPhases;

    /// <summary>Unique identifier of the goal this pipeline is tracking.</summary>
    public string GoalId { get; }
    /// <summary>Human-readable description of the goal.</summary>
    public string Description { get; }
    /// <summary>The goal this pipeline is working toward.</summary>
    public Goal Goal { get; }

    /// <summary>State machine that enforces valid phase transitions.</summary>
    public PipelineStateMachine StateMachine { get; } = new();

    /// <summary>Current phase the pipeline is executing.</summary>
    public GoalPhase Phase { get; private set; } = GoalPhase.Planning;

    /// <summary>Budget tracking review retry attempts.</summary>
    public RetryBudget ReviewRetryBudget { get; }

    /// <summary>Budget tracking test retry attempts.</summary>
    public RetryBudget TestRetryBudget { get; }

    /// <summary>Budget tracking iteration attempts (depleted each time a new iteration starts).</summary>
    public RetryBudget IterationBudget { get; }

    /// <summary>One-based iteration counter; increments each time the pipeline loops.</summary>
    public int Iteration => IterationBudget.Used + 1;

    /// <summary>Number of times the review phase has been retried in the current iteration.</summary>
    public int ReviewRetries => ReviewRetryBudget.Used;

    /// <summary>Number of times the test phase has been retried in the current iteration.</summary>
    public int TestRetries => TestRetryBudget.Used;

    /// <summary>Maximum number of task-level retries allowed.</summary>
    public int MaxRetries => ReviewRetryBudget.Allowed;

    /// <summary>Maximum number of iterations allowed before the goal is failed.</summary>
    public int MaxIterations => IterationBudget.Allowed + 1;

    /// <summary>Brain-determined plan for the current iteration, or null if no plan set.</summary>
    public IterationPlan? Plan { get; private set; }

    /// <summary>The active task ID currently assigned to a worker (null when idle).</summary>
    public string? ActiveTaskId { get; private set; }

    /// <summary>Grouped branch-related state (feature branch, iteration SHA, merge hash).</summary>
    public BranchContext Branch { get; } = new();

    /// <summary>The feature branch created by the coder for this goal.</summary>
    public string? CoderBranch
    {
        get => Branch.CoderBranch;
        set => Branch.CoderBranch = value;
    }

    /// <summary>
    /// Persisted agent session JSON blobs, keyed by role name (case-insensitive).
    /// Allows workers to resume mid-session after an orchestrator restart.
    /// </summary>
    public RoleSessionStore RoleSessions { get; } = new();

    /// <summary>In-memory iteration summaries for completed iterations (available to dashboard before goal finishes).</summary>
    public List<IterationSummary> CompletedIterationSummaries { get; } = [];

    /// <summary>Append-only log of phase entries, one per phase dispatch, in chronological order.</summary>
    public List<PhaseResult> PhaseLog { get; } = [];

    /// <summary>The most recently appended log entry, or <c>null</c> if no phase has started yet.</summary>
    public PhaseResult? CurrentPhaseEntry => PhaseLog.Count > 0 ? PhaseLog[^1] : null;

    /// <summary>All clarifications Q&amp;As that occurred during this goal's execution.</summary>
    public ConcurrentBag<ClarificationEntry> Clarifications { get; } = [];

    /// <summary>
    /// Progress reports (<c>report_progress</c> tool calls) generated during this goal's execution.
    /// Stored per-pipeline for Phase/Iteration-aware dashboard filtering.
    /// </summary>
    public ConcurrentBag<ProgressEntry> ProgressReports { get; } = [];

    /// <summary>
    /// Worker narrative reports (report_narrative tool calls) generated during this goal's execution.
    /// </summary>
    public ConcurrentBag<NarrativeEntry> Narratives { get; } = [];

    /// <summary>
    /// When <c>true</c>, the active phase is paused waiting for a clarification answer.
    /// The dashboard displays this phase with status "waiting".
    /// </summary>
    public bool IsWaitingForClarification { get; set; }

    /// <summary>Metrics extracted by the Brain from worker output.</summary>
    public IterationMetrics Metrics { get; } = new() { Iteration = 1 };

    /// <summary>Owns the per-goal conversation history and context-summary logic.</summary>
    public ConversationTracker ConversationTracker { get; } = new();

    /// <summary>Per-goal conversation history for the Brain.</summary>
    public List<ConversationEntry> Conversation => ConversationTracker.Entries;

    /// <summary>UTC timestamp when the goal was started (captured at dispatch time, before the pipeline is created).</summary>
    public DateTime? GoalStartedAt { get; internal set; }

    /// <summary>UTC timestamp when this pipeline was created.</summary>
    public DateTime CreatedAt { get; private init; } = DateTime.UtcNow;
    /// <summary>UTC timestamp when this pipeline completed (Done or Failed), or <c>null</c> if still active.</summary>
    public DateTime? CompletedAt { get; private set; }
    /// <summary>SHA-1 hash of the merge commit, or <c>null</c> if not yet merged.</summary>
    public string? MergeCommitHash
    {
        get => Branch.MergeCommitHash;
        set => Branch.MergeCommitHash = value;
    }

    /// <summary>HEAD SHA captured before the coder ran this iteration, or <c>null</c>.</summary>
    public string? IterationStartSha
    {
        get => Branch.IterationStartSha;
        set => Branch.IterationStartSha = value;
    }

    /// <summary>
    /// Creates a new pipeline for the specified goal.
    /// </summary>
    /// <param name="goal">The goal to track.</param>
    /// <param name="maxRetries">Maximum task-level retries allowed.</param>
    /// <param name="maxIterations">Maximum iterations before the goal is failed.</param>
    public GoalPipeline(Goal goal, int maxRetries = Constants.DefaultMaxRetriesPerTask, int maxIterations = Constants.DefaultMaxIterations)
    {
        Goal = goal;
        GoalId = goal.Id;
        Description = goal.Description;
        ReviewRetryBudget = new RetryBudget(maxRetries);
        TestRetryBudget = new RetryBudget(maxRetries);
        IterationBudget = new RetryBudget(maxIterations - 1);
    }

    /// <summary>Restore a pipeline from a persisted snapshot.</summary>
    internal GoalPipeline(PipelineSnapshot snapshot)
    {
        Goal = snapshot.Goal;
        GoalId = snapshot.GoalId;
        Description = snapshot.Description;
        Phase = snapshot.Phase;

        // Restore budgets from persisted scalar values.
        // IterationBudget: allowed = maxIterations - 1, used = iteration - 1
        IterationBudget = new RetryBudget(snapshot.MaxIterations - 1);
        for (var i = 0; i < snapshot.Iteration - 1; i++)
            IterationBudget.TryConsume();

        ReviewRetryBudget = new RetryBudget(snapshot.MaxRetries);
        for (var i = 0; i < snapshot.ReviewRetries; i++)
            ReviewRetryBudget.TryConsume();

        TestRetryBudget = new RetryBudget(snapshot.MaxRetries);
        for (var i = 0; i < snapshot.TestRetries; i++)
            TestRetryBudget.TryConsume();

        ActiveTaskId = snapshot.ActiveTaskId;
        Branch.CoderBranch = snapshot.CoderBranch;
        Plan = snapshot.Plan;
        CreatedAt = snapshot.CreatedAt;
        CompletedAt = snapshot.CompletedAt;
        GoalStartedAt = snapshot.GoalStartedAt;
        Branch.MergeCommitHash = snapshot.MergeCommitHash;
        Branch.IterationStartSha = snapshot.IterationStartSha;

        RoleSessions.Load(snapshot.RoleSessions);

        Metrics.BuildSuccess = snapshot.Metrics.BuildSuccess;
        Metrics.TotalTests = snapshot.Metrics.TotalTests;
        Metrics.PassedTests = snapshot.Metrics.PassedTests;
        Metrics.FailedTests = snapshot.Metrics.FailedTests;
        Metrics.CoveragePercent = snapshot.Metrics.CoveragePercent;
        Metrics.IntegrationTestsTotal = snapshot.Metrics.IntegrationTestsTotal;
        Metrics.IntegrationTestsPassed = snapshot.Metrics.IntegrationTestsPassed;
        Metrics.RuntimeVerified = snapshot.Metrics.RuntimeVerified;

        foreach (var entry in snapshot.Conversation)
            Conversation.Add(entry);

        foreach (var entry in snapshot.PhaseLog)
            PhaseLog.Add(entry);

        // Rebuild the state machine from the persisted plan so the dashboard
        // can correctly show completed / active / pending phases.
        if (Plan is not null)
        {
            // A defensive COPY of the restored plan: the capture's stable-input covenant is
            // satisfied without ever holding a lock across the machine call.
            _installedPhases = [.. Plan.Phases];

            // THE PAIR-MATCH RULE: the persisted occurrence is trustworthy ONLY when the snapshot's
            // pipeline phase matches the machine-captured phase from the same save. NULL (no installed
            // plan at save time — the honest-Planning window, old rows) or a MISMATCH (torn/corrupt)
            // → the LEGACY RestoreFromPlan call — byte-identical behavior for all existing data.
            if (snapshot.MachinePhase is { } mp && mp == snapshot.Phase)
                StateMachine.RestoreFromPlanAtOccurrence(_installedPhases, Phase, Math.Max(1, snapshot.PhaseOccurrence));
            else
                StateMachine.RestoreFromPlan(_installedPhases, Phase);
        }
        else
        {
            // THE NO-PLAN SYNC. Without a plan there is nothing to install, so the capture has
            // no phase list — but the machine must still AGREE with the restored phase instead
            // of silently sitting at its Planning default. Restoring from an EMPTY plan leaves
            // the queue empty and drives StateMachine.Phase to snapshot.Phase, so a capture
            // classifies honestly (InvalidPhase for a non-worker phase, PlanUnavailable for a
            // worker phase) instead of misreporting the position.
            _installedPhases = null;
            StateMachine.RestoreFromPlan([], Phase);
        }
    }

    /// <summary>Advance to the next phase.</summary>
    public void AdvanceTo(GoalPhase phase)
    {
        lock (_lock)
        {
            // TERMINAL-ONLY ABANDONMENT. Reaching Done or Failed retires every slot whose work
            // was never claimed; the abandonment happens BEFORE the phase assignment so no
            // observer can see a terminal pipeline that still carries pending slots. Every other
            // AdvanceTo leaves the registry untouched.
            if (phase is GoalPhase.Done or GoalPhase.Failed)
                AbandonPendingSlots();

            Phase = phase;
            if (phase is GoalPhase.Done or GoalPhase.Failed)
                CompletedAt = DateTime.UtcNow;
        }
    }

    /// <summary>Record that a task was dispatched for this pipeline.</summary>
    public void SetActiveTask(string taskId, string? branch = null)
    {
        lock (_lock)
        {
            ActiveTaskId = taskId;
            // Only set CoderBranch on first assignment (the coder's branch).
            // Subsequent phases reuse the same branch for merging.
            if (branch is not null && CoderBranch is null)
                CoderBranch = branch;
        }
    }

    /// <summary>Clear the active task after completion.</summary>
    public void ClearActiveTask()
    {
        lock (_lock)
        {
            ActiveTaskId = null;
        }
    }

    /// <summary>
    /// Clears the active-task pointer ONLY when it currently names <paramref name="taskId"/>.
    /// </summary>
    /// <remarks>
    /// The OWNERSHIP-CHECKED counterpart of <see cref="ClearActiveTask"/>. A caller that finished
    /// with task <c>T</c> must not blank out a pointer that has since moved on to another task —
    /// clearing a pointer it does not own would make a live dispatch look idle. Exactly four cases:
    /// <list type="bullet">
    ///   <item><description>The pointer names <paramref name="taskId"/> → cleared, returns <c>true</c>.</description></item>
    ///   <item><description>The pointer names a DIFFERENT task → untouched, returns <c>false</c>.</description></item>
    ///   <item><description>The pointer is already <c>null</c> → untouched, returns <c>false</c>.</description></item>
    ///   <item><description><paramref name="taskId"/> is <c>null</c>/blank → untouched, returns <c>false</c>.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="taskId">The task id the caller believes is active.</param>
    /// <returns><c>true</c> only when the pointer matched and was cleared.</returns>
    internal bool ClearActiveTaskIfCurrent(string taskId)
    {
        // The blank refusal precedes the lock: a blank argument can never clear a live pointer.
        if (string.IsNullOrWhiteSpace(taskId))
            return false;

        lock (_lock)
        {
            if (ActiveTaskId is null || !string.Equals(ActiveTaskId, taskId, StringComparison.Ordinal))
                return false;

            ActiveTaskId = null;
            return true;
        }
    }

    /// <summary>Extend the iteration budget by the given number of additional iterations.</summary>
    public void ExtendIterations(int additional)
    {
        IterationBudget.TopUp(additional);
    }

    /// <summary>Clear the completed timestamp (used when resuming a failed goal).</summary>
    public void ClearCompletedAt()
    {
        lock (_lock)
        {
            CompletedAt = null;
        }
    }

    /// <summary>Set the iteration plan from the Brain.</summary>
    /// <param name="plan">The plan to install. Must not be <c>null</c>.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/> is <c>null</c>. Thrown BEFORE any mutation: no slot is abandoned,
    /// the installed phases are untouched, and <see cref="Plan"/> keeps its previous value.
    /// </exception>
    public void SetPlan(IterationPlan plan)
    {
        // THE NULL CONTRACT: the refusal precedes the lock, so a null plan cannot abandon a
        // single pending slot nor replace the installed phase list.
        ArgumentNullException.ThrowIfNull(plan);

        lock (_lock)
        {
            // (a) A new plan supersedes every unclaimed dispatch; claimed work keeps its slot.
            AbandonPendingSlots();
            // (b) A defensive COPY: later mutation of the caller's list cannot reach the capture.
            _installedPhases = [.. plan.Phases];
            // (c) Only then does the plan become visible.
            Plan = plan;
        }
    }

    /// <summary>Clear the iteration plan (e.g., after a failure loop-back to Coding).</summary>
    public void ClearPlan()
    {
        lock (_lock)
        {
            Plan = null;
            _installedPhases = null;
            AbandonPendingSlots();
        }
    }

    /// <summary>Records a worker progress report into the pipeline's per-pipeline log.</summary>
    public void AddProgressReport(string workerId, string status, string details)
    {
        ProgressReports.Add(new ProgressEntry
        {
            Timestamp = DateTime.UtcNow,
            WorkerId = workerId,
            GoalId = GoalId,
            Phase = Phase.ToString(),
            Iteration = Iteration,
            Status = status,
            Details = details,
            Occurrence = CurrentPhaseEntry?.Occurrence ?? 1,
        });
    }

    /// <summary>Records a worker narrative report into the pipeline's log.</summary>
    public void AddNarrativeEntry(string workerId, string taskId, string content)
    {
        Narratives.Add(new NarrativeEntry
        {
            Timestamp = DateTime.UtcNow,
            WorkerId = workerId,
            TaskId = taskId,
            Content = content,
        });
    }

    /// <summary>Returns the persisted session JSON for the given role, or <c>null</c> if not found.</summary>
    public string? GetRoleSession(string roleName) =>
        RoleSessions.Get(roleName);

    /// <summary>Stores the session JSON for the given role (case-insensitive key).</summary>
    public void SetRoleSession(string roleName, string sessionJson) =>
        RoleSessions.Set(roleName, sessionJson);

    /// <summary>Build a context summary for the Brain about this pipeline's current state.</summary>
    public string BuildContextSummary() => ConversationTracker.BuildContextSummary(this);

    #region Work-slot registry

    // ══════════════════════════════════════════════════════════════════════════════════
    //  LOCK ORDER (the prohibition, stated once for the whole region).
    //
    //  Two monitors exist on this path: the pipeline's own `_lock` (registry + plan state)
    //  and PipelineStateMachine's private `_machineLock` (taken internally by every machine
    //  entry point, including CapturePosition).
    //
    //  The reverse nested acquisition — holding `_lock` while acquiring `_machineLock` —
    //  is PROHIBITED. CaptureDispatchPosition therefore calls
    //  StateMachine.CapturePosition with NO pipeline lock held, and every registry touch
    //  around it is a SEPARATE short-lived `_lock` acquisition. Nesting is never required,
    //  so the two monitors can never be taken in opposing orders and no lock-order cycle
    //  can exist.
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// TEST-ONLY: a fresh copy of the phase sequence installed for the current iteration,
    /// or <c>null</c> when nothing is installed.
    /// </summary>
    internal IReadOnlyList<GoalPhase>? InstalledPhasesForTest => _installedPhases is null ? null : [.. _installedPhases];

    /// <summary>
    /// Captures the machine's position — the coherent (phase, occurrence) pair — as an
    /// <see cref="MachinePositionSnapshot"/>, for persistence and restore decisions.
    /// <para>
    /// THE SAME SNAPSHOT PATTERN AS <see cref="CaptureDispatchPosition"/>'s step (1): the
    /// <c>_installedPhases</c> reference is read under <c>_lock</c>, the lock is RELEASED, and
    /// only then does <see cref="PipelineStateMachine.CapturePosition"/> run (which takes the
    /// machine lock internally). NEVER hold the pipeline lock while taking the machine lock —
    /// the reverse lock-nesting prohibition (documented in the work-slot region) applies here
    /// too. <c>_installedPhases</c> is only ever REPLACED, never mutated in place, so the
    /// reference handed to the capture is stable for the whole call.
    /// </para>
    /// </summary>
    /// <returns>
    /// The machine's position pair. When <c>OccurrenceFound == false</c> (no installed plan —
    /// e.g. the honest re-plan window), the persistence layer persists <c>MachinePhase</c> as
    /// NULL — the null contract's source.
    /// </returns>
    internal MachinePositionSnapshot CaptureMachinePosition()
    {
        // (1) Snapshot the installed-plan reference under the pipeline lock, then release it.
        List<GoalPhase>? installed;
        lock (_lock)
        {
            installed = _installedPhases;
        }

        // (2) Capture with NO pipeline lock held — the machine takes its own lock.
        return StateMachine.CapturePosition(installed);
    }

    /// <summary>
    /// Captures the pipeline's dispatch position for <paramref name="role"/> and atomically
    /// allocates the work slot that position's next dispatch will occupy.
    /// <para>
    /// SEVEN STEPS, in this exact order — every refusal is a THROW, and every refusal happens
    /// before any registry mutation:
    /// </para>
    /// <list type="number">
    ///   <item>take the machine's phase/occurrence snapshot (no pipeline lock held);</item>
    ///   <item>CLASSIFY FIRST — a phase outside {Coding, Testing, DocWriting, Review, Improve}
    ///     is <see cref="WorkSlotEvent.InvalidPhase"/>;</item>
    ///   <item>an unfound occurrence is <see cref="WorkSlotEvent.PlanUnavailable"/>, carrying
    ///     occurrence <c>0</c>;</item>
    ///   <item>a pipeline phase disagreeing with the snapshot is
    ///     <see cref="WorkSlotEvent.PhaseDivergence"/>;</item>
    ///   <item>a role differing from <see cref="GoalPhaseExtensions.ToWorkerRole"/>'s mapping —
    ///     including an undefined role — is <see cref="WorkSlotEvent.RoleMismatch"/>;</item>
    ///   <item>a LIVE slot already at the position is
    ///     <see cref="WorkSlotEvent.DoubleAssignment"/>; a DEAD slot (Recorded or Abandoned)
    ///     permits the capture to continue;</item>
    ///   <item>the ATOMIC allocation via
    ///     <see cref="AllocateAttemptAndRegisterSlotWithId"/> — the task ID is built from the
    ///     attempt allocated inside that one lock span, so the ID's attempt, the returned
    ///     <see cref="SlotBuildResult.Attempt"/>, and the committed counter can never diverge.</item>
    /// </list>
    /// <para>
    /// HONEST ATOMICITY. The phase+occurrence PAIR is atomic at the snapshot instant (the machine
    /// computes it under its own lock). The ITERATION is sourced separately: it is read under
    /// <c>_lock</c> when the position is constructed, so the position's iteration and the
    /// allocation happen in ADJACENT short-lived lock acquisitions rather than one span. The
    /// integrity boundary is the in-memory registry: within a process lifetime a position is never
    /// silently double-assigned; the cross-restart case is out of scope here.
    /// </para>
    /// <para>
    /// POINTER INDEPENDENCE: the capture never reads or writes <see cref="ActiveTaskId"/>.
    /// </para>
    /// </summary>
    /// <param name="role">The role the caller intends to dispatch to.</param>
    /// <returns>The freshly built task ID, its position, and its allocated attempt number.</returns>
    /// <exception cref="WorkSlotException">Any of the six integrity refusals above.</exception>
    internal SlotBuildResult CaptureDispatchPosition(WorkerRole role)
    {
        // (1) THE SNAPSHOT. `_installedPhases` is only ever REPLACED, never mutated in place, so
        // the reference read here is stable for the whole machine call — which is made with NO
        // pipeline lock held (see the lock-order prohibition above).
        List<GoalPhase>? installed;
        lock (_lock)
        {
            installed = _installedPhases;
        }

        var snapshot = StateMachine.CapturePosition(installed);

        // The iteration is sourced separately from the phase/occurrence pair — see the honest
        // atomicity note above.
        int iteration;
        GoalPhase pipelinePhase;
        lock (_lock)
        {
            iteration = Iteration;
            pipelinePhase = Phase;
        }

        // (2) CLASSIFICATION FIRST — before the plan-availability flag is consulted at all.
        if (snapshot.Phase is not (GoalPhase.Coding or GoalPhase.Testing or GoalPhase.DocWriting
            or GoalPhase.Review or GoalPhase.Improve))
        {
            throw new WorkSlotException(
                WorkSlotEvent.InvalidPhase,
                new WorkSlotPosition(iteration, snapshot.Phase, snapshot.Occurrence),
                pipelinePhase: null,
                snapshot.Phase);
        }

        // (3) NO PLAN TO DERIVE FROM. Both sources land here: a plan whose executed prefix does
        // not contain the phase, and the no-plan restoration (occurrence 0).
        if (!snapshot.OccurrenceFound)
        {
            throw new WorkSlotException(
                WorkSlotEvent.PlanUnavailable,
                new WorkSlotPosition(iteration, snapshot.Phase, 0),
                pipelinePhase: null,
                snapshot.Phase);
        }

        var position = new WorkSlotPosition(iteration, snapshot.Phase, snapshot.Occurrence);

        // (4) DIVERGENCE between the pipeline's own phase and the machine's.
        if (pipelinePhase != snapshot.Phase)
            throw new WorkSlotException(WorkSlotEvent.PhaseDivergence, position, pipelinePhase, snapshot.Phase);

        // (5) ROLE VALIDATION against the single existing mapping. An undefined role simply
        // differs from the derived role, so it lands on the same refusal.
        var derivedRole = snapshot.Phase.ToWorkerRole();
        if (role != derivedRole)
            throw new WorkSlotException(WorkSlotEvent.RoleMismatch, position, role, derivedRole);

        // (6) THE LIVE-POSITION CHECK — a SHORT-LIVED registry read. A DEAD slot (Recorded or
        // Abandoned) does not occupy the position, so the capture continues.
        var occupant = FindLiveSlotTaskIdAt(position);
        if (occupant is not null)
            throw new WorkSlotException(WorkSlotEvent.DoubleAssignment, position, occupant);

        // (7) THE ATOMIC ALLOCATION: counter, task ID, and registration in ONE lock span.
        return AllocateAttemptAndRegisterSlotWithId(GoalId, role, position);
    }

    /// <summary>
    /// Returns the task ID of the LIVE (Pending or Claimed) slot occupying
    /// <paramref name="position"/>, or <c>null</c> when the position is free. Dead slots
    /// (Recorded, Abandoned) never occupy a position.
    /// </summary>
    private string? FindLiveSlotTaskIdAt(WorkSlotPosition position)
    {
        lock (_lock)
        {
            foreach (var (_, entry) in _slots)
            {
                if (entry.State is not (WorkSlotState.Pending or WorkSlotState.Claimed))
                    continue;
                if (entry.Slot.Position != position)
                    continue;
                return entry.Slot.TaskId;
            }

            return null;
        }
    }

    /// <summary>
    /// Allocates the next dispatch attempt for <paramref name="position"/>, BUILDS the task ID
    /// from that freshly allocated attempt, and registers the resulting
    /// <see cref="WorkSlotState.Pending"/> slot — all inside ONE <c>_lock</c> acquisition.
    /// <para>
    /// THE PREDICTION RACE IS ELIMINATED because nothing outside the lock ever guesses the
    /// attempt: the attempt embedded in the task ID, the returned
    /// <see cref="SlotBuildResult.Attempt"/>, and the committed counter are the SAME value, born
    /// in the same lock span. The ID format is
    /// <c>{goalId}-{roleName}-{iteration:D3}-{occurrence:D2}-{attempt:D3}</c> (e.g.
    /// <c>add-auth-coder-002-01-001</c>) with the goal ID used verbatim and the role name from
    /// <see cref="WorkerRoleExtensions.ToRoleName"/>.
    /// </para>
    /// <para>
    /// This is an ADDITIVE overload: <see cref="AllocateAttemptAndRegisterSlot(string, WorkSlotPosition)"/>
    /// is untouched and keeps its own contract.
    /// </para>
    /// </summary>
    /// <param name="goalId">The goal ID, embedded verbatim in the task ID; must be non-blank.</param>
    /// <param name="role">The role whose name is embedded in the task ID.</param>
    /// <param name="position">The iteration/phase/occurrence position the slot occupies.</param>
    /// <returns>The built task ID, the position, and the allocated attempt number.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="goalId"/> is null or blank, or a slot is already registered for the task ID
    /// this allocation would build.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="position"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="role"/> has no role name.</exception>
    /// <exception cref="WorkSlotException">
    /// A live (Pending or Claimed) slot already occupies <paramref name="position"/>.
    /// </exception>
    internal SlotBuildResult AllocateAttemptAndRegisterSlotWithId(string goalId, WorkerRole role, WorkSlotPosition position)
    {
        if (string.IsNullOrWhiteSpace(goalId))
            throw new ArgumentException("Goal ID must be a non-blank string.", nameof(goalId));
        ArgumentNullException.ThrowIfNull(position);

        var roleName = role.ToRoleName();

        lock (_lock)
        {
            // The prospective attempt is READ but not yet committed, so every refusal below
            // still leaves the counter exactly where it was.
            var attempt = _dispatchAttempts.TryGetValue(position, out var previous) ? previous + 1 : 1;
            var taskId = $"{goalId}-{roleName}-{position.Iteration:D3}-{position.Occurrence:D2}-{attempt:D3}";

            if (_slots.ContainsKey(taskId))
                throw new ArgumentException($"A work slot is already registered for task '{taskId}'.", nameof(goalId));

            foreach (var (_, entry) in _slots)
            {
                if (entry.State is not (WorkSlotState.Pending or WorkSlotState.Claimed))
                    continue;
                if (entry.Slot.Position != position)
                    continue;
                throw new WorkSlotException(WorkSlotEvent.DoubleAssignment, position, entry.Slot.TaskId);
            }

            _dispatchAttempts[position] = attempt;
            _slots[taskId] = (new WorkSlot(taskId, position, attempt), WorkSlotState.Pending);

            return new SlotBuildResult(taskId, position, attempt);
        }
    }

    /// <summary>
    /// Allocates the next dispatch attempt number for <paramref name="position"/> and registers a
    /// new <see cref="WorkSlotState.Pending"/> slot for <paramref name="taskId"/>.
    /// </summary>
    /// <remarks>
    /// The attempt counter advance and the slot insertion commit together inside a single lock
    /// acquisition, so a concurrent observer never sees an advanced counter without its slot.
    /// Every refusal (validation failure or double assignment) throws <em>before</em> any mutation,
    /// leaving the registry untouched. The broader invariant that the counter only advances for a
    /// capture that ultimately succeeds is <em>caller-enforced</em>: the caller must perform all of
    /// its own fallible validation before invoking this helper.
    /// </remarks>
    /// <param name="taskId">The task ID that will own the slot; must be non-blank and unused.</param>
    /// <param name="position">The iteration/phase/occurrence position the slot occupies.</param>
    /// <returns>The task ID, position, and freshly allocated attempt number.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="taskId"/> is null or blank, or a slot already exists for it in any state.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="position"/> is <c>null</c>.</exception>
    /// <exception cref="WorkSlotException">
    /// A live (Pending or Claimed) slot already occupies <paramref name="position"/>.
    /// </exception>
    internal SlotBuildResult AllocateAttemptAndRegisterSlot(string taskId, WorkSlotPosition position)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(taskId))
                throw new ArgumentException("Task ID must be a non-blank string.", nameof(taskId));
            ArgumentNullException.ThrowIfNull(position);

            if (_slots.ContainsKey(taskId))
                throw new ArgumentException($"A work slot is already registered for task '{taskId}'.", nameof(taskId));

            foreach (var (_, entry) in _slots)
            {
                if (entry.State is not (WorkSlotState.Pending or WorkSlotState.Claimed))
                    continue;
                if (entry.Slot.Position != position)
                    continue;
                throw new WorkSlotException(WorkSlotEvent.DoubleAssignment, position, entry.Slot.TaskId);
            }

            var attempt = _dispatchAttempts.TryGetValue(position, out var previous) ? previous + 1 : 1;
            _dispatchAttempts[position] = attempt;
            _slots[taskId] = (new WorkSlot(taskId, position, attempt), WorkSlotState.Pending);

            return new SlotBuildResult(taskId, position, attempt);
        }
    }

    /// <summary>
    /// Resolves the slot for <paramref name="taskId"/> and reports whether its work may proceed,
    /// claiming a <see cref="WorkSlotState.Pending"/> slot in the process.
    /// </summary>
    /// <param name="taskId">The task ID identifying the slot.</param>
    /// <returns>
    /// <see cref="SlotGuardResult.Proceed"/> for a Pending (now Claimed), Claimed, or Recorded slot;
    /// <see cref="SlotGuardResult.Abandoned"/> for an abandoned slot;
    /// <see cref="SlotGuardResult.Unknown"/> when no slot exists or the task ID is blank.
    /// </returns>
    internal SlotGuardResult ResolveAndCheckSlot(string taskId)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(taskId) || !_slots.TryGetValue(taskId, out var entry))
                return SlotGuardResult.Unknown;

            switch (entry.State)
            {
                case WorkSlotState.Pending:
                    _slots[taskId] = (entry.Slot, WorkSlotState.Claimed);
                    return SlotGuardResult.Proceed;
                case WorkSlotState.Claimed:
                case WorkSlotState.Recorded:
                    return SlotGuardResult.Proceed;
                case WorkSlotState.Abandoned:
                    return SlotGuardResult.Abandoned;
                default:
                    throw new InvalidOperationException($"Unhandled WorkSlotState: {entry.State}");
            }
        }
    }

    /// <summary>
    /// Records the result of a claimed slot, transitioning it to <see cref="WorkSlotState.Recorded"/>.
    /// </summary>
    /// <param name="taskId">The task ID identifying the slot.</param>
    /// <returns>
    /// <see cref="SlotRecordOutcome.Recorded"/> when a Claimed slot was recorded;
    /// <see cref="SlotRecordOutcome.NoOp"/> for every other state, an unknown task, or a blank task ID.
    /// </returns>
    internal SlotRecordOutcome RecordSlot(string taskId)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(taskId) || !_slots.TryGetValue(taskId, out var entry))
                return SlotRecordOutcome.NoOp;

            if (entry.State != WorkSlotState.Claimed)
                return SlotRecordOutcome.NoOp;

            _slots[taskId] = (entry.Slot, WorkSlotState.Recorded);
            return SlotRecordOutcome.Recorded;
        }
    }

    /// <summary>
    /// Abandons a slot that has not been claimed yet (a superseded dispatch).
    /// </summary>
    /// <param name="taskId">The task ID identifying the slot.</param>
    /// <returns><c>true</c> when a Pending slot was abandoned; otherwise <c>false</c>.</returns>
    internal bool AbandonSlot(string taskId)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(taskId) || !_slots.TryGetValue(taskId, out var entry))
                return false;

            if (entry.State != WorkSlotState.Pending)
                return false;

            _slots[taskId] = (entry.Slot, WorkSlotState.Abandoned);
            return true;
        }
    }

    /// <summary>
    /// Fails a claimed slot, abandoning it so its result can never be recorded.
    /// </summary>
    /// <param name="taskId">The task ID identifying the slot.</param>
    /// <returns><c>true</c> when a Claimed slot was abandoned; otherwise <c>false</c>.</returns>
    internal bool FailSlot(string taskId)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(taskId) || !_slots.TryGetValue(taskId, out var entry))
                return false;

            if (entry.State != WorkSlotState.Claimed)
                return false;

            _slots[taskId] = (entry.Slot, WorkSlotState.Abandoned);
            return true;
        }
    }

    /// <summary>
    /// Releases a claimed slot back to <see cref="WorkSlotState.Pending"/> so it can be claimed again.
    /// </summary>
    /// <param name="taskId">The task ID identifying the slot.</param>
    /// <returns><c>true</c> when a Claimed slot was released; otherwise <c>false</c>.</returns>
    internal bool ReleaseSlot(string taskId)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(taskId) || !_slots.TryGetValue(taskId, out var entry))
                return false;

            if (entry.State != WorkSlotState.Claimed)
                return false;

            _slots[taskId] = (entry.Slot, WorkSlotState.Pending);
            return true;
        }
    }

    /// <summary>
    /// Abandons every <see cref="WorkSlotState.Pending"/> slot. Claimed slots are exempt —
    /// work already in flight keeps its slot.
    /// </summary>
    internal void AbandonPendingSlots()
    {
        lock (_lock)
        {
            foreach (var taskId in _slots.Keys.ToList())
            {
                var entry = _slots[taskId];
                if (entry.State == WorkSlotState.Pending)
                    _slots[taskId] = (entry.Slot, WorkSlotState.Abandoned);
            }
        }
    }

    /// <summary>
    /// TEST-ONLY: returns a fresh immutable view of every registered slot. The order is unspecified.
    /// </summary>
    /// <returns>One <see cref="WorkSlotView"/> per registered slot.</returns>
    internal IReadOnlyList<WorkSlotView> GetSlotsForTest()
    {
        lock (_lock)
        {
            var views = new List<WorkSlotView>(_slots.Count);
            foreach (var (_, entry) in _slots)
                views.Add(new WorkSlotView(entry.Slot, entry.State));
            return views;
        }
    }

    /// <summary>
    /// TEST-ONLY: forces an existing slot into <paramref name="state"/>, bypassing the transition rules.
    /// </summary>
    /// <param name="taskId">The task ID identifying the slot.</param>
    /// <param name="state">The state to force.</param>
    /// <returns><c>true</c> when the slot existed and was forced; <c>false</c> when blank or absent.</returns>
    /// <exception cref="ArgumentException"><paramref name="state"/> is not a defined enum value.</exception>
    internal bool ForceSlotStateForTest(string taskId, WorkSlotState state)
    {
        if (!Enum.IsDefined(state))
            throw new ArgumentException($"Undefined WorkSlotState: {state}", nameof(state));

        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(taskId) || !_slots.TryGetValue(taskId, out var entry))
                return false;

            _slots[taskId] = (entry.Slot, state);
            return true;
        }
    }

    /// <summary>
    /// TEST-ONLY: clears every registered slot and resets all per-position attempt counters
    /// in a single lock acquisition. Always safe to call.
    /// </summary>
    internal void ClearRegistryForTest()
    {
        lock (_lock)
        {
            _slots.Clear();
            _dispatchAttempts.Clear();
        }
    }

    /// <summary>
    /// TEST-ONLY: registers a slot with the exact values supplied, without touching the
    /// per-position attempt counters.
    /// </summary>
    /// <param name="taskId">The task ID that owns the slot.</param>
    /// <param name="position">The position the slot occupies.</param>
    /// <param name="attempt">The attempt number to stamp on the slot; must not be negative.</param>
    /// <param name="state">The state to register the slot in.</param>
    /// <returns>
    /// <c>true</c> when the slot was registered; <c>false</c> when the task ID is blank,
    /// the position is <c>null</c>, or a slot already exists for the task ID.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="state"/> is not a defined enum value, or <paramref name="attempt"/> is negative.
    /// </exception>
    internal bool SeedSlotForTest(string taskId, WorkSlotPosition position, int attempt, WorkSlotState state)
    {
        if (!Enum.IsDefined(state))
            throw new ArgumentException($"Undefined WorkSlotState: {state}", nameof(state));
        if (attempt < 0)
            throw new ArgumentException("Attempt must not be negative.", nameof(attempt));

        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(taskId) || position is null)
                return false;
            if (_slots.ContainsKey(taskId))
                return false;

            _slots[taskId] = (new WorkSlot(taskId, position, attempt), state);
            return true;
        }
    }

    #endregion

    /// <summary>Returns a human-friendly display name for the given <see cref="GoalPhase"/>.</summary>
    /// <param name="phase">The pipeline phase to get a display name for.</param>
    /// <returns>A human-readable string representation of the phase.</returns>
    public static string GetDisplayName(GoalPhase phase) => phase switch
    {
        GoalPhase.Planning   => "Planning",
        GoalPhase.Coding     => "Coding",
        GoalPhase.Testing    => "Testing",
        GoalPhase.DocWriting => "Doc Writing",
        GoalPhase.Review     => "Review",
        GoalPhase.Improve    => "Improvement",
        GoalPhase.Merging    => "Merging",
        GoalPhase.Done       => "Done",
        GoalPhase.Failed     => "Failed",
        _                    => throw new InvalidOperationException($"Unhandled GoalPhase: {phase}")
    };
}

/// <summary>A single worker narrative report.</summary>
public sealed class NarrativeEntry
{
    /// <summary>When the narrative was received.</summary>
    public DateTime Timestamp { get; init; }
    /// <summary>Worker that reported.</summary>
    public string WorkerId { get; init; } = "";
    /// <summary>Task ID associated with this narrative.</summary>
    public string TaskId { get; init; } = "";
    /// <summary>Narrative content from the worker.</summary>
    public string Content { get; init; } = "";
}

/// <summary>
/// Lifecycle state of a work slot inside a <see cref="GoalPipeline"/>'s slot registry.
/// </summary>
internal enum WorkSlotState
{
    /// <summary>The slot has been registered but its work has not been claimed yet.</summary>
    Pending,
    /// <summary>The slot's work has been claimed and is in flight.</summary>
    Claimed,
    /// <summary>The slot's result has been recorded; the slot is complete.</summary>
    Recorded,
    /// <summary>The slot has been abandoned; its result must never be recorded.</summary>
    Abandoned,
}

/// <summary>
/// Integrity violations detected while allocating or capturing a work slot.
/// </summary>
internal enum WorkSlotEvent
{
    /// <summary>A live slot already occupies the requested position.</summary>
    DoubleAssignment,
    /// <summary>The role passed in does not match the role derived from the pipeline state.</summary>
    RoleMismatch,
    /// <summary>The pipeline is not in a phase that permits a slot capture.</summary>
    InvalidPhase,
    /// <summary>The pipeline phase and the state-machine phase disagree.</summary>
    PhaseDivergence,
    /// <summary>No iteration plan is available to derive the slot from.</summary>
    PlanUnavailable,
}

/// <summary>
/// The unique position a work slot occupies: iteration, phase, and the occurrence of that
/// phase within the iteration.
/// </summary>
/// <param name="Iteration">One-based iteration number the slot belongs to.</param>
/// <param name="Phase">The pipeline phase the slot belongs to.</param>
/// <param name="Occurrence">One-based occurrence of that phase within the iteration.</param>
internal sealed record WorkSlotPosition(int Iteration, GoalPhase Phase, int Occurrence);

/// <summary>
/// An immutable work slot: the task that owns it, the position it occupies, and the
/// dispatch attempt number allocated for that position.
/// </summary>
/// <param name="TaskId">The task ID that owns this slot.</param>
/// <param name="Position">The position this slot occupies.</param>
/// <param name="Attempt">One-based dispatch attempt number allocated for the position.</param>
internal sealed record WorkSlot(string TaskId, WorkSlotPosition Position, int Attempt);

/// <summary>
/// Result of resolving a work slot before its work proceeds.
/// </summary>
internal enum SlotGuardResult
{
    /// <summary>The slot is live; the work may proceed.</summary>
    Proceed,
    /// <summary>The slot has been abandoned; the work must be discarded.</summary>
    Abandoned,
    /// <summary>No slot is registered for the task; the caller decides how to handle it.</summary>
    Unknown,
}

/// <summary>
/// Outcome of attempting to record a work slot's result.
/// </summary>
internal enum SlotRecordOutcome
{
    /// <summary>The slot transitioned to <see cref="WorkSlotState.Recorded"/>.</summary>
    Recorded,
    /// <summary>Nothing changed — the slot was not claimed, or does not exist.</summary>
    NoOp,
}

/// <summary>
/// An immutable snapshot pairing a work slot with its state at the time of capture.
/// </summary>
/// <param name="Slot">The slot that was observed.</param>
/// <param name="State">The state the slot was in when observed.</param>
internal sealed record WorkSlotView(WorkSlot Slot, WorkSlotState State);

/// <summary>
/// Result of allocating an attempt and registering a work slot.
/// </summary>
/// <param name="TaskId">The task ID the slot was registered for.</param>
/// <param name="Position">The position the slot occupies.</param>
/// <param name="Attempt">The dispatch attempt number allocated for the position.</param>
internal sealed record SlotBuildResult(string TaskId, WorkSlotPosition Position, int Attempt);

/// <summary>
/// Raised when a work-slot integrity violation is detected. Carries the structured detail
/// relevant to the specific <see cref="WorkSlotEvent"/>; unrelated properties stay <c>null</c>.
/// </summary>
internal sealed class WorkSlotException : Exception
{
    /// <summary>The integrity violation that was detected.</summary>
    public WorkSlotEvent Event { get; }

    /// <summary>The slot position the violation relates to. Never <c>null</c>.</summary>
    public WorkSlotPosition Position { get; }

    /// <summary>
    /// Task ID of the slot already occupying the position, for
    /// <see cref="WorkSlotEvent.DoubleAssignment"/>; otherwise <c>null</c>.
    /// </summary>
    public string? ExistingTaskId { get; }

    /// <summary>
    /// The role passed into the capture, for <see cref="WorkSlotEvent.RoleMismatch"/>;
    /// otherwise <c>null</c>.
    /// </summary>
    public WorkerRole? PassedRole { get; }

    /// <summary>
    /// The role derived from the pipeline state, for <see cref="WorkSlotEvent.RoleMismatch"/>;
    /// otherwise <c>null</c>.
    /// </summary>
    public WorkerRole? DerivedRole { get; }

    /// <summary>
    /// The pipeline's own phase, for <see cref="WorkSlotEvent.PhaseDivergence"/>;
    /// otherwise <c>null</c>.
    /// </summary>
    public GoalPhase? PipelinePhase { get; }

    /// <summary>The state-machine phase, which always equals <see cref="Position"/>'s phase.</summary>
    public GoalPhase? MachinePhase { get; }

    /// <summary>
    /// Creates a <see cref="WorkSlotEvent.DoubleAssignment"/> exception.
    /// </summary>
    /// <param name="ev">Must be <see cref="WorkSlotEvent.DoubleAssignment"/>.</param>
    /// <param name="position">The contested slot position.</param>
    /// <param name="existingTaskId">Task ID of the live slot already at the position.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="ev"/> is not <see cref="WorkSlotEvent.DoubleAssignment"/>, or
    /// <paramref name="existingTaskId"/> is null or blank.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="position"/> is <c>null</c>.</exception>
    internal WorkSlotException(WorkSlotEvent ev, WorkSlotPosition position, string? existingTaskId)
        : base(FormatDoubleAssignment(ev, position, existingTaskId))
    {
        Event = ev;
        Position = position;
        ExistingTaskId = existingTaskId;
        MachinePhase = position.Phase;
    }

    /// <summary>
    /// Creates a <see cref="WorkSlotEvent.RoleMismatch"/> exception.
    /// </summary>
    /// <param name="ev">Must be <see cref="WorkSlotEvent.RoleMismatch"/>.</param>
    /// <param name="position">The slot position the capture targeted.</param>
    /// <param name="passedRole">The role the caller passed in.</param>
    /// <param name="derivedRole">The role derived from the pipeline state.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="ev"/> is not <see cref="WorkSlotEvent.RoleMismatch"/>.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="position"/> is <c>null</c>.</exception>
    internal WorkSlotException(WorkSlotEvent ev, WorkSlotPosition position, WorkerRole passedRole, WorkerRole derivedRole)
        : base(FormatRoleMismatch(ev, position, passedRole, derivedRole))
    {
        Event = ev;
        Position = position;
        PassedRole = passedRole;
        DerivedRole = derivedRole;
        MachinePhase = position.Phase;
    }

    /// <summary>
    /// Creates a phase-related exception:
    /// <see cref="WorkSlotEvent.InvalidPhase"/>, <see cref="WorkSlotEvent.PhaseDivergence"/>,
    /// or <see cref="WorkSlotEvent.PlanUnavailable"/>.
    /// </summary>
    /// <param name="ev">One of InvalidPhase, PhaseDivergence, or PlanUnavailable.</param>
    /// <param name="position">The slot position; its phase must equal <paramref name="machinePhase"/>.</param>
    /// <param name="pipelinePhase">
    /// The pipeline's own phase. Required (non-null) for <see cref="WorkSlotEvent.PhaseDivergence"/>
    /// and must be <c>null</c> for the other two events.
    /// </param>
    /// <param name="machinePhase">The state-machine phase.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="ev"/> is not one of the three permitted events, the position's phase
    /// differs from <paramref name="machinePhase"/>, or <paramref name="pipelinePhase"/>
    /// violates the event's nullability rule.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="position"/> is <c>null</c>.</exception>
    internal WorkSlotException(WorkSlotEvent ev, WorkSlotPosition position, GoalPhase? pipelinePhase, GoalPhase machinePhase)
        : base(FormatPhase(ev, position, pipelinePhase, machinePhase))
    {
        Event = ev;
        Position = position;
        PipelinePhase = pipelinePhase;
        MachinePhase = machinePhase;
    }

    private static string FormatDoubleAssignment(WorkSlotEvent ev, WorkSlotPosition position, string? existingTaskId)
    {
        if (ev != WorkSlotEvent.DoubleAssignment)
            throw new ArgumentException($"Expected {nameof(WorkSlotEvent.DoubleAssignment)} but got '{ev}'.", nameof(ev));
        ArgumentNullException.ThrowIfNull(position);
        if (string.IsNullOrWhiteSpace(existingTaskId))
            throw new ArgumentException("Existing task ID must be a non-blank string.", nameof(existingTaskId));

        return $"{ev}: position {Render(position)} is already occupied by task '{existingTaskId}'.";
    }

    private static string FormatRoleMismatch(WorkSlotEvent ev, WorkSlotPosition position, WorkerRole passedRole, WorkerRole derivedRole)
    {
        if (ev != WorkSlotEvent.RoleMismatch)
            throw new ArgumentException($"Expected {nameof(WorkSlotEvent.RoleMismatch)} but got '{ev}'.", nameof(ev));
        ArgumentNullException.ThrowIfNull(position);

        return $"{ev}: position {Render(position)} passed role '{passedRole}' but derived role '{derivedRole}'.";
    }

    private static string FormatPhase(WorkSlotEvent ev, WorkSlotPosition position, GoalPhase? pipelinePhase, GoalPhase machinePhase)
    {
        ArgumentNullException.ThrowIfNull(position);

        switch (ev)
        {
            case WorkSlotEvent.InvalidPhase:
                if (pipelinePhase is not null)
                    throw new ArgumentException($"{ev} must not carry a pipeline phase.", nameof(pipelinePhase));
                break;
            case WorkSlotEvent.PhaseDivergence:
                if (pipelinePhase is null)
                    throw new ArgumentException($"{ev} requires a pipeline phase.", nameof(pipelinePhase));
                break;
            case WorkSlotEvent.PlanUnavailable:
                if (pipelinePhase is not null)
                    throw new ArgumentException($"{ev} must not carry a pipeline phase.", nameof(pipelinePhase));
                break;
            default:
                throw new ArgumentException(
                    $"Event '{ev}' is not a phase-related work-slot event.", nameof(ev));
        }

        if (position.Phase != machinePhase)
            throw new ArgumentException(
                $"Position phase '{position.Phase}' must equal machine phase '{machinePhase}'.", nameof(machinePhase));

        var pipelineText = pipelinePhase is null ? "none" : pipelinePhase.Value.ToString();
        return $"{ev}: position {Render(position)} pipeline phase '{pipelineText}', machine phase '{machinePhase}'.";
    }

    private static string Render(WorkSlotPosition position) =>
        $"(iteration {position.Iteration}, phase {position.Phase}, occurrence {position.Occurrence})";
}

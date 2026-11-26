using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Claims;

using CopilotHive.Actors;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;
using CopilotHive.Workers;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// INDEPENDENT ACCEPTANCE COVERAGE for the three authorized operational display projections.
/// Every row drives the REAL production facade over the REAL production resolver/policy/directory
/// (<see cref="FacadeAuthorizationHarness"/>), the REAL <see cref="DashboardStateService"/>, and the
/// REAL <see cref="ProgressReportService"/> aggregation. Assertions pin VALUES and OBSERVABLE
/// side effects, not merely success flags or method names.
/// </summary>
public sealed class OperationalFacadeDisplayContractTests : IDisposable
{
    private readonly FacadeAuthorizationHarness _auth = new();

    private const string ViewerId = "contract-viewer";
    private const string MemberId = "contract-member";
    private const string OperatorId = "contract-operator";
    private const string AdminId = "contract-admin";
    private const string UnknownId = "contract-unknown";
    private const string WorkerId = "contract-worker";

    private static readonly DateTime BaseTime = new(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal User(string gitHubId) => FacadeAuthorizationHarness.Principal(gitHubId);

    private static ClaimsPrincipal? OpenUser => OpenModeActingUser.User;

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A goal store whose <see cref="GetAllGoalsAsync"/> throws a NAMED tripwire. Used to prove that
    /// a denied caller does not reach the dashboard's store, that an authorized caller DOES reach
    /// it, and that the worker/progress reads never touch it.
    /// </summary>
    private sealed class ThrowingGoalStore : IGoalStore
    {
        internal const string Tripwire = "CONTRACT_GOAL_STORE_TRIPWIRE";
        internal int ReadCount { get; private set; }

        public string Name => "contract-throwing-store";

        public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default)
        {
            ReadCount++;
            throw new InvalidOperationException(Tripwire);
        }

        public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<IReadOnlyList<Goal>> SearchGoalsAsync(
            string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(
            string goalId, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);

        public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(
            int? limit = null, CancellationToken ct = default) =>
            throw new InvalidOperationException(Tripwire);
    }

    private static DashboardStateService NewDashboard(
        WorkerPool? pool = null,
        GoalPipelineManager? pipelines = null,
        IGoalStore? goalStore = null,
        HiveConfigFile? config = null,
        ProgressLog? progressLog = null,
        Composer? composer = null,
        IDistributedBrain? brain = null) =>
        new(pool ?? new WorkerPool(),
            pipelines ?? new GoalPipelineManager(),
            new DashboardLogSink(),
            progressLog ?? new ProgressLog(),
            brain: brain,
            composer: composer,
            goalStore: goalStore,
            config: config);

    private OperationalFacade FacadeFor(
        HiveAuthMode mode,
        DashboardStateService dashboard,
        ProgressReportService? progressReports = null) =>
        new(dashboard, new LlmSessionRegistry(), _auth.Resolver(mode), _auth.Policy,
            NullLogger<OperationalFacade>.Instance,
            progressReports ?? FacadeAuthorizationHarness.EmptyProgressReports());

    /// <summary>The one VIABLE success control common to all three reads (Viewer, enabled).</summary>
    private async Task SeedViewerAsync() => await _auth.SeedAsync(ViewerId, UserRole.Viewer);

    private async Task SeedAllRolesAsync()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await _auth.SeedAsync(AdminId, UserRole.Admin);
    }

    private static Goal GoalWith(string id, GoalStatus status, DateTime createdAt, int? iterations = null) => new()
    {
        Id = id,
        Description = $"contract description for {id}",
        Status = status,
        CreatedAt = createdAt,
        Iterations = iterations,
    };

    private static void AssertForbidden<T>(FacadeResult<T> result, string scenario)
        where T : class
    {
        Assert.False(result.Success, $"'{scenario}' must be refused.");
        Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
        Assert.Equal(FacadeAuthorization.ForbiddenMessage, result.Error);
        Assert.Null(result.Value);
    }

    private static ProgressEntry Entry(
        string workerId, DateTime timestamp, string goalId, string status, string details) => new()
    {
        Timestamp = timestamp,
        WorkerId = workerId,
        GoalId = goalId,
        Status = status,
        Details = details,
    };

    /// <summary>
    /// Faults a REAL <see cref="GoalPipelineManager"/>'s pipeline dictionary through reflection so
    /// that its <c>GetAllPipelines()</c> throws a NullReferenceException. The dashboard read (which
    /// must read pipelines) and the worker display (which must not) can share this faulted manager.
    /// </summary>
    private static GoalPipelineManager FaultedPipelineManager()
    {
        var manager = new GoalPipelineManager();
        typeof(GoalPipelineManager)
            .GetField("_pipelines", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, null);
        return manager;
    }

    /// <summary>
    /// A Brain whose <c>GetStats()</c> is a NAMED tripwire. <c>DashboardStateService.GetBrainStats()</c>
    /// reads ONLY the Brain; <c>GetOrchestratorInfo()</c> reads the Brain's stats and then the Composer's
    /// (and neither goals nor pipelines). This seam therefore observes Brain-stat reads only: it proves
    /// the worker display never reaches those two Brain paths, and says nothing about a Composer read.
    /// </summary>
    private sealed class TrippingBrain : IDistributedBrain
    {
        internal const string Tripwire = "CONTRACT_BRAIN_STATS_TRIPWIRE";
        internal int StatsReadCount { get; private set; }

        public BrainStats? GetStats()
        {
            StatsReadCount++;
            throw new InvalidOperationException(Tripwire);
        }

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<PlanResult> PlanIterationAsync(
            GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));
        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success("prompt"));
        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult("summary");
        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("answer"));
        public Task UpdateModelAsync(string model, int? maxContextTokens, ReasoningEffort? reasoningEffort, CancellationToken ct) =>
            Task.CompletedTask;
        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;
        public bool GoalSessionExists(string goalId) => false;
    }

    // ── REAL fault fixtures at the ACTUAL production call sites ──────────────

    private const string FixtureModel = "contract/composer-model";
    private static readonly TimeSpan FixtureDiagnosticDeadline = TimeSpan.FromSeconds(30);

    /// <summary>The <see cref="Exception.Data"/> key under which retained cleanup faults ride on the primary failure.</summary>
    private const string ComposerFixtureCleanupFaultsKey = "ComposerFixtureCleanupFaults";

    /// <summary>
    /// <see cref="Exception.Data"/> key on the diagnostic-expiry <see cref="TimeoutException"/> holding the
    /// ACTUAL retained cleanup <see cref="Task"/>. An ordinary caller awaits it after expiry to observe the
    /// real late outcome (success, or the cleanup's own faults); it needs no probe and no shared state.
    /// </summary>
    private const string ComposerFixtureRetainedCleanupKey = "ComposerFixtureRetainedCleanup";

    /// <summary>
    /// Observation and control points of ONE <see cref="WithFaultingComposerStatsAsync"/> run. The default
    /// instance changes nothing; the adversarial regression uses the milestones to observe the REAL
    /// teardown ordering. Every milestone is an event the helper itself raises (never a sleep).
    /// </summary>
    private sealed class ComposerFixtureProbe
    {
        private static TaskCompletionSource NewMilestone() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Clock the OUTER diagnostic wait runs on (the test substitutes a manual clock).</summary>
        internal TimeProvider DiagnosticClock { get; init; } = TimeProvider.System;

        /// <summary>The diagnostic deadline. Expiry reports a failure; it never owns or cancels the cleanup.</summary>
        internal TimeSpan DiagnosticDeadline { get; init; } = FixtureDiagnosticDeadline;

        /// <summary>Setup-phase hook, run after the real connect and BEFORE the model is cleared.</summary>
        internal Func<Composer, Task>? AfterConnected { get; init; }

        internal Composer? Composer { get; set; }
        internal string? StateDir { get; set; }

        /// <summary>The retained cleanup task that owns the directory. Set before any outer wait starts.</summary>
        internal Task? Cleanup { get; set; }

        /// <summary><c>Composer.DisposeAsync</c> has been entered (actor admission closed, first await reached).</summary>
        internal TaskCompletionSource DisposeStarted { get; } = NewMilestone();

        /// <summary><c>Composer.DisposeAsync</c> has RETURNED — which is not proof that teardown is over.</summary>
        internal TaskCompletionSource DisposeReturned { get; } = NewMilestone();

        /// <summary>The join routine has run to its first suspension (or returned without waiting).</summary>
        internal TaskCompletionSource<Task> JoinStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The cleanup is about to delete the directory.</summary>
        internal TaskCompletionSource BeforeDeleteEntered { get; } = NewMilestone();

        internal bool? TeardownCompleteAtDelete { get; set; }
        internal bool? ActorCompletedAtDelete { get; set; }

        /// <summary>The chat client the Composer was built over, so recovery can observe its disposal.</summary>
        internal Mock<IChatClient>? ChatClient { get; set; }
        internal Func<ValueTask>? DisposeChatClientAsync { get; init; }

        /// <summary>
        /// Test-owned chat-client disposal observation: the ACTUAL disposal attempt and its outcome are recorded
        /// separately, so the shared safety decision has positive evidence instead of a hook/reference signal.
        /// </summary>
        internal ObservedClientDisposal? ClientDisposal { get; init; }

        /// <summary>The exception <c>Composer.DisposeAsync</c> itself threw on the DIRECT path (null when it did not throw).</summary>
        internal Exception? DirectDisposeFault { get; set; }

        /// <summary>Whether the observed client disposal had completed successfully when deletion began (null = not observed/not reached).</summary>
        internal bool? ClientDisposalSucceededAtDelete { get; set; }

        /// <summary>
        /// Faulted-teardown recovery only: the retained cleanup has begun joining the REAL <c>Actor.Completion</c>
        /// (the join task is published so a test can observe it pending).
        /// </summary>
        internal TaskCompletionSource<Task> RecoveryActorJoin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Test substitute for the dependent-cleanup step (default: re-run the real <c>Composer.DisposeAsync</c>).</summary>
        internal Func<Composer, Task>? DependentRecovery { get; init; }

        /// <summary>Whether recovery established agent/client/lock quiescence before deletion (null = recovery not run).</summary>
        internal bool? DependentsQuiescentAtDelete { get; set; }

        /// <summary>Why recovery could not establish safety and the directory was RETAINED (null = it did).</summary>
        internal string? RetainedReason { get; set; }
    }

    /// <summary>
    /// A real chat-client disposal the test owns. The ATTEMPT (<see cref="Attempted"/>) and the OUTCOME
    /// (<see cref="Outcome"/>: <c>null</c> = completed successfully, otherwise the exact fault) are separate
    /// observations. Synchronous mode drives <c>IDisposable.Dispose</c> (what <c>Composer</c> calls on a
    /// non-async-disposable client); asynchronous mode can be held until released.
    /// </summary>
    private sealed class ObservedClientDisposal(bool asynchronous, Exception? failWith = null, bool held = false,
        bool suppressFirstTestResourceClose = false)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // The fake client owns this real resource. A throwing disposal leaves it open; a no-op
        // Composer retry on already-cleared references cannot close it.
        private readonly MemoryStream _resource = new();
        private int _suppressNextTestResourceClose = suppressFirstTestResourceClose ? 1 : 0;
        private int _attempts;
        internal bool ResourceClosed => !_resource.CanWrite;

        internal bool Asynchronous { get; } = asynchronous;
        internal int Attempts => Volatile.Read(ref _attempts);
        internal TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Exception?> Outcome { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Dispose()
        {
            Interlocked.Increment(ref _attempts);
            Attempted.TrySetResult();
            Settle();
        }

        internal async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _attempts);
            Attempted.TrySetResult();
            if (held)
                await _release.Task;
            Settle();
        }

        internal void Release() => _release.TrySetResult();

        internal void CloseTestResource()
        {
            if (Interlocked.Exchange(ref _suppressNextTestResourceClose, 0) == 0)
                _resource.Dispose();
        }

        private void Settle()
        {
            if (failWith is not null)
            {
                Outcome.TrySetResult(failWith);
                throw failWith;
            }

            _resource.Dispose();
            Outcome.TrySetResult(null);
        }
    }

    /// <summary>
    /// The chat client a fixture Composer was built over, keyed by the Composer itself so EVERY fallback (with or
    /// without a probe) can ask the same positive question: was the ORIGINAL client's disposal attempted and did it
    /// succeed? A later Composer.DisposeAsync cannot answer it - the direct path clears the client reference before
    /// disposing, so a retry is a no-op that never touches a client that already failed.
    /// </summary>
    private sealed class FixtureClientEvidence
    {
        internal volatile bool Created;

        // Bind test-owned cleanup to the same client whose one real outcome this ledger records.
        internal ObservedClientDisposal? OwnedResource { get; init; }

        /// <summary>
        /// Test-owned behaviour for the ONE real synchronous client disposal (observe state, or throw). It runs INSIDE the
        /// evidence wrapper, so its outcome is the recorded outcome; a fixture without it disposes successfully.
        /// </summary>
        internal Action? SyncDisposeOverride { get; set; }
        internal TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Exception?> Outcome { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Keep the original outcome even if someone invokes the mock again. Only a completed
        // successful disposal call, not an invocation record, can establish client cleanup.
        internal void Dispose(Action? dispose)
        {
            var firstAttempt = Attempted.TrySetResult();
            try
            {
                dispose?.Invoke();
                if (firstAttempt)
                    Outcome.TrySetResult(null);
            }
            catch (Exception ex)
            {
                if (firstAttempt)
                    Outcome.TrySetResult(ex);
                throw;
            }
        }

        internal async ValueTask DisposeAsync(Func<ValueTask> dispose)
        {
            var firstAttempt = Attempted.TrySetResult();
            try
            {
                await dispose();
                if (firstAttempt)
                    Outcome.TrySetResult(null);
            }
            catch (Exception ex)
            {
                if (firstAttempt)
                    Outcome.TrySetResult(ex);
                throw;
            }
        }
    }

    private static readonly ConditionalWeakTable<Composer, FixtureClientEvidence> ClientEvidence = new();

    // Adds a fault unless every leaf of it is ALREADY present (by reference), so the same exception reaching the
    // report through two channels (attached to the scenario primary and thrown by the cleanup task) is listed once.
    private static void AddDistinctFault(List<Exception> faults, Exception fault)
    {
        var known = faults.SelectMany(Flattened).ToHashSet(ReferenceEqualityComparer.Instance);
        if (!Flattened(fault).All(known.Contains))
            faults.Add(fault);
    }

    // The fixture rethrows the EXACT scenario primary and attaches the cleanup faults to it. A caller that expected
    // that primary and swallows it must not also swallow what rode on it, or a failing observer would lose the
    // cleanup outcome. Called only when the observer itself failed (a passing body asserted the attached faults).
    private static void ForwardAttachedCleanupFaults(Exception expectedPrimary, List<Exception> faults)
    {
        if (expectedPrimary.Data[ComposerFixtureCleanupFaultsKey] is not Exception[] attached)
            return;
        foreach (var fault in attached)
            AddDistinctFault(faults, fault);
    }

    /// <summary>What the shared fallback decided, recorded so a test can prove the decision was actually reached.</summary>
    private sealed class FallbackDecisionRecord
    {
        internal volatile bool Reached;
        internal volatile bool Retried;
        internal volatile bool DeletionStarted;
        internal bool? ClientDisposalSucceededAtDelete { get; set; }
        internal string[] Reasons { get; set; } = [];
    }

    // THE SHARED FALLBACK SAFETY RULE: positive evidence only. A no-op retry, a disconnected agent, a cleared
    // field or a disposed issue lock never overrides the original client's recorded outcome.
    private static (List<string> Reasons, Exception? ClientFault) FallbackUnsafety(Composer composer)
    {
        var reasons = new List<string>();
        Exception? clientFault = null;
        if (!ActorOf(composer).Completion.IsCompletedSuccessfully)
            reasons.Add("the actor has not completed successfully");
        var agentService = AgentServiceOf(composer);
        if (agentService.Agent is not null || agentService.IsConnected)
            reasons.Add("the agent service still holds a live agent/connection");
        if (!IssueLockIsDisposed(composer))
            reasons.Add("the issue-update lock is still usable");

        if (!ClientEvidence.TryGetValue(composer, out var evidence))
            reasons.Add("the original chat client's disposal outcome was not tracked");
        else if (evidence.Created)
        {
            if (!evidence.Attempted.Task.IsCompletedSuccessfully)
                reasons.Add("the original chat client's disposal was never attempted");
            else if (!evidence.Outcome.Task.IsCompletedSuccessfully)
                reasons.Add("the original chat client's disposal has not completed");
            else if (evidence.Outcome.Task.Result is { } fault)
            {
                clientFault = fault;
                reasons.Add($"the original chat client's disposal faulted ({fault.GetType().Name}: {fault.Message}) "
                    + "and a later retry cannot have recovered it");
            }
        }

        return (reasons, clientFault);
    }

    // A private, per-Composer sink for faults the real deferred path logs instead of throwing.
    // The retained cleanup still joins TeardownCompletion before reading these exceptions.
    private sealed class ComposerFixtureLogger : ILogger<Composer>
    {
        internal ConcurrentQueue<Exception> DeferredFaults { get; } = new();
        internal TaskCompletionSource DeferredSelected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CleanupSettled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // The ACTUAL retained cleanup task: it carries the owner's exact outcome (success or its faults). CleanupSettled
        // above only reports that cleanup ENDED and always succeeds, so a fallback that joins it can never observe an
        // owner fault. An observer that cannot see the probe joins THIS task. Published (volatile) before the outer
        // diagnostic timer is registered.
        private Task? _retainedCleanup;
        internal Task? RetainedCleanup { get => Volatile.Read(ref _retainedCleanup); set => Volatile.Write(ref _retainedCleanup, value); }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            switch (formatter(state, exception))
            {
                case "Actor disposal timed out — agent disposal deferred":
                    DeferredSelected.TrySetResult();
                    break;
                case "Deferred agent disposal failed" or "Deferred issue-lock disposal failed" when exception is not null:
                    DeferredFaults.Enqueue(exception);
                    break;
            }
        }
    }

    private static ComposerAgentService AgentServiceOf(Composer composer) =>
        (ComposerAgentService)typeof(Composer)
            .GetField("_agentService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(composer)!;

    private static ComposerActor ActorOf(Composer composer) =>
        (ComposerActor)typeof(Composer)
            .GetField("_actor", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(composer)!;

    // GetStats returns null when disconnected. Arm the missing-model guard only after the real
    // connect path supplies an agent and a successful stats result.
    //
    // OWNERSHIP: the state directory is owned by ONE retained cleanup task that (1) disposes the Composer,
    // (2) JOINS the actual TeardownCompletion — DisposeAsync returning is NOT proof, it can return while the
    // actor still runs and defer agent disposal — and only then (3) deletes the directory. The diagnostic
    // deadline is an OUTER wait on that task: expiry fails loudly but never cancels, abandons or short-cuts
    // the cleanup, and never deletes. The temporarily cleared model is restored in a finally BEFORE
    // cleanup starts, and cleanup uses no scenario token. The primary failure is preserved; cleanup faults
    // ride on it in Exception.Data (or are thrown when there is no primary).
    //
    // THE DIAGNOSTIC WAIT IS CLASSIFIED WHEN IT IS DECIDED, not when it is reported: the cleanup task races a
    // diagnostic delay and the winner is fixed at that instant. If the delay wins, the expiry diagnostic carries the
    // SAME retained cleanup task even if that task faults before the diagnostic is published; if cleanup wins and
    // itself throws (even a genuine TimeoutException) that exact fault is reported with no expiry wrapper or handle.
    // `onWaitDecided` is a test-owned checkpoint between the decision and its publication; it receives the decision
    // and the actual retained cleanup task, and the default (null) changes nothing.
    private static async Task WithFaultingComposerStatsAsync(Func<Composer, Task> run, ComposerFixtureProbe? probe = null,
        TimeProvider? diagnosticClock = null, Func<bool, Task, Task>? onWaitDecided = null)
    {
        probe ??= new ComposerFixtureProbe();
        var logger = new ComposerFixtureLogger();
        var stateDir = Directory.CreateTempSubdirectory("contract-composer-").FullName;
        probe.StateDir = stateDir;

        ExceptionDispatchInfo? primary = null;
        var faults = new List<Exception>();
        Composer? composer = null;
        ComposerAgentService? agentService = null;
        var modelField = typeof(ComposerAgentService).GetField("_model", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var modelCleared = false;
        try
        {
            var client = new Mock<IChatClient>();
            var evidence = new FixtureClientEvidence { OwnedResource = probe.ClientDisposal };
            Func<ValueTask>? disposeAsync = probe.DisposeChatClientAsync;
            Action? dispose = null;
            if (probe.ClientDisposal is { } observed)
            {
                if (observed.Asynchronous)
                    disposeAsync = observed.DisposeAsync;
                else
                    dispose = observed.Dispose;
            }
            if (disposeAsync is not null)
                client.As<IAsyncDisposable>().Setup(c => c.DisposeAsync()).Returns(() => evidence.DisposeAsync(disposeAsync));
            else
                client.As<IDisposable>().Setup(c => c.Dispose()).Callback(() => evidence.Dispose(evidence.SyncDisposeOverride ?? dispose));
            composer = new Composer(FixtureModel, logger, new InMemoryGoalStore(),
                stateDir: stateDir, availableModels: [FixtureModel],
                chatClientFactory: _ => { evidence.Created = true; return client.Object; });
            ClientEvidence.Add(composer, evidence);
            probe.Composer = composer;
            probe.ChatClient = client;

            await composer.ConnectAsync(Ct);
            agentService = AgentServiceOf(composer);
            Assert.True(composer.IsConnected);
            Assert.NotNull(agentService.Agent);
            Assert.Equal(FixtureModel, agentService.Model);
            Assert.Equal(FixtureModel, Assert.IsType<BrainStats>(composer.GetStats()).Model);

            if (probe.AfterConnected is { } afterConnected)
                await afterConnected(composer);

            modelField.SetValue(agentService, null);
            modelCleared = true;
            AssertComposerStatsFault(Assert.Throws<InvalidOperationException>(() => composer.GetStats()));
            await run(composer);
        }
        catch (Exception ex)
        {
            primary = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            // Once cleared, restore before cleanup starts, even when the scenario failed.
            if (modelCleared)
            {
                try { modelField.SetValue(agentService!, FixtureModel); }
                catch (Exception ex) { faults.Add(ex); }
            }
        }

        var cleanup = CleanupAndSignalSettlementAsync(composer, stateDir, probe, logger);
        probe.Cleanup = cleanup;
        logger.RetainedCleanup = cleanup;
        // A deadline expiry abandons only the WAIT. The task keeps its outcome (including a later fault) and is
        // handed to the caller on the expiry exception below; this continuation only marks the exception observed
        // so an un-awaited late fault is not reported as an unobserved task exception.
        _ = cleanup.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        // Race the retained cleanup against the diagnostic delay. The winner is fixed here; the delay is cancelled
        // (releasing its timer) if cleanup wins. Neither outcome cancels the cleanup or authorizes deletion.
        using var delayCts = new CancellationTokenSource();
        var delay = Task.Delay(probe.DiagnosticDeadline, diagnosticClock ?? probe.DiagnosticClock, delayCts.Token);
        var winner = await Task.WhenAny(cleanup, delay);
        var expiryWon = !ReferenceEquals(winner, cleanup);
        if (!expiryWon)
            delayCts.Cancel();

        if (onWaitDecided is not null)
            await onWaitDecided(expiryWon, cleanup);

        if (expiryWon)
        {
            var expiry = new TimeoutException(
                $"Composer fixture teardown had not completed when the {probe.DiagnosticDeadline} diagnostic deadline expired. "
                + "Expiry is only a diagnostic: it did not cancel, abandon or short-cut the cleanup, and deletes nothing itself. "
                + "To observe the actual cleanup outcome, await the retained cleanup task in this exception's Data.",
                new TimeoutException("The diagnostic deadline elapsed before the retained cleanup completed."));
            expiry.Data[ComposerFixtureRetainedCleanupKey] = cleanup;
            faults.Add(expiry);
        }
        else
        {
            try { await cleanup; }
            catch (Exception cleanupFault) { faults.Add(cleanupFault); }
        }

        if (primary is not null)
        {
            if (faults.Count > 0)
                primary.SourceException.Data[ComposerFixtureCleanupFaultsKey] = faults.ToArray();
            primary.Throw();
        }

        if (faults.Count == 1)
            ExceptionDispatchInfo.Capture(faults[0]).Throw();
        if (faults.Count > 1)
            throw new AggregateException(faults);
    }

    // Test emergency cleanup can observe settlement even if an assertion or mutant prevented the
    // caller from recovering the outcome. This signal is not used for caller-observability assertions.
    private static async Task CleanupAndSignalSettlementAsync(Composer? composer, string stateDir,
        ComposerFixtureProbe probe, ComposerFixtureLogger logger)
    {
        try { await TeardownThenDeleteAsync(composer, stateDir, probe, logger); }
        finally { logger.CleanupSettled.TrySetResult(); }
    }

    // The retained cleanup: dispose, JOIN actual teardown, then delete. No deadline and no scenario token.
    // A faulted DisposeAsync/TeardownCompletion is NOT quiescence: Composer skips agent disposal on that early
    // path and its teardown task stays faulted forever, so the cleanup keeps ownership and RECOVERS (join the
    // real actor, finish dependent disposal, prove it) before any deletion. If safety cannot be established the
    // directory is retained and the reason is reported.
    private static async Task TeardownThenDeleteAsync(Composer? composer, string stateDir, ComposerFixtureProbe probe,
        ComposerFixtureLogger logger)
    {
        var faults = new List<Exception>();
        var safeToDelete = true;
        if (composer is not null)
        {
            try
            {
                var dispose = composer.DisposeAsync(); // runs synchronously to its first await
                probe.DisposeStarted.TrySetResult();
                await dispose;
            }
            catch (Exception ex)
            {
                probe.DirectDisposeFault = ex;
                faults.Add(ex);
            }

            probe.DisposeReturned.TrySetResult();
            var join = JoinActualTeardownAsync(composer, faults);
            probe.JoinStarted.TrySetResult(join);
            await join;

            if (!composer.TeardownCompletion.IsCompletedSuccessfully)
            {
                var unsafeReason = await RecoverFromFaultedTeardownAsync(composer, probe, faults);
                if (unsafeReason is not null)
                {
                    safeToDelete = false;
                    probe.RetainedReason = unsafeReason;
                    faults.Add(RetentionDiagnostic(ReportSource.Owner,
                        $"Composer teardown faulted and recovery could not establish quiescence ({unsafeReason}); "
                        + $"the state directory '{stateDir}' was RETAINED, not deleted."));
                }
            }

            // A successful TeardownCompletion says the deferred attempt settled, not that agent
            // or lock disposal succeeded. Composer logs the real exception before signaling it.
            var known = faults.SelectMany(Flattened).ToHashSet(ReferenceEqualityComparer.Instance);
            var unreported = logger.DeferredFaults.Where(fault => !known.Contains(fault)).ToArray();

            if (unreported.Length > 0)
            {
                faults.AddRange(unreported);
                if (safeToDelete)
                {
                    safeToDelete = false;
                    probe.RetainedReason = "dependent disposal did not complete successfully";
                    faults.Add(RetentionDiagnostic(ReportSource.Owner,
                        $"Composer agent disposal faulted ({probe.RetainedReason}); "
                        + $"the state directory '{stateDir}' was RETAINED, not deleted."));
                }
            }

            // THE SHARED SAFETY DECISION for evidence the paths above do not already settle. A successful
            // TeardownCompletion, cleared references or an entered hook are NOT proof that a dependent was disposed:
            // on the DIRECT path Composer clears its fields BEFORE disposing, completes TeardownCompletion in a
            // finally, and rethrows a failed client disposal - so a thrown DisposeAsync with a successful teardown
            // is a failed dependent that nothing can show has recovered (a retry on cleared fields would be a no-op).
            // Every reason lands in ONE list and ONE place decides, so separate checks cannot disagree.
            if (safeToDelete)
            {
                var recovered = !composer.TeardownCompletion.IsCompletedSuccessfully;
                var reasons = new List<string>();
                if (probe.DirectDisposeFault is { } directFault && !recovered)
                {
                    var root = Flattened(directFault).First();
                    reasons.Add($"direct dependent disposal faulted ({root.GetType().Name}: {root.Message}) and nothing shows it recovered");
                }

                if (!recovered && !composer.TeardownCompletion.IsCompletedSuccessfully)
                    reasons.Add("the composer teardown has not completed successfully");

                // THE SAME POSITIVE-EVIDENCE RULE THE FALLBACK USES (actor, agent/connection, issue lock and the ORIGINAL
                // client's recorded disposal outcome). It consults the per-Composer evidence, never the optional
                // ClientDisposal probe, so a normally returning recovery, a reconnect or a no-op retry cannot stand in for
                // a disposal that actually completed successfully. The exact underlying fault is kept in the report.
                var (shared, originalClientFault) = FallbackUnsafety(composer);
                foreach (var reason in shared)
                {
                    if (!reasons.Contains(reason))
                        reasons.Add(reason);
                }

                if (originalClientFault is not null)
                    AddDistinctFault(faults, originalClientFault);

                if (reasons.Count > 0)
                {
                    safeToDelete = false;
                    probe.DependentsQuiescentAtDelete = false;
                    probe.RetainedReason = string.Join("; ", reasons);
                    faults.Add(RetentionDiagnostic(ReportSource.Owner,
                        $"Composer disposal could not be shown quiescent ({probe.RetainedReason}); "
                        + $"the state directory '{stateDir}' was RETAINED, not deleted."));
                }
            }

            probe.TeardownCompleteAtDelete = composer.TeardownCompletion.IsCompletedSuccessfully;
            probe.ActorCompletedAtDelete = ActorOf(composer).Completion.IsCompleted;
            probe.ClientDisposalSucceededAtDelete = ClientEvidence.TryGetValue(composer, out var original) && original.Created
                ? original.Outcome.Task is { IsCompletedSuccessfully: true, Result: null }
                : null;
        }

        if (safeToDelete)
        {
            probe.BeforeDeleteEntered.TrySetResult();
            try
            {
                await CopilotHive.Worker.GitOperations.ForceDeleteDirectoryAsync(stateDir);
            }
            catch (Exception ex)
            {
                faults.Add(ex);
            }
        }

        if (faults.Count == 1)
            ExceptionDispatchInfo.Capture(faults[0]).Throw();
        if (faults.Count > 1)
            throw new AggregateException(faults);
    }

    // DisposeAsync can return while the actor still runs (deferred agent disposal); only TeardownCompletion is proof.
    private static async Task JoinActualTeardownAsync(Composer composer, List<Exception> faults)
    {
        try { await composer.TeardownCompletion; }
        catch (Exception ex)
        {
            if (!faults.Contains(ex))
                faults.Add(ex);
        }
    }

    // Recovery after a faulted teardown. Re-awaiting the original (permanently faulted) task or waiting for the
    // actor alone proves nothing about the agent, so: (1) join the REAL Actor.Completion, (2) finish the dependent
    // disposal Composer skipped (re-running DisposeAsync, which is safe once the actor has completed), then (3)
    // PROVE the agent, clients and issue lock are cleaned. Returns null when safe, else the reason it is not.
    private static async Task<string?> RecoverFromFaultedTeardownAsync(
        Composer composer, ComposerFixtureProbe probe, List<Exception> faults)
    {
        var actor = ActorOf(composer);
        var agentService = AgentServiceOf(composer);
        var liveAgent = agentService.Agent;

        var actorJoin = JoinActorCompletionAsync(actor, faults);
        probe.RecoveryActorJoin.TrySetResult(actorJoin);
        await actorJoin;
        if (!actor.Completion.IsCompletedSuccessfully)
            return "the actor loop did not complete";

        var observedAgent = false;
        var priorHook = agentService.OnAgentDisposing;
        agentService.OnAgentDisposing = agent =>
        {
            observedAgent |= ReferenceEquals(agent, liveAgent);
            priorHook?.Invoke(agent);
        };

        var reasons = new List<string>();
        try
        {
            // The retry on an already-joined actor awaits agent AND client disposal and closes
            // the issue lock. The agent hook, cleared fields and a client invocation alone do not
            // establish quiescence: the retry may still be pending or may throw after all three.
            await (probe.DependentRecovery is { } recover ? recover(composer) : composer.DisposeAsync().AsTask());
        }
        catch (Exception ex)
        {
            faults.Add(ex);
            reasons.Add("dependent disposal did not complete successfully");
        }
        finally
        {
            agentService.OnAgentDisposing = priorHook;
        }

        if (liveAgent is not null && !observedAgent)
            reasons.Add("the connected agent was not disposed");
        if (agentService.Agent is not null || agentService.IsConnected)
            reasons.Add("the agent service still holds a live agent/connection");
        if (liveAgent is not null && probe.ChatClient is { } client
            && !client.Invocations.Any(call => call.Method.Name is nameof(IDisposable.Dispose) or nameof(IAsyncDisposable.DisposeAsync)))
            reasons.Add("the chat client was not disposed");
        if (!IssueLockIsDisposed(composer))
            reasons.Add("the issue-update lock is still usable");

        probe.DependentsQuiescentAtDelete = reasons.Count == 0;
        return reasons.Count == 0 ? null : string.Join("; ", reasons);
    }

    private static async Task JoinActorCompletionAsync(ComposerActor actor, List<Exception> faults)
    {
        try { await actor.Completion; }
        catch (Exception ex) { faults.Add(ex); }
    }

    private static bool IssueLockIsDisposed(Composer composer)
    {
        var gate = (SemaphoreSlim)typeof(Composer)
            .GetField("_issueUpdateLock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(composer)!;
        try
        {
            if (!gate.Wait(0))
                return false;
            gate.Release();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>Parks the real actor loop at its before-read seam until the test releases it (not cancellation-aware, by design).</summary>
    private sealed class ActorLoopHold
    {
        private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Parked => _parked.Task;

        internal Task OnBeforeReadAsync()
        {
            _parked.TrySetResult();
            return _release.Task;
        }

        internal void Release() => _release.TrySetResult();
    }

    private static void AssertTeardownStillHeld(ComposerFixtureProbe probe, string when)
    {
        var composer = probe.Composer!;
        Assert.False(composer.TeardownCompletion.IsCompleted, $"TeardownCompletion must be incomplete {when}.");
        Assert.False(ActorOf(composer).Completion.IsCompleted, $"the held actor must still be running {when}.");
        Assert.False(probe.Cleanup!.IsCompleted, $"the retained cleanup must still be running {when}.");
        Assert.False(probe.BeforeDeleteEntered.Task.IsCompleted, $"cleanup must not have reached deletion {when}.");
        Assert.True(Directory.Exists(probe.StateDir), $"the state directory must be intact {when}.");
        Assert.Equal("live state", File.ReadAllText(Path.Combine(probe.StateDir!, "sentinel.txt")));
    }

    private static ComposerFixtureProbe NewHeldComposerProbe(ActorLoopHold hold, TimeProvider actorClock,
        TimeProvider diagnosticClock, Action<Composer> onParked, Func<Composer, Task>? dependentRecovery = null,
        Func<ValueTask>? disposeChatClientAsync = null, ObservedClientDisposal? observedClient = null)
    {
        ComposerFixtureProbe? probe = null;
        probe = new ComposerFixtureProbe
        {
            DiagnosticClock = diagnosticClock,
            DependentRecovery = dependentRecovery,
            DisposeChatClientAsync = disposeChatClientAsync,
            ClientDisposal = observedClient,
            AfterConnected = async composer =>
            {
                File.WriteAllText(Path.Combine(probe!.StateDir!, "sentinel.txt"), "live state");
                var actor = ActorOf(composer);
                typeof(Actor<IComposerMessage>)
                    .GetField("_timeProvider", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(actor, actorClock);
                actor.OnBeforeReadAsync = hold.OnBeforeReadAsync;
                // Wake a loop that may already be awaiting its next mailbox item. The awaited seam,
                // not Tell returning or ConnectAsync finishing, is the rendezvous that proves the hold.
                Assert.True(actor.Tell(new ComposerCancelStreamingMessage()));
                await hold.Parked.WaitAsync(ControlledTimeProvider.HangGuard, Ct);
                Assert.False(actor.Completion.IsCompleted);
                onParked(composer);
            },
        };
        return probe;
    }

    private static async Task ObserveWhileHoldingActorAsync(ActorLoopHold hold, ComposerFixtureProbe probe,
        Task fixtureTask, Func<Task> observe, Func<Exception?> expectedFixtureFailure)
    {
        ExceptionDispatchInfo? primary = null;
        var cleanupFaults = new List<Exception>();
        try
        {
            await observe();
        }
        catch (Exception ex)
        {
            primary = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            hold.Release();
            // Wait for the owner first: an assertion may have failed before it published Cleanup.
            // Bound both waits independently of Ct, and never swallow an unexpected cleanup fault.
            try { await fixtureTask.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None); }
            catch (Exception ex) when (ReferenceEquals(ex, expectedFixtureFailure()))
            {
                if (primary is not null)
                    ForwardAttachedCleanupFaults(ex, cleanupFaults);
            }
            catch (Exception ex) { cleanupFaults.Add(ex); }

            try
            {
                var cleanup = probe.Cleanup
                    ?? throw new InvalidOperationException("The fixture did not publish its retained cleanup task.");
                await cleanup.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
            }
            catch (Exception ex) { cleanupFaults.Add(ex); }

            // Join actual teardown even when an observation failed before releasing the actor.
            if (probe.Composer is { } composer)
            {
                try { await composer.TeardownCompletion.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None); }
                catch (Exception ex) { cleanupFaults.Add(ex); }
            }
        }

        if (primary is not null)
        {
            if (cleanupFaults.Count > 0)
                primary.SourceException.Data["TestCleanupFaults"] = cleanupFaults.ToArray();
            primary.Throw();
        }

        if (cleanupFaults.Count > 0)
            throw new AggregateException(cleanupFaults);
    }

    // ── Teardown-ordering regression for the shared Composer fixture ──────────

    [Theory]
    [InlineData("scenario")]
    [InlineData("setup")]
    public async Task ComposerFixture_ExpiredDiagnosticWait_NeverDeletesBeforeActualTeardownIsJoined(string failurePhase)
    {
        var setupFailure = failurePhase == "setup";
        var diagnosticClock = new ControlledTimeProvider();
        var actorClock = new ControlledTimeProvider();
        var hold = new ActorLoopHold();
        Exception? injected = null;
        var probe = NewHeldComposerProbe(hold, actorClock, diagnosticClock, composer =>
        {
            Assert.Equal(FixtureModel, AgentServiceOf(composer).Model);
            if (setupFailure)
                throw injected = new InvalidOperationException("INJECTED-SETUP-FAILURE");
        });

        var scenarioEntered = false;
        var fixtureTask = WithFaultingComposerStatsAsync(composer =>
        {
            scenarioEntered = true;
            Assert.Null(AgentServiceOf(composer).Model);
            // An actual xUnit assertion fails inside the connected scenario, with the actor held.
            try { Assert.Fail("INJECTED-SCENARIO-ASSERTION"); }
            catch (Xunit.Sdk.FailException ex) { injected = ex; throw; }
            return Task.CompletedTask;
        }, probe);

        await ObserveWhileHoldingActorAsync(hold, probe, fixtureTask, async () =>
        {
            await probe.DisposeStarted.Task.WaitAsync(ControlledTimeProvider.HangGuard, Ct);
            await actorClock.WaitForPendingTimerAsync(Actor<IComposerMessage>.DisposeTimeout, Ct);
            await diagnosticClock.WaitForPendingTimerAsync(probe.DiagnosticDeadline, Ct);
            Assert.True(hold.Parked.IsCompletedSuccessfully);
            Assert.Equal(!setupFailure, scenarioEntered);
            Assert.NotNull(injected);
            Assert.False(ActorOf(probe.Composer!).Tell(new ComposerCancelStreamingMessage()));
            Assert.False(probe.DisposeReturned.Task.IsCompleted);
            AssertTeardownStillHeld(probe, "before the actor disposal deadline");
            Assert.Equal(FixtureModel, AgentServiceOf(probe.Composer!).Model);

            // The real actor timer expires first. Confirm DisposeAsync returned and the retained
            // join routine actually suspended, not just that a signal ahead of the await fired.
            actorClock.Advance(Actor<IComposerMessage>.DisposeTimeout);
            await probe.DisposeReturned.Task.WaitAsync(ControlledTimeProvider.HangGuard, Ct);
            var join = await probe.JoinStarted.Task.WaitAsync(ControlledTimeProvider.HangGuard, Ct);
            Assert.False(join.IsCompleted, "The actual teardown join must be pending while the actor is held.");
            AssertTeardownStillHeld(probe, "after DisposeAsync returned and before diagnostic expiry");
            Assert.Equal(FixtureModel, AgentServiceOf(probe.Composer!).Model);

            diagnosticClock.Advance(probe.DiagnosticDeadline);
            var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
                fixtureTask.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            Assert.Same(injected, failure);
            var retained = Assert.IsType<Exception[]>(failure.Data[ComposerFixtureCleanupFaultsKey]);
            var timeout = Assert.IsType<TimeoutException>(Assert.Single(retained));
            Assert.Contains("did not cancel, abandon or short-cut the cleanup", timeout.Message, StringComparison.Ordinal);
            Assert.IsType<TimeoutException>(timeout.InnerException);
            Assert.False(join.IsCompleted);
            AssertTeardownStillHeld(probe, "after the diagnostic wait expired");
            Assert.Equal(FixtureModel, AgentServiceOf(probe.Composer!).Model);
        }, () => injected);

        AssertJoinedBeforeDeletion(probe);
        var primary = await Record.ExceptionAsync(() => fixtureTask);
        Assert.Same(injected, primary);
        Assert.IsType<TimeoutException>(Assert.Single(Assert.IsType<Exception[]>(
            primary!.Data[ComposerFixtureCleanupFaultsKey])));
    }

    [Fact]
    public async Task ComposerFixture_ObserverAssertionFailure_ReleasesActorAndJoinsRetainedCleanup()
    {
        var hold = new ActorLoopHold();
        var actorClock = new ControlledTimeProvider();
        var diagnosticClock = new ControlledTimeProvider();
        var probe = NewHeldComposerProbe(hold, actorClock, diagnosticClock, _ => { });
        var fixtureTask = WithFaultingComposerStatsAsync(_ => Task.CompletedTask, probe);
        var failure = await Assert.ThrowsAsync<Xunit.Sdk.FailException>(() => ObserveWhileHoldingActorAsync(
            hold, probe, fixtureTask, async () =>
            {
                await diagnosticClock.WaitForPendingTimerAsync(probe.DiagnosticDeadline, Ct);
                Assert.True(hold.Parked.IsCompletedSuccessfully);
                AssertTeardownStillHeld(probe, "when the observer assertion fails");
                Assert.Fail("INJECTED-OBSERVER-ASSERTION");
            }, () => null));

        Assert.Equal("INJECTED-OBSERVER-ASSERTION", failure.Message);
        Assert.False(failure.Data.Contains("TestCleanupFaults"));
        AssertJoinedBeforeDeletion(probe);
    }

    // ── Faulted-teardown ownership ────────────────────────────────────────────

    // The test owns both gates and all tasks from the instant they are started. The fallback cannot
    // use a faulted TeardownCompletion to decide whether it is safe to touch the directory.
    private static Task WithFallbackCleanupAsync(ActorLoopHold hold, ComposerFixtureProbe probe,
        Task fixtureTask, Func<Exception?> expectedFixtureFailure, Func<Task> body, Action? releaseClient = null,
        FallbackDecisionRecord? decisionRecord = null) =>
        WithFallbackCleanupAsync(hold, fixtureTask, expectedFixtureFailure, body,
            () => probe.Composer, () => probe.StateDir, () => probe.Cleanup, releaseClient,
            decisionRecord: decisionRecord);

    private sealed class FallbackJoinExpiredException(string operation)
        : TimeoutException($"The bounded fallback wait for {operation} expired.");

    private sealed record FallbackJoinAttempt(Task Work, Task Join, bool PendingAtEntry);

    // Record the actual join task after the bounded wait has run to its first suspension, not just
    // a signal before calling it. The observer can then prove the owner and its join are both pending.
    private sealed class FallbackJoinTrace
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<FallbackJoinAttempt>> _slots = new();
        private readonly ConcurrentQueue<string> _order = new();

        private TaskCompletionSource<FallbackJoinAttempt> Slot(string operation) =>
            _slots.GetOrAdd(operation, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

        internal void Started(string operation, FallbackJoinAttempt attempt)
        {
            _order.Enqueue(operation);
            Slot(operation).TrySetResult(attempt);
        }

        internal Task<FallbackJoinAttempt> WaitForStartAsync(string operation) =>
            Slot(operation).Task.WaitAsync(ControlledTimeProvider.HangGuard, Ct);

        internal string[] Order => _order.ToArray();
    }

    private sealed class SanitationJoinExpiredException(string operation)
        : TimeoutException($"The bounded sanitation wait for {operation} expired.")
    {
        internal string Operation { get; } = operation;
    }

    /// <summary>
    /// Optional seams for the test-owned SANITATION joins (null by default: system clock, no hooks).
    /// <see cref="Decided"/> runs between the instant a bounded wait is DECIDED and the moment the sanitizer acts on
    /// it, so a test can hold only that decision continuation while the real owner settles.
    /// </summary>
    private sealed class SanitationBounds
    {
        internal TimeProvider Clock { get; init; } = TimeProvider.System;

        /// <summary>(operation, boundExpired, workStillPendingAtTheDecision)</summary>
        internal Func<string, bool, bool, Task>? Decided { get; init; }

        /// <summary>Runs immediately before the sanitizer deletes the directory.</summary>
        internal Action? BeforeDelete { get; init; }

        /// <summary>Runs immediately before the sanitizer closes the test-owned client resource (the resource is still open).</summary>
        internal Action? BeforeResourceClose { get; init; }

        /// <summary>
        /// Receives, BY REFERENCE and at its creation site, each retention diagnostic the DIRECT sanitizer itself produces when
        /// it declines to delete. A test then identifies that diagnostic by identity, not by message or leaf count.
        /// </summary>
        internal Action<Exception>? OwnRetentionReport { get; init; }

        /// <summary>Observes the actual diagnostic created by a specific expired join.</summary>
        internal Action<string, SanitationJoinExpiredException>? OwnDeadline { get; init; }
    }

    // ONE rule for every sanitation join: the bounded wait's WINNER is recorded at the decision and is final. Returns
    // false when the bound won - and then a LATER completion of the work can supply fault evidence (added to
    // `faults`) but can never reverse the decision. A join that completes inside its bound is awaited for its exact
    // outcome; `expected` filters only the exact primaries the caller already reports.
    private static async Task<bool> JoinSanitationWorkAsync(Task work, string operation, SanitationBounds? bounds,
        List<Exception> faults, Func<Exception, bool>? expected)
    {
        var expired = false;
        if (!work.IsCompleted)
        {
            using var timerCancellation = new CancellationTokenSource();
            var deadline = Task.Delay(ControlledTimeProvider.HangGuard, bounds?.Clock ?? TimeProvider.System, timerCancellation.Token);
            expired = ReferenceEquals(await Task.WhenAny(work, deadline), deadline);
            if (!expired)
                timerCancellation.Cancel();
        }

        var callbackSucceeded = true;
        if (bounds?.Decided is { } decided)
        {
            try { await decided(operation, expired, !work.IsCompleted); }
            catch (Exception ex)
            {
                // An observation failure is not permission to delete or to skip the other joins.
                AddDistinctFault(faults, ex);
                callbackSucceeded = false;
            }
        }

        if (expired)
        {
            var expiry = new SanitationJoinExpiredException(operation);
            faults.Add(expiry);
            try { bounds?.OwnDeadline?.Invoke(operation, expiry); }
            catch (Exception ex) { AddDistinctFault(faults, ex); }
            if (work.IsFaulted)
            {
                foreach (var leaf in Flattened(work.Exception!))
                {
                    if (!(expected?.Invoke(leaf) ?? false))
                        AddDistinctFault(faults, leaf);
                }
            }

            return false;
        }

        // `await` rethrows only the FIRST inner exception, so classifying that one exception would let an expected first leaf
        // swallow every other leaf of the task. Classify EACH leaf of the task's own outcome instead.
        try { await work; }
        catch (Exception ex)
        {
            foreach (var leaf in Flattened(work.Exception ?? ex))
            {
                if (!(expected?.Invoke(leaf) ?? false))
                    AddDistinctFault(faults, leaf);
            }
        }
        return callbackSucceeded;
    }

    // ── Expected-fault classification for every sanitation join ─────────────────────────────────────────
    //
    // A sanitizer may drop a leaf of the real retained owner's outcome ONLY when that leaf is one of:
    //   * an EXACT object the test itself created or is reporting as its primary, BY REFERENCE TO THAT OBJECT ONLY: the
    //     intended original-client disposal exception, a scenario's injected fault, and a reported primary failure. The
    //     leaves of a reported aggregate are NOT trusted through it (they are what the owner reported - circular);
    //   * the owner's own retention diagnostic, tagged at creation by the retention decision, and carried by the actual
    //     retained cleanup task. A matching type, message, directory and aggregate membership are not proof of origin.
    // Nothing attached to a reported primary is trusted: those attachments are built from the very owner outcome being
    // filtered, so provenance from them would be circular. A faulted task, an exception type or "every leaf of the observed
    // aggregate" proves nothing. The sanitizer's own deadline is never in this set: it is always reported, and distinctly.
    private enum ReportSource { Owner, Fallback, Sanitizer }
    private static readonly object RetentionSourceKey = new();

    private static InvalidOperationException RetentionDiagnostic(ReportSource source, string message)
    {
        var report = new InvalidOperationException(message);
        report.Data[RetentionSourceKey] = source;
        return report;
    }

    private static readonly string OwnerQuiescencePrefix = "Composer disposal could not be shown quiescent (";
    private static readonly string OwnerRecoveryPrefix = "Composer teardown faulted and recovery could not establish quiescence (";

    private static Func<Exception, bool> ExpectedSanitationLeaf(Func<Task?> retainedCleanup, Func<string?> stateDir,
        Exception? clientFault, params Exception?[] exact)
    {
        var expected = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        foreach (var known in exact.Append(clientFault))
        {
            if (known is null)
                continue;
            expected.Add(known);
        }

        // The fallback's OWN retention diagnostic is expected on the report it produced (it is built by the fallback, not
        // by the owner). It is recognised by what it is - a leaf of that exact report whose message is the fallback's
        // quiescence form, names this state directory and the exact client exception - never because the report carries it.
        var reports = exact.Where(known => known is not null).Select(known => known!).ToArray();
        return leaf => expected.Contains(leaf)
            || IsOwnerRetentionDiagnostic(leaf, retainedCleanup(), stateDir(), clientFault)
            || IsFallbackRetentionDiagnostic(leaf, reports, stateDir(), clientFault);
    }

    private static bool IsFallbackRetentionDiagnostic(Exception leaf, Exception[] reports, string? stateDir, Exception? clientFault) =>
        leaf.Data[RetentionSourceKey] is ReportSource.Fallback && stateDir is not null && clientFault is not null
        && leaf.Message.StartsWith("Fixture fallback could not prove Composer dependents quiescent (", StringComparison.Ordinal)
        && leaf.Message.Contains($"{clientFault.GetType().Name}: {clientFault.Message}", StringComparison.Ordinal)
        && leaf.Message.Contains($"'{stateDir}' was RETAINED, not deleted.", StringComparison.Ordinal)
        && reports.Any(report => Flattened(report).Any(owned => ReferenceEquals(owned, leaf))
            || (report.Data["FallbackCleanupFaults"] is Exception[] attached
                && attached.SelectMany(Flattened).Any(owned => ReferenceEquals(owned, leaf))));

    private static bool IsOwnerRetentionDiagnostic(Exception leaf, Task? retainedCleanup, string? stateDir, Exception? clientFault)
    {
        if (leaf.Data[RetentionSourceKey] is not ReportSource.Owner || stateDir is null
            || retainedCleanup?.Exception is not { } ownerOutcome
            || !Flattened(ownerOutcome).Any(owned => ReferenceEquals(owned, leaf))
            || !leaf.Message.Contains($"'{stateDir}' was RETAINED, not deleted.", StringComparison.Ordinal))
            return false;

        if (leaf.Message.StartsWith(OwnerRecoveryPrefix, StringComparison.Ordinal))
            return true;
        return clientFault is not null && leaf.Message.StartsWith(OwnerQuiescencePrefix, StringComparison.Ordinal)
            && leaf.Message.Contains($"{clientFault.GetType().Name}: {clientFault.Message}", StringComparison.Ordinal);
    }

    private static bool IsSanitizerOwnRetentionReport(Exception leaf) =>
        leaf.Data[RetentionSourceKey] is ReportSource.Sanitizer;

    private static Task JoinFallbackWorkAsync(Task work, string operation, TimeProvider clock,
        FallbackJoinTrace? trace = null)
    {
        var pending = !work.IsCompleted;
        var join = WaitForWorkAsync(work, operation, clock);
        trace?.Started(operation, new FallbackJoinAttempt(work, join, pending));
        return join;

        static async Task WaitForWorkAsync(Task work, string operation, TimeProvider clock)
        {
            if (!work.IsCompleted)
            {
                using var timerCancellation = new CancellationTokenSource();
                var deadline = Task.Delay(ControlledTimeProvider.HangGuard, clock, timerCancellation.Token);
                if (ReferenceEquals(await Task.WhenAny(work, deadline), deadline))
                    throw new FallbackJoinExpiredException(operation);
                timerCancellation.Cancel();
            }

            await work;
        }
    }

    private static async Task WithFallbackCleanupAsync(ActorLoopHold hold, Task fixtureTask,
        Func<Exception?> expectedFixtureFailure, Func<Task> body, Func<Composer?> getComposer,
        Func<string?> getStateDir, Func<Task?> getCleanup, Action? releaseClient = null,
        Func<IEnumerable<Exception>>? emergencyDisposalFaults = null, FallbackDecisionRecord? decisionRecord = null,
        TimeProvider? fallbackClock = null, FallbackJoinTrace? joinTrace = null)
    {
        ExceptionDispatchInfo? primary = null;
        var faults = new List<Exception>();
        var unproven = new List<string>();
        var clock = fallbackClock ?? TimeProvider.System;
        try { await body(); }
        catch (Exception ex) { primary = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            hold.Release();
            try { releaseClient?.Invoke(); }
            catch (Exception ex)
            {
                faults.Add(ex);
                unproven.Add("a test-owned release failed");
            }

            void Expired(FallbackJoinExpiredException ex)
            {
                faults.Add(ex);
                unproven.Add(ex.Message);
            }

            // Even if one bound expires, continue joining the other work. Retain that fact even
            // if the work completes later; a timed-out observer must not authorize deletion.
            try { await JoinFallbackWorkAsync(fixtureTask, "the fixture task", clock, joinTrace); }
            catch (FallbackJoinExpiredException ex) { Expired(ex); }
            catch (Exception ex) when (ReferenceEquals(ex, expectedFixtureFailure()))
            {
                if (primary is not null)
                    ForwardAttachedCleanupFaults(ex, faults);
            }
            catch (Exception ex) { AddDistinctFault(faults, ex); }

            var cleanup = getCleanup();
            if (cleanup is null)
            {
                const string missing = "No retained Composer cleanup was published.";
                faults.Add(new InvalidOperationException(missing));
                unproven.Add(missing);
            }
            else
            {
                try { await JoinFallbackWorkAsync(cleanup, "the retained cleanup", clock, joinTrace); }
                catch (FallbackJoinExpiredException ex) { Expired(ex); }
                catch (Exception ex)
                {
                    if (primary is not null || !cleanup.IsFaulted)
                        AddDistinctFault(faults, cleanup.Exception ?? ex);
                }
            }

            var composer = getComposer();
            if (composer is not null)
            {
                try { await JoinFallbackWorkAsync(ActorOf(composer).Completion, "real actor completion", clock, joinTrace); }
                catch (FallbackJoinExpiredException ex) { Expired(ex); }
                catch (Exception ex)
                {
                    faults.Add(ex);
                    unproven.Add("the actor did not join successfully");
                }

                try { await JoinFallbackWorkAsync(composer.TeardownCompletion, "real Composer teardown", clock, joinTrace); }
                catch (FallbackJoinExpiredException ex) { Expired(ex); }
                catch (Exception ex)
                {
                    // A faulted original teardown task stays faulted after a successful retry.
                    // The fixture already reports it; retain it here if the observer failed.
                    if (primary is not null)
                        AddDistinctFault(faults, ex);
                }
            }

            try
            {
                var dir = getStateDir();
                var retain = unproven.Count > 0;
                if (retain && decisionRecord is not null)
                {
                    decisionRecord.Reasons = unproven.ToArray();
                    decisionRecord.Reached = true;
                }

                if (!retain && composer is not null)
                {
                    var retried = false;
                    if (Directory.Exists(dir) || AgentServiceOf(composer).Agent is not null || !IssueLockIsDisposed(composer))
                    {
                        // Real dependent cleanup the owner could not finish. On a client that already failed this is
                        // a NO-OP (its reference was cleared first), so it can never authorize deletion by itself.
                        retried = true;
                        await JoinFallbackWorkAsync(composer.DisposeAsync().AsTask(), "dependent disposal retry", clock, joinTrace);
                    }

                    // ONE positive-evidence decision for every fallback deletion.
                    var (reasons, clientFault) = FallbackUnsafety(composer);
                    if (decisionRecord is not null)
                    {
                        decisionRecord.Retried = retried;
                        decisionRecord.Reasons = reasons.ToArray();
                        decisionRecord.Reached = true;
                    }

                    if (reasons.Count > 0)
                    {
                        retain = true;
                        if (clientFault is not null)
                            AddDistinctFault(faults, clientFault);
                        faults.Add(RetentionDiagnostic(ReportSource.Fallback,
                            $"Fixture fallback could not prove Composer dependents quiescent ({string.Join("; ", reasons)}); "
                            + $"the state directory '{dir}' was RETAINED, not deleted."));
                    }
                }

                if (!retain && dir is not null)
                {
                    if (decisionRecord is not null)
                    {
                        decisionRecord.ClientDisposalSucceededAtDelete = getComposer() is { } deletingComposer
                            && ClientEvidence.TryGetValue(deletingComposer, out var client)
                            && client.Outcome.Task is { IsCompletedSuccessfully: true, Result: null };
                        decisionRecord.DeletionStarted = true;
                    }
                    await CopilotHive.Worker.GitOperations.ForceDeleteDirectoryAsync(dir)
                        .WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
                }
            }
            catch (Exception ex) { faults.Add(ex); }
        }

        if (primary is not null)
        {
            if (emergencyDisposalFaults is not null)
                faults.AddRange(emergencyDisposalFaults());
            if (faults.Count > 0)
                primary.SourceException.Data["FallbackCleanupFaults"] = faults.ToArray();
            primary.Throw();
        }

        if (faults.Count > 0)
            throw new AggregateException(faults);
    }

    private sealed class HeldChatClientDisposal
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async ValueTask DisposeAsync()
        {
            Entered.TrySetResult();
            await _release.Task;
            Finished.TrySetResult();
        }

        internal void Release() => _release.TrySetResult();
    }

    private static IEnumerable<Exception> Flattened(Exception fault) =>
        fault is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [fault];

    // Hold the real actor at its awaited read boundary and make its disposal throw: a cancellation callback
    // registered on the actor's loop token runs synchronously inside Actor.DisposeAsync -> CTS.Cancel().
    private static ComposerFixtureProbe NewCancellationFaultProbe(ActorLoopHold hold, ControlledTimeProvider actorClock,
        ControlledTimeProvider diagnosticClock, Exception callbackFault, Action onCallback,
        Func<Composer, Task>? dependentRecovery = null, HeldChatClientDisposal? clientDisposal = null,
        ObservedClientDisposal? observedClient = null)
    {
        return NewHeldComposerProbe(hold, actorClock, diagnosticClock, composer =>
        {
            var cts = (CancellationTokenSource)typeof(Actor<IComposerMessage>)
                .GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(ActorOf(composer))!;
            cts.Token.Register(() =>
            {
                onCallback();
                throw callbackFault;
            });
        }, dependentRecovery, clientDisposal is null ? null : clientDisposal.DisposeAsync, observedClient);
    }

    private static async Task AssertCallbackFaultHeldAsync(ComposerFixtureProbe probe, ActorLoopHold hold,
        Exception callbackFault, Func<bool> callbackRan, Task fixtureTask)
    {
        var winner = await Task.WhenAny(probe.RecoveryActorJoin.Task, probe.BeforeDeleteEntered.Task, fixtureTask)
            .WaitAsync(ControlledTimeProvider.HangGuard, Ct);
        Assert.True(ReferenceEquals(probe.RecoveryActorJoin.Task, winner),
            "cleanup reached deletion without joining the actor after the faulted teardown");
        var actorJoin = await probe.RecoveryActorJoin.Task;

        var composer = probe.Composer!;
        var agentService = AgentServiceOf(composer);

        // The callback fault really reached the REAL disposal path and faulted the original teardown task.
        Assert.True(callbackRan(), "the cancellation callback never ran inside Actor.DisposeAsync");
        Assert.True(hold.Parked.IsCompletedSuccessfully);
        Assert.True(probe.DisposeReturned.Task.IsCompleted);
        Assert.True(composer.TeardownCompletion.IsFaulted, "the original teardown task must be faulted");
        var initialDisposalFault = Assert.Single(composer.TeardownCompletion.Exception!.InnerExceptions);
        Assert.Contains(callbackFault, Flattened(initialDisposalFault));
        Assert.Contains("Actor`1.DisposeAsync", initialDisposalFault.StackTrace, StringComparison.Ordinal);
        Assert.False(ActorOf(composer).Tell(new ComposerCancelStreamingMessage()));

        // ... yet nothing is quiescent: actor pending, agent live, deletion not started, state intact.
        Assert.False(actorJoin.IsCompleted, "the retained cleanup must still be joining the actor");
        Assert.False(ActorOf(composer).Completion.IsCompleted, "the held actor must still be running");
        Assert.NotNull(agentService.Agent);
        Assert.True(agentService.IsConnected, "the agent must still be live");
        Assert.False(probe.Cleanup!.IsCompleted, "the retained cleanup must still own the directory");
        Assert.False(fixtureTask.IsCompleted);
        Assert.False(probe.BeforeDeleteEntered.Task.IsCompleted, "deletion must not have started");
        Assert.True(Directory.Exists(probe.StateDir));
        Assert.Equal("live state", File.ReadAllText(Path.Combine(probe.StateDir!, "sentinel.txt")));
    }

    private static async Task<Exception[]> ObserveFaultedFixtureAsync(Task fixtureTask, Exception? injected,
        Exception callbackFault, ComposerFixtureProbe probe)
    {
        var primary = await Record.ExceptionAsync(() => fixtureTask.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
        Assert.Same(injected, primary);
        var retained = Assert.IsType<Exception[]>(primary!.Data[ComposerFixtureCleanupFaultsKey]);
        Assert.True(probe.Composer!.TeardownCompletion.IsFaulted);
        Assert.True(probe.Cleanup!.IsFaulted);
        // The first disposal's AggregateException remains available on the primary failure, not
        // replaced by an error from recovery or a diagnostic timeout.
        var original = Assert.Single(probe.Composer.TeardownCompletion.Exception!.InnerExceptions);
        Assert.Contains(retained, fault => ReferenceEquals(original, fault)
            || fault is AggregateException combined && combined.InnerExceptions.Any(inner => ReferenceEquals(original, inner)));
        var flat = retained.SelectMany(Flattened).ToArray();
        Assert.Contains(callbackFault, flat);
        return flat;
    }

    private static Task FailFaultedFixtureScenario(Action<Exception> remember)
    {
        try { Assert.Fail("INJECTED-SCENARIO-ASSERTION"); }
        catch (Xunit.Sdk.FailException ex) { remember(ex); throw; }
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComposerFixture_FaultedActorDisposal_KeepsOwnershipUntilActorAndDependentsAreQuiescent(bool dependentDisposeThrows)
    {
        var hold = new ActorLoopHold();
        var client = new HeldChatClientDisposal();
        var callbackFault = new InvalidOperationException("INJECTED-CANCELLATION-CALLBACK-FAULT");
        var dependentFault = new InvalidOperationException("INJECTED-DEPENDENT-DISPOSAL-FAULT");
        var callbackRan = false;
        Exception? injected = null;
        var probe = NewCancellationFaultProbe(hold, new ControlledTimeProvider(), new ControlledTimeProvider(),
            callbackFault, () => callbackRan = true, clientDisposal: client);

        // A fault at the real agent-dispose hook is reported by the retry after the agent and client
        // were attempted. It must not be mistaken for success merely because the references clear.
        var fixtureTask = WithFaultingComposerStatsAsync(composer =>
        {
            AgentServiceOf(composer).OnAgentDisposing = _ =>
            {
                if (dependentDisposeThrows)
                    throw dependentFault;
            };
            return FailFaultedFixtureScenario(ex => injected = ex);
        }, probe);

        await WithFallbackCleanupAsync(hold, probe, fixtureTask, () => injected, async () =>
        {
            await AssertCallbackFaultHeldAsync(probe, hold, callbackFault, () => callbackRan, fixtureTask);
            Assert.False(client.Entered.Task.IsCompleted);
            hold.Release();

            var composer = probe.Composer!;
            var winner = await Task.WhenAny(client.Entered.Task, probe.BeforeDeleteEntered.Task, fixtureTask)
                .WaitAsync(ControlledTimeProvider.HangGuard, Ct);
            Assert.True(ActorOf(composer).Completion.IsCompletedSuccessfully);
            Assert.True(ReferenceEquals(client.Entered.Task, winner),
                "The actor completed, but cleanup reached its outcome without awaiting dependent client disposal.");
            Assert.Null(AgentServiceOf(composer).Agent); // cleared BEFORE disposal finishes
            Assert.False(client.Finished.Task.IsCompleted);
            Assert.False(probe.Cleanup!.IsCompleted);
            Assert.False(probe.BeforeDeleteEntered.Task.IsCompleted);
            Assert.True(Directory.Exists(probe.StateDir));
            Assert.Equal("live state", File.ReadAllText(Path.Combine(probe.StateDir!, "sentinel.txt")));
            Assert.False(IssueLockIsDisposed(composer));

            client.Release();
            var faults = await ObserveFaultedFixtureAsync(fixtureTask, injected, callbackFault, probe);
            Assert.True(client.Finished.Task.IsCompletedSuccessfully);
            Assert.False(AgentServiceOf(composer).IsConnected);
            Assert.True(IssueLockIsDisposed(composer));
            Assert.True(probe.ActorCompletedAtDelete);
            Assert.False(probe.TeardownCompleteAtDelete);

            if (dependentDisposeThrows)
            {
                Assert.Contains(dependentFault, faults);
                Assert.Contains(faults, fault => fault.Message.Contains("was RETAINED", StringComparison.Ordinal));
                Assert.Equal("dependent disposal did not complete successfully", probe.RetainedReason);
                Assert.False(probe.DependentsQuiescentAtDelete);
                Assert.False(probe.BeforeDeleteEntered.Task.IsCompleted);
                Assert.True(Directory.Exists(probe.StateDir));
                Assert.Equal("live state", File.ReadAllText(Path.Combine(probe.StateDir!, "sentinel.txt")));
            }
            else
            {
                Assert.DoesNotContain(dependentFault, faults);
                Assert.Null(probe.RetainedReason);
                Assert.True(probe.DependentsQuiescentAtDelete);
                Assert.True(probe.BeforeDeleteEntered.Task.IsCompletedSuccessfully);
                Assert.False(Directory.Exists(probe.StateDir));
            }
        }, client.Release);
        Assert.False(Directory.Exists(probe.StateDir));
    }

    [Fact]
    public async Task ComposerFixture_FaultedActorDisposal_WithUnprovableDependentCleanup_RetainsDirectoryAndReportsWhy()
    {
        var hold = new ActorLoopHold();
        var callbackFault = new InvalidOperationException("INJECTED-CANCELLATION-CALLBACK-FAULT");
        var callbackRan = false;
        Exception? injected = null;
        // A completed recovery delegate alone does not dispose the still-live agent.
        var probe = NewCancellationFaultProbe(hold, new ControlledTimeProvider(), new ControlledTimeProvider(),
            callbackFault, () => callbackRan = true, dependentRecovery: _ => Task.CompletedTask);
        var fixtureTask = WithFaultingComposerStatsAsync(_ => FailFaultedFixtureScenario(ex => injected = ex), probe);

        await WithFallbackCleanupAsync(hold, probe, fixtureTask, () => injected, async () =>
        {
            await AssertCallbackFaultHeldAsync(probe, hold, callbackFault, () => callbackRan, fixtureTask);
            hold.Release();
            var faults = await ObserveFaultedFixtureAsync(fixtureTask, injected, callbackFault, probe);
            var report = Assert.Single(faults.OfType<InvalidOperationException>(),
                fault => fault.Message.Contains("was RETAINED", StringComparison.Ordinal));
            Assert.Contains("the connected agent was not disposed", report.Message, StringComparison.Ordinal);
            Assert.True(ActorOf(probe.Composer!).Completion.IsCompletedSuccessfully);
            Assert.True(AgentServiceOf(probe.Composer!).IsConnected);
            Assert.False(probe.DependentsQuiescentAtDelete);
            Assert.NotNull(probe.RetainedReason);
            Assert.False(probe.BeforeDeleteEntered.Task.IsCompleted);
            Assert.True(Directory.Exists(probe.StateDir));
            Assert.Equal("live state", File.ReadAllText(Path.Combine(probe.StateDir!, "sentinel.txt")));
        });

        // Only the test-owned fallback cleaned up what the fixture rightly retained.
        Assert.False(AgentServiceOf(probe.Composer!).IsConnected);
        Assert.True(IssueLockIsDisposed(probe.Composer!));
        Assert.False(Directory.Exists(probe.StateDir));
    }

    [Fact]
    public async Task ComposerFixture_FaultedActorDisposal_ObserverAssertionFailureStillReleasesAndRecovers()
    {
        var hold = new ActorLoopHold();
        var callbackFault = new InvalidOperationException("INJECTED-CANCELLATION-CALLBACK-FAULT");
        var callbackRan = false;
        Exception? injected = null;
        var probe = NewCancellationFaultProbe(hold, new ControlledTimeProvider(), new ControlledTimeProvider(),
            callbackFault, () => callbackRan = true, dependentRecovery: _ => Task.CompletedTask);
        var fixtureTask = WithFaultingComposerStatsAsync(_ => FailFaultedFixtureScenario(ex => injected = ex), probe);

        var failure = await Assert.ThrowsAsync<Xunit.Sdk.FailException>(() => WithFallbackCleanupAsync(
            hold, probe, fixtureTask, () => injected, async () =>
            {
                await AssertCallbackFaultHeldAsync(probe, hold, callbackFault, () => callbackRan, fixtureTask);
                Assert.Fail("INJECTED-FAULTED-TEARDOWN-OBSERVER-ASSERTION");
            }));
        Assert.Equal("INJECTED-FAULTED-TEARDOWN-OBSERVER-ASSERTION", failure.Message);
        var faults = Assert.IsType<Exception[]>(failure.Data["FallbackCleanupFaults"]).SelectMany(Flattened).ToArray();
        Assert.Contains(callbackFault, faults);
        Assert.Contains(faults, fault => fault.Message.Contains("was RETAINED", StringComparison.Ordinal));
        Assert.False(probe.BeforeDeleteEntered.Task.IsCompleted); // fixture retained; fallback removed
        Assert.True(ActorOf(probe.Composer!).Completion.IsCompletedSuccessfully);
        Assert.False(AgentServiceOf(probe.Composer!).IsConnected);
        Assert.True(IssueLockIsDisposed(probe.Composer!));
        Assert.False(Directory.Exists(probe.StateDir));
    }

    // Ordinary invocation: no ComposerFixtureProbe is supplied. Use the real Composer warning as
    // the deferred-disposal rendezvous and obtain the tested outcome only from the caller failure.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task ComposerFixture_ExpiredDiagnostic_CallerAwaitsTheRetainedCleanupOutcome(bool withPrimary, bool lateAgentDisposalFault)
    {
        var hold = new ActorLoopHold();
        var actorClock = new ControlledTimeProvider();
        var diagnosticClock = new ControlledTimeProvider();
        var agentDisposalFault = new InvalidOperationException("INJECTED-AGENT-DISPOSAL-FAULT");
        var hookRuns = 0;
        var diagnosticObserved = false;
        bool? hookAfterDiagnostic = null;
        string? modelAtDisposal = null;
        Exception? injected = null;
        Exception? callerFailure = null;
        Task? callerOutcome = null;
        Composer? composer = null;
        ComposerFixtureLogger? logger = null;
        string? stateDir = null;

        var fixtureTask = WithFaultingComposerStatsAsync(async running =>
        {
            // Acquire these before arming the hold so a failed setup assertion cannot orphan it.
            composer = running;
            stateDir = Assert.IsType<string>(typeof(Composer).GetField("_stateDir", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(running));
            logger = Assert.IsType<ComposerFixtureLogger>(typeof(Composer)
                .GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(running));
            File.WriteAllText(Path.Combine(stateDir, "sentinel.txt"), "live state");
            var actor = ActorOf(running);
            typeof(Actor<IComposerMessage>).GetField("_timeProvider", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(actor, actorClock);
            actor.OnBeforeReadAsync = hold.OnBeforeReadAsync;
            Assert.True(actor.Tell(new ComposerCancelStreamingMessage()));
            await hold.Parked.WaitAsync(ControlledTimeProvider.HangGuard, Ct);
            Assert.False(actor.Completion.IsCompleted);
            var agentService = AgentServiceOf(running);
            agentService.OnAgentDisposing = _ =>
            {
                hookAfterDiagnostic = Volatile.Read(ref diagnosticObserved);
                modelAtDisposal = agentService.Model;
                Interlocked.Increment(ref hookRuns);
                if (lateAgentDisposalFault)
                    throw agentDisposalFault;
            };
            if (withPrimary)
                await FailFaultedFixtureScenario(ex => injected = ex);
        }, diagnosticClock: diagnosticClock);

        await WithFallbackCleanupAsync(hold, fixtureTask, () => callerFailure ?? injected, async () =>
        {
            await actorClock.WaitForPendingTimerAsync(Actor<IComposerMessage>.DisposeTimeout, Ct);
            await diagnosticClock.WaitForPendingTimerAsync(FixtureDiagnosticDeadline, Ct);
            Assert.True(hold.Parked.IsCompletedSuccessfully);
            actorClock.Advance(Actor<IComposerMessage>.DisposeTimeout);
            // Composer logs this *inside* the deferred branch, before it schedules dependent disposal.
            // Its branch decision is therefore final before this test can release the actor.
            await logger!.DeferredSelected.Task.WaitAsync(ControlledTimeProvider.HangGuard, Ct);
            Assert.False(fixtureTask.IsCompleted);

            diagnosticClock.Advance(FixtureDiagnosticDeadline);
            callerFailure = await Record.ExceptionAsync(() => fixtureTask.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            Assert.True(fixtureTask.IsCompleted, "the fixture's clock, not the test hang guard, must produce the diagnostic");
            TimeoutException expiry;
            if (withPrimary)
            {
                Assert.Same(injected, callerFailure);
                Assert.IsType<Xunit.Sdk.FailException>(callerFailure);
                expiry = Assert.IsType<TimeoutException>(Assert.Single(Assert.IsType<Exception[]>(
                    callerFailure!.Data[ComposerFixtureCleanupFaultsKey])));
            }
            else
            {
                Assert.Null(injected);
                expiry = Assert.IsType<TimeoutException>(callerFailure);
            }
            Assert.Contains("did not cancel, abandon or short-cut the cleanup", expiry.Message, StringComparison.Ordinal);
            Assert.IsType<TimeoutException>(expiry.InnerException);
            callerOutcome = Assert.IsAssignableFrom<Task>(expiry.Data[ComposerFixtureRetainedCleanupKey]);

            var current = Assert.IsType<Composer>(composer);
            Assert.False(callerOutcome.IsCompleted, "the caller's retained cleanup must still be pending after expiry");
            Assert.False(current.TeardownCompletion.IsCompleted);
            Assert.False(ActorOf(current).Completion.IsCompleted);
            Assert.True(AgentServiceOf(current).IsConnected);
            Assert.Equal(0, Volatile.Read(ref hookRuns));
            Assert.Equal(FixtureModel, AgentServiceOf(current).Model);
            Assert.True(Directory.Exists(stateDir), "expiry must not authorize deletion");
            Assert.Equal("live state", File.ReadAllText(Path.Combine(stateDir!, "sentinel.txt")));

            Volatile.Write(ref diagnosticObserved, true);
            hold.Release();
            var outcome = await Record.ExceptionAsync(() => callerOutcome.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            Assert.True(callerOutcome.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref hookRuns));
            Assert.True(hookAfterDiagnostic, "real agent disposal must run after the caller observes expiry");
            Assert.Equal(FixtureModel, modelAtDisposal);
            Assert.True(ActorOf(current).Completion.IsCompletedSuccessfully);
            Assert.Null(AgentServiceOf(current).Agent);
            Assert.True(IssueLockIsDisposed(current));
            // A faulted retained task is not just the original teardown task or the timeout.
            Assert.True(current.TeardownCompletion.IsCompletedSuccessfully);

            if (lateAgentDisposalFault)
            {
                Assert.NotNull(outcome);
                Assert.True(callerOutcome.IsFaulted);
                var flat = Flattened(outcome).ToArray();
                Assert.Contains(agentDisposalFault, flat);
                Assert.DoesNotContain(flat, fault => fault is TimeoutException);
                Assert.Contains(flat, fault => fault.Message.Contains("was RETAINED", StringComparison.Ordinal));
                Assert.True(Directory.Exists(stateDir));
                Assert.Equal("live state", File.ReadAllText(Path.Combine(stateDir!, "sentinel.txt")));
            }
            else
            {
                Assert.Null(outcome);
                Assert.True(callerOutcome.IsCompletedSuccessfully);
                Assert.False(Directory.Exists(stateDir));
            }
        }, () => composer, () => stateDir,
            // Emergency fallback only, including when the discard-handle mutant prevents the caller from joining.
            () => callerOutcome ?? logger?.RetainedCleanup,
            emergencyDisposalFaults: () => logger?.DeferredFaults.ToArray() ?? []);

        Assert.False(Directory.Exists(stateDir)); // test-owned fallback, not evidence for the fixture's decision
    }

    // ── Fallback deletion after a recovery that cannot be proven ──────────────

    // Test-owned sanitation is not the fallback under test. It runs after all observation,
    // preserves an assertion failure, and leaves unknown or still-owned state alone.
    private static async Task SanitizeFallbackTestAsync(ActorLoopHold hold, ComposerFixtureProbe probe,
        ObservedClientDisposal disposal, Task fixtureTask, Task? fallback, Exception? scenarioFailure,
        Exception? observedFallback, Exception? expectedClientFault, List<Exception> faults,
        SanitationBounds? bounds = null, params Exception[] alsoExpected)
    {
        hold.Release();
        disposal.Release();

        // Every join is attempted even after an earlier bound expired; a bound that WON is recorded and final.
        var boundWon = false;
        async Task JoinAsync(Task work, string operation, Func<Exception, bool>? expected)
        {
            if (!await JoinSanitationWorkAsync(work, operation, bounds, faults, expected))
                boundWon = true;
        }

        var expected = ExpectedSanitationLeaf(() => probe.Cleanup, () => probe.StateDir, expectedClientFault,
            [scenarioFailure, observedFallback, .. alsoExpected]);
        await JoinAsync(fixtureTask, "the fixture task", expected);
        if (fallback is not null)
            await JoinAsync(fallback, "the fallback", expected);

        var cleanup = probe.Cleanup;
        if (cleanup is not null)
            await JoinAsync(cleanup, "the retained cleanup", expected);

        var actorCompletion = probe.Composer is { } composer ? ActorOf(composer).Completion : null;
        if (actorCompletion is not null)
            await JoinAsync(actorCompletion, "the real actor", null);

        // The synthetic failing client explicitly owns a resource. Do not interpret a second
        // Composer.DisposeAsync (whose client reference was cleared) as its disposal. Only
        // sanction separate sanitation once all work and the exact test-owned outcome are known.
        var clientOutcome = disposal.Outcome.Task;
        var safe = !boundWon && fixtureTask.IsCompleted && cleanup is { IsCompleted: true }
            && fallback is { IsCompleted: true } && actorCompletion is { IsCompletedSuccessfully: true }
            && probe.Composer is { } ownedComposer && IssueLockIsDisposed(ownedComposer)
            && AgentServiceOf(ownedComposer).Agent is null
            && clientOutcome is { IsCompletedSuccessfully: true } && disposal.Attempts == 1
            && ReferenceEquals(expectedClientFault, clientOutcome.Result);
        if (!safe)
        {
            if (Directory.Exists(probe.StateDir))
                faults.Add(RetentionDiagnostic(ReportSource.Sanitizer, $"Test sanitation could not establish safety; retained '{probe.StateDir}'."));
            return;
        }

        try
        {
            bounds?.BeforeResourceClose?.Invoke();
            disposal.CloseTestResource();
            Assert.True(disposal.ResourceClosed, "Sanitation must close its own client resource before removing state.");
            if (probe.StateDir is { } dir && Directory.Exists(dir))
            {
                bounds?.BeforeDelete?.Invoke();
                await CopilotHive.Worker.GitOperations.ForceDeleteDirectoryAsync(dir)
                    .WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
            }
        }
        catch (Exception ex) { faults.Add(ex); }
    }

    [Fact]
    public async Task ComposerFixture_FallbackKeepsStateWhenTheOriginalClientDisposalFailedRecovery()
    {
        var hold = new ActorLoopHold();
        var callbackFault = new InvalidOperationException("INJECTED-CANCELLATION-CALLBACK-FAULT");
        var clientFault = new InvalidOperationException("INJECTED-ORIGINAL-CLIENT-DISPOSE-FAULT");
        var disposal = new ObservedClientDisposal(asynchronous: false, failWith: clientFault);
        var callbackRan = false;
        Exception? injected = null;
        var probe = NewCancellationFaultProbe(hold, new ControlledTimeProvider(), new ControlledTimeProvider(),
            callbackFault, () => callbackRan = true, observedClient: disposal);
        var fixtureTask = WithFaultingComposerStatsAsync(_ => FailFaultedFixtureScenario(ex => injected = ex), probe);
        var record = new FallbackDecisionRecord();
        var sanitationFaults = new List<Exception>();
        Task? fallback = null;
        Exception? observedFallback = null;
        ExceptionDispatchInfo? preFallbackAssertion = null;
        ExceptionDispatchInfo? primary = null;
        try
        {
            fallback = WithFallbackCleanupAsync(hold, probe, fixtureTask, () => injected, async () =>
            {
                try
                {
                    await AssertCallbackFaultHeldAsync(probe, hold, callbackFault, () => callbackRan, fixtureTask);
                    Assert.Same(probe.ChatClient!.Object, AgentServiceOf(probe.Composer!).ChatClient);
                    hold.Release();
                    var retainedBeforeFallback = await ObserveFaultedFixtureAsync(fixtureTask, injected, callbackFault, probe);

                    Assert.True(disposal.Attempted.Task.IsCompletedSuccessfully);
                    Assert.Equal(1, disposal.Attempts);
                    Assert.Same(clientFault, await disposal.Outcome.Task.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
                    Assert.Contains(clientFault, retainedBeforeFallback);
                    Assert.Contains(callbackFault, retainedBeforeFallback);
                    Assert.Contains("ComposerAgentService.DisposeClientsAndClearStateAsync", clientFault.StackTrace, StringComparison.Ordinal);
                    Assert.False(disposal.ResourceClosed);
                    Assert.False(record.Reached, "the fallback decision must not have been made yet");
                    Assert.NotNull(probe.RetainedReason);
                    Assert.False(probe.BeforeDeleteEntered.Task.IsCompleted);
                    Assert.True(Directory.Exists(probe.StateDir));
                    AssertSentinelIntact(probe, "before the fallback runs");
                }
                catch (Exception ex)
                {
                    preFallbackAssertion = ExceptionDispatchInfo.Capture(ex);
                    throw;
                }
            }, decisionRecord: record);
            observedFallback = await Record.ExceptionAsync(() => fallback.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            if (!fallback.IsCompleted || !record.Reached)
            {
                if (observedFallback is not null)
                    ExceptionDispatchInfo.Capture(observedFallback).Throw();
            }
            preFallbackAssertion?.Throw(); // even if the fallback reached its decision after an earlier failure

            Assert.True(fallback.IsCompleted, "the fallback must complete before observing its deletion decision");
            Assert.True(record.Reached, "the fallback never reached its deletion decision");
            Assert.True(record.Retried, "the fallback must have performed its (no-op) retry");
            Assert.Equal(1, disposal.Attempts); // the retry never touched the failed client

            AssertSentinelIntact(probe, "after the fallback finished");
            Assert.True(Directory.Exists(probe.StateDir));
            Assert.False(record.DeletionStarted, "the fallback must not have started deletion");
            Assert.False(disposal.ResourceClosed);
            Assert.Contains(record.Reasons, reason => reason.Contains("INJECTED-ORIGINAL-CLIENT-DISPOSE-FAULT", StringComparison.Ordinal));
            var flat = Assert.IsType<AggregateException>(observedFallback).Flatten().InnerExceptions;
            Assert.Contains(clientFault, flat);
            Assert.Contains(flat, fault => fault.Message.Contains("was RETAINED", StringComparison.Ordinal)
                && fault.Message.Contains("INJECTED-ORIGINAL-CLIENT-DISPOSE-FAULT", StringComparison.Ordinal));
        }
        catch (Exception ex) { primary = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            await SanitizeFallbackTestAsync(hold, probe, disposal, fixtureTask, fallback, injected,
                observedFallback, clientFault, sanitationFaults, null, callbackFault);
        }

        if (primary is not null)
        {
            if (observedFallback is not null && !ReferenceEquals(primary.SourceException, observedFallback))
                sanitationFaults.Add(observedFallback);
            if (sanitationFaults.Count > 0)
                primary.SourceException.Data["SanitationFaults"] = sanitationFaults.ToArray();
            primary.Throw();
        }
        if (sanitationFaults.Count > 0)
            throw new AggregateException(sanitationFaults);
        Assert.True(disposal.ResourceClosed);
        Assert.False(Directory.Exists(probe.StateDir)); // test-owned sanitation, not evidence about fallback
    }

    [Fact]
    public async Task ComposerFixture_FallbackDeletesOnlyAfterRealDependentCleanupSucceeds()
    {
        var hold = new ActorLoopHold();
        var callbackFault = new InvalidOperationException("INJECTED-CANCELLATION-CALLBACK-FAULT");
        var disposal = new ObservedClientDisposal(asynchronous: true, held: true);
        var callbackRan = false;
        Exception? injected = null;
        // The owner's recovery does nothing, so the original client is still live at fallback entry.
        var probe = NewCancellationFaultProbe(hold, new ControlledTimeProvider(), new ControlledTimeProvider(),
            callbackFault, () => callbackRan = true, dependentRecovery: _ => Task.CompletedTask, observedClient: disposal);
        var fixtureTask = WithFaultingComposerStatsAsync(_ => FailFaultedFixtureScenario(ex => injected = ex), probe);
        var record = new FallbackDecisionRecord();
        var faults = new List<Exception>();
        Task? fallback = null;
        ExceptionDispatchInfo? primary = null;
        try
        {
            fallback = WithFallbackCleanupAsync(hold, probe, fixtureTask, () => injected, async () =>
            {
                await AssertCallbackFaultHeldAsync(probe, hold, callbackFault, () => callbackRan, fixtureTask);
                Assert.Same(probe.ChatClient!.Object, AgentServiceOf(probe.Composer!).ChatClient);
                hold.Release();
                await ObserveFaultedFixtureAsync(fixtureTask, injected, callbackFault, probe);
                Assert.Equal(0, disposal.Attempts);
                Assert.NotNull(probe.RetainedReason);
                Assert.False(record.Reached);
                AssertSentinelIntact(probe, "before the fallback's real disposal starts");
            }, decisionRecord: record);

            // If a pre-fallback assertion failed, surface it instead of waiting for a milestone
            // that the scenario may never reach. The outer finally owns both holds and both tasks.
            var next = await Task.WhenAny(disposal.Attempted.Task, fallback)
                .WaitAsync(ControlledTimeProvider.HangGuard, Ct);
            if (ReferenceEquals(next, fallback))
                await fallback;
            Assert.Same(disposal.Attempted.Task, next);
            Assert.Equal(1, disposal.Attempts);
            Assert.False(disposal.Outcome.Task.IsCompleted, "client disposal must still be pending");
            Assert.False(disposal.ResourceClosed);
            Assert.False(fallback.IsCompleted, "the fallback must still be waiting on real dependent cleanup");
            Assert.False(record.Reached);
            Assert.False(record.DeletionStarted, "deletion must not start while disposal is pending");
            AssertSentinelIntact(probe, "while the fallback's disposal is pending");

            disposal.Release();
            await fallback.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
            Assert.Null(await disposal.Outcome.Task.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            Assert.True(disposal.ResourceClosed);
            Assert.Equal(1, disposal.Attempts);
            Assert.True(record.Reached);
            Assert.True(record.Retried);
            Assert.Empty(record.Reasons);
            Assert.True(record.ClientDisposalSucceededAtDelete, "deletion began before successful client disposal");
            Assert.True(record.DeletionStarted);
            Assert.False(Directory.Exists(probe.StateDir), "the fallback must delete once cleanup is proven");
        }
        catch (Exception ex) { primary = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            await SanitizeFallbackTestAsync(hold, probe, disposal, fixtureTask, fallback, injected,
                primary?.SourceException, null, faults, null, callbackFault);
        }

        if (primary is not null)
        {
            if (faults.Count > 0)
                primary.SourceException.Data["SanitationFaults"] = faults.ToArray();
            primary.Throw();
        }
        if (faults.Count > 0)
            throw new AggregateException(faults);
    }

    // ── Diagnostic wait decided before it is published ────────────────────────

    // Ordinary invocation: no ComposerFixtureProbe. The helper's own checkpoint (onWaitDecided) holds ONLY the
    // reporting continuation after the diagnostic wait is decided; the actual cleanup task is observed from that
    // checkpoint and then re-obtained solely from the caller's diagnostic.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ComposerFixture_ExpiryWinsThenRealDeferredDisposalFaultsBeforePublication_CallerStillGetsTheCleanupTask(bool withPrimary)
    {
        var hold = new ActorLoopHold();
        var actorClock = new ControlledTimeProvider();
        var diagnosticClock = new ControlledTimeProvider();
        var agentDisposalFault = new InvalidOperationException("INJECTED-AGENT-DISPOSAL-FAULT");
        var decided = new TaskCompletionSource<(bool ExpiryWon, bool CleanupPending, Task Cleanup)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publicationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookRuns = 0;
        var hookRanAfterDecision = false;
        Exception? injected = null;
        Exception? callerFailure = null;
        Composer? composer = null;
        ComposerFixtureLogger? logger = null;
        string? stateDir = null;

        var fixtureTask = WithFaultingComposerStatsAsync(async running =>
        {
            composer = running;
            stateDir = Assert.IsType<string>(typeof(Composer).GetField("_stateDir", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(running));
            logger = Assert.IsType<ComposerFixtureLogger>(typeof(Composer)
                .GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(running));
            File.WriteAllText(Path.Combine(stateDir, "sentinel.txt"), "live state");
            var actor = ActorOf(running);
            typeof(Actor<IComposerMessage>).GetField("_timeProvider", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(actor, actorClock);
            actor.OnBeforeReadAsync = hold.OnBeforeReadAsync;
            Assert.True(actor.Tell(new ComposerCancelStreamingMessage()));
            await hold.Parked.WaitAsync(ControlledTimeProvider.HangGuard, Ct);
            Assert.False(actor.Completion.IsCompleted);
            AgentServiceOf(running).OnAgentDisposing = _ =>
            {
                hookRanAfterDecision = decided.Task.IsCompletedSuccessfully;
                Interlocked.Increment(ref hookRuns);
                throw agentDisposalFault;
            };
            if (withPrimary)
                await FailFaultedFixtureScenario(ex => injected = ex);
        }, diagnosticClock: diagnosticClock, onWaitDecided: async (expiryWon, cleanup) =>
        {
            decided.TrySetResult((expiryWon, !cleanup.IsCompleted, cleanup));
            await publicationGate.Task; // holds ONLY the reporting continuation
        });

        await WithFallbackCleanupAsync(hold, fixtureTask, () => callerFailure ?? injected, async () =>
        {
            await actorClock.WaitForPendingTimerAsync(Actor<IComposerMessage>.DisposeTimeout, Ct);
            await diagnosticClock.WaitForPendingTimerAsync(FixtureDiagnosticDeadline, Ct);
            actorClock.Advance(Actor<IComposerMessage>.DisposeTimeout);
            // Pin the REAL deferred branch: Composer logs this only after choosing it.
            await logger!.DeferredSelected.Task.WaitAsync(ControlledTimeProvider.HangGuard, Ct);
            Assert.False(fixtureTask.IsCompleted);

            // Diagnostic expiry wins while the actual cleanup is pending; publication is then held at the checkpoint.
            diagnosticClock.Advance(FixtureDiagnosticDeadline);
            var (expiryWon, cleanupPending, cleanup) = await decided.Task.WaitAsync(ControlledTimeProvider.HangGuard, Ct);
            Assert.True(expiryWon, "the diagnostic must have won the wait");
            Assert.True(cleanupPending, "the wait decision must snapshot an incomplete cleanup task");
            var current = Assert.IsType<Composer>(composer);
            Assert.True(hold.Parked.IsCompletedSuccessfully);
            Assert.False(ActorOf(current).Completion.IsCompleted);
            Assert.False(cleanup.IsCompleted, "the actual cleanup must still be pending while the actor is held");
            Assert.False(publicationGate.Task.IsCompleted);
            Assert.False(fixtureTask.IsCompleted, "publication must be held at the checkpoint");
            Assert.False(current.TeardownCompletion.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref hookRuns));
            Assert.True(Directory.Exists(stateDir));
            Assert.Equal("live state", File.ReadAllText(Path.Combine(stateDir!, "sentinel.txt")));

            // Let real deferred disposal run and fail while ONLY the diagnostic publication is still held.
            hold.Release();
            var cleanupFault = await Record.ExceptionAsync(() => cleanup.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            Assert.True(cleanup.IsFaulted, "the retained cleanup task must be faulted BEFORE publication resumes");
            Assert.False(publicationGate.Task.IsCompleted);
            Assert.False(fixtureTask.IsCompleted, "publication must still be held after the cleanup faulted");
            Assert.True(ActorOf(current).Completion.IsCompletedSuccessfully);
            Assert.Equal(1, Volatile.Read(ref hookRuns));
            Assert.True(hookRanAfterDecision, "real agent disposal must run after the expiry decision");
            Assert.NotNull(cleanupFault);
            Assert.Contains(agentDisposalFault, Flattened(cleanupFault));
            Assert.Contains(agentDisposalFault, logger.DeferredFaults); // the deferred branch only logged it ...
            Assert.True(current.TeardownCompletion.IsCompletedSuccessfully); // ... TeardownCompletion alone proves nothing
            Assert.True(Directory.Exists(stateDir), "the faulted cleanup must retain the directory");
            Assert.Equal("live state", File.ReadAllText(Path.Combine(stateDir!, "sentinel.txt")));

            // Resume publication and obtain the task ONLY from the caller's diagnostic.
            Assert.True(publicationGate.TrySetResult());
            callerFailure = await Record.ExceptionAsync(() => fixtureTask.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            Assert.True(fixtureTask.IsFaulted, "the fixture, not the test hang guard, must publish the failure");
            Assert.NotNull(callerFailure);
            TimeoutException expiry;
            if (withPrimary)
            {
                Assert.Same(injected, callerFailure);
                Assert.IsType<Xunit.Sdk.FailException>(callerFailure);
                expiry = Assert.IsType<TimeoutException>(Assert.Single(Assert.IsType<Exception[]>(
                    callerFailure.Data[ComposerFixtureCleanupFaultsKey])));
            }
            else
            {
                Assert.Null(injected);
                expiry = Assert.IsType<TimeoutException>(callerFailure);
            }

            Assert.Contains("did not cancel, abandon or short-cut the cleanup", expiry.Message, StringComparison.Ordinal);
            Assert.IsType<TimeoutException>(expiry.InnerException);
            var handle = Assert.IsAssignableFrom<Task>(expiry.Data[ComposerFixtureRetainedCleanupKey]);
            Assert.Same(cleanup, handle);
            Assert.True(handle.IsFaulted, "the handle was published after the cleanup had already faulted");

            var outcome = await Record.ExceptionAsync(() => handle.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            Assert.Same(cleanupFault, outcome);
            var flat = Flattened(outcome!).ToArray();
            Assert.Contains(agentDisposalFault, flat);
            Assert.DoesNotContain(flat, fault => fault is TimeoutException);
            Assert.Contains(flat, fault => fault.Message.Contains("was RETAINED", StringComparison.Ordinal));

            // Before any test-owned sanitation.
            Assert.True(Directory.Exists(stateDir));
            Assert.Equal("live state", File.ReadAllText(Path.Combine(stateDir!, "sentinel.txt")));
        }, () => composer, () => stateDir,
            () => decided.Task.IsCompletedSuccessfully ? decided.Task.Result.Cleanup : logger?.RetainedCleanup,
            releaseClient: () => publicationGate.TrySetResult(),
            emergencyDisposalFaults: () => logger?.DeferredFaults.ToArray() ?? []);

        Assert.False(Directory.Exists(stateDir)); // test-owned fallback, not evidence for the fixture's decision
    }

    /// <summary>
    /// CLEANUP-FIRST CONTROL: the actual disposal throws a GENUINE <see cref="TimeoutException"/> and the cleanup
    /// finishes before any diagnostic deadline. The caller must see that exact fault - not a diagnostic-expiry
    /// wrapper, and no retained-cleanup handle.
    /// </summary>
    [Fact]
    public async Task ComposerFixture_CleanupWinsWithAGenuineTimeoutException_ReportsTheExactCleanupFault()
    {
        var injected = new TimeoutException("INJECTED-GENUINE-CLEANUP-TIMEOUT");
        var diagnosticClock = new ControlledTimeProvider(); // never advanced
        var decided = new TaskCompletionSource<(bool ExpiryWon, Task Cleanup)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? callerFailure = null;
        Composer? composer = null;
        string? stateDir = null;
        var hookRuns = 0;

        var fixtureTask = WithFaultingComposerStatsAsync(running =>
        {
            composer = running;
            stateDir = Assert.IsType<string>(typeof(Composer).GetField("_stateDir", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(running));
            File.WriteAllText(Path.Combine(stateDir, "sentinel.txt"), "live state");
            // Never advance the actor clock either, so a slow scheduler cannot select deferred disposal.
            typeof(Actor<IComposerMessage>).GetField("_timeProvider", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(ActorOf(running), new ControlledTimeProvider());
            AgentServiceOf(running).OnAgentDisposing = _ =>
            {
                Interlocked.Increment(ref hookRuns);
                throw injected;
            };
            return Task.CompletedTask;
        }, diagnosticClock: diagnosticClock, onWaitDecided: (expiryWon, cleanup) =>
        {
            decided.TrySetResult((expiryWon, cleanup));
            return Task.CompletedTask;
        });

        await WithFallbackCleanupAsync(new ActorLoopHold(), fixtureTask, () => callerFailure, async () =>
        {
            callerFailure = await Record.ExceptionAsync(() => fixtureTask.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            Assert.True(fixtureTask.IsFaulted);
            Assert.NotNull(callerFailure);
            var (expiryWon, cleanup) = await decided.Task.WaitAsync(ControlledTimeProvider.HangGuard, Ct);
            Assert.False(expiryWon, "cleanup must win, even though its actual fault is a TimeoutException");
            Assert.True(cleanup.IsFaulted);
            Assert.Same(callerFailure, await Record.ExceptionAsync(() =>
                cleanup.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None)));
            Assert.Equal(1, Volatile.Read(ref hookRuns));
            Assert.Contains("ComposerAgentService.DisposeClientsAndClearStateAsync", injected.StackTrace, StringComparison.Ordinal);
            Assert.Contains("Composer.DisposeAsync", injected.StackTrace, StringComparison.Ordinal);
            Assert.True(ActorOf(composer!).Completion.IsCompletedSuccessfully);
            Assert.True(composer!.TeardownCompletion.IsCompletedSuccessfully);

            var flat = Flattened(callerFailure).ToArray();
            Assert.Same(injected, Assert.Single(flat.OfType<TimeoutException>()));
            Assert.DoesNotContain(flat, fault => fault.Data.Contains(ComposerFixtureRetainedCleanupKey)
                || fault.Message.Contains("diagnostic deadline", StringComparison.Ordinal));
            Assert.False(callerFailure.Data.Contains(ComposerFixtureRetainedCleanupKey));
            Assert.Equal(FixtureDiagnosticDeadline, Assert.Single(diagnosticClock.RequestedDelays));
            Assert.Equal(0, diagnosticClock.PendingTimerCount); // the registered diagnostic delay was released

            // Safe outcome before any test-owned sanitation: the fault retained the directory.
            Assert.True(Directory.Exists(stateDir));
            Assert.Equal("live state", File.ReadAllText(Path.Combine(stateDir!, "sentinel.txt")));
        }, () => composer, () => stateDir,
            () => decided.Task.IsCompletedSuccessfully ? decided.Task.Result.Cleanup : fixtureTask);

        Assert.False(Directory.Exists(stateDir));
    }

    // ── Owner deletion requires the ORIGINAL client's recorded disposal success ─────────────────────────

    // Check owner state within the body, before the fallback decides. Only the final, test-owned
    // sanitation can close the synthetic client's failed resource, after the bounded joins.
    private static async Task RunOwnerRetentionCaseAsync(ActorLoopHold hold, ComposerFixtureProbe probe, Task fixtureTask,
        Func<Exception?> expectedPrimary, Func<Task> ownerAssertions, Exception clientFault, ObservedClientDisposal disposal,
        params Exception[] alsoExpected)
    {
        var record = new FallbackDecisionRecord();
        var sanitationFaults = new List<Exception>();
        Task? fallback = null;
        Exception? fallbackReport = null;
        ExceptionDispatchInfo? ownerFailure = null;
        ExceptionDispatchInfo? primary = null;
        try
        {
            fallback = WithFallbackCleanupAsync(hold, probe, fixtureTask, expectedPrimary, async () =>
            {
                try { await ownerAssertions(); }
                catch (Exception ex) { ownerFailure = ExceptionDispatchInfo.Capture(ex); throw; }
            }, releaseClient: disposal.Release, decisionRecord: record);
            fallbackReport = await Record.ExceptionAsync(() => fallback.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            // The fallback helper rethrows a failing observation after doing its own finally.
            // Do not replace that failure (or a join timeout) with an assertion about the fallback report.
            ownerFailure?.Throw();
            if (fallbackReport is not AggregateException)
            {
                Assert.NotNull(fallbackReport);
                ExceptionDispatchInfo.Capture(fallbackReport).Throw();
            }

            Assert.True(fallback.IsCompleted);
            Assert.True(record.Reached, "the fallback never reached its deletion decision");
            Assert.True(record.Retried, "the fallback must actually retry, not assume the original fault was resolved");
            Assert.Equal(1, disposal.Attempts);
            Assert.False(record.DeletionStarted, "the fallback must not have started deletion");
            AssertSentinelIntact(probe, "after the fallback finished");
            Assert.False(disposal.ResourceClosed, "the original client's resource must still be open");
            Assert.Contains(clientFault, Flattened(fallbackReport));
        }
        catch (Exception ex) { primary = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            await SanitizeFallbackTestAsync(hold, probe, disposal, fixtureTask, fallback, expectedPrimary(),
                fallbackReport, clientFault, sanitationFaults, null, alsoExpected);
        }

        if (primary is not null)
        {
            if (fallbackReport is not null && !ReferenceEquals(primary.SourceException, fallbackReport))
                AddDistinctFault(sanitationFaults, fallbackReport);
            if (sanitationFaults.Count > 0)
                primary.SourceException.Data["SanitationFaults"] = sanitationFaults.ToArray();
            primary.Throw();
        }

        if (sanitationFaults.Count > 0)
            throw new AggregateException(sanitationFaults);
        Assert.True(disposal.ResourceClosed); // test-owned sanitation only, not evidence of owner/fallback disposal
        Assert.False(Directory.Exists(probe.StateDir));
    }

    private static async Task AssertFailedOriginalClientAsync(ComposerFixtureProbe probe, IChatClient? originalClient,
        FixtureClientEvidence evidence, ObservedClientDisposal disposal, Exception clientFault)
    {
        Assert.Null(probe.ClientDisposal);
        Assert.NotNull(originalClient);
        Assert.Same(probe.ChatClient!.Object, originalClient);
        Assert.True(evidence.Created);
        Assert.True(ClientEvidence.TryGetValue(probe.Composer!, out var retainedEvidence));
        Assert.Same(evidence, retainedEvidence);
        Assert.Equal(1, probe.ChatClient.Invocations.Count(call => call.Method.Name == nameof(IDisposable.Dispose)));
        Assert.True(disposal.Attempted.Task.IsCompletedSuccessfully);
        Assert.Equal(1, disposal.Attempts);
        Assert.Same(clientFault, await disposal.Outcome.Task.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
        Assert.True(evidence.Attempted.Task.IsCompletedSuccessfully);
        Assert.Same(clientFault, await evidence.Outcome.Task.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
        Assert.False(disposal.ResourceClosed, "the failed disposal must leave the resource owned by the original client open");
    }

    private static void AssertOwnerRetainedOriginalClient(ComposerFixtureProbe probe, Exception reported, Exception clientFault)
    {
        // Run this check only after proving the actual client fault and the owner's settled decision.
        // Keep it before fallback: a later sanitation result cannot prove what the owner decided.
        AssertSentinelIntact(probe, "after the owner finished, before any fallback");
        Assert.True(Directory.Exists(probe.StateDir));
        Assert.False(probe.BeforeDeleteEntered.Task.IsCompleted, "the owner must not have entered deletion");
        Assert.False(probe.DependentsQuiescentAtDelete);
        Assert.False(probe.ClientDisposalSucceededAtDelete);
        Assert.Contains("the original chat client's disposal faulted", probe.RetainedReason, StringComparison.Ordinal);
        Assert.Contains(clientFault.Message, probe.RetainedReason, StringComparison.Ordinal);
        var attached = Assert.IsType<Exception[]>(reported.Data[ComposerFixtureCleanupFaultsKey]).SelectMany(Flattened).ToArray();
        Assert.Contains(clientFault, attached);
        var report = Assert.Single(attached.OfType<InvalidOperationException>(),
            fault => fault.Message.Contains("was RETAINED", StringComparison.Ordinal));
        Assert.Contains(clientFault.Message, report.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// SETUP RECONNECT (no ClientDisposal observation): setup performs a real reconnect, which disposes the ORIGINAL
    /// client and that disposal throws. Composer then has no client or agent left, its own teardown succeeds and nothing
    /// is thrown by its disposal - the only record of the failure is the original client's evidence. The owner must
    /// still retain the directory.
    /// </summary>
    [Fact]
    public async Task ComposerFixture_SetupReconnectClientDisposalFault_OwnerRetainsDirectoryWithoutAProbeObservation()
    {
        var clientFault = new InvalidOperationException("INJECTED-RECONNECT-CLIENT-DISPOSE-FAULT");
        var disposal = new ObservedClientDisposal(asynchronous: false, failWith: clientFault);
        ExceptionDispatchInfo? reconnect = null;
        FixtureClientEvidence? evidence = null;
        IChatClient? originalClient = null;
        var scenarioRan = false;
        ComposerFixtureProbe? probe = null;
        probe = new ComposerFixtureProbe
        {
            DiagnosticClock = new ControlledTimeProvider(), // never advanced
            AfterConnected = async composer =>
            {
                File.WriteAllText(Path.Combine(probe!.StateDir!, "sentinel.txt"), "live state");
                Assert.Null(probe.ClientDisposal);
                Assert.True(ClientEvidence.TryGetValue(composer, out evidence));
                originalClient = AgentServiceOf(composer).ChatClient;
                Assert.Same(probe.ChatClient!.Object, originalClient);
                Assert.False(evidence.Attempted.Task.IsCompleted);
                evidence.SyncDisposeOverride = disposal.Dispose;
                var failure = await Record.ExceptionAsync(() => composer.ConnectAsync(Ct));
                Assert.NotNull(failure);
                reconnect = ExceptionDispatchInfo.Capture(failure);
                reconnect.Throw();
            },
        };

        var fixtureTask = WithFaultingComposerStatsAsync(_ =>
        {
            scenarioRan = true;
            return Task.CompletedTask;
        }, probe);

        await RunOwnerRetentionCaseAsync(new ActorLoopHold(), probe, fixtureTask, () => reconnect?.SourceException, async () =>
        {
            var reported = await Record.ExceptionAsync(() => fixtureTask.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            Assert.True(fixtureTask.IsFaulted);
            Assert.True(probe.Cleanup!.IsCompleted, "the owner must settle before checking its deletion decision");
            Assert.NotNull(reconnect);
            Assert.Same(reconnect.SourceException, reported);
            Assert.Contains(clientFault, Flattened(reported!));
            Assert.False(scenarioRan);

            Assert.Contains("ComposerAgentService.ConnectAsync", clientFault.StackTrace, StringComparison.Ordinal);
            Assert.Contains("DisposeClientsAndClearStateAsync", clientFault.StackTrace, StringComparison.Ordinal);
            await AssertFailedOriginalClientAsync(probe, originalClient, evidence!, disposal, clientFault);

            var composer = probe.Composer!;
            Assert.Null(probe.DirectDisposeFault);
            Assert.Null(AgentServiceOf(composer).Agent);
            Assert.Null(AgentServiceOf(composer).ChatClient);
            Assert.True(composer.TeardownCompletion.IsCompletedSuccessfully);
            Assert.True(ActorOf(composer).Completion.IsCompletedSuccessfully);
            Assert.True(IssueLockIsDisposed(composer));
            AssertOwnerRetainedOriginalClient(probe, reported!, clientFault);
            Assert.True(probe.Cleanup.IsFaulted);
        }, clientFault, disposal);
    }

    /// <summary>
    /// RECOVERY (no ClientDisposal observation): the delegate catches the real original client's
    /// disposal failure and returns normally. That return cannot stand in for disposal success.
    /// </summary>
    [Fact]
    public async Task ComposerFixture_RecoveryDelegateCatchesRealClientDisposalFault_OwnerRetainsDirectoryWithoutAProbeObservation()
    {
        var hold = new ActorLoopHold();
        var callbackFault = new InvalidOperationException("INJECTED-CANCELLATION-CALLBACK-FAULT");
        var clientFault = new InvalidOperationException("INJECTED-RECOVERY-CLIENT-DISPOSE-FAULT");
        var disposal = new ObservedClientDisposal(asynchronous: false, failWith: clientFault);
        var callbackRan = false;
        Exception? injected = null;
        Exception? recoveryObserved = null;
        Task? recovery = null;
        FixtureClientEvidence? evidence = null;
        async Task RecoverAndCatchAsync(Composer composer)
        {
            try { await composer.DisposeAsync(); }
            catch (Exception ex) { recoveryObserved = ex; }
        }

        var probe = NewCancellationFaultProbe(hold, new ControlledTimeProvider(), new ControlledTimeProvider(),
            callbackFault, () => callbackRan = true, dependentRecovery: composer => recovery = RecoverAndCatchAsync(composer));
        var fixtureTask = WithFaultingComposerStatsAsync(composer =>
        {
            Assert.Null(probe.ClientDisposal);
            Assert.True(ClientEvidence.TryGetValue(composer, out evidence));
            evidence.SyncDisposeOverride = disposal.Dispose;
            return FailFaultedFixtureScenario(ex => injected = ex);
        }, probe);

        await RunOwnerRetentionCaseAsync(hold, probe, fixtureTask, () => injected, async () =>
        {
            await AssertCallbackFaultHeldAsync(probe, hold, callbackFault, () => callbackRan, fixtureTask);
            var originalClient = AgentServiceOf(probe.Composer!).ChatClient;
            Assert.Same(probe.ChatClient!.Object, originalClient);
            Assert.False(evidence!.Attempted.Task.IsCompleted, "the original client must not have been disposed before recovery");
            hold.Release();

            var reported = await Record.ExceptionAsync(() => fixtureTask.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            Assert.True(fixtureTask.IsFaulted);
            Assert.True(probe.Cleanup!.IsCompleted, "the owner must settle before checking its deletion decision");
            Assert.IsType<Xunit.Sdk.FailException>(reported);
            Assert.Same(injected, reported);
            Assert.Same(clientFault, recoveryObserved);
            Assert.True(recovery is { IsCompletedSuccessfully: true }, "the delegate must return normally despite the exact client fault");
            Assert.Contains("DisposeClientsAndClearStateAsync", clientFault.StackTrace, StringComparison.Ordinal);
            await AssertFailedOriginalClientAsync(probe, originalClient, evidence, disposal, clientFault);
            Assert.True(ActorOf(probe.Composer!).Completion.IsCompletedSuccessfully);
            Assert.True(probe.Composer!.TeardownCompletion.IsFaulted);
            Assert.Null(AgentServiceOf(probe.Composer).Agent);
            Assert.True(IssueLockIsDisposed(probe.Composer));

            AssertOwnerRetainedOriginalClient(probe, reported!, clientFault);
            var attached = Assert.IsType<Exception[]>(reported!.Data[ComposerFixtureCleanupFaultsKey]).SelectMany(Flattened).ToArray();
            Assert.Contains(callbackFault, attached);
        }, clientFault, disposal, callbackFault);
    }

    [Fact]
    public async Task ComposerFixture_HeldOriginalClientDisposal_OwnerDeletesOnlyAfterRecordedSuccessWithoutAProbeObservation()
    {
        var disposal = new ObservedClientDisposal(asynchronous: true, held: true);
        var probe = NewDirectDisposalProbe(disposal, observeThroughProbe: false);
        FixtureClientEvidence? evidence = null;
        IChatClient? originalClient = null;
        var fixtureTask = WithFaultingComposerStatsAsync(composer =>
        {
            Assert.Null(probe.ClientDisposal);
            Assert.True(ClientEvidence.TryGetValue(composer, out evidence));
            originalClient = AgentServiceOf(composer).ChatClient;
            Assert.Same(probe.ChatClient!.Object, originalClient);
            return Task.CompletedTask;
        }, probe);

        await WithDirectDisposalCleanupAsync(probe, disposal, fixtureTask, null, () => null, async () =>
        {
            await disposal.Attempted.Task.WaitAsync(ControlledTimeProvider.HangGuard, Ct);
            await ((ControlledTimeProvider)probe.DiagnosticClock).WaitForPendingTimerAsync(probe.DiagnosticDeadline, Ct);
            Assert.Null(probe.ClientDisposal);
            Assert.NotNull(originalClient);
            Assert.Same(probe.ChatClient!.Object, originalClient);
            Assert.True(evidence!.Created);
            Assert.True(evidence.Attempted.Task.IsCompletedSuccessfully);
            Assert.Equal(1, disposal.Attempts);
            Assert.False(disposal.ResourceClosed);
            Assert.False(evidence.Outcome.Task.IsCompleted);
            Assert.False(probe.Cleanup!.IsCompleted);
            Assert.False(probe.BeforeDeleteEntered.Task.IsCompleted);
            AssertSentinelIntact(probe, "while the owner awaits original-client disposal");

            disposal.Release();
            await fixtureTask.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
            Assert.True(probe.Cleanup.IsCompletedSuccessfully);
            Assert.Null(await evidence.Outcome.Task.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            Assert.Null(await disposal.Outcome.Task.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            Assert.True(disposal.ResourceClosed);
            Assert.Equal(1, disposal.Attempts);
            Assert.True(probe.ClientDisposalSucceededAtDelete);
            Assert.True(probe.BeforeDeleteEntered.Task.IsCompletedSuccessfully);
            Assert.Null(probe.RetainedReason);
            Assert.False(Directory.Exists(probe.StateDir));
        });
    }

    // ── Observer failure while the actor is held ──────────────────────────────

    // Test-owned sanitation, not the fallback under test. Only a settled owner and the known
    // client outcome permit removing scratch state; do not delete while timed-out work is live.
    private static async Task SanitizeObserverAsync(Composer? composer, string? dir, ComposerFixtureLogger? logger,
        Task fixtureTask, Task? fallback, Exception? scenarioFailure, Exception? knownFallbackFailure,
        Exception? expectedClientFault, List<Exception> faults, SanitationBounds? bounds = null)
    {
        var actor = composer is null ? null : ActorOf(composer).Completion;
        var teardown = composer?.TeardownCompletion;
        var expected = ExpectedSanitationLeaf(() => logger?.RetainedCleanup, () => dir, expectedClientFault,
            scenarioFailure, knownFallbackFailure);

        // Every join is attempted even after an earlier bound expired; a bound that WON is recorded and final.
        var boundWon = false;
        await JoinAsync(fixtureTask, "the fixture task");
        await JoinAsync(fallback, "the fallback");
        // Look up the actual owner after joining the fixture; it may have been published meanwhile.
        var cleanup = logger?.RetainedCleanup;
        await JoinAsync(cleanup, "the retained cleanup");
        await JoinAsync(actor, "the real actor");
        await JoinAsync(teardown, "the real teardown");

        async Task JoinAsync(Task? work, string operation)
        {
            if (work is not null && !await JoinSanitationWorkAsync(work, operation, bounds, faults, expected))
                boundWon = true;
        }

        if (!Directory.Exists(dir))
            return;

        if (!boundWon && composer is not null && fixtureTask.IsCompleted && fallback is { IsCompleted: true }
            && cleanup is { IsCompleted: true }
            && actor is { IsCompletedSuccessfully: true } && teardown is { IsCompletedSuccessfully: true }
            && IssueLockIsDisposed(composer) && AgentServiceOf(composer).Agent is null
            && ClientEvidence.TryGetValue(composer, out var evidence)
            && evidence.Outcome.Task is { IsCompletedSuccessfully: true } outcome
            && ReferenceEquals(expectedClientFault, outcome.Result))
        {
            // An owned resource is read from the original client's evidence, not supplied by the
            // caller independently. Close it directly and verify closure before entering deletion;
            // neither a cleared reference nor a completed teardown task proves closure.
            try
            {
                if (evidence.OwnedResource is { } ownedResource)
                {
                    bounds?.BeforeResourceClose?.Invoke();
                    ownedResource.CloseTestResource();
                    if (!ownedResource.ResourceClosed)
                    {
                        faults.Add(RetentionDiagnostic(ReportSource.Sanitizer,
                            $"Observer test sanitation could not prove the client resource closed; retained '{dir}'."));
                        return;
                    }
                }

                bounds?.BeforeDelete?.Invoke();
                await CopilotHive.Worker.GitOperations.ForceDeleteDirectoryAsync(dir!)
                    .WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
            }
            catch (Exception ex) { AddDistinctFault(faults, ex); }
        }
        else
        {
            faults.Add(RetentionDiagnostic(ReportSource.Sanitizer, $"Observer test sanitation retained unproven state '{dir}'."));
        }
    }

    private static async Task<(string StateDir, ComposerFixtureLogger Logger)> ArmHeldActorAsync(
        Composer running, ActorLoopHold hold, ControlledTimeProvider actorClock, Func<Task>? afterRelease = null)
    {
        var stateDir = Assert.IsType<string>(typeof(Composer).GetField("_stateDir", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(running));
        var logger = Assert.IsType<ComposerFixtureLogger>(typeof(Composer)
            .GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(running));
        File.WriteAllText(Path.Combine(stateDir, "sentinel.txt"), "live state");
        var actor = ActorOf(running);
        typeof(Actor<IComposerMessage>).GetField("_timeProvider", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(actor, actorClock);
        actor.OnBeforeReadAsync = async () =>
        {
            await hold.OnBeforeReadAsync();
            if (afterRelease is not null)
                await afterRelease();
        };
        Assert.True(actor.Tell(new ComposerCancelStreamingMessage()));
        await hold.Parked.WaitAsync(ControlledTimeProvider.HangGuard, Ct);
        Assert.False(actor.Completion.IsCompleted);
        return (stateDir, logger);
    }

    // No probe: the ordinary helper, with the real synchronous client disposal controlled through the per-Composer
    // evidence (never through a log or a probe). The observer assertion fires while the real actor is held and
    // BEFORE its clock advances, so releasing the hold lets the real actor finish and Composer take the DIRECT branch.
    private static async Task RunObserverFailureCaseAsync(bool withPrimary, Exception? clientFault, bool exposeRetainedOwner = true)
    {
        var hold = new ActorLoopHold();
        var actorClock = new ControlledTimeProvider();
        var diagnosticClock = new ControlledTimeProvider();
        var record = new FallbackDecisionRecord();
        var sanitationFaults = new List<Exception>();
        var clientEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClient = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? injected = null;
        Composer? composer = null;
        string? stateDir = null;
        ComposerFixtureLogger? logger = null;
        bool? directoryExistedAtDisposal = null;
        Xunit.Sdk.FailException? observer = null;
        Task? fallback = null;
        Exception? reported = null;
        Exception? ownerFailureForSanitation = null;

        var fixtureTask = WithFaultingComposerStatsAsync(async running =>
        {
            composer = running;
            (stateDir, logger) = await ArmHeldActorAsync(running, hold, actorClock);
            Assert.True(ClientEvidence.TryGetValue(running, out var evidence));
            evidence.SyncDisposeOverride = () =>
            {
                directoryExistedAtDisposal = Directory.Exists(stateDir);
                if (clientFault is not null)
                    throw clientFault;
                clientEntered.TrySetResult();
                // IDisposable is synchronous; the test observes entry before releasing this gate.
                releaseClient.Task.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None).GetAwaiter().GetResult();
            };
            if (withPrimary)
                await FailFaultedFixtureScenario(ex => injected = ex);
        }, diagnosticClock: diagnosticClock);

        ExceptionDispatchInfo? assertionFailure = null;
        try
        {
            fallback = WithFallbackCleanupAsync(hold, fixtureTask, () => injected, async () =>
            {
                await actorClock.WaitForPendingTimerAsync(Actor<IComposerMessage>.DisposeTimeout, Ct);
                await diagnosticClock.WaitForPendingTimerAsync(FixtureDiagnosticDeadline, Ct);
                var current = Assert.IsType<Composer>(composer);
                Assert.True(ClientEvidence.TryGetValue(current, out var held));
                Assert.True(hold.Parked.IsCompletedSuccessfully);
                Assert.False(ActorOf(current).Completion.IsCompleted, "the real actor must be held");
                Assert.False(current.TeardownCompletion.IsCompleted);
                Assert.False(fixtureTask.IsCompleted);
                Assert.Equal(TimeSpan.Zero, actorClock.Elapsed);
                Assert.Equal(TimeSpan.Zero, diagnosticClock.Elapsed);
                Assert.False(held.Attempted.Task.IsCompleted, "the client must not have been disposed yet");
                Assert.True(Directory.Exists(stateDir));
                Assert.Equal("live state", File.ReadAllText(Path.Combine(stateDir!, "sentinel.txt")));
                try { Assert.Fail("INJECTED-OBSERVER-ASSERTION"); }
                catch (Xunit.Sdk.FailException ex) { observer = ex; throw; }
            }, () => composer, () => stateDir, () => exposeRetainedOwner ? logger?.RetainedCleanup : null, decisionRecord: record);
            if (clientFault is null)
            {
                var reached = await Task.WhenAny(clientEntered.Task, fallback)
                    .WaitAsync(ControlledTimeProvider.HangGuard, Ct);
                if (ReferenceEquals(reached, fallback))
                    await fallback;
                Assert.Same(clientEntered.Task, reached);
                var currentComposer = Assert.IsType<Composer>(composer);
                Assert.True(ActorOf(currentComposer).Completion.IsCompletedSuccessfully);
                Assert.True(ClientEvidence.TryGetValue(currentComposer, out var pendingClient));
                Assert.False(pendingClient.Outcome.Task.IsCompleted, "actual client disposal is still held");
                Assert.False(fallback.IsCompleted);
                Assert.False(record.DeletionStarted);
                Assert.False(currentComposer.TeardownCompletion.IsCompleted);
                Assert.True(Directory.Exists(stateDir));
                Assert.Equal("live state", File.ReadAllText(Path.Combine(stateDir!, "sentinel.txt")));
                releaseClient.TrySetResult();
            }
            reported = await Record.ExceptionAsync(() => fallback.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            Assert.True(fallback.IsCompleted, "the fallback decision must finish, rather than time out in the test");

            // The observer assertion is the reported primary, by reference.
            Assert.NotNull(observer);
            Assert.Same(observer, reported);
            Assert.Equal("INJECTED-OBSERVER-ASSERTION", reported!.Message);
            if (withPrimary)
            {
                Assert.NotNull(injected);
                Assert.NotSame(injected, reported);
            }

            // Fallback release selected actual DIRECT client disposal: no actor or diagnostic expiry, no deferred branch.
            var current = Assert.IsType<Composer>(composer);
            Assert.Equal(TimeSpan.Zero, actorClock.Elapsed);
            Assert.Equal(TimeSpan.Zero, diagnosticClock.Elapsed);
            Assert.False(logger!.DeferredSelected.Task.IsCompleted, "the deferred branch must not have been selected");
            Assert.True(ClientEvidence.TryGetValue(current, out var evidence));
            Assert.True(evidence.Attempted.Task.IsCompletedSuccessfully);
            Assert.True(evidence.Outcome.Task.IsCompletedSuccessfully);
            Assert.True(ActorOf(current).Completion.IsCompletedSuccessfully);
            Assert.True(current.TeardownCompletion.IsCompletedSuccessfully);
            Assert.True(record.Reached, "the fallback decision must have been reached");
            Assert.True(directoryExistedAtDisposal, "the client disposal must run before any deletion");

            var attached = reported.Data["FallbackCleanupFaults"] as Exception[];
            if (clientFault is not null)
            {
                Assert.Same(clientFault, evidence.Outcome.Task.Result);
                Assert.NotNull(attached);
                var secondary = attached.SelectMany(Flattened).ToArray();

                // THE EXACT SECONDARY FAILURE, from the reported observer outcome: the client exception itself AND
                // the cleanup owner's own retention report for it (which only the attached cleanup faults carry).
                Assert.Contains(secondary, fault => ReferenceEquals(clientFault, fault));
                Assert.Contains("ComposerAgentService.DisposeClientsAndClearStateAsync", clientFault.StackTrace, StringComparison.Ordinal);
                Assert.Contains("Composer.DisposeAsync", clientFault.StackTrace, StringComparison.Ordinal);

                var ownerFailure = await Record.ExceptionAsync(() => fixtureTask.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
                Assert.True(fixtureTask.IsFaulted);
                var ownerFaults = withPrimary
                    ? Assert.IsType<Exception[]>(Assert.IsType<Xunit.Sdk.FailException>(ownerFailure).Data[ComposerFixtureCleanupFaultsKey])
                    : Flattened(Assert.IsType<AggregateException>(ownerFailure)).ToArray();
                if (withPrimary)
                    Assert.Same(injected, ownerFailure);
                ownerFailureForSanitation = ownerFailure;
                var report = Assert.Single(ownerFaults.SelectMany(Flattened).OfType<InvalidOperationException>(),
                    fault => fault.Message.Contains("direct dependent disposal faulted", StringComparison.Ordinal));
                Assert.Contains("INJECTED-DIRECT-CLIENT-DISPOSE-FAULT", report.Message, StringComparison.Ordinal);
                Assert.Contains("was RETAINED", report.Message, StringComparison.Ordinal);
                Assert.Contains(secondary, fault => ReferenceEquals(report, fault));

                // Safe outcome through the completed fallback decision, before any test-owned sanitation.
                Assert.False(record.DeletionStarted, "the fallback must not have started deletion");
                Assert.True(Directory.Exists(stateDir));
                Assert.Equal("live state", File.ReadAllText(Path.Combine(stateDir!, "sentinel.txt")));
                if (exposeRetainedOwner)
                    Assert.Contains(record.Reasons, reason => reason.Contains("INJECTED-DIRECT-CLIENT-DISPOSE-FAULT", StringComparison.Ordinal));
                else
                {
                    Assert.False(record.Retried);
                    Assert.Equal(["No retained Composer cleanup was published."], record.Reasons);
                }
            }
            else
            {
                Assert.Null(evidence.Outcome.Task.Result);
                Assert.Null(attached); // no spurious cleanup fault attached to the observer assertion
                Assert.Empty(record.Reasons);
                Assert.True(record.ClientDisposalSucceededAtDelete, "deletion began before the client disposal succeeded");
                Assert.True(record.DeletionStarted);
                Assert.False(Directory.Exists(stateDir), "state is removed only after the actual disposal succeeded");
            }
        }
        catch (Exception ex) { assertionFailure = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            hold.Release();
            releaseClient.TrySetResult();
            await SanitizeObserverAsync(composer, stateDir, logger, fixtureTask, fallback,
                ownerFailureForSanitation ?? injected, reported, clientFault, sanitationFaults);
        }

        if (assertionFailure is not null)
        {
            if (sanitationFaults.Count > 0)
                assertionFailure.SourceException.Data["SanitationFaults"] = sanitationFaults.ToArray();
            assertionFailure.Throw();
        }

        if (sanitationFaults.Count > 0)
            throw new AggregateException(sanitationFaults);
    }

    [Fact]
    public async Task ComposerFixture_ObserverFailureWithScenarioPrimary_KeepsTheDirectClientFaultAsSecondary() =>
        await RunObserverFailureCaseAsync(withPrimary: true,
            new InvalidOperationException("INJECTED-DIRECT-CLIENT-DISPOSE-FAULT"));

    [Fact]
    public async Task ComposerFixture_ObserverFailureWithoutScenarioPrimary_KeepsTheDirectClientFaultAsSecondary() =>
        await RunObserverFailureCaseAsync(withPrimary: false,
            new InvalidOperationException("INJECTED-DIRECT-CLIENT-DISPOSE-FAULT"));

    // Without a second outcome source, only the first join can forward the exact faults attached
    // to the scenario primary. The test still joins the real owner in its independent sanitation.
    [Fact]
    public async Task ComposerFixture_ObserverFailureWithoutRetainedOwnerAccess_ForwardsAttachedFaults() =>
        await RunObserverFailureCaseAsync(withPrimary: true,
            new InvalidOperationException("INJECTED-DIRECT-CLIENT-DISPOSE-FAULT"), exposeRetainedOwner: false);

    [Fact]
    public async Task ComposerFixture_ObserverFailureThenSuccessfulCleanup_ReportsNoSpuriousCleanupFault() =>
        await RunObserverFailureCaseAsync(withPrimary: true, clientFault: null);

    // ── Partial fallback deadline: only the fixture-task bound expires ─────────────────────────────

    private const string FixtureTaskJoin = "the fixture task";
    private const string CleanupJoin = "the retained cleanup";
    private const string ActorJoin = "real actor completion";
    private const string TeardownJoin = "real Composer teardown";

    /// <summary>
    /// Ordinary helper, NO probe. The real actor is held by a second test gate that outlives the fallback's own release.
    /// The observer assertion fires, then ONLY the fallback's first (fixture task) bound expires. After the fallback has
    /// demonstrably entered its second join (the retained cleanup) with the owner still pending, the actor is released,
    /// the real DIRECT disposal of the original client throws inside the second bound, and that exact owner outcome must
    /// reach the observer assertion. Actor and diagnostic clocks are never advanced.
    /// </summary>
    private static async Task RunPartialFallbackDeadlineCaseAsync(bool withPrimary)
    {
        var hold = new ActorLoopHold();
        var actorClock = new ControlledTimeProvider();
        var diagnosticClock = new ControlledTimeProvider();
        var fallbackClock = new ControlledTimeProvider();
        var stillHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientFault = new InvalidOperationException("INJECTED-DIRECT-CLIENT-DISPOSE-FAULT");
        var record = new FallbackDecisionRecord();
        var trace = new FallbackJoinTrace();
        var sanitationFaults = new List<Exception>();
        Exception? injected = null;
        Composer? composer = null;
        string? stateDir = null;
        ComposerFixtureLogger? logger = null;
        IChatClient? originalClient = null;
        bool? directoryExistedAtDisposal = null;
        var attempts = 0;
        Xunit.Sdk.FailException? observer = null;
        Task? fallback = null;
        Exception? reported = null;
        Exception? ownerFailureForSanitation = null;
        ExceptionDispatchInfo? assertionFailure = null;

        var fixtureTask = WithFaultingComposerStatsAsync(async running =>
        {
            composer = running;
            (stateDir, logger) = await ArmHeldActorAsync(running, hold, actorClock, () =>
            {
                stillHeld.TrySetResult();
                return releaseActor.Task;
            });
            originalClient = AgentServiceOf(running).ChatClient;
            Assert.NotNull(originalClient);
            Assert.True(ClientEvidence.TryGetValue(running, out var evidence));
            Assert.True(evidence.Created);
            evidence.SyncDisposeOverride = () =>
            {
                Interlocked.Increment(ref attempts);
                directoryExistedAtDisposal = Directory.Exists(stateDir);
                throw clientFault;
            };
            if (withPrimary)
                await FailFaultedFixtureScenario(ex => injected = ex);
        }, diagnosticClock: diagnosticClock);

        try
        {
            fallback = WithFallbackCleanupAsync(hold, fixtureTask, () => injected, async () =>
            {
                await actorClock.WaitForPendingTimerAsync(Actor<IComposerMessage>.DisposeTimeout, Ct);
                await diagnosticClock.WaitForPendingTimerAsync(FixtureDiagnosticDeadline, Ct);
                try { Assert.Fail("INJECTED-PARTIAL-FALLBACK-OBSERVER"); }
                catch (Xunit.Sdk.FailException ex) { observer = ex; throw; }
            }, () => composer, () => stateDir, () => logger?.RetainedCleanup,
                decisionRecord: record, fallbackClock: fallbackClock, joinTrace: trace);

            // The fallback released ITS gate, not the second one: the real actor is still parked and the owner pending.
            await stillHeld.Task.WaitAsync(ControlledTimeProvider.HangGuard, Ct);
            var current = Assert.IsType<Composer>(composer);
            Assert.True(ClientEvidence.TryGetValue(current, out var evidence));

            // FIRST join: the fixture task, pending. Its bound is the ONLY one that expires.
            var first = await trace.WaitForStartAsync(FixtureTaskJoin);
            Assert.True(first.PendingAtEntry);
            Assert.Same(fixtureTask, first.Work);
            Assert.False(first.Join.IsCompleted);
            Assert.Same(originalClient, AgentServiceOf(current).ChatClient);
            await fallbackClock.WaitForTimerCountAsync(1, Ct);
            Assert.Equal([ControlledTimeProvider.HangGuard], fallbackClock.PendingDueTimes);
            Assert.Equal([FixtureTaskJoin], trace.Order);
            Assert.False(ActorOf(current).Completion.IsCompleted);
            Assert.False(fixtureTask.IsCompleted);
            Assert.False(logger!.CleanupSettled.Task.IsCompleted);
            Assert.False(current.TeardownCompletion.IsCompleted);
            Assert.False(evidence.Attempted.Task.IsCompleted, "the original client must not have been disposed yet");
            fallbackClock.Advance(ControlledTimeProvider.HangGuard);

            // SECOND join: the fallback has called its bounded wait and published that wait's task.
            // Before actor release both the exact retained owner and its actual join must be pending.
            var second = await trace.WaitForStartAsync(CleanupJoin);
            Assert.True(first.Join.IsFaulted);
            var firstExpiry = Assert.IsType<FallbackJoinExpiredException>(Assert.Single(first.Join.Exception!.InnerExceptions));
            Assert.True(second.PendingAtEntry, "the fallback must enter its second join while the owner is still pending");
            var retained = Assert.IsAssignableFrom<Task>(logger.RetainedCleanup);
            Assert.Same(retained, second.Work);
            Assert.False(second.Join.IsCompleted, "the actual second bounded wait must be suspended on its owner");
            await fallbackClock.WaitForTimerCountAsync(2, Ct);
            Assert.Equal([ControlledTimeProvider.HangGuard], fallbackClock.PendingDueTimes);
            Assert.Equal([FixtureTaskJoin, CleanupJoin], trace.Order);
            Assert.False(second.Work.IsCompleted);
            Assert.False(fallback.IsCompleted);
            Assert.False(fixtureTask.IsCompleted);
            Assert.False(ActorOf(current).Completion.IsCompleted);
            Assert.False(current.TeardownCompletion.IsCompleted);
            Assert.False(evidence.Attempted.Task.IsCompleted);
            Assert.False(record.Reached);
            Assert.False(record.Retried);
            Assert.False(record.DeletionStarted);
            Assert.True(Directory.Exists(stateDir));
            Assert.Equal("live state", File.ReadAllText(Path.Combine(stateDir!, "sentinel.txt")));

            // Release the REAL actor inside the second bound: Composer takes the DIRECT branch and the original
            // client's real disposal throws.
            releaseActor.TrySetResult();
            reported = await Record.ExceptionAsync(() => fallback.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            Assert.True(fallback.IsCompleted, "the fallback decision must finish, rather than time out in the test");

            // The observer assertion is the reported primary, by reference, with the FIRST deadline diagnostic.
            Assert.NotNull(observer);
            Assert.Same(observer, reported);
            Assert.Equal("INJECTED-PARTIAL-FALLBACK-OBSERVER", reported!.Message);
            if (withPrimary)
            {
                Assert.NotNull(injected);
                Assert.NotSame(injected, reported);
            }

            var faults = Assert.IsType<Exception[]>(reported.Data["FallbackCleanupFaults"]);
            var secondary = faults.SelectMany(Flattened).ToArray();
            var expiry = Assert.Single(secondary.OfType<FallbackJoinExpiredException>()); // no later bound expired
            Assert.Same(firstExpiry, expiry);
            Assert.Equal("The bounded fallback wait for the fixture task expired.", expiry.Message);

            // Every remaining join was attempted, in order; the expired first wait authorised no retry and no deletion.
            Assert.Equal([FixtureTaskJoin, CleanupJoin, ActorJoin, TeardownJoin], trace.Order);
            var actorJoin = await trace.WaitForStartAsync(ActorJoin);
            var teardownJoin = await trace.WaitForStartAsync(TeardownJoin);
            Assert.Same(ActorOf(current).Completion, actorJoin.Work);
            Assert.Same(current.TeardownCompletion, teardownJoin.Work);
            Assert.True(actorJoin.Join.IsCompletedSuccessfully);
            Assert.True(teardownJoin.Join.IsCompletedSuccessfully);
            Assert.Equal(2, fallbackClock.TimersCreated);
            Assert.Equal(0, fallbackClock.PendingTimerCount);
            Assert.Equal(ControlledTimeProvider.HangGuard, fallbackClock.Elapsed);
            Assert.True(record.Reached);
            Assert.Equal([expiry.Message], record.Reasons);
            Assert.False(record.Retried);
            Assert.False(record.DeletionStarted);

            // The DIRECT disposal path, with the actor and diagnostic clocks never advanced.
            Assert.Equal(TimeSpan.Zero, actorClock.Elapsed);
            Assert.Equal(TimeSpan.Zero, diagnosticClock.Elapsed);
            Assert.False(logger.DeferredSelected.Task.IsCompleted, "the deferred branch must not have been selected");
            Assert.True(evidence.Attempted.Task.IsCompletedSuccessfully);
            Assert.Equal(1, Volatile.Read(ref attempts));
            Assert.Same(clientFault, await evidence.Outcome.Task.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            Assert.Contains("ComposerAgentService.DisposeClientsAndClearStateAsync", clientFault.StackTrace, StringComparison.Ordinal);
            Assert.Contains("Composer.DisposeAsync", clientFault.StackTrace, StringComparison.Ordinal);
            Assert.True(directoryExistedAtDisposal, "the client disposal must run before any deletion");
            Assert.True(ActorOf(current).Completion.IsCompletedSuccessfully);
            Assert.True(current.TeardownCompletion.IsCompletedSuccessfully);

            // The owner's ACTUAL outcome: the retained task's own exception, and the retention diagnostic taken from IT.
            Assert.True(retained.IsFaulted);
            Assert.True(second.Join.IsFaulted, "the actual second bounded join must expose the owner's fault, not only settlement");
            var ownerLeaves = Flattened(retained.Exception!).ToArray();
            Assert.Contains(ownerLeaves, leaf => ReferenceEquals(clientFault, leaf));
            var diagnostic = Assert.Single(ownerLeaves, leaf => !ReferenceEquals(clientFault, leaf));
            Assert.Contains("was RETAINED", diagnostic.Message, StringComparison.Ordinal);

            // The scenario primary (where there is one) and its attached outcome are preserved.
            var ownerFailure = await Record.ExceptionAsync(() => fixtureTask.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            Assert.True(fixtureTask.IsFaulted);
            ownerFailureForSanitation = ownerFailure;
            Exception[] ownerReported;
            if (withPrimary)
            {
                Assert.Same(injected, ownerFailure);
                ownerReported = Assert.IsType<Exception[]>(ownerFailure!.Data[ComposerFixtureCleanupFaultsKey]);
            }
            else
            {
                ownerReported = Flattened(Assert.IsType<AggregateException>(ownerFailure)).ToArray();
            }

            var ownerReportedLeaves = ownerReported.SelectMany(Flattened).ToArray();
            Assert.Contains(ownerReportedLeaves, leaf => ReferenceEquals(clientFault, leaf));
            Assert.Contains(ownerReportedLeaves, leaf => ReferenceEquals(diagnostic, leaf));

            // The owner's two exact leaves must travel through the second wait, not be synthesized
            // by the fallback or taken from the scenario primary's data.
            var joinedLeaves = Flattened(second.Join.Exception!).ToArray();
            Assert.Contains(joinedLeaves, leaf => ReferenceEquals(clientFault, leaf));
            Assert.Contains(joinedLeaves, leaf => ReferenceEquals(diagnostic, leaf));

            // Before any test-owned sanitation.
            Assert.True(Directory.Exists(stateDir));
            Assert.Equal("live state", File.ReadAllText(Path.Combine(stateDir!, "sentinel.txt")));
            Assert.True(secondary.Any(fault => ReferenceEquals(diagnostic, fault)),
                "PRESERVED-OWNER-FAULT: the observer assertion lost the cleanup owner's exact retention diagnostic");
            Assert.True(secondary.Any(fault => ReferenceEquals(clientFault, fault)),
                "PRESERVED-OWNER-FAULT: the observer assertion lost the original client's disposal exception");
            Assert.Equal(3, secondary.Length); // precisely the first expiry and the owner's two fault objects
        }
        catch (Exception ex) { assertionFailure = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            hold.Release();
            releaseActor.TrySetResult();
            await SanitizeObserverAsync(composer, stateDir, logger, fixtureTask, fallback,
                ownerFailureForSanitation ?? injected, reported, clientFault, sanitationFaults);
        }

        if (assertionFailure is not null)
        {
            if (sanitationFaults.Count > 0)
                assertionFailure.SourceException.Data["SanitationFaults"] = sanitationFaults.ToArray();
            assertionFailure.Throw();
        }

        if (sanitationFaults.Count > 0)
            throw new AggregateException(sanitationFaults);
    }

    [Fact]
    public async Task Review_PartialFallbackDeadlinePreservesJoinedOwnerFault_WithScenarioPrimary() =>
        await RunPartialFallbackDeadlineCaseAsync(withPrimary: true);

    [Fact]
    public async Task Review_PartialFallbackDeadlinePreservesJoinedOwnerFault_WithoutScenarioPrimary() =>
        await RunPartialFallbackDeadlineCaseAsync(withPrimary: false);

    // ── Sanitation: a bound that WON is final, whatever the work does afterwards ─────────────────────────

    // Test-owned scratch removal, run only after the assertions and an independently proven settled owner.
    private static async Task SanitizeAsync(string? stateDir, List<Exception> faults)
    {
        try
        {
            if (stateDir is not null && Directory.Exists(stateDir))
                await CopilotHive.Worker.GitOperations.ForceDeleteDirectoryAsync(stateDir)
                    .WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
        }
        catch (Exception ex) { faults.Add(ex); }
    }

    private enum SanitationRoute { FallbackTest, Observer, DirectDisposal }

    private static readonly string[] SanitationFixtureTaskOperation = ["the fixture task"];

    /// <summary>How the EMERGENCY (test-owned, after the assertions) driver join is exercised.</summary>
    private enum EmergencyDriver
    {
        /// <summary>The driver has finished long before; the emergency joins are the ordinary ones.</summary>
        None,

        /// <summary>The real driver is held; only the emergency driver deadline expires and the driver stays pending.</summary>
        HeldThroughExpiry,

        /// <summary>The emergency driver deadline expires, then the driver is released BEFORE the decision continuation resumes.</summary>
        ReleasedAtDecision,

        /// <summary>CONTROL: the real driver is released and joins inside its emergency bound.</summary>
        JoinsInBound,
    }

    private const string EmergencyDriverJoin = "the sanitation driver";
    private const string OuterWatcherJoin = "the emergency watcher";

    /// <summary>
    /// How the real outer CONTROLLER (the task that advances the outer watcher deadline) is joined by each RESCUE attempt.
    /// Every held mode parks the controller on its own gate AFTER it advanced the outer deadline, so a rescue attempt's own
    /// bounded controller join is genuinely pending. Always combined with OuterWatcher.HeldPending.
    /// </summary>
    private enum RescueController
    {
        /// <summary>The controller finished long ago; the controller join is the ordinary real-time one.</summary>
        None,

        /// <summary>The first rescue's controller deadline wins; the controller is released only after its checks.</summary>
        HeldAtFirstRescue,

        /// <summary>BOTH the first and the second rescue controller deadlines win (the controller stays parked across them).</summary>
        HeldAtSecondRescue,

        /// <summary>The first rescue's deadline wins, then the controller settles BEFORE the decision continuation resumes.</summary>
        SettlesBeforeDecisionResumes,

        /// <summary>CONTROL: the controller completes inside the first rescue's fresh bound.</summary>
        JoinsInBound,
    }

    /// <summary>
    /// How the real emergency WATCHER (the task that fires the earlier emergency driver deadline) is joined by the FRESH outer
    /// cleanup attempt. Every mode uses an emergency expiry first (EmergencyDriver.HeldThroughExpiry).
    /// </summary>
    private enum OuterWatcher
    {
        /// <summary>The watcher is joined by the ordinary real-time bound (it has long finished).</summary>
        None,

        /// <summary>The watcher stays held; only the OUTER watcher deadline fires and the watcher is still pending afterwards.</summary>
        HeldPending,

        /// <summary>The outer watcher deadline wins, then the watcher completes BEFORE the decision continuation resumes.</summary>
        CompletesBeforeDecisionResumes,

        /// <summary>CONTROL: the watcher completes inside the fresh outer bound.</summary>
        CompletesInBound,
    }

    /// <summary>
    /// One scenario for all three test-owned sanitation entry routes. The real Composer actor is held, so the owner
    /// (fixture task + retained cleanup) is genuinely pending; the original client's disposal will throw, so the owner
    /// itself RETAINS the directory and the sanitizer is the only thing that could delete it. The sanitizer's bounded
    /// joins run on a manual clock. <paramref name="boundExpires"/>:
    /// <list type="bullet">
    /// <item>true: the FIRST required join's bound wins while its work is pending. The decision continuation (the
    /// <see cref="SanitationBounds.Decided"/> seam, a real callback inside the sanitizer) is held while the real owner is
    /// released and settles, so the sanitizer resumes with every other check satisfied. The bound's win must stay final.</item>
    /// <item>false: CONTROL. The owner settles inside the bound; deletion may start only after proven quiescence.</item>
    /// </list>
    /// </summary>
    private static async Task RunSanitationRouteAsync(SanitationRoute route, bool boundExpires,
        bool suppressFirstResourceClose = false, EmergencyDriver emergency = EmergencyDriver.None,
        Exception? extraDisposalFault = null, bool observerFails = true, bool declareExtraToEmergency = true,
        bool useLookalikeOwnerDiagnostic = false, OuterWatcher outerWatcher = OuterWatcher.None, Action? beforeUnprovenRescue = null,
        RescueController rescueController = RescueController.None)
    {
        var realHold = new ActorLoopHold();
        var unrelatedHold = new ActorLoopHold(); // the sanitizer's own release; it must not free the REAL actor
        var actorClock = new ControlledTimeProvider();
        var diagnosticClock = new ControlledTimeProvider();
        var sanitationClock = new ControlledTimeProvider();
        var clientFault = new InvalidOperationException("INJECTED-SANITATION-CLIENT-DISPOSE-FAULT");
        var disposal = new ObservedClientDisposal(asynchronous: false, failWith: clientFault,
            suppressFirstTestResourceClose: suppressFirstResourceClose);
        var probe = NewHeldComposerProbe(realHold, actorClock, diagnosticClock, _ => { }, observedClient: disposal);

        Exception? injected = null;
        Xunit.Sdk.FailException? observer = null;
        ExceptionDispatchInfo? hookFailure = null;
        ExceptionDispatchInfo? driverFailure = null;
        Exception? lateOwnerFailure = null;
        var decisions = new List<(string Operation, bool Expired, bool PendingAfterDecision)>();
        var sanitationDeadlines = new List<(string Operation, SanitationJoinExpiredException Fault)>();
        var emergencyDeadlines = new List<(string Operation, SanitationJoinExpiredException Fault)>();
        var outerDeadlines = new List<(string Operation, SanitationJoinExpiredException Fault)>();
        var deleteSnapshots = new List<bool>();
        var openBeforeClose = 0;
        var closedAtDelete = new List<bool>();
        var eligibleAtDecision = false;
        var pendingWhenDriven = false;
        var primaryHandled = false;

        // EMERGENCY (test-owned, post-assertion) path state. The driver is held on its own gate AFTER it advanced the
        // sanitation clock; the emergency driver deadline has its own manual clock so only IT can expire.
        var driverGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driverHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var emergencyClock = new ControlledTimeProvider();
        var emergencyDriverStarted = new TaskCompletionSource<(Task Work, Task<bool> Join)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var emergencyDecisions = new List<(string Operation, bool Expired, bool PendingAtDecision)>();
        var emergencyJoins = new List<(string Operation, Task Work, bool InBound)>();
        var outerJoins = new List<(string Operation, Task Work, bool InBound)>();
        var emergencyOpenAtClose = new List<bool>();
        var emergencyClosedAtDelete = new List<bool>();

        // FRESH OUTER attempt state: the watcher is held on its own gate after it fires the emergency deadline, and the outer
        // watcher join has its own manual clock so only IT can expire.
        var watcherGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcherFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outerClock = new ControlledTimeProvider();
        var outerWatcherStarted = new TaskCompletionSource<(Task Work, Task<bool> Join)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outerDecisions = new List<(string Operation, bool Expired, bool PendingAtDecision)>();
        var rescueJoins = new List<(string Operation, Task Work, bool InBound)>();
        var outerOpenAtClose = new List<bool>();
        var outerClosedAtDelete = new List<bool>();
        var outerCloseEntries = 0;
        var outerDeleteEntries = 0;
        var watcherCompletedBeforeResume = false;
        var joinParkedAtResume = false;
        ExceptionDispatchInfo? outerHookFailure = null;
        ExceptionDispatchInfo? outerControllerFailure = null;

        // RESCUE attempts: each has its own bounded CONTROLLER join on its own manual clock. The controller parks on its own
        // gate after it advanced the outer deadline, so the join is genuinely pending on that exact task.
        var controllerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controllerAdvanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controllerStartedByJoin = new ConcurrentDictionary<string, TaskCompletionSource<(Task Work, Task<bool> Join)>>();
        TaskCompletionSource<(Task Work, Task<bool> Join)> ControllerStarted(string operation) =>
            controllerStartedByJoin.GetOrAdd(operation, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        var controllerJoins = new List<(string Operation, Task Work, bool InBound)>();
        var rescueDeadlines = new List<(string Operation, SanitationJoinExpiredException Fault)>();
        var rescueAttached = new List<Exception>();
        var rescueDrivers = new List<Task>();
        ExceptionDispatchInfo? rescueHookFailure = null;
        ExceptionDispatchInfo? rescueDriverFailure = null;
        var controllerCompletedBeforeResume = false;
        var rescueJoinParkedAtResume = false;
        ExceptionDispatchInfo? emergencyHookFailure = null;
        ExceptionDispatchInfo? emergencyWatcherFailure = null;

        var extraHookRuns = 0;
        var directOwnReports = new List<Exception>();
        var fixtureTask = WithFaultingComposerStatsAsync(running =>
        {
            if (useLookalikeOwnerDiagnostic)
            {
                // Despite the exact owner form and directory, this is an independent fault thrown
                // by the real agent hook. It must never be classified as a retention diagnostic.
                extraDisposalFault = new InvalidOperationException(
                    $"Composer disposal could not be shown quiescent ({clientFault.GetType().Name}: {clientFault.Message}); "
                    + $"the state directory '{probe.StateDir}' was RETAINED, not deleted.");
            }
            if (extraDisposalFault is not null)
            {
                // ONE additional, unexpected fault thrown by the REAL ComposerAgentService agent-disposal hook.
                AgentServiceOf(running).OnAgentDisposing = _ =>
                {
                    Interlocked.Increment(ref extraHookRuns);
                    throw extraDisposalFault;
                };
            }

            return FailFaultedFixtureScenario(ex => injected = ex);
        }, probe);

        bool QuiescentAndEligible()
        {
            var composer = probe.Composer!;
            var logger = Assert.IsType<ComposerFixtureLogger>(typeof(Composer)
                .GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(composer));
            return fixtureTask.IsCompleted && probe.Cleanup is { IsCompleted: true }
                && logger.CleanupSettled.Task.IsCompletedSuccessfully
                && ActorOf(composer).Completion.IsCompletedSuccessfully
                && composer.TeardownCompletion.IsCompletedSuccessfully
                && IssueLockIsDisposed(composer) && AgentServiceOf(composer).Agent is null
                && ClientEvidence.TryGetValue(composer, out var evidence)
                && evidence.Outcome.Task is { IsCompletedSuccessfully: true } outcome
                && ReferenceEquals(outcome.Result, clientFault) && disposal.Attempts == 1;
        }

        async Task ReleaseAndSettleOwnerAsync()
        {
            realHold.Release();
            lateOwnerFailure = await Record.ExceptionAsync(() => fixtureTask.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            await Record.ExceptionAsync(() => probe.Cleanup!.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
        }

        var bounds = new SanitationBounds
        {
            Clock = sanitationClock,
            OwnRetentionReport = directOwnReports.Add,
            OwnDeadline = (operation, fault) => sanitationDeadlines.Add((operation, fault)),
            // The test-owned resource is still OPEN after the one real, failed disposal attempt, and nothing has retried it.
            BeforeResourceClose = () =>
            {
                try
                {
                    Assert.True(ClientEvidence.TryGetValue(probe.Composer!, out var originalClient));
                    Assert.Same(disposal, originalClient.OwnedResource);
                    Assert.True(originalClient.Attempted.Task.IsCompletedSuccessfully);
                    Assert.True(originalClient.Outcome.Task.IsCompletedSuccessfully);
                    Assert.Same(clientFault, originalClient.Outcome.Task.Result);
                    Assert.Equal(1, disposal.Attempts);
                    Assert.True(disposal.Outcome.Task.IsCompletedSuccessfully);
                    Assert.Same(clientFault, disposal.Outcome.Task.Result);
                    Assert.False(disposal.ResourceClosed, "the failed original-client disposal must have left its resource OPEN");
                    AssertSentinelIntact(probe, "before the test-owned resource is closed");
                    Interlocked.Increment(ref openBeforeClose);
                }
                catch (Exception ex) { hookFailure ??= ExceptionDispatchInfo.Capture(ex); }
            },

            // The ACTUAL deletion checkpoint: the same resource is CLOSED, the client was neither retried nor re-recorded,
            // and the original sentinel is still present. Recorded here (never thrown into the sanitizer's own catch).
            BeforeDelete = () =>
            {
                deleteSnapshots.Add(QuiescentAndEligible() && probe.Cleanup!.IsFaulted
                    && File.Exists(Path.Combine(probe.StateDir!, "sentinel.txt")));
                try
                {
                    Assert.True(disposal.ResourceClosed, "CLOSURE-AT-DELETE: the test-owned client resource must be closed when deletion is entered");
                    Assert.Equal(1, disposal.Attempts);
                    Assert.Same(clientFault, disposal.Outcome.Task.Result);
                    AssertSentinelIntact(probe, "when deletion is entered");
                    closedAtDelete.Add(true);
                }
                catch (Exception ex)
                {
                    closedAtDelete.Add(false);
                    hookFailure ??= ExceptionDispatchInfo.Capture(ex);
                }
            },
            Decided = async (operation, expired, pendingAfterDecision) =>
            {
                decisions.Add((operation, expired, pendingAfterDecision));
                if (!boundExpires || decisions.Count != 1)
                    return;

                // The bound WON while the required join's real work was pending - held right here, in the decision.
                try
                {
                    Assert.Equal(SanitationFixtureTaskOperation[0], operation);
                    Assert.True(expired, "the first required bound must have won");
                    Assert.True(pendingAfterDecision, "the required join's work must be pending when the bound wins");
                    Assert.False(fixtureTask.IsCompleted);
                    Assert.False(probe.Cleanup!.IsCompleted);
                    Assert.False(ActorOf(probe.Composer!).Completion.IsCompleted);
                    Assert.False(probe.Composer!.TeardownCompletion.IsCompleted);
                    Assert.Equal(1, sanitationClock.TimersCreated);
                    AssertSentinelIntact(probe, "when the sanitation bound wins");
                }
                catch (Exception ex) { hookFailure = ExceptionDispatchInfo.Capture(ex); }
                finally
                {
                    // Settle the REAL owner BEFORE the decision continuation resumes: from here every other safety check
                    // is satisfied, so only the recorded bound can still stop deletion.
                    await ReleaseAndSettleOwnerAsync();
                }

                try
                {
                    Assert.True(fixtureTask.IsCompleted);
                    eligibleAtDecision = QuiescentAndEligible();
                    Assert.True(eligibleAtDecision, "the late-settled owner must otherwise be eligible for sanitation deletion");
                    Assert.True(probe.Cleanup!.IsFaulted, "the owner itself must have retained the directory");
                    AssertSentinelIntact(probe, "when the decision continuation resumes");
                }
                catch (Exception ex) { hookFailure ??= ExceptionDispatchInfo.Capture(ex); }
            },
        };

        // Drives the manual sanitation clock from a real callback: waits for the sanitizer's first join to register its
        // bound (proving it is pending), then either expires that bound or - control - settles the owner inside it.
        var driver = Task.Run(async () =>
        {
            try
            {
                await sanitationClock.WaitForTimerCountAsync(1, Ct);
                pendingWhenDriven = !fixtureTask.IsCompleted && !ActorOf(probe.Composer!).Completion.IsCompleted;
                Assert.True(pendingWhenDriven, "the first required join must be pending when its bound is registered");
                if (boundExpires)
                    sanitationClock.Advance(ControlledTimeProvider.HangGuard);
                else
                    await ReleaseAndSettleOwnerAsync();

                if (emergency != EmergencyDriver.None)
                {
                    driverHeld.TrySetResult(); // the driver has finished ALL its clock work and is now held
                    await driverGate.Task;
                }
            }
            catch (Exception ex)
            {
                driverFailure = ExceptionDispatchInfo.Capture(ex);
                realHold.Release();
                sanitationClock.Advance(ControlledTimeProvider.HangGuard); // never leave the sanitizer parked
                driverHeld.TrySetResult();
            }
        }, CancellationToken.None);

        // Emergency hooks record the actual decision and deletion boundaries.
        async Task EmergencyDecidedAsync(string operation, bool expired, bool pendingAtDecision)
        {
            emergencyDecisions.Add((operation, expired, pendingAtDecision));
            if (operation != EmergencyDriverJoin || emergency != EmergencyDriver.ReleasedAtDecision)
                return;

            // PARKED decision continuation: the deadline has already won. Release the driver and let it finish BEFORE
            // the sanitizer resumes - a later completion must not authorize closure or deletion.
            try
            {
                Assert.True(expired, "the emergency driver deadline must have won");
                Assert.True(pendingAtDecision, "the driver must be pending when its emergency deadline wins");
                Assert.False(driver.IsCompleted);
                Assert.False(disposal.ResourceClosed);
                AssertSentinelIntact(probe, "when the emergency driver deadline wins");
            }
            catch (Exception ex) { emergencyHookFailure ??= ExceptionDispatchInfo.Capture(ex); }

            driverGate.TrySetResult();
            try
            {
                await driver.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
                Assert.True(driver.IsCompletedSuccessfully, "the driver must have completed before the decision resumes");
                var (_, join) = await emergencyDriverStarted.Task.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
                Assert.False(join.IsCompleted, "the join decision continuation must still be parked after the driver settled");
            }
            catch (Exception ex) { emergencyHookFailure ??= ExceptionDispatchInfo.Capture(ex); }
        }

        void EmergencyBeforeClose()
        {
            if (emergency == EmergencyDriver.None)
                return;
            try
            {
                var (_, join) = emergencyDriverStarted.Task.Result;
                AssertSentinelIntact(probe, "at emergency resource closure");
                emergencyOpenAtClose.Add(!disposal.ResourceClosed && driver.IsCompletedSuccessfully
                    && join is { IsCompletedSuccessfully: true, Result: true });
            }
            catch (Exception ex) { emergencyHookFailure ??= ExceptionDispatchInfo.Capture(ex); }
        }

        void EmergencyBeforeDelete()
        {
            if (emergency == EmergencyDriver.None)
                return;
            try
            {
                var (_, join) = emergencyDriverStarted.Task.Result;
                AssertSentinelIntact(probe, "at emergency deletion entry");
                emergencyClosedAtDelete.Add(disposal.ResourceClosed && driver.IsCompletedSuccessfully
                    && join is { IsCompletedSuccessfully: true, Result: true });
            }
            catch (Exception ex) { emergencyHookFailure ??= ExceptionDispatchInfo.Capture(ex); }
        }

        // Only the DRIVER join runs on the manual emergency clock; every other emergency join keeps real bounded time,
        // so a failed assertion can never leave a join parked on a clock nobody advances.
        var emergencyDriverBounds = new SanitationBounds
        {
            Clock = emergency == EmergencyDriver.None ? TimeProvider.System : emergencyClock,
            Decided = EmergencyDecidedAsync,
            OwnDeadline = (operation, fault) => emergencyDeadlines.Add((operation, fault)),
            BeforeResourceClose = EmergencyBeforeClose, BeforeDelete = EmergencyBeforeDelete,
        };
        var emergencyJoinBounds = new SanitationBounds
        {
            Decided = EmergencyDecidedAsync,
            BeforeResourceClose = EmergencyBeforeClose, BeforeDelete = EmergencyBeforeDelete,
        };

        // Observes the REAL emergency join pending on the driver (entered, with the driver's own task, and its bound
        // registered on the emergency clock), then expires ONLY that deadline - or, for the control, settles the driver in bound.
        var emergencyWatcher = emergency == EmergencyDriver.None ? Task.CompletedTask : Task.Run(async () =>
        {
            try
            {
                var (work, join) = await emergencyDriverStarted.Task.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
                Assert.Same(driver, work);
                Assert.True(driverHeld.Task.IsCompletedSuccessfully);
                Assert.False(driver.IsCompleted);
                Assert.False(join.IsCompleted, "the actual bounded join must be pending on the held driver");
                Assert.Equal(1, emergencyClock.TimersCreated);
                Assert.Equal([ControlledTimeProvider.HangGuard], emergencyClock.PendingDueTimes);
                if (emergency == EmergencyDriver.JoinsInBound)
                {
                    driverGate.TrySetResult();
                    await driver.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
                }
            }
            catch (Exception ex) { emergencyWatcherFailure = ExceptionDispatchInfo.Capture(ex); }
            finally
            {
                if (emergency == EmergencyDriver.JoinsInBound || emergencyWatcherFailure is not null)
                    driverGate.TrySetResult(); // never leave the driver (and so its join) parked
                if (emergency != EmergencyDriver.JoinsInBound)
                    emergencyClock.Advance(ControlledTimeProvider.HangGuard); // expire ONLY the emergency driver deadline
                watcherFired.TrySetResult();
            }

            // The REAL watcher stays pending after it fired the earlier deadline until its own gate is released (always, in the
            // test's finally), so the fresh outer attempt can join it while it is genuinely pending.
            if (outerWatcher != OuterWatcher.None)
                await watcherGate.Task;
        }, CancellationToken.None);

        // ---- the fresh OUTER attempt's watcher bound, its decision hooks and its controller -------------------------------
        async Task OuterDecidedAsync(string operation, bool expired, bool pendingAtDecision)
        {
            outerDecisions.Add((operation, expired, pendingAtDecision));
            if (operation != OuterWatcherJoin)
                return;
            try
            {
                var (_, join) = await outerWatcherStarted.Task.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
                if (outerWatcher == OuterWatcher.CompletesInBound)
                {
                    Assert.False(expired, "the watcher must join inside its fresh outer bound");
                    return;
                }

                Assert.True(expired, "the outer watcher deadline must have won");
                Assert.True(pendingAtDecision, "the real watcher must be pending when its outer deadline wins");
                Assert.False(disposal.ResourceClosed);
                AssertSentinelIntact(probe, "when the outer watcher deadline wins");
                if (outerWatcher == OuterWatcher.CompletesBeforeDecisionResumes)
                {
                    // PARKED decision continuation: the deadline has already won. The real watcher now completes BEFORE the
                    // sanitizer resumes - a later completion must not authorize closure or deletion.
                    watcherGate.TrySetResult();
                    await emergencyWatcher.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
                    watcherCompletedBeforeResume = emergencyWatcher.IsCompletedSuccessfully;
                    joinParkedAtResume = !join.IsCompleted;
                    Assert.True(watcherCompletedBeforeResume, "the watcher must have completed before the decision resumes");
                    Assert.True(joinParkedAtResume, "the join's decision continuation must still be parked after the watcher completed");
                }
                else
                    Assert.False(emergencyWatcher.IsCompleted, "the watcher must still be pending at the decision");
            }
            catch (Exception ex) { outerHookFailure ??= ExceptionDispatchInfo.Capture(ex); }
        }

        void OuterBeforeClose()
        {
            outerCloseEntries++;
            try
            {
                Assert.True(outerWatcherStarted.Task.IsCompletedSuccessfully);
                var (_, join) = outerWatcherStarted.Task.Result;
                Assert.True(ClientEvidence.TryGetValue(probe.Composer!, out var originalClient));
                Assert.Same(disposal, originalClient.OwnedResource);
                Assert.Same(clientFault, originalClient.Outcome.Task.Result);
                Assert.Equal(1, disposal.Attempts);
                AssertSentinelIntact(probe, "at outer resource closure");
                outerOpenAtClose.Add(!disposal.ResourceClosed && join is { IsCompletedSuccessfully: true, Result: true });
            }
            catch (Exception ex) { outerHookFailure ??= ExceptionDispatchInfo.Capture(ex); }
        }

        void OuterBeforeDelete()
        {
            outerDeleteEntries++;
            try
            {
                Assert.True(outerWatcherStarted.Task.IsCompletedSuccessfully);
                var (_, join) = outerWatcherStarted.Task.Result;
                Assert.True(ClientEvidence.TryGetValue(probe.Composer!, out var originalClient));
                Assert.Same(disposal, originalClient.OwnedResource);
                Assert.Same(clientFault, originalClient.Outcome.Task.Result);
                Assert.Equal(1, disposal.Attempts);
                AssertSentinelIntact(probe, "at outer deletion entry");
                outerClosedAtDelete.Add(disposal.ResourceClosed && join is { IsCompletedSuccessfully: true, Result: true });
            }
            catch (Exception ex) { outerHookFailure ??= ExceptionDispatchInfo.Capture(ex); }
        }

        var outerHooks = new SanitationBounds { BeforeResourceClose = OuterBeforeClose, BeforeDelete = OuterBeforeDelete };
        var outerWatcherBounds = outerWatcher == OuterWatcher.None ? null : new SanitationBounds
        {
            Clock = outerClock,
            Decided = OuterDecidedAsync,
            OwnDeadline = (operation, fault) => outerDeadlines.Add((operation, fault)),
        };

        // Observes the REAL outer join pending on the REAL watcher (both tasks), then expires ONLY the outer deadline - or, for
        // the control, completes the watcher inside the bound. A timer or an arrival signal is never the evidence: the watcher
        // task and the returned bounded join are each observed incomplete.
        var outerController = outerWatcher == OuterWatcher.None ? Task.CompletedTask : Task.Run(async () =>
        {
            try
            {
                var (work, join) = await outerWatcherStarted.Task.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
                Assert.Same(emergencyWatcher, work);
                Assert.True(watcherFired.Task.IsCompletedSuccessfully, "the earlier emergency deadline must already have fired");
                Assert.Equal(ControlledTimeProvider.HangGuard, emergencyClock.Elapsed);
                Assert.False(work.IsCompleted, "the real watcher must be pending when its outer join starts");
                Assert.False(join.IsCompleted, "the returned bounded join must be pending on the watcher");
                Assert.Equal(1, outerClock.TimersCreated);
                Assert.Equal([ControlledTimeProvider.HangGuard], outerClock.PendingDueTimes);
                Assert.Equal(TimeSpan.Zero, outerClock.Elapsed);
                Assert.False(disposal.ResourceClosed);
                AssertSentinelIntact(probe, "before the outer watcher join resolves");
                if (outerWatcher == OuterWatcher.CompletesInBound)
                {
                    watcherGate.TrySetResult();
                    await emergencyWatcher.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
                }
            }
            catch (Exception ex) { outerControllerFailure = ExceptionDispatchInfo.Capture(ex); }
            finally
            {
                if (outerWatcher == OuterWatcher.CompletesInBound || outerControllerFailure is not null)
                    watcherGate.TrySetResult(); // never leave the watcher (and so its join) parked
                if (outerWatcher != OuterWatcher.CompletesInBound)
                    outerClock.Advance(ControlledTimeProvider.HangGuard); // expire ONLY the outer watcher deadline
                controllerAdvanced.TrySetResult();
            }

            // PARKED AFTER its clock advance: a rescue attempt's own controller join is then pending on this exact task.
            if (rescueController != RescueController.None && outerControllerFailure is null)
                await controllerGate.Task;
        }, CancellationToken.None);

        async Task ObserveAsync()
        {
            await actorClock.WaitForPendingTimerAsync(Actor<IComposerMessage>.DisposeTimeout, Ct);
            await diagnosticClock.WaitForPendingTimerAsync(FixtureDiagnosticDeadline, Ct);
            Assert.True(realHold.Parked.IsCompletedSuccessfully);
            Assert.False(fixtureTask.IsCompleted);
            Assert.NotNull(injected);
            AssertSentinelIntact(probe, "before any sanitation");
            if (!observerFails)
                return; // NO PRIMARY: the observation passes, so any failure must come from the sanitation outcome itself
            try { Assert.Fail("INJECTED-SANITATION-OBSERVER"); }
            catch (Xunit.Sdk.FailException ex) { observer = ex; throw; }
        }

        Exception? thrown = null;
        Exception[] sanitationFaults = [];
        var sanitationList = new List<Exception>();
        ExceptionDispatchInfo? assertionFailure = null;
        try
        {
            switch (route)
            {
                case SanitationRoute.FallbackTest:
                case SanitationRoute.Observer:
                    thrown = await Record.ExceptionAsync(ObserveAndSanitizeAsync);
                    sanitationFaults = observerFails
                        ? thrown?.Data["SanitationFaults"] as Exception[] ?? []
                        : thrown is AggregateException aggregate ? aggregate.InnerExceptions.ToArray() : [];
                    break;
                default:
                    thrown = await Record.ExceptionAsync(() => WithDirectDisposalCleanupAsync(probe, disposal, fixtureTask,
                        clientFault, () => null, ObserveAsync, bounds, scenarioFailure: () => injected));
                    sanitationFaults = thrown?.Data["DirectDisposalFallbackFaults"] as Exception[] ?? [];
                    break;
            }

            async Task ObserveAndSanitizeAsync()
            {
                ExceptionDispatchInfo? primary = null;
                try { await ObserveAsync(); }
                catch (Exception ex) { primary = ExceptionDispatchInfo.Capture(ex); }
                finally
                {
                    try
                    {
                        if (route == SanitationRoute.FallbackTest)
                            await SanitizeFallbackTestAsync(unrelatedHold, probe, disposal, fixtureTask, Task.CompletedTask,
                                injected, null, clientFault, sanitationList, bounds);
                        else
                        {
                            var logger = probe.Composer is { } composer
                                ? (ComposerFixtureLogger?)typeof(Composer)
                                    .GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(composer)
                                : null;
                            await SanitizeObserverAsync(probe.Composer, probe.StateDir, logger, fixtureTask,
                                Task.CompletedTask, injected, null, clientFault, sanitationList, bounds);
                        }
                    }
                    catch (Exception ex) { AddDistinctFault(sanitationList, ex); }
                }

                if (primary is not null)
                {
                    if (sanitationList.Count > 0)
                        primary.SourceException.Data["SanitationFaults"] = sanitationList.ToArray();
                    primary.Throw();
                }
                if (sanitationList.Count > 0)
                    throw new AggregateException(sanitationList);
            }

            if (emergency == EmergencyDriver.None)
                await driver.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
            else
            {
                // The held driver has finished its clock work and is parked on its own gate - still PENDING.
                await driverHeld.Task.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
                Assert.False(driver.IsCompleted, "the held driver must still be pending");
            }

            driverFailure?.Throw();
            hookFailure?.Throw();
            primaryHandled = true;

            // The ORIGINAL failure is the observer assertion, by reference, whatever the sanitation decided.
            if (observerFails || route == SanitationRoute.DirectDisposal)
            {
                Assert.NotNull(observer);
                Assert.Same(observer, thrown);
                Assert.Equal("INJECTED-SANITATION-OBSERVER", thrown!.Message);
            }
            else
                Assert.Null(observer);
            Assert.True(pendingWhenDriven);
            Assert.Same(injected, lateOwnerFailure); // the owner really settled, by reference, with the scenario primary
            Assert.True(probe.Cleanup!.IsFaulted);
            Assert.Equal(0, sanitationClock.PendingTimerCount);
            Assert.Equal(TimeSpan.Zero, actorClock.Elapsed);
            Assert.Equal(TimeSpan.Zero, diagnosticClock.Elapsed);

            if (extraDisposalFault is not null)
            {
                // PREMISES: the additional fault REALLY went through ComposerAgentService's agent disposal, and the REAL
                // retained owner task carries that same object next to the expected original-client fault.
                Assert.Equal(1, Volatile.Read(ref extraHookRuns));
                Assert.Contains("ComposerAgentService.DisposeAgentAsync", extraDisposalFault.StackTrace, StringComparison.Ordinal);
                Assert.Contains("ComposerAgentService.DisposeClientsAndClearStateAsync", clientFault.StackTrace, StringComparison.Ordinal);
                var ownerLeaves = Flattened(probe.Cleanup.Exception!).ToArray();
                Assert.Contains(ownerLeaves, leaf => ReferenceEquals(leaf, extraDisposalFault));
                Assert.Contains(ownerLeaves, leaf => ReferenceEquals(leaf, clientFault));
                Assert.Same(clientFault, disposal.Outcome.Task.Result);
                if (useLookalikeOwnerDiagnostic)
                {
                    Assert.False(extraDisposalFault.Data.Contains(RetentionSourceKey));
                    Assert.Contains($"'{probe.StateDir}' was RETAINED, not deleted.", extraDisposalFault.Message, StringComparison.Ordinal);
                    Assert.Single(ownerLeaves, leaf => leaf.Data[RetentionSourceKey] is ReportSource.Owner);
                }
            }
            else
                Assert.Equal(0, Volatile.Read(ref extraHookRuns));

            var expectedOperations = route switch
            {
                SanitationRoute.FallbackTest => new[] { "the fixture task", "the fallback", "the retained cleanup", "the real actor" },
                SanitationRoute.Observer => ["the fixture task", "the fallback", "the retained cleanup", "the real actor", "the real teardown"],
                _ => ["the fixture task", "the retained cleanup", "the real actor", "the real teardown"],
            };
            // EVERY independent join is attempted, in order, even after the first bound expired.
            Assert.Equal(expectedOperations, decisions.Select(d => d.Operation));

            var expiries = sanitationFaults.SelectMany(Flattened).OfType<SanitationJoinExpiredException>().ToArray();
            if (boundExpires)
            {
                Assert.Equal([true], decisions.Select(d => d.Expired).Where(expired => expired));
                Assert.True(decisions[0].Expired);
                Assert.True(decisions[0].PendingAfterDecision);
                Assert.True(eligibleAtDecision);
                Assert.Equal(1, sanitationClock.TimersCreated); // no later bound was needed or expired

                // The exact deadline stays on the original failure ...
                Assert.Equal("The bounded sanitation wait for the fixture task expired.", Assert.Single(expiries).Message);
                Assert.Contains(sanitationFaults, fault => fault is SanitationJoinExpiredException);

                // ... and the late completion could not reverse the decision: the original state survives AFTER
                // sanitation returned (checked first), and no deletion ever began.
                Assert.True(Directory.Exists(probe.StateDir), "a late completion reversed an expired sanitation bound");
                AssertSentinelIntact(probe, "after sanitation returned");
                Assert.Empty(deleteSnapshots);
                Assert.Empty(closedAtDelete);
                Assert.Equal(0, openBeforeClose); // an expired bound closes nothing and deletes nothing
                Assert.False(disposal.ResourceClosed, "an expired bound must not have closed the resource");
            }
            else
            {
                Assert.All(decisions, d => Assert.False(d.Expired));
                Assert.Equal(1, sanitationClock.TimersCreated);
                Assert.Equal(TimeSpan.Zero, sanitationClock.Elapsed);
                Assert.Empty(expiries);

                Assert.Equal(1, disposal.Attempts);
                Assert.Same(clientFault, disposal.Outcome.Task.Result);
                if (route == SanitationRoute.DirectDisposal && extraDisposalFault is not null)
                {
                    // DIRECT ROUTE WITH AN UNEXPECTED OWNER FAULT: Composer.DisposeAsync then throws an AggregateException rather
                    // than the client fault, so the direct sanitizer's existing rule (the direct disposal fault must BE the
                    // original client fault) cannot establish quiescence and it RETAINS the state. The original direct
                    // resource-closure and sentinel-retention checkpoints are unchanged: nothing is closed, nothing is deleted.
                    Assert.IsType<AggregateException>(probe.DirectDisposeFault);
                    Assert.False(ReferenceEquals(probe.DirectDisposeFault, clientFault));
                    Assert.Equal(0, openBeforeClose);
                    Assert.Empty(closedAtDelete);
                    Assert.Empty(deleteSnapshots);
                    Assert.False(disposal.ResourceClosed, "the direct sanitizer must not close the resource it could not prove safe");
                    Assert.True(Directory.Exists(probe.StateDir));
                    AssertSentinelIntact(probe, "after the direct sanitizer retained the state");
                }
                else if (suppressFirstResourceClose)
                {
                    // A close attempt that leaves the original resource open is not deletion permission.
                    Assert.False(disposal.ResourceClosed);
                    Assert.Empty(closedAtDelete);
                    Assert.Empty(deleteSnapshots);
                    Assert.Contains(sanitationFaults, ex => ex is InvalidOperationException
                        && ex.Message.Contains("could not prove the client resource closed", StringComparison.Ordinal));
                    Assert.True(Directory.Exists(probe.StateDir));
                    AssertSentinelIntact(probe, "after the observer could not prove closure");
                    Assert.Equal(1, openBeforeClose);
                }
                else
                {
                    Assert.Equal(1, openBeforeClose); // the resource was open after the attempt, observed before ANY close
                    Assert.Equal([true], closedAtDelete); // closed, same fault, one attempt, sentinel present - at the checkpoint
                    Assert.Equal([true], deleteSnapshots);
                    Assert.True(disposal.ResourceClosed);
                    Assert.False(Directory.Exists(probe.StateDir), "the in-bound control must reach deletion after quiescence");
                }
            }

            if (route == SanitationRoute.DirectDisposal)
            {
                // THE EXTERNALLY REPORTED OUTCOME OF THE DIRECT ROUTE, read from the ACTUAL thrown observer and its attached
                // DirectDisposalFallbackFaults (never from a trace or the owner task alone).
                Assert.Same(observer, thrown); // the identical observer primary
                var attachedDirect = thrown!.Data["DirectDisposalFallbackFaults"] as Exception[];
                var directLeaves = (attachedDirect ?? []).SelectMany(Flattened).ToArray();

                // The pre-established scenario assertion and the observer primary itself are NEVER reported as a cleanup
                // secondary, whatever else is (by reference - not by message or type).
                Assert.DoesNotContain(directLeaves, leaf => ReferenceEquals(leaf, injected));
                Assert.DoesNotContain(directLeaves, leaf => ReferenceEquals(leaf, observer));

                // Provenance: each retention diagnostic here is the exact object the direct sanitizer created, captured at its
                // creation site - a lookalike with the same message cannot satisfy it.
                var ownRetentions = directLeaves.Where(leaf => directOwnReports.Any(own => ReferenceEquals(own, leaf))).ToArray();
                var deadlines = directLeaves.OfType<SanitationJoinExpiredException>().ToArray();
                var unexpectedDirect = directLeaves.Where(leaf => !ownRetentions.Contains(leaf) && !deadlines.Contains(leaf)).ToArray();

                // THE EXACT-REFERENCE ASSERTION comes FIRST, before any count or inventory check: the identical observer must
                // carry the very unexpected owner object that the real agent-disposal hook threw.
                if (extraDisposalFault is not null)
                    Assert.True(unexpectedDirect.Any(leaf => ReferenceEquals(leaf, extraDisposalFault)),
                        "OUTWARD-DIRECT-CHANNEL: the identical observer does not carry the unexpected owner fault");

                if (boundExpires)
                {
                    // EXACT INVENTORY: the genuine required deadline, the sanitizer's own retention diagnostic and - only when
                    // injected - the one unexpected owner fault. Nothing else, so losing, substituting or adding fails.
                    var (operation, deadline) = Assert.Single(sanitationDeadlines);
                    Assert.Equal("the fixture task", operation);
                    Assert.Same(deadline, Assert.Single(deadlines));
                    Assert.Equal("The bounded sanitation wait for the fixture task expired.", deadline.Message);
                    var retention = Assert.Single(ownRetentions);
                    Assert.Same(retention, Assert.Single(directOwnReports));
                    Assert.Contains($"retained '{probe.StateDir}'.", retention.Message, StringComparison.Ordinal);
                    Assert.Equal(extraDisposalFault is null ? 2 : 3, directLeaves.Length);
                }
                else
                {
                    Assert.Empty(deadlines);
                    Assert.Empty(sanitationDeadlines);
                    if (extraDisposalFault is null)
                    {
                        // Expected-only and in bound: the sanitizer proves quiescence and deletes - no retention diagnostic of its
                        // own and no deadline.
                        Assert.Empty(directOwnReports);
                        Assert.Empty(ownRetentions);
                    }
                    else
                    {
                        // Unexpected owner fault, in bound: the direct sanitizer retains. EXACT inventory - the one unexpected owner
                        // fault plus the sanitizer's own retention diagnostic, identified by creation-site identity. No deadline and
                        // no scenario assertion.
                        var retention = Assert.Single(ownRetentions);
                        Assert.Same(retention, Assert.Single(directOwnReports));
                        Assert.Contains($"retained '{probe.StateDir}'.", retention.Message, StringComparison.Ordinal);
                        Assert.Equal(2, directLeaves.Length);
                    }
                }

                if (extraDisposalFault is null)
                {
                    Assert.Empty(unexpectedDirect);
                    if (!boundExpires)
                    {
                        Assert.Empty(directLeaves); // in bound and expected-only: no cleanup secondary at all
                        Assert.False(thrown.Data.Contains("DirectDisposalFallbackFaults"));
                    }
                }
                else
                    Assert.Same(extraDisposalFault, Assert.Single(unexpectedDirect)); // the exact leaf, nothing else unexpected

                if (boundExpires || extraDisposalFault is not null)
                    Assert.IsType<Exception[]>(thrown.Data["DirectDisposalFallbackFaults"]);
            }
            else
            {
                // THE EXTERNALLY REPORTED OUTCOME (never an inspection of the owner task or logger alone): the identical
                // primary with the sanitation outcome attached, or - without a primary - the failure itself.
                Exception[] reportedLeaves;
                if (observerFails)
                    reportedLeaves = sanitationFaults.SelectMany(Flattened).ToArray();
                else if (thrown is null)
                    reportedLeaves = [];
                else
                    reportedLeaves = Flattened(Assert.IsType<AggregateException>(thrown)).ToArray();

                // Only the sanitizer's own, independently produced reports are ever "expected" here.
                var unexpected = reportedLeaves.Where(leaf => leaf is not SanitationJoinExpiredException
                    && !IsSanitizerOwnRetentionReport(leaf)).ToArray();

                if (boundExpires)
                    Assert.Single(reportedLeaves.OfType<SanitationJoinExpiredException>()); // the deadline survives, distinctly
                else
                    Assert.Empty(reportedLeaves.OfType<SanitationJoinExpiredException>());

                if (extraDisposalFault is null)
                {
                    Assert.True(unexpected.Length == 0, "SPURIOUS-FAULT: " + string.Join(" | ", unexpected.Select(u => u.Message)));
                    if (!observerFails && !boundExpires && !suppressFirstResourceClose)
                        Assert.Null(thrown); // expected-only, no primary, in bound: nothing may fail
                }
                else
                {
                    Assert.True(unexpected.Any(leaf => ReferenceEquals(leaf, extraDisposalFault)),
                        observerFails
                            ? "OUTWARD-PRIMARY-CHANNEL: the identical primary does not carry the unexpected owner fault"
                            : "OUTWARD-NO-PRIMARY-CHANNEL: the failure does not carry the unexpected owner fault");
                    Assert.Same(extraDisposalFault, Assert.Single(unexpected)); // the exact leaf, and nothing else unexpected
                    if (!observerFails)
                        Assert.NotNull(thrown);
                }
            }
        }
        catch (Exception ex) { assertionFailure = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            realHold.Release();
            unrelatedHold.Release(); // NOT the driver gate: the held driver stays pending until the checks below ran
            if (!primaryHandled)
                sanitationClock.Advance(ControlledTimeProvider.HangGuard);

            var cleanupFaults = new List<Exception>();
            var vetoed = false;
            var scratchRemoved = !Directory.Exists(probe.StateDir) && disposal.ResourceClosed;

            bool OwnerSettledWithTheExpectedClientFault() =>
                probe.Composer is { } && QuiescentAndEligible() && probe.Cleanup is { IsFaulted: true }
                && disposal.Outcome.Task is { IsCompletedSuccessfully: true } outcome
                && ReferenceEquals(outcome.Result, clientFault);

            async Task CloseAndDeleteAsync(SanitationBounds? hooks)
            {
                try
                {
                    hooks?.BeforeResourceClose?.Invoke();
                    disposal.CloseTestResource();
                    if (!disposal.ResourceClosed)
                        throw new InvalidOperationException("Sanitation could not prove the client resource closed.");
                    hooks?.BeforeDelete?.Invoke();
                    await SanitizeAsync(probe.StateDir, cleanupFaults);
                    scratchRemoved = !Directory.Exists(probe.StateDir);
                }
                catch (Exception ex) { cleanupFaults.Add(ex); }
            }

            async Task<bool> JoinRequiredAsync(Task work, string operation, SanitationBounds? joinBounds,
                List<(string Operation, Task Work, bool InBound)> record, Func<Exception, bool>? expected = null)
            {
                try
                {
                    var join = JoinSanitationWorkAsync(work, operation, joinBounds, cleanupFaults, expected);
                    // Publish after the join itself has run to its first suspension, not before
                    // entering it or when a timer happens to register.
                    if (operation == EmergencyDriverJoin && ReferenceEquals(record, emergencyJoins))
                        emergencyDriverStarted.TrySetResult((work, join));
                    if (operation == OuterWatcherJoin)
                        outerWatcherStarted.TrySetResult((work, join));
                    if (operation.EndsWith(" controller", StringComparison.Ordinal))
                        ControllerStarted(operation).TrySetResult((work, join));
                    var inBound = await join;
                    record.Add((operation, work, inBound));
                    return inBound;
                }
                catch (Exception ex)
                {
                    AddDistinctFault(cleanupFaults, ex);
                    return false;
                }
            }

            async Task<bool> JoinOwnerAsync(SanitationBounds? joinBounds, List<(string Operation, Task Work, bool InBound)> record)
            {
                // Explicit classification, identical for a passing and a failing case: the injected scenario failure, the
                // intended client fault and the owner's own retention diagnostic are expected; nothing else is.
                var isExpected = ExpectedSanitationLeaf(() => probe.Cleanup, () => probe.StateDir, clientFault,
                    injected, declareExtraToEmergency ? extraDisposalFault : null);
                var allInBound = await JoinRequiredAsync(fixtureTask, "the fixture task", joinBounds, record, isExpected);
                // The fixture publishes cleanup before it settles; read that task after its join.
                if (probe.Cleanup is { } cleanup)
                    allInBound &= await JoinRequiredAsync(cleanup, "the retained cleanup", joinBounds, record, isExpected);
                else
                    allInBound = false;
                if (probe.Composer is { } owned)
                {
                    allInBound &= await JoinRequiredAsync(ActorOf(owned).Completion, "the real actor", joinBounds, record);
                    allInBound &= await JoinRequiredAsync(owned.TeardownCompletion, "the real teardown", joinBounds, record);
                }
                else
                    allInBound = false;
                return allInBound;
            }

            try
            {
                // An expired decision vetoes both actions in this attempt, even if work later settles.
                if (!await JoinRequiredAsync(driver, EmergencyDriverJoin, emergencyDriverBounds, emergencyJoins))
                    vetoed = true; // the driver's expiry veto
                vetoed |= !await JoinOwnerAsync(emergencyJoinBounds, emergencyJoins);

                if (!vetoed && !scratchRemoved && OwnerSettledWithTheExpectedClientFault())
                    await CloseAndDeleteAsync(emergencyJoinBounds);

                if (thrown is not null && cleanupFaults.Count > 0)
                    thrown.Data["EmergencyCleanupFaults"] = cleanupFaults.ToArray();

                // Assertions are on the emergency attempt, before any separate cleanup.
                if (outerWatcher == OuterWatcher.None)
                    await emergencyWatcher.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
                else
                    await watcherFired.Task.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None); // fired, still held
                emergencyWatcherFailure?.Throw();
                emergencyHookFailure?.Throw();
                if (emergency == EmergencyDriver.HeldThroughExpiry || emergency == EmergencyDriver.ReleasedAtDecision)
                {
                    var driverJoins = emergencyDecisions.Where(d => d.Operation == EmergencyDriverJoin).ToArray();
                    Assert.Equal([(EmergencyDriverJoin, true, true)], driverJoins);
                    // The exact deadline, attached to the identical primary failure.
                    Assert.NotNull(observer);
                    Assert.Same(observer, thrown);
                    var attached = Assert.IsType<Exception[]>(thrown!.Data["EmergencyCleanupFaults"]);
                    var deadline = Assert.Single(cleanupFaults.OfType<SanitationJoinExpiredException>());
                    var (deadlineOperation, createdDeadline) = Assert.Single(emergencyDeadlines);
                    Assert.Equal(EmergencyDriverJoin, deadlineOperation);
                    Assert.Same(createdDeadline, deadline);
                    Assert.Equal("The bounded sanitation wait for the sanitation driver expired.", deadline.Message);
                    Assert.Contains(attached, fault => ReferenceEquals(fault, deadline));

                    // EVERY other required join was still attempted, in order, within its bound, with its real outcome.
                    Assert.Equal([EmergencyDriverJoin, "the fixture task", "the retained cleanup", "the real actor", "the real teardown"],
                        emergencyDecisions.Select(d => d.Operation));
                    Assert.All(emergencyDecisions.Skip(1), d => Assert.False(d.Expired));
                    Assert.Equal(emergencyDecisions.Select(d => d.Operation), emergencyJoins.Select(j => j.Operation));
                    Assert.Equal(new[] { driver, fixtureTask, probe.Cleanup!, ActorOf(probe.Composer!).Completion,
                        probe.Composer!.TeardownCompletion }, emergencyJoins.Select(j => j.Work));
                    Assert.False(emergencyJoins[0].InBound);
                    Assert.All(emergencyJoins.Skip(1), joined => Assert.True(joined.InBound));
                    Assert.Same(injected, Assert.Single(fixtureTask.Exception!.InnerExceptions));
                    Assert.Contains(clientFault, Flattened(probe.Cleanup!.Exception!));
                    Assert.True(ActorOf(probe.Composer!).Completion.IsCompletedSuccessfully);
                    Assert.True(probe.Composer!.TeardownCompletion.IsCompletedSuccessfully);

                    // Every OTHER deletion prerequisite holds, so only the veto can be what stops closure and deletion.
                    Assert.True(OwnerSettledWithTheExpectedClientFault(), "every other deletion prerequisite must hold");

                    Assert.Equal(1, emergencyClock.TimersCreated);
                    Assert.Equal(0, emergencyClock.PendingTimerCount);
                    Assert.Equal(ControlledTimeProvider.HangGuard, emergencyClock.Elapsed);
                    if (emergency == EmergencyDriver.HeldThroughExpiry)
                        Assert.False(driver.IsCompleted, "EMERGENCY-VETO: the real driver must still be pending after its join timed out");
                    else
                        Assert.True(driver.IsCompletedSuccessfully, "the driver was released before the decision resumed");


                    // THE VETO: nothing was closed or deleted by the timed-out attempt (checked before any outer cleanup).
                    Assert.False(disposal.ResourceClosed, "EMERGENCY-VETO: an expired required join must not close the client resource");
                    Assert.True(Directory.Exists(probe.StateDir), "EMERGENCY-VETO: an expired required join must not delete the state");
                    AssertSentinelIntact(probe, "after the timed-out emergency attempt returned");
                    Assert.Empty(emergencyOpenAtClose);
                    Assert.Empty(emergencyClosedAtDelete);
                    Assert.True(vetoed, "the expired emergency driver bound must be recorded as a veto");
                    cleanupFaults.Remove(deadline); // the exact expected deadline is the point of this case; it is attached above
                }
                else if (emergency == EmergencyDriver.JoinsInBound)
                {
                    // CONTROL: the real driver joined in bound, BEFORE the resource closed and the directory was removed.
                    Assert.False(vetoed);
                    Assert.Equal([EmergencyDriverJoin, "the fixture task", "the retained cleanup", "the real actor", "the real teardown"],
                        emergencyDecisions.Select(d => d.Operation));
                    Assert.All(emergencyDecisions, d => Assert.False(d.Expired));
                    Assert.Equal(emergencyDecisions.Select(d => d.Operation), emergencyJoins.Select(j => j.Operation));
                    Assert.All(emergencyJoins, j => Assert.True(j.InBound));
                    Assert.True(driver.IsCompletedSuccessfully);
                    Assert.Equal(TimeSpan.Zero, emergencyClock.Elapsed);
                    Assert.Equal([true], emergencyOpenAtClose); // driver joined, resource open, sentinel present
                    Assert.Equal([true], emergencyClosedAtDelete); // same resource closed, driver joined, sentinel present
                    Assert.True(disposal.ResourceClosed);
                    Assert.False(Directory.Exists(probe.StateDir), "the in-bound emergency control must remove the state");
                    Assert.Empty(cleanupFaults.OfType<SanitationJoinExpiredException>());
                }
            }
            catch (Exception ex)
            {
                if (assertionFailure is null)
                    assertionFailure = ExceptionDispatchInfo.Capture(ex);
                else
                    cleanupFaults.Add(ex);
            }
            finally
            {
                // A fresh attempt has its own deadline decisions. Release every owned gate even
                // if a checkpoint failed; one failed join cannot skip any later join.
                realHold.Release();
                unrelatedHold.Release();
                driverGate.TrySetResult();
                // THE WATCHER'S BOUNDED JOIN IS A REQUIRED JOIN: its result takes part in the decision exactly like the driver and
                // the owner joins. An expired bound is final for this attempt even if the watcher completes before the decision resumes.
                try
                {
                    var watcherInBound = await JoinRequiredAsync(emergencyWatcher, OuterWatcherJoin, outerWatcherBounds, outerJoins);
                    var outerInBound = watcherInBound;
                    outerInBound &= await JoinRequiredAsync(driver, "the outer cleanup driver", null, outerJoins);
                    outerInBound &= await JoinOwnerAsync(null, outerJoins);
                    try
                    {
                        if (outerInBound && !scratchRemoved && OwnerSettledWithTheExpectedClientFault() && driver.IsCompletedSuccessfully)
                            await CloseAndDeleteAsync(outerWatcher == OuterWatcher.None ? null : outerHooks);

                        if (thrown is not null && outerWatcher != OuterWatcher.None && cleanupFaults.Count > 0)
                            thrown.Data["OuterCleanupFaults"] = cleanupFaults.ToArray();

                        if (assertionFailure is null && outerWatcher != OuterWatcher.None)
                        {
                            try
                            {
                                if (rescueController == RescueController.None)
                                    await outerController.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
                                else
                                    await controllerAdvanced.Task.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None); // advanced, still parked
                                outerControllerFailure?.Throw();
                                outerHookFailure?.Throw();
                                var owner = new Task[] { driver, fixtureTask, probe.Cleanup!, ActorOf(probe.Composer!).Completion, probe.Composer!.TeardownCompletion };

                                // ORDERING PREMISES: every required join was attempted, in order, on the real tasks, and every join
                                // other than the watcher's settled in bound.
                                Assert.Equal([OuterWatcherJoin, "the outer cleanup driver", "the fixture task", "the retained cleanup", "the real actor", "the real teardown"],
                                    outerJoins.Select(j => j.Operation));
                                Assert.Equal(owner.Prepend(emergencyWatcher), outerJoins.Select(j => j.Work));
                                Assert.All(outerJoins.Skip(1), joined => Assert.True(joined.InBound));
                                var (watcherWork, watcherJoin) = await outerWatcherStarted.Task.WaitAsync(
                                    ControlledTimeProvider.HangGuard, CancellationToken.None);
                                Assert.Same(emergencyWatcher, watcherWork);
                                Assert.True(watcherJoin.IsCompletedSuccessfully);
                                Assert.Equal(outerJoins[0].InBound, watcherJoin.Result);
                                Assert.True(driver.IsCompletedSuccessfully);
                                Assert.Same(injected, Assert.Single(fixtureTask.Exception!.InnerExceptions));
                                var retainedLeaves = Flattened(probe.Cleanup!.Exception!).ToArray();
                                Assert.Equal(2, retainedLeaves.Length);
                                Assert.Contains(retainedLeaves, fault => ReferenceEquals(clientFault, fault));
                                Assert.Single(retainedLeaves, fault => fault.Data[RetentionSourceKey] is ReportSource.Owner);
                                Assert.True(ActorOf(probe.Composer!).Completion.IsCompletedSuccessfully);
                                Assert.True(probe.Composer!.TeardownCompletion.IsCompletedSuccessfully);
                                Assert.True(ClientEvidence.TryGetValue(probe.Composer, out var originalClient));
                                Assert.Same(disposal, originalClient.OwnedResource);
                                Assert.Same(clientFault, originalClient.Outcome.Task.Result);
                                Assert.Equal(1, disposal.Attempts);
                                Assert.Equal(1, outerClock.TimersCreated);
                                Assert.Equal(0, outerClock.PendingTimerCount);
                                Assert.Equal(1, emergencyClock.TimersCreated);
                                Assert.Equal(ControlledTimeProvider.HangGuard, emergencyClock.Elapsed); // the EARLIER deadline, unchanged

                                if (outerWatcher == OuterWatcher.CompletesInBound)
                                {
                                    // CONTROL: the real watcher joined IN BOUND, BEFORE the resource closed and the directory was removed.
                                    Assert.True(outerJoins[0].InBound);
                                    Assert.Equal([(OuterWatcherJoin, false, false)], outerDecisions);
                                    Assert.True(emergencyWatcher.IsCompletedSuccessfully);
                                    Assert.Equal(TimeSpan.Zero, outerClock.Elapsed);
                                    Assert.Equal(1, outerCloseEntries);
                                    Assert.Equal(1, outerDeleteEntries);
                                    Assert.Equal([true], outerOpenAtClose); // watcher joined in bound, resource open, sentinel present
                                    Assert.Equal([true], outerClosedAtDelete); // same resource closed, sentinel present at delete entry
                                    Assert.True(disposal.ResourceClosed);
                                    Assert.False(Directory.Exists(probe.StateDir), "the in-bound outer control must remove the state");
                                    Assert.True(scratchRemoved);
                                    Assert.Empty(outerDeadlines);
                                    Assert.Empty(cleanupFaults.OfType<SanitationJoinExpiredException>());
                                }
                                else
                                {
                                    Assert.False(outerJoins[0].InBound);
                                    Assert.Equal([(OuterWatcherJoin, true, true)], outerDecisions);
                                    Assert.Equal(ControlledTimeProvider.HangGuard, outerClock.Elapsed);
                                    if (outerWatcher == OuterWatcher.HeldPending)
                                        Assert.False(emergencyWatcher.IsCompleted, "the real watcher must still be pending after its outer join timed out");
                                    else
                                    {
                                        Assert.True(watcherCompletedBeforeResume);
                                        Assert.True(joinParkedAtResume);
                                        Assert.True(emergencyWatcher.IsCompletedSuccessfully, "the watcher completed before the decision resumed");
                                    }

                                    // Every OTHER deletion prerequisite holds, so only the watcher veto can stop closure and deletion.
                                    Assert.True(OwnerSettledWithTheExpectedClientFault(), "every other deletion prerequisite must hold");
                                    Assert.True(driver.IsCompletedSuccessfully);
                                    Assert.True(vetoed, "the emergency driver veto is unchanged");

                                    // Pin the outward timeout before checking for destructive effects. A veto mutant
                                    // must preserve this exact diagnostic and the already-settled owner outcomes.
                                    Assert.NotNull(observer);
                                    Assert.Same(observer, thrown);
                                    var attached = Assert.IsType<Exception[]>(thrown!.Data["OuterCleanupFaults"]);
                                    var outerDeadline = Assert.IsType<SanitationJoinExpiredException>(Assert.Single(attached));
                                    var (outerOperation, createdOuterDeadline) = Assert.Single(outerDeadlines);
                                    Assert.Equal(OuterWatcherJoin, outerOperation);
                                    Assert.Same(createdOuterDeadline, outerDeadline);
                                    Assert.Equal("The bounded sanitation wait for the emergency watcher expired.", outerDeadline.Message);
                                    Assert.Contains(cleanupFaults, fault => ReferenceEquals(fault, outerDeadline));
                                    var earlier = Assert.IsType<SanitationJoinExpiredException>(Assert.Single(
                                        Assert.IsType<Exception[]>(thrown.Data["EmergencyCleanupFaults"])));
                                    Assert.Same(Assert.Single(emergencyDeadlines).Fault, earlier);
                                    Assert.Equal("The bounded sanitation wait for the sanitation driver expired.", earlier.Message);
                                    Assert.NotSame(earlier, outerDeadline);
                                    cleanupFaults.Remove(outerDeadline); // still attached to the original primary

                                    // THE VETO (checked before any separate rescue attempt can change it).
                                    Assert.False(disposal.ResourceClosed, "OUTER-WATCHER-VETO: an expired required watcher join must not close the client resource");
                                    Assert.True(Directory.Exists(probe.StateDir), "OUTER-WATCHER-VETO: an expired required watcher join must not delete the state");
                                    AssertSentinelIntact(probe, "after the timed-out outer attempt returned");
                                    Assert.Equal(0, outerCloseEntries);
                                    Assert.Equal(0, outerDeleteEntries);
                                    Assert.Empty(outerOpenAtClose);
                                    Assert.Empty(outerClosedAtDelete);
                                    Assert.False(scratchRemoved);
                                    beforeUnprovenRescue?.Invoke();
                                }
                            }
                            catch (Exception ex)
                            {
                                assertionFailure = ExceptionDispatchInfo.Capture(ex);
                            }
                        }
                        else if (assertionFailure is null && emergency is (EmergencyDriver.HeldThroughExpiry or EmergencyDriver.ReleasedAtDecision))
                        {
                            Assert.Equal([OuterWatcherJoin, "the outer cleanup driver", "the fixture task", "the retained cleanup", "the real actor", "the real teardown"],
                                outerJoins.Select(j => j.Operation));
                            Assert.All(outerJoins, joined => Assert.True(joined.InBound));
                            Assert.True(disposal.ResourceClosed);
                            Assert.False(Directory.Exists(probe.StateDir), "only a fresh, successful attempt may remove the retained state");
                        }
                    }
                    catch (Exception ex) { AddDistinctFault(cleanupFaults, ex); }
                }
                finally
                {
                    if (outerWatcher != OuterWatcher.None)
                    {
                        // This is a fresh attempt, after the pre-rescue assertions. Its own joins
                        // cannot be short-circuited by an expired decision or an assertion failure.
                        watcherGate.TrySetResult();
                        var rescueChecksFailed = assertionFailure is not null; // after an earlier failure: fail-safe, real-time cleanup only
                        var rescueClock1 = new ControlledTimeProvider();
                        var rescueClock2 = new ControlledTimeProvider();
                        try
                        {
                            if (rescueChecksFailed)
                                controllerGate.TrySetResult();

                            // EACH rescue attempt requires its OWN successful in-bound controller join, plus the remaining joins.
                            // The controller's bounded-join result takes part in the attempt's decision exactly like the others:
                            // an expired bound is final for that attempt even if the controller settles before the decision resumes.
                            async Task<bool> JoinRescueAsync(string label, SanitationBounds? controllerBounds,
                                List<(string Operation, Task Work, bool InBound)> record)
                            {
                                var controllerOperation = $"the {label} controller";
                                var faultsBeforeController = cleanupFaults.Count;
                                var controllerInBound = await JoinRequiredAsync(outerController, controllerOperation, controllerBounds, controllerJoins);
                                var controllerDeadlines = cleanupFaults.Skip(faultsBeforeController).OfType<SanitationJoinExpiredException>()
                                    .Where(fault => fault.Operation == controllerOperation).ToArray();
                                var inBound = controllerInBound;
                                inBound &= await JoinRequiredAsync(emergencyWatcher, $"the {label} watcher", null, record);
                                inBound &= await JoinRequiredAsync(driver, $"the {label} driver", null, record);
                                inBound &= await JoinOwnerAsync(null, record);
                                if (thrown is not null && controllerDeadlines.Length != 0)
                                {
                                    // Report the actual join's exception on the already-caught observer, independently
                                    // of the assertion hooks (which only verify the outward result).
                                    rescueAttached.AddRange(controllerDeadlines);
                                    thrown.Data["RescueCleanupFaults"] = rescueAttached.ToArray();
                                }
                                return inBound;
                            }

                            void RecordRescueFailure(Exception ex)
                            {
                                rescueChecksFailed = true;
                                controllerGate.TrySetResult(); // never leave the controller (and so a later join) parked
                                if (assertionFailure is null)
                                    assertionFailure = ExceptionDispatchInfo.Capture(ex);
                                else
                                    AddDistinctFault(cleanupFaults, ex);
                            }

                            (Task Work, Task<bool> Join) ReadStartedControllerJoin(string operation)
                            {
                                var published = ControllerStarted(operation).Task;
                                Assert.True(published.IsCompletedSuccessfully, $"The actual join for {operation} was never published.");
                                return published.Result;
                            }

                            void AssertRescuePrerequisites(string label, List<(string Operation, Task Work, bool InBound)> record)
                            {
                                Assert.Equal([$"the {label} watcher", $"the {label} driver", "the fixture task", "the retained cleanup",
                                    "the real actor", "the real teardown"], record.Select(joined => joined.Operation));
                                var realTasks = new[] { emergencyWatcher, driver, fixtureTask, probe.Cleanup!, ActorOf(probe.Composer!).Completion,
                                    probe.Composer!.TeardownCompletion };
                                Assert.Equal(realTasks.Length, record.Count);
                                for (var index = 0; index < realTasks.Length; index++)
                                {
                                    Assert.Same(realTasks[index], record[index].Work);
                                    Assert.True(record[index].InBound, $"{record[index].Operation} must join in bound");
                                    Assert.True(realTasks[index].IsCompleted);
                                }
                                Assert.True(OwnerSettledWithTheExpectedClientFault(), "every other deletion prerequisite must hold");
                                Assert.True(driver.IsCompletedSuccessfully);
                                Assert.True(ClientEvidence.TryGetValue(probe.Composer!, out var originalClient));
                                Assert.Same(disposal, originalClient.OwnedResource);
                                Assert.Same(clientFault, originalClient.Outcome.Task.Result);
                            }

                            // Check the actual joins and eligibility at entry, not just after the attempt has returned.
                            SanitationBounds RescueHooks(string label, List<(string Operation, Task Work, bool InBound)> record,
                                int[] counts, List<bool> openAtClose, List<bool> closedAtDelete) => new()
                            {
                                BeforeResourceClose = () =>
                                {
                                    counts[0]++;
                                    try
                                    {
                                        AssertRescuePrerequisites(label, record);
                                        var (work, join) = ReadStartedControllerJoin($"the {label} controller");
                                        Assert.Same(outerController, work);
                                        AssertSentinelIntact(probe, $"at {label} resource closure");
                                        openAtClose.Add(!disposal.ResourceClosed && join is { IsCompletedSuccessfully: true, Result: true });
                                    }
                                    catch (Exception ex) { rescueHookFailure ??= ExceptionDispatchInfo.Capture(ex); }
                                },
                                BeforeDelete = () =>
                                {
                                    counts[1]++;
                                    try
                                    {
                                        AssertRescuePrerequisites(label, record);
                                        var (work, join) = ReadStartedControllerJoin($"the {label} controller");
                                        Assert.Same(outerController, work);
                                        AssertSentinelIntact(probe, $"at {label} deletion entry");
                                        closedAtDelete.Add(disposal.ResourceClosed && join is { IsCompletedSuccessfully: true, Result: true });
                                    }
                                    catch (Exception ex) { rescueHookFailure ??= ExceptionDispatchInfo.Capture(ex); }
                                },
                            };

                            SanitationBounds RescueControllerBounds(string label, ControlledTimeProvider clock) => new()
                            {
                                Clock = clock,
                                OwnDeadline = (operation, fault) => rescueDeadlines.Add((operation, fault)),
                                Decided = label == "rescue" && rescueController == RescueController.SettlesBeforeDecisionResumes
                                    ? async (operation, expired, pending) =>
                                    {
                                        try
                                        {
                                            Assert.True(expired, "the first rescue controller deadline must have won");
                                            Assert.True(pending, "the real controller must be pending when its deadline wins");
                                            Assert.False(disposal.ResourceClosed);
                                            AssertSentinelIntact(probe, "when the rescue controller deadline wins");
                                            // PARKED decision continuation: the controller settles BEFORE the sanitizer resumes.
                                            controllerGate.TrySetResult();
                                            await outerController.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
                                            controllerCompletedBeforeResume = outerController.IsCompletedSuccessfully;
                                            rescueJoinParkedAtResume = !ReadStartedControllerJoin(operation).Join.IsCompleted;
                                        }
                                        catch (Exception ex) { rescueHookFailure ??= ExceptionDispatchInfo.Capture(ex); }
                                    }
                                    : null,
                            };

                            // Observes the ACTUAL bounded join pending on the exact controller task, then advances ONLY this attempt's
                            // deadline (or, for the control, completes the controller inside the bound).
                            void StartRescueDriver(string label, ControlledTimeProvider clock, bool completeInBound) =>
                                rescueDrivers.Add(Task.Run(async () =>
                                {
                                    var operation = $"the {label} controller";
                                    try
                                    {
                                        var (work, join) = await ControllerStarted(operation).Task.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
                                        Assert.Same(outerController, work);
                                        Assert.True(controllerAdvanced.Task.IsCompletedSuccessfully,
                                            "the real controller must already have advanced the earlier outer deadline");
                                        Assert.Equal(ControlledTimeProvider.HangGuard, outerClock.Elapsed);
                                        Assert.False(work.IsCompleted, "the real controller must be pending when this attempt's join starts");
                                        Assert.False(join.IsCompleted, "the actual bounded join must be pending on that exact controller task");
                                        Assert.Equal(1, clock.TimersCreated);
                                        Assert.Equal([ControlledTimeProvider.HangGuard], clock.PendingDueTimes);
                                        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
                                        Assert.False(disposal.ResourceClosed);
                                        AssertSentinelIntact(probe, $"before the {label} controller join resolves");
                                        if (completeInBound)
                                        {
                                            controllerGate.TrySetResult();
                                            await outerController.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
                                        }
                                    }
                                    catch (Exception ex) { rescueDriverFailure ??= ExceptionDispatchInfo.Capture(ex); }
                                    finally
                                    {
                                        if (completeInBound || rescueDriverFailure is not null)
                                            controllerGate.TrySetResult();
                                        if (!completeInBound)
                                            clock.Advance(ControlledTimeProvider.HangGuard); // expire ONLY this attempt's controller deadline
                                    }
                                }, CancellationToken.None));

                            // Check every premise first. Bypassing only the in-bound result must fail the
                            // final retention assertion, not a timing, task-identity or diagnostic check.
                            void CheckVetoedAttempt(string label, ControlledTimeProvider clock,
                                List<(string Operation, Task Work, bool InBound)> record, int[] counts,
                                List<bool> openAtClose, List<bool> closedAtDelete, bool controllerHeld)
                            {
                                rescueDriverFailure?.Throw();
                                rescueHookFailure?.Throw();
                                AssertRescuePrerequisites(label, record);

                                var controllerOperation = $"the {label} controller";
                                var joined = Assert.Single(controllerJoins, j => j.Operation == controllerOperation);
                                Assert.Same(outerController, joined.Work);
                                Assert.False(joined.InBound);
                                var (work, join) = ReadStartedControllerJoin(controllerOperation);
                                Assert.Same(outerController, work);
                                Assert.True(join is { IsCompletedSuccessfully: true, Result: false });
                                Assert.Equal(1, clock.TimersCreated);
                                Assert.Equal(0, clock.PendingTimerCount);
                                Assert.Equal(ControlledTimeProvider.HangGuard, clock.Elapsed);
                                if (controllerHeld)
                                    Assert.False(outerController.IsCompleted, "the real controller must still be pending after its join timed out");
                                else
                                {
                                    Assert.True(controllerCompletedBeforeResume, "the controller must have completed before the decision resumes");
                                    Assert.True(rescueJoinParkedAtResume, "the join's decision continuation must still be parked after the controller completed");
                                    Assert.True(outerController.IsCompletedSuccessfully);
                                }
                                var (deadlineOperation, created) = Assert.Single(rescueDeadlines, d => d.Operation == controllerOperation);
                                Assert.Equal(controllerOperation, deadlineOperation);
                                Assert.Equal($"The bounded sanitation wait for {controllerOperation} expired.", created.Message);
                                Assert.Contains(cleanupFaults, fault => ReferenceEquals(fault, created));

                                Assert.NotNull(observer);
                                Assert.Same(observer, thrown);
                                var reported = Assert.IsType<Exception[]>(thrown!.Data["RescueCleanupFaults"]);
                                Assert.Equal(label == "rescue" ? 1 : 2, reported.Length);
                                Assert.Same(created, reported[^1]);
                                var emergencyDeadline = Assert.Single(Assert.IsType<Exception[]>(thrown.Data["EmergencyCleanupFaults"]));
                                var outerDeadline = Assert.Single(Assert.IsType<Exception[]>(thrown.Data["OuterCleanupFaults"]));
                                Assert.Same(Assert.Single(emergencyDeadlines).Fault, emergencyDeadline);
                                Assert.Same(Assert.Single(outerDeadlines).Fault, outerDeadline);
                                Assert.NotSame(emergencyDeadline, created);
                                Assert.NotSame(outerDeadline, created);
                                Assert.NotEqual(emergencyDeadline.Message, created.Message);
                                Assert.NotEqual(outerDeadline.Message, created.Message);
                                if (reported.Length == 2)
                                    Assert.Same(Assert.Single(rescueDeadlines, d => d.Operation == "the rescue controller").Fault, reported[0]);

                                // Nothing was closed or deleted before release or an independent later attempt.
                                Assert.False(disposal.ResourceClosed, $"RESCUE-CONTROLLER-VETO: an expired {label} controller join must not close the client resource");
                                Assert.True(Directory.Exists(probe.StateDir), $"RESCUE-CONTROLLER-VETO: an expired {label} controller join must not delete the state");
                                AssertSentinelIntact(probe, $"after the timed-out {label} attempt returned");
                                Assert.Equal(0, counts[0]);
                                Assert.Equal(0, counts[1]);
                                Assert.Empty(openAtClose);
                                Assert.Empty(closedAtDelete);
                                Assert.False(scratchRemoved);
                                cleanupFaults.Remove(created); // already reported on the identical primary
                            }

                            if (!scratchRemoved)
                            {
                                var held = rescueController is RescueController.HeldAtFirstRescue or RescueController.HeldAtSecondRescue
                                    or RescueController.SettlesBeforeDecisionResumes;
                                var useDriver1 = rescueController != RescueController.None && !rescueChecksFailed;
                                var counts1 = new int[2];
                                var open1 = new List<bool>();
                                var closed1 = new List<bool>();
                                var hooks1 = RescueHooks("rescue", rescueJoins, counts1, open1, closed1);
                                var beforeRescueFaults = cleanupFaults.Count;
                                if (useDriver1)
                                    StartRescueDriver("rescue", rescueClock1, rescueController == RescueController.JoinsInBound);
                                try
                                {
                                    var attempt1 = await JoinRescueAsync("rescue", useDriver1 ? RescueControllerBounds("rescue", rescueClock1) : null, rescueJoins);
                                    if (attempt1 && OwnerSettledWithTheExpectedClientFault() && driver.IsCompletedSuccessfully)
                                        await CloseAndDeleteAsync(hooks1);

                                    if (beforeUnprovenRescue is not null)
                                    {
                                        Assert.Equal(["the rescue watcher", "the rescue driver", "the fixture task", "the retained cleanup",
                                            "the real actor", "the real teardown"], rescueJoins.Select(j => j.Operation));
                                        Assert.Equal(new[] { emergencyWatcher, driver, fixtureTask, probe.Cleanup!, ActorOf(probe.Composer!).Completion,
                                            probe.Composer!.TeardownCompletion }, rescueJoins.Select(j => j.Work));
                                        Assert.All(rescueJoins, joined => Assert.True(joined.InBound));
                                        Assert.Equal(1, counts1[0]);
                                        Assert.Equal(0, counts1[1]);
                                        Assert.False(disposal.ResourceClosed);
                                        Assert.False(scratchRemoved);
                                        AssertSentinelIntact(probe, "after rescue could not prove closure");
                                        Assert.Equal("Sanitation could not prove the client resource closed.",
                                            Assert.Single(cleanupFaults.Skip(beforeRescueFaults)).Message);
                                    }
                                    else if (held && assertionFailure is null && !rescueChecksFailed)
                                    {
                                        try
                                        {
                                            CheckVetoedAttempt("rescue", rescueClock1, rescueJoins, counts1, open1, closed1,
                                                controllerHeld: rescueController != RescueController.SettlesBeforeDecisionResumes);
                                        }
                                        catch (Exception ex) { RecordRescueFailure(ex); }
                                    }
                                    else if (rescueController == RescueController.JoinsInBound && assertionFailure is null && !rescueChecksFailed)
                                    {
                                        try
                                        {
                                            rescueDriverFailure?.Throw();
                                            rescueHookFailure?.Throw();
                                            // CONTROL: the real controller joined IN BOUND before the resource closed and the state was removed.
                                            var joined = Assert.Single(controllerJoins);
                                            Assert.Equal("the rescue controller", joined.Operation);
                                            Assert.Same(outerController, joined.Work);
                                            Assert.True(joined.InBound);
                                            Assert.Same(outerController, ReadStartedControllerJoin("the rescue controller").Work);
                                            Assert.True(outerController.IsCompletedSuccessfully);
                                            Assert.Empty(rescueDeadlines);
                                            Assert.Equal(TimeSpan.Zero, rescueClock1.Elapsed);
                                            Assert.Equal(1, rescueClock1.TimersCreated);
                                            Assert.Equal(0, rescueClock1.PendingTimerCount);
                                            AssertRescuePrerequisites("rescue", rescueJoins);
                                            Assert.False(thrown!.Data.Contains("RescueCleanupFaults"));
                                            Assert.Equal(1, counts1[0]); // exactly one close ...
                                            Assert.Equal(1, counts1[1]); // ... and exactly one deletion
                                            Assert.Equal([true], open1); // controller joined in bound, resource open, sentinel intact
                                            Assert.Equal([true], closed1); // same resource closed, sentinel intact at delete entry
                                            Assert.True(disposal.ResourceClosed);
                                            Assert.True(scratchRemoved);
                                            Assert.False(Directory.Exists(probe.StateDir), "the in-bound rescue control must remove the state");
                                            Assert.Same(observer, thrown);
                                        }
                                        catch (Exception ex) { RecordRescueFailure(ex); }
                                    }
                                    else if (assertionFailure is null)
                                    {
                                        Assert.All(rescueJoins, joined => Assert.True(joined.InBound));
                                        Assert.True(disposal.ResourceClosed);
                                        Assert.False(Directory.Exists(probe.StateDir), "only a fresh, successful rescue may remove the retained state");
                                    }
                                }
                                catch (Exception ex) { AddDistinctFault(cleanupFaults, ex); }

                                // ---- SECOND ATTEMPT (call site 2): after the first one's checks. In HeldAtSecondRescue the controller is STILL
                                // parked, so its own join is pending and must veto too; otherwise it is the separate, independent cleanup.
                                var secondExpires = rescueController == RescueController.HeldAtSecondRescue && !rescueChecksFailed;
                                var secondRuns = !scratchRemoved && (beforeUnprovenRescue is not null
                                    || rescueController is RescueController.HeldAtFirstRescue or RescueController.HeldAtSecondRescue
                                        or RescueController.SettlesBeforeDecisionResumes);
                                if (secondRuns)
                                {
                                    var counts2 = new int[2];
                                    var open2 = new List<bool>();
                                    var closed2 = new List<bool>();
                                    var record2 = new List<(string Operation, Task Work, bool InBound)>();
                                    var hooks2 = RescueHooks("rescue retry", record2, counts2, open2, closed2);
                                    if (secondExpires)
                                        StartRescueDriver("rescue retry", rescueClock2, completeInBound: false);
                                    else if (rescueController is RescueController.HeldAtFirstRescue or RescueController.SettlesBeforeDecisionResumes)
                                        controllerGate.TrySetResult(); // the first attempt's checks are done: release the controller
                                    try
                                    {
                                        var attempt2 = await JoinRescueAsync("rescue retry",
                                            secondExpires ? RescueControllerBounds("rescue retry", rescueClock2) : null, record2);
                                        if (!scratchRemoved && attempt2 && OwnerSettledWithTheExpectedClientFault() && driver.IsCompletedSuccessfully)
                                            await CloseAndDeleteAsync(beforeUnprovenRescue is not null ? null : hooks2);

                                        if (secondExpires && assertionFailure is null && !rescueChecksFailed)
                                        {
                                            try
                                            {
                                                CheckVetoedAttempt("rescue retry", rescueClock2, record2, counts2, open2, closed2, controllerHeld: true);
                                                Assert.Equal(2, rescueAttached.Count); // both controller deadlines, distinct
                                                Assert.NotSame(rescueAttached[0], rescueAttached[1]);
                                            }
                                            catch (Exception ex) { RecordRescueFailure(ex); }

                                            // ---- Only now, after the second attempt's checks: release the controller and run the separate cleanup.
                                            controllerGate.TrySetResult();
                                            var record3 = new List<(string Operation, Task Work, bool InBound)>();
                                            var counts3 = new int[2];
                                            var open3 = new List<bool>();
                                            var closed3 = new List<bool>();
                                            var attempt3 = await JoinRescueAsync("rescue final", null, record3);
                                            if (!scratchRemoved && attempt3 && OwnerSettledWithTheExpectedClientFault() && driver.IsCompletedSuccessfully)
                                                await CloseAndDeleteAsync(RescueHooks("rescue final", record3, counts3, open3, closed3));
                                            if (assertionFailure is null)
                                            {
                                                Assert.All(record3, joined => Assert.True(joined.InBound));
                                                Assert.True(controllerJoins.Last().InBound);
                                                Assert.Equal(1, counts3[0]);
                                                Assert.Equal(1, counts3[1]);
                                                Assert.Equal([true], open3);
                                                Assert.Equal([true], closed3);
                                                Assert.True(disposal.ResourceClosed);
                                                Assert.False(Directory.Exists(probe.StateDir), "only a fresh, successful attempt may remove the retained state");
                                            }
                                        }
                                        else if (beforeUnprovenRescue is null && assertionFailure is null)
                                        {
                                            // The separate cleanup after the first attempt's veto: in bound, then exactly one close and one deletion.
                                            Assert.All(record2, joined => Assert.True(joined.InBound));
                                            Assert.True(controllerJoins.Last().InBound);
                                            Assert.Equal(1, counts2[0]);
                                            Assert.Equal(1, counts2[1]);
                                            Assert.Equal([true], open2);
                                            Assert.Equal([true], closed2);
                                            Assert.True(disposal.ResourceClosed);
                                            Assert.False(Directory.Exists(probe.StateDir), "only a fresh, successful attempt may remove the retained state");
                                        }
                                    }
                                    catch (Exception ex) { AddDistinctFault(cleanupFaults, ex); }
                                }
                            }
                        }
                        finally
                        {
                            // Failure-safe: release every owned gate and boundedly observe every actual task, independent of cancellation.
                            realHold.Release();
                            unrelatedHold.Release();
                            driverGate.TrySetResult();
                            watcherGate.TrySetResult();
                            controllerGate.TrySetResult();
                            foreach (var rescueDriver in rescueDrivers)
                            {
                                try { await rescueDriver.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None); }
                                catch (Exception ex) { AddDistinctFault(cleanupFaults, ex); }
                            }

                            try { await outerController.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None); }
                            catch (Exception ex) { AddDistinctFault(cleanupFaults, ex); }
                            // Task wrappers cache assertion failures to release their gates before any throw.
                            // Preserve those failures if an earlier assertion prevented reaching their checkpoint.
                            foreach (var captured in new[] { outerControllerFailure, rescueDriverFailure, rescueHookFailure })
                            {
                                if (captured is not null && !ReferenceEquals(captured.SourceException, assertionFailure?.SourceException))
                                    AddDistinctFault(cleanupFaults, captured.SourceException);
                            }
                        }
                    }
                }
            }

            if (Directory.Exists(probe.StateDir))
                cleanupFaults.Add(new InvalidOperationException($"Sanitation test could not prove cleanup; retained '{probe.StateDir}'."));

            if (cleanupFaults.Count > 0)
            {
                if (assertionFailure is not null)
                    assertionFailure.SourceException.Data["SanitationTestCleanupFaults"] = cleanupFaults.ToArray();
                else
                    assertionFailure = ExceptionDispatchInfo.Capture(new AggregateException(cleanupFaults));
            }
        }

        assertionFailure?.Throw();
    }

    [Fact]
    public async Task ComposerFixture_FallbackSanitation_ExpiredBoundIsFinalEvenWhenTheOwnerSettlesBeforeTheDecisionResumes() =>
        await RunSanitationRouteAsync(SanitationRoute.FallbackTest, boundExpires: true);

    [Fact]
    public async Task ComposerFixture_FallbackSanitation_InBoundSettlementDeletesOnlyAfterProvenQuiescence() =>
        await RunSanitationRouteAsync(SanitationRoute.FallbackTest, boundExpires: false);

    [Fact]
    public async Task ComposerFixture_ObserverSanitation_ExpiredBoundIsFinalEvenWhenTheOwnerSettlesBeforeTheDecisionResumes() =>
        await RunSanitationRouteAsync(SanitationRoute.Observer, boundExpires: true);

    [Fact]
    public async Task ComposerFixture_ObserverSanitation_InBoundSettlementDeletesOnlyAfterProvenQuiescence() =>
        await RunSanitationRouteAsync(SanitationRoute.Observer, boundExpires: false);

    [Fact]
    public async Task ComposerFixture_ObserverSanitation_InBoundSettlementRetainsStateWhenResourceClosureIsUnproven() =>
        await RunSanitationRouteAsync(SanitationRoute.Observer, boundExpires: false, suppressFirstResourceClose: true);

    // ── Unexpected retained-owner fault matrix ───────────────────────────────────────────────────────────

    private static SanitationRoute RouteOf(string route) => route switch
    {
        "fallback" => SanitationRoute.FallbackTest,
        "observer" => SanitationRoute.Observer,
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, "unknown sanitation route"),
    };

    private static Exception NewUnexpectedDisposalFault() =>
        new InvalidOperationException("INJECTED-UNEXPECTED-AGENT-DISPOSAL-FAULT");

    // fallback / observer  x  in-bound / expired  x  primary / no primary, all through RunSanitationRouteAsync. In each, the
    // additional fault is thrown by the real agent-disposal hook; the premise asserts it reached ComposerAgentService and sits in
    // the real retained owner task next to the expected client fault; the outward assertion then reads the REPORTED outcome.
    [Theory]
    [InlineData("fallback", false, true)]
    [InlineData("fallback", false, false)]
    [InlineData("fallback", true, true)]
    [InlineData("fallback", true, false)]
    [InlineData("observer", false, true)]
    [InlineData("observer", false, false)]
    [InlineData("observer", true, true)]
    [InlineData("observer", true, false)]
    public async Task ComposerFixture_SanitationMatrix_UnexpectedOwnerFaultIsReportedOutward(
        string route, bool boundExpires, bool withPrimary) =>
        await RunSanitationRouteAsync(RouteOf(route), boundExpires, extraDisposalFault: NewUnexpectedDisposalFault(), observerFails: withPrimary);

    [Theory]
    [InlineData("fallback")]
    [InlineData("observer")]
    public async Task ComposerFixture_SanitationMatrix_LookalikeOwnerDiagnosticIsNotSwallowed(string route) =>
        await RunSanitationRouteAsync(RouteOf(route), boundExpires: false, observerFails: false, useLookalikeOwnerDiagnostic: true);

    // Expected-only controls WITHOUT a primary: the intended client fault and its retention diagnostic are not reported as
    // failures (in bound: no failure at all; expired: the deadline only, distinctly).
    [Theory]
    [InlineData("fallback", false)]
    [InlineData("fallback", true)]
    [InlineData("observer", false)]
    [InlineData("observer", true)]
    public async Task ComposerFixture_SanitationMatrix_ExpectedOnlyWithoutAPrimaryReportsNoSpuriousFault(
        string route, bool boundExpires) =>
        await RunSanitationRouteAsync(RouteOf(route), boundExpires, observerFails: false);

    // The EMERGENCY (test-owned, after the assertions) cleanup must also fail loudly on an unexpected owner fault: every
    // outward assertion of the case passes, then the emergency joins the actual retained cleanup and may not discard the leaf.
    [Theory]
    [InlineData("fallback")]
    [InlineData("observer")]
    public async Task ComposerFixture_EmergencyCleanup_UnexpectedOwnerFaultFailsTheCaseInsteadOfBeingDiscarded(string route)
    {
        var extra = NewUnexpectedDisposalFault();
        var failure = await Record.ExceptionAsync(() => RunSanitationRouteAsync(RouteOf(route), boundExpires: false,
            extraDisposalFault: extra, declareExtraToEmergency: false));

        Assert.NotNull(failure); // "passing" assertions must not hide an unexpected owner fault in the emergency join
        var aggregate = Assert.IsType<AggregateException>(failure);
        Assert.Same(extra, Assert.Single(Flattened(aggregate), leaf => ReferenceEquals(leaf, extra)));
        Assert.DoesNotContain(Flattened(aggregate), leaf => leaf is Xunit.Sdk.XunitException);
    }

    // The earlier emergency deadline fires first (HeldThroughExpiry); the FRESH outer attempt then joins the real watcher.
    [Fact]
    public async Task ComposerFixture_OuterCleanup_WatcherStillPendingAtItsDeadlineVetoesClosureAndDeletion() =>
        await RunSanitationRouteAsync(SanitationRoute.Observer, boundExpires: true, emergency: EmergencyDriver.HeldThroughExpiry,
            outerWatcher: OuterWatcher.HeldPending);

    [Fact]
    public async Task ComposerFixture_OuterCleanup_WatcherCompletingBeforeTheExpiredDecisionResumesStillVetoesClosureAndDeletion() =>
        await RunSanitationRouteAsync(SanitationRoute.Observer, boundExpires: true, emergency: EmergencyDriver.HeldThroughExpiry,
            outerWatcher: OuterWatcher.CompletesBeforeDecisionResumes);

    [Fact]
    public async Task ComposerFixture_OuterCleanup_WatcherJoinedInBoundClosesTheResourceAndDeletesOnlyAfterTheJoin() =>
        await RunSanitationRouteAsync(SanitationRoute.Observer, boundExpires: true, emergency: EmergencyDriver.HeldThroughExpiry,
            outerWatcher: OuterWatcher.CompletesInBound);

    [Fact]
    public async Task ComposerFixture_OuterCleanup_UnprovenRescuePreservesPrimaryAndRetainsState()
    {
        Xunit.Sdk.FailException? primary = null;
        var failure = await Assert.ThrowsAsync<Xunit.Sdk.FailException>(() => RunSanitationRouteAsync(
            SanitationRoute.Observer, boundExpires: true, suppressFirstResourceClose: true,
            emergency: EmergencyDriver.HeldThroughExpiry, outerWatcher: OuterWatcher.HeldPending,
            beforeUnprovenRescue: () =>
            {
                try { Assert.Fail("INJECTED-OUTER-WATCHER-ASSERTION"); }
                catch (Xunit.Sdk.FailException ex) { primary = ex; throw; }
            }));
        Assert.Same(primary, failure);
        var rescueFault = Assert.IsType<InvalidOperationException>(Assert.Single(
            Assert.IsType<Exception[]>(failure.Data["SanitationTestCleanupFaults"])));
        Assert.Equal("Sanitation could not prove the client resource closed.", rescueFault.Message);
    }

    // Each RESCUE attempt after the outer attempt requires its own successful, in-bound controller join. The controller is parked
    // after it advanced the outer deadline, so the attempt's own bounded join is genuinely pending on that exact task.
    [Fact]
    public async Task ComposerFixture_RescueCleanup_ControllerStillPendingAtTheFirstRescueDeadlineVetoesClosureAndDeletion() =>
        await RunSanitationRouteAsync(SanitationRoute.Observer, boundExpires: true, emergency: EmergencyDriver.HeldThroughExpiry,
            outerWatcher: OuterWatcher.HeldPending, rescueController: RescueController.HeldAtFirstRescue);

    [Fact]
    public async Task ComposerFixture_RescueCleanup_ControllerStillPendingAtTheSecondRescueDeadlineVetoesClosureAndDeletion() =>
        await RunSanitationRouteAsync(SanitationRoute.Observer, boundExpires: true, emergency: EmergencyDriver.HeldThroughExpiry,
            outerWatcher: OuterWatcher.HeldPending, rescueController: RescueController.HeldAtSecondRescue);

    [Fact]
    public async Task ComposerFixture_RescueCleanup_ControllerSettlingBeforeTheExpiredDecisionResumesStillVetoesClosureAndDeletion() =>
        await RunSanitationRouteAsync(SanitationRoute.Observer, boundExpires: true, emergency: EmergencyDriver.HeldThroughExpiry,
            outerWatcher: OuterWatcher.HeldPending, rescueController: RescueController.SettlesBeforeDecisionResumes);

    [Fact]
    public async Task ComposerFixture_RescueCleanup_ControllerJoinedInBoundClosesOnceAndDeletesOnceAfterTheJoin() =>
        await RunSanitationRouteAsync(SanitationRoute.Observer, boundExpires: true, emergency: EmergencyDriver.HeldThroughExpiry,
            outerWatcher: OuterWatcher.HeldPending, rescueController: RescueController.JoinsInBound);

    [Fact]
    public async Task ComposerFixture_EmergencyCleanup_ExpiredDriverJoinVetoesResourceClosureAndDeletion() =>
        await RunSanitationRouteAsync(SanitationRoute.Observer, boundExpires: true, emergency: EmergencyDriver.HeldThroughExpiry);

    [Fact]
    public async Task ComposerFixture_EmergencyCleanup_DriverReleasedBeforeTheExpiredDecisionResumesStillVetoesClosureAndDeletion() =>
        await RunSanitationRouteAsync(SanitationRoute.Observer, boundExpires: true, emergency: EmergencyDriver.ReleasedAtDecision);

    [Fact]
    public async Task ComposerFixture_EmergencyCleanup_DriverJoinedInBoundClosesTheResourceAndDeletesOnlyAfterTheJoin() =>
        await RunSanitationRouteAsync(SanitationRoute.Observer, boundExpires: true, emergency: EmergencyDriver.JoinsInBound);

    [Fact]
    public async Task ComposerFixture_DirectDisposalSanitation_ExpiredBoundIsFinalEvenWhenTheOwnerSettlesBeforeTheDecisionResumes() =>
        await RunSanitationRouteAsync(SanitationRoute.DirectDisposal, boundExpires: true);

    [Fact]
    public async Task ComposerFixture_DirectDisposalSanitation_InBoundSettlementDeletesOnlyAfterProvenQuiescence() =>
        await RunSanitationRouteAsync(SanitationRoute.DirectDisposal, boundExpires: false);

    // ── Direct route: the scenario assertion is declared by exact reference; unexpected owner faults still surface ──────

    // Cases 1 and 2 are the EXISTING expected-only controls (..._InBoundSettlementDeletesOnlyAfterProvenQuiescence and
    // ..._ExpiredBoundIsFinalEvenWhenTheOwnerSettlesBeforeTheDecisionResumes); they now also assert the outward leaf inventory.

    [Fact]
    public async Task ComposerFixture_DirectDisposalSanitation_InBoundUnexpectedOwnerFaultReachesTheIdenticalObserver() =>
        await RunSanitationRouteAsync(SanitationRoute.DirectDisposal, boundExpires: false, extraDisposalFault: NewUnexpectedDisposalFault());

    [Fact]
    public async Task ComposerFixture_DirectDisposalSanitation_ExpiredUnexpectedOwnerFaultAndGenuineDeadlineAreBothReported() =>
        await RunSanitationRouteAsync(SanitationRoute.DirectDisposal, boundExpires: true, extraDisposalFault: NewUnexpectedDisposalFault());

    // ── Delayed publication, real cleanup SUCCEEDS first ────────────────────────────────────────────

    /// <summary>
    /// Ordinary helper, no probe. Expiry wins while the real cleanup is pending; ONLY publication is held. The real
    /// actor is then released, cleanup succeeds and removes the directory BEFORE the diagnostic is published. The
    /// diagnostic must describe what happened at the deadline and point at the SAME retained task - it must not claim
    /// anything about the directory's current state.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ComposerFixture_ExpiryWinsThenRealCleanupSucceedsBeforePublication_DiagnosticDoesNotClaimTheDirectoryState(bool withPrimary)
    {
        var hold = new ActorLoopHold();
        var actorClock = new ControlledTimeProvider();
        var diagnosticClock = new ControlledTimeProvider();
        var decided = new TaskCompletionSource<(bool ExpiryWon, bool CleanupPending, Task Cleanup)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publicationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? injected = null;
        Exception? callerFailure = null;
        Composer? composer = null;
        string? stateDir = null;
        ComposerFixtureLogger? logger = null;

        var fixtureTask = WithFaultingComposerStatsAsync(async running =>
        {
            composer = running;
            (stateDir, logger) = await ArmHeldActorAsync(running, hold, actorClock);
            if (withPrimary)
                await FailFaultedFixtureScenario(ex => injected = ex);
        }, diagnosticClock: diagnosticClock, onWaitDecided: async (expiryWon, cleanup) =>
        {
            decided.TrySetResult((expiryWon, !cleanup.IsCompleted, cleanup));
            await publicationGate.Task; // holds ONLY the reporting continuation
        });

        await WithFallbackCleanupAsync(hold, fixtureTask, () => callerFailure ?? injected, async () =>
        {
            await actorClock.WaitForPendingTimerAsync(Actor<IComposerMessage>.DisposeTimeout, Ct);
            await diagnosticClock.WaitForPendingTimerAsync(FixtureDiagnosticDeadline, Ct);
            diagnosticClock.Advance(FixtureDiagnosticDeadline);

            var (expiryWon, cleanupPending, cleanup) = await decided.Task.WaitAsync(ControlledTimeProvider.HangGuard, Ct);
            Assert.True(expiryWon, "the diagnostic must have won the wait");
            Assert.True(cleanupPending, "the wait decision must snapshot an incomplete cleanup task");
            var current = Assert.IsType<Composer>(composer);
            Assert.False(cleanup.IsCompleted);
            Assert.False(fixtureTask.IsCompleted, "publication must be held at the checkpoint");
            Assert.False(ActorOf(current).Completion.IsCompleted);
            Assert.True(Directory.Exists(stateDir));
            Assert.Equal("live state", File.ReadAllText(Path.Combine(stateDir!, "sentinel.txt")));

            // Real cleanup finishes and removes the directory while ONLY the diagnostic publication is held.
            hold.Release();
            await cleanup.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
            Assert.True(cleanup.IsCompletedSuccessfully);
            Assert.False(publicationGate.Task.IsCompleted);
            Assert.False(fixtureTask.IsCompleted, "publication must still be held after cleanup succeeded");
            Assert.True(current.TeardownCompletion.IsCompletedSuccessfully);
            Assert.False(Directory.Exists(stateDir), "real cleanup must have deleted the directory before publication");
            Assert.Equal(TimeSpan.Zero, actorClock.Elapsed);

            Assert.True(publicationGate.TrySetResult());
            callerFailure = await Record.ExceptionAsync(() => fixtureTask.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            Assert.NotNull(callerFailure);
            TimeoutException expiry;
            if (withPrimary)
            {
                Assert.Same(injected, callerFailure);
                Assert.IsType<Xunit.Sdk.FailException>(callerFailure);
                expiry = Assert.IsType<TimeoutException>(Assert.Single(Assert.IsType<Exception[]>(
                    callerFailure.Data[ComposerFixtureCleanupFaultsKey])));
            }
            else
            {
                Assert.Null(injected);
                expiry = Assert.IsType<TimeoutException>(callerFailure);
            }

            // WORDING: no claim about the directory (it is already gone); it describes the deadline and directs the
            // caller to the retained task.
            Assert.DoesNotContain("NOT deleted", expiry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("still owns", expiry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("keeps running", expiry.Message, StringComparison.Ordinal);
            Assert.Contains("had not completed when the", expiry.Message, StringComparison.Ordinal);
            Assert.Contains("did not cancel, abandon or short-cut the cleanup", expiry.Message, StringComparison.Ordinal);
            Assert.Contains("await the retained cleanup task", expiry.Message, StringComparison.Ordinal);
            Assert.IsType<TimeoutException>(expiry.InnerException);

            // The identical retained task, recovered from the caller's diagnostic, reports the real SUCCESS.
            var handle = Assert.IsAssignableFrom<Task>(expiry.Data[ComposerFixtureRetainedCleanupKey]);
            Assert.Same(cleanup, handle);
            Assert.True(handle.IsCompletedSuccessfully);
            await handle.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
            Assert.False(Directory.Exists(stateDir));
        }, () => composer, () => stateDir,
            () => decided.Task.IsCompletedSuccessfully ? decided.Task.Result.Cleanup : logger?.RetainedCleanup,
            releaseClient: () => publicationGate.TrySetResult());
    }

    [Fact]
    public async Task ComposerFixture_ObserverFailure_WhenJoinBoundsExpire_ReportsAllPendingJoinsAndRetainsState()
    {
        var hold = new ActorLoopHold();
        var actorClock = new ControlledTimeProvider();
        var diagnosticClock = new ControlledTimeProvider();
        var fallbackClock = new ControlledTimeProvider();
        var stillHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Composer? composer = null;
        string? stateDir = null;
        ComposerFixtureLogger? logger = null;
        var record = new FallbackDecisionRecord();
        var sanitationFaults = new List<Exception>();
        Xunit.Sdk.FailException? observer = null;
        Exception? reported = null;
        Task? fallback = null;
        ExceptionDispatchInfo? primary = null;

        var fixtureTask = WithFaultingComposerStatsAsync(async running =>
        {
            composer = running;
            (stateDir, logger) = await ArmHeldActorAsync(running, hold, actorClock, () =>
            {
                stillHeld.TrySetResult();
                return releaseActor.Task;
            });
        }, diagnosticClock: diagnosticClock);

        try
        {
            fallback = WithFallbackCleanupAsync(hold, fixtureTask, () => null, async () =>
            {
                await actorClock.WaitForPendingTimerAsync(Actor<IComposerMessage>.DisposeTimeout, Ct);
                await diagnosticClock.WaitForPendingTimerAsync(FixtureDiagnosticDeadline, Ct);
                try { Assert.Fail("INJECTED-BOUNDED-FALLBACK-OBSERVER"); }
                catch (Xunit.Sdk.FailException ex) { observer = ex; throw; }
            }, () => composer, () => stateDir, () => logger?.RetainedCleanup,
                decisionRecord: record, fallbackClock: fallbackClock);

            // The fallback released its gate, not the second test-owned gate. Every bound below
            // expires while the actual actor and owner are still pending, not at fixture entry.
            await stillHeld.Task.WaitAsync(ControlledTimeProvider.HangGuard, Ct);
            var current = Assert.IsType<Composer>(composer);
            var waits = new[] { "the fixture task", "the retained cleanup", "real actor completion", "real Composer teardown" };
            for (var i = 0; i < waits.Length; i++)
            {
                await fallbackClock.WaitForTimerCountAsync(i + 1, Ct);
                Assert.False(ActorOf(current).Completion.IsCompleted);
                Assert.False(fixtureTask.IsCompleted);
                Assert.False(logger!.CleanupSettled.Task.IsCompleted);
                Assert.False(current.TeardownCompletion.IsCompleted);
                fallbackClock.Advance(ControlledTimeProvider.HangGuard);
            }

            reported = await Record.ExceptionAsync(() => fallback.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            Assert.NotNull(observer);
            Assert.Same(observer, reported);
            var faults = Assert.IsType<Exception[]>(reported!.Data["FallbackCleanupFaults"]);
            Assert.Equal(waits.Select(wait => $"The bounded fallback wait for {wait} expired."),
                faults.Select(fault => Assert.IsType<FallbackJoinExpiredException>(fault).Message));
            Assert.True(record.Reached);
            Assert.Equal(faults.Select(fault => fault.Message), record.Reasons);
            Assert.False(record.Retried);
            Assert.False(record.DeletionStarted);
            Assert.False(ActorOf(current).Completion.IsCompleted);
            Assert.True(AgentServiceOf(current).IsConnected);
            Assert.Equal(TimeSpan.Zero, actorClock.Elapsed);
            Assert.Equal(TimeSpan.Zero, diagnosticClock.Elapsed);
            Assert.True(Directory.Exists(stateDir));
            Assert.Equal("live state", File.ReadAllText(Path.Combine(stateDir!, "sentinel.txt")));
        }
        catch (Exception ex) { primary = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            hold.Release();
            releaseActor.TrySetResult();
            // Release makes the real dependencies settle even if an assertion failed before
            // the test advanced the fallback clock. Sanitation has independent real-time bounds.
            await SanitizeObserverAsync(composer, stateDir, logger, fixtureTask, fallback,
                null, reported, null, sanitationFaults);
        }

        if (primary is not null)
        {
            if (sanitationFaults.Count > 0)
                primary.SourceException.Data["SanitationFaults"] = sanitationFaults.ToArray();
            primary.Throw();
        }
        if (sanitationFaults.Count > 0)
            throw new AggregateException(sanitationFaults);
        Assert.False(Directory.Exists(stateDir)); // after release, owner succeeded; not part of the retained-state proof
    }

    // ── Direct client-disposal ownership ──────────────────────────────────────

    private static void AssertSentinelIntact(ComposerFixtureProbe probe, string when)
    {
        var sentinel = Path.Combine(probe.StateDir!, "sentinel.txt");
        Assert.True(File.Exists(sentinel), $"the sentinel file must be intact {when}.");
        Assert.Equal("live state", File.ReadAllText(sentinel));
    }

    private static ComposerFixtureProbe NewDirectDisposalProbe(ObservedClientDisposal disposal, bool observeThroughProbe = true)
    {
        ComposerFixtureProbe? probe = null;
        probe = new ComposerFixtureProbe
        {
            // Neither clock is advanced. Actor disposal therefore joins the unheld actor before
            // disposing clients, rather than selecting the real deferred branch on a slow host.
            DiagnosticClock = new ControlledTimeProvider(),
            ClientDisposal = observeThroughProbe ? disposal : null,
            DisposeChatClientAsync = observeThroughProbe ? null : disposal.DisposeAsync,
            AfterConnected = composer =>
            {
                typeof(Actor<IComposerMessage>).GetField("_timeProvider", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(ActorOf(composer), new ControlledTimeProvider());
                File.WriteAllText(Path.Combine(probe!.StateDir!, "sentinel.txt"), "live state");
                return Task.CompletedTask;
            },
        };
        return probe;
    }

    // The direct tests own an actual client resource. Join all started work on failure as well as
    // success, then explicitly close that resource for the known synthetic fault. Never retry
    // Composer here: its references were cleared before the attempted client disposal.
    private static async Task WithDirectDisposalCleanupAsync(ComposerFixtureProbe probe, ObservedClientDisposal disposal,
        Task fixtureTask, Exception? expectedClientFault, Func<Exception?> observedFailure, Func<Task> observe,
        SanitationBounds? bounds = null, Func<Exception?>? scenarioFailure = null)
    {
        ExceptionDispatchInfo? primary = null;
        var fallbackFaults = new List<Exception>();
        try { await observe(); }
        catch (Exception ex) { primary = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            disposal.Release();

            // Every join is attempted even after an earlier bound expired; a bound that WON is recorded and final.
            var boundWon = false;
            async Task JoinAsync(Task work, string operation, Func<Exception, bool>? expected)
            {
                if (!await JoinSanitationWorkAsync(work, operation, bounds, fallbackFaults, expected))
                    boundWon = true;
            }

            // The scenario's own injected failure is a pre-established object the fixture task rethrows: declare it by EXACT
            // REFERENCE (never by message, type or membership in the aggregate being joined). The observer primary is declared the
            // same way; an unrelated owner fault is neither.
            var isExpected = ExpectedSanitationLeaf(() => probe.Cleanup, () => probe.StateDir, expectedClientFault,
                primary?.SourceException, scenarioFailure?.Invoke());
            await JoinAsync(fixtureTask, "the fixture task", isExpected);

            var cleanup = probe.Cleanup;
            if (cleanup is null)
                fallbackFaults.Add(new InvalidOperationException("The direct fixture did not publish retained cleanup."));
            else
                await JoinAsync(cleanup, "the retained cleanup", isExpected);

            var composer = probe.Composer;
            var actorCompletion = composer is null ? null : ActorOf(composer).Completion;
            var teardown = composer?.TeardownCompletion;
            if (actorCompletion is not null)
                await JoinAsync(actorCompletion, "the real actor", null);
            if (teardown is not null)
                await JoinAsync(teardown, "the real teardown", null);

            // Only the injected client step is still owned by the test. A hung or unexpected
            // teardown leaves the directory alone, even if Composer no longer references the client.
            var clientOutcome = disposal.Outcome.Task;
            var directOutcomeKnown = expectedClientFault is null
                ? cleanup is { IsCompletedSuccessfully: true }
                : cleanup?.Exception?.Flatten().InnerExceptions.Contains(expectedClientFault) == true
                    && ReferenceEquals(probe.DirectDisposeFault, expectedClientFault);
            var ownerSettled = !boundWon && fixtureTask.IsCompleted && cleanup is { IsCompleted: true }
                && actorCompletion is { IsCompletedSuccessfully: true } && teardown is { IsCompletedSuccessfully: true }
                && directOutcomeKnown && clientOutcome is { IsCompletedSuccessfully: true }
                && ReferenceEquals(clientOutcome.Result, expectedClientFault) && disposal.Attempts == 1;
            if (ownerSettled)
            {
                try
                {
                    // Also close the resource if a mutation already deleted the path.
                    bounds?.BeforeResourceClose?.Invoke();
                    disposal.CloseTestResource();
                    Assert.True(disposal.ResourceClosed, "The test must close its own client resource before removing state.");
                    if (probe.StateDir is { } dir)
                    {
                        bounds?.BeforeDelete?.Invoke();
                        await CopilotHive.Worker.GitOperations.ForceDeleteDirectoryAsync(dir)
                            .WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
                    }
                }
                catch (Exception ex) { fallbackFaults.Add(ex); }
            }
            else if (Directory.Exists(probe.StateDir))
            {
                var retention = new InvalidOperationException($"Direct test fallback could not establish safety; retained '{probe.StateDir}'.");
                fallbackFaults.Add(retention);
                try { bounds?.OwnRetentionReport?.Invoke(retention); }
                catch (Exception ex) { AddDistinctFault(fallbackFaults, ex); }
            }
        }

        if (primary is not null)
        {
            if (fallbackFaults.Count > 0)
                primary.SourceException.Data["DirectDisposalFallbackFaults"] = fallbackFaults.ToArray();
            primary.Throw();
        }
        if (fallbackFaults.Count > 0)
            throw new AggregateException(fallbackFaults);
    }

    /// <summary>
    /// DIRECT PATH, FAULT: the actor is not held, so <c>Composer.DisposeAsync</c> takes the direct branch and a
    /// SYNCHRONOUS chat-client disposal throws. Teardown and the actor both complete successfully, yet the
    /// fixture must keep the directory: the failed dependent cannot be shown to have recovered.
    /// </summary>
    [Fact]
    public async Task ComposerFixture_DirectClientDisposalFault_RetainsDirectoryAndReportsWhy()
    {
        var injected = new InvalidOperationException("INJECTED-DIRECT-CLIENT-DISPOSE-FAULT");
        var disposal = new ObservedClientDisposal(asynchronous: false, failWith: injected);
        var probe = NewDirectDisposalProbe(disposal);
        var fixtureTask = WithFaultingComposerStatsAsync(_ => Task.CompletedTask, probe);
        Exception? observed = null;

        await WithDirectDisposalCleanupAsync(probe, disposal, fixtureTask, injected, () => observed, async () =>
        {
            observed = await Record.ExceptionAsync(() => fixtureTask.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            Assert.NotNull(observed);
            Assert.True(fixtureTask.IsFaulted && probe.Cleanup is { IsFaulted: true },
                "The fixture and retained cleanup must have settled with the direct fault, not a test timeout.");

            // The ACTUAL attempt and its outcome, observed separately.
            Assert.True(disposal.Attempted.Task.IsCompletedSuccessfully);
            Assert.Equal(1, disposal.Attempts);
            Assert.Same(injected, await disposal.Outcome.Task.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            Assert.False(disposal.ResourceClosed);

            // DIRECT branch, not the deferred logging path: DisposeAsync itself threw the exact exception while
            // the actor and the teardown task both completed successfully.
            Assert.Same(injected, Assert.IsType<InvalidOperationException>(probe.DirectDisposeFault));
            Assert.Contains("ComposerAgentService.DisposeClientsAndClearStateAsync", injected.StackTrace, StringComparison.Ordinal);
            Assert.Contains("Composer.DisposeAsync", injected.StackTrace, StringComparison.Ordinal);
            Assert.True(ActorOf(probe.Composer!).Completion.IsCompletedSuccessfully);
            Assert.True(probe.Composer!.TeardownCompletion.IsCompletedSuccessfully);
            Assert.True(probe.ActorCompletedAtDelete);
            Assert.True(probe.TeardownCompleteAtDelete);
            Assert.False(probe.ClientDisposalSucceededAtDelete);
            var faults = Flattened(observed!).ToArray();
            Assert.Contains(injected, faults);

            // The fixture has settled. Run the sentinel assertion before fallback, even when the
            // old path reports the exact client exception but incorrectly enters deletion.
            AssertSentinelIntact(probe, "after a failed direct client disposal, before any fallback");
            Assert.False(probe.BeforeDeleteEntered.Task.IsCompleted, "deletion must not have started");
            Assert.True(Directory.Exists(probe.StateDir));

            // The original fault is kept, with a separate retention diagnostic beside it.
            var report = Assert.Single(faults.OfType<InvalidOperationException>(),
                fault => fault.Message.Contains("was RETAINED", StringComparison.Ordinal));
            Assert.Contains("direct dependent disposal faulted", report.Message, StringComparison.Ordinal);
            Assert.Contains("INJECTED-DIRECT-CLIENT-DISPOSE-FAULT", report.Message, StringComparison.Ordinal);
            Assert.NotNull(probe.RetainedReason);
        });

        // Only explicit disposal of the test-owned resource authorized this separate fallback.
        Assert.True(disposal.ResourceClosed);
        Assert.Equal(1, disposal.Attempts);
        Assert.False(Directory.Exists(probe.StateDir));
    }

    /// <summary>
    /// DIRECT PATH, SUCCESS CONTROL: client disposal is HELD. While it is pending the fixture must not delete;
    /// only after it completes successfully may the directory go, and the cleanup records that fact at the
    /// moment deletion starts.
    /// </summary>
    [Fact]
    public async Task ComposerFixture_DirectClientDisposalSuccess_DeletesOnlyAfterDisposalCompletes()
    {
        var disposal = new ObservedClientDisposal(asynchronous: true, held: true);
        var probe = NewDirectDisposalProbe(disposal);
        var fixtureTask = WithFaultingComposerStatsAsync(_ => Task.CompletedTask, probe);

        await WithDirectDisposalCleanupAsync(probe, disposal, fixtureTask, null, () => null, async () =>
        {
            await disposal.Attempted.Task.WaitAsync(ControlledTimeProvider.HangGuard, Ct);
            // Timer registration occurs only after the owner publishes the retained cleanup task.
            await ((ControlledTimeProvider)probe.DiagnosticClock).WaitForPendingTimerAsync(probe.DiagnosticDeadline, Ct);
            Assert.False(disposal.Outcome.Task.IsCompleted, "client disposal must still be held");
            Assert.False(disposal.ResourceClosed);
            Assert.False(probe.Cleanup!.IsCompleted, "retained cleanup must remain pending during client disposal");
            Assert.False(fixtureTask.IsCompleted, "the cleanup owner must still be waiting on client disposal");
            Assert.False(probe.BeforeDeleteEntered.Task.IsCompleted, "deletion must not start while client disposal is held");
            Assert.False(probe.DisposeReturned.Task.IsCompleted);
            Assert.False(probe.Composer!.TeardownCompletion.IsCompleted);
            Assert.True(ActorOf(probe.Composer).Completion.IsCompletedSuccessfully);
            var logger = Assert.IsType<ComposerFixtureLogger>(typeof(Composer)
                .GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(probe.Composer));
            Assert.False(logger.DeferredSelected.Task.IsCompleted, "client disposal must be on the direct path");
            AssertSentinelIntact(probe, "while client disposal is held");

            disposal.Release();
            await fixtureTask.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);

            Assert.Null(await disposal.Outcome.Task.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None));
            Assert.Equal(1, disposal.Attempts);
            Assert.True(disposal.ResourceClosed);
            Assert.True(probe.Cleanup!.IsCompletedSuccessfully);
            Assert.Null(probe.DirectDisposeFault);
            Assert.Null(probe.RetainedReason);
            Assert.True(probe.ClientDisposalSucceededAtDelete, "deletion began before client disposal completed successfully");
            Assert.True(probe.BeforeDeleteEntered.Task.IsCompletedSuccessfully);
            Assert.True(probe.Composer.TeardownCompletion.IsCompletedSuccessfully);
            Assert.False(Directory.Exists(probe.StateDir));
        });
    }

    private static void AssertJoinedBeforeDeletion(ComposerFixtureProbe probe)
    {
        Assert.True(probe.Cleanup!.IsCompletedSuccessfully);
        Assert.True(probe.Composer!.TeardownCompletion.IsCompletedSuccessfully);
        Assert.True(ActorOf(probe.Composer).Completion.IsCompletedSuccessfully);
        Assert.True(probe.TeardownCompleteAtDelete, "teardown must be complete when deletion starts");
        Assert.True(probe.ActorCompletedAtDelete, "the actor must be complete when deletion starts");
        Assert.False(Directory.Exists(probe.StateDir));
    }

    [Fact]
    public async Task ComposerFixture_NormalCleanup_JoinsActualTeardownBeforeDeleting_AndKeepsTheStatsFaultSite()
    {
        var probe = new ComposerFixtureProbe();
        var reachedRun = false;
        var agentDisposals = new List<string?>();

        await WithFaultingComposerStatsAsync(composer =>
        {
            reachedRun = true;
            var agentService = AgentServiceOf(composer);
            agentService.OnAgentDisposing = _ => agentDisposals.Add(agentService.Model);
            AssertComposerStatsFault(Assert.Throws<InvalidOperationException>(() => composer.GetStats()));
            Assert.True(Directory.Exists(probe.StateDir));
            return Task.CompletedTask;
        }, probe);

        Assert.True(reachedRun);
        Assert.True(probe.DisposeStarted.Task.IsCompletedSuccessfully);
        Assert.True(probe.JoinStarted.Task.IsCompletedSuccessfully);
        Assert.Equal(FixtureModel, Assert.Single(agentDisposals));
        Assert.Equal(FixtureModel, AgentServiceOf(probe.Composer!).Model);
        AssertJoinedBeforeDeletion(probe);
    }

    private static void AssertComposerStatsFault(InvalidOperationException fault)
    {
        Assert.Equal("Composer connected without a model.", fault.Message);
        AssertFaultOrigin(fault, typeof(Composer), nameof(Composer.GetStats));
    }

    // Fault inside the real capture, after the pool has taken its activity lock. Neither a null
    // DashboardStateService nor an unrelated pipeline failure could prove this boundary.
    private static WorkerPool PoolWithFaultingCapture()
    {
        var pool = new WorkerPool();
        typeof(WorkerPool)
            .GetField("_workers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(pool, null);
        AssertCaptureFault(Assert.Throws<NullReferenceException>(() => pool.CaptureWorkerStatus()));
        return pool;
    }

    private static void AssertCaptureFault(NullReferenceException fault)
    {
        AssertFaultOrigin(fault, typeof(WorkerPool), "CaptureWorkerStatusNoLock");
        Assert.Contains(new StackTrace(fault).GetFrames(), frame =>
            frame.GetMethod() is { } method && method.DeclaringType == typeof(WorkerPool)
            && method.Name == nameof(WorkerPool.CaptureWorkerStatus));
    }

    private static void AssertFaultOrigin(Exception fault, Type expectedType, string expectedMethod)
    {
        var method = new StackTrace(fault).GetFrame(0)?.GetMethod();
        Assert.NotNull(method);
        Assert.Equal(expectedType, method.DeclaringType);
        Assert.Equal(expectedMethod, method.Name);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  1. AUTHORIZATION MATRIX — each of the three reads, each identity, one facade
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ACCEPTANCE CELL: cross EACH of the three reads with Viewer, Member, Operator, Admin,
    /// authenticated null identity, unknown identity, disabled account, and explicit Open mode.
    /// The named theory case is the identity; the three assertions inside are the three reads.
    /// A success is a REAL populated value (never a bare success flag), and each refusal is checked
    /// for the sanitized Forbidden kind, the exact sanitized message and a null value.
    /// </summary>
    [Theory(DisplayName = "Display read authorization identity matrix")]
    [InlineData("Viewer", ViewerId)]
    [InlineData("Member", MemberId)]
    [InlineData("Operator", OperatorId)]
    [InlineData("Admin", AdminId)]
    [InlineData("AuthenticatedNull", "")]
    [InlineData("Unknown", UnknownId)]
    [InlineData("Disabled", ViewerId)]
    [InlineData("ExplicitOpen", "")]
    public async Task DisplayRead_IdentityMatrix_EachReadIsAuthorizedBeforeItsData(string scenario, string gitHubId)
    {
        await SeedAllRolesAsync();
        if (scenario == "Disabled")
            await _auth.SetEnabledAsync(ViewerId, enabled: false);

        var pool = new WorkerPool();
        pool.RegisterWorker(WorkerId, []);
        var store = new InMemoryGoalStore();
        store.AddGoal(GoalWith("matrix-goal", GoalStatus.Draft, BaseTime, iterations: 2));

        var pipelines = new GoalPipelineManager();
        var pipeline = pipelines.CreatePipeline(
            new Goal { Id = "matrix-pipeline", Description = "matrix pipeline" });
        pipeline.ProgressReports.Add(
            Entry(WorkerId, BaseTime.AddMinutes(1), "matrix-progress-goal", "Building", "raw details"));

        using var dashboard = NewDashboard(pool: pool, pipelines: pipelines, goalStore: store);
        var reports = new ProgressReportService(pipelines, new ProgressLog());
        var mode = scenario == "ExplicitOpen" ? HiveAuthMode.Open : HiveAuthMode.AuthenticationEnabled;
        var facade = FacadeFor(mode, dashboard, reports);

        ClaimsPrincipal? caller = scenario switch
        {
            "ExplicitOpen" => OpenUser,
            "AuthenticatedNull" => null,
            _ => User(gitHubId),
        };

        var dashboardRead = await facade.GetDashboardDisplayAsync(caller, Ct);
        var workerRead = await facade.GetWorkerDisplayAsync(caller, Ct);
        var progressRead = await facade.GetWorkerProgressAsync(WorkerId, caller, Ct);

        var expectAllowed = scenario is "Viewer" or "Member" or "Operator" or "Admin" or "ExplicitOpen";

        if (expectAllowed)
        {
            Assert.True(dashboardRead.Success, $"{scenario}: dashboard: {dashboardRead.Error}");
            Assert.Equal(1, dashboardRead.Value!.DraftGoals);
            Assert.Equal("matrix-goal", Assert.Single(dashboardRead.Value.RecentGoals).Id);

            Assert.True(workerRead.Success, $"{scenario}: workers: {workerRead.Error}");
            Assert.Equal(1, workerRead.Value!.TotalWorkers);
            Assert.Equal(WorkerId, Assert.Single(workerRead.Value.Workers).Id);

            Assert.True(progressRead.Success, $"{scenario}: progress: {progressRead.Error}");
            var row = Assert.Single(progressRead.Value!);
            Assert.Equal("Building", row.Status);
            // In explicit Open mode the raw details survive; in authenticated mode they do not.
            Assert.Equal(mode.AuthEnabled ? null : "raw details", row.Details);
        }
        else
        {
            AssertForbidden(dashboardRead, $"{scenario}: dashboard");
            AssertForbidden(workerRead, $"{scenario}: workers");
            AssertForbidden(progressRead, $"{scenario}: progress");
        }
    }

    /// <summary>
    /// ACCEPTANCE CELL: SAME ClaimsPrincipal instance reused across calls, through a PERSISTED
    /// demotion to Viewer (Viewer is STILL ALLOWED for SharedRead on all three reads), then
    /// disablement, then deletion. Also pins that the demotion is a real persisted role change by
    /// resolving it through the production resolver, and that a restore after disablement is allowed
    /// again. The positive controls prove the later refusals are current state, not a broken fixture.
    /// </summary>
    [Fact]
    public async Task DisplayReads_ReuseTheSameClaims_AfterPersistedDemotionDisablementAndDeletion()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);

        var pool = new WorkerPool();
        pool.RegisterWorker(WorkerId, []);
        using var dashboard = NewDashboard(pool: pool, goalStore: new InMemoryGoalStore());
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        // ONE principal instance, reused for every call below.
        var caller = User(AdminId);
        Func<Task<(FacadeResult<DashboardDisplaySnapshot> Dashboard,
                    FacadeResult<WorkerDisplayProjection> Workers,
                    FacadeResult<IReadOnlyList<WorkerProgressDisplayEntry>> Progress)>> readAll = async () =>
            (await facade.GetDashboardDisplayAsync(caller, Ct),
             await facade.GetWorkerDisplayAsync(caller, Ct),
             await facade.GetWorkerProgressAsync(WorkerId, caller, Ct));

        var initial = await readAll();
        Assert.True(initial.Dashboard.Success, initial.Dashboard.Error);
        Assert.True(initial.Workers.Success, initial.Workers.Error);
        Assert.True(initial.Progress.Success, initial.Progress.Error);

        // PERSISTED demotion to the LOWEST role: SharedRead still holds on ALL THREE reads.
        await _auth.SetRoleAsync(AdminId, UserRole.Viewer);
        var resolution = await _auth.Resolver(HiveAuthMode.AuthenticationEnabled)
            .ResolveAsync(caller, Ct);
        Assert.NotNull(resolution.Principal);
        Assert.Equal(UserRole.Viewer, resolution.Principal.Role);

        var demoted = await readAll();
        Assert.True(demoted.Dashboard.Success, demoted.Dashboard.Error);
        Assert.True(demoted.Workers.Success, demoted.Workers.Error);
        Assert.True(demoted.Progress.Success, demoted.Progress.Error);
        Assert.Equal(1, demoted.Workers.Value!.TotalWorkers);

        // DISABLEMENT: the next call with the SAME claims is refused on all three.
        await _auth.SetEnabledAsync(AdminId, enabled: false);
        var disabled = await readAll();
        AssertForbidden(disabled.Dashboard, "disabled dashboard");
        AssertForbidden(disabled.Workers, "disabled workers");
        AssertForbidden(disabled.Progress, "disabled progress");

        // Re-enable: the same claims are allowed again (the refusal was current state).
        await _auth.SetEnabledAsync(AdminId, enabled: true);
        var reenabled = await readAll();
        Assert.True(reenabled.Dashboard.Success, reenabled.Dashboard.Error);
        Assert.True(reenabled.Workers.Success, reenabled.Workers.Error);
        Assert.True(reenabled.Progress.Success, reenabled.Progress.Error);

        // DELETION: the final call with the SAME claims is refused on all three.
        await _auth.DeleteUserAsync(AdminId);
        var deleted = await readAll();
        AssertForbidden(deleted.Dashboard, "deleted dashboard");
        AssertForbidden(deleted.Workers, "deleted workers");
        AssertForbidden(deleted.Progress, "deleted progress");
    }

    /// <summary>
    /// ACCEPTANCE CELL: a RESOLVER FAULT (the user directory itself is unreadable) fails closed as
    /// a sanitized Forbidden with NO value on all three reads, with no data access. The positive
    /// control is an EXPLICIT OPEN-mode facade over the SAME faulted directory: open mode does not
    /// consult the directory at all and all three reads still succeed with real values. That control
    /// proves the fault is real and that the refusals are the resolver's answer.
    /// </summary>
    [Fact]
    public async Task DisplayReads_ResolverDirectoryFault_FailsClosedOnAllThree_WithOpenModePositiveControl()
    {
        await SeedViewerAsync();

        var pool = new WorkerPool();
        pool.RegisterWorker(WorkerId, []);
        var store = new InMemoryGoalStore();
        store.AddGoal(GoalWith("fault-goal", GoalStatus.Draft, BaseTime));
        using var dashboard = NewDashboard(pool: pool, goalStore: store);

        // FAULT THE DIRECTORY through its own disposal latch: context creation is rejected, and the
        // production resolver classifies the resulting read failure as DirectoryFailure.
        _auth.Directory.Dispose();
        var faulted = await _auth.Resolver(HiveAuthMode.AuthenticationEnabled)
            .ResolveAsync(User(ViewerId), Ct);
        Assert.Null(faulted.Principal);
        Assert.Equal(PrincipalRefusalReason.DirectoryFailure, faulted.RefusalReason);

        var authenticated = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);
        AssertForbidden(await authenticated.GetDashboardDisplayAsync(User(ViewerId), Ct), "resolver-fault dashboard");
        AssertForbidden(await authenticated.GetWorkerDisplayAsync(User(ViewerId), Ct), "resolver-fault workers");
        AssertForbidden(await authenticated.GetWorkerProgressAsync(WorkerId, User(ViewerId), Ct), "resolver-fault progress");

        // OPEN MODE POSITIVE CONTROL, over the SAME faulted directory: the resolver never reads it.
        var open = FacadeFor(HiveAuthMode.Open, dashboard);
        var openDashboard = await open.GetDashboardDisplayAsync(OpenUser, Ct);
        var openWorkers = await open.GetWorkerDisplayAsync(OpenUser, Ct);
        var openProgress = await open.GetWorkerProgressAsync(WorkerId, OpenUser, Ct);
        Assert.True(openDashboard.Success, openDashboard.Error);
        Assert.Equal("fault-goal", Assert.Single(openDashboard.Value!.RecentGoals).Id);
        Assert.True(openWorkers.Success, openWorkers.Error);
        Assert.Equal(1, openWorkers.Value!.TotalWorkers);
        Assert.True(openProgress.Success, openProgress.Error);
        Assert.NotNull(openProgress.Value);
    }

    /// <summary>
    /// ACCEPTANCE CELL: CANCELLATION raised by the REAL production resolver's directory read
    /// (an EF context factory that throws OperationCanceledException) propagates out of EVERY one of
    /// the three reads as <see cref="OperationCanceledException"/> — never a sanitized Forbidden,
    /// never a success, and never a data access. The SAME facade with an uncancelled healthy resolver
    /// reads successfully (the positive control), and a resolver DIRECTORY FAILURE (a non-cancellation
    /// exception) on the same seam is refused with Forbidden — so the propagation is specifically
    /// the cancellation channel and not an accidentally-broken fixture.
    /// </summary>
    [Fact]
    public async Task DisplayReads_ResolverCancellation_PropagatesOnAllThree_AndIsNeverADenial()
    {
        await SeedViewerAsync();

        using var dashboard = NewDashboard(goalStore: new InMemoryGoalStore());
        var cancelling = CancellingResolver(HiveAuthMode.AuthenticationEnabled);
        var cancelledFacade = new OperationalFacade(
            dashboard, new LlmSessionRegistry(), cancelling, _auth.Policy,
            NullLogger<OperationalFacade>.Instance, FacadeAuthorizationHarness.EmptyProgressReports());

        var caller = User(ViewerId);
        foreach (var call in new Func<Task<object>>[]
        {
            async () => (await cancelledFacade.GetDashboardDisplayAsync(caller, Ct))!,
            async () => (await cancelledFacade.GetWorkerDisplayAsync(caller, Ct))!,
            async () => (await cancelledFacade.GetWorkerProgressAsync(WorkerId, caller, Ct))!,
        })
        {
            var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(call);
            Assert.IsNotType<InvalidOperationException>(thrown);
            // The resolver's own named cancellation is the one that propagated, so the exception
            // came from the authorization seam and not from a denial or a data source.
            Assert.Equal("CONTRACT_RESOLVER_CANCELLATION", thrown.Message);
        }

        // VIABLE POSITIVE CONTROL: the SAME dashboard with the harness's real resolver reads all
        // three successfully, so the cancellation above is not a broken fixture.
        var healthy = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);
        Assert.True((await healthy.GetDashboardDisplayAsync(caller, Ct)).Success);
        Assert.True((await healthy.GetWorkerDisplayAsync(caller, Ct)).Success);
        Assert.True((await healthy.GetWorkerProgressAsync(WorkerId, caller, Ct)).Success);

        // DISCRIMINATION CONTROL: a non-cancellation directory fault on the SAME seam is a refusal,
        // never a propagation. This pins that the propagation above is cancellation-specific.
        var failing = FaultingResolver(HiveAuthMode.AuthenticationEnabled, new InvalidOperationException("DIR-FAULT"));
        var failedFacade = new OperationalFacade(
            dashboard, new LlmSessionRegistry(), failing, _auth.Policy,
            NullLogger<OperationalFacade>.Instance, FacadeAuthorizationHarness.EmptyProgressReports());
        AssertForbidden(await failedFacade.GetDashboardDisplayAsync(caller, Ct), "directory-fault dashboard");
        AssertForbidden(await failedFacade.GetWorkerDisplayAsync(caller, Ct), "directory-fault workers");
        AssertForbidden(await failedFacade.GetWorkerProgressAsync(WorkerId, caller, Ct), "directory-fault progress");
    }

    /// <summary>
    /// A REAL <see cref="HivePrincipalResolver"/> over a user service whose EVERY read throws the
    /// supplied exception. The resolver's own non-cancellation catch turns a directory fault into a
    /// fail-closed refusal; an <see cref="OperationCanceledException"/> deliberately propagates.
    /// </summary>
    private HivePrincipalResolver FaultingResolver(HiveAuthMode mode, Exception fault)
    {
        var service = new UserService(
            new ThrowingDbContextFactory(fault), NullLogger<UserService>.Instance);
        return new HivePrincipalResolver(service, mode, NullLogger<HivePrincipalResolver>.Instance);
    }

    private HivePrincipalResolver CancellingResolver(HiveAuthMode mode) =>
        FaultingResolver(mode, new OperationCanceledException("CONTRACT_RESOLVER_CANCELLATION"));

    /// <summary>An <see cref="IDbContextFactory{TContext}"/> whose context creation throws a NAMED fault.</summary>
    private sealed class ThrowingDbContextFactory(Exception fault) : IDbContextFactory<global::CopilotHive.Persistence.CopilotHiveDbContext>
    {
        public global::CopilotHive.Persistence.CopilotHiveDbContext CreateDbContext() => throw fault;
    }

    /// <summary>
    /// ACCEPTANCE CELL: sanitized Forbidden with NO value and NO underlying data access, proven with
    /// REACHABLE tripwires. The dashboard's goal store and pipeline manager are faulted; the
    /// progress aggregation's pipeline manager is faulted. Each denied caller stays Forbidden while
    /// the matching AUTHORIZED control reaches the fault and propagates it. The worker display is
    /// the independent control: it succeeds over the SAME faulted goal store and pipeline manager,
    /// because it must read neither.
    /// </summary>
    [Fact]
    public async Task DeniedReads_NeverReachAnyDataSource_WhileEveryAuthorizedControlDoes()
    {
        await SeedViewerAsync();

        var throwingStore = new ThrowingGoalStore();
        var faultedPipelines = FaultedPipelineManager();
        // The dashboard service holds a FAULTED pipeline manager and a THROWING goal store; a
        // separate live pipeline manager backs the aggregation so the progress tripwire is isolated.
        using var dashboard = NewDashboard(pipelines: faultedPipelines, goalStore: throwingStore);

        var faultedReports = new ProgressReportService(FaultedPipelineManager(), new ProgressLog());
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, faultedReports);

        // ── DENIED: all three refused, sanitized, no value, no tripwire escapes. ──
        foreach (var caller in new ClaimsPrincipal?[] { null, User(UnknownId), FacadeAuthorizationHarness.Anonymous() })
        {
            AssertForbidden(await facade.GetDashboardDisplayAsync(caller, Ct), "denied dashboard");
            AssertForbidden(await facade.GetWorkerDisplayAsync(caller, Ct), "denied workers");
            AssertForbidden(await facade.GetWorkerProgressAsync(WorkerId, caller, Ct), "denied progress");
        }
        Assert.Equal(0, throwingStore.ReadCount);

        // ── AUTHORIZED DASHBOARD CONTROL: GetSnapshot captures the worker pool first (healthy here)
        //    and then reads the pipeline manager, so the faulted manager's NullReferenceException is
        //    the observable proof that the read really ran. The worker display below must not reach
        //    it. ──
        await Assert.ThrowsAsync<NullReferenceException>(
            () => facade.GetDashboardDisplayAsync(User(ViewerId), Ct));

        // ── AUTHORIZED WORKERS CONTROL over the SAME faulted store + pipeline manager: VIABLE ──
        //    SUCCESS. This proves the worker-only path does not route through GetSnapshot: that read
        //    would fault at the pipeline manager here, before its goal-store read. The store-only
        //    control below proves that the goal tripwire is reachable, and this fixture has no
        //    Composer or Brain. GetOrchestratorInfo reads neither goals nor pipelines. ──
        var workers = await facade.GetWorkerDisplayAsync(User(ViewerId), Ct);
        Assert.True(workers.Success, workers.Error);
        Assert.NotNull(workers.Value);
        Assert.Empty(workers.Value.Workers);
        Assert.Equal(0, throwingStore.ReadCount); // unchanged: no goal read at all

        // ── AUTHORIZED PROGRESS CONTROL: the faulted aggregation IS reached. ──
        await Assert.ThrowsAsync<NullReferenceException>(
            () => facade.GetWorkerProgressAsync(WorkerId, User(ViewerId), Ct));

        // ── GOAL-STORE TRIPWIRE, isolated from the pipeline fault: with a HEALTHY pipeline manager
        //    the dashboard read reaches the throwing store; the worker display still must not. ──
        using var storeOnlyDashboard = NewDashboard(
            pipelines: new GoalPipelineManager(), goalStore: throwingStore);
        var storeOnlyFacade = FacadeFor(HiveAuthMode.AuthenticationEnabled, storeOnlyDashboard);
        var storeFault = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storeOnlyFacade.GetDashboardDisplayAsync(User(ViewerId), Ct));
        Assert.Equal(ThrowingGoalStore.Tripwire, storeFault.Message);
        Assert.Equal(1, throwingStore.ReadCount);
        foreach (var caller in new ClaimsPrincipal?[] { null, User(UnknownId) })
            AssertForbidden(
                await storeOnlyFacade.GetDashboardDisplayAsync(caller, Ct), "denied dashboard over throwing store");
        Assert.Equal(1, throwingStore.ReadCount); // denials did not read the store

        var workerControl = await storeOnlyFacade.GetWorkerDisplayAsync(User(ViewerId), Ct);
        Assert.True(workerControl.Success, workerControl.Error);
        Assert.Equal(1, throwingStore.ReadCount); // the worker read added NO goal read
    }

    /// <summary>
    /// ACCEPTANCE CELL: UNEXPECTED POST-AUTHORIZATION data faults PROPAGATE rather than becoming
    /// denials — on ALL THREE reads, not only the dashboard one. A denied caller over the SAME
    /// faulted facade receives sanitized Forbidden, so the two outcomes are distinguished by the
    /// AUTHORIZATION answer and not by a universally-failing fixture.
    /// </summary>
    [Fact]
    public async Task UnexpectedPostAuthorizationDataFaults_PropagateOnAllThreeReads()
    {
        await SeedViewerAsync();

        var throwingStore = new ThrowingGoalStore();
        // A HEALTHY pipeline manager here: the dashboard read reaches its goal store, whose named
        // tripwire is the observable post-authorization fault.
        using var dashboard = NewDashboard(pipelines: new GoalPipelineManager(), goalStore: throwingStore);
        var progressLog = new ProgressLog();
        // The progress path reads a DIFFERENT, live aggregation whose global log is faulted at its
        // queue field, so the exception can only come from after the authorization gate.
        typeof(ProgressLog)
            .GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(progressLog, null);
        var reports = new ProgressReportService(new GoalPipelineManager(), progressLog);
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, reports);

        var dashboardFault = await Assert.ThrowsAsync<InvalidOperationException>(
            () => facade.GetDashboardDisplayAsync(User(ViewerId), Ct));
        Assert.Equal(ThrowingGoalStore.Tripwire, dashboardFault.Message);
        AssertForbidden(await facade.GetDashboardDisplayAsync(null, Ct), "denied dashboard over faulted store");

        // The worker display's own data source is healthy (an empty pool); fault its dependency
        // reference so an authorized call proves the post-auth path is really executed.
        var dashboardField = typeof(OperationalFacade)
            .GetField("_dashboard", BindingFlags.Instance | BindingFlags.NonPublic)!;
        dashboardField.SetValue(facade, null);
        try
        {
            await Assert.ThrowsAsync<NullReferenceException>(
                () => facade.GetWorkerDisplayAsync(User(ViewerId), Ct));
            AssertForbidden(await facade.GetWorkerDisplayAsync(null, Ct), "denied workers over faulted dashboard");
        }
        finally
        {
            dashboardField.SetValue(facade, dashboard);
        }

        // The faulted progress log's queue makes GetRecent throw ArgumentNullException. The exact
        // type is not the contract; PROPAGATION (rather than a Forbidden) is.
        var progressFault = await Assert.ThrowsAnyAsync<Exception>(
            () => facade.GetWorkerProgressAsync(WorkerId, User(ViewerId), Ct));
        Assert.IsNotType<InvalidOperationException>(progressFault);
        AssertForbidden(
            await facade.GetWorkerProgressAsync(WorkerId, null, Ct),
            "denied progress over faulted aggregation");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  2. DASHBOARD — exact full-set counts, recent cap/order, detachment
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ACCEPTANCE CELL: MORE THAN TWENTY eligible goals plus NEWER INELIGIBLE goals. Exact IDs and
    /// descending CreatedAt order of the twenty retained rows; omission of older eligible AND all
    /// ineligible rows; all five counts over the FULL set (not the capped set). Also pins the exact
    /// three projected fields on a recent row and the exact seven worker-independent fields on the
    /// snapshot (only BusyWorkers/TotalWorkers, no worker rows or extra worker counts).
    /// </summary>
    [Fact]
    public async Task DashboardDisplay_ExactNewestTwentyIdsDescending_AllFiveFullSetCounts_OnlyTwoWorkerCounts()
    {
        await SeedViewerAsync();

        var store = new InMemoryGoalStore();
        // 23 eligible goals, one per minute; the oldest three are older than every retained row.
        for (var index = 0; index < 23; index++)
            store.AddGoal(GoalWith($"eligible-{index:00}", GoalStatus.Completed, BaseTime.AddMinutes(index),
                iterations: index % 2 == 0 ? index : null));

        // NEWER INELIGIBLE goals: the sort order's head, but excluded from the recent set.
        store.AddGoal(GoalWith("newer-pending", GoalStatus.Pending, BaseTime.AddHours(10)));
        store.AddGoal(GoalWith("newer-active", GoalStatus.InProgress, BaseTime.AddHours(11)));
        store.AddGoal(GoalWith("newer-cancelled", GoalStatus.Cancelled, BaseTime.AddHours(12)));

        var pool = new WorkerPool();
        pool.RegisterWorker("busy-worker", []);
        pool.MarkBusy("busy-worker", "busy-task");
        pool.RegisterWorker("idle-worker", []);

        using var dashboard = NewDashboard(pool: pool, goalStore: store);
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        var result = await facade.GetDashboardDisplayAsync(User(ViewerId), Ct);
        Assert.True(result.Success, result.Error);
        var snapshot = result.Value!;

        // FULL-SET counts: 23 completed, 1 pending, 1 active (the InProgress row), 0 failed,
        // 0 draft — none capped. The Cancelled row is in the full set but is counted by none of the
        // five existing counters, so the sum (25) is deliberately LESS than the 26 seeded rows.
        Assert.Equal(0, snapshot.DraftGoals);
        Assert.Equal(1, snapshot.PendingGoals);
        Assert.Equal(1, snapshot.ActiveGoals);
        Assert.Equal(23, snapshot.CompletedGoals);
        Assert.Equal(0, snapshot.FailedGoals);
        Assert.Equal(25, snapshot.DraftGoals + snapshot.PendingGoals + snapshot.ActiveGoals
            + snapshot.CompletedGoals + snapshot.FailedGoals);

        // EXACT IDs in descending creation-time order, with the three oldest eligible rows dropped
        // and every newer ineligible row omitted.
        var expected = Enumerable.Range(3, 20)
            .Reverse()
            .Select(index => $"eligible-{index:00}")
            .ToArray();
        Assert.Equal(20, DashboardDisplaySnapshot.RecentGoalLimit);
        Assert.Equal(expected, snapshot.RecentGoals.Select(goal => goal.Id));
        Assert.DoesNotContain(snapshot.RecentGoals, goal => goal.Id is "eligible-00" or "eligible-01" or "eligible-02");
        Assert.DoesNotContain(snapshot.RecentGoals,
            goal => goal.Id is "newer-pending" or "newer-active" or "newer-cancelled");

        // Populated AND null iteration counts survive projection.
        Assert.Equal(4, snapshot.RecentGoals.Single(goal => goal.Id == "eligible-04").Iterations);
        Assert.Null(snapshot.RecentGoals.Single(goal => goal.Id == "eligible-05").Iterations);

        // ONLY ID / STATUS / ITERATIONS on a recent row: no CreatedAt, no rich goal data.
        Assert.Equal(
            new[] { "Id", "Iterations", "Status" },
            typeof(DashboardGoalDisplayEntry).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.All(snapshot.RecentGoals, goal => Assert.Equal(GoalStatus.Completed, goal.Status));

        // ONLY two worker numbers, no worker rows, no extra worker counts on the snapshot.
        Assert.Equal(2, snapshot.TotalWorkers);
        Assert.Equal(1, snapshot.BusyWorkers);
        Assert.Equal(
            new[]
            {
                "BusyWorkers", "CompletedGoals", "DraftGoals", "FailedGoals", "PendingGoals",
                "Pipelines", "RecentGoals", "TotalWorkers", "ActiveGoals",
            }.Order(StringComparer.Ordinal),
            typeof(DashboardDisplaySnapshot).GetProperties()
                .Where(p => p.Name != nameof(DashboardDisplaySnapshot.RecentGoalLimit))
                .Select(p => p.Name).Order(StringComparer.Ordinal));

        // PROJECTING DOES NOT MUTATE OR RETAIN SOURCE OBJECTS: the store's live goal instances are
        // unchanged, and mutating one after the read cannot change the already-returned projection.
        var liveGoal = (await store.GetGoalAsync("eligible-22", Ct))!;
        Assert.Equal(GoalStatus.Completed, liveGoal.Status);
        Assert.Equal(22, liveGoal.Iterations);
        var projectedHead = snapshot.RecentGoals[0];
        Assert.Equal("eligible-22", projectedHead.Id);
        liveGoal.Description = "MUTATED AFTER PROJECTION";
        liveGoal.FailureReason = "MUTATED SECRET";
        liveGoal.Status = GoalStatus.Failed;
        Assert.Equal("eligible-22", projectedHead.Id);
        Assert.Equal(GoalStatus.Completed, projectedHead.Status);
        Assert.Equal(22, projectedHead.Iterations);
        Assert.All(snapshot.RecentGoals, row => Assert.DoesNotContain("MUTATED", row.Id));
    }

    /// <summary>
    /// ACCEPTANCE CELL: pipeline metrics AND all three timestamps, INCLUDING CreatedAt when
    /// GoalStartedAt is absent (the elapsed-time fallback), plus a pipeline with an actual start and
    /// completion. Distinctive values discriminate every projected field. The short / exact-80 /
    /// long preview boundaries use a distinctive suffix that must not escape ANY projected string.
    /// </summary>
    [Fact]
    public async Task DashboardDisplay_Pipelines_ProjectMetricsTimestampsCreatedAtFallbackAndOnlyThePreview()
    {
        await SeedViewerAsync();

        const string suffix = "-UNIQUE-FORBIDDEN-SUFFIX-9f31";
        var shortText = "contract short description";
        var exactText = new string('q', 80);
        var longText = new string('z', 80) + suffix;

        var pipelines = new GoalPipelineManager();
        var shortPipeline = pipelines.CreatePipeline(
            new Goal { Id = "short", Description = shortText });
        var exactPipeline = pipelines.CreatePipeline(
            new Goal { Id = "exact", Description = exactText });
        var longPipeline = pipelines.CreatePipeline(
            new Goal { Id = "long", Description = longText });

        // Distinct metric values so no assertion can pass on a default/zero value.
        longPipeline.Metrics.TotalTests = 37;
        longPipeline.Metrics.PassedTests = 31;
        longPipeline.Metrics.FailedTests = 6;
        longPipeline.Metrics.CoveragePercent = 64.25;

        // A POPULATED GoalStartedAt, DISTINCT from CreatedAt, and an iteration GREATER THAN ONE. The
        // pipeline's Iteration is IterationBudget.Used + 1, so two consumed retries make it 3; a
        // constant-1 projection or a constant-null start cannot satisfy the assertions below.
        longPipeline.GoalStartedAt = BaseTime.AddMinutes(37);
        Assert.True(longPipeline.IterationBudget.TryConsume());
        Assert.True(longPipeline.IterationBudget.TryConsume());
        longPipeline.AdvanceTo(GoalPhase.Done);

        // SOURCE PREMISES asserted BEFORE the projection is read, so the assertions below are about a
        // genuinely populated source: a distinct start, a non-null completion and iteration 3.
        Assert.Equal(GoalPhase.Done, longPipeline.Phase);
        Assert.NotNull(longPipeline.CompletedAt); // terminal phase stamps a completion time
        Assert.Equal(3, longPipeline.Iteration);
        Assert.True(longPipeline.Iteration > 1);
        Assert.Equal(BaseTime.AddMinutes(37), longPipeline.GoalStartedAt);
        Assert.NotEqual(longPipeline.CreatedAt, longPipeline.GoalStartedAt);
        // The null-start control's premise: this pipeline really never started.
        Assert.Null(shortPipeline.GoalStartedAt);
        Assert.Null(shortPipeline.CompletedAt);

        using var dashboard = NewDashboard(pipelines: pipelines, goalStore: new InMemoryGoalStore());
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        var snapshot = (await facade.GetDashboardDisplayAsync(User(ViewerId), Ct)).Value!;
        Assert.Equal(3, snapshot.Pipelines.Count);

        var shortRow = snapshot.Pipelines.Single(row => row.GoalId == "short");
        Assert.Equal(shortText, shortRow.DescriptionPreview);

        var exactRow = snapshot.Pipelines.Single(row => row.GoalId == "exact");
        Assert.Equal(exactText, exactRow.DescriptionPreview);
        Assert.Equal(80, exactRow.DescriptionPreview.Length);

        var longRow = snapshot.Pipelines.Single(row => row.GoalId == "long");
        Assert.Equal(new string('z', 80) + "…", longRow.DescriptionPreview);
        Assert.DoesNotContain(suffix, longRow.DescriptionPreview, StringComparison.Ordinal);

        // The distinctive suffix must not escape through ANY projected string of the whole snapshot.
        foreach (var row in snapshot.Pipelines)
        {
            Assert.DoesNotContain(suffix, row.DescriptionPreview, StringComparison.Ordinal);
            Assert.DoesNotContain(suffix, row.GoalId, StringComparison.Ordinal);
            Assert.DoesNotContain(suffix, row.Phase, StringComparison.Ordinal);
        }

        // METRICS and IDENTITY survive exactly.
        Assert.Equal(37, longRow.TotalTests);
        Assert.Equal(31, longRow.PassedTests);
        Assert.Equal(6, longRow.FailedTests);
        Assert.Equal(64.25, longRow.CoveragePercent);
        Assert.Equal(longPipeline.Phase.ToString(), longRow.Phase);

        // THE POPULATED VALUES, asserted EXACTLY against the seeded source — never null-to-null and
        // never against a value of 1 that a default could also produce.
        Assert.Equal(3, longRow.Iteration);
        Assert.Equal(BaseTime.AddMinutes(37), longRow.GoalStartedAt);
        Assert.NotNull(longRow.GoalStartedAt);
        Assert.Equal(longPipeline.GoalStartedAt, longRow.GoalStartedAt);

        // THE TERMINAL COMPLETION, retained: the terminal phase stamped a real completion time.
        Assert.NotNull(longRow.CompletedAt);
        Assert.Equal(longPipeline.CompletedAt, longRow.CompletedAt);
        // No CreatedAt/CompletedAt inequality here: AdvanceTo stamps DateTime.UtcNow, which can share a
        // coarse clock tick with CreatedAt. Independent mapping is proven with test-owned timestamps by
        // DashboardDisplay_TerminalPipeline_ProjectsCreatedAtAndCompletedAtIndependently.

        // THE NULL-START CONTROL: a pipeline that never started projects a null start, a null
        // completion, and the CreatedAt the elapsed display falls back to.
        Assert.Null(shortRow.GoalStartedAt);
        Assert.Null(shortRow.CompletedAt);
        Assert.Equal(shortPipeline.CreatedAt, shortRow.CreatedAt);
        Assert.True(shortRow.CreatedAt > DateTime.MinValue);
        // CreatedAt is projected on EVERY row, so the elapsed fallback always has an input.
        Assert.Equal(exactPipeline.CreatedAt, exactRow.CreatedAt);
        Assert.Equal(longPipeline.CreatedAt, longRow.CreatedAt);

        // The pipeline row's own declared surface has no full Description / Phases / ActiveTaskId.
        var names = typeof(DashboardPipelineDisplayEntry).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain("Description", names);
        Assert.DoesNotContain("Goal", names);
        Assert.DoesNotContain("Phases", names);
        Assert.DoesNotContain("ActiveTaskId", names);
        Assert.Contains("CreatedAt", names);
        Assert.Contains("GoalStartedAt", names);
        Assert.Contains("CompletedAt", names);
        Assert.Contains("DescriptionPreview", names);

        // The facade retains the order of the dashboard snapshot's live pipeline rows.
        Assert.Equal(
            (await dashboard.GetSnapshot()).Pipelines.Select(row => row.GoalId),
            snapshot.Pipelines.Select(row => row.GoalId));
    }

    /// <summary>
    /// DETERMINISTIC TERMINAL TIMESTAMPS: the source pipelines are RESTORED from a snapshot carrying
    /// test-owned CreatedAt/CompletedAt (no wall clock, no sleep, no clock seam), so the two fields are
    /// exactly what the case says. "equal": creation and completion legitimately share one instant — the
    /// premise is asserted, both exact values and a non-null CompletedAt are projected. "distinct":
    /// completion is 5 minutes after creation — the premise is asserted and both exact values are
    /// projected, so substituting CreatedAt for CompletedAt (or the reverse) fails.
    /// </summary>
    [Theory]
    [InlineData("equal", 0)]
    [InlineData("distinct", 5)]
    public async Task DashboardDisplay_TerminalPipeline_ProjectsCreatedAtAndCompletedAtIndependently(
        string scenario, int completedAfterMinutes)
    {
        await SeedViewerAsync();

        var created = BaseTime.AddHours(2);
        var completed = created.AddMinutes(completedAfterMinutes);
        var started = completedAfterMinutes == 0 ? created : created.AddMinutes(1);
        var goalId = $"terminal-{scenario}";
        const string description = "deterministic terminal timestamps";
        var pipelines = new GoalPipelineManager();
        var pipeline = new GoalPipeline(new CopilotHive.Persistence.PipelineSnapshot
        {
            GoalId = goalId,
            Description = description,
            Goal = new Goal { Id = goalId, Description = description },
            Phase = GoalPhase.Done,
            Iteration = 3,
            CreatedAt = created,
            GoalStartedAt = started,
            CompletedAt = completed,
        });
        pipelines.RegisterPipelineInstanceForTest(pipeline.GoalId, pipeline);

        // Prove that the terminal instance is in the exact collection GetSnapshot enumerates.
        Assert.Same(pipeline, Assert.Single(pipelines.GetAllPipelines()));
        Assert.Equal(GoalPhase.Done, pipeline.Phase);
        Assert.Equal(3, pipeline.Iteration);
        Assert.Equal(created, pipeline.CreatedAt);
        Assert.Equal(started, pipeline.GoalStartedAt);
        Assert.Equal(completed, pipeline.CompletedAt);
        if (scenario == "equal")
            Assert.Equal(pipeline.CreatedAt, pipeline.CompletedAt!.Value);
        else
            Assert.NotEqual(pipeline.CreatedAt, pipeline.CompletedAt!.Value);

        using var dashboard = NewDashboard(pipelines: pipelines, goalStore: new InMemoryGoalStore());
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        var result = await facade.GetDashboardDisplayAsync(User(ViewerId), Ct);
        Assert.True(result.Success, result.Error);
        var row = Assert.Single(result.Value!.Pipelines);

        Assert.Equal(goalId, row.GoalId);
        Assert.Equal(created, row.CreatedAt);
        Assert.Equal(started, row.GoalStartedAt);
        Assert.NotNull(row.CompletedAt);
        Assert.Equal(completed, row.CompletedAt!.Value);
        Assert.Equal(3, row.Iteration);
        Assert.Equal(nameof(GoalPhase.Done), row.Phase);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  3. WORKERS — one capture, statuses, config detachment, no other reads
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Four worker status populations make all five aggregate values distinct. The facade's rows,
    /// status labels and counts must agree with that population. A quiet pool cannot distinguish
    /// two equivalent captures, so a separate IL assertion counts direct calls to the real
    /// <c>WorkerPool.CaptureWorkerStatus</c> in <c>DashboardStateService.GetWorkerDisplay</c>.
    /// </summary>
    [Fact]
    public async Task WorkerDisplay_OneCaptureSuppliesRowsAndAllFiveCounts_WithFourDistinctStatusPopulations()
    {
        await SeedViewerAsync();

        var pool = new WorkerPool();
        pool.RegisterWorker("busy", []);
        pool.MarkBusy("busy", "task-busy");
        pool.RegisterWorker("busy-2", []);
        pool.MarkBusy("busy-2", "task-busy-2");
        pool.RegisterWorker("available", []);
        pool.RegisterWorker("available-2", []);
        pool.RegisterWorker("available-3", []);
        var awaiting = pool.RegisterWorker(
            "awaiting", [], requestCompletionReceiptAck: true, completionReceiptAckEnabled: true);
        pool.MarkBusy("awaiting", "task-awaiting");
        Assert.True(pool.TryReleaseCompletedTaskHoldingForPublication(awaiting, "task-awaiting"));
        Assert.True(awaiting.AwaitingWorkerReady);
        var withheld = pool.RegisterWorker("withheld", []);
        withheld.BeginCompletionPublicationHold();
        Assert.False(pool.CaptureWorkerStatus().Single(worker => worker.Id == "withheld").IsAvailable);

        using var dashboard = NewDashboard(pool: pool);
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        var result = await facade.GetWorkerDisplayAsync(User(ViewerId), Ct);
        Assert.True(result.Success, result.Error);
        var projection = result.Value!;

        // Every expected count differs, so swapping aggregate fields cannot pass by coincidence.
        Assert.Equal(7, projection.TotalWorkers);
        Assert.Equal(2, projection.BusyWorkers);
        Assert.Equal(5, projection.IdleWorkers);
        Assert.Equal(3, projection.AvailableWorkers);
        Assert.Equal(1, projection.AwaitingReadyWorkers);
        Assert.Equal(7, projection.Workers.Count);
        Assert.Equal(projection.Workers.Count, projection.TotalWorkers);
        Assert.Equal(projection.Workers.Count(row => row.IsBusy), projection.BusyWorkers);
        Assert.Equal(projection.Workers.Count(row => !row.IsBusy), projection.IdleWorkers);
        Assert.Equal(projection.Workers.Count(row => row.IsAvailable), projection.AvailableWorkers);
        Assert.Equal(projection.Workers.Count(row => row.AwaitingWorkerReady), projection.AwaitingReadyWorkers);
        Assert.Equal(
            new[] { "available", "available-2", "available-3", "awaiting", "busy", "busy-2", "withheld" },
            projection.Workers.Select(row => row.Id).Order(StringComparer.Ordinal));

        // EACH of the four statuses, and the idle/not-busy vs available distinction.
        Assert.Equal("Busy", projection.Workers.Single(row => row.Id == "busy").StatusLabel);
        Assert.Equal("badge-yellow", projection.Workers.Single(row => row.Id == "busy").StatusCssClass);
        Assert.Equal("task-busy", projection.Workers.Single(row => row.Id == "busy").CurrentTaskId);
        Assert.Equal("Available", projection.Workers.Single(row => row.Id == "available").StatusLabel);
        Assert.Equal("badge-green", projection.Workers.Single(row => row.Id == "available").StatusCssClass);

        var awaitingRow = projection.Workers.Single(row => row.Id == "awaiting");
        Assert.False(awaitingRow.IsBusy);
        Assert.False(awaitingRow.IsAvailable);
        Assert.True(awaitingRow.AwaitingWorkerReady);
        Assert.Equal("Awaiting Ready", awaitingRow.StatusLabel);
        Assert.Equal("badge-blue", awaitingRow.StatusCssClass);

        var withheldRow = projection.Workers.Single(row => row.Id == "withheld");
        Assert.False(withheldRow.IsBusy);
        Assert.False(withheldRow.IsAvailable);
        Assert.False(withheldRow.AwaitingWorkerReady);
        Assert.Equal("Unavailable", withheldRow.StatusLabel);
        Assert.Equal("badge-muted", withheldRow.StatusCssClass);
        Assert.NotEqual(awaitingRow.StatusLabel, withheldRow.StatusLabel);

        // COMPILED IL GUARD: exactly ONE real call to WorkerPool.CaptureWorkerStatus in the compiled
        // production method. A duplicate capture cannot hide behind a same-named helper or comment.
        var method = typeof(DashboardStateService).GetMethod(nameof(DashboardStateService.GetWorkerDisplay))!;
        var capture = typeof(WorkerPool).GetMethod(
            "CaptureWorkerStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var body = method.GetMethodBody()!;
        var il = body.GetILAsByteArray()!;
        var captureCalls = 0;
        for (var index = 0; index + 4 < il.Length; index++)
        {
            // call / callvirt (0x28 / 0x6F) followed by a 4-byte metadata token.
            if (il[index] is 0x28 or 0x6F)
            {
                var token = BitConverter.ToInt32(il, index + 1);
                try
                {
                    if (method.Module.ResolveMethod(token) == capture)
                        captureCalls++;
                }
                catch (ArgumentException)
                {
                    // An unrelated token is not a capture call.
                }
            }
        }
        Assert.Equal(1, captureCalls);

        // POSITIVE CONTROL for the IL guard's ABILITY TO DISCRIMINATE: a compiled method that really
        // does capture twice would be counted as two. WorkerPool.GetWorkerStats is the production
        // baseline with exactly one, proving the token resolver found the right method identity;
        // the guard itself is exercised by the mutants in the validation report.
        var statsMethod = typeof(WorkerPool).GetMethod(nameof(WorkerPool.GetWorkerStats))!;
        var statsCalls = 0;
        var statsIl = statsMethod.GetMethodBody()!.GetILAsByteArray()!;
        for (var index = 0; index + 4 < statsIl.Length; index++)
        {
            if (statsIl[index] is 0x28 or 0x6F)
            {
                try
                {
                    if (statsMethod.Module.ResolveMethod(BitConverter.ToInt32(statsIl, index + 1)) == capture)
                        statsCalls++;
                }
                catch (ArgumentException) { }
            }
        }
        Assert.Equal(1, statsCalls);
    }

    /// <summary>
    /// ACCEPTANCE CELL: NO goals, NO pipelines and NO BRAIN statistics — including INDIRECT reads.
    /// A throwing goal store, a faulted pipeline manager and a <c>TrippingBrain</c> whose
    /// <c>GetStats()</c> throws a NAMED tripwire are all installed on the REAL dashboard service.
    /// The worker display succeeds with populated rows, while
    /// <c>GetOrchestratorInfoAsync</c> — the indirect stats path — DOES reach the Brain tripwire, and
    /// <c>GetBrainStatsAsync</c> reaches it directly.
    /// <para>
    /// SCOPE OF THIS ROW — READ BEFORE EXTENDING IT: it installs NO Composer, so the
    /// <c>GetOrchestratorInfoAsync</c> control stops at <c>Brain.GetStats()</c> and never exercises a
    /// Composer site; and its dashboard control faults at the pipeline manager, not at a worker
    /// capture. These controls exercise the Brain-stat paths and GetSnapshot's pipeline read;
    /// the worker display also leaves the goal-store read count at zero. The Composer and capture
    /// sites are proven separately, with faults installed at those sites, by
    /// <see cref="WorkerDisplay_PopulatedRows_OverARealComposerStatsFault_WhileTheControlReachesIt"/>,
    /// <see cref="WorkerProgress_PopulatedOverARealComposerStatsFault_WhileTheControlReachesIt"/>,
    /// <see cref="WorkerProgress_PopulatedOverARealCaptureFault_WhileTheControlsReachIt"/>
    /// and <see cref="DeniedDisplayRead_ReturnsSanitizedForbidden_BeforeReachingTheRealCaptureFault"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WorkerDisplay_ReadsNoGoalsPipelinesOrComposerStats_IncludingIndirectly()
    {
        await SeedViewerAsync();

        var pool = new WorkerPool();
        pool.RegisterWorker("w-1", []);
        pool.MarkBusy("w-1", "task-1");
        pool.RegisterWorker("w-2", []);

        var throwingStore = new ThrowingGoalStore();
        var brain = new TrippingBrain();
        var dashboard = new DashboardStateService(
            pool, FaultedPipelineManager(), new DashboardLogSink(), new ProgressLog(),
            brain: brain, config: new HiveConfigFile(), goalStore: throwingStore);
        using var _ = dashboard;
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        var workers = await facade.GetWorkerDisplayAsync(User(ViewerId), Ct);
        Assert.True(workers.Success, workers.Error);
        Assert.Equal(2, workers.Value!.TotalWorkers);
        Assert.Equal(1, workers.Value.BusyWorkers);
        Assert.Equal("task-1", workers.Value.Workers.Single(row => row.Id == "w-1").CurrentTaskId);
        Assert.Equal(0, throwingStore.ReadCount);
        Assert.Equal(0, brain.StatsReadCount);

        // INDIRECT-READ CONTROL: GetOrchestratorInfoAsync over the SAME fixture reaches the Brain's
        // live statistics; the worker display did not (it must not route through that projection).
        var statsFault = await Assert.ThrowsAsync<InvalidOperationException>(
            () => facade.GetOrchestratorInfoAsync(User(ViewerId), Ct));
        Assert.Equal(TrippingBrain.Tripwire, statsFault.Message);
        Assert.Equal(1, brain.StatsReadCount);

        // DIRECT-READ CONTROL: GetBrainStatsAsync reaches the same tripwire.
        var brainFault = await Assert.ThrowsAsync<InvalidOperationException>(
            () => facade.GetBrainStatsAsync(User(ViewerId), Ct));
        Assert.Equal(TrippingBrain.Tripwire, brainFault.Message);
        Assert.Equal(2, brain.StatsReadCount);

        // DASHBOARD CONTROL: GetSnapshot captures workers first, then reaches the faulted pipeline
        // manager (which precedes its goal-store read, so the store stays unread).
        await Assert.ThrowsAsync<NullReferenceException>(
            () => facade.GetDashboardDisplayAsync(User(ViewerId), Ct));
        Assert.Equal(0, throwingStore.ReadCount);

        // The worker display STILL does not read any of those after both controls fired.
        var again = await facade.GetWorkerDisplayAsync(User(ViewerId), Ct);
        Assert.True(again.Success, again.Error);
        Assert.Equal(2, again.Value!.TotalWorkers);
        Assert.Equal(2, brain.StatsReadCount);
        Assert.Equal(0, throwingStore.ReadCount);
    }

    /// <summary>
    /// ACCEPTANCE CELL: current-model/role fallback, standard and premium matches, PREMIUM
    /// PRECEDENCE when both match, unmatched models, and MISSING or INVALID reasoning effort. Then
    /// MUTATE the source config and worker data AFTER capture: the already-returned rows remain
    /// unchanged (detached rows AND copied config), while a FRESH call observes the mutation.
    /// </summary>
    [Fact]
    public async Task WorkerDisplay_ModelReasoningMatrix_AndDetachedRowsAndConfigAfterSourceMutation()
    {
        await SeedViewerAsync();

        var config = new HiveConfigFile
        {
            Workers = new Dictionary<string, WorkerConfig>
            {
                ["coder"] = new()
                {
                    Model = "model/standard",
                    PremiumModel = "model/premium",
                    ReasoningEffort = "low",
                    PremiumReasoningEffort = "extra_high",
                },
                ["reviewer"] = new()
                {
                    Model = "model/same",
                    PremiumModel = "model/same",
                    ReasoningEffort = "none",
                    PremiumReasoningEffort = "high",
                },
                ["tester"] = new() { Model = "model/no-effort" },                       // missing effort
                ["docwriter"] = new()                                                   // invalid + valid
                {
                    Model = "model/invalid-effort",
                    ReasoningEffort = "not-a-real-effort",
                    PremiumModel = "model/invalid-premium",
                    PremiumReasoningEffort = "also-not-real",
                },
            },
        };

        var pool = new WorkerPool();
        var standard = pool.RegisterWorker("standard", []);
        standard.Role = WorkerRole.Coder;
        standard.CurrentModel = "model/standard";

        var premium = pool.RegisterWorker("premium", []);
        premium.Role = WorkerRole.Coder;
        premium.CurrentModel = "model/premium";

        var fallback = pool.RegisterWorker("fallback", []);   // no current model → role's model
        fallback.Role = WorkerRole.Coder;

        var identical = pool.RegisterWorker("identical", []);  // standard == premium: premium wins
        identical.Role = WorkerRole.Reviewer;
        identical.CurrentModel = "model/same";

        var unmatched = pool.RegisterWorker("unmatched", []); // current model matches neither
        unmatched.Role = WorkerRole.Coder;
        unmatched.CurrentModel = "model/other";

        var noEffort = pool.RegisterWorker("no-effort", []);
        noEffort.Role = WorkerRole.Tester;
        var invalidEffort = pool.RegisterWorker("invalid-effort", []);
        invalidEffort.Role = WorkerRole.DocWriter;
        var invalidPremium = pool.RegisterWorker("invalid-premium", []);
        invalidPremium.Role = WorkerRole.DocWriter;
        invalidPremium.CurrentModel = "model/invalid-premium";

        using var dashboard = NewDashboard(pool: pool, config: config);
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);

        var first = (await facade.GetWorkerDisplayAsync(User(ViewerId), Ct)).Value!;
        Assert.Equal("model/standard", first.Workers.Single(r => r.Id == "standard").DisplayModel);
        Assert.Equal("Low", first.Workers.Single(r => r.Id == "standard").ReasoningLabel);
        Assert.Equal("model/premium", first.Workers.Single(r => r.Id == "premium").DisplayModel);
        Assert.Equal("Extra High", first.Workers.Single(r => r.Id == "premium").ReasoningLabel);
        Assert.Equal("model/standard", first.Workers.Single(r => r.Id == "fallback").DisplayModel);
        Assert.Null(first.Workers.Single(r => r.Id == "fallback").CurrentModel);
        Assert.Equal("High", first.Workers.Single(r => r.Id == "identical").ReasoningLabel);
        Assert.Equal("model/other", first.Workers.Single(r => r.Id == "unmatched").DisplayModel);
        Assert.Equal("—", first.Workers.Single(r => r.Id == "unmatched").ReasoningLabel);
        Assert.Equal("—", first.Workers.Single(r => r.Id == "no-effort").ReasoningLabel);
        Assert.Equal("—", first.Workers.Single(r => r.Id == "invalid-effort").ReasoningLabel);
        Assert.Equal("—", first.Workers.Single(r => r.Id == "invalid-premium").ReasoningLabel);
        Assert.Equal(8, first.TotalWorkers);
        Assert.Equal(8, first.Workers.Count);

        // ── MUTATE THE SOURCES AFTER THE CAPTURE ──
        standard.CurrentModel = "MUTATED-WORKER-MODEL";
        standard.ContextUsagePercent = 91;
        config.Workers["coder"].Model = "MUTATED-CONFIG-MODEL";
        config.Workers["coder"].ReasoningEffort = "high";
        config.Workers["reviewer"].PremiumReasoningEffort = "none";
        config.Workers.Clear(); // also prove the projection retained no live dictionary alias

        // The ALREADY-RETURNED rows and their computed labels are unchanged.
        var retained = first.Workers.Single(r => r.Id == "standard");
        Assert.Equal("model/standard", retained.DisplayModel);
        Assert.Equal("Low", retained.ReasoningLabel);
        Assert.Equal(0, retained.ContextUsagePercent);
        Assert.Equal("High", first.Workers.Single(r => r.Id == "identical").ReasoningLabel);
        Assert.Equal(8, first.TotalWorkers);

        // POSITIVE CONTROL: a FRESH call observes BOTH the worker and the config mutation.
        var fresh = (await facade.GetWorkerDisplayAsync(User(ViewerId), Ct)).Value!;
        Assert.Equal("MUTATED-WORKER-MODEL", fresh.Workers.Single(r => r.Id == "standard").DisplayModel);
        Assert.Equal(91, fresh.Workers.Single(r => r.Id == "standard").ContextUsagePercent);
        Assert.Equal("—", fresh.Workers.Single(r => r.Id == "standard").ReasoningLabel);
        Assert.Equal("—", fresh.Workers.Single(r => r.Id == "fallback").ReasoningLabel);
        // With the config dictionary CLEARED, the role fallback renders the existing
        // "(not configured)" sentinel rather than null (a known role still has a display entry).
        Assert.Equal("(not configured)", fresh.Workers.Single(r => r.Id == "fallback").DisplayModel);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  4. PROGRESS — canonical statuses, cap-first, row discrimination, no reads
    // ══════════════════════════════════════════════════════════════════════════

    private static readonly (string Raw, string Canonical, string GoalId, int Minute)[] ApprovedCases =
    [
        ("rEaDiNg FiLeS", "Reading Files", "goal-reading", 1),
        ("BUILDING", "Building", "goal-building", 2),
        ("testing", "Testing", "goal-testing", 3),
        ("TeStS pAsSiNg", "Tests Passing", "goal-tests-passing", 4),
        ("COMMITTING", "Committing", "goal-committing", 5),
        ("cOmPlEtEd", "Completed", "goal-completed", 6),
        ("FaIlEd", "Failed", "goal-failed", 7),
    ];

    /// <summary>
    /// ACCEPTANCE CELL: each of the seven approved statuses in MIXED CASE → canonical output,
    /// <c>null</c> Details, and EXACT timestamps and goal IDs (one distinct goal id per row, so a
    /// swapped/shifted row fails). Unknown values, leading/trailing whitespace and prefix/suffix
    /// lookalikes are OMITTED ENTIRELY. The premise that the rejected rows really were in the
    /// aggregation is asserted first. Only the four columns are pinned on the DTO.
    /// </summary>
    [Fact]
    public async Task WorkerProgress_EachApprovedStatusMixedCase_CanonicalWithExactIdsAndTimestamps_RejectedRowsOmitted()
    {
        await SeedViewerAsync();

        var pipelines = new GoalPipelineManager();
        var pipeline = pipelines.CreatePipeline(
            new Goal { Id = "goal-projection", Description = "projection goal" });

        foreach (var (raw, _, goalId, minute) in ApprovedCases)
            pipeline.ProgressReports.Add(
                Entry(WorkerId, BaseTime.AddMinutes(minute), goalId, raw, $"SECRET-DETAILS-{minute}"));

        var rejected = new[]
        {
            "unknown phase",        // unknown
            " Reading Files",       // leading whitespace
            "Reading Files ",       // trailing whitespace
            "\tBuilding\t",           // tab padding
            "Building-Extra",       // suffix lookalike
            "Pre-Building",         // prefix lookalike
            "TestsPassing",         // joined-word lookalike
            "COMPLETED.",           // punctuation lookalike
            "",                     // empty
        };
        var stamp = BaseTime.AddMinutes(100);
        foreach (var raw in rejected)
            pipeline.ProgressReports.Add(
                Entry(WorkerId, stamp = stamp.AddMinutes(1), "goal-rejected", raw, $"SECRET-REJECTED-{raw}"));

        var reports = new ProgressReportService(pipelines, new ProgressLog());
        // PREMISE: the source aggregation really contains every raw row, including the near misses.
        var aggregation = reports.GetProgressForWorker(WorkerId);
        Assert.Equal(ApprovedCases.Length + rejected.Length, aggregation.Count);
        foreach (var (raw, _, _, _) in ApprovedCases)
            Assert.Contains(aggregation, entry => entry.Status == raw);
        foreach (var raw in rejected)
            Assert.Contains(aggregation, entry => entry.Status == raw);

        using var dashboard = NewDashboard(pipelines: pipelines);
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, reports);

        var result = await facade.GetWorkerProgressAsync(WorkerId, User(ViewerId), Ct);
        Assert.True(result.Success, result.Error);
        var rows = result.Value!;
        Assert.Equal(ApprovedCases.Length, rows.Count);

        // Exact canonical status, exact goal id, exact timestamp, null details, in the source's order.
        Assert.Equal(ApprovedCases.Select(c => c.Canonical), rows.Select(row => row.Status));
        Assert.Equal(ApprovedCases.Select(c => c.GoalId), rows.Select(row => row.GoalId));
        Assert.Equal(
            ApprovedCases.Select(c => BaseTime.AddMinutes(c.Minute)),
            rows.Select(row => row.Timestamp));
        Assert.All(rows, row => Assert.Null(row.Details));
        Assert.DoesNotContain(rows, row => row.GoalId == "goal-rejected");
        Assert.All(rows, row => Assert.DoesNotContain("SECRET-", row.Status));

        // ONLY the four display columns are pinned on the DTO.
        Assert.Equal(
            new[] { "Details", "GoalId", "Status", "Timestamp" },
            typeof(WorkerProgressDisplayEntry).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// ACCEPTANCE CELL (cap-first discriminator): put a REJECTED row INSIDE the actual 100-row cap
    /// and a VALID row JUST OUTSIDE it. The valid row must NOT back-fill the filtered slot. The
    /// aggregation premise asserts the capped set really contains the rejected row and really omits
    /// the valid one. A control with the valid row inside the cap (and the rejected row outside)
    /// proves the valid row is otherwise projectable.
    /// </summary>
    [Fact]
    public async Task WorkerProgress_CapIsAppliedBeforeFilter_RejectedRowInsideCapDoesNotBackfillValidRowOutside()
    {
        await SeedViewerAsync();

        // SCENARIO A: 99 approved "Started"?? — no: 99 UNAPPROVED rows plus ONE rejected row inside
        // the cap, then ONE APPROVED row at position 101, just outside the cap.
        var pipelines = new GoalPipelineManager();
        var pipeline = pipelines.CreatePipeline(new Goal { Id = "goal-cap", Description = "cap" });
        var stamp = BaseTime;
        for (var index = 0; index < 99; index++)
            pipeline.ProgressReports.Add(
                Entry(WorkerId, stamp = stamp.AddMinutes(1), "goal-older", "Started", $"older-{index}"));
        pipeline.ProgressReports.Add(
            Entry(WorkerId, stamp = stamp.AddMinutes(1), "goal-rejected-inside", " Building", "rejected inside cap"));
        var validOutside = stamp.AddMinutes(1);
        pipeline.ProgressReports.Add(
            Entry(WorkerId, validOutside, "goal-valid-outside", "Building", "valid beyond cap"));

        var reports = new ProgressReportService(pipelines, new ProgressLog());
        var aggregation = reports.GetProgressForWorker(WorkerId);
        Assert.Equal(100, aggregation.Count);
        Assert.Contains(aggregation, e => e.GoalId == "goal-rejected-inside");
        Assert.DoesNotContain(aggregation, e => e.GoalId == "goal-valid-outside");
        Assert.Equal(99, aggregation.Count(e => e.Status == "Started"));

        using var dashboard = NewDashboard(pipelines: pipelines);
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, reports);
        var projected = await facade.GetWorkerProgressAsync(WorkerId, User(ViewerId), Ct);
        Assert.True(projected.Success, projected.Error);
        // NO back-fill: the valid row beyond the cap is not fetched to replace the rejected slot.
        Assert.Empty(projected.Value!);

        // POSITIVE CONTROL: put the SAME approved row INSIDE the cap (swap it with the last rejected
        // row by rebuilding a second manager), and it is returned canonically, with exact values.
        var controlPipelines = new GoalPipelineManager();
        var controlPipeline = controlPipelines.CreatePipeline(
            new Goal { Id = "goal-cap-control", Description = "cap control" });
        stamp = BaseTime;
        for (var index = 0; index < 99; index++)
            controlPipeline.ProgressReports.Add(
                Entry(WorkerId, stamp = stamp.AddMinutes(1), "goal-older", "Started", $"older-{index}"));
        controlPipeline.ProgressReports.Add(
            Entry(WorkerId, stamp = stamp.AddMinutes(1), "goal-valid-inside", "BUILDING", "valid inside cap"));
        var controlReports = new ProgressReportService(controlPipelines, new ProgressLog());
        Assert.Equal(100, controlReports.GetProgressForWorker(WorkerId).Count);
        using var controlDashboard = NewDashboard(pipelines: controlPipelines);
        var controlFacade = FacadeFor(HiveAuthMode.AuthenticationEnabled, controlDashboard, controlReports);
        var control = await controlFacade.GetWorkerProgressAsync(WorkerId, User(ViewerId), Ct);
        Assert.True(control.Success, control.Error);
        var survivor = Assert.Single(control.Value!);
        Assert.Equal("Building", survivor.Status);
        Assert.Equal("goal-valid-inside", survivor.GoalId);
        Assert.Equal(stamp, survivor.Timestamp);
        Assert.Null(survivor.Details);
    }

    /// <summary>
    /// ACCEPTANCE CELL: seed a RETAINED TERMINAL-PIPELINE row, a GLOBAL-ONLY row, a DUPLICATE row
    /// (pipeline + global), and an OTHER-WORKER row — each with DISTINGUISHABLE goal ids, statuses,
    /// details and timestamps. Assert the EXACT order and DEDUPLICATION in authenticated mode, and
    /// RAW status/details parity in explicit Open mode. An authenticated NULL identity gets NO
    /// payload. Neither mode reads worker captures or Composer stats (structural + behavioral).
    /// </summary>
    [Fact]
    public async Task WorkerProgress_TerminalPipelineGlobalDuplicateAndOtherWorkerRows_ExactOrderDedupAndModeParity()
    {
        await SeedViewerAsync();

        var pipelines = new GoalPipelineManager();
        // A TERMINAL pipeline is still collected: GetAllPipelines(), not GetActivePipelines().
        var terminal = pipelines.CreatePipeline(
            new Goal { Id = "goal-terminal", Description = "terminal" });
        terminal.AdvanceTo(GoalPhase.Done);
        Assert.Equal(GoalPhase.Done, terminal.Phase);
        Assert.NotNull(terminal.CompletedAt);

        var t1 = BaseTime.AddMinutes(1);
        var t2 = BaseTime.AddMinutes(2);
        var t3 = BaseTime.AddMinutes(3);
        var t4 = BaseTime.AddMinutes(4);

        // PIPELINE rows: a retained TERMINAL-pipeline row and the pipeline copy of the DUPLICATE.
        terminal.ProgressReports.Add(
            Entry(WorkerId, t1, "goal-terminal", "reading files", "TERMINAL-PIPELINE-DETAILS"));
        terminal.ProgressReports.Add(
            Entry(WorkerId, t3, "goal-duplicate", "TESTING", "PIPELINE-COPY-OF-DUPLICATE"));

        // GLOBAL rows are enqueued through the log's REAL queue with deterministic timestamps, so
        // the four row types have a strict, known order (t1 < t2 < t3 < t4).
        var globalLog = new ProgressLog();
        var queue = (System.Collections.Concurrent.ConcurrentQueue<ProgressEntry>)typeof(ProgressLog)
            .GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(globalLog)!;
        queue.Enqueue(Entry(WorkerId, t2, "goal-global-only", "Committing", "GLOBAL-ONLY-DETAILS"));
        queue.Enqueue(Entry("other-worker", t4, "goal-other-worker", "Failed", "OTHER-WORKER-DETAILS"));
        queue.Enqueue(new ProgressEntry
        {
            Timestamp = t3,
            WorkerId = WorkerId,
            GoalId = "goal-duplicate",
            Status = "TESTING",
            Details = "GLOBAL-COPY-OF-DUPLICATE",
        });

        var globalOnly = globalLog.GetRecent(3).Single(e => e.GoalId == "goal-global-only");
        var otherWorker = globalLog.GetRecent(3).Single(e => e.WorkerId == "other-worker");
        Assert.Equal(t2, globalOnly.Timestamp);
        Assert.Equal(t4, otherWorker.Timestamp);

        var reports = new ProgressReportService(pipelines, globalLog);
        var aggregation = reports.GetProgressForWorker(WorkerId);
        // PREMISE: terminal pipeline row + global-only row + ONE deduplicated copy = 3 rows, in the
        // aggregation's own ascending timestamp order.
        Assert.Equal(3, aggregation.Count);
        Assert.Equal(
            new[] { "goal-terminal", "goal-global-only", "goal-duplicate" },
            aggregation.Select(e => e.GoalId));
        Assert.Equal(new[] { t1, t2, t3 }, aggregation.Select(e => e.Timestamp));
        Assert.Contains(aggregation, e => e.Details == "TERMINAL-PIPELINE-DETAILS");
        Assert.Contains(aggregation, e => e.Details == "GLOBAL-ONLY-DETAILS");
        Assert.Contains(aggregation, e => e.Details == "PIPELINE-COPY-OF-DUPLICATE");
        Assert.DoesNotContain(aggregation, e => e.Details == "GLOBAL-COPY-OF-DUPLICATE");
        Assert.DoesNotContain(aggregation, e => e.WorkerId == "other-worker");
        // The terminal pipeline is retained by the REAL aggregation (GetAllPipelines, not
        // GetActivePipelines): the row above proves it contributed a report.
        Assert.Equal(GoalPhase.Done, terminal.Phase);

        using var dashboard = NewDashboard(pipelines: pipelines);
        var authenticatedFacade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, reports);
        var openFacade = FacadeFor(HiveAuthMode.Open, dashboard, reports);

        // AUTHENTICATED: exact canonical statuses, null details, exact goal ids and timestamps,
        // in the aggregation's own order.
        var projected = await authenticatedFacade.GetWorkerProgressAsync(WorkerId, User(ViewerId), Ct);
        Assert.True(projected.Success, projected.Error);
        Assert.Equal(
            aggregation.Select(e => (e.GoalId, e.Timestamp)),
            projected.Value!.Select(row => (row.GoalId, row.Timestamp)));
        Assert.Equal(
            aggregation.Select(e => WorkerProgressDisplayEntry.CanonicalStatus(e.Status)),
            projected.Value!.Select(row => row.Status));
        Assert.All(projected.Value!, row => Assert.Null(row.Details));
        Assert.Equal(new[] { "goal-terminal", "goal-global-only", "goal-duplicate" },
            projected.Value!.Select(row => row.GoalId));
        Assert.Equal(new[] { t1, t2, t3 }, projected.Value!.Select(row => row.Timestamp));
        Assert.Equal(t1, projected.Value!.Single(row => row.GoalId == "goal-terminal").Timestamp);
        Assert.Equal(t2, projected.Value!.Single(row => row.GoalId == "goal-global-only").Timestamp);
        Assert.Equal(t3, projected.Value!.Single(row => row.GoalId == "goal-duplicate").Timestamp);
        Assert.Equal("Reading Files", projected.Value!.Single(row => row.GoalId == "goal-terminal").Status);
        Assert.Equal("Testing", projected.Value!.Single(row => row.GoalId == "goal-duplicate").Status);
        Assert.Equal("Committing", projected.Value!.Single(row => row.GoalId == "goal-global-only").Status);
        Assert.Equal(3, projected.Value!.Count);
        Assert.DoesNotContain(projected.Value!, row => row.GoalId == "goal-other-worker");
        Assert.All(projected.Value!, row => Assert.DoesNotContain("DETAILS", row.Status));

        // EXPLICIT OPEN MODE: RAW status and details parity with the aggregation, row for row.
        var raw = await openFacade.GetWorkerProgressAsync(WorkerId, OpenUser, Ct);
        Assert.True(raw.Success, raw.Error);
        Assert.Equal(
            aggregation.Select(e => (e.Timestamp, e.GoalId, e.Status, (string?)e.Details)),
            raw.Value!.Select(row => (row.Timestamp, row.GoalId, row.Status, row.Details)));
        Assert.All(raw.Value!, row => Assert.NotNull(row.Details));
        Assert.Contains(raw.Value!, row => row.Details == "GLOBAL-ONLY-DETAILS");
        Assert.Contains(raw.Value!, row => row.Status == "reading files");
        Assert.Contains(raw.Value!, row => row.Status == "TESTING");
        Assert.DoesNotContain(raw.Value!, row => row.Details == "OTHER-WORKER-DETAILS");
        // The other-worker row really exists in the global log (non-vacuous exclusion).
        Assert.Equal("other-worker", otherWorker.WorkerId);
        Assert.Equal("goal-global-only", globalOnly.GoalId);

        // AUTHENTICATED NULL IDENTITY: no payload at all.
        AssertForbidden(
            await authenticatedFacade.GetWorkerProgressAsync(WorkerId, null, Ct),
            "authenticated null-identity progress");
    }

    /// <summary>
    /// ACCEPTANCE CELL: pin ONLY Timestamp/GoalId/Status/Details on the progress DTO and prove the read
    /// does not route through the dashboard's own sources. A faulted pipeline manager on the DASHBOARD
    /// service (which the progress read must not touch) leaves the progress read viable, and the
    /// dashboard read over the same fixture throws (control). The facade's operation body is checked
    /// structurally for the sealed boundary.
    /// <para>
    /// SCOPE OF THIS ROW — READ BEFORE EXTENDING IT: this fixture's WORKER POOL IS HEALTHY, so its
    /// dashboard control faults at the pipeline manager after a worker capture that never failed; it
    /// therefore does NOT prove the progress read avoids a worker capture, and the structural check
    /// below cannot see a capture moved into a helper. The capture claim is proven behaviorally, with
    /// the fault installed INSIDE <c>WorkerPool.CaptureWorkerStatus</c>, by
    /// <see cref="WorkerProgress_PopulatedOverARealCaptureFault_WhileTheControlsReachIt"/>
    /// and <see cref="DeniedDisplayRead_ReturnsSanitizedForbidden_BeforeReachingTheRealCaptureFault"/>.
    /// The Composer claim is proven at the real Composer site by
    /// <see cref="WorkerProgress_PopulatedOverARealComposerStatsFault_WhileTheControlReachesIt"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WorkerProgress_PinsFourColumns_AndReadsNoWorkerCaptureOrComposerStats()
    {
        await SeedViewerAsync();

        var pipelines = new GoalPipelineManager();
        var pipeline = pipelines.CreatePipeline(new Goal { Id = "goal-progress-only", Description = "progress" });
        pipeline.ProgressReports.Add(
            Entry(WorkerId, BaseTime.AddMinutes(1), "goal-exact", "BUILDING", "RAW-DETAILS"));

        // The dashboard service is deliberately faulted at its pipeline manager, and its goal store
        // throws: the progress read must not touch either.
        using var dashboard = NewDashboard(
            pipelines: FaultedPipelineManager(), goalStore: new ThrowingGoalStore());
        var reports = new ProgressReportService(pipelines, new ProgressLog());
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard, reports);

        var result = await facade.GetWorkerProgressAsync(WorkerId, User(ViewerId), Ct);
        Assert.True(result.Success, result.Error);
        var row = Assert.Single(result.Value!);
        Assert.Equal("Building", row.Status);
        Assert.Equal("goal-exact", row.GoalId);
        Assert.Equal(BaseTime.AddMinutes(1), row.Timestamp);
        Assert.Null(row.Details);

        // ONLY four columns on the DTO (no Phase/Iteration/WorkerId/credentials).
        Assert.Equal(
            new[] { "Details", "GoalId", "Status", "Timestamp" },
            typeof(WorkerProgressDisplayEntry).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
        var declared = typeof(WorkerProgressDisplayEntry)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.PropertyType).ToHashSet();
        Assert.DoesNotContain(typeof(Goal), declared);
        Assert.DoesNotContain(typeof(WorkerInfo), declared);

        // POSITIVE CONTROL: the dashboard read over the SAME fixture DOES reach its faulted
        // pipeline manager (GetSnapshot captures workers, then reads pipelines). This distinguishes
        // the progress read from the dashboard's pipeline source only; it is not a worker-capture
        // proof (the pool here is healthy) — the capture-fault cases below own that claim.
        await Assert.ThrowsAsync<NullReferenceException>(
            () => facade.GetDashboardDisplayAsync(User(ViewerId), Ct));

        // Structural guard for the sealed boundary: no worker capture / Composer stats / whole
        // dashboard snapshot in the progress operation.
        var source = ReadProductionSource("src/CopilotHive/Services/OperationalFacade.cs");
        var body = MethodBody(source,
            "public async Task<FacadeResult<IReadOnlyList<WorkerProgressDisplayEntry>>> GetWorkerProgressAsync(");
        Assert.Contains("_progressReports.GetProgressForWorker(workerId)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureWorkerStatus", body, StringComparison.Ordinal);
        Assert.DoesNotContain("GetComposerStats", body, StringComparison.Ordinal);
        Assert.DoesNotContain("GetSnapshot", body, StringComparison.Ordinal);
        Assert.DoesNotContain("GetOrchestratorInfo", body, StringComparison.Ordinal);
    }

    // ── Isolation at the real Composer stats and worker capture sites ────────

    [Fact]
    public async Task WorkerDisplay_PopulatedRows_OverARealComposerStatsFault_WhileTheControlReachesIt()
    {
        await SeedViewerAsync();
        await WithFaultingComposerStatsAsync(async composer =>
        {
            var pool = new WorkerPool();
            pool.RegisterWorker("isolation-worker", []);
            pool.MarkBusy("isolation-worker", "isolation-task");

            // No Brain: it cannot preempt the Composer read in the same-fixture control.
            using var dashboard = NewDashboard(pool: pool, composer: composer);
            var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);
            var caller = User(ViewerId);

            var workers = await facade.GetWorkerDisplayAsync(caller, Ct);
            Assert.True(workers.Success, workers.Error);
            var projection = workers.Value!;
            Assert.Equal(1, projection.TotalWorkers);
            Assert.Equal(1, projection.BusyWorkers);
            Assert.Equal(0, projection.IdleWorkers);
            var row = Assert.Single(projection.Workers);
            Assert.Equal("isolation-worker", row.Id);
            Assert.Equal("isolation-task", row.CurrentTaskId);
            Assert.Equal("Busy", row.StatusLabel);

            AssertComposerStatsFault(await Assert.ThrowsAsync<InvalidOperationException>(
                () => facade.GetOrchestratorInfoAsync(caller, Ct)));

            var again = await facade.GetWorkerDisplayAsync(caller, Ct);
            Assert.True(again.Success, again.Error);
            Assert.Single(again.Value!.Workers);
            AssertForbidden(await facade.GetWorkerDisplayAsync(null, Ct), "denied workers over the Composer fault");
        });
    }

    // An authenticated failure must not prevent the Open case from running (and vice versa).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerProgress_PopulatedOverARealComposerStatsFault_WhileTheControlReachesIt(bool openMode)
    {
        await SeedViewerAsync();
        await WithFaultingComposerStatsAsync(async composer =>
        {
            var timestamp = BaseTime.AddMinutes(17);
            var pipelines = new GoalPipelineManager();
            var pipeline = pipelines.CreatePipeline(
                new Goal { Id = "goal-isolation-exact", Description = "isolation goal" });
            const string rawDetails = "ISOLATION-RAW-DETAILS";
            pipeline.ProgressReports.Add(Entry(WorkerId, timestamp, pipeline.GoalId, "BUILDING", rawDetails));

            // Only Composer is faulted, and Brain is absent. The control uses this mode's facade.
            using var dashboard = NewDashboard(pipelines: pipelines, composer: composer);
            var facade = FacadeFor(openMode ? HiveAuthMode.Open : HiveAuthMode.AuthenticationEnabled,
                dashboard, new ProgressReportService(pipelines, new ProgressLog()));
            var caller = openMode ? OpenUser : User(ViewerId);

            AssertComposerStatsFault(await Assert.ThrowsAsync<InvalidOperationException>(
                () => facade.GetOrchestratorInfoAsync(caller, Ct)));

            var result = await facade.GetWorkerProgressAsync(WorkerId, caller, Ct);
            Assert.True(result.Success, result.Error);
            var row = Assert.Single(result.Value!);
            Assert.Equal(timestamp, row.Timestamp);
            Assert.Equal("goal-isolation-exact", row.GoalId);
            Assert.Equal(openMode ? "BUILDING" : "Building", row.Status);
            Assert.Equal(openMode ? rawDetails : null, row.Details);

            if (!openMode)
                AssertForbidden(await facade.GetWorkerProgressAsync(WorkerId, null, Ct),
                    "authenticated null identity over the Composer fault");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerProgress_PopulatedOverARealCaptureFault_WhileTheControlsReachIt(bool openMode)
    {
        await SeedViewerAsync();
        var timestamp = BaseTime.AddMinutes(23);
        var pipelines = new GoalPipelineManager();
        var pipeline = pipelines.CreatePipeline(new Goal { Id = "goal-capture-exact", Description = "capture isolation" });
        const string rawDetails = "CAPTURE-ISOLATION-DETAILS";
        pipeline.ProgressReports.Add(Entry(WorkerId, timestamp, pipeline.GoalId, "TESTING", rawDetails));

        // Only the pool is faulted. Both controls must reach the capture, not the later pipeline read.
        using var dashboard = NewDashboard(pool: PoolWithFaultingCapture(), pipelines: pipelines);
        var facade = FacadeFor(openMode ? HiveAuthMode.Open : HiveAuthMode.AuthenticationEnabled,
            dashboard, new ProgressReportService(pipelines, new ProgressLog()));
        var caller = openMode ? OpenUser : User(ViewerId);

        AssertCaptureFault(await Assert.ThrowsAsync<NullReferenceException>(
            () => facade.GetDashboardDisplayAsync(caller, Ct)));
        AssertCaptureFault(await Assert.ThrowsAsync<NullReferenceException>(
            () => facade.GetWorkerDisplayAsync(caller, Ct)));

        var result = await facade.GetWorkerProgressAsync(WorkerId, caller, Ct);
        Assert.True(result.Success, result.Error);
        var row = Assert.Single(result.Value!);
        Assert.Equal(timestamp, row.Timestamp);
        Assert.Equal("goal-capture-exact", row.GoalId);
        Assert.Equal(openMode ? "TESTING" : "Testing", row.Status);
        Assert.Equal(openMode ? rawDetails : null, row.Details);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeniedDisplayRead_ReturnsSanitizedForbidden_BeforeReachingTheRealCaptureFault(bool dashboardRead)
    {
        await SeedViewerAsync();
        using var dashboard = NewDashboard(pool: PoolWithFaultingCapture());
        var facade = FacadeFor(HiveAuthMode.AuthenticationEnabled, dashboard);
        var caller = User(ViewerId);

        async Task AssertDeniedAsync(ClaimsPrincipal? identity, string scenario)
        {
            if (dashboardRead)
                AssertForbidden(await facade.GetDashboardDisplayAsync(identity, Ct), $"denied dashboard ({scenario})");
            else
                AssertForbidden(await facade.GetWorkerDisplayAsync(identity, Ct), $"denied workers ({scenario})");
        }

        await AssertDeniedAsync(null, "signed-out");
        await AssertDeniedAsync(User(UnknownId), "unknown");
        await AssertDeniedAsync(FacadeAuthorizationHarness.Anonymous(), "anonymous");
        await _auth.SetEnabledAsync(ViewerId, enabled: false);
        await AssertDeniedAsync(caller, "disabled");
        await _auth.SetEnabledAsync(ViewerId, enabled: true);

        // Same facade and pool: after authorization the real capture must still fault in this read.
        var fault = await Assert.ThrowsAsync<NullReferenceException>(() => dashboardRead
            ? (Task)facade.GetDashboardDisplayAsync(caller, Ct)
            : facade.GetWorkerDisplayAsync(caller, Ct));
        AssertCaptureFault(fault);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  5. REAL DI, EXACT SURFACE, EXISTING SEVEN OPERATIONS
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ACCEPTANCE CELL: REAL DI resolves the facade with the REGISTERED progress aggregator, and the
    /// resolved facade's progress read actually RETURNS a report published through the host's OWN
    /// registered pipeline manager — not merely a same-instance field check. The report is seeded via
    /// the host container's singleton, so a facade wired to a second, empty aggregation would fail.
    /// </summary>
    [Fact]
    public async Task RealHost_ResolvesTheFacadeWithTheRegisteredAggregator_AndReadsItsLiveReports()
    {
        // This row uses its OWN factory so a unique progress report cannot collide with any other
        // integration test's worker id. HiveIntegration is non-parallel.
        using var factory = new HiveTestFactory();
        var services = factory.Services;

        var facade = services.GetRequiredService<IOperationalFacade>();
        Assert.IsType<OperationalFacade>(facade);
        var registered = services.GetRequiredService<ProgressReportService>();
        Assert.Same(registered, services.GetRequiredService<ProgressReportService>());
        var held = typeof(OperationalFacade)
            .GetField("_progressReports", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Same(registered, held.GetValue(facade));

        var pipelines = services.GetRequiredService<GoalPipelineManager>();
        var uniqueGoal = $"contract-di-{Guid.NewGuid():N}";
        var uniqueWorker = $"contract-worker-{Guid.NewGuid():N}";
        var pipeline = pipelines.CreatePipeline(new Goal { Id = uniqueGoal, Description = "DI wiring proof" });
        pipeline.ProgressReports.Add(
            Entry(uniqueWorker, BaseTime.AddMinutes(1), uniqueGoal, "BUILDING", "DI-RAW-DETAILS"));
        try
        {
            // PREMISE: the host's OWN registered aggregation sees the published report.
            var aggregation = registered.GetProgressForWorker(uniqueWorker);
            Assert.Single(aggregation);

            var mode = services.GetRequiredService<HiveAuthMode>();
            var caller = mode.AuthEnabled ? null : OpenUser;

            // In an authentication-enabled host a null identity is refused; in an open host it is
            // the legacy local operator. Either way the facade must be wired to the SAME live data.
            if (!mode.AuthEnabled)
            {
                var result = await facade.GetWorkerProgressAsync(uniqueWorker, caller, Ct);
                Assert.True(result.Success, result.Error);
                var row = Assert.Single(result.Value!);
                Assert.Equal(uniqueGoal, row.GoalId);
                Assert.Equal(BaseTime.AddMinutes(1), row.Timestamp);
                Assert.NotNull(row.Details); // open mode keeps the raw details
            }
            else
            {
                AssertForbidden(
                    await facade.GetWorkerProgressAsync(uniqueWorker, null, Ct),
                    "authenticated null identity on the real host");
            }
        }
        finally
        {
            pipelines.RemovePipeline(uniqueGoal);
        }
    }

    /// <summary>
    /// ACCEPTANCE CELL: pin EXACTLY TEN public operation names and COMPLETE signatures on BOTH
    /// <see cref="IOperationalFacade"/> and the public <see cref="OperationalFacade"/>. The three
    /// new signatures are spelled out with nullable-aware parameter inspection, and an identity-free
    /// overload of any of the three new names is explicitly rejected. The private Composer field
    /// guard is retained: no field of the facade may hold a Composer or its statistics.
    /// </summary>
    [Fact]
    public void ExactTenOperations_CompleteSignaturesOnBothTypes_NoIdentityFreeOverloads_NoPrivateComposer()
    {
        var expected = new[]
        {
            "Task`1[FacadeResult] ResetBrainSessionAsync(ClaimsPrincipal, CancellationToken)",
            "Task`1[FacadeResult`1[BrainStats]] GetBrainStatsAsync(ClaimsPrincipal, CancellationToken)",
            "Task`1[FacadeResult`1[OrchestratorInfo]] GetOrchestratorInfoAsync(ClaimsPrincipal, CancellationToken)",
            "Task`1[FacadeResult`1[IReadOnlyList`1[LlmSessionInfo]]] GetSharedLlmSessionsAsync(ClaimsPrincipal, CancellationToken)",
            "Task`1[FacadeResult`1[KnowledgeDocumentListProjection]] GetKnowledgeDocumentsAsync(String, ClaimsPrincipal, CancellationToken)",
            "Task`1[FacadeResult`1[KnowledgeDocumentProjection]] GetKnowledgeDocumentAsync(String, ClaimsPrincipal, CancellationToken)",
            "Task`1[FacadeResult`1[Int32]] GetKnowledgeDocumentCountAsync(ClaimsPrincipal, CancellationToken)",
            "Task`1[FacadeResult`1[DashboardDisplaySnapshot]] GetDashboardDisplayAsync(ClaimsPrincipal, CancellationToken)",
            "Task`1[FacadeResult`1[WorkerDisplayProjection]] GetWorkerDisplayAsync(ClaimsPrincipal, CancellationToken)",
            "Task`1[FacadeResult`1[IReadOnlyList`1[WorkerProgressDisplayEntry]]] GetWorkerProgressAsync(String, ClaimsPrincipal, CancellationToken)",
        };
        Array.Sort(expected, StringComparer.Ordinal);

        foreach (var type in new[] { typeof(IOperationalFacade), typeof(OperationalFacade) })
        {
            Assert.True(type == typeof(IOperationalFacade) || type.IsPublic, "the concrete facade must be public.");
            var declared = type.GetMethods().Where(m => m.DeclaringType == type).ToArray();
            Assert.Equal(10, declared.Length);
            Assert.Equal(expected,
                declared.Select(DescribeOperation).OrderBy(s => s, StringComparer.Ordinal).ToArray());
            Assert.Equal(10, declared.Select(m => m.Name).ToHashSet(StringComparer.Ordinal).Count);

            // NO identity-free overload of ANY operation, and specifically of the three new names.
            foreach (var method in declared)
            {
                Assert.Contains(typeof(ClaimsPrincipal), method.GetParameters().Select(p => p.ParameterType));
                Assert.Contains(typeof(CancellationToken), method.GetParameters().Select(p => p.ParameterType));
            }
            foreach (var name in new[]
            {
                nameof(IOperationalFacade.GetDashboardDisplayAsync),
                nameof(IOperationalFacade.GetWorkerDisplayAsync),
                nameof(IOperationalFacade.GetWorkerProgressAsync),
            })
            {
                Assert.Single(declared, m => m.Name == name);
            }

            // The THREE NEW signatures, complete with nullable-annotated acting identity and
            // exact return value types (verified independently of the string rendering above).
            foreach (var (name, extraParameters, valueType) in new (string, Type[], Type)[]
            {
                (nameof(IOperationalFacade.GetDashboardDisplayAsync), [], typeof(DashboardDisplaySnapshot)),
                (nameof(IOperationalFacade.GetWorkerDisplayAsync), [], typeof(WorkerDisplayProjection)),
                (nameof(IOperationalFacade.GetWorkerProgressAsync), [typeof(string)], typeof(IReadOnlyList<WorkerProgressDisplayEntry>)),
            })
            {
                var method = declared.Single(m => m.Name == name);
                var parameters = method.GetParameters();
                Assert.Equal(extraParameters.Concat([typeof(ClaimsPrincipal), typeof(CancellationToken)]),
                    parameters.Select(p => p.ParameterType));
                Assert.True(
                    new NullabilityInfoContext().Create(parameters[^2]).ReadState == NullabilityState.Nullable,
                    $"'{name}' actingUser must be nullable-annotated ClaimsPrincipal?.");
                Assert.Equal(typeof(Task<>).MakeGenericType(typeof(FacadeResult<>).MakeGenericType(valueType)),
                    method.ReturnType);
            }
        }

        // RETAINED PRIVATE COMPOSER SURFACE GUARD: no field holds a Composer, its stats or a live
        // credential-bearing principal; the facade's fields are only its six required dependencies.
        var fieldTypes = typeof(OperationalFacade)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(f => f.FieldType).ToHashSet();
        Assert.DoesNotContain(typeof(Composer), fieldTypes);
        Assert.DoesNotContain(typeof(BrainStats), fieldTypes);
        Assert.DoesNotContain(typeof(ClaimsPrincipal), fieldTypes);
        Assert.Contains(typeof(ProgressReportService), fieldTypes);
        Assert.Contains(typeof(HivePrincipalResolver), fieldTypes);
    }

    /// <summary>
    /// ACCEPTANCE CELL: the SEVEN EXISTING operations are still present and behave unchanged —
    /// including <see cref="IOperationalFacade.GetOrchestratorInfoAsync"/>, which still delegates to
    /// the unchanged <see cref="DashboardStateService.GetOrchestratorInfo"/> and still refuses a
    /// signed-out caller. Every seven-operation call is made through the SAME facade instance.
    /// </summary>
    [Fact]
    public async Task TheSevenExistingOperations_ArePreserved_IncludingGetOrchestratorInfoBehavior()
    {
        await SeedViewerAsync();
        var config = new HiveConfigFile
        {
            Workers = new Dictionary<string, WorkerConfig>
            {
                ["coder"] = new() { Model = "contract/coder", ReasoningEffort = "medium" },
            },
        };
        var registry = new LlmSessionRegistry();
        registry.RegisterOrUpdate(new LlmSessionInfo
        {
            SessionId = "brain-shared", SessionType = LlmSessionType.Brain,
            Model = "brain-model", Status = "active", MaxTokens = 1,
        });
        registry.RegisterOrUpdate(new LlmSessionInfo
        {
            SessionId = "composer-private", SessionType = LlmSessionType.Composer,
            Model = "composer-model", Status = "active", MaxTokens = 1,
        });

        using var dashboard = NewDashboard(config: config, goalStore: new InMemoryGoalStore());
        var facade = new OperationalFacade(
            dashboard, registry, _auth.Resolver(HiveAuthMode.AuthenticationEnabled), _auth.Policy,
            NullLogger<OperationalFacade>.Instance, FacadeAuthorizationHarness.EmptyProgressReports());

        var info = await facade.GetOrchestratorInfoAsync(User(ViewerId), Ct);
        Assert.True(info.Success, info.Error);
        var serviceAnswer = dashboard.GetOrchestratorInfo();
        Assert.Equal(serviceAnswer.Version, info.Value!.Version);
        Assert.Equal("contract/coder", info.Value.RoleModels["coder"]);
        Assert.Equal(serviceAnswer.RoleReasoningEfforts["coder"], info.Value.RoleReasoningEfforts["coder"]);
        AssertForbidden(await facade.GetOrchestratorInfoAsync(null, Ct), "signed-out orchestrator info");

        var brain = await facade.GetBrainStatsAsync(User(ViewerId), Ct);
        Assert.True(brain.Success, brain.Error);

        var sessions = await facade.GetSharedLlmSessionsAsync(User(ViewerId), Ct);
        Assert.True(sessions.Success, sessions.Error);
        Assert.Equal(["brain-shared"], sessions.Value!.Select(s => s.SessionId));

        var documents = await facade.GetKnowledgeDocumentsAsync(null, User(ViewerId), Ct);
        Assert.True(documents.Success, documents.Error);
        var one = await facade.GetKnowledgeDocumentAsync("missing", User(ViewerId), Ct);
        Assert.Equal(FacadeErrorKind.NotFound, one.Kind);
        var count = await facade.GetKnowledgeDocumentCountAsync(User(ViewerId), Ct);
        Assert.True(count.Success, count.Error);
        Assert.Equal(0, count.Value);

        var reset = await facade.ResetBrainSessionAsync(User(ViewerId), Ct);
        Assert.Equal(FacadeErrorKind.Forbidden, reset.Kind); // Viewer still lacks ExecutionOperator.
    }

    private static string DescribeOperation(MethodInfo method) =>
        $"{DescribeType(method.ReturnType)} {method.Name}("
        + string.Join(", ", method.GetParameters().Select(p => DescribeType(p.ParameterType))) + ")";

    private static string DescribeType(Type type) => type.IsGenericType
        ? $"{type.GetGenericTypeDefinition().Name}[{string.Join(", ", type.GetGenericArguments().Select(DescribeType))}]"
        : type.Name;

    // ── Source-text helpers (scoped structural guards only) ──────────────────

    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' was not found.");
        var open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"'{signature}' has no body.");
        var depth = 0;
        for (var index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[(open + 1)..index];
        }
        Assert.Fail($"'{signature}' is never closed.");
        return null!;
    }

    private static string ReadProductionSource(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        Assert.Fail($"'{relative}' was not found from '{AppContext.BaseDirectory}'.");
        return null!;
    }

    public void Dispose() => _auth.Dispose();
}

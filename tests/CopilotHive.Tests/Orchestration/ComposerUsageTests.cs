using System.Reflection;
using System.Runtime.CompilerServices;

using CopilotHive.Actors;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using SharpCoder;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// The Composer usage-measurement contract of
/// <see cref="Composer.GetUsage"/> / <see cref="ComposerUsageSnapshot"/>:
/// <list type="bullet">
///   <item><b>(a)</b> a multi-round chat turn's per-execution sum lands in <c>LastTurn</c> and is
///   also accumulated in <c>Session</c>, while <c>GetStats().CumulativeInputTokens</c> is the sum
///   over ALL calls of the session, not the last round's value;</item>
///   <item><b>(b)</b> an ERROR-result turn still reports its usage;</item>
///   <item><b>(c)</b> <c>LastTurn</c> is cleared when a new stream starts and stays null until that
///   turn completes;</item>
///   <item><b>(d)</b> clarification answers accumulate in <c>Clarifications</c> only — the forked
///   session's usage never reaches <c>Session</c> — and escalations and errors are counted too;</item>
///   <item><b>(e)</b> MANUAL compactions accumulate in <c>ManualCompactions</c> and are the same
///   calls the compactor records into <c>Session</c>, while automatic compaction inside a turn is
///   NOT counted as manual;</item>
///   <item><b>(f)</b> <c>GetUsage()</c> is null when disconnected and every member of the returned
///   record is a detached copy;</item>
///   <item><b>(g)</b> <c>Session</c> is restored from <c>composer-session.json</c> on reconnect while
///   the in-memory <c>Clarifications</c>/<c>ManualCompactions</c> start empty;</item>
///   <item><b>(h)</b> <see cref="PhaseUsageTotals.Merge"/> sums every counter, keeps ordinal model
///   buckets, skips nulls and never mutates its inputs.</item>
/// </list>
/// <para>
/// <b>Determinism.</b> Every model call is answered by a scripted <see cref="IChatClient"/> with
/// FIXED token counts, and stream completion is observed through the facade's own
/// <see cref="Composer.OnStreamingUpdate"/> event (fired by the actor on the terminal transition) or
/// through an explicit <see cref="TaskCompletionSource"/> gate held inside the client — never by a
/// wall-clock delay. A stream that must be observed mid-flight is held by that gate, so the test can
/// assert the in-flight state before releasing it.
/// </para>
/// </summary>
public sealed class ComposerUsageTests
{
    private const BindingFlags PrivateFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>The streaming model id reported by <see cref="UsageReportingClient"/>.</summary>
    private const string ModelId = "usage-measure-model";

    /// <summary>Input tokens reported for EVERY streaming (agent) call.</summary>
    private const long StreamInputPerCall = 1_000;

    /// <summary>Output tokens reported for EVERY streaming (agent) call.</summary>
    private const long StreamOutputPerCall = 100;

    /// <summary>Cached-input tokens reported for every streaming call (the category IS reported).</summary>
    private const long StreamCachedPerCall = 25;

    /// <summary>Reasoning tokens reported for every streaming call (the category IS reported).</summary>
    private const long StreamReasoningPerCall = 5;

    /// <summary>Input tokens reported for every compaction (non-streaming) call.</summary>
    private const long CompactionInputPerCall = 4_000;

    /// <summary>Output tokens reported for every compaction (non-streaming) call.</summary>
    private const long CompactionOutputPerCall = 400;

    /// <summary>
    /// Hang-guard bound for the deterministic drains below. Every awaited signal in a PASSING run is
    /// an already-deterministic milestone (the held stream is released first, so it cannot block),
    /// so the passing outcome never depends on this bound — it only keeps a genuinely stranded run
    /// from hanging the suite, and a run that hits it is recorded as a <c>*-timeout</c> step and
    /// FAILS. Tests prove "no bound decided anything" by asserting the recorded milestone steps, never
    /// by measuring elapsed time; the failed-join path is exercised deterministically through
    /// <c>Fixture.AbandonJoins</c>, not through this bound.
    /// </summary>
    private static readonly TimeSpan DrainBound = TimeSpan.FromSeconds(15);

    // ── Fixture ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One recorded teardown step, with the state observed at that moment — lets tests prove the
    /// ORDER of teardown (resources are released only after the outstanding work joined).
    /// </summary>
    internal sealed record TeardownStep(string Name, bool IsStreaming, bool ActorCompleted, bool FixtureReportedDisposed);

    /// <summary>
    /// Owns every resource a connected test needs and tears it down in the ONE order that cannot
    /// strand work: release the client's hold, join the outstanding streaming turn, dispose the
    /// Composer, join the actor's message loop, and only then dispose the database and delete the
    /// state directory.
    /// <para>
    /// <b>Race-free drain.</b> The completion observer is registered BEFORE the streaming state is
    /// read (<see cref="ArmStreamCompletion"/>), and a not-streaming read reconciles it as already
    /// completed — so a terminal transition that fires between the release and the read can never be
    /// missed, and a naturally finishing stream is joined by its signal, never by
    /// <see cref="DrainBound"/>.
    /// </para>
    /// <para>
    /// <b>Cancellation-independent joins.</b> No teardown wait observes the test's cancellation token:
    /// a cancelled test must still join its outstanding work and finish cleanup. Every wait is bounded
    /// only by the self-contained <see cref="DrainBound"/> safety guard.
    /// </para>
    /// <para>
    /// <b>One disposal, joined by every caller.</b> The first <see cref="DisposeAsync"/> starts the
    /// teardown task and RETAINS it; every later caller (an explicit failure-path call, then
    /// <c>await using</c>) receives that same Task instance instead of returning early, so no caller
    /// can observe "disposed" while teardown is still running. <see cref="IsDisposed"/> becomes true
    /// only after every step — gate released, drain joined, Composer and actor joined, database
    /// disposed, directory deleted — succeeded.
    /// </para>
    /// <para>
    /// <b>Contained steps.</b> Each step runs even if an earlier one failed; failures are collected
    /// and re-thrown together only after all cleanup ran, so one failure can never skip the rest.
    /// </para>
    /// <para>
    /// <b>Dependent resources are never destroyed under live work.</b> The database and the state
    /// directory are released only once the Composer's disposal AND the actor's message loop have
    /// both completed. If either join fails (safety bound, or a test-controlled
    /// <see cref="AbandonJoins"/> signal), the failure is still recorded and thrown, but the
    /// dependent cleanup is DEFERRED: a <c>dependent-cleanup-deferred</c> step is recorded (capturing
    /// <c>ActorCompleted == false</c>), and a continuation releases the database and directory only
    /// after the actor really completes. <see cref="DependentResourcesReleased"/> completes at that
    /// point, so a failed test still ends leak-free once its outstanding work finishes. The only
    /// intentionally-leaked case is an actor that NEVER completes: then the database and directory
    /// stay alive for the rest of the test process by design, because destroying them under a live
    /// loop is exactly the hazard this fixture exists to prevent.
    /// </para>
    /// </summary>
    private sealed class Fixture(
        Composer composer,
        CopilotHiveDbContext db,
        string stateDir,
        UsageReportingClient client) : IAsyncDisposable
    {
        private readonly object _disposeLock = new();
        private readonly List<TeardownStep> _teardown = [];
        private readonly List<(string Name, TaskCompletionSource Waiter)> _stepWaiters = [];
        private readonly TaskCompletionSource _dependentReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _disposal;
        private volatile bool _disposedCleanly;

        internal Composer Composer { get; } = composer;
        internal CopilotHiveDbContext Db { get; } = db;
        internal string StateDir { get; } = stateDir;
        internal UsageReportingClient Client { get; } = client;

        /// <summary>
        /// Test-controlled join-failure seam. When set, every teardown join that has not ALREADY
        /// completed races this signal instead of the wall-clock safety bound, and is recorded as
        /// <c>*-abandoned</c> if the signal wins. Pre-completing it makes the failed-join path
        /// reachable deterministically — no elapsed time involved. <c>null</c> (the default) means
        /// joins are bounded only by <see cref="DrainBound"/>.
        /// </summary>
        internal TaskCompletionSource? AbandonJoins { get; set; }

        /// <summary>
        /// True only once teardown has FINISHED successfully (gate released, outstanding turn joined,
        /// actor joined, database disposed, directory deleted). Never set early, and never set on a
        /// path where a join failed.
        /// </summary>
        internal bool IsDisposed => _disposedCleanly;

        /// <summary>
        /// Completes once the database is disposed and the state directory deleted — immediately
        /// within a healthy teardown, or later (after the actor really completes) when the cleanup had
        /// to be deferred.
        /// </summary>
        internal Task DependentResourcesReleased => _dependentReleased.Task;

        /// <summary>The recorded teardown steps, in order (a snapshot).</summary>
        internal IReadOnlyList<TeardownStep> Teardown { get { lock (_teardown) { return [.. _teardown]; } } }

        /// <summary>
        /// Completes when a teardown step with the given name has been recorded (immediately if it
        /// already was) — a deterministic milestone signal for tests, no polling.
        /// </summary>
        internal Task StepRecorded(string name)
        {
            lock (_teardown)
            {
                if (_teardown.Any(s => s.Name == name)) return Task.CompletedTask;
                var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _stepWaiters.Add((name, waiter));
                return waiter.Task;
            }
        }

        public ValueTask DisposeAsync()
        {
            Task disposal;
            lock (_disposeLock)
            {
                // Every caller joins the SAME retained teardown task: a later call (e.g. `await using`
                // after an explicit failure-path dispose) awaits the running/finished teardown instead
                // of returning early. The ValueTask wraps that Task, so AsTask() hands every caller
                // the identical Task instance.
                _disposal ??= TeardownAsync();
                disposal = _disposal;
            }

            return new ValueTask(disposal);
        }

        private void Record(string step)
        {
            var actor = GetActor(Composer);
            List<TaskCompletionSource> ready = [];
            lock (_teardown)
            {
                _teardown.Add(new TeardownStep(step, Composer.IsStreaming, actor.IsCompleted, _disposedCleanly));
                for (var i = _stepWaiters.Count - 1; i >= 0; i--)
                {
                    if (_stepWaiters[i].Name != step) continue;
                    ready.Add(_stepWaiters[i].Waiter);
                    _stepWaiters.RemoveAt(i);
                }
            }

            foreach (var waiter in ready) waiter.TrySetResult();
        }

        /// <summary>
        /// Joins <paramref name="target"/>. Returns <c>null</c> when it completed (rethrowing its own
        /// failure, if any), otherwise the failure kind: <c>"abandoned"</c> when the test-controlled
        /// <see cref="AbandonJoins"/> signal won, <c>"timeout"</c> when the safety bound did.
        /// </summary>
        private async Task<string?> JoinAsync(Task target)
        {
            if (target.IsCompleted)
            {
                await target;
                return null;
            }

            if (AbandonJoins is { } abandon)
            {
                var winner = await Task.WhenAny(target, abandon.Task);
                if (winner != target) return "abandoned";
                await target;
                return null;
            }

            try
            {
                await target.WaitAsync(DrainBound);
                return null;
            }
            catch (TimeoutException)
            {
                return "timeout";
            }
        }

        private async Task TeardownAsync()
        {
            // Make the teardown asynchronous from the very first step, so a synchronous caller never
            // runs it inline while holding the dispose lock.
            await Task.Yield();

            var errors = new List<Exception>();
            var actor = GetActor(Composer);

            // 1. Arm the completion observer BEFORE anything else can change the streaming state, then
            //    release. Arming first is what makes the drain race-free: the observer is in place
            //    before the release lets the stream finish, and before the state read.
            var drain = ArmStreamCompletion(Composer);
            try { Client.ReleaseGate(); Record("gate-released"); }
            catch (Exception ex) { errors.Add(ex); }

            // 2. JOIN the outstanding turn by its own completion signal. No test token: a cancelled
            //    test must still join. The bound is a safety guard only.
            var drainFailure = await JoinAsync(drain);
            if (drainFailure is null)
            {
                Record("drain-joined");
            }
            else
            {
                Record($"drain-{drainFailure}");
                errors.Add(new InvalidOperationException($"fixture drain {drainFailure} — a stream never finished"));
            }

            // 3. Dispose the Composer (no token; internally bounded). The disposal task is RETAINED so
            //    a deferred cleanup can still wait for it if the join below fails.
            var composerDisposal = Task.Run(async () => await Composer.DisposeAsync());
            string? composerFailure;
            try { composerFailure = await JoinAsync(composerDisposal); }
            catch (Exception ex) { errors.Add(ex); composerFailure = null; }
            if (composerFailure is null)
            {
                Record("composer-disposed");
            }
            else
            {
                Record($"composer-dispose-{composerFailure}");
                errors.Add(new InvalidOperationException($"Composer disposal {composerFailure}"));
            }

            // 4. JOIN the actor's message loop explicitly. On the healthy path Composer.DisposeAsync
            //    has usually joined it already; this explicit join is what GUARANTEES the fixture
            //    itself observed the loop exit before any dependent resource goes away (Composer
            //    disposal alone returns after its own timeout on an unhealthy path).
            var actorFailure = await JoinAsync(actor.Completion);
            if (actorFailure is null)
            {
                Record("actor-joined");
            }
            else
            {
                Record($"actor-join-{actorFailure}");
                errors.Add(new InvalidOperationException($"the actor's message loop never exited (join {actorFailure})"));
            }

            // 5. Dependent resources — ONLY once the Composer's disposal and the actor's loop have both
            //    completed. Otherwise the failure above is still thrown, but the database and directory
            //    are left INTACT and released by a continuation after the actor really completes.
            if (actor.IsCompleted && composerDisposal.IsCompleted)
            {
                errors.AddRange(ReleaseDependentResources());
            }
            else
            {
                Record("dependent-cleanup-deferred");
                _ = Task.Run(async () =>
                {
                    try { await composerDisposal; } catch { /* already reported or irrelevant to the release */ }
                    await actor.Completion;
                    Record("actor-joined-late");
                    ReleaseDependentResources();
                });
            }

            if (errors.Count > 0)
                throw new AggregateException("Fixture teardown failed", errors);

            // Published LAST, only after every step succeeded.
            _disposedCleanly = true;
        }

        /// <summary>
        /// Disposes the database and deletes the state directory, each contained so neither can skip
        /// the other, then completes <see cref="DependentResourcesReleased"/> (faulted if a step failed).
        /// Only ever called after the actor's loop has completed.
        /// </summary>
        private List<Exception> ReleaseDependentResources()
        {
            var errors = new List<Exception>();
            try { Db.Dispose(); Record("db-disposed"); }
            catch (Exception ex) { errors.Add(ex); }

            try
            {
                if (Directory.Exists(StateDir)) TestHelpers.ForceDeleteDirectory(StateDir);
                Record("dir-deleted");
            }
            catch (Exception ex) { errors.Add(ex); }

            if (errors.Count == 0) _dependentReleased.TrySetResult();
            else _dependentReleased.TrySetException(errors);
            return errors;
        }
    }

    /// <summary>
    /// Builds a connected Composer whose chat client is a fresh <see cref="UsageReportingClient"/>
    /// (also returned) and whose state directory holds (and will persist)
    /// <c>composer-session.json</c>.
    /// <para>
    /// The client is injected through the production <c>chatClientFactory</c> seam and
    /// <see cref="Composer.ConnectAsync"/> is called, so the session lifecycle (including the disk
    /// load/save of <c>composer-session.json</c>) is the real one.
    /// </para>
    /// <para>
    /// <b>Failure-safe ownership.</b> Every resource created here — the temporary directory, the
    /// database and the Composer — is disposed on EVERY exit path, including a throw from the
    /// temporary-directory creation, the database creation or <see cref="Composer.ConnectAsync"/>. A
    /// previously created Composer is never leaked: once it exists, only <see cref="Fixture"/> owns
    /// it, and the failure path hands it to the fixture's disposal.
    /// </para>
    /// </summary>
    private static async Task<(Fixture Fixture, UsageReportingClient Client)> CreateConnectedAsync(
        int maxContextTokens = 150_000)
    {
        var client = new UsageReportingClient();
        CopilotHiveDbContext? db = null;
        string? dir = null;
        Composer? composer = null;
        try
        {
            db = CopilotHiveDbContext.CreateInMemory();
            dir = CreateTempDir();
            var store = new GoalStore(db, NullLogger<GoalStore>.Instance);
            composer = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                maxContextTokens: maxContextTokens,
                stateDir: dir,
                chatClientFactory: _ => client);
            await composer.ConnectAsync(TestContext.Current.CancellationToken);

            var fixture = new Fixture(composer, db, dir, client);
            composer = null; // ownership transferred to the fixture
            db = null;
            dir = null;
            return (fixture, client);
        }
        catch
        {
            // Dispose of EVERYTHING that was created, even when one step of the cleanup throws: a
            // failing cleanup must never mask the original setup failure (Assert.Fail-equivalent
            // reporting), and it must never leave the temp directory behind.
            if (composer is not null)
            {
                try { await composer.DisposeAsync(); }
                catch { /* the original setup failure is the one that matters */ }
            }

            try { db?.Dispose(); }
            catch { /* ditto */ }

            if (dir is not null && Directory.Exists(dir))
            {
                try { TestHelpers.ForceDeleteDirectory(dir); }
                catch { /* ditto */ }
            }

            throw;
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Arms a completion signal on the facade's own streaming-update event: the actor's terminal
    /// sequence fires it only AFTER <c>IsStreaming</c> has been cleared, so awaiting it is the
    /// deterministic "this turn finished" gate (no polling, no delays).
    /// </summary>
    private static Task StreamCompletedAsync(Composer composer)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        composer.OnStreamingUpdate += () =>
        {
            if (!composer.IsStreaming) completed.TrySetResult();
        };
        return completed.Task;
    }

    /// <summary>
    /// A CLEANUP JOIN: awaits <paramref name="work"/> bounded only by the self-contained
    /// <see cref="DrainBound"/> safety guard and deliberately WITHOUT the test's cancellation token —
    /// a cancelled test must still join its outstanding work before any resource is released. (Test
    /// bodies use this instead of passing the ambient token, which would abandon the join exactly on
    /// the cancelled path it exists for.)
    /// </summary>
    private static Task JoinForCleanupAsync(Task work) => work.WaitAsync(DrainBound);

    /// <summary>
    /// RACE-FREE "no stream in flight" signal for teardown: the observer is registered FIRST, and only
    /// THEN is the streaming state read. If the read finds nothing streaming, the task is reconciled as
    /// already completed; otherwise the registered observer completes it on the terminal transition.
    /// <para>
    /// Because the observer exists before the read, there is no window in which the terminal
    /// transition can fire unobserved: either it fired before the read (the read sees not-streaming)
    /// or after the registration (the observer sees it). This is what the old read-then-register
    /// drain got wrong — a transition between the read and the registration was lost, and Composer
    /// disposal emits no replacement transition.
    /// </para>
    /// </summary>
    private static Task ArmStreamCompletion(Composer composer)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action? observer = null;
        observer = () =>
        {
            if (composer.IsStreaming) return;
            if (completed.TrySetResult()) composer.OnStreamingUpdate -= observer;
        };

        composer.OnStreamingUpdate += observer;   // 1. register

        if (!composer.IsStreaming)                // 2. then read, and reconcile
        {
            if (completed.TrySetResult()) composer.OnStreamingUpdate -= observer;
        }

        return completed.Task;
    }

    /// <summary>
    /// The Composer's live <see cref="AgentSession"/> (the persistent, not-yet-forked one).
    /// </summary>
    private static AgentSession GetSession(Composer composer)
    {
        var service = typeof(Composer).GetField("_agentService", PrivateFlags)!.GetValue(composer)!;
        return (AgentSession)service.GetType().GetField("_session", PrivateFlags)!.GetValue(service)!;
    }

    /// <summary>
    /// The Composer's <c>ComposerActor</c>. Its <c>IsCompleted</c> flag is the precise "no stranded
    /// streaming work" signal: <c>Actor&lt;T&gt;</c> sets the underlying completion source only after
    /// <c>OnShutdownAsync</c> has returned, so a disposal that hit its 5-second timeout and deferred
    /// cleanup leaves it incomplete.
    /// </summary>
    private static ComposerActor GetActor(Composer composer) =>
        (ComposerActor)typeof(Composer).GetField("_actor", PrivateFlags)!.GetValue(composer)!;

    /// <summary>Seeds the session history with real text so compaction has something to summarize.</summary>
    private static void PopulateSession(AgentSession session, int nonSystemMessageCount)
    {
        session.MessageHistory.Clear();
        session.MessageHistory.Add(new ChatMessage(ChatRole.System, "You are a helpful assistant."));
        for (var i = 0; i < nonSystemMessageCount; i++)
        {
            session.MessageHistory.Add(new ChatMessage(
                i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                $"Message {i} body text for the compaction fixture."));
        }
    }

    /// <summary>Asserts exactly one <c>(source, model)</c> bucket with an ORDINAL model comparison.</summary>
    private static UsageEntry SingleEntry(UsageSummary summary, UsageSource source, string? model) =>
        Assert.Single(summary.Entries, e => e.Source == source && string.Equals(e.Model, model, StringComparison.Ordinal));

    // ── (a) Multi-round chat turn: per-call sum in LastTurn, accumulated in Session ──

    /// <summary>
    /// A chat turn that makes TWO model calls (one tool round, then the final text) reports the
    /// execution's per-call SUM on <c>LastTurn</c> — exact input, exact output, <c>Calls == 2</c> and
    /// the model the client reports — and the very same tokens are ALSO accumulated in the session.
    /// <c>GetStats().CumulativeInputTokens</c> is the sum over all calls, NOT the last round's value.
    /// <para>
    /// DISCRIMINATOR: reporting only the last round would give <c>Calls == 1</c> and half the tokens;
    /// not wiring the tap at all leaves <c>LastTurn</c> null; a stats implementation that kept the
    /// last round instead of summing gives 1_000 rather than 2_000.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ChatTurn_WithToolRound_LastTurnIsTheExecutionsSumAndSessionAccumulates()
    {
        var (fixture, client) = await CreateConnectedAsync();
        await using var _ = fixture;
        var composer = fixture.Composer;

        // Premise: nothing has been measured yet.
        var before = composer.GetUsage();
        Assert.NotNull(before);
        Assert.Null(before!.LastTurn);
        Assert.Equal(0, before.Session.Total.Calls);

        var completed = StreamCompletedAsync(composer);
        client.BeginTurn(rounds: 2);
        composer.SendMessage("hello");

        await completed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Premise: the fixture really made two model calls (the tool round then the final round).
        Assert.Equal(2, client.StreamingCalls);

        var usage = composer.GetUsage();
        Assert.NotNull(usage);

        // LastTurn == the execution's per-call sum.
        Assert.NotNull(usage!.LastTurn);
        Assert.Equal(2, usage.LastTurn!.Total.Calls);
        Assert.Equal(2 * StreamInputPerCall, usage.LastTurn.Total.InputTokens);
        Assert.Equal(2 * StreamOutputPerCall, usage.LastTurn.Total.OutputTokens);
        Assert.Equal(2 * StreamCachedPerCall, usage.LastTurn.Total.CachedInputTokens);
        Assert.Equal(2 * StreamReasoningPerCall, usage.LastTurn.Total.ReasoningTokens);
        var lastTurnEntry = SingleEntry(usage.LastTurn, UsageSource.Agent, ModelId);
        Assert.Equal(2, lastTurnEntry.Usage.Calls);
        Assert.Equal(2 * StreamInputPerCall, lastTurnEntry.Usage.InputTokens);
        Assert.Single(usage.LastTurn.Entries);

        // Session carries the same tokens (one agent execution on the persistent session).
        Assert.Equal(2, usage.Session.Total.Calls);
        Assert.Equal(2 * StreamInputPerCall, usage.Session.Total.InputTokens);
        Assert.Equal(2 * StreamOutputPerCall, usage.Session.Total.OutputTokens);
        Assert.Equal(2, SingleEntry(usage.Session, UsageSource.Agent, ModelId).Usage.Calls);

        // Stats sum ALL calls (not the last round's 1_000 / 100).
        var stats = composer.GetStats();
        Assert.NotNull(stats);
        Assert.Equal(2 * StreamInputPerCall, stats!.CumulativeInputTokens);
        Assert.Equal(2 * StreamOutputPerCall, stats.CumulativeOutputTokens);
    }

    /// <summary>
    /// A SECOND turn REPLACES <c>LastTurn</c> (it is this turn's usage, never an accumulation) while
    /// the session keeps growing, so the two views are genuinely different numbers.
    /// <para>
    /// DISCRIMINATOR: an implementation that merged turns into <c>LastTurn</c> would report 3 calls
    /// and 3_000 input tokens for the second turn.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SecondTurn_ReplacesLastTurnWhileSessionKeepsAccumulating()
    {
        var (fixture, client) = await CreateConnectedAsync();
        await using var _ = fixture;
        var composer = fixture.Composer;

        var firstCompleted = StreamCompletedAsync(composer);
        client.BeginTurn(rounds: 2);
        composer.SendMessage("first");
        await firstCompleted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var secondCompleted = StreamCompletedAsync(composer);
        client.BeginTurn(rounds: 1);
        composer.SendMessage("second");
        await secondCompleted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var usage = composer.GetUsage();
        Assert.NotNull(usage);

        // LastTurn is the SECOND turn's single call only.
        Assert.NotNull(usage!.LastTurn);
        Assert.Equal(1, usage.LastTurn!.Total.Calls);
        Assert.Equal(StreamInputPerCall, usage.LastTurn.Total.InputTokens);
        Assert.Equal(StreamOutputPerCall, usage.LastTurn.Total.OutputTokens);

        // Session has both turns: 2 + 1 calls.
        Assert.Equal(3, usage.Session.Total.Calls);
        Assert.Equal(3 * StreamInputPerCall, usage.Session.Total.InputTokens);
        Assert.Equal(3 * StreamOutputPerCall, usage.Session.Total.OutputTokens);

        Assert.Equal(3 * StreamInputPerCall, composer.GetStats()!.CumulativeInputTokens);
    }

    // ── (b) ERROR-result turn still reports its usage ──

    /// <summary>
    /// A turn whose FINAL Completed update carries an error result (SharpCoder converts a provider
    /// fault into <c>Status = "Error"</c>) is still measured: the error result's tokens appear on
    /// <c>LastTurn</c> even though the Composer's stream ends in the existing error path.
    /// <para>
    /// DISCRIMINATOR: this is exactly the ordering the tap exists for — an implementation that
    /// reported usage AFTER the <c>IsError()</c> re-throw never runs its tap on this path, leaving
    /// <c>LastTurn</c> null.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ErrorResultTurn_StillSetsLastTurnToTheErrorResultsUsage()
    {
        var (fixture, client) = await CreateConnectedAsync();
        await using var _ = fixture;
        var composer = fixture.Composer;

        var completed = StreamCompletedAsync(composer);
        client.FailNextRound = true;
        composer.SendMessage("fail please");
        await completed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Premise: the turn really ended through the error path, not a success.
        Assert.Contains("❌ Error:", composer.StreamingContent, StringComparison.Ordinal);

        var usage = composer.GetUsage();
        Assert.NotNull(usage);
        Assert.NotNull(usage!.LastTurn);

        // The call reported usage before it failed, so the error execution carries it.
        Assert.Equal(1, usage.LastTurn!.Total.Calls);
        Assert.Equal(StreamInputPerCall, usage.LastTurn.Total.InputTokens);
        Assert.Equal(StreamOutputPerCall, usage.LastTurn.Total.OutputTokens);
        Assert.Equal(1, SingleEntry(usage.LastTurn, UsageSource.Agent, ModelId).Usage.Calls);
    }

    // ── (c) LastTurn is null at the start of a new stream ──

    /// <summary>
    /// <c>LastTurn</c> is cleared when a new stream starts and stays null for as long as that turn is
    /// in flight; it is populated only once the turn completes with a result.
    /// <para>
    /// Determinism: the stream is held open by <see cref="UsageReportingClient.Hold"/> INSIDE the fake
    /// client, so the in-flight assertion happens at a point the test controls exactly — the test
    /// first awaits <see cref="UsageReportingClient.GateHold.Acquired"/>, which completes only when the
    /// streaming round is actually waiting on the hold. No delay, no polling.
    /// </para>
    /// <para>
    /// <b>Failure-path ownership.</b> The hold is released unconditionally and idempotently in the
    /// <c>finally</c> BELOW the assertions, and the fixture releases it again (a no-op then) before it
    /// drains and disposes. So if any assertion in the held window fails, the held stream is still
    /// released, the outstanding turn still finishes, and teardown still completes with no stranded
    /// work — see <see cref="HeldWindow_FailurePath_StillReleasesTheHoldAndDrainsTheTurn"/>, which
    /// proves exactly that outcome.
    /// </para>
    /// <para>
    /// DISCRIMINATOR: without the clear, the PREVIOUS turn's value is still visible while the new
    /// stream runs, so the in-flight assertion sees a non-null <c>LastTurn</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task NewStream_ClearsLastTurnAndKeepsItNullUntilTheTurnCompletes()
    {
        var (fixture, client) = await CreateConnectedAsync();
        await using var _ = fixture;
        var composer = fixture.Composer;

        // First turn completes and populates LastTurn.
        var firstCompleted = StreamCompletedAsync(composer);
        client.BeginTurn(rounds: 1);
        composer.SendMessage("first");
        await firstCompleted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotNull(composer.GetUsage()!.LastTurn);

        // Second turn: held open inside the client until the hold is released.
        var hold = client.Hold();
        client.BeginTurn(rounds: 1);

        // The completion signal is armed BEFORE the send: registering it afterwards could miss the
        // terminal event entirely if the turn had already ended, and the drain below would then wait
        // on a signal that will never fire.
        var secondCompleted = StreamCompletedAsync(composer);

        // Tracks admission: the drain in the finally is only valid once a stream was actually started.
        // If the send itself is rejected (nothing in flight), there is nothing to drain and waiting on
        // the signal would stall for the whole bound.
        var sent = false;

        try
        {
            composer.SendMessage("second");
            sent = true;

            // Deterministic entry into the held window: this signal completes only once the streaming
            // round is actually awaiting the hold, so the assertions below run INSIDE the window.
            await hold.Acquired.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(composer.IsStreaming);
            Assert.False(hold.IsReleased, "the hold must still be held while asserting the in-flight state");

            // In flight: the previous turn's value must already be gone.
            Assert.Null(composer.GetUsage()!.LastTurn);

            // Release: the turn completes and LastTurn is populated again.
            hold.Release();
            await secondCompleted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            var usage = composer.GetUsage();
            Assert.NotNull(usage);
            Assert.NotNull(usage!.LastTurn);
            Assert.Equal(1, usage.LastTurn!.Total.Calls);
            Assert.Equal(StreamInputPerCall, usage.LastTurn.Total.InputTokens);
        }
        finally
        {
            // FAILURE-PATH OWNERSHIP: released unconditionally and idempotently, so a failing
            // assertion above (or a cancelled start wait) can never leave the gate held. The fixture
            // releases again without harm.
            hold.Release();

            // DRAIN before the fixture is disposed: the signal was armed before the send and the hold
            // is already released, so this can only wait on a stream that is finishing. It observes
            // NO test token — a cancelled test must still join its outstanding turn — and a safety
            // timeout here is swallowed (never masking the original failure): the fixture's own
            // race-free drain then joins the turn before releasing any resource.
            if (sent)
            {
                try { await JoinForCleanupAsync(secondCompleted); }
                catch (TimeoutException) { /* the fixture's drain owns the join from here */ }
            }
        }

        // No stranded work: the streaming task must have joined, and IsStreaming is cleared by the
        // actor's terminal sequence. Asserted AFTER the finally so it can never mask a failure raised
        // inside the held window.
        Assert.False(composer.IsStreaming, "a stream was left running after the held window exited");
    }

    /// <summary>
    /// <b>Failure-path proof of the held window.</b> This test deliberately FAILS a discriminating
    /// assertion while the stream is held, then verifies that the teardown protocol — the idempotent
    /// <c>finally</c> release plus the bounded drain — still lets the turn finish and the fixture tear
    /// down cleanly with NO stranded streaming work and no deleted-underneath-it state.
    /// <para>
    /// It is the automated form of the scratch variant the reviewer asked for: an intentional failure
    /// inside the window, run for real, with the gate armed. The failure itself is captured and
    /// asserted (so the test passes overall), and the post-conditions prove the cleanup contract.
    /// </para>
    /// <para>
    /// DISCRIMINATOR: without the unconditional <c>finally</c> release, the held stream never ends;
    /// <c>Composer.DisposeAsync</c> would hit its 5-second actor timeout and defer agent cleanup, and
    /// <c>Assert.False(IsStreaming)</c> below would fail. Removing the drain would likewise leave the
    /// fixture disposing the database while the stream is still running.
    /// </para>
    /// </summary>
    [Fact]
    public async Task HeldWindow_FailurePath_StillReleasesTheHoldAndDrainsTheTurn()
    {
        var (fixture, client) = await CreateConnectedAsync();
        await using var _ = fixture;
        var composer = fixture.Composer;

        // Premise: start from a completed turn so LastTurn is populated before the held window.
        var firstCompleted = StreamCompletedAsync(composer);
        client.BeginTurn(rounds: 1);
        composer.SendMessage("first");
        await firstCompleted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotNull(composer.GetUsage()!.LastTurn);

        var hold = client.Hold();
        client.BeginTurn(rounds: 1);

        // Armed BEFORE the send, so the drain below can never miss an already-issued completion.
        var heldTurnCompleted = StreamCompletedAsync(composer);
        Exception? windowFailure = null;

        try
        {
            composer.SendMessage("held turn");
            await hold.Acquired.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(composer.IsStreaming);

            // ★ THE INTENTIONAL FAILURE, inside the held window, exactly like a failing production
            //   assertion in the sibling test. It is caught so this test can then prove the cleanup.
            Assert.Fail("intentional failure inside the held window (scratch variant)");
        }
        catch (Xunit.Sdk.XunitException ex)
        {
            windowFailure = ex;
        }
        finally
        {
            // The SAME failure-path teardown the sibling test uses: unconditional, idempotent release.
            hold.Release();
        }

        // Premise: the deliberate failure really happened (this is the "failure path" being proven).
        Assert.NotNull(windowFailure);
        Assert.Contains("intentional failure inside the held window", windowFailure!.Message, StringComparison.Ordinal);

        // The hold is released — and releasing again is a no-op (idempotence).
        Assert.True(hold.IsReleased, "the finally did not release the held gate");
        hold.Release();
        Assert.True(hold.IsReleased);

        // THE DRAIN: the released hold lets the outstanding turn finish. The signal was armed before
        // the send and the gate is released, so the passing path never depends on the bound; it
        // observes no test token, matching the fixture's cancellation-independent joins.
        await JoinForCleanupAsync(heldTurnCompleted);

        // NO STRANDED WORK — the properties the reviewer's blocker was about.
        Assert.False(composer.IsStreaming, "the held turn was never drained after the failure");
        Assert.True(composer.GetUsage()!.LastTurn is not null, "the drained turn did not report its usage");

        // The actor's own completion is the precise "no stranded streaming work" signal: the mailbox
        // loop only sets it after OnShutdownAsync returned. The 5-second-timeout path leaves it
        // INCOMPLETE, so awaiting it here fails on a stranded teardown instead of passing silently.
        var actor = GetActor(composer);
        await fixture.DisposeAsync();
        Assert.True(fixture.IsDisposed);
        Assert.True(actor.IsCompleted,
            "the actor's message loop never exited — streaming work was stranded and disposal timed out");

        // Teardown really ran to the end: the state directory is gone and the database is disposed.
        Assert.True(!Directory.Exists(fixture.StateDir), "the fixture left its state directory behind");
        Assert.Throws<ObjectDisposedException>(() => fixture.Db.Goals.Count());
    }

    /// <summary>
    /// <b>Fixture fallback with work STILL outstanding.</b> A turn is held IN FLIGHT (gate armed,
    /// stream waiting inside the client) when <see cref="Fixture.DisposeAsync"/> runs — the test body
    /// does no release and no drain of its own. The fixture's race-free drain must release the hold,
    /// join the turn by its own completion signal, join the actor, and only THEN dispose the database
    /// and delete the directory.
    /// <para>
    /// <b>Milestone proof that no safety bound decided anything</b> (no elapsed time is asserted):
    /// the recorded teardown is exactly <c>gate-released → drain-joined → composer-disposed →
    /// actor-joined → db-disposed → dir-deleted</c>, so NO <c>*-timeout</c> / <c>*-abandoned</c> /
    /// <c>dependent-cleanup-deferred</c> step was recorded; <c>drain-joined</c> was captured with
    /// <c>IsStreaming == false</c> (the turn really finished); <c>db-disposed</c> and
    /// <c>dir-deleted</c> were captured with <c>ActorCompleted == true</c>; and the actor's loop has
    /// completed after disposal.
    /// </para>
    /// <para>
    /// DISCRIMINATOR: the old read-then-register drain could miss the terminal transition and record
    /// <c>drain-timeout</c>; without the release the turn never ends (<c>drain-timeout</c>). The
    /// explicit <c>actor.Completion</c> join is what GUARANTEES the fixture itself observed the loop
    /// exit before releasing resources — on this healthy path <c>Composer.DisposeAsync</c> already
    /// joins the actor, so removing the explicit join would NOT necessarily surface here as
    /// <c>ActorCompleted == false</c>; the unhealthy path where it matters is proven by
    /// <see cref="FixtureDispose_ActorJoinFails_DefersDependentCleanupUntilActorCompletes"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task FixtureDispose_WithTurnStillHeld_DrainsAndJoinsBeforeReleasingResources()
    {
        var (fixture, client) = await CreateConnectedAsync();
        await using var _ = fixture;
        var composer = fixture.Composer;
        var actor = GetActor(composer);

        var hold = client.Hold();
        client.BeginTurn(rounds: 1);
        composer.SendMessage("held turn left for the fixture");
        await hold.Acquired.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Premise: work is genuinely outstanding when the fixture takes over.
        Assert.True(composer.IsStreaming);
        Assert.False(hold.IsReleased);
        Assert.False(actor.IsCompleted);

        await fixture.DisposeAsync();

        Assert.True(fixture.IsDisposed);
        Assert.True(hold.IsReleased, "the fixture did not release the held gate");
        Assert.False(composer.IsStreaming);
        Assert.True(actor.IsCompleted, "the actor's message loop never exited");
        Assert.True(fixture.DependentResourcesReleased.IsCompletedSuccessfully);

        var steps = fixture.Teardown;
        var names = steps.Select(s => s.Name).ToArray();
        Assert.Equal(
            new[] { "gate-released", "drain-joined", "composer-disposed", "actor-joined", "db-disposed", "dir-deleted" },
            names);
        Assert.DoesNotContain(names, n => n.Contains("timeout") || n.Contains("abandoned") || n.Contains("deferred"));

        // Ordering: resources were released only AFTER the outstanding work joined.
        var drainJoined = steps.Single(s => s.Name == "drain-joined");
        Assert.False(drainJoined.IsStreaming, "the drain reported joined while the stream was still running");
        var dbDisposed = steps.Single(s => s.Name == "db-disposed");
        Assert.False(dbDisposed.IsStreaming);
        Assert.True(dbDisposed.ActorCompleted, "the database was disposed before the actor's loop exited");
        Assert.True(steps.Single(s => s.Name == "dir-deleted").ActorCompleted);

        // IsDisposed was published only after every step — never observed early.
        Assert.All(steps, s => Assert.False(s.FixtureReportedDisposed));

        Assert.False(Directory.Exists(fixture.StateDir));
        Assert.Throws<ObjectDisposedException>(() => fixture.Db.Goals.Count());
    }

    /// <summary>
    /// <b>A failed actor join must not destroy dependent resources under live work.</b> A streaming
    /// round is PINNED inside the client (a test-owned signal the fixture cannot release, whose wait
    /// ignores cancellation), so the actor's message loop is genuinely still running when
    /// <see cref="Fixture.DisposeAsync"/> runs. The fixture's joins race a test-controlled
    /// <see cref="Fixture.AbandonJoins"/> signal that is already completed — the failed-join path is
    /// reached deterministically, with NO wall-clock time involved.
    /// <para>
    /// Asserts: the teardown reports the failed join (thrown, and recorded as
    /// <c>actor-join-abandoned</c>); the SAFE independent steps still ran first — <c>gate-released</c>
    /// and the attempted drain join (<c>drain-abandoned</c>) are both present and both recorded BEFORE
    /// <c>dependent-cleanup-deferred</c>, each observing the still-outstanding stream; a
    /// <c>dependent-cleanup-deferred</c> step is recorded with
    /// <c>ActorCompleted == false</c>; while the actor is incomplete the state directory still EXISTS
    /// and the database is NOT disposed; <see cref="Fixture.IsDisposed"/> stays false. Then the test
    /// releases the pin: the actor completes, the deferred cleanup runs (recorded as
    /// <c>actor-joined-late → db-disposed → dir-deleted</c>, all with <c>ActorCompleted == true</c>),
    /// and the database and directory are released.
    /// </para>
    /// <para>
    /// DISCRIMINATOR: an immediate (non-deferred) teardown disposes the database and deletes the
    /// directory while the actor is still running, so the "intact while incomplete" assertions fail;
    /// skipping the safe independent steps (gate release / drain attempt) before the deferral fails
    /// the prefix presence-and-order assertions.
    /// </para>
    /// </summary>
    [Fact]
    public async Task FixtureDispose_ActorJoinFails_DefersDependentCleanupUntilActorCompletes()
    {
        var (fixture, client) = await CreateConnectedAsync();
        var composer = fixture.Composer;
        var actor = GetActor(composer);
        var pin = client.Pin();

        try
        {
            client.BeginTurn(rounds: 1);
            composer.SendMessage("pinned turn");
            await pin.Acquired.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(composer.IsStreaming);
            Assert.False(actor.IsCompleted);

            // Deterministic failed-join path: every join that is not already complete is abandoned.
            var abandon = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            abandon.SetResult();
            fixture.AbandonJoins = abandon;

            var failure = await Assert.ThrowsAsync<AggregateException>(() => fixture.DisposeAsync().AsTask());
            Assert.Contains(failure.InnerExceptions, e => e.Message.Contains("actor's message loop never exited", StringComparison.Ordinal));

            // The deferral is recorded honestly, with the condition that forced it.
            var steps = fixture.Teardown;
            var names = steps.Select(s => s.Name).ToArray();
            Assert.Contains("actor-join-abandoned", names);
            var deferred = steps.Single(s => s.Name == "dependent-cleanup-deferred");
            Assert.False(deferred.ActorCompleted, "the deferral step must capture the incomplete actor");
            Assert.DoesNotContain("db-disposed", names);
            Assert.DoesNotContain("dir-deleted", names);

            // SAFE-STEP PREFIX PROOF: the failed path must not skip the INDEPENDENT safe cleanup steps
            // before deferring. The gate release and the (attempted, abandoned) drain join are both
            // recorded, and both were recorded BEFORE the deferral — so only the DEPENDENT resources
            // were skipped.
            var gateReleasedIndex = Array.IndexOf(names, "gate-released");
            var drainStepIndex = Array.IndexOf(names, "drain-abandoned");
            var deferredIndex = Array.IndexOf(names, "dependent-cleanup-deferred");
            Assert.True(gateReleasedIndex >= 0, "the failed-join path never ran the safe gate-release step");
            Assert.True(drainStepIndex >= 0, "the failed-join path never ran the safe drain-join step");
            Assert.True(deferredIndex >= 0, "the deferral step was not recorded");
            Assert.True(gateReleasedIndex < deferredIndex,
                "gate-released was not recorded before dependent-cleanup-deferred");
            Assert.True(drainStepIndex < deferredIndex,
                "drain-abandoned was not recorded before dependent-cleanup-deferred");

            // Both safe steps ran while the turn was still outstanding (the pin held it) — which is
            // exactly why only the dependent resources were deferred.
            Assert.True(steps[gateReleasedIndex].IsStreaming,
                "the gate was released after the stream had already stopped");
            Assert.True(steps[drainStepIndex].IsStreaming,
                "the drain step did not observe the still-outstanding stream");

            // Dependent resources are INTACT while the actor is still running.
            Assert.False(actor.IsCompleted);
            Assert.True(Directory.Exists(fixture.StateDir), "the state directory was deleted under a live actor");
            var goalsWhileLive = fixture.Db.Goals.Count(); // must NOT throw ObjectDisposedException
            Assert.True(goalsWhileLive >= 0);
            Assert.False(fixture.DependentResourcesReleased.IsCompleted);
            Assert.False(fixture.IsDisposed, "a teardown with a failed join must not report disposed");

            // The test releases the pin: the actor really completes, then — and only then — the
            // deferred cleanup releases the database and the directory.
            pin.Release();
            await JoinForCleanupAsync(actor.Completion);
            await JoinForCleanupAsync(fixture.DependentResourcesReleased);

            var after = fixture.Teardown;
            var lateNames = after.Select(s => s.Name).SkipWhile(n => n != "dependent-cleanup-deferred").ToArray();
            Assert.Equal(new[] { "dependent-cleanup-deferred", "actor-joined-late", "db-disposed", "dir-deleted" }, lateNames);
            Assert.True(after.Single(s => s.Name == "db-disposed").ActorCompleted);
            Assert.True(after.Single(s => s.Name == "dir-deleted").ActorCompleted);

            Assert.False(Directory.Exists(fixture.StateDir));
            Assert.Throws<ObjectDisposedException>(() => fixture.Db.Goals.Count());
        }
        finally
        {
            // Failure-path ownership: the pin is released unconditionally, so even a failing assertion
            // above lets the actor finish and the deferred cleanup run. Disposal is (re)joined here —
            // the retained teardown task is shared, so this either starts teardown (if the body failed
            // before disposing) or re-observes the already-asserted failed join, which is expected.
            pin.Release();
            client.ReleaseGate();
            try { await JoinForCleanupAsync(fixture.DisposeAsync().AsTask()); }
            catch (AggregateException) { /* the failed join was asserted above (or the body already failed) */ }
            catch (TimeoutException) { /* safety guard only; never part of the passing path */ }
            try { await JoinForCleanupAsync(fixture.DependentResourcesReleased); }
            catch (TimeoutException) { /* ditto */ }
        }
    }

    /// <summary>
    /// <b>Every disposal caller JOINS the one retained teardown task.</b> The teardown is held by a
    /// test-owned pin (the in-flight round cannot finish, so the fixture's drain cannot complete);
    /// the FIRST dispose starts the teardown, the test waits for the deterministic
    /// <c>gate-released</c> milestone, then a SECOND dispose is issued.
    /// <para>
    /// Asserts: both callers' <c>AsTask()</c> return the SAME <see cref="Task"/> instance (the retained
    /// teardown), and while the pin holds the teardown BOTH are still pending — so the second caller
    /// is genuinely waiting, not returning early. After the test releases the pin, both complete
    /// together, and both observe the same single, complete teardown.
    /// </para>
    /// <para>
    /// DISCRIMINATOR: a no-op second caller (returning a completed task once a teardown exists) fails
    /// both the identity assertion and the "second caller still pending while held" assertion. The
    /// older <c>Task.WhenAll(first, second)</c> check could not catch this, because the first caller
    /// alone satisfied the combined wait.
    /// </para>
    /// </summary>
    [Fact]
    public async Task FixtureDispose_SecondCallerJoinsTheSameRetainedTeardownTask()
    {
        var (fixture, client) = await CreateConnectedAsync();
        var composer = fixture.Composer;
        var pin = client.Pin();

        try
        {
            client.BeginTurn(rounds: 1);
            composer.SendMessage("pinned turn holding the teardown");
            await pin.Acquired.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(composer.IsStreaming);

            var gateReleased = fixture.StepRecorded("gate-released");
            var firstDispose = fixture.DisposeAsync().AsTask();
            await gateReleased.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Teardown is running and HELD by the pin (its drain cannot complete).
            var secondDispose = fixture.DisposeAsync().AsTask();

            Assert.Same(firstDispose, secondDispose);
            Assert.False(firstDispose.IsCompleted, "the teardown should still be held by the pin");
            Assert.False(secondDispose.IsCompleted, "the second caller returned before the teardown finished");
            Assert.False(fixture.IsDisposed);

            pin.Release();
            await JoinForCleanupAsync(secondDispose);

            Assert.True(firstDispose.IsCompletedSuccessfully);
            Assert.True(secondDispose.IsCompletedSuccessfully);
            Assert.True(fixture.IsDisposed);

            // ONE teardown ran, completely and once.
            Assert.Equal(
                new[] { "gate-released", "drain-joined", "composer-disposed", "actor-joined", "db-disposed", "dir-deleted" },
                fixture.Teardown.Select(s => s.Name).ToArray());

            // A late third caller also receives the same, already-completed retained task.
            Assert.Same(firstDispose, fixture.DisposeAsync().AsTask());
        }
        finally
        {
            pin.Release();
            client.ReleaseGate();
            try { await JoinForCleanupAsync(fixture.DisposeAsync().AsTask()); }
            catch (Exception) when (!fixture.IsDisposed) { /* a failing body is reported by its own assertion */ }
        }
    }

    /// <summary>
    /// <b>Cancelled held-window exit.</b> The test's OWN cancellation token
    /// (<c>TestContext.Current.CancellationToken</c>) is cancelled while a turn is held, and the body's
    /// wait for that turn observes it, so the body leaves the window through an
    /// <see cref="OperationCanceledException"/> without releasing or draining anything. The fixture must still join the outstanding work and
    /// only then release the database and the directory.
    /// <para>
    /// This is the permanent form of the reviewer's cancelled-exit scenario: every teardown join is
    /// independent of the cancelled token, so the disposal is neither abandoned mid-way nor reported as
    /// finished early. A SECOND dispose call (as <c>await using</c> makes) joins the same teardown
    /// rather than returning as a no-op.
    /// </para>
    /// <para>
    /// DISCRIMINATOR: joins that observed the cancelled token would throw immediately, abandoning the
    /// disposal (no <c>db-disposed</c>/<c>dir-deleted</c> steps, directory still present) while an
    /// early <c>IsDisposed</c> flag would already read true.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CancelledHeldWindowExit_FixtureStillJoinsOutstandingWorkBeforeCleanup()
    {
        var (fixture, client) = await CreateConnectedAsync();
        await using var _ = fixture;
        var composer = fixture.Composer;
        var actor = GetActor(composer);

        var hold = client.Hold();
        client.BeginTurn(rounds: 1);

        Exception? exit = null;
        try
        {
            composer.SendMessage("held turn, cancelled exit");
            await hold.Acquired.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // The exit: the TEST'S OWN cancellation token is cancelled while the gate is still armed,
            // and the body's wait for the held turn observes it. The turn cannot complete (it is
            // held), so this throws deterministically — exactly the reviewer's cancelled-exit path.
            var turnFinished = StreamCompletedAsync(composer);
            TestContext.Current.CancelCurrentTest();
            await turnFinished.WaitAsync(TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            exit = ex;
        }

        // Premise: we left through cancellation OF THE AMBIENT TEST TOKEN, with the turn still held
        // and no release/drain done. Every join below must therefore be token-independent.
        Assert.IsAssignableFrom<OperationCanceledException>(exit);
        Assert.True(TestContext.Current.CancellationToken.IsCancellationRequested);
        Assert.True(composer.IsStreaming, "the held turn should still be in flight after the cancelled exit");
        Assert.False(hold.IsReleased);
        Assert.False(fixture.IsDisposed);

        // Two overlapping dispose calls — the second must JOIN the same retained teardown task, not
        // no-op. Identity is the discriminating proof: Task.WhenAll alone would be satisfied by the
        // first caller even if the second returned early.
        var firstDispose = fixture.DisposeAsync().AsTask();
        var secondDispose = fixture.DisposeAsync().AsTask();
        Assert.Same(firstDispose, secondDispose);
        await JoinForCleanupAsync(secondDispose);

        Assert.True(fixture.IsDisposed);
        Assert.True(actor.IsCompleted, "the actor's loop never exited — outstanding work was stranded");
        Assert.False(composer.IsStreaming);

        var steps = fixture.Teardown;
        Assert.Equal(
            new[] { "gate-released", "drain-joined", "composer-disposed", "actor-joined", "db-disposed", "dir-deleted" },
            steps.Select(s => s.Name).ToArray());
        Assert.True(steps.Single(s => s.Name == "db-disposed").ActorCompleted,
            "resources were released before the outstanding work joined");
        Assert.All(steps, s => Assert.False(s.FixtureReportedDisposed));

        Assert.False(Directory.Exists(fixture.StateDir));
        Assert.Throws<ObjectDisposedException>(() => fixture.Db.Goals.Count());
    }

    // ── (d) Clarifications ──

    /// <summary>
    /// A clarification answer accumulates in <c>Clarifications</c> and leaves <c>Session</c>
    /// untouched: the answer runs on a FORKED session whose usage SharpCoder resets, so the main
    /// session's cumulative summary never sees those calls.
    /// <para>
    /// DISCRIMINATOR: counting the clarification on the main session (or forgetting to fold it into
    /// the accumulator at all) makes one of the two assertions fail.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ClarificationAnswer_CountsInClarificationsAndNotInSession()
    {
        var (fixture, client) = await CreateConnectedAsync();
        await using var _ = fixture;
        var composer = fixture.Composer;

        var queue = new ClarificationQueueService();
        var request = new ClarificationRequest { GoalId = "g-clarify", WorkerRole = "coder", Question = "Which format?" };
        queue.Enqueue(request);

        client.BeginTurn(rounds: 1);
        client.ReplyText = "Use JSON.";
        var answer = await composer.AnswerClarificationAsync(
            "g-clarify", "Which format?", "context", queue, request, TestContext.Current.CancellationToken);

        // Premise: the clarification really was auto-answered (not escalated).
        Assert.Equal("Use JSON.", answer);
        Assert.Equal(ClarificationStatus.AwaitingComposer, queue.GetRequest(request.Id)!.Status);

        var usage = composer.GetUsage();
        Assert.NotNull(usage);

        Assert.Equal(1, usage!.Clarifications.Total.Calls);
        Assert.Equal(StreamInputPerCall, usage.Clarifications.Total.InputTokens);
        Assert.Equal(StreamOutputPerCall, usage.Clarifications.Total.OutputTokens);
        Assert.Equal(1, SingleEntry(usage.Clarifications, UsageSource.Agent, ModelId).Usage.Calls);

        // The fork's usage never reaches the main session.
        Assert.Equal(0, usage.Session.Total.Calls);
        Assert.Equal(0, usage.Session.Total.InputTokens);
        Assert.Empty(usage.Session.Entries);
    }

    /// <summary>
    /// Escalated and failed clarifications are counted too — their calls were billed whether or not
    /// the reply was usable — and TWO clarifications SUM (<c>Calls</c> adds up over the merged
    /// entries rather than being replaced).
    /// <para>
    /// Covers three runs in one test so the running total is observable: an <c>ESCALATE_TO_HUMAN</c>
    /// reply, a provider-error reply and an empty reply, each adding exactly one call.
    /// </para>
    /// <para>
    /// DISCRIMINATOR: moving the capture AFTER the <c>IsError()</c> re-throw drops the error run's
    /// call (total 2 instead of 3); treating each clarification as a whole-summary event through
    /// <c>UsageSummary.Add(UsageEvent)</c> would still sum to 3 here, so the per-run assertion on the
    /// ERROR run's own contribution is what pins the ordering.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EscalatedFailedAndEmptyClarifications_AreAllCountedAndSum()
    {
        var (fixture, client) = await CreateConnectedAsync();
        await using var _ = fixture;
        var composer = fixture.Composer;

        var queue = new ClarificationQueueService();

        // 1. Escalation sentinel — auto-answer path, escalated to the human.
        var escalated = new ClarificationRequest { GoalId = "g-1", WorkerRole = "coder", Question = "q1" };
        queue.Enqueue(escalated);
        client.BeginTurn(rounds: 1);
        client.ReplyText = "ESCALATE_TO_HUMAN";
        var answer1 = await composer.AnswerClarificationAsync(
            "g-1", "q1", "ctx", queue, escalated, TestContext.Current.CancellationToken);

        Assert.Null(answer1);
        Assert.Equal(ClarificationStatus.AwaitingHuman, queue.GetRequest(escalated.Id)!.Status);
        var afterEscalation = composer.GetUsage();
        Assert.NotNull(afterEscalation);
        Assert.Equal(1, afterEscalation!.Clarifications.Total.Calls);
        Assert.Equal(StreamInputPerCall, afterEscalation.Clarifications.Total.InputTokens);

        // 2. Provider ERROR result — the error path escalates, but the call still counts.
        var failed = new ClarificationRequest { GoalId = "g-2", WorkerRole = "coder", Question = "q2" };
        queue.Enqueue(failed);
        client.FailNextRound = true;
        var answer2 = await composer.AnswerClarificationAsync(
            "g-2", "q2", "ctx", queue, failed, TestContext.Current.CancellationToken);

        Assert.Null(answer2);
        Assert.Equal(ClarificationStatus.AwaitingHuman, queue.GetRequest(failed.Id)!.Status);

        var afterError = composer.GetUsage();
        Assert.NotNull(afterError);
        // The error run's OWN call was recorded: 2 calls total, 2_000 input tokens.
        Assert.Equal(2, afterError!.Clarifications.Total.Calls);
        Assert.Equal(2 * StreamInputPerCall, afterError.Clarifications.Total.InputTokens);
        Assert.Equal(2, SingleEntry(afterError.Clarifications, UsageSource.Agent, ModelId).Usage.Calls);

        // 3. EMPTY reply — also escalated, also counted; the total accumulates to 3.
        var empty = new ClarificationRequest { GoalId = "g-3", WorkerRole = "coder", Question = "q3" };
        queue.Enqueue(empty);
        client.BeginTurn(rounds: 1);
        client.ReplyText = "   ";
        var answer3 = await composer.AnswerClarificationAsync(
            "g-3", "q3", "ctx", queue, empty, TestContext.Current.CancellationToken);

        Assert.Null(answer3);
        var final = composer.GetUsage();
        Assert.NotNull(final);
        Assert.Equal(3, final!.Clarifications.Total.Calls);
        Assert.Equal(3 * StreamInputPerCall, final.Clarifications.Total.InputTokens);
        Assert.Equal(3 * StreamOutputPerCall, final.Clarifications.Total.OutputTokens);
        Assert.Equal(3 * StreamCachedPerCall, final.Clarifications.Total.CachedInputTokens);
        Assert.Equal(3 * StreamReasoningPerCall, final.Clarifications.Total.ReasoningTokens);

        // Still no session contribution from any clarification.
        Assert.Equal(0, final.Session.Total.Calls);
    }

    /// <summary>
    /// A MULTI-ROUND clarification contributes its per-call sum, not one call: the merge folds the
    /// summary's ENTRY (2 calls), which <c>UsageSummary.Add(UsageEvent)</c> could never express.
    /// <para>
    /// DISCRIMINATOR: folding the whole summary through <c>UsageSummary.Add(UsageEvent)</c> records a
    /// single event and reports <c>Calls == 1</c> with one call's tokens.
    /// </para>
    /// </summary>
    [Fact]
    public async Task MultiRoundClarification_ContributesItsPerCallSumNotASingleCall()
    {
        var (fixture, client) = await CreateConnectedAsync();
        await using var _ = fixture;
        var composer = fixture.Composer;

        var queue = new ClarificationQueueService();
        var request = new ClarificationRequest { GoalId = "g-multi", WorkerRole = "coder", Question = "q" };
        queue.Enqueue(request);

        client.BeginTurn(rounds: 2);
        client.ReplyText = "Use JSON.";
        var answer = await composer.AnswerClarificationAsync(
            "g-multi", "q", "ctx", queue, request, TestContext.Current.CancellationToken);
        Assert.Equal("Use JSON.", answer);
        Assert.Equal(2, client.StreamingCalls);

        var usage = composer.GetUsage();
        Assert.NotNull(usage);
        Assert.Equal(2, usage!.Clarifications.Total.Calls);
        Assert.Equal(2 * StreamInputPerCall, usage.Clarifications.Total.InputTokens);
        Assert.Equal(2, SingleEntry(usage.Clarifications, UsageSource.Agent, ModelId).Usage.Calls);

        // And the second clarification SUMS onto the first (2 + 1 = 3).
        var second = new ClarificationRequest { GoalId = "g-multi-2", WorkerRole = "coder", Question = "q2" };
        queue.Enqueue(second);
        client.BeginTurn(rounds: 1);
        var answer2 = await composer.AnswerClarificationAsync(
            "g-multi-2", "q2", "ctx", queue, second, TestContext.Current.CancellationToken);
        Assert.Equal("Use JSON.", answer2);

        var summed = composer.GetUsage();
        Assert.NotNull(summed);
        Assert.Equal(3, summed!.Clarifications.Total.Calls);
        Assert.Equal(3 * StreamInputPerCall, summed.Clarifications.Total.InputTokens);
    }

    // ── (e) Manual vs automatic compaction ──

    /// <summary>
    /// A manual <see cref="Composer.CompactSessionAsync"/> and a manual
    /// <see cref="Composer.CompactOldestPercentAsync"/> each add exactly one
    /// <see cref="UsageSource.Compaction"/> entry to <c>ManualCompactions</c>, and the SAME calls are
    /// visible in <c>Session</c> — the compactor records into the session itself while our tap
    /// accumulates the events.
    /// <para>
    /// DISCRIMINATOR: without the <c>OnUsage</c> wiring on the compaction-only options clone,
    /// <c>ManualCompactions</c> stays empty (Calls == 0) even though <c>Session</c> grows; a clone
    /// that also copied the service's own options would double-count.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ManualCompactions_AddCompactionEntriesToManualAccumulatorAndSession()
    {
        var (fixture, client) = await CreateConnectedAsync();
        await using var _ = fixture;
        var composer = fixture.Composer;
        client.CompactionInputTokens = CompactionInputPerCall;
        client.CompactionOutputTokens = CompactionOutputPerCall;

        var session = GetSession(composer);

        // ── Full manual compaction ──
        PopulateSession(session, 20);
        Assert.Equal(0, composer.GetUsage()!.ManualCompactions.Total.Calls);

        var full = await composer.CompactSessionAsync(TestContext.Current.CancellationToken);
        Assert.True(full, "the full manual compaction did not run — the fixture is too small");

        var afterFull = composer.GetUsage();
        Assert.NotNull(afterFull);

        var manualEntry = SingleEntry(afterFull!.ManualCompactions, UsageSource.Compaction, ModelId);
        Assert.Equal(1, manualEntry.Usage.Calls);
        Assert.Equal(CompactionInputPerCall, manualEntry.Usage.InputTokens);
        Assert.Equal(CompactionOutputPerCall, manualEntry.Usage.OutputTokens);
        Assert.Equal(1, afterFull.ManualCompactions.Total.Calls);
        Assert.Equal(CompactionInputPerCall, afterFull.ManualCompactions.Total.InputTokens);

        // The compactor recorded the same call into the session's cumulative usage.
        var sessionEntry = SingleEntry(afterFull.Session, UsageSource.Compaction, ModelId);
        Assert.Equal(1, sessionEntry.Usage.Calls);
        Assert.Equal(CompactionInputPerCall, sessionEntry.Usage.InputTokens);
        Assert.Equal(1, afterFull.Session.Total.Calls);

        // ── Partial manual compaction adds a SECOND event ──
        PopulateSession(session, 30);
        var partial = await composer.CompactOldestPercentAsync(50, TestContext.Current.CancellationToken);
        Assert.True(partial, "the partial manual compaction did not run — the fixture is too small");

        var afterPartial = composer.GetUsage();
        Assert.NotNull(afterPartial);

        var partialEntry = SingleEntry(afterPartial!.ManualCompactions, UsageSource.Compaction, ModelId);
        Assert.Equal(2, partialEntry.Usage.Calls);
        Assert.Equal(2 * CompactionInputPerCall, partialEntry.Usage.InputTokens);
        Assert.Equal(2 * CompactionOutputPerCall, partialEntry.Usage.OutputTokens);
        Assert.Equal(2 * CompactionInputPerCall, afterPartial.ManualCompactions.Total.InputTokens);

        // …and two Compaction calls in the session.
        Assert.Equal(2, SingleEntry(afterPartial.Session, UsageSource.Compaction, ModelId).Usage.Calls);
        Assert.Equal(2 * CompactionInputPerCall, afterPartial.Session.Total.InputTokens);
    }

    /// <summary>
    /// A chat turn's AUTOMATIC compaction (triggered inside the turn) is part of THAT turn's execution
    /// — it shows up in <c>Session</c> through the turn's <c>TokenUsage</c> and in <c>LastTurn</c> —
    /// but it must NOT be counted as a manual compaction.
    /// <para>
    /// DISCRIMINATOR: wiring <c>OnUsage</c> onto the service's own options (used for automatic
    /// compaction) would also push this event into <c>ManualCompactions</c>, so the
    /// <c>Calls == 0</c> assertion is exactly the "manual only" boundary.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AutomaticCompactionInsideATurn_AppearsInSessionButNotInManualCompactions()
    {
        // A tiny context window (1_000) with the seeded LastKnownContextTokens at the threshold makes
        // the agent's own CompactIfNeededAsync fire at the START of the turn.
        var (fixture, client) = await CreateConnectedAsync(maxContextTokens: 1_000);
        await using var _ = fixture;
        var composer = fixture.Composer;
        client.CompactionInputTokens = CompactionInputPerCall;
        client.CompactionOutputTokens = CompactionOutputPerCall;

        var session = GetSession(composer);
        PopulateSession(session, 20);
        session.LastKnownContextTokens = 1_000;

        var completed = StreamCompletedAsync(composer);
        client.BeginTurn(rounds: 1);
        composer.SendMessage("turn with automatic compaction");
        await completed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Premise: the automatic compaction really ran — the client answered a non-streaming summary
        // call IN ADDITION to the turn's streaming round.
        Assert.True(client.CompactionResponseCount >= 1,
            "the automatic compaction never asked the model for a summary");
        Assert.Equal(1, client.StreamingCalls);

        var usage = composer.GetUsage();
        Assert.NotNull(usage);

        // The compaction call is part of the turn's execution…
        Assert.NotNull(usage!.LastTurn);
        Assert.Equal(2, usage.LastTurn!.Total.Calls);
        var compactionInTurn = SingleEntry(usage.LastTurn, UsageSource.Compaction, ModelId);
        Assert.Equal(1, compactionInTurn.Usage.Calls);
        Assert.Equal(CompactionInputPerCall, compactionInTurn.Usage.InputTokens);

        // …and therefore of the session.
        Assert.Equal(2, usage.Session.Total.Calls);
        Assert.Equal(1, SingleEntry(usage.Session, UsageSource.Compaction, ModelId).Usage.Calls);

        // But it is NOT a manual compaction.
        Assert.Equal(0, usage.ManualCompactions.Total.Calls);
        Assert.Empty(usage.ManualCompactions.Entries);
    }

    // ── (f) Null when disconnected + detachment ──

    /// <summary>
    /// <c>GetUsage()</c> returns null while the Composer has no agent, using the same guard shape as
    /// <see cref="Composer.GetStats"/>.
    /// <para>
    /// DISCRIMINATOR: without the guard the method would dereference the live session's usage and
    /// return a non-null record (or throw) for a disconnected Composer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetUsage_WhenNotConnected_ReturnsNull()
    {
        var db = CopilotHiveDbContext.CreateInMemory();
        var dir = CreateTempDir();
        try
        {
            var store = new GoalStore(db, NullLogger<GoalStore>.Instance);
            var composer = new Composer("test-model", NullLogger<Composer>.Instance, store, stateDir: dir);
            await using (composer)
            {
                // Premise: this is the disconnected state (no ConnectAsync, no agent).
                Assert.Null(composer.GetStats());
                Assert.Null(composer.GetUsage());
            }
        }
        finally
        {
            db.Dispose();
            if (Directory.Exists(dir)) TestHelpers.ForceDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// Every member of the returned record is a DETACHED copy: mutating the entries of a returned
    /// summary (and adding to it) cannot change what a later <c>GetUsage()</c> reports.
    /// <para>
    /// DISCRIMINATOR: returning the live <c>Session.Usage</c> / accumulator instances makes the
    /// post-mutation read report the tampered numbers.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReturnedSummaries_AreDetachedCopies()
    {
        var (fixture, client) = await CreateConnectedAsync();
        await using var _ = fixture;
        var composer = fixture.Composer;

        // Populate all four members with real numbers.
        var turnCompleted = StreamCompletedAsync(composer);
        client.BeginTurn(rounds: 2);
        composer.SendMessage("measured turn");
        await turnCompleted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        client.CompactionInputTokens = CompactionInputPerCall;
        client.CompactionOutputTokens = CompactionOutputPerCall;
        PopulateSession(GetSession(composer), 20);
        Assert.True(await composer.CompactSessionAsync(TestContext.Current.CancellationToken));

        var queue = new ClarificationQueueService();
        var request = new ClarificationRequest { GoalId = "g-detach", WorkerRole = "coder", Question = "q" };
        queue.Enqueue(request);
        client.BeginTurn(rounds: 1);
        client.ReplyText = "Use JSON.";
        Assert.Equal("Use JSON.", await composer.AnswerClarificationAsync(
            "g-detach", "q", "ctx", queue, request, TestContext.Current.CancellationToken));

        var first = composer.GetUsage();
        Assert.NotNull(first);
        Assert.NotNull(first!.LastTurn);
        Assert.NotEmpty(first.Session.Entries);
        Assert.NotEmpty(first.Clarifications.Entries);
        Assert.NotEmpty(first.ManualCompactions.Entries);

        var sessionInput = first.Session.Total.InputTokens;
        var sessionCalls = first.Session.Total.Calls;
        var turnInput = first.LastTurn!.Total.InputTokens;
        var turnCalls = first.LastTurn.Total.Calls;
        var clarificationInput = first.Clarifications.Total.InputTokens;
        var clarificationCalls = first.Clarifications.Total.Calls;
        var manualInput = first.ManualCompactions.Total.InputTokens;
        var manualCalls = first.ManualCompactions.Total.Calls;

        // Mutate each returned summary through its OWN mutating members: `Add` records a real event
        // and the `Entries` setter REPLACES the summary's entries. (Entry-level and `Total`-level
        // writes are deliberately NOT used — both getters already hand out fresh copies, so mutating
        // them would prove nothing about detachness.)
        var tampered = new UsageEvent(UsageSource.Agent, "tampered", new TokenUsage { InputTokens = 999_999 }, null);
        first.Session.Add(tampered);
        first.LastTurn.Entries = [];
        first.Clarifications.Add(tampered);
        first.ManualCompactions.Entries = [];

        // Premise: every mutation really took effect ON THE RECORD (a no-op mutation could never
        // distinguish a live instance from a detached copy, so these assertions keep the ones below
        // from being vacuous).
        Assert.Equal(sessionCalls + 1, first.Session.Total.Calls);
        Assert.Equal(sessionInput + 999_999, first.Session.Total.InputTokens);
        Assert.Contains(first.Session.Entries, e => e.Model == "tampered");
        Assert.Empty(first.LastTurn.Entries);
        Assert.Equal(0, first.LastTurn.Total.Calls);
        Assert.Equal(clarificationCalls + 1, first.Clarifications.Total.Calls);
        Assert.Empty(first.ManualCompactions.Entries);
        Assert.Equal(0, first.ManualCompactions.Total.Calls);

        var second = composer.GetUsage();
        Assert.NotNull(second);

        Assert.Equal(sessionInput, second!.Session.Total.InputTokens);
        Assert.Equal(sessionCalls, second.Session.Total.Calls);
        Assert.DoesNotContain(second.Session.Entries, e => e.Model == "tampered");
        Assert.NotNull(second.LastTurn);
        Assert.Equal(turnInput, second.LastTurn!.Total.InputTokens);
        Assert.Equal(turnCalls, second.LastTurn.Total.Calls);
        Assert.DoesNotContain(second.LastTurn.Entries, e => e.Model == "tampered");
        Assert.NotEmpty(second.LastTurn.Entries);
        Assert.Equal(clarificationInput, second.Clarifications.Total.InputTokens);
        Assert.Equal(clarificationCalls, second.Clarifications.Total.Calls);
        Assert.DoesNotContain(second.Clarifications.Entries, e => e.Model == "tampered");
        Assert.Equal(manualInput, second.ManualCompactions.Total.InputTokens);
        Assert.Equal(manualCalls, second.ManualCompactions.Total.Calls);
        Assert.DoesNotContain(second.ManualCompactions.Entries, e => e.Model == "tampered");

        // The two records are independent snapshots: the first kept the mutations applied to it, and
        // the second read was unaffected by them.
        Assert.Equal(sessionCalls + 1, first.Session.Total.Calls);
        Assert.Empty(first.LastTurn.Entries);
        Assert.NotEmpty(second.Session.Entries);
    }

    // ── (g) Persistence across a reconnect ──

    /// <summary>
    /// After the session is saved to <c>composer-session.json</c> and a FRESH Composer connects from
    /// that state directory, <c>Session</c> is restored from disk while the in-memory
    /// <c>Clarifications</c> and <c>ManualCompactions</c> start EMPTY in the new instance.
    /// <para>
    /// DISCRIMINATOR: persisting the accumulators (or failing to reload session usage) breaks one of
    /// the two halves of the assertion.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Reconnect_RestoresSessionUsageWhileInMemoryAccumulatorsStartEmpty()
    {
        var stateDir = CreateTempDir();
        long sessionCalls;
        long sessionInput;
        long manualCalls;
        var firstClient = new UsageReportingClient();
        CopilotHiveDbContext? firstDb = null;
        Composer? firstComposer = null;
        Composer? fresh = null;
        CopilotHiveDbContext? freshDb = null;
        var bodyCompleted = false;
        try
        {
            var firstBodyCompleted = false;
            try
            {
                // ── Instance 1: turn + manual compaction + clarification, then a normal dispose ──
                firstDb = CopilotHiveDbContext.CreateInMemory();
                var firstStore = new GoalStore(firstDb, NullLogger<GoalStore>.Instance);
                firstComposer = new Composer(
                    "test-model",
                    NullLogger<Composer>.Instance,
                    firstStore,
                    stateDir: stateDir,
                    chatClientFactory: _ => firstClient);
                await firstComposer.ConnectAsync(TestContext.Current.CancellationToken);

                var turnCompleted = StreamCompletedAsync(firstComposer);
                firstClient.BeginTurn(rounds: 1);
                firstComposer.SendMessage("measured turn");
                await turnCompleted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

                firstClient.CompactionInputTokens = CompactionInputPerCall;
                firstClient.CompactionOutputTokens = CompactionOutputPerCall;
                PopulateSession(GetSession(firstComposer), 20);
                Assert.True(await firstComposer.CompactSessionAsync(TestContext.Current.CancellationToken));

                var queue = new ClarificationQueueService();
                var request = new ClarificationRequest { GoalId = "g-persist", WorkerRole = "coder", Question = "q" };
                queue.Enqueue(request);
                firstClient.BeginTurn(rounds: 1);
                firstClient.ReplyText = "Use JSON.";
                Assert.Equal("Use JSON.", await firstComposer.AnswerClarificationAsync(
                    "g-persist", "q", "ctx", queue, request, TestContext.Current.CancellationToken));

                var before = firstComposer.GetUsage();
                Assert.NotNull(before);
                Assert.True(before!.Session.Total.Calls > 0);
                Assert.True(before.ManualCompactions.Total.Calls > 0);
                Assert.True(before.Clarifications.Total.Calls > 0);

                sessionCalls = before.Session.Total.Calls;
                sessionInput = before.Session.Total.InputTokens;
                manualCalls = before.ManualCompactions.Total.Calls;

                // Premise: the compaction persisted the session file the reconnect will load.
                Assert.True(File.Exists(Path.Combine(stateDir, "composer-session.json")),
                    "the Composer did not persist composer-session.json");
                firstBodyCompleted = true;
            }
            finally
            {
                // Instance 1 is disposed on EVERY path — including a failure in the block above — and
                // each resource is contained individually: a throwing Composer disposal can no longer
                // skip the database disposal.
                var firstCleanupErrors = new List<Exception>();
                await CleanupStepAsync(firstCleanupErrors, async () =>
                {
                    if (firstComposer is not null) await firstComposer.DisposeAsync();
                });
                firstComposer = null;
                await CleanupStepAsync(firstCleanupErrors, () => { firstDb?.Dispose(); return Task.CompletedTask; });
                firstDb = null;

                // Surface cleanup failures only when the body itself succeeded — otherwise the
                // original failure propagates and is never masked by a secondary cleanup error.
                if (firstBodyCompleted) ThrowIfAny(firstCleanupErrors, "Instance-1 cleanup failed");
            }

            // Premise: disposing the Composer left the persisted session in place (nothing on the
            // disposal path may delete it — the file belongs to the session, not the instance).
            Assert.True(File.Exists(Path.Combine(stateDir, "composer-session.json")),
                "disposing the Composer deleted composer-session.json");

            // ── Instance 2: connects from the SAME state directory ──
            freshDb = CopilotHiveDbContext.CreateInMemory();
            var store = new GoalStore(freshDb, NullLogger<GoalStore>.Instance);
            fresh = new Composer(
                "test-model",
                NullLogger<Composer>.Instance,
                store,
                stateDir: stateDir,
                chatClientFactory: _ => new UsageReportingClient());

            await fresh.ConnectAsync(TestContext.Current.CancellationToken);

            // Premise: the connection came from disk.
            Assert.True(fresh.SessionLoadedFromDisk, "the fresh Composer did not load the session from disk");

            var after = fresh.GetUsage();
            Assert.NotNull(after);

            // LastTurn is per-execution, so a fresh instance has none.
            Assert.Null(after!.LastTurn);

            // Session restored, exactly — one Agent bucket (the turn) plus the Compaction bucket
            // (the manual compaction), whose calls together are the session's total.
            Assert.Equal(sessionCalls, after.Session.Total.Calls);
            Assert.Equal(sessionInput, after.Session.Total.InputTokens);
            var restoredAgentCalls = SingleEntry(after.Session, UsageSource.Agent, ModelId).Usage.Calls;
            var restoredCompactionCalls = SingleEntry(after.Session, UsageSource.Compaction, ModelId).Usage.Calls;
            Assert.Equal(sessionCalls, restoredAgentCalls + restoredCompactionCalls);
            Assert.Equal(2, after.Session.Entries.Count);
            Assert.Equal(1, restoredAgentCalls);
            Assert.Equal(1, restoredCompactionCalls);

            // The in-memory accumulators are EMPTY in the new instance even though the first
            // instance had counted a manual compaction — they live since orchestrator start only.
            Assert.True(manualCalls > 0, "the first instance should have counted its manual compaction");
            Assert.Equal(0, after.ManualCompactions.Total.Calls);
            Assert.Empty(after.ManualCompactions.Entries);

            // The clarifications of the first instance were counted there, so the new instance's
            // accumulator is empty rather than carrying them over.
            Assert.Equal(0, after.Clarifications.Total.Calls);
            Assert.Empty(after.Clarifications.Entries);
            bodyCompleted = true;
        }
        finally
        {
            // SECOND instance, its database, then the temp directory — EACH contained, so a throwing
            // disposal can never skip the directory deletion, and on a failed body the original
            // failure is never masked by a secondary cleanup error.
            var cleanupErrors = new List<Exception>();
            await CleanupStepAsync(cleanupErrors, async () =>
            {
                if (fresh is not null) await fresh.DisposeAsync();
            });
            await CleanupStepAsync(cleanupErrors, () => { freshDb?.Dispose(); return Task.CompletedTask; });
            await CleanupStepAsync(cleanupErrors, () =>
            {
                if (Directory.Exists(stateDir)) TestHelpers.ForceDeleteDirectory(stateDir);
                return Task.CompletedTask;
            });

            if (bodyCompleted) ThrowIfAny(cleanupErrors, "Reconnect-test cleanup failed");
        }
    }

    /// <summary>
    /// Runs ONE cleanup step, recording (never propagating) its failure, so the next cleanup step
    /// always runs. Used wherever several resources are released in sequence.
    /// </summary>
    private static async Task CleanupStepAsync(List<Exception> errors, Func<Task> step)
    {
        try { await step(); }
        catch (Exception ex) { errors.Add(ex); }
    }

    /// <summary>Re-throws the collected cleanup failures together, if any.</summary>
    private static void ThrowIfAny(List<Exception> errors, string message)
    {
        if (errors.Count > 0) throw new AggregateException(message, errors);
    }

    // ── (h) PhaseUsageTotals.Merge ──

    /// <summary>
    /// <see cref="PhaseUsageTotals.Merge"/> sums EVERY counter — <c>Calls</c>, both
    /// <c>*ReportedCalls</c> counters and the whole estimated breakdown (coverage counters included)
    /// — and merges by <c>(source, model)</c>.
    /// <para>
    /// DISCRIMINATOR: folding the entries through <c>UsageSummary.Add(UsageEvent)</c> instead of
    /// <c>TokenUsage.Add</c> would report <c>Calls == 2</c> (one per summary) instead of 7 + 4, and
    /// would drop the reported-calls/estimated sums.
    /// </para>
    /// </summary>
    [Fact]
    public void Merge_SumsCallsReportedCallsAndEstimatedBreakdown()
    {
        var first = new UsageSummary(
        [
            new UsageEntry(UsageSource.Agent, "model-a", new TokenUsage
            {
                InputTokens = 100,
                OutputTokens = 10,
                Calls = 7,
                CachedInputTokens = 50,
                CachedInputReportedCalls = 5,
                ReasoningTokens = 70,
                ReasoningReportedCalls = 7,
                Estimated = new EstimatedTokenBreakdown
                {
                    SystemPrompt = 11, ToolDefinitions = 12, UserText = 13, AssistantText = 14,
                    ToolCalls = 15, ToolResults = 16, Reasoning = 17, Images = 18,
                    OutputText = 19, OutputToolCalls = 20, OutputReasoning = 21,
                    InputEstimatedCalls = 22, OutputEstimatedCalls = 23,
                },
            }),
        ]);
        var second = new UsageSummary(
        [
            new UsageEntry(UsageSource.Agent, "model-a", new TokenUsage
            {
                InputTokens = 300,
                OutputTokens = 30,
                Calls = 4,
                CachedInputTokens = 6,
                CachedInputReportedCalls = 0,
                ReasoningTokens = 3,
                ReasoningReportedCalls = 2,
                Estimated = new EstimatedTokenBreakdown
                {
                    SystemPrompt = 1, ToolDefinitions = 2, UserText = 3, AssistantText = 4,
                    ToolCalls = 5, ToolResults = 6, Reasoning = 7, Images = 8,
                    OutputText = 9, OutputToolCalls = 10, OutputReasoning = 11,
                    InputEstimatedCalls = 12, OutputEstimatedCalls = 13,
                },
            }),
        ]);

        var merged = PhaseUsageTotals.Merge([first, second]);

        var entry = Assert.Single(merged.Entries);
        Assert.Equal(UsageSource.Agent, entry.Source);
        Assert.Equal("model-a", entry.Model);

        Assert.Equal(11, entry.Usage.Calls);
        Assert.Equal(400, entry.Usage.InputTokens);
        Assert.Equal(40, entry.Usage.OutputTokens);
        Assert.Equal(56, entry.Usage.CachedInputTokens);
        Assert.Equal(5, entry.Usage.CachedInputReportedCalls);
        Assert.NotEqual(entry.Usage.Calls, entry.Usage.CachedInputReportedCalls);
        Assert.Equal(73, entry.Usage.ReasoningTokens);
        Assert.Equal(9, entry.Usage.ReasoningReportedCalls);

        Assert.Equal(12, entry.Usage.Estimated.SystemPrompt);
        Assert.Equal(14, entry.Usage.Estimated.ToolDefinitions);
        Assert.Equal(16, entry.Usage.Estimated.UserText);
        Assert.Equal(18, entry.Usage.Estimated.AssistantText);
        Assert.Equal(20, entry.Usage.Estimated.ToolCalls);
        Assert.Equal(22, entry.Usage.Estimated.ToolResults);
        Assert.Equal(24, entry.Usage.Estimated.Reasoning);
        Assert.Equal(26, entry.Usage.Estimated.Images);
        Assert.Equal(28, entry.Usage.Estimated.OutputText);
        Assert.Equal(30, entry.Usage.Estimated.OutputToolCalls);
        Assert.Equal(32, entry.Usage.Estimated.OutputReasoning);
        Assert.Equal(34, entry.Usage.Estimated.InputEstimatedCalls);
        Assert.Equal(36, entry.Usage.Estimated.OutputEstimatedCalls);

        // Total aggregates the merged entry.
        Assert.Equal(11, merged.Total.Calls);
        Assert.Equal(400, merged.Total.InputTokens);
    }

    /// <summary>
    /// Bucketing is by <c>(source, model)</c> with an ORDINAL model comparison: case-different model
    /// names stay SEPARATE entries, a null model groups only with null, and distinct sources with the
    /// same model stay separate too.
    /// <para>
    /// DISCRIMINATOR: an ordinal-insensitive (or culture-based) comparer collapses "model-a" and
    /// "MODEL-A" into one entry, so the count/amount assertions fail.
    /// </para>
    /// </summary>
    [Fact]
    public void Merge_KeepsOrdinalModelBucketsAndDistinctSources()
    {
        var lower = new UsageSummary([new UsageEntry(UsageSource.Agent, "model-a", new TokenUsage { InputTokens = 1, Calls = 1 })]);
        var upper = new UsageSummary([new UsageEntry(UsageSource.Agent, "MODEL-A", new TokenUsage { InputTokens = 2, Calls = 1 })]);
        var nullModel = new UsageSummary([new UsageEntry(UsageSource.Agent, null, new TokenUsage { InputTokens = 3, Calls = 1 })]);
        var nullModelAgain = new UsageSummary([new UsageEntry(UsageSource.Agent, null, new TokenUsage { InputTokens = 4, Calls = 1 })]);
        var otherSource = new UsageSummary([new UsageEntry(UsageSource.Compaction, "model-a", new TokenUsage { InputTokens = 9, Calls = 1 })]);

        var merged = PhaseUsageTotals.Merge([lower, upper, nullModel, nullModelAgain, otherSource]);

        Assert.Equal(4, merged.Entries.Count);
        Assert.Equal(1, SingleEntry(merged, UsageSource.Agent, "model-a").Usage.InputTokens);
        Assert.Equal(2, SingleEntry(merged, UsageSource.Agent, "MODEL-A").Usage.InputTokens);
        Assert.Equal(7, SingleEntry(merged, UsageSource.Agent, null).Usage.InputTokens);
        Assert.Equal(9, SingleEntry(merged, UsageSource.Compaction, "model-a").Usage.InputTokens);
        // 1 + 2 + 3 + 4 + 9 — every bucket contributes to the total.
        Assert.Equal(19, merged.Total.InputTokens);
        Assert.Equal(5, merged.Total.Calls);

        // Insertion order is preserved (deterministic rendering).
        Assert.Equal(
            new string?[] { "model-a", "MODEL-A", null, "model-a" },
            merged.Entries.Select(e => e.Model).ToArray());
    }

    /// <summary>
    /// Null summaries in the input are SKIPPED (not treated as an empty bucket), an all-null or empty
    /// input yields an EMPTY non-null summary, and the inputs are never mutated.
    /// <para>
    /// DISCRIMINATOR: an implementation that dereferenced the null entries would throw; one that
    /// mutated the input would change the asserted input totals below.
    /// </para>
    /// </summary>
    [Fact]
    public void Merge_SkipsNullSummariesYieldsEmptySummaryAndNeverMutatesInputs()
    {
        // All-null / empty inputs.
        var empty = PhaseUsageTotals.Merge([null, null]);
        Assert.Empty(empty.Entries);
        Assert.Equal(0, empty.Total.Calls);
        Assert.Equal(0, empty.Total.InputTokens);
        Assert.Empty(PhaseUsageTotals.Merge([]).Entries);
        var first = new UsageSummary(
        [
            new UsageEntry(UsageSource.Agent, "model-a", new TokenUsage { InputTokens = 100, OutputTokens = 10, Calls = 2, CachedInputReportedCalls = 1 }),
            new UsageEntry(UsageSource.SubAgent, null, new TokenUsage { InputTokens = 1_000, Calls = 3 }),
        ]);
        var second = new UsageSummary(
        [
            new UsageEntry(UsageSource.Agent, "model-a", new TokenUsage { InputTokens = 200, OutputTokens = 20, Calls = 4, CachedInputReportedCalls = 2 }),
        ]);

        var merged = PhaseUsageTotals.Merge([first, null, second]);

        Assert.Equal(2, merged.Entries.Count);
        var agentA = SingleEntry(merged, UsageSource.Agent, "model-a");
        Assert.Equal(6, agentA.Usage.Calls);
        Assert.Equal(300, agentA.Usage.InputTokens);
        Assert.Equal(3, agentA.Usage.CachedInputReportedCalls);
        var subAgent = SingleEntry(merged, UsageSource.SubAgent, null);
        Assert.Equal(3, subAgent.Usage.Calls);
        Assert.Equal(1_000, subAgent.Usage.InputTokens);

        // Inputs unchanged: same instance-level numbers and the same entry counts.
        Assert.Equal(2, first.Entries.Count);
        Assert.Equal(5, first.Total.Calls);
        Assert.Equal(1_100, first.Total.InputTokens);
        Assert.Equal(1, first.Entries.Single(e => e.Source == UsageSource.Agent).Usage.CachedInputReportedCalls);

        Assert.Single(second.Entries);
        Assert.Equal(4, second.Total.Calls);
        Assert.Equal(200, second.Total.InputTokens);

        // The result is detached from the inputs: further merging cannot reach back.
        var third = PhaseUsageTotals.Merge([merged, new UsageSummary([new UsageEntry(UsageSource.Agent, "model-a", new TokenUsage { Calls = 1, InputTokens = 1 })])]);
        Assert.Equal(7, SingleEntry(third, UsageSource.Agent, "model-a").Usage.Calls);
        Assert.Equal(6, SingleEntry(merged, UsageSource.Agent, "model-a").Usage.Calls);
    }

    /// <summary>
    /// The public surface this slice added is what the chat UI will consume: the record exposes
    /// exactly the four documented members with the documented nullability.
    /// <para>
    /// DISCRIMINATOR: a renamed or re-typed member (e.g. a non-nullable <c>LastTurn</c>) fails the
    /// reflection assertions, and a record that is not public fails the visibility ones.
    /// </para>
    /// </summary>
    [Fact]
    public void ComposerUsageSnapshot_ExposesTheFourDocumentedMembers()
    {
        var type = typeof(ComposerUsageSnapshot);
        Assert.True(type.IsPublic);
        Assert.Equal("CopilotHive.Orchestration", type.Namespace);

        var lastTurn = type.GetProperty(nameof(ComposerUsageSnapshot.LastTurn));
        Assert.NotNull(lastTurn);
        Assert.Equal(typeof(UsageSummary), lastTurn!.PropertyType);

        var nullable = new NullabilityInfoContext().Create(lastTurn);
        Assert.Equal(NullabilityState.Nullable, nullable.ReadState);

        foreach (var name in new[] { nameof(ComposerUsageSnapshot.Session), nameof(ComposerUsageSnapshot.Clarifications), nameof(ComposerUsageSnapshot.ManualCompactions) })
        {
            var property = type.GetProperty(name);
            Assert.NotNull(property);
            Assert.Equal(typeof(UsageSummary), property!.PropertyType);
            Assert.Equal(NullabilityState.NotNull, new NullabilityInfoContext().Create(property).ReadState);
        }

        // Defaults are empty summaries, never null, so a caller can always read .Total.
        var defaults = new ComposerUsageSnapshot();
        Assert.Null(defaults.LastTurn);
        Assert.Empty(defaults.Session.Entries);
        Assert.Empty(defaults.Clarifications.Entries);
        Assert.Empty(defaults.ManualCompactions.Entries);
    }

    // ── Stub chat client ────────────────────────────────────────────────────

    /// <summary>
    /// A scripted <see cref="IChatClient"/> that reports FIXED usage for every call, so all sums are
    /// exact and machine-checkable.
    /// <list type="bullet">
    ///   <item>Streaming (agent) calls report <see cref="StreamInputPerCall"/> /
    ///   <see cref="StreamOutputPerCall"/> plus cached-input and reasoning counts, and are recorded
    ///   as <see cref="UsageSource.Agent"/> under <see cref="ModelId"/>.</item>
    ///   <item>Non-streaming calls (the context compactor's summaries) report
    ///   <see cref="CompactionInputPerCall"/> / <see cref="CompactionOutputPerCall"/>.</item>
    ///   <item>A turn consumes <c>rounds</c> model calls: tool rounds request
    ///   <c>get_current_time</c> (a real, side-effect-free Composer tool) and the LAST round returns
    ///   <see cref="ReplyText"/> with <see cref="ChatFinishReason.Stop"/>.</item>
    ///   <item><see cref="FailNextRound"/> makes the next streaming round report usage and then throw,
    ///   which SharpCoder converts into a Completed update with <c>Status = "Error"</c> — the shape
    ///   the error-path tests need.</item>
    ///   <item><see cref="Hold"/> holds every streaming round until the returned hold is released,
    ///   giving the test deterministic control over the in-flight window. The gate is released
    ///   idempotently (a double release is a no-op) and the hold signal tells the test exactly when
    ///   the streaming task has entered the held window.</item>
    /// </list>
    /// </summary>
    private sealed class UsageReportingClient : IChatClient
    {
        private readonly object _gateLock = new();
        private TaskCompletionSource? _gate;
        private TaskCompletionSource? _gateAcquired;
        private int _remainingRounds;
        private int _streamingCalls;
        private int _compactionResponses;

        /// <summary>Makes the next streaming round report usage and then throw a provider fault.</summary>
        internal bool FailNextRound { get; set; }

        /// <summary>The text the final streaming round replies with.</summary>
        internal string ReplyText { get; set; } = "All done.";

        /// <summary>Input tokens reported by non-streaming (compaction) calls.</summary>
        internal long CompactionInputTokens { get; set; } = CompactionInputPerCall;

        /// <summary>Output tokens reported by non-streaming (compaction) calls.</summary>
        internal long CompactionOutputTokens { get; set; } = CompactionOutputPerCall;

        /// <summary>How many NON-streaming calls the client has answered (compaction summaries).</summary>
        internal int CompactionResponseCount => Volatile.Read(ref _compactionResponses);

        /// <summary>How many streaming rounds the client has started.</summary>
        internal int StreamingCalls => Volatile.Read(ref _streamingCalls);

        /// <summary>The gate currently holding streaming rounds, or <c>null</c> when none is armed.</summary>
        internal TaskCompletionSource? HeldGate { get { lock (_gateLock) { return _gate; } } }

        public ChatClientMetadata Metadata => new("usage-reporting-stub", null, ModelId);

        /// <summary>Arms a turn that consumes exactly <paramref name="rounds"/> model calls.</summary>
        internal void BeginTurn(int rounds) => _remainingRounds = rounds;

        /// <summary>
        /// Arms a hold: every streaming round waits on the returned signal until
        /// <see cref="ReleaseGate"/> is called. <see cref="GateHold.Acquired"/> completes when the
        /// FIRST round is actually waiting on it, so a test can distinguish "the stream has reached
        /// the held window" from "the stream has not started yet".
        /// </summary>
        internal GateHold Hold()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gateLock)
            {
                _gate = gate;
                _gateAcquired = acquired;
            }

            return new GateHold(gate, acquired, this);
        }

        /// <summary>
        /// Releases the current hold, if any. IDEMPOTENT — safe to call from a <c>finally</c> and
        /// again from the fixture's disposal — and safe to call when no hold is armed.
        /// </summary>
        internal void ReleaseGate()
        {
            TaskCompletionSource? gate;
            lock (_gateLock)
            {
                gate = _gate;
                _gate = null;
                _gateAcquired = null;
            }

            gate?.TrySetResult();
        }

        private TaskCompletionSource? _pin;
        private TaskCompletionSource? _pinAcquired;

        /// <summary>
        /// Arms a PIN: unlike <see cref="Hold"/>, a pin is released ONLY by the returned
        /// <see cref="PinHold.Release"/> — <see cref="ReleaseGate"/> (and therefore the fixture) cannot
        /// release it — and its wait deliberately IGNORES cancellation. A pinned streaming round thus
        /// keeps the actor's message loop genuinely incomplete across Composer disposal, which is the
        /// only way to reach the fixture's "actor not joined" path deterministically, with the test
        /// controlling exactly when the loop may finish.
        /// </summary>
        internal PinHold Pin()
        {
            var pin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gateLock)
            {
                _pin = pin;
                _pinAcquired = acquired;
            }

            return new PinHold(pin, acquired);
        }

        private async Task WaitForPinAsync()
        {
            TaskCompletionSource? pin;
            TaskCompletionSource? acquired;
            lock (_gateLock)
            {
                pin = _pin;
                acquired = _pinAcquired;
                _pin = null;          // one round only
                _pinAcquired = null;
            }

            if (pin is null) return;
            acquired?.TrySetResult();
            await pin.Task;           // no cancellation: only the test may release it
        }

        /// <summary>A test-owned pin on ONE streaming round (see <see cref="Pin"/>).</summary>
        internal sealed class PinHold(TaskCompletionSource pin, TaskCompletionSource acquired)
        {
            /// <summary>Completes when a streaming round is waiting on the pin.</summary>
            internal Task Acquired => acquired.Task;

            /// <summary>True once the pin has been released.</summary>
            internal bool IsReleased => pin.Task.IsCompleted;

            /// <summary>Releases the pin. Idempotent.</summary>
            internal void Release() => pin.TrySetResult();
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Interlocked.Increment(ref _compactionResponses);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary of conversation"))
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = ModelId,
                Usage = new UsageDetails
                {
                    InputTokenCount = CompactionInputTokens,
                    OutputTokenCount = CompactionOutputTokens,
                    CachedInputTokenCount = 40,
                    ReasoningTokenCount = 4,
                },
            };
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await WaitForGateAsync(cancellationToken);
            await WaitForPinAsync();

            Interlocked.Increment(ref _streamingCalls);

            if (FailNextRound)
            {
                FailNextRound = false;
                // Usage arrives BEFORE the fault, so the recorded call carries real tokens while the
                // turn itself ends in SharpCoder's Error status.
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(StreamDetails())])
                {
                    ModelId = ModelId,
                };
                throw new InvalidOperationException("provider failure");
            }

            var remaining = _remainingRounds <= 0 ? 1 : _remainingRounds--;
            var isLast = remaining <= 1;

            yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(StreamDetails())])
            {
                ModelId = ModelId,
            };

            if (isLast)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(ReplyText)])
                {
                    ModelId = ModelId,
                    FinishReason = ChatFinishReason.Stop,
                };
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent(
                $"call-{Guid.NewGuid():N}", "get_current_time", new Dictionary<string, object?>())])
            {
                ModelId = ModelId,
                FinishReason = ChatFinishReason.ToolCalls,
            };
        }

        /// <summary>
        /// Waits on the armed gate (if any) while observing cancellation, and signals the hold's
        /// <see cref="GateHold.Acquired"/> once. Cancellation is OBSERVED so that disposing the
        /// Composer (which cancels the streaming token) always unblocks this wait — a hold can
        /// therefore never strand the actor's shutdown on its own.
        /// </summary>
        private async Task WaitForGateAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource? gate;
            TaskCompletionSource? acquired;
            lock (_gateLock)
            {
                gate = _gate;
                acquired = gate is null ? null : _gateAcquired;
            }

            if (gate is null) return;

            acquired?.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken);
        }

        /// <summary>
        /// A released-and-signalled hold on the client's streaming rounds.
        /// </summary>
        internal sealed class GateHold(
            TaskCompletionSource gate,
            TaskCompletionSource acquired,
            UsageReportingClient owner)
        {
            /// <summary>Completes when the FIRST streaming round is waiting on this hold.</summary>
            internal Task Acquired => acquired.Task;

            /// <summary>True once the hold has been released (directly or via the owner).</summary>
            internal bool IsReleased => gate.Task.IsCompleted;

            /// <summary>Releases the hold. Idempotent and safe to call any number of times.</summary>
            internal void Release() => owner.ReleaseGate();
        }

        private static UsageDetails StreamDetails() => new()
        {
            InputTokenCount = StreamInputPerCall,
            OutputTokenCount = StreamOutputPerCall,
            CachedInputTokenCount = StreamCachedPerCall,
            ReasoningTokenCount = StreamReasoningPerCall,
        };

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? Metadata : null;

        public void Dispose() { }
    }
}

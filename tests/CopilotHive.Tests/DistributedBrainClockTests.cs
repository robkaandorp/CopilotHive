using CopilotHive.Actors;
using CopilotHive.Dashboard;
using CopilotHive.Orchestration;

using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// THE CLOCK-MIGRATION SEAM PROOF for <see cref="DistributedBrain"/>'s time-bounded actor waits:
/// the 5-second connect-reply wait, every <c>AskActorAsync</c> timeout and the three 1-second
/// synchronous query timeouts. Every vector runs on a shared <see cref="ControlledTimeProvider"/>,
/// so the clock moves only when the test moves it: a rendezvous on
/// <see cref="ControlledTimeProvider.WaitForPendingTimerAsync"/> proves production registered the
/// EXACT window before the advance, and <see cref="ControlledTimeProvider.PendingTimerCount"/>
/// proves no producer was left parked on the manual clock afterwards.
/// <para>
/// MAILBOX READ ORDER — confirmed in the production code, not assumed:
/// <list type="number">
/// <item><description><c>StartBrainActorAsync</c> calls <c>actor.Start()</c> and then <c>Tell</c>s the
/// Connect message and waits for its reply. The parking hook runs BEFORE each dequeue, so read 1 is
/// that Connect message.</description></item>
/// <item><description><c>ConnectAsync</c> then calls <c>RegisterMasterSessionFromActor</c>, which
/// UNCONDITIONALLY <c>Tell</c>s a GetStats message and blocks on its reply — even with no session
/// registry configured (only the registry write is conditional). So read 2 is that connect-time
/// GetStats message.</description></item>
/// <item><description>Read 3 is therefore the FIRST POST-CONNECT message: parking read 3 leaves the
/// loop parked before that dequeue while <c>ConnectAsync</c> itself completes.</description></item>
/// </list>
/// </para>
/// <para>
/// TIMER-FIRING FACT these sequences depend on: <see cref="ControlledTimeProvider.Advance"/> fires
/// every due callback synchronously before it returns, so a <see cref="TimeProvider"/>-backed
/// <see cref="CancellationTokenSource"/> runs <c>Cancel()</c> inside the advance and the awaited
/// <c>WaitAsync</c> task transitions to canceled right there. The timed wait's outcome is FIXED once
/// the advance returns — a reply set afterwards cannot change it, which is what makes these vectors
/// deterministic rather than races.
/// </para>
/// <para>
/// The parking seam is the internal <c>DistributedBrain._actorFactory</c> (the factory builds the
/// actor and installs <see cref="Actor{TMessage}.OnBeforeReadAsync"/>) plus a gate that only the Nth
/// read awaits. Every test releases its gate, cancels its own tokens, joins the tasks it started and
/// disposes the brain in <c>finally</c>; normal-completion paths additionally assert that no timer is
/// left pending on the shared clock.
/// </para>
/// </summary>
public sealed class DistributedBrainClockTests
{
    /// <summary>The ask-timeout window every <c>AskActorAsync</c> call site passes.</summary>
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The connect-reply window armed by <c>StartBrainActorAsync</c>.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The 1-second window of the synchronous query fallbacks.</summary>
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Read 1 — the Connect message sent by <c>StartBrainActorAsync</c>.</summary>
    private const int ConnectRead = 1;

    /// <summary>Read 2 — the connect-time GetStats message sent by <c>RegisterMasterSessionFromActor</c>.</summary>
    private const int ConnectStatsRead = 2;

    /// <summary>Read 3 — the first post-connect message.</summary>
    private const int PostConnectRead = 3;

    private const string BrainModel = "copilot/brain-fallback-model";
    private const int BrainMaxContextTokens = 200_000;

    /// <summary>
    /// The model the FACTORY actor reports through stats. Deliberately different from
    /// <see cref="BrainModel"/> so a stats-derived registry entry is distinguishable from the
    /// locally configured fallback one.
    /// </summary>
    private const string ActorStatsModel = "actor-reported-stats-model";

    /// <summary>The context window the factory actor reports; different from the brain's, for the same reason.</summary>
    private const int ActorStatsMaxContextTokens = 50_000;

    // ── Parking seam ─────────────────────────────────────────────────────────

    /// <summary>
    /// Parks the actor loop before the Nth mailbox read. The hook runs before every dequeue, so
    /// blocking read N keeps message N queued while every earlier message has already been handled.
    /// </summary>
    private sealed class MailboxPark(int blockReadNumber)
    {
        private int _reads;

        /// <summary>The read whose dequeue is held back.</summary>
        public int BlockReadNumber { get; } = blockReadNumber;

        /// <summary>Released by the test to let the parked loop continue.</summary>
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed once the loop is parked on the gate.</summary>
        public TaskCompletionSource Parked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Awaited by the actor before every mailbox read.</summary>
        public async Task OnBeforeReadAsync()
        {
            if (Interlocked.Increment(ref _reads) != BlockReadNumber)
                return;

            Parked.TrySetResult();
            await Gate.Task;
        }

        /// <summary>Lets the parked loop continue. Idempotent.</summary>
        public void Release() => Gate.TrySetResult();
    }

    /// <summary>
    /// Builds a brain on the controlled clock whose actor parks the Nth read. The factory actor
    /// reports <see cref="ActorStatsModel"/>/<see cref="ActorStatsMaxContextTokens"/> so the registry
    /// vector can tell the connect-time stats reply apart from the local fallback.
    /// </summary>
    private static DistributedBrain NewBrain(
        ControlledTimeProvider clock,
        string stateDir,
        MailboxPark park,
        LlmSessionRegistry? sessionRegistry = null)
    {
        var brain = new DistributedBrain(BrainModel, NullLogger<DistributedBrain>.Instance,
            stateDir: stateDir,
            chatClient: new FakeChatClient(),
            sessionRegistry: sessionRegistry,
            maxContextTokens: BrainMaxContextTokens,
            timeProvider: clock);

        // Internal test seam: the actor is built here so the read gate is installed before the loop
        // starts. The injected client makes child creation need no provider.
        brain._actorFactory = actorStateDir =>
        {
            var actor = new BrainActor(ActorStatsModel, ActorStatsMaxContextTokens, actorStateDir,
                NullLogger.Instance, injectedChatClient: new FakeChatClient());
            actor.OnBeforeReadAsync = park.OnBeforeReadAsync;
            return actor;
        };

        return brain;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"brain-clock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Rendezvous on the pending timer with exactly <paramref name="dueTime"/> to run. A hang here
    /// means production never registered that window with the controlled clock — i.e. the awaited
    /// window (or the <see cref="TimeProvider"/> seam itself) was removed — so it is reported with the
    /// caller's diagnostic instead of a bare timeout.
    /// </summary>
    private static async Task RendezvousAsync(
        ControlledTimeProvider clock, TimeSpan dueTime, string diagnostic, CancellationToken ct)
    {
        try
        {
            await clock.WaitForPendingTimerAsync(dueTime, ct);
        }
        catch (TimeoutException)
        {
            Assert.Fail(diagnostic);
        }
    }

    /// <summary>Waits until the actor loop is provably parked on its read gate.</summary>
    private static async Task AwaitParkedAsync(MailboxPark park, CancellationToken ct)
    {
        try
        {
            await park.Parked.Task.WaitAsync(ControlledTimeProvider.HangGuard, ct);
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                $"the actor loop never parked before read {park.BlockReadNumber} — the mailbox read "
                + "order this vector gates on has changed");
        }
    }

    /// <summary>
    /// Joins a task the test started, for use exclusively in <c>finally</c> blocks. The await is
    /// bounded and its outcome deliberately discarded: the purpose is only to prove the task ENDED
    /// before the test returned, so an intermediate failure can never leak a live task. Swallowing
    /// here is what preserves the ORIGINAL failure — an exception escaping a <c>finally</c> would
    /// replace it.
    /// </summary>
    private static async Task JoinQuietlyAsync(Task? task)
    {
        if (task is null)
            return;

        try
        {
            await task.WaitAsync(ControlledTimeProvider.HangGuard, CancellationToken.None);
        }
        catch (Exception)
        {
            // Joining only: the test's own assertions decide the verdict.
        }
    }

    /// <summary>Reads the brain's private connected flag — the disconnect assertion of the connect vector.</summary>
    private static bool IsConnected(DistributedBrain brain) =>
        (bool)typeof(DistributedBrain)
            .GetField("_connected", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(brain)!;

    // ── AskActorAsync: the 3-second timeout lives on the injected clock ──────

    /// <summary>
    /// M1 removal proof. The reply to the first post-connect message is withheld, the 3-second window
    /// is rendezvoused on the CONTROLLED clock and only then advanced, so the wait is classified as an
    /// elapsed timeout with the exact production message. Reverting <c>AskActorAsync</c> to
    /// <c>CreateLinkedTokenSource(ct)</c> + <c>CancelAfter(timeout)</c> never registers a 3-second
    /// timer here, the rendezvous hangs, and this test fails with "ask timeout (3 s) never registered
    /// with the controlled clock".
    /// </summary>
    [Fact]
    public async Task InjectOrchestratorInstructionsAsync_ReplyWithheld_TimesOutAtThreeSecondsOnTheControlledClock()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ControlledTimeProvider();
        var stateDir = NewTempDir();
        var park = new MailboxPark(PostConnectRead);
        var brain = NewBrain(clock, stateDir, park);
        Task? askToJoin = null;

        try
        {
            await brain.ConnectAsync(ct).WaitAsync(ControlledTimeProvider.HangGuard, ct);
            await AwaitParkedAsync(park, ct);

            var ask = brain.InjectOrchestratorInstructionsAsync("CLOCK_TEST_INSTRUCTIONS", ct);
            askToJoin = ask;

            await RendezvousAsync(clock, AskTimeout,
                "ask timeout (3 s) never registered with the controlled clock", ct);
            clock.Advance(AskTimeout);

            var ex = await Assert.ThrowsAsync<TimeoutException>(
                () => ask.WaitAsync(ControlledTimeProvider.HangGuard, ct));
            Assert.Equal(
                "BrainActor did not respond to InjectOrchestratorInstructionsMessage within 3s.",
                ex.Message);

            // The timed wait's CTS was disposed as the timeout unwound: nothing is left parked.
            Assert.Equal(0, clock.PendingTimerCount);
        }
        finally
        {
            park.Release();
            await JoinQuietlyAsync(askToJoin);
            await brain.DisposeAsync();
            TestHelpers.ForceDeleteDirectory(stateDir);
        }
    }

    /// <summary>
    /// The mirrored negative: with the same parked mailbox, the rendezvoused 3-second window does NOT
    /// expire on its own — real time passing is no substitute for the injected clock. Once the gate is
    /// released the reply arrives and the call completes successfully, which also proves the ask still
    /// works through the same seam.
    /// </summary>
    [Fact]
    public async Task InjectOrchestratorInstructionsAsync_ReplyWithheldAndClockNotAdvanced_DoesNotTimeOutOnItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ControlledTimeProvider();
        var stateDir = NewTempDir();
        var park = new MailboxPark(PostConnectRead);
        var brain = NewBrain(clock, stateDir, park);
        Task? askToJoin = null;

        try
        {
            await brain.ConnectAsync(ct).WaitAsync(ControlledTimeProvider.HangGuard, ct);
            await AwaitParkedAsync(park, ct);

            var ask = brain.InjectOrchestratorInstructionsAsync("CLOCK_TEST_INSTRUCTIONS", ct);
            askToJoin = ask;

            await RendezvousAsync(clock, AskTimeout,
                "ask timeout (3 s) never registered with the controlled clock", ct);

            Assert.False(ask.IsCompleted,
                "the ask must not complete while the mailbox is parked and the clock has not advanced");

            park.Release();
            await ask.WaitAsync(ControlledTimeProvider.HangGuard, ct);

            Assert.Equal(0, clock.PendingTimerCount);
        }
        finally
        {
            park.Release();
            await JoinQuietlyAsync(askToJoin);
            await brain.DisposeAsync();
            TestHelpers.ForceDeleteDirectory(stateDir);
        }
    }

    /// <summary>
    /// M6 removal proof. Caller cancellation must stay caller cancellation: the caller token is
    /// cancelled while the deadline has NOT expired, so the classification filter
    /// (<c>!ct.IsCancellationRequested &amp;&amp; timeoutCts.IsCancellationRequested</c>) must not fire.
    /// Removing that filter reclassifies this cancellation as an elapsed-actor-timeout — and a
    /// <see cref="TimeoutException"/> is not an <see cref="OperationCanceledException"/> at all, so it
    /// fails the assertion below.
    /// </summary>
    [Fact]
    public async Task InjectOrchestratorInstructionsAsync_CallerCancellation_ThrowsOperationCanceledNotTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ControlledTimeProvider();
        var stateDir = NewTempDir();
        var park = new MailboxPark(PostConnectRead);
        var brain = NewBrain(clock, stateDir, park);
        using var callerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? askToJoin = null;

        try
        {
            await brain.ConnectAsync(ct).WaitAsync(ControlledTimeProvider.HangGuard, ct);
            await AwaitParkedAsync(park, ct);

            var ask = brain.InjectOrchestratorInstructionsAsync("CLOCK_TEST_INSTRUCTIONS", callerCts.Token);
            askToJoin = ask;

            // The deadline is armed and pending; the clock is never advanced, so ONLY the caller token
            // explains the cancelled wait below.
            await RendezvousAsync(clock, AskTimeout,
                "ask timeout (3 s) never registered with the controlled clock", ct);

            callerCts.Cancel();

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ask.WaitAsync(ControlledTimeProvider.HangGuard, ct));
            // Observed identity of a caller-cancelled wait: the awaiter's TaskCanceledException. The
            // mutant yields a TimeoutException here instead, which never reaches this assertion.
            Assert.IsType<TaskCanceledException>(ex);

            Assert.Equal(0, clock.PendingTimerCount);
        }
        finally
        {
            // Guaranteed release for the TEST-OWNED caller token: if parking, the rendezvous or an
            // assertion above failed before the explicit Cancel() in the try, this is what releases the
            // parked ask instead of leaving it waiting on the manual timer, which never fires on its
            // own. Cancel() on an already-cancelled source is a no-op, so the approved success path is
            // unaffected (the primary assertion stays the one made in the try), and it cannot mask the
            // try's failure: an exception leaving the try still propagates through this block, whose
            // steps are non-throwing and whose bounded join deliberately discards its own outcome.
            callerCts.Cancel();

            park.Release();
            await JoinQuietlyAsync(askToJoin);
            await brain.DisposeAsync();
            TestHelpers.ForceDeleteDirectory(stateDir);
        }
    }

    // ── StartBrainActorAsync: the 5-second connect wait ──────────────────────

    /// <summary>
    /// M2 removal proof, and the connect-wait outcome contract. Read 1 is held, so the reply to the
    /// Connect message can never arrive; the 5-second window is rendezvoused on the controlled clock
    /// and advanced FIRST — with the gate still closed the outcome is fixed as timed-out — and the
    /// gate is released strictly afterwards so the actor loop can drain while the failure path disposes
    /// it. Reverting to <c>CancelAfter(TimeSpan.FromSeconds(5))</c> never registers a 5-second timer on
    /// the controlled clock, so the rendezvous hangs and this test fails.
    /// <para>
    /// The observed failure with <c>throwOnFailure: true</c>: the elapsed deadline cancels the linked
    /// wait, the caller token is NOT cancelled so the <c>when (ct.IsCancellationRequested)</c> rethrow
    /// branch is skipped, and the generic catch — after disposing the actor — rethrows. The rethrown
    /// exception is the cancellation from the awaited reply (<see cref="TaskCanceledException"/>), and
    /// the brain must not report itself connected afterwards.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ConnectAsync_ReplyWithheld_TimesOutAtFiveSecondsOnTheControlledClock_AndStaysDisconnected()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ControlledTimeProvider();
        var stateDir = NewTempDir();
        var park = new MailboxPark(ConnectRead);
        var brain = NewBrain(clock, stateDir, park);
        Task? connectToJoin = null;

        try
        {
            var connect = brain.ConnectAsync(ct);
            connectToJoin = connect;
            await AwaitParkedAsync(park, ct);

            await RendezvousAsync(clock, ConnectTimeout,
                "connect timeout (5 s) never registered with the controlled clock", ct);
            clock.Advance(ConnectTimeout);

            // Releasing AFTER the advance: the outcome is already fixed as the timed-out one, and the
            // released loop lets the failure path's actor disposal finish instead of parking on the gate.
            park.Release();

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => connect.WaitAsync(ControlledTimeProvider.HangGuard, ct));
            Assert.IsType<TaskCanceledException>(ex);

            Assert.False(IsConnected(brain), "a failed connect must leave the brain disconnected");
            var notConnected = await Assert.ThrowsAsync<InvalidOperationException>(
                () => brain.ForkSessionForGoalAsync("clock-goal", ct));
            Assert.Equal("Brain not connected. Call ConnectAsync first.", notConnected.Message);

            Assert.Equal(0, clock.PendingTimerCount);
        }
        finally
        {
            park.Release();
            await JoinQuietlyAsync(connectToJoin);
            await brain.DisposeAsync();
            TestHelpers.ForceDeleteDirectory(stateDir);
        }
    }

    // ── The three 1-second synchronous fallbacks ─────────────────────────────

    /// <summary>
    /// M3 removal proof. The read-3 gate keeps the query message queued, the 1-second window is
    /// rendezvoused and advanced, and the synchronous wait must swallow the elapsed window and fall
    /// back to <c>false</c>. Without <c>_timeProvider</c> the window is armed on the real clock, no
    /// 1-second timer appears here, and the rendezvous fails.
    /// </summary>
    [Fact]
    public async Task GoalSessionExists_ReplyWithheld_FallsBackToFalseAfterOneSecondOnTheControlledClock()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ControlledTimeProvider();
        var stateDir = NewTempDir();
        var park = new MailboxPark(PostConnectRead);
        var brain = NewBrain(clock, stateDir, park);
        Task<bool>? existsToJoin = null;

        try
        {
            await brain.ConnectAsync(ct).WaitAsync(ControlledTimeProvider.HangGuard, ct);
            await AwaitParkedAsync(park, ct);

            // The probe blocks synchronously on the reply, so it must not run on the test thread.
            var exists = Task.Run(() => brain.GoalSessionExists("clock-goal"), ct);
            existsToJoin = exists;

            await RendezvousAsync(clock, QueryTimeout,
                "GoalSessionExists 1 s fallback never registered with the controlled clock", ct);
            clock.Advance(QueryTimeout);

            Assert.False(await exists.WaitAsync(ControlledTimeProvider.HangGuard, ct));

            Assert.Equal(0, clock.PendingTimerCount);
        }
        finally
        {
            park.Release();
            await JoinQuietlyAsync(existsToJoin);
            await brain.DisposeAsync();
            TestHelpers.ForceDeleteDirectory(stateDir);
        }
    }

    /// <summary>
    /// M4 removal proof. Same shape as the <c>GoalSessionExists</c> vector: the parked mailbox keeps
    /// the stats query unanswered, the rendezvoused 1-second window expires and the synchronous wait
    /// must fall back to <c>null</c> — a stats-derived value could only come from a reply, which is
    /// impossible while the loop is parked before that dequeue.
    /// </summary>
    [Fact]
    public async Task GetStats_ReplyWithheld_FallsBackToNullAfterOneSecondOnTheControlledClock()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ControlledTimeProvider();
        var stateDir = NewTempDir();
        var park = new MailboxPark(PostConnectRead);
        var brain = NewBrain(clock, stateDir, park);
        Task<BrainStats?>? statsToJoin = null;

        try
        {
            await brain.ConnectAsync(ct).WaitAsync(ControlledTimeProvider.HangGuard, ct);
            await AwaitParkedAsync(park, ct);

            var stats = Task.Run(() => brain.GetStats(), ct);
            statsToJoin = stats;

            await RendezvousAsync(clock, QueryTimeout,
                "GetStats 1 s fallback never registered with the controlled clock", ct);
            clock.Advance(QueryTimeout);

            Assert.Null(await stats.WaitAsync(ControlledTimeProvider.HangGuard, ct));

            Assert.Equal(0, clock.PendingTimerCount);
        }
        finally
        {
            park.Release();
            await JoinQuietlyAsync(statsToJoin);
            await brain.DisposeAsync();
            TestHelpers.ForceDeleteDirectory(stateDir);
        }
    }

    /// <summary>
    /// M5 removal proof. Read 2 — the connect-time GetStats message — is held, so
    /// <c>RegisterMasterSessionFromActor</c> blocks on its 1-second window while the 5-second connect
    /// window is already satisfied (its timer may still be pending until it is disposed, which is why
    /// the rendezvous matches the exact due time, never a timer count). Advancing 1 second makes the
    /// synchronous wait swallow the elapsed window and publish the LOCALLY CONFIGURED fallback: the
    /// brain's own model override and context window, with <c>CurrentTokens == 0</c>. The factory actor
    /// deliberately reports a different model and window, so a stats-derived entry cannot pass these
    /// assertions. Connect completes successfully once the gate is released.
    /// </summary>
    [Fact]
    public async Task ConnectAsync_StatsReplyWithheld_RegistersConfiguredFallbackAfterOneSecondOnTheControlledClock()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ControlledTimeProvider();
        var stateDir = NewTempDir();
        var registry = new LlmSessionRegistry();
        var park = new MailboxPark(ConnectStatsRead);
        var brain = NewBrain(clock, stateDir, park, registry);
        Task? connectToJoin = null;

        try
        {
            // The connect path blocks synchronously inside RegisterMasterSessionFromActor, so it must
            // not run on the test thread.
            var connect = Task.Run(() => brain.ConnectAsync(ct), ct);
            connectToJoin = connect;
            await AwaitParkedAsync(park, ct);

            await RendezvousAsync(clock, QueryTimeout,
                "RegisterMasterSessionFromActor 1 s fallback never registered with the controlled clock", ct);
            clock.Advance(QueryTimeout);
            park.Release();

            await connect.WaitAsync(ControlledTimeProvider.HangGuard, ct);

            var master = Assert.Single(registry.GetAll(), s => s.SessionId == "brain-master");
            Assert.Equal(LlmSessionType.Brain, master.SessionType);
            Assert.Equal("idle", master.Status);
            Assert.Equal(BrainModel, master.Model);
            Assert.NotEqual(ActorStatsModel, master.Model);
            Assert.Equal(BrainMaxContextTokens, master.MaxTokens);
            Assert.NotEqual(ActorStatsMaxContextTokens, master.MaxTokens);
            Assert.Equal(0, master.CurrentTokens);

            Assert.Equal(0, clock.PendingTimerCount);
        }
        finally
        {
            park.Release();
            await JoinQuietlyAsync(connectToJoin);
            await brain.DisposeAsync();
            TestHelpers.ForceDeleteDirectory(stateDir);
        }
    }
}

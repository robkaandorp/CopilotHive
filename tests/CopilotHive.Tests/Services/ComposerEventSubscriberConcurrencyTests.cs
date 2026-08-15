using System.Collections.Concurrent;
using System.Reflection;

using CopilotHive.Orchestration;
using CopilotHive.Services;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Deterministic lock-safety tests for <see cref="ComposerEventSubscriber"/>.
/// <para>
/// <b>Why the earlier designs were rejected.</b> Two previous iterations of this file were not
/// removal-proof:
/// </para>
/// <list type="bullet">
/// <item>
/// A <c>Barrier(2)</c> + <c>Thread.Yield()</c> design only rendezvoused the threads at startup,
/// so a legal schedule could complete every publish and every drain with ZERO conflicting
/// <see cref="Queue{T}"/> operations — green even with the production <c>lock</c> removed.
/// </item>
/// <item>
/// A <c>GatedEventBus</c> design parked the publishing thread either BEFORE
/// <c>OnEventReceived</c> started or AFTER it (and its lock) had already completed. The gate
/// therefore never overlapped an actual subscriber queue operation, and every one of those
/// tests passed with the lock removed. Those tests — and the gate double itself — have been
/// deleted rather than patched: a test that cannot fail when the behaviour it names is deleted
/// earns nothing.
/// </item>
/// <item>
/// The mutual-exclusion helper inferred "blocked" purely from a worker not completing within a
/// 500 ms window. A worker descheduled after signalling start but before reaching monitor
/// contention would satisfy that inference even with no lock at all.
/// </item>
/// </list>
/// <para>
/// <b>What replaces them.</b> Two layers, both of which fail deterministically when any
/// <c>lock</c> is removed from <see cref="ComposerEventSubscriber"/>:
/// </para>
/// <list type="number">
/// <item>
/// <b>Mutual-exclusion proofs</b> (one per operation). The test thread acquires the
/// subscriber's private <c>_lock</c> monitor by reflection and holds it. A worker thread
/// invokes one public operation. The harness then resolves a two-outcome decision by
/// OBSERVED STATE, never by elapsed time: either the worker reaches genuine monitor
/// contention (<see cref="ThreadState.WaitSleepJoin"/> after it has signalled that it is
/// about to call the operation) — which proves the operation takes the lock — or the worker
/// COMPLETES while the monitor is held, which proves it does not. Completion-while-held fails
/// the test immediately. After <c>Monitor.Exit</c> the operation must finish and leave the
/// buffer in exactly the right state.
/// </item>
/// <item>
/// <b>Sustained real-contention trials.</b> A publisher and a reader run against the same
/// subscriber simultaneously so genuine concurrent <c>Enqueue</c> / <c>ToList</c> /
/// <c>Clear</c> calls occur, asserting exact conservation and FIFO order. Each trial stays
/// strictly under the 50-entry <c>MaxBufferSize</c> cap so overflow can never cause a false
/// failure.
/// </item>
/// </list>
/// <para>
/// Every test drives its threads through <see cref="ConcurrencyScope"/>, which enforces a
/// strict teardown order on EVERY exit path — success, assertion failure, or exception:
/// exit held monitors → release gates → join workers and confirm actual exit → verify captured
/// worker exceptions → and only then dispose synchronization primitives. Disposal is
/// conditional on confirmed exit: if a worker is still alive after its monitor and gates were
/// released, the primitives are left undisposed and the leak is reported, so a live worker can
/// never touch a disposed handle.
/// </para>
/// </summary>
public sealed class ComposerEventSubscriberConcurrencyTests
{
    /// <summary>Upper bound for joining a worker thread. Exceeding it fails the test.</summary>
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Upper bound for resolving the blocked-vs-completed decision, and for barrier rendezvous.</summary>
    private static readonly TimeSpan DecisionTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Production cap mirrored from <c>ComposerEventSubscriber.MaxBufferSize</c>. Every test in
    /// this class stays strictly below it so overflow can never produce a false failure —
    /// cap behaviour is covered by the dedicated overflow tests in <c>EventBusTests</c>.
    /// </summary>
    private const int MaxBufferSize = 50;

    private static SystemEvent Evt(string id, EventType type = EventType.GoalCompleted) =>
        new(type, $"msg-{id}", GoalId: id);

    private static string[] Ids(IEnumerable<SystemEvent> events) =>
        events.Select(e => e.GoalId!).ToArray();

    // ────────────────────────────────────────────────────────────────────────────────
    //  Failure-safe thread/gate lifetime management  (MAJOR 2)
    // ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Owns every worker thread, held monitor, and synchronization primitive a test creates,
    /// and guarantees orderly teardown on EVERY exit path — success, assertion failure, or
    /// unexpected exception.
    /// <para>
    /// <b>Cleanup ordering is strict and load-bearing:</b>
    /// </para>
    /// <list type="number">
    /// <item><b>Exit every held monitor.</b> Released first so a worker blocked acquiring the
    /// subscriber's <c>_lock</c> can proceed. Every operation under test performs exactly one
    /// lock acquisition, so once the monitor is free the worker cannot block on it again and
    /// the join below is guaranteed to complete.</item>
    /// <item><b>Release every gate</b>, so a worker parked on a <see cref="ManualResetEventSlim"/>
    /// can also make progress and run to completion.</item>
    /// <item><b>Join every worker and CONFIRM actual exit</b> (<c>Join</c> returned <c>true</c>
    /// AND <c>IsAlive == false</c>).</item>
    /// <item><b>Verify the captured worker-exception collection.</b></item>
    /// <item><b>Only then dispose</b> the synchronization primitives.</item>
    /// </list>
    /// <para>
    /// <b>Disposal is conditional on confirmed exit.</b> If a worker still has not exited after
    /// its monitor and gates were released, the primitives are deliberately NOT disposed —
    /// disposing them under a live thread produces <see cref="ObjectDisposedException"/> on that
    /// worker and contaminates later tests. Instead the leak is reported loudly and the
    /// primitives are left for process teardown to reclaim. That tripwire cannot fire while the
    /// production locks are intact, precisely because of the single-acquisition argument above.
    /// </para>
    /// </summary>
    private sealed class ConcurrencyScope : IDisposable
    {
        private readonly List<(Thread Thread, string Name)> _threads = [];
        private readonly List<ManualResetEventSlim> _gatesToRelease = [];
        private readonly List<IDisposable> _disposeAfterJoin = [];
        private readonly List<object> _heldMonitors = [];
        private readonly ConcurrentQueue<(string Name, Exception Error)> _workerErrors = new();
        private bool _disposed;
        private bool _verified;

        /// <summary>
        /// Starts a tracked background worker. Any exception escaping <paramref name="body"/> is
        /// captured (never allowed to terminate the process or leak into a later test) and
        /// surfaced on the test thread by <see cref="CompleteAndVerify"/>.
        /// </summary>
        public Thread StartWorker(string name, Action body)
        {
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { _workerErrors.Enqueue((name, ex)); }
            })
            { IsBackground = true, Name = name };

            _threads.Add((thread, name));
            thread.Start();
            return thread;
        }

        /// <summary>
        /// Acquires <paramref name="lockObject"/> and records the hold, so that the monitor is
        /// released FIRST during teardown even if the test body throws while holding it. This
        /// makes "never leave the monitor held during disposal" a structural invariant of the
        /// scope rather than something every call site must remember to do.
        /// </summary>
        public void EnterMonitor(object lockObject)
        {
            Monitor.Enter(lockObject);
            // Recorded only after Enter succeeds, so a failed acquisition is never "released".
            _heldMonitors.Add(lockObject);
        }

        /// <summary>
        /// Releases a monitor previously taken via <see cref="EnterMonitor"/>. Idempotent: a
        /// monitor already released (explicitly or during teardown) is ignored, so call sites
        /// can safely release in a <c>finally</c> without double-exit risk.
        /// </summary>
        public void ExitMonitor(object lockObject)
        {
            var index = _heldMonitors.FindLastIndex(m => ReferenceEquals(m, lockObject));
            if (index < 0) return;

            _heldMonitors.RemoveAt(index);
            try { Monitor.Exit(lockObject); }
            catch (SynchronizationLockException) { /* not held by this thread */ }
        }

        /// <summary>
        /// Creates a gate owned by the scope. Gates registered here are unconditionally
        /// <c>Set()</c> during teardown so a parked worker can always make progress and exit,
        /// and are disposed only after every worker has been confirmed exited.
        /// </summary>
        public ManualResetEventSlim CreateGate(bool initialState = false)
        {
            var gate = new ManualResetEventSlim(initialState);
            _gatesToRelease.Add(gate);
            _disposeAfterJoin.Add(gate);
            return gate;
        }

        /// <summary>
        /// Creates a barrier owned by the scope, disposed only after every worker has exited.
        /// Callers must use <see cref="SignalAndWaitBounded"/> — never the unbounded overload.
        /// </summary>
        public Barrier CreateBarrier(int participantCount)
        {
            var barrier = new Barrier(participantCount);
            _disposeAfterJoin.Add(barrier);
            return barrier;
        }

        /// <summary>Registers a disposable to be disposed only after all workers have exited.</summary>
        public T RegisterForDisposal<T>(T disposable) where T : IDisposable
        {
            _disposeAfterJoin.Add(disposable);
            return disposable;
        }

        /// <summary>Records a worker-side failure discovered by the worker itself.</summary>
        public void RecordWorkerError(string name, Exception error) => _workerErrors.Enqueue((name, error));

        /// <summary>
        /// Happy-path verification: every worker joined within the bound, actually exited, and
        /// reported no exception. Called at the end of each test body.
        /// </summary>
        public void CompleteAndVerify()
        {
            foreach (var (thread, name) in _threads)
            {
                var joined = thread.Join(JoinTimeout);
                Assert.True(joined,
                    $"Worker '{name}' did not exit within {JoinTimeout.TotalSeconds}s — deadlock or lost wakeup.");
                Assert.False(thread.IsAlive, $"Worker '{name}' reported joined but is still alive.");
            }

            if (!_workerErrors.IsEmpty)
                Assert.Fail(DescribeWorkerErrors());

            _verified = true;
        }

        private string DescribeWorkerErrors()
        {
            var details = string.Join(
                Environment.NewLine,
                _workerErrors.Select(e => $"  [{e.Name}] {e.Error.GetType().Name}: {e.Error.Message}"));
            return $"Worker thread(s) failed:{Environment.NewLine}{details}";
        }

        /// <summary>
        /// Teardown honouring the strict ordering documented on the type: exit monitors →
        /// release gates → join and confirm exit → verify worker errors → dispose.
        /// <para>
        /// Disposal happens ONLY on the confirmed-exit path. A worker that is still alive after
        /// its monitor and gates were released indicates a genuine hang; that is reported by
        /// throwing rather than swallowed, because silently leaking a live thread into
        /// subsequent tests is worse than surfacing a second failure. The primitives are left
        /// undisposed for process teardown so the live worker can never touch a disposed handle.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // (a) Release every monitor this scope still holds, FIRST — a worker blocked
            //     acquiring the subscriber's lock cannot exit until this happens.
            for (var i = _heldMonitors.Count - 1; i >= 0; i--)
            {
                try { Monitor.Exit(_heldMonitors[i]); }
                catch (SynchronizationLockException) { /* not held by this thread */ }
            }
            _heldMonitors.Clear();

            // (b) Unblock anything parked on a gate, so no worker waits forever.
            foreach (var gate in _gatesToRelease)
            {
                try { gate.Set(); }
                catch (ObjectDisposedException) { /* already torn down */ }
            }

            // (c) Join every worker and CONFIRM it actually exited. The join result is
            //     load-bearing here: it decides whether disposal below is safe at all.
            var stillAlive = new List<string>();
            foreach (var (thread, name) in _threads)
            {
                bool exited;
                try { exited = thread.Join(JoinTimeout) && !thread.IsAlive; }
                catch (ThreadStateException) { exited = true; /* never started */ }

                if (!exited)
                    stillAlive.Add(name);
            }

            // (d) A worker that never exited means disposal is unsafe: skip it entirely and
            //     fail loudly so the hang is visible. Process teardown reclaims the primitives.
            if (stillAlive.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Worker thread(s) [{string.Join(", ", stillAlive)}] were still alive " +
                    $"{JoinTimeout.TotalSeconds}s after every monitor and gate was released. " +
                    "Synchronization primitives were deliberately NOT disposed, because disposing " +
                    "them under a live thread would raise ObjectDisposedException on that worker " +
                    "and contaminate later tests.");
            }

            // (e) Surface a late worker failure that arrived after the body already declared
            //     success. When the body did NOT reach CompleteAndVerify it is unwinding from a
            //     primary failure, and throwing here would mask that more informative exception.
            if (_verified && !_workerErrors.IsEmpty)
                throw new InvalidOperationException(DescribeWorkerErrors());

            // (f) Every worker is confirmed exited — disposal is now safe.
            foreach (var disposable in _disposeAfterJoin)
            {
                try { disposable.Dispose(); }
                catch (ObjectDisposedException) { /* idempotent */ }
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────────────
    //  Mutual-exclusion proof harness  (MAJOR 1)
    // ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reads the subscriber's private <c>_lock</c> monitor object via reflection.</summary>
    private static object GetLockObject(ComposerEventSubscriber subscriber)
    {
        var field = typeof(ComposerEventSubscriber).GetField("_lock",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_lock field not found on ComposerEventSubscriber");
        return field.GetValue(subscriber)
            ?? throw new InvalidOperationException("_lock was null");
    }

    /// <summary>How the blocked-vs-completed decision resolved.</summary>
    private enum ContentionOutcome
    {
        /// <summary>Neither state was observed within the bound (harness/environment problem).</summary>
        Undetermined,

        /// <summary>The worker reached genuine monitor contention — the operation takes the lock.</summary>
        BlockedOnMonitor,

        /// <summary>The worker finished while the monitor was held — the operation does NOT take the lock.</summary>
        CompletedWhileHeld,
    }

    /// <summary>
    /// Resolves, by OBSERVED THREAD STATE rather than elapsed time, whether
    /// <paramref name="worker"/> is genuinely blocked acquiring a monitor or has run to
    /// completion.
    /// <para>
    /// This is the crux of the removal proof. The previous helper waited a fixed 500 ms and
    /// treated "did not finish" as evidence of blocking — which a worker merely descheduled
    /// before reaching the lock also satisfies. Here the loop spins until one of two mutually
    /// exclusive, terminal, directly observable conditions holds:
    /// </para>
    /// <list type="bullet">
    /// <item><paramref name="workerCompleted"/> is set → the operation ran to completion while
    /// the monitor was held, so it cannot have acquired it.</item>
    /// <item>The worker has signalled <paramref name="aboutToCallOperation"/> AND its
    /// <see cref="Thread.ThreadState"/> reports <see cref="ThreadState.WaitSleepJoin"/> → it is
    /// parked inside the runtime waiting on the contended monitor.</item>
    /// </list>
    /// <para>
    /// The elapsed-time bound remains only as a safety valve against an unresponsive
    /// environment; it is never itself treated as evidence of correctness.
    /// </para>
    /// </summary>
    private static ContentionOutcome ResolveContentionOutcome(
        Thread worker,
        ManualResetEventSlim aboutToCallOperation,
        ManualResetEventSlim workerCompleted)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        var spin = new SpinWait();

        while (deadline.Elapsed < DecisionTimeout)
        {
            // Completion while the monitor is held is decisive: the operation is unsynchronized.
            if (workerCompleted.IsSet)
                return ContentionOutcome.CompletedWhileHeld;

            // Only meaningful once the worker says it is at the call site: before that, a
            // WaitSleepJoin state could belong to unrelated startup work.
            if (aboutToCallOperation.IsSet)
            {
                var state = worker.ThreadState;

                // Re-check completion first — a worker that finished between the two reads must
                // be reported as completed, never as blocked.
                if (workerCompleted.IsSet)
                    return ContentionOutcome.CompletedWhileHeld;

                if ((state & ThreadState.WaitSleepJoin) != 0)
                    return ContentionOutcome.BlockedOnMonitor;

                if ((state & ThreadState.Stopped) != 0)
                    return ContentionOutcome.CompletedWhileHeld;
            }

            spin.SpinOnce();
        }

        return ContentionOutcome.Undetermined;
    }

    /// <summary>
    /// Proves <paramref name="operation"/> acquires the subscriber's monitor.
    /// <para>
    /// The test thread holds <c>_lock</c> for the whole observation window. The worker signals
    /// immediately before invoking the operation — as close to the monitor-acquisition attempt
    /// as managed code allows — and the outcome is then resolved from observed state. Blocking
    /// is required; completion while held fails. After release the operation must finish, and
    /// the caller asserts the resulting buffer state.
    /// </para>
    /// </summary>
    private static void AssertOperationTakesMonitor(
        ConcurrencyScope scope,
        ComposerEventSubscriber subscriber,
        string operationName,
        Action operation)
    {
        var lockObject = GetLockObject(subscriber);

        var aboutToCallOperation = scope.CreateGate();
        var workerCompleted = scope.CreateGate();

        ContentionOutcome outcome;
        Thread worker;

        // Taken through the scope so teardown releases it FIRST on every exit path — including
        // an assertion failure inside this method — before any worker join or disposal.
        scope.EnterMonitor(lockObject);
        try
        {
            worker = scope.StartWorker($"{operationName} worker", () =>
            {
                try
                {
                    // Signalled as late as possible before the call so the window between this
                    // flag and the monitor-acquisition attempt is as small as managed code allows.
                    aboutToCallOperation.Set();
                    operation();
                }
                finally
                {
                    workerCompleted.Set();
                }
            });

            outcome = ResolveContentionOutcome(worker, aboutToCallOperation, workerCompleted);
        }
        finally
        {
            scope.ExitMonitor(lockObject);
        }

        Assert.False(outcome == ContentionOutcome.CompletedWhileHeld,
            $"{operationName} ran to completion while another thread held the subscriber's " +
            "_lock monitor. It therefore does not acquire the lock, so concurrent access to the " +
            "pending-event queue is unsynchronized.");

        Assert.False(outcome == ContentionOutcome.Undetermined,
            $"{operationName} neither reached monitor contention nor completed within " +
            $"{DecisionTimeout.TotalSeconds}s — the contention proof could not be established.");

        Assert.Equal(ContentionOutcome.BlockedOnMonitor, outcome);

        // Released: the operation must now finish promptly (no lost wakeup, no deadlock).
        var finishedAfterRelease = workerCompleted.Wait(JoinTimeout);
        Assert.True(finishedAfterRelease,
            $"{operationName} did not complete within {JoinTimeout.TotalSeconds}s after the " +
            "monitor was released — deadlock or lost wakeup.");

        var joined = worker.Join(JoinTimeout);
        Assert.True(joined, $"{operationName} worker did not exit within {JoinTimeout.TotalSeconds}s.");
    }

    // ────────────────────────────────────────────────────────────────────────────────
    //  0. Harness self-check — the contention detector must not be vacuous
    // ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates the detector itself against a known-blocking operation: a worker that calls
    /// <see cref="Monitor.Enter(object)"/> on the held monitor directly MUST be reported as
    /// <see cref="ContentionOutcome.BlockedOnMonitor"/>. Without this, a detector that always
    /// returned "blocked" would make every mutual-exclusion test vacuous.
    /// </summary>
    [Fact]
    public void Harness_WorkerBlockingOnHeldMonitor_IsDetectedAsBlocked()
    {
        using var scope = new ConcurrencyScope();
        var bus = new EventBus();
        var subscriber = scope.RegisterForDisposal(new ComposerEventSubscriber(bus));
        var lockObject = GetLockObject(subscriber);

        var aboutToCall = scope.CreateGate();
        var completed = scope.CreateGate();

        ContentionOutcome outcome;
        scope.EnterMonitor(lockObject);
        Thread worker;
        try
        {
            worker = scope.StartWorker("direct monitor worker", () =>
            {
                try
                {
                    aboutToCall.Set();
                    Monitor.Enter(lockObject);
                    Monitor.Exit(lockObject);
                }
                finally { completed.Set(); }
            });

            outcome = ResolveContentionOutcome(worker, aboutToCall, completed);
        }
        finally
        {
            scope.ExitMonitor(lockObject);
        }

        Assert.Equal(ContentionOutcome.BlockedOnMonitor, outcome);
        Assert.True(completed.Wait(JoinTimeout, TestContext.Current.CancellationToken),
            "Worker did not complete after the monitor was released.");

        scope.CompleteAndVerify();
    }

    /// <summary>
    /// The negative half of the self-check: a worker that does NOT touch the monitor must be
    /// reported as <see cref="ContentionOutcome.CompletedWhileHeld"/>. This is exactly the
    /// signal that fires when a production <c>lock</c> is removed, so proving the detector
    /// produces it guarantees the mutual-exclusion tests can actually fail.
    /// </summary>
    [Fact]
    public void Harness_WorkerNotTouchingMonitor_IsDetectedAsCompletedWhileHeld()
    {
        using var scope = new ConcurrencyScope();
        var bus = new EventBus();
        var subscriber = scope.RegisterForDisposal(new ComposerEventSubscriber(bus));
        var lockObject = GetLockObject(subscriber);

        var aboutToCall = scope.CreateGate();
        var completed = scope.CreateGate();

        ContentionOutcome outcome;
        scope.EnterMonitor(lockObject);
        try
        {
            var worker = scope.StartWorker("lock-free worker", () =>
            {
                try
                {
                    aboutToCall.Set();
                    // Deliberately does not acquire the monitor — models an unsynchronized operation.
                }
                finally { completed.Set(); }
            });

            outcome = ResolveContentionOutcome(worker, aboutToCall, completed);
        }
        finally
        {
            scope.ExitMonitor(lockObject);
        }

        Assert.Equal(ContentionOutcome.CompletedWhileHeld, outcome);

        scope.CompleteAndVerify();
    }

    // ────────────────────────────────────────────────────────────────────────────────
    //  1. Mutual exclusion: every public operation must take the monitor
    // ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MutualExclusion_OnEventReceived_BlocksWhileMonitorHeld_ThenEnqueuesExactly()
    {
        using var scope = new ConcurrencyScope();
        var bus = new EventBus();
        var subscriber = scope.RegisterForDisposal(new ComposerEventSubscriber(bus));

        bus.Publish(Evt("m-1"));
        Assert.Equal(["m-1"], Ids(subscriber.PeekPendingEvents()));

        // The enqueue path runs inside the subscriber's OnEventReceived callback.
        AssertOperationTakesMonitor(scope, subscriber, "OnEventReceived (publish)",
            () => bus.Publish(Evt("m-2")));

        // EXACT state after the blocked publish completes: contents, order and count.
        Assert.Equal(["m-1", "m-2"], Ids(subscriber.PeekPendingEvents()));

        scope.CompleteAndVerify();
    }

    [Fact]
    public void MutualExclusion_DrainPendingEvents_BlocksWhileMonitorHeld_ThenDrainsExactly()
    {
        using var scope = new ConcurrencyScope();
        var bus = new EventBus();
        var subscriber = scope.RegisterForDisposal(new ComposerEventSubscriber(bus));

        bus.Publish(Evt("m-1"));
        bus.Publish(Evt("m-2"));

        List<SystemEvent>? drained = null;
        AssertOperationTakesMonitor(scope, subscriber, "DrainPendingEvents",
            () => drained = subscriber.DrainPendingEvents());

        // EXACT state: both events returned in FIFO order and the buffer emptied.
        Assert.NotNull(drained);
        Assert.Equal(["m-1", "m-2"], Ids(drained!));
        Assert.Empty(subscriber.PeekPendingEvents());

        scope.CompleteAndVerify();
    }

    [Fact]
    public void MutualExclusion_PeekPendingEvents_BlocksWhileMonitorHeld_ThenPeeksExactly()
    {
        using var scope = new ConcurrencyScope();
        var bus = new EventBus();
        var subscriber = scope.RegisterForDisposal(new ComposerEventSubscriber(bus));

        bus.Publish(Evt("m-1"));
        bus.Publish(Evt("m-2"));

        List<SystemEvent>? peeked = null;
        AssertOperationTakesMonitor(scope, subscriber, "PeekPendingEvents",
            () => peeked = subscriber.PeekPendingEvents());

        // EXACT state: both returned in FIFO order, and peek did NOT clear the buffer.
        Assert.NotNull(peeked);
        Assert.Equal(["m-1", "m-2"], Ids(peeked!));
        Assert.Equal(["m-1", "m-2"], Ids(subscriber.PeekPendingEvents()));

        scope.CompleteAndVerify();
    }

    [Fact]
    public void MutualExclusion_RestoreEvents_BlocksWhileMonitorHeld_ThenRestoresExactly()
    {
        using var scope = new ConcurrencyScope();
        var bus = new EventBus();
        var subscriber = scope.RegisterForDisposal(new ComposerEventSubscriber(bus));

        bus.Publish(Evt("later-1", EventType.GoalFailed));
        var toRestore = new List<SystemEvent> { Evt("m-1"), Evt("m-2") };

        AssertOperationTakesMonitor(scope, subscriber, "RestoreEvents",
            () => subscriber.RestoreEvents(toRestore));

        // EXACT state: restored events land ahead of the pre-existing arrival, in FIFO order.
        Assert.Equal(["m-1", "m-2", "later-1"], Ids(subscriber.PeekPendingEvents()));

        scope.CompleteAndVerify();
    }

    // ────────────────────────────────────────────────────────────────────────────────
    //  2. Sustained real contention (genuine simultaneous Queue<T> operations)
    // ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Bounded barrier rendezvous; an unbounded <c>SignalAndWait()</c> could hang the suite.</summary>
    private static void SignalAndWaitBounded(ConcurrencyScope scope, Barrier barrier, string who)
    {
        try
        {
            if (!barrier.SignalAndWait(DecisionTimeout, TestContext.Current.CancellationToken))
                scope.RecordWorkerError(who, new TimeoutException(
                    $"Barrier rendezvous for '{who}' timed out after {DecisionTimeout.TotalSeconds}s."));
        }
        catch (Exception ex)
        {
            scope.RecordWorkerError(who, ex);
            throw;
        }
    }

    /// <summary>
    /// Runs many independent trials in which a publisher and a drainer operate on the SAME
    /// subscriber at the same time, so real concurrent <c>Enqueue</c> / <c>ToList</c> /
    /// <c>Clear</c> calls occur rather than merely being scheduled near each other.
    /// <para>
    /// Each trial publishes 40 events — strictly below the 50-entry cap — and asserts exact
    /// conservation and FIFO order over everything the drainer collected plus whatever
    /// remained. Without the subscriber's lock the <c>ToList</c>/<c>Clear</c> pair races
    /// <c>Enqueue</c> and deterministically drops, duplicates or corrupts entries.
    /// </para>
    /// </summary>
    [Fact]
    public void SustainedContention_PublishVersusDrain_ConservesEveryEventInOrder()
    {
        const int trials = 60;
        const int eventsPerTrial = 40; // < MaxBufferSize (50): overflow can never cause failure

        Assert.True(eventsPerTrial < MaxBufferSize,
            "The contention trial must stay under MaxBufferSize so overflow cannot cause a false failure.");

        for (var trial = 0; trial < trials; trial++)
        {
            using var scope = new ConcurrencyScope();
            var bus = new EventBus();
            var subscriber = scope.RegisterForDisposal(new ComposerEventSubscriber(bus));

            var collected = new ConcurrentQueue<SystemEvent>();
            var publisherFinished = scope.CreateGate();
            var bothReady = scope.CreateBarrier(2);

            scope.StartWorker($"publisher (trial {trial})", () =>
            {
                SignalAndWaitBounded(scope, bothReady, $"publisher (trial {trial})");
                try
                {
                    for (var i = 0; i < eventsPerTrial; i++)
                        bus.Publish(Evt($"e-{i:D2}"));
                }
                finally { publisherFinished.Set(); }
            });

            scope.StartWorker($"drainer (trial {trial})", () =>
            {
                SignalAndWaitBounded(scope, bothReady, $"drainer (trial {trial})");
                // Tight, unthrottled drain loop: maximises genuine overlap with the enqueues.
                while (!publisherFinished.IsSet)
                {
                    foreach (var e in subscriber.DrainPendingEvents())
                        collected.Enqueue(e);
                }
                foreach (var e in subscriber.DrainPendingEvents())
                    collected.Enqueue(e);
            });

            scope.CompleteAndVerify();

            var observed = new List<SystemEvent>(collected);
            observed.AddRange(subscriber.DrainPendingEvents());

            var observedIds = Ids(observed);
            var expectedIds = Enumerable.Range(0, eventsPerTrial).Select(i => $"e-{i:D2}").ToArray();

            // EXACT state: every published event observed exactly once, in publication order.
            Assert.Equal(expectedIds, observedIds);
        }
    }

    /// <summary>
    /// Same sustained-contention shape, but the reader alternates <c>PeekPendingEvents</c> with
    /// <c>DrainPendingEvents</c> so the read-only path is also exercised concurrently with live
    /// enqueues. A peek over a torn <see cref="Queue{T}"/> (lock removed) throws or returns
    /// inconsistent contents.
    /// </summary>
    [Fact]
    public void SustainedContention_PublishVersusPeekAndDrain_ConservesEveryEventInOrder()
    {
        const int trials = 60;
        const int eventsPerTrial = 40; // < MaxBufferSize (50)

        for (var trial = 0; trial < trials; trial++)
        {
            using var scope = new ConcurrencyScope();
            var bus = new EventBus();
            var subscriber = scope.RegisterForDisposal(new ComposerEventSubscriber(bus));

            var collected = new ConcurrentQueue<SystemEvent>();
            var publisherFinished = scope.CreateGate();
            var bothReady = scope.CreateBarrier(2);

            scope.StartWorker($"publisher (trial {trial})", () =>
            {
                SignalAndWaitBounded(scope, bothReady, $"publisher (trial {trial})");
                try
                {
                    for (var i = 0; i < eventsPerTrial; i++)
                        bus.Publish(Evt($"e-{i:D2}"));
                }
                finally { publisherFinished.Set(); }
            });

            scope.StartWorker($"reader (trial {trial})", () =>
            {
                SignalAndWaitBounded(scope, bothReady, $"reader (trial {trial})");
                while (!publisherFinished.IsSet)
                {
                    // Peek only snapshots; it must never lose or duplicate anything.
                    var peeked = subscriber.PeekPendingEvents();
                    var drained = subscriber.DrainPendingEvents();

                    if (peeked.Count > MaxBufferSize)
                        throw new InvalidOperationException(
                            $"Peek returned {peeked.Count} events, exceeding MaxBufferSize ({MaxBufferSize}) — torn read.");

                    foreach (var e in drained)
                        collected.Enqueue(e);
                }
                foreach (var e in subscriber.DrainPendingEvents())
                    collected.Enqueue(e);
            });

            scope.CompleteAndVerify();

            var observed = new List<SystemEvent>(collected);
            observed.AddRange(subscriber.DrainPendingEvents());

            var observedIds = Ids(observed);
            var expectedIds = Enumerable.Range(0, eventsPerTrial).Select(i => $"e-{i:D2}").ToArray();

            Assert.Equal(expectedIds, observedIds);
        }
    }

    /// <summary>
    /// Sustained contention over the drain → restore cycle: the reader drains and immediately
    /// restores (modelling a rejected Composer send) while the publisher keeps enqueuing.
    /// Nothing may be lost or duplicated.
    /// </summary>
    [Fact]
    public void SustainedContention_PublishVersusDrainAndRestore_ConservesEveryEvent()
    {
        const int trials = 40;
        const int eventsPerTrial = 30; // < MaxBufferSize (50), with headroom for restore re-entry

        for (var trial = 0; trial < trials; trial++)
        {
            using var scope = new ConcurrencyScope();
            var bus = new EventBus();
            var subscriber = scope.RegisterForDisposal(new ComposerEventSubscriber(bus));

            var collected = new ConcurrentQueue<SystemEvent>();
            var publisherFinished = scope.CreateGate();
            var bothReady = scope.CreateBarrier(2);

            scope.StartWorker($"publisher (trial {trial})", () =>
            {
                SignalAndWaitBounded(scope, bothReady, $"publisher (trial {trial})");
                try
                {
                    for (var i = 0; i < eventsPerTrial; i++)
                        bus.Publish(Evt($"e-{i:D2}"));
                }
                finally { publisherFinished.Set(); }
            });

            scope.StartWorker($"reader (trial {trial})", () =>
            {
                SignalAndWaitBounded(scope, bothReady, $"reader (trial {trial})");
                while (!publisherFinished.IsSet)
                {
                    var batch = subscriber.DrainPendingEvents();
                    if (batch.Count == 0)
                        continue;

                    // Rejected send: put them back, then take them for good.
                    subscriber.RestoreEvents(batch);
                    foreach (var e in subscriber.DrainPendingEvents())
                        collected.Enqueue(e);
                }
                foreach (var e in subscriber.DrainPendingEvents())
                    collected.Enqueue(e);
            });

            scope.CompleteAndVerify();

            var observed = new List<SystemEvent>(collected);
            observed.AddRange(subscriber.DrainPendingEvents());

            // Conservation with exact multiplicity: restore must not duplicate or drop.
            var observedIds = Ids(observed).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var expectedIds = Enumerable.Range(0, eventsPerTrial)
                .Select(i => $"e-{i:D2}")
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expectedIds, observedIds);
        }
    }
}

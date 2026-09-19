using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;

using Grpc.Core;
using Grpc.Net.Client;

using System.Diagnostics;
using System.Reflection;
using System.Threading.Channels;

using System.Threading;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Builds and PUBLISHES the <see cref="WorkerConnection"/> a direct-loop fixture drives the real
/// <c>WorkerService.ProcessMessagesAsync</c> with, and returns it so the fixture can pass it to the
/// loop.
/// </summary>
/// <remarks>
/// <para>
/// The fixtures that drive the message loop directly have no ACCEPTED registration behind them, so
/// they build their own connection instead of running <c>RunAsync</c>. Its gRPC client is
/// materialised from a process-wide channel that is never used for an RPC — those fixtures exercise
/// the DUPLEX STREAM only — and is deliberately not disposed, since it owns no connection.
/// </para>
/// <para>
/// A <c>null</c> provisioner leaves the connection WITHOUT one, which is what retains the LEGACY,
/// seam-free executor branch for these fixtures; supplying one exercises the seam path.
/// </para>
/// <para>
/// The NEGOTIATED completion-receipt ACK answer defaults to DISABLED, exactly as a connection built
/// without one does in production, so every existing fixture keeps its previous behavior. A fixture
/// that needs the enabled connection passes it explicitly — it is a captured registration fact, never
/// something inferred from capabilities, version or model.
/// </para>
/// </remarks>
internal static class TestConnectionFactory
{
    private static readonly GrpcChannel Channel = GrpcChannel.ForAddress("http://localhost:9999");

    /// <summary>Publishes a fixture connection on <paramref name="service"/> and returns it.</summary>
    internal static WorkerConnection Attach(
        WorkerService service,
        string assignedId,
        AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        WorkerConfigProvisioner? provisioner = null,
        bool completionReceiptAckEnabled = false,
        bool completionReadyRequired = false)
    {
        var connection = new WorkerConnection(
            assignedId,
            new HiveOrchestrator.HiveOrchestratorClient(Channel),
            stream,
            provisioner,
            includeProductionProvisioner: false,
            provisioningEnvironment: null,
            completionReceiptAckEnabled: completionReceiptAckEnabled,
            completionReadyRequired: completionReadyRequired);

        service.PublishConnection(connection);
        return connection;
    }

    /// <summary>
    /// Builds — but does NOT publish — a second connection carrying the given negotiated answer. It
    /// is used only to prove that a delivery belonging to a DIFFERENT connection object can never
    /// confirm the retained assignment, even when every wire identity matches.
    /// </summary>
    internal static WorkerConnection CreateUnpublished(
        string assignedId,
        AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        bool completionReceiptAckEnabled = false,
        bool completionReadyRequired = false) =>
        new(
            assignedId,
            new HiveOrchestrator.HiveOrchestratorClient(Channel),
            stream,
            provisionerOverride: null,
            includeProductionProvisioner: false,
            provisioningEnvironment: null,
            completionReceiptAckEnabled: completionReceiptAckEnabled,
            completionReadyRequired: completionReadyRequired);
}

/// <summary>
/// THE SEND-BOUNDARY ARRIVAL OBSERVER. Reads the production <c>SemaphoreSlim</c> send gate's
/// ASYNC WAITER QUEUE so a test can prove that a competing producer has actually reached the
/// send boundary and is PARKED there — not merely that it was scheduled.
/// <para>
/// This exists because the two interleaving tests must keep the first underlying write parked
/// until the contender is provably waiting. Without that proof they also pass when the gate is
/// bypassed (the contender simply writes immediately, and releasing the first write early hides
/// the overlap), so they would not detect the very defect they guard. With it, a bypassed gate
/// enrolls NO waiter, the arrival wait fails on its bound, and the test fails by name.
/// </para>
/// <para>
/// <see cref="SemaphoreSlim"/> raises no event when a waiter enrolls, and the production code is
/// frozen (no instrumentation may be added to it), so arrival is observed by sampling the queue.
/// The sampling bound is a FAILURE GUARD only — the wait returns as soon as the waiter appears,
/// and it is never used to order anything. If the runtime's internals ever change shape, the
/// probe throws loudly rather than silently reporting "no waiters", so a test can never pass
/// because the observation stopped working.
/// </para>
/// </summary>
internal static class SendGateObserver
{
    private const BindingFlags AnyInstance =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>Bound for <see cref="WaitForWaitersAsync"/>; generous enough never to fire on a healthy schedule.</summary>
    private static readonly TimeSpan ArrivalFailsafe = TimeSpan.FromSeconds(10);

    /// <summary>Sampling interval while waiting for an arrival (observation only, never ordering).</summary>
    private static readonly TimeSpan SamplingInterval = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Counts the callers currently enrolled in <paramref name="gate"/>'s async waiter queue,
    /// i.e. the sends that have reached the boundary and are parked awaiting a permit.
    /// </summary>
    /// <remarks>
    /// The walk holds the SAME monitor <see cref="SemaphoreSlim"/> itself takes when it mutates
    /// the queue, so the list is stable for the duration of the walk and the count can never be
    /// read mid-splice. A missing field means the observation is broken on this runtime and
    /// throws loudly — a test must never pass merely because arrival stopped being observable.
    /// </remarks>
    internal static int CountWaiters(SemaphoreSlim gate)
    {
        var headField = typeof(SemaphoreSlim).GetField("m_asyncHead", AnyInstance)
            ?? throw new InvalidOperationException(
                "SemaphoreSlim.m_asyncHead was not found: the send gate's waiter queue cannot be "
                + "observed on this runtime, so send-boundary arrival cannot be proven.");
        var lockField = typeof(SemaphoreSlim).GetField("m_lockObjAndDisposed", AnyInstance)
            ?? throw new InvalidOperationException(
                "SemaphoreSlim.m_lockObjAndDisposed was not found: the waiter queue cannot be "
                + "walked without racing the runtime's own mutations.");
        var monitor = lockField.GetValue(gate)
            ?? throw new InvalidOperationException("The send gate's lock object was null.");

        lock (monitor)
        {
            var node = headField.GetValue(gate);
            var count = 0;
            while (node is not null)
            {
                count++;
                var nextField = node.GetType().GetField("Next", AnyInstance)
                    ?? throw new InvalidOperationException(
                        "SemaphoreSlim waiter node has no 'Next' field: the waiter queue cannot be walked.");
                node = nextField.GetValue(node);
            }

            return count;
        }
    }

    /// <summary>
    /// Completes once at least <paramref name="count"/> senders are PARKED on
    /// <paramref name="gate"/>. Throws <see cref="TimeoutException"/> on the failsafe bound — the
    /// signature of a bypassed gate, where a contender writes instead of waiting.
    /// </summary>
    internal static async Task WaitForWaitersAsync(SemaphoreSlim gate, int count, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (CountWaiters(gate) < count)
        {
            if (stopwatch.Elapsed > ArrivalFailsafe)
            {
                throw new TimeoutException(
                    $"Timed out waiting for {count} sender(s) to park on the send gate (observed "
                    + $"{CountWaiters(gate)}). A contender that never parks means the write was not "
                    + "routed through the serialization boundary.");
            }

            await Task.Delay(SamplingInterval, ct);
        }
    }
}

/// <summary>
/// Base class for <see cref="IClientStreamWriter{T}"/> test doubles. Implements the
/// two-argument <c>WriteAsync(T, CancellationToken)</c> overload explicitly: the default
/// interface method on <see cref="IAsyncStreamWriter{T}"/> throws
/// <see cref="NotSupportedException"/> for any cancellable token, which would land on an
/// unobserved TCS and hang the test host.
/// </summary>
internal abstract class FakeClientStreamWriter<T> : IClientStreamWriter<T>
{
    public WriteOptions? WriteOptions { get; set; }

    public abstract Task WriteAsync(T message);

    Task IAsyncStreamWriter<T>.WriteAsync(T message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return WriteWithTokenAsync(message, cancellationToken);
    }

    /// <summary>
    /// Token-aware write hook. The default honours the token only at entry; overrides may
    /// additionally observe it DURING the write (e.g. while parked in a gated fake).
    /// </summary>
    protected virtual Task WriteWithTokenAsync(T message, CancellationToken cancellationToken)
        => WriteAsync(message);

    public abstract Task CompleteAsync();
}

/// <summary>
/// A gated, overlap-detecting <see cref="IClientStreamWriter{WorkerMessage}"/> for the
/// send-serialization tests. Every write parks inside the fake until
/// <see cref="ReleaseCurrentWrite"/> is called, and entering a write while another one is
/// still parked is recorded in <see cref="OverlapDetected"/>/<see cref="RejectedWriteCount"/>
/// and throws — exactly the invariant the production send boundary promises (at most ONE
/// outstanding <c>RequestStream.WriteAsync</c> per WorkerService connection).
/// <para>
/// All observables are TCS/int based: there are NO sleeps and no polling. Tests await
/// <see cref="WaitForWriteEnteredAsync"/> for producer-start evidence before asserting.
/// The two-argument <c>WriteAsync(T, CancellationToken)</c> overload is implemented
/// EXPLICITLY (never inherited): it pre-checks the token and honours it DURING the parked
/// write, so a cancelled in-flight write unwinds with <see cref="OperationCanceledException"/>
/// exactly like a real gRPC stream writer.
/// </para>
/// </summary>
internal sealed class GatedOverlapDetectingRequestStream : FakeClientStreamWriter<WorkerMessage>
{
    private readonly object _gate = new();
    private readonly List<WorkerMessage> _enteredWrites = [];
    private readonly List<WorkerMessage> _completedWrites = [];
    private readonly List<TaskCompletionSource<bool>> _releaseWaiters = [];
    private readonly List<(int Index, TaskCompletionSource<bool> Waiter)> _enteredWaiters = [];
    private Exception? _failNextWrite;
    private bool _writeInProgress;
    private bool _teardownMode;
    private int _rejectedWrites;

    /// <summary>Writes that ENTERED the fake (started; possibly still parked or since faulted).</summary>
    internal int EnteredWriteCount { get { lock (_gate) return _enteredWrites.Count; } }

    /// <summary>Writes that COMPLETED successfully (were released and returned normally).</summary>
    internal int CompletedWriteCount { get { lock (_gate) return _completedWrites.Count; } }

    /// <summary>Writes currently parked inside the fake awaiting <see cref="ReleaseCurrentWrite"/>.</summary>
    internal int ParkedWriteCount { get { lock (_gate) return _releaseWaiters.Count; } }

    /// <summary>True once any second write entered while another was still parked.</summary>
    internal bool OverlapDetected { get { lock (_gate) return _rejectedWrites > 0; } }

    /// <summary>Number of writes rejected because they overlapped a parked write.</summary>
    internal int RejectedWriteCount { get { lock (_gate) return _rejectedWrites; } }

    /// <summary>Snapshots the messages that ENTERED writes, oldest first.</summary>
    internal IReadOnlyList<WorkerMessage> EnteredWrites
    {
        get { lock (_gate) return _enteredWrites.ToList(); }
    }

    /// <summary>Snapshots the messages whose writes COMPLETED, oldest first.</summary>
    internal IReadOnlyList<WorkerMessage> CompletedWrites
    {
        get { lock (_gate) return _completedWrites.ToList(); }
    }

    /// <summary>
    /// Arms a ONE-SHOT failure: the next write records its entry (producer-start evidence is
    /// still observable) and then throws this exception instead of parking, modelling an
    /// underlying gRPC write failure. Consumed on use.
    /// </summary>
    internal Exception FailNextWrite
    {
        set { lock (_gate) _failNextWrite = value; }
    }

    /// <summary>
    /// The single gated write. Records the message, fails loudly on overlap, signals
    /// <see cref="WaitForWriteEnteredAsync"/>, honours the one-shot failure, then parks until
    /// <see cref="ReleaseCurrentWrite"/>.
    /// </summary>
    public override Task WriteAsync(WorkerMessage message) => WriteCoreAsync(message, CancellationToken.None);

    protected override Task WriteWithTokenAsync(WorkerMessage message, CancellationToken cancellationToken)
        => WriteCoreAsync(message, cancellationToken);

    private async Task WriteCoreAsync(WorkerMessage message, CancellationToken ct)
    {
        TaskCompletionSource<bool> release;
        Exception? failure;
        lock (_gate)
        {
            if (_writeInProgress)
            {
                _rejectedWrites++;
                throw new InvalidOperationException(
                    "OVERLAP: a second RequestStream.WriteAsync started while another write was still pending.");
            }

            _writeInProgress = true;
            _enteredWrites.Add(message);

            // Create and enqueue the release TCS BEFORE signalling entry, so a test that
            // observed entry can always release this write deterministically.
            release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _releaseWaiters.Add(release);

            // TEARDOWN MODE — a write that arrives AFTER the teardown sweep (e.g. a producer that
            // was still queued on the production send gate and only acquired it once the sweep
            // released the write ahead of it) must NOT park, or the drain would block until its
            // hang guard expires and leave the producer alive. Pre-completing its release here
            // makes late arrivals unblock immediately, which is what lets the teardown loop
            // converge. Entry/overlap bookkeeping is unchanged, so teardown can never manufacture
            // a passing observation.
            if (_teardownMode)
                release.TrySetResult(true);

            failure = _failNextWrite;
            _failNextWrite = null;

            SignalEnteredWaiters_Locked();
        }

        try
        {
            if (failure is not null)
                throw failure;

            // Park here. While parked, _writeInProgress stays true, so any concurrent producer
            // entering the fake records OverlapDetected instead of succeeding. A cancellation
            // of the caller's token while parked models a cancelled IN-FLIGHT write.
            await release.Task.WaitAsync(ct);

            lock (_gate)
            {
                _writeInProgress = false;
                _completedWrites.Add(message);

                // Normally ReleaseCurrentWrite/ReleaseAllParkedWrites already removed this
                // waiter; a teardown-mode write completes without ever being swept, so drop it
                // here too. Idempotent, and keeps ParkedWriteCount honest in every mode.
                _releaseWaiters.Remove(release);
            }
        }
        catch
        {
            // The write is abandoning (injected failure or cancellation): free the slot and
            // remove the now-stale release waiter so later sends remain individually releasable.
            lock (_gate)
            {
                _writeInProgress = false;
                _releaseWaiters.Remove(release);
            }

            throw;
        }
    }

    /// <summary>
    /// Releases the OLDEST currently parked write. Deterministic tests call this only after
    /// <see cref="WaitForWriteEnteredAsync"/> gave producer-start evidence, so the waiter is
    /// guaranteed present; releasing with nothing parked is a testing error.
    /// </summary>
    internal void ReleaseCurrentWrite()
    {
        TaskCompletionSource<bool>? waiter;
        lock (_gate)
        {
            if (_releaseWaiters.Count == 0)
                throw new InvalidOperationException(
                    "No parked write to release — release only after WaitForWriteEnteredAsync evidence.");
            var index = 0;
            waiter = _releaseWaiters[index];
            _releaseWaiters.RemoveAt(index);
        }

        waiter.TrySetResult(true);
    }

    /// <summary>
    /// Deterministic failsafe bound for <see cref="WaitForWriteEnteredAsync"/>. It converts a
    /// producer that never arrives (e.g. under a serialization bypass, where the overlapping
    /// write is REJECTED instead of entering) into a fast, diagnosable failure instead of a
    /// hung test run. Generous enough to never fire on a healthy schedule.
    /// </summary>
    private static readonly TimeSpan EntryFailsafe = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Completes once the <paramref name="index"/>-th write (0-based) has ENTERED the fake —
    /// the producer-start evidence every gating assertion is required to await first.
    /// Throws <see cref="TimeoutException"/> on the failsafe bound if that many writes never
    /// arrive, so a regression manifests as a named test failure, not a hang.
    /// </summary>
    internal Task WaitForWriteEnteredAsync(int index, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_enteredWrites.Count > index)
                return Task.CompletedTask;

            var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _enteredWaiters.Add((index, waiter));
            return waiter.Task.WaitAsync(EntryFailsafe, ct);
        }
    }

    public override Task CompleteAsync()
    {
        lock (_gate) _writeInProgress = false;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Test-teardown drain: releases every currently parked write so awaited sends unwind in
    /// guaranteed <c>finally</c> blocks instead of hanging the test host.
    /// </summary>
    /// <remarks>
    /// This is a SNAPSHOT of what is parked right now. On its own it does not cover a producer
    /// that is still queued on the production send gate and parks here only after the sweep —
    /// see <see cref="EnterTeardownMode"/>, which closes that window.
    /// </remarks>
    internal void ReleaseAllParkedWrites()
    {
        lock (_gate)
        {
            foreach (var waiter in _releaseWaiters)
                waiter.TrySetResult(true);
            _releaseWaiters.Clear();
        }
    }

    /// <summary>
    /// Switches the fake into TEARDOWN MODE and releases everything currently parked. From this
    /// point on, writes that ENTER are released immediately instead of parking, so a producer
    /// that acquires the production send gate after the sweep still unwinds promptly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gap this closes: <see cref="ReleaseAllParkedWrites"/> can only free writes already
    /// inside the fake. If a test fails while writer A is parked here and writer B is queued on
    /// the production <c>SemaphoreSlim</c>, the sweep frees A, A releases the permit, and B then
    /// acquires it and parks here — AFTER the sweep. The drain would then block on B for its
    /// full hang guard and still leave B's task alive when the <c>finally</c> completes.
    /// </para>
    /// <para>
    /// This is a ONE-WAY, teardown-only switch. It is never enabled while a test is asserting, so
    /// it cannot weaken any gating observation: entry recording, overlap detection and the
    /// completed-write list all behave exactly as before, and a write released this way still
    /// counts as a normal completed write.
    /// </para>
    /// </remarks>
    internal void EnterTeardownMode()
    {
        lock (_gate)
            _teardownMode = true;

        ReleaseAllParkedWrites();
    }

    private void SignalEnteredWaiters_Locked()
    {
        for (var i = _enteredWaiters.Count - 1; i >= 0; i--)
        {
            var (index, waiter) = _enteredWaiters[i];
            if (_enteredWrites.Count > index)
            {
                waiter.TrySetResult(true);
                _enteredWaiters.RemoveAt(i);
            }
        }
    }
}

/// <summary>
/// A channel-backed <see cref="IAsyncStreamReader{OrchestratorMessage}"/> for driving the REAL
/// <c>WorkerService.ProcessMessagesAsync</c> loop. Tests push orchestrator messages and await
/// <see cref="Consumed"/> as a deterministic barrier proving the loop has processed them; a
/// <c>null</c> push ends the stream. No sleeps, no polling.
/// </summary>
internal sealed class ChannelResponseReader : IAsyncStreamReader<OrchestratorMessage>
{
    private readonly Channel<OrchestratorMessage> _channel = Channel.CreateUnbounded<OrchestratorMessage>();
    private readonly object _gate = new();
    private readonly Dictionary<int, TaskCompletionSource> _consumedWaiters = [];
    private readonly Dictionary<int, TaskCompletionSource> _readStartedWaiters = [];
    private int _consumed;
    private int _readsStarted;

    public OrchestratorMessage Current { get; private set; } = null!;

    /// <summary>
    /// How many reads production has STARTED. It advances once per <c>MoveNext</c> entry, and
    /// production re-arms exactly one pending read per dispatched message only AFTER that
    /// message's handler returns — so a read count that has NOT advanced is positive evidence
    /// that the loop is still inside the handler it last entered.
    /// </summary>
    internal int ReadsStarted { get { lock (_gate) return _readsStarted; } }

    /// <summary>
    /// Optional ONE-SHOT hook awaited at the start of the NEXT <see cref="MoveNext"/>, after the
    /// read-start counter advances but before the channel is consulted. It lets a fixture construct
    /// a genuine reader-first tie from inside the production read call; null keeps existing users'
    /// behavior byte-for-byte unchanged.
    /// </summary>
    internal Func<Task>? BeforeNextRead { get; set; }

    /// <summary>Pushes one message; <c>null</c> completes the stream.</summary>
    internal void Push(OrchestratorMessage? message)
    {
        if (message is null)
            _channel.Writer.TryComplete();
        else
            _channel.Writer.TryWrite(message);
    }

    internal void TryComplete() => _channel.Writer.TryComplete();

    /// <summary>Completes once the loop has consumed at least <paramref name="count"/> messages.</summary>
    internal Task Consumed(int count)
    {
        lock (_gate)
        {
            if (_consumed >= count)
                return Task.CompletedTask;
            if (!_consumedWaiters.TryGetValue(count, out var waiter))
            {
                waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _consumedWaiters[count] = waiter;
            }
            return waiter.Task;
        }
    }

    /// <summary>Completes once production has STARTED at least <paramref name="count"/> reads.</summary>
    internal Task ReadStarted(int count)
    {
        lock (_gate)
        {
            if (_readsStarted >= count)
                return Task.CompletedTask;
            if (!_readStartedWaiters.TryGetValue(count, out var waiter))
            {
                waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _readStartedWaiters[count] = waiter;
            }
            return waiter.Task;
        }
    }

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        List<TaskCompletionSource> readReady;
        Func<Task>? before;
        lock (_gate)
        {
            _readsStarted++;
            before = BeforeNextRead;
            BeforeNextRead = null;
            readReady = [];
            foreach (var (threshold, waiter) in _readStartedWaiters)
            {
                if (_readsStarted >= threshold)
                    readReady.Add(waiter);
            }
        }

        foreach (var waiter in readReady)
            waiter.TrySetResult();

        if (before is not null)
            await before();

        if (!await _channel.Reader.WaitToReadAsync(cancellationToken))
            return false;

        if (!_channel.Reader.TryRead(out var message))
            return false;

        Current = message;
        List<TaskCompletionSource> ready = [];
        lock (_gate)
        {
            _consumed++;
            foreach (var (threshold, waiter) in _consumedWaiters)
            {
                if (_consumed >= threshold)
                    ready.Add(waiter);
            }
        }

        foreach (var waiter in ready)
            waiter.TrySetResult();

        return true;
    }
}

/// <summary>
/// A channel-backed reader that behaves exactly like <see cref="ChannelResponseReader"/> until
/// <see cref="ArmFault"/> is called, then throws the ORIGINAL exception from the next
/// <c>MoveNext</c> — modelling a reader fault whose identity the loop must propagate.
/// </summary>
internal sealed class FaultingResponseReader : IAsyncStreamReader<OrchestratorMessage>
{
    private readonly Channel<OrchestratorMessage> _channel =
        Channel.CreateUnbounded<OrchestratorMessage>();

    private readonly object _gate = new();
    private readonly Dictionary<int, TaskCompletionSource> _consumedWaiters = [];
    private Exception? _fault;
    private int _consumed;

    public OrchestratorMessage Current { get; private set; } = null!;

    internal void Push(OrchestratorMessage message) => _channel.Writer.TryWrite(message);

    internal void TryComplete() => _channel.Writer.TryComplete();

    /// <summary>
    /// One-shot: the next <c>MoveNext</c> outcome surfaces <paramref name="fault"/>. The channel
    /// is also completed, so a loop ALREADY parked inside <c>WaitToReadAsync</c> wakes
    /// deterministically and reaches the fault check.
    /// </summary>
    internal void ArmFault(Exception fault)
    {
        lock (_gate) _fault = fault;
        _channel.Writer.TryComplete();
    }

    /// <summary>Completes once the loop has consumed at least <paramref name="count"/> messages.</summary>
    internal Task Consumed(int count)
    {
        lock (_gate)
        {
            if (_consumed >= count)
                return Task.CompletedTask;
            if (!_consumedWaiters.TryGetValue(count, out var waiter))
            {
                waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _consumedWaiters[count] = waiter;
            }
            return waiter.Task;
        }
    }

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        Exception? fault;
        lock (_gate)
        {
            fault = _fault;
            _fault = null;
        }

        if (fault is not null)
            throw fault;

        if (!await _channel.Reader.WaitToReadAsync(cancellationToken))
        {
            // A loop ALREADY parked inside WaitToReadAsync when ArmFault completed the channel
            // wakes here, so the fault must be surfaced from THIS outcome — not silently turned
            // into an EOF.
            lock (_gate)
            {
                fault = _fault;
                _fault = null;
            }

            if (fault is not null)
                throw fault;

            return false;
        }

        if (!_channel.Reader.TryRead(out var message))
            return false;

        Current = message;
        List<TaskCompletionSource> ready = [];
        lock (_gate)
        {
            _consumed++;
            foreach (var (threshold, waiter) in _consumedWaiters)
            {
                if (_consumed >= threshold)
                    ready.Add(waiter);
            }
        }

        foreach (var waiter in ready)
            waiter.TrySetResult();

        return true;
    }
}
/// <summary>
/// A MANUAL <see cref="TimeProvider"/> for the completion-retransmission fixtures: the retry
/// delay's ONLY clock, advanced exclusively through <see cref="Advance"/>. It is handed to the
/// service's internal <c>TimeProvider</c> seam, which the assignment handler reads ONCE per
/// assignment, so the five-second sequencing is proven against the REAL clock seam production
/// reads for the retry wait — never against a real timer, never against a sleep.
/// <para>
/// WHAT IT OBSERVES. Every created delay timer (the production delay calls <c>CreateTimer</c>
/// exactly once per retry attempt) is recorded, and <see cref="TimerCount"/> is a positive,
/// monotone witness that a retry actually ENTERED its wait through this clock. A fixture that
/// wants to prove "no retransmission was ever attempted" asserts <see cref="TimerCount"/> stays
/// at zero while a bounded window elapses.
/// </para>
/// <para>
/// NO POLLING, NO STRAY POLLERS. <see cref="WaitForTimerCountAsync"/> is a CREATION-SIGNALLED
/// rendezvous: a waiter registers a <see cref="TaskCompletionSource"/> under the same lock
/// <see cref="CreateTimer"/> takes, and creation completes it. A caller that wraps the returned
/// task in a SHORTER bounded wait (the suppression windows) therefore leaves nothing running —
/// the abandoned waiter is an inert TCS, not a live <c>Task.Delay</c> loop that keeps ticking
/// after the bounded wait expired.
/// </para>
/// <para>
/// WHAT IT NEVER DOES. It never runs anything on a real thread-pool timer: the timers it returns
/// are fully owned by the clock, so a delay parks until the test advances this clock or the
/// retry's own lifetime cancels the wait (via <see cref="ITimer.Change"/> /
/// <see cref="IDisposable.Dispose"/>, which production's Task.Delay uses on cancellation).
/// That is what makes the "at most one attempt at a time" and "a fresh interval after every prior
/// retry terminates" assertions deterministic — the test, not the wall clock, orders the attempts.
/// </para>
/// <para>
/// VIRTUAL ABSOLUTE TIME, SO A PARTIAL ADVANCE IS EXACT. Each timer records the virtual instant it
/// becomes due (<c>now + dueTime</c>), and <see cref="Advance"/> moves <c>now</c> forward and fires
/// only timers whose deadline is at or before the new instant. Advancing 4.999s and then 0.001s is
/// therefore indistinguishable from advancing 5s in one step, which is exactly what lets a fixture
/// assert "no attempt JUST BEFORE the boundary, exactly one attempt AT the boundary" against an
/// independently stated interval rather than production's own constant.
/// </para>
/// <para>
/// GENERATION TAGGING IS ATOMIC. The advance generation, and whether an advance is currently
/// firing, are read and written ONLY under <c>_gate</c> — including by <see cref="CreateTimer"/> —
/// so a timer created from a continuation that runs while an advance is in progress is
/// deterministically stamped with the CURRENT generation and can never be fired by that same
/// advance. It becomes eligible for the NEXT advance, which is precisely the production
/// "fresh full interval after the prior attempt terminated" shape.
/// </para>
/// </summary>
internal sealed class ManualRetransmissionClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly List<(int Count, TaskCompletionSource Waiter)> _timerCountWaiters = [];
    private int _timerCount;
    private int _advanceCount;

    /// <summary>
    /// The VIRTUAL now. Only <see cref="Advance"/> moves it, and only under <c>_gate</c>, so every
    /// deadline comparison is against a single consistent instant.
    /// </summary>
    private TimeSpan _now = TimeSpan.Zero;

    /// <summary>
    /// The CURRENT advance's generation, incremented once per <see cref="Advance"/> while the lock
    /// is held. A timer created while an advance is running records that generation; the advance
    /// fires only timers whose creation generation differs from its own, so such a timer always
    /// waits for the next advance.
    /// </summary>
    private int _advanceGeneration;

    /// <summary>
    /// Whether an <see cref="Advance"/> is currently firing callbacks. Written and read ONLY under
    /// <c>_gate</c> (never a lock-free field), so the generation a concurrently created timer is
    /// stamped with can never be classified inconsistently with the advance that is running.
    /// </summary>
    private bool _advanceInProgress;

    /// <summary>How many delay timers this clock has CREATED (one per production retry wait).</summary>
    internal int TimerCount { get { lock (_gate) return _timerCount; } }

    /// <summary>How many times the test has advanced the clock.</summary>
    internal int AdvanceCount { get { lock (_gate) return _advanceCount; } }

    /// <summary>The VIRTUAL instant this clock currently reports; diagnostics only.</summary>
    internal TimeSpan VirtualNow { get { lock (_gate) return _now; } }

    /// <summary>
    /// Completes once this clock has created at least <paramref name="count"/> delay timers —
    /// positive evidence that a retry reached its wait THROUGH the production clock seam.
    /// </summary>
    /// <remarks>
    /// CREATION-SIGNALLED, NEVER POLLED. The waiter is registered under the same lock
    /// <see cref="CreateTimer"/> takes and is completed by the creation itself, so this leaves no
    /// background loop behind when a caller abandons it on a shorter bounded wait.
    /// </remarks>
    internal Task WaitForTimerCountAsync(int count, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_timerCount >= count)
                return Task.CompletedTask;

            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _timerCountWaiters.Add((count, waiter));
            return waiter.Task.WaitAsync(RendezvousFailsafe, ct);
        }
    }

    /// <summary>
    /// Completes once a retry has PARKED IN ITS DELAY — i.e. at least <paramref name="count"/>
    /// delay timers have been created through this clock. The same creation rendezvous, named for
    /// the fact it witnesses, and equally free of stray pollers when a bounded wait abandons it.
    /// </summary>
    internal Task WaitForRetryParkedInDelayAsync(int count, CancellationToken ct) =>
        WaitForTimerCountAsync(count, ct);

    /// <summary>
    /// The bound that turns a never-arriving rendezvous into a NAMED failure rather than a hung
    /// host. It orders nothing: the wait completes the instant the timer is created.
    /// </summary>
    private static readonly TimeSpan RendezvousFailsafe = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Advances the VIRTUAL clock by <paramref name="delta"/> and fires every timer whose deadline
    /// is at or before the new instant, earliest deadline first, repeatedly, until none is due.
    /// </summary>
    /// <remarks>
    /// A timer created from INSIDE this advance (the retry's fresh interval, armed by the
    /// continuation the fired callback released) carries the CURRENT generation and is therefore
    /// skipped here — it waits for the next advance, which is the production shape "a fresh FULL
    /// interval after the prior attempt terminated".
    /// </remarks>
    internal void Advance(TimeSpan delta)
    {
        lock (_gate)
        {
            _advanceCount++;
            _advanceGeneration++;
            _advanceInProgress = true;
            _now += delta;
        }

        try
        {
            var guard = 0;
            while (true)
            {
                if (++guard > 1000)
                {
                    throw new InvalidOperationException(
                        "ManualRetransmissionClock.Advance fired a timer more than 1000 times: a " +
                        "re-armed timer is re-firing without its wait being cancelled.");
                }

                ManualTimer? due = null;
                lock (_gate)
                {
                    foreach (var timer in _timers)
                    {
                        if (timer.Disposed || timer.DueAt is not TimeSpan deadline)
                            continue;

                        // A timer created during THIS advance belongs to the NEXT one.
                        if (timer.CreatedInGeneration == _advanceGeneration)
                            continue;

                        if (deadline > _now)
                            continue;

                        // Earliest deadline first, so a re-armed chain fires in real order.
                        if (due is null || deadline < due.DueAt)
                            due = timer;
                    }

                    // Park the winner while it fires, so it can never be selected twice.
                    if (due is not null)
                        due.DueAt = null;
                }

                if (due is null)
                    return;

                due.Fire();
            }
        }
        finally
        {
            lock (_gate)
                _advanceInProgress = false;
        }
    }

    /// <summary>Creates and records ONE timer for the production delay.</summary>
    public override ITimer CreateTimer(
        TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer;
        List<TaskCompletionSource> satisfied = [];
        lock (_gate)
        {
            // THE DEADLINE AND THE GENERATION ARE STAMPED TOGETHER, under the one lock an advance
            // also holds while it mutates them — so a timer created from a continuation running
            // during an in-progress advance is deterministically assigned to the NEXT advance.
            timer = new ManualTimer(
                this,
                callback,
                state,
                dueTime,
                _now,
                _advanceInProgress ? _advanceGeneration : 0);

            _timerCount++;
            _timers.Add(timer);

            for (var i = _timerCountWaiters.Count - 1; i >= 0; i--)
            {
                var (count, waiter) = _timerCountWaiters[i];
                if (_timerCount >= count)
                {
                    satisfied.Add(waiter);
                    _timerCountWaiters.RemoveAt(i);
                }
            }
        }

        // Completed OUTSIDE the lock: a continuation must never run while the clock is held.
        foreach (var waiter in satisfied)
            waiter.TrySetResult();

        return timer;
    }

    /// <summary>
    /// THE ONE fully test-owned timer. Nothing here touches the thread pool: <see cref="Fire"/>
    /// runs the production callback synchronously on the ADVANCING thread, and production's
    /// cancellation path (<c>Change</c> with an infinite due time and <see cref="Dispose"/>) simply
    /// parks or drops the timer, which is exactly what stops a pending retry delay.
    /// </summary>
    private sealed class ManualTimer : ITimer
    {
        private readonly ManualRetransmissionClock _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        internal ManualTimer(
            ManualRetransmissionClock clock,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan createdAt,
            int createdInGeneration)
        {
            _clock = clock;
            _callback = callback;
            _state = state;
            CreatedInGeneration = createdInGeneration;
            DueAt = IsInfinite(dueTime) ? null : createdAt + dueTime;
        }

        /// <summary>
        /// The VIRTUAL instant this timer becomes due, or <c>null</c> while parked / never armed.
        /// Always mutated under the clock's lock.
        /// </summary>
        internal TimeSpan? DueAt { get; set; }

        internal bool Disposed { get; private set; }

        /// <summary>
        /// The advance generation this timer was created in, or <c>0</c> when it was created while
        /// NO advance was running. An advance fires only timers whose generation differs from its
        /// own, so a timer created from inside an advance always waits for the next one.
        /// </summary>
        internal int CreatedInGeneration { get; }

        /// <summary>Runs the production callback synchronously — the ONLY fire path.</summary>
        internal void Fire()
        {
            if (Disposed)
                return;

            _callback(_state);

            // NO RE-ARM FROM THE CALLBACK. The production delay passes an INFINITE period, so
            // the timer stays parked after firing; the retry's "fresh interval" loop re-arms
            // itself by creating a NEW timer, which is how the next delay's timer appears through
            // the SAME clock instance. A periodic timer (period >= 0) would re-arm here — no
            // production call site uses one, so this branch is deliberately absent.
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_clock._gate)
            {
                if (Disposed)
                    return false;

                DueAt = IsInfinite(dueTime) ? null : _clock._now + dueTime;
                return true;
            }
        }

        public void Dispose()
        {
            lock (_clock._gate)
            {
                Disposed = true;
                DueAt = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private static bool IsInfinite(TimeSpan dueTime) =>
            dueTime == Timeout.InfiniteTimeSpan || dueTime < TimeSpan.Zero;
    }
}

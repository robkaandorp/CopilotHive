using CopilotHive.Shared.Grpc;
using CopilotHive.Worker;

using Grpc.Core;
using Grpc.Net.Client;

using System.Diagnostics;
using System.Reflection;
using System.Threading.Channels;

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
/// </remarks>
internal static class TestConnectionFactory
{
    private static readonly GrpcChannel Channel = GrpcChannel.ForAddress("http://localhost:9999");

    /// <summary>Publishes a fixture connection on <paramref name="service"/> and returns it.</summary>
    internal static WorkerConnection Attach(
        WorkerService service,
        string assignedId,
        AsyncDuplexStreamingCall<WorkerMessage, OrchestratorMessage> stream,
        WorkerConfigProvisioner? provisioner = null)
    {
        var connection = new WorkerConnection(
            assignedId,
            new HiveOrchestrator.HiveOrchestratorClient(Channel),
            stream,
            provisioner,
            includeProductionProvisioner: false);

        service.PublishConnection(connection);
        return connection;
    }
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
    private int _consumed;

    public OrchestratorMessage Current { get; private set; } = null!;

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

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
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
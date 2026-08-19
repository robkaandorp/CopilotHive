using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;

using CopilotHive.Configuration;
using CopilotHive.Orchestration;
using CopilotHive.Services;

using Microsoft.Extensions.Logging;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Tests for <see cref="ActiveEventInjector"/>. Covers mode gating, whitelist filtering,
/// throttle/batching semantics, bounded draining, test seams (<c>startGate</c>,
/// <c>sendFunc</c>, <c>disposalTimeout</c>, <c>throttleOverride</c>, <c>timeProvider</c>),
/// disposal, and notification formatting.
/// <para>
/// <b>Determinism contract.</b> Every throttle assertion drives a
/// <see cref="ControlledTimeProvider"/> whose clock advances ONLY when the test advances it,
/// and rendezvouses on observed state (a registered timer, a recorded send) rather than on
/// wall-clock sleeps or polling. This buys two things a real-time test cannot:
/// </para>
/// <list type="number">
/// <item>
/// <b>Removal proof for the <c>TimeProvider</c> overload.</b> Production must call
/// <c>Task.Delay(delay, _timeProvider, ct)</c>. If it is replaced with an ordinary
/// <c>Task.Delay</c>, the controlled provider never receives a <c>CreateTimer</c> call, so
/// every <see cref="ControlledTimeProvider.WaitForTimerCountAsync"/> rendezvous times out and
/// the test fails.
/// </item>
/// <item>
/// <b>Removal proof for each individual throttle window.</b> Because the loop can only leave
/// a window when the test advances the clock, "did NOT send yet" is a fact about a parked
/// loop, not a guess from a sleep. Deleting a window makes the awaited send arrive early —
/// or makes the timer rendezvous time out — and the test fails either way.
/// </item>
/// </list>
/// <para>
/// Tests that assert an event was NOT delivered never sleep either: they publish through a
/// second, deliberately enabled <i>control</i> injector on the same bus. <see cref="EventBus.Publish"/>
/// invokes every subscriber synchronously, so once the control injector's send is observed,
/// the injector under test has provably already had its chance to receive that same publish.
/// Absence then proves filtering rather than merely proving the test did not wait long enough.
/// </para>
/// </summary>
public sealed class ActiveEventInjectorTests
{
    /// <summary>Upper bound for any rendezvous. Exceeding it fails the test.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Mirrors the production <c>ActiveEventInjector.ChannelCapacity</c>.</summary>
    private const int ChannelCapacity = 50;

    // ────────────────────────────────────────────────────────────────────────
    //  Controlled time
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A <see cref="TimeProvider"/> whose clock advances only when the test calls
    /// <see cref="Advance"/>. Records every timer the production code registers, so tests can
    /// rendezvous on "the loop has parked on its Nth throttle window" and can assert the exact
    /// requested window length (used to prove the configured throttle was snapshotted).
    /// </summary>
    private sealed class ControlledTimeProvider : TimeProvider
    {
        private readonly object _lock = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly List<(int Target, TaskCompletionSource Tcs)> _waiters = [];
        private readonly List<TimeSpan> _requestedDelays = [];
        private DateTimeOffset _now = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private int _timersCreated;

        /// <summary>Number of timers production has registered through this provider.</summary>
        public int TimersCreated { get { lock (_lock) return _timersCreated; } }

        /// <summary>The due-times production requested, in registration order.</summary>
        public TimeSpan[] RequestedDelays { get { lock (_lock) return [.. _requestedDelays]; } }

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() { lock (_lock) return _now; }

        /// <inheritdoc />
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, this);
            List<TaskCompletionSource> ready = [];

            lock (_lock)
            {
                timer.DueAt = _now + dueTime;
                _timers.Add(timer);
                _requestedDelays.Add(dueTime);
                _timersCreated++;

                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_waiters[i].Target <= _timersCreated)
                    {
                        ready.Add(_waiters[i].Tcs);
                        _waiters.RemoveAt(i);
                    }
                }
            }

            foreach (var tcs in ready) tcs.TrySetResult();
            return timer;
        }

        /// <summary>
        /// Completes once production has registered at least <paramref name="expected"/> timers.
        /// A timeout here means production never awaited a <see cref="TimeProvider"/>-aware
        /// delay — i.e. the throttle window (or the required overload) was removed.
        /// </summary>
        public async Task WaitForTimerCountAsync(int expected, CancellationToken ct)
        {
            Task wait;
            lock (_lock)
            {
                if (_timersCreated >= expected) return;
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((expected, tcs));
                wait = tcs.Task;
            }

            await wait.WaitAsync(Timeout, ct);
        }

        /// <summary>Advances the clock and fires every timer that has come due.</summary>
        public void Advance(TimeSpan delta)
        {
            List<ManualTimer> due;
            lock (_lock)
            {
                _now += delta;
                due = _timers.Where(t => !t.Fired && t.DueAt <= _now).ToList();
                foreach (var t in due) t.Fired = true;
            }

            foreach (var t in due) t.Fire();
        }

        internal void UpdateDueAt(ManualTimer timer, TimeSpan dueTime)
        {
            lock (_lock) { timer.DueAt = _now + dueTime; timer.Fired = false; }
        }

        internal void Remove(ManualTimer timer)
        {
            lock (_lock) _timers.Remove(timer);
        }

        internal sealed class ManualTimer(TimerCallback callback, object? state, ControlledTimeProvider owner) : ITimer
        {
            public DateTimeOffset DueAt;
            public bool Fired;

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                // Fully qualified: inside a TimeProvider subclass the name `System` binds to
                // the inherited TimeProvider.System property, not the root namespace.
                if (dueTime == global::System.Threading.Timeout.InfiniteTimeSpan) return true;
                owner.UpdateDueAt(this, dueTime);
                return true;
            }

            public void Dispose() => owner.Remove(this);

            public ValueTask DisposeAsync() { owner.Remove(this); return ValueTask.CompletedTask; }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Recording helpers
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Captures every log entry so tests can assert on (and scan) log output.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _entries.Enqueue((logLevel, formatter(state, exception), exception));

        public (LogLevel Level, string Message, Exception? Exception)[] Snapshot() => [.. _entries];

        public bool HasWarning(string fragment) =>
            Snapshot().Any(e => e.Level == LogLevel.Warning && e.Message.Contains(fragment, StringComparison.Ordinal));
    }

    /// <summary>
    /// Records every <c>sendFunc</c> invocation and lets tests rendezvous on an exact send
    /// count. The configured <see cref="Behavior"/> runs AFTER the call is recorded, so a
    /// throwing or rejecting send is still observed.
    /// </summary>
    private sealed class SendRecorder
    {
        private readonly object _lock = new();
        private readonly List<(string DisplayText, string Wrapped)> _calls = [];
        private readonly List<(int Target, TaskCompletionSource Tcs)> _waiters = [];

        /// <summary>Optional behaviour: return false to reject, or throw to fault the send.</summary>
        public Func<string, string, bool>? Behavior { get; init; }

        public int Count { get { lock (_lock) return _calls.Count; } }

        public (string DisplayText, string Wrapped)[] Snapshot() { lock (_lock) return [.. _calls]; }

        /// <summary>The <c>sendFunc</c> delegate handed to the injector.</summary>
        public bool Record(string displayText, string wrapped)
        {
            List<TaskCompletionSource> ready = [];
            lock (_lock)
            {
                _calls.Add((displayText, wrapped));
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_waiters[i].Target <= _calls.Count)
                    {
                        ready.Add(_waiters[i].Tcs);
                        _waiters.RemoveAt(i);
                    }
                }
            }
            foreach (var tcs in ready) tcs.TrySetResult();

            return Behavior?.Invoke(displayText, wrapped) ?? true;
        }

        /// <summary>Completes once at least <paramref name="expected"/> sends have been recorded.</summary>
        public async Task WaitForCountAsync(int expected, CancellationToken ct)
        {
            Task wait;
            lock (_lock)
            {
                if (_calls.Count >= expected) return;
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((expected, tcs));
                wait = tcs.Task;
            }
            await wait.WaitAsync(Timeout, ct);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Fixtures
    // ────────────────────────────────────────────────────────────────────────

    private static SystemEvent Evt(string id, EventType type = EventType.GoalCompleted, string? message = null, string? issueId = null) =>
        new(type, message ?? $"msg-{id}", GoalId: id, IssueId: issueId);

    private static HiveConfigFile ActiveConfig(
        List<string>? activeEvents = null,
        string? mode = "active",
        int? throttleSeconds = null) => new()
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig
            {
                Model = "test-model",
                EventNotifications = new EventNotificationsConfig
                {
                    Mode = mode,
                    ActiveEvents = activeEvents,
                    ThrottleSeconds = throttleSeconds
                }
            }
        };

    /// <summary>Number of notifications in a batched display text.</summary>
    private static int BatchSize(string displayText) =>
        displayText.Split("[System Notification]", StringSplitOptions.None).Length - 1;

    /// <summary>Reads the injector's private CTS so disposal can be observed.</summary>
    private static CancellationTokenSource? GetCts(ActiveEventInjector injector)
    {
        var field = typeof(ActiveEventInjector).GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_cts field not found on ActiveEventInjector");
        return (CancellationTokenSource?)field.GetValue(injector);
    }

    /// <summary>
    /// Whether <paramref name="cts"/> has been disposed. A disposed
    /// <see cref="CancellationTokenSource"/> throws <see cref="ObjectDisposedException"/> from
    /// its <see cref="CancellationTokenSource.Token"/> getter; a merely cancelled one does not.
    /// </summary>
    private static bool IsDisposed(CancellationTokenSource cts)
    {
        try { _ = cts.Token; return false; }
        catch (ObjectDisposedException) { return true; }
    }

    /// <summary>
    /// A <see cref="ChannelReader{T}"/> decorator that turns "a concurrent writer interleaves
    /// with the drain" from a scheduling accident into a structural guarantee: every successful
    /// <see cref="TryRead"/> synchronously writes a replacement event back into the underlying
    /// channel before returning.
    /// <para>
    /// The refill happens on the drain's own thread, inside the drain loop, on every iteration,
    /// so the channel is never observed empty while refilling is armed. The drain can therefore
    /// only terminate by honouring its own bound — which is exactly the production behaviour
    /// under test. No threads, sleeps, or scheduler cooperation are involved.
    /// </para>
    /// <para>
    /// <see cref="ReadBudget"/> is a tripwire, not a timing heuristic: a bounded drain performs
    /// at most <see cref="ChannelCapacity"/> reads per window, so exceeding a budget several
    /// times larger proves the bound is gone. Once tripped the reader reports empty, which lets
    /// an unbounded drain terminate and the test fail with a precise message instead of hanging.
    /// </para>
    /// </summary>
    private sealed class InterleavingReader : ChannelReader<SystemEvent>
    {
        /// <summary>Read ceiling before the runaway tripwire fires.</summary>
        internal const int ReadBudget = ChannelCapacity * 4;

        private readonly ChannelReader<SystemEvent> _inner;
        private readonly ChannelWriter<SystemEvent> _writer;
        private int _reads;
        private int _refills;
        private volatile bool _refilling;
        private volatile bool _exceeded;

        private InterleavingReader(ChannelReader<SystemEvent> inner, ChannelWriter<SystemEvent> writer)
        {
            _inner = inner;
            _writer = writer;
        }

        /// <summary>Whether the drain exceeded <see cref="ReadBudget"/> reads — i.e. ran away.</summary>
        public bool ExceededReadBudget => _exceeded;

        /// <summary>How many replacement events the seam injected during draining.</summary>
        public int RefillCount => Volatile.Read(ref _refills);

        /// <summary>Arms the interleaved writer. Until called the reader is a pass-through.</summary>
        public void StartRefilling() => _refilling = true;

        /// <summary>
        /// Replaces the injector's private channel with one exposing this reader, keeping the
        /// original writer so <c>OnEventReceived</c> (and the seam itself) still enqueue into the
        /// same bounded, drop-oldest buffer the production code created.
        /// </summary>
        public static InterleavingReader Install(ActiveEventInjector injector)
        {
            var field = typeof(ActiveEventInjector).GetField("_channel", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_channel field not found on ActiveEventInjector");

            var original = (Channel<SystemEvent>)field.GetValue(injector)!;
            var reader = new InterleavingReader(original.Reader, original.Writer);
            field.SetValue(injector, new SeamChannel(reader, original.Writer));
            return reader;
        }

        /// <inheritdoc />
        public override bool TryRead(out SystemEvent item)
        {
            if (_exceeded)
            {
                // Tripwire already fired: report empty so an unbounded drain can terminate and
                // the assertion reports the defect precisely rather than the test hanging.
                item = default!;
                return false;
            }

            var read = _inner.TryRead(out item!);
            if (!read) return false;

            if (++_reads > ReadBudget)
            {
                _exceeded = true;
                return true;
            }

            if (_refilling)
            {
                // THE interleaved write: synchronous, inside the drain, before returning.
                _writer.TryWrite(new SystemEvent(EventType.GoalCompleted, "refill", GoalId: "refill"));
                Interlocked.Increment(ref _refills);
            }

            return true;
        }

        /// <inheritdoc />
        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
            => _inner.WaitToReadAsync(cancellationToken);

        /// <inheritdoc />
        public override bool TryPeek(out SystemEvent item) => _inner.TryPeek(out item!);

        /// <inheritdoc />
        public override Task Completion => _inner.Completion;

    }

    /// <summary>
    /// Observes the processing loop's channel calls so a test can rendezvous on the loop
    /// becoming IDLE — parked at its outer read with nothing queued.
    /// <para>
    /// The production loop calls <c>WaitToReadAsync</c> only from the outer
    /// <c>ReadAllAsync</c> enumeration, and it can only get there after an expiry drain found
    /// the channel empty and the batch loop was skipped. Observing that call is therefore a
    /// precise, deterministic signal that the empty-window transition has fully completed —
    /// no sleeps, no polling, and no dependence on how the delay continuation was scheduled.
    /// </para>
    /// </summary>
    private sealed class IdleObserver : ChannelReader<SystemEvent>
    {
        private readonly ChannelReader<SystemEvent> _inner;
        private readonly object _lock = new();
        private TaskCompletionSource? _pending;

        private IdleObserver(ChannelReader<SystemEvent> inner) => _inner = inner;

        /// <summary>Replaces the injector's channel with one exposing this observing reader.</summary>
        public static IdleObserver Install(ActiveEventInjector injector)
        {
            var field = typeof(ActiveEventInjector).GetField("_channel", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_channel field not found on ActiveEventInjector");

            var original = (Channel<SystemEvent>)field.GetValue(injector)!;
            var reader = new IdleObserver(original.Reader);
            field.SetValue(injector, new SeamChannel(reader, original.Writer));
            return reader;
        }

        /// <summary>
        /// Returns a task completing on the next time the loop parks at its outer read.
        /// Arm this BEFORE the action expected to produce the idle transition.
        /// </summary>
        public Task WaitForNextIdleAsync()
        {
            lock (_lock)
            {
                _pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _pending.Task;
            }
        }

        /// <inheritdoc />
        public override bool TryRead(out SystemEvent item) => _inner.TryRead(out item!);

        /// <inheritdoc />
        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        {
            TaskCompletionSource? signal;
            lock (_lock) { signal = _pending; _pending = null; }
            signal?.TrySetResult();

            return _inner.WaitToReadAsync(cancellationToken);
        }

        /// <inheritdoc />
        public override bool TryPeek(out SystemEvent item) => _inner.TryPeek(out item!);

        /// <inheritdoc />
        public override Task Completion => _inner.Completion;
    }

    /// <summary>Pairs a decorated test reader with the production writer.</summary>
    private sealed class SeamChannel : Channel<SystemEvent>
    {
        public SeamChannel(ChannelReader<SystemEvent> reader, ChannelWriter<SystemEvent> writer)
        {
            Reader = reader;
            Writer = writer;
        }
    }

    /// <summary>
    /// A plain synchronous <see cref="IEventBus.OnEvent"/> subscriber that counts deliveries.
    /// <para>
    /// Used to prove a publish has been fully dispatched WITHOUT waiting.
    /// <see cref="EventBus.Publish"/> invokes every handler synchronously in subscription
    /// order, so when a probe registered AFTER the injector has counted an event, the
    /// injector's own handler has provably already run for that same event. Absence of a
    /// send then proves filtering/disabling rather than proving the test did not wait long
    /// enough.
    /// </para>
    /// <para>
    /// Deliberately NOT another <see cref="ActiveEventInjector"/>: a second injector carries
    /// its own bounded, drop-oldest channel and would itself silently drop events once more
    /// than the capacity is published — which cannot serve as a dispatch oracle.
    /// </para>
    /// </summary>
    private sealed class PublishProbe
    {
        private int _count;

        /// <summary>Number of events dispatched to this probe.</summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>Subscribes a probe to <paramref name="bus"/>, after any existing subscriber.</summary>
        public static PublishProbe Attach(EventBus bus)
        {
            var probe = new PublishProbe();
            bus.OnEvent += _ => Interlocked.Increment(ref probe._count);
            return probe;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Mode gating and null-safety
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Constructor_ModeActive_EnablesInjector()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var recorder = new SendRecorder();

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: ActiveConfig(),
            logger: new RecordingLogger<ActiveEventInjector>(),
            throttleOverride: TimeSpan.Zero,
            sendFunc: recorder.Record);

        bus.Publish(Evt("g-1"));

        await recorder.WaitForCountAsync(1, ct);
        Assert.Equal(1, recorder.Count);
    }

    [Theory]
    [InlineData("passive")]
    [InlineData("off")]
    [InlineData("bogus")]
    [InlineData(null)]
    public async Task Constructor_NonActiveMode_DisablesInjector(string? mode)
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var disabled = new SendRecorder();

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: ActiveConfig(mode: mode),
            logger: new RecordingLogger<ActiveEventInjector>(),
            throttleOverride: TimeSpan.Zero,
            sendFunc: disabled.Record);

        // Attached after the injector: Publish dispatches synchronously in subscription
        // order, so a counted probe proves the injector already saw this event.
        var probe = PublishProbe.Attach(bus);

        bus.Publish(Evt("g-1"));

        Assert.Equal(1, probe.Count);
        Assert.Equal(0, disabled.Count);
    }

    [Fact]
    public async Task Constructor_NullEventNotifications_DisablesInjector()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var disabled = new SendRecorder();
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig { Model = "test-model", EventNotifications = null }
        };

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: config,
            logger: new RecordingLogger<ActiveEventInjector>(),
            throttleOverride: TimeSpan.Zero,
            sendFunc: disabled.Record);

        var probe = PublishProbe.Attach(bus);

        bus.Publish(Evt("g-1"));

        Assert.Equal(1, probe.Count);
        Assert.Equal(0, disabled.Count);
    }

    [Fact]
    public async Task Constructor_NullDependencies_DisablesInjector_DisposalNullSafe()
    {
        // Every dependency null → disabled, and disposal must not throw.
        var injector = new ActiveEventInjector(
            composer: null,
            eventBus: null,
            config: null,
            logger: new RecordingLogger<ActiveEventInjector>());

        Assert.Null(GetCts(injector));
        await injector.DisposeAsync();

        // Null event bus with an otherwise valid active config → still disabled.
        var injector2 = new ActiveEventInjector(
            composer: null,
            eventBus: null,
            config: ActiveConfig(),
            logger: new RecordingLogger<ActiveEventInjector>(),
            sendFunc: (_, _) => true);

        Assert.Null(GetCts(injector2));
        await injector2.DisposeAsync();

        // No composer AND no sendFunc → nothing to send to → disabled.
        var injector3 = new ActiveEventInjector(
            composer: null,
            eventBus: new EventBus(),
            config: ActiveConfig(),
            logger: new RecordingLogger<ActiveEventInjector>());

        Assert.Null(GetCts(injector3));
        await injector3.DisposeAsync();
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Whitelist filtering
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Whitelist_OnlyConfiguredEventTypesAreInjected()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var recorder = new SendRecorder();

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: ActiveConfig(activeEvents: ["goal_completed"]),
            logger: new RecordingLogger<ActiveEventInjector>(),
            throttleOverride: TimeSpan.Zero,
            sendFunc: recorder.Record);

        // Filtering happens synchronously inside Publish, so once the trailing whitelisted
        // event has been sent, every non-whitelisted publish above has provably been rejected.
        bus.Publish(Evt("g-2", EventType.GoalFailed));
        bus.Publish(Evt("g-3", EventType.CiFailed));
        bus.Publish(Evt("g-4", EventType.IssueRaised));
        bus.Publish(Evt("g-5", EventType.GoalDispatched));
        bus.Publish(Evt("g-1", EventType.GoalCompleted));

        await recorder.WaitForCountAsync(1, ct);

        Assert.Equal(1, recorder.Count);
        Assert.Contains("g-1", recorder.Snapshot()[0].DisplayText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Whitelist_DefaultAllFour_WhenNoActiveEventsConfigured()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var recorder = new SendRecorder();

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: ActiveConfig(activeEvents: null),
            logger: new RecordingLogger<ActiveEventInjector>(),
            throttleOverride: TimeSpan.Zero,
            sendFunc: recorder.Record);

        bus.Publish(Evt("g-1", EventType.GoalCompleted));
        bus.Publish(Evt("g-2", EventType.GoalFailed));
        bus.Publish(Evt("g-3", EventType.CiFailed));
        bus.Publish(Evt("g-4", EventType.IssueRaised));

        await recorder.WaitForCountAsync(4, ct);
        Assert.Equal(4, recorder.Count);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Throttle and batching — driven entirely by controlled time
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Throttle_FirstEventAfterIdle_SendsImmediately()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var time = new ControlledTimeProvider();
        var recorder = new SendRecorder();
        var throttle = TimeSpan.FromSeconds(30);

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: ActiveConfig(),
            logger: new RecordingLogger<ActiveEventInjector>(),
            timeProvider: time,
            throttleOverride: throttle,
            sendFunc: recorder.Record);

        bus.Publish(Evt("g-1"));

        // The clock is NEVER advanced. If production delayed the first send behind the
        // throttle window, this rendezvous could not complete.
        await recorder.WaitForCountAsync(1, ct);
        Assert.Equal(1, recorder.Count);
    }

    [Fact]
    public async Task Throttle_EventsDuringWindow_AreBatchedAndSentTogetherAtExpiry()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var time = new ControlledTimeProvider();
        var recorder = new SendRecorder();
        var throttle = TimeSpan.FromSeconds(30);

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: ActiveConfig(),
            logger: new RecordingLogger<ActiveEventInjector>(),
            timeProvider: time,
            throttleOverride: throttle,
            sendFunc: recorder.Record);

        bus.Publish(Evt("g-1"));
        await recorder.WaitForCountAsync(1, ct);

        // The loop is now provably parked on the window opened by that send attempt.
        await time.WaitForTimerCountAsync(1, ct);

        bus.Publish(Evt("g-2"));
        bus.Publish(Evt("g-3"));

        // Parked on a manual timer only this test can fire: no further send is possible.
        Assert.Equal(1, recorder.Count);

        time.Advance(throttle);
        await recorder.WaitForCountAsync(2, ct);

        var calls = recorder.Snapshot();
        Assert.Contains("g-1", calls[0].DisplayText, StringComparison.Ordinal);
        Assert.DoesNotContain("g-2", calls[0].DisplayText, StringComparison.Ordinal);

        Assert.Contains("g-2", calls[1].DisplayText, StringComparison.Ordinal);
        Assert.Contains("g-3", calls[1].DisplayText, StringComparison.Ordinal);
        Assert.Equal(2, BatchSize(calls[1].DisplayText));
    }

    /// <summary>
    /// The batch sent at window expiry must itself open a NEW window. Removing that second
    /// window is caught twice over: the timer rendezvous would never resolve, and the send
    /// asserted as "not yet delivered" would already have happened.
    /// </summary>
    [Fact]
    public async Task Throttle_BatchAtExpiry_StartsNewWindow_NextEventWaitsForIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var time = new ControlledTimeProvider();
        var recorder = new SendRecorder();
        var throttle = TimeSpan.FromSeconds(30);

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: ActiveConfig(),
            logger: new RecordingLogger<ActiveEventInjector>(),
            timeProvider: time,
            throttleOverride: throttle,
            sendFunc: recorder.Record);

        // Send 1 (immediate) opens window 1.
        bus.Publish(Evt("g-1"));
        await recorder.WaitForCountAsync(1, ct);
        await time.WaitForTimerCountAsync(1, ct);

        // Batch sent at window-1 expiry → send 2.
        bus.Publish(Evt("g-2"));
        bus.Publish(Evt("g-3"));
        time.Advance(throttle);
        await recorder.WaitForCountAsync(2, ct);

        // THE ASSERTION UNDER TEST: send 2 opened window 2.
        await time.WaitForTimerCountAsync(2, ct);

        bus.Publish(Evt("g-4"));

        // g-4 must remain undelivered until window 2 advances.
        Assert.Equal(2, recorder.Count);

        time.Advance(throttle);
        await recorder.WaitForCountAsync(3, ct);

        var calls = recorder.Snapshot();
        Assert.Contains("g-4", calls[2].DisplayText, StringComparison.Ordinal);
        Assert.DoesNotContain("g-2", calls[2].DisplayText, StringComparison.Ordinal);
    }

    /// <summary>
    /// After a window expires with nothing queued, the NEXT event must be sent immediately
    /// rather than waiting for another window.
    /// <para>
    /// <b>Why a bare <c>Advance</c> is not enough.</b> Firing the timer only schedules the
    /// delay continuation. Publishing immediately afterwards races it: the event can already be
    /// queued when the continuation resumes, so it gets picked up by the EXPIRING window's drain
    /// and sent as part of that batch. The test would then pass without ever exercising the
    /// empty-window → idle transition it claims to cover.
    /// </para>
    /// <para>
    /// <b>The rendezvous.</b> <see cref="IdleObserver"/> watches the loop's own channel calls.
    /// The loop only reaches <c>WaitToReadAsync</c> after its expiry drain found the channel
    /// empty AND the batch loop was skipped — i.e. exactly the empty-window transition. Awaiting
    /// that signal proves the loop is parked and idle BEFORE <c>g-2</c> is published, so the
    /// event provably cannot belong to the expiring window.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Throttle_EmptyWindow_NextEventSendsImmediately()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var time = new ControlledTimeProvider();
        var recorder = new SendRecorder();
        var throttle = TimeSpan.FromSeconds(30);

        // Ordered install: the loop captures _channel.Reader right after the start gate, so the
        // observer must be in place before the gate is released (otherwise the swap races the
        // capture and the idle signal would never fire).
        var observerInstalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: ActiveConfig(),
            logger: new RecordingLogger<ActiveEventInjector>(),
            timeProvider: time,
            throttleOverride: throttle,
            startGate: () => observerInstalled.Task,
            sendFunc: recorder.Record);

        var idle = IdleObserver.Install(injector);
        observerInstalled.SetResult();

        bus.Publish(Evt("g-1"));
        await recorder.WaitForCountAsync(1, ct);
        await time.WaitForTimerCountAsync(1, ct);

        // The loop is parked on the throttle timer, so it has not yet re-entered the outer
        // read. Arm the observer for the idle transition that the expiry will produce.
        var idleAfterEmptyWindow = idle.WaitForNextIdleAsync();

        // Expire the window with nothing queued.
        time.Advance(throttle);

        // Rendezvous: the empty drain completed and the loop is back at its outer read.
        // Only now can a publish be attributed to the post-window (idle) state.
        await idleAfterEmptyWindow.WaitAsync(Timeout, ct);

        // The empty window produced no send.
        Assert.Equal(1, recorder.Count);

        // The clock is never advanced again, so this can only be delivered by the
        // first-after-idle immediate path.
        bus.Publish(Evt("g-2"));
        await recorder.WaitForCountAsync(2, ct);

        var calls = recorder.Snapshot();
        Assert.Equal(2, calls.Length);
        Assert.Contains("g-2", calls[1].DisplayText, StringComparison.Ordinal);
        // Delivered on its own, not folded into the expiring window's batch.
        Assert.Equal(1, BatchSize(calls[1].DisplayText));
    }

    [Fact]
    public async Task Throttle_ZeroOverride_NoWindowIsEverOpened()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var time = new ControlledTimeProvider();
        var recorder = new SendRecorder();

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: ActiveConfig(),
            logger: new RecordingLogger<ActiveEventInjector>(),
            timeProvider: time,
            throttleOverride: TimeSpan.Zero,
            sendFunc: recorder.Record);

        bus.Publish(Evt("g-1"));
        bus.Publish(Evt("g-2"));
        bus.Publish(Evt("g-3"));

        await recorder.WaitForCountAsync(3, ct);

        // A zero/negative throttle must skip the delay entirely.
        Assert.Equal(0, time.TimersCreated);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Bounded drain (regression: unbounded drain defeated the channel cap)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deterministic regression guard for the bounded expiry drain.
    /// <para>
    /// <b>Why a thread-based publisher is not enough.</b> An earlier version raced real
    /// publisher threads against the drain. That is scheduler-dependent: nothing forces a
    /// writer to interleave with <c>TryRead</c> while the drain is executing, so the runtime
    /// may pause every publisher after the clock advances. An unbounded drain would then empty
    /// the static queue, send promptly, and satisfy every assertion — passing despite the
    /// defect. Repeated stability cannot close that hole.
    /// </para>
    /// <para>
    /// <b>The seam.</b> This test swaps the injector's private channel for one whose reader is
    /// an <see cref="InterleavingReader"/>: every successful <c>TryRead</c> synchronously writes
    /// a fresh event back into the underlying channel before returning. That IS the concurrent
    /// writer, expressed as a guaranteed happens-before edge rather than a hoped-for
    /// interleaving — the writer provably runs inside the drain loop, on the drain's own
    /// thread, on every single iteration.
    /// </para>
    /// <para>
    /// Consequently the channel is never observed empty, so the outcome is decided purely by
    /// the drain bound:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Bounded (correct):</b> stops after exactly <see cref="ChannelCapacity"/> reads
    /// and sends.</item>
    /// <item><b>Unbounded (the defect):</b> <c>while (reader.TryRead(...))</c> never sees a
    /// false, so it spins forever — the send never arrives and the rendezvous below times out.
    /// A capped read counter also trips, reporting the runaway directly.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task Drain_WriterInterleavedWithEveryRead_IsBounded_AndSendIsNotPostponed()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var time = new ControlledTimeProvider();
        var recorder = new SendRecorder();
        var throttle = TimeSpan.FromSeconds(30);

        // The loop captures _channel.Reader ONCE, immediately after awaiting the start gate.
        // Holding the gate until the seam is installed makes the swap deterministically
        // visible to the loop; without this ordering the install races the reader capture and
        // the seam may never be used at all.
        var seamInstalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: ActiveConfig(),
            logger: new RecordingLogger<ActiveEventInjector>(),
            timeProvider: time,
            throttleOverride: throttle,
            startGate: () => seamInstalled.Task,
            sendFunc: recorder.Record);

        var seam = InterleavingReader.Install(injector);
        seamInstalled.SetResult();

        // Seed send opens the first window and parks the loop on the throttle timer.
        bus.Publish(Evt("seed"));
        await recorder.WaitForCountAsync(1, ct);
        await time.WaitForTimerCountAsync(1, ct);

        // Saturate the channel so the expiry drain begins against a full queue.
        for (var i = 0; i < ChannelCapacity; i++)
            bus.Publish(Evt($"queued-{i:D3}"));

        // Arm the interleaving writer: from here every successful read refills the channel.
        seam.StartRefilling();

        // Expire the window. The drain now runs with a writer interleaved on every read.
        time.Advance(throttle);

        // A bounded drain stops at the cap and sends. An unbounded drain spins forever here.
        await recorder.WaitForCountAsync(2, ct);

        // The runaway tripwire must not have fired.
        Assert.False(seam.ExceededReadBudget,
            $"The expiry drain performed more than {InterleavingReader.ReadBudget} reads against a " +
            "continuously refilled channel. The drain is unbounded: a concurrent writer can grow " +
            "the batch without bound and postpone the send indefinitely.");

        // Hard invariant: no single drain may exceed the channel capacity.
        foreach (var call in recorder.Snapshot())
        {
            var size = BatchSize(call.DisplayText);
            Assert.True(size <= ChannelCapacity,
                $"A batch contained {size} notifications, exceeding the channel capacity of {ChannelCapacity}. " +
                "The expiry drain is unbounded and the interleaved writer grew the batch without bound.");
        }

        // Non-vacuity: the seam really did interleave writes during the drain, so the bound was
        // genuinely exercised rather than trivially satisfied by an already-empty channel.
        Assert.True(seam.RefillCount > 1,
            $"Expected the interleaving writer to refill during the drain, but it ran {seam.RefillCount} time(s).");

        // Non-vacuity: the expiry batch aggregated the saturated channel.
        var expiryBatchSize = BatchSize(recorder.Snapshot()[1].DisplayText);
        Assert.True(expiryBatchSize > 1,
            $"Expected the expiry batch to aggregate a saturated channel, got {expiryBatchSize}.");
    }

    // ────────────────────────────────────────────────────────────────────────
    //  sendFunc semantics
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendFunc_ReturnsFalse_IsSilent_AndStillOpensThrottleWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var time = new ControlledTimeProvider();
        var logger = new RecordingLogger<ActiveEventInjector>();
        var throttle = TimeSpan.FromSeconds(30);
        var recorder = new SendRecorder { Behavior = (_, _) => false };

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: ActiveConfig(),
            logger: logger,
            timeProvider: time,
            throttleOverride: throttle,
            sendFunc: recorder.Record);

        bus.Publish(Evt("g-1"));
        await recorder.WaitForCountAsync(1, ct);

        // A rejected send must still open a window.
        await time.WaitForTimerCountAsync(1, ct);

        bus.Publish(Evt("g-2"));

        // Removing the post-attempt window would let this send through immediately.
        Assert.Equal(1, recorder.Count);

        // A plain false is NOT an error and must not be logged.
        Assert.False(logger.HasWarning("Active event injection failed"));

        time.Advance(throttle);
        await recorder.WaitForCountAsync(2, ct);

        Assert.Contains("g-2", recorder.Snapshot()[1].DisplayText, StringComparison.Ordinal);
        Assert.False(logger.HasWarning("Active event injection failed"));
    }

    [Fact]
    public async Task SendFunc_Throws_IsLogged_AndStillOpensThrottleWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var time = new ControlledTimeProvider();
        var logger = new RecordingLogger<ActiveEventInjector>();
        var throttle = TimeSpan.FromSeconds(30);
        var throwNext = 1;
        var recorder = new SendRecorder
        {
            Behavior = (_, _) =>
            {
                if (Interlocked.Exchange(ref throwNext, 0) == 1)
                    throw new InvalidOperationException("boom");
                return true;
            }
        };

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: ActiveConfig(),
            logger: logger,
            timeProvider: time,
            throttleOverride: throttle,
            sendFunc: recorder.Record);

        bus.Publish(Evt("g-1"));
        await recorder.WaitForCountAsync(1, ct);

        // A throwing send must be logged and must still open a window.
        await time.WaitForTimerCountAsync(1, ct);
        Assert.True(logger.HasWarning("Active event injection failed"));

        bus.Publish(Evt("g-2"));

        // Removing the post-attempt window would let this send through immediately.
        Assert.Equal(1, recorder.Count);

        time.Advance(throttle);
        await recorder.WaitForCountAsync(2, ct);

        // The loop survived the exception and kept processing.
        Assert.Contains("g-2", recorder.Snapshot()[1].DisplayText, StringComparison.Ordinal);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  startGate and channel bounding
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartGate_BlocksBeforeFirstRead_Queue51_OldestDropped()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var recorder = new SendRecorder();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: ActiveConfig(),
            logger: new RecordingLogger<ActiveEventInjector>(),
            throttleOverride: TimeSpan.Zero,
            startGate: () => gate.Task,
            sendFunc: recorder.Record);

        var probe = PublishProbe.Attach(bus);

        // Queue 51 events while the gate is closed. Capacity is 50 with drop-oldest, so the
        // first event is evicted and 2..51 survive.
        for (var i = 1; i <= 51; i++)
            bus.Publish(Evt($"g-{i:D2}"));

        // All 51 were dispatched synchronously to the injector, which still sent nothing
        // because the start gate blocks before the first channel read.
        Assert.Equal(51, probe.Count);
        Assert.Equal(0, recorder.Count);

        gate.SetResult();

        await recorder.WaitForCountAsync(ChannelCapacity, ct);
        Assert.Equal(ChannelCapacity, recorder.Count);

        var allText = string.Join("|", recorder.Snapshot().Select(c => c.DisplayText));
        Assert.DoesNotContain("g-01", allText, StringComparison.Ordinal);
        Assert.Contains("g-02", allText, StringComparison.Ordinal);
        Assert.Contains("g-51", allText, StringComparison.Ordinal);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Disposal
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dispose_Normal_DisposesCts_AndStopsProcessing()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var logger = new RecordingLogger<ActiveEventInjector>();
        var recorder = new SendRecorder();

        var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: ActiveConfig(),
            logger: logger,
            throttleOverride: TimeSpan.Zero,
            sendFunc: recorder.Record);

        var cts = GetCts(injector);
        Assert.NotNull(cts);
        Assert.False(IsDisposed(cts!), "The CTS must be live while the injector is running.");

        bus.Publish(Evt("g-1"));
        await recorder.WaitForCountAsync(1, ct);

        await injector.DisposeAsync();

        // The processing task completed, so the CTS must have been disposed.
        Assert.True(IsDisposed(cts!),
            "DisposeAsync must dispose the CTS once the processing task has completed.");
        Assert.False(logger.HasWarning("CTS not disposed"));
        Assert.False(logger.HasWarning("Disposal timed out"));

        // Unsubscribed: further publishes are dispatched to the bus but not processed.
        var probe = PublishProbe.Attach(bus);
        bus.Publish(Evt("g-2"));

        Assert.Equal(1, probe.Count);
        Assert.Equal(1, recorder.Count);
    }

    [Fact]
    public async Task Dispose_Timeout_LeavesCtsForGc_AndLogsWarnings()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var logger = new RecordingLogger<ActiveEventInjector>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: ActiveConfig(),
            logger: logger,
            throttleOverride: TimeSpan.Zero,
            startGate: () =>
            {
                enteredGate.TrySetResult();
                return gate.Task; // parks the processing loop until the test releases it
            },
            sendFunc: (_, _) => true,
            disposalTimeout: TimeSpan.FromMilliseconds(50));

        var cts = GetCts(injector);
        Assert.NotNull(cts);

        await enteredGate.Task.WaitAsync(Timeout, ct);

        // The loop cannot exit, so disposal must time out.
        await injector.DisposeAsync();

        Assert.True(logger.HasWarning("Disposal timed out"));
        Assert.True(logger.HasWarning("CTS not disposed"));

        // The task is still alive, so the CTS must be left for GC rather than disposed
        // underneath it.
        Assert.False(IsDisposed(cts!),
            "A CTS still in use by a live processing task must NOT be disposed.");

        gate.SetResult();
    }

    [Fact]
    public async Task Dispose_Disabled_IsNullSafe_AndLogsNothing()
    {
        var logger = new RecordingLogger<ActiveEventInjector>();

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: new EventBus(),
            config: ActiveConfig(mode: "passive"),
            logger: logger);

        Assert.Null(GetCts(injector));

        await injector.DisposeAsync();

        Assert.False(logger.HasWarning("Disposal timed out"));
        Assert.False(logger.HasWarning("CTS not disposed"));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Envelope and formatting
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Wrapped_UsesE0EnvelopePrefixAndDisplayText()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var recorder = new SendRecorder();

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: ActiveConfig(),
            logger: new RecordingLogger<ActiveEventInjector>(),
            throttleOverride: TimeSpan.Zero,
            sendFunc: recorder.Record);

        bus.Publish(Evt("g-1", EventType.GoalCompleted, message: "done"));
        await recorder.WaitForCountAsync(1, ct);

        var (displayText, wrapped) = recorder.Snapshot()[0];

        Assert.Equal($"{Composer.EnvelopePrefix}0{Composer.EnvelopeSeparator}{Composer.EnvelopeSuffix}{displayText}", wrapped);
        Assert.StartsWith("[System Notification]", displayText, StringComparison.Ordinal);
        Assert.Contains("g-1", displayText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisplayText_GoalCompleted_OmitsMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var recorder = new SendRecorder();

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: ActiveConfig(),
            logger: new RecordingLogger<ActiveEventInjector>(),
            throttleOverride: TimeSpan.Zero,
            sendFunc: recorder.Record);

        bus.Publish(Evt("g-1", EventType.GoalCompleted, message: "secret detail"));
        await recorder.WaitForCountAsync(1, ct);

        var displayText = recorder.Snapshot()[0].DisplayText;

        Assert.StartsWith("[System Notification]", displayText, StringComparison.Ordinal);
        Assert.Contains("g-1", displayText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret detail", displayText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EventType.GoalFailed, "tests failed", "analyze the failure")]
    [InlineData(EventType.CiFailed, "build broke", "CI failed")]
    public async Task DisplayText_NonCompletedGoalEvents_IncludeMessage(EventType type, string message, string expectedHint)
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var recorder = new SendRecorder();

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: ActiveConfig(),
            logger: new RecordingLogger<ActiveEventInjector>(),
            throttleOverride: TimeSpan.Zero,
            sendFunc: recorder.Record);

        bus.Publish(Evt("g-1", type, message: message));
        await recorder.WaitForCountAsync(1, ct);

        var displayText = recorder.Snapshot()[0].DisplayText;

        Assert.StartsWith("[System Notification]", displayText, StringComparison.Ordinal);
        Assert.Contains("g-1", displayText, StringComparison.Ordinal);
        Assert.Contains(message, displayText, StringComparison.Ordinal);
        Assert.Contains(expectedHint, displayText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisplayText_IssueRaised_IncludesIssueIdAndMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var recorder = new SendRecorder();

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: ActiveConfig(),
            logger: new RecordingLogger<ActiveEventInjector>(),
            throttleOverride: TimeSpan.Zero,
            sendFunc: recorder.Record);

        bus.Publish(Evt("g-1", EventType.IssueRaised, message: "worker found a bug", issueId: "ISS-42"));
        await recorder.WaitForCountAsync(1, ct);

        var displayText = recorder.Snapshot()[0].DisplayText;

        Assert.StartsWith("[System Notification]", displayText, StringComparison.Ordinal);
        Assert.Contains("ISS-42", displayText, StringComparison.Ordinal);
        Assert.Contains("worker found a bug", displayText, StringComparison.Ordinal);
        Assert.Contains("triage", displayText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisplayText_Batch_JoinsWithDoubleNewline()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var time = new ControlledTimeProvider();
        var recorder = new SendRecorder();
        var throttle = TimeSpan.FromSeconds(30);

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: ActiveConfig(),
            logger: new RecordingLogger<ActiveEventInjector>(),
            timeProvider: time,
            throttleOverride: throttle,
            sendFunc: recorder.Record);

        bus.Publish(Evt("g-1", EventType.GoalCompleted));
        await recorder.WaitForCountAsync(1, ct);
        await time.WaitForTimerCountAsync(1, ct);

        bus.Publish(Evt("g-2", EventType.GoalFailed, message: "fail-2"));
        bus.Publish(Evt("g-3", EventType.CiFailed, message: "fail-3"));

        time.Advance(throttle);
        await recorder.WaitForCountAsync(2, ct);

        var parts = recorder.Snapshot()[1].DisplayText.Split("\n\n");
        Assert.Equal(2, parts.Length);
        Assert.Contains("g-2", parts[0], StringComparison.Ordinal);
        Assert.Contains("g-3", parts[1], StringComparison.Ordinal);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Invalid event names
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Constructor_InvalidEventNames_LogsWarning()
    {
        var logger = new RecordingLogger<ActiveEventInjector>();

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: new EventBus(),
            config: ActiveConfig(activeEvents: ["goal_completed", "bogus_event", "not_whitelisted"]),
            logger: logger,
            throttleOverride: TimeSpan.Zero,
            sendFunc: (_, _) => true);

        var warning = logger.Snapshot()
            .FirstOrDefault(e => e.Level == LogLevel.Warning
                && e.Message.Contains("Invalid active event names ignored", StringComparison.Ordinal));

        Assert.NotEqual(default, warning);
        Assert.Contains("bogus_event", warning.Message, StringComparison.Ordinal);
        Assert.Contains("not_whitelisted", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("goal_completed", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Constructor_NoInvalidEvents_NoWarningLogged()
    {
        var logger = new RecordingLogger<ActiveEventInjector>();

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: new EventBus(),
            config: ActiveConfig(activeEvents: ["goal_completed", "issue_raised"]),
            logger: logger,
            throttleOverride: TimeSpan.Zero,
            sendFunc: (_, _) => true);

        Assert.False(logger.HasWarning("Invalid active event names ignored"));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Config snapshot — ReloadFrom must not affect a running injector
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfigSnapshot_ReloadFrom_DoesNotChangeWhitelistOrMode()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var recorder = new SendRecorder();
        var config = ActiveConfig(activeEvents: ["goal_completed"]);

        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: config,
            logger: new RecordingLogger<ActiveEventInjector>(),
            throttleOverride: TimeSpan.Zero,
            sendFunc: recorder.Record);

        // Reload to a different mode AND a different whitelist — neither may take effect.
        config.ReloadFrom(ActiveConfig(activeEvents: ["ci_failed"], mode: "off", throttleSeconds: 5));

        var probe = PublishProbe.Attach(bus);

        // In the reloaded whitelist this would qualify; under the snapshot it must not.
        bus.Publish(Evt("g-2", EventType.CiFailed));
        // In the snapshotted whitelist this qualifies.
        bus.Publish(Evt("g-1", EventType.GoalCompleted));

        Assert.Equal(2, probe.Count);
        await recorder.WaitForCountAsync(1, ct);

        Assert.Equal(1, recorder.Count);
        Assert.Contains("g-1", recorder.Snapshot()[0].DisplayText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The CONFIGURED throttle (no <c>throttleOverride</c>) must be snapshotted at construction.
    /// Proven by the exact due-time production requests from the time provider: the original
    /// <c>EffectiveThrottleSeconds</c>, never the value a later <c>ReloadFrom</c> installed.
    /// </summary>
    [Fact]
    public async Task ConfigSnapshot_ConfiguredThrottle_IsSnapshotted_AcrossReloadFrom()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new EventBus();
        var time = new ControlledTimeProvider();
        var recorder = new SendRecorder();

        // Deliberately NOT the 30-second configuration default: a hard-coded or defaulted
        // delay must not be able to satisfy this test. Also distinct from the reloaded value.
        const int configuredSeconds = 47;
        const int reloadedSeconds = 211;
        const int defaultSeconds = 30;

        Assert.NotEqual(defaultSeconds, configuredSeconds);
        Assert.NotEqual(reloadedSeconds, configuredSeconds);
        Assert.Equal(defaultSeconds, new EventNotificationsConfig().EffectiveThrottleSeconds);

        var config = ActiveConfig(throttleSeconds: configuredSeconds);

        // No throttleOverride: the injector must derive its window from EffectiveThrottleSeconds.
        await using var injector = new ActiveEventInjector(
            composer: null,
            eventBus: bus,
            config: config,
            logger: new RecordingLogger<ActiveEventInjector>(),
            timeProvider: time,
            sendFunc: recorder.Record);

        // Mutate the live config in both supported ways after construction.
        config.Composer!.EventNotifications!.ThrottleSeconds = reloadedSeconds;
        config.ReloadFrom(ActiveConfig(throttleSeconds: reloadedSeconds));

        bus.Publish(Evt("g-1"));
        await recorder.WaitForCountAsync(1, ct);
        await time.WaitForTimerCountAsync(1, ct);

        // The window is the CONFIGURED value captured at construction — neither the default
        // nor the value installed by the later mutation/ReloadFrom.
        Assert.Equal(TimeSpan.FromSeconds(configuredSeconds), time.RequestedDelays[0]);

        // And that exact window is what gates the next send.
        bus.Publish(Evt("g-2"));
        Assert.Equal(1, recorder.Count);

        // Advancing by the DEFAULT must not be enough: this is what fails if production ever
        // substitutes a hard-coded/default 30-second delay for the configured one.
        time.Advance(TimeSpan.FromSeconds(defaultSeconds));
        Assert.Equal(1, recorder.Count);

        // Advancing the remainder of the configured window releases the send.
        time.Advance(TimeSpan.FromSeconds(configuredSeconds - defaultSeconds));
        await recorder.WaitForCountAsync(2, ct);

        Assert.Contains("g-2", recorder.Snapshot()[1].DisplayText, StringComparison.Ordinal);
    }
}

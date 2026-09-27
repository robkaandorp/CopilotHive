namespace CopilotHive.Tests;

/// <summary>
/// A shared, fully manual <see cref="TimeProvider"/> for deterministic tests: the clock advances
/// ONLY when the test calls <see cref="Advance"/>, so "nothing happened yet" is a fact about a
/// parked producer rather than a guess made from a wall-clock sleep.
/// <para>
/// Every timer production registers through <see cref="CreateTimer"/> (and every re-arm through
/// <see cref="ITimer.Change"/>) is recorded, so a test can rendezvous on observed state — "the
/// producer has parked on a timer whose due time is exactly X" — instead of sleeping, and can
/// assert the exact requested window length (which is how a snapshotted configuration value is
/// told apart from a default).
/// </para>
/// <para>
/// All clock channels move together:
/// </para>
/// <list type="bullet">
/// <item><see cref="GetUtcNow"/> — absolute UTC time.</item>
/// <item><see cref="GetTimestamp"/> and <see cref="TimestampFrequency"/> — coherent monotonic
/// ticks, so <see cref="TimeProvider.GetElapsedTime(long)"/> equals the total advanced time
/// exactly (timestamp = elapsed <see cref="TimeSpan"/> ticks).</item>
/// <item>Timers — due times are computed from the manual clock and only fire from
/// <see cref="Advance"/>.</item>
/// </list>
/// <para>
/// Determinism contract for consumers: rendezvous on <see cref="WaitForPendingTimerAsync"/> or
/// <see cref="WaitForTimerCountAsync"/> rather than sleeping, and never advance the clock while a
/// rendezvous is in flight.
/// </para>
/// </summary>
internal sealed class ControlledTimeProvider : TimeProvider
{
    /// <summary>
    /// Upper bound for every rendezvous implemented by this class. Exceeding it fails the test:
    /// it means production never registered the awaited timer — typically because it never used
    /// the <see cref="TimeProvider"/> seam at all.
    /// </summary>
    internal static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly List<(int Target, TaskCompletionSource Tcs)> _waiters = [];
    private readonly List<(TimeSpan DueTime, TaskCompletionSource Tcs)> _pendingTimerWaiters = [];
    private readonly List<TimeSpan> _requestedDelays = [];
    private DateTimeOffset _now = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private long _elapsedTicks;
    private int _timersCreated;

    /// <summary>Number of timers production has registered through this provider.</summary>
    public int TimersCreated { get { lock (_lock) return _timersCreated; } }

    /// <summary>
    /// Number of <see cref="ITimer"/>s that are still PENDING — created and neither fired nor
    /// disposed. After a producer has been cleaned up this must be zero, which is how tests prove
    /// no producer was left parked forever on the manual clock.
    /// </summary>
    public int PendingTimerCount { get { lock (_lock) return _timers.Count(t => !t.Fired); } }

    /// <summary>The due-times production requested at timer creation, in registration order.</summary>
    public TimeSpan[] RequestedDelays { get { lock (_lock) return [.. _requestedDelays]; } }

    /// <summary>
    /// Synchronous snapshot of the remaining due time of every PENDING timer (created, not
    /// fired, not disposed), relative to the current manual <c>now</c>, in registration order.
    /// Computed from each timer's absolute due time (<c>DueAt - now</c>), so a re-armed timer
    /// reports its current remaining time; for a timer that was never re-armed and before any
    /// <see cref="Advance"/> this equals its requested due time.
    /// </summary>
    public IReadOnlyList<TimeSpan> PendingDueTimes
    {
        get { lock (_lock) return [.. _timers.Where(t => !t.Fired).Select(t => t.DueAt - _now)]; }
    }

    /// <summary>Total time advanced through <see cref="Advance"/>.</summary>
    public TimeSpan Elapsed { get { lock (_lock) return TimeSpan.FromTicks(_elapsedTicks); } }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() { lock (_lock) return _now; }

    /// <summary>
    /// Coherent monotonic timestamp: the manual elapsed ticks. Together with
    /// <see cref="TimestampFrequency"/> set to <see cref="TimeSpan.TicksPerSecond"/>, this makes
    /// <see cref="TimeProvider.GetElapsedTime(long)"/> return exactly the total advanced time —
    /// without these overrides elapsed-time measurements would silently fall back to a real
    /// stopwatch and re-introduce wall-clock nondeterminism.
    /// </summary>
    public override long GetTimestamp() { lock (_lock) return _elapsedTicks; }

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, this);
        List<TaskCompletionSource> ready = [];

        lock (_lock)
        {
            timer.RequestedDueTime = dueTime;
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

            // Due-time rendezvous, resolved under the SAME lock that just registered the timer:
            // a waiter can neither miss a timer created after its check nor be satisfied by one
            // created before it armed.
            SignalPendingWaitersLocked(ready);
        }

        foreach (var tcs in ready) tcs.TrySetResult();
        return timer;
    }

    /// <summary>
    /// Completes once production has registered at least <paramref name="expected"/> timers.
    /// A timeout here means production never awaited a <see cref="TimeProvider"/>-aware delay —
    /// i.e. the awaited window (or the required overload) was removed.
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

        await wait.WaitAsync(HangGuard, ct);
    }

    /// <summary>
    /// Completes once a PENDING (created, not yet fired, not yet disposed) timer whose current
    /// requested due time is EXACTLY <paramref name="dueTime"/> exists on this provider.
    /// <para>
    /// This is what keeps distinct waits apart before the clock is advanced: the overall
    /// monitoring timeout, a poll delay, a Retry-After hint and a short probe timeout are
    /// separate timers with separate due times, so a test can rendezvous on the one it means
    /// instead of inferring intent from a timer count.
    /// </para>
    /// <para>
    /// The rendezvous is registered under the timer-creation lock and resolved from
    /// <see cref="CreateTimer"/>/re-arm (never by polling), so it is a real hand-off and cannot
    /// race the producer. Timeout means production never registered a timer with that due time.
    /// </para>
    /// </summary>
    /// <param name="dueTime">The exact due time production must be waiting on.</param>
    /// <param name="ct">Cancellation token for the test.</param>
    public async Task WaitForPendingTimerAsync(TimeSpan dueTime, CancellationToken ct)
    {
        Task wait;
        lock (_lock)
        {
            if (HasPendingTimerLocked(dueTime)) return;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingTimerWaiters.Add((dueTime, tcs));
            wait = tcs.Task;
        }

        await wait.WaitAsync(HangGuard, ct);
    }

    /// <summary>Advances the clock and fires every pending timer that has come due.</summary>
    public void Advance(TimeSpan delta)
    {
        List<ManualTimer> due;
        lock (_lock)
        {
            _now += delta;
            _elapsedTicks += delta.Ticks;
            due = _timers.Where(t => !t.Fired && t.DueAt <= _now).ToList();
            foreach (var t in due) t.Fired = true;
        }

        // Fired outside the lock: a due callback may synchronously create or re-arm its next timer.
        foreach (var t in due) t.Fire();
    }

    /// <summary>Re-arms a timer, arming any rendezvous waiting on its new due time.</summary>
    internal void UpdateDueAt(ManualTimer timer, TimeSpan dueTime)
    {
        List<TaskCompletionSource> ready = [];
        lock (_lock)
        {
            timer.RequestedDueTime = dueTime;
            timer.DueAt = _now + dueTime;
            timer.Fired = false;
            SignalPendingWaitersLocked(ready);
        }

        foreach (var tcs in ready) tcs.TrySetResult();
    }

    internal void Remove(ManualTimer timer)
    {
        lock (_lock) _timers.Remove(timer);
    }

    /// <summary>
    /// Moves every pending-timer rendezvous that is now satisfied into <paramref name="ready"/>.
    /// Caller holds the lock.
    /// </summary>
    private void SignalPendingWaitersLocked(List<TaskCompletionSource> ready)
    {
        for (var i = _pendingTimerWaiters.Count - 1; i >= 0; i--)
        {
            if (HasPendingTimerLocked(_pendingTimerWaiters[i].DueTime))
            {
                ready.Add(_pendingTimerWaiters[i].Tcs);
                _pendingTimerWaiters.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Whether a pending timer with exactly this requested due time exists. Caller holds the lock.
    /// </summary>
    private bool HasPendingTimerLocked(TimeSpan dueTime)
        => _timers.Any(t => !t.Fired && t.RequestedDueTime == dueTime);

    /// <summary>
    /// An <see cref="ITimer"/> that only fires when <see cref="Advance"/> moves the owning clock
    /// past its due time.
    /// <para>
    /// <see cref="RequestedDueTime"/> is the due time production is currently waiting on — set at
    /// creation and updated by <see cref="Change"/> — which is the stable identity a
    /// <see cref="WaitForPendingTimerAsync"/> rendezvous matches, unlike <see cref="DueAt"/>,
    /// which is an absolute manual time.
    /// </para>
    /// </summary>
    internal sealed class ManualTimer(TimerCallback callback, object? state, ControlledTimeProvider owner) : ITimer
    {
        /// <summary>The absolute manual time this timer is due at.</summary>
        public DateTimeOffset DueAt;

        /// <summary>Whether the manual clock has already passed this timer's due time.</summary>
        public bool Fired;

        /// <summary>The due time production requested, updated whenever production re-arms.</summary>
        public TimeSpan RequestedDueTime { get; set; }

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

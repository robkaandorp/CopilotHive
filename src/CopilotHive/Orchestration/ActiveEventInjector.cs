using CopilotHive.Configuration;
using CopilotHive.Services;

using System.Threading.Channels;

namespace CopilotHive.Orchestration;

/// <summary>
/// Background service that subscribes to <see cref="IEventBus"/> and injects significant
/// system events into the <see cref="Composer"/> proactively (active mode).
/// <para>
/// <b>Throttling and batching:</b> The first event after idle is injected immediately.
/// Every injection attempt (successful, rejected, or throwing) starts a throttle window
/// (<see cref="EventNotificationsConfig.ThrottleSeconds"/>, overridable for tests).
/// Events arriving during the window are batched and injected together when the window
/// expires, which starts a new window. If a window expires empty, the next event is
/// injected immediately.
/// </para>
/// <para>
/// <b>Config snapshot:</b> The active mode, whitelisted event types, and effective throttle
/// seconds are captured at construction time. <see cref="HiveConfigFile.ReloadFrom"/> does
/// not change a running injector.
/// </para>
/// <para>
/// <b>Passive buffer duplicate:</b> Active events also remain in the passive
/// <see cref="ComposerEventSubscriber"/> buffer and may be delivered again with the next
/// user message. This is intentional and harmless — the passive buffer is drained and
/// cleared on every send, so the duplicate only appears in the chat history.
/// </para>
/// <para>
/// The injector is disabled when any of <c>composer</c>, <c>eventBus</c>, <c>config</c>,
/// or <c>config.Composer.EventNotifications</c> is null, or when
/// <see cref="EventNotificationsConfig.EffectiveMode"/> is not <c>"active"</c>. A disabled
/// injector subscribes to nothing, starts no background task, and creates no CTS.
/// </para>
/// </summary>
public sealed class ActiveEventInjector : IAsyncDisposable
{
    /// <summary>Bounded-channel capacity; oldest events are dropped when full.</summary>
    private const int ChannelCapacity = 50;

    private readonly Composer? _composer;
    private readonly IEventBus? _eventBus;
    private readonly ILogger<ActiveEventInjector> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Task>? _startGate;
    private readonly Func<string, string, bool>? _sendFunc;
    private readonly TimeSpan? _disposalTimeout;
    private readonly TimeSpan _throttleDelay;
    private readonly HashSet<EventType> _activeEventTypes;
    private readonly Channel<SystemEvent> _channel = Channel.CreateBounded<SystemEvent>(
        new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly CancellationTokenSource? _cts;
    private readonly Task _processingTask;
    private readonly bool _enabled;

    /// <summary>
    /// Creates the injector. Null dependencies or a non-active mode produce a disabled,
    /// inert instance: no subscription, no background task, no CTS.
    /// <para>
    /// The config snapshot (<see cref="EventNotificationsConfig.EffectiveMode"/>,
    /// <see cref="EventNotificationsConfig.GetActiveEventTypes"/>,
    /// <see cref="EventNotificationsConfig.EffectiveThrottleSeconds"/>) is captured here
    /// and never re-read. <see cref="HiveConfigFile.ReloadFrom"/> has no effect on a
    /// running injector.
    /// </para>
    /// </summary>
    /// <param name="composer">The Composer to inject notifications into.</param>
    /// <param name="eventBus">The event bus to subscribe to.</param>
    /// <param name="config">Hive configuration; the event-notifications section is read once.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="timeProvider">Time provider for throttle delays; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="throttleOverride">Test seam: overrides the configured throttle delay. Zero/negative → no delay.</param>
    /// <param name="startGate">Test seam: awaited before the first channel read; blocks the processing loop until released.</param>
    /// <param name="sendFunc">Test seam: replaces the Composer send. Returning false simulates a rejected send (silent, window still starts).</param>
    /// <param name="disposalTimeout">Test seam: timeout for waiting on the processing task during disposal. Defaults to 5 seconds.</param>
    public ActiveEventInjector(
        Composer? composer,
        IEventBus? eventBus,
        HiveConfigFile? config,
        ILogger<ActiveEventInjector> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? throttleOverride = null,
        Func<Task>? startGate = null,
        Func<string, string, bool>? sendFunc = null,
        TimeSpan? disposalTimeout = null)
    {
        _composer = composer;
        _eventBus = eventBus;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startGate = startGate;
        _sendFunc = sendFunc;
        _disposalTimeout = disposalTimeout;

        var notifConfig = config?.Composer?.EventNotifications;
        if (notifConfig is not null)
        {
            var invalid = notifConfig.GetInvalidEventNames();
            if (invalid.Count > 0)
                _logger.LogWarning("Invalid active event names ignored: {Names}", string.Join(", ", invalid));
        }

        if (eventBus is null || config is null || notifConfig is null
            || notifConfig.EffectiveMode != "active"
            || (composer is null && sendFunc is null))
        {
            _enabled = false;
            _activeEventTypes = [];
            _throttleDelay = TimeSpan.Zero;
            _cts = null;
            _processingTask = Task.CompletedTask;
            return;
        }

        _enabled = true;
        _activeEventTypes = notifConfig.GetActiveEventTypes();
        _throttleDelay = throttleOverride is { } overrideDelay
            ? (overrideDelay > TimeSpan.Zero ? overrideDelay : TimeSpan.Zero)
            : TimeSpan.FromSeconds(notifConfig.EffectiveThrottleSeconds);

        _cts = new CancellationTokenSource();
        eventBus.OnEvent += OnEventReceived;
        _processingTask = Task.Run(() => ProcessQueueAsync(_cts.Token));
    }

    /// <summary>
    /// Filters events to the whitelisted active types and enqueues them into the bounded
    /// channel (drop-oldest at capacity 50). Non-whitelisted event types are ignored.
    /// </summary>
    private void OnEventReceived(SystemEvent evt)
    {
        if (!_activeEventTypes.Contains(evt.Type))
            return;
        _channel.Writer.TryWrite(evt);
    }

    /// <summary>
    /// Processing loop: reads events from the channel and injects them into the Composer
    /// with throttle windows. The first event after idle is injected immediately; every
    /// send attempt starts a window via <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>;
    /// events arriving during a window are batched and sent together at window expiry,
    /// which starts a new window; an empty window leaves the next event immediate.
    /// <para>
    /// Each expiry drain is bounded to at most <see cref="ChannelCapacity"/> events (see
    /// <see cref="DrainUpToCapacity"/>), so a sustained publisher can never grow a batch
    /// without bound nor postpone the send indefinitely.
    /// </para>
    /// </summary>
    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        try
        {
            if (_startGate is not null)
            {
                try { await _startGate(); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { _logger.LogWarning(ex, "Start gate failed"); return; }
            }

            var batch = new List<SystemEvent>();
            var reader = _channel.Reader;

            await foreach (var evt in reader.ReadAllAsync(ct))
            {
                try
                {
                    // First event after idle (or after an empty window): inject immediately.
                    batch.Add(evt);
                    SendBatch(batch);
                    batch.Clear();

                    // Every send attempt (success, rejection, or exception) starts a throttle window.
                    if (_throttleDelay <= TimeSpan.Zero)
                        continue;

                    await Task.Delay(_throttleDelay, _timeProvider, ct);

                    // Bounded drain: take at most one channel-capacity worth of events that
                    // arrived during the window. Later arrivals are left for the next window.
                    DrainUpToCapacity(reader, batch);

                    // While events keep arriving, keep sending batches at window expiry.
                    // Each iteration sends a bounded batch and then awaits a fresh window,
                    // so cancellation is observed at least once per batch.
                    while (batch.Count > 0)
                    {
                        SendBatch(batch);
                        batch.Clear();
                        await Task.Delay(_throttleDelay, _timeProvider, ct);
                        DrainUpToCapacity(reader, batch);
                    }
                    // Empty window: the next event is injected immediately (loop back to top).
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { _logger.LogWarning(ex, "Active event injection failed"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Processing loop faulted"); }
    }

    /// <summary>
    /// Moves at most <see cref="ChannelCapacity"/> already-queued events from
    /// <paramref name="reader"/> into <paramref name="batch"/>.
    /// <para>
    /// The bound is load-bearing, not cosmetic. Reading frees a channel slot, so an
    /// unbounded <c>while (reader.TryRead(...))</c> drain lets a concurrent publisher keep
    /// the loop consuming indefinitely: the batch would grow past the channel capacity, the
    /// send would be postponed for as long as the publisher sustains, and cancellation would
    /// not be observed until the drain happened to end. Taking a finite snapshot instead
    /// guarantees the drain terminates after at most <see cref="ChannelCapacity"/> reads and
    /// leaves any later arrivals for the next throttle window.
    /// </para>
    /// </summary>
    /// <param name="reader">The channel reader to drain from.</param>
    /// <param name="batch">The batch to append the drained events to.</param>
    private static void DrainUpToCapacity(ChannelReader<SystemEvent> reader, List<SystemEvent> batch)
    {
        var drained = 0;
        while (drained < ChannelCapacity && reader.TryRead(out var pending))
        {
            batch.Add(pending);
            drained++;
        }
    }

    /// <summary>
    /// Formats the batch and injects it into the Composer (or the <see cref="_sendFunc"/>
    /// test seam). A rejected send (false) is silent; an exception is logged. In both cases
    /// the throttle window still starts (handled by the caller).
    /// </summary>
    private void SendBatch(List<SystemEvent> batch)
    {
        string displayText = FormatBatch(batch);
        string wrapped = $"{Composer.EnvelopePrefix}0{Composer.EnvelopeSeparator}{Composer.EnvelopeSuffix}{displayText}";

        try
        {
            if (_sendFunc is not null)
                _sendFunc(displayText, wrapped);
            else if (_composer is not null)
                _composer.SendActiveNotification(displayText, wrapped);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Active event injection failed");
        }
    }

    /// <summary>
    /// Formats a single event as a <c>[System Notification]</c> message. GoalCompleted
    /// omits <see cref="SystemEvent.Message"/> (not useful); all other types include it.
    /// </summary>
    private static string FormatNotification(SystemEvent evt) => evt.Type switch
    {
        EventType.GoalCompleted => $"[System Notification]\nGoal '{evt.GoalId}' has completed and been merged. You may want to update linked issues or proceed with the release.",
        EventType.GoalFailed => $"[System Notification]\nGoal '{evt.GoalId}' has failed: {evt.Message}. You may want to analyze the failure and create a fix goal.",
        EventType.CiFailed => $"[System Notification]\nCI failed for goal '{evt.GoalId}': {evt.Message}. You may want to triage any auto-created issues and create a fix goal.",
        EventType.IssueRaised => $"[System Notification]\nIssue '{evt.IssueId}' raised: {evt.Message}. You may want to triage it.",
        _ => $"[System Notification]\n{evt.Type}: {evt.Message}"
    };

    /// <summary>Joins the formatted notifications of a batch with double newlines.</summary>
    private static string FormatBatch(List<SystemEvent> events) =>
        string.Join("\n\n", events.Select(FormatNotification));

    /// <summary>
    /// Unsubscribes from the event bus, completes the channel, cancels the processing
    /// loop, and waits for it to exit within the disposal timeout. The disposal timeout
    /// intentionally uses the system timer (not <see cref="_timeProvider"/>) — disposal
    /// timing is infrastructure, not application logic. If the processing task does not
    /// complete in time, the CTS is left for GC (a live task may still be using it).
    /// Null-safe: a disabled injector completes the channel and returns immediately.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_eventBus is not null) _eventBus.OnEvent -= OnEventReceived;
        _channel.Writer.TryComplete();
        if (!_enabled) return;

        _cts!.Cancel();
        var timeout = _disposalTimeout ?? TimeSpan.FromSeconds(5);
        try { await _processingTask.WaitAsync(timeout); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { _logger.LogWarning("Disposal timed out"); }

        if (_processingTask.IsCompleted) _cts.Dispose();
        else _logger.LogWarning("CTS not disposed");
    }
}

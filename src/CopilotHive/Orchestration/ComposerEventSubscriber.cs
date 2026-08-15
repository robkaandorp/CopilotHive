using CopilotHive.Services;

namespace CopilotHive.Orchestration;

/// <summary>Buffers system events for passive delivery to the Composer on the next user message.</summary>
public sealed class ComposerEventSubscriber : IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly object _lock = new();
    private readonly Queue<SystemEvent> _pending = new();
    private const int MaxBufferSize = 50;

    /// <summary>Creates a subscriber and subscribes to the event bus.</summary>
    /// <param name="eventBus">The event bus to subscribe to.</param>
    public ComposerEventSubscriber(IEventBus eventBus)
    {
        _eventBus = eventBus;
        _eventBus.OnEvent += OnEventReceived;
    }

    private void OnEventReceived(SystemEvent evt)
    {
        lock (_lock)
        {
            _pending.Enqueue(evt);
            while (_pending.Count > MaxBufferSize)
                _pending.Dequeue();
        }
    }

    /// <summary>Atomically drains all pending events and returns them. Clears the buffer.</summary>
    /// <returns>The drained events. Use <see cref="RestoreEvents"/> to put them back on failure.</returns>
    public List<SystemEvent> DrainPendingEvents()
    {
        lock (_lock)
        {
            var result = _pending.ToList();
            _pending.Clear();
            return result;
        }
    }

    /// <summary>Restores events to the front of the buffer. MaxBufferSize overflow applies.</summary>
    /// <param name="events">The events to restore.</param>
    public void RestoreEvents(List<SystemEvent> events)
    {
        lock (_lock)
        {
            var existing = _pending.ToList();
            _pending.Clear();
            foreach (var e in events) _pending.Enqueue(e);
            foreach (var e in existing) _pending.Enqueue(e);
            while (_pending.Count > MaxBufferSize) _pending.Dequeue();
        }
    }

    /// <summary>Returns pending events without clearing the buffer.</summary>
    /// <returns>A copy of the pending events.</returns>
    public List<SystemEvent> PeekPendingEvents()
    {
        lock (_lock) { return _pending.ToList(); }
    }

    /// <inheritdoc />
    public void Dispose() => _eventBus.OnEvent -= OnEventReceived;
}

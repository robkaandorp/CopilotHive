namespace CopilotHive.Services;

/// <summary>Type of system event published to the event bus.</summary>
public enum EventType
{
    /// <summary>A goal completed successfully and was merged.</summary>
    GoalCompleted,
    /// <summary>A goal failed and was marked as Failed.</summary>
    GoalFailed,
    /// <summary>A goal was dispatched to a worker.</summary>
    GoalDispatched,
    /// <summary>An issue was raised by a worker, the Brain, or the Composer.</summary>
    IssueRaised,
    /// <summary>An issue was resolved or closed.</summary>
    IssueResolved,
    /// <summary>A release was marked as Released.</summary>
    ReleaseCompleted,
    /// <summary>CI passed for a goal's repository.</summary>
    CiSucceeded,
    /// <summary>CI failed for a goal's repository.</summary>
    CiFailed,
}

/// <summary>A typed system event with a human-readable message and optional entity references.</summary>
/// <param name="Type">The event type.</param>
/// <param name="Message">Human-readable summary for the Composer.</param>
/// <param name="GoalId">Goal ID if the event relates to a goal.</param>
/// <param name="IssueId">Issue ID if the event relates to an issue.</param>
/// <param name="ReleaseId">Release ID if the event relates to a release.</param>
/// <param name="Repository">Repository name if applicable.</param>
/// <param name="Timestamp">UTC timestamp; auto-populated to <see cref="DateTime.UtcNow"/> if default.</param>
public sealed record SystemEvent(
    EventType Type,
    string Message,
    string? GoalId = null,
    string? IssueId = null,
    string? ReleaseId = null,
    string? Repository = null,
    DateTime Timestamp = default);

/// <summary>Typed event bus for broadcasting system events to subscribers.</summary>
public interface IEventBus
{
    /// <summary>Fired when an event is published.</summary>
    event Action<SystemEvent>? OnEvent;

    /// <summary>Publishes an event to all subscribers.</summary>
    /// <param name="evt">The event to publish.</param>
    void Publish(SystemEvent evt);
}

/// <summary>Default implementation of <see cref="IEventBus"/>. Thread-safe with exception isolation.</summary>
public sealed class EventBus : IEventBus
{
    /// <inheritdoc />
    public event Action<SystemEvent>? OnEvent;

    /// <inheritdoc />
    public void Publish(SystemEvent evt)
    {
        if (evt.Timestamp == default)
            evt = evt with { Timestamp = DateTime.UtcNow };

        var handlers = OnEvent;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action<SystemEvent>)handler)(evt); }
            catch { }
        }
    }
}

using CopilotHive.Services;
using YamlDotNet.Serialization;

namespace CopilotHive.Configuration;

/// <summary>
/// Configuration for active system event notifications injected into the Composer.
/// <para>
/// Modes: <c>"passive"</c> (default — events are only delivered with the next user message),
/// <c>"active"</c> (significant events are injected proactively), <c>"off"</c> (no event delivery).
/// </para>
/// </summary>
public sealed class EventNotificationsConfig
{
    /// <summary>All event types that qualify for active injection (recognized names).</summary>
    private static readonly HashSet<EventType> RecognizedActiveEvents = new()
    {
        EventType.GoalCompleted, EventType.GoalFailed, EventType.CiFailed,
        EventType.IssueRaised, EventType.PackagePublished,
        EventType.CiSucceeded, EventType.ReleaseCompleted,
        EventType.GoalDispatched, EventType.IssueResolved
    };

    /// <summary>Event types injected by default when no explicit list is configured.</summary>
    private static readonly HashSet<EventType> DefaultActiveEvents = new()
    { EventType.GoalCompleted, EventType.GoalFailed, EventType.CiFailed, EventType.IssueRaised };

    /// <summary>Mode: "passive" (default), "active", "off". Invalid values fall back to "passive".</summary>
    public string? Mode { get; set; }

    /// <summary>Snake_case event names to inject actively (e.g. "goal_completed"). Null → all whitelisted types.</summary>
    public List<string>? ActiveEvents { get; set; }

    /// <summary>Minimum seconds between active injection attempts. Default 30, clamped to [1, 300].</summary>
    public int? ThrottleSeconds { get; set; }

    /// <summary>Valid modes: "passive" (default), "active", "off". Null/blank/invalid → "passive".</summary>
    [YamlIgnore]
    public string EffectiveMode
    {
        get { var m = Mode?.Trim().ToLowerInvariant(); return m is "passive" or "active" or "off" ? m : "passive"; }
    }

    /// <summary>Throttle window in seconds: <see cref="ThrottleSeconds"/> clamped to [1, 300], default 30.</summary>
    [YamlIgnore]
    public int EffectiveThrottleSeconds => Math.Clamp(ThrottleSeconds ?? 30, 1, 300);

    /// <summary>
    /// Parses snake_case event names (e.g. <c>"goal_completed"</c> → <see cref="EventType.GoalCompleted"/>).
    /// Null/empty/invalid-only input defaults to the four default types. Names that parse but are not
    /// recognized are skipped. Numeric strings and comma-combined values are rejected.
    /// </summary>
    /// <returns>A new <see cref="HashSet{T}"/> of active event types; never a shared reference.</returns>
    public HashSet<EventType> GetActiveEventTypes()
    {
        if (ActiveEvents is null || ActiveEvents.Count == 0)
            return new HashSet<EventType>(DefaultActiveEvents);

        var result = new HashSet<EventType>();
        foreach (var name in ActiveEvents)
        {
            if (name is null) continue;
            if (TryParseSnakeCase(name, out var t) && RecognizedActiveEvents.Contains(t))
                result.Add(t);
        }

        return result.Count == 0 ? new HashSet<EventType>(DefaultActiveEvents) : result;
    }

    /// <summary>
    /// Returns names from <see cref="ActiveEvents"/> that could not be parsed or are not recognized.
    /// Empty if <see cref="ActiveEvents"/> is null. Used by <c>ActiveEventInjector</c> for logging.
    /// </summary>
    public List<string> GetInvalidEventNames()
    {
        var invalid = new List<string>();
        if (ActiveEvents is null) return invalid;
        foreach (var name in ActiveEvents)
        {
            if (name is null) continue;
            if (!TryParseSnakeCase(name, out var t) || !RecognizedActiveEvents.Contains(t))
                invalid.Add(name);
        }
        return invalid;
    }

    /// <summary>
    /// Parses a snake_case or PascalCase event name into an <see cref="EventType"/>.
    /// Rejects numeric strings and comma-combined values that <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/>
    /// would otherwise accept via bitwise OR.
    /// </summary>
    private static bool TryParseSnakeCase(string name, out EventType type)
    {
        type = default;
        if (int.TryParse(name, out _) || name.Contains(','))
            return false;
        // Enum.TryParse does not strip underscores; normalize snake_case → the bare enum name.
        var normalized = name.Replace("_", "", StringComparison.Ordinal);
        return Enum.TryParse<EventType>(normalized, ignoreCase: true, out type);
    }
}

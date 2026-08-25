namespace CopilotHive.Configuration;

/// <summary>
/// Configuration for the Composer conversational agent.
/// </summary>
public sealed class ComposerConfig
{
    /// <summary>Model used by the Composer (e.g. "copilot/claude-sonnet-4.6"). <c>null</c> means
    /// unset (no model configured); blank/whitespace values are normalized to <c>null</c> at
    /// parse time (see <see cref="ConfigRepoManager.ParseConfig"/>).</summary>
    public string? Model { get; set; }
    /// <summary>Maximum tool-call steps per Composer request.</summary>
    public int MaxSteps { get; set; } = Constants.DefaultBrainMaxSteps;

    /// <summary>
    /// Reasoning effort for the Composer <see cref="Model"/> (one of:
    /// none, low, medium, high, extra_high). Required when <see cref="Model"/> is set.
    /// YAML key: <c>reasoning_effort</c>.
    /// </summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// Active event notification configuration. When null, active injection is disabled
    /// (passive mode). Null stays null in YAML — do not initialize with <c>new()</c>,
    /// as that would emit an empty <c>event_notifications: {}</c> key on serialization.
    /// </summary>
    public EventNotificationsConfig? EventNotifications { get; set; }
}

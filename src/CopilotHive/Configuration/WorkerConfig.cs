namespace CopilotHive.Configuration;

/// <summary>
/// Per-role worker configuration.
/// </summary>
public sealed class WorkerConfig
{
    /// <summary>Model override for this worker role; <c>null</c> means use the global default.</summary>
    public string? Model { get; set; }

    /// <summary>Premium model override for this worker role, selected when the Brain requests the 'premium' tier.</summary>
    public string? PremiumModel { get; set; }

    /// <summary>
    /// Context window size in tokens for this worker role. When set and greater than 0,
    /// overrides the orchestrator's <c>worker_context_window</c>.
    /// Used for heartbeat Ctx% calculation and agent compaction threshold.
    /// </summary>
    public int ContextWindow { get; set; }
}

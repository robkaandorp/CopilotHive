namespace CopilotHive.Dashboard;

/// <summary>Information about an active LLM session tracked by the dashboard.</summary>
public sealed record LlmSessionInfo
{
    /// <summary>Unique session identifier.</summary>
    public required string SessionId { get; init; }

    /// <summary>Session classification, e.g. "brain", "composer", or "worker".</summary>
    public required string SessionType { get; init; }

    /// <summary>Model identifier used by the session.</summary>
    public required string Model { get; init; }

    /// <summary>Current session status, e.g. "active", "idle", or "completed".</summary>
    public required string Status { get; init; }

    /// <summary>Identifier of the goal associated with this session, if any.</summary>
    public string? GoalId { get; init; }

    /// <summary>Current number of tokens consumed by the session.</summary>
    public long CurrentTokens { get; init; }

    /// <summary>Maximum number of tokens allowed in the context window.</summary>
    public long MaxTokens { get; init; }

    /// <summary>Timestamp when the session was created. Defaults to UTC now.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Timestamp of the last recorded activity. Defaults to UTC now.</summary>
    public DateTime LastActivity { get; init; } = DateTime.UtcNow;

    /// <summary>Context usage percentage, clamped to 0–100, overflow-safe.</summary>
    public int ContextUsagePercent
    {
        get
        {
            if (MaxTokens <= 0)
                return 0;

            // Use double arithmetic to avoid long overflow, then clamp and cast.
            return (int)Math.Clamp((double)CurrentTokens / MaxTokens * 100.0, 0.0, 100.0);
        }
    }
}

namespace CopilotHive.Worker;

/// <summary>Centralised constants for the CopilotHive Worker process.</summary>
internal static class WorkerConstants
{
    /// <summary>
    /// Maximum allowed character count per *.agents.md file. This is the single source of
    /// truth for the limit: a file of exactly this many characters is allowed, one more
    /// character triggers enforcement. Size is measured as
    /// <c>File.ReadAllText(...).Length</c> — UTF-16 code units, not bytes and not tokens.
    /// </summary>
    public const int AgentsMdMaxCharacters = 8000;

    /// <summary>Maximum retries to condense an over-limit agents.md before discarding changes.</summary>
    public const int AgentsMdMaxRetries = 3;
}

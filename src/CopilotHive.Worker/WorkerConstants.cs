using CopilotHive.Shared;

namespace CopilotHive.Worker;

/// <summary>Centralised constants for the CopilotHive Worker process.</summary>
internal static class WorkerConstants
{
    /// <summary>
    /// Maximum allowed character count per *.agents.md file, aliased to
    /// <see cref="AgentsMdSize.MaxCharacters"/> — the single source of truth shared with the
    /// orchestrator's Composer tools. A file of exactly this many characters is allowed, one more
    /// character triggers enforcement. Size is measured as
    /// <c>File.ReadAllText(...).Length</c> — UTF-16 code units, not bytes and not tokens.
    /// </summary>
    public const int AgentsMdMaxCharacters = AgentsMdSize.MaxCharacters;

    /// <summary>Maximum retries to condense an over-limit agents.md before discarding changes.</summary>
    public const int AgentsMdMaxRetries = 3;
}

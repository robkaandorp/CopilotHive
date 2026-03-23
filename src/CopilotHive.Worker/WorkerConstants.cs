namespace CopilotHive.Worker;

/// <summary>Centralised constants for the CopilotHive Worker process.</summary>
internal static class WorkerConstants
{
    /// <summary>Maximum allowed character count per *.agents.md file.</summary>
    public const int AgentsMdMaxCharacters = 4000;

    /// <summary>Maximum retries to condense an over-limit agents.md before discarding changes.</summary>
    public const int AgentsMdMaxRetries = 3;
}

namespace CopilotHive.Worker;

/// <summary>Centralised constants for the CopilotHive Worker process.</summary>
internal static class WorkerConstants
{
    public const int MaxConnectRetries = 12;
    public const int DefaultAgentPort  = 8000;
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
}

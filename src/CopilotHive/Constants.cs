namespace CopilotHive;

/// <summary>Centralised magic numbers and strings for CopilotHive.</summary>
internal static class Constants
{
    // Retry
    public const int CopilotRunnerMaxRetries    = 12;
    public const int DistributedBrainMaxRetries = 20;
    public const int DefaultMaxRetriesPerTask   = 3;

    // Retry delays
    public const int RetryDelaySeconds      = 5;
    public const int WorkerBootDelaySeconds = 10;
    public const int TaskTimeoutMinutes     = 3;

    // Ports
    public const int DefaultHttpPort  = 5000;
    public const int DefaultAgentPort = 8000;
    public const int DefaultBasePort  = 8001;

    // Truncation lengths
    public const int TruncationShort    = 200;
    public const int TruncationBrief    = 300;
    public const int TruncationMedium   = 500;
    public const int TruncationLong     = 2000;
    public const int TruncationConversationSummary = 2000;
    public const int TruncationVerbose  = 4000;
    public const int TruncationVeryLong = 6000;
    public const int TruncationFull     = 8000;

    // Config defaults
    public const int    DefaultMaxIterations = 10;
    public const string DefaultModel         = "claude-opus-4.6";

    /// <summary>Default model for reviewer role.</summary>
    public const string DefaultReviewerModel    = "gpt-5.3-codex";
    /// <summary>Default model for tester, improver and orchestrator roles.</summary>
    public const string DefaultWorkerModel      = "claude-sonnet-4.6";
}

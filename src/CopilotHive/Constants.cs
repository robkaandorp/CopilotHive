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
    public const int TaskTimeoutMinutes     = 10;

    // Ports
    public const int DefaultHttpPort  = 5000;
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
    public const int    DefaultMaxIterations      = 10;
    public const int    DefaultBrainContextWindow = 150_000;
    public const int    DefaultBrainMaxSteps      = 50;
    public const string DefaultModel              = "claude-opus-4.6";

    // Version
    /// <summary>Current orchestrator version reported to workers on registration.</summary>
    public const string OrchestratorVersion = "1.0.0";

    /// <summary>Default model for reviewer role.</summary>
    public const string DefaultReviewerModel    = "gpt-5.3-codex";
    /// <summary>Default model for tester, improver and orchestrator roles.</summary>
    public const string DefaultWorkerModel      = "claude-sonnet-4.6";
    /// <summary>Default model for doc-writer role (fast, cheap model for documentation).</summary>
    public const string DefaultDocWriterModel   = "claude-haiku-4.5";
    /// <summary>Default premium model for doc-writer role.</summary>
    public const string DefaultPremiumDocWriterModel = "claude-sonnet-4.6";
}

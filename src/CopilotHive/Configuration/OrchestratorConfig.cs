namespace CopilotHive.Configuration;

/// <summary>
/// Orchestrator-level configuration from the config file.
/// </summary>
public sealed class OrchestratorConfig
{
    /// <summary>
    /// Model used by the orchestrator LLM. Defaults to <see cref="Constants.DefaultWorkerModel"/>.
    /// An empty/blank string means "unset" (used by the no-config-repo fallback — see
    /// <see cref="CreateEmptyModelFallback"/>); consumers treat blank as "no effective model".
    /// </summary>
    public string Model { get; set; } = Constants.DefaultWorkerModel;
    /// <summary>Maximum number of goal iterations before giving up.</summary>
    public int MaxIterations { get; set; } = Constants.DefaultMaxIterations;
    /// <summary>Maximum number of retries per individual task.</summary>
    public int MaxRetriesPerTask { get; set; } = Constants.DefaultMaxRetriesPerTask;
    /// <summary>
    /// Maximum number of goals to execute in parallel. Default: 1 (sequential).
    /// Set to a value &gt; 1 to enable concurrent goal execution. When multiple goals
    /// run in parallel, each has its own Brain session forked from the master.
    /// </summary>
    public int MaxParallelGoals { get; set; } = 1;
    /// <summary>When <c>true</c>, enables verbose logging of prompts, worker output, and Brain reasoning.</summary>
    public bool VerboseLogging { get; set; }
    /// <summary>Maximum tool-call steps the Brain agent may take per request.</summary>
    public int BrainMaxSteps { get; set; } = Constants.DefaultBrainMaxSteps;
    /// <summary>
    /// Delay in hours before deleting feature branches for completed goals.
    /// Default: 48 hours. Set to 0 for immediate cleanup.
    /// </summary>
    public int BranchCleanupDelayHours { get; set; } = 48;

    /// <summary>
    /// Maximum wall-clock minutes a single worker task may run before the orchestrator
    /// reclaims it and re-dispatches the phase. Guards against workers that keep
    /// heartbeating while their LLM call hangs. Set to 0 to disable.
    /// </summary>
    public int WorkerTaskTimeoutMinutes { get; set; } = Services.CleanupDefaults.WorkerTaskTimeoutMinutes;

    /// <summary>
    /// Reasoning effort for the orchestrator <see cref="Model"/> (one of:
    /// none, low, medium, high, extra_high). Always required because
    /// <see cref="Model"/> always has a value. YAML key: <c>reasoning_effort</c>.
    /// </summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// Creates an orchestrator config with an EMPTY <see cref="Model"/> (empty string,
    /// never <see cref="Constants.DefaultWorkerModel"/>). This is the fallback used by
    /// <c>Program.cs</c> for the no-config-repo <see cref="HiveConfigFile"/> singleton:
    /// both the Brain factory and the Composer factory treat an empty/blank model as
    /// "unset", so the fallback never overrides <c>BRAIN_MODEL</c> for the Brain nor
    /// alters the Composer's model chain.
    /// </summary>
    public static OrchestratorConfig CreateEmptyModelFallback() => new() { Model = string.Empty };
}

namespace CopilotHive.Configuration;

/// <summary>
/// Orchestrator-level configuration from the config file.
/// </summary>
public sealed class OrchestratorConfig
{
    /// <summary>
    /// Model used by the orchestrator LLM. <c>null</c> means "unset" (no model configured);
    /// blank/whitespace values are normalized to <c>null</c> at parse time (see
    /// <see cref="ConfigRepoManager.ParseConfig"/>). The no-config-repo fallback also uses
    /// <c>null</c> (see <see cref="CreateEmptyModelFallback"/>).
    /// <para>
    /// CopilotHive requires a Brain, so a <c>null</c> model is NOT a "no Brain" operating mode:
    /// startup fails (see <c>Program.RequireBrainModel</c>) unless a Brain is supplied explicitly
    /// — which is what test hosts do.
    /// </para>
    /// </summary>
    public string? Model { get; set; }
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
    /// none, low, medium, high, extra_high). Required only when <see cref="Model"/>
    /// is set (non-null); an unset model is its own unconfigured state and does not
    /// require a reasoning effort. YAML key: <c>reasoning_effort</c>.
    /// </summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// Creates an orchestrator config with an UNSET <see cref="Model"/> (<c>null</c>,
    /// never <see cref="Constants.DefaultWorkerModel"/>). This is the fallback used by
    /// <c>Program.cs</c> for the no-config-repo <see cref="HiveConfigFile"/> singleton.
    /// <para>
    /// A null model is NOT a no-Brain operating mode: CopilotHive requires a Brain, so startup
    /// fails with the Brain startup-contract error unless a Brain is supplied explicitly — which
    /// is exactly what test hosts do by registering their own <c>IDistributedBrain</c>. The
    /// Composer stays a disconnected, resolver-only shell either way.
    /// </para>
    /// </summary>
    public static OrchestratorConfig CreateEmptyModelFallback() => new() { Model = null };
}

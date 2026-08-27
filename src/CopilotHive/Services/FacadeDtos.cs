using CopilotHive.Configuration;
using CopilotHive.Goals;

using Microsoft.Extensions.AI;

using System.Text.Json.Serialization;

namespace CopilotHive.Services;

/// <summary>
/// Request body for adding or updating an available model.
/// </summary>
/// <param name="Name">Model name (used for add; ignored for update where the route name is authoritative).</param>
/// <param name="ContextWindow">Optional context window in tokens.</param>
/// <param name="Description">Optional human-readable description.</param>
/// <param name="SupportsVision">Informational vision flag: <c>true</c>, <c>false</c>, or <c>null</c> for unset.</param>
public sealed record AvailableModelRequest(string Name, int? ContextWindow, string? Description = null, bool? SupportsVision = null);

/// <summary>
/// Request body for adding or updating a sub-agent model.
/// </summary>
/// <param name="Name">Model name (used for add; ignored for update where the route name is authoritative).</param>
/// <param name="ContextWindow">Optional context window in tokens.</param>
/// <param name="ReasoningEffort">
/// Optional default reasoning effort. Wire values are snake_case (<c>none</c>, <c>low</c>,
/// <c>medium</c>, <c>high</c>, <c>extra_high</c>); an unknown value is rejected with a 400 by
/// the global JSON enum converter.
/// </param>
/// <param name="Description">Optional human-readable description.</param>
/// <param name="SupportsVision">Informational vision flag: <c>true</c>, <c>false</c>, or <c>null</c> for unset (inherit).</param>
public sealed record SubAgentModelRequest(string Name, int? ContextWindow, ReasoningEffort? ReasoningEffort, string? Description = null, bool? SupportsVision = null);

/// <summary>
/// Describes a batch of model configuration changes to apply atomically.
/// </summary>
/// <param name="OrchestratorModel">New orchestrator model, or <c>null</c> to leave unchanged.</param>
/// <param name="ComposerModel">New Composer model, or <c>null</c> to leave unchanged.</param>
/// <param name="WorkerModels">Per-role model overrides, keyed by role name. <c>null</c> to leave unchanged.</param>
/// <param name="PremiumWorkerModels">Per-role premium model overrides, keyed by role name. <c>null</c> to leave unchanged.</param>
/// <param name="CompactionModel">New compaction model, or <c>null</c> to leave unchanged.</param>
/// <param name="OrchestratorReasoningEffort">
/// New orchestrator reasoning effort. <c>null</c> leaves the persisted value unchanged.
/// <see cref="ReasoningEffort.None"/> persists the explicit <c>none</c> level. The running Brain
/// retains its reasoning until restart (UpdateModelAsync null = retain).
/// </param>
/// <param name="ComposerReasoningEffort">
/// New Composer reasoning effort. <c>null</c> leaves the persisted value unchanged.
/// Persistence-only. The running Composer picks up on restart.
/// </param>
/// <param name="WorkerReasoningEffort">
/// Per-role reasoning effort keyed by role name (case-insensitive). <c>null</c> leaves everything
/// unchanged. A present key with a <c>null</c> value is a no-op for that role. Unknown role keys
/// are ignored entirely.
/// </param>
/// <param name="WorkerPremiumReasoningEffort">
/// Per-role premium reasoning effort keyed by role name (case-insensitive). Same semantics as
/// <paramref name="WorkerReasoningEffort"/>, mapped to <see cref="WorkerConfig.PremiumReasoningEffort"/>.
/// </param>
/// <param name="SubAgentModelReasoning">
/// Per-sub-agent-model reasoning effort keyed by model name (case-insensitive). Only model names
/// present in the current <c>sub_agent_models</c> list are applied; unknown keys are ignored entirely.
/// </param>
public sealed record ModelConfigUpdate(
    string? OrchestratorModel,
    string? ComposerModel,
    Dictionary<string, string>? WorkerModels,
    Dictionary<string, string>? PremiumWorkerModels,
    string? CompactionModel,
    ReasoningEffort? OrchestratorReasoningEffort = null,
    ReasoningEffort? ComposerReasoningEffort = null,
    Dictionary<string, ReasoningEffort?>? WorkerReasoningEffort = null,
    Dictionary<string, ReasoningEffort?>? WorkerPremiumReasoningEffort = null,
    Dictionary<string, ReasoningEffort?>? SubAgentModelReasoning = null)
{
    private static string Show(ReasoningEffort? value)
        => ReasoningEffortConverter.Format(value) ?? "(unchanged)";

    /// <summary>
    /// Human-readable summary of the changes in this update (used as the git commit message body).
    /// </summary>
    public string Description => string.Join(", ", new[]
    {
        OrchestratorModel is not null ? $"orchestrator→{OrchestratorModel}" : null,
        ComposerModel      is not null ? $"composer→{ComposerModel}" : null,
        CompactionModel    is not null ? $"compaction→{CompactionModel}" : null,
        WorkerModels?.Count > 0
            ? "workers: " + string.Join(", ", WorkerModels.Select(kv => $"{kv.Key}→{kv.Value}"))
            : null,
        PremiumWorkerModels?.Count > 0
            ? "premium: " + string.Join(", ", PremiumWorkerModels.Select(kv => $"{kv.Key}→{kv.Value}"))
            : null,
        OrchestratorReasoningEffort is not null ? $"orchestrator reasoning→{Show(OrchestratorReasoningEffort)}" : null,
        ComposerReasoningEffort     is not null ? $"composer reasoning→{Show(ComposerReasoningEffort)}" : null,
        WorkerReasoningEffort?.Count > 0
            ? "worker reasoning: " + string.Join(", ", WorkerReasoningEffort.Select(kv => $"{kv.Key}→{Show(kv.Value)}"))
            : null,
        WorkerPremiumReasoningEffort?.Count > 0
            ? "premium reasoning: " + string.Join(", ", WorkerPremiumReasoningEffort.Select(kv => $"{kv.Key}→{Show(kv.Value)}"))
            : null,
        SubAgentModelReasoning?.Count > 0
            ? "sub-agent reasoning: " + string.Join(", ", SubAgentModelReasoning.Select(kv => $"{kv.Key}→{Show(kv.Value)}"))
            : null,
    }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>
/// Describes a batch of orchestrator-level setting changes to apply. Each field is
/// applied only when non-null, leaving the existing value unchanged otherwise.
/// </summary>
/// <param name="MaxIterations">New maximum number of goal iterations.</param>
/// <param name="MaxRetriesPerTask">New maximum retries per task.</param>
/// <param name="MaxParallelGoals">New maximum number of parallel goals.</param>
/// <param name="VerboseLogging">Whether verbose logging is enabled.</param>
/// <param name="BrainMaxSteps">New maximum Brain tool-call steps.</param>
/// <param name="BranchCleanupDelayHours">New branch cleanup delay in hours.</param>
public sealed record OrchestratorSettingsUpdate(
    int? MaxIterations, int? MaxRetriesPerTask, int? MaxParallelGoals,
    bool? VerboseLogging,
    int? BrainMaxSteps,
    int? BranchCleanupDelayHours);

/// <summary>
/// Request body for adding or updating a repository.
/// </summary>
/// <param name="Name">Short name used to identify the repository.</param>
/// <param name="Url">Remote clone URL of the repository.</param>
/// <param name="DefaultBranch">Default branch to use (e.g. "main").</param>
/// <param name="Release">Optional release automation configuration.</param>
/// <param name="MonitorCi">Whether CI monitoring is enabled for this repository.</param>
/// <param name="CiTimeoutMinutes">Timeout in minutes before a CI run is considered failed.</param>
public sealed record RepositoryRequest(string Name, string Url, string DefaultBranch, ReleaseRepoConfig? Release = null, bool? MonitorCi = null, int? CiTimeoutMinutes = null);

/// <summary>
/// Describes Composer setting changes to apply. Each field is applied only when non-null.
/// </summary>
/// <param name="MaxSteps">New maximum Composer tool-call steps.</param>
/// <param name="EventNotificationsMode">New event notification mode ("passive", "active", "off").</param>
/// <param name="EventNotificationsActiveEvents">New active event names (snake_case or PascalCase, case-insensitive).</param>
/// <param name="EventNotificationsThrottleSeconds">New throttle window in seconds (clamped to 1-300).</param>
public sealed record ComposerSettingsUpdate(
    int? MaxSteps = null,
    string? EventNotificationsMode = null,
    List<string>? EventNotificationsActiveEvents = null,
    int? EventNotificationsThrottleSeconds = null);

/// <summary>
/// Shared response DTO for save-style operations. <c>Saved</c> is always serialized;
/// <c>Description</c> is omitted from the JSON when <c>null</c>.
/// </summary>
/// <param name="Saved">Whether the operation persisted successfully.</param>
/// <param name="Description">Optional human-readable description of what was saved.</param>
public sealed record SavedResult(bool Saved, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description);

/// <summary>
/// Shared response DTO for remove-style operations.
/// </summary>
/// <param name="Removed">Whether the resource was removed (<c>false</c> when not found).</param>
public sealed record RemovedResult(bool Removed);

/// <summary>
/// Response DTO for an entry in the <c>available_models</c> list. Deliberately carries no
/// <c>reasoningEffort</c> property — the endpoint contract never exposes one.
/// </summary>
/// <param name="Name">Model name.</param>
/// <param name="ContextWindow">Optional context window in tokens.</param>
/// <param name="Description">Optional human-readable description.</param>
/// <param name="SupportsVision">Informational vision flag: <c>true</c>, <c>false</c>, or <c>null</c> for unset.</param>
public sealed record AvailableModelDto(string Name, int? ContextWindow, string? Description, bool? SupportsVision);

/// <summary>
/// Response DTO for an entry in the <c>sub_agent_models</c> list. <c>ReasoningEffort</c> is the
/// strongly typed enum; the global snake_case enum converter renders it on the wire
/// (e.g. <c>"extra_high"</c>).
/// </summary>
/// <param name="Name">Model name.</param>
/// <param name="ContextWindow">Optional context window in tokens.</param>
/// <param name="ReasoningEffort">Optional default reasoning effort.</param>
/// <param name="Description">Optional human-readable description.</param>
/// <param name="SupportsVision">Informational vision flag: <c>true</c>, <c>false</c>, or <c>null</c> for unset (inherit).</param>
public sealed record ConfigSubAgentModelDto(string Name, int? ContextWindow, Microsoft.Extensions.AI.ReasoningEffort? ReasoningEffort, string? Description, bool? SupportsVision);

/// <summary>
/// Response DTO for a worker role's model configuration. Deliberately carries no
/// <c>contextWindow</c> property — the endpoint contract exposes it separately.
/// </summary>
/// <param name="Model">Standard model for the role, or <c>null</c> when unset.</param>
/// <param name="PremiumModel">Premium model for the role, or <c>null</c> when unset.</param>
public sealed record WorkerModelsDto(string? Model, string? PremiumModel);

/// <summary>
/// Response DTO for GET /api/config/models — the complete model configuration surface.
/// Reasoning efforts are projected entry-by-entry through
/// <see cref="ConfigModelService.ParseLenient"/> so an unrecognised stored value degrades
/// to <c>null</c> rather than leaking a raw string onto the enum-typed wire.
/// </summary>
/// <param name="Orchestrator">Orchestrator model identifier, or <c>null</c> when unset.</param>
/// <param name="Composer">Composer model identifier, or <c>null</c> when unset.</param>
/// <param name="Compaction">Compaction model identifier, or <c>null</c> when unset.</param>
/// <param name="Workers">Per-role worker model configuration (standard + premium model).</param>
/// <param name="OrchestratorReasoningEffort">Orchestrator reasoning effort, or <c>null</c> when unset/invalid.</param>
/// <param name="ComposerReasoningEffort">Composer reasoning effort, or <c>null</c> when unset/invalid.</param>
/// <param name="WorkerReasoningEffort">Per-role worker reasoning effort, or <c>null</c> per role when unset/invalid.</param>
/// <param name="WorkerPremiumReasoningEffort">Per-role worker premium reasoning effort, or <c>null</c> per role when unset/invalid.</param>
/// <param name="SubAgentModelReasoning">Per-model sub-agent reasoning effort keyed by model name, or <c>null</c> when no sub-agent models are configured.</param>
/// <param name="AvailableModels">The global available-models catalog, or <c>null</c> when not configured.</param>
/// <param name="SubAgentModels">The curated sub-agent model list, or <c>null</c> when not configured.</param>
public sealed record ModelsConfigDto(
    string? Orchestrator,
    string? Composer,
    string? Compaction,
    IReadOnlyDictionary<string, WorkerModelsDto> Workers,
    ReasoningEffort? OrchestratorReasoningEffort,
    ReasoningEffort? ComposerReasoningEffort,
    IReadOnlyDictionary<string, ReasoningEffort?> WorkerReasoningEffort,
    IReadOnlyDictionary<string, ReasoningEffort?> WorkerPremiumReasoningEffort,
    IReadOnlyDictionary<string, ReasoningEffort?>? SubAgentModelReasoning,
    IReadOnlyList<AvailableModelDto>? AvailableModels,
    IReadOnlyList<ConfigSubAgentModelDto>? SubAgentModels);

/// <summary>
/// Response DTO for a model discovered from a provider API
/// (GET /api/config/models/discover). Mirrors <see cref="DiscoveredModel"/> field-for-field.
/// </summary>
/// <param name="Id">Provider-prefixed identifier (e.g. "copilot/claude-sonnet-4.6").</param>
/// <param name="Name">Human-readable display name.</param>
/// <param name="Vendor">Vendor name, or <c>null</c> if not reported.</param>
/// <param name="ContextWindow">Maximum context window in tokens, or <c>null</c> if not reported.</param>
/// <param name="Enabled">Whether the model is enabled by provider policy.</param>
public sealed record DiscoveredModelDto(string Id, string Name, string? Vendor, int? ContextWindow, bool Enabled);

/// <summary>
/// Response DTO for a configured repository.
/// </summary>
/// <param name="Name">Short name used to identify the repository.</param>
/// <param name="Url">Remote clone URL of the repository.</param>
/// <param name="DefaultBranch">Default branch to use (e.g. "main").</param>
/// <param name="MonitorCi">Whether CI monitoring is enabled for this repository.</param>
/// <param name="CiTimeoutMinutes">Timeout in minutes before a CI run is considered failed.</param>
/// <param name="Release">Optional release automation configuration.</param>
/// <param name="PublishNuGet">Optional NuGet publish monitoring configuration.</param>
public sealed record RepositoryDto(
    string Name,
    string Url,
    string DefaultBranch,
    bool MonitorCi,
    int CiTimeoutMinutes,
    RepositoryReleaseDto? Release,
    RepositoryPublishNuGetDto? PublishNuGet);

/// <summary>
/// Response DTO for a repository's release automation configuration.
/// </summary>
/// <param name="MergeTo">Branch to merge the feature branch into (e.g. "main").</param>
/// <param name="TagBranch">Branch to tag releases from (e.g. "main").</param>
public sealed record RepositoryReleaseDto(string? MergeTo, string? TagBranch);

/// <summary>
/// Response DTO for a repository's NuGet publish monitoring configuration.
/// </summary>
/// <param name="Packages">Packages to watch for publish events.</param>
public sealed record RepositoryPublishNuGetDto(IReadOnlyList<RepositoryPackageDto> Packages);

/// <summary>
/// Response DTO for a single NuGet package watched for publish events.
/// </summary>
/// <param name="PackageId">NuGet package ID (e.g. "My.Library").</param>
public sealed record RepositoryPackageDto(string PackageId);

/// <summary>
/// Response DTO for GET /api/config/workers — a TOP-LEVEL dictionary keyed by worker role.
/// Derives from <see cref="Dictionary{TKey,TValue}"/> so the JSON output is the same
/// role-keyed object the pre-facade endpoint produced; a conventional wrapper record would
/// change the wire shape.
/// </summary>
public sealed class WorkersConfigDto : Dictionary<string, WorkerEntryDto>
{
    /// <summary>Creates an empty workers dictionary.</summary>
    public WorkersConfigDto()
    {
    }
}

/// <summary>
/// Response DTO for a single worker role's settings in GET /api/config/workers.
/// </summary>
/// <param name="Model">Standard model for the role, or <c>null</c> when unset.</param>
/// <param name="PremiumModel">Premium model for the role, or <c>null</c> when unset.</param>
/// <param name="ContextWindow">Context window in tokens (0 when unset).</param>
public sealed record WorkerEntryDto(string? Model, string? PremiumModel, int? ContextWindow);

/// <summary>
/// Response DTO for GET /api/config/orchestrator. Mirrors the pre-facade projection — the
/// endpoint serialized the raw <see cref="OrchestratorConfig"/> object, so every property is
/// present with the same name and order.
/// </summary>
/// <param name="Model">Orchestrator model identifier, or <c>null</c> when unset.</param>
/// <param name="MaxIterations">Maximum number of goal iterations before giving up.</param>
/// <param name="MaxRetriesPerTask">Maximum number of retries per individual task.</param>
/// <param name="MaxParallelGoals">Maximum number of goals to execute in parallel.</param>
/// <param name="VerboseLogging">Whether verbose logging is enabled.</param>
/// <param name="BrainMaxSteps">Maximum tool-call steps the Brain agent may take per request.</param>
/// <param name="BranchCleanupDelayHours">Delay in hours before deleting feature branches for completed goals.</param>
/// <param name="WorkerTaskTimeoutMinutes">Maximum wall-clock minutes a single worker task may run.</param>
/// <param name="ReasoningEffort">Orchestrator reasoning effort, or <c>null</c> when unset.</param>
public sealed record OrchestratorConfigDto(
    string? Model,
    int MaxIterations,
    int MaxRetriesPerTask,
    int MaxParallelGoals,
    bool VerboseLogging,
    int BrainMaxSteps,
    int BranchCleanupDelayHours,
    int WorkerTaskTimeoutMinutes,
    string? ReasoningEffort);

/// <summary>
/// Response DTO for GET /api/config/composer — the runtime-effective Composer settings
/// projection (model, max steps, reasoning effort, and the typed event-notifications shape).
/// </summary>
/// <param name="Model">Composer model identifier, or <c>null</c> when unset.</param>
/// <param name="MaxSteps">Maximum tool-call steps per Composer request.</param>
/// <param name="ReasoningEffort">Composer reasoning effort, or <c>null</c> when unset.</param>
/// <param name="EventNotifications">The effective event-notification configuration.</param>
public sealed record ComposerConfigDto(
    string? Model,
    int MaxSteps,
    string? ReasoningEffort,
    ComposerEventNotificationsDto EventNotifications);

/// <summary>
/// Response DTO for the <c>eventNotifications</c> sub-object of GET /api/config/composer.
/// </summary>
/// <param name="Mode">Effective notification mode ("passive", "active", "off").</param>
/// <param name="ActiveEvents">Active event names in canonical whitelist order.</param>
/// <param name="ValidActiveEvents">All recognized active event names in canonical order.</param>
/// <param name="ThrottleSeconds">Effective minimum seconds between active injection attempts.</param>
public sealed record ComposerEventNotificationsDto(
    string Mode,
    IReadOnlyList<string> ActiveEvents,
    IReadOnlyList<string> ValidActiveEvents,
    int ThrottleSeconds);
/// <summary>
/// Response DTO describing a single backup archive. Used by BOTH <c>GET /api/backup</c>
/// (each list entry) and <c>POST /api/backup</c> (the newly created archive), because the
/// create endpoint returns the same shape as a list entry. The property names mirror
/// <c>BackupService.BackupInfo</c> exactly, so the wire shape is unchanged:
/// <c>fileName</c>, <c>sizeBytes</c>, <c>createdAt</c>.
/// </summary>
/// <param name="FileName">The archive file name (e.g. <c>copilothive-backup-20240101T000000.tar.gz</c>).</param>
/// <param name="SizeBytes">The archive size in bytes.</param>
/// <param name="CreatedAt">The UTC creation time of the archive.</param>
public sealed record BackupInfoDto(string FileName, long SizeBytes, DateTime CreatedAt);

/// <summary>
/// Response DTO for <c>GET /api/composer/current-model</c>. The model is NULLABLE by contract:
/// a Composer that is not connected / has no active model reports <c>{"model":null}</c> on a
/// SUCCESSFUL read — a catalog entry is never fabricated in its place.
/// </summary>
/// <param name="Model">The Composer's active model, or <c>null</c> when it has none.</param>
public sealed record CurrentModelDto(string? Model);

/// <summary>
/// Response DTO for <c>GET /api/composer/models</c>: the Composer's normalised model catalog
/// (<c>Composer.AvailableModels</c> — trimmed and deduplicated) plus its current reasoning effort.
/// </summary>
/// <param name="Models">The available models, in catalog order.</param>
/// <param name="ReasoningEffort">The Composer's current reasoning effort, or <c>null</c> when unset.</param>
public sealed record ComposerModelsDto(IReadOnlyList<string> Models, ReasoningEffort? ReasoningEffort);

/// <summary>
/// Response DTO for <c>POST /api/composer/models/switch</c> — the model and reasoning effort
/// actually applied to the running Composer.
/// </summary>
/// <param name="Model">The model now active.</param>
/// <param name="ReasoningEffort">The reasoning effort now active, or <c>null</c> when unset.</param>
public sealed record SwitchResultDto(string Model, ReasoningEffort? ReasoningEffort);

/// <summary>
/// Response DTO for <c>POST /api/composer/compact</c> and <c>POST /api/composer/compact-partial</c>.
/// </summary>
/// <param name="Compacted">Whether compaction actually ran.</param>
/// <param name="MessageCount">The session message count after the attempt (0 when unknown).</param>
public sealed record CompactResultDto(bool Compacted, int MessageCount);

/// <summary>
/// Response DTO for the goal routes that serialize a whole goal
/// (<c>PATCH /api/goals/{id}/status</c> and <c>PATCH /api/goals/{id}/release</c>).
/// </summary>
/// <remarks>
/// The pre-facade handlers returned the raw <see cref="Goal"/> entity, so EVERY property of
/// <see cref="Goal"/> is reproduced here — in declaration order — and nothing is truncated or
/// projected away. <see cref="IterationSummaries"/> deliberately reuses the domain
/// <see cref="IterationSummary"/> type so the nested wire shape stays byte-identical rather
/// than being re-modelled (and drifting) here.
/// </remarks>
/// <param name="Id">Unique identifier for this goal.</param>
/// <param name="Description">Human-readable description of what the goal requires.</param>
/// <param name="Priority">Scheduling priority; higher-priority goals are dispatched first.</param>
/// <param name="Scope">Scope of change this goal introduces.</param>
/// <param name="Status">Current lifecycle status of the goal.</param>
/// <param name="RepositoryNames">Names of repositories this goal applies to.</param>
/// <param name="TargetRepositoryNames">Comma-separated editable target repositories, or <c>null</c> for all.</param>
/// <param name="DependsOn">IDs of goals that must complete before this goal can be dispatched.</param>
/// <param name="Metadata">Arbitrary key/value metadata associated with the goal.</param>
/// <param name="CreatedAt">UTC timestamp when the goal was created.</param>
/// <param name="StartedAt">UTC timestamp when the goal was picked up, or <c>null</c>.</param>
/// <param name="CompletedAt">UTC timestamp when the goal finished, or <c>null</c>.</param>
/// <param name="Iterations">Number of iterations used, or <c>null</c> if not yet finished.</param>
/// <param name="FailureReason">Reason the goal failed, or <c>null</c>.</param>
/// <param name="Notes">Optional informational notes.</param>
/// <param name="PhaseDurations">Per-phase wall-clock durations in seconds.</param>
/// <param name="IterationSummaries">Structured summaries written after each iteration completes.</param>
/// <param name="TotalDurationSeconds">Total wall-clock duration of the goal in seconds.</param>
/// <param name="MergeCommitHash">SHA-1 of the merge commit that landed this goal, or <c>null</c>.</param>
/// <param name="ReleaseId">Release this goal is grouped into, or <c>null</c> if unassigned.</param>
/// <param name="Documents">IDs of knowledge documents related to this goal.</param>
/// <param name="BranchCleanedUp">Whether the feature branch has been cleaned up after completion.</param>
/// <param name="ReviewStatus">Pre-execution review status.</param>
public sealed record GoalDto(
    string Id,
    string Description,
    GoalPriority Priority,
    GoalScope Scope,
    GoalStatus Status,
    List<string> RepositoryNames,
    string? TargetRepositoryNames,
    List<string> DependsOn,
    Dictionary<string, string> Metadata,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    int? Iterations,
    string? FailureReason,
    List<string> Notes,
    Dictionary<string, double>? PhaseDurations,
    List<IterationSummary> IterationSummaries,
    double? TotalDurationSeconds,
    string? MergeCommitHash,
    string? ReleaseId,
    List<string> Documents,
    bool BranchCleanedUp,
    ReviewStatus ReviewStatus)
{
    /// <summary>
    /// Projects a <see cref="Goal"/> entity onto its wire representation, property for property.
    /// </summary>
    /// <param name="goal">The goal entity to project.</param>
    /// <returns>The DTO the goal routes serialize.</returns>
    public static GoalDto From(Goal goal) => new(
        goal.Id,
        goal.Description,
        goal.Priority,
        goal.Scope,
        goal.Status,
        goal.RepositoryNames,
        goal.TargetRepositoryNames,
        goal.DependsOn,
        goal.Metadata,
        goal.CreatedAt,
        goal.StartedAt,
        goal.CompletedAt,
        goal.Iterations,
        goal.FailureReason,
        goal.Notes,
        goal.PhaseDurations,
        goal.IterationSummaries,
        goal.TotalDurationSeconds,
        goal.MergeCommitHash,
        goal.ReleaseId,
        goal.Documents,
        goal.BranchCleanedUp,
        goal.ReviewStatus);
}

/// <summary>
/// Response DTO for an issue linked to a goal (via <c>SourceGoalId</c> or <c>LinkedGoalId</c>).
/// Mirrors the FULL issue shape the <c>GET /api/issues</c> route produces —
/// field for field, in the same order — so a component consuming the facade sees exactly what
/// the HTTP route returns.
/// </summary>
/// <param name="Id">Unique kebab-case identifier for the issue.</param>
/// <param name="Type">Category of the issue.</param>
/// <param name="Title">Short summary of the issue.</param>
/// <param name="Description">Detailed markdown description of the issue.</param>
/// <param name="Severity">Severity of the issue.</param>
/// <param name="Status">Current lifecycle status of the issue.</param>
/// <param name="RepositoryNames">Names of repositories this issue applies to.</param>
/// <param name="SourceGoalId">ID of the goal that produced this issue, or <c>null</c> if user-reported.</param>
/// <param name="SourceRole">Role that produced this issue, or <c>null</c> if user-reported.</param>
/// <param name="SourceIteration">Iteration number in which the issue was produced, or <c>null</c>.</param>
/// <param name="CreatedAt">UTC timestamp when the issue was created.</param>
/// <param name="UpdatedAt">UTC timestamp of the last update, or <c>null</c> if never updated.</param>
/// <param name="ResolvedAt">UTC timestamp when the issue was resolved or closed, or <c>null</c>.</param>
/// <param name="LinkedGoalId">ID of a goal linked to this issue, or <c>null</c> if none.</param>
public sealed record LinkedIssueDto(
    string Id,
    IssueType Type,
    string Title,
    string Description,
    IssueSeverity Severity,
    IssueStatus Status,
    List<string> RepositoryNames,
    string? SourceGoalId,
    string? SourceRole,
    int? SourceIteration,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    DateTime? ResolvedAt,
    string? LinkedGoalId)
{
    /// <summary>
    /// Projects an <see cref="Issue"/> entity onto its wire representation.
    /// </summary>
    /// <param name="issue">The issue entity to project.</param>
    /// <returns>The DTO mirroring the issues API response shape.</returns>
    public static LinkedIssueDto From(Issue issue) => new(
        issue.Id,
        issue.Type,
        issue.Title,
        issue.Description,
        issue.Severity,
        issue.Status,
        issue.RepositoryNames,
        issue.SourceGoalId,
        issue.SourceRole,
        issue.SourceIteration,
        issue.CreatedAt,
        issue.UpdatedAt,
        issue.ResolvedAt,
        issue.LinkedGoalId);
}

/// <summary>
/// Filter criteria for listing issues via <see cref="IIssueFacade.GetIssuesAsync"/>. The status,
/// type and severity values are RAW strings — the facade reproduces the endpoint's parsing and
/// its exact error messages. The snake_case query parameter names (<c>source_goal_id</c>,
/// <c>linked_goal_id</c>) are the ENDPOINT's concern and stay unchanged; the facade receives the
/// already-bound values.
/// </summary>
/// <param name="Status">Raw status filter value, or <c>null</c> for no filter.</param>
/// <param name="Type">Raw type filter value, or <c>null</c> for no filter.</param>
/// <param name="Severity">Raw severity filter value, or <c>null</c> for no filter.</param>
/// <param name="Repository">Repository name filter (case-insensitive), or <c>null</c> for no filter.</param>
/// <param name="SourceGoalId">Source goal ID filter, or <c>null</c> for no filter.</param>
/// <param name="LinkedGoalId">Linked goal ID filter, or <c>null</c> for no filter.</param>
public sealed record IssueFilter(
    string? Status = null,
    string? Type = null,
    string? Severity = null,
    string? Repository = null,
    string? SourceGoalId = null,
    string? LinkedGoalId = null);

/// <summary>
/// Response DTO for <c>POST /api/goals/{goalId}/review</c>. Mirrors
/// <see cref="ReviewResult"/> exactly — the pre-facade handler serialized that record directly.
/// </summary>
/// <param name="Verdict">Either "Approved" or "NeedsChanges".</param>
/// <param name="Issues">Human-readable summary of issues found, or a "no issues" message.</param>
/// <param name="Summary">Recommendation / summary of what should change.</param>
public sealed record ReviewResultDto(string Verdict, string Issues, string Summary);

/// <summary>
/// Response DTO for <c>POST /api/goals/{id}/cancel</c>, reproducing the pre-facade anonymous
/// body <c>{ message }</c>.
/// </summary>
/// <param name="Message">Confirmation message (e.g. <c>Goal 'x' has been cancelled.</c>).</param>
public sealed record CancelledResult(string Message);

/// <summary>
/// Response DTO for <c>POST /api/goals/{id}/extend-iterations</c>, reproducing the pre-facade
/// anonymous body <c>{ message }</c>.
/// </summary>
/// <param name="Message">Confirmation message (e.g. <c>Extended iteration budget by 5.</c>).</param>
public sealed record ExtendedResult(string Message);

/// <summary>
/// Response DTO for a release, mirroring the wire shape of the <see cref="Release"/> entity the
/// pre-facade release endpoints serialized directly — property for property, in the same order.
/// <see cref="ReleaseStatus"/> and <see cref="ReleaseExecutionState"/> are the ENUM types; the
/// global snake_case enum converter renders them on the wire exactly as before
/// (e.g. <c>"planning"</c>, <c>"executing"</c>).
/// </summary>
/// <param name="Id">Unique identifier for this release (e.g. "v1.2.0").</param>
/// <param name="Tag">Human-readable tag or version label.</param>
/// <param name="Status">Current lifecycle status of the release.</param>
/// <param name="Notes">Optional notes or changelog summary for this release.</param>
/// <param name="CreatedAt">UTC timestamp when the release was created.</param>
/// <param name="ReleasedAt">UTC timestamp when the release was published, or <c>null</c>.</param>
/// <param name="RepositoryNames">Names of repositories this release applies to.</param>
/// <param name="ExecutionState">Execution status for release automation.</param>
public sealed record ReleaseDto(
    string Id,
    string Tag,
    ReleaseStatus Status,
    string? Notes,
    DateTime CreatedAt,
    DateTime? ReleasedAt,
    IReadOnlyList<string> RepositoryNames,
    ReleaseExecutionState ExecutionState)
{
    /// <summary>Projects a <see cref="Release"/> entity onto its wire representation.</summary>
    /// <param name="release">The release entity to project.</param>
    /// <returns>The DTO the release routes serialize.</returns>
    public static ReleaseDto From(Release release) => new(
        release.Id,
        release.Tag,
        release.Status,
        release.Notes,
        release.CreatedAt,
        release.ReleasedAt,
        release.RepositoryNames,
        release.ExecutionState);
}

/// <summary>
/// Response DTO for a single repository's release execution result, mirroring
/// <see cref="RepoReleaseResult"/> field for field so the 500 <c>{detail, results}</c> body and
/// the 200 <c>{release, result}</c> body keep their exact wire shape.
/// </summary>
/// <param name="RepoName">The repository name.</param>
/// <param name="Skipped">Whether the repository was skipped (no release config).</param>
/// <param name="Success">Whether the merge and tag operations succeeded.</param>
/// <param name="MergedTo">The branch that was merged into, or <c>null</c>.</param>
/// <param name="MergeSha">The resulting merge commit SHA, or <c>null</c> for a no-op merge.</param>
/// <param name="TaggedBranch">The branch that was tagged, or <c>null</c>.</param>
/// <param name="TagCreated">Whether a new tag was created (false = tag already existed).</param>
/// <param name="Error">An error message when the repository failed, or <c>null</c>.</param>
public sealed record RepoReleaseResultDto(
    string RepoName,
    bool Skipped,
    bool Success,
    string? MergedTo,
    string? MergeSha,
    string? TaggedBranch,
    bool TagCreated,
    string? Error)
{
    /// <summary>Projects a <see cref="RepoReleaseResult"/> onto its wire representation.</summary>
    /// <param name="result">The execution result to project.</param>
    /// <returns>The DTO the release routes serialize.</returns>
    public static RepoReleaseResultDto From(RepoReleaseResult result) => new(
        result.RepoName,
        result.Skipped,
        result.Success,
        result.MergedTo,
        result.MergeSha,
        result.TaggedBranch,
        result.TagCreated,
        result.Error);
}

/// <summary>
/// Response DTO for a full release execution, mirroring <see cref="ReleaseExecutionResult"/>.
/// <see cref="Failure"/> is the <see cref="ReleaseExecutionFailure"/> ENUM (non-nullable) — the
/// global snake_case converter preserves its <c>"none"</c> wire form on success exactly as the
/// pre-facade endpoint serialized it.
/// </summary>
/// <param name="Success">Whether the whole release executed successfully.</param>
/// <param name="Results">Per-repository execution results.</param>
/// <param name="Error">A human-readable error message, or <c>null</c> on success.</param>
/// <param name="Failure">The typed failure category (<c>none</c> on success).</param>
public sealed record ReleaseExecutionResultDto(
    bool Success,
    IReadOnlyList<RepoReleaseResultDto> Results,
    string? Error,
    ReleaseExecutionFailure Failure)
{
    /// <summary>Projects a <see cref="ReleaseExecutionResult"/> onto its wire representation.</summary>
    /// <param name="result">The execution result to project.</param>
    /// <returns>The DTO the release routes serialize.</returns>
    public static ReleaseExecutionResultDto From(ReleaseExecutionResult result) => new(
        result.Success,
        result.Results.Select(RepoReleaseResultDto.From).ToList(),
        result.Error,
        result.Failure);
}

/// <summary>
/// Response DTO for <c>GET /api/releases/{id}/validate</c>, reproducing the pre-facade anonymous
/// body <c>{ valid, errors }</c>. A missing execution service yields <c>{valid:true, errors:[]}</c>
/// exactly as today.
/// </summary>
/// <param name="Valid">Whether the release passed all validation checks.</param>
/// <param name="Errors">The list of validation errors (empty when valid).</param>
public sealed record ValidationDto(bool Valid, IReadOnlyList<string> Errors);

/// <summary>
/// Discriminated outcome of <see cref="IReleaseFacade.UpdateReleaseStatusAsync"/>. The outcome
/// record IS the complete result — success and failure alike — so the status endpoint maps each
/// variant to its exact HTTP response without a success-only-value wrapper.
/// </summary>
public abstract record ReleaseStatusOutcome;

/// <summary>
/// Outcome for a Planning→Planning status change (the no-op branch): the bare Release JSON,
/// exactly as the pre-facade handler's <c>Results.Ok(existing)</c> produced.
/// </summary>
/// <param name="Release">The unchanged release.</param>
public sealed record PlanningNoOpOutcome(ReleaseDto Release) : ReleaseStatusOutcome;

/// <summary>
/// Outcome for a successful Planning→Released execution: the <c>{ release, result }</c> envelope
/// the pre-facade handler returned.
/// </summary>
/// <param name="Release">The re-read release marked Released.</param>
/// <param name="Result">The execution result.</param>
public sealed record ExecutionSuccessOutcome(ReleaseDto Release, ReleaseExecutionResultDto Result) : ReleaseStatusOutcome;

/// <summary>
/// Failure outcome of a status change. Only the fields that carry the response shape for the
/// given <see cref="FacadeErrorKind"/> are populated — the endpoint serializes per-variant bodies
/// so no null helper fields leak into existing response payloads.
/// </summary>
/// <param name="Kind">The failure category mirroring the HTTP status.</param>
/// <param name="Error">Message for <c>{error}</c> bodies, or <c>null</c>.</param>
/// <param name="Detail">Message for the 500 <c>{detail}</c> / 503 <c>{detail}</c> bodies, or <c>null</c>.</param>
/// <param name="Errors">Validation errors for the 400 <c>{errors:[...]}</c> body, or empty.</param>
/// <param name="Results">Per-repo results for the 500 <c>{results}</c> body, or empty.</param>
public sealed record StatusFailureOutcome(
    FacadeErrorKind Kind,
    string? Error,
    string? Detail,
    string?[] Errors,
    IReadOnlyList<RepoReleaseResultDto> Results) : ReleaseStatusOutcome;

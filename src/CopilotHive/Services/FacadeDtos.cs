using CopilotHive.Configuration;

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

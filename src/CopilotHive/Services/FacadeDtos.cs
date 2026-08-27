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

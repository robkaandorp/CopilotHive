using CopilotHive.Services;
using CopilotHive.Workers;

namespace CopilotHive.Configuration;

/// <summary>
/// Represents the YAML configuration file (hive-config.yaml) from the config repository.
/// </summary>
public sealed class HiveConfigFile
{
    /// <summary>Schema version of the config file.</summary>
    public string Version { get; set; } = "1.0";
    /// <summary>List of repositories this hive operates on.</summary>
    public List<RepositoryConfig> Repositories { get; set; } = [];
    /// <summary>Per-role worker configuration keyed by role name.</summary>
    public Dictionary<string, WorkerConfig> Workers { get; set; } = [];
    /// <summary>Orchestrator-level configuration.</summary>
    public OrchestratorConfig Orchestrator { get; set; } = new();
    /// <summary>Model-level configuration (compaction model, etc.).</summary>
    public ModelsConfig? Models { get; set; }
    /// <summary>Composer agent configuration. When set, the Composer is enabled.</summary>
    public ComposerConfig? Composer { get; set; }

    /// <summary>
    /// Resolves the model to use for a given role.
    /// Returns the per-role override if configured, otherwise the orchestrator's default model.
    /// </summary>
    public string GetModelForRole(string roleName) =>
        Workers.TryGetValue(roleName.ToLowerInvariant(), out var wc) && !string.IsNullOrEmpty(wc.Model)
            ? wc.Model
            : Orchestrator.Model;

    /// <summary>
    /// Resolves the premium model configured for a given role, or <c>null</c> if none is set.
    /// </summary>
    /// <param name="roleName">Role name (e.g. "coder", "reviewer").</param>
    /// <returns>The premium model identifier for the role, or <c>null</c> if not configured.</returns>
    public string? GetPremiumModelForRole(string roleName) =>
        Workers.TryGetValue(roleName.ToLowerInvariant(), out var wc) && !string.IsNullOrEmpty(wc.PremiumModel)
            ? wc.PremiumModel
            : null;

    /// <summary>
    /// Looks up the model in <see cref="ModelsConfig.AvailableModels"/> and returns its
    /// <see cref="ModelEntry.ContextWindow"/> if set and greater than 0.
    /// </summary>
    /// <param name="modelName">Model identifier to look up.</param>
    /// <returns>The configured context window, or <c>null</c> if the model is not found or has no value set.</returns>
    public int? TryGetContextWindowForModel(string? modelName)
    {
        var entry = Models?.AvailableModels?.FirstOrDefault(
            m => string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));
        return entry?.ContextWindow;
    }

    /// <summary>
    /// Resolves the context window size for a given role.
    /// Returns the per-role <c>context_window</c> if set and greater than 0,
    /// otherwise the model-specific context window from the global <c>available_models</c> list,
    /// or finally <see cref="Constants.DefaultBrainContextWindow"/>.
    /// </summary>
    /// <param name="roleName">Role name (e.g. "coder", "reviewer").</param>
    /// <returns>The resolved context window in tokens.</returns>
    public int GetContextWindowForRole(string roleName)
    {
        if (Workers.TryGetValue(roleName.ToLowerInvariant(), out var wc) && wc.ContextWindow > 0)
            return wc.ContextWindow;

        var roleModel = GetModelForRole(roleName);
        var modelCtx = TryGetContextWindowForModel(roleModel);
        if (modelCtx.HasValue && modelCtx.Value > 0)
            return modelCtx.Value;

        return Constants.DefaultBrainContextWindow;
    }

    /// <summary>
    /// Returns the globally-configured available model names from <see cref="ModelsConfig.AvailableModels"/>,
    /// or falls back to the composer-local list via <see cref="ComposerConfig.GetAvailableModels(string)"/>.
    /// </summary>
    /// <param name="fallback">Model to return when no models are configured anywhere.</param>
    /// <returns>A non-empty list of model identifiers.</returns>
    public List<string> GetComposerAvailableModels(string fallback)
    {
        if (Models?.AvailableModels is not null && Models.AvailableModels.Count > 0)
        {
            return Models.AvailableModels.Select(m => m.Name).ToList();
        }

        return Composer?.GetAvailableModels(fallback) ?? [fallback];
    }

    /// <summary>
    /// Returns the curated sub-agent model list. When <see cref="ModelsConfig.SubAgentModels"/>
    /// is non-empty, it is returned; otherwise falls back to <see cref="ModelsConfig.AvailableModels"/>.
    /// Returns an empty list when neither is configured.
    /// </summary>
    public IReadOnlyList<ModelEntry> GetSubAgentModels()
    {
        if (Models?.SubAgentModels is not { Count: > 0 })
        {
            if (Models?.AvailableModels is { Count: > 0 } available)
                return available;
            return [];
        }

        var curated = Models.SubAgentModels;
        // Last-wins by name (case-insensitive): duplicate names in available_models must
        // not crash the merge path.
        var availableByName = new Dictionary<string, ModelEntry>(StringComparer.OrdinalIgnoreCase);
        if (Models.AvailableModels is { Count: > 0 } availableModels)
        {
            foreach (var a in availableModels)
                availableByName[a.Name] = a;
        }

        List<ModelEntry> merged = new(curated.Count);
        foreach (var entry in curated)
        {
            var available = availableByName.GetValueOrDefault(entry.Name);
            merged.Add(new ModelEntry
            {
                Name = entry.Name,
                ContextWindow = entry.ContextWindow ?? available?.ContextWindow,
                // Reasoning effort is never inherited from available_models — it is an
                // explicit per-entry assignment on sub_agent_models.
                ReasoningEffort = entry.ReasoningEffort,
                Description = entry.Description ?? available?.Description,
                SupportsVision = entry.SupportsVision ?? available?.SupportsVision
            });
        }

        return merged;
    }

    /// <summary>
    /// Validates that a reasoning effort is configured (and valid) for every model assignment
    /// in this config: the orchestrator model, each worker model and premium model, the Composer
    /// model, and every <c>sub_agent_models</c> entry.
    /// <para>
    /// Not validated: <c>models.compaction_model</c> (summarization only),
    /// <c>models.available_models</c> entries (transitional legacy field), and
    /// <c>composer.models</c> (model name references, not assignments).
    /// </para>
    /// </summary>
    /// <returns>All validation errors found; an empty list when the config is valid. Never throws.</returns>
    public List<string> ValidateReasoningEffort()
    {
        var errors = new List<string>();

        static bool IsSet(string? value) => !string.IsNullOrWhiteSpace(value);

        void Check(string? effort, string field)
        {
            if (!IsSet(effort))
            {
                errors.Add($"{field}: reasoning_effort is required but missing.");
                return;
            }

            var trimmed = effort!.Trim();
            try
            {
                ReasoningEffortConverter.Parse(trimmed);
            }
            catch (ArgumentException)
            {
                errors.Add(
                    $"{field}: invalid reasoning_effort '{effort}'. Valid values: none, low, medium, high, extra_high.");
            }
        }

        // orchestrator.model always has a value, so reasoning effort is always required.
        Check(Orchestrator.ReasoningEffort, "orchestrator.reasoning_effort");

        foreach (var kv in Workers)
        {
            var worker = kv.Value;
            if (worker is null)
                continue;

            if (IsSet(worker.Model))
                Check(worker.ReasoningEffort, $"workers.{kv.Key}.reasoning_effort");

            if (IsSet(worker.PremiumModel))
                Check(worker.PremiumReasoningEffort, $"workers.{kv.Key}.premium_reasoning_effort");
        }

        if (Composer is not null && IsSet(Composer.Model))
            Check(Composer.ReasoningEffort, "composer.reasoning_effort");

        if (Models?.SubAgentModels is { Count: > 0 } subAgentModels)
        {
            for (var i = 0; i < subAgentModels.Count; i++)
            {
                var entry = subAgentModels[i];
                if (entry is null)
                    continue;
                Check(entry.ReasoningEffort, $"models.sub_agent_models[{i}] ({entry.Name}).reasoning_effort");
            }
        }

        return errors;
    }

    /// <summary>
    /// Resolves the model to use for a given role (typed overload).
    /// Delegates to <see cref="GetModelForRole(string)"/> using the role's name.
    /// </summary>
    public string GetModelForRole(WorkerRole role) => GetModelForRole(role.ToRoleName());

    /// <summary>
    /// Resolves the premium model configured for a given role, or <c>null</c> if none is set (typed overload).
    /// Delegates to <see cref="GetPremiumModelForRole(string)"/> using the role's name.
    /// </summary>
    public string? GetPremiumModelForRole(WorkerRole role) => GetPremiumModelForRole(role.ToRoleName());

    /// <summary>
    /// Resolves the context window size for a given role (typed overload).
    /// Delegates to <see cref="GetContextWindowForRole(string)"/> using the role's name.
    /// </summary>
    public int GetContextWindowForRole(WorkerRole role) => GetContextWindowForRole(role.ToRoleName());

    /// <summary>
    /// Deep-copies all top-level properties from <paramref name="source"/> onto this instance,
    /// replacing old collections with new instances so callers holding the singleton reference
    /// see the updated data immediately.
    /// </summary>
    public void ReloadFrom(HiveConfigFile source)
    {
        Version = source.Version;

        Repositories = new List<RepositoryConfig>(
            source.Repositories.Select(r => new RepositoryConfig
            {
                Name = r.Name,
                Url = r.Url,
                DefaultBranch = r.DefaultBranch,
                MonitorCi = r.MonitorCi,
                CiTimeoutMinutes = r.CiTimeoutMinutes,
                Release = r.Release is null ? null : new ReleaseRepoConfig { MergeTo = r.Release.MergeTo, TagBranch = r.Release.TagBranch }
            }));

        Workers = new Dictionary<string, WorkerConfig>(
            source.Workers.Select(kv => KeyValuePair.Create(
                kv.Key,
                new WorkerConfig
                {
                    Model = kv.Value.Model,
                    PremiumModel = kv.Value.PremiumModel,
                    ContextWindow = kv.Value.ContextWindow,
                    ReasoningEffort = kv.Value.ReasoningEffort,
                    PremiumReasoningEffort = kv.Value.PremiumReasoningEffort
                })));

        Orchestrator = new OrchestratorConfig
        {
            Model = source.Orchestrator.Model,
            MaxIterations = source.Orchestrator.MaxIterations,
            MaxRetriesPerTask = source.Orchestrator.MaxRetriesPerTask,
            MaxParallelGoals = source.Orchestrator.MaxParallelGoals,
            VerboseLogging = source.Orchestrator.VerboseLogging,
            BrainMaxSteps = source.Orchestrator.BrainMaxSteps,
            BranchCleanupDelayHours = source.Orchestrator.BranchCleanupDelayHours,
            WorkerTaskTimeoutMinutes = source.Orchestrator.WorkerTaskTimeoutMinutes,
            ReasoningEffort = source.Orchestrator.ReasoningEffort
        };

        if (source.Models is not null)
        {
            Models = new ModelsConfig
            {
                CompactionModel = source.Models.CompactionModel,
                AvailableModels = source.Models.AvailableModels?.Select(m => new ModelEntry
                {
                    Name = m.Name,
                    ContextWindow = m.ContextWindow,
                    ReasoningEffort = m.ReasoningEffort,
                    Description = m.Description,
                    SupportsVision = m.SupportsVision
                }).ToList(),
                SubAgentModels = source.Models.SubAgentModels?.Select(m => new ModelEntry
                {
                    Name = m.Name,
                    ContextWindow = m.ContextWindow,
                    ReasoningEffort = m.ReasoningEffort,
                    Description = m.Description,
                    SupportsVision = m.SupportsVision
                }).ToList()
            };
        }
        else
        {
            Models = null;
        }

        if (source.Composer is not null)
        {
            Composer = new ComposerConfig
            {
                Model = source.Composer.Model,
                Models = source.Composer.Models?.ToList(),
                MaxSteps = source.Composer.MaxSteps,
                ReasoningEffort = source.Composer.ReasoningEffort,
                EventNotifications = source.Composer.EventNotifications is null
                    ? null
                    : new EventNotificationsConfig
                    {
                        Mode = source.Composer.EventNotifications.Mode,
                        ActiveEvents = source.Composer.EventNotifications.ActiveEvents?.ToList(),
                        ThrottleSeconds = source.Composer.EventNotifications.ThrottleSeconds
                    }
            };
        }
        else
        {
            Composer = null;
        }
    }
}

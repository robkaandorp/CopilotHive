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
        var strippedName = StripReasoningSuffix(modelName);
        var entry = Models?.AvailableModels?.FirstOrDefault(
            m => string.Equals(m.Name, strippedName, StringComparison.OrdinalIgnoreCase));
        return entry?.ContextWindow;
    }

    /// <summary>
    /// Known reasoning effort levels recognised as model-name suffixes (e.g. <c>:high</c>).
    /// </summary>
    private static readonly HashSet<string> KnownReasoningLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "low", "medium", "high", "extra_high"
    };

    /// <summary>
    /// Strips a known reasoning-effort suffix (e.g. <c>:high</c>) from a model name.
    /// Returns the name unchanged if the suffix after the last colon is not a known
    /// reasoning level, so provider prefixes like <c>ollama-cloud/gpt-oss:120b</c> are preserved.
    /// </summary>
    /// <param name="name">Model name that may carry a reasoning suffix.</param>
    private static string StripReasoningSuffix(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return name ?? string.Empty;

        var lastColon = name.LastIndexOf(':');
        if (lastColon > 0 && lastColon < name.Length - 1)
        {
            var suffix = name.Substring(lastColon + 1);
            if (KnownReasoningLevels.Contains(suffix))
                return name.Substring(0, lastColon);
        }
        return name;
    }

    /// <summary>
    /// Looks up the model in <see cref="ModelsConfig.AvailableModels"/> and returns its
    /// <see cref="ModelEntry.ReasoningEffort"/> if set.
    /// </summary>
    /// <param name="modelName">Model identifier to look up.</param>
    /// <returns>The configured reasoning effort, or <c>null</c> if the model is not found or has no value set.</returns>
    public string? TryGetReasoningEffortForModel(string? modelName)
    {
        var strippedName = StripReasoningSuffix(modelName);
        var entry = Models?.AvailableModels?.FirstOrDefault(
            m => string.Equals(m.Name, strippedName, StringComparison.OrdinalIgnoreCase));
        return entry?.ReasoningEffort;
    }

    /// <summary>
    /// Appends a reasoning effort suffix (e.g. <c>:high</c>) to a model name when one is configured.
    /// An explicit reasoning suffix already present on the model name takes precedence and is
    /// preserved unchanged. A <c>null</c> or empty <paramref name="reasoningEffort"/> leaves the model unchanged.
    /// </summary>
    /// <param name="model">The model identifier.</param>
    /// <param name="reasoningEffort">The reasoning effort to apply, or <c>null</c>/empty for none.</param>
    /// <returns>The model name with the reasoning suffix applied (or unchanged).</returns>
    public static string ApplyReasoningSuffix(string model, string? reasoningEffort)
    {
        if (string.IsNullOrEmpty(reasoningEffort))
            return model;

        // Check if model already ends with a known reasoning suffix — explicit suffix takes precedence
        var lastColon = model.LastIndexOf(':');
        if (lastColon > 0 && lastColon < model.Length - 1)
        {
            var suffix = model.Substring(lastColon + 1);
            if (KnownReasoningLevels.Contains(suffix))
                return model; // Already has a reasoning suffix, don't override
        }

        return $"{model}:{reasoningEffort}";
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
                Release = r.Release is null ? null : new ReleaseRepoConfig { MergeTo = r.Release.MergeTo, TagBranch = r.Release.TagBranch }
            }));

        Workers = new Dictionary<string, WorkerConfig>(
            source.Workers.Select(kv => KeyValuePair.Create(
                kv.Key,
                new WorkerConfig
                {
                    Model = kv.Value.Model,
                    PremiumModel = kv.Value.PremiumModel,
                    ContextWindow = kv.Value.ContextWindow
                })));

        Orchestrator = new OrchestratorConfig
        {
            Model = source.Orchestrator.Model,
            MaxIterations = source.Orchestrator.MaxIterations,
            MaxRetriesPerTask = source.Orchestrator.MaxRetriesPerTask,
            MaxParallelGoals = source.Orchestrator.MaxParallelGoals,
            AlwaysImprove = source.Orchestrator.AlwaysImprove,
            VerboseLogging = source.Orchestrator.VerboseLogging,
            BrainMaxSteps = source.Orchestrator.BrainMaxSteps,
            BranchCleanupDelayHours = source.Orchestrator.BranchCleanupDelayHours
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
                    ReasoningEffort = m.ReasoningEffort
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
                MaxSteps = source.Composer.MaxSteps
            };
        }
        else
        {
            Composer = null;
        }
    }
}

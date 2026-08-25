using CopilotHive.Services;
using CopilotHive.Workers;
using YamlDotNet.Serialization;

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
    /// Marks whether this instance was actually parsed from a config repository
    /// (set by <see cref="ConfigRepoManager"/>). <c>false</c> for the ordinary/fallback
    /// construction used when no <c>--config-repo</c> is configured. Never YAML-serialized.
    /// The setter is internal: only the config layer (and the test assembly via
    /// <c>InternalsVisibleTo</c>) can set it — external consumers only read it.
    /// </summary>
    [YamlIgnore]
    public bool IsConfigured { get; internal set; }

    /// <summary>
    /// Resolves the model to use for a given role.
    /// Returns the per-role override if configured, or <c>null</c> when the role has no
    /// configured model (no orchestrator fall-through, no constant default).
    /// </summary>
    public string? GetModelForRole(string roleName) =>
        Workers.TryGetValue(roleName.ToLowerInvariant(), out var wc) && !string.IsNullOrWhiteSpace(wc.Model)
            ? wc.Model
            : null;

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
    /// The single normalization/matching primitive for model names. Trims whitespace on BOTH
    /// <paramref name="candidate"/> and each catalog entry; drops whitespace-only/empty entries;
    /// matches ordinal-ignore-case; ordinal-ignore-case duplicates collapse to the FIRST entry;
    /// returns the TRIMMED canonical name, or <c>null</c> when there is no match, the catalog is
    /// empty, or the candidate is null/whitespace-only.
    /// </summary>
    /// <param name="catalog">The catalog of model names to match against; may be null/empty.</param>
    /// <param name="candidate">The model name to resolve; trimmed before matching.</param>
    /// <returns>The trimmed canonical model name, or <c>null</c> on no match.</returns>
    public string? ResolveAvailableModel(IEnumerable<string>? catalog, string? candidate)
    {
        if (catalog is null || string.IsNullOrWhiteSpace(candidate))
            return null;

        var trimmedCandidate = candidate.Trim();
        if (trimmedCandidate.Length == 0)
            return null;

        foreach (var entry in catalog)
        {
            if (entry is null)
                continue;
            var trimmed = entry.Trim();
            if (trimmed.Length == 0)
                continue;
            if (string.Equals(trimmed, trimmedCandidate, StringComparison.OrdinalIgnoreCase))
                return trimmed;
        }

        return null;
    }

    /// <summary>
    /// Resolves <paramref name="candidate"/> against the GLOBAL <see cref="ModelsConfig.AvailableModels"/>
    /// entry names via <see cref="ResolveAvailableModel(IEnumerable{string}?, string?)"/>.
    /// </summary>
    /// <param name="candidate">The model name to resolve.</param>
    /// <returns>The trimmed canonical name from the global catalog, or <c>null</c>.</returns>
    public string? ResolveAvailableModel(string? candidate) =>
        ResolveAvailableModel(Models?.AvailableModels?.Select(m => m.Name), candidate);

    /// <summary>
    /// Resolves the Composer's default model: <see cref="ComposerConfig.Model"/> normalized against
    /// the global <see cref="ModelsConfig.AvailableModels"/> catalog. Returns the trimmed canonical
    /// name when the composer model is present (and normalized) in the global list; <c>null</c>
    /// when unset, absent from the catalog, or no global catalog exists. Delegates to
    /// <see cref="ResolveAvailableModel(IEnumerable{string}?, string?)"/> — no duplicated matching logic.
    /// </summary>
    public string? ResolveComposerDefaultModel() => ResolveAvailableModel(Composer?.Model);

    /// <summary>
    /// Looks up the model in <see cref="ModelsConfig.AvailableModels"/> and returns its
    /// <see cref="ModelEntry.ContextWindow"/> if set and greater than 0.
    /// Name matching routes through <see cref="ResolveAvailableModel(IEnumerable{string}?, string?)"/>:
    /// trim + ordinal-ignore-case, FIRST-wins on normalized duplicates — so a trimmed canonical
    /// model resolves a catalog entry whose stored name carries surrounding whitespace.
    /// </summary>
    /// <param name="modelName">Model identifier to look up.</param>
    /// <returns>The configured context window, or <c>null</c> if the model is not found or has no value set.</returns>
    public int? TryGetContextWindowForModel(string? modelName)
    {
        var canonical = ResolveAvailableModel(modelName);
        if (canonical is null)
            return null;

        var entry = Models?.AvailableModels?.FirstOrDefault(
            m => string.Equals(m.Name?.Trim(), canonical, StringComparison.OrdinalIgnoreCase));
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
    /// Returns the Composer's normalized selectable catalog: the GLOBAL
    /// <see cref="ModelsConfig.AvailableModels"/> names ONLY — trimmed, whitespace-only/empty
    /// dropped, ordinal-ignore-case duplicates collapsed to the FIRST. There is NO
    /// composer-local fall-through and NO fabricated fallback: an empty global list yields an
    /// empty catalog.
    /// </summary>
    /// <returns>The normalized list of selectable model identifiers (possibly empty).</returns>
    public List<string> GetComposerAvailableModels()
    {
        var result = new List<string>();
        if (Models?.AvailableModels is null)
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Models.AvailableModels)
        {
            var name = entry.Name?.Trim();
            if (string.IsNullOrEmpty(name))
                continue;
            if (seen.Add(name))
                result.Add(name);
        }

        return result;
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
    /// Not validated: <c>models.compaction_model</c> (summarization only) and
    /// <c>models.available_models</c> entries (transitional legacy field).
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

        // orchestrator.reasoning_effort is required ONLY when orchestrator.model is set.
        // An unset orchestrator model (null after Slice 3a's blank→null normalization) is
        // its own unconfigured state — not a reasoning error.
        if (IsSet(Orchestrator.Model))
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

        // composer.reasoning_effort is required ONLY when the composer's model resolves to a
        // valid effective default in the global available_models catalog. A set-but-absent
        // composer.model ⇒ no effective default ⇒ reasoning_effort NOT required.
        if (Composer is not null && ResolveComposerDefaultModel() is not null)
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
    public string? GetModelForRole(WorkerRole role) => GetModelForRole(role.ToRoleName());

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
    /// <para>
    /// <see cref="IsConfigured"/> is deliberately NOT copied: it marks that a repo config was
    /// loaded onto this singleton, so a configured instance that reloads stays
    /// <c>IsConfigured = true</c>.
    /// </para>
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
                Release = r.Release is null ? null : new ReleaseRepoConfig { MergeTo = r.Release.MergeTo, TagBranch = r.Release.TagBranch },
                PublishNuGet = r.PublishNuGet is null
                    ? null
                    : new NuGetPublishConfig
                    {
                        Packages = r.PublishNuGet.Packages
                            .Select(p => new NuGetPackageEntry { PackageId = p.PackageId })
                            .ToList()
                    }
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

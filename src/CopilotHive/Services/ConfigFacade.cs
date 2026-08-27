using CopilotHive.Configuration;
using CopilotHive.Git;

namespace CopilotHive.Services;

/// <summary>
/// Facade over the model-catalog configuration surface: reading the live model configuration,
/// persisting model changes, discovering provider models, and managing the available/sub-agent
/// model lists. Endpoint handlers depend on this interface instead of reaching into the
/// configuration services directly, so the HTTP layer stays thin and the failure mapping
/// (status codes, problem details, error bodies) lives in one place.
/// </summary>
/// <remarks>
/// Each operation returns a <see cref="FacadeResult{T}"/> whose <see cref="FacadeErrorKind"/>
/// mirrors the HTTP status the endpoint would have returned before the facade existed. A
/// facade method catches ONLY the exception types its previous endpoint handler caught;
/// anything else is rethrown so unexpected failures surface as exceptions instead of being
/// silently converted into a result.
/// </remarks>
public interface IConfigFacade
{
    /// <summary>
    /// Reads the full model configuration (orchestrator/composer/compaction models, per-role
    /// worker models, reasoning efforts, available models, sub-agent models).
    /// </summary>
    /// <returns>The model configuration, or <see cref="FacadeErrorKind.NotFound"/> when no
    /// <see cref="HiveConfigFile"/> is registered.</returns>
    FacadeResult<ModelsConfigDto> GetModels();

    /// <summary>
    /// Persists a batch of model configuration changes (validate → mutate → write → commit →
    /// live Brain update).
    /// </summary>
    /// <param name="update">The model changes to apply.</param>
    /// <param name="ct">Cancellation token forwarded to the persistence layer.</param>
    /// <returns>
    /// Success with the saved description, <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered, or <see cref="FacadeErrorKind.BadRequest"/>
    /// when the update is invalid. Cancellation and any other exception propagate to the caller.
    /// </returns>
    Task<FacadeResult<SavedResult>> SaveModelsAsync(ModelConfigUpdate update, CancellationToken ct);

    /// <summary>
    /// Discovers models available from all configured providers (Copilot, Ollama).
    /// </summary>
    /// <returns>
    /// The discovered models, or <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ModelDiscoveryService"/> is registered.
    /// </returns>
    Task<FacadeResult<IReadOnlyList<DiscoveredModelDto>>> DiscoverModelsAsync();

    /// <summary>
    /// Adds a model to the global <c>available_models</c> list.
    /// </summary>
    /// <param name="request">The model to add.</param>
    /// <returns>
    /// Success, <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered, or
    /// <see cref="FacadeErrorKind.Conflict"/> when the name already exists.
    /// </returns>
    Task<FacadeResult<SavedResult>> AddAvailableModelAsync(AvailableModelRequest request);

    /// <summary>
    /// Updates an existing <c>available_models</c> entry by name.
    /// </summary>
    /// <param name="name">Model name to update (already URL-unescaped by the caller).</param>
    /// <param name="request">The new values.</param>
    /// <returns>
    /// Success, <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered, or
    /// <see cref="FacadeErrorKind.NotFound"/> when the model does not exist.
    /// </returns>
    Task<FacadeResult<SavedResult>> UpdateAvailableModelAsync(string name, AvailableModelRequest request);

    /// <summary>
    /// Removes a model from the <c>available_models</c> list by name.
    /// </summary>
    /// <param name="name">Model name to remove (already URL-unescaped by the caller).</param>
    /// <returns>
    /// Success, <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered, or
    /// <see cref="FacadeErrorKind.NotFound"/> when the model does not exist.
    /// </returns>
    Task<FacadeResult<RemovedResult>> RemoveAvailableModelAsync(string name);

    /// <summary>
    /// Adds a model to the curated <c>sub_agent_models</c> list.
    /// </summary>
    /// <param name="request">The model to add.</param>
    /// <returns>
    /// Success, <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered, or
    /// <see cref="FacadeErrorKind.Conflict"/> when the name already exists.
    /// </returns>
    Task<FacadeResult<SavedResult>> AddSubAgentModelAsync(SubAgentModelRequest request);

    /// <summary>
    /// Updates an existing <c>sub_agent_models</c> entry by name.
    /// </summary>
    /// <param name="name">Model name to update (already URL-unescaped by the caller).</param>
    /// <param name="request">The new values.</param>
    /// <returns>
    /// Success, <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered, or
    /// <see cref="FacadeErrorKind.NotFound"/> when the model does not exist.
    /// </returns>
    Task<FacadeResult<SavedResult>> UpdateSubAgentModelAsync(string name, SubAgentModelRequest request);

    /// <summary>
    /// Removes a model from the <c>sub_agent_models</c> list by name.
    /// </summary>
    /// <param name="name">Model name to remove (already URL-unescaped by the caller).</param>
    /// <returns>
    /// Success, <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered, or
    /// <see cref="FacadeErrorKind.NotFound"/> when the model does not exist.
    /// </returns>
    Task<FacadeResult<RemovedResult>> RemoveSubAgentModelAsync(string name);

    /// <summary>
    /// Lists the configured repositories.
    /// </summary>
    /// <returns>
    /// The configured repositories as DTOs, or <see cref="FacadeErrorKind.NotFound"/> when no
    /// <see cref="HiveConfigFile"/> is registered.
    /// </returns>
    FacadeResult<IReadOnlyList<RepositoryDto>> GetRepositories();

    /// <summary>
    /// Adds a repository to the config (validate → mutate → write → commit → clone).
    /// </summary>
    /// <param name="request">The repository to add.</param>
    /// <returns>
    /// Success, <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered, <see cref="FacadeErrorKind.BadRequest"/>
    /// when the request is invalid, or <see cref="FacadeErrorKind.Conflict"/> when a repository
    /// with the same name already exists. Any other exception propagates to the caller.
    /// </returns>
    Task<FacadeResult<SavedResult>> AddRepositoryAsync(RepositoryRequest request);

    /// <summary>
    /// Lists the remote branches of a repository via the brain repo manager.
    /// </summary>
    /// <param name="name">Repository name (NOT URL-unescaped — the repository routes do not unescape).</param>
    /// <param name="ct">Cancellation token forwarded to the repo manager.</param>
    /// <returns>
    /// The branch names, <see cref="FacadeErrorKind.ServiceUnavailable"/> when no
    /// <see cref="IBrainRepoManager"/> is registered, <see cref="FacadeErrorKind.NotFound"/>
    /// when the repository is not cloned, <see cref="FacadeErrorKind.BadRequest"/> when the
    /// name is invalid, or <see cref="FacadeErrorKind.Internal"/> for any other failure
    /// (including cancellation, matching the pre-facade catch-all).
    /// </returns>
    Task<FacadeResult<IReadOnlyList<string>>> GetBranchesAsync(string name, CancellationToken ct);

    /// <summary>
    /// Updates an existing repository's URL, default branch, and optional release configuration.
    /// </summary>
    /// <param name="name">Repository name to update (NOT URL-unescaped — the repository routes do not unescape).</param>
    /// <param name="request">The new values.</param>
    /// <returns>
    /// Success, <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered, <see cref="FacadeErrorKind.BadRequest"/>
    /// when the request is invalid, or <see cref="FacadeErrorKind.NotFound"/> when the
    /// repository does not exist. Any other exception propagates to the caller.
    /// </returns>
    Task<FacadeResult<SavedResult>> UpdateRepositoryAsync(string name, RepositoryRequest request);

    /// <summary>
    /// Removes a repository from the config.
    /// </summary>
    /// <param name="name">Repository name to remove (NOT URL-unescaped — the repository routes do not unescape).</param>
    /// <returns>
    /// Success, <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered, or <see cref="FacadeErrorKind.NotFound"/>
    /// when the repository does not exist.
    /// </returns>
    Task<FacadeResult<RemovedResult>> RemoveRepositoryAsync(string name);

    /// <summary>
    /// Reads the orchestrator-level settings (model, iteration limits, logging, timeouts).
    /// </summary>
    /// <returns>The orchestrator settings, or <see cref="FacadeErrorKind.NotFound"/> when no
    /// <see cref="HiveConfigFile"/> is registered.</returns>
    FacadeResult<OrchestratorConfigDto> GetOrchestrator();

    /// <summary>
    /// Persists orchestrator-level setting changes (validate → mutate → write → commit).
    /// </summary>
    /// <param name="update">The orchestrator settings to apply.</param>
    /// <returns>
    /// Success with the saved result, or <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered. Any other exception propagates to the caller.
    /// </returns>
    Task<FacadeResult<SavedResult>> SaveOrchestratorAsync(OrchestratorSettingsUpdate update);

    /// <summary>
    /// Reads the per-role worker settings (model, premium model, context window) keyed by role.
    /// </summary>
    /// <returns>The workers dictionary, or <see cref="FacadeErrorKind.NotFound"/> when no
    /// <see cref="HiveConfigFile"/> is registered.</returns>
    FacadeResult<WorkersConfigDto> GetWorkers();

    /// <summary>
    /// Persists per-role worker context windows (validate → mutate → write → commit).
    /// </summary>
    /// <param name="contextWindows">Role → context window size mapping.</param>
    /// <returns>
    /// Success with the saved result, or <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered. Any other exception propagates to the caller.
    /// </returns>
    Task<FacadeResult<SavedResult>> SaveWorkersAsync(Dictionary<string, int> contextWindows);

    /// <summary>
    /// Reads the runtime-effective Composer settings (model, max steps, reasoning effort, and
    /// the typed event-notifications shape).
    /// </summary>
    /// <returns>The Composer settings, or <see cref="FacadeErrorKind.NotFound"/> when no
    /// <see cref="HiveConfigFile"/> is registered.</returns>
    FacadeResult<ComposerConfigDto> GetComposer();

    /// <summary>
    /// Persists Composer setting changes (validate → mutate → write → commit).
    /// </summary>
    /// <param name="update">The Composer settings to apply.</param>
    /// <param name="ct">Cancellation token forwarded to the persistence layer.</param>
    /// <returns>
    /// Success with the saved result, <see cref="FacadeErrorKind.BadRequest"/> when the update
    /// is invalid (e.g. an unknown notification mode or event name), or
    /// <see cref="FacadeErrorKind.NotConfigured"/> when no <see cref="ConfigModelService"/> is
    /// registered. Cancellation and any other exception propagate to the caller.
    /// </returns>
    Task<FacadeResult<SavedResult>> SaveComposerAsync(ComposerSettingsUpdate update, CancellationToken ct);
}

/// <summary>
/// Default implementation of <see cref="IConfigFacade"/> delegating to the model configuration
/// services. All dependencies are optional: when a dependency is absent (e.g. no config repo
/// was configured at startup), reads fall back to the registered <see cref="HiveConfigFile"/>
/// singleton while persistence/discovery operations report
/// <see cref="FacadeErrorKind.NotConfigured"/>.
/// </summary>
public sealed class ConfigFacade : IConfigFacade
{
    private readonly HiveConfigFile? _hiveConfig;
    private readonly ConfigModelService? _configModel;
    private readonly ModelDiscoveryService? _discovery;
    private readonly IBrainRepoManager? _repoManager;
    private readonly ILogger<ConfigFacade> _log;

    /// <summary>
    /// Initialises a new <see cref="ConfigFacade"/>.
    /// </summary>
    /// <param name="hiveConfig">The live <see cref="HiveConfigFile"/> singleton (may be <c>null</c> when not registered).</param>
    /// <param name="configModel">The <see cref="ConfigModelService"/> (may be <c>null</c> when no config repo is configured).</param>
    /// <param name="discovery">The <see cref="ModelDiscoveryService"/> (may be <c>null</c> when no config repo is configured).</param>
    /// <param name="log">Logger instance.</param>
    /// <param name="repoManager">The <see cref="IBrainRepoManager"/> (may be <c>null</c> when not registered).</param>
    public ConfigFacade(
        HiveConfigFile? hiveConfig,
        ConfigModelService? configModel,
        ModelDiscoveryService? discovery,
        ILogger<ConfigFacade> log,
        IBrainRepoManager? repoManager = null)
    {
        _hiveConfig = hiveConfig;
        _configModel = configModel;
        _discovery = discovery;
        _log = log;
        _repoManager = repoManager;
    }

    /// <inheritdoc />
    public FacadeResult<ModelsConfigDto> GetModels()
    {
        var config = _hiveConfig;
        if (config is null)
        {
            _log.LogWarning("Config repo is not configured.");
            return new(false, null, "Config repo not configured.", FacadeErrorKind.NotFound);
        }

        // Reasoning effort is stored as string? in the YAML-bound config classes but is
        // projected here as the ReasoningEffort enum. The global JsonStringEnumConverter
        // renders it snake_case (e.g. "extra_high"). A value a dynamic reload left
        // unrecognised degrades to null rather than failing the whole response.
        return new(true, new ModelsConfigDto(
            Orchestrator: config.Orchestrator.Model,
            Composer: config.Composer?.Model,
            Compaction: config.Models?.CompactionModel,
            Workers: config.Workers.ToDictionary(
                kv => kv.Key,
                kv => new WorkerModelsDto(kv.Value.Model, kv.Value.PremiumModel)),
            OrchestratorReasoningEffort: ConfigModelService.ParseLenient(config.Orchestrator.ReasoningEffort),
            ComposerReasoningEffort: ConfigModelService.ParseLenient(config.Composer?.ReasoningEffort),
            WorkerReasoningEffort: config.Workers.ToDictionary(
                kv => kv.Key,
                kv => ConfigModelService.ParseLenient(kv.Value.ReasoningEffort)),
            WorkerPremiumReasoningEffort: config.Workers.ToDictionary(
                kv => kv.Key,
                kv => ConfigModelService.ParseLenient(kv.Value.PremiumReasoningEffort)),
            SubAgentModelReasoning: config.Models?.SubAgentModels?
                .Where(m => !string.IsNullOrEmpty(m.Name))
                .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => ConfigModelService.ParseLenient(g.First().ReasoningEffort)),
            AvailableModels: config.Models?.AvailableModels?
                .Select(m => new AvailableModelDto(m.Name, m.ContextWindow, m.Description, m.SupportsVision))
                .ToList(),
            // Projected entry-by-entry rather than returned as raw ModelEntry objects:
            // ModelEntry.ReasoningEffort is deliberately string? at the YAML boundary, so
            // serializing the entity directly would leak a raw string (and an unrecognised
            // stored value such as "turbo" verbatim) into an otherwise enum-typed response.
            SubAgentModels: config.Models?.SubAgentModels?
                .Select(m => new ConfigSubAgentModelDto(
                    m.Name,
                    m.ContextWindow,
                    ConfigModelService.ParseLenient(m.ReasoningEffort),
                    m.Description,
                    m.SupportsVision))
                .ToList()),
            null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<SavedResult>> SaveModelsAsync(ModelConfigUpdate update, CancellationToken ct)
    {
        var svc = _configModel;
        if (svc is null)
        {
            _log.LogWarning("Config repo is not configured — model changes cannot be persisted.");
            return new(false, null, "Config repo is not configured — model changes cannot be persisted.", FacadeErrorKind.NotConfigured);
        }

        try
        {
            await svc.SaveModelConfigAsync(update, ct);
            return new(true, new SavedResult(true, update.Description), null, FacadeErrorKind.None);
        }
        catch (ArgumentException ex)
        {
            return new(false, null, ex.Message, FacadeErrorKind.BadRequest);
        }
        // OperationCanceledException and any other exception propagate to the caller —
        // the endpoint never catches them, matching the pre-facade handler.
    }

    /// <inheritdoc />
    public async Task<FacadeResult<IReadOnlyList<DiscoveredModelDto>>> DiscoverModelsAsync()
    {
        var discovery = _discovery;
        if (discovery is null)
        {
            _log.LogWarning("Model discovery service is not configured.");
            return new(false, null, "Model discovery service is not configured.", FacadeErrorKind.NotConfigured);
        }

        var models = await discovery.DiscoverAllAsync();
        return new(
            true,
            models.Select(m => new DiscoveredModelDto(m.Id, m.Name, m.Vendor, m.ContextWindow, m.Enabled)).ToList(),
            null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<SavedResult>> AddAvailableModelAsync(AvailableModelRequest request)
    {
        var svc = _configModel;
        if (svc is null)
        {
            _log.LogWarning("Config service is not configured.");
            return new(false, null, "Config service is not configured.", FacadeErrorKind.NotConfigured);
        }

        try
        {
            await svc.AddAvailableModelAsync(request.Name, request.ContextWindow, request.Description, request.SupportsVision);
            return new(true, new SavedResult(true, null), null, FacadeErrorKind.None);
        }
        catch (InvalidOperationException ex)
        {
            return new(false, null, ex.Message, FacadeErrorKind.Conflict);
        }
    }

    /// <inheritdoc />
    public async Task<FacadeResult<SavedResult>> UpdateAvailableModelAsync(string name, AvailableModelRequest request)
    {
        var svc = _configModel;
        if (svc is null)
        {
            _log.LogWarning("Config service is not configured.");
            return new(false, null, "Config service is not configured.", FacadeErrorKind.NotConfigured);
        }

        try
        {
            await svc.UpdateAvailableModelAsync(name, request.ContextWindow, request.Description, request.SupportsVision);
            return new(true, new SavedResult(true, null), null, FacadeErrorKind.None);
        }
        catch (InvalidOperationException ex)
        {
            return new(false, null, ex.Message, FacadeErrorKind.NotFound);
        }
    }

    /// <inheritdoc />
    public async Task<FacadeResult<RemovedResult>> RemoveAvailableModelAsync(string name)
    {
        var svc = _configModel;
        if (svc is null)
        {
            _log.LogWarning("Config service is not configured.");
            return new(false, null, "Config service is not configured.", FacadeErrorKind.NotConfigured);
        }

        var removed = await svc.RemoveAvailableModelAsync(name);
        return removed
            ? new(true, new RemovedResult(true), null, FacadeErrorKind.None)
            : new(false, null, $"Model '{name}' not found.", FacadeErrorKind.NotFound);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<SavedResult>> AddSubAgentModelAsync(SubAgentModelRequest request)
    {
        var svc = _configModel;
        if (svc is null)
        {
            _log.LogWarning("Config service is not configured.");
            return new(false, null, "Config service is not configured.", FacadeErrorKind.NotConfigured);
        }

        try
        {
            await svc.AddSubAgentModelAsync(request.Name, request.ContextWindow, request.ReasoningEffort, request.Description, request.SupportsVision);
            return new(true, new SavedResult(true, null), null, FacadeErrorKind.None);
        }
        catch (InvalidOperationException ex)
        {
            return new(false, null, ex.Message, FacadeErrorKind.Conflict);
        }
    }

    /// <inheritdoc />
    public async Task<FacadeResult<SavedResult>> UpdateSubAgentModelAsync(string name, SubAgentModelRequest request)
    {
        var svc = _configModel;
        if (svc is null)
        {
            _log.LogWarning("Config service is not configured.");
            return new(false, null, "Config service is not configured.", FacadeErrorKind.NotConfigured);
        }

        try
        {
            await svc.UpdateSubAgentModelAsync(name, request.ContextWindow, request.ReasoningEffort, request.Description, request.SupportsVision);
            return new(true, new SavedResult(true, null), null, FacadeErrorKind.None);
        }
        catch (InvalidOperationException ex)
        {
            return new(false, null, ex.Message, FacadeErrorKind.NotFound);
        }
    }

    /// <inheritdoc />
    public async Task<FacadeResult<RemovedResult>> RemoveSubAgentModelAsync(string name)
    {
        var svc = _configModel;
        if (svc is null)
        {
            _log.LogWarning("Config service is not configured.");
            return new(false, null, "Config service is not configured.", FacadeErrorKind.NotConfigured);
        }

        var removed = await svc.RemoveSubAgentModelAsync(name);
        return removed
            ? new(true, new RemovedResult(true), null, FacadeErrorKind.None)
            : new(false, null, $"Model '{name}' not found.", FacadeErrorKind.NotFound);
    }

    /// <inheritdoc />
    public FacadeResult<IReadOnlyList<RepositoryDto>> GetRepositories()
    {
        var config = _hiveConfig;
        if (config is null)
        {
            _log.LogWarning("Config repo is not configured.");
            return new(false, null, "Config repo not configured.", FacadeErrorKind.NotFound);
        }

        return new(
            true,
            config.Repositories.Select(r => new RepositoryDto(
                r.Name,
                r.Url,
                r.DefaultBranch,
                r.MonitorCi,
                r.CiTimeoutMinutes,
                r.Release is null ? null : new RepositoryReleaseDto(r.Release.MergeTo, r.Release.TagBranch),
                r.PublishNuGet is null
                    ? null
                    : new RepositoryPublishNuGetDto(
                        r.PublishNuGet.Packages.Select(p => new RepositoryPackageDto(p.PackageId)).ToList()))).ToList(),
            null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<SavedResult>> AddRepositoryAsync(RepositoryRequest request)
    {
        var svc = _configModel;
        if (svc is null)
        {
            _log.LogWarning("Config service is not configured.");
            return new(false, null, "Config service is not configured.", FacadeErrorKind.NotConfigured);
        }

        try
        {
            await svc.AddRepositoryAsync(request.Name, request.Url, request.DefaultBranch, request.Release, request.MonitorCi, request.CiTimeoutMinutes);
            return new(true, new SavedResult(true, null), null, FacadeErrorKind.None);
        }
        catch (ArgumentException ex)
        {
            return new(false, null, ex.Message, FacadeErrorKind.BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            return new(false, null, ex.Message, FacadeErrorKind.Conflict);
        }
        // Any other exception propagates to the caller — the endpoint never caught it either.
    }

    /// <inheritdoc />
    public async Task<FacadeResult<IReadOnlyList<string>>> GetBranchesAsync(string name, CancellationToken ct)
    {
        var repoManager = _repoManager;
        if (repoManager is null)
        {
            _log.LogWarning("Repository manager is not available.");
            return new(false, null, "Repository manager is not available.", FacadeErrorKind.ServiceUnavailable);
        }

        try
        {
            var branches = await repoManager.ListRemoteBranchesAsync(name, ct);
            return new(true, branches, null, FacadeErrorKind.None);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("is not cloned"))
        {
            return new(false, null, ex.Message, FacadeErrorKind.NotFound);
        }
        catch (ArgumentException ex)
        {
            return new(false, null, ex.Message, FacadeErrorKind.BadRequest);
        }
        catch (Exception ex)
        {
            // The pre-facade handler's catch-all: every other failure — including
            // OperationCanceledException — becomes a 500 problem-details body.
            _log.LogError(ex, "Failed to list branches for repository '{Name}'", name);
            return new(false, null, "Failed to list branches for this repository.", FacadeErrorKind.Internal);
        }
    }

    /// <inheritdoc />
    public async Task<FacadeResult<SavedResult>> UpdateRepositoryAsync(string name, RepositoryRequest request)
    {
        var svc = _configModel;
        if (svc is null)
        {
            _log.LogWarning("Config service is not configured.");
            return new(false, null, "Config service is not configured.", FacadeErrorKind.NotConfigured);
        }

        try
        {
            await svc.UpdateRepositoryAsync(name, request.Url, request.DefaultBranch, request.Release, request.MonitorCi, request.CiTimeoutMinutes);
            return new(true, new SavedResult(true, null), null, FacadeErrorKind.None);
        }
        catch (ArgumentException ex)
        {
            return new(false, null, ex.Message, FacadeErrorKind.BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            return new(false, null, ex.Message, FacadeErrorKind.NotFound);
        }
        // Any other exception propagates to the caller — the endpoint never caught it either.
    }

    /// <inheritdoc />
    public async Task<FacadeResult<RemovedResult>> RemoveRepositoryAsync(string name)
    {
        var svc = _configModel;
        if (svc is null)
        {
            _log.LogWarning("Config service is not configured.");
            return new(false, null, "Config service is not configured.", FacadeErrorKind.NotConfigured);
        }

        var removed = await svc.RemoveRepositoryAsync(name);
        return removed
            ? new(true, new RemovedResult(true), null, FacadeErrorKind.None)
            : new(false, null, $"Repository '{name}' not found.", FacadeErrorKind.NotFound);
    }

    /// <inheritdoc />
    public FacadeResult<OrchestratorConfigDto> GetOrchestrator()
    {
        var config = _hiveConfig;
        if (config is null)
        {
            _log.LogWarning("Config repo is not configured.");
            return new(false, null, "Config repo not configured.", FacadeErrorKind.NotFound);
        }

        // The pre-facade handler serialized the raw OrchestratorConfig object, so every
        // property is projected with the same name and order.
        var o = config.Orchestrator;
        return new(true, new OrchestratorConfigDto(
            o.Model,
            o.MaxIterations,
            o.MaxRetriesPerTask,
            o.MaxParallelGoals,
            o.VerboseLogging,
            o.BrainMaxSteps,
            o.BranchCleanupDelayHours,
            o.WorkerTaskTimeoutMinutes,
            o.ReasoningEffort), null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<SavedResult>> SaveOrchestratorAsync(OrchestratorSettingsUpdate update)
    {
        var svc = _configModel;
        if (svc is null)
        {
            _log.LogWarning("Config service is not configured.");
            return new(false, null, "Config service is not configured.", FacadeErrorKind.NotConfigured);
        }

        await svc.UpdateOrchestratorSettingsAsync(update);
        return new(true, new SavedResult(true, null), null, FacadeErrorKind.None);
        // Any other exception propagates to the caller — the endpoint never caught it either.
    }

    /// <inheritdoc />
    public FacadeResult<WorkersConfigDto> GetWorkers()
    {
        var config = _hiveConfig;
        if (config is null)
        {
            _log.LogWarning("Config repo is not configured.");
            return new(false, null, "Config repo not configured.", FacadeErrorKind.NotFound);
        }

        // The pre-facade handler projected a TOP-LEVEL role-keyed dictionary; the DTO derives
        // from Dictionary so the JSON shape is identical.
        var workers = new WorkersConfigDto();
        foreach (var kv in config.Workers)
            workers[kv.Key] = new WorkerEntryDto(kv.Value.Model, kv.Value.PremiumModel, kv.Value.ContextWindow);
        return new(true, workers, null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<SavedResult>> SaveWorkersAsync(Dictionary<string, int> contextWindows)
    {
        var svc = _configModel;
        if (svc is null)
        {
            _log.LogWarning("Config service is not configured.");
            return new(false, null, "Config service is not configured.", FacadeErrorKind.NotConfigured);
        }

        await svc.UpdateWorkerContextWindowsAsync(contextWindows);
        return new(true, new SavedResult(true, null), null, FacadeErrorKind.None);
        // Any other exception propagates to the caller — the endpoint never caught it either.
    }

    /// <inheritdoc />
    public FacadeResult<ComposerConfigDto> GetComposer()
    {
        var config = _hiveConfig;
        if (config is null)
        {
            _log.LogWarning("Config repo is not configured.");
            return new(false, null, "Config repo not configured.", FacadeErrorKind.NotFound);
        }

        if (config.Composer is null)
        {
            return new(true, new ComposerConfigDto(
                null,
                Constants.DefaultBrainMaxSteps,
                null,
                new ComposerEventNotificationsDto(
                    "passive",
                    DefaultActiveEvents,
                    ValidActiveEvents,
                    30)), null, FacadeErrorKind.None);
        }

        var composer = config.Composer;
        var notif = composer.EventNotifications;
        var activeTypes = notif?.GetActiveEventTypes();
        // Map through the whitelist order so the response is always canonical and stable.
        var activeEvents = activeTypes is { Count: > 0 }
            ? CanonicalEventOrder.Where(activeTypes.Contains).Select(ToSnakeCase).ToArray()
            : DefaultActiveEvents;

        return new(true, new ComposerConfigDto(
            composer.Model,
            composer.MaxSteps,
            composer.ReasoningEffort,
            new ComposerEventNotificationsDto(
                notif?.EffectiveMode ?? "passive",
                activeEvents,
                ValidActiveEvents,
                notif?.EffectiveThrottleSeconds ?? 30)), null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<SavedResult>> SaveComposerAsync(ComposerSettingsUpdate update, CancellationToken ct)
    {
        var svc = _configModel;
        if (svc is null)
        {
            _log.LogWarning("Config service is not configured.");
            return new(false, null, "Config service is not configured.", FacadeErrorKind.NotConfigured);
        }

        try
        {
            await svc.UpdateComposerSettingsAsync(update, ct);
            return new(true, new SavedResult(true, null), null, FacadeErrorKind.None);
        }
        catch (ArgumentException ex)
        {
            return new(false, null, ex.Message, FacadeErrorKind.BadRequest);
        }
        // OperationCanceledException and any other exception propagate to the caller —
        // the endpoint never caught them either.
    }

    /// <summary>The canonical snake_case names of the four default active events.</summary>
    private static readonly string[] DefaultActiveEvents =
        ["goal_completed", "goal_failed", "ci_failed", "issue_raised"];

    /// <summary>All recognized active event types in canonical whitelist order.</summary>
    private static readonly EventType[] CanonicalEventOrder =
    [
        EventType.GoalCompleted, EventType.GoalFailed, EventType.CiFailed, EventType.IssueRaised,
        EventType.PackagePublished, EventType.CiSucceeded, EventType.ReleaseCompleted,
        EventType.GoalDispatched, EventType.IssueResolved,
    ];

    /// <summary>All recognized active event names in canonical whitelist order.</summary>
    private static readonly string[] ValidActiveEvents =
        CanonicalEventOrder.Select(ToSnakeCase).ToArray();

    /// <summary>
    /// Converts an <see cref="EventType"/> to its canonical snake_case wire form
    /// (e.g. <c>GoalCompleted</c> → <c>"goal_completed"</c>).
    /// </summary>
    private static string ToSnakeCase(EventType type)
    {
        var name = type.ToString();
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}

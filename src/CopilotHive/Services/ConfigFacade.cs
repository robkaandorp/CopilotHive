using CopilotHive.Configuration;

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
    private readonly ILogger<ConfigFacade> _log;

    /// <summary>
    /// Initialises a new <see cref="ConfigFacade"/>.
    /// </summary>
    /// <param name="hiveConfig">The live <see cref="HiveConfigFile"/> singleton (may be <c>null</c> when not registered).</param>
    /// <param name="configModel">The <see cref="ConfigModelService"/> (may be <c>null</c> when no config repo is configured).</param>
    /// <param name="discovery">The <see cref="ModelDiscoveryService"/> (may be <c>null</c> when no config repo is configured).</param>
    /// <param name="log">Logger instance.</param>
    public ConfigFacade(
        HiveConfigFile? hiveConfig,
        ConfigModelService? configModel,
        ModelDiscoveryService? discovery,
        ILogger<ConfigFacade> log)
    {
        _hiveConfig = hiveConfig;
        _configModel = configModel;
        _discovery = discovery;
        _log = log;
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
}

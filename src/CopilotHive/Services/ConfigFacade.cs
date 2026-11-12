using System.Security.Claims;

using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Security;

namespace CopilotHive.Services;

/// <summary>
/// Facade over the model-catalog configuration surface: reading the live model configuration,
/// persisting model changes, discovering provider models, and managing the available/sub-agent
/// model lists. Endpoint handlers depend on this interface instead of reaching into the
/// configuration services directly, so the HTTP layer stays thin and the failure mapping
/// (status codes, problem details, error bodies) lives in one place.
/// </summary>
/// <remarks>
/// <para>
/// Each operation returns a <see cref="FacadeResult{T}"/> whose <see cref="FacadeErrorKind"/>
/// mirrors the HTTP status the endpoint would have returned before the facade existed. A
/// facade method catches ONLY the exception types its previous endpoint handler caught;
/// anything else is rethrown so unexpected failures surface as exceptions instead of being
/// silently converted into a result.
/// </para>
/// <para>
/// AUTHORIZATION IS PART OF EVERY OPERATION, AND IT RUNS FIRST. Each operation takes the caller's
/// per-request (or per-circuit) <see cref="ClaimsPrincipal"/> and re-resolves the acting principal
/// FRESH through <see cref="HivePrincipalResolver"/> — never trusting cookie claims and never
/// retaining a resolved principal between calls — so a demoted, disabled or deleted account is
/// refused on its very NEXT operation. A refusal, or a principal that does not hold the required
/// capability, produces <see cref="FacadeErrorKind.Forbidden"/> BEFORE any configuration read and
/// before any validate → mutate → write → commit work.
/// </para>
/// <para>
/// THE ROLE MAP this facade enforces: EVERY operation — the full configuration projections
/// (<see cref="GetModelsAsync"/>, <see cref="GetRepositoriesAsync"/>, <see cref="GetBranchesAsync"/>,
/// <see cref="GetOrchestratorAsync"/>, <see cref="GetWorkersAsync"/>, <see cref="GetComposerAsync"/>,
/// <see cref="DiscoverModelsAsync"/>) and every write (models, available models, sub-agent models,
/// repositories, orchestrator, workers, Composer) — requires
/// <see cref="HiveCapability.GlobalAdmin"/>. There is no shared-read projection on this surface and
/// deliberately NONE is created here. Open mode resolves the explicit open-mode local operator
/// through the same resolver, so the single-operator installation keeps its previous behaviour
/// exactly.
/// </para>
/// </remarks>
public interface IConfigFacade
{
    /// <summary>
    /// Reads the full model configuration (orchestrator/composer/compaction models, per-role
    /// worker models, reasoning efforts, available models, sub-agent models).
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>The model configuration, or <see cref="FacadeErrorKind.NotFound"/> when no
    /// <see cref="HiveConfigFile"/> is registered.</returns>
    Task<FacadeResult<ModelsConfigDto>> GetModelsAsync(ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Persists a batch of model configuration changes (validate → mutate → write → commit →
    /// live Brain update).
    /// </summary>
    /// <param name="update">The model changes to apply.</param>
    /// <param name="ct">Cancellation token forwarded to the persistence layer.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <returns>
    /// Success with the saved description, <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered, or <see cref="FacadeErrorKind.BadRequest"/>
    /// when the update is invalid. Cancellation and any other exception propagate to the caller.
    /// </returns>
    Task<FacadeResult<SavedResult>> SaveModelsAsync(
        ModelConfigUpdate update, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Discovers models available from all configured providers (Copilot, Ollama).
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// The discovered models, or <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ModelDiscoveryService"/> is registered.
    /// </returns>
    Task<FacadeResult<IReadOnlyList<DiscoveredModelDto>>> DiscoverModelsAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Adds a model to the global <c>available_models</c> list.
    /// </summary>
    /// <param name="request">The model to add.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// Success, <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered, or
    /// <see cref="FacadeErrorKind.Conflict"/> when the name already exists.
    /// </returns>
    Task<FacadeResult<SavedResult>> AddAvailableModelAsync(
        AvailableModelRequest request, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Updates an existing <c>available_models</c> entry by name.
    /// </summary>
    /// <param name="name">Model name to update (already URL-unescaped by the caller).</param>
    /// <param name="request">The new values.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// Success, <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered, or
    /// <see cref="FacadeErrorKind.NotFound"/> when the model does not exist.
    /// </returns>
    Task<FacadeResult<SavedResult>> UpdateAvailableModelAsync(
        string name, AvailableModelRequest request, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Removes a model from the <c>available_models</c> list by name.
    /// </summary>
    /// <param name="name">Model name to remove (already URL-unescaped by the caller).</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// Success, <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered, or
    /// <see cref="FacadeErrorKind.NotFound"/> when the model does not exist.
    /// </returns>
    Task<FacadeResult<RemovedResult>> RemoveAvailableModelAsync(
        string name, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Adds a model to the curated <c>sub_agent_models</c> list.
    /// </summary>
    /// <param name="request">The model to add.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// Success, <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered, or
    /// <see cref="FacadeErrorKind.Conflict"/> when the name already exists.
    /// </returns>
    Task<FacadeResult<SavedResult>> AddSubAgentModelAsync(
        SubAgentModelRequest request, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Updates an existing <c>sub_agent_models</c> entry by name.
    /// </summary>
    /// <param name="name">Model name to update (already URL-unescaped by the caller).</param>
    /// <param name="request">The new values.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// Success, <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered, or
    /// <see cref="FacadeErrorKind.NotFound"/> when the model does not exist.
    /// </returns>
    Task<FacadeResult<SavedResult>> UpdateSubAgentModelAsync(
        string name, SubAgentModelRequest request, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Removes a model from the <c>sub_agent_models</c> list by name.
    /// </summary>
    /// <param name="name">Model name to remove (already URL-unescaped by the caller).</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// Success, <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered, or
    /// <see cref="FacadeErrorKind.NotFound"/> when the model does not exist.
    /// </returns>
    Task<FacadeResult<RemovedResult>> RemoveSubAgentModelAsync(
        string name, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Lists the configured repositories.
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// The configured repositories as DTOs, or <see cref="FacadeErrorKind.NotFound"/> when no
    /// <see cref="HiveConfigFile"/> is registered.
    /// </returns>
    Task<FacadeResult<IReadOnlyList<RepositoryDto>>> GetRepositoriesAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Adds a repository to the config (validate → mutate → write → commit → clone).
    /// </summary>
    /// <param name="request">The repository to add.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// Success, <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered, <see cref="FacadeErrorKind.BadRequest"/>
    /// when the request is invalid, or <see cref="FacadeErrorKind.Conflict"/> when a repository
    /// with the same name already exists. Any other exception propagates to the caller.
    /// </returns>
    Task<FacadeResult<SavedResult>> AddRepositoryAsync(
        RepositoryRequest request, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Lists the remote branches of a repository via the brain repo manager.
    /// </summary>
    /// <param name="name">Repository name (NOT URL-unescaped — the repository routes do not unescape).</param>
    /// <param name="ct">Cancellation token forwarded to the repo manager.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <returns>
    /// The branch names, <see cref="FacadeErrorKind.ServiceUnavailable"/> when no
    /// <see cref="IBrainRepoManager"/> is registered, <see cref="FacadeErrorKind.NotFound"/>
    /// when the repository is not cloned, <see cref="FacadeErrorKind.BadRequest"/> when the
    /// name is invalid, or <see cref="FacadeErrorKind.Internal"/> for any other failure
    /// (including cancellation, matching the pre-facade catch-all).
    /// </returns>
    Task<FacadeResult<IReadOnlyList<string>>> GetBranchesAsync(
        string name, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Updates an existing repository's URL, default branch, and optional release configuration.
    /// </summary>
    /// <param name="name">Repository name to update (NOT URL-unescaped — the repository routes do not unescape).</param>
    /// <param name="request">The new values.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// Success, <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered, <see cref="FacadeErrorKind.BadRequest"/>
    /// when the request is invalid, or <see cref="FacadeErrorKind.NotFound"/> when the
    /// repository does not exist. Any other exception propagates to the caller.
    /// </returns>
    Task<FacadeResult<SavedResult>> UpdateRepositoryAsync(
        string name, RepositoryRequest request, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Removes a repository from the config.
    /// </summary>
    /// <param name="name">Repository name to remove (NOT URL-unescaped — the repository routes do not unescape).</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// Success, <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered, or <see cref="FacadeErrorKind.NotFound"/>
    /// when the repository does not exist.
    /// </returns>
    Task<FacadeResult<RemovedResult>> RemoveRepositoryAsync(
        string name, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Reads the orchestrator-level settings (model, iteration limits, logging, timeouts).
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>The orchestrator settings, or <see cref="FacadeErrorKind.NotFound"/> when no
    /// <see cref="HiveConfigFile"/> is registered.</returns>
    Task<FacadeResult<OrchestratorConfigDto>> GetOrchestratorAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Persists orchestrator-level setting changes (validate → mutate → write → commit).
    /// </summary>
    /// <param name="update">The orchestrator settings to apply.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// Success with the saved result, or <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered. Any other exception propagates to the caller.
    /// </returns>
    Task<FacadeResult<SavedResult>> SaveOrchestratorAsync(
        OrchestratorSettingsUpdate update, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Reads the per-role worker settings (model, premium model, context window) keyed by role.
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>The workers dictionary, or <see cref="FacadeErrorKind.NotFound"/> when no
    /// <see cref="HiveConfigFile"/> is registered.</returns>
    Task<FacadeResult<WorkersConfigDto>> GetWorkersAsync(ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Persists per-role worker context windows (validate → mutate → write → commit).
    /// </summary>
    /// <param name="contextWindows">Role → context window size mapping.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// Success with the saved result, or <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered. Any other exception propagates to the caller.
    /// </returns>
    Task<FacadeResult<SavedResult>> SaveWorkersAsync(
        Dictionary<string, int> contextWindows, ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Reads the runtime-effective Composer settings (model, max steps, reasoning effort, and
    /// the typed event-notifications shape).
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>The Composer settings, or <see cref="FacadeErrorKind.NotFound"/> when no
    /// <see cref="HiveConfigFile"/> is registered.</returns>
    Task<FacadeResult<ComposerConfigDto>> GetComposerAsync(ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Reads the RAW configuration-repo file contents the Configuration page's file tabs render: the
    /// whole <c>hive-config.yaml</c> and every <c>agents/*.agents.md</c> file, verbatim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS IS A FULL CONFIGURATION PROJECTION AND IT REQUIRES
    /// <see cref="HiveCapability.GlobalAdmin"/>. It is deliberately NOT a sanitized projection: no
    /// filtering is applied to the content, so the ONLY thing standing between a non-admin and the
    /// complete configuration is this gate — which is why the gate runs BEFORE the file system is
    /// touched and why a refusal returns no content at all (never a partial or redacted document that
    /// could be mistaken for the real one).
    /// </para>
    /// <para>
    /// The gate exists because rendering these files is a raw full-config read: before this operation
    /// existed, the page read them straight off disk and rendered them to any authenticated role,
    /// bypassing the administration requirement every other configuration projection enforces.
    /// </para>
    /// </remarks>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token; cancellation propagates.</param>
    /// <returns>
    /// The files in the page's existing order (<c>hive-config.yaml</c> first, then the agents files by
    /// name; EMPTY when no config repo is configured or none exist), or
    /// <see cref="FacadeErrorKind.Forbidden"/> when the caller does not currently hold
    /// <see cref="HiveCapability.GlobalAdmin"/>.
    /// </returns>
    Task<FacadeResult<IReadOnlyList<ConfigFileContentDto>>> GetConfigFilesAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct);

    /// <summary>
    /// Persists Composer setting changes (validate → mutate → write → commit).
    /// </summary>
    /// <remarks>
    /// The GLOBAL Composer write is an administration operation: EVERY shape of the update — an
    /// empty update, a MaxSteps-only update and a notification-only update alike — requires
    /// <see cref="HiveCapability.GlobalAdmin"/> for the caller re-resolved FRESH from current stored
    /// state. A non-admin (including a plain Member) is refused with
    /// <see cref="FacadeErrorKind.Forbidden"/> before any persistence work runs. Open mode resolves
    /// the local operator and therefore succeeds exactly as before.
    /// </remarks>
    /// <param name="update">The Composer settings to apply.</param>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="ct">Cancellation token forwarded to the persistence layer.</param>
    /// <returns>
    /// Success with the saved result, <see cref="FacadeErrorKind.Forbidden"/> when the caller does
    /// not currently hold <see cref="HiveCapability.GlobalAdmin"/>,
    /// <see cref="FacadeErrorKind.BadRequest"/> when the update is invalid (e.g. an unknown
    /// notification mode or event name), or <see cref="FacadeErrorKind.NotConfigured"/> when no
    /// <see cref="ConfigModelService"/> is registered. Cancellation and any other exception
    /// propagate to the caller.
    /// </returns>
    Task<FacadeResult<SavedResult>> SaveComposerAsync(
        ComposerSettingsUpdate update, ClaimsPrincipal? actingUser, CancellationToken ct);
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
    private readonly ConfigRepoManager? _configRepo;
    private readonly HivePrincipalResolver _resolver;
    private readonly HivePolicy _policy;
    private readonly ILogger<ConfigFacade> _log;

    /// <summary>
    /// Initialises a new <see cref="ConfigFacade"/>.
    /// </summary>
    /// <param name="hiveConfig">The live <see cref="HiveConfigFile"/> singleton (may be <c>null</c> when not registered).</param>
    /// <param name="configModel">The <see cref="ConfigModelService"/> (may be <c>null</c> when no config repo is configured).</param>
    /// <param name="discovery">The <see cref="ModelDiscoveryService"/> (may be <c>null</c> when no config repo is configured).</param>
    /// <param name="log">Logger instance.</param>
    /// <param name="resolver">
    /// The stateless principal resolver the GLOBAL Composer write re-resolves through. It is a
    /// REQUIRED dependency so there is no unauthenticated construction: the resolver and the policy
    /// are stateless singletons, and this facade never captures the scoped circuit accessor.
    /// </param>
    /// <param name="policy">The pure capability evaluator (required together with <paramref name="resolver"/>).</param>
    /// <param name="repoManager">The <see cref="IBrainRepoManager"/> (may be <c>null</c> when not registered).</param>
    /// <param name="configRepo">
    /// The <see cref="ConfigRepoManager"/> whose local clone the raw configuration-file read
    /// (<see cref="GetConfigFilesAsync"/>) projects (may be <c>null</c> when no config repo was
    /// configured at startup, in which case that read reports no files).
    /// </param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public ConfigFacade(
        HiveConfigFile? hiveConfig,
        ConfigModelService? configModel,
        ModelDiscoveryService? discovery,
        ILogger<ConfigFacade> log,
        HivePrincipalResolver resolver,
        HivePolicy policy,
        IBrainRepoManager? repoManager = null,
        ConfigRepoManager? configRepo = null)
    {
        _hiveConfig = hiveConfig;
        _configModel = configModel;
        _discovery = discovery;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _repoManager = repoManager;
        _configRepo = configRepo;
    }

    /// <inheritdoc />
    public async Task<FacadeResult<ModelsConfigDto>> GetModelsAsync(ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(GetModelsAsync), ct).ConfigureAwait(false))
            return Forbidden<ModelsConfigDto>();

        var config = _hiveConfig;
        if (config is null)
        {
            _log.LogWarning("Config repo is not configured.");
            return new(false, null, "Config repo not configured.", FacadeErrorKind.NotFound);
        }

        // ONE detached capture of the whole configuration graph under the config's catalog
        // lock: every DTO field below is projected from this single snapshot, so the response
        // can never mix reload generations — a concurrent ReloadFrom or synchronized catalog
        // mutation commits only BETWEEN captures, never between the fields of one response.
        // Deliberately NOT GetSubAgentModels: its curated/available fallback-merge semantics
        // would change this raw-configuration response (the DTO must show the curated entries
        // exactly as stored).
        var snapshot = config.CaptureConfigSnapshot();

        // Reasoning effort is stored as string? in the YAML-bound config classes but is
        // projected here as the ReasoningEffort enum. The global JsonStringEnumConverter
        // renders it snake_case (e.g. "extra_high"). A value a dynamic reload left
        // unrecognised degrades to null rather than failing the whole response.
        return new(true, new ModelsConfigDto(
            Orchestrator: snapshot.Orchestrator!.Model,
            Composer: snapshot.Composer?.Model,
            Compaction: snapshot.Models?.CompactionModel,
            Workers: snapshot.Workers!.ToDictionary(
                kv => kv.Key,
                kv => new WorkerModelsDto(kv.Value!.Model, kv.Value!.PremiumModel)),
            OrchestratorReasoningEffort: ConfigModelService.ParseLenient(snapshot.Orchestrator!.ReasoningEffort),
            ComposerReasoningEffort: ConfigModelService.ParseLenient(snapshot.Composer?.ReasoningEffort),
            WorkerReasoningEffort: snapshot.Workers!.ToDictionary(
                kv => kv.Key,
                kv => ConfigModelService.ParseLenient(kv.Value!.ReasoningEffort)),
            WorkerPremiumReasoningEffort: snapshot.Workers!.ToDictionary(
                kv => kv.Key,
                kv => ConfigModelService.ParseLenient(kv.Value!.PremiumReasoningEffort)),
            SubAgentModelReasoning: snapshot.Models?.SubAgentModels?
                .Where(m => !string.IsNullOrEmpty(m.Name))
                .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => ConfigModelService.ParseLenient(g.First().ReasoningEffort)),
            AvailableModels: snapshot.Models?.AvailableModels?
                .Select(m => new AvailableModelDto(m.Name, m.ContextWindow, m.Description, m.SupportsVision))
                .ToList(),
            // Projected entry-by-entry rather than returned as raw ModelEntry objects:
            // ModelEntry.ReasoningEffort is deliberately string? at the YAML boundary, so
            // serializing the entity directly would leak a raw string (and an unrecognised
            // stored value such as "turbo" verbatim) into an otherwise enum-typed response.
            SubAgentModels: snapshot.Models?.SubAgentModels?
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
    public async Task<FacadeResult<SavedResult>> SaveModelsAsync(
        ModelConfigUpdate update, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(SaveModelsAsync), ct).ConfigureAwait(false))
            return Forbidden<SavedResult>();

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
    public async Task<FacadeResult<IReadOnlyList<DiscoveredModelDto>>> DiscoverModelsAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(DiscoverModelsAsync), ct).ConfigureAwait(false))
            return Forbidden<IReadOnlyList<DiscoveredModelDto>>();

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
    public async Task<FacadeResult<SavedResult>> AddAvailableModelAsync(
        AvailableModelRequest request, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(AddAvailableModelAsync), ct).ConfigureAwait(false))
            return Forbidden<SavedResult>();

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
    public async Task<FacadeResult<SavedResult>> UpdateAvailableModelAsync(
        string name, AvailableModelRequest request, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(UpdateAvailableModelAsync), ct).ConfigureAwait(false))
            return Forbidden<SavedResult>();

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
    public async Task<FacadeResult<RemovedResult>> RemoveAvailableModelAsync(
        string name, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(RemoveAvailableModelAsync), ct).ConfigureAwait(false))
            return Forbidden<RemovedResult>();

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
    public async Task<FacadeResult<SavedResult>> AddSubAgentModelAsync(
        SubAgentModelRequest request, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(AddSubAgentModelAsync), ct).ConfigureAwait(false))
            return Forbidden<SavedResult>();

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
    public async Task<FacadeResult<SavedResult>> UpdateSubAgentModelAsync(
        string name, SubAgentModelRequest request, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(UpdateSubAgentModelAsync), ct).ConfigureAwait(false))
            return Forbidden<SavedResult>();

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
    public async Task<FacadeResult<RemovedResult>> RemoveSubAgentModelAsync(
        string name, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(RemoveSubAgentModelAsync), ct).ConfigureAwait(false))
            return Forbidden<RemovedResult>();

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
    public async Task<FacadeResult<IReadOnlyList<RepositoryDto>>> GetRepositoriesAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(GetRepositoriesAsync), ct).ConfigureAwait(false))
            return Forbidden<IReadOnlyList<RepositoryDto>>();

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
    public async Task<FacadeResult<SavedResult>> AddRepositoryAsync(
        RepositoryRequest request, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(AddRepositoryAsync), ct).ConfigureAwait(false))
            return Forbidden<SavedResult>();

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
    public async Task<FacadeResult<IReadOnlyList<string>>> GetBranchesAsync(
        string name, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(GetBranchesAsync), ct).ConfigureAwait(false))
            return Forbidden<IReadOnlyList<string>>();

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
    public async Task<FacadeResult<SavedResult>> UpdateRepositoryAsync(
        string name, RepositoryRequest request, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(UpdateRepositoryAsync), ct).ConfigureAwait(false))
            return Forbidden<SavedResult>();

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
    public async Task<FacadeResult<RemovedResult>> RemoveRepositoryAsync(
        string name, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(RemoveRepositoryAsync), ct).ConfigureAwait(false))
            return Forbidden<RemovedResult>();

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
    public async Task<FacadeResult<OrchestratorConfigDto>> GetOrchestratorAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(GetOrchestratorAsync), ct).ConfigureAwait(false))
            return Forbidden<OrchestratorConfigDto>();

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
    public async Task<FacadeResult<SavedResult>> SaveOrchestratorAsync(
        OrchestratorSettingsUpdate update, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(SaveOrchestratorAsync), ct).ConfigureAwait(false))
            return Forbidden<SavedResult>();

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
    public async Task<FacadeResult<WorkersConfigDto>> GetWorkersAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(GetWorkersAsync), ct).ConfigureAwait(false))
            return Forbidden<WorkersConfigDto>();

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
    public async Task<FacadeResult<SavedResult>> SaveWorkersAsync(
        Dictionary<string, int> contextWindows, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(SaveWorkersAsync), ct).ConfigureAwait(false))
            return Forbidden<SavedResult>();

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
    public async Task<FacadeResult<ComposerConfigDto>> GetComposerAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        if (!await IsAllowedAsync(actingUser, nameof(GetComposerAsync), ct).ConfigureAwait(false))
            return Forbidden<ComposerConfigDto>();

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
    public async Task<FacadeResult<IReadOnlyList<ConfigFileContentDto>>> GetConfigFilesAsync(
        ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        // AUTHORIZATION FIRST, before the file system is touched at all: the raw configuration files
        // are a FULL projection, so a non-admin learns nothing — not even whether the repo exists or
        // which files it holds.
        if (!await IsAllowedAsync(actingUser, nameof(GetConfigFilesAsync), ct).ConfigureAwait(false))
            return Forbidden<IReadOnlyList<ConfigFileContentDto>>();

        // No config repo configured: there are no files to project. This is a SUCCESS with an empty
        // list, exactly the state the page rendered when its clone directory did not exist.
        var configRepo = _configRepo;
        if (configRepo is null)
            return new(true, [], null, FacadeErrorKind.None);

        var files = new List<ConfigFileContentDto>();

        // hive-config.yaml FIRST — the same order the page's tab list has always used.
        var configPath = Path.Combine(configRepo.LocalPath, "hive-config.yaml");
        if (File.Exists(configPath))
            files.Add(new ConfigFileContentDto("hive-config.yaml", await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false)));

        // …then agents/*.agents.md, ordered by file name.
        var agentsDir = Path.Combine(configRepo.LocalPath, "agents");
        if (Directory.Exists(agentsDir))
        {
            foreach (var path in Directory.GetFiles(agentsDir, "*.agents.md").OrderBy(f => f, StringComparer.Ordinal))
            {
                files.Add(new ConfigFileContentDto(
                    Path.GetFileName(path),
                    await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)));
            }
        }

        return new(true, files, null, FacadeErrorKind.None);
    }

    /// <inheritdoc />
    public async Task<FacadeResult<SavedResult>> SaveComposerAsync(
        ComposerSettingsUpdate update, ClaimsPrincipal? actingUser, CancellationToken ct)
    {
        // AUTHORIZATION FIRST, and for EVERY update shape — empty, MaxSteps-only or
        // notification-only. The GLOBAL Composer write is an administration operation, so the
        // caller is re-resolved FRESH from current stored state on every call: a demoted or
        // disabled account is refused on the very next call, and no principal is retained here.
        var authorized = await FacadeAuthorization.IsAllowedAsync(
            _resolver,
            _policy,
            actingUser,
            HiveCapability.GlobalAdmin,
            _log,
            nameof(SaveComposerAsync),
            ct).ConfigureAwait(false);

        if (!authorized)
            return new(false, null, FacadeAuthorization.ForbiddenMessage, FacadeErrorKind.Forbidden);

        var svc = _configModel;
        if (svc is null)
        {
            _log.LogWarning("Config service is not configured.");
            return new(false, null, "Config service is not configured.", FacadeErrorKind.NotConfigured);
        }

        try
        {
            await svc.UpdateComposerSettingsAsync(update, ct).ConfigureAwait(false);
            return new(true, new SavedResult(true, null), null, FacadeErrorKind.None);
        }
        catch (ArgumentException ex)
        {
            return new(false, null, ex.Message, FacadeErrorKind.BadRequest);
        }
        // OperationCanceledException and any other exception propagate to the caller —
        // the endpoint never caught them either.
    }

    // ── Authorization + shared results ──────────────────────────────────────

    /// <summary>
    /// THE ONE AUTHORIZATION CALL every non-Composer operation makes, BEFORE any of its own work:
    /// one fresh resolution of the caller's CURRENT stored state and one
    /// <see cref="HiveCapability.GlobalAdmin"/> evaluation.
    /// <para>
    /// EVERY configuration projection and every configuration write in this facade — the model
    /// catalog, the available/sub-agent model lists, the repositories (including the branches read),
    /// the orchestrator settings, the worker settings and the Composer settings — is a GLOBAL
    /// administration surface, so it requires the administration capability and nothing less. No
    /// sanitized shared-read projection is created here.
    /// </para>
    /// </summary>
    /// <param name="actingUser">The per-request/per-circuit identity, or <c>null</c> when signed out.</param>
    /// <param name="operation">The operation name (log context only).</param>
    /// <param name="ct">Caller cancellation token; cancellation propagates, never a denial.</param>
    /// <returns><c>true</c> only when the caller currently holds <see cref="HiveCapability.GlobalAdmin"/>.</returns>
    private Task<bool> IsAllowedAsync(ClaimsPrincipal? actingUser, string operation, CancellationToken ct) =>
        FacadeAuthorization.IsAllowedAsync(
            _resolver, _policy, actingUser, HiveCapability.GlobalAdmin, _log, operation, ct);

    /// <summary>Builds the shared denial result for a value-producing operation.</summary>
    /// <typeparam name="T">The facade value type of the operation.</typeparam>
    /// <returns>A <see cref="FacadeErrorKind.Forbidden"/> result carrying the sanitized denial text.</returns>
    private static FacadeResult<T> Forbidden<T>() =>
        new(false, default, FacadeAuthorization.ForbiddenMessage, FacadeErrorKind.Forbidden);

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

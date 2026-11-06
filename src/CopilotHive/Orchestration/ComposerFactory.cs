using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Security;
using CopilotHive.Services;
using Microsoft.Extensions.AI;

namespace CopilotHive.Orchestration;

/// <summary>
/// The IMMUTABLE per-runtime construction inputs resolved ONCE by <see cref="ComposerFactory.Build"/>: the
/// primary model the <see cref="Composer"/> was constructed with and the compaction model handed to its
/// constructor.
/// </summary>
/// <remarks>
/// This record exists so that a per-user runtime's deferral decision can never diverge from the inputs its
/// Composer was ACTUALLY constructed with: both the constructor argument and the
/// <see cref="ComposerFactory.ShouldDeferConnect"/> predicate read THIS capture, so a later
/// <see cref="HiveConfigFile"/> reload (which live-updates the singleton) cannot flip the decision of an
/// already-constructed runtime.
/// </remarks>
internal sealed record ComposerConstructionSnapshot
{
    /// <summary>Captures the resolved construction inputs.</summary>
    /// <param name="primaryModel">
    /// The resolver's default model (<see cref="HiveConfigFile.ResolveComposerDefaultModel"/>), normalized exactly like
    /// the <see cref="Composer"/> constructor normalizes it, so it equals <see cref="Composer.StartupDefaultModel"/>.
    /// </param>
    /// <param name="compactionModel">
    /// The compaction model value read from configuration. Kept VERBATIM: it is the exact value handed to the
    /// Composer constructor, so whitespace is preserved (the runtime's construction inputs stay byte-equivalent).
    /// </param>
    internal ComposerConstructionSnapshot(string? primaryModel, string? compactionModel)
    {
        PrimaryModel = string.IsNullOrWhiteSpace(primaryModel) ? null : primaryModel;
        CompactionModel = compactionModel;
    }

    /// <summary>
    /// The resolved primary model — the value handed to the Composer constructor, i.e.
    /// <see cref="Composer.StartupDefaultModel"/>; <c>null</c> for a model-less (disconnected shell) runtime.
    /// </summary>
    public string? PrimaryModel { get; }

    /// <summary>The compaction model handed to the Composer constructor, verbatim; <c>null</c> when unset.</summary>
    public string? CompactionModel { get; }
}

/// <summary>One built <see cref="Composer"/> together with the construction inputs it was built from.</summary>
/// <param name="Composer">The constructed Composer.</param>
/// <param name="Configuration">The construction-captured configuration (the same values the Composer was built with).</param>
internal sealed record ComposerBuildResult(Composer Composer, ComposerConstructionSnapshot Configuration);

/// <summary>
/// The ONE shared construction seam for <see cref="Composer"/>: it resolves the Composer's default
/// model, context window, step budget, available models and reasoning effort from configuration and
/// wires the full dependency list. Both the legacy DI singleton (<see cref="CreateLegacy"/>) and the
/// per-user runtime manager (<see cref="CreateForUser"/>) go through <see cref="Build"/>, so no
/// resolution logic exists anywhere else.
/// </summary>
public sealed class ComposerFactory
{
    private readonly IServiceProvider _sp;
    private readonly string _stateDir;
    private readonly string? _ollamaApiKey;
    private readonly Func<string, IChatClient>? _chatClientFactory;
    private readonly Func<bool> _isTokenAvailable;
    private readonly Func<string, string> _providerOf;

    /// <summary>Creates the factory.</summary>
    /// <param name="sp">Provider the dependencies are resolved from (also handed to the Composer for lazy resolution).</param>
    /// <param name="stateDir">Runtime state directory.</param>
    /// <param name="ollamaApiKey">Optional Ollama API key enabling the web tools.</param>
    /// <param name="chatClientFactory">Optional chat-client override (tests); <c>null</c> keeps the Composer default.</param>
    /// <param name="isTokenAvailable">Optional token probe override (tests); defaults to <c>ChatClientFactory.IsTokenAvailable</c>.</param>
    /// <param name="providerOf">Optional provider resolver override (tests); defaults to <c>ChatClientFactory.ParseProviderAndModel</c>.</param>
    public ComposerFactory(
        IServiceProvider sp, string stateDir, string? ollamaApiKey, Func<string, IChatClient>? chatClientFactory = null,
        Func<bool>? isTokenAvailable = null, Func<string, string>? providerOf = null)
    {
        ArgumentNullException.ThrowIfNull(sp);
        ArgumentNullException.ThrowIfNull(stateDir);
        _sp = sp;
        _stateDir = stateDir;
        _ollamaApiKey = ollamaApiKey;
        _chatClientFactory = chatClientFactory;
        _isTokenAvailable = isTokenAvailable ?? SharpCoder.Providers.ChatClientFactory.IsTokenAvailable;
        _providerOf = providerOf ?? (static model => SharpCoder.Providers.ChatClientFactory.ParseProviderAndModel(model).Item1);
    }

    /// <summary>
    /// Whether the runtime built from <paramref name="configuration"/> would currently have its connect DEFERRED
    /// (OAuth on, a Copilot-backed primary/compaction model, no token yet). It evaluates the legacy coordinator's
    /// PURE deferral predicate (<c>LlmConnectionCoordinator.ShouldDefer</c> static form) over
    /// <paramref name="configuration"/>'s CONSTRUCTION-CAPTURED models — never a live
    /// <see cref="HiveConfigFile"/> compaction lookup — so a later config reload can never change the deferral
    /// decision of an already-constructed runtime. It never touches a coordinator instance, the token-available
    /// signal, or any retry loop.
    /// </summary>
    /// <param name="configuration">The construction-captured inputs of the runtime being asked about.</param>
    internal bool ShouldDeferConnect(ComposerConstructionSnapshot configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return LlmConnectionCoordinator.ShouldDefer(
            _sp.GetService<HiveAuthMode>()?.AuthEnabled ?? false,
            configuration.PrimaryModel,
            configuration.CompactionModel,
            _isTokenAvailable,
            _providerOf,
            _sp.GetService<ILogger<ComposerRuntimeManager>>());
    }

    /// <summary>
    /// Builds the legacy single-admin Composer: <c>userState</c> null, the legacy root attachment
    /// service and the legacy shared event subscriber, exactly as resolved from DI. Legacy construction and its
    /// startup inputs are unchanged: its deferral is evaluated once by the legacy coordinator, not by this factory.
    /// </summary>
    public Composer CreateLegacy() =>
        Build(userState: null, _sp.GetService<ComposerAttachmentService>(), _sp.GetService<ComposerEventSubscriber>()).Composer;

    /// <summary>
    /// Builds a Composer bound to one validated per-user identity and its own attachment service / subscriber,
    /// together with the construction-captured configuration its deferral decisions are evaluated against.
    /// </summary>
    internal ComposerBuildResult CreateForUser(
        ComposerUserState userState, ComposerAttachmentService attachments, ComposerEventSubscriber? subscriber) =>
        Build(userState ?? throw new ArgumentNullException(nameof(userState)), attachments, subscriber);

    /// <summary>Creates the per-user attachment service rooted at <c>{stateDir}/composer/users/N/attachments</c>.</summary>
    internal ComposerAttachmentService CreateUserAttachments(ComposerUserState userState) =>
        new(_stateDir, userState, _sp.GetRequiredService<ILogger<ComposerAttachmentService>>());

    /// <summary>Creates a fresh per-runtime event subscriber, or <c>null</c> when no event bus is registered.</summary>
    internal ComposerEventSubscriber? CreateUserSubscriber() =>
        _sp.GetService<IEventBus>() is { } bus ? new ComposerEventSubscriber(bus) : null;

    /// <summary>Removes <paramref name="userState"/>'s <c>composer:N</c> row from the session registry, if any.</summary>
    internal void UnregisterUser(ComposerUserState userState) =>
        _sp.GetService<LlmSessionRegistry>()?.Unregister(ComposerUserState.RegistryId(userState));

    private ComposerBuildResult Build(
        ComposerUserState? userState, ComposerAttachmentService? attachmentService, ComposerEventSubscriber? eventSubscriber)
    {
        var config = _sp.GetRequiredService<HiveConfigFile>();

        // ONE resolution of the Composer's default model (Slice 1A1 resolver): the
        // resolved value is used BOTH for construction and (via Composer.StartupDefaultModel)
        // for the startup-connect gate. No fall-through to orchestrator.model /
        // Constants.DefaultWorkerModel — resolver-only construction.
        var resolvedDefault = config.ResolveComposerDefaultModel();

        // ONE read of the compaction model, captured together with the primary model in the immutable
        // per-runtime configuration. BOTH the Composer's constructor argument and every later
        // ShouldDeferConnect evaluation read THIS capture, so a config reload can never make the
        // deferral decision diverge from the inputs this runtime was actually constructed with.
        var constructionConfiguration = new ComposerConstructionSnapshot(resolvedDefault, config.GetCompactionModel());

        var maxCtx = resolvedDefault is not null
            ? config.TryGetContextWindowForModel(resolvedDefault)
                ?? Constants.DefaultBrainContextWindow
            : Constants.DefaultBrainContextWindow;
        var maxSteps = config.Composer?.MaxSteps ?? config.Orchestrator.BrainMaxSteps;
        var availableModels = config.GetComposerAvailableModels();

        var composer = new Composer(resolvedDefault, _sp.GetRequiredService<ILogger<Composer>>(),
            _sp.GetRequiredService<IGoalStore>(),
            maxCtx, maxSteps,
            _sp.GetService<IBrainRepoManager>(),
            _stateDir,
            _sp, // IServiceProvider — lazy resolution of GoalDispatcher to avoid circular DI
            !string.IsNullOrWhiteSpace(_ollamaApiKey) ? _sp.GetRequiredService<IHttpClientFactory>() : null,
            _ollamaApiKey,
            config,
            _sp.GetService<ConfigRepoManager>(),
            availableModels,
            chatClientFactory: _chatClientFactory,
            compactionModel: constructionConfiguration.CompactionModel,
            knowledgeGraph: _sp.GetService<KnowledgeGraph>(),
            goalReviewService: _sp.GetService<GoalReviewService>(),
            sessionRegistry: _sp.GetService<LlmSessionRegistry>(),
            goalReadyNotifier: _sp.GetService<GoalReadyNotifier>(),
            attachmentService: attachmentService,
            reasoningEffort: Program.ParseConfiguredReasoningEffort(
                // Composer-specific override → orchestrator reasoning effort.
                !string.IsNullOrWhiteSpace(config.Composer?.ReasoningEffort)
                    ? config.Composer.ReasoningEffort
                    : config.Orchestrator?.ReasoningEffort,
                "composer.reasoning_effort",
                _sp.GetService<ILogger<Composer>>()),
            issueStore: _sp.GetService<IIssueStore>(),
            eventSubscriber: eventSubscriber,
            eventBus: _sp.GetService<IEventBus>(),
            userState: userState);

        return new ComposerBuildResult(composer, constructionConfiguration);
    }
}

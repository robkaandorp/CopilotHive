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
    /// Whether <paramref name="composer"/>'s connect would currently be DEFERRED (OAuth on, a Copilot-backed
    /// model, no token yet). It evaluates the legacy coordinator's PURE deferral predicate
    /// (<c>LlmConnectionCoordinator.ShouldDefer</c> static form) over this installation's inputs — it never
    /// touches a coordinator instance, the token-available signal, or any retry loop.
    /// </summary>
    internal bool ShouldDeferConnect(Composer composer) =>
        LlmConnectionCoordinator.ShouldDefer(
            _sp.GetService<HiveAuthMode>()?.AuthEnabled ?? false,
            composer.StartupDefaultModel,
            _sp.GetRequiredService<HiveConfigFile>().GetCompactionModel(),
            _isTokenAvailable,
            _providerOf,
            _sp.GetService<ILogger<ComposerRuntimeManager>>());

    /// <summary>
    /// Builds the legacy single-admin Composer: <c>userState</c> null, the legacy root attachment
    /// service and the legacy shared event subscriber, exactly as resolved from DI.
    /// </summary>
    public Composer CreateLegacy() =>
        Build(userState: null, _sp.GetService<ComposerAttachmentService>(), _sp.GetService<ComposerEventSubscriber>());

    /// <summary>Builds a Composer bound to one validated per-user identity and its own attachment service / subscriber.</summary>
    internal Composer CreateForUser(
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

    private Composer Build(
        ComposerUserState? userState, ComposerAttachmentService? attachmentService, ComposerEventSubscriber? eventSubscriber)
    {
        var config = _sp.GetRequiredService<HiveConfigFile>();

        // ONE resolution of the Composer's default model (Slice 1A1 resolver): the
        // resolved value is used BOTH for construction and (via Composer.StartupDefaultModel)
        // for the startup-connect gate. No fall-through to orchestrator.model /
        // Constants.DefaultWorkerModel — resolver-only construction.
        var resolvedDefault = config.ResolveComposerDefaultModel();

        var maxCtx = resolvedDefault is not null
            ? config.TryGetContextWindowForModel(resolvedDefault)
                ?? Constants.DefaultBrainContextWindow
            : Constants.DefaultBrainContextWindow;
        var maxSteps = config.Composer?.MaxSteps ?? config.Orchestrator.BrainMaxSteps;
        var availableModels = config.GetComposerAvailableModels();

        return new Composer(resolvedDefault, _sp.GetRequiredService<ILogger<Composer>>(),
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
            compactionModel: config.GetCompactionModel(),
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
    }
}

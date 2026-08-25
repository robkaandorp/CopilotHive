using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Services;

using Microsoft.Extensions.AI;

using SharpCoder;
using SharpCoder.Providers;
using SharpCoder.SubAgents;

using System.Text.Json;

namespace CopilotHive.Orchestration;

/// <summary>
/// Owns the Composer's LLM connection lifecycle: chat clients, the <see cref="CodingAgent"/>,
/// the persistent <see cref="AgentSession"/>, and the <see cref="AgentOptions"/> used to build them.
/// </summary>
internal sealed class ComposerAgentService(
    string? model,
    int maxContextTokens,
    int maxSteps,
    ReasoningEffort? configuredReasoningEffort,
    HiveConfigFile? hiveConfig,
    string systemPrompt,
    List<AITool> composerTools,
    IBrainRepoManager? repoManager,
    string stateDir,
    string? compactionModel,
    ILogger logger,
    Func<string, IChatClient>? chatClientFactory,
    LlmSessionRegistry? sessionRegistry,
    IReadOnlyList<string> startupAvailableModels,
    Action? onCompacting,
    Action<CompactionResult>? onCompacted,
    bool subAgentsEnabled,
    IReadOnlyList<ModelEntry> subAgentModels,
    string? additionalImagesRoot = null) : IAsyncDisposable
{
    private string? _model = string.IsNullOrWhiteSpace(model) ? null : model;
    private int _maxContextTokens = maxContextTokens;
    private readonly int _maxSteps = maxSteps;
    /// <summary>
    /// The explicitly configured reasoning effort. Replaced by every explicit model switch.
    /// </summary>
    private ReasoningEffort? _configuredReasoningEffort = configuredReasoningEffort;

    /// <summary>The effective reasoning effort — always the configured value.</summary>
    private ReasoningEffort? _reasoningEffort = configuredReasoningEffort;
    private readonly HiveConfigFile? _hiveConfig = hiveConfig;
    private readonly string _systemPrompt = systemPrompt;
    private readonly List<AITool> _composerTools = composerTools;
    private readonly IBrainRepoManager? _repoManager = repoManager;
    private readonly string _stateDir = stateDir;
    private readonly string? _compactionModel = compactionModel;
    private readonly ILogger _logger = logger;
    private readonly Func<string, IChatClient>? _chatClientFactory = chatClientFactory;
    private readonly LlmSessionRegistry? _sessionRegistry = sessionRegistry;
    private readonly IReadOnlyList<string> _startupAvailableModels = startupAvailableModels;
    private readonly Action? _onCompacting = onCompacting;
    private readonly Action<CompactionResult>? _onCompacted = onCompacted;
    private readonly bool _subAgentsEnabled = subAgentsEnabled;
    private readonly string? _additionalImagesRoot = additionalImagesRoot;

    /// <summary>
    /// Immutable construction-time snapshot of the sub-agent model catalog. Deliberately NOT read
    /// from <c>_hiveConfig</c>, which is a mutable singleton live-updated by config reloads — a
    /// reload must never desynchronise the already-appended sub-agent prompt section from the
    /// catalog handed to <see cref="SubAgentOptions"/>.
    /// </summary>
    private readonly IReadOnlyList<ModelEntry> _subAgentModels = subAgentModels;

    private IChatClient? _chatClient;
    private IChatClient? _compactionChatClient;
    private CodingAgent? _agent;
    private AgentSession _session = AgentSession.Create("composer");
    private AgentOptions? _agentOptions;

    /// <summary>
    /// Whether the current session was loaded from disk during <see cref="ConnectAsync"/>
    /// and the connection succeeded. False for fresh sessions or failed connections.
    /// </summary>
    private bool _sessionLoadedFromDisk;

    /// <summary>
    /// Channel-fed consumer holding the current sub-agent status snapshot. Created lazily when the
    /// agent is (re)created and torn down with the rest of the connection state.
    /// </summary>
    private SubAgentStateTracker? _subAgentTracker;

    /// <summary>
    /// Raised when a sub-agent starts or reaches a terminal state, carrying a defensive clone.
    /// Forwarded from <see cref="SubAgentStateTracker.OnSubAgentChanged"/>.
    /// </summary>
    public event Action<SubAgentInfo>? OnSubAgentChanged;

    /// <summary>
    /// Returns the current sub-agent entries (running first, then most recent terminal ones), or an
    /// empty list when no agent has been created yet.
    /// </summary>
    public IReadOnlyList<SubAgentInfo> GetSubAgents() => _subAgentTracker?.GetSubAgents() ?? [];

    /// <summary>
    /// Agent-level handler for <see cref="CodingAgent.SubAgentChanged"/>. Declared as a method (not
    /// a lambda) so the exact same delegate can be removed with <c>-=</c> on every teardown path.
    /// </summary>
    private void HandleSubAgentChanged(SubAgentInfo info) => _subAgentTracker?.Post(info);

    /// <summary>Re-raises the tracker's event to this service's subscribers.</summary>
    private void ForwardSubAgentChanged(SubAgentInfo info) => OnSubAgentChanged?.Invoke(info);

    /// <summary>
    /// Unsubscribes from and stops the current tracker, then clears the field so the panel reads as
    /// empty until a new agent creates a replacement. Idempotent: a no-op when no tracker exists, so
    /// the nested calls from <see cref="DisposeClientsAndClearStateAsync"/> cannot double-stop.
    /// </summary>
    private async Task StopAndClearTrackerAsync()
    {
        var tracker = _subAgentTracker;
        if (tracker is null)
            return;

        _subAgentTracker = null;

        tracker.OnSubAgentChanged -= ForwardSubAgentChanged;

        // StopAsync logs (and never rethrows) reader failures, so it cannot mask a disposal error.
        await tracker.StopAsync();
    }

    /// <summary>
    /// Invoked immediately before each <see cref="CodingAgent.DisposeAsync"/> call.
    /// If the hook throws, the exception is captured and rethrown (or aggregated with a
    /// disposal failure) after agent disposal completes.
    /// </summary>
    internal Action<CodingAgent>? OnAgentDisposing;

    /// <summary>The active chat client, or <c>null</c> when not connected.</summary>
    public IChatClient? ChatClient => _chatClient;

    /// <summary>The active coding agent, or <c>null</c> when not connected.</summary>
    public CodingAgent? Agent => _agent;

    /// <summary>The current persistent session (never <c>null</c>).</summary>
    public AgentSession Session => _session;

    /// <summary>The agent options used to build the current agent.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the agent has not been created yet.</exception>
    public AgentOptions AgentOptions => _agentOptions
        ?? throw new InvalidOperationException("AgentOptions not yet created. Call ConnectAsync or RecreateAgentAsync first.");

    /// <summary>Whether both the chat client and the agent exist.</summary>
    public bool IsConnected => _chatClient is not null && _agent is not null;

    /// <summary>
    /// Whether the current session was loaded from disk during <see cref="ConnectAsync"/>
    /// and the connection succeeded. False for fresh sessions or failed connections.
    /// </summary>
    public bool SessionLoadedFromDisk => _sessionLoadedFromDisk;

    /// <summary>The current model identifier, or <c>null</c> when no model is configured.</summary>
    public string? Model => _model;

    /// <summary>The current maximum context window in tokens.</summary>
    public int MaxContextTokens => _maxContextTokens;

    /// <summary>The maximum number of agent steps per run.</summary>
    public int MaxSteps => _maxSteps;

    /// <summary>The current reasoning effort, if any.</summary>
    public ReasoningEffort? ReasoningEffort => _reasoningEffort;

    /// <summary>
    /// Models the Composer can switch between at runtime. Reads the normalized catalog from
    /// <see cref="HiveConfigFile.GetComposerAvailableModels"/> when a config is present (sole
    /// authority — an empty global list yields an EMPTY catalog, never a fallback). When no
    /// config is present (direct test construction only), the constructor-provided startup
    /// catalog is used.
    /// </summary>
    public IReadOnlyList<string> AvailableModels =>
        _hiveConfig is not null
            ? _hiveConfig.GetComposerAvailableModels()
            : _startupAvailableModels;

    private IChatClient CreateClient(string modelId) => (_chatClientFactory ?? ChatClientFactory.Create)(modelId);

    private string GetSessionFilePath() => Path.Combine(_stateDir, "composer-session.json");

    /// <summary>
    /// Disposes the old agent asynchronously, invoking <see cref="OnAgentDisposing"/> first.
    /// If the hook throws, the exception is captured — agent disposal is still attempted —
    /// and the captured exception is rethrown after disposal completes.
    /// <para>
    /// The sub-agent tracker is scoped to the agent it observes, so it is torn down here too:
    /// every agent replacement (reset, recreate, model switch, reconnect, dispose) routes through
    /// this method, which guarantees the panel is cleared and a stale snapshot can never survive
    /// into the next agent.
    /// </para>
    /// </summary>
    private async Task DisposeAgentAsync(CodingAgent? agent)
    {
        if (agent is null)
            return;

        // Detach before disposal so no late event can reach a tracker that is about to be dropped.
        agent.SubAgentChanged -= HandleSubAgentChanged;

        // Tear the tracker down with its agent: complete the writer, let the reader drain whatever
        // is already queued, then publish an empty snapshot. RecreateAgentAsync builds a fresh
        // tracker for the new agent.
        await StopAndClearTrackerAsync();

        Exception? hookEx = null;
        try
        {
            OnAgentDisposing?.Invoke(agent);
        }
        catch (Exception ex)
        {
            hookEx = ex;
        }

        try
        {
            await agent.DisposeAsync();
        }
        catch (Exception disposeEx)
        {
            if (hookEx is not null)
                throw new AggregateException(hookEx, disposeEx);
            throw;
        }

        if (hookEx is not null)
            throw hookEx;
    }

    /// <summary>
    /// Disposes the old agent first, then clears all connection state and disposes both clients.
    /// Agent disposal invokes <see cref="OnAgentDisposing"/>. Client disposal proceeds even if
    /// agent disposal throws. Both clients are attempted even if one throws; the same instance is
    /// disposed only once. Any disposal failure is re-thrown after cleanup completes.
    /// </summary>
    private async ValueTask DisposeClientsAndClearStateAsync()
    {
        var oldAgent = _agent;
        var main = _chatClient;
        var compaction = _compactionChatClient;

        // Clear state BEFORE disposal so no stale references survive a disposal failure.
        // _subAgentTracker is deliberately NOT cleared here — DisposeAgentAsync owns its teardown
        // and needs to read the field. The safety-net call below covers the agent-less case.
        _agent = null;
        _agentOptions = null;
        _chatClient = null;
        _compactionChatClient = null;

        Exception? agentEx = null;
        try
        {
            await DisposeAgentAsync(oldAgent);
        }
        catch (Exception ex)
        {
            agentEx = ex;
        }

        // Safety net: DisposeAgentAsync already stopped the tracker for a non-null agent, and
        // StopAndClearTrackerAsync is idempotent. This only does work when there was no agent to
        // dispose (or when agent disposal threw before reaching its own teardown).
        await StopAndClearTrackerAsync();

        // Clients are disposed even when agent disposal threw — the agent failure is captured
        // above and re-thrown (aggregated) only after all client disposals are attempted.
        Exception? clientEx = null;

        if (main is not null)
        {
            try
            {
                await DisposeClientAsync(main);
            }
            catch (Exception ex)
            {
                clientEx = ex;
            }
        }

        if (compaction is not null && !ReferenceEquals(compaction, main))
        {
            try
            {
                await DisposeClientAsync(compaction);
            }
            catch (Exception ex)
            {
                clientEx = clientEx is null ? ex : new AggregateException(clientEx, ex);
            }
        }

        // Aggregate failures — agent first, then client.
        if (agentEx is not null && clientEx is not null)
            throw new AggregateException(agentEx, clientEx);
        if (agentEx is not null)
            throw agentEx;
        if (clientEx is not null)
            throw clientEx;
    }

    /// <summary>
    /// Same as <see cref="DisposeClientsAndClearStateAsync"/> but swallows (and logs) agent and
    /// client disposal failures. Used on failure paths so the original exception is never masked.
    /// </summary>
    private async ValueTask SafeDisposeClientsAndClearStateAsync()
    {
        try
        {
            await DisposeClientsAndClearStateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispose Composer chat clients during cleanup");
        }
    }

    private static async ValueTask DisposeClientAsync(IChatClient client)
    {
        if (client is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (client is IDisposable disposable)
            disposable.Dispose();
    }

    /// <summary>
    /// Creates the chat clients and coding agent, loading any persisted session from disk.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        // LITERALLY the first statement — before the log call below, before teardown, before
        // anything else fallible. Every operation in this method can throw (ILogger.Log is no
        // exception: a logger or provider may throw), and any throw exits ConnectAsync. If the
        // flag were still carrying `true` from a prior successful disk-loaded connection, a
        // failed reconnect would report a live loaded session that no longer exists. The flag
        // must only ever mean "session was loaded from disk AND this connection attempt
        // succeeded", so every invocation starts from `false` with nothing able to run first.
        _sessionLoadedFromDisk = false;

        // No-model guard: a Composer constructed without a configured model is a disconnected
        // shell. Connecting without a model is a configuration error, not a runtime fallback.
        if (_model is null)
            throw new InvalidOperationException("no model configured");

        // Non-null local established by the guard above — used for every downstream feed
        // (client creation, registry assignment) so the nullable field never reaches a
        // non-null DTO.
        var model = _model;

        _logger.LogInformation("Composer connecting with model '{Model}'…", model);

        // Reconnect: dispose old agent + clients. Operation-failure cleanup via SafeDispose in
        // catch blocks logs failures.
        await DisposeClientsAndClearStateAsync();

        try
        {
            _chatClient = CreateClient(model);
            if (!string.IsNullOrEmpty(_compactionModel))
                _compactionChatClient = CreateClient(_compactionModel);
        }
        catch
        {
            // Failed client creation: safe cleanup logs failures so the original exception
            // propagates. The hook fires only if an old agent exists.
            await SafeDisposeClientsAndClearStateAsync();
            throw;
        }

        var sessionFile = GetSessionFilePath();

        // Tracked locally and committed to the field only after EVERY step of ConnectAsync has
        // succeeded (see the end of this method). Assigning the field here instead would let a
        // later failure — RecreateAgentAsync, the registry update, or any step added in future —
        // return/throw with a `true` flag that no longer means "connection succeeded".
        var loadedFromDisk = false;

        if (File.Exists(sessionFile))
        {
            try
            {
                _session = await AgentSession.LoadAsync(sessionFile, ct);
                loadedFromDisk = true;
                _logger.LogInformation("Loaded Composer session with {Count} messages from {File}",
                    _session.MessageHistory.Count, sessionFile);
            }
            catch (OperationCanceledException)
            {
                await SafeDisposeClientsAndClearStateAsync();
                throw;
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException)
            {
                _logger.LogWarning(ex, "Composer session file {File} is corrupt — starting fresh", sessionFile);
                _session = AgentSession.Create("composer");
                loadedFromDisk = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or DirectoryNotFoundException or FileNotFoundException)
            {
                _logger.LogWarning(ex, "Failed to read Composer session from {File} — keeping current session", sessionFile);
                loadedFromDisk = false;
            }
            catch
            {
                await SafeDisposeClientsAndClearStateAsync();
                throw;
            }
        }

        try
        {
            await RecreateAgentAsync();
        }
        catch
        {
            await SafeDisposeClientsAndClearStateAsync();
            throw;
        }

        _sessionRegistry?.RegisterOrUpdate(new LlmSessionInfo
        {
            SessionId = "composer",
            SessionType = LlmSessionType.Composer,
            Model = model,
            Status = "idle",
            CurrentTokens = _session.EstimatedContextTokens,
            MaxTokens = _maxContextTokens,
            ReasoningEffort = _reasoningEffort,
        });

        _logger.LogInformation("Composer connected (model={Model}, contextWindow={ContextWindow})",
            model, _maxContextTokens);

        // Commit last: reaching this point means the session was loaded from disk AND the whole
        // connection succeeded. Every earlier exit path leaves the field at the `false` set at
        // the top of the method.
        _sessionLoadedFromDisk = loadedFromDisk;
    }

    /// <summary>
    /// Switches to a different model and reasoning effort, disposing the old clients and
    /// recreating the agent. The session is preserved.
    /// </summary>
    /// <param name="newModel">The model identifier to switch to.</param>
    /// <param name="reasoningEffort">
    /// The reasoning effort to run with. Required — reasoning is never derived from the model name
    /// and is never inherited implicitly from the previous selection.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">Thrown when the model is not available.</exception>
    public async Task SwitchModelAsync(string newModel, ReasoningEffort reasoningEffort, CancellationToken ct = default)
    {
        if (!AvailableModels.Contains(newModel, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Model '{newModel}' is not available. Available models: {string.Join(", ", AvailableModels)}.", nameof(newModel));

        ct.ThrowIfCancellationRequested();

        _logger.LogInformation(
            "Switching Composer model from '{OldModel}' to '{NewModel}' (reasoning={Reasoning})",
            _model, newModel, ReasoningEffortConverter.Format(reasoningEffort));

        // Model switch: dispose old agent + clients before creating new ones.
        await DisposeClientsAndClearStateAsync();

        _model = newModel;

        // The caller-supplied reasoning effort becomes the configured value — reasoning is
        // never derived from the model name.
        _configuredReasoningEffort = reasoningEffort;
        _reasoningEffort = _configuredReasoningEffort;

        var modelCtx = _hiveConfig?.TryGetContextWindowForModel(newModel);
        if (modelCtx.HasValue && modelCtx.Value > 0 && _maxContextTokens != modelCtx.Value)
        {
            _logger.LogInformation(
                "Updating Composer context window from {OldContextWindow} to {NewContextWindow} for model '{Model}'",
                _maxContextTokens, modelCtx.Value, newModel);
            _maxContextTokens = modelCtx.Value;
        }

        try
        {
            _chatClient = CreateClient(newModel);
            if (!string.IsNullOrEmpty(_compactionModel))
                _compactionChatClient = CreateClient(_compactionModel);

            // Session is preserved — do NOT reload from file.
            await RecreateAgentAsync();
        }
        catch
        {
            // Failed new client creation: safe cleanup logs failures so the original
            // exception propagates.
            await SafeDisposeClientsAndClearStateAsync();
            throw;
        }

        _sessionRegistry?.RegisterOrUpdate(new LlmSessionInfo
        {
            SessionId = "composer",
            SessionType = LlmSessionType.Composer,
            Model = newModel,
            Status = "idle",
            CurrentTokens = _session.EstimatedContextTokens,
            MaxTokens = _maxContextTokens,
            ReasoningEffort = _reasoningEffort,
        });

        _logger.LogInformation("Composer switched to model '{Model}'", _model);
    }

    /// <summary>Rebuilds <see cref="AgentOptions"/> and the <see cref="CodingAgent"/>. Preserves the session.</summary>
    /// <exception cref="InvalidOperationException">Thrown when no chat client exists.</exception>
    public async Task RecreateAgentAsync()
    {
        if (_chatClient is null)
            throw new InvalidOperationException("Composer not connected");

        // Agent-only replacement: client retained, only the old agent is disposed.
        // Non-failure path: disposal failure MAY propagate.
        var oldAgent = _agent;
        _agent = null;
        _agentOptions = null;
        await DisposeAgentAsync(oldAgent);

        var workDir = _repoManager?.WorkDirectory ?? _stateDir;

        _agentOptions = new AgentOptions
        {
            WorkDirectory = workDir,
            MaxSteps = _maxSteps,
            EnableBash = false,
            EnableFileOps = _repoManager is not null,
            EnableFileWrites = false,
            EnableSkills = false,
            SystemPrompt = _systemPrompt,
            CustomTools = _composerTools,
            MaxContextTokens = _maxContextTokens,
            EnableAutoCompaction = true,
            AutoLoadWorkspaceInstructions = false,
            ReasoningEffort = _reasoningEffort,
            ShowToolCallsInStream = true,
            Logger = _logger,
            CompactionClient = !string.IsNullOrEmpty(_compactionModel) ? _compactionChatClient : null,
            CompactionMaxTokens = !string.IsNullOrEmpty(_compactionModel)
                ? _hiveConfig?.TryGetContextWindowForModel(_compactionModel)
                : null,
            OnCompacting = () => _onCompacting?.Invoke(),
            OnCompacted = r =>
            {
                _logger.LogInformation(
                    "Composer context compaction: {TokensBefore} → {TokensAfter} tokens ({ReductionPercent}% reduction)",
                    r.TokensBefore, r.TokensAfter, r.ReductionPercent);
                _onCompacted?.Invoke(r);
            },
            SubAgents = BuildSubAgentOptions(),
        };

        _agent = new CodingAgent(_chatClient, _agentOptions);

        // A fresh tracker per agent: DisposeAgentAsync stopped and cleared the previous one, so the
        // new agent always starts from an empty snapshot and stale entries can never linger.
        var tracker = new SubAgentStateTracker(_logger);
        tracker.OnSubAgentChanged += ForwardSubAgentChanged;
        _subAgentTracker = tracker;
        await tracker.StartAsync();

        // Subscribe only once the tracker is live so the first Running event cannot be dropped.
        // CodingAgent wires SubAgentChanged through its lazily-created SubAgentManager, so the
        // subscription is valid from construction and no early sub-agent start is missed.
        _agent.SubAgentChanged += HandleSubAgentChanged;

        _logger.LogDebug("Composer CodingAgent created with WorkDirectory={WorkDir}, FileOps={FileOps}",
            workDir, _repoManager is not null);
    }

    /// <summary>
    /// Builds sub-agent options when enabled, or <c>null</c> when disabled.
    /// Sub-sessions inherit the parent's reasoning effort — reasoning suffixes are NOT applied
    /// to the catalog model identifiers.
    /// </summary>
    private SubAgentOptions? BuildSubAgentOptions()
    {
        if (!_subAgentsEnabled || _repoManager is null)
            return null;

        // Construction-time snapshot, NOT the mutable _hiveConfig.
        if (_subAgentModels is null || _subAgentModels.Count == 0)
            return null;

        var options = new SubAgentOptions
        {
            MaxConcurrentSubAgents = 4,
            DefaultTimeout = TimeSpan.FromMinutes(5),
            MaxTimeout = TimeSpan.FromMinutes(15),
            MaxSummaryChars = 8_000,
            ClientFactory = modelId => CreateClient(modelId),
            DefaultClient = null,
            // Sub-agents are read-only: no bash, no writes, no skills.
            DefaultEnableBash = false,
            DefaultEnableFileOps = true,
            DefaultEnableFileWrites = false,
            DefaultEnableSkills = false,
            AdditionalImagesRoot = _additionalImagesRoot,
        };

        foreach (var entry in _subAgentModels)
        {
            var autoDescription = entry.ContextWindow is int cw ? $"Configured model, {cw / 1000}K context window" : "Configured model";
            options.AvailableModels.Add(new SubAgentModelInfo(
                entry.Name,
                !string.IsNullOrWhiteSpace(entry.Description) ? entry.Description : autoDescription,
                entry.ContextWindow,
                supportsVision: entry.SupportsVision ?? false));
        }

        return options;
    }

    /// <summary>Replaces the session with a fresh one and rebuilds the agent.</summary>
    /// <exception cref="InvalidOperationException">Thrown when no chat client exists.</exception>
    public async Task ResetSessionAsync()
    {
        if (_chatClient is null)
            throw new InvalidOperationException("Composer not connected");

        // Agent-only replacement: client retained, only the old agent is disposed.
        // Non-failure path: disposal failure MAY propagate.
        var oldAgent = _agent;
        _agent = null;
        _agentOptions = null;
        await DisposeAgentAsync(oldAgent);

        _session = AgentSession.Create("composer");
        _sessionLoadedFromDisk = false;

        // _agent is already null, so RecreateAgentAsync's own disposal step is a no-op; it
        // simply builds the new agent over the freshly created session.
        await RecreateAgentAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Shutdown: dispose agent first, then clients.
        // Non-failure path: disposal failure MAY propagate.
        await DisposeClientsAndClearStateAsync();
    }
}

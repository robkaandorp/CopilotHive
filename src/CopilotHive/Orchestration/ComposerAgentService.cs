using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Shared.AI;

using Microsoft.Extensions.AI;

using SharpCoder;

using System.Text.Json;

namespace CopilotHive.Orchestration;

/// <summary>
/// Owns the Composer's LLM connection lifecycle: chat clients, the <see cref="CodingAgent"/>,
/// the persistent <see cref="AgentSession"/>, and the <see cref="AgentOptions"/> used to build them.
/// </summary>
internal sealed class ComposerAgentService(
    string model,
    int maxContextTokens,
    int maxSteps,
    ReasoningEffort? reasoningEffort,
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
    Action<CompactionResult>? onCompacted) : IAsyncDisposable
{
    private string _model = model;
    private int _maxContextTokens = maxContextTokens;
    private readonly int _maxSteps = maxSteps;
    private ReasoningEffort? _reasoningEffort = reasoningEffort;
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

    private IChatClient? _chatClient;
    private IChatClient? _compactionChatClient;
    private CodingAgent? _agent;
    private AgentSession _session = AgentSession.Create("composer");
    private AgentOptions? _agentOptions;

    /// <summary>The active chat client, or <c>null</c> when not connected.</summary>
    public IChatClient? ChatClient => _chatClient;

    /// <summary>The active coding agent, or <c>null</c> when not connected.</summary>
    public CodingAgent? Agent => _agent;

    /// <summary>The current persistent session (never <c>null</c>).</summary>
    public AgentSession Session => _session;

    /// <summary>The agent options used to build the current agent.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the agent has not been created yet.</exception>
    public AgentOptions AgentOptions => _agentOptions
        ?? throw new InvalidOperationException("AgentOptions not yet created. Call ConnectAsync or RecreateAgent first.");

    /// <summary>Whether both the chat client and the agent exist.</summary>
    public bool IsConnected => _chatClient is not null && _agent is not null;

    /// <summary>The current model identifier.</summary>
    public string Model => _model;

    /// <summary>The current maximum context window in tokens.</summary>
    public int MaxContextTokens => _maxContextTokens;

    /// <summary>The maximum number of agent steps per run.</summary>
    public int MaxSteps => _maxSteps;

    /// <summary>The current reasoning effort, if any.</summary>
    public ReasoningEffort? ReasoningEffort => _reasoningEffort;

    /// <summary>Models the Composer can switch between at runtime.</summary>
    public IReadOnlyList<string> AvailableModels =>
        _hiveConfig?.Models?.AvailableModels is { Count: > 0 } available
            ? available.Select(m => string.IsNullOrEmpty(m.ReasoningEffort)
                ? m.Name
                : $"{m.Name}:{m.ReasoningEffort}").ToList().AsReadOnly()
            : _startupAvailableModels;

    private IChatClient CreateClient(string modelId) => (_chatClientFactory ?? ChatClientFactory.Create)(modelId);

    private string GetSessionFilePath() => Path.Combine(_stateDir, "composer-session.json");

    /// <summary>
    /// Clears all connection state first, then disposes both distinct clients.
    /// Both clients are attempted even if one throws; the same instance is disposed only once.
    /// Any disposal failure is re-thrown after cleanup completes.
    /// </summary>
    private async ValueTask DisposeClientsAndClearStateAsync()
    {
        var main = _chatClient;
        var compaction = _compactionChatClient;

        // Clear state BEFORE disposal so no stale references survive a disposal failure.
        _chatClient = null;
        _compactionChatClient = null;
        _agent = null;
        _agentOptions = null;

        Exception? disposeEx = null;

        if (main is not null)
        {
            try
            {
                await DisposeClientAsync(main);
            }
            catch (Exception ex)
            {
                disposeEx = ex;
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
                disposeEx = disposeEx is null ? ex : new AggregateException(disposeEx, ex);
            }
        }

        if (disposeEx is not null)
            throw disposeEx;
    }

    /// <summary>
    /// Same as <see cref="DisposeClientsAndClearStateAsync"/> but swallows (and logs) disposal
    /// failures. Used on failure paths so the original exception is never masked.
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
        _logger.LogInformation("Composer connecting with model '{Model}'…", _model);

        await DisposeClientsAndClearStateAsync();

        try
        {
            _chatClient = CreateClient(_model);
            if (!string.IsNullOrEmpty(_compactionModel))
                _compactionChatClient = CreateClient(_compactionModel);
        }
        catch
        {
            await SafeDisposeClientsAndClearStateAsync();
            throw;
        }

        var sessionFile = GetSessionFilePath();
        if (File.Exists(sessionFile))
        {
            try
            {
                _session = await AgentSession.LoadAsync(sessionFile, ct);
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
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or DirectoryNotFoundException or FileNotFoundException)
            {
                _logger.LogWarning(ex, "Failed to read Composer session from {File} — keeping current session", sessionFile);
            }
            catch
            {
                await SafeDisposeClientsAndClearStateAsync();
                throw;
            }
        }

        try
        {
            RecreateAgent();
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
            Model = _model,
            Status = "idle",
            CurrentTokens = _session.EstimatedContextTokens,
            MaxTokens = _maxContextTokens,
        });

        _logger.LogInformation("Composer connected (model={Model}, contextWindow={ContextWindow})",
            _model, _maxContextTokens);
    }

    /// <summary>
    /// Switches to a different model, disposing the old clients and recreating the agent.
    /// The session is preserved.
    /// </summary>
    /// <param name="newModel">The model identifier to switch to.</param>
    /// <exception cref="ArgumentException">Thrown when the model is not available.</exception>
    public async Task SwitchModelAsync(string newModel)
    {
        if (!AvailableModels.Contains(newModel, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Model '{newModel}' is not available. Available models: {string.Join(", ", AvailableModels)}.", nameof(newModel));

        _logger.LogInformation("Switching Composer model from '{OldModel}' to '{NewModel}'", _model, newModel);

        await DisposeClientsAndClearStateAsync();

        _model = newModel;
        var (_, _, reasoning) = ChatClientFactory.ParseProviderModelAndReasoning(newModel);
        _reasoningEffort = reasoning;

        // Strip only the reasoning suffix (if present) while preserving the provider prefix and any
        // tag so the lookup matches ModelEntry.Name.
        var modelForLookup = newModel;
        if (reasoning is not null)
        {
            var lastColon = newModel.LastIndexOf(':');
            if (lastColon > 0)
                modelForLookup = newModel[..lastColon];
        }
        var modelCtx = _hiveConfig?.TryGetContextWindowForModel(modelForLookup);
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
            RecreateAgent();
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
            Model = _model,
            Status = "idle",
            CurrentTokens = _session.EstimatedContextTokens,
            MaxTokens = _maxContextTokens,
        });

        _logger.LogInformation("Composer switched to model '{Model}'", _model);
    }

    /// <summary>Rebuilds <see cref="AgentOptions"/> and the <see cref="CodingAgent"/>. Preserves the session.</summary>
    /// <exception cref="InvalidOperationException">Thrown when no chat client exists.</exception>
    public void RecreateAgent()
    {
        if (_chatClient is null)
            throw new InvalidOperationException("Composer not connected");

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
        };

        _agent = new CodingAgent(_chatClient, _agentOptions);

        _logger.LogDebug("Composer CodingAgent created with WorkDirectory={WorkDir}, FileOps={FileOps}",
            workDir, _repoManager is not null);
    }

    /// <summary>Replaces the session with a fresh one and rebuilds the agent.</summary>
    /// <exception cref="InvalidOperationException">Thrown when no chat client exists.</exception>
    public void ResetSession()
    {
        if (_chatClient is null)
            throw new InvalidOperationException("Composer not connected");

        _session = AgentSession.Create("composer");
        RecreateAgent();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await DisposeClientsAndClearStateAsync();
}

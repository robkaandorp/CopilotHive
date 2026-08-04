using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Knowledge;
using CopilotHive.Orchestration;
using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Shared.AI;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using SharpCoder;
using SharpCoder.SubAgents;

namespace CopilotHive.Actors;

/// <summary>
/// Channel-based actor prototype owning the mutable state of the Brain (master session,
/// per-goal sessions and active pipelines). All state changes are serialized through a
/// single-reader mailbox, so no locking is required by callers.
/// </summary>
internal sealed class BrainActor : Actor<IBrainMessage>
{
    private readonly string _stateDir;
    private readonly ILogger _logger;

    private readonly Dictionary<string, string> _activeGoalSessions = [];
    private readonly Dictionary<string, GoalPipeline> _activePipelines = [];
    private readonly Dictionary<string, GoalBrainActor> _childActors = [];

    private readonly Func<string, IChatClient> _chatClientFactory;
    private readonly IChatClient? _injectedChatClient;
    private readonly string? _compactionModel;
    private readonly HiveConfigFile? _hiveConfig;
    private readonly int _maxSteps;
    private readonly string? _systemPrompt;
    private readonly string? _workDirectory;
    private readonly IGoalStore? _goalStore;
    private readonly KnowledgeGraph? _knowledgeGraph;
    private readonly LlmSessionRegistry? _sessionRegistry;
    private readonly IReadOnlyList<SubAgentModelEntry>? _subAgentModels;
    private readonly bool _subAgentsEnabled;
    private readonly ConfigRepoManager? _configRepo;
    private ReasoningEffort? _reasoningEffort;

    private AgentSession? _masterSession;
    private string _modelOverride;
    private int _maxContextTokens;
    private bool _connected;
    private string _orchestratorInstructions = string.Empty;

    /// <summary>Creates a brain actor bound to the given state directory.</summary>
    internal BrainActor(
        string modelOverride,
        int maxContextTokens,
        string stateDir,
        ILogger logger,
        Func<string, IChatClient>? chatClientFactory = null,
        IChatClient? injectedChatClient = null,
        string? compactionModel = null,
        HiveConfigFile? hiveConfig = null,
        int maxSteps = 50,
        string? systemPrompt = null,
        ReasoningEffort? reasoningEffort = null,
        string? workDirectory = null,
        IGoalStore? goalStore = null,
        KnowledgeGraph? knowledgeGraph = null,
        LlmSessionRegistry? sessionRegistry = null,
        IReadOnlyList<SubAgentModelEntry>? subAgentModels = null,
        bool subAgentsEnabled = false,
        ConfigRepoManager? configRepo = null)
    {
        _modelOverride = modelOverride;
        _maxContextTokens = maxContextTokens;
        _stateDir = stateDir;
        _logger = logger;
        _chatClientFactory = chatClientFactory ?? ChatClientFactory.Create;
        _injectedChatClient = injectedChatClient;
        _compactionModel = compactionModel;
        _hiveConfig = hiveConfig;
        _maxSteps = maxSteps;
        _systemPrompt = systemPrompt;
        _reasoningEffort = reasoningEffort;
        _workDirectory = workDirectory;
        _goalStore = goalStore;
        _knowledgeGraph = knowledgeGraph;
        _sessionRegistry = sessionRegistry;
        _subAgentModels = subAgentModels;
        _subAgentsEnabled = subAgentsEnabled;
        _configRepo = configRepo;
    }

    /// <inheritdoc />
    protected override async Task HandleAsync(IBrainMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case ConnectMessage m:
                await ConnectAsync(ct);
                m.Reply.TrySetResult(true);
                break;

            case ForkSessionMessage m:
                await ForkSessionAsync(m.GoalId, ct);
                m.Reply.TrySetResult(true);
                break;

            case DeleteSessionMessage m:
                await DeleteSessionAsync(m.GoalId);
                m.Reply.TrySetResult(true);
                break;

            case MergeSummaryMessage m:
                await MergeSummaryAsync(m.GoalId, m.Summary, ct);
                m.Reply.TrySetResult(true);
                break;

            case UpdateModelMessage m:
                _modelOverride = m.Model;
                if (m.MaxContextTokens is { } maxTokens)
                {
                    _maxContextTokens = maxTokens;
                }

                // Affects future children only; existing children keep their original model.
                // An explicit reasoning effort wins; otherwise fall back to legacy suffix parsing.
                _reasoningEffort = m.ReasoningEffort
                    ?? ChatClientFactory.ParseProviderModelAndReasoning(m.Model).reasoning;

                m.Reply.TrySetResult(true);
                break;

            case RegisterPipelineMessage m:
                _activePipelines[m.GoalId] = m.Pipeline;
                break;

            case DeregisterPipelineMessage m:
                _activePipelines.Remove(m.GoalId);
                break;

            case GetPipelineMessage m:
                m.Reply.TrySetResult(_activePipelines.TryGetValue(m.GoalId, out var pipeline) ? pipeline : null);
                break;

            case GetStatsMessage m:
                m.Reply.TrySetResult(CreateStats());
                break;

            case GoalSessionExistsMessage m:
                m.Reply.TrySetResult(File.Exists(ValidateGoalPath(m.GoalId)));
                break;

            case RegisterExistingSessionMessage m:
                await RegisterExistingSessionAsync(m.GoalId, ct);
                m.Reply.TrySetResult(true);
                break;

            case InjectOrchestratorInstructionsMessage m:
                _orchestratorInstructions = m.Instructions;
                m.Reply.TrySetResult(true);
                break;

            case ExecutePromptOnChildMessage m:
                ExecutePromptOnChild(m);
                break;

            case InjectNoteOnChildMessage m:
                InjectNoteOnChild(m);
                break;

            default:
                throw new InvalidOperationException($"Unhandled brain message type '{message.GetType().Name}'.");
        }
    }

    private void ExecutePromptOnChild(ExecutePromptOnChildMessage m)
    {
        if (!_childActors.TryGetValue(m.GoalId, out var child))
        {
            m.Reply.TrySetException(new KeyNotFoundException($"No child actor for goal '{m.GoalId}'."));
            return;
        }

        var inner = GoalBrainActorMessages.CreateExecutePromptMessage(m.Prompt, m.Ct);
        if (!child.Tell(inner))
        {
            m.Reply.TrySetException(new InvalidOperationException("Child actor mailbox closed."));
            return;
        }

        Relay(inner.Reply.Task, m.Reply, static r => r);
    }

    private void InjectNoteOnChild(InjectNoteOnChildMessage m)
    {
        if (!_childActors.TryGetValue(m.GoalId, out var child))
        {
            m.Reply.TrySetException(new KeyNotFoundException($"No child actor for goal '{m.GoalId}'."));
            return;
        }

        var inner = GoalBrainActorMessages.CreateInjectNoteMessage(m.Note);
        if (!child.Tell(inner))
        {
            m.Reply.TrySetException(new InvalidOperationException("Child actor mailbox closed."));
            return;
        }

        Relay(inner.Reply.Task, m.Reply, static _ => true);
    }

    /// <summary>Relays an inner reply to an outer reply without blocking the mailbox.</summary>
    private static void Relay<TInner, TOuter>(
        Task<TInner> innerTask,
        TaskCompletionSource<TOuter> outer,
        Func<TInner, TOuter> project) =>
        innerTask.ContinueWith(
            inner =>
            {
                if (inner.IsFaulted)
                {
                    outer.TrySetException(inner.Exception!);
                }
                else if (inner.IsCanceled)
                {
                    outer.TrySetCanceled();
                }
                else
                {
                    outer.TrySetResult(project(inner.Result));
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private async Task ConnectAsync(CancellationToken ct)
    {
        if (_connected)
        {
            return;
        }

        var masterFile = GetMasterSessionFilePath();
        if (File.Exists(masterFile))
        {
            try
            {
                _masterSession = await AgentSession.LoadAsync(masterFile, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load Brain master session from {File} — starting fresh", masterFile);
                _masterSession = AgentSession.Create("brain");
            }
        }
        else
        {
            _masterSession = AgentSession.Create("brain");
        }

        _connected = true;
        var path = GetMasterSessionFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await _masterSession.SaveAsync(path, ct);
    }

    private async Task ForkSessionAsync(string goalId, CancellationToken ct)
    {
        var master = EnsureConnected();
        if (_activeGoalSessions.ContainsKey(goalId))
        {
            return;
        }

        var filePath = ValidateGoalPath(goalId);
        var goalSession = master.Fork($"brain-goal-{goalId}");

        // Phase 1 — parent owns the raw clients. Phase 2 — ownership transfers to the child.
        var resources = PrepareChildResources(goalId);
        var child = CreateChildActor(goalId, goalSession, resources);

        // Phase 3 — the parent owns the child; failures dispose the child, not the raw clients.
        try
        {
            child.Start();
            await SaveSessionAsync(goalSession, filePath, ct);
        }
        catch
        {
            await DisposeChildQuietlyAsync(child);
            _sessionRegistry?.Unregister($"brain-goal-{goalId}");
            throw;
        }

        _activeGoalSessions[goalId] = filePath;
        _childActors[goalId] = child;
    }

    /// <summary>Raw, parent-owned resources prepared before the child constructor is invoked.</summary>
    private readonly record struct ChildResources(
        IChatClient ChatClient,
        bool OwnsClient,
        IChatClient? CompactionClient,
        AgentOptions Options);

    /// <summary>
    /// Phase 1 — creates the raw chat clients and agent options. The parent owns these resources,
    /// so any failure here disposes the owned clients (never the injected one) before rethrowing.
    /// </summary>
    private ChildResources PrepareChildResources(string goalId)
    {
        IChatClient chatClient;
        bool ownsClient;
        IChatClient? compactionClient = null;

        if (_injectedChatClient is not null)
        {
            chatClient = _injectedChatClient;
            ownsClient = false;
        }
        else
        {
            chatClient = _chatClientFactory(_modelOverride);
            ownsClient = true;
        }

        try
        {
            compactionClient = !string.IsNullOrEmpty(_compactionModel)
                ? ChatClientFactory.Create(_compactionModel)
                : null;

            var options = new AgentOptions
            {
                EnableBash = false,
                EnableFileOps = _workDirectory is not null,
                EnableFileWrites = false,
                EnableSkills = false,
                AutoLoadWorkspaceInstructions = false,
                MaxSteps = _maxSteps,
                SystemPrompt = !string.IsNullOrWhiteSpace(_orchestratorInstructions) ? _orchestratorInstructions : _systemPrompt,
                MaxContextTokens = _maxContextTokens,
                EnableAutoCompaction = true,
                ReasoningEffort = _reasoningEffort,
                Logger = _logger,
                CompactionClient = compactionClient,
                CompactionMaxTokens = !string.IsNullOrEmpty(_compactionModel)
                    ? _hiveConfig?.TryGetContextWindowForModel(_compactionModel)
                    : null,
                OnCompacted = r => _logger.LogInformation(
                    "BrainActor child compaction: {TokensBefore} -> {TokensAfter} tokens",
                    r.TokensBefore,
                    r.TokensAfter),
                SubAgents = BuildSubAgentOptions(),
            };

            if (!string.IsNullOrEmpty(_workDirectory))
            {
                options.WorkDirectory = _workDirectory;
            }

            return new ChildResources(chatClient, ownsClient, compactionClient, options);
        }
        catch
        {
            if (compactionClient is not null && !ReferenceEquals(compactionClient, chatClient))
            {
                try { compactionClient.Dispose(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose compaction client for {GoalId}", goalId); }
            }

            if (ownsClient)
            {
                try { chatClient.Dispose(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose chat client for {GoalId}", goalId); }
            }

            throw;
        }
    }

    /// <summary>
    /// Builds the sub-agent options for child Brain sessions, or null when sub-agents are disabled.
    /// </summary>
    private SubAgentOptions? BuildSubAgentOptions()
    {
        if (!_subAgentsEnabled || _subAgentModels is null || _subAgentModels.Count == 0)
        {
            return null;
        }

        var options = new SubAgentOptions
        {
            MaxConcurrentSubAgents = 2,
            DefaultTimeout = TimeSpan.FromMinutes(5),
            MaxTimeout = TimeSpan.FromMinutes(15),
            MaxSummaryChars = 8_000,
            ClientFactory = modelId => _chatClientFactory(modelId),
            DefaultClient = null,
            DefaultEnableBash = false,
            DefaultEnableFileOps = true,
            DefaultEnableFileWrites = false,
            DefaultEnableSkills = false,
        };

        foreach (var entry in _subAgentModels)
        {
            var autoDescription = entry.ContextWindow is int cw ? $"Configured model, {cw / 1000}K context window" : "Configured model";
            options.AvailableModels.Add(new SubAgentModelInfo(
                entry.Name,
                !string.IsNullOrWhiteSpace(entry.Description) ? entry.Description : autoDescription,
                entry.ContextWindow,
                supportsVision: entry.SupportsVision));
        }

        return options;
    }

    /// <summary>
    /// Phase 2 — invokes the child constructor. Ownership of the raw clients transfers to the
    /// child here; a constructor failure is handled by the child's own catch, so this call must
    /// never sit inside the phase 1 catch (that would double-dispose the clients).
    /// </summary>
    private GoalBrainActor CreateChildActor(string goalId, AgentSession goalSession, ChildResources resources) =>
        new(
            goalId,
            goalSession,
            resources.ChatClient,
            resources.OwnsClient,
            resources.CompactionClient,
            resources.Options,
            _modelOverride,
            _maxContextTokens,
            _stateDir,
            sessionRegistry: _sessionRegistry,
            _logger,
            goalStore: _goalStore,
            knowledgeGraph: _knowledgeGraph,
            parentTell: Tell,
            configRepo: _configRepo);

    private async Task DisposeChildQuietlyAsync(GoalBrainActor child)
    {
        try { await child.DisposeAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose goal brain child actor for {GoalId}", child.GoalId); }
    }

    /// <summary>
    /// Tracks a goal session that already exists on disk. When the file is missing the master
    /// session is forked and persisted so the goal has a usable session either way.
    /// </summary>
    private async Task RegisterExistingSessionAsync(string goalId, CancellationToken ct)
    {
        if (_activeGoalSessions.ContainsKey(goalId))
        {
            return;
        }

        var filePath = ValidateGoalPath(goalId);
        AgentSession? goalSession = null;
        if (File.Exists(filePath))
        {
            try
            {
                goalSession = await AgentSession.LoadAsync(filePath, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load goal Brain session from {File} — forking a fresh one", filePath);
            }
        }

        if (goalSession is null)
        {
            goalSession = EnsureConnected().Fork($"brain-goal-{goalId}");
            await SaveSessionAsync(goalSession, filePath, ct);
        }

        // Phase 1 — parent owns the raw clients. Phase 2 — ownership transfers to the child.
        var resources = PrepareChildResources(goalId);
        var child = CreateChildActor(goalId, goalSession, resources);

        // Phase 3 — the parent owns the child; failures dispose the child, not the raw clients.
        try
        {
            child.Start();
        }
        catch
        {
            await DisposeChildQuietlyAsync(child);
            throw;
        }

        _activeGoalSessions[goalId] = filePath;
        _childActors[goalId] = child;
    }

    /// <summary>
    /// Removes all tracking for a goal and deletes its session file. Child disposal is awaited
    /// before the file is deleted so a still-running child cannot re-save the file afterwards.
    /// </summary>
    private async Task DeleteSessionAsync(string goalId)
    {
        var hasChild = _childActors.Remove(goalId, out var child);
        _activeGoalSessions.Remove(goalId);
        _activePipelines.Remove(goalId);

        if (hasChild)
        {
            await DisposeChildQuietlyAsync(child!);
            if (!child!.IsCompleted)
            {
                _logger.LogWarning("Goal brain child actor for {GoalId} did not complete after disposal", goalId);
            }
        }

        var filePath = ValidateGoalPath(goalId);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        _sessionRegistry?.Unregister($"brain-goal-{goalId}");
    }

    private async Task MergeSummaryAsync(string goalId, string summary, CancellationToken ct)
    {
        var master = EnsureConnected();
        master.MessageHistory.Add(new ChatMessage(ChatRole.User, $"[Goal completed: {goalId}] Summarize what was done."));
        master.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, summary));
        master.LastKnownContextTokens = 0;
        await SaveSessionAsync(master, GetMasterSessionFilePath(), ct);

        _sessionRegistry?.RegisterOrUpdate(new LlmSessionInfo
        {
            SessionId = "brain-master",
            SessionType = LlmSessionType.Brain,
            Model = _modelOverride,
            Status = "idle",
            CurrentTokens = master.EstimatedContextTokens,
            MaxTokens = _maxContextTokens,
        });
    }

    private BrainActorStats? CreateStats()
    {
        if (!_connected || _masterSession is null)
        {
            return null;
        }

        var contextTokens = _masterSession.LastKnownContextTokens > 0
            ? _masterSession.LastKnownContextTokens
            : _masterSession.EstimatedContextTokens;

        return new BrainActorStats(
            _modelOverride,
            _masterSession.MessageHistory.Count,
            contextTokens,
            _maxContextTokens,
            _connected,
            _masterSession.InputTokensUsed,
            _masterSession.OutputTokensUsed,
            _maxSteps);
    }

    private AgentSession EnsureConnected() =>
        _connected && _masterSession is not null
            ? _masterSession
            : throw new InvalidOperationException("Brain is not connected.");

    private static async Task SaveSessionAsync(AgentSession session, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await session.SaveAsync(path, ct);
    }

    private string GetMasterSessionFilePath() => Path.Combine(_stateDir, "brain-master.json");

    /// <summary>
    /// Resolves the session file path for a goal, rejecting ids that could escape the state
    /// directory. Throws when the id contains separators or the canonical path escapes.
    /// </summary>
    private string ValidateGoalPath(string goalId)
    {
        if (string.IsNullOrWhiteSpace(goalId))
        {
            throw new ArgumentException("Goal id must not be empty.", nameof(goalId));
        }

        if (goalId.Contains('/') || goalId.Contains('\\') || goalId.Contains(".."))
        {
            throw new ArgumentException($"Goal id '{goalId}' contains invalid path characters.", nameof(goalId));
        }

        var root = Path.GetFullPath(_stateDir);
        var fullPath = Path.GetFullPath(Path.Combine(root, $"brain-goal-{goalId}.json"));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"Goal id '{goalId}' resolves outside the state directory.");
        }

        return fullPath;
    }

    /// <inheritdoc />
    protected override void CancelReply(IBrainMessage message)
    {
        switch (message)
        {
            case ConnectMessage m: m.Reply.TrySetCanceled(); break;
            case ForkSessionMessage m: m.Reply.TrySetCanceled(); break;
            case DeleteSessionMessage m: m.Reply.TrySetCanceled(); break;
            case MergeSummaryMessage m: m.Reply.TrySetCanceled(); break;
            case UpdateModelMessage m: m.Reply.TrySetCanceled(); break;
            case GetPipelineMessage m: m.Reply.TrySetCanceled(); break;
            case GetStatsMessage m: m.Reply.TrySetCanceled(); break;
            case GoalSessionExistsMessage m: m.Reply.TrySetCanceled(); break;
            case RegisterExistingSessionMessage m: m.Reply.TrySetCanceled(); break;
            case InjectOrchestratorInstructionsMessage m: m.Reply.TrySetCanceled(); break;
            case ExecutePromptOnChildMessage m: m.Reply.TrySetCanceled(); break;
            case InjectNoteOnChildMessage m: m.Reply.TrySetCanceled(); break;
        }
    }

    /// <inheritdoc />
    protected override void OnUnhandledException(IBrainMessage message, Exception ex)
    {
        switch (message)
        {
            case ConnectMessage m: m.Reply.TrySetException(ex); break;
            case ForkSessionMessage m: m.Reply.TrySetException(ex); break;
            case DeleteSessionMessage m: m.Reply.TrySetException(ex); break;
            case MergeSummaryMessage m: m.Reply.TrySetException(ex); break;
            case UpdateModelMessage m: m.Reply.TrySetException(ex); break;
            case GetPipelineMessage m: m.Reply.TrySetException(ex); break;
            case GetStatsMessage m: m.Reply.TrySetException(ex); break;
            case GoalSessionExistsMessage m: m.Reply.TrySetException(ex); break;
            case RegisterExistingSessionMessage m: m.Reply.TrySetException(ex); break;
            case InjectOrchestratorInstructionsMessage m: m.Reply.TrySetException(ex); break;
            case ExecutePromptOnChildMessage m: m.Reply.TrySetException(ex); break;
            case InjectNoteOnChildMessage m: m.Reply.TrySetException(ex); break;
            default:
                _logger.LogError(ex, "Brain actor failed to handle {MessageType}", message.GetType().Name);
                break;
        }
    }

    /// <inheritdoc />
    protected override async Task OnShutdownAsync()
    {
        var children = _childActors.Values.ToList();
        _childActors.Clear();
        await Task.WhenAll(children.Select(c => DisposeChildQuietlyAsync(c)));
        foreach (var child in children)
        {
            _sessionRegistry?.Unregister($"brain-goal-{child.GoalId}");
        }
    }

    /// <inheritdoc />
    protected override void OnDisposeTimeout() =>
        _logger.LogWarning("BrainActor did not complete within 5 seconds.");
}

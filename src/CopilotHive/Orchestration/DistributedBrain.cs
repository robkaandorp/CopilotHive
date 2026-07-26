using CopilotHive.Actors;
using CopilotHive.Agents;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Metrics;
using CopilotHive.Services;
using CopilotHive.Shared.AI;
using CopilotHive.Workers;

using Microsoft.Extensions.AI;

using SharpCoder;

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;

namespace CopilotHive.Orchestration;

/// <summary>
/// LLM-powered brain that runs inside the orchestrator container.
/// The Brain has two jobs: plan iteration phases and craft worker prompts.
/// Maintains a master AgentSession with shared context (system notes,
/// orchestrator instructions) and forks per-goal sessions from it to
/// isolate each goal's conversation. File tools give the Brain read-only
/// access to target repositories via BrainRepoManager clones.
/// </summary>
public sealed class DistributedBrain : IDistributedBrain, IAsyncDisposable
{
    private string _modelOverride;
    private int _maxContextTokens;
    private readonly int _maxSteps;
    private ReasoningEffort? _reasoningEffort;
    private readonly ILogger<DistributedBrain> _logger;
    private readonly MetricsTracker? _metricsTracker;
    private readonly IBrainRepoManager? _repoManager;
    private readonly IGoalStore? _goalStore;
    private readonly string _stateDir;
    private readonly string? _compactionModel;
    private readonly KnowledgeGraph? _knowledgeGraph;
    private readonly Func<string, IChatClient> _chatClientFactory;
    private readonly HiveConfigFile? _hiveConfig;
    private readonly LlmSessionRegistry? _sessionRegistry;

    /// <summary>
    /// Directory used for persistent Brain state (session files).
    /// </summary>
    public string StateDirectory => _stateDir;

    private AgentSession _masterSession;

    /// <summary>Per-goal Brain contexts, each with its own gate, chat client, coding agent, and session.</summary>
    private readonly ConcurrentDictionary<string, GoalBrainContext> _goalContexts = new();

    /// <summary>Goal IDs currently being deleted, guarded so no new context is created during teardown.</summary>
    private readonly ConcurrentDictionary<string, bool> _deletingGoals = new();

    private volatile bool _disposing;
    private bool _resetting;
    private bool _connected;
    private bool _skipSessionMigration;

    /// <summary>Shadow brain actor, created on connect when <c>UseBrainActors</c> is enabled.</summary>
    private BrainActor? _brainActor;

    /// <summary>
    /// Connect-time snapshot of <c>UseBrainActors</c>. Captured once in <see cref="ConnectAsync"/>
    /// so runtime config reloads cannot toggle the shadow-actor behaviour.
    /// </summary>
    private bool _useBrainActors;

    /// <summary>Test seam for constructing the shadow actor from a state directory.</summary>
    internal Func<string, BrainActor>? _actorFactory;

    /// <summary>Test seam: artificial delay applied before mirroring a message to the shadow actor.</summary>
    internal TimeSpan? _mirrorDelay;

    /// <summary>Guards one-shot disposal bookkeeping.</summary>
    private readonly object _lifecycleLock = new();
    private Task? _disposeTask;

    /// <summary>An externally-injected chat client shared across contexts (never owned/disposed by a context).</summary>
    private readonly IChatClient? _injectedChatClient;

    /// <summary>Flows the current goal's Brain context across async calls within a single Brain operation.</summary>
    private readonly AsyncLocal<GoalBrainContext?> _currentContext = new();

    private string _systemPrompt;
    private readonly List<AITool> _brainTools;
    private readonly AgentsManager? _agentsManager;

    /// <summary>Active pipeline snapshots keyed by goal ID, used by the <c>get_goal</c> tool.</summary>
    private readonly ConcurrentDictionary<string, GoalPipeline> _activePipelines = new();

    /// <summary>Serialises session-state mutations (master session, model settings, registered goals, session files).</summary>
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private const string DefaultSystemPrompt = BrainPromptBuilder.DefaultSystemPrompt;

    /// <summary>Initialises a new <see cref="DistributedBrain"/> that connects directly to an LLM provider.</summary>
    public DistributedBrain(string modelOverride, ILogger<DistributedBrain> logger,
        MetricsTracker? metricsTracker = null, Agents.AgentsManager? agentsManager = null,
        int maxContextTokens = Constants.DefaultBrainContextWindow,
        int maxSteps = Constants.DefaultBrainMaxSteps,
        IBrainRepoManager? repoManager = null,
        string? stateDir = null,
        IGoalStore? goalStore = null,
        IChatClient? chatClient = null,
        string? compactionModel = null,
        KnowledgeGraph? knowledgeGraph = null,
        Func<string, IChatClient>? chatClientFactory = null,
        HiveConfigFile? hiveConfig = null,
        LlmSessionRegistry? sessionRegistry = null)
    {
        _modelOverride = modelOverride;
        _maxContextTokens = maxContextTokens;
        _maxSteps = maxSteps;
        _logger = logger;
        _metricsTracker = metricsTracker;
        _repoManager = repoManager;
        _agentsManager = agentsManager;
        _goalStore = goalStore;
        _injectedChatClient = chatClient;
        _stateDir = stateDir ?? "/app/state";
        _compactionModel = compactionModel;
        _knowledgeGraph = knowledgeGraph;
        _chatClientFactory = chatClientFactory ?? ChatClientFactory.Create;
        _hiveConfig = hiveConfig;
        _sessionRegistry = sessionRegistry;
        _masterSession = AgentSession.Create("brain");

        var (_, _, reasoning) = ChatClientFactory.ParseProviderModelAndReasoning(modelOverride);
        _reasoningEffort = reasoning;

        _brainTools = BuildBrainTools();

        var orchestratorInstructions = agentsManager?.GetAgentsMd(WorkerRole.Orchestrator) ?? "";
        _systemPrompt = string.IsNullOrWhiteSpace(orchestratorInstructions)
            ? DefaultSystemPrompt
            : $"{DefaultSystemPrompt}\n\n{orchestratorInstructions}";
    }

    /// <summary>Loads or creates the master Brain session. Idempotent. Per-goal agents and
    /// chat clients are created lazily via <see cref="CreateGoalBrainContextAsync"/>.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Brain connecting with model '{Model}'…", _modelOverride);

        await _sessionLock.WaitAsync(ct);
        try
        {
            if (_disposing)
                throw new ObjectDisposedException(nameof(DistributedBrain));

            if (_connected)
                return;

            // Try to load a persisted master session from a previous run.
            // Migrate legacy brain-session.json to brain-master.json if needed.
            var masterFile = GetMasterSessionFilePath();
            var oldFile = Path.Combine(_stateDir, "brain-session.json");
            if (File.Exists(oldFile) && !File.Exists(masterFile))
            {
                File.Move(oldFile, masterFile);
                _logger.LogInformation("Migrated brain-session.json to brain-master.json");
            }
            if (File.Exists(masterFile))
            {
                try
                {
                    _masterSession = await AgentSession.LoadAsync(masterFile, ct);
                    _logger.LogInformation("Loaded Brain master session with {Count} messages from {File}",
                        _masterSession.MessageHistory.Count, masterFile);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load Brain master session from {File} — starting fresh", masterFile);
                    _masterSession = AgentSession.Create("brain");
                }
            }

            RefreshMasterSessionRegistry();

            _connected = true;

            // Capture the flag once — runtime config reloads must not toggle the shadow.
            _useBrainActors = _hiveConfig?.Orchestrator?.UseBrainActors == true;

            await StartShadowActorAsync(ct);
        }
        finally
        {
            _sessionLock.Release();
        }

        _logger.LogInformation("Brain connected (model={Model}, contextWindow={ContextWindow})",
            _modelOverride, _maxContextTokens);
    }

    /// <summary>
    /// Starts the shadow <see cref="BrainActor"/> when enabled. Startup failures are logged and
    /// leave the brain fully functional without a shadow; caller cancellation propagates.
    /// </summary>
    private async Task StartShadowActorAsync(CancellationToken ct)
    {
        if (!_useBrainActors)
            return;

        BrainActor? actor = null;
        try
        {
            var actorStateDir = Path.Combine(_stateDir, "actors");
            Directory.CreateDirectory(actorStateDir);

            MigrateSessionFiles(actorStateDir);

            actor = _actorFactory?.Invoke(actorStateDir)
                ?? new BrainActor(
                    _modelOverride, _maxContextTokens, actorStateDir, _logger,
                    chatClientFactory: _chatClientFactory,
                    injectedChatClient: _injectedChatClient,
                    compactionModel: _compactionModel,
                    hiveConfig: _hiveConfig,
                    maxSteps: _maxSteps,
                    systemPrompt: _systemPrompt,
                    reasoningEffort: _reasoningEffort,
                    workDirectory: _repoManager?.WorkDirectory,
                    goalStore: _goalStore,
                    knowledgeGraph: _knowledgeGraph);
            actor.Start();

            var connectMsg = BrainActorMessages.CreateConnectMessage();
            if (!actor.Tell(connectMsg))
                throw new InvalidOperationException("BrainActor mailbox closed");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            await connectMsg.Reply.Task.WaitAsync(timeoutCts.Token);

            Volatile.Write(ref _brainActor, actor);
            actor = null;
            _logger.LogInformation("BrainActor shadow started (state dir: {Dir})", actorStateDir);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await DisposeActorSafelyAsync(actor);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BrainActor startup failed — shadow disabled");
            await DisposeActorSafelyAsync(actor);
        }
    }

    private static async Task DisposeActorSafelyAsync(BrainActor? actor)
    {
        if (actor is null)
            return;

        try { await actor.DisposeAsync(); } catch { }
    }

    /// <summary>
    /// Migrates legacy session files from the state directory into the actor state directory.
    /// Files are only copied if they do not already exist in the actor directory.
    /// </summary>
    private void MigrateSessionFiles(string actorStateDir)
    {
        if (_resetting || _skipSessionMigration)
            return;

        var legacyMasterFile = Path.Combine(_stateDir, "brain-master.json");
        var actorMasterFile = Path.Combine(actorStateDir, "brain-master.json");
        if (File.Exists(legacyMasterFile) && !File.Exists(actorMasterFile))
        {
            try
            {
                File.Copy(legacyMasterFile, actorMasterFile);
                _logger.LogInformation("Migrated legacy master session to actor directory");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to migrate legacy master session to actor directory");
            }
        }

        foreach (var legacyGoalFile in Directory.GetFiles(_stateDir, "brain-goal-*.json"))
        {
            var fileName = Path.GetFileName(legacyGoalFile);
            var actorGoalFile = Path.Combine(actorStateDir, fileName);
            if (!File.Exists(actorGoalFile))
            {
                try
                {
                    File.Copy(legacyGoalFile, actorGoalFile);
                    _logger.LogInformation("Migrated legacy goal session {File} to actor directory", fileName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to migrate legacy goal session {File} to actor directory", fileName);
                }
            }
        }
    }

    /// <summary>
    /// Mirrors a lifecycle message to the shadow actor and awaits its reply. The shadow is
    /// non-authoritative: a missing actor, a closed mailbox, a timeout or a fault is logged
    /// as a warning and never propagated to the caller.
    /// </summary>
    private async Task MirrorAsync<T>(IBrainMessage message, TaskCompletionSource<T> reply, TimeSpan timeout)
    {
        if (Volatile.Read(ref _brainActor) is not { } actor)
            return;

        using var cts = new CancellationTokenSource(timeout);

        if (_mirrorDelay is { } delay)
        {
            try { await Task.Delay(delay, cts.Token); }
            catch (OperationCanceledException) { /* timeout elapsed before Tell */ }

            if (cts.IsCancellationRequested)
            {
                _logger.LogWarning("BrainActor mirror: reply timed out or faulted");
                return;
            }
        }

        if (!actor.Tell(message))
        {
            _logger.LogWarning("BrainActor mirror: Tell failed (mailbox closed)");
            return;
        }

        try
        {
            await reply.Task.WaitAsync(cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BrainActor mirror: reply timed out or faulted");
        }
    }

    /// <summary>Mirrors a fire-and-forget message to the shadow actor, logging a closed mailbox.</summary>
    private void MirrorFireAndForget(IBrainMessage message)
    {
        if (Volatile.Read(ref _brainActor) is not { } actor)
            return;

        if (!actor.Tell(message))
            _logger.LogWarning("BrainActor mirror: Tell failed for {Type} (mailbox closed)", message.GetType().Name);
    }

    /// <summary>
    /// Fires a note injection to the shadow BrainActor for a goal session.
    /// Fire-and-forget: never blocks the caller, never propagates exceptions.
    /// </summary>
    private void FireShadowNote(string goalId, string note)
    {
        if (!_useBrainActors)
            return;

        if (Volatile.Read(ref _brainActor) is not { } actor)
            return;

        var msg = BrainActorMessages.CreateInjectNoteOnChildMessage(goalId, note);
        if (!actor.Tell(msg))
        {
            _logger.LogWarning("FireShadowNote: Tell failed for goal {GoalId} (mailbox closed)", goalId);
            return;
        }

        _logger.LogInformation("Shadow note injection fired for goal {GoalId}", goalId);

        msg.Reply.Task.ContinueWith(
            t =>
            {
                if (t.IsCompletedSuccessfully)
                    _logger.LogInformation("Shadow note injection completed for goal {GoalId}", goalId);
                else if (t.IsFaulted)
                    _logger.LogWarning(t.Exception, "FireShadowNote: shadow faulted for goal {GoalId}", goalId);
                else if (t.IsCanceled)
                    _logger.LogInformation("Shadow note injection canceled for goal {GoalId}", goalId);
            },
            TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <inheritdoc />
    public async Task UpdateModelAsync(string model, int? maxContextTokens = null, CancellationToken ct = default)
    {
        EnsureConnected();

        await _sessionLock.WaitAsync(ct);
        try
        {
            _modelOverride = model;
            if (maxContextTokens.HasValue)
                _maxContextTokens = maxContextTokens.Value;

            var (_, _, reasoning) = ChatClientFactory.ParseProviderModelAndReasoning(model);
            _reasoningEffort = reasoning;

            // Refresh the master session registry entry. Existing goal contexts keep their original
            // model/maxContextTokens snapshot (captured at fork time) — only NEW contexts created
            // after this update will use the new config.
            _sessionRegistry?.RegisterOrUpdate(new LlmSessionInfo
            {
                SessionId = "brain-master",
                SessionType = LlmSessionType.Brain,
                Model = _modelOverride,
                Status = "idle",
                CurrentTokens = _masterSession.EstimatedContextTokens,
                MaxTokens = _maxContextTokens,
            });
        }
        finally { _sessionLock.Release(); }

        _logger.LogInformation("Brain model updated to '{Model}' with context window {ContextWindow}",
            model, _maxContextTokens);

        var updateMsg = BrainActorMessages.CreateUpdateModelMessage(model, maxContextTokens);
        await MirrorAsync(updateMsg, updateMsg.Reply, TimeSpan.FromSeconds(3));
    }

    /// <inheritdoc />
    public async Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default)
    {
        if (_repoManager is null)
        {
            _logger.LogDebug("No BrainRepoManager configured — skipping repo clone for '{RepoName}'", repoName);
            return;
        }

        await _repoManager.EnsureCloneAsync(repoName, repoUrl, defaultBranch, ct);
        _logger.LogInformation("Brain repo ready for '{RepoName}' at {ClonePath}",
            repoName, _repoManager.GetClonePath(repoName));
    }

    /// <summary>Registers a pipeline as active so the <c>get_goal</c> tool can include iteration and phase context.</summary>
    /// <param name="pipeline">The active goal pipeline.</param>
    public void RegisterActivePipeline(GoalPipeline pipeline)
    {
        _activePipelines[pipeline.GoalId] = pipeline;
        MirrorFireAndForget(new RegisterPipelineMessage(pipeline.GoalId, pipeline));
    }

    /// <summary>Removes a pipeline from the active-pipeline registry once a goal completes or fails.</summary>
    public void DeregisterActivePipeline(string goalId)
    {
        _activePipelines.TryRemove(goalId, out _);
        MirrorFireAndForget(new DeregisterPipelineMessage(goalId));
    }

    /// <summary>Builds the AIFunction tools that the Brain LLM can call.</summary>
    private List<AITool> BuildBrainTools()
    {
        var pipelineResolver = (Func<string, Task<GoalPipeline?>>)(goalId =>
            Task.FromResult(_activePipelines.TryGetValue(goalId, out var p) ? p : null));

        return
        [
            AIFunctionFactory.Create(
                ([Description("The question to forward to the Composer for resolution.")] string question,
                 [Description("The reason why the Brain cannot answer this question from the codebase.")] string reason) =>
                {
                    var ctx = _currentContext.Value;
                    if (ctx is null)
                    {
                        _logger.LogWarning("Tool call with no active context");
                        return "Tool call with no active context.";
                    }
                    ctx.LastToolCallResult = new EscalateResult(question, reason);
                    return "Escalation recorded.";
                },
                "escalate_to_composer",
                "Escalate a question to the Composer when the Brain cannot answer from the codebase alone."),
            AIFunctionFactory.Create(
                ([Description("Ordered phase names, e.g. [\"coding\",\"testing\",\"docwriting\",\"review\",\"merging\"]")] string[] phases,
                 [Description("JSON object with per-phase instructions.\n  Single-round: {\"coding\": \"...\", \"review\": \"...\"}\n  Multi-round:  {\"coding-1\": \"step 1: revert...\", \"coding-2\": \"step 2: restructure...\", \"review\": \"...\"}")] string phase_instructions,
                 [Description("Why you chose this iteration plan")] string reason,
                 [Description("Optional JSON-encoded dict of phase name to model tier, e.g. {\"coding\":\"premium\"}. Valid phases: coding, testing, docwriting, review, improve. Valid tiers: standard, premium. Omitted phases use the default tier.")] string? model_tiers = null) =>
                {
                    var (valid, validationError) = BrainTools.ValidateIterationPlan(
                        phases ?? [], phase_instructions, reason, model_tiers);
                    if (!valid) return validationError!;

                    var ctx = _currentContext.Value;
                    if (ctx is null)
                    {
                        _logger.LogWarning("Tool call with no active context");
                        return "Tool call with no active context.";
                    }
                    ctx.LastToolCallResult = new IterationPlanResult(phases ?? [], phase_instructions, reason, model_tiers);
                    return "Iteration plan recorded.";
                },
                "report_iteration_plan",
                "Report your iteration plan — which phases to run and in what order."),
            .. BrainTools.BuildDependencyTools(_goalStore, pipelineResolver, _knowledgeGraph, _logger),
        ];
    }

    /// <summary>Base type for results captured from Brain tool calls.</summary>
    internal abstract record BrainToolCallResult(string ToolName);

    /// <summary>Result of an <c>escalate_to_composer</c> tool call.</summary>
    internal sealed record EscalateResult(string Question, string Reason)
        : BrainToolCallResult("escalate_to_composer");

    /// <summary>Result of a <c>report_iteration_plan</c> tool call.</summary>
    internal sealed record IterationPlanResult(
        string[] Phases,
        string PhaseInstructions,
        string Reason,
        string? ModelTiers)
        : BrainToolCallResult("report_iteration_plan");

    /// <summary>Creates all per-goal Brain resources (chat client, compaction client, coding
    /// agent) and persists the goal session. Callers must hold <see cref="_sessionLock"/>.</summary>
    private async Task<GoalBrainContext> CreateGoalBrainContextAsync(string goalId, AgentSession session, CancellationToken ct)
    {
        var model = _modelOverride;
        var maxTokens = _maxContextTokens;
        var reasoning = _reasoningEffort;
        var systemPrompt = _systemPrompt;

        IChatClient chatClient;
        bool ownsClient;
        if (_injectedChatClient is not null) { chatClient = _injectedChatClient; ownsClient = false; }
        else { chatClient = _chatClientFactory(model); ownsClient = true; }

        IChatClient? compactionClient = null;
        try
        {
            compactionClient = !string.IsNullOrEmpty(_compactionModel)
                ? ChatClientFactory.Create(_compactionModel) : null;

            var workDir = _repoManager?.WorkDirectory ?? _stateDir;
            var agent = new CodingAgent(chatClient, new AgentOptions
            {
                WorkDirectory = workDir,
                MaxSteps = _maxSteps,
                EnableBash = false,
                EnableFileOps = _repoManager is not null,
                EnableFileWrites = false,
                EnableSkills = false,
                SystemPrompt = systemPrompt,
                CustomTools = _brainTools,
                MaxContextTokens = maxTokens,
                EnableAutoCompaction = true,
                AutoLoadWorkspaceInstructions = false,
                ReasoningEffort = reasoning,
                Logger = _logger,
                CompactionClient = compactionClient,
                CompactionMaxTokens = !string.IsNullOrEmpty(_compactionModel)
                    ? _hiveConfig?.TryGetContextWindowForModel(_compactionModel)
                    : null,
                OnCompacted = r =>
                {
                    _logger.LogInformation(
                        "Brain context compaction: {TokensBefore} \u2192 {TokensAfter} tokens ({ReductionPercent}% reduction), {MessagesBefore} \u2192 {MessagesAfter} messages",
                        r.TokensBefore, r.TokensAfter, r.ReductionPercent, r.MessagesBefore, r.MessagesAfter);
                },
            });

            await session.SaveAsync(GetGoalSessionFilePath(goalId), ct);

            _sessionRegistry?.RegisterOrUpdate(new LlmSessionInfo
            {
                SessionId = $"brain-goal-{goalId}",
                SessionType = LlmSessionType.BrainGoal,
                GoalId = goalId,
                Model = model,
                Status = "idle",
                CurrentTokens = session.EstimatedContextTokens,
                MaxTokens = maxTokens,
            });

            return new GoalBrainContext(goalId, chatClient, ownsClient, compactionClient, agent, session, model, maxTokens, reasoning, systemPrompt);
        }
        catch
        {
            // Dispose partial resources on failure. CodingAgent is not IDisposable.
            try { compactionClient?.Dispose(); } catch { }
            if (ownsClient) { try { chatClient.Dispose(); } catch { } }
            throw;
        }
    }

    /// <summary>Returns the file path for a goal-specific forked session.</summary>
    private string GetGoalSessionFilePath(string goalId)
        => Path.Combine(_stateDir, $"brain-goal-{goalId}.json");

    /// <summary>Returns the file path for the master Brain session.</summary>
    private string GetMasterSessionFilePath() => Path.Combine(_stateDir, "brain-master.json");

    /// <summary>Refreshes the <c>brain-master</c> registry entry with the current master session tokens.</summary>
    private void RefreshMasterSessionRegistry(long? currentTokens = null)
    {
        _sessionRegistry?.RegisterOrUpdate(new LlmSessionInfo
        {
            SessionId = "brain-master",
            SessionType = LlmSessionType.Brain,
            Model = _modelOverride,
            Status = "idle",
            CurrentTokens = currentTokens ?? _masterSession.EstimatedContextTokens,
            MaxTokens = _maxContextTokens,
        });
    }

    /// <summary>Forks the master session for a goal and creates a dedicated Brain context.</summary>
    /// <inheritdoc />
    public async Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default)
    {
        EnsureConnected();

        await _sessionLock.WaitAsync(ct);
        try
        {
            if (_disposing)
                throw new InvalidOperationException("Brain is being disposed.");
            if (_resetting)
                throw new InvalidOperationException("Brain is being reset.");
            if (_deletingGoals.ContainsKey(goalId))
                throw new InvalidOperationException($"Goal '{goalId}' is being deleted.");

            // Idempotent: skip creation when a context already exists, but still mirror below.
            if (!_goalContexts.ContainsKey(goalId))
            {
                var goalSession = _masterSession.Fork($"brain-goal-{goalId}");
                var context = await CreateGoalBrainContextAsync(goalId, goalSession, ct);
                _goalContexts[goalId] = context;

                _logger.LogInformation("Forked master session for goal '{GoalId}' ({Messages} messages)",
                    goalId, goalSession.MessageHistory.Count);
            }
        }
        finally
        {
            _sessionLock.Release();
        }

        var forkMsg = BrainActorMessages.CreateForkSessionMessage(goalId);
        await MirrorAsync(forkMsg, forkMsg.Reply, TimeSpan.FromSeconds(3));
    }

    /// <summary>Loads (or forks) an existing goal session from disk and creates its Brain context.</summary>
    /// <inheritdoc />
    public async Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default)
    {
        EnsureConnected();

        await _sessionLock.WaitAsync(ct);
        try
        {
            if (_disposing)
                throw new InvalidOperationException("Brain is being disposed.");
            if (_resetting)
                throw new InvalidOperationException("Brain is being reset.");
            if (_deletingGoals.ContainsKey(goalId))
                throw new InvalidOperationException($"Goal '{goalId}' is being deleted.");

            // Idempotent: skip creation when a context already exists, but still mirror below.
            if (!_goalContexts.ContainsKey(goalId))
            {
                var session = await LoadOrForkGoalSessionAsync(goalId, ct);
                _goalContexts[goalId] = await CreateGoalBrainContextAsync(goalId, session, ct);

                _logger.LogInformation("Registered existing Brain context for goal '{GoalId}' ({Messages} messages)",
                    goalId, session.MessageHistory.Count);
            }
        }
        finally
        {
            _sessionLock.Release();
        }

        var regMsg = BrainActorMessages.CreateRegisterExistingSessionMessage(goalId);
        await MirrorAsync(regMsg, regMsg.Reply, TimeSpan.FromSeconds(3));
    }

    /// <summary>Loads a goal session from disk, falling back to a fresh fork of the master session.</summary>
    private async Task<AgentSession> LoadOrForkGoalSessionAsync(string goalId, CancellationToken ct)
    {
        var goalSessionFile = GetGoalSessionFilePath(goalId);
        if (!File.Exists(goalSessionFile))
            return _masterSession.Fork($"brain-goal-{goalId}");

        try
        {
            return await AgentSession.LoadAsync(goalSessionFile, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to load existing goal session for '{GoalId}' — forking from master", goalId);
            return _masterSession.Fork($"brain-goal-{goalId}");
        }
    }

    /// <summary>Deletes the persisted goal session file and disposes the goal's Brain context.</summary>
    /// <inheritdoc />
    public async Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default)
    {
        await DeleteGoalSessionCoreAsync(goalId, ct);

        var deleteMsg = BrainActorMessages.CreateDeleteSessionMessage(goalId);
        await MirrorAsync(deleteMsg, deleteMsg.Reply, TimeSpan.FromSeconds(3));
    }

    /// <summary>Authoritative goal-session deletion without shadow-actor mirroring.</summary>
    private async Task DeleteGoalSessionCoreAsync(string goalId, CancellationToken ct)
    {
        EnsureConnected();

        await _sessionLock.WaitAsync(ct);
        GoalBrainContext? context;
        try
        {
            // Single-owner deletion: if another call is already deleting this goal, return early.
            // Only the owning delete adds the marker and removes it in the finally block, so a
            // concurrent duplicate delete cannot clear the marker while the owner is still draining.
            if (_deletingGoals.ContainsKey(goalId))
                return;

            _deletingGoals[goalId] = true;
            _goalContexts.TryRemove(goalId, out context);
        }
        finally { _sessionLock.Release(); }

        try
        {
            if (context is not null)
            {
                context.Release(); // release dictionary reference
                await context.WaitForDrainAsync(); // non-cancelable — must drain
                try { context.ActiveCallCts?.Cancel(); } catch { }
            }

            var file = GetGoalSessionFilePath(goalId);
            try { if (File.Exists(file)) File.Delete(file); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete session file for {GoalId}", goalId); }

            _sessionRegistry?.Unregister($"brain-goal-{goalId}");

            if (context is not null)
            {
                try { await context.DisposeAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose context for {GoalId}", goalId); }
            }
        }
        finally
        {
            _deletingGoals.TryRemove(goalId, out _);
        }
    }

    /// <inheritdoc />
    public bool GoalSessionExists(string goalId)
    {
        _sessionLock.Wait();
        try
        {
            return File.Exists(GetGoalSessionFilePath(goalId));
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>Persists the master Brain session to disk.</summary>
    internal async Task SaveSessionAsync(CancellationToken ct = default)
    {
        await _sessionLock.WaitAsync(ct);
        try
        {
            await SaveSessionCoreAsync(ct);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>Core master-session save logic. Callers must hold <see cref="_sessionLock"/>.</summary>
    private async Task SaveSessionCoreAsync(CancellationToken ct)
    {
        var path = GetMasterSessionFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await _masterSession.SaveAsync(path, ct);
        _logger.LogDebug("Brain master session saved ({Count} messages)", _masterSession.MessageHistory.Count);
    }

    /// <inheritdoc/>
    public async Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct)
    {
        EnsureConnected();

        pipeline.Conversation.Add(new ConversationEntry("system", note, pipeline.Iteration, "plan-adjustment"));

        await _sessionLock.WaitAsync(ct);
        try
        {
            _masterSession.MessageHistory.Add(new ChatMessage(ChatRole.User,
                $"SYSTEM NOTE (plan adjustment for goal {pipeline.GoalId}):\n\n{note}"));
            _masterSession.MessageHistory.Add(new ChatMessage(ChatRole.Assistant,
                "Acknowledged. I have noted the plan adjustment and will craft prompts for all phases in the final plan."));
            _masterSession.LastKnownContextTokens = 0;
            RefreshMasterSessionRegistry();
        }
        finally { _sessionLock.Release(); }

        _logger.LogInformation("Injected plan adjustment note for goal {GoalId}: {Note}", pipeline.GoalId, note);

        FireShadowNote(pipeline.GoalId, note);
    }

    /// <inheritdoc/>
    public async Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default)
    {
        EnsureConnected();

        if (!string.IsNullOrWhiteSpace(instructions))
        {
            await _sessionLock.WaitAsync(ct);
            try
            {
                _systemPrompt = $"{DefaultSystemPrompt}\n\n{instructions}";
            }
            finally { _sessionLock.Release(); }

            _logger.LogInformation("Updated Brain system prompt with new orchestrator instructions ({Chars} chars)",
                instructions.Length);
        }

        var injectMsg = BrainActorMessages.CreateInjectOrchestratorInstructionsMessage(_systemPrompt);
        await MirrorAsync(injectMsg, injectMsg.Reply, TimeSpan.FromSeconds(3));
    }

    /// <summary>Asks the Brain to plan which phases should run during the current iteration.</summary>
    public async Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default)
    {
        EnsureConnected();

        var prompt = BrainPromptBuilder.BuildPlanningPrompt(pipeline, additionalContext);

        try
        {
            const int maxToolAttempts = 3;
            string currentPrompt = prompt;

            for (int attempt = 1; attempt <= maxToolAttempts; attempt++)
            {
                var (response, toolCall) = await Shared.CopilotRetryPolicy.ExecuteAsync(
                    () => ExecuteBrainAsync(currentPrompt, pipeline.GoalId, ct, status: "planning"),
                    onRetry: (retryAttempt, delay, ex) =>
                    {
                        _logger.LogWarning(
                            "Brain iteration plan call failed (attempt {Attempt}/{Max}): {Error}. Retrying in {Delay}s",
                            retryAttempt, Shared.CopilotRetryPolicy.MaxRetries + 1, ex.Message, delay.TotalSeconds);
                    },
                    ct);

                pipeline.Conversation.Add(new ConversationEntry("user", currentPrompt, pipeline.Iteration, "planning"));
                pipeline.Conversation.Add(new ConversationEntry("assistant", response, pipeline.Iteration, "planning"));

                // Check for escalate_to_composer BEFORE report_iteration_plan
                if (toolCall is EscalateResult escalation)
                {
                    var escalationQuestion = string.IsNullOrEmpty(escalation.Question) ? "Brain requested clarification during planning" : escalation.Question;
                    var escalationReason = string.IsNullOrEmpty(escalation.Reason) ? "Brain requested escalation" : escalation.Reason;
                    _logger.LogInformation(
                        "Brain escalated planning for goal {GoalId}: {Reason}", pipeline.GoalId, escalationReason);
                    return PlanResult.Escalated(escalationQuestion, escalationReason);
                }

                if (toolCall is IterationPlanResult iterationPlanResult)
                {
                    var plan = BrainPlanParser.BuildIterationPlanFromToolCall(iterationPlanResult);

                    if (plan is { Phases.Count: > 0 })
                    {
                        _logger.LogInformation(
                            "Brain planned iteration {Iteration} for goal {GoalId}: [{Phases}] — {Reason}",
                            pipeline.Iteration, pipeline.GoalId,
                            string.Join(", ", plan.Phases), plan.Reason ?? "no reason");
                        return PlanResult.Success(plan);
                    }

                    _logger.LogWarning("Failed to parse iteration plan from Brain response: {Response}",
                        BrainPromptBuilder.Truncate(response, Constants.TruncationShort));
                    break;
                }

                if (attempt < maxToolAttempts)
                {
                    _logger.LogWarning(
                        "Brain responded with text instead of calling report_iteration_plan (attempt {Attempt}/{Max}). Nudging.",
                        attempt, maxToolAttempts);
                    currentPrompt = "You must call the report_iteration_plan tool now. Do not respond with text.";
                }
                else
                {
                    _logger.LogWarning(
                        "Brain did not call report_iteration_plan after {MaxAttempts} attempts for goal {GoalId}",
                        maxToolAttempts, pipeline.GoalId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Brain iteration planning failed for goal {GoalId}", pipeline.GoalId);
            pipeline.Conversation.Add(new ConversationEntry("system", $"Error: {ex.Message}", pipeline.Iteration, "error"));
        }

        _logger.LogInformation("Using default iteration plan for goal {GoalId}", pipeline.GoalId);
        return PlanResult.Success(IterationPlan.Default());
    }

    /// <summary>Generates a summary of the completed goal's work and appends it to the master session.</summary>
    public async Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default)
    {
        EnsureConnected();

        var prompt = BrainPromptBuilder.BuildSummarizePrompt(pipeline);
        var (summaryText, _) = await ExecuteBrainAsync(prompt, pipeline.GoalId, ct, status: "summarizing");
        var summary = string.IsNullOrWhiteSpace(summaryText)
            ? $"Goal '{pipeline.GoalId}' completed."
            : summaryText.Trim();

        // Merge into master (AFTER releasing lease + gate — NO nested locks)
        await _sessionLock.WaitAsync(ct);
        try
        {
            _masterSession.MessageHistory.Add(new ChatMessage(ChatRole.User,
                $"[Goal completed: {pipeline.GoalId}] Summarize what was done."));
            _masterSession.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, summary));
            _masterSession.LastKnownContextTokens = 0;
            await SaveSessionCoreAsync(ct);
            RefreshMasterSessionRegistry();
        }
        finally { _sessionLock.Release(); }

        var mergeMsg = BrainActorMessages.CreateMergeSummaryMessage(pipeline.GoalId, summary);
        await MirrorAsync(mergeMsg, mergeMsg.Reply, TimeSpan.FromSeconds(3));

        await DeleteGoalSessionCoreAsync(pipeline.GoalId, ct);

        var deleteMsg = BrainActorMessages.CreateDeleteSessionMessage(pipeline.GoalId);
        await MirrorAsync(deleteMsg, deleteMsg.Reply, TimeSpan.FromSeconds(3));

        _logger.LogInformation("Merged summary for goal '{GoalId}' into master session: {Summary}",
            pipeline.GoalId, BrainPromptBuilder.Truncate(summary, 200));

        return summary;
    }

    /// <inheritdoc />
    public async Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default)
    {
        if (!_connected)
        {
            _logger.LogDebug("Brain not connected — skipping commit message generation for goal {GoalId}", pipeline.GoalId);
            return null;
        }

        var prompt = BrainPromptBuilder.BuildCommitMessagePrompt(pipeline);

        try
        {
            string? message = null;

            await Shared.CopilotRetryPolicy.ExecuteAsync(
                async () =>
                {
                    var (response, _) = await ExecuteBrainAsync(prompt, pipeline.GoalId, ct, status: "generating-commit-message");

                    if (!string.IsNullOrWhiteSpace(response))
                        message = response.Trim();
                    else
                        throw new InvalidOperationException(
                            $"Brain returned empty commit message for {pipeline.GoalId}");
                },
                onRetry: (attempt, delay, ex) =>
                {
                    _logger.LogWarning(
                        "Brain commit message generation failed for {GoalId} (attempt {Attempt}/{Max}): {Error}. Retrying in {Delay}s",
                        pipeline.GoalId, attempt, Shared.CopilotRetryPolicy.MaxRetries + 1, ex.Message, delay.TotalSeconds);
                },
                ct);

            _logger.LogDebug("Brain generated commit message for goal {GoalId}: {Message}",
                pipeline.GoalId, message);

            return message;
        }
        catch (OperationCanceledException)
        {
            throw; // Preserve cancellation - do NOT swallow it
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate Brain commit message for goal {GoalId} — will use fallback",
                pipeline.GoalId);
            return null;
        }
    }

    /// <summary>Asks the Brain to craft a prompt for the specified phase's worker.</summary>
    public async Task<PromptResult> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default)
    {
        EnsureConnected();

        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, phase, additionalContext);

        try
        {
            var craftedPrompt = await Shared.CopilotRetryPolicy.ExecuteAsync(
                async () =>
                {
                    _logger.LogDebug("Brain craft-prompt request for {GoalId} (phase={Phase}):\n{Prompt}",
                        pipeline.GoalId, phase, BrainPromptBuilder.Truncate(prompt, Constants.TruncationVerbose));

                    var (response, toolCall) = await ExecuteBrainAsync(prompt, pipeline.GoalId, ct, status: "crafting-prompt");

                    _logger.LogDebug("Brain craft-prompt response for {GoalId}:\n{Response}",
                        pipeline.GoalId, BrainPromptBuilder.Truncate(response, Constants.TruncationVerbose));

                    pipeline.Conversation.Add(new ConversationEntry("user", prompt, pipeline.Iteration, "craft-prompt"));
                    pipeline.Conversation.Add(new ConversationEntry("assistant", response, pipeline.Iteration, "craft-prompt"));

                    // Check for escalate_to_composer tool call
                    if (toolCall is EscalateResult escalation)
                    {
                        var escalationQuestion = string.IsNullOrEmpty(escalation.Question) ? "Brain requested clarification during prompt crafting" : escalation.Question;
                        var escalationReason = string.IsNullOrEmpty(escalation.Reason) ? "Brain requested escalation" : escalation.Reason;
                        _logger.LogInformation(
                            "Brain escalated prompt crafting for {GoalId} phase {Phase}: {Reason}",
                            pipeline.GoalId, phase, escalationReason);
                        // Return a sentinel that signals escalation — the caller unwraps it
                        return $"__ESCALATION__{escalationQuestion}\x00{escalationReason}";
                    }

                    if (string.IsNullOrWhiteSpace(response))
                        throw new InvalidOperationException(
                            $"Brain returned empty prompt for {pipeline.GoalId} phase {phase}");

                    return response;
                },
                onRetry: (attempt, delay, ex) =>
                {
                    _logger.LogWarning(
                        "Brain craft-prompt failed for {GoalId} (attempt {Attempt}/{Max}): {Error}. Retrying in {Delay}s",
                        pipeline.GoalId, attempt, Shared.CopilotRetryPolicy.MaxRetries + 1, ex.Message, delay.TotalSeconds);
                    pipeline.Conversation.Add(new ConversationEntry("system", $"Error on attempt {attempt}: {ex.Message}", pipeline.Iteration, "error"));
                },
                ct);

            // Unwrap escalation sentinel
            if (craftedPrompt.StartsWith("__ESCALATION__", StringComparison.Ordinal))
            {
                var payload = craftedPrompt["__ESCALATION__".Length..];
                var sepIdx = payload.IndexOf('\x00');
                var question = sepIdx >= 0 ? payload[..sepIdx] : payload;
                var reason = sepIdx >= 0 ? payload[(sepIdx + 1)..] : string.Empty;
                return PromptResult.Escalated(question, reason);
            }

            return PromptResult.Success(craftedPrompt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Brain failed to craft prompt for {GoalId} phase {Phase} — using fallback",
                pipeline.GoalId, phase);
            pipeline.Conversation.Add(new ConversationEntry("system", $"CraftPrompt error: {ex.Message}", pipeline.Iteration, "error"));
            return PromptResult.Success($"Work on: {pipeline.Description}");
        }
    }

    /// <inheritdoc />
    public async Task<BrainResponse> AskQuestionAsync(
        string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default)
    {
        EnsureConnected();

        var prompt = BrainPromptBuilder.BuildAskQuestionPrompt(goalId, iteration, phase, workerRole, question);

        try
        {
            var (response, toolCall) = await Shared.CopilotRetryPolicy.ExecuteAsync(
                () => ExecuteBrainAsync(prompt, goalId, ct, status: "answering-question"),
                onRetry: (attempt, delay, ex) =>
                {
                    _logger.LogWarning(
                        "Brain AskQuestion call failed (attempt {Attempt}/{Max}): {Error}. Retrying in {Delay}s",
                        attempt, Shared.CopilotRetryPolicy.MaxRetries + 1, ex.Message, delay.TotalSeconds);
                },
                ct);

            if (toolCall is EscalateResult escalation)
            {
                var escalationQuestion = string.IsNullOrEmpty(escalation.Question) ? question : escalation.Question;
                var escalationReason = string.IsNullOrEmpty(escalation.Reason) ? "Brain requested escalation" : escalation.Reason;
                _logger.LogInformation(
                    "Brain escalated question for goal {GoalId} via tool call: {Reason}", goalId, escalationReason);
                return BrainResponse.Escalated(escalationQuestion, escalationReason);
            }

            return BrainResponse.Answer(response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Brain AskQuestionAsync failed for goal {GoalId} — returning fallback", goalId);
            return BrainResponse.Answer("Brain encountered an error. Please proceed with your best judgment.");
        }
    }

    /// <summary>Formats a context-usage log message for the Brain LLM call.</summary>
    internal static string FormatContextUsageMessage(long inputTokens, int contextWindow, string callerName) =>
        BrainPromptBuilder.FormatContextUsageMessage(inputTokens, contextWindow, callerName);

    private async Task<(string Text, BrainToolCallResult? ToolCall)> ExecuteBrainAsync(
        string prompt, string goalId, CancellationToken ct,
        string status = "idle",
        [System.Runtime.CompilerServices.CallerMemberName] string callerName = "")
    {
        EnsureConnected();

        if (_useBrainActors && Volatile.Read(ref _brainActor) is { } actor)
            return await ExecuteBrainViaActorAsync(actor, prompt, goalId, ct, status, callerName);

        return await ExecuteBrainViaContextAsync(prompt, goalId, ct, status, callerName);
    }

    /// <summary>Routes the Brain LLM call through the BrainActor's per-goal child actor.</summary>
    private async Task<(string Text, BrainToolCallResult? ToolCall)> ExecuteBrainViaActorAsync(
        BrainActor actor, string prompt, string goalId, CancellationToken ct,
        string status, string callerName)
    {
        var msg = BrainActorMessages.CreateExecutePromptOnChildMessage(goalId, prompt, ct);
        if (!actor.Tell(msg))
            throw new InvalidOperationException("BrainActor mailbox closed.");

        var result = await msg.Reply.Task.WaitAsync(ct);

        BrainToolCallResult? mapped = result.ToolCall switch
        {
            EscalateToolResult escalate => new EscalateResult(escalate.Question, escalate.Reason),
            PlanToolResult plan => MapPlan(plan),
            null => null,
            _ => throw new InvalidOperationException(
                $"Unknown tool call result type '{result.ToolCall.GetType().Name}' from actor."),
        };

        _logger.LogInformation("Brain execution via actor for {GoalId} ({Caller})", goalId, callerName);

        return (result.Text, mapped);

        static IterationPlanResult MapPlan(PlanToolResult plan)
        {
            var (valid, error) = BrainTools.ValidateIterationPlan(
                plan.Phases, plan.PhaseInstructions, plan.Reason, plan.ModelTiers);
            if (!valid)
                throw new InvalidOperationException($"Invalid iteration plan from actor: {error}");

            return new IterationPlanResult(plan.Phases, plan.PhaseInstructions, plan.Reason, plan.ModelTiers);
        }
    }

    /// <summary>Executes the Brain LLM call against the local per-goal context (non-actor path).</summary>
    private async Task<(string Text, BrainToolCallResult? ToolCall)> ExecuteBrainViaContextAsync(
        string prompt, string goalId, CancellationToken ct,
        string status,
        string callerName)
    {
        if (!_goalContexts.TryGetValue(goalId, out var context) || !context.TryAcquire())
            throw new InvalidOperationException($"No Brain context for goal '{goalId}'.");

        try
        {
            await context.Gate.WaitAsync(ct);
            // Capture a STABLE reference to the goal session before the call. Idle-status
            // restoration in the finally block must use this reference — not context.Session read
            // fresh — so a mid-call session replacement (e.g. overflow recovery) cannot corrupt the
            // restored token count.
            var session = context.Session;
            try
            {
                _currentContext.Value = context;
                context.LastToolCallResult = null;
                context.ActiveCallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                context.ActiveCallCts.CancelAfter(TimeSpan.FromMinutes(Constants.TaskTimeoutMinutes));

                _sessionRegistry?.RegisterOrUpdate(new LlmSessionInfo
                {
                    SessionId = $"brain-goal-{goalId}",
                    SessionType = LlmSessionType.BrainGoal,
                    GoalId = goalId,
                    Model = context.Model,
                    Status = status,
                    CurrentTokens = session.EstimatedContextTokens,
                    MaxTokens = context.MaxContextTokens,
                });

                var result = await context.Agent.ExecuteAsync(session, prompt, context.ActiveCallCts.Token);
                var responseText = result.Message;

                // Log usage
                if (result.Usage is not null)
                {
                    _logger.LogDebug(
                        "Brain Usage: model={Model} in={InputTokens} out={OutputTokens} tools={ToolCalls}",
                        result.ModelId, result.Usage.InputTokenCount, result.Usage.OutputTokenCount,
                        result.ToolCallCount);
                }

                // Log context size (compaction is logged via OnCompacted callback)
                var estimatedTokens = session.EstimatedContextTokens;
                var usagePct = context.MaxContextTokens > 0 ? (int)(estimatedTokens * 100.0 / context.MaxContextTokens) : 0;
                _logger.LogInformation(
                    "Brain context: messages={Messages} ~tokens={EstTokens}/{Limit} ({Pct}%) cumIn={CumIn} cumOut={CumOut}",
                    session.MessageHistory.Count, estimatedTokens, context.MaxContextTokens,
                    usagePct, session.InputTokensUsed, session.OutputTokensUsed);

                var contextTokens = session.LastKnownContextTokens > 0
                    ? session.LastKnownContextTokens
                    : session.EstimatedContextTokens;
                _logger.LogInformation("{Message}", FormatContextUsageMessage(contextTokens, context.MaxContextTokens, callerName));

                _logger.LogDebug("Brain response ({Length} chars), tool={Tool}",
                    responseText?.Length ?? 0, context.LastToolCallResult?.ToolName ?? "none");

                // Auto-save session after each Brain call
                try { await session.SaveAsync(GetGoalSessionFilePath(goalId), ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to save Brain session"); }

                return (responseText ?? string.Empty, context.LastToolCallResult);
            }
            finally
            {
                _currentContext.Value = null;
                context.ActiveCallCts?.Dispose();
                context.ActiveCallCts = null;
                _sessionRegistry?.RegisterOrUpdate(new LlmSessionInfo
                {
                    SessionId = $"brain-goal-{goalId}",
                    SessionType = LlmSessionType.BrainGoal,
                    GoalId = goalId,
                    Model = context.Model,
                    Status = "idle",
                    CurrentTokens = session.EstimatedContextTokens,
                    MaxTokens = context.MaxContextTokens,
                });
                context.Gate.Release();
            }
        }
        finally
        {
            context.Release();
        }
    }

    /// <inheritdoc />
    public BrainStats? GetStats()
    {
        if (!_connected) return null;

        _sessionLock.Wait();
        try
        {
            return GetStatsCore();
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>Core stats logic. Callers must hold <see cref="_sessionLock"/>.</summary>
    private BrainStats? GetStatsCore()
    {
        var contextTokens = _masterSession.LastKnownContextTokens > 0
            ? _masterSession.LastKnownContextTokens
            : _masterSession.EstimatedContextTokens;
        var usagePct = _maxContextTokens > 0 ? (int)(contextTokens * 100.0 / _maxContextTokens) : 0;

        return new BrainStats
        {
            Model = _modelOverride,
            MessageCount = _masterSession.MessageHistory.Count,
            ContextTokens = contextTokens,
            MaxContextTokens = _maxContextTokens,
            ContextUsagePercent = usagePct,
            CumulativeInputTokens = _masterSession.InputTokensUsed,
            CumulativeOutputTokens = _masterSession.OutputTokensUsed,
            MaxSteps = _maxSteps,
            IsConnected = true,
        };
    }

    /// <inheritdoc />
    public async Task ResetSessionAsync(CancellationToken ct = default)
    {
        // NOTE: ResetSessionAsync intentionally does NOT call EnsureConnected(). With per-goal
        // contexts there is no shared agent to recreate, so a reset just drains any existing
        // goal contexts (none if never connected) and rebuilds the master session + reloads
        // orchestrator instructions from disk. This matches the design spec and lets callers
        // reset a Brain that was constructed but never connected.

        // Phase 1: Mark resetting and snapshot contexts
        List<GoalBrainContext> contextsToDrain;
        await _sessionLock.WaitAsync(ct);
        try
        {
            _resetting = true;
            contextsToDrain = _goalContexts.Values.ToList();
            _goalContexts.Clear();
        }
        finally { _sessionLock.Release(); }

        // Phase 2: Non-cancelable cleanup of all goal contexts
        foreach (var context in contextsToDrain)
        {
            context.Release();
            try { await context.WaitForDrainAsync(); } catch { }
            try { context.ActiveCallCts?.Cancel(); } catch { }
            _sessionRegistry?.Unregister($"brain-goal-{context.GoalId}");
            try { await context.DisposeAsync(); } catch { }
        }

        // Phase 3: Recreate master session
        await _sessionLock.WaitAsync(CancellationToken.None);
        try
        {
            // Re-read orchestrator instructions from disk
            var freshInstructions = _agentsManager?.GetAgentsMd(WorkerRole.Orchestrator) ?? "";
            _systemPrompt = string.IsNullOrWhiteSpace(freshInstructions)
                ? DefaultSystemPrompt
                : $"{DefaultSystemPrompt}\n\n{freshInstructions}";

            _masterSession = AgentSession.Create("brain");
            RefreshMasterSessionRegistry(currentTokens: 0);

            var sessionFile = GetMasterSessionFilePath();
            if (File.Exists(sessionFile))
                File.Delete(sessionFile);
        }
        finally
        {
            _resetting = false;
            _sessionLock.Release();
        }

        _logger.LogInformation("Brain session reset — conversation history cleared, orchestrator instructions reloaded from disk, and session file deleted.");

        await ResetShadowActorAsync();
    }

    /// <summary>
    /// Atomically detaches and disposes the shadow actor, clears its persisted state and starts a
    /// fresh shadow when brain actors are enabled and the brain is connected.
    /// </summary>
    private async Task ResetShadowActorAsync()
    {
        var oldActor = Interlocked.Exchange(ref _brainActor, null);

        // Known limitation: mirrors issued concurrently with the reset see a null actor and are
        // silently skipped. The shadow is non-authoritative, so this divergence is accepted.
        _logger.LogWarning("Shadow actor detached during reset — concurrent summaries may be permanently lost from the shadow (non-authoritative, rebuilt on next reset)");

        if (oldActor is not null)
        {
            try { await oldActor.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose shadow actor during reset"); }
        }

        if (!_useBrainActors || !_connected)
            return;

        DeleteActorStateFiles();
        _skipSessionMigration = true;
        try
        {
            await StartShadowActorAsync(CancellationToken.None);
        }
        finally
        {
            _skipSessionMigration = false;
        }
    }

    /// <summary>Deletes persisted shadow-actor session files so a new shadow starts fresh.</summary>
    private void DeleteActorStateFiles()
    {
        var actorsDir = Path.Combine(_stateDir, "actors");
        try
        {
            if (!Directory.Exists(actorsDir))
                return;

            foreach (var file in Directory.EnumerateFiles(actorsDir, "brain-*.json"))
            {
                try { File.Delete(file); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete actor state file {File}", file); }
            }

            if (File.Exists(Path.Combine(actorsDir, "brain-master.json")))
                _logger.LogWarning("Brain actor master session file survived reset deletion");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean actor state directory during reset");
        }
    }

    private void EnsureConnected()
    {
        if (!_connected)
            throw new InvalidOperationException("Brain not connected. Call ConnectAsync first.");
    }

    /// <summary>Drains and disposes all goal contexts, the shared injected chat client and the
    /// shadow actor. Idempotent — concurrent callers await the same disposal task.</summary>
    public async ValueTask DisposeAsync()
    {
        Task taskToAwait;
        lock (_lifecycleLock)
        {
            _disposing = true;
            _disposeTask ??= DisposeAsyncCore();
            taskToAwait = _disposeTask;
        }

        try { await taskToAwait; }
        catch { }
    }

    private async Task DisposeAsyncCore()
    {
        List<GoalBrainContext> contexts;
        await _sessionLock.WaitAsync();
        try
        {
            contexts = _goalContexts.Values.ToList();
            _goalContexts.Clear();
        }
        finally { _sessionLock.Release(); }

        foreach (var context in contexts)
        {
            context.Release();
            try { await context.WaitForDrainAsync(); } catch { }
            try { context.ActiveCallCts?.Cancel(); } catch { }
            _sessionRegistry?.Unregister($"brain-goal-{context.GoalId}");
            try { await context.DisposeAsync(); } catch { }
        }

        // The shadow actor (and its children) may reference the injected chat client, so it must
        // be shut down before that client is disposed.
        await DisposeActorSafelyAsync(Volatile.Read(ref _brainActor));

        if (_injectedChatClient is not null)
        {
            try { _injectedChatClient.Dispose(); } catch { }
        }

        // _sessionLock is intentionally NOT disposed: it must live for the object's lifetime
        // because ConnectAsync reads _disposing only after acquiring it.
    }

    /// <summary>Per-goal Brain execution context. Owns a dedicated gate, chat client, compaction
    /// client, coding agent, and forked session so goals can execute in parallel without sharing
    /// mutable state. Reference-counted so in-flight calls drain before disposal.</summary>
    private sealed class GoalBrainContext : IAsyncDisposable
    {
        public string GoalId { get; }
        public IChatClient? ChatClient { get; }
        public bool OwnsChatClient { get; }
        public IChatClient? CompactionClient { get; }
        public CodingAgent Agent { get; }
        public AgentSession Session { get; set; }
        public BrainToolCallResult? LastToolCallResult { get; set; }
        public string Model { get; }
        public int MaxContextTokens { get; }
        public ReasoningEffort? ReasoningEffort { get; }
        public string SystemPrompt { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CancellationTokenSource? ActiveCallCts { get; set; }
        private int _refCount = 1;
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GoalBrainContext(string goalId, IChatClient? chatClient, bool ownsChatClient,
            IChatClient? compactionClient, CodingAgent agent, AgentSession session,
            string model, int maxContextTokens, ReasoningEffort? reasoningEffort, string systemPrompt)
        {
            GoalId = goalId;
            ChatClient = chatClient;
            OwnsChatClient = ownsChatClient;
            CompactionClient = compactionClient;
            Agent = agent;
            Session = session;
            Model = model;
            MaxContextTokens = maxContextTokens;
            ReasoningEffort = reasoningEffort;
            SystemPrompt = systemPrompt;
        }

        public bool TryAcquire()
        {
            while (true)
            {
                var current = Volatile.Read(ref _refCount);
                if (current == 0) return false;
                if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                    return true;
            }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _refCount) == 0)
                _drained.TrySetResult();
        }

        public Task WaitForDrainAsync() => _drained.Task;

        public async ValueTask DisposeAsync()
        {
            try { CompactionClient?.Dispose(); } catch { }
            if (OwnsChatClient) { try { ChatClient?.Dispose(); } catch { } }
            Gate.Dispose();
            await ValueTask.CompletedTask;
        }
    }
}

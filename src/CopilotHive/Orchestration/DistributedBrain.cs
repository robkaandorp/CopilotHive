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


namespace CopilotHive.Orchestration;

/// <summary>
/// LLM-powered brain that runs inside the orchestrator container.
/// The Brain has two jobs: plan iteration phases and craft worker prompts.
/// All session state (master session, per-goal forked sessions) and LLM
/// execution live in the <see cref="BrainActor"/>; this class is a thin,
/// message-passing facade over it.
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

    private volatile bool _disposing;
    private bool _resetting;
    private bool _connected;

    /// <summary>The brain actor — the sole execution path for Brain LLM calls and session state.</summary>
    private BrainActor? _brainActor;

    /// <summary>Test seam for constructing the actor from a state directory.</summary>
    internal Func<string, BrainActor>? _actorFactory;

    /// <summary>Test seam: deletes a file during reset. Default is File.Delete.</summary>
    internal Action<string> _fileDeleter = File.Delete;

    /// <summary>Test seam: copies a file during migration. Returns true on success, false on failure.</summary>
    internal Func<string, string, bool> _fileCopier = (src, dst) => { try { File.Copy(src, dst); return true; } catch { return false; } };

    /// <summary>Guards one-shot disposal bookkeeping.</summary>
    private readonly object _lifecycleLock = new();
    private Task? _disposeTask;

    /// <summary>An externally-injected chat client passed to the actor (never owned/disposed here).</summary>
    private readonly IChatClient? _injectedChatClient;

    private string _systemPrompt;
    private readonly AgentsManager? _agentsManager;

    /// <summary>Serialises brain lifecycle transitions (connect, reset, dispose).</summary>
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

        var (_, _, reasoning) = ChatClientFactory.ParseProviderModelAndReasoning(modelOverride);
        _reasoningEffort = reasoning;

        var orchestratorInstructions = agentsManager?.GetAgentsMd(WorkerRole.Orchestrator) ?? "";
        _systemPrompt = string.IsNullOrWhiteSpace(orchestratorInstructions)
            ? DefaultSystemPrompt
            : $"{DefaultSystemPrompt}\n\n{orchestratorInstructions}";
    }

    /// <summary>Starts the brain actor, which owns the master session and all per-goal sessions. Idempotent.</summary>
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

            _connected = true;

            try
            {
                await StartBrainActorAsync(ct, throwOnFailure: true);
            }
            catch
            {
                _connected = false;
                _sessionRegistry?.Unregister("brain-master");
                throw;
            }

            RegisterMasterSessionFromActor();

            if (_disposing)
            {
                var actor = Interlocked.Exchange(ref _brainActor, null);
                if (actor is not null)
                    await actor.DisposeAsync();
                _connected = false;
                _sessionRegistry?.Unregister("brain-master");
                throw new ObjectDisposedException(nameof(DistributedBrain));
            }
        }
        finally
        {
            _sessionLock.Release();
        }

        _logger.LogInformation("Brain connected (model={Model}, contextWindow={ContextWindow})",
            _modelOverride, _maxContextTokens);
    }

    /// <summary>
    /// Registers the <c>brain-master</c> session entry using stats queried from the actor,
    /// falling back to locally-configured values when the actor cannot answer in time.
    /// </summary>
    private void RegisterMasterSessionFromActor()
    {
        var info = new LlmSessionInfo
        {
            SessionId = "brain-master",
            SessionType = LlmSessionType.Brain,
            Model = _modelOverride,
            Status = "idle",
            CurrentTokens = 0,
            MaxTokens = _maxContextTokens,
        };

        try
        {
            if (Volatile.Read(ref _brainActor) is { } actor)
            {
                var statsMsg = BrainActorMessages.CreateGetStatsMessage();
                if (actor.Tell(statsMsg))
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    var stats = statsMsg.Reply.Task.WaitAsync(timeoutCts.Token).GetAwaiter().GetResult();
                    if (stats is not null)
                    {
                        info = info with
                        {
                            Model = stats.Model,
                            CurrentTokens = stats.ContextTokens,
                            MaxTokens = stats.MaxContextTokens,
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query BrainActor stats on connect — registering fallback values");
        }

        _sessionRegistry?.RegisterOrUpdate(info);
    }

    /// <summary>
    /// Starts the <see cref="BrainActor"/>. Startup failures are logged; when
    /// <paramref name="throwOnFailure"/> is set they are rethrown. Caller cancellation propagates.
    /// </summary>
    private async Task StartBrainActorAsync(CancellationToken ct, bool throwOnFailure = false)
    {
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
                    knowledgeGraph: _knowledgeGraph,
                    sessionRegistry: _sessionRegistry);
            actor.Start();

            var connectMsg = BrainActorMessages.CreateConnectMessage();
            if (!actor.Tell(connectMsg))
                throw new InvalidOperationException("BrainActor mailbox closed");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            await connectMsg.Reply.Task.WaitAsync(timeoutCts.Token);

            Volatile.Write(ref _brainActor, actor);
            actor = null;
            _logger.LogInformation("BrainActor started (state dir: {Dir})", actorStateDir);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await DisposeActorSafelyAsync(actor);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BrainActor startup failed");
            await DisposeActorSafelyAsync(actor);
            if (throwOnFailure) throw;
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
    /// The <code>actors/.migrated</code> marker controls one-time migration: files are copied
    /// only when the actor file is missing, legacy files are never deleted, and actor files are
    /// never overwritten. The marker is created after processing even if individual copies failed.
    /// </summary>
    private void MigrateSessionFiles(string actorStateDir)
    {
        if (Volatile.Read(ref _resetting))
            return;

        var markerPath = Path.Combine(actorStateDir, ".migrated");
        if (File.Exists(markerPath))
            return;

        var legacyMasterFile = Path.Combine(_stateDir, "brain-master.json");
        var actorMasterFile = Path.Combine(actorStateDir, "brain-master.json");
        if (File.Exists(legacyMasterFile) && !File.Exists(actorMasterFile))
        {
            if (_fileCopier(legacyMasterFile, actorMasterFile))
            {
                _logger.LogInformation("Migrated legacy master session to actor directory");
            }
            else
            {
                _logger.LogWarning("Failed to migrate legacy master session to actor directory");
            }
        }

        foreach (var legacyGoalFile in Directory.GetFiles(_stateDir, "brain-goal-*.json"))
        {
            var fileName = Path.GetFileName(legacyGoalFile);
            var actorGoalFile = Path.Combine(actorStateDir, fileName);
            if (!File.Exists(actorGoalFile))
            {
                if (_fileCopier(legacyGoalFile, actorGoalFile))
                {
                    _logger.LogInformation("Migrated legacy goal session {File} to actor directory", fileName);
                }
                else
                {
                    _logger.LogWarning("Failed to migrate legacy goal session {File} to actor directory", fileName);
                }
            }
        }

        try
        {
            File.WriteAllText(markerPath, "");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create migration marker {Marker}", markerPath);
        }
    }

    /// <summary>
    /// Sends a message to the actor and awaits its reply. Errors are never swallowed:
    /// a missing actor or closed mailbox throws <see cref="InvalidOperationException"/>,
    /// an elapsed <paramref name="timeout"/> throws <see cref="TimeoutException"/>,
    /// caller cancellation surfaces as <see cref="OperationCanceledException"/>, and a faulted
    /// reply propagates its own exception.
    /// </summary>
    private async Task<T> AskActorAsync<T>(IBrainMessage message, TaskCompletionSource<T> reply,
        CancellationToken ct, TimeSpan timeout)
    {
        var actor = Volatile.Read(ref _brainActor)
            ?? throw new InvalidOperationException("BrainActor not available.");

        if (!actor.Tell(message))
            throw new InvalidOperationException("BrainActor mailbox closed.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            return await reply.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"BrainActor did not respond to {message.GetType().Name} within {timeout.TotalSeconds}s.");
        }
    }

    /// <summary>Throws when the brain is currently being reset.</summary>
    private void EnsureNotResetting()
    {
        if (Volatile.Read(ref _resetting))
            throw new InvalidOperationException("Brain is being reset.");
    }

    /// <inheritdoc />
    public async Task UpdateModelAsync(string model, int? maxContextTokens = null, CancellationToken ct = default)
    {
        EnsureConnected();
        EnsureNotResetting();

        // The actor is the source of truth: its update must succeed before any local or registry
        // state is published, otherwise a failed update would leave the two permanently divergent.
        var updateMsg = BrainActorMessages.CreateUpdateModelMessage(model, maxContextTokens);
        await AskActorAsync(updateMsg, updateMsg.Reply, ct, TimeSpan.FromSeconds(3));

        _modelOverride = model;
        if (maxContextTokens.HasValue)
            _maxContextTokens = maxContextTokens.Value;

        var (_, _, reasoning) = ChatClientFactory.ParseProviderModelAndReasoning(model);
        _reasoningEffort = reasoning;

        _sessionRegistry?.RegisterOrUpdate(new LlmSessionInfo
        {
            SessionId = "brain-master",
            SessionType = LlmSessionType.Brain,
            Model = _modelOverride,
            Status = "idle",
            CurrentTokens = 0,
            MaxTokens = _maxContextTokens,
        });

        _logger.LogInformation("Brain model updated to '{Model}' with context window {ContextWindow}",
            model, _maxContextTokens);
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
        var actor = Volatile.Read(ref _brainActor);
        if (actor is not null && !actor.Tell(new RegisterPipelineMessage(pipeline.GoalId, pipeline)))
            _logger.LogWarning("RegisterPipeline: Tell failed for goal {GoalId} (mailbox closed)", pipeline.GoalId);
    }

    /// <summary>Removes a pipeline from the active-pipeline registry once a goal completes or fails.</summary>
    public void DeregisterActivePipeline(string goalId)
    {
        var actor = Volatile.Read(ref _brainActor);
        if (actor is not null && !actor.Tell(new DeregisterPipelineMessage(goalId)))
            _logger.LogWarning("DeregisterPipeline: Tell failed for goal {GoalId} (mailbox closed)", goalId);
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

    /// <summary>Forks the master session for a goal inside the brain actor.</summary>
    /// <inheritdoc />
    public async Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default)
    {
        EnsureConnected();
        if (_disposing)
            throw new InvalidOperationException("Brain is being disposed.");
        EnsureNotResetting();

        var forkMsg = BrainActorMessages.CreateForkSessionMessage(goalId);
        await AskActorAsync(forkMsg, forkMsg.Reply, ct, TimeSpan.FromSeconds(3));
    }

    /// <summary>Registers an already-existing goal session inside the brain actor.</summary>
    /// <inheritdoc />
    public async Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default)
    {
        EnsureConnected();
        if (_disposing)
            throw new InvalidOperationException("Brain is being disposed.");
        EnsureNotResetting();

        var regMsg = BrainActorMessages.CreateRegisterExistingSessionMessage(goalId);
        await AskActorAsync(regMsg, regMsg.Reply, ct, TimeSpan.FromSeconds(3));
    }

    /// <summary>Deletes the goal session and its child actor inside the brain actor.</summary>
    /// <inheritdoc />
    public async Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default)
    {
        EnsureConnected();
        EnsureNotResetting();

        var deleteMsg = BrainActorMessages.CreateDeleteSessionMessage(goalId);
        await AskActorAsync(deleteMsg, deleteMsg.Reply, ct, TimeSpan.FromSeconds(3));
    }

    /// <inheritdoc />
    public bool GoalSessionExists(string goalId)
    {
        if (Volatile.Read(ref _brainActor) is not { } actor)
            return false;

        var msg = BrainActorMessages.CreateGoalSessionExistsMessage(goalId);
        if (!actor.Tell(msg))
            return false;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            return msg.Reply.Task.WaitAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct)
    {
        EnsureConnected();
        EnsureNotResetting();

        pipeline.Conversation.Add(new ConversationEntry("system", note, pipeline.Iteration, "plan-adjustment"));

        _logger.LogInformation("Injected plan adjustment note for goal {GoalId}: {Note}", pipeline.GoalId, note);

        var msg = BrainActorMessages.CreateInjectNoteOnChildMessage(pipeline.GoalId, note);
        await AskActorAsync(msg, msg.Reply, ct, TimeSpan.FromSeconds(3));
    }

    /// <inheritdoc/>
    public async Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default)
    {
        EnsureConnected();
        EnsureNotResetting();

        if (!string.IsNullOrWhiteSpace(instructions))
        {
            _systemPrompt = $"{DefaultSystemPrompt}\n\n{instructions}";
            _logger.LogInformation("Updated Brain system prompt with new orchestrator instructions ({Chars} chars)",
                instructions.Length);
        }

        var injectMsg = BrainActorMessages.CreateInjectOrchestratorInstructionsMessage(_systemPrompt);
        await AskActorAsync(injectMsg, injectMsg.Reply, ct, TimeSpan.FromSeconds(3));
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
                    () => ExecuteBrainAsync(currentPrompt, pipeline.GoalId, ct),
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
        EnsureNotResetting();

        var prompt = BrainPromptBuilder.BuildSummarizePrompt(pipeline);
        var (summaryText, _) = await ExecuteBrainAsync(prompt, pipeline.GoalId, ct);
        var summary = string.IsNullOrWhiteSpace(summaryText)
            ? $"Goal '{pipeline.GoalId}' completed."
            : summaryText.Trim();

        _sessionRegistry?.RegisterOrUpdate(new LlmSessionInfo
        {
            SessionId = "brain-master",
            SessionType = LlmSessionType.Brain,
            Model = _modelOverride,
            Status = "idle",
            CurrentTokens = 0,
            MaxTokens = _maxContextTokens,
        });

        var mergeMsg = BrainActorMessages.CreateMergeSummaryMessage(pipeline.GoalId, summary);
        await AskActorAsync(mergeMsg, mergeMsg.Reply, ct, TimeSpan.FromSeconds(3));

        var deleteMsg = BrainActorMessages.CreateDeleteSessionMessage(pipeline.GoalId);
        await AskActorAsync(deleteMsg, deleteMsg.Reply, ct, TimeSpan.FromSeconds(3));

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
                    var (response, _) = await ExecuteBrainAsync(prompt, pipeline.GoalId, ct);

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

                    var (response, toolCall) = await ExecuteBrainAsync(prompt, pipeline.GoalId, ct);

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
                () => ExecuteBrainAsync(prompt, goalId, ct),
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
        [System.Runtime.CompilerServices.CallerMemberName] string callerName = "")
    {
        EnsureConnected();
        EnsureNotResetting();

        var actor = Volatile.Read(ref _brainActor)
            ?? throw new InvalidOperationException("BrainActor not available.");

        return await ExecuteBrainViaActorAsync(actor, prompt, goalId, ct, callerName);
    }

    /// <summary>Routes the Brain LLM call through the BrainActor's per-goal child actor.</summary>
    private async Task<(string Text, BrainToolCallResult? ToolCall)> ExecuteBrainViaActorAsync(
        BrainActor actor, string prompt, string goalId, CancellationToken ct,
        string callerName)
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

    /// <inheritdoc />
    public BrainStats? GetStats()
    {
        if (!_connected) return null;

        // No _resetting check: during a reset the actor is detached, so the null-actor guard
        // below is what makes GetStats report "no stats".
        if (Volatile.Read(ref _brainActor) is not { } actor)
            return null;

        var statsMsg = BrainActorMessages.CreateGetStatsMessage();
        if (!actor.Tell(statsMsg))
            return null;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var stats = statsMsg.Reply.Task.WaitAsync(cts.Token).GetAwaiter().GetResult();
            if (stats is null) return null;

            var usagePct = stats.MaxContextTokens > 0
                ? (int)(stats.ContextTokens * 100.0 / stats.MaxContextTokens)
                : 0;

            return new BrainStats
            {
                Model = stats.Model,
                MessageCount = stats.MessageCount,
                ContextTokens = stats.ContextTokens,
                MaxContextTokens = stats.MaxContextTokens,
                ContextUsagePercent = usagePct,
                CumulativeInputTokens = stats.CumulativeInputTokens,
                CumulativeOutputTokens = stats.CumulativeOutputTokens,
                MaxSteps = stats.MaxSteps,
                IsConnected = stats.IsConnected,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task ResetSessionAsync(CancellationToken ct = default)
    {
        // NOTE: ResetSessionAsync intentionally does NOT call EnsureConnected(). All session state
        // lives in the BrainActor, so a reset detaches and disposes it, clears the persisted state
        // files, reloads orchestrator instructions from disk, and starts a fresh actor.
        //
        // _sessionLock is held for the ENTIRE reset — including ResetBrainActorAsync — so that
        // ConnectAsync and DisposeAsyncCore (which both take the same lock) can never observe a
        // half-reset brain, e.g. a detached-but-not-yet-replaced actor. The wait uses
        // CancellationToken.None because an interrupted reset would leave exactly that state.
        await _sessionLock.WaitAsync(CancellationToken.None);
        try
        {
            Volatile.Write(ref _resetting, true);

            var freshInstructions = _agentsManager?.GetAgentsMd(WorkerRole.Orchestrator) ?? "";
            _systemPrompt = string.IsNullOrWhiteSpace(freshInstructions)
                ? DefaultSystemPrompt
                : $"{DefaultSystemPrompt}\n\n{freshInstructions}";

            await ResetBrainActorAsync();
            _logger.LogInformation("Brain session reset — actor state cleared, orchestrator instructions reloaded from disk, and session files deleted.");
        }
        catch
        {
            _connected = false;
            _sessionRegistry?.Unregister("brain-master");
            throw;
        }
        finally
        {
            Volatile.Write(ref _resetting, false);
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Atomically detaches and disposes the brain actor, clears all session state from both the
    /// actor and legacy state directories, then starts a fresh actor with strict startup.
    /// Throws when any session file survives deletion or when the replacement actor fails to start.
    /// </summary>
    private async Task ResetBrainActorAsync()
    {
        var oldActor = Interlocked.Exchange(ref _brainActor, null);

        _logger.LogWarning("Brain actor detached during reset — concurrent operations will fail until the replacement actor starts");

        if (oldActor is not null)
        {
            try { await oldActor.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose brain actor during reset"); }
        }

        if (!_connected)
            return;

        var actorsDir = Path.Combine(_stateDir, "actors");

        if (Directory.Exists(actorsDir))
        {
            foreach (var file in Directory.EnumerateFiles(actorsDir, "brain-*.json"))
            {
                try { _fileDeleter(file); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete actor state file {File}", file); }
            }
        }

        var migratedMarker = Path.Combine(actorsDir, ".migrated");
        if (File.Exists(migratedMarker))
        {
            try { _fileDeleter(migratedMarker); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete migration marker {File}", migratedMarker); }
        }

        foreach (var file in Directory.EnumerateFiles(_stateDir, "brain-*.json"))
        {
            try { _fileDeleter(file); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete legacy state file {File}", file); }
        }

        var actorSurvivors = Directory.Exists(actorsDir) && Directory.EnumerateFiles(actorsDir, "brain-*.json").Any();
        var stateSurvivors = Directory.EnumerateFiles(_stateDir, "brain-*.json").Any();
        if (actorSurvivors || stateSurvivors)
            throw new InvalidOperationException("Failed to clear session state during reset");

        await StartBrainActorAsync(CancellationToken.None, throwOnFailure: true);
    }

    private void EnsureConnected()
    {
        if (!_connected)
            throw new InvalidOperationException("Brain not connected. Call ConnectAsync first.");
    }
    /// <summary>Disposes the brain actor and unregisters the master session entry.
    /// Idempotent — concurrent callers await the same disposal task.</summary>
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
        // Serialized against ConnectAsync and ResetSessionAsync so disposal can never interleave
        // with a mid-flight actor transition.
        await _sessionLock.WaitAsync();
        try
        {
            var actor = Interlocked.Exchange(ref _brainActor, null);
            await DisposeActorSafelyAsync(actor);
            _sessionRegistry?.Unregister("brain-master");

            // Disposed AFTER the actor: child actors borrow this client with ownsClient=false and
            // never dispose it, so the brain that injected it must — and only once the actor (and
            // therefore every child that could still be using it) is gone.
            if (_injectedChatClient is not null)
            {
                try { _injectedChatClient.Dispose(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose injected chat client"); }
            }
        }
        finally
        {
            _sessionLock.Release();
        }

        // _sessionLock is intentionally NOT disposed: it must live for the object's lifetime
        // because ConnectAsync reads _disposing only after acquiring it.
    }
}

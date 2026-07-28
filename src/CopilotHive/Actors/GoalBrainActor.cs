using System.ComponentModel;

using CopilotHive.Dashboard;
using CopilotHive.Knowledge;
using CopilotHive.Orchestration;
using CopilotHive.Goals;
using CopilotHive.Services;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using SharpCoder;

namespace CopilotHive.Actors;

/// <summary>
/// Channel-based actor prototype owning the per-goal Brain resources: the coding agent,
/// the goal session and the last tool call result. All state changes are serialized
/// through a single-reader mailbox, so no locking is required by callers.
/// </summary>
internal sealed class GoalBrainActor : Actor<IGoalBrainMessage>
{
    private readonly IChatClient _chatClient;
    private readonly bool _ownsChatClient;
    private readonly IChatClient? _compactionClient;
    private readonly bool _compactionIsChatClient;
    private readonly int _maxContextTokens;
    private readonly string _sessionFilePath;
    private readonly LlmSessionRegistry? _sessionRegistry;
    private readonly ILogger _logger;
    private readonly List<AITool> _brainTools;
    private readonly IGoalStore? _goalStore;
    private readonly KnowledgeGraph? _knowledgeGraph;
    private readonly Func<IBrainMessage, bool>? _parentTell;

    private int _resourcesDisposed;

    /// <summary>Creates a goal-brain actor for a single goal.</summary>
    internal GoalBrainActor(
        string goalId,
        AgentSession session,
        IChatClient chatClient,
        bool ownsChatClient,
        IChatClient? compactionClient,
        AgentOptions baseOptions,
        string model,
        int maxContextTokens,
        string stateDir,
        LlmSessionRegistry? sessionRegistry,
        ILogger logger,
        IGoalStore? goalStore = null,
        KnowledgeGraph? knowledgeGraph = null,
        Func<IBrainMessage, bool>? parentTell = null)
    {
        ValidateGoalId(goalId);

        GoalId = goalId;
        Session = session;
        Model = model;
        _chatClient = chatClient;
        _ownsChatClient = ownsChatClient;
        _compactionClient = compactionClient;
        _compactionIsChatClient = compactionClient is not null && ReferenceEquals(compactionClient, chatClient);
        _maxContextTokens = maxContextTokens;
        _sessionRegistry = sessionRegistry;
        _logger = logger;
        _goalStore = goalStore;
        _knowledgeGraph = knowledgeGraph;
        _parentTell = parentTell;

        try
        {
            _sessionFilePath = Path.Combine(stateDir, $"brain-goal-{goalId}.json");
            _brainTools = BuildTools();

            var configured = new AgentOptions
            {
                WorkDirectory = baseOptions.WorkDirectory,
                MaxSteps = baseOptions.MaxSteps,
                EnableBash = baseOptions.EnableBash,
                BashShellPath = baseOptions.BashShellPath,
                BashShellArgsFormat = baseOptions.BashShellArgsFormat,
                EnableFileOps = baseOptions.EnableFileOps,
                EnableFileWrites = baseOptions.EnableFileWrites,
                EnableSkills = baseOptions.EnableSkills,
                SystemPrompt = baseOptions.SystemPrompt,
                CustomInstructions = baseOptions.CustomInstructions,
                AutoLoadWorkspaceInstructions = baseOptions.AutoLoadWorkspaceInstructions,
                CompactionThreshold = baseOptions.CompactionThreshold,
                CompactionRetainRecent = baseOptions.CompactionRetainRecent,
                EnableAutoCompaction = baseOptions.EnableAutoCompaction,
                OnCompacting = baseOptions.OnCompacting,
                OnCompacted = baseOptions.OnCompacted,
                Logger = baseOptions.Logger,
                ReasoningEffort = baseOptions.ReasoningEffort,
                ShowToolCallsInStream = baseOptions.ShowToolCallsInStream,
                CompactionMaxTokens = baseOptions.CompactionMaxTokens,
                CustomTools = _brainTools,
                CompactionClient = compactionClient,
                MaxContextTokens = maxContextTokens,
            };

            CodingAgent = new CodingAgent(chatClient, configured);
        }
        catch
        {
            DisposeOwnedResources();
            throw;
        }
    }

    /// <summary>Identifier of the goal this actor serves.</summary>
    internal string GoalId { get; }

    /// <summary>Model identifier used by the agent.</summary>
    internal string Model { get; }

    /// <summary>The goal session. Mutable so it can be replaced on overflow recovery.</summary>
    internal AgentSession Session { get; set; }

    /// <summary>The coding agent driving the goal's Brain calls.</summary>
    internal CodingAgent CodingAgent { get; } = null!;

    /// <summary>Result of the most recent tool call, if any.</summary>
    internal GoalBrainToolCallResult? LastToolCallResult { get; set; }

    private static void ValidateGoalId(string goalId)
    {
        if (string.IsNullOrWhiteSpace(goalId))
        {
            throw new ArgumentException("Goal id must not be empty.", nameof(goalId));
        }

        if (goalId.Contains('/') || goalId.Contains('\\') || goalId.Contains(".."))
        {
            throw new ArgumentException($"Goal id '{goalId}' contains invalid path characters.", nameof(goalId));
        }
    }

    private List<AITool> BuildTools()
    {
        Func<string, Task<GoalPipeline?>> pipelineResolver = _parentTell is null
            ? _ => Task.FromResult<GoalPipeline?>(null)
            : async goalId =>
            {
                var msg = BrainActorMessages.CreateGetPipelineMessage(goalId);
                if (!_parentTell(msg)) return null;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { return await msg.Reply.Task.WaitAsync(cts.Token); }
                catch { return null; }
            };

        return
        [
            AIFunctionFactory.Create(
                ([Description("The question to forward to the Composer for resolution.")] string question,
                 [Description("The reason why the Brain cannot answer this question from the codebase.")] string reason) =>
                {
                    LastToolCallResult = new EscalateToolResult(question, reason);
                    return "Escalation recorded.";
                },
                "escalate_to_composer",
                "Escalate a question to the Composer when the Brain cannot answer from the codebase alone."),
            AIFunctionFactory.Create(
                ([Description("The phases to run in order.")] string[] phases,
                 [Description("Instructions for each phase.")] string phase_instructions,
                 [Description("Why this plan was chosen.")] string reason,
                 [Description("Optional JSON mapping phase names to model tiers.")] string? model_tiers) =>
                {
                    LastToolCallResult = new PlanToolResult(phases ?? [], phase_instructions, reason, model_tiers);
                    return "Iteration plan recorded.";
                },
                "report_iteration_plan",
                "Report your iteration plan — which phases to run and in what order."),
            .. BrainTools.BuildDependencyTools(_goalStore, pipelineResolver, _knowledgeGraph, _logger),
        ];
    }

    /// <inheritdoc />
    protected override async Task HandleAsync(IGoalBrainMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case ExecutePromptMessage m:
                await ExecutePromptAsync(m);
                break;

            case InjectNoteMessage m:
                InjectNote(m);
                break;

            case GetGoalStateMessage m:
                m.Reply.TrySetResult(new GoalBrainActorState(
                    GoalId, Session.MessageHistory.Count, Session.EstimatedContextTokens, Model));
                break;

            default:
                throw new InvalidOperationException($"Unhandled goal brain message type '{message.GetType().Name}'.");
        }
    }

    private async Task ExecutePromptAsync(ExecutePromptMessage message)
    {
        LastToolCallResult = null;
        var sessionRef = Session;

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(message.Ct, LoopToken);
        linkedCts.CancelAfter(TimeSpan.FromMinutes(Constants.TaskTimeoutMinutes));

        RegisterSessionStatus("active", sessionRef);

        try
        {
            var result = await CodingAgent.ExecuteAsync(sessionRef, message.Prompt, linkedCts.Token);

            if (result.Status != "Error")
            {
                try
                {
                    await sessionRef.SaveAsync(_sessionFilePath, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to save goal Brain session for {GoalId}", GoalId);
                }

                if (result.Usage is not null)
                {
                    _logger.LogDebug(
                        "Goal Brain usage: model={Model} in={InputTokens} out={OutputTokens} tools={ToolCalls}",
                        result.ModelId, result.Usage.InputTokenCount, result.Usage.OutputTokenCount, result.ToolCallCount);
                }
            }

            RegisterSessionStatus("idle", sessionRef);
            message.Reply.TrySetResult(new GoalBrainExecutionResult(result.Message ?? string.Empty, LastToolCallResult));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Goal Brain execution failed for {GoalId}", GoalId);
            RegisterSessionStatus("idle", sessionRef);
            message.Reply.TrySetException(ex);
        }
        finally
        {
            linkedCts.Dispose();
        }
    }

    private void RegisterSessionStatus(string status, AgentSession sessionRef) =>
        _sessionRegistry?.RegisterOrUpdate(new LlmSessionInfo
        {
            SessionId = $"brain-goal-{GoalId}",
            SessionType = LlmSessionType.BrainGoal,
            GoalId = GoalId,
            Model = Model,
            Status = status,
            CurrentTokens = sessionRef.EstimatedContextTokens,
            MaxTokens = _maxContextTokens,
        });

    private void InjectNote(InjectNoteMessage message)
    {
        Session.MessageHistory.Add(new ChatMessage(ChatRole.User, message.Note));
        Session.LastKnownContextTokens = 0;
        message.Reply.TrySetResult(true);
    }

    /// <inheritdoc />
    protected override void CancelReply(IGoalBrainMessage message)
    {
        switch (message)
        {
            case ExecutePromptMessage m: m.Reply.TrySetCanceled(); break;
            case InjectNoteMessage m: m.Reply.TrySetCanceled(); break;
            case GetGoalStateMessage m: m.Reply.TrySetCanceled(); break;
        }
    }

    /// <inheritdoc />
    protected override void OnUnhandledException(IGoalBrainMessage message, Exception ex)
    {
        switch (message)
        {
            case ExecutePromptMessage m: m.Reply.TrySetException(ex); break;
            case InjectNoteMessage m: m.Reply.TrySetException(ex); break;
            case GetGoalStateMessage m: m.Reply.TrySetException(ex); break;
            default:
                _logger.LogError(ex, "Goal Brain actor failed to handle {MessageType}", message.GetType().Name);
                break;
        }
    }

    /// <summary>Disposes owned chat clients exactly once, per the ownership rules.</summary>
    private void DisposeOwnedResources()
    {
        if (Interlocked.CompareExchange(ref _resourcesDisposed, 1, 0) != 0)
        {
            return;
        }

        if (!_compactionIsChatClient && _compactionClient is not null)
        {
            try { _compactionClient.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose compaction client for {GoalId}", GoalId); }
        }

        if (_ownsChatClient)
        {
            try { _chatClient.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose chat client for {GoalId}", GoalId); }
        }
    }

    /// <inheritdoc />
    protected override Task OnShutdownAsync()
    {
        DisposeOwnedResources();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override void OnUnstartedDispose() => DisposeOwnedResources();

    /// <inheritdoc />
    protected override void OnDisposeTimeout() =>
        _logger.LogWarning("Goal Brain actor for {GoalId} did not stop in time — deferring client disposal", GoalId);
}

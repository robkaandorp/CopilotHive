using System.ComponentModel;
using System.Threading.Channels;

using CopilotHive.Dashboard;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using SharpCoder;

namespace CopilotHive.Actors;

/// <summary>
/// Channel-based actor prototype owning the per-goal Brain resources: the coding agent,
/// the goal session and the last tool call result. All state changes are serialized
/// through a single-reader mailbox, so no locking is required by callers.
/// </summary>
internal sealed class GoalBrainActor : IAsyncDisposable
{
    private readonly Channel<IGoalBrainMessage> _mailbox = Channel.CreateUnbounded<IGoalBrainMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _loopCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationToken _loopToken;
    private readonly object _lifecycleLock = new();

    private readonly IChatClient _chatClient;
    private readonly bool _ownsChatClient;
    private readonly IChatClient? _compactionClient;
    private readonly bool _compactionIsChatClient;
    private readonly int _maxContextTokens;
    private readonly string _sessionFilePath;
    private readonly LlmSessionRegistry? _sessionRegistry;
    private readonly ILogger _logger;
    private readonly List<AITool> _brainTools;

    private int _resourcesDisposed;
    private Task? _loopTask;
    private bool _disposed;

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
        ILogger logger)
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
            _loopToken = _cts.Token;
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

    /// <summary>Completes when the message loop has exited.</summary>
    internal Task Completion => _loopCompletion.Task;

    /// <summary>True once the message loop has exited.</summary>
    internal bool IsCompleted => Completion.IsCompleted;

    /// <summary>True once the message loop has been launched.</summary>
    internal bool IsStarted
    {
        get { lock (_lifecycleLock) { return _loopTask is not null; } }
    }

    /// <summary>Enqueues a message. Returns false once the mailbox is closed.</summary>
    internal bool Tell(IGoalBrainMessage message) => _mailbox.Writer.TryWrite(message);

    /// <summary>Starts the message loop. Safe to call concurrently; only one loop runs.</summary>
    internal void Start()
    {
        lock (_lifecycleLock)
        {
            if (_loopTask is not null || _disposed)
            {
                return;
            }

            _loopTask = Task.Run(() => MessageLoopAsync(_loopToken));
        }
    }

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

    private List<AITool> BuildTools() =>
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
    ];

    private async Task MessageLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _mailbox.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await HandleAsync(message);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    CancelReply(message);
                    break;
                }
                catch (Exception ex)
                {
                    FaultReplyOrLog(message, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled — remaining items are drained below.
        }
        catch (ChannelClosedException)
        {
            // Normal exit — the mailbox was completed.
        }
        finally
        {
            while (_mailbox.Reader.TryRead(out var message))
            {
                CancelReply(message);
            }

            _loopCompletion.TrySetResult();
        }
    }

    private async Task HandleAsync(IGoalBrainMessage message)
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

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(message.Ct, _loopToken);
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

            message.Reply.TrySetResult(new GoalBrainExecutionResult(result.Message ?? string.Empty, LastToolCallResult));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Goal Brain execution failed for {GoalId}", GoalId);
            message.Reply.TrySetException(ex);
        }
        finally
        {
            RegisterSessionStatus("idle", sessionRef);
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

    private static void CancelReply(IGoalBrainMessage message)
    {
        switch (message)
        {
            case ExecutePromptMessage m: m.Reply.TrySetCanceled(); break;
            case InjectNoteMessage m: m.Reply.TrySetCanceled(); break;
            case GetGoalStateMessage m: m.Reply.TrySetCanceled(); break;
        }
    }

    private void FaultReplyOrLog(IGoalBrainMessage message, Exception exception)
    {
        switch (message)
        {
            case ExecutePromptMessage m: m.Reply.TrySetException(exception); break;
            case InjectNoteMessage m: m.Reply.TrySetException(exception); break;
            case GetGoalStateMessage m: m.Reply.TrySetException(exception); break;
            default:
                _logger.LogError(exception, "Goal Brain actor failed to handle {MessageType}", message.GetType().Name);
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

    /// <summary>Stops the actor, cancelling any pending replies and disposing owned clients.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _cts.Cancel();
                _mailbox.Writer.TryComplete();

                if (_loopTask is null)
                {
                    // Unstarted — no loop is reading the mailbox, so draining here is single-reader safe.
                    while (_mailbox.Reader.TryRead(out var message))
                    {
                        CancelReply(message);
                    }

                    _loopCompletion.TrySetResult();
                    DisposeOwnedResources();
                    _cts.Dispose();
                    return;
                }
            }

            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _loopCompletion.Task.WaitAsync(timeoutCts.Token);
                DisposeOwnedResources();
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "Goal Brain actor for {GoalId} did not stop in time — deferring client disposal", GoalId);
                _ = _loopCompletion.Task.ContinueWith(_ => DisposeOwnedResources(), TaskScheduler.Default);
            }
        }
        finally
        {
            if (_loopTask is not null)
            {
                _cts.Dispose();
            }
        }
    }
}

using System.Threading.Channels;

using CopilotHive.Services;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using SharpCoder;

namespace CopilotHive.Actors;

/// <summary>
/// Channel-based actor prototype owning the mutable state of the Brain (master session,
/// per-goal sessions and active pipelines). All state changes are serialized through a
/// single-reader mailbox, so no locking is required by callers.
/// </summary>
internal sealed class BrainActor : IAsyncDisposable
{
    private readonly Channel<IBrainMessage> _mailbox = Channel.CreateUnbounded<IBrainMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _loopCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationToken _loopToken;
    private readonly object _lifecycleLock = new();

    private readonly string _stateDir;
    private readonly ILogger _logger;

    private readonly Dictionary<string, string> _activeGoalSessions = [];
    private readonly Dictionary<string, GoalPipeline> _activePipelines = [];

    private AgentSession? _masterSession;
    private string _modelOverride;
    private int _maxContextTokens;
    private bool _connected;

    private Task? _loopTask;
    private bool _disposed;

    /// <summary>Creates a brain actor bound to the given state directory.</summary>
    internal BrainActor(string modelOverride, int maxContextTokens, string stateDir, ILogger logger)
    {
        _modelOverride = modelOverride;
        _maxContextTokens = maxContextTokens;
        _stateDir = stateDir;
        _logger = logger;
        _loopToken = _cts.Token;
    }

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
    internal bool Tell(IBrainMessage message) => _mailbox.Writer.TryWrite(message);

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

    private async Task MessageLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _mailbox.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await HandleAsync(message, ct);
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

    private async Task HandleAsync(IBrainMessage message, CancellationToken ct)
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
                DeleteSession(m.GoalId);
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

            default:
                throw new InvalidOperationException($"Unhandled brain message type '{message.GetType().Name}'.");
        }
    }

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
        await SaveSessionAsync(goalSession, filePath, ct);
        _activeGoalSessions[goalId] = filePath;
    }

    private void DeleteSession(string goalId)
    {
        var filePath = ValidateGoalPath(goalId);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        _activeGoalSessions.Remove(goalId);
    }

    private async Task MergeSummaryAsync(string goalId, string summary, CancellationToken ct)
    {
        var master = EnsureConnected();
        master.MessageHistory.Add(new ChatMessage(ChatRole.User, $"[Goal completed: {goalId}] Summarize what was done."));
        master.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, summary));
        master.LastKnownContextTokens = 0;
        await SaveSessionAsync(master, GetMasterSessionFilePath(), ct);
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
            _connected);
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

    private static void CancelReply(IBrainMessage message)
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
        }
    }

    private void FaultReplyOrLog(IBrainMessage message, Exception exception)
    {
        switch (message)
        {
            case ConnectMessage m: m.Reply.TrySetException(exception); break;
            case ForkSessionMessage m: m.Reply.TrySetException(exception); break;
            case DeleteSessionMessage m: m.Reply.TrySetException(exception); break;
            case MergeSummaryMessage m: m.Reply.TrySetException(exception); break;
            case UpdateModelMessage m: m.Reply.TrySetException(exception); break;
            case GetPipelineMessage m: m.Reply.TrySetException(exception); break;
            case GetStatsMessage m: m.Reply.TrySetException(exception); break;
            case GoalSessionExistsMessage m: m.Reply.TrySetException(exception); break;
            default:
                _logger.LogError(exception, "Brain actor failed to handle {MessageType}", message.GetType().Name);
                break;
        }
    }

    /// <summary>Stops the actor, cancelling any pending replies.</summary>
    public async ValueTask DisposeAsync()
    {
        Task taskToAwait;
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
                _cts.Dispose();
                return;
            }

            taskToAwait = _loopTask;
        }

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _loopCompletion.Task.WaitAsync(timeoutCts.Token);
            await taskToAwait.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timed out — the loop is stuck; nothing more we can do safely.
        }
        finally
        {
            _cts.Dispose();
        }
    }
}

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
internal sealed class BrainActor : Actor<IBrainMessage>
{
    private readonly string _stateDir;
    private readonly ILogger _logger;

    private readonly Dictionary<string, string> _activeGoalSessions = [];
    private readonly Dictionary<string, GoalPipeline> _activePipelines = [];

    private AgentSession? _masterSession;
    private string _modelOverride;
    private int _maxContextTokens;
    private bool _connected;
    private string _orchestratorInstructions = string.Empty;

    /// <summary>Creates a brain actor bound to the given state directory.</summary>
    internal BrainActor(string modelOverride, int maxContextTokens, string stateDir, ILogger logger)
    {
        _modelOverride = modelOverride;
        _maxContextTokens = maxContextTokens;
        _stateDir = stateDir;
        _logger = logger;
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

            case RegisterExistingSessionMessage m:
                await RegisterExistingSessionAsync(m.GoalId, ct);
                m.Reply.TrySetResult(true);
                break;

            case InjectOrchestratorInstructionsMessage m:
                _orchestratorInstructions = m.Instructions;
                m.Reply.TrySetResult(true);
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
        if (!File.Exists(filePath))
        {
            var goalSession = EnsureConnected().Fork($"brain-goal-{goalId}");
            await SaveSessionAsync(goalSession, filePath, ct);
        }

        _activeGoalSessions[goalId] = filePath;
    }

    private void DeleteSession(string goalId)    {
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
            default:
                _logger.LogError(ex, "Brain actor failed to handle {MessageType}", message.GetType().Name);
                break;
        }
    }

    /// <inheritdoc />
    protected override void OnDisposeTimeout() =>
        _logger.LogWarning("BrainActor did not complete within 5 seconds.");
}

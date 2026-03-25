using System.ComponentModel;
using System.Text.Json;
using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using Microsoft.Extensions.AI;
using SharpCoder;

namespace CopilotHive.Orchestration;

/// <summary>
/// Conversational agent for goal decomposition and management.
/// The Composer helps users break down high-level intent into well-scoped goals
/// and manages the goal lifecycle (create → approve → dispatch).
/// Uses a persistent SharpCoder session with streaming for real-time interaction.
/// </summary>
public sealed class Composer : IAsyncDisposable
{
    private readonly string _model;
    private readonly int _maxContextTokens;
    private readonly int _maxSteps;
    private readonly ReasoningEffort? _reasoningEffort;
    private readonly ILogger<Composer> _logger;
    private readonly IGoalStore _goalStore;
    private readonly BrainRepoManager? _repoManager;
    private readonly string _stateDir;
    private IChatClient? _chatClient;
    private CodingAgent? _agent;
    private AgentSession _session;

    private readonly string _systemPrompt = DefaultSystemPrompt;
    private readonly List<AITool> _composerTools;

    private const string DefaultSystemPrompt = """
        You are the Composer — a strategic advisor for the CopilotHive multi-agent system.
        You help the user decompose high-level intent into well-scoped, actionable goals.

        Your capabilities:
        - Read the codebase to understand current state (read_file, glob, grep)
        - Search existing goals to avoid duplication (search_goals)
        - Browse goal history and status (list_goals, get_goal)
        - Create goals as drafts for user review (create_goal)
        - Approve drafts to queue them for execution (approve_goal)
        - Update existing goals (update_goal)
        - Delete draft or failed goals (delete_goal)

        Guidelines for goal creation:
        - Each goal should be completable in 1-3 iterations (small, focused)
        - Include clear acceptance criteria in the description
        - Reference specific files/classes when possible
        - Set dependencies when goals must be ordered
        - Always include "All existing tests must continue to pass"
        - Check existing goals first to avoid duplication
        - New goals are created as Draft — user must approve before dispatch
        - Use lowercase-kebab-case for goal IDs (e.g. "add-user-auth", "fix-parser-bug")
        """;

    /// <summary>
    /// Initialises a new <see cref="Composer"/> that connects to an LLM provider
    /// and uses the given goal store for CRUD operations.
    /// </summary>
    public Composer(
        string model,
        ILogger<Composer> logger,
        IGoalStore goalStore,
        int maxContextTokens = Constants.DefaultBrainContextWindow,
        int maxSteps = Constants.DefaultBrainMaxSteps,
        BrainRepoManager? repoManager = null,
        string? stateDir = null)
    {
        _model = model;
        _maxContextTokens = maxContextTokens;
        _maxSteps = maxSteps;
        _logger = logger;
        _goalStore = goalStore;
        _repoManager = repoManager;
        _stateDir = stateDir ?? "/app/state";
        _session = AgentSession.Create("composer");

        var (_, _, reasoning) = SDK.ChatClientFactory.ParseProviderModelAndReasoning(model);
        _reasoningEffort = reasoning;

        _composerTools = BuildComposerTools();
    }

    /// <summary>Whether the Composer has connected and is ready for streaming.</summary>
    public bool IsConnected => _agent is not null;

    /// <summary>Returns current Composer session statistics.</summary>
    public BrainStats? GetStats()
    {
        if (_agent is null) return null;

        var estimatedTokens = _session.EstimatedContextTokens;
        var usagePct = _maxContextTokens > 0 ? (int)(estimatedTokens * 100.0 / _maxContextTokens) : 0;

        return new BrainStats
        {
            Model = _model,
            MessageCount = _session.MessageHistory.Count,
            EstimatedContextTokens = estimatedTokens,
            MaxContextTokens = _maxContextTokens,
            ContextUsagePercent = usagePct,
            CumulativeInputTokens = _session.InputTokensUsed,
            CumulativeOutputTokens = _session.OutputTokensUsed,
            MaxSteps = _maxSteps,
            IsConnected = true,
        };
    }

    /// <summary>
    /// Creates the IChatClient and CodingAgent, and loads any persisted session.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Composer connecting with model '{Model}'…", _model);

        _chatClient = SDK.ChatClientFactory.Create(_model);

        var sessionFile = GetSessionFilePath();
        if (File.Exists(sessionFile))
        {
            try
            {
                _session = await AgentSession.LoadAsync(sessionFile, ct);
                _logger.LogInformation("Loaded Composer session with {Count} messages from {File}",
                    _session.MessageHistory.Count, sessionFile);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load Composer session from {File} — starting fresh", sessionFile);
                _session = AgentSession.Create("composer");
            }
        }

        RecreateAgent();

        _logger.LogInformation("Composer connected (model={Model}, contextWindow={ContextWindow})",
            _model, _maxContextTokens);
    }

    /// <summary>
    /// Streams a response to the user's message, yielding text deltas in real-time.
    /// The session is persisted after the stream completes.
    /// </summary>
    public async IAsyncEnumerable<StreamingUpdate> StreamAsync(
        string userMessage,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_agent is null)
            throw new InvalidOperationException("Composer not connected. Call ConnectAsync first.");

        _logger.LogInformation("Composer streaming response for: {Message}",
            userMessage.Length > 100 ? userMessage[..100] + "…" : userMessage);

        await foreach (var update in _agent.ExecuteStreamingAsync(_session, userMessage, ct))
        {
            yield return update;
        }

        await SaveSessionAsync(ct);
    }

    /// <summary>
    /// Resets the Composer session, clearing all conversation history.
    /// </summary>
    public async Task ResetSessionAsync(CancellationToken ct = default)
    {
        _session = AgentSession.Create("composer");
        RecreateAgent();

        var sessionFile = GetSessionFilePath();
        if (File.Exists(sessionFile))
        {
            File.Delete(sessionFile);
            _logger.LogInformation("Deleted previous Composer session file");
        }

        _logger.LogInformation("Composer session reset");
        await Task.CompletedTask; // keep async signature for future use
    }

    /// <summary>
    /// Returns the recent user/assistant message pairs from the persistent session.
    /// Tool-call and tool-result messages are excluded.
    /// </summary>
    /// <param name="maxMessages">Maximum number of messages to return.</param>
    public IReadOnlyList<(string Role, string Content)> GetChatHistory(int maxMessages = 50)
    {
        var result = new List<(string Role, string Content)>();
        foreach (var msg in _session.MessageHistory)
        {
            if (msg.Role == Microsoft.Extensions.AI.ChatRole.User ||
                msg.Role == Microsoft.Extensions.AI.ChatRole.Assistant)
            {
                var text = msg.Text;
                if (!string.IsNullOrWhiteSpace(text))
                    result.Add((msg.Role == Microsoft.Extensions.AI.ChatRole.User ? "user" : "assistant", text));
            }
        }

        if (result.Count > maxMessages)
            return result.GetRange(result.Count - maxMessages, maxMessages);

        return result;
    }

    /// <summary>Returns the file path for persisting the Composer session.</summary>
    private string GetSessionFilePath() => Path.Combine(_stateDir, "composer-session.json");

    /// <summary>Persists the current Composer session to disk.</summary>
    internal async Task SaveSessionAsync(CancellationToken ct = default)
    {
        var path = GetSessionFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await _session.SaveAsync(path, ct);
        _logger.LogDebug("Composer session saved ({Count} messages)", _session.MessageHistory.Count);
    }

    private void RecreateAgent()
    {
        if (_chatClient is null)
            throw new InvalidOperationException("Composer not connected. Call ConnectAsync first.");

        var workDir = _repoManager?.WorkDirectory ?? _stateDir;

        _agent = new CodingAgent(_chatClient, new AgentOptions
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
            Logger = _logger,
            OnCompacted = r => _logger.LogInformation(
                "Composer context compaction: {TokensBefore} → {TokensAfter} tokens ({ReductionPercent}% reduction)",
                r.TokensBefore, r.TokensAfter, r.ReductionPercent),
        });

        _logger.LogDebug("Composer CodingAgent created with WorkDirectory={WorkDir}, FileOps={FileOps}",
            workDir, _repoManager is not null);
    }

    // ── Tool implementations ──

    private List<AITool> BuildComposerTools()
    {
        return
        [
            AIFunctionFactory.Create(CreateGoalAsync, "create_goal",
                "Create a new goal as Draft. It will not be dispatched until approved."),
            AIFunctionFactory.Create(ApproveGoalAsync, "approve_goal",
                "Approve a Draft goal, changing its status to Pending so it will be dispatched."),
            AIFunctionFactory.Create(UpdateGoalAsync, "update_goal",
                "Update a field on an existing goal."),
            AIFunctionFactory.Create(GetGoalAsync, "get_goal",
                "Get full details for a goal including iteration history."),
            AIFunctionFactory.Create(ListGoalsAsync, "list_goals",
                "List goals, optionally filtered by status."),
            AIFunctionFactory.Create(SearchGoalsAsync, "search_goals",
                "Search goals by text query across ID, description, and failure reason."),
            AIFunctionFactory.Create(DeleteGoalAsync, "delete_goal",
                "Permanently delete a goal. Only Draft or Failed goals can be deleted."),
        ];
    }

    [Description("Create a new goal as Draft status. Returns the created goal summary.")]
    internal async Task<string> CreateGoalAsync(
        [Description("Unique goal ID in lowercase-kebab-case (e.g. 'add-user-auth')")] string id,
        [Description("Clear description including acceptance criteria")] string description,
        [Description("Comma-separated repository names this goal applies to")] string? repositories = null,
        [Description("Priority: Low, Normal, High, or Critical. Default: Normal")] string? priority = null)
    {
        var isValidId = IsValidGoalId(id);
        var error = Shared.ToolValidation.Check(
            (!string.IsNullOrWhiteSpace(id), "id is required"),
            (!string.IsNullOrWhiteSpace(description), "description is required"),
            (isValidId, $"invalid goal ID '{id}': must be lowercase-kebab-case (a-z, 0-9, hyphens)"));
        if (error is not null) return error;

        var existing = await _goalStore.GetGoalAsync(id);
        if (existing is not null)
            return $"❌ Goal '{id}' already exists (status: {existing.Status.ToDisplayName()}).";

        var goalPriority = GoalPriority.Normal;
        if (!string.IsNullOrEmpty(priority) && Enum.TryParse<GoalPriority>(priority, ignoreCase: true, out var p))
            goalPriority = p;

        var repos = string.IsNullOrWhiteSpace(repositories)
            ? new List<string>()
            : repositories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var goal = new Goal
        {
            Id = id,
            Description = description,
            Priority = goalPriority,
            Status = GoalStatus.Draft,
            RepositoryNames = repos,
        };

        await _goalStore.CreateGoalAsync(goal);
        _logger.LogInformation("Composer created draft goal '{GoalId}'", id);

        return $"""
            ✅ Goal created as Draft:
            - ID: {id}
            - Priority: {goalPriority}
            - Repositories: {(repos.Count > 0 ? string.Join(", ", repos) : "(none)")}
            - Status: Draft (not yet dispatched — use approve_goal to queue it)
            """;
    }

    [Description("Approve a Draft goal, changing its status to Pending for dispatch.")]
    internal async Task<string> ApproveGoalAsync(
        [Description("Goal ID to approve")] string id)
    {
        var error = Shared.ToolValidation.Check(
            (!string.IsNullOrWhiteSpace(id), "id is required"));
        if (error is not null) return error;

        var goal = await _goalStore.GetGoalAsync(id);
        if (goal is null)
            return $"❌ Goal '{id}' not found.";

        if (goal.Status != GoalStatus.Draft)
            return $"❌ Goal '{id}' is {goal.Status.ToDisplayName()}, not Draft. Only Draft goals can be approved.";

        goal.Status = GoalStatus.Pending;
        await _goalStore.UpdateGoalAsync(goal);
        _logger.LogInformation("Composer approved goal '{GoalId}' → Pending", id);

        return $"✅ Goal '{id}' approved — status changed to Pending. It will be dispatched in the next cycle.";
    }

    [Description("Permanently delete a goal. Only Draft or Failed goals can be deleted.")]
    internal async Task<string> DeleteGoalAsync(
        [Description("Goal ID to delete")] string id)
    {
        var error = Shared.ToolValidation.Check(
            (!string.IsNullOrWhiteSpace(id), "id is required"));
        if (error is not null) return error;

        var goal = await _goalStore.GetGoalAsync(id);
        if (goal is null)
            return $"❌ Goal '{id}' not found.";

        if (goal.Status is not (GoalStatus.Draft or GoalStatus.Failed))
            return $"❌ Goal '{id}' is {goal.Status.ToDisplayName()}. Only Draft or Failed goals can be deleted.";

        var deleted = await _goalStore.DeleteGoalAsync(id);
        if (!deleted)
            return $"❌ Failed to delete goal '{id}'.";

        _logger.LogInformation("Composer deleted goal '{GoalId}'", id);
        return $"✅ Goal '{id}' has been permanently deleted.";
    }

    [Description("Update a field on an existing goal.")]
    internal async Task<string> UpdateGoalAsync(
        [Description("Goal ID to update")] string id,
        [Description("Field to update: description, priority, repositories, or status")] string field,
        [Description("New value for the field")] string value)
    {
        var error = Shared.ToolValidation.Check(
            (!string.IsNullOrWhiteSpace(id), "id is required"),
            (!string.IsNullOrWhiteSpace(field), "field is required"),
            (!string.IsNullOrWhiteSpace(value), "value is required"));
        if (error is not null) return error;

        var goal = await _goalStore.GetGoalAsync(id);
        if (goal is null)
            return $"❌ Goal '{id}' not found.";

        switch (field.ToLowerInvariant())
        {
            case "description":
                // Description is init-only, so we must re-create the goal record.
                // For now, return an error — description is immutable once created.
                return "❌ Description cannot be changed after creation. Delete and re-create the goal instead.";

            case "priority":
                if (!Enum.TryParse<GoalPriority>(value, ignoreCase: true, out var newPriority))
                    return $"❌ Invalid priority '{value}'. Valid: Low, Normal, High, Critical.";
                // Priority is init-only in Goal; update via store directly
                return $"❌ Priority cannot be changed after creation. Delete and re-create the goal instead.";

            case "status":
                if (!Enum.TryParse<GoalStatus>(value, ignoreCase: true, out var newStatus))
                    return $"❌ Invalid status '{value}'. Valid: Draft, Pending.";
                if (newStatus is not (GoalStatus.Draft or GoalStatus.Pending))
                    return $"❌ Can only set status to Draft or Pending via update_goal.";
                goal.Status = newStatus;
                await _goalStore.UpdateGoalAsync(goal);
                _logger.LogInformation("Composer updated goal '{GoalId}' status to {Status}", id, newStatus);
                return $"✅ Goal '{id}' status updated to {newStatus.ToDisplayName()}.";

            case "repositories":
                var repos = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                // RepositoryNames is init-only; we'd need to recreate
                return "❌ Repositories cannot be changed after creation. Delete and re-create the goal instead.";

            default:
                return $"❌ Unknown field '{field}'. Valid fields: description, priority, status, repositories.";
        }
    }

    [Description("Get full details for a goal including iteration history.")]
    internal async Task<string> GetGoalAsync(
        [Description("Goal ID to look up")] string id)
    {
        var error = Shared.ToolValidation.Check(
            (!string.IsNullOrWhiteSpace(id), "id is required"));
        if (error is not null) return error;

        var goal = await _goalStore.GetGoalAsync(id);
        if (goal is null)
            return $"Goal '{id}' not found.";

        var iterations = await _goalStore.GetIterationsAsync(id);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Goal: {goal.Id}");
        sb.AppendLine($"- **Status:** {goal.Status.ToDisplayName()}");
        sb.AppendLine($"- **Priority:** {goal.Priority}");
        sb.AppendLine($"- **Created:** {goal.CreatedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- **Repositories:** {(goal.RepositoryNames.Count > 0 ? string.Join(", ", goal.RepositoryNames) : "(none)")}");
        sb.AppendLine($"- **Description:** {goal.Description}");

        if (goal.FailureReason is not null)
            sb.AppendLine($"- **Failure:** {goal.FailureReason}");

        if (goal.TotalDurationSeconds.HasValue)
            sb.AppendLine($"- **Duration:** {TimeSpan.FromSeconds(goal.TotalDurationSeconds.Value):hh\\:mm\\:ss}");

        if (iterations.Count > 0)
        {
            sb.AppendLine($"\n### Iterations ({iterations.Count})");
            foreach (var iter in iterations)
            {
                sb.AppendLine($"- **Iteration {iter.Iteration}:** {iter.Phases.Count} phases" +
                    (iter.TestCounts is not null ? $", {iter.TestCounts.Passed}/{iter.TestCounts.Total} tests" : "") +
                    (iter.ReviewVerdict is not null ? $", review: {iter.ReviewVerdict}" : ""));
            }
        }

        if (goal.Notes.Count > 0)
        {
            sb.AppendLine($"\n### Notes");
            foreach (var note in goal.Notes)
                sb.AppendLine($"- {note}");
        }

        return sb.ToString();
    }

    [Description("List goals, optionally filtered by status.")]
    internal async Task<string> ListGoalsAsync(
        [Description("Optional status filter: Draft, Pending, InProgress, Completed, Failed")] string? status = null)
    {
        IReadOnlyList<Goal> goals;

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<GoalStatus>(status, ignoreCase: true, out var filter))
        {
            goals = await _goalStore.GetGoalsByStatusAsync(filter);
        }
        else
        {
            goals = await _goalStore.GetAllGoalsAsync();
        }

        if (goals.Count == 0)
            return status is not null ? $"No goals with status '{status}'." : "No goals found.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"**{goals.Count} goal(s):**\n");

        foreach (var g in goals.OrderByDescending(g => g.CreatedAt))
        {
            sb.AppendLine($"- `{g.Id}` [{g.Status.ToDisplayName()}] {g.Priority} — {Truncate(g.Description, 80)}");
        }

        return sb.ToString();
    }

    [Description("Search goals by text query across ID, description, and failure reason.")]
    internal async Task<string> SearchGoalsAsync(
        [Description("Search query text")] string query,
        [Description("Optional status filter: Draft, Pending, InProgress, Completed, Failed")] string? status = null)
    {
        var error = Shared.ToolValidation.Check(
            (!string.IsNullOrWhiteSpace(query), "query is required"));
        if (error is not null) return error;

        GoalStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<GoalStatus>(status, ignoreCase: true, out var s))
            statusFilter = s;

        var results = await _goalStore.SearchGoalsAsync(query, statusFilter);

        if (results.Count == 0)
            return $"No goals matching '{query}'" + (statusFilter.HasValue ? $" with status {statusFilter}" : "") + ".";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"**{results.Count} result(s) for '{query}':**\n");

        foreach (var g in results)
        {
            sb.AppendLine($"- `{g.Id}` [{g.Status.ToDisplayName()}] — {Truncate(g.Description, 80)}");
        }

        return sb.ToString();
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";

    private static bool IsValidGoalId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        try { GoalId.Validate(id); return true; }
        catch (ArgumentException) { return false; }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_chatClient is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (_chatClient is IDisposable disposable)
            disposable.Dispose();
    }
}

using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Services;
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
    private readonly IBrainRepoManager? _repoManager;
    private readonly GoalDispatcher? _goalDispatcher;
    private readonly string _stateDir;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly string? _ollamaApiKey;
    private IChatClient? _chatClient;
    private CodingAgent? _agent;
    private AgentSession _session;

    private readonly HiveConfigFile? _hiveConfig;
    private readonly string _systemPrompt;
    private readonly List<AITool> _composerTools;

    // Streaming state owned by the service (survives component navigation)
    private string _streamingContent = "";
    private bool _isStreaming;
    private int _lastToolCalls;
    private CancellationTokenSource? _streamCts;

    /// <summary>Whether the Composer is currently streaming a response.</summary>
    public bool IsStreaming => _isStreaming;

    /// <summary>The accumulated streaming text (partial response in progress).</summary>
    public string StreamingContent => _streamingContent;

    /// <summary>Tool call count from the last completed response.</summary>
    public int LastToolCalls => _lastToolCalls;

    /// <summary>Raised when streaming state changes (new text, completion, error).</summary>
    public event Action? OnStreamingUpdate;

    /// <summary>The question currently waiting for a user answer, or <c>null</c> if none.</summary>
    public ComposerQuestion? PendingQuestion { get; private set; }

    /// <summary>Raised when the Composer asks a new question so the UI can re-render.</summary>
    public event Action? OnQuestionAsked;

    private const string DefaultSystemPrompt = """
        You are the Composer — a strategic advisor for the CopilotHive multi-agent system.
        You help the user decompose high-level intent into well-scoped, actionable goals.

        Your capabilities:
        - Read the codebase to understand current state (read_file, glob, grep)
        - Search existing goals to avoid duplication (search_goals)
        - Browse goal history and status (list_goals, get_goal)
        - Drill into worker phase output for Coding, Testing, Review, DocWriting, or Improve (get_phase_output)
        - Create goals as drafts for user review (create_goal)
        - Approve drafts to queue them for execution (approve_goal)
        - Update existing goals (update_goal)
        - Delete draft or failed goals (delete_goal)
        - Cancel InProgress or Pending goals (cancel_goal)
        - Inspect repository history (git_log, git_diff, git_show, git_branch, git_blame)
        - List configured repositories (list_repositories)
        - Ask the user questions for clarification (ask_user)

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
        IBrainRepoManager? repoManager = null,
        string? stateDir = null,
        GoalDispatcher? goalDispatcher = null,
        IHttpClientFactory? httpClientFactory = null,
        string? ollamaApiKey = null,
        HiveConfigFile? hiveConfig = null)
    {
        _model = model;
        _maxContextTokens = maxContextTokens;
        _maxSteps = maxSteps;
        _logger = logger;
        _goalStore = goalStore;
        _repoManager = repoManager;
        _goalDispatcher = goalDispatcher;
        _stateDir = stateDir ?? "/app/state";
        _httpClientFactory = httpClientFactory;
        _ollamaApiKey = string.IsNullOrWhiteSpace(ollamaApiKey) ? null : ollamaApiKey;
        _hiveConfig = hiveConfig;
        _session = AgentSession.Create("composer");

        var (_, _, reasoning) = SDK.ChatClientFactory.ParseProviderModelAndReasoning(model);
        _reasoningEffort = reasoning;

        _systemPrompt = DefaultSystemPrompt;
        if (_ollamaApiKey is not null)
            _systemPrompt += "\n- Research information on the web (web_search, web_fetch)";

        var repos = _hiveConfig?.Repositories;
        if (repos is not null && repos.Count > 0)
        {
            _systemPrompt += "\n\nConfigured repositories:";
            foreach (var repo in repos)
                _systemPrompt += $"\n- {repo.Name} ({repo.Url}, default branch: {repo.DefaultBranch})";
        }

        _composerTools = BuildComposerTools();
    }

    /// <summary>Whether the Composer has connected and is ready for streaming.</summary>
    public bool IsConnected => _agent is not null;

    /// <summary>Returns the system prompt used by the Composer.</summary>
    internal string GetSystemPrompt() => _systemPrompt;

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
    /// Sends a message and streams the response in the background.
    /// The streaming state is owned by the Composer service and survives component navigation.
    /// Subscribe to <see cref="OnStreamingUpdate"/> to receive updates.
    /// </summary>
    public void SendMessage(string userMessage)
    {
        if (_agent is null)
            throw new InvalidOperationException("Composer not connected. Call ConnectAsync first.");
        if (_isStreaming)
            throw new InvalidOperationException("Composer is already streaming a response.");

        _streamingContent = "";
        _isStreaming = true;
        _lastToolCalls = 0;
        _streamCts = new CancellationTokenSource();

        _ = RunStreamingAsync(userMessage, _streamCts.Token);
    }

    private async Task RunStreamingAsync(string userMessage, CancellationToken ct)
    {
        _logger.LogInformation("Composer streaming response for: {Message}",
            userMessage.Length > 100 ? userMessage[..100] + "…" : userMessage);

        try
        {
            await foreach (var update in _agent!.ExecuteStreamingAsync(_session, userMessage, ct))
            {
                switch (update.Kind)
                {
                    case StreamingUpdateKind.TextDelta:
                        _streamingContent += update.Text;
                        OnStreamingUpdate?.Invoke();
                        break;

                    case StreamingUpdateKind.Completed:
                        _lastToolCalls = update.Result?.ToolCallCount ?? 0;
                        break;
                }
            }

            await SaveSessionAsync(ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Composer streaming cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Composer streaming failed");
            _streamingContent += $"\n\n❌ Error: {ex.Message}";
        }
        finally
        {
            _isStreaming = false;
            _streamCts?.Dispose();
            _streamCts = null;
            OnStreamingUpdate?.Invoke();
        }
    }

    /// <summary>
    /// Cancels the current streaming response if one is in progress.
    /// </summary>
    public void CancelStreaming()
    {
        _streamCts?.Cancel();
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
    /// Tool-call messages are formatted as markdown inline code, tool-result messages
    /// as blockquotes, so they appear in the chat history at the correct position.
    /// </summary>
    /// <param name="maxMessages">Maximum number of messages to return.</param>
    public IReadOnlyList<(string Role, string Content)> GetChatHistory(int maxMessages = 50)
    {
        var result = new List<(string Role, string Content)>();
        foreach (var msg in _session.MessageHistory)
        {
            if (msg.Role == Microsoft.Extensions.AI.ChatRole.User)
            {
                var text = msg.Text;
                if (!string.IsNullOrWhiteSpace(text))
                    result.Add(("user", text));
            }
            else if (msg.Role == Microsoft.Extensions.AI.ChatRole.Assistant)
            {
                // Check if this assistant message contains tool calls
                var functionCalls = msg.Contents.OfType<Microsoft.Extensions.AI.FunctionCallContent>().ToList();
                if (functionCalls.Count > 0)
                {
                    // Include any text preceding the tool calls
                    var text = msg.Text;
                    if (!string.IsNullOrWhiteSpace(text))
                        result.Add(("assistant", text));

                    // Format each tool call as markdown
                    var sb = new System.Text.StringBuilder();
                    foreach (var fc in functionCalls)
                    {
                        var argsStr = FormatToolCallArgs(fc.Arguments);
                        sb.AppendLine($"`🔧 {fc.Name}({argsStr})`");
                    }
                    result.Add(("assistant", sb.ToString().TrimEnd()));
                }
                else
                {
                    var text = msg.Text;
                    if (!string.IsNullOrWhiteSpace(text))
                        result.Add(("assistant", text));
                }
            }
            else if (msg.Role == Microsoft.Extensions.AI.ChatRole.Tool)
            {
                // Format tool results as blockquote
                var results = msg.Contents.OfType<Microsoft.Extensions.AI.FunctionResultContent>().ToList();
                if (results.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var fr in results)
                    {
                        var resultStr = fr.Result?.ToString() ?? "(no result)";
                        var firstLine = TruncateFirstLine(resultStr, 120);
                        sb.AppendLine($"> {firstLine}");
                    }
                    result.Add(("assistant", sb.ToString().TrimEnd()));
                }
            }
        }

        if (result.Count > maxMessages)
            return result.GetRange(result.Count - maxMessages, maxMessages);

        return result;
    }

    private static string FormatToolCallArgs(IDictionary<string, object?>? args, int maxLength = 100)
    {
        if (args == null || args.Count == 0) return "";
        var parts = args.Select(a =>
        {
            var val = a.Value?.ToString() ?? "null";
            if (val.Length > 40) val = val[..39] + "…";
            return $"{a.Key}=\"{val}\"";
        });
        var joined = string.Join(", ", parts);
        if (joined.Length > maxLength) joined = joined[..(maxLength - 1)] + "…";
        return joined;
    }

    private static string TruncateFirstLine(string text, int maxLength)
    {
        var newline = text.IndexOfAny(['\n', '\r']);
        var line = newline >= 0 ? text[..newline] : text;
        if (line.Length > maxLength) line = line[..(maxLength - 1)] + "…";
        return line;
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
            ShowToolCallsInStream = true,
            Logger = _logger,
            OnCompacted = r => _logger.LogInformation(
                "Composer context compaction: {TokensBefore} → {TokensAfter} tokens ({ReductionPercent}% reduction)",
                r.TokensBefore, r.TokensAfter, r.ReductionPercent),
        });

        _logger.LogDebug("Composer CodingAgent created with WorkDirectory={WorkDir}, FileOps={FileOps}",
            workDir, _repoManager is not null);
    }

    // ── Tool implementations ──

    /// <summary>
    /// Presents a question to the user and suspends the streaming loop until an answer is received.
    /// Called by the Composer LLM via the <c>ask_user</c> tool.
    /// </summary>
    [Description("Ask the user a question and wait for their answer. Suspends the response until the user replies.")]
    internal async Task<string> AskUserAsync(
        [Description("The question text to display to the user.")] string question,
        [Description("Question type: YesNo, SingleChoice, or MultiChoice. Default: YesNo")] string type = "YesNo",
        [Description("Comma-separated list of options for SingleChoice or MultiChoice questions. Leave empty for YesNo.")] string? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return "❌ question is required.";

        if (!Enum.TryParse<QuestionType>(type, ignoreCase: true, out var questionType))
            return $"❌ Invalid type '{type}'. Valid types: YesNo, SingleChoice, MultiChoice.";

        var optionList = questionType == QuestionType.YesNo
            ? ["Yes", "No"]
            : (options?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? []);

        if (questionType != QuestionType.YesNo && optionList.Count == 0)
            return $"❌ Options are required for {questionType} questions.";

        var pending = new ComposerQuestion
        {
            Text = question,
            Type = questionType,
            Options = optionList,
        };

        PendingQuestion = pending;
        OnQuestionAsked?.Invoke();

        _logger.LogInformation("Composer waiting for user answer to question: {Question}", question);

        try
        {
            return await pending.Completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            PendingQuestion = null;
        }
    }

    /// <summary>
    /// Submits the user's answer to the currently pending question, resuming the streaming loop.
    /// </summary>
    /// <param name="answer">The answer string to return to the Composer LLM.</param>
    public void SubmitAnswer(string answer)
    {
        var pending = PendingQuestion;
        if (pending is null) return;
        pending.Completion.TrySetResult(answer);
    }

    /// <summary>
    /// Cancels the currently pending question, returning a cancellation message to the LLM.
    /// </summary>
    public void CancelQuestion()
    {
        var pending = PendingQuestion;
        if (pending is null) return;
        pending.Completion.TrySetResult("(User cancelled the question.)");
    }

    internal List<AITool> BuildComposerTools()
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(AskUserAsync, "ask_user",
                "Ask the user a question and wait for their answer. Use for clarification or confirmation."),
            AIFunctionFactory.Create(CreateGoalAsync, "create_goal",
                "Create a new goal as Draft. It will not be dispatched until approved."),
            AIFunctionFactory.Create(ApproveGoalAsync, "approve_goal",
                "Approve a Draft goal, changing its status to Pending so it will be dispatched."),
            AIFunctionFactory.Create(UpdateGoalAsync, "update_goal",
                "Update a field on an existing goal."),
            AIFunctionFactory.Create(GetGoalAsync, "get_goal",
                "Get full details for a goal including iteration history."),
            AIFunctionFactory.Create(GetPhaseOutputAsync, "get_phase_output",
                "Get the raw worker output for a specific phase within an iteration."),
            AIFunctionFactory.Create(ListGoalsAsync, "list_goals",
                "List goals, optionally filtered by status."),
            AIFunctionFactory.Create(SearchGoalsAsync, "search_goals",
                "Search goals by text query across ID, description, and failure reason."),
            AIFunctionFactory.Create(DeleteGoalAsync, "delete_goal",
                "Permanently delete a goal. Only Draft or Failed goals can be deleted."),
            AIFunctionFactory.Create(CancelGoalAsync, "cancel_goal",
                "Cancel an InProgress or Pending goal, stopping its execution."),
            AIFunctionFactory.Create(GitLogAsync, "git_log",
                "View commit history for a repository branch or path."),
            AIFunctionFactory.Create(GitDiffAsync, "git_diff",
                "Compare changes between two refs or between a ref and the working tree."),
            AIFunctionFactory.Create(GitShowAsync, "git_show",
                "View the details and diff of a specific commit."),
            AIFunctionFactory.Create(GitBranchAsync, "git_branch",
                "List local or remote branches in a repository."),
            AIFunctionFactory.Create(GitBlameAsync, "git_blame",
                "Show line-by-line authorship information for a file."),
            AIFunctionFactory.Create(ListRepositoriesAsync, "list_repositories",
                "List all configured repositories with their names, URLs, and default branches."),
        };

        if (_ollamaApiKey is not null)
        {
            tools.Add(AIFunctionFactory.Create(WebSearchAsync, "web_search",
                "Search the web for information. Returns titles, URLs, and content snippets."));
            tools.Add(AIFunctionFactory.Create(WebFetchAsync, "web_fetch",
                "Fetch a web page and return its content. Use after web_search to read full pages."));
        }

        return tools;
    }

    [Description("Create a new goal as Draft status. Returns the created goal summary.")]
    internal async Task<string> CreateGoalAsync(
        [Description("Unique goal ID in lowercase-kebab-case (e.g. 'add-user-auth')")] string id,
        [Description("Clear description including acceptance criteria")] string description,
        [Description("Comma-separated repository names this goal applies to")] string? repositories = null,
        [Description("Priority: Low, Normal, High, or Critical. Default: Normal")] string? priority = null,
        [Description("Comma-separated goal IDs this goal depends on")] string? depends_on = null)
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

        var deps = string.IsNullOrWhiteSpace(depends_on)
            ? new List<string>()
            : depends_on.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var goal = new Goal
        {
            Id = id,
            Description = description,
            Priority = goalPriority,
            Status = GoalStatus.Draft,
            RepositoryNames = repos,
            DependsOn = deps,
        };

        await _goalStore.CreateGoalAsync(goal);
        _logger.LogInformation("Composer created draft goal '{GoalId}'", id);

        return $"""
            ✅ Goal created as Draft:
            - ID: {id}
            - Priority: {goalPriority}
            - Repositories: {(repos.Count > 0 ? string.Join(", ", repos) : "(none)")}
            - Dependencies: {(deps.Count > 0 ? string.Join(", ", deps) : "(none)")}
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

        // Best-effort cleanup of remote feature branches for Failed goals
        if (_repoManager is not null && goal.Status == GoalStatus.Failed)
        {
            var branchName = $"copilothive/{id}";
            foreach (var repoName in goal.RepositoryNames)
            {
                try
                {
                    await _repoManager.DeleteRemoteBranchAsync(repoName, branchName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete remote branch {Branch} from {Repo}", branchName, repoName);
                }
            }
        }

        return $"✅ Goal '{id}' has been permanently deleted.";
    }

    [Description("Cancel an InProgress or Pending goal, stopping its execution.")]
    internal async Task<string> CancelGoalAsync(
        [Description("Goal ID to cancel")] string id)
    {
        var error = Shared.ToolValidation.Check(
            (!string.IsNullOrWhiteSpace(id), "id is required"));
        if (error is not null) return error;

        var goal = await _goalStore.GetGoalAsync(id);
        if (goal is null)
            return $"❌ Goal '{id}' not found.";

        if (goal.Status is not (GoalStatus.InProgress or GoalStatus.Pending))
            return $"❌ Goal '{id}' is {goal.Status.ToDisplayName()}. Only InProgress or Pending goals can be cancelled.";

        if (_goalDispatcher is null)
            return "❌ Goal dispatcher is not available — cannot cancel goals.";

        var cancelled = await _goalDispatcher.CancelGoalAsync(id);
        if (!cancelled)
            return $"❌ Goal '{id}' could not be cancelled (it may have already completed or failed).";

        _logger.LogInformation("Composer cancelled goal '{GoalId}'", id);
        return $"✅ Goal '{id}' has been cancelled.";
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
                var validTransition =
                    (goal.Status == GoalStatus.Draft && newStatus == GoalStatus.Pending) ||
                    (goal.Status == GoalStatus.Pending && newStatus == GoalStatus.Draft);
                if (!validTransition)
                    return $"❌ Invalid transition from {goal.Status.ToDisplayName()} to {newStatus.ToDisplayName()}. Only Draft→Pending and Pending→Draft are allowed.";
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
                var reviewSuffix = iter.ReviewVerdict is not null ? $" (review: {iter.ReviewVerdict})" : "";
                sb.AppendLine($"\n### Iteration {iter.Iteration}{reviewSuffix}");
                foreach (var phase in iter.Phases)
                {
                    var durationStr = $"{phase.DurationSeconds:F1}s";
                    var line = $"- {phase.Name}: {phase.Result} ({durationStr})";
                    if (phase.Name.Equals("Testing", StringComparison.OrdinalIgnoreCase) && iter.TestCounts is not null)
                        line += $" — {iter.TestCounts.Passed}/{iter.TestCounts.Total}";
                    sb.AppendLine(line);
                }
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

    private static readonly Dictionary<string, string> PhaseOutputKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Coding"] = "coder",
        ["Testing"] = "tester",
        ["Review"] = "reviewer",
        ["DocWriting"] = "docwriter",
        ["Improve"] = "improver",
    };

    [Description("Get the raw worker output for a specific phase within an iteration.")]
    internal async Task<string> GetPhaseOutputAsync(
        [Description("Goal ID")] string id,
        [Description("Iteration number (1-based)")] int iteration,
        [Description("Phase name: Coding, Testing, Review, DocWriting, or Improve")] string phase,
        [Description("Maximum lines to return. Default: 200")] int max_lines = 200)
    {
        // 1. Explicit required-param validation with EXACT messages FIRST
        if (string.IsNullOrWhiteSpace(id))
            return "Goal ID is required";
        if (iteration <= 0)
            return "Iteration must be a positive number";
        if (string.IsNullOrWhiteSpace(phase))
            return "ERROR: Invalid parameters: phase is required";

        // 2. Whitelist check SECOND
        if (!PhaseOutputKeys.TryGetValue(phase, out var rolePrefix))
            return $"Unknown phase '{phase}'. Supported phases: Coding, Testing, Review, DocWriting, Improve.";

        // 2. Fetch goal
        var goal = await _goalStore.GetGoalAsync(id);
        if (goal is null)
            return "Goal not found";

        // 3. Fetch iterations and find the requested iteration
        var iterations = await _goalStore.GetIterationsAsync(id);
        var iterSummary = iterations.FirstOrDefault(i => i.Iteration == iteration);
        if (iterSummary is null)
            return $"Iteration {iteration} not found";

        // 4. Find the phase in the iteration
        var phaseResult = iterSummary.Phases
            .FirstOrDefault(p => p.Name.Equals(phase, StringComparison.OrdinalIgnoreCase));
        if (phaseResult is null)
            return $"Phase '{phase}' not found in iteration {iteration}";

        // 5. Check worker output, then fall back to PhaseOutputs dictionary
        string? output = phaseResult.WorkerOutput;

        if (string.IsNullOrEmpty(output))
        {
            var outputKey = $"{rolePrefix}-{iteration}";
            iterSummary.PhaseOutputs.TryGetValue(outputKey, out output);
        }

        if (string.IsNullOrEmpty(output))
            return $"No output recorded for phase {phase} in iteration {iteration}";

        var lines = output.Split('\n');
        if (lines.Length <= max_lines)
            return output;

        var truncated = string.Join('\n', lines.Take(max_lines));
        return truncated + $"\n... (truncated, {lines.Length} lines total)";
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

    [Description("List all configured repositories with their names, URLs, and default branches.")]
    internal Task<string> ListRepositoriesAsync()
    {
        var repos = _hiveConfig?.Repositories;
        if (repos is null || repos.Count == 0)
            return Task.FromResult("No repositories configured.");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Configured Repositories ({repos.Count})");
        foreach (var repo in repos)
            sb.AppendLine($"- **{repo.Name}** — {repo.Url} (branch: {repo.DefaultBranch})");
        return Task.FromResult(sb.ToString().TrimEnd());
    }

    // ── Git tool implementations ──

    /// <summary>
    /// Runs a git command in the clone of <paramref name="repoName"/> and returns the output.
    /// Returns an error string if the repo manager is unavailable or the repo is not found.
    /// Output is truncated to <paramref name="maxLines"/> lines with a notice when truncated.
    /// </summary>
    /// <param name="repoName">Short name of the cloned repository.</param>
    /// <param name="args">Git arguments to pass via <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>.</param>
    /// <param name="maxLines">Maximum lines to return before truncating.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Git command output, or an error message string.</returns>
    private async Task<string> RunGitAsync(
        string repoName, string[] args, int maxLines = 500, CancellationToken ct = default)
    {
        if (_repoManager is null)
            return "Git tools are not available — no repository manager configured.";

        var clonePath = _repoManager.GetClonePath(repoName);

        // SECURITY: Prevent path traversal — validate resolved path is under the expected repos directory.
        var expectedReposDir = Path.GetFullPath(_repoManager.WorkDirectory);
        var resolvedPath = Path.GetFullPath(clonePath);
        if (!resolvedPath.StartsWith(expectedReposDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(resolvedPath, expectedReposDir, StringComparison.Ordinal))
        {
            return $"Repository '{repoName}' is not within the managed repos directory. Access denied.";
        }

        if (!Directory.Exists(Path.Combine(clonePath, ".git")))
        {
            var reposDir = _repoManager.WorkDirectory;
            var available = Directory.Exists(reposDir)
                ? Directory.GetDirectories(reposDir)
                    .Where(d => Directory.Exists(Path.Combine(d, ".git")))
                    .Select(Path.GetFileName)
                    .Where(n => n is not null)
                    .ToList()
                : [];
            var list = available.Count > 0 ? string.Join(", ", available) : "(none)";
            return $"Repository '{repoName}' not found. Available: {list}";
        }

        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = clonePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var stderr = stderrTask.Result;
            return $"git {string.Join(' ', args)} failed (exit {process.ExitCode}): {stderr}";
        }

        var output = stdoutTask.Result;
        var lines = output.Split('\n');
        if (lines.Length <= maxLines)
            return output;

        var truncated = string.Join('\n', lines.Take(maxLines));
        return truncated + $"\n... (truncated, {lines.Length} lines total)";
    }

    [Description("View commit history for a repository.")]
    internal async Task<string> GitLogAsync(
        [Description("Repository name")] string repository,
        [Description("Maximum number of commits to show. Default: 20")] int max_count = 20,
        [Description("Branch or ref to show history for. Optional — uses current HEAD if omitted")] string? branch = null,
        [Description("Limit to commits that touch this file or directory path. Optional")] string? path = null,
        [Description("Show only commits after this date (e.g. '2024-01-01'). Optional")] string? since = null,
        [Description("Log format: oneline, short, or full. Default: oneline")] string format = "oneline",
        [Description("Include diffstat summary per commit. Default: false")] bool stat = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return "❌ repository is required.";

        var validFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "oneline", "short", "full" };
        if (!validFormats.Contains(format))
            return $"❌ Invalid format '{format}'. Must be one of: oneline, short, full.";

        var args = new List<string> { "log", $"--max-count={max_count}" };

        // Use correct git format flags: 'oneline' needs --oneline, not --format=oneline
        var formatArg = format.ToLowerInvariant() switch
        {
            "oneline" => "--oneline",
            "short" => "--format=short",
            "full" => "--format=full",
            _ => "--oneline",
        };
        args.Add(formatArg);

        if (stat)
            args.Add("--stat");

        if (!string.IsNullOrWhiteSpace(since))
            args.Add($"--since={since}");

        // SECURITY: Validate branch to prevent git option injection
        if (!string.IsNullOrWhiteSpace(branch))
        {
            if (branch.StartsWith('-'))
                return $"❌ Invalid branch '{branch}': branch names cannot start with '-'.";
            args.Add(branch);
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            args.Add("--");
            args.Add(path);
        }

        return await RunGitAsync(repository, [.. args], ct: cancellationToken);
    }

    [Description("Compare changes between two refs, or between a ref and HEAD.")]
    internal async Task<string> GitDiffAsync(
        [Description("Repository name")] string repository,
        [Description("First ref (commit, branch, or tag) to compare")] string ref1,
        [Description("Second ref to compare against. If omitted, diffs ref1 against HEAD")] string? ref2 = null,
        [Description("Limit the diff to this file or directory path. Optional")] string? path = null,
        [Description("Show only the diffstat summary, not the full diff. Default: false")] bool stat_only = false,
        [Description("Maximum lines of output to return. Default: 500")] int max_lines = 500,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return "❌ repository is required.";
        if (string.IsNullOrWhiteSpace(ref1))
            return "❌ ref1 is required.";

        // SECURITY: Prevent git option injection for refs
        if (ref1.StartsWith('-'))
            return $"❌ Invalid ref '{ref1}': refs cannot start with '-'.";
        if (!string.IsNullOrWhiteSpace(ref2) && ref2.StartsWith('-'))
            return $"❌ Invalid ref '{ref2}': refs cannot start with '-'.";

        var args = new List<string> { "diff" };

        if (stat_only)
            args.Add("--stat");

        if (!string.IsNullOrWhiteSpace(ref2))
            args.Add($"{ref1}..{ref2}");
        else
            args.Add(ref1);

        if (!string.IsNullOrWhiteSpace(path))
        {
            args.Add("--");
            args.Add(path);
        }

        return await RunGitAsync(repository, [.. args], maxLines: max_lines, ct: cancellationToken);
    }

    [Description("View the details and diff of a specific commit.")]
    internal async Task<string> GitShowAsync(
        [Description("Repository name")] string repository,
        [Description("Commit ref (SHA, tag, or branch) to show")] string @ref,
        [Description("Show only the diffstat summary, not the full diff. Default: false")] bool stat_only = false,
        [Description("Limit output to this file or directory path. Optional")] string? path = null,
        [Description("Maximum lines of output to return. Default: 500")] int max_lines = 500,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return "❌ repository is required.";
        if (string.IsNullOrWhiteSpace(@ref))
            return "❌ ref is required.";

        // SECURITY: Prevent git option injection for ref
        if (@ref.StartsWith('-'))
            return $"❌ Invalid ref '{@ref}': refs cannot start with '-'.";

        var args = new List<string> { "show" };

        if (stat_only)
            args.Add("--stat");

        args.Add(@ref);

        if (!string.IsNullOrWhiteSpace(path))
        {
            args.Add("--");
            args.Add(path);
        }

        return await RunGitAsync(repository, [.. args], maxLines: max_lines, ct: cancellationToken);
    }

    [Description("List local or remote branches in a repository.")]
    internal async Task<string> GitBranchAsync(
        [Description("Repository name")] string repository,
        [Description("Optional glob pattern to filter branches (e.g. 'feature/*')")] string? pattern = null,
        [Description("List remote tracking branches instead of local branches. Default: false")] bool remote = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return "❌ repository is required.";

        var args = new List<string> { "branch", "--list" };

        if (remote)
            args.Add("-r");

        // SECURITY: Prevent git option injection for pattern
        if (!string.IsNullOrWhiteSpace(pattern))
        {
            if (pattern.StartsWith('-'))
                return $"❌ Invalid pattern '{pattern}': patterns cannot start with '-'.";
            args.Add(pattern);
        }

        return await RunGitAsync(repository, [.. args], ct: cancellationToken);
    }

    [Description("Show line-by-line authorship information for a file.")]
    internal async Task<string> GitBlameAsync(
        [Description("Repository name")] string repository,
        [Description("Relative path to the file within the repository")] string path,
        [Description("First line to show blame for (1-indexed). Optional")] int? start_line = null,
        [Description("Last line to show blame for (1-indexed, inclusive). Optional")] int? end_line = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return "❌ repository is required.";
        if (string.IsNullOrWhiteSpace(path))
            return "❌ path is required.";

        var args = new List<string> { "blame" };

        if (start_line.HasValue || end_line.HasValue)
        {
            var start = start_line ?? 1;
            var end = end_line ?? start;
            args.Add($"-L{start},{end}");
        }

        // SECURITY: Place path after -- to terminate option parsing
        args.Add("--");
        args.Add(path);

        return await RunGitAsync(repository, [.. args], ct: cancellationToken);
    }

    // ── Web research tool implementations ──

    [Description("Search the web for information. Returns titles, URLs, and content snippets.")]
    internal async Task<string> WebSearchAsync(
        [Description("Search query")] string query,
        [Description("Maximum results to return (1-10, default 5)")] int max_results = 5)
    {
        if (_ollamaApiKey is null)
            return "❌ Web search is not available — no OLLAMA_API_KEY configured.";

        if (string.IsNullOrWhiteSpace(query))
            return "❌ query is required.";

        max_results = Math.Clamp(max_results, 1, 10);

        var client = _httpClientFactory!.CreateClient("ollama-web");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/web_search");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _ollamaApiKey);
            request.Content = JsonContent.Create(new { query, max_results });

            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return $"❌ Web search failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            const int MaxContentChars = 500;
            var sb = new System.Text.StringBuilder();
            var results = doc.RootElement.GetProperty("results");
            foreach (var result in results.EnumerateArray())
            {
                var title = result.TryGetProperty("title", out var t) ? t.GetString() : "";
                var url = result.TryGetProperty("url", out var u) ? u.GetString() : "";
                var content = result.TryGetProperty("content", out var c) ? c.GetString() : "";
                if (content is not null && content.Length > MaxContentChars)
                    content = content[..MaxContentChars] + "…";
                sb.AppendLine($"### {title}");
                sb.AppendLine(url);
                sb.AppendLine(content);
                sb.AppendLine();
            }

            return sb.Length > 0 ? sb.ToString().TrimEnd() : "No results found.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "WebSearch failed for query '{Query}'", query);
            return $"❌ Web search error: {ex.Message}";
        }
    }

    [Description("Fetch a web page and return its content. Use after web_search to read full pages.")]
    internal async Task<string> WebFetchAsync(
        [Description("URL to fetch")] string url,
        [Description("Maximum lines of content to return. Default: 100")] int max_lines = 100)
    {
        if (_ollamaApiKey is null)
            return "❌ Web fetch is not available — no OLLAMA_API_KEY configured.";

        if (string.IsNullOrWhiteSpace(url))
            return "❌ url is required.";

        var client = _httpClientFactory!.CreateClient("ollama-web");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/web_fetch");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _ollamaApiKey);
            request.Content = JsonContent.Create(new { url });

            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return $"❌ Web fetch failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var content = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            var links = doc.RootElement.TryGetProperty("links", out var l)
                ? l.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).ToList()
                : new List<string?>();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# {title}");
            sb.AppendLine();
            sb.Append(content);

            if (links.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("## Links");
                foreach (var link in links)
                    sb.AppendLine($"- {link}");
            }

            var output = sb.ToString();
            var lines = output.Split('\n');
            if (lines.Length > max_lines)
            {
                output = string.Join('\n', lines.Take(max_lines));
                output += $"\n...(truncated, {lines.Length} lines total)";
            }

            return output;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "WebFetch failed for url '{Url}'", url);
            return $"❌ Web fetch error: {ex.Message}";
        }
    }

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

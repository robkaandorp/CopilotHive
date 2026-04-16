using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Services;
using CopilotHive.Shared.AI;

using Microsoft.Extensions.AI;

using SharpCoder;

using System.ComponentModel;

namespace CopilotHive.Orchestration;

/// <summary>
/// Conversational agent for goal decomposition and management.
/// The Composer helps users break down high-level intent into well-scoped goals
/// and manages the goal lifecycle (create → approve → dispatch).
/// Uses a persistent SharpCoder session with streaming for real-time interaction.
/// </summary>
public sealed partial class Composer : IClarificationRouter, IAsyncDisposable
{
    private string _model;
    private readonly int _maxContextTokens;
    private readonly int _maxSteps;
    private ReasoningEffort? _reasoningEffort;
    private readonly ILogger<Composer> _logger;
    private readonly IGoalStore _goalStore;
    private readonly IBrainRepoManager? _repoManager;
    private readonly IServiceProvider? _serviceProvider;
    private readonly string _stateDir;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly string? _ollamaApiKey;
    private readonly string? _compactionModel;
    private IChatClient? _chatClient;
    private CodingAgent? _agent;
    private AgentSession _session;

    private readonly HiveConfigFile? _hiveConfig;
    private readonly string _systemPrompt;
    private readonly List<AITool> _composerTools;
    private readonly ConfigRepoManager? _configRepo;
    private readonly KnowledgeGraph? _knowledgeGraph;
    private readonly Func<string, IChatClient>? _chatClientFactory;

    /// <summary>Models the Composer can switch between at runtime.</summary>
    public IReadOnlyList<string> AvailableModels { get; }

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

    /// <summary>Whether context compaction is currently running.</summary>
    public bool IsCompacting { get; private set; }

    /// <summary>Whether context compaction has occurred in the current session.</summary>
    public bool WasCompacted { get; private set; }

    /// <summary>Raised when streaming state changes (new text, completion, error).</summary>
    public event Action? OnStreamingUpdate;

    /// <summary>Raised when context compaction starts.</summary>
    public event Action? OnCompactingStarted;

    /// <summary>Raised when context compaction completes.</summary>
    public event Action? OnCompacted;

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
        - Drill into worker phase output, brain prompts, or worker prompts for Coding, Testing, Review, DocWriting, or Improve (get_phase_output)
        - Create goals as drafts for user review (create_goal)
        - Approve drafts to queue them for execution (approve_goal)
        - Update existing goals (update_goal) — description, priority, scope, repositories, depends_on, and documents can only be changed on Draft goals; status and release can be changed on any goal
        - Delete draft or failed goals (delete_goal)
        - Cancel InProgress or Pending goals (cancel_goal)
        - Inspect repository history (git_log, git_diff, git_show, git_branch, git_blame)
        - List configured repositories (list_repositories)
        - Create and manage releases (create_release, list_releases, update_release)
        - Manage knowledge documents in the knowledge graph (create_document, read_document, update_document, delete_document, search_knowledge, link_document, unlink_document, list_documents, traverse_graph)
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
        - Documentation: only include docwriting in the goal description when the goal explicitly
          requires documentation updates (e.g. "update README", "add changelog entry"). Internal
          refactors, bug fixes, and test additions do NOT need a docwriting phase.
        - Files NOT to change: if certain files must not be modified (e.g. source files for a
          docs-only goal, or docs files for an internal refactor), list them explicitly in the
          description so workers know to leave them untouched.
        - Always call list_goals (or get_goal) to check the current live status of goals before
          making any statement about them — e.g. whether a goal is still in progress, completed,
          or failed. Never rely on previously seen status from earlier in the conversation.

        Goal creation pre-flight checklist — verify every item before calling create_goal:
        - Files & Paths:
          - Every file in "Files to change" exists — verify with glob or read_file
          - Every file in "Files NOT to change" exists
          - No file appears in both lists
        - Repositories:
          - Each listed repository actually contains the files being changed — verify with grep or glob
          - Do not assign a repository unless files in that repo are being modified
        - Code References:
          - Every class, method, field, or property named in the description exists — verify with grep
          - Quoted "current code" snippets match what is actually in the file — verify with read_file
          - Line number references are approximate ("around line X") not exact, since they shift
        - Worker Capabilities:
          - Do not assume workers have access to tools or repos they do not have (e.g. DocWriter cannot access the config repo)
          - If a goal requires config repo AGENTS.md changes, note that the Composer will handle it separately after the goal completes
        - Scope & Sizing:
          - Goal is completable in 1-3 iterations
          - Large file rewrites use the "full file replacement" strategy instruction
          - Dependencies are set if the goal requires another goal's output

        Goal approval policy:
        - Never approve a goal unless the user explicitly requests it.
        - After creating a goal as Draft, inform the user and wait for their approval instruction.
        - Do not batch-approve multiple goals without the user confirming each one.

        Knowledge consultation:
        - At the start of each new conversation, search the knowledge graph for relevant context before making plans or creating goals.
        - At the start of each new conversation, read the `memory-composer-operating-procedures` document via `read_document("memory-composer-operating-procedures")` — it contains persistent conventions you must follow.
        - When making architectural decisions or discussing system behavior, search the knowledge graph for existing decisions, constraints, or memory documents.
        - Use `search_knowledge` with keywords related to the topic at hand (e.g. "composer", "agents", "config", "release", "branch").
        - Prefer `memory` type documents as they capture persistent facts and decisions you should recall.
        - When you create a goal that involves config repo changes, AGENTS.md files, or system prompt modifications, search for the "architecture-composer-vs-workers-config" document first to ensure you follow the established patterns.

        Idea-to-Implementation Document Transition:
        When an idea document is implemented (a goal completing it has been merged):
        1. Create a new `implementation` document in the appropriate topic describing what was actually built, with a `supersedes` link to the original idea, status `active`
        2. Archive the original idea document: set status to `archived`, add a `related` link back to the new implementation doc, keep original content unchanged
        This preserves the decision trail (why we chose what we chose) while giving a clean, accurate implementation doc.
        """;

    private const string ConfigRepoSystemPromptSection = """


        ## Config Repository
        The config repo contains AGENTS.md files that define how each worker role behaves.
        You can read, edit, and commit changes to these files to improve worker behaviour.

        Config repo tools:
        - list_config_files(path?) — list files under the config repo root (or a subdirectory)
        - read_config_file(path, offset?, limit?) — read a config file with line numbers
        - update_agents_md(role, content) — replace the full content of agents/{role}.agents.md
        - edit_agents_md(role, old_string, new_string) — exact string replacement in agents/{role}.agents.md
        - commit_config_changes(message) — stage all changes, commit, and push to the remote

        Valid roles for update_agents_md / edit_agents_md:
        Coder, Tester, Reviewer, Improver, Orchestrator, DocWriter, MergeWorker

        Guidelines for editing AGENTS.md files:
        - Always read the current file before making changes (read_config_file)
        - Make targeted, minimal edits — prefer edit_agents_md over full rewrites
        - Use update_agents_md only when the change is substantial or structural
        - Always commit changes with a clear message describing what was improved and why
        - One commit per logical change — do not bundle unrelated AGENTS.md updates
        """;

    private const string KnowledgeGraphSystemPromptSection = """


        ## Knowledge Graph
        The knowledge graph stores documents as markdown files with YAML frontmatter under knowledge/ in the config repo.
        Use it to capture and retrieve architectural decisions, feature designs, ideas, and persistent facts.

        Knowledge graph tools:
        - create_document(topic, slug, title, type, content, subtopic?, tags?, links?) — create a new knowledge document
        - read_document(document_id) — read a document's full content, links, and metadata
        - update_document(document_id, title?, content?, type?, status?, tags?, append_content?) — update a document
        - delete_document(document_id) — delete a document (warns about incoming links)
        - search_knowledge(query, topic?, type?, status?, tag?, limit?) — full-text search across all documents
        - link_document(document_id, target_id, link_type, description?) — add an outgoing link
        - unlink_document(document_id, target_id, link_type) — remove an outgoing link
        - list_documents(topic?, type?, status?, tag?, limit?) — list documents with optional filters
        - traverse_graph(document_id, depth?, direction?, link_types?) — explore the graph from a starting document

        Document types: implementation, feature, idea, scratch, memory
        Document statuses: draft, active, archived, superseded
        Link types: parent, supersedes, depends_on, implements, related, references

        Guidelines for using the knowledge graph:
        - Use 'memory' type for persistent facts and decisions the LLM should recall
        - Use 'implementation' for documenting existing code or architecture
        - Use 'feature' for planned or in-progress feature designs
        - Use 'idea' for unformed concepts needing exploration
        - Use 'scratch' for working notes or temporary content
        - All mutating operations (create, update, delete, link, unlink) are immediately committed to the config repo
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
        IServiceProvider? serviceProvider = null,
        IHttpClientFactory? httpClientFactory = null,
        string? ollamaApiKey = null,
        HiveConfigFile? hiveConfig = null,
        ConfigRepoManager? configRepo = null,
        IEnumerable<string>? availableModels = null,
        Func<string, IChatClient>? chatClientFactory = null,
        string? compactionModel = null,
        KnowledgeGraph? knowledgeGraph = null)
    {
        _model = model;
        _maxContextTokens = maxContextTokens;
        _maxSteps = maxSteps;
        _logger = logger;
        _goalStore = goalStore;
        _repoManager = repoManager;
        _serviceProvider = serviceProvider;
        _stateDir = stateDir ?? "/app/state";
        _httpClientFactory = httpClientFactory;
        _ollamaApiKey = string.IsNullOrWhiteSpace(ollamaApiKey) ? null : ollamaApiKey;
        _hiveConfig = hiveConfig;
        _configRepo = configRepo;
        _knowledgeGraph = knowledgeGraph;
        _chatClientFactory = chatClientFactory;
        _compactionModel = compactionModel;
        _session = AgentSession.Create("composer");

        AvailableModels = (availableModels?.ToList() ?? [model]).AsReadOnly();

        var (_, _, reasoning) = ChatClientFactory.ParseProviderModelAndReasoning(model);
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

        if (_configRepo is not null)
            _systemPrompt += ConfigRepoSystemPromptSection;

        if (_knowledgeGraph is not null)
            _systemPrompt += KnowledgeGraphSystemPromptSection;

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

        var contextTokens = _session.LastKnownContextTokens > 0
            ? _session.LastKnownContextTokens
            : _session.EstimatedContextTokens;
        var usagePct = _maxContextTokens > 0 ? (int)(contextTokens * 100.0 / _maxContextTokens) : 0;

        return new BrainStats
        {
            Model = _model,
            MessageCount = _session.MessageHistory.Count,
            ContextTokens = contextTokens,
            MaxContextTokens = _maxContextTokens,
            ContextUsagePercent = usagePct,
            CumulativeInputTokens = _session.InputTokensUsed,
            CumulativeOutputTokens = _session.OutputTokensUsed,
            MaxSteps = _maxSteps,
            IsConnected = true,
        };
    }

    /// <summary>
    /// Switches to a different model, disposing the old chat client and recreating the agent.
    /// The session history is preserved.
    /// </summary>
    /// <param name="model">The model identifier to switch to.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="model"/> is not in <see cref="AvailableModels"/>.</exception>
    public async Task SwitchModelAsync(string model)
    {
        if (!AvailableModels.Contains(model, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Model '{model}' is not available. Available models: {string.Join(", ", AvailableModels)}.", nameof(model));

        _logger.LogInformation("Switching Composer model from '{OldModel}' to '{NewModel}'", _model, model);

        // Dispose old client
        if (_chatClient is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (_chatClient is IDisposable disposable)
            disposable.Dispose();

        // Update model and re-parse reasoning effort
        _model = model;
        var (_, _, reasoning) = ChatClientFactory.ParseProviderModelAndReasoning(model);
        _reasoningEffort = reasoning;

        // Create new client
        _chatClient = (_chatClientFactory ?? ChatClientFactory.Create)(model);

        // Rebuild agent — session is preserved
        RecreateAgent();

        _logger.LogInformation("Composer switched to model '{Model}'", _model);
    }

    /// <summary>
    /// Creates the IChatClient and CodingAgent, and loads any persisted session.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Composer connecting with model '{Model}'…", _model);

        _chatClient = (_chatClientFactory ?? ChatClientFactory.Create)(_model);

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
        catch (Exception ex) when (IsContextOverflowError(ex))
        {
            _logger.LogWarning(ex, "Composer context overflow detected — resetting session");
            _streamingContent += "\n\n⚠️ Context limit reached. Session has been reset automatically. Please repeat your request.";
            _session = AgentSession.Create("composer");
            IsCompacting = false;
            WasCompacted = false;
            RecreateAgent();
            var sessionFile = GetSessionFilePath();
            if (File.Exists(sessionFile))
                File.Delete(sessionFile);
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
    /// Returns <c>true</c> if the exception (or any inner exception) represents a context
    /// overflow error from the LLM provider, identified by the
    /// <c>model_max_prompt_tokens_exceeded</c> error code in the message.
    /// </summary>
    /// <param name="ex">The exception to inspect.</param>
    /// <returns><c>true</c> when the exception indicates a context-window overflow.</returns>
    internal static bool IsContextOverflowError(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex.Message.Contains("model_max_prompt_tokens_exceeded", StringComparison.OrdinalIgnoreCase))
                return true;
            ex = ex.InnerException;
        }
        return false;
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
        IsCompacting = false;
        WasCompacted = false;
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
            CompactionClient = !string.IsNullOrEmpty(_compactionModel)
                ? ChatClientFactory.Create(_compactionModel)
                : null,
            OnCompacting = () =>
            {
                IsCompacting = true;
                OnCompactingStarted?.Invoke();
            },
            OnCompacted = r =>
            {
                _logger.LogInformation(
                    "Composer context compaction: {TokensBefore} → {TokensAfter} tokens ({ReductionPercent}% reduction)",
                    r.TokensBefore, r.TokensAfter, r.ReductionPercent);
                IsCompacting = false;
                WasCompacted = true;
                OnCompacted?.Invoke();
            },
        });

        _logger.LogDebug("Composer CodingAgent created with WorkDirectory={WorkDir}, FileOps={FileOps}",
            workDir, _repoManager is not null);
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
                "Get the raw worker output, brain prompt, or worker prompt for a specific phase within an iteration."),
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
            AIFunctionFactory.Create(CreateReleaseAsync, "create_release",
                "Create a new release in Planning status."),
            AIFunctionFactory.Create(ListReleasesAsync, "list_releases",
                "List all releases with their status and goal count."),
            AIFunctionFactory.Create(UpdateReleaseAsync, "update_release",
                "Update a field (tag, notes, or repositories) on a Planning release. Non-Planning releases cannot be edited."),
        };

        if (_ollamaApiKey is not null)
        {
            tools.Add(AIFunctionFactory.Create(WebSearchAsync, "web_search",
                "Search the web for information. Returns titles, URLs, and content snippets."));
            tools.Add(AIFunctionFactory.Create(WebFetchAsync, "web_fetch",
                "Fetch a web page and return its content. Use after web_search to read full pages."));
        }

        if (_configRepo is not null)
        {
            tools.Add(AIFunctionFactory.Create(ListConfigFilesAsync, "list_config_files",
                "List files under the config repo root or a subdirectory. Returns relative paths."));
            tools.Add(AIFunctionFactory.Create(ReadConfigFileAsync, "read_config_file",
                "Read a config repo file with line numbers. Validates that the path stays within the config repo."));
            tools.Add(AIFunctionFactory.Create(UpdateAgentsMdAsync, "update_agents_md",
                "Replace the full content of agents/{role}.agents.md in the config repo."));
            tools.Add(AIFunctionFactory.Create(EditAgentsMdAsync, "edit_agents_md",
                "Perform an exact string replacement in agents/{role}.agents.md in the config repo."));
            tools.Add(AIFunctionFactory.Create(CommitConfigChangesAsync, "commit_config_changes",
                "Stage all changes in the config repo, commit, and push to the remote."));
        }

        if (_knowledgeGraph is not null)
        {
            tools.Add(AIFunctionFactory.Create(CreateDocumentAsync, "create_document",
                "Create a new knowledge document in the config repo."));
            tools.Add(AIFunctionFactory.Create(ReadDocumentAsync, "read_document",
                "Read a knowledge document by ID. Returns full document including title, type, status, tags, links, and body."));
            tools.Add(AIFunctionFactory.Create(UpdateDocumentAsync, "update_document",
                "Update an existing knowledge document. Supports full replace or append mode for content."));
            tools.Add(AIFunctionFactory.Create(DeleteDocumentAsync, "delete_document",
                "Delete a knowledge document. Warns if other documents link to it."));
            tools.Add(AIFunctionFactory.Create(SearchKnowledgeAsync, "search_knowledge",
                "Full-text search across all knowledge documents, with optional filters."));
            tools.Add(AIFunctionFactory.Create(LinkDocumentAsync, "link_document",
                "Add an outgoing link from a document to another. Does not modify the target."));
            tools.Add(AIFunctionFactory.Create(UnlinkDocumentAsync, "unlink_document",
                "Remove an outgoing link from a document."));
            tools.Add(AIFunctionFactory.Create(ListDocumentsAsync, "list_documents",
                "List knowledge documents with optional filters for topic, type, status, and tag."));
            tools.Add(AIFunctionFactory.Create(TraverseGraphAsync, "traverse_graph",
                "Explore the knowledge graph from a starting document, following links up to a given depth."));
        }

        return tools;
    }

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
        pending.Completion.TrySetResult("User cancelled the question without answering.");
    }

    /// <summary>
    /// Attempts to answer a worker's clarification question using the Composer LLM.
    /// If the LLM is confident, returns the answer directly. If the LLM returns
    /// <c>ESCALATE_TO_HUMAN</c> or times out, escalates the request to the human
    /// queue and returns <c>null</c>.
    /// </summary>
    /// <param name="goalId">The goal that triggered the clarification.</param>
    /// <param name="question">The worker's question text.</param>
    /// <param name="context">Additional context about the goal and current state.</param>
    /// <param name="clarificationQueue">The queue service for human escalation.</param>
    /// <param name="request">The clarification request to escalate if needed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The answer string if the Composer is confident; <c>null</c> if escalated to human.</returns>
    public async Task<string?> AnswerClarificationAsync(
        string goalId,
        string question,
        string context,
        ClarificationQueueService clarificationQueue,
        ClarificationRequest request,
        CancellationToken ct = default)
    {
        if (_agent is null)
        {
            _logger.LogWarning("Composer not connected — escalating clarification to human for goal {GoalId}", goalId);
            clarificationQueue.EscalateToHuman(request.Id);
            return null;
        }

        var prompt = $"""
            A worker is blocked and needs clarification. Answer the question if you can.

            **Goal ID:** {goalId}
            **Worker question:** {question}
            **Context:** {context}

            INSTRUCTIONS:
            - If you are confident in the answer, provide it directly as plain text.
            - If you are NOT confident or the question requires human judgment/domain knowledge
              that you cannot determine from the codebase, respond with exactly: ESCALATE_TO_HUMAN
            - Do NOT guess or fabricate information. When in doubt, escalate.
            """;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ClarificationQueueService.ComposerTimeout);

            // Use the agent to get a response via a fresh one-shot session
            // so we don't pollute the main Composer conversation
            var clarificationSession = _session.Fork($"clarification-{request.Id}");
            string responseText = "";

            await foreach (var update in _agent.ExecuteStreamingAsync(clarificationSession, prompt, timeoutCts.Token))
            {
                if (update.Kind == StreamingUpdateKind.TextDelta)
                    responseText += update.Text;
            }

            responseText = responseText.Trim();

            if (string.IsNullOrEmpty(responseText) ||
                responseText == "ESCALATE_TO_HUMAN")
            {
                _logger.LogInformation(
                    "Composer escalating clarification to human for goal {GoalId}: {Question}",
                    goalId, question);
                clarificationQueue.EscalateToHuman(request.Id);
                return null;
            }

            _logger.LogInformation(
                "Composer auto-answered clarification for goal {GoalId}: {Answer}",
                goalId, responseText.Length > 200 ? responseText[..200] + "…" : responseText);

            return responseText;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Composer clarification timed out for goal {GoalId} — escalating to human", goalId);
            clarificationQueue.EscalateToHuman(request.Id);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Composer clarification failed for goal {GoalId} — escalating to human", goalId);
            clarificationQueue.EscalateToHuman(request.Id);
            return null;
        }
    }

    /// <inheritdoc />
    Task<string?> IClarificationRouter.TryAutoAnswerAsync(
        string goalId,
        string question,
        string context,
        ClarificationQueueService clarificationQueue,
        ClarificationRequest request,
        CancellationToken ct) =>
        AnswerClarificationAsync(goalId, question, context, clarificationQueue, request, ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_chatClient is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (_chatClient is IDisposable disposable)
            disposable.Dispose();
    }
}

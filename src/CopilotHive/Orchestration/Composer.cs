using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Services;
using CopilotHive.Shared.AI;

using Microsoft.Extensions.AI;

using SharpCoder;
using SharpCoder.SubAgents;

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
    private readonly ILogger<Composer> _logger;
    private readonly IGoalStore _goalStore;
    private readonly IBrainRepoManager? _repoManager;
    private readonly IServiceProvider? _serviceProvider;
    private readonly string _stateDir;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly string? _ollamaApiKey;
    private readonly ComposerAgentService _agentService;
    private readonly ComposerStreamingService _streamingService;

    /// <summary>
    /// Stored delegate forwarding <see cref="ComposerAgentService.OnSubAgentChanged"/> to
    /// <see cref="OnSubAgentChanged"/>. Held in a field (rather than subscribed as an inline
    /// lambda) so the exact same instance can be detached in <see cref="DisposeAsync"/>.
    /// </summary>
    private readonly Action<SubAgentInfo> _handleSubAgentChanged;

    private readonly HiveConfigFile? _hiveConfig;
    private readonly string _systemPrompt;
    private readonly List<AITool> _composerTools;
    private readonly ConfigRepoManager? _configRepo;
    private readonly KnowledgeGraph? _knowledgeGraph;
    private readonly GoalReviewService? _goalReviewService;

    // Clarification sessions are out of scope for registry tracking — they are short-lived and ephemeral.
    private readonly LlmSessionRegistry? _sessionRegistry;

    private readonly GoalReadyNotifier? _goalReadyNotifier;
    private readonly ComposerAttachmentService? _attachmentService;

    /// <summary>
    /// The explicitly configured reasoning effort (from configuration, not derived from a model
    /// name suffix). When non-null it takes precedence over suffix-parsed values.
    /// </summary>
    private readonly ReasoningEffort? _configuredReasoningEffort;
    private readonly IIssueStore? _issueStore;

    /// <summary>
    /// Serializes the <c>update_issue</c> read-modify-write cycle. <see cref="Issue"/> has no
    /// optimistic-concurrency token and <see cref="IIssueStore.UpdateIssueAsync"/> takes a full
    /// replacement entity, so two overlapping partial updates that change different fields would
    /// otherwise read the same snapshot and the later writer would clobber the earlier writer's
    /// change with stale copied values. The lock is held from the initial read through the
    /// authoritative update and re-fetch, and is always released in a <c>finally</c>.
    /// </summary>
    private readonly SemaphoreSlim _issueUpdateLock = new(1, 1);

    /// <summary>Models the Composer can switch between at runtime.</summary>
    public IReadOnlyList<string> AvailableModels => _agentService.AvailableModels;

    /// <summary>Whether the Composer is currently streaming a response.</summary>
    public bool IsStreaming => _streamingService.IsStreaming;

    /// <summary>The accumulated streaming text (partial response in progress).</summary>
    public string StreamingContent => _streamingService.StreamingContent;

    /// <summary>Tool call count from the last completed response.</summary>
    public int LastToolCalls => _streamingService.LastToolCalls;

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

    /// <summary>
    /// Raised when a background sub-agent starts or reaches a terminal state.
    /// The payload is a defensive clone — mutating it cannot affect tracked state.
    /// </summary>
    public event Action<SubAgentInfo>? OnSubAgentChanged;

    /// <summary>The question currently waiting for a user answer, or <c>null</c> if none.</summary>
    public ComposerQuestion? PendingQuestion { get; private set; }

    /// <summary>Raised when the Composer asks a new question so the UI can re-render.</summary>
    public event Action? OnQuestionAsked;

    private const string DefaultSystemPrompt = """
        You are the Composer — a strategic advisor for the CopilotHive multi-agent system.
        You help the user decompose high-level intent into well-scoped, actionable goals.

        Your capabilities:
        - Read the codebase to understand current state (read_file, glob, grep)
        - Search existing goals to avoid duplication (search_goals, with optional release filter)
        - Browse goal history and status (list_goals, get_goal)
        - Drill into worker phase output, brain prompts, or worker prompts for Coding, Testing, Review, DocWriting, or Improve (get_phase_output)
        - Create goals as drafts for user review (create_goal)
        - Approve drafts to queue them for execution (approve_goal)
        - Update existing goals (update_goal) — description, priority, scope, repositories, depends_on, and documents can only be changed on Draft goals; status and release can be changed on any goal
        - Delete draft or failed goals (delete_goal)
        - Cancel InProgress or Pending goals (cancel_goal)
        - Extend the iteration budget for failed goals that exhausted their max iterations (extend_goal_iterations)
        - Manage issues reported by the user or discovered during execution (create_issue, list_issues, get_issue, update_issue)
        - create_issue — create a new issue when the user reports a bug, code quality problem, suggestion, concern, or workflow issue
        - list_issues — list and filter issues
        - get_issue — get full issue details
        - update_issue — triage or update an issue (change status, severity, type, title, description, or linked goal)
        - Inspect repository history (git_log, git_diff, git_show, git_branch, git_blame, git_fetch)
        - Fetch remote branches (git_fetch(repository, remote?, branch?) — default: origin. Use to access remote feature branches.)
        - List configured repositories (list_repositories)
        - Create and manage releases (create_release, list_releases, get_release, update_release) — get_release retrieves a release's full record including attached goals
        - Manage knowledge documents in the knowledge graph (create_document, read_document, update_document, delete_document, search_knowledge, link_document, unlink_document, list_documents, traverse_graph)
        - Ask the user questions for clarification (ask_user)
        - Get the current date and time (get_current_time)
        - Review draft goals before dispatch (review_goal) — triggers an automated pre-execution review that checks for issues
        - Retrieve recent application log entries for debugging (get_recent_logs) — filters by level, category, or message text
        - When review_goal returns NeedsChanges, read the review document (read_document "review-{goal-id}") and update the goal to address the feedback, then re-review

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

        ## CLEAN CODE PRINCIPLES

        When creating and decomposing goals:
        - Fewer lines of code contain fewer bugs. If a problem needs more and more code, something is wrong in a broader perspective — the design may be incorrect, or a simpler solution was overlooked.
        - Before creating a goal, ask: "Is there a simpler solution that doesn't require adding code?"
        - Prefer deleting code over adding code. Prefer refactoring over patching.
        - If a goal needs 5+ iterations, it's too large — split it into smaller goals.
        - Don't patch symptoms with workarounds, timers, or reconciliation — find the root cause.
        - Each goal should be completable in 1-3 iterations. Split by layer, subsystem, or phase.
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

    private const string SubAgentsSystemPromptSection = """


        ## Sub-Agents
        You can delegate self-contained exploration and verification subtasks to background sub-sessions.
        Only a summary of the sub-session's work returns to you — your own context stays lean.

        Sub-agent tools:
        - start_sub_agent(task, model?, timeout_seconds?, image_paths?) — launch a background sub-session with a self-contained task
        - await_sub_agents() — wait for all running sub-agents to complete and receive their summaries
        - get_sub_agent_status(sub_agent_id) — check the status of a specific sub-agent
        - list_sub_agent_models() — list the models available to sub-agents

        When to use sub-agents:
        - Prefer start_sub_agent over many sequential read/grep/glob calls when you need a thorough digest of a codebase area
        - Use them for verification sweeps (e.g. "check all callers of method X") — the summary returns findings without bloating your context
        - Keep sub-agent prompts self-contained — sub-sessions cannot see your conversation history

        Vision delegation:
        - When a user has attached an image or PDF, delegate visual analysis to a vision-capable sub-agent
        - Use: start_sub_agent(task: "Analyze this attachment and describe what you see", image_paths: ["<attachment path>"])
        - Only the textual summary returns to you — the image content stays in the sub-agent's context
        - Check list_sub_agent_models for models with supports_vision: true

        Sub-agent limitations (read-only):
        - Sub-sessions have file read/grep/glob tools ONLY — no bash, no writes, no composer tools (web search, git, knowledge graph)
        - Sub-sessions are read-only and cannot modify any files
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
        - Progress documents live under the 'progress' topic (document IDs: `progress-{goal-id}`). They are living 'scratch' documents maintained automatically during a goal's execution — use read_document to inspect the Brain's iteration plans, worker narratives, and iteration summaries for a goal.
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
        KnowledgeGraph? knowledgeGraph = null,
        GoalReviewService? goalReviewService = null,
        LlmSessionRegistry? sessionRegistry = null,
        GoalReadyNotifier? goalReadyNotifier = null,
        ComposerAttachmentService? attachmentService = null,
        ReasoningEffort? reasoningEffort = null,
        IIssueStore? issueStore = null)
    {
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
        _goalReviewService = goalReviewService;
        _sessionRegistry = sessionRegistry;
        _goalReadyNotifier = goalReadyNotifier;
        _attachmentService = attachmentService;
        _configuredReasoningEffort = reasoningEffort;
        _issueStore = issueStore;

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

        // Construction-time snapshot: sub-agents need a model catalog and repo file access.
        var subAgentCatalog = _hiveConfig?.GetSubAgentModels() ?? [];
        bool subAgentsEnabled = subAgentCatalog.Count > 0 && _repoManager is not null;

        // Snapshot the catalog now — _hiveConfig is a mutable singleton that config reloads
        // live-update, and the prompt section appended below is fixed for the process lifetime.
        IReadOnlyList<ModelEntry> subAgentModels = subAgentCatalog
            .Select(m => new ModelEntry
            {
                Name = m.Name,
                ContextWindow = m.ContextWindow,
                ReasoningEffort = m.ReasoningEffort,
                Description = m.Description,
                SupportsVision = m.SupportsVision
            })
            .ToList()
            .AsReadOnly();

        if (subAgentsEnabled)
            _systemPrompt += SubAgentsSystemPromptSection;

        _composerTools = BuildComposerTools();

        _agentService = new ComposerAgentService(
            model, maxContextTokens, maxSteps, _configuredReasoningEffort,
            _hiveConfig, _systemPrompt, _composerTools,
            _repoManager, _stateDir,
            compactionModel, _logger,
            chatClientFactory,
            _sessionRegistry,
            (availableModels?.ToList() ?? [model]).AsReadOnly(),
            () =>
            {
                IsCompacting = true;
                OnCompactingStarted?.Invoke();
            },
            r =>
            {
                IsCompacting = false;
                WasCompacted = true;
                OnCompacted?.Invoke();
            },
            subAgentsEnabled,
            subAgentModels,
            attachmentService?.AttachmentsRootPath);

        _handleSubAgentChanged = info => OnSubAgentChanged?.Invoke(info);
        _agentService.OnSubAgentChanged += _handleSubAgentChanged;

        _streamingService = new ComposerStreamingService(
            _agentService,
            _logger,
            SaveSessionAsync,
            RefreshComposerRegistry,
            () => OnStreamingUpdate?.Invoke(),
            () =>
            {
                IsCompacting = false;
                WasCompacted = false;
                var sessionFile = GetSessionFilePath();
                if (File.Exists(sessionFile))
                    File.Delete(sessionFile);
            });
    }

    /// <summary>Whether the Composer has connected and is ready for streaming.</summary>
    public bool IsConnected => _agentService.IsConnected;

    /// <summary>Returns the system prompt used by the Composer.</summary>
    internal string GetSystemPrompt() => _systemPrompt;

    /// <summary>Returns current Composer session statistics.</summary>
    public BrainStats? GetStats()
    {
        if (_agentService.Agent is null) return null;

        var session = _agentService.Session;
        var maxContextTokens = _agentService.MaxContextTokens;
        var contextTokens = session.LastKnownContextTokens > 0
            ? session.LastKnownContextTokens
            : session.EstimatedContextTokens;
        var usagePct = maxContextTokens > 0 ? (int)(contextTokens * 100.0 / maxContextTokens) : 0;

        return new BrainStats
        {
            Model = _agentService.Model,
            MessageCount = session.MessageHistory.Count,
            ContextTokens = contextTokens,
            MaxContextTokens = maxContextTokens,
            ContextUsagePercent = usagePct,
            CumulativeInputTokens = session.InputTokensUsed,
            CumulativeOutputTokens = session.OutputTokensUsed,
            MaxSteps = _agentService.MaxSteps,
            IsConnected = true,
        };
    }

    /// <summary>
    /// Switches to a different model and reasoning effort, disposing the old chat client and
    /// recreating the agent. The session history is preserved.
    /// </summary>
    /// <param name="model">The model identifier to switch to.</param>
    /// <param name="reasoningEffort">The reasoning effort to run with (required).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="model"/> is not in <see cref="AvailableModels"/>.</exception>
    public Task SwitchModelAsync(string model, ReasoningEffort reasoningEffort, CancellationToken ct = default)
        => _agentService.SwitchModelAsync(model, reasoningEffort, ct);

    /// <summary>The Composer's current reasoning effort, or <c>null</c> when unset.</summary>
    public ReasoningEffort? ReasoningEffort => _agentService?.ReasoningEffort;

    /// <summary>
    /// Returns the current background sub-agent entries — running ones first (oldest first), then
    /// the most recent terminal ones. Items are defensive clones and safe to read from the UI.
    /// </summary>
    public IReadOnlyList<SubAgentInfo> GetSubAgents() => _agentService.GetSubAgents();


    /// <summary>
    /// Creates the IChatClient and CodingAgent, and loads any persisted session.
    /// </summary>
    public Task ConnectAsync(CancellationToken ct = default) => _agentService.ConnectAsync(ct);

    /// <summary>
    /// Sends a message and streams the response in the background.
    /// The streaming state is owned by the Composer service and survives component navigation.
    /// Subscribe to <see cref="OnStreamingUpdate"/> to receive updates.
    /// </summary>
    public void SendMessage(string userMessage)
    {
        _streamingService.SendMessage(userMessage);
    }

    /// <summary>
    /// Returns <c>true</c> if the exception (or any inner exception) represents a context
    /// overflow error from the LLM provider, identified by the
    /// <c>model_max_prompt_tokens_exceeded</c> error code in the message.
    /// </summary>
    /// <param name="ex">The exception to inspect.</param>
    /// <returns><c>true</c> when the exception indicates a context-window overflow.</returns>
    internal static bool IsContextOverflowError(Exception? ex) => ComposerStreamingService.IsContextOverflowError(ex);

    /// <summary>
    /// Cancels the current streaming response if one is in progress.
    /// </summary>
    public void CancelStreaming()
    {
        _streamingService.CancelStreaming();
    }

    /// <summary>
    /// Resets the Composer session, clearing all conversation history.
    /// </summary>
    public async Task ResetSessionAsync(CancellationToken ct = default)
    {
        await _agentService.ResetSessionAsync();

        if (_attachmentService is not null)
            await _attachmentService.ClearAllAsync();

        IsCompacting = false;
        WasCompacted = false;

        var sessionFile = GetSessionFilePath();
        if (File.Exists(sessionFile))
        {
            File.Delete(sessionFile);
            _logger.LogInformation("Deleted previous Composer session file");
        }

        _logger.LogInformation("Composer session reset");

        RefreshComposerRegistry(status: "idle", currentTokens: 0);

        await Task.CompletedTask; // keep async signature for future use
    }

    /// <summary>Returns the file path for persisting the Composer session.</summary>
    private string GetSessionFilePath() => Path.Combine(_stateDir, "composer-session.json");

    /// <summary>Refreshes the <c>composer</c> registry entry with the current session tokens and status.</summary>
    private void RefreshComposerRegistry(string status = "idle", long? currentTokens = null)
    {
        _sessionRegistry?.RegisterOrUpdate(new LlmSessionInfo
        {
            SessionId = "composer",
            SessionType = LlmSessionType.Composer,
            Model = _agentService.Model,
            Status = status,
            CurrentTokens = currentTokens ?? _agentService.Session.EstimatedContextTokens,
            MaxTokens = _agentService.MaxContextTokens,
            ReasoningEffort = _agentService?.ReasoningEffort,
        });
    }

    /// <summary>Persists the current Composer session to disk.</summary>
    internal async Task SaveSessionAsync(CancellationToken ct = default)
    {
        var path = GetSessionFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await _agentService.Session.SaveAsync(path, ct);
        _logger.LogDebug("Composer session saved ({Count} messages)", _agentService.Session.MessageHistory.Count);
    }

    /// <summary>
    /// Force-compacts the Composer's conversation history into a summary, reducing token usage.
    /// </summary>
    /// <param name="ct">A token to cancel the compaction operation.</param>
    /// <returns><c>true</c> if compaction occurred; otherwise <c>false</c>.</returns>
    public async Task<bool> CompactSessionAsync(CancellationToken ct = default)
    {
        if (_agentService.Agent is null)
            throw new InvalidOperationException("Composer not connected. Call ConnectAsync first.");

        if (_streamingService.IsStreaming)
            throw new InvalidOperationException("Cannot compact while streaming.");

        IsCompacting = true;
        OnCompactingStarted?.Invoke();

        try
        {
            var compactor = new ContextCompactor(_agentService.AgentOptions.CompactionClient ?? _agentService.ChatClient!, _logger);
            var result = await compactor.ForceCompactAsync(_agentService.Session, _agentService.AgentOptions, ct);
            if (result)
            {
                await SaveSessionAsync(ct);
                RefreshComposerRegistry();
                _logger.LogInformation(
                    "Composer session manually compacted ({Count} messages remaining)",
                    _agentService.Session.MessageHistory.Count);
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Composer manual compaction failed");
            return false;
        }
        finally
        {
            IsCompacting = false;
            OnCompacted?.Invoke();
        }
    }

    /// <summary>
    /// Partially compacts the Composer session by summarizing only the oldest <paramref name="percent"/>%
    /// of the estimated token budget. The newest messages are kept verbatim.
    /// Returns true if compaction was performed, false if there were too few messages.
    /// </summary>
    public async Task<bool> CompactOldestPercentAsync(int percent, CancellationToken ct = default)
    {
        if (_agentService.Agent is null)
            throw new InvalidOperationException("Composer not connected. Call ConnectAsync first.");
        if (_streamingService.IsStreaming)
            throw new InvalidOperationException("Cannot compact while streaming.");

        IsCompacting = true;
        OnCompactingStarted?.Invoke();

        try
        {
            var compactor = new ContextCompactor(
                _agentService.AgentOptions.CompactionClient ?? _agentService.ChatClient!,
                _logger);

            var result = await compactor.CompactOldestPercentAsync(_agentService.Session, _agentService.AgentOptions, percent, ct);

            if (result)
            {
                await SaveSessionAsync(ct);
                RefreshComposerRegistry();
                _logger.LogInformation("Composer session partially compacted ({Percent}% oldest, {Count} messages remaining)",
                    percent, _agentService.Session.MessageHistory.Count);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Composer partial compaction failed");
            return false;
        }
        finally
        {
            IsCompacting = false;
            OnCompacted?.Invoke();
        }
    }

    private async Task RecreateAgentAsync() => await _agentService.RecreateAgentAsync();

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
                "List goals, optionally filtered by status and release. Default release filter is 'unreleased'. Use 'all' for all goals or a release ID for a specific release (selects all releases sharing its tag and status). Output always names the active filter."),
            AIFunctionFactory.Create(SearchGoalsAsync, "search_goals",
                "Search goals by text query across ID, description, and failure reason. Optional release filter: 'unreleased', 'all', or a release ID. When omitted, no release filtering is applied ('all' is equivalent to omitted)."),
            AIFunctionFactory.Create(DeleteGoalAsync, "delete_goal",
                "Permanently delete a goal. Only Draft or Failed goals can be deleted."),
            AIFunctionFactory.Create(CancelGoalAsync, "cancel_goal",
                "Cancel an InProgress or Pending goal, stopping its execution."),
            AIFunctionFactory.Create(ExtendGoalIterationsAsync, "extend_goal_iterations",
                "Extend the iteration budget for a goal that has exhausted or is close to exhausting its max iterations."),
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
            AIFunctionFactory.Create(GitFetchAsync, "git_fetch",
                "Fetch from a remote repository so remote branches and commits are available for inspection."),
            AIFunctionFactory.Create(ListRepositoriesAsync, "list_repositories",
                "List all configured repositories with their names, URLs, and default branches."),
            AIFunctionFactory.Create(CreateReleaseAsync, "create_release",
                "Create a new release in Planning status."),
            AIFunctionFactory.Create(ListReleasesAsync, "list_releases",
                "List all releases with their status and goal count."),
            AIFunctionFactory.Create(GetReleaseAsync, "get_release",
                "Get the full record for a release, including its attached goals."),
            AIFunctionFactory.Create(UpdateReleaseAsync, "update_release",
                "Update a field (tag, notes, or repositories) on a Planning release. Non-Planning releases cannot be edited."),
            AIFunctionFactory.Create(
                () =>
                {
                    var now = DateTime.UtcNow;
                    return System.Text.Json.JsonSerializer.Serialize(new
                    {
                        date = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                        time = now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                        iso = now.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                        timezone = "UTC"
                    });
                },
                "get_current_time",
                "Get the current date and time in UTC. Use when you need to know the current date for changelog entries, release notes, or other date-sensitive content."),
            AIFunctionFactory.Create(ReviewGoalAsync, "review_goal",
                "Trigger a pre-execution review on a Draft goal. Returns the review verdict, issues, and recommendations."),
            AIFunctionFactory.Create(GetRecentLogsAsync, "get_recent_logs",
                "Retrieve recent application log entries. Useful for debugging issues, checking error messages, and monitoring goal dispatch status."),
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

        if (_issueStore is not null)
        {
            tools.Add(AIFunctionFactory.Create(CreateIssueAsync, "create_issue",
                "Create a new issue when the user reports a bug, code quality problem, suggestion, concern, or workflow issue."));
            tools.Add(AIFunctionFactory.Create(ListIssuesAsync, "list_issues",
                "List issues, optionally filtered by status, type, and severity."));
            tools.Add(AIFunctionFactory.Create(GetIssueAsync, "get_issue",
                "Get full details for an issue by ID."));
            tools.Add(AIFunctionFactory.Create(UpdateIssueAsync, "update_issue",
                "Triage or update an issue: change status, severity, type, title, description, or linked goal."));
        }

        return tools;
    }

    /// <summary>
    /// Parses an issue status string into an <see cref="IssueStatus"/>. Null-safe:
    /// null or empty input returns <c>null</c> (no filter). Unknown values throw
    /// <see cref="ArgumentException"/>.
    /// </summary>
    private static IssueStatus? ParseIssueStatus(string? value) =>
        string.IsNullOrEmpty(value) ? null :
        value.ToLowerInvariant().Trim() switch
        {
            "open" => IssueStatus.Open,
            "triaged" => IssueStatus.Triaged,
            "acknowledged" => IssueStatus.Acknowledged,
            "in_progress" or "inprogress" => IssueStatus.InProgress,
            "resolved" => IssueStatus.Resolved,
            "closed" => IssueStatus.Closed,
            _ => throw new ArgumentException($"Unknown status '{value}'"),
        };

    /// <summary>
    /// Presents a question to the user and suspends the streaming loop until an answer is received.
    /// Called by the Composer LLM via the <c>ask_user</c> tool.
    /// </summary>
    [Description("Ask the user a question and wait for their answer. Suspends the response until the user replies.")]
    internal async Task<string> AskUserAsync(
        [Description("The question text to display to the user.")] string question,
        [Description("Question type: YesNo, SingleChoice, or MultiChoice. Default: YesNo")] string type = "YesNo",
        [Description("Array of option strings required for SingleChoice or MultiChoice questions. Ignored for YesNo.")] string[]? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return "❌ question is required.";

        if (!Enum.TryParse<QuestionType>(type, ignoreCase: true, out var questionType))
            return $"❌ Invalid type '{type}'. Valid types: YesNo, SingleChoice, MultiChoice.";

        if (questionType == QuestionType.YesNo)
        {
            var yesNoPending = new ComposerQuestion
            {
                Text = question,
                Type = questionType,
                Options = ["Yes", "No"],
            };

            PendingQuestion = yesNoPending;
            OnQuestionAsked?.Invoke();

            _logger.LogInformation("Composer waiting for user answer to question: {Question}", question);

            try
            {
                return await yesNoPending.Completion.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                PendingQuestion = null;
            }
        }

        if (options is null || options.Length == 0)
            return $"❌ Options are required for {questionType} questions.";

        if (options.Length < 2)
            return $"❌ At least 2 options are required for {questionType} questions; received {options.Length}.";

        if (options.Length > 50)
            return $"❌ At most 50 options are allowed for {questionType} questions; received {options.Length}.";

        var trimmed = new List<string>(options.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
        {
            var t = option?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(t))
                return $"❌ Option entries must be non-blank for {questionType} questions.";

            if (!seen.Add(t))
                return $"❌ Duplicate option '{t}' is not allowed for {questionType} questions.";

            trimmed.Add(t);
        }

        var pending = new ComposerQuestion
        {
            Text = question,
            Type = questionType,
            Options = trimmed,
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
        if (_agentService.Agent is null)
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
            var clarificationSession = _agentService.Session.Fork($"clarification-{request.Id}");
            string responseText = "";

            await foreach (var update in _agentService.Agent.ExecuteStreamingAsync(clarificationSession, prompt, timeoutCts.Token))
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
        // Detach the forwarding delegate before anything is torn down. Removing a handler cannot
        // throw, so this needs no guard, and doing it first guarantees detachment no matter which
        // disposal step below fails.
        _agentService.OnSubAgentChanged -= _handleSubAgentChanged;

        Exception? disposeEx = null;
        try
        {
            await _streamingService.DisposeAsync();
        }
        catch (Exception ex)
        {
            disposeEx = ex;
        }
        finally
        {
            try
            {
                await _agentService.DisposeAsync();
            }
            catch (Exception ex)
            {
                disposeEx = disposeEx is null ? ex : new AggregateException(disposeEx, ex);
            }

            // Independent of the services above — dispose regardless of their outcome.
            _issueUpdateLock.Dispose();
        }

        if (disposeEx is not null)
            throw disposeEx;
    }
}

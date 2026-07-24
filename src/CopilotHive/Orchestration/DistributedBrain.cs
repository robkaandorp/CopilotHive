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

    private bool _disposing;
    private bool _resetting;
    private bool _connected;

    /// <summary>An externally-injected chat client shared across contexts (never owned/disposed by a context).</summary>
    private readonly IChatClient? _injectedChatClient;

    /// <summary>Flows the current goal's Brain context across async calls within a single Brain operation.</summary>
    private readonly AsyncLocal<GoalBrainContext?> _currentContext = new();

    private string _systemPrompt;
    private readonly List<AITool> _brainTools;
    private readonly AgentsManager? _agentsManager;

    /// <summary>Active pipeline snapshots keyed by goal ID, used by the <c>get_goal</c> tool.</summary>
    private Dictionary<string, GoalPipeline>? _activePipelines;

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
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load Brain master session from {File} — starting fresh", masterFile);
                    _masterSession = AgentSession.Create("brain");
                }
            }

            RefreshMasterSessionRegistry();

            _connected = true;
        }
        finally
        {
            _sessionLock.Release();
        }

        _logger.LogInformation("Brain connected (model={Model}, contextWindow={ContextWindow})",
            _modelOverride, _maxContextTokens);
    }

    /// <inheritdoc />
    public async Task UpdateModelAsync(string model, int? maxContextTokens = null, CancellationToken ct = default)
    {
        await _sessionLock.WaitAsync(ct);
        try
        {
            _modelOverride = model;
            if (maxContextTokens.HasValue)
                _maxContextTokens = maxContextTokens.Value;

            var (_, _, reasoning) = ChatClientFactory.ParseProviderModelAndReasoning(model);
            _reasoningEffort = reasoning;

            // Refresh the master session registry entry
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
        _activePipelines ??= [];
        _activePipelines[pipeline.GoalId] = pipeline;
    }

    /// <summary>Removes a pipeline from the active-pipeline registry once a goal completes or fails.</summary>
    public void DeregisterActivePipeline(string goalId)
    {
        _activePipelines?.Remove(goalId);
    }

    /// <summary>Builds the AIFunction tools that the Brain LLM can call.</summary>
    private List<AITool> BuildBrainTools()
    {
        var validPhases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "coding", "testing", "docwriting", "review", "improve", "merging" };
        var tierablePhases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "coding", "testing", "docwriting", "review", "improve" };
        var validTiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "standard", "premium" };

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
                async ([Description("The goal ID to retrieve details for.")] string goal_id) =>
                {
                    if (_goalStore is null)
                        return "Goal store is not available.";

                    var goal = await _goalStore.GetGoalAsync(goal_id);
                    if (goal is null)
                        return $"Goal '{goal_id}' not found.";

                    var pipeline = _activePipelines?.GetValueOrDefault(goal_id);
                    var iterationInfo = pipeline is not null
                        ? $"Current iteration: {pipeline.Iteration}, Phase: {pipeline.Phase}"
                        : "Pipeline not active.";

                    var relatedDocs = goal.Documents.Count > 0
                        ? $"\nRelated docs: {string.Join(", ", goal.Documents.Select(docId =>
                        {
                            var title = _knowledgeGraph?.GetDocument(docId)?.Title;
                            return title is not null ? $"{docId} ({title})" : docId;
                        }))}"
                        : "";

                    return $"""
                        Goal ID: {goal.Id}
                        Description: {goal.Description}
                        Status: {goal.Status}
                        Review Status: {goal.ReviewStatus}
                        Repositories: {string.Join(", ", goal.RepositoryNames)}
                        {iterationInfo}{relatedDocs}
                        """;
                },
                "get_goal",
                "Retrieve goal details (description, status, repositories, iteration info) by goal ID."),
            AIFunctionFactory.Create(
                ([Description("Search terms to look up in the knowledge graph.")] string query,
                 [Description("Optional topic filter (e.g. \"architecture\", \"features\").")] string? topic = null,
                 [Description("Optional document type filter (e.g. \"implementation\", \"feature\").")] string? type = null,
                 [Description("Maximum number of results to return (default 5).")] int? limit = null) =>
                {
                    if (_knowledgeGraph is null)
                        return "Knowledge graph not available.";

                    var results = _knowledgeGraph.Search(query);

                    if (topic is not null)
                        results = results.Where(d => string.Equals(d.Topic, topic, StringComparison.OrdinalIgnoreCase)).ToList();

                    if (type is not null && Enum.TryParse<DocumentType>(type, ignoreCase: true, out var docType))
                        results = results.Where(d => d.Type == docType).ToList();

                    var maxResults = limit ?? 5;
                    results = results.Take(maxResults).ToList();

                    if (results.Count == 0)
                        return "No documents match your query.";

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"Found {results.Count} document{(results.Count == 1 ? "" : "s")}:");
                    for (var i = 0; i < results.Count; i++)
                    {
                        var doc = results[i];
                        sb.AppendLine();
                        sb.AppendLine($"{i + 1}. [{doc.Id}] {doc.Title} ({doc.Type.ToString().ToLowerInvariant()}, {doc.Status.ToString().ToLowerInvariant()})");
                        const int snippetLength = 300;
                        var snippet = doc.Content.Length > snippetLength
                            ? doc.Content[..snippetLength] + "..."
                            : doc.Content;
                        sb.Append($"   {snippet}");
                    }
                    return sb.ToString();
                },
                "search_knowledge",
                "Search the knowledge graph for architecture and design documents by query. Supports optional topic, type, and limit filters."),
            AIFunctionFactory.Create(
                ([Description("The ID of the document to read.")] string document_id) =>
                {
                    if (_knowledgeGraph is null)
                        return "Knowledge graph not available.";

                    var doc = _knowledgeGraph.GetDocument(document_id);
                    if (doc is null)
                        return $"Document '{document_id}' not found.";

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"## {doc.Title}");
                    sb.AppendLine($"- **ID:** {doc.Id}");
                    sb.AppendLine($"- **Type:** {doc.Type}");
                    sb.AppendLine($"- **Status:** {doc.Status}");
                    sb.AppendLine($"- **Topic:** {doc.Topic}" + (doc.Subtopic is not null ? $"/{doc.Subtopic}" : ""));
                    sb.AppendLine($"- **File:** {doc.FilePath}");
                    sb.AppendLine($"- **Author:** {doc.Author}");
                    sb.AppendLine($"- **Created:** {doc.CreatedAt:yyyy-MM-dd}");
                    sb.AppendLine($"- **Updated:** {doc.UpdatedAt:yyyy-MM-dd}");

                    if (doc.Tags.Count > 0)
                        sb.AppendLine($"- **Tags:** {string.Join(", ", doc.Tags)}");

                    if (doc.Links.Count > 0)
                    {
                        sb.AppendLine("- **Links:**");
                        foreach (var link in doc.Links)
                        {
                            var descPart = link.Description is not null ? $" — {link.Description}" : "";
                            sb.AppendLine($"  - [{link.Type}] → {link.TargetId}{descPart}");
                        }
                    }

                    sb.AppendLine();
                    sb.Append(doc.Content);

                    return sb.ToString();
                },
                "read_document",
                "Read a knowledge document by ID. Returns full document including title, type, status, tags, links, and markdown body."),
            AIFunctionFactory.Create(
                ([Description("Starting document ID")] string document_id,
                 [Description("Traversal depth (default 1, max 3)")] int depth = 1,
                 [Description("Direction: 'outgoing' (default), 'incoming', or 'both'")] string direction = "outgoing",
                 [Description("Filter to specific link types (optional array): parent, supersedes, depends_on, implements, related, references")] string[]? link_types = null) =>
                {
                    if (_knowledgeGraph is null)
                        return "Knowledge graph not available.";

                    var startDoc = _knowledgeGraph.GetDocument(document_id);
                    if (startDoc is null)
                        return $"Document '{document_id}' not found.";

                    // Clamp depth to [1, 3]
                    depth = Math.Clamp(depth, 1, 3);

                    var validDirections = new[] { "outgoing", "incoming", "both" };
                    if (!validDirections.Contains(direction, StringComparer.OrdinalIgnoreCase))
                        return $"Invalid direction '{direction}'. Valid values: outgoing, incoming, both.";

                    // Parse optional link type filter from string array
                    HashSet<Knowledge.LinkType>? linkTypeFilter = null;
                    if (link_types is { Length: > 0 })
                    {
                        linkTypeFilter = new HashSet<Knowledge.LinkType>();
                        foreach (var ltStr in link_types)
                        {
                            var normalized = ltStr.Replace("_", "");
                            if (Enum.TryParse<Knowledge.LinkType>(normalized, ignoreCase: true, out var lt))
                                linkTypeFilter.Add(lt);
                        }
                    }

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"## Knowledge Graph: {startDoc.Id}");
                    sb.AppendLine($"**{startDoc.Title}** ({startDoc.Type}, {startDoc.Status})");
                    sb.AppendLine();

                    // BFS traversal
                    var visited = new HashSet<string> { document_id };
                    var queue = new Queue<(string Id, int CurrentDepth)>();
                    queue.Enqueue((document_id, 0));
                    var edges = new List<(string From, string To, Knowledge.LinkType LinkType, string Direction)>();

                    while (queue.Count > 0)
                    {
                        var (currentId, currentDepth) = queue.Dequeue();
                        if (currentDepth >= depth) continue;

                        var currentDoc = _knowledgeGraph.GetDocument(currentId);
                        if (currentDoc is null) continue;

                        // Outgoing links
                        if (direction.Equals("outgoing", StringComparison.OrdinalIgnoreCase) ||
                            direction.Equals("both", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var link in currentDoc.Links)
                            {
                                if (linkTypeFilter is not null && !linkTypeFilter.Contains(link.Type))
                                    continue;

                                edges.Add((currentId, link.TargetId, link.Type, "→"));

                                if (!visited.Contains(link.TargetId))
                                {
                                    visited.Add(link.TargetId);
                                    queue.Enqueue((link.TargetId, currentDepth + 1));
                                }
                            }
                        }

                        // Incoming links (from reverse index via dedicated inverse-type methods)
                        if (direction.Equals("incoming", StringComparison.OrdinalIgnoreCase) ||
                            direction.Equals("both", StringComparison.OrdinalIgnoreCase))
                        {
                            // Combine all incoming docs from all inverse types (including Related and References)
                            var incoming = new List<Knowledge.KnowledgeDocument>();
                            incoming.AddRange(_knowledgeGraph.GetChildren(currentId));
                            incoming.AddRange(_knowledgeGraph.GetSupersededBy(currentId));
                            incoming.AddRange(_knowledgeGraph.GetDependedOnBy(currentId));
                            incoming.AddRange(_knowledgeGraph.GetImplementedBy(currentId));
                            incoming.AddRange(_knowledgeGraph.GetRelatedBy(currentId));
                            incoming.AddRange(_knowledgeGraph.GetReferencedBy(currentId));

                            foreach (var incomingDoc in incoming.DistinctBy(d => d.Id))
                            {
                                foreach (var link in incomingDoc.Links.Where(l => l.TargetId == currentId))
                                {
                                    if (linkTypeFilter is not null && !linkTypeFilter.Contains(link.Type))
                                        continue;

                                    edges.Add((incomingDoc.Id, currentId, link.Type, "←"));

                                    if (!visited.Contains(incomingDoc.Id))
                                    {
                                        visited.Add(incomingDoc.Id);
                                        queue.Enqueue((incomingDoc.Id, currentDepth + 1));
                                    }
                                }
                            }
                        }
                    }

                    if (edges.Count == 0)
                    {
                        sb.AppendLine("No links found in the specified direction and depth.");
                        return sb.ToString().TrimEnd();
                    }

                    sb.AppendLine("### Relationships\n");
                    foreach (var (from, to, lt, dir) in edges)
                    {
                        var toDoc = _knowledgeGraph.GetDocument(to);
                        var toTitle = toDoc is not null ? $" ({toDoc.Title})" : " [not found]";
                        var fromDoc = _knowledgeGraph.GetDocument(from);
                        var fromTitle = fromDoc is not null ? $" ({fromDoc.Title})" : " [not found]";

                        if (dir == "→")
                            sb.AppendLine($"- {from}{fromTitle} **{dir}[{lt}]** {to}{toTitle}");
                        else
                            sb.AppendLine($"- {from}{fromTitle} **{dir}[{lt}]** {to}{toTitle}");
                    }

                    // List all reachable documents (excluding start)
                    var reachable = visited.Where(id => id != document_id).ToList();
                    if (reachable.Count > 0)
                    {
                        sb.AppendLine($"\n### Reachable Documents ({reachable.Count})");
                        foreach (var docId in reachable)
                        {
                            var d = _knowledgeGraph.GetDocument(docId);
                            if (d is not null)
                                sb.AppendLine($"- **{d.Id}** — {d.Title} ({d.Type}, {d.Status})");
                            else
                                sb.AppendLine($"- **{docId}** [not found]");
                        }
                    }

                    return sb.ToString().TrimEnd();
                },
                "traverse_graph",
                "Explore the knowledge graph from a starting document, following links up to a given depth."),
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
            AIFunctionFactory.Create(
                ([Description("Ordered phase names, e.g. [\"coding\",\"testing\",\"docwriting\",\"review\",\"merging\"]")] string[] phases,
                 [Description("JSON object with per-phase instructions.\n  Single-round: {\"coding\": \"...\", \"review\": \"...\"}\n  Multi-round:  {\"coding-1\": \"step 1: revert...\", \"coding-2\": \"step 2: restructure...\", \"review\": \"...\"}")] string phase_instructions,
                 [Description("Why you chose this iteration plan")] string reason,
                 [Description("Optional JSON-encoded dict of phase name to model tier, e.g. {\"coding\":\"premium\"}. Valid phases: coding, testing, docwriting, review, improve. Valid tiers: standard, premium. Omitted phases use the default tier.")] string? model_tiers = null) =>
                {
                    var invalidPhases = phases?.Where(p => !validPhases.Contains(p)).ToList() ?? [];

                    // Validate model_tiers if provided
                    Dictionary<string, string>? parsedTiers = null;
                    List<string> tierErrors = [];
                    if (model_tiers is not null)
                    {
                        try
                        {
                            parsedTiers = JsonSerializer.Deserialize<Dictionary<string, string>>(model_tiers, ProtocolJson.Options);
                        }
                        catch (JsonException)
                        {
                            tierErrors.Add("model_tiers must be valid JSON");
                        }

                        if (parsedTiers is not null)
                        {
                            var invalidTierPhases = parsedTiers.Keys.Where(k => !tierablePhases.Contains(k)).ToList();
                            if (invalidTierPhases.Count > 0)
                                tierErrors.Add($"invalid phase names in model_tiers: {string.Join(", ", invalidTierPhases)}. Valid: {string.Join(", ", tierablePhases)}");

                            var invalidTierValues = parsedTiers.Values.Where(v => !validTiers.Contains(v)).ToList();
                            if (invalidTierValues.Count > 0)
                                tierErrors.Add($"invalid tier values in model_tiers: {string.Join(", ", invalidTierValues)}. Valid: {string.Join(", ", validTiers)}");
                        }
                    }

                    var error = Shared.ToolValidation.Check(
                        (phases is { Length: > 0 }, "phases must be a non-empty array"),
                        (invalidPhases.Count == 0,
                            $"invalid phase names: {string.Join(", ", invalidPhases)}. Valid: {string.Join(", ", validPhases)}"),
                        (!string.IsNullOrEmpty(reason), "reason is required"),
                        (tierErrors.Count == 0, string.Join("; ", tierErrors)));
                    if (error is not null) return error;

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
        await _sessionLock.WaitAsync(ct);
        try
        {
            if (_disposing)
                throw new InvalidOperationException("Brain is being disposed.");
            if (_resetting)
                throw new InvalidOperationException("Brain is being reset.");
            if (_deletingGoals.ContainsKey(goalId))
                throw new InvalidOperationException($"Goal '{goalId}' is being deleted.");

            // Idempotent: already have a context for this goal.
            if (_goalContexts.ContainsKey(goalId))
                return;

            var goalSession = _masterSession.Fork($"brain-goal-{goalId}");
            var context = await CreateGoalBrainContextAsync(goalId, goalSession, ct);
            _goalContexts[goalId] = context;

            _logger.LogInformation("Forked master session for goal '{GoalId}' ({Messages} messages)",
                goalId, goalSession.MessageHistory.Count);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>Loads (or forks) an existing goal session from disk and creates its Brain context.</summary>
    /// <inheritdoc />
    public async Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default)
    {
        await _sessionLock.WaitAsync(ct);
        try
        {
            if (_disposing)
                throw new InvalidOperationException("Brain is being disposed.");
            if (_resetting)
                throw new InvalidOperationException("Brain is being reset.");
            if (_deletingGoals.ContainsKey(goalId))
                throw new InvalidOperationException($"Goal '{goalId}' is being deleted.");

            // Idempotent: already have a context for this goal.
            if (_goalContexts.ContainsKey(goalId))
                return;

            var goalSessionFile = GetGoalSessionFilePath(goalId);
            AgentSession session;
            if (File.Exists(goalSessionFile))
            {
                try
                {
                    session = await AgentSession.LoadAsync(goalSessionFile, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to load existing goal session for '{GoalId}' — forking from master", goalId);
                    session = _masterSession.Fork($"brain-goal-{goalId}");
                }
            }
            else
            {
                session = _masterSession.Fork($"brain-goal-{goalId}");
            }

            var context = await CreateGoalBrainContextAsync(goalId, session, ct);
            _goalContexts[goalId] = context;

            _logger.LogInformation("Registered existing Brain context for goal '{GoalId}' ({Messages} messages)",
                goalId, session.MessageHistory.Count);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>Deletes the persisted goal session file and disposes the goal's Brain context.</summary>
    /// <inheritdoc />
    public async Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default)
    {
        await _sessionLock.WaitAsync(ct);
        GoalBrainContext? context;
        try
        {
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
    }

    /// <inheritdoc/>
    public async Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instructions)) return;

        await _sessionLock.WaitAsync(ct);
        try
        {
            _systemPrompt = $"{DefaultSystemPrompt}\n\n{instructions}";
        }
        finally { _sessionLock.Release(); }

        _logger.LogInformation("Updated Brain system prompt with new orchestrator instructions ({Chars} chars)",
            instructions.Length);
    }

    /// <summary>Asks the Brain to plan which phases should run during the current iteration.</summary>
    public async Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default)
    {
        var prompt = BrainPromptBuilder.BuildPlanningPrompt(pipeline, additionalContext);

        if (!_connected)
        {
            _logger.LogWarning("Brain not connected — using default iteration plan for goal {GoalId}", pipeline.GoalId);
            return PlanResult.Success(IterationPlan.Default());
        }

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

        if (!_goalContexts.TryGetValue(pipeline.GoalId, out var context) || !context.TryAcquire())
            throw new InvalidOperationException($"No Brain context for goal '{pipeline.GoalId}'.");

        string summary;
        try
        {
            await context.Gate.WaitAsync(ct);
            try
            {
                _currentContext.Value = context;
                context.LastToolCallResult = null;
                context.ActiveCallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                context.ActiveCallCts.CancelAfter(TimeSpan.FromMinutes(Constants.TaskTimeoutMinutes));

                _sessionRegistry?.RegisterOrUpdate(new LlmSessionInfo
                {
                    SessionId = $"brain-goal-{pipeline.GoalId}",
                    SessionType = LlmSessionType.BrainGoal,
                    GoalId = pipeline.GoalId,
                    Model = context.Model,
                    Status = "summarizing",
                    CurrentTokens = context.Session.EstimatedContextTokens,
                    MaxTokens = context.MaxContextTokens,
                });

                var prompt = BrainPromptBuilder.BuildSummarizePrompt(pipeline);
                var result = await context.Agent.ExecuteAsync(context.Session, prompt, context.ActiveCallCts.Token);
                summary = result.Message?.Trim() ?? $"Goal '{pipeline.GoalId}' completed.";

                try { await context.Session.SaveAsync(GetGoalSessionFilePath(pipeline.GoalId), ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to save Brain session"); }
            }
            finally
            {
                _currentContext.Value = null;
                context.ActiveCallCts?.Dispose();
                context.ActiveCallCts = null;
                _sessionRegistry?.RegisterOrUpdate(new LlmSessionInfo
                {
                    SessionId = $"brain-goal-{pipeline.GoalId}",
                    SessionType = LlmSessionType.BrainGoal,
                    GoalId = pipeline.GoalId,
                    Model = context.Model,
                    Status = "idle",
                    CurrentTokens = context.Session.EstimatedContextTokens,
                    MaxTokens = context.MaxContextTokens,
                });
                context.Gate.Release();
            }
        }
        finally { context.Release(); }

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

        await DeleteGoalSessionAsync(pipeline.GoalId, ct);

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
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, phase, additionalContext);

        if (!_connected)
        {
            _logger.LogWarning("Brain not connected — using fallback prompt for {GoalId} phase {Phase}",
                pipeline.GoalId, phase);
            return phase == GoalPhase.Review
                ? PromptResult.Success(BrainPromptBuilder.BuildReviewFallbackPrompt(pipeline, additionalContext))
                : PromptResult.Success($"Work on: {pipeline.Description}");
        }

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
        if (!_connected)
        {
            _logger.LogWarning(
                "Brain not connected — returning direct fallback answer for question in goal {GoalId}", goalId);
            return BrainResponse.Answer("Brain is not available. Please proceed with your best judgment.");
        }

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

        if (!_goalContexts.TryGetValue(goalId, out var context) || !context.TryAcquire())
            throw new InvalidOperationException($"No Brain context for goal '{goalId}'.");

        try
        {
            await context.Gate.WaitAsync(ct);
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
                    CurrentTokens = context.Session.EstimatedContextTokens,
                    MaxTokens = context.MaxContextTokens,
                });

                var result = await context.Agent.ExecuteAsync(context.Session, prompt, context.ActiveCallCts.Token);
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
                var estimatedTokens = context.Session.EstimatedContextTokens;
                var usagePct = context.MaxContextTokens > 0 ? (int)(estimatedTokens * 100.0 / context.MaxContextTokens) : 0;
                _logger.LogInformation(
                    "Brain context: messages={Messages} ~tokens={EstTokens}/{Limit} ({Pct}%) cumIn={CumIn} cumOut={CumOut}",
                    context.Session.MessageHistory.Count, estimatedTokens, context.MaxContextTokens,
                    usagePct, context.Session.InputTokensUsed, context.Session.OutputTokensUsed);

                var contextTokens = context.Session.LastKnownContextTokens > 0
                    ? context.Session.LastKnownContextTokens
                    : context.Session.EstimatedContextTokens;
                _logger.LogInformation("{Message}", FormatContextUsageMessage(contextTokens, context.MaxContextTokens, callerName));

                _logger.LogDebug("Brain response ({Length} chars), tool={Tool}",
                    responseText?.Length ?? 0, context.LastToolCallResult?.ToolName ?? "none");

                // Auto-save session after each Brain call
                try { await context.Session.SaveAsync(GetGoalSessionFilePath(goalId), ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to save Brain session"); }

                return (responseText, context.LastToolCallResult);
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
                    CurrentTokens = context.Session.EstimatedContextTokens,
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
    }

    private void EnsureConnected()
    {
        if (!_connected)
            throw new InvalidOperationException("Brain not connected. Call ConnectAsync first.");
    }

    /// <summary>Drains and disposes all goal contexts and the shared injected chat client.</summary>
    public async ValueTask DisposeAsync()
    {
        _disposing = true;

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

        if (_injectedChatClient is not null)
        {
            try { _injectedChatClient.Dispose(); } catch { }
        }

        _sessionLock.Dispose();
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

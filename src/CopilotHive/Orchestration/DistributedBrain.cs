using System.ComponentModel;
using System.Text.Json;
using CopilotHive.Agents;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Metrics;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.AI;
using SharpCoder;

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
    private readonly string _modelOverride;
    private readonly int _maxContextTokens;
    private readonly int _maxSteps;
    private readonly ReasoningEffort? _reasoningEffort;
    private readonly ILogger<DistributedBrain> _logger;
    private readonly MetricsTracker? _metricsTracker;
    private readonly IBrainRepoManager? _repoManager;
    private readonly IGoalStore? _goalStore;
    private readonly string _stateDir;
    private readonly string? _compactionModel;
    private readonly KnowledgeGraph? _knowledgeGraph;

    /// <summary>
    /// Directory used for persistent Brain state (session files).
    /// </summary>
    public string StateDirectory => _stateDir;

    private IChatClient? _chatClient;
    private CodingAgent? _agent;
    private AgentSession _masterSession;
    private AgentSession _session;
    private string? _activeGoalId;

    private string _systemPrompt;
    private readonly List<AITool> _brainTools;
    private readonly AgentsManager? _agentsManager;

    /// <summary>Active pipeline snapshots keyed by goal ID, used by the <c>get_goal</c> tool.</summary>
    private Dictionary<string, GoalPipeline>? _activePipelines;

    /// <summary>Serialises all Brain LLM calls so <see cref="_lastToolCallResult"/> is never overwritten concurrently.</summary>
    private readonly SemaphoreSlim _brainCallGate = new(1, 1);

    /// <summary>Last tool call result captured by Brain tool lambdas. Serialised by <see cref="_brainCallGate"/>.</summary>
    private volatile BrainToolCallResult? _lastToolCallResult;

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
        KnowledgeGraph? knowledgeGraph = null)
    {
        _modelOverride = modelOverride;
        _maxContextTokens = maxContextTokens;
        _maxSteps = maxSteps;
        _logger = logger;
        _metricsTracker = metricsTracker;
        _repoManager = repoManager;
        _agentsManager = agentsManager;
        _goalStore = goalStore;
        _chatClient = chatClient;
        _stateDir = stateDir ?? "/app/state";
        _compactionModel = compactionModel;
        _knowledgeGraph = knowledgeGraph;
        _masterSession = AgentSession.Create("brain");
        _session = _masterSession;

        var (_, _, reasoning) = SDK.ChatClientFactory.ParseProviderModelAndReasoning(modelOverride);
        _reasoningEffort = reasoning;

        _brainTools = BuildBrainTools();

        var orchestratorInstructions = agentsManager?.GetAgentsMd(WorkerRole.Orchestrator) ?? "";
        _systemPrompt = string.IsNullOrWhiteSpace(orchestratorInstructions)
            ? DefaultSystemPrompt
            : $"{DefaultSystemPrompt}\n\n{orchestratorInstructions}";
    }

    /// <summary>Creates the IChatClient and CodingAgent. Also loads a previously saved session.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Brain connecting with model '{Model}'…", _modelOverride);

        _chatClient ??= SDK.ChatClientFactory.Create(_modelOverride);

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
        _session = _masterSession;

        RecreateAgent();

        _logger.LogInformation("Brain connected via CodingAgent (model={Model}, contextWindow={ContextWindow})",
            _modelOverride, _maxContextTokens);
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
                    _lastToolCallResult = new EscalateResult(question, reason);
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

                    _lastToolCallResult = new IterationPlanResult(phases ?? [], phase_instructions, reason, model_tiers);
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

    /// <summary>Creates a CodingAgent with current configuration.</summary>
    private void RecreateAgent()
    {
        if (_chatClient is null)
            throw new InvalidOperationException("Brain not connected. Call ConnectAsync first.");

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
            CustomTools = _brainTools,
            MaxContextTokens = _maxContextTokens,
            EnableAutoCompaction = true,
            AutoLoadWorkspaceInstructions = false,
            ReasoningEffort = _reasoningEffort,
            Logger = _logger,
            CompactionClient = !string.IsNullOrEmpty(_compactionModel)
                ? CopilotHive.SDK.ChatClientFactory.Create(_compactionModel)
                : null,
            OnCompacted = r =>
            {
                _logger.LogInformation(
                    "Brain context compaction: {TokensBefore} \u2192 {TokensAfter} tokens ({ReductionPercent}% reduction), {MessagesBefore} \u2192 {MessagesAfter} messages",
                    r.TokensBefore, r.TokensAfter, r.ReductionPercent, r.MessagesBefore, r.MessagesAfter);

                // Re-inject orchestrator instructions after compaction so they survive summarization
                var instructions = _agentsManager?.GetAgentsMd(WorkerRole.Orchestrator);
                if (!string.IsNullOrWhiteSpace(instructions))
                {
                    _masterSession.MessageHistory.Add(new ChatMessage(ChatRole.User,
                        $"ORCHESTRATOR INSTRUCTIONS (re-injected after context compaction):\n\n{instructions}"));
                    _masterSession.MessageHistory.Add(new ChatMessage(ChatRole.Assistant,
                        "Acknowledged. Orchestrator instructions refreshed."));
                    _logger.LogInformation("Re-injected orchestrator instructions after compaction");
                }
            },
        });

        _logger.LogDebug("CodingAgent created with WorkDirectory={WorkDir}, FileOps={FileOps}",
            workDir, _repoManager is not null);
    }

    /// <summary>Returns the file path for a goal-specific forked session.</summary>
    private string GetGoalSessionFilePath(string goalId)
        => Path.Combine(_stateDir, $"brain-goal-{goalId}.json");

    /// <summary>Returns the file path for the master Brain session.</summary>
    private string GetMasterSessionFilePath() => Path.Combine(_stateDir, "brain-master.json");

    /// <summary>Forks the master session for a goal and persists it to disk.</summary>
    /// <inheritdoc />
    public async Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default)
    {
        var goalSession = _masterSession.Fork($"brain-goal-{goalId}");
        var goalSessionFile = GetGoalSessionFilePath(goalId);
        await goalSession.SaveAsync(goalSessionFile, ct);
        _logger.LogInformation("Forked master session for goal '{GoalId}' ({Messages} messages)",
            goalId, goalSession.MessageHistory.Count);
    }

    /// <summary>Loads the goal-specific session, saving the current session first if switching goals.</summary>
    private async Task LoadGoalSessionAsync(string goalId, CancellationToken ct)
    {
        // Idempotent: already loaded for this goal
        if (_activeGoalId == goalId && _session != null)
            return;

        if (_activeGoalId != null && _activeGoalId != goalId)
            await SaveCurrentSessionAsync(ct);

        var goalSessionFile = GetGoalSessionFilePath(goalId);
        if (File.Exists(goalSessionFile))
            _session = await AgentSession.LoadAsync(goalSessionFile, ct);
        else
            _session = _masterSession.Fork($"brain-goal-{goalId}");

        _activeGoalId = goalId;
        RecreateAgent();
    }

    /// <summary>Persists the currently active goal session to disk. No-op if no goal is active.</summary>
    private async Task SaveCurrentSessionAsync(CancellationToken ct)
    {
        if (_activeGoalId == null) return;
        var goalSessionFile = GetGoalSessionFilePath(_activeGoalId);
        await _session.SaveAsync(goalSessionFile, ct);
    }

    /// <summary>Deletes the persisted goal session file after goal completion or failure.</summary>
    /// <inheritdoc />
    public void DeleteGoalSession(string goalId)
    {
        var goalSessionFile = GetGoalSessionFilePath(goalId);
        if (File.Exists(goalSessionFile))
        {
            File.Delete(goalSessionFile);
            _logger.LogInformation("Deleted goal session for '{GoalId}'", goalId);
        }
        // Clear active state if this was the currently-loaded goal
        if (_activeGoalId == goalId)
        {
            _activeGoalId = null;
            _session = _masterSession;
            RecreateAgent();
            _logger.LogInformation("Cleared active goal session for '{GoalId}'", goalId);
        }
    }

    /// <inheritdoc />
    public bool GoalSessionExists(string goalId) =>
        File.Exists(GetGoalSessionFilePath(goalId));

    /// <summary>Persists the master Brain session to disk.</summary>
    internal async Task SaveSessionAsync(CancellationToken ct = default)
    {
        var path = GetMasterSessionFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await _masterSession.SaveAsync(path, ct);
        _logger.LogDebug("Brain master session saved ({Count} messages)", _masterSession.MessageHistory.Count);
    }

    /// <inheritdoc/>
    public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct)
    {
        pipeline.Conversation.Add(new ConversationEntry("system", note, pipeline.Iteration, "plan-adjustment"));

        // Also inject directly into the Brain's master session so the note is included
        // when the Brain crafts the next prompt. Adding as a user message followed by
        // an assistant acknowledgement keeps the conversation in a valid turn sequence.
        _masterSession.MessageHistory.Add(new ChatMessage(ChatRole.User,
            $"SYSTEM NOTE (plan adjustment for goal {pipeline.GoalId}):\n\n{note}"));
        _masterSession.MessageHistory.Add(new ChatMessage(ChatRole.Assistant,
            "Acknowledged. I have noted the plan adjustment and will craft prompts for all phases in the final plan."));

        _logger.LogInformation("Injected plan adjustment note for goal {GoalId}: {Note}", pipeline.GoalId, note);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instructions)) return;

        _masterSession.MessageHistory.Add(new ChatMessage(ChatRole.User,
            $"ORCHESTRATOR INSTRUCTIONS UPDATE:\n\n{instructions}"));
        _masterSession.MessageHistory.Add(new ChatMessage(ChatRole.Assistant,
            "Acknowledged. I will follow the updated orchestrator instructions for all future goals."));

        _logger.LogInformation("Injected orchestrator instructions into Brain session ({Chars} chars)",
            instructions.Length);

        await SaveSessionAsync(ct);
    }

    /// <summary>Asks the Brain to plan which phases should run during the current iteration.</summary>
    public async Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default)
    {
        var prompt = BrainPromptBuilder.BuildPlanningPrompt(pipeline, additionalContext);

        if (_agent is null)
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
        await _brainCallGate.WaitAsync(ct);
        try
        {
            await LoadGoalSessionAsync(pipeline.GoalId, ct);

            var prompt = BrainPromptBuilder.BuildSummarizePrompt(pipeline);

            string summary;
            if (_agent is not null)
            {
                var result = await _agent.ExecuteAsync(_session, prompt, ct);
                summary = result.Message?.Trim() ?? $"Goal '{pipeline.GoalId}' completed.";
            }
            else
            {
                summary = $"Goal '{pipeline.GoalId}' completed ({pipeline.Iteration} iteration(s)).";
            }

            await SaveCurrentSessionAsync(ct);

            // Append summary to master session
            _masterSession.MessageHistory.Add(new ChatMessage(ChatRole.User,
                $"[Goal completed: {pipeline.GoalId}] Summarize what was done."));
            _masterSession.MessageHistory.Add(new ChatMessage(ChatRole.Assistant, summary));
            await SaveSessionAsync(ct);

            // Clean up goal session
            DeleteGoalSession(pipeline.GoalId);
            _activeGoalId = null;

            _logger.LogInformation("Merged summary for goal '{GoalId}' into master session: {Summary}",
                pipeline.GoalId, BrainPromptBuilder.Truncate(summary, 200));

            return summary;
        }
        finally
        {
            _brainCallGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default)
    {
        if (_agent is null)
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
        var prompt = BrainPromptBuilder.BuildCraftPromptText(pipeline, phase, additionalContext);

        if (_agent is null)
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
        if (_agent is null)
        {
            _logger.LogWarning(
                "Brain not connected — returning direct fallback answer for question in goal {GoalId}", goalId);
            return BrainResponse.Answer("Brain is not available. Please proceed with your best judgment.");
        }

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
        string prompt, string? goalId, CancellationToken ct,
        [System.Runtime.CompilerServices.CallerMemberName] string callerName = "")
    {
        EnsureConnected();

        await _brainCallGate.WaitAsync(ct);
        try
        {
            _lastToolCallResult = null;

            // Load the goal session for this call if a goalId is provided
            if (!string.IsNullOrEmpty(goalId))
                await LoadGoalSessionAsync(goalId, ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(Constants.TaskTimeoutMinutes));

            var result = await _agent!.ExecuteAsync(_session, prompt, cts.Token);
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
            var estimatedTokens = _session.EstimatedContextTokens;
            var usagePct = _maxContextTokens > 0 ? (int)(estimatedTokens * 100.0 / _maxContextTokens) : 0;
            _logger.LogInformation(
                "Brain context: messages={Messages} ~tokens={EstTokens}/{Limit} ({Pct}%) cumIn={CumIn} cumOut={CumOut}",
                _session.MessageHistory.Count, estimatedTokens, _maxContextTokens,
                usagePct, _session.InputTokensUsed, _session.OutputTokensUsed);

            _logger.LogInformation("{Message}", FormatContextUsageMessage(_session.InputTokensUsed, _maxContextTokens, callerName));

            _logger.LogDebug("Brain response ({Length} chars), tool={Tool}",
                responseText.Length, _lastToolCallResult?.ToolName ?? "none");

            // Auto-save session after each Brain call
            try { await SaveCurrentSessionAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to save Brain session"); }

            return (responseText, _lastToolCallResult);
        }
        finally
        {
            _brainCallGate.Release();
        }
    }

    /// <inheritdoc />
    public BrainStats? GetStats()
    {
        if (_agent is null) return null;

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
        await _brainCallGate.WaitAsync(ct);
        try
        {
            // Re-read orchestrator instructions from disk so the system prompt
            // reflects the latest orchestrator.agents.md content.
            var freshInstructions = _agentsManager?.GetAgentsMd(WorkerRole.Orchestrator) ?? "";
            _systemPrompt = string.IsNullOrWhiteSpace(freshInstructions)
                ? DefaultSystemPrompt
                : $"{DefaultSystemPrompt}\n\n{freshInstructions}";

            _masterSession = AgentSession.Create("brain");
            _session = _masterSession;
            _activeGoalId = null;
            RecreateAgent();

            var sessionFile = GetMasterSessionFilePath();
            if (File.Exists(sessionFile))
                File.Delete(sessionFile);

            _logger.LogInformation("Brain session reset — conversation history cleared, orchestrator instructions reloaded from disk, and session file deleted.");
        }
        finally
        {
            _brainCallGate.Release();
        }
    }

    private void EnsureConnected()
    {
        if (_agent is null)
            throw new InvalidOperationException("Brain not connected. Call ConnectAsync first.");
    }

    /// <summary>Saves the session and disposes the underlying chat client.</summary>
    public async ValueTask DisposeAsync()
    {
        try { await SaveSessionAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to save Brain session on dispose"); }

        _agent = null;
        _chatClient?.Dispose();
        _brainCallGate.Dispose();
    }
}

using System.ComponentModel;
using System.Text.Json;

using CopilotHive.Configuration;
using CopilotHive.Knowledge;
using CopilotHive.Goals;
using CopilotHive.Services;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Orchestration;

/// <summary>
/// Shared factory for the Brain tools that depend only on external services
/// (goal store, pipeline lookup and knowledge graph). Both <see cref="DistributedBrain"/>
/// and the goal brain actors build these from the same source so tool behaviour stays identical.
/// </summary>
internal static class BrainTools
{
    /// <summary>
    /// Strips an occurrence suffix (e.g. "-1", "-2") from a phase name, leaving base names like "coding" unchanged.
    /// Only positive numeric suffixes are removed; "-0", non-digit suffixes, overflow and malformed values are preserved.
    /// </summary>
    internal static string StripOccurrenceSuffix(string? phase)
    {
        if (string.IsNullOrEmpty(phase)) return phase ?? "";
        var dashIndex = phase.IndexOf('-');
        if (dashIndex <= 0 || dashIndex >= phase.Length - 1) return phase;
        var suffix = phase[(dashIndex + 1)..];
        if (suffix.Length > 0 && suffix.All(c => c >= '0' && c <= '9') && int.TryParse(suffix, out var n) && n > 0)
            return phase[..dashIndex];
        return phase;
    }

    /// <summary>The phases a worker can run, and therefore the only keys allowed in <c>model_tiers</c>.</summary>
    private static readonly HashSet<string> TierablePhases =
        new(StringComparer.OrdinalIgnoreCase) { "coding", "testing", "docwriting", "review", "improve" };

    /// <summary>Human-readable, order-stable listing of the tierable keys used in rejection texts.</summary>
    private const string TierableKeyList = "coding, testing, docwriting, review, improve";

    /// <summary>
    /// Builds the actionable rejection text for <c>model_tiers</c> keys that are not tierable
    /// worker phases, or returns <c>null</c> when every key is tierable.
    /// </summary>
    /// <remarks>
    /// <c>merging</c> gets its own message because — unlike a typo or a lifecycle name — it IS a
    /// valid plan phase, just not a tierable one. Any other key (including a mixed set that also
    /// contains <c>merging</c>) gets the generic listing, which never claims an unknown key is a
    /// valid plan phase.
    /// </remarks>
    /// <param name="invalidKeys">The non-tierable keys, in submission order.</param>
    internal static string? BuildInvalidTierKeyRejection(IReadOnlyList<string> invalidKeys)
    {
        if (invalidKeys.Count == 0)
            return null;

        var mergingOnly = invalidKeys.All(k =>
            string.Equals(StripOccurrenceSuffix(k), "merging", StringComparison.OrdinalIgnoreCase));

        return mergingOnly
            ? "Merging is a valid plan phase but NOT a tierable worker phase — remove 'merging' from "
              + $"model_tiers and keep it in `phases`. Tierable keys are: {TierableKeyList}."
            : $"The following model_tiers keys are not tierable worker phases: {string.Join(", ", invalidKeys)}. "
              + $"Tierable keys are: {TierableKeyList}.";
    }

    /// <summary>
    /// Separates non-tierable <c>model_tiers</c> keys (e.g. <c>merging</c>) from the rest so the
    /// caller can surface them as a bounded-replan-loop rejection reason instead of a hard failure.
    /// </summary>
    /// <param name="modelTiers">The raw <c>model_tiers</c> JSON, or null when omitted.</param>
    /// <returns>
    /// <c>Rejection</c> — the actionable rejection text when non-tierable keys are present, else null.
    /// <c>RemainingTiers</c> — the <c>model_tiers</c> JSON with the non-tierable keys removed, so the
    /// remaining structural checks (e.g. tier values) still apply. Malformed JSON is returned
    /// unchanged with no rejection: that is a separate structural error owned by
    /// <see cref="ValidateIterationPlan"/>.
    /// </returns>
    internal static (string? Rejection, string? RemainingTiers) CarveOutNonTierableKeys(string? modelTiers)
    {
        if (modelTiers is null)
            return (null, null);

        Dictionary<string, string>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(modelTiers, ProtocolJson.Options);
        }
        catch (JsonException)
        {
            return (null, modelTiers);
        }

        if (parsed is null)
            return (null, modelTiers);

        var invalidKeys = parsed.Keys.Where(k => !TierablePhases.Contains(StripOccurrenceSuffix(k))).ToList();
        if (invalidKeys.Count == 0)
            return (null, modelTiers);

        var remaining = parsed.Where(kv => !invalidKeys.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return (BuildInvalidTierKeyRejection(invalidKeys), JsonSerializer.Serialize(remaining, ProtocolJson.Options));
    }

    /// <summary>
    /// Validates the structural parts of an iteration plan reported by the Brain LLM
    /// (non-empty phases array, reason and optional model tiers).
    /// Phase-name membership is deliberately NOT checked here: unrecognized phase names are
    /// surfaced through <see cref="Services.IterationPlan.UnrecognizedPhases"/> and rejected
    /// inside the bounded replan loop so the Brain gets an actionable "fix and resubmit" reason
    /// instead of a hard failure during plan mapping.
    /// </summary>
    /// <returns><c>(true, null)</c> when valid, otherwise <c>(false, error)</c>.</returns>
    internal static (bool Valid, string? Error) ValidateIterationPlan(
        string[] phases, string phaseInstructions, string reason, string? modelTiers)
    {
        var validTiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "standard", "premium" };

        // Validate model_tiers if provided
        Dictionary<string, string>? parsedTiers = null;
        List<string> tierErrors = [];
        if (modelTiers is not null)
        {
            try
            {
                parsedTiers = JsonSerializer.Deserialize<Dictionary<string, string>>(modelTiers, ProtocolJson.Options);
            }
            catch (JsonException)
            {
                tierErrors.Add("model_tiers must be valid JSON");
            }

            if (parsedTiers is not null)
            {
                var invalidTierPhases = parsedTiers.Keys.Where(k => !TierablePhases.Contains(StripOccurrenceSuffix(k))).ToList();
                if (BuildInvalidTierKeyRejection(invalidTierPhases) is { } tierKeyRejection)
                    tierErrors.Add(tierKeyRejection);

                var invalidTierValues = parsedTiers.Values.Where(v => !validTiers.Contains(v)).ToList();
                if (invalidTierValues.Count > 0)
                    tierErrors.Add($"invalid tier values in model_tiers: {string.Join(", ", invalidTierValues)}. Valid: {string.Join(", ", validTiers)}");
            }
        }

        var error = Shared.ToolValidation.Check(
            (phases is { Length: > 0 }, "phases must be a non-empty array"),
            (!string.IsNullOrEmpty(reason), "reason is required"),
            (tierErrors.Count == 0, string.Join("; ", tierErrors)));

        return error is not null ? (false, error) : (true, null);
    }

    /// <summary>
    /// Builds the dependency-only Brain tools: <c>get_goal</c>, <c>search_knowledge</c>,
    /// <c>read_document</c>, <c>traverse_graph</c>, <c>get_current_time</c>,
    /// <c>list_config_files</c>, <c>read_config_file</c> and <c>raise_issue</c>.
    /// </summary>
    internal static List<AITool> BuildDependencyTools(
        IGoalStore? goalStore,
        Func<string, Task<GoalPipeline?>> pipelineResolver,
        KnowledgeGraph? knowledgeGraph,
        ILogger logger,
        ConfigRepoManager? configRepo = null,
        IIssueStore? issueStore = null,
        string? sourceGoalId = null,
        IEventBus? eventBus = null)
    {
        ArgumentNullException.ThrowIfNull(pipelineResolver);
        ArgumentNullException.ThrowIfNull(logger);

        return
        [
            AIFunctionFactory.Create(
                async ([Description("The goal ID to retrieve details for.")] string goal_id) =>
                {
                    if (goalStore is null)
                        return "Goal store is not available.";

                    var goal = await goalStore.GetGoalAsync(goal_id);
                    if (goal is null)
                        return $"Goal '{goal_id}' not found.";

                    var pipeline = await pipelineResolver(goal_id);
                    var iterationInfo = pipeline is not null
                        ? $"Current iteration: {pipeline.Iteration}, Phase: {pipeline.Phase}"
                        : "Pipeline not active.";

                    var relatedDocs = goal.Documents.Count > 0
                        ? $"\nRelated docs: {string.Join(", ", goal.Documents.Select(docId =>
                        {
                            var title = knowledgeGraph?.GetDocument(docId)?.Title;
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
                    if (knowledgeGraph is null)
                        return "Knowledge graph not available.";

                    var results = knowledgeGraph.Search(query);

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
                    if (knowledgeGraph is null)
                        return "Knowledge graph not available.";

                    var doc = knowledgeGraph.GetDocument(document_id);
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
                    if (knowledgeGraph is null)
                        return "Knowledge graph not available.";

                    var startDoc = knowledgeGraph.GetDocument(document_id);
                    if (startDoc is null)
                        return $"Document '{document_id}' not found.";

                    // Clamp depth to [1, 3]
                    depth = Math.Clamp(depth, 1, 3);

                    var validDirections = new[] { "outgoing", "incoming", "both" };
                    if (!validDirections.Contains(direction, StringComparer.OrdinalIgnoreCase))
                        return $"Invalid direction '{direction}'. Valid values: outgoing, incoming, both.";

                    // Parse optional link type filter from string array
                    HashSet<LinkType>? linkTypeFilter = null;
                    if (link_types is { Length: > 0 })
                    {
                        linkTypeFilter = new HashSet<LinkType>();
                        foreach (var ltStr in link_types)
                        {
                            var normalized = ltStr.Replace("_", "");
                            if (Enum.TryParse<LinkType>(normalized, ignoreCase: true, out var lt))
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
                    var edges = new List<(string From, string To, LinkType LinkType, string Direction)>();

                    while (queue.Count > 0)
                    {
                        var (currentId, currentDepth) = queue.Dequeue();
                        if (currentDepth >= depth) continue;

                        var currentDoc = knowledgeGraph.GetDocument(currentId);
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
                            var incoming = new List<KnowledgeDocument>();
                            incoming.AddRange(knowledgeGraph.GetChildren(currentId));
                            incoming.AddRange(knowledgeGraph.GetSupersededBy(currentId));
                            incoming.AddRange(knowledgeGraph.GetDependedOnBy(currentId));
                            incoming.AddRange(knowledgeGraph.GetImplementedBy(currentId));
                            incoming.AddRange(knowledgeGraph.GetRelatedBy(currentId));
                            incoming.AddRange(knowledgeGraph.GetReferencedBy(currentId));

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
                        var toDoc = knowledgeGraph.GetDocument(to);
                        var toTitle = toDoc is not null ? $" ({toDoc.Title})" : " [not found]";
                        var fromDoc = knowledgeGraph.GetDocument(from);
                        var fromTitle = fromDoc is not null ? $" ({fromDoc.Title})" : " [not found]";

                        sb.AppendLine($"- {from}{fromTitle} **{dir}[{lt}]** {to}{toTitle}");
                    }

                    // List all reachable documents (excluding start)
                    var reachable = visited.Where(id => id != document_id).ToList();
                    if (reachable.Count > 0)
                    {
                        sb.AppendLine($"\n### Reachable Documents ({reachable.Count})");
                        foreach (var docId in reachable)
                        {
                            var d = knowledgeGraph.GetDocument(docId);
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
                    return JsonSerializer.Serialize(new
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
                ([Description("Subdirectory to list files under. Leave empty for the config repo root.")] string? path = null,
                 CancellationToken cancellationToken = default) =>
                {
                    if (configRepo is null)
                        return "❌ Config repo tools are not available — no config repo configured.";

                    var baseDir = configRepo.LocalPath;
                    string targetDir;

                    if (string.IsNullOrWhiteSpace(path))
                    {
                        targetDir = baseDir;
                    }
                    else
                    {
                        var resolved = Path.GetFullPath(Path.Combine(baseDir, path));
                        if (!resolved.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                            && !string.Equals(resolved, baseDir, StringComparison.Ordinal))
                            return $"❌ Path '{path}' is outside the config repo. Access denied.";
                        targetDir = resolved;
                    }

                    // SECURITY: Block .git directory access to prevent credential disclosure
                    var targetSegments = targetDir.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                    if (targetSegments.Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase)))
                        return $"❌ Path '{path ?? "(root)"}' is inside a .git directory. Access denied.";

                    if (!Directory.Exists(targetDir))
                        return $"❌ Directory '{path ?? "(root)"}' not found in config repo.";

                    var files = Directory.GetFiles(targetDir, "*", SearchOption.AllDirectories)
                        .Where(f =>
                        {
                            var fileSegments = f.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                            return !fileSegments.Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase));
                        })
                        .Select(f => Path.GetRelativePath(baseDir, f).Replace('\\', '/'))
                        .OrderBy(f => f)
                        .ToList();

                    if (files.Count == 0)
                        return "(no files found)";

                    return string.Join('\n', files);
                },
                "list_config_files",
                "List files under the config repo root or a subdirectory. Returns relative paths."),
            AIFunctionFactory.Create(
                async ([Description("Relative path to the file within the config repo.")] string path,
                 [Description("Line number to start reading from (1-indexed). Default: 1")] int offset = 1,
                 [Description("Maximum number of lines to read. Default: 200")] int limit = 200,
                 CancellationToken cancellationToken = default) =>
                {
                    if (configRepo is null)
                        return "❌ Config repo tools are not available — no config repo configured.";

                    if (string.IsNullOrWhiteSpace(path))
                        return "❌ path is required.";

                    var baseDir = configRepo.LocalPath;
                    var resolved = Path.GetFullPath(Path.Combine(baseDir, path));

                    // SECURITY: Prevent path traversal
                    if (!resolved.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !string.Equals(resolved, baseDir, StringComparison.Ordinal))
                        return $"❌ Path '{path}' is outside the config repo. Access denied.";

                    // SECURITY: Block .git directory access to prevent credential disclosure
                    var segments = resolved.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase)))
                        return $"❌ Path '{path}' is inside a .git directory. Access denied.";

                    if (!File.Exists(resolved))
                        return $"❌ File '{path}' not found in config repo.";

                    var lines = await File.ReadAllLinesAsync(resolved, cancellationToken);
                    var startIndex = Math.Max(0, offset - 1);
                    if (startIndex >= lines.Length)
                        return $"❌ offset {offset} is beyond end of file ({lines.Length} lines total).";

                    var sb = new System.Text.StringBuilder();
                    var end = Math.Min(startIndex + limit, lines.Length);
                    for (var i = startIndex; i < end; i++)
                        sb.AppendLine($"{i + 1}: {lines[i]}");

                    if (end < lines.Length)
                        sb.AppendLine($"... ({lines.Length - end} more lines — use offset={end + 1} to continue)");

                    return sb.ToString().TrimEnd();
                },
                "read_config_file",
                "Read a config repo file with line numbers. Validates that the resolved path stays within the config repo root."),
            AIFunctionFactory.Create(
                // `severity` MUST carry a default: Microsoft.Extensions.AI derives AIFunction
                // argument optionality from parameter defaults, not nullable-reference
                // annotations. Without `= null` it stays in the required schema/binding set and
                // a call that omits it fails before the IssueSeverity.Low default can apply.
                // The CancellationToken then needs a default too (C# requires every parameter
                // after a defaulted one to be defaulted); the factory still injects the real
                // invocation token.
                async ([Description("Issue type: bug, suggestion, concern, code_quality, workflow")] string type,
                       [Description("Short title for the issue")] string title,
                       [Description("Detailed description of the issue")] string description,
                       [Description("Severity: low, medium, high (defaults to low)")] string? severity = null,
                       CancellationToken ct = default) =>
                {
                    if (issueStore is null)
                        return "Issue tracking is not available.";

                    if (string.IsNullOrWhiteSpace(type))
                        return "Type is required.";

                    IssueType parsedType;
                    try
                    {
                        parsedType = IssueIdGenerator.ParseIssueType(type);
                    }
                    catch (ArgumentException ex)
                    {
                        return ex.Message;
                    }

                    if (string.IsNullOrWhiteSpace(title))
                        return "Title is required.";

                    if (string.IsNullOrWhiteSpace(description))
                        return "Description is required.";

                    IssueSeverity parsedSeverity;
                    if (severity is null)
                    {
                        parsedSeverity = IssueSeverity.Low;
                    }
                    else
                    {
                        try
                        {
                            parsedSeverity = IssueIdGenerator.ParseIssueSeverity(severity);
                        }
                        catch (ArgumentException ex)
                        {
                            return ex.Message;
                        }
                    }

                    var generatedId = await IssueIdGenerator.GenerateAsync(title, issueStore, ct);

                    Issue BuildIssue(string id) => new()
                    {
                        Id = id,
                        Type = parsedType,
                        Title = title,
                        Description = description,
                        Severity = parsedSeverity,
                        SourceRole = "brain",
                        SourceGoalId = sourceGoalId,
                        SourceIteration = 0,
                        RepositoryNames = [],
                        CreatedAt = DateTime.UtcNow,
                    };

                    var issue = BuildIssue(generatedId);

                    try
                    {
                        await issueStore.CreateIssueAsync(issue, ct);
                    }
                    catch (InvalidOperationException)
                    {
                        // Duplicate ID (race): retry with a GUID-suffixed ID.
                        issue = BuildIssue($"{generatedId}-{Guid.NewGuid():N}");
                        await issueStore.CreateIssueAsync(issue, ct);
                    }

                    eventBus?.Publish(new SystemEvent(
                        Type: EventType.IssueRaised,
                        Message: issue.Title,
                        IssueId: issue.Id,
                        GoalId: sourceGoalId));

                    return $"Issue created: {issue.Id}";
                },
                "raise_issue",
                "Raise an issue for code quality problems, bugs, suggestions, concerns, or workflow issues noticed during planning or review that are out of scope for the current goal."),
        ];
    }
}

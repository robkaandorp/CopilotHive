using System.ComponentModel;
using System.Text;
using System.Text.Json;
using CopilotHive.Knowledge;

namespace CopilotHive.Orchestration;

public sealed partial class Composer
{
    /// <summary>
    /// Creates a new knowledge document in the config repo.
    /// </summary>
    [Description("Create a new knowledge document in the config repo.")]
    internal async Task<string> CreateDocumentAsync(
        [Description("Primary topic/directory (e.g. 'architecture', 'features', 'ideas', 'scratch', 'memory')")] string topic,
        [Description("Filename slug (e.g. 'brain-architecture') — combined with topic/subtopic to form the document ID")] string slug,
        [Description("Human-readable title")] string title,
        [Description("Document type: implementation, feature, idea, scratch, or memory")] string type,
        [Description("Markdown body content")] string content,
        [Description("Optional second-level subdirectory within the topic")] string? subtopic = null,
        [Description("Optional list of tags")] string[]? tags = null,
        [Description("Optional JSON array of link objects: [{\"target\":\"doc-id\",\"type\":\"related\",\"description\":\"why\"}]")] string? links = null,
        CancellationToken cancellationToken = default)
    {
        if (_knowledgeGraph is null)
            return "❌ Knowledge graph tools are not available — no config repo configured.";

        if (string.IsNullOrWhiteSpace(topic))
            return "❌ topic is required.";
        if (string.IsNullOrWhiteSpace(slug))
            return "❌ slug is required.";
        if (string.IsNullOrWhiteSpace(title))
            return "❌ title is required.";
        if (string.IsNullOrWhiteSpace(content))
            return "❌ content is required.";

        if (!Enum.TryParse<DocumentType>(type, ignoreCase: true, out var docType))
            return $"❌ Invalid type '{type}'. Valid types: implementation, feature, idea, scratch, memory.";

        // Build document ID from topic/subtopic/slug
        var docId = string.IsNullOrWhiteSpace(subtopic)
            ? $"{topic}-{slug}"
            : $"{topic}-{subtopic}-{slug}";

        // Parse links JSON if provided
        var parsedLinks = new List<DocumentLink>();
        if (!string.IsNullOrWhiteSpace(links))
        {
            try
            {
                using var doc = JsonDocument.Parse(links);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        var targetId = elem.TryGetProperty("target", out var t) ? t.GetString() : null;
                        var linkTypeStr = elem.TryGetProperty("type", out var lt) ? lt.GetString() : "related";
                        var desc = elem.TryGetProperty("description", out var d) ? d.GetString() : null;

                        if (string.IsNullOrWhiteSpace(targetId))
                            continue;

                        // Normalize underscores (e.g. "depends_on" → "DependsOn")
                        var normalizedLinkTypeStr = (linkTypeStr ?? "related").Replace("_", "");
                        if (!Enum.TryParse<LinkType>(normalizedLinkTypeStr, ignoreCase: true, out var linkType))
                            linkType = LinkType.Related;

                        parsedLinks.Add(new DocumentLink(targetId, linkType, desc));
                    }
                }
            }
            catch (JsonException ex)
            {
                return $"❌ Invalid links JSON: {ex.Message}";
            }
        }

        KnowledgeDocument createdDoc;
        try
        {
            createdDoc = await _knowledgeGraph.CreateDocumentAsync(
                docId, title, docType, content,
                topic: topic, subtopic: subtopic,
                author: "composer",
                tags: tags,
                ct: cancellationToken);

            // Add links to the document
            foreach (var link in parsedLinks)
                _knowledgeGraph.AddLink(docId, link);

            // Persist to config repo (no-op if configRepo is not configured)
            if (_configRepo is not null)
                await _knowledgeGraph.CommitToConfigRepoAsync(
                    _configRepo.LocalPath,
                    $"Add knowledge document: {docId}",
                    cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return $"❌ {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"❌ Failed to create document: {ex.Message}";
        }

        _logger.LogInformation("Composer created knowledge document '{DocId}'", docId);
        return $"✅ Document created:\n- ID: {createdDoc.Id}\n- File: {createdDoc.FilePath}\n- Type: {createdDoc.Type}\n- Status: {createdDoc.Status}";
    }

    /// <summary>
    /// Reads a knowledge document by ID.
    /// </summary>
    [Description("Read a knowledge document by ID. Returns full document including title, type, status, tags, links, and markdown body.")]
    internal Task<string> ReadDocumentAsync(
        [Description("Document ID (e.g. 'architecture-brain')")] string document_id,
        CancellationToken cancellationToken = default)
    {
        if (_knowledgeGraph is null)
            return Task.FromResult("❌ Knowledge graph tools are not available — no config repo configured.");

        if (string.IsNullOrWhiteSpace(document_id))
            return Task.FromResult("❌ document_id is required.");

        var doc = _knowledgeGraph.GetDocument(document_id);
        if (doc is null)
            return Task.FromResult($"❌ Document '{document_id}' not found.");

        var sb = new StringBuilder();
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

        return Task.FromResult(sb.ToString());
    }

    /// <summary>
    /// Updates an existing knowledge document.
    /// </summary>
    [Description("Update an existing knowledge document. Supports full replace or append mode for content.")]
    internal async Task<string> UpdateDocumentAsync(
        [Description("Document ID to update")] string document_id,
        [Description("New title (optional)")] string? title = null,
        [Description("New markdown body — replaces the existing content entirely (optional)")] string? content = null,
        [Description("New document type: implementation, feature, idea, scratch, or memory (optional)")] string? type = null,
        [Description("New status: draft, active, archived, or superseded (optional)")] string? status = null,
        [Description("Replace tags entirely with this new list (optional)")] string[]? tags = null,
        [Description("Append this text to the existing body instead of replacing it (optional)")] string? append_content = null,
        CancellationToken cancellationToken = default)
    {
        if (_knowledgeGraph is null)
            return "❌ Knowledge graph tools are not available — no config repo configured.";

        if (string.IsNullOrWhiteSpace(document_id))
            return "❌ document_id is required.";

        var doc = _knowledgeGraph.GetDocument(document_id);
        if (doc is null)
            return $"❌ Document '{document_id}' not found.";

        DocumentType? parsedType = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!Enum.TryParse<DocumentType>(type, ignoreCase: true, out var dt))
                return $"❌ Invalid type '{type}'. Valid types: implementation, feature, idea, scratch, memory.";
            parsedType = dt;
        }

        DocumentStatus? parsedStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<DocumentStatus>(status, ignoreCase: true, out var ds))
                return $"❌ Invalid status '{status}'. Valid statuses: draft, active, archived, superseded.";
            parsedStatus = ds;
        }

        // Handle append_content mode
        string? resolvedContent = content;
        if (!string.IsNullOrEmpty(append_content))
        {
            var existingContent = doc.Content;
            resolvedContent = existingContent.TrimEnd() + "\n\n" + append_content;
        }

        try
        {
            await _knowledgeGraph.UpdateDocumentAsync(
                document_id,
                content: resolvedContent,
                title: title,
                type: parsedType,
                status: parsedStatus,
                tags: tags,
                ct: cancellationToken);

            if (_configRepo is not null)
                await _knowledgeGraph.CommitToConfigRepoAsync(
                    _configRepo.LocalPath,
                    $"Update knowledge document: {document_id}",
                    cancellationToken);
        }
        catch (KeyNotFoundException ex)
        {
            return $"❌ {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"❌ Failed to update document: {ex.Message}";
        }

        _logger.LogInformation("Composer updated knowledge document '{DocId}'", document_id);
        return $"✅ Document '{document_id}' updated.";
    }

    /// <summary>
    /// Deletes a knowledge document, warning about incoming links.
    /// </summary>
    [Description("Delete a knowledge document. Warns if other documents link to it (dangling links).")]
    internal async Task<string> DeleteDocumentAsync(
        [Description("Document ID to delete")] string document_id,
        CancellationToken cancellationToken = default)
    {
        if (_knowledgeGraph is null)
            return "❌ Knowledge graph tools are not available — no config repo configured.";

        if (string.IsNullOrWhiteSpace(document_id))
            return "❌ document_id is required.";

        var doc = _knowledgeGraph.GetDocument(document_id);
        if (doc is null)
            return $"❌ Document '{document_id}' not found.";

        // Check for dangling links — documents that depend on or link to this one
        var dependedOnBy = _knowledgeGraph.GetDependedOnBy(document_id);
        var children = _knowledgeGraph.GetChildren(document_id);
        var implementedBy = _knowledgeGraph.GetImplementedBy(document_id);
        var supersededBy = _knowledgeGraph.GetSupersededBy(document_id);

        var warnings = new List<string>();
        if (dependedOnBy.Count > 0)
            warnings.Add($"  - DependsOn links from: {string.Join(", ", dependedOnBy.Select(d => d.Id))}");
        if (children.Count > 0)
            warnings.Add($"  - Parent links from: {string.Join(", ", children.Select(d => d.Id))}");
        if (implementedBy.Count > 0)
            warnings.Add($"  - Implements links from: {string.Join(", ", implementedBy.Select(d => d.Id))}");
        if (supersededBy.Count > 0)
            warnings.Add($"  - Supersedes links from: {string.Join(", ", supersededBy.Select(d => d.Id))}");

        try
        {
            await _knowledgeGraph.DeleteDocumentAsync(document_id, cancellationToken);

            if (_configRepo is not null)
                await _knowledgeGraph.CommitToConfigRepoAsync(
                    _configRepo.LocalPath,
                    $"Delete knowledge document: {document_id}",
                    cancellationToken);
        }
        catch (KeyNotFoundException ex)
        {
            return $"❌ {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"❌ Failed to delete document: {ex.Message}";
        }

        _logger.LogInformation("Composer deleted knowledge document '{DocId}'", document_id);

        if (warnings.Count > 0)
            return $"✅ Document '{document_id}' deleted.\n⚠️ The following documents had links pointing to it (now dangling):\n{string.Join("\n", warnings)}";

        return $"✅ Document '{document_id}' deleted.";
    }

    /// <summary>
    /// Full-text search across all knowledge documents with optional filters.
    /// </summary>
    [Description("Search knowledge documents by text query, with optional filters for topic, type, status, and tag.")]
    internal Task<string> SearchKnowledgeAsync(
        [Description("Search terms — substring match across title, content, and tags")] string query,
        [Description("Filter by topic (optional)")] string? topic = null,
        [Description("Filter by document type: implementation, feature, idea, scratch, or memory (optional)")] string? type = null,
        [Description("Filter by status: draft, active, archived, or superseded (optional)")] string? status = null,
        [Description("Filter by tag (optional)")] string? tag = null,
        [Description("Maximum number of results to return. Default: 10")] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (_knowledgeGraph is null)
            return Task.FromResult("❌ Knowledge graph tools are not available — no config repo configured.");

        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult("❌ query is required.");

        var results = _knowledgeGraph.Search(query);

        // Apply optional filters
        if (!string.IsNullOrWhiteSpace(topic))
            results = results.Where(d => string.Equals(d.Topic, topic, StringComparison.OrdinalIgnoreCase)).ToList();

        if (!string.IsNullOrWhiteSpace(type) && Enum.TryParse<DocumentType>(type, ignoreCase: true, out var dt))
            results = results.Where(d => d.Type == dt).ToList();

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<DocumentStatus>(status, ignoreCase: true, out var ds))
            results = results.Where(d => d.Status == ds).ToList();

        if (!string.IsNullOrWhiteSpace(tag))
            results = results.Where(d => d.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))).ToList();

        if (results.Count == 0)
            return Task.FromResult($"No knowledge documents found matching '{query}'.");

        var capped = results.Take(limit).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} document(s) (showing {capped.Count}):\n");

        foreach (var doc in capped)
        {
            var snippet = doc.Content.Length > 200 ? doc.Content[..200] + "…" : doc.Content;
            snippet = snippet.Replace('\n', ' ');
            sb.AppendLine($"### {doc.Id}");
            sb.AppendLine($"- **Title:** {doc.Title}");
            sb.AppendLine($"- **Type:** {doc.Type} | **Status:** {doc.Status}");
            if (doc.Tags.Count > 0)
                sb.AppendLine($"- **Tags:** {string.Join(", ", doc.Tags)}");
            sb.AppendLine($"- **Snippet:** {snippet}");
            sb.AppendLine();
        }

        return Task.FromResult(sb.ToString().TrimEnd());
    }

    /// <summary>
    /// Adds an outgoing link from a document to another.
    /// </summary>
    [Description("Add an outgoing link from a document to another. Does not modify the target document.")]
    internal async Task<string> LinkDocumentAsync(
        [Description("Source document ID")] string document_id,
        [Description("Target document ID")] string target_id,
        [Description("Link type: parent, supersedes, depends_on, implements, related, or references")] string link_type,
        [Description("Optional description of why this link exists")] string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (_knowledgeGraph is null)
            return "❌ Knowledge graph tools are not available — no config repo configured.";

        if (string.IsNullOrWhiteSpace(document_id))
            return "❌ document_id is required.";
        if (string.IsNullOrWhiteSpace(target_id))
            return "❌ target_id is required.";
        if (string.IsNullOrWhiteSpace(link_type))
            return "❌ link_type is required.";

        if (_knowledgeGraph.GetDocument(document_id) is null)
            return $"❌ Source document '{document_id}' not found.";

        // Normalize link_type: replace underscores with nothing (depends_on → DependsOn)
        var normalizedLinkType = link_type.Replace("_", "");
        if (!Enum.TryParse<LinkType>(normalizedLinkType, ignoreCase: true, out var lt))
            return $"❌ Invalid link_type '{link_type}'. Valid types: parent, supersedes, depends_on, implements, related, references.";

        // Warn if target doesn't exist (forward reference allowed)
        var targetExists = _knowledgeGraph.GetDocument(target_id) is not null;
        var warning = targetExists ? "" : $"\n⚠️ Target document '{target_id}' does not exist yet (forward reference).";

        try
        {
            _knowledgeGraph.AddLink(document_id, new DocumentLink(target_id, lt, description));

            if (_configRepo is not null)
                await _knowledgeGraph.CommitToConfigRepoAsync(
                    _configRepo.LocalPath,
                    $"Link {document_id} → {target_id} ({lt})",
                    cancellationToken);
        }
        catch (KeyNotFoundException ex)
        {
            return $"❌ {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"❌ Failed to add link: {ex.Message}";
        }

        _logger.LogInformation("Composer linked '{DocId}' → '{TargetId}' ({LinkType})", document_id, target_id, lt);
        return $"✅ Link added: {document_id} → {target_id} ({lt}).{warning}";
    }

    /// <summary>
    /// Removes an outgoing link from a document.
    /// </summary>
    [Description("Remove an outgoing link from a document.")]
    internal async Task<string> UnlinkDocumentAsync(
        [Description("Source document ID")] string document_id,
        [Description("Target document ID")] string target_id,
        [Description("Link type to remove: parent, supersedes, depends_on, implements, related, or references")] string link_type,
        CancellationToken cancellationToken = default)
    {
        if (_knowledgeGraph is null)
            return "❌ Knowledge graph tools are not available — no config repo configured.";

        if (string.IsNullOrWhiteSpace(document_id))
            return "❌ document_id is required.";
        if (string.IsNullOrWhiteSpace(target_id))
            return "❌ target_id is required.";
        if (string.IsNullOrWhiteSpace(link_type))
            return "❌ link_type is required.";

        if (_knowledgeGraph.GetDocument(document_id) is null)
            return $"❌ Document '{document_id}' not found.";

        var normalizedLinkType = link_type.Replace("_", "");
        if (!Enum.TryParse<LinkType>(normalizedLinkType, ignoreCase: true, out var lt))
            return $"❌ Invalid link_type '{link_type}'. Valid types: parent, supersedes, depends_on, implements, related, references.";

        try
        {
            _knowledgeGraph.RemoveLink(document_id, target_id, lt);

            if (_configRepo is not null)
                await _knowledgeGraph.CommitToConfigRepoAsync(
                    _configRepo.LocalPath,
                    $"Unlink {document_id} → {target_id} ({lt})",
                    cancellationToken);
        }
        catch (KeyNotFoundException ex)
        {
            return $"❌ {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"❌ Failed to remove link: {ex.Message}";
        }

        _logger.LogInformation("Composer unlinked '{DocId}' → '{TargetId}' ({LinkType})", document_id, target_id, lt);
        return $"✅ Link removed: {document_id} → {target_id} ({lt}).";
    }

    /// <summary>
    /// Lists knowledge documents with optional filters.
    /// </summary>
    [Description("List knowledge documents with optional filters for topic, type, status, and tag.")]
    internal Task<string> ListDocumentsAsync(
        [Description("Filter by topic (optional)")] string? topic = null,
        [Description("Filter by document type: implementation, feature, idea, scratch, or memory (optional)")] string? type = null,
        [Description("Filter by status: draft, active, archived, or superseded (optional)")] string? status = null,
        [Description("Filter by tag (optional)")] string? tag = null,
        [Description("Maximum number of results to return. Default: 20")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (_knowledgeGraph is null)
            return Task.FromResult("❌ Knowledge graph tools are not available — no config repo configured.");

        // Get all documents and apply filters
        IEnumerable<KnowledgeDocument> all = _knowledgeGraph.Search("");

        if (!string.IsNullOrWhiteSpace(topic))
            all = all.Where(d => string.Equals(d.Topic, topic, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(type) && Enum.TryParse<DocumentType>(type, ignoreCase: true, out var dt))
            all = all.Where(d => d.Type == dt);

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<DocumentStatus>(status, ignoreCase: true, out var ds))
            all = all.Where(d => d.Status == ds);

        if (!string.IsNullOrWhiteSpace(tag))
            all = all.Where(d => d.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)));

        var results = all.ToList();
        if (results.Count == 0)
            return Task.FromResult("No knowledge documents found.");

        var capped = results.Take(limit).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"{results.Count} document(s) (showing {capped.Count}):\n");

        foreach (var doc in capped)
        {
            sb.AppendLine($"- **{doc.Id}** — {doc.Title}");
            sb.Append($"  Type: {doc.Type} | Status: {doc.Status}");
            if (doc.Tags.Count > 0)
                sb.Append($" | Tags: {string.Join(", ", doc.Tags)}");
            sb.AppendLine();
        }

        return Task.FromResult(sb.ToString().TrimEnd());
    }

    /// <summary>
    /// Traverses the knowledge graph from a starting document.
    /// </summary>
    [Description("Explore the knowledge graph from a starting document, following links up to a given depth.")]
    internal Task<string> TraverseGraphAsync(
        [Description("Starting document ID")] string document_id,
        [Description("Traversal depth (default 1, max 3)")] int depth = 1,
        [Description("Direction: 'outgoing' (default), 'incoming', or 'both'")] string direction = "outgoing",
        [Description("Filter to specific link types (optional, comma-separated): parent, supersedes, depends_on, implements, related, references")] string? link_types = null,
        CancellationToken cancellationToken = default)
    {
        if (_knowledgeGraph is null)
            return Task.FromResult("❌ Knowledge graph tools are not available — no config repo configured.");

        if (string.IsNullOrWhiteSpace(document_id))
            return Task.FromResult("❌ document_id is required.");

        var startDoc = _knowledgeGraph.GetDocument(document_id);
        if (startDoc is null)
            return Task.FromResult($"❌ Document '{document_id}' not found.");

        // Clamp depth to [1, 3]
        depth = Math.Clamp(depth, 1, 3);

        var validDirections = new[] { "outgoing", "incoming", "both" };
        if (!validDirections.Contains(direction, StringComparer.OrdinalIgnoreCase))
            return Task.FromResult($"❌ Invalid direction '{direction}'. Valid values: outgoing, incoming, both.");

        // Parse optional link type filter
        HashSet<LinkType>? linkTypeFilter = null;
        if (!string.IsNullOrWhiteSpace(link_types))
        {
            linkTypeFilter = new HashSet<LinkType>();
            foreach (var ltStr in link_types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalized = ltStr.Replace("_", "");
                if (Enum.TryParse<LinkType>(normalized, ignoreCase: true, out var lt))
                    linkTypeFilter.Add(lt);
            }
        }

        var sb = new StringBuilder();
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

            // Incoming links (from reverse index via GetRelated traversal approach)
            if (direction.Equals("incoming", StringComparison.OrdinalIgnoreCase) ||
                direction.Equals("both", StringComparison.OrdinalIgnoreCase))
            {
                // Combine all incoming docs from all inverse types
                var incoming = new List<KnowledgeDocument>();
                incoming.AddRange(_knowledgeGraph.GetChildren(currentId));
                incoming.AddRange(_knowledgeGraph.GetSupersededBy(currentId));
                incoming.AddRange(_knowledgeGraph.GetDependedOnBy(currentId));
                incoming.AddRange(_knowledgeGraph.GetImplementedBy(currentId));

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
            return Task.FromResult(sb.ToString().TrimEnd());
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

        return Task.FromResult(sb.ToString().TrimEnd());
    }
}

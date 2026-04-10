using CopilotHive.Configuration;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Knowledge;

/// <summary>
/// In-memory knowledge graph loaded from and persisted to the config repo.
/// Documents are markdown files with YAML frontmatter stored under <c>knowledge/</c>.
/// </summary>
public sealed class KnowledgeGraph
{
    private readonly ConfigRepoManager? _configRepo;
    private readonly ILogger<KnowledgeGraph>? _logger;

    private Dictionary<string, KnowledgeDocument> _documents = [];

    // targetId → list of (sourceId, LinkType) for inverse queries
    private Dictionary<string, List<(string SourceId, LinkType Type)>> _reverseIndex = [];

    // IDs of documents modified since the last load (need to be written back)
    private HashSet<string> _dirtyDocuments = [];

    // File paths (relative to config repo root) for documents deleted since the last load
    private HashSet<string> _deletedDocumentPaths = [];

    /// <summary>
    /// Initialises a new <see cref="KnowledgeGraph"/>.
    /// </summary>
    /// <param name="configRepo">Optional config repo manager used to commit changes.</param>
    /// <param name="logger">Optional logger.</param>
    public KnowledgeGraph(ConfigRepoManager? configRepo = null, ILogger<KnowledgeGraph>? logger = null)
    {
        _configRepo = configRepo;
        _logger = logger;
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new knowledge document and adds it to the in-memory graph.
    /// The document is marked dirty and will be written on the next <see cref="CommitToConfigRepoAsync"/>.
    /// </summary>
    /// <param name="id">Unique document identifier (e.g. "architecture-distributed-systems-brain-session-per-goal").</param>
    /// <param name="title">Human-readable title.</param>
    /// <param name="type">Document type.</param>
    /// <param name="content">Markdown body content.</param>
    /// <param name="topic">Explicit topic (first directory level, e.g. "architecture"). Required.</param>
    /// <param name="subtopic">Optional second directory level (e.g. "distributed-systems").</param>
    /// <param name="author">Author of the document.</param>
    /// <param name="tags">Optional tags.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<KnowledgeDocument> CreateDocumentAsync(
        string id,
        string title,
        DocumentType type,
        string content,
        string? topic = null,
        string? subtopic = null,
        string? author = null,
        IEnumerable<string>? tags = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Document ID cannot be empty.", nameof(id));

        if (_documents.ContainsKey(id))
            throw new InvalidOperationException($"A document with id '{id}' already exists.");

        var now = DateTime.UtcNow;

        // Derive topic from the id when not explicitly provided (first segment before '-')
        var resolvedTopic = !string.IsNullOrWhiteSpace(topic) ? topic : id.Split('-')[0];
        var filePath = BuildFilePath(id, resolvedTopic, subtopic);

        var doc = new KnowledgeDocument
        {
            Id = id,
            Title = title,
            Topic = resolvedTopic,
            Subtopic = subtopic,
            Type = type,
            Status = DocumentStatus.Draft,
            Content = content,
            Tags = tags?.ToList() ?? [],
            Links = [],
            CreatedAt = now,
            UpdatedAt = now,
            Author = author ?? "composer",
            FilePath = filePath,
        };

        _documents[id] = doc;
        _dirtyDocuments.Add(id);

        _logger?.LogInformation("Created knowledge document {DocId}", id);
        return Task.FromResult(doc);
    }

    /// <summary>Returns the document with the given ID, or null if not found.</summary>
    public KnowledgeDocument? GetDocument(string id)
        => _documents.TryGetValue(id, out var doc) ? doc : null;

    /// <summary>
    /// Updates the content and/or metadata of an existing document.
    /// </summary>
    public Task UpdateDocumentAsync(
        string id,
        string? content = null,
        string? title = null,
        DocumentType? type = null,
        DocumentStatus? status = null,
        IEnumerable<string>? tags = null,
        string? author = null,
        CancellationToken ct = default)
    {
        if (!_documents.TryGetValue(id, out var doc))
            throw new KeyNotFoundException($"Document '{id}' not found.");

        if (content is not null) doc.Content = content;
        if (title is not null) doc.Title = title;
        if (type.HasValue) doc.Type = type.Value;
        if (status.HasValue) doc.Status = status.Value;
        if (tags is not null) doc.Tags = tags.ToList();
        if (author is not null) doc.Author = author;
        doc.UpdatedAt = DateTime.UtcNow;

        _dirtyDocuments.Add(id);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes a document from the graph. Also removes it from the reverse index and
    /// strips all incoming links that point to it.
    /// The file on disk will be deleted on the next <see cref="CommitToConfigRepoAsync"/>.
    /// </summary>
    public Task DeleteDocumentAsync(string id, CancellationToken ct = default)
    {
        if (!_documents.TryGetValue(id, out var doc))
            throw new KeyNotFoundException($"Document '{id}' not found.");

        // Remove outgoing links from the reverse index
        foreach (var link in doc.Links)
            RemoveFromReverseIndex(id, link.TargetId, link.Type);

        // Remove incoming reverse-index entries that point to this doc
        _reverseIndex.Remove(id);

        // Remove this doc's ID from every other doc's reverse-index entry
        foreach (var targetId in _reverseIndex.Keys.ToList())
        {
            _reverseIndex[targetId].RemoveAll(e => e.SourceId == id);
            if (_reverseIndex[targetId].Count == 0)
                _reverseIndex.Remove(targetId);
        }

        // Track file for deletion on next commit
        _deletedDocumentPaths.Add(doc.FilePath);

        _documents.Remove(id);
        _dirtyDocuments.Remove(id);
        return Task.CompletedTask;
    }

    // ── Links ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a link to the specified document. Deduplicates by (TargetId, Type).
    /// Updates both the forward link list on the document and the reverse index.
    /// </summary>
    public void AddLink(string documentId, DocumentLink link)
    {
        if (!_documents.TryGetValue(documentId, out var doc))
            throw new KeyNotFoundException($"Document '{documentId}' not found.");

        // Remove any existing link with the same TargetId+Type pair
        doc.Links.RemoveAll(l => l.TargetId == link.TargetId && l.Type == link.Type);
        doc.Links.Add(link);

        AddToReverseIndex(documentId, link.TargetId, link.Type);
        doc.UpdatedAt = DateTime.UtcNow;
        _dirtyDocuments.Add(documentId);
    }

    /// <summary>
    /// Removes the link with the given (TargetId, Type) from the specified document.
    /// Also removes the corresponding entry from the reverse index.
    /// </summary>
    public void RemoveLink(string documentId, string targetId, LinkType type)
    {
        if (!_documents.TryGetValue(documentId, out var doc))
            throw new KeyNotFoundException($"Document '{documentId}' not found.");

        doc.Links.RemoveAll(l => l.TargetId == targetId && l.Type == type);
        RemoveFromReverseIndex(documentId, targetId, type);
        doc.UpdatedAt = DateTime.UtcNow;
        _dirtyDocuments.Add(documentId);
    }

    // ── Query ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Performs a simple case-insensitive substring search across document Title, Tags, and Content.
    /// </summary>
    public List<KnowledgeDocument> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [.. _documents.Values];

        var lower = query.ToLowerInvariant();
        return _documents.Values
            .Where(d =>
                d.Title.ToLowerInvariant().Contains(lower) ||
                d.Content.ToLowerInvariant().Contains(lower) ||
                d.Tags.Any(t => t.ToLowerInvariant().Contains(lower)))
            .ToList();
    }

    /// <summary>Returns all documents whose Topic matches the given topic (case-insensitive).</summary>
    public List<KnowledgeDocument> FindByTopic(string topic)
        => _documents.Values
            .Where(d => string.Equals(d.Topic, topic, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Returns all documents of the given type.</summary>
    public List<KnowledgeDocument> FindByType(DocumentType type)
        => _documents.Values.Where(d => d.Type == type).ToList();

    /// <summary>Returns all documents that include the given tag (case-insensitive).</summary>
    public List<KnowledgeDocument> FindByTag(string tag)
        => _documents.Values
            .Where(d => d.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    /// <summary>Returns all documents with the given status.</summary>
    public List<KnowledgeDocument> FindByStatus(DocumentStatus status)
        => _documents.Values.Where(d => d.Status == status).ToList();

    /// <summary>Returns all documents in the graph.</summary>
    public List<KnowledgeDocument> GetAllDocuments()
        => [.. _documents.Values];

    /// <summary>
    /// Returns the outgoing links from the specified document (the document's own link list).
    /// Returns an empty list if the document is not found.
    /// </summary>
    public List<DocumentLink> GetOutgoingLinks(string id)
        => _documents.TryGetValue(id, out var doc) ? [.. doc.Links] : [];

    /// <summary>
    /// Returns incoming links to the specified document — i.e., all links from other documents
    /// that target this document. Each entry includes the source document ID, link type, and
    /// the description stored on the outgoing link.
    /// Returns an empty list if no documents link to this one.
    /// </summary>
    public List<IncomingLink> GetIncomingLinks(string id)
    {
        if (!_reverseIndex.TryGetValue(id, out var entries))
            return [];

        var result = new List<IncomingLink>();
        foreach (var (sourceId, type) in entries)
        {
            if (!_documents.TryGetValue(sourceId, out var sourceDoc))
                continue;

            var outgoing = sourceDoc.Links.FirstOrDefault(l => l.TargetId == id && l.Type == type);
            result.Add(new IncomingLink(sourceId, type, outgoing?.Description));
        }
        return result;
    }

    // ── Graph Traversal ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all documents that have a <see cref="LinkType.Parent"/> link pointing to <paramref name="id"/>.
    /// These are the "children" of the document — documents that declared this one as their parent.
    /// </summary>
    public List<KnowledgeDocument> GetChildren(string id)
        => GetInverseDocuments(id, LinkType.Parent);

    /// <summary>
    /// Returns all documents that have a <see cref="LinkType.Supersedes"/> link pointing to <paramref name="id"/>.
    /// These are the newer documents that have superseded the given document.
    /// </summary>
    public List<KnowledgeDocument> GetSupersededBy(string id)
        => GetInverseDocuments(id, LinkType.Supersedes);

    /// <summary>
    /// Returns all documents that have a <see cref="LinkType.DependsOn"/> link pointing to <paramref name="id"/>.
    /// These are the documents that depend on the given document.
    /// </summary>
    public List<KnowledgeDocument> GetDependedOnBy(string id)
        => GetInverseDocuments(id, LinkType.DependsOn);

    /// <summary>
    /// Returns all documents that have a <see cref="LinkType.Implements"/> link pointing to <paramref name="id"/>.
    /// These are the documents that implement the given document (e.g. spec → implementation).
    /// </summary>
    public List<KnowledgeDocument> GetImplementedBy(string id)
        => GetInverseDocuments(id, LinkType.Implements);

    /// <summary>
    /// Returns all documents that have a <see cref="LinkType.Related"/> link pointing to <paramref name="id"/>.
    /// </summary>
    public List<KnowledgeDocument> GetRelatedBy(string id)
        => GetInverseDocuments(id, LinkType.Related);

    /// <summary>
    /// Returns all documents that have a <see cref="LinkType.References"/> link pointing to <paramref name="id"/>.
    /// </summary>
    public List<KnowledgeDocument> GetReferencedBy(string id)
        => GetInverseDocuments(id, LinkType.References);

    /// <summary>
    /// Performs a BFS traversal of forward links up to <paramref name="maxDepth"/> hops.
    /// Returns all reachable documents (excluding the starting document).
    /// </summary>
    public List<KnowledgeDocument> GetRelated(string id, int maxDepth = 1)
    {
        if (!_documents.ContainsKey(id))
            return [];

        var visited = new HashSet<string> { id };
        var queue = new Queue<(string Id, int Depth)>();
        queue.Enqueue((id, 0));
        var result = new List<KnowledgeDocument>();

        while (queue.Count > 0)
        {
            var (currentId, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            if (!_documents.TryGetValue(currentId, out var current)) continue;

            foreach (var link in current.Links)
            {
                if (visited.Contains(link.TargetId)) continue;
                visited.Add(link.TargetId);

                if (_documents.TryGetValue(link.TargetId, out var target))
                {
                    result.Add(target);
                    queue.Enqueue((link.TargetId, depth + 1));
                }
            }
        }

        return result;
    }

    // ── Persistence ────────────────────────────────────────────────────────────

    /// <summary>
    /// Enumerates all <c>*.md</c> files under <c>knowledge/</c> in the config repo,
    /// parses their YAML frontmatter and markdown body, and rebuilds the in-memory
    /// document dictionary and reverse link index. Clears dirty tracking.
    /// </summary>
    public Task ReloadFromConfigRepoAsync(string configRepoPath, CancellationToken ct = default)
    {
        var knowledgePath = Path.Combine(configRepoPath, "knowledge");
        if (!Directory.Exists(knowledgePath))
        {
            _logger?.LogInformation("Knowledge directory not found at {KnowledgePath} — starting with empty graph", knowledgePath);
            _documents = [];
            _reverseIndex = [];
            _dirtyDocuments = [];
            _deletedDocumentPaths = [];
            return Task.CompletedTask;
        }

        var newDocuments = new Dictionary<string, KnowledgeDocument>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in Directory.EnumerateFiles(knowledgePath, "*.md", SearchOption.AllDirectories))
        {
            try
            {
                var relativePath = Path.GetRelativePath(configRepoPath, filePath)
                    .Replace('\\', '/');

                var doc = ParseMarkdownFile(filePath, relativePath);
                if (doc is not null)
                    newDocuments[doc.Id] = doc;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to parse knowledge document at {FilePath}", filePath);
            }
        }

        _documents = newDocuments;
        _reverseIndex = [];
        _dirtyDocuments = [];
        _deletedDocumentPaths = [];

        // Rebuild reverse index from all loaded documents
        foreach (var doc in _documents.Values)
        {
            foreach (var link in doc.Links)
                AddToReverseIndex(doc.Id, link.TargetId, link.Type);
        }

        _logger?.LogInformation("Loaded {Count} knowledge documents from {KnowledgePath}", _documents.Count, knowledgePath);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes all dirty documents to disk and commits each via <see cref="ConfigRepoManager.CommitFileAsync"/>.
    /// Also deletes files for any documents that were removed since the last load.
    /// </summary>
    public async Task CommitToConfigRepoAsync(string configRepoPath, string message, CancellationToken ct = default)
    {
        var dirty = _dirtyDocuments.ToList();
        var toDelete = _deletedDocumentPaths.ToList();

        if (dirty.Count == 0 && toDelete.Count == 0) return;

        foreach (var docId in dirty)
        {
            if (!_documents.TryGetValue(docId, out var doc)) continue;

            var fullPath = Path.Combine(configRepoPath, doc.FilePath);
            var dir = Path.GetDirectoryName(fullPath);
            if (dir is not null)
                Directory.CreateDirectory(dir);

            var markdown = SerializeDocument(doc);
            await File.WriteAllTextAsync(fullPath, markdown, ct);

            if (_configRepo is not null)
            {
                await _configRepo.CommitFileAsync(doc.FilePath, message, ct);
                _logger?.LogInformation("Committed knowledge document {DocId} to config repo", docId);
            }
        }

        foreach (var filePath in toDelete)
        {
            var fullPath = Path.Combine(configRepoPath, filePath);
            if (File.Exists(fullPath))
                File.Delete(fullPath);

            if (_configRepo is not null)
            {
                await _configRepo.DeleteFileAsync(filePath, message, ct);
                _logger?.LogInformation("Deleted knowledge document file {FilePath} from config repo", filePath);
            }
        }

        _dirtyDocuments.Clear();
        _deletedDocumentPaths.Clear();
    }

    // ── ID / Path Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Derives a document ID from a relative file path.
    /// Strips the "knowledge/" prefix and ".md" suffix, replaces "/" with "-".
    /// Example: "knowledge/architecture/distributed-systems/brain.md" → "architecture-distributed-systems-brain"
    /// </summary>
    public static string DeriveDocumentIdFromPath(string relativePath)
    {
        // Normalize separators
        var normalized = relativePath.Replace('\\', '/').Trim('/');

        // Strip "knowledge/" prefix
        const string prefix = "knowledge/";
        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[prefix.Length..];

        // Strip ".md" suffix
        if (normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^3];

        // Replace "/" with "-"
        return normalized.Replace('/', '-');
    }

    // ── Private Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the relative file path for a document given its id, topic, and optional subtopic.
    /// The file name is the id with the topic (and subtopic) directory prefix stripped,
    /// so that <see cref="DeriveDocumentIdFromPath"/> round-trips correctly.
    /// Examples:
    ///   id="architecture-core", topic="architecture" → "knowledge/architecture/core.md"
    ///   id="architecture-distributed-systems-brain-session-per-goal", topic="architecture", subtopic="distributed-systems"
    ///       → "knowledge/architecture/distributed-systems/brain-session-per-goal.md"
    /// </summary>
    private static string BuildFilePath(string id, string topic, string? subtopic)
    {
        // Strip the topic prefix from the id to get the leaf slug
        var topicPrefix = topic + "-";
        var leaf = id.StartsWith(topicPrefix, StringComparison.OrdinalIgnoreCase)
            ? id[topicPrefix.Length..] : id;

        if (!string.IsNullOrWhiteSpace(subtopic))
        {
            // Strip subtopic prefix from the leaf
            var subtopicPrefix = subtopic + "-";
            if (leaf.StartsWith(subtopicPrefix, StringComparison.OrdinalIgnoreCase))
                leaf = leaf[subtopicPrefix.Length..];

            return $"knowledge/{topic}/{subtopic}/{leaf}.md";
        }

        return $"knowledge/{topic}/{leaf}.md";
    }

    private static (string Topic, string? Subtopic) DeriveTopicSubtopicFromPath(string relativePath)
    {
        // relativePath is like "knowledge/architecture/distributed-systems/brain.md"
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        const string prefix = "knowledge/";
        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[prefix.Length..];

        var parts = normalized.Split('/');
        if (parts.Length == 1)
        {
            // flat file: knowledge/something.md → topic = filename without .md
            var name = parts[0].EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? parts[0][..^3] : parts[0];
            return (name, null);
        }

        // parts[0] is the topic directory
        // parts[1] might be a sub-directory or the filename
        if (parts.Length == 2)
            return (parts[0], null);

        // 3+ parts: parts[0] = topic, parts[1] = subtopic, rest is filename
        return (parts[0], parts[1]);
    }

    private KnowledgeDocument? ParseMarkdownFile(string fullPath, string relativePath)
    {
        var rawContent = File.ReadAllText(fullPath);
        var (frontmatter, body) = SplitFrontmatter(rawContent);

        var id = DeriveDocumentIdFromPath(relativePath);
        var (topic, subtopic) = DeriveTopicSubtopicFromPath(relativePath);

        // Parse YAML frontmatter
        var title = id; // fallback
        var type = DocumentType.Implementation;
        var status = DocumentStatus.Active;
        var tags = new List<string>();
        var links = new List<DocumentLink>();
        var createdAt = DateTime.UtcNow;
        var updatedAt = DateTime.UtcNow;
        var author = "composer";

        if (!string.IsNullOrEmpty(frontmatter))
            ParseFrontmatter(frontmatter, ref title, ref type, ref status, ref tags, ref links, ref createdAt, ref updatedAt, ref author);

        return new KnowledgeDocument
        {
            Id = id,
            Title = title,
            Topic = topic,
            Subtopic = subtopic,
            Type = type,
            Status = status,
            Content = body.Trim(),
            Tags = tags,
            Links = links,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            Author = author,
            FilePath = relativePath,
        };
    }

    private static (string Frontmatter, string Body) SplitFrontmatter(string content)
    {
        if (!content.StartsWith("---", StringComparison.Ordinal))
            return ("", content);

        var lines = content.Split('\n');
        var endIndex = -1;

        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == "---")
            {
                endIndex = i;
                break;
            }
        }

        if (endIndex < 0)
            return ("", content);

        var frontmatter = string.Join('\n', lines[1..endIndex]);
        var body = string.Join('\n', lines[(endIndex + 1)..]);
        return (frontmatter, body);
    }

    private static void ParseFrontmatter(
        string frontmatter,
        ref string title,
        ref DocumentType type,
        ref DocumentStatus status,
        ref List<string> tags,
        ref List<DocumentLink> links,
        ref DateTime createdAt,
        ref DateTime updatedAt,
        ref string author)
    {
        var lines = frontmatter.Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];
            i++;

            if (string.IsNullOrWhiteSpace(line)) continue;

            var colonIdx = line.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx < 0) continue;

            var key = line[..colonIdx].Trim().ToLowerInvariant();
            var value = line[(colonIdx + 1)..].Trim();

            switch (key)
            {
                case "title":
                    title = StripQuotes(value);
                    break;

                case "type":
                    if (Enum.TryParse<DocumentType>(StripQuotes(value), ignoreCase: true, out var parsedType))
                        type = parsedType;
                    break;

                case "status":
                    if (Enum.TryParse<DocumentStatus>(StripQuotes(value), ignoreCase: true, out var parsedStatus))
                        status = parsedStatus;
                    break;

                case "author":
                    author = StripQuotes(value);
                    break;

                case "created":
                    if (DateTime.TryParse(StripQuotes(value), out var created))
                        createdAt = DateTime.SpecifyKind(created, DateTimeKind.Utc);
                    break;

                case "updated":
                    if (DateTime.TryParse(StripQuotes(value), out var updated))
                        updatedAt = DateTime.SpecifyKind(updated, DateTimeKind.Utc);
                    break;

                case "tags":
                    tags = ParseInlineList(value);
                    break;

                case "links":
                    // Parse block list of link objects
                    links = ParseLinksBlock(lines, ref i);
                    break;
            }
        }
    }

    private static List<string> ParseInlineList(string value)
    {
        // Handles: [item1, item2, item3] or just empty
        value = value.Trim();
        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            value = value[1..^1];
            return [.. value.Split(',').Select(s => StripQuotes(s.Trim())).Where(s => !string.IsNullOrEmpty(s))];
        }
        if (!string.IsNullOrEmpty(value))
            return [StripQuotes(value)];
        return [];
    }

    private static List<DocumentLink> ParseLinksBlock(string[] lines, ref int index)
    {
        var result = new List<DocumentLink>();

        // Read indented sub-entries starting with "  - "
        while (index < lines.Length)
        {
            var line = lines[index];

            // A non-indented line (or a new top-level key) ends the block
            if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("  ", StringComparison.Ordinal) && !line.StartsWith("\t", StringComparison.Ordinal))
                break;

            index++;

            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
                continue;

            // Parse the link entry: collect subsequent indented key: value pairs
            var entryLines = new List<string> { trimmed[2..] };

            while (index < lines.Length)
            {
                var nextLine = lines[index];
                if (string.IsNullOrWhiteSpace(nextLine))
                {
                    index++;
                    break;
                }

                // Next entry or top-level key
                if (!nextLine.StartsWith("    ", StringComparison.Ordinal) && !nextLine.StartsWith("\t  ", StringComparison.Ordinal))
                    break;

                entryLines.Add(nextLine.TrimStart());
                index++;
            }

            var link = ParseLinkEntry(entryLines);
            if (link is not null)
                result.Add(link);
        }

        return result;
    }

    private static DocumentLink? ParseLinkEntry(List<string> entryLines)
    {
        string? targetId = null;
        LinkType linkType = LinkType.Related;
        string? description = null;

        foreach (var line in entryLines)
        {
            var colonIdx = line.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx < 0) continue;

            var key = line[..colonIdx].Trim().ToLowerInvariant();
            var value = line[(colonIdx + 1)..].Trim();

            switch (key)
            {
                case "target":
                    targetId = StripQuotes(value);
                    break;
                case "type":
                    Enum.TryParse<LinkType>(StripQuotes(value), ignoreCase: true, out linkType);
                    break;
                case "description":
                    description = StripQuotes(value);
                    break;
            }
        }

        return targetId is not null ? new DocumentLink(targetId, linkType, description) : null;
    }

    private static string StripQuotes(string value)
    {
        value = value.Trim();
        if ((value.StartsWith('"') && value.EndsWith('"')) ||
            (value.StartsWith('\'') && value.EndsWith('\'')))
            return value[1..^1];
        return value;
    }

    private static string SerializeDocument(KnowledgeDocument doc)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"title: {doc.Title}");
        sb.AppendLine($"type: {doc.Type.ToString().ToLowerInvariant()}");
        sb.AppendLine($"status: {doc.Status.ToString().ToLowerInvariant()}");
        sb.AppendLine($"author: {doc.Author}");

        if (doc.Tags.Count > 0)
            sb.AppendLine($"tags: [{string.Join(", ", doc.Tags)}]");
        else
            sb.AppendLine("tags: []");

        if (doc.Links.Count > 0)
        {
            sb.AppendLine("links:");
            foreach (var link in doc.Links)
            {
                sb.AppendLine($"  - target: {link.TargetId}");
                sb.AppendLine($"    type: {link.Type.ToString().ToLowerInvariant()}");
                if (!string.IsNullOrEmpty(link.Description))
                    sb.AppendLine($"    description: {link.Description}");
            }
        }
        else
        {
            sb.AppendLine("links: []");
        }

        sb.AppendLine($"created: {doc.CreatedAt:yyyy-MM-dd}");
        sb.AppendLine($"updated: {doc.UpdatedAt:yyyy-MM-dd}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(doc.Content);

        return sb.ToString();
    }

    private void AddToReverseIndex(string sourceId, string targetId, LinkType type)
    {
        if (!_reverseIndex.TryGetValue(targetId, out var entries))
        {
            entries = [];
            _reverseIndex[targetId] = entries;
        }

        // Deduplicate
        if (!entries.Any(e => e.SourceId == sourceId && e.Type == type))
            entries.Add((sourceId, type));
    }

    private void RemoveFromReverseIndex(string sourceId, string targetId, LinkType type)
    {
        if (_reverseIndex.TryGetValue(targetId, out var entries))
        {
            entries.RemoveAll(e => e.SourceId == sourceId && e.Type == type);
            if (entries.Count == 0)
                _reverseIndex.Remove(targetId);
        }
    }

    private List<KnowledgeDocument> GetInverseDocuments(string id, LinkType type)
    {
        if (!_reverseIndex.TryGetValue(id, out var entries))
            return [];

        return entries
            .Where(e => e.Type == type)
            .Select(e => _documents.TryGetValue(e.SourceId, out var doc) ? doc : null)
            .OfType<KnowledgeDocument>()
            .ToList();
    }
}

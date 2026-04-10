namespace CopilotHive.Knowledge;

/// <summary>
/// Represents a single knowledge document stored as a markdown file with YAML frontmatter
/// in the config repo under the <c>knowledge/</c> directory.
/// </summary>
public sealed class KnowledgeDocument
{
    /// <summary>Unique identifier derived from the file path (e.g. "architecture-brain").</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable title from the YAML frontmatter.</summary>
    public required string Title { get; set; }

    /// <summary>Top-level topic derived from the first directory segment (e.g. "architecture").</summary>
    public required string Topic { get; init; }

    /// <summary>Second-level topic from the second directory segment, or null for flat documents.</summary>
    public string? Subtopic { get; init; }

    /// <summary>Document type indicating its purpose.</summary>
    public required DocumentType Type { get; set; }

    /// <summary>Current lifecycle status of the document.</summary>
    public required DocumentStatus Status { get; set; }

    /// <summary>Markdown body content (everything after the YAML frontmatter block).</summary>
    public string Content { get; set; } = "";

    /// <summary>Tags used for search and categorization.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Outgoing links to other documents. Reverse links are computed by the graph.</summary>
    public List<DocumentLink> Links { get; set; } = [];

    /// <summary>UTC timestamp when this document was created.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>UTC timestamp when this document was last updated.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Author of this document (e.g. "composer", "human").</summary>
    public string Author { get; set; } = "composer";

    /// <summary>Relative path under the config repo root (e.g. "knowledge/architecture/brain.md").</summary>
    public required string FilePath { get; init; }
}

namespace CopilotHive.Knowledge;

/// <summary>
/// Represents a directed link from one knowledge document to another.
/// </summary>
/// <param name="TargetId">The ID of the target document.</param>
/// <param name="Type">The relationship type.</param>
/// <param name="Description">Optional description of the relationship.</param>
public sealed record DocumentLink(string TargetId, LinkType Type, string? Description = null);

/// <summary>
/// Represents an incoming link to a knowledge document — a link originating from another document.
/// </summary>
/// <param name="SourceId">The ID of the document that has this outgoing link.</param>
/// <param name="Type">The relationship type as declared on the outgoing link.</param>
/// <param name="Description">Optional description from the outgoing link.</param>
public sealed record IncomingLink(string SourceId, LinkType Type, string? Description = null);

/// <summary>The category of knowledge this document represents.</summary>
public enum DocumentType
{
    /// <summary>Describes current code or architecture.</summary>
    Implementation,
    /// <summary>Planned or in-progress feature design.</summary>
    Feature,
    /// <summary>Unformed concept needing exploration.</summary>
    Idea,
    /// <summary>Working notes or temporary content (not auto-archived).</summary>
    Scratch,
    /// <summary>Persistent facts or decisions for LLM recall.</summary>
    Memory,
}

/// <summary>Lifecycle status of a knowledge document.</summary>
public enum DocumentStatus
{
    /// <summary>The document is a draft in progress.</summary>
    Draft,
    /// <summary>The document is current and active.</summary>
    Active,
    /// <summary>The document has been archived and is no longer current.</summary>
    Archived,
    /// <summary>The document has been superseded by a newer document.</summary>
    Superseded,
}

/// <summary>The semantic relationship type of a directed link between documents.</summary>
public enum LinkType
{
    /// <summary>"I am a subtopic of X" — inverse: X has child Y.</summary>
    Parent,
    /// <summary>"I replace X" — inverse: X is superseded-by Y.</summary>
    Supersedes,
    /// <summary>"I require X" — inverse: X is depended-on-by Y.</summary>
    DependsOn,
    /// <summary>"I implement design X" — inverse: X is implemented-by Y.</summary>
    Implements,
    /// <summary>"I am related to X" — symmetric.</summary>
    Related,
    /// <summary>"I cite X" — no inverse needed.</summary>
    References,
}

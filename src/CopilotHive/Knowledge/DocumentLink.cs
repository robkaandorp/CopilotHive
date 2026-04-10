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
/// The <see cref="Type"/> is the <em>inverse</em> of the outgoing link type (e.g. if document B
/// links to A with <see cref="LinkType.Parent"/>, the incoming link on A has type
/// <see cref="LinkType.Child"/>).
/// </summary>
/// <param name="SourceId">The ID of the document that has this outgoing link.</param>
/// <param name="Type">The inverse relationship type (from the perspective of the target document).</param>
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
    /// <summary>Outgoing: "I am a subtopic of X". Inverse: <see cref="Child"/>.</summary>
    Parent,
    /// <summary>Inverse of <see cref="Parent"/>: "X is a subtopic of me".</summary>
    Child,
    /// <summary>Outgoing: "I replace X". Inverse: <see cref="SupersededBy"/>.</summary>
    Supersedes,
    /// <summary>Inverse of <see cref="Supersedes"/>: "I have been superseded by X".</summary>
    SupersededBy,
    /// <summary>Outgoing: "I require X". Inverse: <see cref="DependedOnBy"/>.</summary>
    DependsOn,
    /// <summary>Inverse of <see cref="DependsOn"/>: "X depends on me".</summary>
    DependedOnBy,
    /// <summary>Outgoing: "I implement design X". Inverse: <see cref="ImplementedBy"/>.</summary>
    Implements,
    /// <summary>Inverse of <see cref="Implements"/>: "X implements me".</summary>
    ImplementedBy,
    /// <summary>Symmetric: "I am related to X". Inverse: <see cref="Related"/>.</summary>
    Related,
    /// <summary>Outgoing: "I cite X". Inverse: <see cref="ReferencedBy"/>.</summary>
    References,
    /// <summary>Inverse of <see cref="References"/>: "X cites me".</summary>
    ReferencedBy,
}

/// <summary>Extension methods for <see cref="LinkType"/>.</summary>
public static class LinkTypeExtensions
{
    /// <summary>
    /// Returns the inverse relationship type — the type that describes the link
    /// from the perspective of the <em>target</em> document.
    /// </summary>
    public static LinkType Inverse(this LinkType type) => type switch
    {
        LinkType.Parent       => LinkType.Child,
        LinkType.Child        => LinkType.Parent,
        LinkType.Supersedes   => LinkType.SupersededBy,
        LinkType.SupersededBy => LinkType.Supersedes,
        LinkType.DependsOn    => LinkType.DependedOnBy,
        LinkType.DependedOnBy => LinkType.DependsOn,
        LinkType.Implements   => LinkType.ImplementedBy,
        LinkType.ImplementedBy => LinkType.Implements,
        LinkType.Related      => LinkType.Related,
        LinkType.References   => LinkType.ReferencedBy,
        LinkType.ReferencedBy => LinkType.References,
        _ => throw new InvalidOperationException($"Unknown LinkType: {type}"),
    };
}


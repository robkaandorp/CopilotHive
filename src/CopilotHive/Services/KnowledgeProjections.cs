using CopilotHive.Knowledge;

namespace CopilotHive.Services;

/// <summary>
/// ONE DETACHED knowledge-document row as the <c>Knowledge</c> page renders it in its folder tree and
/// table: identity, placement, classification badges, tags and the sort/display timestamp.
/// <para>
/// DETACHED BY CONSTRUCTION: every value is copied out of the live
/// <see cref="KnowledgeDocument"/> graph and the tag list is a NEW list, so a caller can neither
/// mutate the graph through this projection nor observe a later in-place graph mutation through it.
/// The live mutable <see cref="KnowledgeDocument"/> (and its mutable
/// <see cref="KnowledgeDocument.Tags"/>/<see cref="KnowledgeDocument.Links"/> collections) is
/// deliberately never returned by <see cref="IOperationalFacade"/>.
/// </para>
/// </summary>
/// <param name="Id">The document's unique identifier (also its route segment).</param>
/// <param name="Title">Human-readable title from the document's frontmatter.</param>
/// <param name="Topic">Top-level topic (the folder group the page buckets by).</param>
/// <param name="Subtopic">Second-level topic, or <c>null</c> for a flat document.</param>
/// <param name="Type">Document type badge.</param>
/// <param name="Status">Lifecycle status badge.</param>
/// <param name="Tags">A DETACHED copy of the document's tags.</param>
/// <param name="UpdatedAt">UTC timestamp of the last update (the table's "Updated" column).</param>
public sealed record KnowledgeDocumentSummary(
    string Id,
    string Title,
    string Topic,
    string? Subtopic,
    DocumentType Type,
    DocumentStatus Status,
    IReadOnlyList<string> Tags,
    DateTime UpdatedAt)
{
    /// <summary>
    /// Projects one live document onto its detached row representation, copying the tag list.
    /// </summary>
    /// <param name="document">The live document to project (never retained).</param>
    /// <returns>The detached summary row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static KnowledgeDocumentSummary From(KnowledgeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new(
            document.Id,
            document.Title,
            document.Topic,
            document.Subtopic,
            document.Type,
            document.Status,
            DetachedKnowledgeCopy.Of(document.Tags),
            document.UpdatedAt);
    }
}

/// <summary>
/// THE ONE detachment helper for knowledge projections: copies a live mutable collection into a
/// read-only copy that the caller cannot mutate (and that a later in-place graph mutation cannot
/// change behind the caller's back).
/// </summary>
internal static class DetachedKnowledgeCopy
{
    /// <summary>Copies <paramref name="values"/> into a read-only snapshot.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="values">The live collection to copy (never retained).</param>
    /// <returns>A read-only copy of the values, in the original order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is null.</exception>
    internal static IReadOnlyList<T> Of<T>(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return Array.AsReadOnly(values.ToArray());
    }
}

/// <summary>
/// ONE DETACHED knowledge document as the <c>KnowledgeDetail</c> page renders it: the row fields plus
/// the author, both timestamps and the markdown body.
/// <para>
/// Same detachment contract as <see cref="KnowledgeDocumentSummary"/>: this is a value copy, never the
/// live mutable <see cref="KnowledgeDocument"/>.
/// </para>
/// </summary>
/// <param name="Id">The document's unique identifier.</param>
/// <param name="Title">Human-readable title.</param>
/// <param name="Topic">Top-level topic.</param>
/// <param name="Subtopic">Second-level topic, or <c>null</c>.</param>
/// <param name="Type">Document type badge.</param>
/// <param name="Status">Lifecycle status badge.</param>
/// <param name="Author">Author recorded in the frontmatter.</param>
/// <param name="CreatedAt">UTC creation timestamp.</param>
/// <param name="UpdatedAt">UTC last-update timestamp.</param>
/// <param name="Content">The markdown body (rendered by the page).</param>
/// <param name="Tags">A DETACHED copy of the document's tags.</param>
public sealed record KnowledgeDocumentDetail(
    string Id,
    string Title,
    string Topic,
    string? Subtopic,
    DocumentType Type,
    DocumentStatus Status,
    string Author,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string Content,
    IReadOnlyList<string> Tags)
{
    /// <summary>
    /// Projects one live document onto its detached detail representation, copying the tag list.
    /// </summary>
    /// <param name="document">The live document to project (never retained).</param>
    /// <returns>The detached detail projection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static KnowledgeDocumentDetail From(KnowledgeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new(
            document.Id,
            document.Title,
            document.Topic,
            document.Subtopic,
            document.Type,
            document.Status,
            document.Author,
            document.CreatedAt,
            document.UpdatedAt,
            document.Content,
            DetachedKnowledgeCopy.Of(document.Tags));
    }
}

/// <summary>
/// ONE DETACHED outgoing link of a knowledge document, with the TARGET document's title already
/// resolved by <see cref="IOperationalFacade"/> WHILE THE CALLER WAS AUTHORIZED.
/// <para>
/// The page never performs the lookup itself: resolving a title is a graph read, so it happens
/// inside the authorized operation. <see cref="TargetTitle"/> is <c>null</c> when the target document
/// does not exist (the page then renders only the link target id, exactly as before).
/// </para>
/// </summary>
/// <param name="TargetId">The linked target document's identifier.</param>
/// <param name="TargetTitle">The resolved target title, or <c>null</c> when it does not exist.</param>
/// <param name="Type">The relationship type.</param>
/// <param name="Description">Optional description recorded on the link.</param>
public sealed record KnowledgeOutgoingLink(
    string TargetId,
    string? TargetTitle,
    LinkType Type,
    string? Description);

/// <summary>
/// ONE DETACHED incoming link of a knowledge document, with the SOURCE document's title already
/// resolved by <see cref="IOperationalFacade"/> while the caller was authorized.
/// </summary>
/// <param name="SourceId">The document that owns the outgoing link.</param>
/// <param name="SourceTitle">The resolved source title, or <c>null</c> when it does not exist.</param>
/// <param name="Type">The INVERSE relationship type (from this document's perspective).</param>
/// <param name="Description">Optional description carried over from the outgoing link.</param>
public sealed record KnowledgeIncomingLink(
    string SourceId,
    string? SourceTitle,
    LinkType Type,
    string? Description);

/// <summary>
/// THE DETACHED DETAIL PROJECTION of one knowledge document: the document itself plus its outgoing
/// and incoming links. Every part is a value copy produced inside the authorized operation, so no
/// live graph, document or mutable collection ever crosses the facade boundary.
/// </summary>
/// <param name="Document">The detached document projection.</param>
/// <param name="OutgoingLinks">The document's detached outgoing links.</param>
/// <param name="IncomingLinks">The document's detached incoming links.</param>
public sealed record KnowledgeDocumentProjection(
    KnowledgeDocumentDetail Document,
    IReadOnlyList<KnowledgeOutgoingLink> OutgoingLinks,
    IReadOnlyList<KnowledgeIncomingLink> IncomingLinks);

/// <summary>
/// THE DETACHED LIST/SEARCH PROJECTION of the knowledge graph: whether a graph is configured at all,
/// plus the projected rows (the caller's search query, when supplied, has already been applied by
/// <see cref="KnowledgeGraph.Search(string)"/> semantics).
/// </summary>
/// <param name="IsConfigured">
/// <c>false</c> when the orchestrator was started without a config repo, so the page renders its
/// existing "not configured" surface. An unconfigured graph is a SUCCESS answer, never a denial.
/// </param>
/// <param name="Documents">The detached rows (empty when the graph is unconfigured).</param>
public sealed record KnowledgeDocumentListProjection(
    bool IsConfigured,
    IReadOnlyList<KnowledgeDocumentSummary> Documents);

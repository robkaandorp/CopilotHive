using CopilotHive.Configuration;
using CopilotHive.Knowledge;

namespace CopilotHive.Tests.Knowledge;

public sealed class KnowledgeGraphTests : IDisposable
{
    private readonly string _tempDir;
    private readonly KnowledgeGraph _graph;

    public KnowledgeGraphTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"KGTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _graph = new KnowledgeGraph();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    // ── DeriveDocumentIdFromPath ──────────────────────────────────────────────

    [Theory]
    [InlineData("knowledge/architecture/brain.md", "architecture-brain")]
    [InlineData("knowledge/features/knowledge-graph.md", "features-knowledge-graph")]
    [InlineData("knowledge/architecture/distributed-systems/brain-session-per-goal.md",
        "architecture-distributed-systems-brain-session-per-goal")]
    [InlineData("knowledge/memory/coding-standards.md", "memory-coding-standards")]
    [InlineData("knowledge/scratch/2025-01-15-refactoring-plan.md", "scratch-2025-01-15-refactoring-plan")]
    public void DeriveDocumentIdFromPath_VariousPaths_ReturnsExpectedId(string path, string expectedId)
    {
        var id = KnowledgeGraph.DeriveDocumentIdFromPath(path);
        Assert.Equal(expectedId, id);
    }

    [Fact]
    public void DeriveDocumentIdFromPath_PathWithBackslashes_NormalizesSlashes()
    {
        var id = KnowledgeGraph.DeriveDocumentIdFromPath(@"knowledge\architecture\brain.md");
        Assert.Equal("architecture-brain", id);
    }

    [Fact]
    public void DeriveDocumentIdFromPath_LeadingSlash_Stripped()
    {
        var id = KnowledgeGraph.DeriveDocumentIdFromPath("/knowledge/architecture/brain.md");
        Assert.Equal("architecture-brain", id);
    }

    [Fact]
    public void DeriveDocumentIdFromPath_TrailingSlash_Stripped()
    {
        var id = KnowledgeGraph.DeriveDocumentIdFromPath("knowledge/architecture/brain.md/");
        // trailing slash after stripping .md would be gone after trim
        // the actual result depends on whether the slash was before .md
        // "knowledge/architecture/brain.md/" → strip leading/trailing slashes → "knowledge/architecture/brain.md"
        Assert.Equal("architecture-brain", id);
    }

    [Fact]
    public void DeriveDocumentIdFromPath_NoKnowledgePrefix_ReturnsIdDirectly()
    {
        // If someone passes a raw path without the prefix, it should still process
        var id = KnowledgeGraph.DeriveDocumentIdFromPath("architecture/brain.md");
        Assert.Equal("architecture-brain", id);
    }

    // ── CreateDocumentAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task CreateDocumentAsync_NewDocument_StoredInGraph()
    {
        var ct = TestContext.Current.CancellationToken;
        var doc = await _graph.CreateDocumentAsync("features-test", "Test Feature",
            DocumentType.Feature, "Some content", ct: ct);

        Assert.Equal("features-test", doc.Id);
        Assert.Equal("Test Feature", doc.Title);
        Assert.Equal(DocumentType.Feature, doc.Type);
        Assert.Equal(DocumentStatus.Draft, doc.Status);
        Assert.Equal("Some content", doc.Content);
        Assert.Equal("features", doc.Topic);
        Assert.Null(doc.Subtopic); // no subtopic passed — defaults to null

        var fetched = _graph.GetDocument("features-test");
        Assert.NotNull(fetched);
        Assert.Equal("Test Feature", fetched.Title);
    }

    [Fact]
    public async Task CreateDocumentAsync_DuplicateId_ThrowsInvalidOperation()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("features-test", "Test Feature", DocumentType.Feature, "", ct: ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _graph.CreateDocumentAsync("features-test", "Duplicate", DocumentType.Feature, "", ct: ct));
    }

    [Fact]
    public async Task CreateDocumentAsync_WithTags_TagsStored()
    {
        var ct = TestContext.Current.CancellationToken;
        var doc = await _graph.CreateDocumentAsync("arch-brain", "Brain",
            DocumentType.Implementation, "content", tags: ["brain", "planning"], ct: ct);

        Assert.Contains("brain", doc.Tags);
        Assert.Contains("planning", doc.Tags);
    }

    // ── GetDocument ───────────────────────────────────────────────────────────

    [Fact]
    public void GetDocument_NotFound_ReturnsNull()
    {
        var result = _graph.GetDocument("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetDocument_AfterCreate_ReturnsDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-brain", "Brain", DocumentType.Implementation, "body", ct: ct);

        var doc = _graph.GetDocument("arch-brain");
        Assert.NotNull(doc);
        Assert.Equal("Brain", doc.Title);
    }

    // ── UpdateDocumentAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task UpdateDocumentAsync_UpdatesContent()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-brain", "Brain", DocumentType.Implementation, "old content", ct: ct);

        await _graph.UpdateDocumentAsync("arch-brain", content: "new content", ct: ct);

        var doc = _graph.GetDocument("arch-brain");
        Assert.Equal("new content", doc!.Content);
    }

    [Fact]
    public async Task UpdateDocumentAsync_UpdatesStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-brain", "Brain", DocumentType.Implementation, "", ct: ct);

        await _graph.UpdateDocumentAsync("arch-brain", status: DocumentStatus.Active, ct: ct);

        var doc = _graph.GetDocument("arch-brain");
        Assert.Equal(DocumentStatus.Active, doc!.Status);
    }

    [Fact]
    public async Task UpdateDocumentAsync_NotFound_ThrowsKeyNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _graph.UpdateDocumentAsync("ghost-doc", content: "x", ct: TestContext.Current.CancellationToken));
    }

    // ── DeleteDocumentAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task DeleteDocumentAsync_RemovesDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-brain", "Brain", DocumentType.Implementation, "", ct: ct);

        await _graph.DeleteDocumentAsync("arch-brain", ct);

        Assert.Null(_graph.GetDocument("arch-brain"));
    }

    [Fact]
    public async Task DeleteDocumentAsync_AlsoRemovesFromReverseIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-brain", "Brain", DocumentType.Implementation, "", ct: ct);
        await _graph.CreateDocumentAsync("features-new", "New Feature", DocumentType.Feature, "", ct: ct);

        _graph.AddLink("features-new", new DocumentLink("arch-brain", LinkType.Parent));

        Assert.Single(_graph.GetChildren("arch-brain"));

        await _graph.DeleteDocumentAsync("features-new", ct);

        // After deleting the source doc, children list for arch-brain should be empty
        Assert.Empty(_graph.GetChildren("arch-brain"));
    }

    [Fact]
    public async Task DeleteDocumentAsync_NotFound_ThrowsKeyNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _graph.DeleteDocumentAsync("nonexistent", TestContext.Current.CancellationToken));
    }

    // ── AddLink ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddLink_UpdatesForwardAndReverseIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-brain", "Brain", DocumentType.Implementation, "", ct: ct);
        await _graph.CreateDocumentAsync("features-compaction", "Compaction", DocumentType.Feature, "", ct: ct);

        _graph.AddLink("arch-brain", new DocumentLink("features-compaction", LinkType.Related));

        var brain = _graph.GetDocument("arch-brain");
        Assert.Single(brain!.Links);
        Assert.Equal("features-compaction", brain.Links[0].TargetId);
        Assert.Equal(LinkType.Related, brain.Links[0].Type);

        // Verify reverse index: compaction is related-to from brain
        var related = _graph.GetRelated("arch-brain");
        Assert.Contains(related, d => d.Id == "features-compaction");
    }

    [Fact]
    public async Task AddLink_Deduplicates_ByTargetAndType()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-brain", "Brain", DocumentType.Implementation, "", ct: ct);
        await _graph.CreateDocumentAsync("features-x", "X", DocumentType.Feature, "", ct: ct);

        _graph.AddLink("arch-brain", new DocumentLink("features-x", LinkType.Related, "desc1"));
        _graph.AddLink("arch-brain", new DocumentLink("features-x", LinkType.Related, "desc2"));

        var brain = _graph.GetDocument("arch-brain");
        Assert.Single(brain!.Links);
        Assert.Equal("desc2", brain.Links[0].Description); // last one wins
    }

    [Fact]
    public async Task AddLink_NotFound_ThrowsKeyNotFound()
    {
        Assert.Throws<KeyNotFoundException>(() =>
            _graph.AddLink("nonexistent", new DocumentLink("target", LinkType.Related)));
    }

    // ── RemoveLink ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveLink_RemovesFromForwardAndReverseIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-brain", "Brain", DocumentType.Implementation, "", ct: ct);
        await _graph.CreateDocumentAsync("features-compaction", "Compaction", DocumentType.Feature, "", ct: ct);

        _graph.AddLink("arch-brain", new DocumentLink("features-compaction", LinkType.Related));
        _graph.RemoveLink("arch-brain", "features-compaction", LinkType.Related);

        var brain = _graph.GetDocument("arch-brain");
        Assert.Empty(brain!.Links);

        var related = _graph.GetRelated("arch-brain");
        Assert.Empty(related);
    }

    // ── Search ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_ByTitle_ReturnsMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-brain", "Brain Architecture",
            DocumentType.Implementation, "body", ct: ct);
        await _graph.CreateDocumentAsync("features-x", "Unrelated", DocumentType.Feature, "body", ct: ct);

        var results = _graph.Search("Brain");

        Assert.Single(results);
        Assert.Equal("arch-brain", results[0].Id);
    }

    [Fact]
    public async Task Search_ByContent_ReturnsMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-brain", "Title",
            DocumentType.Implementation, "The quick brown fox", ct: ct);
        await _graph.CreateDocumentAsync("features-x", "Other", DocumentType.Feature, "Nothing here", ct: ct);

        var results = _graph.Search("quick brown");

        Assert.Single(results);
        Assert.Equal("arch-brain", results[0].Id);
    }

    [Fact]
    public async Task Search_ByTag_ReturnsMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-brain", "Brain",
            DocumentType.Implementation, "", tags: ["orchestration", "planning"], ct: ct);
        await _graph.CreateDocumentAsync("features-x", "Other", DocumentType.Feature, "", ct: ct);

        var results = _graph.Search("orchestration");

        Assert.Single(results);
        Assert.Equal("arch-brain", results[0].Id);
    }

    [Fact]
    public async Task Search_CaseInsensitive_ReturnsMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-brain", "Brain Architecture",
            DocumentType.Implementation, "content", ct: ct);

        var results = _graph.Search("BRAIN");
        Assert.Single(results);
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsAll()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("doc-a", "A", DocumentType.Implementation, "", ct: ct);
        await _graph.CreateDocumentAsync("doc-b", "B", DocumentType.Feature, "", ct: ct);

        var results = _graph.Search("");
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task Search_ByMultipleTerms_ReturnsIntersection()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-idea-impl", "Idea-to-Implementation Transition",
            DocumentType.Implementation, "content", ct: ct);
        await _graph.CreateDocumentAsync("features-idea", "Idea Collection",
            DocumentType.Feature, "content", ct: ct);

        var results = _graph.Search("idea implementation");

        Assert.Single(results);
        Assert.Equal("arch-idea-impl", results[0].Id);
    }

    [Fact]
    public async Task Search_TokenizesHyphenatedWords()
    {
        var ct = TestContext.Current.CancellationToken;
        // Use neutral title and id that contain no part of the search query
        await _graph.CreateDocumentAsync("cfg-doc", "General Setup",
            DocumentType.Feature, "In-App Configuration", ct: ct);

        var results = _graph.Search("in app");

        Assert.Single(results);
        Assert.Equal("cfg-doc", results[0].Id);
        // Verify the match came from content, not title or id
        Assert.Contains("In-App Configuration", results[0].Content);
        Assert.DoesNotContain("in", results[0].Title.ToLowerInvariant());
        Assert.DoesNotContain("app", results[0].Title.ToLowerInvariant());
    }

    [Fact]
    public async Task Search_TokenizesQueryPunctuation()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-brain", "Brain Architecture",
            DocumentType.Implementation, "body", ct: ct);

        var results = _graph.Search("brain, architecture!");

        Assert.Single(results);
        Assert.Equal("arch-brain", results[0].Id);
    }

    [Fact]
    public async Task Search_ById_ReturnsMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("memory-idea-to-implementation-transition",
            "Memory Doc", DocumentType.Memory, "some content", ct: ct);

        var results = _graph.Search("idea implementation");

        Assert.Single(results);
        Assert.Equal("memory-idea-to-implementation-transition", results[0].Id);
    }

    [Fact]
    public async Task Search_PartialTermMatchInContent()
    {
        var ct = TestContext.Current.CancellationToken;
        // Use neutral title and id that contain no part of the search query
        await _graph.CreateDocumentAsync("proc-doc", "Workflow Steps",
            DocumentType.Implementation, "The orchestration system", ct: ct);

        var results = _graph.Search("orchestr");

        Assert.Single(results);
        Assert.Equal("proc-doc", results[0].Id);
        // Verify the match came from content, not title or id
        Assert.Contains("orchestration", results[0].Content);
        Assert.DoesNotContain("orchestr", results[0].Title.ToLowerInvariant());
    }

    // ── FindByTopic ───────────────────────────────────────────────────────────

    [Fact]
    public async Task FindByTopic_ReturnsOnlyMatchingTopic()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("architecture-brain", "Brain",
            DocumentType.Implementation, "", ct: ct);
        await _graph.CreateDocumentAsync("features-kg", "KG", DocumentType.Feature, "", ct: ct);

        var results = _graph.FindByTopic("architecture");
        Assert.Single(results);
        Assert.Equal("architecture-brain", results[0].Id);
    }

    // ── FindByType ────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindByType_ReturnsOnlyMatchingType()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-brain", "Brain", DocumentType.Implementation, "", ct: ct);
        await _graph.CreateDocumentAsync("features-kg", "KG", DocumentType.Feature, "", ct: ct);

        var results = _graph.FindByType(DocumentType.Implementation);
        Assert.Single(results);
        Assert.Equal("arch-brain", results[0].Id);
    }

    // ── FindByTag ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindByTag_ReturnsMatchingDocuments()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-brain", "Brain",
            DocumentType.Implementation, "", tags: ["brain", "planning"], ct: ct);
        await _graph.CreateDocumentAsync("features-kg", "KG",
            DocumentType.Feature, "", tags: ["knowledge", "planning"], ct: ct);

        var results = _graph.FindByTag("planning");
        Assert.Equal(2, results.Count);
    }

    // ── FindByStatus ──────────────────────────────────────────────────────────

    [Fact]
    public async Task FindByStatus_ReturnsMatchingDocuments()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-brain", "Brain", DocumentType.Implementation, "", ct: ct);
        await _graph.UpdateDocumentAsync("arch-brain", status: DocumentStatus.Active, ct: ct);
        await _graph.CreateDocumentAsync("features-kg", "KG", DocumentType.Feature, "", ct: ct);
        // features-kg stays Draft

        var activeResults = _graph.FindByStatus(DocumentStatus.Active);
        Assert.Single(activeResults);
        Assert.Equal("arch-brain", activeResults[0].Id);

        var draftResults = _graph.FindByStatus(DocumentStatus.Draft);
        Assert.Single(draftResults);
        Assert.Equal("features-kg", draftResults[0].Id);
    }

    // ── GetChildren ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetChildren_FromParentLinks_ReturnsChildren()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-brain", "Brain", DocumentType.Implementation, "", ct: ct);
        await _graph.CreateDocumentAsync("arch-brain-session", "Session",
            DocumentType.Implementation, "", ct: ct);
        await _graph.CreateDocumentAsync("arch-brain-compaction", "Compaction",
            DocumentType.Implementation, "", ct: ct);

        // Both children declare arch-brain as parent
        _graph.AddLink("arch-brain-session", new DocumentLink("arch-brain", LinkType.Parent));
        _graph.AddLink("arch-brain-compaction", new DocumentLink("arch-brain", LinkType.Parent));

        var children = _graph.GetChildren("arch-brain");
        Assert.Equal(2, children.Count);
        Assert.Contains(children, d => d.Id == "arch-brain-session");
        Assert.Contains(children, d => d.Id == "arch-brain-compaction");
    }

    [Fact]
    public async Task GetChildren_NoParentLinks_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-brain", "Brain", DocumentType.Implementation, "", ct: ct);

        var children = _graph.GetChildren("arch-brain");
        Assert.Empty(children);
    }

    // ── GetSupersededBy ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetSupersededBy_FromSupersedesLinks_ReturnsNewer()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-brain-v1", "Brain V1", DocumentType.Implementation, "", ct: ct);
        await _graph.CreateDocumentAsync("arch-brain-v2", "Brain V2", DocumentType.Implementation, "", ct: ct);

        _graph.AddLink("arch-brain-v2", new DocumentLink("arch-brain-v1", LinkType.Supersedes));

        var supersededBy = _graph.GetSupersededBy("arch-brain-v1");
        Assert.Single(supersededBy);
        Assert.Equal("arch-brain-v2", supersededBy[0].Id);
    }

    // ── GetDependedOnBy ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetDependedOnBy_ReturnsDocumentsWithDependsOnLinkToTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-core", "Core", DocumentType.Implementation, "", ct: ct);
        await _graph.CreateDocumentAsync("features-new", "New Feature", DocumentType.Feature, "", ct: ct);

        // features-new depends on arch-core
        _graph.AddLink("features-new", new DocumentLink("arch-core", LinkType.DependsOn));

        var dependedOnBy = _graph.GetDependedOnBy("arch-core");
        Assert.Single(dependedOnBy);
        Assert.Equal("features-new", dependedOnBy[0].Id);
    }

    [Fact]
    public async Task GetDependedOnBy_NoDependsOnLinks_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-core", "Core", DocumentType.Implementation, "", ct: ct);

        Assert.Empty(_graph.GetDependedOnBy("arch-core"));
    }

    [Fact]
    public async Task GetDependedOnBy_AfterRoundTrip_ReverseIndexRebuilt()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        await graph.CreateDocumentAsync("arch-core", "Core", DocumentType.Implementation, "", ct: ct);
        await graph.CreateDocumentAsync("features-new", "New Feature", DocumentType.Feature, "", ct: ct);
        graph.AddLink("features-new", new DocumentLink("arch-core", LinkType.DependsOn));

        await graph.CommitToConfigRepoAsync(_tempDir, "round-trip", ct);

        var graph2 = new KnowledgeGraph();
        await graph2.ReloadFromConfigRepoAsync(_tempDir, ct);

        var dependedOnBy = graph2.GetDependedOnBy("arch-core");
        Assert.Single(dependedOnBy);
        Assert.Equal("features-new", dependedOnBy[0].Id);
    }

    // ── GetImplementedBy ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetImplementedBy_ReturnsDocumentsWithImplementsLinkToTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("features-spec", "Feature Spec", DocumentType.Feature, "", ct: ct);
        await _graph.CreateDocumentAsync("arch-impl", "Implementation", DocumentType.Implementation, "", ct: ct);

        // arch-impl implements features-spec
        _graph.AddLink("arch-impl", new DocumentLink("features-spec", LinkType.Implements));

        var implementedBy = _graph.GetImplementedBy("features-spec");
        Assert.Single(implementedBy);
        Assert.Equal("arch-impl", implementedBy[0].Id);
    }

    [Fact]
    public async Task GetImplementedBy_NoImplementsLinks_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("features-spec", "Feature Spec", DocumentType.Feature, "", ct: ct);

        Assert.Empty(_graph.GetImplementedBy("features-spec"));
    }

    [Fact]
    public async Task GetImplementedBy_AfterRoundTrip_ReverseIndexRebuilt()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        await graph.CreateDocumentAsync("features-spec", "Feature Spec", DocumentType.Feature, "", ct: ct);
        await graph.CreateDocumentAsync("arch-impl", "Implementation", DocumentType.Implementation, "", ct: ct);
        graph.AddLink("arch-impl", new DocumentLink("features-spec", LinkType.Implements));

        await graph.CommitToConfigRepoAsync(_tempDir, "round-trip-impl", ct);

        var graph2 = new KnowledgeGraph();
        await graph2.ReloadFromConfigRepoAsync(_tempDir, ct);

        var implementedBy = graph2.GetImplementedBy("features-spec");
        Assert.Single(implementedBy);
        Assert.Equal("arch-impl", implementedBy[0].Id);
    }

    [Fact]
    public async Task GetDependedOnBy_IgnoresNonDependsOnLinkTypes_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-core", "Core", DocumentType.Implementation, "", ct: ct);
        await _graph.CreateDocumentAsync("features-new", "New Feature", DocumentType.Feature, "", ct: ct);

        // B implements A — should NOT appear in GetDependedOnBy(A)
        _graph.AddLink("features-new", new DocumentLink("arch-core", LinkType.Implements));
        Assert.Empty(_graph.GetDependedOnBy("arch-core"));

        // B is related to A — should also NOT appear in GetDependedOnBy(A)
        _graph.AddLink("features-new", new DocumentLink("arch-core", LinkType.Related));
        Assert.Empty(_graph.GetDependedOnBy("arch-core"));
    }

    [Fact]
    public async Task GetImplementedBy_IgnoresNonImplementsLinkTypes_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("features-spec", "Feature Spec", DocumentType.Feature, "", ct: ct);
        await _graph.CreateDocumentAsync("arch-impl", "Implementation", DocumentType.Implementation, "", ct: ct);

        // B depends on A — should NOT appear in GetImplementedBy(A)
        _graph.AddLink("arch-impl", new DocumentLink("features-spec", LinkType.DependsOn));
        Assert.Empty(_graph.GetImplementedBy("features-spec"));

        // B is related to A — should also NOT appear in GetImplementedBy(A)
        _graph.AddLink("arch-impl", new DocumentLink("features-spec", LinkType.Related));
        Assert.Empty(_graph.GetImplementedBy("features-spec"));
    }

    // ── GetRelated ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRelated_MaxDepth1_ReturnsDirectLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("doc-a", "A", DocumentType.Implementation, "", ct: ct);
        await _graph.CreateDocumentAsync("doc-b", "B", DocumentType.Feature, "", ct: ct);
        await _graph.CreateDocumentAsync("doc-c", "C", DocumentType.Idea, "", ct: ct);

        _graph.AddLink("doc-a", new DocumentLink("doc-b", LinkType.Related));
        _graph.AddLink("doc-b", new DocumentLink("doc-c", LinkType.Related));

        var related = _graph.GetRelated("doc-a", maxDepth: 1);
        Assert.Single(related);
        Assert.Equal("doc-b", related[0].Id);
    }

    [Fact]
    public async Task GetRelated_MaxDepth2_ReturnsTransitiveLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("doc-a", "A", DocumentType.Implementation, "", ct: ct);
        await _graph.CreateDocumentAsync("doc-b", "B", DocumentType.Feature, "", ct: ct);
        await _graph.CreateDocumentAsync("doc-c", "C", DocumentType.Idea, "", ct: ct);

        _graph.AddLink("doc-a", new DocumentLink("doc-b", LinkType.Related));
        _graph.AddLink("doc-b", new DocumentLink("doc-c", LinkType.Related));

        var related = _graph.GetRelated("doc-a", maxDepth: 2);
        Assert.Equal(2, related.Count);
        Assert.Contains(related, d => d.Id == "doc-b");
        Assert.Contains(related, d => d.Id == "doc-c");
    }

    // ── ReloadFromConfigRepoAsync ─────────────────────────────────────────────

    [Fact]
    public async Task ReloadFromConfigRepoAsync_ParsesMarkdownFiles()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create test knowledge directory with a sample markdown file
        var knowledgePath = Path.Combine(_tempDir, "knowledge", "architecture");
        Directory.CreateDirectory(knowledgePath);

        var mdContent = """
            ---
            title: Brain Architecture
            type: implementation
            status: active
            author: composer
            tags: [brain, planning]
            links:
              - target: features-compaction
                type: related
            created: 2025-01-15
            updated: 2025-01-20
            ---

            # Brain Architecture

            The Brain is an LLM-powered orchestrator.
            """;

        await File.WriteAllTextAsync(Path.Combine(knowledgePath, "brain.md"), mdContent, ct);

        var graph = new KnowledgeGraph();
        await graph.ReloadFromConfigRepoAsync(_tempDir, ct);

        var doc = graph.GetDocument("architecture-brain");
        Assert.NotNull(doc);
        Assert.Equal("Brain Architecture", doc.Title);
        Assert.Equal(DocumentType.Implementation, doc.Type);
        Assert.Equal(DocumentStatus.Active, doc.Status);
        Assert.Equal("composer", doc.Author);
        Assert.Contains("brain", doc.Tags);
        Assert.Contains("planning", doc.Tags);
        Assert.Single(doc.Links);
        Assert.Equal("features-compaction", doc.Links[0].TargetId);
        Assert.Equal(LinkType.Related, doc.Links[0].Type);
        Assert.Contains("Brain is an LLM", doc.Content);
    }

    [Fact]
    public async Task ReloadFromConfigRepoAsync_NoKnowledgeDirectory_EmptyGraph()
    {
        var ct = TestContext.Current.CancellationToken;
        var emptyDir = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(emptyDir);

        var graph = new KnowledgeGraph();
        await graph.ReloadFromConfigRepoAsync(emptyDir, ct);

        Assert.Empty(graph.FindByTopic("anything"));
    }

    [Fact]
    public async Task ReloadFromConfigRepoAsync_BuildsReverseIndex()
    {
        var ct = TestContext.Current.CancellationToken;

        var knowledgePath = Path.Combine(_tempDir, "knowledge", "architecture");
        Directory.CreateDirectory(knowledgePath);

        // Create two documents, one with a Parent link to the other
        var parentDoc = """
            ---
            title: Brain
            type: implementation
            status: active
            tags: []
            links: []
            created: 2025-01-01
            updated: 2025-01-01
            ---
            Parent content.
            """;

        var childDoc = """
            ---
            title: Brain Session
            type: implementation
            status: active
            tags: []
            links:
              - target: architecture-brain
                type: parent
            created: 2025-01-01
            updated: 2025-01-01
            ---
            Child content.
            """;

        await File.WriteAllTextAsync(Path.Combine(knowledgePath, "brain.md"), parentDoc, ct);
        await File.WriteAllTextAsync(Path.Combine(knowledgePath, "brain-session.md"), childDoc, ct);

        var graph = new KnowledgeGraph();
        await graph.ReloadFromConfigRepoAsync(_tempDir, ct);

        var children = graph.GetChildren("architecture-brain");
        Assert.Single(children);
        Assert.Equal("architecture-brain-session", children[0].Id);
    }

    [Fact]
    public async Task ReloadFromConfigRepoAsync_ClearsDirtyTracking()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a doc programmatically (marks it dirty)
        await _graph.CreateDocumentAsync("arch-brain", "Brain", DocumentType.Implementation, "", ct: ct);

        // Create the directory structure matching the reload
        var knowledgePath = Path.Combine(_tempDir, "knowledge");
        Directory.CreateDirectory(knowledgePath);

        // Reload from empty temp dir — should clear dirty docs
        await _graph.ReloadFromConfigRepoAsync(_tempDir, ct);

        // The programmatically added doc should no longer be in the graph
        Assert.Null(_graph.GetDocument("arch-brain"));
    }

    // ── CommitToConfigRepoAsync ───────────────────────────────────────────────

    [Fact]
    public async Task CommitToConfigRepoAsync_WritesDirtyDocsToDisk()
    {
        var ct = TestContext.Current.CancellationToken;

        // Use a graph without a config repo (no git push, just file write)
        var graph = new KnowledgeGraph();
        var doc = await graph.CreateDocumentAsync(
            "features-test", "Test Feature", DocumentType.Feature,
            "# Feature\nSome content.", ct: ct);

        // CommitToConfigRepoAsync writes files to disk (skips git commit if no configRepo)
        await graph.CommitToConfigRepoAsync(_tempDir, "test commit", ct);

        var expectedPath = Path.Combine(_tempDir, doc.FilePath);
        Assert.True(File.Exists(expectedPath), $"Expected file at {expectedPath}");

        var contents = await File.ReadAllTextAsync(expectedPath, ct);
        Assert.Contains("title: Test Feature", contents);
        Assert.Contains("type: feature", contents);
        Assert.Contains("# Feature", contents);
    }

    [Fact]
    public async Task CommitToConfigRepoAsync_NoDirtyDocs_WritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        // Should complete without error even when nothing is dirty
        await graph.CommitToConfigRepoAsync(_tempDir, "empty commit", ct);

        // No files should have been created
        var knowledgeDir = Path.Combine(_tempDir, "knowledge");
        Assert.False(Directory.Exists(knowledgeDir));
    }

    [Fact]
    public async Task CommitToConfigRepoAsync_YamlFrontmatterContainsLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        await graph.CreateDocumentAsync("arch-brain", "Brain",
            DocumentType.Implementation, "Content.", tags: ["brain"], ct: ct);
        await graph.CreateDocumentAsync("features-x", "X", DocumentType.Feature, "", ct: ct);
        graph.AddLink("arch-brain", new DocumentLink("features-x", LinkType.Related, "linked to X"));

        await graph.CommitToConfigRepoAsync(_tempDir, "msg", ct);

        var archBrainDoc = graph.GetDocument("arch-brain");
        var filePath = Path.Combine(_tempDir, archBrainDoc!.FilePath);
        var contents = await File.ReadAllTextAsync(filePath, ct);

        Assert.Contains("- target: features-x", contents);
        Assert.Contains("type: related", contents);
        Assert.Contains("description: linked to X", contents);
    }

    // ── Round-trip ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_WriteAndReload_PreservesData()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        await graph.CreateDocumentAsync("architecture-core", "Core Architecture",
            DocumentType.Implementation, "# Core\nThe core system.",
            author: "human", tags: ["core", "architecture"], ct: ct);
        await graph.CreateDocumentAsync("features-new-feature", "New Feature",
            DocumentType.Feature, "Feature body.", ct: ct);
        graph.AddLink("features-new-feature", new DocumentLink("architecture-core", LinkType.DependsOn));

        // Write to disk
        await graph.CommitToConfigRepoAsync(_tempDir, "round-trip", ct);

        // Reload into a fresh graph
        var graph2 = new KnowledgeGraph();
        await graph2.ReloadFromConfigRepoAsync(_tempDir, ct);

        var core = graph2.GetDocument("architecture-core");
        Assert.NotNull(core);
        Assert.Equal("Core Architecture", core.Title);
        Assert.Equal(DocumentType.Implementation, core.Type);
        Assert.Contains("core", core.Tags);
        Assert.Equal("human", core.Author);

        var feature = graph2.GetDocument("features-new-feature");
        Assert.NotNull(feature);
        Assert.Single(feature.Links);
        Assert.Equal("architecture-core", feature.Links[0].TargetId);
        Assert.Equal(LinkType.DependsOn, feature.Links[0].Type);
    }

    // ── Nested document path ─────────────────────────────────────────────────

    [Fact]
    public async Task CreateDocumentAsync_NestedPath_FileWrittenAtCorrectPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        // Create a deeply nested document with explicit topic and subtopic
        var doc = await graph.CreateDocumentAsync(
            "architecture-distributed-systems-brain-session-per-goal",
            "Brain Session Per Goal",
            DocumentType.Implementation,
            "Session content.",
            topic: "architecture",
            subtopic: "distributed-systems",
            ct: ct);

        // FilePath should reflect the nested path with leaf-only filename
        Assert.Equal(
            "knowledge/architecture/distributed-systems/brain-session-per-goal.md",
            doc.FilePath);

        Assert.Equal("architecture", doc.Topic);
        Assert.Equal("distributed-systems", doc.Subtopic);

        // Write to disk and verify the file exists at the correct path
        await graph.CommitToConfigRepoAsync(_tempDir, "nested-test", ct);

        var expectedPath = Path.Combine(
            _tempDir,
            "knowledge", "architecture", "distributed-systems",
            "brain-session-per-goal.md");
        Assert.True(File.Exists(expectedPath), $"Expected nested file at {expectedPath}");
    }

    [Fact]
    public async Task ReloadFromConfigRepoAsync_NestedDocument_TopicAndSubtopicFromPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        // Create and commit a nested document
        await graph.CreateDocumentAsync(
            "architecture-distributed-systems-brain-session-per-goal",
            "Brain Session Per Goal",
            DocumentType.Implementation,
            "Session content.",
            topic: "architecture",
            subtopic: "distributed-systems",
            ct: ct);
        await graph.CommitToConfigRepoAsync(_tempDir, "nested-test", ct);

        // Reload into a fresh graph
        var graph2 = new KnowledgeGraph();
        await graph2.ReloadFromConfigRepoAsync(_tempDir, ct);

        var doc = graph2.GetDocument("architecture-distributed-systems-brain-session-per-goal");
        Assert.NotNull(doc);
        Assert.Equal("architecture", doc.Topic);
        Assert.Equal("distributed-systems", doc.Subtopic);
    }

    // ── Delete persists across reload ─────────────────────────────────────────

    [Fact]
    public async Task DeleteDocumentAsync_CommitAndReload_DocumentIsGone()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        // Create and commit a document
        var doc = await graph.CreateDocumentAsync(
            "features-to-delete", "To Delete", DocumentType.Feature, "content", ct: ct);
        await graph.CommitToConfigRepoAsync(_tempDir, "create", ct);

        var writtenPath = Path.Combine(_tempDir, doc.FilePath);
        Assert.True(File.Exists(writtenPath), "File should exist after commit.");

        // Delete the document and commit
        await graph.DeleteDocumentAsync("features-to-delete", ct);
        await graph.CommitToConfigRepoAsync(_tempDir, "delete", ct);

        // File should no longer exist on disk
        Assert.False(File.Exists(writtenPath), "File should have been deleted from disk.");

        // Reload into a fresh graph — document should not reappear
        var graph2 = new KnowledgeGraph();
        await graph2.ReloadFromConfigRepoAsync(_tempDir, ct);

        Assert.Null(graph2.GetDocument("features-to-delete"));
    }

    // ── GetAllDocuments ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetAllDocuments_ReturnsAllDocuments()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("arch-brain", "Brain", DocumentType.Implementation, "", ct: ct);
        await _graph.CreateDocumentAsync("features-kg", "KG", DocumentType.Feature, "", ct: ct);

        var all = _graph.GetAllDocuments();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, d => d.Id == "arch-brain");
        Assert.Contains(all, d => d.Id == "features-kg");
    }

    [Fact]
    public void GetAllDocuments_EmptyGraph_ReturnsEmptyList()
    {
        var all = _graph.GetAllDocuments();
        Assert.Empty(all);
    }

    // ── GetOutgoingLinks ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetOutgoingLinks_ReturnsLinksFromDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("doc-a", "A", DocumentType.Implementation, "", ct: ct);
        await _graph.CreateDocumentAsync("doc-b", "B", DocumentType.Feature, "", ct: ct);
        await _graph.CreateDocumentAsync("doc-c", "C", DocumentType.Idea, "", ct: ct);

        _graph.AddLink("doc-a", new DocumentLink("doc-b", LinkType.Related, "A relates to B"));
        _graph.AddLink("doc-a", new DocumentLink("doc-c", LinkType.DependsOn, "A depends on C"));

        var links = _graph.GetOutgoingLinks("doc-a");
        Assert.Equal(2, links.Count);
        Assert.Contains(links, l => l.TargetId == "doc-b" && l.Type == LinkType.Related && l.Description == "A relates to B");
        Assert.Contains(links, l => l.TargetId == "doc-c" && l.Type == LinkType.DependsOn && l.Description == "A depends on C");
    }

    [Fact]
    public void GetOutgoingLinks_DocumentNotFound_ReturnsEmptyList()
    {
        var links = _graph.GetOutgoingLinks("nonexistent");
        Assert.Empty(links);
    }

    [Fact]
    public async Task GetOutgoingLinks_DocumentWithNoLinks_ReturnsEmptyList()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("doc-isolated", "Isolated", DocumentType.Implementation, "", ct: ct);

        var links = _graph.GetOutgoingLinks("doc-isolated");
        Assert.Empty(links);
    }

    // ── GetIncomingLinks ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetIncomingLinks_ReturnsLinksPointingToDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("doc-a", "A", DocumentType.Implementation, "", ct: ct);
        await _graph.CreateDocumentAsync("doc-b", "B", DocumentType.Feature, "", ct: ct);

        // doc-b links to doc-a → doc-a has an incoming link from doc-b
        _graph.AddLink("doc-b", new DocumentLink("doc-a", LinkType.Parent, "B is a child of A"));

        var incoming = _graph.GetIncomingLinks("doc-a");
        Assert.Single(incoming);
        Assert.Equal("doc-b", incoming[0].SourceId);
        Assert.Equal(LinkType.Child, incoming[0].Type);
        Assert.Equal("B is a child of A", incoming[0].Description);
    }

    [Fact]
    public async Task GetIncomingLinks_MultipleIncomingLinks_ReturnsAll()
    {
        var ct = TestContext.Current.CancellationToken;
        await _graph.CreateDocumentAsync("target", "Target", DocumentType.Feature, "", ct: ct);
        await _graph.CreateDocumentAsync("source1", "Source 1", DocumentType.Implementation, "", ct: ct);
        await _graph.CreateDocumentAsync("source2", "Source 2", DocumentType.Idea, "", ct: ct);

        _graph.AddLink("source1", new DocumentLink("target", LinkType.DependsOn, "depends on target"));
        _graph.AddLink("source2", new DocumentLink("target", LinkType.Related, null));

        var incoming = _graph.GetIncomingLinks("target");
        Assert.Equal(2, incoming.Count);
        Assert.Contains(incoming, l => l.SourceId == "source1" && l.Type == LinkType.DependedOnBy && l.Description == "depends on target");
        Assert.Contains(incoming, l => l.SourceId == "source2" && l.Type == LinkType.Related && l.Description == null);
    }

    [Fact]
    public void GetIncomingLinks_NoLinks_ReturnsEmptyList()
    {
        var incoming = _graph.GetIncomingLinks("nonexistent");
        Assert.Empty(incoming);
    }
}

/// <summary>
/// A spy <see cref="ConfigRepoManager"/> that records calls to <see cref="CommitFileAsync"/>
/// and <see cref="DeleteFileAsync"/> without executing real git operations.
/// </summary>
internal sealed class SpyConfigRepoManager : ConfigRepoManager
{
    private static readonly string DummyPath = Path.Combine(Path.GetTempPath(), $"SpyCRM_{Guid.NewGuid():N}");

    public readonly List<string> CommittedPaths = [];
    public readonly List<string> DeletedPaths = [];

    public SpyConfigRepoManager() : base("https://example.com/spy.git", DummyPath) { }

    public override Task CommitFileAsync(string filePath, string commitMessage, CancellationToken ct = default)
    {
        CommittedPaths.Add(filePath);
        return Task.CompletedTask;
    }

    public override Task DeleteFileAsync(string filePath, string commitMessage, CancellationToken ct = default)
    {
        DeletedPaths.Add(filePath);
        return Task.CompletedTask;
    }
}

public sealed class KnowledgeGraphDeletePersistenceTests : IDisposable
{
    private readonly string _tempDir;

    public KnowledgeGraphDeletePersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"KGDeleteTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task CommitToConfigRepoAsync_AfterDelete_CallsDeleteFileAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var spy = new SpyConfigRepoManager();
        var graph = new KnowledgeGraph(spy);

        // Create and "commit" a document (spy records the commit but no real git ops)
        var doc = await graph.CreateDocumentAsync(
            "features-spy-delete", "Spy Delete", DocumentType.Feature, "body", ct: ct);
        await graph.CommitToConfigRepoAsync(_tempDir, "create", ct);

        // The commit should have called CommitFileAsync for the new doc
        Assert.Contains(doc.FilePath, spy.CommittedPaths);
        Assert.Empty(spy.DeletedPaths);

        // Delete the document and commit
        await graph.DeleteDocumentAsync("features-spy-delete", ct);
        await graph.CommitToConfigRepoAsync(_tempDir, "delete", ct);

        // DeleteFileAsync must have been called with the correct path
        Assert.Single(spy.DeletedPaths);
        Assert.Equal(doc.FilePath, spy.DeletedPaths[0]);

        // File should be deleted from disk
        var fullPath = Path.Combine(_tempDir, doc.FilePath);
        Assert.False(File.Exists(fullPath), "Local file should be gone after commit.");

        // Reload into fresh graph — document must not reappear
        var graph2 = new KnowledgeGraph(spy);
        await graph2.ReloadFromConfigRepoAsync(_tempDir, ct);
        Assert.Null(graph2.GetDocument("features-spy-delete"));
    }
}

/// <summary>
/// A spy <see cref="ConfigRepoManager"/> that records the order of commit/delete calls.
/// </summary>
internal sealed class OrderRecordingConfigRepoManager : ConfigRepoManager
{
    private static string NewDummyPath() => Path.Combine(Path.GetTempPath(), $"OrderCRM_{Guid.NewGuid():N}");

    public readonly List<(string Operation, string Path)> Operations = [];

    public OrderRecordingConfigRepoManager() : base("https://example.com/order.git", NewDummyPath()) { }

    public override Task CommitFileAsync(string filePath, string commitMessage, CancellationToken ct = default)
    {
        Operations.Add(("commit", filePath));
        return Task.CompletedTask;
    }

    public override Task DeleteFileAsync(string filePath, string commitMessage, CancellationToken ct = default)
    {
        Operations.Add(("delete", filePath));
        return Task.CompletedTask;
    }
}

/// <summary>Captures log messages emitted by the graph.</summary>
internal sealed class CapturingKnowledgeLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public readonly List<string> Messages = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (Messages)
            Messages.Add(formatter(state, exception));
    }
}

/// <summary>
/// Tests for the graph-wide lock, batch deletion, case-insensitive tracking,
/// and deletion-before-write commit ordering.
/// </summary>
public sealed class KnowledgeGraphLockingAndBatchDeleteTests : IDisposable
{
    private readonly string _tempDir;

    public KnowledgeGraphLockingAndBatchDeleteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"KGLockTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    // ── Reflection helpers ────────────────────────────────────────────────────

    private static HashSet<string> GetDeletedPaths(KnowledgeGraph graph)
    {
        var field = typeof(KnowledgeGraph).GetField(
            "_deletedDocumentPaths",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var set = field!.GetValue(graph) as HashSet<string>;
        Assert.NotNull(set);
        return set!;
    }

    private static SemaphoreSlim GetGraphLock(KnowledgeGraph graph)
    {
        var field = typeof(KnowledgeGraph).GetField(
            "_graphLock",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var semaphore = field!.GetValue(graph) as SemaphoreSlim;
        Assert.NotNull(semaphore);
        return semaphore!;
    }

    private static HashSet<string> GetDirtyDocuments(KnowledgeGraph graph)
    {
        var field = typeof(KnowledgeGraph).GetField(
            "_dirtyDocuments",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var set = field!.GetValue(graph) as HashSet<string>;
        Assert.NotNull(set);
        return set!;
    }

    private static Dictionary<string, List<(string SourceId, LinkType Type)>> GetReverseIndex(KnowledgeGraph graph)
    {
        var field = typeof(KnowledgeGraph).GetField(
            "_reverseIndex",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var dict = field!.GetValue(graph) as Dictionary<string, List<(string SourceId, LinkType Type)>>;
        Assert.NotNull(dict);
        return dict!;
    }

    /// <summary>Probes at runtime whether the filesystem under the temp dir is case-insensitive.</summary>
    private bool IsFileSystemCaseInsensitive()
    {
        var probeDir = Path.Combine(_tempDir, "case-probe");
        Directory.CreateDirectory(probeDir);
        var lower = Path.Combine(probeDir, "test.tmp");
        File.WriteAllText(lower, "probe");
        return File.Exists(Path.Combine(probeDir, "TEST.TMP"));
    }

    /// <summary>Returns a path whose parent is a regular file, so directory creation fails.</summary>
    private string CreateUnusablePath()
    {
        var blocker = Path.Combine(_tempDir, $"blocker-{Guid.NewGuid():N}.txt");
        File.WriteAllText(blocker, "not a directory");
        return Path.Combine(blocker, "repo");
    }

    // ── 1. Batch delete, in-memory only ───────────────────────────────────────

    [Fact]
    public async Task DeleteDocumentsAndCommitAsync_NullConfigRepoPath_DeletesInMemoryAndClearsOnlyNewTracking()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        await graph.CreateDocumentAsync("features-alpha", "Alpha", DocumentType.Feature, "a", ct: ct);
        await graph.CreateDocumentAsync("features-beta", "Beta", DocumentType.Feature, "b", ct: ct);

        // Seed a pre-existing pending deletion that must survive the call.
        var deleted = GetDeletedPaths(graph);
        deleted.Add("knowledge/features/pre-existing.md");

        var result = await graph.DeleteDocumentsAndCommitAsync(
            ["features-alpha", "features-beta"], configRepoPath: null, "batch delete", ct);

        Assert.True(result.Persisted);
        Assert.Equal(2, result.DeletedCount);
        Assert.Null(result.PersistError);
        Assert.Null(graph.GetDocument("features-alpha"));
        Assert.Null(graph.GetDocument("features-beta"));

        var remaining = GetDeletedPaths(graph);
        Assert.Single(remaining);
        Assert.Contains("knowledge/features/pre-existing.md", remaining);
    }

    // ── 2. Batch delete against a plain directory (no ConfigRepoManager) ───────

    [Fact]
    public async Task DeleteDocumentsAndCommitAsync_DiskOnly_DeletesFilesAndClearsTracking()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        var alpha = await graph.CreateDocumentAsync("features-alpha", "Alpha", DocumentType.Feature, "a", ct: ct);
        var beta = await graph.CreateDocumentAsync("features-beta", "Beta", DocumentType.Feature, "b", ct: ct);
        await graph.CommitToConfigRepoAsync(_tempDir, "create", ct);

        var alphaPath = Path.Combine(_tempDir, alpha.FilePath);
        var betaPath = Path.Combine(_tempDir, beta.FilePath);
        Assert.True(File.Exists(alphaPath));
        Assert.True(File.Exists(betaPath));

        var result = await graph.DeleteDocumentsAndCommitAsync(
            ["features-alpha", "features-beta"], _tempDir, "batch delete", ct);

        Assert.True(result.Persisted);
        Assert.Equal(2, result.DeletedCount);
        Assert.Null(result.PersistError);
        Assert.False(File.Exists(alphaPath));
        Assert.False(File.Exists(betaPath));
        Assert.Empty(GetDeletedPaths(graph));
    }

    // ── 3. Batch delete with a persistence failure ────────────────────────────

    [Fact]
    public async Task DeleteDocumentsAndCommitAsync_PersistFails_ReturnsErrorAndRetainsTracking()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        // A dirty document forces a disk write during the commit, which fails on a bad path.
        await graph.CreateDocumentAsync("features-keep", "Keep", DocumentType.Feature, "keep", ct: ct);
        var doomed = await graph.CreateDocumentAsync("features-doomed", "Doomed", DocumentType.Feature, "x", ct: ct);

        var badPath = CreateUnusablePath();

        var result = await graph.DeleteDocumentsAndCommitAsync(["features-doomed"], badPath, "batch delete", ct);

        Assert.False(result.Persisted);
        Assert.NotNull(result.PersistError);
        Assert.Equal(1, result.DeletedCount);
        Assert.Null(graph.GetDocument("features-doomed"));

        // Tracking is retained so the operation can be retried idempotently.
        Assert.Contains(doomed.FilePath, GetDeletedPaths(graph));
    }

    // ── 4. Retry after a failed persist ───────────────────────────────────────

    [Fact]
    public async Task DeleteDocumentsAndCommitAsync_RetryAfterFailure_PersistsRemainingWork()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        var keep = await graph.CreateDocumentAsync("features-keep", "Keep", DocumentType.Feature, "keep", ct: ct);
        var doomed = await graph.CreateDocumentAsync("features-doomed", "Doomed", DocumentType.Feature, "x", ct: ct);

        var firstResult = await graph.DeleteDocumentsAndCommitAsync(
            ["features-doomed"], CreateUnusablePath(), "batch delete", ct);
        Assert.False(firstResult.Persisted);

        // Retry with an empty ID list and a good path — the pending work is flushed.
        var retry = await graph.DeleteDocumentsAndCommitAsync([], _tempDir, "batch delete retry", ct);

        Assert.True(retry.Persisted);
        Assert.Equal(0, retry.DeletedCount);
        Assert.Null(retry.PersistError);
        Assert.Empty(GetDeletedPaths(graph));
        Assert.False(File.Exists(Path.Combine(_tempDir, doomed.FilePath)));
        Assert.True(File.Exists(Path.Combine(_tempDir, keep.FilePath)));
    }

    // ── 5. ConfigRepoPath ─────────────────────────────────────────────────────

    [Fact]
    public void ConfigRepoPath_NoConfigRepoManager_ReturnsNull()
    {
        var graph = new KnowledgeGraph();
        Assert.Null(graph.ConfigRepoPath);
    }

    [Fact]
    public void ConfigRepoPath_WithConfigRepoManager_ReturnsLocalPath()
    {
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        var graph = new KnowledgeGraph(manager);
        Assert.Equal(manager.LocalPath, graph.ConfigRepoPath);
    }

    // ── 6. Atomicity: reload cannot interleave with a deletion ────────────────

    [Fact]
    public async Task ReloadAndDelete_AreSerialized_DeletionIsNotLost()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        var doc = await graph.CreateDocumentAsync("features-serialized", "Serialized",
            DocumentType.Feature, "content", ct: ct);
        await graph.CommitToConfigRepoAsync(_tempDir, "create", ct);

        var graphLock = GetGraphLock(graph);
        await graphLock.WaitAsync(ct);

        // Queue the reload first, then the deletion — both must block on the lock.
        var reloadTask = Task.Run(() => graph.ReloadFromConfigRepoAsync(_tempDir, ct), ct);
        await Task.Delay(150, ct);
        var deleteTask = Task.Run(() => graph.DeleteDocumentAsync(doc.Id, ct), ct);
        await Task.Delay(150, ct);

        Assert.False(reloadTask.IsCompleted, "Reload must block while the graph lock is held");
        Assert.False(deleteTask.IsCompleted, "Delete must block while the graph lock is held");

        graphLock.Release();

        await reloadTask;
        await deleteTask;

        // The deletion ran to completion after the reload — it was not lost or corrupted.
        Assert.Null(graph.GetDocument(doc.Id));
        Assert.Contains(doc.FilePath, GetDeletedPaths(graph));
    }

    // ── 7. Atomicity: same-ID recreation after deletion ───────────────────────

    [Fact]
    public async Task DeleteThenCreateSameId_CreateBlocksUntilGraphLockReleased_AndPersistsAfterCommit()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        await graph.CreateDocumentAsync("features-recycle", "First", DocumentType.Feature,
            "original content", topic: "features", ct: ct);
        await graph.DeleteDocumentAsync("features-recycle", ct);

        // Hold the graph lock so the recreation cannot proceed.
        var graphLock = GetGraphLock(graph);
        await graphLock.WaitAsync(ct);

        var createTask = Task.Run(() => graph.CreateDocumentAsync(
            "features-recycle", "Second", DocumentType.Feature, "new content",
            topic: "features", ct: ct), ct);

        // The create must be serialized behind the lock — it cannot complete yet.
        await Assert.ThrowsAsync<TimeoutException>(
            () => createTask.WaitAsync(TimeSpan.FromMilliseconds(200), ct));
        Assert.False(createTask.IsCompleted, "CreateDocumentAsync must block while the graph lock is held");

        graphLock.Release();

        var recreated = await createTask;
        Assert.Equal("Second", recreated.Title);
        Assert.Equal("new content", recreated.Content);
        Assert.Equal("Second", graph.GetDocument("features-recycle")!.Title);

        // Persistence: commit to disk (no ConfigRepoManager), then reload into a fresh graph.
        await graph.CommitToConfigRepoAsync(_tempDir, "recreate commit", ct);

        var graph2 = new KnowledgeGraph();
        await graph2.ReloadFromConfigRepoAsync(_tempDir, ct);

        var reloaded = graph2.GetDocument("features-recycle");
        Assert.NotNull(reloaded);
        Assert.Equal("Second", reloaded!.Title);
        Assert.Equal("new content", reloaded.Content);
    }

    // ── 8. Deletion runs before writes for the same path ──────────────────────

    [Fact]
    public async Task CommitToConfigRepoAsync_DeleteThenRecreateSamePath_NewContentSurvives()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        var original = await graph.CreateDocumentAsync(
            "features-reborn", "Original", DocumentType.Feature, "old body", ct: ct);
        await graph.CommitToConfigRepoAsync(_tempDir, "create", ct);

        var fullPath = Path.Combine(_tempDir, original.FilePath);
        Assert.True(File.Exists(fullPath));

        await graph.DeleteDocumentAsync("features-reborn", ct);
        var recreated = await graph.CreateDocumentAsync(
            "features-reborn", "Reborn", DocumentType.Feature, "new body", ct: ct);
        Assert.Equal(original.FilePath, recreated.FilePath);

        await graph.CommitToConfigRepoAsync(_tempDir, "recreate", ct);

        // Deletions run first, so the recreated file is the surviving state on disk.
        Assert.True(File.Exists(fullPath), "The recreated file must exist after the commit");
        var contents = await File.ReadAllTextAsync(fullPath, ct);
        Assert.Contains("new body", contents);
        Assert.DoesNotContain("old body", contents);
    }

    // ── 9. Deletion/recreation on a case-insensitive filesystem ───────────────

    [Fact]
    public async Task CommitToConfigRepoAsync_CaseInsensitiveFileSystem_DeleteThenWriteKeepsNewContent()
    {
        var ct = TestContext.Current.CancellationToken;

        if (!IsFileSystemCaseInsensitive())
        {
            Assert.Skip("Filesystem is case-sensitive — the delete/write collision cannot occur.");
            return;
        }

        // Seed an existing file with mixed-case path, then load it.
        var mixedRelative = "knowledge/Reborn/Doc.md";
        var mixedFull = Path.Combine(_tempDir, "knowledge", "Reborn", "Doc.md");
        Directory.CreateDirectory(Path.GetDirectoryName(mixedFull)!);
        await File.WriteAllTextAsync(mixedFull,
            "---\ntitle: Old\ntype: feature\nstatus: active\ntags: []\nlinks: []\n---\n\nold body\n", ct);

        var graph = new KnowledgeGraph();
        await graph.ReloadFromConfigRepoAsync(_tempDir, ct);

        var loaded = graph.GetDocument("Reborn-Doc");
        Assert.NotNull(loaded);
        Assert.Equal(mixedRelative, loaded!.FilePath);

        await graph.DeleteDocumentAsync(loaded.Id, ct);

        // Create a new document that maps to the same file path in lowercase.
        var recreated = await graph.CreateDocumentAsync(
            "Reborn-Doc", "New", DocumentType.Feature, "new body", topic: "Reborn", ct: ct);
        Assert.Equal("knowledge/reborn/doc.md", recreated.FilePath);

        await graph.CommitToConfigRepoAsync(_tempDir, "recreate", ct);

        var lowerFull = Path.Combine(_tempDir, "knowledge", "reborn", "doc.md");
        Assert.True(File.Exists(lowerFull), "The recreated file must exist on a case-insensitive filesystem");
        var contents = await File.ReadAllTextAsync(lowerFull, ct);
        Assert.Contains("new body", contents);
        Assert.DoesNotContain("old body", contents);
    }

    // ── 10. Commit order: deletions before writes ─────────────────────────────

    [Fact]
    public async Task CommitToConfigRepoAsync_ProcessesDeletionsBeforeWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var spy = new OrderRecordingConfigRepoManager();
        var graph = new KnowledgeGraph(spy);

        var doomed = await graph.CreateDocumentAsync("features-doomed", "Doomed", DocumentType.Feature, "x", ct: ct);
        await graph.CommitToConfigRepoAsync(_tempDir, "create", ct);
        spy.Operations.Clear();

        await graph.DeleteDocumentAsync("features-doomed", ct);
        var fresh = await graph.CreateDocumentAsync("features-fresh", "Fresh", DocumentType.Feature, "y", ct: ct);

        await graph.CommitToConfigRepoAsync(_tempDir, "delete and write", ct);

        var deleteIndex = spy.Operations.FindIndex(o => o.Operation == "delete");
        var commitIndex = spy.Operations.FindIndex(o => o.Operation == "commit");
        Assert.True(deleteIndex >= 0, "A delete operation should have been recorded");
        Assert.True(commitIndex >= 0, "A commit operation should have been recorded");
        Assert.True(deleteIndex < commitIndex, "Deletions must be processed before writes");

        Assert.False(File.Exists(Path.Combine(_tempDir, doomed.FilePath)));
        Assert.True(File.Exists(Path.Combine(_tempDir, fresh.FilePath)));
    }

    // ── 11. Graph lock blocks queries ─────────────────────────────────────────

    [Fact]
    public async Task GetDocument_BlocksWhileGraphLockIsHeld()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        await graph.CreateDocumentAsync("features-locked", "Locked", DocumentType.Feature, "body", ct: ct);

        var graphLock = GetGraphLock(graph);
        await graphLock.WaitAsync(ct);

        var queryTask = Task.Run(() => graph.GetDocument("features-locked"), ct);

        var completedEarly = await Task.WhenAny(queryTask, Task.Delay(200, ct)) == queryTask;
        Assert.False(completedEarly, "GetDocument must block while the graph lock is held");

        graphLock.Release();

        var doc = await queryTask;
        Assert.NotNull(doc);
    }

    // ── 12. Case-insensitive IDs ──────────────────────────────────────────────

    [Fact]
    public async Task Documents_AreTrackedCaseInsensitively()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        await graph.CreateDocumentAsync("Architecture-Core", "Core", DocumentType.Implementation,
            "body", topic: "Architecture", ct: ct);
        await graph.CreateDocumentAsync("Other-Doc", "Other", DocumentType.Feature,
            "body", topic: "Other", ct: ct);

        // Lookups ignore case.
        Assert.NotNull(graph.GetDocument("architecture-core"));
        Assert.NotNull(graph.GetDocument("ARCHITECTURE-CORE"));

        // The reverse index resolves case-differing target IDs.
        graph.AddLink("Other-Doc", new DocumentLink("ARCHITECTURE-CORE", LinkType.DependsOn, "depends"));

        var incoming = graph.GetIncomingLinks("Architecture-Core");
        var entry = Assert.Single(incoming);
        Assert.Equal("Other-Doc", entry.SourceId);
        Assert.Equal("depends", entry.Description);

        Assert.Single(graph.GetDependedOnBy("architecture-core"));

        // Creating a case-differing duplicate is rejected.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => graph.CreateDocumentAsync("ARCHITECTURE-CORE", "Dup", DocumentType.Feature, "x", ct: ct));
        Assert.Contains("already exists", ex.Message);
    }

    // ── 12a. AddLink deduplicates case-differing target IDs ───────────────────

    [Fact]
    public async Task AddLink_MixedCaseTargetId_ReplacesExistingLinkInsteadOfDuplicating()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        await graph.CreateDocumentAsync("doc-a", "A", DocumentType.Feature, "a", topic: "doc", ct: ct);
        await graph.CreateDocumentAsync("doc-b", "B", DocumentType.Feature, "b", topic: "doc", ct: ct);

        graph.AddLink("doc-a", new DocumentLink("doc-b", LinkType.Related, "first"));
        graph.AddLink("doc-a", new DocumentLink("DOC-B", LinkType.Related, "second"));

        // Deduplication is case-insensitive: only the newest link survives.
        var links = graph.GetOutgoingLinks("doc-a");
        Assert.Single(links);
        Assert.Equal("DOC-B", links[0].TargetId);
        Assert.Equal("second", links[0].Description);

        // The reverse index likewise holds a single entry.
        var incoming = graph.GetIncomingLinks("doc-b");
        var entry = Assert.Single(incoming);
        Assert.Equal("doc-a", entry.SourceId);
        Assert.Equal("second", entry.Description);
    }

    // ── 12b. RemoveLink matches case-differing target IDs ─────────────────────

    [Fact]
    public async Task RemoveLink_MixedCaseTargetId_RemovesForwardLinkAndReverseIndexEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        await graph.CreateDocumentAsync("doc-a", "A", DocumentType.Feature, "a", topic: "doc", ct: ct);
        await graph.CreateDocumentAsync("doc-b", "B", DocumentType.Feature, "b", topic: "doc", ct: ct);

        graph.AddLink("doc-a", new DocumentLink("doc-b", LinkType.Related, "linked"));
        Assert.Single(graph.GetOutgoingLinks("doc-a"));

        // The target ID differs only by case — the removal must still match.
        graph.RemoveLink("doc-a", "DOC-B", LinkType.Related);

        Assert.Empty(graph.GetOutgoingLinks("doc-a"));
        Assert.Empty(graph.GetIncomingLinks("doc-b"));
        Assert.DoesNotContain("doc-b", GetReverseIndex(graph).Keys);
    }

    // ── 12c. DeleteDocumentInternal sweeps case-differing source IDs ──────────

    [Fact]
    public async Task DeleteDocumentAsync_MixedCaseId_SweepsStaleReverseIndexEntries()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        await graph.CreateDocumentAsync("Doc-A", "A", DocumentType.Feature, "a", topic: "Doc", ct: ct);
        await graph.CreateDocumentAsync("doc-b", "B", DocumentType.Feature, "b", topic: "doc", ct: ct);

        graph.AddLink("Doc-A", new DocumentLink("doc-b", LinkType.Related, "linked"));

        // Drop A's forward link directly so the reverse-index entry for A becomes stale.
        // Only DeleteDocumentInternal's reverse-index sweep can clean it up, and that
        // sweep must match the stored source ID "Doc-A" against the deletion ID "DOC-A".
        graph.GetDocument("Doc-A")!.Links.Clear();

        var reverseIndex = GetReverseIndex(graph);
        Assert.Contains("doc-b", reverseIndex.Keys);
        Assert.Contains(reverseIndex["doc-b"], e => e.SourceId == "Doc-A");

        await graph.DeleteDocumentAsync("DOC-A", ct);

        // The stale entry must be gone — a case-sensitive sweep would leave it behind.
        Assert.DoesNotContain("doc-b", GetReverseIndex(graph).Keys);
        Assert.Empty(graph.GetIncomingLinks("doc-b"));
        Assert.Empty(graph.GetRelatedBy("doc-b"));
    }

    // ── 12d. GetRelated uses a case-insensitive visited set ───────────────────

    [Fact]
    public async Task GetRelated_MixedCaseBackLink_DoesNotRevisitStartingDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        await graph.CreateDocumentAsync("Doc-A", "A", DocumentType.Feature, "a", topic: "Doc", ct: ct);
        await graph.CreateDocumentAsync("doc-b", "B", DocumentType.Feature, "b", topic: "doc", ct: ct);
        await graph.CreateDocumentAsync("doc-c", "C", DocumentType.Feature, "c", topic: "doc", ct: ct);

        graph.AddLink("Doc-A", new DocumentLink("doc-b", LinkType.Related));
        graph.AddLink("doc-b", new DocumentLink("doc-c", LinkType.Related));
        // Back link to the starting document, spelled with different casing.
        graph.AddLink("doc-b", new DocumentLink("DOC-A", LinkType.Related));

        var related = graph.GetRelated("Doc-A", maxDepth: 2);

        // B and C are reachable; the starting document must not be revisited via the
        // case-differing back link (a default-comparer visited set would re-add it).
        Assert.Equal(2, related.Count);
        Assert.Contains(related, d => d.Id == "doc-b");
        Assert.Contains(related, d => d.Id == "doc-c");
        Assert.DoesNotContain(related, d => StringComparer.OrdinalIgnoreCase.Equals(d.Id, "Doc-A"));
    }

    // ── 12e. Dirty tracking is case-insensitive ───────────────────────────────

    [Fact]
    public async Task UpdateDocumentAsync_MixedCaseId_DoesNotAddDuplicateDirtyEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        await graph.CreateDocumentAsync("Test-Doc", "Test", DocumentType.Feature, "body", topic: "Test", ct: ct);

        var dirty = GetDirtyDocuments(graph);
        Assert.Single(dirty);

        await graph.UpdateDocumentAsync("TEST-DOC", content: "updated", ct: ct);

        // A default-comparer set would now hold both "Test-Doc" and "TEST-DOC".
        dirty = GetDirtyDocuments(graph);
        Assert.Single(dirty);
        Assert.Contains("Test-Doc", dirty);
        Assert.Contains("TEST-DOC", dirty);
        Assert.Equal("updated", graph.GetDocument("Test-Doc")!.Content);
    }

    // ── 12f. Deleted-path tracking is case-insensitive ────────────────────────

    [Fact]
    public async Task DeleteDocumentAsync_MixedCaseId_TracksFilePathCaseInsensitivelyAndDeletesFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        var doc = await graph.CreateDocumentAsync(
            "Test-Doc", "Test", DocumentType.Feature, "body", topic: "Test", ct: ct);
        Assert.Equal("knowledge/test/doc.md", doc.FilePath);

        await graph.CommitToConfigRepoAsync(_tempDir, "create", ct);
        var fullPath = Path.Combine(_tempDir, doc.FilePath);
        Assert.True(File.Exists(fullPath));

        await graph.DeleteDocumentAsync("TEST-DOC", ct);

        var deleted = GetDeletedPaths(graph);
        Assert.Single(deleted);
        Assert.Contains(doc.FilePath, deleted);
        // Only an OrdinalIgnoreCase-backed set resolves the upper-cased spelling.
        Assert.Contains(doc.FilePath.ToUpperInvariant(), deleted);

        await graph.CommitToConfigRepoAsync(_tempDir, "delete", ct);

        Assert.False(File.Exists(fullPath), "The tracked file must be deleted from disk");
        Assert.Empty(GetDeletedPaths(graph));
    }

    // ── 13. BuildFilePath lowercases new documents ────────────────────────────

    [Fact]
    public async Task CreateDocumentAsync_MixedCaseTopicAndId_ProducesLowercaseFilePath()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        var doc = await graph.CreateDocumentAsync("Architecture-Core", "Core",
            DocumentType.Implementation, "body", topic: "Architecture", ct: ct);

        Assert.Equal("knowledge/architecture/core.md", doc.FilePath);
    }

    // ── 14. Loaded documents preserve their original path casing ──────────────

    [Fact]
    public async Task ReloadFromConfigRepoAsync_PreservesOriginalFilePathCasing_AndDeletesThatFile()
    {
        var ct = TestContext.Current.CancellationToken;

        var fullPath = Path.Combine(_tempDir, "knowledge", "Architecture", "Core.md");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath,
            "---\ntitle: Core\ntype: implementation\nstatus: active\ntags: []\nlinks: []\n---\n\nbody\n", ct);

        var graph = new KnowledgeGraph();
        await graph.ReloadFromConfigRepoAsync(_tempDir, ct);

        var doc = graph.GetDocument("Architecture-Core");
        Assert.NotNull(doc);
        Assert.Equal("knowledge/Architecture/Core.md", doc!.FilePath);

        await graph.DeleteDocumentAsync(doc.Id, ct);
        await graph.CommitToConfigRepoAsync(_tempDir, "delete", ct);

        Assert.False(File.Exists(fullPath), "The original-cased file must be deleted from disk");
    }

    // ── 15. Duplicate case-differing files ────────────────────────────────────

    [Fact]
    public async Task ReloadFromConfigRepoAsync_DuplicateCaseDifferingFiles_FirstSortedPathWins()
    {
        var ct = TestContext.Current.CancellationToken;

        if (IsFileSystemCaseInsensitive())
        {
            Assert.Skip("Filesystem is case-insensitive — two case-differing files cannot coexist.");
            return;
        }

        var dir = Path.Combine(_tempDir, "knowledge", "test");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "Dup.md"),
            "---\ntitle: Upper\ntype: feature\nstatus: active\ntags: []\nlinks: []\n---\n\nupper body\n", ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "dup.md"),
            "---\ntitle: Lower\ntype: feature\nstatus: active\ntags: []\nlinks: []\n---\n\nlower body\n", ct);

        var logger = new CapturingKnowledgeLogger<KnowledgeGraph>();
        var graph = new KnowledgeGraph(logger: logger);
        await graph.ReloadFromConfigRepoAsync(_tempDir, ct);

        var doc = graph.GetDocument("test-dup");
        Assert.NotNull(doc);

        // "knowledge/test/Dup.md" sorts before "knowledge/test/dup.md" in ordinal order.
        Assert.Equal("knowledge/test/Dup.md", doc!.FilePath);
        Assert.Equal("Upper", doc.Title);

        Assert.Contains(logger.Messages, m =>
            m.Contains("Duplicate document ID", StringComparison.Ordinal) &&
            m.Contains("knowledge/test/dup.md", StringComparison.Ordinal));
    }

    // ── 16. DeleteDocumentAsync XML documentation ─────────────────────────────

    [Fact]
    public void DeleteDocumentAsync_XmlDoc_ClarifiesForwardLinksAreNotRemoved()
    {
        var sourcePath = FindRepoFile(Path.Combine("src", "CopilotHive", "Knowledge", "KnowledgeGraph.cs"));
        Assert.NotNull(sourcePath);

        var source = File.ReadAllText(sourcePath!);
        Assert.Contains("Does NOT remove forward links from other documents that point to this document.", source, StringComparison.Ordinal);
        Assert.Contains("strips all incoming reverse-index entries that point to it.", source, StringComparison.Ordinal);
    }

    private static string? FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}

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
}

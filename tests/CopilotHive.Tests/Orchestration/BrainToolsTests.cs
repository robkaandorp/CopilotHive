using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Orchestration;
using CopilotHive.Services;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests.Orchestration;

/// <summary>Tests for the shared <see cref="BrainTools"/> dependency tool factory.</summary>
public class BrainToolsTests
{
    private static Func<string, Task<GoalPipeline?>> NoPipeline => _ => Task.FromResult<GoalPipeline?>(null);

    private static AIFunction Tool(string name, IGoalStore? store = null, KnowledgeGraph? graph = null,
        Func<string, Task<GoalPipeline?>>? resolver = null) =>
        BrainTools.BuildDependencyTools(store, resolver ?? NoPipeline, graph, NullLogger.Instance)
            .Cast<AIFunction>().First(t => t.Name == name);

    private static async Task<string> InvokeAsync(AIFunction tool, AIFunctionArguments args) =>
        (await tool.InvokeAsync(args, TestContext.Current.CancellationToken))?.ToString() ?? "";

    [Fact]
    public void BuildDependencyTools_ReturnsFiveNamedTools()
    {
        var tools = BrainTools.BuildDependencyTools(null, NoPipeline, null, NullLogger.Instance);
        Assert.Equal(
            ["get_goal", "search_knowledge", "read_document", "traverse_graph", "get_current_time"],
            tools.Cast<AIFunction>().Select(t => t.Name));
    }

    [Fact]
    public async Task GetGoal_NullStore_ReturnsUnavailable() =>
        Assert.Equal("Goal store is not available.",
            await InvokeAsync(Tool("get_goal"), new AIFunctionArguments { ["goal_id"] = "g1" }));

    [Fact]
    public async Task GetGoal_UnknownGoal_ReturnsNotFound() =>
        Assert.Equal("Goal 'nope' not found.",
            await InvokeAsync(Tool("get_goal", new InMemoryGoalStore()), new AIFunctionArguments { ["goal_id"] = "nope" }));

    [Fact]
    public async Task GetGoal_WithPipelineAndDocs_ReturnsFullDetails()
    {
        var store = new InMemoryGoalStore();
        store.AddGoal(new Goal { Id = "g1", Description = "Do it", RepositoryNames = ["repo"], Documents = ["doc-1"] });

        var graph = new KnowledgeGraph();
        await graph.CreateDocumentAsync("doc-1", "Design Doc", DocumentType.Feature, "body",
            topic: "architecture", ct: TestContext.Current.CancellationToken);

        var pipeline = new GoalPipeline(new Goal { Id = "g1", Description = "Do it", RepositoryNames = ["repo"] });
        var tool = Tool("get_goal", store, graph, _ => Task.FromResult<GoalPipeline?>(pipeline));

        var result = await InvokeAsync(tool, new AIFunctionArguments { ["goal_id"] = "g1" });

        Assert.Contains("Goal ID: g1", result);
        Assert.Contains("Description: Do it", result);
        Assert.Contains("Repositories: repo", result);
        Assert.Contains("Current iteration: 1", result);
        Assert.Contains("Related docs: doc-1 (Design Doc)", result);
    }

    [Fact]
    public async Task GetGoal_NoPipeline_ReportsPipelineNotActive()
    {
        var store = new InMemoryGoalStore();
        store.AddGoal(new Goal { Id = "g1", Description = "Do it", RepositoryNames = ["repo"] });

        var result = await InvokeAsync(Tool("get_goal", store), new AIFunctionArguments { ["goal_id"] = "g1" });
        Assert.Contains("Pipeline not active.", result);
    }

    [Fact]
    public async Task SearchKnowledge_NullGraph_ReturnsUnavailable() =>
        Assert.Equal("Knowledge graph not available.",
            await InvokeAsync(Tool("search_knowledge"), new AIFunctionArguments { ["query"] = "x" }));

    [Fact]
    public async Task SearchKnowledge_NoMatches_ReturnsNoDocuments() =>
        Assert.Equal("No documents match your query.",
            await InvokeAsync(Tool("search_knowledge", graph: new KnowledgeGraph()),
                new AIFunctionArguments { ["query"] = "zzz" }));

    [Fact]
    public async Task SearchKnowledge_Matches_ReturnsFormattedResults()
    {
        var graph = new KnowledgeGraph();
        await graph.CreateDocumentAsync("doc-1", "Brain Sessions", DocumentType.Feature, "content about brain",
            topic: "architecture", ct: TestContext.Current.CancellationToken);

        var result = await InvokeAsync(Tool("search_knowledge", graph: graph),
            new AIFunctionArguments { ["query"] = "brain" });

        Assert.Contains("Found 1 document:", result);
        Assert.Contains("1. [doc-1] Brain Sessions", result);
        Assert.Contains("content about brain", result);
    }

    [Fact]
    public async Task ReadDocument_NullGraph_ReturnsUnavailable() =>
        Assert.Equal("Knowledge graph not available.",
            await InvokeAsync(Tool("read_document"), new AIFunctionArguments { ["document_id"] = "d" }));

    [Fact]
    public async Task ReadDocument_NotFound_ReturnsMessage() =>
        Assert.Equal("Document 'd' not found.",
            await InvokeAsync(Tool("read_document", graph: new KnowledgeGraph()),
                new AIFunctionArguments { ["document_id"] = "d" }));

    [Fact]
    public async Task ReadDocument_Existing_ReturnsMetadataAndContent()
    {
        var graph = new KnowledgeGraph();
        await graph.CreateDocumentAsync("doc-1", "Brain Sessions", DocumentType.Feature, "body text",
            topic: "architecture", ct: TestContext.Current.CancellationToken);

        var result = await InvokeAsync(Tool("read_document", graph: graph),
            new AIFunctionArguments { ["document_id"] = "doc-1" });

        Assert.Contains("## Brain Sessions", result);
        Assert.Contains("- **ID:** doc-1", result);
        Assert.Contains("- **Topic:** architecture", result);
        Assert.Contains("body text", result);
    }

    [Fact]
    public async Task TraverseGraph_NullGraph_ReturnsUnavailable() =>
        Assert.Equal("Knowledge graph not available.",
            await InvokeAsync(Tool("traverse_graph"), new AIFunctionArguments { ["document_id"] = "d" }));

    [Fact]
    public async Task TraverseGraph_InvalidDirection_ReturnsError()
    {
        var graph = new KnowledgeGraph();
        await graph.CreateDocumentAsync("doc-1", "A", DocumentType.Feature, "a",
            topic: "architecture", ct: TestContext.Current.CancellationToken);

        var result = await InvokeAsync(Tool("traverse_graph", graph: graph),
            new AIFunctionArguments { ["document_id"] = "doc-1", ["direction"] = "sideways" });

        Assert.Equal("Invalid direction 'sideways'. Valid values: outgoing, incoming, both.", result);
    }

    [Fact]
    public async Task TraverseGraph_NoLinks_ReportsNoLinks()
    {
        var graph = new KnowledgeGraph();
        await graph.CreateDocumentAsync("doc-1", "A", DocumentType.Feature, "a",
            topic: "architecture", ct: TestContext.Current.CancellationToken);

        var result = await InvokeAsync(Tool("traverse_graph", graph: graph),
            new AIFunctionArguments { ["document_id"] = "doc-1" });

        Assert.Contains("## Knowledge Graph: doc-1", result);
        Assert.Contains("No links found in the specified direction and depth.", result);
    }

    [Fact]
    public async Task TraverseGraph_OutgoingLink_ListsRelationshipAndReachable()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        await graph.CreateDocumentAsync("doc-1", "A", DocumentType.Feature, "a", topic: "architecture", ct: ct);
        await graph.CreateDocumentAsync("doc-2", "B", DocumentType.Feature, "b", topic: "architecture", ct: ct);
        graph.AddLink("doc-1", new DocumentLink("doc-2", LinkType.Related));

        var result = await InvokeAsync(Tool("traverse_graph", graph: graph),
            new AIFunctionArguments { ["document_id"] = "doc-1" });

        Assert.Contains("### Relationships", result);
        Assert.Contains("→", result);
        Assert.Contains("doc-2", result);
        Assert.Contains("### Reachable Documents (1)", result);
    }

    [Fact]
    public async Task TraverseGraph_IncomingDirection_UsesInverseIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();
        await graph.CreateDocumentAsync("doc-1", "A", DocumentType.Feature, "a", topic: "architecture", ct: ct);
        await graph.CreateDocumentAsync("doc-2", "B", DocumentType.Feature, "b", topic: "architecture", ct: ct);
        graph.AddLink("doc-2", new DocumentLink("doc-1", LinkType.Related));

        var result = await InvokeAsync(Tool("traverse_graph", graph: graph),
            new AIFunctionArguments { ["document_id"] = "doc-1", ["direction"] = "incoming" });

        Assert.Contains("←", result);
        Assert.Contains("doc-2", result);
    }

    [Fact]
    public async Task GetCurrentTime_ReturnsUtcJson()
    {
        var result = await InvokeAsync(Tool("get_current_time"), new AIFunctionArguments());
        using var json = System.Text.Json.JsonDocument.Parse(result);
        Assert.Equal("UTC", json.RootElement.GetProperty("timezone").GetString());
        Assert.True(json.RootElement.TryGetProperty("date", out _));
        Assert.True(json.RootElement.TryGetProperty("time", out _));
        Assert.True(json.RootElement.TryGetProperty("iso", out _));
    }

    [Theory]
    [InlineData("coding-1", "coding")]
    [InlineData("testing-2", "testing")]
    [InlineData("review", "review")]
    [InlineData("coding", "coding")]
    [InlineData("coding-0", "coding-0")]
    [InlineData("coding-x", "coding-x")]
    [InlineData("coding-", "coding-")]
    [InlineData("coding--1", "coding--1")]
    [InlineData("coding-99999999999999999999", "coding-99999999999999999999")]
    // Unicode digits (Arabic-Indic 1, U+0661) must NOT be treated as numeric suffixes (ASCII-only)
    [InlineData("coding-١", "coding-١")]
    public void StripOccurrenceSuffix_RemovesPositiveNumericSuffixes(string input, string expected) =>
        Assert.Equal(expected, BrainTools.StripOccurrenceSuffix(input));

    [Fact]
    public void StripOccurrenceSuffix_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", BrainTools.StripOccurrenceSuffix(null));
        Assert.Equal("", BrainTools.StripOccurrenceSuffix(""));
    }

    [Fact]
    public void ValidateIterationPlan_SuffixedPhases_AreValid()
    {
        var result = BrainTools.ValidateIterationPlan(["coding-1", "testing-1", "review"], "", "reason", null);
        Assert.True(result.Valid);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("coding-0")]
    [InlineData("foo-1")]
    [InlineData("GarbageName")]
    [InlineData("Planning")]
    [InlineData("1")]
    public void ValidateIterationPlan_UnknownPhaseNames_AreNotRejectedHere(string phase)
    {
        // Phase-name membership moved out of this tool validator: unknown names are surfaced
        // via IterationPlan.UnrecognizedPhases and rejected inside PlanIterationAsync's loop.
        var result = BrainTools.ValidateIterationPlan([phase], "", "reason", null);
        Assert.True(result.Valid);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ValidateIterationPlan_UnknownPhaseName_DoesNotTriggerMapPlanEarlyThrow()
    {
        // MapPlan (DistributedBrain.ExecuteBrainViaActorAsync) throws only when this validator
        // reports invalid. A previously-throwing unknown-phase submission now validates cleanly,
        // so planning proceeds into the bounded replan loop instead of hard-crashing.
        var result = BrainTools.ValidateIterationPlan(
            ["coding", "GarbageName", "merging"], "{}", "reason", null);

        Assert.True(result.Valid);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ValidateIterationPlan_ModelTiersWithSuffixedKeys_AreValid()
    {
        var tiers = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["coding-1"] = "premium" });
        var result = BrainTools.ValidateIterationPlan(["coding-1"], "", "reason", tiers);
        Assert.True(result.Valid);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ValidateIterationPlan_ModelTiersInvalidPhaseName_ReportedAsIs()
    {
        var tiers = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["foo-1"] = "premium" });
        var result = BrainTools.ValidateIterationPlan(["coding-1"], "", "reason", tiers);
        Assert.False(result.Valid);
        Assert.Contains("foo-1", result.Error);
        Assert.Contains("coding, testing, docwriting, review, improve", result.Error);
    }

    [Fact]
    public void ValidateIterationPlan_NullPhases_IsInvalid()
    {
        var result = BrainTools.ValidateIterationPlan(null!, "", "reason", null);
        Assert.False(result.Valid);
        Assert.Contains("phases must be a non-empty array", result.Error);
    }

    [Fact]
    public void ValidateIterationPlan_EmptyPhases_IsInvalid()
    {
        var result = BrainTools.ValidateIterationPlan([], "", "reason", null);
        Assert.False(result.Valid);
        Assert.Contains("phases must be a non-empty array", result.Error);
    }

    [Fact]
    public void ValidateIterationPlan_MissingReason_IsInvalid()
    {
        var result = BrainTools.ValidateIterationPlan(["coding", "merging"], "{}", "", null);
        Assert.False(result.Valid);
        Assert.Contains("reason is required", result.Error);
    }

    [Fact]
    public void ValidateIterationPlan_InvalidTierValue_IsInvalid()
    {
        var tiers = System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, string> { ["coding"] = "turbo" });
        var result = BrainTools.ValidateIterationPlan(["coding"], "", "reason", tiers);
        Assert.False(result.Valid);
        Assert.Contains("turbo", result.Error);
        Assert.Contains("standard, premium", result.Error);
    }

    [Fact]
    public void ValidateIterationPlan_MalformedTierJson_IsInvalid()
    {
        var result = BrainTools.ValidateIterationPlan(["coding"], "", "reason", "{not json");
        Assert.False(result.Valid);
        Assert.Contains("model_tiers must be valid JSON", result.Error);
    }
}

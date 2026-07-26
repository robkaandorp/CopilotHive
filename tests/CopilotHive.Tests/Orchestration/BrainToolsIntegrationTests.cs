using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

using CopilotHive.Actors;
using CopilotHive.Goals;
using CopilotHive.Knowledge;
using CopilotHive.Orchestration;
using CopilotHive.Services;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using SharpCoder;

using Xunit;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Integration tests for <see cref="BrainTools.BuildDependencyTools"/> covering all 5 tools
/// with real in-memory <see cref="IGoalStore"/> and <see cref="KnowledgeGraph"/>,
/// null-dependency cases, format parity, depth clamping, link-type filtering,
/// pipeline-resolver timeout, and tool-count parity between
/// <see cref="DistributedBrain"/> and <see cref="GoalBrainActor"/>.
/// </summary>
public class BrainToolsIntegrationTests
{
    // ──────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────

    private static readonly Func<string, Task<GoalPipeline?>> NoPipeline =
        _ => Task.FromResult<GoalPipeline?>(null);

    private static List<AIFunction> BuildTools(
        IGoalStore? store = null,
        KnowledgeGraph? graph = null,
        Func<string, Task<GoalPipeline?>>? resolver = null) =>
        BrainTools.BuildDependencyTools(store, resolver ?? NoPipeline, graph, NullLogger.Instance)
            .Cast<AIFunction>().ToList();

    private static AIFunction Tool(string name,
        IGoalStore? store = null, KnowledgeGraph? graph = null,
        Func<string, Task<GoalPipeline?>>? resolver = null) =>
        BuildTools(store, graph, resolver).First(t => t.Name == name);

    private static async Task<string> InvokeAsync(AIFunction tool, AIFunctionArguments args) =>
        (await tool.InvokeAsync(args, TestContext.Current.CancellationToken))?.ToString() ?? "";

    private static async Task<KnowledgeGraph> CreateGraphWithDocumentsAsync()
    {
        var graph = new KnowledgeGraph();
        var ct = TestContext.Current.CancellationToken;
        await graph.CreateDocumentAsync("doc-arch", "Architecture Overview",
            DocumentType.Implementation, "This document describes the overall system architecture.",
            topic: "architecture", ct: ct);
        await graph.CreateDocumentAsync("doc-feat", "Feature Design",
            DocumentType.Feature, "This document outlines the feature design and user stories.",
            topic: "features", ct: ct);
        return graph;
    }

    // ──────────────────────────────────────────────────────────
    //  #1 — get_goal with real IGoalStore + pipeline resolver returning real GoalPipeline
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetGoal_WithRealStoreAndPipeline_ReturnsFullDetails()
    {
        var store = new InMemoryGoalStore();
        store.AddGoal(new Goal
        {
            Id = "goal-100",
            Description = "Refactor Brain tools into shared factory",
            Status = GoalStatus.InProgress,
            ReviewStatus = ReviewStatus.Approved,
            RepositoryNames = ["CopilotHive"],
        });

        var pipeline = new GoalPipeline(new Goal
        {
            Id = "goal-100",
            Description = "Refactor Brain tools",
            RepositoryNames = ["CopilotHive"],
        });

        var tool = Tool("get_goal", store, resolver: _ => Task.FromResult<GoalPipeline?>(pipeline));
        var result = await InvokeAsync(tool, new AIFunctionArguments { ["goal_id"] = "goal-100" });

        Assert.Contains("Goal ID: goal-100", result);
        Assert.Contains("Description: Refactor Brain tools into shared factory", result);
        Assert.Contains("Status: InProgress", result);
        Assert.Contains("Review Status: Approved", result);
        Assert.Contains("Repositories: CopilotHive", result);
        // Pipeline iteration info: "Current iteration: {n}, Phase: {phase}"
        Assert.Matches(@"Current iteration: \d+, Phase: \w+", result);
    }

    // ──────────────────────────────────────────────────────────
    //  #2 — get_goal with null goalStore
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetGoal_NullGoalStore_ReturnsExactMessage()
    {
        var result = await InvokeAsync(Tool("get_goal"),
            new AIFunctionArguments { ["goal_id"] = "any" });
        Assert.Equal("Goal store is not available.", result);
    }

    // ──────────────────────────────────────────────────────────
    //  #3 — get_goal: goal exists but pipeline resolver returns null
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetGoal_GoalExistsButPipelineNull_ReportsPipelineNotActive()
    {
        var store = new InMemoryGoalStore();
        store.AddGoal(new Goal
        {
            Id = "g-no-pipe",
            Description = "A goal without active pipeline",
            RepositoryNames = ["repo"],
        });

        var result = await InvokeAsync(Tool("get_goal", store),
            new AIFunctionArguments { ["goal_id"] = "g-no-pipe" });

        Assert.Contains("Pipeline not active.", result);
        Assert.Contains("Goal ID: g-no-pipe", result);
    }

    // ──────────────────────────────────────────────────────────
    //  #4 — get_goal: goal ID doesn't exist
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetGoal_GoalNotFound_ReturnsExactMessage()
    {
        var store = new InMemoryGoalStore();
        var result = await InvokeAsync(Tool("get_goal", store),
            new AIFunctionArguments { ["goal_id"] = "missing-id" });
        Assert.Equal("Goal 'missing-id' not found.", result);
    }

    // ──────────────────────────────────────────────────────────
    //  #5 — search_knowledge with 2+ documents, verify numbered entries with snippet
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchKnowledge_MultipleDocuments_ReturnsNumberedEntriesWithSnippet()
    {
        var graph = new KnowledgeGraph();
        var ct = TestContext.Current.CancellationToken;
        var longContent = new string('x', 400); // longer than 300 chars → snippet should be truncated

        await graph.CreateDocumentAsync("doc-1", "First Doc",
            DocumentType.Implementation, "Short content one.",
            topic: "architecture", ct: ct);
        await graph.CreateDocumentAsync("doc-2", "Second Doc",
            DocumentType.Feature, longContent,
            topic: "features", ct: ct);

        // Search for something that matches both documents.
        // Search("doc") should find both since title contains "Doc".
        var result = await InvokeAsync(Tool("search_knowledge", graph: graph),
            new AIFunctionArguments { ["query"] = "doc" });

        // Verify numbered entries
        Assert.Contains("1. [doc-1] First Doc", result);
        Assert.Contains("2. [doc-2] Second Doc", result);
        // Verify type and status appear in the entry line
        Assert.Contains("implementation", result);
        Assert.Contains("feature", result);
        Assert.Contains("draft", result); // default status is Draft

        // Verify snippet truncation: doc-2 has 400 chars, snippet is first 300 + "..."
        Assert.Contains("...", result);
        Assert.Contains(new string('x', 300) + "...", result);
    }

    // ──────────────────────────────────────────────────────────
    //  #6 — search_knowledge with null knowledgeGraph
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchKnowledge_NullGraph_ReturnsExactMessage()
    {
        var result = await InvokeAsync(Tool("search_knowledge"),
            new AIFunctionArguments { ["query"] = "anything" });
        Assert.Equal("Knowledge graph not available.", result);
    }

    // ──────────────────────────────────────────────────────────
    //  #7 — search_knowledge with topic and type filters
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchKnowledge_WithTopicAndTypeFilters_FiltersCorrectly()
    {
        var graph = await CreateGraphWithDocumentsAsync();

        // Filter by topic "architecture" → should only return doc-arch
        var resultByTopic = await InvokeAsync(Tool("search_knowledge", graph: graph),
            new AIFunctionArguments { ["query"] = "doc", ["topic"] = "architecture" });
        Assert.Contains("doc-arch", resultByTopic);
        Assert.DoesNotContain("doc-feat", resultByTopic);

        // Filter by type "feature" → should only return doc-feat
        var resultByType = await InvokeAsync(Tool("search_knowledge", graph: graph),
            new AIFunctionArguments { ["query"] = "doc", ["type"] = "feature" });
        Assert.Contains("doc-feat", resultByType);
        Assert.DoesNotContain("doc-arch", resultByType);
    }

    // ──────────────────────────────────────────────────────────
    //  #8 — search_knowledge with no matching results
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchKnowledge_NoMatchingResults_ReturnsExactMessage()
    {
        var graph = await CreateGraphWithDocumentsAsync();
        var result = await InvokeAsync(Tool("search_knowledge", graph: graph),
            new AIFunctionArguments { ["query"] = "zzznomatch" });
        Assert.Equal("No documents match your query.", result);
    }

    // ──────────────────────────────────────────────────────────
    //  #9 — read_document with a found document
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadDocument_Found_ReturnsAllMetadataAndContent()
    {
        var graph = new KnowledgeGraph();
        var ct = TestContext.Current.CancellationToken;
        await graph.CreateDocumentAsync("doc-full", "Full Document",
            DocumentType.Implementation, "The body content here.",
            topic: "architecture", subtopic: "design",
            author: "tester", tags: ["tag1", "tag2"], ct: ct);
        // Add a link to verify Links section
        await graph.CreateDocumentAsync("doc-target", "Target",
            DocumentType.Feature, "target body", topic: "features", ct: ct);
        graph.AddLink("doc-full", new DocumentLink("doc-target", LinkType.Related, "see also"));

        var result = await InvokeAsync(Tool("read_document", graph: graph),
            new AIFunctionArguments { ["document_id"] = "doc-full" });

        // Title heading
        Assert.Contains("## Full Document", result);
        // ID
        Assert.Contains("- **ID:** doc-full", result);
        // Type
        Assert.Contains("- **Type:** Implementation", result);
        // Status
        Assert.Contains("- **Status:** Draft", result);
        // Topic (with subtopic)
        Assert.Contains("- **Topic:** architecture/design", result);
        // File
        Assert.Contains("- **File:**", result);
        // Author
        Assert.Contains("- **Author:** tester", result);
        // Created date
        Assert.Contains("- **Created:**", result);
        // Updated date
        Assert.Contains("- **Updated:**", result);
        // Tags
        Assert.Contains("- **Tags:** tag1, tag2", result);
        // Links section
        Assert.Contains("- **Links:**", result);
        Assert.Contains("[Related] → doc-target — see also", result);
        // Content
        Assert.Contains("The body content here.", result);
    }

    // ──────────────────────────────────────────────────────────
    //  #10 — read_document with null knowledgeGraph
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadDocument_NullGraph_ReturnsExactMessage()
    {
        var result = await InvokeAsync(Tool("read_document"),
            new AIFunctionArguments { ["document_id"] = "any" });
        Assert.Equal("Knowledge graph not available.", result);
    }

    // ──────────────────────────────────────────────────────────
    //  #11 — read_document with non-existent document ID
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadDocument_NotFound_ReturnsExactMessage()
    {
        var graph = new KnowledgeGraph();
        var result = await InvokeAsync(Tool("read_document", graph: graph),
            new AIFunctionArguments { ["document_id"] = "nonexistent" });
        Assert.Equal("Document 'nonexistent' not found.", result);
    }

    // ──────────────────────────────────────────────────────────
    //  #12 — traverse_graph with depth clamping (depth 5 → clamped to 3)
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TraverseGraph_DepthClampedToThree()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        // Create a chain: doc-0 → doc-1 → doc-2 → doc-3 → doc-4
        for (var i = 0; i <= 4; i++)
        {
            await graph.CreateDocumentAsync($"doc-{i}", $"Doc {i}",
                DocumentType.Feature, $"content {i}",
                topic: "chain", ct: ct);
        }
        for (var i = 0; i < 4; i++)
        {
            graph.AddLink($"doc-{i}", new DocumentLink($"doc-{i + 1}", LinkType.Related));
        }

        // Request depth 5 — should be clamped to 3, so doc-4 should NOT be reachable
        var result = await InvokeAsync(Tool("traverse_graph", graph: graph),
            new AIFunctionArguments { ["document_id"] = "doc-0", ["depth"] = 5 });

        // doc-1, doc-2, doc-3 should be reachable (depths 1, 2, 3)
        Assert.Contains("doc-1", result);
        Assert.Contains("doc-2", result);
        Assert.Contains("doc-3", result);
        // doc-4 is at depth 4 which exceeds the clamped max of 3 — should NOT appear
        Assert.DoesNotContain("doc-4", result);
        // Reachable Documents count should be 3 (doc-1, doc-2, doc-3)
        Assert.Contains("### Reachable Documents (3)", result);
    }

    // ──────────────────────────────────────────────────────────
    //  #13 — traverse_graph with invalid direction
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TraverseGraph_InvalidDirection_ReturnsExactError()
    {
        var graph = new KnowledgeGraph();
        var ct = TestContext.Current.CancellationToken;
        await graph.CreateDocumentAsync("doc-1", "A", DocumentType.Feature, "a",
            topic: "arch", ct: ct);

        var result = await InvokeAsync(Tool("traverse_graph", graph: graph),
            new AIFunctionArguments { ["document_id"] = "doc-1", ["direction"] = "sideways" });

        Assert.Equal("Invalid direction 'sideways'. Valid values: outgoing, incoming, both.", result);
    }

    // ──────────────────────────────────────────────────────────
    //  #14 — traverse_graph with link_types filter
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TraverseGraph_WithLinkTypesFilter_OnlyMatchingLinksAppear()
    {
        var ct = TestContext.Current.CancellationToken;
        var graph = new KnowledgeGraph();

        await graph.CreateDocumentAsync("doc-root", "Root", DocumentType.Feature, "root",
            topic: "arch", ct: ct);
        await graph.CreateDocumentAsync("doc-related", "Related Doc", DocumentType.Feature, "rel",
            topic: "arch", ct: ct);
        await graph.CreateDocumentAsync("doc-dep", "Dependency Doc", DocumentType.Feature, "dep",
            topic: "arch", ct: ct);

        // doc-root has two outgoing links: one Related, one DependsOn
        graph.AddLink("doc-root", new DocumentLink("doc-related", LinkType.Related));
        graph.AddLink("doc-root", new DocumentLink("doc-dep", LinkType.DependsOn));

        // Filter to only "depends_on" link type
        var result = await InvokeAsync(Tool("traverse_graph", graph: graph),
            new AIFunctionArguments
            {
                ["document_id"] = "doc-root",
                ["link_types"] = new string[] { "depends_on" }
            });

        // Only doc-dep should appear in the relationships (DependsOn link)
        Assert.Contains("doc-dep", result);
        Assert.Contains("[DependsOn]", result);
        // doc-related should NOT appear because its link type is Related, not DependsOn
        Assert.DoesNotContain("doc-related", result);
    }

    // ──────────────────────────────────────────────────────────
    //  #15 — traverse_graph with null knowledgeGraph
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TraverseGraph_NullGraph_ReturnsExactMessage()
    {
        var result = await InvokeAsync(Tool("traverse_graph"),
            new AIFunctionArguments { ["document_id"] = "any" });
        Assert.Equal("Knowledge graph not available.", result);
    }

    // ──────────────────────────────────────────────────────────
    //  #16 — get_current_time: verify JSON fields, timezone=UTC, iso format
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCurrentTime_ReturnsValidJsonWithExpectedFields()
    {
        var result = await InvokeAsync(Tool("get_current_time"), new AIFunctionArguments());

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("date", out var dateEl), "Missing 'date' field");
        Assert.True(root.TryGetProperty("time", out var timeEl), "Missing 'time' field");
        Assert.True(root.TryGetProperty("iso", out var isoEl), "Missing 'iso' field");
        Assert.True(root.TryGetProperty("timezone", out var tzEl), "Missing 'timezone' field");

        // timezone must be "UTC"
        Assert.Equal("UTC", tzEl.GetString());

        // date must be YYYY-MM-DD
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", dateEl.GetString()!);
        // time must be HH:MM:SS
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}$", timeEl.GetString()!);

        // iso must be ISO 8601 round-trip "o" format: e.g. 2025-01-15T14:30:00.0000000Z
        var iso = isoEl.GetString()!;
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", iso);
        // The "o" format for DateTime.UtcNow ends with 'Z' (UTC specifier)
        Assert.EndsWith("Z", iso);
    }

    // ──────────────────────────────────────────────────────────
    //  #17 — Pipeline resolver timeout: parentTell receives GetPipelineMessage but never replies
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PipelineResolver_ParentNeverReplies_TimesOutAndReportsPipelineNotActive()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new InMemoryGoalStore();
            store.AddGoal(new Goal
            {
                Id = "g-timeout",
                Description = "Timeout test goal",
                RepositoryNames = ["repo"],
            });

            // parentTell receives the GetPipelineMessage but never sets the reply
            // → 2-second timeout elapses → resolver returns null → "Pipeline not active."
            await using var actor = CreateGoalBrainActor(
                dir,
                FakeChatClientForActor.Text("unused"),
                goalStore: store,
                parentTell: msg =>
                {
                    // Accept the message but do NOT set the reply — simulates unresponsive parent
                    Assert.IsType<GetPipelineMessage>(msg);
                    return true;
                });

            var tools = GetActorBrainTools(actor);
            var getGoalTool = tools.First(t => t.Name == "get_goal");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await InvokeAsync(getGoalTool,
                new AIFunctionArguments { ["goal_id"] = "g-timeout" });
            sw.Stop();

            Assert.Contains("Goal ID: g-timeout", result);
            Assert.Contains("Pipeline not active.", result);
            // The 2-second timeout should have elapsed
            Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(1.5),
                $"Expected timeout to take ~2s, but elapsed only {sw.Elapsed.TotalMilliseconds:F0}ms");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ──────────────────────────────────────────────────────────
    //  #18 — GoalBrainActor.BuildTools() produces exactly 7 tools (via _brainTools reflection)
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GoalBrainActor_BuildTools_ProducesExactlySevenTools()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await using var actor = CreateGoalBrainActor(dir, FakeChatClientForActor.Text("unused"));

            // Use reflection to access the _brainTools field
            var field = typeof(GoalBrainActor)
                .GetField("_brainTools", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var tools = (List<AITool>)field.GetValue(actor)!;

            Assert.Equal(7, tools.Count);

            var names = tools.OfType<AIFunction>().Select(t => t.Name).ToHashSet();
            Assert.Contains("escalate_to_composer", names);
            Assert.Contains("report_iteration_plan", names);
            Assert.Contains("get_goal", names);
            Assert.Contains("search_knowledge", names);
            Assert.Contains("read_document", names);
            Assert.Contains("traverse_graph", names);
            Assert.Contains("get_current_time", names);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ──────────────────────────────────────────────────────────
    //  #19 — DistributedBrain.BuildBrainTools still produces >= 7 tools after refactor
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void DistributedBrain_BuildBrainTools_ProducesSevenToolsAfterRefactor()
    {
        var goalStore = new InMemoryGoalStore();
        var brain = new DistributedBrain("copilot/test-model",
            NullLogger<DistributedBrain>.Instance, goalStore: goalStore);

        // Use reflection to access the _brainTools field
        var field = typeof(DistributedBrain)
            .GetField("_brainTools", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tools = (List<AITool>)field.GetValue(brain)!;

        Assert.True(tools.Count >= 7, $"Expected >= 7 tools, got {tools.Count}");

        var names = tools.OfType<AIFunction>().Select(t => t.Name).ToHashSet();
        Assert.Contains("escalate_to_composer", names);
        Assert.Contains("report_iteration_plan", names);
        Assert.Contains("get_goal", names);
        Assert.Contains("search_knowledge", names);
        Assert.Contains("read_document", names);
        Assert.Contains("traverse_graph", names);
        Assert.Contains("get_current_time", names);
    }

    // ──────────────────────────────────────────────────────────
    //  GoalBrainActor helpers (adapted from GoalBrainActorTests.cs)
    // ──────────────────────────────────────────────────────────

    /// <summary>Minimal FakeChatClient for creating GoalBrainActor instances.</summary>
    private sealed class FakeChatClientForActor : IChatClient
    {
        private readonly string _text;
        internal FakeChatClientForActor(string text) => _text = text;
        internal static FakeChatClientForActor Text(string text) => new(text);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _text)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static AgentOptions CreateBaseOptions(string workDir) => new()
    {
        WorkDirectory = workDir,
        MaxSteps = 5,
        EnableBash = false,
        EnableFileOps = false,
        EnableFileWrites = false,
        EnableSkills = false,
        AutoLoadWorkspaceInstructions = false,
        SystemPrompt = "You are the Brain.",
    };

    private static GoalBrainActor CreateGoalBrainActor(
        string stateDir,
        IChatClient chatClient,
        string goalId = "goal-1",
        IGoalStore? goalStore = null,
        KnowledgeGraph? knowledgeGraph = null,
        Func<IBrainMessage, bool>? parentTell = null) =>
        new(goalId,
            AgentSession.Create($"brain-goal-{goalId}"),
            chatClient,
            ownsChatClient: true,
            compactionClient: null,
            CreateBaseOptions(stateDir),
            "test-model",
            100_000,
            stateDir,
            sessionRegistry: null,
            NullLogger<GoalBrainActor>.Instance,
            goalStore,
            knowledgeGraph,
            parentTell);

    private static List<AIFunction> GetActorBrainTools(GoalBrainActor actor)
    {
        var field = typeof(GoalBrainActor)
            .GetField("_brainTools", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((List<AITool>)field.GetValue(actor)!).Cast<AIFunction>().ToList();
    }
}
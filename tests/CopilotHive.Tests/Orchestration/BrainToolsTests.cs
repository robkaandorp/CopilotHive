using CopilotHive.Configuration;
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
        Func<string, Task<GoalPipeline?>>? resolver = null, ConfigRepoManager? configRepo = null,
        IIssueStore? issueStore = null, string? sourceGoalId = null) =>
        BrainTools.BuildDependencyTools(store, resolver ?? NoPipeline, graph, NullLogger.Instance, configRepo,
            issueStore, sourceGoalId)
            .Cast<AIFunction>().First(t => t.Name == name);

    private static async Task<string> InvokeAsync(AIFunction tool, AIFunctionArguments args) =>
        (await tool.InvokeAsync(args, TestContext.Current.CancellationToken))?.ToString() ?? "";

    /// <summary>
    /// Invokes a tool with an explicit caller token. Named distinctly rather than overloading
    /// <see cref="InvokeAsync(AIFunction, AIFunctionArguments)"/> so the xUnit1051 analyzer does
    /// not flag every existing token-less call site.
    /// </summary>
    private static async Task<string> InvokeWithTokenAsync(
        AIFunction tool, AIFunctionArguments args, CancellationToken ct) =>
        (await tool.InvokeAsync(args, ct))?.ToString() ?? "";

    [Fact]
    public void BuildDependencyTools_ReturnsEightNamedTools()
    {
        var tools = BrainTools.BuildDependencyTools(null, NoPipeline, null, NullLogger.Instance, null);
        Assert.Equal(
            ["get_goal", "search_knowledge", "read_document", "traverse_graph", "get_current_time", "list_config_files", "read_config_file", "raise_issue"],
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

    [Fact]
    public async Task ListConfigFiles_NullConfigRepo_ReturnsError()
    {
        var result = await InvokeAsync(Tool("list_config_files"), new AIFunctionArguments());
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task ListConfigFiles_WithConfigRepo_ListsAgentsFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"config-repo-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "agents"));
            File.WriteAllText(Path.Combine(dir, "agents", "coder.agents.md"), "coder instructions");

            var configRepo = new ConfigRepoManager("https://example.com/config.git", dir);
            var tool = Tool("list_config_files", configRepo: configRepo);

            var result = await InvokeAsync(tool, new AIFunctionArguments { ["path"] = "agents" });
            Assert.Contains("coder.agents.md", result);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ReadConfigFile_ReturnsContentWithLineNumbers()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"config-repo-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "test.txt"), "line one\nline two\nline three");

            var configRepo = new ConfigRepoManager("https://example.com/config.git", dir);
            var tool = Tool("read_config_file", configRepo: configRepo);

            var result = await InvokeAsync(tool, new AIFunctionArguments { ["path"] = "test.txt" });
            Assert.Contains("1: line one", result);
            Assert.Contains("2: line two", result);
            Assert.Contains("3: line three", result);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ReadConfigFile_PathTraversal_ReturnsAccessDenied()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"config-repo-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);

            var configRepo = new ConfigRepoManager("https://example.com/config.git", dir);
            var tool = Tool("read_config_file", configRepo: configRepo);

            var result = await InvokeAsync(tool, new AIFunctionArguments { ["path"] = "../../etc/passwd" });
            Assert.Contains("Access denied", result);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ReadConfigFile_NotFound_ReturnsFileNotFound()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"config-repo-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);

            var configRepo = new ConfigRepoManager("https://example.com/config.git", dir);
            var tool = Tool("read_config_file", configRepo: configRepo);

            var result = await InvokeAsync(tool, new AIFunctionArguments { ["path"] = "nonexistent.txt" });
            Assert.Contains("not found", result);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ReadConfigFile_DotGitConfig_ReturnsAccessDenied()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"config-repo-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            File.WriteAllText(Path.Combine(dir, ".git", "config"), "url = https://x:token@example.com/repo.git");

            var configRepo = new ConfigRepoManager("https://example.com/config.git", dir);
            var tool = Tool("read_config_file", configRepo: configRepo);

            var result = await InvokeAsync(tool, new AIFunctionArguments { ["path"] = ".git/config" });
            Assert.Contains("Access denied", result);
            Assert.Contains(".git", result);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ReadConfigFile_ObfuscatedDotGitPath_ReturnsAccessDenied()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"config-repo-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            File.WriteAllText(Path.Combine(dir, ".git", "config"), "url = https://x:token@example.com/repo.git");

            var configRepo = new ConfigRepoManager("https://example.com/config.git", dir);
            var tool = Tool("read_config_file", configRepo: configRepo);

            var result = await InvokeAsync(tool, new AIFunctionArguments { ["path"] = "agents/../.git/config" });
            Assert.Contains("Access denied", result);
            Assert.Contains(".git", result);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ListConfigFiles_Root_ExcludesDotGitDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"config-repo-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "agents"));
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            File.WriteAllText(Path.Combine(dir, "agents", "coder.agents.md"), "coder instructions");
            File.WriteAllText(Path.Combine(dir, ".git", "config"), "url = https://x:token@example.com/repo.git");

            var configRepo = new ConfigRepoManager("https://example.com/config.git", dir);
            var tool = Tool("list_config_files", configRepo: configRepo);

            var result = await InvokeAsync(tool, new AIFunctionArguments());
            Assert.Contains("agents/coder.agents.md", result);
            Assert.DoesNotContain(".git", result);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ListConfigFiles_DotGitPath_ReturnsAccessDenied()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"config-repo-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));

            var configRepo = new ConfigRepoManager("https://example.com/config.git", dir);
            var tool = Tool("list_config_files", configRepo: configRepo);

            var result = await InvokeAsync(tool, new AIFunctionArguments { ["path"] = ".git" });
            Assert.Contains("Access denied", result);
            Assert.Contains(".git", result);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ReadConfigFile_MixedCaseDotGit_ReturnsAccessDenied()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"config-repo-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            File.WriteAllText(Path.Combine(dir, ".git", "config"), "url = https://x:token@example.com/repo.git");

            var configRepo = new ConfigRepoManager("https://example.com/config.git", dir);
            var tool = Tool("read_config_file", configRepo: configRepo);

            var result = await InvokeAsync(tool, new AIFunctionArguments { ["path"] = ".GIT/config" });
            Assert.Contains("Access denied", result);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ReadConfigFile_ObfuscatedMixedCaseDotGit_ReturnsAccessDenied()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"config-repo-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            File.WriteAllText(Path.Combine(dir, ".git", "config"), "url = https://x:token@example.com/repo.git");

            var configRepo = new ConfigRepoManager("https://example.com/config.git", dir);
            var tool = Tool("read_config_file", configRepo: configRepo);

            var result = await InvokeAsync(tool, new AIFunctionArguments { ["path"] = "agents/../.GIT/config" });
            Assert.Contains("Access denied", result);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ListConfigFiles_MixedCaseDotGit_ReturnsAccessDenied()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"config-repo-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));

            var configRepo = new ConfigRepoManager("https://example.com/config.git", dir);
            var tool = Tool("list_config_files", configRepo: configRepo);

            var result = await InvokeAsync(tool, new AIFunctionArguments { ["path"] = ".GIT" });
            Assert.Contains("Access denied", result);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ListConfigFiles_Root_ExcludesMixedCaseDotGit()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"config-repo-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "agents"));
            Directory.CreateDirectory(Path.Combine(dir, ".GIT"));
            File.WriteAllText(Path.Combine(dir, "agents", "coder.agents.md"), "coder instructions");
            File.WriteAllText(Path.Combine(dir, ".GIT", "config"), "url = https://x:token@example.com/repo.git");

            var configRepo = new ConfigRepoManager("https://example.com/config.git", dir);
            var tool = Tool("list_config_files", configRepo: configRepo);

            var result = await InvokeAsync(tool, new AIFunctionArguments());
            Assert.Contains("agents/coder.agents.md", result);
            Assert.DoesNotContain(".GIT", result);
            Assert.DoesNotContain(".git", result);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void BrainTools_DoNotIncludeWriteTools()
    {
        var tools = BrainTools.BuildDependencyTools(null, NoPipeline, null, NullLogger.Instance, null);
        var names = tools.Cast<AIFunction>().Select(t => t.Name);
        Assert.DoesNotContain("edit_agents_md", names);
        Assert.DoesNotContain("update_agents_md", names);
        Assert.DoesNotContain("commit_config_changes", names);
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

    // ──────────────────────────────────────────────────────────
    //  raise_issue tool tests
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void RaiseIssue_ToolIsRegistered()
    {
        var store = new FakeIssueStore();
        var tools = BrainTools.BuildDependencyTools(null, NoPipeline, null, NullLogger.Instance, null, store, "goal-x");
        Assert.Contains("raise_issue", tools.Cast<AIFunction>().Select(t => t.Name));
    }

    [Fact]
    public async Task RaiseIssue_NullStore_ReturnsUnavailable()
    {
        var tool = Tool("raise_issue", issueStore: null);
        var result = await InvokeAsync(tool, new AIFunctionArguments
        {
            ["type"] = "bug",
            ["title"] = "T",
            ["description"] = "D",
            ["severity"] = "low",
        });
        Assert.Equal("Issue tracking is not available.", result);
    }

    [Fact]
    public async Task RaiseIssue_NullType_ReturnsTypeRequired()
    {
        var store = new FakeIssueStore();
        var tool = Tool("raise_issue", issueStore: store);
        var result = await InvokeAsync(tool, new AIFunctionArguments
        {
            ["type"] = null,
            ["title"] = "T",
            ["description"] = "D",
            ["severity"] = "low",
        });
        Assert.Equal("Type is required.", result);
    }

    [Fact]
    public async Task RaiseIssue_WhitespaceType_ReturnsTypeRequired()
    {
        var store = new FakeIssueStore();
        var tool = Tool("raise_issue", issueStore: store);
        var result = await InvokeAsync(tool, new AIFunctionArguments
        {
            ["type"] = "   ",
            ["title"] = "T",
            ["description"] = "D",
            ["severity"] = "low",
        });
        Assert.Equal("Type is required.", result);
    }

    [Fact]
    public async Task RaiseIssue_InvalidType_ReturnsErrorMessage()
    {
        var store = new FakeIssueStore();
        var tool = Tool("raise_issue", issueStore: store);
        var result = await InvokeAsync(tool, new AIFunctionArguments
        {
            ["type"] = "garbage",
            ["title"] = "T",
            ["description"] = "D",
            ["severity"] = "low",
        });
        Assert.Contains("Unknown issue type", result);
    }

    [Theory]
    [InlineData("bug")]
    [InlineData("suggestion")]
    [InlineData("concern")]
    [InlineData("code_quality")]
    [InlineData("codequality")]
    [InlineData("workflow")]
    public async Task RaiseIssue_ValidTypes_CreateIssue(string type)
    {
        var store = new FakeIssueStore();
        var tool = Tool("raise_issue", issueStore: store, sourceGoalId: "g-1");
        var result = await InvokeAsync(tool, new AIFunctionArguments
        {
            ["type"] = type,
            ["title"] = "Test Title",
            ["description"] = "Test Description",
            ["severity"] = "low",
        });
        Assert.StartsWith("Issue created:", result);
        Assert.Single(store.Issues);
    }

    [Fact]
    public async Task RaiseIssue_NullTitle_ReturnsTitleRequired()
    {
        var store = new FakeIssueStore();
        var tool = Tool("raise_issue", issueStore: store);
        var result = await InvokeAsync(tool, new AIFunctionArguments
        {
            ["type"] = "bug",
            ["title"] = null,
            ["description"] = "D",
            ["severity"] = "low",
        });
        Assert.Equal("Title is required.", result);
    }

    [Fact]
    public async Task RaiseIssue_WhitespaceTitle_ReturnsTitleRequired()
    {
        var store = new FakeIssueStore();
        var tool = Tool("raise_issue", issueStore: store);
        var result = await InvokeAsync(tool, new AIFunctionArguments
        {
            ["type"] = "bug",
            ["title"] = "   ",
            ["description"] = "D",
            ["severity"] = "low",
        });
        Assert.Equal("Title is required.", result);
    }

    [Fact]
    public async Task RaiseIssue_NullDescription_ReturnsDescriptionRequired()
    {
        var store = new FakeIssueStore();
        var tool = Tool("raise_issue", issueStore: store);
        var result = await InvokeAsync(tool, new AIFunctionArguments
        {
            ["type"] = "bug",
            ["title"] = "T",
            ["description"] = null,
            ["severity"] = "low",
        });
        Assert.Equal("Description is required.", result);
    }

    [Fact]
    public async Task RaiseIssue_NullSeverity_DefaultsToLow()
    {
        var store = new FakeIssueStore();
        var tool = Tool("raise_issue", issueStore: store, sourceGoalId: "g-1");
        var result = await InvokeAsync(tool, new AIFunctionArguments
        {
            ["type"] = "bug",
            ["title"] = "T",
            ["description"] = "D",
            ["severity"] = null,
        });
        Assert.StartsWith("Issue created:", result);
        var issue = store.Issues.Single().Value;
        Assert.Equal(IssueSeverity.Low, issue.Severity);
    }

    /// <summary>
    /// Regression: invoking <c>raise_issue</c> with the <c>severity</c> key OMITTED entirely
    /// must succeed and default to <see cref="IssueSeverity.Low"/>. This is distinct from the
    /// explicit-null test above: Microsoft.Extensions.AI derives argument optionality from the
    /// parameter's DEFAULT VALUE, not its nullable-reference annotation. Removing the
    /// `= null` default on the lambda parameter puts `severity` back into the required binding
    /// set and makes this invocation fail before the lambda's null-handling can run.
    /// </summary>
    [Fact]
    public async Task RaiseIssue_SeverityArgumentOmitted_DefaultsToLow()
    {
        var store = new FakeIssueStore();
        var tool = Tool("raise_issue", issueStore: store, sourceGoalId: "g-1");

        // NOTE: no ["severity"] key at all — this is the point of the test.
        var result = await InvokeAsync(tool, new AIFunctionArguments
        {
            ["type"] = "bug",
            ["title"] = "T",
            ["description"] = "D",
        });

        Assert.StartsWith("Issue created:", result);
        var issue = store.Issues.Single().Value;
        Assert.Equal(IssueSeverity.Low, issue.Severity);
    }

    /// <summary>
    /// Contract assertion over the generated AIFunction schema: <c>type</c>, <c>title</c> and
    /// <c>description</c> must be required, and <c>severity</c> must be optional. Asserting on
    /// the JSON schema's "required" array makes the `severity = null` default removal-proof —
    /// dropping the default puts "severity" back into the required array and fails this test.
    /// </summary>
    [Fact]
    public void RaiseIssue_Schema_TypeTitleDescriptionRequired_SeverityOptional()
    {
        var store = new FakeIssueStore();
        var tool = Tool("raise_issue", issueStore: store, sourceGoalId: "g-1");

        var required = tool.JsonSchema.TryGetProperty("required", out var requiredEl)
            ? requiredEl.EnumerateArray().Select(e => e.GetString()).ToHashSet()
            : [];

        Assert.Contains("type", required);
        Assert.Contains("title", required);
        Assert.Contains("description", required);
        Assert.DoesNotContain("severity", required);

        // The schema must still describe severity as a bindable property, otherwise the
        // "not required" assertion above could pass vacuously by the parameter disappearing.
        var properties = tool.JsonSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("severity", out _),
            "severity must still be exposed as a schema property");

        // CancellationToken is injected by the factory and must never surface as a model-visible
        // argument, even though it now carries an explicit default.
        Assert.False(properties.TryGetProperty("ct", out _),
            "CancellationToken must not be exposed as a model-visible parameter");
    }

    /// <summary>
    /// Reflection-level counterpart to the schema test: the underlying lambda parameters must
    /// carry the defaults that drive optionality (<c>severity</c> and <c>ct</c> defaulted,
    /// the three content parameters not).
    /// </summary>
    [Fact]
    public void RaiseIssue_UnderlyingMethod_SeverityAndCancellationTokenHaveDefaults()
    {
        var store = new FakeIssueStore();
        var tool = Tool("raise_issue", issueStore: store, sourceGoalId: "g-1");

        var parameters = tool.UnderlyingMethod!.GetParameters()
            .ToDictionary(p => p.Name!, p => p.HasDefaultValue);

        Assert.False(parameters["type"]);
        Assert.False(parameters["title"]);
        Assert.False(parameters["description"]);
        Assert.True(parameters["severity"]);
        Assert.True(parameters["ct"]);
    }

    [Fact]
    public async Task RaiseIssue_InvalidSeverity_ReturnsErrorMessage()
    {
        var store = new FakeIssueStore();
        var tool = Tool("raise_issue", issueStore: store);
        var result = await InvokeAsync(tool, new AIFunctionArguments
        {
            ["type"] = "bug",
            ["title"] = "T",
            ["description"] = "D",
            ["severity"] = "critical",
        });
        Assert.Contains("Unknown severity", result);
    }

    [Theory]
    [InlineData("low", IssueSeverity.Low)]
    [InlineData("medium", IssueSeverity.Medium)]
    [InlineData("high", IssueSeverity.High)]
    public async Task RaiseIssue_ValidSeverity_UsesProvidedValue(string severity, IssueSeverity expected)
    {
        var store = new FakeIssueStore();
        var tool = Tool("raise_issue", issueStore: store, sourceGoalId: "g-1");
        var result = await InvokeAsync(tool, new AIFunctionArguments
        {
            ["type"] = "bug",
            ["title"] = "T",
            ["description"] = "D",
            ["severity"] = severity,
        });
        Assert.StartsWith("Issue created:", result);
        var issue = store.Issues.Single().Value;
        Assert.Equal(expected, issue.Severity);
    }

    [Fact]
    public async Task RaiseIssue_SetsSourceRoleToBrain()
    {
        var store = new FakeIssueStore();
        var tool = Tool("raise_issue", issueStore: store, sourceGoalId: "goal-42");
        await InvokeAsync(tool, new AIFunctionArguments
        {
            ["type"] = "bug",
            ["title"] = "T",
            ["description"] = "D",
            ["severity"] = "low",
        });
        var issue = store.Issues.Single().Value;

        // All four source-metadata fields are asserted together: a partial assertion could
        // pass vacuously while another field silently regressed.
        Assert.Equal("brain", issue.SourceRole);
        Assert.Equal("goal-42", issue.SourceGoalId);
        Assert.Equal(0, issue.SourceIteration);
        Assert.Empty(issue.RepositoryNames);
    }

    [Fact]
    public async Task RaiseIssue_SetsSourceGoalIdFromParameter()
    {
        var store = new FakeIssueStore();
        var tool = Tool("raise_issue", issueStore: store, sourceGoalId: "goal-abc");
        await InvokeAsync(tool, new AIFunctionArguments
        {
            ["type"] = "bug",
            ["title"] = "T",
            ["description"] = "D",
            ["severity"] = "low",
        });
        var issue = store.Issues.Single().Value;

        Assert.Equal("goal-abc", issue.SourceGoalId);
        Assert.Equal("brain", issue.SourceRole);
        Assert.Equal(0, issue.SourceIteration);
        Assert.Empty(issue.RepositoryNames);
    }

    [Fact]
    public async Task RaiseIssue_NullSourceGoalId_LeavesSourceGoalIdNullAndKeepsOtherMetadata()
    {
        var store = new FakeIssueStore();
        var tool = Tool("raise_issue", issueStore: store, sourceGoalId: null);
        await InvokeAsync(tool, new AIFunctionArguments
        {
            ["type"] = "bug",
            ["title"] = "T",
            ["description"] = "D",
            ["severity"] = "low",
        });
        var issue = store.Issues.Single().Value;

        Assert.Null(issue.SourceGoalId);
        Assert.Equal("brain", issue.SourceRole);
        Assert.Equal(0, issue.SourceIteration);
        Assert.Empty(issue.RepositoryNames);
    }

    [Fact]
    public async Task RaiseIssue_CollisionRetry_UsesGuidAndConstructsNewIssue()
    {
        var store = new FakeIssueStore(throwOnCreateOnce: true);
        var tool = Tool("raise_issue", issueStore: store, sourceGoalId: "g-1");
        var result = await InvokeAsync(tool, new AIFunctionArguments
        {
            ["type"] = "bug",
            ["title"] = "Collision Title",
            ["description"] = "Collision Desc",
            ["severity"] = "high",
        });

        Assert.StartsWith("Issue created:", result);
        Assert.Single(store.Issues);

        // Both attempts must have been observed: the rejected first one and the retry.
        Assert.Equal(2, store.CreateAttempts.Count);
        var firstIssue = store.CreateAttempts[0];
        var secondIssue = store.CreateAttempts[1];

        // The retry must construct a genuinely NEW Issue instance. `Issue` is a class, not a
        // record, so a `with` expression is unavailable — but mutating and re-submitting the
        // SAME instance would also satisfy the ID assertions below. Reference inequality is the
        // only assertion that actually proves a new object was built.
        Assert.NotSame(firstIssue, secondIssue);

        // The slug for "Collision Title" is "collision-title"; the retry appends a GUID suffix.
        Assert.Equal("collision-title", firstIssue.Id);
        Assert.NotEqual(firstIssue.Id, secondIssue.Id);
        Assert.StartsWith("collision-title-", secondIssue.Id);

        // Every content field must survive the retry unchanged on the new instance.
        Assert.Equal(firstIssue.Title, secondIssue.Title);
        Assert.Equal(firstIssue.Description, secondIssue.Description);
        Assert.Equal(firstIssue.Type, secondIssue.Type);
        Assert.Equal(firstIssue.Severity, secondIssue.Severity);

        // The persisted issue is the retry instance, with the expected values.
        var issue = store.Issues.Single().Value;
        Assert.Same(secondIssue, issue);
        Assert.StartsWith("collision-title-", issue.Id);
        Assert.Equal(IssueType.Bug, issue.Type);
        Assert.Equal("Collision Title", issue.Title);
        Assert.Equal("Collision Desc", issue.Description);
        Assert.Equal(IssueSeverity.High, issue.Severity);

        // Source metadata must be preserved through the retry path too.
        Assert.Equal("brain", issue.SourceRole);
        Assert.Equal("g-1", issue.SourceGoalId);
        Assert.Equal(0, issue.SourceIteration);
        Assert.Empty(issue.RepositoryNames);
    }

    [Fact]
    public async Task RaiseIssue_EveryCreateFails_ReturnsError()
    {
        var store = new FakeIssueStore(throwOnEveryCreate: true);
        var tool = Tool("raise_issue", issueStore: store);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(tool, new AIFunctionArguments
            {
                ["type"] = "bug",
                ["title"] = "T",
                ["description"] = "D",
                ["severity"] = "low",
            }));
        Assert.Contains("Failed to create issue", ex.Message);
    }

    [Fact]
    public async Task RaiseIssue_CancellationToken_PropagatedToStore()
    {
        var store = new CapturingIssueStore();
        var tool = Tool("raise_issue", issueStore: store, sourceGoalId: "g-1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await tool.InvokeAsync(new AIFunctionArguments
        {
            ["type"] = "bug",
            ["title"] = "T",
            ["description"] = "D",
            ["severity"] = "low",
        }, cts.Token);

        Assert.True(store.LastGetIssueToken.HasValue, "GetIssueAsync was never called");
        Assert.True(store.LastCreateToken.HasValue, "CreateIssueAsync was never called");

        // Identity comparison, not just IsCancellationRequested: checking the flag alone would
        // still pass if the SUT forwarded some unrelated already-cancelled token (or
        // CancellationToken.None from a differently-cancelled source). CancellationToken is a
        // struct whose equality compares the underlying source, so this pins the exact token.
        Assert.Equal(cts.Token, store.LastGetIssueToken!.Value);
        Assert.Equal(cts.Token, store.LastCreateToken!.Value);
    }

    /// <summary>
    /// Companion to the cancelled-token test: with a LIVE (non-cancelled) token the store must
    /// still receive that exact token rather than <see cref="CancellationToken.None"/>. Without
    /// this, a SUT that forwarded a hard-coded cancelled token could pass the test above.
    /// </summary>
    [Fact]
    public async Task RaiseIssue_LiveCancellationToken_ForwardedVerbatimToStore()
    {
        var store = new CapturingIssueStore();
        var tool = Tool("raise_issue", issueStore: store, sourceGoalId: "g-1");
        using var cts = new CancellationTokenSource();

        var result = await InvokeWithTokenAsync(tool, new AIFunctionArguments
        {
            ["type"] = "bug",
            ["title"] = "T",
            ["description"] = "D",
            ["severity"] = "low",
        }, cts.Token);

        Assert.StartsWith("Issue created:", result);
        Assert.Equal(cts.Token, store.LastGetIssueToken!.Value);
        Assert.Equal(cts.Token, store.LastCreateToken!.Value);
        Assert.NotEqual(CancellationToken.None, store.LastCreateToken!.Value);
    }

    // ──────────────────────────────────────────────────────────
    //  Fake IIssueStore implementations for raise_issue tests
    // ──────────────────────────────────────────────────────────

    private sealed class FakeIssueStore : IIssueStore
    {
        public Dictionary<string, Issue> Issues { get; } = new();

        /// <summary>
        /// Every <see cref="Issue"/> instance handed to <see cref="CreateIssueAsync"/>, in call
        /// order — including attempts that were rejected. Retaining the rejected instance is what
        /// lets the collision test assert a genuinely NEW object was constructed on retry.
        /// </summary>
        public List<Issue> CreateAttempts { get; } = [];

        private readonly bool _throwOnCreateOnce;
        private readonly bool _throwOnEveryCreate;
        private bool _hasThrown;

        public FakeIssueStore(bool throwOnCreateOnce = false, bool throwOnEveryCreate = false)
        {
            _throwOnCreateOnce = throwOnCreateOnce;
            _throwOnEveryCreate = throwOnEveryCreate;
        }

        public Task<IReadOnlyList<Issue>> GetAllIssuesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Issue>>(Issues.Values.ToList());

        public Task<IReadOnlyList<Issue>> GetIssuesAsync(
            IssueStatus? status = null, IssueType? type = null, IssueSeverity? severity = null,
            string? repository = null, string? sourceGoalId = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Issue>>(Issues.Values.ToList());

        public Task<Issue?> GetIssueAsync(string issueId, CancellationToken ct = default)
            => Task.FromResult(Issues.TryGetValue(issueId, out var issue) ? issue : null);

        public Task<Issue> CreateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            // Recorded before any rejection so failed attempts are captured too.
            CreateAttempts.Add(issue);

            if (_throwOnEveryCreate)
                throw new InvalidOperationException($"Failed to create issue '{issue.Id}'");
            if (_throwOnCreateOnce && !_hasThrown)
            {
                _hasThrown = true;
                throw new InvalidOperationException($"Failed to create issue '{issue.Id}'");
            }
            if (Issues.ContainsKey(issue.Id))
                throw new InvalidOperationException($"Failed to create issue '{issue.Id}'");
            Issues[issue.Id] = issue;
            return Task.FromResult(issue);
        }

        public Task UpdateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            Issues[issue.Id] = issue;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteIssueAsync(string issueId, CancellationToken ct = default)
            => Task.FromResult(Issues.Remove(issueId));
    }

    /// <summary>
    /// Fake <see cref="IIssueStore"/> that captures the <see cref="CancellationToken"/>
    /// passed to <see cref="IIssueStore.GetIssueAsync"/> and <see cref="IIssueStore.CreateIssueAsync"/>.
    /// </summary>
    private sealed class CapturingIssueStore : IIssueStore
    {
        public Dictionary<string, Issue> Issues { get; } = new();
        public CancellationToken? LastGetIssueToken { get; private set; }
        public CancellationToken? LastCreateToken { get; private set; }

        public Task<IReadOnlyList<Issue>> GetAllIssuesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Issue>>(Issues.Values.ToList());

        public Task<IReadOnlyList<Issue>> GetIssuesAsync(
            IssueStatus? status = null, IssueType? type = null, IssueSeverity? severity = null,
            string? repository = null, string? sourceGoalId = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Issue>>(Issues.Values.ToList());

        public Task<Issue?> GetIssueAsync(string issueId, CancellationToken ct = default)
        {
            LastGetIssueToken = ct;
            return Task.FromResult(Issues.TryGetValue(issueId, out var issue) ? issue : null);
        }

        public Task<Issue> CreateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            LastCreateToken = ct;
            Issues[issue.Id] = issue;
            return Task.FromResult(issue);
        }

        public Task UpdateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            Issues[issue.Id] = issue;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteIssueAsync(string issueId, CancellationToken ct = default)
            => Task.FromResult(Issues.Remove(issueId));
    }
}

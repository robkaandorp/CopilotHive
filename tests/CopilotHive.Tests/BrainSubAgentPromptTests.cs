using System.Reflection;

using CopilotHive.Actors;
using CopilotHive.Configuration;
using CopilotHive.Orchestration;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using SharpCoder.SubAgents;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Tests for the sub-agent planning guidance in the Brain system prompt and its consistent
/// application across all three <see cref="DistributedBrain"/> prompt-assembly sites.
/// </summary>
public class BrainSubAgentPromptTests
{
    private const string PlanningSentence = "delegate the exploration to a start_sub_agent sub-session";

    private static readonly FieldInfo SystemPromptField = typeof(DistributedBrain)
        .GetField("_systemPrompt", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static string SystemPrompt(DistributedBrain brain) => (string)SystemPromptField.GetValue(brain)!;

    private static HiveConfigFile ConfigWithModels() => new()
    {
        Models = new ModelsConfig
        {
            AvailableModels = [new ModelEntry { Name = "copilot/model-a", ContextWindow = 128_000 }],
        },
    };

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "stub")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"brain-subagent-prompt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempPath(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
    }

    [Fact]
    public void BuildSystemPrompt_WhenEnabled_ContainsPlanningSentence()
    {
        var prompt = BrainPromptBuilder.BuildSystemPrompt(subAgentsEnabled: true);

        Assert.Contains(PlanningSentence, prompt, StringComparison.Ordinal);
        Assert.Contains("SUB-AGENT PLANNING", prompt, StringComparison.Ordinal);
        Assert.StartsWith(BrainPromptBuilder.DefaultSystemPrompt, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSystemPrompt_WhenDisabled_OmitsPlanningSentence()
    {
        var prompt = BrainPromptBuilder.BuildSystemPrompt(subAgentsEnabled: false);

        Assert.Equal(BrainPromptBuilder.DefaultSystemPrompt, prompt);
        Assert.DoesNotContain("start_sub_agent", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("delegate the exploration", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSystemPrompt_DoesNotRestateSharpCoderBuiltInSubAgentText()
    {
        var added = BrainPromptBuilder.BuildSystemPrompt(subAgentsEnabled: true)
            .Substring(BrainPromptBuilder.DefaultSystemPrompt.Length);

        Assert.DoesNotContain("You can delegate self-contained subtasks", added, StringComparison.Ordinal);
        Assert.DoesNotContain("Sub-sessions run read-only by default", added, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DistributedBrain_WithAvailableModels_KeepsPlanningSentenceAtAllThreePromptSites()
    {
        var stateDir = CreateTempDir();
        try
        {
            await using var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: stateDir, hiveConfig: ConfigWithModels(), chatClient: new StubChatClient());
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            // Site 1 — construction.
            Assert.Contains(PlanningSentence, SystemPrompt(brain), StringComparison.Ordinal);

            // Site 2 — orchestrator instruction injection (no connect needed: the prompt is
            // updated before the actor message is sent, and a missing actor is tolerated).
            await brain.InjectOrchestratorInstructionsAsync("NEW_RULES", TestContext.Current.CancellationToken);
            Assert.Contains(PlanningSentence, SystemPrompt(brain), StringComparison.Ordinal);
            Assert.Contains("NEW_RULES", SystemPrompt(brain), StringComparison.Ordinal);

            // Site 3 — reset.
            await brain.ResetSessionAsync(TestContext.Current.CancellationToken);
            Assert.Contains(PlanningSentence, SystemPrompt(brain), StringComparison.Ordinal);
        }
        finally { DeleteTempPath(stateDir); }
    }

    [Fact]
    public async Task DistributedBrain_WithoutAvailableModels_OmitsPlanningSentenceAtAllThreePromptSites()
    {
        var stateDir = CreateTempDir();
        try
        {
            await using var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: stateDir, chatClient: new StubChatClient());
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            Assert.DoesNotContain("start_sub_agent", SystemPrompt(brain), StringComparison.Ordinal);

            await brain.InjectOrchestratorInstructionsAsync("NEW_RULES", TestContext.Current.CancellationToken);
            Assert.DoesNotContain("start_sub_agent", SystemPrompt(brain), StringComparison.Ordinal);

            await brain.ResetSessionAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("start_sub_agent", SystemPrompt(brain), StringComparison.Ordinal);
        }
        finally { DeleteTempPath(stateDir); }
    }

    /// <summary>
    /// End-to-end proof that the sub-agent model snapshot survives a live config reload:
    /// the brain's prompt AND the options actually built by its <see cref="BrainActor"/>
    /// must still reflect the models captured at construction time.
    /// </summary>
    [Fact]
    public async Task DistributedBrain_AfterConfigMutation_PromptAndActorOptionsUseOriginalSnapshot()
    {
        var stateDir = CreateTempDir();
        var config = ConfigWithModels();
        var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
            stateDir: stateDir, hiveConfig: config, chatClient: new StubChatClient());
        try
        {
            // Connecting creates the BrainActor with the snapshot captured at construction.
            await brain.ConnectAsync(TestContext.Current.CancellationToken);

            // Simulate a config reload that mutates the live collection in place.
            config.Models!.AvailableModels!.Clear();
            config.Models.AvailableModels.Add(new ModelEntry { Name = "copilot/mutated", ContextWindow = 1_000 });

            // (a) The prompt still carries the sub-agent planning guidance.
            Assert.Contains(PlanningSentence, SystemPrompt(brain), StringComparison.Ordinal);

            // (b) The real actor still builds options from the ORIGINAL catalog.
            var actor = (BrainActor?)typeof(DistributedBrain)
                .GetField("_brainActor", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(brain);
            Assert.NotNull(actor);

            var options = (SubAgentOptions?)typeof(BrainActor)
                .GetMethod("BuildSubAgentOptions", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(actor, null);

            Assert.NotNull(options);
            var model = Assert.Single(options!.AvailableModels);
            Assert.Equal("copilot/model-a", model.Id);
            Assert.Equal(128_000, model.ContextWindow);
            Assert.DoesNotContain(options.AvailableModels, m => m.Id == "copilot/mutated");
        }
        finally
        {
            await brain.DisposeAsync();
            DeleteTempPath(stateDir);
        }
    }

    [Fact]
    public async Task DistributedBrain_WithEmptyAvailableModels_DisablesSubAgents()
    {
        var stateDir = CreateTempDir();
        try
        {
            var config = new HiveConfigFile { Models = new ModelsConfig { AvailableModels = [] } };
            using var unusedClient = new StubChatClient();
            await using var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: stateDir, hiveConfig: config, chatClient: unusedClient);

            var enabled = (bool)typeof(DistributedBrain)
                .GetField("_subAgentsEnabled", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(brain)!;

            Assert.False(enabled);
            Assert.DoesNotContain("start_sub_agent", SystemPrompt(brain), StringComparison.Ordinal);
        }
        finally { DeleteTempPath(stateDir); }
    }

    // ── Config → SubAgentModelEntry mapping (DistributedBrain consumer) ──────

    /// <summary>Reads the Brain's construction-time sub-agent catalog snapshot.</summary>
    private static IReadOnlyList<SubAgentModelEntry>? SubAgentModels(DistributedBrain brain) =>
        (IReadOnlyList<SubAgentModelEntry>?)typeof(DistributedBrain)
            .GetField("_subAgentModels", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(brain);

    [Fact]
    public async Task DistributedBrain_MapsConfiguredDescription_IntoSubAgentModelEntry()
    {
        var stateDir = CreateTempDir();
        try
        {
            var config = new HiveConfigFile
            {
                Models = new ModelsConfig
                {
                    AvailableModels =
                    [
                        new ModelEntry
                        {
                            Name = "copilot/model-a",
                            ContextWindow = 128_000,
                            Description = "Fast model for quick tasks"
                        }
                    ]
                }
            };

            using var unusedClient = new StubChatClient();
            await using var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: stateDir, hiveConfig: config, chatClient: unusedClient);

            var entry = Assert.Single(SubAgentModels(brain)!);
            Assert.Equal("copilot/model-a", entry.Name);
            Assert.Equal(128_000, entry.ContextWindow);
            // Must be the configured text — NOT the auto-generated "Configured model, ..." string.
            Assert.Equal("Fast model for quick tasks", entry.Description);
        }
        finally { DeleteTempPath(stateDir); }
    }

    [Fact]
    public async Task DistributedBrain_UsesCuratedSubAgentModels_OverAvailableModels()
    {
        var stateDir = CreateTempDir();
        try
        {
            var config = new HiveConfigFile
            {
                Models = new ModelsConfig
                {
                    AvailableModels =
                    [
                        new ModelEntry { Name = "copilot/available-a", ContextWindow = 100_000 },
                        new ModelEntry { Name = "copilot/available-b", ContextWindow = 200_000 },
                    ],
                    SubAgentModels =
                    [
                        new ModelEntry
                        {
                            Name = "copilot/curated-only",
                            ContextWindow = 64_000,
                            Description = "Curated pick"
                        }
                    ]
                }
            };

            using var unusedClient = new StubChatClient();
            await using var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: stateDir, hiveConfig: config, chatClient: unusedClient);

            var entry = Assert.Single(SubAgentModels(brain)!);
            Assert.Equal("copilot/curated-only", entry.Name);
            Assert.Equal("Curated pick", entry.Description);
        }
        finally { DeleteTempPath(stateDir); }
    }

    [Fact]
    public async Task DistributedBrain_NullDescription_IsPreservedAsNull_ForDownstreamFallback()
    {
        var stateDir = CreateTempDir();
        try
        {
            using var unusedClient = new StubChatClient();
            await using var brain = new DistributedBrain("copilot/test-model", NullLogger<DistributedBrain>.Instance,
                stateDir: stateDir, hiveConfig: ConfigWithModels(), chatClient: unusedClient);

            Assert.Null(Assert.Single(SubAgentModels(brain)!).Description);
        }
        finally { DeleteTempPath(stateDir); }
    }
}

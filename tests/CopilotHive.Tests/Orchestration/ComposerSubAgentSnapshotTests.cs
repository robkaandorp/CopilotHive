using System.Reflection;

using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Verifies that <see cref="Composer"/> takes a genuine deep-copy snapshot of the sub-agent
/// model catalog at construction time. The catalog lives on the mutable
/// <see cref="HiveConfigFile"/> singleton, which the dashboard mutates in place via
/// <c>ConfigModelService</c>. Copying only the list wrapper would still expose the shared
/// <see cref="ModelEntry"/> references.
/// </summary>
public sealed class ComposerSubAgentSnapshotTests : IDisposable
{
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;

    public ComposerSubAgentSnapshotTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);
    }

    public void Dispose() => _dbContext.Dispose();

    /// <summary>Reads the sub-agent catalog snapshot the Composer handed to its agent service.</summary>
    private static IReadOnlyList<ModelEntry> GetSnapshot(Composer composer)
    {
        var agentService = typeof(Composer)
            .GetField("_agentService", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(composer)!;

        return (IReadOnlyList<ModelEntry>)typeof(ComposerAgentService)
            .GetField("_subAgentModels", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(agentService)!;
    }

    [Fact]
    public async Task Composer_SubAgentSnapshot_IsDeepCopy_NotSharedWithLiveConfig()
    {
        var sourceEntry = new ModelEntry
        {
            Name = "test-model",
            ContextWindow = 200000,
            Description = "Original desc",
        };
        var hiveConfig = new HiveConfigFile
        {
            Models = new ModelsConfig { AvailableModels = [sourceEntry] }
        };

        var repoManager = new Mock<IBrainRepoManager>();
        repoManager.SetupGet(r => r.WorkDirectory).Returns(Path.GetTempPath());

        await using var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            repoManager: repoManager.Object,
            stateDir: Path.GetTempPath(),
            hiveConfig: hiveConfig,
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        var snapshot = GetSnapshot(composer);
        var snapshotEntry = Assert.Single(snapshot);
        Assert.NotSame(sourceEntry, snapshotEntry);

        // Mutate the live config entry in place — exactly what ConfigModelService does.
        sourceEntry.Name = "mutated";
        sourceEntry.ContextWindow = 999;
        sourceEntry.Description = "changed";
        sourceEntry.ReasoningEffort = "high";

        // The construction-time snapshot must be unaffected.
        Assert.Equal("test-model", snapshotEntry.Name);
        Assert.Equal(200000, snapshotEntry.ContextWindow);
        Assert.Equal("Original desc", snapshotEntry.Description);
        Assert.Null(snapshotEntry.ReasoningEffort);
    }

    [Fact]
    public async Task Composer_SubAgentSnapshot_PrefersCuratedSubAgentModels()
    {
        var hiveConfig = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "available-only" }],
                SubAgentModels = [new ModelEntry { Name = "curated", Description = "Curated pick" }],
            }
        };

        var repoManager = new Mock<IBrainRepoManager>();
        repoManager.SetupGet(r => r.WorkDirectory).Returns(Path.GetTempPath());

        await using var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            repoManager: repoManager.Object,
            stateDir: Path.GetTempPath(),
            hiveConfig: hiveConfig,
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        var entry = Assert.Single(GetSnapshot(composer));
        Assert.Equal("curated", entry.Name);
        Assert.Equal("Curated pick", entry.Description);
    }

    [Fact]
    public async Task Composer_CuratedEntryWithMatchingAvailable_MergesContextWindow()
    {
        var hiveConfig = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "ollama-cloud/glm-5.2", ContextWindow = 976000 },
                ],
                SubAgentModels =
                [
                    new ModelEntry { Name = "ollama-cloud/glm-5.2" },
                ],
            }
        };

        var repoManager = new Mock<IBrainRepoManager>();
        repoManager.SetupGet(r => r.WorkDirectory).Returns(Path.GetTempPath());

        await using var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            repoManager: repoManager.Object,
            stateDir: Path.GetTempPath(),
            hiveConfig: hiveConfig,
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        var entry = Assert.Single(GetSnapshot(composer));
        Assert.Equal("ollama-cloud/glm-5.2", entry.Name);
        // ContextWindow inherited from the matching available_models entry via GetSubAgentModels merge
        Assert.Equal(976000, entry.ContextWindow);
    }

    /// <summary>
    /// Removal-proof guard for the construction-time deep copy: dropping
    /// <c>SupportsVision = m.SupportsVision</c> from the <see cref="ModelEntry"/> copy in
    /// <c>Composer</c> leaves the snapshot entry at <c>null</c>, so this assertion fails.
    /// Without it the Composer silently reported <c>supportsVision: false</c> to
    /// <c>SubAgentModelInfo</c> even when the config explicitly set <c>supports_vision: true</c>.
    /// </summary>
    [Fact]
    public async Task Composer_SubAgentSnapshot_CopiesSupportsVisionTrue()
    {
        var hiveConfig = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                SubAgentModels = [new ModelEntry { Name = "vision-model", SupportsVision = true }],
            }
        };

        var repoManager = new Mock<IBrainRepoManager>();
        repoManager.SetupGet(r => r.WorkDirectory).Returns(Path.GetTempPath());

        await using var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            repoManager: repoManager.Object,
            stateDir: Path.GetTempPath(),
            hiveConfig: hiveConfig,
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        var entry = Assert.Single(GetSnapshot(composer));
        Assert.Equal("vision-model", entry.Name);
        Assert.NotNull(entry.SupportsVision);
        Assert.True(entry.SupportsVision);
    }

    /// <summary>
    /// An explicit <c>supports_vision: false</c> on a curated entry must survive the snapshot as
    /// <c>false</c> — never collapsed to <c>null</c> — so it keeps overriding an inherited
    /// <c>true</c> from the matching available_models entry.
    /// </summary>
    [Fact]
    public async Task Composer_SubAgentSnapshot_CopiesExplicitSupportsVisionFalse_OverridingAvailable()
    {
        var hiveConfig = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "shared-model", SupportsVision = true }],
                SubAgentModels = [new ModelEntry { Name = "shared-model", SupportsVision = false }],
            }
        };

        var repoManager = new Mock<IBrainRepoManager>();
        repoManager.SetupGet(r => r.WorkDirectory).Returns(Path.GetTempPath());

        await using var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            repoManager: repoManager.Object,
            stateDir: Path.GetTempPath(),
            hiveConfig: hiveConfig,
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        var entry = Assert.Single(GetSnapshot(composer));
        Assert.NotNull(entry.SupportsVision);
        Assert.False(entry.SupportsVision);
    }

    /// <summary>
    /// A curated entry that leaves <c>supports_vision</c> unset inherits the flag from the
    /// matching available_models entry through the <c>GetSubAgentModels</c> merge, and the
    /// inherited <c>true</c> must survive the Composer's deep copy.
    /// </summary>
    [Fact]
    public async Task Composer_SubAgentSnapshot_InheritsSupportsVisionFromAvailableModel()
    {
        var hiveConfig = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "shared-model", SupportsVision = true }],
                SubAgentModels = [new ModelEntry { Name = "shared-model" }],
            }
        };

        var repoManager = new Mock<IBrainRepoManager>();
        repoManager.SetupGet(r => r.WorkDirectory).Returns(Path.GetTempPath());

        await using var composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            _store,
            repoManager: repoManager.Object,
            stateDir: Path.GetTempPath(),
            hiveConfig: hiveConfig,
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        var entry = Assert.Single(GetSnapshot(composer));
        Assert.NotNull(entry.SupportsVision);
        Assert.True(entry.SupportsVision);
    }
}

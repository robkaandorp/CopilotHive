using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SharpCoder;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Tests for the deferred original-admin migration seam (<see cref="ComposerLegacyMigration"/>),
/// driven with the prerequisite goal's real original-admin contract: a real <see cref="UserService"/>
/// over a file-backed SQLite database whose lowest-id activated account (bootstrapped through
/// <c>CreateOrUpdateUserAsync</c>) is the ONLY identity allowed to claim the legacy session.
/// Nothing in production invokes the seam yet.
/// </summary>
public sealed class ComposerLegacyMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-migration-{Guid.NewGuid():N}");
    private readonly FileDbFactory _factory;
    private readonly UserService _users;

    public ComposerLegacyMigrationTests()
    {
        Directory.CreateDirectory(StateDir);
        _factory = new FileDbFactory(Path.Combine(_dir, "users.db"));
        _users = new UserService(_factory, NullLogger<UserService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TestHelpers.ForceDeleteDirectory(_dir);
    }

    private sealed class FileDbFactory : IDbContextFactory<CopilotHiveDbContext>
    {
        private readonly DbContextOptions<CopilotHiveDbContext> _options;

        public FileDbFactory(string path)
        {
            _options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False")
                .Options;
            using var ctx = CreateDbContext();
            ctx.Database.EnsureCreated();
        }

        public CopilotHiveDbContext CreateDbContext() => new(_options);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StateDir => Path.Combine(_dir, "state");

    private string LegacyFile => Path.Combine(StateDir, "composer-session.json");

    private int _originalId;
    private int _secondId;

    /// <summary>Bootstraps the original admin and activates a second ADMIN (higher id).</summary>
    private async Task SeedUsersAsync()
    {
        var original = await _users.CreateOrUpdateUserAsync(
            "100", "first", null, null, null, "tok", "ref", "2030-01-01T00:00:00.0000000Z", Ct);
        await _users.InviteUserAsync("second", UserRole.Admin, Ct);
        var second = await _users.ActivateInvitationAsync("second", "200", null, null, null, Ct);
        _originalId = original.Id;
        _secondId = second.Id;
    }

    private Task<ComposerUserState> OriginalAsync() => ComposerUserState.ResolveOriginalAdminAsync(_users, _originalId, Ct);

    private string PerUserFile(int id) => ComposerUserState.SessionFilePath(StateDir, ComposerUserState.ResolveAsync(_users, id, Ct).GetAwaiter().GetResult());

    private string Evidence(ComposerUserState user) => ComposerUserState.LegacyMigrationEvidencePath(StateDir, user);

    private async Task WriteLegacySessionAsync(string text)
    {
        var s = AgentSession.Create("composer");
        s.MessageHistory.Add(new ChatMessage(ChatRole.User, text));
        await s.SaveAsync(LegacyFile, Ct);
    }

    private static ComposerAgentService CreateAgent(string stateDir, ComposerUserState? user, LlmSessionRegistry? registry) =>
        new("test-model", 64000, 50, null, null, "system prompt", new List<AITool>(), null, stateDir,
            null, NullLogger<ComposerAgentService>.Instance, _ => new Mock<IChatClient>().Object,
            registry, ["test-model"], null, null, false, [], null, user);

    private static Composer CreateComposer(GoalStore store, string stateDir, ComposerUserState? user,
        Func<string, IChatClient>? factory = null, LlmSessionRegistry? registry = null) =>
        new("test-model", NullLogger<Composer>.Instance, store, stateDir: stateDir,
            chatClientFactory: factory ?? (_ => new Mock<IChatClient>().Object),
            sessionRegistry: registry, userState: user);

    private static string[] Messages(AgentSession s) => s.MessageHistory.Select(m => m.Text).ToArray();

    // ── Original-admin contract ──

    [Fact]
    public async Task OnlyTheLowestIdAccount_IsAccepted_AnyOtherIdentityIsRejected()
    {
        await SeedUsersAsync();
        Assert.True(_secondId > _originalId);

        var original = await OriginalAsync();
        Assert.Equal(_originalId, original.UserId);
        Assert.True(original.LegacyClaimEligible);

        // Another activated ADMIN, and arbitrary positive ids, are rejected loudly.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ComposerUserState.ResolveOriginalAdminAsync(_users, _secondId, Ct));
        Assert.Contains("not the original administrator", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ComposerUserState.ResolveOriginalAdminAsync(_users, _secondId + 41, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => ComposerUserState.ResolveOriginalAdminAsync(_users, 0, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => ComposerUserState.ResolveOriginalAdminAsync(_users, -_originalId, Ct));
    }

    [Fact]
    public async Task OriginalAdmin_StaysTheOnlyOwner_AfterDemotionAndDisable_AndOnEmptyDirectoryNobodyIs()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ComposerUserState.ResolveOriginalAdminAsync(_users, 1, Ct));

        await SeedUsersAsync();
        await _users.ChangeUserRoleAsync(_originalId, UserRole.Viewer, Ct);
        await _users.SetUserEnabledAsync(_originalId, enabled: false, Ct);

        // Independent of role and enabled flag: the lowest id is still the original; the Admin is not.
        Assert.True((await OriginalAsync()).LegacyClaimEligible);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ComposerUserState.ResolveOriginalAdminAsync(_users, _secondId, Ct));
    }

    [Fact]
    public async Task PlainActivatedIdentity_NeverClaimsLegacy_AndNeverReadsTheLegacyFile()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var second = await ComposerUserState.ResolveAsync(_users, _secondId, Ct);

        Assert.Equal(ComposerMigrationOutcome.NotEligible, await ComposerLegacyMigration.EnsureAsync(StateDir, second, Ct));
        var agent = CreateAgent(StateDir, second, new LlmSessionRegistry());
        await agent.ConnectAsync(Ct);

        Assert.False(agent.SessionLoadedFromDisk);
        Assert.Empty(agent.Session.MessageHistory);
        Assert.False(File.Exists(ComposerUserState.SessionFilePath(StateDir, second)));
        Assert.False(File.Exists(Evidence(second)));
        Assert.True(File.Exists(LegacyFile));
        Assert.True(second.PersistenceAllowed);
    }

    // ── Success ──

    [Fact]
    public async Task OriginalAdminConnect_CopiesVerifiesAndRecordsEvidence_KeepingTheLegacySource()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var legacyBytes = await File.ReadAllBytesAsync(LegacyFile, Ct);
        var original = await OriginalAsync();
        var registry = new LlmSessionRegistry();
        var agent = CreateAgent(StateDir, original, registry);

        Assert.False(original.PersistenceAllowed);
        await agent.ConnectAsync(Ct);

        Assert.True(original.PersistenceAllowed);
        Assert.True(agent.SessionLoadedFromDisk);
        Assert.Equal(["legacy-history"], Messages(agent.Session));
        Assert.Equal(legacyBytes, await File.ReadAllBytesAsync(ComposerUserState.SessionFilePath(StateDir, original), Ct));
        Assert.Equal(legacyBytes, await File.ReadAllBytesAsync(LegacyFile, Ct));
        Assert.True(File.Exists(Evidence(original)));
        Assert.Equal(["composer:" + _originalId], registry.GetAll().Select(s => s.SessionId).ToArray());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Evidence(original))!, "*.migrating-*")
            .Concat(Directory.GetFiles(Path.GetDirectoryName(Evidence(original))!, "*.tmp-*")));
    }

    [Fact]
    public async Task Ensure_ReportsMigratedThenAlreadyClaimed()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("h");
        var original = await OriginalAsync();

        Assert.Equal(ComposerMigrationOutcome.Migrated, await ComposerLegacyMigration.EnsureAsync(StateDir, original, Ct));
        Assert.Equal(ComposerMigrationOutcome.AlreadyClaimed, await ComposerLegacyMigration.EnsureAsync(StateDir, original, Ct));
    }

    // ── Missing source ──

    [Fact]
    public async Task MissingLegacySource_IsAGenuinelyNewSession_WithNoMarker_AndSavesWork()
    {
        await SeedUsersAsync();
        var original = await OriginalAsync();
        using var db = CopilotHiveDbContext.CreateInMemory();
        await using var composer = CreateComposer(new GoalStore(db, NullLogger<GoalStore>.Instance), StateDir, original);

        await composer.ConnectAsync(Ct);

        Assert.Equal(ComposerMigrationOutcome.NoLegacySource, await ComposerLegacyMigration.EnsureAsync(StateDir, original, Ct));
        Assert.False(composer.SessionLoadedFromDisk);
        Assert.False(File.Exists(Evidence(original)));
        await composer.SaveSessionAsync(Ct);
        Assert.True(File.Exists(ComposerUserState.SessionFilePath(StateDir, original)));
    }

    // ── Existing destination ──

    [Fact]
    public async Task DifferentExistingDestination_FailsConnect_ChangesNothing_AndBlocksPerUserSaves()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var original = await OriginalAsync();
        var dest = ComposerUserState.SessionFilePath(StateDir, original);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var existing = AgentSession.Create("composer");
        existing.MessageHistory.Add(new ChatMessage(ChatRole.User, "per-user-history"));
        await existing.SaveAsync(dest, Ct);
        var destBytes = await File.ReadAllBytesAsync(dest, Ct);
        var legacyBytes = await File.ReadAllBytesAsync(LegacyFile, Ct);
        var registry = new LlmSessionRegistry();
        using var db = CopilotHiveDbContext.CreateInMemory();
        await using var composer = CreateComposer(new GoalStore(db, NullLogger<GoalStore>.Instance), StateDir, original, registry: registry);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => composer.ConnectAsync(Ct));

        Assert.IsType<ComposerMigrationException>(ex is AggregateException a ? a.InnerException : ex);
        Assert.False(original.PersistenceAllowed);
        Assert.False(composer.IsConnected);
        Assert.Empty(registry.GetAll());
        Assert.False(File.Exists(Evidence(original)));
        await Assert.ThrowsAsync<ComposerMigrationException>(() => composer.SaveSessionAsync(Ct));
        Assert.Equal(destBytes, await File.ReadAllBytesAsync(dest, Ct));
        Assert.Equal(legacyBytes, await File.ReadAllBytesAsync(LegacyFile, Ct));
    }

    [Fact]
    public async Task IdenticalExistingDestinationWithoutEvidence_CompletesTheInterruptedClaim()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var original = await OriginalAsync();
        var dest = ComposerUserState.SessionFilePath(StateDir, original);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(LegacyFile, dest);
        var before = File.GetLastWriteTimeUtc(dest);

        Assert.Equal(ComposerMigrationOutcome.CompletedExistingDestination,
            await ComposerLegacyMigration.EnsureAsync(StateDir, original, Ct));

        Assert.True(File.Exists(Evidence(original)));
        Assert.Equal(before, File.GetLastWriteTimeUtc(dest));
        Assert.True(original.PersistenceAllowed);
    }

    // ── Corruption / IO ──

    [Fact]
    public async Task CorruptLegacySource_FailsPerUserConnect_NeverStartsFresh_AndBlocksSavesAndDeletes()
    {
        await SeedUsersAsync();
        await File.WriteAllTextAsync(LegacyFile, "{invalid json", Ct);
        var original = await OriginalAsync();
        var registry = new LlmSessionRegistry();
        var agent = CreateAgent(StateDir, original, registry);

        var ex = await Assert.ThrowsAsync<ComposerMigrationException>(() => agent.ConnectAsync(Ct));

        Assert.Contains("corrupt", ex.Message);
        Assert.False(agent.IsConnected);
        Assert.Empty(registry.GetAll());
        Assert.False(original.PersistenceAllowed);
        Assert.False(File.Exists(ComposerUserState.SessionFilePath(StateDir, original)));
        Assert.False(File.Exists(Evidence(original)));
        Assert.Equal("{invalid json", await File.ReadAllTextAsync(LegacyFile, Ct));
    }

    [Fact]
    public async Task FailedMigration_NeverLetsAComposerSaveOrDeleteAPerUserFile()
    {
        await SeedUsersAsync();
        await File.WriteAllTextAsync(LegacyFile, "{invalid json", Ct);
        var original = await OriginalAsync();
        var dest = ComposerUserState.SessionFilePath(StateDir, original);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        await File.WriteAllTextAsync(dest, "precious", Ct);
        using var db = CopilotHiveDbContext.CreateInMemory();
        await using var composer = CreateComposer(new GoalStore(db, NullLogger<GoalStore>.Instance), StateDir, original);

        await Assert.ThrowsAnyAsync<Exception>(() => composer.ConnectAsync(Ct));
        await Assert.ThrowsAsync<ComposerMigrationException>(() => composer.SaveSessionAsync(Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => composer.ResetSessionAsync(Ct));

        Assert.Equal("precious", await File.ReadAllTextAsync(dest, Ct));
    }

    [Fact]
    public async Task LockedLegacySource_IoFailure_FailsConnectWithoutEvidenceOrFreshSession()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var original = await OriginalAsync();
        var agent = CreateAgent(StateDir, original, new LlmSessionRegistry());

        using (new FileStream(LegacyFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<ComposerMigrationException>(() => agent.ConnectAsync(Ct));
        }

        Assert.False(agent.IsConnected);
        Assert.False(original.PersistenceAllowed);
        Assert.False(File.Exists(Evidence(original)));
        Assert.False(File.Exists(ComposerUserState.SessionFilePath(StateDir, original)));

        // Retry once the fault clears: the claim now completes.
        await agent.ConnectAsync(Ct);
        Assert.True(agent.SessionLoadedFromDisk);
        Assert.True(original.PersistenceAllowed);
    }

    [Fact]
    public async Task UnwritableDestination_IoFailure_FailsConnectAndRecordsNoEvidence()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var original = await OriginalAsync();
        // A DIRECTORY where the destination file must go: not an existing file, but unwritable.
        Directory.CreateDirectory(ComposerUserState.SessionFilePath(StateDir, original));
        var agent = CreateAgent(StateDir, original, new LlmSessionRegistry());

        await Assert.ThrowsAsync<ComposerMigrationException>(() => agent.ConnectAsync(Ct));

        Assert.False(File.Exists(Evidence(original)));
        Assert.False(original.PersistenceAllowed);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Evidence(original))!, "*.migrating-*"));
    }

    [Fact]
    public async Task CancelledMigration_PropagatesCancellation_AndRecordsNothing()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var original = await OriginalAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ComposerLegacyMigration.EnsureAsync(StateDir, original, cts.Token));

        Assert.False(File.Exists(Evidence(original)));
        Assert.False(original.PersistenceAllowed);
    }

    // ── Legacy runtime unchanged ──

    [Fact]
    public async Task LegacyConstruction_WithCorruptFile_StillStartsFresh_AndMigrationIsNeverInvoked()
    {
        await SeedUsersAsync();
        await File.WriteAllTextAsync(LegacyFile, "{invalid json", Ct);
        var agent = CreateAgent(StateDir, null, new LlmSessionRegistry());

        await agent.ConnectAsync(Ct);

        Assert.True(agent.IsConnected);
        Assert.False(agent.SessionLoadedFromDisk);
        Assert.False(Directory.Exists(Path.Combine(StateDir, "composer")));
    }

    // ── Restart without resurrection ──

    [Fact]
    public async Task RestartAfterReset_DoesNotResurrectLegacyHistory()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var legacyBytes = await File.ReadAllBytesAsync(LegacyFile, Ct);
        var perUser = ComposerUserState.SessionFilePath(StateDir, await OriginalAsync());

        using (var db = CopilotHiveDbContext.CreateInMemory())
        {
            await using var first = CreateComposer(new GoalStore(db, NullLogger<GoalStore>.Instance), StateDir, await OriginalAsync());
            await first.ConnectAsync(Ct);
            Assert.True(first.SessionLoadedFromDisk);
            await first.ResetSessionAsync(Ct);
            Assert.False(File.Exists(perUser));
        }

        // "Restart": a brand-new process re-resolves the identity and builds a new Composer.
        using (var db = CopilotHiveDbContext.CreateInMemory())
        {
            await using var second = CreateComposer(new GoalStore(db, NullLogger<GoalStore>.Instance), StateDir, await OriginalAsync());
            await second.ConnectAsync(Ct);

            Assert.False(second.SessionLoadedFromDisk);
            Assert.Empty(LiveSession(second).MessageHistory);
            Assert.False(File.Exists(perUser));
        }

        Assert.Equal(legacyBytes, await File.ReadAllBytesAsync(LegacyFile, Ct));
    }

    [Fact]
    public async Task RestartAfterOverflowRecovery_DoesNotResurrectLegacyHistory()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var original = await OriginalAsync();
        var perUser = ComposerUserState.SessionFilePath(StateDir, original);

        using (var db = CopilotHiveDbContext.CreateInMemory())
        {
            await using var first = CreateComposer(new GoalStore(db, NullLogger<GoalStore>.Instance), StateDir, original, _ => OverflowClient());
            await first.ConnectAsync(Ct);
            Assert.True(File.Exists(perUser));

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.OnStreamingUpdate += () => { if (!first.IsStreaming) done.TrySetResult(); };
            first.SendMessage("hello");
            await done.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

            Assert.False(File.Exists(perUser)); // overflow recovery deleted the per-user file
        }

        var restarted = await OriginalAsync();
        var agent = CreateAgent(StateDir, restarted, new LlmSessionRegistry());
        await agent.ConnectAsync(Ct);

        Assert.False(agent.SessionLoadedFromDisk);
        Assert.Empty(agent.Session.MessageHistory);
        Assert.False(File.Exists(perUser));
        Assert.True(File.Exists(LegacyFile));
    }

    private static AgentSession LiveSession(Composer composer) =>
        ((ComposerAgentService)typeof(Composer)
            .GetField("_agentService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(composer)!).Session;

    private static IChatClient OverflowClient()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("model_max_prompt_tokens_exceeded"));
        mock.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("model_max_prompt_tokens_exceeded"));
        return mock.Object;
    }
}

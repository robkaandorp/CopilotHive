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
/// Round-1 tests for the shared per-user Composer identity/path helper: validation against the real
/// persisted user directory, disjoint per-user session/registry/attachment identities, mismatch
/// rejection at Composer construction, and byte-identical legacy/default behaviour.
/// </summary>
public sealed class ComposerUserStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-user-state-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly FileDbFactory _factory;
    private readonly UserService _users;

    public ComposerUserStateTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(StateDir); // the agent's WorkDirectory must exist
        _dbPath = Path.Combine(_dir, "users.db");
        _factory = new FileDbFactory(_dbPath);
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

    /// <summary>Bootstraps the original admin, then activates one invited account; returns both validated identities.</summary>
    private async Task<(ComposerUserState First, ComposerUserState Second)> TwoUsersAsync()
    {
        var original = await _users.CreateOrUpdateUserAsync(
            "100", "first", null, null, null, "tok", "ref", "2030-01-01T00:00:00.0000000Z", Ct);
        await _users.InviteUserAsync("second", UserRole.Admin, Ct);
        var invited = await _users.ActivateInvitationAsync("second", "200", null, null, null, Ct);
        return (await ComposerUserState.ResolveAsync(_users, original.Id, Ct),
                await ComposerUserState.ResolveAsync(_users, invited.Id, Ct));
    }

    private static ComposerAgentService CreateAgent(
        string stateDir, ComposerUserState? user, LlmSessionRegistry? registry, Func<string, IChatClient>? factory = null) =>
        new(
            "test-model", 64000, 50, null, null, "system prompt", new List<AITool>(), null, stateDir,
            null, NullLogger<ComposerAgentService>.Instance,
            factory ?? (_ => new Mock<IChatClient>().Object),
            registry, ["test-model"], null, null, false, [], null, user);

    private static Composer CreateComposer(
        GoalStore store, string stateDir, ComposerUserState? user, ComposerAttachmentService? attachments,
        LlmSessionRegistry? registry = null) =>
        new("test-model", NullLogger<Composer>.Instance, store, stateDir: stateDir,
            chatClientFactory: _ => new Mock<IChatClient>().Object,
            sessionRegistry: registry, attachmentService: attachments, userState: user);

    // ── Validation ──

    [Fact]
    public async Task Resolve_ActivatedUser_ProducesCanonicalIdentity()
    {
        var (first, _) = await TwoUsersAsync();

        Assert.Equal(1, first.UserId);
        Assert.Equal(Path.Combine("S", "composer", "users", "1", "session.json"),
            ComposerUserState.SessionFilePath("S", first));
        Assert.Equal(Path.GetFullPath(Path.Combine("S", "composer", "users", "1", "attachments")),
            ComposerUserState.AttachmentsRootPath("S", first));
        Assert.Equal("composer:1", ComposerUserState.RegistryId(first));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task Resolve_NonPositiveId_IsRejected(int id)
    {
        await TwoUsersAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ComposerUserState.ResolveAsync(_users, id, Ct));
    }

    [Fact]
    public async Task Resolve_PositiveIdOfNoActivatedAccount_IsRejected()
    {
        await TwoUsersAsync();

        // 2 exists; 3 and int.MaxValue do not — a positive id is NEVER inferred valid.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ComposerUserState.ResolveAsync(_users, 3, Ct));
        Assert.Contains("No activated account", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ComposerUserState.ResolveAsync(_users, int.MaxValue, Ct));
    }

    [Fact]
    public async Task Resolve_OnEmptyDirectory_RejectsEvenIdOne()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ComposerUserState.ResolveAsync(_users, 1, Ct));
    }

    [Theory]
    [InlineData("first")]
    [InlineData("100")]          // the GitHub id of user 1 as digits is still only a candidate INTERNAL id → no account 100
    [InlineData("../1")]
    [InlineData("..\\1")]
    [InlineData("1/../../x")]
    [InlineData("/etc/passwd")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1.0")]
    [InlineData("01x")]
    [InlineData("٣")]            // non-ASCII digit
    [InlineData("99999999999")]  // overflows int
    [InlineData("")]
    [InlineData(null)]
    public async Task Resolve_NonIntegerOrUnknownText_IsRejected(string? raw)
    {
        await TwoUsersAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => ComposerUserState.ResolveAsync(_users, raw, Ct));
    }

    [Theory]
    [InlineData("first")]
    [InlineData("../1")]
    [InlineData("1/../../x")]
    [InlineData("+1")]
    [InlineData("99999999999")]
    public async Task Resolve_NonIntegerText_IsRejectedAsArgumentErrorBeforeAnyLookup(string raw)
    {
        await TwoUsersAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => ComposerUserState.ResolveAsync(_users, raw, Ct));
    }

    [Fact]
    public async Task Resolve_PlainDigitText_ResolvesThroughTheSameValidation()
    {
        var (_, second) = await TwoUsersAsync();

        var viaText = await ComposerUserState.ResolveAsync(_users, "2", Ct);

        Assert.Equal(second.UserId, viaText.UserId);
    }

    [Fact]
    public async Task Paths_NeverEscapeTheStateRoot()
    {
        var (first, second) = await TwoUsersAsync();
        var root = Path.GetFullPath(StateDir) + Path.DirectorySeparatorChar;

        foreach (var user in new[] { first, second })
        {
            Assert.StartsWith(root, Path.GetFullPath(ComposerUserState.SessionFilePath(StateDir, user)));
            Assert.StartsWith(root, ComposerUserState.AttachmentsRootPath(StateDir, user));
        }
    }

    // ── Disjointness ──

    [Fact]
    public async Task TwoIdentities_HaveDisjointSessionPathsRegistryKeysAndAttachmentRoots()
    {
        var (first, second) = await TwoUsersAsync();

        Assert.NotEqual(ComposerUserState.SessionFilePath(StateDir, first), ComposerUserState.SessionFilePath(StateDir, second));
        Assert.NotEqual(ComposerUserState.RegistryId(first), ComposerUserState.RegistryId(second));
        Assert.NotEqual(ComposerUserState.AttachmentsRootPath(StateDir, first), ComposerUserState.AttachmentsRootPath(StateDir, second));
        Assert.Equal("composer:1", ComposerUserState.RegistryId(first));
        Assert.Equal("composer:2", ComposerUserState.RegistryId(second));

        var a1 = new ComposerAttachmentService(StateDir, first, NullLogger<ComposerAttachmentService>.Instance);
        var a2 = new ComposerAttachmentService(StateDir, second, NullLogger<ComposerAttachmentService>.Instance);
        Assert.Equal(ComposerUserState.AttachmentsRootPath(StateDir, first), a1.AttachmentsRootPath);
        Assert.Equal(ComposerUserState.AttachmentsRootPath(StateDir, second), a2.AttachmentsRootPath);
        Assert.True(Directory.Exists(a1.AttachmentsRootPath));
        Assert.True(Directory.Exists(a2.AttachmentsRootPath));

        // Records are instance-isolated: a file saved for one user is unknown to the other.
        var saved = await a1.SaveAsync("x.png", new MemoryStream(new byte[4]), Ct);
        Assert.True(saved.Success);
        Assert.True(File.Exists(Path.Combine(a1.AttachmentsRootPath, saved.Attachment!.SavedRelativePath)));
        Assert.False(a2.Remove(saved.Attachment).Success);
    }

    [Fact]
    public async Task IdentityBoundAgents_SaveAndLoadSessionsAtDisjointPerUserPaths()
    {
        var (first, second) = await TwoUsersAsync();
        var registry = new LlmSessionRegistry();
        var a = CreateAgent(StateDir, first, registry);
        var b = CreateAgent(StateDir, second, registry);
        await a.ConnectAsync(Ct);
        await b.ConnectAsync(Ct);

        a.Session.MessageHistory.Add(new ChatMessage(ChatRole.User, "from-first"));
        await a.Session.SaveAsync(ComposerUserState.SessionFilePath(StateDir, first), Ct);

        var reloaded = CreateAgent(StateDir, first, null);
        await reloaded.ConnectAsync(Ct);
        Assert.True(reloaded.SessionLoadedFromDisk);
        Assert.Equal(["from-first"], reloaded.Session.MessageHistory.Select(m => m.Text));

        var other = CreateAgent(StateDir, second, null);
        await other.ConnectAsync(Ct);
        Assert.False(other.SessionLoadedFromDisk);
        Assert.False(File.Exists(Path.Combine(StateDir, "composer-session.json")));
    }

    // ── Registry ──

    [Fact]
    public async Task IdentityBoundAgents_RegisterDistinctKeys_AndOneUsersUnregisterLeavesTheOther()
    {
        var (first, second) = await TwoUsersAsync();
        var registry = new LlmSessionRegistry();
        var failFirst = false;
        var a = CreateAgent(StateDir, first, registry, _ => failFirst ? throw new InvalidOperationException("boom") : new Mock<IChatClient>().Object);
        var b = CreateAgent(StateDir, second, registry);

        await a.ConnectAsync(Ct);
        await b.ConnectAsync(Ct);
        Assert.Equal(["composer:1", "composer:2"], registry.GetAll().Select(s => s.SessionId).Order().ToArray());

        // A's failing reconnect runs the catch-path Unregister(RegistryId) for A only.
        failFirst = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.ConnectAsync(Ct));

        Assert.Equal(["composer:2"], registry.GetAll().Select(s => s.SessionId).ToArray());
    }

    [Fact]
    public async Task IdentityBoundAgent_SwitchModelRegistersUnderPerUserKey()
    {
        var (first, _) = await TwoUsersAsync();
        var registry = new LlmSessionRegistry();
        var hive = new CopilotHive.Configuration.HiveConfigFile
        {
            Models = new CopilotHive.Configuration.ModelsConfig
            {
                AvailableModels = [new() { Name = "test-model" }, new() { Name = "model-b" }]
            }
        };
        var agent = new ComposerAgentService(
            "test-model", 64000, 50, null, hive, "p", new List<AITool>(), null, StateDir, null,
            NullLogger<ComposerAgentService>.Instance, _ => new Mock<IChatClient>().Object, registry,
            ["test-model", "model-b"], null, null, false, [], null, first);
        await agent.ConnectAsync(Ct);
        registry.Unregister("composer:1");

        await agent.SwitchModelAsync("model-b", ReasoningEffort.High, Ct);

        var entry = Assert.Single(registry.GetAll());
        Assert.Equal("composer:1", entry.SessionId);
        Assert.Equal("model-b", entry.Model);
    }

    // ── Composer wiring + mismatch rejection ──

    [Fact]
    public async Task IdentityBoundComposer_RegistersPerUserKey_AndSavesAndResetsAtPerUserSessionPath()
    {
        var (first, _) = await TwoUsersAsync();
        var registry = new LlmSessionRegistry();
        var attachments = new ComposerAttachmentService(StateDir, first, NullLogger<ComposerAttachmentService>.Instance);
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);
        await using var composer = CreateComposer(store, StateDir, first, attachments, registry);
        await composer.ConnectAsync(Ct);

        Assert.Equal(["composer:1"], registry.GetAll().Select(s => s.SessionId).ToArray());

        await composer.SaveSessionAsync(Ct);
        var perUser = ComposerUserState.SessionFilePath(StateDir, first);
        Assert.True(File.Exists(perUser));
        Assert.False(File.Exists(Path.Combine(StateDir, "composer-session.json")));

        await composer.ResetSessionAsync(Ct);
        Assert.False(File.Exists(perUser));
        Assert.Equal(["composer:1"], registry.GetAll().Select(s => s.SessionId).ToArray());
    }

    [Fact]
    public async Task IdentityBoundComposer_WithAttachmentServiceOfAnotherUser_IsRejectedAtConstruction()
    {
        var (first, second) = await TwoUsersAsync();
        var other = new ComposerAttachmentService(StateDir, second, NullLogger<ComposerAttachmentService>.Instance);
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);

        var ex = Assert.Throws<ArgumentException>(() => CreateComposer(store, StateDir, first, other));

        Assert.Contains("does not match", ex.Message);
        Assert.Contains("user 1", ex.Message);
    }

    [Fact]
    public async Task IdentityBoundComposer_WithLegacyAttachmentService_IsRejectedAtConstruction()
    {
        var (first, _) = await TwoUsersAsync();
        var legacy = new ComposerAttachmentService(StateDir, NullLogger<ComposerAttachmentService>.Instance);
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);

        Assert.Throws<ArgumentException>(() => CreateComposer(store, StateDir, first, legacy));
    }

    [Fact]
    public async Task IdentityBoundComposer_WithSameUserServiceOverDifferentStateDir_IsRejectedAtConstruction()
    {
        var (first, _) = await TwoUsersAsync();
        var elsewhere = new ComposerAttachmentService(
            Path.Combine(_dir, "other-state"), first, NullLogger<ComposerAttachmentService>.Instance);
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);

        Assert.Throws<ArgumentException>(() => CreateComposer(store, StateDir, first, elsewhere));
    }

    [Fact]
    public async Task IdentityBoundComposer_WithMatchingAttachmentService_IsAccepted()
    {
        var (first, _) = await TwoUsersAsync();
        var matching = new ComposerAttachmentService(StateDir, first, NullLogger<ComposerAttachmentService>.Instance);
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);

        await using var composer = CreateComposer(store, StateDir, first, matching);

        Assert.NotNull(composer);
    }

    [Fact]
    public async Task LegacyComposer_WithIdentityBoundAttachmentService_StaysValid()
    {
        var (first, _) = await TwoUsersAsync();
        var bound = new ComposerAttachmentService(StateDir, first, NullLogger<ComposerAttachmentService>.Instance);
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);

        await using var composer = CreateComposer(store, StateDir, null, bound);

        Assert.NotNull(composer);
    }

    [Fact]
    public void LegacyComposer_WithLegacyAttachmentService_StaysValid_AndKeepsLegacyRoot()
    {
        var legacy = new ComposerAttachmentService(StateDir, NullLogger<ComposerAttachmentService>.Instance);
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);

        using var _ = new ExitStack();
        var composer = CreateComposer(store, StateDir, null, legacy);

        Assert.Equal(Path.GetFullPath(Path.Combine(StateDir, "composer-attachments")), legacy.AttachmentsRootPath);
        Assert.Null(legacy.UserId);
        Assert.NotNull(composer);
    }

    private sealed class ExitStack : IDisposable { public void Dispose() { } }

    // ── Legacy byte-identical behaviour ──

    [Fact]
    public void LegacyHelper_ResolvesTheHistoricalLiterals()
    {
        Assert.Equal(Path.Combine("S", "composer-session.json"), ComposerUserState.SessionFilePath("S", null));
        Assert.Equal(Path.GetFullPath(Path.Combine("S", "composer-attachments")), ComposerUserState.AttachmentsRootPath("S", null));
        Assert.Equal("composer", ComposerUserState.RegistryId(null));
    }

    [Fact]
    public async Task LegacyComposer_SavesResetsAndRegistersAtLegacyLocations()
    {
        var registry = new LlmSessionRegistry();
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);
        await using var composer = CreateComposer(store, StateDir, null, null, registry);
        await composer.ConnectAsync(Ct);

        Assert.Equal(["composer"], registry.GetAll().Select(s => s.SessionId).ToArray());

        await composer.SaveSessionAsync(Ct);
        var legacyFile = Path.Combine(StateDir, "composer-session.json");
        Assert.True(File.Exists(legacyFile));
        Assert.False(Directory.Exists(Path.Combine(StateDir, "composer")));

        await composer.ResetSessionAsync(Ct);
        Assert.False(File.Exists(legacyFile));
    }

    [Fact]
    public async Task LegacyAgent_ConnectAsync_CorruptSessionFile_StartsFresh_ForLegacyAndPerUser()
    {
        var (first, _) = await TwoUsersAsync();

        foreach (var user in new ComposerUserState?[] { null, first })
        {
            var file = ComposerUserState.SessionFilePath(StateDir, user);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, "{invalid json", Ct);
            var agent = CreateAgent(StateDir, user, new LlmSessionRegistry());

            await agent.ConnectAsync(Ct);

            Assert.True(agent.IsConnected);
            Assert.False(agent.SessionLoadedFromDisk);
            Assert.True(agent.Session.MessageHistory.Count <= 1);
        }
    }

    [Fact]
    public async Task Agent_ConnectAsync_IoErrorOnSessionRead_KeepsCurrentSession_ForLegacyAndPerUser()
    {
        var (first, _) = await TwoUsersAsync();

        foreach (var user in new ComposerUserState?[] { null, first })
        {
            var file = ComposerUserState.SessionFilePath(StateDir, user);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var persisted = AgentSession.Create("composer");
            persisted.MessageHistory.Add(new ChatMessage(ChatRole.User, "persisted"));
            await persisted.SaveAsync(file, Ct);
            var agent = CreateAgent(StateDir, user, new LlmSessionRegistry());
            var current = agent.Session;

            // An exclusive lock makes the read fail with an IOException (honoured on Unix by .NET).
            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await agent.ConnectAsync(Ct);
            }

            Assert.True(agent.IsConnected);
            Assert.False(agent.SessionLoadedFromDisk);
            Assert.Same(current, agent.Session);
            Assert.Empty(agent.Session.MessageHistory);
        }
    }
}

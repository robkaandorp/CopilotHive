using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;
using SharpCoder;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// INDEPENDENT verification of the two round-2 production fixes to the original-admin migration
/// seam, driven through the prerequisite goal's REAL original-admin contract (a real
/// <see cref="UserService"/> over a file-backed SQLite database).
/// <para>
/// <b>FINDING 1</b> — the seam used to validate the legacy source only by
/// <c>AgentSession.LoadAsync</c> SUCCEEDING, so a payload like <c>{"messages":[null]}</c> parsed,
/// was copied to the per-user destination, had its evidence recorded, and opened persistence —
/// and only then blew up downstream with a NullReferenceException. The fix must reject a
/// STRUCTURALLY UNUSABLE session before any copy, evidence or persistence-open, treating it
/// exactly like corruption.
/// </para>
/// <para>
/// <b>FINDING 2</b> — <c>File.Exists(source) == false</c> was treated as proven absence, so a
/// DIRECTORY at the legacy source path settled as "no legacy source" and permitted saving. The fix
/// must distinguish genuine absence from a wrong-type entry (fail closed) and propagate genuine
/// filesystem faults instead of settling.
/// </para>
/// <para>
/// This file adds what the landed regression tests did NOT prove:
/// <list type="bullet">
///   <item><b>(1) The reviewer's exact probe, reproduced independently</b> — the same payload, in
///   BOTH registry configurations, through the Composer AND through the agent service.</item>
///   <item><b>(2) The sibling-shape audit</b> — every additional shape that deserializes but breaks
///   a consumer, and the proof that every shape that DOES pass validation is usable downstream
///   (connect + re-save).</item>
///   <item><b>(3) The valid-empty control</b> — <c>{}</c> and the other benign shapes stay
///   accepted.</item>
///   <item><b>(4) The source-path type matrix</b> — file, directory, absent, and an injected
///   filesystem fault, each with its own outcome.</item>
/// </list>
/// </para>
/// <para>
/// <b>Determinism.</b> No wall-clock assertion: every effect is observed through the file system.
/// The user directory is always the real one, never a stub. Every probe gets its OWN state
/// directory, so no earlier claim's evidence can short-circuit a later probe.
/// </para>
/// </summary>
public sealed class ComposerLegacyMigrationHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-migration-hardening-{Guid.NewGuid():N}");
    private readonly FileDbFactory _factory;
    private readonly UserService _users;

    public ComposerLegacyMigrationHardeningTests()
    {
        Directory.CreateDirectory(_dir);
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

    /// <summary>The reviewer's exact probe payload.</summary>
    private const string ReviewerPayload = """{"messages":[null]}""";

    /// <summary>
    /// Bootstraps the ONE original admin (GitHub id "100") through the real sign-in seam and
    /// activates a second ADMIN (GitHub id "200") so the "another activated admin" case is real.
    /// </summary>
    private async Task<(int OriginalId, int SecondId)> SeedUsersAsync()
    {
        var original = await _users.CreateOrUpdateUserAsync(
            "100", "first", null, null, null, "tok", "ref", "2030-01-01T00:00:00.0000000Z", Ct);
        await _users.InviteUserAsync("second", UserRole.Admin, Ct);
        var second = await _users.ActivateInvitationAsync("second", "200", null, null, null, Ct);
        return (original.Id, second.Id);
    }

    /// <summary>
    /// A FRESH state directory with the given legacy payload, plus the original-admin identity
    /// resolved against the real directory. Every probe gets its own directory so no earlier
    /// claim's evidence can short-circuit it.
    /// </summary>
    private async Task<(string StateDir, string LegacyFile, ComposerUserState User)> FreshProbeAsync(
        int originalId, string? legacyPayload)
    {
        var stateDir = Path.Combine(_dir, "state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDir);
        var legacyFile = Path.Combine(stateDir, "composer-session.json");
        if (legacyPayload is not null)
            await File.WriteAllTextAsync(legacyFile, legacyPayload, Ct);

        var user = await ComposerUserState.ResolveOriginalAdminAsync(_users, originalId, Ct);
        return (stateDir, legacyFile, user);
    }

    private static string PerUserFile(string stateDir, ComposerUserState user) =>
        ComposerUserState.SessionFilePath(stateDir, user);

    private static string Evidence(string stateDir, ComposerUserState user) =>
        ComposerUserState.LegacyMigrationEvidencePath(stateDir, user);

    private ComposerAgentService CreateAgent(
        string stateDir, ComposerUserState? user, LlmSessionRegistry? registry) =>
        new("test-model", 64000, 50, null, null, "system prompt", new List<AITool>(), null, stateDir,
            null, NullLogger<ComposerAgentService>.Instance, _ => new Mock<IChatClient>().Object,
            registry, ["test-model"], null, null, false, [], null, user,
            user is null ? null : ToolAuthorizationGateFor(user));

    private Composer CreateComposer(
        GoalStore store, string stateDir, ComposerUserState? user, LlmSessionRegistry? registry) =>
        new("test-model", NullLogger<Composer>.Instance, store, stateDir: stateDir,
            serviceProvider: user is null ? null : SecurityPrimitives,
            chatClientFactory: _ => new Mock<IChatClient>().Object,
            sessionRegistry: registry, userState: user,
            principal: user is null ? null : PrincipalFor(user));

    /// <summary>
    /// The RESOLVER-PRODUCED acting principal of <paramref name="user"/>'s account, read through the
    /// REAL production <see cref="HivePrincipalResolver"/> over this fixture's user directory. An
    /// identity-bound Composer requires one for the SAME account (there is no legacy-admin fallback),
    /// so every identity-bound construction here supplies exactly this.
    /// </summary>
    private ActingUserPrincipal PrincipalFor(ComposerUserState user)
    {
        var resolver = new HivePrincipalResolver(
            _users, HiveAuthMode.AuthenticationEnabled, NullLogger<HivePrincipalResolver>.Instance);
        var resolution = resolver.ResolveByUserIdAsync(user.UserId, Ct).GetAwaiter().GetResult();
        Assert.True(resolution.IsResolved, $"The fixture account {user.UserId} did not resolve to a principal.");
        return resolution.Principal!;
    }

    /// <summary>
    /// A service provider holding the COMPLETE security-primitive set over this fixture's user directory. An
    /// identity-bound Composer REQUIRES a tool-authorization gate (a partial registration fails closed), so
    /// every identity-bound construction here is handed one.
    /// </summary>
    private IServiceProvider SecurityPrimitives => _securityPrimitives ??= BuildSecurityPrimitives();

    private ServiceProvider? _securityPrimitives;

    private ServiceProvider BuildSecurityPrimitives()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(_users);
        services.AddSingleton(HiveAuthMode.AuthenticationEnabled);
        services.AddSingleton<HivePolicy>();
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(),
            sp.GetRequiredService<HiveAuthMode>(),
            NullLogger<HivePrincipalResolver>.Instance));
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The per-identity authorization gate an identity-bound <see cref="ComposerAgentService"/> now requires,
    /// bound through the SAME central helper the Composer uses. It reads the fixture's real user directory.
    /// </summary>
    private ComposerToolAuthorizationGate ToolAuthorizationGateFor(ComposerUserState user) =>
        ComposerToolAuthorization.FromServiceProvider(SecurityPrimitives)!.Bind(user.UserId);


    /// <summary>
    /// Asserts the FULL fail-closed contract: no destination, no evidence, persistence blocked,
    /// and the legacy source byte-unchanged.
    /// </summary>
    private async Task AssertFailedClosedAsync(
        string stateDir, string legacyFile, ComposerUserState user, string payload)
    {
        Assert.False(user.PersistenceAllowed, "persistence must stay blocked");
        Assert.False(File.Exists(PerUserFile(stateDir, user)), "no destination copy may exist");
        Assert.False(File.Exists(Evidence(stateDir, user)), "no evidence may exist");
        Assert.Equal(payload, await File.ReadAllTextAsync(legacyFile, Ct)); // source byte-unchanged
    }

    // ── (1) The reviewer's exact probe, reproduced independently ─────────────

    /// <summary>
    /// FINDING 1 — THE REVIEWER'S EXACT PROBE FAILS CLOSED WITH A REGISTRY: the connect fails as a
    /// MIGRATION failure (never a NullReferenceException), no registry entry is published, no
    /// destination or evidence exists, the save is blocked, and the legacy source is unchanged.
    /// </summary>
    [Fact]
    public async Task Finding1_ReviewerProbe_ConnectWithRegistry_FailsClosedAsMigrationFailure()
    {
        var (originalId, _) = await SeedUsersAsync();
        var (stateDir, legacyFile, user) = await FreshProbeAsync(originalId, ReviewerPayload);
        var registry = new LlmSessionRegistry();
        using var db = CopilotHiveDbContext.CreateInMemory();
        await using var composer = CreateComposer(
            new GoalStore(db, NullLogger<GoalStore>.Instance), stateDir, user, registry);

        var ex = await Record.ExceptionAsync(() => composer.ConnectAsync(Ct));

        Assert.NotNull(ex);
        Assert.IsType<ComposerMigrationException>(ex);
        Assert.IsNotType<NullReferenceException>(ex);
        Assert.False(composer.IsConnected);
        Assert.Empty(registry.GetAll());
        await Assert.ThrowsAsync<ComposerMigrationException>(() => composer.SaveSessionAsync(Ct));
        await AssertFailedClosedAsync(stateDir, legacyFile, user, ReviewerPayload);
    }

    /// <summary>
    /// FINDING 1 — THE REVIEWER'S EXACT PROBE FAILS CLOSED WITHOUT A REGISTRY: the same contract
    /// holds when no session registry is supplied at all, so the failure cannot be an artefact of
    /// the registry path.
    /// </summary>
    [Fact]
    public async Task Finding1_ReviewerProbe_ConnectWithoutRegistry_FailsClosedAsMigrationFailure()
    {
        var (originalId, _) = await SeedUsersAsync();
        var (stateDir, legacyFile, user) = await FreshProbeAsync(originalId, ReviewerPayload);
        using var db = CopilotHiveDbContext.CreateInMemory();
        await using var composer = CreateComposer(
            new GoalStore(db, NullLogger<GoalStore>.Instance), stateDir, user, registry: null);

        var ex = await Record.ExceptionAsync(() => composer.ConnectAsync(Ct));

        Assert.NotNull(ex);
        Assert.IsType<ComposerMigrationException>(ex);
        Assert.IsNotType<NullReferenceException>(ex);
        Assert.False(composer.IsConnected);
        await Assert.ThrowsAsync<ComposerMigrationException>(() => composer.SaveSessionAsync(Ct));
        await AssertFailedClosedAsync(stateDir, legacyFile, user, ReviewerPayload);
    }

    /// <summary>
    /// FINDING 1 — THE REVIEWER'S EXACT PROBE FAILS CLOSED AT THE AGENT-SERVICE LEVEL, in BOTH
    /// registry configurations: the seam is the same code path, so the raw service proves the fix
    /// is not a Composer-only artefact.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Finding1_ReviewerProbe_AgentConnect_FailsClosedInBothRegistryConfigurations(bool withRegistry)
    {
        var (originalId, _) = await SeedUsersAsync();
        var (stateDir, legacyFile, user) = await FreshProbeAsync(originalId, ReviewerPayload);
        var registry = withRegistry ? new LlmSessionRegistry() : null;
        var agent = CreateAgent(stateDir, user, registry);

        var ex = await Record.ExceptionAsync(() => agent.ConnectAsync(Ct));

        Assert.NotNull(ex);
        Assert.IsType<ComposerMigrationException>(ex);
        Assert.IsNotType<NullReferenceException>(ex);
        Assert.False(agent.IsConnected);
        if (registry is not null)
            Assert.Empty(registry.GetAll());
        await AssertFailedClosedAsync(stateDir, legacyFile, user, ReviewerPayload);

        await agent.DisposeAsync();
    }

    /// <summary>
    /// FINDING 1 — THE DIRECT SEAM FAILS CLOSED FOR THE REVIEWER'S PROBE: the same refusal, with
    /// no Composer involved at all, and the seam reports no outcome (it throws).
    /// </summary>
    [Fact]
    public async Task Finding1_ReviewerProbe_DirectSeam_FailsClosed()
    {
        var (originalId, _) = await SeedUsersAsync();
        var (stateDir, legacyFile, user) = await FreshProbeAsync(originalId, ReviewerPayload);

        await Assert.ThrowsAsync<ComposerMigrationException>(
            () => ComposerLegacyMigration.EnsureAsync(stateDir, user, Ct));

        await AssertFailedClosedAsync(stateDir, legacyFile, user, ReviewerPayload);
    }

    // ── (2) The sibling-shape audit ─────────────────────────────────────────

    /// <summary>
    /// SIBLING SHAPES THAT DESERIALIZE BUT BREAK A CONSUMER: each must fail closed exactly like
    /// the reviewer's payload — no destination, no evidence, persistence blocked, source
    /// unchanged. The set covers a null content entry, a null message AFTER a valid one, an empty
    /// role, a function-call content with a null id or name, a null top-level document, a
    /// non-array messages field, a non-object messages field, an unknown content kind, a null
    /// role, a message with no role at all, a non-array contents field, and a null uri.
    /// </summary>
    public static TheoryData<string> UnusableSiblingShapes => new()
    {
        """{"messages":[{"role":"user","contents":[null]}]}""",
        """{"messages":[{"role":"user","text":"ok"},null]}""",
        """{"messages":[{"role":""}]}""",
        """{"messages":[{"role":"user","contents":[{"$type":"functionCall","callId":null,"name":"n"}]}]}""",
        """{"messages":[{"role":"user","contents":[{"$type":"functionCall","callId":"c","name":null}]}]}""",
        """{"messages":[{"role":"user","contents":[{"$type":"unknownKind"}]}]}""",
        """{"messages":[{"role":"user","contents":[{"$type":"uri","uri":null}]}]}""",
        """{"messages":[{"role":null}]}""",
        """{"messages":[{"role":"user","contents":"notarray"}]}""",
        """{"messages":"notanarray"}""",
        """{"messages":{"a":1}}""",
        """{"messages":[{"role":"user","contents":[{"$type":"data","data":null}]}]}""",
        "null",
    };

    /// <summary>
    /// EACH SIBLING SHAPE FAILS CLOSED AT THE SEAM: the direct seam refuses every one of them,
    /// writes nothing, and leaves persistence blocked.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnusableSiblingShapes))]
    public async Task Finding1_SiblingShape_DirectSeam_FailsClosed(string payload)
    {
        var (originalId, _) = await SeedUsersAsync();
        var (stateDir, legacyFile, user) = await FreshProbeAsync(originalId, payload);

        await Assert.ThrowsAsync<ComposerMigrationException>(
            () => ComposerLegacyMigration.EnsureAsync(stateDir, user, Ct));

        await AssertFailedClosedAsync(stateDir, legacyFile, user, payload);
    }

    /// <summary>
    /// EACH SIBLING SHAPE FAILS CLOSED THROUGH A CONNECT: the same refusal with a registry
    /// supplied, so no shape can slip through the Composer path either.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnusableSiblingShapes))]
    public async Task Finding1_SiblingShape_Connect_FailsClosed(string payload)
    {
        var (originalId, _) = await SeedUsersAsync();
        var (stateDir, legacyFile, user) = await FreshProbeAsync(originalId, payload);
        var registry = new LlmSessionRegistry();
        using var db = CopilotHiveDbContext.CreateInMemory();
        await using var composer = CreateComposer(
            new GoalStore(db, NullLogger<GoalStore>.Instance), stateDir, user, registry);

        var ex = await Record.ExceptionAsync(() => composer.ConnectAsync(Ct));

        Assert.NotNull(ex);
        Assert.IsType<ComposerMigrationException>(ex);
        Assert.Empty(registry.GetAll());
        await Assert.ThrowsAsync<ComposerMigrationException>(() => composer.SaveSessionAsync(Ct));
        await AssertFailedClosedAsync(stateDir, legacyFile, user, payload);
    }

    /// <summary>
    /// EVERY SHAPE THAT PASSES VALIDATION IS USABLE DOWNSTREAM: the benign shapes (an empty
    /// message list, an empty or null text, an empty content list, a null text content, unknown
    /// but harmless fields, a null usage, a function-call content, a message with no role) all
    /// MIGRATE, and a fresh agent can then CONNECT to the migrated per-user file AND RE-SAVE the
    /// session without an exception. This is the closing half of the sibling-shape audit: it
    /// proves the validation is neither too strict (it rejects nothing usable) nor too lax (it
    /// accepts nothing unusable).
    /// </summary>
    [Fact]
    public async Task Finding1_EveryAcceptedShape_IsUsableDownstream_ConnectAndReSave()
    {
        var (originalId, _) = await SeedUsersAsync();

        string[] benign =
        [
            "{}",
            """{"messages":[]}""",
            """{"messages":[{"role":"user","text":""}]}""",
            """{"messages":[{"role":"user","text":null}]}""",
            """{"messages":[{"role":"user","contents":[]}]}""",
            """{"messages":[{"role":"user","contents":[{"$type":"text","text":null}]}]}""",
            """{"messages":[{"role":"user","contents":[{"$type":"text","text":""}]}]}""",
            """{"messages":[{"role":"user","contents":[{"$type":"text"}]}]}""",
            """{"messages":[{"role":"user","text":"ok"}],"extra":{"nested":null}}""",
            """{"unknownField":123,"messages":[{"role":"user","text":"ok"}]}""",
            """{"usage":null,"messages":[{"role":"user","text":"ok"}]}""",
            """{"messages":[{"role":"assistant","contents":[{"$type":"functionCall","callId":"c","name":"n"}]}]}""",
            """{"messages":[{"role":"user","text":"ok"},{"role":"assistant","contents":[{"$type":"text","text":null}]}]}""",
            """{"messages":[{"role":"user","contents":[{"$type":"text","text":"ok"},{"$type":"text","text":null}]}]}""",
            """{"messages":[{}]}""",
            """{"messages":[{"role":"user","text":"ok"}],"usage":{"inputTokens":null}}""",
        ];

        foreach (var payload in benign)
        {
            var (stateDir, _, user) = await FreshProbeAsync(originalId, payload);

            var outcome = await ComposerLegacyMigration.EnsureAsync(stateDir, user, Ct);
            Assert.Equal(ComposerMigrationOutcome.Migrated, outcome);
            Assert.True(user.PersistenceAllowed);
            Assert.True(File.Exists(PerUserFile(stateDir, user)));
            Assert.True(File.Exists(Evidence(stateDir, user)));

            // The migrated file is USABLE: a fresh agent connects to it and can re-save it.
            var agent = CreateAgent(stateDir, user, new LlmSessionRegistry());
            var connectEx = await Record.ExceptionAsync(() => agent.ConnectAsync(Ct));
            Assert.Null(connectEx);
            Assert.True(agent.SessionLoadedFromDisk);

            var roundTrip = Path.Combine(stateDir, "round-trip.json");
            var saveEx = await Record.ExceptionAsync(() => agent.Session.SaveAsync(roundTrip, Ct));
            Assert.Null(saveEx);
            Assert.True(File.Exists(roundTrip));

            await agent.DisposeAsync();
        }
    }

    /// <summary>
    /// THE VALID-EMPTY CONTROL IS STILL ACCEPTED: <c>{}</c> is a legitimate empty session, so it
    /// migrates and opens persistence — the fix must not turn "empty" into "unusable".
    /// </summary>
    [Fact]
    public async Task Finding1_ValidEmptySession_IsStillAcceptedAndOpensPersistence()
    {
        var (originalId, _) = await SeedUsersAsync();
        var (stateDir, _, user) = await FreshProbeAsync(originalId, "{}");

        var outcome = await ComposerLegacyMigration.EnsureAsync(stateDir, user, Ct);

        Assert.Equal(ComposerMigrationOutcome.Migrated, outcome);
        Assert.True(user.PersistenceAllowed);
        Assert.True(File.Exists(PerUserFile(stateDir, user)));
        Assert.True(File.Exists(Evidence(stateDir, user)));
    }

    // ── (4) The source-path type matrix ─────────────────────────────────────

    /// <summary>
    /// FINDING 2 — A DIRECTORY AT THE LEGACY SOURCE PATH FAILS CLOSED: the connect fails as a
    /// migration failure, the save is blocked, no evidence and no per-user session file exist, and
    /// the directory at the source path is left exactly as it was.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Finding2_DirectoryAtSourcePath_Connect_FailsClosed(bool withRegistry)
    {
        var (originalId, _) = await SeedUsersAsync();
        var (stateDir, legacyFile, user) = await FreshProbeAsync(originalId, null);
        Directory.CreateDirectory(legacyFile);
        var registry = withRegistry ? new LlmSessionRegistry() : null;
        using var db = CopilotHiveDbContext.CreateInMemory();
        await using var composer = CreateComposer(
            new GoalStore(db, NullLogger<GoalStore>.Instance), stateDir, user, registry);

        var ex = await Record.ExceptionAsync(() => composer.ConnectAsync(Ct));

        Assert.NotNull(ex);
        Assert.IsType<ComposerMigrationException>(ex);
        Assert.False(composer.IsConnected);
        if (registry is not null)
            Assert.Empty(registry.GetAll());
        await Assert.ThrowsAsync<ComposerMigrationException>(() => composer.SaveSessionAsync(Ct));
        Assert.False(user.PersistenceAllowed);
        Assert.False(File.Exists(Evidence(stateDir, user)));
        Assert.False(File.Exists(PerUserFile(stateDir, user)));
        Assert.True(Directory.Exists(legacyFile));
    }

    /// <summary>
    /// FINDING 2 — A DIRECTORY AT THE LEGACY SOURCE PATH FAILS CLOSED AT THE SEAM: the seam
    /// refuses it (it is not "no legacy source") and writes nothing.
    /// </summary>
    [Fact]
    public async Task Finding2_DirectoryAtSourcePath_DirectSeam_RefusesAndWritesNothing()
    {
        var (originalId, _) = await SeedUsersAsync();
        var (stateDir, legacyFile, user) = await FreshProbeAsync(originalId, null);
        Directory.CreateDirectory(legacyFile);

        var ex = await Assert.ThrowsAsync<ComposerMigrationException>(
            () => ComposerLegacyMigration.EnsureAsync(stateDir, user, Ct));

        Assert.Contains("directory", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(user.PersistenceAllowed);
        Assert.False(File.Exists(Evidence(stateDir, user)));
        Assert.False(File.Exists(PerUserFile(stateDir, user)));
        Assert.True(Directory.Exists(legacyFile));
    }

    /// <summary>
    /// FINDING 2 — A GENUINE FILESYSTEM FAULT PROPAGATES INSTEAD OF SETTLING AS "NO LEGACY
    /// SOURCE": the seam's own probe reports an access-denied fault, and the seam must surface it
    /// (as a migration failure carrying the original fault) rather than pretending the source is
    /// absent and opening persistence.
    /// </summary>
    [Fact]
    public async Task Finding2_ProbeFault_PropagatesInsteadOfSettlingAsNoLegacySource()
    {
        var (originalId, _) = await SeedUsersAsync();
        var (stateDir, _, user) = await FreshProbeAsync(originalId, null);
        var fault = new UnauthorizedAccessException("probe denied");

        var ex = await Assert.ThrowsAsync<ComposerMigrationException>(
            () => ComposerLegacyMigration.EnsureAsync(stateDir, user, Ct, _ => throw fault));

        Assert.Same(fault, ex.InnerException);
        Assert.False(user.PersistenceAllowed);
        Assert.False(File.Exists(Evidence(stateDir, user)));
        Assert.False(File.Exists(PerUserFile(stateDir, user)));
    }

    /// <summary>
    /// FINDING 2 — A "NOT FOUND" PROBE IS GENUINE ABSENCE: the seam settles as
    /// <see cref="ComposerMigrationOutcome.NoLegacySource"/>, opens persistence, and writes NO
    /// marker — the genuinely-absent case keeps its successful fresh-session behaviour.
    /// </summary>
    [Fact]
    public async Task Finding2_NotFoundProbe_IsGenuineAbsence_AndKeepsTheFreshSessionSuccess()
    {
        var (originalId, _) = await SeedUsersAsync();
        var (stateDir, _, user) = await FreshProbeAsync(originalId, null);

        var outcome = await ComposerLegacyMigration.EnsureAsync(
            stateDir, user, Ct, p => throw new DirectoryNotFoundException(p));

        Assert.Equal(ComposerMigrationOutcome.NoLegacySource, outcome);
        Assert.True(user.PersistenceAllowed);
        Assert.False(File.Exists(Evidence(stateDir, user)));
    }

    /// <summary>
    /// FINDING 2 — A GENUINELY ABSENT SOURCE STILL CONNECTS FRESH AND SAVES: the successful
    /// control for the absent case, driven through a real connect — no marker, and the per-user
    /// session file IS written.
    /// </summary>
    [Fact]
    public async Task Finding2_GenuinelyAbsentSource_ConnectsFreshAndSavesWithoutAMarker()
    {
        var (originalId, _) = await SeedUsersAsync();
        var (stateDir, _, user) = await FreshProbeAsync(originalId, null);
        using var db = CopilotHiveDbContext.CreateInMemory();
        await using var composer = CreateComposer(
            new GoalStore(db, NullLogger<GoalStore>.Instance), stateDir, user, registry: null);

        await composer.ConnectAsync(Ct);
        await composer.SaveSessionAsync(Ct);

        Assert.True(composer.IsConnected);
        Assert.False(File.Exists(Evidence(stateDir, user)));
        Assert.True(File.Exists(PerUserFile(stateDir, user)));
    }

    /// <summary>
    /// FINDING 2 — A FILE AT THE LEGACY SOURCE PATH IS STILL MIGRATED: the wrong-type rejection
    /// must not reject the NORMAL case. A real session file (written by the production session
    /// format) migrates, keeps its content, and records its evidence.
    /// </summary>
    [Fact]
    public async Task Finding2_ARealSessionFile_IsStillMigrated()
    {
        var (originalId, _) = await SeedUsersAsync();
        var (stateDir, legacyFile, user) = await FreshProbeAsync(originalId, null);
        var session = AgentSession.Create("composer");
        session.MessageHistory.Add(new ChatMessage(ChatRole.User, "legacy-history"));
        await session.SaveAsync(legacyFile, Ct);
        var legacyBytes = await File.ReadAllBytesAsync(legacyFile, Ct);

        var outcome = await ComposerLegacyMigration.EnsureAsync(stateDir, user, Ct);

        Assert.Equal(ComposerMigrationOutcome.Migrated, outcome);
        Assert.True(user.PersistenceAllowed);
        Assert.Equal(legacyBytes, await File.ReadAllBytesAsync(PerUserFile(stateDir, user), Ct));
        Assert.Equal(legacyBytes, await File.ReadAllBytesAsync(legacyFile, Ct));
        Assert.True(File.Exists(Evidence(stateDir, user)));
    }
}

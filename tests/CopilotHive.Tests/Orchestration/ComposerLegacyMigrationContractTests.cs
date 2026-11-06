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
/// INDEPENDENT verification of the original-admin migration seam and its durable claim evidence
/// (<see cref="ComposerLegacyMigration"/>), driven through the prerequisite goal's REAL
/// original-admin contract: a real <see cref="UserService"/> over a file-backed SQLite database
/// whose lowest-id activated account — bootstrapped through
/// <c>CreateOrUpdateUserAsync</c> — is the only identity that may claim legacy history.
/// <para>
/// This file deliberately covers the parts of the migration contract the landed tests did NOT
/// prove:
/// <list type="bullet">
///   <item><b>(1) The seam's own outcome matrix</b> — every <see cref="ComposerMigrationOutcome"/>
///   is reachable and reported, and the outcome is the same whether the seam is driven directly or
///   through a real connect.</item>
///   <item><b>(2) The durable evidence</b> — its content records the CLAIMED user and the
///   sha256 of the claimed source, so the evidence is verifiable and not a mere marker.</item>
///   <item><b>(3) The claim is stable across a fresh identity</b> — a restarted process that
///   re-resolves the original admin finds the evidence and never re-claims.</item>
///   <item><b>(4) Production stays legacy</b> — nothing in production wires the seam, and the
///   legacy runtime's recovery paths are untouched.</item>
///   <item><b>(5) The migration-evidence file itself is part of the per-user tree</b> — it lives
///   under the canonical per-user root, never outside it.</item>
/// </list>
/// </para>
/// <para>
/// <b>Determinism.</b> No wall-clock assertion is used: every file effect is observed through the
/// file system, and the user directory is the real one (a file-backed SQLite context), never a
/// stub.
/// </para>
/// </summary>
public sealed class ComposerLegacyMigrationContractTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-migration-contract-{Guid.NewGuid():N}");
    private readonly FileDbFactory _factory;
    private readonly UserService _users;

    public ComposerLegacyMigrationContractTests()
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

    /// <summary>
    /// Bootstraps the original admin (GitHub id "100") and activates a second ADMIN (GitHub id
    /// "200", internal id &gt; the original's).
    /// </summary>
    private async Task SeedUsersAsync()
    {
        var original = await _users.CreateOrUpdateUserAsync(
            "100", "first", null, null, null, "tok", "ref", "2030-01-01T00:00:00.0000000Z", Ct);
        await _users.InviteUserAsync("second", UserRole.Admin, Ct);
        var second = await _users.ActivateInvitationAsync("second", "200", null, null, null, Ct);
        _originalId = original.Id;
        _secondId = second.Id;
    }

    private Task<ComposerUserState> OriginalAsync() =>
        ComposerUserState.ResolveOriginalAdminAsync(_users, _originalId, Ct);

    private string PerUserFile(ComposerUserState user) =>
        ComposerUserState.SessionFilePath(StateDir, user);

    private string Evidence(ComposerUserState user) =>
        ComposerUserState.LegacyMigrationEvidencePath(StateDir, user);

    private async Task WriteLegacySessionAsync(string text)
    {
        var s = AgentSession.Create("composer");
        s.MessageHistory.Add(new ChatMessage(ChatRole.User, text));
        await s.SaveAsync(LegacyFile, Ct);
    }

    private static ComposerAgentService CreateAgent(
        string stateDir, ComposerUserState? user, LlmSessionRegistry? registry) =>
        new("test-model", 64000, 50, null, null, "system prompt", new List<AITool>(), null, stateDir,
            null, NullLogger<ComposerAgentService>.Instance, _ => new Mock<IChatClient>().Object,
            registry, ["test-model"], null, null, false, [], null, user);

    private static Composer CreateComposer(
        GoalStore store, string stateDir, ComposerUserState? user,
        Func<string, IChatClient>? factory = null, LlmSessionRegistry? registry = null) =>
        new("test-model", NullLogger<Composer>.Instance, store, stateDir: stateDir,
            chatClientFactory: factory ?? (_ => new Mock<IChatClient>().Object),
            sessionRegistry: registry, userState: user);

    /// <summary>
    /// Reads the Composer's live agent session through the private agent-service field.
    /// </summary>
    private static AgentSession LiveSession(Composer composer) =>
        ((ComposerAgentService)typeof(Composer)
            .GetField("_agentService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(composer)!).Session;

    /// <summary>
    /// A chat client that yields NOTHING and then fails with the provider's context-overflow
    /// text, so the overflow-recovery delete is the only completion signal.
    /// </summary>
    private static IChatClient OverflowClient()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("model_max_prompt_tokens_exceeded"));
        mock.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("model_max_prompt_tokens_exceeded"));
        return mock.Object;
    }

    // ── (1) The outcome matrix ──────────────────────────────────────────────

    /// <summary>
    /// EVERY OUTCOME IS REACHABLE AND REPORTED: an ineligible identity reports
    /// <see cref="ComposerMigrationOutcome.NotEligible"/> without touching anything; an eligible
    /// identity with no legacy source reports
    /// <see cref="ComposerMigrationOutcome.NoLegacySource"/> and writes NO evidence; a successful
    /// claim reports <see cref="ComposerMigrationOutcome.Migrated"/>; a second call reports
    /// <see cref="ComposerMigrationOutcome.AlreadyClaimed"/>; and a byte-identical destination left
    /// by an interrupted claim reports
    /// <see cref="ComposerMigrationOutcome.CompletedExistingDestination"/>.
    /// </summary>
    [Fact]
    public async Task EveryMigrationOutcome_IsReachableAndReported()
    {
        await SeedUsersAsync();
        var original = await OriginalAsync();
        var second = await ComposerUserState.ResolveAsync(_users, _secondId, Ct);

        // NotEligible: an ordinary activated identity, with a legacy source PRESENT, still never
        // touches it.
        await WriteLegacySessionAsync("legacy-history");
        var legacyBytesBefore = await File.ReadAllBytesAsync(LegacyFile, Ct);
        Assert.Equal(ComposerMigrationOutcome.NotEligible,
            await ComposerLegacyMigration.EnsureAsync(StateDir, second, Ct));
        Assert.Equal(legacyBytesBefore, await File.ReadAllBytesAsync(LegacyFile, Ct));
        Assert.False(File.Exists(PerUserFile(second)));
        Assert.False(File.Exists(Evidence(second)));

        // NoLegacySource: no legacy file at all.
        File.Delete(LegacyFile);
        Assert.Equal(ComposerMigrationOutcome.NoLegacySource,
            await ComposerLegacyMigration.EnsureAsync(StateDir, original, Ct));
        Assert.False(File.Exists(Evidence(original)));

        // Migrated.
        await WriteLegacySessionAsync("legacy-history");
        Assert.Equal(ComposerMigrationOutcome.Migrated,
            await ComposerLegacyMigration.EnsureAsync(StateDir, original, Ct));
        Assert.True(File.Exists(Evidence(original)));

        // AlreadyClaimed: the evidence short-circuits the second call.
        Assert.Equal(ComposerMigrationOutcome.AlreadyClaimed,
            await ComposerLegacyMigration.EnsureAsync(StateDir, original, Ct));

        // CompletedExistingDestination: a byte-identical destination with NO evidence.
        File.Delete(Evidence(original));
        Assert.Equal(ComposerMigrationOutcome.CompletedExistingDestination,
            await ComposerLegacyMigration.EnsureAsync(StateDir, original, Ct));
        Assert.True(File.Exists(Evidence(original)));
    }

    /// <summary>
    /// THE SEAM'S OUTCOME IS THE SAME WHETHER IT IS DRIVEN DIRECTLY OR THROUGH A REAL CONNECT:
    /// the agent service's connect path calls the seam, so a connect that finds a legacy source
    /// migrates it and a connect that finds none starts fresh — both leaving the SAME observable
    /// state the direct seam calls produce.
    /// </summary>
    [Fact]
    public async Task ConnectDrivenMigration_ProducesTheSameObservableStateAsTheDirectSeam()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var original = await OriginalAsync();

        var agent = CreateAgent(StateDir, original, new LlmSessionRegistry());
        await agent.ConnectAsync(Ct);

        // The connect migrated (the file exists at the per-user path) and recorded the evidence.
        Assert.True(File.Exists(PerUserFile(original)));
        Assert.True(File.Exists(Evidence(original)));
        Assert.True(agent.SessionLoadedFromDisk);
        Assert.Equal(["legacy-history"], agent.Session.MessageHistory.Select(m => m.Text).ToArray());
    }

    // ── (2) The durable evidence ────────────────────────────────────────────

    /// <summary>
    /// THE EVIDENCE RECORDS THE CLAIMED USER AND THE SHA256 OF THE CLAIMED SOURCE: it is a
    /// verifiable record, not a mere "done" marker, so a later operator can tell WHICH history was
    /// claimed and prove the legacy file has not changed underneath it.
    /// </summary>
    [Fact]
    public async Task MigrationEvidence_RecordsTheClaimedUserAndTheSourceHash()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var original = await OriginalAsync();

        await ComposerLegacyMigration.EnsureAsync(StateDir, original, Ct);

        var evidencePath = Evidence(original);
        Assert.True(File.Exists(evidencePath));

        using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(evidencePath, Ct));
        var root = json.RootElement;

        Assert.Equal(_originalId, root.GetProperty("userId").GetInt32());
        Assert.Equal("composer-session.json", root.GetProperty("source").GetString());

        var legacyBytes = await File.ReadAllBytesAsync(LegacyFile, Ct);
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(legacyBytes));
        Assert.Equal(expectedHash, root.GetProperty("sha256").GetString());
        Assert.Equal(legacyBytes.Length, root.GetProperty("length").GetInt32());
    }

    /// <summary>
    /// THE EVIDENCE LIVES INSIDE THE CANONICAL PER-USER TREE: it is a sibling of that user's
    /// <c>session.json</c> under <c>{stateDir}/composer/users/N</c>, and never anywhere else in the
    /// state root.
    /// </summary>
    [Fact]
    public async Task MigrationEvidence_LivesInsideTheCanonicalPerUserTree()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var original = await OriginalAsync();

        await ComposerLegacyMigration.EnsureAsync(StateDir, original, Ct);

        var expected = Path.Combine(StateDir, "composer", "users", _originalId.ToString(System.Globalization.CultureInfo.InvariantCulture), "legacy-migration.json");
        Assert.Equal(expected, Evidence(original));

        var usersRoot = Path.GetFullPath(Path.Combine(StateDir, "composer", "users")) + Path.DirectorySeparatorChar;
        Assert.StartsWith(usersRoot, Path.GetFullPath(Evidence(original)));
        Assert.Equal(Path.GetDirectoryName(expected), Path.GetDirectoryName(PerUserFile(original)));
    }

    /// <summary>
    /// A CHANGED LEGACY FILE IS DETECTED BY THE EVIDENCE: after the claim, changing the legacy
    /// file makes the recorded hash no longer match, so the evidence no longer vouches for the
    /// current legacy content. (The claim itself is not re-run — the evidence short-circuits it.)
    /// </summary>
    [Fact]
    public async Task MigrationEvidence_DetectsALaterChangeToTheLegacySource()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var original = await OriginalAsync();
        await ComposerLegacyMigration.EnsureAsync(StateDir, original, Ct);

        using (var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Evidence(original), Ct)))
        {
            var recorded = json.RootElement.GetProperty("sha256").GetString();
            var legacyBytes = await File.ReadAllBytesAsync(LegacyFile, Ct);
            Assert.Equal(recorded, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(legacyBytes)));
        }

        // Change the legacy source: the recorded hash no longer matches it.
        await WriteLegacySessionAsync("different-history");
        using (var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Evidence(original), Ct)))
        {
            var recorded = json.RootElement.GetProperty("sha256").GetString();
            var legacyBytes = await File.ReadAllBytesAsync(LegacyFile, Ct);
            Assert.NotEqual(recorded, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(legacyBytes)));
        }
    }

    // ── (3) The claim is stable across a fresh identity ─────────────────────

    /// <summary>
    /// A RESTARTED PROCESS RE-RESOLVES THE ORIGINAL ADMIN AND FINDS THE EVIDENCE: the second
    /// identity object is a DIFFERENT instance, yet the seam reports
    /// <see cref="ComposerMigrationOutcome.AlreadyClaimed"/> and the destination is left
    /// byte-identical — the claim is durable, not an in-memory flag.
    /// </summary>
    [Fact]
    public async Task AFreshIdentity_FindsTheDurableEvidence_AndDoesNotReclaim()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var first = await OriginalAsync();

        Assert.Equal(ComposerMigrationOutcome.Migrated, await ComposerLegacyMigration.EnsureAsync(StateDir, first, Ct));
        var destBytes = await File.ReadAllBytesAsync(PerUserFile(first), Ct);

        // A "restart": a brand-new identity object for the SAME account.
        var second = await OriginalAsync();
        Assert.NotSame(first, second);

        Assert.Equal(ComposerMigrationOutcome.AlreadyClaimed, await ComposerLegacyMigration.EnsureAsync(StateDir, second, Ct));
        Assert.Equal(destBytes, await File.ReadAllBytesAsync(PerUserFile(second), Ct));
        Assert.True(second.PersistenceAllowed);
    }

    /// <summary>
    /// THE CLAIM IS EXCLUSIVE: the second activated ADMIN can never claim the legacy history, even
    /// after the original admin's claim completed and even when the legacy file is still present.
    /// </summary>
    [Fact]
    public async Task TheSecondAdmin_CanNeverClaimTheLegacyHistory()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var original = await OriginalAsync();
        var second = await ComposerUserState.ResolveAsync(_users, _secondId, Ct);

        await ComposerLegacyMigration.EnsureAsync(StateDir, original, Ct);

        // The second admin is not claim-eligible at all.
        Assert.False(second.LegacyClaimEligible);
        Assert.Equal(ComposerMigrationOutcome.NotEligible,
            await ComposerLegacyMigration.EnsureAsync(StateDir, second, Ct));

        // And it cannot even be resolved as an original-admin identity.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ComposerUserState.ResolveOriginalAdminAsync(_users, _secondId, Ct));

        // The original admin's per-user file is untouched by the second admin's attempt.
        Assert.False(File.Exists(PerUserFile(second)));
    }

    /// <summary>
    /// A NON-ELIGIBLE IDENTITY IS NEVER BLOCKED BY THE MIGRATION: an ordinary activated identity
    /// may persist freely (its persistence is never gated on a claim it cannot make), so the
    /// "blocked persistence" rule applies ONLY to a claim-eligible identity whose claim has not
    /// settled.
    /// </summary>
    [Fact]
    public async Task ANonEligibleIdentity_IsNeverBlockedFromPersisting()
    {
        await SeedUsersAsync();
        var second = await ComposerUserState.ResolveAsync(_users, _secondId, Ct);

        Assert.True(second.PersistenceAllowed);
        Assert.Equal(ComposerMigrationOutcome.NotEligible,
            await ComposerLegacyMigration.EnsureAsync(StateDir, second, Ct));
        Assert.True(second.PersistenceAllowed);
    }

    // ── (4) Production stays legacy ─────────────────────────────────────────

    /// <summary>
    /// THE LEGACY RUNTIME NEVER INVOKES THE MIGRATION SEAM: a legacy construction reads and
    /// writes only the legacy single file and creates NO per-user tree — not even for an
    /// identity-bound instance that merely shares the state directory.
    /// </summary>
    [Fact]
    public async Task ALegacyConstruction_NeverInvokesTheMigrationSeam()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");

        var agent = CreateAgent(StateDir, null, new LlmSessionRegistry());
        await agent.ConnectAsync(Ct);

        Assert.True(agent.IsConnected);
        Assert.True(agent.SessionLoadedFromDisk);
        Assert.Equal(["legacy-history"], agent.Session.MessageHistory.Select(m => m.Text).ToArray());
        Assert.False(Directory.Exists(Path.Combine(StateDir, "composer")));
        Assert.False(File.Exists(Path.Combine(StateDir, "composer", "users")));
    }

    /// <summary>
    /// NO PRODUCTION FILE WIRES THE MIGRATION SEAM: neither the original-admin resolution nor the
    /// seam itself is referenced from any production source outside the Composer's own
    /// orchestration types. The seam is therefore reachable only through tests, exactly as the
    /// goal requires ("Production Program invocation is deferred").
    /// </summary>
    [Fact]
    public void NoProductionFile_WiresTheOriginalAdminMigrationSeam()
    {
        var root = TestRepoRoot.Find();
        var srcRoot = Path.Combine(root, "src", "CopilotHive");

        // Every production file that mentions the seam or the original-admin resolution.
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(srcRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("Orchestration/ComposerUserState.cs", StringComparison.Ordinal))
                continue; // the seam's own definition
            if (relative.StartsWith("Orchestration/ComposerAgentService.cs", StringComparison.Ordinal))
                continue; // the connect path that calls the seam for an identity-bound service
            if (relative.Equals("Orchestration/ComposerRuntimeManager.cs", StringComparison.Ordinal))
                continue; // the per-user runtime manager: DI-registered but not wired to any live path

            var text = File.ReadAllText(file);
            if (text.Contains("ResolveOriginalAdminAsync", StringComparison.Ordinal)
                || text.Contains("ComposerLegacyMigration", StringComparison.Ordinal)
                || text.Contains("LegacyClaimEligible", StringComparison.Ordinal))
            {
                offenders.Add(relative);
            }
        }

        Assert.True(offenders.Count == 0,
            "The migration seam must stay test-driven only, but these production files reference it: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// THE PROGRAM WIRING STILL CONSTRUCTS THE LEGACY SINGLE-ADMIN COMPOSER: the production
    /// Composer registration passes NO per-user identity, and the production attachment service is
    /// the legacy one, so production keeps the legacy layout.
    /// </summary>
    [Fact]
    public void ProgramWiring_StillBuildsTheLegacySingleAdminComposer()
    {
        var programPath = Path.Combine(TestRepoRoot.Find(), "src", "CopilotHive", "Program.cs");
        Assert.True(File.Exists(programPath), $"Program.cs not found at {programPath}");

        var source = File.ReadAllText(programPath).ReplaceLineEndings("\n");

        // The production registration builds the legacy Composer through the shared seam's
        // legacy entry point, and that entry point passes NO per-user identity.
        Assert.Contains("sp.GetRequiredService<ComposerFactory>().CreateLegacy()", source, StringComparison.Ordinal);
        var factorySource = File.ReadAllText(Path.Combine(
            TestRepoRoot.Find(), "src", "CopilotHive", "Orchestration", "ComposerFactory.cs")).ReplaceLineEndings("\n");
        Assert.Contains(
            "Build(userState: null, _sp.GetService<ComposerAttachmentService>(), _sp.GetService<ComposerEventSubscriber>())",
            factorySource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveOriginalAdminAsync", source, StringComparison.Ordinal);

        // The production attachment service is the legacy (state-dir only) construction.
        Assert.Contains(
            "new ComposerAttachmentService(stateDir, sp.GetRequiredService<ILogger<ComposerAttachmentService>>())",
            source,
            StringComparison.Ordinal);
    }

    // ── (5) The evidence is part of the per-user tree ───────────────────────

    /// <summary>
    /// A FAILED CLAIM LEAVES NO EVIDENCE AND NO PARTIAL ARTEFACT: when the legacy source is
    /// corrupt, the seam throws, writes no evidence, and creates no per-user session file — so a
    /// later attempt starts from a clean, unambiguous state.
    /// </summary>
    [Fact]
    public async Task AFailedClaim_LeavesNoEvidenceAndNoPerUserSessionFile()
    {
        await SeedUsersAsync();
        await File.WriteAllTextAsync(LegacyFile, "{invalid json", Ct);
        var original = await OriginalAsync();

        await Assert.ThrowsAsync<ComposerMigrationException>(
            () => ComposerLegacyMigration.EnsureAsync(StateDir, original, Ct));

        Assert.False(File.Exists(Evidence(original)));
        Assert.False(File.Exists(PerUserFile(original)));
        Assert.False(original.PersistenceAllowed);
    }

    /// <summary>
    /// A CORRUPT LEGACY SOURCE IS NEVER COPIED: the per-user session file is not created at all,
    /// so the corrupt bytes cannot become the user's history.
    /// </summary>
    [Fact]
    public async Task ACorruptLegacySource_IsNeverCopiedToThePerUserPath()
    {
        await SeedUsersAsync();
        await File.WriteAllTextAsync(LegacyFile, "{invalid json", Ct);
        var original = await OriginalAsync();

        await Assert.ThrowsAsync<ComposerMigrationException>(
            () => ComposerLegacyMigration.EnsureAsync(StateDir, original, Ct));

        Assert.False(File.Exists(PerUserFile(original)));

        // The corrupt legacy file itself is left exactly as it was.
        Assert.Equal("{invalid json", await File.ReadAllTextAsync(LegacyFile, Ct));
    }

    /// <summary>
    /// A LEGACY FILE THAT IS VALID JSON BUT NOT A SESSION IS REJECTED TOO: the seam validates the
    /// SOURCE as a real session, so a JSON array (or any other non-session JSON) cannot be
    /// migrated into a user's history.
    /// </summary>
    [Fact]
    public async Task ALegacyFileThatIsNotASession_IsRejected()
    {
        await SeedUsersAsync();
        await File.WriteAllTextAsync(LegacyFile, "[]", Ct);
        var original = await OriginalAsync();

        await Assert.ThrowsAsync<ComposerMigrationException>(
            () => ComposerLegacyMigration.EnsureAsync(StateDir, original, Ct));

        Assert.False(File.Exists(Evidence(original)));
        Assert.False(File.Exists(PerUserFile(original)));
        Assert.False(original.PersistenceAllowed);
    }

    /// <summary>
    /// AN EMPTY-BUT-VALID SESSION FILE IS MIGRATED: a legacy file holding a valid, empty session
    /// is a legitimate (if empty) history, so it is claimed like any other — the seam validates
    /// the SHAPE, not the amount of content.
    /// </summary>
    [Fact]
    public async Task AValidButEmptyLegacySession_IsMigrated()
    {
        await SeedUsersAsync();
        await File.WriteAllTextAsync(LegacyFile, "{}", Ct);
        var original = await OriginalAsync();

        Assert.Equal(ComposerMigrationOutcome.Migrated,
            await ComposerLegacyMigration.EnsureAsync(StateDir, original, Ct));

        Assert.True(File.Exists(PerUserFile(original)));
        Assert.True(File.Exists(Evidence(original)));
        Assert.True(original.PersistenceAllowed);
    }

    /// <summary>
    /// THE CLAIMED PER-USER SESSION IS LOADED BY A REAL CONNECT AFTER THE CLAIM: the agent
    /// service reads the per-user path (never the legacy file) and the session history the
    /// original admin had is what the connect sees.
    /// </summary>
    [Fact]
    public async Task AClaimedPerUserSession_IsWhatARealConnectLoads()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var original = await OriginalAsync();

        var agent = CreateAgent(StateDir, original, new LlmSessionRegistry());
        await agent.ConnectAsync(Ct);

        Assert.True(agent.SessionLoadedFromDisk);
        Assert.Equal(["legacy-history"], agent.Session.MessageHistory.Select(m => m.Text).ToArray());

        // The legacy file is still there — the source is KEPT until (and after) the claim.
        Assert.True(File.Exists(LegacyFile));
    }

    /// <summary>
    /// THE DURABLE EVIDENCE IS EXACTLY WHAT PREVENTS RESURRECTION: with the evidence present, a
    /// reset deletes the per-user session file and the LEGACY history is NOT re-claimed on the
    /// next connect; with the evidence deleted, the very same legacy file IS re-claimed. The
    /// evidence — not the per-user file's absence — is what stops the resurrection.
    /// </summary>
    [Fact]
    public async Task TheDurableEvidence_IsWhatPreventsResurrection_NotTheMissingPerUserFile()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var user = await OriginalAsync();
        var dest = PerUserFile(user);
        var evidence = Evidence(user);

        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);

        await using (var first = CreateComposer(store, StateDir, user))
        {
            await first.ConnectAsync(Ct);
            Assert.True(File.Exists(dest));
            Assert.True(File.Exists(evidence));

            // The reset deletes the per-user file but KEEPS the evidence.
            await first.ResetSessionAsync(Ct);
            Assert.False(File.Exists(dest));
            Assert.True(File.Exists(evidence));
        }

        // With the evidence present, the reconnect does NOT resurrect the legacy history.
        await using (var second = CreateComposer(store, StateDir, user))
        {
            await second.ConnectAsync(Ct);

            Assert.False(second.SessionLoadedFromDisk);
            Assert.Empty(LiveSession(second).MessageHistory);
            Assert.False(File.Exists(dest));
        }

        // Now remove the evidence and reconnect: the legacy history IS re-claimed — proving the
        // evidence (and not something else) is what prevented the resurrection above.
        File.Delete(evidence);
        await using (var third = CreateComposer(store, StateDir, user))
        {
            await third.ConnectAsync(Ct);

            Assert.True(third.SessionLoadedFromDisk);
            Assert.Equal(["legacy-history"], LiveSession(third).MessageHistory.Select(m => m.Text).ToArray());
        }
    }

    /// <summary>
    /// A SECOND CONNECT ON THE SAME IDENTITY IS A NO-OP: the evidence short-circuits the claim, so
    /// the per-user session file is not rewritten and the connect still succeeds.
    /// </summary>
    [Fact]
    public async Task ASecondConnectOnTheSameIdentity_DoesNotRewriteTheClaimedFile()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var user = await OriginalAsync();
        var dest = PerUserFile(user);

        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);
        await using var composer = CreateComposer(store, StateDir, user);

        await composer.ConnectAsync(Ct);
        var afterFirst = await File.ReadAllBytesAsync(dest, Ct);

        await composer.ConnectAsync(Ct);

        Assert.Equal(afterFirst, await File.ReadAllBytesAsync(dest, Ct));
        Assert.True(composer.SessionLoadedFromDisk);
        Assert.Equal(["legacy-history"], LiveSession(composer).MessageHistory.Select(m => m.Text).ToArray());
    }

    /// <summary>
    /// A PENDING INVITATION HAS NO INTERNAL ID AT ALL, so it can never be mistaken for the
    /// original admin: the pending login adds NO row to the users table (only the two already
    /// activated accounts are there), and every id other than the original's is refused.
    /// </summary>
    [Fact]
    public async Task APendingInvitation_CanNeverClaimTheLegacyHistory()
    {
        await SeedUsersAsync();
        var before = await _users.ListActivatedUsersAsync(Ct);
        await _users.InviteUserAsync("pending", UserRole.Admin, Ct);
        var after = await _users.ListActivatedUsersAsync(Ct);

        // The invited-but-not-activated login adds NO row: the account count is unchanged.
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(2, after.Count);
        Assert.DoesNotContain(after, u => u.Username == "pending");

        // Every id other than the original's is refused — the pending invitation cannot claim.
        foreach (var id in new[] { _secondId, _secondId + 1, 99, int.MaxValue })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ComposerUserState.ResolveOriginalAdminAsync(_users, id, Ct));
        }
    }

    /// <summary>
    /// THE LEGACY FILE IS NEVER DELETED BY THE CLAIM: the source is kept, so the history is
    /// recoverable by an operator even after the per-user session has been reset away.
    /// </summary>
    [Fact]
    public async Task TheClaim_KeepsTheLegacySourceEvenAfterThePerUserFileIsDeleted()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var legacyBytes = await File.ReadAllBytesAsync(LegacyFile, Ct);
        var user = await OriginalAsync();

        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);
        await using var composer = CreateComposer(store, StateDir, user);
        await composer.ConnectAsync(Ct);

        await composer.ResetSessionAsync(Ct);
        Assert.False(File.Exists(PerUserFile(user)));

        Assert.Equal(legacyBytes, await File.ReadAllBytesAsync(LegacyFile, Ct));
    }

    /// <summary>
    /// THE CLAIM IS A NO-OP FOR AN IDENTITY THAT CANNOT CLAIM: the seam reports
    /// <see cref="ComposerMigrationOutcome.NotEligible"/> and writes nothing, so an ordinary
    /// user's state is never touched by the original admin's claim.
    /// </summary>
    [Fact]
    public async Task AnIneligibleIdentity_IsCompletelyUntouchedByTheSeam()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var second = await ComposerUserState.ResolveAsync(_users, _secondId, Ct);
        var secondRoot = Path.GetDirectoryName(PerUserFile(second))!;

        Assert.Equal(ComposerMigrationOutcome.NotEligible,
            await ComposerLegacyMigration.EnsureAsync(StateDir, second, Ct));

        // Nothing was created under that user's root.
        Assert.False(Directory.Exists(secondRoot));
    }

    /// <summary>
    /// THE IDENTITY'S OWN PERSISTENCE GATE FOLLOWS THE CLAIM: a claim-eligible identity is blocked
    /// until its claim settles, and unblocked once it has.
    /// </summary>
    [Fact]
    public async Task ThePersistenceGate_FollowsTheClaimOutcome()
    {
        await SeedUsersAsync();

        // A claim-eligible identity starts BLOCKED: its claim has not run yet.
        var original = await OriginalAsync();
        Assert.True(original.LegacyClaimEligible);
        Assert.False(original.PersistenceAllowed);

        // With no legacy source the claim settles immediately and persistence is allowed.
        Assert.Equal(ComposerMigrationOutcome.NoLegacySource,
            await ComposerLegacyMigration.EnsureAsync(StateDir, original, Ct));
        Assert.True(original.PersistenceAllowed);

        // An ordinary identity is never blocked (it cannot claim, so nothing is pending).
        var second = await ComposerUserState.ResolveAsync(_users, _secondId, Ct);
        Assert.True(second.PersistenceAllowed);
    }

    /// <summary>
    /// THE DELETE GUARD IS REACHABLE AND REAL: a Composer whose identity has an UNSETTLED claim
    /// never deletes its per-user session file — not on a reset, not on the overflow-recovery
    /// delete, and not on the streaming-callback delete. The per-user file may be the only copy of
    /// history after a failed migration, so it must survive.
    /// <para>
    /// The state is reached through the identity's own internal settle flag (the same seam the
    /// migration uses), because the public API cannot produce a CONNECTED Composer with an
    /// unsettled claim: a failed connect never reaches the connected state, and a successful
    /// connect always settles the claim.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AnUnsettledClaim_NeverDeletesThePerUserSessionFile()
    {
        await SeedUsersAsync();
        var user = await OriginalAsync();
        var dest = PerUserFile(user);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        await File.WriteAllTextAsync(dest, "precious-history", Ct);

        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);
        await using var composer = CreateComposer(store, StateDir, user);
        await composer.ConnectAsync(Ct);

        // The claim settled during the connect; un-settle it so the guard is armed.
        Assert.True(user.PersistenceAllowed);
        user.MarkLegacyHistorySettled(false);
        Assert.False(user.PersistenceAllowed);

        // ── the reset's delete ──
        await composer.ResetSessionAsync(Ct);
        Assert.True(File.Exists(dest), "An unsettled claim must never delete the per-user session file.");
        Assert.Equal("precious-history", await File.ReadAllTextAsync(dest, Ct));

        // ── the overflow-recovery delete ──
        await using (var overflowing = CreateComposer(store, StateDir, user, _ => OverflowClient()))
        {
            await overflowing.ConnectAsync(Ct);
            user.MarkLegacyHistorySettled(false);

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            overflowing.OnStreamingUpdate += () => { if (!overflowing.IsStreaming) done.TrySetResult(); };
            overflowing.SendMessage("hello");
            await done.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

            Assert.True(File.Exists(dest), "The overflow-recovery delete must respect the unsettled claim.");
            Assert.Equal("precious-history", await File.ReadAllTextAsync(dest, Ct));
        }
    }

    /// <summary>
    /// A SETTLED CLAIM DOES DELETE THE PER-USER SESSION FILE: the guard is a real gate, not a
    /// blanket refusal — after the claim settled, a reset removes the per-user file exactly as it
    /// always did.
    /// </summary>
    [Fact]
    public async Task ASettledClaim_DeletesThePerUserSessionFileOnReset()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var user = await OriginalAsync();
        var dest = PerUserFile(user);

        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);
        await using var composer = CreateComposer(store, StateDir, user);
        await composer.ConnectAsync(Ct);

        Assert.True(user.PersistenceAllowed);
        Assert.True(File.Exists(dest));

        await composer.ResetSessionAsync(Ct);

        Assert.False(File.Exists(dest));
    }

    /// <summary>
    /// THE MIGRATION EVIDENCE IS WRITTEN ONLY AFTER THE COPY VERIFIED: when the seam runs, the
    /// per-user session file and the evidence appear TOGETHER — never the evidence alone.
    /// </summary>
    [Fact]
    public async Task TheEvidenceIsWrittenOnlyAfterTheCopyVerified()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var original = await OriginalAsync();

        Assert.False(File.Exists(Evidence(original)));
        Assert.False(File.Exists(PerUserFile(original)));

        await ComposerLegacyMigration.EnsureAsync(StateDir, original, Ct);

        Assert.True(File.Exists(PerUserFile(original)));
        Assert.True(File.Exists(Evidence(original)));

        // And the copied file is byte-identical to the source it claimed.
        Assert.Equal(
            await File.ReadAllBytesAsync(LegacyFile, Ct),
            await File.ReadAllBytesAsync(PerUserFile(original), Ct));
    }

    /// <summary>
    /// NO TEMPORARY ARTEFACT SURVIVES A SUCCESSFUL CLAIM: the temp-then-move copy leaves no
    /// <c>*.migrating-*</c> file behind in the per-user root.
    /// </summary>
    [Fact]
    public async Task ASuccessfulClaim_LeavesNoTemporaryArtefact()
    {
        await SeedUsersAsync();
        await WriteLegacySessionAsync("legacy-history");
        var original = await OriginalAsync();

        await ComposerLegacyMigration.EnsureAsync(StateDir, original, Ct);

        var userRoot = Path.GetDirectoryName(Evidence(original))!;
        Assert.Empty(Directory.GetFiles(userRoot, "*.migrating-*"));
        Assert.Empty(Directory.GetFiles(userRoot, "*.tmp-*"));
        Assert.Equal(
            ["legacy-migration.json", "session.json"],
            Directory.GetFiles(userRoot).Select(Path.GetFileName).Order().ToArray());
    }
}

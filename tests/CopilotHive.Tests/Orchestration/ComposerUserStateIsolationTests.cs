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

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Round-1 ISOLATION tests for the shared per-user Composer identity/path helper: the properties
/// that must hold BETWEEN two identities and BETWEEN a per-user and a legacy instance.
/// <para>
/// The four areas proved here are deliberately disjoint from the single-instance policy proved in
/// <see cref="ComposerUserStateTests"/>:
/// <list type="number">
///   <item><b>(1) Cross-identity disjointness</b> — two validated identities never share a session
///   file, a registry key or an attachments root, and one identity's failed reconnect (the
///   catch-path unregister) removes ONLY its own registry entry — the other identity's entry and
///   the legacy <c>composer</c> entry both survive.</item>
///   <item><b>(2) Containment</b> — every per-user artefact lands under the canonical
///   <c>{stateDir}/composer/users/N</c> tree and nowhere else; a rejected identity leaves NO
///   artefact behind, and a GitHub-id string is never mistaken for an internal id.</item>
///   <item><b>(3) Mismatch rejection</b> — an identity-bound Composer rejects a mismatched
///   attachment service at CONSTRUCTION, before it can publish anything, and an identity-bound
///   Composer with NO attachment service is accepted.</item>
///   <item><b>(4) Legacy byte-identical behaviour</b> — the overflow-recovery delete and the
///   constructor-installed streaming-callback delete operate on the LEGACY path for a legacy
///   Composer and on the PER-USER path for an identity-bound Composer, and neither ever touches
///   the other user's file.</item>
/// </list>
/// </para>
/// <para>
/// <b>Determinism.</b> No wall-clock assertion is used anywhere: the overflow turns are driven by
/// a chat client that yields NOTHING and then fails, so the only completion signal is the
/// Composer's own terminal transition, observed through its <c>OnStreamingUpdate</c> event. The
/// user directory is the real one — a file-backed SQLite context driven through
/// <see cref="UserService.CreateOrUpdateUserAsync"/>/<see cref="UserService.InviteUserAsync"/>/
/// <see cref="UserService.ActivateInvitationAsync"/> — never a stub.
/// </para>
/// </summary>
public sealed class ComposerUserStateIsolationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-user-isolation-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly FileDbFactory _factory;
    private readonly UserService _users;

    public ComposerUserStateIsolationTests()
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

    /// <summary>A file-backed SQLite context factory (never a stubbed directory).</summary>
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

    /// <summary>
    /// Bootstraps the original admin (GitHub id "2" — deliberately EQUAL to the internal id the
    /// second account will get) and activates one invited account (GitHub id "999").
    /// </summary>
    private async Task<(ComposerUserState First, ComposerUserState Second)> TwoUsersAsync()
    {
        var original = await _users.CreateOrUpdateUserAsync(
            "2", "first", null, null, null, "tok", "ref", "2030-01-01T00:00:00.0000000Z", Ct);
        await _users.InviteUserAsync("second", UserRole.Admin, Ct);
        var invited = await _users.ActivateInvitationAsync("second", "999", null, null, null, Ct);
        return (await ComposerUserState.ResolveAsync(_users, original.Id, Ct),
                await ComposerUserState.ResolveAsync(_users, invited.Id, Ct));
    }

    private static ComposerAgentService CreateAgent(
        string stateDir, ComposerUserState? user, LlmSessionRegistry? registry,
        Func<string, IChatClient>? factory = null) =>
        new(
            "test-model", 64000, 50, null, null, "system prompt", new List<AITool>(), null, stateDir,
            null, NullLogger<ComposerAgentService>.Instance,
            factory ?? (_ => new Mock<IChatClient>().Object),
            registry, ["test-model"], null, null, false, [], null, user);

    private static Composer CreateComposer(
        GoalStore store, string stateDir, ComposerUserState? user, ComposerAttachmentService? attachments,
        LlmSessionRegistry? registry = null, Func<string, IChatClient>? chatClientFactory = null) =>
        new("test-model", NullLogger<Composer>.Instance, store, stateDir: stateDir,
            chatClientFactory: chatClientFactory ?? (_ => new Mock<IChatClient>().Object),
            sessionRegistry: registry, attachmentService: attachments, userState: user);

    /// <summary>
    /// A chat client that yields NOTHING and then fails with the provider's context-overflow
    /// text. Because it yields nothing, the streaming loop has no delta to report, so the ONLY
    /// completion signal is the terminal transition.
    /// </summary>
    private static IChatClient CreateOverflowClient()
    {
        var mock = new Mock<IChatClient>();
        mock
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("model_max_prompt_tokens_exceeded"));
        mock
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("model_max_prompt_tokens_exceeded"));
        return mock.Object;
    }

    /// <summary>
    /// Observes the Composer's terminal transition: the callback fires for every streaming
    /// update, but it resolves the wait ONLY once the Composer is no longer streaming — the
    /// transition that follows the terminal handler (and, on the overflow path, the delete).
    /// </summary>
    private static Task AwaitTerminalTransitionAsync(Composer composer)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observer()
        {
            if (composer.IsStreaming) return;
            completed.TrySetResult();
        }

        composer.OnStreamingUpdate += Observer;
        return completed.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
    }

    /// <summary>Reads the Composer's private streaming service (the one the constructor wires).</summary>
    private static ComposerStreamingService GetStreamingService(Composer composer) =>
        (ComposerStreamingService)(typeof(Composer)
            .GetField("_streamingService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(composer)
            ?? throw new InvalidOperationException("_streamingService was null"));

    private static string LegacySessionFile(string stateDir) => Path.Combine(stateDir, "composer-session.json");

    private static string PerUserSessionFile(string stateDir, ComposerUserState user) =>
        Path.Combine(stateDir, "composer", "users", user.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), "session.json");

    // ── (1) Cross-identity disjointness ──────────────────────────────────────

    /// <summary>
    /// TWO IDENTITY-BOUND COMPOSERS NEVER SHARE A REGISTRY KEY: the facade's own refresh site
    /// (<c>Composer.RefreshComposerRegistry</c>) publishes <c>composer:N</c>, so two identities
    /// produce two DISTINCT entries and the legacy <c>composer</c> key is absent.
    /// </summary>
    [Fact]
    public async Task TwoIdentityBoundComposers_PublishDistinctRegistryKeys_AndNoLegacyKey()
    {
        var (first, second) = await TwoUsersAsync();
        var registry = new LlmSessionRegistry();
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);

        await using var a = CreateComposer(store, StateDir, first, null, registry);
        await using var b = CreateComposer(store, StateDir, second, null, registry);
        await a.ConnectAsync(Ct);
        await b.ConnectAsync(Ct);

        var ids = registry.GetAll().Select(s => s.SessionId).Order().ToArray();
        Assert.Equal(["composer:1", "composer:2"], ids);
        Assert.DoesNotContain("composer", ids);
    }

    /// <summary>
    /// ONE IDENTITY'S FAILED RECONNECT REMOVES ONLY ITS OWN ENTRY: the catch-path unregister of
    /// the per-user key leaves BOTH the other identity's entry AND the legacy <c>composer</c>
    /// entry intact.
    /// </summary>
    [Fact]
    public async Task OneIdentitysFailedReconnect_RemovesOnlyItsOwnRegistryEntry()
    {
        var (first, second) = await TwoUsersAsync();
        var registry = new LlmSessionRegistry();

        // The legacy entry is published by a legacy Composer, exactly as the legacy runtime does.
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);
        await using (var legacy = CreateComposer(store, StateDir, null, null, registry))
        {
            await legacy.ConnectAsync(Ct);
        }

        var failFirst = false;
        var a = CreateAgent(StateDir, first, registry,
            _ => failFirst ? throw new InvalidOperationException("boom") : new Mock<IChatClient>().Object);
        var b = CreateAgent(StateDir, second, registry);
        await a.ConnectAsync(Ct);
        await b.ConnectAsync(Ct);
        Assert.Equal(["composer", "composer:1", "composer:2"], registry.GetAll().Select(s => s.SessionId).Order().ToArray());

        failFirst = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.ConnectAsync(Ct));

        var remaining = registry.GetAll().Select(s => s.SessionId).Order().ToArray();
        Assert.Equal(["composer", "composer:2"], remaining);
        Assert.DoesNotContain("composer:1", remaining);
    }

    /// <summary>
    /// TWO IDENTITIES NEVER SHARE AN ATTACHMENTS ROOT OR A RECORD: each identity-bound attachment
    /// service keeps its own files, and one service can neither see nor delete the other's.
    /// </summary>
    [Fact]
    public async Task TwoIdentities_KeepDisjointAttachmentRootsAndRecords()
    {
        var (first, second) = await TwoUsersAsync();

        var a = new ComposerAttachmentService(StateDir, first, NullLogger<ComposerAttachmentService>.Instance);
        var b = new ComposerAttachmentService(StateDir, second, NullLogger<ComposerAttachmentService>.Instance);

        Assert.NotEqual(a.AttachmentsRootPath, b.AttachmentsRootPath);
        Assert.Equal(first.UserId, a.UserId);
        Assert.Equal(second.UserId, b.UserId);

        var saved = await a.SaveAsync("x.png", new MemoryStream(new byte[] { 1, 2, 3 }), Ct);
        Assert.True(saved.Success);

        // The file exists ONLY under the first user's root.
        Assert.True(File.Exists(Path.Combine(a.AttachmentsRootPath, saved.Attachment!.SavedRelativePath)));
        Assert.Empty(Directory.GetFiles(b.AttachmentsRootPath));

        // The other service does not know the record and cannot delete it.
        Assert.False(b.Remove(saved.Attachment).Success);
        Assert.True(File.Exists(Path.Combine(a.AttachmentsRootPath, saved.Attachment!.SavedRelativePath)));
    }

    // ── (2) Containment ─────────────────────────────────────────────────────

    /// <summary>
    /// EVERY PER-USER ARTEFACT LANDS UNDER THE CANONICAL PER-USER TREE: the session file is
    /// exactly <c>{stateDir}/composer/users/N/session.json</c>, the attachments root is exactly
    /// <c>{stateDir}/composer/users/N/attachments</c>, and both are contained by
    /// <c>{stateDir}/composer/users/</c> — never anywhere else in the state root.
    /// </summary>
    [Fact]
    public async Task PerUserArtefacts_AreExactlyUnderTheCanonicalUsersTree()
    {
        var (first, second) = await TwoUsersAsync();
        var stateRoot = Path.GetFullPath(StateDir) + Path.DirectorySeparatorChar;
        var usersRoot = Path.GetFullPath(Path.Combine(StateDir, "composer", "users")) + Path.DirectorySeparatorChar;

        foreach (var user in new[] { first, second })
        {
            var id = user.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            Assert.Equal(Path.Combine(StateDir, "composer", "users", id, "session.json"),
                ComposerUserState.SessionFilePath(StateDir, user));
            Assert.Equal(Path.GetFullPath(Path.Combine(StateDir, "composer", "users", id, "attachments")),
                ComposerUserState.AttachmentsRootPath(StateDir, user));

            Assert.StartsWith(stateRoot, Path.GetFullPath(ComposerUserState.SessionFilePath(StateDir, user)));
            Assert.StartsWith(usersRoot, Path.GetFullPath(ComposerUserState.SessionFilePath(StateDir, user)));
            Assert.StartsWith(usersRoot, ComposerUserState.AttachmentsRootPath(StateDir, user));
        }

        // The two identities never share a session file or a root.
        Assert.NotEqual(
            ComposerUserState.SessionFilePath(StateDir, first),
            ComposerUserState.SessionFilePath(StateDir, second));
        Assert.NotEqual(
            ComposerUserState.AttachmentsRootPath(StateDir, first),
            ComposerUserState.AttachmentsRootPath(StateDir, second));
    }

    /// <summary>
    /// A REJECTED IDENTITY LEAVES NO ARTEFACT: the resolution fails LOUDLY (an exception, never
    /// a silent fall back to the legacy layout) and creates nothing under the state root.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    [InlineData(int.MaxValue)]
    public async Task RejectedId_ThrowsAndLeavesNoStateArtefact(int id)
    {
        await TwoUsersAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => ComposerUserState.ResolveAsync(_users, id, Ct));

        Assert.False(Directory.Exists(Path.Combine(StateDir, "composer")));
        Assert.False(File.Exists(LegacySessionFile(StateDir)));
    }

    /// <summary>
    /// A GITHUB-ID STRING IS NEVER MISTAKEN FOR AN INTERNAL ID: the bootstrap account's GitHub id
    /// is "2", but the text "2" addresses the account whose INTERNAL id is 2 — the second,
    /// invited account whose GitHub id is "999".
    /// </summary>
    [Fact]
    public async Task Resolve_TextIsAlwaysAnInternalId_NeverAGitHubId()
    {
        var (first, second) = await TwoUsersAsync();

        // The bootstrap account has GitHub id "2" and internal id 1.
        var admin = await _users.GetByGitHubIdAsync("2", Ct);
        Assert.NotNull(admin);
        Assert.Equal(first.UserId, admin!.Id);

        // The text "2" resolves to the account with INTERNAL id 2 — NOT the account whose GitHub
        // id happens to be "2".
        var viaText = await ComposerUserState.ResolveAsync(_users, "2", Ct);
        Assert.Equal(second.UserId, viaText.UserId);
        Assert.NotEqual(first.UserId, viaText.UserId);

        // The GitHub id of the second account ("999") addresses no internal account at all.
        await Assert.ThrowsAsync<InvalidOperationException>(() => ComposerUserState.ResolveAsync(_users, "999", Ct));
    }

    /// <summary>
    /// A USERNAME (and every other non-numeric text) IS REJECTED — a GitHub login never reaches a
    /// path, even when it is the login of an ACTIVATED account.
    /// </summary>
    [Theory]
    [InlineData("first")]
    [InlineData("second")]
    [InlineData("FIRST")]
    [InlineData(" ")]
    [InlineData("  ")]
    [InlineData("\t1")]
    [InlineData("1\n")]
    [InlineData("../composer/users/1")]
    [InlineData("1/../../etc")]
    [InlineData("\\\\server\\share")]
    public async Task Resolve_UsernameAndPathText_AreRejectedLoudly(string raw)
    {
        await TwoUsersAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => ComposerUserState.ResolveAsync(_users, raw, Ct));

        Assert.False(Directory.Exists(Path.Combine(StateDir, "composer")));
    }

    /// <summary>
    /// THE TEXT "0" IS A VALID INTEGER AND IS REJECTED BY THE POSITIVITY RULE: the digits parse
    /// (so the text overload does not throw an argument-format error), but the resulting id is
    /// not positive, so the resolution refuses it loudly as well.
    /// </summary>
    [Fact]
    public async Task Resolve_TextZero_IsRejectedByThePositivityRule()
    {
        await TwoUsersAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ComposerUserState.ResolveAsync(_users, "0", Ct));

        Assert.False(Directory.Exists(Path.Combine(StateDir, "composer")));
    }

    // ── (3) Mismatch rejection ──────────────────────────────────────────────

    /// <summary>
    /// AN IDENTITY-BOUND COMPOSER WITH NO ATTACHMENT SERVICE IS ACCEPTED: the mismatch rule only
    /// binds when a service is actually supplied.
    /// </summary>
    [Fact]
    public async Task IdentityBoundComposer_WithoutAttachmentService_IsAccepted()
    {
        var (first, _) = await TwoUsersAsync();
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);

        await using var composer = CreateComposer(store, StateDir, first, null);

        Assert.NotNull(composer);
    }

    /// <summary>
    /// THE MISMATCH IS REJECTED AT CONSTRUCTION — before the Composer can publish a registry entry
    /// or touch the attachments root of the wrong user.
    /// </summary>
    [Fact]
    public async Task MismatchedAttachmentService_IsRejectedBeforeAnyPublication()
    {
        var (first, second) = await TwoUsersAsync();
        var registry = new LlmSessionRegistry();
        var other = new ComposerAttachmentService(StateDir, second, NullLogger<ComposerAttachmentService>.Instance);
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);

        Assert.Throws<ArgumentException>(() => CreateComposer(store, StateDir, first, other, registry));

        // Nothing was published and the OTHER user's root was not touched.
        Assert.Empty(registry.GetAll());
        Assert.Empty(Directory.GetFiles(other.AttachmentsRootPath));
    }

    /// <summary>
    /// THE TEXT OVERLOAD REJECTS BEFORE ANY DIRECTORY LOOKUP: with a directory that THROWS on
    /// every access, a non-numeric text still fails with the format error (so no lookup
    /// happened), while a numeric id DOES reach the directory (and its failure propagates).
    /// </summary>
    [Fact]
    public async Task Resolve_TextIsRejectedBeforeAnyDirectoryLookup()
    {
        var exploding = new UserService(
            new ExplodingDbFactory(),
            NullLogger<UserService>.Instance);

        // Non-numeric text: rejected by the format rule, never by the directory.
        await Assert.ThrowsAsync<ArgumentException>(() => ComposerUserState.ResolveAsync(exploding, "not-a-number", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => ComposerUserState.ResolveAsync(exploding, "../1", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => ComposerUserState.ResolveAsync(exploding, "", Ct));

        // A numeric id DOES reach the directory — proving the text rule short-circuits first.
        var directoryFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ComposerUserState.ResolveAsync(exploding, 7, Ct));
        Assert.Contains("directory was touched", directoryFailure.Message);
    }

    /// <summary>A context factory that throws on every access — used to prove a lookup did NOT happen.</summary>
    private sealed class ExplodingDbFactory : IDbContextFactory<CopilotHiveDbContext>
    {
        public CopilotHiveDbContext CreateDbContext() =>
            throw new InvalidOperationException("the directory was touched");
    }

    /// <summary>
    /// A PENDING INVITATION NEVER BECOMES AN INTERNAL ACCOUNT ID: the invited-but-not-activated
    /// login has no row in the users table, so no internal id can resolve to it.
    /// </summary>
    [Fact]
    public async Task PendingInvitation_NeverResolvesToAnIdentity()
    {
        await TwoUsersAsync();

        // A third login is invited but NEVER activated.
        await _users.InviteUserAsync("third", UserRole.Admin, Ct);

        // Exactly two accounts exist; the pending invitation is not one of them.
        Assert.Equal(2, await _users.GetUserCountAsync(Ct));

        // Every id beyond the two activated accounts is refused.
        for (var id = 3; id <= 6; id++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ComposerUserState.ResolveAsync(_users, id, Ct));
        }
    }

    /// <summary>
    /// THE EXACT REJECTION KIND PER INPUT CLASS: a non-numeric text is an argument-format error,
    /// a non-positive number is an out-of-range error, and an unknown positive id is a
    /// "no activated account" error. Each is a distinct, loud refusal.
    /// </summary>
    [Theory]
    [InlineData("first")]
    [InlineData("second")]
    [InlineData("../1")]
    [InlineData("1/../../x")]
    [InlineData("/etc/passwd")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1.0")]
    [InlineData("٣")]
    [InlineData("99999999999")]
    [InlineData("")]
    public async Task Resolve_NonIntegerText_IsAnArgumentFormatError(string raw)
    {
        await TwoUsersAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => ComposerUserState.ResolveAsync(_users, raw, Ct));

        Assert.Equal("rawUserId", ex.ParamName);
    }

    /// <summary>
    /// A NULL TEXT IS REJECTED AS A FORMAT ERROR TOO (never silently treated as "no identity").
    /// </summary>
    [Fact]
    public async Task Resolve_NullText_IsRejected()
    {
        await TwoUsersAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => ComposerUserState.ResolveAsync(_users, (string?)null, Ct));

        Assert.Equal("rawUserId", ex.ParamName);
    }

    /// <summary>
    /// THE IDENTITY TYPE HAS NO PUBLIC CONSTRUCTOR: an unvalidated id can therefore never reach
    /// a path — the only way to obtain a <see cref="ComposerUserState"/> is the validating
    /// resolution, which proves the id is positive AND resolves to an activated account.
    /// </summary>
    [Fact]
    public void ComposerUserState_HasNoPublicConstructor_AcceptingAnUnvalidatedId()
    {
        var publicConstructors = typeof(ComposerUserState)
            .GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        Assert.Empty(publicConstructors);
    }

    /// <summary>
    /// TWO IDENTITY-BOUND COMPOSERS SAVE TO DISTINCT SESSION FILES: each Composer persists its own
    /// history under its own per-user path, and neither file contains the other's content.
    /// </summary>
    [Fact]
    public async Task TwoIdentityBoundComposers_SaveToDistinctSessionFiles()
    {
        var (first, second) = await TwoUsersAsync();
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);

        await using var a = CreateComposer(store, StateDir, first, null);
        await using var b = CreateComposer(store, StateDir, second, null);
        await a.ConnectAsync(Ct);
        await b.ConnectAsync(Ct);

        GetSession(a).MessageHistory.Add(new ChatMessage(ChatRole.User, "marker-from-first"));
        GetSession(b).MessageHistory.Add(new ChatMessage(ChatRole.User, "marker-from-second"));

        await a.SaveSessionAsync(Ct);
        await b.SaveSessionAsync(Ct);

        var firstFile = PerUserSessionFile(StateDir, first);
        var secondFile = PerUserSessionFile(StateDir, second);
        Assert.True(File.Exists(firstFile));
        Assert.True(File.Exists(secondFile));
        Assert.NotEqual(firstFile, secondFile);

        var firstText = await File.ReadAllTextAsync(firstFile, Ct);
        var secondText = await File.ReadAllTextAsync(secondFile, Ct);
        Assert.Contains("marker-from-first", firstText);
        Assert.DoesNotContain("marker-from-second", firstText);
        Assert.Contains("marker-from-second", secondText);
        Assert.DoesNotContain("marker-from-first", secondText);

        // The legacy single-file location is never written by a per-user Composer.
        Assert.False(File.Exists(LegacySessionFile(StateDir)));
    }

    /// <summary>
    /// AN IDENTITY-BOUND AGENT NEVER LOADS THE LEGACY SESSION FILE: the legacy
    /// <c>{stateDir}/composer-session.json</c> belongs to the legacy runtime, so an identity-bound
    /// connect must start fresh even when the legacy file holds a real history.
    /// </summary>
    [Fact]
    public async Task IdentityBoundAgent_NeverLoadsTheLegacySessionFile()
    {
        var (first, _) = await TwoUsersAsync();

        // A REAL legacy session, with content the identity-bound agent must NOT pick up.
        var legacyFile = LegacySessionFile(StateDir);
        var legacy = SharpCoder.AgentSession.Create("composer");
        legacy.MessageHistory.Add(new ChatMessage(ChatRole.User, "legacy-history-marker"));
        await legacy.SaveAsync(legacyFile, Ct);

        var agent = CreateAgent(StateDir, first, new LlmSessionRegistry());
        await agent.ConnectAsync(Ct);

        Assert.False(agent.SessionLoadedFromDisk);
        Assert.DoesNotContain(agent.Session.MessageHistory, m => m.Text == "legacy-history-marker");

        // The legacy file is left untouched — reading it never destroys it.
        Assert.True(File.Exists(legacyFile));
    }

    /// <summary>
    /// THE MODEL-SWITCH REGISTRATION IS ALSO PER-USER: switching the model of an identity-bound
    /// agent re-publishes <c>composer:N</c> and never OVERWRITES another identity's (or the
    /// legacy runtime's) entry.
    /// </summary>
    [Fact]
    public async Task IdentityBoundAgent_SwitchModel_RePublishesItsOwnKeyOnly()
    {
        var (first, second) = await TwoUsersAsync();
        var registry = new LlmSessionRegistry();
        var hive = new CopilotHive.Configuration.HiveConfigFile
        {
            Models = new CopilotHive.Configuration.ModelsConfig
            {
                AvailableModels = [new() { Name = "test-model" }, new() { Name = "model-b" }]
            }
        };

        // A legacy Composer owns the plain "composer" key.
        using (var db = CopilotHiveDbContext.CreateInMemory())
        {
            var store = new GoalStore(db, NullLogger<GoalStore>.Instance);
            await using var legacy = CreateComposer(store, StateDir, null, null, registry);
            await legacy.ConnectAsync(Ct);
        }

        var agent = new ComposerAgentService(
            "test-model", 64000, 50, null, hive, "p", new List<AITool>(), null, StateDir, null,
            NullLogger<ComposerAgentService>.Instance, _ => new Mock<IChatClient>().Object, registry,
            ["test-model", "model-b"], null, null, false, [], null, first);
        await agent.ConnectAsync(Ct);

        // A second identity holds its own key at the same time.
        var other = CreateAgent(StateDir, second, registry);
        await other.ConnectAsync(Ct);
        Assert.Equal(["composer", "composer:1", "composer:2"],
            registry.GetAll().Select(s => s.SessionId).Order().ToArray());

        await agent.SwitchModelAsync("model-b", ReasoningEffort.High, Ct);

        var entries = registry.GetAll().OrderBy(s => s.SessionId).ToArray();
        Assert.Equal(["composer", "composer:1", "composer:2"], entries.Select(s => s.SessionId).ToArray());
        Assert.Equal("model-b", entries.Single(s => s.SessionId == "composer:1").Model);
        Assert.Equal("test-model", entries.Single(s => s.SessionId == "composer").Model);
    }

    /// <summary>
    /// THE FACADE'S OWN REGISTRY REFRESH IS PER-USER TOO: after the entry is removed from the
    /// registry, the Composer's own refresh (here: the one the reset runs) re-publishes
    /// <c>composer:N</c> — it never falls back to the plain <c>composer</c> key.
    /// </summary>
    [Fact]
    public async Task IdentityBoundComposer_OwnRegistryRefresh_RePublishesItsPerUserKey()
    {
        var (first, _) = await TwoUsersAsync();
        var registry = new LlmSessionRegistry();
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);

        await using var composer = CreateComposer(store, StateDir, first, null, registry);
        await composer.ConnectAsync(Ct);
        Assert.Equal(["composer:1"], registry.GetAll().Select(s => s.SessionId).ToArray());

        // Remove the entry so the NEXT publication can only come from the Composer's own refresh.
        Assert.True(registry.Unregister("composer:1"));
        Assert.Empty(registry.GetAll());

        await composer.ResetSessionAsync(Ct);

        var ids = registry.GetAll().Select(s => s.SessionId).ToArray();
        Assert.Equal(["composer:1"], ids);
        Assert.DoesNotContain("composer", ids);
    }

    /// <summary>
    /// A LEGACY COMPOSER'S OWN REGISTRY REFRESH KEEPS THE PLAIN <c>composer</c> KEY: the legacy
    /// layout is byte-identical, so the refresh must not invent a per-user key.
    /// </summary>
    [Fact]
    public async Task LegacyComposer_OwnRegistryRefresh_KeepsThePlainComposerKey()
    {
        var registry = new LlmSessionRegistry();
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);

        await using var composer = CreateComposer(store, StateDir, null, null, registry);
        await composer.ConnectAsync(Ct);
        Assert.True(registry.Unregister("composer"));

        await composer.ResetSessionAsync(Ct);

        Assert.Equal(["composer"], registry.GetAll().Select(s => s.SessionId).ToArray());
    }

    /// <summary>
    /// THE ATTACHMENT SERVICE OF AN IDENTITY-BOUND COMPOSER USES THE CANONICAL PER-USER ROOT:
    /// the file lands in <c>{stateDir}/composer/users/N/attachments</c> and NOT in the legacy
    /// <c>{stateDir}/composer-attachments</c> directory.
    /// </summary>
    [Fact]
    public async Task IdentityBoundAttachmentService_StoresUnderTheCanonicalPerUserRoot()
    {
        var (first, _) = await TwoUsersAsync();

        var attachments = new ComposerAttachmentService(StateDir, first, NullLogger<ComposerAttachmentService>.Instance);
        var expectedRoot = Path.GetFullPath(Path.Combine(StateDir, "composer", "users", "1", "attachments"));
        Assert.Equal(expectedRoot, attachments.AttachmentsRootPath);

        var saved = await attachments.SaveAsync("diagram.png", new MemoryStream(new byte[] { 9 }), Ct);
        Assert.True(saved.Success);

        Assert.True(File.Exists(Path.Combine(expectedRoot, saved.Attachment!.SavedRelativePath)));
        Assert.False(Directory.Exists(Path.Combine(StateDir, "composer-attachments")));
    }

    /// <summary>
    /// THE IDENTITY IS THREADED FROM THE COMPOSER TO ITS AGENT SERVICE: the agent service the
    /// Composer constructs carries the SAME identity, and the Composer's connect therefore LOADS
    /// the session from the PER-USER path (proving the agent's own load path is per-user and not
    /// the legacy one).
    /// </summary>
    [Fact]
    public async Task IdentityBoundComposer_ThreadsTheIdentityToItsAgentService()
    {
        var (first, _) = await TwoUsersAsync();
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);

        // A real per-user session file, with content only the per-user path can produce.
        var perUserFile = PerUserSessionFile(StateDir, first);
        Directory.CreateDirectory(Path.GetDirectoryName(perUserFile)!);
        var persisted = SharpCoder.AgentSession.Create("composer");
        persisted.MessageHistory.Add(new ChatMessage(ChatRole.User, "per-user-marker"));
        await persisted.SaveAsync(perUserFile, Ct);

        await using var composer = CreateComposer(store, StateDir, first, null);
        await composer.ConnectAsync(Ct);

        // The agent service the Composer built carries the identity.
        var agentService = typeof(Composer)
            .GetField("_agentService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(composer)
            ?? throw new InvalidOperationException("_agentService was null");
        var userStateField = agentService.GetType()
            .GetField("_userState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_userState field not found on ComposerAgentService");
        Assert.Same(first, userStateField.GetValue(agentService));

        // And the connect really loaded the PER-USER file (not the legacy one).
        Assert.True(composer.SessionLoadedFromDisk);
        Assert.Contains(
            ((SharpCoder.AgentSession)agentService.GetType().GetProperty("Session")!.GetValue(agentService)!)
                .MessageHistory,
            m => m.Text == "per-user-marker");
    }

    /// <summary>
    /// UNREGISTERING ONE IDENTITY'S KEY LEAVES THE OTHER IDENTITY'S ENTRY INTACT: the registry
    /// keys are disjoint, so removing <c>composer:1</c> can never remove <c>composer:2</c> (nor
    /// the legacy <c>composer</c> key).
    /// </summary>
    [Fact]
    public async Task UnregisteringOneIdentitysKey_LeavesTheOtherIdentitysEntryIntact()
    {
        var (first, second) = await TwoUsersAsync();
        var registry = new LlmSessionRegistry();
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);

        await using (var legacy = CreateComposer(store, StateDir, null, null, registry))
        {
            await legacy.ConnectAsync(Ct);
        }
        await using var a = CreateComposer(store, StateDir, first, null, registry);
        await using var b = CreateComposer(store, StateDir, second, null, registry);
        await a.ConnectAsync(Ct);
        await b.ConnectAsync(Ct);

        Assert.Equal(["composer", "composer:1", "composer:2"],
            registry.GetAll().Select(s => s.SessionId).Order().ToArray());

        Assert.True(registry.Unregister("composer:1"));

        Assert.Equal(["composer", "composer:2"],
            registry.GetAll().Select(s => s.SessionId).Order().ToArray());
    }

    /// <summary>Reads the Composer's live agent session through the private agent-service field.</summary>
    private static SharpCoder.AgentSession GetSession(Composer composer)
    {
        var service = typeof(Composer)
            .GetField("_agentService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(composer)
            ?? throw new InvalidOperationException("_agentService was null");
        return (SharpCoder.AgentSession)service.GetType()
            .GetProperty("Session")!.GetValue(service)!;
    }

    // ── (4) Legacy byte-identical behaviour ─────────────────────────────────

    /// <summary>
    /// THE OVERFLOW-RECOVERY DELETE FOLLOWS THE IDENTITY: for a legacy Composer it deletes the
    /// LEGACY session file; for an identity-bound Composer it deletes that user's PER-USER file —
    /// and it NEVER touches the other user's file or the legacy file of the other layout.
    /// </summary>
    [Fact]
    public async Task OverflowRecovery_DeletesTheSessionFileOfTheOwningIdentityOnly()
    {
        var (first, second) = await TwoUsersAsync();

        // ── legacy Composer ──
        using (var db = CopilotHiveDbContext.CreateInMemory())
        {
            var store = new GoalStore(db, NullLogger<GoalStore>.Instance);
            await using var legacy = CreateComposer(store, StateDir, null, null, null, _ => CreateOverflowClient());
            await legacy.ConnectAsync(Ct);
            await legacy.SaveSessionAsync(Ct);

            var legacyFile = LegacySessionFile(StateDir);
            Assert.True(File.Exists(legacyFile));

            // A per-user file of ANOTHER identity must survive the legacy Composer's overflow.
            var otherUsersFile = PerUserSessionFile(StateDir, first);
            Directory.CreateDirectory(Path.GetDirectoryName(otherUsersFile)!);
            await File.WriteAllTextAsync(otherUsersFile, "{\"other\":\"user\"}", Ct);

            var terminal = AwaitTerminalTransitionAsync(legacy);
            legacy.SendMessage("hello");
            await terminal;

            Assert.False(File.Exists(legacyFile));
            Assert.True(File.Exists(otherUsersFile));
        }

        // ── identity-bound Composer ──
        using (var db = CopilotHiveDbContext.CreateInMemory())
        {
            var store = new GoalStore(db, NullLogger<GoalStore>.Instance);
            await using var bound = CreateComposer(store, StateDir, first, null, null, _ => CreateOverflowClient());
            await bound.ConnectAsync(Ct);
            await bound.SaveSessionAsync(Ct);

            var perUserFile = PerUserSessionFile(StateDir, first);
            Assert.True(File.Exists(perUserFile));

            // The legacy file must survive the per-user Composer's overflow.
            var legacyFile = LegacySessionFile(StateDir);
            await File.WriteAllTextAsync(legacyFile, "{\"legacy\":true}", Ct);

            var terminal = AwaitTerminalTransitionAsync(bound);
            bound.SendMessage("hello");
            await terminal;

            Assert.False(File.Exists(perUserFile));
            Assert.True(File.Exists(legacyFile));
        }
    }

    /// <summary>
    /// THE CONSTRUCTOR-INSTALLED STREAMING-CALLBACK DELETE FOLLOWS THE IDENTITY TOO: the
    /// streaming service the Composer constructs deletes the LEGACY file for a legacy Composer
    /// and the PER-USER file for an identity-bound Composer.
    /// </summary>
    [Fact]
    public async Task StreamingCallbackDelete_FollowsTheIdentityOfTheComposer()
    {
        var (first, _) = await TwoUsersAsync();

        // ── legacy Composer ──
        using (var db = CopilotHiveDbContext.CreateInMemory())
        {
            var store = new GoalStore(db, NullLogger<GoalStore>.Instance);
            await using var legacy = CreateComposer(store, StateDir, null, null, null, _ => CreateOverflowClient());
            await legacy.ConnectAsync(Ct);
            await legacy.SaveSessionAsync(Ct);

            var legacyFile = LegacySessionFile(StateDir);
            Assert.True(File.Exists(legacyFile));

            var streaming = GetStreamingService(legacy);
            var updates = 0;
            var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            legacy.OnStreamingUpdate += () =>
            {
                Interlocked.Increment(ref updates);
                terminal.TrySetResult();
            };

            streaming.SendMessage("hello");
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

            // The client yields nothing, so the terminal update is the ONLY one.
            Assert.Equal(1, Volatile.Read(ref updates));
            Assert.False(File.Exists(legacyFile));
        }

        // ── identity-bound Composer ──
        using (var db = CopilotHiveDbContext.CreateInMemory())
        {
            var store = new GoalStore(db, NullLogger<GoalStore>.Instance);
            await using var bound = CreateComposer(store, StateDir, first, null, null, _ => CreateOverflowClient());
            await bound.ConnectAsync(Ct);
            await bound.SaveSessionAsync(Ct);

            var perUserFile = PerUserSessionFile(StateDir, first);
            Assert.True(File.Exists(perUserFile));

            var streaming = GetStreamingService(bound);
            var updates = 0;
            var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bound.OnStreamingUpdate += () =>
            {
                Interlocked.Increment(ref updates);
                terminal.TrySetResult();
            };

            streaming.SendMessage("hello");
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

            Assert.Equal(1, Volatile.Read(ref updates));
            Assert.False(File.Exists(perUserFile));
        }
    }

    /// <summary>
    /// A RESET DELETES ONLY THE OWNING IDENTITY'S SESSION FILE: resetting user 1 leaves user 2's
    /// per-user session file (and the legacy file) untouched.
    /// </summary>
    [Fact]
    public async Task ResetSession_DeletesOnlyTheOwningIdentitysSessionFile()
    {
        var (first, second) = await TwoUsersAsync();
        using var db = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(db, NullLogger<GoalStore>.Instance);

        var otherUsersFile = PerUserSessionFile(StateDir, second);
        Directory.CreateDirectory(Path.GetDirectoryName(otherUsersFile)!);
        await File.WriteAllTextAsync(otherUsersFile, "{\"other\":\"user\"}", Ct);
        var legacyFile = LegacySessionFile(StateDir);
        await File.WriteAllTextAsync(legacyFile, "{\"legacy\":true}", Ct);

        await using var composer = CreateComposer(store, StateDir, first, null);
        await composer.ConnectAsync(Ct);
        await composer.SaveSessionAsync(Ct);

        var perUserFile = PerUserSessionFile(StateDir, first);
        Assert.True(File.Exists(perUserFile));

        await composer.ResetSessionAsync(Ct);

        Assert.False(File.Exists(perUserFile));
        Assert.True(File.Exists(otherUsersFile));
        Assert.True(File.Exists(legacyFile));
    }
}

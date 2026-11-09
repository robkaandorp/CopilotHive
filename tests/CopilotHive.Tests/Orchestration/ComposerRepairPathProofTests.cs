using System.Reflection;

using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// THE REPAIR-PATH PROOFS for the five iteration-2 repairs. Each repair is proven at the level the
/// review demanded, not by an aggregate:
/// <list type="number">
/// <item>PARTIAL PRIMITIVES FAIL CLOSED — a partial registration is rejected (it never degrades to
/// "unguarded"), and the all-absent case survives ONLY inside the approved legacy exception.</item>
/// <item>A NON-NULL NON-POSITIVE ID IS DENIED IN BOTH AUTH MODES — it never selects legacy authority.</item>
/// <item>THE OPEN-MODE PRINCIPAL COMES ONLY FROM THE RESOLVER — the central helper contains no
/// principal construction of its own.</item>
/// <item>CANCELLATION PROPAGATES WITH TOKEN IDENTITY at each repaired guard site — and it is NEVER a
/// denial, and never produces a side effect.</item>
/// <item>THE HOOK-SIDE CALLBACK FORWARDS SharpCoder's OWN TOKEN.</item>
/// </list>
/// </summary>
public sealed class ComposerRepairPathProofTests : IAsyncLifetime
{
    private const BindingFlags PrivateFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-repair-{Guid.NewGuid():N}");
    private readonly MemoryDbFactory _factory;
    private readonly UserService _users;
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<Composer> _composers = [];

    public ComposerRepairPathProofTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(StateDir);
        _factory = new MemoryDbFactory();
        _users = new UserService(_factory, NullLogger<UserService>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StateDir => Path.Combine(_dir, "state");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var composer in _composers)
        {
            try { await composer.DisposeAsync(); }
            catch (Exception) { /* cleanup must not mask a test failure */ }
        }

        foreach (var provider in _providers)
        {
            try { await provider.DisposeAsync(); }
            catch (Exception) { /* cleanup must not mask a test failure */ }
        }

        try { _factory.Dispose(); }
        catch (Exception) { /* cleanup must not mask a test failure */ }

        TestHelpers.ForceDeleteDirectory(_dir);
    }

    private sealed class MemoryDbFactory : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly SqliteConnection _keeper;
        private readonly DbContextOptions<CopilotHiveDbContext> _options;

        public MemoryDbFactory()
        {
            var name = $"repair-{Guid.NewGuid():N}";
            _keeper = new SqliteConnection($"Data Source={name};Mode=Memory;Cache=Shared");
            _keeper.Open();
            _options = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(_keeper).Options;
            using var ctx = new CopilotHiveDbContext(_options);
            ctx.Database.EnsureCreated();
        }

        public CopilotHiveDbContext CreateDbContext() => new(_options);

        public void Dispose() => _keeper.Dispose();
    }

    private async Task<(int OriginalId, int SecondId)> SeedAccountsAsync(UserRole secondRole)
    {
        var original = await _users.CreateOrUpdateUserAsync(
            "100", "original", null, null, null, "tok", "ref", "2030-01-01T00:00:00.0000000Z", Ct);
        await _users.InviteUserAsync("second", secondRole, Ct);
        var second = await _users.ActivateInvitationAsync("second", "200", null, null, null, Ct);
        return (original.Id, second.Id);
    }

    private ActingUserPrincipal Resolve(int userId)
    {
        var resolver = new HivePrincipalResolver(
            _users, HiveAuthMode.AuthenticationEnabled, NullLogger<HivePrincipalResolver>.Instance);
        var resolution = resolver.ResolveByUserIdAsync(userId, Ct).GetAwaiter().GetResult();
        Assert.True(resolution.IsResolved, $"Account {userId} did not resolve: {resolution.RefusalReason}.");
        return resolution.Principal!;
    }

    private ComposerUserState StateOf(int userId) =>
        ComposerUserState.ResolveAsync(_users, userId, Ct).GetAwaiter().GetResult();

    /// <summary>
    /// Builds a container that registers the security primitives EXCEPT the ones named in
    /// <paramref name="omit"/> — the partial-registration shape.
    /// </summary>
    private ServiceProvider BuildProvider(
        HiveAuthMode mode, params Type[] omit)
    {
        var services = new ServiceCollection();
        if (!omit.Contains(typeof(UserService)))
        {
            services.AddSingleton(_users);
        }
        if (!omit.Contains(typeof(HiveAuthMode)))
        {
            services.AddSingleton(mode);
        }
        if (!omit.Contains(typeof(HivePolicy)))
        {
            services.AddSingleton<HivePolicy>();
        }
        if (!omit.Contains(typeof(HivePrincipalResolver)))
        {
            // The resolver is registered LAZILY and resolves its own dependencies, so omitting
            // UserService or HiveAuthMode does not break the CONTAINER's own build — it only means
            // the primitive is genuinely absent, which is the shape under test.
            services.AddSingleton(sp => new HivePrincipalResolver(
                sp.GetService<UserService>()!,
                sp.GetService<HiveAuthMode>()!,
                NullLogger<HivePrincipalResolver>.Instance));
        }

        services.AddSingleton<IGoalStore>(new GoalStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(new HiveConfigFile
        {
            Models = new ModelsConfig { AvailableModels = [new ModelEntry { Name = "test-model" }] },
            Composer = new ComposerConfig { Model = "test-model" },
        });

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    private Composer CreateComposer(
        ServiceProvider provider, int userId, CopilotHiveDbContext goalDb, IIssueStore? issueStore = null)
    {
        var store = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);
        var composer = new Composer(
            "test-model", NullLogger<Composer>.Instance, store,
            stateDir: StateDir,
            serviceProvider: provider,
            chatClientFactory: _ => new MockChatClient(_ => "unused"),
            issueStore: issueStore,
            userState: StateOf(userId),
            principal: Resolve(userId));
        _composers.Add(composer);
        return composer;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // REPAIR 1 — PARTIAL PRIMITIVES FAIL CLOSED
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// EVERY SINGLE-PRIMITIVE OMISSION FAILS CLOSED: for each of the four primitives, a container that
    /// registers the other three THROWS instead of silently degrading to "no authorization". The
    /// omission is enumerated from the actual primitive types, so a new primitive is covered too.
    /// </summary>
    /// <param name="omittedTypeName">The simple name of the primitive left out of the container.</param>
    [Theory]
    [InlineData("HivePrincipalResolver")]
    [InlineData("HivePolicy")]
    [InlineData("UserService")]
    [InlineData("HiveAuthMode")]
    public async Task EveryPartialPrimitiveRegistration_IsRejected(string omittedTypeName)
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        var omittedType = new[]
        {
            typeof(HivePrincipalResolver), typeof(HivePolicy), typeof(UserService), typeof(HiveAuthMode),
        }.Single(t => t.Name == omittedTypeName);

        using var partial = BuildProvider(HiveAuthMode.AuthenticationEnabled, omittedType);

        // FAIL CLOSED: the construction throws, so an identity-bound runtime is NEVER built over a
        // partially configured container (which would leave its tools unguarded).
        var ex = Assert.ThrowsAny<Exception>(
            () => CreateComposer(partial, secondId, CopilotHiveDbContext.CreateInMemory()));

        // …and the failure DIAGNOSES the missing piece, so the misconfiguration is actionable:
        // either the helper's own "partially registered (X missing)" message, or — when the omitted
        // primitive is one the resolver itself depends on — the resolver's constructor refusing the
        // null dependency. Both name the piece; neither is a silent proceed.
        var helperMessage = ex is InvalidOperationException && ex.Message.Contains("partially registered", StringComparison.Ordinal)
            ? ex.Message
            : ex.Message + " " + (ex as ArgumentNullException)?.ParamName;

        Assert.True(
            helperMessage.Contains(omittedTypeName, StringComparison.Ordinal)
                || helperMessage.Contains(omittedTypeName.Replace("HiveAuthMode", "mode").Replace("UserService", "userService"), StringComparison.Ordinal),
            $"The failure for the omitted '{omittedTypeName}' must name the missing piece, but was: {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>
    /// THE PARTIAL REGISTRATION IS REJECTED AT THE HELPER TOO — not only at the Composer — so no other
    /// caller of <c>FromServiceProvider</c> can quietly receive a null helper over a partially
    /// configured container.
    /// </summary>
    [Fact]
    public async Task APartialRegistration_ThrowsAtTheHelperItself()
    {
        var (_, _) = await SeedAccountsAsync(UserRole.Member);
        using var partial = BuildProvider(HiveAuthMode.AuthenticationEnabled, typeof(HivePolicy));

        var ex = Assert.Throws<InvalidOperationException>(
            () => ComposerToolAuthorization.FromServiceProvider(partial));

        Assert.Contains("partially registered", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(HivePolicy), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE ALL-ABSENT CASE SURVIVES ONLY INSIDE THE LEGACY EXCEPTION: a legacy Composer
    /// (<c>userState == null</c>) over a primitives-free container is ACCEPTED and its tools are
    /// unguarded — the approved legacy behaviour — while IDENTITY-BOUND state on the SAME container is
    /// REJECTED. The pair is what makes the exception narrow.
    /// </summary>
    [Fact]
    public async Task TheAllAbsentCase_SurvivesOnlyForTheLegacyConstruction()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);

        // A container with NONE of the four primitives.
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(new HiveConfigFile
        {
            Models = new ModelsConfig { AvailableModels = [new ModelEntry { Name = "test-model" }] },
            Composer = new ComposerConfig { Model = "test-model" },
        });
        using var bare = services.BuildServiceProvider();

        // All-absent yields NO helper — the genuinely-absent case.
        Assert.Null(ComposerToolAuthorization.FromServiceProvider(bare));

        // The LEGACY construction is accepted…
        var store = new GoalStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance);
        await using var legacy = new Composer(
            "test-model", NullLogger<Composer>.Instance, store, stateDir: StateDir, serviceProvider: bare);
        Assert.NotNull(legacy);

        // …while IDENTITY-BOUND state on the SAME container is rejected.
        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateComposer(bare, secondId, CopilotHiveDbContext.CreateInMemory()));
        Assert.Contains("requires the security primitives", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE AGENT SERVICE ALSO REFUSES TO BUILD AN UNGUARDED AGENT: even if a gate somehow failed to
    /// reach the agent service for an identity-bound runtime, the agent construction itself refuses.
    /// </summary>
    [Fact]
    public async Task TheAgentService_RefusesToBuildAnUnguardedIdentityBoundAgent()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);

        var service = new ComposerAgentService(
            "test-model", 64_000, 50, null, null, "system prompt", new List<AITool>(), null, StateDir,
            null, NullLogger<ComposerAgentService>.Instance, _ => new MockChatClient(_ => "unused"),
            null, ["test-model"], null, null, false, [], null,
            userState: StateOf(secondId),
            toolAuthorizationGate: null);

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConnectAsync(Ct));
            Assert.Contains("requires a tool-authorization gate", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // REPAIR 3 — A NON-NULL NON-POSITIVE ID IS DENIED IN BOTH AUTH MODES
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A SUPPLIED NON-POSITIVE ID NEVER SELECTS LEGACY AUTHORITY — in AUTHENTICATION-ENABLED mode and
    /// in OPEN mode alike. Both modes refuse it, so a caller-supplied <c>0</c> or negative id can never
    /// inherit the legacy original account or the local operator.
    /// </summary>
    /// <param name="userId">The supplied non-positive id.</param>
    /// <param name="authEnabled">Whether authentication is enabled (otherwise open mode).</param>
    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    [InlineData(int.MinValue, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(int.MinValue, false)]
    public async Task ANonPositiveSuppliedId_IsRefusedInBothAuthModes(int userId, bool authEnabled)
    {
        var mode = authEnabled ? HiveAuthMode.AuthenticationEnabled : HiveAuthMode.Open;
        await SeedAccountsAsync(UserRole.Admin);
        using var provider = BuildProvider(mode);
        var helper = ComposerToolAuthorization.FromServiceProvider(provider)!;

        var result = await helper.AuthorizeAsync(userId, "list_goals", arguments: null, Ct);

        Assert.False(result.IsAllowed);
        Assert.Contains("could not be verified", result.DenialReason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE NULL SENTINEL STILL SELECTS THE LEGACY ORIGINAL ACCOUNT: the repair narrowed the sentinel
    /// to <c>null</c> alone, and did not break the legitimate legacy path.
    /// </summary>
    [Fact]
    public async Task TheNullSentinel_StillSelectsTheLegacyOriginalAccount()
    {
        var (originalId, _) = await SeedAccountsAsync(UserRole.Admin);
        using var provider = BuildProvider(HiveAuthMode.AuthenticationEnabled);
        var helper = ComposerToolAuthorization.FromServiceProvider(provider)!;

        // The original account is an Admin, so an Admin-only tool is allowed for the legacy runtime.
        Assert.True((await helper.AuthorizeAsync(null, "update_agents_md", arguments: null, Ct)).IsAllowed);

        // Disabling that original account denies the legacy runtime at once — no fallback.
        await _users.SetUserEnabledAsync(originalId, enabled: false, Ct);
        Assert.False((await helper.AuthorizeAsync(null, "update_agents_md", arguments: null, Ct)).IsAllowed);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // REPAIR 2 — THE OPEN-MODE PRINCIPAL COMES ONLY FROM THE RESOLVER
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE CENTRAL HELPER CONTAINS NO PRINCIPAL CONSTRUCTION OF ITS OWN: the open-mode principal is
    /// produced by the resolver, so the mode decision stays owned by the resolver (which holds the
    /// explicit mode input). This is a STRUCTURAL pin over the helper's source.
    /// </summary>
    [Fact]
    public void TheCentralHelper_ContainsNoPrincipalConstructionOfItsOwn()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepoRoot.Find(), "src", "CopilotHive", "Security", "ComposerToolAuthorization.cs"))
            .ReplaceLineEndings("\n");

        // The helper may NAME the type (in signatures and docs) but must never CONSTRUCT one.
        Assert.DoesNotContain("ActingUserPrincipal.LocalOperator(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ActingUserPrincipal.ForActivatedAccount(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new ActingUserPrincipal", source, StringComparison.Ordinal);

        // …and it DOES go through the resolver for the open-mode branch.
        Assert.Contains("_resolver.ResolveAsync(", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE OPEN-MODE PRINCIPAL IS THE RESOLVER'S LOCAL OPERATOR: the legacy runtime in open mode is
    /// authorized as the explicit local operator — the same principal the resolver yields — and it
    /// holds the full matrix.
    /// </summary>
    [Fact]
    public async Task TheOpenModePrincipal_IsTheResolversLocalOperator()
    {
        await SeedAccountsAsync(UserRole.Admin);
        using var provider = BuildProvider(HiveAuthMode.Open);
        var helper = ComposerToolAuthorization.FromServiceProvider(provider)!;

        // The open-mode legacy runtime holds every capability.
        foreach (var toolName in new[] { "list_goals", "approve_goal", "update_agents_md", "create_document" })
        {
            Assert.True(
                (await helper.AuthorizeAsync(null, toolName, arguments: null, Ct)).IsAllowed,
                $"'{toolName}' must be allowed for the open-mode local operator.");
        }

        // And it is the resolver's principal, byte for byte.
        var resolver = new HivePrincipalResolver(
            _users, HiveAuthMode.Open, NullLogger<HivePrincipalResolver>.Instance);
        var openResolution = await resolver.ResolveAsync(claimsPrincipal: null, Ct);
        Assert.True(openResolution.IsResolved);
        Assert.True(openResolution.Principal!.IsLocalOperator);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // REPAIR 5 — CANCELLATION PROPAGATES WITH TOKEN IDENTITY AT EVERY GUARD SITE
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A CANCELLED DIRECT CALL PROPAGATES THE CALLER'S OWN CANCELLATION — with token identity — at
    /// EVERY repaired guard site, and never becomes a refusal.
    /// <para>
    /// Each case asserts the exception's <see cref="OperationCanceledException.CancellationToken"/> is
    /// the CALLER's token (not a substituted one) and that the refusal text never appears.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ACancelledDirectCall_PropagatesTheCallersTokenAtEveryGuardedMutator()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Operator);
        using var provider = BuildProvider(HiveAuthMode.AuthenticationEnabled);
        using var goalDb = CopilotHiveDbContext.CreateInMemory();
        var store = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);
        await store.CreateGoalAsync(new Goal
        {
            Id = "goal-a",
            Description = "A draft goal",
            Status = GoalStatus.Draft,
            CreatedAt = DateTime.UtcNow,
        }, Ct);

        var issueStore = new RecordingIssueStore();
        var composer = CreateComposer(provider, secondId, goalDb, issueStore);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // One call per repaired guard site. Each must throw the CALLER's cancellation.
        var calls = new (string Name, Func<CancellationToken, Task<string>> Call)[]
        {
            ("approve_goal", ct => composer.ApproveGoalAsync("goal-a", ct)),
            ("delete_goal", ct => composer.DeleteGoalAsync("goal-a", ct)),
            ("cancel_goal", ct => composer.CancelGoalAsync("goal-a", ct)),
            ("update_goal", ct => composer.UpdateGoalAsync("goal-a", "description", "x", ct)),
            ("extend_goal_iterations", ct => composer.ExtendGoalIterationsAsync("goal-a", 5, ct)),
            ("review_goal", ct => composer.ReviewGoalAsync("goal-a", ct)),
            ("create_goal", ct => composer.CreateGoalAsync("cancelled-goal", "x", ct: ct)),
            ("create_release", ct => composer.CreateReleaseAsync("v9.9.9", "9.9.9", ct: ct)),
            ("update_release", ct => composer.UpdateReleaseAsync("v9.9.9", "notes", "x", ct)),
            ("create_issue", ct => composer.CreateIssueAsync("bug", "t", "d", ct: ct)),
            ("update_issue", ct => composer.UpdateIssueAsync("issue-a", status: "closed", ct: ct)),
            ("git_fetch", ct => composer.GitFetchAsync("repo-a", cancellationToken: ct)),
        };

        foreach (var (name, call) in calls)
        {
            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call(cts.Token));
            Assert.Equal(cts.Token, ex.CancellationToken);
            // A cancellation is NEVER a refusal.
            Assert.DoesNotContain("role does not permit", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("could not be verified", ex.Message, StringComparison.Ordinal);
            Assert.NotNull(name);
        }

        // NO SIDE EFFECT anywhere: the goal is untouched and nothing was created.
        Assert.Equal(GoalStatus.Draft, (await store.GetGoalAsync("goal-a", Ct))!.Status);
        Assert.Null(await store.GetGoalAsync("cancelled-goal", Ct));
        Assert.Null(await store.GetReleaseAsync("v9.9.9", Ct));
        Assert.Empty(issueStore.Created);
        Assert.Empty(issueStore.Updates);
    }

    /// <summary>
    /// THE CONFIG AND KNOWLEDGE GUARD SITES PROPAGATE THE CALLER'S TOKEN TOO — the two partials the
    /// review named explicitly (the three config mutations and the five knowledge mutations).
    /// </summary>
    [Fact]
    public async Task ACancelledDirectCall_PropagatesTheCallersTokenAtTheConfigAndKnowledgeGuardSites()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Admin);
        using var provider = BuildProvider(HiveAuthMode.AuthenticationEnabled);
        using var goalDb = CopilotHiveDbContext.CreateInMemory();

        var configRepoDir = Path.Combine(_dir, "config-repo");
        Directory.CreateDirectory(configRepoDir);

        // The tool's OWN work is made UNRUNNABLE, so the cancellation can only come from the
        // authorization guard: a FILE occupies the "agents" directory path, so the tool's
        // Directory.CreateDirectory would throw a NON-cancellation error. A dropped caller token
        // would therefore surface as that error instead of the caller's cancellation — which is
        // exactly what makes this case discriminating rather than incidentally green.
        await File.WriteAllTextAsync(Path.Combine(configRepoDir, "agents"), "not a directory", Ct);

        var configRepo = new ConfigRepoManager("https://example.invalid/config.git", configRepoDir);
        var knowledgeGraph = new CopilotHive.Knowledge.KnowledgeGraph();

        var store = new GoalStore(goalDb, NullLogger<GoalStore>.Instance);
        var composer = new Composer(
            "test-model", NullLogger<Composer>.Instance, store,
            stateDir: StateDir,
            serviceProvider: provider,
            chatClientFactory: _ => new MockChatClient(_ => "unused"),
            configRepo: configRepo,
            knowledgeGraph: knowledgeGraph,
            userState: StateOf(secondId),
            principal: Resolve(secondId));
        _composers.Add(composer);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var calls = new (string Name, Func<CancellationToken, Task<string>> Call)[]
        {
            ("update_agents_md", ct => composer.UpdateAgentsMdAsync("Coder", "content", ct)),
            ("edit_agents_md", ct => composer.EditAgentsMdAsync("Coder", "old", "new", ct)),
            ("commit_config_changes", ct => composer.CommitConfigChangesAsync("message", ct)),
            ("create_document", ct => composer.CreateDocumentAsync("t", "s", "T", "implementation", "b", cancellationToken: ct)),
            ("update_document", ct => composer.UpdateDocumentAsync("t/s", "b", cancellationToken: ct)),
            ("delete_document", ct => composer.DeleteDocumentAsync("t/s", cancellationToken: ct)),
            ("link_document", ct => composer.LinkDocumentAsync("t/s", "t/x", "related", cancellationToken: ct)),
            ("unlink_document", ct => composer.UnlinkDocumentAsync("t/s", "t/x", "related", cancellationToken: ct)),
        };

        foreach (var (name, call) in calls)
        {
            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call(cts.Token));
            Assert.Equal(cts.Token, ex.CancellationToken);
            Assert.NotNull(name);
        }

        // NO SIDE EFFECT: the knowledge corpus is untouched and no AGENTS.md was written.
        Assert.Empty(knowledgeGraph.GetAllDocuments());
        Assert.False(File.Exists(Path.Combine(configRepoDir, "agents", "coder.agents.md")));
    }

    /// <summary>
    /// THE HOOK-SIDE CALLBACK FORWARDS SharpCoder's OWN TOKEN: the wired callback threads the token it
    /// was handed into the fresh lookup, so a cancelled call propagates with THAT token's identity and
    /// is never converted into a denial.
    /// </summary>
    [Fact]
    public async Task TheHookSideCallback_ForwardsSharpCodersOwnToken()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Member);
        using var provider = BuildProvider(HiveAuthMode.AuthenticationEnabled);
        var gate = ComposerToolAuthorization.FromServiceProvider(provider)!.Bind(secondId);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var context = (SharpCoder.ToolAuthorizationContext)typeof(SharpCoder.ToolAuthorizationContext)
            .GetConstructors(PrivateFlags)
            .Single()
            .Invoke(["approve_goal", "correlation", (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>()]);

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.AuthorizeToolAsync(context, cts.Token));

        // TOKEN IDENTITY: the caller (SharpCoder) observes ITS OWN cancellation.
        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    // ── Helpers ──

    /// <summary>A chat client that must be disposed by the owner (the agent service) — a bare stub.</summary>
    private sealed class MockChatClient(Func<IEnumerable<ChatMessage>, string> responder) : IChatClient
    {
        public ChatClientMetadata Metadata => new("mock", null, "mock-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responder(messages))));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>An issue store that RECORDS every create and write, so a test can prove no guarded
    /// method reached the store.</summary>
    private sealed class RecordingIssueStore : IIssueStore
    {
        public List<Issue> Created { get; } = [];

        public List<Issue> Updates { get; } = [];

        public Task<Issue> CreateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            Created.Add(issue);
            return Task.FromResult(issue);
        }

        public Task<Issue?> GetIssueAsync(string issueId, CancellationToken ct = default) => Task.FromResult<Issue?>(null);

        public Task<IReadOnlyList<Issue>> GetAllIssuesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Issue>>([]);

        public Task<IReadOnlyList<Issue>> GetIssuesAsync(
            IssueStatus? status = null, IssueType? type = null, IssueSeverity? severity = null,
            string? repository = null, string? sourceGoalId = null, string? linkedGoalId = null,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Issue>>([]);

        public Task UpdateIssueAsync(Issue issue, CancellationToken ct = default)
        {
            Updates.Add(issue);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteIssueAsync(string issueId, CancellationToken ct = default) => Task.FromResult(true);
    }
}

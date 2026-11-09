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
/// FRESHNESS of the Composer authorization boundary: a role change, a disablement, a missing account
/// and an unreadable directory must take effect on the NEXT effective invocation of the SAME runtime —
/// no recreation, no <c>GetOrCreateAsync</c>, no stale cached decision.
/// <para>
/// Two users' runtimes must authorize INDEPENDENTLY: one user's demotion must not deny the other
/// user's calls, and no principal or decision may leak between runtimes.
/// </para>
/// <para>
/// Every answer here comes from the REAL resolver over the REAL user directory, so freshness is
/// proven against current stored state rather than against a stub.
/// </para>
/// </summary>
public sealed class ComposerAuthorizationFreshnessTests : IAsyncLifetime
{
    private const BindingFlags PrivateFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-fresh-{Guid.NewGuid():N}");
    private readonly MemoryDbFactory _factory;
    private readonly UserService _users;
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<Composer> _composers = [];

    public ComposerAuthorizationFreshnessTests()
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
            var name = $"fresh-{Guid.NewGuid():N}";
            _keeper = new SqliteConnection($"Data Source={name};Mode=Memory;Cache=Shared");
            _keeper.Open();
            _options = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(_keeper).Options;
            using var ctx = new CopilotHiveDbContext(_options);
            ctx.Database.EnsureCreated();
        }

        public CopilotHiveDbContext CreateDbContext() => new(_options);

        public void Dispose() => _keeper.Dispose();
    }

    /// <summary>Seeds the original account (id 1) and two activated invited accounts (ids 2 and 3).</summary>
    private async Task<(int OriginalId, int SecondId, int ThirdId)> SeedAccountsAsync(
        UserRole secondRole, UserRole thirdRole)
    {
        var original = await _users.CreateOrUpdateUserAsync(
            "100", "original", null, null, null, "tok", "ref", "2030-01-01T00:00:00.0000000Z", Ct);
        await _users.InviteUserAsync("second", secondRole, Ct);
        var second = await _users.ActivateInvitationAsync("second", "200", null, null, null, Ct);
        await _users.InviteUserAsync("third", thirdRole, Ct);
        var third = await _users.ActivateInvitationAsync("third", "300", null, null, null, Ct);
        return (original.Id, second.Id, third.Id);
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

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_users);
        services.AddSingleton(HiveAuthMode.AuthenticationEnabled);
        services.AddSingleton<HivePolicy>();
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(),
            sp.GetRequiredService<HiveAuthMode>(),
            NullLogger<HivePrincipalResolver>.Instance));
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

    /// <summary>
    /// Builds ONE identity-bound runtime over the fixture's security primitives, bound to
    /// <paramref name="userId"/> — the same runtime instance is reused across every assertion of a
    /// freshness case, so a change that only took effect after a RECREATION cannot pass.
    /// </summary>
    private Composer BuildRuntime(ServiceProvider provider, int userId)
    {
        var store = new GoalStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance);
        var composer = new Composer(
            "test-model", NullLogger<Composer>.Instance, store,
            stateDir: StateDir,
            serviceProvider: provider,
            chatClientFactory: _ => new MockChatClient(_ => "unused"),
            userState: StateOf(userId),
            principal: Resolve(userId));
        _composers.Add(composer);
        return composer;
    }

    private static ComposerToolAuthorizationGate GateOf(Composer composer) =>
        (ComposerToolAuthorizationGate)typeof(Composer)
            .GetField("_toolAuthorizationGate", PrivateFlags)!.GetValue(composer)!;

    // ── (c) Freshness within one runtime ──

    /// <summary>
    /// A DEMOTION takes effect on the next effective invocation of the SAME runtime: the account's role
    /// changes in the directory and the very next call through the runtime's own gate is refused. The
    /// runtime instance is not recreated and the gate is not replaced.
    /// </summary>
    [Fact]
    public async Task ADemotion_TakesEffectOnTheNextInvocationOfTheSameRuntime()
    {
        var (_, secondId, _) = await SeedAccountsAsync(UserRole.Operator, UserRole.Viewer);
        using var provider = BuildProvider();
        var composer = BuildRuntime(provider, secondId);
        var gate = GateOf(composer);

        // Before: the Operator may approve a goal.
        Assert.True((await gate.ToolDenialAsync("approve_goal", Ct)) is null);

        await _users.ChangeUserRoleAsync(secondId, UserRole.Viewer, Ct);

        // After: the SAME gate refuses on the very next invocation — no recreation.
        var denial = await gate.ToolDenialAsync("approve_goal", Ct);
        Assert.NotNull(denial);
        Assert.Contains("approve_goal", denial!, StringComparison.Ordinal);
        // The gate object is the same one — the runtime was never rebuilt behind the caller's back.
        Assert.Same(gate, GateOf(composer));
    }

    /// <summary>
    /// A PROMOTION takes effect on the next effective invocation of the SAME runtime: a Viewer that is
    /// promoted to Operator may approve a goal on the next call.
    /// </summary>
    [Fact]
    public async Task APromotion_TakesEffectOnTheNextInvocationOfTheSameRuntime()
    {
        var (_, secondId, _) = await SeedAccountsAsync(UserRole.Viewer, UserRole.Viewer);
        using var provider = BuildProvider();
        var composer = BuildRuntime(provider, secondId);
        var gate = GateOf(composer);

        Assert.NotNull(await gate.ToolDenialAsync("approve_goal", Ct));

        await _users.ChangeUserRoleAsync(secondId, UserRole.Operator, Ct);

        Assert.Null(await gate.ToolDenialAsync("approve_goal", Ct));
    }

    /// <summary>
    /// A DISABLEMENT takes effect on the next effective invocation of the SAME runtime: the account is
    /// disabled and every capability — including one it held a moment earlier — is refused at once.
    /// </summary>
    [Fact]
    public async Task ADisablement_TakesEffectOnTheNextInvocationOfTheSameRuntime()
    {
        var (_, secondId, _) = await SeedAccountsAsync(UserRole.Admin, UserRole.Viewer);
        using var provider = BuildProvider();
        var composer = BuildRuntime(provider, secondId);
        var gate = GateOf(composer);

        Assert.Null(await gate.ToolDenialAsync("update_agents_md", Ct));

        await _users.SetUserEnabledAsync(secondId, enabled: false, Ct);

        Assert.NotNull(await gate.ToolDenialAsync("update_agents_md", Ct));
        // Even a read is refused: a disabled account holds nothing.
        Assert.NotNull(await gate.ToolDenialAsync("list_goals", Ct));
    }

    /// <summary>
    /// A MISSING ACCOUNT takes effect on the next effective invocation of the SAME runtime: the account
    /// row is deleted and the runtime's calls are refused — never silently re-authorized as somebody
    /// else.
    /// </summary>
    [Fact]
    public async Task AMissingAccount_TakesEffectOnTheNextInvocationOfTheSameRuntime()
    {
        var (_, secondId, _) = await SeedAccountsAsync(UserRole.Admin, UserRole.Viewer);
        using var provider = BuildProvider();
        var composer = BuildRuntime(provider, secondId);
        var gate = GateOf(composer);

        Assert.Null(await gate.ToolDenialAsync("update_agents_md", Ct));

        await DeleteAccountAsync(secondId);

        Assert.NotNull(await gate.ToolDenialAsync("update_agents_md", Ct));
    }

    /// <summary>
    /// AN UNKNOWN PERSISTED ROLE takes effect on the next effective invocation of the SAME runtime: the
    /// row's role text is replaced with an unsupported value and the runtime's calls are refused —
    /// never defaulted to a role that would authorize them.
    /// </summary>
    [Fact]
    public async Task AnUnknownPersistedRole_TakesEffectOnTheNextInvocationOfTheSameRuntime()
    {
        var (_, secondId, _) = await SeedAccountsAsync(UserRole.Admin, UserRole.Viewer);
        using var provider = BuildProvider();
        var composer = BuildRuntime(provider, secondId);
        var gate = GateOf(composer);

        Assert.Null(await gate.ToolDenialAsync("update_agents_md", Ct));

        await SetUnsupportedRoleAsync(secondId, "superuser");

        Assert.NotNull(await gate.ToolDenialAsync("update_agents_md", Ct));
    }

    /// <summary>
    /// A DIRECTORY FAILURE takes effect on the next effective invocation of the SAME runtime: the
    /// directory becomes unreadable and the runtime's calls are refused (fail closed) rather than
    /// allowed because "the lookup failed".
    /// </summary>
    [Fact]
    public async Task ADirectoryFailure_TakesEffectOnTheNextInvocationOfTheSameRuntime()
    {
        var (_, secondId, _) = await SeedAccountsAsync(UserRole.Admin, UserRole.Viewer);
        using var provider = BuildProvider();
        var composer = BuildRuntime(provider, secondId);
        var gate = GateOf(composer);

        Assert.Null(await gate.ToolDenialAsync("update_agents_md", Ct));

        // The directory is gone: every later read fails.
        _factory.Dispose();

        Assert.NotNull(await gate.ToolDenialAsync("update_agents_md", Ct));
    }

    // ── (c) Two users' runtimes authorize independently ──

    /// <summary>
    /// TWO RUNTIMES AUTHORIZE INDEPENDENTLY: the second user's demotion denies the second runtime
    /// while the first runtime keeps its own authority — no principal, decision or cache is shared
    /// between them.
    /// </summary>
    [Fact]
    public async Task TwoRuntimes_AuthorizeIndependently_WithoutCrossRuntimeLeakage()
    {
        var (_, secondId, thirdId) = await SeedAccountsAsync(UserRole.Operator, UserRole.Operator);
        using var provider = BuildProvider();

        var firstRuntime = BuildRuntime(provider, secondId);
        var secondRuntime = BuildRuntime(provider, thirdId);
        var firstGate = GateOf(firstRuntime);
        var secondGate = GateOf(secondRuntime);

        // The gates are distinct objects bound to distinct accounts.
        Assert.NotSame(firstGate, secondGate);
        Assert.Equal(secondId, firstGate.UserId);
        Assert.Equal(thirdId, secondGate.UserId);

        // Both start with the same authority.
        Assert.Null(await firstGate.ToolDenialAsync("approve_goal", Ct));
        Assert.Null(await secondGate.ToolDenialAsync("approve_goal", Ct));

        // The SECOND user is demoted: only the SECOND runtime is affected.
        await _users.ChangeUserRoleAsync(thirdId, UserRole.Viewer, Ct);

        Assert.Null(await firstGate.ToolDenialAsync("approve_goal", Ct));
        Assert.NotNull(await secondGate.ToolDenialAsync("approve_goal", Ct));

        // …and the reverse: the FIRST user is demoted, the SECOND runtime is unaffected.
        await _users.ChangeUserRoleAsync(secondId, UserRole.Viewer, Ct);
        Assert.NotNull(await firstGate.ToolDenialAsync("approve_goal", Ct));
        Assert.NotNull(await secondGate.ToolDenialAsync("approve_goal", Ct));

        // The first user is promoted back: only the FIRST runtime recovers.
        await _users.ChangeUserRoleAsync(secondId, UserRole.Operator, Ct);
        Assert.Null(await firstGate.ToolDenialAsync("approve_goal", Ct));
        Assert.NotNull(await secondGate.ToolDenialAsync("approve_goal", Ct));
    }

    /// <summary>
    /// A DISABLED FIRST USER does not disable the SECOND runtime: the disabled state is per account, so
    /// the two runtimes never share an authorization outcome.
    /// </summary>
    [Fact]
    public async Task DisablingOneUser_DoesNotDisableTheOtherRuntimesAuthorization()
    {
        var (_, secondId, thirdId) = await SeedAccountsAsync(UserRole.Admin, UserRole.Admin);
        using var provider = BuildProvider();

        var firstGate = GateOf(BuildRuntime(provider, secondId));
        var secondGate = GateOf(BuildRuntime(provider, thirdId));

        await _users.SetUserEnabledAsync(secondId, enabled: false, Ct);

        Assert.NotNull(await firstGate.ToolDenialAsync("update_agents_md", Ct));
        Assert.Null(await secondGate.ToolDenialAsync("update_agents_md", Ct));
    }

    /// <summary>
    /// TWO CONCURRENT RUNTIMES STAY INDEPENDENT: the two accounts hold DIFFERENT roles, both runtimes
    /// are queried concurrently, and every answer must correspond to its OWN account's stored role. A
    /// shared decision cache (or a principal that leaked between runtimes) would make one runtime
    /// answer with the other's role.
    /// </summary>
    [Fact]
    public async Task ConcurrentQueries_AcrossTwoRuntimes_NeverLeakOneUsersDecisionIntoTheOther()
    {
        var (_, secondId, thirdId) = await SeedAccountsAsync(UserRole.Operator, UserRole.Viewer);
        using var provider = BuildProvider();

        var firstGate = GateOf(BuildRuntime(provider, secondId));
        var secondGate = GateOf(BuildRuntime(provider, thirdId));

        // The Operator may approve a goal; the Viewer may not. Both runtimes are queried at the same
        // time, repeatedly, so a shared cache would have to be wrong on one of the two.
        var firstQueries = Enumerable.Range(0, 25)
            .Select(_ => firstGate.ToolDenialAsync("approve_goal", Ct)).ToArray();
        var secondQueries = Enumerable.Range(0, 25)
            .Select(_ => secondGate.ToolDenialAsync("approve_goal", Ct)).ToArray();

        var firstResults = await Task.WhenAll(firstQueries);
        var secondResults = await Task.WhenAll(secondQueries);

        // Every answer belongs to its OWN account's role.
        Assert.All(firstResults, denial => Assert.Null(denial));
        Assert.All(secondResults, denial => Assert.NotNull(denial));

        // …and the roles are swapped: now the SECOND runtime is the Operator. The answers swap with
        // them, which a latched decision could not do.
        await _users.ChangeUserRoleAsync(thirdId, UserRole.Operator, Ct);
        await _users.ChangeUserRoleAsync(secondId, UserRole.Viewer, Ct);

        var firstAfter = await Task.WhenAll(
            Enumerable.Range(0, 25).Select(_ => firstGate.ToolDenialAsync("approve_goal", Ct)).ToArray());
        var secondAfter = await Task.WhenAll(
            Enumerable.Range(0, 25).Select(_ => secondGate.ToolDenialAsync("approve_goal", Ct)).ToArray());

        Assert.All(firstAfter, denial => Assert.NotNull(denial));
        Assert.All(secondAfter, denial => Assert.Null(denial));
    }

    private async Task DeleteAccountAsync(int userId)
    {
        await using var db = _factory.CreateDbContext();
        var entity = await db.Users.SingleAsync(u => u.Id == userId, Ct);
        db.Users.Remove(entity);
        await db.SaveChangesAsync(Ct);
    }

    private async Task SetUnsupportedRoleAsync(int userId, string roleText)
    {
        await using var db = _factory.CreateDbContext();
        var githubId = await db.Users.Where(u => u.Id == userId).Select(u => u.GitHubId).SingleAsync(Ct);
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "users" SET "role" = {0} WHERE "id" = {1}
            """,
            roleText,
            userId);
        _ = githubId;
    }

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
}

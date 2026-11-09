using System.Reflection;

using CopilotHive.Configuration;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// IDENTITY-BOUND PRINCIPAL-MATCH ENFORCEMENT in the manager/factory/Composer construction seam.
/// <para>
/// An identity-bound runtime (<c>userState != null</c>) REQUIRES the resolver-produced acting principal
/// of the SAME account. Every failure mode must be a hard refusal — there is NO legacy-admin fallback
/// and no silent proceed, so an identity-bound Composer can never run with somebody else's authority
/// (or with none at all).
/// </para>
/// <para>
/// The LEGACY exception is narrow and is asserted here too: only a legacy Composer
/// (<c>userState == null</c>) may omit the principal — and that one re-resolves the ORIGINAL account
/// per invocation while authentication is enabled, or is the explicit local operator in open mode.
/// </para>
/// </summary>
public sealed class ComposerIdentityBoundPrincipalTests : IAsyncLifetime
{
    private const BindingFlags PrivateFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-identity-{Guid.NewGuid():N}");
    private readonly MemoryDbFactory _factory;
    private readonly UserService _users;
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<Composer> _composers = [];

    public ComposerIdentityBoundPrincipalTests()
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

    // ── Directory fixture ──

    private sealed class MemoryDbFactory : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly SqliteConnection _keeper;
        private readonly DbContextOptions<CopilotHiveDbContext> _options;

        public MemoryDbFactory()
        {
            var name = $"identity-{Guid.NewGuid():N}";
            _keeper = new SqliteConnection($"Data Source={name};Mode=Memory;Cache=Shared");
            _keeper.Open();
            _options = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(_keeper).Options;
            using var ctx = new CopilotHiveDbContext(_options);
            ctx.Database.EnsureCreated();
        }

        public CopilotHiveDbContext CreateDbContext() => new(_options);

        public void Dispose() => _keeper.Dispose();
    }

    /// <summary>Seeds the original account (id 1) and one activated invited account (id 2).</summary>
    private async Task<(int OriginalId, int SecondId)> SeedAccountsAsync(UserRole secondRole)
    {
        var original = await _users.CreateOrUpdateUserAsync(
            "100", "original", null, null, null, "tok", "ref", "2030-01-01T00:00:00.0000000Z", Ct);
        await _users.InviteUserAsync("second", secondRole, Ct);
        var second = await _users.ActivateInvitationAsync("second", "200", null, null, null, Ct);
        return (original.Id, second.Id);
    }

    /// <summary>Resolves the RESOLVER-PRODUCED principal of an account from current stored state.</summary>
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

    private ServiceProvider BuildProvider(HiveAuthMode? mode = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_users);
        services.AddSingleton(mode ?? HiveAuthMode.AuthenticationEnabled);
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

    private ComposerToolAuthorization Helper(ServiceProvider provider) =>
        ComposerToolAuthorization.FromServiceProvider(provider)
        ?? throw new InvalidOperationException("The fixture container must expose the security primitives.");

    private Composer CreateComposer(
        ServiceProvider provider, int userId, ActingUserPrincipal? principal,
        CopilotHiveDbContext? goalDb = null)
    {
        var store = new GoalStore(goalDb ?? CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance);
        var composer = new Composer(
            "test-model", NullLogger<Composer>.Instance, store,
            stateDir: StateDir,
            serviceProvider: provider,
            chatClientFactory: _ => new MockChatClient(_ => "unused"),
            userState: StateOf(userId),
            principal: principal);
        _composers.Add(composer);
        return composer;
    }

    // ── (b) Identity-bound principal-match enforcement ──

    /// <summary>
    /// A MATCHING, resolver-produced principal of the SAME account is accepted, and the identity-bound
    /// runtime then authorizes against that account: the gate is bound to the state's own id, never to
    /// another one.
    /// </summary>
    [Fact]
    public async Task IdentityBoundComposer_WithMatchingPrincipal_IsAcceptedAndBoundToItsOwnAccount()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Operator);
        using var provider = BuildProvider();

        var composer = CreateComposer(provider, secondId, Resolve(secondId));

        var gate = Field<ComposerToolAuthorizationGate>(composer, "_toolAuthorizationGate");
        Assert.NotNull(gate);
        Assert.Equal(secondId, gate!.UserId);
        Assert.Equal($"composer-user:{secondId}", gate.CorrelationId);
    }

    /// <summary>
    /// A MISMATCHED principal (a different account) is rejected: one user's state can never be driven
    /// by another user's authority, and there is NO legacy-admin fallback for it.
    /// </summary>
    [Fact]
    public async Task IdentityBoundComposer_WithMismatchedPrincipal_IsRejected()
    {
        var (originalId, secondId) = await SeedAccountsAsync(UserRole.Operator);
        using var provider = BuildProvider();

        var ex = Assert.Throws<ArgumentException>(
            () => CreateComposer(provider, secondId, Resolve(originalId)));

        Assert.Equal("principal", ex.ParamName);
        Assert.Contains("does not match the Composer's identity", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An ABSENT principal for identity-bound state is rejected: there is no legacy-admin fallback and
    /// no silent proceed, so an identity-bound Composer can never run unauthenticated.
    /// </summary>
    [Fact]
    public async Task IdentityBoundComposer_WithAbsentPrincipal_IsRejected()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Operator);
        using var provider = BuildProvider();

        var ex = Assert.Throws<ArgumentException>(() => CreateComposer(provider, secondId, principal: null));

        Assert.Equal("principal", ex.ParamName);
        Assert.Contains("no principal was supplied", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// AN OPEN-MODE LOCAL OPERATOR is rejected for identity-bound state: open mode has no persisted
    /// account, and an identity-bound Composer exists only for one. This is the fourth failure mode
    /// that must NOT fall back to legacy-admin authority.
    /// </summary>
    [Fact]
    public async Task IdentityBoundComposer_WithLocalOperatorPrincipal_IsRejected()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Operator);
        using var provider = BuildProvider();

        var ex = Assert.Throws<ArgumentException>(
            () => CreateComposer(provider, secondId, ActingUserPrincipal.LocalOperator()));

        Assert.Equal("principal", ex.ParamName);
        Assert.Contains("open-mode local operator", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE FACTORY ENFORCES THE SAME RULE: <c>CreateForUser</c> without a principal cannot produce an
    /// identity-bound Composer — the rejection happens at construction, so no partially-built runtime
    /// escapes.
    /// </summary>
    [Fact]
    public async Task Factory_CreateForUser_WithoutPrincipal_RefusesToBuildAnIdentityBoundComposer()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Operator);
        using var provider = BuildProvider();
        var factory = new ComposerFactory(provider, StateDir, ollamaApiKey: null);
        var state = StateOf(secondId);
        var attachments = factory.CreateUserAttachments(state);

        Assert.Throws<ArgumentException>(() => factory.CreateForUser(state, attachments, subscriber: null));
    }

    /// <summary>
    /// THE FACTORY ACCEPTS THE MATCHING PRINCIPAL: the resolver-produced principal of the SAME account
    /// threads through the factory into the Composer, and the resulting runtime's gate is bound to that
    /// account.
    /// </summary>
    [Fact]
    public async Task Factory_CreateForUser_WithMatchingPrincipal_BuildsTheIdentityBoundRuntime()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Operator);
        using var provider = BuildProvider();
        var factory = new ComposerFactory(provider, StateDir, ollamaApiKey: null);
        var state = StateOf(secondId);
        var attachments = factory.CreateUserAttachments(state);

        var build = factory.CreateForUser(state, attachments, subscriber: null, Resolve(secondId));

        _composers.Add(build.Composer);
        var gate = Field<ComposerToolAuthorizationGate>(build.Composer, "_toolAuthorizationGate");
        Assert.NotNull(gate);
        Assert.Equal(secondId, gate!.UserId);
    }

    /// <summary>
    /// THE LEGACY PATH STILL WORKS: a legacy Composer (<c>userState == null</c>) is constructed WITHOUT
    /// a principal — the only place an absent principal is legitimate — and its gate re-resolves the
    /// ORIGINAL account per invocation while authentication is enabled.
    /// </summary>
    [Fact]
    public async Task LegacyComposer_NeedsNoPrincipal_AndResolvesTheOriginalAccountPerInvocation()
    {
        var (originalId, _) = await SeedAccountsAsync(UserRole.Admin);
        using var provider = BuildProvider();

        var store = new GoalStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance);
        var legacy = new Composer(
            "test-model", NullLogger<Composer>.Instance, store, stateDir: StateDir, serviceProvider: provider);
        _composers.Add(legacy);

        var gate = Field<ComposerToolAuthorizationGate>(legacy, "_toolAuthorizationGate");
        Assert.NotNull(gate);
        // The legacy runtime has NO fixed account id: it re-resolves per invocation.
        Assert.Null(gate!.UserId);
        Assert.Equal("composer-legacy-original", gate.CorrelationId);

        // …and the account it resolves is the ORIGINAL one, by id, from current stored state.
        var helper = Helper(provider);
        var resolution = await helper.AuthorizeAsync(null, "update_agents_md", arguments: null, Ct);
        Assert.True(resolution.IsAllowed);

        // Disabling that ORIGINAL account denies the legacy runtime at once — no fallback.
        await _users.SetUserEnabledAsync(originalId, enabled: false, Ct);
        var afterDisable = await helper.AuthorizeAsync(null, "update_agents_md", arguments: null, Ct);
        Assert.False(afterDisable.IsAllowed);
    }

    /// <summary>
    /// THE EXPLICIT OPEN/NO-AUTH LOCAL OPERATOR is the ONLY authority that exists without a persisted
    /// account: the legacy runtime in open mode is allowed everything, and — crucially — the
    /// identity-bound path can never reach it (the open branch is unreachable while authentication is
    /// enabled).
    /// </summary>
    [Fact]
    public async Task LegacyComposer_InOpenMode_IsTheExplicitLocalOperator()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Admin);
        using var provider = BuildProvider(HiveAuthMode.Open);

        var store = new GoalStore(CopilotHiveDbContext.CreateInMemory(), NullLogger<GoalStore>.Instance);
        var legacy = new Composer(
            "test-model", NullLogger<Composer>.Instance, store, stateDir: StateDir, serviceProvider: provider);
        _composers.Add(legacy);

        var gate = Field<ComposerToolAuthorizationGate>(legacy, "_toolAuthorizationGate");
        Assert.NotNull(gate);
        Assert.Equal("composer-local-operator", gate!.CorrelationId);

        // The local operator holds the full matrix…
        var helper = Helper(provider);
        Assert.True((await helper.AuthorizeAsync(null, "update_agents_md", arguments: null, Ct)).IsAllowed);

        // …but an IDENTITY-BOUND runtime still cannot borrow it: the local-operator principal is refused.
        Assert.Throws<ArgumentException>(
            () => CreateComposer(provider, secondId, ActingUserPrincipal.LocalOperator()));
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, PrivateFlags)!.GetValue(target)!;

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

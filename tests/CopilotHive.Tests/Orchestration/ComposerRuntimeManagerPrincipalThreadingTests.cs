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
/// THE MANAGER'S IDENTITY THREADING: the per-user runtime manager must carry the RESOLVER-PRODUCED
/// acting principal of the SAME account into the Composer it builds, so the identity-bound Composer
/// never has to fall back to legacy-admin authority.
/// <para>
/// The manager is exercised over the REAL <see cref="UserService"/> and the REAL
/// <see cref="ComposerFactory"/>, so the principal really travels from the manager into the built
/// runtime rather than being asserted at a stub boundary.
/// </para>
/// </summary>
public sealed class ComposerRuntimeManagerPrincipalThreadingTests : IAsyncLifetime
{
    private const BindingFlags PrivateFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-mgrthread-{Guid.NewGuid():N}");
    private readonly MemoryDbFactory _factory;
    private readonly UserService _users;
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<ComposerRuntimeManager> _managers = [];

    public ComposerRuntimeManagerPrincipalThreadingTests()
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
        foreach (var manager in _managers)
        {
            try { await manager.ShutdownAsync(); }
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
            var name = $"mgrthread-{Guid.NewGuid():N}";
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

    /// <summary>The RESOLVER-PRODUCED principal of an account, read from CURRENT stored state.</summary>
    private ActingUserPrincipal Resolve(int userId)
    {
        var resolver = new HivePrincipalResolver(
            _users, HiveAuthMode.AuthenticationEnabled, NullLogger<HivePrincipalResolver>.Instance);
        var resolution = resolver.ResolveByUserIdAsync(userId, Ct).GetAwaiter().GetResult();
        Assert.True(resolution.IsResolved, $"Account {userId} did not resolve: {resolution.RefusalReason}.");
        return resolution.Principal!;
    }

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
        services.AddSingleton<LlmSessionRegistry>();
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
    /// THE MANAGER THREADS THE PRINCIPAL: the runtime the manager hands out carries the
    /// resolver-produced principal of ITS OWN account, and the runtime's authorization gate is bound
    /// to that same account id — so the runtime can never authorize a different account.
    /// </summary>
    [Fact]
    public async Task TheManager_ThreadsTheMatchingPrincipalIntoTheRuntimeItBuilds()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Operator);
        using var provider = BuildProvider();

        var factory = new ComposerFactory(
            provider, StateDir, ollamaApiKey: null, chatClientFactory: _ => new MockChatClient(_ => "unused"));
        var manager = new ComposerRuntimeManager(
            factory, _users, NullLogger<ComposerRuntimeManager>.Instance,
            (composer, ct) => composer.ConnectAsync(ct));
        _managers.Add(manager);

        var principal = Resolve(secondId);
        var result = await manager.GetOrCreateAsync(principal, Ct);

        Assert.True(result.IsGranted);
        var runtime = result.Runtime!;

        // The Composer the manager built carries the principal of THAT account.
        var carried = Field<ActingUserPrincipal>(runtime.Composer, "_principal");
        Assert.NotNull(carried);
        Assert.Equal(secondId, carried!.UserId);
        Assert.Equal(principal.GitHubId, carried.GitHubId);
        Assert.Equal(principal.Role, carried.Role);

        // …and its authorization gate is bound to the same account.
        var gate = Field<ComposerToolAuthorizationGate>(runtime.Composer, "_toolAuthorizationGate");
        Assert.NotNull(gate);
        Assert.Equal(secondId, gate!.UserId);
        Assert.Equal($"composer-user:{secondId}", gate.CorrelationId);
    }

    /// <summary>
    /// A SECOND USER'S RUNTIME CARRIES THE OTHER PRINCIPAL: two manager-built runtimes carry two
    /// different principals and two different gates — one user's authority never reaches the other's
    /// runtime.
    /// </summary>
    [Fact]
    public async Task TheManager_ThreadsEachUsersOwnPrincipalIntoItsOwnRuntime()
    {
        var (_, secondId) = await SeedAccountsAsync(UserRole.Operator);
        await _users.InviteUserAsync("third", UserRole.Viewer, Ct);
        var thirdId = (await _users.ActivateInvitationAsync("third", "300", null, null, null, Ct)).Id;

        using var provider = BuildProvider();

        var factory = new ComposerFactory(
            provider, StateDir, ollamaApiKey: null, chatClientFactory: _ => new MockChatClient(_ => "unused"));
        var manager = new ComposerRuntimeManager(
            factory, _users, NullLogger<ComposerRuntimeManager>.Instance,
            (composer, ct) => composer.ConnectAsync(ct));
        _managers.Add(manager);

        var secondRuntime = (await manager.GetOrCreateAsync(Resolve(secondId), Ct)).Runtime!;
        var thirdRuntime = (await manager.GetOrCreateAsync(Resolve(thirdId), Ct)).Runtime!;

        Assert.NotSame(secondRuntime.Composer, thirdRuntime.Composer);
        Assert.Equal(secondId, Field<ActingUserPrincipal>(secondRuntime.Composer, "_principal")!.UserId);
        Assert.Equal(thirdId, Field<ActingUserPrincipal>(thirdRuntime.Composer, "_principal")!.UserId);
        Assert.Equal(secondId, Field<ComposerToolAuthorizationGate>(secondRuntime.Composer, "_toolAuthorizationGate")!.UserId);
        Assert.Equal(thirdId, Field<ComposerToolAuthorizationGate>(thirdRuntime.Composer, "_toolAuthorizationGate")!.UserId);
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

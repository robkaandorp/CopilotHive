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
using Moq;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Creation / cache / re-check semantics of <see cref="ComposerRuntimeManager"/> and equivalence of
/// the legacy Composer built through the shared <see cref="ComposerFactory"/> seam.
/// </summary>
public sealed class ComposerRuntimeManagerTests : IDisposable
{
    private const string Model = "test-model";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-runtime-mgr-{Guid.NewGuid():N}");
    private readonly FileDbFactory _factory;
    private readonly UserService _users;
    private readonly CountingEventBus _bus = new();
    private readonly CopilotHiveDbContext _goalDb = CopilotHiveDbContext.CreateInMemory();
    private readonly List<ComposerRuntimeManager> _managers = [];

    // Gate hooks for the chat-client factory used by every Composer built in these tests.
    private int _clientCreations;
    private Func<string, IChatClient> _clientFactory;

    public ComposerRuntimeManagerTests()
    {
        Directory.CreateDirectory(StateDir);
        _factory = new FileDbFactory(Path.Combine(_dir, "users.db"));
        _users = new UserService(_factory, NullLogger<UserService>.Instance);
        _clientFactory = _ => new Mock<IChatClient>().Object;
    }

    public void Dispose()
    {
        _goalDb.Dispose();
        SqliteConnection.ClearAllPools();
        TestHelpers.ForceDeleteDirectory(_dir);
    }

    private sealed class FileDbFactory : IDbContextFactory<CopilotHiveDbContext>
    {
        private readonly DbContextOptions<CopilotHiveDbContext> _options;

        public FileDbFactory(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options;
            using var ctx = CreateDbContext();
            ctx.Database.EnsureCreated();
        }

        public CopilotHiveDbContext CreateDbContext() => new(_options);
    }

    private sealed class CountingEventBus : IEventBus
    {
        private event Action<SystemEvent>? Handlers;
        public int SubscriberCount => Handlers?.GetInvocationList().Length ?? 0;

        public event Action<SystemEvent>? OnEvent
        {
            add => Handlers += value;
            remove => Handlers -= value;
        }

        public void Publish(SystemEvent evt) => Handlers?.Invoke(evt);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StateDir => Path.Combine(_dir, "state");

    private ServiceProvider BuildProvider()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig { AvailableModels = [new ModelEntry { Name = Model }] },
            Composer = new ComposerConfig { Model = Model },
        };
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IGoalStore>(new GoalStore(_goalDb, NullLogger<GoalStore>.Instance));
        services.AddSingleton<IEventBus>(_bus);
        services.AddSingleton<LlmSessionRegistry>();
        return services.BuildServiceProvider();
    }

    private ComposerFactory NewFactory(IServiceProvider sp) =>
        new(sp, StateDir, ollamaApiKey: null, chatClientFactory: m =>
        {
            Interlocked.Increment(ref _clientCreations);
            return _clientFactory(m);
        });

    private ComposerRuntimeManager NewManager(IServiceProvider sp)
    {
        var m = new ComposerRuntimeManager(NewFactory(sp), _users, NullLogger<ComposerRuntimeManager>.Instance);
        _managers.Add(m);
        return m;
    }

    /// <summary>Bootstraps the original admin (id 1) and one activated invited Member (id 2).</summary>
    private async Task<(ActingUserPrincipal First, ActingUserPrincipal Second)> TwoUsersAsync()
    {
        var original = await _users.CreateOrUpdateUserAsync(
            "100", "first", null, null, null, "tok", "ref", "2030-01-01T00:00:00.0000000Z", Ct);
        await _users.InviteUserAsync("second", UserRole.Member, Ct);
        var invited = await _users.ActivateInvitationAsync("second", "200", null, null, null, Ct);
        return (ActingUserPrincipal.ForActivatedAccount(original.Id, "100", UserRole.Admin, true, isOriginal: true),
                ActingUserPrincipal.ForActivatedAccount(invited.Id, "200", UserRole.Member, true, isOriginal: false));
    }

    private static async Task<Composer> GrantedComposerAsync(ComposerRuntimeManager manager, ActingUserPrincipal p)
    {
        var result = await manager.GetOrCreateAsync(p, Ct);
        Assert.True(result.IsGranted, $"expected a runtime, refused with {result.Refusal}");
        return result.Runtime!.Composer;
    }

    private static T Field<T>(object target, string name) =>
        (T)typeof(Composer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    // ── Same user → one shared runtime ──

    [Fact]
    public async Task SameUser_SequentialAndConcurrentCalls_ShareExactlyOneRuntimeAndConnection()
    {
        var (first, _) = await TwoUsersAsync();
        using var sp = BuildProvider();
        var manager = NewManager(sp);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => manager.GetOrCreateAsync(first, Ct)));
        var later = await manager.GetOrCreateAsync(first, Ct);

        Assert.All(results, r => Assert.Same(results[0].Runtime, r.Runtime));
        Assert.Same(results[0].Runtime, later.Runtime);
        Assert.Equal(1, _clientCreations);
        Assert.Equal(1, _bus.SubscriberCount);
        Assert.True(later.Runtime!.Composer.IsConnected);
    }

    // ── Different users → isolation ──

    [Fact]
    public async Task DifferentUsers_GetDistinctStateAttachmentsSubscribersAndRegistryIds()
    {
        var (first, second) = await TwoUsersAsync();
        using var sp = BuildProvider();
        var manager = NewManager(sp);

        var r1 = (await manager.GetOrCreateAsync(first, Ct)).Runtime!;
        var r2 = (await manager.GetOrCreateAsync(second, Ct)).Runtime!;

        Assert.NotSame(r1, r2);
        Assert.NotSame(r1.Composer, r2.Composer);
        Assert.Equal(1, r1.State.UserId);
        Assert.Equal(2, r2.State.UserId);
        Assert.True(r1.State.LegacyClaimEligible);
        Assert.False(r2.State.LegacyClaimEligible);
        Assert.NotSame(r1.Attachments, r2.Attachments);
        Assert.Equal(ComposerUserState.AttachmentsRootPath(StateDir, r1.State), r1.Attachments.AttachmentsRootPath);
        Assert.Equal(ComposerUserState.AttachmentsRootPath(StateDir, r2.State), r2.Attachments.AttachmentsRootPath);
        Assert.NotEqual(r1.Attachments.AttachmentsRootPath, r2.Attachments.AttachmentsRootPath);
        Assert.NotEqual(ComposerUserState.SessionFilePath(StateDir, r1.State), ComposerUserState.SessionFilePath(StateDir, r2.State));
        Assert.Equal("composer:1", ComposerUserState.RegistryId(r1.State));
        Assert.Equal("composer:2", ComposerUserState.RegistryId(r2.State));
        Assert.NotNull(r1.Subscriber);
        Assert.NotNull(r2.Subscriber);
        Assert.NotSame(r1.Subscriber, r2.Subscriber);
        Assert.Equal(2, _bus.SubscriberCount);

        // The Composers actually carry their own identity, attachments and subscriber.
        Assert.Same(r1.State, Field<ComposerUserState>(r1.Composer, "_userState"));
        Assert.Same(r2.Attachments, Field<ComposerAttachmentService>(r2.Composer, "_attachmentService"));
        Assert.Same(r2.Subscriber, Field<ComposerEventSubscriber>(r2.Composer, "_eventSubscriber"));
        var registry = sp.GetRequiredService<LlmSessionRegistry>();
        Assert.Contains("composer:1", registry.GetAll().Select(s => s.SessionId));
        Assert.Contains("composer:2", registry.GetAll().Select(s => s.SessionId));

        // One user's event delivery never lands in the other's buffer is NOT asserted (shared bus
        // broadcasts); ownership is per instance, which the distinct-subscriber checks above prove.
    }

    // ── Rejection matrix ──

    [Fact]
    public async Task DisabledPrincipal_IsRefusedWithoutCreatingAnything()
    {
        await TwoUsersAsync();
        using var sp = BuildProvider();
        var manager = NewManager(sp);
        var disabled = ActingUserPrincipal.ForActivatedAccount(2, "200", UserRole.Member, enabled: false, isOriginal: false);

        var result = await manager.GetOrCreateAsync(disabled, Ct);

        Assert.False(result.IsGranted);
        Assert.Equal(ComposerRuntimeRefusal.DisabledAccount, result.Refusal);
        Assert.Equal(0, _clientCreations);
        Assert.False(Directory.Exists(Path.Combine(StateDir, "composer")));
    }

    [Fact]
    public async Task UndefinedRolePrincipal_IsRefusedWithoutCreatingAnything()
    {
        var (first, _) = await TwoUsersAsync();
        using var sp = BuildProvider();
        var manager = NewManager(sp);
        // Factories refuse undefined roles, so force the (unreachable-in-production) value in.
        var forged = first with { };
        typeof(ActingUserPrincipal).GetField("<Role>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(forged, (UserRole)999);

        var result = await manager.GetOrCreateAsync(forged, Ct);

        Assert.Equal(ComposerRuntimeRefusal.UndefinedRole, result.Refusal);
        Assert.Equal(0, _clientCreations);
    }

    [Fact]
    public async Task SyntheticLocalOperator_IsRefusedRegardlessOfAuthMode_AndNeverCreatesARuntime()
    {
        await TwoUsersAsync();
        using var sp = BuildProvider();
        var manager = NewManager(sp);

        // The manager takes no auth-mode input: the refusal is unconditional, so open mode
        // (where the resolver yields this principal) and auth mode behave identically.
        var result = await manager.GetOrCreateAsync(ActingUserPrincipal.LocalOperator(), Ct);

        Assert.False(result.IsGranted);
        Assert.Equal(ComposerRuntimeRefusal.LocalOperator, result.Refusal);
        Assert.Null(result.Runtime);
        Assert.Equal(0, _clientCreations);
        Assert.False(Directory.Exists(Path.Combine(StateDir, "composer")));
    }

    [Fact]
    public async Task PrincipalWithoutActivatedAccount_IsRefused()
    {
        await TwoUsersAsync();
        using var sp = BuildProvider();
        var manager = NewManager(sp);
        var ghost = ActingUserPrincipal.ForActivatedAccount(77, "777", UserRole.Admin, true, isOriginal: false);

        var result = await manager.GetOrCreateAsync(ghost, Ct);

        Assert.Equal(ComposerRuntimeRefusal.UnknownAccount, result.Refusal);
        Assert.Equal(0, _clientCreations);
    }

    // ── Re-check on every call ──

    [Fact]
    public async Task CacheHit_WithStalePrincipalAfterDisable_IsRefused_AndRecoversAfterReEnable()
    {
        var (_, second) = await TwoUsersAsync();
        using var sp = BuildProvider();
        var manager = NewManager(sp);
        var runtime = (await manager.GetOrCreateAsync(second, Ct)).Runtime!;

        await _users.SetUserEnabledAsync(second.UserId, false, Ct);
        var stale = await manager.GetOrCreateAsync(second, Ct); // principal still says Enabled=true

        Assert.False(stale.IsGranted);
        Assert.Equal(ComposerRuntimeRefusal.DisabledAccount, stale.Refusal);

        await _users.SetUserEnabledAsync(second.UserId, true, Ct);
        var back = await manager.GetOrCreateAsync(second, Ct);
        Assert.Same(runtime, back.Runtime);
        Assert.Equal(1, _clientCreations);
    }

    [Fact]
    public async Task WaiterJoinedToInFlightCreation_IsRefusedWhenAccountDisabledMeanwhile()
    {
        var (_, second) = await TwoUsersAsync();
        using var sp = BuildProvider();
        var manager = NewManager(sp);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(false);
        _clientFactory = _ =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(30)), "creation gate was never released");
            return new Mock<IChatClient>().Object;
        };

        var creator = manager.GetOrCreateAsync(second, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct); // creation is parked inside connect
        // The entry exists, so this call joins the in-flight creation synchronously before returning.
        var waiter = manager.GetOrCreateAsync(second, Ct);
        Assert.False(waiter.IsCompleted);

        await _users.SetUserEnabledAsync(second.UserId, false, Ct);
        release.Set();

        var waiterResult = await waiter.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var creatorResult = await creator.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(ComposerRuntimeRefusal.DisabledAccount, waiterResult.Refusal);
        Assert.Equal(ComposerRuntimeRefusal.DisabledAccount, creatorResult.Refusal);
        Assert.Null(waiterResult.Runtime);
    }

    // ── Failed creation ──

    [Fact]
    public async Task FailedConnect_DisposesPartialRuntime_ClearsCacheEntry_AndRetrySucceeds()
    {
        var (_, second) = await TwoUsersAsync();
        using var sp = BuildProvider();
        var manager = NewManager(sp);
        var userRoot = ComposerUserState.AttachmentsRootPath(StateDir, await ComposerUserState.ResolveAsync(_users, second.UserId, Ct));
        _clientFactory = _ => throw new InvalidOperationException("boom");

        await Assert.ThrowsAnyAsync<Exception>(() => manager.GetOrCreateAsync(second, Ct));

        Assert.Equal(0, _bus.SubscriberCount); // the partial runtime's subscriber was detached
        Assert.Empty(sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(s => s.SessionId + "/" + s.Status));

        _clientFactory = _ => new Mock<IChatClient>().Object;
        var retry = await manager.GetOrCreateAsync(second, Ct);

        Assert.True(retry.IsGranted);
        Assert.True(retry.Runtime!.Composer.IsConnected);
        Assert.Equal(userRoot, retry.Runtime.Attachments.AttachmentsRootPath);
        Assert.Equal(1, _bus.SubscriberCount);
    }

    // ── Legacy equivalence through the shared seam ──

    [Fact]
    public async Task LegacyComposerBuiltThroughSharedSeam_KeepsLegacyIdentityPathsAndSharedSubscriber()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig { AvailableModels = [new ModelEntry { Name = Model }] },
            Composer = new ComposerConfig { Model = Model },
        };
        var legacyAttachments = new ComposerAttachmentService(StateDir, NullLogger<ComposerAttachmentService>.Instance);
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IGoalStore>(new GoalStore(_goalDb, NullLogger<GoalStore>.Instance));
        services.AddSingleton<IEventBus>(_bus);
        services.AddSingleton<LlmSessionRegistry>();
        services.AddSingleton(legacyAttachments);
        services.AddSingleton<ComposerEventSubscriber>();
        using var sp = services.BuildServiceProvider();

        await using var composer = NewFactory(sp).CreateLegacy();
        await composer.ConnectAsync(Ct);
        await composer.SaveSessionAsync(Ct);

        Assert.Equal(Model, composer.StartupDefaultModel);
        Assert.Null(Field<ComposerUserState?>(composer, "_userState"));
        Assert.Same(legacyAttachments, Field<ComposerAttachmentService>(composer, "_attachmentService"));
        Assert.Same(sp.GetRequiredService<ComposerEventSubscriber>(), Field<ComposerEventSubscriber>(composer, "_eventSubscriber"));
        Assert.Equal(Path.Combine(StateDir, "composer-attachments"), legacyAttachments.AttachmentsRootPath);
        Assert.True(File.Exists(Path.Combine(StateDir, "composer-session.json")));
        Assert.False(Directory.Exists(Path.Combine(StateDir, "composer", "users")));
        Assert.Equal(["composer"], sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(s => s.SessionId).ToArray());
    }
}

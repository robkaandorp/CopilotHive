using System.Reflection;
using System.Runtime.ExceptionServices;
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
using SharpCoder;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Creation / cache / re-check semantics of <see cref="ComposerRuntimeManager"/> and equivalence of
/// the legacy Composer built through the shared <see cref="ComposerFactory"/> seam.
/// </summary>
public sealed partial class ComposerRuntimeManagerTests : IAsyncLifetime
{
    private const string Model = "test-model";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"composer-runtime-mgr-{Guid.NewGuid():N}");
    private readonly MemoryDbFactory _factory;
    private readonly UserService _users;
    private readonly CountingEventBus _bus = new();
    private readonly CopilotHiveDbContext _goalDb = CopilotHiveDbContext.CreateInMemory();
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<Task<ComposerRuntimeResult>> _requests = [];
    private readonly List<Task<bool>> _removals = [];
    private readonly Dictionary<ComposerRuntimeManager, HashSet<int>> _managerUsers = [];
    private readonly List<LegacyIsolationSentinel> _legacySentinels = [];

    // Gate hooks for the chat-client factory used by every Composer built in these tests.
    private int _clientCreations;
    private Func<string, IChatClient> _clientFactory;

    // Connect-outcome hooks: the token probe / provider resolver the shared factory evaluates its deferral
    // gate with, and an optional replacement for the manager's single connect call.
    private volatile bool _tokenAvailable = true;
    private volatile string _provider = "test";
    private Func<string, string>? _providerResolver;
    private Func<Composer, CancellationToken, Task>? _connectHook;

    /// <summary>
    /// Test observation seam for the factory's token probe; <c>null</c> (inert) by default. Production evaluates
    /// this probe SYNCHRONOUSLY inside its deferral gate, so a test can park it deterministically and prove the
    /// manager never holds its gate across it. A hook must never assert: the production gate contains probe
    /// failures, so a hook records outcomes in counters that the test asserts on afterwards.
    /// </summary>
    private Action? _tokenProbeHook;

    public ComposerRuntimeManagerTests()
    {
        Directory.CreateDirectory(StateDir);
        _factory = new MemoryDbFactory();
        _users = new UserService(_factory, NullLogger<UserService>.Instance);
        _clientFactory = _ => new Mock<IChatClient>().Object;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        // Observe every owned call, then remove entries even if all callers were cancelled or
        // refused and therefore never exposed a runtime handle. Each cleanup step still runs.
        IEnumerable<Func<Task>> CleanupSteps()
        {
            foreach (var request in _requests)
                yield return async () =>
                {
                    // A fault can be the expected rejection asserted in the test. Still join EVERY
                    // request before disposing resources; timeout is never ignored as a fault.
                    await JoinRequestAsync(request);
                };
            foreach (var removal in _removals)
                yield return () => GuardAsync(removal, "owned removal completion");
            // RemoveAsync returns false for entries already owned by removal/shutdown. Only
            // ShutdownAsync joins those entries too, before providers/databases can be released.
            foreach (var manager in _managerUsers.Keys)
                yield return () => GuardAsync(manager.ShutdownAsync(), "fixture joins all actual manager teardowns");
            foreach (var sentinel in _legacySentinels)
            {
                yield return () => { sentinel.AssertUntouched(); return Task.CompletedTask; };
                yield return () => sentinel.DisposeAsync().AsTask();
            }
            foreach (var provider in _providers)
                yield return () => provider.DisposeAsync().AsTask();
            yield return () => { _goalDb.Dispose(); return Task.CompletedTask; };
            yield return () => { _factory.Dispose(); return Task.CompletedTask; };
            yield return () => { TestHelpers.ForceDeleteDirectory(_dir); return Task.CompletedTask; };
        }
        await CompleteCleanupAsync(CleanupSteps());
    }

    private static async Task CompleteCleanupAsync(IEnumerable<Func<Task>> steps)
    {
        var failures = new List<Exception>();
        foreach (var step in steps)
        {
            try { await step(); }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (failures.Count > 0) throw new AggregateException("Runtime test cleanup failed", failures);
    }

    // Separate connections share a uniquely named in-memory database. Keeping one open retains
    // it, without concurrently using a single SqliteConnection from several manager callers.
    private sealed class MemoryDbFactory : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly SqliteConnection _keeper;
        private readonly DbContextOptions<CopilotHiveDbContext> _options;

        public MemoryDbFactory()
        {
            var connectionString = $"Data Source=runtime-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            _keeper = new SqliteConnection(connectionString);
            _keeper.Open();
            _options = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(connectionString).Options;
            using var ctx = CreateDbContext();
            ctx.Database.EnsureCreated();
        }

        public CopilotHiveDbContext CreateDbContext() => new(_options);
        public void Dispose() => _keeper.Dispose();
    }

    private sealed class CountingEventBus : IEventBus
    {
        private event Action<SystemEvent>? Handlers;
        public int SubscriberCount => Handlers?.GetInvocationList().Length ?? 0;

        /// <summary>Production-caused milestone: invoked inside a subscriber's unsubscribe, before the handler is removed.</summary>
        public Action? OnUnsubscribe { get; set; }

        public event Action<SystemEvent>? OnEvent
        {
            add => Handlers += value;
            remove
            {
                OnUnsubscribe?.Invoke();
                Handlers -= value;
            }
        }

        public void Publish(SystemEvent evt) => Handlers?.Invoke(evt);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StateDir => Path.Combine(_dir, "state");

    private ServiceProvider BuildProvider(bool authEnabled = true, HiveConfigFile? config = null)
    {
        config ??= new HiveConfigFile
        {
            Models = new ModelsConfig { AvailableModels = [new ModelEntry { Name = Model }] },
            Composer = new ComposerConfig { Model = Model },
        };
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton(_users);
        var sentinel = new LegacyIsolationSentinel(_users);
        _legacySentinels.Add(sentinel);
        services.AddSingleton(sentinel.Coordinator);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IGoalStore>(new GoalStore(_goalDb, NullLogger<GoalStore>.Instance));
        services.AddSingleton<IEventBus>(_bus);
        services.AddSingleton<ComposerEventSubscriber>();
        services.AddSingleton(authEnabled ? HiveAuthMode.AuthenticationEnabled : HiveAuthMode.Open);
        // The COMPLETE security-primitive set. The per-user runtimes these tests create are identity-bound, and
        // identity-bound construction now REQUIRES a tool-authorization gate (a partial registration fails
        // closed), so a provider missing the policy catalog or the resolver could not build a runtime at all.
        services.AddSingleton<HivePolicy>();
        services.AddSingleton(sp => new HivePrincipalResolver(
            sp.GetRequiredService<UserService>(),
            sp.GetRequiredService<HiveAuthMode>(),
            NullLogger<HivePrincipalResolver>.Instance));
        services.AddSingleton<LlmSessionRegistry>();
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    private ComposerFactory NewFactory(IServiceProvider sp) =>
        new(sp, StateDir, ollamaApiKey: null, chatClientFactory: m =>
        {
            Interlocked.Increment(ref _clientCreations);
            return _clientFactory(m);
        }, isTokenAvailable: () =>
        {
            _tokenProbeHook?.Invoke();
            return _tokenAvailable;
        }, providerOf: model => _providerResolver?.Invoke(model) ?? _provider);

    private ComposerRuntimeManager NewManager(IServiceProvider sp, ILogger<ComposerRuntimeManager>? logger = null,
        ComposerFactory? factory = null)
    {
        var m = new ComposerRuntimeManager(factory ?? NewFactory(sp), _users, logger ?? NullLogger<ComposerRuntimeManager>.Instance,
            (composer, ct) => (_connectHook ?? ((c, t) => c.ConnectAsync(t)))(composer, ct));
        _managerUsers.Add(m, []);
        return m;
    }

    /// <summary>Bootstraps the original admin (id 1) and one activated invited Member (id 2).</summary>
    private async Task<(ActingUserPrincipal First, ActingUserPrincipal Second)> TwoUsersAsync(UserRole secondRole = UserRole.Member)
    {
        var original = await _users.CreateOrUpdateUserAsync(
            "100", "first", null, null, null, "tok", "ref", "2030-01-01T00:00:00.0000000Z", Ct);
        await _users.InviteUserAsync("second", secondRole, Ct);
        var invited = await _users.ActivateInvitationAsync("second", "200", null, null, null, Ct);
        return (ActingUserPrincipal.ForActivatedAccount(original.Id, "100", UserRole.Admin, true, isOriginal: true),
                ActingUserPrincipal.ForActivatedAccount(invited.Id, "200", secondRole, true, isOriginal: false));
    }

    private Task<ComposerRuntimeResult> Request(ComposerRuntimeManager manager, ActingUserPrincipal principal, CancellationToken? cancellationToken = null)
    {
        _managerUsers[manager].Add(principal.UserId);
        var task = manager.GetOrCreateAsync(principal, cancellationToken ?? Ct);
        _requests.Add(task); // Own it BEFORE any assertion can throw.
        return task;
    }

    private Task<ComposerRuntimeResult> Retry(ComposerRuntimeManager manager, ActingUserPrincipal principal,
        CancellationToken? cancellationToken = null)
    {
        _managerUsers[manager].Add(principal.UserId);
        var task = manager.ConnectAsync(principal, cancellationToken ?? Ct);
        _requests.Add(task);
        return task;
    }

    private Task<bool> Remove(ComposerRuntimeManager manager, int userId)
    {
        var task = manager.RemoveAsync(userId);
        _removals.Add(task);
        return task;
    }

    private static async Task GuardAsync(Task task, string milestone)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None); }
        catch (TimeoutException ex) { throw new TimeoutException($"Timed out: {milestone}", ex); }
    }

    private static async Task JoinRequestAsync(Task<ComposerRuntimeResult> task)
    {
        // Observe faults only AFTER establishing completion with the named hang guard.
        await GuardAsync(task.ContinueWith(_ => { }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default), "manager request completion");
        if (task.IsFaulted) _ = task.Exception;
    }

    private async Task SetStoredRoleAsync(ActingUserPrincipal principal, string role)
    {
        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE users SET role={role} WHERE id={principal.UserId}", CancellationToken.None));
    }

    private static string InvalidRoleMessage =>
        "The persisted user role 'retired-role' is not a supported CopilotHive user role " +
        "(expected exactly one of: viewer, member, operator, admin).";

    private static async Task AssertInvalidRoleAsync(Task<ComposerRuntimeResult> request)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await GuardAsync(request, "invalid-role request rejection");
            Assert.Null(request.Result.Runtime); // a successful handle must NEVER escape this vector
        });
        Assert.Equal(InvalidRoleMessage, error.Message);
        Assert.True(request.IsFaulted);
    }

    private async Task WithParkedCreationAsync(ComposerRuntimeManager manager, ActingUserPrincipal principal,
        Func<Task<ComposerRuntimeResult>, Action, Task> body)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        _clientFactory = _ =>
        {
            // Only the first creation is parked; a different user must be free to create.
            if (Volatile.Read(ref _clientCreations) > 1) return new Mock<IChatClient>().Object;
            entered.TrySetResult(); // production-caused: Composer reached the real client factory
            Assert.True(release.Wait(TimeSpan.FromSeconds(30)), "Timed out: release parked Composer client factory");
            return new Mock<IChatClient>().Object;
        };
        ExceptionDispatchInfo? primary = null;
        Exception? cleanupFailure = null;
        try
        {
            var creator = Request(manager, principal);
            await GuardAsync(entered.Task, "Composer entered client factory");
            await body(creator, release.Set);
        }
        catch (Exception ex) { primary = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            release.Set(); // unblock production BEFORE observing any owned task
            try
            {
                foreach (var task in _requests.ToArray()) await JoinRequestAsync(task);
                // Refused creator/waiter calls do not expose the created runtime. Restore the
                // directory and retrieve it for cleanup even when a test assertion failed.
                await SetStoredRoleAsync(principal, UserRoleText.Format(principal.Role));
                var account = await _users.GetActivatedUserByIdAsync(principal.UserId, CancellationToken.None);
                if (account is { Enabled: false })
                    await _users.SetUserEnabledAsync(principal.UserId, true, CancellationToken.None);
                var recovered = Request(manager, principal, CancellationToken.None);
                await GuardAsync(recovered, "retrieve parked runtime for cleanup");
                Assert.True(recovered.Result.IsGranted);
            }
            catch (Exception ex) { cleanupFailure = ex; }
        }
        if (primary is not null)
        {
            if (cleanupFailure is not null) primary.SourceException.Data["CleanupFailure"] = cleanupFailure;
            primary.Throw();
        }
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    private static T Field<T>(object target, string name) =>
        (T)typeof(Composer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    /// <summary>
    /// Reads a private instance field declared on <paramref name="target"/>'s OWN type (e.g. a
    /// <see cref="ComposerAgentService"/> field), unlike <see cref="Field{T}"/> which is anchored on <see cref="Composer"/>.
    /// </summary>
    private static T InstanceField<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    // ── Same user → one shared runtime ──

    [Fact]
    public async Task SameUser_SequentialAndConcurrentCalls_ShareExactlyOneRuntimeAndConnection()
    {
        var (first, _) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);

        await WithParkedCreationAsync(manager, first, async (creator, release) =>
        {
            var concurrent = Enumerable.Range(0, 8).Select(_ => Request(manager, first)).ToArray();
            Assert.False(creator.IsCompleted);
            Assert.All(concurrent, task => Assert.False(task.IsCompleted));
            release();
            var all = Task.WhenAll(concurrent.Prepend(creator));
            await GuardAsync(all, "all same-user callers receive shared runtime");
            var results = await all;
            var later = await Request(manager, first);

            Assert.All(results, r =>
            {
                Assert.True(r.IsGranted);
                Assert.Equal(ComposerRuntimeRefusal.None, r.Refusal);
                Assert.Same(results[0].Runtime, r.Runtime);
                Assert.Same(later.Runtime!.Composer, r.Runtime!.Composer);
                Assert.Same(later.Runtime.Attachments, r.Runtime.Attachments);
                Assert.Same(later.Runtime.Subscriber, r.Runtime.Subscriber);
                Assert.Same(Field<object>(later.Runtime.Composer, "_actor"), Field<object>(r.Runtime.Composer, "_actor"));
            });
            Assert.Same(results[0].Runtime, later.Runtime);
            Assert.Equal(1, _clientCreations);
            Assert.Equal(1, _bus.SubscriberCount);
            Assert.True(later.Runtime!.Composer.IsConnected);
            Assert.Equal(["composer:1"], sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(s => s.SessionId).ToArray());
        });
    }

    // ── Different users → isolation ──

    [Fact]
    public async Task DifferentUsers_GetDistinctStateAttachmentsSubscribersAndRegistryIds()
    {
        var (first, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);

        var firstPath = Path.Combine(StateDir, "composer", "users", "1", "session.json");
        var secondPath = Path.Combine(StateDir, "composer", "users", "2", "session.json");
        await WriteSessionAsync(firstPath, "first-user-session-marker");
        await WriteSessionAsync(secondPath, "second-user-session-marker");
        var r1 = (await Request(manager, first)).Runtime!;
        var r2 = (await Request(manager, second)).Runtime!;

        Assert.NotSame(r1, r2);
        Assert.NotSame(r1.Composer, r2.Composer);
        Assert.NotSame(r1.State, r2.State);
        Assert.NotSame(Field<object>(r1.Composer, "_actor"), Field<object>(r2.Composer, "_actor"));
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

        Assert.Equal(["composer:1", "composer:2"], registry.GetAll().Select(s => s.SessionId).Order().ToArray());
        Assert.Equal(firstPath, ComposerUserState.SessionFilePath(StateDir, r1.State));
        Assert.Equal(secondPath, ComposerUserState.SessionFilePath(StateDir, r2.State));
        Assert.Equal(Path.GetFullPath(Path.Combine(StateDir, "composer", "users", "1", "attachments")), r1.Attachments.AttachmentsRootPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(StateDir, "composer", "users", "2", "attachments")), r2.Attachments.AttachmentsRootPath);
        Assert.Equal(["first-user-session-marker"], r1.Composer.GetChatHistory().Select(m => m.Content));
        Assert.Equal(["second-user-session-marker"], r2.Composer.GetChatHistory().Select(m => m.Content));
        await r1.Composer.SaveSessionAsync(Ct);
        await r2.Composer.SaveSessionAsync(Ct);
        var firstSaved = await File.ReadAllTextAsync(firstPath, Ct);
        var secondSaved = await File.ReadAllTextAsync(secondPath, Ct);
        Assert.Contains("first-user-session-marker", firstSaved);
        Assert.DoesNotContain("second-user-session-marker", firstSaved);
        Assert.Contains("second-user-session-marker", secondSaved);
        Assert.DoesNotContain("first-user-session-marker", secondSaved);
        Assert.False(File.Exists(Path.Combine(StateDir, "composer-session.json")));

        using var firstContent = new MemoryStream([1, 3, 5, 7]);
        using var secondContent = new MemoryStream([2, 4, 6, 8]);
        var firstAttachment = await r1.Attachments.SaveAsync("first.png", firstContent, Ct);
        var secondAttachment = await r2.Attachments.SaveAsync("second.png", secondContent, Ct);
        Assert.True(firstAttachment.Success, firstAttachment.Error);
        Assert.True(secondAttachment.Success, secondAttachment.Error);
        Assert.Equal(new byte[] { 1, 3, 5, 7 }, await File.ReadAllBytesAsync(Path.Combine(r1.Attachments.AttachmentsRootPath, firstAttachment.Attachment!.SavedRelativePath), Ct));
        Assert.Equal(new byte[] { 2, 4, 6, 8 }, await File.ReadAllBytesAsync(Path.Combine(r2.Attachments.AttachmentsRootPath, secondAttachment.Attachment!.SavedRelativePath), Ct));
        Assert.False(r2.Attachments.Remove(firstAttachment.Attachment).Success);
        Assert.True(File.Exists(Path.Combine(r1.Attachments.AttachmentsRootPath, firstAttachment.Attachment.SavedRelativePath)));

        // The global bus broadcasts to all buffers. Draining one managed buffer must NOT drain
        // another (or the legacy subscriber), and managed construction must never borrow DI's one.
        var legacySubscriber = sp.GetRequiredService<ComposerEventSubscriber>();
        Assert.NotSame(legacySubscriber, r1.Subscriber);
        Assert.NotSame(legacySubscriber, r2.Subscriber);
        var evt = new SystemEvent(EventType.GoalCompleted, "independent-buffer-marker", GoalId: "isolated-goal");
        _bus.Publish(evt);
        Assert.Same(evt, Assert.Single(r1.Subscriber.DrainPendingEvents()));
        Assert.Empty(r1.Subscriber.PeekPendingEvents());
        Assert.Same(evt, Assert.Single(r2.Subscriber.PeekPendingEvents()));
        Assert.Same(evt, Assert.Single(r2.Subscriber.DrainPendingEvents()));
        Assert.Same(evt, Assert.Single(legacySubscriber.DrainPendingEvents()));
    }

    // ── Rejection matrix ──

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledPrincipal_IsRefusedWithoutCreatingAnything(bool authEnabled)
    {
        await TwoUsersAsync();
        var sp = BuildProvider(authEnabled);
        Assert.Equal(authEnabled, sp.GetRequiredService<HiveAuthMode>().AuthEnabled);
        var manager = NewManager(sp);
        var disabled = ActingUserPrincipal.ForActivatedAccount(2, "200", UserRole.Member, enabled: false, isOriginal: false);

        var result = await Request(manager, disabled);

        Assert.False(result.IsGranted);
        Assert.Null(result.Runtime);
        Assert.Equal(ComposerRuntimeRefusal.DisabledAccount, result.Refusal);
        Assert.Equal(0, _clientCreations);
        Assert.Empty(sp.GetRequiredService<LlmSessionRegistry>().GetAll());
        Assert.False(Directory.Exists(Path.Combine(StateDir, "composer")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UndefinedRolePrincipal_IsRefusedWithoutCreatingAnything(bool authEnabled)
    {
        var (first, _) = await TwoUsersAsync();
        var sp = BuildProvider(authEnabled);
        Assert.Equal(authEnabled, sp.GetRequiredService<HiveAuthMode>().AuthEnabled);
        var manager = NewManager(sp);
        // Sanctioned factories reject undefined roles. Only this defensive-input cell uses
        // the private constructor; directory-state vectors below use real persisted SQL data.
        var forged = (ActingUserPrincipal)typeof(ActingUserPrincipal)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Length == 6)
            .Invoke([first.UserId, first.GitHubId, (UserRole)999, true, true, false]);

        var result = await Request(manager, forged);

        Assert.False(result.IsGranted);
        Assert.Null(result.Runtime);
        Assert.Equal(ComposerRuntimeRefusal.UndefinedRole, result.Refusal);
        Assert.Equal(0, _clientCreations);
        Assert.Empty(sp.GetRequiredService<LlmSessionRegistry>().GetAll());
        Assert.False(Directory.Exists(Path.Combine(StateDir, "composer")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyntheticLocalOperator_IsRefusedRegardlessOfAuthMode_AndNeverCreatesARuntime(bool authEnabled)
    {
        await TwoUsersAsync();
        var sp = BuildProvider(authEnabled);
        Assert.Equal(authEnabled, sp.GetRequiredService<HiveAuthMode>().AuthEnabled);
        var manager = NewManager(sp);

        // The manager takes no auth-mode input: the refusal is unconditional, so open mode
        // (where the resolver yields this principal) and auth mode behave identically.
        var result = await Request(manager, ActingUserPrincipal.LocalOperator());

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
        var sp = BuildProvider();
        var manager = NewManager(sp);
        var ghost = ActingUserPrincipal.ForActivatedAccount(77, "777", UserRole.Admin, true, isOriginal: false);

        var result = await Request(manager, ghost);

        Assert.Equal(ComposerRuntimeRefusal.UnknownAccount, result.Refusal);
        Assert.Equal(0, _clientCreations);
    }

    // ── Re-check on every call ──

    [Fact]
    public async Task CacheHit_WithStalePrincipalAfterDisable_IsRefused_AndRecoversAfterReEnable()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);
        var runtime = (await Request(manager, second)).Runtime!;

        await _users.SetUserEnabledAsync(second.UserId, false, Ct);
        var stale = await Request(manager, second); // principal still says Enabled=true

        Assert.False(stale.IsGranted);
        Assert.Equal(ComposerRuntimeRefusal.DisabledAccount, stale.Refusal);
        Assert.Null(stale.Runtime);
        Assert.True(second.Enabled);

        await _users.SetUserEnabledAsync(second.UserId, true, Ct);
        var back = await Request(manager, second);
        Assert.Same(runtime, back.Runtime);
        Assert.Equal(1, _clientCreations);
    }

    [Fact]
    public async Task WaiterJoinedToInFlightCreation_IsRefusedWhenAccountDisabledMeanwhile()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);

        await WithParkedCreationAsync(manager, second, async (creator, release) =>
        {
            // With an entry already parked, GetOrCreate reaches Creation.WaitAsync synchronously
            // (no earlier await is executed on this path). Returning an incomplete task therefore
            // proves this caller has joined the in-flight entry, not a hoped-for scheduler turn.
            var waiter = Request(manager, second);
            Assert.False(waiter.IsCompleted);
            Assert.False(creator.IsCompleted);
            await _users.SetUserEnabledAsync(second.UserId, false, Ct);
            Assert.True(second.Enabled); // caller still holds the pre-disable principal
            release();
            await GuardAsync(Task.WhenAll(creator, waiter), "disabled creator and waiter rechecks");
            foreach (var result in new[] { await creator, await waiter })
            {
                Assert.False(result.IsGranted);
                Assert.Equal(ComposerRuntimeRefusal.DisabledAccount, result.Refusal);
                Assert.Null(result.Runtime);
            }
            await _users.SetUserEnabledAsync(second.UserId, true, Ct);
            var recovered = await Request(manager, second);
            Assert.True(recovered.IsGranted);
            Assert.Same(recovered.Runtime, (await Request(manager, second)).Runtime);
            Assert.Equal(1, _clientCreations);
        });
    }

    [Fact]
    public async Task CacheHit_WithStalePrincipalAfterRoleInvalidation_IsRejectedAndKeepsCachedRuntime()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);
        var before = (await Request(manager, second)).Runtime!;
        await SetStoredRoleAsync(second, "retired-role");

        Assert.Equal(UserRole.Member, second.Role);
        await AssertInvalidRoleAsync(Request(manager, second));

        await SetStoredRoleAsync(second, "member");
        var after = await Request(manager, second);
        Assert.True(after.IsGranted);
        Assert.Same(before, after.Runtime);
        Assert.Same(before.Composer, after.Runtime!.Composer);
        Assert.Same(before.Attachments, after.Runtime.Attachments);
        Assert.Equal(1, _clientCreations);
        Assert.Equal(1, _bus.SubscriberCount);
        Assert.Equal(["composer:2"], sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(s => s.SessionId));
    }

    [Fact]
    public async Task WaiterJoinedToInFlightCreation_IsRejectedWhenRoleInvalidatedMeanwhile_AndCacheRecovers()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);
        await WithParkedCreationAsync(manager, second, async (creator, release) =>
        {
            var waiter = Request(manager, second);
            Assert.False(waiter.IsCompleted);
            Assert.False(creator.IsCompleted);
            await SetStoredRoleAsync(second, "retired-role");
            Assert.Equal(UserRole.Member, second.Role);
            release();

            await AssertInvalidRoleAsync(waiter);
            await AssertInvalidRoleAsync(creator);
            await SetStoredRoleAsync(second, "member");
            var recovered = await Request(manager, second);
            var again = await Request(manager, second);
            Assert.True(recovered.IsGranted);
            Assert.Same(recovered.Runtime, again.Runtime);
            Assert.Same(recovered.Runtime!.Composer, again.Runtime!.Composer);
            Assert.Same(recovered.Runtime.Attachments, again.Runtime.Attachments);
            Assert.Equal(1, _clientCreations); // failed rechecks did not evict/recreate the entry
            Assert.Equal(1, _bus.SubscriberCount);
            Assert.Equal(["composer:2"], sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(s => s.SessionId));
        });
    }

    [Fact]
    public async Task CacheHit_DemotionToDefinedViewerRole_StillGrantsTheSameRuntime()
    {
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider());
        var before = (await Request(manager, second)).Runtime!;
        await SetStoredRoleAsync(second, "viewer");

        var after = await Request(manager, second);

        Assert.True(after.IsGranted); // capability enforcement is deliberately NOT this slice
        Assert.Equal(ComposerRuntimeRefusal.None, after.Refusal);
        Assert.Same(before, after.Runtime);
        Assert.Equal(1, _clientCreations);
    }

    [Fact]
    public async Task WaiterJoinedToInFlightCreation_DemotionToDefinedViewerRole_StillSharesRuntime()
    {
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider());
        await WithParkedCreationAsync(manager, second, async (creator, release) =>
        {
            var waiter = Request(manager, second);
            Assert.False(waiter.IsCompleted);
            await SetStoredRoleAsync(second, "viewer");
            release();
            await GuardAsync(Task.WhenAll(creator, waiter), "defined-role creator and waiter complete");
            Assert.True((await creator).IsGranted);
            Assert.True((await waiter).IsGranted);
            Assert.Same((await creator).Runtime, (await waiter).Runtime);
            Assert.Equal(1, _clientCreations);
        });
    }

    [Fact]
    public async Task FirstCall_WithStalePrincipalAfterDisable_DoesNotCreateOrMigrate()
    {
        var (first, _) = await TwoUsersAsync(UserRole.Admin);
        var manager = NewManager(BuildProvider());
        var legacyPath = Path.Combine(StateDir, "composer-session.json");
        await WriteSessionAsync(legacyPath, "disabled-original-history-marker");
        var before = await File.ReadAllBytesAsync(legacyPath, Ct);
        await _users.SetUserEnabledAsync(first.UserId, false, Ct);

        var result = await Request(manager, first);

        Assert.True(first.Enabled);
        Assert.False(result.IsGranted);
        Assert.Null(result.Runtime);
        Assert.Equal(ComposerRuntimeRefusal.DisabledAccount, result.Refusal);
        Assert.Equal(0, _clientCreations);
        Assert.Equal(0, _bus.SubscriberCount);
        Assert.False(Directory.Exists(Path.Combine(StateDir, "composer")));
        Assert.Equal(before, await File.ReadAllBytesAsync(legacyPath, Ct));
    }

    [Fact]
    public async Task FirstCall_WithStalePrincipalAfterRoleInvalidation_DoesNotCreateOrMigrate()
    {
        var (first, _) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider());
        var legacyPath = Path.Combine(StateDir, "composer-session.json");
        await WriteSessionAsync(legacyPath, "invalid-role-original-history-marker");
        var before = await File.ReadAllBytesAsync(legacyPath, Ct);
        await SetStoredRoleAsync(first, "retired-role");

        await AssertInvalidRoleAsync(Request(manager, first));

        Assert.Equal(UserRole.Admin, first.Role);
        Assert.Equal(0, _clientCreations);
        Assert.Equal(0, _bus.SubscriberCount);
        Assert.False(Directory.Exists(Path.Combine(StateDir, "composer")));
        Assert.Equal(before, await File.ReadAllBytesAsync(legacyPath, Ct));
    }

    [Fact]
    public async Task DifferentUsers_OneCreationInFlight_DoesNotBlockTheOtherUser()
    {
        var (first, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider());
        await WithParkedCreationAsync(manager, first, async (creator, release) =>
        {
            var other = Request(manager, second);
            await GuardAsync(other, "second user's independent creation while first is parked");
            Assert.True((await other).IsGranted);
            Assert.False(creator.IsCompleted);
            release();
            await GuardAsync(creator, "first user's creation resumes");
            Assert.NotSame((await creator).Runtime, (await other).Runtime);
            Assert.Equal(2, _clientCreations);
        });
    }

    [Fact]
    public async Task ParkedCreationHelper_WhenBodyThrows_JoinsRequestsAndRetainsPrimaryFailure()
    {
        var (_, second) = await TwoUsersAsync();
        var manager = NewManager(BuildProvider());
        var sentinel = new InvalidOperationException("deliberate test-body failure while creator is parked");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WithParkedCreationAsync(manager, second, (_, _) => throw sentinel));

        Assert.Same(sentinel, error);
        Assert.False(error.Data.Contains("CleanupFailure"));
        Assert.All(_requests, task => Assert.True(task.IsCompletedSuccessfully));
        var runtime = Assert.Single(_requests.Select(task => task.Result.Runtime).Distinct());
        Assert.NotNull(runtime);
        Assert.Equal(1, _clientCreations);
        Assert.Same(runtime, (await Request(manager, second)).Runtime);
    }

    [Fact]
    public async Task FixtureCleanup_WhenAStepFails_CompletesRemainingStepsAndRetainsAllFailures()
    {
        var firstFailure = new InvalidOperationException("deliberate first cleanup failure");
        var lastFailure = new IOException("deliberate last cleanup failure");
        var completed = new List<int>();
        var error = await Assert.ThrowsAsync<AggregateException>(() => CompleteCleanupAsync(
        [
            () => { completed.Add(1); throw firstFailure; },
            () => { completed.Add(2); return Task.CompletedTask; },
            () => { completed.Add(3); throw lastFailure; },
            () => { completed.Add(4); return Task.CompletedTask; },
        ]));

        Assert.Equal([1, 2, 3, 4], completed);
        Assert.Collection(error.InnerExceptions,
            failure => Assert.Same(firstFailure, failure),
            failure => Assert.Same(lastFailure, failure));
    }

    private async Task WriteSessionAsync(string path, string marker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var session = AgentSession.Create("composer");
        session.MessageHistory.Add(new ChatMessage(ChatRole.User, marker));
        await session.SaveAsync(path, Ct);
    }

    [Fact]
    public async Task OriginalAccount_ManagerRequest_MigratesLegacyBytesAndLoadsHistory()
    {
        var (first, _) = await TwoUsersAsync();
        var legacyPath = Path.Combine(StateDir, "composer-session.json");
        await WriteSessionAsync(legacyPath, "original-only-legacy-history-marker");
        var before = await File.ReadAllBytesAsync(legacyPath, Ct);
        var manager = NewManager(BuildProvider());

        var result = await Request(manager, first);

        Assert.True(result.IsGranted);
        var runtime = result.Runtime!;
        Assert.True(runtime.State.LegacyClaimEligible);
        Assert.True(runtime.State.PersistenceAllowed);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(StateDir, "composer", "users", "1", "session.json"), Ct));
        Assert.Equal(before, await File.ReadAllBytesAsync(legacyPath, Ct));
        Assert.Equal(["original-only-legacy-history-marker"], runtime.Composer.GetChatHistory().Select(m => m.Content));
        using var evidence = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(StateDir, "composer", "users", "1", "legacy-migration.json"), Ct));
        Assert.Equal(first.UserId, evidence.RootElement.GetProperty("userId").GetInt32());
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(before)), evidence.RootElement.GetProperty("sha256").GetString());
        Assert.False(Directory.Exists(Path.Combine(StateDir, "composer", "users", "2")));
    }

    [Fact]
    public async Task SecondAdmin_ManagerRequestBeforeOriginal_NeverClaimsOrLoadsLegacyHistory()
    {
        var (first, second) = await TwoUsersAsync(UserRole.Admin);
        var legacyPath = Path.Combine(StateDir, "composer-session.json");
        await WriteSessionAsync(legacyPath, "unclaimed-original-history-marker");
        var before = await File.ReadAllBytesAsync(legacyPath, Ct);
        var manager = NewManager(BuildProvider());

        var result = await Request(manager, second);

        Assert.True(result.IsGranted);
        Assert.False(result.Runtime!.State.LegacyClaimEligible);
        Assert.True(result.Runtime.State.PersistenceAllowed);
        Assert.Empty(result.Runtime.Composer.GetChatHistory());
        Assert.False(File.Exists(Path.Combine(StateDir, "composer", "users", "2", "session.json")));
        Assert.False(File.Exists(Path.Combine(StateDir, "composer", "users", "2", "legacy-migration.json")));
        Assert.False(Directory.Exists(Path.Combine(StateDir, "composer", "users", "1")));
        Assert.Equal(before, await File.ReadAllBytesAsync(legacyPath, Ct));

        // Positive control in the same fresh directory: legacy history really IS claimable.
        var original = await Request(manager, first);
        Assert.True(original.IsGranted);
        Assert.Equal(["unclaimed-original-history-marker"], original.Runtime!.Composer.GetChatHistory().Select(m => m.Content));
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(StateDir, "composer", "users", "1", "session.json"), Ct));
        Assert.Empty(result.Runtime.Composer.GetChatHistory());
    }

    // ── Failed creation ──

    [Fact]
    public async Task FailedConnect_DisposesPartialRuntime_ClearsCacheEntry_AndRetrySucceeds()
    {
        var (_, second) = await TwoUsersAsync();
        var sp = BuildProvider();
        var manager = NewManager(sp);
        var userRoot = ComposerUserState.AttachmentsRootPath(StateDir, await ComposerUserState.ResolveAsync(_users, second.UserId, Ct));
        var sentinel = new InvalidOperationException("deliberate client construction failure");
        _clientFactory = _ => throw sentinel;

        // A failed connect is a TYPED outcome carrying the original fault, reported only after cleanup.
        var failed = await Request(manager, second);
        Assert.Equal(ComposerRuntimeStatus.Failed, failed.Status);
        Assert.Same(sentinel, failed.Failure);
        Assert.Null(failed.Runtime);
        Assert.False(failed.IsGranted);

        Assert.Equal(0, _bus.SubscriberCount); // the partial runtime's subscriber was detached
        Assert.Empty(sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(s => s.SessionId + "/" + s.Status));

        _clientFactory = _ => new Mock<IChatClient>().Object;
        var retry = await Request(manager, second);

        Assert.True(retry.IsGranted);
        Assert.True(retry.Runtime!.Composer.IsConnected);
        Assert.Equal(userRoot, retry.Runtime.Attachments.AttachmentsRootPath);
        Assert.Equal(1, _bus.SubscriberCount);
    }

    [Theory]
    [InlineData(true, "high", "low", ReasoningEffort.High, 45001, 17)]
    [InlineData(true, "   ", "low", ReasoningEffort.Low, null, 0)]
    [InlineData(true, null, null, null, null, 19)]
    [InlineData(true, "unsupported-effort", "low", null, 45003, 23)]
    [InlineData(false, null, "medium", ReasoningEffort.Medium, 45005, 29)]
    public async Task SharedFactory_LegacyAndManagedConstruction_PreserveConfigurationPrecedence(
        bool composerSection, string? composerEffort, string? brainEffort, ReasoningEffort? expectedEffort,
        int? contextWindow, int maxSteps)
    {
        var (_, second) = await TwoUsersAsync();
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = Model, ContextWindow = contextWindow },
                    new ModelEntry { Name = "brain-only-model", ContextWindow = 81000 }],
            },
            Orchestrator = new OrchestratorConfig
            {
                Model = "brain-only-model", BrainMaxSteps = 73, ReasoningEffort = brainEffort,
            },
            Composer = composerSection
                ? new ComposerConfig { Model = $"  {Model.ToUpperInvariant()}  ", MaxSteps = maxSteps, ReasoningEffort = composerEffort }
                : null,
        };
        var sp = BuildProvider(config: config);
        await using var legacy = NewFactory(sp).CreateLegacy();
        var managed = (await Request(NewManager(sp), second)).Runtime!;

        foreach (var composer in new[] { legacy, managed.Composer })
        {
            var service = Field<ComposerAgentService>(composer, "_agentService");
            Assert.Equal(composerSection ? Model : null, composer.StartupDefaultModel);
            Assert.Equal(composerSection ? maxSteps : 73, service.MaxSteps);
            Assert.Equal(composerSection ? contextWindow ?? 150000 : 150000, service.MaxContextTokens);
            Assert.Equal(expectedEffort, composer.ReasoningEffort);
            Assert.Equal([Model, "brain-only-model"], composer.AvailableModels);
        }
        // Missing Composer model never falls back to the orchestrator's distinct model, nor
        // to its context window. Both entry points consume the SAME moved construction logic.
        Assert.Equal(composerSection ? 1 : 0, _clientCreations);
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

        await WriteSessionAsync(Path.Combine(StateDir, "composer-session.json"), "legacy-factory-history-marker");
        await using var composer = NewFactory(sp).CreateLegacy();
        await composer.ConnectAsync(Ct);
        Assert.Equal(["legacy-factory-history-marker"], composer.GetChatHistory().Select(m => m.Content));
        await composer.SaveSessionAsync(Ct);

        Assert.Equal(Model, composer.StartupDefaultModel);
        Assert.Null(Field<ComposerUserState?>(composer, "_userState"));
        Assert.Same(legacyAttachments, Field<ComposerAttachmentService>(composer, "_attachmentService"));
        Assert.Same(sp.GetRequiredService<ComposerEventSubscriber>(), Field<ComposerEventSubscriber>(composer, "_eventSubscriber"));
        var evt = new SystemEvent(EventType.IssueRaised, "legacy-shared-subscriber-marker", IssueId: "legacy-issue");
        _bus.Publish(evt);
        Assert.Same(evt, Assert.Single(sp.GetRequiredService<ComposerEventSubscriber>().DrainPendingEvents()));
        Assert.Equal(Path.Combine(StateDir, "composer-attachments"), legacyAttachments.AttachmentsRootPath);
        Assert.True(File.Exists(Path.Combine(StateDir, "composer-session.json")));
        Assert.False(Directory.Exists(Path.Combine(StateDir, "composer", "users")));
        Assert.Equal(["composer"], sp.GetRequiredService<LlmSessionRegistry>().GetAll().Select(s => s.SessionId).ToArray());
    }
}

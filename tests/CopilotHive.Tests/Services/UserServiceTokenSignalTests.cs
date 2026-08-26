using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Tests for the token-available signal contract on <see cref="UserService.CreateOrUpdateUserAsync"/>:
/// it fires ONLY for a non-whitespace committed token, delivery is fire-and-forget, and the
/// sign-in request's behaviour never changes based on signal delivery (in particular a failed
/// deferred Composer connect is never thrown into it).
/// </summary>
public sealed class UserServiceTokenSignalTests
{
    /// <summary>
    /// Creates fresh contexts over a single shared, open in-memory SQLite connection so the
    /// database survives per-call context disposal.
    /// </summary>
    private sealed class SharedConnectionFactory : IDbContextFactory<CopilotHiveDbContext>, IDisposable
    {
        private readonly SqliteConnection _connection;

        public SharedConnectionFactory()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();

            using var ctx = CreateDbContext();
            ctx.Database.EnsureCreated();
        }

        public CopilotHiveDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite(_connection)
                .Options;
            return new CopilotHiveDbContext(options);
        }

        public void Dispose() => _connection.Dispose();
    }

    private static (UserService Service, SharedConnectionFactory Factory) CreateService()
    {
        var factory = new SharedConnectionFactory();
        return (new UserService(factory, NullLogger<UserService>.Instance), factory);
    }

    private static Task<UserEntity> SignInAsync(UserService service, string accessToken) =>
        service.CreateOrUpdateUserAsync(
            "12345", "octocat", "The Octocat", null, null,
            accessToken, "refresh", null, TestContext.Current.CancellationToken);

    /// <summary>A committed non-whitespace token raises the signal exactly once, on create.</summary>
    [Fact]
    public async Task CreateOrUpdateUserAsync_NonWhitespaceToken_RaisesSignalOnce()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var signals = 0;
        service.TokenAvailable += () => Interlocked.Increment(ref signals);

        await SignInAsync(service, "token-abc");

        Assert.Equal(1, signals);
    }

    /// <summary>The signal fires on the UPDATE path too (a returning admin re-authenticating).</summary>
    [Fact]
    public async Task CreateOrUpdateUserAsync_UpdateWithToken_RaisesSignal()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await SignInAsync(service, "token-abc");

        var signals = 0;
        service.TokenAvailable += () => Interlocked.Increment(ref signals);

        var updated = await SignInAsync(service, "token-def");

        Assert.Equal(1, signals);
        Assert.Equal("token-def", updated.AccessToken);
    }

    /// <summary>A whitespace-only (or empty) committed token NEVER raises the signal.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n ")]
    public async Task CreateOrUpdateUserAsync_WhitespaceToken_DoesNotRaiseSignal(string token)
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var signals = 0;
        service.TokenAvailable += () => Interlocked.Increment(ref signals);

        var user = await SignInAsync(service, token);

        Assert.Equal(0, signals);
        // The commit itself is unaffected — only the SIGNAL is suppressed.
        Assert.Equal(token, user.AccessToken);
    }

    /// <summary>
    /// A throwing subscriber (e.g. an async connect failure surfacing synchronously) must NOT
    /// change <see cref="UserService.CreateOrUpdateUserAsync"/>'s behaviour: it still returns the
    /// committed user and never throws into the sign-in request.
    /// </summary>
    [Fact]
    public async Task CreateOrUpdateUserAsync_SubscriberThrows_SignInUnaffected()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        service.TokenAvailable += () => throw new InvalidOperationException("subscriber boom");

        var user = await SignInAsync(service, "token-abc");

        Assert.Equal("token-abc", user.AccessToken);
        Assert.Equal("admin", user.Role);
    }

    /// <summary>
    /// End-to-end signal wiring: a deferred coordinator subscribed to
    /// <see cref="UserService.TokenAvailable"/> connects on the first non-whitespace commit, and a
    /// whitespace commit never triggers a connect.
    /// </summary>
    [Fact]
    public async Task TokenSignal_DrivesDeferredCoordinator_WhitespaceCommitDoesNotConnect()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var connectCalls = 0;
        var tokenAvailable = false;
        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var coordinator = new LlmConnectionCoordinator(
            primaryModel: "gpt-5",
            compactionModel: "gpt-5",
            oauthEnabled: true,
            connectAsync: _ => { Interlocked.Increment(ref connectCalls); return Task.CompletedTask; },
            isTokenAvailable: () => tokenAvailable,
            providerOf: m => SharpCoder.Providers.ChatClientFactory.ParseProviderAndModel(m).Item1,
            logger: NullLogger<LlmConnectionCoordinator>.Instance);

        coordinator.StateChanged += s => { if (s == ComposerState.Connected) connected.TrySetResult(true); };
        coordinator.SubscribeTokenSignal(h => service.TokenAvailable += h, h => service.TokenAvailable -= h);

        await coordinator.StartAsync();
        Assert.Equal(ComposerState.PendingConnect, coordinator.State);

        // A whitespace commit never triggers a connect.
        await SignInAsync(service, "   ");
        Assert.Equal(0, connectCalls);
        Assert.Equal(ComposerState.PendingConnect, coordinator.State);

        // A real token does.
        tokenAvailable = true;
        await SignInAsync(service, "token-abc");

        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, connectCalls);
        Assert.Equal(ComposerState.Connected, coordinator.State);
    }
}

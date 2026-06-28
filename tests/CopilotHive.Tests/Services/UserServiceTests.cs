using CopilotHive.Persistence;
using CopilotHive.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Unit tests for <see cref="UserService"/> covering create/update, lookup, count, and
/// active-access-token retrieval against an in-memory SQLite database.
/// </summary>
public sealed class UserServiceTests
{
    /// <summary>
    /// An <see cref="IDbContextFactory{TContext}"/> that creates fresh contexts over a single
    /// shared, open SQLite in-memory connection. Disposing a created context does not destroy
    /// the database because the underlying connection stays open for the lifetime of the factory.
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

    [Fact]
    public async Task CreateOrUpdateUserAsync_CreatesNewUser()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var user = await service.CreateOrUpdateUserAsync(
            "12345", "octocat", "The Octocat", "https://avatar/octocat.png",
            "octocat@example.com", "token-abc", "refresh-xyz", "2030-01-01T00:00:00.0000000Z",
            CancellationToken.None);

        Assert.True(user.Id > 0);
        Assert.Equal("12345", user.GitHubId);
        Assert.Equal("octocat", user.Username);
        Assert.Equal("The Octocat", user.DisplayName);
        Assert.Equal("https://avatar/octocat.png", user.AvatarUrl);
        Assert.Equal("octocat@example.com", user.Email);
        Assert.Equal("token-abc", user.AccessToken);
        Assert.Equal("refresh-xyz", user.RefreshToken);
        Assert.Equal("2030-01-01T00:00:00.0000000Z", user.TokenExpiresAt);
        Assert.Equal("admin", user.Role);
        Assert.False(string.IsNullOrEmpty(user.CreatedAt));
        Assert.False(string.IsNullOrEmpty(user.LastLoginAt));
    }

    [Fact]
    public async Task CreateOrUpdateUserAsync_UpdatesExistingUser()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var created = await service.CreateOrUpdateUserAsync(
            "12345", "octocat", "The Octocat", null, null,
            "token-old", null, null, CancellationToken.None);
        var originalCreatedAt = created.CreatedAt;

        var updated = await service.CreateOrUpdateUserAsync(
            "12345", "octocat-renamed", "Renamed", "https://avatar/new.png",
            "new@example.com", "token-new", "refresh-new", null, CancellationToken.None);

        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("octocat-renamed", updated.Username);
        Assert.Equal("Renamed", updated.DisplayName);
        Assert.Equal("token-new", updated.AccessToken);
        Assert.Equal("refresh-new", updated.RefreshToken);
        Assert.Equal(originalCreatedAt, updated.CreatedAt);
        Assert.Equal(1, await service.GetUserCountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetByGitHubIdAsync_ReturnsUser_WhenExists()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await service.CreateOrUpdateUserAsync(
            "777", "user777", null, null, null, "tok", null, null, CancellationToken.None);

        var found = await service.GetByGitHubIdAsync("777", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("user777", found!.Username);
    }

    [Fact]
    public async Task GetByGitHubIdAsync_ReturnsNull_WhenNotExists()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var found = await service.GetByGitHubIdAsync("does-not-exist", CancellationToken.None);

        Assert.Null(found);
    }

    [Fact]
    public async Task GetAdminUserAsync_ReturnsFirstUser()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var first = await service.CreateOrUpdateUserAsync(
            "100", "first", null, null, null, "tok1", null, null, CancellationToken.None);
        await service.CreateOrUpdateUserAsync(
            "200", "second", null, null, null, "tok2", null, null, CancellationToken.None);

        var admin = await service.GetAdminUserAsync(CancellationToken.None);

        Assert.NotNull(admin);
        Assert.Equal(first.Id, admin!.Id);
        Assert.Equal("first", admin.Username);
    }

    [Fact]
    public async Task GetAdminUserAsync_ReturnsNull_WhenNoUsers()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var admin = await service.GetAdminUserAsync(CancellationToken.None);

        Assert.Null(admin);
    }

    [Fact]
    public async Task GetUserCountAsync_ReturnsZero_WhenEmpty()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        Assert.Equal(0, await service.GetUserCountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetUserCountAsync_ReturnsCorrectCount()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await service.CreateOrUpdateUserAsync(
            "1", "a", null, null, null, "t", null, null, CancellationToken.None);
        await service.CreateOrUpdateUserAsync(
            "2", "b", null, null, null, "t", null, null, CancellationToken.None);

        Assert.Equal(2, await service.GetUserCountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetActiveAccessTokenAsync_ReturnsToken_WhenUserExists()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        await service.CreateOrUpdateUserAsync(
            "55", "tokenuser", null, null, null, "active-token", null, null, CancellationToken.None);

        var token = await service.GetActiveAccessTokenAsync(CancellationToken.None);

        Assert.Equal("active-token", token);
    }

    [Fact]
    public async Task GetActiveAccessTokenAsync_ReturnsNull_WhenNoUsers()
    {
        var (service, factory) = CreateService();
        using var _ = factory;

        var token = await service.GetActiveAccessTokenAsync(CancellationToken.None);

        Assert.Null(token);
    }
}


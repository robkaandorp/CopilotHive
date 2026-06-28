using System.Globalization;

using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Services;

/// <summary>
/// Manages the single-user (admin) account backing GitHub OAuth authentication.
/// The first GitHub user to authenticate is stored as admin; subsequent users are rejected
/// by the authentication pipeline. The stored OAuth access token is exposed for use by the
/// AI chat clients in place of the <c>GH_TOKEN</c> environment variable.
/// </summary>
public sealed class UserService
{
    private readonly IDbContextFactory<CopilotHiveDbContext> _dbContextFactory;
    private readonly ILogger<UserService> _logger;

    /// <summary>
    /// Initialises a new <see cref="UserService"/>.
    /// </summary>
    /// <param name="dbContextFactory">Factory used to create transient <see cref="CopilotHiveDbContext"/> instances.</param>
    /// <param name="logger">Logger instance.</param>
    public UserService(IDbContextFactory<CopilotHiveDbContext> dbContextFactory, ILogger<UserService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <summary>Returns the user with the given GitHub ID, or null if none exists.</summary>
    public async Task<UserEntity?> GetByGitHubIdAsync(string githubId, CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.GitHubId == githubId, ct);
    }

    /// <summary>Returns the admin user (first by Id), or null if no users exist.</summary>
    public async Task<UserEntity?> GetAdminUserAsync(CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().OrderBy(u => u.Id).FirstOrDefaultAsync(ct);
    }

    /// <summary>Returns the number of users currently stored.</summary>
    public async Task<int> GetUserCountAsync(CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        return await db.Users.CountAsync(ct);
    }

    /// <summary>
    /// Creates a new user or updates the existing user with the same GitHub ID.
    /// On update, the profile fields, tokens and <see cref="UserEntity.LastLoginAt"/> are refreshed.
    /// On create, <see cref="UserEntity.CreatedAt"/> and <see cref="UserEntity.LastLoginAt"/> are set to now.
    /// </summary>
    public async Task<UserEntity> CreateOrUpdateUserAsync(
        string githubId,
        string username,
        string? displayName,
        string? avatarUrl,
        string? email,
        string accessToken,
        string? refreshToken,
        string? tokenExpiresAt,
        CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var nowIso = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var existing = await db.Users.FirstOrDefaultAsync(u => u.GitHubId == githubId, ct);

        if (existing is not null)
        {
            existing.Username = username;
            existing.DisplayName = displayName;
            existing.AvatarUrl = avatarUrl;
            existing.Email = email;
            existing.AccessToken = accessToken;
            existing.RefreshToken = refreshToken;
            existing.TokenExpiresAt = tokenExpiresAt;
            existing.LastLoginAt = nowIso;

            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Updated admin user '{Username}' (GitHub ID {GitHubId})", username, githubId);
            return existing;
        }

        var user = new UserEntity
        {
            GitHubId = githubId,
            Username = username,
            DisplayName = displayName,
            AvatarUrl = avatarUrl,
            Email = email,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            TokenExpiresAt = tokenExpiresAt,
            Role = "admin",
            CreatedAt = nowIso,
            LastLoginAt = nowIso,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Created admin user '{Username}' (GitHub ID {GitHubId})", username, githubId);
        return user;
    }

    /// <summary>
    /// Returns the admin user's OAuth access token, or null if no users exist.
    /// Used by <c>ChatClientFactory</c> to authenticate against the GitHub Copilot API.
    /// </summary>
    public async Task<string?> GetActiveAccessTokenAsync(CancellationToken ct)
    {
        var admin = await GetAdminUserAsync(ct);
        return admin?.AccessToken;
    }
}

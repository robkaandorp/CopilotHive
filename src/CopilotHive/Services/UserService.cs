using System.Data;
using System.Globalization;

using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Services;

/// <summary>
/// Owns the CopilotHive USER DIRECTORY: the activated OAuth accounts (<c>users</c>) and the separate
/// administrator invitations (<c>user_invitations</c>).
/// <para>
/// THE ORIGINAL/BOOTSTRAP ACCOUNT is the activated row with the LOWEST <see cref="UserEntity.Id"/>,
/// selected exactly as before by <see cref="GetAdminUserAsync"/>. That selector is independent of
/// role ordering and of the enabled flag: it stays the legacy OAuth admission identity and the
/// account whose stored token <see cref="GetActiveAccessTokenAsync"/> returns. Invitations live in a
/// different table, so they can never enter it, and an invited Admin never replaces it.
/// </para>
/// <para>
/// SCOPE. Every method here is a TRUSTED INTERNAL directory operation: it performs no authorization
/// check of its own and makes no public-authorization claim. Callers (facades, endpoints, UI) are
/// responsible for admin authorization, which a later slice adds; this type deliberately does not
/// pretend to provide it.
/// </para>
/// <para>
/// TIMESTAMPS. Every new timestamp comes from the injected <see cref="TimeProvider"/> (default
/// <see cref="TimeProvider.System"/>), written as the same invariant ISO 8601 round-trip text the
/// existing columns already use, so tests are deterministic and stored rows stay format-compatible.
/// </para>
/// </summary>
public sealed class UserService
{
    /// <summary>
    /// The single, stable refusal text for an activation attempt made before the original account
    /// exists. Fresh installations may hold invitations, but the bootstrap admin must sign in first.
    /// </summary>
    internal const string ActivationRequiresBootstrapMessage =
        "Cannot activate an invitation: no activated account exists yet. " +
        "The original (bootstrap) administrator must first sign in through OAuth.";

    private readonly IDbContextFactory<CopilotHiveDbContext> _dbContextFactory;
    private readonly ILogger<UserService> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initialises a new <see cref="UserService"/>.
    /// </summary>
    /// <param name="dbContextFactory">Factory used to create transient <see cref="CopilotHiveDbContext"/> instances.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="timeProvider">Clock for the new timestamps; defaults to <see cref="TimeProvider.System"/>.</param>
    public UserService(
        IDbContextFactory<CopilotHiveDbContext> dbContextFactory,
        ILogger<UserService> logger,
        TimeProvider? timeProvider = null)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Returns the activated user with the given GitHub numeric ID, or null if none exists.</summary>
    public async Task<UserEntity?> GetByGitHubIdAsync(string githubId, CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.GitHubId == githubId, ct);
    }

    /// <summary>
    /// Returns the ORIGINAL/bootstrap activated account (lowest <see cref="UserEntity.Id"/>), or null
    /// when no account exists yet.
    /// <para>
    /// This selector is the legacy OAuth admission identity and the account whose token
    /// <see cref="GetActiveAccessTokenAsync"/> returns. It is deliberately independent of role and of
    /// the enabled flag: demoting or disabling the original account never re-routes the stored token
    /// and never admits a fallback identity. Pending invitations are a different table and can never
    /// appear here.
    /// </para>
    /// </summary>
    public async Task<UserEntity?> GetAdminUserAsync(CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().OrderBy(u => u.Id).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Returns the number of ACTIVATED accounts. Pending invitations live in a separate table and are
    /// never counted, so a pending Admin invitation never makes this non-zero.
    /// </summary>
    public async Task<int> GetUserCountAsync(CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        return await db.Users.CountAsync(ct);
    }

    /// <summary>
    /// Raised after a GitHub OAuth access token has been committed to the database, and ONLY when
    /// the committed token is non-whitespace — a whitespace commit never signals availability.
    /// <para>
    /// Delivery is fire-and-forget: <see cref="CreateOrUpdateUserAsync"/> swallows every handler
    /// exception, so its behaviour never changes based on signal delivery (in particular a failed
    /// deferred Composer connect is never thrown into the sign-in request).
    /// </para>
    /// </summary>
    public event Action? TokenAvailable;

    /// <summary>
    /// Publishes <see cref="TokenAvailable"/> when the committed token is non-whitespace.
    /// Every subscriber exception is caught and logged — signal delivery must not change the
    /// outcome of the sign-in request.
    /// </summary>
    private void RaiseTokenAvailable(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return;

        try
        {
            TokenAvailable?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Token-available signal subscriber threw; sign-in is unaffected");
        }
    }

    /// <summary>
    /// THE MANDATORY ATOMIC BOOTSTRAP SEAM used by the OAuth <c>OnCreatingTicket</c> handler.
    /// <para>
    /// The caller reads <see cref="GetUserCountAsync"/> SEPARATELY, so that read cannot be the guard:
    /// the guard lives here, inside ONE immediate database transaction. The FIRST identity to reach
    /// this method creates the single bootstrap account (<see cref="UserRole.Admin"/>, enabled) and is
    /// the only identity that can ever be created here. Any later DISTINCT identity is REJECTED with a
    /// <see cref="UserDirectoryException"/> and is never inserted, and the original identity is
    /// refreshed in place — its id, credentials and history preserved.
    /// </para>
    /// <para>
    /// If the winning bootstrap login matches an ENABLED, still-pending invitation, that invitation is
    /// atomically consumed (marked activated with the new account's id and timestamp) INSIDE the same
    /// transaction. The bootstrap account keeps <see cref="UserRole.Admin"/> regardless of the
    /// invitation's role, so a matching Viewer/Member invitation can never demote the original admin.
    /// A matching invitation that is revoked or already activated is NOT consumed: the conflict is
    /// reported in the outcome and logged, and nothing about it is changed.
    /// </para>
    /// <para>
    /// After the commit, <see cref="TokenAvailable"/> is raised when (and only when) the committed
    /// token is non-whitespace.
    /// </para>
    /// </summary>
    /// <returns>The committed account.</returns>
    /// <exception cref="UserDirectoryException">A distinct identity attempted to sign in after bootstrap.</exception>
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
        var outcome = await BootstrapOrRefreshAsync(
            githubId, username, displayName, avatarUrl, email,
            accessToken, refreshToken, tokenExpiresAt, ct);

        return outcome.User;
    }

    /// <summary>
    /// The bootstrap seam with its full outcome exposed (the public
    /// <see cref="CreateOrUpdateUserAsync"/> returns just the account for the OAuth handler).
    /// </summary>
    /// <param name="githubId">GitHub numeric id of the signing-in identity.</param>
    /// <param name="username">GitHub login of the signing-in identity.</param>
    /// <param name="displayName">Optional display name.</param>
    /// <param name="avatarUrl">Optional avatar URL.</param>
    /// <param name="email">Optional email.</param>
    /// <param name="accessToken">The OAuth access token to commit.</param>
    /// <param name="refreshToken">Optional OAuth refresh token.</param>
    /// <param name="tokenExpiresAt">Optional token expiry.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task<UserBootstrapOutcome> BootstrapOrRefreshAsync(
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
        var nowIso = NowIso();
        var normalizedUsername = UserNormalization.TryNormalize(username);
        var storedUsername = username.Trim();

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        await using var tx = await BeginImmediateTransactionAsync(db, ct);

        // THE ORIGINAL ACCOUNT: lowest id, regardless of role or enabled state. Read INSIDE the
        // immediate transaction, so a concurrent bootstrap either already committed (and is seen) or
        // is still waiting for this transaction's write lock.
        var originalId = await db.Users
            .AsNoTracking()
            .OrderBy(u => u.Id)
            .Select(u => (int?)u.Id)
            .FirstOrDefaultAsync(ct);

        UserEntity user;
        bool created;
        UserInvitationEntity? consumedInvitation = null;
        UserInvitationEntity? conflictingInvitation = null;
        UsernameConflict? usernameConflict = null;

        if (originalId is null)
        {
            // THE WINNING FIRST IDENTITY: exactly one bootstrap account, always an enabled Admin.
            user = new UserEntity
            {
                GitHubId = githubId,
                // Trimmed BEFORE persistence (see UserNormalization): a stored " OctoCat " would
                // otherwise bypass every normalized lookup/conflict check.
                Username = storedUsername,
                DisplayName = displayName,
                AvatarUrl = avatarUrl,
                Email = email,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                TokenExpiresAt = tokenExpiresAt,
                Role = UserRole.Admin,
                Enabled = true,
                CreatedAt = nowIso,
                LastLoginAt = nowIso,
            };

            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            created = true;

            // MATCHING PENDING INVITATION CONSUMPTION — inside the SAME transaction as the insert.
            if (normalizedUsername is not null)
            {
                var invitation = await db.UserInvitations
                    .FirstOrDefaultAsync(i => i.NormalizedUsername == normalizedUsername, ct);

                if (invitation is not null)
                {
                    if (invitation.Enabled && invitation.ActivatedUserId is null)
                    {
                        // Consumed, but the account role is NOT taken from the invitation: the
                        // bootstrap account stays Admin no matter what the invitation asked for.
                        invitation.ActivatedUserId = user.Id;
                        invitation.ActivatedAt = nowIso;
                        invitation.UpdatedAt = nowIso;
                        await db.SaveChangesAsync(ct);
                        consumedInvitation = invitation;
                    }
                    else
                    {
                        // NEVER consumed silently: a revoked or already-activated invitation is
                        // reported and left exactly as it was.
                        conflictingInvitation = invitation;
                    }
                }
            }
        }
        else
        {
            // AFTER BOOTSTRAP: only the ORIGINAL identity may be refreshed here. A distinct identity
            // is rejected — never inserted — so a second identity can never become an account
            // through this seam.
            var original = await db.Users.FirstOrDefaultAsync(u => u.Id == originalId.Value, ct)
                ?? throw new UserDirectoryException(
                    $"The original activated account (id {originalId.Value}) could not be read back inside its own transaction.");

            if (!string.Equals(original.GitHubId, githubId, StringComparison.Ordinal))
            {
                throw new UserDirectoryException(
                    $"Only the original activated account may sign in; GitHub id '{githubId}' is not the bootstrap identity " +
                    $"(id {original.Id}). Invited users are activated through the invitation directory, not through sign-in.");
            }

            // THE ORIGINAL ACCOUNT'S SIGN-IN IS ANCHORED TO ITS IMMUTABLE GITHUB ID, never to its
            // mutable login. A rename whose normalized login already belongs to ANOTHER activated
            // account (or is reserved by another invitation) must not block the sign-in or the token
            // refresh, and must not create a second account for one normalized login: the original's
            // EXISTING unique username is preserved, everything else is refreshed, and the conflict
            // is reported on the outcome and in the log. Nothing throws and nothing is consumed.
            if (normalizedUsername is not null
                && !UserNormalization.NormalizedEquals(original.Username, storedUsername))
            {
                usernameConflict = await FindUsernameConflictAsync(
                    db, original.Id, storedUsername, normalizedUsername, ct);
            }

            if (usernameConflict is null)
            {
                original.Username = storedUsername;
            }

            original.DisplayName = displayName;
            original.AvatarUrl = avatarUrl;
            original.Email = email;
            original.AccessToken = accessToken;
            original.RefreshToken = refreshToken;
            original.TokenExpiresAt = tokenExpiresAt;
            original.LastLoginAt = nowIso;

            // Role and enabled state are deliberately NOT touched: sign-in must never re-promote or
            // re-enable an account an administrator changed.
            await db.SaveChangesAsync(ct);
            user = original;
            created = false;
        }

        await tx.CommitAsync(ct);

        if (created)
        {
            _logger.LogInformation("Created bootstrap admin user '{Username}' (GitHub ID {GitHubId})", username, githubId);
        }
        else
        {
            _logger.LogInformation("Updated admin user '{Username}' (GitHub ID {GitHubId})", username, githubId);
        }

        if (consumedInvitation is not null)
        {
            _logger.LogInformation(
                "Consumed pending invitation '{NormalizedUsername}' (invitation role {InvitationRole}) into the bootstrap account {UserId}, which stays Admin",
                consumedInvitation.NormalizedUsername, consumedInvitation.Role, user.Id);
        }

        if (conflictingInvitation is not null)
        {
            _logger.LogWarning(
                "Matching invitation '{NormalizedUsername}' was NOT consumed for the bootstrap account {UserId}: it is {State}. The invitation was left unchanged.",
                conflictingInvitation.NormalizedUsername,
                user.Id,
                conflictingInvitation.ActivatedUserId is not null
                    ? $"already activated into user {conflictingInvitation.ActivatedUserId}"
                    : "revoked");
        }

        if (usernameConflict is not null)
        {
            _logger.LogWarning(
                "Original account {UserId} signed in with login '{RequestedUsername}', which is already taken by {Holder}. " +
                "The account keeps its existing username '{ExistingUsername}'; the profile and token were refreshed.",
                user.Id,
                usernameConflict.RequestedUsername,
                usernameConflict.ConflictingUserId is not null
                    ? $"activated account {usernameConflict.ConflictingUserId}"
                    : $"invitation {usernameConflict.ConflictingInvitationId}",
                user.Username);
        }

        RaiseTokenAvailable(accessToken);

        return new UserBootstrapOutcome(user, created, consumedInvitation, conflictingInvitation, usernameConflict);
    }

    /// <summary>
    /// Returns the ORIGINAL/bootstrap account's OAuth access token, or null if no account exists.
    /// Used by <c>ChatClientFactory</c> to authenticate against the GitHub Copilot API.
    /// <para>
    /// Deliberately UNCHANGED in behaviour by the multi-user foundation: it reads the lowest-id
    /// activated account's stored credential, so demoting, disabling or revoking anything else can
    /// never re-route the legacy global token, and an invited Admin never becomes the fallback.
    /// </para>
    /// </summary>
    public async Task<string?> GetActiveAccessTokenAsync(CancellationToken ct)
    {
        var admin = await GetAdminUserAsync(ct);
        return admin?.AccessToken;
    }

    // ── Secret-free directory listings ───────────────────────────────────────

    /// <summary>
    /// THE SINGLE activated-account projection. It names only non-credential columns, so no listing or
    /// resolver can select a token — the secret-free property is enforced once, in one place.
    /// </summary>
    private static readonly System.Linq.Expressions.Expression<Func<UserEntity, ActivatedUserSummary>>
        ActivatedUserProjection = u => new ActivatedUserSummary(
            u.Id,
            u.GitHubId,
            u.Username,
            u.DisplayName,
            u.AvatarUrl,
            u.Email,
            u.Role,
            u.Enabled,
            u.CreatedAt,
            u.LastLoginAt);

    /// <summary>
    /// Lists every ACTIVATED account, ordered by internal id (so the bootstrap account is first), as a
    /// SECRET-FREE projection: no access token, refresh token or expiry is ever selected.
    /// </summary>
    internal async Task<IReadOnlyList<ActivatedUserSummary>> ListActivatedUsersAsync(CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        return await db.Users
            .AsNoTracking()
            .OrderBy(u => u.Id)
            .Select(ActivatedUserProjection)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Lists every administrator invitation (pending, activated and revoked), ordered by internal id,
    /// as a SECRET-FREE projection. Invitations never hold credentials at all.
    /// </summary>
    internal async Task<IReadOnlyList<UserInvitationSummary>> ListInvitationsAsync(CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        return await ProjectInvitationSummariesAsync(db.UserInvitations.AsNoTracking(), ct);
    }

    /// <summary>Resolves one activated account by its internal id, or null when no such account exists.</summary>
    /// <param name="userId">The internal account id.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task<ActivatedUserSummary?> GetActivatedUserByIdAsync(int userId, CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        return await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(ActivatedUserProjection)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Resolves one activated account by GITHUB IDENTITY: the login is normalized exactly as the
    /// invitation directory normalizes it, so <c>"OctoCat"</c> and <c>" octocat "</c> resolve to the
    /// same account.
    /// </summary>
    /// <param name="username">The GitHub login to resolve.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task<ActivatedUserSummary?> GetActivatedUserByUsernameAsync(string username, CancellationToken ct)
    {
        var normalized = UserNormalization.Normalize(username, nameof(username));

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        // Compared with the SAME trim + case-fold the invitation directory uses, applied to the STORED
        // value too, so a legacy row stored verbatim (" OctoCat ") still resolves as "octocat". The
        // users table is tiny, and comparing in C# keeps the rule identical to UserNormalization.
        var ids = await FindAccountIdsByNormalizedUsernameAsync(db, normalized, ct);
        int? id = ids.Count == 0 ? null : ids[0];

        return id is null ? null : await GetActivatedUserByIdAsync(id.Value, ct);
    }

    /// <summary>Resolves one invitation by its normalized GitHub username, or null when absent.</summary>
    /// <param name="username">The GitHub login the invitation is for.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task<UserInvitationSummary?> GetInvitationByUsernameAsync(string username, CancellationToken ct)
    {
        var normalized = UserNormalization.Normalize(username, nameof(username));

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var matches = await ProjectInvitationSummariesAsync(
            db.UserInvitations.AsNoTracking().Where(i => i.NormalizedUsername == normalized), ct);

        return matches.FirstOrDefault();
    }

    // ── Invitation directory operations ─────────────────────────────────────

    /// <summary>
    /// Invites one NORMALIZED GitHub username with the given role.
    /// <para>
    /// Duplicate and conflicting identities are refused explicitly: an existing invitation for the
    /// same normalized login (in ANY state — a revoked invitation is re-enabled, never duplicated) and
    /// an already-activated account for the same login are both rejected. There is no matching-bootstrap
    /// exception here — that single exception belongs to
    /// <see cref="BootstrapOrRefreshAsync"/>.
    /// </para>
    /// </summary>
    /// <param name="username">The GitHub login to invite.</param>
    /// <param name="role">The role the activated account will carry.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created invitation summary.</returns>
    /// <exception cref="UserDirectoryException">A duplicate or conflicting identity already exists.</exception>
    internal async Task<UserInvitationSummary> InviteUserAsync(string username, UserRole role, CancellationToken ct)
    {
        var normalized = UserNormalization.Normalize(username, nameof(username));
        UserRoleText.EnsureDefined(role, nameof(role));

        var nowIso = NowIso();

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        await using var tx = await BeginImmediateTransactionAsync(db, ct);

        var existingInvitation = await db.UserInvitations
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.NormalizedUsername == normalized, ct);

        if (existingInvitation is not null)
        {
            throw new UserDirectoryException(
                existingInvitation.ActivatedUserId is not null
                    ? $"Cannot invite '{normalized}': the invitation was already activated into user {existingInvitation.ActivatedUserId}."
                    : $"Cannot invite '{normalized}': an invitation already exists (enabled: {existingInvitation.Enabled}). " +
                      "Change its role, revoke it, or re-enable it instead of creating a duplicate.");
        }

        if (await NormalizedAccountExistsAsync(db, normalized, ct))
        {
            throw new UserDirectoryException(
                $"Cannot invite '{normalized}': an activated account already exists for that GitHub login.");
        }

        var invitation = new UserInvitationEntity
        {
            NormalizedUsername = normalized,
            Role = role,
            Enabled = true,
            CreatedAt = nowIso,
            UpdatedAt = nowIso,
            RevokedAt = null,
            ActivatedUserId = null,
            ActivatedAt = null,
        };

        db.UserInvitations.Add(invitation);
        await SaveWithDuplicateGuardAsync(db, normalized, ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation("Invited '{NormalizedUsername}' with role {Role}", normalized, role);
        return UserInvitationSummary.FromEntity(invitation);
    }

    /// <summary>
    /// Changes the ROLE carried by a still-pending invitation. An activated invitation's role is
    /// history and is refused: the activated account's role is changed through
    /// <see cref="ChangeUserRoleAsync"/> instead.
    /// </summary>
    /// <param name="username">The GitHub login the invitation is for.</param>
    /// <param name="role">The new role.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated invitation summary.</returns>
    /// <exception cref="UserDirectoryException">No invitation exists, it is already activated, or the role is unchanged.</exception>
    internal async Task<UserInvitationSummary> ChangeInvitationRoleAsync(string username, UserRole role, CancellationToken ct)
    {
        var normalized = UserNormalization.Normalize(username, nameof(username));
        UserRoleText.EnsureDefined(role, nameof(role));

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        await using var tx = await BeginImmediateTransactionAsync(db, ct);

        var invitation = await RequireInvitationAsync(db, normalized, ct);

        if (invitation.ActivatedUserId is not null)
        {
            throw new UserDirectoryException(
                $"Cannot change the role of the invitation for '{normalized}': it was already activated into " +
                $"user {invitation.ActivatedUserId}. Change the activated account's role instead.");
        }

        if (invitation.Role == role)
        {
            throw new UserDirectoryException(
                $"The invitation for '{normalized}' already carries role '{UserRoleText.Format(role)}'.");
        }

        var previous = invitation.Role;
        invitation.Role = role;
        invitation.UpdatedAt = NowIso();

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Changed invitation '{NormalizedUsername}' role {PreviousRole} -> {Role}", normalized, previous, role);
        return UserInvitationSummary.FromEntity(invitation);
    }

    /// <summary>
    /// Revokes (disables) a pending invitation. Revocation KEEPS the row and its history; it never
    /// deletes it, so <see cref="EnableInvitationAsync"/> can restore it.
    /// </summary>
    /// <param name="username">The GitHub login the invitation is for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated invitation summary.</returns>
    /// <exception cref="UserDirectoryException">No invitation exists, it is already revoked, or it is already activated.</exception>
    internal async Task<UserInvitationSummary> RevokeInvitationAsync(string username, CancellationToken ct)
    {
        var normalized = UserNormalization.Normalize(username, nameof(username));
        var nowIso = NowIso();

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        await using var tx = await BeginImmediateTransactionAsync(db, ct);

        var invitation = await RequireInvitationAsync(db, normalized, ct);

        if (invitation.ActivatedUserId is not null)
        {
            throw new UserDirectoryException(
                $"Cannot revoke the invitation for '{normalized}': it was already activated into user " +
                $"{invitation.ActivatedUserId}. Disable that account instead.");
        }

        if (!invitation.Enabled)
        {
            throw new UserDirectoryException($"The invitation for '{normalized}' is already revoked.");
        }

        invitation.Enabled = false;
        invitation.RevokedAt = nowIso;
        invitation.UpdatedAt = nowIso;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation("Revoked invitation '{NormalizedUsername}'", normalized);
        return UserInvitationSummary.FromEntity(invitation);
    }

    /// <summary>
    /// Re-enables a REVOKED, not-yet-activated invitation.
    /// </summary>
    /// <param name="username">The GitHub login the invitation is for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated invitation summary.</returns>
    /// <exception cref="UserDirectoryException">No invitation exists, it is already enabled, or it is already activated.</exception>
    internal async Task<UserInvitationSummary> EnableInvitationAsync(string username, CancellationToken ct)
    {
        var normalized = UserNormalization.Normalize(username, nameof(username));

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        await using var tx = await BeginImmediateTransactionAsync(db, ct);

        var invitation = await RequireInvitationAsync(db, normalized, ct);

        if (invitation.ActivatedUserId is not null)
        {
            throw new UserDirectoryException(
                $"Cannot enable the invitation for '{normalized}': it was already activated into user " +
                $"{invitation.ActivatedUserId}.");
        }

        if (invitation.Enabled)
        {
            throw new UserDirectoryException($"The invitation for '{normalized}' is already enabled.");
        }

        invitation.Enabled = true;
        invitation.RevokedAt = null;
        invitation.UpdatedAt = NowIso();

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation("Re-enabled invitation '{NormalizedUsername}'", normalized);
        return UserInvitationSummary.FromEntity(invitation);
    }

    // ── Activated-account directory operations ───────────────────────────────

    /// <summary>
    /// Changes an ACTIVATED account's role.
    /// <para>
    /// THE LAST-ADMIN PROTECTION: the last enabled Admin can never be demoted. A PENDING Admin
    /// invitation does NOT satisfy the protection — it lives in a different table and is not an
    /// activated Admin — so demoting the only enabled Admin while an Admin invitation is pending is
    /// refused.
    /// </para>
    /// </summary>
    /// <param name="userId">The internal account id.</param>
    /// <param name="role">The new role.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated account summary.</returns>
    /// <exception cref="UserDirectoryException">No such account, the role is unchanged, or the last enabled Admin would be demoted.</exception>
    internal async Task<ActivatedUserSummary> ChangeUserRoleAsync(int userId, UserRole role, CancellationToken ct)
    {
        UserRoleText.EnsureDefined(role, nameof(role));

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        await using var tx = await BeginImmediateTransactionAsync(db, ct);

        var user = await RequireActivatedUserAsync(db, userId, ct);

        if (user.Role == role)
        {
            throw new UserDirectoryException(
                $"Activated account {userId} already carries role '{UserRoleText.Format(role)}'.");
        }

        if (user.Role == UserRole.Admin && user.Enabled)
        {
            await EnsureNotLastEnabledAdminAsync(db, userId, "demoted", ct);
        }

        var previous = user.Role;
        user.Role = role;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation("Changed activated account {UserId} role {PreviousRole} -> {Role}", userId, previous, role);
        return ActivatedUserSummary.FromEntity(user);
    }

    /// <summary>
    /// Enables or disables an ACTIVATED account. Disabling retains the row, its credentials and its
    /// history (active-session revocation is explicitly out of scope for this slice).
    /// <para>
    /// THE LAST-ADMIN PROTECTION: the last enabled Admin can never be disabled, and a pending Admin
    /// invitation does NOT satisfy the protection.
    /// </para>
    /// <para>
    /// Disabling (or demoting) the ORIGINAL/bootstrap account never re-routes its legacy token:
    /// <see cref="GetActiveAccessTokenAsync"/> keeps selecting the lowest-id account and never falls
    /// back to another identity.
    /// </para>
    /// </summary>
    /// <param name="userId">The internal account id.</param>
    /// <param name="enabled">Whether the account should be enabled.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated account summary.</returns>
    /// <exception cref="UserDirectoryException">No such account, the state is unchanged, or the last enabled Admin would be disabled.</exception>
    internal async Task<ActivatedUserSummary> SetUserEnabledAsync(int userId, bool enabled, CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        await using var tx = await BeginImmediateTransactionAsync(db, ct);

        var user = await RequireActivatedUserAsync(db, userId, ct);

        if (user.Enabled == enabled)
        {
            throw new UserDirectoryException(
                $"Activated account {userId} is already {(enabled ? "enabled" : "disabled")}.");
        }

        if (!enabled && user.Role == UserRole.Admin)
        {
            await EnsureNotLastEnabledAdminAsync(db, userId, "disabled", ct);
        }

        user.Enabled = enabled;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Activated account {UserId} {State}", userId, enabled ? "enabled" : "disabled");
        return ActivatedUserSummary.FromEntity(user);
    }

    // ── Invitation activation ───────────────────────────────────────────────

    /// <summary>
    /// Atomically ACTIVATES one eligible invitation into EXACTLY ONE activated account carrying the
    /// invitation's assigned role, and records the activated account id and timestamp on the
    /// invitation.
    /// <para>
    /// ACTIVATION REQUIRES THE BOOTSTRAP ACCOUNT. A fresh installation may hold invitations, but the
    /// original admin must sign in through OAuth first: with no activated account the operation fails
    /// with <see cref="ActivationRequiresBootstrapMessage"/>. Because the check and the insert run
    /// inside one immediate transaction, a concurrent bootstrap cannot produce an invited account that
    /// outranks (or becomes) the original: either the bootstrap commits first and this activation gets
    /// a HIGHER id, or this activation refuses.
    /// </para>
    /// <para>
    /// Refusals (all committed as nothing): no invitation, revoked invitation, already-activated
    /// invitation, an activated account already holding the same GitHub id or login.
    /// </para>
    /// </summary>
    /// <param name="username">The invited GitHub login.</param>
    /// <param name="githubId">The GitHub numeric id of the identity being activated.</param>
    /// <param name="displayName">Optional display name.</param>
    /// <param name="avatarUrl">Optional avatar URL.</param>
    /// <param name="email">Optional email.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly activated account as a secret-free summary.</returns>
    /// <exception cref="UserDirectoryException">The invitation is missing, ineligible, or conflicts with an existing account.</exception>
    internal async Task<ActivatedUserSummary> ActivateInvitationAsync(
        string username,
        string githubId,
        string? displayName,
        string? avatarUrl,
        string? email,
        CancellationToken ct)
    {
        var normalized = UserNormalization.Normalize(username, nameof(username));
        ArgumentException.ThrowIfNullOrWhiteSpace(githubId);

        var nowIso = NowIso();

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        await using var tx = await BeginImmediateTransactionAsync(db, ct);

        var hasActivatedAccount = await db.Users.AsNoTracking().AnyAsync(ct);
        if (!hasActivatedAccount)
        {
            throw new UserDirectoryException(ActivationRequiresBootstrapMessage);
        }

        var invitation = await db.UserInvitations
            .FirstOrDefaultAsync(i => i.NormalizedUsername == normalized, ct)
            ?? throw new UserDirectoryException($"No invitation exists for GitHub username '{normalized}'.");

        if (invitation.ActivatedUserId is not null)
        {
            throw new UserDirectoryException(
                $"The invitation for '{normalized}' was already activated into user {invitation.ActivatedUserId}.");
        }

        if (!invitation.Enabled)
        {
            throw new UserDirectoryException(
                $"The invitation for '{normalized}' is revoked; re-enable it before activation.");
        }

        if (await db.Users.AsNoTracking().AnyAsync(u => u.GitHubId == githubId, ct))
        {
            throw new UserDirectoryException(
                $"An activated account already exists for GitHub id '{githubId}'.");
        }

        if (await NormalizedAccountExistsAsync(db, normalized, ct))
        {
            throw new UserDirectoryException(
                $"An activated account already exists for GitHub login '{normalized}'.");
        }

        var user = new UserEntity
        {
            GitHubId = githubId,
            // The account keeps the login's real casing; identity comparisons everywhere use the
            // normalized form.
            Username = username.Trim(),
            DisplayName = displayName,
            AvatarUrl = avatarUrl,
            Email = email,
            // Invitation activation is a DIRECTORY operation, not an OAuth sign-in: no credentials
            // are minted or copied here.
            AccessToken = string.Empty,
            RefreshToken = null,
            TokenExpiresAt = null,
            Role = invitation.Role,
            Enabled = true,
            CreatedAt = nowIso,
            LastLoginAt = null,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        invitation.ActivatedUserId = user.Id;
        invitation.ActivatedAt = nowIso;
        invitation.UpdatedAt = nowIso;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Activated invitation '{NormalizedUsername}' into user {UserId} with role {Role}",
            normalized, user.Id, invitation.Role);

        return ActivatedUserSummary.FromEntity(user);
    }

    // ── Internals ───────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the write transaction every mutating directory operation runs inside.
    /// <para>
    /// <see cref="IsolationLevel.Serializable"/> makes Microsoft.Data.Sqlite issue a
    /// <c>BEGIN IMMEDIATE</c>: the write lock is taken BEFORE the guard reads, so two service instances
    /// on separate connections to one database file SERIALIZE instead of both observing the pre-write
    /// state. That is what makes bootstrap, invitation consumption, activation, duplicate detection and
    /// the last-admin check atomic at the database boundary rather than read-check-write races.
    /// </para>
    /// <para>
    /// The level is declared EXPLICITLY rather than left to a provider default, and must stay a level
    /// that maps to an immediate begin. Measured behaviour of Microsoft.Data.Sqlite: Serializable,
    /// ReadCommitted and RepeatableRead all begin IMMEDIATE; only
    /// <see cref="IsolationLevel.ReadUncommitted"/> begins DEFERRED, which would silently turn every
    /// guard below into a read-check-write race. That weakening is caught by the concurrency suite —
    /// switching this transaction to a deferred begin makes the bootstrap, invitation-consumption,
    /// activation and last-admin protection tests fail — so a regression here is loud, not quiet.
    /// </para>
    /// </summary>
    private static Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginImmediateTransactionAsync(
        CopilotHiveDbContext db,
        CancellationToken ct)
        => db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

    private string NowIso() =>
        _timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static async Task<IReadOnlyList<UserInvitationSummary>> ProjectInvitationSummariesAsync(
        IQueryable<UserInvitationEntity> query,
        CancellationToken ct)
        => await query
            .OrderBy(i => i.Id)
            .Select(i => new UserInvitationSummary(
                i.Id,
                i.NormalizedUsername,
                i.Role,
                i.Enabled,
                i.CreatedAt,
                i.UpdatedAt,
                i.RevokedAt,
                i.ActivatedUserId,
                i.ActivatedAt))
            .ToListAsync(ct);

    /// <summary>
    /// The ids (ascending, so the lowest id is first) of the ACTIVATED accounts whose stored login
    /// normalizes to <paramref name="normalizedUsername"/>. The normalization is applied to the STORED
    /// value as well as the probe, so a legacy row stored with surrounding whitespace or different
    /// casing cannot hide behind its verbatim spelling.
    /// </summary>
    private static async Task<List<int>> FindAccountIdsByNormalizedUsernameAsync(
        CopilotHiveDbContext db,
        string normalizedUsername,
        CancellationToken ct)
    {
        var rows = await db.Users
            .AsNoTracking()
            .OrderBy(u => u.Id)
            .Select(u => new { u.Id, u.Username })
            .ToListAsync(ct);

        return rows
            .Where(r => UserNormalization.NormalizedEquals(r.Username, normalizedUsername))
            .Select(r => r.Id)
            .ToList();
    }

    /// <summary>
    /// Whether an ACTIVATED account already holds this normalized GitHub login (see
    /// <see cref="FindAccountIdsByNormalizedUsernameAsync"/>).
    /// </summary>
    private static async Task<bool> NormalizedAccountExistsAsync(
        CopilotHiveDbContext db,
        string normalizedUsername,
        CancellationToken ct)
        => (await FindAccountIdsByNormalizedUsernameAsync(db, normalizedUsername, ct)).Count > 0;

    /// <summary>
    /// Finds who else already owns the normalized login the original account is renaming to: another
    /// ACTIVATED account, or an invitation that is not the original's own consumed one. An invitation
    /// in ANY state counts (a revoked invitation can be re-enabled, and activating it would then collide
    /// with the renamed account). Runs inside the caller's write transaction.
    /// </summary>
    private static async Task<UsernameConflict?> FindUsernameConflictAsync(
        CopilotHiveDbContext db,
        int originalUserId,
        string requestedUsername,
        string normalizedUsername,
        CancellationToken ct)
    {
        var otherAccountIds = (await FindAccountIdsByNormalizedUsernameAsync(db, normalizedUsername, ct))
            .Where(id => id != originalUserId)
            .ToList();

        if (otherAccountIds.Count > 0)
        {
            return new UsernameConflict(requestedUsername, otherAccountIds[0], null);
        }

        var invitation = await db.UserInvitations
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.NormalizedUsername == normalizedUsername, ct);

        // The original's OWN consumed invitation is its history, not a competing claim.
        if (invitation is not null && invitation.ActivatedUserId != originalUserId)
        {
            return new UsernameConflict(requestedUsername, null, invitation.Id);
        }

        return null;
    }

    private static async Task<UserInvitationEntity> RequireInvitationAsync(
        CopilotHiveDbContext db,
        string normalizedUsername,
        CancellationToken ct)
        => await db.UserInvitations.FirstOrDefaultAsync(i => i.NormalizedUsername == normalizedUsername, ct)
            ?? throw new UserDirectoryException(
                $"No invitation exists for GitHub username '{normalizedUsername}'.");

    private static async Task<UserEntity> RequireActivatedUserAsync(
        CopilotHiveDbContext db,
        int userId,
        CancellationToken ct)
        => await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UserDirectoryException(
                $"No activated account exists with internal id {userId}.");

    /// <summary>
    /// The last-enabled-Admin guard, evaluated INSIDE the caller's immediate transaction.
    /// <para>
    /// Only ENABLED accounts carrying <see cref="UserRole.Admin"/> count. Pending Admin invitations are
    /// stored in the separate <c>user_invitations</c> table and are therefore structurally incapable of
    /// satisfying this protection.
    /// </para>
    /// </summary>
    /// <param name="db">The caller's context, already inside the write transaction.</param>
    /// <param name="userId">The account being changed (excluded from the count).</param>
    /// <param name="action">The action name used in the refusal text ("demoted"/"disabled").</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task EnsureNotLastEnabledAdminAsync(
        CopilotHiveDbContext db,
        int userId,
        string action,
        CancellationToken ct)
    {
        var otherEnabledAdmins = await db.Users
            .AsNoTracking()
            .CountAsync(u => u.Id != userId && u.Role == UserRole.Admin && u.Enabled, ct);

        if (otherEnabledAdmins == 0)
        {
            throw new UserDirectoryException(
                $"Activated account {userId} cannot be {action}: it is the last enabled Admin. " +
                "A pending Admin invitation does not count as an enabled Admin.");
        }
    }

    /// <summary>
    /// Saves a newly inserted invitation, translating a UNIQUE-index violation (a concurrent
    /// insertion of the same normalized login) into the same explicit duplicate refusal the sequential
    /// path produces. The database, not a read-then-insert check, is the arbiter.
    /// <para>
    /// ONLY a constraint violation is translated: any other write failure propagates unchanged, so a
    /// storage problem is never reported as a duplicate-identity refusal.
    /// </para>
    /// </summary>
    private static async Task SaveWithDuplicateGuardAsync(
        CopilotHiveDbContext db,
        string normalizedUsername,
        CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            throw new UserDirectoryException(
                $"Cannot invite '{normalizedUsername}': an invitation for that GitHub login already exists.", ex);
        }
    }

    /// <summary>
    /// Whether an EF write failure was caused by a SQLite UNIQUE/PK constraint
    /// (<c>SQLITE_CONSTRAINT</c>). Anything else — a lock timeout, a schema mismatch — is NOT a
    /// duplicate identity and must not be reported as one.
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
                return true;
        }

        return false;
    }
}

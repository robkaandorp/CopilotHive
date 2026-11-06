using CopilotHive.Security;
using CopilotHive.Services;

namespace CopilotHive.Orchestration;

/// <summary>Why <see cref="ComposerRuntimeManager.GetOrCreateAsync"/> refused to yield a runtime. Every value is fail-closed.</summary>
public enum ComposerRuntimeRefusal
{
    /// <summary>No refusal — a runtime was produced.</summary>
    None = 0,

    /// <summary>The principal is the synthetic open-mode local operator (or carries no activated account id). Open mode stays legacy-only.</summary>
    LocalOperator = 1,

    /// <summary>The principal is disabled, or the account was disabled since the principal was resolved.</summary>
    DisabledAccount = 2,

    /// <summary>The principal's role, or the freshly stored role, is not a defined <see cref="UserRole"/>.</summary>
    UndefinedRole = 3,

    /// <summary>No activated account currently exists under the principal's id.</summary>
    UnknownAccount = 4,
}

/// <summary>
/// The outcome of one runtime request: either the user's shared <see cref="ComposerUserRuntime"/>
/// or a typed, secret-free refusal. A refusal carries NO runtime.
/// </summary>
public sealed record ComposerRuntimeResult
{
    private ComposerRuntimeResult(ComposerUserRuntime? runtime, ComposerRuntimeRefusal refusal)
    {
        Runtime = runtime;
        Refusal = refusal;
    }

    /// <summary>The shared runtime, or <c>null</c> when refused.</summary>
    public ComposerUserRuntime? Runtime { get; }

    /// <summary><see cref="ComposerRuntimeRefusal.None"/> when a runtime was produced; otherwise the reason.</summary>
    public ComposerRuntimeRefusal Refusal { get; }

    /// <summary>Whether a runtime was produced.</summary>
    public bool IsGranted => Runtime is not null;

    internal static ComposerRuntimeResult Granted(ComposerUserRuntime runtime) => new(runtime, ComposerRuntimeRefusal.None);

    internal static ComposerRuntimeResult Refused(ComposerRuntimeRefusal reason) => new(null, reason);
}

/// <summary>
/// The per-user pieces of one managed Composer: validated identity, the Composer (and through it the
/// actor / agent service), the per-user attachment service and the per-runtime event subscriber.
/// </summary>
public sealed class ComposerUserRuntime : IAsyncDisposable
{
    internal ComposerUserRuntime(
        ComposerUserState state, Composer composer, ComposerAttachmentService attachments, ComposerEventSubscriber? subscriber)
    {
        State = state;
        Composer = composer;
        Attachments = attachments;
        Subscriber = subscriber;
    }

    /// <summary>The validated per-user identity (session / attachment paths, <c>composer:N</c> registry id).</summary>
    public ComposerUserState State { get; }

    /// <summary>The user's Composer.</summary>
    public Composer Composer { get; }

    /// <summary>The user's own attachment service, rooted under the per-user attachments directory.</summary>
    public ComposerAttachmentService Attachments { get; }

    /// <summary>The user's own event subscriber, or <c>null</c> when no event bus is registered.</summary>
    public ComposerEventSubscriber? Subscriber { get; }

    /// <summary>Disposes the Composer, then detaches the subscriber. Session history on disk is never touched.</summary>
    public async ValueTask DisposeAsync()
    {
        try { await Composer.DisposeAsync(); }
        finally { Subscriber?.Dispose(); }
    }
}

/// <summary>
/// Creates, caches and re-validates one <see cref="ComposerUserRuntime"/> per ACTIVATED internal user
/// id. Slice scope: creation / cache / re-check only. Open mode stays legacy-only: the synthetic
/// local operator never yields a runtime here.
/// </summary>
/// <remarks>
/// SEAM for later slices (connect outcomes, caller-cancellation ownership, removal, shutdown): each
/// cache <see cref="Entry"/> owns ONE shared in-flight creation task that every concurrent caller of
/// the same user awaits; richer entry state (e.g. Removing) belongs on <see cref="Entry"/>.
/// </remarks>
public sealed class ComposerRuntimeManager
{
    private readonly ComposerFactory _factory;
    private readonly UserService _users;
    private readonly ILogger<ComposerRuntimeManager> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<int, Entry> _entries = [];

    /// <summary>Creates the manager.</summary>
    /// <param name="factory">The shared Composer construction seam.</param>
    /// <param name="users">The persisted user directory used for identity resolution and re-checks.</param>
    /// <param name="logger">Logger.</param>
    public ComposerRuntimeManager(ComposerFactory factory, UserService users, ILogger<ComposerRuntimeManager> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private sealed class Entry
    {
        public Task<ComposerUserRuntime> Creation { get; set; } = null!;
    }

    /// <summary>
    /// Returns the shared runtime for <paramref name="principal"/>'s account, creating it on first use.
    /// EVERY call — cache hit, creator and waiter on an in-flight creation alike — re-reads the stored
    /// account before returning and refuses when it is missing, disabled or role-invalid.
    /// </summary>
    /// <param name="principal">The acting principal (never trusted beyond its id; state is re-read).</param>
    /// <param name="ct">Cancels THIS caller's wait only; the shared creation is not cancelled by it.</param>
    /// <returns>A granted runtime, or a typed refusal.</returns>
    public async Task<ComposerRuntimeResult> GetOrCreateAsync(ActingUserPrincipal principal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.IsLocalOperator || principal.UserId <= 0)
            return ComposerRuntimeResult.Refused(ComposerRuntimeRefusal.LocalOperator);
        if (!principal.Enabled)
            return ComposerRuntimeResult.Refused(ComposerRuntimeRefusal.DisabledAccount);
        if (!IsDefinedRole(principal.Role))
            return ComposerRuntimeResult.Refused(ComposerRuntimeRefusal.UndefinedRole);

        var userId = principal.UserId;

        // A stale principal must not trigger creation (and legacy migration) for a since-disabled
        // account; callers joining an existing entry are re-checked after the await below instead.
        bool exists;
        lock (_gate) exists = _entries.ContainsKey(userId);
        if (!exists && await RecheckAsync(userId, ct) is { } early)
            return ComposerRuntimeResult.Refused(early);

        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(userId, out entry!))
            {
                entry = new Entry();
                _entries[userId] = entry;
                entry.Creation = Task.Run(() => CreateAsync(principal, entry), CancellationToken.None);
            }
        }

        var runtime = await entry.Creation.WaitAsync(ct);

        // Re-read AFTER the creation settled so a waiter whose account was disabled meanwhile is refused.
        return await RecheckAsync(userId, ct) is { } refusal
            ? ComposerRuntimeResult.Refused(refusal)
            : ComposerRuntimeResult.Granted(runtime);
    }

    /// <summary>Re-reads the stored account; <c>null</c> when it is still activated, enabled and role-valid.</summary>
    private async Task<ComposerRuntimeRefusal?> RecheckAsync(int userId, CancellationToken ct)
    {
        var fresh = await _users.GetActivatedUserByIdAsync(userId, ct);
        if (fresh is null || fresh.Id != userId)
            return ComposerRuntimeRefusal.UnknownAccount;
        if (!fresh.Enabled)
            return ComposerRuntimeRefusal.DisabledAccount;
        return IsDefinedRole(fresh.Role) ? null : ComposerRuntimeRefusal.UndefinedRole;
    }

    private static bool IsDefinedRole(UserRole role)
    {
        try
        {
            UserRoleText.EnsureDefined(role, nameof(role));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private async Task<ComposerUserRuntime> CreateAsync(ActingUserPrincipal principal, Entry entry)
    {
        ComposerUserState? state = null;
        ComposerAttachmentService? attachments = null;
        ComposerEventSubscriber? subscriber = null;
        Composer? composer = null;
        try
        {
            state = principal.IsOriginal
                ? await ComposerUserState.ResolveOriginalAdminAsync(_users, principal.UserId, CancellationToken.None)
                : await ComposerUserState.ResolveAsync(_users, principal.UserId, CancellationToken.None);
            attachments = _factory.CreateUserAttachments(state);
            subscriber = _factory.CreateUserSubscriber();
            composer = _factory.CreateForUser(state, attachments, subscriber);

            // A Composer without a resolved model is a disconnected shell: nothing to connect.
            // Migration / session load run inside ConnectAsync and fail closed (never delete history).
            if (composer.StartupDefaultModel is not null)
                await composer.ConnectAsync(CancellationToken.None);

            return new ComposerUserRuntime(state, composer, attachments, subscriber);
        }
        catch
        {
            // Dispose ONLY this attempt's partial pieces, then drop ONLY this attempt's cache entry;
            // history on disk is preserved. Cleanup failures never mask the original exception.
            if (composer is not null)
            {
                try { await composer.DisposeAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Disposing the partial Composer of user {UserId} failed", principal.UserId); }
            }

            try { subscriber?.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Disposing the partial subscriber of user {UserId} failed", principal.UserId); }

            if (attachments is not null)
            {
                try { await attachments.ClearAllAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Clearing the partial attachments of user {UserId} failed", principal.UserId); }
            }

            // Disposal re-publishes an idle registry entry for the partial Composer: drop it so a
            // failed runtime leaves no stale composer:N row. Done BEFORE the cache entry is cleared,
            // so a retry cannot be racing this cleanup for the same registry id.
            if (state is not null)
                _factory.UnregisterUser(state);

            lock (_gate)
            {
                if (_entries.TryGetValue(principal.UserId, out var current) && ReferenceEquals(current, entry))
                    _entries.Remove(principal.UserId);
            }

            throw;
        }
    }
}

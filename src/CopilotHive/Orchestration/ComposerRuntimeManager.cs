using CopilotHive.Security;
using CopilotHive.Services;

namespace CopilotHive.Orchestration;

/// <summary>Why the manager refused to yield a runtime. Every value is fail-closed.</summary>
public enum ComposerRuntimeRefusal
{
    /// <summary>No refusal — the request was not refused.</summary>
    None = 0,

    /// <summary>The principal is the synthetic open-mode local operator (or carries no activated account id). Open mode stays legacy-only.</summary>
    LocalOperator = 1,

    /// <summary>The principal is disabled, or the account was disabled since the principal was resolved.</summary>
    DisabledAccount = 2,

    /// <summary>The principal's role, or the freshly stored role, is not a defined <see cref="UserRole"/>.</summary>
    UndefinedRole = 3,

    /// <summary>No activated account currently exists under the principal's id.</summary>
    UnknownAccount = 4,

    /// <summary><see cref="ComposerRuntimeManager.ConnectAsync"/> found no cached runtime for the user.</summary>
    NoRuntime = 5,

    /// <summary>The manager is shutting down (or has shut down): no runtime is created and none is handed out.</summary>
    ShuttingDown = 6,
}

/// <summary>
/// What a settled manager request ended in. ONE outcome model for create and connect: a request only
/// completes in a state a caller can act on — never on "the connect was started".
/// </summary>
public enum ComposerRuntimeStatus
{
    /// <summary>The request was refused (<see cref="ComposerRuntimeResult.Refusal"/>); no runtime.</summary>
    Refused = 0,

    /// <summary>The runtime finished connecting its LLM connection.</summary>
    Connected = 1,

    /// <summary>The Composer has no configured model: an explicit disconnected shell, nothing to connect.</summary>
    DisconnectedShell = 2,

    /// <summary>The connect is deferred (PendingConnect-like): the gate says no usable token yet. Use <see cref="ComposerRuntimeManager.ConnectAsync"/> to retry.</summary>
    Deferred = 3,

    /// <summary>
    /// Creation or connect failed (<see cref="ComposerRuntimeResult.Failure"/>). Reported only AFTER the
    /// partial runtime was disposed and its cache entry removed; history on disk is preserved.
    /// </summary>
    Failed = 4,

    /// <summary>The runtime was removed while the request was in flight (or is being removed): no runtime is handed out.</summary>
    Removed = 5,
}

/// <summary>
/// The outcome of one runtime request: a <see cref="ComposerRuntimeStatus"/>, plus the user's shared
/// <see cref="ComposerUserRuntime"/> for the granted statuses or a typed, secret-free reason otherwise.
/// </summary>
public sealed record ComposerRuntimeResult
{
    private ComposerRuntimeResult(
        ComposerRuntimeStatus status, ComposerUserRuntime? runtime, ComposerRuntimeRefusal refusal, Exception? failure)
    {
        Status = status;
        Runtime = runtime;
        Refusal = refusal;
        Failure = failure;
    }

    /// <summary>What the request ended in.</summary>
    public ComposerRuntimeStatus Status { get; }

    /// <summary>The shared runtime for <see cref="ComposerRuntimeStatus.Connected"/>, <see cref="ComposerRuntimeStatus.DisconnectedShell"/> and <see cref="ComposerRuntimeStatus.Deferred"/>; otherwise <c>null</c>.</summary>
    public ComposerUserRuntime? Runtime { get; }

    /// <summary><see cref="ComposerRuntimeRefusal.None"/> unless <see cref="Status"/> is <see cref="ComposerRuntimeStatus.Refused"/>.</summary>
    public ComposerRuntimeRefusal Refusal { get; }

    /// <summary>The creation/connect fault when <see cref="Status"/> is <see cref="ComposerRuntimeStatus.Failed"/>.</summary>
    public Exception? Failure { get; }

    /// <summary>Whether a runtime was produced (connected, shell or deferred).</summary>
    public bool IsGranted => Runtime is not null;

    internal static ComposerRuntimeResult Granted(ComposerRuntimeStatus status, ComposerUserRuntime runtime) =>
        new(status, runtime, ComposerRuntimeRefusal.None, failure: null);

    internal static ComposerRuntimeResult Refused(ComposerRuntimeRefusal reason) =>
        new(ComposerRuntimeStatus.Refused, null, reason, failure: null);

    internal static ComposerRuntimeResult Faulted(Exception failure) =>
        new(ComposerRuntimeStatus.Failed, null, ComposerRuntimeRefusal.None, failure);

    internal static ComposerRuntimeResult RemovedResult() =>
        new(ComposerRuntimeStatus.Removed, null, ComposerRuntimeRefusal.None, failure: null);
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
/// id, and owns the per-user connect outcome. Open mode stays legacy-only: the synthetic local
/// operator never yields a runtime here.
/// </summary>
/// <remarks>
/// <para>
/// OWNERSHIP: each cache <see cref="Entry"/> owns ONE shared operation (initial create+connect, or a
/// deferred connect) and the <see cref="CancellationTokenSource"/> that cancels it. A caller's token
/// cancels only that caller's WAIT; only <see cref="RemoveAsync"/> cancels the shared operation. The
/// operation never throws: it settles into a <see cref="ComposerRuntimeStatus"/>, and a failed
/// runtime is disposed and removed from the cache BEFORE the operation completes, so a recreation can
/// neither resurrect nor overlap a faulted instance.
/// </para>
/// <para>
/// NO GLOBAL COORDINATOR: this class never touches <see cref="LlmConnectionCoordinator"/> instances nor
/// <see cref="UserService.TokenAvailable"/>; it only evaluates the coordinator's pure deferral predicate
/// through <see cref="ComposerFactory.ShouldDeferConnect"/>. A deferred runtime is connected by an
/// explicit, awaitable <see cref="ConnectAsync"/>.
/// </para>
/// <para>
/// TEARDOWN: a removed (or failed) runtime is torn down until <see cref="Composer.TeardownCompletion"/> says
/// the actor AND agent are genuinely finished — <see cref="Composer.DisposeAsync"/> returning is NOT enough,
/// because it can return while a stuck actor is still running. The entry stays in the cache in the Removing
/// state for that whole time, so a same-user recreation waits for the old teardown instead of overlapping a
/// second <c>composer:N</c> actor. <see cref="ShutdownAsync"/> refuses new creation and waits for EVERY entry's
/// teardown. Persisted sessions and attachments are never deleted by removal or shutdown.
/// </para>
/// </remarks>
public sealed class ComposerRuntimeManager : IAsyncDisposable
{
    private readonly ComposerFactory _factory;
    private readonly UserService _users;
    private readonly ILogger<ComposerRuntimeManager> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<int, Entry> _entries = [];
    private readonly Func<Composer, CancellationToken, Task> _connect;
    private Task? _shutdown;

    /// <summary>
    /// Test observation seam, <c>null</c> (inert) in production: invoked with the user id and the EXACT task the
    /// manager is about to block on — <see cref="Composer.TeardownCompletion"/> while a runtime is being torn down, or
    /// the entry's teardown while a same-user recreation waits for it. It lets a test prove the manager joins the
    /// actual teardown task rather than a surrogate, and fire a milestone when the manager is genuinely blocked.
    /// </summary>
    internal Action<int, Task>? OnJoiningTeardown { get; set; }

    /// <summary>Test-only observation of the exact shared operation being joined; null is inert.</summary>
    internal Action<int, Task>? OnJoiningOperation { get; set; }

    /// <summary>Creates the manager.</summary>
    /// <param name="factory">The shared Composer construction seam.</param>
    /// <param name="users">The persisted user directory used for identity resolution and re-checks.</param>
    /// <param name="logger">Logger.</param>
    public ComposerRuntimeManager(ComposerFactory factory, UserService users, ILogger<ComposerRuntimeManager> logger)
        : this(factory, users, logger, connect: null)
    {
    }

    /// <summary>
    /// Test seam: <paramref name="connect"/> replaces the single call that runs a Composer's connect, so tests
    /// can observe the shared token and park/fail the connect deterministically. <c>null</c> (the default and
    /// the only production value) calls <see cref="Composer.ConnectAsync"/>.
    /// </summary>
    internal ComposerRuntimeManager(
        ComposerFactory factory, UserService users, ILogger<ComposerRuntimeManager> logger,
        Func<Composer, CancellationToken, Task>? connect)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connect = connect ?? ((composer, ct) => composer.ConnectAsync(ct));
    }

    /// <summary>How a shared operation settled. Never carries a thrown exception: failures are data.</summary>
    private sealed record Settled(ComposerRuntimeStatus Status, ComposerUserRuntime? Runtime, Exception? Failure);

    private sealed class Entry
    {
        /// <summary>Cancels the shared operation. Owned by the manager; only removal cancels it.</summary>
        public CancellationTokenSource Cts { get; } = new();

        /// <summary>The latest shared operation; every joiner awaits this task. Assigned under the manager gate.</summary>
        public Task<Settled> Operation { get; set; } = null!;

        /// <summary>Set under the gate when removal/shutdown owns the entry: no new waiter may join and no runtime is handed out.</summary>
        public bool Removing { get; set; }

        /// <summary>
        /// The ONE teardown of this entry (cancel the operation, dispose the runtime, join its actual
        /// actor/agent completion, then drop the entry). Assigned under the gate together with <see cref="Removing"/>;
        /// it never throws. Same-user recreation and shutdown await it.
        /// </summary>
        public Task Teardown { get; set; } = Task.CompletedTask;
    }

    /// <summary>
    /// Returns the shared runtime for <paramref name="principal"/>'s account, creating it on first use.
    /// The call completes only when the runtime is Connected, an explicit model-less shell, or Deferred;
    /// a failed create/connect completes as <see cref="ComposerRuntimeStatus.Failed"/> AFTER cleanup.
    /// EVERY call — cache hit, creator and waiter alike — re-reads the stored account before returning
    /// and refuses when it is missing, disabled or role-invalid.
    /// </summary>
    /// <param name="principal">The acting principal (never trusted beyond its id; state is re-read).</param>
    /// <param name="ct">Cancels THIS caller's wait only (it throws <see cref="OperationCanceledException"/>); the shared operation is not cancelled by it.</param>
    /// <returns>A typed outcome.</returns>
    public async Task<ComposerRuntimeResult> GetOrCreateAsync(ActingUserPrincipal principal, CancellationToken ct = default)
    {
        if (Admit(principal) is { } refusal)
            return ComposerRuntimeResult.Refused(refusal);

        var userId = principal.UserId;

        while (true)
        {
            // A stale principal must not trigger creation (and legacy migration) for a since-disabled
            // account; callers joining an existing entry are re-checked after the await below instead.
            bool exists;
            lock (_gate)
            {
                if (_shutdown is not null)
                    return ComposerRuntimeResult.Refused(ComposerRuntimeRefusal.ShuttingDown);
                exists = _entries.ContainsKey(userId);
            }
            if (!exists && await RecheckAsync(userId, ct) is { } early)
                return ComposerRuntimeResult.Refused(early);

            Entry entry;
            Task? teardown = null;
            lock (_gate)
            {
                // Re-checked INSIDE the lock that creates the entry: shutdown cannot begin between the
                // check and the creation, so no runtime is ever created after shutdown started.
                if (_shutdown is not null)
                    return ComposerRuntimeResult.Refused(ComposerRuntimeRefusal.ShuttingDown);

                if (!_entries.TryGetValue(userId, out entry!))
                {
                    entry = new Entry();
                    _entries[userId] = entry;
                    entry.Operation = Task.Run(() => CreateAsync(principal, entry), CancellationToken.None);
                }
                else if (entry.Removing)
                {
                    // While removal is still cancelling the in-flight operation this caller would only join a
                    // doomed operation: it is told Removed. Once the operation has settled and only the old
                    // runtime's teardown remains, a same-user recreation WAITS for that actual teardown below.
                    if (!entry.Operation.IsCompleted)
                        return ComposerRuntimeResult.RemovedResult();
                    teardown = entry.Teardown;
                }
            }

            if (teardown is not null)
            {
                // Joins the ACTUAL teardown task (never a surrogate); the caller's token ends only this wait.
                await WaitForTeardownAsync(userId, teardown, ct);
                continue; // the entry is gone: re-run admission and create a fresh runtime
            }

            return await JoinAsync(entry, userId, ct);
        }
    }

    /// <summary>
    /// The awaitable per-user connect/retry: completes after the runtime is Connected, or — when the
    /// connect faults — AFTER the failed runtime was disposed and removed from the cache
    /// (<see cref="ComposerRuntimeStatus.Failed"/>). Concurrent callers share ONE connect. An already
    /// settled runtime (connected / shell) is returned as is; a Deferred runtime whose gate still defers
    /// stays <see cref="ComposerRuntimeStatus.Deferred"/> with no connect attempt. It never reaches the
    /// legacy <see cref="LlmConnectionCoordinator"/> or <see cref="UserService.TokenAvailable"/>.
    /// </summary>
    /// <param name="principal">The acting principal (re-checked exactly like <see cref="GetOrCreateAsync"/>).</param>
    /// <param name="ct">Cancels THIS caller's wait only.</param>
    /// <returns>A typed outcome; <see cref="ComposerRuntimeRefusal.NoRuntime"/> when the user has no cached runtime.</returns>
    public async Task<ComposerRuntimeResult> ConnectAsync(ActingUserPrincipal principal, CancellationToken ct = default)
    {
        if (Admit(principal) is { } refusal)
            return ComposerRuntimeResult.Refused(refusal);

        var userId = principal.UserId;
        Entry entry;
        lock (_gate)
        {
            if (_shutdown is not null)
                return ComposerRuntimeResult.Refused(ComposerRuntimeRefusal.ShuttingDown);
            if (!_entries.TryGetValue(userId, out entry!))
                return ComposerRuntimeResult.Refused(ComposerRuntimeRefusal.NoRuntime);
            if (entry.Removing)
                return ComposerRuntimeResult.RemovedResult();

            // Only a SETTLED Deferred runtime starts a connect; an in-flight operation (initial create or
            // an earlier connect) is joined, never duplicated.
            if (entry.Operation.IsCompletedSuccessfully
                && entry.Operation.Result is { Status: ComposerRuntimeStatus.Deferred, Runtime: { } runtime }
                && !_factory.ShouldDeferConnect(runtime.Composer))
            {
                entry.Operation = Task.Run(() => ConnectDeferredAsync(entry, runtime), CancellationToken.None);
            }
        }

        return await JoinAsync(entry, userId, ct);
    }

    /// <summary>
    /// Removes the user's runtime: marks the entry Removing (no new waiter joins; waiters already joined
    /// receive <see cref="ComposerRuntimeStatus.Removed"/>), cancels the shared operation, waits for it to
    /// settle, disposes the runtime and its per-instance subscriber, then waits for the old actor/agent to
    /// ACTUALLY finish (<see cref="Composer.TeardownCompletion"/>) before dropping the entry. The entry stays
    /// Removing the whole time, so a same-user recreation waits for this teardown. Persisted sessions and
    /// attachments are never deleted. Returns <c>false</c> when there was nothing to remove (or the entry is
    /// already being removed by someone else).
    /// </summary>
    /// <param name="userId">The internal account id.</param>
    /// <returns>Whether this call removed a cache entry.</returns>
    public async Task<bool> RemoveAsync(int userId)
    {
        Task teardown;
        lock (_gate)
        {
            if (!_entries.TryGetValue(userId, out var entry) || entry.Removing)
                return false;
            teardown = BeginTeardownLocked(userId, entry);
        }

        await teardown;
        return true;
    }

    /// <summary>
    /// Shuts the manager down: from the moment it is called no runtime is created or handed out
    /// (<see cref="ComposerRuntimeRefusal.ShuttingDown"/>), and the returned task completes only after EVERY
    /// entry — including ones already being removed — has finished its actual actor/agent teardown. Safe to
    /// call repeatedly and concurrently: every caller joins the same shutdown. Persisted data is never deleted.
    /// </summary>
    /// <returns>A task that completes once all teardown is genuinely over; it never throws.</returns>
    public Task ShutdownAsync()
    {
        lock (_gate)
        {
            if (_shutdown is not null)
                return _shutdown;

            // The flag and the snapshot are taken under the SAME lock that admits creation, so an entry is
            // either in the snapshot or was never created.
            var teardowns = new List<Task>();
            foreach (var (userId, entry) in _entries.ToArray())
                teardowns.Add(entry.Removing ? entry.Teardown : BeginTeardownLocked(userId, entry));

            _shutdown = Task.WhenAll(teardowns);
            return _shutdown;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await ShutdownAsync();

    /// <summary>Marks the entry Removing and starts its single teardown. Must be called under the gate.</summary>
    private Task BeginTeardownLocked(int userId, Entry entry)
    {
        entry.Removing = true;
        entry.Teardown = Task.Run(() => TearDownAsync(userId, entry), CancellationToken.None);
        return entry.Teardown;
    }

    /// <summary>The one teardown of an entry; never throws. The entry leaves the cache only at the very end.</summary>
    private async Task TearDownAsync(int userId, Entry entry)
    {
        try
        {
            // A throwing registered cancellation callback surfaces from CancelAsync, but the token IS cancelled and
            // every other callback still ran. That fault must neither skip the join below nor evict the entry
            // early: contain it HERE, surface it as a warning, and carry on with the teardown.
            try { await entry.Cts.CancelAsync(); }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A cancellation callback of the shared operation of user {UserId} threw; teardown continues", userId);
            }

            // The operation never throws; a connect aborted by the cancellation disposed its own partial
            // runtime (and joined its actual teardown) itself.
            try { OnJoiningOperation?.Invoke(userId, entry.Operation); }
            catch { /* Observation must never change teardown, even if a test hook throws. */ }
            var settled = await entry.Operation;
            if (settled.Runtime is { } runtime)
                await DisposeRuntimeAsync(runtime, clearAttachments: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tearing down the runtime of user {UserId} failed", userId);
        }
        finally
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(userId, out var current) && ReferenceEquals(current, entry))
                    _entries.Remove(userId);
            }

            entry.Cts.Dispose();
        }
    }

    private static ComposerRuntimeRefusal? Admit(ActingUserPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.IsLocalOperator || principal.UserId <= 0)
            return ComposerRuntimeRefusal.LocalOperator;
        if (!principal.Enabled)
            return ComposerRuntimeRefusal.DisabledAccount;
        return IsDefinedRole(principal.Role) ? null : ComposerRuntimeRefusal.UndefinedRole;
    }

    /// <summary>Awaits the entry's shared operation for ONE caller, then re-reads the account and maps the outcome.</summary>
    private async Task<ComposerRuntimeResult> JoinAsync(Entry entry, int userId, CancellationToken ct)
    {
        // The caller's token ends only this wait; the shared operation keeps running for everyone else.
        var settled = await entry.Operation.WaitAsync(ct);

        // Re-read AFTER the operation settled so a waiter whose account was disabled meanwhile is refused.
        if (await RecheckAsync(userId, ct) is { } refusal)
            return ComposerRuntimeResult.Refused(refusal);

        // Never hand out a runtime that removal is tearing down.
        bool removing;
        lock (_gate) removing = entry.Removing;
        if (removing || settled.Status == ComposerRuntimeStatus.Removed)
            return ComposerRuntimeResult.RemovedResult();

        return settled.Status == ComposerRuntimeStatus.Failed
            ? ComposerRuntimeResult.Faulted(settled.Failure!)
            : ComposerRuntimeResult.Granted(settled.Status, settled.Runtime!);
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

    /// <summary>Initial shared operation: build the runtime, then settle as shell / deferred / connected / failed.</summary>
    private async Task<Settled> CreateAsync(ActingUserPrincipal principal, Entry entry)
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
            var runtime = new ComposerUserRuntime(state, composer, attachments, subscriber);

            // A Composer without a resolved model is an explicit disconnected shell: nothing to connect.
            if (composer.StartupDefaultModel is null)
                return new Settled(ComposerRuntimeStatus.DisconnectedShell, runtime, null);

            // Deferred: the gate says no usable token yet. An explicit state — not a failure and not a
            // started connect; the manager's awaitable ConnectAsync completes it later.
            if (_factory.ShouldDeferConnect(composer))
                return new Settled(ComposerRuntimeStatus.Deferred, runtime, null);

            // Migration / session load run inside ConnectAsync and fail closed (never delete history).
            // The token is the ENTRY's, not any caller's: only removal cancels this connect.
            await _connect(composer, entry.Cts.Token);
            return new Settled(ComposerRuntimeStatus.Connected, runtime, null);
        }
        catch (Exception ex)
        {
            // Lifecycle cancellation of a constructed runtime: route through the lifecycle teardown (no clearing).
            if (entry.Cts.IsCancellationRequested && composer is not null)
                return new Settled(ComposerRuntimeStatus.Removed,
                    new ComposerUserRuntime(state!, composer, attachments!, subscriber), null);

            return await FailAsync(entry, principal.UserId, state, composer, subscriber, attachments, ex);
        }
    }

    /// <summary>Deferred-connect shared operation: connects an existing Deferred runtime, or tears it down on failure.</summary>
    private async Task<Settled> ConnectDeferredAsync(Entry entry, ComposerUserRuntime runtime)
    {
        try
        {
            await _connect(runtime.Composer, entry.Cts.Token);
            return new Settled(ComposerRuntimeStatus.Connected, runtime, null);
        }
        catch (Exception ex)
        {
            // A fault caused by removal's/shutdown's own cancellation is NOT a connect failure: the EXISTING
            // runtime (with its uploaded attachments and session) is handed to the lifecycle teardown, which
            // disposes it WITHOUT clearing attachments. Only a genuine connect failure reaches FailAsync.
            if (entry.Cts.IsCancellationRequested)
                return new Settled(ComposerRuntimeStatus.Removed, runtime, null);

            return await FailAsync(entry, runtime.State.UserId, runtime.State, runtime.Composer,
                runtime.Subscriber, runtime.Attachments, ex);
        }
    }

    /// <summary>
    /// Failure path shared by create and deferred connect: dispose ONLY this attempt's pieces, unregister the
    /// registry row, and ONLY THEN drop this attempt's cache entry — so a recreation cannot overlap a
    /// still-live faulted instance. Cleanup failures never mask the original exception. A fault caused by
    /// removal's cancellation settles as <see cref="ComposerRuntimeStatus.Removed"/>.
    /// </summary>
    private async Task<Settled> FailAsync(
        Entry entry, int userId, ComposerUserState? state, Composer? composer,
        ComposerEventSubscriber? subscriber, ComposerAttachmentService? attachments, Exception failure)
    {
        if (composer is not null)
        {
            try { await composer.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Disposing the partial Composer of user {UserId} failed", userId); }

            // DisposeAsync may return while a stuck actor still runs: the entry may only leave the cache (and
            // a recreation may only start) after the ACTUAL actor/agent teardown.
            await JoinTeardownAsync(composer, userId);
        }

        try { subscriber?.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Disposing the partial subscriber of user {UserId} failed", userId); }

        if (attachments is not null)
        {
            try { await attachments.ClearAllAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Clearing the partial attachments of user {UserId} failed", userId); }
        }

        // Disposal re-publishes an idle registry entry for the partial Composer: drop it.
        if (state is not null)
            _factory.UnregisterUser(state);

        bool cancelledByRemoval;
        lock (_gate)
        {
            cancelledByRemoval = entry.Cts.IsCancellationRequested;
            if (_entries.TryGetValue(userId, out var current) && ReferenceEquals(current, entry))
                _entries.Remove(userId);
        }

        return cancelledByRemoval
            ? new Settled(ComposerRuntimeStatus.Removed, null, null)
            : new Settled(ComposerRuntimeStatus.Failed, null, failure);
    }

    /// <summary>
    /// Disposes a runtime's pieces and registry row; every step runs and no failure escapes. The registry row is
    /// dropped only AFTER the actor/agent teardown truly finished, because a still-running actor can
    /// re-publish its idle <c>composer:N</c> entry until then.
    /// </summary>
    private async Task DisposeRuntimeAsync(ComposerUserRuntime runtime, bool clearAttachments)
    {
        var userId = runtime.State.UserId;
        try { await runtime.Composer.DisposeAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Disposing the Composer of user {UserId} failed", userId); }

        try { runtime.Subscriber?.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Disposing the subscriber of user {UserId} failed", userId); }

        if (clearAttachments)
        {
            try { await runtime.Attachments.ClearAllAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Clearing the attachments of user {UserId} failed", userId); }
        }

        await JoinTeardownAsync(runtime.Composer, userId);

        // Disposal re-publishes an idle registry entry: drop it so no stale composer:N row remains.
        _factory.UnregisterUser(runtime.State);
    }

    /// <summary>Waits for <see cref="Composer.TeardownCompletion"/>; a faulted completion is logged, never thrown.</summary>
    private async Task JoinTeardownAsync(Composer composer, int userId)
    {
        try { await WaitForTeardownAsync(userId, composer.TeardownCompletion, CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning(ex, "Teardown of the Composer of user {UserId} faulted", userId); }
    }

    /// <summary>The single place the manager blocks on a teardown task; reports it to the observation seam first.</summary>
    private Task WaitForTeardownAsync(int userId, Task teardown, CancellationToken ct)
    {
        OnJoiningTeardown?.Invoke(userId, teardown);
        return teardown.WaitAsync(ct);
    }
}

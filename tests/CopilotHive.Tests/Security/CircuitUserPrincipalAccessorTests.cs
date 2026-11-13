using System.Security.Claims;

using System.Data.Common;

using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Security;

/// <summary>
/// Covers the EXPLICITLY INITIALISABLE scoped circuit-principal accessor:
/// <list type="bullet">
/// <item>two supplied <see cref="AuthenticationState"/> values (two circuits) on separate accessor
/// instances resolve independently, with no <c>HttpContext</c> involved at all;</item>
/// <item>every operation RE-RESOLVES the current database role/enabled state, so a demotion or a
/// disablement is honoured on the next question asked;</item>
/// <item>an accessor that was never initialised (or was cleared) fails CLOSED in auth-enabled mode;
/// </item>
/// <item>no automatic cascading-state wiring is claimed: the state is always supplied explicitly.</item>
/// </list>
/// </summary>
public sealed class CircuitUserPrincipalAccessorTests
{
    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>Wraps a supplied <see cref="ClaimsPrincipal"/> as the circuit's authentication state.</summary>
    private static AuthenticationState StateOf(ClaimsPrincipal user) => new(user);

    /// <summary>
    /// TWO SUPPLIED CIRCUITS, TWO IDENTITIES: each accessor resolves its own initialised identity from
    /// the directory. The accessors are independent instances (as scoped registration makes them per
    /// circuit), and no ambient/<c>HttpContext</c> identity is consulted.
    /// </summary>
    [Fact]
    public async Task GetPrincipalAsync_TwoInitializedCircuits_ResolveTheirOwnIdentitiesIndependently()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin, username: "original");
        await directory.SeedActivatedAccountAsync("2", UserRole.Member, username: "member");

        var firstCircuit = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        var secondCircuit = directory.Accessor(HiveAuthMode.AuthenticationEnabled);

        firstCircuit.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));
        secondCircuit.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("2")));

        var first = await firstCircuit.GetPrincipalAsync(Ct);
        var second = await secondCircuit.GetPrincipalAsync(Ct);

        Assert.Equal("1", first.Principal!.GitHubId);
        Assert.Equal(UserRole.Admin, first.Principal.Role);
        Assert.True(first.Principal.IsOriginal);

        Assert.Equal("2", second.Principal!.GitHubId);
        Assert.Equal(UserRole.Member, second.Principal.Role);
        Assert.False(second.Principal.IsOriginal);

        // Cross-check the capability answers differ between the two circuits.
        Assert.True(await firstCircuit.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
        Assert.False(await secondCircuit.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
        Assert.True(await secondCircuit.IsAllowedAsync(HiveCapability.InteractiveMember, Ct));
    }

    /// <summary>
    /// EVERY OPERATION REFRESHES: one initialisation, then a demotion in the directory, then a second
    /// question answered from the NEW state. The principal captured at initialisation is never reused
    /// as authorization truth.
    /// </summary>
    [Fact]
    public async Task IsAllowedAsync_AfterDemotion_ReflectsTheNewRoleOnTheNextOperation()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        accessor.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));

        Assert.True(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));

        await directory.SetRoleAsync("1", UserRole.Operator);

        Assert.False(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
        Assert.True(await accessor.IsAllowedAsync(HiveCapability.ExecutionOperator, Ct));
        Assert.Equal(UserRole.Operator, (await accessor.GetPrincipalAsync(Ct)).Principal!.Role);
    }

    /// <summary>
    /// DISABLEMENT IS HONOURED ON THE NEXT OPERATION even though the account was ENABLED and Admin at
    /// initialisation: the accessor refuses and holds no capability.
    /// </summary>
    [Fact]
    public async Task IsAllowedAsync_AfterDisablement_RefusesAndHoldsNoCapability()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        accessor.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));

        Assert.True(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));

        await directory.SetEnabledAsync("1", enabled: false);

        var resolution = await accessor.GetPrincipalAsync(Ct);
        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.DisabledAccount, resolution.RefusalReason);

        Assert.False(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
    }

    /// <summary>
    /// AN UN-INITIALISED ACCESSOR FAILS CLOSED in auth-enabled mode — regardless of how many accounts
    /// exist and even for an Admin. There is no implicit identity and no open-mode fall-back.
    /// </summary>
    [Fact]
    public async Task GetPrincipalAsync_UninitializedInAuthEnabledMode_RefusesAndDeniesEveryCapability()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);

        Assert.False(accessor.IsInitialized);

        var resolution = await accessor.GetPrincipalAsync(Ct);
        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Principal);
        Assert.Equal(PrincipalRefusalReason.AccessorNotInitialized, resolution.RefusalReason);

        Assert.False(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.InteractiveMember, Ct));
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.ExecutionOperator, Ct));
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
    }

    /// <summary>
    /// CLEARING (the sign-out shape) returns the accessor to the fail-closed state: the previously
    /// resolved identity grants nothing afterwards.
    /// </summary>
    [Fact]
    public async Task Clear_AfterInitialization_ReturnsToTheFailClosedState()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        accessor.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));

        Assert.True(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));

        accessor.Clear();

        Assert.False(accessor.IsInitialized);
        Assert.Equal(
            PrincipalRefusalReason.AccessorNotInitialized,
            (await accessor.GetPrincipalAsync(Ct)).RefusalReason);
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
    }

    /// <summary>
    /// A CLEAR IS RECORDED AND PERMANENT: it is distinguishable from never-initialised, and a later
    /// <c>Initialize</c> (even with the very same identity) restores nothing.
    /// </summary>
    [Fact]
    public async Task Clear_IsRecordedAsSignedOut_AndIsPermanent()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);
        var never = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        accessor.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));
        Assert.Equal(CircuitIdentityState.Uninitialized, never.IdentityState);
        Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);
        Assert.NotNull(accessor.EstablishedIdentity);

        accessor.Clear();

        Assert.Equal(CircuitIdentityState.SignedOut, accessor.IdentityState);
        Assert.True(accessor.IsInvalidated);
        Assert.False(never.IsInvalidated);
        Assert.Null(accessor.EstablishedIdentity);

        accessor.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));

        Assert.Equal(CircuitIdentityState.SignedOut, accessor.IdentityState);
        Assert.False(accessor.IsInitialized);
        Assert.False((await accessor.GetPrincipalAsync(Ct)).IsResolved);
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));

        // Explicit reauthentication requires a NEW scope/accessor, never revival of this circuit.
        var reauthenticated = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        Assert.NotSame(accessor, reauthenticated);
        reauthenticated.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));
        Assert.Equal("1", (await reauthenticated.GetPrincipalAsync(Ct)).Principal!.GitHubId);
        Assert.True(await reauthenticated.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
        Assert.Equal(CircuitIdentityState.SignedOut, accessor.IdentityState);
    }

    /// <summary>
    /// A DIFFERENT identity observed after initialisation PERMANENTLY invalidates the accessor: the
    /// replacement is not adopted and the original is not restored. The SAME identity is idempotent.
    /// </summary>
    [Fact]
    public async Task Observe_ADifferentIdentity_PermanentlyInvalidates_AndTheSameIdentityIsIdempotent()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);
        await directory.SeedActivatedAccountAsync("2", UserRole.Admin, username: "second");
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        var one = HiveSecurityTestDirectory.GitHubPrincipal("1");

        Assert.Same(one, accessor.Observe(StateOf(one)));
        Assert.NotNull(accessor.Observe(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1"))));
        Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);

        Assert.Null(accessor.Observe(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("2"))));
        Assert.Equal(CircuitIdentityState.IdentityReplaced, accessor.IdentityState);

        // Neither B nor A is accepted afterwards, and Clear cannot resurrect or downgrade the state.
        Assert.Null(accessor.Observe(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("2"))));
        Assert.Null(accessor.Observe(StateOf(one)));
        accessor.Clear();
        Assert.Equal(CircuitIdentityState.IdentityReplaced, accessor.IdentityState);
        Assert.Equal(
            PrincipalRefusalReason.AccessorNotInitialized,
            (await accessor.GetPrincipalAsync(Ct)).RefusalReason);
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));

        var reauthenticated = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        Assert.NotSame(accessor, reauthenticated);
        reauthenticated.Initialize(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("2")));
        Assert.Equal("2", (await reauthenticated.GetPrincipalAsync(Ct)).Principal!.GitHubId);
        Assert.True(await reauthenticated.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
        Assert.Equal(CircuitIdentityState.IdentityReplaced, accessor.IdentityState);
    }

    /// <summary>
    /// An explicitly SIGNED-OUT state observed after an established identity is a sign-out: permanent,
    /// and the later identity A is not restored. NO authorization cache: a demotion still lands
    /// immediately for an initialised accessor.
    /// </summary>
    [Fact]
    public async Task Observe_SignedOutAfterEstablished_IsPermanent_ButAnEstablishedAccessorStillReResolves()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);
        var one = HiveSecurityTestDirectory.GitHubPrincipal("1");
        accessor.Observe(StateOf(one));
        Assert.True(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
        await directory.SetRoleAsync("1", UserRole.Viewer);
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));

        Assert.Null(accessor.Observe(StateOf(HiveSecurityTestDirectory.AnonymousPrincipal())));
        Assert.Equal(CircuitIdentityState.SignedOut, accessor.IdentityState);
        Assert.Null(accessor.Observe(StateOf(one)));
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));
    }

    /// <summary>
    /// A signed-out state as the FIRST observation is recorded as an initialised anonymous identity
    /// and is refused as unauthenticated; a later real identity is then a REPLACEMENT, not an adoption.
    /// </summary>
    [Fact]
    public async Task Observe_SignedOutFirst_ThenAnIdentity_IsNeverAdopted()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);

        accessor.Observe(StateOf(HiveSecurityTestDirectory.AnonymousPrincipal()));
        Assert.Null(accessor.EstablishedIdentity);
        Assert.Equal(PrincipalRefusalReason.Unauthenticated, (await accessor.GetPrincipalAsync(Ct)).RefusalReason);

        Assert.Null(accessor.Observe(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1"))));
        Assert.True(accessor.IsInvalidated);
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));
    }

    /// <summary>
    /// A SIGNED-OUT state is accepted by the explicit initialiser and still fails closed: an
    /// unauthenticated supplied state is refused as unauthenticated, never as the local operator.
    /// </summary>
    [Fact]
    public async Task GetPrincipalAsync_InitializedWithSignedOutState_IsRefusedAsUnauthenticated()
    {
        using var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("1", UserRole.Admin);
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);

        accessor.Initialize(StateOf(HiveSecurityTestDirectory.AnonymousPrincipal()));

        var resolution = await accessor.GetPrincipalAsync(Ct);
        Assert.False(resolution.IsResolved);
        Assert.Equal(PrincipalRefusalReason.Unauthenticated, resolution.RefusalReason);
        Assert.False(await accessor.IsAllowedAsync(HiveCapability.SharedRead, Ct));
    }

    /// <summary>
    /// In OPEN (no-auth) mode the accessor needs no initialisation — that mode carries no per-circuit
    /// identity — and reports the full local operator, preserving backward compatibility.
    /// </summary>
    [Fact]
    public async Task GetPrincipalAsync_OpenModeWithoutInitialization_ResolvesTheLocalOperator()
    {
        using var directory = new HiveSecurityTestDirectory();
        var accessor = directory.Accessor(HiveAuthMode.Open);

        Assert.False(accessor.IsInitialized);
        Assert.False(accessor.AuthEnabled);

        var resolution = await accessor.GetPrincipalAsync(Ct);
        Assert.True(resolution.IsResolved);
        Assert.True(resolution.Principal!.IsLocalOperator);
        Assert.True(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
    }

    /// <summary>
    /// An UNDEFINED capability is a programming error even when the accessor would refuse anyway: the
    /// accessor validates the capability BEFORE any identity work, so the bug is never masked as a
    /// plain "denied" answer.
    /// </summary>
    [Fact]
    public async Task IsAllowedAsync_UndefinedCapability_ThrowsEvenWhenUninitialized()
    {
        using var directory = new HiveSecurityTestDirectory();
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => accessor.IsAllowedAsync((HiveCapability)0, Ct));
    }

    /// <summary>The accessor requires a resolver, and initialisation requires a state.</summary>
    [Fact]
    public void Initialize_NullState_Throws()
    {
        using var directory = new HiveSecurityTestDirectory();
        var accessor = directory.Accessor(HiveAuthMode.AuthenticationEnabled);

        Assert.Throws<ArgumentNullException>(() => accessor.Initialize(null!));
        Assert.Throws<ArgumentNullException>(
            () => new CircuitUserPrincipalAccessor(null!, new HivePolicy()));
        Assert.Throws<ArgumentNullException>(
            () => new CircuitUserPrincipalAccessor(directory.AuthEnabledResolver(), null!));
    }

    // ── Sign-out / replacement DURING a real directory resolution ────────────────────────────

    /// <summary>
    /// A REAL directory (SQLite in-memory + the production <see cref="UserService"/>/resolver/accessor)
    /// whose FIRST armed <c>users</c> read is HELD inside an EF command interceptor
    /// (<c>ReaderExecutingAsync</c>). The hold is therefore genuinely mid-directory-read: the resolver
    /// has started its lookup, and has not yet produced a resolution. <see cref="Arrived"/> completes
    /// only when the read is parked on the release gate; nothing is timing based.
    /// </summary>
    private sealed class HeldDirectoryRead : DbCommandInterceptor, IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private TaskCompletionSource _arrived = NewGate();
        private TaskCompletionSource _release = NewGate();
        private int _armed;

        private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal HeldDirectoryRead()
        {
            _connection.Open();
            using var ctx = CreateDbContext();
            ctx.Database.EnsureCreated();
            Service = new UserService(new Factory(this), NullLogger<UserService>.Instance);
        }

        internal UserService Service { get; }

        /// <summary>The text of the command that was parked (null until it arrived).</summary>
        internal string? HeldCommandText { get; private set; }

        /// <summary>Completes when the armed read is parked on the release gate.</summary>
        internal Task Arrived => _arrived.Task;

        internal CircuitUserPrincipalAccessor Accessor() =>
            new(new HivePrincipalResolver(Service, HiveAuthMode.AuthenticationEnabled, NullLogger<HivePrincipalResolver>.Instance),
                new HivePolicy());

        internal async Task SeedAsync(string gitHubId, UserRole role)
        {
            await using var db = CreateDbContext();
            db.Users.Add(new UserEntity
            {
                GitHubId = gitHubId,
                Username = "user-" + gitHubId,
                AccessToken = "token-" + gitHubId,
                Role = role,
                Enabled = true,
                CreatedAt = "2024-01-01T00:00:00.0000000Z",
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }

        /// <summary>
        /// Arms the hold with FRESH incomplete gates: the NEXT directory read parks until
        /// <see cref="Release"/>. Must only be called while no read is parked.
        /// </summary>
        internal void Arm()
        {
            _arrived = NewGate();
            _release = NewGate();
            Volatile.Write(ref _armed, 1);
        }

        /// <summary>Opens the gate (idempotent).</summary>
        internal void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                HeldCommandText = command.CommandText;
                _arrived.TrySetResult();
                await _release.Task.ConfigureAwait(false);
            }

            return result;
        }

        private CopilotHiveDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(this)
                .Options);

        public void Dispose() => _connection.Dispose();

        private sealed class Factory(HeldDirectoryRead owner) : IDbContextFactory<CopilotHiveDbContext>
        {
            public CopilotHiveDbContext CreateDbContext() => owner.CreateDbContext();
        }
    }

    /// <summary>
    /// Starts <paramref name="question"/> on the accessor, then proves DETERMINISTICALLY that its
    /// directory read is in flight: it waits for the hold's arrival signal (or the question finishing,
    /// which would be a failure) and then positively asserts the read arrived on a <c>users</c> query
    /// and the question has NOT completed.
    /// </summary>
    private static async Task<Task<T>> StartAndAwaitInFlightAsync<T>(HeldDirectoryRead directory, Func<Task<T>> question)
    {
        directory.Arm();
        var task = question();
        await Task.WhenAny(directory.Arrived, task);

        Assert.True(directory.Arrived.IsCompleted, "the directory read must have started and parked");
        Assert.False(task.IsCompleted, "the resolution must still be in flight while the read is held");
        Assert.Contains("users", directory.HeldCommandText, StringComparison.OrdinalIgnoreCase);
        return task;
    }

    /// <summary>
    /// SIGN-OUT DURING A REAL DIRECTORY RESOLUTION (the post-directory-read guard): identity A is
    /// established, the accessor's resolution is held MID-READ, an explicit sign-out is recorded on the
    /// SAME accessor inside that window, and the gate is released. The directory read itself succeeds
    /// (the account is enabled and valid), yet the resolution is REFUSED with no principal, the circuit
    /// is terminally SignedOut and no identity remains.
    /// </summary>
    [Fact]
    public async Task GetPrincipalAsync_SignOutDuringTheDirectoryRead_IsRefused_AndTheCircuitIsSignedOut()
    {
        using var directory = new HeldDirectoryRead();
        await directory.SeedAsync("1", UserRole.Admin);
        var accessor = directory.Accessor();
        Assert.NotNull(accessor.Observe(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1"))));

        Task<PrincipalResolution>? resolving = null;
        try
        {
            resolving = await StartAndAwaitInFlightAsync(directory, () => accessor.GetPrincipalAsync(Ct));

            accessor.Clear(); // the sign-out lands INSIDE the in-flight resolution
            directory.Release();
            var resolution = await resolving;

            Assert.False(resolution.IsResolved);
            Assert.Null(resolution.Principal);
            Assert.Equal(PrincipalRefusalReason.AccessorNotInitialized, resolution.RefusalReason);
            Assert.Equal(CircuitIdentityState.SignedOut, accessor.IdentityState);
            Assert.Null(accessor.EstablishedIdentity);
        }
        finally
        {
            directory.Release();
            if (resolving is not null)
                await resolving.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// The same window through the CAPABILITY question: a sign-out inside the in-flight directory read
    /// must make <see cref="CircuitUserPrincipalAccessor.IsAllowedAsync"/> answer <c>false</c> even
    /// though the account is an enabled Admin.
    /// </summary>
    [Fact]
    public async Task IsAllowedAsync_SignOutDuringTheDirectoryRead_DeniesTheCapability()
    {
        using var directory = new HeldDirectoryRead();
        await directory.SeedAsync("1", UserRole.Admin);
        var accessor = directory.Accessor();
        accessor.Observe(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));

        Task<bool>? asking = null;
        try
        {
            asking = await StartAndAwaitInFlightAsync(
                directory, () => accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));

            accessor.Clear();
            directory.Release();

            Assert.False(await asking);
            Assert.Equal(CircuitIdentityState.SignedOut, accessor.IdentityState);
            Assert.Null(accessor.EstablishedIdentity);
        }
        finally
        {
            directory.Release();
            if (asking is not null)
                await asking.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// REPLACEMENT DURING A REAL DIRECTORY RESOLUTION: a DIFFERENT identity B observed on the same
    /// accessor inside the in-flight read permanently invalidates the circuit; the resolution that was
    /// started for A is refused (B is not adopted, A is not served), and the state is IdentityReplaced.
    /// </summary>
    [Fact]
    public async Task GetPrincipalAsync_ReplacementDuringTheDirectoryRead_IsRefused_AndTheCircuitIsReplaced()
    {
        using var directory = new HeldDirectoryRead();
        await directory.SeedAsync("1", UserRole.Admin);
        await directory.SeedAsync("2", UserRole.Admin);
        var accessor = directory.Accessor();
        accessor.Observe(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));

        Task<PrincipalResolution>? resolving = null;
        try
        {
            resolving = await StartAndAwaitInFlightAsync(directory, () => accessor.GetPrincipalAsync(Ct));

            Assert.Null(accessor.Observe(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("2"))));
            directory.Release();
            var resolution = await resolving;

            Assert.False(resolution.IsResolved);
            Assert.Null(resolution.Principal);
            Assert.Equal(PrincipalRefusalReason.AccessorNotInitialized, resolution.RefusalReason);
            Assert.Equal(CircuitIdentityState.IdentityReplaced, accessor.IdentityState);
            Assert.Null(accessor.EstablishedIdentity);
        }
        finally
        {
            directory.Release();
            if (resolving is not null)
                await resolving.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// POSITIVE CONTROL on the SAME harness and the SAME hold: no sign-out/replacement inside the
    /// window. The held resolution completes successfully with the A principal and the correct
    /// capability answers — so the hold itself denies nothing and the regressions above are
    /// discriminating, not vacuous.
    /// </summary>
    [Fact]
    public async Task GetPrincipalAndIsAllowed_HeldDirectoryReadWithNoSignOut_CompleteSuccessfully()
    {
        using var directory = new HeldDirectoryRead();
        await directory.SeedAsync("1", UserRole.Admin);
        var accessor = directory.Accessor();
        accessor.Observe(StateOf(HiveSecurityTestDirectory.GitHubPrincipal("1")));

        Task<PrincipalResolution>? resolving = null;
        Task<bool>? asking = null;
        try
        {
            resolving = await StartAndAwaitInFlightAsync(directory, () => accessor.GetPrincipalAsync(Ct));
            directory.Release();
            var resolution = await resolving;

            Assert.True(resolution.IsResolved);
            Assert.Equal("1", resolution.Principal!.GitHubId);
            Assert.Equal(UserRole.Admin, resolution.Principal.Role);
            Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);
            Assert.NotNull(accessor.EstablishedIdentity);

            // The capability question through the identical hold (a fresh arm of the same harness).
            asking = await StartAndAwaitInFlightAsync(
                directory, () => accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));
            directory.Release();
            Assert.True(await asking);
            Assert.Equal(CircuitIdentityState.Initialized, accessor.IdentityState);
        }
        finally
        {
            directory.Release();
            if (resolving is not null)
                await resolving.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (asking is not null)
                await asking.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}

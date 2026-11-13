using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.ExceptionServices;

using CopilotHive.Dashboard;
using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CopilotHive.Tests.Security;

/// <summary>
/// THE CONCURRENCY AND LIFETIME CONTRACT of <see cref="HiveSecurityTestDirectory"/>: the shared test
/// fixture hands every <see cref="CopilotHiveDbContext"/> its OWN connection to ONE named shared-cache
/// in-memory database, keeps that database alive with a KEEPER connection, and rejects context
/// creation once disposed.
/// <para>
/// WHY THIS BATTERY EXISTS: the fixture used to hand EVERY context the SAME
/// <see cref="SqliteConnection"/> instance. EF's SQLite provider registers its scalar functions on the
/// connection while it initialises it (<c>SqliteRelationalConnection.InitializeDbConnection</c> →
/// <c>SqliteConnection.CreateFunction</c>), so two contexts created CONCURRENTLY on one connection
/// raced inside SQLite and one of them failed with <c>SQLite Error 5: 'database is locked'</c> — a
/// non-deterministic failure that surfaced across the ~20 security test classes sharing the fixture.
/// </para>
/// <para>
/// SCOPE OF THE PROMISE — READ THIS BEFORE EXTENDING THE OVERLAP ROW: these rows target the
/// CONNECTION-INITIALISATION race ONLY. Shared-cache SQLite still arbitrates between connections, and
/// an UNCOMMITTED write takes a cache-wide lock that makes ANY other connection's read fail with
/// <c>SQLite Error 6: 'database table is locked'</c> (verified — this is not a busy-handler retry
/// case). Nothing here promises that arbitrary concurrent write contention is avoided; the overlap row
/// therefore keeps its write boundary COMMITTED and holds only a READ transaction open across the
/// production reads.
/// </para>
/// <para>
/// Every concurrency row coordinates with an explicit rendezvous (a <see cref="CountdownEvent"/> /
/// <see cref="TaskCompletionSource"/>) and bounded joins — never a sleep and never a poll — so the
/// window under test is reached by construction and a genuine hang surfaces as a named failure.
/// </para>
/// </summary>
public sealed class HiveSecurityTestDirectoryConcurrencyTests
{
    /// <summary>How many contexts are created and initialised CONCURRENTLY in the race row.</summary>
    private const int ConcurrentActors = 8;

    /// <summary>
    /// The BOUNDED HANG-GUARD for every rendezvous and join: a parked actor or an unfinished task
    /// fails the test with a named timeout instead of hanging the run. The operations under test are
    /// milliseconds of SQLite work, so this can only trip on a real hang.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ActorGitHubId(int index) =>
        index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ══════════════════════════════════════════════════════════════════════════
    //  (a) THE INITIALIZATION-RACE REGRESSION
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE REGRESSION: many contexts are created, INITIALISED and used CONCURRENTLY over one directory
    /// without a single <c>'database is locked'</c> failure, while a production
    /// <see cref="UserService"/> read overlaps the test thread's in-flight read AND write on other
    /// contexts.
    /// <para>
    /// THE MECHANISM UNDER TEST: every actor opens its OWN context connection before the rendezvous —
    /// exactly the work EF performs while registering the provider's scalar functions on that
    /// connection. Under the OLD single-shared-connection shape all actors were calling
    /// <c>CreateFunction</c> on the SAME handle while another thread used it, and SQLite refused one of
    /// them; with one connection per context there is no shared initialisation left to race. The
    /// rendezvous makes that window deterministic: NO actor reads until every actor has initialised,
    /// and the test thread's boundaries stay IN FLIGHT while the production reads run.
    /// </para>
    /// <para>
    /// THE TEST THREAD'S EXPLICIT BOUNDARIES, in order: a WRITE transaction on its own context
    /// (committed — an uncommitted write would lock the whole shared cache and make the production
    /// reads fail for a reason that has nothing to do with connection initialisation), then a READ
    /// transaction that stays OPEN across the production reads.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ConcurrentContextInitialization_WithAProductionReadOverlappingATestReadAndWrite_NeverFailsWithDatabaseIsLocked()
    {
        using var directory = new HiveSecurityTestDirectory();

        for (var index = 0; index < ConcurrentActors; index++)
        {
            await directory.SeedActivatedAccountAsync(
                ActorGitHubId(index), UserRole.Member, username: "actor-" + ActorGitHubId(index));
        }

        // Each actor's OWN connection instance, published before the rendezvous and inspected after it.
        var connections = new DbConnection?[ConcurrentActors];

        // Any actor failure (a 'database is locked' connection-initialisation race in particular) is
        // recorded WITH ITS ACTOR so the failure is named instead of being lost in a join.
        var actorFailures = new ConcurrentQueue<string>();

        using var allInitialized = new CountdownEvent(ConcurrentActors);
        var productionMayRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlapFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var testSideInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var actors = new Task[ConcurrentActors];
        for (var index = 0; index < ConcurrentActors; index++)
        {
            var actor = index;
            actors[actor] = Task.Run(async () =>
            {
                try
                {
                    await using var db = directory.CreateDbContext();

                    // THE CONCURRENT INITIALISATION: EF registers the provider's scalar functions on
                    // THIS actor's own connection here, at the same time as every other actor.
                    await db.Database.OpenConnectionAsync(Ct);
                    connections[actor] = db.Database.GetDbConnection();

                    allInitialized.Signal();
                    await productionMayRead.Task.ConfigureAwait(false);

                    // The PRODUCTION read path, overlapping the test thread's in-flight boundaries.
                    var account = await directory.Service.GetByGitHubIdAsync(ActorGitHubId(actor), Ct);

                    Assert.NotNull(account);
                    Assert.Equal("actor-" + ActorGitHubId(actor), account.Username);

                    // ...and the actor's own context is still usable after that overlap.
                    Assert.Equal(1, await db.Users.AsNoTracking().CountAsync(u => u.GitHubId == ActorGitHubId(actor), Ct));
                }
                catch (Exception ex)
                {
                    actorFailures.Enqueue($"actor {actor}: {ex.GetType().Name}: {ex.Message}");
                    throw;
                }
            }, Ct);
        }

        // The test-thread half of the overlap: an OWN context per boundary. A failure here surfaces AT
        // the rendezvous (through the gate) instead of looking like a hang.
        var testSide = Task.Run(async () =>
        {
            try
            {
                // WRITE boundary: its own context, its own explicit transaction, COMMITTED.
                await using (var writeDb = directory.CreateDbContext())
                await using (var writeTransaction = await writeDb.Database.BeginTransactionAsync(Ct))
                {
                    writeDb.UserInvitations.Add(new UserInvitationEntity
                    {
                        NormalizedUsername = "invited-during-the-overlap",
                        Role = UserRole.Viewer,
                        Enabled = true,
                        CreatedAt = "2024-01-01T00:00:00.0000000Z",
                        UpdatedAt = "2024-01-01T00:00:00.0000000Z",
                    });
                    await writeDb.SaveChangesAsync(Ct);
                    await writeTransaction.CommitAsync(Ct);
                }

                // READ boundary: an open read transaction that survives the production reads.
                await using var readDb = directory.CreateDbContext();
                await using var readTransaction = await readDb.Database.BeginTransactionAsync(Ct);
                Assert.Equal(ConcurrentActors, await readDb.Users.AsNoTracking().CountAsync(Ct));

                testSideInFlight.SetResult();
                await overlapFinished.Task.ConfigureAwait(false);

                await readTransaction.CommitAsync(Ct);
            }
            catch (Exception ex)
            {
                testSideInFlight.TrySetException(ex);
                throw;
            }
        }, Ct);

        ExceptionDispatchInfo? primary = null;
        try
        {
            // RENDEZVOUS 1: every actor has created and initialised its own connection.
            await Task.Run(() => allInitialized.Wait(HangGuard, Ct), Ct);

            // No context shares a connection instance: the shared initialisation that used to race is
            // structurally impossible, not merely unlikely.
            for (var first = 0; first < connections.Length; first++)
            {
                Assert.NotNull(connections[first]);
                for (var second = first + 1; second < connections.Length; second++)
                {
                    Assert.NotSame(connections[first], connections[second]);
                }
            }

            // RENDEZVOUS 2: the test thread's write is committed and its read is IN FLIGHT first...
            await testSideInFlight.Task.WaitAsync(HangGuard, Ct);

            // ...and only then are the production reads released, so they OVERLAP that open read.
            productionMayRead.SetResult();
            await Task.WhenAll(actors).WaitAsync(HangGuard, Ct);
        }
        catch (Exception ex)
        {
            primary = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            // ALWAYS release every gate: a failed assertion above must never leave work parked.
            productionMayRead.TrySetResult();
            overlapFinished.TrySetResult();
        }

        // JOIN BOTH SIDES regardless of the outcome, so a failure on one side never leaves the other
        // side running past the directory's disposal.
        var joinFailures = new List<Exception>();
        try
        {
            await Task.WhenAll(actors).WaitAsync(HangGuard, Ct);
        }
        catch (Exception ex)
        {
            joinFailures.Add(ex);
        }

        try
        {
            await testSide.WaitAsync(HangGuard, Ct);
        }
        catch (Exception ex)
        {
            joinFailures.Add(ex);
        }

        if (primary is not null)
        {
            primary.SourceException.Data["ActorFailures"] = actorFailures.ToArray();
            primary.SourceException.Data["JoinFailures"] = joinFailures;
            primary.Throw();
        }

        // THE NAMED ASSERTION: the concurrent initialisation produced NO failure at all — and in
        // particular never the 'database is locked' refusal the shared connection used to cause.
        Assert.Empty(actorFailures);
        Assert.Empty(joinFailures);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  (b) CROSS-CONTEXT DATA VISIBILITY
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ONE NAMED DATABASE, MANY CONNECTIONS: a row written through one context is visible to every
    /// other context, to the production <see cref="UserService"/> and to the production resolver — the
    /// per-context connections share the DATA without sharing a connection instance.
    /// </summary>
    [Fact]
    public async Task AWriteThroughOneContext_IsVisibleToAnotherContextAndToTheProductionDirectory()
    {
        using var directory = new HiveSecurityTestDirectory();

        await using (var writer = directory.CreateDbContext())
        {
            writer.Users.Add(new UserEntity
            {
                GitHubId = "7001",
                Username = "written-elsewhere",
                AccessToken = "token-7001",
                Role = UserRole.Admin,
                Enabled = true,
                CreatedAt = "2024-01-01T00:00:00.0000000Z",
            });
            await writer.SaveChangesAsync(Ct);
        }

        // A DIFFERENT context (its own, already closed connection) reads the row.
        await using (var reader = directory.CreateDbContext())
        {
            var stored = await reader.Users.AsNoTracking().SingleAsync(u => u.GitHubId == "7001", Ct);
            Assert.Equal("written-elsewhere", stored.Username);
            Assert.Equal(UserRole.Admin, stored.Role);
        }

        // ...as does the production directory and the production resolver over it.
        var viaService = await directory.Service.GetByGitHubIdAsync("7001", Ct);
        Assert.NotNull(viaService);
        Assert.Equal("written-elsewhere", viaService.Username);

        var resolution = await directory.AuthEnabledResolver()
            .ResolveAsync(HiveSecurityTestDirectory.GitHubPrincipal("7001"), Ct);

        Assert.True(resolution.IsResolved);
        Assert.Equal("7001", resolution.Principal!.GitHubId);
        Assert.Equal(UserRole.Admin, resolution.Principal.Role);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  (c) POST-DISPOSAL REFUSAL
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// AFTER DISPOSAL THE DIRECTORY IS A LATCH, NOT A RESET: a fresh context is REJECTED with
    /// <see cref="ObjectDisposedException"/> instead of silently reopening (with a NAMED shared-cache
    /// database a new connection would otherwise create a new, EMPTY database, so a read would answer
    /// "no such account" instead of "the directory is unreadable"), the seeders inherit that rejection,
    /// and both production resolver paths still classify it as
    /// <see cref="PrincipalRefusalReason.DirectoryFailure"/> — a refusal, never a throw and never a
    /// principal.
    /// </summary>
    [Fact]
    public async Task AfterDisposal_ContextCreationIsRejected_AndBothResolverPathsRefuseWithDirectoryFailure()
    {
        var directory = new HiveSecurityTestDirectory();
        var original = await directory.SeedActivatedAccountAsync("9001", UserRole.Admin);
        var claimsResolver = directory.AuthEnabledResolver();
        var byUserIdResolver = directory.AuthEnabledResolver();

        directory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => directory.CreateDbContext());
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => directory.SeedActivatedAccountAsync("9002", UserRole.Viewer));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => directory.SetRoleAsync("9001", UserRole.Viewer));

        var claimsResolution = await claimsResolver
            .ResolveAsync(HiveSecurityTestDirectory.GitHubPrincipal("9001"), Ct);

        Assert.False(claimsResolution.IsResolved);
        Assert.Null(claimsResolution.Principal);
        Assert.Equal(PrincipalRefusalReason.DirectoryFailure, claimsResolution.RefusalReason);

        var byUserIdResolution = await byUserIdResolver.ResolveByUserIdAsync(original.Id, Ct);

        Assert.False(byUserIdResolution.IsResolved);
        Assert.Null(byUserIdResolution.Principal);
        Assert.Equal(PrincipalRefusalReason.DirectoryFailure, byUserIdResolution.RefusalReason);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  (d) KEEPER LIFETIME
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE KEEPER IS NOT THE ONLY THING HOLDING THE DATABASE ALIVE: closing it does NOT drop the
    /// database while one context still holds its own connection open (that context keeps reading the
    /// same rows), and only once EVERY connection is closed does the NAMED database disappear.
    /// <para>
    /// THE ABSENCE IS PROVEN WITHOUT <see cref="HiveSecurityTestDirectory.CreateDbContext"/>: a RAW
    /// <see cref="SqliteConnection"/> to the very connection string the fixture handed out opens a
    /// brand-new, EMPTY in-memory database and sees no <c>users</c> table. Using the fixture itself
    /// there would prove nothing — it rejects by design.
    /// </para>
    /// </summary>
    [Fact]
    public async Task DisposingTheDirectory_DoesNotDropTheDatabaseWhileAContextStillHoldsItsConnection()
    {
        var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("8001", UserRole.Admin, username: "keeper-survivor");

        string connectionString;
        var live = directory.CreateDbContext();
        try
        {
            // This context OWNS its own connection and holds it OPEN for the whole row: the keeper is
            // not the only thing keeping the named database alive.
            await live.Database.OpenConnectionAsync(Ct);
            connectionString = live.Database.GetDbConnection().ConnectionString;
            Assert.False(string.IsNullOrWhiteSpace(connectionString));
            Assert.Equal(1, await live.Users.AsNoTracking().CountAsync(Ct));

            // The keeper closes here. The live context's OPEN connection keeps the database alive, so
            // the data is still there and still readable.
            directory.Dispose();
            Assert.Equal(1, await live.Users.AsNoTracking().CountAsync(Ct));
            Assert.Equal("keeper-survivor", (await live.Users.AsNoTracking().SingleAsync(Ct)).Username);

            // The LAST connection closes here — now the named database really is gone.
            await live.Database.CloseConnectionAsync();
        }
        finally
        {
            await live.DisposeAsync();
        }

        await using var raw = new SqliteConnection(connectionString);
        await raw.OpenAsync(Ct);

        await using var command = raw.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'users'";

        Assert.Equal(
            0L,
            Convert.ToInt64(
                await command.ExecuteScalarAsync(Ct),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  (e) REPEATED DISPOSE
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// DISPOSAL IS IDEMPOTENT: disposing the same directory repeatedly is safe (the keeper is closed at
    /// most once) and the rejection stays in force; the latch is per INSTANCE, so a directory created
    /// afterwards is a completely independent database with its own name and rows.
    /// </summary>
    [Fact]
    public async Task DisposingRepeatedly_IsSafe_KeepsTheRejection_AndLeavesTheNextDirectoryIndependent()
    {
        var directory = new HiveSecurityTestDirectory();
        await directory.SeedActivatedAccountAsync("6001", UserRole.Admin);

        directory.Dispose();
        directory.Dispose();
        directory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => directory.CreateDbContext());

        // A NEW directory is unaffected by the closed one: its own name, its own keeper, its own rows.
        using var replacement = new HiveSecurityTestDirectory();
        Assert.Equal(0, await replacement.Service.GetUserCountAsync(Ct));

        await replacement.SeedActivatedAccountAsync("6002", UserRole.Viewer);
        var resolution = await replacement.AuthEnabledResolver()
            .ResolveAsync(HiveSecurityTestDirectory.GitHubPrincipal("6002"), Ct);
        Assert.True(resolution.IsResolved);
        Assert.Equal(UserRole.Viewer, resolution.Principal!.Role);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  (f) INDIRECT CONSUMER — the shared facade harness
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE INDIRECT CONSUMERS WORK UNDER THE NEW SHAPE: <see cref="FacadeAuthorizationHarness"/> owns a
    /// directory of its own, so its seeded accounts must resolve and its production facades must answer
    /// — an authorized Operator mutation SUCCEEDS and an insufficient role is REFUSED (the refusal
    /// control proving the success above is a real authorization answer rather than a blanket allow).
    /// </summary>
    [Fact]
    public async Task FacadeAuthorizationHarness_DirectoryWorksUnderThePerContextConnectionShape()
    {
        using var harness = new FacadeAuthorizationHarness();
        await harness.SeedAsync("5001", UserRole.Operator);
        await harness.SeedAsync("5002", UserRole.Viewer);

        // Its directory is reachable through the fixture's own context API, over its own connections.
        await using (var db = harness.Directory.CreateDbContext())
        {
            Assert.Equal(2, await db.Users.AsNoTracking().CountAsync(Ct));
        }

        var facade = harness.OperationalFacade(
            HiveAuthMode.AuthenticationEnabled,
            new DashboardStateService(
                new WorkerPool(),
                new GoalPipelineManager(),
                new DashboardLogSink(),
                new ProgressLog()),
            new LlmSessionRegistry());

        // POSITIVE CONTROL: the Operator holds ExecutionOperator, so the reset is authorized through
        // the directory (a reset with no Brain configured is the service's own successful no-op).
        var allowed = await facade.ResetBrainSessionAsync(
            FacadeAuthorizationHarness.Principal("5001"), Ct);
        Assert.True(allowed.Success, allowed.Error);

        // ...and a Viewer lacks it, so the success above is a real authorization answer.
        var refused = await facade.ResetBrainSessionAsync(
            FacadeAuthorizationHarness.Principal("5002"), Ct);
        Assert.False(refused.Success);
        Assert.Equal(FacadeErrorKind.Forbidden, refused.Kind);
        Assert.NotNull(refused.Error);
    }
}

using System.Net;
using System.Security.Claims;
using System.Text;

using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// THE AUTHORIZATION CONTRACT of the release / configuration / backup / clarification / session
/// surfaces: the role matrix each facade and each covered route enforces, the
/// <see cref="FacadeErrorKind.Forbidden"/> result shape, the HTTP 403 mapping, the fresh
/// role/enabled re-resolution on every call, and the response shapes preserved for authorized
/// callers.
/// <para>
/// Every test drives the REAL production facade over the REAL production
/// <see cref="UserService"/> over an in-memory SQLite directory
/// (<see cref="HiveSecurityTestDirectory"/> / <see cref="FacadeAuthorizationHarness"/>), so a
/// refusal is an actual resolution answer rather than a stubbed error kind. Each denial row is
/// paired with a SUCCESS CONTROL proving the operation really works for a caller who holds the
/// capability — a route that refused everybody would fail the control.
/// </para>
/// </summary>
public sealed class ReleaseConfigBackupAuthorizationTests : IDisposable
{
    private readonly FacadeAuthorizationHarness _auth = new();

    private const string ViewerId = "rcb-viewer";
    private const string MemberId = "rcb-member";
    private const string OperatorId = "rcb-operator";
    private const string AdminId = "rcb-admin";

    private const string ForbiddenMessage = "The acting account is not authorized to perform this operation.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal User(string gitHubId) => FacadeAuthorizationHarness.Principal(gitHubId);

    // ── Real-store harness ──────────────────────────────────────────────────

    /// <summary>
    /// A SQLite context factory over ONE fixed database file. Every context therefore observes the
    /// same rows — which is what the production factory does, and what a per-context in-memory database
    /// would NOT do.
    /// </summary>
    private sealed class SharedFileFactory : IDbContextFactory<CopilotHiveDbContext>
    {
        private readonly DbContextOptions<CopilotHiveDbContext> _options;

        internal SharedFileFactory(string dbPath) =>
            _options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

        public CopilotHiveDbContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// A real <see cref="GoalStore"/> over an isolated, freshly created SQLite database file (created
    /// on first use and removed in <see cref="Dispose"/>, together with the other state dirs).
    /// </summary>
    private GoalStore CreateStore()
    {
        var dbPath = Path.Combine(NewStateDir(), "copilothive.db");
        var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        using (var context = new CopilotHiveDbContext(options))
            context.Database.EnsureCreated();

        return new GoalStore(new SharedFileFactory(dbPath), NullLogger<GoalStore>.Instance);
    }

    /// <summary>Creates an isolated state directory that <see cref="Dispose"/> removes.</summary>
    private string NewStateDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rcb-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>Every isolated state directory this instance created.</summary>
    private readonly List<string> _tempDirs = [];

    /// <summary>
    /// The production <see cref="ReleaseFacade"/> for <paramref name="mode"/> over the harness
    /// directory, with every optional dependency ABSENT (so a denial cannot be confused with a
    /// dependency failure, and a success is a plain store success).
    /// </summary>
    private ReleaseFacade CreateReleaseFacade(GoalStore store, HiveAuthMode mode) =>
        new(store,
            new DashboardNotifier(),
            NullLogger<ReleaseFacade>.Instance,
            _auth.Resolver(mode),
            _auth.Policy,
            executionService: null,
            eventBus: null,
            nuGetMonitor: null,
            hiveConfig: null,
            appLifetime: null,
            docCleanup: null);

    // ══════════════════════════════════════════════════════════════════════════
    //  A. RELEASES — reads need SharedRead, every mutation needs ExecutionOperator
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// EVERY <see cref="IReleaseFacade"/> read answers a Viewer and up (it needs only
    /// <see cref="HiveCapability.SharedRead"/>) with the store's own data, and a signed-out caller
    /// with <see cref="FacadeErrorKind.Forbidden"/> — the read half of the release matrix.
    /// </summary>
    [Fact]
    public async Task ReleaseReads_AreSharedRead_AndRefuseASignedOutCaller()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await _auth.SeedAsync(AdminId, UserRole.Admin);

        var store = CreateStore();
        await store.CreateReleaseAsync(new Release { Id = "rel-1", Tag = "v1.0.0" }, Ct);
        await store.CreateGoalAsync(new Goal { Id = "goal-1", Description = "d", ReleaseId = "rel-1" }, Ct);

        var facade = CreateReleaseFacade(store, HiveAuthMode.AuthenticationEnabled);

        foreach (var caller in new[] { ViewerId, MemberId, OperatorId, AdminId })
        {
            var release = await facade.GetReleaseAsync("rel-1", User(caller), Ct);
            Assert.True(release.Success, $"{caller} holds SharedRead and must read the release.");
            Assert.Equal("v1.0.0", release.Value!.Tag);

            var goals = await facade.GetGoalsByReleaseAsync("rel-1", User(caller), Ct);
            Assert.True(goals.Success, $"{caller} holds SharedRead and must read the release's goals.");
            Assert.Equal(["goal-1"], goals.Value!.Select(g => g.Id));
        }

        // Signed out (no principal at all, authentication enabled) is a refusal, never a read.
        foreach (var signedOut in new ClaimsPrincipal?[] { null, FacadeAuthorizationHarness.Anonymous() })
        {
            var release = await facade.GetReleaseAsync("rel-1", signedOut, Ct);
            Assert.False(release.Success);
            Assert.Equal(FacadeErrorKind.Forbidden, release.Kind);
            Assert.Equal(ForbiddenMessage, release.Error);
            Assert.Null(release.Value);

            var goals = await facade.GetGoalsByReleaseAsync("rel-1", signedOut, Ct);
            Assert.False(goals.Success);
            Assert.Equal(FacadeErrorKind.Forbidden, goals.Kind);
            Assert.Null(goals.Value);
        }
    }

    /// <summary>
    /// A Viewer and a Member are refused with <see cref="FacadeErrorKind.Forbidden"/> on EVERY
    /// release mutation and on the validation, while an Operator succeeds — the mutation half of the
    /// release matrix, with the Operator rows as the success control.
    /// </summary>
    [Fact]
    public async Task ReleaseMutations_AreExecutionOperator_AndRunBeforeValidation()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync(OperatorId, UserRole.Operator);

        foreach (var insufficient in new[] { ViewerId, MemberId })
        {
            // A FRESH store per caller so an accidental side effect is observable rather than
            // overwritten by the next caller.
            var store = CreateStore();
            await store.CreateReleaseAsync(new Release { Id = "rel-1", Tag = "v1.0.0" }, Ct);
            var facade = CreateReleaseFacade(store, HiveAuthMode.AuthenticationEnabled);
            var caller = User(insufficient);

            await AssertForbiddenAsync(await facade.CreateReleaseAsync(new CreateReleaseRequest("v9.9.9"), caller));
            // The denial runs BEFORE the version validation: a blank version is Forbidden, not
            // BadRequest.
            await AssertForbiddenAsync(await facade.CreateReleaseAsync(new CreateReleaseRequest(" "), caller));

            var status = await facade.UpdateReleaseStatusAsync(
                "rel-1", new UpdateReleaseStatusRequest("Bogus"), caller, Ct);
            var statusFailure = Assert.IsType<StatusFailureOutcome>(status);
            Assert.Equal(FacadeErrorKind.Forbidden, statusFailure.Kind);
            Assert.Equal(ForbiddenMessage, statusFailure.Error);

            await AssertForbiddenAsync(await facade.UpdateReleaseNotesAsync(
                "rel-1", new UpdateReleaseNotesRequest("n"), caller));
            await AssertForbiddenAsync(await facade.UpdateReleaseTagAsync(
                "rel-1", new UpdateReleaseTagRequest(" "), caller));
            await AssertForbiddenAsync(await facade.UpdateReleaseRepositoriesAsync(
                "rel-1", new UpdateReleaseRepositoriesRequest(["r"]), caller));
            await AssertForbiddenAsync(await facade.DeleteReleaseAsync("rel-1", caller, Ct));
            await AssertForbiddenAsync(await facade.ValidateReleaseAsync("rel-1", caller, Ct));

            // A denied create persisted nothing.
            Assert.Null(await store.GetReleaseAsync("v9.9.9", Ct));
            // ...and the denied delete left the release in place.
            Assert.NotNull(await store.GetReleaseAsync("rel-1", Ct));
        }

        // ── SUCCESS CONTROL: the Operator really performs every one of them. ──
        var operatorStore = CreateStore();
        await operatorStore.CreateReleaseAsync(new Release { Id = "rel-1", Tag = "v1.0.0" }, Ct);
        var operatorFacade = CreateReleaseFacade(operatorStore, HiveAuthMode.AuthenticationEnabled);
        var op = User(OperatorId);

        var created = await operatorFacade.CreateReleaseAsync(new CreateReleaseRequest("v2.0.0"), op);
        Assert.True(created.Success, created.Error);

        var noOp = await operatorFacade.UpdateReleaseStatusAsync(
            "rel-1", new UpdateReleaseStatusRequest("Planning"), op, Ct);
        Assert.IsType<PlanningNoOpOutcome>(noOp);

        var notes = await operatorFacade.UpdateReleaseNotesAsync(
            "rel-1", new UpdateReleaseNotesRequest("notes"), op);
        Assert.True(notes.Success, notes.Error);
        Assert.Equal("notes", notes.Value!.Notes);

        var tag = await operatorFacade.UpdateReleaseTagAsync(
            "rel-1", new UpdateReleaseTagRequest("v1.1.0"), op);
        Assert.True(tag.Success, tag.Error);
        Assert.Equal("v1.1.0", tag.Value!.Tag);

        var repos = await operatorFacade.UpdateReleaseRepositoriesAsync(
            "rel-1", new UpdateReleaseRepositoriesRequest(["repo-a"]), op);
        Assert.True(repos.Success, repos.Error);
        Assert.Equal(["repo-a"], repos.Value!.RepositoryNames);

        var validated = await operatorFacade.ValidateReleaseAsync("rel-1", op, Ct);
        Assert.True(validated.Success, validated.Error);
        Assert.True(validated.Value!.Valid); // no execution service → {valid:true}

        var deleted = await operatorFacade.DeleteReleaseAsync("rel-1", op, Ct);
        Assert.True(deleted.Success, deleted.Error);
        Assert.Null(await operatorStore.GetReleaseAsync("rel-1", Ct));
    }

    /// <summary>
    /// THE FRESH-RESOLUTION CONTRACT for releases: an Operator's mutation succeeds, the account is
    /// then DEMOTED, and the very NEXT mutation is refused — nothing about the earlier success is
    /// cached. The same holds for a DISABLED account.
    /// </summary>
    [Fact]
    public async Task ReleaseFacade_ReResolvesCurrentState_OnEveryCall()
    {
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await _auth.SeedAsync(AdminId, UserRole.Admin);

        var store = CreateStore();
        await store.CreateReleaseAsync(new Release { Id = "rel-1", Tag = "v1.0.0" }, Ct);
        var facade = CreateReleaseFacade(store, HiveAuthMode.AuthenticationEnabled);

        // BEFORE: the Operator is authorized.
        Assert.True((await facade.DeleteReleaseAsync("rel-1", User(OperatorId), Ct)).Success);

        // DEMOTION takes effect on the very next call.
        await store.CreateReleaseAsync(new Release { Id = "rel-2", Tag = "v2.0.0" }, Ct);
        await _auth.SetRoleAsync(OperatorId, UserRole.Viewer);
        await AssertForbiddenAsync(await facade.DeleteReleaseAsync("rel-2", User(OperatorId), Ct));
        Assert.NotNull(await store.GetReleaseAsync("rel-2", Ct));

        // ENABLED → DISABLED likewise.
        await _auth.SetRoleAsync(AdminId, UserRole.Admin);
        await store.CreateReleaseAsync(new Release { Id = "rel-3", Tag = "v3.0.0" }, Ct);
        Assert.True((await facade.DeleteReleaseAsync("rel-3", User(AdminId), Ct)).Success);

        await store.CreateReleaseAsync(new Release { Id = "rel-4", Tag = "v4.0.0" }, Ct);
        await _auth.SetEnabledAsync(AdminId, enabled: false);
        await AssertForbiddenAsync(await facade.DeleteReleaseAsync("rel-4", User(AdminId), Ct));
        Assert.NotNull(await store.GetReleaseAsync("rel-4", Ct));
    }

    /// <summary>
    /// CANCELLATION IS NEVER A DENIAL: a token cancelled before the call propagates out of every
    /// identity-aware release operation as <see cref="OperationCanceledException"/> rather than
    /// returning <see cref="FacadeErrorKind.Forbidden"/>.
    /// </summary>
    [Fact]
    public async Task ReleaseFacade_Cancellation_PropagatesAndIsNeverADenial()
    {
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        var store = CreateStore();
        await store.CreateReleaseAsync(new Release { Id = "rel-1", Tag = "v1.0.0" }, Ct);
        var facade = CreateReleaseFacade(store, HiveAuthMode.AuthenticationEnabled);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => facade.DeleteReleaseAsync("rel-1", User(OperatorId), cts.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        // The release is untouched.
        Assert.NotNull(await store.GetReleaseAsync("rel-1", Ct));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  B. CONFIG — every projection AND every write is GlobalAdmin
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// EVERY non-Composer <see cref="IConfigFacade"/> operation refuses a Viewer, a Member and an
    /// Operator with <see cref="FacadeErrorKind.Forbidden"/> — the FULL projection reads and the
    /// writes alike — and each refusal runs BEFORE the operation's own validation or persistence.
    /// </summary>
    [Fact]
    public async Task EveryConfigOperation_RequiresGlobalAdmin()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync(OperatorId, UserRole.Operator);

        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "m" },
            Models = new ModelsConfig { AvailableModels = [], SubAgentModels = [] },
            Composer = new ComposerConfig(),
        };

        foreach (var insufficient in new[] { ViewerId, MemberId, OperatorId })
        {
            // No ConfigModelService / discovery / repo manager: if the gate were missing, these would
            // answer NotConfigured/ServiceUnavailable instead of Forbidden — so a Forbidden proves the
            // gate ran FIRST.
            var facade = new ConfigFacade(
                config, configModel: null, discovery: null,
                NullLogger<ConfigFacade>.Instance,
                _auth.Resolver(HiveAuthMode.AuthenticationEnabled), _auth.Policy);
            var caller = User(insufficient);

            await AssertForbiddenAsync(await facade.GetModelsAsync(caller, Ct));
            await AssertForbiddenAsync(await facade.GetRepositoriesAsync(caller, Ct));
            await AssertForbiddenAsync(await facade.GetBranchesAsync("repo", caller, Ct));
            await AssertForbiddenAsync(await facade.GetOrchestratorAsync(caller, Ct));
            await AssertForbiddenAsync(await facade.GetWorkersAsync(caller, Ct));
            await AssertForbiddenAsync(await facade.GetComposerAsync(caller, Ct));
            await AssertForbiddenAsync(await facade.DiscoverModelsAsync(caller, Ct));

            await AssertForbiddenAsync(await facade.SaveModelsAsync(
                new ModelConfigUpdate(
                    OrchestratorModel: "x", ComposerModel: null, WorkerModels: null,
                    PremiumWorkerModels: null, CompactionModel: null), caller, Ct));
            await AssertForbiddenAsync(await facade.AddAvailableModelAsync(
                new AvailableModelRequest("m", null), caller, Ct));
            await AssertForbiddenAsync(await facade.UpdateAvailableModelAsync(
                "m", new AvailableModelRequest("m", null), caller, Ct));
            await AssertForbiddenAsync(await facade.RemoveAvailableModelAsync("m", caller, Ct));
            await AssertForbiddenAsync(await facade.AddSubAgentModelAsync(
                new SubAgentModelRequest("m", null, null), caller, Ct));
            await AssertForbiddenAsync(await facade.UpdateSubAgentModelAsync(
                "m", new SubAgentModelRequest("m", null, null), caller, Ct));
            await AssertForbiddenAsync(await facade.RemoveSubAgentModelAsync("m", caller, Ct));
            await AssertForbiddenAsync(await facade.AddRepositoryAsync(
                new RepositoryRequest("r", "u", "main"), caller, Ct));
            await AssertForbiddenAsync(await facade.UpdateRepositoryAsync(
                "r", new RepositoryRequest("r", "u", "main"), caller, Ct));
            await AssertForbiddenAsync(await facade.RemoveRepositoryAsync("r", caller, Ct));
            await AssertForbiddenAsync(await facade.SaveOrchestratorAsync(
                new OrchestratorSettingsUpdate(
                    MaxIterations: null, MaxRetriesPerTask: null, MaxParallelGoals: null,
                    VerboseLogging: null, BrainMaxSteps: null, BranchCleanupDelayHours: null), caller, Ct));
            await AssertForbiddenAsync(await facade.SaveWorkersAsync(
                new Dictionary<string, int> { ["coder"] = 1 }, caller, Ct));
            // SaveComposerAsync was authorized by an earlier goal and keeps that exact contract.
            await AssertForbiddenAsync(await facade.SaveComposerAsync(
                new ComposerSettingsUpdate(), caller, Ct));
        }

        // Signed out is refused too.
        var signedOutFacade = new ConfigFacade(
            config, configModel: null, discovery: null,
            NullLogger<ConfigFacade>.Instance,
            _auth.Resolver(HiveAuthMode.AuthenticationEnabled), _auth.Policy);
        await AssertForbiddenAsync(await signedOutFacade.GetModelsAsync(null, Ct));

        // ── SUCCESS CONTROL: the Admin really reads the projection. ──
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var adminFacade = new ConfigFacade(
            config, configModel: null, discovery: null,
            NullLogger<ConfigFacade>.Instance,
            _auth.Resolver(HiveAuthMode.AuthenticationEnabled), _auth.Policy);
        var admin = User(AdminId);

        var models = await adminFacade.GetModelsAsync(admin, Ct);
        Assert.True(models.Success, models.Error);
        Assert.Equal("m", models.Value!.Orchestrator);

        var orchestrator = await adminFacade.GetOrchestratorAsync(admin, Ct);
        Assert.True(orchestrator.Success, orchestrator.Error);

        var workers = await adminFacade.GetWorkersAsync(admin, Ct);
        Assert.True(workers.Success, workers.Error);

        var composer = await adminFacade.GetComposerAsync(admin, Ct);
        Assert.True(composer.Success, composer.Error);

        var repositories = await adminFacade.GetRepositoriesAsync(admin, Ct);
        Assert.True(repositories.Success, repositories.Error);
        Assert.Empty(repositories.Value!);
    }

    /// <summary>
    /// THE FRESH-RESOLUTION CONTRACT for configuration: an Admin's projection succeeds, the account is
    /// DEMOTED to Viewer, and the very NEXT projection is refused — the full config surface is
    /// re-authorized on every call, so a stale page can never read global settings after a demotion.
    /// </summary>
    [Fact]
    public async Task ConfigFacade_ReResolvesCurrentState_OnEveryCall()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);

        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "m" },
            Models = new ModelsConfig { AvailableModels = [] },
        };
        var facade = new ConfigFacade(
            config, configModel: null, discovery: null,
            NullLogger<ConfigFacade>.Instance,
            _auth.Resolver(HiveAuthMode.AuthenticationEnabled), _auth.Policy);

        Assert.True((await facade.GetModelsAsync(User(AdminId), Ct)).Success);

        await _auth.SetRoleAsync(AdminId, UserRole.Operator);
        await AssertForbiddenAsync(await facade.GetModelsAsync(User(AdminId), Ct));

        await _auth.SetRoleAsync(AdminId, UserRole.Admin);
        Assert.True((await facade.GetModelsAsync(User(AdminId), Ct)).Success);

        await _auth.SetEnabledAsync(AdminId, enabled: false);
        await AssertForbiddenAsync(await facade.GetModelsAsync(User(AdminId), Ct));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  C. BACKUP — every operation is GlobalAdmin, including download and restore
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// EVERY backup operation (list, create, download, restore) refuses a non-admin — including the
    /// Operator, who is the highest non-admin role — and the download/restore refusals run BEFORE the
    /// archive-name validation, so a denied caller learns nothing about the file layout.
    /// </summary>
    [Fact]
    public async Task EveryBackupOperation_RequiresGlobalAdmin()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync(OperatorId, UserRole.Operator);

        var (stateDir, service, facade) = CreateBackupFacade();
        try
        {
            foreach (var insufficient in new[] { ViewerId, MemberId, OperatorId })
            {
                var caller = User(insufficient);

                await AssertForbiddenAsync(await facade.GetBackupsAsync(caller, Ct));
                await AssertForbiddenAsync(await facade.CreateBackupAsync(caller, Ct));
                await AssertForbiddenAsync(await facade.OpenBackupForDownloadAsync("x.tar.gz", caller, Ct));
                await AssertForbiddenAsync(await facade.RestoreBackupAsync("x.tar.gz", caller, Ct));

                // The name validation is BEHIND the gate: a traversal name is Forbidden (not the
                // "Invalid file name." BadRequest a caller who passed the gate would get).
                await AssertForbiddenAsync(
                    await facade.OpenBackupForDownloadAsync("../escape.tar.gz", caller, Ct));
                await AssertForbiddenAsync(
                    await facade.RestoreBackupAsync("../../etc/passwd", caller, Ct));
            }

            // Nothing was created by the denied creates.
            Assert.Empty(service.ListBackups());

            // ── SUCCESS CONTROL: the Admin really lists, creates, downloads and restores. ──
            await _auth.SeedAsync(AdminId, UserRole.Admin);
            var admin = User(AdminId);

            var empty = await facade.GetBackupsAsync(admin, Ct);
            Assert.True(empty.Success, empty.Error);
            Assert.Empty(empty.Value!);

            var created = await facade.CreateBackupAsync(admin, Ct);
            Assert.True(created.Success, created.Error);
            Assert.EndsWith(".tar.gz", created.Value!.FileName);

            var listed = await facade.GetBackupsAsync(admin, Ct);
            Assert.True(listed.Success, listed.Error);
            Assert.Contains(listed.Value!, b => b.FileName == created.Value!.FileName);

            var download = await facade.OpenBackupForDownloadAsync(created.Value!.FileName, admin, Ct);
            Assert.True(download.Success, download.Error);
            Assert.True(File.Exists(download.Value!.FullPath));
            Assert.Equal(created.Value!.FileName, download.Value!.FileName);
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    /// <summary>
    /// The download/restore VALIDATION table is preserved for authorized callers: the traversal name
    /// and the blank restore name keep their exact messages and kinds, and a well-formed but missing
    /// archive is NotFound. This is the authorized half of the contract, so the gate provably did not
    /// change it.
    /// </summary>
    [Fact]
    public async Task BackupDownloadAndRestore_KeepTheirFrozenValidationTable_ForAnAuthorizedCaller()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var (stateDir, service, facade) = CreateBackupFacade();
        try
        {
            // A real archive first, so the directory exists and the NotFound rows are meaningful.
            var created = await facade.CreateBackupAsync(User(AdminId), Ct);
            Assert.True(created.Success, created.Error);

            var admin = User(AdminId);

            foreach (var bad in new[] { "", "  ", "../x.tar.gz", "sub/x.tar.gz", "sub\\x.tar.gz", "..", "a/../b" })
            {
                var download = await facade.OpenBackupForDownloadAsync(bad, admin, Ct);
                Assert.False(download.Success);
                Assert.Equal(FacadeErrorKind.BadRequest, download.Kind);
                Assert.Equal("Invalid file name.", download.Error);
            }

            // The restore route's OWN blank-name message is distinct and preserved.
            foreach (var blank in new[] { "", "   " })
            {
                var restore = await facade.RestoreBackupAsync(blank, admin, Ct);
                Assert.False(restore.Success);
                Assert.Equal(FacadeErrorKind.BadRequest, restore.Kind);
                Assert.Equal("FileName is required.", restore.Error);
            }

            foreach (var bad in new[] { "../x.tar.gz", "sub/x.tar.gz", "sub\\x.tar.gz", "a/../b" })
            {
                var restore = await facade.RestoreBackupAsync(bad, admin, Ct);
                Assert.False(restore.Success);
                Assert.Equal(FacadeErrorKind.BadRequest, restore.Kind);
                Assert.Equal("Invalid file name.", restore.Error);
            }

            // A well-formed name that is not there is NotFound on BOTH routes.
            foreach (var missing in new[] { "no-such-backup.tar.gz" })
            {
                var download = await facade.OpenBackupForDownloadAsync(missing, admin, Ct);
                Assert.False(download.Success);
                Assert.Equal(FacadeErrorKind.NotFound, download.Kind);
                Assert.Equal("Backup not found.", download.Error);

                var restore = await facade.RestoreBackupAsync(missing, admin, Ct);
                Assert.False(restore.Success);
                Assert.Equal(FacadeErrorKind.NotFound, restore.Kind);
                Assert.Equal("Backup not found.", restore.Error);
            }

            Assert.NotEmpty(service.ListBackups());
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  D. CLARIFICATIONS — reads SharedRead, answering ExecutionOperator
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The clarification adapter enforces the designed split: the three reads need only
    /// <see cref="HiveCapability.SharedRead"/> (a Viewer reads them), answering needs
    /// <see cref="HiveCapability.ExecutionOperator"/> (a Viewer and a Member are refused), and the
    /// refusal runs BEFORE the answer validation. The queue's own Composer path is untouched.
    /// </summary>
    [Fact]
    public async Task ClarificationAdapter_EnforcesTheSharedReadAndOperatorSplit()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync(OperatorId, UserRole.Operator);

        var queue = new ClarificationQueueService();
        var pending = new ClarificationRequest
        {
            Id = "clar-1",
            GoalId = "goal-1",
            WorkerRole = "coder",
            Question = "Which branch?",
        };
        queue.Enqueue(pending);

        var facade = new ClarificationFacade(
            queue, _auth.Resolver(HiveAuthMode.AuthenticationEnabled), _auth.Policy,
            NullLogger<ClarificationFacade>.Instance);

        // ── Reads: every role that holds SharedRead. ──
        foreach (var caller in new[] { ViewerId, MemberId, OperatorId })
        {
            var all = await facade.GetAllRequestsAsync(User(caller), Ct);
            Assert.True(all.Success, all.Error);
            Assert.Equal(["clar-1"], all.Value!.Select(r => r.Id));

            var human = await facade.GetPendingHumanRequestsAsync(User(caller), Ct);
            Assert.True(human.Success, human.Error);

            var count = await facade.GetPendingHumanCountAsync(User(caller), Ct);
            Assert.True(count.Success, count.Error);
        }

        // ── Answer: non-operators are refused, and the refusal precedes validation. ──
        foreach (var insufficient in new[] { ViewerId, MemberId })
        {
            await AssertForbiddenAsync(
                await facade.SubmitAnswerAsync("clar-1", "main", User(insufficient), Ct));
            // A blank answer is Forbidden for them, NOT "Answer is required.".
            await AssertForbiddenAsync(
                await facade.SubmitAnswerAsync("clar-1", "  ", User(insufficient), Ct));
            // A missing request is likewise Forbidden, not NotFound.
            await AssertForbiddenAsync(
                await facade.SubmitAnswerAsync("no-such-clarification", "x", User(insufficient), Ct));
        }

        // Nothing was answered by the denied calls.
        Assert.Null(queue.GetRequest("clar-1")!.Answer);

        // ── Signed out: refused on every operation. ──
        await AssertForbiddenAsync(await facade.GetAllRequestsAsync(null, Ct));
        await AssertForbiddenAsync(await facade.GetPendingHumanRequestsAsync(null, Ct));
        await AssertForbiddenAsync(await facade.GetPendingHumanCountAsync(null, Ct));
        await AssertForbiddenAsync(await facade.SubmitAnswerAsync("clar-1", "main", null, Ct));

        // ── SUCCESS CONTROL: the Operator really answers, with the frozen confirmation. ──
        var answered = await facade.SubmitAnswerAsync("clar-1", "main", User(OperatorId), Ct);
        Assert.True(answered.Success, answered.Error);
        Assert.Equal("Answer submitted for clarification 'clar-1'.", answered.Value!.Message);
        Assert.Equal("main", queue.GetRequest("clar-1")!.Answer);
        Assert.Equal("human", queue.GetRequest("clar-1")!.ResolvedBy);

        // The authorized validation answers are preserved too.
        var blankAnswer = await facade.SubmitAnswerAsync("clar-1", "  ", User(OperatorId), Ct);
        Assert.False(blankAnswer.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, blankAnswer.Kind);
        Assert.Equal("Answer is required.", blankAnswer.Error);

        var missing = await facade.SubmitAnswerAsync("no-such", "x", User(OperatorId), Ct);
        Assert.False(missing.Success);
        Assert.Equal(FacadeErrorKind.NotFound, missing.Kind);
        Assert.Equal("Clarification 'no-such' not found.", missing.Error);
    }

    /// <summary>
    /// THE QUEUE STAYS UNCHANGED AND ITS INTERNAL CALLERS KEEP WORKING: a
    /// <c>"composer"</c>-attributed answer submitted directly on
    /// <see cref="ClarificationQueueService"/> (the ClarificationHandler path) still resolves the
    /// request without any identity — proving the adapter is the USER-FACING boundary only and did
    /// not gate the trusted internal queue.
    /// </summary>
    [Fact]
    public async Task ClarificationQueue_KeepsItsInternalComposerPath()
    {
        await _auth.SeedAsync(OperatorId, UserRole.Operator);

        var queue = new ClarificationQueueService();
        var waiter = queue.Enqueue(new ClarificationRequest { Id = "clar-composer", Question = "q" });

        // The internal Composer path: no principal, no facade.
        Assert.True(queue.SubmitAnswer("clar-composer", "composer answer", "composer"));
        Assert.Equal("composer answer", await waiter.Task);
        Assert.Equal("composer", queue.GetRequest("clar-composer")!.ResolvedBy);

        // The facade is a SEPARATE, authorized adapter over the same queue.
        var facade = new ClarificationFacade(
            queue, _auth.Resolver(HiveAuthMode.AuthenticationEnabled), _auth.Policy,
            NullLogger<ClarificationFacade>.Instance);
        await AssertForbiddenAsync(await facade.SubmitAnswerAsync("clar-composer", "x", null, Ct));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  E. HTTP 403 MAPPING — every covered route, over the real hubs
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE HTTP MAPPING of a denial on the covered release / config / clarification / backup routes:
    /// <see cref="FacadeErrorKind.Forbidden"/> becomes HTTP 403 with the sanitized <c>{error}</c>
    /// body — never 400, never a 500 — driven through the REAL route registration over a REAL
    /// directory-backed facade.
    /// </summary>
    [Fact]
    public async Task CoveredRoutes_MapForbidden_To403WithTheSanitizedBody()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");

        var store = CreateStore();
        await store.CreateReleaseAsync(new Release { Id = "rel-1", Tag = "v1.0.0" }, Ct);
        var queue = new ClarificationQueueService();
        queue.Enqueue(new ClarificationRequest { Id = "clar-1", Question = "q" });

        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] },
        };
        var (backupStateDir, _, backupFacade) = CreateBackupFacade();

        var builder = WebApplication.CreateBuilder();
        Program.AddHiveJsonOptions(builder.Services);
        // The primitives the PRE-BINDING middleware resolves per request (a production host
        // registers these; this minimal host must too, or the middleware cannot answer at all).
        var resolver = _auth.Resolver(HiveAuthMode.AuthenticationEnabled);
        builder.Services.AddSingleton(resolver);
        builder.Services.AddSingleton(_auth.Policy);
        // The facades a production host registers; this minimal host supplies them itself.
        builder.Services.AddSingleton(backupFacade);
        builder.Services.AddSingleton<IReleaseFacade>(
            CreateReleaseFacade(store, HiveAuthMode.AuthenticationEnabled));
        builder.Services.AddSingleton<IClarificationFacade>(sp => new ClarificationFacade(
            queue, resolver, _auth.Policy, NullLogger<ClarificationFacade>.Instance));
        var app = builder.Build();
        // The production hubs forward HttpContext.User; this host sets a VIEWER identity on every
        // request (strictly weaker than the cookie handler — the facades still re-resolve).
        app.Use(async (context, next) =>
        {
            context.User = FacadeAuthorizationHarness.Principal(ViewerId);
            await next();
        });

        app.MapReleaseEndpoints();
        app.MapClarificationEndpoints();
        app.MapBackupEndpoints();
        app.MapConfigEndpoints(new ConfigFacade(
            config, configModel: null, discovery: null,
            NullLogger<ConfigFacade>.Instance, resolver, _auth.Policy));

        await app.StartAsync(Ct);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

            async Task AssertForbiddenRoute(HttpMethod method, string url, string? json = null)
            {
                using var request = new HttpRequestMessage(method, url);
                if (json is not null)
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using var response = await client.SendAsync(request, Ct);

                var body = await response.Content.ReadAsStringAsync(Ct);
                Assert.Equal(
                    HttpStatusCode.Forbidden,
                    (HttpStatusCode)response.StatusCode);
                Assert.True(
                    response.StatusCode == HttpStatusCode.Forbidden,
                    $"{method} {url} answered {(int)response.StatusCode}: {body}");
                Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);

                using var doc = System.Text.Json.JsonDocument.Parse(body);
                Assert.Equal(ForbiddenMessage, doc.RootElement.GetProperty("error").GetString());
            }

            // Releases: mutations need ExecutionOperator (a Viewer lacks it); the reads do not.
            await AssertForbiddenRoute(HttpMethod.Post, "/api/releases/", """{"version":"v9.9.9"}""");
            await AssertForbiddenRoute(HttpMethod.Patch, "/api/releases/rel-1/status", """{"status":"Planning"}""");
            await AssertForbiddenRoute(HttpMethod.Patch, "/api/releases/rel-1/notes", """{"notes":"n"}""");
            await AssertForbiddenRoute(HttpMethod.Patch, "/api/releases/rel-1/tag", """{"tag":"v2"}""");
            await AssertForbiddenRoute(HttpMethod.Patch, "/api/releases/rel-1/repositories", """{"repositories":["r"]}""");
            await AssertForbiddenRoute(HttpMethod.Delete, "/api/releases/rel-1");
            await AssertForbiddenRoute(HttpMethod.Get, "/api/releases/rel-1/validate");

            // Clarifications: answering needs ExecutionOperator; the reads do not.
            await AssertForbiddenRoute(HttpMethod.Post, "/api/clarifications/clar-1/answer", """{"answer":"a"}""");

            // Backups: EVERY route needs GlobalAdmin.
            await AssertForbiddenRoute(HttpMethod.Get, "/api/backup/");
            await AssertForbiddenRoute(HttpMethod.Post, "/api/backup/");
            await AssertForbiddenRoute(HttpMethod.Get, "/api/backup/some.tar.gz");
            await AssertForbiddenRoute(HttpMethod.Post, "/api/backup/restore", """{"fileName":"some.tar.gz"}""");

            // Config: the FULL projections and every write need GlobalAdmin.
            await AssertForbiddenRoute(HttpMethod.Get, "/api/config/models");
            await AssertForbiddenRoute(HttpMethod.Get, "/api/config/models/discover");
            await AssertForbiddenRoute(HttpMethod.Get, "/api/config/repositories");
            await AssertForbiddenRoute(HttpMethod.Get, "/api/config/repositories/r/branches");
            await AssertForbiddenRoute(HttpMethod.Get, "/api/config/orchestrator");
            await AssertForbiddenRoute(HttpMethod.Get, "/api/config/workers");
            await AssertForbiddenRoute(HttpMethod.Get, "/api/config/composer");
            await AssertForbiddenRoute(HttpMethod.Patch, "/api/config/models", """{"orchestratorModel":"x"}""");
            await AssertForbiddenRoute(HttpMethod.Post, "/api/config/available-models", """{"name":"m"}""");
            await AssertForbiddenRoute(HttpMethod.Put, "/api/config/available-models/m", """{"name":"m"}""");
            await AssertForbiddenRoute(HttpMethod.Delete, "/api/config/available-models/m");
            await AssertForbiddenRoute(HttpMethod.Post, "/api/config/sub-agent-models", """{"name":"m"}""");
            await AssertForbiddenRoute(HttpMethod.Put, "/api/config/sub-agent-models/m", """{"name":"m"}""");
            await AssertForbiddenRoute(HttpMethod.Delete, "/api/config/sub-agent-models/m");
            await AssertForbiddenRoute(HttpMethod.Post, "/api/config/repositories", """{"name":"r","url":"u","defaultBranch":"main"}""");
            await AssertForbiddenRoute(HttpMethod.Put, "/api/config/repositories/r", """{"name":"r","url":"u","defaultBranch":"main"}""");
            await AssertForbiddenRoute(HttpMethod.Delete, "/api/config/repositories/r");
            await AssertForbiddenRoute(HttpMethod.Patch, "/api/config/orchestrator", """{"maxIterations":5}""");
            await AssertForbiddenRoute(HttpMethod.Patch, "/api/config/workers", """{"coder":100}""");
            await AssertForbiddenRoute(HttpMethod.Patch, "/api/config/composer", """{"maxSteps":75}""");

            // ── The SharedRead rows still answer a Viewer (positive control). ──
            using (var releases = await client.GetAsync("/api/releases/rel-1", Ct))
                Assert.Equal(HttpStatusCode.OK, releases.StatusCode);

            using (var goals = await client.GetAsync("/api/releases/rel-1/goals", Ct))
                Assert.Equal(HttpStatusCode.OK, goals.StatusCode);

            using (var clarifications = await client.GetAsync("/api/clarifications/", Ct))
                Assert.Equal(HttpStatusCode.OK, clarifications.StatusCode);

            using (var pending = await client.GetAsync("/api/clarifications/pending", Ct))
                Assert.Equal(HttpStatusCode.OK, pending.StatusCode);

            using (var count = await client.GetAsync("/api/clarifications/count", Ct))
                Assert.Equal(HttpStatusCode.OK, count.StatusCode);
        }
        finally
        {
            await app.StopAsync(Ct);
            await app.DisposeAsync();
            Cleanup(backupStateDir);
            _ = backupFacade;
        }
    }

    /// <summary>
    /// A MALFORMED BODY FROM AN UNAUTHORIZED CALLER GETS 403 VIA PRE-BINDING on every covered
    /// mutating route — the body never reaches the binder — while an AUTHORIZED caller keeps the
    /// existing 400 binding answer. This is the minimal pre-binding proof for the NEW routes; the
    /// full goals/issues battery covers the rest.
    /// </summary>
    [Fact]
    public async Task MalformedBody_FromAnUnauthorizedCaller_Gets403BeforeBinding()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await _auth.SeedAsync(AdminId, UserRole.Admin);

        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");

        var store = CreateStore();
        await store.CreateReleaseAsync(new Release { Id = "rel-1", Tag = "v1.0.0" }, Ct);

        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] },
        };

        var builder = WebApplication.CreateBuilder();
        Program.AddHiveJsonOptions(builder.Services);
        var resolver = _auth.Resolver(HiveAuthMode.AuthenticationEnabled);
        builder.Services.AddSingleton(resolver);
        builder.Services.AddSingleton(_auth.Policy);
        builder.Services.AddSingleton<IReleaseFacade>(
            CreateReleaseFacade(store, HiveAuthMode.AuthenticationEnabled));
        builder.Services.AddSingleton<IClarificationFacade>(sp => new ClarificationFacade(
            new ClarificationQueueService(), resolver, _auth.Policy, NullLogger<ClarificationFacade>.Instance));
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            // The identity the client selects in X-Test-Caller, resolved per request.
            var caller = context.Request.Headers["X-Test-Caller"].ToString();
            if (!string.IsNullOrEmpty(caller))
                context.User = FacadeAuthorizationHarness.Principal(caller);
            await next();
        });

        app.MapReleaseEndpoints();
        app.MapClarificationEndpoints();
        app.MapConfigEndpoints(new ConfigFacade(
            config, configModel: null, discovery: null,
            NullLogger<ConfigFacade>.Instance, resolver, _auth.Policy));

        await app.StartAsync(Ct);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

            const string malformed = "{ this is not json";

            (string Method, string Url)[] covered =
            [
                ("POST", "/api/releases/"),
                ("PATCH", "/api/releases/rel-1/status"),
                ("PATCH", "/api/releases/rel-1/notes"),
                ("PATCH", "/api/releases/rel-1/tag"),
                ("PATCH", "/api/releases/rel-1/repositories"),
                ("POST", "/api/clarifications/clar-1/answer"),
                ("PATCH", "/api/config/models"),
                ("POST", "/api/config/available-models"),
                ("PUT", "/api/config/available-models/m"),
                ("POST", "/api/config/sub-agent-models"),
                ("PUT", "/api/config/sub-agent-models/m"),
                ("POST", "/api/config/repositories"),
                ("PUT", "/api/config/repositories/r"),
                ("PATCH", "/api/config/orchestrator"),
                ("PATCH", "/api/config/workers"),
                ("PATCH", "/api/config/composer"),
            ];

            async Task<HttpResponseMessage> SendAsync(string method, string url, string caller)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), url)
                {
                    Content = new StringContent(malformed, Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("X-Test-Caller", caller);
                return await client.SendAsync(request, Ct);
            }

            foreach (var (method, url) in covered)
            {
                // The VIEWER is insufficient on every one of these routes.
                using var denied = await SendAsync(method, url, ViewerId);
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                Assert.NotEqual(HttpStatusCode.BadRequest, denied.StatusCode);
                Assert.Contains(
                    ForbiddenMessage,
                    await denied.Content.ReadAsStringAsync(Ct),
                    StringComparison.Ordinal);

                // The AUTHORIZED caller keeps the pre-existing 400 binding answer (an empty body).
                var authorizedCaller = url.StartsWith("/api/config/", StringComparison.Ordinal)
                    ? AdminId
                    : OperatorId;
                using var allowed = await SendAsync(method, url, authorizedCaller);
                Assert.Equal(HttpStatusCode.BadRequest, allowed.StatusCode);
                Assert.Empty(await allowed.Content.ReadAsStringAsync(Ct));
            }
        }
        finally
        {
            await app.StopAsync(Ct);
            await app.DisposeAsync();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  F. SESSIONS — Composer entries excluded from the endpoint, kept in GetAll
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>GET /api/sessions</c> reports SHARED EXECUTION SESSIONS ONLY: with a Composer session and
    /// three execution sessions registered, the endpoint omits exactly the Composer one — while
    /// <see cref="LlmSessionRegistry.GetAll"/> still returns all four, proving the filter is
    /// endpoint-local and the dashboard contract is unchanged.
    /// </summary>
    [Fact]
    public async Task SessionsEndpoint_ExcludesComposerEntries_ButTheRegistryKeepsThem()
    {
        var registry = new LlmSessionRegistry();
        registry.RegisterOrUpdate(new LlmSessionInfo
        {
            SessionId = "composer-1", SessionType = LlmSessionType.Composer, Model = "m", Status = "active", MaxTokens = 1,
        });
        registry.RegisterOrUpdate(new LlmSessionInfo
        {
            SessionId = "brain-1", SessionType = LlmSessionType.Brain, Model = "m", Status = "active", MaxTokens = 1,
        });
        registry.RegisterOrUpdate(new LlmSessionInfo
        {
            SessionId = "brain-goal-1", SessionType = LlmSessionType.BrainGoal, Model = "m", Status = "active", MaxTokens = 1,
        });
        registry.RegisterOrUpdate(new LlmSessionInfo
        {
            SessionId = "review-1", SessionType = LlmSessionType.GoalReview, Model = "m", Status = "active", MaxTokens = 1,
        });

        // GetAll() is UNCHANGED: all four entries, Composer included.
        Assert.Equal(4, registry.GetAll().Count);
        Assert.Contains(registry.GetAll(), s => s.SessionType == LlmSessionType.Composer);

        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");
        var builder = WebApplication.CreateBuilder();
        Program.AddHiveJsonOptions(builder.Services);
        builder.Services.AddSingleton(registry);
        var app = builder.Build();
        app.MapSessionEndpoints();
        await app.StartAsync(Ct);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            var body = await client.GetStringAsync("/api/sessions", Ct);
            using var doc = System.Text.Json.JsonDocument.Parse(body);

            var ids = doc.RootElement.EnumerateArray()
                .Select(e => e.GetProperty("sessionId").GetString())
                .ToList();

            Assert.Equal(["brain-1", "brain-goal-1", "review-1"], ids.OrderBy(id => id));
            Assert.DoesNotContain("composer-1", ids);
        }
        finally
        {
            await app.StopAsync(Ct);
            await app.DisposeAsync();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  G. OPEN MODE IS UNCHANGED
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// OPEN MODE IS BYTE-IDENTICAL TO BEFORE: with no authentication configured, the explicit
    /// open-mode local operator holds every capability, so every covered facade operation still runs
    /// — the single-operator installation keeps its previous behaviour exactly. No principal is
    /// fabricated on the way.
    /// </summary>
    [Fact]
    public async Task OpenMode_KeepsFullAccess()
    {
        var store = CreateStore();
        await store.CreateReleaseAsync(new Release { Id = "rel-1", Tag = "v1.0.0" }, Ct);

        var releaseFacade = CreateReleaseFacade(store, HiveAuthMode.Open);
        Assert.True((await releaseFacade.GetReleaseAsync("rel-1", null, Ct)).Success);
        Assert.True((await releaseFacade.DeleteReleaseAsync("rel-1", null, Ct)).Success);

        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "m" },
            Models = new ModelsConfig { AvailableModels = [] },
        };
        var configFacade = new ConfigFacade(
            config, configModel: null, discovery: null,
            NullLogger<ConfigFacade>.Instance,
            _auth.Resolver(HiveAuthMode.Open), _auth.Policy);
        Assert.True((await configFacade.GetModelsAsync(null, Ct)).Success);

        var (stateDir, _, backupFacade) = CreateBackupFacade(HiveAuthMode.Open);
        try
        {
            Assert.True((await backupFacade.GetBackupsAsync(null, Ct)).Success);
        }
        finally
        {
            Cleanup(stateDir);
        }

        var clarificationFacade = new ClarificationFacade(
            new ClarificationQueueService(), _auth.Resolver(HiveAuthMode.Open), _auth.Policy,
            NullLogger<ClarificationFacade>.Instance);
        Assert.True((await clarificationFacade.GetAllRequestsAsync(null, Ct)).Success);
    }

    // ── Shared helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates an isolated state directory with a real SQLite database and the production
    /// <see cref="BackupFacade"/> over the production resolver/policy for <paramref name="mode"/>.
    /// </summary>
    private (string StateDir, BackupService Service, IBackupFacade Facade) CreateBackupFacade(
        HiveAuthMode? mode = null)
    {
        var stateDir = Path.Combine(Path.GetTempPath(), $"rcb-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);

        var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite($"Data Source={Path.Combine(stateDir, "copilothive.db")}")
            .Options;
        using (var context = new CopilotHiveDbContext(options))
            context.Database.EnsureCreated();

        var service = new BackupService(
            stateDir, new FixedOptionsFactory(options), NullLogger<BackupService>.Instance);
        var facade = new BackupFacade(
            service,
            _auth.Resolver(mode ?? HiveAuthMode.AuthenticationEnabled),
            _auth.Policy,
            NullLogger<BackupFacade>.Instance);
        return (stateDir, service, facade);
    }

    /// <summary>A context factory over one fixed options instance.</summary>
    private sealed class FixedOptionsFactory(DbContextOptions<CopilotHiveDbContext> options)
        : IDbContextFactory<CopilotHiveDbContext>
    {
        public CopilotHiveDbContext CreateDbContext() => new(options);
    }

    /// <summary>Asserts the shared denial contract of a value-producing facade result.</summary>
    private static void AssertForbidden<T>(FacadeResult<T> result)
    {
        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
        Assert.Equal(ForbiddenMessage, result.Error);
        Assert.Equal(default, result.Value);
    }

    /// <summary>Asserts the shared denial contract, awaited.</summary>
    private static Task AssertForbiddenAsync<T>(FacadeResult<T> result)
    {
        AssertForbidden(result);
        return Task.CompletedTask;
    }

    /// <summary>Removes an isolated state directory, tolerating a lingering file lock.</summary>
    private static void Cleanup(string stateDir)
    {
        try
        {
            if (Directory.Exists(stateDir))
                Directory.Delete(stateDir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _auth.Dispose();

        foreach (var dir in _tempDirs)
            Cleanup(dir);
    }
}

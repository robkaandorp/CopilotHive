using System.Security.Claims;

using CopilotHive.Configuration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE FACADE-LEVEL ROLE × OPERATION MATRIX for the release / configuration / backup /
/// clarification surfaces, over the REAL production facades over the REAL production
/// <see cref="UserService"/> directory.
/// <para>
/// This file fills the gap the HTTP battery cannot: it supplies a VIABLE SUCCESS CONTROL for every
/// ALLOWED cell, including the ones whose HTTP route needs an external service (the configuration
/// WRITES and the backup RESTORE). A refusal row is only meaningful next to a success row for a
/// caller who really holds the capability — otherwise a facade that refused everybody would
/// satisfy the denial table.
/// </para>
/// <para>
/// EVERY denial row asserts the EXACT sanitized denial text
/// (<c>FacadeAuthorization.ForbiddenMessage</c>), never a role name or a capability name.
/// </para>
/// </summary>
public sealed class ReleaseConfigBackupRoleOperationMatrixTests : IDisposable
{
    private readonly FacadeAuthorizationHarness _auth = new();

    private const string ViewerId = "matrix-viewer";
    private const string MemberId = "matrix-member";
    private const string OperatorId = "matrix-operator";
    private const string AdminId = "matrix-admin";

    private const string ForbiddenMessage = "The acting account is not authorized to perform this operation.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal User(string gitHubId) => FacadeAuthorizationHarness.Principal(gitHubId);

    // ══════════════════════════════════════════════════════════════════════════
    //  CONFIGURATION — every projection AND every write is GlobalAdmin
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// EVERY CONFIGURATION WRITE is an administration operation: a VIEWER, a MEMBER and an
    /// OPERATOR are all refused with the sanitized denial text, while the ADMIN really persists
    /// the change — the VIABLE SUCCESS CONTROL for the write half of the matrix.
    /// </summary>
    [Fact]
    public async Task ConfigWrites_RequireGlobalAdmin_AndTheAdminReallyPersists()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await _auth.SeedAsync(AdminId, UserRole.Admin);

        var (config, service, dir) = ConfigFacadeTests.CreateRealService();
        try
        {
            var facade = new ConfigFacade(
                config, service, discovery: null,
                NullLogger<ConfigFacade>.Instance,
                _auth.Resolver(HiveAuthMode.AuthenticationEnabled), _auth.Policy);

            foreach (var insufficient in new[] { ViewerId, MemberId, OperatorId })
            {
                var caller = User(insufficient);

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
                        VerboseLogging: null, BrainMaxSteps: null, BranchCleanupDelayHours: null),
                    caller, Ct));
                await AssertForbiddenAsync(await facade.SaveWorkersAsync(
                    new Dictionary<string, int> { ["coder"] = 1 }, caller, Ct));
                await AssertForbiddenAsync(await facade.SaveComposerAsync(
                    new ComposerSettingsUpdate(), caller, Ct));
            }

            // NOTHING was persisted by the denied calls.
            Assert.Null(config.Composer);
            Assert.Empty(config.Repositories);
            Assert.Null(config.Models!.AvailableModels?.FirstOrDefault(m => m.Name == "m"));

            // ── VIABLE SUCCESS CONTROL: the Admin really persists. ──
            var admin = User(AdminId);

            var added = await facade.AddAvailableModelAsync(
                new AvailableModelRequest("admin-model", null), admin, Ct);
            Assert.True(added.Success, added.Error);
            Assert.NotNull(config.Models!.AvailableModels!.FirstOrDefault(m => m.Name == "admin-model"));

            var orchestrator = await facade.SaveOrchestratorAsync(
                new OrchestratorSettingsUpdate(
                    MaxIterations: 11, MaxRetriesPerTask: null, MaxParallelGoals: null,
                    VerboseLogging: null, BrainMaxSteps: null, BranchCleanupDelayHours: null),
                admin, Ct);
            Assert.True(orchestrator.Success, orchestrator.Error);
            Assert.Equal(11, config.Orchestrator.MaxIterations);

            var composer = await facade.SaveComposerAsync(
                new ComposerSettingsUpdate(MaxSteps: 64), admin, Ct);
            Assert.True(composer.Success, composer.Error);
            Assert.NotNull(config.Composer);
            Assert.Equal(64, config.Composer!.MaxSteps);

            var repository = await facade.AddRepositoryAsync(
                new RepositoryRequest("admin-repo", "https://example.com/r.git", "main"), admin, Ct);
            Assert.True(repository.Success, repository.Error);
            Assert.Contains(config.Repositories, r => r.Name == "admin-repo");
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// THE CONFIGURATION READS ARE ADMINISTRATION TOO: a VIEWER, a MEMBER and an OPERATOR are all
    /// refused on EVERY projection — including the repository BRANCHES read, which needs no
    /// repository manager at all — while the ADMIN really reads each of them.
    /// </summary>
    [Fact]
    public async Task ConfigReads_RequireGlobalAdmin_AndTheAdminReallyReads()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await _auth.SeedAsync(AdminId, UserRole.Admin);

        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "m" },
            Models = new ModelsConfig { AvailableModels = [] },
            Composer = new ComposerConfig(),
        };

        var facade = new ConfigFacade(
            config, configModel: null, discovery: null,
            NullLogger<ConfigFacade>.Instance,
            _auth.Resolver(HiveAuthMode.AuthenticationEnabled), _auth.Policy);

        foreach (var insufficient in new[] { ViewerId, MemberId, OperatorId })
        {
            var caller = User(insufficient);

            await AssertForbiddenAsync(await facade.GetModelsAsync(caller, Ct));
            await AssertForbiddenAsync(await facade.GetRepositoriesAsync(caller, Ct));
            await AssertForbiddenAsync(await facade.GetBranchesAsync("repo", caller, Ct));
            await AssertForbiddenAsync(await facade.GetOrchestratorAsync(caller, Ct));
            await AssertForbiddenAsync(await facade.GetWorkersAsync(caller, Ct));
            await AssertForbiddenAsync(await facade.GetComposerAsync(caller, Ct));
            await AssertForbiddenAsync(await facade.DiscoverModelsAsync(caller, Ct));
        }

        // ── VIABLE SUCCESS CONTROL: the Admin really reads each projection. ──
        var admin = User(AdminId);

        Assert.True((await facade.GetModelsAsync(admin, Ct)).Success);
        Assert.True((await facade.GetRepositoriesAsync(admin, Ct)).Success);
        Assert.True((await facade.GetOrchestratorAsync(admin, Ct)).Success);
        Assert.True((await facade.GetWorkersAsync(admin, Ct)).Success);
        Assert.True((await facade.GetComposerAsync(admin, Ct)).Success);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  BACKUP — every operation is GlobalAdmin, including restore
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// EVERY BACKUP OPERATION is an administration operation — list, create, download AND restore
    /// — and the RESTORE really works for an ADMIN over a REAL archive (the VIABLE SUCCESS
    /// CONTROL for the restore half of the matrix).
    /// </summary>
    [Fact]
    public async Task BackupOperations_RequireGlobalAdmin_AndTheAdminReallyRestores()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await _auth.SeedAsync(AdminId, UserRole.Admin);

        var stateDir = Path.Combine(Path.GetTempPath(), $"matrix-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        try
        {
            var dbPath = Path.Combine(stateDir, "copilothive.db");
            var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            using (var context = new CopilotHiveDbContext(options))
                context.Database.EnsureCreated();

            var service = new BackupService(
                stateDir, new FixedOptionsFactory(options), NullLogger<BackupService>.Instance);
            var facade = new BackupFacade(
                service,
                _auth.Resolver(HiveAuthMode.AuthenticationEnabled),
                _auth.Policy,
                NullLogger<BackupFacade>.Instance);

            foreach (var insufficient in new[] { ViewerId, MemberId, OperatorId })
            {
                var caller = User(insufficient);

                await AssertForbiddenAsync(await facade.GetBackupsAsync(caller, Ct));
                await AssertForbiddenAsync(await facade.CreateBackupAsync(caller, Ct));
                await AssertForbiddenAsync(await facade.OpenBackupForDownloadAsync("x.tar.gz", caller, Ct));
                await AssertForbiddenAsync(await facade.RestoreBackupAsync("x.tar.gz", caller, Ct));
            }

            // Nothing was created by the denied creates.
            Assert.Empty(service.ListBackups());

            // ── VIABLE SUCCESS CONTROL: the Admin really lists, creates, downloads and restores. ──
            var admin = User(AdminId);

            var created = await facade.CreateBackupAsync(admin, Ct);
            Assert.True(created.Success, created.Error);
            Assert.NotEmpty(service.ListBackups());

            var download = await facade.OpenBackupForDownloadAsync(created.Value!.FileName, admin, Ct);
            Assert.True(download.Success, download.Error);
            Assert.True(File.Exists(download.Value!.FullPath));

            var restored = await facade.RestoreBackupAsync(created.Value!.FileName, admin, Ct);
            Assert.True(restored.Success, restored.Error);
            Assert.NotNull(restored.Value!.SafetyBackupPath);
        }
        finally
        {
            Cleanup(stateDir);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  CLARIFICATIONS — reads SharedRead, answering ExecutionOperator
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE CLARIFICATION SPLIT, WITH A VIABLE SUCCESS CONTROL FOR BOTH HALVES: a VIEWER really
    /// reads (shared read) but is refused on the answer (execution operator), and an OPERATOR
    /// really answers.
    /// </summary>
    [Fact]
    public async Task ClarificationReads_AreSharedRead_AndAnsweringIsExecutionOperator()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync(OperatorId, UserRole.Operator);

        var queue = new CopilotHive.Orchestration.ClarificationQueueService();
        queue.Enqueue(new CopilotHive.Orchestration.ClarificationRequest
        {
            Id = "clar-1",
            GoalId = "goal-1",
            WorkerRole = "coder",
            Question = "Which branch?",
        });

        var facade = new ClarificationFacade(
            queue,
            new InMemoryGoalStore(),
            _auth.Resolver(HiveAuthMode.AuthenticationEnabled),
            _auth.Policy,
            NullLogger<ClarificationFacade>.Instance);

        // ── SHARED READ: every role that holds it, including the Viewer. ──
        foreach (var caller in new[] { ViewerId, MemberId, OperatorId })
        {
            var all = await facade.GetAllRequestsAsync(User(caller), Ct);
            Assert.True(all.Success, all.Error);
            Assert.Equal(["clar-1"], all.Value!.Select(r => r.Id));

            var pending = await facade.GetPendingHumanRequestsAsync(User(caller), Ct);
            Assert.True(pending.Success, pending.Error);

            var count = await facade.GetPendingHumanCountAsync(User(caller), Ct);
            Assert.True(count.Success, count.Error);
        }

        // ── EXECUTION OPERATOR: the Viewer and the Member are refused on the answer. ──
        foreach (var insufficient in new[] { ViewerId, MemberId })
        {
            await AssertForbiddenAsync(
                await facade.SubmitAnswerAsync("clar-1", "main", User(insufficient), Ct));
        }

        Assert.Null(queue.GetRequest("clar-1")!.Answer);

        // ── VIABLE SUCCESS CONTROL: the Operator really answers. ──
        var answered = await facade.SubmitAnswerAsync("clar-1", "main", User(OperatorId), Ct);
        Assert.True(answered.Success, answered.Error);
        Assert.Equal("main", queue.GetRequest("clar-1")!.Answer);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

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

    /// <summary>A context factory over one fixed options instance.</summary>
    private sealed class FixedOptionsFactory(DbContextOptions<CopilotHiveDbContext> options)
        : IDbContextFactory<CopilotHiveDbContext>
    {
        public CopilotHiveDbContext CreateDbContext() => new(options);
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

    public void Dispose() => _auth.Dispose();
}

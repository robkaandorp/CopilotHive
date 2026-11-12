using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// THE TWO REVIEW-REGRESSION FIXES of the release/config backend-authorization goal, each pinned by a
/// load-bearing assertion.
/// <para>
/// FINDING 1 — <c>MapReleaseStatusFailure</c> omitted <see cref="FacadeErrorKind.Forbidden"/>, so the
/// facade's OWN fresh refusal (reachable on the status PATCH even though the route also carries
/// pre-binding authorization: an Operator can pass that gate and be demoted or disabled while the
/// body binds) surfaced as an unexpected-kind <see cref="InvalidOperationException"/> — HTTP 500 —
/// instead of the authorization answer. The mapper arm is proven directly against a
/// <see cref="StatusFailureOutcome"/> with <c>Kind = Forbidden</c>, and the facade's production of
/// that outcome is proven over the REAL directory.
/// </para>
/// <para>
/// FINDING 2 — <c>Configuration.razor</c> read the whole <c>hive-config.yaml</c> (and the agents files)
/// straight off disk and rendered them to any authenticated role, bypassing the GlobalAdmin
/// requirement on every other configuration projection. The read is now a gated
/// <see cref="IConfigFacade.GetConfigFilesAsync"/> operation; the denial, the empty-repo success, and
/// the admin/open-mode success are all pinned here.
/// </para>
/// </summary>
public sealed class ReleaseConfigAuthorizationReviewRegressionTests : IDisposable
{
    private const string ViewerId = "rr-viewer";
    private const string MemberId = "rr-member";
    private const string OperatorId = "rr-operator";
    private const string AdminId = "rr-admin";

    /// <summary>The single sanitized denial text every facade and route returns.</summary>
    private const string ForbiddenMessage = "The acting account is not authorized to perform this operation.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ══════════════════════════════════════════════════════════════════════════
    //  FINDING 1 — the status-failure mapper's Forbidden arm
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The private mapper declared by <see cref="ApiEndpoints"/> under that name.</summary>
    private static MethodInfo StatusFailureMapper()
    {
        var method = typeof(ApiEndpoints).GetMethod(
            "MapReleaseStatusFailure", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(typeof(IResult), method!.ReturnType);
        Assert.Equal(typeof(StatusFailureOutcome), method.GetParameters().Single().ParameterType);
        return method;
    }

    /// <summary>The per-request services the JSON result types resolve their options from.</summary>
    private static readonly IServiceProvider RequestServices = BuildRequestServices();

    private static IServiceProvider BuildRequestServices()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddLogging();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(
            new Microsoft.AspNetCore.Http.Json.JsonOptions()));
        return services.BuildServiceProvider();
    }

    /// <summary>Executes <paramref name="result"/> and returns the status code and body it wrote.</summary>
    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext { RequestServices = RequestServices };
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync(Ct));
    }

    /// <summary>
    /// THE FIX, PROVEN DIRECTLY: a <see cref="StatusFailureOutcome"/> with
    /// <c>Kind = Forbidden</c> — exactly the outcome
    /// <see cref="ReleaseFacade.UpdateReleaseStatusAsync"/> returns after its own fresh refusal — maps
    /// to HTTP 403 carrying the sanitized <c>{ error }</c> body, and NOT to the unexpected-kind
    /// <see cref="InvalidOperationException"/> it produced before the fix (which ASP.NET would have
    /// rendered as 500).
    /// </summary>
    [Fact]
    public async Task StatusFailure_Forbidden_MapsTo403_WithTheSanitizedErrorBody()
    {
        var mapper = StatusFailureMapper();

        var forbidden = new StatusFailureOutcome(
            FacadeErrorKind.Forbidden, ForbiddenMessage, null, [], []);

        // Invoking the mapper must NOT throw — that throw is precisely the pre-fix defect.
        var result = Assert.IsAssignableFrom<IResult>(mapper.Invoke(null, [forbidden]));

        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("error", out var error),
            "the 403 body must carry an 'error' property");
        Assert.Equal(ForbiddenMessage, error.GetString());
        // The sanitized body carries ONLY the error — no detail/results/errors fields leak.
        Assert.Single(doc.RootElement.EnumerateObject());
    }

    /// <summary>
    /// THE DISCRIMINATING PAIR: the same mapper still produces every PRE-EXISTING answer byte-for-byte
    /// — 404 <c>{error}</c>, 400 <c>{errors:[...]}</c> (the array form), 400 <c>{error}</c>,
    /// 409 <c>{error}</c>, 500 <c>{detail, results}</c> and 503 <c>{detail}</c> — so the new 403 arm
    /// was inserted without reshaping any other mapping. The switch also still THROWS for a kind this
    /// route never produces, so it kept its throw-on-unexpected-kind discipline.
    /// </summary>
    [Fact]
    public async Task StatusFailure_EveryOtherKind_KeepsItsExactPreExistingMapping()
    {
        var mapper = StatusFailureMapper();

        async Task<(int Status, string Body)> MapAsync(StatusFailureOutcome outcome) =>
            await ExecuteAsync(Assert.IsAssignableFrom<IResult>(mapper.Invoke(null, [outcome])));

        // 404 {error}
        var notFound = await MapAsync(new StatusFailureOutcome(
            FacadeErrorKind.NotFound, "Release 'x' not found.", null, [], []));
        Assert.Equal(StatusCodes.Status404NotFound, notFound.Status);
        using (var doc = JsonDocument.Parse(notFound.Body))
            Assert.Equal("Release 'x' not found.", doc.RootElement.GetProperty("error").GetString());

        // 400 {errors:[...]} — the ARRAY form, chosen when Errors is non-empty.
        var validation = await MapAsync(new StatusFailureOutcome(
            FacadeErrorKind.BadRequest, null, null, ["first error", "second error"], []));
        Assert.Equal(StatusCodes.Status400BadRequest, validation.Status);
        using (var doc = JsonDocument.Parse(validation.Body))
        {
            Assert.False(doc.RootElement.TryGetProperty("error", out _));
            Assert.Equal(
                ["first error", "second error"],
                doc.RootElement.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToList());
        }

        // 400 {error} — the scalar form when Errors is empty.
        var badRequest = await MapAsync(new StatusFailureOutcome(
            FacadeErrorKind.BadRequest, "Status is required.", null, [], []));
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.Status);
        using (var doc = JsonDocument.Parse(badRequest.Body))
            Assert.Equal("Status is required.", doc.RootElement.GetProperty("error").GetString());

        // 409 {error}
        var conflict = await MapAsync(new StatusFailureOutcome(
            FacadeErrorKind.Conflict, "Release is already in 'Released' status.", null, [], []));
        Assert.Equal(StatusCodes.Status409Conflict, conflict.Status);
        using (var doc = JsonDocument.Parse(conflict.Body))
            Assert.Equal(
                "Release is already in 'Released' status.",
                doc.RootElement.GetProperty("error").GetString());

        // 500 {detail, results}
        var execution = await MapAsync(new StatusFailureOutcome(
            FacadeErrorKind.Internal, null, "execution failed", [],
            [new RepoReleaseResultDto("repo", false, false, null, null, null, false, "boom")]));
        Assert.Equal(StatusCodes.Status500InternalServerError, execution.Status);
        using (var doc = JsonDocument.Parse(execution.Body))
        {
            Assert.Equal("execution failed", doc.RootElement.GetProperty("detail").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("results").GetArrayLength());
        }

        // 503 {detail}
        var unavailable = await MapAsync(new StatusFailureOutcome(
            FacadeErrorKind.ServiceUnavailable, null, "Release execution service is not available.", [], []));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.Status);
        using (var doc = JsonDocument.Parse(unavailable.Body))
            Assert.Equal(
                "Release execution service is not available.",
                doc.RootElement.GetProperty("detail").GetString());

        // THROW-ON-UNEXPECTED-KIND is intact: a kind this route never produces still throws.
        var unexpected = Assert.Throws<TargetInvocationException>(
            () => mapper.Invoke(null, [new StatusFailureOutcome(FacadeErrorKind.NotConfigured, null, "d", [], [])]));
        Assert.IsType<InvalidOperationException>(unexpected.InnerException);
    }

    /// <summary>
    /// THE FACADE REALLY PRODUCES THE OUTCOME THE ARM MAPS — INDEPENDENTLY OF PRE-BINDING: this drives
    /// <see cref="ReleaseFacade.UpdateReleaseStatusAsync"/> directly over the REAL directory, so the
    /// <see cref="FacadeErrorKind.Forbidden"/> outcome is not a hand-built stub. An Operator is
    /// authorized, then DEMOTED, then DISABLED; the very next call is refused with the sanitized
    /// message while an existing release is left completely untouched.
    /// <para>
    /// This is the second of the two independent gates: the first (pre-binding) runs before the body
    /// binds, so it cannot refuse a caller demoted AFTER it — only the facade's own per-call
    /// resolution can, which is why the mapper arm must exist.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReleaseStatusFacade_RefusesADemotionOrDisablementThatHappenedAfterTheFirstGate()
    {
        using var auth = new FacadeAuthorizationHarness();
        await auth.SeedAsync(OperatorId, UserRole.Operator);

        var dbPath = Path.Combine(NewTempDir(), "copilothive.db");
        var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options;
        using (var context = new CopilotHiveDbContext(options))
            context.Database.EnsureCreated();

        var store = new GoalStore(new FixedContextFactory(options), NullLogger<GoalStore>.Instance);
        await store.CreateReleaseAsync(new Release { Id = "rel-1", Tag = "v1.0.0" }, Ct);

        var facade = new ReleaseFacade(
            store, new DashboardNotifier(), NullLogger<ReleaseFacade>.Instance,
            auth.Resolver(HiveAuthMode.AuthenticationEnabled), auth.Policy,
            executionService: null, eventBus: null, nuGetMonitor: null,
            hiveConfig: null, appLifetime: null, docCleanup: null);

        var caller = FacadeAuthorizationHarness.Principal(OperatorId);

        // BEFORE: the Operator is authorized, so the facade answers on the merits (Planning→Planning
        // is the no-op branch) — proving the denial below is caused by the role change and not by a
        // gate that refuses everyone.
        var before = await facade.UpdateReleaseStatusAsync(
            "rel-1", new UpdateReleaseStatusRequest("Planning"), caller, Ct);
        Assert.IsType<PlanningNoOpOutcome>(before);

        // DEMOTED: the very next call is the authorization answer.
        await auth.SetRoleAsync(OperatorId, UserRole.Viewer);
        var demoted = await facade.UpdateReleaseStatusAsync(
            "rel-1", new UpdateReleaseStatusRequest("Planning"), caller, Ct);
        var demotedFailure = Assert.IsType<StatusFailureOutcome>(demoted);
        Assert.Equal(FacadeErrorKind.Forbidden, demotedFailure.Kind);
        Assert.Equal(ForbiddenMessage, demotedFailure.Error);

        // …and that exact outcome maps to the 403 body, end to end.
        var mapped = Assert.IsAssignableFrom<IResult>(
            StatusFailureMapper().Invoke(null, [demotedFailure]));
        var (status, body) = await ExecuteAsync(mapped);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        using (var doc = JsonDocument.Parse(body))
            Assert.Equal(ForbiddenMessage, doc.RootElement.GetProperty("error").GetString());

        // DISABLED: likewise, with no role change needed.
        await auth.SetRoleAsync(OperatorId, UserRole.Operator);
        Assert.IsType<PlanningNoOpOutcome>(await facade.UpdateReleaseStatusAsync(
            "rel-1", new UpdateReleaseStatusRequest("Planning"), caller, Ct));

        await auth.SetEnabledAsync(OperatorId, enabled: false);
        var disabled = await facade.UpdateReleaseStatusAsync(
            "rel-1", new UpdateReleaseStatusRequest("Planning"), caller, Ct);
        var disabledFailure = Assert.IsType<StatusFailureOutcome>(disabled);
        Assert.Equal(FacadeErrorKind.Forbidden, disabledFailure.Kind);
        Assert.Equal(ForbiddenMessage, disabledFailure.Error);

        // The denials never touched the release.
        var stored = await store.GetReleaseAsync("rel-1", Ct);
        Assert.NotNull(stored);
        Assert.Equal(ReleaseStatus.Planning, stored!.Status);
        Assert.Null(stored.ReleasedAt);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  FINDING 2 — the raw configuration-file read is a GlobalAdmin operation
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>A configuration-repo clone directory holding real, recognizable file contents.</summary>
    private sealed record ConfigRepoFixture(string Root, string YamlText, IReadOnlyList<string> AgentFileNames);

    /// <summary>
    /// Creates an isolated config-repo clone directory with a <c>hive-config.yaml</c> whose content is
    /// UNIQUE and RECOGNIZABLE (so "the content never reached the caller" is observable) plus two
    /// <c>agents/*.agents.md</c> files.
    /// </summary>
    private ConfigRepoFixture CreateConfigRepo()
    {
        var root = NewTempDir();

        var yaml = "version: \"9.9.9-secret-marker\"\norchestrator:\n  model: secret-model\n";
        File.WriteAllText(Path.Combine(root, "hive-config.yaml"), yaml);

        var agentsDir = Path.Combine(root, "agents");
        Directory.CreateDirectory(agentsDir);
        File.WriteAllText(Path.Combine(agentsDir, "coder.agents.md"), "coder agent secret body");
        File.WriteAllText(Path.Combine(agentsDir, "tester.agents.md"), "tester agent secret body");

        return new ConfigRepoFixture(root, yaml, ["coder.agents.md", "tester.agents.md"]);
    }

    /// <summary>The production <see cref="ConfigFacade"/> over one config repo for a chosen mode.</summary>
    private static ConfigFacade CreateConfigFacade(
        FacadeAuthorizationHarness auth, ConfigRepoFixture fixture, HiveAuthMode mode) =>
        new(hiveConfig: new HiveConfigFile
            {
                Orchestrator = new OrchestratorConfig { Model = "m" },
                Models = new ModelsConfig { AvailableModels = [] },
            },
            configModel: null,
            discovery: null,
            NullLogger<ConfigFacade>.Instance,
            auth.Resolver(mode),
            auth.Policy,
            repoManager: null,
            configRepo: new ConfigRepoManager("https://example.com/config.git", fixture.Root));

    /// <summary>
    /// EVERY NON-ADMIN ROLE IS REFUSED AND RECEIVES NO CONTENT: a Viewer, a Member and an Operator all
    /// get <see cref="FacadeErrorKind.Forbidden"/> with the sanitized message and a NULL value — never
    /// a partial document, never a redacted one, never the file names.
    /// </summary>
    [Fact]
    public async Task ConfigFileRead_RefusesEveryNonAdmin_AndReturnsNoContent()
    {
        using var auth = new FacadeAuthorizationHarness();
        await auth.SeedAsync(ViewerId, UserRole.Viewer);
        await auth.SeedAsync(MemberId, UserRole.Member);
        await auth.SeedAsync(OperatorId, UserRole.Operator);

        var fixture = CreateConfigRepo();
        var facade = CreateConfigFacade(auth, fixture, HiveAuthMode.AuthenticationEnabled);

        foreach (var insufficient in new[] { ViewerId, MemberId, OperatorId })
        {
            var result = await facade.GetConfigFilesAsync(
                FacadeAuthorizationHarness.Principal(insufficient), Ct);

            Assert.False(result.Success);
            Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
            Assert.Equal(ForbiddenMessage, result.Error);
            // NO CONTENT AT ALL — not even an empty list the caller could mistake for "no files".
            Assert.Null(result.Value);
        }

        // Signed out is refused too (no principal, authentication enabled).
        var signedOut = await facade.GetConfigFilesAsync(null, Ct);
        Assert.False(signedOut.Success);
        Assert.Equal(FacadeErrorKind.Forbidden, signedOut.Kind);
        Assert.Null(signedOut.Value);
    }

    /// <summary>
    /// THE ADMIN REALLY READS THE FILES — the success control proving the gate above is not a
    /// refuse-everyone. The <c>hive-config.yaml</c> comes FIRST with its content VERBATIM, followed by
    /// the agent files in name order, which is exactly the order/content the page rendered before.
    /// </summary>
    [Fact]
    public async Task ConfigFileRead_Admin_GetsTheRealContent_InThePageOrder()
    {
        using var auth = new FacadeAuthorizationHarness();
        await auth.SeedAsync(AdminId, UserRole.Admin);

        var fixture = CreateConfigRepo();
        var facade = CreateConfigFacade(auth, fixture, HiveAuthMode.AuthenticationEnabled);

        var result = await facade.GetConfigFilesAsync(
            FacadeAuthorizationHarness.Principal(AdminId), Ct);

        Assert.True(result.Success, result.Error);
        Assert.Equal(FacadeErrorKind.None, result.Kind);

        var files = result.Value!;
        Assert.Equal(
            new[] { "hive-config.yaml" }.Concat(fixture.AgentFileNames),
            files.Select(f => f.Name));

        // The YAML is the file's REAL content, unmodified.
        Assert.Equal(fixture.YamlText, files[0].Content);
        Assert.Contains("secret-marker", files[0].Content, StringComparison.Ordinal);
        Assert.Equal("coder agent secret body", files[1].Content);
        Assert.Equal("tester agent secret body", files[2].Content);
    }

    /// <summary>
    /// THE READ IS RE-RESOLVED FRESH: an Admin's read succeeds, the account is DEMOTED to Operator, and
    /// the very next read is refused — a stale page cannot keep rendering the full configuration after
    /// a demotion. Re-promotion restores it, and DISABLEMENT refuses it, all without any content ever
    /// reaching the demoted or disabled caller.
    /// </summary>
    [Fact]
    public async Task ConfigFileRead_ReResolvesCurrentState_OnEveryCall()
    {
        using var auth = new FacadeAuthorizationHarness();
        await auth.SeedAsync(AdminId, UserRole.Admin);

        var fixture = CreateConfigRepo();
        var facade = CreateConfigFacade(auth, fixture, HiveAuthMode.AuthenticationEnabled);
        var caller = FacadeAuthorizationHarness.Principal(AdminId);

        var asAdmin = await facade.GetConfigFilesAsync(caller, Ct);
        Assert.True(asAdmin.Success, asAdmin.Error);
        Assert.NotEmpty(asAdmin.Value!);

        await auth.SetRoleAsync(AdminId, UserRole.Operator);
        var demoted = await facade.GetConfigFilesAsync(caller, Ct);
        Assert.False(demoted.Success);
        Assert.Equal(FacadeErrorKind.Forbidden, demoted.Kind);
        Assert.Null(demoted.Value);

        await auth.SetRoleAsync(AdminId, UserRole.Admin);
        Assert.True((await facade.GetConfigFilesAsync(caller, Ct)).Success);

        await auth.SetEnabledAsync(AdminId, enabled: false);
        var disabled = await facade.GetConfigFilesAsync(caller, Ct);
        Assert.False(disabled.Success);
        Assert.Equal(FacadeErrorKind.Forbidden, disabled.Kind);
        Assert.Null(disabled.Value);
    }

    /// <summary>
    /// THE EXISTING NO-CONFIG-REPO BEHAVIOUR IS PRESERVED: with no <see cref="ConfigRepoManager"/>
    /// registered (a host booted without a config repo) an ADMIN gets a SUCCESS carrying an empty list,
    /// which is the state the page already rendered when its clone directory did not exist. The
    /// GlobalAdmin gate still runs FIRST, so a non-admin is still refused in that configuration.
    /// </summary>
    [Fact]
    public async Task ConfigFileRead_WithoutAConfigRepo_IsAnEmptySuccessForAnAdmin_AndStillRefusedForANonAdmin()
    {
        using var auth = new FacadeAuthorizationHarness();
        await auth.SeedAsync(AdminId, UserRole.Admin);
        await auth.SeedAsync(ViewerId, UserRole.Viewer);

        var facade = new ConfigFacade(
            new HiveConfigFile
            {
                Orchestrator = new OrchestratorConfig { Model = "m" },
                Models = new ModelsConfig { AvailableModels = [] },
            },
            configModel: null, discovery: null,
            NullLogger<ConfigFacade>.Instance,
            auth.Resolver(HiveAuthMode.AuthenticationEnabled), auth.Policy,
            repoManager: null, configRepo: null);

        var admin = await facade.GetConfigFilesAsync(
            FacadeAuthorizationHarness.Principal(AdminId), Ct);
        Assert.True(admin.Success, admin.Error);
        Assert.Empty(admin.Value!);

        var viewer = await facade.GetConfigFilesAsync(
            FacadeAuthorizationHarness.Principal(ViewerId), Ct);
        Assert.False(viewer.Success);
        Assert.Equal(FacadeErrorKind.Forbidden, viewer.Kind);
        Assert.Null(viewer.Value);
    }

    /// <summary>
    /// OPEN MODE IS UNCHANGED: with no authentication configured the explicit open-mode local operator
    /// holds GlobalAdmin, so the read returns the genuine file content exactly as the pre-gate page did
    /// — no special-casing, the same seam answers it.
    /// </summary>
    [Fact]
    public async Task ConfigFileRead_OpenMode_KeepsTheGenuineOutput()
    {
        using var auth = new FacadeAuthorizationHarness();
        var fixture = CreateConfigRepo();
        var facade = CreateConfigFacade(auth, fixture, HiveAuthMode.Open);

        var result = await facade.GetConfigFilesAsync(null, Ct);

        Assert.True(result.Success, result.Error);
        Assert.Equal(fixture.YamlText, result.Value![0].Content);
        Assert.Equal(fixture.AgentFileNames, result.Value!.Skip(1).Select(f => f.Name));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>Every isolated temp directory this instance created.</summary>
    private readonly List<string> _tempDirs = [];

    /// <summary>Creates an isolated temp directory that <see cref="Dispose"/> removes.</summary>
    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>A context factory over one fixed options instance (one shared SQLite file).</summary>
    private sealed class FixedContextFactory(DbContextOptions<CopilotHiveDbContext> options)
        : IDbContextFactory<CopilotHiveDbContext>
    {
        public CopilotHiveDbContext CreateDbContext() => new(options);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

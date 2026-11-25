using System.Security.Claims;

using CopilotHive.Configuration;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE CONFIG-REPO PATH PROJECTION'S AUTHORIZATION CONTRACT:
/// <see cref="IConfigFacade.GetConfigRepositoryPathAsync"/> is the ONLY way a caller can learn whether a
/// config repo exists and where its clone lives, so it must re-resolve the caller's CURRENT stored state
/// and require <see cref="HiveCapability.GlobalAdmin"/> BEFORE the optional
/// <see cref="ConfigRepoManager"/> is consulted at all.
/// <para>
/// <c>RepositoryPath_ConfiguredAndAbsent</c> is the EXHAUSTIVE 8-caller × 2-manager matrix: the
/// configured manager's clone directory is deliberately NEVER created, so a successful exact-path
/// answer proves the operation projects the configured manager's constructor-time
/// <see cref="ConfigRepoManager.LocalPath"/> WITHOUT cloning or probing the disk. Every row uses the
/// real directory / resolver / policy, so a demotion between two calls is a genuine stored-state change.
/// </para>
/// </summary>
public sealed class ConfigFacadeRepositoryPathTests : IDisposable
{
    private const string AdminId = "cfgrepo-admin";
    private const string ViewerId = "cfgrepo-viewer";
    private const string MemberId = "cfgrepo-member";
    private const string OperatorId = "cfgrepo-operator";
    private const string DisabledId = "cfgrepo-disabled";
    private const string UnknownId = "cfgrepo-never-activated";

    private const string ConfigRepoUrl = "https://example.com/config.git";

    /// <summary>The sanitized denial text every facade returns for a refused operation.</summary>
    private const string ForbiddenMessage = "The acting account is not authorized to perform this operation.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FacadeAuthorizationHarness _auth = new();

    /// <summary>
    /// The production facade over one config-repo manager (or none) for a chosen mode. The manager is
    /// constructed with a NONEXISTENT clone directory: no clone, no git directory, no disk I/O.
    /// </summary>
    private ConfigFacade CreateFacade(string? repoRoot, HiveAuthMode mode) =>
        new(
            hiveConfig: new HiveConfigFile
            {
                Orchestrator = new OrchestratorConfig { Model = "m" },
                Models = new ModelsConfig { AvailableModels = [] },
            },
            configModel: null,
            discovery: null,
            NullLogger<ConfigFacade>.Instance,
            _auth.Resolver(mode),
            _auth.Policy,
            repoManager: null,
            configRepo: repoRoot is null ? null : new ConfigRepoManager(ConfigRepoUrl, repoRoot));

    /// <summary>A unique, GUARANTEED-ABSENT clone path — never created by any test in this class.</summary>
    private static string MissingCloneDirectory() =>
        Path.Combine(Path.GetTempPath(), $"cfgrepo-no-clone-{Guid.NewGuid():N}");

    // ══════════════════════════════════════════════════════════════════════════
    //  RepositoryPath_ConfiguredAndAbsent — EVERY caller × manager present/absent
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE EXHAUSTIVE AUTHORIZATION MATRIX. Each of the eight caller shapes (Admin, explicit open
    /// mode, Viewer, Member, Operator, disabled, unknown, anonymous) is tested against BOTH a
    /// CONFIGURED manager and NO manager at all — 16 rows. Authorized rows receive the manager's EXACT
    /// <see cref="ConfigRepoManager.LocalPath"/> or a SUCCESS carrying <c>null</c>; denied rows receive
    /// the standard Forbidden result and a <c>null</c> value in BOTH shapes, so an unauthorized caller
    /// cannot use the answer as an oracle for whether a config repo is configured.
    /// </summary>
    [Theory]
    [InlineData("admin", true)]
    [InlineData("admin", false)]
    [InlineData("open", true)]
    [InlineData("open", false)]
    [InlineData("viewer", true)]
    [InlineData("viewer", false)]
    [InlineData("member", true)]
    [InlineData("member", false)]
    [InlineData("operator", true)]
    [InlineData("operator", false)]
    [InlineData("disabled", true)]
    [InlineData("disabled", false)]
    [InlineData("unknown", true)]
    [InlineData("unknown", false)]
    [InlineData("anonymous", true)]
    [InlineData("anonymous", false)]
    public async Task RepositoryPath_ConfiguredAndAbsent(string caller, bool configured)
    {
        // Seed ALL real directory shapes. The caller chooses which identity to present; the directory
        // is otherwise identical for every row so the only variable is the caller's CURRENT state.
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await _auth.SeedAsync(DisabledId, UserRole.Admin, enabled: false);

        var repoRoot = configured ? MissingCloneDirectory() : null;
        var mode = caller == "open" ? HiveAuthMode.Open : HiveAuthMode.AuthenticationEnabled;
        var facade = CreateFacade(repoRoot, mode);

        // PREMISE: the configured manager's clone directory genuinely does not exist, so a success
        // proves pure projection rather than a clone/disk dependency.
        if (configured) Assert.False(Directory.Exists(repoRoot!));

        var actingUser = ResolveActingUser(caller);
        var authorized = caller is "admin" or "open";

        var result = await facade.GetConfigRepositoryPathAsync(actingUser, Ct);

        if (authorized)
        {
            Assert.True(result.Success);
            Assert.Equal(FacadeErrorKind.None, result.Kind);
            Assert.Null(result.Error);
            Assert.Equal(
                configured ? new ConfigRepoManager(ConfigRepoUrl, repoRoot!).LocalPath : null,
                result.Value);
        }
        else
        {
            Assert.False(result.Success);
            Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
            Assert.Equal(ForbiddenMessage, result.Error);
            Assert.Null(result.Value);
        }
    }

    /// <summary>The real identity (or absence thereof) for one named matrix caller.</summary>
    private static ClaimsPrincipal? ResolveActingUser(string caller) => caller switch
    {
        "admin" => FacadeAuthorizationHarness.Principal(AdminId),
        // Explicit open mode registers NO cascade: a real open-mode page forwards NO identity.
        "open" => OpenModeActingUser.User,
        "viewer" => FacadeAuthorizationHarness.Principal(ViewerId),
        "member" => FacadeAuthorizationHarness.Principal(MemberId),
        "operator" => FacadeAuthorizationHarness.Principal(OperatorId),
        "disabled" => FacadeAuthorizationHarness.Principal(DisabledId),
        "unknown" => FacadeAuthorizationHarness.Principal(UnknownId),
        "anonymous" => FacadeAuthorizationHarness.Anonymous(),
        _ => throw new InvalidOperationException($"Unknown caller shape '{caller}'."),
    };

    /// <summary>
    /// THE SAME PRINCIPAL IS RECHECKED ON THE NEXT CALL: the exact same
    /// <see cref="ClaimsPrincipal"/> instance succeeds first, then the real directory DEMOTES or
    /// DISABLES that account; the second call must answer the standard refusal with no value. A facade
    /// that cached a resolved principal or the previously-returned path fails both rows.
    /// </summary>
    [Theory]
    [InlineData("demote")]
    [InlineData("disable")]
    public async Task RepositoryPath_RechecksDirectory(string transition)
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        var repoRoot = MissingCloneDirectory();
        var facade = CreateFacade(repoRoot, HiveAuthMode.AuthenticationEnabled);
        var principal = FacadeAuthorizationHarness.Principal(AdminId);

        // PREMISE: the first call really succeeded and really produced the path (no clone exists).
        var authorized = await facade.GetConfigRepositoryPathAsync(principal, Ct);
        Assert.True(authorized.Success);
        Assert.Equal(new ConfigRepoManager(ConfigRepoUrl, repoRoot).LocalPath, authorized.Value);
        Assert.False(Directory.Exists(repoRoot));

        if (transition == "demote")
            await _auth.SetRoleAsync(AdminId, UserRole.Viewer);
        else
            await _auth.SetEnabledAsync(AdminId, enabled: false);

        // SAME principal object, SAME facade instance, SAME configured manager: only stored state changed.
        var denied = await facade.GetConfigRepositoryPathAsync(principal, Ct);

        Assert.False(denied.Success);
        Assert.Equal(FacadeErrorKind.Forbidden, denied.Kind);
        Assert.Equal(ForbiddenMessage, denied.Error);
        Assert.Null(denied.Value);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Cancellation and the no-bypass surface
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// CALLER CANCELLATION PROPAGATES: a pre-cancelled token raises
    /// <see cref="OperationCanceledException"/> out of the operation — never a
    /// <see cref="FacadeErrorKind.Forbidden"/> result, which would misreport a cancellation as an
    /// authorization decision. Both an authorized and a denied caller see cancellation, so the gate
    /// is not skipped by a shortcut on either branch.
    /// </summary>
    [Theory]
    [InlineData("admin")]
    [InlineData("viewer")]
    public async Task RepositoryPath_Cancellation_Propagates(string caller)
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        var facade = CreateFacade(MissingCloneDirectory(), HiveAuthMode.AuthenticationEnabled);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            facade.GetConfigRepositoryPathAsync(ResolveActingUser(caller), cts.Token));
    }

    /// <summary>
    /// THE NO-BYPASS SURFACE CONTRACT: the operation declares EXACTLY ONE overload, and both the acting
    /// identity and the cancellation token are REQUIRED — so an identity-free overload (or a defaulted
    /// identity) can never appear and hand out the host path unauthenticated.
    /// </summary>
    [Fact]
    public void GetConfigRepositoryPathAsync_RequiresAnIdentityAndACancellationToken_WithNoBypassOverload()
    {
        var methods = typeof(IConfigFacade).GetMethods()
            .Where(m => m.Name == nameof(IConfigFacade.GetConfigRepositoryPathAsync))
            .ToArray();

        var method = Assert.Single(methods);
        var parameters = method.GetParameters();

        Assert.Equal(
            [typeof(ClaimsPrincipal), typeof(CancellationToken)],
            parameters.Select(p => p.ParameterType).ToArray());
        Assert.All(parameters, p => Assert.False(p.IsOptional, $"{p.Name} must be required."));
        Assert.Equal(typeof(Task<FacadeResult<string>>), method.ReturnType);
    }

    public void Dispose() => _auth.Dispose();
}

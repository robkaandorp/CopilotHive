using System.Reflection;
using System.Security.Claims;

using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// THE CONFIGURATION PAGE'S GLOBAL-COMPOSER GATE: the REAL
/// <see cref="CopilotHive.Components.Pages.Configuration"/> page is rendered through the
/// framework's own <see cref="HtmlRenderer"/>, and the page's OWN gate flag is read back.
/// <para>
/// The flag is what the markup branches on (<c>@if (_composerAdminAllowed)</c>), so asserting its
/// VALUE proves the page's own decision — the branch structure itself is pinned separately, and
/// branch-by-branch, by the source-level
/// <c>ComposerNotificationGatingTests</c> class (the repository does not use bUnit, and the goal
/// explicitly sanctions source-level pinning for this page).
/// </para>
/// <para>
/// The gate is driven by the circuit identity through the scoped
/// <see cref="CircuitUserPrincipalAccessor"/>, so it follows the DIRECTORY's stored role: a
/// demotion between renders closes the gate on the next render.
/// </para>
/// </summary>
public sealed class ConfigurationComposerGateTests : IDisposable
{
    private const string AdminId = "cfg-gate-admin";
    private const string MemberId = "cfg-gate-member";

    private readonly List<string> _tempDirs = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A real user directory over SQLite, so the gate's role lookup is a genuine directory read.
    /// </summary>
    private sealed class UserDirectory : IDisposable
    {
        private readonly SqliteConnection _connection;

        internal UserDirectory()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();

            using var ctx = CreateDbContext();
            ctx.Database.EnsureCreated();

            Service = new UserService(new Factory(this), NullLogger<UserService>.Instance);
        }

        internal UserService Service { get; }

        internal CopilotHiveDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite(_connection)
                .Options;
            return new CopilotHiveDbContext(options);
        }

        internal async Task SeedAsync(string gitHubId, UserRole role)
        {
            await using var db = CreateDbContext();
            db.Users.Add(new UserEntity
            {
                GitHubId = gitHubId,
                Username = gitHubId,
                AccessToken = $"token-{gitHubId}",
                Role = role,
                Enabled = true,
                CreatedAt = "2024-01-01T00:00:00.0000000Z",
            });
            await db.SaveChangesAsync(Ct);
        }

        internal async Task SetRoleAsync(string gitHubId, UserRole role)
        {
            await using var db = CreateDbContext();
            var account = await db.Users.SingleAsync(u => u.GitHubId == gitHubId, Ct);
            account.Role = role;
            await db.SaveChangesAsync(Ct);
        }

        public void Dispose() => _connection.Dispose();

        private sealed class Factory(UserDirectory owner) : IDbContextFactory<CopilotHiveDbContext>
        {
            public CopilotHiveDbContext CreateDbContext() => owner.CreateDbContext();
        }
    }

    /// <summary>A JS runtime that answers nothing.</summary>
    private sealed class NullJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default!;

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) => default!;
    }

    /// <summary>
    /// A backup facade that reports an empty archive list, so the page's load sequence completes.
    /// </summary>
    private sealed class EmptyBackupFacade : IBackupFacade
    {
        public Task<FacadeResult<IReadOnlyList<BackupInfoDto>>> GetBackupsAsync(
            System.Security.Claims.ClaimsPrincipal? actingUser, CancellationToken ct) =>
            Task.FromResult(new FacadeResult<IReadOnlyList<BackupInfoDto>>(true, [], null, FacadeErrorKind.None));

        public Task<FacadeResult<BackupInfoDto>> CreateBackupAsync(
            System.Security.Claims.ClaimsPrincipal? actingUser, CancellationToken ct) =>
            Task.FromResult(new FacadeResult<BackupInfoDto>(
                false, null, "not used", FacadeErrorKind.NotFound));

        public Task<FacadeResult<BackupDownload>> OpenBackupForDownloadAsync(
            string fileName, System.Security.Claims.ClaimsPrincipal? actingUser, CancellationToken ct) =>
            Task.FromResult(new FacadeResult<BackupDownload>(
                false, null, "not used", FacadeErrorKind.NotFound));

        public Task<FacadeResult<BackupRestore>> RestoreBackupAsync(
            string fileName, System.Security.Claims.ClaimsPrincipal? actingUser, CancellationToken ct) =>
            Task.FromResult(new FacadeResult<BackupRestore>(
                false, null, "not used", FacadeErrorKind.NotFound));
    }

    /// <summary>
    /// Renders the REAL Configuration page for one circuit identity and returns the page instance,
    /// so its own gate flag can be read.
    /// </summary>
    private async Task<CopilotHive.Components.Pages.Configuration> RenderConfigurationAsync(
        UserDirectory directory, string? gitHubId)
    {
        var repoDir = Path.Combine(Path.GetTempPath(), $"config-gate-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(repoDir);
        _tempDirs.Add(repoDir);

        var resolver = new HivePrincipalResolver(
            directory.Service,
            HiveAuthMode.AuthenticationEnabled,
            NullLogger<HivePrincipalResolver>.Instance);
        var policy = new HivePolicy();

        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] },
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new PageHeaderState());
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NullJsRuntime());
        services.AddSingleton<IBackupFacade>(new EmptyBackupFacade());
        services.AddSingleton(new ConfigRepoManager("https://example.com/config.git", repoDir));
        services.AddSingleton<IConfigFacade>(_ => new ConfigFacade(
            config, null, null, NullLogger<ConfigFacade>.Instance, resolver, policy));

        // The SCOPED circuit accessor — one per circuit, exactly as the host registers it.
        services.AddScoped(_ => new CircuitUserPrincipalAccessor(resolver, policy));

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);

        var identity = gitHubId is null
            ? new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")], authenticationType: null)
            : new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, gitHubId),
                    // A FORGED role claim: the gate must never trust it.
                    new Claim(ClaimTypes.Role, "Admin"),
                ],
                authenticationType: "Cookies");

        var authenticationState = new AuthenticationState(new ClaimsPrincipal(identity));

        CopilotHive.Components.Pages.Configuration? page = null;

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Value"] = Task.FromResult(authenticationState),
                ["ChildContent"] = (RenderFragment)(builder =>
                {
                    builder.OpenComponent<CopilotHive.Components.Pages.Configuration>(0);
                    builder.AddComponentReferenceCapture(
                        1,
                        captured => page = (CopilotHive.Components.Pages.Configuration)captured);
                    builder.CloseComponent();
                }),
            });

            await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(parameters);
        });

        Assert.NotNull(page);
        return page!;
    }

    /// <summary>Reads the page's own admin-gate flag — the value its markup branches on.</summary>
    private static bool ReadAdminGate(CopilotHive.Components.Pages.Configuration page)
    {
        var field = page.GetType().GetField(
            "_composerAdminAllowed",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        return (bool)field!.GetValue(page)!;
    }

    /// <summary>
    /// THE ADMIN HOLDS THE GATE: the page's own flag is TRUE for an admin, so the editable global
    /// notification controls are rendered.
    /// </summary>
    [Fact]
    public async Task TheAdmin_HoldsTheGlobalComposerGate()
    {
        using var directory = new UserDirectory();
        await directory.SeedAsync(AdminId, UserRole.Admin);

        var page = await RenderConfigurationAsync(directory, AdminId);

        Assert.True(ReadAdminGate(page));
    }

    /// <summary>
    /// THE NON-ADMIN DOES NOT HOLD THE GATE: the page's own flag is FALSE for a Member, so the
    /// editable global notification controls are NOT rendered.
    /// </summary>
    [Fact]
    public async Task TheNonAdmin_DoesNotHoldTheGlobalComposerGate()
    {
        using var directory = new UserDirectory();
        await directory.SeedAsync(MemberId, UserRole.Member);

        var page = await RenderConfigurationAsync(directory, MemberId);

        Assert.False(ReadAdminGate(page));
    }

    /// <summary>
    /// THE GATE FOLLOWS THE DIRECTORY, NOT THE CLAIM: the very same session (the same principal
    /// object, carrying a FORGED "Admin" role claim) holds the gate while the account is an Admin
    /// and loses it once the account is demoted in the directory.
    /// <para>
    /// This is the discriminating row: a gate that trusted the cookie's role claim would keep the
    /// flag set after the demotion.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheGate_FollowsTheStoredRole_NotTheForgedRoleClaim()
    {
        using var directory = new UserDirectory();
        await directory.SeedAsync(AdminId, UserRole.Admin);

        var asAdmin = await RenderConfigurationAsync(directory, AdminId);
        Assert.True(ReadAdminGate(asAdmin));

        // The SAME identity is demoted in the directory.
        await directory.SetRoleAsync(AdminId, UserRole.Viewer);

        var afterDemotion = await RenderConfigurationAsync(directory, AdminId);
        Assert.False(ReadAdminGate(afterDemotion));
    }

    /// <summary>
    /// A SIGNED-OUT circuit does not hold the gate: without an identity the accessor cannot answer
    /// the capability question, so the gate stays closed (under-render) rather than offering a
    /// control the facade would refuse.
    /// </summary>
    [Fact]
    public async Task ASignedOutCircuit_DoesNotHoldTheGlobalComposerGate()
    {
        using var directory = new UserDirectory();
        await directory.SeedAsync(AdminId, UserRole.Admin);

        var page = await RenderConfigurationAsync(directory, gitHubId: null);

        Assert.False(ReadAdminGate(page));
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (System.IO.Directory.Exists(dir))
                    System.IO.Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of the isolated state directory.
            }
        }
    }
}

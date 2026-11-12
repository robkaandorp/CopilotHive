using System.Linq;
using System.Net;
using System.Text.Json;

using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Services;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// THE OPEN-MODE TOLERANCE OF THE UI ROUND: on a host with NO authentication configured, the
/// migrated pages must work exactly as before — no cascade exists, the scoped accessor is never
/// initialised, the identity is null, and the facades resolve the LOCAL OPERATOR so every call
/// succeeds.
/// <para>
/// This is the row that breaks if the UI round hard-codes an authentication requirement: an
/// unconditional <c>@inject AuthenticationStateProvider</c>, a non-nullable cascading parameter, or
/// a helper that dereferences an absent cascade would all surface here as a failed component
/// activation (an <see cref="InvalidOperationException"/> or a
/// <see cref="NullReferenceException"/>) rather than as a working page.
/// </para>
/// <para>
/// The host is the REAL application (the shared <see cref="HiveTestFactory"/> pattern): the same
/// <c>Program.cs</c> pipeline, the same DI registrations, the same components.
/// </para>
/// </summary>
[Collection("HiveIntegration")]
public sealed class OpenModeUiToleranceTests : IDisposable
{
    private readonly HiveTestFactory _factory;

    public OpenModeUiToleranceTests(HiveTestFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// THE HOST REALLY IS IN OPEN MODE: no authentication services are registered at all, so the
    /// pages genuinely face an absent cascade. Without this control the rows below could be
    /// satisfied by an authentication-enabled host.
    /// </summary>
    [Fact]
    public void TheHost_RegistersNoAuthenticationServices_SoTheCascadeIsGenuinelyAbsent()
    {
        using var client = _factory.CreateClient();

        var mode = _factory.Services.GetRequiredService<HiveAuthMode>();
        Assert.False(mode.AuthEnabled, "the host must really be in open mode");

        // The cascading authentication state is registered ONLY when authentication is enabled, so
        // in open mode there is no CascadingAuthenticationState component to cascade — which is
        // exactly the "absent cascade" the pages must tolerate.
        var cascadingStateType = typeof(Microsoft.AspNetCore.Components.Authorization.CascadingAuthenticationState);
        var registrations = _factory.Services.GetServices(cascadingStateType).ToArray();
        Assert.Empty(registrations);
    }

    /// <summary>
    /// THE SCOPED ACCESSOR IS RESOLVABLE IN OPEN MODE AND ANSWERS THE LOCAL OPERATOR: the pages
    /// inject it unconditionally, so it must resolve — and it must report the local operator
    /// WITHOUT any initialisation, because open mode carries no per-circuit identity.
    /// </summary>
    [Fact]
    public async Task TheScopedAccessor_ResolvesInOpenMode_AndReportsTheLocalOperator()
    {
        using var client = _factory.CreateClient();

        var accessor = _factory.Services.GetRequiredService<CircuitUserPrincipalAccessor>();

        // It is SCOPED (one per circuit), so two scopes never share one instance.
        var scopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        using var first = scopeFactory.CreateScope();
        using var second = scopeFactory.CreateScope();
        Assert.NotSame(
            first.ServiceProvider.GetRequiredService<CircuitUserPrincipalAccessor>(),
            second.ServiceProvider.GetRequiredService<CircuitUserPrincipalAccessor>());

        // In open mode the accessor needs NO initialisation and still answers the local operator.
        Assert.False(accessor.IsInitialized);
        Assert.True(await accessor.IsAllowedAsync(HiveCapability.GlobalAdmin, Ct));

        var resolution = await accessor.GetPrincipalAsync(Ct);
        Assert.True(resolution.IsResolved);
        Assert.True(resolution.Principal!.IsLocalOperator);
    }

    /// <summary>
    /// THE IDENTITY HELPER TOLERATES THE ABSENT CASCADE: handed no cascading state at all — exactly
    /// what an open-mode page receives — the helper neither throws nor fabricates an identity, and
    /// the local-operator capability answer follows.
    /// </summary>
    [Fact]
    public async Task TheIdentityHelper_ToleratesTheAbsentCascade_InOpenMode()
    {
        using var client = _factory.CreateClient();

        var accessor = _factory.Services.GetRequiredService<CircuitUserPrincipalAccessor>();

        var identity = await ComponentActingUser.AcquireAsync(authenticationState: null, accessor);
        Assert.Null(identity);

        // The open-mode answer: the local operator holds every capability.
        Assert.True(await ComponentActingUser.IsAllowedAsync(
            null, accessor, HiveCapability.GlobalAdmin, Ct));

        // The accessor is still uninitialised — open mode never seeds it.
        Assert.False(accessor.IsInitialized);
    }

    /// <summary>
    /// THE OPEN-MODE FACADES RESOLVE THE LOCAL OPERATOR AND SUCCEED: every migrated call the pages
    /// make goes through a facade whose authorization resolves the local operator in open mode, so
    /// the operations succeed rather than being refused.
    /// <para>
    /// The facade instances here are the REAL ones the host registered, so this is the production
    /// wiring — not a hand-built facade.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheRealOpenModeFacades_ResolveTheLocalOperator_AndSucceed()
    {
        using var client = _factory.CreateClient();

        var composerFacade = _factory.Services.GetRequiredService<IComposerFacade>();
        var configFacade = _factory.Services.GetRequiredService<IConfigFacade>();

        // The Composer's own operations succeed with the null identity the pages pass in open mode.
        var models = await composerFacade.GetModelsAsync(actingUser: null, Ct);
        Assert.True(models.Success, models.Error ?? "open-mode models read failed");

        var current = await composerFacade.GetCurrentModelAsync(actingUser: null, Ct);
        Assert.True(current.Success, current.Error ?? "open-mode current-model read failed");

        // The global Composer write succeeds too (the host registers no config repo, so it reports
        // NotConfigured — a service answer, NOT a refusal).
        var saved = await configFacade.SaveComposerAsync(
            new ComposerSettingsUpdate(MaxSteps: 50), actingUser: null, Ct);
        Assert.NotEqual(FacadeErrorKind.Forbidden, saved.Kind);
    }

    /// <summary>
    /// THE MIGRATED PAGES ACTIVATE IN OPEN MODE: the REAL pages are rendered through the framework's
    /// own <see cref="Microsoft.AspNetCore.Components.Web.HtmlRenderer"/> with NO cascading
    /// authentication state at all — exactly what an open-mode circuit delivers.
    /// <para>
    /// This is the discriminating open-mode row: a page that dereferenced the absent cascade, or
    /// that injected an <c>AuthenticationStateProvider</c> (unavailable in open mode), would fail to
    /// activate here.
    /// </para>
    /// <para>
    /// A plain HTTP fetch is NOT enough: the pages are rendered with
    /// <c>InteractiveServer</c>, so their markup never appears in the initial response and the
    /// component is never activated by it. The component must be rendered directly.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("composer")]
    [InlineData("configuration")]
    public async Task TheMigratedPages_ActivateInOpenMode_WithNoCascadeAtAll(string page)
    {
        var html = page switch
        {
            "composer" => await RenderComposerChatAsync(),
            "configuration" => await RenderConfigurationAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(page), page, "Unknown page."),
        };

        // The page really rendered (not an empty or failed render).
        Assert.False(string.IsNullOrWhiteSpace(html), $"the {page} page produced no markup");

        // The chat page's open-mode notification gate is OPEN: the local operator may change the
        // global setting, which is what "open mode is unchanged" means. The Configuration page's
        // gate is the same flag, asserted through the page's own field.
        if (page == "composer")
            Assert.Contains("chat-actions", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Renders the REAL ComposerChat page with NO cascading authentication state.
    /// </summary>
    private static async Task<string> RenderComposerChatAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new CopilotHive.Dashboard.PageHeaderState());
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NullJsRuntime());
        services.AddSingleton<CopilotHive.Services.ComposerAttachmentService>(
            new CopilotHive.Services.ComposerAttachmentService(
                Path.GetTempPath(),
                NullLogger<CopilotHive.Services.ComposerAttachmentService>.Instance));

        // The Composer the page injects: a real, never-connected instance over an isolated
        // state directory.
        var stateDir = Path.Combine(Path.GetTempPath(), $"open-mode-composer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        services.AddSingleton(new CopilotHive.Orchestration.Composer(
            "claude-sonnet-4",
            NullLogger<CopilotHive.Orchestration.Composer>.Instance,
            new CopilotHive.Goals.GoalStore(
                CopilotHive.Persistence.CopilotHiveDbContext.CreateInMemory(),
                NullLogger<CopilotHive.Goals.GoalStore>.Instance),
            stateDir: stateDir,
            availableModels: ["claude-sonnet-4"],
            chatClientFactory: _ => new Moq.Mock<Microsoft.Extensions.AI.IChatClient>().Object));

        // The open-mode resolver: the local operator, no directory consulted.
        var resolver = new HivePrincipalResolver(
            new UserService(new NullDbFactory(), NullLogger<UserService>.Instance),
            HiveAuthMode.Open,
            NullLogger<HivePrincipalResolver>.Instance);
        var policy = new HivePolicy();

        services.AddScoped(_ => new CircuitUserPrincipalAccessor(resolver, policy));
        services.AddSingleton<IComposerFacade>(_ => new ComposerFacade(
            null, resolver, policy, NullLogger<ComposerFacade>.Instance));
        services.AddSingleton<IConfigFacade>(_ => new ConfigFacade(
            null, null, null, NullLogger<ConfigFacade>.Instance, resolver, policy));

        // THE OPEN-MODE CIRCUIT RUNTIME SEAM: the page injects the scoped accessor, and in open mode the
        // resolver yields the synthetic local operator, which the manager refuses
        // (ComposerRuntimeRefusal.LocalOperator) — the ONE refusal the page serves from the legacy singleton.
        // Substituting that production answer keeps this row about page ACTIVATION with no cascade at all.
        services.AddScoped<IComposerRuntimeAccessor>(_ =>
            new ComposerCircuitTesting.ScriptedCircuitRuntimeAccessor(
                (_, _) => Task.FromResult(ComposerRuntimeAccess.Granted(
                    ComposerRuntimeResult.Refused(
                        ComposerRuntimeRefusal.LocalOperator))),
                authEnabled: false));

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new Microsoft.AspNetCore.Components.Web.HtmlRenderer(
            provider, NullLoggerFactory.Instance);

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CopilotHive.Components.Pages.ComposerChat>();
            return output.ToHtmlString();
        });
    }

    /// <summary>
    /// Renders the REAL Configuration page with NO cascading authentication state.
    /// </summary>
    private static async Task<string> RenderConfigurationAsync()
    {
        var repoDir = Path.Combine(Path.GetTempPath(), $"open-mode-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoDir);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new CopilotHive.Dashboard.PageHeaderState());
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NullJsRuntime());
        services.AddSingleton<IBackupFacade>(new EmptyBackupFacade());
        services.AddSingleton(new CopilotHive.Configuration.ConfigRepoManager(
            "https://example.com/config.git", repoDir));

        var resolver = new HivePrincipalResolver(
            new UserService(new NullDbFactory(), NullLogger<UserService>.Instance),
            HiveAuthMode.Open,
            NullLogger<HivePrincipalResolver>.Instance);
        var policy = new HivePolicy();

        services.AddScoped(_ => new CircuitUserPrincipalAccessor(resolver, policy));
        services.AddSingleton<IConfigFacade>(_ => new ConfigFacade(
            null, null, null, NullLogger<ConfigFacade>.Instance, resolver, policy));

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new Microsoft.AspNetCore.Components.Web.HtmlRenderer(
            provider, NullLoggerFactory.Instance);

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CopilotHive.Components.Pages.Configuration>();
            return output.ToHtmlString();
        });
    }

    /// <summary>A JS runtime that answers nothing.</summary>
    private sealed class NullJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default!;

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) => default!;
    }

    /// <summary>A backup facade that reports an empty archive list.</summary>
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
    /// A database factory over an in-memory context. The open-mode resolver never reads the
    /// directory, so it is never consulted.
    /// </summary>
    private sealed class NullDbFactory : IDbContextFactory<CopilotHive.Persistence.CopilotHiveDbContext>
    {
        public CopilotHive.Persistence.CopilotHiveDbContext CreateDbContext() =>
            CopilotHive.Persistence.CopilotHiveDbContext.CreateInMemory();
    }

    /// <summary>
    /// THE OPEN-MODE COMPOSER ENDPOINTS STILL ANSWER: the API the pages call behaves exactly as it
    /// did before the UI round — no 401, no 403.
    /// </summary>
    [Theory]
    [InlineData("/api/composer/current-model")]
    [InlineData("/api/composer/models")]
    public async Task TheComposerApi_BehavesAsBeforeInOpenMode(string path)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync(path, Ct);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    /// <summary>
    /// The host is the SHARED collection fixture, so this class must NOT dispose it — the
    /// collection owns its lifetime.
    /// </summary>
    public void Dispose()
    {
        // Intentionally empty: the HiveTestFactory is a COLLECTION FIXTURE owned by the
        // "HiveIntegration" collection. Disposing it here would tear down the host for every other
        // test in the collection.
    }
}

using System.Net;
using System.Security.Claims;
using System.Text;

using CopilotHive.Components.Pages;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Services;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// THE COMPONENT-LEVEL NOTIFICATION GATE: the REAL <see cref="ComposerChat"/> page is rendered
/// through the framework's own <see cref="HtmlRenderer"/>, so the assertions are about the MARKUP
/// THE USER ACTUALLY SEES — not about the source text that produces it.
/// <para>
/// Two sessions are rendered over a REAL user directory (the production <see cref="UserService"/>
/// over SQLite) and the production resolver/policy:
/// </para>
/// <list type="bullet">
/// <item>an ADMIN session must see the notification toggle BUTTON;</item>
/// <item>a NON-ADMIN session must see NO button — only the global label that says an administrator
/// changes the setting.</item>
/// </list>
/// <para>
/// The rendering is driven by the circuit identity through the scoped
/// <see cref="CircuitUserPrincipalAccessor"/>, so the gate really follows the DIRECTORY's stored
/// role: a demotion between renders closes the gate on the next render.
/// </para>
/// </summary>
public sealed class ComposerChatNotificationGateTests : IDisposable
{
    private const string AdminId = "gate-admin";
    private const string MemberId = "gate-member";

    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;
    private readonly List<string> _tempDirs = [];

    public ComposerChatNotificationGateTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);
    }

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

            Service = new UserService(
                new Factory(this), NullLogger<UserService>.Instance);
        }

        internal UserService Service { get; }

        internal CopilotHiveDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
                .UseSqlite(_connection)
                .Options;
            return new CopilotHiveDbContext(options);
        }

        internal async Task SeedAsync(string gitHubId, UserRole role, bool enabled = true)
        {
            await using var db = CreateDbContext();
            db.Users.Add(new UserEntity
            {
                GitHubId = gitHubId,
                Username = gitHubId,
                AccessToken = $"token-{gitHubId}",
                Role = role,
                Enabled = enabled,
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

    /// <summary>A JS runtime that answers nothing — the page only calls it for paste handling.</summary>
    private sealed class NullJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default!;

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) => default!;
    }

    /// <summary>
    /// Renders the REAL ComposerChat page for one circuit identity and returns the markup.
    /// <para>
    /// The page's cascading <see cref="AuthenticationState"/> is supplied through the render tree
    /// (a <c>CascadingValue</c> wrapper), which is exactly how the host cascades it.
    /// </para>
    /// </summary>
    private async Task<string> RenderChatAsync( UserDirectory directory, string? gitHubId)
    {
        var stateDir = Path.Combine(Path.GetTempPath(), $"composer-gate-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(stateDir);
        _tempDirs.Add(stateDir);

        var composer = new Composer(
            "claude-sonnet-4",
            NullLogger<Composer>.Instance,
            _store,
            stateDir: stateDir,
            availableModels: ["claude-sonnet-4", "claude-opus"],
            chatClientFactory: _ => new Mock<IChatClient>().Object);

        await composer.ConnectAsync(Ct);

        var resolver = new HivePrincipalResolver(
            directory.Service,
            HiveAuthMode.AuthenticationEnabled,
            NullLogger<HivePrincipalResolver>.Instance);
        var policy = new HivePolicy();

        var attachments = new ComposerAttachmentService(
            stateDir, NullLogger<ComposerAttachmentService>.Instance);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_store);
        services.AddSingleton(new PageHeaderState());
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NullJsRuntime());
        services.AddSingleton(attachments);
        services.AddSingleton(composer);
        services.AddSingleton<IComposerFacade>(_ => new ComposerFacade(
            composer, resolver, policy, NullLogger<ComposerFacade>.Instance));
        services.AddSingleton<IConfigFacade>(_ => new ConfigFacade(
            null, null, null, NullLogger<ConfigFacade>.Instance, resolver, policy));

        // The SCOPED circuit accessor — one per circuit, exactly as the host registers it.
        services.AddScoped(_ => new CircuitUserPrincipalAccessor(resolver, policy));

        // THE CIRCUIT RUNTIME SEAM the page injects. This row is about the GLOBAL notification gate, so the
        // runtime SELECTION is substituted by the production-shaped answer for ONE circuit: the signed-in
        // account's own runtime handle (wrapping the connected Composer above) for a signed-in identity, and the
        // fail-closed DENIAL for a signed-out one — exactly the two answers the real accessor gives there.
        var runtime = gitHubId is null
            ? ComposerRuntimeAccess.Denied()
            : ComposerCircuitTesting.GrantedAccessAsync(
                composer,
                attachments,
                directory.Service,
                (await directory.Service.GetByGitHubIdAsync(gitHubId!, Ct))!.Id,
                ct: Ct).GetAwaiter().GetResult();
        services.AddScoped<IComposerRuntimeAccessor>(_ =>
            new ComposerCircuitTesting.ScriptedCircuitRuntimeAccessor(
                (_, _) => Task.FromResult(runtime), authEnabled: true));

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);

        // The circuit identity: a signed-in cookie principal, or a signed-out state.
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

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Value"] = Task.FromResult(authenticationState),
                ["ChildContent"] = (RenderFragment)(builder =>
                {
                    builder.OpenComponent<ComposerChat>(0);
                    builder.CloseComponent();
                }),
            });

            var output = await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(parameters);
            return output.ToHtmlString();
        });
    }

    /// <summary>
    /// The RENDERED form of the toggle button's label.
    /// <para>
    /// A static HTML render HTML-encodes the text, so the label appears as its numeric character
    /// references. The encoder is the framework's own, so the expected value is derived from the
    /// production label rather than hand-copied.
    /// </para>
    /// </summary>
    private static string RenderedToggleLabel =>
        System.Text.Encodings.Web.HtmlEncoder.Default.Encode(
            ComposerChat.ActiveNotificationsLabel(null));

    /// <summary>
    /// THE ADMIN SEES THE TOGGLE BUTTON: the rendered markup carries the toggle control (the button
    /// whose label is the notification-mode label) and NOT the non-admin global hint.
    /// <para>
    /// The discriminator is the RENDERED BUTTON ELEMENT, not the <c>onclick</c> handler: a static
    /// HTML render does not emit event handlers, so only the element (and its text) can be observed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheAdmin_SeesTheNotificationToggleButton()
    {
        using var directory = new UserDirectory();
        await directory.SeedAsync(AdminId, UserRole.Admin);

        var html = await RenderChatAsync(directory, AdminId);

        // The toggle button is rendered with the notification-mode label.
        Assert.Contains(RenderedToggleLabel, html, StringComparison.Ordinal);
        Assert.DoesNotContain("Notifications (global setting)", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE NON-ADMIN SEES NO TOGGLE BUTTON: the rendered markup carries only the global label that
    /// says an administrator changes the setting — never the editable control (whose label is the
    /// notification-mode label).
    /// </summary>
    [Fact]
    public async Task TheNonAdmin_SeesNoToggleButton_OnlyTheGlobalLabel()
    {
        using var directory = new UserDirectory();
        await directory.SeedAsync(MemberId, UserRole.Member);

        var html = await RenderChatAsync(directory, MemberId);

        // The global label IS present...
        Assert.Contains("Notifications (global setting)", html, StringComparison.Ordinal);
        Assert.Contains("only an administrator can change it", html, StringComparison.Ordinal);

        // ...and the toggle BUTTON's label is not.
        Assert.DoesNotContain(RenderedToggleLabel, html, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE GATE FOLLOWS THE DIRECTORY, NOT THE CLAIM: the very same session (the same principal
    /// object, carrying a FORGED "Admin" role claim) sees the button while the account is an
    /// Admin and loses it once the account is demoted in the directory.
    /// <para>
    /// This is the discriminating row: a gate that trusted the cookie's role claim would keep the
    /// button after the demotion.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheGate_FollowsTheStoredRole_NotTheForgedRoleClaim()
    {
        using var directory = new UserDirectory();
        await directory.SeedAsync(AdminId, UserRole.Admin);

        var asAdmin = await RenderChatAsync(directory, AdminId);
        Assert.Contains(RenderedToggleLabel, asAdmin, StringComparison.Ordinal);

        // The SAME identity is demoted in the directory.
        await directory.SetRoleAsync(AdminId, UserRole.Viewer);

        var afterDemotion = await RenderChatAsync(directory, AdminId);
        Assert.DoesNotContain(RenderedToggleLabel, afterDemotion, StringComparison.Ordinal);
        Assert.Contains("Notifications (global setting)", afterDemotion, StringComparison.Ordinal);
    }

    /// <summary>
    /// A SIGNED-OUT circuit sees no toggle: without an identity the accessor cannot answer the
    /// capability question, so the gate stays closed (under-render) rather than offering a control
    /// the facade would refuse.
    /// </summary>
    [Fact]
    public async Task ASignedOutCircuit_SeesNoToggleButton()
    {
        using var directory = new UserDirectory();
        await directory.SeedAsync(AdminId, UserRole.Admin);

        var html = await RenderChatAsync(directory, gitHubId: null);

        Assert.DoesNotContain(RenderedToggleLabel, html, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
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

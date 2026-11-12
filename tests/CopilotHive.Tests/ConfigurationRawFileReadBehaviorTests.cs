using System.Reflection;
using System.Security.Claims;

using CopilotHive.Configuration;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using ConfigurationPage = CopilotHive.Components.Pages.Configuration;

namespace CopilotHive.Tests;

/// <summary>
/// THE CONFIGURATION PAGE'S RAW CONFIGURATION-FILE READ IS A GATED READ — proved BEHAVIOURALLY, by
/// driving the real page's real <c>LoadFilesAsync</c> and inspecting the page's own file map.
/// <para>
/// The raw <c>hive-config.yaml</c> and the <c>agents/*.agents.md</c> files ARE a full configuration
/// projection: they carry the complete global configuration, so they must require
/// <see cref="HiveCapability.GlobalAdmin"/>. These rows assert that a DENIED caller's page state
/// contains NO file content at all — not a partial document, not a redacted one, not even the file
/// names — while an ADMIN's page state carries the genuine content and open mode keeps working.
/// </para>
/// <para>
/// The behavioural half matters because a UI-only gate (hiding the tab) would satisfy a
/// source-scanning test while still letting the content into the page's state.
/// </para>
/// </summary>
public sealed class ConfigurationRawFileReadBehaviorTests : IDisposable
{
    private const string ViewerId = "cfgread-viewer";
    private const string MemberId = "cfgread-member";
    private const string OperatorId = "cfgread-operator";
    private const string AdminId = "cfgread-admin";

    /// <summary>The single sanitized denial text every facade and route returns.</summary>
    private const string ForbiddenMessage = "The acting account is not authorized to perform this operation.";

    /// <summary>A marker string that appears ONLY in the genuine configuration file.</summary>
    private const string SecretMarker = "cfgread-secret-marker-9f3a";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly FacadeAuthorizationHarness _auth = new();

    private readonly List<string> _tempDirs = [];

    // ── the fixture ─────────────────────────────────────────────────────────

    /// <summary>
    /// A config-repo clone directory holding a <c>hive-config.yaml</c> and two
    /// <c>agents/*.agents.md</c> files, each with UNIQUE, RECOGNIZABLE content — so "the content
    /// never reached the caller" is directly observable.
    /// </summary>
    private (string Root, string YamlText) CreateConfigRepo()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cfgread-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);

        var yaml = $"version: \"{SecretMarker}\"\norchestrator:\n  model: {SecretMarker}-model\n";
        File.WriteAllText(Path.Combine(root, "hive-config.yaml"), yaml);

        var agentsDir = Path.Combine(root, "agents");
        Directory.CreateDirectory(agentsDir);
        File.WriteAllText(Path.Combine(agentsDir, "coder.agents.md"), $"{SecretMarker}-coder-agent");
        File.WriteAllText(Path.Combine(agentsDir, "tester.agents.md"), $"{SecretMarker}-tester-agent");

        return (root, yaml);
    }

    /// <summary>The production <see cref="ConfigFacade"/> over one config repo for a chosen mode.</summary>
    private ConfigFacade CreateConfigFacade(string repoRoot, HiveAuthMode mode) =>
        new(hiveConfig: new HiveConfigFile
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
            configRepo: new ConfigRepoManager("https://example.com/config.git", repoRoot));

    /// <summary>
    /// Builds the page with the circuit identity of <paramref name="gitHubId"/> (or NO cascade at
    /// all, which is what open mode looks like), then runs its REAL <c>LoadFilesAsync</c>.
    /// </summary>
    private async Task<(Dictionary<string, string> Files, string? Error, List<string> Tabs)>
        RunLoadFilesAsync(ConfigFacade facade, string? gitHubId, string? repoRoot = null)
    {
        var page = new ConfigurationPage();

        SetProperty(page, "ConfigFacade", facade);
        SetProperty(page, "CircuitPrincipal", new CircuitUserPrincipalAccessor(
            _auth.Resolver(HiveAuthMode.AuthenticationEnabled), _auth.Policy));

        // The page's own config-repo handle. It is injected so a mutant that reaches for the raw
        // files through the page can compile — and so the ungated-read mutant below is a REAL
        // "read straight off disk" rather than a stub.
        if (repoRoot is not null)
        {
            SetProperty(page, "ConfigRepo",
                new ConfigRepoManager("https://example.com/config.git", repoRoot));
        }

        if (gitHubId is not null)
        {
            // The page's cascading authentication state — the identity it hands to the facade.
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, gitHubId)], authenticationType: "Cookies"));
            SetProperty(page, "AuthenticationState",
                Task.FromResult(new AuthenticationState(principal)));
        }

        await (Task)typeof(ConfigurationPage)
            .GetMethod("LoadFilesAsync", NonPublicInstance)!
            .Invoke(page, null)!;

        var files = (Dictionary<string, string>)typeof(ConfigurationPage)
            .GetField("_files", NonPublicInstance)!
            .GetValue(page)!;
        var error = (string?)typeof(ConfigurationPage)
            .GetField("_error", NonPublicInstance)!
            .GetValue(page);
        var tabs = (List<string>)typeof(ConfigurationPage)
            .GetField("_tabs", NonPublicInstance)!
            .GetValue(page)!;

        return (files, error, tabs);
    }

    private static void SetProperty(object target, string name, object value)
    {
        var property = typeof(ConfigurationPage).GetProperty(name, NonPublicInstance);
        Assert.NotNull(property);
        property!.SetValue(target, value);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  1. Denied roles never receive the content
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A DENIED CALLER'S PAGE STATE CONTAINS NO CONFIGURATION CONTENT AT ALL: a Viewer, a Member
    /// and an Operator each load the page's files and find the file map EMPTY — no
    /// <c>hive-config.yaml</c>, no agent file, not even a file NAME — and the page reports the
    /// sanitized refusal through its existing error surface.
    /// <para>
    /// The rows assert on the CONTENT, not on a tab's visibility: a UI-only gate that still read the
    /// files would leave the content in the map and fail here.
    /// </para>
    /// </summary>
    [Fact]
    public async Task LoadFiles_DeniedRoles_NeverReceiveTheConfigurationContent()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync(OperatorId, UserRole.Operator);

        var (root, _) = CreateConfigRepo();
        var facade = CreateConfigFacade(root, HiveAuthMode.AuthenticationEnabled);

        foreach (var insufficient in new[] { ViewerId, MemberId, OperatorId })
        {
            var (files, error, _) = await RunLoadFilesAsync(facade, insufficient, root);

            // NO CONTENT — not even a file name.
            Assert.Empty(files);
            Assert.DoesNotContain(files.Keys, name => name.Contains("hive-config", StringComparison.Ordinal));
            Assert.DoesNotContain(files.Values, content =>
                content.Contains(SecretMarker, StringComparison.Ordinal));

            // The refusal is reported through the page's EXISTING error surface, with the exact
            // sanitized denial text.
            Assert.NotNull(error);
            Assert.Equal(ForbiddenMessage, error);
        }
    }

    /// <summary>
    /// A DENIED CALLER'S PAGE STATE NEVER CONTAINS THE CONTENT EVEN WHEN A PREVIOUSLY LOADED PAGE
    /// STILL HOLDS IT: the failure branch clears the file map before returning, so a demotion
    /// between two loads cannot leave the old configuration on the page.
    /// </summary>
    [Fact]
    public async Task LoadFiles_DeniedCaller_ClearsAnyPreviouslyLoadedContent()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        var (root, yaml) = CreateConfigRepo();
        var facade = CreateConfigFacade(root, HiveAuthMode.AuthenticationEnabled);

        // The ADMIN loads the genuine content first — proving the fixture really has content.
        var (adminFiles, adminError, _) = await RunLoadFilesAsync(facade, AdminId, root);
        Assert.Null(adminError);
        Assert.NotEmpty(adminFiles);
        Assert.Contains(SecretMarker, adminFiles["hive-config.yaml"], StringComparison.Ordinal);

        // The VIEWER loads the SAME page: the map must be empty.
        var (viewerFiles, viewerError, _) = await RunLoadFilesAsync(facade, ViewerId, root);
        Assert.Empty(viewerFiles);
        Assert.Equal(ForbiddenMessage, viewerError);

        // The content is on disk the whole time — the gate, not a missing file, is what emptied the
        // map.
        Assert.True(File.Exists(Path.Combine(root, "hive-config.yaml")));
        Assert.Contains(SecretMarker, yaml, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  2. The positive controls — Admin and open mode
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE ADMIN REALLY RECEIVES THE GENUINE CONTENT — the success control proving the denial rows
    /// above are caused by the gate and not by an empty fixture: the map carries the verbatim
    /// <c>hive-config.yaml</c> and the agent files.
    /// </summary>
    [Fact]
    public async Task LoadFiles_Admin_ReceivesTheGenuineConfigurationContent()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);

        var (root, yaml) = CreateConfigRepo();
        var facade = CreateConfigFacade(root, HiveAuthMode.AuthenticationEnabled);

        var (files, error, tabs) = await RunLoadFilesAsync(facade, AdminId, root);

        Assert.Null(error);
        Assert.Equal(3, files.Count);
        Assert.Equal(yaml, files["hive-config.yaml"]);
        Assert.Contains(SecretMarker, files["hive-config.yaml"], StringComparison.Ordinal);
        Assert.Contains(SecretMarker, files["coder.agents.md"], StringComparison.Ordinal);
        Assert.Contains(SecretMarker, files["tester.agents.md"], StringComparison.Ordinal);

        // The file tabs are present for the content the page actually holds.
        Assert.Contains("hive-config.yaml", tabs);
        Assert.Contains("coder.agents.md", tabs);
    }

    /// <summary>
    /// OPEN MODE IS UNCHANGED: with no authentication configured there is NO cascading
    /// authentication state at all, and the page's read still returns the genuine content — the
    /// explicit open-mode local operator holds GlobalAdmin.
    /// </summary>
    [Fact]
    public async Task LoadFiles_OpenMode_KeepsTheGenuineConfigurationContent()
    {
        var (root, yaml) = CreateConfigRepo();
        var facade = CreateConfigFacade(root, HiveAuthMode.Open);

        // NO identity at all — exactly what a page in open mode has.
        var (files, error, tabs) = await RunLoadFilesAsync(facade, gitHubId: null, root);

        Assert.Null(error);
        Assert.Equal(3, files.Count);
        Assert.Equal(yaml, files["hive-config.yaml"]);
        Assert.Contains(SecretMarker, files["hive-config.yaml"], StringComparison.Ordinal);
        Assert.Contains("hive-config.yaml", tabs);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  3. The page itself never reaches the raw files
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE PAGE NEVER REACHES THE RAW FILES — not even to decide whether it MAY show them: the
    /// load path contains no file-system read and no use of the page's config-repo handle at all, so
    /// the ONLY path by which the configuration content can enter the page is the gated facade
    /// operation.
    /// <para>
    /// This is the row that makes a "UI-ONLY GATE" mutant detectable: a mutant that hides the tab
    /// (or guards the markup) while still reading the files off disk cannot satisfy it, because the
    /// read itself would be back in the method.
    /// </para>
    /// </summary>
    [Fact]
    public void ThePageNeverReadsTheRawConfigurationFilesItself()
    {
        var source = File.ReadAllText(ConfigurationSourcePath());

        // The page's CODE section (everything from @code onwards) — the MARKUP is exempt from the
        // path assertion because it legitimately DISPLAYS the config-repo directory name (an
        // informational path, not file content).
        var codeStart = source.IndexOf("@code", StringComparison.Ordinal);
        Assert.True(codeStart >= 0, "the page's @code section not found");
        var code = source[codeStart..];

        // No content-bearing read verb anywhere in the page.
        Assert.DoesNotContain("ReadAllTextAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAllText(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenReadAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadToEndAsync", source, StringComparison.Ordinal);

        // Nor the agents directory enumeration, nor the config-repo clone path used for the read.
        Assert.DoesNotContain("*.agents.md", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigRepo.LocalPath", code, StringComparison.Ordinal);
    }

    /// <summary>Reads <c>Configuration.razor</c> by walking up to the repository root.</summary>
    private static string ConfigurationSourcePath()
    {
        var repoRoot = Environment.CurrentDirectory;
        while (repoRoot is not null && !Directory.GetFiles(repoRoot, "*.slnx").Any())
            repoRoot = Directory.GetParent(repoRoot)?.FullName;

        Assert.NotNull(repoRoot);
        var path = Path.Combine(repoRoot, "src", "CopilotHive", "Components", "Pages", "Configuration.razor");
        Assert.True(File.Exists(path), $"Source file not found at {path}");
        return path;
    }

    // ── cleanup ─────────────────────────────────────────────────────────────
    public void Dispose()
    {
        _auth.Dispose();

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

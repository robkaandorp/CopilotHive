using CopilotHive.Components.Pages;
using CopilotHive.Services;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// THE PRODUCTION WIRING of the circuit: <c>Program.cs</c> selects the facade from the authentication mode
/// (never from an environment variable), and the ComposerChat page resolves its runtime through the scoped
/// accessor with the CIRCUIT methods — re-querying it per operation and never disposing a runtime.
/// <para>
/// These are the REMOVAL-PROOF rows for the wiring itself: a revert to the legacy singleton injection, to a
/// cached selection, or to an env-var-driven gate fails here even while every behavioural row still passes.
/// </para>
/// </summary>
public sealed class ComposerCircuitWiringSourceTests
{
    private static string ReadProduction(string relativePath)
    {
        var repoRoot = TestRepoRoot.Find();
        var path = Path.Combine(repoRoot, "src", "CopilotHive", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Source file not found at {path}");
        return File.ReadAllText(path).ReplaceLineEndings("\n");
    }

    private static string ProgramSource() => ReadProduction("Program.cs");

    private static string PageSource() => ReadProduction("Components/Pages/ComposerChat.razor");

    private static string CodeBehindSource() => ReadProduction("Components/Pages/ComposerChat.razor.cs");

    /// <summary>
    /// THE HOST SELECTS THE FACADES FROM THE AUTHENTICATION MODE: <c>Program.cs</c> passes
    /// <c>ForAuthMode(authMode)</c> — the SAME explicit <see cref="CopilotHive.Security.HiveAuthMode"/> instance the
    /// resolver holds — so the live path can never diverge from what every authorization answer really is.
    /// <para>
    /// The retired environment gate is NOT passed: a re-introduction of <c>FromEnvironment()</c> here is exactly
    /// the split-runtime regression this goal closes.
    /// </para>
    /// </summary>
    [Fact]
    public void Program_SelectsTheFacadesFromTheAuthMode_NotFromTheRetiredVariable()
    {
        var program = TestSourceScanner.StripNonCode(ProgramSource());

        Assert.Contains(
            "AddComposerRuntimeFacades(ComposerRuntimeFacadeDeployment.ForAuthMode(authMode))",
            program,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ComposerRuntimeFacadeDeployment.FromEnvironment()", program, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE PAGE INJECTS THE SCOPED RUNTIME ACCESSOR AND THE CIRCUIT METHODS: it resolves
    /// <see cref="IComposerRuntimeAccessor"/> and asks through <c>AcquireForCircuitAsync</c> /
    /// <c>ConnectForCircuitAsync</c> — the circuit identity path, whose optional cascade is what open mode needs.
    /// </summary>
    [Fact]
    public void ThePage_ResolvesTheScopedAccessor_AndUsesTheCircuitMethods()
    {
        var page = PageSource();
        var codeBehind = TestSourceScanner.StripNonCode(CodeBehindSource());

        Assert.Contains("@inject IComposerRuntimeAccessor ComposerRuntimeAccessor", page, StringComparison.Ordinal);

        // The CIRCUIT path, not the per-request path: the page is a circuit, and the optional cascade is what
        // makes open mode work without any authentication services.
        Assert.Contains("accessor.AcquireForCircuitAsync(authenticationState, ct)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("accessor.ConnectForCircuitAsync(authenticationState, ct)", codeBehind, StringComparison.Ordinal);
        // ...and the selection entry point really hands it THIS circuit's cascade.
        var selection = ExtractMethodSource(codeBehind,
            "private async Task<ComposerCircuitStatus> AcquireCircuitRuntimeAsync(");
        Assert.Contains("SelectRuntimeAsync(", selection, StringComparison.Ordinal);
        Assert.Contains("ComposerRuntimeAccessor", selection, StringComparison.Ordinal);
        Assert.Contains("AuthenticationState", selection, StringComparison.Ordinal);
        Assert.DoesNotContain("AcquireForRequestAsync", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectForRequestAsync", codeBehind, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CIRCUIT IDENTITY IS TAKEN FROM THE CASCADING STATE THROUGH THE ONE HELPER, AND THE CASCADE IS OPTIONAL:
    /// open mode has no authentication services at all, so a required cascade (or a required state provider) would
    /// break the single-operator installation.
    /// </summary>
    [Fact]
    public void ThePage_TakesTheCascadeOptionally_AndAcquiresIdentityThroughTheOneHelper()
    {
        var page = PageSource();

        Assert.Contains(
            "[CascadingParameter]\n    private Task<AuthenticationState>? AuthenticationState { get; set; }",
            page,
            StringComparison.Ordinal);
        Assert.DoesNotContain("@inject AuthenticationStateProvider", page, StringComparison.Ordinal);

        // The ONE identity helper the security contract names.
        Assert.Contains("ComponentActingUser", page, StringComparison.Ordinal);
        Assert.Contains("ComponentActingUser.AcquireAsync(AuthenticationState, CircuitPrincipal)", page,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// THE PAGE NEVER DISPOSES A RUNTIME: disposal unsubscribes from the Composer this component was subscribed to
    /// and releases its JS handles — nothing else. The singleton <see cref="CopilotHive.Orchestration.ComposerRuntimeManager"/>
    /// is the sole owner and disposer, so a circuit teardown can never tear down a runtime another request uses.
    /// </summary>
    [Fact]
    public void ThePage_NeverDisposesAComposer()
    {
        var page = PageSource();
        var codeBehind = CodeBehindSource();

        foreach (var source in new[] { page, codeBehind })
        {
            Assert.DoesNotContain("Composer.DisposeAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("LegacyComposer.DisposeAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Composer?.DisposeAsync", source, StringComparison.Ordinal);
        }

        // ...and its disposal path really unsubscribes through the single subscription helper.
        var dispose = ExtractMethodSource(page, "public async ValueTask DisposeAsync()");
        Assert.Contains("UnsubscribeFromBoundComposer();", dispose, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE LEGACY SINGLETON IS THE OPEN-MODE BINDING, NOT THE PAGE'S COMPOSER: the page injects it under an
    /// explicitly legacy name and never resolves content from it inside the authenticated path — a revert to
    /// <c>@inject Composer Composer</c> (the split-runtime shape) fails here.
    /// </summary>
    [Fact]
    public void ThePage_TakesTheLegacyComposerAsTheExplicitOpenModeBinding()
    {
        var page = PageSource();

        Assert.Contains("@inject Composer LegacyComposer", page, StringComparison.Ordinal);
        Assert.Contains("@inject ComposerAttachmentService LegacyAttachments", page, StringComparison.Ordinal);
        Assert.DoesNotContain("@inject Composer Composer\n", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE ACCESSOR IS RE-QUERIED PER OPERATION, NOT PER COMPONENT LIFETIME: the operation algorithm is a
    /// separate seam that is called from the selection entry point, and it holds NO cached access/status of its
    /// own.
    /// </summary>
    [Fact]
    public void TheSelectionSeam_ReQueriesAndCachesNothing()
    {
        var codeBehind = TestSourceScanner.StripNonCode(CodeBehindSource());

        var select = ExtractMethodSource(codeBehind, "internal static async Task<(ComposerRuntimeAccess Access, ComposerCircuitStatus Status)> SelectRuntimeAsync(");

        // At least one acquisition on every call, and the connect only after a deferred answer.
        Assert.Contains("accessor.AcquireForCircuitAsync(", select, StringComparison.Ordinal);
        Assert.Contains("accessor.ConnectForCircuitAsync(", select, StringComparison.Ordinal);

        // No field is read or written: the algorithm has no per-circuit cache of its own.
        Assert.DoesNotContain("_circuit", select, StringComparison.Ordinal);
        Assert.DoesNotContain("_subscribedComposer", select, StringComparison.Ordinal);
    }

    /// <summary>
    /// Extracts the brace-bounded source of a method by its signature prefix.
    /// </summary>
    private static string ExtractMethodSource(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method '{signature}' was not found.");

        var braceStart = source.IndexOf('{', start);
        Assert.True(braceStart >= 0, $"Opening brace for '{signature}' was not found.");

        var depth = 0;
        for (var i = braceStart; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source.Substring(start, i - start + 1);
            }
        }

        Assert.Fail($"Could not find the matching closing brace for '{signature}'.");
        return string.Empty;
    }
}

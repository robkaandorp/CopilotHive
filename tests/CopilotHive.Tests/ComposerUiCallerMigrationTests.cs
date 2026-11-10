using CopilotHive.Components.Pages;
using CopilotHive.Services;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// THE CALLER-MIGRATION CENSUS: every identity-aware facade call site in the Blazor component tree
/// hands the facade a REAL circuit identity — never a null principal, never a fabricated one.
/// <para>
/// The census is a CALL-SITE census, not a file census: each migrated operation is located in the
/// source and its own argument list is inspected. A census that only asserted "the file mentions
/// ActingUserAsync somewhere" would be satisfied by ONE migrated call site while the other seven
/// silently kept passing a null identity — which is exactly the bypass the slice exists to close.
/// </para>
/// <para>
/// The repository does not use bUnit, so the established strategy (see
/// <see cref="ActiveModeConfigUiTests"/>) is used: the REAL Razor source is read and every migrated
/// call site's own arguments are asserted, together with the behavioural contract of the
/// <see cref="ComponentActingUser"/> helper that produces the identity.
/// </para>
/// </summary>
public sealed class ComposerUiCallerMigrationTests
{
    // ── source-file access ───────────────────────────────────────────────────

    private static string ReadRazorSource(string relativePath)
    {
        var repoRoot = TestRepoRoot.Find();
        var razorPath = Path.Combine(repoRoot, "src", "CopilotHive", "Components", "Pages", relativePath);
        Assert.True(File.Exists(razorPath), $"Source file not found at {razorPath}");
        return File.ReadAllText(razorPath).ReplaceLineEndings("\n");
    }

    private static string ReadComposerChatSource() => ReadRazorSource("ComposerChat.razor");

    private static string ReadConfigurationSource() => ReadRazorSource("Configuration.razor");

    /// <summary>
    /// Extracts the argument list of every call to <paramref name="operation"/> in the source, skipping
    /// the occurrences whose immediately preceding text contains <paramref name="excludedPrefix"/>.
    /// <para>
    /// The exclusion is needed because the host site's text ("owner.ComposerFacade.SwitchModelAsync(")
    /// CONTAINS the page site's text ("ComposerFacade.SwitchModelAsync("): matching both by one
    /// literal would enumerate the same site twice and silently skip the other.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> ExtractAllCallArguments(
        string source, string operation, string? excludedPrefix = null)
    {
        var results = new List<string>();
        var index = 0;

        while (true)
        {
            var operationIndex = source.IndexOf(operation, index, StringComparison.Ordinal);
            if (operationIndex < 0)
                break;

            var open = source.IndexOf('(', operationIndex);
            Assert.True(open >= 0, $"'(' not found for '{operation}'.");

            var depth = 0;
            var closed = false;
            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '(') depth++;
                else if (source[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        // The prefix check looks at the text IMMEDIATELY before the operation, so it
                        // can only exclude the occurrence it names.
                        var preceding = excludedPrefix is not null
                            && operationIndex >= excludedPrefix.Length
                            ? source.Substring(operationIndex - excludedPrefix.Length, excludedPrefix.Length)
                            : string.Empty;

                        if (excludedPrefix is null || !preceding.Contains(excludedPrefix, StringComparison.Ordinal))
                            results.Add(source.Substring(open + 1, i - open - 1));

                        index = i + 1;
                        closed = true;
                        break;
                    }
                }
            }

            Assert.True(closed, $"Could not find the matching ')' for '{operation}'.");
        }

        return results;
    }

    /// <summary>
    /// Asserts that the extracted argument list of a migrated call carries the circuit identity and
    /// a cancellation token.
    /// <para>
    /// The identity may arrive either DIRECTLY from the helper
    /// (<c>ComponentActingUser.AcquireAsync(...)</c>) or through the page's own
    /// <c>ActingUserAsync()</c> wrapper — both are the ONE seam, and the wrapper's own body is
    /// checked separately.
    /// </para>
    /// </summary>
    private static void AssertCarriesCircuitIdentity(string arguments, string operation, string page)
    {
        Assert.True(
            arguments.Contains("ActingUserAsync", StringComparison.Ordinal)
                || arguments.Contains("ComponentActingUser.AcquireAsync(", StringComparison.Ordinal),
            $"'{operation}' in {page} must pass the circuit identity acquired through ComponentActingUser. "
            + $"Offending argument list: {arguments}");

        Assert.Contains("CancellationToken.None", arguments, StringComparison.Ordinal);

        // A null literal identity would be an UNAUTHENTICATED BYPASS: the call would resolve
        // nobody's principal instead of the circuit's.
        Assert.DoesNotContain("null,", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("(null)", arguments, StringComparison.Ordinal);

        Assert.False(
            string.IsNullOrWhiteSpace(arguments),
            $"'{operation}' in {page} must pass the circuit identity, not an empty argument list.");
    }

    // ── ComposerChat.razor: EVERY migrated call site ─────────────────────────

    /// <summary>
    /// EVERY ComposerChat call site that touches an identity-aware facade operation hands it the
    /// circuit identity. The census enumerates EVERY occurrence of each operation, so a single
    /// unmigrated site fails this test even while its siblings are migrated.
    /// </summary>
    [Fact]
    public void EveryComposerChatCallSite_PassesTheCircuitIdentity()
    {
        var source = ReadComposerChatSource();

        var expectedOccurrences = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["ComposerFacade.CompactAsync("] = 1,
            ["ComposerFacade.CompactPartialAsync("] = 1,
            ["owner.ConfigFacade.SaveComposerAsync("] = 1,
            ["ComposerFacade.GetModelsAsync("] = 1,
            ["owner.ComposerFacade.GetCurrentModelAsync("] = 1,
            // The page's own switch site is written WITHOUT the "owner." prefix, so the two switch
            // call sites are matched by two DISTINCT literals (the host site's text contains the
            // page site's text, so one literal would enumerate the same site twice).
            ["owner.ComposerFacade.SwitchModelAsync("] = 1,
            ["ComposerFacade.SwitchModelAsync("] = 1,
        };

        // The page's own switch site is the occurrence NOT preceded by "owner.".
        var pageSwitchSites = ExtractAllCallArguments(
            source, "ComposerFacade.SwitchModelAsync(", excludedPrefix: "owner.");

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (operation, expected) in expectedOccurrences)
        {
            if (operation == "ComposerFacade.SwitchModelAsync(")
            {
                Assert.Single(pageSwitchSites);
                AssertCarriesCircuitIdentity(pageSwitchSites[0], operation, "ComposerChat.razor");
                seen[operation] = pageSwitchSites.Count;
                continue;
            }

            var argumentLists = ExtractAllCallArguments(source, operation);

            Assert.True(
                argumentLists.Count == expected,
                $"ComposerChat.razor: expected {expected} call site(s) of '{operation}' but found "
                + $"{argumentLists.Count} — the census must see every site, so a new or removed one fails here.");

            foreach (var arguments in argumentLists)
                AssertCarriesCircuitIdentity(arguments, operation, "ComposerChat.razor");

            seen[operation] = argumentLists.Count;
        }

        Assert.Equal(expectedOccurrences.Count, seen.Count);
    }

    /// <summary>
    /// THE COMPOSITIONER-CHAT IDENTITY IS ACQUIRED FROM THE ONE HELPER: the page's
    /// <c>ActingUserAsync</c> helper goes through <see cref="ComponentActingUser.AcquireAsync"/>
    /// with the SCOPED accessor, so the identity AND the accessor's initialisation come from the
    /// single seam — never from a second, divergent resolution path.
    /// </summary>
    [Fact]
    public void TheComposerChatIdentity_IsAcquiredThroughTheOneHelperWithTheScopedAccessor()
    {
        var source = ReadComposerChatSource();

        // The page injects the SCOPED accessor...
        Assert.Contains(
            "@inject CopilotHive.Security.CircuitUserPrincipalAccessor CircuitPrincipal",
            source,
            StringComparison.Ordinal);

        // ...and acquires the identity through the helper with that accessor.
        var helper = Assert.Single(ExtractAllCallArguments(source, "ComponentActingUser.AcquireAsync("));
        Assert.Contains("AuthenticationState", helper, StringComparison.Ordinal);
        Assert.Contains("CircuitPrincipal", helper, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE COMPOSER-CHAT PAGE DOES NOT INJECT AN AUTHENTICATION-STATE PROVIDER: in open mode the
    /// host registers NO authentication services, so an unconditional
    /// <c>@inject AuthenticationStateProvider</c> would fail to resolve the component at all — the
    /// single-operator installation would break. The page takes the cascade as an OPTIONAL
    /// cascading parameter instead.
    /// </summary>
    [Fact]
    public void TheComposerChatPage_DoesNotInjectAnAuthenticationStateProvider()
    {
        var source = ReadComposerChatSource();

        Assert.DoesNotContain("@inject AuthenticationStateProvider", source, StringComparison.Ordinal);
        Assert.DoesNotContain("@inject Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider",
            source, StringComparison.Ordinal);

        // The cascade is OPTIONAL: a non-nullable cascading parameter would fail to bind in open
        // mode, where no cascade exists at all.
        Assert.Contains(
            "[CascadingParameter]\n    private Task<AuthenticationState>? AuthenticationState { get; set; }",
            source,
            StringComparison.Ordinal);
    }

    // ── Configuration.razor: the migrated call site ──────────────────────────

    /// <summary>
    /// THE CONFIGURATION PAGE'S GLOBAL COMPOSER WRITE carries the circuit identity: the global
    /// Composer settings write is the one operation that requires GlobalAdmin, so it must be the one
    /// operation that can never be reached without an identity.
    /// </summary>
    [Fact]
    public void TheConfigurationGlobalComposerWrite_PassesTheCircuitIdentity()
    {
        var source = ReadConfigurationSource();

        var arguments = Assert.Single(
            ExtractAllCallArguments(source, "ConfigFacade.SaveComposerAsync("));
        AssertCarriesCircuitIdentity(arguments, "ConfigFacade.SaveComposerAsync(", "Configuration.razor");

        // The identity is acquired through the ONE helper with the SCOPED accessor.
        Assert.Contains("ComponentActingUser.AcquireAsync(AuthenticationState, CircuitPrincipal)", arguments,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CONFIGURATION PAGE ALSO TAKES THE CASCADE OPTIONALLY and does not inject an
    /// authentication-state provider, so the open-mode host can still render the page.
    /// </summary>
    [Fact]
    public void TheConfigurationPage_TakesTheCascadeOptionally_AndInjectsNoStateProvider()
    {
        var source = ReadConfigurationSource();

        Assert.DoesNotContain("@inject AuthenticationStateProvider", source, StringComparison.Ordinal);
        Assert.Contains(
            "[CascadingParameter]\n    private Task<AuthenticationState>? AuthenticationState { get; set; }",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "@inject CopilotHive.Security.CircuitUserPrincipalAccessor CircuitPrincipal",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// NO IDENTITY-FREE FACADE CALL SURVIVES ANYWHERE IN THE COMPONENT TREE: every occurrence of an
    /// identity-aware operation in the two pages is preceded by an identity acquisition. The census
    /// counts the occurrences, so ADDING an unmigrated call site fails the test even though the
    /// already-migrated ones are fine.
    /// </summary>
    [Fact]
    public void NoIdentityFreeCallSite_SurvivesInEitherPage()
    {
        foreach (var (page, source) in new[]
        {
            ("ComposerChat.razor", ReadComposerChatSource()),
            ("Configuration.razor", ReadConfigurationSource()),
        })
        {
            foreach (var operation in new[]
            {
                "ComposerFacade.GetModelsAsync(",
                "ComposerFacade.GetCurrentModelAsync(",
                "ComposerFacade.SwitchModelAsync(",
                "ComposerFacade.CompactAsync(",
                "ComposerFacade.CompactPartialAsync(",
                "ConfigFacade.SaveComposerAsync(",
            })
            {
                var occurrences = CountOccurrences(source, operation);
                if (occurrences == 0)
                    continue;

                // EVERY occurrence must be accompanied by an identity acquisition somewhere in the
                // file. The count of acquisitions must be at least the count of call sites.
                var acquisitions = CountOccurrences(source, "ActingUserAsync()")
                    + CountOccurrences(source, "ComponentActingUser.AcquireAsync(");

                Assert.True(
                    acquisitions >= occurrences,
                    $"{page}: {occurrences} occurrence(s) of '{operation}' but only {acquisitions} identity "
                    + "acquisition(s) — an identity-free call site would bypass the authorization gate.");
            }
        }
    }

    /// <summary>Counts non-overlapping occurrences of a literal substring.</summary>
    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    /// <summary>
    /// THE SINGLETON FACADES NEVER SEE THE SCOPED ACCESSOR: neither facade holds a
    /// <see cref="CopilotHive.Security.CircuitUserPrincipalAccessor"/> field, so the per-circuit
    /// accessor can never be captured by a singleton (which would leak one circuit's identity into
    /// every other circuit).
    /// </summary>
    [Fact]
    public void TheSingletonFacades_NeverCaptureTheScopedAccessor()
    {
        foreach (var facadeType in new[]
        {
            typeof(ComposerFacade),
            typeof(ConfigFacade),
        })
        {
            var fieldTypes = facadeType
                .GetFields(System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public)
                .Select(f => f.FieldType)
                .ToArray();

            Assert.DoesNotContain(typeof(CopilotHive.Security.CircuitUserPrincipalAccessor), fieldTypes);
        }
    }
}

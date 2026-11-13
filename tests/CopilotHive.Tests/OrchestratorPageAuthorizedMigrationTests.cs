using CopilotHive.Dashboard;
using CopilotHive.Services;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// REMOVAL-PROOF source-contract tests for the ORCHESTRATOR PAGE migration onto the authorized
/// <see cref="IOperationalFacade"/>.
/// <para>
/// The project does not use bUnit, so — following the established convention of
/// <see cref="ReleasePagesAuthorizedReadMigrationTests"/> — these tests read the ACTUAL razor source
/// and assert what it does and does NOT call. What is pinned:
/// </para>
/// <list type="bullet">
///   <item>The page no longer reaches the trusted services for an operational read or the Brain
///   reset: no <c>State.GetBrainStats(</c>, <c>State.GetComposerStats(</c>,
///   <c>State.GetOrchestratorInfo(</c>, <c>State.ResetBrainSessionAsync(</c> and no
///   <c>LlmSessionRegistry.GetAll(</c> survives. Every one of those would be an identity-free
///   bypass of the authorized boundary.</item>
///   <item>EVERY facade call carries the circuit identity, acquired through the single
///   <c>ComponentActingUser</c> helper from the OPTIONAL authentication cascade and the SCOPED
///   circuit accessor — so no identity-free facade call site can survive, and open mode (no cascade
///   at all) still renders.</item>
///   <item>The private per-user Composer usage section is GONE from the shared page, and the
///   sanitized session projection is what feeds its session table.</item>
/// </list>
/// </summary>
public sealed class OrchestratorPageAuthorizedMigrationTests
{
    private const string PageName = "Orchestrator.razor";

    // ── source access ────────────────────────────────────────────────────────

    private static string ReadOrchestratorSource()
    {
        var repoRoot = TestRepoRoot.Find();
        var path = Path.Combine(repoRoot, "src", "CopilotHive", "Components", "Pages", PageName);
        Assert.True(File.Exists(path), $"Source file not found at {path}");
        return File.ReadAllText(path).ReplaceLineEndings("\n");
    }

    /// <summary>
    /// Removes <c>//</c> line comments and <c>@* ... *@</c> razor comments, so a comment that merely
    /// NAMES a removed call cannot be read as a call site — and, symmetrically, so the removal
    /// assertions below cannot be satisfied (or defeated) by prose.
    /// </summary>
    private static string StripComments(string source)
    {
        var stripped = new System.Text.StringBuilder(source.Length);
        var index = 0;

        while (index < source.Length)
        {
            if (string.CompareOrdinal(source, index, "@*", 0, 2) == 0)
            {
                var close = source.IndexOf("*@", index + 2, StringComparison.Ordinal);
                index = close < 0 ? source.Length : close + 2;
                continue;
            }

            if (string.CompareOrdinal(source, index, "//", 0, 2) == 0)
            {
                while (index < source.Length && source[index] != '\n')
                    index++;
                continue;
            }

            stripped.Append(source[index]);
            index++;
        }

        return stripped.ToString();
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

    // ══════════════════════════════════════════════════════════════════════════
    //  1. No identity-free operational caller survives
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE DIRECT CALLERS ARE GONE. The page must not reach
    /// <see cref="DashboardStateService"/> for an operational read or the Brain reset, and must not
    /// reach <see cref="LlmSessionRegistry.GetAll"/> at all: each of those is exactly the
    /// identity-free bypass this slice closes. The service stays injected ONLY as the page's refresh
    /// event source, which is not an operational read.
    /// </summary>
    [Fact]
    public void ThePage_NoLongerCallsTheTrustedServicesDirectly()
    {
        var code = StripComments(ReadOrchestratorSource());

        foreach (var removed in new[]
        {
            "State.GetBrainStats(",
            "State.GetComposerStats(",
            "State.GetOrchestratorInfo(",
            "State.ResetBrainSessionAsync(",
            "LlmSessionRegistry.GetAll(",
            "SessionRegistry.GetAll(",
            "SessionRegistry",
        })
        {
            Assert.DoesNotContain(removed, code, StringComparison.Ordinal);
        }

        // The ONLY remaining use of the injected dashboard state service is the refresh EVENT
        // subscription, which reads nothing and mutates nothing.
        var stateUsages = code.Split('\n')
            .Where(line => line.Contains("State.", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .ToList();

        Assert.NotEmpty(stateUsages);
        foreach (var usage in stateUsages)
        {
            Assert.Contains("State.OnStateChanged", usage, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// THE RESET IS THE AUTHORIZED ONE: the button handler calls
    /// <c>OperationalFacade.ResetBrainSessionAsync(</c> — never the trusted service — and keeps the
    /// pre-existing confirmation flow and busy/refresh behaviour.
    /// </summary>
    [Fact]
    public void TheResetHandler_UsesTheAuthorizedFacade_AndKeepsTheConfirmFlow()
    {
        var code = StripComments(ReadOrchestratorSource());

        Assert.Contains("OperationalFacade.ResetBrainSessionAsync(", code, StringComparison.Ordinal);

        // Exactly ONE reset call site: the button handler, not a second un-migrated path.
        Assert.Equal(1, CountOccurrences(code, "OperationalFacade.ResetBrainSessionAsync("));

        // The JS confirm flow, the busy flag and the refresh are unchanged.
        Assert.Contains("JS.InvokeAsync<bool>(\"confirm\"", code, StringComparison.Ordinal);
        Assert.Contains("_resettingBrain = true;", code, StringComparison.Ordinal);
        Assert.Contains("_resettingBrain = false;", code, StringComparison.Ordinal);
        Assert.Contains("await RefreshAsync();", code, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  2. Every facade call site carries the circuit identity
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// EVERY <c>OperationalFacade.&lt;Method&gt;(</c> INVOCATION passes the identity acquired through
    /// the single <c>ComponentActingUser</c> seam plus a cancellation token, so no identity-free
    /// facade call site can exist. The census enumerates EVERY occurrence, so ADDING an unmigrated
    /// call site fails here even while the migrated ones are fine.
    /// </summary>
    [Fact]
    public void EveryOperationalFacadeCallSite_PassesTheCircuitIdentity()
    {
        var code = StripComments(ReadOrchestratorSource());

        foreach (var operation in new[]
        {
            "OperationalFacade.ResetBrainSessionAsync(",
            "OperationalFacade.GetBrainStatsAsync(",
            "OperationalFacade.GetOrchestratorInfoAsync(",
            "OperationalFacade.GetSharedLlmSessionsAsync(",
        })
        {
            var occurrences = CountOccurrences(code, operation);
            Assert.True(occurrences >= 1, $"'{operation}' is not used by {PageName}.");

            // Each call is followed (within its own argument list) by the identity + token pair.
            var index = 0;
            while ((index = code.IndexOf(operation, index, StringComparison.Ordinal)) >= 0)
            {
                var open = index + operation.Length - 1;
                var close = FindMatchingParenthesis(code, open);
                Assert.True(close > open, $"Unbalanced argument list for '{operation}'.");

                var arguments = code.Substring(open + 1, close - open - 1);
                Assert.Contains("ActingUserAsync()", arguments, StringComparison.Ordinal);
                Assert.Contains("CancellationToken.None", arguments, StringComparison.Ordinal);

                // A null literal identity would be an UNAUTHENTICATED BYPASS.
                Assert.DoesNotContain("null,", arguments, StringComparison.Ordinal);

                index = close;
            }
        }
    }

    private static int FindMatchingParenthesis(string source, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < source.Length; i++)
        {
            if (source[i] == '(') depth++;
            else if (source[i] == ')')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// THE IDENTITY COMES FROM THE ONE HELPER, WITH THE SCOPED ACCESSOR: the page injects the scoped
    /// <see cref="CopilotHive.Security.CircuitUserPrincipalAccessor"/>, takes the authentication
    /// state as an OPTIONAL cascading parameter and derives the identity with
    /// <c>ComponentActingUser.AcquireAsync(AuthenticationState, CircuitPrincipal)</c> — never the
    /// resolver, the policy or a captured principal.
    /// </summary>
    [Fact]
    public void TheIdentity_IsAcquiredThroughTheOneHelperWithTheScopedAccessor()
    {
        var source = ReadOrchestratorSource();

        Assert.Contains(
            "@inject CopilotHive.Security.CircuitUserPrincipalAccessor CircuitPrincipal",
            source,
            StringComparison.Ordinal);

        // OPTIONAL cascade: a non-nullable parameter would fail to bind in open mode, where no
        // cascade exists at all — and the page must still render there.
        Assert.Contains("[CascadingParameter]", source, StringComparison.Ordinal);
        Assert.Contains(
            "private Task<AuthenticationState>? AuthenticationState { get; set; }",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("@inject AuthenticationStateProvider", source, StringComparison.Ordinal);

        var helper = Assert.Single(
            AllCallArguments(source, "ComponentActingUser.AcquireAsync("));
        Assert.Contains("AuthenticationState", helper, StringComparison.Ordinal);
        Assert.Contains("CircuitPrincipal", helper, StringComparison.Ordinal);

        // The page never reaches a second identity/resolution path.
        var code = StripComments(source);
        Assert.DoesNotContain("HivePrincipalResolver", code, StringComparison.Ordinal);
        Assert.DoesNotContain("HivePolicy", code, StringComparison.Ordinal);
        Assert.DoesNotContain(".ResolveAsync(", code, StringComparison.Ordinal);
    }

    /// <summary>Extracts the argument list of every call to <paramref name="operation"/>.</summary>
    private static IReadOnlyList<string> AllCallArguments(string source, string operation)
    {
        var results = new List<string>();
        var index = 0;

        while ((index = source.IndexOf(operation, index, StringComparison.Ordinal)) >= 0)
        {
            var open = index + operation.Length - 1;
            var close = FindMatchingParenthesis(source, open);
            Assert.True(close > open, $"Unbalanced argument list for '{operation}'.");
            results.Add(source.Substring(open + 1, close - open - 1));
            index = close;
        }

        return results;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  3. Private per-user Composer data is not on the shared page
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE PRIVATE COMPOSER USAGE SECTION IS GONE. Composer statistics are a user's private usage, so
    /// a shared page must not render them — and must not reach the trusted service for them. The
    /// session table stays, fed by the SANITIZED shared projection whose Composer exclusion is
    /// applied by the facade.
    /// </summary>
    [Fact]
    public void ThePage_RendersNoPrivateComposerUsage_ButKeepsTheSanitizedSessionTable()
    {
        var code = StripComments(ReadOrchestratorSource());

        // No Composer usage/statistics surface remains.
        Assert.DoesNotContain("GetComposerStats", code, StringComparison.Ordinal);
        Assert.DoesNotContain("_composerStats", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ComposerContextColor", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Composer not connected.", code, StringComparison.Ordinal);

        // The LLM Sessions table survives, fed by the sanitized shared projection.
        Assert.Contains("LLM Sessions", code, StringComparison.Ordinal);
        Assert.Contains("OperationalFacade.GetSharedLlmSessionsAsync(", code, StringComparison.Ordinal);
        Assert.Contains("_llmSessions", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE FACADE THE PAGE DEPENDS ON IS THE AUTHORIZATION BOUNDARY: every injected operation the
    /// page uses exists on <see cref="IOperationalFacade"/>, so the page cannot be wired to a
    /// narrower — or wider — surface than the one this slice authorized.
    /// </summary>
    [Fact]
    public void ThePageDependsOnlyOnAuthorizedOperationalFacadeOperations()
    {
        var operations = typeof(IOperationalFacade).GetMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

        var code = StripComments(ReadOrchestratorSource());
        foreach (var used in new[]
        {
            nameof(IOperationalFacade.ResetBrainSessionAsync),
            nameof(IOperationalFacade.GetBrainStatsAsync),
            nameof(IOperationalFacade.GetOrchestratorInfoAsync),
            nameof(IOperationalFacade.GetSharedLlmSessionsAsync),
        })
        {
            Assert.Contains(used, operations);
            Assert.Contains($"OperationalFacade.{used}(", code, StringComparison.Ordinal);
        }
    }
}

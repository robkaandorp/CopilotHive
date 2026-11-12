using CopilotHive.Services;

namespace CopilotHive.Tests;

/// <summary>
/// REMOVAL-PROOF source-contract tests for the RELEASE PAGE migration onto the authorized
/// <see cref="IReleaseFacade"/> reads.
/// <para>
/// Because this project does not use bUnit, these tests read the ACTUAL razor sources and scope
/// every assertion to the region it belongs to. They pin:
/// </para>
/// <list type="bullet">
///   <item>NEITHER page reaches the goal store directly for a RELEASE read: the release detail
///   read and the GOAL-BY-RELEASE read both go through the authorized facade methods that
///   re-resolve the caller's current role/enabled state. Only the release LIST read
///   (<c>GetReleasesAsync</c>) may remain a direct store read — the goal's explicit exception.</item>
///   <item>EVERY facade call in both pages passes the circuit identity, acquired through the single
///   <c>ComponentActingUser</c> helper — so no identity-free facade call site can survive.</item>
/// </list>
/// </summary>
public sealed class ReleasePagesAuthorizedReadMigrationTests
{
    // ── source access ────────────────────────────────────────────────────────

    private static string ReadPage(string fileName)
    {
        var repoRoot = Environment.CurrentDirectory;
        while (repoRoot is not null && !Directory.GetFiles(repoRoot, "*.slnx").Any())
            repoRoot = Directory.GetParent(repoRoot)?.FullName;

        Assert.NotNull(repoRoot);
        var path = Path.Combine(repoRoot, "src", "CopilotHive", "Components", "Pages", fileName);
        Assert.True(File.Exists(path), $"Source file not found at {path}");
        return File.ReadAllText(path);
    }

    private static string ReadReleases() => ReadPage("Releases.razor");

    private static string ReadReleaseDetail() => ReadPage("ReleaseDetail.razor");

    /// <summary>
    /// Extracts the source text of a method by signature, so an assertion targets the method body
    /// rather than the whole file.
    /// </summary>
    private static string ExtractMethodSource(string source, string methodSignature, string page)
    {
        var start = source.IndexOf(methodSignature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method '{methodSignature}' not found in {page}");

        var braceStart = source.IndexOf('{', start);
        Assert.True(braceStart >= 0, $"Opening brace for '{methodSignature}' not found in {page}");

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

        Assert.Fail($"Could not find matching closing brace for '{methodSignature}' in {page}");
        return string.Empty; // unreachable
    }

    /// <summary>
    /// Removes <c>//</c> line comments (and the trailing comment on a code line), so a doc comment
    /// that merely NAMES a method cannot be read as a call site.
    /// </summary>
    private static string StripLineComments(string source)
    {
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var index = lines[i].IndexOf("//", StringComparison.Ordinal);
            if (index >= 0)
                lines[i] = lines[i][..index];
        }

        return string.Join("\n", lines);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  1. No identity-free store read of the release detail or the goals
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>ReleaseDetail.razor</c> reads the release and its goals through the AUTHORIZED facade
    /// methods — never directly through the goal store. The facade re-resolves the caller's
    /// CURRENT role/enabled state on every call, so a direct store read would be an
    /// identity-free bypass of the covered release-read surface.
    /// </summary>
    [Fact]
    public void ReleaseDetailPage_ReadsTheReleaseAndItsGoals_ThroughTheAuthorizedFacade()
    {
        var source = ReadReleaseDetail();
        var code = StripLineComments(source);

        // The load path uses the facade, with the identity.
        var load = ExtractMethodSource(code, "protected override async Task OnInitializedAsync()", "ReleaseDetail.razor");
        Assert.Contains("ReleaseFacade.GetReleaseAsync(", load, StringComparison.Ordinal);
        Assert.Contains("ReleaseFacade.GetGoalsByReleaseAsync(", load, StringComparison.Ordinal);
        Assert.Contains("await ActingUserAsync()", load, StringComparison.Ordinal);

        // NO direct goal-store read of either surface remains.
        Assert.DoesNotContain("GoalStore.GetReleaseAsync(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("GoalStore.GetGoalsByReleaseAsync(", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Releases.razor</c> reads each release's goals through the AUTHORIZED facade method —
    /// never directly through the goal store. The release LIST read
    /// (<c>GoalStore.GetReleasesAsync()</c>) is the goal's EXPLICIT exception and may remain.
    /// </summary>
    [Fact]
    public void ReleasesPage_ReadsTheGoalsByRelease_ThroughTheAuthorizedFacade()
    {
        var source = ReadReleases();
        var code = StripLineComments(source);

        var load = ExtractMethodSource(code, "private async Task LoadGoalCountsAsync()", "Releases.razor");
        Assert.Contains("ReleaseFacade.GetGoalsByReleaseAsync(", load, StringComparison.Ordinal);
        Assert.Contains("await ActingUserAsync()", load, StringComparison.Ordinal);

        // The direct goal-by-release store read is gone.
        Assert.DoesNotContain("GoalStore.GetGoalsByReleaseAsync(", code, StringComparison.Ordinal);

        // The release LIST read remains the explicit exception — and it is the ONLY goal-store
        // use left in the page.
        Assert.Contains("GoalStore.GetReleasesAsync()", code, StringComparison.Ordinal);
        foreach (var line in code.Split('\n').Where(l => l.Contains("GoalStore.", StringComparison.Ordinal)))
        {
            Assert.Contains("GetReleasesAsync", line, StringComparison.Ordinal);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  2. Every facade call in the two pages carries the circuit identity
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// EVERY <c>ReleaseFacade.&lt;Method&gt;(</c> INVOCATION in the two pages passes the circuit
    /// identity (through <c>ActingUserAsync()</c>), so no identity-free facade call site can
    /// survive in the component tree.
    /// </summary>
    [Fact]
    public void EveryReleaseFacadeCallSite_InBothPages_PassesTheCircuitIdentity()
    {
        foreach (var (page, source) in new[]
        {
            ("Releases.razor", ReadReleases()),
            ("ReleaseDetail.razor", ReadReleaseDetail()),
        })
        {
            var code = StripLineComments(source);

            var statements = code.Split(';')
                .Where(s => s.Contains("ReleaseFacade.", StringComparison.Ordinal))
                .ToList();

            Assert.NotEmpty(statements);

            foreach (var statement in statements)
            {
                // A facade call either passes the identity inline, or binds it to a local first
                // (the multi-line call form) — in both cases the identity travels with the call.
                var passesInline = statement.Contains("ActingUserAsync()", StringComparison.Ordinal);
                var bindsLocal = statement.Contains("user,", StringComparison.Ordinal)
                                || statement.Contains("user)", StringComparison.Ordinal);

                Assert.True(
                    passesInline || bindsLocal,
                    $"{page}: a release facade call must pass the circuit identity. " +
                    $"Offending statement: {statement.Trim()}");
            }
        }
    }

    /// <summary>
    /// THE IDENTITY IS ACQUIRED THROUGH THE SINGLE HELPER, ONCE PER CIRCUIT: each page injects the
    /// scoped <see cref="CopilotHive.Security.CircuitUserPrincipalAccessor"/>, cascades the
    /// authentication state, and derives the identity with <c>ComponentActingUser.AcquireAsync</c> —
    /// never the resolver, the policy or the principal type directly.
    /// </summary>
    [Fact]
    public void TheReleasePages_AcquireTheIdentityThroughTheSingleHelper()
    {
        foreach (var (page, source) in new[]
        {
            ("Releases.razor", ReadReleases()),
            ("ReleaseDetail.razor", ReadReleaseDetail()),
        })
        {
            Assert.Contains(
                "@inject CopilotHive.Security.CircuitUserPrincipalAccessor CircuitPrincipal", source,
                StringComparison.Ordinal);
            Assert.Contains("[CascadingParameter]", source, StringComparison.Ordinal);
            Assert.Contains("Task<AuthenticationState>? AuthenticationState", source, StringComparison.Ordinal);
            Assert.Contains("ComponentActingUser.AcquireAsync(AuthenticationState, CircuitPrincipal)", source,
                StringComparison.Ordinal);
            Assert.DoesNotContain("HivePrincipalResolver", source, StringComparison.Ordinal);
            Assert.DoesNotContain("HivePolicy", source, StringComparison.Ordinal);
        }
    }
}

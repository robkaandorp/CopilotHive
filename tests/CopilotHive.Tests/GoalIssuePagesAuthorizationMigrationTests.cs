using CopilotHive.Services;

namespace CopilotHive.Tests;

/// <summary>
/// REMOVAL-PROOF source-contract tests for the GOAL/ISSUE OPERATION AUTHORIZATION migration of the
/// three named pages (<c>Goals.razor</c>, <c>GoalDetail.razor</c>, <c>Issues.razor</c>).
/// <para>
/// Because this project does not use bUnit, these tests read the ACTUAL razor sources and scope
/// every assertion to the region it belongs to. They pin:
/// <list type="bullet">
///   <item>Every covered goal/issue read and mutation goes through an AUTHORIZED facade method that
///   receives the circuit identity acquired through the single <c>ComponentActingUser</c> helper.</item>
///   <item>NO direct goal store read of the list/detail/dependencies/iterations and NO direct
///   live-dashboard/progress read remains in the three pages (the release dropdown/list read stays,
///   as this slice's explicit exception).</item>
///   <item>A DENIED refresh CLEARS every previously loaded goal/live/detail/progress/dependency/
///   iteration value BEFORE returning, and never falls back to a direct store read.</item>
/// </list>
/// </para>
/// </summary>
public sealed class GoalIssuePagesAuthorizationMigrationTests
{
    // ── source access ────────────────────────────────────────────────────────

    private static string ReadPage(string fileName)
    {
        var repoRoot = Environment.CurrentDirectory;
        while (repoRoot != null && !Directory.GetFiles(repoRoot, "*.slnx").Any())
            repoRoot = Directory.GetParent(repoRoot)?.FullName;

        Assert.NotNull(repoRoot);
        var path = Path.Combine(repoRoot, "src", "CopilotHive", "Components", "Pages", fileName);
        Assert.True(File.Exists(path), $"Source file not found at {path}");
        return File.ReadAllText(path);
    }

    private static string ReadGoals() => ReadPage("Goals.razor");

    private static string ReadGoalDetail() => ReadPage("GoalDetail.razor");

    private static string ReadIssues() => ReadPage("Issues.razor");

    /// <summary>
    /// Extracts the source text of a method by signature, so an assertion targets the method body
    /// rather than the whole file (a whole-file search could be satisfied by an unrelated method or a
    /// comment).
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

    private static string ExtractDirectiveHeader(string source)
    {
        var header = new List<string>();
        foreach (var line in source.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('@') || trimmed.Length == 0)
            {
                header.Add(line);
                continue;
            }
            break;
        }

        Assert.NotEmpty(header);
        return string.Join("\n", header);
    }

    // ── no direct store / live reads remain ──────────────────────────────────

    /// <summary>
    /// NO DIRECT GOAL-SURFACE READ REMAINS in the three pages. Every occurrence of the goal store is
    /// the RELEASE read (<c>GetReleasesAsync</c>), which this slice keeps as an explicit exception,
    /// and the live dashboard/progress services are never read directly — the pages only subscribe
    /// to the change NOTIFICATION (<c>OnStateChanged</c>) or project a repository URL, neither of
    /// which is a goal-list/detail/dependencies/iterations/live-detail/progress read.
    /// </summary>
    [Fact]
    public void TheThreePages_HaveNoDirectGoalStoreOrLiveReadLeft()
    {
        foreach (var (page, source) in new[]
        {
            ("Goals.razor", ReadGoals()),
            ("GoalDetail.razor", ReadGoalDetail()),
            ("Issues.razor", ReadIssues()),
        })
        {
            // The direct goal-list/detail/dependencies/iterations reads.
            Assert.DoesNotContain("GetAllGoalsAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("GoalStore.GetGoalAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("GoalStore.GetIterationsAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SearchGoalsAsync", source, StringComparison.Ordinal);

            // The direct live dashboard/progress reads.
            Assert.DoesNotContain("State.GetGoalDetail", source, StringComparison.Ordinal);
            Assert.DoesNotContain("GetProgressForGoal", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ProgressReports.", source, StringComparison.Ordinal);

            // The issue store is never reached directly either.
            Assert.DoesNotContain("IIssueStore", source, StringComparison.Ordinal);

            // EVERY goal-store use that remains is the release read (the explicit exception).
            foreach (var line in source.Split('\n').Where(l => l.Contains("GoalStore.", StringComparison.Ordinal)))
            {
                Assert.Contains("GetReleasesAsync", line, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// THE COVERED FACADE CALLS ALL CARRY THE CIRCUIT IDENTITY: every
    /// <c>GoalFacade.&lt;Method&gt;(</c>/<c>IssueFacade.&lt;Method&gt;(</c> INVOCATION in the three
    /// pages passes <c>ActingUserAsync()</c> (or the <c>user</c> local it was assigned from), so no
    /// identity-free facade call site can survive in the component tree.
    /// <para>
    /// Comments are STRIPPED first, so a prose mention of a facade method (a doc comment) can neither
    /// satisfy nor break the assertion.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryCoveredFacadeCallSite_PassesTheCircuitIdentity()
    {
        foreach (var (page, source) in new[]
        {
            ("Goals.razor", ReadGoals()),
            ("GoalDetail.razor", ReadGoalDetail()),
            ("Issues.razor", ReadIssues()),
        })
        {
            var code = StripLineComments(source);

            var statements = SplitStatements(code)
                .Where(s => FacadeInvocation.IsMatch(s))
                .ToList();

            Assert.NotEmpty(statements);

            foreach (var statement in statements)
            {
                Assert.True(
                    statement.Contains("ActingUserAsync()", StringComparison.Ordinal)
                        || statement.Contains(", user,", StringComparison.Ordinal)
                        || statement.Contains(", user)", StringComparison.Ordinal),
                    $"{page}: a facade call must pass the circuit identity. Offending statement: {statement}");
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
    public void TheThreePages_AcquireTheIdentityThroughTheSingleHelper()
    {
        foreach (var (page, source) in new[]
        {
            ("Goals.razor", ReadGoals()),
            ("GoalDetail.razor", ReadGoalDetail()),
            ("Issues.razor", ReadIssues()),
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

    // ── denied refresh clears stale data ────────────────────────────────────

    /// <summary>
    /// <c>Goals.razor</c>: the load path routes the goal list through the authorized facade, and on a
    /// refused read it CLEARS the goal list, the filtered view, the dependency status map and the
    /// per-goal error dictionaries before returning — so a denied refresh presents NO stale goal data.
    /// </summary>
    [Fact]
    public void GoalsPage_RefusedRead_ClearsEveryLoadedValue()
    {
        var source = ReadGoals();
        var load = ExtractMethodSource(source, "private async Task LoadAsync()", "Goals.razor");
        var clear = ExtractMethodSource(source, "private void ClearGoalState()", "Goals.razor");

        Assert.Contains("GoalFacade.GetGoalsAsync(await ActingUserAsync(), CancellationToken.None)", load,
            StringComparison.Ordinal);

        // The refusal branch clears and RETURNS: no store fallback, no assignment after the clear.
        Assert.Contains("if (!goalsResult.Success)", load, StringComparison.Ordinal);
        Assert.Contains("ClearGoalState();", load, StringComparison.Ordinal);
        Assert.Contains("_loadError", load, StringComparison.Ordinal);

        // …and the clear really empties every goal-surface value.
        Assert.Contains("_allGoals = []", clear, StringComparison.Ordinal);
        Assert.Contains("_filtered = []", clear, StringComparison.Ordinal);
        Assert.Contains("_goalStatusById = new()", clear, StringComparison.Ordinal);

        // The release selector renders values DERIVED from these two, so a denied refresh must empty
        // them too (previously loaded release IDs/tags must not stay visible) — and WITHOUT any
        // store reread on the denial path.
        Assert.Contains("_allReleases = []", clear, StringComparison.Ordinal);
        Assert.Contains("_releaseStatusById = new()", clear, StringComparison.Ordinal);
        Assert.DoesNotContain("GetReleasesAsync", clear, StringComparison.Ordinal);
        var denial = load[..load.IndexOf("_allGoals = goalsResult", StringComparison.Ordinal)];
        Assert.DoesNotContain("GetReleasesAsync", denial, StringComparison.Ordinal);

        Assert.Contains("_approveErrors.Clear()", clear, StringComparison.Ordinal);
        Assert.Contains("_revertErrors.Clear()", clear, StringComparison.Ordinal);
        Assert.Contains("_retryErrors.Clear()", clear, StringComparison.Ordinal);
        Assert.Contains("_loaded = false", clear, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>GoalDetail.razor</c>: every read of the page's own data goes through an AUTHORIZED facade
    /// method, and every refusal path CLEARS the live detail, the stored goal, its iterations, the
    /// progress feed, the timeline, the dependency statuses, the linked issues and the release
    /// dropdown before returning — and never falls back to a direct store read.
    /// </summary>
    [Fact]
    public void GoalDetailPage_RefusedRead_ClearsEveryLoadedValue()
    {
        var source = ReadGoalDetail();
        var refresh = ExtractMethodSource(source, "private async Task RefreshAsync()", "GoalDetail.razor");
        var clear = ExtractMethodSource(source, "private void ClearLoadedState(string? error)", "GoalDetail.razor");

        // EVERY read the page performs is an authorized facade call.
        foreach (var call in new[]
        {
            "GoalFacade.GetLiveGoalDetailAsync(GoalId, user, ct)",
            "GoalFacade.GetProgressAsync(GoalId, user, ct)",
            "GoalFacade.GetGoalAsync(GoalId, user, ct)",
            "GoalFacade.GetIterationsAsync(GoalId, user, ct)",
            "GoalFacade.GetDependencyStatusesAsync(GoalId, user, ct)",
            "GoalFacade.GetLinkedIssuesAsync(GoalId, user, ct)",
        })
        {
            Assert.Contains(call, refresh, StringComparison.Ordinal);
        }

        // Every refusal branch clears and returns.
        Assert.True(
            CountOccurrences(refresh, "ClearLoadedState(") >= 6,
            "every refused read in RefreshAsync must clear the loaded state");
        Assert.DoesNotContain("GoalStore.GetGoalAsync", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("GoalStore.GetIterationsAsync", refresh, StringComparison.Ordinal);

        // …and the clear really empties every goal/live/progress/dependency/iteration value.
        Assert.Contains("_detail = null", clear, StringComparison.Ordinal);
        Assert.Contains("_storedGoal = null", clear, StringComparison.Ordinal);
        Assert.Contains("_storedIterations = []", clear, StringComparison.Ordinal);
        Assert.Contains("_progress = []", clear, StringComparison.Ordinal);
        Assert.Contains("_timeline = []", clear, StringComparison.Ordinal);
        Assert.Contains("_dependencyStatuses = new()", clear, StringComparison.Ordinal);
        Assert.Contains("_linkedIssues = []", clear, StringComparison.Ordinal);
        Assert.Contains("_planningReleases = []", clear, StringComparison.Ordinal);
        Assert.Contains("_goalReleaseId = null", clear, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Issues.razor</c>: the list read is authorized, and a refused refresh CLEARS the issue list,
    /// the expanded detail, the per-issue errors and the loaded marker — so a denied refresh presents
    /// NO stale issue data.
    /// </summary>
    [Fact]
    public void IssuesPage_RefusedRead_ClearsEveryLoadedValue()
    {
        var source = ReadIssues();
        var load = ExtractMethodSource(source, "private async Task LoadIssues()", "Issues.razor");
        var clear = ExtractMethodSource(source, "private void ClearIssueState()", "Issues.razor");

        Assert.Contains("IssueFacade.GetIssuesAsync(new IssueFilter(", load, StringComparison.Ordinal);
        Assert.Contains("await ActingUserAsync()", load, StringComparison.Ordinal);

        Assert.Contains("if (!result.Success)", load, StringComparison.Ordinal);
        Assert.Contains("ClearIssueState();", load, StringComparison.Ordinal);

        Assert.Contains("_issues = []", clear, StringComparison.Ordinal);
        Assert.Contains("_expandedIssueId = null", clear, StringComparison.Ordinal);
        Assert.Contains("_errors.Clear()", clear, StringComparison.Ordinal);
        Assert.Contains("_loaded = false", clear, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE DENIAL IS AN AUTHORIZATION ANSWER, NOT A VALIDATION ONE: the pages surface the facade's
    /// error text (which for a refusal is the sanitized denial message) and never re-derive a status
    /// from the identity — no page decides authorization locally. The capability question is never
    /// asked on these pages; only the facade's own <see cref="FacadeErrorKind"/> answer is read.
    /// </summary>
    [Fact]
    public void TheThreePages_SurfaceTheFacadeError_WithoutDerivingAuthorizationLocally()
    {
        foreach (var (page, source) in new[]
        {
            ("Goals.razor", ReadGoals()),
            ("GoalDetail.razor", ReadGoalDetail()),
            ("Issues.razor", ReadIssues()),
        })
        {
            // No page asks a capability question, calls the resolver or evaluates the policy: the
            // authorization answer comes from the facade, never from the component.
            Assert.DoesNotContain("HiveCapability", source, StringComparison.Ordinal);
            Assert.DoesNotContain("FacadeAuthorization", source, StringComparison.Ordinal);
            Assert.DoesNotContain(".ResolveAsync(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IsAllowedAsync(", source, StringComparison.Ordinal);

            // …and each page SURFACES the facade's error text instead of inventing its own wording.
            Assert.Contains("result.Error", source, StringComparison.Ordinal);
        }

        // The resume/extend handler keeps its dedicated NotFound presentation, keyed off the facade's
        // typed error kind rather than an HTTP status — the one page-level kind read in the slice.
        Assert.Contains("FacadeErrorKind.NotFound", ReadGoalDetail(), StringComparison.Ordinal);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>Matches a real facade INVOCATION: <c>GoalFacade.Method(</c> or <c>IssueFacade.Method(</c>.</summary>
    private static readonly System.Text.RegularExpressions.Regex FacadeInvocation = new(
        @"(?:GoalFacade|IssueFacade)\.[A-Za-z]+\(",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Removes <c>//</c> line comments (and the trailing comment on a code line), so a doc comment
    /// that merely NAMES a facade method cannot be read as a call site. The razor sources contain no
    /// line comment inside a string literal that matters here.
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

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    /// <summary>
    /// Splits source into semicolon-delimited statements. Razor markup is included verbatim, which is
    /// harmless here: a ';' inside markup simply ends a statement that contains no facade call.
    /// </summary>
    private static IEnumerable<string> SplitStatements(string source)
    {
        var start = 0;
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] != ';')
                continue;

            yield return source[start..(i + 1)];
            start = i + 1;
        }
    }
}

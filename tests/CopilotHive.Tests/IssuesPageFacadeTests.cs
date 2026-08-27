using CopilotHive.Services;

namespace CopilotHive.Tests;

/// <summary>
/// REMOVAL-PROOF source-contract tests for the <c>Issues.razor</c> migration onto
/// <see cref="IIssueFacade"/>. Because the project does not use bUnit, these tests read the
/// actual <c>Issues.razor</c> source and assert that:
/// <list type="bullet">
///   <item>The page injects <see cref="IIssueFacade"/> and no longer injects an
///   <see cref="HttpClient"/>.</item>
///   <item>No handler anywhere reaches for an <c>HttpClient</c> or an <c>/api/issues</c> route.</item>
///   <item>Each handler calls its facade method, scoped to the handler body so a single reverted
///   handler fails.</item>
///   <item>The error-carrying handlers surface the facade's inner error text directly — no
///   status prefix, no JSON envelope.</item>
///   <item>Existing UI behaviour survives: the triage dropdowns with <c>ToSnakeCase&lt;T&gt;</c>
///   option values via <c>@bind</c>, the stale-concurrent-response generation counter, the
///   refresh-after-mutation patterns, rollback on failed PATCH, and the closed-by-default /
///   max-width title handling.</item>
/// </list>
/// </summary>
public sealed class IssuesPageFacadeTests
{
    // ── source-file access ───────────────────────────────────────────────────

    /// <summary>
    /// Reads the actual <c>Issues.razor</c> source by walking up from the current directory
    /// to the repo root (identified by the presence of a <c>*.slnx</c> file).
    /// </summary>
    private static string ReadIssuesRazorSource()
    {
        var repoRoot = Environment.CurrentDirectory;
        while (repoRoot != null && !Directory.GetFiles(repoRoot, "*.slnx").Any())
        {
            repoRoot = Directory.GetParent(repoRoot)?.FullName;
        }
        Assert.NotNull(repoRoot);

        var razorPath = Path.Combine(repoRoot, "src", "CopilotHive", "Components", "Pages", "Issues.razor");
        Assert.True(File.Exists(razorPath), $"Source file not found at {razorPath}");
        return File.ReadAllText(razorPath);
    }

    /// <summary>
    /// Extracts the directive header (the <c>@page</c>/<c>@using</c>/<c>@inject</c> block at the
    /// top of the file) so injection assertions target the header rather than the whole file.
    /// </summary>
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

    /// <summary>
    /// Extracts the source text of a method by signature so assertions target the method body
    /// rather than the whole file (a whole-file search can be satisfied by an unrelated method
    /// or a comment).
    /// </summary>
    private static string ExtractMethodSource(string source, string methodSignature)
    {
        var start = source.IndexOf(methodSignature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method '{methodSignature}' not found in Issues.razor");

        var braceStart = source.IndexOf('{', start);
        Assert.True(braceStart >= 0, $"Opening brace for '{methodSignature}' not found");

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

        Assert.Fail($"Could not find matching closing brace for '{methodSignature}'");
        return string.Empty; // unreachable
    }

    /// <summary>Extracts one concrete Razor element identified by a unique marker.</summary>
    private static string ExtractElementContaining(string source, string marker, string elementName)
    {
        var markerIndex = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"Marker '{marker}' not found in Issues.razor");

        var start = source.LastIndexOf($"<{elementName}", markerIndex, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Opening <{elementName}> for '{marker}' not found");
        var closingTag = $"</{elementName}>";
        var end = source.IndexOf(closingTag, markerIndex, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Closing {closingTag} for '{marker}' not found");
        return source.Substring(start, end - start + closingTag.Length);
    }

    /// <summary>Extracts the single concrete source line containing a unique marker.</summary>
    private static string ExtractLineContaining(string source, string marker)
    {
        var matches = source.Split('\n').Where(line => line.Contains(marker, StringComparison.Ordinal)).ToList();
        return Assert.Single(matches).Trim();
    }

    // ── injection and HttpClient removal ─────────────────────────────────────

    /// <summary>
    /// REMOVAL-PROOF: <c>Issues.razor</c> injects <see cref="IIssueFacade"/> and no longer
    /// injects an <see cref="HttpClient"/>. Reverting the page to HttpClient fails this test.
    /// </summary>
    [Fact]
    public void IssuesPage_InjectsIssueFacadeAndNotHttpClient()
    {
        var source = ReadIssuesRazorSource();
        var injectDirectives = ExtractDirectiveHeader(source).Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("@inject ", StringComparison.Ordinal))
            .ToList();
        Assert.Contains("@inject IIssueFacade IssueFacade", injectDirectives);
        Assert.DoesNotContain(injectDirectives,
            line => line.StartsWith("@inject HttpClient ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Each issue operation handler is independently free of HTTP transport calls. Scoping the
    /// assertion to each method prevents an unrelated facade call elsewhere in the file from
    /// hiding a reverted handler.
    /// </summary>
    [Theory]
    [InlineData("private async Task LoadIssues()")]
    [InlineData("private async Task UpdateField(LinkedIssueDto issue, string field, string newValue)")]
    [InlineData("private async Task DeleteIssue(LinkedIssueDto issue)")]
    [InlineData("private async Task CreateIssue()")]
    public void IssuesPage_OperationHandler_ContainsNoHttpTransport(string signature)
    {
        var method = ExtractMethodSource(ReadIssuesRazorSource(), signature);
        Assert.DoesNotContain("HttpClient", method);
        Assert.DoesNotContain("/api/issues", method);
        Assert.DoesNotContain("ReadAsStringAsync", method);
        Assert.DoesNotContain("StatusCode", method);
    }

    /// <summary>
    /// The page uses the SHARED facade DTO (<see cref="LinkedIssueDto"/>) instead of a private
    /// response DTO, and no private <c>IssueResponse</c> record remains.
    /// </summary>
    [Fact]
    public void IssuesPage_UsesSharedLinkedIssueDto()
    {
        var declarations = ReadIssuesRazorSource().Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("private ", StringComparison.Ordinal))
            .ToList();
        Assert.Contains("private List<LinkedIssueDto> _issues = [];", declarations);
        Assert.DoesNotContain(declarations,
            line => line.StartsWith("private record IssueResponse", StringComparison.Ordinal));
        Assert.NotNull(typeof(LinkedIssueDto).GetProperty("Id"));
        Assert.NotNull(typeof(LinkedIssueDto).GetProperty("LinkedGoalId"));
    }

    // ── per-handler facade calls ──────────────────────────────────────────────

    /// <summary>
    /// Each <c>Issues.razor</c> handler calls its facade method, scoped to the handler body so
    /// a single reverted handler fails.
    /// </summary>
    [Theory]
    [InlineData("private async Task LoadIssues()", "var result = await IssueFacade.GetIssuesAsync(new IssueFilter(")]
    [InlineData("private async Task UpdateField(LinkedIssueDto issue, string field, string newValue)", "var result = await IssueFacade.UpdateIssueAsync(issue.Id, request, CancellationToken.None);")]
    [InlineData("private async Task DeleteIssue(LinkedIssueDto issue)", "var result = await IssueFacade.DeleteIssueAsync(issue.Id, CancellationToken.None);")]
    [InlineData("private async Task CreateIssue()", "var result = await IssueFacade.CreateIssueAsync(new CreateIssueRequest(")]
    public void IssuesPage_Handler_CallsItsFacadeMethod(string signature, string expectedCall)
    {
        var method = ExtractMethodSource(ReadIssuesRazorSource(), signature);
        Assert.Contains(expectedCall, method);
        Assert.DoesNotContain("HttpClient", method);
        Assert.DoesNotContain("GetAsync", method);
        Assert.DoesNotContain("PostAsync", method);
        Assert.DoesNotContain("PatchAsync", method);
        Assert.DoesNotContain("DeleteAsync", method);
    }

    /// <summary>
    /// The error-carrying handlers surface the facade's inner error text into their error
    /// dictionaries — no status prefix, no JSON envelope.
    /// </summary>
    [Theory]
    [InlineData("private async Task UpdateField(LinkedIssueDto issue, string field, string newValue)", "_errors[issue.Id] = result.Error ?? \"\"")]
    [InlineData("private async Task DeleteIssue(LinkedIssueDto issue)", "_errors[issue.Id] = result.Error ?? \"\"")]
    [InlineData("private async Task CreateIssue()", "_createError = result.Error ?? \"\"")]
    public void IssuesPage_Handler_SurfacesTheFacadeErrorDirectly(string signature, string expectedAssignment)
    {
        var method = ExtractMethodSource(ReadIssuesRazorSource(), signature);
        Assert.Contains(expectedAssignment, method);
        Assert.DoesNotContain("ReadAsStringAsync", method);
        Assert.DoesNotContain("Error {(int)response.StatusCode}", method);
    }

    // ── preserved UI behaviour ───────────────────────────────────────────────

    /// <summary>
    /// The triage dropdowns still bind their option values through <c>ToSnakeCase&lt;T&gt;</c>
    /// and re-trigger the update handlers via <c>@bind:after</c>.
    /// </summary>
    [Fact]
    public void IssuesPage_TriageDropdowns_UseToSnakeCaseOptionValues()
    {
        var source = ReadIssuesRazorSource();
        var statusSelect = ExtractElementContaining(source, "@bind=\"_editStatus\"", "select");
        Assert.Contains("<option value=\"@ToSnakeCase(s)\">@s</option>", statusSelect);
        Assert.Contains("@bind:after=\"() => UpdateStatus(issue)\"", statusSelect);

        var severitySelect = ExtractElementContaining(source, "@bind=\"_editSeverity\"", "select");
        Assert.Contains("<option value=\"@ToSnakeCase(s)\">@s</option>", severitySelect);
        Assert.Contains("@bind:after=\"() => UpdateSeverity(issue)\"", severitySelect);

        var typeSelect = ExtractElementContaining(source, "@bind=\"_editType\"", "select");
        Assert.Contains("<option value=\"@ToSnakeCase(t)\">@t</option>", typeSelect);
        Assert.Contains("@bind:after=\"() => UpdateType(issue)\"", typeSelect);
    }

    /// <summary>
    /// The stale-concurrent-response generation counter survives: a stale response is discarded
    /// after the await, and the counter is incremented at the start of every load.
    /// </summary>
    [Fact]
    public void IssuesPage_LoadIssues_KeepsGenerationCounter()
    {
        var method = ExtractMethodSource(ReadIssuesRazorSource(), "private async Task LoadIssues()");
        Assert.Contains("var generation = Interlocked.Increment(ref _loadGeneration);", method);
        Assert.Contains("if (generation != _loadGeneration)", method);
        Assert.Contains("return; // stale — discard", method);
    }

    /// <summary>
    /// The refresh-after-mutation patterns survive: both the update and create handlers
    /// re-query with the active filters after a successful mutation.
    /// </summary>
    [Fact]
    public void IssuesPage_Mutations_RefreshAfterSuccess()
    {
        var update = ExtractMethodSource(ReadIssuesRazorSource(), "private async Task UpdateField(LinkedIssueDto issue, string field, string newValue)");
        Assert.Contains("await LoadIssues();", update);

        var create = ExtractMethodSource(ReadIssuesRazorSource(), "private async Task CreateIssue()");
        Assert.Contains("await LoadIssues();", create);
    }

    /// <summary>
    /// Rollback on failed PATCH survives: when an update fails, the triage dropdowns are reset
    /// to the current (unchanged) issue values.
    /// </summary>
    [Fact]
    public void IssuesPage_FailedUpdate_RollsBackTriageDropdowns()
    {
        var method = ExtractMethodSource(ReadIssuesRazorSource(), "private async Task UpdateField(LinkedIssueDto issue, string field, string newValue)");
        Assert.Contains("_errors[issue.Id] = result.Error ?? \"\";", method);
        Assert.Contains("var current = _issues.FirstOrDefault(i => i.Id == issue.Id);", method);
        Assert.Contains("_editStatus = ToSnakeCase(current.Status);", method);
        Assert.Contains("_editSeverity = ToSnakeCase(current.Severity);", method);
        Assert.Contains("_editType = ToSnakeCase(current.Type);", method);
    }

    /// <summary>
    /// The closed-by-default handling survives: with no status filter, closed issues are hidden
    /// from the list.
    /// </summary>
    [Fact]
    public void IssuesPage_ClosedByDefault_StillFiltersClosedIssues()
    {
        var method = ExtractMethodSource(ReadIssuesRazorSource(), "private async Task LoadIssues()");
        Assert.Contains("if (string.IsNullOrEmpty(_statusFilter))", method);
        Assert.Contains("issues = issues.Where(i => i.Status != IssueStatus.Closed).ToList();", method);
    }

    /// <summary>
    /// The max-width title handling survives: the title cell keeps its 300px ellipsis.
    /// </summary>
    [Fact]
    public void IssuesPage_MaxWidthTitle_StillPresent()
    {
        var source = ReadIssuesRazorSource();
        var titleLine = ExtractLineContaining(source, "title=\"@issue.Title\"");
        Assert.Contains("max-width:300px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-weight:500", titleLine);

        var idLine = ExtractLineContaining(source, ">@issue.Id</div>");
        Assert.Contains("max-width:300px;font-size:0.75rem;color:var(--text-muted);overflow:hidden;text-overflow:ellipsis;white-space:nowrap", idLine);
    }

    /// <summary>
    /// The create form's local validation (Title then Description) survives the migration.
    /// </summary>
    [Fact]
    public void IssuesPage_CreateForm_KeepsLocalValidation()
    {
        var method = ExtractMethodSource(ReadIssuesRazorSource(), "private async Task CreateIssue()");
        Assert.Contains("_createError = \"Title is required.\";", method);
        Assert.Contains("_createError = \"Description is required.\";", method);
    }

    /// <summary>
    /// The create form resets its fields and closes on success, exactly as before.
    /// </summary>
    [Fact]
    public void IssuesPage_CreateForm_ResetsOnSuccess()
    {
        var method = ExtractMethodSource(ReadIssuesRazorSource(), "private async Task CreateIssue()");
        Assert.Contains("_createTitle = \"\";", method);
        Assert.Contains("_createDescription = \"\";", method);
        Assert.Contains("_createRepos = \"\";", method);
        Assert.Contains("_createType = IssueType.Suggestion;", method);
        Assert.Contains("_createSeverity = IssueSeverity.Low;", method);
        Assert.Contains("_showCreateForm = false;", method);
    }

    /// <summary>
    /// The delete handler removes the issue from the local list and collapses the expanded row
    /// on success, exactly as before.
    /// </summary>
    [Fact]
    public void IssuesPage_Delete_RemovesFromListAndCollapses()
    {
        var method = ExtractMethodSource(ReadIssuesRazorSource(), "private async Task DeleteIssue(LinkedIssueDto issue)");
        Assert.Contains("_issues.RemoveAll(i => i.Id == issue.Id);", method);
        Assert.Contains("_expandedIssueId = null;", method);
    }
}

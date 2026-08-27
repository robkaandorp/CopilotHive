using CopilotHive.Goals;
using CopilotHive.Services;

using System.Text.RegularExpressions;

namespace CopilotHive.Tests;

/// <summary>
/// REMOVAL-PROOF source-contract tests for the <c>Releases.razor</c> and
/// <c>ReleaseDetail.razor</c> migration onto <see cref="IReleaseFacade"/>. Because the project
/// does not use bUnit, these tests read the actual <c>.razor</c> source and assert that:
/// <list type="bullet">
///   <item>Both pages inject <see cref="IReleaseFacade"/> and no longer inject an
///   <see cref="HttpClient"/>.</item>
///   <item>Each handler calls its facade method, scoped to the handler body so a single reverted
///   handler fails.</item>
///   <item>The <see cref="StatusFailureOutcome"/> branch mapping in <c>MarkAsReleased</c> assigns
///   the exact per-branch state: plain errors → <c>_releaseError</c>, validation →
///   <c>_validationErrors</c>, execution → <c>_releaseResult</c> + <c>_releaseError</c> +
///   <c>_executionFailed</c>, 503 → <c>_releaseError</c> with the detail text.</item>
///   <item>A failed validate surfaces the generic "Validation request failed." message — never
///   the facade's <c>result.Error</c> text.</item>
///   <item>The per-operation inline error fields (<c>_notesError</c> / <c>_tagError</c> /
///   <c>_repositoriesError</c> / <c>_deleteError</c>) each receive their own operation's error.</item>
///   <item><c>_release</c> is initialized from the domain <see cref="Release"/> and replaced with
///   the returned <see cref="ReleaseDto"/> after each successful mutation.</item>
///   <item>Delete success navigates away; a thrown exception keeps the
///   "Failed to delete release: …" formatting.</item>
///   <item>The create-success path maps the <see cref="ReleaseDto"/> back to the domain
///   <see cref="Release"/>, inserts into the list, clears/closes the form, and reapplies
///   filters without navigating.</item>
/// </list>
/// </summary>
public sealed class ReleasePagesFacadeTests
{
    // ── source-file access ───────────────────────────────────────────────────

    /// <summary>
    /// Reads the actual <c>.razor</c> source by walking up from the current directory to the
    /// repo root (identified by the presence of a <c>*.slnx</c> file).
    /// </summary>
    private static string ReadRazorSource(string fileName)
    {
        var repoRoot = Environment.CurrentDirectory;
        while (repoRoot != null && !Directory.GetFiles(repoRoot, "*.slnx").Any())
        {
            repoRoot = Directory.GetParent(repoRoot)?.FullName;
        }
        Assert.NotNull(repoRoot);

        var razorPath = Path.Combine(repoRoot, "src", "CopilotHive", "Components", "Pages", fileName);
        Assert.True(File.Exists(razorPath), $"Source file not found at {razorPath}");
        return File.ReadAllText(razorPath);
    }

    private static string ReadReleasesSource() => ReadRazorSource("Releases.razor");

    private static string ReadReleaseDetailSource() => ReadRazorSource("ReleaseDetail.razor");

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
        Assert.True(start >= 0, $"Method '{methodSignature}' not found");

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
        Assert.True(markerIndex >= 0, $"Marker '{marker}' not found");

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

    /// <summary>
    /// Extracts the <c>switch (failure.Kind)</c> arm source from <c>MarkAsReleased</c> for a
    /// given <see cref="FacadeErrorKind"/> case label, so the branch assertions target only that
    /// branch's assignments and cannot be satisfied by a sibling branch.
    /// </summary>
    private static string ExtractStatusFailureBranch(string markAsReleasedSource, string kindLabel)
    {
        var caseStart = markAsReleasedSource.IndexOf(
            $"case FacadeErrorKind.{kindLabel}", StringComparison.Ordinal);
        Assert.True(caseStart >= 0, $"case FacadeErrorKind.{kindLabel} not found in MarkAsReleased");

        var colon = markAsReleasedSource.IndexOf(':', caseStart);
        Assert.True(colon >= 0, $"Case label '{kindLabel}' has no body");

        // The branch body ends at the next "case " or "default:" label (or the switch's closing
        // brace) at any indentation — a brace-bounded body extraction does not apply because a
        // case body may be braceless.
        var nextLabel = Regex.Match(
            markAsReleasedSource[(colon + 1)..],
            @"(case FacadeErrorKind\.|default:)",
            RegexOptions.CultureInvariant);
        if (nextLabel.Success)
            return markAsReleasedSource.Substring(colon + 1, nextLabel.Index).TrimEnd();

        var switchEnd = markAsReleasedSource.LastIndexOf('}');
        Assert.True(switchEnd > colon, $"No switch end found after case '{kindLabel}'");
        return markAsReleasedSource.Substring(colon + 1, switchEnd - colon - 1).TrimEnd();
    }

    // ── 1. facade injection and HttpClient removal (revert-proof) ────────────

    /// <summary>
    /// REMOVAL-PROOF: both pages inject <see cref="IReleaseFacade"/> and neither injects an
    /// <see cref="HttpClient"/>. Reverting either page to HttpClient fails this test.
    /// </summary>
    [Theory]
    [InlineData("Releases.razor")]
    [InlineData("ReleaseDetail.razor")]
    public void ReleasePages_InjectReleaseFacadeAndNotHttpClient(string fileName)
    {
        var injectDirectives = ExtractDirectiveHeader(ReadRazorSource(fileName)).Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("@inject ", StringComparison.Ordinal))
            .ToList();
        Assert.Contains(injectDirectives, line =>
            line.StartsWith("@inject IReleaseFacade ", StringComparison.Ordinal));
        Assert.DoesNotContain(injectDirectives,
            line => line.StartsWith("@inject HttpClient ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every release handler is independently free of HTTP transport calls. Scoping the
    /// assertion to each method prevents an unrelated facade call elsewhere in the file from
    /// hiding a reverted handler.
    /// </summary>
    [Theory]
    [InlineData("ReleaseDetail.razor", "private async Task ValidateReleaseAsync()")]
    [InlineData("ReleaseDetail.razor", "private async Task MarkAsReleased()")]
    [InlineData("ReleaseDetail.razor", "private async Task DeleteReleaseAsync()")]
    [InlineData("ReleaseDetail.razor", "private async Task SaveNotes()")]
    [InlineData("ReleaseDetail.razor", "private async Task SaveTag()")]
    [InlineData("ReleaseDetail.razor", "private async Task SaveRepositories()")]
    [InlineData("Releases.razor", "private async Task CreateRelease()")]
    public void ReleasePages_OperationHandler_ContainsNoHttpTransport(string fileName, string signature)
    {
        var method = ExtractMethodSource(ReadRazorSource(fileName), signature);
        Assert.DoesNotContain("HttpClient", method);
        Assert.DoesNotContain("GetAsync", method);
        Assert.DoesNotContain("PostAsync", method);
        Assert.DoesNotContain("PatchAsync", method);
        Assert.DoesNotContain("DeleteAsync", method);
        Assert.DoesNotContain("ReadAsStringAsync", method);
        Assert.DoesNotContain("StatusCode", method);
    }

    // ── 2. branch-specific StatusFailureOutcome mapping ──────────────────────

    /// <summary>
    /// A plain <c>Error</c> outcome (NotFound / BadRequest-without-errors / Conflict) maps ONLY
    /// to <c>_releaseError</c> with the outcome's <c>Error</c> text — never to
    /// <c>_validationErrors</c> and never to the <c>Detail</c> field. The three case labels
    /// share one fall-through body in the production switch; the test asserts the whole
    /// fall-through GROUP is bounded by exactly those labels and that single assignment.
    /// </summary>
    [Fact]
    public void ReleaseDetail_MarkAsReleased_PlainErrorKinds_AssignReleaseErrorOnly()
    {
        var method = ExtractMethodSource(ReadReleaseDetailSource(), "private async Task MarkAsReleased()");
        var groupStart = method.IndexOf("case FacadeErrorKind.NotFound:", StringComparison.Ordinal);
        Assert.True(groupStart >= 0, "case FacadeErrorKind.NotFound not found in MarkAsReleased");

        var breakEnd = method.IndexOf("break;", groupStart, StringComparison.Ordinal);
        Assert.True(breakEnd > groupStart, "No terminating break after the plain-error case group");

        var group = method.Substring(groupStart, breakEnd - groupStart);
        Assert.Contains("case FacadeErrorKind.BadRequest when failure.Errors.Length == 0:", group);
        Assert.Contains("case FacadeErrorKind.Conflict:", group);
        Assert.Contains("_releaseError = failure.Error;", group);

        // A plain error must NOT touch the validation list, the DTO result, or the Detail text.
        Assert.DoesNotContain("_validationErrors", group);
        Assert.DoesNotContain("failure.Detail", group);
        Assert.DoesNotContain("_releaseResult", group);
        Assert.DoesNotContain("_executionFailed", group);

        // The guard splits the BadRequest kind: the guarded label must come BEFORE the
        // unguarded validation case so a validation failure never lands in _releaseError.
        var guardIndex = method.IndexOf("case FacadeErrorKind.BadRequest when", StringComparison.Ordinal);
        var unguardedIndex = method.IndexOf("case FacadeErrorKind.BadRequest:", StringComparison.Ordinal);
        Assert.True(guardIndex >= 0 && unguardedIndex > guardIndex,
            "The guarded BadRequest case must precede the unguarded (validation) case");
    }

    /// <summary>
    /// The BadRequest branch is SPLIT: an outcome with no per-error list is a plain
    /// <c>Error</c> failure (<c>_releaseError</c>), while an outcome WITH a non-empty
    /// <c>Errors</c> list is a validation failure (<c>_validationErrors</c>, per-error) that
    /// also clears <c>_isValid</c>.
    /// </summary>
    [Fact]
    public void ReleaseDetail_MarkAsReleased_BadRequest_SplitsPlainErrorFromValidation()
    {
        var method = ExtractMethodSource(ReadReleaseDetailSource(), "private async Task MarkAsReleased()");

        // The validation branch is the guard-LESS BadRequest case: extract from its label to
        // the terminating break so the assertions cover exactly that branch's body.
        var labelLine = method.Split('\n')
            .Select(line => line.Trim())
            .Single(line => line.StartsWith("case FacadeErrorKind.BadRequest:", StringComparison.Ordinal));
        var labelIndex = method.IndexOf(labelLine, StringComparison.Ordinal);
        var breakEnd = method.IndexOf("break;", labelIndex, StringComparison.Ordinal);
        Assert.True(breakEnd > labelIndex, "No terminating break after the validation branch");
        var branch = method.Substring(labelIndex, breakEnd - labelIndex);

        Assert.Contains("_validationErrors = failure.Errors.Select(e => e ?? \"\").ToList();", branch);
        Assert.Contains("_isValid = false;", branch);
        Assert.DoesNotContain("_releaseError", branch);

        // The guarded (plain-error) BadRequest case must precede the unguarded one so a
        // validation failure never lands in _releaseError — and both exist.
        var guardIndex = method.IndexOf("case FacadeErrorKind.BadRequest when", StringComparison.Ordinal);
        var unguardedIndex = method.IndexOf("case FacadeErrorKind.BadRequest:", StringComparison.Ordinal);
        Assert.True(guardIndex >= 0 && unguardedIndex > guardIndex,
            "The guarded BadRequest case must precede the unguarded (validation) case");
    }

    /// <summary>
    /// An <c>Internal</c> (execution) failure maps to ALL THREE state assignments:
    /// <c>_releaseResult</c> is constructed as the exact
    /// <see cref="ReleaseExecutionResultDto"/>(Success: false, Results: outcome.Results,
    /// Error: outcome.Detail, Failure: ReleaseExecutionFailure.Execution), <c>_releaseError</c>
    /// is <c>outcome.Detail ?? "Release failed."</c>, and <c>_executionFailed</c> is true.
    /// </summary>
    [Fact]
    public void ReleaseDetail_MarkAsReleased_ExecutionFailure_TripleAssignment()
    {
        var method = ExtractMethodSource(ReadReleaseDetailSource(), "private async Task MarkAsReleased()");
        var branch = ExtractStatusFailureBranch(method, "Internal");

        // The EXACT DTO construction with named arguments and exact values.
        Assert.Contains(
            "_releaseResult = new ReleaseExecutionResultDto(",
            branch);
        Assert.Contains("Success: false,", branch);
        Assert.Contains("Results: failure.Results,", branch);
        Assert.Contains("Error: failure.Detail,", branch);
        Assert.Contains("Failure: ReleaseExecutionFailure.Execution)", branch);

        Assert.Contains("_releaseError = failure.Detail ?? \"Release failed.\";", branch);
        Assert.Contains("_executionFailed = true;", branch);

        // No sibling branch may carry the execution mapping.
        var otherBranches = Regex.Replace(method, @"case FacadeErrorKind\.Internal[\s\S]*?(?=case FacadeErrorKind\.|default:)", string.Empty);
        Assert.DoesNotContain("ReleaseExecutionFailure.Execution", otherBranches);
        Assert.DoesNotContain("_executionFailed = true", otherBranches);
    }

    /// <summary>
    /// A <c>ServiceUnavailable</c> (503) failure maps ONLY to <c>_releaseError</c> with the
    /// outcome's <c>Detail</c> text — never to <c>_releaseResult</c> or
    /// <c>_executionFailed</c>.
    /// </summary>
    [Fact]
    public void ReleaseDetail_MarkAsReleased_ServiceUnavailable_AssignsReleaseErrorFromDetail()
    {
        var method = ExtractMethodSource(ReadReleaseDetailSource(), "private async Task MarkAsReleased()");
        var branch = ExtractStatusFailureBranch(method, "ServiceUnavailable");
        Assert.Contains("_releaseError = failure.Detail;", branch);
        Assert.DoesNotContain("_releaseResult", branch);
        Assert.DoesNotContain("_executionFailed", branch);
        Assert.DoesNotContain("_validationErrors", branch);
    }

    /// <summary>
    /// The unexpected-outcome default cases THROW (no silent fallback): both the unexpected
    /// <see cref="StatusFailureOutcome"/> kind and the unexpected outcome type surface
    /// <see cref="InvalidOperationException"/> with a message naming what was unexpected —
    /// never a generic fallback.
    /// </summary>
    [Fact]
    public void ReleaseDetail_MarkAsReleased_Defaults_ThrowOnUnexpectedOutcome()
    {
        var method = ExtractMethodSource(ReadReleaseDetailSource(), "private async Task MarkAsReleased()");
        Assert.Contains(
            "throw new InvalidOperationException($\"Unexpected release status failure kind: {failure.Kind}.\");",
            method);
        Assert.Contains(
            "throw new InvalidOperationException($\"Unexpected release status outcome: {outcome.GetType().Name}.\");",
            method);
    }

    /// <summary>
    /// Success outcomes replace <c>_release</c> with the returned DTO: a
    /// <see cref="PlanningNoOpOutcome"/> replaces it with the no-op release and an
    /// <see cref="ExecutionSuccessOutcome"/> replaces it AND populates <c>_releaseResult</c>
    /// with the execution result.
    /// </summary>
    [Fact]
    public void ReleaseDetail_MarkAsReleased_SuccessOutcomes_ReplaceReleaseState()
    {
        var method = ExtractMethodSource(ReadReleaseDetailSource(), "private async Task MarkAsReleased()");
        Assert.Contains("case PlanningNoOpOutcome noOp:", method);
        Assert.Contains("_release = noOp.Release;", method);
        Assert.Contains("case ExecutionSuccessOutcome success:", method);
        Assert.Contains("_release = success.Release;", method);
        Assert.Contains("_releaseResult = success.Result;", method);
    }

    // ── 3. validate generic message ──────────────────────────────────────────

    /// <summary>
    /// A failed validate result surfaces EXACTLY the generic "Validation request failed."
    /// message in <c>_validationErrors</c> — NOT the facade's <c>result.Error</c> text. Both
    /// the unsuccessful-result branch AND the catch branch carry it.
    /// </summary>
    [Fact]
    public void ReleaseDetail_Validate_FailedResult_SurfacesGenericMessageNotFacadeError()
    {
        var method = ExtractMethodSource(ReadReleaseDetailSource(), "private async Task ValidateReleaseAsync()");
        Assert.Contains(
            "_validationErrors = [\"Validation request failed.\"];",
            method);
        Assert.Equal(2, Regex.Matches(method, Regex.Escape("_validationErrors = [\"Validation request failed.\"];")).Count);
        Assert.DoesNotContain("result.Error", method);
        Assert.DoesNotContain("_isValid = true", method);
    }

    /// <summary>
    /// A successful validate result projects the facade's <see cref="ValidationDto"/> onto the
    /// page state: <c>_isValid</c> from <c>Valid</c>, <c>_validationErrors</c> from
    /// <c>Errors</c>.
    /// </summary>
    [Fact]
    public void ReleaseDetail_Validate_Success_ProjectsValidAndErrors()
    {
        var method = ExtractMethodSource(ReadReleaseDetailSource(), "private async Task ValidateReleaseAsync()");
        Assert.Contains("_isValid = result.Value.Valid;", method);
        Assert.Contains("_validationErrors = result.Value.Errors.ToList();", method);
        Assert.Contains("ReleaseFacade.ValidateReleaseAsync(", method);
    }

    // ── 4. inline error fields (each separately) ─────────────────────────────

    /// <summary>
    /// Each editor's failure populates ITS OWN inline error field with the facade's inner
    /// error text — one failing field must not satisfy another.
    /// </summary>
    [Theory]
    [InlineData("private async Task SaveNotes()", "_notesError", "UpdateReleaseNotesAsync(")]
    [InlineData("private async Task SaveTag()", "_tagError", "UpdateReleaseTagAsync(")]
    [InlineData("private async Task SaveRepositories()", "_repositoriesError", "UpdateReleaseRepositoriesAsync(")]
    public void ReleaseDetail_SaveHandlers_PopulateTheirOwnInlineErrorField(
        string signature, string errorField, string facadeMethod)
    {
        var method = ExtractMethodSource(ReadReleaseDetailSource(), signature);
        Assert.Contains($"ReleaseFacade.{facadeMethod}", method);
        Assert.Contains($"{errorField} = null;", method);
        Assert.Contains($"{errorField} = result.Error ?? \"\";", method);

        // No sibling editor's error field may be assigned in this handler.
        foreach (var other in new[] { "_notesError", "_tagError", "_repositoriesError", "_deleteError" })
        {
            if (other != errorField)
                Assert.DoesNotContain($"{other} =", method);
        }
    }

    /// <summary>
    /// A delete failure populates <c>_deleteError</c> with the facade's inner error text —
    /// scoped to the delete handler so a sibling editor's error assignment cannot satisfy it.
    /// </summary>
    [Fact]
    public void ReleaseDetail_Delete_Failure_PopulatesDeleteErrorWithFacadeError()
    {
        var method = ExtractMethodSource(ReadReleaseDetailSource(), "private async Task DeleteReleaseAsync()");
        Assert.Contains("ReleaseFacade.DeleteReleaseAsync(", method);
        Assert.Contains("_deleteError = result.Error ?? \"\";", method);
        Assert.DoesNotContain("_notesError", method);
        Assert.DoesNotContain("_tagError", method);
        Assert.DoesNotContain("_repositoriesError", method);
    }

    // ── 5. DTO state replacement ─────────────────────────────────────────────

    /// <summary>
    /// <c>_release</c> is initialized from the domain <see cref="Release"/> via an explicit
    /// mapping: the page calls <c>ReleaseDto.From(release)</c> on the store-loaded entity, and
    /// the field itself is declared as <see cref="ReleaseDto"/>.
    /// </summary>
    [Fact]
    public void ReleaseDetail_InitialState_MapsDomainReleaseToDto()
    {
        var source = ReadReleaseDetailSource();
        Assert.Contains("private ReleaseDto? _release;", source);
        Assert.Contains("_release = ReleaseDto.From(release);", source);
        Assert.Contains("var release = await GoalStore.GetReleaseAsync(decoded);", source);
    }

    /// <summary>
    /// <c>_release</c> is replaced with the returned <see cref="ReleaseDto"/> after each
    /// SUCCESSFUL notes, tag, and repositories operation — scoped per handler so removing the
    /// replacement in any one of them fails.
    /// </summary>
    [Theory]
    [InlineData("private async Task SaveNotes()")]
    [InlineData("private async Task SaveTag()")]
    [InlineData("private async Task SaveRepositories()")]
    public void ReleaseDetail_SuccessfulMutation_ReplacesReleaseWithReturnedDto(string signature)
    {
        var method = ExtractMethodSource(ReadReleaseDetailSource(), signature);
        Assert.Contains("if (result.Success && result.Value is not null)", method);
        Assert.Contains("_release = result.Value;", method);
        Assert.Contains("StateHasChanged();", method);
    }

    // ── 6. delete navigation and exception formatting ────────────────────────

    /// <summary>
    /// Delete success navigates away to the releases list; a thrown exception keeps the
    /// "Failed to delete release: …" formatting.
    /// </summary>
    [Fact]
    public void ReleaseDetail_Delete_SuccessNavigates_ExceptionKeepsFormatting()
    {
        var method = ExtractMethodSource(ReadReleaseDetailSource(), "private async Task DeleteReleaseAsync()");
        Assert.Contains("if (result.Success)", method);
        Assert.Contains("Nav.NavigateTo(\"/releases\");", method);
        Assert.Contains("_deleteError = $\"Failed to delete release: {ex.Message}\";", method);
        // A thrown exception must NOT silently become the plain facade-error text.
        Assert.DoesNotContain("_deleteError = ex.Message;", method);
    }

    // ── 7. Releases.razor mapper and create-success behavior ────────────────

    /// <summary>
    /// The created <see cref="ReleaseDto"/> maps back to a domain <see cref="Release"/>
    /// covering ALL EIGHT fields — Id, Tag, Status, Notes, CreatedAt, ReleasedAt,
    /// RepositoryNames, ExecutionState.
    /// </summary>
    [Fact]
    public void ReleasesPage_ToDomainRelease_MapsAllEightFields()
    {
        var method = ExtractMethodSource(ReadReleasesSource(), "private static Release ToDomainRelease(ReleaseDto dto)");
        Assert.Contains("Id = dto.Id,", method);
        Assert.Contains("Tag = dto.Tag,", method);
        Assert.Contains("Status = dto.Status,", method);
        Assert.Contains("Notes = dto.Notes,", method);
        Assert.Contains("CreatedAt = dto.CreatedAt,", method);
        Assert.Contains("ReleasedAt = dto.ReleasedAt,", method);
        Assert.Contains("RepositoryNames = dto.RepositoryNames.ToList(),", method);
        Assert.Contains("ExecutionState = dto.ExecutionState,", method);
    }

    /// <summary>
    /// The create-success path inserts the mapped domain release into the in-memory list,
    /// clears and closes the form, reapplies filters — and does NOT navigate.
    /// </summary>
    [Fact]
    public void ReleasesPage_CreateSuccess_InsertsClearsClosesReapplies_NoNavigation()
    {
        var method = ExtractMethodSource(ReadReleasesSource(), "private async Task CreateRelease()");
        Assert.Contains("var created = ToDomainRelease(result.Value);", method);
        Assert.Contains("_allReleases.Insert(0, created);", method);
        Assert.Contains("_createVersion = \"\";", method);
        Assert.Contains("_createRepo = \"\";", method);
        Assert.Contains("_showCreateForm = false;", method);
        Assert.Contains("ApplyFilters();", method);
        Assert.DoesNotContain("Nav.NavigateTo", method);

        // Navigation exists ONLY in the card click handler — the create path must not share it.
        var nav = ExtractMethodSource(ReadReleasesSource(), "private void NavigateToRelease(string releaseId)");
        Assert.Contains("Nav.NavigateTo(", nav);
    }

    /// <summary>
    /// The create-call itself goes through the facade with the exact
    /// <see cref="CreateReleaseRequest"/>(version, repository) shape, and a failure surfaces
    /// the facade's inner error text in <c>_createError</c>.
    /// </summary>
    [Fact]
    public void ReleasesPage_CreateRelease_CallsFacadeAndSurfacesError()
    {
        var method = ExtractMethodSource(ReadReleasesSource(), "private async Task CreateRelease()");
        Assert.Contains("ReleaseFacade.CreateReleaseAsync(new CreateReleaseRequest(_createVersion, _createRepo))", method);
        Assert.Contains("_createError = result.Error ?? \"\";", method);
        Assert.DoesNotContain("Nav.NavigateTo", method);
    }
}
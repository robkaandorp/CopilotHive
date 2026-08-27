using CopilotHive.Goals;

namespace CopilotHive.Tests;

/// <summary>
/// Tests for the "Extend Iterations" button and <c>ExtendIterationsAsync</c> handler in
/// <c>GoalDetail.razor</c>. Because the project does not use bUnit, these tests use two
/// removal-proof strategies:
/// <list type="bullet">
///   <item>
///     Source-file content assertions that read the actual <c>GoalDetail.razor</c> source and
///     verify the required markup, state fields, endpoint URL, request body, status-code handling,
///     success refresh, and <c>finally</c> reset are present. Deleting or regressing any of these
///     production elements fails the corresponding test.
///   </item>
///   <item>
///     Helper-logic tests that mirror the button's visibility condition and text computation with
///     specific expected values (like <c>GoalDetailReviewBadgeTests</c>).
///   </item>
/// </list>
/// </summary>
public sealed class GoalDetailExtendButtonTests
{
    // ── source-file access ───────────────────────────────────────────────────

    /// <summary>
    /// Reads the actual <c>GoalDetail.razor</c> source by walking up from the current directory
    /// to the repo root (identified by the presence of a <c>*.slnx</c> file), mirroring the
    /// pattern used in <c>DistributedBrainTests</c>.
    /// </summary>
    private static string ReadGoalDetailRazorSource()
    {
        var repoRoot = Environment.CurrentDirectory;
        while (repoRoot != null && !Directory.GetFiles(repoRoot, "*.slnx").Any())
        {
            repoRoot = Directory.GetParent(repoRoot)?.FullName;
        }
        Assert.NotNull(repoRoot);

        var razorPath = Path.Combine(repoRoot, "src", "CopilotHive", "Components", "Pages", "GoalDetail.razor");
        Assert.True(File.Exists(razorPath), $"Source file not found at {razorPath}");
        return File.ReadAllText(razorPath);
    }

    /// <summary>
    /// Extracts the source text of the <c>ExtendIterationsAsync</c> method so assertions target the
    /// method body rather than the whole file. This keeps the "handles X within the extend method"
    /// assertions honest.
    /// </summary>
    private static string ExtractExtendMethodSource(string source)
    {
        const string marker = "private async Task ExtendIterationsAsync()";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "ExtendIterationsAsync method not found in GoalDetail.razor");

        // Find the opening brace and then match braces to capture the full method body.
        var braceStart = source.IndexOf('{', start);
        Assert.True(braceStart >= 0, "Opening brace for ExtendIterationsAsync not found");

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

        Assert.Fail("Could not find matching closing brace for ExtendIterationsAsync");
        return string.Empty; // unreachable
    }

    /// <summary>
    /// Extracts the source text of a method by signature so assertions target the method body
    /// rather than the whole file (a whole-file search can be satisfied by an unrelated method
    /// or a comment).
    /// </summary>
    private static string ExtractMethodSource(string source, string methodSignature)
    {
        var start = source.IndexOf(methodSignature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method '{methodSignature}' not found in GoalDetail.razor");

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

    /// <summary>
    /// Extracts the source text of the extend-iterations button block (the <c>@if</c> that guards
    /// the button markup) so markup assertions target that block specifically.
    /// </summary>
    private static string ExtractExtendButtonBlock(string source)
    {
        const string marker = "@* Extend Iterations — Failed with iteration exhaustion *@";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Extend Iterations markup block comment not found in GoalDetail.razor");

        // Capture a generous slice through the closing of the guarded block.
        var end = source.IndexOf("_extendError", start, StringComparison.Ordinal);
        Assert.True(end >= 0, "Extend button block does not contain expected error markup");
        // Extend to end of that line's containing block.
        var blockEnd = source.IndexOf('}', source.IndexOf('}', end) + 1);
        if (blockEnd < 0) blockEnd = Math.Min(source.Length, start + 1200);
        return source.Substring(start, blockEnd - start);
    }

    // ── markup source assertions (removal-proof) ─────────────────────────────

    [Fact]
    public void ExtendButton_Markup_ContainsVisibilityConditionForDetailFailureReason()
    {
        var source = ReadGoalDetailRazorSource();
        Assert.Contains(
            "_detail?.FailureReason?.Contains(\"iteration\", StringComparison.OrdinalIgnoreCase)",
            source);
    }

    [Fact]
    public void ExtendButton_Markup_ContainsVisibilityConditionForStoredGoalFailureReason()
    {
        var source = ReadGoalDetailRazorSource();
        Assert.Contains(
            "_storedGoal?.FailureReason?.Contains(\"iteration\", StringComparison.OrdinalIgnoreCase)",
            source);
    }

    [Fact]
    public void ExtendButton_Markup_ContainsFailedStatusCheck()
    {
        var source = ReadGoalDetailRazorSource();
        var block = ExtractExtendButtonBlock(source);
        // The Failed status check must live inside the extend button block, next to the
        // FailureReason iteration check — not merely somewhere in the file.
        Assert.Contains("FailureReason", block);
        Assert.Contains("GoalStatus.Failed", block);
    }

    [Fact]
    public void ExtendButton_Markup_ContainsExtendingIterationsLoadingState()
    {
        var source = ReadGoalDetailRazorSource();
        var block = ExtractExtendButtonBlock(source);
        Assert.Contains("_extendingIterations", block);
        Assert.Contains("Extending...", block);
    }

    [Fact]
    public void ExtendButton_Markup_ContainsExtendErrorDisplay()
    {
        var source = ReadGoalDetailRazorSource();
        var block = ExtractExtendButtonBlock(source);
        Assert.Contains("_extendError", block);
    }

    [Fact]
    public void ExtendButton_Markup_ContainsApproveBtnClass()
    {
        var source = ReadGoalDetailRazorSource();
        var block = ExtractExtendButtonBlock(source);
        Assert.Contains("approve-btn", block);
    }

    // ── ExtendIterationsAsync method source assertions (removal-proof) ────────

    [Fact]
    public void ExtendIterationsAsync_CallsTheGoalFacade()
    {
        var method = ExtractExtendMethodSource(ReadGoalDetailRazorSource());
        // REMOVAL-PROOF: the extend goes through IGoalFacade.ExtendIterationsAsync. A revert to
        // an HttpClient POST (or deletion of the facade call) fails these assertions.
        Assert.Contains("GoalFacade.ExtendIterationsAsync(GoalId, 5", method);
        Assert.DoesNotContain("HttpClient", method);
        Assert.DoesNotContain("PostAsync", method);
        Assert.DoesNotContain("/api/goals/", method);
    }

    [Fact]
    public void ExtendIterationsAsync_SendsAdditionalIterationsFive()
    {
        var method = ExtractExtendMethodSource(ReadGoalDetailRazorSource());
        // The +5 budget is part of the button's contract and is now the facade argument.
        Assert.Contains("ExtendIterationsAsync(GoalId, 5", method);
    }

    [Fact]
    public void ExtendIterationsAsync_HandlesNotFound()
    {
        var method = ExtractExtendMethodSource(ReadGoalDetailRazorSource());
        // The dedicated not-found message is keyed off the facade's error kind, not an HTTP status.
        Assert.Contains("FacadeErrorKind.NotFound", method);
        Assert.Contains("Goal or pipeline not found", method);
        Assert.DoesNotContain("HttpStatusCode", method);
    }

    [Fact]
    public void ExtendIterationsAsync_HandlesOtherFailures()
    {
        var method = ExtractExtendMethodSource(ReadGoalDetailRazorSource());
        // Non-success, non-NotFound results surface the facade's inner error text directly —
        // no HTTP-status prefix and no JSON envelope.
        Assert.Contains("else", method);
        Assert.Contains("_extendError = result.Error", method);
        Assert.DoesNotContain("ReadAsStringAsync", method);
        Assert.DoesNotContain("Error {(int)response.StatusCode}", method);
    }

    [Fact]
    public void ExtendIterationsAsync_RefreshesOnSuccess()
    {
        var method = ExtractExtendMethodSource(ReadGoalDetailRazorSource());
        // The success branch must refresh; ensure RefreshAsync is called after the facade call.
        var callIndex = method.IndexOf("GoalFacade.ExtendIterationsAsync", StringComparison.Ordinal);
        var refreshIndex = method.IndexOf("RefreshAsync()", StringComparison.Ordinal);
        Assert.True(callIndex >= 0, "The extend must go through the facade");
        Assert.True(refreshIndex > callIndex,
            "RefreshAsync() should be called after the facade call in the success branch");
        Assert.Contains("result.Success", method);
    }

    [Fact]
    public void ExtendIterationsAsync_HasFinallyReset()
    {
        var method = ExtractExtendMethodSource(ReadGoalDetailRazorSource());
        var finallyIndex = method.IndexOf("finally", StringComparison.Ordinal);
        Assert.True(finallyIndex >= 0, "ExtendIterationsAsync must have a finally block");
        var reset = method.IndexOf("_extendingIterations = false", finallyIndex, StringComparison.Ordinal);
        Assert.True(reset > finallyIndex,
            "_extendingIterations must be reset to false within the finally block");
    }

    // ── helper-logic tests mirroring GoalDetail.razor conditions ─────────────

    /// <summary>
    /// Mirrors the <c>GoalDetail.razor</c> visibility condition for the Extend Iterations button.
    /// The button is visible only when the goal is <see cref="GoalStatus.Failed"/> and its failure
    /// reason contains "iteration" (case-insensitive).
    /// </summary>
    private static bool IsExtendButtonVisible(GoalStatus? status, string? failureReason) =>
        (failureReason?.Contains("iteration", StringComparison.OrdinalIgnoreCase) == true)
        && status == GoalStatus.Failed;

    [Fact]
    public void Visibility_FailedWithIterationReason_ReturnsTrue() =>
        Assert.True(IsExtendButtonVisible(GoalStatus.Failed, "Exceeded max iterations"));

    [Fact]
    public void Visibility_FailedWithOtherReason_ReturnsFalse() =>
        Assert.False(IsExtendButtonVisible(GoalStatus.Failed, "merge conflict"));

    [Fact]
    public void Visibility_CompletedWithIterationReason_ReturnsFalse() =>
        Assert.False(IsExtendButtonVisible(GoalStatus.Completed, "Exceeded max iterations"));

    [Fact]
    public void Visibility_FailedWithNullFailureReason_ReturnsFalse() =>
        Assert.False(IsExtendButtonVisible(GoalStatus.Failed, null));

    [Fact]
    public void Visibility_FailedWithUppercaseIteration_ReturnsTrue() =>
        Assert.True(IsExtendButtonVisible(GoalStatus.Failed, "ITERATION exhausted"));

    [Fact]
    public void Visibility_NullStatusWithIterationReason_ReturnsFalse() =>
        Assert.False(IsExtendButtonVisible(null, "iteration"));

    /// <summary>
    /// Mirrors the button-text computation in <c>GoalDetail.razor</c>:
    /// <c>_extendingIterations ? "Extending..." : "➕ Extend Iterations (+5)"</c>.
    /// </summary>
    private static string GetExtendButtonText(bool isExtending) =>
        isExtending ? "Extending..." : "➕ Extend Iterations (+5)";

    [Fact]
    public void ButtonText_Extending_ReturnsExtendingText() =>
        Assert.Equal("Extending...", GetExtendButtonText(true));

    [Fact]
    public void ButtonText_NotExtending_ReturnsDefaultText() =>
        Assert.Equal("➕ Extend Iterations (+5)", GetExtendButtonText(false));

    // ── Linked Issues backlink (source-file assertions, removal-proof) ──────

    [Fact]
    public void LinkedIssues_Markup_ContainsLinkedIssuesCard()
    {
        var source = ReadGoalDetailRazorSource();
        Assert.Contains("Linked Issues", source);
        Assert.Contains("_linkedIssues.Count > 0", source);
        Assert.Contains("IssueStatusBadge(issue.Status)", source);
        Assert.Contains("href=\"/issues\"", source);
    }

    [Fact]
    public void LinkedIssues_Markup_IsPlacedAfterLinkedDocuments()
    {
        var source = ReadGoalDetailRazorSource();
        var docsIndex = source.IndexOf("Linked Documents", StringComparison.Ordinal);
        var issuesIndex = source.IndexOf("Linked Issues", StringComparison.Ordinal);
        Assert.True(docsIndex >= 0, "Linked Documents card not found");
        Assert.True(issuesIndex > docsIndex,
            "Linked Issues card must appear after the Linked Documents card");
    }

    [Fact]
    public void LinkedIssues_RefreshAsync_ReadsThroughTheGoalFacade()
    {
        var refresh = ExtractMethodSource(ReadGoalDetailRazorSource(), "private async Task RefreshAsync()");
        // REMOVAL-PROOF: the linked-issues read goes through the facade's single
        // GetLinkedIssuesAsync call — which performs BOTH store queries and deduplicates —
        // instead of two HTTP GETs against /api/issues. A revert fails these assertions.
        Assert.Contains("GoalFacade.GetLinkedIssuesAsync(GoalId", refresh);
        Assert.DoesNotContain("HttpClient", refresh);
        Assert.DoesNotContain("GetFromJsonAsync", refresh);
        Assert.DoesNotContain("/api/issues", refresh);
        Assert.DoesNotContain("source_goal_id", refresh);
        Assert.DoesNotContain("linked_goal_id", refresh);
    }

    [Fact]
    public void LinkedIssues_RefreshAsync_FallsBackToEmptyOnFailure()
    {
        var refresh = ExtractMethodSource(ReadGoalDetailRazorSource(), "private async Task RefreshAsync()");
        // A failed read leaves the card empty rather than throwing out of the refresh.
        Assert.Contains("linkedIssuesResult.Success", refresh);
        Assert.Contains("_linkedIssues =", refresh);
    }

    [Fact]
    public void LinkedIssues_UsesSharedLinkedIssueDto()
    {
        var source = ReadGoalDetailRazorSource();
        // The page must NOT reintroduce its own private issue DTO — it consumes the shared
        // LinkedIssueDto, which carries the FULL issues-API shape.
        Assert.DoesNotContain("record LinkedIssueInfo", source);
        Assert.Contains("List<LinkedIssueDto> _linkedIssues = []", source);
        Assert.NotNull(typeof(CopilotHive.Services.LinkedIssueDto).GetProperty("Id"));
        Assert.NotNull(typeof(CopilotHive.Services.LinkedIssueDto).GetProperty("LinkedGoalId"));
    }

    [Fact]
    public void LinkedIssues_HasStatusBadgeHelper()
    {
        var source = ReadGoalDetailRazorSource();
        Assert.Contains("IssueStatusBadge(IssueStatus s)", source);
        Assert.Contains("IssueStatus.Open => \"badge-yellow\"", source);
        Assert.Contains("IssueStatus.Resolved => \"badge-green\"", source);
        Assert.Contains("IssueStatus.Closed => \"badge-muted\"", source);
    }

    // ── Page-level facade migration proofs (removal-proof) ──────────────────

    /// <summary>
    /// Extracts the directive header of the Razor page — everything before the first markup or
    /// code element — so the injection assertions cannot be satisfied by a comment or a string
    /// literal deeper in the file.
    /// </summary>
    private static string ExtractDirectiveHeader(string source)
    {
        var lines = source.Split('\n');
        var header = new List<string>();
        foreach (var line in lines)
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
    /// REMOVAL-PROOF: <c>GoalDetail.razor</c> injects <c>IGoalFacade</c> and no longer injects
    /// an <c>HttpClient</c>. Reverting the page to HttpClient fails this test.
    /// </summary>
    [Fact]
    public void GoalDetail_InjectsGoalFacadeAndNotHttpClient()
    {
        var header = ExtractDirectiveHeader(ReadGoalDetailRazorSource());
        Assert.Contains("@inject IGoalFacade GoalFacade", header);
        Assert.DoesNotContain("@inject HttpClient", header);
    }

    /// <summary>
    /// No handler anywhere in <c>GoalDetail.razor</c> may reach for an <c>HttpClient</c> or a
    /// goal/issue REST route — every one of the six goal operations plus the linked-issues read
    /// goes through the facade.
    /// </summary>
    [Fact]
    public void GoalDetail_ContainsNoHttpClientUsageAtAll()
    {
        var source = ReadGoalDetailRazorSource();
        Assert.DoesNotContain("HttpClient", source);
        Assert.DoesNotContain("/api/goals/", source);
        Assert.DoesNotContain("/api/issues", source);
    }

    /// <summary>
    /// Each of the remaining five goal operations calls its facade method. Assertions are scoped
    /// to the individual method bodies so deleting the facade call from ONE handler fails —
    /// another handler's call cannot satisfy it.
    /// </summary>
    [Theory]
    [InlineData("private async Task ApproveGoal()", "GoalFacade.UpdateGoalStatusAsync(GoalId, \"Pending\")")]
    [InlineData("private async Task RevertToDraft()", "GoalFacade.UpdateGoalStatusAsync(GoalId, \"Draft\")")]
    [InlineData("private async Task ReviewGoal()", "GoalFacade.RequestReviewAsync(GoalId")]
    [InlineData("private async Task CancelGoal()", "GoalFacade.CancelGoalAsync(GoalId)")]
    [InlineData("private async Task DeleteCurrentGoal()", "GoalFacade.DeleteGoalAsync(GoalId)")]
    [InlineData("private async Task AssignToRelease()", "GoalFacade.AttachReleaseAsync(GoalId")]
    public void GoalDetail_Handler_CallsItsFacadeMethod(string signature, string expectedCall)
    {
        var method = ExtractMethodSource(ReadGoalDetailRazorSource(), signature);
        Assert.Contains(expectedCall, method);
        Assert.DoesNotContain("HttpClient", method);
        Assert.DoesNotContain("Error {(int)response.StatusCode}", method);
    }

    /// <summary>
    /// The established error-formatting simplification: failures surface the facade's inner
    /// error text, with no HTTP-status prefix and no JSON envelope. Scoped per handler so a
    /// single regressed handler fails.
    /// </summary>
    [Theory]
    [InlineData("private async Task ApproveGoal()", "_approveError = result.Error")]
    [InlineData("private async Task RevertToDraft()", "_revertError = result.Error")]
    [InlineData("private async Task ReviewGoal()", "_reviewError = result.Error")]
    [InlineData("private async Task CancelGoal()", "_cancelError = result.Error")]
    [InlineData("private async Task DeleteCurrentGoal()", "_deleteError = result.Error")]
    [InlineData("private async Task AssignToRelease()", "_releaseAssignError = result.Error")]
    public void GoalDetail_Handler_SurfacesTheFacadeErrorDirectly(string signature, string expectedAssignment)
    {
        var method = ExtractMethodSource(ReadGoalDetailRazorSource(), signature);
        Assert.Contains(expectedAssignment, method);
        Assert.DoesNotContain("ReadAsStringAsync", method);
    }

    /// <summary>
    /// Existing UI behaviour is preserved through the migration: delete still navigates back to
    /// the goals list on success, and cancel/delete still confirm first.
    /// </summary>
    [Fact]
    public void GoalDetail_DeleteAndCancel_PreserveConfirmAndNavigation()
    {
        var source = ReadGoalDetailRazorSource();

        var delete = ExtractMethodSource(source, "private async Task DeleteCurrentGoal()");
        Assert.Contains("JS.InvokeAsync<bool>(\"confirm\"", delete);
        Assert.Contains("Nav.NavigateTo(\"/goals\")", delete);

        var cancel = ExtractMethodSource(source, "private async Task CancelGoal()");
        Assert.Contains("JS.InvokeAsync<bool>(\"confirm\"", cancel);
        Assert.Contains("RefreshAsync()", cancel);
    }

    /// <summary>
    /// REMOVAL-PROOF for the other migrated page: <c>Goals.razor</c> injects <c>IGoalFacade</c>,
    /// no longer injects an <c>HttpClient</c>, and none of its handlers touch a REST route.
    /// </summary>
    [Fact]
    public void GoalsPage_InjectsGoalFacadeAndNotHttpClient()
    {
        var source = ReadGoalsRazorSource();
        var header = ExtractDirectiveHeader(source);
        Assert.Contains("@inject IGoalFacade GoalFacade", header);
        Assert.DoesNotContain("@inject HttpClient", header);
        Assert.DoesNotContain("HttpClient", source);
        Assert.DoesNotContain("/api/goals", source);
    }

    /// <summary>
    /// Each <c>Goals.razor</c> handler calls its facade method, scoped to the handler body so a
    /// single reverted handler fails.
    /// </summary>
    [Theory]
    [InlineData("private async Task DeleteGoal(string goalId)", "GoalFacade.DeleteGoalAsync(goalId)")]
    [InlineData("private async Task ApproveGoal(string goalId)", "GoalFacade.UpdateGoalStatusAsync(goalId, \"Pending\")")]
    [InlineData("private async Task RevertToDraft(string goalId)", "GoalFacade.UpdateGoalStatusAsync(goalId, \"Draft\")")]
    [InlineData("private async Task RetryFailedGoal(string goalId)", "GoalFacade.UpdateGoalStatusAsync(goalId, \"Draft\")")]
    public void GoalsPage_Handler_CallsItsFacadeMethod(string signature, string expectedCall)
    {
        var method = ExtractMethodSource(ReadGoalsRazorSource(), signature);
        Assert.Contains(expectedCall, method);
        Assert.DoesNotContain("HttpClient", method);
        Assert.DoesNotContain("PatchAsync", method);
        Assert.DoesNotContain("DeleteAsync", method);
    }

    /// <summary>
    /// The three error-carrying <c>Goals.razor</c> handlers surface the facade's inner error
    /// text into their per-goal error dictionaries — no status prefix, no JSON envelope.
    /// </summary>
    [Theory]
    [InlineData("private async Task ApproveGoal(string goalId)", "_approveErrors[goalId] = result.Error")]
    [InlineData("private async Task RevertToDraft(string goalId)", "_revertErrors[goalId] = result.Error")]
    [InlineData("private async Task RetryFailedGoal(string goalId)", "_retryErrors[goalId] = result.Error")]
    public void GoalsPage_Handler_SurfacesTheFacadeErrorDirectly(string signature, string expectedAssignment)
    {
        var method = ExtractMethodSource(ReadGoalsRazorSource(), signature);
        Assert.Contains(expectedAssignment, method);
        Assert.DoesNotContain("ReadAsStringAsync", method);
        Assert.DoesNotContain("Error {(int)response.StatusCode}", method);
    }

    /// <summary>
    /// The retry handler's local-state reset (the fields it clears so a retried goal renders as
    /// a fresh Draft) survives the migration.
    /// </summary>
    [Fact]
    public void GoalsPage_RetryFailedGoal_StillResetsLocalGoalState()
    {
        var method = ExtractMethodSource(ReadGoalsRazorSource(), "private async Task RetryFailedGoal(string goalId)");
        Assert.Contains("goal.Status = GoalStatus.Draft", method);
        Assert.Contains("goal.FailureReason = null", method);
        Assert.Contains("goal.Iterations = 0", method);
        Assert.Contains("goal.StartedAt = null", method);
        Assert.Contains("goal.TotalDurationSeconds = null", method);
        Assert.Contains("goal.IterationSummaries = []", method);
        Assert.Contains("ApplyFilters()", method);
    }

    /// <summary>Reads the actual <c>Goals.razor</c> source from the repo.</summary>
    private static string ReadGoalsRazorSource()
    {
        var repoRoot = Environment.CurrentDirectory;
        while (repoRoot != null && !Directory.GetFiles(repoRoot, "*.slnx").Any())
        {
            repoRoot = Directory.GetParent(repoRoot)?.FullName;
        }
        Assert.NotNull(repoRoot);

        var razorPath = Path.Combine(repoRoot, "src", "CopilotHive", "Components", "Pages", "Goals.razor");
        Assert.True(File.Exists(razorPath), $"Source file not found at {razorPath}");
        return File.ReadAllText(razorPath);
    }
}

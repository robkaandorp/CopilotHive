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
    public void ExtendIterationsAsync_PostsToCorrectEndpoint()
    {
        var method = ExtractExtendMethodSource(ReadGoalDetailRazorSource());
        Assert.Contains("/api/goals/{GoalId}/extend-iterations", method);
        Assert.Contains("PostAsync", method);
    }

    [Fact]
    public void ExtendIterationsAsync_SendsAdditionalIterationsFive()
    {
        var method = ExtractExtendMethodSource(ReadGoalDetailRazorSource());
        Assert.Contains("additionalIterations", method);
        Assert.Contains("additionalIterations = 5", method);
    }

    [Fact]
    public void ExtendIterationsAsync_HandlesNotFound()
    {
        var method = ExtractExtendMethodSource(ReadGoalDetailRazorSource());
        Assert.Contains("HttpStatusCode.NotFound", method);
        Assert.Contains("Goal or pipeline not found", method);
    }

    [Fact]
    public void ExtendIterationsAsync_HandlesBadRequest()
    {
        var method = ExtractExtendMethodSource(ReadGoalDetailRazorSource());
        // Non-success, non-404 responses read the error body and surface it.
        Assert.Contains("else", method);
        Assert.Contains("ReadAsStringAsync", method);
        Assert.Contains("Error {(int)response.StatusCode}", method);
    }

    [Fact]
    public void ExtendIterationsAsync_RefreshesOnSuccess()
    {
        var method = ExtractExtendMethodSource(ReadGoalDetailRazorSource());
        // The success branch must refresh; ensure RefreshAsync is called after the POST.
        var postIndex = method.IndexOf("PostAsync", StringComparison.Ordinal);
        var refreshIndex = method.IndexOf("RefreshAsync()", StringComparison.Ordinal);
        Assert.True(refreshIndex > postIndex,
            "RefreshAsync() should be called after the POST in the success branch");
        Assert.Contains("IsSuccessStatusCode", method);
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
    public void LinkedIssues_RefreshAsync_FetchesSourceAndLinkedQueries()
    {
        var source = ReadGoalDetailRazorSource();
        Assert.Contains("source_goal_id=", source);
        Assert.Contains("linked_goal_id=", source);
        Assert.Contains("Uri.EscapeDataString(GoalId)", source);
        Assert.Contains("GetFromJsonAsync<List<LinkedIssueInfo>>", source);
    }

    [Fact]
    public void LinkedIssues_RefreshAsync_MergesAndDeduplicatesById()
    {
        var source = ReadGoalDetailRazorSource();
        Assert.Contains("sourceIssues.Concat(linkedIssues)", source);
        Assert.Contains("DistinctBy(i => i.Id)", source);
    }

    [Fact]
    public void LinkedIssues_DeclaresRecordAndField()
    {
        var source = ReadGoalDetailRazorSource();
        Assert.Contains("record LinkedIssueInfo(string Id, string Title, IssueType Type, IssueSeverity Severity, IssueStatus Status)", source);
        Assert.Contains("List<LinkedIssueInfo> _linkedIssues = []", source);
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

    // ── Linked Issues merge/deduplication helper-logic tests ────────────────

    /// <summary>
    /// Mirrors the <c>LinkedIssueInfo</c> record declared in <c>GoalDetail.razor</c>.
    /// </summary>
    private sealed record LinkedIssueInfo(string Id, string Title, IssueType Type, IssueSeverity Severity, IssueStatus Status);

    /// <summary>
    /// Mirrors the merge/deduplication logic in <c>GoalDetail.razor</c>:
    /// <c>sourceIssues.Concat(linkedIssues).DistinctBy(i =&gt; i.Id).ToList()</c>.
    /// </summary>
    private static List<LinkedIssueInfo> MergeLinkedIssues(
        IEnumerable<LinkedIssueInfo> sourceIssues,
        IEnumerable<LinkedIssueInfo> linkedIssues) =>
        sourceIssues.Concat(linkedIssues).DistinctBy(i => i.Id).ToList();

    private static LinkedIssueInfo MakeLinkedIssue(string id) =>
        new(id, $"Title {id}", IssueType.Bug, IssueSeverity.Medium, IssueStatus.Open);

    [Fact]
    public void Merge_OnlySourceIssues_ReturnsAll()
    {
        var merged = MergeLinkedIssues(
            [MakeLinkedIssue("a"), MakeLinkedIssue("b")],
            []);
        Assert.Equal(2, merged.Count);
        Assert.Equal(["a", "b"], merged.Select(i => i.Id).ToList());
    }

    [Fact]
    public void Merge_OnlyLinkedIssues_ReturnsAll()
    {
        var merged = MergeLinkedIssues(
            [],
            [MakeLinkedIssue("a"), MakeLinkedIssue("b")]);
        Assert.Equal(2, merged.Count);
        Assert.Equal(["a", "b"], merged.Select(i => i.Id).ToList());
    }

    [Fact]
    public void Merge_IssueInBothQueries_AppearsOnce()
    {
        var merged = MergeLinkedIssues(
            [MakeLinkedIssue("dup"), MakeLinkedIssue("only-source")],
            [MakeLinkedIssue("dup"), MakeLinkedIssue("only-linked")]);
        Assert.Equal(3, merged.Count);
        Assert.Equal(1, merged.Count(i => i.Id == "dup"));
        Assert.Contains(merged, i => i.Id == "only-source");
        Assert.Contains(merged, i => i.Id == "only-linked");
    }

    [Fact]
    public void Merge_NoIssues_ReturnsEmpty()
    {
        var merged = MergeLinkedIssues([], []);
        Assert.Empty(merged);
    }

    [Fact]
    public void Merge_FirstOccurrenceWins_ForDuplicateId()
    {
        // DistinctBy keeps the FIRST occurrence; the source query result wins.
        var source = MakeLinkedIssue("dup");
        var linked = new LinkedIssueInfo("dup", "Different title", IssueType.Suggestion, IssueSeverity.High, IssueStatus.Closed);
        var merged = MergeLinkedIssues([source], [linked]);
        Assert.Single(merged);
        Assert.Equal("Title dup", merged[0].Title);
        Assert.Equal(IssueType.Bug, merged[0].Type);
    }
}

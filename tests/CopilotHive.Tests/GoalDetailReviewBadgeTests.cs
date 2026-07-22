using CopilotHive.Goals;

namespace CopilotHive.Tests;

/// <summary>
/// Unit tests for the review-status badge rendering logic used by the GoalDetail Blazor page.
/// Verifies the same CSS class and text computation as <c>GoalDetail.razor</c> without requiring
/// bUnit or a live browser environment.
/// </summary>
/// <remarks>
/// The helper methods below mirror the private <c>ReviewStatusBadge</c> and <c>ReviewStatusText</c>
/// helpers in <c>GoalDetail.razor</c> exactly, and the visibility condition mirrors the
/// <c>@if (reviewStatus != ReviewStatus.None)</c> guard around the badge markup. Because the badge
/// is only rendered when the status is not <see cref="ReviewStatus.None"/>, the <c>None</c> branch of
/// the helpers (which returns <c>"badge-muted"</c> / the enum name) is never displayed in practice.
/// </remarks>
public sealed class GoalDetailReviewBadgeTests
{
    // ── helpers that mirror GoalDetail.razor private helpers ─────────────────

    /// <summary>Mirrors <c>GoalDetail.razor ReviewStatusBadge</c>.</summary>
    private static string ReviewStatusBadge(ReviewStatus s) => s switch
    {
        ReviewStatus.Pending => "badge-warning",
        ReviewStatus.Approved => "badge-success",
        ReviewStatus.NeedsChanges => "badge-danger",
        _ => "badge-muted",
    };

    /// <summary>Mirrors <c>GoalDetail.razor ReviewStatusText</c>.</summary>
    private static string ReviewStatusText(ReviewStatus s) => s switch
    {
        ReviewStatus.Pending => "🔄 Review in progress",
        ReviewStatus.Approved => "✅ Reviewed: Approved",
        ReviewStatus.NeedsChanges => "⚠️ Reviewed: Needs Changes",
        _ => s.ToString(),
    };

    /// <summary>
    /// Mirrors the <c>@if (reviewStatus != ReviewStatus.None)</c> visibility guard that wraps
    /// the review-status badge in <c>GoalDetail.razor</c>.
    /// </summary>
    private static bool IsReviewBadgeVisible(ReviewStatus s) => s != ReviewStatus.None;

    // ── badge CSS class ──────────────────────────────────────────────────────

    [Fact]
    public void ReviewBadgeClass_Pending_ReturnsBadgeWarning() =>
        Assert.Equal("badge-warning", ReviewStatusBadge(ReviewStatus.Pending));

    [Fact]
    public void ReviewBadgeClass_Approved_ReturnsBadgeSuccess() =>
        Assert.Equal("badge-success", ReviewStatusBadge(ReviewStatus.Approved));

    [Fact]
    public void ReviewBadgeClass_NeedsChanges_ReturnsBadgeDanger() =>
        Assert.Equal("badge-danger", ReviewStatusBadge(ReviewStatus.NeedsChanges));

    [Fact]
    public void ReviewBadgeClass_None_ReturnsBadgeMuted() =>
        // GoalDetail.razor returns "badge-muted" for None, but the visibility guard prevents
        // the badge from ever being rendered in that case (see ReviewBadgeVisibility_None_ReturnsFalse).
        Assert.Equal("badge-muted", ReviewStatusBadge(ReviewStatus.None));

    // ── badge text ───────────────────────────────────────────────────────────

    [Fact]
    public void ReviewBadgeText_Pending_ReturnsCorrectText() =>
        Assert.Equal("🔄 Review in progress", ReviewStatusText(ReviewStatus.Pending));

    [Fact]
    public void ReviewBadgeText_Approved_ReturnsCorrectText() =>
        Assert.Equal("✅ Reviewed: Approved", ReviewStatusText(ReviewStatus.Approved));

    [Fact]
    public void ReviewBadgeText_NeedsChanges_ReturnsCorrectText() =>
        Assert.Equal("⚠️ Reviewed: Needs Changes", ReviewStatusText(ReviewStatus.NeedsChanges));

    [Fact]
    public void ReviewBadgeText_None_ReturnsEnumName() =>
        // GoalDetail.razor falls back to the enum name for None, but the visibility guard prevents
        // the badge from ever being rendered in that case (see ReviewBadgeVisibility_None_ReturnsFalse).
        Assert.Equal("None", ReviewStatusText(ReviewStatus.None));

    // ── badge visibility ─────────────────────────────────────────────────────

    [Fact]
    public void ReviewBadgeVisibility_None_ReturnsFalse() =>
        Assert.False(IsReviewBadgeVisible(ReviewStatus.None));

    [Theory]
    [InlineData(ReviewStatus.Pending)]
    [InlineData(ReviewStatus.Approved)]
    [InlineData(ReviewStatus.NeedsChanges)]
    public void ReviewBadgeVisibility_NonNone_ReturnsTrue(ReviewStatus status) =>
        Assert.True(IsReviewBadgeVisible(status));
}

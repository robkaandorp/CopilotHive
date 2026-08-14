using System.Globalization;
using System.Text;

using CopilotHive.Dashboard;

using Microsoft.Extensions.AI;

namespace CopilotHive.Tests;

/// <summary>
/// Unit tests for the Reasoning column rendered by <c>Workers.razor</c>. The page picks the
/// reasoning effort based on whether the worker is displaying the role's premium model or its
/// standard model, so the three-way resolution is mirrored here and exercised without bUnit.
/// </summary>
public sealed class WorkersReasoningTests
{
    /// <summary>
    /// Mirrors the <c>ReasoningLabelFor</c> helper in <c>Workers.razor</c>.
    /// </summary>
    private static string ReasoningLabelFor(
        string? displayedModel,
        string? premiumModel,
        string? standardModel,
        ReasoningEffort? premiumReasoning,
        ReasoningEffort? standardReasoning)
    {
        if (string.IsNullOrEmpty(displayedModel)) return "—";

        if (string.Equals(displayedModel, premiumModel, StringComparison.OrdinalIgnoreCase))
            return ReasoningEffortOptions.Label(premiumReasoning);

        if (string.Equals(displayedModel, standardModel, StringComparison.OrdinalIgnoreCase))
            return ReasoningEffortOptions.Label(standardReasoning);

        return "—";
    }

    /// <summary>
    /// Mirrors the Model + Reasoning cells of a worker row in <c>Workers.razor</c>.
    /// </summary>
    private static string BuildRowHtml(
        string? displayedModel,
        string? premiumModel,
        string? standardModel,
        ReasoningEffort? premiumReasoning,
        ReasoningEffort? standardReasoning)
    {
        var sb = new StringBuilder();
        sb.Append("<td style=\"font-family:monospace;font-size:0.85rem;color:var(--text-muted)\">");
        sb.Append(string.IsNullOrEmpty(displayedModel) ? "—" : displayedModel);
        sb.Append("</td>");
        sb.Append(CultureInfo.InvariantCulture,
            $"<td style=\"font-size:0.85rem\">{ReasoningLabelFor(displayedModel, premiumModel, standardModel, premiumReasoning, standardReasoning)}</td>");
        return sb.ToString();
    }

    // ── three-way resolution ──────────────────────────────────────────────────

    [Fact]
    public void Worker_DisplayingPremiumModel_RendersPremiumReasoningLabel()
    {
        var label = ReasoningLabelFor(
            displayedModel: "copilot/premium-coder",
            premiumModel: "copilot/premium-coder",
            standardModel: "copilot/coder",
            premiumReasoning: ReasoningEffort.ExtraHigh,
            standardReasoning: ReasoningEffort.Low);

        Assert.Equal("Extra High", label);
    }

    [Fact]
    public void Worker_DisplayingStandardModel_RendersStandardReasoningLabel()
    {
        var label = ReasoningLabelFor(
            displayedModel: "copilot/coder",
            premiumModel: "copilot/premium-coder",
            standardModel: "copilot/coder",
            premiumReasoning: ReasoningEffort.ExtraHigh,
            standardReasoning: ReasoningEffort.Low);

        Assert.Equal("Low", label);
    }

    [Fact]
    public void Worker_DisplayingNeitherModel_RendersEmDash()
    {
        var label = ReasoningLabelFor(
            displayedModel: "copilot/some-other-model",
            premiumModel: "copilot/premium-coder",
            standardModel: "copilot/coder",
            premiumReasoning: ReasoningEffort.ExtraHigh,
            standardReasoning: ReasoningEffort.Low);

        Assert.Equal("—", label);
    }

    [Fact]
    public void Worker_NullDisplayedModel_RendersEmDash()
    {
        var label = ReasoningLabelFor(
            displayedModel: null,
            premiumModel: "copilot/premium-coder",
            standardModel: "copilot/coder",
            premiumReasoning: ReasoningEffort.ExtraHigh,
            standardReasoning: ReasoningEffort.Low);

        Assert.Equal("—", label);
    }

    [Fact]
    public void Worker_EmptyDisplayedModel_RendersEmDash()
    {
        var label = ReasoningLabelFor(
            displayedModel: "",
            premiumModel: "copilot/premium-coder",
            standardModel: "copilot/coder",
            premiumReasoning: ReasoningEffort.ExtraHigh,
            standardReasoning: ReasoningEffort.Low);

        Assert.Equal("—", label);
    }

    [Fact]
    public void Worker_ModelMatchIsCaseInsensitive()
    {
        var premium = ReasoningLabelFor(
            displayedModel: "COPILOT/PREMIUM-CODER",
            premiumModel: "copilot/premium-coder",
            standardModel: "copilot/coder",
            premiumReasoning: ReasoningEffort.High,
            standardReasoning: ReasoningEffort.Low);

        var standard = ReasoningLabelFor(
            displayedModel: "Copilot/Coder",
            premiumModel: "copilot/premium-coder",
            standardModel: "copilot/coder",
            premiumReasoning: ReasoningEffort.High,
            standardReasoning: ReasoningEffort.Low);

        Assert.Equal("High", premium);
        Assert.Equal("Low", standard);
    }

    [Fact]
    public void Worker_PremiumTakesPrecedenceWhenBothModelsAreIdentical()
    {
        // When a role's premium model equals its standard model, the premium reasoning wins
        // because the premium comparison runs first.
        var label = ReasoningLabelFor(
            displayedModel: "copilot/same-model",
            premiumModel: "copilot/same-model",
            standardModel: "copilot/same-model",
            premiumReasoning: ReasoningEffort.ExtraHigh,
            standardReasoning: ReasoningEffort.None);

        Assert.Equal("Extra High", label);
    }

    [Fact]
    public void Worker_NullPremiumModel_FallsBackToStandardReasoning()
    {
        var label = ReasoningLabelFor(
            displayedModel: "copilot/coder",
            premiumModel: null,
            standardModel: "copilot/coder",
            premiumReasoning: null,
            standardReasoning: ReasoningEffort.Medium);

        Assert.Equal("Medium", label);
    }

    [Fact]
    public void Worker_MatchedModelWithNullReasoning_RendersEmDash()
    {
        var label = ReasoningLabelFor(
            displayedModel: "copilot/coder",
            premiumModel: "copilot/premium-coder",
            standardModel: "copilot/coder",
            premiumReasoning: ReasoningEffort.High,
            standardReasoning: null);

        Assert.Equal("—", label);
    }

    // ── rendered markup ───────────────────────────────────────────────────────

    [Fact]
    public void Row_RendersPlainModelAndSeparateReasoningCell()
    {
        var html = BuildRowHtml(
            displayedModel: "copilot/coder",
            premiumModel: "copilot/premium-coder",
            standardModel: "copilot/coder",
            premiumReasoning: ReasoningEffort.ExtraHigh,
            standardReasoning: ReasoningEffort.Medium);

        Assert.Contains("copilot/coder", html);
        Assert.Contains("Medium</td>", html);
        // The reasoning is its own cell, not an inline badge glued to the model name.
        Assert.DoesNotContain("class=\"badge\"", html);
    }

    [Fact]
    public void Row_ModelNameWithColon_IsNotStripped()
    {
        // No legacy suffix parsing: the model name renders verbatim.
        var html = BuildRowHtml(
            displayedModel: "copilot/coder:high",
            premiumModel: null,
            standardModel: "copilot/coder:high",
            premiumReasoning: null,
            standardReasoning: ReasoningEffort.None);

        Assert.Contains("copilot/coder:high", html);
        Assert.Contains("None</td>", html);
    }

    // ── integration with WorkerInfo.GetDisplayModel ───────────────────────────

    [Fact]
    public void GetDisplayModel_CurrentModelWins_AndDrivesPremiumReasoning()
    {
        var roleModels = new Dictionary<string, string> { ["coder"] = "copilot/coder" };
        var worker = new WorkerInfo { Id = "w1", Role = "coder", CurrentModel = "copilot/premium-coder" };

        var displayed = worker.GetDisplayModel(roleModels);
        var label = ReasoningLabelFor(displayed, "copilot/premium-coder", "copilot/coder",
            ReasoningEffort.ExtraHigh, ReasoningEffort.Low);

        Assert.Equal("copilot/premium-coder", displayed);
        Assert.Equal("Extra High", label);
    }

    [Fact]
    public void GetDisplayModel_IdleWorkerUsesRoleDefault_AndDrivesStandardReasoning()
    {
        var roleModels = new Dictionary<string, string> { ["coder"] = "copilot/coder" };
        var worker = new WorkerInfo { Id = "w2", Role = "coder", CurrentModel = null };

        var displayed = worker.GetDisplayModel(roleModels);
        var label = ReasoningLabelFor(displayed, "copilot/premium-coder", "copilot/coder",
            ReasoningEffort.ExtraHigh, ReasoningEffort.Low);

        Assert.Equal("copilot/coder", displayed);
        Assert.Equal("Low", label);
    }

    [Fact]
    public void GetDisplayModel_UnspecifiedRole_YieldsNullModelAndEmDashReasoning()
    {
        var roleModels = new Dictionary<string, string> { ["coder"] = "copilot/coder" };
        var worker = new WorkerInfo { Id = "w3", Role = "Unspecified", CurrentModel = null };

        var displayed = worker.GetDisplayModel(roleModels);
        var label = ReasoningLabelFor(displayed, null, null, null, null);

        Assert.Null(displayed);
        Assert.Equal("—", label);
    }
}

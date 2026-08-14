using System.Globalization;
using System.Text;

using CopilotHive.Dashboard;

using Microsoft.Extensions.AI;

namespace CopilotHive.Tests;

/// <summary>
/// Unit tests for the Model Configuration table rendered by <c>Orchestrator.razor</c>. The table
/// markup is mirrored here so the five-column layout
/// (<c>Role | Model | Reasoning | Premium Model | Premium Reasoning</c>) can be verified without
/// bUnit or a live Blazor runtime.
/// </summary>
public sealed class OrchestratorModelTableTests
{
    /// <summary>
    /// Builds the Model Configuration table markup exactly as <c>Orchestrator.razor</c> does.
    /// </summary>
    private static string BuildTableHtml(OrchestratorInfo info)
    {
        var sb = new StringBuilder();
        sb.Append("<table>");
        sb.Append("<thead><tr><th>Role</th><th>Model</th><th>Reasoning</th><th>Premium Model</th><th>Premium Reasoning</th></tr></thead>");
        sb.Append("<tbody>");

        sb.Append("<tr>");
        sb.Append("<td><span class=\"badge badge-yellow\">brain</span></td>");
        sb.Append(CultureInfo.InvariantCulture, $"<td style=\"font-family:monospace;font-size:0.85rem\">{info.BrainModel}</td>");
        sb.Append(CultureInfo.InvariantCulture, $"<td>{ReasoningEffortOptions.Label(info.BrainReasoningEffort)}</td>");
        sb.Append("<td>—</td>");
        sb.Append("<td>—</td>");
        sb.Append("</tr>");

        sb.Append("<tr>");
        sb.Append("<td><span class=\"badge badge-yellow\">composer</span></td>");
        sb.Append(CultureInfo.InvariantCulture, $"<td style=\"font-family:monospace;font-size:0.85rem\">{info.ComposerModel}</td>");
        sb.Append(CultureInfo.InvariantCulture, $"<td>{ReasoningEffortOptions.Label(info.ComposerReasoningEffort)}</td>");
        sb.Append("<td>—</td>");
        sb.Append("<td>—</td>");
        sb.Append("</tr>");

        foreach (var role in info.RoleModels.Keys)
        {
            var roleKey = role.ToLowerInvariant();
            var premiumModel = info.RolePremiumModels.GetValueOrDefault(roleKey);
            var hasPremium = !string.IsNullOrWhiteSpace(premiumModel);

            sb.Append("<tr>");
            sb.Append(CultureInfo.InvariantCulture, $"<td><span class=\"badge badge-blue\">{role}</span></td>");
            sb.Append(CultureInfo.InvariantCulture,
                $"<td style=\"font-family:monospace;font-size:0.85rem\">{info.RoleModels.GetValueOrDefault(roleKey)}</td>");
            sb.Append(CultureInfo.InvariantCulture,
                $"<td>{ReasoningEffortOptions.Label(info.RoleReasoningEfforts.GetValueOrDefault(roleKey))}</td>");
            sb.Append(CultureInfo.InvariantCulture,
                $"<td style=\"font-family:monospace;font-size:0.85rem\">{(hasPremium ? premiumModel : "—")}</td>");
            sb.Append(CultureInfo.InvariantCulture,
                $"<td>{(hasPremium ? ReasoningEffortOptions.Label(info.RolePremiumReasoningEfforts.GetValueOrDefault(roleKey)) : "—")}</td>");
            sb.Append("</tr>");
        }

        sb.Append("<tr>");
        sb.Append("<td><span class=\"badge badge-green\">compaction</span></td>");
        sb.Append(CultureInfo.InvariantCulture,
            $"<td style=\"font-family:monospace;font-size:0.85rem\">{(string.IsNullOrWhiteSpace(info.CompactionModel) ? "(use main model)" : info.CompactionModel)}</td>");
        sb.Append("<td>—</td>");
        sb.Append("<td>—</td>");
        sb.Append("<td>—</td>");
        sb.Append("</tr>");

        sb.Append("</tbody>");
        sb.Append("</table>");
        return sb.ToString();
    }

    private static OrchestratorInfo BuildInfo(
        string? premiumModel,
        ReasoningEffort? premiumReasoning = ReasoningEffort.ExtraHigh,
        ReasoningEffort? standardReasoning = ReasoningEffort.Medium,
        string? compactionModel = null)
    {
        return new OrchestratorInfo
        {
            BrainModel = "copilot/brain-model",
            BrainReasoningEffort = ReasoningEffort.High,
            ComposerModel = "copilot/composer-model",
            ComposerReasoningEffort = ReasoningEffort.Low,
            CompactionModel = compactionModel,
            RoleModels = new Dictionary<string, string> { ["coder"] = "copilot/coder-model" },
            RoleReasoningEfforts = new Dictionary<string, ReasoningEffort?>(StringComparer.OrdinalIgnoreCase)
            {
                ["coder"] = standardReasoning,
            },
            RolePremiumModels = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["coder"] = premiumModel,
            },
            RolePremiumReasoningEfforts = new Dictionary<string, ReasoningEffort?>(StringComparer.OrdinalIgnoreCase)
            {
                ["coder"] = premiumReasoning,
            },
        };
    }

    // ── header ────────────────────────────────────────────────────────────────

    [Fact]
    public void Table_RendersFiveColumnHeader()
    {
        var html = BuildTableHtml(BuildInfo(premiumModel: null));

        Assert.Contains("<th>Role</th>", html);
        Assert.Contains("<th>Model</th>", html);
        Assert.Contains("<th>Reasoning</th>", html);
        Assert.Contains("<th>Premium Model</th>", html);
        Assert.Contains("<th>Premium Reasoning</th>", html);
    }

    // ── worker role rows ──────────────────────────────────────────────────────

    [Fact]
    public void RoleRow_WithPremiumModel_RendersPremiumModelAndPremiumReasoning()
    {
        var html = BuildTableHtml(BuildInfo(premiumModel: "copilot/premium-coder"));

        Assert.Contains("copilot/coder-model", html);
        Assert.Contains("copilot/premium-coder", html);
        Assert.Contains("<td>Medium</td>", html);
        Assert.Contains("<td>Extra High</td>", html);
    }

    [Fact]
    public void RoleRow_NullPremiumModel_RendersEmDashInBothPremiumColumns()
    {
        var html = BuildTableHtml(BuildInfo(premiumModel: null));

        Assert.Contains("<td style=\"font-family:monospace;font-size:0.85rem\">—</td>", html);
        Assert.DoesNotContain("Extra High", html);
        Assert.Contains("<td>Medium</td>", html);
    }

    [Fact]
    public void RoleRow_WhitespacePremiumModel_RendersEmDashInBothPremiumColumns()
    {
        var html = BuildTableHtml(BuildInfo(premiumModel: "   "));

        Assert.Contains("<td style=\"font-family:monospace;font-size:0.85rem\">—</td>", html);
        Assert.DoesNotContain("Extra High", html);
    }

    [Fact]
    public void RoleRow_NullReasoning_RendersEmDash()
    {
        var html = BuildTableHtml(BuildInfo(premiumModel: null, standardReasoning: null));

        Assert.Contains("<td>—</td>", html);
        Assert.DoesNotContain("<td>Medium</td>", html);
    }

    [Fact]
    public void RoleRow_PremiumModelPresentButPremiumReasoningNull_RendersEmDashReasoning()
    {
        var html = BuildTableHtml(BuildInfo(premiumModel: "copilot/premium-coder", premiumReasoning: null));

        Assert.Contains("copilot/premium-coder", html);
        Assert.Contains("<td>—</td>", html);
        Assert.DoesNotContain("Extra High", html);
    }

    // ── brain / composer rows ─────────────────────────────────────────────────

    [Fact]
    public void BrainAndComposerRows_RenderPlainModelsReasoningAndEmDashPremiums()
    {
        var html = BuildTableHtml(BuildInfo(premiumModel: "copilot/premium-coder"));

        Assert.Contains("copilot/brain-model", html);
        Assert.Contains("copilot/composer-model", html);
        Assert.Contains("<td>High</td>", html);
        Assert.Contains("<td>Low</td>", html);
        // Brain and composer rows each end with two em-dash premium cells.
        Assert.Contains("<td>—</td><td>—</td></tr>", html);
    }

    // ── compaction row ────────────────────────────────────────────────────────

    [Fact]
    public void CompactionRow_NullModel_RendersUseMainModelAndEmDashReasoning()
    {
        var html = BuildTableHtml(BuildInfo(premiumModel: null, compactionModel: null));

        Assert.Contains("(use main model)", html);
        Assert.Contains("<td><span class=\"badge badge-green\">compaction</span></td>", html);
        Assert.Contains("<td>—</td><td>—</td><td>—</td></tr>", html);
    }

    [Fact]
    public void CompactionRow_ModelSet_RendersPlainModelAndEmDashReasoning()
    {
        var html = BuildTableHtml(BuildInfo(premiumModel: null, compactionModel: "copilot/compaction-model"));

        Assert.Contains("copilot/compaction-model", html);
        Assert.DoesNotContain("(use main model)", html);
        Assert.Contains("<td>—</td><td>—</td><td>—</td></tr>", html);
    }

    [Fact]
    public void CompactionRow_WhitespaceModel_RendersUseMainModel()
    {
        var html = BuildTableHtml(BuildInfo(premiumModel: null, compactionModel: "   "));

        Assert.Contains("(use main model)", html);
    }

    // ── no legacy suffix parsing ──────────────────────────────────────────────

    [Fact]
    public void Table_ModelNamesRenderVerbatim_NoSuffixStripping()
    {
        // A model name that happens to contain a colon must NOT be truncated: the backend now
        // stores reasoning separately, so the page renders whatever it is given.
        var info = new OrchestratorInfo
        {
            BrainModel = "copilot/brain:high",
            BrainReasoningEffort = ReasoningEffort.None,
            ComposerModel = "copilot/composer-model",
            ComposerReasoningEffort = null,
            RoleModels = new Dictionary<string, string>(),
        };

        var html = BuildTableHtml(info);

        Assert.Contains("copilot/brain:high", html);
        Assert.Contains("<td>None</td>", html);
    }
}

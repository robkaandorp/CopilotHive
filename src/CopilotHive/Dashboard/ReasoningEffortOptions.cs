using CopilotHive.Services;
using Microsoft.Extensions.AI;

namespace CopilotHive.Dashboard;

/// <summary>
/// Shared, single source of truth for reasoning-effort presentation in the dashboard UI:
/// the ordered dropdown options, their human-readable labels, and the canonical wire value.
/// </summary>
public static class ReasoningEffortOptions
{
    /// <summary>
    /// The reasoning levels offered by the UI, in ascending order, with their display labels.
    /// </summary>
    public static readonly IReadOnlyList<(ReasoningEffort Value, string Label)> Options =
    [
        (ReasoningEffort.None, "None"),
        (ReasoningEffort.Low, "Low"),
        (ReasoningEffort.Medium, "Medium"),
        (ReasoningEffort.High, "High"),
        (ReasoningEffort.ExtraHigh, "Extra High"),
    ];

    /// <summary>
    /// Human-readable label for a reasoning effort. <c>null</c> (unset) renders as an em dash.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the value is not a known level.</exception>
    public static string Label(ReasoningEffort? effort) =>
        effort is null ? "—" : Options.First(o => o.Value == effort).Label;

    /// <summary>
    /// Canonical snake_case wire value for a reasoning effort, as produced by
    /// <see cref="ReasoningEffortConverter.Format(ReasoningEffort?)"/>.
    /// </summary>
    public static string WireValue(ReasoningEffort effort) =>
        ReasoningEffortConverter.Format(effort) ?? "none";
}

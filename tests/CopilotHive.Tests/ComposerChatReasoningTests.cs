using System.Text.Json;
using System.Text.Json.Serialization;

using CopilotHive.Components.Pages;
using CopilotHive.Dashboard;
using CopilotHive.Services;

using Microsoft.Extensions.AI;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Pure composition tests for the Composer chat model/reasoning selection helpers in
/// <see cref="ComposerChat"/>. These exercise the production static methods directly through
/// <c>InternalsVisibleTo</c>; no bUnit rendering is required.
/// </summary>
public sealed class ComposerChatReasoningTests
{
    // ── Switch reasoning wire value: canonical form ─────────────────────────

    /// <summary>
    /// The facade parses the CANONICAL wire form, so the page must hand it that form — the
    /// multi-word level travels as <c>extra_high</c>, never as the C# name <c>ExtraHigh</c>.
    /// </summary>
    [Fact]
    public void SwitchReasoningWireValue_ExtraHigh_UsesSnakeCaseWireForm()
    {
        var value = ComposerChat.SwitchReasoningWireValue(ReasoningEffort.ExtraHigh);

        Assert.Equal("extra_high", value);
        Assert.DoesNotContain("ExtraHigh", value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ReasoningEffort.None, "none")]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.ExtraHigh, "extra_high")]
    public void SwitchReasoningWireValue_EveryLevel_MatchesConverterFormat(ReasoningEffort effort, string expected)
    {
        Assert.Equal(expected, ComposerChat.SwitchReasoningWireValue(effort));
        Assert.Equal(expected, ReasoningEffortConverter.Format(effort));
    }

    /// <summary>
    /// The value the page produces must round-trip through the converter the facade parses with,
    /// so a switch is never rejected for an unparsable reasoning effort.
    /// </summary>
    [Theory]
    [InlineData(ReasoningEffort.None)]
    [InlineData(ReasoningEffort.Low)]
    [InlineData(ReasoningEffort.Medium)]
    [InlineData(ReasoningEffort.High)]
    [InlineData(ReasoningEffort.ExtraHigh)]
    public void SwitchReasoningWireValue_RoundTripsThroughTheParserTheFacadeUses(ReasoningEffort effort)
    {
        var value = ComposerChat.SwitchReasoningWireValue(effort);

        Assert.Equal(effort, ReasoningEffortConverter.Parse(value));
    }

    // ── InitialReasoning: null → None ────────────────────────────────────────

    [Fact]
    public void InitialReasoning_NullResponse_DefaultsToNone()
    {
        Assert.Equal(ReasoningEffort.None, ComposerChat.InitialReasoning(null));
    }

    [Theory]
    [InlineData(ReasoningEffort.None)]
    [InlineData(ReasoningEffort.Low)]
    [InlineData(ReasoningEffort.Medium)]
    [InlineData(ReasoningEffort.High)]
    [InlineData(ReasoningEffort.ExtraHigh)]
    public void InitialReasoning_PresentResponse_IsUsedVerbatim(ReasoningEffort effort)
    {
        Assert.Equal(effort, ComposerChat.InitialReasoning(effort));
    }

    // ── ResolveReasoningAfterSwitch: failure restores prior ──────────────────

    [Fact]
    public void ResolveReasoningAfterSwitch_Success_KeepsAttemptedLevel()
    {
        var result = ComposerChat.ResolveReasoningAfterSwitch(
            previous: ReasoningEffort.Low, attempted: ReasoningEffort.ExtraHigh, succeeded: true);

        Assert.Equal(ReasoningEffort.ExtraHigh, result);
    }

    [Fact]
    public void ResolveReasoningAfterSwitch_Failure_RestoresPriorLevel()
    {
        // A rejected switch left the server on the previous level, so the dropdown must not
        // keep showing a level that was never applied.
        var result = ComposerChat.ResolveReasoningAfterSwitch(
            previous: ReasoningEffort.Low, attempted: ReasoningEffort.ExtraHigh, succeeded: false);

        Assert.Equal(ReasoningEffort.Low, result);
    }

    [Fact]
    public void ResolveReasoningAfterSwitch_FailureFromNone_RestoresNone()
    {
        var result = ComposerChat.ResolveReasoningAfterSwitch(
            previous: ReasoningEffort.None, attempted: ReasoningEffort.High, succeeded: false);

        Assert.Equal(ReasoningEffort.None, result);
    }

    // ── Dropdown options ─────────────────────────────────────────────────────

    [Fact]
    public void ReasoningOptions_ContainsAllFiveLevelsInAscendingOrder()
    {
        Assert.Equal(
            new[]
            {
                ReasoningEffort.None,
                ReasoningEffort.Low,
                ReasoningEffort.Medium,
                ReasoningEffort.High,
                ReasoningEffort.ExtraHigh,
            },
            ReasoningEffortOptions.Options.Select(o => o.Value));
    }

    [Theory]
    [InlineData(ReasoningEffort.None, "None")]
    [InlineData(ReasoningEffort.Low, "Low")]
    [InlineData(ReasoningEffort.Medium, "Medium")]
    [InlineData(ReasoningEffort.High, "High")]
    [InlineData(ReasoningEffort.ExtraHigh, "Extra High")]
    public void ReasoningLabel_UsesCapitalizedDisplayText(ReasoningEffort effort, string expected)
    {
        Assert.Equal(expected, ReasoningEffortOptions.Label(effort));
    }

    [Fact]
    public void ReasoningLabel_UnknownValue_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => ReasoningEffortOptions.Label((ReasoningEffort)999));
    }

    // ── Client JSON options must match the server's global converter ─────────

    /// <summary>
    /// The page deserializes API responses with its own options, so they must carry the SAME
    /// snake_case enum converter the server writes with — otherwise <c>extra_high</c> would
    /// fail to bind and the dropdown would silently fall back to None.
    /// </summary>
    [Fact]
    public void ApiJsonOptions_DeserializesSnakeCaseReasoningEffort()
    {
        var value = JsonSerializer.Deserialize<ReasoningEffort?>(
            "\"extra_high\"", ReasoningEffortOptions.JsonOptions);

        Assert.Equal(ReasoningEffort.ExtraHigh, value);
    }

    [Fact]
    public void ApiJsonOptions_SerializesReasoningEffortAsSnakeCase()
    {
        var json = JsonSerializer.Serialize(ReasoningEffort.ExtraHigh, ReasoningEffortOptions.JsonOptions);

        Assert.Equal("\"extra_high\"", json);
    }

    [Fact]
    public void ApiJsonOptions_RejectsIntegerReasoningEffort()
    {
        // allowIntegerValues: false — matches the server so numeric values are never coerced.
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ReasoningEffort>("3", ReasoningEffortOptions.JsonOptions));
    }

    [Fact]
    public void ApiJsonOptions_CarriesSnakeCaseStringEnumConverter()
    {
        Assert.Contains(ReasoningEffortOptions.JsonOptions.Converters, c => c is JsonStringEnumConverter);
    }
}

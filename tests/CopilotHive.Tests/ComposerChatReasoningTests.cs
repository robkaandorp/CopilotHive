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
    // ── BuildSwitchUri: both params, canonical wire form ─────────────────────

    [Fact]
    public void BuildSwitchUri_SendsBothModelAndReasoning()
    {
        var uri = ComposerChat.BuildSwitchUri("claude-opus", ReasoningEffort.Medium);

        Assert.Contains("model=claude-opus", uri, StringComparison.Ordinal);
        Assert.Contains("reasoning=medium", uri, StringComparison.Ordinal);
        Assert.StartsWith("/api/composer/models/switch?", uri, StringComparison.Ordinal);
    }

    /// <summary>
    /// The multi-word level must travel as <c>extra_high</c>, never as the C# name
    /// <c>ExtraHigh</c> — the server parses the canonical wire form only.
    /// </summary>
    [Fact]
    public void BuildSwitchUri_ExtraHigh_UsesSnakeCaseWireForm()
    {
        var uri = ComposerChat.BuildSwitchUri("gpt-5", ReasoningEffort.ExtraHigh);

        Assert.Contains("reasoning=extra_high", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("ExtraHigh", uri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ReasoningEffort.None, "none")]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.ExtraHigh, "extra_high")]
    public void BuildSwitchUri_EveryLevel_MatchesConverterFormat(ReasoningEffort effort, string expected)
    {
        var uri = ComposerChat.BuildSwitchUri("m", effort);

        Assert.Contains($"reasoning={expected}", uri, StringComparison.Ordinal);
        Assert.Equal(expected, ReasoningEffortConverter.Format(effort));
    }

    [Fact]
    public void BuildSwitchUri_ModelWithSlash_IsUrlEncoded()
    {
        var uri = ComposerChat.BuildSwitchUri("copilot/claude-sonnet-4.6", ReasoningEffort.High);

        Assert.Contains("model=copilot%2Fclaude-sonnet-4.6", uri, StringComparison.Ordinal);
        Assert.Contains("reasoning=high", uri, StringComparison.Ordinal);
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
            "\"extra_high\"", ComposerChat.ApiJsonOptions);

        Assert.Equal(ReasoningEffort.ExtraHigh, value);
    }

    [Fact]
    public void ApiJsonOptions_SerializesReasoningEffortAsSnakeCase()
    {
        var json = JsonSerializer.Serialize(ReasoningEffort.ExtraHigh, ComposerChat.ApiJsonOptions);

        Assert.Equal("\"extra_high\"", json);
    }

    [Fact]
    public void ApiJsonOptions_RejectsIntegerReasoningEffort()
    {
        // allowIntegerValues: false — matches the server so numeric values are never coerced.
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ReasoningEffort>("3", ComposerChat.ApiJsonOptions));
    }

    [Fact]
    public void ApiJsonOptions_CarriesSnakeCaseStringEnumConverter()
    {
        Assert.Contains(ComposerChat.ApiJsonOptions.Converters, c => c is JsonStringEnumConverter);
    }
}

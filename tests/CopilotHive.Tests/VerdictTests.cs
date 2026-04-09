using Xunit;

namespace CopilotHive.Tests;

/// <summary>Tests for <see cref="CopilotHive.Services.Verdict"/> constants and matching logic.</summary>
public sealed class VerdictTests
{
    /// <summary>
    /// Verifies that an exact uppercase match returns true.
    /// </summary>
    [Fact]
    public void Matches_ExactUppercaseMatch_ReturnsTrue()
    {
        Assert.True(CopilotHive.Services.Verdict.Matches("PASS", CopilotHive.Services.Verdict.Pass));
    }

    /// <summary>
    /// Verifies that a lowercase input still matches the uppercase constant.
    /// This is the key new behavior — lowercase worker output now works correctly.
    /// </summary>
    [Fact]
    public void Matches_LowercaseInputMatch_ReturnsTrue()
    {
        Assert.True(CopilotHive.Services.Verdict.Matches("pass", CopilotHive.Services.Verdict.Pass));
    }

    /// <summary>
    /// Verifies that mixed-case input matches the corresponding constant.
    /// </summary>
    [Fact]
    public void Matches_MixedCaseInputMatch_ReturnsTrue()
    {
        Assert.True(CopilotHive.Services.Verdict.Matches("Request_Changes", CopilotHive.Services.Verdict.RequestChanges));
    }

    /// <summary>
    /// Verifies that a non-matching verdict returns false.
    /// </summary>
    [Fact]
    public void Matches_NonMatchingVerdict_ReturnsFalse()
    {
        Assert.False(CopilotHive.Services.Verdict.Matches("FAIL", CopilotHive.Services.Verdict.Pass));
    }

    /// <summary>
    /// Verifies that a null verdict returns false rather than throwing.
    /// </summary>
    [Fact]
    public void Matches_NullVerdict_ReturnsFalse()
    {
        Assert.False(CopilotHive.Services.Verdict.Matches(null, CopilotHive.Services.Verdict.Pass));
    }
}
using CopilotHive.Configuration;

namespace CopilotHive.Tests.Configuration;

public class GoalIdTests
{
    // ── Valid IDs ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("fix-build-error")]
    [InlineData("add-feature")]
    [InlineData("abc")]
    [InlineData("a1b2")]
    [InlineData("a-1-b")]
    public void Validate_ValidId_DoesNotThrow(string id)
    {
        var ex = Record.Exception(() => GoalId.Validate(id));
        Assert.Null(ex);
    }

    // ── Invalid IDs ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Validate_NullOrEmpty_ThrowsWithNullOrEmptyMessage(string? id)
    {
        var ex = Assert.Throws<ArgumentException>(() => GoalId.Validate(id!));
        Assert.Contains("null or empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Fix-Build")]
    [InlineData("FIX")]
    public void Validate_UppercaseId_ThrowsWithUppercaseMessage(string id)
    {
        var ex = Assert.Throws<ArgumentException>(() => GoalId.Validate(id));
        Assert.Contains("uppercase", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("fix build")]
    public void Validate_WhitespaceId_ThrowsWithWhitespaceMessage(string id)
    {
        var ex = Assert.Throws<ArgumentException>(() => GoalId.Validate(id));
        Assert.Contains("whitespace", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("-fix")]
    public void Validate_LeadingHyphenId_ThrowsWithLeadingHyphenMessage(string id)
    {
        var ex = Assert.Throws<ArgumentException>(() => GoalId.Validate(id));
        Assert.Contains("start with a hyphen", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("fix-")]
    public void Validate_TrailingHyphenId_ThrowsWithTrailingHyphenMessage(string id)
    {
        var ex = Assert.Throws<ArgumentException>(() => GoalId.Validate(id));
        Assert.Contains("end with a hyphen", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("fix_build")]
    public void Validate_InvalidCharId_ThrowsWithInvalidCharMessage(string id)
    {
        var ex = Assert.Throws<ArgumentException>(() => GoalId.Validate(id));
        Assert.Contains("invalid characters", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

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
    [InlineData("Fix-Build")]
    [InlineData("fix build")]
    [InlineData("-fix")]
    [InlineData("fix-")]
    [InlineData("fix_build")]
    [InlineData("FIX")]
    public void Validate_InvalidId_ThrowsArgumentException(string? id)
    {
        Assert.Throws<ArgumentException>(() => GoalId.Validate(id!));
    }
}

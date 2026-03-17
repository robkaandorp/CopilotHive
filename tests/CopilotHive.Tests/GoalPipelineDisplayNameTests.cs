using CopilotHive.Services;
using Xunit;

namespace CopilotHive.Tests;

public class GoalPipelineDisplayNameTests
{
    [Theory]
    [InlineData(GoalPhase.Coding,     "Coding")]
    [InlineData(GoalPhase.Testing,    "Testing")]
    [InlineData(GoalPhase.DocWriting, "Doc Writing")]
    [InlineData(GoalPhase.Review,     "Review")]
    [InlineData(GoalPhase.Improve,    "Improvement")]
    [InlineData(GoalPhase.Merging,    "Merging")]
    [InlineData(GoalPhase.Done,       "Done")]
    [InlineData(GoalPhase.Failed,     "Failed")]
    public void GetDisplayName_ReturnsExpectedString(GoalPhase phase, string expected)
    {
        Assert.Equal(expected, GoalPipeline.GetDisplayName(phase));
    }

    [Fact]
    public void GetDisplayName_UnknownPhase_ReturnsPhaseName()
    {
        // Planning has no explicit mapping, should fall through to ToString()
        Assert.Equal("Planning", GoalPipeline.GetDisplayName(GoalPhase.Planning));
    }
}

using CopilotHive.Services;
using Xunit;

namespace CopilotHive.Tests;

public class GoalPipelineDisplayNameTests
{
    [Fact] public void GetDisplayName_Coding_ReturnsCoding() =>
        Assert.Equal("Coding", GoalPipeline.GetDisplayName(GoalPhase.Coding));

    [Fact] public void GetDisplayName_Testing_ReturnsTesting() =>
        Assert.Equal("Testing", GoalPipeline.GetDisplayName(GoalPhase.Testing));

    [Fact] public void GetDisplayName_DocWriting_ReturnsDocWriting() =>
        Assert.Equal("Doc Writing", GoalPipeline.GetDisplayName(GoalPhase.DocWriting));

    [Fact] public void GetDisplayName_Review_ReturnsReview() =>
        Assert.Equal("Review", GoalPipeline.GetDisplayName(GoalPhase.Review));

    [Fact] public void GetDisplayName_Improve_ReturnsImprovement() =>
        Assert.Equal("Improvement", GoalPipeline.GetDisplayName(GoalPhase.Improve));

    [Fact] public void GetDisplayName_Merging_ReturnsMerging() =>
        Assert.Equal("Merging", GoalPipeline.GetDisplayName(GoalPhase.Merging));

    [Fact] public void GetDisplayName_Done_ReturnsDone() =>
        Assert.Equal("Done", GoalPipeline.GetDisplayName(GoalPhase.Done));

    [Fact] public void GetDisplayName_Failed_ReturnsFailed() =>
        Assert.Equal("Failed", GoalPipeline.GetDisplayName(GoalPhase.Failed));

    [Fact] public void GetDisplayName_UnknownPhase_ReturnsPhaseName()
    {
        // Planning has no explicit mapping, should fall through to ToString()
        Assert.Equal("Planning", GoalPipeline.GetDisplayName(GoalPhase.Planning));
    }
}

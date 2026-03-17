using CopilotHive.Services;
using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Tests for the <see cref="GoalPhaseExtensions.ToDisplayName"/> extension method.
/// </summary>
public class GoalPhaseExtensionsTests
{
    [Fact]
    public void ToDisplayName_Planning_ReturnsPlanning() =>
        Assert.Equal("Planning", GoalPhase.Planning.ToDisplayName());

    [Fact]
    public void ToDisplayName_Coding_ReturnsCoding() =>
        Assert.Equal("Coding", GoalPhase.Coding.ToDisplayName());

    [Fact]
    public void ToDisplayName_Review_ReturnsReview() =>
        Assert.Equal("Review", GoalPhase.Review.ToDisplayName());

    [Fact]
    public void ToDisplayName_Testing_ReturnsTesting() =>
        Assert.Equal("Testing", GoalPhase.Testing.ToDisplayName());

    [Fact]
    public void ToDisplayName_DocWriting_ReturnsDocWriting() =>
        Assert.Equal("Doc Writing", GoalPhase.DocWriting.ToDisplayName());

    [Fact]
    public void ToDisplayName_Improve_ReturnsImprovement() =>
        Assert.Equal("Improvement", GoalPhase.Improve.ToDisplayName());

    [Fact]
    public void ToDisplayName_Merging_ReturnsMerging() =>
        Assert.Equal("Merging", GoalPhase.Merging.ToDisplayName());

    [Fact]
    public void ToDisplayName_Done_ReturnsDone() =>
        Assert.Equal("Done", GoalPhase.Done.ToDisplayName());

    [Fact]
    public void ToDisplayName_Failed_ReturnsFailed() =>
        Assert.Equal("Failed", GoalPhase.Failed.ToDisplayName());

    [Fact]
    public void ToDisplayName_InvalidPhase_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ((GoalPhase)999).ToDisplayName());
        Assert.Contains("999", ex.Message);
    }
}

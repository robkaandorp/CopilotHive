using CopilotHive.Services;
using CopilotHive.Workers;
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

/// <summary>
/// Tests for the <see cref="GoalPhaseExtensions.ToWorkerRole"/> extension method.
/// </summary>
public class GoalPhaseToWorkerRoleTests
{
    [Theory]
    [InlineData(GoalPhase.Coding, WorkerRole.Coder)]
    [InlineData(GoalPhase.Testing, WorkerRole.Tester)]
    [InlineData(GoalPhase.Review, WorkerRole.Reviewer)]
    [InlineData(GoalPhase.DocWriting, WorkerRole.DocWriter)]
    [InlineData(GoalPhase.Improve, WorkerRole.Improver)]
    public void ToWorkerRole_ValidPhases_ReturnsCorrectRole(GoalPhase phase, WorkerRole expected) =>
        Assert.Equal(expected, phase.ToWorkerRole());

    [Theory]
    [InlineData(GoalPhase.Planning)]
    [InlineData(GoalPhase.Merging)]
    [InlineData(GoalPhase.Done)]
    [InlineData(GoalPhase.Failed)]
    public void ToWorkerRole_NonWorkerPhases_Throws(GoalPhase phase) =>
        Assert.Throws<InvalidOperationException>(() => phase.ToWorkerRole());

    [Fact]
    public void ToWorkerRole_InvalidPhase_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ((GoalPhase)999).ToWorkerRole());
        Assert.Contains("999", ex.Message);
    }
}

/// <summary>
/// Tests for the <see cref="GoalPhaseExtensions.ToRoleName"/> extension method.
/// </summary>
public class GoalPhaseToRoleNameTests
{
    [Theory]
    [InlineData(GoalPhase.Coding,     "coder")]
    [InlineData(GoalPhase.Testing,    "tester")]
    [InlineData(GoalPhase.Review,     "reviewer")]
    [InlineData(GoalPhase.DocWriting, "docwriter")]
    [InlineData(GoalPhase.Improve,    "improver")]
    public void ToRoleName_WorkerPhases_ReturnsCorrectRoleName(GoalPhase phase, string expected) =>
        Assert.Equal(expected, phase.ToRoleName());

    [Theory]
    [InlineData(GoalPhase.Planning)]
    [InlineData(GoalPhase.Merging)]
    [InlineData(GoalPhase.Done)]
    [InlineData(GoalPhase.Failed)]
    public void ToRoleName_NonWorkerPhases_ReturnsEmptyString(GoalPhase phase) =>
        Assert.Equal("", phase.ToRoleName());

    [Fact]
    public void ToRoleName_InvalidEnumValue_ReturnsEmptyString() =>
        Assert.Equal("", ((GoalPhase)999).ToRoleName());
}

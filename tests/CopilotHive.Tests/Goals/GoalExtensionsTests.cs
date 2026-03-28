using System;
using CopilotHive.Goals;
using Xunit;

namespace CopilotHive.Tests;

public class GoalExtensionsTests
{
    [Fact]
    public void GoalPriority_Critical_ReturnsCritical()
        => Assert.Equal("Critical", GoalPriority.Critical.ToDisplayName());

    [Fact]
    public void GoalPriority_High_ReturnsHigh()
        => Assert.Equal("High", GoalPriority.High.ToDisplayName());

    [Fact]
    public void GoalPriority_Normal_ReturnsNormal()
        => Assert.Equal("Normal", GoalPriority.Normal.ToDisplayName());

    [Fact]
    public void GoalPriority_Low_ReturnsLow()
        => Assert.Equal("Low", GoalPriority.Low.ToDisplayName());

    [Fact]
    public void GoalStatus_Pending_ReturnsPending()
        => Assert.Equal("Pending", GoalStatus.Pending.ToDisplayName());

    [Fact]
    public void GoalStatus_InProgress_ReturnsInProgress()
        => Assert.Equal("In Progress", GoalStatus.InProgress.ToDisplayName());

    [Fact]
    public void GoalStatus_Completed_ReturnsCompleted()
        => Assert.Equal("Completed", GoalStatus.Completed.ToDisplayName());

    [Fact]
    public void GoalStatus_Failed_ReturnsFailed()
        => Assert.Equal("Failed", GoalStatus.Failed.ToDisplayName());

    [Fact]
    public void GoalStatus_Cancelled_ReturnsCancelled()
        => Assert.Equal("Cancelled", GoalStatus.Cancelled.ToDisplayName());

    [Fact]
    public void GoalPriority_InvalidValue_ThrowsInvalidOperationException()
        => Assert.Throws<InvalidOperationException>(() => ((GoalPriority)999).ToDisplayName());

    [Fact]
    public void GoalStatus_InvalidValue_ThrowsInvalidOperationException()
        => Assert.Throws<InvalidOperationException>(() => ((GoalStatus)999).ToDisplayName());

    [Fact]
    public void GoalScope_Patch_ReturnsPatch()
        => Assert.Equal("Patch", GoalScope.Patch.ToDisplayName());

    [Fact]
    public void GoalScope_Feature_ReturnsFeature()
        => Assert.Equal("Feature", GoalScope.Feature.ToDisplayName());

    [Fact]
    public void GoalScope_Breaking_ReturnsBreaking()
        => Assert.Equal("Breaking", GoalScope.Breaking.ToDisplayName());

    [Fact]
    public void GoalScope_InvalidValue_ThrowsInvalidOperationException()
        => Assert.Throws<InvalidOperationException>(() => ((GoalScope)999).ToDisplayName());
}

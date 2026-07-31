using CopilotHive.Orchestration;
using CopilotHive.Services;

namespace CopilotHive.Tests;

/// <summary>
/// Boundary tests for the SupportsVision flag at the catalog boundary:
/// <list type="bullet">
/// <item><see cref="SubAgentModelEntry"/> has a non-nullable <c>SupportsVision</c> (default false).</item>
/// <item><see cref="SubAgentModelDto"/> has a non-nullable <c>SupportsVision</c> (default false).</item>
/// </list>
/// These verify that the nullable config flag is resolved to <c>false</c> only at the catalog boundary,
/// never as a side-effect of the merge.
/// </summary>
public sealed class SupportsVisionBoundaryTests
{
    [Fact]
    public void SubAgentModelEntry_SupportsVision_DefaultsToFalse()
    {
        var entry = new SubAgentModelEntry("model-a", 128_000, null);
        Assert.False(entry.SupportsVision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SubAgentModelEntry_SupportsVision_ExplicitValuePreserved(bool vision)
    {
        var entry = new SubAgentModelEntry("model-a", 128_000, null, vision);
        Assert.Equal(vision, entry.SupportsVision);
    }

    [Fact]
    public void SubAgentModelDto_SupportsVision_DefaultsToFalse()
    {
        var dto = new SubAgentModelDto { Id = "model-a" };
        Assert.False(dto.SupportsVision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SubAgentModelDto_SupportsVision_ExplicitValuePreserved(bool vision)
    {
        var dto = new SubAgentModelDto { Id = "model-a", SupportsVision = vision };
        Assert.Equal(vision, dto.SupportsVision);
    }
}
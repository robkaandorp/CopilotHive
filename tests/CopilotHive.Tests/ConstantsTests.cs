using Xunit;

namespace CopilotHive.Tests;

/// <summary>Tests for <see cref="Constants"/>.</summary>
public sealed class ConstantsTests
{
    /// <summary>Verifies that <see cref="Constants.OrchestratorVersion"/> equals "1.0.0".</summary>
    [Fact]
    public void OrchestratorVersion_IsExpectedValue()
    {
        Assert.Equal("1.0.0", Constants.OrchestratorVersion);
    }
}

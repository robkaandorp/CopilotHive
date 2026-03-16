using CopilotHive.Services;

namespace CopilotHive.Tests;

/// <summary>
/// Tests verifying the constant values defined in <see cref="CleanupDefaults"/>.
/// </summary>
public class CleanupDefaultsTests
{
    /// <summary>
    /// Verifies that <see cref="CleanupDefaults.CleanupIntervalSeconds"/> has the expected value.
    /// </summary>
    [Fact]
    public void CleanupDefaults_CleanupIntervalSeconds_HasExpectedValue()
    {
        Assert.Equal(60, CleanupDefaults.CleanupIntervalSeconds);
    }

    /// <summary>
    /// Verifies that <see cref="CleanupDefaults.StaleTimeoutMinutes"/> has the expected value.
    /// </summary>
    [Fact]
    public void CleanupDefaults_StaleTimeoutMinutes_HasExpectedValue()
    {
        Assert.Equal(2, CleanupDefaults.StaleTimeoutMinutes);
    }
}

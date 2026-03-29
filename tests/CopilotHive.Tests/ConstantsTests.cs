using System.Reflection;
using Xunit;

namespace CopilotHive.Tests;

/// <summary>Tests for <see cref="Constants"/> and assembly metadata utilities.</summary>
public sealed class ConstantsTests
{
    /// <summary>
    /// Verifies that the assembly informational version is not null or empty,
    /// confirming that version metadata is present in the build output.
    /// </summary>
    [Fact]
    public void AssemblyInformationalVersion_IsNotNullOrEmpty()
    {
        var version = Assembly.GetAssembly(typeof(Constants))!
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        Assert.False(string.IsNullOrEmpty(version),
            "AssemblyInformationalVersionAttribute should be present and non-empty.");
    }

    /// <summary>
    /// Verifies that <see cref="CopilotHive.Services.VersionHelper.InformationalVersion"/>
    /// returns a non-null, non-empty string at runtime.
    /// </summary>
    [Fact]
    public void VersionHelper_InformationalVersion_IsNotNullOrEmpty()
    {
        var version = CopilotHive.Services.VersionHelper.InformationalVersion;
        Assert.False(string.IsNullOrEmpty(version),
            "VersionHelper.InformationalVersion must not be null or empty.");
    }
}

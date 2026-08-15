using System.Xml.Linq;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Verifies that the SharpCoder package reference is pinned to 0.16.1, which is required for the
/// agent-level <c>CodingAgent.SubAgentChanged</c> event used by the sub-agent panel, for the
/// <c>SubAgentModelInfo</c> overload carrying the informational <c>supportsVision</c> flag, and for
/// the internal <c>ImageLoader.MaxTotalBytes</c> limit mirrored by
/// <see cref="CopilotHive.Services.ComposerAttachmentService"/>.
/// </summary>
public sealed class SharpCoderPackageVersionTests
{
    [Fact]
    public void SharpCoder_PackageReference_IsVersion0_16_1()
    {
        // Walk up from the test bin directory to the repository root.
        var repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var propsFile = Path.Combine(repoRoot, "Directory.Packages.props");

        Assert.True(File.Exists(propsFile),
            $"Directory.Packages.props not found at {propsFile}");

        var doc = XDocument.Load(propsFile);
        // PackageVersion entries are nested inside ItemGroup, so use Descendants.
        var sharpCoder = doc.Root!.Descendants()
            .FirstOrDefault(e => e.Name == "PackageVersion"
                && e.Attribute("Include")?.Value == "SharpCoder");

        Assert.NotNull(sharpCoder);
        var version = sharpCoder!.Attribute("Version")?.Value;
        Assert.Equal("0.16.1", version);
    }
}

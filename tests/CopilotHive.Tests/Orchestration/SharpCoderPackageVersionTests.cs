using System.Xml.Linq;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Verifies that the SharpCoder package reference is pinned to 0.17.0, which is required for the
/// agent-level <c>CodingAgent.SubAgentChanged</c> event used by the sub-agent panel, for the
/// <c>SubAgentModelInfo</c> overload carrying the informational <c>supportsVision</c> flag, and for
/// the internal <c>ImageLoader.MaxTotalBytes</c> limit mirrored by
/// <see cref="CopilotHive.Services.ComposerAttachmentService"/>.
/// </summary>
public sealed class SharpCoderPackageVersionTests
{
    [Fact]
    public void SharpCoder_PackageReference_IsVersion0_17_0()
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
        Assert.Equal("0.17.0", version);
    }

    /// <summary>
    /// <c>SharpCoder.Providers</c> must be pinned to 0.18.0, the first version exposing
    /// <c>ChatClientFactory.IsTokenAvailable</c> — the single source of truth for GitHub Copilot
    /// token availability used by <see cref="CopilotHive.Orchestration.LlmConnectionCoordinator"/>
    /// to gate the Composer's startup connect.
    /// </summary>
    [Fact]
    public void SharpCoderProviders_PackageReference_IsVersion0_18_0()
    {
        var repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var propsFile = Path.Combine(repoRoot, "Directory.Packages.props");

        Assert.True(File.Exists(propsFile), $"Directory.Packages.props not found at {propsFile}");

        var doc = XDocument.Load(propsFile);
        var providers = doc.Root!.Descendants()
            .FirstOrDefault(e => e.Name == "PackageVersion"
                && e.Attribute("Include")?.Value == "SharpCoder.Providers");

        Assert.NotNull(providers);
        Assert.Equal("0.18.0", providers!.Attribute("Version")?.Value);
    }

    /// <summary>
    /// The restored <c>SharpCoder.Providers</c> assembly actually exposes the availability API the
    /// coordinator depends on — a version bump that silently lost it must fail here.
    /// </summary>
    [Fact]
    public void ChatClientFactory_ExposesIsTokenAvailable()
    {
        var method = typeof(SharpCoder.Providers.ChatClientFactory)
            .GetMethod("IsTokenAvailable", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, Type.EmptyTypes);

        Assert.NotNull(method);
        Assert.Equal(typeof(bool), method!.ReturnType);
    }
}

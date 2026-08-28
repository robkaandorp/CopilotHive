using System.Text.RegularExpressions;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Tests for the release-metadata consistency invariant: the version declared in
/// <c>Directory.Build.props</c> (<c>&lt;VersionPrefix&gt;</c>) and the top
/// <c>## [x.y.z]</c> heading in <c>CHANGELOG.md</c> must always agree.
/// <para>
/// The version bump and the CHANGELOG entry must be committed atomically — the consistency
/// test must never tolerate a mismatch. This documents the procedural phase-assignment rule
/// (version bump + CHANGELOG entry in one phase, one commit) that prevents the red window
/// between a version bump and the CHANGELOG entry.
/// </para>
/// </summary>
public sealed class ReleaseMetadataTests
{
    /// <summary>
    /// Asserts the release-metadata consistency invariant: the <c>&lt;VersionPrefix&gt;</c> in
    /// <c>Directory.Build.props</c> equals the top <c>## [x.y.z]</c> heading in
    /// <c>CHANGELOG.md</c>, compared with <see cref="StringComparison.Ordinal"/>.
    /// </summary>
    [Fact]
    public void Changelog_TopHeading_Matches_DirectoryBuildProps_VersionPrefix()
    {
        var propsText = File.ReadAllText(DirectoryBuildPropsPath());
        var propsMatch = Regex.Match(propsText, @"<VersionPrefix>([^<]+)</VersionPrefix>");
        Assert.True(propsMatch.Success,
            $"{DirectoryBuildPropsPath()} must contain a non-empty <VersionPrefix> element.");

        var propsVersion = propsMatch.Groups[1].Value;

        var changelogText = File.ReadAllText(ChangelogPath());
        var headingMatch = Regex.Match(changelogText, @"^## \[([^\]]+)\]", RegexOptions.Multiline);
        Assert.True(headingMatch.Success,
            $"{ChangelogPath()} must contain at least one '## [<version>]' heading.");

        var headingVersion = headingMatch.Groups[1].Value;

        Assert.True(
            string.Equals(propsVersion, headingVersion, StringComparison.Ordinal),
            $"Directory.Build.props <VersionPrefix> is '{propsVersion}' but the top CHANGELOG.md heading '## [...]' is '{headingVersion}'; they must match.");
    }

    /// <summary>
    /// Resolves the CHANGELOG.md path relative to the test assembly location, walking up to the
    /// repository root.
    /// </summary>
    private static string ChangelogPath()
    {
        var dir = AppContext.BaseDirectory;
        // Walk up until we find CHANGELOG.md.
        while (dir is not null && !File.Exists(Path.Combine(dir, "CHANGELOG.md")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? AppContext.BaseDirectory, "CHANGELOG.md");
    }

    /// <summary>
    /// Resolves the Directory.Build.props path relative to the test assembly location, walking up
    /// to the repository root.
    /// </summary>
    private static string DirectoryBuildPropsPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Directory.Build.props")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? AppContext.BaseDirectory, "Directory.Build.props");
    }
}

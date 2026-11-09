using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// A containment check is useless if both sides have been remapped onto the same WRONG directory.
/// No hard links, link replacements, or symlinks are involved here: these are distinct Linux names.
/// Canonical-root identity has no public seam; right-file/right-content and a sibling-only negative
/// pin observe that identity through the production read itself.
/// </summary>
public sealed class ReviewRepairBackslashRootTests : IDisposable
{
    private const string Actual = "actual configured root payload";
    private const string Outside = "outside sibling payload must not be disclosed";
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "review-root-" + Guid.NewGuid().ToString("N"));
    private readonly CopilotHiveDbContext _db = CopilotHiveDbContext.CreateInMemory();

    private (string Root, string Sibling, Composer Tool) CreateAndProveDistinctDirectories()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("A literal backslash is a Linux filename character.");
        Assert.Equal('/', Path.DirectorySeparatorChar);
        Assert.Equal('/', Path.AltDirectorySeparatorChar);
        var root = Path.GetFullPath(Path.Combine(_parent, @"repo\name"));
        var sibling = Path.GetFullPath(Path.Combine(_parent, "repo", "name"));
        Assert.NotEqual(root, sibling);
        Assert.False(sibling.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(root, "note.txt"), Actual);
        File.WriteAllText(Path.Combine(sibling, "note.txt"), Outside);
        File.WriteAllText(Path.Combine(sibling, "sibling-only.txt"), Outside);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, ".git", "config"), "metadata secret");
        // Premises BEFORE the production read: real distinct directory entries with opposite data,
        // not an assumed escaped spelling or a broken/missing sibling fixture.
        Assert.True(Directory.Exists(root));
        Assert.True(Directory.Exists(sibling));
        Assert.Equal(@"repo\name", new DirectoryInfo(root).Name);
        Assert.Equal("name", new DirectoryInfo(sibling).Name);
        Assert.Null(new DirectoryInfo(root).LinkTarget);
        Assert.Null(new DirectoryInfo(sibling).LinkTarget);
        Assert.Equal(Actual, File.ReadAllText(Path.Combine(root, "note.txt")));
        Assert.Equal(Outside, File.ReadAllText(Path.Combine(sibling, "note.txt")));
        Assert.Equal(Outside, File.ReadAllText(Path.Combine(sibling, "sibling-only.txt")));
        Assert.False(File.Exists(Path.Combine(root, "sibling-only.txt")));
        var manager = new ConfigRepoManager("https://github.com/org/config.git", root);
        Assert.Equal(root, manager.LocalPath);
        return (root, sibling, new Composer("test-model", NullLogger<Composer>.Instance,
            new GoalStore(_db, NullLogger<GoalStore>.Instance), stateDir: _parent, configRepo: manager));
    }

    [Fact]
    public async Task LiteralBackslashRoot_ReturnsActualDirectoryContent_NotMappedSiblingContent()
    {
        var (_, _, tool) = CreateAndProveDistinctDirectories();
        var result = await tool.ReadConfigFileAsync("note.txt", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("1: " + Actual, result);
        Assert.DoesNotContain(Outside, result);
    }

    [Fact]
    public async Task LiteralBackslashRoot_SiblingOnlyFile_IsNotFound_NotDisclosed()
    {
        var (root, sibling, tool) = CreateAndProveDistinctDirectories();
        var outsideTarget = Path.GetFullPath(Path.Combine(sibling, "sibling-only.txt"));
        Assert.False(outsideTarget.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.Equal(Outside, File.ReadAllText(outsideTarget));
        var result = await tool.ReadConfigFileAsync("sibling-only.txt", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("❌ File 'sibling-only.txt' not found in config repo.", result);
        Assert.DoesNotContain(Outside, result);
    }

    [Theory]
    [InlineData(".git/config")]
    [InlineData(@".GIt\config")]
    [InlineData("../repo/name/sibling-only.txt")]
    public async Task LiteralBackslashRoot_LexicalMetadataAndOutsideTraversal_StillDenied(string path)
    {
        var (_, _, tool) = CreateAndProveDistinctDirectories();
        var result = await tool.ReadConfigFileAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.StartsWith("❌ Path '", result);
        Assert.EndsWith("Access denied.", result);
        Assert.DoesNotContain(Outside, result);
        Assert.DoesNotContain("metadata secret", result);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestHelpers.ForceDeleteDirectory(_parent);
    }
}

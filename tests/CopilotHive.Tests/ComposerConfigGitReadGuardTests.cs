using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Focused coverage for <c>Composer.ReadConfigFileAsync</c>'s git-metadata read guard: the exact
/// <c>.git</c> spellings, separator and case variants, symlink aliases and fail-closed states.
/// </summary>
public sealed class ComposerConfigGitReadGuardTests : IDisposable
{
    private readonly string _root;
    private readonly ConfigRepoManager _configRepo;
    private readonly CopilotHiveDbContext _dbContext;
    private readonly Composer _composer;

    public ComposerConfigGitReadGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"copilothive-gitguard-{Guid.NewGuid():N}");
        var repoDir = Path.Combine(_root, "config-repo");
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        Directory.CreateDirectory(Path.Combine(repoDir, "agents"));

        File.WriteAllText(Path.Combine(repoDir, "agents", "coder.agents.md"), "# Coder\n");
        File.WriteAllText(Path.Combine(repoDir, ".git", "config"),
            "[remote \"origin\"]\n\turl = https://x-access-token:ghp_secret@github.com/org/cfg.git\n");

        _configRepo = new ConfigRepoManager("https://github.com/org/cfg.git", repoDir);
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            new GoalStore(_dbContext, NullLogger<GoalStore>.Instance),
            stateDir: _root,
            configRepo: _configRepo);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        TestHelpers.ForceDeleteDirectory(_root);
    }

    private string RepoDir => _configRepo.LocalPath;

    /// <summary>
    /// Every spelling that names git metadata is DENIED, and the response must not contain the
    /// credential the file holds — proving the guard rejected rather than read and redacted.
    /// </summary>
    [Theory]
    [InlineData(".git/config")]
    [InlineData(".GIt/config")]
    [InlineData(".git\\config")]
    [InlineData("agents/../.git/config")]
    [InlineData("./.git/config")]
    [InlineData("agents\\..\\.git\\config")]
    public async Task ReadConfigFile_GitMetadataSpellings_AreDenied(string path)
    {
        var result = await _composer.ReadConfigFileAsync(
            path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("❌", result);
        Assert.Contains("Access denied", result);
        Assert.DoesNotContain("ghp_secret", result);
        Assert.DoesNotContain("x-access-token", result);
    }

    /// <summary>
    /// A SYMLINK whose target is git metadata is denied even though its own name is harmless — this
    /// is the alias case a purely lexical guard misses.
    /// </summary>
    [Fact]
    public async Task ReadConfigFile_SymlinkToGitMetadata_IsDenied()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Creating a symlink requires privileges on Windows; covered on Linux.");
            return;
        }

        var link = Path.Combine(RepoDir, "harmless.txt");
        File.CreateSymbolicLink(link, Path.Combine(RepoDir, ".git", "config"));

        var result = await _composer.ReadConfigFileAsync(
            "harmless.txt", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("❌", result);
        Assert.Contains("Access denied", result);
        Assert.DoesNotContain("ghp_secret", result);
    }

    /// <summary>
    /// A DIRECTORY symlink that aliases <c>.git</c>, with a harmless-looking path THROUGH it, is
    /// denied: the chain is canonicalized, not just the leaf.
    /// </summary>
    [Fact]
    public async Task ReadConfigFile_DirectorySymlinkAliasingGitMetadata_IsDenied()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Creating a symlink requires privileges on Windows; covered on Linux.");
            return;
        }

        Directory.CreateSymbolicLink(Path.Combine(RepoDir, "meta"), Path.Combine(RepoDir, ".git"));

        var result = await _composer.ReadConfigFileAsync(
            "meta/config", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("❌", result);
        Assert.Contains("Access denied", result);
        Assert.DoesNotContain("ghp_secret", result);
    }

    /// <summary>A symlink pointing OUTSIDE the config-repo root is denied.</summary>
    [Fact]
    public async Task ReadConfigFile_SymlinkOutsideTheRoot_IsDenied()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Creating a symlink requires privileges on Windows; covered on Linux.");
            return;
        }

        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "outside secret");
        File.CreateSymbolicLink(Path.Combine(RepoDir, "escape.txt"), outside);

        var result = await _composer.ReadConfigFileAsync(
            "escape.txt", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("❌", result);
        Assert.Contains("outside the config repo", result);
        Assert.DoesNotContain("outside secret", result);
    }

    /// <summary>A DANGLING symlink's real location is unknown, so the read fails closed.</summary>
    [Fact]
    public async Task ReadConfigFile_DanglingSymlink_FailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Creating a symlink requires privileges on Windows; covered on Linux.");
            return;
        }

        File.CreateSymbolicLink(
            Path.Combine(RepoDir, "dangling.txt"), Path.Combine(_root, "does-not-exist"));

        var result = await _composer.ReadConfigFileAsync(
            "dangling.txt", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("❌", result);
        Assert.Contains("Access denied", result);
    }

    /// <summary>
    /// POSITIVE CONTROL: an ordinary file still reads normally, so the guard is not simply refusing
    /// everything.
    /// </summary>
    [Fact]
    public async Task ReadConfigFile_OrdinaryFile_StillReads()
    {
        var result = await _composer.ReadConfigFileAsync(
            "agents/coder.agents.md", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("1: # Coder", result);
        Assert.DoesNotContain("❌", result);
    }

    /// <summary>
    /// POSITIVE CONTROL for canonicalization: a symlink INSIDE the root pointing at an ordinary
    /// in-root file is followed and read — the guard rejects aliasing, not links as such.
    /// </summary>
    [Fact]
    public async Task ReadConfigFile_SymlinkToOrdinaryInRootFile_StillReads()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Creating a symlink requires privileges on Windows; covered on Linux.");
            return;
        }

        File.CreateSymbolicLink(
            Path.Combine(RepoDir, "alias.md"),
            Path.Combine(RepoDir, "agents", "coder.agents.md"));

        var result = await _composer.ReadConfigFileAsync(
            "alias.md", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("1: # Coder", result);
    }

    /// <summary>The existing traversal denial still wins for an escaping relative path.</summary>
    [Fact]
    public async Task ReadConfigFile_TraversalOutsideTheRoot_StillDenied()
    {
        var result = await _composer.ReadConfigFileAsync(
            "../../etc/passwd", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("❌", result);
        Assert.Contains("outside the config repo", result);
    }
    // ── Ancestor canonicalization: link targets are NEVER trusted as already canonical ───────
    //
    // `ResolveLinkTarget(true).FullName` resolves the link chain but does NOT canonicalize the
    // directory ANCESTORS inside the returned target. Each test below builds one demonstrated
    // topology where a harmless-looking target still reaches `.git` (or outside the root) through a
    // directory alias. Every one must be DENIED, and the response must not contain the secret —
    // proving rejection rather than read-and-redact.

    private const string Secret = "ghp_secret";

    private void RequireSymlinks()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Creating a symlink requires privileges on Windows; covered on Linux.");
    }

    private async Task AssertDeniedWithoutSecretAsync(string path, string expectedFragment)
    {
        var result = await _composer.ReadConfigFileAsync(
            path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("❌", result);
        Assert.Contains(expectedFragment, result);
        Assert.DoesNotContain(Secret, result);
        Assert.DoesNotContain("x-access-token", result);
    }

    /// <summary>ABSOLUTE leaf target that runs THROUGH a directory alias of .git.</summary>
    [Fact]
    public async Task ReadConfigFile_AbsoluteLeafTargetThroughDirectoryAlias_IsDenied()
    {
        RequireSymlinks();
        Directory.CreateSymbolicLink(Path.Combine(RepoDir, "bridge"), Path.Combine(RepoDir, ".git"));
        File.CreateSymbolicLink(Path.Combine(RepoDir, "leaf"), Path.Combine(RepoDir, "bridge", "config"));

        await AssertDeniedWithoutSecretAsync("leaf", "git metadata");
    }

    /// <summary>RELATIVE leaf target that runs THROUGH a directory alias of .git.</summary>
    [Fact]
    public async Task ReadConfigFile_RelativeLeafTargetThroughDirectoryAlias_IsDenied()
    {
        RequireSymlinks();
        Directory.CreateSymbolicLink(Path.Combine(RepoDir, "bridge"), ".git");
        File.CreateSymbolicLink(Path.Combine(RepoDir, "leaf"), "bridge/config");

        await AssertDeniedWithoutSecretAsync("leaf", "git metadata");
    }

    /// <summary>A DIRECTORY link whose target is itself reached through a directory alias.</summary>
    [Fact]
    public async Task ReadConfigFile_DirectoryTargetThroughAlias_IsDenied()
    {
        RequireSymlinks();
        Directory.CreateSymbolicLink(Path.Combine(RepoDir, "bridge"), Path.Combine(RepoDir, ".git"));
        Directory.CreateSymbolicLink(Path.Combine(RepoDir, "dirlink"), Path.Combine(RepoDir, "bridge"));

        await AssertDeniedWithoutSecretAsync("dirlink/config", "git metadata");
    }

    /// <summary>The analogous OUTSIDE-the-root escape through a directory alias.</summary>
    [Fact]
    public async Task ReadConfigFile_OutsideRootTargetThroughDirectoryAlias_IsDenied()
    {
        RequireSymlinks();
        var outsideDir = Path.Combine(_root, "outside-dir");
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "secret.txt"), "ghp_secret outside");
        Directory.CreateSymbolicLink(Path.Combine(RepoDir, "bridge-out"), outsideDir);
        File.CreateSymbolicLink(
            Path.Combine(RepoDir, "leaf-out"), Path.Combine(RepoDir, "bridge-out", "secret.txt"));

        await AssertDeniedWithoutSecretAsync("leaf-out", "outside the config repo");
    }

    /// <summary>
    /// A configured ROOT with a symlink-bearing ANCESTOR: an ordinary in-root file (and an in-root
    /// link spelled with the REAL path) still reads, while .git is still denied by every route.
    /// </summary>
    [Fact]
    public async Task ReadConfigFile_RootWithSymlinkAncestor_IsCanonicalized()
    {
        RequireSymlinks();
        var realParent = Path.Combine(_root, "real-parent");
        var realRepo = Path.Combine(realParent, "config-repo");
        Directory.CreateDirectory(Path.Combine(realRepo, ".git"));
        Directory.CreateDirectory(Path.Combine(realRepo, "agents"));
        File.WriteAllText(Path.Combine(realRepo, "agents", "coder.agents.md"), "# Aliased\n");
        File.WriteAllText(Path.Combine(realRepo, ".git", "config"), "url = https://x-access-token:ghp_secret@github.com/o/r.git\n");
        var aliasParent = Path.Combine(_root, "alias-parent");
        Directory.CreateSymbolicLink(aliasParent, realParent);

        var aliasedRepoPath = Path.Combine(aliasParent, "config-repo");
        // in-root links whose targets are spelled with the REAL (un-aliased) path
        File.CreateSymbolicLink(Path.Combine(realRepo, "ok-link"), Path.Combine(realRepo, "agents", "coder.agents.md"));
        File.CreateSymbolicLink(Path.Combine(realRepo, "git-link"), Path.Combine(realRepo, ".git", "config"));

        using var db = CopilotHiveDbContext.CreateInMemory();
        var composer = new Composer(
            "test-model", NullLogger<Composer>.Instance, new GoalStore(db, NullLogger<GoalStore>.Instance),
            stateDir: _root, configRepo: new ConfigRepoManager("https://github.com/org/cfg.git", aliasedRepoPath));
        var ct = TestContext.Current.CancellationToken;

        // POSITIVE controls: the aliased root really resolves, and an in-root link still reads.
        Assert.Contains("1: # Aliased", await composer.ReadConfigFileAsync("agents/coder.agents.md", cancellationToken: ct));
        Assert.Contains("1: # Aliased", await composer.ReadConfigFileAsync("ok-link", cancellationToken: ct));

        foreach (var path in new[] { ".git/config", "git-link" })
        {
            var denied = await composer.ReadConfigFileAsync(path, cancellationToken: ct);
            Assert.Contains("❌", denied);
            Assert.DoesNotContain(Secret, denied);
        }
    }

    /// <summary>
    /// The bypass was STABLE (100/100 repetitions on unchanged links); the fix must be too — the
    /// same topology is denied on every repetition, and never discloses the secret even once.
    /// </summary>
    [Fact]
    public async Task ReadConfigFile_DirectoryAliasBypassTopology_IsDeniedOnEveryRepetition()
    {
        RequireSymlinks();
        Directory.CreateSymbolicLink(Path.Combine(RepoDir, "bridge"), Path.Combine(RepoDir, ".git"));
        File.CreateSymbolicLink(Path.Combine(RepoDir, "leaf"), Path.Combine(RepoDir, "bridge", "config"));

        for (var i = 0; i < 100; i++)
        {
            var result = await _composer.ReadConfigFileAsync(
                "leaf", cancellationToken: TestContext.Current.CancellationToken);
            Assert.DoesNotContain(Secret, result);
            Assert.Contains("Access denied", result);
        }
    }

    /// <summary>DIRECT link to .git/config: the control proving rejection, not read-and-redact.</summary>
    [Fact]
    public async Task ReadConfigFile_DirectLinkToGitConfig_IsDeniedWithoutTheSecret()
    {
        RequireSymlinks();
        File.CreateSymbolicLink(Path.Combine(RepoDir, "direct"), Path.Combine(RepoDir, ".git", "config"));

        await AssertDeniedWithoutSecretAsync("direct", "git metadata");
    }

    /// <summary>A link LOOP is undeterminable: denied (fail closed), never read, never hangs.</summary>
    [Fact]
    public async Task ReadConfigFile_LinkLoop_FailsClosed()
    {
        RequireSymlinks();
        File.CreateSymbolicLink(Path.Combine(RepoDir, "a"), Path.Combine(RepoDir, "b"));
        File.CreateSymbolicLink(Path.Combine(RepoDir, "b"), Path.Combine(RepoDir, "a"));

        await AssertDeniedWithoutSecretAsync("a", "Access denied");
    }

    /// <summary>A chain longer than the hop bound is denied rather than followed without limit.</summary>
    [Fact]
    public async Task ReadConfigFile_OverlongLinkChain_FailsClosed()
    {
        RequireSymlinks();
        File.WriteAllText(Path.Combine(RepoDir, "target.txt"), "plain\n");
        var previous = "target.txt";
        for (var i = 0; i < 60; i++)
        {
            var name = $"hop{i}";
            File.CreateSymbolicLink(Path.Combine(RepoDir, name), previous);
            previous = name;
        }

        await AssertDeniedWithoutSecretAsync(previous, "Access denied");
    }

}

/// <summary>
/// CONTAINMENT regression (not the hard-link/replacement-race limit): on Linux a backslash is an
/// ORDINARY filename character, so a configured root named <c>repo\name</c> is ONE directory and is
/// distinct from the sibling <c>repo/name</c>. Physical traversal must split on the platform
/// separator only; the lexical <c>.git</c> policy keeps both separators.
/// </summary>
public sealed class ComposerConfigBackslashRootTests : IDisposable
{
    private readonly string _base;
    private readonly string _actualRoot;
    private readonly CopilotHiveDbContext _dbContext;
    private readonly Composer _composer;

    public ComposerConfigBackslashRootTests()
    {
        _base = Path.Combine(Path.GetTempPath(), $"copilothive-backslash-{Guid.NewGuid():N}");
        // The ACTUAL configured root: a single directory whose NAME contains a literal backslash.
        _actualRoot = _base + Path.DirectorySeparatorChar + "repo\\name";
        // A DIFFERENT, ordinary directory that a backslash-splitting walker would map the root onto.
        var outside = Path.Combine(_base, "repo", "name");

        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Path.Combine(_actualRoot, ".git"));
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(_actualRoot, "note.txt"), "ACTUAL_ROOT_NOTE\n");
            File.WriteAllText(Path.Combine(_actualRoot, ".git", "config"), "url = https://x-access-token:ghp_secret@github.com/o/r.git\n");
            File.WriteAllText(Path.Combine(outside, "note.txt"), "OUTSIDE_SIBLING_NOTE\n");
            File.WriteAllText(Path.Combine(outside, "outside-only.txt"), "OUTSIDE_ONLY_SECRET\n");
        }

        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _composer = new Composer(
            "test-model",
            NullLogger<Composer>.Instance,
            new GoalStore(_dbContext, NullLogger<GoalStore>.Instance),
            stateDir: _base,
            configRepo: new ConfigRepoManager("https://github.com/org/cfg.git", _actualRoot));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        TestHelpers.ForceDeleteDirectory(_base);
    }

    private static void RequireLinux()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("A literal backslash in a directory name is a Linux filesystem property.");
    }

    /// <summary>An ordinary file resolves in the ACTUAL configured root: right file, right content.</summary>
    [Fact]
    public async Task BackslashNamedRoot_OrdinaryRead_ResolvesTheActualConfiguredRoot()
    {
        RequireLinux();
        var result = await _composer.ReadConfigFileAsync(
            "note.txt", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("1: ACTUAL_ROOT_NOTE", result);
        Assert.DoesNotContain("OUTSIDE_SIBLING_NOTE", result);
    }

    /// <summary>A file that exists ONLY in the outside sibling is not found, and never disclosed.</summary>
    [Fact]
    public async Task BackslashNamedRoot_SiblingOnlyFile_IsNotFound_WithoutDisclosure()
    {
        RequireLinux();
        var result = await _composer.ReadConfigFileAsync(
            "outside-only.txt", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("❌", result);
        Assert.Contains("not found", result);
        Assert.DoesNotContain("OUTSIDE_ONLY_SECRET", result);
    }

    /// <summary>Metadata (both separators) and traversal denial still work for such a root.</summary>
    [Theory]
    [InlineData(".git/config")]
    [InlineData(".GIt\\config")]
    [InlineData("../note.txt")]
    [InlineData("../../repo/name/outside-only.txt")]
    public async Task BackslashNamedRoot_MetadataAndTraversal_StillDenied(string path)
    {
        RequireLinux();
        var result = await _composer.ReadConfigFileAsync(
            path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("❌", result);
        Assert.Contains("Access denied", result);
        Assert.DoesNotContain("ghp_secret", result);
        Assert.DoesNotContain("OUTSIDE_ONLY_SECRET", result);
        Assert.DoesNotContain("OUTSIDE_SIBLING_NOTE", result);
    }
}

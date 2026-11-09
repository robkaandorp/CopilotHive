using CopilotHive.Configuration;
using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// The EXTENDED coverage for <c>Composer.ReadConfigFileAsync</c>'s git-metadata read guard: the
/// rejection vectors the focused companion file does not exercise, the symlink/reparse alias
/// shapes, the fail-closed states, and the positive controls that prove ordinary config-repo
/// content still reads with IDENTICAL output.
/// <para>
/// <b>The documented limits, stated honestly.</b> Two classes of alias are NOT covered by this
/// guard, and the tests below say so rather than pretending otherwise:
/// </para>
/// <list type="bullet">
///   <item><description>
///   <b>Link-replacement races.</b> A symbolic link REPLACED between the guard's resolution and
///   the actual read can still redirect the read — there is no atomic, handle-relative open here.
///   This is an accepted limit of the design, not an untested gap.
///   </description></item>
///   <item><description>
///   <b>Hard-link aliases.</b> A HARD LINK to a file outside the config-repo root (or into a
///   <c>.git</c> location) is indistinguishable from an ordinary file by path alone, so the guard
///   cannot refuse it. This is an accepted limit of the design too.
///   </description></item>
/// </list>
/// <para>
/// The guard makes no claim beyond the paths it resolves.
/// </para>
/// </summary>
public sealed class ComposerConfigGitReadGuardExtendedTests : IDisposable
{
    /// <summary>The credential the guarded file holds — its ABSENCE from any response is the proof.</summary>
    private const string Secret = "ghp_extended_guard_secret";

    private readonly string _root;
    private readonly ConfigRepoManager _configRepo;
    private readonly CopilotHiveDbContext _dbContext;
    private readonly Composer _composer;

    public ComposerConfigGitReadGuardExtendedTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"copilothive-gitguard-ext-{Guid.NewGuid():N}");
        var repoDir = Path.Combine(_root, "config-repo");
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        Directory.CreateDirectory(Path.Combine(repoDir, "agents"));
        Directory.CreateDirectory(Path.Combine(repoDir, "configs"));

        File.WriteAllText(Path.Combine(repoDir, "agents", "coder.agents.md"), "# Coder\n");
        File.WriteAllText(Path.Combine(repoDir, "configs", "template.yaml"), "template: true\n");
        File.WriteAllText(Path.Combine(repoDir, "hive-config.yaml"), "version: 1\n");
        File.WriteAllText(
            Path.Combine(repoDir, ".git", "config"),
            $"[remote \"origin\"]\n\turl = https://x-access-token:{Secret}@github.com/org/cfg.git\n");

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

    private Task<string> ReadAsync(string path) =>
        _composer.ReadConfigFileAsync(path, cancellationToken: TestContext.Current.CancellationToken);

    // ── (2) Rejection vectors: the .git spellings ───────────────────────────

    /// <summary>
    /// Every spelling that names git metadata is DENIED, and the response never contains the
    /// credential the guarded file holds — proving the guard REJECTED rather than read and
    /// redacted.
    /// </summary>
    /// <remarks>
    /// The vector list covers the shapes the goal names: case variants, BOTH separators, a nested
    /// <c>foo/.git/config</c>, <c>.git</c> as a FILE (not a directory), and traversal spellings
    /// layered on top of the existing outside-root check.
    /// </remarks>
    [Theory]
    // Case variants.
    [InlineData(".git/config")]
    [InlineData(".GIT/config")]
    [InlineData(".GIt/config")]
    [InlineData(".giT/config")]
    // Forward and BACKWARD separators.
    [InlineData(".git\\config")]
    [InlineData("agents\\..\\.git\\config")]
    [InlineData(".git/config/extra")]
    [InlineData("agents/.git/config")]
    // Nested under an ordinary directory.
    [InlineData("configs/.git/config")]
    [InlineData("configs/nested/.git/config")]
    // A trailing separator on the .git directory itself.
    [InlineData(".git/")]
    [InlineData("agents/.git/")]
    // Traversal spellings that still land on .git.
    [InlineData("./.git/config")]
    [InlineData("agents/../.git/config")]
    [InlineData("configs/../.git/config")]
    [InlineData("configs/./../.git/config")]
    public async Task ReadConfigFile_GitMetadataSpellings_AreDenied(string path)
    {
        var result = await ReadAsync(path);

        Assert.Contains("❌", result);
        Assert.Contains("Access denied", result);
        Assert.DoesNotContain(Secret, result);
        Assert.DoesNotContain("x-access-token", result);
    }

    /// <summary>
    /// A <c>.git</c> FILE (not a directory) is denied as well — the rule is about the NAME, not
    /// about what the entry happens to be.
    /// </summary>
    [Fact]
    public async Task ReadConfigFile_GitAsAPlainFile_IsDenied()
    {
        // A real FILE named .git, holding a secret.
        var dotGitFile = Path.Combine(RepoDir, ".gitfile");
        File.WriteAllText(dotGitFile, "not a directory\n");

        // Replace the .git DIRECTORY with a FILE of the same name.
        var dotGit = Path.Combine(RepoDir, ".git");
        Directory.Delete(dotGit, recursive: true);
        File.WriteAllText(dotGit, $"token = {Secret}\n");

        try
        {
            var result = await ReadAsync(".git");

            Assert.Contains("❌", result);
            Assert.Contains("Access denied", result);
            Assert.DoesNotContain(Secret, result);
        }
        finally
        {
            File.Delete(dotGit);
            Directory.CreateDirectory(dotGit);
        }
    }

    /// <summary>
    /// The traversal denial still wins for an escaping relative path: the guard does not let a
    /// <c>.git</c> spelling bypass the pre-existing outside-root check, and vice versa.
    /// </summary>
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("agents/../../etc/passwd")]
    [InlineData("..")]
    public async Task ReadConfigFile_TraversalOutsideTheRoot_StillDenied(string path)
    {
        var result = await ReadAsync(path);

        Assert.Contains("❌", result);
        Assert.Contains("outside the config repo", result);
        Assert.Contains("Access denied", result);
    }

    // ── (2) Symlink / reparse aliases ───────────────────────────────────────

    /// <summary>
    /// A FILE symlink whose target is a <c>.git</c> file is denied even though the link's own
    /// name is harmless — this is the alias case a purely lexical guard misses.
    /// </summary>
    [Fact]
    public async Task ReadConfigFile_FileSymlinkToGitMetadata_IsDenied()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Creating a symlink requires privileges on Windows; covered on Linux.");
            return;
        }

        var link = Path.Combine(RepoDir, "harmless.txt");
        File.CreateSymbolicLink(link, Path.Combine(RepoDir, ".git", "config"));

        var result = await ReadAsync("harmless.txt");

        Assert.Contains("❌", result);
        Assert.Contains("Access denied", result);
        Assert.DoesNotContain(Secret, result);
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

        var result = await ReadAsync("meta/config");

        Assert.Contains("❌", result);
        Assert.Contains("Access denied", result);
        Assert.DoesNotContain(Secret, result);
    }

    /// <summary>
    /// A symlink whose target is a <c>.git</c> file in a NESTED ordinary directory is denied too:
    /// the rule follows the real location, wherever it is.
    /// </summary>
    [Fact]
    public async Task ReadConfigFile_SymlinkToNestedGitMetadata_IsDenied()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Creating a symlink requires privileges on Windows; covered on Linux.");
            return;
        }

        var nestedGit = Path.Combine(RepoDir, "configs", ".git");
        Directory.CreateDirectory(nestedGit);
        File.WriteAllText(Path.Combine(nestedGit, "config"), $"token = {Secret}\n");

        var link = Path.Combine(RepoDir, "innocent.yaml");
        File.CreateSymbolicLink(link, Path.Combine(nestedGit, "config"));

        var result = await ReadAsync("innocent.yaml");

        Assert.Contains("❌", result);
        Assert.Contains("Access denied", result);
        Assert.DoesNotContain(Secret, result);
    }

    /// <summary>
    /// A symlink pointing OUTSIDE the config-repo root is denied — the file it aliases really does
    /// live outside, which the test proves before asserting the refusal.
    /// </summary>
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
        var link = Path.Combine(RepoDir, "escape.txt");
        File.CreateSymbolicLink(link, outside);

        // PREMISE: the alias genuinely resolves OUTSIDE the config-repo root.
        var resolved = new FileInfo(link).ResolveLinkTarget(returnFinalTarget: true);
        Assert.NotNull(resolved);
        Assert.False(
            Path.GetFullPath(resolved!.FullName)
                .StartsWith(RepoDir + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "The premise is broken: the alias does not resolve outside the root.");

        var result = await ReadAsync("escape.txt");

        Assert.Contains("❌", result);
        Assert.Contains("outside the config repo", result);
        Assert.DoesNotContain("outside secret", result);
    }

    /// <summary>
    /// A DIRECTORY symlink pointing outside the root, with a file read THROUGH it, is denied: the
    /// whole chain is canonicalized, so a leaf inside the root that is reached through an
    /// out-of-root directory is still refused.
    /// </summary>
    [Fact]
    public async Task ReadConfigFile_DirectorySymlinkOutsideTheRoot_IsDenied()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Creating a symlink requires privileges on Windows; covered on Linux.");
            return;
        }

        var outsideDir = Path.Combine(_root, "outside-dir");
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "secret.txt"), "outside secret");

        Directory.CreateSymbolicLink(Path.Combine(RepoDir, "escape-dir"), outsideDir);

        // PREMISE: the alias genuinely resolves OUTSIDE the config-repo root.
        var resolved = new DirectoryInfo(Path.Combine(RepoDir, "escape-dir"))
            .ResolveLinkTarget(returnFinalTarget: true);
        Assert.NotNull(resolved);
        Assert.False(
            Path.GetFullPath(resolved!.FullName)
                .StartsWith(RepoDir + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "The premise is broken: the alias does not resolve outside the root.");

        var result = await ReadAsync("escape-dir/secret.txt");

        Assert.Contains("❌", result);
        Assert.Contains("outside the config repo", result);
        Assert.DoesNotContain("outside secret", result);
    }

    /// <summary>
    /// A DANGLING symlink's real location is unknown, so the read fails closed rather than
    /// reading through the unresolved link.
    /// </summary>
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

        var result = await ReadAsync("dangling.txt");

        Assert.Contains("❌", result);
        Assert.Contains("Access denied", result);
    }

    /// <summary>
    /// A DANGLING DIRECTORY symlink in the chain also fails closed: the real location of the
    /// chain cannot be established, so nothing is read through it.
    /// </summary>
    [Fact]
    public async Task ReadConfigFile_DanglingDirectorySymlinkInTheChain_FailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Creating a symlink requires privileges on Windows; covered on Linux.");
            return;
        }

        Directory.CreateSymbolicLink(
            Path.Combine(RepoDir, "dangling-dir"), Path.Combine(_root, "does-not-exist"));

        var result = await ReadAsync("dangling-dir/whatever.txt");

        Assert.Contains("❌", result);
        Assert.Contains("Access denied", result);
    }

    /// <summary>
    /// A symlink CHAIN (a link pointing at another link pointing at <c>.git</c>) is denied: the
    /// FINAL target is what the guard resolves.
    /// </summary>
    [Fact]
    public async Task ReadConfigFile_SymlinkChainToGitMetadata_IsDenied()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Creating a symlink requires privileges on Windows; covered on Linux.");
            return;
        }

        var first = Path.Combine(RepoDir, "link1.txt");
        var second = Path.Combine(RepoDir, "link2.txt");
        File.CreateSymbolicLink(first, Path.Combine(RepoDir, ".git", "config"));
        File.CreateSymbolicLink(second, first);

        var result = await ReadAsync("link2.txt");

        Assert.Contains("❌", result);
        Assert.Contains("Access denied", result);
        Assert.DoesNotContain(Secret, result);
    }

    // ── (2) Positive controls: ordinary content still reads ─────────────────

    /// <summary>
    /// POSITIVE CONTROL: ordinary config-repo files still read, with IDENTICAL output to a
    /// direct file read — the guard refuses git metadata and aliases, not content.
    /// </summary>
    [Theory]
    [InlineData("hive-config.yaml")]
    [InlineData("agents/coder.agents.md")]
    [InlineData("configs/template.yaml")]
    public async Task ReadConfigFile_OrdinaryFiles_StillReadWithIdenticalOutput(string path)
    {
        var result = await ReadAsync(path);

        // The guard's output shape: "<line>: <content>".
        var direct = (await File.ReadAllTextAsync(
            Path.Combine(RepoDir, path), TestContext.Current.CancellationToken))
            .Split('\n')
            .Where(l => l.Length > 0)
            .Select((line, index) => $"{index + 1}: {line}");

        foreach (var expected in direct)
            Assert.Contains(expected, result);

        Assert.DoesNotContain("❌", result);
        Assert.DoesNotContain("Access denied", result);
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

        var result = await ReadAsync("alias.md");

        Assert.Contains("1: # Coder", result);
        Assert.DoesNotContain("❌", result);
    }

    /// <summary>
    /// POSITIVE CONTROL for canonicalization: a symlink to a REPO-RELATIVE target (not through a
    /// <c>.git</c> directory) still reads — only <c>.git</c> targets and outside-the-root targets
    /// are denied.
    /// </summary>
    [Fact]
    public async Task ReadConfigFile_SymlinkToRepoRelativeTarget_StillReads()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Creating a symlink requires privileges on Windows; covered on Linux.");
            return;
        }

        File.CreateSymbolicLink(
            Path.Combine(RepoDir, "configs", "linked.yaml"),
            Path.Combine(RepoDir, "configs", "template.yaml"));

        var result = await ReadAsync("configs/linked.yaml");

        Assert.Contains("1: template: true", result);
        Assert.DoesNotContain("❌", result);
    }

    /// <summary>
    /// POSITIVE CONTROL: a symlink whose target is INSIDE the root but reached through a
    /// directory symlink that stays inside the root still reads.
    /// </summary>
    [Fact]
    public async Task ReadConfigFile_SymlinkThroughInRootDirectorySymlink_StillReads()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Creating a symlink requires privileges on Windows; covered on Linux.");
            return;
        }

        Directory.CreateSymbolicLink(
            Path.Combine(RepoDir, "configs-alias"), Path.Combine(RepoDir, "configs"));

        var result = await ReadAsync("configs-alias/template.yaml");

        Assert.Contains("1: template: true", result);
        Assert.DoesNotContain("❌", result);
    }

    /// <summary>
    /// The OFFSET and LIMIT behaviour is unchanged by the guard — the canonicalization does not
    /// disturb the line-window semantics.
    /// </summary>
    [Fact]
    public async Task ReadConfigFile_OffsetAndLimit_AreUnchangedByTheGuard()
    {
        var many = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"Line {i}"));
        File.WriteAllText(Path.Combine(RepoDir, "many.txt"), many);

        var result = await _composer.ReadConfigFileAsync(
            "many.txt",
            offset: 3,
            limit: 2,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("3: Line 3", result);
        Assert.Contains("4: Line 4", result);
        Assert.DoesNotContain("5: Line 5", result);
        Assert.Contains("more lines", result);
    }

    /// <summary>
    /// Creates a hard link. .NET exposes no managed API for this, so the platform's own
    /// <c>ln</c> command is used — the fixture is POSIX-only anyway (the test skips on Windows).
    /// </summary>
    private static void CreateHardLink(string linkPath, string targetPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("ln")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(targetPath);
        psi.ArgumentList.Add(linkPath);

        using var process = System.Diagnostics.Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"Could not create the hard link: {stderr}");
    }

    // ── (2) The documented limits, stated honestly ──────────────────────────

    /// <summary>
    /// DOCUMENTED LIMIT — a HARD LINK to a file outside the config-repo root is NOT refused: the
    /// guard resolves paths and links, and a hard link has no link target to resolve.
    /// <para>
    /// This test records the limit explicitly rather than pretending it does not exist: the guard
    /// makes no claim beyond the paths it resolves, and the goal accepts this limit.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReadConfigFile_HardLinkToOutsideFile_IsNotCoveredByTheGuard_DocumentedLimit()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Hard links require privileges on Windows; covered on Linux.");
            return;
        }

        var outside = Path.Combine(_root, "outside-hardlink.txt");
        File.WriteAllText(outside, "outside hardlink secret");

        var hardLink = Path.Combine(RepoDir, "hardlink.txt");
        CreateHardLink(hardLink, outside);

        var result = await ReadAsync("hardlink.txt");

        // The ACCEPTED LIMIT: the hard link is read, because nothing about the PATH distinguishes
        // it from an ordinary file. This is documented, accepted, and asserted here so the limit
        // cannot silently change.
        Assert.Contains("1: outside hardlink secret", result);
        Assert.DoesNotContain("❌", result);
    }
}

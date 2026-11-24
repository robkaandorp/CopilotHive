using System.Diagnostics;
using System.Text;
using CopilotHive.Git;
using CopilotHive.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CopilotHive.Tests.Orchestration;

public sealed partial class ComposerToolTests
{
    private static CancellationToken RedactionCt => TestContext.Current.CancellationToken;
    private const string FakeValue = "FAKE_GIT_SENTINEL";

    private sealed class GitRedactionFixture(string root, string repo, Composer composer, RoutingRepoManager manager) : IDisposable
    {
        public string Root { get; } = root;
        public string Repo { get; } = repo;
        public Composer Composer { get; } = composer;
        public RoutingRepoManager Manager { get; } = manager;
        public void Dispose() => TestHelpers.ForceDeleteDirectory(Root);
    }

    private GitRedactionFixture NewGitRedactionFixture(string? apiKey = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "git-output-" + Guid.NewGuid().ToString("N"));
        try
        {
            InitTempGitRepo(root);
            var repo = Path.Combine(root, "repos", "test-repo");
            var manager = new RoutingRepoManager(Path.Combine(root, "repos"));
            var composer = new Composer("test-model", NullLogger<Composer>.Instance, _store,
                repoManager: manager, stateDir: root, ollamaApiKey: apiKey);
            return new GitRedactionFixture(root, repo, composer, manager);
        }
        catch
        {
            TestHelpers.ForceDeleteDirectory(root);
            throw;
        }
    }

    private static async Task<(int ExitCode, string Out, string Error)> RawRedactionGit(string repo, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("commit.gpgsign=false");
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(RedactionCt);
        var stderr = process.StandardError.ReadToEndAsync(RedactionCt);
        await Task.WhenAll(stdout, stderr);
        await process.WaitForExitAsync(RedactionCt);
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    private static async Task<string> CheckedRedactionGit(string repo, params string[] args)
    {
        var result = await RawRedactionGit(repo, args);
        // Do not echo raw process output or arguments: the fixture deliberately holds sentinels.
        Assert.Equal(0, result.ExitCode);
        return result.Out.TrimEnd('\r', '\n');
    }

    private static void NoSentinel(string result, params string[] values)
    {
        foreach (var value in values)
            Assert.False(result.Contains(value, StringComparison.Ordinal), "Synthetic credential reached model-visible Git output.");
    }

    /// <summary>
    /// Returns the leading object-name token of the model-visible blame output, ignoring only the
    /// COMPLETE Git colour decoration directly before it and the optional boundary caret.
    /// </summary>
    /// <remarks>
    /// Ordinary <c>git blame</c> prints <c>&lt;abbreviated-name&gt; (author date lineno) content</c>.
    /// Git can colour the name (<c>color.blame=always</c>, notably <c>blame.coloring=repeatedLines</c>
    /// and <c>highlightRecent</c>) by emitting a complete SGR sequence (<c>ESC '[' digits/';' 'm'</c>)
    /// BEFORE it, and prefixes the boundary (root) commit's name with <c>^</c> unless
    /// <c>blame.showRoot</c> is set; observed shapes are <c>name</c>, <c>^name</c>, <c>ESC[31mname</c>
    /// and <c>ESC[31m^name</c>, so SGR sequences are skipped before the caret. Nothing else is removed:
    /// the token ends at the first space or newline, so an incomplete or non-SGR escape stays in the
    /// returned token (which then cannot be a prefix of the commit name), and a token is never joined
    /// from fragments or borrowed from a later line. A bare marker leaves an empty name.
    /// </remarks>
    private static string BlameObjectName(string output)
    {
        var index = 0;
        while (TryConsumeSgr(output, ref index))
        {
        }
        if (index < output.Length && output[index] == '^')
            index++;
        var end = output.IndexOfAny([' ', '\n'], index);
        return end < 0 ? output[index..] : output[index..end];
    }

    /// <summary>
    /// Advances <paramref name="index"/> past ONE complete SGR colour sequence
    /// (<c>ESC '[' (digit | ';')* 'm'</c>) and returns <see langword="true"/>; leaves it unchanged and
    /// returns <see langword="false"/> for anything else, including an unterminated sequence, a bare
    /// ESC, or a CSI whose final byte is not <c>m</c>.
    /// </summary>
    private static bool TryConsumeSgr(string text, ref int index)
    {
        if (index + 1 >= text.Length || text[index] != '\u001b' || text[index + 1] != '[')
            return false;
        var cursor = index + 2;
        while (cursor < text.Length && (text[cursor] is (>= '0' and <= '9') or ';'))
            cursor++;
        if (cursor >= text.Length || text[cursor] != 'm')
            return false;
        index = cursor + 1;
        return true;
    }

    /// <summary>
    /// Proves the leading object name of the model-visible blame output is attributed to the commit
    /// <paramref name="sha"/> that <c>git rev-parse HEAD</c> already reported for this fixture.
    /// </summary>
    /// <remarks>
    /// Git derives the displayed abbreviation length from the object store, so it is NOT a fixed number
    /// of characters: CI run 43606604223/job 132482481229 displayed 7 (<c>fa1efcb</c>) where the retired
    /// assertion had assumed 8, and a SHA-256 repository legitimately reports a 64-character name. The
    /// displayed name must therefore be a PREFIX of whatever length <paramref name="sha"/> actually is,
    /// and is never truncated or padded to a guessed width. Being a literal prefix of the lowercase
    /// fixture name already forces lowercase hex, so no separate character-class check is needed.
    /// Requiring such a prefix — rather than the mere presence of SHA-like text — is what rejects an
    /// attribution to a DIFFERENT commit.
    /// </remarks>
    private static void AssertBlameOutputNamesCommit(string output, string sha)
    {
        Assert.NotEmpty(sha);
        var name = BlameObjectName(output);
        Assert.True(
            name.Length >= 4
                && name.Length <= sha.Length
                && sha.StartsWith(name, StringComparison.Ordinal),
            $"Blame output does not name the fixture commit '{sha}': {output}");
    }

    /// <summary>
    /// The full commit name that the failing CI run (43606604223/job 132482481229) displayed as
    /// <c>fa1efcb</c> — 7 characters, where the retired assertion had assumed 8. Its tail past the
    /// CI-observed prefix is arbitrary: the correlation under test only looks at prefixes of this name.
    /// </summary>
    private const string CiObservedFullSha = "fa1efcbb0123456789abcdef0123456789abcdef";

    [Fact]
    public void GitBlame_AbbreviatedCommitNameCorrelation_AcceptsSevenAndEightButRejectsOtherCommits()
    {
        var sha = CiObservedFullSha;
        const string author = " (Test 2026-11-24 13:40:46 +0000 1) ";
        var seven = $"{sha[..7]}{author}unchanged-marker\n{sha[..7]}{author}ssh://u:[redacted]@h.invalid/r\n";
        var eight = $"{sha[..8]}{author}unchanged-marker\n";

        // The RETIRED oracle was `Assert.Contains(sha[..8], output)`. It throws on the observed
        // 7-character display and passes on the 8-character one.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => Assert.Contains(sha[..8], seven, StringComparison.Ordinal));
        Assert.Contains(sha[..8], eight, StringComparison.Ordinal);

        // The replacement accepts BOTH observed lengths, with and without the optional root `^` marker:
        // 7 and 8 characters times marked and unmarked is all four combinations.
        AssertBlameOutputNamesCommit(seven, sha);
        AssertBlameOutputNamesCommit(eight, sha);
        AssertBlameOutputNamesCommit($"^{sha[..7]}{author}unchanged-marker\n", sha);
        // Kept as its own shape even though the coloured caret-8 case below also rejects a missing caret
        // strip: this is the uncoloured `blame.showRoot` default that ordinary output actually takes.
        AssertBlameOutputNamesCommit($"^{sha[..8]}{author}unchanged-marker\n", sha);

        // Git may colour the object name (color.blame=always with blame.coloring=repeatedLines or
        // highlightRecent), emitting an ANSI SGR sequence BEFORE the name; the escape is decoration
        // rather than part of the name. When both are present, Git prints the colour before the caret.
        // The repeatedLines shape — plain leading line, coloured second line — is included too.
        AssertBlameOutputNamesCommit($"\u001b[36m{sha[..8]}{author}unchanged-marker\n", sha);
        AssertBlameOutputNamesCommit($"\u001b[31m^{sha[..7]}{author}unchanged-marker\n", sha);
        AssertBlameOutputNamesCommit($"\u001b[31m^{sha[..8]}{author}unchanged-marker\n", sha);
        AssertBlameOutputNamesCommit(
            $"{sha[..8]}{author}unchanged-marker\n\u001b[36m{sha[..8]}{author}ssh://u:[redacted]@h.invalid/r\u001b[m\n",
            sha);

        // No 40-character assumption: a SHA-256 repository names HEAD with 64 characters, and an
        // object-store abbreviation of that name still correlates to it, with OR without the optional
        // `^` marker (blame.showRoot decides whether git prints it, independently of hash width).
        var sha256 = sha + sha[..24];
        Assert.Equal(64, sha256.Length);
        var sha256Line = $"{sha256[..12]}{author}unchanged-marker\n";
        AssertBlameOutputNamesCommit(sha256Line, sha256);
        AssertBlameOutputNamesCommit("^" + sha256Line, sha256);
        AssertBlameOutputNamesCommit("\u001b[31m^" + sha256Line, sha256);

        // Rejections, none of which fall back to "some SHA-like text exists". `fa1efcc` has the SAME
        // length as the observed display and shares its first six characters, so only an exact prefix
        // comparison rejects it; `deadbeef` is another commit at the retired assumption's length; and a
        // leading token that is not the commit name at all (here the file CONTENT) must also fail.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertBlameOutputNamesCommit(seven.Replace(sha[..7], "fa1efcc", StringComparison.Ordinal), sha));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertBlameOutputNamesCommit(seven.Replace(sha[..7], "deadbeef", StringComparison.Ordinal), sha));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertBlameOutputNamesCommit($"unchanged-marker{author}x\n", sha));
        // git never abbreviates below four characters: a shorter prefix, and a bare `^` boundary marker
        // with no object name at all, must not be mistaken for an attribution to the fixture commit.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertBlameOutputNamesCommit($"{sha[..3]}{author}x\n", sha));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertBlameOutputNamesCommit($"^{author}x\n", sha));
        // The wrong prefix must also be rejected under the 64-character name and the marked shape:
        // neither a different hash width nor an optional `^` may relax the exact-prefix correlation.
        var wrongSha256 = sha256Line.Replace(sha256[..12], "0" + sha256[..11], StringComparison.Ordinal);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertBlameOutputNamesCommit(wrongSha256, sha256));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertBlameOutputNamesCommit("^" + wrongSha256, sha256));
        // Stripping the colour decoration must not become a way to smuggle a wrong commit past the
        // check: a coloured wrong prefix stays rejected, exactly like its uncoloured form.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertBlameOutputNamesCommit("\u001b[31m^" + wrongSha256, sha256));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertBlameOutputNamesCommit(
                seven.Replace(sha[..7], "\u001b[36mfa1efcc", StringComparison.Ordinal),
                sha));

        // Malformed decoration is NOT Git's complete SGR colour, so it must neither be stripped nor let
        // the check borrow a token from elsewhere. Each probe is labelled and asserted on its own.
        // Valid complete-SGR controls first (multi-parameter and bare reset forms), so that rejecting
        // every coloured token could not satisfy the probes below.
        AssertBlameOutputNamesCommit($"\u001b[1;31m{sha[..7]}{author}unchanged-marker\n", sha);
        AssertBlameOutputNamesCommit($"\u001b[m\u001b[36m^{sha[..7]}{author}unchanged-marker\n", sha);

        // PROBE 1: an incomplete CSI crossing a line. The valid-looking name on the NEXT line must not
        // be borrowed.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertBlameOutputNamesCommit($"\u001b[31\n{sha[..7]}{author}unchanged-marker\n", sha));
        // PROBE 2: a bare ESC crossing a line, with the same borrowed-token hazard.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertBlameOutputNamesCommit($"\u001b\n{sha[..7]}{author}unchanged-marker\n", sha));
        // PROBE 3: a non-SGR CSI (final byte `c`, not `m`) inside the token, hiding the mismatching `c`:
        // permissive stripping would leave `fa1efc`, a valid prefix, and accept it.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertBlameOutputNamesCommit($"{sha[..6]}\u001b[31c{author}unchanged-marker\n", sha));
        // Even a complete SGR is not decoration when it splits the name. Stripping it here would
        // join the two fragments into the otherwise valid seven-character prefix.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertBlameOutputNamesCommit($"{sha[..3]}\u001b[31m{sha[3..7]}{author}unchanged-marker\n", sha));
    }

    [Theory]
    [InlineData("log")]
    [InlineData("diff")]
    [InlineData("show")]
    [InlineData("blame")]
    [InlineData("branch")]
    public async Task GitRedaction_ActualOutput_RemovesCredentialsAndRetainsGitContent(string tool)
    {
        using var f = NewGitRedactionFixture();
        await File.WriteAllTextAsync(Path.Combine(f.Repo, "sample.txt"), $"unchanged-marker\nssh://u:{FakeValue}@h.invalid/r\n", RedactionCt);
        await CheckedRedactionGit(f.Repo, "add", "sample.txt");
        await CheckedRedactionGit(f.Repo, "commit", "-m", $"ordinary-subject GH_TOKEN={FakeValue}");
        var sha = await CheckedRedactionGit(f.Repo, "rev-parse", "HEAD");
        await CheckedRedactionGit(f.Repo, "update-ref", $"refs/heads/topic-GH_TOKEN={FakeValue}", sha);
        var output = tool switch
        {
            "log" => await f.Composer.GitLogAsync("test-repo", format: "full", cancellationToken: RedactionCt),
            "diff" => await f.Composer.GitDiffAsync("test-repo", "HEAD~1", "HEAD", cancellationToken: RedactionCt),
            "show" => await f.Composer.GitShowAsync("test-repo", "HEAD", cancellationToken: RedactionCt),
            "blame" => await f.Composer.GitBlameAsync("test-repo", "sample.txt", cancellationToken: RedactionCt),
            "branch" => await f.Composer.GitBranchAsync("test-repo", cancellationToken: RedactionCt),
            _ => throw new InvalidOperationException("Unknown tool"),
        };
        NoSentinel(output, FakeValue);
        switch (tool)
        {
            case "log":
                Assert.Contains(sha, output);
                Assert.Contains("ordinary-subject GH_TOKEN=[redacted]", output);
                break;
            case "diff":
            case "show":
                Assert.Contains("diff --git a/sample.txt b/sample.txt", output);
                Assert.Contains("@@ -0,0 +1,2 @@", output);
                Assert.Contains("+unchanged-marker", output);
                Assert.Contains("+ssh://u:[redacted]@h.invalid/r", output);
                break;
            case "blame":
                // Git abbreviates the leading object name to an object-store-dependent length, so
                // correlate it to the sha `rev-parse HEAD` already returned instead of an assumed width.
                AssertBlameOutputNamesCommit(output, sha);
                Assert.Contains("unchanged-marker", output);
                Assert.Contains("ssh://u:[redacted]@h.invalid/r", output);
                break;
            case "branch":
                Assert.Contains("topic-GH_TOKEN=[redacted]", output);
                Assert.Contains("main", output);
                break;
        }
    }

    [Theory]
    [InlineData("log")]
    [InlineData("diff")]
    [InlineData("show")]
    [InlineData("blame")]
    [InlineData("branch")]
    public async Task GitRedaction_ActualErrors_RetainDiagnosticsWithoutEchoedSecrets(string tool)
    {
        using var f = NewGitRedactionFixture();
        // Git blame normalizes URL-like filenames from ssh:// to ssh:/ in stderr. Use an
        // assignment-shaped filename here so BOTH the echoed args and stderr remain structural.
        var payload = tool == "blame"
            ? $"h.invalid/not-a-ref-GH_TOKEN={FakeValue}"
            : $"ssh://u:{FakeValue}@h.invalid/not-a-ref";
        string result;
        if (tool == "branch")
        {
            // The included config's filename is echoed by REAL git's config loader.
            var included = Path.Combine(f.Root, $"config-marker-GH_TOKEN={FakeValue}");
            await File.WriteAllTextAsync(included, "[broken\n", RedactionCt);
            await CheckedRedactionGit(f.Repo, "config", "include.path", included);
            var raw = await RawRedactionGit(f.Repo, "branch", "--list");
            Assert.NotEqual(0, raw.ExitCode);
            Assert.True(raw.Error.Contains(FakeValue, StringComparison.Ordinal), "Config loader must echo the planted sentinel.");
            result = await f.Composer.GitBranchAsync("test-repo", cancellationToken: RedactionCt);
            Assert.Contains("config-marker-GH_TOKEN=[redacted]", result);
        }
        else
        {
            result = tool switch
            {
                "log" => await f.Composer.GitLogAsync("test-repo", branch: payload, cancellationToken: RedactionCt),
                "diff" => await f.Composer.GitDiffAsync("test-repo", payload, cancellationToken: RedactionCt),
                "show" => await f.Composer.GitShowAsync("test-repo", payload, cancellationToken: RedactionCt),
                "blame" => await f.Composer.GitBlameAsync("test-repo", payload, cancellationToken: RedactionCt),
                _ => throw new InvalidOperationException("Unknown tool"),
            };
            Assert.Contains("h.invalid/not-a-ref", result);
        }
        Assert.Contains("git " + tool, result);
        Assert.Contains("failed (exit", result);
        Assert.Contains("fatal:", result);
        NoSentinel(result, FakeValue);
    }

    [Fact]
    public async Task GitRedaction_BlameNormalizedFilename_InventoryStillProtectsRealStderr()
    {
        using var f = NewGitRedactionFixture(FakeValue);
        var payload = $"ssh://u:{FakeValue}@h.invalid/not-a-file";
        var raw = await RawRedactionGit(f.Repo, "blame", "--", payload);
        Assert.NotEqual(0, raw.ExitCode);
        Assert.True(raw.Error.Contains(FakeValue, StringComparison.Ordinal));
        var output = await f.Composer.GitBlameAsync("test-repo", payload, cancellationToken: RedactionCt);
        Assert.Contains("git blame --", output);
        Assert.Contains("fatal: no such path", output);
        Assert.Contains("h.invalid/not-a-file", output);
        NoSentinel(output, FakeValue);
    }

    [Fact]
    public async Task GitRedaction_FetchRemoteProbeError_RedactsInternalStderrWhenSurfaced()
    {
        using var f = NewGitRedactionFixture();
        var included = Path.Combine(f.Root, $"config-marker-GH_TOKEN={FakeValue}");
        await File.WriteAllTextAsync(included, "[broken\n", RedactionCt);
        await CheckedRedactionGit(f.Repo, "config", "include.path", included);
        var raw = await RawRedactionGit(f.Repo, "remote");
        Assert.NotEqual(0, raw.ExitCode);
        Assert.True(raw.Error.Contains(FakeValue, StringComparison.Ordinal));
        var output = await f.Composer.GitFetchAsync("test-repo", cancellationToken: RedactionCt);
        Assert.Contains("git remote failed (exit", output);
        Assert.Contains("config-marker-GH_TOKEN=[redacted]", output);
        Assert.Contains("fatal:", output);
        NoSentinel(output, FakeValue);
        Assert.Empty(f.Manager.FetchCalls);
    }

    [Fact]
    public async Task GitRedaction_OriginArgumentException_RedactsMessageAtBoundary()
    {
        using var f = NewGitRedactionFixture();
        await CheckedRedactionGit(f.Repo, "remote", "add", "origin", Path.Combine(f.Root, "unused.git"));
        var manager = new ThrowingFetchRepoManager(f.Manager.WorkDirectory, new ArgumentException($"argument-marker ssh://u:{FakeValue}@h.invalid/r"));
        var composer = new Composer("test-model", NullLogger<Composer>.Instance, _store, repoManager: manager, stateDir: f.Root);
        var output = await composer.GitFetchAsync("test-repo", cancellationToken: RedactionCt);
        Assert.Equal("❌ argument-marker ssh://u:[redacted]@h.invalid/r", output);
        NoSentinel(output, FakeValue);
    }

    [Fact]
    public async Task GitRedaction_FetchLocalTransportError_RedactsRealStderr()
    {
        using var f = NewGitRedactionFixture();
        // A nonexistent LOCAL path, with an assignment in its name: no external network or auth.
        var missing = Path.Combine(f.Root, "remote-marker", $"GH_TOKEN={FakeValue}");
        await CheckedRedactionGit(f.Repo, "remote", "add", "upstream", missing);
        var raw = await RawRedactionGit(f.Repo, "fetch", "upstream");
        Assert.NotEqual(0, raw.ExitCode);
        Assert.True(raw.Error.Contains(FakeValue, StringComparison.Ordinal), "The fixture must exercise secret-bearing real git stderr.");
        var output = await f.Composer.GitFetchAsync("test-repo", remote: "upstream", cancellationToken: RedactionCt);
        NoSentinel(output, FakeValue);
        Assert.Contains("git fetch upstream failed (exit", output);
        Assert.Contains("remote-marker", output);
        Assert.Contains("does not appear to be a git repository", output);
        Assert.Empty(f.Manager.FetchCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GitRedaction_ManagerResult_BothOutputAndErrorAreSanitized(bool failure)
    {
        using var f = NewGitRedactionFixture();
        await CheckedRedactionGit(f.Repo, "remote", "add", "origin", Path.Combine(f.Root, "unused.git"));
        var text = $"normal-fetch-summary ssh://u:{FakeValue}@h.invalid/r";
        f.Manager.Result = new BrainFetchResult(!failure, failure ? "" : text, failure ? text : null);
        var output = await f.Composer.GitFetchAsync("test-repo", cancellationToken: RedactionCt);
        Assert.Equal("normal-fetch-summary ssh://u:[redacted]@h.invalid/r", output);
        NoSentinel(output, FakeValue);
        Assert.Single(f.Manager.FetchCalls);
    }

    [Fact]
    public async Task GitRedaction_RefFormatError_RedactsGitRejectionNotInternalData()
    {
        using var f = NewGitRedactionFixture();
        await CheckedRedactionGit(f.Repo, "remote", "add", "origin", Path.Combine(f.Root, "unused.git"));
        var branch = $"https://u:{FakeValue}@h.invalid/topic";
        var raw = await RawRedactionGit(f.Repo, "check-ref-format", "--branch", branch);
        Assert.NotEqual(0, raw.ExitCode);
        Assert.True(raw.Error.Contains(FakeValue, StringComparison.Ordinal));
        var output = await f.Composer.GitFetchAsync("test-repo", branch: branch, cancellationToken: RedactionCt);
        NoSentinel(output, FakeValue);
        Assert.Contains("Invalid branch", output);
        Assert.Contains("h.invalid/topic", output);
        Assert.Contains("not a valid branch name", output);
        Assert.Empty(f.Manager.FetchCalls);
    }

    [Theory]
    [InlineData("log-format")]
    [InlineData("log-branch")]
    [InlineData("diff-ref1")]
    [InlineData("diff-ref2")]
    [InlineData("show-ref")]
    [InlineData("branch-pattern")]
    [InlineData("fetch-repo")]
    [InlineData("fetch-remote")]
    [InlineData("fetch-branch")]
    public async Task GitRedaction_PreValidationErrors_SanitizeCallerInput(string site)
    {
        using var f = NewGitRedactionFixture();
        await CheckedRedactionGit(f.Repo, "remote", "add", "origin", Path.Combine(f.Root, "unused.git"));
        var payload = $"-ssh://u:{FakeValue}@h.invalid/r";
        var result = site switch
        {
            "log-format" => await f.Composer.GitLogAsync("test-repo", format: payload, cancellationToken: RedactionCt),
            "log-branch" => await f.Composer.GitLogAsync("test-repo", branch: payload, cancellationToken: RedactionCt),
            "diff-ref1" => await f.Composer.GitDiffAsync("test-repo", payload, cancellationToken: RedactionCt),
            "diff-ref2" => await f.Composer.GitDiffAsync("test-repo", "HEAD", payload, cancellationToken: RedactionCt),
            "show-ref" => await f.Composer.GitShowAsync("test-repo", payload, cancellationToken: RedactionCt),
            "branch-pattern" => await f.Composer.GitBranchAsync("test-repo", pattern: payload, cancellationToken: RedactionCt),
            "fetch-repo" => await f.Composer.GitFetchAsync(payload, cancellationToken: RedactionCt),
            "fetch-remote" => await f.Composer.GitFetchAsync("test-repo", remote: payload, cancellationToken: RedactionCt),
            "fetch-branch" => await f.Composer.GitFetchAsync("test-repo", branch: payload + "@{", cancellationToken: RedactionCt),
            _ => throw new InvalidOperationException("Unknown validation site"),
        };
        NoSentinel(result, FakeValue);
        Assert.Contains("❌ Invalid", result);
        Assert.Contains("h.invalid/r", result);
        Assert.Empty(f.Manager.FetchCalls);
    }

    private static string PercentInventory(string value) => string.Concat(Encoding.UTF8.GetBytes(value).Select(b => $"%{b:x2}"));
    private static string InventoryText(string value) => $"ordinary-file\n{value}\n{PercentInventory(value)}\n{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}\n{Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=')}\nend-marker\n";

    [Fact]
    public async Task GitRedaction_ConstructorInventory_RawPercentAndPaddedUnpaddedBase64()
    {
        const string key = "FAKE/key+xy"; // base64 has padding; encoded percent form is not the raw key.
        Assert.EndsWith("=", Convert.ToBase64String(Encoding.UTF8.GetBytes(key)));
        using var f = NewGitRedactionFixture(key);
        await File.WriteAllTextAsync(Path.Combine(f.Repo, "inventory.txt"), InventoryText(key), RedactionCt);
        await CheckedRedactionGit(f.Repo, "add", "inventory.txt");
        await CheckedRedactionGit(f.Repo, "commit", "-m", "inventory-control");
        var output = await f.Composer.GitShowAsync("test-repo", "HEAD:inventory.txt", cancellationToken: RedactionCt);
        Assert.Equal("ordinary-file\n[redacted]\n[redacted]\n[redacted]\n[redacted]\nend-marker\n", output);
        NoSentinel(output, key, PercentInventory(key), Convert.ToBase64String(Encoding.UTF8.GetBytes(key)).TrimEnd('='));
    }

    [Fact]
    public async Task GitRedaction_ShortConstructorInventory_IsNotExactReplaced()
    {
        const string key = "FAKExy";
        using var f = NewGitRedactionFixture(key);
        await File.WriteAllTextAsync(Path.Combine(f.Repo, "inventory.txt"), InventoryText(key), RedactionCt);
        await CheckedRedactionGit(f.Repo, "add", "inventory.txt");
        await CheckedRedactionGit(f.Repo, "commit", "-m", "short-inventory-control");
        var output = await f.Composer.GitShowAsync("test-repo", "HEAD:inventory.txt", cancellationToken: RedactionCt);
        Assert.Equal(InventoryText(key), output);
    }

    [Theory]
    [InlineData("GH_TOKEN")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("GITHUB_CONFIG_REPO_TOKEN")]
    [InlineData("GITHUB_ORCHESTRATOR_TOKEN")]
    public async Task GitRedaction_EnvironmentInventory_FollowsRotationRemovalAndShortValues(string variable)
    {
        using var f = NewGitRedactionFixture();
        const string first = "FAKE/env+on";
        const string second = "FAKE/env+tw";
        const string shortValue = "FAKEzz";
        Assert.EndsWith("=", Convert.ToBase64String(Encoding.UTF8.GetBytes(first)));
        Assert.EndsWith("=", Convert.ToBase64String(Encoding.UTF8.GetBytes(second)));
        var content = InventoryText(first) + InventoryText(second) + InventoryText(shortValue);
        await File.WriteAllTextAsync(Path.Combine(f.Repo, "inventory.txt"), content, RedactionCt);
        await CheckedRedactionGit(f.Repo, "add", "inventory.txt");
        await CheckedRedactionGit(f.Repo, "commit", "-m", "env-inventory-control");
        var original = Environment.GetEnvironmentVariable(variable);
        try
        {
            // Collection DisableParallelization serializes all reads/writes/restores against all tests.
            foreach (var current in new string?[] { first, second, null, shortValue })
            {
                Environment.SetEnvironmentVariable(variable, current);
                var output = await f.Composer.GitShowAsync("test-repo", "HEAD:inventory.txt", cancellationToken: RedactionCt);
                var expected = content;
                if (current is first or second)
                {
                    expected = expected.Replace(current, "[redacted]", StringComparison.Ordinal)
                        .Replace(PercentInventory(current), "[redacted]", StringComparison.Ordinal)
                        .Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes(current)), "[redacted]", StringComparison.Ordinal)
                        .Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes(current)).TrimEnd('='), "[redacted]", StringComparison.Ordinal);
                }
                Assert.Equal(expected, output);
                Assert.Contains("ordinary-file", output);
                Assert.Contains("end-marker", output);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    [Fact]
    public async Task GitRedaction_Truncation_KeepsExactOriginalCountAndSanitizesRetainedWindow()
    {
        using var f = NewGitRedactionFixture();
        var rows = Enumerable.Range(1, 80).Select(i => i == 2 ? $"GH_TOKEN={FakeValue}" : i == 75 ? "token=FAKE_AFTER_BOUNDARY" : $"ordinary-row-{i}");
        await File.WriteAllTextAsync(Path.Combine(f.Repo, "long.txt"), string.Join('\n', rows) + "\n", RedactionCt);
        await CheckedRedactionGit(f.Repo, "add", "long.txt");
        await CheckedRedactionGit(f.Repo, "commit", "-m", "long-file-control");
        // Raw git defines both boundary placement and original total; no helper under test supplies the oracle.
        var raw = await RawRedactionGit(f.Repo, "show", "HEAD:long.txt");
        Assert.Equal(0, raw.ExitCode);
        Assert.Contains(raw.Out.Split('\n').Take(10), l => l.Contains(FakeValue, StringComparison.Ordinal));
        Assert.Contains(raw.Out.Split('\n').Skip(10), l => l.Contains("FAKE_AFTER_BOUNDARY", StringComparison.Ordinal));
        var full = await f.Composer.GitShowAsync("test-repo", "HEAD:long.txt", max_lines: 200, cancellationToken: RedactionCt);
        var limited = await f.Composer.GitShowAsync("test-repo", "HEAD:long.txt", max_lines: 10, cancellationToken: RedactionCt);
        NoSentinel(full, FakeValue, "FAKE_AFTER_BOUNDARY");
        NoSentinel(limited, FakeValue, "FAKE_AFTER_BOUNDARY");
        Assert.Contains("token=[redacted]", full);
        Assert.Contains("GH_TOKEN=[redacted]", limited);
        Assert.Contains("ordinary-row-10", limited);
        Assert.Equal(raw.Out.Split('\n').Length, full.Split('\n').Length);
        Assert.Equal(81, raw.Out.Split('\n').Length);
        Assert.Equal(string.Join('\n', full.Split('\n').Take(10)) + "\n... (truncated, 81 lines total)", limited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GitRedaction_LocalCancellation_PreservesExceptionIdentityAndToken(bool fetchProbe)
    {
        using var f = NewGitRedactionFixture();
        using var cts = new CancellationTokenSource();
        var original = new OperationCanceledException("FAKE_CANCEL_MESSAGE", new IOException("FAKE_INNER"), cts.Token);
        var manager = new ThrowingClonePathRepoManager(f.Manager.WorkDirectory, _ => { }, original);
        var composer = new Composer("test-model", NullLogger<Composer>.Instance, _store, repoManager: manager, stateDir: f.Root);
        var observed = await Assert.ThrowsAsync<OperationCanceledException>(() => fetchProbe
            ? composer.GitFetchAsync("test-repo", cancellationToken: cts.Token)
            : composer.GitLogAsync("test-repo", cancellationToken: cts.Token));
        Assert.Same(original, observed);
        Assert.Equal(cts.Token, observed.CancellationToken);
    }

    [Fact]
    public async Task GitRedaction_OriginCancellation_IsFreshSafeAndKeepsTokenWithoutInner()
    {
        using var f = NewGitRedactionFixture();
        await CheckedRedactionGit(f.Repo, "remote", "add", "origin", Path.Combine(f.Root, "unused.git"));
        using var cts = new CancellationTokenSource();
        var original = new OperationCanceledException("FAKE_CANCEL_MESSAGE", new IOException("FAKE_INNER"), cts.Token);
        var manager = new ThrowingFetchRepoManager(f.Manager.WorkDirectory, original);
        var composer = new Composer("test-model", NullLogger<Composer>.Instance, _store, repoManager: manager, stateDir: f.Root);
        var observed = await Assert.ThrowsAsync<OperationCanceledException>(() => composer.GitFetchAsync("test-repo", cancellationToken: RedactionCt));
        Assert.NotSame(original, observed);
        Assert.Equal("git fetch origin was cancelled.", observed.Message);
        Assert.Equal(cts.Token, observed.CancellationToken);
        Assert.Null(observed.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GitRedaction_ActualPathAndProcessFailures_AreClassified(bool fetchProcess)
    {
        using var f = NewGitRedactionFixture();
        var manager = new Mock<IBrainRepoManager>(MockBehavior.Strict);
        manager.SetupGet(m => m.WorkDirectory).Returns(f.Manager.WorkDirectory);
        manager.Setup(m => m.GetClonePath("test-repo")).Returns(fetchProcess
            ? Path.Combine(f.Root, "missing-FAKE_PATH_MARKER")
            : f.Repo + "\0FAKE_PATH_MARKER");
        var composer = new Composer("test-model", NullLogger<Composer>.Instance, _store, repoManager: manager.Object, stateDir: f.Root);
        var output = fetchProcess
            ? await composer.GitFetchAsync("test-repo", cancellationToken: RedactionCt)
            : await composer.GitLogAsync("test-repo", cancellationToken: RedactionCt);
        var expectedType = fetchProcess ? "Win32Exception" : "ArgumentException";
        Assert.Equal($"❌ git tool failed ({expectedType}). See the orchestrator logs for details.", output);
        NoSentinel(output, "FAKE_PATH_MARKER");
    }

    [Theory]
    [InlineData("path", false)]
    [InlineData("path", true)]
    [InlineData("filesystem", false)]
    [InlineData("filesystem", true)]
    [InlineData("process", false)]
    [InlineData("process", true)]
    public async Task GitRedaction_EscapingFailures_OnlyClassifiedTypeReachesModel(string failure, bool fetchProbe)
    {
        using var f = NewGitRedactionFixture();
        const string unsafeMessage = "FAKE_MESSAGE_FRAGMENT ssh://u:FAKE_PASSWORD@h.invalid/r";
        Exception error = failure switch
        {
            "path" => new ArgumentException(unsafeMessage),
            "filesystem" => new IOException(unsafeMessage),
            "process" => new System.ComponentModel.Win32Exception(unsafeMessage),
            _ => throw new InvalidOperationException("Unknown failure"),
        };
        var manager = new ThrowingClonePathRepoManager(f.Manager.WorkDirectory, _ => { }, error);
        var composer = new Composer("test-model", NullLogger<Composer>.Instance, _store, repoManager: manager, stateDir: f.Root);
        var output = fetchProbe
            ? await composer.GitFetchAsync("test-repo", cancellationToken: RedactionCt)
            : await composer.GitLogAsync("test-repo", cancellationToken: RedactionCt);
        Assert.Equal($"❌ git tool failed ({error.GetType().Name}). See the orchestrator logs for details.", output);
        NoSentinel(output, "FAKE_MESSAGE_FRAGMENT", "FAKE_PASSWORD");
    }
}

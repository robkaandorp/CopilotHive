using System.Diagnostics;

namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Real-Git regressions for the ANSI-SGR normalization
/// <see cref="CopilotHive.Services.GitToolOutputRedactor"/> applies at the Composer's git-output
/// boundary. The tests drive the REAL git executable through the REAL Composer tool methods.
/// </summary>
/// <remarks>
/// <para>
/// They live in this partial (rather than in <c>ComposerToolTests.GitRedaction.cs</c>, which is not
/// edited by this change) and inherit the class-level
/// <c>[Collection(GitOutputInventoryCollection.Name)]</c>, whose disabled parallelization keeps the
/// process environment and temporary repositories to themselves.
/// </para>
/// <para>
/// Every credential here is synthetic, and every configuration change is explicitly REPO-LOCAL
/// (<c>git config --local</c>) inside the temporary clone. The blame test runs once with the host's
/// ordinary settings and once with a temporary inherited global file that already sets
/// <c>color.blame=always</c>. It never edits the host's global config; the temporary file and
/// environment override are scoped to the inherited-config case.
/// </para>
/// </remarks>
public sealed partial class ComposerToolTests
{
    /// <summary>
    /// Runs exactly <c>git blame -- &lt;path&gt;</c> in <paramref name="repo"/> with the INHERITED
    /// environment and the same redirection the Composer's own runner uses, returning the raw result.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT <c>RawRedactionGit</c>: that helper injects <c>-c commit.gpgsign=false</c>,
    /// so it is not the argument list <c>GitBlameAsync</c> builds. This probe passes no <c>-c</c> at
    /// all and leaves <see cref="ProcessStartInfo.Environment"/> untouched, so git reads exactly the
    /// configuration (repo-local included) that the Composer's run reads.
    /// </remarks>
    private static async Task<(int ExitCode, string Out, string Error, string[] Args, bool UsesShellExecute, string WorkingDirectory)> PlainBlameProbe(
        string repo, string path)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // NO `-c` overrides and NO psi.Environment mutations: this is the exact argument list
        // GitBlameAsync passes to RunGitAsync and the same inherited environment (repo-local config
        // included) its ProcessStartInfo gets.
        psi.ArgumentList.Add("blame");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(path);

        var args = psi.ArgumentList.ToArray();
        var usesShellExecute = psi.UseShellExecute;
        var workingDirectory = psi.WorkingDirectory;
        // The probe inherits the process environment: GIT_CONFIG_GLOBAL (present or absent) must
        // agree with the parent's value, so no isolated global config is smuggled into this probe.
        // Comparing the VALUE, rather than requiring absence, stays valid on hosts that set it.
        var inheritedGlobal = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        Assert.Equal(inheritedGlobal, psi.Environment.TryGetValue("GIT_CONFIG_GLOBAL", out var probeGlobal) ? probeGlobal : null);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(RedactionCt);
        var stderr = process.StandardError.ReadToEndAsync(RedactionCt);
        await Task.WhenAll(stdout, stderr);
        await process.WaitForExitAsync(RedactionCt);
        return (process.ExitCode, stdout.Result, stderr.Result, args, usesShellExecute, workingDirectory);
    }

    /// <summary>
    /// The reported regression: with <c>color.blame=always</c> and
    /// <c>blame.coloring=highlightRecent</c> real git prints the object name coloured and then a BARE
    /// RESET (<c>ESC[m</c>) immediately before the line's content, so <c>token = …</c> no longer
    /// starts a line and the assignment pass missed the credential entirely.
    /// </summary>
    /// <remarks>
    /// The premise is proved against real git BEFORE the Composer runs: the raw probe contains the
    /// credential AND the exact byte sequence <c>ESC[m</c> followed by the assignment. Only then is
    /// the model-visible result asserted — the credential is gone and the authorisation
    /// (object name) plus ordinary content survive. The object name is correlated to
    /// <c>rev-parse HEAD</c> rather than an assumed width, because <c>core.abbrev=7</c> is also set
    /// here and a repository may legitimately abbreviate to any unique length.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GitBlame_ColoredBlameOutput_RedactsTheDecoratedCredentialAndKeepsItsContent(bool inheritGlobalColor)
    {
        if (!inheritGlobalColor)
        {
            await AssertColoredBlameAsync(inheritGlobalColor);
            return;
        }

        const string globalVariable = "GIT_CONFIG_GLOBAL";
        var inherited = Environment.GetEnvironmentVariable(globalVariable);
        var directory = Directory.CreateTempSubdirectory("colored-blame-global-").FullName;
        var configPath = Path.Combine(directory, "gitconfig");
        // Include a comment so a textual rewrite which leaves `git config --list` unchanged
        // still fails the byte comparison.
        var originalBytes = "# Inherited fixture configuration\n[color]\n\tblame = always\n"u8.ToArray();
        try
        {
            File.WriteAllBytes(configPath, originalBytes);
            Environment.SetEnvironmentVariable(globalVariable, configPath);
            try
            {
                await AssertColoredBlameAsync(inheritGlobalColor);
            }
            finally
            {
                Environment.SetEnvironmentVariable(globalVariable, inherited);
                Assert.Equal(originalBytes, File.ReadAllBytes(configPath));
            }
        }
        finally
        {
            TestHelpers.ForceDeleteDirectory(directory);
        }

        Assert.Equal(inherited, Environment.GetEnvironmentVariable(globalVariable));
        Assert.False(Directory.Exists(directory));
    }

    private async Task AssertColoredBlameAsync(bool inheritGlobalColor)
    {
        using var f = NewGitRedactionFixture();
        const string token = "ghp_colored_blame_synthetic_secret";

        // The inherited GLOBAL configuration is whatever the host has — possibly already holding
        // color.blame=always, which is a legitimate value. Snapshot it so the test can prove its
        // own writes never reach it, instead of assuming the host's value is anything in particular.
        var globalBefore = await RawRedactionGit(f.Repo, "config", "--global", "--list");
        if (inheritGlobalColor)
        {
            var inheritedColor = await RawRedactionGit(f.Repo, "config", "--global", "--get", "color.blame");
            Assert.Equal(0, inheritedColor.ExitCode);
            Assert.Equal("always", inheritedColor.Out.Trim());
        }

        // Repo-LOCAL configuration only, spelled with an explicit --local so a write can never land
        // in the global file: the forced colour and the narrow abbreviation are what real git needs
        // to emit the hostile shape, and neither leaves the temporary clone.
        await CheckedRedactionGit(f.Repo, "config", "--local", "color.blame", "always");
        await CheckedRedactionGit(f.Repo, "config", "--local", "blame.coloring", "highlightRecent");
        await CheckedRedactionGit(f.Repo, "config", "--local", "core.abbrev", "7");

        // The three settings are readable from the same repo's local file. The fixture deletes
        // that repo on exit; the wrapper owns restoration of any temporary inherited environment.
        Assert.Equal("always", await CheckedRedactionGit(f.Repo, "config", "--local", "--get", "color.blame"));
        Assert.Equal("highlightRecent", await CheckedRedactionGit(f.Repo, "config", "--local", "--get", "blame.coloring"));
        Assert.Equal("7", await CheckedRedactionGit(f.Repo, "config", "--local", "--get", "core.abbrev"));
        // Actual pre/post state, not an assumed inherited value: whether the host has no global
        // config, an unrelated one, or one that already says color.blame=always, the fixture's
        // writes left the global view exactly as it was (same exit status, same listing).
        var globalAfter = await RawRedactionGit(f.Repo, "config", "--global", "--list");
        Assert.Equal(globalBefore.ExitCode, globalAfter.ExitCode);
        Assert.Equal(globalBefore.Out, globalAfter.Out);

        await File.WriteAllTextAsync(
            Path.Combine(f.Repo, "README.md"),
            $"token = {token}\nunchanged-marker\n",
            RedactionCt);
        await CheckedRedactionGit(f.Repo, "add", "README.md");
        await CheckedRedactionGit(f.Repo, "commit", "-m", "sample");
        var sha = await CheckedRedactionGit(f.Repo, "rev-parse", "HEAD");

        // ── Premise, from real git, before the Composer runs ──
        var raw = await PlainBlameProbe(f.Repo, "README.md");
        Assert.Equal(0, raw.ExitCode);

        // The probe used EXACTLY `git blame -- README.md` in the same repo with the inherited
        // environment: no `-c` override (RawRedactionGit) and no shell/working-directory substitution.
        Assert.Equal(["blame", "--", "README.md"], raw.Args);
        Assert.False(raw.UsesShellExecute);
        Assert.Equal(f.Repo, raw.WorkingDirectory);
        Assert.True(
            raw.Out.Contains(token, StringComparison.Ordinal),
            "The fixture must make real git print the synthetic credential in its blame output.");
        Assert.True(
            raw.Out.Contains("\u001b[m" + $"token = {token}", StringComparison.Ordinal),
            "Real git must place the bare SGR reset immediately before the assignment.");
        // The probe really is DECORATED output, not just a stray reset: git coloured the object name
        // too, emitting at least one escape before the reset that precedes the assignment.
        var resetBeforeAssignment = raw.Out.IndexOf("\u001b[m" + $"token = {token}", StringComparison.Ordinal);
        Assert.True(
            raw.Out.AsSpan(0, resetBeforeAssignment).Contains('\u001b'),
            "Real git must decorate the object name as well, so the coloured shape is genuinely exercised.");

        // ── The boundary's result ──
        var output = await f.Composer.GitBlameAsync("test-repo", "README.md", cancellationToken: RedactionCt);

        // The credential-absence assertion: this is what a normalization-disabled build fails.
        Assert.DoesNotContain(token, output, StringComparison.Ordinal);
        // ...and neither the assignment's value NOR any fragment of its spelling survives.
        Assert.DoesNotContain("ghp_colored_blame", output, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic_secret", output, StringComparison.Ordinal);
        // ...and the reset no longer hides the assignment from the structural scan.
        Assert.Contains("token = [redacted]", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);
        // Useful output survives, correlated to the commit instead of an assumed abbreviation width.
        AssertBlameOutputNamesCommit(output, sha);
        Assert.Contains("unchanged-marker", output, StringComparison.Ordinal);
        Assert.Equal(raw.Out.Count(c => c == '\n'), output.Count(c => c == '\n'));

        // Both coloured lines survive in the model-visible output, so redaction did not discard
        // the whole blame body (a vacuous "credential absent" result). No SHA width is assumed.
        var lines = output.Split('\n');
        Assert.Equal(2, lines.Length - 1);
        Assert.Contains("token = [redacted]", lines[0], StringComparison.Ordinal);
        Assert.Contains("unchanged-marker", lines[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// Caller truncation still describes the REDACTED, SGR-normalized output: the retained window
    /// keeps the credential line in its redacted form, the exact total-line notice counts the same
    /// lines raw git printed (normalization never inserts or removes a line break), and a credential
    /// beyond the window never reaches the model.
    /// </summary>
    /// <remarks>
    /// <c>color.ui=always</c> is repo-local, so real git decorates the diff body itself
    /// (<c>ESC[32m+ESC[m ESC[32m…ESC[m</c>) instead of only its headers; the values are synthetic.
    /// </remarks>
    [Fact]
    public async Task GitShow_ColoredTruncatedOutput_KeepsRedactedWindowAndExactLineCount()
    {
        using var f = NewGitRedactionFixture();
        const string token = "ghp_colored_truncation_synthetic_secret";
        const string beyondBoundary = "ghp_colored_truncation_beyond_boundary";

        await CheckedRedactionGit(f.Repo, "config", "--local", "color.ui", "always");

        var rows = Enumerable.Range(1, 40).Select(i => i switch
        {
            3 => $"GH_TOKEN={token}",
            35 => $"token={beyondBoundary}",
            _ => $"ordinary-row-{i}",
        });
        await File.WriteAllTextAsync(Path.Combine(f.Repo, "long.txt"), string.Join('\n', rows) + "\n", RedactionCt);
        await CheckedRedactionGit(f.Repo, "add", "long.txt");
        await CheckedRedactionGit(f.Repo, "commit", "-m", "colored-truncation");

        // Raw git supplies both the boundary-independent total and the decorated shape.
        var raw = await RawRedactionGit(f.Repo, "show", "HEAD");
        Assert.Equal(0, raw.ExitCode);
        Assert.Contains("\u001b[32m", raw.Out, StringComparison.Ordinal);
        Assert.Contains(token, raw.Out, StringComparison.Ordinal);
        var expectedTotal = raw.Out.Split('\n').Length;

        var full = await f.Composer.GitShowAsync("test-repo", "HEAD", cancellationToken: RedactionCt);
        // The coloured header block puts the credential row at line 13 and the beyond-boundary row
        // near line 45, so this window keeps the first and drops the second.
        const int window = 20;
        var limited = await f.Composer.GitShowAsync("test-repo", "HEAD", max_lines: window, cancellationToken: RedactionCt);

        // The retained line survives normalization in its redacted form...
        Assert.Contains("+GH_TOKEN=[redacted]", limited, StringComparison.Ordinal);
        Assert.Contains("+ordinary-row-1", limited, StringComparison.Ordinal);
        // ...no decoration reaches the model...
        Assert.DoesNotContain("\u001b", full, StringComparison.Ordinal);
        // ...neither credential survives anywhere...
        Assert.DoesNotContain(token, full, StringComparison.Ordinal);
        Assert.DoesNotContain(beyondBoundary, full, StringComparison.Ordinal);
        Assert.DoesNotContain(beyondBoundary, limited, StringComparison.Ordinal);
        // ...the redacted text keeps raw git's line structure...
        Assert.Equal(expectedTotal, full.Split('\n').Length);
        // ...and the notice is exact: redacted window plus the RAW total line count.
        Assert.Equal(
            string.Join('\n', full.Split('\n').Take(window)) + $"\n... (truncated, {expectedTotal} lines total)",
            limited);
    }
}

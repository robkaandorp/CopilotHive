using System.Diagnostics;
using CopilotHive.Git;

namespace CopilotHive.Tests;

[Collection("EnvVarMutation")]
public sealed class ReviewRepairFallbackCloneTests : IDisposable
{
    private const string Url = "https://github.com/org/retry.git";
    private const string Token = "review-fallback-token";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "review-retry-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RetryPrimaryOutcome_IsPreserved_WithRealResidualConfig(bool cancel, bool locked)
    {
        using var cts = new CancellationTokenSource();
        Exception primary = cancel ? new OperationCanceledException("retry cancellation sentinel", cts.Token)
            : new IOException("retry failure sentinel");
        var logger = new TestLogger<BrainRepoManager>();
        var clones = 0;
        var cleanups = 0;
        var clonePath = Path.Combine(_root, "repos", "retry");
        var helperPaths = new List<string>();
        var manager = new BrainRepoManager(_root, logger, request =>
        {
            if (request.Arguments[0] == "clone")
            {
                clones++;
                helperPaths.Add(request.ChildEnvironment![HostGitAskpassHelper.AskpassEnvName]!);
                Assert.True(File.Exists(helperPaths[^1]));
                Assert.DoesNotContain(Token, File.ReadAllText(helperPaths[^1]));
                if (clones == 1)
                {
                    Assert.Contains("--branch", request.Arguments);
                    return new BrainGitResult(128, "", "fatal: branch missing not found in upstream");
                }
                Assert.Equal(2, clones);
                Assert.DoesNotContain("--branch", request.Arguments);
                Directory.CreateDirectory(clonePath);
                Git(clonePath, ["init", "-b", "main"]);
                Git(clonePath, ["remote", "add", "origin", $"https://x-access-token:{Token}@github.com/org/retry.git"]);
                // A REAL, usable residual repo whose origin genuinely needs normalization.
                Assert.Equal("true", Git(clonePath, ["rev-parse", "--is-inside-work-tree"]).Stdout.Trim());
                Assert.Contains(Token, File.ReadAllText(Path.Combine(clonePath, ".git", "config")));
                if (locked) File.WriteAllText(Path.Combine(clonePath, ".git", "config.lock"), "held");
                if (cancel) cts.Cancel();
                throw primary;
            }
            Assert.Equal(new[] { "remote", "set-url", "origin", Url }, request.Arguments);
            Assert.Equal(2, clones); // cleanup belongs to the RETRY, not the initial attempt
            cleanups++;
            Assert.Null(request.ChildEnvironment); // bounded local cleanup needs no credential
            var result = Git(request.WorkingDirectory, request.Arguments);
            // Supply a secret-bearing cleanup diagnostic in addition to the real config-lock error.
            return locked ? result with { Stderr = result.Stderr + $" rejected {Token}" } : result;
        }, _ => Task.FromResult<string?>(Token), _ => Url);

        var observed = await Record.ExceptionAsync(() => manager.EnsureCloneAsync("retry", Url, "missing", cts.Token));
        Assert.Same(primary, observed);
        if (cancel) Assert.Equal(cts.Token, Assert.IsType<OperationCanceledException>(observed).CancellationToken);
        Assert.Equal(2, clones);
        Assert.Equal(1, cleanups);
        var config = File.ReadAllText(Path.Combine(clonePath, ".git", "config"));
        var diagnostics = logger.LogEntries.Where(e => e.Message.Contains("residual origin", StringComparison.Ordinal)).ToList();
        if (locked)
        {
            Assert.Contains(Token, config); // honest best-effort limitation, no claimed normalization
            var diagnostic = Assert.Single(diagnostics);
            Assert.Contains("Could not normalize", diagnostic.Message);
            Assert.DoesNotContain(Token, diagnostic.Message);
            Assert.DoesNotContain(Token, diagnostic.Exception?.ToString() ?? "");
        }
        else
        {
            Assert.Contains(Url, config);
            Assert.DoesNotContain(Token, config);
            // m5 discriminator: successful independent-token cleanup, not just a config write by
            // a cancelled process whose await/termination path happened to report cancellation.
            Assert.Empty(diagnostics);
        }
        Assert.All(helperPaths, p => Assert.False(Directory.Exists(Path.GetDirectoryName(p))));
    }

    private static BrainGitResult Git(string dir, IReadOnlyList<string> args)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("git")
        { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit(15000);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            Assert.True(process.WaitForExit(10000), "Residual fixture git could not be joined after termination.");
        }
        var result = new BrainGitResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        Assert.True(exited, "Residual fixture git did not exit within 15s.");
        if (!args.Contains("set-url")) Assert.Equal(0, result.ExitCode);
        return result;
    }

    public void Dispose() => TestHelpers.ForceDeleteDirectory(_root);
}

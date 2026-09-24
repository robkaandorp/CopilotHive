using System.Diagnostics;
using CopilotHive.Configuration;

namespace CopilotHive.Tests;

/// <summary>
/// Cancellation contract of the config-repo git core runner: a cancelled caller must not be able
/// to leave a git process mutating the clone after the call returns.
/// <para>
/// Driven against a REAL git process — <see cref="ConfigRepoManager.GitRunner"/> is deliberately
/// left <c>null</c> so the production process path (and its cancellation cleanup) is what runs.
/// The blocking git command is a POSIX shell alias that writes its own PID and then <c>exec</c>s
/// <c>sleep</c>, so the recorded child is a real OS process whose lifetime is observed directly
/// (bounded polling only — no timing-based assertions).
/// </para>
/// </summary>
public sealed class ConfigRepoManagerCancellationTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigRepoManagerCancellationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-cfgcancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Cancelling a config-repo git call propagates the caller's
    /// <see cref="OperationCanceledException"/> AND terminates the still-running git process tree
    /// (the recorded child is gone within the bounded wait).
    /// </summary>
    /// <remarks>
    /// Kills the mutation that drops the termination step — the <c>sleep</c> child would remain
    /// alive for its full 60s, well past the 10s bounded wait — and the mutation that drops the
    /// rethrow (the <c>ThrowsAnyAsync</c> assertion fails). It cannot be satisfied by a git that
    /// exited on its own: the child is asserted to be RUNNING before the cancellation.
    /// </remarks>
    [Fact]
    public async Task RunGitCoreAsync_CancelledWhileGitRuns_TerminatesTheGitProcessTree()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The blocking git command is a POSIX shell alias (alias + sleep); covered on Linux/macOS.");
            return;
        }

        var testToken = TestContext.Current.CancellationToken;

        // The working directory AND the PID-file directory deliberately contain a space, so the
        // alias body's quoting of the PID path is exercised rather than assumed.
        var workDir = Path.Combine(_tempDir, "work dir");
        var pidDir = Path.Combine(_tempDir, "pid files");
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(pidDir);
        var pidFile = Path.Combine(pidDir, "git-child.pid");

        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        // Premise: no seam was installed, so the real process path in RunGitCoreAsync runs.
        Assert.Null(manager.GitRunner);

        using var cts = new CancellationTokenSource();
        string[] blockingArgs =
        [
            "-c",
            $"alias.hiveblock=!echo $$ > \"{pidFile}\"; exec sleep 60",
            "hiveblock",
        ];

        var run = manager.RunGitForTestAsync(workDir, blockingArgs, cts.Token);
        var childPid = 0;

        try
        {
            // Bounded poll: the alias writes the PID file before it blocks, so its appearance
            // proves the real git process is up and has actually reached the alias.
            Assert.True(
                await WaitUntilAsync(() => TryReadPid(pidFile, out _), TimeSpan.FromSeconds(10), testToken),
                $"The blocking git alias never wrote its PID file at '{pidFile}'.");

            Assert.True(
                TryReadPid(pidFile, out childPid) && childPid > 1,
                $"The PID file '{pidFile}' did not contain a usable PID.");

            // Premise for the termination assertion: the recorded child really is running right
            // now, so "it exited" after the cancellation cannot be an artefact of it never having
            // started.
            Assert.True(
                IsChildAlive(childPid),
                $"Child process {childPid} had already exited before cancellation — the premise is broken.");

            cts.Cancel();

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Equal(cts.Token, ex.CancellationToken);

            Assert.True(
                await WaitUntilAsync(() => !IsChildAlive(childPid), TimeSpan.FromSeconds(10), testToken),
                $"Git child process {childPid} was still running 10s after the cancelled call returned — "
                + "the git process tree was not terminated.");
        }
        finally
        {
            cts.Cancel();

            if (childPid <= 0)
                TryReadPid(pidFile, out childPid);
            TryKillProcess(childPid);

            // Drain the call so a failing body cannot leak a pending process launch.
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(15), testToken);
            }
            catch (Exception)
            {
                // The body asserts the outcome; a drain fault must not replace it.
            }
        }
    }

    /// <summary>
    /// True when <paramref name="pid"/> is a live process; false when it no longer exists (the
    /// lookup throws) or has already exited.
    /// </summary>
    private static bool IsChildAlive(int pid)
    {
        if (pid <= 1)
            return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No such process — reaped/absent.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Reads a PID from <paramref name="path"/>, tolerating a not-yet-written/partial file.</summary>
    private static bool TryReadPid(string path, out int pid)
    {
        pid = 0;
        try
        {
            return File.Exists(path)
                && int.TryParse(File.ReadAllText(path).Trim(), out pid)
                && pid > 1;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Bounded polling wait for <paramref name="condition"/>: the deadline is purely protective
    /// (the failure is asserted by the caller), and no conclusion is drawn from how long the poll
    /// took.
    /// </summary>
    private static async Task<bool> WaitUntilAsync(
        Func<bool> condition, TimeSpan deadline, CancellationToken testToken)
    {
        if (condition())
            return true;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        deadlineCts.CancelAfter(deadline);

        try
        {
            while (await timer.WaitForNextTickAsync(deadlineCts.Token))
            {
                if (condition())
                    return true;
            }
        }
        catch (OperationCanceledException) when (!testToken.IsCancellationRequested)
        {
            // Protective deadline expired — the caller asserts the condition.
        }

        return condition();
    }

    /// <summary>Best-effort cleanup so a failing test cannot leak a sleeping child process.</summary>
    private static void TryKillProcess(int pid)
    {
        if (pid <= 1)
            return;

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // Already absent.
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Best-effort only; the assertions report any contract failure first.
        }
    }
}

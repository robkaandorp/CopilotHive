using System.Diagnostics;

namespace CopilotHive.Git;

/// <summary>
/// Manages a bare git repository and per-worker shallow clones inside a shared workspace directory.
/// </summary>
public sealed class GitWorkspaceManager
{
    private readonly string _workspacePath;
    private readonly string _bareRepoPath;

    /// <summary>
    /// Initialises a new <see cref="GitWorkspaceManager"/> for the given workspace root.
    /// </summary>
    /// <param name="workspacePath">Root directory under which all repos and clones are created.</param>
    public GitWorkspaceManager(string workspacePath)
    {
        _workspacePath = Path.GetFullPath(workspacePath);
        _bareRepoPath = Path.Combine(_workspacePath, "origin");
    }

    /// <summary>Absolute path to the bare repository that acts as the shared origin.</summary>
    public string BareRepoPath => _bareRepoPath;

    /// <summary>
    /// Initialise the bare repo. When <paramref name="sourcePath"/> is provided,
    /// the initial commit is seeded with the contents of that directory (respecting
    /// any .gitignore found there). Otherwise an empty initial commit is created.
    /// </summary>
    public async Task InitBareRepoAsync(string? sourcePath = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_bareRepoPath);

        if (!Directory.Exists(Path.Combine(_bareRepoPath, "HEAD")))
        {
            await RunGitAsync(_bareRepoPath, ["init", "--bare", "--initial-branch=main"], ct);

            var tempClone = Path.Combine(_workspacePath, "_init-temp");
            try
            {
                if (Directory.Exists(tempClone))
                    await ForceDeleteDirectoryAsync(tempClone);

                await RunGitAsync(_workspacePath, ["clone", _bareRepoPath, "_init-temp"], ct);
                await RunGitAsync(tempClone, ["config", "user.email", "copilothive@local"], ct);
                await RunGitAsync(tempClone, ["config", "user.name", "CopilotHive"], ct);
                await RunGitAsync(tempClone, ["config", "core.autocrlf", "input"], ct);

                if (!string.IsNullOrEmpty(sourcePath) && Directory.Exists(sourcePath))
                {
                    CopyDirectoryContents(sourcePath, tempClone);
                    await RunGitAsync(tempClone, ["add", "."], ct);
                    await RunGitAsync(tempClone, ["commit", "-m", "Initial commit (seeded from source)"], ct);
                }
                else
                {
                    await RunGitAsync(tempClone, ["commit", "--allow-empty", "-m", "Initial commit"], ct);
                }

                await RunGitAsync(tempClone, ["push", "origin", "main"], ct);
            }
            finally
            {
                await ForceDeleteDirectoryAsync(tempClone);
            }
        }
    }

    /// <summary>
    /// Recursively copy directory contents, skipping .git/ and common build artifacts.
    /// The target directory's .gitignore (copied from source) handles the rest via git add.
    /// </summary>
    private static void CopyDirectoryContents(string sourceDir, string targetDir)
    {
        var source = new DirectoryInfo(sourceDir);

        foreach (var dir in source.EnumerateDirectories("*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, dir.FullName);
            if (ShouldSkipDirectory(relativePath))
                continue;
            Directory.CreateDirectory(Path.Combine(targetDir, relativePath));
        }

        foreach (var file in source.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, file.FullName);
            var dirPart = Path.GetDirectoryName(relativePath) ?? "";
            if (ShouldSkipDirectory(dirPart))
                continue;

            var destPath = Path.Combine(targetDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            file.CopyTo(destPath, overwrite: true);
        }
    }

    private static bool ShouldSkipDirectory(string relativePath)
    {
        // Normalise to forward slashes for consistent matching
        var normalized = relativePath.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            if (segment is ".git" or "bin" or "obj" or "node_modules" or "workspaces")
                return true;
        }
        return false;
    }

    /// <summary>
    /// Creates an isolated clone of the bare repo for the specified worker.
    /// Deletes any pre-existing clone at the same path first.
    /// </summary>
    /// <param name="workerName">Unique name used to derive the clone directory path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Absolute path to the newly created clone.</returns>
    public async Task<string> CreateWorkerCloneAsync(string workerName, CancellationToken ct = default)
    {
        var clonePath = Path.Combine(_workspacePath, workerName);

        if (Directory.Exists(clonePath))
            await ForceDeleteDirectoryAsync(clonePath);

        await RunGitAsync(_workspacePath, ["clone", _bareRepoPath, workerName], ct);

        // Configure git user so commits work in isolated clones
        await RunGitAsync(clonePath, ["config", "user.email", "copilothive@local"], ct);
        await RunGitAsync(clonePath, ["config", "user.name", "CopilotHive"], ct);
        // Normalize line endings: workers run in Linux containers, so never commit CRLF
        await RunGitAsync(clonePath, ["config", "core.autocrlf", "input"], ct);

        return clonePath;
    }

    /// <summary>
    /// Creates and checks out a new branch in the specified clone.
    /// </summary>
    /// <param name="clonePath">Path to the worker's local clone.</param>
    /// <param name="branchName">Name of the new branch to create.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CreateBranchAsync(string clonePath, string branchName, CancellationToken ct = default)
    {
        await RunGitAsync(clonePath, ["checkout", "-b", branchName], ct);
    }

    /// <summary>
    /// Repair the remote URL after a Docker container may have corrupted it.
    /// Workers run inside containers where the host path doesn't exist,
    /// so Copilot may modify .git/config. This resets it to the correct bare repo path.
    /// </summary>
    public async Task RepairRemoteAsync(string clonePath, CancellationToken ct = default)
    {
        await RunGitAsync(clonePath, ["remote", "set-url", "origin", _bareRepoPath], ct);
    }

    /// <summary>
    /// Force-pushes the specified branch from the clone to the bare repo origin.
    /// </summary>
    /// <param name="clonePath">Path to the worker's local clone.</param>
    /// <param name="branchName">Name of the branch to push.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PushBranchAsync(string clonePath, string branchName, CancellationToken ct = default)
    {
        await RunGitAsync(clonePath, ["push", "origin", branchName, "--force"], ct);
    }

    /// <summary>
    /// Pulls the latest commits for the specified branch from origin into the clone.
    /// </summary>
    /// <param name="clonePath">Path to the worker's local clone.</param>
    /// <param name="branchName">Name of the branch to pull.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PullBranchAsync(string clonePath, string branchName, CancellationToken ct = default)
    {
        await RunGitAsync(clonePath, ["fetch", "origin"], ct);
        await RunGitAsync(clonePath, ["checkout", branchName], ct);
        await RunGitAsync(clonePath, ["pull", "origin", branchName], ct);
    }

    /// <summary>
    /// Merges the specified branch into main and pushes the result to origin.
    /// Aborts the merge automatically if a conflict occurs.
    /// </summary>
    /// <param name="clonePath">Path to the worker's local clone.</param>
    /// <param name="branchName">Name of the feature branch to merge.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A tuple where <c>Success</c> indicates whether the merge succeeded
    /// and <c>Output</c> contains the git output or conflict details.
    /// </returns>
    public async Task<(bool Success, string Output)> MergeToMainAsync(
        string clonePath,
        string branchName,
        CancellationToken ct = default)
    {
        await RunGitAsync(clonePath, ["checkout", "main"], ct);
        await RunGitAsync(clonePath, ["pull", "origin", "main"], ct);

        try
        {
            var output = await RunGitAsync(clonePath, ["merge", branchName, "--no-ff", "-m", $"Merge {branchName}"], ct);
            await RunGitAsync(clonePath, ["push", "origin", "main"], ct);
            return (true, output);
        }
        catch (GitException ex)
        {
            await RunGitAsync(clonePath, ["merge", "--abort"], ct);
            return (false, ex.Output);
        }
    }

    /// <summary>
    /// Returns the diff between the current HEAD of the clone and the specified base branch.
    /// </summary>
    /// <param name="clonePath">Path to the worker's local clone.</param>
    /// <param name="baseBranch">Branch to diff against (defaults to "main").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The unified diff as a string.</returns>
    public async Task<string> GetDiffAsync(string clonePath, string baseBranch = "main", CancellationToken ct = default)
    {
        return await RunGitAsync(clonePath, ["diff", baseBranch], ct);
    }

    /// <summary>
    /// Reverts the last merge commit on main and force-pushes to origin.
    /// Used when post-merge verification fails.
    /// </summary>
    public async Task RevertLastMergeAsync(string clonePath, CancellationToken ct = default)
    {
        await RunGitAsync(clonePath, ["checkout", "main"], ct);
        await RunGitAsync(clonePath, ["reset", "--hard", "HEAD~1"], ct);
        await RunGitAsync(clonePath, ["push", "--force", "origin", "main"], ct);
    }

    /// <summary>
    /// Delete a directory with retries — on Windows, git processes may hold brief file locks.
    /// </summary>
    private static async Task ForceDeleteDirectoryAsync(string path, int maxRetries = 5)
    {
        for (var i = 0; i < maxRetries; i++)
        {
            if (!Directory.Exists(path))
                return;

            try
            {
                // Clear read-only attributes that git sets on pack files
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException) when (i < maxRetries - 1)
            {
                await Task.Delay(200 * (i + 1));
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                await Task.Delay(200 * (i + 1));
            }
        }
    }

    private static async Task<string> RunGitAsync(string workingDir, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");

        // Read stdout and stderr concurrently to avoid deadlock when buffers fill
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(ct);

        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;

        if (process.ExitCode != 0)
            throw new GitException(process.ExitCode, $"{stdout}\n{stderr}".Trim());

        return stdout.Trim();
    }
}

/// <summary>Exception thrown when a git command exits with a non-zero exit code.</summary>
public sealed class GitException(int exitCode, string output)
    : Exception($"git exited with code {exitCode}: {output}")
{
    /// <summary>The exit code returned by the git process.</summary>
    public int ExitCode => exitCode;
    /// <summary>Combined stdout and stderr output from the git process.</summary>
    public string Output => output;
}

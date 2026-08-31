using System.Diagnostics;
using CopilotHive.Services;
using CopilotHive.Shared;
using CopilotHive.Workers;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CopilotHive.Configuration;

/// <summary>
/// Manages the configuration repository: clones/pulls the repo, reads hive-config.yaml
/// and per-role AGENTS.md files, and commits AGENTS.md updates back.
/// <para>
/// <b>Credentials.</b> Every git operation resolves ONE credential for its whole duration
/// via <see cref="TokenResolver"/> (the OAuth bridge — an admin token stored in the database
/// on an OAuth-only deployment) falling back to the <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c>
/// environment chain. The resolved credential is injected into the <c>origin</c> remote
/// immediately before the operation's FIRST network command and is used to redact the
/// credential out of any exception text.
/// </para>
/// </summary>
public class ConfigRepoManager
{
    private readonly string _configRepoUrl;
    private readonly string _localPath;
    private HiveConfigFile? _cachedConfig;
    private readonly SemaphoreSlim _gitLock = new(1, 1);

    /// <summary>
    /// The result of a single git invocation. A NON-ZERO exit code is RETURNED here — never
    /// thrown — so the calling wrapper decides whether that exit code is a failure.
    /// </summary>
    /// <param name="ExitCode">The process exit code.</param>
    /// <param name="Stdout">The captured standard output.</param>
    /// <param name="Stderr">The captured standard error.</param>
    internal record GitRunResult(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// Test seam replacing the real git process launch. <c>null</c> (the default) runs the
    /// real <c>git</c> process, so production behaviour is unchanged.
    /// <para>
    /// Arguments are (working directory, git arguments, cancellation token). A non-zero exit
    /// must be RETURNED via <see cref="GitRunResult"/>; a seam that THROWS is wrapped and
    /// redacted exactly like a core failure.
    /// </para>
    /// </summary>
    internal Func<string, string[], CancellationToken, Task<GitRunResult>>? GitRunner { get; set; }

    /// <summary>
    /// Optional OAuth token bridge. When set, it is awaited ONCE per git operation and its
    /// result becomes the FIRST candidate of the credential chain, ahead of the
    /// <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c> environment variables.
    /// <para>
    /// <b>Failure semantics.</b> A caller cancellation (an <see cref="OperationCanceledException"/>
    /// raised while the caller's token is cancelled) is RETHROWN. Every other exception — and a
    /// <c>null</c> result — falls through to the environment-only chain.
    /// </para>
    /// </summary>
    public Func<CancellationToken, Task<string?>>? TokenResolver { get; set; }

    /// <summary>
    /// Attempts to run <c>git merge --abort</c> on a best-effort basis.
    /// Any failure is silently ignored so the original exception can propagate.
    /// <para>
    /// A CALLER-CANCELLATION <see cref="OperationCanceledException"/> is deliberately NOT
    /// swallowed: swallowing it would let a cancelled operation continue running further git
    /// commands. It is rethrown so cancellation still terminates the operation.
    /// </para>
    /// </summary>
    private async Task TryAbortMergeAsync(string localPath, string? credential, CancellationToken ct)
    {
        try
        {
            await RunGitAsync(localPath, ["merge", "--abort"], credential, ct);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw;

            // Best-effort: ignore failures (e.g., no merge in progress).
        }
    }

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitNull)
        .Build();

    /// <summary>
    /// Initialises a new <see cref="ConfigRepoManager"/>.
    /// </summary>
    /// <param name="configRepoUrl">URL of the remote configuration repository.</param>
    /// <param name="localPath">Local path where the config repo will be cloned.</param>
    public ConfigRepoManager(string configRepoUrl, string localPath)
    {
        _configRepoUrl = configRepoUrl;
        _localPath = Path.GetFullPath(localPath);
    }

    /// <summary>
    /// The local filesystem path where the config repo is cloned.
    /// </summary>
    public string LocalPath => _localPath;

    /// <summary>
    /// The remote configuration repository URL this manager was constructed with.
    /// <para>
    /// This is the SANITIZED operator value (see <see cref="ConfigRepoUrlSanitizer"/>) and is
    /// therefore safe to provision to workers — it never carries credentials. It is distinct
    /// from <see cref="LocalPath"/> (the local clone directory) and from the credential-bearing
    /// clone URL built internally for git operations.
    /// </para>
    /// </summary>
    public string ConfigRepoUrl => _configRepoUrl;

    /// <summary>
    /// Clones the config repo, or pulls latest if already cloned.
    /// <para>
    /// The existing-clone PULL path refreshes the <c>origin</c> credential immediately before
    /// the pull (its first network command). The CLONE path is exempt from that pre-network
    /// refresh: the credential is resolved once at the start, injected into the clone URL, and
    /// the <c>origin</c> remote is normalized back to the sanitized URL immediately AFTER the
    /// clone.
    /// </para>
    /// </summary>
    public async Task SyncRepoAsync(CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            if (Directory.Exists(Path.Combine(_localPath, ".git")))
            {
                // The pull is the first network command — refresh origin immediately before it.
                var credential = await EnsureOriginCredentialAsync(ct);
                try
                {
                    await RunGitAsync(_localPath, ["pull"], credential, ct);
                }
                catch (Exception ex)
                {
                    // A CALLER cancellation is not a merge failure: propagate it immediately
                    // and run NO cleanup command — the caller asked us to stop.
                    if (ex is OperationCanceledException && ct.IsCancellationRequested)
                        throw;

                    await TryAbortMergeAsync(_localPath, credential, ct);
                    throw;
                }
            }
            else
            {
                var credential = await ResolveCredentialAsync(ct);
                var parent = Path.GetDirectoryName(_localPath)!;
                Directory.CreateDirectory(parent);
                var dirName = Path.GetFileName(_localPath);
                var cloneUrl = InjectTokenIntoUrl(_configRepoUrl, credential);
                await RunGitAsync(parent, ["clone", cloneUrl, dirName], credential, ct);
                // Normalize origin back to the sanitized URL right AFTER the clone — one code
                // path, regardless of whether a credential was present.
                await RunGitAsync(_localPath, ["remote", "set-url", "origin", _configRepoUrl], credential, ct);
                await RunGitAsync(_localPath, ["config", "user.email", "copilothive@local"], credential, ct);
                await RunGitAsync(_localPath, ["config", "user.name", "CopilotHive"], credential, ct);
            }

            _cachedConfig = null;
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Resolves the operation credential ONCE: the <see cref="TokenResolver"/> result (when
    /// present and successful) followed by the <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c> environment
    /// candidates, selected by <see cref="GitCredentialResolver.Resolve"/> (returned UNCHANGED).
    /// </summary>
    /// <remarks>
    /// A caller cancellation from the resolver propagates. Any OTHER resolver failure is caught
    /// and the environment-only chain is used, so a broken OAuth bridge can never take down an
    /// otherwise working environment-credentialed deployment.
    /// </remarks>
    private async Task<string?> ResolveCredentialAsync(CancellationToken ct)
    {
        string? oauthToken = null;
        if (TokenResolver is not null)
        {
            try
            {
                oauthToken = await TokenResolver(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Any other resolver failure falls through to the environment-only chain.
                oauthToken = null;
            }
        }

        return GitCredentialResolver.Resolve(
            oauthToken,
            Environment.GetEnvironmentVariable("GH_TOKEN"),
            Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
    }

    /// <summary>
    /// Resolves the operation credential and, when one is present, points <c>origin</c> at the
    /// credential-bearing URL via <c>git remote set-url</c>. Returns the resolved credential so
    /// the git wrappers can apply the literal redaction pass.
    /// <para>
    /// <b>The stale-origin rule.</b> A <c>null</c> resolution performs NO <c>set-url</c>: a
    /// transient resolver failure must never strip a working persisted credential. The declared
    /// and accepted consequence is that a revoked token can persist in <c>.git/config</c> until
    /// a later successful resolution replaces it; there is no automatic removal.
    /// </para>
    /// </summary>
    private async Task<string?> EnsureOriginCredentialAsync(CancellationToken ct)
    {
        var credential = await ResolveCredentialAsync(ct);
        if (credential is null)
            return null;

        await RunGitAsync(
            _localPath,
            ["remote", "set-url", "origin", InjectTokenIntoUrl(_configRepoUrl, credential)],
            credential,
            ct);
        return credential;
    }

    /// <summary>
    /// Injects <paramref name="credential"/> into an HTTPS GitHub URL for authentication.
    /// Purely synchronous and parameter-driven — it never reads the environment.
    /// </summary>
    /// <param name="url">The sanitized repository URL.</param>
    /// <param name="credential">The resolved credential, or <c>null</c>/empty for no injection.</param>
    /// <returns>The credential-bearing URL, or <paramref name="url"/> unchanged.</returns>
    private static string InjectTokenIntoUrl(string url, string? credential)
    {
        if (string.IsNullOrEmpty(credential))
            return url;

        if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            return url.Replace("https://github.com/", $"https://x-access-token:{credential}@github.com/");

        return url;
    }

    /// <summary>
    /// Loads and parses hive-config.yaml from the config repo root.
    /// </summary>
    public async Task<HiveConfigFile> LoadConfigAsync(CancellationToken ct = default)
    {
        if (_cachedConfig is not null)
            return _cachedConfig;

        var configPath = Path.Combine(_localPath, "hive-config.yaml");
        if (!File.Exists(configPath))
            throw new FileNotFoundException("Config file not found in config repo.", configPath);

        var yaml = await File.ReadAllTextAsync(configPath, ct);
        _cachedConfig = ParseConfig(yaml);
        return _cachedConfig;
    }

    /// <summary>
    /// Parses a YAML string into a <see cref="HiveConfigFile"/>.
    /// The returned instance is marked <see cref="HiveConfigFile.IsConfigured"/> = <c>true</c>
    /// (it came from an actual config repo file, unlike the no-repo fallback singleton).
    /// <para>
    /// Blank/whitespace <c>model:</c> values (orchestrator, worker role, composer default)
    /// are normalized to <c>null</c> — the canonical "unset" representation. The retired
    /// <c>composer.models</c> key is a FATAL parse failure naming the key.
    /// </para>
    /// </summary>
    internal static HiveConfigFile ParseConfig(string yaml)
    {
        RejectRetiredComposerModels(yaml);

        var config = YamlDeserializer.Deserialize<HiveConfigFile>(yaml) ?? new HiveConfigFile();
        config.IsConfigured = true;

        // Blank/whitespace model values normalize to null (UNCONFIGURED) — orchestrator,
        // worker role, and composer default alike. This is the single normalization point.
        if (string.IsNullOrWhiteSpace(config.Orchestrator.Model))
            config.Orchestrator.Model = null;
        foreach (var wc in config.Workers.Values)
        {
            if (wc is not null && string.IsNullOrWhiteSpace(wc.Model))
                wc.Model = null;
        }
        if (config.Composer is not null && string.IsNullOrWhiteSpace(config.Composer.Model))
            config.Composer.Model = null;

        foreach (var repo in config.Repositories)
        {
            if (repo.Release is not null &&
                string.IsNullOrWhiteSpace(repo.Release.MergeTo) &&
                string.IsNullOrWhiteSpace(repo.Release.TagBranch))
            {
                repo.Release = null;
            }
        }
        return config;
    }

    /// <summary>
    /// The <c>composer.models</c> YAML key is retired: its presence is a FATAL parse failure
    /// naming the key. The deserializer's <c>IgnoreUnmatchedProperties</c> would silently drop
    /// it, so the retired key is detected explicitly via the raw YAML representation model.
    /// </summary>
    private static void RejectRetiredComposerModels(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return;

        using var reader = new StringReader(yaml);
        var stream = new YamlStream();
        stream.Load(reader);

        foreach (var document in stream.Documents)
        {
            if (document.RootNode is not YamlMappingNode root)
                continue;

            if (root.Children.TryGetValue(new YamlScalarNode("composer"), out var composerNode)
                && composerNode is YamlMappingNode composerMap
                && composerMap.Children.ContainsKey(new YamlScalarNode("models")))
            {
                throw new YamlDotNet.Core.YamlException(
                    "The 'composer.models' key is retired and no longer supported. " +
                    "Remove 'composer.models' from hive-config.yaml; the global " +
                    "'models.available_models' list is the sole model catalog.");
            }
        }
    }

    /// <summary>
    /// Serializes <paramref name="config"/> to YAML and writes it to <c>hive-config.yaml</c>
    /// in the local config repo path, then updates the in-memory cache.
    /// Call <see cref="CommitFileAsync"/> afterward to commit and push the change.
    /// </summary>
    /// <param name="config">The updated configuration to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteConfigAsync(HiveConfigFile config, CancellationToken ct = default)
    {
        var yaml = YamlSerializer.Serialize(config);
        var configPath = Path.Combine(_localPath, "hive-config.yaml");
        await File.WriteAllTextAsync(configPath, yaml, ct);
        _cachedConfig = config;
    }

    /// <summary>
    /// Loads a per-role AGENTS.md file from the config repo (agents/{role}.agents.md).
    /// Returns null if the file does not exist.
    /// </summary>
    public async Task<string?> LoadAgentsMdAsync(WorkerRole role, CancellationToken ct = default)
    {
        var agentsPath = Path.Combine(_localPath, "agents", $"{role.ToRoleName()}.agents.md");
        if (!File.Exists(agentsPath))
            return null;

        return await File.ReadAllTextAsync(agentsPath, ct);
    }

    /// <summary>
    /// Checks whether a repository URL is in the allowed list.
    /// Compares normalized URLs (trimmed, case-insensitive, trailing-slash-insensitive).
    /// </summary>
    public bool IsRepositoryAllowed(string repoUrl)
    {
        if (_cachedConfig is null)
            return false;

        var normalized = NormalizeUrl(repoUrl);
        return _cachedConfig.Repositories.Exists(r => NormalizeUrl(r.Url) == normalized);
    }

    /// <summary>
    /// Commits and pushes a single file that has already been written to disk.
    /// Used to persist config file updates back to the config repo.
    /// </summary>
    public virtual async Task CommitFileAsync(string filePath, string commitMessage, CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            // add + diff --cached are LOCAL — no credential is resolved for them.
            await RunGitAsync(_localPath, ["add", filePath], credential: null, ct);
            var exitCode = await RunGitOptionalAsync(_localPath, ["diff", "--cached", "--quiet"], credential: null, ct);
            if (exitCode == 0)
            {
                // No diff: the push IS this path's first network command.
                var pushCredential = await EnsureOriginCredentialAsync(ct);
                await PushOnlyAsync(pushCredential, ct);
                return;
            }
            // commit is LOCAL too — the refresh waits until after it.
            await RunGitAsync(_localPath, ["commit", "-m", commitMessage], credential: null, ct);
            var credential = await EnsureOriginCredentialAsync(ct);
            await PushWithConflictRecoveryAsync(credential, ct);
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Stages a file deletion and commits/pushes the removal.
    /// The file should already be deleted from the working tree before calling this method.
    /// Uses <c>git rm --cached</c> to stage the removal from the index without touching
    /// the local file (which the caller has already removed).
    /// </summary>
    public virtual async Task DeleteFileAsync(string filePath, string commitMessage, CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            // rm --cached + diff --cached are LOCAL — no credential is resolved for them.
            await RunGitAsync(_localPath, ["rm", "--cached", "--ignore-unmatch", filePath], credential: null, ct);
            var exitCode = await RunGitOptionalAsync(_localPath, ["diff", "--cached", "--quiet"], credential: null, ct);
            if (exitCode == 0)
            {
                // No diff: the push IS this path's first network command.
                var pushCredential = await EnsureOriginCredentialAsync(ct);
                await PushOnlyAsync(pushCredential, ct);
                return;
            }
            // commit is LOCAL too — the refresh waits until after it.
            await RunGitAsync(_localPath, ["commit", "-m", commitMessage], credential: null, ct);
            var credential = await EnsureOriginCredentialAsync(ct);
            await PushWithConflictRecoveryAsync(credential, ct);
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Stages a batch of file deletions and commits/pushes them as a SINGLE commit.
    /// The files should already be deleted from the working tree before calling this method.
    /// Uses one <c>git rm --cached</c> invocation scoped to all of <paramref name="filePaths"/>,
    /// so the removals land in exactly one commit instead of one commit per file.
    /// </summary>
    /// <param name="filePaths">Relative paths of the files to remove from the index. Must not be null.</param>
    /// <param name="commitMessage">Commit message used for the single commit.</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task DeleteFilesAsync(IReadOnlyList<string> filePaths, string commitMessage, CancellationToken ct = default)
    {
        if (filePaths is null)
            throw new ArgumentNullException(nameof(filePaths));
        if (filePaths.Count == 0)
            return;

        await _gitLock.WaitAsync(ct);
        try
        {
            string[] args = ["rm", "--cached", "--ignore-unmatch", .. filePaths];
            // rm --cached + diff --cached are LOCAL — no credential is resolved for them.
            await RunGitAsync(_localPath, args, credential: null, ct);
            var exitCode = await RunGitOptionalAsync(_localPath, ["diff", "--cached", "--quiet"], credential: null, ct);
            if (exitCode == 0)
            {
                // No diff: the push IS this path's first network command.
                var pushCredential = await EnsureOriginCredentialAsync(ct);
                await PushOnlyAsync(pushCredential, ct);
                return;
            }
            // commit is LOCAL too — the refresh waits until after it.
            await RunGitAsync(_localPath, ["commit", "-m", commitMessage], credential: null, ct);
            var credential = await EnsureOriginCredentialAsync(ct);
            await PushWithConflictRecoveryAsync(credential, ct);
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Stages all changes, commits with the given message, and pushes to the remote.
    /// Used by the Composer to persist AGENTS.md updates made via config repo tools.
    /// </summary>
    /// <param name="commitMessage">Commit message to use.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CommitAllChangesAsync(string commitMessage, CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            // add --all + diff --cached are LOCAL: the no-diff return below exits with NO
            // credential resolution and NO set-url.
            await RunGitAsync(_localPath, ["add", "--all"], credential: null, ct);
            var exitCode = await RunGitOptionalAsync(_localPath, ["diff", "--cached", "--quiet"], credential: null, ct);
            if (exitCode == 0)
                return;
            // commit is LOCAL too — the refresh waits until after it, immediately before the
            // pull inside PushWithConflictRecoveryAsync (this path's first network command).
            await RunGitAsync(_localPath, ["commit", "-m", commitMessage], credential: null, ct);
            var credential = await EnsureOriginCredentialAsync(ct);
            await PushWithConflictRecoveryAsync(credential, ct);
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Attempts a plain git push without pull/conflict recovery. Does NOT acquire _gitLock
    /// (caller must already hold it). Propagates on failure — no reset --hard.
    /// </summary>
    /// <param name="credential">The operation's already-resolved credential (never re-resolved here).</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task PushOnlyAsync(string? credential, CancellationToken ct)
    {
        await RunGitAsync(_localPath, ["push"], credential, ct);
    }

    /// <summary>
    /// Pulls the latest changes and pushes the local commit, recovering from merge conflicts.
    /// If a plain pull fails (likely a conflict), the merge is aborted, the working tree is
    /// reset to the local commit, and a rebase pull is attempted. If the rebase also fails,
    /// the rebase is aborted, the tree is reset hard, and the local commit is pushed as-is.
    /// </summary>
    /// <param name="credential">
    /// The operation's already-resolved credential. The recovery path REUSES it — it never
    /// resolves the chain a second time.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Neither catch may classify a CALLER cancellation as a merge/rebase conflict: an
    /// <see cref="OperationCanceledException"/> raised while <paramref name="ct"/> is cancelled
    /// propagates immediately, so no recovery command and no push runs after it.
    /// </remarks>
    private async Task PushWithConflictRecoveryAsync(string? credential, CancellationToken ct)
    {
        try
        {
            await RunGitAsync(_localPath, ["pull"], credential, ct);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw;

            // Pull failed — likely a merge conflict. Recover by aborting the merge,
            // resetting to the local commit, and retrying with rebase.
            await TryAbortMergeAsync(_localPath, credential, ct);
            await RunGitAsync(_localPath, ["reset", "--hard", "HEAD"], credential, ct);
            try
            {
                await RunGitAsync(_localPath, ["pull", "--rebase"], credential, ct);
            }
            catch (Exception rebaseEx)
            {
                if (rebaseEx is OperationCanceledException && ct.IsCancellationRequested)
                    throw;

                // Rebase also failed — abort rebase, reset hard, and push local commit as-is
                await RunGitAsync(_localPath, ["rebase", "--abort"], credential, ct);
                await RunGitAsync(_localPath, ["reset", "--hard", "HEAD"], credential, ct);
            }
        }
        await RunGitAsync(_localPath, ["push"], credential, ct);
    }

    /// <summary>
    /// Resets the local config repo clone to match the remote, discarding any local changes.
    /// Used for recovery when the repo is stuck in a conflicted state.
    /// </summary>
    public async Task ResetToRemoteAsync(CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            // merge --abort is local and best-effort; the fetch is the first network command,
            // so the origin refresh runs immediately before it.
            await TryAbortMergeAsync(_localPath, credential: null, ct);
            var credential = await EnsureOriginCredentialAsync(ct);
            await RunGitAsync(_localPath, ["fetch", "origin"], credential, ct);
            await RunGitAsync(_localPath, ["reset", "--hard", "origin/HEAD"], credential, ct);
        }
        finally
        {
            _gitLock.Release();
        }
    }

    private static string NormalizeUrl(string url)
    {
        return url.Trim().TrimEnd('/').ToLowerInvariant();
    }

    /// <summary>
    /// Redacts the combined git output for embedding in an exception message: the URL scanner
    /// pass plus, when a credential is known, an ordinal literal replacement that also catches a
    /// BARE token no URL scanner would see.
    /// </summary>
    private static string Sanitize(string text, string? credential)
    {
        var redacted = GitUrlRedactor.Redact(text) ?? string.Empty;
        if (!string.IsNullOrEmpty(credential))
            redacted = redacted.Replace(credential, "[redacted]", StringComparison.Ordinal);
        return redacted;
    }

    /// <summary>
    /// Builds the sanitized "git exited with code" exception for a non-zero exit.
    /// </summary>
    private static InvalidOperationException BuildGitFailure(GitRunResult result, string? credential) =>
        new(Sanitize($"git exited with code {result.ExitCode}: {result.Stdout}\n{result.Stderr}".Trim(), credential));

    /// <summary>
    /// Wraps an exception thrown by the <see cref="GitRunner"/> seam (or by the core launch)
    /// so that neither <see cref="Exception.Message"/> nor <see cref="object.ToString"/> can
    /// leak a credential: the message is sanitized, the ORIGINAL exception type name is kept as
    /// text, and <see cref="Exception.InnerException"/> is deliberately <c>null</c>.
    /// </summary>
    private static InvalidOperationException WrapGitException(Exception ex, string? credential) =>
        new(Sanitize($"{ex.GetType().Name}: {ex.Message}", credential));

    /// <summary>
    /// Runs git and returns the raw result — a non-zero exit code is RETURNED, never thrown.
    /// Routes through <see cref="GitRunner"/> when the seam is set, otherwise launches the real
    /// <c>git</c> process with the <c>-c core.autocrlf=false</c> injection.
    /// </summary>
    private async Task<GitRunResult> RunGitCoreAsync(string workingDir, string[] args, CancellationToken ct)
    {
        if (GitRunner is not null)
            return await GitRunner(workingDir, args, ct);

        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // Force LF line endings regardless of the host's global/system git config
        // (e.g. Windows commonly defaults core.autocrlf=true) so config file contents
        // committed/read by the Brain are identical on any OS.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.autocrlf=false");
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(ct);

        return new GitRunResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    /// <summary>
    /// Runs the core and applies the shared failure translation: a caller cancellation
    /// propagates UNREDACTED (its message carries no git output by construction); any other
    /// exception is wrapped + sanitized with no inner exception.
    /// </summary>
    private async Task<GitRunResult> RunGitGuardedAsync(
        string workingDir, string[] args, string? credential, CancellationToken ct)
    {
        try
        {
            return await RunGitCoreAsync(workingDir, args, ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException && ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw WrapGitException(ex, credential);
        }
    }

    /// <summary>
    /// Runs git tolerating exit code 1 (the "difference found" convention of
    /// <c>diff --quiet</c>); an exit code greater than 1 throws a sanitized exception.
    /// </summary>
    private async Task<int> RunGitOptionalAsync(string workingDir, string[] args, string? credential, CancellationToken ct)
    {
        var result = await RunGitGuardedAsync(workingDir, args, credential, ct);

        if (result.ExitCode > 1)
            throw BuildGitFailure(result, credential);

        return result.ExitCode;
    }

    /// <summary>
    /// Runs git and returns trimmed stdout; a non-zero exit throws a sanitized exception.
    /// </summary>
    private async Task<string> RunGitAsync(string workingDir, string[] args, string? credential, CancellationToken ct)
    {
        var result = await RunGitGuardedAsync(workingDir, args, credential, ct);

        if (result.ExitCode != 0)
            throw BuildGitFailure(result, credential);

        return result.Stdout.Trim();
    }
}

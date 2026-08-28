using CopilotHive.Worker;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Redaction tests for the <see cref="GitOperations"/> exception-construction boundary.
/// <para>
/// Every <see cref="GitOperationException"/> is built from a git argument string and/or git's
/// stderr, both of which can carry a credential-bearing remote URL. These tests drive the real
/// git CLI against local temp repositories with a credential-bearing URL that is never resolved
/// (the failures are local: "destination already exists", "pathspec did not match", "src refspec
/// does not match") — no network call and no live credential is involved.
/// </para>
/// </summary>
public sealed class GitOperationsRedactionTests : IAsyncLifetime
{
    private const string Token = "ghp_gitops_secret_value";
    private const string CredentialUrl =
        $"https://x-access-token:{Token}@github.com/acme/widgets.git";
    private const string RedactedUrl = "https://github.com/acme/widgets.git";

    private string _root = string.Empty;

    /// <inheritdoc/>
    public ValueTask InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"GitOpsRedact_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
            await GitOperations.ForceDeleteDirectoryAsync(_root);
    }

    // ── CloneRepositoryAsync: the interpolated '{url}' ────────────────────────

    /// <summary>
    /// The clone failure message interpolates the URL directly. The failure used here is purely
    /// local — the destination directory already exists and is non-empty — so git never contacts
    /// a remote, yet the message still carries the credential-bearing URL before redaction.
    /// </summary>
    [Fact]
    public async Task CloneRepositoryAsync_WhenCloneFails_MessageHasNoCredential()
    {
        var target = Path.Combine(_root, "occupied");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(
            Path.Combine(target, "existing.txt"), "blocks the clone",
            TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            GitOperations.CloneRepositoryAsync(
                CredentialUrl, target, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.DoesNotContain("x-access-token", ex.Message);
        // The credential-FREE URL is still reported, so the message stays diagnostic.
        Assert.Contains(RedactedUrl, ex.Message);
        Assert.StartsWith($"Failed to clone '{RedactedUrl}'", ex.Message);
    }

    /// <summary>
    /// A query-token clone URL is redacted through the same boundary, keeping the other
    /// parameters.
    /// </summary>
    [Fact]
    public async Task CloneRepositoryAsync_QueryTokenUrl_MessageHasNoCredential()
    {
        const string QueryUrl = "https://github.com/acme/widgets.git?token=ghp_query_secret&ref=main";
        var target = Path.Combine(_root, "occupied-query");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(
            Path.Combine(target, "existing.txt"), "blocks the clone",
            TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            GitOperations.CloneRepositoryAsync(
                QueryUrl, target, TestContext.Current.CancellationToken));

        Assert.DoesNotContain("ghp_query_secret", ex.Message);
        Assert.Contains("https://github.com/acme/widgets.git?ref=main", ex.Message);
    }

    /// <summary>
    /// A credential-free URL is reported verbatim: redaction never rewrites an innocent URL.
    /// </summary>
    [Fact]
    public async Task CloneRepositoryAsync_CredentialFreeUrl_MessageIsUnchanged()
    {
        var target = Path.Combine(_root, "occupied-plain");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(
            Path.Combine(target, "existing.txt"), "blocks the clone",
            TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            GitOperations.CloneRepositoryAsync(
                RedactedUrl, target, TestContext.Current.CancellationToken));

        Assert.Contains($"Failed to clone '{RedactedUrl}'", ex.Message);
    }

    // ── CheckoutBranchAsync: the stderr copy ──────────────────────────────────

    /// <summary>
    /// The checkout failure message embeds git's stderr, which echoes the pathspec it was given.
    /// Passing a credential-bearing URL as the branch name reproduces a credential inside stderr
    /// deterministically and without any network access.
    /// </summary>
    [Fact]
    public async Task CheckoutBranchAsync_WhenStderrEchoesCredentialUrl_MessageHasNoCredential()
    {
        var repo = await InitRepoAsync("checkout-repo");

        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            GitOperations.CheckoutBranchAsync(
                repo, CredentialUrl, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.Contains("Failed to checkout branch", ex.Message);
        // git's own "did not match any file(s)" wording survives — only the credential is gone.
        Assert.Contains(RedactedUrl, ex.Message);
    }

    // ── PushBranchAsync: the stderr copy ──────────────────────────────────────

    /// <summary>
    /// A push to a branch that does not exist fails LOCALLY ("invalid refspec"), yet git echoes
    /// the refspec it was handed straight back into stderr. A query-token credential is echoed
    /// verbatim by git (unlike userinfo, which git redacts itself), so this asserts OUR boundary.
    /// </summary>
    [Fact]
    public async Task PushBranchAsync_WhenStderrEchoesQueryToken_MessageHasNoCredential()
    {
        var repo = await InitRepoAsync("push-query-repo");
        await CommitFileAsync(repo, "seed.txt", "seed");
        await RunAsync(repo, $"remote add origin {CredentialUrl}");

        // git echoes the (invalid) refspec verbatim, credential and all.
        const string TokenRefspec = "https://github.com/acme/widgets?token=ghp_push_secret";

        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            GitOperations.PushBranchAsync(
                repo, TokenRefspec, TestContext.Current.CancellationToken));

        Assert.DoesNotContain("ghp_push_secret", ex.Message);
        Assert.Contains("invalid refspec", ex.Message);
        Assert.Contains("https://github.com/acme/widgets", ex.Message);
    }

    /// <summary>
    /// A push to a branch that does not exist fails LOCALLY ("src refspec ... does not match
    /// any"). git redacts userinfo in its own stderr, so this asserts the surrounding message
    /// (which interpolates the branch name) is credential-free end to end.
    /// </summary>
    [Fact]
    public async Task PushBranchAsync_WhenStderrEchoesCredentialRemote_MessageHasNoCredential()
    {
        var repo = await InitRepoAsync("push-repo");
        await CommitFileAsync(repo, "seed.txt", "seed");
        await RunAsync(repo, $"remote add origin {CredentialUrl}");

        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            GitOperations.PushBranchAsync(
                repo, "branch-that-does-not-exist", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.DoesNotContain("x-access-token", ex.Message);
        Assert.Contains("Failed to push branch 'branch-that-does-not-exist'", ex.Message);
    }

    // ── Raw stdout/stderr stay functional data ────────────────────────────────

    /// <summary>
    /// <see cref="GitOperations.RunGitCommandAsync"/> is the raw seam every parser reads: SHAs,
    /// porcelain status and numstat diffs. It must return git's output verbatim — the redactor is
    /// applied only when a message is CONSTRUCTED.
    /// </summary>
    [Fact]
    public async Task RunGitCommandAsync_ReturnsRawStdoutUnmodified()
    {
        var repo = await InitRepoAsync("raw-repo");
        await CommitFileAsync(repo, "file.txt", "content");

        var (exitCode, stdout, _) = await GitOperations.RunGitCommandAsync(
            repo, "rev-parse HEAD", TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(40, stdout.Trim().Length);
        Assert.Matches("^[0-9a-f]{40}$", stdout.Trim());
    }

    /// <summary>
    /// Raw stderr from a failing command is likewise returned untouched — the credential-bearing
    /// remote is still present there, which is exactly what makes the redaction at the exception
    /// boundary meaningful (and proves nothing mutates the stream itself).
    /// </summary>
    [Fact]
    public async Task RunGitCommandAsync_ReturnsRawStderrUnmodified()
    {
        var repo = await InitRepoAsync("raw-stderr-repo");
        await CommitFileAsync(repo, "seed.txt", "seed");
        await RunAsync(repo, $"remote add origin {CredentialUrl}");

        var (exitCode, _, stderr) = await GitOperations.RunGitCommandAsync(
            repo, "push origin branch-that-does-not-exist --force",
            TestContext.Current.CancellationToken);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("does not match any", stderr);
    }

    /// <summary>
    /// Status parsing is unaffected: a filename that merely LOOKS like a URL is functional data
    /// and survives verbatim through the changed-file list.
    /// </summary>
    [Fact]
    public async Task GetGitStatusAsync_ChangedFilePathsAreNeverRedacted()
    {
        var repo = await InitRepoAsync("status-repo");
        await CommitFileAsync(repo, "seed.txt", "seed");
        await RunAsync(repo, "checkout -b feature");
        await CommitFileAsync(repo, "notes.txt", "https://user:pw@example.com/o/r is a link");

        var summary = await GitOperations.GetGitStatusAsync(
            repo, baseBranch: null, TestContext.Current.CancellationToken);

        Assert.Contains("notes.txt", summary.ChangedFiles);
        Assert.Equal(1, summary.FilesChanged);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> InitRepoAsync(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        await RunAsync(dir, "init");
        await RunAsync(dir, "config user.email \"test@example.com\"");
        await RunAsync(dir, "config user.name \"Test\"");
        await RunAsync(dir, "config commit.gpgsign false");
        return dir;
    }

    private static async Task CommitFileAsync(string repoDir, string fileName, string content)
    {
        await File.WriteAllTextAsync(
            Path.Combine(repoDir, fileName), content, TestContext.Current.CancellationToken);
        await RunAsync(repoDir, $"add {fileName}");
        await RunAsync(repoDir, $"commit -m \"add {fileName}\"");
    }

    private static async Task RunAsync(string workDir, string args) =>
        await GitOperations.RunGitCommandAsync(
            workDir, args, TestContext.Current.CancellationToken);
}

using CopilotHive.Git;

using Microsoft.Extensions.Logging;

namespace CopilotHive.Tests;

/// <summary>
/// Flow A of the credential-URL redaction goal: the orchestrator Brain's clone path.
/// <para>
/// <c>PipelineHelpers.InjectTokenIntoUrl</c> hands <see cref="BrainRepoManager.EnsureCloneAsync"/>
/// a credential-bearing remote URL. These tests drive the manager through its optional
/// <see cref="BrainGitRequest"/>/<see cref="BrainGitResult"/> runner seam — which returns RAW
/// process results — so the PRODUCTION log/exception construction (and therefore the production
/// redaction) is what gets asserted. No git process, no network, no live credential.
/// </para>
/// </summary>
public sealed class BrainRepoManagerRedactionTests : IDisposable
{
    private const string Token = "ghp_brain_clone_secret";
    private const string CredentialUrl =
        $"https://x-access-token:{Token}@github.com/acme/widgets.git";
    private const string RedactedUrl = "https://github.com/acme/widgets.git";

    private readonly string _tempDir;

    public BrainRepoManagerRedactionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            TestHelpers.ForceDeleteDirectory(_tempDir);
    }

    /// <summary>
    /// Records every git invocation the manager makes and replies with scripted RAW results.
    /// </summary>
    private sealed class RecordingGitRunner
    {
        private readonly Func<BrainGitRequest, BrainGitResult> _respond;

        public RecordingGitRunner(Func<BrainGitRequest, BrainGitResult>? respond = null)
            => _respond = respond ?? (_ => new BrainGitResult(0, string.Empty, string.Empty));

        /// <summary>Every request the manager issued, in order.</summary>
        public List<BrainGitRequest> Requests { get; } = [];

        public BrainGitResult Run(BrainGitRequest request)
        {
            Requests.Add(request);
            return _respond(request);
        }
    }

    // ── The runner seam itself ────────────────────────────────────────────────

    /// <summary>
    /// The seam is an OPTIONAL TRAILING parameter: the two-argument constructor every existing
    /// call site uses still compiles and still runs the real process-based runner (null seam).
    /// </summary>
    [Fact]
    public void Constructor_WithoutRunner_IsStillAvailable()
    {
        var manager = new BrainRepoManager(_tempDir, new TestLogger<BrainRepoManager>());

        Assert.EndsWith("repos", manager.WorkDirectory.Replace('\\', '/'));
    }

    /// <summary>
    /// The seam receives the RAW, credential-bearing URL — redaction is a message-construction
    /// concern only and must never change what is handed to git.
    /// </summary>
    [Fact]
    public async Task EnsureCloneAsync_PassesRawCredentialUrlToTheRunner()
    {
        var runner = new RecordingGitRunner();
        var manager = new BrainRepoManager(
            _tempDir, new TestLogger<BrainRepoManager>(), runner.Run);

        await manager.EnsureCloneAsync(
            "widgets", CredentialUrl, "main", TestContext.Current.CancellationToken);

        var cloneRequest = Assert.Single(runner.Requests, r => r.Arguments.Contains("clone"));
        Assert.Contains(CredentialUrl, cloneRequest.Arguments);
        Assert.Contains(Token, string.Join(' ', cloneRequest.Arguments));
    }

    // ── Boundary 1: the pre-clone log ─────────────────────────────────────────

    [Fact]
    public async Task EnsureCloneAsync_PreCloneLog_IsCredentialFree()
    {
        var logger = new TestLogger<BrainRepoManager>();
        var runner = new RecordingGitRunner();
        var manager = new BrainRepoManager(_tempDir, logger, runner.Run);

        await manager.EnsureCloneAsync(
            "widgets", CredentialUrl, "main", TestContext.Current.CancellationToken);

        var entry = Assert.Single(
            logger.LogEntries, e => e.Message.Contains("Creating Brain clone"));
        Assert.DoesNotContain(Token, entry.Message);
        Assert.DoesNotContain("x-access-token", entry.Message);
        // The credential-FREE URL may (and should) still be logged.
        Assert.Contains(RedactedUrl, entry.Message);
    }

    [Fact]
    public async Task EnsureCloneAsync_NoLogEntryAnywhereCarriesTheCredential()
    {
        var logger = new TestLogger<BrainRepoManager>();
        var runner = new RecordingGitRunner();
        var manager = new BrainRepoManager(_tempDir, logger, runner.Run);

        await manager.EnsureCloneAsync(
            "widgets", CredentialUrl, "main", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(Token));
    }

    /// <summary>
    /// A query-token clone URL is redacted by the same log boundary.
    /// </summary>
    [Fact]
    public async Task EnsureCloneAsync_QueryTokenUrl_PreCloneLogIsCredentialFree()
    {
        const string QueryUrl = "https://github.com/acme/widgets.git?token=ghp_query_secret&ref=main";
        var logger = new TestLogger<BrainRepoManager>();
        var runner = new RecordingGitRunner();
        var manager = new BrainRepoManager(_tempDir, logger, runner.Run);

        await manager.EnsureCloneAsync(
            "widgets", QueryUrl, "main", TestContext.Current.CancellationToken);

        var entry = Assert.Single(
            logger.LogEntries, e => e.Message.Contains("Creating Brain clone"));
        Assert.DoesNotContain("ghp_query_secret", entry.Message);
        Assert.Contains("https://github.com/acme/widgets.git?ref=main", entry.Message);
    }

    // ── Boundary 2: the RunGitAsync exception construction ────────────────────

    /// <summary>
    /// The failure message embeds the complete git ARGUMENT LIST, which for a clone contains the
    /// credential-bearing remote. It must be redacted where the message is constructed.
    /// </summary>
    [Fact]
    public async Task EnsureCloneAsync_WhenCloneFails_ExceptionMessageHasNoCredentialFromArguments()
    {
        var runner = new RecordingGitRunner(_ =>
            new BrainGitResult(128, string.Empty, "fatal: repository not found"));
        var manager = new BrainRepoManager(
            _tempDir, new TestLogger<BrainRepoManager>(), runner.Run);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.EnsureCloneAsync(
                "widgets", CredentialUrl, "main", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.DoesNotContain("x-access-token", ex.Message);
        Assert.Contains(RedactedUrl, ex.Message);
        // Behavior is otherwise unchanged: exit code and stderr still surface.
        Assert.Contains("exit 128", ex.Message);
        Assert.Contains("fatal: repository not found", ex.Message);
    }

    /// <summary>
    /// git echoes the remote it was handed back through STDERR. That copy is redacted too.
    /// </summary>
    [Fact]
    public async Task EnsureCloneAsync_WhenCloneFails_ExceptionMessageHasNoCredentialFromStderr()
    {
        var runner = new RecordingGitRunner(_ => new BrainGitResult(
            128,
            string.Empty,
            $"fatal: unable to access '{CredentialUrl}/': The requested URL returned error: 403"));
        var manager = new BrainRepoManager(
            _tempDir, new TestLogger<BrainRepoManager>(), runner.Run);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.EnsureCloneAsync(
                "widgets", CredentialUrl, "main", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.Contains($"unable to access '{RedactedUrl}/'", ex.Message);
        Assert.Contains("The requested URL returned error: 403", ex.Message);
    }

    /// <summary>
    /// A failing <c>fetch</c> on an EXISTING clone goes through the same construction boundary.
    /// </summary>
    [Fact]
    public async Task EnsureCloneAsync_ExistingClone_FetchFailureMessageIsRedacted()
    {
        var manager0 = new BrainRepoManager(_tempDir, new TestLogger<BrainRepoManager>());
        Directory.CreateDirectory(Path.Combine(manager0.GetClonePath("widgets"), ".git"));

        var runner = new RecordingGitRunner(_ => new BrainGitResult(
            1, string.Empty, $"fatal: could not read from '{CredentialUrl}'"));
        var manager = new BrainRepoManager(
            _tempDir, new TestLogger<BrainRepoManager>(), runner.Run);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.EnsureCloneAsync(
                "widgets", CredentialUrl, "main", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.Contains(RedactedUrl, ex.Message);
    }

    /// <summary>
    /// The redaction must not disturb the "branch not found in upstream" recovery: that branch
    /// is selected on message CONTENT, and redaction only removes credentials.
    /// </summary>
    [Fact]
    public async Task EnsureCloneAsync_BranchNotFoundInUpstream_StillRetriesWithoutBranchFlag()
    {
        var runner = new RecordingGitRunner(request =>
            request.Arguments.Contains("--branch")
                ? new BrainGitResult(
                    128,
                    string.Empty,
                    $"fatal: Remote branch main not found in upstream origin ({CredentialUrl})")
                : new BrainGitResult(0, string.Empty, string.Empty));
        var logger = new TestLogger<BrainRepoManager>();
        var manager = new BrainRepoManager(_tempDir, logger, runner.Run);

        var path = await manager.EnsureCloneAsync(
            "widgets", CredentialUrl, "main", TestContext.Current.CancellationToken);

        Assert.Equal(manager.GetClonePath("widgets"), path);
        // The retry happened: a clone WITHOUT --branch was issued.
        Assert.Contains(
            runner.Requests,
            r => r.Arguments.Contains("clone") && !r.Arguments.Contains("--branch"));
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(Token));
    }

    // ── Behavior preservation ─────────────────────────────────────────────────

    /// <summary>
    /// A successful run is unchanged: the clone path is returned and the identity configuration
    /// commands still run in order.
    /// </summary>
    [Fact]
    public async Task EnsureCloneAsync_Success_IssuesTheSameGitCommandsAsBefore()
    {
        var runner = new RecordingGitRunner();
        var manager = new BrainRepoManager(
            _tempDir, new TestLogger<BrainRepoManager>(), runner.Run);

        var path = await manager.EnsureCloneAsync(
            "widgets", CredentialUrl, "main", TestContext.Current.CancellationToken);

        Assert.Equal(manager.GetClonePath("widgets"), path);
        Assert.Collection(
            runner.Requests.Select(r => string.Join(' ', r.Arguments)),
            first => Assert.Equal($"clone --branch main {CredentialUrl} widgets", first),
            second => Assert.Equal("config user.email copilothive@local", second),
            third => Assert.Equal("config user.name CopilotHive", third));
    }

    /// <summary>
    /// A URL with no credential component is logged verbatim — redaction never rewrites an
    /// innocent URL.
    /// </summary>
    [Fact]
    public async Task EnsureCloneAsync_CredentialFreeUrl_IsLoggedUnchanged()
    {
        const string PlainUrl = "https://github.com/acme/widgets.git";
        var logger = new TestLogger<BrainRepoManager>();
        var runner = new RecordingGitRunner();
        var manager = new BrainRepoManager(_tempDir, logger, runner.Run);

        await manager.EnsureCloneAsync(
            "widgets", PlainUrl, "main", TestContext.Current.CancellationToken);

        var entry = Assert.Single(
            logger.LogEntries, e => e.Message.Contains("Creating Brain clone"));
        Assert.Contains(PlainUrl, entry.Message);
    }

    // ── Remote-tag boundaries (RunGitCaptureAsync consumers) ──────────────────
    //
    // The Brain clone persists the injected credential-bearing URL as `origin`, so a failing
    // REMOTE tag command (`ls-remote ... origin`, `push origin :refs/tags/...`) echoes that URL
    // through stderr. CreateTagAsync and DeleteTagAsync build exception messages and warning
    // logs from that raw stderr, so each construction point must redact.

    /// <summary>The stderr shape git emits when a remote operation fails against `origin`.</summary>
    private static string RemoteFailureStderr =>
        $"fatal: unable to access '{CredentialUrl}/': The requested URL returned error: 403";

    /// <summary>Creates a manager whose clone directory exists, so the tag paths are reachable.</summary>
    private BrainRepoManager CreateManagerWithClone(
        TestLogger<BrainRepoManager> logger, Func<BrainGitRequest, BrainGitResult> runner)
    {
        var manager = new BrainRepoManager(_tempDir, logger, runner);
        Directory.CreateDirectory(Path.Combine(manager.GetClonePath("widgets"), ".git"));
        return manager;
    }

    [Fact]
    public async Task CreateTagAsync_WhenRemoteTagQueryFails_ExceptionMessageIsRedacted()
    {
        var runner = new RecordingGitRunner(request =>
            request.Arguments.Contains("ls-remote")
                ? new BrainGitResult(128, string.Empty, RemoteFailureStderr)
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManagerWithClone(new TestLogger<BrainRepoManager>(), runner.Run);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.CreateTagAsync(
                "widgets", "v1.0.0", "main", "Release", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.DoesNotContain("x-access-token", ex.Message);
        Assert.StartsWith("Failed to query remote tags for 'widgets':", ex.Message);
        // The credential-FREE remote is still reported, so the message stays diagnostic.
        Assert.Contains($"unable to access '{RedactedUrl}/'", ex.Message);
        Assert.Contains("The requested URL returned error: 403", ex.Message);
    }

    [Fact]
    public async Task DeleteTagAsync_WhenRemoteTagQueryFails_ExceptionMessageIsRedacted()
    {
        var runner = new RecordingGitRunner(request =>
            request.Arguments.Contains("ls-remote")
                ? new BrainGitResult(128, string.Empty, RemoteFailureStderr)
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManagerWithClone(new TestLogger<BrainRepoManager>(), runner.Run);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.DeleteTagAsync("widgets", "v1.0.0", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.StartsWith("Failed to query remote tags for 'widgets':", ex.Message);
        Assert.Contains($"unable to access '{RedactedUrl}/'", ex.Message);
    }

    [Fact]
    public async Task DeleteTagAsync_WhenLocalTagQueryFails_ExceptionMessageIsRedacted()
    {
        var runner = new RecordingGitRunner(request => request.Arguments switch
        {
            var a when a.Contains("ls-remote") => new BrainGitResult(0, string.Empty, string.Empty),
            var a when a.Contains("tag") && a.Contains("-l") =>
                new BrainGitResult(128, string.Empty, RemoteFailureStderr),
            _ => new BrainGitResult(0, string.Empty, string.Empty),
        });
        var manager = CreateManagerWithClone(new TestLogger<BrainRepoManager>(), runner.Run);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.DeleteTagAsync("widgets", "v1.0.0", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.StartsWith("Failed to query local tags for 'widgets':", ex.Message);
        Assert.Contains(RedactedUrl, ex.Message);
    }

    /// <summary>
    /// Partial delete: the LOCAL delete succeeds and the REMOTE push fails, so the operation
    /// still returns true and the remote failure is only WARNED about — that warning is built
    /// from the credential-bearing push stderr.
    /// </summary>
    [Fact]
    public async Task DeleteTagAsync_PartialDelete_RemoteWarningIsRedacted()
    {
        var logger = new TestLogger<BrainRepoManager>();
        var runner = new RecordingGitRunner(request => request.Arguments switch
        {
            // The tag exists on BOTH sides.
            var a when a.Contains("ls-remote") =>
                new BrainGitResult(0, "abc123\trefs/tags/v1.0.0\n", string.Empty),
            var a when a.Contains("-l") => new BrainGitResult(0, "v1.0.0\n", string.Empty),
            // Local delete succeeds…
            var a when a.Contains("-d") => new BrainGitResult(0, string.Empty, string.Empty),
            // …remote push fails, echoing the credential-bearing origin.
            var a when a.Contains("push") =>
                new BrainGitResult(1, string.Empty, RemoteFailureStderr),
            _ => new BrainGitResult(0, string.Empty, string.Empty),
        });
        var manager = CreateManagerWithClone(logger, runner.Run);

        var deleted = await manager.DeleteTagAsync(
            "widgets", "v1.0.0", TestContext.Current.CancellationToken);

        // Behavior is unchanged: a partial delete still reports success.
        Assert.True(deleted);

        var warning = Assert.Single(
            logger.LogEntries, e => e.Message.Contains("Remote tag delete failed"));
        Assert.DoesNotContain(Token, warning.Message);
        Assert.Contains($"unable to access '{RedactedUrl}/'", warning.Message);
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(Token));
    }

    /// <summary>
    /// The mirror case: the REMOTE delete succeeds and the LOCAL delete fails, exercising the
    /// local-warning construction point.
    /// </summary>
    [Fact]
    public async Task DeleteTagAsync_PartialDelete_LocalWarningIsRedacted()
    {
        var logger = new TestLogger<BrainRepoManager>();
        var runner = new RecordingGitRunner(request => request.Arguments switch
        {
            var a when a.Contains("ls-remote") =>
                new BrainGitResult(0, "abc123\trefs/tags/v1.0.0\n", string.Empty),
            var a when a.Contains("-l") => new BrainGitResult(0, "v1.0.0\n", string.Empty),
            // Local delete fails with a message that happens to carry the remote…
            var a when a.Contains("-d") =>
                new BrainGitResult(1, string.Empty, RemoteFailureStderr),
            // …remote push succeeds.
            var a when a.Contains("push") => new BrainGitResult(0, string.Empty, string.Empty),
            _ => new BrainGitResult(0, string.Empty, string.Empty),
        });
        var manager = CreateManagerWithClone(logger, runner.Run);

        var deleted = await manager.DeleteTagAsync(
            "widgets", "v1.0.0", TestContext.Current.CancellationToken);

        Assert.True(deleted);

        var warning = Assert.Single(
            logger.LogEntries, e => e.Message.Contains("Local tag delete failed"));
        Assert.DoesNotContain(Token, warning.Message);
        Assert.Contains($"unable to access '{RedactedUrl}/'", warning.Message);
    }

    /// <summary>
    /// BOTH deletions fail, so the final aggregate exception embeds both stderr copies.
    /// </summary>
    [Fact]
    public async Task DeleteTagAsync_WhenBothDeletionsFail_ExceptionMessageIsRedacted()
    {
        var logger = new TestLogger<BrainRepoManager>();
        var runner = new RecordingGitRunner(request => request.Arguments switch
        {
            var a when a.Contains("ls-remote") =>
                new BrainGitResult(0, "abc123\trefs/tags/v1.0.0\n", string.Empty),
            var a when a.Contains("-l") => new BrainGitResult(0, "v1.0.0\n", string.Empty),
            var a when a.Contains("-d") =>
                new BrainGitResult(1, string.Empty, $"error: local delete against {CredentialUrl}"),
            var a when a.Contains("push") =>
                new BrainGitResult(1, string.Empty, RemoteFailureStderr),
            _ => new BrainGitResult(0, string.Empty, string.Empty),
        });
        var manager = CreateManagerWithClone(logger, runner.Run);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.DeleteTagAsync("widgets", "v1.0.0", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.DoesNotContain("x-access-token", ex.Message);
        Assert.StartsWith("Failed to delete tag 'v1.0.0' for 'widgets'.", ex.Message);
        // BOTH stderr copies are present, both credential-free.
        Assert.Contains($"Local error: error: local delete against {RedactedUrl}", ex.Message);
        Assert.Contains($"Remote error: fatal: unable to access '{RedactedUrl}/'", ex.Message);
    }

    /// <summary>
    /// The sole-side failure form of the final exception: the tag exists only on the remote and
    /// that single deletion fails, so <c>Local error</c> renders as <c>(n/a)</c>.
    /// </summary>
    [Fact]
    public async Task DeleteTagAsync_WhenSoleRemoteDeletionFails_ExceptionMessageIsRedacted()
    {
        var runner = new RecordingGitRunner(request => request.Arguments switch
        {
            var a when a.Contains("ls-remote") =>
                new BrainGitResult(0, "abc123\trefs/tags/v1.0.0\n", string.Empty),
            // Not present locally.
            var a when a.Contains("-l") => new BrainGitResult(0, string.Empty, string.Empty),
            var a when a.Contains("push") =>
                new BrainGitResult(1, string.Empty, RemoteFailureStderr),
            _ => new BrainGitResult(0, string.Empty, string.Empty),
        });
        var manager = CreateManagerWithClone(new TestLogger<BrainRepoManager>(), runner.Run);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.DeleteTagAsync("widgets", "v1.0.0", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.Contains("Local error: (n/a)", ex.Message);
        Assert.Contains($"Remote error: fatal: unable to access '{RedactedUrl}/'", ex.Message);
    }

    /// <summary>
    /// Raw capture data is NOT mutated: the tag-existence decisions still read git's verbatim
    /// stdout, so a successful create/delete behaves exactly as before.
    /// </summary>
    [Fact]
    public async Task CreateTagAsync_WhenTagAlreadyExistsOnOrigin_StillSkipsUsingRawStdout()
    {
        var logger = new TestLogger<BrainRepoManager>();
        var runner = new RecordingGitRunner(request =>
            request.Arguments.Contains("ls-remote")
                ? new BrainGitResult(0, "abc123\trefs/tags/v1.0.0\n", string.Empty)
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManagerWithClone(logger, runner.Run);

        var created = await manager.CreateTagAsync(
            "widgets", "v1.0.0", "main", "Release", TestContext.Current.CancellationToken);

        Assert.False(created);
        Assert.Contains(logger.LogEntries, e => e.Message.Contains("already exists on origin"));
        // No tag/push commands were issued after the skip.
        Assert.DoesNotContain(runner.Requests, r => r.Arguments.Contains("push"));
    }

    /// <summary>
    /// A tag that exists nowhere is reported as "not found" from RAW stdout, unchanged.
    /// </summary>
    [Fact]
    public async Task DeleteTagAsync_WhenTagExistsNowhere_ReturnsFalseFromRawStdout()
    {
        var runner = new RecordingGitRunner();
        var manager = CreateManagerWithClone(new TestLogger<BrainRepoManager>(), runner.Run);

        var deleted = await manager.DeleteTagAsync(
            "widgets", "v1.0.0", TestContext.Current.CancellationToken);

        Assert.False(deleted);
        Assert.DoesNotContain(runner.Requests, r => r.Arguments.Contains("push"));
    }

    /// <summary>
    /// A credential-free stderr is reported verbatim through the tag boundaries — redaction
    /// never rewrites an innocent message.
    /// </summary>
    [Fact]
    public async Task DeleteTagAsync_CredentialFreeStderr_IsReportedUnchanged()
    {
        var runner = new RecordingGitRunner(request =>
            request.Arguments.Contains("ls-remote")
                ? new BrainGitResult(128, string.Empty, "fatal: not a git repository")
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManagerWithClone(new TestLogger<BrainRepoManager>(), runner.Run);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.DeleteTagAsync("widgets", "v1.0.0", TestContext.Current.CancellationToken));

        Assert.Equal(
            "Failed to query remote tags for 'widgets': fatal: not a git repository", ex.Message);
    }
}

/// <summary>
/// The target-repository OAuth credential fix: every network-bearing
/// <see cref="BrainRepoManager"/> operation resolves ONE credential and refreshes the clone's
/// <c>origin</c> with it BEFORE its first network command, under the per-repository lock.
/// <para>
/// Every test drives the manager through the <see cref="BrainGitRequest"/>/
/// <see cref="BrainGitResult"/> runner seam — no git process, no network, no real credential.
/// The class serializes on <c>EnvVarMutation</c> because the credential chain reads
/// <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c>: both are cleared in the constructor and restored on
/// dispose so a host that exports them cannot make these assertions non-deterministic.
/// </para>
/// </summary>
[Collection("EnvVarMutation")]
public sealed class BrainRepoManagerCredentialTests : IDisposable
{
    internal const string RepoName = "widgets";
    internal const string ConfiguredUrl = "https://github.com/acme/widgets.git";
    internal const string StoredToken = "gho_stored_admin_token";
    internal const string RotatedToken = "gho_rotated_admin_token";

    /// <summary>The exact origin the manager must write for <see cref="StoredToken"/>.</summary>
    internal const string CredentialOrigin =
        $"https://x-access-token:{StoredToken}@github.com/acme/widgets.git";

    private readonly string _tempDir;
    private readonly string? _originalGhToken;
    private readonly string? _originalGitHubToken;

    public BrainRepoManagerCredentialTests()
    {
        _originalGhToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        _originalGitHubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GH_TOKEN", null);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);

        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", _originalGhToken);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", _originalGitHubToken);

        if (Directory.Exists(_tempDir))
            TestHelpers.ForceDeleteDirectory(_tempDir);
    }

    // ── Fake runner ───────────────────────────────────────────────────────────

    /// <summary>
    /// Scripted runner that answers the manager's LOCAL origin-inspection queries
    /// (<c>remote get-url --all origin</c>, <c>config --get-all remote.origin.pushurl</c>) from
    /// mutable state, records every request in order, and delegates everything else.
    /// </summary>
    internal sealed class CredentialRunner(Func<BrainGitRequest, BrainGitResult>? respond = null)
    {
        /// <summary>Lines returned by <c>remote get-url --all origin</c>.</summary>
        public List<string> Origins { get; set; } = [ConfiguredUrl];

        /// <summary>Exit code of the origin query — non-zero models an unreadable origin.</summary>
        public int OriginExit { get; set; }

        /// <summary>Value returned by <c>config --get-all remote.origin.pushurl</c>.</summary>
        public string PushUrl { get; set; } = string.Empty;

        /// <summary>
        /// Exit code of the pushurl query. <c>null</c> derives git's real behaviour from
        /// <see cref="PushUrl"/> (exit 1 = key not set, exit 0 = key present); an explicit value
        /// models an exit code independently of the value, which is exactly the
        /// exit-0-with-empty-value and unexpected-failure cases the fail-closed policy must catch.
        /// </summary>
        public int? PushUrlExit { get; set; }

        /// <summary>Every request the manager issued, in order.</summary>
        public List<BrainGitRequest> Requests { get; } = [];

        /// <summary>Invoked (inside the runner) on every request — used for deterministic gating.</summary>
        public Action<BrainGitRequest>? OnRequest { get; set; }

        public BrainGitResult Run(BrainGitRequest request)
        {
            lock (Requests)
                Requests.Add(request);

            OnRequest?.Invoke(request);

            var a = request.Arguments;
            if (a.Count >= 2 && a[0] == "remote" && a[1] == "get-url")
                return new BrainGitResult(OriginExit, string.Join('\n', Origins), string.Empty);
            if (a.Count >= 2 && a[0] == "config" && a[1] == "--get-all")
                return new BrainGitResult(
                    PushUrlExit ?? (string.IsNullOrEmpty(PushUrl) ? 1 : 0), PushUrl, string.Empty);

            return respond?.Invoke(request) ?? new BrainGitResult(0, string.Empty, string.Empty);
        }

        /// <summary>The recorded requests rendered as whitespace-joined argument strings.</summary>
        public List<string> Joined()
        {
            lock (Requests)
                return Requests.Select(r => string.Join(' ', r.Arguments)).ToList();
        }
    }

    /// <summary>Git verbs that talk to the network. The refresh must precede the first of them.</summary>
    private static readonly string[] NetworkVerbs = ["fetch", "push", "ls-remote"];

    /// <summary>Index of the first recorded NETWORK command, or -1 when none was issued.</summary>
    private static int FirstNetworkIndex(CredentialRunner runner) =>
        runner.Joined().FindIndex(j => NetworkVerbs.Any(v =>
            j.StartsWith(v + " ", StringComparison.Ordinal) || j == v));

    /// <summary>Index of the credential-attaching <c>remote set-url origin</c>, or -1.</summary>
    private static int SetUrlIndex(CredentialRunner runner) =>
        runner.Joined().FindIndex(j => j.StartsWith("remote set-url origin ", StringComparison.Ordinal));

    /// <summary>Builds a manager wired to a live token lookup and a live configured-URL lookup.</summary>
    private BrainRepoManager CreateManager(
        CredentialRunner runner,
        Func<CancellationToken, Task<string?>>? tokenLookup,
        Func<string, string?>? urlLookup,
        TestLogger<BrainRepoManager>? logger = null,
        bool createClone = true)
    {
        var manager = new BrainRepoManager(
            _tempDir,
            logger ?? new TestLogger<BrainRepoManager>(),
            runner.Run,
            tokenLookup,
            urlLookup);

        if (createClone)
            Directory.CreateDirectory(Path.Combine(manager.GetClonePath(RepoName), ".git"));

        return manager;
    }

    /// <summary>The standard live configured-URL lookup: a named, case-insensitive match.</summary>
    private static Func<string, string?> UrlLookup(string url) =>
        name => string.Equals(name, RepoName, StringComparison.OrdinalIgnoreCase) ? url : null;

    /// <summary>A live token lookup returning the same value on every call.</summary>
    private static Func<CancellationToken, Task<string?>> TokenLookup(string? token) =>
        _ => Task.FromResult(token);

    // ── OAuth-only fresh clone ────────────────────────────────────────────────

    [Fact]
    public async Task EnsureCloneAsync_OAuthOnlyFreshClone_ClonesWithTheStoredCredential()
    {
        var runner = new CredentialRunner();
        // No clone on disk: this is the FRESH-clone path.
        var manager = CreateManager(
            runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl), createClone: false);

        await manager.EnsureCloneAsync(
            RepoName, ConfiguredUrl, "main", TestContext.Current.CancellationToken);

        var clone = Assert.Single(runner.Requests, r => r.Arguments.Contains("clone"));
        Assert.Contains(CredentialOrigin, clone.Arguments);
    }

    [Fact]
    public async Task EnsureCloneAsync_FreshCloneWithNoCredentialAnywhere_UsesTheSuppliedUrlUnchanged()
    {
        var runner = new CredentialRunner();
        var manager = CreateManager(
            runner, TokenLookup(null), UrlLookup(ConfiguredUrl), createClone: false);

        await manager.EnsureCloneAsync(
            RepoName, ConfiguredUrl, "main", TestContext.Current.CancellationToken);

        var clone = Assert.Single(runner.Requests, r => r.Arguments.Contains("clone"));
        Assert.Contains(ConfiguredUrl, clone.Arguments);
        Assert.DoesNotContain("x-access-token", string.Join(' ', clone.Arguments));
    }

    // ── Existing stale origin ─────────────────────────────────────────────────

    [Fact]
    public async Task ExistingCloneWithStaleOrigin_IsRefreshedToTheCurrentCredential()
    {
        var runner = new CredentialRunner
        {
            // Same repository, STALE embedded credential.
            Origins = ["https://x-access-token:gho_expired_token@github.com/acme/widgets.git"],
        };
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        await manager.EnsureCloneAsync(
            RepoName, ConfiguredUrl, "main", TestContext.Current.CancellationToken);

        Assert.Contains($"remote set-url origin {CredentialOrigin}", runner.Joined());
    }

    [Fact]
    public async Task ExistingCloneWithCredentialFreeOrigin_IsRefreshedToTheCurrentCredential()
    {
        var runner = new CredentialRunner { Origins = [ConfiguredUrl] };
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        await manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken);

        Assert.Contains($"remote set-url origin {CredentialOrigin}", runner.Joined());
    }

    /// <summary>A trailing <c>.git</c>/slash difference is the SAME repository, not a mismatch.</summary>
    [Theory]
    [InlineData("https://github.com/acme/widgets")]
    [InlineData("https://github.com/acme/widgets.git")]
    [InlineData("https://github.com/acme/widgets.git/")]
    [InlineData("https://GitHub.com/acme/widgets.git")]
    public async Task SameRepositoryOriginVariants_AreAccepted(string origin)
    {
        var runner = new CredentialRunner { Origins = [origin] };
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        await manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken);

        Assert.Contains($"remote set-url origin {CredentialOrigin}", runner.Joined());
    }

    // ── Token rotation ────────────────────────────────────────────────────────

    [Fact]
    public async Task ConsecutiveCalls_AfterTokenRotation_UseTheNewToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new CredentialRunner();
        var current = StoredToken;
        // A LIVE lookup: re-read on every call, never captured once.
        var manager = CreateManager(runner, _ => Task.FromResult<string?>(current), UrlLookup(ConfiguredUrl));

        await manager.ListRemoteBranchesAsync(RepoName, ct);
        current = RotatedToken;
        await manager.ListRemoteBranchesAsync(RepoName, ct);

        var setUrls = runner.Joined()
            .Where(j => j.StartsWith("remote set-url origin ", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, setUrls.Count);
        Assert.Contains(StoredToken, setUrls[0]);
        Assert.Contains(RotatedToken, setUrls[1]);
        Assert.DoesNotContain(StoredToken, setUrls[1]);
    }

    /// <summary>
    /// Restart/reload: a BRAND NEW manager instance over the SAME existing clone still refreshes
    /// from the CURRENT configuration — the lookups are live, never a startup snapshot.
    /// </summary>
    [Fact]
    public async Task NewManagerInstance_OverExistingClone_StillRefreshesFromCurrentConfiguration()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = new CredentialRunner();
        var firstManager = CreateManager(first, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));
        await firstManager.ListRemoteBranchesAsync(RepoName, ct);

        // Simulated restart: a fresh manager, fresh runner, same clone directory on disk.
        var second = new CredentialRunner();
        var secondManager = CreateManager(second, TokenLookup(RotatedToken), UrlLookup(ConfiguredUrl));

        await secondManager.ListRemoteBranchesAsync(RepoName, ct);

        Assert.Contains(
            second.Joined(),
            j => j.StartsWith("remote set-url origin ", StringComparison.Ordinal)
                 && j.Contains(RotatedToken, StringComparison.Ordinal));
    }

    /// <summary>
    /// A repository added to the LIVE configuration after the manager was constructed is looked up
    /// successfully — proving the URL lookup is evaluated per call, not snapshotted.
    /// </summary>
    [Fact]
    public async Task ConfiguredUrlLookup_ReflectsConfigurationReload_WithoutReconstruction()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new CredentialRunner();
        // The live configuration list the DI delegate reads at call time.
        var liveRepositories = new List<(string Name, string Url)>();
        var manager = CreateManager(
            runner,
            TokenLookup(StoredToken),
            name => liveRepositories
                .Where(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Url)
                .FirstOrDefault());

        // Before the reload the repository is unknown: no refresh at all.
        await manager.ListRemoteBranchesAsync(RepoName, ct);
        Assert.DoesNotContain(
            runner.Joined(), j => j.StartsWith("remote set-url origin ", StringComparison.Ordinal));

        // Configuration reload adds the repository — no manager reconstruction.
        liveRepositories.Add((RepoName, ConfiguredUrl));

        await manager.ListRemoteBranchesAsync(RepoName, ct);
        Assert.Contains($"remote set-url origin {CredentialOrigin}", runner.Joined());
    }

    // ── Blank-aware chain and lookup failure ──────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankStoredValue_FallsThroughToGhToken(string? stored)
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "gh_env_token");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "github_env_token");

        var runner = new CredentialRunner();
        var manager = CreateManager(runner, TokenLookup(stored), UrlLookup(ConfiguredUrl));

        await manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken);

        var setUrl = Assert.Single(
            runner.Joined(), j => j.StartsWith("remote set-url origin ", StringComparison.Ordinal));
        Assert.Contains("gh_env_token", setUrl);
        Assert.DoesNotContain("github_env_token", setUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankStoredAndBlankGhToken_FallsThroughToGitHubToken(string? blank)
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", blank);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "github_env_token");

        var runner = new CredentialRunner();
        var manager = CreateManager(runner, TokenLookup(blank), UrlLookup(ConfiguredUrl));

        await manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken);

        var setUrl = Assert.Single(
            runner.Joined(), j => j.StartsWith("remote set-url origin ", StringComparison.Ordinal));
        Assert.Contains("github_env_token", setUrl);
    }

    [Fact]
    public async Task StoredTokenPresent_WinsOverBothEnvironmentCandidates()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "gh_env_token");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "github_env_token");

        var runner = new CredentialRunner();
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        await manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken);

        var setUrl = Assert.Single(
            runner.Joined(), j => j.StartsWith("remote set-url origin ", StringComparison.Ordinal));
        Assert.Contains(StoredToken, setUrl);
        Assert.DoesNotContain("gh_env_token", setUrl);
        Assert.DoesNotContain("github_env_token", setUrl);
    }

    [Fact]
    public async Task StoredLookupThrows_FallsBackToTheEnvironmentChain()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "gh_env_token");

        var runner = new CredentialRunner();
        var manager = CreateManager(
            runner,
            _ => throw new InvalidOperationException($"database is locked; token was {StoredToken}"),
            UrlLookup(ConfiguredUrl));

        await manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken);

        var setUrl = Assert.Single(
            runner.Joined(), j => j.StartsWith("remote set-url origin ", StringComparison.Ordinal));
        Assert.Contains("gh_env_token", setUrl);
    }

    /// <summary>
    /// The lookup-failure diagnostic is FIXED text: it never formats the thrown exception, whose
    /// message could itself embed the credential the lookup failed to deliver.
    /// </summary>
    [Fact]
    public async Task StoredLookupThrows_LogsAFixedCredentialFreeDiagnostic()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "gh_env_token");

        var logger = new TestLogger<BrainRepoManager>();
        var runner = new CredentialRunner();
        var manager = CreateManager(
            runner,
            _ => throw new InvalidOperationException($"database is locked; token was {StoredToken}"),
            UrlLookup(ConfiguredUrl),
            logger);

        await manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken);

        var entry = Assert.Single(
            logger.LogEntries, e => e.Message.Contains("Stored OAuth credential lookup failed"));
        Assert.DoesNotContain(StoredToken, entry.Message);
        Assert.DoesNotContain("database is locked", entry.Message);
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(StoredToken));
    }

    [Fact]
    public async Task StoredLookupThrows_AndNoEnvironmentCredential_LeavesOriginUntouched()
    {
        var runner = new CredentialRunner();
        var manager = CreateManager(
            runner, _ => throw new InvalidOperationException("boom"), UrlLookup(ConfiguredUrl));

        await manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            runner.Joined(), j => j.StartsWith("remote set-url origin ", StringComparison.Ordinal));
    }

    // ── Per-method refresh matrix ─────────────────────────────────────────────

    /// <summary>Drives one network-bearing method with the shared scripted runner.</summary>
    private static async Task InvokeAsync(BrainRepoManager manager, string method, CancellationToken ct)
    {
        switch (method)
        {
            case nameof(BrainRepoManager.EnsureCloneAsync):
                await manager.EnsureCloneAsync(RepoName, ConfiguredUrl, "main", ct);
                return;
            case nameof(BrainRepoManager.MergeFeatureBranchAsync):
                await manager.MergeFeatureBranchAsync(RepoName, "feature", "main", "msg", ct);
                return;
            case nameof(BrainRepoManager.MergeBranchAsync):
                await Assert.ThrowsAnyAsync<InvalidOperationException>(
                    () => manager.MergeBranchAsync(RepoName, "feature", "main", ct));
                return;
            case nameof(BrainRepoManager.CreateTagAsync):
                await Assert.ThrowsAnyAsync<InvalidOperationException>(
                    () => manager.CreateTagAsync(RepoName, "v1.0.0", "main", "Release", ct));
                return;
            case nameof(BrainRepoManager.DeleteTagAsync):
                await manager.DeleteTagAsync(RepoName, "v1.0.0", ct);
                return;
            case nameof(BrainRepoManager.DeleteRemoteBranchAsync):
                await manager.DeleteRemoteBranchAsync(RepoName, "feature", ct);
                return;
            case nameof(BrainRepoManager.ListRemoteBranchesAsync):
                await manager.ListRemoteBranchesAsync(RepoName, ct);
                return;
            case nameof(BrainRepoManager.FetchOriginAsync):
                await manager.FetchOriginAsync(RepoName, null, ct);
                return;
            default:
                throw new InvalidOperationException($"Unhandled method '{method}'.");
        }
    }

    /// <summary>Every network-bearing entry point covered by the refresh matrix.</summary>
    public static TheoryData<string> NetworkBearingMethods() =>
    [
        nameof(BrainRepoManager.EnsureCloneAsync),
        nameof(BrainRepoManager.MergeFeatureBranchAsync),
        nameof(BrainRepoManager.MergeBranchAsync),
        nameof(BrainRepoManager.CreateTagAsync),
        nameof(BrainRepoManager.DeleteTagAsync),
        nameof(BrainRepoManager.DeleteRemoteBranchAsync),
        nameof(BrainRepoManager.ListRemoteBranchesAsync),
        nameof(BrainRepoManager.FetchOriginAsync),
    ];

    [Theory]
    [MemberData(nameof(NetworkBearingMethods))]
    public async Task EveryNetworkBearingMethod_RefreshesCredentialsBeforeItsFirstNetworkCommand(string method)
    {
        var runner = new CredentialRunner();
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        await InvokeAsync(manager, method, TestContext.Current.CancellationToken);

        var setUrl = SetUrlIndex(runner);
        var firstNetwork = FirstNetworkIndex(runner);

        Assert.True(setUrl >= 0, $"{method} never attached the credential. Requests: {string.Join(" | ", runner.Joined())}");
        Assert.True(firstNetwork >= 0, $"{method} issued no network command. Requests: {string.Join(" | ", runner.Joined())}");
        Assert.True(setUrl < firstNetwork,
            $"{method} ran '{runner.Joined()[firstNetwork]}' at index {firstNetwork} BEFORE the credential refresh at index {setUrl}.");
    }

    [Theory]
    [MemberData(nameof(NetworkBearingMethods))]
    public async Task EveryNetworkBearingMethod_ResolvesTheCredentialExactlyOnce(string method)
    {
        var lookups = 0;
        var runner = new CredentialRunner();
        var manager = CreateManager(
            runner,
            _ => { Interlocked.Increment(ref lookups); return Task.FromResult<string?>(StoredToken); },
            UrlLookup(ConfiguredUrl));

        await InvokeAsync(manager, method, TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref lookups));
    }

    /// <summary>
    /// The ROLLBACK path of a failed squash merge still runs against the refreshed origin: the
    /// refresh precedes the fetch that precedes the merge that triggers the rollback, and the
    /// rollback's own commands all follow it too.
    /// </summary>
    /// <remarks>
    /// <c>MergeFeatureBranchAsync</c> probes <c>origin/{branch}</c> with a DIRECT git process
    /// (deliberately outside the runner seam), so the clone here is a REAL repository carrying
    /// real <c>refs/remotes/origin/*</c> refs. Everything else still goes through the seam.
    /// </remarks>
    [Fact]
    public async Task MergeFeatureBranchAsync_RollbackPath_RunsAfterTheCredentialRefresh()
    {
        var runner = new CredentialRunner(request =>
            request.Arguments.Contains("--squash")
                ? new BrainGitResult(1, string.Empty, "CONFLICT (content): merge conflict")
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManager(
            runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl), createClone: false);

        CreateRealCloneWithOriginRefs(manager.GetClonePath(RepoName), "main", "feature");

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => manager.MergeFeatureBranchAsync(
            RepoName, "feature", "main", "msg", TestContext.Current.CancellationToken));

        var joined = runner.Joined();
        var setUrl = SetUrlIndex(runner);
        Assert.True(setUrl >= 0, "The merge never attached the credential.");

        // The rollback actually ran (proving this is the rollback path, not a clean merge)…
        Assert.Contains("merge --abort", joined);
        Assert.Contains("clean -fd", joined);

        // …and EVERY network command, rollback ones included, came after the refresh.
        for (var i = 0; i < joined.Count; i++)
        {
            if (NetworkVerbs.Any(v => joined[i].StartsWith(v + " ", StringComparison.Ordinal)))
                Assert.True(i > setUrl, $"Network command '{joined[i]}' preceded the refresh.");
        }
    }

    /// <summary>
    /// Creates a REAL git repository at <paramref name="clonePath"/> with one commit and a
    /// <c>refs/remotes/origin/{branch}</c> ref for each requested branch, so the manager's
    /// direct-process <c>rev-parse --verify origin/{branch}</c> probes resolve.
    /// </summary>
    private static void CreateRealCloneWithOriginRefs(string clonePath, params string[] branches)
    {
        Directory.CreateDirectory(clonePath);
        RunRealGit(clonePath, "init", "-b", "main");
        RunRealGit(clonePath, "config", "user.email", "test@test.com");
        RunRealGit(clonePath, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(clonePath, "seed.txt"), "seed");
        RunRealGit(clonePath, "add", "seed.txt");
        RunRealGit(clonePath, "commit", "-m", "Seed commit");

        foreach (var branch in branches)
            RunRealGit(clonePath, "update-ref", $"refs/remotes/origin/{branch}", "HEAD");
    }

    private static void RunRealGit(string workDir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("commit.gpgsign=false");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
    }

    // ── Origin-identity and eligibility rejection ─────────────────────────────

    /// <summary>
    /// A FOREIGN origin is rejected with a fixed safe error, BEFORE any credential attachment and
    /// before any network command — a working tree is never redirected at another repository.
    /// </summary>
    [Theory]
    [InlineData("https://github.com/evil/widgets.git")]
    [InlineData("https://github.com/acme/other.git")]
    [InlineData("https://gitlab.com/acme/widgets.git")]
    [InlineData("https://github.com:8443/acme/widgets.git")]
    [InlineData("http://github.com/acme/widgets.git")]
    [InlineData("ssh://git@github.com/acme/widgets.git")]
    [InlineData("git@github.com:acme/widgets.git")]
    [InlineData("/srv/local/widgets.git")]
    public async Task NonMatchingOrigin_IsRejectedBeforeAnyCredentialOrNetworkActivity(string origin)
    {
        var runner = new CredentialRunner { Origins = [origin] };
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken));

        Assert.Equal(
            $"Refusing to refresh credentials for '{RepoName}': its 'origin' remote points at a different repository than the configured URL.",
            ex.Message);
        Assert.DoesNotContain(StoredToken, ex.Message);

        var joined = runner.Joined();
        Assert.DoesNotContain(joined, j => j.StartsWith("remote set-url", StringComparison.Ordinal));
        Assert.DoesNotContain(joined, j => NetworkVerbs.Any(v => j.StartsWith(v + " ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A NON-ELIGIBLE configured URL (SSH, local, plain HTTP, non-GitHub, non-443) is never
    /// credentialed: the refresh is a complete no-op and the operation proceeds unchanged.
    /// </summary>
    [Theory]
    [InlineData("ssh://git@github.com/acme/widgets.git")]
    [InlineData("git@github.com:acme/widgets.git")]
    [InlineData("http://github.com/acme/widgets.git")]
    [InlineData("https://gitlab.com/acme/widgets.git")]
    [InlineData("https://github.com:8443/acme/widgets.git")]
    [InlineData("/srv/local/widgets.git")]
    [InlineData("")]
    public async Task NonEligibleConfiguredUrl_NeverReceivesTheCredential(string configuredUrl)
    {
        var lookups = 0;
        var runner = new CredentialRunner { Origins = [configuredUrl] };
        var manager = CreateManager(
            runner,
            _ => { Interlocked.Increment(ref lookups); return Task.FromResult<string?>(StoredToken); },
            UrlLookup(configuredUrl));

        // The operation still runs — this is the pre-existing unconfigured/local behaviour.
        await manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(runner.Joined(), j => j.StartsWith("remote set-url", StringComparison.Ordinal));
        Assert.DoesNotContain(StoredToken, string.Join(" | ", runner.Joined()));
        // Local-only work needs no OAuth lookup at all.
        Assert.Equal(0, Volatile.Read(ref lookups));
    }

    [Fact]
    public async Task NoConfiguredUrlLookupAtAll_PreservesPreExistingBehaviour()
    {
        var runner = new CredentialRunner();
        var manager = CreateManager(runner, TokenLookup(StoredToken), urlLookup: null);

        await manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(runner.Joined(), j => j.StartsWith("remote set-url", StringComparison.Ordinal));
        Assert.Contains("fetch --prune origin", runner.Joined());
    }

    // ── Conflicting fetch destinations / pushurl ──────────────────────────────

    [Fact]
    public async Task MultipleFetchUrls_AreRejectedWithAFixedSafeError()
    {
        var runner = new CredentialRunner
        {
            Origins = [ConfiguredUrl, "https://github.com/acme/mirror.git"],
        };
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken));

        Assert.Equal(
            $"Refusing to refresh credentials for '{RepoName}': its 'origin' remote has multiple conflicting fetch URLs.",
            ex.Message);
        Assert.DoesNotContain(StoredToken, ex.Message);
        Assert.DoesNotContain(runner.Joined(), j => j.StartsWith("remote set-url", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Joined(), j => NetworkVerbs.Any(v => j.StartsWith(v + " ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ExplicitPushUrl_IsRejectedWithAFixedSafeError()
    {
        var runner = new CredentialRunner { PushUrl = "https://github.com/acme/elsewhere.git" };
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken));

        Assert.Equal(
            $"Refusing to refresh credentials for '{RepoName}': its 'origin' remote has an explicit push URL.",
            ex.Message);
        Assert.DoesNotContain(StoredToken, ex.Message);
        Assert.DoesNotContain(runner.Joined(), j => j.StartsWith("remote set-url", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Joined(), j => NetworkVerbs.Any(v => j.StartsWith(v + " ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task UnreadableOrigin_IsRejectedWithAFixedSafeError()
    {
        var runner = new CredentialRunner { OriginExit = 128, Origins = [] };
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken));

        Assert.Equal(
            $"Refusing to refresh credentials for '{RepoName}': its 'origin' remote could not be read.",
            ex.Message);
        Assert.DoesNotContain(StoredToken, ex.Message);
    }

    // ── No-token policy ───────────────────────────────────────────────────────

    [Fact]
    public async Task NoTokenResolved_NeverStripsOrRewritesThePersistedOrigin()
    {
        var runner = new CredentialRunner
        {
            Origins = ["https://x-access-token:gho_persisted_token@github.com/acme/widgets.git"],
        };
        var manager = CreateManager(runner, TokenLookup(null), UrlLookup(ConfiguredUrl));

        await manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken);

        // No set-url at all — the persisted credential survives untouched.
        Assert.DoesNotContain(runner.Joined(), j => j.StartsWith("remote set-url", StringComparison.Ordinal));
        // …and the operation itself proceeded exactly as before.
        Assert.Contains("fetch --prune origin", runner.Joined());
    }

    [Fact]
    public async Task NoTokenResolved_OperationFailureIsUnchanged()
    {
        var runner = new CredentialRunner(request =>
            request.Arguments.Contains("fetch")
                ? new BrainGitResult(128, string.Empty, "fatal: Authentication failed")
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManager(runner, TokenLookup(null), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken));

        Assert.Contains("fatal: Authentication failed", ex.Message);
        Assert.DoesNotContain(runner.Joined(), j => j.StartsWith("remote set-url", StringComparison.Ordinal));
    }

    // ── Cancellation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelledCaller_PropagatesBeforeAnyMutationOrNetworkCommand()
    {
        var runner = new CredentialRunner();
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.ListRemoteBranchesAsync(RepoName, cts.Token));

        Assert.Empty(runner.Requests);
    }

    /// <summary>
    /// A cancellation raised INSIDE the token lookup propagates: it is never reinterpreted as a
    /// lookup failure and downgraded to the environment fallback.
    /// </summary>
    [Fact]
    public async Task CancellationFromInsideTheLookup_IsNotConvertedIntoAFallback()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "gh_env_token");

        var runner = new CredentialRunner();
        using var cts = new CancellationTokenSource();
        var manager = CreateManager(
            runner,
            async ct =>
            {
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return StoredToken;
            },
            UrlLookup(ConfiguredUrl));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.ListRemoteBranchesAsync(RepoName, cts.Token));

        // No fallback credential was attached and no network command ran.
        Assert.DoesNotContain(runner.Joined(), j => j.StartsWith("remote set-url", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Joined(), j => NetworkVerbs.Any(v => j.StartsWith(v + " ", StringComparison.Ordinal)));
        Assert.DoesNotContain("gh_env_token", string.Join(" | ", runner.Joined()));
    }

    /// <summary>
    /// Cancellation while WAITING for the per-repository semaphore propagates too: the blocked
    /// caller never proceeds to mutate the origin.
    /// </summary>
    [Fact]
    public async Task CancellationDuringSemaphoreAcquisition_PropagatesWithoutMutation()
    {
        using var holderArrived = new ManualResetEventSlim(false);
        using var releaseHolder = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();

        var runner = new CredentialRunner
        {
            OnRequest = _ =>
            {
                holderArrived.Set();
                releaseHolder.Wait(TimeSpan.FromSeconds(30));
            },
        };
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        // Occupy the per-repository lock.
        var holder = Task.Run(
            () => manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        Assert.True(holderArrived.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        // This caller can only be waiting on the semaphore. Cancelling must surface immediately.
        var blocked = manager.ListRemoteBranchesAsync(RepoName, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);

        releaseHolder.Set();
        await holder;
    }

    // ── Redaction ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task FailureStderrEchoingTheCredentialOrigin_IsRedactedInTheException()
    {
        var runner = new CredentialRunner(request =>
            request.Arguments.Contains("fetch")
                ? new BrainGitResult(128, string.Empty,
                    $"fatal: unable to access '{CredentialOrigin}/': error 403")
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(StoredToken, ex.Message);
        Assert.DoesNotContain("x-access-token", ex.Message);
        Assert.Contains("error 403", ex.Message);
    }

    /// <summary>
    /// A BARE token in stderr — no URL around it, so no URL scanner would see it — is caught by
    /// the literal redaction pass over the operation's selected credential.
    /// </summary>
    [Fact]
    public async Task BareCredentialInFailureOutput_IsRedactedByTheLiteralPass()
    {
        var runner = new CredentialRunner(request =>
            request.Arguments.Contains("fetch")
                ? new BrainGitResult(128, string.Empty,
                    $"remote: Invalid credential supplied: {StoredToken} (rejected)")
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(StoredToken, ex.Message);
        Assert.Contains("[redacted]", ex.Message);
        Assert.Contains("Invalid credential supplied", ex.Message);
    }

    /// <summary>
    /// The failing command's own ARGUMENT LIST carries the credential when the failure is the
    /// <c>remote set-url</c> itself — that copy is redacted at the same construction boundary.
    /// </summary>
    [Fact]
    public async Task FailingSetUrlCommand_DoesNotEchoTheCredentialFromItsArguments()
    {
        var runner = new CredentialRunner(request =>
            request.Arguments.Contains("set-url")
                ? new BrainGitResult(1, string.Empty, "error: could not lock config file")
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(StoredToken, ex.Message);
        Assert.Contains("could not lock config file", ex.Message);
    }

    [Fact]
    public async Task NoLogEntryEverCarriesTheSelectedCredential()
    {
        var logger = new TestLogger<BrainRepoManager>();
        var runner = new CredentialRunner(request =>
            request.Arguments.Contains("push")
                ? new BrainGitResult(1, string.Empty, $"error against {CredentialOrigin}")
                : new BrainGitResult(0, "sha\trefs/tags/v1.0.0\n", string.Empty));
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl), logger);

        await manager.DeleteTagAsync(RepoName, "v1.0.0", TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains(StoredToken));
        Assert.DoesNotContain(logger.LogEntries, e => e.Message.Contains("x-access-token"));
    }

    // ── FIX 1: cancellation observed AFTER a normally-returning lookup ────────

    /// <summary>
    /// A token lookup that cancels the caller's token and then RETURNS NORMALLY (rather than
    /// throwing an <see cref="OperationCanceledException"/>) must not let the operation proceed.
    /// Without the post-await cancellation check the returned credential reaches AttachCredential
    /// and a credential-bearing git command is issued after the caller already cancelled.
    /// </summary>
    [Fact]
    public async Task LookupThatCancelsThenReturnsNormally_StopsBeforeAnyCredentialOrNetworkCommand()
    {
        var runner = new CredentialRunner();
        using var cts = new CancellationTokenSource();
        var manager = CreateManager(
            runner,
            async _ =>
            {
                // Cancel the caller, then return a perfectly good credential WITHOUT throwing.
                await cts.CancelAsync();
                return StoredToken;
            },
            UrlLookup(ConfiguredUrl));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.ListRemoteBranchesAsync(RepoName, cts.Token));

        var joined = runner.Joined();
        Assert.DoesNotContain(joined, j => j.StartsWith("remote set-url", StringComparison.Ordinal));
        Assert.DoesNotContain(joined, j => NetworkVerbs.Any(v => j.StartsWith(v + " ", StringComparison.Ordinal)));
        Assert.DoesNotContain(StoredToken, string.Join(" | ", joined));
    }

    /// <summary>
    /// The same defect on the FRESH-CLONE path: a normally-returning cancelling lookup must not
    /// reach <c>git clone</c> with a credential-bearing URL.
    /// </summary>
    [Fact]
    public async Task LookupThatCancelsThenReturnsNormally_NeverStartsACredentialBearingClone()
    {
        var runner = new CredentialRunner();
        using var cts = new CancellationTokenSource();
        var manager = CreateManager(
            runner,
            async _ =>
            {
                await cts.CancelAsync();
                return StoredToken;
            },
            UrlLookup(ConfiguredUrl),
            createClone: false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.EnsureCloneAsync(RepoName, ConfiguredUrl, "main", cts.Token));

        Assert.DoesNotContain(runner.Requests, r => r.Arguments.Contains("clone"));
        Assert.DoesNotContain(StoredToken, string.Join(" | ", runner.Joined()));
    }

    /// <summary>
    /// A lookup that cancels the caller and THEN fails must not be downgraded to the environment
    /// fallback: cancellation wins over the fallback path.
    /// </summary>
    [Fact]
    public async Task LookupThatCancelsThenThrowsNonCancellation_DoesNotFallBackToEnvironment()
    {
        Environment.SetEnvironmentVariable("GH_TOKEN", "gh_env_token");

        var runner = new CredentialRunner();
        using var cts = new CancellationTokenSource();
        var manager = CreateManager(
            runner,
            async _ =>
            {
                await cts.CancelAsync();
                throw new InvalidOperationException("bridge exploded after cancelling");
            },
            UrlLookup(ConfiguredUrl));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.ListRemoteBranchesAsync(RepoName, cts.Token));

        var joined = runner.Joined();
        Assert.DoesNotContain(joined, j => j.StartsWith("remote set-url", StringComparison.Ordinal));
        Assert.DoesNotContain("gh_env_token", string.Join(" | ", joined));
    }

    // ── FIX 1 (real launch): the pre-launch guards, observably ────────────────
    //
    // The fake-runner branches perform their OWN pre-invocation cancellation check, so the
    // pre-launch guards in the REAL-process paths cannot be exercised through the seam. These
    // tests therefore run the manager WITHOUT a runner seam.
    //
    // ── Why NOT a child-written marker file ──────────────────────────────────
    //
    // An earlier version of these tests inferred "no launch" from the ABSENCE of a marker written
    // by a stub shell within a polling window. That is timeout-as-success, and two legal
    // schedules defeat it even when the guard is gone and Process.Start really ran:
    //
    //   1. The cancelled parent can return before the spawned shell is ever scheduled, and the
    //      shell can then write its marker AFTER the window closed. Writing the marker as the
    //      shell's first action is NOT synchronous with the parent's Process.Start.
    //   2. On the capture path, production cancellation cleanup kills the whole process tree, so
    //      the shell can die before its first write and NO marker is ever produced.
    //
    // ── The launch-failure sentinel used instead ─────────────────────────────
    //
    // Launch attempts are made observable IN THE PARENT, synchronously, with no child involved:
    // the working directory the manager passes to Process.Start is deleted first. Process.Start
    // then fails SYNCHRONOUSLY with a Win32Exception ("No such file or directory") and NO child
    // process is ever created.
    //
    // That makes the two schedules above structurally impossible rather than merely unlikely:
    //   • There is no child, so no child scheduling can influence the outcome.
    //   • There is no marker to race, and nothing for cancellation cleanup to kill.
    //   • The evidence is the manager's OWN observable result — a non-cancellation launch failure
    //     surfacing out of the call — produced on the same thread, before the call returns.
    //
    // Each negative test therefore asserts an EXACT outcome rather than an absence:
    //   guard present  → OperationCanceledException (the launch never happened), and
    //   guard removed  → the launch-failure sentinel escapes instead, deterministically, because
    //                    Process.Start is reached and throws every single time.
    // No polling, no sleeps, no timing windows, and no spawned child to drain.

    /// <summary>
    /// Marker text of the synchronous <c>Process.Start</c> failure sentinel. .NET wraps the
    /// underlying <c>ENOENT</c> for a missing working directory in this message.
    /// </summary>
    private const string LaunchFailureSentinel = "An error occurred trying to start process";

    /// <summary>
    /// Whether <paramref name="ex"/> (or anything in its inner chain) is the synchronous
    /// launch-failure sentinel — i.e. proof that <c>Process.Start</c> was actually reached.
    /// </summary>
    /// <remarks>
    /// The manager wraps a credential-bearing launch failure in its own exception type, so the
    /// TEXT is matched as well as the type; the whole chain is walked because the wrap may nest.
    /// </remarks>
    private static bool IsLaunchAttempt(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is System.ComponentModel.Win32Exception)
                return true;
            if (current.Message.Contains(LaunchFailureSentinel, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Deletes <paramref name="directory"/> so the next <c>Process.Start</c> that uses it as its
    /// working directory fails synchronously, spawning nothing.
    /// </summary>
    private static void ArmLaunchFailureSentinel(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);

        Assert.False(Directory.Exists(directory), "The launch-failure sentinel was not armed.");
    }

    // ── POSITIVE CONTROLS ────────────────────────────────────────────────────
    //
    // These prove the sentinel is genuinely reachable and genuinely detected on each launch path
    // with an UNCANCELLED token. Without them, a passing negative test could be an artefact of a
    // path that never launches anything at all.

    /// <summary>
    /// POSITIVE CONTROL for the <c>RunGitCoreAsync</c> launch path: with an UNCANCELLED token the
    /// launch really is attempted, so the sentinel surfaces out of the manager.
    /// </summary>
    [Fact]
    public async Task PositiveControl_RunGitCorePath_LaunchIsAttemptedAndObserved()
    {
        // Arms the sentinel from the pre-fetch LOG call: the clone directory exists for the
        // `.git` precheck, and is removed immediately before the first RunGitCoreAsync launch.
        // Arming any earlier would fail the precheck instead of the launch.
        BrainRepoManager? built = null;
        var armer = new ActionLogger(
            "pulling latest on", () => ArmLaunchFailureSentinel(built!.GetClonePath(RepoName)));

        built = new BrainRepoManager(
            _tempDir,
            armer,
            gitRunner: null,
            tokenLookup: TokenLookup(StoredToken),
            // Unconfigured: the refresh short-circuits, so the fetch is the FIRST launch and no
            // origin inspection (a CAPTURE command) runs before it.
            configuredUrlLookup: _ => null);

        Directory.CreateDirectory(Path.Combine(built.GetClonePath(RepoName), ".git"));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            built.EnsureCloneAsync(
                RepoName, ConfiguredUrl, "main", TestContext.Current.CancellationToken));

        Assert.True(armer.Fired, "The arming seam never triggered — the control is not set up.");
        Assert.True(
            IsLaunchAttempt(ex),
            $"The core launch path was never reached — the control cannot validate the guard. Got: {ex}");
        // The sentinel never carries the credential out with it.
        Assert.DoesNotContain(StoredToken, ex.ToString());
    }

    /// <summary>
    /// POSITIVE CONTROL for the <c>RunGitCaptureAsync</c> launch path — a DIFFERENT real-process
    /// runner with its own pre-launch guard. <c>FetchOriginAsync</c> reaches it directly.
    /// </summary>
    [Fact]
    public async Task PositiveControl_RunGitCapturePath_LaunchIsAttemptedAndObserved()
    {
        var manager = new BrainRepoManager(
            _tempDir,
            new TestLogger<BrainRepoManager>(),
            gitRunner: null,
            tokenLookup: TokenLookup(StoredToken),
            // Unconfigured: the refresh short-circuits, so the fetch is the FIRST launch.
            configuredUrlLookup: _ => null);

        var clonePath = manager.GetClonePath(RepoName);
        Directory.CreateDirectory(Path.Combine(clonePath, ".git"));

        // FetchOriginAsync's capture command runs with the clone path as its working directory.
        // The `.git` precheck has already been satisfied above; the configured-URL lookup fires
        // AFTER it, so arming from there removes the directory immediately before the launch.
        var armed = false;
        var armingManager = new BrainRepoManager(
            _tempDir,
            new TestLogger<BrainRepoManager>(),
            gitRunner: null,
            tokenLookup: TokenLookup(StoredToken),
            configuredUrlLookup: _ =>
            {
                armed = true;
                ArmLaunchFailureSentinel(clonePath);
                return null;   // unconfigured: the refresh short-circuits, the fetch is first
            });

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            armingManager.FetchOriginAsync(RepoName, null, TestContext.Current.CancellationToken));

        Assert.True(armed, "The arming seam never triggered — the control is not set up.");
        Assert.True(
            IsLaunchAttempt(ex),
            $"The capture launch path was never reached — the control cannot validate the guard. Got: {ex}");
    }

    // ── NEGATIVE TESTS: each guard, independently ────────────────────────────

    /// <summary>
    /// The <c>RunGitCoreAsync</c> pre-launch guard: cancellation arriving AFTER the credential has
    /// been resolved and attached, but BEFORE the process launch, must not reach
    /// <c>Process.Start</c>.
    /// <para>
    /// This isolates the guard precisely — credential resolution already SUCCEEDED, so the
    /// resolver's own cancellation checks cannot fire. The cancellation is injected from the
    /// pre-clone LOG call, which the manager makes between attaching the credential and invoking
    /// git, and the sentinel is armed so that reaching the launch is unmistakable.
    /// </para>
    /// <para>
    /// Guard present → cancellation. Guard removed → the launch is reached and the sentinel throws
    /// synchronously, on EVERY schedule, because no child and no timing are involved.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunGitCorePreLaunchGuard_CancelledAfterCredentialAttachment_NeverReachesProcessStart()
    {
        using var cts = new CancellationTokenSource();

        // ONE seam does both jobs at the same instant, on the EXISTING-clone fetch path: it arms
        // the sentinel (so reaching Process.Start is unmistakable) and cancels the caller. The
        // `.git` precheck has already passed by then, so the only thing left between here and the
        // launch is the guard under test.
        BrainRepoManager? built = null;
        var seam = new ActionLogger("pulling latest on", () =>
        {
            ArmLaunchFailureSentinel(built!.GetClonePath(RepoName));
            cts.Cancel();
        });

        built = new BrainRepoManager(
            _tempDir,
            seam,
            gitRunner: null,
            tokenLookup: TokenLookup(StoredToken),   // resolves normally: no cancellation here
            // Unconfigured: the refresh short-circuits, so the fetch is the FIRST launch on this
            // path and no CAPTURE command runs before it — this isolates RunGitCoreAsync.
            configuredUrlLookup: _ => null);

        Directory.CreateDirectory(Path.Combine(built.GetClonePath(RepoName), ".git"));

        var ex = await Record.ExceptionAsync(() =>
            built.EnsureCloneAsync(RepoName, ConfiguredUrl, "main", cts.Token));

        Assert.True(seam.Fired, "The cancellation seam never triggered — test premise broken.");
        Assert.NotNull(ex);

        Assert.False(
            IsLaunchAttempt(ex),
            "Process.Start was REACHED after the caller cancelled — the pre-launch guard in "
            + $"RunGitCoreAsync is missing. Observed launch failure: {ex}");

        // The operation ended as cancellation, not as some unrelated failure.
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.DoesNotContain(StoredToken, ex!.ToString());
    }

    /// <summary>
    /// The <c>RunGitCaptureAsync</c> pre-launch guard, ISOLATED so no upstream check can mask it.
    /// <para>
    /// Finding a route with no earlier cancellation check takes care: every path into the refresh
    /// passes checks at the top of <c>RefreshOriginCredentialAsync</c> and inside
    /// <c>ResolveCredentialAsync</c>. The exception is an UNCONFIGURED repository — the refresh
    /// returns as soon as the configured-URL lookup yields nothing, resolving no credential.
    /// Cancelling from inside that lookup lands the operation on <c>FetchOriginAsync</c>'s capture
    /// command with a cancelled token and NOTHING between them but this guard.
    /// </para>
    /// <para>
    /// Guard present → cancellation. Guard removed → the armed sentinel throws synchronously from
    /// Process.Start. Crucially there is no child for cancellation cleanup to kill, which is
    /// exactly the schedule that made the previous marker-file version unsound on this path.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunGitCapturePreLaunchGuard_CancelledBeforeFirstCapture_NeverReachesProcessStart()
    {
        using var cts = new CancellationTokenSource();
        var lookupInvoked = false;

        // The clone path is known before the manager is built, so the lookup can arm it.
        var probe = new BrainRepoManager(_tempDir, new TestLogger<BrainRepoManager>());
        var clonePath = probe.GetClonePath(RepoName);
        Directory.CreateDirectory(Path.Combine(clonePath, ".git"));

        var manager = new BrainRepoManager(
            _tempDir,
            new TestLogger<BrainRepoManager>(),
            gitRunner: null,
            tokenLookup: TokenLookup(StoredToken),
            configuredUrlLookup: _ =>
            {
                // Fires AFTER the refresh's own entry check and after the `.git` precheck. It arms
                // the sentinel and cancels in one step, then reports "unconfigured" so the refresh
                // exits before ResolveCredentialAsync's checks can fire.
                lookupInvoked = true;
                ArmLaunchFailureSentinel(clonePath!);
                cts.Cancel();
                return null;
            });

        var ex = await Record.ExceptionAsync(() => manager.FetchOriginAsync(RepoName, null, cts.Token));

        Assert.True(lookupInvoked, "The cancellation seam never triggered — test premise broken.");
        Assert.NotNull(ex);

        Assert.False(
            IsLaunchAttempt(ex),
            "Process.Start was REACHED after the caller cancelled — the pre-launch guard in "
            + $"RunGitCaptureAsync is missing. Observed launch failure: {ex}");

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    /// <summary>
    /// Logger that runs <paramref name="action"/> the first time it observes a message containing
    /// <paramref name="trigger"/>. This gives a test a deterministic seam at a precise point
    /// INSIDE a production code path — used to arm the launch-failure sentinel (and optionally
    /// cancel) at the exact instant before a process launch, with no timing involved.
    /// </summary>
    private sealed class ActionLogger(string trigger, Action action) : ILogger<BrainRepoManager>
    {
        /// <summary>Whether the trigger message was ever observed.</summary>
        public bool Fired { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (Fired || !formatter(state, exception).Contains(trigger, StringComparison.Ordinal))
                return;

            Fired = true;
            action();
        }
    }

    /// <summary>
    /// Logger that cancels <paramref name="cts"/> the first time it observes a message containing
    /// <paramref name="trigger"/>, giving tests a deterministic cancellation seam at a precise
    /// point inside a production code path.
    /// </summary>
    private sealed class CancellingLogger(CancellationTokenSource cts, string trigger)
        : ILogger<BrainRepoManager>
    {
        /// <summary>Whether the trigger message was ever observed.</summary>
        public bool Fired { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains(trigger, StringComparison.Ordinal))
            {
                Fired = true;
                cts.Cancel();
            }
        }
    }

    // ── FIX 2: THROWN runner exceptions are credential-redacted ───────────────

    /// <summary>
    /// A runner seam that THROWS (rather than returning a failing <see cref="BrainGitResult"/>)
    /// must not escape with the credential in any part of its payload. The whole
    /// <see cref="Exception.ToString"/> is asserted, so the message, the stack trace and every
    /// inner exception are covered.
    /// </summary>
    [Fact]
    public async Task ThrowingRunner_ExceptionPayloadCarriesNoCredential()
    {
        var runner = new CredentialRunner(request =>
            request.Arguments.Contains("fetch")
                ? throw new InvalidOperationException(
                    $"transport blew up using {CredentialOrigin} with bare {StoredToken}")
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(StoredToken, ex.ToString());
        Assert.DoesNotContain("x-access-token", ex.ToString());
        // The unsafe original is DISCARDED — it cannot be recovered through the inner chain.
        Assert.Null(ex.InnerException);
    }

    /// <summary>
    /// The credential can also be hidden in a nested INNER exception, which
    /// <see cref="Exception.Message"/> alone would never reveal.
    /// </summary>
    [Fact]
    public async Task ThrowingRunner_CredentialInInnerException_IsNotExposed()
    {
        var runner = new CredentialRunner(request =>
            request.Arguments.Contains("fetch")
                ? throw new InvalidOperationException(
                    "outer failure",
                    new InvalidOperationException($"inner detail: {StoredToken}"))
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(StoredToken, ex.ToString());
        Assert.Null(ex.InnerException);
    }

    /// <summary>The URI-ESCAPED form of the credential is redacted too.</summary>
    [Fact]
    public async Task ThrowingRunner_EscapedCredentialForm_IsNotExposed()
    {
        // A credential whose escaped form differs from its raw form.
        const string AwkwardToken = "gho tok/en+value";
        var escaped = Uri.EscapeDataString(AwkwardToken);

        var runner = new CredentialRunner(request =>
            request.Arguments.Contains("fetch")
                ? throw new InvalidOperationException($"failed with {escaped} embedded")
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManager(runner, TokenLookup(AwkwardToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(escaped, ex.ToString());
        Assert.DoesNotContain(AwkwardToken, ex.ToString());
    }

    /// <summary>
    /// A runner that throws while executing the credential-bearing <c>remote set-url</c> would
    /// otherwise leak the credential through the exception's ARGUMENT echo.
    /// </summary>
    [Fact]
    public async Task ThrowingRunner_DuringCredentialSetUrl_DoesNotLeakTheArguments()
    {
        var runner = new CredentialRunner(request =>
            request.Arguments.Contains("set-url")
                ? throw new InvalidOperationException(
                    $"cannot write config for {string.Join(' ', request.Arguments)}")
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(StoredToken, ex.ToString());
        Assert.DoesNotContain("x-access-token", ex.ToString());
    }

    /// <summary>A throwing runner on the CLONE path leaks neither the URL nor the bare token.</summary>
    [Fact]
    public async Task ThrowingRunner_DuringCredentialBearingClone_DoesNotLeak()
    {
        var runner = new CredentialRunner(request =>
            request.Arguments.Contains("clone")
                ? throw new InvalidOperationException(
                    $"clone failed: {string.Join(' ', request.Arguments)}")
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManager(
            runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl), createClone: false);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            manager.EnsureCloneAsync(RepoName, ConfiguredUrl, "main", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(StoredToken, ex.ToString());
        Assert.DoesNotContain("x-access-token", ex.ToString());
    }

    /// <summary>
    /// The LOGGER payload is checked too: a swallowed-and-logged exception object must not carry
    /// the credential either. <c>DeleteRemoteBranchAsync</c> logs the exception it catches.
    /// </summary>
    [Fact]
    public async Task ThrowingRunner_LoggedExceptionPayload_CarriesNoCredential()
    {
        var logger = new TestLogger<BrainRepoManager>();
        var runner = new CredentialRunner(request =>
            request.Arguments.Contains("push")
                ? throw new InvalidOperationException(
                    $"push transport failed for {CredentialOrigin} token {StoredToken}")
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl), logger);

        // DeleteRemoteBranchAsync SWALLOWS the failure and reports it through the return value,
        // so the exception object only ever reaches the LOG — which is exactly the payload under
        // test here.
        var result = await manager.DeleteRemoteBranchAsync(
            RepoName, "feature", TestContext.Current.CancellationToken);
        Assert.Equal(BranchDeleteResult.Failed, result);

        // The warning really was written, so the assertions below are not vacuous.
        Assert.Contains(
            logger.LogEntries,
            e => e.Message.Contains("Failed to delete remote branch") && e.Exception is not null);

        foreach (var entry in logger.LogEntries)
        {
            Assert.DoesNotContain(StoredToken, entry.Message);
            var payload = entry.Exception?.ToString() ?? string.Empty;
            Assert.DoesNotContain(StoredToken, payload);
            Assert.DoesNotContain("x-access-token", payload);
        }
    }

    /// <summary>
    /// A throwing runner inside <see cref="BrainRepoManager.FetchOriginAsync"/> — the API the
    /// Composer echoes to the model — is redacted before it propagates.
    /// </summary>
    [Fact]
    public async Task ThrowingRunner_DuringFetchOrigin_PropagatesARedactedException()
    {
        var runner = new CredentialRunner(request =>
            request.Arguments.Contains("fetch")
                ? throw new InvalidOperationException(
                    $"fetch transport failed against {CredentialOrigin} ({StoredToken})")
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            manager.FetchOriginAsync(RepoName, null, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(StoredToken, ex.ToString());
        Assert.DoesNotContain("x-access-token", ex.ToString());
        Assert.Null(ex.InnerException);
    }

    /// <summary>
    /// A CALLER cancellation still propagates untouched through the new exception boundary — it is
    /// never rewrapped into an InvalidOperationException.
    /// </summary>
    [Fact]
    public async Task CredentialBoundary_DoesNotRewrapCallerCancellation()
    {
        var runner = new CredentialRunner();
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.ListRemoteBranchesAsync(RepoName, cts.Token));
    }

    /// <summary>
    /// An exception that is ALREADY credential-free propagates completely unchanged, so exception
    /// types the callers depend on are preserved by the boundary.
    /// </summary>
    [Fact]
    public async Task CredentialBoundary_PreservesCredentialFreeExceptionsExactly()
    {
        var sentinel = new InvalidOperationException("plain transport failure, no secrets here");
        var runner = new CredentialRunner(request =>
            request.Arguments.Contains("fetch")
                ? throw sentinel
                : new BrainGitResult(0, string.Empty, string.Empty));
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken));

        Assert.Same(sentinel, ex);
    }

    // ── FIX A1: credential-bearing CANCELLATION payloads are sanitized ────────

    /// <summary>
    /// A runner that cancels the token and THEN throws an
    /// <see cref="OperationCanceledException"/> whose own payload embeds the credential must not
    /// escape the boundary unsanitized. Cancellation semantics are preserved (it is still an OCE,
    /// still carrying the token) but the payload is replaced.
    /// </summary>
    /// <remarks>
    /// This enters the boundary properly: the lock is acquired, the refresh completes and
    /// publishes the credential, and only then does the fetch cancel and throw. It is the vector
    /// the pre-lock cancellation test can never reach.
    /// </remarks>
    [Fact]
    public async Task PostEntryCancellation_WithCredentialInMessage_IsSanitizedButStaysCancellation()
    {
        using var cts = new CancellationTokenSource();
        var runner = new CredentialRunner(request =>
        {
            if (!request.Arguments.Contains("fetch"))
                return new BrainGitResult(0, string.Empty, string.Empty);

            cts.Cancel();
            throw new OperationCanceledException(StoredToken, cts.Token);
        });
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.ListRemoteBranchesAsync(RepoName, cts.Token));

        // Still cancellation, still carrying the token — semantics preserved.
        Assert.Equal(cts.Token, ex.CancellationToken);
        // …but the payload is clean.
        Assert.DoesNotContain(StoredToken, ex.ToString());
        Assert.Null(ex.InnerException);
    }

    /// <summary>The credential hidden in a cancellation's INNER chain is removed too.</summary>
    [Fact]
    public async Task PostEntryCancellation_WithCredentialInInnerException_IsSanitized()
    {
        using var cts = new CancellationTokenSource();
        var runner = new CredentialRunner(request =>
        {
            if (!request.Arguments.Contains("fetch"))
                return new BrainGitResult(0, string.Empty, string.Empty);

            cts.Cancel();
            throw new OperationCanceledException(
                "cancelled", new InvalidOperationException($"inner holds {StoredToken}"), cts.Token);
        });
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.ListRemoteBranchesAsync(RepoName, cts.Token));

        Assert.DoesNotContain(StoredToken, ex.ToString());
        Assert.Null(ex.InnerException);
    }

    /// <summary>The URI-ESCAPED credential form inside a cancellation payload is removed.</summary>
    [Fact]
    public async Task PostEntryCancellation_WithEscapedCredentialForm_IsSanitized()
    {
        const string AwkwardToken = "gho tok/en+value";
        var escaped = Uri.EscapeDataString(AwkwardToken);

        using var cts = new CancellationTokenSource();
        var runner = new CredentialRunner(request =>
        {
            if (!request.Arguments.Contains("fetch"))
                return new BrainGitResult(0, string.Empty, string.Empty);

            cts.Cancel();
            throw new OperationCanceledException($"cancelled carrying {escaped}", cts.Token);
        });
        var manager = CreateManager(runner, TokenLookup(AwkwardToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.ListRemoteBranchesAsync(RepoName, cts.Token));

        Assert.DoesNotContain(escaped, ex.ToString());
        Assert.DoesNotContain(AwkwardToken, ex.ToString());
    }

    /// <summary>The same vector through <see cref="BrainRepoManager.FetchOriginAsync"/>.</summary>
    [Fact]
    public async Task PostEntryCancellation_DuringFetchOrigin_IsSanitized()
    {
        using var cts = new CancellationTokenSource();
        var runner = new CredentialRunner(request =>
        {
            if (!request.Arguments.Contains("fetch"))
                return new BrainGitResult(0, string.Empty, string.Empty);

            cts.Cancel();
            throw new OperationCanceledException(
                $"cancelled against {CredentialOrigin}",
                new Exception(StoredToken),
                cts.Token);
        });
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.FetchOriginAsync(RepoName, null, cts.Token));

        Assert.DoesNotContain(StoredToken, ex.ToString());
        Assert.DoesNotContain("x-access-token", ex.ToString());
        Assert.Null(ex.InnerException);
    }

    /// <summary>
    /// A cancellation thrown by the TOKEN LOOKUP itself, carrying a BARE token in its payload.
    /// <para>
    /// This is the vector ONLY the lookup catch can close. At that point the token was never
    /// assigned, so nothing — not this catch, not the operation-level boundary — knows the value
    /// to redact, and a bare token matches no URL pattern. The payload must therefore be replaced
    /// WHOLESALE with fixed text rather than inspected.
    /// </para>
    /// </summary>
    [Fact]
    public async Task LookupCancellation_WithBareTokenInPayload_IsReplacedWholesale()
    {
        using var cts = new CancellationTokenSource();
        var runner = new CredentialRunner();
        var manager = CreateManager(
            runner,
            _ =>
            {
                cts.Cancel();
                // A BARE token: no URL around it, and never published anywhere the code can see.
                throw new OperationCanceledException(
                    StoredToken, new Exception(StoredToken), cts.Token);
            },
            UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.ListRemoteBranchesAsync(RepoName, cts.Token));

        Assert.DoesNotContain(StoredToken, ex.ToString());
        Assert.Null(ex.InnerException);
        // Cancellation semantics preserved.
        Assert.Equal(cts.Token, ex.CancellationToken);
        // No git command ran.
        Assert.Empty(runner.Requests);
    }

    /// <summary>
    /// A cancellation thrown by the TOKEN LOOKUP itself — before any credential is published to
    /// the box — is sanitized at the lookup catch, since the operation-level boundary would have
    /// no credential to match literally.
    /// </summary>
    [Fact]
    public async Task LookupCancellation_WithCredentialInItsOwnPayload_IsSanitized()
    {
        using var cts = new CancellationTokenSource();
        var runner = new CredentialRunner();
        var manager = CreateManager(
            runner,
            _ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(
                    $"lookup cancelled fetching {CredentialOrigin}",
                    new Exception(StoredToken),
                    cts.Token);
            },
            UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.ListRemoteBranchesAsync(RepoName, cts.Token));

        Assert.DoesNotContain(StoredToken, ex.ToString());
        Assert.DoesNotContain("x-access-token", ex.ToString());
        Assert.Null(ex.InnerException);
    }

    /// <summary>
    /// A credential-FREE cancellation still propagates completely untouched — the boundary does
    /// not rewrap what it does not need to.
    /// </summary>
    [Fact]
    public async Task PostEntryCancellation_WithCleanPayload_PropagatesReferenceIdentical()
    {
        using var cts = new CancellationTokenSource();
        OperationCanceledException? thrown = null;
        var runner = new CredentialRunner(request =>
        {
            if (!request.Arguments.Contains("fetch"))
                return new BrainGitResult(0, string.Empty, string.Empty);

            cts.Cancel();
            thrown = new OperationCanceledException("plain cancellation", cts.Token);
            throw thrown;
        });
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.ListRemoteBranchesAsync(RepoName, cts.Token));

        Assert.Same(thrown, ex);
    }

    // ── FIX A2: URL redaction independent of the selected credential ──────────

    /// <summary>
    /// With credential A selected, an exception echoing a DIFFERENT (stale) credential-bearing URL
    /// must still be redacted. The literal pass cannot see token B, so URL redaction has to run
    /// unconditionally.
    /// </summary>
    [Fact]
    public async Task ThrownExceptionWithStaleForeignCredentialUrl_IsRedacted()
    {
        const string StaleToken = "gho_stale_persisted_token";
        var staleUrl = $"https://x-access-token:{StaleToken}@github.com/acme/widgets.git";

        var runner = new CredentialRunner(request =>
            request.Arguments.Contains("fetch")
                ? throw new InvalidOperationException($"could not read from '{staleUrl}'")
                : new BrainGitResult(0, string.Empty, string.Empty));
        // The operation selects a DIFFERENT credential.
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(StaleToken, ex.ToString());
        Assert.DoesNotContain("x-access-token", ex.ToString());
        Assert.Null(ex.InnerException);
    }

    /// <summary>
    /// The stale credential can also arrive from the ORIGIN-INSPECTION command, which runs before
    /// any credential is published to the box.
    /// </summary>
    [Fact]
    public async Task ThrownExceptionDuringOriginInspection_WithCredentialUrl_IsRedacted()
    {
        const string StaleToken = "gho_inspection_stale_token";
        var staleUrl = $"https://x-access-token:{StaleToken}@github.com/acme/widgets.git";

        var runner = new CredentialRunner(_ => new BrainGitResult(0, string.Empty, string.Empty));
        runner.OnRequest = request =>
        {
            if (request.Arguments.Count >= 2
                && request.Arguments[0] == "remote"
                && request.Arguments[1] == "get-url")
            {
                throw new InvalidOperationException($"config read failed for '{staleUrl}'");
            }
        };
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(StaleToken, ex.ToString());
        Assert.DoesNotContain("x-access-token", ex.ToString());
    }

    /// <summary>
    /// A NO-CREDENTIAL operation over a clone with a persisted credential-bearing origin: there is
    /// no selected credential at all, so ONLY unconditional URL redaction can protect the payload.
    /// </summary>
    [Fact]
    public async Task NoSelectedCredential_ThrownExceptionWithPersistedCredentialUrl_IsRedacted()
    {
        const string PersistedToken = "gho_persisted_no_selection_token";
        var persistedUrl = $"https://x-access-token:{PersistedToken}@github.com/acme/widgets.git";

        var runner = new CredentialRunner(request =>
            request.Arguments.Contains("fetch")
                ? throw new InvalidOperationException($"fatal: unable to access '{persistedUrl}/'")
                : new BrainGitResult(0, string.Empty, string.Empty))
        {
            Origins = [persistedUrl],
        };
        // NO token anywhere: the operation selects nothing.
        var manager = CreateManager(runner, TokenLookup(null), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(PersistedToken, ex.ToString());
        Assert.DoesNotContain("x-access-token", ex.ToString());
    }

    /// <summary>
    /// The same no-selected-credential hole on the LOGGER path:
    /// <c>DeleteRemoteBranchAsync</c> swallows the failure and logs the exception object.
    /// </summary>
    [Fact]
    public async Task NoSelectedCredential_LoggedExceptionWithPersistedCredentialUrl_IsRedacted()
    {
        const string PersistedToken = "gho_persisted_logged_token";
        var persistedUrl = $"https://x-access-token:{PersistedToken}@github.com/acme/widgets.git";

        var logger = new TestLogger<BrainRepoManager>();
        var runner = new CredentialRunner(request =>
            request.Arguments.Contains("push")
                ? throw new InvalidOperationException($"push failed against '{persistedUrl}'")
                : new BrainGitResult(0, string.Empty, string.Empty))
        {
            Origins = [persistedUrl],
        };
        var manager = CreateManager(runner, TokenLookup(null), UrlLookup(ConfiguredUrl), logger);

        var result = await manager.DeleteRemoteBranchAsync(
            RepoName, "feature", TestContext.Current.CancellationToken);
        Assert.Equal(BranchDeleteResult.Failed, result);

        // The warning really was written, so the assertions are not vacuous.
        Assert.Contains(
            logger.LogEntries,
            e => e.Message.Contains("Failed to delete remote branch") && e.Exception is not null);

        foreach (var entry in logger.LogEntries)
        {
            Assert.DoesNotContain(PersistedToken, entry.Message);
            var payload = entry.Exception?.ToString() ?? string.Empty;
            Assert.DoesNotContain(PersistedToken, payload);
            Assert.DoesNotContain("x-access-token", payload);
        }
    }

    // ── FIX 4: push-destination inspection fails CLOSED ───────────────────────

    /// <summary>
    /// An EXPLICITLY EMPTY (or whitespace) pushurl is still an explicit override: exit 0 means the
    /// key exists, so it must be rejected rather than treated as absence.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    public async Task ExplicitlyEmptyPushUrl_IsRejectedWithoutAnySetUrlOrNetworkCommand(string pushUrl)
    {
        var runner = new CredentialRunner { PushUrlExit = 0, PushUrl = pushUrl };
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken));

        Assert.Equal(
            $"Refusing to refresh credentials for '{RepoName}': its 'origin' remote has an explicit push URL.",
            ex.Message);
        Assert.DoesNotContain(StoredToken, ex.Message);

        var joined = runner.Joined();
        Assert.DoesNotContain(joined, j => j.StartsWith("remote set-url", StringComparison.Ordinal));
        Assert.DoesNotContain(joined, j => NetworkVerbs.Any(v => j.StartsWith(v + " ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// An UNEXPECTED inspection failure (anything other than git's exit-1 "key not set") cannot
    /// establish the destination policy, so it is rejected with a fixed credential-free
    /// diagnostic instead of failing open.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(128)]
    [InlineData(-1)]
    public async Task UnexpectedPushUrlInspectionFailure_IsRejectedWithoutAnySetUrlOrNetworkCommand(int exitCode)
    {
        var runner = new CredentialRunner { PushUrlExit = exitCode, PushUrl = string.Empty };
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken));

        Assert.Equal(
            $"Refusing to refresh credentials for '{RepoName}': its 'origin' push destination could not be determined.",
            ex.Message);
        Assert.DoesNotContain(StoredToken, ex.Message);

        var joined = runner.Joined();
        Assert.DoesNotContain(joined, j => j.StartsWith("remote set-url", StringComparison.Ordinal));
        Assert.DoesNotContain(joined, j => NetworkVerbs.Any(v => j.StartsWith(v + " ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Exit 1 is git's genuine "key is not set" signal and remains the ONLY accepted absence — the
    /// refresh proceeds normally.
    /// </summary>
    [Fact]
    public async Task PushUrlExitOne_IsTheAcceptedAbsence_AndTheRefreshProceeds()
    {
        var runner = new CredentialRunner { PushUrlExit = 1, PushUrl = string.Empty };
        var manager = CreateManager(runner, TokenLookup(StoredToken), UrlLookup(ConfiguredUrl));

        await manager.ListRemoteBranchesAsync(RepoName, TestContext.Current.CancellationToken);

        Assert.Contains($"remote set-url origin {CredentialOrigin}", runner.Joined());
    }

    // ── Locked serialization ──────────────────────────────────────────────────

    /// <summary>The phase of the first caller's operation at which the lock is held.</summary>
    public enum ParkPhase
    {
        /// <summary>Parked at the credential-attaching <c>remote set-url</c> (the REFRESH).</summary>
        Refresh,

        /// <summary>Parked at the <c>fetch</c> — AFTER the refresh already completed.</summary>
        Fetch,
    }

    /// <summary>
    /// The refresh and the operation it authenticates share ONE acquisition of the per-repository
    /// semaphore, so a second caller cannot begin ANY work — not even its own refresh — while the
    /// first caller's operation owns the lock.
    /// <para>
    /// <b>The contention is established deterministically, with no elapsed-time assertion.</b>
    /// The fake runner is SYNCHRONOUS, so while the first caller is parked inside it, the second
    /// call is invoked DIRECTLY on the test thread (not via <c>Task.Run</c>). An async method runs
    /// synchronously until its first incomplete await, therefore:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///   If the lock is held, the second call reaches <c>SemaphoreSlim.WaitAsync</c>, finds it
    ///   contended, and returns an INCOMPLETE task. Observing <c>IsCompleted == false</c> proves
    ///   the caller actually reached and blocked on the lock — not merely that a task was never
    ///   scheduled, which is the flaw a negative timed wait cannot exclude.
    ///   </description></item>
    ///   <item><description>
    ///   If the lock is missing or reduced, nothing blocks the second call, so it runs to
    ///   COMPLETION synchronously on the test thread — the returned task is already completed and
    ///   its git requests are already recorded. Both assertions fail immediately.
    ///   </description></item>
    /// </list>
    /// <para>
    /// The <see cref="ParkPhase.Fetch"/> case is what kills a REFRESH-ONLY lock: the first caller
    /// is parked after its refresh finished, so a lock released at the end of the refresh would
    /// already be free and the contender would complete synchronously.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(ParkPhase.Refresh)]
    [InlineData(ParkPhase.Fetch)]
    public async Task SecondCallerDoesNoWork_WhileTheFirstOperationOwnsTheLock(ParkPhase phase)
    {
        const string TokenA = "gho_caller_a_token";
        const string TokenB = "gho_caller_b_token";
        const string BranchA = "alpha";
        const string BranchB = "beta";

        var ct = TestContext.Current.CancellationToken;

        using var firstParked = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);

        var runner = new CredentialRunner();
        runner.OnRequest = request =>
        {
            var joined = string.Join(' ', request.Arguments);
            var isParkPoint = phase switch
            {
                // The credential attachment: the refresh is IN PROGRESS.
                ParkPhase.Refresh => joined.StartsWith("remote set-url origin ", StringComparison.Ordinal)
                                     && joined.Contains(TokenA, StringComparison.Ordinal),
                // The network command: the refresh is already COMPLETE.
                ParkPhase.Fetch => joined.Contains($"refs/heads/{BranchA}:", StringComparison.Ordinal),
                _ => throw new InvalidOperationException($"Unhandled phase '{phase}'."),
            };

            if (!isParkPoint || firstParked.IsSet)
                return;

            firstParked.Set();
            releaseFirst.Wait(TimeSpan.FromSeconds(30));
        };

        // Each caller gets its OWN credential, so every set-url is unambiguously attributable.
        var issued = 0;
        var manager = CreateManager(
            runner,
            _ => Task.FromResult<string?>(Interlocked.Increment(ref issued) == 1 ? TokenA : TokenB),
            UrlLookup(ConfiguredUrl));

        Task<BrainFetchResult>? first = null;
        Task<BrainFetchResult>? second = null;
        try
        {
            first = Task.Run(() => manager.FetchOriginAsync(RepoName, BranchA, ct), ct);
            Assert.True(
                firstParked.Wait(TimeSpan.FromSeconds(30), ct),
                $"The first caller never reached its {phase} park point.");

            // Snapshot of everything the FIRST caller has done so far.
            var beforeContender = runner.Joined();

            if (phase == ParkPhase.Fetch)
            {
                // Sanity: the first caller's refresh really did complete before it parked, so a
                // refresh-only lock would already have been released at this instant.
                Assert.Contains(
                    beforeContender,
                    j => j.StartsWith("remote set-url origin ", StringComparison.Ordinal)
                         && j.Contains(TokenA, StringComparison.Ordinal));
            }

            // ── The deterministic contention probe ────────────────────────────
            // Invoked DIRECTLY (no Task.Run): it runs synchronously until it blocks on the lock.
            second = manager.FetchOriginAsync(RepoName, BranchB, ct);

            Assert.False(
                second.IsCompleted,
                $"The contender ran to completion while the first caller's operation was parked at "
                + $"its {phase} phase — the per-repository lock is missing or is held only for the "
                + $"refresh. Requests recorded: {string.Join(" | ", runner.Joined())}");

            // It performed NO work at all: not its refresh, not even its origin inspection.
            var afterContender = runner.Joined();
            Assert.Equal(beforeContender.Count, afterContender.Count);
            Assert.DoesNotContain(
                afterContender, j => j.Contains(TokenB, StringComparison.Ordinal));
            Assert.DoesNotContain(
                afterContender, j => j.Contains($"refs/heads/{BranchB}:", StringComparison.Ordinal));
        }
        finally
        {
            // ALWAYS release the parked caller, including on assertion failure, so no participant
            // is left blocked on its 30-second wait.
            releaseFirst.Set();

            if (first is not null)
                await SettleAsync(first);
            if (second is not null)
                await SettleAsync(second);
        }

        // Both completed successfully once the lock was released.
        Assert.True(first!.IsCompletedSuccessfully);
        Assert.True(second!.IsCompletedSuccessfully);

        // Strict ordering: the contender's FIRST request comes after the first caller's LAST one.
        var joinedAll = runner.Joined();
        var refreshA = joinedAll.FindIndex(j =>
            j.StartsWith("remote set-url origin ", StringComparison.Ordinal)
            && j.Contains(TokenA, StringComparison.Ordinal));
        var fetchA = joinedAll.FindIndex(j =>
            j.Contains($"refs/heads/{BranchA}:", StringComparison.Ordinal));
        var refreshB = joinedAll.FindIndex(j =>
            j.StartsWith("remote set-url origin ", StringComparison.Ordinal)
            && j.Contains(TokenB, StringComparison.Ordinal));
        var fetchB = joinedAll.FindIndex(j =>
            j.Contains($"refs/heads/{BranchB}:", StringComparison.Ordinal));

        Assert.True(refreshA >= 0 && fetchA >= 0 && refreshB >= 0 && fetchB >= 0,
            $"Not every expected request was recorded: {string.Join(" | ", joinedAll)}");

        Assert.True(refreshA < fetchA, "The first caller fetched before refreshing.");
        Assert.True(fetchA < refreshB,
            "The contender's refresh began before the first caller's fetch — the operation did "
            + "not hold the lock across its network command.");
        Assert.True(refreshB < fetchB, "The contender fetched before refreshing.");

        // No cross-contamination: each caller used only its own credential.
        Assert.DoesNotContain(TokenB, joinedAll[refreshA]);
        Assert.DoesNotContain(TokenA, joinedAll[refreshB]);
    }

    /// <summary>
    /// Awaits <paramref name="task"/> to completion, swallowing its outcome. Used in cleanup so a
    /// failing assertion surfaces the REAL failure rather than a secondary fault from a drained
    /// participant, while still guaranteeing no task is left running.
    /// </summary>
    private static async Task SettleAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch
        {
            // Outcome is asserted by the caller when relevant.
        }
    }
}

using System.Diagnostics;
using CopilotHive.Git;
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
/// environment chain. The credential authenticates the operation's network commands through an
/// OPERATION-SCOPED, token-free askpass helper (<see cref="HostGitAskpassHelper"/>) against the
/// SANITIZED configured URL, so it never reaches <c>.git/config</c>, a git argument list, or this
/// process's own (long-lived) environment. It is additionally used to redact the credential out of
/// any exception text.
/// </para>
/// <para>
/// <b>Eligibility.</b> Askpass is installed ONLY where this manager's historical credential
/// injection was eligible: an <c>https://github.com/</c> URL. A NONELIGIBLE destination (local path,
/// <c>file://</c>, SSH, another host, plain HTTP) gets NO credential in any form — it proceeds
/// exactly as it always has. This manager deliberately does NOT share
/// <see cref="CopilotHive.Git.BrainRepoManager"/>'s destination/pushurl validation: each manager
/// keeps its own origin policy.
/// </para>
/// </summary>
public class ConfigRepoManager
{
    private readonly string _configRepoUrl;
    private readonly string _localPath;
    private HiveConfigFile? _cachedConfig;
    private readonly SemaphoreSlim _gitLock = new(1, 1);

    /// <summary>
    /// Where this manager's credential-free best-effort diagnostics go. <c>null</c> in production,
    /// where <see cref="Console.Error"/> is written; a test sets it to CAPTURE the text a cleanup
    /// problem produced instead of letting it escape to the console.
    /// </summary>
    /// <remarks>
    /// This manager has no logger of its own, so this is the one channel its out-of-band diagnostics
    /// use. Every message written through it has already passed the credential redaction pass.
    /// </remarks>
    internal Action<string>? DiagnosticSink { get; set; }

    /// <summary>
    /// Writes an ALREADY-SANITIZED, credential-free diagnostic. Never throws: a failing sink must not
    /// turn a best-effort cleanup note into a new failure.
    /// </summary>
    private void WriteDiagnostic(string message)
    {
        try
        {
            if (DiagnosticSink is { } sink)
                sink(message);
            else
                Console.Error.WriteLine(message);
        }
        catch
        {
            // A diagnostic channel that is unavailable must never fail the operation.
        }
    }

    /// <summary>
    /// The BOUND on a failed/cancelled clone's residual origin normalization. Deliberately short:
    /// this is a local <c>remote set-url</c>, and a hung cleanup must never delay the primary
    /// exception or cancellation it is unwinding.
    /// </summary>
    private static readonly TimeSpan ResidualCleanupTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// TEST SEAM — overrides the platform predicate <see cref="HostGitAskpassHelper.TryCreate"/>
    /// uses (<see cref="OperatingSystem.IsLinux"/> by default). <c>null</c> in production, where the
    /// real predicate is the single authority: on any non-Linux platform the helper fails closed and
    /// no credential is transported at all.
    /// </summary>
    internal Func<bool>? AskpassPlatformSupported { get; set; }

    /// <summary>
    /// Serialises the MULTI-STEP operations that span <c>hive-config.yaml</c> and the live
    /// <see cref="HiveConfigFile"/>: every <see cref="ConfigModelService"/> read-modify-write-commit
    /// transaction (and the live Brain update that follows it), plus the dispatcher's periodic
    /// config-repo sync/reload. One participant can therefore never interleave with the other.
    /// <para>
    /// It lives on this manager because this manager is the ONE instance the two participants
    /// share; it is deliberately NOT a service field.
    /// </para>
    /// <para>
    /// <b>Lock order:</b> this semaphore → <c>_gitLock</c> → the short-lived
    /// <see cref="HiveConfigFile"/> catalog monitor. It is NEVER acquired from inside
    /// <c>_gitLock</c>, so a single git/file call can never wait on a whole config operation.
    /// </para>
    /// <para>
    /// <b>Not reentrant.</b> A caller that already holds it must not enter another region that
    /// acquires it — that includes every lock-taking <see cref="ConfigModelService"/> method and
    /// <c>DispatcherMaintenance.SyncAgentsFromConfigRepoAsync</c>.
    /// </para>
    /// </summary>
    public SemaphoreSlim ConfigOperationLock { get; } = new(1, 1);

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
    /// Test seam OBSERVING the launch environment. <c>null</c> in production.
    /// </summary>
    /// <remarks>
    /// Called with the working directory, the git arguments and the COMPLETE child environment the
    /// launch would use — or <c>null</c> when the launch inherits the parent process environment
    /// unchanged (a local-only command, or an operation that resolved no credential). It is invoked
    /// for EVERY launch, before <see cref="GitRunner"/> or the real process, and the same dictionary
    /// is what the real-process runner installs on the child. A test can therefore assert the ACTUAL
    /// environment a git process would receive — the operation-scoped askpass wiring and the ABSENCE
    /// of every inherited credential variable — which argv inspection alone can never show.
    /// <para>
    /// It is a separate seam (not an extra <see cref="GitRunner"/> parameter) so every existing
    /// three-argument runner delegate keeps working unchanged.
    /// </para>
    /// </remarks>
    internal Action<string, string[], IReadOnlyDictionary<string, string?>?>? GitLaunchObserver { get; set; }

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
    /// <param name="localPath">The clone to abort a merge in.</param>
    /// <param name="auth">The operation's authenticated transport (redaction only; this command is local).</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task TryAbortMergeAsync(string localPath, BrainGitAuth? auth, CancellationToken ct)
    {
        try
        {
            await RunGitAsync(localPath, ["merge", "--abort"], auth, ct);
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
    /// <b>Existing clone.</b> The <c>origin</c> is normalized to the sanitized configured URL and the
    /// operation-scoped askpass helper is created immediately before the pull (the first network
    /// command), so the pull authenticates without any credential reaching <c>.git/config</c> or argv.
    /// </para>
    /// <para>
    /// <b>Clone.</b> The clone runs against the SANITIZED configured URL with the same askpass helper;
    /// no tokenized clone URL is constructed. The origin is normalized to the sanitized URL right
    /// after the clone as before. A failed or cancelled clone that still left a usable repository has
    /// its origin normalized best-effort by a bounded LOCAL cleanup that does not depend on the
    /// caller's (possibly already cancelled) token; the primary exception or cancellation always
    /// propagates, and a cleanup that cannot complete emits a credential-free diagnostic instead of
    /// claiming guaranteed normalization.
    /// </para>
    /// <para>
    /// A NONELIGIBLE destination (anything but <c>https://github.com/</c>) gets no credential in any
    /// form and proceeds exactly as it did before this transport existed.
    /// </para>
    /// </summary>
    public async Task SyncRepoAsync(CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        // The operation's authenticated transport, disposed by the finally below on EVERY exit path
        // (success, failure and cancellation) so its temporary helper directory cannot leak.
        BrainGitAuth? auth = null;
        try
        {
            if (Directory.Exists(Path.Combine(_localPath, ".git")))
            {
                // The pull is the first network command — normalize origin and build the transport
                // immediately before it.
                auth = await EnsureOriginCredentialAsync(ct);
                try
                {
                    await RunGitAsync(_localPath, ["pull"], auth, ct);
                }
                catch (Exception ex)
                {
                    // A CALLER cancellation is not a merge failure: propagate it immediately
                    // and run NO cleanup command — the caller asked us to stop.
                    if (ex is OperationCanceledException && ct.IsCancellationRequested)
                        throw;

                    await TryAbortMergeAsync(_localPath, auth, ct);
                    throw;
                }
            }
            else
            {
                // The clone authenticates the SANITIZED configured URL through the askpass helper;
                // the argument list carries no credential.
                auth = await ResolveCloneAuthAsync(ct);
                var parent = Path.GetDirectoryName(_localPath)!;
                Directory.CreateDirectory(parent);
                var dirName = Path.GetFileName(_localPath);
                var cloneUrl = auth?.SanitizedOriginUrl ?? _configRepoUrl;
                try
                {
                    await RunGitAsync(parent, ["clone", cloneUrl, dirName], auth, ct);
                }
                catch (Exception)
                {
                    // A failed or cancelled clone may still have left a usable repository whose
                    // origin would otherwise keep whatever the clone wrote. Normalize it
                    // best-effort, then let the primary outcome through unchanged.
                    if (auth is not null)
                        await TryNormalizeResidualCloneOriginAsync(_localPath, auth);

                    throw;
                }

                // Normalize origin back to the sanitized URL right AFTER the clone — one code
                // path, regardless of whether a credential was present. (The clone above already
                // used the sanitized URL, so this is an idempotent re-assert of the same value, kept
                // because it also repairs an origin a previous deployment left tokenized.)
                //
                // A NONELIGIBLE destination keeps the PRE-TRANSPORT form EXACTLY: the configured URL
                // verbatim, never re-spelled. Sanitizing it would rewrite e.g. an ssh scp-form or
                // userinfo-bearing URL, which is a change this round must not make.
                var normalizedOrigin = IsCredentialEligible(_configRepoUrl)
                    ? SanitizeOriginUrl(_configRepoUrl)
                    : _configRepoUrl;
                await RunGitAsync(_localPath, ["remote", "set-url", "origin", normalizedOrigin], null, ct);
                await RunGitAsync(_localPath, ["config", "user.email", "copilothive@local"], null, ct);
                await RunGitAsync(_localPath, ["config", "user.name", "CopilotHive"], null, ct);
            }

            _cachedConfig = null;
        }
        finally
        {
            auth?.Dispose();
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Resolves the credential for a NEW clone and builds its transport, or returns <c>null</c> so the
    /// clone proceeds with the plain configured URL.
    /// </summary>
    /// <remarks>
    /// The eligible-destination rule is the same one <see cref="EnsureOriginCredentialAsync"/>
    /// applies: only an <c>https://github.com/</c> repository is ever credentialed.
    /// </remarks>
    private async Task<BrainGitAuth?> ResolveCloneAuthAsync(CancellationToken ct)
    {
        var credential = await ResolveCredentialAsync(ct);
        if (credential is null || !IsCredentialEligible(_configRepoUrl))
            return null;

        var sanitizedOrigin = SanitizeOriginUrl(_configRepoUrl);

        var auth = CreateAuth(credential, sanitizedOrigin);
        if (auth is null)
            throw new InvalidOperationException(
                "Refusing to authenticate config repo git operations: the operation-scoped "
                + "credential helper is unavailable on this platform.");

        return auth;
    }

    /// <summary>
    /// Best-effort normalization of a clone's <c>origin</c> to the SANITIZED configured URL after a
    /// failed or cancelled clone attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it is bounded and token-independent.</b> The caller's token may already be cancelled,
    /// which is precisely the case this exists for, so the cleanup runs under a FRESH short-lived
    /// token. Nothing here needs network access or the operation's credential: this is a local
    /// <c>remote set-url</c> against an already-created repository.
    /// </para>
    /// <para>
    /// <b>Never masks the primary outcome.</b> If no repository was created there is nothing to
    /// normalize. If the repository is unreadable, the config is locked, or the cleanup cannot
    /// complete within the bound, a CREDENTIAL-FREE diagnostic is emitted and the operation proceeds
    /// — normalization is best-effort and is never claimed as guaranteed.
    /// </para>
    /// </remarks>
    /// <param name="clonePath">The clone destination the failed attempt was writing.</param>
    /// <param name="auth">The operation's transport (sanitized origin and redaction credential).</param>
    private async Task TryNormalizeResidualCloneOriginAsync(string clonePath, BrainGitAuth auth)
    {
        if (!Directory.Exists(Path.Combine(clonePath, ".git")))
            return; // No repository was created — nothing to normalize.

        using var cleanupCts = new CancellationTokenSource(ResidualCleanupTimeout);
        try
        {
            var result = await RunGitCoreAsync(
                clonePath, ["remote", "set-url", "origin", auth.SanitizedOriginUrl], cleanupCts.Token);

            if (result.ExitCode != 0)
            {
                // Sanitized with the operation's credential so neither the argument list nor git's
                // output can carry a secret into the diagnostic.
                WriteDiagnostic(Sanitize(
                    $"Could not normalize the residual config-repo origin after a failed clone "
                    + $"(exit {result.ExitCode}); the sanitized URL is not guaranteed to be in place: "
                    + $"{result.Stderr.Trim()}",
                    auth.Credential));
            }
        }
        catch (OperationCanceledException)
        {
            WriteDiagnostic(
                $"Could not normalize the residual config-repo origin after a failed clone within "
                + $"{ResidualCleanupTimeout.TotalSeconds}s; the sanitized URL is not guaranteed to be in place.");
        }
        catch (Exception ex)
        {
            // Credential-free, exception-object free: the repository may be unreadable or its config
            // locked, and the exception's own text could carry a path or a URL.
            WriteDiagnostic(Sanitize(
                $"Could not normalize the residual config-repo origin after a failed clone: {ex.Message}",
                auth.Credential));
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
    /// Resolves the operation credential and, when one is present AND this manager's destination is
    /// eligible, builds the operation's authenticated transport: the SANITIZED configured URL plus a
    /// freshly created <see cref="HostGitAskpassHelper"/> that carries the credential into git
    /// processes only. Returns the transport so the git wrappers can apply it and the literal
    /// redaction pass.
    /// <para>
    /// <b>The stale-origin rule (no-askpass form).</b> A <c>null</c> resolution — and any
    /// NONELIGIBLE destination — installs NO askpass: a transient resolver failure must never strip a
    /// working persisted credential, and a destination this manager never credentialed must receive no
    /// credential in any form. The declared and accepted consequence is that a revoked token can
    /// persist in <c>.git/config</c> until a later successful resolution normalizes it; there is no
    /// automatic removal.
    /// </para>
    /// <para>
    /// <b>NONELIGIBLE destinations are byte-for-byte unchanged.</b> They keep the pre-transport
    /// origin write with the configured URL VERBATIM — the historical token-injection helper returned
    /// such a URL untouched, so that write carried no credential then and carries none now — and they
    /// receive no askpass transport.
    /// </para>
    /// </summary>
    /// <returns>The operation's authenticated transport, or <c>null</c> when none applies.</returns>
    private async Task<BrainGitAuth?> EnsureOriginCredentialAsync(CancellationToken ct)
    {
        var credential = await ResolveCredentialAsync(ct);
        if (credential is null)
            return null;

        // THE ELIGIBILITY BOUNDARY. This manager's historical credential injection applied only to
        // an https://github.com/ URL; anything else gets NO credential in any form — no askpass, no
        // argument, no environment entry.
        if (!IsCredentialEligible(_configRepoUrl))
        {
            // The pre-transport behaviour for this destination, preserved exactly: the configured URL
            // is written back VERBATIM (the old injection helper returned it unchanged) and no
            // askpass helper is created.
            await RunGitAsync(_localPath, ["remote", "set-url", "origin", _configRepoUrl], null, ct);
            return null;
        }

        var sanitizedOrigin = SanitizeOriginUrl(_configRepoUrl);

        // FAIL CLOSED on the platform, BEFORE any mutation: no helper means no credential transport
        // at all, reported as an honest, credential-free error — never a silent fallback to a
        // tokenized URL.
        var auth = CreateAuth(credential, sanitizedOrigin);
        if (auth is null)
        {
            throw new InvalidOperationException(
                "Refusing to authenticate config repo git operations: the operation-scoped "
                + "credential helper is unavailable on this platform.");
        }

        try
        {
            // NORMALIZE FIRST, AUTHENTICATE SECOND. The origin is rewritten to the credential-free
            // configured URL BEFORE the authenticated network command. This is also the step that
            // strips a stale token a previous deployment wrote; a failure here throws
            // (credential-free text: the URL is the sanitized one) so no network command runs against
            // an origin we could not normalize.
            await RunGitAsync(_localPath, ["remote", "set-url", "origin", sanitizedOrigin], auth, ct);
        }
        catch
        {
            // Ownership has not left this method yet, so dispose here rather than leaking the
            // helper directory on the normalization-failure path.
            auth.Dispose();
            throw;
        }

        return auth;
    }

    /// <summary>
    /// Builds the operation-scoped authenticated transport for a resolved
    /// <paramref name="credential"/>, or <c>null</c> when this platform cannot execute the helper (an
    /// explicit fail-closed result — never a tokenized-URL fallback).
    /// </summary>
    private BrainGitAuth? CreateAuth(string credential, string sanitizedOrigin)
    {
        var helper = HostGitAskpassHelper.TryCreate(AskpassPlatformSupported);
        return helper is null
            ? null
            : new BrainGitAuth(credential, sanitizedOrigin, helper);
    }

    /// <summary>
    /// Whether <paramref name="url"/> is a destination this manager has ALWAYS been willing to
    /// credential: an <c>https://github.com/</c> URL (case-insensitive prefix). This is the SAME rule
    /// the removed token-injection helper applied, kept verbatim so the set of destinations that can
    /// receive a credential does not change — only the channel does.
    /// </summary>
    private static bool IsCredentialEligible(string url) =>
        !string.IsNullOrEmpty(url)
        && url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the CREDENTIAL-FREE form of <paramref name="url"/>: any userinfo is removed and the
    /// repository identity — scheme, host, port, path, query and fragment — is preserved verbatim.
    /// </summary>
    /// <remarks>
    /// This is the ONLY origin form an authenticated operation may persist or hand to git, so no
    /// host-resolved credential ever reaches <c>.git/config</c> or an argument list.
    /// </remarks>
    private static string SanitizeOriginUrl(string url)
    {
        var uri = new Uri(url.Trim(), UriKind.Absolute);
        var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        return $"{uri.Scheme}://{authority}{uri.PathAndQuery}{uri.Fragment}";
    }

    /// <summary>
    /// Loads and parses hive-config.yaml from the config repo root.
    /// <para>
    /// <b>Cache ownership.</b> The parsed config is cached privately; every call — cache hit
    /// or miss — returns a DETACHED deep copy of the cached graph (via
    /// <see cref="HiveConfigFile.CaptureConfigSnapshot"/>), so a caller mutating a returned
    /// instance can never corrupt the cache, and <see cref="IsRepositoryAllowed"/> membership
    /// stays stable until the cache is explicitly replaced. Disk is read ONLY on a cache miss;
    /// a cache hit never reparses the YAML. Runtime nulls (null/missing sections) survive the
    /// copy, and <see cref="HiveConfigFile.IsConfigured"/> is carried over explicitly.
    /// </para>
    /// <para>
    /// <b>Serialization.</b> The WHOLE operation — the cache check included — runs under the
    /// manager's <c>_gitLock</c>, the same semaphore <see cref="SyncRepoAsync"/> holds. A load
    /// queued behind a running sync therefore observes the POST-sync cache state (i.e. the
    /// invalidation), never a pre-sync cached generation, and can never read
    /// <c>hive-config.yaml</c> while another operation rewrites it. The lock is acquired BEFORE
    /// any cache read, file access or YAML parse; a cancelled/failed acquisition releases
    /// nothing. No other lock-taking manager entry point is called from inside the region.
    /// </para>
    /// </summary>
    public async Task<HiveConfigFile> LoadConfigAsync(CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            // One local capture: field reads can straddle invalidation by SyncRepoAsync.
            var cached = _cachedConfig;
            if (cached is null)
            {
                var configPath = Path.Combine(_localPath, "hive-config.yaml");
                if (!File.Exists(configPath))
                    throw new FileNotFoundException("Config file not found in config repo.", configPath);

                var yaml = await File.ReadAllTextAsync(configPath, ct);
                cached = ParseConfig(yaml);
                _cachedConfig = cached;
            }

            return MaterializeDetached(cached.CaptureConfigSnapshot(), cached.IsConfigured);
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Materializes a detached <see cref="HiveConfigFile"/> from an ALREADY-DETACHED
    /// <see cref="HiveConfigSnapshot"/> by transferring the snapshot's top-level properties and
    /// setting <see cref="HiveConfigFile.IsConfigured"/> explicitly from the caller's value
    /// (the snapshot deliberately excludes it). This is the single materialization helper for
    /// both cache-fill paths — no second cloner, no serialization round-trip.
    /// </summary>
    private static HiveConfigFile MaterializeDetached(HiveConfigSnapshot snapshot, bool isConfigured) => new()
    {
        Version = snapshot.Version!,
        Repositories = snapshot.Repositories!,
        Workers = snapshot.Workers!,
        Orchestrator = snapshot.Orchestrator!,
        Models = snapshot.Models,
        Composer = snapshot.Composer,
        IsConfigured = isConfigured
    };

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
    /// <para>
    /// The YAML is produced from a DETACHED snapshot taken via
    /// <see cref="HiveConfigFile.CaptureConfigSnapshot"/> — never from the live instance — so
    /// synchronized catalog mutations and <see cref="HiveConfigFile.ReloadFrom"/> that interleave
    /// with the serializer's traversal cannot tear the written file. The snapshot is coherent
    /// relative to participating synchronized writers only.
    /// </para>
    /// <para>
    /// <b>Cache ownership.</b> That SAME single snapshot is reused as the post-write cache
    /// (materialized detached, with <see cref="HiveConfigFile.IsConfigured"/> carried over
    /// explicitly — the snapshot excludes it), so the cache never aliases the caller's mutable
    /// object graph and the serialized bytes and cached values cannot diverge. The new graph is
    /// built BEFORE publication; the cache field is replaced only AFTER the file write
    /// succeeds — a failed or cancelled write never replaces an existing cache, and the
    /// live config is never re-read after the await.
    /// </para>
    /// <para>
    /// <b>Serialization.</b> The WHOLE operation runs under the manager's <c>_gitLock</c>, the
    /// same semaphore <see cref="SyncRepoAsync"/> holds. The snapshot is captured only AFTER the
    /// permit is acquired, so a write queued behind a sync serializes the caller's state as of
    /// the moment it actually owns the lock — never a pre-lock generation — and its file write
    /// and cache publication are ordered after the sync's invalidation. A cancelled or failed
    /// acquisition performs no snapshot, no file access and releases nothing.
    /// </para>
    /// <para>
    /// <b>Scope.</b> This is a single git/file call: it takes ONLY <c>_gitLock</c>. The
    /// whole-operation serialization (the config read-modify-write-commit transaction and the
    /// dispatcher's config-repo sync/reload) is the caller's <see cref="ConfigOperationLock"/>,
    /// which is acquired BEFORE this method is entered and is never taken here. Lock order is
    /// <see cref="ConfigOperationLock"/> → <c>_gitLock</c> → the short-lived
    /// <see cref="HiveConfigFile"/> catalog monitor, which the synchronous
    /// <see cref="HiveConfigFile.CaptureConfigSnapshot"/> takes and drops without any await; no
    /// other lock-taking manager entry point is called from inside the region.
    /// </para>
    /// </summary>
    /// <param name="config">The updated configuration to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteConfigAsync(HiveConfigFile config, CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            // ONE snapshot: serialized to YAML AND used as the post-write cache — the written
            // bytes and the cached values are the same detached generation by construction.
            // IsConfigured is not part of the snapshot; it is carried explicitly from the caller.
            var snapshot = config.CaptureConfigSnapshot();
            var cached = MaterializeDetached(snapshot, config.IsConfigured);

            var yaml = YamlSerializer.Serialize(snapshot);
            var configPath = Path.Combine(_localPath, "hive-config.yaml");
            await File.WriteAllTextAsync(configPath, yaml, ct);

            // Publish only after the write succeeded: a failed or cancelled write must never
            // replace an existing cache.
            _cachedConfig = cached;
        }
        finally
        {
            _gitLock.Release();
        }
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
    /// <para>
    /// The cache is read into ONE local reference — a mid-method invalidation by
    /// <see cref="SyncRepoAsync"/>, <see cref="ResetToRemoteAsync"/> or the push recovery path
    /// cannot straddle the null check and the membership scan.
    /// Because the cache never aliases a caller-owned graph (all publications are detached
    /// copies), mutating a previously returned config cannot silently change cached membership.
    /// </para>
    /// </summary>
    public bool IsRepositoryAllowed(string repoUrl)
    {
        var cached = _cachedConfig;
        if (cached is null)
            return false;

        var normalized = NormalizeUrl(repoUrl);
        return cached.Repositories.Exists(r => NormalizeUrl(r.Url) == normalized);
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
            await RunGitAsync(_localPath, ["add", filePath], auth: null, ct);
            var exitCode = await RunGitOptionalAsync(_localPath, ["diff", "--cached", "--quiet"], auth: null, ct);
            if (exitCode == 0)
            {
                // No diff: the push IS this path's first network command.
                using var pushAuth = await EnsureOriginCredentialAsync(ct);
                await PushOnlyAsync(pushAuth, ct);
                return;
            }
            // commit is LOCAL too — the refresh waits until after it.
            await RunGitAsync(_localPath, ["commit", "-m", commitMessage], auth: null, ct);
            using var auth = await EnsureOriginCredentialAsync(ct);
            await PushWithConflictRecoveryAsync(auth, ct);
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
            await RunGitAsync(_localPath, ["rm", "--cached", "--ignore-unmatch", filePath], auth: null, ct);
            var exitCode = await RunGitOptionalAsync(_localPath, ["diff", "--cached", "--quiet"], auth: null, ct);
            if (exitCode == 0)
            {
                // No diff: the push IS this path's first network command.
                using var pushAuth = await EnsureOriginCredentialAsync(ct);
                await PushOnlyAsync(pushAuth, ct);
                return;
            }
            // commit is LOCAL too — the refresh waits until after it.
            await RunGitAsync(_localPath, ["commit", "-m", commitMessage], auth: null, ct);
            using var auth = await EnsureOriginCredentialAsync(ct);
            await PushWithConflictRecoveryAsync(auth, ct);
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
            await RunGitAsync(_localPath, args, auth: null, ct);
            var exitCode = await RunGitOptionalAsync(_localPath, ["diff", "--cached", "--quiet"], auth: null, ct);
            if (exitCode == 0)
            {
                // No diff: the push IS this path's first network command.
                using var pushAuth = await EnsureOriginCredentialAsync(ct);
                await PushOnlyAsync(pushAuth, ct);
                return;
            }
            // commit is LOCAL too — the refresh waits until after it.
            await RunGitAsync(_localPath, ["commit", "-m", commitMessage], auth: null, ct);
            using var auth = await EnsureOriginCredentialAsync(ct);
            await PushWithConflictRecoveryAsync(auth, ct);
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
            await RunGitAsync(_localPath, ["add", "--all"], auth: null, ct);
            var exitCode = await RunGitOptionalAsync(_localPath, ["diff", "--cached", "--quiet"], auth: null, ct);
            if (exitCode == 0)
                return;
            // commit is LOCAL too — the refresh waits until after it, immediately before the
            // pull inside PushWithConflictRecoveryAsync (this path's first network command).
            await RunGitAsync(_localPath, ["commit", "-m", commitMessage], auth: null, ct);
            using var auth = await EnsureOriginCredentialAsync(ct);
            await PushWithConflictRecoveryAsync(auth, ct);
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
    /// <param name="auth">The operation's already-resolved transport (never re-resolved here).</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task PushOnlyAsync(BrainGitAuth? auth, CancellationToken ct)
    {
        await RunGitAsync(_localPath, ["push"], auth, ct);
    }

    /// <summary>
    /// Pulls the latest changes and pushes the local commit, recovering from merge conflicts.
    /// If a plain pull fails (likely a conflict), the merge is aborted, the working tree is
    /// reset to the local commit, and a rebase pull is attempted. If the rebase also fails,
    /// the rebase is aborted, the tree is reset hard, and the local commit is pushed as-is.
    /// </summary>
    /// <param name="auth">
    /// The operation's already-resolved transport. The recovery path REUSES it — it never
    /// resolves the chain a second time.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// <b>Cache invalidation.</b> <c>_cachedConfig</c> is cleared at method ENTRY, before the
    /// initial pull: the pull may rewrite <c>hive-config.yaml</c> (e.g. fast-forward to a
    /// remote change), so every subsequent <see cref="LoadConfigAsync"/> must reread disk.
    /// The invalidation is sticky — it stays null on success, on partial recovery failure,
    /// and on caller cancellation after entry; the old cached graph is never restored. All
    /// callers (<see cref="CommitFileAsync"/>, <see cref="DeleteFileAsync"/>,
    /// <see cref="DeleteFilesAsync"/>, <see cref="CommitAllChangesAsync"/>) already hold
    /// <c>_gitLock</c>, so the field write is synchronized without any new locking.
    /// </remarks>
    /// <remarks>
    /// Neither catch may classify a CALLER cancellation as a merge/rebase conflict: an
    /// <see cref="OperationCanceledException"/> raised while <paramref name="ct"/> is cancelled
    /// propagates immediately, so no recovery command and no push runs after it.
    /// </remarks>
    private async Task PushWithConflictRecoveryAsync(BrainGitAuth? auth, CancellationToken ct)
    {
        // The pull below can rewrite hive-config.yaml (or leave the tree changed on a
        // recovery path). Invalidate up front and keep it invalidated — even a failure that
        // happens not to change YAML must never resurrect the stale cached graph.
        _cachedConfig = null;
        try
        {
            await RunGitAsync(_localPath, ["pull"], auth, ct);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw;

            // Pull failed — likely a merge conflict. Recover by aborting the merge,
            // resetting to the local commit, and retrying with rebase.
            await TryAbortMergeAsync(_localPath, auth, ct);
            await RunGitAsync(_localPath, ["reset", "--hard", "HEAD"], auth, ct);
            try
            {
                await RunGitAsync(_localPath, ["pull", "--rebase"], auth, ct);
            }
            catch (Exception rebaseEx)
            {
                if (rebaseEx is OperationCanceledException && ct.IsCancellationRequested)
                    throw;

                // Rebase also failed — abort rebase, reset hard, and push local commit as-is
                await RunGitAsync(_localPath, ["rebase", "--abort"], auth, ct);
                await RunGitAsync(_localPath, ["reset", "--hard", "HEAD"], auth, ct);
            }
        }
        await RunGitAsync(_localPath, ["push"], auth, ct);
    }

    /// <summary>
    /// Resets the local config repo clone to match the remote, discarding any local changes.
    /// Used for recovery when the repo is stuck in a conflicted state.
    /// <para>
    /// <b>Cache invalidation.</b> <c>_cachedConfig</c> is cleared FIRST — before the merge
    /// abort — under the manager's <c>_gitLock</c>: even the best-effort abort may alter the
    /// working tree before any later credential/fetch/reset step runs, so every subsequent
    /// <see cref="LoadConfigAsync"/> must reread disk. The invalidation is sticky: a partial
    /// failure or caller cancellation after this point never restores the old cached graph.
    /// A failed or cancelled lock acquisition touches nothing and releases nothing.
    /// </para>
    /// </summary>
    public async Task ResetToRemoteAsync(CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        // The operation's authenticated transport, disposed by the finally below on EVERY exit path
        // (success, failure and cancellation) so its temporary helper directory cannot leak.
        BrainGitAuth? auth = null;
        try
        {
            // Reset rewrites hive-config.yaml (or leaves the working tree changed). Clear the
            // cache BEFORE the abort: even the abort may change the working tree, so any
            // later failure must still leave the stale graph invalidated (never restored).
            _cachedConfig = null;
            // merge --abort is local and best-effort; the fetch is the first network command,
            // so the origin refresh runs immediately before it.
            await TryAbortMergeAsync(_localPath, auth: null, ct);
            auth = await EnsureOriginCredentialAsync(ct);
            await RunGitAsync(_localPath, ["fetch", "origin"], auth, ct);
            await RunGitAsync(_localPath, ["reset", "--hard", "origin/HEAD"], auth, ct);
        }
        finally
        {
            auth?.Dispose();
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
    /// <remarks>
    /// <para>
    /// <b>The operation-scoped credential transport.</b> When <paramref name="auth"/> is non-null the
    /// child is launched with the COMPLETE operation environment
    /// (<see cref="HostGitAskpassHelper.BuildChildEnvironment"/>): inherited credential helpers
    /// disabled, persistent credential storage disabled, every inherited token variable scrubbed, and
    /// the credential supplied only through the token-free askpass script and a child-only variable.
    /// With <paramref name="auth"/> <c>null</c> (local-only commands, operations that resolved no
    /// credential, and NONELIGIBLE destinations) the child inherits the parent environment exactly as
    /// before.
    /// </para>
    /// <para>
    /// <b>Cancellation.</b> <c>ReadToEndAsync(ct)</c>/<c>WaitForExitAsync(ct)</c> observe the token
    /// but do NOT terminate the child process, so a cancelled caller would otherwise return while
    /// git is still mutating the clone (and would release the caller's lock on a repo that is still
    /// being written). A caller cancellation (<see cref="OperationCanceledException"/> raised while
    /// <paramref name="ct"/> is cancelled) therefore performs the SAME best-effort termination every
    /// real-process git runner in the orchestrator performs — via the ONE shared
    /// <see cref="GitProcessTermination.TerminateTreeBestEffort"/> helper, a guarded
    /// <c>Kill(entireProcessTree: true)</c> followed by a bounded <c>WaitForExit(5000)</c> — and then
    /// always rethrows the ORIGINAL cancellation. Nothing here can block indefinitely, and failing to
    /// confirm termination never suppresses the cancellation.
    /// </para>
    /// </remarks>
    private async Task<GitRunResult> RunGitCoreAsync(
        string workingDir, string[] args, CancellationToken ct, BrainGitAuth? auth = null)
    {
        // Built BEFORE either launch path so the process path and the observation seam see the
        // identical dictionary. null means "inherit the parent environment unchanged".
        var childEnvironment = auth?.BuildChildEnvironment();

        // The observation seam fires for EVERY launch, including the credential-free ones, so a test
        // can prove a noneligible destination carries NO environment at all.
        GitLaunchObserver?.Invoke(workingDir, args, childEnvironment);

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

        if (childEnvironment is not null)
            ApplyChildEnvironment(psi, childEnvironment);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // The caller's token was canceled while the git process was still running. Terminate the
            // whole process tree (bounded, best-effort — see the shared helper) BEFORE rethrowing so
            // a cancelled config-repo operation never leaves a git process mutating the clone after
            // the caller has been released.
            GitProcessTermination.TerminateTreeBestEffort(process);

            throw; // Always rethrow the OperationCanceledException.
        }

        return new GitRunResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    /// <summary>
    /// Replaces the child's inherited environment with <paramref name="environment"/>, which is the
    /// COMPLETE operation environment, so no inherited entry survives by omission. A <c>null</c> value
    /// is skipped: an environment block cannot represent one, and skipping is exactly the "absent
    /// variable" semantics the builder's scrubbing relies on.
    /// </summary>
    private static void ApplyChildEnvironment(
        ProcessStartInfo psi, IReadOnlyDictionary<string, string?> environment)
    {
        psi.Environment.Clear();
        foreach (var (key, value) in environment)
        {
            if (value is not null)
                psi.Environment[key] = value;
        }
    }

    /// <summary>
    /// Test-only entry point that drives the real core runner (the <see cref="GitRunner"/> seam is
    /// left <c>null</c> by the caller) so cancellation and process termination can be observed
    /// against a REAL git process.
    /// </summary>
    /// <param name="workingDir">Working directory for the git process.</param>
    /// <param name="args">Arguments to pass to git.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="auth">
    /// Optional operation transport. When supplied the REAL child receives the operation environment,
    /// which is how a test proves the environment is installed on the actual process rather than only
    /// reported to a seam.
    /// </param>
    internal Task<GitRunResult> RunGitForTestAsync(
        string workingDir, string[] args, CancellationToken ct, BrainGitAuth? auth = null) =>
        RunGitCoreAsync(workingDir, args, ct, auth);

    /// <summary>
    /// Runs the core and applies the shared failure translation: a caller cancellation
    /// propagates UNREDACTED (its message carries no git output by construction); any other
    /// exception is wrapped + sanitized with no inner exception.
    /// </summary>
    private async Task<GitRunResult> RunGitGuardedAsync(
        string workingDir, string[] args, BrainGitAuth? auth, CancellationToken ct)
    {
        try
        {
            return await RunGitCoreAsync(workingDir, args, ct, auth);
        }
        catch (Exception ex) when (ex is OperationCanceledException && ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw WrapGitException(ex, auth?.Credential);
        }
    }

    /// <summary>
    /// Runs git tolerating exit code 1 (the "difference found" convention of
    /// <c>diff --quiet</c>); an exit code greater than 1 throws a sanitized exception.
    /// </summary>
    private async Task<int> RunGitOptionalAsync(string workingDir, string[] args, BrainGitAuth? auth, CancellationToken ct)
    {
        var result = await RunGitGuardedAsync(workingDir, args, auth, ct);

        if (result.ExitCode > 1)
            throw BuildGitFailure(result, auth?.Credential);

        return result.ExitCode;
    }

    /// <summary>
    /// Runs git and returns trimmed stdout; a non-zero exit throws a sanitized exception.
    /// </summary>
    private async Task<string> RunGitAsync(string workingDir, string[] args, BrainGitAuth? auth, CancellationToken ct)
    {
        var result = await RunGitGuardedAsync(workingDir, args, auth, ct);

        if (result.ExitCode != 0)
            throw BuildGitFailure(result, auth?.Credential);

        return result.Stdout.Trim();
    }
}

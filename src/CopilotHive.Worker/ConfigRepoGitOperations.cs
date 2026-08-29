using System.Globalization;
using System.Security;
using CopilotHive.Configuration;
using CopilotHive.Services;

namespace CopilotHive.Worker;

/// <summary>
/// Validation and EXECUTION layer for the config-repo git seam (slices 2c-b1b-i, 2c-b1b-ii,
/// 2c-b1c-i and 2c-b1c-ii): strict per-command grammar, the Stage 6a URL resolution / transport
/// eligibility, ref prechecks PLUS the check-ref-format subprocess (Stage 6b), worktree
/// containment, canonicalization, constructors, disposal, the real process execution via the
/// SHARED <see cref="GitOperations.ExecuteProcessAsync"/> with the concrete result mapping and
/// redaction boundary, AND — for ELIGIBLE (Branch A) transport operations — the serialized
/// origin state machine (Stages 6c/6d), the credential + helper resolution (Stage 6e) and the
/// Stage 7 credential env injection with the literal-secret redaction pass. The health probe
/// (2c-b2) and the clone (2c-b3) are later slices.
/// </summary>
internal sealed class ConfigRepoGitOperations : IDisposable
{
    /// <summary>Stage 6d — the origin could not be INSPECTED (or the inspection failed to launch).</summary>
    private const string OriginNotVerified = "Config repo origin could not be verified.";

    /// <summary>Stage 6d — <c>git remote add origin</c> failed.</summary>
    private const string OriginNotAdded = "Config repo origin could not be added.";

    /// <summary>Stage 6d — <c>git remote set-url origin</c> failed.</summary>
    private const string OriginNotUpdated = "Config repo origin could not be updated.";

    /// <summary>Stage 6d — the PRESENT origin is neither equivalent nor safely repairable.</summary>
    private const string OriginMismatch = "Config repo origin does not match the configured repository.";

    /// <summary>The FIXED message for any non-cancellation resolver failure (URL or credential).</summary>
    private const string NotProvisioned = "Config repo not provisioned.";

    /// <summary>Stage 6e — the credential helper path is missing or its delegate failed.</summary>
    private const string HelperUnavailable = "Git credential helper path is not available.";

    /// <summary>The env variable carrying the config-repo credential to the FINAL command.</summary>
    private const string CredentialEnvName = "GITHUB_CONFIG_REPO_TOKEN";

    /// <summary>The env variable pointing git at the non-interactive credential helper.</summary>
    private const string AskpassEnvName = "GIT_ASKPASS";

    /// <summary>The literal-redaction replacement for an ordinal credential occurrence.</summary>
    private const string RedactedPlaceholder = "[redacted]";

    private readonly string _configRepoDirCanonical;
    private readonly Func<string?> _resolvedUrlResolver;
    private readonly Func<string?> _credentialResolver;
    private readonly WorkerLogger _log;
    private readonly Func<string> _credentialHelperPath;
    private readonly Action _onDispose;
    private readonly Func<string, string> _pathCanonicalizer;

    /// <summary>
    /// Stage 6c — the PER-INSTANCE serialization lock covering Stage 6c through the completion
    /// of Stage 7 for ELIGIBLE (Branch A) operations only. Branch B and local commands NEVER
    /// take it.
    /// </summary>
    /// <remarks>
    /// It is INTENTIONALLY never disposed by <see cref="Dispose"/>: the seam's disposal
    /// contract is that in-flight operations complete normally, and disposing the semaphore
    /// underneath them would fault a waiter or a release. Letting the finalizer-free
    /// <see cref="SemaphoreSlim"/> be garbage-collected with the instance is the correct
    /// trade-off here.
    /// </remarks>
    private readonly SemaphoreSlim _originGate = new(1, 1);

    /// <summary>0 = not disposed; 1 = disposed. Guarded by <see cref="Interlocked"/>.</summary>
    private int _disposed;

    /// <summary>
    /// Production constructor. Delegates to the testing constructor with resolvers derived
    /// from the provisioner. The delegates are never invoked by this slice.
    /// </summary>
    internal ConfigRepoGitOperations(
        string configRepoDir,
        WorkerConfigProvisioner provisioner,
        WorkerLogger log,
        Func<string> credentialHelperPath,
        Action onDispose)
        : this(
            configRepoDir,
            () => provisioner.ResolvedConfigRepoUrl,
            () => provisioner.ResolveConfigRepoCredential(),
            log,
            credentialHelperPath,
            onDispose)
    {
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(credentialHelperPath);
        ArgumentNullException.ThrowIfNull(onDispose);
    }

    /// <summary>
    /// Testing constructor — the real implementation. <paramref name="pathCanonicalizer"/>
    /// defaults to <see cref="Path.GetFullPath(string)"/>.
    /// </summary>
    internal ConfigRepoGitOperations(
        string configRepoDir,
        Func<string?> resolvedUrlResolver,
        Func<string?> credentialResolver,
        WorkerLogger log,
        Func<string> credentialHelperPath,
        Action onDispose,
        Func<string, string>? pathCanonicalizer = null)
    {
        if (string.IsNullOrWhiteSpace(configRepoDir))
            throw new ArgumentException("Config repo directory must not be null or whitespace.", nameof(configRepoDir));

        if (!Path.IsPathFullyQualified(configRepoDir))
            throw new ArgumentException("Config repo directory must be a fully qualified path.", nameof(configRepoDir));

        ArgumentNullException.ThrowIfNull(resolvedUrlResolver);
        ArgumentNullException.ThrowIfNull(credentialResolver);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(credentialHelperPath);
        ArgumentNullException.ThrowIfNull(onDispose);

        _resolvedUrlResolver = resolvedUrlResolver;
        _credentialResolver = credentialResolver;
        _log = log;
        _credentialHelperPath = credentialHelperPath;
        _onDispose = onDispose;
        _pathCanonicalizer = pathCanonicalizer ?? Path.GetFullPath;

        try
        {
            _configRepoDirCanonical = Canonicalize(configRepoDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException or IOException)
        {
            throw new ArgumentException("Config repo directory could not be canonicalized.", nameof(configRepoDir));
        }
    }

    /// <summary>
    /// Runs the strict validation pipeline for a config-repo git command and, when every
    /// stage passes, executes the SNAPSHOTTED command via the shared
    /// <see cref="GitOperations.ExecuteProcessAsync"/>. Stage 6b additionally validates the
    /// ref candidate with a <c>git check-ref-format --allow-onelevel &lt;ref&gt;</c>
    /// subprocess, and an ELIGIBLE (Branch A) transport command then runs — serialized on the
    /// per-instance Stage 6c gate — the Stage 6d origin state machine and the Stage 6e
    /// credential/helper resolution before its final, credential-carrying launch.
    /// </summary>
    internal async Task<ConfigRepoOpResult> RunConfigRepoCommandAsync(
        IReadOnlyList<string> args, string workingDirectory, CancellationToken ct)
    {
        // Stage 1 — disposed.
        if (Volatile.Read(ref _disposed) != 0)
            return Reject("Seam disposed.");

        // Stage 2a — args null or empty (BEFORE the snapshot).
        if (args is null || args.Count == 0)
            return Reject("Invalid arguments.");

        // Stage 2b — workingDirectory null/whitespace.
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return Reject("Invalid arguments.");

        // Stage 2c — the snapshot, taken ONCE and used for everything downstream.
        var snapshot = args.ToArray();

        // Stage 2d — a null element over the snapshot.
        foreach (var arg in snapshot)
        {
            if (arg is null)
                return Reject("Invalid arguments.");
        }

        // Stage 2e — a path-related exception from the containment's Canonicalize.
        string canonicalWorkingDir;
        try
        {
            canonicalWorkingDir = Canonicalize(workingDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException or IOException)
        {
            return Reject("Invalid arguments.");
        }

        // Stage 3 — containment: the working directory must EQUAL the config repo directory.
        if (!IsContained(canonicalWorkingDir))
            return Reject("Invalid git command: the working directory is not the config repository.");

        // Stage 4 — subcommand recognition.
        var subcommand = snapshot[0];
        if (!IsKnownSubcommand(subcommand))
            return Reject($"Invalid git command: unknown subcommand '{subcommand}'.");

        // Stage 5 — the COMPLETE grammar scan. It classifies every token and, for the
        // credential-scoped commands, SELECTS (but does not yet validate) the ref candidate.
        // Every grammar rejection — misplacement, unknown option, duplicate, conflict,
        // --depth, positional count, remote identity, push arity, credential-free form
        // matching — is decided here, BEFORE Stage 6 ever looks at the ref.
        var (scanError, refCandidate, hasPositionals) = ScanTokens(subcommand, snapshot);
        if (scanError is not null)
            return Reject(scanError);

        // Stage 6a — URL resolution (slice 2c-b1c-i), for the TRANSPORT commands
        // (pull/push/fetch) ONLY. Local commands (checkout/add/diff/commit/merge --abort/
        // status) skip this stage entirely: the URL resolver is NEVER read for them, and they
        // go straight to Stage 7 with the scrubbed env. The resolver is read EXACTLY ONCE.
        var eligibleTransport = false;
        string? sanitizedUrl = null;
        if (IsTransportSubcommand(subcommand))
        {
            var (urlError, eligible, sanitized) = ResolveTransportEligibility();
            if (urlError is not null)
                return Reject(urlError);

            eligibleTransport = eligible;
            sanitizedUrl = sanitized;
        }

        // Stage 6b — ref validation: the PRECHECKS, then (when they pass) the check-ref-format
        // subprocess. Run only once the Stage 5 scan completed without a grammar rejection.
        // This ordering is what makes `pull origin +bad extra` report `too many arguments.`
        // and `pull badremote +bad` report the remote rejection: a Stage 5 error ALWAYS wins
        // over a Stage 6 ref error.
        if (refCandidate is not null)
        {
            var refError = ValidateRef(refCandidate);
            if (refError is not null)
                return Reject(refError);

            // The prechecks passed — confirm with the subprocess. The ref-validation
            // subprocess is ALWAYS credential-free: only the scrubbed inherited environment
            // plus GIT_TERMINAL_PROMPT=0. A non-zero exit rejects the ref.
            var refValidation = await LaunchGitProcessAsync(
                new[] { "check-ref-format", "--allow-onelevel", refCandidate }, ct);
            if (refValidation is null)
                return Reject("Git process failed to start.");

            if (refValidation.ExitCode != 0)
                return Reject($"Invalid git ref: '{GitUrlRedactor.Redact(refCandidate)}'.");
        }

        // The Stage 7 launch arguments.
        //
        // CANONICALIZATION (slice 2c-b1c-i): an ELIGIBLE pull/fetch whose validated form
        // carries NO positionals gets the literal `origin` appended as the remote argument,
        // so the command always targets the explicit origin remote rather than whatever
        // upstream tracking configuration happens to exist. Every other launch — a form that
        // already has positionals (the grammar guarantees its first positional is exactly
        // `origin`), EVERY push form, and every Branch B (ineligible transport) command —
        // launches the SNAPSHOT verbatim.
        string[] launchArgs = ShouldAppendExplicitOrigin(subcommand, eligibleTransport, hasPositionals)
            ? [.. snapshot, "origin"]
            : snapshot;

        // Branch B (and every local command) — Stage 7 directly, with the scrubbed env plus
        // GIT_TERMINAL_PROMPT=0 and NO credential environment. No origin state machine, no
        // credential resolution, and NO serialization lock.
        if (!eligibleTransport)
        {
            var execution = await LaunchGitProcessAsync(launchArgs, ct);
            if (execution is null)
                return Reject("Git process failed to start.");

            return MapResult(execution);
        }

        // Branch A — Stages 6c → 6d → 6e → 7, serialized on the per-instance gate.
        return await RunEligibleOperationAsync(launchArgs, sanitizedUrl!, ct);
    }

    /// <summary>
    /// Branch A (an ELIGIBLE transport operation): Stage 6c (the per-instance serialization
    /// gate), Stage 6d (the origin state machine), Stage 6e (the credential + helper
    /// resolution) and Stage 7 (the final launch with the credential env injection and the
    /// literal-secret redaction pass).
    /// </summary>
    /// <remarks>
    /// THE ACQUIRED-FLAG RULE: <see cref="SemaphoreSlim.Release()"/> runs in the finally ONLY
    /// when <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/> actually completed —
    /// a cancellation BEFORE acquisition propagates without releasing a semaphore this call
    /// never owned; a cancellation AFTER acquisition releases normally.
    /// </remarks>
    private async Task<ConfigRepoOpResult> RunEligibleOperationAsync(
        string[] launchArgs, string sanitizedUrl, CancellationToken ct)
    {
        var acquired = false;
        string? credential = null;
        try
        {
            // Stage 6c.
            await _originGate.WaitAsync(ct);
            acquired = true;

            // Stage 6d — the origin must be PRESENT, credential-free and equivalent to the
            // sanitized configured URL before any credential is ever resolved.
            var originError = await EnsureOriginAsync(sanitizedUrl, ct);
            if (originError is not null)
                return Reject(originError);

            // Stage 6e — read the credential ONCE and (only when it will be injected) the
            // credential helper path.
            var (credentialError, resolvedCredential, helperPath) = ResolveCredential();
            credential = resolvedCredential;
            if (credentialError is not null)
                return RedactLiteralCredential(Reject(credentialError), credential);

            // Stage 7 — the FINAL command; the only launch that ever carries the credential.
            var execution = await LaunchGitProcessAsync(launchArgs, ct, credential, helperPath);
            if (execution is null)
                return RedactLiteralCredential(Reject("Git process failed to start."), credential);

            return RedactLiteralCredential(MapResult(execution), credential);
        }
        finally
        {
            if (acquired)
                _originGate.Release();
        }
    }

    /// <summary>
    /// Stage 6d — the origin state machine. Inspects <c>git remote get-url origin</c> and,
    /// depending on the outcome, ADDS an absent origin, REPAIRS a credential-bearing but
    /// otherwise equivalent origin with <c>git remote set-url origin</c>, leaves an equivalent
    /// credential-free origin untouched, or REJECTS. Every subprocess here is credential-free.
    /// </summary>
    /// <returns>The fixed rejection message, or <c>null</c> when the origin is verified.</returns>
    private async Task<string?> EnsureOriginAsync(string sanitizedUrl, CancellationToken ct)
    {
        // Step 3a — inspection.
        var inspection = await LaunchGitProcessAsync(
            new[] { "remote", "get-url", "origin" }, ct);
        if (inspection is null)
            return OriginNotVerified;

        string? origin = null;
        if (inspection.ExitCode == 0)
        {
            var trimmed = (inspection.Stdout ?? string.Empty).Trim();
            if (trimmed.Length != 0)
                origin = trimmed;
        }
        else if (!IsAbsentOriginStderr(inspection.Stderr))
        {
            return OriginNotVerified;
        }

        // Step 3b — an ABSENT origin is ADDED with the sanitized URL.
        if (origin is null)
        {
            var add = await LaunchGitProcessAsync(
                new[] { "remote", "add", "origin", sanitizedUrl }, ct);
            return add is null || add.ExitCode != 0 ? OriginNotAdded : null;
        }

        // Step 3c — a PRESENT origin: equivalence, then repair-vs-reject.
        if (!IsStructurallyEquivalentOrigin(origin, sanitizedUrl))
            return OriginMismatch;

        // Structurally equivalent AND credential-free: leave it exactly as it is.
        if (!IsCredentialBearing(origin))
            return null;

        var setUrl = await LaunchGitProcessAsync(
            new[] { "remote", "set-url", "origin", sanitizedUrl }, ct);
        return setUrl is null || setUrl.ExitCode != 0 ? OriginNotUpdated : null;
    }

    /// <summary>
    /// The ABSENCE classification for a NON-ZERO <c>git remote get-url origin</c> exit,
    /// decided case-INSENSITIVELY over stderr. Anything else is an inspection FAILURE.
    /// </summary>
    private static bool IsAbsentOriginStderr(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr))
            return false;

        return stderr.Contains("no such remote", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("does not appear to be a git repository", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a URL carries a credential, decided by the SHARED structural redactor: a URL
    /// the redactor rewrites is credential-bearing.
    /// </summary>
    private static bool IsCredentialBearing(string url) =>
        !string.Equals(GitUrlRedactor.Redact(url), url, StringComparison.Ordinal);

    /// <summary>
    /// Structural origin equivalence against the SANITIZED configured URL: HTTPS scheme,
    /// effective port 443, a case-insensitively equal host, NO query and NO fragment, and a
    /// path that matches after the exact RAW normalization (see
    /// <see cref="NormalizePathComponents"/>). It is deliberately independent of the presence
    /// of a credential — the credential decides REPAIR vs LEAVE-AS-IS, never equivalence.
    /// </summary>
    /// <remarks>
    /// <see cref="Uri"/> is consulted ONLY for the validated AUTHORITY facts — scheme, host,
    /// effective port, and query/fragment PRESENCE. It is deliberately NOT used to obtain the
    /// path: <see cref="Uri"/> canonicalizes a path before handing it over (dot-segment
    /// collapse, <c>\</c>→<c>/</c> normalization, and unescaping of unreserved characters),
    /// and NONE of those transformations is among the four permitted normalization steps. An
    /// alias such as <c>/org/other/../config-repo.git</c> would otherwise be accepted as
    /// equivalent to <c>/org/config-repo.git</c> — and, when credential-bearing, silently
    /// "repaired" — even though the RAW components differ. The path therefore comes from
    /// <see cref="ExtractRawPath"/>, which slices the ORIGINAL string.
    /// </remarks>
    private static bool IsStructurallyEquivalentOrigin(string origin, string sanitizedUrl)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
            return false;

        if (!Uri.TryCreate(sanitizedUrl, UriKind.Absolute, out var targetUri))
            return false;

        if (!string.Equals(originUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        if (originUri.Port != 443)
            return false;

        if (!string.Equals(originUri.Host, targetUri.Host, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrEmpty(originUri.Query) || !string.IsNullOrEmpty(originUri.Fragment))
            return false;

        // The path comparison runs over the RAW strings — never over a Uri-canonicalized path.
        var originPath = NormalizePathComponents(origin);
        var targetPath = NormalizePathComponents(sanitizedUrl);
        if (originPath.Count != targetPath.Count)
            return false;

        for (var i = 0; i < originPath.Count; i++)
        {
            // The components are compared as .NET strings, case-SENSITIVELY.
            if (!string.Equals(originPath[i], targetPath[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Slices the RAW path out of a URL STRING: the characters from the first <c>/</c> after
    /// the authority up to (excluding) the first <c>?</c> or <c>#</c>. Nothing is canonicalized,
    /// re-escaped, unescaped, or collapsed — the substring is returned exactly as the caller
    /// wrote it.
    /// </summary>
    /// <returns>
    /// The raw path INCLUDING its leading <c>/</c>, or the empty string when the URL carries
    /// no path at all. Both sides of a comparison go through this method, so the leading
    /// separator contributes the same leading empty component to each.
    /// </returns>
    private static string ExtractRawPath(string url)
    {
        // The authority begins after the scheme delimiter.
        var schemeDelimiter = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeDelimiter < 0)
            return string.Empty;

        var authorityStart = schemeDelimiter + 3;

        // The path begins at the FIRST '/' after the authority. A '?' or '#' encountered
        // first means the URL has no path component.
        var pathStart = -1;
        for (var i = authorityStart; i < url.Length; i++)
        {
            var c = url[i];
            if (c == '/')
            {
                pathStart = i;
                break;
            }

            if (c is '?' or '#')
                return string.Empty;
        }

        if (pathStart < 0)
            return string.Empty;

        // The path ends at the FIRST '?' or '#'.
        var pathEnd = url.Length;
        for (var i = pathStart; i < url.Length; i++)
        {
            if (url[i] is '?' or '#')
            {
                pathEnd = i;
                break;
            }
        }

        return url[pathStart..pathEnd];
    }

    /// <summary>
    /// THE PATH-NORMALIZATION ORDER (exact), applied to the RAW path of
    /// <paramref name="url"/> (see <see cref="ExtractRawPath"/>): (1) split on RAW <c>/</c>;
    /// (2) strip ONE trailing <c>.git</c> from the LAST component, case-SENSITIVELY;
    /// (3) trim ALL trailing <c>/</c> characters from the end of the whole path (i.e. drop
    /// every trailing EMPTY component); (4) single-pass percent-decode each component. The
    /// components are returned separately so a DECODED <c>%2F</c> can never act as a
    /// separator: the split happened on the RAW string, before any decoding.
    /// </summary>
    private static IReadOnlyList<string> NormalizePathComponents(string url)
    {
        // (0) the RAW path — no Uri canonicalization of any kind.
        var path = ExtractRawPath(url);

        // (1) split on the RAW separator.
        var components = new List<string>(path.Split('/'));

        // (2) strip ONE trailing ".git" from the LAST component (lower-case only).
        if (components.Count > 0)
        {
            var last = components[^1];
            if (last.EndsWith(".git", StringComparison.Ordinal))
                components[^1] = last[..^4];
        }

        // (3) trim ALL trailing separators from the whole path.
        while (components.Count > 0 && components[^1].Length == 0)
            components.RemoveAt(components.Count - 1);

        // (4) percent-decode each component, LAST.
        for (var i = 0; i < components.Count; i++)
            components[i] = PercentDecode(components[i]);

        return components;
    }

    /// <summary>
    /// A SINGLE-PASS percent decoder: <c>%xx</c> with two hex digits becomes the corresponding
    /// byte mapped BYTE-TO-CODE-POINT (Latin-1 style — adjacent escapes are never combined
    /// into a UTF-8 sequence); a malformed escape is left exactly as it is; the decoded output
    /// is never re-scanned.
    /// </summary>
    private static string PercentDecode(string component)
    {
        if (!component.Contains('%', StringComparison.Ordinal))
            return component;

        var builder = new System.Text.StringBuilder(component.Length);
        for (var i = 0; i < component.Length; i++)
        {
            if (component[i] == '%'
                && i + 2 < component.Length
                && TryParseHexDigit(component[i + 1], out var high)
                && TryParseHexDigit(component[i + 2], out var low))
            {
                builder.Append((char)((high << 4) | low));
                i += 2;
                continue;
            }

            builder.Append(component[i]);
        }

        return builder.ToString();
    }

    private static bool TryParseHexDigit(char c, out int value)
    {
        if (c is >= '0' and <= '9')
        {
            value = c - '0';
            return true;
        }

        if (c is >= 'a' and <= 'f')
        {
            value = c - 'a' + 10;
            return true;
        }

        if (c is >= 'A' and <= 'F')
        {
            value = c - 'A' + 10;
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Stage 6e — reads the credential resolver EXACTLY ONCE and, only when a NON-WHITESPACE
    /// credential will be injected, the credential helper path.
    /// </summary>
    /// <returns>
    /// <c>(Error, Credential, HelperPath)</c>. A null error with a null credential is the
    /// UNAUTHENTICATED run (a null/whitespace credential); a non-null error still reports the
    /// resolved credential when there was one, so the literal-secret redaction pass applies to
    /// the returned message too.
    /// </returns>
    /// <remarks>
    /// An <see cref="OperationCanceledException"/> from either delegate PROPAGATES
    /// unconditionally. Any other credential-resolver exception maps to the FIXED
    /// <c>Config repo not provisioned.</c>; any other helper-path failure (a throw, or a
    /// null/empty/whitespace path) maps to the FIXED
    /// <c>Git credential helper path is not available.</c>.
    /// </remarks>
    private (string? Error, string? Credential, string? HelperPath) ResolveCredential()
    {
        string? credential;
        try
        {
            credential = _credentialResolver();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return (NotProvisioned, null, null);
        }

        // The credential ABSENCE: the operation runs UNAUTHENTICATED (fail-fast with
        // GIT_TERMINAL_PROMPT=0), and the helper path is NEVER read.
        if (string.IsNullOrWhiteSpace(credential))
            return (null, null, null);

        string? helperPath;
        try
        {
            helperPath = _credentialHelperPath();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return (HelperUnavailable, credential, null);
        }

        if (string.IsNullOrWhiteSpace(helperPath))
            return (HelperUnavailable, credential, null);

        return (null, credential, helperPath);
    }

    /// <summary>
    /// The LITERAL-secret redaction pass, applied AFTER <see cref="GitUrlRedactor.Redact"/>
    /// to every result of an operation for which a NON-WHITESPACE credential was resolved
    /// (whether it was injected or not): every ORDINAL occurrence of the credential in
    /// <see cref="ConfigRepoOpResult.Stdout"/> and
    /// <see cref="ConfigRepoOpResult.SanitizedError"/> becomes <c>[redacted]</c>.
    /// </summary>
    private static ConfigRepoOpResult RedactLiteralCredential(
        ConfigRepoOpResult result, string? credential)
    {
        if (string.IsNullOrWhiteSpace(credential))
            return result;

        return result with
        {
            Stdout = result.Stdout.Replace(credential, RedactedPlaceholder, StringComparison.Ordinal),
            SanitizedError = result.SanitizedError.Replace(credential, RedactedPlaceholder, StringComparison.Ordinal),
        };
    }

    /// <summary>
    /// Fires <c>onDispose</c> exactly once; exceptions from it are swallowed. Post-disposal
    /// calls return <c>Seam disposed.</c>.
    /// </summary>
    /// <remarks>
    /// The Stage 6c semaphore is INTENTIONALLY NOT disposed here — see
    /// <see cref="_originGate"/>. In-flight operations complete normally, and one of them may
    /// still own the gate.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                _onDispose();
            }
            catch
            {
                // Swallowed — onDispose is best-effort cleanup.
            }
        }
    }

    /// <summary>
    /// Canonicalizes a path: fully qualifies it, then trims trailing directory separators
    /// unless the path IS the root.
    /// </summary>
    private string Canonicalize(string path)
    {
        var full = _pathCanonicalizer(path);
        var root = Path.GetPathRoot(full);
        if (string.Equals(full, root, StringComparison.Ordinal))
            return full;
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// The working directory is contained when its canonical form EQUALS the canonical config
    /// repo directory (exact equality, never a prefix/descendant check).
    /// </summary>
    private bool IsContained(string canonicalWorkingDirectory) =>
        canonicalWorkingDirectory.Equals(
            _configRepoDirCanonical,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Builds a rejected result. EVERY SanitizedError passes through
    /// <see cref="GitUrlRedactor.Redact"/> after construction.
    /// </summary>
    private static ConfigRepoOpResult Reject(string message) =>
        new(false, -1, "", GitUrlRedactor.Redact(message));

    /// <summary>
    /// The child environment for a git process launched by this seam: the SCRUBBED inherited
    /// environment (the shared five-variable <see cref="GitOperations.SanitizeChildEnv"/>)
    /// plus <c>GIT_TERMINAL_PROMPT=0</c>, and — ONLY for the FINAL command of an eligible
    /// operation with a NON-WHITESPACE credential — <c>GITHUB_CONFIG_REPO_TOKEN</c> and
    /// <c>GIT_ASKPASS</c>. Those two are the SOLE post-scrub exceptions and are added AFTER
    /// the scrub. Every other launch (the origin inspection/add/set-url and the ref-validation
    /// subprocess) is credential-free.
    /// </summary>
    private IReadOnlyDictionary<string, string?> BuildChildEnv(
        string? credential = null, string? helperPath = null)
    {
        var sanitized = GitOperations.SanitizeChildEnv(SnapshotCurrentProcessEnv());
        var withPromptDisabled = new Dictionary<string, string?>(sanitized);
        withPromptDisabled["GIT_TERMINAL_PROMPT"] = "0";

        if (!string.IsNullOrWhiteSpace(credential) && !string.IsNullOrWhiteSpace(helperPath))
        {
            withPromptDisabled[CredentialEnvName] = credential;
            withPromptDisabled[AskpassEnvName] = helperPath;
        }

        return withPromptDisabled;
    }

    /// <summary>
    /// Snapshots the CURRENT process environment without mutating it, narrowing the
    /// non-generic bound collection down to string keys and <c>string?</c> values.
    /// </summary>
    private static IDictionary<string, string?> SnapshotCurrentProcessEnv()
    {
        var snapshot = new Dictionary<string, string?>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key)
                continue;

            snapshot[key] = entry.Value as string;
        }

        return snapshot;
    }

    /// <summary>
    /// Launches a git process via the SHARED <see cref="GitOperations.ExecuteProcessAsync"/>
    /// with the tokenized form. The working directory is ALWAYS the constructor-canonicalized
    /// <see cref="_configRepoDirCanonical"/>.
    /// </summary>
    /// <returns>
    /// The process result, or <c>null</c> when the process FAILED TO START (any exception
    /// other than <see cref="OperationCanceledException"/> — the exception's own text is never
    /// propagated). <see cref="OperationCanceledException"/> ALWAYS propagates, unconditionally.
    /// </returns>
    private async Task<GitProcessResult?> LaunchGitProcessAsync(
        IReadOnlyList<string> tokenizedArgs,
        CancellationToken ct,
        string? credential = null,
        string? helperPath = null)
    {
        try
        {
            return await GitOperations.ExecuteProcessAsync(
                new GitProcessRequest(
                    "git",
                    Args: Array.Empty<string>(),
                    WorkingDirectory: _configRepoDirCanonical,
                    Env: BuildChildEnv(credential, helperPath),
                    TokenizedArgs: tokenizedArgs),
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A catch-ALL for non-cancellation launch failures — including a throwing
            // ProcessRunner delegate. The exception's own text is NEVER propagated.
            return null;
        }
    }

    /// <summary>
    /// Maps a completed git process result to <see cref="ConfigRepoOpResult"/>. Redaction
    /// FIRST (<see cref="GitUrlRedactor.Redact"/>), then TrimEnd for the error. Exit codes
    /// 0-255 are preserved verbatim; any other value is mapped to -1.
    /// </summary>
    private static ConfigRepoOpResult MapResult(GitProcessResult result)
    {
        var stdout = GitUrlRedactor.Redact(result.Stdout) ?? string.Empty;
        var exitCode = result.ExitCode is >= 0 and <= 255 ? result.ExitCode : -1;

        if (exitCode == 0)
            return new ConfigRepoOpResult(true, 0, stdout, "");

        var stderr = (GitUrlRedactor.Redact(result.Stderr) ?? string.Empty).TrimEnd();
        return new ConfigRepoOpResult(false, exitCode, stdout, stderr);
    }

    private static bool IsKnownSubcommand(string subcommand) =>
        subcommand is "pull" or "push" or "fetch" or "checkout" or "add" or "diff" or "commit" or "merge" or "status";

    /// <summary>
    /// The TRANSPORT (network) subcommands — the only ones that reach Stage 6a. Every other
    /// subcommand is local and NEVER reads the URL resolver.
    /// </summary>
    private static bool IsTransportSubcommand(string subcommand) =>
        subcommand is "pull" or "push" or "fetch";

    /// <summary>
    /// Stage 6a — resolves the config repo URL EXACTLY ONCE and decides transport eligibility.
    /// </summary>
    /// <returns>
    /// <c>(Error, _, _)</c> with a non-null message when the command must be rejected without
    /// ever running; otherwise <c>(null, Eligible, Sanitized)</c> where <c>Eligible</c> selects
    /// the canonicalized explicit-origin launch (Branch A) over the verbatim launch (Branch B)
    /// and <c>Sanitized</c> is the sanitized resolved URL used by the Stage 6d origin state
    /// machine.
    /// </returns>
    /// <remarks>
    /// An <see cref="OperationCanceledException"/> from the resolver PROPAGATES
    /// unconditionally. ANY other resolver exception maps to the FIXED
    /// <c>Config repo not provisioned.</c> message — the resolver's own text (e.g. the
    /// production provisioner's "snapshot absent" <see cref="InvalidOperationException"/>)
    /// NEVER escapes.
    /// </remarks>
    private (string? Error, bool Eligible, string? Sanitized) ResolveTransportEligibility()
    {
        string? resolvedUrl;
        try
        {
            resolvedUrl = _resolvedUrlResolver();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return (NotProvisioned, false, null);
        }

        if (string.IsNullOrWhiteSpace(resolvedUrl))
            return ("Config repo URL is not available.", false, null);

        string? sanitized;
        try
        {
            sanitized = ConfigRepoUrlSanitizer.Sanitize(resolvedUrl);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The sanitizer's messages are already redacted by construction; the returned
            // SanitizedError still passes through GitUrlRedactor.Redact like every other one.
            return ($"Invalid config repo URL: {ex.Message}", false, null);
        }

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            // ConfigRepoUrlSanitizer.Sanitize returns null ONLY for an ABSENT value, which the
            // whitespace check above has already rejected. Reaching this branch would mean the
            // sanitizer broke its contract — report it as an absent URL rather than launching.
            return ("Config repo URL is not available.", false, null);
        }

        return (null, IsEligibleTransportUrl(sanitized), sanitized);
    }

    /// <summary>
    /// Transport eligibility, computed from the SANITIZED URL: an <c>https</c> URL whose host
    /// is <c>github.com</c> (case-insensitively) and whose EFFECTIVE port is 443 — the explicit
    /// port when present, 443 implicitly when absent. An explicit <c>:443</c> IS eligible; an
    /// explicit non-443 port, and every ssh/scp/file/local-path form, is Branch B.
    /// </summary>
    /// <remarks>
    /// The host check is defence in depth: the sanitizer already rejects every https URL whose
    /// host is not <c>github.com</c>, so no sanitized value can reach this method with a
    /// different host. It is kept so eligibility remains self-contained and correct if the
    /// sanitizer's host policy ever widens.
    /// </remarks>
    private static bool IsEligibleTransportUrl(string sanitizedUrl)
    {
        if (!Uri.TryCreate(sanitizedUrl, UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            return false;

        // Uri.Port yields the scheme default (443 for https) when no explicit port is present,
        // which is exactly the "effective port" rule.
        return uri.Port == 443;
    }

    /// <summary>
    /// Whether the launch must INSERT the literal <c>origin</c> remote: only an ELIGIBLE
    /// pull/fetch whose validated form carries NO positionals. push NEVER gets it, Branch B
    /// NEVER gets it, and a form that already has positionals NEVER gets it.
    /// </summary>
    /// <remarks>
    /// The <c>push</c> exclusion is belt-and-braces: the Stage 5 grammar requires
    /// <c>push origin &lt;ref&gt;</c>, so a validated push always has positionals and would be
    /// excluded by <paramref name="hasPositionals"/> alone. It is stated explicitly because
    /// "ALL push forms launch verbatim" is the contract, not an emergent property.
    /// </remarks>
    private static bool ShouldAppendExplicitOrigin(
        string subcommand, bool eligibleTransport, bool hasPositionals) =>
        eligibleTransport && !hasPositionals && subcommand is "pull" or "fetch";

    /// <summary>
    /// Stage 5 — the single left-to-right token scan plus the post-scan structural checks.
    /// Returns the first grammar error message (or <c>null</c>) together with the SELECTED
    /// ref candidate and whether the form carries ANY positional (the remote slot). The ref
    /// candidate is deliberately NOT validated here: Stage 6 runs only after the whole Stage 5
    /// scan completed without a grammar rejection, so a Stage 5 error always wins over a
    /// Stage 6 ref error. <c>HasPositionals</c> feeds the Stage 7 explicit-origin
    /// canonicalization.
    /// </summary>
    private static (string? Error, string? RefCandidate, bool HasPositionals) ScanTokens(
        string subcommand, string[] snapshot) =>
        subcommand switch
        {
            "checkout" or "add" or "diff" or "commit" or "merge" or "status" =>
                (ValidateLocalForm(subcommand, snapshot), null, false),
            "pull" or "push" or "fetch" => ScanCredentialScoped(subcommand, snapshot),
            _ => throw new InvalidOperationException($"Unhandled subcommand '{subcommand}'."),
        };

    /// <summary>
    /// Credential-free local commands accept EXACT token forms only.
    /// </summary>
    private static string? ValidateLocalForm(string subcommand, string[] snapshot)
    {
        var malformed = $"Invalid git command: the arguments do not match the allowed form for '{subcommand}'.";

        switch (subcommand)
        {
            case "checkout":
                return snapshot.Length == 3 && snapshot[1] == "--" && snapshot[2] == "agents/" ? null : malformed;
            case "add":
                return snapshot.Length == 2 && snapshot[1] == "agents/*.agents.md" ? null : malformed;
            case "diff":
                return snapshot.Length == 4 && snapshot[1] == "--cached" && snapshot[2] == "--name-only" && snapshot[3] == "-z" ? null : malformed;
            case "commit":
                return snapshot.Length == 3 && snapshot[1] == "-m" && !string.IsNullOrEmpty(snapshot[2]) ? null : malformed;
            case "merge":
                return snapshot.Length == 2 && snapshot[1] == "--abort" ? null : malformed;
            case "status":
                return snapshot.Length == 1 ? null : malformed;
            default:
                throw new InvalidOperationException($"Unhandled local subcommand '{subcommand}'.");
        }
    }

    /// <summary>
    /// The credential-scoped scan (pull/push/fetch): a single left-to-right pass classifying
    /// every token; the first error wins. Structural validation (remote identity, push arity)
    /// runs after the scan. The selected ref candidate is RETURNED, never validated here —
    /// Stage 6 owns the ref prechecks and runs only after this whole scan succeeded.
    /// <c>HasPositionals</c> reports whether the form filled the remote slot; it selects the
    /// Stage 7 explicit-origin canonicalization.
    /// </summary>
    private static (string? Error, string? RefCandidate, bool HasPositionals) ScanCredentialScoped(
        string subcommand, string[] snapshot)
    {
        var seenOptions = new HashSet<string>(StringComparer.Ordinal);

        // The two positional slots. A dash-prefixed UNKNOWN token appearing after the first
        // positional fills the ref slot exactly like a plain token does.
        string? remote = null;
        var remoteSeen = false;
        string? refSlot = null;
        var refSeen = false;

        for (var i = 1; i < snapshot.Length; i++)
        {
            var token = snapshot[i];

            if (IsKnownOption(subcommand, token))
            {
                // A KNOWN option AFTER the first positional → misplacement (never a ref).
                if (remoteSeen)
                    return ("Invalid git command: options must precede positionals.", null, false);

                if (!seenOptions.Add(token))
                    return ($"Invalid git command: duplicate option '{token}'.", null, false);

                if (IsConflictingOptionPair(token, seenOptions))
                    return ("Invalid git command: --rebase and --no-rebase are mutually exclusive.", null, false);

                if (token == "--depth")
                {
                    if (i + 1 >= snapshot.Length
                        || IsOptionLike(snapshot[i + 1])
                        || !IsValidDepth(snapshot[i + 1]))
                    {
                        return ("Invalid git command: --depth requires a positive integer.", null, false);
                    }

                    i++; // consume the value token
                }

                continue;
            }

            if (IsOptionLike(token))
            {
                // An UNKNOWN dash-prefixed token.
                if (!remoteSeen)
                    return ($"Invalid git command: unknown option '{token}'.", null, false);

                if (refSeen)
                    return ("Invalid git command: too many arguments.", null, false);

                // A ref CANDIDATE — recorded now, validated by Stage 6 after this scan.
                refSlot = token;
                refSeen = true;
                continue;
            }

            // A non-dash token.
            if (!remoteSeen)
            {
                remote = token;
                remoteSeen = true;
                continue;
            }

            if (refSeen)
                return ("Invalid git command: too many arguments.", null, false);

            refSlot = token;
            refSeen = true;
        }

        // Post-scan structural validation — still Stage 5, so it precedes Stage 6.
        if (subcommand == "push")
        {
            // Arity is checked BEFORE remote identity: `push badremote` is an arity failure.
            if (!remoteSeen || !refSeen)
                return ("Invalid git command: push requires 'origin <ref>'.", null, false);

            if (remote != "origin")
                return ("Invalid git command: the remote must be 'origin'.", null, false);
        }
        else if (remoteSeen && remote != "origin")
        {
            return ("Invalid git command: the remote must be 'origin'.", null, false);
        }

        return (null, refSlot, remoteSeen);
    }

    private static bool IsKnownOption(string subcommand, string token) => subcommand switch
    {
        "pull" => token is "--ff-only" or "--no-rebase" or "--rebase" or "--tags" or "--prune" or "--depth",
        "fetch" => token is "--tags" or "--prune" or "--depth",
        "push" => false,
        _ => throw new InvalidOperationException($"Unhandled subcommand '{subcommand}'."),
    };

    private static bool IsConflictingOptionPair(string token, HashSet<string> seenOptions) =>
        (token == "--rebase" && seenOptions.Contains("--no-rebase"))
        || (token == "--no-rebase" && seenOptions.Contains("--rebase"));

    private static bool IsOptionLike(string token) => token.StartsWith('-');

    /// <summary>
    /// The --depth numeric domain: no whitespace/control characters, invariant-culture
    /// <c>int.TryParse</c>, then positive. <c>+5</c> is accepted; <c>-5</c>, <c>0</c>,
    /// whitespace-padded, overflow, hex, and empty are rejected.
    /// </summary>
    private static bool IsValidDepth(string value)
    {
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
                return false;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var depth) && depth > 0;
    }

    /// <summary>
    /// Stage 6 — ref PRECHECKS (the <c>check-ref-format</c> subprocess runs afterwards):
    /// no <c>..</c>/<c>...</c>, no <c>://</c>, no leading <c>-</c>/<c>+</c>, non-empty, no
    /// whitespace/control characters, and an explicit <c>*</c> rejection.
    /// </summary>
    private static string? ValidateRef(string refName)
    {
        if (refName.Contains("..") || refName.Contains("://"))
            return $"Invalid git ref: '{GitUrlRedactor.Redact(refName)}'.";

        if (refName.StartsWith('-') || refName.StartsWith('+'))
            return $"Invalid git ref: '{GitUrlRedactor.Redact(refName)}'.";

        if (refName.Length == 0)
            return $"Invalid git ref: '{GitUrlRedactor.Redact(refName)}'.";

        foreach (var c in refName)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
                return $"Invalid git ref: '{GitUrlRedactor.Redact(refName)}'.";
        }

        if (refName.Contains('*'))
            return $"Invalid git ref: '{GitUrlRedactor.Redact(refName)}'.";

        return null;
    }
}

/// <summary>
/// The result of a config-repo git command. <see cref="ConfigRepoOpResult.SanitizedError"/>
/// is always redacted (see <see cref="GitUrlRedactor"/>).
/// </summary>
internal sealed record ConfigRepoOpResult(bool Success, int ExitCode, string Stdout, string SanitizedError);

/// <summary>
/// Config-repo health summary. DECLARED in this slice; implemented by the health probe (2c-b2).
/// </summary>
internal sealed record ConfigRepoHealth(bool HasRepo, string? RepoDir, string? AgentsWorkDir, string? SanitizedReason);

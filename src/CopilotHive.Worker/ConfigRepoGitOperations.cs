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
/// Stage 7 credential env injection with the literal-secret redaction pass. Slice 2c-b2 adds the
/// credential-free HEALTH PROBE — <see cref="ProbeAndEnsureRepoHealthyAsync"/> — with the
/// best-effort origin reconciliation-as-API and the local identity configuration. Slice 2c-b3
/// adds the OWNED-CONTAINER staging + atomic-move CLONE — <see cref="CloneAsync"/>.
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

    /// <summary>2c-b2 — the health probe could not read the worktree root.</summary>
    private const string ToplevelUnknown = "Could not determine worktree root.";

    /// <summary>2c-b2 — the probed worktree root is not the configured config repo directory.</summary>
    private const string ToplevelMismatch =
        "Config repo worktree root does not match the configured directory.";

    /// <summary>2c-b2 — the origin reconciliation is not applicable to a non-HTTPS-github.com repo.</summary>
    private const string OriginReconciliationSkipped =
        "Config repo origin reconciliation skipped: the configured repository is not HTTPS github.com.";

    /// <summary>2c-b2 — the NOTE prefix applied to an <see cref="EnsureOriginAsync"/> rejection.</summary>
    private const string OriginReconciliationFailedPrefix = "Origin reconciliation failed: ";

    /// <summary>2c-b2 — the local identity email configured by the health API.</summary>
    private const string IdentityEmail = "copilothive-worker@local";

    /// <summary>2c-b2 — the local identity name configured by the health API.</summary>
    private const string IdentityName = "CopilotHive Worker";

    /// <summary>2c-b2 — the FIXED note for a failed <c>git config user.email</c>.</summary>
    private const string IdentityEmailFailed = "Identity configuration failed: user.email.";

    /// <summary>2c-b2 — the FIXED note for a failed <c>git config user.name</c>.</summary>
    private const string IdentityNameFailed = "Identity configuration failed: user.name.";

    /// <summary>2c-b2 — the separator joining the best-effort notes in EXECUTION order.</summary>
    private const string NoteSeparator = "; ";

    /// <summary>Stage 6e — the credential helper path is missing or its delegate failed.</summary>
    private const string HelperUnavailable = "Git credential helper path is not available.";

    /// <summary>The env variable carrying the config-repo credential to the FINAL command.</summary>
    private const string CredentialEnvName = "GITHUB_CONFIG_REPO_TOKEN";

    /// <summary>The env variable pointing git at the non-interactive credential helper.</summary>
    private const string AskpassEnvName = "GIT_ASKPASS";

    /// <summary>The literal-redaction replacement for an ordinal credential occurrence.</summary>
    private const string RedactedPlaceholder = "[redacted]";

    /// <summary>2c-b3 — the clone target already exists, so nothing may be staged or moved.</summary>
    private const string CloneTargetExists = "Config repo clone target already exists.";

    /// <summary>2c-b3 — no owned staging container could be created within the attempt bound.</summary>
    private const string StagingUnavailable =
        "Config repo clone staging directory could not be created.";

    /// <summary>2c-b3 — the mandatory post-clone identity configuration failed.</summary>
    private const string CloneIdentityFailed = "Config repo clone identity configuration failed.";

    /// <summary>2c-b3 — the infix marking a staging container as this seam's work directory.</summary>
    private const string StagingContainerInfix = ".copilothive-clone-";

    /// <summary>2c-b3 — the suffix closing a staging container's name.</summary>
    private const string StagingContainerSuffix = ".copilothive-work";

    /// <summary>2c-b3 — the OWNERSHIP marker file created (exclusively) inside a container.</summary>
    private const string StagingOwnerMarker = ".copilothive-owner";

    /// <summary>2c-b3 — the container child that git clones INTO (it must be EMPTY).</summary>
    private const string StagingRepoChild = "repo";

    /// <summary>2c-b3 — the bound on staging-container creation attempts.</summary>
    private const int StagingAttempts = 5;

    /// <summary>2c-b3 — the maximum accepted length of a staging nonce.</summary>
    private const int MaxNonceLength = 32;

    private readonly string _configRepoDirCanonical;
    private readonly Func<string?> _resolvedUrlResolver;
    private readonly Func<string?> _credentialResolver;
    private readonly WorkerLogger _log;
    private readonly Func<string> _credentialHelperPath;
    private readonly Action _onDispose;
    private readonly Func<string, string> _pathCanonicalizer;
    private readonly Func<string> _stagingNonceGenerator;
    private readonly Func<string, bool>? _targetEntryExists;
    private readonly Func<string, bool>? _stagingMarkerCreateNew;
    private readonly Func<string, bool>? _stagingRepoChildCreate;

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
    /// defaults to <see cref="Path.GetFullPath(string)"/>. The four trailing 2c-b3 delegates
    /// are the CLONE seams: <paramref name="stagingNonceGenerator"/> (defaulting to a 12-char
    /// hex GUID slice), <paramref name="targetEntryExists"/> (the TWO clone-target checks
    /// only — the staging occupancy checks always use the real algorithm),
    /// <paramref name="stagingMarkerCreateNew"/> (receiving the CONTAINER path) and
    /// <paramref name="stagingRepoChildCreate"/> (receiving the fully-joined
    /// <c>&lt;container&gt;/repo</c> path). A null delegate selects the real implementation.
    /// </summary>
    internal ConfigRepoGitOperations(
        string configRepoDir,
        Func<string?> resolvedUrlResolver,
        Func<string?> credentialResolver,
        WorkerLogger log,
        Func<string> credentialHelperPath,
        Action onDispose,
        Func<string, string>? pathCanonicalizer = null,
        Func<string>? stagingNonceGenerator = null,
        Func<string, bool>? targetEntryExists = null,
        Func<string, bool>? stagingMarkerCreateNew = null,
        Func<string, bool>? stagingRepoChildCreate = null)
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
        _stagingNonceGenerator = stagingNonceGenerator ?? DefaultNonce;
        _targetEntryExists = targetEntryExists;
        _stagingMarkerCreateNew = stagingMarkerCreateNew;
        _stagingRepoChildCreate = stagingRepoChildCreate;

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
    /// Slice 2c-b2 — the config-repo HEALTH PROBE, the origin reconciliation-as-API and the
    /// local identity configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pipeline: Stage 1 disposal → Stage 2 argument basics → Stage 3 the SAME exact
    /// containment as the command path (a foreign directory NEVER launches a probe subprocess,
    /// so the seam can neither reconcile nor identity-mutate a repository it does not own) →
    /// Stage 4 <c>git rev-parse --is-inside-work-tree</c> → Stage 5 the top-level containment
    /// verification via <c>git rev-parse --show-toplevel</c> → Stage 6 the BEST-EFFORT origin
    /// reconciliation (eligible URLs only, serialized on the same per-instance gate) → Stage 6b
    /// the ALWAYS-attempted identity configuration.
    /// </para>
    /// <para>
    /// EVERY subprocess launched here is CREDENTIAL-FREE: the probe, the top-level query, the
    /// origin inspection/add/set-url and the two identity commands all run with the scrubbed
    /// environment plus <c>GIT_TERMINAL_PROMPT=0</c>. The credential resolver and the credential
    /// helper delegate are NEVER invoked by this API — no credential is needed to inspect or
    /// repair an origin to the sanitized, credential-free URL.
    /// </para>
    /// <para>
    /// Stages 6 and 6b are BEST-EFFORT: their failures become NOTES joined in EXECUTION order
    /// with <c>"; "</c> into <see cref="ConfigRepoHealth.SanitizedReason"/>, and never downgrade
    /// <see cref="ConfigRepoHealth.HasRepo"/> or clear the reported directories. An
    /// <see cref="OperationCanceledException"/> from ANY stage — a subprocess, the origin gate
    /// after acquisition, or the URL resolver — PROPAGATES rather than being recorded.
    /// </para>
    /// <para>
    /// REDACTION SCOPE: <see cref="ConfigRepoHealth.SanitizedReason"/> is the only field passed
    /// through <see cref="GitUrlRedactor.Redact"/> (the fixed notes are redaction no-ops, which
    /// keeps the rule uniform). <see cref="ConfigRepoHealth.RepoDir"/> and
    /// <see cref="ConfigRepoHealth.AgentsWorkDir"/> are PATHS — the trimmed git output and a
    /// path derived from it — and carry no URL to redact.
    /// </para>
    /// </remarks>
    internal async Task<ConfigRepoHealth> ProbeAndEnsureRepoHealthyAsync(
        string targetDir, CancellationToken ct)
    {
        // Stage 1 — disposed, FIRST.
        if (Volatile.Read(ref _disposed) != 0)
            return Unhealthy("Seam disposed.");

        // Stage 2 — argument basics.
        if (string.IsNullOrWhiteSpace(targetDir))
            return Unhealthy("Invalid arguments.");

        string canonicalTargetDir;
        try
        {
            canonicalTargetDir = Canonicalize(targetDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException or IOException)
        {
            return Unhealthy("Invalid arguments.");
        }

        // Stage 3 — the containment. NO subprocess is launched for a foreign directory.
        if (!IsContained(canonicalTargetDir))
            return Unhealthy("Invalid git command: the working directory is not the config repository.");

        // Stage 4 — the worktree probe (credential-free).
        var probe = await LaunchGitProcessAsync(
            new[] { "rev-parse", "--is-inside-work-tree" }, ct);
        if (probe is null)
            return Unhealthy("Git process failed to start.");

        if (probe.ExitCode != 0)
            return Unhealthy("Not a git worktree.");

        var probeOutput = (probe.Stdout ?? string.Empty).Trim();

        // A BARE repository reports `false`; anything else (including an EMPTY output) is
        // output this seam does not recognize and must not act on.
        if (string.Equals(probeOutput, "false", StringComparison.Ordinal))
            return Unhealthy("Not a git worktree.");

        if (!string.Equals(probeOutput, "true", StringComparison.Ordinal))
            return Unhealthy("Unrecognized rev-parse output.");

        // Stage 5 — the top-level containment verification.
        var toplevel = await LaunchGitProcessAsync(
            new[] { "rev-parse", "--show-toplevel" }, ct);
        if (toplevel is null || toplevel.ExitCode != 0)
            return Unhealthy(ToplevelUnknown);

        // The reported root is returned VERBATIM after the trim — the canonicalization below
        // exists only for the COMPARISON.
        var repoDir = (toplevel.Stdout ?? string.Empty).Trim();
        if (repoDir.Length == 0)
            return Unhealthy(ToplevelUnknown);

        string canonicalToplevel;
        try
        {
            canonicalToplevel = Canonicalize(repoDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException or IOException)
        {
            return Unhealthy(ToplevelUnknown);
        }

        if (!IsContained(canonicalToplevel))
        {
            // The repository EXISTS (HasRepo stays true) but its root is somewhere else, so the
            // reconciliation and identity steps are SKIPPED and no directory is reported.
            return new ConfigRepoHealth(true, null, null, GitUrlRedactor.Redact(ToplevelMismatch));
        }

        var agentsWorkDir = Path.Combine(repoDir, "agents");

        // Stage 6 / 6b — the best-effort reconciliation and identity. Their notes accumulate in
        // EXECUTION order and never change the healthy verdict.
        var notes = new List<string>();
        await ReconcileOriginBestEffortAsync(notes, ct);
        await ConfigureIdentityBestEffortAsync(notes, ct);

        var reason = notes.Count == 0
            ? null
            : GitUrlRedactor.Redact(string.Join(NoteSeparator, notes));

        return new ConfigRepoHealth(true, repoDir, agentsWorkDir, reason);
    }

    /// <summary>
    /// Stage 6 — the BEST-EFFORT origin reconciliation. The URL resolution happens BEFORE the
    /// gate (exactly as in the command path); only an ELIGIBLE (Branch A) sanitized URL enters
    /// the gate and runs the credential-free <see cref="EnsureOriginAsync"/> inspection / add /
    /// set-url. Every failure becomes a NOTE; nothing aborts the health report.
    /// </summary>
    /// <remarks>
    /// An INELIGIBLE (Branch B) URL — an SSH, file or local-path config repo — is
    /// self-consistent and needs no origin repair: the 2c-b1c-ii equivalence logic requires
    /// HTTPS/443, and the health API deliberately does NOT extend it. THE ACQUIRED-FLAG RULE
    /// applies verbatim: a cancellation during <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>
    /// BEFORE acquisition propagates without releasing a gate this call never owned.
    /// </remarks>
    private async Task ReconcileOriginBestEffortAsync(List<string> notes, CancellationToken ct)
    {
        var (urlError, eligible, sanitized) = ResolveTransportEligibility();
        if (urlError is not null)
        {
            // A missing URL, a sanitizer rejection, or a non-cancellation resolver throw. There
            // is no URL to reconcile against, so the reconciliation cannot run at all.
            notes.Add(urlError);
            return;
        }

        if (!eligible)
        {
            notes.Add(OriginReconciliationSkipped);
            return;
        }

        var acquired = false;
        try
        {
            await _originGate.WaitAsync(ct);
            acquired = true;

            var originError = await EnsureOriginAsync(sanitized!, ct);
            if (originError is not null)
                notes.Add(ToReconciliationNote(originError));
        }
        finally
        {
            if (acquired)
                _originGate.Release();
        }
    }

    /// <summary>
    /// 2c-b2 — turns an <see cref="EnsureOriginAsync"/> rejection into a health NOTE: the fixed
    /// <c>Origin reconciliation failed: </c> prefix followed by the rejection as a CONTINUATION
    /// of that sentence, so its leading capital is lowered (<c>Config repo origin could not be
    /// verified.</c> becomes <c>… failed: config repo origin could not be verified.</c>). The
    /// four state-machine messages remain the single source of the note text.
    /// </summary>
    /// <remarks>
    /// The whitelist is explicit and has NO silent fallback: an unrecognized rejection means
    /// the state machine grew a message this mapping does not know about, which must fail
    /// loudly rather than emit a mis-cased or unclassified note.
    /// </remarks>
    private static string ToReconciliationNote(string rejection)
    {
        if (rejection is not (OriginNotVerified or OriginNotAdded or OriginNotUpdated or OriginMismatch))
            throw new InvalidOperationException("Unhandled origin reconciliation rejection.");

        return OriginReconciliationFailedPrefix
            + char.ToLowerInvariant(rejection[0])
            + rejection[1..];
    }

    /// <summary>
    /// Stage 6b — the local identity configuration, ALWAYS attempted once the probe and the
    /// top-level verification succeeded (even with no resolved URL or a skipped reconciliation).
    /// The two commands run INDEPENDENTLY: an <c>user.email</c> failure never prevents the
    /// <c>user.name</c> attempt, so both notes can appear, in order.
    /// </summary>
    /// <remarks>
    /// This is deliberately NOT <see cref="GitOperations.ConfigureLocalIdentity"/>: that method
    /// configures a different identity and STOPS after an email failure. Only its command ORDER
    /// is prior art. The notes are FIXED text — no stderr and no exception message ever reaches
    /// them.
    /// </remarks>
    private async Task ConfigureIdentityBestEffortAsync(List<string> notes, CancellationToken ct)
    {
        var email = await LaunchGitProcessAsync(
            new[] { "config", "user.email", IdentityEmail }, ct);
        if (email is null || email.ExitCode != 0)
            notes.Add(IdentityEmailFailed);

        var name = await LaunchGitProcessAsync(
            new[] { "config", "user.name", IdentityName }, ct);
        if (name is null || name.ExitCode != 0)
            notes.Add(IdentityNameFailed);
    }

    /// <summary>
    /// Builds a NEGATIVE health report: no repo, no directories, and the reason redacted at
    /// construction like every other seam message.
    /// </summary>
    private static ConfigRepoHealth Unhealthy(string reason) =>
        new(false, null, null, GitUrlRedactor.Redact(reason));

    // ==================================================================
    // Slice 2c-b3 — the OWNED-CONTAINER staging + atomic-move CLONE.
    // ==================================================================

    /// <summary>
    /// Slice 2c-b3 — clones the configured config repo into <paramref name="targetDir"/> via an
    /// OWNED staging container followed by an ATOMIC <see cref="Directory.Move"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE EIGHT STAGES, in order: (1) disposal; (2) the argument basics + the SAME exact
    /// containment as every other API (<paramref name="targetDir"/> must canonicalize to the
    /// configured config repo directory); (3) the clone-target ABSENCE check plus the
    /// parent-existence validation; (4) the URL resolution / transport eligibility and — only
    /// for an ELIGIBLE URL with a non-whitespace credential — the credential + helper
    /// resolution; (5) the bounded (5-attempt) staging-container acquisition; (6) the
    /// <c>git clone</c> subprocess; (7) the MANDATORY, credential-free clone-time identity
    /// configuration; (8) the absence RE-CHECK and the atomic move of
    /// <c>&lt;container&gt;/repo</c> onto the canonical target.
    /// </para>
    /// <para>
    /// THE OWNED-CONTAINER DESIGN. Every attempt stages into
    /// <c>&lt;parent&gt;/&lt;target-name&gt;.copilothive-clone-&lt;nonce&gt;.copilothive-work/</c>
    /// and claims it by creating <c>.copilothive-owner</c> with
    /// <see cref="FileMode.CreateNew"/> — the ATOMIC exclusive primitive that decides ownership.
    /// The git destination is the SIBLING <c>&lt;container&gt;/repo</c>, so the directory git
    /// clones into is EMPTY and the marker never lands inside the cloned worktree. The container
    /// is deleted (recursively, with any deletion exception SWALLOWED) if and ONLY IF the marker
    /// file exists at cleanup time AND the attempt's in-memory flag records that THIS
    /// invocation's <see cref="FileMode.CreateNew"/> succeeded: a foreign, unmarked container is
    /// NEVER deleted.
    /// </para>
    /// <para>
    /// THE TWO ACCEPTED RACE LIMITATIONS. (1) THE PRE-CREATION RACE: a foreign actor could
    /// create the container directory under this invocation's high-entropy name in the window
    /// between the occupancy check and this seam's own creation, with the marker
    /// <see cref="FileMode.CreateNew"/> then still succeeding INSIDE it — in which case this
    /// invocation would take ownership of, and ultimately delete, a directory it did not create.
    /// Guessing a <c>.copilothive-clone-&lt;nonce&gt;.copilothive-work</c> name within that
    /// window is astronomically improbable. (2) THE REPLACEMENT RACE: after this invocation has
    /// created its container and its marker, a foreign actor could remove the container and
    /// recreate it with a FORGED <c>.copilothive-owner</c>; the marker-iff cleanup rule would
    /// then see a marker plus this attempt's flag and delete the replacement. The threat model
    /// is the single-actor operator environment: an actor racing this seam on a
    /// <c>.copilothive-clone-&lt;nonce&gt;.copilothive-work/</c> container AND forging
    /// <c>.copilothive-owner</c> is ADVERSARIAL, not accidental. Both residual risks are
    /// ACCEPTED and documented rather than mitigated — no filesystem-portable primitive removes
    /// them, and a weaker cleanup rule would leak staging directories on every failure path.
    /// </para>
    /// <para>
    /// SECRECY: the credential NEVER appears in the clone's tokenized arguments (they carry the
    /// SANITIZED, credential-free URL), so the origin git writes is credential-free too. The
    /// injection is env-only and applies to the clone launch alone; the identity commands are
    /// credential-free. Every returned <see cref="ConfigRepoOpResult.Stdout"/> and
    /// <see cref="ConfigRepoOpResult.SanitizedError"/> passes through
    /// <see cref="GitUrlRedactor"/> and then the literal-secret redaction pass.
    /// </para>
    /// <para>
    /// CANCELLATION: an <see cref="OperationCanceledException"/> from ANY stage — the nonce
    /// generator, the target-existence delegate, a staging seam, the clone, or EITHER identity
    /// command — propagates AFTER the owned container has been cleaned up. In particular a
    /// cancelled <c>user.email</c> ABORTS immediately: <c>user.name</c> does not run.
    /// </para>
    /// </remarks>
    internal async Task<ConfigRepoOpResult> CloneAsync(string targetDir, CancellationToken ct)
    {
        // Stage 1 — disposed, FIRST.
        if (Volatile.Read(ref _disposed) != 0)
            return Reject("Seam disposed.");

        // Stage 2 — argument basics, then the exact containment.
        if (string.IsNullOrWhiteSpace(targetDir))
            return Reject("Invalid arguments.");

        string canonicalTargetDir;
        try
        {
            canonicalTargetDir = Canonicalize(targetDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException or IOException)
        {
            return Reject("Invalid arguments.");
        }

        if (!IsContained(canonicalTargetDir))
            return Reject("Invalid git command: the working directory is not the config repository.");

        // THE CANONICAL-PATH RULE: the parent, the target name, the clone arguments and the
        // final move ALL derive from _configRepoDirCanonical — never from the caller's spelling.
        if (TargetEntryExists())
            return Reject(CloneTargetExists);

        var parent = Path.GetDirectoryName(_configRepoDirCanonical);
        var targetName = Path.GetFileName(_configRepoDirCanonical);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(targetName) || !Directory.Exists(parent))
            return Reject("Invalid arguments.");

        // Stage 4 — the URL / credential gating. NO container exists yet, so a rejection here
        // leaves the filesystem completely untouched.
        var (urlError, eligible, sanitizedUrl) = ResolveTransportEligibility();
        if (urlError is not null)
            return Reject(urlError);

        string? credential = null;
        string? helperPath = null;
        if (eligible)
        {
            var (credentialError, resolvedCredential, resolvedHelper) = ResolveCredential();
            credential = resolvedCredential;
            if (credentialError is not null)
                return RedactLiteralCredential(Reject(credentialError), credential);

            helperPath = resolvedHelper;
        }

        // Stage 5 — the bounded staging-container acquisition.
        var staging = AcquireStagingContainer(parent, targetName);
        if (staging is null)
            return RedactLiteralCredential(Reject(StagingUnavailable), credential);

        var (container, owned) = staging.Value;
        try
        {
            // Stage 6 — the clone subprocess. The working directory is the canonical PARENT
            // (the target does not exist yet, so it cannot be the working directory).
            var repoChild = Path.Combine(container, StagingRepoChild);
            var clone = await LaunchCloneProcessAsync(
                ["clone", sanitizedUrl!, repoChild], parent, ct, credential, helperPath);
            if (clone is null)
                return RedactLiteralCredential(Reject("Git process failed to start."), credential);

            if (clone.ExitCode != 0)
                return RedactLiteralCredential(MapResult(clone), credential);

            // Stage 7 — the MANDATORY, credential-free clone-time identity, inside the clone.
            var identityFailed = false;
            var email = await LaunchCloneProcessAsync(
                ["config", "user.email", IdentityEmail], repoChild, ct);
            if (email is null || email.ExitCode != 0)
                identityFailed = true;

            var name = await LaunchCloneProcessAsync(
                ["config", "user.name", IdentityName], repoChild, ct);
            if (name is null || name.ExitCode != 0)
                identityFailed = true;

            if (identityFailed)
                return RedactLiteralCredential(Reject(CloneIdentityFailed), credential);

            // Stage 8 — the absence RE-CHECK, then the atomic move.
            if (TargetEntryExists())
                return RedactLiteralCredential(Reject(CloneTargetExists), credential);

            try
            {
                Directory.Move(repoChild, _configRepoDirCanonical);
            }
            catch (PathTooLongException)
            {
                // The PATH-RETRY category, caught BEFORE IOException.
                return RedactLiteralCredential(Reject(StagingUnavailable), credential);
            }
            catch (Exception ex) when (ex is SecurityException or ArgumentException)
            {
                return RedactLiteralCredential(Reject(StagingUnavailable), credential);
            }
            catch (Exception ex) when (ex is IOException and not PathTooLongException
                or UnauthorizedAccessException or NotSupportedException)
            {
                // The COLLISION category. Anything ELSE PROPAGATES.
                return RedactLiteralCredential(Reject(StagingUnavailable), credential);
            }

            return new ConfigRepoOpResult(true, 0, "", "");
        }
        finally
        {
            // The marker-iff cleanup. On the successful move the container still holds only the
            // marker, so it is removed here too and nothing is ever left behind.
            CleanupOwnedContainer(container, owned);
        }
    }

    /// <summary>
    /// Stage 5 — acquires an OWNED staging container within <see cref="StagingAttempts"/>
    /// attempts. Each attempt: a fresh, VALIDATED nonce → the real occupancy check → the
    /// container creation → the exclusive marker creation → the <c>repo</c>-child creation.
    /// A collision or path-category failure at any step retries with a NEW nonce, cleaning up
    /// this attempt's container FIRST when ownership had already been acquired.
    /// </summary>
    /// <returns>
    /// The container path and its ownership flag, or <c>null</c> when all attempts collided.
    /// </returns>
    private (string Container, bool Owned)? AcquireStagingContainer(
        string parent, string targetName)
    {
        for (var attempt = 0; attempt < StagingAttempts; attempt++)
        {
            var nonce = NextNonce();
            if (nonce is null)
                continue;

            var candidate = Path.Combine(
                parent, targetName + StagingContainerInfix + nonce + StagingContainerSuffix);

            // The occupancy check ALWAYS uses the REAL algorithm — never the target seam — and
            // its exceptions PROPAGATE exactly like the two target checks'.
            if (EntryExists(candidate))
                continue;

            // THE PER-ATTEMPT marker state: an attempt owns only the container IT claimed.
            string? container = candidate;
            var owned = false;
            try
            {
                Directory.CreateDirectory(candidate);

                if (!CreateOwnershipMarker(candidate))
                {
                    // The marker was NOT created by this invocation, so the container must not
                    // be deleted: it may be a foreign actor's.
                    container = null;
                    continue;
                }

                owned = true;

                if (!CreateRepoChild(Path.Combine(candidate, StagingRepoChild)))
                    continue; // the finally cleans up the OWNED container

                container = null; // ownership passes to the caller — no cleanup here
                return (candidate, true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (PathTooLongException)
            {
                // THE PATH-RETRY CATEGORY, caught BEFORE IOException (it derives from it).
            }
            catch (Exception ex) when (ex is SecurityException or ArgumentException)
            {
                // The rest of the PATH-RETRY category.
            }
            catch (Exception ex) when (ex is IOException and not PathTooLongException
                or UnauthorizedAccessException or NotSupportedException)
            {
                // THE COLLISION CATEGORY — the already-exists forms. Anything ELSE PROPAGATES.
            }
            finally
            {
                if (container is not null)
                    CleanupOwnedContainer(container, owned);
            }
        }

        return null;
    }

    /// <summary>
    /// Produces the next staging nonce. An INVALID output (empty, over
    /// <see cref="MaxNonceLength"/> characters, or outside the safe leaf alphabet
    /// <c>[0-9a-f]</c>) or a NON-cancellation throw counts as a COLLISION —
    /// <c>null</c> here — so the attempt simply retries. An
    /// <see cref="OperationCanceledException"/> PROPAGATES.
    /// </summary>
    private string? NextNonce()
    {
        string? nonce;
        try
        {
            nonce = _stagingNonceGenerator();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        return IsValidNonce(nonce) ? nonce : null;
    }

    /// <summary>The default nonce: a 12-character lower-case hex slice of a fresh GUID.</summary>
    private static string DefaultNonce() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// The nonce domain: non-empty, at most <see cref="MaxNonceLength"/> characters, and made
    /// exclusively of the SAFE leaf alphabet <c>[0-9a-f]</c> — so a nonce can never introduce a
    /// separator, a dot segment, a wildcard or a case-folding surprise into the container name.
    /// </summary>
    private static bool IsValidNonce(string? nonce)
    {
        if (string.IsNullOrEmpty(nonce) || nonce.Length > MaxNonceLength)
            return false;

        foreach (var c in nonce)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Creates the ownership marker inside <paramref name="container"/> — the ATOMIC exclusive
    /// primitive. The real implementation is
    /// <c>File.Open(&lt;container&gt;/.copilothive-owner, FileMode.CreateNew)</c>; the seam
    /// receives the CONTAINER path and reports success/collision itself.
    /// </summary>
    private bool CreateOwnershipMarker(string container)
    {
        if (_stagingMarkerCreateNew is not null)
            return _stagingMarkerCreateNew(container);

        using var marker = File.Open(
            Path.Combine(container, StagingOwnerMarker), FileMode.CreateNew);
        return true;
    }

    /// <summary>
    /// Creates the EMPTY git clone destination. The seam receives the fully-joined
    /// <c>&lt;container&gt;/repo</c> PATH — not the bare container path.
    /// </summary>
    private bool CreateRepoChild(string repoChildPath)
    {
        if (_stagingRepoChildCreate is not null)
            return _stagingRepoChildCreate(repoChildPath);

        Directory.CreateDirectory(repoChildPath);
        return true;
    }

    /// <summary>
    /// THE CLEANUP RULE (the marker-iff): delete the container recursively IF AND ONLY IF the
    /// marker file EXISTS at cleanup time AND this attempt's in-memory flag records that THIS
    /// invocation's exclusive create succeeded. A foreign, unmarked container is NEVER deleted.
    /// A deletion exception is SWALLOWED — a leaked staging directory is strictly better than a
    /// failure that masks the operation's real outcome.
    /// </summary>
    private static void CleanupOwnedContainer(string container, bool owned)
    {
        if (!owned)
            return;

        try
        {
            if (!File.Exists(Path.Combine(container, StagingOwnerMarker)))
                return;

            Directory.Delete(container, recursive: true);
        }
        catch
        {
            // Swallowed — the cleanup is strictly best-effort.
        }
    }

    /// <summary>
    /// The TWO clone-target checks (the Stage 3 initial check and the Stage 8 re-check) over
    /// <see cref="_configRepoDirCanonical"/>. When the seam is installed it REPLACES the
    /// algorithm for these two call sites only — the staging occupancy checks always run the
    /// real one. Every exception (including an <see cref="OperationCanceledException"/>)
    /// PROPAGATES out of <see cref="CloneAsync"/> rather than being mapped to a fixed result.
    /// </summary>
    private bool TargetEntryExists() =>
        _targetEntryExists is not null
            ? _targetEntryExists(_configRepoDirCanonical)
            : EntryExists(_configRepoDirCanonical);

    /// <summary>
    /// THE REAL entry-existence algorithm. <see cref="File.GetAttributes(string)"/> answers for
    /// every ordinary entry — a file, a directory, or a symlink with a live target — regardless
    /// of its attributes. Only the two ABSENCE exceptions fall through to the enumeration
    /// fallback, which catches the DANGLING-symlink case: the link entry itself is listed in its
    /// parent even though its target cannot be resolved. Any OTHER exception PROPAGATES.
    /// </summary>
    private static bool EntryExists(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // The dangling-link case — fall through to the enumeration below.
        }

        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            return false;

        var leaf = Path.GetFileName(full);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
        {
            if (string.Equals(Path.GetFileName(entry), leaf, comparison))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The clone-path launch helper: identical to <see cref="LaunchGitProcessAsync"/> except
    /// that the working directory is EXPLICIT (the canonical parent for the clone itself, the
    /// cloned worktree for the identity commands) rather than the config repo directory, which
    /// does not exist yet.
    /// </summary>
    private async Task<GitProcessResult?> LaunchCloneProcessAsync(
        IReadOnlyList<string> tokenizedArgs,
        string workingDirectory,
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
                    WorkingDirectory: workingDirectory,
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
            // A catch-ALL for non-cancellation launch failures. The exception's own text is
            // NEVER propagated.
            return null;
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
/// Config-repo health summary. Produced by the 2c-b2 health probe
/// (<c>ProbeAndEnsureRepoHealthyAsync</c>). <see cref="SanitizedReason"/> is redacted at
/// construction; <see cref="RepoDir"/> and <see cref="AgentsWorkDir"/> are PATHS and are not.
/// </summary>
internal sealed record ConfigRepoHealth(bool HasRepo, string? RepoDir, string? AgentsWorkDir, string? SanitizedReason);

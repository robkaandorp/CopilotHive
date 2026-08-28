using System.Globalization;
using System.Security;
using CopilotHive.Services;

namespace CopilotHive.Worker;

/// <summary>
/// Validation and EXECUTION layer for the config-repo git seam (slices 2c-b1b-i + 2c-b1b-ii):
/// strict per-command grammar, ref prechecks PLUS the check-ref-format subprocess, worktree
/// containment, canonicalization, constructors, disposal, and the real process execution via
/// the SHARED <see cref="GitOperations.ExecuteProcessAsync"/> with the concrete result mapping
/// and redaction boundary. The credential/origin transport (2c-b1c), the health probe (2c-b2),
/// and the clone (2c-b3) are later slices.
/// </summary>
internal sealed class ConfigRepoGitOperations : IDisposable
{
    private readonly string _configRepoDirCanonical;
    private readonly Func<string?> _resolvedUrlResolver;
    private readonly Func<string?> _credentialResolver;
    private readonly WorkerLogger _log;
    private readonly Func<string> _credentialHelperPath;
    private readonly Action _onDispose;
    private readonly Func<string, string> _pathCanonicalizer;

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
    /// <see cref="GitOperations.ExecuteProcessAsync"/>. Stage 6 additionally validates the
    /// ref candidate with a <c>git check-ref-format --allow-onelevel &lt;ref&gt;</c> subprocess.
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
        var (scanError, refCandidate) = ScanTokens(subcommand, snapshot);
        if (scanError is not null)
            return Reject(scanError);

        // Stage 6 — ref validation: the PRECHECKS, then (when they pass) the check-ref-format
        // subprocess. Run only once the Stage 5 scan completed without a grammar rejection.
        // This ordering is what makes `pull origin +bad extra` report `too many arguments.`
        // and `pull badremote +bad` report the remote rejection: a Stage 5 error ALWAYS wins
        // over a Stage 6 ref error.
        if (refCandidate is not null)
        {
            var refError = ValidateRef(refCandidate);
            if (refError is not null)
                return Reject(refError);

            // The prechecks passed — confirm with the subprocess. NO credential env in this
            // slice (2c-b1b-ii): only the scrubbed inherited environment plus
            // GIT_TERMINAL_PROMPT=0. A non-zero exit rejects the ref.
            var refValidation = await LaunchGitProcessAsync(
                new[] { "check-ref-format", "--allow-onelevel", refCandidate }, ct);
            if (refValidation is null)
                return Reject("Git process failed to start.");

            if (refValidation.ExitCode != 0)
                return Reject($"Invalid git ref: '{GitUrlRedactor.Redact(refCandidate)}'.");
        }

        // Stage 7 — the real execution: the SNAPSHOT launched verbatim via the SHARED
        // ExecuteProcessAsync. The working directory is the CONSTRUCTOR-canonicalized
        // configRepoDir — NOT the call-time workingDirectory string (Stage 3 containment has
        // already verified their equivalence).
        var execution = await LaunchGitProcessAsync(snapshot, ct);
        if (execution is null)
            return Reject("Git process failed to start.");

        return MapResult(execution);
    }

    /// <summary>
    /// Fires <c>onDispose</c> exactly once; exceptions from it are swallowed. Post-disposal
    /// calls return <c>Seam disposed.</c>.
    /// </summary>
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
    /// The child environment for EVERY git process launched by this seam: the SCRUBBED
    /// inherited environment (the shared five-variable <see cref="GitOperations.SanitizeChildEnv"/>)
    /// plus <c>GIT_TERMINAL_PROMPT=0</c>. NO credential environment in this slice (2c-b1c owns it).
    /// </summary>
    private IReadOnlyDictionary<string, string?> BuildChildEnv()
    {
        var sanitized = GitOperations.SanitizeChildEnv(SnapshotCurrentProcessEnv());
        var withPromptDisabled = new Dictionary<string, string?>(sanitized);
        withPromptDisabled["GIT_TERMINAL_PROMPT"] = "0";
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
        IReadOnlyList<string> tokenizedArgs, CancellationToken ct)
    {
        try
        {
            return await GitOperations.ExecuteProcessAsync(
                new GitProcessRequest(
                    "git",
                    Args: Array.Empty<string>(),
                    WorkingDirectory: _configRepoDirCanonical,
                    Env: BuildChildEnv(),
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
    /// Stage 5 — the single left-to-right token scan plus the post-scan structural checks.
    /// Returns the first grammar error message (or <c>null</c>) together with the SELECTED
    /// ref candidate. The ref candidate is deliberately NOT validated here: Stage 6 runs only
    /// after the whole Stage 5 scan completed without a grammar rejection, so a Stage 5 error
    /// always wins over a Stage 6 ref error.
    /// </summary>
    private static (string? Error, string? RefCandidate) ScanTokens(string subcommand, string[] snapshot) =>
        subcommand switch
        {
            "checkout" or "add" or "diff" or "commit" or "merge" or "status" =>
                (ValidateLocalForm(subcommand, snapshot), null),
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
    /// </summary>
    private static (string? Error, string? RefCandidate) ScanCredentialScoped(
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
                    return ("Invalid git command: options must precede positionals.", null);

                if (!seenOptions.Add(token))
                    return ($"Invalid git command: duplicate option '{token}'.", null);

                if (IsConflictingOptionPair(token, seenOptions))
                    return ("Invalid git command: --rebase and --no-rebase are mutually exclusive.", null);

                if (token == "--depth")
                {
                    if (i + 1 >= snapshot.Length
                        || IsOptionLike(snapshot[i + 1])
                        || !IsValidDepth(snapshot[i + 1]))
                    {
                        return ("Invalid git command: --depth requires a positive integer.", null);
                    }

                    i++; // consume the value token
                }

                continue;
            }

            if (IsOptionLike(token))
            {
                // An UNKNOWN dash-prefixed token.
                if (!remoteSeen)
                    return ($"Invalid git command: unknown option '{token}'.", null);

                if (refSeen)
                    return ("Invalid git command: too many arguments.", null);

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
                return ("Invalid git command: too many arguments.", null);

            refSlot = token;
            refSeen = true;
        }

        // Post-scan structural validation — still Stage 5, so it precedes Stage 6.
        if (subcommand == "push")
        {
            // Arity is checked BEFORE remote identity: `push badremote` is an arity failure.
            if (!remoteSeen || !refSeen)
                return ("Invalid git command: push requires 'origin <ref>'.", null);

            if (remote != "origin")
                return ("Invalid git command: the remote must be 'origin'.", null);
        }
        else if (remoteSeen && remote != "origin")
        {
            return ("Invalid git command: the remote must be 'origin'.", null);
        }

        return (null, refSlot);
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

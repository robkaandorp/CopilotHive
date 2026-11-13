using System.Globalization;
using System.Text;

namespace CopilotHive.Worker;

/// <summary>
/// The OPERATION-SCOPED git credential helper every authenticated WORKER-side target-repository
/// network command uses: a private temporary directory holding a <b>TOKEN-FREE</b> POSIX
/// <c>askpass</c> script, plus the child-only environment that points git at it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> The worker's target-repository credential used to ride the clone
/// URL: it appeared on the git argv AND was persisted by git into the cloned repository's
/// <c>origin</c> configuration. This helper ends that transport: the credential is never part of
/// the script file, of any git argument list, of <c>.git/config</c>, or of the worker's own
/// long-lived environment. It is injected into the CHILD process environment for one launch only
/// by <see cref="BuildChildEnvironment"/>.
/// </para>
/// <para>
/// <b>Provenance.</b> This mirrors the host-side <c>HostGitAskpassHelper</c> design (which lives in
/// the orchestrator assembly and is therefore not reachable from the worker project). The
/// differences are deliberate and limited to the worker's destination contract: the script also
/// accepts an EXPLICIT <c>:443</c> (the eligible-port rule the config seam's
/// <c>IsEligibleTransportUrl</c> states) and case-folds the challenged host before comparing it.
/// </para>
/// <para>
/// <b>Why the child environment must reset the credential helper list.</b> A container can inherit a
/// credential helper from system/global/local git configuration (GCM, a <c>store</c> helper, a
/// recording helper). Any of them would be consulted BEFORE git's askpass fallback and — worse —
/// could PERSIST the credential it saw. <see cref="BuildChildEnvironment"/> therefore appends
/// operation-scoped <c>credential.helper</c> entries through git's environment config mechanism
/// (<c>GIT_CONFIG_COUNT</c>/<c>GIT_CONFIG_KEY_n</c>/<c>GIT_CONFIG_VALUE_n</c>) AFTER any inherited
/// entries, and then REPLACES the inherited legacy <c>GIT_CONFIG_PARAMETERS</c> channel (which git
/// evaluates last, so an inherited helper there would outrank the reset). The empty
/// <c>credential.helper</c> value resets the accumulated helper list, which disables inherited
/// <c>get</c> (selection) and inherited <c>store</c>/<c>approve</c> (persistence) alike. Nothing
/// here rewrites global, system or repository git configuration: the reset lives entirely in the
/// child's environment for the duration of one launch.
/// </para>
/// <para>
/// <b>Platform support is explicitly Linux-only.</b> The helper is a <c>/bin/sh</c> script with a
/// <c>#!/bin/sh</c> shebang, and the owner-only modes below are POSIX. On any other platform git
/// could not reliably execute it, so creation FAILS CLOSED (returns <c>null</c>) and the caller
/// refuses the authenticated operation with an honest, credential-free error instead of ever
/// falling back to a tokenized URL. There is no such fallback anywhere in this class.
/// </para>
/// <para>
/// <b>Cleanup never throws.</b> <see cref="Dispose"/> is idempotent and best-effort: a leaked
/// temporary directory must never fail — or mask the outcome of — the git operation that owned it.
/// </para>
/// </remarks>
internal sealed class WorkerGitAskpassHelper : IDisposable
{
    /// <summary>The helper script's file name inside its own private directory.</summary>
    internal const string ScriptFileName = "askpass.sh";

    /// <summary>
    /// The CHILD-ONLY environment variable the script reads the credential from. It is set for
    /// exactly one launch and never on the worker's own process environment.
    /// </summary>
    internal const string CredentialEnvName = "COPILOTHIVE_WORKER_GIT_ASKPASS_CREDENTIAL";

    /// <summary>The variable git reads the helper script's path from.</summary>
    internal const string AskpassEnvName = "GIT_ASKPASS";

    /// <summary>Disables git's interactive terminal prompting for the child.</summary>
    internal const string PromptDisabledEnvName = "GIT_TERMINAL_PROMPT";

    /// <summary>
    /// Git Credential Manager's interactive switch. It is FORCED to
    /// <see cref="GcmInteractiveNever"/> for the child, so an inherited <c>always</c> cannot
    /// re-enable a GUI/terminal credential prompt for this invocation.
    /// </summary>
    internal const string GcmInteractiveEnvName = "GCM_INTERACTIVE";

    /// <summary>The <c>GCM_INTERACTIVE</c> value that forbids interactive prompting.</summary>
    internal const string GcmInteractiveNever = "never";

    /// <summary>Git's environment-config entry count.</summary>
    internal const string ConfigCountEnvName = "GIT_CONFIG_COUNT";

    /// <summary>
    /// Git's legacy environment-config channel (a quoted <c>'key=value'</c> list). Git evaluates it
    /// AFTER the indexed <c>GIT_CONFIG_COUNT</c> entries, so an inherited helper supplied here
    /// outranks the operation's reset — it is therefore REPLACED, not merely appended to.
    /// </summary>
    internal const string ConfigParametersEnvName = "GIT_CONFIG_PARAMETERS";

    /// <summary>The ONLY host whose challenges the helper script answers.</summary>
    internal const string EligibleHost = "github.com";

    /// <summary>The SAME host with an EXPLICIT default port — equally eligible.</summary>
    internal const string EligibleHostWithPort = "github.com:443";

    /// <summary>The fixed principal a USERNAME prompt is answered with.</summary>
    internal const string UsernamePrincipal = "x-access-token";

    /// <summary>
    /// The EXACT helper script. It implements git's <c>$1</c>-prompt protocol, is TOKEN-FREE, and
    /// releases the credential ONLY for a challenge it can PROVE is for <c>https://github.com</c>
    /// or <c>https://github.com:443</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Strict, fail-closed prompt parsing.</b> Git renders the challenged URL inside the prompt
    /// (<c>Username for 'https://github.com': </c>, <c>Password for 'https://x-access-token@github.com': </c>).
    /// When <c>credential.sanitizePrompt=false</c> (a setting the operation leaves intact) git
    /// renders DECODED userinfo, so a hostile username or push URL can forge text that merely LOOKS
    /// like the eligible host. The script therefore accepts only the two exact prompt shapes, then
    /// refuses (empty output, exit 1 — exactly what git sees when no credential exists) when the URL
    /// part contains ANY quote character, more than one <c>@</c>, or a userinfo that contains
    /// <c>/</c>. Only then — with userinfo already separated from the host — is the host taken as
    /// everything up to the first <c>/</c> and required to equal the eligible host exactly.
    /// Authority text is thus never parsed before userinfo is stripped, so
    /// <c>github.com'@gitlab.com</c> and <c>github.com/@gitlab.com</c> are refused independently of
    /// sanitizePrompt, as are lookalike hosts, other ports, other schemes and anything unparseable.
    /// </para>
    /// <para>
    /// <b>Host case-folding.</b> The challenged host is lower-cased with POSIX <c>tr</c> before the
    /// comparison, because destination eligibility elsewhere is a case-insensitive host comparison.
    /// A lower-cased lookup that fails (no <c>tr</c>) yields an empty host, which is REFUSED.
    /// </para>
    /// <para>
    /// This binds credential RELEASE to the challenged destination, independent of which URL the
    /// worker configured: a push routed through <c>remote.origin.pushurl</c>, a fetch from an
    /// alternate remote, or a redirect to a noneligible host still RUNS but receives no credential.
    /// </para>
    /// </remarks>
    internal const string ScriptContent =
        "#!/bin/sh\n"
        + "# Answer ONLY a challenge for https://github.com or https://github.com:443; refuse anything\n"
        + "# ambiguous. The destination binding is the whole point: no other prompt is ever answered.\n"
        + "p=$1\n"
        + "case \"$p\" in\n"
        + "  \"Username for 'https://\"*\"': \") kind=u; rest=${p#\"Username for 'https://\"} ;;\n"
        + "  \"Password for 'https://\"*\"': \") kind=p; rest=${p#\"Password for 'https://\"} ;;\n"
        + "  *) exit 1 ;;\n"
        + "esac\n"
        + "rest=${rest%\"': \"}\n"
        + "case \"$rest\" in\n"
        + "  *\\'*) exit 1 ;;\n"
        + "esac\n"
        + "case \"$rest\" in\n"
        + "  *@*@*) exit 1 ;;\n"
        + "  *@*)\n"
        + "    user=${rest%@*}\n"
        + "    case \"$user\" in */*) exit 1 ;; esac\n"
        + "    hostpart=${rest#*@} ;;\n"
        + "  *) hostpart=$rest ;;\n"
        + "esac\n"
        + "host=${hostpart%%/*}\n"
        + "host=$(printf '%s' \"$host\" | tr '[:upper:]' '[:lower:]')\n"
        + $"case \"$host\" in\n  \"{EligibleHost}\") ;;\n"
        + $"  \"{EligibleHostWithPort}\") ;;\n"
        + "  *) exit 1 ;;\n"
        + "esac\n"
        + "if [ \"$kind\" = u ]; then\n"
        + $"  printf '%s' \"{UsernamePrincipal}\"\n"
        + "else\n"
        + $"  printf '%s' \"${{{CredentialEnvName}}}\"\n"
        + "fi\n";

    /// <summary>The private directory name prefix, so a leaked directory is identifiable.</summary>
    private const string DirectoryPrefix = "copilothive-target-askpass-";

    /// <summary>
    /// Environment variables REMOVED from the child before anything is added, matched
    /// ordinal-ignore-case. Every one of them would otherwise let this child (or a hook it runs)
    /// pick up a credential through a channel other than the operation-scoped helper: the token
    /// chain itself, the config-repo token, a pre-existing askpass script, an interactive prompt, a
    /// terminal askpass, or a stale copy of this helper's own credential variable.
    /// </summary>
    private static readonly string[] ScrubbedChildEnvNames =
    [
        "GH_TOKEN",
        "GITHUB_TOKEN",
        "GITHUB_CONFIG_REPO_TOKEN",
        AskpassEnvName,
        PromptDisabledEnvName,
        "SSH_ASKPASS",
        GcmInteractiveEnvName,
        CredentialEnvName,
    ];

    /// <summary>
    /// The operation-scoped git configuration appended to the child's environment config list,
    /// AFTER every inherited entry. <c>credential.helper</c> with an EMPTY value RESETS the
    /// accumulated helper list for this invocation only (inherited <c>get</c> and <c>store</c>
    /// alike).
    /// </summary>
    /// <remarks>
    /// <c>credential.interactive</c> is deliberately NOT set. Since git 2.47.0 it is a TOTAL askpass
    /// suppression: <c>credential_getpass()</c> returns before <c>credential_ask_one()</c> (the code
    /// that runs <c>GIT_ASKPASS</c>), so <c>credential.interactive=false</c> makes git fail with
    /// <c>fatal: unable to get password from user</c> without ever invoking this operation's askpass
    /// helper. Terminal prompting is prevented instead by <c>GIT_TERMINAL_PROMPT=0</c> and
    /// <c>GCM_INTERACTIVE=never</c>, which <see cref="BuildChildEnvironment"/> always sets.
    /// </remarks>
    private static readonly (string Key, string Value)[] OperationConfigEntries =
    [
        ("credential.helper", string.Empty),
    ];

    private int _disposed;

    private WorkerGitAskpassHelper(string directoryPath, string scriptPath)
    {
        DirectoryPath = directoryPath;
        ScriptPath = scriptPath;
    }

    /// <summary>The private temporary directory owned by this helper.</summary>
    internal string DirectoryPath { get; }

    /// <summary>The absolute path of the token-free helper script inside <see cref="DirectoryPath"/>.</summary>
    internal string ScriptPath { get; }

    /// <summary>
    /// Creates the per-operation helper directory and script.
    /// </summary>
    /// <param name="platformSupported">
    /// Decides whether this platform can execute the helper at all. <c>null</c> selects the real
    /// <see cref="OperatingSystem.IsLinux"/> predicate, so production behaviour is unchanged
    /// whenever no seam is installed.
    /// </param>
    /// <returns>
    /// The created helper, or <c>null</c> when the platform is not supported. A partially built
    /// helper directory is never left behind.
    /// </returns>
    internal static WorkerGitAskpassHelper? TryCreate(Func<bool>? platformSupported = null)
    {
        // FAIL CLOSED — no credential transport on an unsupported platform, never a tokenized URL.
        if (!(platformSupported?.Invoke() ?? OperatingSystem.IsLinux()))
            return null;

        var helperDir = Path.Combine(
            Path.GetTempPath(), $"{DirectoryPrefix}{Guid.NewGuid():N}");
        var scriptPath = Path.Combine(helperDir, ScriptFileName);

        // Ownership transfers to the returned instance ONLY when every step succeeded, so an
        // exception part-way through best-effort removes the partial directory.
        var owned = false;
        try
        {
            Directory.CreateDirectory(helperDir);

            // UTF-8 WITHOUT a BOM and with the trailing newline: /bin/sh must see `#!` as the
            // first two bytes of the file.
            File.WriteAllText(
                scriptPath, ScriptContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // On Windows (reachable only with a platform seam installed) Unix modes do not exist and
            // are skipped; the production path never gets here on anything but Linux.
            if (!OperatingSystem.IsWindows())
            {
                ApplyOwnerOnlyMode(scriptPath);
                ApplyOwnerOnlyMode(helperDir);
            }

            owned = true;
            return new WorkerGitAskpassHelper(helperDir, scriptPath);
        }
        finally
        {
            if (!owned)
                TryDeleteDirectory(helperDir);
        }
    }

    /// <summary>
    /// Builds the COMPLETE child environment for one authenticated launch: the current process
    /// environment with every credential-bearing channel scrubbed, plus the operation-scoped
    /// askpass wiring and the child-only credential variable.
    /// </summary>
    /// <remarks>
    /// Returning the complete dictionary (rather than a delta) is what makes a test seam that
    /// captures it observe the ACTUAL child environment, including the ABSENCE of the inherited
    /// token variables.
    /// </remarks>
    /// <param name="credential">The credential resolved for this operation.</param>
    /// <returns>The complete environment to hand the child process.</returns>
    internal IReadOnlyDictionary<string, string?> BuildChildEnvironment(string credential)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
                environment[key] = value;
        }

        foreach (var scrubbed in ScrubbedChildEnvNames)
            environment.Remove(scrubbed);

        environment[PromptDisabledEnvName] = "0";
        environment[GcmInteractiveEnvName] = GcmInteractiveNever;
        environment[AskpassEnvName] = ScriptPath;
        environment[CredentialEnvName] = credential;

        // APPEND to whatever indexed environment-config entries the host already supplied, so the
        // resets below come after them and no inherited indexed helper can be selected.
        var index = ParseInheritedConfigCount(environment);
        foreach (var (key, value) in OperationConfigEntries)
        {
            environment[$"GIT_CONFIG_KEY_{index.ToString(CultureInfo.InvariantCulture)}"] = key;
            environment[$"GIT_CONFIG_VALUE_{index.ToString(CultureInfo.InvariantCulture)}"] = value;
            index++;
        }

        environment[ConfigCountEnvName] = index.ToString(CultureInfo.InvariantCulture);

        // GIT_CONFIG_PARAMETERS is evaluated by git AFTER the indexed entries, so it OUTRANKS them:
        // an inherited `credential.helper=...` here would stay active and could both SELECT
        // credentials and PERSIST the askpass token. Helper suppression must be authoritative across
        // channels, so the inherited value is DISCARDED and replaced by ONLY the operation's own
        // resets (the same resets, expressed in the channel that has the final say). The replacement
        // lives in this child's environment only — no persistent or global git configuration is
        // touched.
        environment[ConfigParametersEnvName] = string.Join(
            ' ', OperationConfigEntries.Select(e => $"'{e.Key}={e.Value}'"));

        return environment;
    }

    /// <summary>
    /// The host's inherited <c>GIT_CONFIG_COUNT</c>, or <c>0</c> when absent/unparsable — the
    /// operation-scoped entries then simply start at the beginning of the list.
    /// </summary>
    private static int ParseInheritedConfigCount(IReadOnlyDictionary<string, string?> environment) =>
        environment.TryGetValue(ConfigCountEnvName, out var raw)
        && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
        && count > 0
            ? count
            : 0;

    /// <summary>
    /// Applies mode 0700 (owner read/write/execute) to ONE path. Declared unsupported on Windows so
    /// the analyzer enforces that every call site sits behind the explicit
    /// <see cref="OperatingSystem.IsWindows"/> guard.
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void ApplyOwnerOnlyMode(string path) =>
        File.SetUnixFileMode(
            path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    /// <summary>IDEMPOTENT, best-effort delete of the helper directory; never throws.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        TryDeleteDirectory(DirectoryPath);
    }

    /// <summary>Best-effort recursive delete; never throws.</summary>
    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // A leaked temporary directory must never fail the operation that owned it.
        }
    }
}

/// <summary>
/// The credential ONE authenticated worker-side target-repository operation runs with: the resolved
/// secret, the credential-free origin it must be used against, and the operation-scoped
/// <see cref="WorkerGitAskpassHelper"/> that carries it into the git child process.
/// </summary>
/// <remarks>
/// <para>
/// The type is the transport scope of ONE git launch. It is created immediately before that launch,
/// disposed by that launch (deleting the helper directory on success, failure AND cancellation), and
/// holds NO static state, so two concurrent assignments can never observe each other's credential.
/// </para>
/// <para>
/// <see cref="Credential"/> is deliberately NOT part of any message this type could produce: it is
/// only ever placed in the CHILD environment built by <see cref="BuildChildEnvironment"/>.
/// </para>
/// </remarks>
internal sealed class WorkerGitCredentialTransport : IWorkerGitCredentialTransport, IDisposable
{
    private readonly WorkerGitAskpassHelper _helper;

    private WorkerGitCredentialTransport(
        string credential, string sanitizedOriginUrl, WorkerGitAskpassHelper helper)
    {
        Credential = credential;
        SanitizedOriginUrl = sanitizedOriginUrl;
        _helper = helper;
    }

    /// <summary>The operation's resolved credential (child-environment only; never logged).</summary>
    internal string Credential { get; }

    /// <inheritdoc/>
    public string SanitizedOriginUrl { get; }

    /// <summary>The token-free helper script's path — the child's <c>GIT_ASKPASS</c> value.</summary>
    internal string ScriptPath => _helper.ScriptPath;

    /// <summary>
    /// Creates the operation-scoped transport for <paramref name="credential"/> against
    /// <paramref name="sanitizedOriginUrl"/>, or <c>null</c> when there is no credential to carry (a
    /// blank value means the operation runs unauthenticated).
    /// </summary>
    /// <remarks>
    /// THE ORIGIN GATE is defense in depth: <see cref="TargetRepositoryCredential.Split"/> already
    /// withholds a credential whose destination is ineligible, so a credential paired with a
    /// NONELIGIBLE sanitized origin can only arise from a programming error. It is refused rather than
    /// silently accepted, because the transport's contract is that a credential is ever carried against
    /// an eligible origin only.
    /// </remarks>
    /// <exception cref="GitOperationException">
    /// A credential WAS resolved but this platform cannot execute the helper, or the sanitized origin is
    /// not an eligible HTTPS <c>github.com:443</c> destination. Either is a fail-closed refusal: the
    /// operation is never downgraded to a tokenized URL or to a silent unauthenticated run, and the
    /// message carries no credential.
    /// </exception>
    internal static WorkerGitCredentialTransport? Create(
        string? credential, string sanitizedOriginUrl)
    {
        if (string.IsNullOrWhiteSpace(credential))
            return null;

        if (!ConfigRepoGitOperations.IsEligibleTransportUrl(sanitizedOriginUrl))
        {
            throw new GitOperationException(
                "The repository destination is not an eligible HTTPS github.com:443 URL; "
                + "refusing to release the repository credential.");
        }

        var helper = WorkerGitAskpassHelper.TryCreate();
        if (helper is null)
        {
            throw new GitOperationException(
                "The operation-scoped git credential helper is unavailable on this platform; "
                + "refusing to run an authenticated git operation.");
        }

        return new WorkerGitCredentialTransport(credential, sanitizedOriginUrl, helper);
    }

    /// <summary>
    /// The complete CHILD environment for one authenticated launch. Built per invocation so the
    /// credential is never held in any long-lived (or process-wide) environment.
    /// </summary>
    public IReadOnlyDictionary<string, string?> BuildChildEnvironment() =>
        _helper.BuildChildEnvironment(Credential);

    /// <summary>Best-effort helper-directory cleanup; idempotent and never throws.</summary>
    public void Dispose() => _helper.Dispose();
}

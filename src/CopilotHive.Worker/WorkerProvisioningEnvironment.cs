namespace CopilotHive.Worker;

/// <summary>
/// THE WORKER PROCESS'S ENVIRONMENT PROVENANCE: the ONE operator snapshot, the set of variables a
/// provisioner currently owns, and the environment read/write delegates those two are expressed in
/// terms of.
/// <para>
/// <b>Why it is a separate object.</b> The snapshot's whole purpose is to answer "did the OPERATOR
/// set this variable before any provisioning happened?". That question is about the PROCESS, not
/// about one connection attempt: the worker process runs a SEQUENCE of connection attempts
/// (a fresh <see cref="WorkerService"/> per attempt, per the new-service-per-attempt policy in
/// <c>Program.cs</c>), and an attempt that provisions a value into the environment must never be
/// re-read by a LATER attempt as if the operator had supplied it. If each attempt owned its own
/// snapshot, attempt B would capture attempt A's server-provisioned values as operator overrides —
/// promoting provisioned state into operator authority. Sharing THIS object across sequential
/// attempts keeps ORIGINAL operator provenance authoritative for the whole process.
/// </para>
/// <para>
/// <b>Scope.</b> Only environment provenance is shared. Everything else stays per-attempt: each
/// <see cref="WorkerConnection"/> keeps its OWN assigned identity, checked fetch, provisioner,
/// config-repository URL and provisioned-GitHub-token RESPONSE provenance. No transport delegate
/// is shared or rebound, and there is no static/global state and no new environment variable.
/// </para>
/// <para>
/// <b>Lifecycle.</b> The operator snapshot is captured LAZILY, before the first provisioning call,
/// and exactly ONCE — the first caller wins and it is never retaken. A variable that held a
/// non-whitespace value in that snapshot is an OPERATOR value and is never replaced or cleared;
/// everything else is provisioned space, where a later response REPLACES a previously-provisioned
/// value and CLEARS one that is no longer provisioned.
/// </para>
/// <para>
/// <b>Sequential quiescent attempts only.</b> There is deliberately NO synchronization here (no
/// locks, no concurrency-support machinery): the worker runs one attempt at a time, and the
/// previous attempt is retired and drained before the next one starts. Concurrent independent
/// workers mutating one process environment are NOT a supported contract.
/// </para>
/// </summary>
internal sealed class WorkerProvisioningEnvironment
{
    /// <summary>Environment reader. Defaults to the process environment.</summary>
    private readonly Func<string, string?> _readEnv;

    /// <summary>Environment writer. Defaults to the process environment.</summary>
    private readonly Action<string, string?> _writeEnv;

    /// <summary>
    /// The process environment as it looked BEFORE the first provisioning call. Populated exactly
    /// once by <see cref="EnsureSnapshot"/>; <c>null</c> until then.
    /// </summary>
    private Dictionary<string, string?>? _operatorSnapshot;

    /// <summary>Variables currently holding a value written by a provisioner.</summary>
    private readonly HashSet<string> _provisionedVars = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates the process-lifetime environment provenance.
    /// </summary>
    /// <param name="readEnv">Environment reader seam. <c>null</c> selects the process environment.</param>
    /// <param name="writeEnv">Environment writer seam. <c>null</c> selects the process environment.</param>
    internal WorkerProvisioningEnvironment(
        Func<string, string?>? readEnv = null,
        Action<string, string?>? writeEnv = null)
    {
        _readEnv = readEnv ?? Environment.GetEnvironmentVariable;
        _writeEnv = writeEnv ?? ((name, value) => Environment.SetEnvironmentVariable(name, value));
    }

    /// <summary>Whether the operator snapshot has been taken (lazily, exactly once).</summary>
    internal bool HasSnapshot => _operatorSnapshot is not null;

    /// <summary>
    /// Captures the operator-provided environment exactly once, BEFORE the first provisioning call,
    /// so no later attempt can mistake a previously-provisioned value for an operator override.
    /// <paramref name="names"/> is only consulted for the FIRST caller — the snapshot is never
    /// retaken, so later calls are no-ops.
    /// </summary>
    /// <param name="names">The variable names the snapshot tracks.</param>
    internal void EnsureSnapshot(IReadOnlyList<string> names)
    {
        if (_operatorSnapshot is not null) return;

        var snapshot = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in names)
            snapshot[name] = Normalize(_readEnv(name));

        _operatorSnapshot = snapshot;
    }

    /// <summary>
    /// Whether the operator supplied this variable before any provisioning happened.
    /// </summary>
    /// <param name="name">The variable name.</param>
    internal bool IsOperatorProvided(string name) => OperatorValue(name) is not null;

    /// <summary>
    /// The operator-supplied value for a variable, normalized (whitespace is absence), or
    /// <c>null</c> when the operator never set it — which is the value a revert writes back and
    /// also the value meaning "remove the variable entirely".
    /// </summary>
    /// <param name="name">The variable name.</param>
    internal string? OperatorValue(string name) =>
        _operatorSnapshot is not null && _operatorSnapshot.TryGetValue(name, out var value)
            ? value
            : null;

    /// <summary>Reads a live environment value through the owned reader.</summary>
    /// <param name="name">The variable name.</param>
    internal string? Read(string name) => _readEnv(name);

    /// <summary>
    /// Writes a live environment value through the owned writer. <c>null</c> removes the variable.
    /// </summary>
    /// <param name="name">The variable name.</param>
    /// <param name="value">The value, or <c>null</c> to remove it.</param>
    internal void Write(string name, string? value) => _writeEnv(name, value);

    /// <summary>Registers a variable as currently provisioned by a provisioner.</summary>
    /// <param name="name">The variable name.</param>
    internal void MarkProvisioned(string name) => _provisionedVars.Add(name);

    /// <summary>
    /// Unregisters a variable from the provisioned set.
    /// </summary>
    /// <param name="name">The variable name.</param>
    /// <returns><c>true</c> when it WAS registered — i.e. a provisioner wrote it.</returns>
    internal bool UnmarkProvisioned(string name) => _provisionedVars.Remove(name);

    /// <summary>A snapshot of the currently provisioned variable NAMES.</summary>
    internal IReadOnlyList<string> ProvisionedNames => [.. _provisionedVars];

    /// <summary>Treats whitespace as absence.</summary>
    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

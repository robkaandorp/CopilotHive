namespace CopilotHive.Services;

/// <summary>
/// ONE phase's completed report of a single iteration, as handed from the orchestrator to a
/// worker through <see cref="PhaseReportMetadata"/>.
/// </summary>
/// <param name="Phase">
/// The <c>GoalPhase</c> name as a string — <c>Coding</c>, <c>Testing</c>, <c>Review</c>,
/// <c>DocWriting</c> or <c>Improve</c>. The shared project has no phase enum type, so the name
/// travels as a plain string and is never re-typed here.
/// </param>
/// <param name="Occurrence">1-based occurrence index of that phase within the iteration.</param>
/// <param name="Report">
/// The worker's report, carried VERBATIM — never trimmed, never truncated and never capped.
/// </param>
public sealed record PhaseReport(string Phase, int Occurrence, string Report);

/// <summary>
/// THE SINGLE DEFINITION of the phase-report metadata contract, shared by the orchestrator (which
/// writes it onto every dispatched task) and the worker (which reads it back for this assignment).
/// Neither side hand-builds or hand-parses the keys.
/// </summary>
/// <remarks>
/// One entry per report, keyed <c>phase_report:{Phase}:{Occurrence}</c>. The metadata dictionary
/// travels over the existing gRPC <c>WorkTask.Metadata</c> mapping unchanged, so this file is the
/// only place that needs to know the key layout.
/// </remarks>
public static class PhaseReportMetadata
{
    /// <summary>
    /// The key prefix of every phase-report entry: <c>phase_report:</c>. A key without it is not
    /// part of this contract and is ignored by <see cref="Read"/>.
    /// </summary>
    public const string KeyPrefix = "phase_report:";

    /// <summary>
    /// Writes one metadata entry per report, keyed <c>phase_report:{Phase}:{Occurrence}</c> with
    /// the report as the value.
    /// </summary>
    /// <remarks>
    /// Two reports of the same phase with different occurrences therefore produce two keys, and a
    /// report is written EXACTLY as supplied — the value is never trimmed or shortened.
    /// </remarks>
    /// <param name="metadata">The task metadata dictionary to write into.</param>
    /// <param name="reports">The reports to write; nothing is written for an empty sequence.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="metadata"/> or <paramref name="reports"/> is null.</exception>
    public static void Write(IDictionary<string, string> metadata, IEnumerable<PhaseReport> reports)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(reports);

        foreach (var report in reports)
            metadata[$"{KeyPrefix}{report.Phase}:{report.Occurrence}"] = report.Report;
    }

    /// <summary>
    /// Reads every well-formed phase-report entry out of <paramref name="metadata"/>.
    /// </summary>
    /// <remarks>
    /// A key is read only when it carries <see cref="KeyPrefix"/>, has exactly the two
    /// <c>:</c>-separated segments of the contract (a non-empty phase and the occurrence) and its
    /// occurrence segment parses as an integer. Every other key — unrelated metadata, a truncated
    /// key, or a malformed occurrence value — is ignored rather than guessed at. The phase token is
    /// carried through verbatim; matching it against the known phase names is the reader's concern.
    /// <para>
    /// The entries are returned in the order <paramref name="metadata"/> enumerates them, which for
    /// a dictionary written by <see cref="Write"/> is the order the reports were written.
    /// </para>
    /// </remarks>
    /// <param name="metadata">The task metadata to read from.</param>
    /// <returns>A fresh list of the reports found; empty when there are none.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="metadata"/> is null.</exception>
    public static IReadOnlyList<PhaseReport> Read(IReadOnlyDictionary<string, string> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var reports = new List<PhaseReport>();
        foreach (var (key, value) in metadata)
        {
            if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                continue;

            var segments = key[KeyPrefix.Length..].Split(':');
            if (segments.Length != 2 || segments[0].Length == 0)
                continue;
            if (!int.TryParse(segments[1], out var occurrence))
                continue;

            reports.Add(new PhaseReport(segments[0], occurrence, value));
        }

        return reports;
    }
}

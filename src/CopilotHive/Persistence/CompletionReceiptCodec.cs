using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using CopilotHive.Services;
using CopilotHive.Workers;

namespace CopilotHive.Persistence;

/// <summary>
/// Explicit, versioned <see cref="System.Text.Json"/> encoder/decoder for a
/// <see cref="CompletionReceipt"/>.
/// <para>
/// THE FORMAT is a version-1 envelope over the EXISTING domain values
/// (<see cref="WorkSlot"/>, <see cref="WorkSlotPosition"/>, <see cref="WorkerRole"/>,
/// <see cref="GoalPhase"/>, <see cref="TaskResult"/>, <see cref="TaskMetrics"/>,
/// <see cref="GitChangeSummary"/>, <see cref="TaskOutcome"/>) — no second domain model is
/// introduced. Output is COMPACT (no indentation, no trailing newline) with this exact member order,
/// and enum values are the canonical lowercase names produced by the existing <c>ToRoleName</c>
/// mapping:
/// </para>
/// <code>
/// {
///   "version": 1,
///   "goalId": "g1",
///   "workerId": "w1",
///   "role": "coder",
///   "slot": {
///     "taskId": "t1",
///     "position": { "iteration": 1, "phase": "coding", "occurrence": 1 },
///     "attempt": 2
///   },
///   "result": {
///     "taskId": "t1",
///     "status": "completed",
///     "output": "...",
///     "model": "...",
///     "iterationStartSha": "abc" | null,
///     "metrics": {
///       "verdict": "PASS",
///       "buildSuccess": true,
///       "totalTests": 10,
///       "passedTests": 9,
///       "failedTests": 1,
///       "coveragePercent": 87.5,
///       "issues": [],
///       "summary": "..."
///     } | null,
///     "gitStatus": {
///       "filesChanged": 2,
///       "insertions": 10,
///       "deletions": 1,
///       "pushed": true,
///       "changedFiles": []
///     } | null
///   }
/// }
/// </code>
/// <para>
/// THE VERSION MARKER LIVES ONLY INSIDE THE PAYLOAD. There is no version column, so the envelope's
/// <c>version</c> is the single source of format truth; <see cref="Version"/> is the only version
/// this codec writes and accepts.
/// </para>
/// <para>
/// DECODE REJECTS, IT NEVER INVENTS. Malformed JSON, a JSON <c>null</c> or non-object root, a
/// missing/unsupported/non-integer <c>version</c>, a missing or <c>null</c> required member, a
/// <c>null</c> list or list element, an unknown property, a DUPLICATE property name (even with equal
/// values, at every object level), an ordinal case-variant PROPERTY NAME, a numeric or aliased enum
/// token, a non-canonical non-finite coverage token and an out-of-range numeric coverage token are
/// all hard failures — never a silent default, never a repair, never a truncation, never a
/// "best effort" salvage. In particular a status is NEVER defaulted to
/// <see cref="TaskOutcome.Completed"/> and a verdict is NEVER defaulted to <c>"PASS"</c>.
/// </para>
/// <para>
/// ENUM LABEL CASE-INSENSITIVITY APPLIES TO VALUES ONLY. A <c>phase</c>, <c>role</c> or
/// <c>status</c> label may use an ordinal-ignore-case variant of its canonical lowercase name
/// (<c>"Coding"</c>, <c>"CODER"</c>, <c>"Completed"</c>), and is normalized to lowercase on output;
/// numeric tokens (<c>1</c> or <c>"1"</c>), aliases and unknown labels are refused. Property NAMES
/// are always ordinal case-sensitive.
/// </para>
/// <para>
/// NON-FINITE COVERAGE IS EXPLICIT AND NARROWLY ADMITTED. A non-finite
/// <see cref="TaskMetrics.CoveragePercent"/> is written as the exact JSON string <c>"NaN"</c>,
/// <c>"Infinity"</c> or <c>"-Infinity"</c>; finite values remain JSON numbers. Those three spellings
/// are the only accepted string forms, and a numeric token that OVERFLOWS <see cref="double"/> (such
/// as <c>1e400</c>) is refused rather than being silently coerced into an infinity — the conversion
/// is owned by <see cref="CoverageConverter"/>, not by a permissive global number policy.
/// </para>
/// <para>
/// TEXT IS ENCODED VERBATIM OR REFUSED. Every string this envelope writes must survive the UTF-8
/// transcode unchanged, so a string containing an UNPAIRED UTF-16 surrogate is refused at encode
/// time rather than being silently replaced with U+FFFD. Substituting would corrupt evidence and
/// would let two DISTINCT values collapse onto the same canonical text, producing a false duplicate
/// in the ordinal comparison below. A well-formed surrogate pair (any non-BMP character) is accepted
/// and preserved exactly; the decode side rejects unpaired surrogate escapes for the same reason.
/// </para>
/// <para>
/// CANONICALIZATION. Decoding returns freshly allocated domain records and lists (a deep copy at the
/// boundary — no caller alias into any encoded source), and re-encoding that result reproduces the
/// canonical text. Therefore <c>Encode(Decode(p))</c> and <c>Encode(candidate)</c> can be compared
/// ORDINALLY: two payloads are the same receipt exactly when those canonical texts are equal, no
/// matter how the incoming JSON was formatted, ordered, or enum-cased. First-stored time is NOT part
/// of the payload and NOT part of that comparison — it lives in a separate column.
/// </para>
/// <para>
/// DOMAIN VALIDATION IS RE-APPLIED, NOT DUPLICATED. Decoding materializes the receipt through the
/// <see cref="CompletionReceipt"/> constructor, so the carrier's own internal-consistency rules
/// (nonblank identities, matching task IDs, positive iteration/occurrence/attempt, worker-backed
/// phase, phase-mapped role) are enforced on decoded input by the single existing implementation.
/// A SUCCESSFUL DECODE IS NOT DURABLE AUTHORIZATION — it only means the bytes were well-formed and
/// internally consistent.
/// </para>
/// </summary>
internal static class CompletionReceiptCodec
{
    /// <summary>The only envelope version this codec writes and accepts.</summary>
    internal const int Version = 1;

    private const string VersionProperty = "version";
    private const string GoalIdProperty = "goalId";
    private const string WorkerIdProperty = "workerId";
    private const string RoleProperty = "role";
    private const string SlotProperty = "slot";
    private const string ResultProperty = "result";
    private const string TaskIdProperty = "taskId";
    private const string PositionProperty = "position";
    private const string IterationProperty = "iteration";
    private const string PhaseProperty = "phase";
    private const string OccurrenceProperty = "occurrence";
    private const string AttemptProperty = "attempt";
    private const string StatusProperty = "status";
    private const string OutputProperty = "output";
    private const string ModelProperty = "model";
    private const string IterationStartShaProperty = "iterationStartSha";
    private const string MetricsProperty = "metrics";
    private const string VerdictProperty = "verdict";
    private const string BuildSuccessProperty = "buildSuccess";
    private const string TotalTestsProperty = "totalTests";
    private const string PassedTestsProperty = "passedTests";
    private const string FailedTestsProperty = "failedTests";
    private const string CoveragePercentProperty = "coveragePercent";
    private const string IssuesProperty = "issues";
    private const string SummaryProperty = "summary";
    private const string GitStatusProperty = "gitStatus";
    private const string FilesChangedProperty = "filesChanged";
    private const string InsertionsProperty = "insertions";
    private const string DeletionsProperty = "deletions";
    private const string PushedProperty = "pushed";
    private const string ChangedFilesProperty = "changedFiles";

    /// <summary>The canonical JSON string for a <see cref="double.NaN"/> coverage value.</summary>
    internal const string NaNToken = "NaN";
    /// <summary>The canonical JSON string for a <see cref="double.PositiveInfinity"/> coverage value.</summary>
    internal const string PositiveInfinityToken = "Infinity";
    /// <summary>The canonical JSON string for a <see cref="double.NegativeInfinity"/> coverage value.</summary>
    internal const string NegativeInfinityToken = "-Infinity";

    /// <summary>
    /// The worker-backed phases this envelope may carry, in the canonical order used for label
    /// recognition. The remaining phases (Planning, Merging, Done, Failed) have no worker and can
    /// never appear in a worker completion.
    /// </summary>
    private static readonly GoalPhase[] WorkerPhases =
        [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.DocWriting, GoalPhase.Improve];

    /// <summary>
    /// The worker-backed roles this envelope may carry, in the canonical order used for label
    /// recognition. <see cref="WorkerRole.Unspecified"/>, <see cref="WorkerRole.Orchestrator"/> and
    /// <see cref="WorkerRole.MergeWorker"/> are deliberately absent.
    /// </summary>
    private static readonly WorkerRole[] WorkerRoles =
        [WorkerRole.Coder, WorkerRole.Tester, WorkerRole.Reviewer, WorkerRole.DocWriter, WorkerRole.Improver];

    /// <summary>
    /// THE READ SETTINGS, and what each one is responsible for:
    /// <list type="bullet">
    ///   <item><description><c>AllowDuplicateProperties = false</c> — a duplicate property name is
    ///     refused at EVERY object level, including duplicates carrying equal values.</description></item>
    ///   <item><description><c>UnmappedMemberHandling = Disallow</c> — an unknown property is refused
    ///     at every object level.</description></item>
    ///   <item><description><c>PropertyNameCaseInsensitive = false</c> — property names are ordinal
    ///     case-sensitive, so only the enum VALUES accept case variants (validated by name,
    ///     below).</description></item>
    ///   <item><description><c>RespectNullableAnnotations = true</c> — an explicit <c>null</c> in a
    ///     non-nullable member is refused, which is what makes the nullable members
    ///     (<c>iterationStartSha</c>, <c>metrics</c>, <c>gitStatus</c>) the ONLY ones that may be
    ///     <c>null</c>. Every member is <c>required</c>, so absence is refused separately by the
    ///     serializer, and <see cref="JsonRequiredAttribute"/> additionally pins the three
    ///     nullable members as present-but-may-be-null.</description></item>
    /// </list>
    /// Number strictness is deliberately NOT configured globally: <see cref="CoverageConverter"/>
    /// owns the one property that has any latitude, and the default strict behaviour for every other
    /// numeric member is what refuses numeric strings and out-of-range integers. A global
    /// <c>NumberHandling</c> here would be inert (the per-property converter wins) and would imply a
    /// laxness this format does not have.
    /// </summary>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
    };

    /// <summary>
    /// THE WRITE SETTINGS: compact output (no indentation, no trailing newline) and the relaxed
    /// encoder, so evidence text is emitted as readable characters rather than <c>\uXXXX</c> escapes
    /// for HTML-sensitive and non-ASCII characters. Only the characters JSON requires are escaped, so
    /// the canonical text stays a faithful, compact rendering of the stored evidence.
    /// </summary>
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The single shared instance of <see cref="StringListConverter"/>, used on the WRITE side so the
    /// two evidence lists are emitted by the same converter that reads them. The write path ignores
    /// the options argument, and the outer <see cref="Utf8JsonWriter"/> still supplies the relaxed
    /// encoder, so the canonical text is unchanged.
    /// </summary>
    private static readonly StringListConverter EvidenceListConverter = new();

    /// <summary>
    /// Encodes a receipt into the canonical version-1 envelope.
    /// <para>
    /// THE DETACH COMES FIRST, THEN THE VALIDATION, THEN THE WRITE. <see cref="Capture"/> copies the
    /// whole receipt graph — including every caller-owned mutable collection — into a PRIVATE
    /// snapshot before anything is inspected. <see cref="Validate"/> then examines ONLY that snapshot,
    /// and <see cref="WriteReceipt"/> writes ONLY that snapshot. The caller's objects are read exactly
    /// once, during the capture, and never again; a mutation the caller makes after
    /// <see cref="Encode"/> has begun therefore cannot change the output and cannot slip a null or an
    /// unpaired surrogate past the validation into the writer.
    /// </para>
    /// <para>
    /// THE RECEIPT IS VALIDATED BEFORE A SINGLE BYTE IS WRITTEN: every rule in <see cref="Validate"/>
    /// runs first, so a refused receipt produces no partial output and no stream is left half
    /// written. The result is compact JSON with the fixed member order documented on this type, and
    /// it is deterministic — encoding the same values twice yields byte-identical text.
    /// </para>
    /// </summary>
    /// <param name="receipt">The receipt to encode.</param>
    /// <returns>The canonical compact JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="receipt"/> is <c>null</c>.</exception>
    /// <exception cref="CompletionReceiptCodecException">The receipt cannot be represented.</exception>
    internal static string Encode(CompletionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        // (1) THE DETACH. The caller's graph is read HERE and nowhere else.
        var snapshot = Capture(receipt);

        // (2) Validate the CAPTURED graph FIRST: a refusal must never emit partial JSON.
        Validate(snapshot);

        // (3) Write the CAPTURED graph. No caller-owned object is reachable from this point on.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteReceipt(writer, snapshot);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// THE DETACH BOUNDARY: copies the entire receipt graph into a private
    /// <see cref="ReceiptSnapshot"/>, so validation and writing can never observe a later caller
    /// mutation.
    /// <para>
    /// EVERY caller-owned mutable collection the encoder reads is COPIED here —
    /// <see cref="TaskMetrics.Issues"/> and <see cref="GitChangeSummary.ChangedFiles"/> — into fresh
    /// lists this class alone holds. Every other member the envelope writes is either an immutable
    /// <see cref="string"/> or a value type, and is copied by value into the snapshot; even the
    /// immutable <see cref="WorkSlotPosition"/> record is flattened into scalars, so no field of the
    /// caller's graph remains reachable from the snapshot at all. A future field that added another
    /// caller-owned collection would have to be copied here too in order to be written.
    /// </para>
    /// <para>
    /// IT VALIDATES NOTHING AND REPAIRS NOTHING. A <c>null</c> reference, a <c>null</c> list and a
    /// <c>null</c> element are all carried into the snapshot exactly as found, so
    /// <see cref="Validate"/> remains the single place that decides what is refused and produces the
    /// same diagnostics it always did.
    /// </para>
    /// </summary>
    /// <param name="receipt">The caller's receipt; read once, here.</param>
    /// <returns>A snapshot that shares no mutable state with the caller.</returns>
    private static ReceiptSnapshot Capture(CompletionReceipt receipt)
    {
        var slot = receipt.Slot;
        var position = slot?.Position;
        var result = receipt.Result;
        var metrics = result?.Metrics;
        var gitStatus = result?.GitStatus;

        return new ReceiptSnapshot
        {
            GoalId = receipt.GoalId,
            WorkerId = receipt.WorkerId,
            Role = receipt.Role,
            Slot = slot is null
                ? null
                : new SlotSnapshot
                {
                    TaskId = slot.TaskId,
                    Position = position is null
                        ? null
                        : new PositionSnapshot
                        {
                            Iteration = position.Iteration,
                            Phase = position.Phase,
                            Occurrence = position.Occurrence,
                        },
                    Attempt = slot.Attempt,
                },
            Result = result is null
                ? null
                : new ResultSnapshot
                {
                    TaskId = result.TaskId,
                    Status = result.Status,
                    Output = result.Output,
                    Model = result.Model,
                    IterationStartSha = result.IterationStartSha,
                    Metrics = metrics is null
                        ? null
                        : new MetricsSnapshot
                        {
                            Verdict = metrics.Verdict,
                            BuildSuccess = metrics.BuildSuccess,
                            TotalTests = metrics.TotalTests,
                            PassedTests = metrics.PassedTests,
                            FailedTests = metrics.FailedTests,
                            CoveragePercent = metrics.CoveragePercent,
                            // THE COPY: a fresh list, so a later Add/Clear/indexer write on the
                            // caller's own list cannot reach the validation or the writer. A null
                            // list stays null for Validate to refuse with its own message.
                            Issues = metrics.Issues is null ? null : new List<string>(metrics.Issues),
                            Summary = metrics.Summary,
                        },
                    GitStatus = gitStatus is null
                        ? null
                        : new GitSnapshot
                        {
                            FilesChanged = gitStatus.FilesChanged,
                            Insertions = gitStatus.Insertions,
                            Deletions = gitStatus.Deletions,
                            Pushed = gitStatus.Pushed,
                            // THE COPY, for the same reason as Issues above.
                            ChangedFiles = gitStatus.ChangedFiles is null ? null : new List<string>(gitStatus.ChangedFiles),
                        },
                },
        };
    }

    /// <summary>
    /// Decodes a canonical version-1 envelope back into a receipt built entirely from freshly
    /// allocated domain records and lists.
    /// <para>
    /// The returned receipt is materialized through the <see cref="CompletionReceipt"/> constructor,
    /// so the carrier's internal-consistency rules apply to decoded input as well. The payload's
    /// bytes are not retained and are never aliased: the result shares no list with the input.
    /// </para>
    /// </summary>
    /// <param name="json">The encoded payload.</param>
    /// <returns>A detached receipt carrying exactly the encoded values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <c>null</c>.</exception>
    /// <exception cref="CompletionReceiptCodecException">
    /// The payload is malformed, is a JSON null/non-object root, carries a missing, non-integer or
    /// unsupported <c>version</c>, contains an unknown or duplicate property, is missing any required
    /// member, carries a <c>null</c> where a value is required, uses a non-canonical enum label or a
    /// non-canonical non-finite coverage token, or violates the carrier's internal-consistency rules.
    /// </exception>
    internal static CompletionReceipt Decode(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        WireReceipt wire;
        try
        {
            wire = JsonSerializer.Deserialize<WireReceipt>(json, ReadOptions)
                ?? throw new CompletionReceiptCodecException("The completion receipt payload is a JSON null root.");
        }
        catch (JsonException ex)
        {
            throw new CompletionReceiptCodecException(
                $"The completion receipt payload is not valid canonical v1 JSON: {ex.Message}", ex);
        }

        if (wire.Version != Version)
        {
            throw new CompletionReceiptCodecException(
                $"Unsupported completion receipt payload version {wire.Version}; only version {Version} is supported.");
        }

        return Materialize(wire);
    }

    /// <summary>
    /// Builds the canonical wire object from the CAPTURED snapshot. The caller must already have
    /// validated it (see <see cref="Validate"/>); the writes below therefore assume every required
    /// value is present and only re-check what would otherwise corrupt the output.
    /// <para>
    /// It takes a <see cref="ReceiptSnapshot"/>, never a <see cref="CompletionReceipt"/>, so the
    /// writer physically cannot reach a caller-owned object.
    /// </para>
    /// </summary>
    private static void WriteReceipt(Utf8JsonWriter writer, ReceiptSnapshot receipt)
    {
        var slot = receipt.Slot!;
        var position = slot.Position!;
        var result = receipt.Result!;

        writer.WriteStartObject();
        writer.WriteNumber(VersionProperty, Version);
        writer.WriteString(GoalIdProperty, receipt.GoalId);
        writer.WriteString(WorkerIdProperty, receipt.WorkerId);
        writer.WriteString(RoleProperty, RoleLabel(receipt.Role));

        writer.WriteStartObject(SlotProperty);
        writer.WriteString(TaskIdProperty, slot.TaskId);
        writer.WriteStartObject(PositionProperty);
        writer.WriteNumber(IterationProperty, position.Iteration);
        writer.WriteString(PhaseProperty, PhaseLabel(position.Phase));
        writer.WriteNumber(OccurrenceProperty, position.Occurrence);
        writer.WriteEndObject();
        writer.WriteNumber(AttemptProperty, slot.Attempt);
        writer.WriteEndObject();

        writer.WriteStartObject(ResultProperty);
        writer.WriteString(TaskIdProperty, result.TaskId);
        writer.WriteString(StatusProperty, StatusLabel(result.Status));
        writer.WriteString(OutputProperty, result.Output);
        writer.WriteString(ModelProperty, result.Model);
        WriteNullableString(writer, IterationStartShaProperty, result.IterationStartSha);
        WriteMetrics(writer, result.Metrics);
        WriteGitStatus(writer, result.GitStatus);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes the <c>metrics</c> member from the CAPTURED snapshot: JSON <c>null</c> when absent,
    /// otherwise an object carrying EVERY <see cref="TaskMetrics"/> member in the fixed order (the
    /// members are always present, so a non-null metrics object is never abbreviated). The
    /// <c>issues</c> array is written from the snapshot's OWN list copy, never from the caller's.
    /// </summary>
    private static void WriteMetrics(Utf8JsonWriter writer, MetricsSnapshot? metrics)
    {
        if (metrics is null)
        {
            writer.WriteNull(MetricsProperty);
            return;
        }

        writer.WriteStartObject(MetricsProperty);
        writer.WriteString(VerdictProperty, metrics.Verdict);
        writer.WriteBoolean(BuildSuccessProperty, metrics.BuildSuccess);
        writer.WriteNumber(TotalTestsProperty, metrics.TotalTests);
        writer.WriteNumber(PassedTestsProperty, metrics.PassedTests);
        writer.WriteNumber(FailedTestsProperty, metrics.FailedTests);
        writer.WritePropertyName(CoveragePercentProperty);
        CoverageConverterInstance.Write(writer, metrics.CoveragePercent, ReadOptions);
        writer.WritePropertyName(IssuesProperty);
        EvidenceListConverter.Write(writer, metrics.Issues!, ReadOptions);
        writer.WriteString(SummaryProperty, metrics.Summary);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes the <c>gitStatus</c> member from the CAPTURED snapshot: JSON <c>null</c> when absent,
    /// otherwise an object carrying EVERY <see cref="GitChangeSummary"/> member in the fixed order.
    /// The <c>changedFiles</c> array is written from the snapshot's OWN list copy, never from the
    /// caller's.
    /// </summary>
    private static void WriteGitStatus(Utf8JsonWriter writer, GitSnapshot? gitStatus)
    {
        if (gitStatus is null)
        {
            writer.WriteNull(GitStatusProperty);
            return;
        }

        writer.WriteStartObject(GitStatusProperty);
        writer.WriteNumber(FilesChangedProperty, gitStatus.FilesChanged);
        writer.WriteNumber(InsertionsProperty, gitStatus.Insertions);
        writer.WriteNumber(DeletionsProperty, gitStatus.Deletions);
        writer.WriteBoolean(PushedProperty, gitStatus.Pushed);
        writer.WritePropertyName(ChangedFilesProperty);
        EvidenceListConverter.Write(writer, gitStatus.ChangedFiles!, ReadOptions);
        writer.WriteEndObject();
    }

    /// <summary>
    /// The single shared instance of <see cref="CoverageConverter"/>, used on the WRITE side so the
    /// coverage member is emitted by the same converter that reads it. The write path ignores the
    /// options argument.
    /// </summary>
    private static readonly CoverageConverter CoverageConverterInstance = new();

    /// <summary>Writes a JSON string, or a JSON <c>null</c> when <paramref name="value"/> is <c>null</c>.</summary>
    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
            writer.WriteNull(propertyName);
        else
            writer.WriteString(propertyName, value);
    }

    /// <summary>
    /// THE ENCODE-SIDE VALIDATION, run in full before any byte is written. It refuses every value the
    /// format cannot represent faithfully — a null domain reference, a null position, a null string
    /// where a value is required, a null list or list element, text that would not survive the UTF-8
    /// transcode verbatim, or an unrepresentable role/phase — with
    /// <see cref="CompletionReceiptCodecException"/>. Nothing is defaulted, repaired or dropped.
    /// <para>
    /// IT EXAMINES THE CAPTURED SNAPSHOT, NOT THE CALLER'S GRAPH. That is what closes the
    /// validate-then-write window: the list this method walks is the very same private copy the
    /// writer will enumerate, so an element the caller injects afterwards is neither seen here nor
    /// written there.
    /// </para>
    /// <para>
    /// The role and phase checks are BELT-AND-BRACES: <see cref="CompletionReceipt"/> already
    /// guarantees a worker-backed, phase-mapped role, so on a receipt built through that constructor
    /// they can never fire. They are kept so that a future carrier change cannot silently widen what
    /// this envelope writes.
    /// </para>
    /// </summary>
    private static void Validate(ReceiptSnapshot receipt)
    {
        if (receipt.GoalId is null)
            throw new CompletionReceiptCodecException("Cannot encode a receipt whose goal id is null.");
        if (receipt.WorkerId is null)
            throw new CompletionReceiptCodecException("Cannot encode a receipt whose worker id is null.");
        if (receipt.Slot is null)
            throw new CompletionReceiptCodecException("Cannot encode a receipt whose slot is null.");
        if (receipt.Result is null)
            throw new CompletionReceiptCodecException("Cannot encode a receipt whose result is null.");

        var slot = receipt.Slot;
        if (slot.TaskId is null)
            throw new CompletionReceiptCodecException("Cannot encode a receipt whose slot task id is null.");
        if (slot.Position is null)
            throw new CompletionReceiptCodecException("Cannot encode a receipt whose slot position is null.");

        var result = receipt.Result;
        if (result.TaskId is null)
            throw new CompletionReceiptCodecException("Cannot encode a receipt whose result task id is null.");
        if (result.Output is null)
            throw new CompletionReceiptCodecException("Cannot encode a receipt whose result output is null.");
        if (result.Model is null)
            throw new CompletionReceiptCodecException("Cannot encode a receipt whose result model is null.");

        // The labels are computed here so an unrepresentable role, phase or status is reported
        // BEFORE the writer starts, rather than halfway through the object.
        _ = RoleLabel(receipt.Role);
        _ = PhaseLabel(slot.Position.Phase);
        _ = StatusLabel(result.Status);

        // TEXT ENCODABILITY. Every string this envelope writes must survive the UTF-8 transcode
        // verbatim; an unpaired surrogate would be silently replaced with U+FFFD by the writer.
        RequireEncodableText(receipt.GoalId, GoalIdProperty);
        RequireEncodableText(receipt.WorkerId, WorkerIdProperty);
        RequireEncodableText(slot.TaskId, $"slot.{TaskIdProperty}");
        RequireEncodableText(result.TaskId, $"result.{TaskIdProperty}");
        RequireEncodableText(result.Output, OutputProperty);
        RequireEncodableText(result.Model, ModelProperty);
        if (result.IterationStartSha is { } sha)
            RequireEncodableText(sha, IterationStartShaProperty);

        if (result.Metrics is { } metrics)
        {
            if (metrics.Verdict is null)
                throw new CompletionReceiptCodecException("Cannot encode a receipt whose metrics verdict is null.");
            if (metrics.Issues is null)
                throw new CompletionReceiptCodecException("Cannot encode a receipt whose metrics issues list is null.");
            if (metrics.Summary is null)
                throw new CompletionReceiptCodecException("Cannot encode a receipt whose metrics summary is null.");

            RequireEncodableText(metrics.Verdict, VerdictProperty);
            RequireEncodableText(metrics.Summary, SummaryProperty);
            foreach (var issue in metrics.Issues)
            {
                if (issue is null)
                    throw new CompletionReceiptCodecException("Cannot encode a receipt containing a null metrics issue.");
                RequireEncodableText(issue, $"{IssuesProperty}[]");
            }
        }

        if (result.GitStatus is { } gitStatus)
        {
            if (gitStatus.ChangedFiles is null)
                throw new CompletionReceiptCodecException("Cannot encode a receipt whose git status changed-files list is null.");
            foreach (var path in gitStatus.ChangedFiles)
            {
                if (path is null)
                    throw new CompletionReceiptCodecException("Cannot encode a receipt containing a null changed-file path.");
                RequireEncodableText(path, $"{ChangedFilesProperty}[]");
            }
        }
    }

    /// <summary>
    /// Materializes a decoded wire object into a receipt, validating every decoded value and
    /// detaching it from the payload: all lists are copied and all records are constructed fresh.
    /// <para>
    /// The members the serializer and the two converters have already guaranteed (presence,
    /// non-null, correct JSON kind, non-null elements) are not re-checked; what remains here is the
    /// blank-identity rule, the canonical enum-label rule and the carrier's own constructor contract.
    /// </para>
    /// </summary>
    private static CompletionReceipt Materialize(WireReceipt wire)
    {
        var slot = wire.Slot;
        var position = slot.Position;
        var result = wire.Result;

        RequireIdentity(wire.GoalId, GoalIdProperty);
        RequireIdentity(wire.WorkerId, WorkerIdProperty);
        RequireIdentity(slot.TaskId, $"slot.{TaskIdProperty}");
        RequireIdentity(result.TaskId, $"result.{TaskIdProperty}");

        var role = ParseRole(wire.Role);
        var phase = ParsePhase(position.Phase);
        var status = ParseStatus(result.Status);

        TaskMetrics? metrics = null;
        if (result.Metrics is { } wireMetrics)
        {
            metrics = new TaskMetrics
            {
                Verdict = wireMetrics.Verdict,
                BuildSuccess = wireMetrics.BuildSuccess,
                TotalTests = wireMetrics.TotalTests,
                PassedTests = wireMetrics.PassedTests,
                FailedTests = wireMetrics.FailedTests,
                // CoverageConverter is the ONLY way a value reaches here: a finite number, or one of
                // the three canonical tokens. It is carried through unchanged, never coerced.
                CoveragePercent = wireMetrics.CoveragePercent,
                // The converter allocated this list fresh, and its entries are guaranteed non-null.
                Issues = wireMetrics.Issues,
                Summary = wireMetrics.Summary,
            };
        }

        GitChangeSummary? gitStatus = null;
        if (result.GitStatus is { } wireGit)
        {
            gitStatus = new GitChangeSummary
            {
                FilesChanged = wireGit.FilesChanged,
                Insertions = wireGit.Insertions,
                Deletions = wireGit.Deletions,
                Pushed = wireGit.Pushed,
                ChangedFiles = wireGit.ChangedFiles,
            };
        }

        var domainSlot = new WorkSlot(slot.TaskId, new WorkSlotPosition(position.Iteration, phase, position.Occurrence), slot.Attempt);

        var domainResult = new TaskResult
        {
            TaskId = result.TaskId,
            Status = status,
            Output = result.Output,
            Metrics = metrics,
            GitStatus = gitStatus,
            Model = result.Model,
            IterationStartSha = result.IterationStartSha,
        };

        // THE CARRIER'S OWN RULES RE-APPLY HERE: nonblank identities (already checked), matching slot
        // and result task ids, positive iteration/occurrence/attempt, a worker-backed phase and the
        // phase's mapped role. Its refusals are reported as codec refusals so a caller sees one
        // failure category for "this payload is not a valid receipt".
        try
        {
            return new CompletionReceipt(wire.GoalId, wire.WorkerId, role, domainSlot, domainResult);
        }
        catch (ArgumentException ex)
        {
            throw new CompletionReceiptCodecException(
                $"The completion receipt payload is not an internally consistent receipt: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Refuses a blank identity string, naming the property in the failure. The carrier re-checks the
    /// same rule, so this is a MESSAGE-QUALITY guard that runs first: it is what makes the refusal
    /// name the offending member (<c>'slot.taskId'</c>) instead of describing the receipt in general.
    /// </summary>
    private static void RequireIdentity(string value, string context)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new CompletionReceiptCodecException(
                $"The completion receipt payload's '{context}' must be a non-blank string.");
    }

    /// <summary>
    /// Refuses a string containing an UNPAIRED UTF-16 surrogate, naming the property in the failure.
    /// <para>
    /// WHY THIS IS A REFUSAL AND NOT A SUBSTITUTION: an unpaired surrogate has no valid UTF-8
    /// encoding, so <see cref="Utf8JsonWriter"/> would silently replace it with U+FFFD. That is
    /// silent data coercion — two DISTINCT domain values would collapse onto the same canonical text,
    /// which would make the ordinal <c>Encode(Decode(p))</c> comparison report a false duplicate and
    /// would store corrupted evidence. A well-formed surrogate PAIR (any non-BMP character) is
    /// perfectly representable and is accepted unchanged; only the unpaired case is refused. This
    /// matches the decode side, which rejects such escapes outright, so both directions agree.
    /// </para>
    /// </summary>
    private static void RequireEncodableText(string value, string context)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                // A high surrogate is valid ONLY when its low surrogate follows immediately.
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    i++; // Consume the pair.
                    continue;
                }

                throw new CompletionReceiptCodecException(
                    $"The completion receipt payload's '{context}' contains an unpaired UTF-16 high " +
                    $"surrogate (U+{(int)c:X4}) at index {i}, which cannot be encoded faithfully.");
            }

            if (char.IsLowSurrogate(c))
            {
                throw new CompletionReceiptCodecException(
                    $"The completion receipt payload's '{context}' contains an unpaired UTF-16 low " +
                    $"surrogate (U+{(int)c:X4}) at index {i}, which cannot be encoded faithfully.");
            }
        }
    }

    /// <summary>
    /// The canonical label of a worker-backed <see cref="WorkerRole"/>, taken from the existing
    /// <see cref="WorkerRoleExtensions.ToRoleName"/> mapping. A role outside the worker-backed set
    /// (or an undefined value) has no label in this envelope and is reported rather than written.
    /// </summary>
    private static string RoleLabel(WorkerRole role)
    {
        foreach (var candidate in WorkerRoles)
        {
            if (candidate == role)
                return role.ToRoleName();
        }

        throw new CompletionReceiptCodecException(
            $"Cannot encode the non-worker WorkerRole '{role}' as a completion receipt role.");
    }

    /// <summary>
    /// The canonical lowercase label of a worker-backed <see cref="GoalPhase"/>. A phase outside the
    /// worker-backed set (or an undefined value) has no label in this envelope and is reported rather
    /// than written.
    /// </summary>
    private static string PhaseLabel(GoalPhase phase)
    {
        foreach (var candidate in WorkerPhases)
        {
            if (candidate == phase)
                return candidate.ToString().ToLowerInvariant();
        }

        throw new CompletionReceiptCodecException(
            $"Cannot encode the non-worker GoalPhase '{phase}' as a completion receipt phase.");
    }

    /// <summary>The canonical lowercase label of a defined <see cref="TaskOutcome"/>; see <see cref="PhaseLabel"/>.</summary>
    private static string StatusLabel(TaskOutcome status)
    {
        foreach (var candidate in Enum.GetValues<TaskOutcome>())
        {
            if (candidate == status)
                return candidate.ToString().ToLowerInvariant();
        }

        throw new CompletionReceiptCodecException(
            $"Cannot encode the undefined TaskOutcome value {(int)status} as a completion receipt status.");
    }

    /// <summary>
    /// CANONICAL-LABEL RECOGNITION ONLY (ordinal, case-insensitive) for the worker-backed roles —
    /// the exact inverse of <see cref="RoleLabel"/>. Numeric tokens, numeric strings, aliases and
    /// unknown labels are all refused: an unreadable role is corruption, and corruption must never be
    /// repaired into a plausible value.
    /// </summary>
    private static WorkerRole ParseRole(string label)
    {
        foreach (var role in WorkerRoles)
        {
            if (string.Equals(role.ToRoleName(), label, StringComparison.OrdinalIgnoreCase))
                return role;
        }

        throw new CompletionReceiptCodecException(
            $"'{label}' is not a permitted worker role label for a completion receipt.");
    }

    /// <summary>Canonical-label recognition for the worker-backed phases; see <see cref="ParseRole"/>.</summary>
    private static GoalPhase ParsePhase(string label)
    {
        foreach (var phase in WorkerPhases)
        {
            if (string.Equals(phase.ToString().ToLowerInvariant(), label, StringComparison.OrdinalIgnoreCase))
                return phase;
        }

        throw new CompletionReceiptCodecException(
            $"'{label}' is not a permitted worker phase label for a completion receipt.");
    }

    /// <summary>Canonical-label recognition for <see cref="TaskOutcome"/>; see <see cref="ParseRole"/>.</summary>
    private static TaskOutcome ParseStatus(string label)
    {
        foreach (var status in Enum.GetValues<TaskOutcome>())
        {
            if (string.Equals(status.ToString().ToLowerInvariant(), label, StringComparison.OrdinalIgnoreCase))
                return status;
        }

        throw new CompletionReceiptCodecException(
            $"'{label}' is not a permitted task status label for a completion receipt.");
    }

    /// <summary>
    /// THE COVERAGE CONTRACT, in one place for both directions. Reading accepts exactly two shapes: a
    /// JSON number that is FINITE (an out-of-range token such as <c>1e400</c> is refused rather than
    /// silently becoming an infinity), or one of the three canonical strings <c>"NaN"</c>,
    /// <c>"Infinity"</c> and <c>"-Infinity"</c>. Numeric strings, case variants of the tokens, JSON
    /// <c>null</c> and non-number/non-string kinds are all refused. Writing emits a number for a
    /// finite value and the matching canonical token otherwise.
    /// </summary>
    private sealed class CoverageConverter : JsonConverter<double>
    {
        /// <inheritdoc />
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    var token = reader.GetString();
                    if (token == NaNToken)
                        return double.NaN;
                    if (token == PositiveInfinityToken)
                        return double.PositiveInfinity;
                    if (token == NegativeInfinityToken)
                        return double.NegativeInfinity;
                    throw new JsonException(
                        $"'{token}' is not a canonical coverage value; only \"{NaNToken}\", " +
                        $"\"{PositiveInfinityToken}\" and \"{NegativeInfinityToken}\" are permitted strings.");

                case JsonTokenType.Number:
                    var value = reader.GetDouble();
                    if (!double.IsFinite(value))
                    {
                        throw new JsonException(
                            "The numeric coverage value is out of range for a double; a non-finite " +
                            "coverage value must use one of the canonical string tokens.");
                    }

                    return value;

                default:
                    throw new JsonException(
                        $"A coverage value must be a number or a canonical string, but was {reader.TokenType}.");
            }
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (double.IsNaN(value))
                writer.WriteStringValue(NaNToken);
            else if (double.IsPositiveInfinity(value))
                writer.WriteStringValue(PositiveInfinityToken);
            else if (double.IsNegativeInfinity(value))
                writer.WriteStringValue(NegativeInfinityToken);
            else
                writer.WriteNumberValue(value);
        }
    }

    /// <summary>
    /// THE EVIDENCE-LIST CONTRACT, in one place for both directions: a JSON array of non-null
    /// strings, with no other element kind accepted. Reading allocates a FRESH list, so the decoded
    /// domain record never aliases the payload. An absent list is refused by the serializer (the
    /// members are <c>required</c>) and an explicit <c>null</c> by the non-nullable annotation, so
    /// neither case reaches this converter.
    /// </summary>
    private sealed class StringListConverter : JsonConverter<List<string>>
    {
        /// <inheritdoc />
        public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException($"A string array is required, but the token was {reader.TokenType}.");

            var list = new List<string>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                    return list;
                if (reader.TokenType == JsonTokenType.Null)
                    throw new JsonException($"Array entries must be strings, but one was {reader.TokenType}.");

                list.Add(reader.GetString()!);
            }

            throw new JsonException("The string array was not terminated.");
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value)
                writer.WriteStringValue(item);
            writer.WriteEndArray();
        }
    }

    /// <summary>
    /// THE ENCODE-SIDE DETACHED SNAPSHOT of a receipt: the private, codec-owned mirror of the
    /// caller's graph that <see cref="Capture"/> produces, <see cref="Validate"/> inspects and
    /// <see cref="WriteReceipt"/> writes.
    /// <para>
    /// It exists ONLY to close the validate-then-write window. Every member is nullable and carries
    /// whatever the caller's graph held — including <c>null</c> — because the snapshot decides
    /// nothing; <see cref="Validate"/> remains the single authority on what is refused, and its
    /// messages are unchanged. It is deliberately NOT the wire shape: it has no JSON attributes and
    /// takes no part in decoding.
    /// </para>
    /// </summary>
    private sealed class ReceiptSnapshot
    {
        /// <summary>The captured goal identity.</summary>
        public string? GoalId { get; init; }

        /// <summary>The captured worker identity.</summary>
        public string? WorkerId { get; init; }

        /// <summary>The captured assigned role.</summary>
        public WorkerRole Role { get; init; }

        /// <summary>The captured work-slot identity, or <c>null</c> when the caller's slot was null.</summary>
        public SlotSnapshot? Slot { get; init; }

        /// <summary>The captured result, or <c>null</c> when the caller's result was null.</summary>
        public ResultSnapshot? Result { get; init; }
    }

    /// <summary>The captured <c>slot</c>; see <see cref="ReceiptSnapshot"/>.</summary>
    private sealed class SlotSnapshot
    {
        /// <summary>The captured task identity.</summary>
        public string? TaskId { get; init; }

        /// <summary>The captured position, or <c>null</c> when the caller's position was null.</summary>
        public PositionSnapshot? Position { get; init; }

        /// <summary>The captured dispatch attempt.</summary>
        public int Attempt { get; init; }
    }

    /// <summary>
    /// The captured <c>slot.position</c>, flattened into scalars so not even the caller's immutable
    /// position record remains reachable; see <see cref="ReceiptSnapshot"/>.
    /// </summary>
    private sealed class PositionSnapshot
    {
        /// <summary>The captured one-based iteration.</summary>
        public int Iteration { get; init; }

        /// <summary>The captured phase.</summary>
        public GoalPhase Phase { get; init; }

        /// <summary>The captured one-based occurrence.</summary>
        public int Occurrence { get; init; }
    }

    /// <summary>The captured <c>result</c>; see <see cref="ReceiptSnapshot"/>.</summary>
    private sealed class ResultSnapshot
    {
        /// <summary>The captured task identity.</summary>
        public string? TaskId { get; init; }

        /// <summary>The captured outcome.</summary>
        public TaskOutcome Status { get; init; }

        /// <summary>The captured worker output text.</summary>
        public string? Output { get; init; }

        /// <summary>The captured model id.</summary>
        public string? Model { get; init; }

        /// <summary>The captured iteration start SHA, which may legitimately be <c>null</c>.</summary>
        public string? IterationStartSha { get; init; }

        /// <summary>The captured metrics, which may legitimately be <c>null</c>.</summary>
        public MetricsSnapshot? Metrics { get; init; }

        /// <summary>The captured git status, which may legitimately be <c>null</c>.</summary>
        public GitSnapshot? GitStatus { get; init; }
    }

    /// <summary>The captured <c>result.metrics</c>; see <see cref="ReceiptSnapshot"/>.</summary>
    private sealed class MetricsSnapshot
    {
        /// <summary>The captured verdict string.</summary>
        public string? Verdict { get; init; }

        /// <summary>The captured build-success flag.</summary>
        public bool BuildSuccess { get; init; }

        /// <summary>The captured total test count.</summary>
        public int TotalTests { get; init; }

        /// <summary>The captured passed test count.</summary>
        public int PassedTests { get; init; }

        /// <summary>The captured failed test count.</summary>
        public int FailedTests { get; init; }

        /// <summary>The captured coverage percentage.</summary>
        public double CoveragePercent { get; init; }

        /// <summary>
        /// THE PRIVATE COPY of the caller's issue list, or <c>null</c> when the caller's list was
        /// null. Validation walks this list and the writer emits this list, so the two can never
        /// disagree about its contents.
        /// </summary>
        public List<string>? Issues { get; init; }

        /// <summary>The captured summary string.</summary>
        public string? Summary { get; init; }
    }

    /// <summary>The captured <c>result.gitStatus</c>; see <see cref="ReceiptSnapshot"/>.</summary>
    private sealed class GitSnapshot
    {
        /// <summary>The captured changed-file count.</summary>
        public int FilesChanged { get; init; }

        /// <summary>The captured insertion count.</summary>
        public int Insertions { get; init; }

        /// <summary>The captured deletion count.</summary>
        public int Deletions { get; init; }

        /// <summary>The captured pushed flag.</summary>
        public bool Pushed { get; init; }

        /// <summary>THE PRIVATE COPY of the caller's changed-file list; see <see cref="MetricsSnapshot.Issues"/>.</summary>
        public List<string>? ChangedFiles { get; init; }
    }

    /// <summary>
    /// The wire shape of the version-1 root object. Every member is REQUIRED, and each is
    /// non-nullable unless the format explicitly permits <c>null</c>. Together with the read
    /// settings, <c>required</c> refuses a MISSING member while the non-nullable annotation refuses
    /// an explicit <c>null</c>; <see cref="JsonRequiredAttribute"/> is used for the members that are
    /// allowed to be <c>null</c> but must still be PRESENT. The member order here is irrelevant to
    /// decoding (the canonical order is produced by the writer) and every name is pinned with
    /// <see cref="JsonPropertyNameAttribute"/>.
    /// </summary>
    private sealed class WireReceipt
    {
        /// <summary>The envelope version marker; must equal <see cref="Version"/>.</summary>
        [JsonPropertyName(VersionProperty)]
        public required int Version { get; init; }

        /// <summary>The goal identity; must be present and nonblank.</summary>
        [JsonPropertyName(GoalIdProperty)]
        public required string GoalId { get; init; }

        /// <summary>The worker identity; must be present and nonblank.</summary>
        [JsonPropertyName(WorkerIdProperty)]
        public required string WorkerId { get; init; }

        /// <summary>The assigned role's canonical label; must be present and a worker-backed role label.</summary>
        [JsonPropertyName(RoleProperty)]
        public required string Role { get; init; }

        /// <summary>The work-slot identity object; must be present and never <c>null</c>.</summary>
        [JsonPropertyName(SlotProperty)]
        public required WireSlot Slot { get; init; }

        /// <summary>The domain result object; must be present and never <c>null</c>.</summary>
        [JsonPropertyName(ResultProperty)]
        public required WireResult Result { get; init; }
    }

    /// <summary>The wire shape of the <c>slot</c> object.</summary>
    private sealed class WireSlot
    {
        /// <summary>The task identity; must be present, nonblank, and equal the result's task id.</summary>
        [JsonPropertyName(TaskIdProperty)]
        public required string TaskId { get; init; }

        /// <summary>The slot position object; must be present and never <c>null</c>.</summary>
        [JsonPropertyName(PositionProperty)]
        public required WirePosition Position { get; init; }

        /// <summary>The dispatch attempt; must be present and positive.</summary>
        [JsonPropertyName(AttemptProperty)]
        public required int Attempt { get; init; }
    }

    /// <summary>The wire shape of the <c>slot.position</c> object.</summary>
    private sealed class WirePosition
    {
        /// <summary>The one-based iteration; must be present and positive.</summary>
        [JsonPropertyName(IterationProperty)]
        public required int Iteration { get; init; }

        /// <summary>The phase's canonical lowercase label; must be present and a worker-backed phase label.</summary>
        [JsonPropertyName(PhaseProperty)]
        public required string Phase { get; init; }

        /// <summary>The one-based occurrence; must be present and positive.</summary>
        [JsonPropertyName(OccurrenceProperty)]
        public required int Occurrence { get; init; }
    }

    /// <summary>
    /// The wire shape of the <c>result</c> object. Every member is required to be PRESENT;
    /// <c>iterationStartSha</c>, <c>metrics</c> and <c>gitStatus</c> are the only members whose value
    /// may legitimately be <c>null</c> (hence <see cref="JsonRequiredAttribute"/> plus a nullable
    /// annotation), and a <c>null</c> SHA stays distinct from an empty one.
    /// </summary>
    private sealed class WireResult
    {
        /// <summary>The task identity; must be present, nonblank, and equal the slot's task id.</summary>
        [JsonPropertyName(TaskIdProperty)]
        public required string TaskId { get; init; }

        /// <summary>The outcome's canonical lowercase label; must be present and a defined status label.</summary>
        [JsonPropertyName(StatusProperty)]
        public required string Status { get; init; }

        /// <summary>The worker output text; must be present and non-<c>null</c>, but may be empty, and is preserved verbatim.</summary>
        [JsonPropertyName(OutputProperty)]
        public required string Output { get; init; }

        /// <summary>The model id; must be present and non-<c>null</c>, but may be empty, and is preserved verbatim.</summary>
        [JsonPropertyName(ModelProperty)]
        public required string Model { get; init; }

        /// <summary>The iteration start SHA, or <c>null</c> when the worker reported none. Must be present.</summary>
        [JsonPropertyName(IterationStartShaProperty)]
        [JsonRequired]
        public string? IterationStartSha { get; init; }

        /// <summary>The metrics object, or <c>null</c> when the worker reported none. Must be present.</summary>
        [JsonPropertyName(MetricsProperty)]
        [JsonRequired]
        public WireMetrics? Metrics { get; init; }

        /// <summary>The git-status object, or <c>null</c> when the worker reported none. Must be present.</summary>
        [JsonPropertyName(GitStatusProperty)]
        [JsonRequired]
        public WireGitStatus? GitStatus { get; init; }
    }

    /// <summary>
    /// The wire shape of the <c>result.metrics</c> object. Every member is required whenever the
    /// object itself is present — the object is never abbreviated.
    /// </summary>
    private sealed class WireMetrics
    {
        /// <summary>The verdict string; must be present and non-<c>null</c>, but may be empty, and is preserved verbatim.</summary>
        [JsonPropertyName(VerdictProperty)]
        public required string Verdict { get; init; }

        /// <summary>Whether the build succeeded.</summary>
        [JsonPropertyName(BuildSuccessProperty)]
        public required bool BuildSuccess { get; init; }

        /// <summary>Total tests executed.</summary>
        [JsonPropertyName(TotalTestsProperty)]
        public required int TotalTests { get; init; }

        /// <summary>Tests that passed.</summary>
        [JsonPropertyName(PassedTestsProperty)]
        public required int PassedTests { get; init; }

        /// <summary>Tests that failed.</summary>
        [JsonPropertyName(FailedTestsProperty)]
        public required int FailedTests { get; init; }

        /// <summary>
        /// Coverage percentage. A finite value is a JSON number; a non-finite value is exactly
        /// <c>"NaN"</c>, <c>"Infinity"</c> or <c>"-Infinity"</c>. See <see cref="CoverageConverter"/>
        /// for the full contract.
        /// </summary>
        [JsonPropertyName(CoveragePercentProperty)]
        [JsonConverter(typeof(CoverageConverter))]
        public required double CoveragePercent { get; init; }

        /// <summary>The issue list; must be present and non-<c>null</c>, with no <c>null</c> elements.</summary>
        [JsonPropertyName(IssuesProperty)]
        [JsonConverter(typeof(StringListConverter))]
        public required List<string> Issues { get; init; }

        /// <summary>The summary string; must be present and non-<c>null</c>, but may be empty, and is preserved verbatim.</summary>
        [JsonPropertyName(SummaryProperty)]
        public required string Summary { get; init; }
    }

    /// <summary>
    /// The wire shape of the <c>result.gitStatus</c> object. Every member is required whenever the
    /// object itself is present.
    /// </summary>
    private sealed class WireGitStatus
    {
        /// <summary>Number of files changed.</summary>
        [JsonPropertyName(FilesChangedProperty)]
        public required int FilesChanged { get; init; }

        /// <summary>Total lines inserted.</summary>
        [JsonPropertyName(InsertionsProperty)]
        public required int Insertions { get; init; }

        /// <summary>Total lines deleted.</summary>
        [JsonPropertyName(DeletionsProperty)]
        public required int Deletions { get; init; }

        /// <summary>Whether the changes were pushed.</summary>
        [JsonPropertyName(PushedProperty)]
        public required bool Pushed { get; init; }

        /// <summary>The changed-file list; must be present and non-<c>null</c>, with no <c>null</c> elements.</summary>
        [JsonPropertyName(ChangedFilesProperty)]
        [JsonConverter(typeof(StringListConverter))]
        public required List<string> ChangedFiles { get; init; }
    }
}

/// <summary>
/// Raised by <see cref="CompletionReceiptCodec"/> when a receipt payload cannot be encoded or
/// decoded. Derives from <see cref="InvalidOperationException"/> so a caller that only cares that the
/// operation was refused (rather than which rule refused it) still catches it.
/// </summary>
internal sealed class CompletionReceiptCodecException : InvalidOperationException
{
    /// <summary>Creates the exception with a message describing the rejected payload or receipt.</summary>
    /// <param name="message">A clear description of what was rejected.</param>
    internal CompletionReceiptCodecException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception wrapping the underlying parse or construction failure.</summary>
    /// <param name="message">A clear description of what was rejected.</param>
    /// <param name="innerException">The underlying failure.</param>
    internal CompletionReceiptCodecException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

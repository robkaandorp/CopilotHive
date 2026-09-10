using System.Text.Json;

using CopilotHive.Services;

namespace CopilotHive.Persistence;

/// <summary>
/// Explicit, versioned <see cref="System.Text.Json"/> encoder/decoder for a
/// <see cref="WorkSlotRegistrySnapshot"/>.
/// <para>
/// THE FORMAT is a version-1 envelope over the EXISTING domain values
/// (<see cref="WorkSlotView"/>, <see cref="WorkSlot"/>, <see cref="WorkSlotPosition"/>,
/// <see cref="WorkSlotState"/>, <see cref="WorkSlotRegistryAttemptEntry"/>) — no second domain
/// model is introduced, and no reflection-based contract is relied upon:
/// </para>
/// <code>
/// {
///   "version": 1,
///   "slots": [
///     { "taskId": "t1",
///       "position": { "iteration": 1, "phase": "Coding", "occurrence": 1 },
///       "attempt": 2,
///       "state": "Claimed" }
///   ],
///   "dispatchAttempts": [
///     { "position": { "iteration": 1, "phase": "Coding", "occurrence": 1 },
///       "highWaterAttempt": 3 }
///   ]
/// }
/// </code>
/// <para>
/// PHASE AND STATE ARE NAMED STRINGS, matching the JSON contract the shared
/// <see cref="GoalPhase"/> converter already declares
/// (<c>JsonStringEnumConverter&lt;GoalPhase&gt;</c>). No shared enum attribute is changed here.
/// </para>
/// <para>
/// DECODE REJECTS, IT NEVER INVENTS. Malformed JSON, a JSON <c>null</c> root, a non-object root,
/// a missing or unsupported <c>version</c> marker, a missing or null collection, a null entry, and
/// ANY missing or null required field (task id, position, iteration, phase, occurrence, attempt,
/// state, high-water) are all hard failures — never a silent default, never a heuristic version
/// repair, never a corruption-to-empty fallback, never a "best effort" salvage.
/// </para>
/// <para>
/// DOMAIN VALIDATION IS NOT PERFORMED HERE. Positive-iteration / positive-occurrence /
/// positive-attempt rules, duplicate detection and every cross-entry invariant belong to
/// <c>GoalPipeline.RestoreRegistry</c> and are deliberately NOT duplicated: a value such as
/// <c>attempt: 0</c> decodes faithfully and is then rejected by the restore. A SUCCESSFUL DECODE IS
/// NOT AUTHORIZATION TO RESTORE — it only means the bytes were well-formed.
/// </para>
/// <para>
/// DEEP COPY AT THE BOUNDARY. Decoding always allocates fresh lists and fresh records, so a caller
/// can never obtain an alias into any live registry's collections.
/// </para>
/// </summary>
internal static class WorkSlotRegistryCodec
{
    /// <summary>The only envelope version this codec writes and accepts.</summary>
    internal const int Version = 1;

    private const string VersionProperty = "version";
    private const string SlotsProperty = "slots";
    private const string AttemptsProperty = "dispatchAttempts";
    private const string TaskIdProperty = "taskId";
    private const string PositionProperty = "position";
    private const string IterationProperty = "iteration";
    private const string PhaseProperty = "phase";
    private const string OccurrenceProperty = "occurrence";
    private const string AttemptProperty = "attempt";
    private const string StateProperty = "state";
    private const string HighWaterProperty = "highWaterAttempt";

    /// <summary>
    /// Encodes a snapshot into the version-1 envelope. Purely a serialization step: it performs no
    /// database interaction and no domain validation beyond what the format can physically
    /// represent (a null snapshot, a null collection, a null entry, or an enum value that has no
    /// canonical name cannot be written and are reported instead of being guessed at).
    /// </summary>
    /// <param name="snapshot">The detached snapshot to encode.</param>
    /// <returns>The encoded JSON text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <c>null</c>.</exception>
    /// <exception cref="WorkSlotRegistryCodecException">The snapshot cannot be represented.</exception>
    internal static string Encode(WorkSlotRegistrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Slots is null)
            throw new WorkSlotRegistryCodecException("Cannot encode a snapshot whose slot collection is null.");
        if (snapshot.DispatchAttempts is null)
            throw new WorkSlotRegistryCodecException("Cannot encode a snapshot whose attempt collection is null.");

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber(VersionProperty, Version);

            writer.WriteStartArray(SlotsProperty);
            foreach (var view in snapshot.Slots)
            {
                if (view is null)
                    throw new WorkSlotRegistryCodecException("Cannot encode a snapshot containing a null slot entry.");
                if (view.Slot is null)
                    throw new WorkSlotRegistryCodecException("Cannot encode a snapshot slot entry whose slot is null.");
                if (view.Slot.TaskId is null)
                    throw new WorkSlotRegistryCodecException("Cannot encode a snapshot slot whose task id is null.");

                writer.WriteStartObject();
                writer.WriteString(TaskIdProperty, view.Slot.TaskId);
                WritePosition(writer, view.Slot.Position);
                writer.WriteNumber(AttemptProperty, view.Slot.Attempt);
                writer.WriteString(StateProperty, StateName(view.State));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray(AttemptsProperty);
            foreach (var entry in snapshot.DispatchAttempts)
            {
                if (entry is null)
                    throw new WorkSlotRegistryCodecException("Cannot encode a snapshot containing a null attempt entry.");

                writer.WriteStartObject();
                WritePosition(writer, entry.Position);
                writer.WriteNumber(HighWaterProperty, entry.HighWaterAttempt);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Decodes a version-1 envelope back into a snapshot built entirely from freshly allocated
    /// records and lists.
    /// </summary>
    /// <param name="json">The encoded payload.</param>
    /// <returns>A detached snapshot carrying exactly the encoded values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <c>null</c>.</exception>
    /// <exception cref="WorkSlotRegistryCodecException">
    /// The payload is malformed, is a JSON null/non-object root, carries a missing or unsupported
    /// version marker, or is missing any required structure, entry, or field.
    /// </exception>
    internal static WorkSlotRegistrySnapshot Decode(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new WorkSlotRegistryCodecException(
                $"The work-slot registry payload is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Null)
                throw new WorkSlotRegistryCodecException("The work-slot registry payload is a JSON null root.");
            if (root.ValueKind != JsonValueKind.Object)
                throw new WorkSlotRegistryCodecException(
                    $"The work-slot registry payload root must be a JSON object, but was {root.ValueKind}.");

            if (!root.TryGetProperty(VersionProperty, out var versionElement))
                throw new WorkSlotRegistryCodecException(
                    $"The work-slot registry payload has no '{VersionProperty}' marker.");
            if (versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out var version))
                throw new WorkSlotRegistryCodecException(
                    $"The work-slot registry payload's '{VersionProperty}' marker is not an integer number.");
            if (version != Version)
                throw new WorkSlotRegistryCodecException(
                    $"Unsupported work-slot registry payload version {version}; only version {Version} is supported.");

            var slotsElement = RequireArray(root, SlotsProperty);
            var attemptsElement = RequireArray(root, AttemptsProperty);

            var slots = new List<WorkSlotView>();
            foreach (var slotElement in slotsElement.EnumerateArray())
            {
                if (slotElement.ValueKind == JsonValueKind.Null)
                    throw new WorkSlotRegistryCodecException($"The '{SlotsProperty}' array contains a null entry.");
                if (slotElement.ValueKind != JsonValueKind.Object)
                    throw new WorkSlotRegistryCodecException(
                        $"The '{SlotsProperty}' array contains a {slotElement.ValueKind} entry; an object is required.");

                var taskId = RequireString(slotElement, TaskIdProperty, SlotsProperty);
                var position = ReadPosition(slotElement, SlotsProperty);
                var attempt = RequireInt32(slotElement, AttemptProperty, SlotsProperty);
                var state = ParseState(RequireString(slotElement, StateProperty, SlotsProperty));

                slots.Add(new WorkSlotView(new WorkSlot(taskId, position, attempt), state));
            }

            var attempts = new List<WorkSlotRegistryAttemptEntry>();
            foreach (var attemptElement in attemptsElement.EnumerateArray())
            {
                if (attemptElement.ValueKind == JsonValueKind.Null)
                    throw new WorkSlotRegistryCodecException($"The '{AttemptsProperty}' array contains a null entry.");
                if (attemptElement.ValueKind != JsonValueKind.Object)
                    throw new WorkSlotRegistryCodecException(
                        $"The '{AttemptsProperty}' array contains a {attemptElement.ValueKind} entry; an object is required.");

                var position = ReadPosition(attemptElement, AttemptsProperty);
                var highWater = RequireInt32(attemptElement, HighWaterProperty, AttemptsProperty);

                attempts.Add(new WorkSlotRegistryAttemptEntry(position, highWater));
            }

            // The lists are freshly allocated here and handed out as the snapshot's only storage —
            // no caller can reach a live registry's collections through them.
            return new WorkSlotRegistrySnapshot(slots, attempts);
        }
    }

    private static void WritePosition(Utf8JsonWriter writer, WorkSlotPosition position)
    {
        if (position is null)
            throw new WorkSlotRegistryCodecException("Cannot encode a snapshot entry whose position is null.");

        writer.WriteStartObject(PositionProperty);
        writer.WriteNumber(IterationProperty, position.Iteration);
        writer.WriteString(PhaseProperty, PhaseName(position.Phase));
        writer.WriteNumber(OccurrenceProperty, position.Occurrence);
        writer.WriteEndObject();
    }

    private static WorkSlotPosition ReadPosition(JsonElement owner, string context)
    {
        if (!owner.TryGetProperty(PositionProperty, out var positionElement))
            throw new WorkSlotRegistryCodecException(
                $"An entry in '{context}' has no '{PositionProperty}' object.");
        if (positionElement.ValueKind == JsonValueKind.Null)
            throw new WorkSlotRegistryCodecException(
                $"An entry in '{context}' has a null '{PositionProperty}'.");
        if (positionElement.ValueKind != JsonValueKind.Object)
            throw new WorkSlotRegistryCodecException(
                $"An entry in '{context}' has a '{PositionProperty}' of kind {positionElement.ValueKind}; an object is required.");

        var iteration = RequireInt32(positionElement, IterationProperty, context);
        var phase = ParsePhase(RequireString(positionElement, PhaseProperty, context));
        var occurrence = RequireInt32(positionElement, OccurrenceProperty, context);

        return new WorkSlotPosition(iteration, phase, occurrence);
    }

    private static JsonElement RequireArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element))
            throw new WorkSlotRegistryCodecException(
                $"The work-slot registry payload has no '{propertyName}' array.");
        if (element.ValueKind == JsonValueKind.Null)
            throw new WorkSlotRegistryCodecException(
                $"The work-slot registry payload's '{propertyName}' is null.");
        if (element.ValueKind != JsonValueKind.Array)
            throw new WorkSlotRegistryCodecException(
                $"The work-slot registry payload's '{propertyName}' is of kind {element.ValueKind}; an array is required.");

        return element;
    }

    private static string RequireString(JsonElement owner, string propertyName, string context)
    {
        if (!owner.TryGetProperty(propertyName, out var element))
            throw new WorkSlotRegistryCodecException(
                $"An entry in '{context}' has no '{propertyName}' value.");
        if (element.ValueKind == JsonValueKind.Null)
            throw new WorkSlotRegistryCodecException(
                $"An entry in '{context}' has a null '{propertyName}' value.");
        if (element.ValueKind != JsonValueKind.String)
            throw new WorkSlotRegistryCodecException(
                $"An entry in '{context}' has a '{propertyName}' of kind {element.ValueKind}; a string is required.");

        return element.GetString()!;
    }

    private static int RequireInt32(JsonElement owner, string propertyName, string context)
    {
        if (!owner.TryGetProperty(propertyName, out var element))
            throw new WorkSlotRegistryCodecException(
                $"An entry in '{context}' has no '{propertyName}' value.");
        if (element.ValueKind == JsonValueKind.Null)
            throw new WorkSlotRegistryCodecException(
                $"An entry in '{context}' has a null '{propertyName}' value.");
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
            throw new WorkSlotRegistryCodecException(
                $"An entry in '{context}' has a '{propertyName}' that is not a 32-bit integer number.");

        return value;
    }

    /// <summary>
    /// The canonical name of a defined <see cref="GoalPhase"/>. An undefined value has no name and
    /// would have to be written as a bare number — which is not the named-string contract — so it
    /// is reported instead of being written.
    /// </summary>
    private static string PhaseName(GoalPhase phase)
    {
        foreach (var candidate in Enum.GetValues<GoalPhase>())
        {
            if (candidate == phase)
                return candidate.ToString();
        }

        throw new WorkSlotRegistryCodecException(
            $"Cannot encode the undefined GoalPhase value {(int)phase} as a name.");
    }

    /// <summary>The canonical name of a defined <see cref="WorkSlotState"/>; see <see cref="PhaseName"/>.</summary>
    private static string StateName(WorkSlotState state)
    {
        foreach (var candidate in Enum.GetValues<WorkSlotState>())
        {
            if (candidate == state)
                return candidate.ToString();
        }

        throw new WorkSlotRegistryCodecException(
            $"Cannot encode the undefined WorkSlotState value {(int)state} as a name.");
    }

    /// <summary>
    /// CANONICAL-NAME RECOGNITION ONLY (ordinal, case-insensitive) — the exact inverse of the write
    /// side. Numeric forms, comma-separated flag expressions and whitespace-padded forms that
    /// <c>Enum.TryParse</c> would accept are all rejected: an unreadable phase is corruption, and
    /// corruption must never be repaired into a plausible value.
    /// </summary>
    private static GoalPhase ParsePhase(string name)
    {
        foreach (var phase in Enum.GetValues<GoalPhase>())
        {
            if (string.Equals(phase.ToString(), name, StringComparison.OrdinalIgnoreCase))
                return phase;
        }

        throw new WorkSlotRegistryCodecException($"'{name}' is not a known GoalPhase name.");
    }

    /// <summary>Canonical-name recognition for <see cref="WorkSlotState"/>; see <see cref="ParsePhase"/>.</summary>
    private static WorkSlotState ParseState(string name)
    {
        foreach (var state in Enum.GetValues<WorkSlotState>())
        {
            if (string.Equals(state.ToString(), name, StringComparison.OrdinalIgnoreCase))
                return state;
        }

        throw new WorkSlotRegistryCodecException($"'{name}' is not a known WorkSlotState name.");
    }
}

/// <summary>
/// Raised by <see cref="WorkSlotRegistryCodec"/> when a payload cannot be encoded or decoded.
/// Derives from <see cref="InvalidOperationException"/> so a caller that only cares that the
/// operation was refused (rather than which rule refused it) still catches it.
/// </summary>
internal sealed class WorkSlotRegistryCodecException : InvalidOperationException
{
    /// <summary>Creates the exception with a message describing the rejected payload.</summary>
    /// <param name="message">A clear description of what was rejected.</param>
    internal WorkSlotRegistryCodecException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception wrapping the underlying parse failure.</summary>
    /// <param name="message">A clear description of what was rejected.</param>
    /// <param name="innerException">The underlying failure.</param>
    internal WorkSlotRegistryCodecException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

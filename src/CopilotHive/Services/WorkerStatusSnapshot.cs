using CopilotHive.Workers;

namespace CopilotHive.Services;

/// <summary>
/// A DETACHED, point-in-time capture of ONE registered worker's operator-visible status, taken under
/// the pool's activity lock.
/// </summary>
/// <remarks>
/// <para>
/// IT IS DETACHED ON PURPOSE: it holds COPIED VALUES — never a live <see cref="ConnectedWorker"/>
/// alias — so a consumer projecting it cannot be handed a reference that keeps changing underneath
/// it, and one response can never mix facts read at two different instants. That is what lets the
/// counts and the per-worker flags of a single response be derived from the SAME captured list.
/// </para>
/// <para>
/// IT IS AN OBSERVATION, NOT A RESERVATION AND NOT A DELIVERY GUARANTEE. Everything here describes
/// only the instant the pool captured it; the registered instance remains free to change before any
/// consumer acts, which is exactly why the checked pool claim re-applies its own predicate at the
/// mutation point rather than trusting a snapshot.
/// </para>
/// <para>
/// <see cref="IsAvailable"/> is the pool's availability verdict for this capture, evaluated under the
/// same lock span as the facts it is derived from. It is NOT a reservation: it says the worker was
/// eligible for a new assignment at the captured instant and nothing about whether it will still be
/// eligible when a dispatcher acts.
/// </para>
/// </remarks>
internal readonly record struct WorkerStatusSnapshot
{
    /// <summary>Identifier of the captured worker.</summary>
    public required string Id { get; init; }

    /// <summary>The worker's role at the captured instant.</summary>
    public required WorkerRole Role { get; init; }

    /// <summary>Whether the worker was executing a task at the captured instant.</summary>
    public required bool IsBusy { get; init; }

    /// <summary>The task the worker was executing at the captured instant, or <c>null</c> when idle.</summary>
    public required string? CurrentTaskId { get; init; }

    /// <summary>
    /// The captured worker's completion-publication hold (<c>CompletionPublicationPending</c>) at the
    /// captured instant — one of the two withheld selection facts.
    /// </summary>
    public required bool CompletionPublicationPending { get; init; }

    /// <summary>
    /// The captured worker's readiness wait (<c>AwaitingWorkerReady</c>) at the captured instant —
    /// the longer of the two withheld selection facts.
    /// </summary>
    public required bool AwaitingWorkerReady { get; init; }

    /// <summary>
    /// THE AVAILABILITY OBSERVATION for this capture: the worker was, at the captured instant,
    /// eligible for the checked pool claim's own state predicate — not busy, carrying no task,
    /// holding no completion publication and not awaiting its own accepted Ready.
    /// </summary>
    public required bool IsAvailable { get; init; }

    /// <summary>Model the worker was using for its current task, or <c>null</c> when idle.</summary>
    public required string? CurrentModel { get; init; }

    /// <summary>Estimated context window usage as a percentage (0–100), or 0 when idle.</summary>
    public required int ContextUsagePercent { get; init; }

    /// <summary>UTC timestamp of the worker's last heartbeat at the captured instant.</summary>
    public required DateTime LastHeartbeat { get; init; }

    /// <summary>UTC timestamp when the worker first connected.</summary>
    public required DateTime ConnectedAt { get; init; }
}

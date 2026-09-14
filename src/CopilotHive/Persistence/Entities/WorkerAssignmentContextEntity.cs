using CopilotHive.Services;
using CopilotHive.Workers;

namespace CopilotHive.Persistence.Entities;

/// <summary>
/// EF Core entity mapping for the <c>worker_assignment_contexts</c> table: the durable,
/// INSERT-ONCE typed-column record of the SERVER's INTENDED binding for one dispatched task.
/// <para>
/// Stored as a plain POCO; the store's immutable value type is
/// <see cref="WorkerAssignmentContext"/>. A stored row records INTENDED binding only — it proves
/// nothing about delivery, about whether the binding is still authorized, or about worker
/// liveness, and it is not a completion receipt.
/// </para>
/// <para>
/// There is deliberately NO foreign key and NO cascade relationship to <c>pipelines</c> or
/// <c>task_mappings</c>: a transient pipeline or routing row must be deletable without erasing the
/// recorded assignment context.
/// </para>
/// </summary>
public sealed class WorkerAssignmentContextEntity
{
    /// <summary>Primary key — the OPAQUE task id the assignment was recorded for.</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>The goal id the assigned task belonged to.</summary>
    public string GoalId { get; set; } = string.Empty;

    /// <summary>The worker id the task was assigned to.</summary>
    public string WorkerId { get; set; } = string.Empty;

    /// <summary>
    /// The assigned role, stored with the existing lowercase enum convention (e.g.
    /// <c>"coder"</c>, <c>"docwriter"</c>).
    /// </summary>
    public WorkerRole Role { get; set; }

    /// <summary>
    /// The phase of the recorded work-slot position, stored with the existing lowercase enum
    /// convention (e.g. <c>"coding"</c>, <c>"docwriting"</c>).
    /// </summary>
    public GoalPhase Phase { get; set; }

    /// <summary>
    /// The ORIGINAL ASSIGNED model, preserved verbatim. It may legitimately be empty or
    /// whitespace: no provider normalization is applied anywhere in this slice.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>The one-based iteration of the recorded work-slot position.</summary>
    public int Iteration { get; set; }

    /// <summary>The one-based occurrence of the recorded work-slot position.</summary>
    public int Occurrence { get; set; }

    /// <summary>The one-based dispatch attempt of the recorded work-slot position.</summary>
    public int Attempt { get; set; }

    /// <summary>
    /// When the assignment context was FIRST recorded, as a UTC instant. This records first
    /// confirmed persistence intent — NOT an observed worker start and NOT a delivery time.
    /// </summary>
    public DateTime FirstAssignedAtUtc { get; set; }
}

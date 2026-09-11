namespace CopilotHive.Persistence.Entities;

/// <summary>
/// EF Core entity mapping for the completion_receipts table.
/// Stored as a plain POCO; domain logic uses <see cref="CompletionReceipt"/>.
/// </summary>
public sealed class CompletionReceiptEntity
{
    /// <summary>Primary key — the worker task id the receipt was stored for.</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>The goal id the receipt's task belonged to.</summary>
    public string GoalId { get; set; } = string.Empty;

    /// <summary>
    /// The canonical encoded receipt payload. The format's version marker lives ONLY inside this
    /// JSON — there is no version column.
    /// </summary>
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>
    /// When the payload was FIRST stored, as a UTC instant. This slice neither generates nor
    /// updates the value: the entity simply carries whatever it was given.
    /// </summary>
    public DateTime FirstStoredAtUtc { get; set; }
}

namespace CopilotHive.Persistence.Entities;

/// <summary>
/// EF Core entity mapping for the conversation_entries table.
/// Stored as a plain POCO.
/// </summary>
public sealed class ConversationEntryEntity
{
    /// <summary>Primary key — autoincrement integer.</summary>
    public int Id { get; set; }

    /// <summary>Foreign key to pipelines(goal_id).</summary>
    public string GoalId { get; set; } = string.Empty;

    /// <summary>Sequence order of the entry within the conversation.</summary>
    public int Seq { get; set; }

    /// <summary>Role that produced the entry (e.g. "user", "assistant", "system").</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Message content.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Optional 1-based iteration number, or null.</summary>
    public int? Iteration { get; set; }

    /// <summary>Optional purpose label, or null.</summary>
    public string? Purpose { get; set; }
}

namespace CopilotHive.Configuration;

/// <summary>
/// Describes a single LLM model available for selection in the hive.
/// </summary>
public sealed class ModelEntry
{
    /// <summary>Model identifier (e.g. "copilot/claude-sonnet-4.6").</summary>
    public required string Name { get; set; }
    /// <summary>Maximum context window in tokens, or <c>null</c> to use the global default.</summary>
    public int? ContextWindow { get; set; }
    /// <summary>
    /// Default reasoning effort for models that support extended thinking.
    /// When set, this is used instead of the :suffix parsing in model names.
    /// null = no reasoning configuration (use existing :suffix behavior).
    /// </summary>
    public string? ReasoningEffort { get; set; }
}

/// <summary>
/// Top-level models configuration. Supports compaction_model
/// and can grow with additional model-level settings.
/// </summary>
public sealed class ModelsConfig
{
    /// <summary>
    /// Model to use for context compaction summaries (e.g. "gpt-5.4-mini").
    /// When null or empty, the main model is used for compaction (default behavior).
    /// </summary>
    public string? CompactionModel { get; set; }

    /// <summary>
    /// Enumerated models available for selection in the UI. When set, dropdowns use this list
    /// instead of free-text input.
    /// </summary>
    public List<ModelEntry>? AvailableModels { get; set; }
}

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
    /// Reasoning effort for this model entry. Only meaningful for sub_agent_models entries.
    /// For available_models, this field is informational only.
    /// </summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>Human-readable description of the model's strengths/cost/speed for sub-agent selection.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Informational flag indicating whether the model accepts image input (vision).
    /// null = unset (inherit from the matching available model, or default to false at the catalog boundary).
    /// YAML key: <c>supports_vision</c>.
    /// </summary>
    public bool? SupportsVision { get; set; }
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
    /// <para>
    /// The lists are mutable DTOs on a standalone <see cref="ModelsConfig"/>, but when the
    /// instance is owned by a <see cref="HiveConfigFile"/>, the
    /// <see cref="HiveConfigFile.Models"/> getter/setter are synchronized and deep-copying:
    /// mutating a <see cref="ModelsConfig"/> obtained from <see cref="HiveConfigFile.Models"/>
    /// does NOT affect the owner, and the stored copy is detached from the input at assignment.
    /// Mutation sites must use the synchronized APIs on <see cref="HiveConfigFile"/>.
    /// </para>
    /// </summary>
    public List<ModelEntry>? AvailableModels { get; set; }

    /// <summary>
    /// Curated list of models for sub-agent selection. When null or empty,
    /// falls back to <see cref="AvailableModels"/>.
    /// <para>
    /// Same ownership rule as <see cref="AvailableModels"/>: when the
    /// <see cref="ModelsConfig"/> is owned by a <see cref="HiveConfigFile"/>, mutations must go
    /// through the synchronized APIs on <see cref="HiveConfigFile"/> — the
    /// <see cref="HiveConfigFile.Models"/> getter/setter clone (deep-copy) on both sides.
    /// </para>
    /// </summary>
    public List<ModelEntry>? SubAgentModels { get; set; }
}

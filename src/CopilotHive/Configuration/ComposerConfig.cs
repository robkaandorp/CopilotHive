namespace CopilotHive.Configuration;

/// <summary>
/// Configuration for the Composer conversational agent.
/// </summary>
public sealed class ComposerConfig
{
    /// <summary>Model used by the Composer (e.g. "copilot/claude-sonnet-4.6"). Falls back to orchestrator model if empty.</summary>
    public string? Model { get; set; }
    /// <summary>Additional models available for switching at runtime. The <see cref="Model"/> entry is always first.</summary>
    public List<string>? Models { get; set; }
    /// <summary>Maximum context window size in tokens.</summary>
    public int ContextWindow { get; set; } = Constants.DefaultBrainContextWindow;
    /// <summary>Maximum tool-call steps per Composer request.</summary>
    public int MaxSteps { get; set; } = Constants.DefaultBrainMaxSteps;

    /// <summary>
    /// Returns a merged, deduplicated list of available models, with <see cref="Model"/> as the first entry.
    /// Falls back to <paramref name="fallback"/> if neither <see cref="Model"/> nor <see cref="Models"/> is set.
    /// </summary>
    /// <param name="fallback">Model to return when neither <see cref="Model"/> nor <see cref="Models"/> is configured.</param>
    /// <returns>A non-empty list of model identifiers.</returns>
    public List<string> GetAvailableModels(string fallback)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        if (!string.IsNullOrEmpty(Model))
        {
            result.Add(Model);
            seen.Add(Model);
        }

        if (Models is not null)
        {
            foreach (var m in Models)
            {
                if (!string.IsNullOrEmpty(m) && seen.Add(m))
                    result.Add(m);
            }
        }

        if (result.Count == 0)
        {
            result.Add(fallback);
        }

        return result;
    }
}

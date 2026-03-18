using System.Text.Json.Serialization;

namespace CopilotHive.Workers;

/// <summary>Model tier selection for a task.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModelTier
{
    /// <summary>Use the default model configured for the role.</summary>
    Default,
    /// <summary>Use the standard model.</summary>
    Standard,
    /// <summary>Use the premium (more capable, more expensive) model.</summary>
    Premium,
}

/// <summary>Extension methods for <see cref="ModelTier"/>.</summary>
public static class ModelTierExtensions
{
    /// <summary>Parses a model tier string to <see cref="ModelTier"/>.</summary>
    /// <returns>The parsed tier, defaulting to <see cref="ModelTier.Default"/> for unknown values.</returns>
    public static ModelTier ParseModelTier(string? value) => value?.ToLowerInvariant() switch
    {
        "standard" => ModelTier.Standard,
        "premium" => ModelTier.Premium,
        _ => ModelTier.Default,
    };
}

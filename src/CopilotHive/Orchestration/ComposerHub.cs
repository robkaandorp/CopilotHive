using CopilotHive.Configuration;

namespace CopilotHive.Orchestration;

/// <summary>
/// REST API endpoints for Composer model management: listing available models
/// and switching the active model at runtime without losing the session.
/// </summary>
public static class ComposerHub
{
    /// <summary>
    /// Registers the Composer model-management endpoints on the given route group.
    /// </summary>
    /// <param name="routes">The route group to map endpoints onto.</param>
    /// <param name="composer">The Composer instance to expose.</param>
    /// <param name="config">Optional global configuration that may define a shared model list.</param>
    public static void MapComposerEndpoints(this WebApplication routes, Composer? composer, HiveConfigFile? config = null)
    {
        if (composer is null) return;

        routes.MapGet("/api/composer/current-model", () =>
            Results.Ok(new { model = composer.GetStats()?.Model ?? composer.AvailableModels.FirstOrDefault() ?? "" }));

        routes.MapGet("/api/composer/models", () =>
        {
            var globalModelNames = config?.Models?.AvailableModels is { Count: > 0 } available
                ? available.Select(m => string.IsNullOrEmpty(m.ReasoningEffort)
                    ? m.Name
                    : $"{m.Name}:{m.ReasoningEffort}").ToList()
                : null;
            return Results.Ok(new { models = globalModelNames ?? composer.AvailableModels });
        });

        routes.MapPost("/api/composer/models/switch", async (string model) =>
        {
            try
            {
                var globalModelNames = config?.Models?.AvailableModels is { Count: > 0 } available
                    ? available.Select(m => string.IsNullOrEmpty(m.ReasoningEffort)
                        ? m.Name
                        : $"{m.Name}:{m.ReasoningEffort}").ToList()
                    : null;
                var validModels = globalModelNames ?? composer.AvailableModels.ToList();
                if (!validModels.Contains(model, StringComparer.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"Model '{model}' is not available. Available models: {string.Join(", ", validModels)}.",
                        nameof(model));
                }

                await composer.SwitchModelAsync(model);
                return Results.Ok(new { model = composer.GetStats()?.Model ?? model });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        routes.MapPost("/api/composer/compact", async () =>
        {
            try
            {
                var result = await composer.CompactSessionAsync();
                return Results.Ok(new { compacted = result, messageCount = composer.GetStats()?.MessageCount ?? 0 });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}

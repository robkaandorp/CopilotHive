using CopilotHive.Configuration;
using CopilotHive.Services;

using Microsoft.Extensions.AI;

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
                ? available.Select(m => m.Name).ToList()
                : null;
            // The reasoning effort is serialized snake_case by the global JSON enum converter
            // (e.g. ReasoningEffort.ExtraHigh → "extra_high").
            return Results.Ok(new
            {
                models = globalModelNames ?? composer.AvailableModels,
                reasoningEffort = composer.ReasoningEffort,
            });
        });

        routes.MapPost("/api/composer/models/switch", async (string? model, string? reasoning) =>
        {
            // Both query parameters are required: a model switch always carries an explicit
            // reasoning effort so the running Composer can never inherit a stale one.
            if (string.IsNullOrWhiteSpace(model))
                return Results.BadRequest(new { error = "The 'model' query parameter is required." });
            if (string.IsNullOrWhiteSpace(reasoning))
                return Results.BadRequest(new { error = "The 'reasoning' query parameter is required." });

            ReasoningEffort parsedReasoning;
            try
            {
                // Query strings are untyped, so the canonical wire form is parsed explicitly.
                // Parse only returns null for null/empty/whitespace, already rejected above.
                parsedReasoning = ReasoningEffortConverter.Parse(reasoning)!.Value;
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            try
            {
                var globalModelNames = config?.Models?.AvailableModels is { Count: > 0 } available
                    ? available.Select(m => m.Name).ToList()
                    : null;
                var validModels = globalModelNames ?? composer.AvailableModels.ToList();
                if (!validModels.Contains(model, StringComparer.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"Model '{model}' is not available. Available models: {string.Join(", ", validModels)}.",
                        nameof(model));
                }

                await composer.SwitchModelAsync(model, parsedReasoning);
                return Results.Ok(new
                {
                    model = composer.GetStats()?.Model ?? model,
                    reasoningEffort = composer.ReasoningEffort,
                });
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

        routes.MapPost("/api/composer/compact-partial", async (int percent) =>
        {
            try
            {
                var result = await composer.CompactOldestPercentAsync(percent);
                return Results.Ok(new { compacted = result, messageCount = composer.GetStats()?.MessageCount ?? 0 });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}

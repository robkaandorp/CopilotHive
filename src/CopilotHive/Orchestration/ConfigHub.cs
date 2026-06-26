using CopilotHive.Configuration;
using CopilotHive.Services;
using Microsoft.AspNetCore.Mvc;

namespace CopilotHive.Orchestration;

/// <summary>
/// REST API endpoints for reading and updating hive model configuration.
/// </summary>
public static class ConfigHub
{
    /// <summary>
    /// Registers the model-configuration endpoints on the given <see cref="WebApplication"/>.
    /// </summary>
    /// <param name="app">The web application to register routes on.</param>
    public static void MapConfigEndpoints(this WebApplication app)
    {
        app.MapGet("/api/config/models", ([FromServices] HiveConfigFile? config) =>
        {
            if (config is null)
                return Results.NotFound(new { error = "Config repo not configured." });

            return Results.Ok(new
            {
                orchestrator   = config.Orchestrator.Model,
                composer       = config.Composer?.Model,
                compaction     = config.Models?.CompactionModel,
                workers        = config.Workers.ToDictionary(
                    kv => kv.Key,
                    kv => new { model = kv.Value.Model, premiumModel = kv.Value.PremiumModel }),
                availableModels = config.Models?.AvailableModels,
            });
        });

        app.MapMethods("/api/config/models", ["PATCH"], async (
            ModelConfigUpdate update,
            [FromServices] ConfigModelService? svc) =>
        {
            if (svc is null)
                return Results.Problem("Config repo is not configured — model changes cannot be persisted.");

            await svc.SaveModelConfigAsync(update);
            return Results.Ok(new { saved = true, description = update.Description });
        });

        // Discover available models from providers
        app.MapGet("/api/config/models/discover", async ([FromServices] ModelDiscoveryService? svc) =>
        {
            if (svc is null)
                return Results.Problem("Model discovery service is not configured.");
            var models = await svc.DiscoverAllAsync();
            return Results.Ok(models);
        });

        // Add a model to available_models
        app.MapPost("/api/config/available-models", async (AvailableModelRequest req, [FromServices] ConfigModelService? svc) =>
        {
            if (svc is null)
                return Results.Problem("Config service is not configured.");
            try
            {
                await svc.AddAvailableModelAsync(req.Name, req.ContextWindow, req.ReasoningEffort);
                return Results.Ok(new { saved = true });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // Update a model
        app.MapPut("/api/config/available-models/{name}", async (string name, AvailableModelRequest req, [FromServices] ConfigModelService? svc) =>
        {
            if (svc is null)
                return Results.Problem("Config service is not configured.");
            try
            {
                await svc.UpdateAvailableModelAsync(name, req.ContextWindow, req.ReasoningEffort);
                return Results.Ok(new { saved = true });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // Remove a model
        app.MapDelete("/api/config/available-models/{name}", async (string name, [FromServices] ConfigModelService? svc) =>
        {
            if (svc is null)
                return Results.Problem("Config service is not configured.");
            var removed = await svc.RemoveAvailableModelAsync(name);
            return removed ? Results.Ok(new { removed = true }) : Results.NotFound(new { error = $"Model '{name}' not found." });
        });
    }
}

/// <summary>
/// Request body for adding or updating an available model.
/// </summary>
/// <param name="Name">Model name (used for add; ignored for update where the route name is authoritative).</param>
/// <param name="ContextWindow">Optional context window in tokens.</param>
/// <param name="ReasoningEffort">Optional default reasoning effort.</param>
public sealed record AvailableModelRequest(string Name, int? ContextWindow, string? ReasoningEffort);

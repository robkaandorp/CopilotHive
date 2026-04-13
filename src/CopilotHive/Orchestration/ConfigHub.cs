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
                workers        = config.Workers,
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
    }
}

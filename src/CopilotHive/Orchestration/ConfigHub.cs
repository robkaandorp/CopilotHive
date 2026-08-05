using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;

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

            // Reasoning effort is stored as string? in the YAML-bound config classes but is
            // projected here as the ReasoningEffort enum. The global JsonStringEnumConverter
            // renders it snake_case (e.g. "extra_high"). A value a dynamic reload left
            // unrecognised degrades to null rather than failing the whole response.
            return Results.Ok(new
            {
                orchestrator   = config.Orchestrator.Model,
                composer       = config.Composer?.Model,
                compaction     = config.Models?.CompactionModel,
                workers        = config.Workers.ToDictionary(
                    kv => kv.Key,
                    kv => new { model = kv.Value.Model, premiumModel = kv.Value.PremiumModel }),
                orchestratorReasoningEffort = ConfigModelService.ParseLenient(config.Orchestrator.ReasoningEffort),
                composerReasoningEffort     = ConfigModelService.ParseLenient(config.Composer?.ReasoningEffort),
                workerReasoningEffort       = config.Workers.ToDictionary(
                    kv => kv.Key,
                    kv => ConfigModelService.ParseLenient(kv.Value.ReasoningEffort)),
                workerPremiumReasoningEffort = config.Workers.ToDictionary(
                    kv => kv.Key,
                    kv => ConfigModelService.ParseLenient(kv.Value.PremiumReasoningEffort)),
                subAgentModelReasoning = config.Models?.SubAgentModels?
                    .Where(m => !string.IsNullOrEmpty(m.Name))
                    .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => ConfigModelService.ParseLenient(g.First().ReasoningEffort)),
                availableModels = config.Models?.AvailableModels?
                    .Select(m => new { m.Name, m.ContextWindow, m.Description, m.SupportsVision }),
                // Projected entry-by-entry rather than returned as raw ModelEntry objects:
                // ModelEntry.ReasoningEffort is deliberately string? at the YAML boundary, so
                // serializing the entity directly would leak a raw string (and an unrecognised
                // stored value such as "turbo" verbatim) into an otherwise enum-typed response.
                subAgentModels = config.Models?.SubAgentModels?
                    .Select(m => new
                    {
                        m.Name,
                        m.ContextWindow,
                        reasoningEffort = ConfigModelService.ParseLenient(m.ReasoningEffort),
                        m.Description,
                        m.SupportsVision
                    }),
            });
        });

        app.MapMethods("/api/config/models", ["PATCH"], async (
            ModelConfigUpdate update,
            [FromServices] ConfigModelService? svc,
            CancellationToken ct) =>
        {
            if (svc is null)
                return Results.Problem("Config repo is not configured — model changes cannot be persisted.");

            try
            {
                await svc.SaveModelConfigAsync(update, ct);
                return Results.Ok(new { saved = true, description = update.Description });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
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
                await svc.AddAvailableModelAsync(req.Name, req.ContextWindow, req.Description, req.SupportsVision);
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
            name = Uri.UnescapeDataString(name);
            try
            {
                await svc.UpdateAvailableModelAsync(name, req.ContextWindow, req.Description, req.SupportsVision);
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
            name = Uri.UnescapeDataString(name);
            var removed = await svc.RemoveAvailableModelAsync(name);
            return removed ? Results.Ok(new { removed = true }) : Results.NotFound(new { error = $"Model '{name}' not found." });
        });

        // Add a model to sub_agent_models
        app.MapPost("/api/config/sub-agent-models", async (SubAgentModelRequest req, [FromServices] ConfigModelService? svc) =>
        {
            if (svc is null)
                return Results.Problem("Config service is not configured.");
            try
            {
                await svc.AddSubAgentModelAsync(req.Name, req.ContextWindow, req.ReasoningEffort, req.Description, req.SupportsVision);
                return Results.Ok(new { saved = true });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // Update a sub-agent model
        app.MapPut("/api/config/sub-agent-models/{name}", async (string name, SubAgentModelRequest req, [FromServices] ConfigModelService? svc) =>
        {
            if (svc is null)
                return Results.Problem("Config service is not configured.");
            name = Uri.UnescapeDataString(name);
            try
            {
                await svc.UpdateSubAgentModelAsync(name, req.ContextWindow, req.ReasoningEffort, req.Description, req.SupportsVision);
                return Results.Ok(new { saved = true });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // Remove a sub-agent model
        app.MapDelete("/api/config/sub-agent-models/{name}", async (string name, [FromServices] ConfigModelService? svc) =>
        {
            if (svc is null)
                return Results.Problem("Config service is not configured.");
            name = Uri.UnescapeDataString(name);
            var removed = await svc.RemoveSubAgentModelAsync(name);
            return removed ? Results.Ok(new { removed = true }) : Results.NotFound(new { error = $"Model '{name}' not found." });
        });

        // ── Repositories ────────────────────────────────────────────────────

        // List repositories
        app.MapGet("/api/config/repositories", ([FromServices] HiveConfigFile? config) =>
        {
            if (config is null)
                return Results.NotFound(new { error = "Config repo not configured." });
            return Results.Ok(config.Repositories);
        });

        // Add a repository
        app.MapPost("/api/config/repositories", async (
            RepositoryRequest req,
            [FromServices] ConfigModelService? svc,
            [FromServices] IBrainRepoManager? repoManager) =>
        {
            if (svc is null)
                return Results.Problem("Config service is not configured.");
            try
            {
                await svc.AddRepositoryAsync(req.Name, req.Url, req.DefaultBranch, req.Release);
                return Results.Ok(new { saved = true });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // Update a repository
        app.MapPut("/api/config/repositories/{name}", async (
            string name,
            RepositoryRequest req,
            [FromServices] ConfigModelService? svc,
            [FromServices] IBrainRepoManager? repoManager) =>
        {
            if (svc is null)
                return Results.Problem("Config service is not configured.");
            try
            {
                await svc.UpdateRepositoryAsync(name, req.Url, req.DefaultBranch, req.Release);
                return Results.Ok(new { saved = true });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // Remove a repository
        app.MapDelete("/api/config/repositories/{name}", async (string name, [FromServices] ConfigModelService? svc) =>
        {
            if (svc is null)
                return Results.Problem("Config service is not configured.");
            var removed = await svc.RemoveRepositoryAsync(name);
            return removed ? Results.Ok(new { removed = true }) : Results.NotFound(new { error = $"Repository '{name}' not found." });
        });

        // List remote branches for a repository
        app.MapGet("/api/config/repositories/{name}/branches", async (
            string name,
            [FromServices] IBrainRepoManager? repoManager,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (repoManager is null)
                return Results.Problem("Repository manager is not available.", statusCode: 503);
            try
            {
                var branches = await repoManager.ListRemoteBranchesAsync(name, ct);
                return Results.Ok(branches);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("is not cloned"))
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to list branches for repository '{Name}'", name);
                return Results.Problem("Failed to list branches for this repository.", statusCode: 500);
            }
        });

        // ── Orchestrator settings ───────────────────────────────────────────

        // Get orchestrator settings
        app.MapGet("/api/config/orchestrator", ([FromServices] HiveConfigFile? config) =>
        {
            if (config is null)
                return Results.NotFound(new { error = "Config repo not configured." });
            return Results.Ok(config.Orchestrator);
        });

        // Update orchestrator settings
        app.MapMethods("/api/config/orchestrator", ["PATCH"], async (
            OrchestratorSettingsUpdate update,
            [FromServices] ConfigModelService? svc) =>
        {
            if (svc is null)
                return Results.Problem("Config service is not configured.");
            await svc.UpdateOrchestratorSettingsAsync(update);
            return Results.Ok(new { saved = true });
        });

        // ── Worker settings ─────────────────────────────────────────────────

        // Get workers
        app.MapGet("/api/config/workers", ([FromServices] HiveConfigFile? config) =>
        {
            if (config is null)
                return Results.NotFound(new { error = "Config repo not configured." });
            return Results.Ok(config.Workers.ToDictionary(
                kv => kv.Key,
                kv => new { model = kv.Value.Model, premiumModel = kv.Value.PremiumModel, contextWindow = kv.Value.ContextWindow }));
        });

        // Update worker context windows
        app.MapMethods("/api/config/workers", ["PATCH"], async (
            Dictionary<string, int> contextWindows,
            [FromServices] ConfigModelService? svc) =>
        {
            if (svc is null)
                return Results.Problem("Config service is not configured.");
            await svc.UpdateWorkerContextWindowsAsync(contextWindows);
            return Results.Ok(new { saved = true });
        });

        // ── Composer settings ───────────────────────────────────────────────

        // Get composer settings
        app.MapGet("/api/config/composer", ([FromServices] HiveConfigFile? config) =>
        {
            if (config is null)
                return Results.NotFound(new { error = "Config repo not configured." });
            return Results.Ok(config.Composer);
        });

        // Update composer settings
        app.MapMethods("/api/config/composer", ["PATCH"], async (
            ComposerSettingsUpdate update,
            [FromServices] ConfigModelService? svc) =>
        {
            if (svc is null)
                return Results.Problem("Config service is not configured.");
            await svc.UpdateComposerSettingsAsync(update.MaxSteps);
            return Results.Ok(new { saved = true });
        });
    }
}

/// <summary>
/// Request body for adding or updating an available model.
/// </summary>
/// <param name="Name">Model name (used for add; ignored for update where the route name is authoritative).</param>
/// <param name="ContextWindow">Optional context window in tokens.</param>
/// <param name="Description">Optional human-readable description.</param>
/// <param name="SupportsVision">Informational vision flag: <c>true</c>, <c>false</c>, or <c>null</c> for unset.</param>
public sealed record AvailableModelRequest(string Name, int? ContextWindow, string? Description = null, bool? SupportsVision = null);

/// <summary>
/// Request body for adding or updating a sub-agent model.
/// </summary>
/// <param name="Name">Model name (used for add; ignored for update where the route name is authoritative).</param>
/// <param name="ContextWindow">Optional context window in tokens.</param>
/// <param name="ReasoningEffort">
/// Optional default reasoning effort. Wire values are snake_case (<c>none</c>, <c>low</c>,
/// <c>medium</c>, <c>high</c>, <c>extra_high</c>); an unknown value is rejected with a 400 by
/// the global JSON enum converter.
/// </param>
/// <param name="Description">Optional human-readable description.</param>
/// <param name="SupportsVision">Informational vision flag: <c>true</c>, <c>false</c>, or <c>null</c> for unset (inherit).</param>
public sealed record SubAgentModelRequest(string Name, int? ContextWindow, ReasoningEffort? ReasoningEffort, string? Description = null, bool? SupportsVision = null);

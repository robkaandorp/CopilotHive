using CopilotHive.Services;

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
    /// <param name="facade">The model-catalog facade backing the model endpoints.</param>
    public static void MapConfigEndpoints(this WebApplication app, IConfigFacade facade)
    {
        app.MapGet("/api/config/models", () =>
        {
            var result = facade.GetModels();
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);

            // Reasoning effort is projected entry-by-entry through ParseLenient by the facade
            // (never leaked as a raw string); the global JsonStringEnumConverter renders the
            // enum snake_case (e.g. "extra_high") on the wire.
            var dto = result.Value!;
            return Results.Ok(dto);
        });

        app.MapMethods("/api/config/models", ["PATCH"], async (
            ModelConfigUpdate update,
            CancellationToken ct) =>
        {
            var result = await facade.SaveModelsAsync(update, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // Discover available models from providers
        app.MapGet("/api/config/models/discover", async () =>
        {
            var result = await facade.DiscoverModelsAsync();
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // Add a model to available_models
        app.MapPost("/api/config/available-models", async (AvailableModelRequest req) =>
        {
            var result = await facade.AddAvailableModelAsync(req);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // Update a model
        app.MapPut("/api/config/available-models/{name}", async (string name, AvailableModelRequest req) =>
        {
            name = Uri.UnescapeDataString(name);
            var result = await facade.UpdateAvailableModelAsync(name, req);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // Remove a model
        app.MapDelete("/api/config/available-models/{name}", async (string name) =>
        {
            name = Uri.UnescapeDataString(name);
            var result = await facade.RemoveAvailableModelAsync(name);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // Add a model to sub_agent_models
        app.MapPost("/api/config/sub-agent-models", async (SubAgentModelRequest req) =>
        {
            var result = await facade.AddSubAgentModelAsync(req);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // Update a sub-agent model
        app.MapPut("/api/config/sub-agent-models/{name}", async (string name, SubAgentModelRequest req) =>
        {
            name = Uri.UnescapeDataString(name);
            var result = await facade.UpdateSubAgentModelAsync(name, req);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // Remove a sub-agent model
        app.MapDelete("/api/config/sub-agent-models/{name}", async (string name) =>
        {
            name = Uri.UnescapeDataString(name);
            var result = await facade.RemoveSubAgentModelAsync(name);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // ── Repositories ────────────────────────────────────────────────────

        // List repositories
        app.MapGet("/api/config/repositories", () =>
        {
            var result = facade.GetRepositories();
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // Add a repository
        app.MapPost("/api/config/repositories", async (RepositoryRequest req) =>
        {
            var result = await facade.AddRepositoryAsync(req);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // Update a repository
        app.MapPut("/api/config/repositories/{name}", async (string name, RepositoryRequest req) =>
        {
            var result = await facade.UpdateRepositoryAsync(name, req);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // Remove a repository
        app.MapDelete("/api/config/repositories/{name}", async (string name) =>
        {
            var result = await facade.RemoveRepositoryAsync(name);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // List remote branches for a repository
        app.MapGet("/api/config/repositories/{name}/branches", async (string name, CancellationToken ct) =>
        {
            var result = await facade.GetBranchesAsync(name, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // ── Orchestrator settings ───────────────────────────────────────────

        // Get orchestrator settings
        app.MapGet("/api/config/orchestrator", () =>
        {
            var result = facade.GetOrchestrator();
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // Update orchestrator settings
        app.MapMethods("/api/config/orchestrator", ["PATCH"], async (
            OrchestratorSettingsUpdate update) =>
        {
            var result = await facade.SaveOrchestratorAsync(update);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // ── Worker settings ─────────────────────────────────────────────────

        // Get workers
        app.MapGet("/api/config/workers", () =>
        {
            var result = facade.GetWorkers();
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // Update worker context windows
        app.MapMethods("/api/config/workers", ["PATCH"], async (
            Dictionary<string, int> contextWindows) =>
        {
            var result = await facade.SaveWorkersAsync(contextWindows);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // ── Composer settings ───────────────────────────────────────────────

        // Get composer settings (runtime-effective values, not raw storage)
        app.MapGet("/api/config/composer", () =>
        {
            var result = facade.GetComposer();
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // Update composer settings
        app.MapMethods("/api/config/composer", ["PATCH"], async (
            ComposerSettingsUpdate update,
            CancellationToken ct) =>
        {
            var result = await facade.SaveComposerAsync(update, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });
    }

    /// <summary>
    /// Maps a <see cref="FacadeErrorKind"/> from the model-catalog facade to the exact HTTP
    /// response the pre-facade handlers produced: <c>NotFound</c>/<c>Conflict</c>/<c>BadRequest</c>
    /// return a JSON <c>{error}</c> body; <c>NotConfigured</c>, <c>ServiceUnavailable</c> and
    /// <c>Internal</c> return a problem-details body. <see cref="FacadeErrorKind.None"/> is a
    /// programming error and throws.
    /// </summary>
    /// <param name="kind">The failure category reported by the facade.</param>
    /// <param name="error">The human-readable error message.</param>
    /// <returns>The HTTP result matching the pre-facade handler behaviour.</returns>
    private static IResult MapModelFacadeError(FacadeErrorKind kind, string? error)
    {
        return kind switch
        {
            FacadeErrorKind.NotFound => Results.NotFound(new { error }),
            FacadeErrorKind.Conflict => Results.Conflict(new { error }),
            FacadeErrorKind.BadRequest => Results.BadRequest(new { error }),
            FacadeErrorKind.NotConfigured => Results.Problem(error),
            FacadeErrorKind.ServiceUnavailable => Results.Problem(error, statusCode: 503),
            FacadeErrorKind.Internal => Results.Problem(error, statusCode: 500),
            _ => throw new InvalidOperationException($"Unexpected facade error kind: {kind}."),
        };
    }
}

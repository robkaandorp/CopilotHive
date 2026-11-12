using CopilotHive.Security;
using CopilotHive.Services;

namespace CopilotHive.Orchestration;

/// <summary>
/// REST API endpoints for reading and updating hive model configuration.
/// </summary>
public static class ConfigHub
{
    /// <summary>
    /// Registers the configuration endpoints on the given <see cref="WebApplication"/>.
    /// </summary>
    /// <remarks>
    /// EVERY route delegates to <see cref="IConfigFacade"/> with the REQUEST's identity
    /// (<see cref="HttpContext.User"/>), and every facade operation — each full configuration
    /// projection AND every write — requires <see cref="HiveCapability.GlobalAdmin"/>, re-resolved
    /// FRESH by the facade on every call. The mutating routes (POST/PUT/PATCH/DELETE) additionally
    /// carry <see cref="ApiEndpoints.PreBindingAuthorization"/> metadata so an authenticated but insufficient,
    /// demoted or disabled caller is refused with 403 BEFORE a typed body is bound, while an
    /// authorized caller keeps the unchanged binding behaviour.
    /// <para>
    /// The pre-binding middleware itself is registered by <c>Program.cs</c> immediately AFTER
    /// <c>UseAuthentication</c>/<c>UseAuthorization</c> — this hub is mapped EARLIER in startup, so
    /// registering it here would place it before authentication had resolved the caller and every
    /// request would look anonymous. The metadata attached below is inert until that one late
    /// registration runs; it is global, so it covers these routes whatever their mapping order.
    /// </para>
    /// </remarks>
    /// <param name="app">The web application to register routes on.</param>
    /// <param name="facade">The configuration facade backing the endpoints.</param>
    public static void MapConfigEndpoints(this WebApplication app, IConfigFacade facade)
    {

        app.MapGet("/api/config/models", async (HttpContext context,
            CancellationToken ct) =>
        {
            var result = await facade.GetModelsAsync(context.User, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);

            // Reasoning effort is projected entry-by-entry through ParseLenient by the facade
            // (never leaked as a raw string); the global JsonStringEnumConverter renders the
            // enum snake_case (e.g. "extra_high") on the wire.
            var dto = result.Value!;
            return Results.Ok(dto);
        });

        app.MapMethods("/api/config/models", ["PATCH"], async (
            HttpContext context,
            ModelConfigUpdate update,
            CancellationToken ct) =>
        {
            var result = await facade.SaveModelsAsync(update, context.User, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        })
            .WithMetadata(new ApiEndpoints.PreBindingAuthorization(HiveCapability.GlobalAdmin, "config.models"));

        // Discover available models from providers
        app.MapGet("/api/config/models/discover", async (HttpContext context,
            CancellationToken ct) =>
        {
            var result = await facade.DiscoverModelsAsync(context.User, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // Add a model to available_models
        app.MapPost("/api/config/available-models", async (AvailableModelRequest req, HttpContext context,
            CancellationToken ct) =>
        {
            var result = await facade.AddAvailableModelAsync(req, context.User, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        })
            .WithMetadata(new ApiEndpoints.PreBindingAuthorization(HiveCapability.GlobalAdmin, "config.available-models.add"));

        // Update a model
        app.MapPut("/api/config/available-models/{name}", async (string name, AvailableModelRequest req,
            HttpContext context, CancellationToken ct) =>
        {
            name = Uri.UnescapeDataString(name);
            var result = await facade.UpdateAvailableModelAsync(name, req, context.User, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        })
            .WithMetadata(new ApiEndpoints.PreBindingAuthorization(HiveCapability.GlobalAdmin, "config.available-models.update"));

        // Remove a model
        app.MapDelete("/api/config/available-models/{name}", async (string name, HttpContext context,
            CancellationToken ct) =>
        {
            name = Uri.UnescapeDataString(name);
            var result = await facade.RemoveAvailableModelAsync(name, context.User, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        })
            .WithMetadata(new ApiEndpoints.PreBindingAuthorization(HiveCapability.GlobalAdmin, "config.available-models.remove"));

        // Add a model to sub_agent_models
        app.MapPost("/api/config/sub-agent-models", async (SubAgentModelRequest req, HttpContext context,
            CancellationToken ct) =>
        {
            var result = await facade.AddSubAgentModelAsync(req, context.User, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        })
            .WithMetadata(new ApiEndpoints.PreBindingAuthorization(HiveCapability.GlobalAdmin, "config.sub-agent-models.add"));

        // Update a sub-agent model
        app.MapPut("/api/config/sub-agent-models/{name}", async (string name, SubAgentModelRequest req,
            HttpContext context, CancellationToken ct) =>
        {
            name = Uri.UnescapeDataString(name);
            var result = await facade.UpdateSubAgentModelAsync(name, req, context.User, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        })
            .WithMetadata(new ApiEndpoints.PreBindingAuthorization(HiveCapability.GlobalAdmin, "config.sub-agent-models.update"));

        // Remove a sub-agent model
        app.MapDelete("/api/config/sub-agent-models/{name}", async (string name, HttpContext context,
            CancellationToken ct) =>
        {
            name = Uri.UnescapeDataString(name);
            var result = await facade.RemoveSubAgentModelAsync(name, context.User, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        })
            .WithMetadata(new ApiEndpoints.PreBindingAuthorization(HiveCapability.GlobalAdmin, "config.sub-agent-models.remove"));

        // ── Repositories ────────────────────────────────────────────────────

        // List repositories
        app.MapGet("/api/config/repositories", async (HttpContext context,
            CancellationToken ct) =>
        {
            var result = await facade.GetRepositoriesAsync(context.User, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // Add a repository
        app.MapPost("/api/config/repositories", async (RepositoryRequest req, HttpContext context,
            CancellationToken ct) =>
        {
            var result = await facade.AddRepositoryAsync(req, context.User, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        })
            .WithMetadata(new ApiEndpoints.PreBindingAuthorization(HiveCapability.GlobalAdmin, "config.repositories.add"));

        // Update a repository
        app.MapPut("/api/config/repositories/{name}", async (string name, RepositoryRequest req,
            HttpContext context, CancellationToken ct) =>
        {
            var result = await facade.UpdateRepositoryAsync(name, req, context.User, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        })
            .WithMetadata(new ApiEndpoints.PreBindingAuthorization(HiveCapability.GlobalAdmin, "config.repositories.update"));

        // Remove a repository
        app.MapDelete("/api/config/repositories/{name}", async (string name, HttpContext context,
            CancellationToken ct) =>
        {
            var result = await facade.RemoveRepositoryAsync(name, context.User, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        })
            .WithMetadata(new ApiEndpoints.PreBindingAuthorization(HiveCapability.GlobalAdmin, "config.repositories.remove"));

        // List remote branches for a repository
        app.MapGet("/api/config/repositories/{name}/branches", async (string name, HttpContext context,
            CancellationToken ct) =>
        {
            var result = await facade.GetBranchesAsync(name, context.User, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // ── Orchestrator settings ───────────────────────────────────────────

        // Get orchestrator settings
        app.MapGet("/api/config/orchestrator", async (HttpContext context,
            CancellationToken ct) =>
        {
            var result = await facade.GetOrchestratorAsync(context.User, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // Update orchestrator settings
        app.MapMethods("/api/config/orchestrator", ["PATCH"], async (
            HttpContext context,
            OrchestratorSettingsUpdate update,
            CancellationToken ct) =>
        {
            var result = await facade.SaveOrchestratorAsync(update, context.User, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        })
            .WithMetadata(new ApiEndpoints.PreBindingAuthorization(HiveCapability.GlobalAdmin, "config.orchestrator"));

        // ── Worker settings ─────────────────────────────────────────────────

        // Get workers
        app.MapGet("/api/config/workers", async (HttpContext context,
            CancellationToken ct) =>
        {
            var result = await facade.GetWorkersAsync(context.User, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // Update worker context windows
        app.MapMethods("/api/config/workers", ["PATCH"], async (
            HttpContext context,
            Dictionary<string, int> contextWindows,
            CancellationToken ct) =>
        {
            var result = await facade.SaveWorkersAsync(contextWindows, context.User, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        })
            .WithMetadata(new ApiEndpoints.PreBindingAuthorization(HiveCapability.GlobalAdmin, "config.workers"));

        // ── Composer settings ───────────────────────────────────────────────

        // Get composer settings (runtime-effective values, not raw storage)
        app.MapGet("/api/config/composer", async (HttpContext context,
            CancellationToken ct) =>
        {
            var result = await facade.GetComposerAsync(context.User, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        // Update composer settings
        // IDENTITY IS PER REQUEST: this GLOBAL write forwards the request's authenticated principal
        // (HttpContext.User) to the facade, which re-resolves the caller's CURRENT role/enabled state
        // and requires GlobalAdmin.
        app.MapMethods("/api/config/composer", ["PATCH"], async (
            HttpContext context,
            ComposerSettingsUpdate update,
            CancellationToken ct) =>
        {
            var result = await facade.SaveComposerAsync(update, context.User, ct);
            if (!result.Success)
                return MapModelFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        })
            .WithMetadata(new ApiEndpoints.PreBindingAuthorization(HiveCapability.GlobalAdmin, "config.composer"));
    }

    /// <summary>
    /// Maps a <see cref="FacadeErrorKind"/> from the model-catalog facade to the exact HTTP
    /// response the pre-facade handlers produced: <c>NotFound</c>/<c>Conflict</c>/<c>BadRequest</c>
    /// return a JSON <c>{error}</c> body; <c>Forbidden</c> returns HTTP 403 with the same body shape
    /// (the AUTHORIZATION answer of the global Composer write); <c>NotConfigured</c>,
    /// <c>ServiceUnavailable</c> and <c>Internal</c> return a problem-details body.
    /// <see cref="FacadeErrorKind.None"/> is a programming error and throws.
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
            FacadeErrorKind.Forbidden => Results.Json(new { error }, statusCode: StatusCodes.Status403Forbidden),
            FacadeErrorKind.NotConfigured => Results.Problem(error),
            FacadeErrorKind.ServiceUnavailable => Results.Problem(error, statusCode: 503),
            FacadeErrorKind.Internal => Results.Problem(error, statusCode: 500),
            _ => throw new InvalidOperationException($"Unexpected facade error kind: {kind}."),
        };
    }
}

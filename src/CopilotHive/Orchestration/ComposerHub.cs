using CopilotHive.Configuration;
using CopilotHive.Services;

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
    /// <remarks>
    /// <para>
    /// The handlers are thin adapters over <see cref="IComposerFacade"/>: all Composer work
    /// (authorization, validation, switching, compaction) lives in the facade, and the mapping from
    /// <see cref="FacadeErrorKind"/> to an HTTP status happens in <see cref="MapComposerFacadeError"/>.
    /// The facade is passed EXPLICITLY rather than resolved from DI so tests can substitute one.
    /// </para>
    /// <para>
    /// IDENTITY IS PER REQUEST: each handler takes the request's <see cref="HttpContext"/> and
    /// forwards its <see cref="HttpContext.User"/> — the authenticated principal the cookie handler
    /// produced for THIS request — to the facade, which re-resolves current role/enabled state from
    /// the trusted directory. Nothing is cached between requests, and the endpoint never inspects a
    /// claim itself.
    /// </para>
    /// </remarks>
    /// <param name="routes">The route group to map endpoints onto.</param>
    /// <param name="composer">
    /// The Composer instance to expose. When <c>null</c>, NO routes are mapped at all — the
    /// endpoints 404, exactly as before.
    /// </param>
    /// <param name="composerFacade">The facade the handlers delegate to.</param>
    /// <param name="config">Optional global configuration; retained for existing call sites.</param>
    public static void MapComposerEndpoints(
        this WebApplication routes, Composer? composer, IComposerFacade composerFacade, HiveConfigFile? config = null)
    {
        if (composer is null) return;

        // Frozen contract: when the Composer is not connected / has no active model, the
        // endpoint returns HTTP 200 with {"model":null} — it NEVER fabricates a value from
        // the catalog (no FirstOrDefault fallback). A null model is a SUCCESSFUL read.
        routes.MapGet("/api/composer/current-model", async (HttpContext context) =>
        {
            var result = await composerFacade.GetCurrentModelAsync(
                context.User, context.RequestAborted);
            if (!result.Success)
                return MapComposerFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        routes.MapGet("/api/composer/models", async (HttpContext context) =>
        {
            var result = await composerFacade.GetModelsAsync(context.User, context.RequestAborted);
            if (!result.Success)
                return MapComposerFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        routes.MapPost("/api/composer/models/switch", async (
            HttpContext context, string? model, string? reasoning) =>
        {
            var result = await composerFacade.SwitchModelAsync(
                context.User, model, reasoning, context.RequestAborted);
            if (!result.Success)
                return MapComposerFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        routes.MapPost("/api/composer/compact", async (HttpContext context) =>
        {
            var result = await composerFacade.CompactAsync(context.User, context.RequestAborted);
            if (!result.Success)
                return MapComposerFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        routes.MapPost("/api/composer/compact-partial", async (HttpContext context, int percent) =>
        {
            var result = await composerFacade.CompactPartialAsync(
                context.User, percent, context.RequestAborted);
            if (!result.Success)
                return MapComposerFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });
    }

    /// <summary>
    /// Maps a <see cref="FacadeErrorKind"/> from the Composer facade to the exact HTTP response
    /// the pre-facade handlers produced: <c>BadRequest</c> returns HTTP 400 with a JSON
    /// <c>{error}</c> body and <c>Forbidden</c> returns HTTP 403 with the same body shape (an
    /// AUTHORIZATION answer, always produced outside the compaction catch-all, so it can never be
    /// reported as a 400). <c>NotConfigured</c> returns a 503 problem-details body — through the
    /// real endpoints it is unreachable (a null Composer maps no routes at all), but a substituted
    /// facade can report it, so the case is handled explicitly instead of falling through.
    /// Every other kind is a programming error and throws.
    /// </summary>
    /// <param name="kind">The failure category reported by the facade.</param>
    /// <param name="error">The human-readable error message.</param>
    /// <returns>The HTTP result matching the pre-facade handler behaviour.</returns>
    private static IResult MapComposerFacadeError(FacadeErrorKind kind, string? error)
    {
        return kind switch
        {
            FacadeErrorKind.BadRequest => Results.BadRequest(new { error }),
            FacadeErrorKind.Forbidden => Results.Json(new { error }, statusCode: StatusCodes.Status403Forbidden),
            FacadeErrorKind.NotConfigured => Results.Problem(error, statusCode: 503),
            _ => throw new InvalidOperationException($"Unexpected facade error kind: {kind}."),
        };
    }
}

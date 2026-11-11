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
    /// </para>
    /// <para>
    /// THE FACADE IS RESOLVED PER REQUEST, from <see cref="HttpContext.RequestServices"/>, so the
    /// request's OWN scope supplies it. That is what lets the registration be scoped (the gate-selected
    /// facade may hold the scoped circuit accessor) without any host startup closure capturing a scoped
    /// service — the reason this hub no longer receives an <c>app.Services</c>-resolved instance.
    /// A NON-NULL <paramref name="composerFacade"/> is still honoured as an explicit OVERRIDE so a test
    /// can substitute one; it is never resolved from the root provider here.
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
    /// <param name="composerFacade">
    /// An explicit facade override for tests, or <c>null</c> to resolve the scoped
    /// <see cref="IComposerFacade"/> from the request's own service provider.
    /// </param>
    /// <param name="config">Optional global configuration; retained for existing call sites.</param>
    public static void MapComposerEndpoints(
        this WebApplication routes, Composer? composer, IComposerFacade? composerFacade = null, HiveConfigFile? config = null)
    {
        if (composer is null) return;

        // Frozen contract: when the Composer is not connected / has no active model, the
        // endpoint returns HTTP 200 with {"model":null} — it NEVER fabricates a value from
        // the catalog (no FirstOrDefault fallback). A null model is a SUCCESSFUL read.
        routes.MapGet("/api/composer/current-model", async (HttpContext context) =>
        {
            var result = await FacadeFor(context, composerFacade).GetCurrentModelAsync(
                context.User, context.RequestAborted);
            if (!result.Success)
                return MapComposerFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        routes.MapGet("/api/composer/models", async (HttpContext context) =>
        {
            var result = await FacadeFor(context, composerFacade).GetModelsAsync(context.User, context.RequestAborted);
            if (!result.Success)
                return MapComposerFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        routes.MapPost("/api/composer/models/switch", async (
            HttpContext context, string? model, string? reasoning) =>
        {
            var result = await FacadeFor(context, composerFacade).SwitchModelAsync(
                context.User, model, reasoning, context.RequestAborted);
            if (!result.Success)
                return MapComposerFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        routes.MapPost("/api/composer/compact", async (HttpContext context) =>
        {
            var result = await FacadeFor(context, composerFacade).CompactAsync(context.User, context.RequestAborted);
            if (!result.Success)
                return MapComposerFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });

        routes.MapPost("/api/composer/compact-partial", async (HttpContext context, int percent) =>
        {
            var result = await FacadeFor(context, composerFacade).CompactPartialAsync(
                context.User, percent, context.RequestAborted);
            if (!result.Success)
                return MapComposerFacadeError(result.Kind, result.Error);
            return Results.Ok(result.Value!);
        });
    }

    /// <summary>
    /// The facade for ONE request: the explicit test override when one was supplied, otherwise the
    /// request's OWN scoped <see cref="IComposerFacade"/>.
    /// </summary>
    /// <param name="context">The request being handled.</param>
    /// <param name="override">The explicit override passed to <see cref="MapComposerEndpoints"/>, or <c>null</c>.</param>
    /// <returns>The facade the handler must use.</returns>
    private static IComposerFacade FacadeFor(HttpContext context, IComposerFacade? @override)
    {
        if (@override is not null)
            return @override;

        return context.RequestServices.GetRequiredService<IComposerFacade>();
    }

    /// <summary>
    /// Maps a <see cref="FacadeErrorKind"/> from the Composer facade to the exact HTTP response
    /// the pre-facade handlers produced: <c>BadRequest</c> returns HTTP 400 with a JSON
    /// <c>{error}</c> body and <c>Forbidden</c> returns HTTP 403 with the same body shape (an
    /// AUTHORIZATION answer, always produced outside the compaction catch-all, so it can never be
    /// reported as a 400). <c>NotConfigured</c> returns a 503 problem-details body — through the
    /// real endpoints it is unreachable (a null Composer maps no routes at all), but a substituted
    /// facade can report it, so the case is handled explicitly instead of falling through.
    /// </summary>
    /// <remarks>
    /// The two ADDITIVE recovery kinds are mapped explicitly and nothing else moves:
    /// <c>ServiceUnavailable</c> (the runtime is failed, or its connect is still deferred after the
    /// operation's own retry) is a 503 problem-details body, and <c>Conflict</c> (a runtime removed
    /// while the request was in flight) is a 409 <c>{error}</c> body carrying the safe retry message.
    /// Every existing mapping above is byte-unchanged.
    /// </remarks>
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
            // Additive recovery kinds (the per-user runtime path): explicitly handled, never a
            // fall-through, and never a silent remap of an existing kind.
            FacadeErrorKind.ServiceUnavailable => Results.Problem(error, statusCode: StatusCodes.Status503ServiceUnavailable),
            FacadeErrorKind.Conflict => Results.Json(new { error }, statusCode: StatusCodes.Status409Conflict),
            _ => throw new InvalidOperationException($"Unexpected facade error kind: {kind}."),
        };
    }
}

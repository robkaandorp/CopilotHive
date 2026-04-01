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
    public static void MapComposerEndpoints(this WebApplication routes, Composer? composer)
    {
        if (composer is null) return;

        routes.MapGet("/api/composer/current-model", () =>
            Results.Ok(new { model = composer.GetStats()?.Model ?? composer.AvailableModels.FirstOrDefault() ?? "" }));

        routes.MapGet("/api/composer/models", () =>
            Results.Ok(new { models = composer.AvailableModels }));

        routes.MapPost("/api/composer/models/switch", async (string model) =>
        {
            try
            {
                await composer.SwitchModelAsync(model);
                return Results.Ok(new { model = composer.GetStats()?.Model ?? model });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}

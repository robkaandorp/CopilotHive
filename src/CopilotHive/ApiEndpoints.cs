using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Models;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace CopilotHive;

/// <summary>
/// Extension methods that register the orchestrator's REST API endpoints on a
/// <see cref="WebApplication"/>. Extracted from <c>Program.cs</c> to keep endpoint
/// definitions grouped and discoverable.
/// </summary>
public static class ApiEndpoints
{
    /// <summary>
    /// Registers the <c>/health</c> and <c>/health/utilization</c> endpoints.
    /// </summary>
    /// <param name="app">The web application to register routes on.</param>
    /// <param name="serverStartTime">The UTC time the server started, used to compute uptime.</param>
    /// <param name="version">The informational version string reported by the health endpoint.</param>
    public static void MapHealthEndpoints(this WebApplication app, DateTime serverStartTime, string version)
    {
        var checkCount = 0;

        app.MapGet("/health", async (IGoalStore goalStore, WorkerPool workerPool) =>
        {
            var count = Interlocked.Increment(ref checkCount);
            var uptime = DateTime.UtcNow - serverStartTime;
            var goals = await goalStore.GetAllGoalsAsync();

            // ONE capture for the whole response: the nested worker_pool snapshot is taken once and
            // the top-level worker total is projected from THAT SAME snapshot, so the two totals in
            // one response can never disagree. A separate GetAllWorkers().Count read here would be a
            // second, independent instant.
            var workerPoolStats = workerPool.GetDetailedStats();

            return Results.Ok(new HealthResponse
            {
                Status = "Healthy",
                Uptime = $"{(int)uptime.TotalHours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}",
                UptimeSpan = uptime,
                ActiveGoals = goals.Count(g => g.Status is GoalStatus.Pending or GoalStatus.InProgress),
                CompletedGoals = goals.Count(g => g.Status == GoalStatus.Completed),
                ConnectedWorkers = workerPoolStats.TotalWorkers,
                Version = version,
                SharpCoderVersion = typeof(SharpCoder.CodingAgent).Assembly.GetName().Version?.ToString(),
                ServerTime = DateTime.UtcNow,
                CheckNumber = count,
                WorkerPool = workerPoolStats,
            });
        }).AllowAnonymous();

        app.MapGet("/health/utilization", (WorkerUtilizationService svc) => Results.Ok(svc.GetUtilization())).AllowAnonymous();

        // Logout: clears the auth cookie and returns the user to the login page.
        // Safe to register even when authentication is not configured — when the cookie
        // scheme is absent the sign-out is skipped and the redirect still works.
        app.MapPost("/logout", async (HttpContext ctx) =>
        {
            var schemeProvider = ctx.RequestServices.GetService<IAuthenticationSchemeProvider>();
            if (schemeProvider is not null)
            {
                var cookieScheme = await schemeProvider.GetSchemeAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                if (cookieScheme is not null)
                    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
            return Results.Redirect("/login");
        }).AllowAnonymous().DisableAntiforgery();
    }

    /// <summary>
    /// Endpoint metadata marking a goal/issue MUTATING route that must be authorized BEFORE the
    /// minimal-API pipeline binds or parses its request (route values, query string, typed JSON body).
    /// </summary>
    /// <param name="Capability">The capability the route's operation requires.</param>
    /// <param name="Operation">The operation name (denial log context only).</param>
    internal sealed record PreBindingAuthorization(HiveCapability Capability, string Operation);

    /// <summary>Application-property key that makes the pre-binding middleware registration idempotent.</summary>
    private const string PreBindingMiddlewareKey = "CopilotHive.PreBindingAuthorization";

    /// <summary>
    /// Registers (once per application) the PRE-BINDING AUTHORIZATION middleware for every covered
    /// mutating route (goals, issues, releases, config and clarifications).
    /// <para>
    /// WHY MIDDLEWARE: in minimal APIs, parameter binding — including typed JSON body deserialization
    /// and enum conversion — runs BEFORE any endpoint filter and before the handler, so a facade-level
    /// gate alone would let a malformed body answer 400 for a caller who is not allowed to learn
    /// anything about the schema. Middleware runs after routing has selected the endpoint but before
    /// the endpoint delegate binds anything, so it can refuse first.
    /// </para>
    /// <para>
    /// ORDER: it is registered from the route-mapping methods, which <c>Program.cs</c> calls AFTER
    /// <c>UseAuthentication</c>/<c>UseAuthorization</c> (the config hub's routes are mapped earlier, so
    /// <c>Program.cs</c> calls this explicitly at that same late point), so an anonymous caller is still
    /// challenged with the path-aware 401 (no redirect) by the fallback policy before this runs. For an
    /// authenticated caller it performs ONE FRESH <see cref="HivePrincipalResolver"/> resolution per
    /// request through the single <see cref="FacadeAuthorization"/> seam (nothing is cached across
    /// requests) and answers 403 with the same <c>{ error }</c> body the facade mapping uses. An
    /// allowed caller passes through to the unchanged binding and handler, whose facade gate re-checks
    /// (defence in depth), so authorized-caller behaviour — including 400 for invalid input — is
    /// untouched. Cancellation propagates and is never turned into a denial.
    /// </para>
    /// </summary>
    /// <param name="app">The web application to register the middleware on.</param>
    internal static void EnsurePreBindingAuthorization(WebApplication app)
    {
        var properties = ((IApplicationBuilder)app).Properties;
        if (properties.ContainsKey(PreBindingMiddlewareKey))
            return;
        properties[PreBindingMiddlewareKey] = true;

        app.Use(async (context, next) =>
        {
            var gate = context.GetEndpoint()?.Metadata.GetMetadata<PreBindingAuthorization>();
            if (gate is null)
            {
                await next(context);
                return;
            }

            var services = context.RequestServices;
            var allowed = await FacadeAuthorization.IsAllowedAsync(
                services.GetRequiredService<HivePrincipalResolver>(),
                services.GetRequiredService<HivePolicy>(),
                context.User,
                gate.Capability,
                services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(ApiEndpoints)),
                gate.Operation,
                context.RequestAborted);

            if (!allowed)
            {
                await Results.Json(
                    new { error = FacadeAuthorization.ForbiddenMessage },
                    statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
                return;
            }

            await next(context);
        });
    }

    /// <summary>
    /// Registers the goals REST API endpoints under <c>/api/goals</c>.
    /// </summary>
    /// <remarks>
    /// EVERY covered goal route — the three GET reads, the status/review-status/review/cancel/
    /// extend-iterations/release mutations and the delete — delegates to <see cref="IGoalFacade"/>
    /// with the REQUEST's identity (<see cref="HttpContext.User"/>), so the facade re-resolves the
    /// caller's CURRENT role/enabled state and answers <see cref="FacadeErrorKind.Forbidden"/> (403)
    /// before any store work. Goal CREATION (<c>POST /api/goals</c>) is deliberately unchanged: it is
    /// a separate, dependent goal.
    /// </remarks>
    /// <param name="app">The web application to register routes on.</param>
    public static void MapGoalEndpoints(this WebApplication app)
    {
        // ── Goals REST API ───────────────────────────────────────────────────────
        var goalsApi = app.MapGroup("/api/goals");
        EnsurePreBindingAuthorization(app);

        // IDENTITY IS PER REQUEST: each covered handler forwards the request's authenticated
        // principal (HttpContext.User) to the facade, which re-resolves the caller's CURRENT
        // role/enabled state and requires shared read (GET) or ExecutionOperator (mutations).
        goalsApi.MapGet("/", async (HttpContext context, [FromServices] IGoalFacade facade, CancellationToken ct) =>
        {
            var result = await facade.GetGoalsAsync(context.User, ct);
            return result.Success
                ? Results.Ok(result.Value)
                : MapGoalFacadeError(result.Kind, result.Error);
        });

        goalsApi.MapGet("/{id}", async (string id, HttpContext context,
            [FromServices] IGoalFacade facade, CancellationToken ct) =>
        {
            var result = await facade.GetGoalAsync(id, context.User, ct);
            return result.Success
                ? Results.Ok(result.Value)
                : MapGoalFacadeError(result.Kind, result.Error);
        });

        goalsApi.MapPost("/", async (Goal goal, IGoalStore store, [FromServices] DashboardNotifier dashboardNotifier,
            [FromServices] GoalReadyNotifier? goalReadyNotifier) =>
        {
            try
            {
                var created = await store.CreateGoalAsync(goal);
                var result = Results.Created($"/api/goals/{created.Id}", created);
                dashboardNotifier.NotifyStateChanged();
                if (created.Status == GoalStatus.Pending) goalReadyNotifier?.NotifyGoalReady();
                return result;
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT (duplicate primary key)
            {
                return Results.Conflict(new { error = $"Goal '{goal.Id}' already exists." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        goalsApi.MapPatch("/{id}/status", async (string id, GoalStatusUpdate update, HttpContext context,
            [FromServices] IGoalFacade facade) =>
        {
            var result = await facade.UpdateGoalStatusAsync(id, update.Status, context.User);
            return result.Success
                ? Results.Ok(result.Value)
                : MapGoalFacadeError(result.Kind, result.Error);
        })
            .WithMetadata(new PreBindingAuthorization(HiveCapability.ExecutionOperator, "goals.status"));

        goalsApi.MapDelete("/{id}", async (string id, HttpContext context, [FromServices] IGoalFacade facade) =>
        {
            var result = await facade.DeleteGoalAsync(id, context.User);
            return result.Success
                ? Results.NoContent()
                : MapGoalFacadeError(result.Kind, result.Error);
        })
            .WithMetadata(new PreBindingAuthorization(HiveCapability.ExecutionOperator, "goals.delete"));

        goalsApi.MapPost("/{id}/cancel", async (string id, HttpContext context, [FromServices] IGoalFacade facade) =>
        {
            var result = await facade.CancelGoalAsync(id, context.User);
            return result.Success
                ? Results.Ok(result.Value)
                : MapGoalFacadeError(result.Kind, result.Error);
        })
            .WithMetadata(new PreBindingAuthorization(HiveCapability.ExecutionOperator, "goals.cancel"));

        goalsApi.MapPost("/{id}/extend-iterations", async (string id, ExtendIterationsRequest request,
            HttpContext context, [FromServices] IGoalFacade facade, CancellationToken ct) =>
        {
            var result = await facade.ExtendIterationsAsync(id, request.AdditionalIterations, context.User, ct);
            if (result.Success)
                return Results.Ok(result.Value);

            // This route answers an unavailable dispatcher with a BARE 503 (no body) — not the
            // problem-details form the other facade routes use — so the contract is unchanged.
            return result.Kind == FacadeErrorKind.ServiceUnavailable
                ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                : MapGoalFacadeError(result.Kind, result.Error);
        })
            .WithMetadata(new PreBindingAuthorization(HiveCapability.ExecutionOperator, "goals.extend-iterations"));

        goalsApi.MapGet("/search", async (string q, string? status, HttpContext context,
            [FromServices] IGoalFacade facade, CancellationToken ct) =>
        {
            var result = await facade.SearchGoalsAsync(q, status, context.User, ct);
            return result.Success
                ? Results.Ok(result.Value)
                : MapGoalFacadeError(result.Kind, result.Error);
        });

        goalsApi.MapPatch("/{id}/release", async (string id, AssignGoalReleaseRequest request,
            HttpContext context, [FromServices] IGoalFacade facade) =>
        {
            var result = await facade.AttachReleaseAsync(id, request.ReleaseId, context.User);
            return result.Success
                ? Results.Ok(result.Value)
                : MapGoalFacadeError(result.Kind, result.Error);
        })
            .WithMetadata(new PreBindingAuthorization(HiveCapability.ExecutionOperator, "goals.release"));

        goalsApi.MapPatch("/{id}/review-status", async (string id, GoalReviewStatusUpdate update,
            HttpContext context, [FromServices] IGoalFacade facade) =>
        {
            var result = await facade.UpdateReviewStatusAsync(id, update.ReviewStatus, context.User);
            return result.Success
                ? Results.Ok(result.Value)
                : MapGoalFacadeError(result.Kind, result.Error);
        })
            .WithMetadata(new PreBindingAuthorization(HiveCapability.ExecutionOperator, "goals.review-status"));

        goalsApi.MapPost("/{goalId}/review", async (string goalId, HttpContext context,
            [FromServices] IGoalFacade facade, CancellationToken ct) =>
        {
            var result = await facade.RequestReviewAsync(goalId, context.User, ct);
            return result.Success
                ? Results.Ok(result.Value)
                : MapGoalFacadeError(result.Kind, result.Error);
        })
            .WithMetadata(new PreBindingAuthorization(HiveCapability.ExecutionOperator, "goals.review"));
    }

    /// <summary>
    /// Maps a <see cref="FacadeErrorKind"/> from <see cref="IGoalFacade"/> to the exact HTTP
    /// response the pre-facade goal handlers produced: <c>NotFound</c>, <c>BadRequest</c> and
    /// <c>Conflict</c> all return a JSON <c>{error}</c> body, and the AUTHORIZATION answer
    /// <c>Forbidden</c> returns HTTP 403 with the same body shape.
    /// <see cref="FacadeErrorKind.None"/> is a programming error (a success result must not be mapped
    /// here) and throws, as does any kind these routes never produce — including
    /// <see cref="FacadeErrorKind.ServiceUnavailable"/>, which only the extend-iterations route
    /// produces and handles itself as a bare bodyless 503.
    /// </summary>
    /// <param name="kind">The failure category reported by the facade.</param>
    /// <param name="error">The human-readable error message.</param>
    /// <returns>The HTTP result matching the pre-facade handler behaviour.</returns>
    private static IResult MapGoalFacadeError(FacadeErrorKind kind, string? error)
    {
        return kind switch
        {
            FacadeErrorKind.NotFound => Results.NotFound(new { error }),
            FacadeErrorKind.BadRequest => Results.BadRequest(new { error }),
            FacadeErrorKind.Conflict => Results.Conflict(new { error }),
            FacadeErrorKind.Forbidden => Results.Json(new { error }, statusCode: StatusCodes.Status403Forbidden),
            _ => throw new InvalidOperationException($"Unexpected goal facade error kind: {kind}."),
        };
    }

    /// <summary>
    /// Registers the releases REST API endpoints under <c>/api/releases</c>. All nine routes
    /// (the two reads, create, status, notes, tag, repositories, delete, validate) delegate to
    /// <see cref="IReleaseFacade"/> so the validation order, the status orchestration side
    /// effects and the failure mapping live in exactly one place and run exactly once.
    /// </summary>
    /// <remarks>
    /// EVERY covered release route delegates to <see cref="IReleaseFacade"/> with the REQUEST's
    /// identity (<see cref="HttpContext.User"/>), so the facade re-resolves the caller's CURRENT
    /// role/enabled state before any store work: the two reads need
    /// <see cref="HiveCapability.SharedRead"/> and every mutation/validation needs
    /// <see cref="HiveCapability.ExecutionOperator"/>. The mutating routes additionally carry
    /// <see cref="PreBindingAuthorization"/> metadata so an authenticated but insufficient/demoted/
    /// disabled caller is refused with 403 BEFORE a typed body is bound (a malformed body therefore
    /// never answers 400 to a caller who may not perform the operation), while an authorized caller
    /// keeps the unchanged binding behaviour.
    /// </remarks>
    /// <param name="app">The web application to register routes on.</param>
    public static void MapReleaseEndpoints(this WebApplication app)
    {
        // ── Releases REST API ────────────────────────────────────────────────────
        var releasesApi = app.MapGroup("/api/releases");
        EnsurePreBindingAuthorization(app);

        releasesApi.MapPost("/", async (CreateReleaseRequest request, HttpContext context,
            [FromServices] IReleaseFacade facade) =>
        {
            var result = await facade.CreateReleaseAsync(request, context.User);
            return result.Success
                ? Results.Created($"/api/releases/{result.Value!.Id}", result.Value)
                : MapReleaseFacadeError(result.Kind, result.Error);
        })
            .WithMetadata(new PreBindingAuthorization(HiveCapability.ExecutionOperator, "releases.create"));

        releasesApi.MapPatch("/{id}/status", async (string id, UpdateReleaseStatusRequest request,
            HttpContext context, [FromServices] IReleaseFacade facade, CancellationToken ct) =>
        {
            var outcome = await facade.UpdateReleaseStatusAsync(id, request, context.User, ct);
            return MapReleaseStatusOutcome(outcome);
        })
            .WithMetadata(new PreBindingAuthorization(HiveCapability.ExecutionOperator, "releases.status"));

        releasesApi.MapPatch("/{id}/notes", async (string id, UpdateReleaseNotesRequest request,
            HttpContext context, [FromServices] IReleaseFacade facade) =>
        {
            var result = await facade.UpdateReleaseNotesAsync(id, request, context.User);
            return result.Success
                ? Results.Ok(result.Value)
                : MapReleaseFacadeError(result.Kind, result.Error);
        })
            .WithMetadata(new PreBindingAuthorization(HiveCapability.ExecutionOperator, "releases.notes"));

        releasesApi.MapPatch("/{id}/tag", async (string id, UpdateReleaseTagRequest request,
            HttpContext context, [FromServices] IReleaseFacade facade) =>
        {
            var result = await facade.UpdateReleaseTagAsync(id, request, context.User);
            return result.Success
                ? Results.Ok(result.Value)
                : MapReleaseFacadeError(result.Kind, result.Error);
        })
            .WithMetadata(new PreBindingAuthorization(HiveCapability.ExecutionOperator, "releases.tag"));

        releasesApi.MapPatch("/{id}/repositories", async (string id, UpdateReleaseRepositoriesRequest request,
            HttpContext context, [FromServices] IReleaseFacade facade) =>
        {
            var result = await facade.UpdateReleaseRepositoriesAsync(id, request, context.User);
            return result.Success
                ? Results.Ok(result.Value)
                : MapReleaseFacadeError(result.Kind, result.Error);
        })
            .WithMetadata(new PreBindingAuthorization(HiveCapability.ExecutionOperator, "releases.repositories"));

        releasesApi.MapDelete("/{id}", async (string id, HttpContext context,
            [FromServices] IReleaseFacade facade, CancellationToken ct) =>
        {
            var result = await facade.DeleteReleaseAsync(id, context.User, ct);
            return result.Success
                ? Results.NoContent()
                : MapReleaseFacadeError(result.Kind, result.Error);
        })
            .WithMetadata(new PreBindingAuthorization(HiveCapability.ExecutionOperator, "releases.delete"));

        releasesApi.MapGet("/{id}/validate", async (string id, HttpContext context,
            [FromServices] IReleaseFacade facade, CancellationToken ct) =>
        {
            var result = await facade.ValidateReleaseAsync(id, context.User, ct);
            return result.Success
                ? Results.Ok(result.Value)
                : MapReleaseFacadeError(result.Kind, result.Error);
        })
            .WithMetadata(new PreBindingAuthorization(HiveCapability.ExecutionOperator, "releases.validate"));

        // ── Authorized reads (SharedRead) ─────────────────────────────────────────
        // No pre-binding gate: these routes take no body, and the fallback policy already challenges
        // an anonymous caller with the path-aware 401 before the facade re-resolves the caller.

        releasesApi.MapGet("/{id}", async (string id, HttpContext context,
            [FromServices] IReleaseFacade facade, CancellationToken ct) =>
        {
            var result = await facade.GetReleaseAsync(id, context.User, ct);
            return result.Success
                ? Results.Ok(result.Value)
                : MapReleaseFacadeError(result.Kind, result.Error);
        });

        releasesApi.MapGet("/{id}/goals", async (string id, HttpContext context,
            [FromServices] IReleaseFacade facade, CancellationToken ct) =>
        {
            var result = await facade.GetGoalsByReleaseAsync(id, context.User, ct);
            return result.Success
                ? Results.Ok(result.Value)
                : MapReleaseFacadeError(result.Kind, result.Error);
        });
    }

    /// <summary>
    /// Maps a <see cref="FacadeErrorKind"/> from <see cref="IReleaseFacade"/>'s simple operations
    /// (the two reads, create, notes, tag, repositories, delete, validate) to the exact HTTP response
    /// the pre-facade release handlers produced: <c>NotFound</c>, <c>BadRequest</c> and
    /// <c>Conflict</c> all return a JSON <c>{error}</c> body, and the AUTHORIZATION answer
    /// <c>Forbidden</c> returns HTTP 403 with the same body shape.
    /// <see cref="FacadeErrorKind.None"/> is a programming error (a success result must not be mapped
    /// here) and throws, as does any kind these eight routes never produce.
    /// </summary>
    /// <param name="kind">The failure category reported by the facade.</param>
    /// <param name="error">The human-readable error message.</param>
    /// <returns>The HTTP result matching the pre-facade handler behaviour.</returns>
    private static IResult MapReleaseFacadeError(FacadeErrorKind kind, string? error)
    {
        return kind switch
        {
            FacadeErrorKind.NotFound => Results.NotFound(new { error }),
            FacadeErrorKind.BadRequest => Results.BadRequest(new { error }),
            FacadeErrorKind.Conflict => Results.Conflict(new { error }),
            FacadeErrorKind.Forbidden => Results.Json(new { error }, statusCode: StatusCodes.Status403Forbidden),
            _ => throw new InvalidOperationException($"Unexpected release facade error kind: {kind}."),
        };
    }

    /// <summary>
    /// Maps a <see cref="ReleaseStatusOutcome"/> to the exact HTTP response the pre-facade status
    /// handler produced: <see cref="PlanningNoOpOutcome"/> → 200 with the bare Release JSON;
    /// <see cref="ExecutionSuccessOutcome"/> → 200 with the <c>{release, result}</c> envelope; a
    /// <see cref="StatusFailureOutcome"/> → the per-variant status and body from the frozen
    /// contract (403/404/400/409 <c>{error}</c>, 400 <c>{errors:[...]}</c>, 500
    /// <c>{detail, results}</c>, 503 <c>{detail}</c>).
    /// </summary>
    /// <param name="outcome">The outcome reported by the facade.</param>
    /// <returns>The HTTP result matching the pre-facade handler behaviour.</returns>
    private static IResult MapReleaseStatusOutcome(ReleaseStatusOutcome outcome)
    {
        return outcome switch
        {
            PlanningNoOpOutcome noOp => Results.Ok(noOp.Release),
            ExecutionSuccessOutcome success => Results.Ok(new { release = success.Release, result = success.Result }),
            StatusFailureOutcome failure => MapReleaseStatusFailure(failure),
            _ => throw new InvalidOperationException($"Unexpected release status outcome: {outcome.GetType().Name}."),
        };
    }

    /// <summary>
    /// Maps a <see cref="StatusFailureOutcome"/> to its exact HTTP response. Only the fields the
    /// variant's body carries are serialized: <c>{error}</c> bodies use <see cref="StatusFailureOutcome.Error"/>,
    /// the <c>{errors:[...]}</c> body uses <see cref="StatusFailureOutcome.Errors"/>, the 500 body
    /// uses <see cref="StatusFailureOutcome.Detail"/> + <see cref="StatusFailureOutcome.Results"/>,
    /// and the 503 body uses <see cref="StatusFailureOutcome.Detail"/> alone.
    /// <para>
    /// <see cref="FacadeErrorKind.Forbidden"/> → 403 with the sanitized <c>{error}</c> body, matching
    /// <see cref="MapReleaseFacadeError"/> and <see cref="MapBackupFacadeResult"/>. This arm is
    /// REACHABLE even though this route also carries <see cref="PreBindingAuthorization"/>: the
    /// pre-binding gate and the facade's own fresh resolution are two independent evaluations, so an
    /// Operator who passes the gate can be demoted or disabled WHILE the request's body is being
    /// bound, and the facade's per-call resolution then correctly refuses. Without this arm that
    /// refusal would surface as an unexpected-kind <see cref="InvalidOperationException"/> (HTTP 500)
    /// instead of the authorization answer.
    /// </para>
    /// </summary>
    /// <param name="failure">The failure outcome to map.</param>
    /// <returns>The HTTP result matching the pre-facade handler behaviour.</returns>
    private static IResult MapReleaseStatusFailure(StatusFailureOutcome failure)
    {
        return failure.Kind switch
        {
            FacadeErrorKind.NotFound => Results.NotFound(new { error = failure.Error }),
            FacadeErrorKind.Forbidden => Results.Json(
                new { error = failure.Error }, statusCode: StatusCodes.Status403Forbidden),
            FacadeErrorKind.BadRequest when failure.Errors.Length > 0
                => Results.BadRequest(new { errors = failure.Errors }),
            FacadeErrorKind.BadRequest => Results.BadRequest(new { error = failure.Error }),
            FacadeErrorKind.Conflict => Results.Json(new { error = failure.Error }, statusCode: StatusCodes.Status409Conflict),
            FacadeErrorKind.Internal => Results.Json(
                new { detail = failure.Detail, results = failure.Results },
                statusCode: StatusCodes.Status500InternalServerError),
            FacadeErrorKind.ServiceUnavailable => Results.Json(
                new { detail = failure.Detail },
                statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => throw new InvalidOperationException($"Unexpected release status failure kind: {failure.Kind}."),
        };
    }

    /// <summary>
    /// Registers the issues REST API endpoints under <c>/api/issues</c>.
    /// </summary>
    /// <remarks>
    /// EVERY issue route delegates to <see cref="IIssueFacade"/> with the REQUEST's identity
    /// (<see cref="HttpContext.User"/>), so the facade re-resolves the caller's CURRENT
    /// role/enabled state before any store work: reads need shared read, create needs
    /// <see cref="Security.HiveCapability.InteractiveMember"/>, and the whole update/delete surface
    /// needs <see cref="Security.HiveCapability.ExecutionOperator"/>.
    /// </remarks>
    /// <param name="app">The web application to register routes on.</param>
    public static void MapIssueEndpoints(this WebApplication app)
    {
        // ── Issues REST API ─────────────────────────────────────────────────────
        var issuesApi = app.MapGroup("/api/issues");
        EnsurePreBindingAuthorization(app);

        // IDENTITY IS PER REQUEST: each handler forwards the request's authenticated principal
        // (HttpContext.User) to the facade, which re-resolves the caller's CURRENT role/enabled state.
        issuesApi.MapGet("/", async (string? status, string? type, string? severity, string? repository,
            string? source_goal_id, string? linked_goal_id, HttpContext context,
            [FromServices] IIssueFacade facade, CancellationToken ct) =>
        {
            var result = await facade.GetIssuesAsync(new IssueFilter(
                Status: status,
                Type: type,
                Severity: severity,
                Repository: repository,
                SourceGoalId: source_goal_id,
                LinkedGoalId: linked_goal_id), context.User, ct);
            return result.Success
                ? Results.Ok(result.Value)
                : MapIssueFacadeError(result.Kind, result.Error);
        });

        issuesApi.MapGet("/{id}", async (string id, HttpContext context,
            [FromServices] IIssueFacade facade, CancellationToken ct) =>
        {
            var result = await facade.GetIssueAsync(id, context.User, ct);
            return result.Success
                ? Results.Ok(result.Value)
                : MapIssueFacadeError(result.Kind, result.Error);
        });

        issuesApi.MapPost("/", async (CreateIssueRequest request, HttpContext context,
            [FromServices] IIssueFacade facade, CancellationToken ct) =>
        {
            var result = await facade.CreateIssueAsync(request, context.User, ct);
            if (result.Success)
                return Results.Created($"/api/issues/{Uri.EscapeDataString(result.Value!.Id)}", result.Value);
            return MapIssueFacadeError(result.Kind, result.Error);
        })
            .WithMetadata(new PreBindingAuthorization(HiveCapability.InteractiveMember, "issues.create"));

        issuesApi.MapPatch("/{id}", async (string id, UpdateIssueRequest request, HttpContext context,
            [FromServices] IIssueFacade facade, CancellationToken ct) =>
        {
            var result = await facade.UpdateIssueAsync(id, request, context.User, ct);
            return result.Success
                ? Results.Ok(result.Value)
                : MapIssueFacadeError(result.Kind, result.Error);
        })
            .WithMetadata(new PreBindingAuthorization(HiveCapability.ExecutionOperator, "issues.update"));

        issuesApi.MapDelete("/{id}", async (string id, HttpContext context,
            [FromServices] IIssueFacade facade, CancellationToken ct) =>
        {
            var result = await facade.DeleteIssueAsync(id, context.User, ct);
            return result.Success
                ? Results.NoContent()
                : MapIssueFacadeError(result.Kind, result.Error);
        })
            .WithMetadata(new PreBindingAuthorization(HiveCapability.ExecutionOperator, "issues.delete"));
    }

    /// <summary>
    /// Maps a <see cref="FacadeErrorKind"/> from <see cref="IIssueFacade"/> to the exact HTTP
    /// response the pre-facade issue handlers produced: <c>NotFound</c>, <c>BadRequest</c> and
    /// <c>Conflict</c> all return a JSON <c>{error}</c> body, and the AUTHORIZATION answer
    /// <c>Forbidden</c> returns HTTP 403 with the same body shape.
    /// <see cref="FacadeErrorKind.None"/> is a programming error (a success result must not be mapped
    /// here) and throws, as does any kind these five routes never produce.
    /// </summary>
    /// <param name="kind">The failure category reported by the facade.</param>
    /// <param name="error">The human-readable error message.</param>
    /// <returns>The HTTP result matching the pre-facade handler behaviour.</returns>
    private static IResult MapIssueFacadeError(FacadeErrorKind kind, string? error)
    {
        return kind switch
        {
            FacadeErrorKind.NotFound => Results.NotFound(new { error }),
            FacadeErrorKind.BadRequest => Results.BadRequest(new { error }),
            FacadeErrorKind.Conflict => Results.Conflict(new { error }),
            FacadeErrorKind.Forbidden => Results.Json(new { error }, statusCode: StatusCodes.Status403Forbidden),
            _ => throw new InvalidOperationException($"Unexpected issue facade error kind: {kind}."),
        };
    }

    /// <summary>
    /// Registers the clarifications REST API endpoints under <c>/api/clarifications</c>.
    /// </summary>
    /// <remarks>
    /// EVERY route delegates to <see cref="IClarificationFacade"/> with the REQUEST's identity
    /// (<see cref="HttpContext.User"/>), so the caller's CURRENT role/enabled state is re-resolved
    /// before the queue is read or mutated: the three reads need
    /// <see cref="HiveCapability.SharedRead"/> and answering needs
    /// <see cref="HiveCapability.ExecutionOperator"/>. The answer route additionally carries
    /// <see cref="PreBindingAuthorization"/> metadata so a malformed answer body from an unauthorized
    /// caller is refused with 403 before binding, while an authorized caller keeps the unchanged
    /// 400/404 bodies. <see cref="ClarificationQueueService"/> itself is untouched, so the internal
    /// Composer and <c>ClarificationHandler</c> callers keep working; the MainLayout caller is
    /// deferred to a later phase.
    /// </remarks>
    /// <param name="app">The web application to register routes on.</param>
    public static void MapClarificationEndpoints(this WebApplication app)
    {
        // ── Clarifications REST API ──────────────────────────────────────────────
        var clarificationsApi = app.MapGroup("/api/clarifications");
        EnsurePreBindingAuthorization(app);

        clarificationsApi.MapGet("/", async (HttpContext context,
            [FromServices] IClarificationFacade facade, CancellationToken ct) =>
        {
            var result = await facade.GetAllRequestsAsync(context.User, ct);
            return result.Success
                ? Results.Ok(result.Value)
                : MapClarificationFacadeError(result.Kind, result.Error);
        });

        clarificationsApi.MapGet("/pending", async (HttpContext context,
            [FromServices] IClarificationFacade facade, CancellationToken ct) =>
        {
            var result = await facade.GetPendingHumanRequestsAsync(context.User, ct);
            return result.Success
                ? Results.Ok(result.Value)
                : MapClarificationFacadeError(result.Kind, result.Error);
        });

        clarificationsApi.MapGet("/count", async (HttpContext context,
            [FromServices] IClarificationFacade facade, CancellationToken ct) =>
        {
            var result = await facade.GetPendingHumanCountAsync(context.User, ct);
            return result.Success
                ? Results.Ok(new { count = result.Value })
                : MapClarificationFacadeError(result.Kind, result.Error);
        });

        clarificationsApi.MapPost("/{id}/answer", async (string id, SubmitClarificationRequest body,
            HttpContext context, [FromServices] IClarificationFacade facade, CancellationToken ct) =>
        {
            var result = await facade.SubmitAnswerAsync(id, body.Answer, context.User, ct);
            return result.Success
                ? Results.Ok(new { message = result.Value!.Message })
                : MapClarificationFacadeError(result.Kind, result.Error);
        })
            .WithMetadata(new PreBindingAuthorization(
                HiveCapability.ExecutionOperator, "clarifications.answer"));
    }

    /// <summary>
    /// Maps a <see cref="FacadeErrorKind"/> from <see cref="IClarificationFacade"/> to the exact HTTP
    /// response the pre-facade clarification handlers produced: <c>BadRequest</c> and <c>NotFound</c>
    /// return their JSON <c>{error}</c> bodies, and the AUTHORIZATION answer <c>Forbidden</c> returns
    /// HTTP 403 with the same body shape. <see cref="FacadeErrorKind.None"/> is a programming error (a
    /// success result must not be mapped here) and throws, as does any kind these four routes never
    /// produce.
    /// </summary>
    /// <param name="kind">The failure category reported by the facade.</param>
    /// <param name="error">The human-readable error message.</param>
    /// <returns>The HTTP result matching the pre-facade handler behaviour.</returns>
    private static IResult MapClarificationFacadeError(FacadeErrorKind kind, string? error)
    {
        return kind switch
        {
            FacadeErrorKind.NotFound => Results.NotFound(new { error }),
            FacadeErrorKind.BadRequest => Results.BadRequest(new { error }),
            FacadeErrorKind.Forbidden => Results.Json(new { error }, statusCode: StatusCodes.Status403Forbidden),
            _ => throw new InvalidOperationException($"Unexpected clarification facade error kind: {kind}."),
        };
    }

    /// <summary>
    /// Registers the backup REST API endpoints under <c>/api/backup</c>.
    /// </summary>
    /// <remarks>
    /// EVERY backup route — list, create, download and restore — delegates to
    /// <see cref="IBackupFacade"/> with the REQUEST's identity (<see cref="HttpContext.User"/>) and
    /// requires <see cref="HiveCapability.GlobalAdmin"/>, re-resolved FRESH on every call. The
    /// archive-name validation and the two validation messages live in the facade (the download and
    /// restore paths), so a handler can never serve or restore a file without it.
    /// <para>
    /// RESTORE binds a TYPED BODY, so it additionally carries
    /// <see cref="PreBindingAuthorization"/> metadata: an authenticated but insufficient caller is
    /// refused with 403 BEFORE the body is deserialized (a malformed body therefore never answers 400
    /// to a caller who may not restore), while an authorized caller keeps the unchanged binding
    /// behaviour. The facade gate still re-checks in the handler (defence in depth).
    /// </para>
    /// <see cref="BackupService"/> stays a trusted internal service with its public API unchanged.
    /// </remarks>
    /// <param name="app">The web application to register routes on.</param>
    public static void MapBackupEndpoints(this WebApplication app)
    {
        // ── Backup REST API ──────────────────────────────────────────────────────
        var backupApi = app.MapGroup("/api/backup");

        backupApi.MapPost("/", async (HttpContext context,
            [FromServices] IBackupFacade facade, CancellationToken ct) =>
        {
            // Failures (including cancellation) propagate out of the facade and become a 500,
            // exactly as before the facade existed.
            var result = await facade.CreateBackupAsync(context.User, ct);
            return MapBackupFacadeResult(result);
        });

        backupApi.MapGet("/", async (HttpContext context,
            [FromServices] IBackupFacade facade, CancellationToken ct) =>
            MapBackupFacadeResult(await facade.GetBackupsAsync(context.User, ct)));

        backupApi.MapGet("/{fileName}", async (string fileName, HttpContext context,
            [FromServices] IBackupFacade facade, CancellationToken ct) =>
        {
            var result = await facade.OpenBackupForDownloadAsync(fileName, context.User, ct);
            if (!result.Success)
                return MapBackupFacadeResult(result);

            var download = result.Value!;
            return Results.File(download.FullPath, "application/gzip", download.FileName);
        });

        backupApi.MapPost("/restore", async (RestoreRequest request, HttpContext context,
            [FromServices] IBackupFacade facade, CancellationToken ct) =>
        {
            var result = await facade.RestoreBackupAsync(request.FileName, context.User, ct);
            if (!result.Success)
                return MapBackupFacadeResult(result);

            var restored = result.Value!;
            return Results.Ok(new
            {
                message = "Restore complete. Restart the orchestrator for changes to take effect.",
                restored.DatabaseRestored,
                restored.BrainMasterSession,
                restored.BrainGoalSessionCount,
                restored.ComposerSession,
                restored.MetricsCount,
                restored.KeysCount,
                restored.SafetyBackupPath,
            });
        })
            .WithMetadata(new PreBindingAuthorization(HiveCapability.GlobalAdmin, "backup.restore"));
    }

    /// <summary>
    /// Maps a backup facade result to the HTTP response the pre-facade handlers produced.
    /// <see cref="FacadeErrorKind.None"/> → 200 with the value; the AUTHORIZATION answer
    /// <see cref="FacadeErrorKind.Forbidden"/> → 403 with the sanitized <c>{error}</c> body; the
    /// download/restore validation outcomes keep their frozen bodies —
    /// <see cref="FacadeErrorKind.BadRequest"/> → 400, <see cref="FacadeErrorKind.NotFound"/> → 404.
    /// Any other kind is a programming error and throws rather than silently falling back.
    /// </summary>
    /// <typeparam name="T">The facade value type.</typeparam>
    /// <param name="result">The facade result to map.</param>
    /// <returns>A 200 result carrying the facade value, or the matching failure response.</returns>
    private static IResult MapBackupFacadeResult<T>(FacadeResult<T> result)
    {
        return result.Kind switch
        {
            FacadeErrorKind.None => Results.Ok(result.Value!),
            FacadeErrorKind.Forbidden => Results.Json(
                new { error = result.Error }, statusCode: StatusCodes.Status403Forbidden),
            FacadeErrorKind.BadRequest => Results.BadRequest(new { error = result.Error }),
            FacadeErrorKind.NotFound => Results.NotFound(new { error = result.Error }),
            _ => throw new InvalidOperationException($"Unexpected backup facade error kind: {result.Kind}."),
        };
    }

    /// <summary>
    /// Registers the LLM sessions REST API endpoint under <c>/api/sessions</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE ROUTE IS A SHARED-READ SURFACE: it resolves the caller FRESH through the SAME
    /// <see cref="FacadeAuthorization"/> seam every facade uses (one
    /// <see cref="HivePrincipalResolver"/> resolution + one <see cref="HivePolicy"/> evaluation per
    /// request, nothing cached between requests) and requires
    /// <see cref="HiveCapability.SharedRead"/>. A refusal — signed out while authentication is
    /// enabled, unknown account, disabled account, unreadable directory — answers 403 with the single
    /// sanitized <c>{ error }</c> body, so a disabled or unknown identity can never read the shared
    /// execution-session list. Cancellation propagates and is never turned into a denial. This is the
    /// same seam, the same resolver/policy singletons and the same denial text as the facades and the
    /// pre-binding middleware — deliberately no parallel authorization code.
    /// </para>
    /// <para>
    /// The route reports SHARED EXECUTION sessions only: <see cref="Dashboard.LlmSessionType.Composer"/>
    /// entries are excluded, because a Composer session is per-user private metadata. The filter lives
    /// HERE, at the endpoint, and <see cref="Dashboard.LlmSessionRegistry.GetAll"/> keeps its
    /// unchanged semantics (the dashboard renders Composer entries).
    /// </para>
    /// </remarks>
    /// <param name="app">The web application to register routes on.</param>
    public static void MapSessionEndpoints(this WebApplication app)
    {
        // ── LLM Sessions REST API ────────────────────────────────────────────────
        // IDENTITY IS PER REQUEST: the handler forwards the request's authenticated principal
        // (HttpContext.User) to the shared authorization seam, which re-resolves the caller's CURRENT
        // role/enabled state and requires shared read.
        app.MapGet("/api/sessions", async (
            HttpContext context,
            LlmSessionRegistry registry,
            HivePrincipalResolver resolver,
            HivePolicy policy,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var allowed = await FacadeAuthorization.IsAllowedAsync(
                resolver,
                policy,
                context.User,
                HiveCapability.SharedRead,
                loggerFactory.CreateLogger(nameof(ApiEndpoints)),
                "sessions.list",
                ct);

            if (!allowed)
            {
                return Results.Json(
                    new { error = FacadeAuthorization.ForbiddenMessage },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            return Results.Ok(registry.GetAll()
                .Where(session => session.SessionType != LlmSessionType.Composer)
                .ToList());
        });
    }

    /// <summary>
    /// Launches fire-and-forget NuGet publish monitors for every configured package of every
    /// PublishNuGet repository named in the release. Both the monitor and the hive config are
    /// required (either missing → no-op). The application lifetime is optional; when absent
    /// the monitor is bound to <see cref="CancellationToken.None"/>. Failures are logged and
    /// never propagate. Exposed as <c>internal</c> for unit testing via
    /// <c>InternalsVisibleTo</c>.
    /// </summary>
    /// <param name="monitor">The NuGet publish monitor, or <c>null</c> to skip.</param>
    /// <param name="config">The hive configuration, or <c>null</c> to skip.</param>
    /// <param name="appLifetime">Optional application lifetime providing the shutdown token.</param>
    /// <param name="logger">Optional logger for monitor failures.</param>
    /// <param name="release">The freshly-released release to monitor.</param>
    internal static void LaunchNuGetMonitors(
        NuGetPublishMonitorService? monitor,
        HiveConfigFile? config,
        IHostApplicationLifetime? appLifetime,
        ILogger? logger,
        Release release)
    {
        if (monitor is null || config is null)
            return;

        var ct = appLifetime?.ApplicationStopping ?? CancellationToken.None;

        foreach (var repo in release.RepositoryNames)
        {
            var repoConfig = config.Repositories.FirstOrDefault(
                r => string.Equals(r.Name, repo, StringComparison.OrdinalIgnoreCase));
            if (repoConfig?.PublishNuGet?.Packages is not { Count: > 0 })
                continue;

            _ = Task.Run(async () =>
            {
                try
                {
                    await monitor.MonitorReleaseAsync(repo, release.Tag, ct);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "NuGet publish monitor failed for release {Tag} repo {Repo}", release.Tag, repo);
                }
            }, ct);
        }
    }

    }

/// <summary>Request body for updating the status of a goal via the HTTP API.</summary>
/// <param name="Status">New status string (e.g. "completed", "failed").</param>
public record GoalStatusUpdate(string Status);

/// <summary>Request body for updating the pre-execution review status of a goal via the HTTP API.</summary>
/// <param name="ReviewStatus">New review status string: none, pending, approved, or needschanges.</param>
public record GoalReviewStatusUpdate(string ReviewStatus);

/// <summary>Request body for creating a new release via the HTTP API.</summary>
/// <param name="Version">Version tag for the release (e.g. "v1.2.0").</param>
/// <param name="Repository">Optional repository name this release belongs to.</param>
public record CreateReleaseRequest(string Version, string? Repository = null);

/// <summary>Request body for updating the status of a release via the HTTP API.</summary>
/// <param name="Status">New status string (e.g. "Planning" or "Released").</param>
public record UpdateReleaseStatusRequest(string Status);

/// <summary>Request body for updating the notes of a release via the HTTP API.</summary>
/// <param name="Notes">Updated release notes.</param>
public record UpdateReleaseNotesRequest(string? Notes);

/// <summary>Request body for updating the tag of a Planning release via the HTTP API.</summary>
/// <param name="Tag">New version tag (e.g. "v1.2.1").</param>
public record UpdateReleaseTagRequest(string Tag);

/// <summary>Request body for updating the repository list of a Planning release via the HTTP API.</summary>
/// <param name="Repositories">New list of repository names. An empty list clears all repositories.</param>
public record UpdateReleaseRepositoriesRequest(List<string>? Repositories);

/// <summary>Request body for assigning a goal to a release via the HTTP API.</summary>
/// <param name="ReleaseId">The release ID to assign this goal to.</param>
public record AssignGoalReleaseRequest(string ReleaseId);

/// <summary>Request body for extending the iteration budget of a goal via the HTTP API.</summary>
/// <param name="AdditionalIterations">Number of additional iterations to grant (1-100).</param>
public record ExtendIterationsRequest(int AdditionalIterations);

/// <summary>Request body for submitting an answer to a clarification request via the HTTP API.</summary>
/// <param name="Answer">The answer text to submit.</param>
public record SubmitClarificationRequest(string Answer);

/// <summary>Request body for restoring a backup archive via the HTTP API.</summary>
/// <param name="FileName">The backup archive file name to restore.</param>
public record RestoreRequest(string FileName);

/// <summary>Request body for creating a new issue via the HTTP API.</summary>
/// <param name="Id">Optional caller-supplied kebab-case ID. When null/empty/whitespace, an ID is generated from the title.</param>
/// <param name="Type">Category of the issue.</param>
/// <param name="Title">Short summary of the issue.</param>
/// <param name="Description">Detailed markdown description of the issue.</param>
/// <param name="Severity">Severity of the issue; defaults to <see cref="IssueSeverity.Low"/> when omitted.</param>
/// <param name="RepositoryNames">Names of repositories this issue applies to.</param>
/// <param name="SourceGoalId">ID of the goal that produced this issue, or <c>null</c> if user-reported.</param>
/// <param name="SourceRole">Role that produced this issue, or <c>null</c> if user-reported.</param>
/// <param name="SourceIteration">Iteration number in which the issue was produced, or <c>null</c>.</param>
public record CreateIssueRequest(string? Id, IssueType? Type, string? Title, string? Description,
    IssueSeverity? Severity = null, List<string>? RepositoryNames = null,
    string? SourceGoalId = null, string? SourceRole = null, int? SourceIteration = null);

/// <summary>Request body for partially updating an issue via the HTTP API.</summary>
/// <param name="Type">New category of the issue, or <c>null</c> to leave unchanged.</param>
/// <param name="Title">New short summary, or <c>null</c> to leave unchanged.</param>
/// <param name="Description">New detailed description, or <c>null</c> to leave unchanged.</param>
/// <param name="Severity">New severity, or <c>null</c> to leave unchanged.</param>
/// <param name="Status">New lifecycle status, or <c>null</c> to leave unchanged.</param>
/// <param name="RepositoryNames">Replacement repository list, or <c>null</c> to leave unchanged.</param>
/// <param name="LinkedGoalId">New linked goal ID; <c>null</c> = no change, empty string = clear, non-empty = set.</param>
public record UpdateIssueRequest(IssueType? Type = null, string? Title = null, string? Description = null,
    IssueSeverity? Severity = null, IssueStatus? Status = null,
    List<string>? RepositoryNames = null, string? LinkedGoalId = null);

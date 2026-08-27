using System.Text.RegularExpressions;

using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Models;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
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
            return Results.Ok(new HealthResponse
            {
                Status = "Healthy",
                Uptime = $"{(int)uptime.TotalHours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}",
                UptimeSpan = uptime,
                ActiveGoals = goals.Count(g => g.Status is GoalStatus.Pending or GoalStatus.InProgress),
                CompletedGoals = goals.Count(g => g.Status == GoalStatus.Completed),
                ConnectedWorkers = workerPool.GetAllWorkers().Count,
                Version = version,
                SharpCoderVersion = typeof(SharpCoder.CodingAgent).Assembly.GetName().Version?.ToString(),
                ServerTime = DateTime.UtcNow,
                CheckNumber = count,
                WorkerPool = workerPool.GetDetailedStats(),
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
    /// Registers the goals REST API endpoints under <c>/api/goals</c>.
    /// </summary>
    /// <param name="app">The web application to register routes on.</param>
    public static void MapGoalEndpoints(this WebApplication app)
    {
        // ── Goals REST API ───────────────────────────────────────────────────────
        var goalsApi = app.MapGroup("/api/goals");

        goalsApi.MapGet("/", async (IGoalStore store) =>
            Results.Ok(await store.GetAllGoalsAsync()));

        goalsApi.MapGet("/{id}", async (string id, IGoalStore store) =>
        {
            var goal = await store.GetGoalAsync(id);
            return goal is null ? Results.NotFound(new { error = $"Goal '{id}' not found." }) : Results.Ok(goal);
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

        goalsApi.MapPatch("/{id}/status", async (string id, GoalStatusUpdate update,
            [FromServices] IGoalFacade facade) =>
        {
            var result = await facade.UpdateGoalStatusAsync(id, update.Status);
            return result.Success
                ? Results.Ok(result.Value)
                : MapGoalFacadeError(result.Kind, result.Error);
        });

        goalsApi.MapDelete("/{id}", async (string id, [FromServices] IGoalFacade facade) =>
        {
            var result = await facade.DeleteGoalAsync(id);
            return result.Success
                ? Results.NoContent()
                : MapGoalFacadeError(result.Kind, result.Error);
        });

        goalsApi.MapPost("/{id}/cancel", async (string id, [FromServices] IGoalFacade facade) =>
        {
            var result = await facade.CancelGoalAsync(id);
            return result.Success
                ? Results.Ok(result.Value)
                : MapGoalFacadeError(result.Kind, result.Error);
        });

        goalsApi.MapPost("/{id}/extend-iterations", async (string id, ExtendIterationsRequest request,
            [FromServices] IGoalFacade facade, CancellationToken ct) =>
        {
            var result = await facade.ExtendIterationsAsync(id, request.AdditionalIterations, ct);
            if (result.Success)
                return Results.Ok(result.Value);

            // This route answers an unavailable dispatcher with a BARE 503 (no body) — not the
            // problem-details form the other facade routes use — so the contract is unchanged.
            return result.Kind == FacadeErrorKind.ServiceUnavailable
                ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                : MapGoalFacadeError(result.Kind, result.Error);
        });

        goalsApi.MapGet("/search", async (string q, string? status, IGoalStore store) =>
        {
            GoalStatus? statusFilter = null;
            if (!string.IsNullOrEmpty(status) && Enum.TryParse<GoalStatus>(status, ignoreCase: true, out var s))
                statusFilter = s;
            var results = await store.SearchGoalsAsync(q, statusFilter);
            return Results.Ok(results);
        });

        goalsApi.MapPatch("/{id}/release", async (string id, AssignGoalReleaseRequest request,
            [FromServices] IGoalFacade facade) =>
        {
            var result = await facade.AttachReleaseAsync(id, request.ReleaseId);
            return result.Success
                ? Results.Ok(result.Value)
                : MapGoalFacadeError(result.Kind, result.Error);
        });

        goalsApi.MapPatch("/{id}/review-status", async (string id, GoalReviewStatusUpdate update, IGoalStore store, [FromServices] DashboardNotifier dashboardNotifier) =>
        {
            var goal = await store.GetGoalAsync(id);
            if (goal is null)
                return Results.NotFound(new { error = $"Goal '{id}' not found." });

            // Reject numeric input — only named enum values are accepted.
            if (int.TryParse(update.ReviewStatus, out _))
                return Results.BadRequest(new { error = "Invalid review status. Valid values: none, pending, approved, needschanges." });

            // Parse and validate that the result is a defined single enum value.
            if (!Enum.TryParse<ReviewStatus>(update.ReviewStatus, ignoreCase: true, out var reviewStatus) || !Enum.IsDefined(reviewStatus))
                return Results.BadRequest(new { error = "Invalid review status. Valid values: none, pending, approved, needschanges." });

            // Reject comma-combined inputs (e.g. "Pending, Approved") that Enum.TryParse may accept via bitwise OR.
            if (update.ReviewStatus.Contains(','))
                return Results.BadRequest(new { error = "Invalid review status. Valid values: none, pending, approved, needschanges." });

            goal.ReviewStatus = reviewStatus;
            await store.UpdateGoalAsync(goal);
            var updated = await store.GetGoalAsync(id);
            dashboardNotifier.NotifyStateChanged();
            return Results.Ok(updated);
        });

        goalsApi.MapPost("/{goalId}/review", async (string goalId, [FromServices] IGoalFacade facade, CancellationToken ct) =>
        {
            var result = await facade.RequestReviewAsync(goalId, ct);
            return result.Success
                ? Results.Ok(result.Value)
                : MapGoalFacadeError(result.Kind, result.Error);
        });
    }

    /// <summary>
    /// Maps a <see cref="FacadeErrorKind"/> from <see cref="IGoalFacade"/> to the exact HTTP
    /// response the pre-facade goal handlers produced: <c>NotFound</c>, <c>BadRequest</c> and
    /// <c>Conflict</c> all return a JSON <c>{error}</c> body. <see cref="FacadeErrorKind.None"/>
    /// is a programming error (a success result must not be mapped here) and throws, as does
    /// any kind these six routes never produce — including
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
            _ => throw new InvalidOperationException($"Unexpected goal facade error kind: {kind}."),
        };
    }

    /// <summary>
    /// Registers the releases REST API endpoints under <c>/api/releases</c>.
    /// </summary>
    /// <param name="app">The web application to register routes on.</param>
    public static void MapReleaseEndpoints(this WebApplication app)
    {
        // ── Releases REST API ────────────────────────────────────────────────────
        var releasesApi = app.MapGroup("/api/releases");

        releasesApi.MapPost("/", async (CreateReleaseRequest request, IGoalStore store, [FromServices] DashboardNotifier dashboardNotifier) =>
        {
            if (string.IsNullOrWhiteSpace(request.Version))
                return Results.BadRequest(new { error = "Version is required." });

            var release = new Release
            {
                Id = request.Version,
                Tag = request.Version,
                RepositoryNames = string.IsNullOrEmpty(request.Repository) ? [] : [request.Repository],
            };

            try
            {
                var created = await store.CreateReleaseAsync(release);
                var createdResult = Results.Created($"/api/releases/{created.Id}", created);
                dashboardNotifier.NotifyStateChanged();
                return createdResult;
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        releasesApi.MapPatch("/{id}/status", async (string id, UpdateReleaseStatusRequest request, IGoalStore store, IServiceProvider services, HttpContext HttpContext, [FromServices] DashboardNotifier dashboardNotifier, [FromServices] IEventBus? eventBus = null) =>
        {
            var existing = await store.GetReleaseAsync(id);
            if (existing is null)
                return Results.NotFound(new { error = $"Release '{id}' not found." });

            if (string.IsNullOrEmpty(request.Status))
                return Results.BadRequest(new { error = "Status is required." });

            if (int.TryParse(request.Status, out _))
                return Results.BadRequest(new { error = $"Invalid status '{request.Status}'. Valid values: Planning, Released." });

            if (!Enum.TryParse<ReleaseStatus>(request.Status, ignoreCase: true, out var newStatus) || !Enum.IsDefined(newStatus))
                return Results.BadRequest(new { error = $"Invalid status '{request.Status}'. Valid values: Planning, Released." });

            // Reject comma-combined inputs (e.g. "Released,Planning") that Enum.TryParse may accept via bitwise OR.
            if (request.Status.Contains(','))
                return Results.BadRequest(new { error = $"Invalid status '{request.Status}'. Valid values: Planning, Released." });

            if (existing.Status == ReleaseStatus.Released && newStatus == ReleaseStatus.Released)
                return Results.Json(new { error = "Release is already in 'Released' status." }, statusCode: 409);

            if (newStatus == ReleaseStatus.Planning && existing.Status == ReleaseStatus.Released)
                return Results.Json(new { error = "Cannot revert a Released release back to Planning." }, statusCode: 409);

            if (newStatus == ReleaseStatus.Planning && existing.Status == ReleaseStatus.Planning)
                return Results.Ok(existing);

            if (newStatus == ReleaseStatus.Released)
            {
                var execService = services.GetService<ReleaseExecutionService>();
                if (execService is null)
                    return Results.Json(new { detail = "Release execution service is not available." }, statusCode: 503);

                var result = await execService.ExecuteReleaseAsync(existing, HttpContext.RequestAborted);
                if (!result.Success)
                {
                    return result.Failure switch
                    {
                        ReleaseExecutionFailure.NotFound => Results.NotFound(new { error = result.Error }),
                        ReleaseExecutionFailure.AlreadyReleased => Results.Json(new { error = result.Error }, statusCode: 409),
                        ReleaseExecutionFailure.AlreadyExecuting => Results.Json(new { error = result.Error }, statusCode: 409),
                        ReleaseExecutionFailure.Validation => Results.BadRequest(new { errors = new[] { result.Error ?? "Validation failed." } }),
                        ReleaseExecutionFailure.Execution => Results.Json(new { detail = result.Error, results = result.Results }, statusCode: 500),
                        _ => throw new InvalidOperationException($"Unhandled release execution failure: {result.Failure}"),
                    };
                }

                // Re-read to avoid overwriting fields that changed during execution.
                var updated = await store.GetReleaseAsync(id);
                updated!.Status = ReleaseStatus.Released;
                updated.ReleasedAt = DateTime.UtcNow;
                await store.UpdateReleaseAsync(updated);

                eventBus?.Publish(new SystemEvent(
                    Type: EventType.ReleaseCompleted,
                    Message: $"Release '{updated.Tag}' marked as Released",
                    ReleaseId: updated.Id));

                // NuGet publish monitoring: fire-and-forget monitors for every configured
                // package of every PublishNuGet repository in this release. Both services are
                // required; the application lifetime is optional (CancellationToken.None when
                // absent so the monitor is not tied to a request). Failures are logged and
                // must never fail the release.
                LaunchNuGetMonitors(
                    services.GetService<NuGetPublishMonitorService>(),
                    services.GetService<HiveConfigFile>(),
                    services.GetService<IHostApplicationLifetime>(),
                    services.GetService<ILoggerFactory>()?.CreateLogger<NuGetPublishMonitorService>(),
                    updated);

                // Best-effort cleanup of transient progress/review knowledge documents for
                // every goal in the release. Failures are logged and must never fail the release.
                var docCleanup = services.GetService<KnowledgeDocumentCleanupService>();
                if (docCleanup is not null)
                {
                    try
                    {
                        var goals = await store.GetGoalsByReleaseAsync(id);
                        await docCleanup.CleanupGoalsDocumentsAsync(
                            goals.Select(g => g.Id),
                            $"Cleanup progress/review docs for release '{updated.Tag}'",
                            HttpContext.RequestAborted);
                    }
                    catch (Exception ex)
                    {
                        services.GetRequiredService<ILogger<Program>>().LogWarning(
                            ex, "Failed to clean up knowledge documents for release {Tag}", updated.Tag);
                    }
                }

                dashboardNotifier.NotifyStateChanged();
                return Results.Ok(new { release = updated, result = result });
            }

            existing.Status = newStatus;
            await store.UpdateReleaseAsync(existing);
            dashboardNotifier.NotifyStateChanged();
            return Results.Ok(existing);
        });

        releasesApi.MapPatch("/{id}/notes", async (string id, UpdateReleaseNotesRequest request, IGoalStore store, [FromServices] DashboardNotifier dashboardNotifier) =>
        {
            var existing = await store.GetReleaseAsync(id);
            if (existing is null)
                return Results.NotFound(new { error = $"Release '{id}' not found." });

            existing.Notes = request.Notes;
            await store.UpdateReleaseAsync(existing);
            dashboardNotifier.NotifyStateChanged();
            return Results.Ok(existing);
        });

        releasesApi.MapPatch("/{id}/tag", async (string id, UpdateReleaseTagRequest request, IGoalStore store, [FromServices] DashboardNotifier dashboardNotifier) =>
        {
            if (string.IsNullOrWhiteSpace(request.Tag))
                return Results.BadRequest(new { error = "Tag is required." });

            try
            {
                await store.UpdateReleaseAsync(id, new ReleaseUpdateData { Tag = request.Tag.Trim() });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Release '{id}' not found." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var updated = await store.GetReleaseAsync(id);
            dashboardNotifier.NotifyStateChanged();
            return Results.Ok(updated);
        });

        releasesApi.MapPatch("/{id}/repositories", async (string id, UpdateReleaseRepositoriesRequest request, IGoalStore store, [FromServices] DashboardNotifier dashboardNotifier) =>
        {
            try
            {
                await store.UpdateReleaseAsync(id, new ReleaseUpdateData { Repositories = request.Repositories });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = $"Release '{id}' not found." });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var updated = await store.GetReleaseAsync(id);
            dashboardNotifier.NotifyStateChanged();
            return Results.Ok(updated);
        });

        releasesApi.MapDelete("/{id}", async (string id, IGoalStore store, [FromServices] DashboardNotifier dashboardNotifier, CancellationToken ct) =>
        {
            var release = await store.GetReleaseAsync(id, ct);
            if (release is null)
                return Results.NotFound(new { error = $"Release '{id}' not found." });

            if (release.Status != ReleaseStatus.Planning)
                return Results.BadRequest(new { error = "Only Planning releases can be deleted." });

            if (release.ExecutionState == ReleaseExecutionState.Executing)
                return Results.Conflict(new { error = "Release is currently executing — cannot delete." });

            var goals = await store.GetGoalsByReleaseAsync(id, ct);
            if (goals.Count > 0)
                return Results.BadRequest(new { error = $"Release has {goals.Count} goal(s) attached — remove or reassign them before deleting." });

            // The store re-validates every precondition atomically. A false result here means a
            // concurrent state change slipped between the pre-checks above and the delete.
            var deleted = await store.DeleteReleaseAsync(id, ct);
            if (!deleted)
                return Results.Conflict(new { error = "Release could not be deleted due to a concurrent state change. Refresh and try again." });

            dashboardNotifier.NotifyStateChanged();
            return Results.NoContent();
        });

        releasesApi.MapGet("/{id}/validate", async (string id, IGoalStore store, IServiceProvider sp, CancellationToken ct) =>
        {
            var release = await store.GetReleaseAsync(id, ct);
            if (release is null) return Results.NotFound(new { error = $"Release '{id}' not found." });
            var execService = sp.GetService<ReleaseExecutionService>();
            if (execService is null) return Results.Ok(new { valid = true, errors = Array.Empty<string>() });
            var validation = await execService.ValidateReleaseAsync(release, ct);
            return Results.Ok(new { valid = validation.IsValid, errors = validation.Errors });
        });
    }

    /// <summary>
    /// Registers the issues REST API endpoints under <c>/api/issues</c>.
    /// </summary>
    /// <param name="app">The web application to register routes on.</param>
    public static void MapIssueEndpoints(this WebApplication app)
    {
        // ── Issues REST API ─────────────────────────────────────────────────────
        var issuesApi = app.MapGroup("/api/issues");

        issuesApi.MapGet("/", async (string? status, string? type, string? severity, string? repository,
            string? source_goal_id, string? linked_goal_id, IIssueStore issueStore, CancellationToken ct) =>
        {
            // Query params use explicit snake_case parsing: reject numeric strings and
            // comma-combined values that Enum.TryParse may otherwise accept via bitwise OR.
            // Enum.TryParse does not strip underscores, so snake_case inputs ("in_progress")
            // are normalized before the standard parse.
            IssueStatus? statusFilter = null;
            if (!string.IsNullOrEmpty(status))
            {
                var normalizedStatus = status.Replace("_", "", StringComparison.Ordinal);
                if (int.TryParse(status, out _) || status.Contains(',')
                    || !Enum.TryParse<IssueStatus>(normalizedStatus, ignoreCase: true, out var parsedStatus)
                    || !Enum.IsDefined(parsedStatus))
                {
                    return Results.BadRequest(new { error = $"Invalid status '{status}'. Valid values: open, triaged, acknowledged, in_progress, resolved, closed." });
                }
                statusFilter = parsedStatus;
            }

            IssueType? typeFilter = null;
            if (!string.IsNullOrEmpty(type))
            {
                // Explicit alias: "codequality" maps to IssueType.CodeQuality.
                IssueType parsedType;
                if (string.Equals(type, "codequality", StringComparison.OrdinalIgnoreCase))
                {
                    parsedType = IssueType.CodeQuality;
                }
                else
                {
                    var normalizedType = type.Replace("_", "", StringComparison.Ordinal);
                    if (int.TryParse(type, out _) || type.Contains(',')
                        || !Enum.TryParse<IssueType>(normalizedType, ignoreCase: true, out parsedType)
                        || !Enum.IsDefined(parsedType))
                    {
                        return Results.BadRequest(new { error = $"Invalid type '{type}'. Valid values: code_quality, bug, suggestion, concern, workflow." });
                    }
                }
                typeFilter = parsedType;
            }

            IssueSeverity? severityFilter = null;
            if (!string.IsNullOrEmpty(severity))
            {
                var normalizedSeverity = severity.Replace("_", "", StringComparison.Ordinal);
                if (int.TryParse(severity, out _) || severity.Contains(',')
                    || !Enum.TryParse<IssueSeverity>(normalizedSeverity, ignoreCase: true, out var parsedSeverity)
                    || !Enum.IsDefined(parsedSeverity))
                {
                    return Results.BadRequest(new { error = $"Invalid severity '{severity}'. Valid values: low, medium, high." });
                }
                severityFilter = parsedSeverity;
            }

            var issues = await issueStore.GetIssuesAsync(
                statusFilter, typeFilter, severityFilter, repository, source_goal_id, linked_goal_id, ct);
            return Results.Ok(issues.Select(ToResponse).ToList());
        });

        issuesApi.MapGet("/{id}", async (string id, IIssueStore issueStore, CancellationToken ct) =>
        {
            var issue = await issueStore.GetIssueAsync(id, ct);
            return issue is null
                ? Results.NotFound(new { error = $"Issue '{id}' not found." })
                : Results.Ok(ToResponse(issue));
        });

        issuesApi.MapPost("/", async (CreateIssueRequest request, IIssueStore issueStore, CancellationToken ct, [FromServices] IEventBus? eventBus = null) =>
        {
            if (request.Type is null)
                return Results.BadRequest(new { error = "Type is required." });

            if (string.IsNullOrWhiteSpace(request.Title))
                return Results.BadRequest(new { error = "Title is required." });

            if (string.IsNullOrWhiteSpace(request.Description))
                return Results.BadRequest(new { error = "Description is required." });

            // ID resolution: null/empty/whitespace → generate; non-empty → validate kebab-case.
            string id;
            if (request.Id is null)
            {
                id = await IssueIdGenerator.GenerateAsync(request.Title, issueStore, ct);
            }
            else
            {
                var trimmed = request.Id.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    id = await IssueIdGenerator.GenerateAsync(request.Title, issueStore, ct);
                }
                else if (!IssueIdPattern.IsMatch(trimmed))
                {
                    return Results.BadRequest(new { error = $"Invalid issue ID '{request.Id}'. IDs must be lowercase kebab-case (letters, digits, hyphens)." });
                }
                else
                {
                    id = trimmed;
                }
            }

            var issue = new Issue
            {
                Id = id,
                Type = request.Type.Value,
                Title = request.Title,
                Description = request.Description,
                Severity = request.Severity ?? IssueSeverity.Low,
                RepositoryNames = request.RepositoryNames ?? [],
                SourceGoalId = request.SourceGoalId,
                SourceRole = request.SourceRole,
                SourceIteration = request.SourceIteration,
            };

            try
            {
                var created = await issueStore.CreateIssueAsync(issue, ct);
                eventBus?.Publish(new SystemEvent(
                    Type: EventType.IssueRaised,
                    Message: created.Title,
                    IssueId: created.Id));
                return Results.Created($"/api/issues/{Uri.EscapeDataString(created.Id)}", ToResponse(created));
            }
            catch (InvalidOperationException)
            {
                return Results.Conflict(new { error = $"Issue '{id}' already exists." });
            }
        });

        issuesApi.MapPatch("/{id}", async (string id, UpdateIssueRequest request, IIssueStore issueStore, CancellationToken ct, [FromServices] IEventBus? eventBus = null) =>
        {
            var existing = await issueStore.GetIssueAsync(id, ct);
            if (existing is null)
                return Results.NotFound(new { error = $"Issue '{id}' not found." });

            // Capture original status before mutation so we can detect a transition to terminal.
            var previousStatus = existing.Status;

            // Title/Description, when provided, must be non-empty/whitespace.
            if (request.Title is not null && string.IsNullOrWhiteSpace(request.Title))
                return Results.BadRequest(new { error = "Title is required." });

            if (request.Description is not null && string.IsNullOrWhiteSpace(request.Description))
                return Results.BadRequest(new { error = "Description is required." });

            if (request.Type is not null) existing.Type = request.Type.Value;
            if (request.Title is not null) existing.Title = request.Title;
            if (request.Description is not null) existing.Description = request.Description;
            if (request.Severity is not null) existing.Severity = request.Severity.Value;
            if (request.Status is not null) existing.Status = request.Status.Value;
            if (request.RepositoryNames is not null) existing.RepositoryNames = request.RepositoryNames;

            // LinkedGoalId tri-state: null = no change, "" = clear, non-empty = set.
            if (request.LinkedGoalId is not null)
                existing.LinkedGoalId = request.LinkedGoalId.Length == 0 ? null : request.LinkedGoalId;

            await issueStore.UpdateIssueAsync(existing, ct);

            // Publish IssueResolved only on transition from non-terminal to terminal.
            if (request.Status is not null
                && request.Status.Value is IssueStatus.Resolved or IssueStatus.Closed
                && previousStatus is not IssueStatus.Resolved and not IssueStatus.Closed)
            {
                eventBus?.Publish(new SystemEvent(
                    Type: EventType.IssueResolved,
                    Message: $"Issue '{id}' marked as {request.Status.Value}",
                    IssueId: id,
                    GoalId: existing.LinkedGoalId));
            }

            // Re-fetch for accurate timestamps (UpdatedAt / ResolvedAt are store-managed).
            var updated = await issueStore.GetIssueAsync(id, ct);
            return Results.Ok(ToResponse(updated!));
        });

        issuesApi.MapDelete("/{id}", async (string id, IIssueStore issueStore, CancellationToken ct) =>
        {
            var deleted = await issueStore.DeleteIssueAsync(id, ct);
            return deleted
                ? Results.NoContent()
                : Results.NotFound(new { error = $"Issue '{id}' not found." });
        });
    }

    /// <summary>
    /// Registers the clarifications REST API endpoints under <c>/api/clarifications</c>.
    /// </summary>
    /// <param name="app">The web application to register routes on.</param>
    public static void MapClarificationEndpoints(this WebApplication app)
    {
        // ── Clarifications REST API ──────────────────────────────────────────────
        var clarificationsApi = app.MapGroup("/api/clarifications");

        clarificationsApi.MapGet("/", (ClarificationQueueService queue) =>
            Results.Ok(queue.GetAllRequests()));

        clarificationsApi.MapGet("/pending", (ClarificationQueueService queue) =>
            Results.Ok(queue.GetPendingHumanRequests()));

        clarificationsApi.MapGet("/count", (ClarificationQueueService queue) =>
            Results.Ok(new { count = queue.PendingHumanCount }));

        clarificationsApi.MapPost("/{id}/answer", (string id, SubmitClarificationRequest body, ClarificationQueueService queue) =>
        {
            if (string.IsNullOrWhiteSpace(body.Answer))
                return Results.BadRequest(new { error = "Answer is required." });

            var answered = queue.SubmitAnswer(id, body.Answer, "human");
            if (!answered)
                return Results.NotFound(new { error = $"Clarification '{id}' not found." });

            return Results.Ok(new { message = $"Answer submitted for clarification '{id}'." });
        });
    }

    /// <summary>
    /// Registers the backup REST API endpoints under <c>/api/backup</c>.
    /// </summary>
    /// <param name="app">The web application to register routes on.</param>
    public static void MapBackupEndpoints(this WebApplication app)
    {
        // ── Backup REST API ──────────────────────────────────────────────────────
        var backupApi = app.MapGroup("/api/backup");

        backupApi.MapPost("/", async ([FromServices] IBackupFacade facade, CancellationToken ct) =>
        {
            // Failures (including cancellation) propagate out of the facade and become a 500,
            // exactly as before the facade existed.
            var result = await facade.CreateBackupAsync(ct);
            return MapBackupFacadeResult(result);
        });

        backupApi.MapGet("/", ([FromServices] IBackupFacade facade) =>
            MapBackupFacadeResult(facade.GetBackups()));

        backupApi.MapGet("/{fileName}", (string fileName, [FromServices] BackupService svc) =>
        {
            // Path traversal protection: reject any fileName that is not a bare file name.
            if (string.IsNullOrWhiteSpace(fileName)
                || fileName.Contains('/')
                || fileName.Contains('\\')
                || fileName.Contains("..")
                || Path.GetFileName(fileName) != fileName)
            {
                return Results.BadRequest(new { error = "Invalid file name." });
            }

            var backupDir = Path.GetFullPath(svc.BackupDirectory);
            var fullPath = Path.GetFullPath(Path.Combine(backupDir, fileName));

            // Ensure the resolved path stays within the backup directory.
            var backupDirWithSep = backupDir.EndsWith(Path.DirectorySeparatorChar)
                ? backupDir
                : backupDir + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(backupDirWithSep, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Invalid file name." });

            if (!File.Exists(fullPath))
                return Results.NotFound(new { error = "Backup not found." });

            return Results.File(fullPath, "application/gzip", fileName);
        });

        backupApi.MapPost("/restore", async (RestoreRequest request, [FromServices] BackupService svc) =>
        {
            if (string.IsNullOrWhiteSpace(request.FileName))
                return Results.BadRequest(new { error = "FileName is required." });

            var fileName = request.FileName;
            if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..")
                || Path.GetFileName(fileName) != fileName)
            {
                return Results.BadRequest(new { error = "Invalid file name." });
            }

            var fullPath = Path.GetFullPath(Path.Combine(svc.BackupDirectory, fileName));
            var backupDirWithSep = Path.GetFullPath(svc.BackupDirectory)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(backupDirWithSep, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Invalid file name." });

            if (!File.Exists(fullPath))
                return Results.NotFound(new { error = "Backup not found." });

            var result = await svc.RestoreBackupAsync(fullPath);
            return Results.Ok(new
            {
                message = "Restore complete. Restart the orchestrator for changes to take effect.",
                result.DatabaseRestored,
                result.BrainMasterSession,
                result.BrainGoalSessionCount,
                result.ComposerSession,
                result.MetricsCount,
                result.KeysCount,
                result.SafetyBackupPath,
            });
        });
    }

    /// <summary>
    /// Maps a backup facade result to the HTTP response the pre-facade handlers produced.
    /// The backup facade only ever reports <see cref="FacadeErrorKind.None"/> (failures throw
    /// and become ASP.NET's 500), so <see cref="FacadeErrorKind.None"/> → 200 with the value;
    /// the switch exists for uniformity with the other facades and throws for any other kind
    /// rather than silently falling back.
    /// </summary>
    /// <typeparam name="T">The facade value type.</typeparam>
    /// <param name="result">The facade result to map.</param>
    /// <returns>A 200 result carrying the facade value.</returns>
    private static IResult MapBackupFacadeResult<T>(FacadeResult<T> result)
    {
        return result.Kind switch
        {
            FacadeErrorKind.None => Results.Ok(result.Value!),
            _ => throw new InvalidOperationException($"Unexpected backup facade error kind: {result.Kind}."),
        };
    }

    /// <summary>
    /// Registers the LLM sessions REST API endpoints under <c>/api/sessions</c>.
    /// </summary>
    /// <param name="app">The web application to register routes on.</param>
    public static void MapSessionEndpoints(this WebApplication app)
    {
        // ── LLM Sessions REST API ────────────────────────────────────────────────
        app.MapGet("/api/sessions", (LlmSessionRegistry registry) =>
            Results.Ok(registry.GetAll()));
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

    /// <summary>Validates caller-supplied issue IDs: ASCII lowercase letters, digits, hyphens only.</summary>
    private static readonly Regex IssueIdPattern = new(
        "^[a-z0-9]+(-[a-z0-9]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Maps an <see cref="Issue"/> entity to its wire representation <see cref="IssueResponse"/>.
    /// </summary>
    /// <param name="issue">The issue entity to convert.</param>
    /// <returns>The DTO returned by the API.</returns>
    private static IssueResponse ToResponse(Issue issue) => new(
        issue.Id,
        issue.Type,
        issue.Title,
        issue.Description,
        issue.Severity,
        issue.Status,
        issue.RepositoryNames,
        issue.SourceGoalId,
        issue.SourceRole,
        issue.SourceIteration,
        issue.CreatedAt,
        issue.UpdatedAt,
        issue.ResolvedAt,
        issue.LinkedGoalId);
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

/// <summary>Wire representation of an issue returned by the issues REST API.</summary>
/// <param name="Id">Unique kebab-case identifier for the issue.</param>
/// <param name="Type">Category of the issue.</param>
/// <param name="Title">Short summary of the issue.</param>
/// <param name="Description">Detailed markdown description of the issue.</param>
/// <param name="Severity">Severity of the issue.</param>
/// <param name="Status">Current lifecycle status of the issue.</param>
/// <param name="RepositoryNames">Names of repositories this issue applies to.</param>
/// <param name="SourceGoalId">ID of the goal that produced this issue, or <c>null</c> if user-reported.</param>
/// <param name="SourceRole">Role that produced this issue, or <c>null</c> if user-reported.</param>
/// <param name="SourceIteration">Iteration number in which the issue was produced, or <c>null</c>.</param>
/// <param name="CreatedAt">UTC timestamp when the issue was created.</param>
/// <param name="UpdatedAt">UTC timestamp of the last update, or <c>null</c> if never updated.</param>
/// <param name="ResolvedAt">UTC timestamp when the issue was resolved or closed, or <c>null</c>.</param>
/// <param name="LinkedGoalId">ID of a goal linked to this issue, or <c>null</c> if none.</param>
public record IssueResponse(string Id, IssueType Type, string Title, string Description,
    IssueSeverity Severity, IssueStatus Status, List<string> RepositoryNames,
    string? SourceGoalId, string? SourceRole, int? SourceIteration,
    DateTime CreatedAt, DateTime? UpdatedAt, DateTime? ResolvedAt, string? LinkedGoalId);

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

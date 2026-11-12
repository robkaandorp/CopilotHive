using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotHive.Tests;

/// <summary>
/// THE HTTP-LEVEL AUTHORIZATION BATTERY for the goal/issue REST API, over a REAL host booted
/// with authentication ENABLED, a REAL test authentication scheme, and the REAL
/// <see cref="UserService"/> directory (SQLite) that the facades re-resolve on every request.
/// <para>
/// THE ANSWERS THIS BATTERY PINS:
/// </para>
/// <list type="bullet">
///   <item>An ANONYMOUS caller gets 401 with NO redirect and NO <c>Location</c> header — the
///   programmatic API challenge, never an OAuth interstitial.</item>
///   <item>An AUTHENTICATED but INSUFFICIENT caller (Viewer/Member on an Operator operation), a
///   DEMOTED caller and a DISABLED caller all get 403 — never 400, never a validation answer.</item>
///   <item>The EXISTING 409 answers are preserved for an authorized caller (duplicate goal
///   creation, duplicate issue creation, a review already in progress).</item>
///   <item>A SUCCESSFUL GET response (goal list, goal detail, goal search) is BYTE-IDENTICAL to the
///   pre-change wire shape: the domain <see cref="Goal"/>, never a mutation DTO.</item>
/// </list>
/// </summary>
[Collection("HiveIntegration")]
public sealed class GoalIssueApiAuthorizationBatteryTests : IDisposable
{
    /// <summary>The header the test scheme reads the GitHub identity from.</summary>
    private const string IdentityHeader = "X-Test-GitHub-Id";

    private const string ViewerId = "api-viewer";
    private const string MemberId = "api-member";
    private const string OperatorId = "api-operator";

    private const string ForbiddenMessage = "The acting account is not authorized to perform this operation.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The REAL host, booted with OAuth CONFIGURED (so the authentication/authorization pipeline
    /// and the path-aware challenge handler are wired exactly as in production) but with the
    /// default AUTHENTICATION SCHEME replaced by a test scheme that reads the identity from a
    /// request header. The CHALLENGE scheme is left as the production
    /// <see cref="HiveChallengeHandler"/>, so the anonymous 401-no-redirect contract is exercised
    /// against the real handler and not a stand-in.
    /// </summary>
    private sealed class AuthApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _stateDir =
            Path.Combine(Path.GetTempPath(), $"copilothive-goalissue-api-{Guid.NewGuid():N}");
        private readonly string? _previousStateDir;
        private readonly string? _previousClientId;
        private readonly string? _previousClientSecret;
        private readonly string? _previousAllowInsecure;

        public AuthApiFactory()
        {
            _previousStateDir = Environment.GetEnvironmentVariable("STATE_DIR");
            _previousClientId = Environment.GetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID");
            _previousClientSecret = Environment.GetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET");
            _previousAllowInsecure = Environment.GetEnvironmentVariable("ALLOW_INSECURE_OAUTH");

            Environment.SetEnvironmentVariable("STATE_DIR", _stateDir);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID", "test-client-id");
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET", "test-client-secret");
            Environment.SetEnvironmentVariable("ALLOW_INSECURE_OAUTH", "true");
            Directory.CreateDirectory(_stateDir);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                services.ReplaceDistributedBrain(new NoOpDistributedBrain());

                // The test scheme authenticates the caller from a request header. The CHALLENGE
                // scheme stays the production path-aware handler, so the anonymous 401 contract
                // is exercised against the real implementation.
                services.AddAuthentication(options =>
                {
                    options.DefaultScheme = TestIdentityHandler.SchemeName;
                    options.DefaultChallengeScheme = HiveChallengeHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, TestIdentityHandler>(
                    TestIdentityHandler.SchemeName, _ => { });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            Environment.SetEnvironmentVariable("STATE_DIR", _previousStateDir);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID", _previousClientId);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET", _previousClientSecret);
            Environment.SetEnvironmentVariable("ALLOW_INSECURE_OAUTH", _previousAllowInsecure);

            if (!disposing || !Directory.Exists(_stateDir))
                return;

            try
            {
                Directory.Delete(_stateDir, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The test authentication scheme: it authenticates a request that carries the identity
    /// header and refuses every other request (so an anonymous request stays anonymous and the
    /// challenge pipeline answers it).
    /// </summary>
    private sealed class TestIdentityHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SchemeName = "TestIdentity";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(IdentityHeader, out var values))
                return Task.FromResult(AuthenticateResult.NoResult());

            var gitHubId = values.ToString();
            if (string.IsNullOrWhiteSpace(gitHubId))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, gitHubId)],
                authenticationType: SchemeName);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }

    private readonly AuthApiFactory _factory = new();

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });

    private static HttpRequestMessage Authenticated(HttpMethod method, string url, string gitHubId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(IdentityHeader, gitHubId);
        return request;
    }

    /// <summary>Builds an authenticated request carrying a JSON body.</summary>
    private static HttpRequestMessage Authenticated(HttpMethod method, string url, string gitHubId, HttpContent body)
    {
        var request = Authenticated(method, url, gitHubId);
        request.Content = body;
        return request;
    }

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    // ── Directory seeding (the REAL SQLite directory the facades re-read) ────

    private async Task SeedAccountAsync(string gitHubId, UserRole role)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Ct);
        db.Users.Add(new UserEntity
        {
            GitHubId = gitHubId,
            Username = gitHubId,
            AccessToken = "token-" + gitHubId,
            Role = role,
            Enabled = true,
            CreatedAt = "2024-01-01T00:00:00.0000000Z",
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task SetRoleAsync(string gitHubId, UserRole role)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Ct);
        var entity = await db.Users.SingleAsync(u => u.GitHubId == gitHubId, Ct);
        entity.Role = role;
        await db.SaveChangesAsync(Ct);
    }

    private async Task SetEnabledAsync(string gitHubId, bool enabled)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Ct);
        var entity = await db.Users.SingleAsync(u => u.GitHubId == gitHubId, Ct);
        entity.Enabled = enabled;
        await db.SaveChangesAsync(Ct);
    }

    private async Task SeedGoalAsync(string id, GoalStatus status = GoalStatus.Draft, ReviewStatus reviewStatus = ReviewStatus.None)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
        await store.CreateGoalAsync(new Goal
        {
            Id = id,
            Description = "Battery goal",
            Status = status,
            ReviewStatus = reviewStatus,
            CreatedAt = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc),
        }, Ct);
    }

    // ── 1. The anonymous caller ─────────────────────────────────────────────

    /// <summary>
    /// AN ANONYMOUS CALLER GETS 401 WITH NO REDIRECT on every covered goal and issue ROUTE —
    /// the three reads, the goal mutations and the whole issue surface — and no <c>Location</c>
    /// header and no OAuth correlation cookie are ever produced.
    /// </summary>
    [Fact]
    public async Task AnonymousCaller_Gets401WithNoRedirect_OnEveryCoveredRoute()
    {
        await SeedAccountAsync(ViewerId, UserRole.Viewer);
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedGoalAsync("anon-goal");

        using var client = NewClient();

        var requests = new (HttpMethod Method, string Url, HttpContent? Body)[]
        {
            (HttpMethod.Get, "/api/goals", null),
            (HttpMethod.Get, "/api/goals/anon-goal", null),
            (HttpMethod.Get, "/api/goals/search?q=anon", null),
            (HttpMethod.Patch, "/api/goals/anon-goal/status", Json(new { status = "Pending" })),
            (HttpMethod.Patch, "/api/goals/anon-goal/review-status", Json(new { reviewStatus = "approved" })),
            (HttpMethod.Patch, "/api/goals/anon-goal/release", Json(new { releaseId = "rel-1" })),
            (HttpMethod.Post, "/api/goals/anon-goal/cancel", null),
            (HttpMethod.Post, "/api/goals/anon-goal/extend-iterations", Json(new { additionalIterations = 5 })),
            (HttpMethod.Post, "/api/goals/anon-goal/review", null),
            (HttpMethod.Delete, "/api/goals/anon-goal", null),
            (HttpMethod.Get, "/api/issues", null),
            (HttpMethod.Get, "/api/issues/issue-1", null),
            (HttpMethod.Post, "/api/issues", Json(new { id = "issue-1", type = "bug", title = "T", description = "D" })),
            (HttpMethod.Patch, "/api/issues/issue-1", Json(new { severity = "high" })),
            (HttpMethod.Delete, "/api/issues/issue-1", null),
        };

        foreach (var (method, url, body) in requests)
        {
            using var request = new HttpRequestMessage(method, url);
            if (body is not null)
                request.Content = body;

            var response = await client.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.True(
                response.StatusCode is HttpStatusCode.Unauthorized,
                $"{method} {url} must answer 401 for an anonymous caller, got {(int)response.StatusCode}.");

            // NO redirect: no Location header and no OAuth handshake was started.
            Assert.Null(response.Headers.Location);
            Assert.False(
                response.Headers.TryGetValues("Set-Cookie", out var cookies)
                && cookies.Any(cookie => cookie.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal)),
                $"{method} {url} must not start an OAuth handshake for a programmatic caller");
        }
    }

    // ── 2. The authenticated-but-insufficient caller ────────────────────────

    /// <summary>
    /// AN AUTHENTICATED BUT INSUFFICIENT CALLER GETS 403 (never 400) on every goal mutation and
    /// on the whole issue update/delete surface, and on issue create for a VIEWER. The 403 body
    /// carries the SAME <c>{ error }</c> shape the other failure kinds use and the sanitized
    /// denial text — never a validation message that would leak the schema.
    /// </summary>
    [Fact]
    public async Task InsufficientCaller_Gets403_Not400_OnEveryMutation()
    {
        await SeedAccountAsync(ViewerId, UserRole.Viewer);
        await SeedAccountAsync(MemberId, UserRole.Member);
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedGoalAsync("denied-goal");

        using var client = NewClient();

        var goalMutations = new (string Actor, HttpMethod Method, string Url, HttpContent? Body)[]
        {
            (ViewerId, HttpMethod.Delete, "/api/goals/denied-goal", null),
            (ViewerId, HttpMethod.Patch, "/api/goals/denied-goal/status", Json(new { status = "Pending" })),
            (ViewerId, HttpMethod.Patch, "/api/goals/denied-goal/review-status", Json(new { reviewStatus = "approved" })),
            (ViewerId, HttpMethod.Post, "/api/goals/denied-goal/cancel", null),
            (ViewerId, HttpMethod.Post, "/api/goals/denied-goal/extend-iterations", Json(new { additionalIterations = 5 })),
            (ViewerId, HttpMethod.Post, "/api/goals/denied-goal/review", null),
            (ViewerId, HttpMethod.Patch, "/api/goals/denied-goal/release", Json(new { releaseId = "rel-1" })),
            (MemberId, HttpMethod.Delete, "/api/goals/denied-goal", null),
            (MemberId, HttpMethod.Patch, "/api/goals/denied-goal/status", Json(new { status = "Pending" })),
            (MemberId, HttpMethod.Patch, "/api/goals/denied-goal/review-status", Json(new { reviewStatus = "approved" })),
            (MemberId, HttpMethod.Post, "/api/goals/denied-goal/cancel", null),
            (MemberId, HttpMethod.Post, "/api/goals/denied-goal/extend-iterations", Json(new { additionalIterations = 5 })),
            (MemberId, HttpMethod.Post, "/api/goals/denied-goal/review", null),
            (MemberId, HttpMethod.Patch, "/api/goals/denied-goal/release", Json(new { releaseId = "rel-1" })),
        };

        foreach (var (actor, method, url, body) in goalMutations)
        {
            using var request = Authenticated(method, url, actor);
            if (body is not null)
                request.Content = body;

            var response = await client.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await AssertForbiddenBodyAsync(response);
        }

        // ISSUE CREATE needs InteractiveMember: a Viewer is refused.
        using (var request = Authenticated(HttpMethod.Post, "/api/issues", ViewerId,
                   Json(new { id = "viewer-issue", type = "bug", title = "T", description = "D" })))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await AssertForbiddenBodyAsync(response);
        }

        // THE WHOLE ISSUE UPDATE SURFACE and the delete need ExecutionOperator.
        var issueUpdates = new (string Actor, HttpContent Body)[]
        {
            (ViewerId, Json(new { type = "bug" })),
            (ViewerId, Json(new { status = "resolved" })),
            (ViewerId, Json(new { severity = "high" })),
            (ViewerId, Json(new { title = "renamed" })),
            (ViewerId, Json(new { description = "changed" })),
            (ViewerId, Json(new { repositoryNames = new[] { "RepoA" } })),
            (ViewerId, Json(new { linkedGoalId = "denied-goal" })),
            (ViewerId, Json(new { linkedGoalId = "" })),
            (ViewerId, Json(new
            {
                type = "concern", status = "closed", severity = "low",
                title = "combined", description = "combined",
                repositoryNames = new[] { "RepoA" }, linkedGoalId = "denied-goal",
            })),
            (MemberId, Json(new { type = "bug" })),
            (MemberId, Json(new { status = "resolved" })),
            (MemberId, Json(new { severity = "high" })),
            (MemberId, Json(new { title = "renamed" })),
            (MemberId, Json(new { description = "changed" })),
            (MemberId, Json(new { repositoryNames = new[] { "RepoA" } })),
            (MemberId, Json(new { linkedGoalId = "denied-goal" })),
            (MemberId, Json(new { linkedGoalId = "" })),
            (MemberId, Json(new
            {
                type = "concern", status = "closed", severity = "low",
                title = "combined", description = "combined",
                repositoryNames = new[] { "RepoA" }, linkedGoalId = "denied-goal",
            })),
        };

        foreach (var (actor, body) in issueUpdates)
        {
            using var request = Authenticated(HttpMethod.Patch, "/api/issues/issue-1", actor);
            request.Content = body;
            var response = await client.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await AssertForbiddenBodyAsync(response);
        }

        foreach (var actor in new[] { ViewerId, MemberId })
        {
            using var request = Authenticated(HttpMethod.Delete, "/api/issues/issue-1", actor);
            var response = await client.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await AssertForbiddenBodyAsync(response);
        }
    }

    // ── 3. The demoted and the disabled caller ─────────────────────────────

    /// <summary>
    /// A DEMOTED CALLER GETS 403 ON THE VERY NEXT REQUEST: an Operator demoted to Viewer loses
    /// every goal mutation and the whole issue update/delete surface immediately, and the answer
    /// is the authorization answer (403), never the validation answer (400).
    /// </summary>
    [Fact]
    public async Task DemotedCaller_Gets403_OnTheVeryNextRequest()
    {
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedGoalAsync("demoted-goal");

        using var client = NewClient();

        // BEFORE the demotion the Operator is authorized.
        using (var request = Authenticated(HttpMethod.Patch, "/api/goals/demoted-goal/status", OperatorId))
        {
            request.Content = Json(new { status = "Pending" });
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await SetRoleAsync(OperatorId, UserRole.Viewer);

        using (var request = Authenticated(HttpMethod.Patch, "/api/goals/demoted-goal/status", OperatorId))
        {
            request.Content = Json(new { status = "Draft" });
            var response = await client.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await AssertForbiddenBodyAsync(response);
        }

        using (var request = Authenticated(HttpMethod.Delete, "/api/goals/demoted-goal", OperatorId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var request = Authenticated(HttpMethod.Patch, "/api/issues/issue-1", OperatorId))
        {
            request.Content = Json(new { severity = "high" });
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    /// <summary>
    /// A DISABLED CALLER GETS 403 ON THE VERY NEXT REQUEST — including on the READS a disabled
    /// account used to hold, because a disabled account holds no capability at all.
    /// </summary>
    [Fact]
    public async Task DisabledCaller_Gets403_OnTheVeryNextRequest()
    {
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedGoalAsync("disabled-goal");

        using var client = NewClient();

        // BEFORE the disablement the Operator may read and mutate.
        using (var request = Authenticated(HttpMethod.Get, "/api/goals", OperatorId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await SetEnabledAsync(OperatorId, enabled: false);

        using (var request = Authenticated(HttpMethod.Get, "/api/goals", OperatorId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await AssertForbiddenBodyAsync(response);
        }

        using (var request = Authenticated(HttpMethod.Get, "/api/goals/disabled-goal", OperatorId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var request = Authenticated(HttpMethod.Delete, "/api/goals/disabled-goal", OperatorId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    // ── 4. The existing 409 answers are preserved ───────────────────────────

    /// <summary>
    /// THE EXISTING 409 ANSWERS ARE PRESERVED FOR AN AUTHORIZED CALLER: a duplicate goal
    /// creation, a duplicate issue creation and a review already in progress all still answer
    /// 409 — the authorization gate did not swallow or reshape them.
    /// </summary>
    [Fact]
    public async Task AuthorizedCaller_Existing409AnswersArePreserved()
    {
        await SeedAccountAsync(OperatorId, UserRole.Operator);

        using var client = NewClient();

        // Duplicate goal creation → 409 (the creation route is unchanged by this slice).
        var goalBody = """{"id":"dup-goal","description":"dup"}""";
        using (var request = Authenticated(HttpMethod.Post, "/api/goals", OperatorId,
                   new StringContent(goalBody, Encoding.UTF8, "application/json")))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using (var request = Authenticated(HttpMethod.Post, "/api/goals", OperatorId,
                   new StringContent(goalBody, Encoding.UTF8, "application/json")))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        // Duplicate issue creation → 409.
        var issueBody = """{"id":"dup-issue","type":"bug","title":"T","description":"D"}""";
        using (var request = Authenticated(HttpMethod.Post, "/api/issues", OperatorId,
                   new StringContent(issueBody, Encoding.UTF8, "application/json")))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using (var request = Authenticated(HttpMethod.Post, "/api/issues", OperatorId,
                   new StringContent(issueBody, Encoding.UTF8, "application/json")))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        // A review already in progress → 409 through the covered review route.
        await SeedGoalAsync("review-conflict-goal", GoalStatus.Draft, ReviewStatus.Pending);
        using (var request = Authenticated(HttpMethod.Post, "/api/goals/review-conflict-goal/review", OperatorId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
    }

    // ── 5. The successful GET wire shape is preserved ───────────────────────

    /// <summary>
    /// THE SUCCESSFUL GET WIRE SHAPE IS BYTE-IDENTICAL TO THE PRE-CHANGE SHAPE: the goal list,
    /// the goal detail and the goal search all serialize the DOMAIN goal — the same property
    /// set, in the same order, with the same values and the same snake_case enum strings — and
    /// NEVER a mutation DTO (which carries no <c>brainUsage</c>/<c>reviewUsage</c> and no
    /// <c>targetRepositoryNames</c>).
    /// </summary>
    [Fact]
    public async Task SuccessfulGet_GoalListDetailAndSearch_KeepThePreChangeWireShape()
    {
        await SeedAccountAsync(ViewerId, UserRole.Viewer);
        await SeedGoalAsync("shape-goal");

        using var client = NewClient();

        var expectedGoal = new
        {
            id = "shape-goal",
            description = "Battery goal",
            priority = "normal",
            scope = "patch",
            status = "draft",
            repositoryNames = Array.Empty<string>(),
            targetRepositoryNames = (string?)null,
            dependsOn = Array.Empty<string>(),
            metadata = new Dictionary<string, string>(),
            createdAt = "2024-05-06T07:08:09Z",
            startedAt = (string?)null,
            completedAt = (string?)null,
            iterations = (int?)null,
            failureReason = (string?)null,
            notes = Array.Empty<string>(),
            phaseDurations = (object?)null,
            iterationSummaries = Array.Empty<object>(),
            totalDurationSeconds = (double?)null,
            mergeCommitHash = (string?)null,
            releaseId = (string?)null,
            documents = Array.Empty<string>(),
            branchCleanedUp = false,
            reviewStatus = "none",
            brainUsage = (object?)null,
            reviewUsage = (object?)null,
        };

        // LIST.
        using (var request = Authenticated(HttpMethod.Get, "/api/goals", ViewerId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync(Ct);
            using var doc = JsonDocument.Parse(body);
            var goal = Assert.Single(doc.RootElement.EnumerateArray().ToList());
            AssertGoalShape(goal, expectedGoal);
        }

        // DETAIL.
        using (var request = Authenticated(HttpMethod.Get, "/api/goals/shape-goal", ViewerId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync(Ct);
            using var doc = JsonDocument.Parse(body);
            AssertGoalShape(doc.RootElement, expectedGoal);
        }

        // SEARCH.
        using (var request = Authenticated(HttpMethod.Get, "/api/goals/search?q=shape", ViewerId))
        {
            var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync(Ct);
            using var doc = JsonDocument.Parse(body);
            var goal = Assert.Single(doc.RootElement.EnumerateArray().ToList());
            AssertGoalShape(goal, expectedGoal);
        }
    }

    /// <summary>
    /// The ASSERTED GOAL SHAPE: every expected property is present with the expected value AND
    /// the property SET is exactly the domain goal's property set (no extra property, no missing
    /// one), so a DTO substitution or a dropped field fails here.
    /// </summary>
    private static void AssertGoalShape(JsonElement goal, object expected)
    {
        var expectedProperties = expected.GetType().GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var actualProperties = goal.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expectedProperties.OrderBy(n => n, StringComparer.Ordinal),
            actualProperties.OrderBy(n => n, StringComparer.Ordinal));

        // The values, read through the EXPECTED shape so the assertion reads as the contract.
        var expectedJson = JsonSerializer.SerializeToElement(expected, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        foreach (var property in expectedJson.EnumerateObject())
        {
            var actual = goal.GetProperty(property.Name);
            Assert.True(
                JsonElement.DeepEquals(property.Value, actual),
                $"the '{property.Name}' property changed: expected {property.Value.GetRawText()}, got {actual.GetRawText()}.");
        }
    }

    /// <summary>Asserts the 403 body carries the sanitized denial text in the <c>{ error }</c> shape.</summary>
    private static async Task AssertForbiddenBodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        using var doc = JsonDocument.Parse(body);

        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("error", out var error), "the 403 body must carry an 'error' property");
        Assert.Equal(ForbiddenMessage, error.GetString());
        Assert.Single(doc.RootElement.EnumerateObject());
    }

    public void Dispose() => _factory.Dispose();
}

using System.Net;
using System.Security.Claims;
using System.Text;

using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Persistence.Entities;
using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotHive.Tests;

/// <summary>
/// THE PRE-BINDING AUTHORIZATION BATTERY: the HTTP-level contract that authorization is answered
/// BEFORE minimal-API model binding / JSON deserialization on every covered MUTATING goal/issue
/// route.
/// <para>
/// EVERY covered mutating route is exercised with the bodies that FAIL BINDING — malformed JSON,
/// an invalid enum value and a wrong JSON type — and each body is sent by FOUR caller classes:
/// </para>
/// <list type="bullet">
///   <item><b>ANONYMOUS</b> → 401 with NO redirect (the path-aware challenge).</item>
///   <item><b>AUTHENTICATED BUT INSUFFICIENT</b> (a Viewer, and a Member on an Operator route) →
///   403, NEVER 400.</item>
///   <item><b>DEMOTED</b> and <b>DISABLED</b> → 403, NEVER 400.</item>
///   <item><b>AUTHORIZED</b> → the EXISTING answer, which for a binding-failing body is a 400 with
///   an EMPTY body. The status AND the body are PINNED, so the fix provably did not change the
///   authorized caller's behaviour.</item>
/// </list>
/// <para>
/// The routes that take NO body (the two DELETEs, cancel and review) are covered by the same
/// matrix with their OWN authorized answer pinned — they cannot produce a binding failure, but
/// they must still answer 401 for an anonymous caller and 403 for an insufficient, demoted or
/// disabled one, and their authorized answer must be unchanged.
/// </para>
/// <para>
/// The 403 bodies are checked to carry the sanitized denial text, so a 403 that leaked a
/// validation message would fail here; and the 401s are checked to carry no OAuth interstitial.
/// </para>
/// </summary>
[Collection("HiveIntegration")]
public sealed class GoalIssuePreBindingApiBatteryTests : IDisposable
{
    /// <summary>The header the test scheme reads the GitHub identity from.</summary>
    private const string IdentityHeader = "X-Test-GitHub-Id";

    private const string ViewerId = "pbb-viewer";
    private const string MemberId = "pbb-member";
    private const string OperatorId = "pbb-operator";

    private const string ForbiddenMessage = "The acting account is not authorized to perform this operation.";

    /// <summary>The Draft goal the mutating goal routes target.</summary>
    private const string GoalId = "pbb-goal";

    /// <summary>The goal whose review is ALREADY IN PROGRESS, so the review route answers 409.</summary>
    private const string ReviewConflictGoalId = "pbb-review-goal";

    private const string IssueId = "pbb-issue";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Host ────────────────────────────────────────────────────────────────

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
            Path.Combine(Path.GetTempPath(), $"copilothive-prebinding-battery-{Guid.NewGuid():N}");
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

    private static HttpRequestMessage Request(HttpMethod method, string url, string? gitHubId, string? body)
    {
        var request = new HttpRequestMessage(method, url);
        if (gitHubId is not null)
            request.Headers.Add(IdentityHeader, gitHubId);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

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

    private async Task SeedGoalsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
        await store.CreateGoalAsync(new Goal
        {
            Id = GoalId,
            Description = "Pre-binding battery goal",
            Status = GoalStatus.Draft,
        }, Ct);
        // A goal whose review is ALREADY IN PROGRESS, so the review route answers the pre-existing
        // 409 without needing a real reviewer.
        await store.CreateGoalAsync(new Goal
        {
            Id = ReviewConflictGoalId,
            Description = "Pre-binding battery review goal",
            Status = GoalStatus.Draft,
            ReviewStatus = ReviewStatus.Pending,
        }, Ct);
    }

    private async Task SeedIssueAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IIssueStore>();
        await store.CreateIssueAsync(new Issue
        {
            Id = IssueId,
            Title = "Pre-binding battery issue",
            Description = "d",
            Type = IssueType.Bug,
        }, Ct);
    }

    // ── The matrix ──────────────────────────────────────────────────────────

    /// <summary>
    /// EVERY covered MUTATING route, paired with the least role that may call it, the body kind
    /// and the AUTHORIZED CALLER'S PINNED ANSWER (the status code and whether the body is empty).
    /// <para>
    /// A body kind of <c>none</c> marks a route that takes no body at all: it cannot produce a
    /// binding failure, so its authorized answer is the route's own.
    /// </para>
    /// </summary>
    /// <summary>
    /// ONE covered MUTATING route: its HTTP method, its URL, the least role that may call it, the
    /// body kind and the AUTHORIZED CALLER'S PINNED ANSWER (the status code and whether the body
    /// is empty).
    /// <para>
    /// A body kind of <c>none</c> marks a route that takes no body at all: it cannot produce a
    /// binding failure, so its authorized answer is the route's own.
    /// </para>
    /// </summary>
    public sealed record PreBindingCase(
        string Method,
        string Url,
        string BodyKind,
        string RequiredRole,
        int AuthorizedStatus,
        bool AuthorizedBodyEmpty);

    /// <summary>EVERY covered MUTATING route, with its body kinds and its pinned authorized answer.</summary>
    public static TheoryData<PreBindingCase> PreBindingMutations
    {
        get
        {
            var data = new TheoryData<PreBindingCase>();
            foreach (var testCase in AllPreBindingCases)
                data.Add(testCase);
            return data;
        }
    }

    /// <summary>EVERY covered MUTATING route, with its body kinds and its pinned authorized answer.</summary>
    public static IEnumerable<PreBindingCase> AllPreBindingCases
    {
        get
        {
            // ── The issue surface: three binding-failing bodies each ──────────
            yield return new PreBindingCase("PATCH", $"/api/issues/{IssueId}", "malformed-json", "operator", 400, true);
            yield return new PreBindingCase("PATCH", $"/api/issues/{IssueId}", "invalid-enum", "operator", 400, true);
            yield return new PreBindingCase("PATCH", $"/api/issues/{IssueId}", "wrong-json-type", "operator", 400, true);
            yield return new PreBindingCase("POST", "/api/issues", "malformed-json", "member", 400, true);
            yield return new PreBindingCase("POST", "/api/issues", "invalid-enum", "member", 400, true);
            yield return new PreBindingCase("POST", "/api/issues", "wrong-json-type", "member", 400, true);
            yield return new PreBindingCase("DELETE", $"/api/issues/{IssueId}", "none", "operator", 204, true);

            // ── The goal surface: the body-failing routes ──────────────────────
            yield return new PreBindingCase("PATCH", $"/api/goals/{GoalId}/status", "malformed-json", "operator", 400, true);
            yield return new PreBindingCase("PATCH", $"/api/goals/{GoalId}/status", "wrong-json-type", "operator", 400, true);
            yield return new PreBindingCase("PATCH", $"/api/goals/{GoalId}/review-status", "malformed-json", "operator", 400, true);
            yield return new PreBindingCase("PATCH", $"/api/goals/{GoalId}/review-status", "wrong-json-type", "operator", 400, true);
            yield return new PreBindingCase("PATCH", $"/api/goals/{GoalId}/release", "malformed-json", "operator", 400, true);
            yield return new PreBindingCase("PATCH", $"/api/goals/{GoalId}/release", "wrong-json-type", "operator", 400, true);
            yield return new PreBindingCase("POST", $"/api/goals/{GoalId}/extend-iterations", "malformed-json", "operator", 400, true);
            yield return new PreBindingCase("POST", $"/api/goals/{GoalId}/extend-iterations", "wrong-json-type", "operator", 400, true);
            yield return new PreBindingCase("POST", $"/api/goals/{GoalId}/extend-iterations", "invalid-enum", "operator", 400, true);

            // ── The goal surface: the no-body routes ──────────────────────────
            // A Draft goal is deletable → 204; a Draft goal cannot be cancelled → 400 with the
            // pre-existing message; a goal whose review is already pending → 409.
            yield return new PreBindingCase("DELETE", $"/api/goals/{GoalId}", "none", "operator", 204, true);
            yield return new PreBindingCase("POST", $"/api/goals/{GoalId}/cancel", "none", "operator", 400, false);
            yield return new PreBindingCase("POST", $"/api/goals/{ReviewConflictGoalId}/review", "none", "operator", 409, false);
        }
    }

    /// <summary>
    /// The RAW BODY for one body kind, per route. A body that fails binding is what makes the
    /// pre-binding gate observable: had binding run first, the caller would have received the
    /// 400 instead of the authorization answer.
    /// </summary>
    private static string? BodyFor(string kind, string url)
    {
        if (kind == "none")
            return null;

        if (url.StartsWith("/api/issues/", StringComparison.Ordinal))
        {
            return kind switch
            {
                "malformed-json" => "{ this is not json",
                "invalid-enum" => """{"severity":"not-a-severity"}""",
                "wrong-json-type" => """{"title":123}""",
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown body kind"),
            };
        }

        if (url.StartsWith("/api/issues", StringComparison.Ordinal))
        {
            return kind switch
            {
                "malformed-json" => "{ this is not json",
                "invalid-enum" => """{"type":"not-a-type","title":"t","description":"d"}""",
                "wrong-json-type" => """{"title":123}""",
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown body kind"),
            };
        }

        if (url.EndsWith("/status", StringComparison.Ordinal))
        {
            return kind switch
            {
                "malformed-json" => "{ this is not json",
                "wrong-json-type" => """{"status":123}""",
                "invalid-enum" => """{"status":123.5}""",
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown body kind"),
            };
        }

        if (url.EndsWith("/review-status", StringComparison.Ordinal))
        {
            return kind switch
            {
                "malformed-json" => "{ this is not json",
                "wrong-json-type" => """{"reviewStatus":123}""",
                "invalid-enum" => """{"reviewStatus":123.5}""",
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown body kind"),
            };
        }

        if (url.EndsWith("/release", StringComparison.Ordinal))
        {
            return kind switch
            {
                "malformed-json" => "{ this is not json",
                "wrong-json-type" => """{"releaseId":["an-array"]}""",
                "invalid-enum" => """{"releaseId":12.5}""",
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown body kind"),
            };
        }

        if (url.EndsWith("/extend-iterations", StringComparison.Ordinal))
        {
            return kind switch
            {
                "malformed-json" => "{ this is not json",
                "wrong-json-type" => """{"additionalIterations":"many"}""",
                "invalid-enum" => """{"additionalIterations":1.5}""",
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown body kind"),
            };
        }

        throw new ArgumentOutOfRangeException(nameof(url), url, "unknown route");
    }

    private static string RoleId(string role) => role == "member" ? MemberId : OperatorId;

    // ══════════════════════════════════════════════════════════════════════════
    //  1. The authorized caller's behaviour is PRESERVED
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE AUTHORIZED CALLER'S BEHAVIOUR IS PRESERVED: for EVERY covered mutating route and EVERY
    /// body, the authorized caller still receives the EXISTING answer — a 400 with an EMPTY body for
    /// a binding-failing body, and the route's own pinned answer for a no-body route — so the
    /// pre-binding fix provably did not change what an authorized caller sees.
    /// <para>
    /// This is the pinning row: it makes the 400 (and its empty body) a CONTRACT, so a fix that
    /// turned the authorized caller's 400 into a 403 would fail here.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(PreBindingMutations))]
    public async Task AuthorizedCaller_KeepsTheExistingAnswer(PreBindingCase testCase)
    {
        var method = testCase.Method;
        var url = testCase.Url;
        var bodyKind = testCase.BodyKind;
        var requiredRole = testCase.RequiredRole;
        var authorizedStatus = testCase.AuthorizedStatus;
        var authorizedBodyEmpty = testCase.AuthorizedBodyEmpty;
        await SeedAccountAsync(MemberId, UserRole.Member);
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedGoalsAsync();
        await SeedIssueAsync();

        using var client = NewClient();
        var body = BodyFor(bodyKind, url);

        using var response = await client.SendAsync(
            Request(new HttpMethod(method), url, RoleId(requiredRole), body), Ct);

        Assert.Equal(authorizedStatus, (int)response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);

        var text = await response.Content.ReadAsStringAsync(Ct);
        if (authorizedBodyEmpty)
            Assert.Equal("", text);
        else
            Assert.NotEqual("", text);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  2. The anonymous caller
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE ANONYMOUS CALLER GETS 401 WITH NO REDIRECT on every covered mutating route and every
    /// body: the authorization layer answers BEFORE binding, so the body is never even looked at,
    /// and no OAuth interstitial is produced.
    /// </summary>
    [Theory]
    [MemberData(nameof(PreBindingMutations))]
    public async Task AnonymousCaller_Gets401WithNoRedirect_OnEveryRoute(PreBindingCase testCase)
    {
        var method = testCase.Method;
        var url = testCase.Url;
        var bodyKind = testCase.BodyKind;
        var requiredRole = testCase.RequiredRole;
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedGoalsAsync();
        await SeedIssueAsync();

        using var client = NewClient();
        var body = BodyFor(bodyKind, url);

        // The anonymous caller is unauthorized on EVERY covered route, whatever the route's own
        // required role is — so this row does not depend on it.
        Assert.NotNull(requiredRole);

        using var response = await client.SendAsync(
            Request(new HttpMethod(method), url, gitHubId: null, body), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.False(
            response.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(cookie => cookie.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal)),
            "an API challenge must not start an OAuth handshake for a programmatic caller");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  3. The authenticated-but-insufficient caller
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE AUTHENTICATED-BUT-INSUFFICIENT CALLER GETS 403 — NEVER 400 — on every covered mutating
    /// route and every body, and the 403 carries the sanitized denial text (never a validation
    /// message).
    /// </summary>
    [Theory]
    [MemberData(nameof(PreBindingMutations))]
    public async Task InsufficientCaller_Gets403Never400_OnEveryRoute(PreBindingCase testCase)
    {
        var method = testCase.Method;
        var url = testCase.Url;
        var bodyKind = testCase.BodyKind;
        var requiredRole = testCase.RequiredRole;
        await SeedAccountAsync(ViewerId, UserRole.Viewer);
        await SeedAccountAsync(MemberId, UserRole.Member);
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedGoalsAsync();
        await SeedIssueAsync();

        using var client = NewClient();
        var body = BodyFor(bodyKind, url);

        // The Viewer is insufficient everywhere; the Member is insufficient on the operator
        // routes (and IS the least sufficient role on the member route).
        var insufficient = requiredRole == "operator" ? new[] { ViewerId, MemberId } : [ViewerId];

        foreach (var caller in insufficient)
        {
            using var response = await client.SendAsync(
                Request(new HttpMethod(method), url, caller, body), Ct);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);

            var text = await response.Content.ReadAsStringAsync(Ct);
            Assert.Contains(ForbiddenMessage, text, StringComparison.Ordinal);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  4. The demoted and the disabled caller
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE DEMOTED CALLER GETS 403 — NEVER the authorized caller's answer — on the very next
    /// request: the demotion lands BEFORE binding, so the body never reaches the binder.
    /// </summary>
    [Theory]
    [MemberData(nameof(PreBindingMutations))]
    public async Task DemotedCaller_Gets403_OnTheVeryNextRequest(PreBindingCase testCase)
    {
        var method = testCase.Method;
        var url = testCase.Url;
        var bodyKind = testCase.BodyKind;
        var requiredRole = testCase.RequiredRole;
        var authorizedStatus = testCase.AuthorizedStatus;
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedGoalsAsync();
        await SeedIssueAsync();

        using var client = NewClient();
        var body = BodyFor(bodyKind, url);

        // The demoted caller starts as the route's OWN authorized role, so the route's required
        // role is the role the demotion demotes FROM.
        Assert.NotNull(requiredRole);

        // BEFORE the demotion the Operator is authorized: the route's own answer is produced.
        using (var before = await client.SendAsync(
            Request(new HttpMethod(method), url, OperatorId, body), Ct))
        {
            Assert.Equal(authorizedStatus, (int)before.StatusCode);
        }

        await SetRoleAsync(OperatorId, UserRole.Viewer);

        using var response = await client.SendAsync(
            Request(new HttpMethod(method), url, OperatorId, body), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            ForbiddenMessage,
            await response.Content.ReadAsStringAsync(Ct),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// THE DISABLED CALLER GETS 403 — NEVER the authorized caller's answer — on the very next
    /// request.
    /// </summary>
    [Theory]
    [MemberData(nameof(PreBindingMutations))]
    public async Task DisabledCaller_Gets403_OnTheVeryNextRequest(PreBindingCase testCase)
    {
        var method = testCase.Method;
        var url = testCase.Url;
        var bodyKind = testCase.BodyKind;
        var requiredRole = testCase.RequiredRole;
        var authorizedStatus = testCase.AuthorizedStatus;
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedGoalsAsync();
        await SeedIssueAsync();

        using var client = NewClient();
        var body = BodyFor(bodyKind, url);

        // The disabled caller starts as the route's OWN authorized role.
        Assert.NotNull(requiredRole);

        using (var before = await client.SendAsync(
            Request(new HttpMethod(method), url, OperatorId, body), Ct))
        {
            Assert.Equal(authorizedStatus, (int)before.StatusCode);
        }

        await SetEnabledAsync(OperatorId, enabled: false);

        using var response = await client.SendAsync(
            Request(new HttpMethod(method), url, OperatorId, body), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            ForbiddenMessage,
            await response.Content.ReadAsStringAsync(Ct),
            StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  5. The layered contract: the facade gate is still there
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// THE LAYERED CONTRACT: the facade's OWN gate is still in force, independently of the
    /// pre-binding gate. A caller who passes the pre-binding gate but targets a MISSING resource
    /// still gets the facade's 404, and a caller who passes it but sends a request the facade
    /// validates still gets the facade's own validation answer.
    /// <para>
    /// This row is what proves the pre-binding gate did not REPLACE the facade gate: it was added
    /// IN FRONT of it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheFacadeGateIsStillInForce_BehindThePreBindingGate()
    {
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedIssueAsync();
        using var client = NewClient();

        // The pre-binding gate passes (an Operator) and the FACADE answers NotFound.
        using var missing = await client.SendAsync(
            Request(HttpMethod.Patch, "/api/issues/does-not-exist", OperatorId, """{"severity":"high"}"""), Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // The pre-binding gate passes and the FACADE answers its own BadRequest for a blank title.
        using var blank = await client.SendAsync(
            Request(HttpMethod.Patch, $"/api/issues/{IssueId}", OperatorId, """{"title":"   "}"""), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("Title is required.", await blank.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    public void Dispose() => _factory.Dispose();
}

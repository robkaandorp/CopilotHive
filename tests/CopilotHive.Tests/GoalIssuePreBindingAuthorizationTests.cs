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
/// THE PRE-BINDING AUTHORIZATION CONTRACT of the goal/issue MUTATING routes, over a REAL host with
/// authentication enabled and the REAL <see cref="UserService"/> directory: authorization is answered
/// BEFORE minimal-API model binding / JSON deserialization, so an authenticated caller who is not
/// allowed gets <c>403</c> even for a MALFORMED body (invalid enum, malformed JSON, wrong JSON types),
/// while an AUTHORIZED caller keeps the byte-identical binding/validation behaviour (400 for the same
/// bodies) and an ANONYMOUS caller is still challenged with 401 and no redirect.
/// <para>
/// Every refusal row is paired with the SAME request from an authorized caller in the same test, which
/// proves the 403 is caused by authorization and not by a gate that refuses everything (and proves the
/// malformed body really WOULD have produced a 400 had binding run first).
/// </para>
/// </summary>
[Collection("HiveIntegration")]
public sealed class GoalIssuePreBindingAuthorizationTests : IDisposable
{
    private const string IdentityHeader = "X-Test-GitHub-Id";
    private const string ViewerId = "pb-viewer";
    private const string MemberId = "pb-member";
    private const string OperatorId = "pb-operator";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class AuthApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _stateDir =
            Path.Combine(Path.GetTempPath(), $"copilothive-prebinding-{Guid.NewGuid():N}");
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
            try { Directory.Delete(_stateDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class TestIdentityHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SchemeName = "TestIdentityPreBinding";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(IdentityHeader, out var values)
                || string.IsNullOrWhiteSpace(values.ToString()))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, values.ToString())], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    private readonly AuthApiFactory _factory = new();

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });

    private static HttpRequestMessage Request(HttpMethod method, string url, string? gitHubId, string? rawBody)
    {
        var request = new HttpRequestMessage(method, url);
        if (gitHubId is not null)
            request.Headers.Add(IdentityHeader, gitHubId);
        if (rawBody is not null)
            request.Content = new StringContent(rawBody, Encoding.UTF8, "application/json");
        return request;
    }

    private async Task WithDbAsync(Func<CopilotHiveDbContext, Task> action)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CopilotHiveDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Ct);
        await action(db);
        await db.SaveChangesAsync(Ct);
    }

    private Task SeedAccountAsync(string gitHubId, UserRole role) => WithDbAsync(db =>
    {
        db.Users.Add(new UserEntity
        {
            GitHubId = gitHubId,
            Username = gitHubId,
            AccessToken = "token-" + gitHubId,
            Role = role,
            Enabled = true,
            CreatedAt = "2024-01-01T00:00:00.0000000Z",
        });
        return Task.CompletedTask;
    });

    private Task SetRoleAsync(string gitHubId, UserRole role) => WithDbAsync(async db =>
        (await db.Users.SingleAsync(u => u.GitHubId == gitHubId, Ct)).Role = role);

    private Task SetEnabledAsync(string gitHubId, bool enabled) => WithDbAsync(async db =>
        (await db.Users.SingleAsync(u => u.GitHubId == gitHubId, Ct)).Enabled = enabled);

    private async Task SeedGoalAsync(string id)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
        await store.CreateGoalAsync(new Goal { Id = id, Description = "Pre-binding goal", Status = GoalStatus.Draft }, Ct);
    }

    private async Task SeedIssueAsync(string id)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IIssueStore>();
        await store.CreateIssueAsync(new Issue
        {
            Id = id,
            Title = "Pre-binding issue",
            Description = "d",
            Type = IssueType.Bug,
        }, Ct);
    }

    /// <summary>
    /// EVERY covered mutating route paired with a body that FAILS binding (invalid enum, malformed
    /// JSON, wrong JSON type, wrong shape) and the least role that may call it.
    /// </summary>
    public static TheoryData<string, string, string, string> MalformedMutations => new()
    {
        { "PATCH", "/api/issues/pb-issue", """{"severity":"invalid"}""", "operator" },
        { "PATCH", "/api/issues/pb-issue", """{"type":"nope","status":"nope"}""", "operator" },
        { "PATCH", "/api/issues/pb-issue", "{ this is not json", "operator" },
        { "PATCH", "/api/issues/pb-issue", """{"repositoryNames":"not-a-list"}""", "operator" },
        { "POST", "/api/issues", """{"type":"nope","title":"t","description":"d"}""", "member" },
        { "POST", "/api/issues", "{ this is not json", "member" },
        { "POST", "/api/issues", """{"title":123}""", "member" },
        { "DELETE", "/api/issues/pb-issue", null!, "operator" },
        { "PATCH", "/api/goals/pb-goal/status", "{ this is not json", "operator" },
        { "PATCH", "/api/goals/pb-goal/status", """{"status":123}""", "operator" },
        { "PATCH", "/api/goals/pb-goal/review-status", "{ this is not json", "operator" },
        { "PATCH", "/api/goals/pb-goal/release", """{"releaseId":["x"]}""", "operator" },
        { "POST", "/api/goals/pb-goal/extend-iterations", """{"additionalIterations":"many"}""", "operator" },
        { "POST", "/api/goals/pb-goal/cancel", null!, "operator" },
        { "POST", "/api/goals/pb-goal/review", null!, "operator" },
        { "DELETE", "/api/goals/pb-goal", null!, "operator" },
    };

    private static string RoleId(string role) => role == "member" ? MemberId : OperatorId;

    /// <summary>
    /// THE CORE CONTRACT: for every covered mutating route, an authenticated Viewer — and, for the
    /// operator routes, a Member — gets 403 with the sanitized body for a MALFORMED request, while the
    /// authorized caller sending the SAME request reaches binding (400 for the malformed bodies).
    /// </summary>
    [Theory]
    [MemberData(nameof(MalformedMutations))]
    public async Task InsufficientCaller_Gets403_BeforeBinding_AndAuthorizedCallerKeepsTheBindingAnswer(
        string method, string url, string? body, string requiredRole)
    {
        await SeedAccountAsync(ViewerId, UserRole.Viewer);
        await SeedAccountAsync(MemberId, UserRole.Member);
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedGoalAsync("pb-goal");
        await SeedIssueAsync("pb-issue");

        using var client = NewClient();
        var verb = new HttpMethod(method);

        var insufficient = requiredRole == "operator" ? new[] { ViewerId, MemberId } : [ViewerId];
        foreach (var caller in insufficient)
        {
            using var response = await client.SendAsync(Request(verb, url, caller, body), Ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains(
                "The acting account is not authorized to perform this operation.",
                await response.Content.ReadAsStringAsync(Ct));
        }

        // THE CONTROL: the very same request from an AUTHORIZED caller is NOT a 403 — it reaches the
        // unchanged binding/handler. Bodies that are malformed answer the existing 400.
        using var authorized = await client.SendAsync(Request(verb, url, RoleId(requiredRole), body), Ct);
        Assert.NotEqual(HttpStatusCode.Forbidden, authorized.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, authorized.StatusCode);
        if (body is not null && (body.StartsWith("{ this", StringComparison.Ordinal) || body.Contains("nope")
            || body.Contains("invalid") || body.Contains("not-a-list") || body.Contains("123")
            || body.Contains("many") || body.Contains("[\"x\"]")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, authorized.StatusCode);
        }
    }

    /// <summary>
    /// THE EXACT FINDING: a Viewer PATCHing <c>/api/issues/x</c> with <c>{"severity":"invalid"}</c> is
    /// 403, while an Operator sending that body still gets the existing 400.
    /// </summary>
    [Fact]
    public async Task Viewer_PatchingAnIssueWithAnInvalidEnum_Is403_WhileOperatorStill400()
    {
        await SeedAccountAsync(ViewerId, UserRole.Viewer);
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedIssueAsync("pb-issue");
        using var client = NewClient();

        using var viewer = await client.SendAsync(
            Request(HttpMethod.Patch, "/api/issues/pb-issue", ViewerId, """{"severity":"invalid"}"""), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);

        using var op = await client.SendAsync(
            Request(HttpMethod.Patch, "/api/issues/pb-issue", OperatorId, """{"severity":"invalid"}"""), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, op.StatusCode);
    }

    /// <summary>
    /// FRESHNESS APPLIES TO THE PRE-BINDING CHECK: a demotion or disablement bites on the VERY NEXT
    /// request — nothing from the previous request's gate is reused.
    /// </summary>
    [Fact]
    public async Task DemotionAndDisablement_BiteOnTheNextRequest_AtThePreBindingGate()
    {
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedIssueAsync("pb-issue");
        using var client = NewClient();
        const string malformed = """{"severity":"invalid"}""";

        using (var before = await client.SendAsync(
            Request(HttpMethod.Patch, "/api/issues/pb-issue", OperatorId, malformed), Ct))
            Assert.Equal(HttpStatusCode.BadRequest, before.StatusCode);

        await SetRoleAsync(OperatorId, UserRole.Viewer);
        using (var demoted = await client.SendAsync(
            Request(HttpMethod.Patch, "/api/issues/pb-issue", OperatorId, malformed), Ct))
            Assert.Equal(HttpStatusCode.Forbidden, demoted.StatusCode);

        await SetRoleAsync(OperatorId, UserRole.Operator);
        using (var restored = await client.SendAsync(
            Request(HttpMethod.Patch, "/api/issues/pb-issue", OperatorId, malformed), Ct))
            Assert.Equal(HttpStatusCode.BadRequest, restored.StatusCode);

        await SetEnabledAsync(OperatorId, false);
        using (var disabled = await client.SendAsync(
            Request(HttpMethod.Patch, "/api/issues/pb-issue", OperatorId, malformed), Ct))
            Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
    }

    /// <summary>
    /// An UNKNOWN authenticated identity (no activated account) is refused 403 pre-binding, and an
    /// ANONYMOUS caller keeps the 401-no-redirect answer — the pre-binding gate never turns an
    /// anonymous challenge into a 403 nor leaks a redirect.
    /// </summary>
    [Fact]
    public async Task UnknownIdentity_Is403_AndAnonymousStays401WithNoRedirect()
    {
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedIssueAsync("pb-issue");
        using var client = NewClient();
        const string malformed = "{ this is not json";

        using var unknown = await client.SendAsync(
            Request(HttpMethod.Patch, "/api/issues/pb-issue", "never-invited", malformed), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);

        using var anonymous = await client.SendAsync(
            Request(HttpMethod.Patch, "/api/issues/pb-issue", gitHubId: null, malformed), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null(anonymous.Headers.Location);
    }

    /// <summary>
    /// SCOPE: only the MUTATING routes are pre-gated. A Viewer's GET is not refused (SharedRead), and
    /// GOAL CREATION (<c>POST /api/goals</c>) is NOT pre-gated — it keeps its own binding/validation
    /// answer (400 for malformed JSON) for any authenticated caller, because it is a separate goal.
    /// </summary>
    [Fact]
    public async Task OnlyCoveredMutatingRoutesArePreGated_ReadsAndGoalCreationAreUntouched()
    {
        await SeedAccountAsync(ViewerId, UserRole.Viewer);
        await SeedIssueAsync("pb-issue");
        using var client = NewClient();

        using var read = await client.SendAsync(Request(HttpMethod.Get, "/api/issues/pb-issue", ViewerId, null), Ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var create = await client.SendAsync(
            Request(HttpMethod.Post, "/api/goals", ViewerId, "{ this is not json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    /// <summary>
    /// AUTHORIZED-CALLER PRESERVATION: an Operator/Member sending VALID requests succeeds with the
    /// existing status codes, and a missing resource is still a 404 — the pre-binding gate changed
    /// nothing for a caller who passes it.
    /// </summary>
    [Fact]
    public async Task AuthorizedCallers_KeepSuccessAnd404Behaviour()
    {
        await SeedAccountAsync(MemberId, UserRole.Member);
        await SeedAccountAsync(OperatorId, UserRole.Operator);
        await SeedIssueAsync("pb-issue");
        using var client = NewClient();

        using var created = await client.SendAsync(Request(HttpMethod.Post, "/api/issues", MemberId,
            """{"type":"bug","title":"From member","description":"d"}"""), Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var patched = await client.SendAsync(Request(HttpMethod.Patch, "/api/issues/pb-issue", OperatorId,
            """{"severity":"high"}"""), Ct);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        using var missing = await client.SendAsync(Request(HttpMethod.Patch, "/api/issues/does-not-exist", OperatorId,
            """{"severity":"high"}"""), Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var deleted = await client.SendAsync(Request(HttpMethod.Delete, "/api/issues/pb-issue", OperatorId, null), Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    public void Dispose() => _factory.Dispose();
}

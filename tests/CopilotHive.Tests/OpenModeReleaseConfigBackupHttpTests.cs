using System.Net;
using System.Text;
using System.Text.Json;

using CopilotHive.Goals;
using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// OPEN MODE IS UNCHANGED for the release / configuration / backup / clarification surfaces: with
/// no OAuth configured the host resolves the EXPLICIT open-mode local operator, which holds every
/// capability, so every covered route still runs exactly as it did before the authorization slice.
/// <para>
/// THE CONTROL: the resolver really yields the local operator in this host (and not an
/// authentication-enabled one), so the rows below cannot be satisfied by a host that simply
/// answers everything.
/// </para>
/// </summary>
[Collection("HiveIntegration")]
public sealed class OpenModeReleaseConfigBackupHttpTests : IDisposable
{
    /// <summary>
    /// The OPEN-MODE host: BOTH OAuth env vars are unset, so the resolver yields the legacy local
    /// operator for every caller and the authorization gate admits it.
    /// </summary>
    private sealed class OpenModeFactory : WebApplicationFactory<Program>
    {
        private readonly string _stateDir =
            Path.Combine(Path.GetTempPath(), $"copilothive-rcb-open-mode-{Guid.NewGuid():N}");
        private readonly string? _previousStateDir;
        private readonly string? _previousClientId;
        private readonly string? _previousClientSecret;

        internal OpenModeFactory()
        {
            _previousStateDir = Environment.GetEnvironmentVariable("STATE_DIR");
            _previousClientId = Environment.GetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID");
            _previousClientSecret = Environment.GetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET");

            Environment.SetEnvironmentVariable("STATE_DIR", _stateDir);
            // Open mode: BOTH OAuth vars unset — the explicit host decision the resolver reads.
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID", null);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET", null);
            Directory.CreateDirectory(_stateDir);
        }

        /// <inheritdoc />
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
                services.ReplaceDistributedBrain(new NoOpDistributedBrain()));
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            Environment.SetEnvironmentVariable("STATE_DIR", _previousStateDir);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_ID", _previousClientId);
            Environment.SetEnvironmentVariable("GITHUB_OAUTH_CLIENT_SECRET", _previousClientSecret);

            if (!disposing || !Directory.Exists(_stateDir))
                return;

            try { Directory.Delete(_stateDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private readonly OpenModeFactory _factory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// THE HOST REALLY IS IN OPEN MODE: the registered auth mode reports authentication disabled
    /// and the resolver answers an anonymous caller with the legacy local operator. Without this
    /// control the rows below could be satisfied by an authentication-enabled host that happened
    /// to answer the same way.
    /// </summary>
    [Fact]
    public async Task TheHost_IsInOpenMode_AndTheResolverYieldsTheLocalOperator()
    {
        using var client = _factory.CreateClient();

        var mode = _factory.Services.GetRequiredService<HiveAuthMode>();
        Assert.False(mode.AuthEnabled, "the host must really be in open mode");

        var resolver = _factory.Services.GetRequiredService<HivePrincipalResolver>();
        var resolution = await resolver.ResolveAsync(null, Ct);

        Assert.True(resolution.IsResolved);
        var principal = Assert.IsType<ActingUserPrincipal>(resolution.Principal);
        Assert.True(principal.IsLocalOperator);
    }

    /// <summary>
    /// THE RELEASE SURFACE IS UNCHANGED IN OPEN MODE: an unauthenticated caller is NOT challenged
    /// (open mode has no fallback policy), and the release reads and mutations really run — the
    /// local operator holds every capability, so the authorization gate admits it.
    /// </summary>
    [Fact]
    public async Task OpenMode_ReleaseSurface_RunsWithoutAChallenge()
    {
        using var client = _factory.CreateClient();

        var releaseId = $"open-rel-{Guid.NewGuid():N}";
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateReleaseAsync(new Release { Id = releaseId, Tag = "v1.0.0" }, Ct);
        }

        // The read is served — not challenged, not refused.
        using (var response = await client.GetAsync($"/api/releases/{releaseId}", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // The goals read is served.
        using (var response = await client.GetAsync($"/api/releases/{releaseId}/goals", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // The mutation is served.
        using (var response = await client.PatchAsync(
                   $"/api/releases/{releaseId}/notes",
                   new StringContent("""{"notes":"open mode"}""", Encoding.UTF8, "application/json"),
                   Ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // The create is served.
        var version = $"open-v-{Guid.NewGuid():N}";
        using (var response = await client.PostAsync(
                   "/api/releases/",
                   new StringContent($$"""{"version":"{{version}}"}""", Encoding.UTF8, "application/json"),
                   Ct))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    /// <summary>
    /// THE CONFIGURATION AND BACKUP SURFACES ARE UNCHANGED IN OPEN MODE: the local operator is the
    /// installation's administrator, so the global configuration read and the backup list are
    /// served — never a 403.
    /// </summary>
    [Fact]
    public async Task OpenMode_ConfigAndBackupSurfaces_AreServedNotRefused()
    {
        using var client = _factory.CreateClient();

        foreach (var url in new[]
        {
            "/api/config/models",
            "/api/config/repositories",
            "/api/config/orchestrator",
            "/api/config/workers",
            "/api/config/composer",
        })
        {
            using var response = await client.GetAsync(url, Ct);
            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var response = await client.GetAsync("/api/backup/", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        }
    }

    /// <summary>
    /// THE CLARIFICATION SURFACE IS UNCHANGED IN OPEN MODE: the reads are served and the answer
    /// really resolves the request — the local operator holds the execution-operator capability.
    /// </summary>
    [Fact]
    public async Task OpenMode_ClarificationSurface_IsServedNotRefused()
    {
        using var client = _factory.CreateClient();

        var clarificationId = $"open-clar-{Guid.NewGuid():N}";
        var queue = _factory.Services.GetRequiredService<CopilotHive.Orchestration.ClarificationQueueService>();
        queue.Enqueue(new CopilotHive.Orchestration.ClarificationRequest
        {
            Id = clarificationId,
            GoalId = "open-goal",
            WorkerRole = "coder",
            Question = "Which branch?",
        });

        using (var response = await client.GetAsync("/api/clarifications/", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var response = await client.GetAsync("/api/clarifications/count", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var response = await client.PostAsync(
                   $"/api/clarifications/{clarificationId}/answer",
                   new StringContent("""{"answer":"main"}""", Encoding.UTF8, "application/json"),
                   Ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal("main", queue.GetRequest(clarificationId)!.Answer);
    }

    public void Dispose() => _factory.Dispose();
}

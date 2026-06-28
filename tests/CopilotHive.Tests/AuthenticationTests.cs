using System.Net;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests verifying the application boots and serves the auth-related endpoints
/// correctly when OAuth is NOT configured (the default test environment). This proves the
/// backward-compatible "open mode" behaviour: health endpoints are reachable without auth,
/// the login page renders, and logout still redirects.
/// </summary>
[Collection("HiveIntegration")]
public class AuthenticationTests
{
    private readonly HiveTestFactory _factory;

    /// <summary>Receives the shared factory fixture for this test class.</summary>
    /// <param name="factory">The shared <see cref="HiveTestFactory"/> for this test class.</param>
    public AuthenticationTests(HiveTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HealthEndpoint_Returns200_WithoutAuth()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LoginEndpoint_Returns200()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/login", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Sign in with GitHub", body);
    }

    [Fact]
    public async Task LogoutEndpoint_ReturnsRedirect_WhenAuthDisabled()
    {
        using var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.PostAsync("/logout", content: null, TestContext.Current.CancellationToken);

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther,
            $"Expected a redirect status, got {(int)response.StatusCode} {response.StatusCode}.");
        Assert.Equal("/login", response.Headers.Location?.OriginalString);
    }
}

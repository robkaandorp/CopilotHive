using Microsoft.AspNetCore.Authentication;

namespace CopilotHive.Security;

/// <summary>
/// THE PATH-AWARE CHALLENGE HANDLER: one scheme that answers an unauthenticated request with the
/// response shape its CLIENT can act on.
/// <para>
/// An <c>/api/*</c> request is a programmatic API call: it is challenged with a bare
/// <c>401 Unauthorized</c> and NO <c>Location</c> header, so a JSON client never receives an HTML
/// login page or a redirect it would follow into an OAuth handshake. Every other request is a
/// browser/page navigation and keeps the EXISTING GitHub OAuth challenge, so the <c>/login</c> page,
/// the <c>/signin-github</c> callback, the correlation cookie and the open-mode behaviour are
/// unchanged.
/// </para>
/// <para>
/// This type is registered as the host's <c>DefaultChallengeScheme</c> ONLY when authentication is
/// enabled; it performs no identity work of its own and never grants access. The authenticated
/// scheme stays the cookie scheme, so a signed-in caller is untouched by this handler.
/// </para>
/// </summary>
public sealed class HiveChallengeHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The route-prefix that identifies a programmatic API request.</summary>
    public const string ApiPathPrefix = "/api";

    /// <summary>The DI scheme name of this path-aware challenge handler.</summary>
    public const string SchemeName = "HiveChallenge";

    /// <summary>
    /// Initialises a new <see cref="HiveChallengeHandler"/>.
    /// </summary>
    /// <param name="options">The scheme options.</param>
    /// <param name="logger">The logger factory.</param>
    /// <param name="encoder">The URL encoder.</param>
    public HiveChallengeHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <summary>
    /// This scheme never authenticates anything: it only shapes the challenge response, so the
    /// cookie scheme remains the single authentication authority.
    /// </summary>
    /// <returns>Always <see cref="AuthenticateResult.NoResult"/>.</returns>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());

    /// <summary>
    /// Answers an API request with <c>401</c> and no redirect; hands every browser/page request to
    /// the existing GitHub challenge.
    /// </summary>
    /// <param name="properties">The challenge properties produced by the authorization layer.</param>
    /// <returns>A task that completes when the challenge has been written.</returns>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var path = Request.Path;

        if (path.StartsWithSegments(ApiPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // No Location header and no correlation cookie: a programmatic caller must see the
            // authorization outcome, never an OAuth interstitial.
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Browser/page navigation: the pre-existing GitHub OAuth challenge, byte-identical.
        await Context.ChallengeAsync(GitHubChallengeScheme, properties);
    }

    /// <summary>The scheme name of the GitHub OAuth challenge kept for browser requests.</summary>
    public const string GitHubChallengeScheme = "GitHub";
}

namespace CopilotHive.Services;

/// <summary>
/// Claim types produced for the GitHub OAuth login that
/// <c>AspNet.Security.OAuth.GitHub</c> does not emit by itself.
/// </summary>
public static class GitHubClaimTypes
{
    /// <summary>
    /// The avatar claim type. <c>AspNet.Security.OAuth.GitHub</c> maps only id/login/email/name/url,
    /// so this claim is produced in <c>Program.cs</c> by mapping GitHub's <c>avatar_url</c> JSON key
    /// onto it via a claim action.
    /// </summary>
    public const string Avatar = "urn:github:avatar";
}

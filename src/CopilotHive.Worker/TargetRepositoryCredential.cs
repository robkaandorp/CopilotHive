using CopilotHive.Services;

namespace CopilotHive.Worker;

/// <summary>
/// Splits a target-repository URL into its credential-free form and the credential that must travel
/// through <see cref="WorkerGitCredentialTransport"/> instead of the URL.
/// </summary>
/// <remarks>
/// <para>
/// <b>The handoff keeps its shape; the transport does not.</b> The orchestrator may still put the
/// credential into <c>TargetRepository.Url</c> (the wire/in-memory assignment contract is unchanged),
/// so the worker's FIRST act on every repository is this split — BEFORE any process launch. Nothing
/// downstream of it ever sees a credential-bearing URL: git receives the sanitized URL on its argv
/// and the credential only in a child-only environment variable.
/// </para>
/// <para>
/// <b>The destination contract.</b> A credential is released ONLY when the SANITIZED URL is an
/// effective HTTPS <c>github.com:443</c> destination (the config seam's eligibility rule, including
/// an explicit <c>:443</c>). A credential-bearing URL for any other host, scheme or port is still
/// SANITIZED — the credential is removed from the URL and WITHHELD
/// (<c>Withheld</c>), so the operation runs unauthenticated and fails with a sanitized error
/// instead of sending a token to a destination that was never proven eligible. Local paths, file
/// URLs and non-credential HTTP(S) URLs pass through untouched.
/// </para>
/// <para>
/// Only HTTP(S) <b>userinfo</b> is treated as the credential. A <c>?token=</c> query parameter is
/// removed from the sanitized URL by <see cref="GitUrlRedactor"/> but is NOT promoted into the
/// child-only transport: such a URL must be reconfigured without the query secret.
/// </para>
/// </remarks>
internal static class TargetRepositoryCredential
{
    /// <summary>The separator between a username and a password inside userinfo.</summary>
    private const char UserInfoSeparator = ':';

    /// <summary>
    /// Splits <paramref name="url"/>.
    /// </summary>
    /// <param name="url">The assignment URL, which may carry a credential in its userinfo.</param>
    /// <returns>
    /// <c>SanitizedUrl</c> — ALWAYS credential-free, and the ONLY URL that may reach a git argument
    /// list; <c>Credential</c> — the resolved secret to release through the askpass helper, or
    /// <c>null</c>; <c>Withheld</c> — <c>true</c> when a credential was present but refused because
    /// the destination is not eligible.
    /// </returns>
    internal static (string SanitizedUrl, string? Credential, bool Withheld) Split(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return (url ?? string.Empty, null, false);

        var sanitized = GitUrlRedactor.Redact(url);
        if (string.Equals(sanitized, url, StringComparison.Ordinal))
            return (url, null, false);

        var credential = ExtractUserInfoCredential(url);
        if (credential is null)
            return (sanitized, null, false);

        // DESTINATION GATE — the credential is released only to the eligible destination. The
        // sanitized URL is what eligibility is computed from, exactly as in the config seam.
        return ConfigRepoGitOperations.IsEligibleTransportUrl(sanitized)
            ? (sanitized, credential, false)
            : (sanitized, null, true);
    }

    /// <summary>
    /// Extracts the password part of the URL's userinfo (the whole userinfo when it carries no
    /// separator), unescaped. Returns <c>null</c> when the URL is not an absolute HTTP(S) URL, has no
    /// userinfo DELIMITER, or the extracted value is blank.
    /// </summary>
    private static string? ExtractUserInfoCredential(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Presence is decided by the DELIMITER in the authority (Uri.UserInfo reports "" for both
        // "no userinfo" and syntactically present but empty userinfo).
        string authority;
        try
        {
            authority = uri.GetComponents(
                UriComponents.UserInfo | UriComponents.HostAndPort, UriFormat.UriEscaped);
        }
        catch
        {
            // Unparseable authority — treat as credential-bearing but unextractable: the sanitized
            // URL is still used, so no secret can reach an argument list.
            return null;
        }

        if (!authority.Contains('@'))
            return null;

        var userInfo = uri.UserInfo;
        var separator = userInfo.IndexOf(UserInfoSeparator);
        var secret = separator >= 0 ? userInfo[(separator + 1)..] : userInfo;

        string unescaped;
        try
        {
            unescaped = Uri.UnescapeDataString(secret);
        }
        catch
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(unescaped) ? null : unescaped;
    }
}

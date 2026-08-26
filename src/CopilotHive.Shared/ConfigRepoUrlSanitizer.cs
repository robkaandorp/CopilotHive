using System.Text.RegularExpressions;

namespace CopilotHive.Configuration;

/// <summary>
/// Sanitizes the operator-supplied <c>--config-repo=&lt;value&gt;</c> command-line argument.
/// <para>
/// The raw operator value can carry a credential (e.g. <c>https://ghp_token@github.com/org/repo.git</c>).
/// Such a value must NEVER reach a log, an exception message, the web application builder's
/// argument array, or the config repository manager. Every rejection therefore reports a
/// REDACTED reason only — the raw input is never echoed.
/// </para>
/// <para>
/// This type is distinct from the config repository manager's internal token-injection path,
/// which builds credential-bearing clone URLs from <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c> and is
/// deliberately untouched by this sanitizer.
/// </para>
/// </summary>
public static class ConfigRepoUrlSanitizer
{
    /// <summary>The exact command-line prefix recognised for the config repo argument.</summary>
    internal const string ArgPrefix = "--config-repo=";

    /// <summary>The bare (value-less) form of the config repo argument — an unrecognized form.</summary>
    private const string BareArg = "--config-repo";

    /// <summary>The only host accepted for network (https/ssh) config repo URLs.</summary>
    private const string AllowedHost = "github.com";

    /// <summary>The only SSH username accepted.</summary>
    private const string AllowedSshUser = "git";

    /// <summary>
    /// Matches an scp-style git remote (<c>user@host:path</c>). Deliberately requires an
    /// <c>@</c> before the first <c>/</c> or <c>:</c>, so Windows absolute paths
    /// (<c>C:\foo</c>) and Unix absolute paths (<c>/abs/path</c>) never match.
    /// </summary>
    private static readonly Regex ScpStyle = new(@"^[^\s/@]+@[^\s/:]+:.+$", RegexOptions.Compiled);

    /// <summary>
    /// Sanitizes a raw <c>--config-repo</c> value.
    /// </summary>
    /// <param name="raw">The raw operator-supplied value. May be null or empty.</param>
    /// <returns>
    /// <c>null</c> when the value is ABSENT (null, empty or whitespace-only); otherwise the
    /// canonical sanitized value.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The value is present but not acceptable. The message states a redacted reason and NEVER
    /// contains any part of <paramref name="raw"/>.
    /// </exception>
    public static string? Sanitize(string? raw)
    {
        // Redaction boundary: every exception that escapes this method is a RejectedException
        // whose message is reason-only. Framework exceptions (Path.GetFullPath, Uri component
        // access, regex) embed the raw input in their message, so anything that is not already
        // a redacted rejection is replaced wholesale — the original is NOT chained as an inner
        // exception, because its message would carry the raw value.
        try
        {
            return SanitizeCore(raw);
        }
        catch (RejectedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            throw Reject("value could not be processed");
        }
    }

    private static string? SanitizeCore(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var value = raw.Trim();

        // Rule 1: scp-style (user@host:org/repo.git) normalizes FIRST to an ssh:// URL.
        if (!HasUriScheme(value) && ScpStyle.IsMatch(value))
            value = NormalizeScpStyle(value);

        // A value without a URI scheme is a plain local filesystem path. (Note: on Unix,
        // Uri.TryCreate happily parses "/abs/path" — and on Windows "C:\dir" — as a file:
        // URI, so the scheme check, not Uri.TryCreate, decides which branch applies.)
        if (!HasUriScheme(value))
            return SanitizeLocalPath(value);

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw Reject("value is not a valid absolute URL");

        var scheme = uri.Scheme.ToLowerInvariant();

        // Rule 2: scheme must be exactly https, ssh or file.
        return scheme switch
        {
            "https" => SanitizeHttps(uri),
            "ssh" => SanitizeSsh(uri),
            "file" => SanitizeFileUri(uri),
            _ => throw Reject("unsupported scheme (only https, ssh and file are allowed)"),
        };
    }

    /// <summary>
    /// Scans the process command-line arguments for the config repo argument and returns a
    /// sanitized copy of the argument array plus the sanitized value.
    /// <para>
    /// Strict handling: more than one <c>--config-repo=</c> argument is rejected; any argument
    /// that LOOKS like a config repo argument but is not the exact <c>--config-repo=</c> equals
    /// form (e.g. the bare <c>--config-repo</c>, or a different casing such as
    /// <c>--Config-Repo=x</c>) is an unrecognized form and is rejected.
    /// </para>
    /// </summary>
    /// <param name="args">Raw command-line arguments.</param>
    /// <returns>
    /// The sanitized value (<c>null</c> when absent or empty) and an args array in which the
    /// config repo argument is either replaced by its sanitized form or removed entirely.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The arguments contain a duplicate, unrecognized, or unacceptable config repo argument.
    /// The message never echoes any raw value.
    /// </exception>
    public static (string? Value, string[] SanitizedArgs) SanitizeArgs(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // Same redaction boundary as Sanitize: nothing but a redacted RejectedException escapes.
        try
        {
            return SanitizeArgsCore(args);
        }
        catch (RejectedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            throw Reject("arguments could not be processed");
        }
    }

    private static (string? Value, string[] SanitizedArgs) SanitizeArgsCore(string[] args)
    {
        var matchIndexes = new List<int>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is null)
                continue;

            if (arg.StartsWith(ArgPrefix, StringComparison.Ordinal))
            {
                matchIndexes.Add(i);
                continue;
            }

            // Anything that looks like the config repo argument but is not the exact
            // "--config-repo=" equals form is an unrecognized form. Distinct arguments that
            // merely share the prefix (e.g. "--config-repo-path=") are left untouched.
            if (arg.Equals(BareArg, StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith(ArgPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw Reject(
                    $"unrecognized config repo argument form (expected exactly '{ArgPrefix}<value>')");
            }
        }

        if (matchIndexes.Count > 1)
            throw Reject($"'{ArgPrefix}' specified more than once (expected at most one)");

        if (matchIndexes.Count == 0)
            return (null, args);

        var index = matchIndexes[0];
        var rawValue = args[index][ArgPrefix.Length..];
        var sanitized = Sanitize(rawValue);

        // Build a fresh args array so the raw value never reaches downstream consumers.
        var result = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (i != index)
            {
                result.Add(args[i]);
                continue;
            }

            // An empty value is ABSENT: drop the argument entirely.
            if (sanitized is not null)
                result.Add(ArgPrefix + sanitized);
        }

        return (sanitized, result.ToArray());
    }

    // ── Per-scheme rules ──────────────────────────────────────────────────────

    private static string SanitizeHttps(Uri uri)
    {
        // Rule 3: ANY userinfo on an HTTPS URL is rejected — this is the operator-supplied
        // credential case (e.g. https://<token>@github.com/org/repo.git).
        //
        // Presence is decided by the userinfo DELIMITER in the authority, not by a non-empty
        // Uri.UserInfo: "https://@github.com/org/repo.git" has syntactically present but EMPTY
        // userinfo, which Uri.UserInfo reports as "" and the rebuild would silently drop.
        if (HasUserInfoDelimiter(uri))
            throw Reject("https URL carries userinfo credentials, which are not allowed");

        RequireAllowedHost(uri);
        RejectQueryOrFragment(uri);

        return Rebuild(uri, userInfo: null);
    }

    private static string SanitizeSsh(Uri uri)
    {
        // Rule 4: username must be exactly "git"; a password is stripped.
        //
        // A syntactically present but empty (or username-less) userinfo — "ssh://@host/..." and
        // "ssh://:pw@host/..." — must be REJECTED, never silently normalized to "git@".
        if (!HasUserInfoDelimiter(uri))
            throw Reject($"ssh URL must use the '{AllowedSshUser}' username");

        var userInfo = uri.UserInfo;
        var separator = userInfo.IndexOf(':');
        var user = separator >= 0 ? userInfo[..separator] : userInfo;
        if (!string.Equals(user, AllowedSshUser, StringComparison.Ordinal))
            throw Reject($"ssh URL username must be exactly '{AllowedSshUser}'");

        RequireAllowedHost(uri);
        RejectQueryOrFragment(uri);

        return Rebuild(uri, AllowedSshUser);
    }

    private static string SanitizeFileUri(Uri uri)
    {
        // Rule 7: only an empty authority (file:///abs/path) is accepted.
        if (!string.IsNullOrEmpty(uri.Host))
            throw Reject("file URL must not specify a host authority");

        if (HasUserInfoDelimiter(uri))
            throw Reject("file URL must not carry userinfo");

        string localPath;
        try
        {
            localPath = uri.LocalPath;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // The framework message would embed the raw input — replace it wholesale.
            throw Reject("file URL path could not be resolved");
        }

        if (string.IsNullOrWhiteSpace(localPath) || localPath == "/")
            throw Reject("file URL has an empty path");

        // A rootless 'file:' URI must NEVER be canonicalized into a process-relative absolute
        // path (Path.GetFullPath would resolve it against the current directory and accept it).
        if (!Path.IsPathRooted(localPath))
            throw Reject("file URL path is not absolute (relative paths are not allowed)");

        return Canonicalize(localPath);
    }

    private static string SanitizeLocalPath(string value)
    {
        if (!Path.IsPathRooted(value))
            throw Reject("relative local paths are not allowed (use an absolute path)");

        return Canonicalize(value);
    }

    /// <summary>
    /// Canonicalizes an already-verified-rooted path, converting any framework failure into a
    /// REDACTED rejection. <see cref="Path.GetFullPath(string)"/> throws
    /// <see cref="ArgumentException"/>, <see cref="NotSupportedException"/>,
    /// <see cref="PathTooLongException"/> and friends with the raw path embedded in the message,
    /// so the original exception is dropped entirely — not chained as an inner exception.
    /// The canonicalized result is re-checked for rootedness (belt and braces).
    /// </summary>
    private static string Canonicalize(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            throw Reject("path could not be canonicalized");
        }

        if (string.IsNullOrWhiteSpace(full) || !Path.IsPathRooted(full))
            throw Reject("canonicalized path is not absolute");

        return full;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the URL's authority carries a userinfo DELIMITER (<c>@</c>) — i.e. userinfo is
    /// syntactically PRESENT, even when it is empty.
    /// <para>
    /// <see cref="Uri.UserInfo"/> alone cannot distinguish "no userinfo" from "empty userinfo":
    /// both <c>https://github.com/x</c> and <c>https://@github.com/x</c> report <c>""</c>. The
    /// authority component preserves the delimiter (<c>"github.com:443"</c> vs
    /// <c>"@github.com:443"</c>), so presence is decided there. The host itself can never
    /// contain an <c>@</c>, so the delimiter is unambiguous.
    /// </para>
    /// </summary>
    private static bool HasUserInfoDelimiter(Uri uri)
    {
        string authority;
        try
        {
            authority = uri.GetComponents(
                UriComponents.UserInfo | UriComponents.HostAndPort, UriFormat.UriEscaped);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Unparseable authority: treat as credential-bearing and reject downstream.
            return true;
        }

        return authority.Contains('@');
    }

    private static void RequireAllowedHost(Uri uri)
    {
        // Rule 5: exact host match, case-insensitive.
        if (!string.Equals(uri.Host, AllowedHost, StringComparison.OrdinalIgnoreCase))
            throw Reject($"host must be exactly '{AllowedHost}'");
    }

    private static void RejectQueryOrFragment(Uri uri)
    {
        // Rule 6: no query and no fragment on network URLs.
        if (!string.IsNullOrEmpty(uri.Query))
            throw Reject("URL must not contain a query string");
        if (!string.IsNullOrEmpty(uri.Fragment))
            throw Reject("URL must not contain a fragment");
    }

    /// <summary>
    /// Rebuilds a network URL from its validated components, with the given user info
    /// (<c>null</c> for none). Query and fragment are already rejected at this point.
    /// Any framework failure while reading URI components is converted into a REDACTED
    /// rejection so no raw input can leak through a framework exception message.
    /// </summary>
    private static string Rebuild(Uri uri, string? userInfo)
    {
        try
        {
            var authority = uri.Host;
            if (!uri.IsDefaultPort && uri.Port >= 0)
                authority += $":{uri.Port}";
            if (!string.IsNullOrEmpty(userInfo))
                authority = $"{userInfo}@{authority}";

            return $"{uri.Scheme}://{authority}{uri.AbsolutePath}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            throw Reject("URL could not be rebuilt from its components");
        }
    }

    /// <summary>
    /// Normalizes an scp-style remote (<c>user@host:org/repo.git</c>) to
    /// <c>ssh://user@host/org/repo.git</c>. Exposed so the normalization step can be asserted
    /// in isolation from the host/username rules that follow it.
    /// </summary>
    public static string NormalizeScpStyle(string value)
    {
        var colon = value.IndexOf(':');
        var userHost = value[..colon];
        var path = value[(colon + 1)..].TrimStart('/');
        return $"ssh://{userHost}/{path}";
    }

    /// <summary>
    /// Whether the value starts with a URI scheme (<c>scheme:</c>). A single-letter "scheme"
    /// is treated as a Windows drive letter, not a scheme.
    /// </summary>
    private static bool HasUriScheme(string value)
    {
        var colon = value.IndexOf(':');
        if (colon <= 1)
            return false;

        for (var i = 0; i < colon; i++)
        {
            var c = value[i];
            if (!char.IsLetterOrDigit(c) && c is not ('+' or '-' or '.'))
                return false;
        }
        return char.IsLetter(value[0]);
    }

    /// <summary>
    /// Creates a rejection exception whose message contains the redacted reason only —
    /// never the raw operator input. No inner exception is ever attached: a framework inner
    /// exception (e.g. from <see cref="Path.GetFullPath(string)"/>) would embed the raw path
    /// in its own message and resurface through <see cref="Exception.ToString"/>.
    /// </summary>
    private static RejectedException Reject(string reason) =>
        new($"Invalid --config-repo value: {reason}. (The supplied value is redacted because it may contain credentials.)");

    /// <summary>
    /// The rejection exception type. Derives from <see cref="ArgumentException"/> so existing
    /// callers (including the <c>Program</c> startup catch) keep working, while giving the
    /// sanitizer's own redaction boundary a way to tell "already redacted" apart from a raw
    /// framework exception.
    /// </summary>
    public sealed class RejectedException(string message) : ArgumentException(message);
}

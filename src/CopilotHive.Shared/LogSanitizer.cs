namespace CopilotHive.Services;

/// <summary>
/// Shared helpers for rendering untrusted, externally-supplied strings (such as
/// repository-relative file paths reported by git) safely into log output.
/// </summary>
/// <remarks>
/// Git's <c>-z</c> / NUL-delimited output deliberately does NOT C-quote paths, so a legal
/// filename may contain newlines, tabs, ESC or Unicode line separators verbatim. Writing such
/// a path straight to a log would let it forge additional log lines. Every log site that
/// renders a git-supplied path must route it through <see cref="SanitizePath"/>.
/// </remarks>
public static class LogSanitizer
{
    /// <summary>The placeholder substituted for each unsafe character.</summary>
    public const char Placeholder = '?';

    /// <summary>
    /// Replaces every character that could break a log line into multiple lines with
    /// <see cref="Placeholder"/>, leaving all other characters (including legal non-ASCII
    /// path characters and leading/trailing whitespace) untouched.
    /// </summary>
    /// <param name="path">The raw path to render. May be null or empty.</param>
    /// <returns>The sanitized path, safe to embed in a single-line log message.</returns>
    public static string SanitizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        Span<char> buffer = path.Length <= 512 ? stackalloc char[path.Length] : new char[path.Length];
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            buffer[i] = IsLogUnsafe(c) ? Placeholder : c;
        }
        return new string(buffer);
    }

    /// <summary>
    /// Replaces every character that could break a log line into multiple lines with
    /// <see cref="Placeholder"/>, leaving all other characters (including legal non-ASCII
    /// characters and leading/trailing whitespace) untouched.
    /// </summary>
    /// <remarks>
    /// The free-text counterpart of <see cref="SanitizePath"/>: SAME unsafe-character set
    /// (<see cref="IsLogUnsafe"/>), SAME <see cref="Placeholder"/> substitution and SAME
    /// null/empty passthrough. Use it for any untrusted, externally-supplied message
    /// (git stderr, seam error text) that is rendered into a single-line log message.
    /// </remarks>
    /// <param name="text">The raw text to render. May be null or empty.</param>
    /// <returns>The sanitized text, safe to embed in a single-line log message.</returns>
    public static string SanitizeText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        Span<char> buffer = text.Length <= 512 ? stackalloc char[text.Length] : new char[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            buffer[i] = IsLogUnsafe(c) ? Placeholder : c;
        }
        return new string(buffer);
    }

    /// <summary>
    /// True when the character is a control character (C0 <c>0x00-0x1F</c> including NUL,
    /// backspace, tab, newline, vertical tab, form feed and ESC; DEL <c>0x7F</c>; and the C1
    /// range <c>0x80-0x9F</c>, which already includes U+0085 NEL) or one of the Unicode
    /// line/paragraph separators U+2028 / U+2029.
    /// </summary>
    public static bool IsLogUnsafe(char c) =>
        char.IsControl(c) || c == '\u0085' || c == '\u2028' || c == '\u2029';

    /// <summary>
    /// Renders a BOUNDED, sanitized, comma-separated list of paths for a log message.
    /// </summary>
    /// <param name="paths">The paths to render. Already capped by the caller if desired.</param>
    /// <param name="totalCount">
    /// The TRUE total number of changed files, which may exceed <paramref name="paths"/> count.
    /// When it does, a <c>(+N more)</c> suffix is appended.
    /// </param>
    /// <returns>
    /// A single-line string safe for logging. The <c>(+N more)</c> suffix is log formatting
    /// only — it is never inserted into any domain or protobuf collection.
    /// </returns>
    public static string FormatPathList(IReadOnlyCollection<string> paths, int totalCount)
    {
        var rendered = string.Join(", ", paths.Select(SanitizePath));
        var omitted = totalCount - paths.Count;
        return omitted > 0 ? $"{rendered} (+{omitted} more)" : rendered;
    }
}

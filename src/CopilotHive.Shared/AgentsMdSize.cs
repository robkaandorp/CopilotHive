using System.Globalization;

namespace CopilotHive.Shared;

/// <summary>
/// The single source of truth for the <c>*.agents.md</c> size limit and for the human-readable
/// size line reported after a guidance file is edited.
/// <para>
/// Both the orchestrator's Composer tools and the worker's Improver tools report the FULL file
/// size after a successful edit through <see cref="Describe"/>, so a caller can spot an over-limit
/// file without a separate size-check call.
/// </para>
/// <para>
/// Size is measured in CHARACTERS — UTF-16 code units of <c>File.ReadAllText(...).Length</c> — not
/// bytes and not tokens. A file of exactly <see cref="MaxCharacters"/> characters is allowed; one
/// more character is over the limit.
/// </para>
/// </summary>
public static class AgentsMdSize
{
    /// <summary>
    /// Maximum allowed character count per <c>*.agents.md</c> file. This is the single source of
    /// truth for the limit: a file of exactly this many characters is allowed, one more character
    /// is over the limit.
    /// </summary>
    public const int MaxCharacters = 8000;

    /// <summary>
    /// Describes a guidance file's character count against <see cref="MaxCharacters"/>.
    /// </summary>
    /// <param name="characterCount">
    /// The file's full character count (UTF-16 code units), typically
    /// <c>File.ReadAllText(path).Length</c>.
    /// </param>
    /// <returns>
    /// Within the limit (<paramref name="characterCount"/> &lt;= <see cref="MaxCharacters"/>):
    /// <c>File is now {n} characters (limit 8000).</c> Over the limit:
    /// <c>File is now {n} characters — {n - 8000} over the 8000-character limit. Bring it back within the limit.</c>
    /// <para>
    /// Numbers are formatted with the invariant culture and NO thousands separators, so the same
    /// text is produced regardless of the host's culture.
    /// </para>
    /// </returns>
    public static string Describe(int characterCount)
    {
        var count = characterCount.ToString(CultureInfo.InvariantCulture);
        var limit = MaxCharacters.ToString(CultureInfo.InvariantCulture);

        if (characterCount <= MaxCharacters)
            return $"File is now {count} characters (limit {limit}).";

        var over = (characterCount - MaxCharacters).ToString(CultureInfo.InvariantCulture);
        return $"File is now {count} characters — {over} over the {limit}-character limit. Bring it back within the limit.";
    }
}

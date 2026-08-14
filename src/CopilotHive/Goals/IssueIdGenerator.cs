using System.Text;

namespace CopilotHive.Goals;

/// <summary>
/// Generates unique, human-friendly kebab-case issue IDs from a title slug,
/// with numbered suffix collision handling and a GUID fallback.
/// </summary>
public static class IssueIdGenerator
{
    /// <summary>
    /// Generates an issue ID for the given title. Produces a slug (lowercased,
    /// non-alphanumeric runs collapsed to single hyphens, trimmed of leading/trailing
    /// hyphens) and probes up to 10 candidates (slug, slug-2, ... slug-10) against the
    /// store. Falls back to <c>issue-{Guid:N}</c> when the title slugifies to empty or
    /// all candidates are already taken.
    /// </summary>
    /// <param name="title">The issue title to slugify.</param>
    /// <param name="store">The issue store used to check for ID collisions.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A unique issue ID.</returns>
    public static async Task<string> GenerateAsync(string? title, IIssueStore store, CancellationToken ct)
    {
        var baseSlug = Slugify(title);
        if (string.IsNullOrEmpty(baseSlug)) baseSlug = $"issue-{Guid.NewGuid():N}";
        for (var i = 0; i < 10; i++)
        {
            var candidate = i == 0 ? baseSlug : $"{baseSlug}-{i + 1}";
            if (await store.GetIssueAsync(candidate, ct) is null)
                return candidate;
        }
        return $"issue-{Guid.NewGuid():N}";
    }
    private static string Slugify(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var slug = new StringBuilder();
        var prevHyphen = false;
        foreach (var c in title.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) { slug.Append(c); prevHyphen = false; }
            else if (!prevHyphen && slug.Length > 0) { slug.Append('-'); prevHyphen = true; }
        }
        return slug.ToString().Trim('-');
    }
}

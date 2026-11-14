using System.Text;
using CopilotHive.Services;

namespace CopilotHive.Tests;

/// <summary>Iteration-13 probes of URL rewriting versus original known-value spans. Synthetic values only.</summary>
public sealed class GitToolOutputUrlInventorySequencingTests
{
    private const string Other = "FAKE_OTHER_KEY";
    private static string Wrap(string body) => "file.txt\n" + body + "\nend-marker";
    private static string WithOrdinary(string body) => Wrap("ordinary " + Other + "\n" + body);
    private static string Expected(string body) => Wrap("ordinary [redacted]\n" + body);
    private static string[] Inventory(bool reverse, params string[] values)
    {
        var all = values.Append(Other).ToArray();
        if (reverse) Array.Reverse(all);
        return all;
    }

    private static (string Url, string Safe) Url(string scheme, string shape) => shape switch
    {
        "password" => ($"{scheme}://hunter:FAKE_PASSWORD@host.invalid/r", $"{scheme}://host.invalid/r"),
        "username-only" => ($"{scheme}://FAKE_USERNAME@host.invalid/r", $"{scheme}://host.invalid/r"),
        "query" => ($"{scheme}://host.invalid/r?token=FAKE_QUERY&ref=topic", $"{scheme}://host.invalid/r?ref=topic"),
        "both" => ($"{scheme}://hunter:FAKE_PASSWORD@host.invalid/r?token=FAKE_QUERY&ref=topic", $"{scheme}://host.invalid/r?ref=topic"),
        _ => throw new InvalidOperationException("Unknown URL shape"),
    };

    private static string Place(string value, string placement) => placement switch
    {
        "starts-with-url" => value + " FAKE_PRIVATE_TAIL",
        "ends-with-url" => "FAKE_PRIVATE_PREFIX " + value,
        "middle-url" => "FAKE_PRIVATE_PREFIX " + value + " FAKE_PRIVATE_TAIL",
        _ => throw new InvalidOperationException("Unknown placement"),
    };

    public static TheoryData<string, string, string, bool> WholeCases
    {
        get
        {
            var data = new TheoryData<string, string, string, bool>();
            foreach (var scheme in new[] { "http", "https" })
            foreach (var shape in new[] { "password", "username-only", "query", "both" })
            foreach (var placement in new[] { "starts-with-url", "ends-with-url", "middle-url" })
            foreach (var reverse in new[] { false, true }) data.Add(scheme, shape, placement, reverse);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(WholeCases))]
    public void WholeKnownUrlValue_AllShapesAndPlacements_LeaveNoInventoryResidue(string scheme, string shape, string placement, bool reverse)
    {
        var value = Place(Url(scheme, shape).Url, placement);
        Assert.Equal(Expected("[redacted]"), GitToolOutputRedactor.Redact(WithOrdinary(value), Inventory(reverse, value)));
    }

    [Theory]
    [InlineData("deleted-userinfo", false)]
    [InlineData("deleted-userinfo", true)]
    [InlineData("prefix-into-userinfo", false)]
    [InlineData("prefix-into-userinfo", true)]
    [InlineData("overlapping-retained", false)]
    [InlineData("overlapping-retained", true)]
    [InlineData("touching-retained", false)]
    [InlineData("touching-retained", true)]
    public void PartialInventory_RetainedOverlapAndTouchingHaveDistinctExactBoundaries(string site, bool reverse)
    {
        string body;
        string safe;
        string[] values;
        switch (site)
        {
            case "deleted-userinfo":
                body = "https://hunter:FAKE_PASSWORD@host.invalid/r";
                safe = "https://host.invalid/r"; // Frozen grammar deletes userinfo, no marker needed.
                values = ["hunter:FAKE_PASSWORD"];
                break;
            case "prefix-into-userinfo":
                body = "FAKE_PREFIX https://hunter:FAKE_PASSWORD@host.invalid/r";
                safe = "[redacted]host.invalid/r";
                values = ["FAKE_PREFIX https://hunter:FAKE_PASSWORD"];
                break;
            case "overlapping-retained":
                body = "FAKE_PREFIX https://u:FAKE_PASSWORD@host.invalid/rFAKE_TAIL";
                safe = "[redacted]";
                values = ["FAKE_PREFIX https://u:FAKE_PASSWORD@host.invalid", "host.invalid/rFAKE_TAIL"];
                break;
            case "touching-retained":
                body = "FAKE_PREFIX https://u:FAKE_PASSWORD@host.invalid/rFAKE_TAIL";
                safe = "[redacted][redacted]";
                values = ["FAKE_PREFIX https://u:FAKE_PASSWORD@host.invalid", "/rFAKE_TAIL"];
                break;
            default:
                throw new InvalidOperationException("Unknown boundary");
        }
        Assert.Equal(Expected(safe), GitToolOutputRedactor.Redact(WithOrdinary(body), Inventory(reverse, values)));
    }

    [Theory]
    [InlineData("through-equals", false)]
    [InlineData("through-equals", true)]
    [InlineData("before-equals", false)]
    [InlineData("before-equals", true)]
    [InlineData("whole-query-tail", false)]
    [InlineData("whole-query-tail", true)]
    [InlineData("deleted-parameter", false)]
    [InlineData("deleted-parameter", true)]
    public void QueryBoundary_KnownSurvivingExtentMustNotLeaveDelimiterResidue(string boundary, bool reverse)
    {
        const string body = "https://h.invalid/r?token=FAKE_QUERY&ref=topic";
        var (value, safe) = boundary switch
        {
            "through-equals" => ("FAKE_QUERY&ref=", "https://h.invalid/r?[redacted]topic"),
            "before-equals" => ("FAKE_QUERY&ref", "https://h.invalid/r?[redacted]=topic"),
            "whole-query-tail" => ("token=FAKE_QUERY&ref=topic", "https://h.invalid/r?[redacted]"),
            "deleted-parameter" => ("token=FAKE_QUERY", "https://h.invalid/r?ref=topic"),
            _ => throw new InvalidOperationException("Unknown query boundary"),
        };
        Assert.Equal(Expected(safe), GitToolOutputRedactor.Redact(WithOrdinary(body), Inventory(reverse, value)));
    }

    public static TheoryData<string, string, bool> SshCases
    {
        get
        {
            var data = new TheoryData<string, string, bool>();
            foreach (var transport in new[] { "ssh", "scp" })
            foreach (var placement in new[] { "starts-with-url", "ends-with-url", "middle-url" })
            foreach (var reverse in new[] { false, true }) data.Add(transport, placement, reverse);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(SshCases))]
    public void SshScpKnownWholeValues_RemainProtectedWithoutHttpRewriting(string transport, string placement, bool reverse)
    {
        var url = transport == "ssh" ? "ssh://u:FAKE_PASSWORD@host.invalid:2222/r" : "u:FAKE_PASSWORD@host.invalid:org/r";
        var value = Place(url, placement);
        Assert.Equal(Expected("[redacted]"), GitToolOutputRedactor.Redact(WithOrdinary(value), Inventory(reverse, value)));
    }

    [Theory]
    [InlineData("http", "password")]
    [InlineData("https", "password")]
    [InlineData("http", "username-only")]
    [InlineData("https", "username-only")]
    [InlineData("http", "query")]
    [InlineData("https", "query")]
    [InlineData("http", "both")]
    [InlineData("https", "both")]
    public void NoInventory_FrozenUrlGrammarPreservesNormalHostPathAndQuery(string scheme, string shape)
    {
        var row = Url(scheme, shape);
        Assert.Equal(Wrap(row.Safe), GitToolOutputRedactor.Redact(Wrap(row.Url)));
    }

    private static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string Percent(string value) => string.Concat(Encoding.UTF8.GetBytes(value).Select(b => $"%{b:X2}"));
    private static string UrlBearingValue()
    {
        var value = "https://u:FAKE_PASSWORD@h.invalid/rFAKE_TAIL";
        while (value.Length % 3 == 0) value += "x";
        return value;
    }

    [Theory]
    [InlineData("percent-upper", false)]
    [InlineData("percent-upper", true)]
    [InlineData("percent-lower", false)]
    [InlineData("percent-lower", true)]
    [InlineData("percent-mixed", false)]
    [InlineData("percent-mixed", true)]
    [InlineData("base64-padded", false)]
    [InlineData("base64-padded", true)]
    [InlineData("base64-unpadded", false)]
    [InlineData("base64-unpadded", true)]
    public void EncodedUrlBearingInventory_SingleLayerVariantsAreWholeMatches(string form, bool reverse)
    {
        var value = UrlBearingValue();
        Assert.EndsWith("=", B64(value));
        Assert.NotEqual(B64(value), B64(value).TrimEnd('='));
        var encoded = form switch
        {
            "percent-upper" => Percent(value),
            "percent-lower" => Percent(value).ToLowerInvariant(),
            "percent-mixed" => Uri.EscapeDataString(value),
            "base64-padded" => B64(value),
            "base64-unpadded" => B64(value).TrimEnd('='),
            _ => throw new InvalidOperationException("Unknown encoding"),
        };
        Assert.Equal(Expected("[redacted]"), GitToolOutputRedactor.Redact(WithOrdinary(encoded), Inventory(reverse, value)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComposedEncoding_StillRemainsOutsideTheSingleLayerContract(bool reverse)
    {
        var value = UrlBearingValue();
        var composed = B64(Percent(value));
        Assert.Equal(Expected(composed), GitToolOutputRedactor.Redact(WithOrdinary(composed), Inventory(reverse, value)));
    }

    [Theory]
    [InlineData("full-alphanumeric-region", false)]
    [InlineData("full-alphanumeric-region", true)]
    [InlineData("many-ambiguous-user-characters", false)]
    [InlineData("many-ambiguous-user-characters", true)]
    public void AmbiguousAlignment_KnownHostExtentMustNotLeakCharacters(string shape, bool reverse)
    {
        const string value = "FAKE_PASSWORD@host.invalid";
        // Both are valid complete URLs: no URL unfolding or new grammar. The first uses ordinary
        // alphanumerics in the path; the second uses a long, valid userinfo name.
        var user = shape == "full-alphanumeric-region" ? "hunter" : new string('h', 5000);
        var path = shape == "full-alphanumeric-region"
            ? "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"
            : "r";
        var body = $"https://{user}:FAKE_PASSWORD@host.invalid/{path}";
        Assert.Equal(Expected($"https://[redacted]/{path}"), GitToolOutputRedactor.Redact(WithOrdinary(body), Inventory(reverse, value)));
    }
}

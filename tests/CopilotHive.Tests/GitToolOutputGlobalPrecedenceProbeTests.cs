using System.Text;
using CopilotHive.Services;

namespace CopilotHive.Tests;

/// <summary>Independent iteration-10 probes of global inventory precedence. Synthetic values only.</summary>
public sealed class GitToolOutputGlobalPrecedenceProbeTests
{
    private static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string Percent(string value) => string.Concat(Encoding.UTF8.GetBytes(value).Select(b => $"%{b:X2}"));
    private static string[] Order(string a, string b, bool reverse) => reverse ? [b, a] : [a, b];
    private const string ExpectedOne = "file.txt\n[redacted]\nend-marker";
    private static string Wrap(string text) => "file.txt\n" + text + "\nend-marker";

    public static TheoryData<string, int, bool, bool> PrefixCases
    {
        get
        {
            var rows = new TheoryData<string, int, bool, bool>();
            foreach (var id in new[] { "short-symbols", "long-plain" })
            for (var length = 9; length <= B64(BaseValue(id)).Length; length++)
            foreach (var unpadded in new[] { false, true })
            foreach (var reverse in new[] { false, true })
                rows.Add(id, length, unpadded, reverse);
            return rows;
        }
    }

    private static string BaseValue(string id) => id switch
    {
        "short-symbols" => "FAKE/A+%",
        "long-plain" => "FAKE_GLOBAL_VALUE",
        _ => throw new InvalidOperationException("Unknown base value"),
    };

    [Theory]
    [MemberData(nameof(PrefixCases))]
    public void GlobalBase64Prefixes_EveryLengthThroughFullEncoding_IsRemovedWhole(string id, int prefixLength, bool unpadded, bool reverse)
    {
        var a = BaseValue(id);
        var full = B64(a);
        var b = full[..prefixLength];
        Assert.StartsWith(b, full);
        Assert.InRange(prefixLength, 9, full.Length);
        var input = unpadded ? full.TrimEnd('=') : full;
        Assert.Equal(ExpectedOne, GitToolOutputRedactor.Redact(Wrap(input), Order(a, b, reverse)));
    }

    private static string[] ThreeValueOrder(int permutation, string a, string b, string c) => permutation switch
    {
        0 => [a, b, c], 1 => [a, c, b], 2 => [b, a, c],
        3 => [b, c, a], 4 => [c, a, b], 5 => [c, b, a],
        _ => throw new InvalidOperationException("Unknown permutation"),
    };

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    public void ThreeInventoryChain_SimultaneousFormsAreWholeInEveryPermutation(int permutation, bool unpadded)
    {
        const string a = "FAKE/A+%";
        var b = B64(a)[..10];
        var c = B64(b)[..11];
        Assert.StartsWith(b, B64(a));
        Assert.StartsWith(c, B64(b));
        string Encoded(string value) => unpadded ? B64(value).TrimEnd('=') : B64(value);
        var forms = new[] { a, b, c, Encoded(a), Encoded(b), Encoded(c) };
        var input = string.Join(" | ", forms) + " | " + a + b;
        var expected = string.Join(" | ", Enumerable.Repeat("[redacted]", forms.Length)) + " | [redacted][redacted]";
        Assert.Equal(Wrap(expected), GitToolOutputRedactor.Redact(Wrap(input), ThreeValueOrder(permutation, a, b, c)));
    }

    [Theory]
    [InlineData("percent-to-raw", false)]
    [InlineData("percent-to-raw", true)]
    [InlineData("raw-to-percent", false)]
    [InlineData("raw-to-percent", true)]
    [InlineData("base64-to-percent", false)]
    [InlineData("base64-to-percent", true)]
    public void PercentCrossInventory_FeasibleCollisionDirectionsRemoveWholeVariants(string direction, bool reverse)
    {
        string a;
        string b;
        string input;
        switch (direction)
        {
            case "percent-to-raw":
                a = "FAKE/KEY";
                b = Uri.EscapeDataString(a) + "FAKE_TAIL";
                input = b;
                Assert.StartsWith(Uri.EscapeDataString(a), b);
                break;
            case "raw-to-percent":
                a = "FAKE_KEY%";
                input = Uri.EscapeDataString(a);
                b = input[..10];
                Assert.True(b.Length > a.Length, "Fixture must expose raw-length ordering, not only caller ordering.");
                Assert.StartsWith(b, input);
                break;
            case "base64-to-percent":
                a = "FAKE_KEY";
                b = B64(a) + "%";
                input = B64(a) + "%25"; // A valid mixed literal/escaped one-level spelling of B.
                Assert.StartsWith(B64(a), input);
                break;
            default:
                throw new InvalidOperationException("Unknown direction");
        }
        // A genuinely percent-escaped spelling cannot prefix a STANDARD base64 spelling: '%' is
        // outside that alphabet. The feasible inverse is tested above, per scope clarification.
        Assert.Equal(ExpectedOne, GitToolOutputRedactor.Redact(Wrap(input), Order(a, b, reverse)));
    }

    [Theory]
    [InlineData("ssh-user", false)]
    [InlineData("ssh-user", true)]
    [InlineData("ssh-password", false)]
    [InlineData("ssh-password", true)]
    [InlineData("scp-user", false)]
    [InlineData("scp-user", true)]
    [InlineData("scp-password", false)]
    [InlineData("scp-password", true)]
    [InlineData("header", false)]
    [InlineData("header", true)]
    [InlineData("proxy-header", false)]
    [InlineData("proxy-header", true)]
    public void GlobalCollision_WithinStructuralRegionsAndAdjacentPlaceholders_RemainsSanitized(string site, bool reverse)
    {
        const string a = "FAKE/A+%";
        var encoded = B64(a);
        var b = encoded[..9];
        var (body, safe) = site switch
        {
            "ssh-user" => ($"ssh://[redacted]{encoded}:FAKE_PASSWORD@h.invalid/r", "ssh://[redacted][redacted]:[redacted]@h.invalid/r"),
            "ssh-password" => ($"ssh://u:[redacted]{encoded}FAKE_TAIL@h.invalid/r", "ssh://u:[redacted]@h.invalid/r"),
            "scp-user" => ($"[redacted]{encoded}:FAKE_PASSWORD@h.invalid:org/r", "[redacted][redacted]:[redacted]@h.invalid:org/r"),
            "scp-password" => ($"u:[redacted]{encoded}FAKE_TAIL@h.invalid:org/r", "u:[redacted]@h.invalid:org/r"),
            "header" => ($"Authorization: Bearer [redacted]{encoded}FAKE_TAIL\n\t{encoded}FAKE_LAST", "Authorization: Bearer [redacted]\n\t[redacted]"),
            "proxy-header" => ($"Proxy-Authorization: Basic [redacted]{encoded}\n\tAuthorization: Bearer {encoded}FAKE_LAST", "Proxy-Authorization: Basic [redacted]\n\tAuthorization: Bearer [redacted]"),
            _ => throw new InvalidOperationException("Unknown structural site"),
        };
        // The ordinary line proves global matching independently of any structural pattern that
        // could otherwise hide an inventory-layer failure in a password or header value.
        var input = "ordinary " + encoded + "\n" + body;
        Assert.Equal(Wrap("ordinary [redacted]\n" + safe), GitToolOutputRedactor.Redact(Wrap(input), Order(a, b, reverse)));
    }

    [Theory]
    [InlineData("raw", false)]
    [InlineData("raw", true)]
    [InlineData("base64", false)]
    [InlineData("base64", true)]
    [InlineData("percent", false)]
    [InlineData("percent", true)]
    public void UnrelatedInventories_RetainIndependentWholeReplacements(string form, bool reverse)
    {
        const string a = "FAKE_ONE/1";
        const string b = "FAKE_TWO+2";
        string Encode(string value) => form switch
        {
            "raw" => value,
            "base64" => B64(value),
            "percent" => Percent(value),
            _ => throw new InvalidOperationException("Unknown form"),
        };
        Assert.Equal(Wrap("[redacted] | ordinary | [redacted]"), GitToolOutputRedactor.Redact(Wrap(Encode(a) + " | ordinary | " + Encode(b)), Order(a, b, reverse)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComposedBase64OfPercent_RemainsUnchangedWithBothInventoryOrders(bool reverse)
    {
        const string a = "FAKE/a+b%";
        const string b = "FAKE_UNRELATED";
        var composed = B64(Percent(a));
        Assert.DoesNotContain(B64(a), composed);
        Assert.DoesNotContain(B64(b), composed);
        var input = a + " | " + b + " | " + composed;
        Assert.Equal(Wrap("[redacted] | [redacted] | " + composed), GitToolOutputRedactor.Redact(Wrap(input), Order(a, b, reverse)));
    }

    [Theory]
    [InlineData("raw-base64", false)]
    [InlineData("raw-base64", true)]
    [InlineData("percent-base64", false)]
    [InlineData("percent-base64", true)]
    [InlineData("percent-raw", false)]
    [InlineData("percent-raw", true)]
    public void TouchingEncodedVariants_KeepTwoIndependentPlaceholders(string forms, bool reverse)
    {
        const string a = "FAKE_A/1";
        const string b = "FAKE_B+2";
        var text = forms switch
        {
            "raw-base64" => a + B64(b),
            "percent-base64" => Percent(a) + B64(b),
            "percent-raw" => Percent(a) + b,
            _ => throw new InvalidOperationException("Unknown adjacent forms"),
        };
        Assert.Equal(Wrap("[redacted][redacted]"), GitToolOutputRedactor.Redact(Wrap(text), Order(a, b, reverse)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OverlappingChains_MergeTransitively_WhileTouchingDistinctValuesStaySeparate(bool touching, bool reverse)
    {
        const string a = "FAKE_AAA";
        const string b = "AAA_MID_BBB";
        const string c = "BBB_FAKE_END";
        const string d = "FAKE_OTHER_KEY";
        const string chain = "FAKE_AAA_MID_BBB_FAKE_END";
        Assert.Contains(a, chain);
        Assert.Contains(b, chain);
        Assert.Contains(c, chain);
        var separator = touching ? "" : "|";
        string[] inventory = reverse ? [d, c, b, a] : [a, b, c, d];
        Assert.Equal(Wrap("[redacted]" + separator + "[redacted]"), GitToolOutputRedactor.Redact(Wrap(chain + separator + d), inventory));
    }
}

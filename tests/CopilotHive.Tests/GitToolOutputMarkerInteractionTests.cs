using System.Text;
using CopilotHive.Services;

namespace CopilotHive.Tests;

/// <summary>Iteration-12 marker/inventory interaction probes. All credential-shaped text is synthetic.</summary>
public sealed class GitToolOutputMarkerInteractionTests
{
    private const string Private = "FAKE_PRIVATE_VALUE";
    private const string Other = "FAKE_OTHER_KEY";
    private const string KnownUrl = "https://u:FAKE_PASSWORD@h.invalid/rFAKE_PRIVATE_TAIL";
    private static string Wrap(string text) => "file.txt\n" + text + "\nend-marker";
    private static string[] Order(string a, string b, bool reverse) => reverse ? [b, a] : [a, b];
    private static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string Percent(string value) => string.Concat(Encoding.UTF8.GetBytes(value).Select(b => $"%{b:X2}"));

    private static (string Marker, string Input, string Expected, string WithoutInventory) Reviewer(string id) => id switch
    {
        "authorization" => ("Authorization", $"Authorization: Bearer {Private}", "[redacted]: Bearer [redacted]", "Authorization: Bearer [redacted]"),
        "proxy" => ("Proxy-Authorization", $"Proxy-Authorization: Basic {Private}==", "[redacted]: Basic [redacted]", "Proxy-Authorization: Basic [redacted]"),
        "assignment" => ("GITHUB_TOKEN", $"GITHUB_TOKEN={Private}", "[redacted]=[redacted]", "GITHUB_TOKEN=[redacted]"),
        "ssh" => ("ssh://u:", $"ssh://u:{Private}@h.invalid/r", "[redacted][redacted]@h.invalid/r", "ssh://u:[redacted]@h.invalid/r"),
        _ => throw new InvalidOperationException("Unknown reviewer vector"),
    };

    [Theory]
    [InlineData("authorization")]
    [InlineData("proxy")]
    [InlineData("assignment")]
    [InlineData("ssh")]
    public void ReviewerMarkers_ExactCanonicalOutputsAndBothInventoryOrders(string id)
    {
        var row = Reviewer(id);
        Assert.Equal(row.Expected, GitToolOutputRedactor.Redact(row.Input, [row.Marker]));
        foreach (var reverse in new[] { false, true })
        {
            var input = $"ordinary {row.Marker} | {Other}\n{row.Input}";
            Assert.Equal(Wrap("ordinary [redacted] | [redacted]\n" + row.Expected), GitToolOutputRedactor.Redact(Wrap(input), Order(row.Marker, Other, reverse)));
        }
    }

    [Theory]
    [InlineData("authorization")]
    [InlineData("proxy")]
    [InlineData("assignment")]
    [InlineData("ssh")]
    public void ReviewerMarkers_WithoutInventory_RetainStructuralPositiveControls(string id)
    {
        var row = Reviewer(id);
        Assert.Equal(row.WithoutInventory, GitToolOutputRedactor.Redact(row.Input));
    }

    private static Dictionary<string, (string Marker, string Input, string Expected)> MarkerRows()
    {
        var rows = new Dictionary<string, (string, string, string)>
        {
            ["header-lower"] = ("authorization", $"authorization: Bearer {Private}", "[redacted]: Bearer [redacted]"),
            ["header-upper"] = ("AUTHORIZATION", $"AUTHORIZATION: Basic {Private}==", "[redacted]: Basic [redacted]"),
            ["proxy-lower"] = ("proxy-authorization", $"proxy-authorization: Bearer {Private}", "[redacted]: Bearer [redacted]"),
            ["proxy-upper"] = ("PROXY-AUTHORIZATION", $"PROXY-AUTHORIZATION: Basic {Private}==", "[redacted]: Basic [redacted]"),
            ["proxy-suffix-marker"] = ("Authorization", $"Proxy-Authorization: Basic {Private}==", "Proxy-[redacted]: Basic [redacted]"),
            ["ssh-prefix"] = ("ssh://deployer:", $"ssh://deployer:{Private}@h.invalid:2222/r", "[redacted][redacted]@h.invalid:2222/r"),
            ["scp-prefix"] = ("deployer:", $"deployer:{Private}@h.invalid:org/r", "[redacted][redacted]@h.invalid:org/r"),
            ["scp-user"] = ("deployer", $"deployer:{Private}@h.invalid:org/r", "[redacted]:[redacted]@h.invalid:org/r"),
        };
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GITHUB_CONFIG_REPO_TOKEN", "GITHUB_ORCHESTRATOR_TOKEN", "access_token", "refresh_token", "client_secret", "password", "api_token" })
        {
            rows.Add("assignment-" + name + "-equals", (name, name + "=" + Private, "[redacted]=[redacted]"));
            rows.Add("assignment-" + name + "-colon", (name, name + ": " + Private, "[redacted]: [redacted]"));
        }
        return rows;
    }

    public static TheoryData<string, bool> MarkerCases
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (var id in MarkerRows().Keys.Order(StringComparer.Ordinal))
            foreach (var reverse in new[] { false, true }) data.Add(id, reverse);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(MarkerCases))]
    public void MarkerNames_ExactOutputsAndOrdinaryInventoryControls(string id, bool reverse)
    {
        var row = MarkerRows()[id];
        Assert.True(row.Marker.Length >= 8, "Fixture must meet the inventory length bound.");
        var input = $"ordinary {row.Marker} | {Other}\n{row.Input}";
        var expected = "ordinary [redacted] | [redacted]\n" + row.Expected;
        Assert.Equal(Wrap(expected), GitToolOutputRedactor.Redact(Wrap(input), Order(row.Marker, Other, reverse)));
    }

    public static TheoryData<string, string, bool> EncodedCases
    {
        get
        {
            var data = new TheoryData<string, string, bool>();
            foreach (var marker in new[] { "Authorization", "GITHUB_TOKEN" })
            {
                // Authorization has real base64 padding; GITHUB_TOKEN is 12 bytes and has none.
                var forms = marker == "Authorization"
                    ? new[] { "percent-upper", "percent-lower", "base64-padded", "base64-unpadded" }
                    : new[] { "percent-upper", "percent-lower", "base64-no-padding" };
                foreach (var form in forms)
                foreach (var reverse in new[] { false, true }) data.Add(marker, form, reverse);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EncodedCases))]
    public void EncodedMarkers_AreWholeInventoryMatches_AndDoNotBlindOtherStructures(string marker, string form, bool reverse)
    {
        var encoded = form switch
        {
            "percent-upper" => Percent(marker),
            "percent-lower" => Percent(marker).ToLowerInvariant(),
            "base64-padded" => B64(marker),
            "base64-unpadded" => B64(marker).TrimEnd('='),
            "base64-no-padding" => B64(marker),
            _ => throw new InvalidOperationException("Unknown encoding"),
        };
        if (marker == "Authorization") Assert.EndsWith("==", B64(marker));
        else Assert.DoesNotContain("=", B64(marker));
        var input = $"encoded {encoded} | {Other}\nssh://git:{Private}@h.invalid/r";
        Assert.Equal(Wrap("encoded [redacted] | [redacted]\nssh://git:[redacted]@h.invalid/r"), GitToolOutputRedactor.Redact(Wrap(input), Order(marker, Other, reverse)));
    }

    public static TheoryData<string, bool, bool> UnionCases
    {
        get
        {
            var data = new TheoryData<string, bool, bool>();
            foreach (var id in new[] { "authorization", "proxy", "assignment", "ssh", "scp" })
            foreach (var overlap in new[] { false, true })
            foreach (var reverse in new[] { false, true }) data.Add(id, overlap, reverse);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(UnionCases))]
    public void MarkerCredentialUnion_TouchingStaysTwo_OverlapBecomesOne(string id, bool overlap, bool reverse)
    {
        var (prefix, credential, suffix) = id switch
        {
            "authorization" => ("Authorization: Bearer ", Private, ""),
            "proxy" => ("Proxy-Authorization: Basic ", Private + "==", ""),
            "assignment" => ("GITHUB_TOKEN=", Private, ""),
            "ssh" => ("ssh://u:", Private, "@h.invalid/r"),
            "scp" => ("deployer:", Private, "@h.invalid:org/r"),
            _ => throw new InvalidOperationException("Unknown union vector"),
        };
        var marker = prefix + (overlap ? "FAKE_PRI" : "");
        var body = prefix + credential + suffix;
        var input = $"ordinary {marker}\nother {Other}\n{body}";
        var safeBody = (overlap ? "[redacted]" : "[redacted][redacted]") + suffix;
        Assert.Equal(Wrap("ordinary [redacted]\nother [redacted]\n" + safeBody), GitToolOutputRedactor.Redact(Wrap(input), Order(marker, Other, reverse)));
    }

    [Theory]
    [InlineData("authorization", false)]
    [InlineData("authorization", true)]
    [InlineData("proxy", false)]
    [InlineData("proxy", true)]
    [InlineData("assignment", false)]
    [InlineData("assignment", true)]
    [InlineData("ssh", false)]
    [InlineData("ssh", true)]
    public void MarkerAndCredentialInventories_TogetherRetainExactCanonicalShape(string id, bool reverse)
    {
        var row = Reviewer(id);
        var input = $"ordinary {row.Marker} | {Private}\n{row.Input}\ntoken={B64(Private)}";
        var expected = "ordinary [redacted] | [redacted]\n" + row.Expected + "\ntoken=[redacted]";
        Assert.Equal(Wrap(expected), GitToolOutputRedactor.Redact(Wrap(input), Order(row.Marker, Private, reverse)));
    }

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\n", true)]
    [InlineData("\r\n", false)]
    [InlineData("\r\n", true)]
    public void MarkerNamedFoldedHeaders_EverySegmentAndLineBreakSurvivesSafely(string newline, bool reverse)
    {
        var input = $"file.txt{newline}ordinary Authorization | {Other}{newline}Authorization: Bearer {Private} \t{newline}"
            + $"\tFAKE_CONTINUATION{newline}  Proxy-Authorization: Basic FAKE_INNER==\t {newline} \tFAKE_LAST=={newline}end-marker";
        var expected = $"file.txt{newline}ordinary [redacted] | [redacted]{newline}[redacted]: Bearer [redacted] \t{newline}"
            + $"\t[redacted]{newline}  Proxy-[redacted]: Basic [redacted]\t {newline} \t[redacted]{newline}end-marker";
        Assert.Equal(expected, GitToolOutputRedactor.Redact(input, Order("Authorization", Other, reverse)));
    }

    [Fact]
    public void HttpUrlFirst_WholeInventoryValueMustNotLeaveTail()
    {
        Assert.Equal("[redacted]", GitToolOutputRedactor.Redact(KnownUrl, [KnownUrl]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HttpUrlFirst_KnownValueWithIndependentInventory_ControlStillRedactsWhole(bool reverse)
    {
        var input = $"ordinary {Other}\n{KnownUrl}";
        Assert.Equal(Wrap("ordinary [redacted]\n[redacted]"), GitToolOutputRedactor.Redact(Wrap(input), Order(KnownUrl, Other, reverse)));
    }

    [Fact]
    public void HttpUrlWithoutInventory_PreservesInheritedStructuralControl()
    {
        Assert.Equal("https://h.invalid/rFAKE_PRIVATE_TAIL", GitToolOutputRedactor.Redact(KnownUrl));
    }
}

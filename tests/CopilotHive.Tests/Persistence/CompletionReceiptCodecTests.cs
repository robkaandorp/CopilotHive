using System.Text.Json;
using System.Text.Json.Nodes;

using CopilotHive.Persistence;
using CopilotHive.Services;
using CopilotHive.Workers;

namespace CopilotHive.Tests.Persistence;

/// <summary>
/// Behavioral tests for <see cref="CompletionReceiptCodec"/>: the canonical version-1 envelope, its
/// fixed member order and compact form, the ordinal <c>Encode(Decode(p))</c> comparison contract, and
/// the fail-closed decoding rules (unknown/duplicate/case-variant property names, missing or null
/// members, non-canonical enum labels, non-canonical or out-of-range coverage tokens, non-string list
/// elements, and the carrier's own internal-consistency rules).
/// </summary>
public sealed class CompletionReceiptCodecTests
{
    private static readonly (GoalPhase Phase, WorkerRole Role, string RoleLabel, string PhaseLabel)[] Pairs =
    [
        (GoalPhase.Coding, WorkerRole.Coder, "coder", "coding"),
        (GoalPhase.Testing, WorkerRole.Tester, "tester", "testing"),
        (GoalPhase.Review, WorkerRole.Reviewer, "reviewer", "review"),
        (GoalPhase.DocWriting, WorkerRole.DocWriter, "docwriter", "docwriting"),
        (GoalPhase.Improve, WorkerRole.Improver, "improver", "improve"),
    ];

    private static CompletionReceipt Make(
        GoalPhase phase = GoalPhase.Coding,
        WorkerRole role = WorkerRole.Coder,
        TaskOutcome status = TaskOutcome.Completed,
        TaskMetrics? metrics = null,
        GitChangeSummary? gitStatus = null,
        string? sha = null,
        string output = "out",
        string summary = "sum",
        string verdict = "PASS",
        string model = "m1",
        string goalId = "g1",
        string workerId = "w1",
        string taskId = "t1",
        int iteration = 1,
        int occurrence = 1,
        int attempt = 1)
    {
        var m = metrics is null
            ? null
            : new TaskMetrics
            {
                Verdict = verdict,
                BuildSuccess = metrics.BuildSuccess,
                TotalTests = metrics.TotalTests,
                PassedTests = metrics.PassedTests,
                FailedTests = metrics.FailedTests,
                CoveragePercent = metrics.CoveragePercent,
                Issues = metrics.Issues,
                Summary = summary,
            };

        return new CompletionReceipt(
            goalId,
            workerId,
            role,
            new WorkSlot(taskId, new WorkSlotPosition(iteration, phase, occurrence), attempt),
            new TaskResult
            {
                TaskId = taskId,
                Status = status,
                Output = output,
                Metrics = m,
                GitStatus = gitStatus,
                Model = model,
                IterationStartSha = sha,
            });
    }

    private static TaskMetrics FullMetrics(double coverage = 87.5) => new()
    {
        Verdict = "PASS",
        BuildSuccess = true,
        TotalTests = 10,
        PassedTests = 9,
        FailedTests = 1,
        CoveragePercent = coverage,
        Issues = ["i1", "i2"],
        Summary = "sum",
    };

    private static GitChangeSummary Git() => new()
    {
        FilesChanged = 2,
        Insertions = 10,
        Deletions = 1,
        Pushed = true,
        ChangedFiles = ["a.cs", "b.cs"],
    };

    // ── 1. Canonical output shape ─────────────────────────────────────────

    [Fact]
    public void Encode_ProducesExactCanonicalText()
    {
        var text = CompletionReceiptCodec.Encode(Make(metrics: FullMetrics(), gitStatus: Git(), sha: "abc"));

        const string expected =
            """{"version":1,"goalId":"g1","workerId":"w1","role":"coder","slot":{"taskId":"t1","position":{"iteration":1,"phase":"coding","occurrence":1},"attempt":1},"result":{"taskId":"t1","status":"completed","output":"out","model":"m1","iterationStartSha":"abc","metrics":{"verdict":"PASS","buildSuccess":true,"totalTests":10,"passedTests":9,"failedTests":1,"coveragePercent":87.5,"issues":["i1","i2"],"summary":"sum"},"gitStatus":{"filesChanged":2,"insertions":10,"deletions":1,"pushed":true,"changedFiles":["a.cs","b.cs"]}}}""";

        Assert.Equal(expected, text);
        Assert.DoesNotContain("\n", text);
        Assert.Equal(text, CompletionReceiptCodec.Encode(Make(metrics: FullMetrics(), gitStatus: Git(), sha: "abc")));
    }

    [Fact]
    public void Encode_NullOptionalMembers_EmitsNullsInPlace()
    {
        var text = CompletionReceiptCodec.Encode(Make());
        const string expected =
            """{"version":1,"goalId":"g1","workerId":"w1","role":"coder","slot":{"taskId":"t1","position":{"iteration":1,"phase":"coding","occurrence":1},"attempt":1},"result":{"taskId":"t1","status":"completed","output":"out","model":"m1","iterationStartSha":null,"metrics":null,"gitStatus":null}}""";
        Assert.Equal(expected, text);
    }

    // ── 2. Round-trip: all outcomes, all phase/role pairs ─────────────────

    [Fact]
    public void RoundTrip_EveryOutcomeAndPhaseRolePair()
    {
        foreach (var (phase, role, roleLabel, phaseLabel) in Pairs)
        {
            foreach (var status in Enum.GetValues<TaskOutcome>())
            {
                var receipt = Make(phase, role, status, FullMetrics(), Git(), "sha-" + roleLabel, iteration: 3, occurrence: 2, attempt: 4);
                var text = CompletionReceiptCodec.Encode(receipt);
                Assert.Contains($"\"role\":\"{roleLabel}\"", text);
                Assert.Contains($"\"phase\":\"{phaseLabel}\"", text);
                Assert.Contains($"\"status\":\"{status.ToString().ToLowerInvariant()}\"", text);

                var back = CompletionReceiptCodec.Decode(text);
                Assert.Equal(roleLabel, back.Role.ToRoleName());
                Assert.Equal(phase, back.Slot.Position.Phase);
                Assert.Equal(status, back.Result.Status);
                Assert.Equal(3, back.Slot.Position.Iteration);
                Assert.Equal(2, back.Slot.Position.Occurrence);
                Assert.Equal(4, back.Slot.Attempt);
                Assert.Equal(text, CompletionReceiptCodec.Encode(back));
            }
        }
    }

    [Fact]
    public void RoundTrip_PreservesEveryResultField()
    {
        var receipt = Make(
            GoalPhase.DocWriting,
            WorkerRole.DocWriter,
            TaskOutcome.Cancelled,
            FullMetrics(12.25),
            Git(),
            "deadbeef",
            output: "  leading and trailing  ",
            summary: "  summary with  spaces  ",
            verdict: "",
            model: "",
            goalId: "goal-x",
            workerId: "worker-y",
            taskId: "task-z",
            iteration: 7,
            occurrence: 5,
            attempt: 9);

        var back = CompletionReceiptCodec.Decode(CompletionReceiptCodec.Encode(receipt));

        Assert.Equal("goal-x", back.GoalId);
        Assert.Equal("worker-y", back.WorkerId);
        Assert.Equal("task-z", back.Slot.TaskId);
        Assert.Equal("task-z", back.Result.TaskId);
        Assert.Equal("  leading and trailing  ", back.Result.Output);
        Assert.Equal("", back.Result.Model);
        Assert.Equal("", back.Result.Metrics!.Verdict);
        Assert.Equal("  summary with  spaces  ", back.Result.Metrics.Summary);
        Assert.Equal(12.25, back.Result.Metrics.CoveragePercent);
        Assert.True(back.Result.Metrics.BuildSuccess);
        Assert.Equal(10, back.Result.Metrics.TotalTests);
        Assert.Equal(9, back.Result.Metrics.PassedTests);
        Assert.Equal(1, back.Result.Metrics.FailedTests);
        Assert.Equal(["i1", "i2"], back.Result.Metrics.Issues);
        Assert.Equal(2, back.Result.GitStatus!.FilesChanged);
        Assert.Equal(10, back.Result.GitStatus.Insertions);
        Assert.Equal(1, back.Result.GitStatus.Deletions);
        Assert.True(back.Result.GitStatus.Pushed);
        Assert.Equal(["a.cs", "b.cs"], back.Result.GitStatus.ChangedFiles);
        Assert.Equal("deadbeef", back.Result.IterationStartSha);
    }

    [Fact]
    public void RoundTrip_NullVersusEmptySha_StaysDistinct()
    {
        var withNull = CompletionReceiptCodec.Decode(CompletionReceiptCodec.Encode(Make(sha: null)));
        Assert.Null(withNull.Result.IterationStartSha);

        var withEmpty = CompletionReceiptCodec.Decode(CompletionReceiptCodec.Encode(Make(sha: "")));
        Assert.Equal("", withEmpty.Result.IterationStartSha);
        Assert.NotEqual(
            CompletionReceiptCodec.Encode(Make(sha: null)),
            CompletionReceiptCodec.Encode(Make(sha: "")));
    }

    [Fact]
    public void RoundTrip_PreservesLongTrailingEvidenceVerbatim()
    {
        var tail = new string('x', 5000) + "\n\t\"quoted\" \\backslash\\ <a>&b'c</a> \u00e9\u4e2d";
        var output = "OUT-START " + tail;
        var summary = "SUM-START " + tail;

        var text = CompletionReceiptCodec.Encode(Make(metrics: FullMetrics(), output: output, summary: summary));
        var back = CompletionReceiptCodec.Decode(text);

        Assert.Equal(output, back.Result.Output);
        Assert.Equal(summary, back.Result.Metrics!.Summary);
        Assert.EndsWith(tail, back.Result.Output, StringComparison.Ordinal);
        Assert.EndsWith(tail, back.Result.Metrics.Summary, StringComparison.Ordinal);
        Assert.Equal(text, CompletionReceiptCodec.Encode(back));
        Assert.Contains("OUT-START", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTrip_PreservesListOrdering()
    {
        var metrics = FullMetrics() with { Issues = ["z", "a", "m", "a"] };
        var git = Git() with { ChangedFiles = ["z.cs", "a.cs", "a.cs"] };

        var back = CompletionReceiptCodec.Decode(CompletionReceiptCodec.Encode(Make(metrics: metrics, gitStatus: git)));

        Assert.Equal(["z", "a", "m", "a"], back.Result.Metrics!.Issues);
        Assert.Equal(["z.cs", "a.cs", "a.cs"], back.Result.GitStatus!.ChangedFiles);
    }

    [Fact]
    public void Decode_ReturnsFreshLists_NoAlias()
    {
        var metrics = FullMetrics();
        var git = Git();
        var text = CompletionReceiptCodec.Encode(Make(metrics: metrics, gitStatus: git));

        var first = CompletionReceiptCodec.Decode(text);
        var second = CompletionReceiptCodec.Decode(text);

        Assert.NotSame(first.Result.Metrics!.Issues, second.Result.Metrics!.Issues);
        Assert.NotSame(first.Result.GitStatus!.ChangedFiles, second.Result.GitStatus!.ChangedFiles);
        Assert.NotSame(first.Result.Metrics.Issues, metrics.Issues);

        first.Result.Metrics.Issues.Add("mutated");
        first.Result.GitStatus.ChangedFiles.Add("mutated.cs");

        Assert.Equal(["i1", "i2"], second.Result.Metrics.Issues);
        Assert.Equal(["a.cs", "b.cs"], second.Result.GitStatus.ChangedFiles);
        Assert.Equal(["i1", "i2"], metrics.Issues);
    }

    // ── 3. Non-finite coverage ────────────────────────────────────────────

    [Theory]
    [InlineData(double.NaN, "\"NaN\"")]
    [InlineData(double.PositiveInfinity, "\"Infinity\"")]
    [InlineData(double.NegativeInfinity, "\"-Infinity\"")]
    public void NonFiniteCoverage_UsesExactStringTokens(double value, string token)
    {
        var text = CompletionReceiptCodec.Encode(Make(metrics: FullMetrics(value)));
        Assert.Contains($"\"coveragePercent\":{token}", text);

        var back = CompletionReceiptCodec.Decode(text);
        if (double.IsNaN(value))
            Assert.True(double.IsNaN(back.Result.Metrics!.CoveragePercent));
        else
            Assert.Equal(value, back.Result.Metrics!.CoveragePercent);
        Assert.Equal(text, CompletionReceiptCodec.Encode(back));
    }

    [Fact]
    public void FiniteCoverage_RemainsANumber()
    {
        Assert.Contains("\"coveragePercent\":0", CompletionReceiptCodec.Encode(Make(metrics: FullMetrics(0))));
        Assert.Contains("\"coveragePercent\":-12.5", CompletionReceiptCodec.Encode(Make(metrics: FullMetrics(-12.5))));
    }

    [Theory]
    [InlineData("\"nan\"")]
    [InlineData("\"NAN\"")]
    [InlineData("\"infinity\"")]
    [InlineData("\"1.5\"")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    public void NonCanonicalCoverage_IsRejected(string token)
    {
        var mutated = WithRawCoverage(token);
        Assert.NotEqual(Canonical(), mutated);
        Assert.Throws<CompletionReceiptCodecException>(() => CompletionReceiptCodec.Decode(mutated));
    }

    [Fact]
    public void Coverage_OverflowingNumber_IsNotSilentlyCoercedToInfinity()
    {
        // 1e400 exceeds double range: it must be REFUSED, never read back as Infinity.
        foreach (var token in new[] { "1e400", "-1e400" })
        {
            var mutated = WithRawCoverage(token);
            Assert.NotEqual(Canonical(), mutated);
            Assert.Throws<CompletionReceiptCodecException>(() => CompletionReceiptCodec.Decode(mutated));
        }
    }

    [Fact]
    public void Coverage_IntegerTokenIsAcceptedAsFiniteNumber()
    {
        var back = CompletionReceiptCodec.Decode(WithRawCoverage("90"));
        Assert.Equal(90d, back.Result.Metrics!.CoveragePercent);
        Assert.Equal(
            CompletionReceiptCodec.Encode(Make(metrics: FullMetrics(90), gitStatus: Git(), sha: "abc")),
            CompletionReceiptCodec.Encode(back));
    }

    /// <summary>Replaces the canonical coverage value with a raw token, changing nothing else.</summary>
    private static string WithRawCoverage(string token) =>
        Canonical().Replace("\"coveragePercent\":87.5", $"\"coveragePercent\":{token}", StringComparison.Ordinal);

    // ── 3b. Evidence lists and the relaxed encoder ────────────────────────

    [Fact]
    public void Decode_RejectsNonStringListElementsAtEveryList()
    {
        foreach (var element in new[] { "null", "3", "1.5", "true", "{}", "[]" })
        {
            var issues = Root();
            ((JsonObject)((JsonObject)issues["result"]!)["metrics"]!)["issues"] = JsonNode.Parse($"[{element}]");
            Reject(issues);

            var files = Root();
            ((JsonObject)((JsonObject)files["result"]!)["gitStatus"]!)["changedFiles"] = JsonNode.Parse($"[{element}]");
            Reject(files);
        }

        // A list must be a JSON array, not an object/scalar.
        var objectIssues = Root();
        ((JsonObject)((JsonObject)objectIssues["result"]!)["metrics"]!)["issues"] = new JsonObject();
        Reject(objectIssues);

        var scalarFiles = Root();
        ((JsonObject)((JsonObject)scalarFiles["result"]!)["gitStatus"]!)["changedFiles"] = "a.cs";
        Reject(scalarFiles);
    }

    [Fact]
    public void CanonicalOutput_UsesRawReadableCharacters_NotUnicodeEscapes()
    {
        // The relaxed encoder must leave HTML-sensitive and non-ASCII evidence as readable characters.
        var output = "<a href=\"x\">&'c'</a> \u00e9\u4e2d\u6587";
        var text = CompletionReceiptCodec.Encode(Make(metrics: FullMetrics(), output: output, summary: output));

        // Double quotes are still JSON-escaped (that is required), but the HTML-sensitive and
        // non-ASCII characters must appear raw rather than as \uXXXX escapes.
        Assert.Contains("""<a href=\"x\">&'c'</a>""", text, StringComparison.Ordinal);
        Assert.Contains("\u00e9\u4e2d\u6587", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u003C", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\u0026", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\u00e9", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\u4e2d", text, StringComparison.OrdinalIgnoreCase);

        // And the raw form still round-trips byte-exactly.
        var back = CompletionReceiptCodec.Decode(text);
        Assert.Equal(output, back.Result.Output);
        Assert.Equal(output, back.Result.Metrics!.Summary);
    }

    // ── 4. Canonicalization under valid variation ─────────────────────────

    [Fact]
    public void Canonicalization_WhitespaceOrderAndEnumCase_VariantsAllCollapse()
    {
        var canonical = Canonical();

        // Differing whitespace, differing property order, and permitted enum case variants.
        var reordered = new JsonObject
        {
            ["result"] = Root()["result"]!.DeepClone(),
            ["version"] = 1,
            ["role"] = "Coder",
            ["slot"] = Root()["slot"]!.DeepClone(),
            ["workerId"] = "w1",
            ["goalId"] = "g1",
        };
        ((JsonObject)reordered["slot"]!)["position"]!["phase"] = "CODING";
        ((JsonObject)reordered["result"]!)["status"] = "Completed";

        var variant = JsonSerializer.Serialize(reordered, new JsonSerializerOptions { WriteIndented = true });
        Assert.NotEqual(canonical, variant);
        Assert.Contains("\n", variant, StringComparison.Ordinal);

        var decoded = CompletionReceiptCodec.Decode(variant);
        Assert.Equal(canonical, CompletionReceiptCodec.Encode(decoded));
    }

    [Fact]
    public void OrdinalComparisonContract_TwoEncodingsOfEqualValues_AreEqual()
    {
        var a = CompletionReceiptCodec.Encode(Make(metrics: FullMetrics(), gitStatus: Git(), sha: "s"));
        var b = CompletionReceiptCodec.Encode(Make(metrics: FullMetrics(), gitStatus: Git(), sha: "s"));
        var c = CompletionReceiptCodec.Encode(Make(metrics: FullMetrics(), gitStatus: Git(), sha: "other"));

        Assert.Equal(a, b, StringComparer.Ordinal);
        Assert.NotEqual(a, c, StringComparer.Ordinal);
        Assert.Equal(a, CompletionReceiptCodec.Encode(CompletionReceiptCodec.Decode(a)), StringComparer.Ordinal);
    }

    [Fact]
    public void FirstStoredTime_IsNotPartOfThePayload()
    {
        var text = CompletionReceiptCodec.Encode(Make(metrics: FullMetrics()));
        Assert.DoesNotContain("firstStored", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storedAt", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── 5. Fail-closed decoding (STRUCTURAL mutations — no vacuous passes) ─

    private static string Canonical() => CompletionReceiptCodec.Encode(Make(metrics: FullMetrics(), gitStatus: Git(), sha: "abc"));

    /// <summary>The canonical payload as a mutable JSON object tree.</summary>
    private static JsonObject Root() => (JsonObject)JsonNode.Parse(Canonical())!;

    /// <summary>Compact text of a mutated tree.</summary>
    private static string Text(JsonNode node) => node.ToJsonString();

    /// <summary>Asserts the tree is a genuine change that the codec refuses.</summary>
    private static void Reject(JsonNode node)
    {
        var text = Text(node);
        Assert.NotEqual(Canonical(), text);
        Assert.Throws<CompletionReceiptCodecException>(() => CompletionReceiptCodec.Decode(text));
    }

    private static JsonObject ResultOf(JsonObject root) => (JsonObject)root["result"]!;
    private static JsonObject SlotOf(JsonObject root) => (JsonObject)root["slot"]!;
    private static JsonObject PositionOf(JsonObject root) => (JsonObject)SlotOf(root)["position"]!;
    private static JsonObject MetricsOf(JsonObject root) => (JsonObject)ResultOf(root)["metrics"]!;
    private static JsonObject GitOf(JsonObject root) => (JsonObject)ResultOf(root)["gitStatus"]!;

    [Fact]
    public void Decode_RejectsMalformedAndNonObjectRoots()
    {
        foreach (var json in new[] { "", "{", "not json", "null", "[]", "\"text\"", "1", Canonical() + " trailing" })
            Assert.Throws<CompletionReceiptCodecException>(() => CompletionReceiptCodec.Decode(json));
    }

    [Fact]
    public void Decode_RejectsVersionProblems()
    {
        var missing = Root();
        missing.Remove("version");
        Reject(missing);

        foreach (var token in new[] { "2", "0", "-1", "\"1\"", "1.5", "null", "true" })
        {
            var root = Root();
            root["version"] = JsonNode.Parse(token);
            Reject(root);
        }
    }

    [Fact]
    public void Decode_RejectsUnknownPropertyAtEveryLevel()
    {
        foreach (var build in new Func<JsonObject, JsonObject>[]
                 {
                     r => { r["extra"] = 1; return r; },
                     r => { SlotOf(r)["extra"] = 1; return r; },
                     r => { PositionOf(r)["extra"] = 1; return r; },
                     r => { ResultOf(r)["extra"] = 1; return r; },
                     r => { MetricsOf(r)["extra"] = 1; return r; },
                     r => { GitOf(r)["extra"] = 1; return r; },
                 })
        {
            Reject(build(Root()));
        }
    }

    [Fact]
    public void Decode_RejectsDuplicatePropertyNameAtEveryLevel_EvenWithEqualValues()
    {
        // Duplicates of EXISTING members (equal values), inserted verbatim — no unknown member.
        foreach (var (member, at) in new (string, string)[]
                 {
                     ("\"goalId\":\"g1\"", "root"),
                     ("\"role\":\"coder\"", "root"),
                     ("\"version\":1", "root"),
                     ("\"taskId\":\"t1\"", "slot"),
                     ("\"attempt\":1", "slot"),
                     ("\"iteration\":1", "position"),
                     ("\"occurrence\":1", "position"),
                     ("\"status\":\"completed\"", "result"),
                     ("\"verdict\":\"PASS\"", "metrics"),
                     ("\"coveragePercent\":87.5", "metrics"),
                     ("\"pushed\":true", "git"),
                     ("\"filesChanged\":2", "git"),
                 })
        {
            var mutated = DuplicateMember(Canonical(), member, at);
            Assert.NotEqual(Canonical(), mutated);
            Assert.Throws<CompletionReceiptCodecException>(() => CompletionReceiptCodec.Decode(mutated));
        }
    }

    /// <summary>
    /// Inserts a verbatim duplicate of <paramref name="member"/> immediately after its single
    /// occurrence, restricted to the intended object by anchoring on that object's distinctive text.
    /// </summary>
    private static string DuplicateMember(string canonical, string member, string at)
    {
        var anchor = at switch
        {
            "root" => canonical,
            "slot" => "\"slot\":{" + canonical.Split("\"slot\":{", 2)[1].Split("},\"result\"", 2)[0],
            "position" => canonical.Split("\"position\":{", 2)[1].Split("},", 2)[0],
            "result" => canonical.Split("\"result\":{", 2)[1].Split(",\"gitStatus\"", 2)[0],
            "metrics" => canonical.Split("\"metrics\":{", 2)[1].Split("},\"gitStatus\"", 2)[0],
            "git" => canonical.Split("\"gitStatus\":{", 2)[1],
            _ => throw new ArgumentOutOfRangeException(nameof(at)),
        };

        Assert.Contains(member, anchor, StringComparison.Ordinal);
        var mutatedAnchor = anchor.Replace(member, member + "," + member, StringComparison.Ordinal);
        return canonical.Replace(anchor, mutatedAnchor, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_RejectsCaseVariantPropertyNames()
    {
        foreach (var (name, token) in new (string, string)[]
                 {
                     ("goalId", "\"GoalId\""),
                     ("workerId", "\"WorkerId\""),
                     ("role", "\"Role\""),
                     ("slot", "\"Slot\""),
                     ("result", "\"Result\""),
                     ("taskId", "\"TaskId\""),
                     ("position", "\"Position\""),
                     ("iteration", "\"Iteration\""),
                     ("phase", "\"Phase\""),
                     ("occurrence", "\"Occurrence\""),
                     ("attempt", "\"Attempt\""),
                     ("status", "\"Status\""),
                     ("output", "\"Output\""),
                     ("model", "\"Model\""),
                     ("iterationStartSha", "\"IterationStartSha\""),
                     ("metrics", "\"Metrics\""),
                     ("verdict", "\"Verdict\""),
                     ("buildSuccess", "\"BuildSuccess\""),
                     ("totalTests", "\"TotalTests\""),
                     ("passedTests", "\"PassedTests\""),
                     ("failedTests", "\"FailedTests\""),
                     ("coveragePercent", "\"CoveragePercent\""),
                     ("issues", "\"Issues\""),
                     ("summary", "\"Summary\""),
                     ("gitStatus", "\"GitStatus\""),
                     ("filesChanged", "\"FilesChanged\""),
                     ("insertions", "\"Insertions\""),
                     ("deletions", "\"Deletions\""),
                     ("pushed", "\"Pushed\""),
                     ("changedFiles", "\"ChangedFiles\""),
                 })
        {
            var mutated = Canonical().Replace($"\"{name}\":", $"{token}:", StringComparison.Ordinal);
            Assert.NotEqual(Canonical(), mutated);
            Assert.Throws<CompletionReceiptCodecException>(() => CompletionReceiptCodec.Decode(mutated));
        }
    }

    [Fact]
    public void Decode_RejectsMissingMemberAtEveryLevel()
    {
        foreach (var name in new[]
                 {
                     "version", "goalId", "workerId", "role", "slot", "result",
                     "taskId", "position", "iteration", "phase", "occurrence", "attempt",
                     "status", "output", "model", "iterationStartSha", "metrics", "gitStatus",
                     "verdict", "buildSuccess", "totalTests", "passedTests", "failedTests",
                     "coveragePercent", "issues", "summary",
                     "filesChanged", "insertions", "deletions", "pushed", "changedFiles",
                 })
        {
            var root = Root();
            var removed = RemoveEverywhere(root, name);
            Assert.True(removed, $"'{name}' was not found anywhere in the payload");
            Reject(root);
        }
    }

    /// <summary>Removes every occurrence of the named member from the tree; reports whether any existed.</summary>
    private static bool RemoveEverywhere(JsonNode node, string name)
    {
        var removed = false;
        if (node is JsonObject obj)
        {
            if (obj.Remove(name))
                removed = true;
            foreach (var child in obj.Select(p => p.Value).ToList())
            {
                if (child is not null)
                    removed |= RemoveEverywhere(child, name);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is not null)
                    removed |= RemoveEverywhere(child, name);
            }
        }

        return removed;
    }

    [Fact]
    public void Decode_RejectsNullsWhereValuesAreRequired_ButAcceptsNullableOnes()
    {
        // The three nullable members accept an explicit null.
        var nullSha = Root();
        ResultOf(nullSha)["iterationStartSha"] = null;
        var decodedNullSha = CompletionReceiptCodec.Decode(Text(nullSha));
        Assert.Null(decodedNullSha.Result.IterationStartSha);

        var nullMetrics = Root();
        ResultOf(nullMetrics)["metrics"] = null;
        Assert.Null(CompletionReceiptCodec.Decode(Text(nullMetrics)).Result.Metrics);

        var nullGit = Root();
        ResultOf(nullGit)["gitStatus"] = null;
        Assert.Null(CompletionReceiptCodec.Decode(Text(nullGit)).Result.GitStatus);

        // Every non-nullable member refuses an explicit null.
        foreach (var build in new Func<JsonObject, JsonObject>[]
                 {
                     r => { r["version"] = null; return r; },
                     r => { r["goalId"] = null; return r; },
                     r => { r["workerId"] = null; return r; },
                     r => { r["role"] = null; return r; },
                     r => { r["slot"] = null; return r; },
                     r => { r["result"] = null; return r; },
                     r => { SlotOf(r)["taskId"] = null; return r; },
                     r => { SlotOf(r)["position"] = null; return r; },
                     r => { SlotOf(r)["attempt"] = null; return r; },
                     r => { PositionOf(r)["iteration"] = null; return r; },
                     r => { PositionOf(r)["phase"] = null; return r; },
                     r => { PositionOf(r)["occurrence"] = null; return r; },
                     r => { ResultOf(r)["taskId"] = null; return r; },
                     r => { ResultOf(r)["status"] = null; return r; },
                     r => { ResultOf(r)["output"] = null; return r; },
                     r => { ResultOf(r)["model"] = null; return r; },
                     r => { MetricsOf(r)["verdict"] = null; return r; },
                     r => { MetricsOf(r)["buildSuccess"] = null; return r; },
                     r => { MetricsOf(r)["totalTests"] = null; return r; },
                     r => { MetricsOf(r)["passedTests"] = null; return r; },
                     r => { MetricsOf(r)["failedTests"] = null; return r; },
                     r => { MetricsOf(r)["coveragePercent"] = null; return r; },
                     r => { MetricsOf(r)["issues"] = null; return r; },
                     r => { MetricsOf(r)["summary"] = null; return r; },
                     r => { GitOf(r)["filesChanged"] = null; return r; },
                     r => { GitOf(r)["insertions"] = null; return r; },
                     r => { GitOf(r)["deletions"] = null; return r; },
                     r => { GitOf(r)["pushed"] = null; return r; },
                     r => { GitOf(r)["changedFiles"] = null; return r; },
                 })
        {
            Reject(build(Root()));
        }
    }

    [Fact]
    public void Decode_RejectsBadEnumLabels()
    {
        foreach (var bad in new[] { "\"planning\"", "\"merging\"", "\"done\"", "\"failed\"", "\"0\"", "0", "\"unknown\"", "\"doc_writer\"" })
        {
            var root = Root();
            PositionOf(root)["phase"] = JsonNode.Parse(bad);
            Reject(root);
        }

        foreach (var bad in new[] { "\"unspecified\"", "\"orchestrator\"", "\"mergeworker\"", "\"merge_worker\"", "\"1\"", "1", "\"nope\"" })
        {
            var root = Root();
            root["role"] = JsonNode.Parse(bad);
            Reject(root);
        }

        foreach (var bad in new[] { "\"0\"", "0", "\"unknown\"", "\"pass\"" })
        {
            var root = Root();
            ResultOf(root)["status"] = JsonNode.Parse(bad);
            Reject(root);
        }
    }

    [Fact]
    public void Decode_AcceptsPermittedEnumCaseVariants_AndNormalizesThem()
    {
        foreach (var (phase, role, roleLabel, phaseLabel) in Pairs)
        {
            var root = Root();
            root["role"] = roleLabel.ToUpperInvariant();
            PositionOf(root)["phase"] = char.ToUpperInvariant(phaseLabel[0]) + phaseLabel[1..];
            ResultOf(root)["status"] = "Cancelled";

            var back = CompletionReceiptCodec.Decode(Text(root));
            Assert.Equal(role, back.Role);
            Assert.Equal(phase, back.Slot.Position.Phase);
            Assert.Equal(TaskOutcome.Cancelled, back.Result.Status);
            Assert.Contains($"\"role\":\"{roleLabel}\"", CompletionReceiptCodec.Encode(back));
            Assert.Contains($"\"status\":\"cancelled\"", CompletionReceiptCodec.Encode(back));
        }
    }

    [Fact]
    public void Decode_RejectsIdentityAndCarrierRuleViolations()
    {
        foreach (var blank in new[] { "", "   " })
        {
            var goal = Root();
            goal["goalId"] = blank;
            Reject(goal);

            var worker = Root();
            worker["workerId"] = blank;
            Reject(worker);

            var slotTask = Root();
            SlotOf(slotTask)["taskId"] = blank;
            Reject(slotTask);

            var resultTask = Root();
            ResultOf(resultTask)["taskId"] = blank;
            Reject(resultTask);
        }

        // Task-id mismatch between slot and result.
        var mismatch = Root();
        ResultOf(mismatch)["taskId"] = "t2";
        Reject(mismatch);

        // Non-positive numbers.
        foreach (var name in new[] { "iteration", "occurrence" })
        {
            foreach (var value in new[] { "0", "-1" })
            {
                var root = Root();
                PositionOf(root)[name] = int.Parse(value);
                Reject(root);
            }
        }

        foreach (var value in new[] { "0", "-1" })
        {
            var root = Root();
            SlotOf(root)["attempt"] = int.Parse(value);
            Reject(root);
        }

        // Role/phase mismatch (both individually permitted labels).
        var roleMismatch = Root();
        roleMismatch["role"] = "tester";
        Reject(roleMismatch);

        var phaseMismatch = Root();
        PositionOf(phaseMismatch)["phase"] = "testing";
        Reject(phaseMismatch);
    }

    [Fact]
    public void Decode_BlankIdentity_NamesTheOffendingMember()
    {
        var root = Root();
        SlotOf(root)["taskId"] = "  ";
        var ex = Assert.Throws<CompletionReceiptCodecException>(() => CompletionReceiptCodec.Decode(Text(root)));
        Assert.Contains("slot.taskId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CompletionReceiptCodec.Decode(null!));
        Assert.Throws<ArgumentNullException>(() => CompletionReceiptCodec.Encode(null!));
    }

    [Fact]
    public void Decode_DoesNotDefaultStatusOrVerdict()
    {
        // A payload missing "status" must never silently become "completed".
        var noStatus = Root();
        ResultOf(noStatus).Remove("status");
        Reject(noStatus);

        // A payload missing "verdict" must never silently become "PASS".
        var noVerdict = Root();
        MetricsOf(noVerdict).Remove("verdict");
        Reject(noVerdict);
    }

    [Fact]
    public void Decode_AcceptsLeadingAndTrailingWhitespace()
    {
        var text = Canonical();
        var back = CompletionReceiptCodec.Decode("  \n\t" + text + "\n  ");
        Assert.Equal(text, CompletionReceiptCodec.Encode(back));
    }

    // ── 6. Encode-side validation (reachable via hand-built domain values) ─

    [Fact]
    public void Encode_RejectsUnrepresentableDomainValues()
    {
        // A TaskResult whose required strings are null cannot be written faithfully.
        AssertEncodeRejected(new CompletionReceipt(
            "g1", "w1", WorkerRole.Coder,
            new WorkSlot("t1", new WorkSlotPosition(1, GoalPhase.Coding, 1), 1),
            new TaskResult { TaskId = "t1", Status = TaskOutcome.Completed, Output = null!, Model = "m" }));

        AssertEncodeRejected(new CompletionReceipt(
            "g1", "w1", WorkerRole.Coder,
            new WorkSlot("t1", new WorkSlotPosition(1, GoalPhase.Coding, 1), 1),
            new TaskResult { TaskId = "t1", Status = TaskOutcome.Completed, Output = "o", Model = null! }));

        AssertEncodeRejected(new CompletionReceipt(
            "g1", "w1", WorkerRole.Coder,
            new WorkSlot("t1", new WorkSlotPosition(1, GoalPhase.Coding, 1), 1),
            new TaskResult
            {
                TaskId = "t1", Status = TaskOutcome.Completed, Output = "o", Model = "m",
                Metrics = new TaskMetrics { Verdict = null!, Issues = [], Summary = "s" },
            }));

        AssertEncodeRejected(new CompletionReceipt(
            "g1", "w1", WorkerRole.Coder,
            new WorkSlot("t1", new WorkSlotPosition(1, GoalPhase.Coding, 1), 1),
            new TaskResult
            {
                TaskId = "t1", Status = TaskOutcome.Completed, Output = "o", Model = "m",
                Metrics = new TaskMetrics { Verdict = "v", Issues = null!, Summary = "s" },
            }));

        AssertEncodeRejected(new CompletionReceipt(
            "g1", "w1", WorkerRole.Coder,
            new WorkSlot("t1", new WorkSlotPosition(1, GoalPhase.Coding, 1), 1),
            new TaskResult
            {
                TaskId = "t1", Status = TaskOutcome.Completed, Output = "o", Model = "m",
                Metrics = new TaskMetrics { Verdict = "v", Issues = [null!], Summary = "s" },
            }));

        AssertEncodeRejected(new CompletionReceipt(
            "g1", "w1", WorkerRole.Coder,
            new WorkSlot("t1", new WorkSlotPosition(1, GoalPhase.Coding, 1), 1),
            new TaskResult
            {
                TaskId = "t1", Status = TaskOutcome.Completed, Output = "o", Model = "m",
                GitStatus = new GitChangeSummary { ChangedFiles = null! },
            }));

        AssertEncodeRejected(new CompletionReceipt(
            "g1", "w1", WorkerRole.Coder,
            new WorkSlot("t1", new WorkSlotPosition(1, GoalPhase.Coding, 1), 1),
            new TaskResult
            {
                TaskId = "t1", Status = TaskOutcome.Completed, Output = "o", Model = "m",
                GitStatus = new GitChangeSummary { ChangedFiles = [null!] },
            }));
    }

    private static void AssertEncodeRejected(CompletionReceipt receipt)
    {
        Assert.Throws<CompletionReceiptCodecException>(() => CompletionReceiptCodec.Encode(receipt));
    }
}

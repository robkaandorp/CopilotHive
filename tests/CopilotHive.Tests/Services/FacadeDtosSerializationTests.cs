using CopilotHive.Configuration;

using CopilotHive.Persistence;

using CopilotHive.Services;

using Microsoft.Extensions.AI;

using System.Text.Json;

using System.Text.Json.Serialization;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Serialization contract tests for the facade DTOs relocated to
/// <c>CopilotHive/Services/FacadeDtos.cs</c> in step 1 of the config-repo Blazor migration.
/// <para>
/// All tests use the COMPLETE endpoint JSON configuration — Web defaults PLUS the global
/// snake_case <see cref="Program.GlobalStringEnumConverter"/> — which is the wire contract for
/// minimal-API payloads (registered in production via <see cref="Program.AddHiveJsonOptions"/>).
/// Plain <c>JsonSerializerOptions(JsonSerializerDefaults.Web)</c> would serialize
/// <c>ReasoningEffort</c> enums numerically, which is NOT the wire contract.
/// </para>
/// </summary>
public sealed class FacadeDtosSerializationTests
{
    /// <summary>Web defaults plus the global snake_case enum converter: the endpoint wire options.</summary>
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new Program.GlobalStringEnumConverter() },
    };

    // ── Relocated request DTOs ───────────────────────────────────────────────

    [Fact]

    public void AvailableModelRequest_RoundTrips_AllProperties()
    {
        var request = new AvailableModelRequest("gpt-5", 128_000, "flagship", SupportsVision: true);

        var json = JsonSerializer.Serialize(request, JsonOpts);

        Assert.Equal(
            """{"name":"gpt-5","contextWindow":128000,"description":"flagship","supportsVision":true}""",
            json);

        var roundTripped = JsonSerializer.Deserialize<AvailableModelRequest>(json, JsonOpts);

        Assert.Equal(request, roundTripped);
    }

    [Fact]

    public void AvailableModelRequest_NullableMembers_DeserializeAsNull()
    {
        var request = new AvailableModelRequest("claude-opus-4", null, null, null);

        var json = JsonSerializer.Serialize(request, JsonOpts);

        var roundTripped = JsonSerializer.Deserialize<AvailableModelRequest>(json, JsonOpts);

        Assert.NotNull(roundTripped);

        Assert.Equal("claude-opus-4", roundTripped.Name);

        Assert.Null(roundTripped.ContextWindow);

        Assert.Null(roundTripped.Description);

        Assert.Null(roundTripped.SupportsVision);
    }

    [Fact]

    public void SubAgentModelRequest_RoundTrips_AllProperties()
    {
        var request = new SubAgentModelRequest("o4-mini", 200_000, ReasoningEffort.ExtraHigh, "fast", SupportsVision: false);

        var json = JsonSerializer.Serialize(request, JsonOpts);

        // ReasoningEffort serializes as the canonical snake_case string, not a number.

        Assert.Equal(
            """{"name":"o4-mini","contextWindow":200000,"reasoningEffort":"extra_high","description":"fast","supportsVision":false}""",
            json);

        var roundTripped = JsonSerializer.Deserialize<SubAgentModelRequest>(json, JsonOpts);

        Assert.Equal(request, roundTripped);
    }

    [Fact]

    public void SubAgentModelRequest_NullReasoningEffort_RoundTrips()
    {
        var request = new SubAgentModelRequest("haiku", null, null, null, null);

        var roundTripped = JsonSerializer.Deserialize<SubAgentModelRequest>(
            JsonSerializer.Serialize(request, JsonOpts), JsonOpts);

        Assert.Equal(request, roundTripped);

        Assert.Null(roundTripped!.ReasoningEffort);
    }

    [Fact]

    public void ModelConfigUpdate_RoundTrips_AllProperties()
    {
        var update = new ModelConfigUpdate(
            OrchestratorModel: "gpt-5",

            ComposerModel: "claude-sonnet-4",

            WorkerModels: new Dictionary<string, string> { ["coder"] = "gpt-5-mini" },

            PremiumWorkerModels: new Dictionary<string, string> { ["tester"] = "o3" },

            CompactionModel: "gpt-4.1-mini",

            OrchestratorReasoningEffort: ReasoningEffort.High,

            ComposerReasoningEffort: ReasoningEffort.None,

            WorkerReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["coder"] = ReasoningEffort.Medium, ["reviewer"] = null },

            WorkerPremiumReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["tester"] = ReasoningEffort.Low },

            SubAgentModelReasoning: new Dictionary<string, ReasoningEffort?> { ["o4-mini"] = ReasoningEffort.ExtraHigh });

        var roundTripped = JsonSerializer.Deserialize<ModelConfigUpdate>(
            JsonSerializer.Serialize(update, JsonOpts), JsonOpts);

        Assert.NotNull(roundTripped);

        Assert.Equal(update.OrchestratorModel, roundTripped.OrchestratorModel);

        Assert.Equal(update.ComposerModel, roundTripped.ComposerModel);

        Assert.Equal(update.WorkerModels, roundTripped.WorkerModels);

        Assert.Equal(update.PremiumWorkerModels, roundTripped.PremiumWorkerModels);

        Assert.Equal(update.CompactionModel, roundTripped.CompactionModel);

        Assert.Equal(update.OrchestratorReasoningEffort, roundTripped.OrchestratorReasoningEffort);

        Assert.Equal(update.ComposerReasoningEffort, roundTripped.ComposerReasoningEffort);

        Assert.Equal(update.WorkerReasoningEffort, roundTripped.WorkerReasoningEffort);

        Assert.Equal(update.WorkerPremiumReasoningEffort, roundTripped.WorkerPremiumReasoningEffort);

        Assert.Equal(update.SubAgentModelReasoning, roundTripped.SubAgentModelReasoning);
    }

    [Fact]

    public void ModelConfigUpdate_DefaultOptionalMembers_SerializeAsNullAndRoundTrip()
    {
        var update = new ModelConfigUpdate(null, null, null, null, null);

        var json = JsonSerializer.Serialize(update, JsonOpts);

        var roundTripped = JsonSerializer.Deserialize<ModelConfigUpdate>(json, JsonOpts);

        Assert.Equal(update, roundTripped);

        Assert.Null(roundTripped!.OrchestratorReasoningEffort);

        Assert.Null(roundTripped.SubAgentModelReasoning);
    }

    [Fact]

    public void OrchestratorSettingsUpdate_RoundTrips_AllProperties()
    {
        var update = new OrchestratorSettingsUpdate(10, 3, 2, true, 50, 24);

        var json = JsonSerializer.Serialize(update, JsonOpts);

        Assert.Equal(
            """{"maxIterations":10,"maxRetriesPerTask":3,"maxParallelGoals":2,"verboseLogging":true,"brainMaxSteps":50,"branchCleanupDelayHours":24}""",
            json);

        Assert.Equal(update, JsonSerializer.Deserialize<OrchestratorSettingsUpdate>(json, JsonOpts));
    }

    [Fact]

    public void OrchestratorSettingsUpdate_NullableMembers_DefaultToNull()
    {
        var roundTripped = JsonSerializer.Deserialize<OrchestratorSettingsUpdate>("{}", JsonOpts);

        Assert.NotNull(roundTripped);

        Assert.Null(roundTripped.MaxIterations);

        Assert.Null(roundTripped.MaxRetriesPerTask);

        Assert.Null(roundTripped.MaxParallelGoals);

        Assert.Null(roundTripped.VerboseLogging);

        Assert.Null(roundTripped.BrainMaxSteps);

        Assert.Null(roundTripped.BranchCleanupDelayHours);
    }

    [Fact]

    public void RepositoryRequest_RoundTrips_AllProperties()
    {
        var request = new RepositoryRequest(
            "CopilotHive",

            "https://github.com/example/CopilotHive.git",

            "main",

            new ReleaseRepoConfig { MergeTo = "release", TagBranch = "main" },

            MonitorCi: true,

            CiTimeoutMinutes: 45);

        var json = JsonSerializer.Serialize(request, JsonOpts);

        var roundTripped = JsonSerializer.Deserialize<RepositoryRequest>(json, JsonOpts);

        Assert.NotNull(roundTripped);

        Assert.Equal(request.Name, roundTripped.Name);

        Assert.Equal(request.Url, roundTripped.Url);

        Assert.Equal(request.DefaultBranch, roundTripped.DefaultBranch);

        Assert.Equal(request.MonitorCi, roundTripped.MonitorCi);

        Assert.Equal(request.CiTimeoutMinutes, roundTripped.CiTimeoutMinutes);

        Assert.NotNull(roundTripped.Release);

        Assert.Equal("release", roundTripped.Release.MergeTo);

        Assert.Equal("main", roundTripped.Release.TagBranch);
    }

    [Fact]

    public void RepositoryRequest_OmittedOptionalMembers_DefaultCorrectly()
    {
        var roundTripped = JsonSerializer.Deserialize<RepositoryRequest>(
            """{"name":"r","url":"https://x","defaultBranch":"main"}""", JsonOpts);

        Assert.NotNull(roundTripped);

        Assert.Null(roundTripped.Release);

        Assert.Null(roundTripped.MonitorCi);

        Assert.Null(roundTripped.CiTimeoutMinutes);
    }

    [Fact]

    public void ComposerSettingsUpdate_RoundTrips_AllProperties()
    {
        var update = new ComposerSettingsUpdate(
            MaxSteps: 80,

            EventNotificationsMode: "active",

            EventNotificationsActiveEvents: ["goal_completed", "task_failed"],

            EventNotificationsThrottleSeconds: 30);

        var json = JsonSerializer.Serialize(update, JsonOpts);

        var roundTripped = JsonSerializer.Deserialize<ComposerSettingsUpdate>(json, JsonOpts);

        Assert.NotNull(roundTripped);

        Assert.Equal(80, roundTripped.MaxSteps);

        Assert.Equal("active", roundTripped.EventNotificationsMode);

        Assert.Equal(["goal_completed", "task_failed"], roundTripped.EventNotificationsActiveEvents);

        Assert.Equal(30, roundTripped.EventNotificationsThrottleSeconds);
    }

    [Fact]

    public void ComposerSettingsUpdate_OmittedMembers_DefaultToNull()
    {
        var roundTripped = JsonSerializer.Deserialize<ComposerSettingsUpdate>("{}", JsonOpts);

        Assert.NotNull(roundTripped);

        Assert.Null(roundTripped.MaxSteps);

        Assert.Null(roundTripped.EventNotificationsMode);

        Assert.Null(roundTripped.EventNotificationsActiveEvents);

        Assert.Null(roundTripped.EventNotificationsThrottleSeconds);
    }

    // ── Shared response DTOs ─────────────────────────────────────────────────

    [Fact]

    public void SavedResult_NullDescription_OmitsDescriptionProperty()
    {
        var result = new SavedResult(true, null);

        var json = JsonSerializer.Serialize(result, JsonOpts);

        Assert.Equal("""{"saved":true}""", json);

        Assert.DoesNotContain("description", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]

    public void SavedResult_NonNullDescription_SerializesNormally()
    {
        var result = new SavedResult(true, "model saved");

        var json = JsonSerializer.Serialize(result, JsonOpts);

        Assert.Equal("""{"saved":true,"description":"model saved"}""", json);
    }

    [Fact]

    public void SavedResult_FalseSaved_SerializesAndRoundTrips()
    {
        var json = JsonSerializer.Serialize(new SavedResult(false, "not found"), JsonOpts);

        var roundTripped = JsonSerializer.Deserialize<SavedResult>(json, JsonOpts);

        Assert.NotNull(roundTripped);

        Assert.False(roundTripped.Saved);

        Assert.Equal("not found", roundTripped.Description);
    }

    [Fact]

    public void RemovedResult_SerializesAsCamelCaseRemoved()
    {
        var json = JsonSerializer.Serialize(new RemovedResult(true), JsonOpts);

        Assert.Equal("""{"removed":true}""", json);

        Assert.True(JsonSerializer.Deserialize<RemovedResult>(json, JsonOpts)!.Removed);
    }

    [Fact]

    public void AvailableModelDto_HasNoReasoningEffortProperty()
    {
        var dto = new AvailableModelDto("gpt-5", 128_000, "flagship", true);

        var json = JsonSerializer.Serialize(dto, JsonOpts);

        Assert.Equal(
            """{"name":"gpt-5","contextWindow":128000,"description":"flagship","supportsVision":true}""",
            json);

        using var document = JsonDocument.Parse(json);

        Assert.False(document.RootElement.TryGetProperty("reasoningEffort", out _));
    }

    [Fact]

    public void AvailableModelDto_ContextWindowAcceptsNull()
    {
        var json = JsonSerializer.Serialize(new AvailableModelDto("haiku", null, null, null), JsonOpts);

        var roundTripped = JsonSerializer.Deserialize<AvailableModelDto>(json, JsonOpts);

        Assert.NotNull(roundTripped);

        Assert.Equal("haiku", roundTripped.Name);

        Assert.Null(roundTripped.ContextWindow);

        Assert.Null(roundTripped.Description);

        Assert.Null(roundTripped.SupportsVision);
    }

    [Fact]

    public void ConfigSubAgentModelDto_ReasoningEffort_SerializesAsSnakeCaseAndRoundTrips()
    {
        var dto = new ConfigSubAgentModelDto("o4-mini", 200_000, ReasoningEffort.ExtraHigh, "fast", true);

        var json = JsonSerializer.Serialize(dto, JsonOpts);

        Assert.Equal(
            """{"name":"o4-mini","contextWindow":200000,"reasoningEffort":"extra_high","description":"fast","supportsVision":true}""",
            json);

        var roundTripped = JsonSerializer.Deserialize<ConfigSubAgentModelDto>(json, JsonOpts);

        Assert.Equal(dto, roundTripped);

        Assert.Equal(ReasoningEffort.ExtraHigh, roundTripped!.ReasoningEffort);
    }

    [Fact]

    public void ConfigSubAgentModelDto_NoneEffort_SerializesAsNoneString()
    {
        var json = JsonSerializer.Serialize(
            new ConfigSubAgentModelDto("haiku", 200_000, ReasoningEffort.None, null, null), JsonOpts);

        Assert.Contains("\"reasoningEffort\":\"none\"", json, StringComparison.Ordinal);
    }

    [Fact]

    public void ConfigSubAgentModelDto_NullableMembers_ArePresentInJson()
    {
        var json = JsonSerializer.Serialize(
            new ConfigSubAgentModelDto("haiku", null, null, null, null), JsonOpts);

        // Every property is present, even when null — the endpoint always projects the full shape.

        using var document = JsonDocument.Parse(json);

        var root = document.RootElement;

        Assert.True(root.TryGetProperty("contextWindow", out var contextWindow));

        Assert.Equal(JsonValueKind.Null, contextWindow.ValueKind);

        Assert.True(root.TryGetProperty("reasoningEffort", out var reasoningEffort));

        Assert.Equal(JsonValueKind.Null, reasoningEffort.ValueKind);

        Assert.True(root.TryGetProperty("description", out _));

        Assert.True(root.TryGetProperty("supportsVision", out _));
    }

    [Fact]

    public void WorkerModelsDto_NullModel_SerializesAsExplicitNull()
    {
        var json = JsonSerializer.Serialize(new WorkerModelsDto(null, "o3"), JsonOpts);

        Assert.Equal("""{"model":null,"premiumModel":"o3"}""", json);
    }

    [Fact]

    public void WorkerModelsDto_RoundTripsBothModels()
    {
        var json = JsonSerializer.Serialize(new WorkerModelsDto("gpt-5-mini", null), JsonOpts);

        var roundTripped = JsonSerializer.Deserialize<WorkerModelsDto>(json, JsonOpts);

        Assert.Equal(new WorkerModelsDto("gpt-5-mini", null), roundTripped);

        Assert.Null(roundTripped!.PremiumModel);
    }

    [Fact]

    public void RepositoryDto_MirrorsRepositoryConfigShape()
    {
        var dto = new RepositoryDto(
            "CopilotHive",

            "https://github.com/example/CopilotHive.git",

            "main",

            MonitorCi: true,

            CiTimeoutMinutes: 45,

            new RepositoryReleaseDto("release", "main"),

            new RepositoryPublishNuGetDto([new RepositoryPackageDto("My.Library")]));

        var json = JsonSerializer.Serialize(dto, JsonOpts);

        Assert.Equal(
            """{"name":"CopilotHive","url":"https://github.com/example/CopilotHive.git","defaultBranch":"main","monitorCi":true,"ciTimeoutMinutes":45,"release":{"mergeTo":"release","tagBranch":"main"},"publishNuGet":{"packages":[{"packageId":"My.Library"}]}}""",
            json);

        var roundTripped = JsonSerializer.Deserialize<RepositoryDto>(json, JsonOpts);

        Assert.NotNull(roundTripped);

        Assert.Equal(dto.Name, roundTripped.Name);

        Assert.Equal(dto.Url, roundTripped.Url);

        Assert.Equal(dto.DefaultBranch, roundTripped.DefaultBranch);

        Assert.Equal(dto.MonitorCi, roundTripped.MonitorCi);

        Assert.Equal(dto.CiTimeoutMinutes, roundTripped.CiTimeoutMinutes);

        Assert.Equal(dto.Release, roundTripped.Release);

        Assert.Equal(dto.PublishNuGet?.Packages, roundTripped.PublishNuGet?.Packages);
    }

    [Fact]

    public void RepositoryDto_EmptyReleaseObject_DeserializesWithNullBranches()
    {
        var dto = JsonSerializer.Deserialize<RepositoryDto>(
            """
            {
              "name": "r",

              "url": "https://x",

              "defaultBranch": "main",

              "monitorCi": false,

              "ciTimeoutMinutes": 30,

              "release": {}
            }
            """, JsonOpts);

        Assert.NotNull(dto);

        Assert.NotNull(dto.Release);

        Assert.Null(dto.Release.MergeTo);

        Assert.Null(dto.Release.TagBranch);

        Assert.Null(dto.PublishNuGet);
    }

    // ── Settings response DTOs (step 4) ─────────────────────────────────────

    /// <summary>
    /// <see cref="WorkersConfigDto"/> serializes as a TOP-LEVEL role-keyed dictionary — the
    /// exact wire shape the pre-facade GET /api/config/workers handler produced. A wrapper
    /// object would change the contract, so this test locks the dictionary-derived shape.
    /// </summary>
    [Fact]

    public void WorkersConfigDto_SerializesAsTopLevelRoleKeyedDictionary()
    {
        var dto = new WorkersConfigDto
        {
            ["coder"] = new WorkerEntryDto("copilot/coder", "copilot/coder-premium", 128000),
            ["tester"] = new WorkerEntryDto("copilot/tester", null, 0),
        };

        var json = JsonSerializer.Serialize(dto, JsonOpts);

        Assert.Equal(
            """{"coder":{"model":"copilot/coder","premiumModel":"copilot/coder-premium","contextWindow":128000},"tester":{"model":"copilot/tester","premiumModel":null,"contextWindow":0}}""",
            json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("coder", out var coder));
        Assert.Equal("copilot/coder", coder.GetProperty("model").GetString());
        Assert.Equal("copilot/coder-premium", coder.GetProperty("premiumModel").GetString());
        Assert.Equal(128000, coder.GetProperty("contextWindow").GetInt32());
        Assert.True(root.TryGetProperty("tester", out var tester));
        Assert.Equal(JsonValueKind.Null, tester.GetProperty("premiumModel").ValueKind);
        Assert.Equal(0, tester.GetProperty("contextWindow").GetInt32());
    }

    /// <summary>
    /// <see cref="WorkersConfigDto"/> with no roles serializes as an empty top-level object.
    /// </summary>
    [Fact]

    public void WorkersConfigDto_Empty_SerializesAsEmptyObject()
    {
        var json = JsonSerializer.Serialize(new WorkersConfigDto(), JsonOpts);

        Assert.Equal("{}", json);
    }

    /// <summary>
    /// <see cref="OrchestratorConfigDto"/> asserts EVERY property the pre-facade GET
    /// /api/config/orchestrator handler serialized (the raw <see cref="OrchestratorConfig"/>
    /// object): model, maxIterations, maxRetriesPerTask, maxParallelGoals, verboseLogging,
    /// brainMaxSteps, branchCleanupDelayHours, workerTaskTimeoutMinutes, reasoningEffort.
    /// Removing any property from the DTO fails this test.
    /// </summary>
    [Fact]

    public void OrchestratorConfigDto_SerializesEveryCurrentProperty()
    {
        var dto = new OrchestratorConfigDto(
            Model: "copilot/gpt-5",
            MaxIterations: 7,
            MaxRetriesPerTask: 4,
            MaxParallelGoals: 2,
            VerboseLogging: true,
            BrainMaxSteps: 60,
            BranchCleanupDelayHours: 12,
            WorkerTaskTimeoutMinutes: 25,
            ReasoningEffort: "high");

        var json = JsonSerializer.Serialize(dto, JsonOpts);

        Assert.Equal(
            """{"model":"copilot/gpt-5","maxIterations":7,"maxRetriesPerTask":4,"maxParallelGoals":2,"verboseLogging":true,"brainMaxSteps":60,"branchCleanupDelayHours":12,"workerTaskTimeoutMinutes":25,"reasoningEffort":"high"}""",
            json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(9, root.EnumerateObject().Count());
        Assert.True(root.TryGetProperty("model", out _));
        Assert.True(root.TryGetProperty("maxIterations", out _));
        Assert.True(root.TryGetProperty("maxRetriesPerTask", out _));
        Assert.True(root.TryGetProperty("maxParallelGoals", out _));
        Assert.True(root.TryGetProperty("verboseLogging", out _));
        Assert.True(root.TryGetProperty("brainMaxSteps", out _));
        Assert.True(root.TryGetProperty("branchCleanupDelayHours", out _));
        Assert.True(root.TryGetProperty("workerTaskTimeoutMinutes", out _));
        Assert.True(root.TryGetProperty("reasoningEffort", out _));
    }

    /// <summary>
    /// <see cref="OrchestratorConfigDto"/> null members serialize as explicit nulls — the
    /// endpoint always projects the full shape.
    /// </summary>
    [Fact]

    public void OrchestratorConfigDto_NullableMembers_SerializeAsExplicitNull()
    {
        var json = JsonSerializer.Serialize(
            new OrchestratorConfigDto(null, 10, 3, 1, false, 50, 48, 10, null), JsonOpts);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("model").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("reasoningEffort").ValueKind);
    }

    /// <summary>
    /// <see cref="ComposerConfigDto"/> null/default case: null model and reasoning effort,
    /// default max steps, passive mode, the four default active events, nine valid events,
    /// throttle 30 — the exact shape the pre-facade GET /api/config/composer handler produced
    /// for a null Composer section.
    /// </summary>
    [Fact]

    public void ComposerConfigDto_NullComposerDefaults_SerializeAsWireShape()
    {
        var dto = new ComposerConfigDto(
            Model: null,
            MaxSteps: 50,
            ReasoningEffort: null,
            EventNotifications: new ComposerEventNotificationsDto(
                Mode: "passive",
                ActiveEvents: ["goal_completed", "goal_failed", "ci_failed", "issue_raised"],
                ValidActiveEvents:
                [
                    "goal_completed", "goal_failed", "ci_failed", "issue_raised",
                    "package_published", "ci_succeeded", "release_completed",
                    "goal_dispatched", "issue_resolved",
                ],
                ThrottleSeconds: 30));

        var json = JsonSerializer.Serialize(dto, JsonOpts);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("model").ValueKind);
        Assert.Equal(50, root.GetProperty("maxSteps").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("reasoningEffort").ValueKind);
        var notif = root.GetProperty("eventNotifications");
        Assert.Equal("passive", notif.GetProperty("mode").GetString());
        Assert.Equal(
            ["goal_completed", "goal_failed", "ci_failed", "issue_raised"],
            notif.GetProperty("activeEvents").EnumerateArray().Select(e => e.GetString()).ToList());
        Assert.Equal(
            ["goal_completed", "goal_failed", "ci_failed", "issue_raised", "package_published",
             "ci_succeeded", "release_completed", "goal_dispatched", "issue_resolved"],
            notif.GetProperty("validActiveEvents").EnumerateArray().Select(e => e.GetString()).ToList());
        Assert.Equal(30, notif.GetProperty("throttleSeconds").GetInt32());
    }

    /// <summary>
    /// <see cref="ComposerConfigDto"/> serializes the fully-populated shape verbatim: the DTO is
    /// a pure carrier, so <c>activeEvents</c> is emitted in exactly the order the list holds.
    /// <para>
    /// The input here is the facade's already-canonicalized whitelist order, matching what
    /// <c>ConfigFacade.GetComposer</c> produces. This test locks SERIALIZATION ONLY — the
    /// canonical REORDERING contract lives in the facade and is covered by
    /// <c>ConfigFacadeSettingsTests.GetComposer_NoncanonicalStoredEventOrder_ProjectsCanonicalWhitelistOrder</c>,
    /// which feeds a noncanonical stored list and asserts the reordered projection.
    /// </para>
    /// </summary>
    [Fact]

    public void ComposerConfigDto_PopulatedShape_SerializesListOrderVerbatim()
    {
        var dto = new ComposerConfigDto(
            Model: "copilot/composer",
            MaxSteps: 80,
            ReasoningEffort: "medium",
            EventNotifications: new ComposerEventNotificationsDto(
                Mode: "active",
                // Canonical whitelist order — the shape the facade hands to the serializer.
                ActiveEvents: ["goal_completed", "ci_failed", "package_published"],
                ValidActiveEvents:
                [
                    "goal_completed", "goal_failed", "ci_failed", "issue_raised",
                    "package_published", "ci_succeeded", "release_completed",
                    "goal_dispatched", "issue_resolved",
                ],
                ThrottleSeconds: 60));

        var json = JsonSerializer.Serialize(dto, JsonOpts);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("copilot/composer", root.GetProperty("model").GetString());
        Assert.Equal(80, root.GetProperty("maxSteps").GetInt32());
        Assert.Equal("medium", root.GetProperty("reasoningEffort").GetString());
        var notif = root.GetProperty("eventNotifications");
        Assert.Equal("active", notif.GetProperty("mode").GetString());
        Assert.Equal(
            ["goal_completed", "ci_failed", "package_published"],
            notif.GetProperty("activeEvents").EnumerateArray().Select(e => e.GetString()).ToList());
        Assert.Equal(60, notif.GetProperty("throttleSeconds").GetInt32());
    }

    // ── Backup DTO ───────────────────────────────────────────────────────────

    /// <summary>
    /// The single backup DTO must serialize to the exact wire shape the backup endpoints
    /// produced before the facade existed — the property names of
    /// <see cref="BackupService.BackupInfo"/> under Web (camelCase) naming.
    /// </summary>
    [Fact]

    public void BackupInfoDto_SerializesToTheBackupInfoWireShape()
    {
        var dto = new BackupInfoDto(
            "copilothive-backup-20240102T030405.tar.gz",
            123_456L,
            new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        var json = JsonSerializer.Serialize(dto, JsonOpts);

        Assert.Equal(
            """{"fileName":"copilothive-backup-20240102T030405.tar.gz","sizeBytes":123456,"createdAt":"2024-01-02T03:04:05Z"}""",
            json);
    }

    /// <summary>
    /// Serializing the DTO and the original <see cref="BackupService.BackupInfo"/> record must
    /// produce byte-identical JSON, so the create/list responses are unchanged by the migration.
    /// </summary>
    [Fact]

    public void BackupInfoDto_ProducesIdenticalJsonToBackupServiceBackupInfo()
    {
        var createdAt = new DateTime(2030, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        var info = new BackupService.BackupInfo("copilothive-backup-20300607T080910.tar.gz", 7L, createdAt);
        var dto = new BackupInfoDto(info.FileName, info.SizeBytes, info.CreatedAt);

        Assert.Equal(
            JsonSerializer.Serialize(info, JsonOpts),
            JsonSerializer.Serialize(dto, JsonOpts));
    }

    [Fact]

    public void BackupInfoDto_RoundTripsAllProperties()
    {
        var dto = new BackupInfoDto(
            "copilothive-backup-19991231T235959.tar.gz",
            0L,
            new DateTime(1999, 12, 31, 23, 59, 59, DateTimeKind.Utc));

        var json = JsonSerializer.Serialize(dto, JsonOpts);
        var roundTripped = JsonSerializer.Deserialize<BackupInfoDto>(json, JsonOpts);

        Assert.Equal(dto, roundTripped);
    }

    // ── Composer runtime DTOs (step 5b) ──────────────────────────────────────

    /// <summary>
    /// <see cref="CurrentModelDto"/> serializes as <c>{"model":…}</c>; the model is NULLABLE
    /// by contract — an unconfigured Composer reports <c>{"model":null}</c> on a SUCCESSFUL
    /// read, so the null must be explicit in the JSON (not omitted).
    /// </summary>
    [Fact]
    public void CurrentModelDto_SerializesAsNullableModelProperty()
    {
        var json = JsonSerializer.Serialize(new CurrentModelDto("claude-opus"), JsonOpts);
        Assert.Equal("""{"model":"claude-opus"}""", json);

        var nullJson = JsonSerializer.Serialize(new CurrentModelDto(null), JsonOpts);
        Assert.Equal("""{"model":null}""", nullJson);

        var roundTripped = JsonSerializer.Deserialize<CurrentModelDto>(nullJson, JsonOpts);
        Assert.NotNull(roundTripped);
        Assert.Null(roundTripped.Model);

        var value = JsonSerializer.Deserialize<CurrentModelDto>(json, JsonOpts);
        Assert.Equal("claude-opus", value!.Model);
    }

    /// <summary>
    /// <see cref="ComposerModelsDto"/> serializes with camelCase <c>models</c> and the
    /// reasoning effort through the global snake_case enum converter (<c>"extra_high"</c>,
    /// never the numeric value). A null effort serializes as an explicit null.
    /// </summary>
    [Fact]
    public void ComposerModelsDto_ReasoningEffort_SerializesAsSnakeCaseAndRoundTrips()
    {
        var dto = new ComposerModelsDto(["claude-sonnet-4", "claude-opus"], ReasoningEffort.ExtraHigh);

        var json = JsonSerializer.Serialize(dto, JsonOpts);

        Assert.Equal(
            """{"models":["claude-sonnet-4","claude-opus"],"reasoningEffort":"extra_high"}""",
            json);

        var roundTripped = JsonSerializer.Deserialize<ComposerModelsDto>(json, JsonOpts);
        Assert.NotNull(roundTripped);
        Assert.Equal(dto.Models, roundTripped.Models);
        Assert.Equal(dto.ReasoningEffort, roundTripped.ReasoningEffort);
    }

    [Fact]
    public void ComposerModelsDto_NullReasoningEffort_SerializesAsExplicitNull()
    {
        var dto = new ComposerModelsDto(["claude-sonnet-4"], null);

        var json = JsonSerializer.Serialize(dto, JsonOpts);

        Assert.Equal("""{"models":["claude-sonnet-4"],"reasoningEffort":null}""", json);

        var roundTripped = JsonSerializer.Deserialize<ComposerModelsDto>(json, JsonOpts);
        Assert.NotNull(roundTripped);
        Assert.Equal(dto.Models, roundTripped.Models);
        Assert.Null(roundTripped.ReasoningEffort);
    }

    /// <summary>
    /// <see cref="SwitchResultDto"/> carries the applied model and effort; the effort
    /// serializes through the global snake_case enum converter and rejects numeric values
    /// on read (allowIntegerValues=false, matching the server).
    /// </summary>
    [Fact]
    public void SwitchResultDto_ReasoningEffort_SerializesAsSnakeCaseAndRoundTrips()
    {
        var dto = new SwitchResultDto("claude-opus", ReasoningEffort.Medium);

        var json = JsonSerializer.Serialize(dto, JsonOpts);

        Assert.Equal("""{"model":"claude-opus","reasoningEffort":"medium"}""", json);

        var roundTripped = JsonSerializer.Deserialize<SwitchResultDto>(json, JsonOpts);
        Assert.Equal(dto, roundTripped);
    }

    [Fact]
    public void SwitchResultDto_NullReasoningEffort_SerializesAsExplicitNull()
    {
        var dto = new SwitchResultDto("claude-opus", null);

        var json = JsonSerializer.Serialize(dto, JsonOpts);

        Assert.Equal("""{"model":"claude-opus","reasoningEffort":null}""", json);

        var roundTripped = JsonSerializer.Deserialize<SwitchResultDto>(json, JsonOpts);
        Assert.Equal(dto, roundTripped);
    }

    /// <summary>
    /// <see cref="CompactResultDto"/> serializes with camelCase <c>compacted</c>/<c>messageCount</c>.
    /// </summary>
    [Fact]
    public void CompactResultDto_SerializesAsCamelCaseWireShape()
    {
        var dto = new CompactResultDto(true, 7);

        var json = JsonSerializer.Serialize(dto, JsonOpts);

        Assert.Equal("""{"compacted":true,"messageCount":7}""", json);

        var roundTripped = JsonSerializer.Deserialize<CompactResultDto>(json, JsonOpts);
        Assert.Equal(dto, roundTripped);
    }

    [Fact]
    public void CompactResultDto_FalseCompacted_ZeroCount_SerializesBothProperties()
    {
        var dto = new CompactResultDto(false, 0);

        var json = JsonSerializer.Serialize(dto, JsonOpts);

        // Both members are non-nullable and always present — nothing is omitted.
        Assert.Equal("""{"compacted":false,"messageCount":0}""", json);
    }
}

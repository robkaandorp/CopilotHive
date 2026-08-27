using CopilotHive.Configuration;

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
}
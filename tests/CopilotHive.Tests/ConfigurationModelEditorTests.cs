using System.Reflection;

using CopilotHive.Components.Pages;
using ConfigurationPage = CopilotHive.Components.Pages.Configuration;
using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Services;
using CopilotHive.Tests.Services;

using Microsoft.Extensions.AI;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Focused behavioral tests for the Models-tab editor mapping in <see cref="ConfigurationPage"/>:
/// the null-preserving field population (<see cref="ConfigurationPage.PopulateModelEditorFields"/>)
/// and <see cref="ModelConfigUpdate"/> construction (<see cref="ConfigurationPage.BuildModelConfigUpdate"/>).
/// <para>
/// The component is constructed directly (no bUnit, no browser rendering — these are mapping
/// tests). The evidence chain is: the REAL editor population/update helpers → the REAL
/// <see cref="ConfigFacade"/> / <see cref="ConfigModelService"/> → the file they wrote → raw
/// YamlDotNet value inspection of that file, plus independent assertions on the ORIGINAL live
/// config object. Production <see cref="ConfigRepoManager"/>.ParseConfig and browser/rendered
/// save-button wiring are NOT exercised by these tests: persistence assertions read
/// hive-config.yaml directly from disk with the raw YamlDotNet deserializer (mirroring the
/// production deserializer's conventions) instead of calling the production parser, so its
/// primary-model normalization cannot hide accidentally persisted blank values — the bug
/// this suite exists to catch.
/// </para>
/// </summary>
[Collection("HiveIntegration")]
public sealed class ConfigurationModelEditorTests
{
    // ── Reflection plumbing ──────────────────────────────────────────────────

    private static void SetField(ConfigurationPage page, string name, string value) =>
        typeof(ConfigurationPage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(page, value);

    private static string GetField(ConfigurationPage page, string name) =>
        (string)typeof(ConfigurationPage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(page)!;

    private static void SetData(ConfigurationPage page, ModelsConfigDto data)
    {
        var field = typeof(ConfigurationPage).GetField("_modelsData", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(page, data);
    }

    /// <summary>Simulates a user selecting the given model in the named role's primary select.</summary>
    private void SelectModel(ConfigurationPage page, string field, string model) =>
        SetField(page, field, model);

    // ── Harness ──────────────────────────────────────────────────────────────

    /// <summary>Seeds a config with a catalog and the given orchestrator/worker assignments.</summary>
    private static (HiveConfigFile Config, ConfigModelService Service, string Dir) Seed(
        string? orchestrator,
        Dictionary<string, WorkerConfig>? workers = null,
        ComposerConfig? composer = null,
        string? compaction = null,
        Dictionary<string, string?>? reasoning = null,
        Dictionary<string, string?>? premiumReasoning = null)
    {
        var (config, service, dir) = ConfigFacadeTests.CreateRealService(cfg =>
        {
            if (orchestrator is not null)
                cfg.Orchestrator = new OrchestratorConfig { Model = orchestrator };
            if (composer is not null)
                cfg.Composer = composer;
            if (workers is not null)
                cfg.Workers = workers;
            cfg.Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "model-a" },
                    new ModelEntry { Name = "model-b" },
                ],
            };
            if (reasoning is not null)
            {
                foreach (var (key, value) in reasoning)
                {
                    if (key == "orchestrator")
                        cfg.Orchestrator.ReasoningEffort = value;
                    else if (key == "composer")
                    {
                        cfg.Composer ??= new ComposerConfig();
                        cfg.Composer.ReasoningEffort = value;
                    }
                    else
                    {
                        if (!cfg.Workers.TryGetValue(key, out var wc))
                            cfg.Workers[key] = wc = new WorkerConfig();
                        wc.ReasoningEffort = value;
                    }
                }
            }
            if (premiumReasoning is not null)
            {
                foreach (var (key, value) in premiumReasoning)
                {
                    if (!cfg.Workers.TryGetValue(key, out var wc))
                        cfg.Workers[key] = wc = new WorkerConfig();
                    wc.PremiumReasoningEffort = value;
                }
            }
        });
        return (config, service, dir);
    }

    /// <summary>
    /// Loads the page fields through the REAL <see cref="ConfigurationPage.PopulateModelEditorFields"/>
    /// with the REAL facade's <c>GetModels</c> projection.
    /// </summary>
    private static ConfigurationPage LoadPage(ConfigFacade facade)
    {
        var page = new ConfigurationPage();
        var result = facade.GetModels();
        Assert.True(result.Success);
        SetData(page, result.Value!);
        page.PopulateModelEditorFields(result.Value!);
        return page;
    }

    /// <summary>
    /// Saves the REAL payload produced by <see cref="ConfigurationPage.BuildModelConfigUpdate"/>
    /// through the REAL facade + service, then reads hive-config.yaml directly from the config
    /// repo directory the tests received from <see cref="Seed"/> / <see cref="ConfigFacadeTests.CreateRealService"/>
    /// and inspects it with the raw YamlDotNet deserializer (the same conventions the production
    /// <see cref="ConfigRepoManager"/> deserializer builder uses) so parser normalization cannot
    /// mask empty-string materialization.
    /// </summary>
    private static async Task<HiveConfigFile> SaveAndParsePersistedYamlAsync(
        ConfigFacade facade, ConfigurationPage page, string configRepoDir)
    {
        var update = page.BuildModelConfigUpdate();
        var result = await facade.SaveModelsAsync(update, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error ?? "save failed");
        return ParsePersistedYaml(configRepoDir);
    }

    /// <summary>
    /// Raw-value reader: reads the hive-config.yaml the real service wrote from the passed
    /// config repo directory and deserializes it with YamlDotNet using the SAME production
    /// conventions (UnderscoredNamingConvention + IgnoreUnmatchedProperties, mirroring
    /// <see cref="ConfigRepoManager"/>'s deserializer builder). This is the intentional raw
    /// reader — NOT a fallback and NOT <see cref="ConfigRepoManager"/>.ParseConfig, whose
    /// primary-model normalization would hide accidentally persisted blank values (the bug
    /// this suite exists to catch).
    /// </summary>
    private static HiveConfigFile ParsePersistedYaml(string configRepoDir)
    {
        var yaml = File.ReadAllText(Path.Combine(configRepoDir, "hive-config.yaml"));
        return DeserializeConfig(yaml);
    }

    private static HiveConfigFile DeserializeConfig(string yaml)
    {
        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<HiveConfigFile>(yaml);
    }

    /// <summary>
    /// Asserts the ORIGINAL live <see cref="HiveConfigFile"/> (the runtime authority the real
    /// service mutates in place) still has NO Workers entries and NO Composer section — i.e. a
    /// save carrying only no-ops/omissions never materialized a section. Reading the live object
    /// after the save is what makes an empty-string materialization visible even when YAML
    /// serialization or parsing would normalize it away.
    /// </summary>
    private static void AssertLiveConfigHasNoMaterializedSections(HiveConfigFile config)
    {
        Assert.Empty(config.Workers);
        Assert.Null(config.Composer);
    }

    // ── Load mapping: null-preserving display ────────────────────────────────
    /// <summary>
    /// Load with orchestrator A and null Composer/all five worker roles: the seven primary
    /// selects display ONLY their own assignment — an unset role shows the empty sentinel,
    /// never the orchestrator's model.
    /// </summary>
    [Fact]
    public void Load_OrchestratorOnly_ShowsNullAssignmentsAsSentinel()
    {
        var (config, _, dir) = Seed("model-a");
        try
        {
            var (cfg, _, _) = (config, (ConfigModelService?)null, dir);
            var facade = NewFacade(cfg, null, dir);
            var page = LoadPage(facade);

            Assert.Equal("model-a", GetField(page, "_orchestratorModel"));
            Assert.Equal(ConfigurationPage.UnsetSentinel, GetField(page, "_composerModel"));
            Assert.Equal(ConfigurationPage.UnsetSentinel, GetField(page, "_coderModel"));
            Assert.Equal(ConfigurationPage.UnsetSentinel, GetField(page, "_testerModel"));
            Assert.Equal(ConfigurationPage.UnsetSentinel, GetField(page, "_reviewerModel"));
            Assert.Equal(ConfigurationPage.UnsetSentinel, GetField(page, "_docwriterModel"));
            Assert.Equal(ConfigurationPage.UnsetSentinel, GetField(page, "_improverModel"));
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// Load with a fully missing Workers/Composer section (orchestrator also null): every
    /// primary select shows the sentinel and no section is fabricated.
    /// </summary>
    [Fact]
    public void Load_MissingWorkerAndComposerSections_ShowsSentinels()
    {
        var (config, service, dir) = Seed(orchestrator: null);
        Assert.Empty(config.Workers);
        Assert.Null(config.Composer);
        try
        {
            var facade = NewFacade(config, service, dir);
            var page = LoadPage(facade);

            Assert.Equal(ConfigurationPage.UnsetSentinel, GetField(page, "_orchestratorModel"));
            Assert.Equal(ConfigurationPage.UnsetSentinel, GetField(page, "_composerModel"));
            foreach (var role in new[] { "_coderModel", "_testerModel", "_reviewerModel", "_docwriterModel", "_improverModel" })
                Assert.Equal(ConfigurationPage.UnsetSentinel, GetField(page, role));
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    // ── Save mapping: unchanged save is a true no-op ─────────────────────────

    /// <summary>
    /// Unchanged save with everything unset: the payload contains no-ops for scalars and an
    /// EMPTY WorkerModels dictionary (unset roles omitted). Both the persisted YAML AND the
    /// ORIGINAL live config object are asserted afterwards, so parser normalization cannot mask
    /// an empty-string materialization in the live runtime state.
    /// </summary>
    [Fact]
    public async Task Save_Unchanged_AllUnset_SendsNoOpsAndPersistsNothing()
    {
        var (config, service, dir) = Seed(orchestrator: null);
        try
        {
            var facade = NewFacade(config, service, dir);
            var page = LoadPage(facade);

            var update = page.BuildModelConfigUpdate();
            Assert.Null(update.OrchestratorModel);
            Assert.Null(update.ComposerModel);
            Assert.Null(update.CompactionModel);
            // Unset worker roles are omitted entirely.
            Assert.NotNull(update.WorkerModels);
            Assert.Empty(update.WorkerModels!);
            Assert.NotNull(update.PremiumWorkerModels);
            Assert.Empty(update.PremiumWorkerModels!);

            var persisted = await SaveAndParsePersistedYamlAsync(facade, page, dir);
            Assert.Empty(persisted.Workers);
            Assert.Null(persisted.Composer);
            Assert.Null(persisted.Orchestrator.Model);

            // POST-SAVE LIVE CONFIG: the original in-memory authority the service mutates must
            // agree — no Workers/Composer section was created and no blank scalar materialized.
            AssertLiveConfigHasNoMaterializedSections(config);
            Assert.Null(config.Orchestrator.Model);
            Assert.Null(config.Models?.CompactionModel);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// Change only the orchestrator A→B: no previously unset role is assigned to B (or anything
    /// else). Asserted against the persisted YAML AND the ORIGINAL live config object.
    /// </summary>
    [Fact]
    public async Task Save_ChangeOnlyOrchestrator_DoesNotAssignUnsetRoles()
    {
        var (config, service, dir) = Seed("model-a");
        try
        {
            var facade = NewFacade(config, service, dir);
            var page = LoadPage(facade);

            // Simulate the edit: orchestrator select now shows model-b.
            SelectModel(page, "_orchestratorModel", "model-b");

            var update = page.BuildModelConfigUpdate();
            Assert.Equal("model-b", update.OrchestratorModel);
            Assert.Null(update.ComposerModel);
            // The previously unset roles must not be re-sent as model-b (or anything).
            Assert.NotNull(update.WorkerModels);
            Assert.Empty(update.WorkerModels!);

            var persisted = await SaveAndParsePersistedYamlAsync(facade, page, dir);
            Assert.Equal("model-b", persisted.Orchestrator.Model);
            Assert.Empty(persisted.Workers);
            Assert.Null(persisted.Composer);

            // POST-SAVE LIVE CONFIG: only the orchestrator changed; the missing Workers and
            // Composer sections were NOT created by the orchestrator-only edit.
            Assert.Equal("model-b", config.Orchestrator.Model);
            AssertLiveConfigHasNoMaterializedSections(config);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    // ── Save mapping: configure a previously unset role/Composer ─────────────

    /// <summary>
    /// Configuring one previously unset worker role and the Composer sends their exact names;
    /// the other four roles remain omitted. Compact theory over all five role bindings.
    /// </summary>
    [Theory]
    [InlineData("coder")]
    [InlineData("tester")]
    [InlineData("reviewer")]
    [InlineData("docwriter")]
    [InlineData("improver")]
    public async Task Save_ConfigurePreviouslyUnsetRole_PersistsExactRoleAssignment(string role)
    {
        var (config, service, dir) = Seed("model-a");
        try
        {
            var facade = NewFacade(config, service, dir);
            var page = LoadPage(facade);

            SelectModel(page, $"_{role}Model", "model-b");

            var update = page.BuildModelConfigUpdate();
            var entry = Assert.Single(update.WorkerModels!);
            Assert.Equal(role, entry.Key);
            Assert.Equal("model-b", entry.Value);

            var persisted = await SaveAndParsePersistedYamlAsync(facade, page, dir);
            Assert.Equal("model-b", persisted.GetModelForRole(role));
            // The other four roles were never materialized.
            foreach (var other in new[] { "coder", "tester", "reviewer", "docwriter", "improver" }.Where(r => r != role))
                Assert.Null(persisted.GetModelForRole(other));
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// Configuring a previously unset Composer persists the Composer section with the chosen
    /// model — the section is created only by a real assignment, not by untouched blanks.
    /// </summary>
    [Fact]
    public async Task Save_ConfigurePreviouslyUnsetComposer_CreatesSectionWithExactModel()
    {
        var (config, service, dir) = Seed("model-a");
        try
        {
            var facade = NewFacade(config, service, dir);
            var page = LoadPage(facade);

            SelectModel(page, "_composerModel", "model-b");

            var update = page.BuildModelConfigUpdate();
            Assert.Equal("model-b", update.ComposerModel);

            var persisted = await SaveAndParsePersistedYamlAsync(facade, page, dir);
            Assert.Equal("model-b", persisted.Composer!.Model);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    // ── Save mapping: explicit equality stays explicit ───────────────────────

    /// <summary>
    /// A role explicitly assigned the orchestrator's model (A) must round-trip as an explicit
    /// per-role assignment even when the orchestrator changes to B — equality must NOT be
    /// inferred as inheritance.
    /// </summary>
    [Fact]
    public async Task Save_ExplicitRoleModelEqualToOrchestrator_StaysExplicit()
    {
        var (config, service, dir) = Seed(
            "model-a",
            workers: new Dictionary<string, WorkerConfig> { ["coder"] = new() { Model = "model-a" } });
        try
        {
            var facade = NewFacade(config, service, dir);
            var page = LoadPage(facade);

            // Untouched load: coder shows model-a explicitly (not a sentinel).
            Assert.Equal("model-a", GetField(page, "_coderModel"));

            // User changes only the orchestrator to model-b.
            SelectModel(page, "_orchestratorModel", "model-b");

            var update = page.BuildModelConfigUpdate();
            var entry = Assert.Single(update.WorkerModels!);
            Assert.Equal("coder", entry.Key);
            Assert.Equal("model-a", entry.Value);

            var persisted = await SaveAndParsePersistedYamlAsync(facade, page, dir);
            Assert.Equal("model-b", persisted.Orchestrator.Model);
            Assert.Equal("model-a", persisted.GetModelForRole("coder"));
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// A previously explicit nonmatching role assignment stays unchanged on an untouched save:
    /// the payload carries the role with its exact prior name.
    /// </summary>
    [Fact]
    public async Task Save_PreviouslyExplicitNonMatchingRole_StaysUnchanged()
    {
        var (config, service, dir) = Seed(
            "model-a",
            workers: new Dictionary<string, WorkerConfig>
            {
                ["coder"] = new() { Model = "model-b" },
                ["tester"] = new() { Model = "model-a" },
            });
        try
        {
            var facade = NewFacade(config, service, dir);
            var page = LoadPage(facade);

            // Unchanged save.
            var update = page.BuildModelConfigUpdate();
            Assert.Equal(2, update.WorkerModels!.Count);
            Assert.Equal("model-b", update.WorkerModels!["coder"]);
            Assert.Equal("model-a", update.WorkerModels!["tester"]);

            var persisted = await SaveAndParsePersistedYamlAsync(facade, page, dir);
            Assert.Equal("model-b", persisted.GetModelForRole("coder"));
            Assert.Equal("model-a", persisted.GetModelForRole("tester"));
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    // ── Reasoning preservation ───────────────────────────────────────────────

    /// <summary>
    /// An unset reasoning assignment binds the empty sentinel (NOT 'none') and an untouched save
    /// leaves the adjacent nullable fields unmaterialized — asserted in the persisted YAML AND in
    /// the ORIGINAL live config object.
    /// </summary>
    [Fact]
    public async Task Save_UnsetReasoning_DoesNotMaterializeNullableFields()
    {
        var (config, service, dir) = Seed("model-a");
        try
        {
            var facade = NewFacade(config, service, dir);
            var page = LoadPage(facade);

            // Sentinel, never 'none'.
            Assert.Equal(ConfigurationPage.UnsetSentinel, GetField(page, "_orchestratorReasoningEffort"));
            Assert.Equal(ConfigurationPage.UnsetSentinel, GetField(page, "_composerReasoningEffort"));
            Assert.Equal(ConfigurationPage.UnsetSentinel, GetField(page, "_coderReasoningEffort"));

            var update = page.BuildModelConfigUpdate();
            Assert.Null(update.OrchestratorReasoningEffort);
            Assert.Null(update.ComposerReasoningEffort);
            Assert.Empty(update.WorkerReasoningEffort!);
            Assert.Empty(update.WorkerPremiumReasoningEffort!);

            var persisted = await SaveAndParsePersistedYamlAsync(facade, page, dir);
            Assert.Null(persisted.Orchestrator.ReasoningEffort);
            Assert.Null(persisted.Composer);
            Assert.Empty(persisted.Workers);

            // POST-SAVE LIVE CONFIG: nullable reasoning fields stayed null and no Workers /
            // Composer section was created by the untouched save.
            Assert.Null(config.Orchestrator.ReasoningEffort);
            AssertLiveConfigHasNoMaterializedSections(config);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// Explicit <see cref="ReasoningEffort.None"/> is a REAL configured value: it binds the
    /// 'none' wire value (distinct from the sentinel) and round-trips through the real service.
    /// </summary>
    [Fact]
    public async Task Load_ExplicitNoneReasoning_BindsNoneWireAndRoundTrips()
    {
        var (config, service, dir) = Seed(
            "model-a",
            reasoning: new Dictionary<string, string?> { ["coder"] = "none" });
        try
        {
            var facade = NewFacade(config, service, dir);
            var page = LoadPage(facade);

            Assert.Equal("none", GetField(page, "_coderReasoningEffort"));
            Assert.Equal(ConfigurationPage.UnsetSentinel, GetField(page, "_testerReasoningEffort"));

            var update = page.BuildModelConfigUpdate();
            var entry = Assert.Single(update.WorkerReasoningEffort!);
            Assert.Equal("coder", entry.Key);
            Assert.Equal(ReasoningEffort.None, entry.Value);

            var persisted = await SaveAndParsePersistedYamlAsync(facade, page, dir);
            Assert.Equal("none", persisted.Workers["coder"].ReasoningEffort);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// Selecting a real reasoning level for one assignment persists exactly that level through
    /// the real service, without touching unset siblings.
    /// </summary>
    [Fact]
    public async Task Save_SelectExplicitReasoningLevel_PersistsOnlyThatAssignment()
    {
        var (config, service, dir) = Seed("model-a");
        try
        {
            var facade = NewFacade(config, service, dir);
            var page = LoadPage(facade);

            SetField(page, "_reviewerReasoningEffort", ReasoningEffortOptions.WireValue(ReasoningEffort.High));

            var update = page.BuildModelConfigUpdate();
            var entry = Assert.Single(update.WorkerReasoningEffort!);
            Assert.Equal("reviewer", entry.Key);
            Assert.Equal(ReasoningEffort.High, entry.Value);

            var persisted = await SaveAndParsePersistedYamlAsync(facade, page, dir);
            Assert.Equal("high", persisted.Workers["reviewer"].ReasoningEffort);
            // Sibling assignments stay unmaterialized.
            Assert.Null(persisted.Orchestrator.ReasoningEffort);
            Assert.DoesNotContain(persisted.Workers, kv => kv.Key != "reviewer");
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    // ── Premium / compaction preservation ────────────────────────────────────

    /// <summary>
    /// Null premium/compaction baselines: displayed empties are omitted (worker keys) or sent
    /// as null (compaction), so untouched blanks never create worker sections or a compaction
    /// entry — asserted in the persisted YAML AND in the ORIGINAL live config object.
    /// </summary>
    [Fact]
    public async Task Save_NullPremiumAndCompaction_AreOmittedOrNull()
    {
        var (config, service, dir) = Seed("model-a");
        try
        {
            var facade = NewFacade(config, service, dir);
            var page = LoadPage(facade);

            Assert.Equal(ConfigurationPage.UnsetSentinel, GetField(page, "_coderPremiumModel"));
            Assert.Equal(ConfigurationPage.UnsetSentinel, GetField(page, "_compactionModel"));

            var update = page.BuildModelConfigUpdate();
            Assert.Empty(update.PremiumWorkerModels!);
            Assert.Null(update.CompactionModel);

            var persisted = await SaveAndParsePersistedYamlAsync(facade, page, dir);
            Assert.Empty(persisted.Workers);
            Assert.Null(persisted.Models?.CompactionModel);

            // POST-SAVE LIVE CONFIG: no worker section was created for the blank premiums and
            // the compaction model stayed null (not an empty string).
            AssertLiveConfigHasNoMaterializedSections(config);
            Assert.Null(config.Models?.CompactionModel);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// Clearing an existing premium or compaction selection still works under its existing
    /// empty-string contract: the displayed empty over a CONCRETE baseline sends "" (not a
    /// no-op), the '(none)' / '(use main model)' choices stay functional.
    /// </summary>
    [Fact]
    public async Task Save_ClearExistingPremiumAndCompaction_SendsEmptyStringContract()
    {
        var (config, service, dir) = Seed(
            "model-a",
            workers: new Dictionary<string, WorkerConfig> { ["coder"] = new() { PremiumModel = "model-b" } });
        config.SetCompactionModel("model-a");
        try
        {
            var facade = NewFacade(config, service, dir);
            var page = LoadPage(facade);

            Assert.Equal("model-b", GetField(page, "_coderPremiumModel"));
            Assert.Equal("model-a", GetField(page, "_compactionModel"));

            // Simulate the user clearing both selects (choosing '(none)' / '(use main model)').
            SetField(page, "_coderPremiumModel", "");
            SetField(page, "_compactionModel", "");

            var update = page.BuildModelConfigUpdate();
            var premium = Assert.Single(update.PremiumWorkerModels!);
            Assert.Equal("coder", premium.Key);
            Assert.Equal("", premium.Value);
            Assert.Equal("", update.CompactionModel);

            var persisted = await SaveAndParsePersistedYamlAsync(facade, page, dir);
            // The service's blank normalization clears premium/compaction: the effective
            // GetPremiumModelForRole contract treats blank as unset (null). The persisted
            // premium string may retain the empty form (service normalization territory);
            // the contract under test is that the clearing update was accepted.
            Assert.Equal("", persisted.Workers["coder"].PremiumModel);
            Assert.Null(persisted.GetPremiumModelForRole("coder"));
            Assert.Equal("", persisted.Models?.CompactionModel);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// Missing worker sections must not be created merely by untouched premium blanks: an
    /// unset premium over a role with no config entry at all is omitted from the payload, and
    /// neither the persisted YAML nor the ORIGINAL live config grows a worker section.
    /// </summary>
    [Fact]
    public async Task Save_PremiumOverMissingWorkerSection_IsOmitted()
    {
        var (config, service, dir) = Seed("model-a");
        try
        {
            var facade = NewFacade(config, service, dir);
            var page = LoadPage(facade);

            var update = page.BuildModelConfigUpdate();
            Assert.DoesNotContain("tester", update.PremiumWorkerModels!.Keys);
            Assert.DoesNotContain("coder", update.PremiumWorkerModels!.Keys);

            var persisted = await SaveAndParsePersistedYamlAsync(facade, page, dir);
            Assert.Empty(persisted.Workers);

            // POST-SAVE LIVE CONFIG: the missing sections were not created in the runtime
            // authority either (a YAML-only assertion could be masked by serializer omission).
            AssertLiveConfigHasNoMaterializedSections(config);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    // ── Primary vs premium/compaction contract split ─────────────────────────

    /// <summary>
    /// REGRESSION GUARD for the contract split: an empty primary display over a PREVIOUSLY
    /// CONCRETE baseline must be a no-op (orchestrator/Composer → <c>null</c>, worker role
    /// omitted), NOT the premium/compaction empty-string clear. Clearing an existing primary
    /// model is not a feature of this editor, so reintroducing the baseline-aware clearing rule
    /// for primaries fails here: the live config AND the parsed YAML would lose the values.
    /// </summary>
    [Fact]
    public async Task Save_EmptyPrimaryOverConcreteBaseline_IsNoOpNotClear()
    {
        var (config, service, dir) = Seed(
            "model-a",
            workers: new Dictionary<string, WorkerConfig>
            {
                ["coder"] = new() { Model = "model-b" },
                ["tester"] = new() { Model = "model-a" },
            },
            composer: new ComposerConfig { Model = "model-b" });
        try
        {
            var facade = NewFacade(config, service, dir);
            var page = LoadPage(facade);

            // Loaded concrete values.
            Assert.Equal("model-a", GetField(page, "_orchestratorModel"));
            Assert.Equal("model-b", GetField(page, "_composerModel"));
            Assert.Equal("model-b", GetField(page, "_coderModel"));
            Assert.Equal("model-a", GetField(page, "_testerModel"));

            // Force every primary select to the empty sentinel over its concrete baseline.
            SetField(page, "_orchestratorModel", ConfigurationPage.UnsetSentinel);
            SetField(page, "_composerModel", ConfigurationPage.UnsetSentinel);
            SetField(page, "_coderModel", ConfigurationPage.UnsetSentinel);
            SetField(page, "_testerModel", ConfigurationPage.UnsetSentinel);

            var update = page.BuildModelConfigUpdate();
            // Primary scalars: unconditional no-op — NOT "".
            Assert.Null(update.OrchestratorModel);
            Assert.Null(update.ComposerModel);
            // Primary worker roles: ALWAYS omitted — never an empty-string clear.
            Assert.Empty(update.WorkerModels!);
            Assert.DoesNotContain("coder", update.WorkerModels!.Keys);
            Assert.DoesNotContain("tester", update.WorkerModels!.Keys);

            var persisted = await SaveAndParsePersistedYamlAsync(facade, page, dir);
            // Nothing was cleared in the persisted YAML.
            Assert.Equal("model-a", persisted.Orchestrator.Model);
            Assert.Equal("model-b", persisted.Composer!.Model);
            Assert.Equal("model-b", persisted.GetModelForRole("coder"));
            Assert.Equal("model-a", persisted.GetModelForRole("tester"));

            // POST-SAVE LIVE CONFIG: the runtime authority kept every primary assignment too.
            Assert.Equal("model-a", config.Orchestrator.Model);
            Assert.Equal("model-b", config.Composer!.Model);
            Assert.Equal("model-b", config.GetModelForRole("coder"));
            Assert.Equal("model-a", config.GetModelForRole("tester"));
            Assert.Equal("model-b", config.Workers["coder"].Model);
            Assert.Equal("model-a", config.Workers["tester"].Model);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// The premium/compaction clearing contract must remain baseline-aware in the SAME save in
    /// which primaries stay untouched: an empty premium/compaction over a concrete baseline
    /// still clears (empty string), while the empty primaries beside them are no-ops. This
    /// fails if the two contracts are re-merged in either direction.
    /// </summary>
    [Fact]
    public async Task Save_EmptyPrimaryAndEmptyPremium_ClearOnlyPremium()
    {
        var (config, service, dir) = Seed(
            "model-a",
            workers: new Dictionary<string, WorkerConfig>
            {
                ["coder"] = new() { Model = "model-b", PremiumModel = "model-a" },
            });
        config.SetCompactionModel("model-b");
        try
        {
            var facade = NewFacade(config, service, dir);
            var page = LoadPage(facade);

            SetField(page, "_coderModel", ConfigurationPage.UnsetSentinel);
            SetField(page, "_coderPremiumModel", ConfigurationPage.UnsetSentinel);
            SetField(page, "_compactionModel", ConfigurationPage.UnsetSentinel);

            var update = page.BuildModelConfigUpdate();
            Assert.Empty(update.WorkerModels!);
            var premium = Assert.Single(update.PremiumWorkerModels!);
            Assert.Equal("coder", premium.Key);
            Assert.Equal("", premium.Value);
            Assert.Equal("", update.CompactionModel);

            var persisted = await SaveAndParsePersistedYamlAsync(facade, page, dir);
            Assert.Equal("model-b", persisted.GetModelForRole("coder"));
            Assert.Null(persisted.GetPremiumModelForRole("coder"));

            // POST-SAVE LIVE CONFIG: primary kept, premium/compaction cleared.
            Assert.Equal("model-b", config.Workers["coder"].Model);
            Assert.Equal("", config.Workers["coder"].PremiumModel);
            Assert.Null(config.GetPremiumModelForRole("coder"));
            Assert.Equal("", config.Models?.CompactionModel);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    // ── Helpers: real facade construction ────────────────────────────────────

    /// <summary>
    /// Builds a REAL <see cref="ConfigFacade"/> over the given config/service (the same wiring
    /// the production DI container performs) without booting the web host — the Models-tab
    /// mapping tests never touch HTTP.
    /// </summary>
    private static ConfigFacade NewFacade(
        HiveConfigFile config, ConfigModelService? service, string dir)
    {
        var repo = new FakeConfigRepoManager("https://example.com/config.git", dir);
        if (service is null)
        {
            service = new ConfigModelService(config, repo, Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigModelService>.Instance);
        }
        return new ConfigFacade(
            config,
            service,
            discovery: null,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigFacade>.Instance,
            repoManager: null);
    }
}
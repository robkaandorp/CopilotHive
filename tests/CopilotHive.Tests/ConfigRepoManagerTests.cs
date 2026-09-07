using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using CopilotHive.Configuration;
using CopilotHive.Services;
using CopilotHive.Workers;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CopilotHive.Tests;

public class ConfigRepoManagerTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigRepoManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-cfgtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── YAML parsing ─────────────────────────────────────────────────────────

    [Fact]
    public void ParseConfig_FullConfig_ParsesAllFields()
    {
        const string yaml = """
            version: "2.0"
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
                default_branch: develop
              - name: my-api
                url: https://github.com/org/my-api.git
            workers:
              coder:
                model: claude-opus-4.6
              tester:
                model: gpt-5-mini
            orchestrator:
              model: gpt-5.4
              max_iterations: 5
              max_retries_per_task: 2
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.Equal("2.0", config.Version);
        Assert.Equal(2, config.Repositories.Count);
        Assert.Equal("my-app", config.Repositories[0].Name);
        Assert.Equal("https://github.com/org/my-app.git", config.Repositories[0].Url);
        Assert.Equal("develop", config.Repositories[0].DefaultBranch);
        Assert.Equal("my-api", config.Repositories[1].Name);
        Assert.Equal("main", config.Repositories[1].DefaultBranch); // default

        Assert.Equal(2, config.Workers.Count);
        Assert.Equal("claude-opus-4.6", config.Workers["coder"].Model);
        Assert.Equal("gpt-5-mini", config.Workers["tester"].Model);

        Assert.Equal("gpt-5.4", config.Orchestrator.Model);
        Assert.Equal(5, config.Orchestrator.MaxIterations);
        Assert.Equal(2, config.Orchestrator.MaxRetriesPerTask);
    }

    [Fact]
    public void ParseConfig_MinimalConfig_UsesDefaults()
    {
        const string yaml = """
            version: "1.0"
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.Equal("1.0", config.Version);
        Assert.Empty(config.Repositories);
        Assert.Empty(config.Workers);
        Assert.Null(config.Orchestrator.Model);
        Assert.Equal(10, config.Orchestrator.MaxIterations);
        Assert.Equal(3, config.Orchestrator.MaxRetriesPerTask);
    }

    [Fact]
    public void ParseConfig_EmptyYaml_ReturnsDefaults()
    {
        var config = ConfigRepoManager.ParseConfig("");

        Assert.Equal("1.0", config.Version);
        Assert.Empty(config.Repositories);
        Assert.Empty(config.Workers);
    }

    [Fact]
    public void ParseConfig_MissingFields_FallsBackToDefaults()
    {
        const string yaml = """
            repositories:
              - name: only-repo
                url: https://github.com/org/only-repo.git
            orchestrator:
              max_iterations: 20
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.Single(config.Repositories);
        Assert.Equal("main", config.Repositories[0].DefaultBranch);
        Assert.Null(config.Orchestrator.Model);
        Assert.Equal(20, config.Orchestrator.MaxIterations);
        Assert.Equal(3, config.Orchestrator.MaxRetriesPerTask);
    }

    [Fact]
    public void ParseConfig_MarksInstanceIsConfiguredTrue()
    {
        const string yaml = """
            version: "1.0"
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        // A repo-parsed config is marked IsConfigured = true (vs. the no-repo fallback false).
        Assert.True(config.IsConfigured);
    }

    [Fact]
    public void ParseConfig_UnknownFields_AreIgnored()
    {
        const string yaml = """
            version: "1.0"
            some_future_field: true
            orchestrator:
              model: gpt-5.4
              unknown_setting: 42
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.Equal("gpt-5.4", config.Orchestrator.Model);
    }

    // ── Nullable models + blank→null normalization (Slice 3a) ────────────────

    [Fact]
    public void ParseConfig_OrchestratorModelOmitted_IsNull()
    {
        const string yaml = """
            version: "1.0"
            orchestrator:
              max_iterations: 5
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.Null(config.Orchestrator.Model);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ParseConfig_OrchestratorBlankModel_NormalizesToNull(string blank)
    {
        var yaml = $"""
            version: "1.0"
            orchestrator:
              model: "{blank}"
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.Null(config.Orchestrator.Model);
    }

    [Fact]
    public void ParseConfig_WorkerRoleModelOmitted_IsNull()
    {
        const string yaml = """
            version: "1.0"
            workers:
              coder:
                context_window: 100000
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.Null(config.Workers["coder"].Model);
    }

    [Fact]
    public void ParseConfig_WorkerRoleBlankModel_NormalizesToNull()
    {
        const string yaml = """
            version: "1.0"
            workers:
              coder:
                model: "   "
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.Null(config.Workers["coder"].Model);
    }

    [Fact]
    public void ParseConfig_ComposerModelOmitted_IsNull()
    {
        const string yaml = """
            version: "1.0"
            composer:
              max_steps: 50
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.NotNull(config.Composer);
        Assert.Null(config.Composer!.Model);
    }

    [Fact]
    public void ParseConfig_ComposerBlankModel_NormalizesToNull()
    {
        const string yaml = """
            version: "1.0"
            composer:
              model: ""
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.NotNull(config.Composer);
        Assert.Null(config.Composer!.Model);
    }

    // ── Retired composer.models key (Slice 3a) ─────────────────────────────────

    [Fact]
    public void ParseConfig_ComposerModelsKey_ThrowsFatalNamingTheKey()
    {
        const string yaml = """
            version: "1.0"
            composer:
              model: copilot/composer-model
              models:
                - copilot/composer-model
                - copilot/alt-model
            """;

        var ex = Assert.Throws<YamlDotNet.Core.YamlException>(() => ConfigRepoManager.ParseConfig(yaml));
        Assert.Contains("composer.models", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseConfig_ComposerModelsKeyEmptyList_ThrowsFatalNamingTheKey()
    {
        const string yaml = """
            version: "1.0"
            composer:
              model: copilot/composer-model
              models: []
            """;

        var ex = Assert.Throws<YamlDotNet.Core.YamlException>(() => ConfigRepoManager.ParseConfig(yaml));
        Assert.Contains("composer.models", ex.Message, StringComparison.Ordinal);
    }

    // ── Models subtree YAML compatibility (ownership boundary at HiveConfigFile.Models) ──

    /// <summary>
    /// Omitted / explicit-null / empty / populated Models sections keep value semantics through
    /// <see cref="ConfigRepoManager.ParseConfig"/> and the disk round trip: omitted stays null,
    /// an explicit <c>models: null</c> stays null, an empty models mapping stays a non-null empty
    /// ModelsConfig, and a populated section carries compaction model and BOTH catalogs.
    /// Ownership is at <see cref="HiveConfigFile.Models"/>: each parsed instance gets its own
    /// detached subtree.
    /// </summary>
    [Theory]
    [InlineData("omitted")]
    [InlineData("explicitNull")]
    [InlineData("empty")]
    [InlineData("populated")]
    public void ParseConfig_ModelsSectionVariants_KeepValueSemantics(string variant)
    {
        var yaml = variant switch
        {
            "omitted" => """
                version: "1.0"
                orchestrator:
                  model: orch-model
                """,
            // Explicit null node (distinct from an omitted key): value semantics must keep it null.
            "explicitNull" => """
                version: "1.0"
                models: null
                orchestrator:
                  model: orch-model
                """,
            "empty" => """
                version: "1.0"
                models: {}
                """,
            _ => """
                version: "1.0"
                models:
                  compaction_model: compactor
                  available_models:
                    - name: model-a
                      context_window: 1000
                      description: first
                      supports_vision: true
                    - name: model-b
                """,
        };

        var config = ConfigRepoManager.ParseConfig(yaml);

        if (variant is "omitted" or "explicitNull")
        {
            // Null stays null on BOTH the omitted and the explicit-null input.
            Assert.Null(config.Models);
            Assert.Null(config.GetCompactionModel());
            Assert.Null(config.GetAvailableModelsSnapshot());
            Assert.Null(config.GetSubAgentModelsSnapshot());
            Assert.Empty(config.GetSubAgentModels());
            // The rest of the document still parsed (the null models node is not fatal).
            Assert.Equal("orch-model", config.Orchestrator.Model);
            return;
        }

        Assert.NotNull(config.Models);
        if (variant == "empty")
        {
            Assert.Null(config.Models!.CompactionModel);
            Assert.Null(config.Models.AvailableModels);
            Assert.Null(config.Models.SubAgentModels);
            Assert.Null(config.GetCompactionModel());
            return;
        }

        Assert.Equal("compactor", config.Models!.CompactionModel);
        var available = config.GetAvailableModelsSnapshot()!;
        Assert.Equal(2, available.Count);
        Assert.Equal("model-a", available[0].Name);
        Assert.Equal(1000, available[0].ContextWindow);
        Assert.Equal("first", available[0].Description);
        Assert.True(available[0].SupportsVision);
        Assert.Equal("model-b", available[1].Name);
        Assert.Null(available[1].ContextWindow);
        Assert.Null(available[1].Description);
        Assert.Null(available[1].SupportsVision);
        Assert.Null(config.GetSubAgentModelsSnapshot());
    }

    /// <summary>
    /// Catalog list variants keep value semantics: null lists stay null, empty lists stay empty,
    /// and null entries inside a list are preserved in position (with their null fields).
    /// </summary>
    [Theory]
    [InlineData("nullLists")]
    [InlineData("emptyLists")]
    [InlineData("nullEntries")]
    public void ParseConfig_ModelsListVariants_KeepValueSemantics(string variant)
    {
        var yaml = variant switch
        {
            "nullLists" => """
                version: "1.0"
                models:
                  compaction_model: cm
                  available_models:
                  sub_agent_models:
                """,
            "emptyLists" => """
                version: "1.0"
                models:
                  available_models: []
                  sub_agent_models: []
                """,
            _ => """
                version: "1.0"
                models:
                  available_models:
                    - name: real-a
                    -
                    - name: real-b
                  sub_agent_models:
                    - name: sub-a
                    -
                """,
        };

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.NotNull(config.Models);
        if (variant == "nullLists")
        {
            Assert.Null(config.Models!.AvailableModels);
            Assert.Null(config.Models.SubAgentModels);
            Assert.Equal("cm", config.GetCompactionModel());
            return;
        }

        if (variant == "emptyLists")
        {
            Assert.NotNull(config.Models!.AvailableModels);
            Assert.Empty(config.Models.AvailableModels);
            Assert.NotNull(config.Models.SubAgentModels);
            Assert.Empty(config.Models.SubAgentModels);
            return;
        }

        // null entries: positions and neighbors preserved exactly as stored.
        var avail = config.Models!.AvailableModels!;
        Assert.Equal(3, avail.Count);
        Assert.Equal("real-a", avail[0].Name);
        Assert.Null(avail[1]);
        Assert.Equal("real-b", avail[2].Name);
        var sub = config.Models.SubAgentModels!;
        Assert.Equal(2, sub.Count);
        Assert.Equal("sub-a", sub[0].Name);
        Assert.Null(sub[1]);
    }

    /// <summary>
    /// Backward-anchor YAML with a real <c>&amp;anchor</c> on an available entry that the curated
    /// list references via a real <c>*alias</c> node. Pinned YamlDotNet 18.1.0 behavior (validated
    /// here, not by reading upstream source): an aliased mapping binds to the SAME CLR instance in
    /// both catalogs, and every field value is carried across the alias.
    /// </summary>
    private const string EntryAnchorYaml = """
        version: "1.0"
        models:
          compaction_model: compactor
          available_models:
            - &shared
              name: shared-model
              context_window: 976000
              reasoning_effort: medium
              description: shared-desc
              supports_vision: true
            - name: solo-avail
              context_window: 4096
              reasoning_effort: low
              description: solo-desc
              supports_vision: false
          sub_agent_models:
            - *shared
            - name: curated-solo
              context_window: 2048
              reasoning_effort: high
              description: curated-desc
              supports_vision: false
        """;

    /// <summary>
    /// The SAME document as <see cref="EntryAnchorYaml"/> with the alias node replaced by a
    /// duplicated mapping carrying identical content. Used as the mechanism-observability control:
    /// the parsed VALUES are identical, but the alias binding (shared instance) is not present, so
    /// a test that only compared values could not distinguish real alias binding from plain
    /// content duplication.
    /// </summary>
    private const string EntryAnchorDuplicatedYaml = """
        version: "1.0"
        models:
          compaction_model: compactor
          available_models:
            - name: shared-model
              context_window: 976000
              reasoning_effort: medium
              description: shared-desc
              supports_vision: true
            - name: solo-avail
              context_window: 4096
              reasoning_effort: low
              description: solo-desc
              supports_vision: false
          sub_agent_models:
            - name: shared-model
              context_window: 976000
              reasoning_effort: medium
              description: shared-desc
              supports_vision: true
            - name: curated-solo
              context_window: 2048
              reasoning_effort: high
              description: curated-desc
              supports_vision: false
        """;

    /// <summary>
    /// The second required alias shape: one whole catalog LIST carries the <c>&amp;anchor</c> and
    /// the other catalog is a real <c>*alias</c> reference to it.
    /// </summary>
    private const string ListAnchorYaml = """
        version: "1.0"
        models:
          compaction_model: list-compactor
          available_models: &catalog
            - name: list-a
              context_window: 111
              reasoning_effort: low
              description: list-a-desc
              supports_vision: true
            - name: list-b
              context_window: 222
              reasoning_effort: high
              description: list-b-desc
              supports_vision: false
          sub_agent_models: *catalog
        """;

    /// <summary>
    /// Asserts the COMPLETE ordered field values of a catalog list: name, context window,
    /// reasoning effort, description and supports-vision, in list order.
    /// </summary>
    private static void AssertOrderedEntries(
        IReadOnlyList<ModelEntry>? actual,
        params (string Name, int? ContextWindow, string? ReasoningEffort, string? Description, bool? SupportsVision)[] expected)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Length, actual!.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Name, actual[i].Name);
            Assert.Equal(expected[i].ContextWindow, actual[i].ContextWindow);
            Assert.Equal(expected[i].ReasoningEffort, actual[i].ReasoningEffort);
            Assert.Equal(expected[i].Description, actual[i].Description);
            Assert.Equal(expected[i].SupportsVision, actual[i].SupportsVision);
        }
    }

    /// <summary>
    /// The <c>models:</c> subtree of <see cref="EntryAnchorYaml"/> — identical anchor/alias
    /// content, bound directly to a plain <see cref="ModelsConfig"/> DTO so the raw alias
    /// binding is observable without the ownership boundary's cloning in the way.
    /// </summary>
    private const string EntryAnchorModelsYaml = """
        compaction_model: compactor
        available_models:
          - &shared
            name: shared-model
            context_window: 976000
            reasoning_effort: medium
            description: shared-desc
            supports_vision: true
          - name: solo-avail
            context_window: 4096
            reasoning_effort: low
            description: solo-desc
            supports_vision: false
        sub_agent_models:
          - *shared
          - name: curated-solo
            context_window: 2048
            reasoning_effort: high
            description: curated-desc
            supports_vision: false
        """;

    /// <summary>The duplicated-content control for <see cref="EntryAnchorModelsYaml"/> (no alias).</summary>
    private const string EntryAnchorDuplicatedModelsYaml = """
        compaction_model: compactor
        available_models:
          - name: shared-model
            context_window: 976000
            reasoning_effort: medium
            description: shared-desc
            supports_vision: true
          - name: solo-avail
            context_window: 4096
            reasoning_effort: low
            description: solo-desc
            supports_vision: false
        sub_agent_models:
          - name: shared-model
            context_window: 976000
            reasoning_effort: medium
            description: shared-desc
            supports_vision: true
          - name: curated-solo
            context_window: 2048
            reasoning_effort: high
            description: curated-desc
            supports_vision: false
        """;

    /// <summary>The <c>models:</c> subtree of <see cref="ListAnchorYaml"/> (whole-list alias).</summary>
    private const string ListAnchorModelsYaml = """
        compaction_model: list-compactor
        available_models: &catalog
          - name: list-a
            context_window: 111
            reasoning_effort: low
            description: list-a-desc
            supports_vision: true
          - name: list-b
            context_window: 222
            reasoning_effort: high
            description: list-b-desc
            supports_vision: false
        sub_agent_models: *catalog
        """;

    /// <summary>
    /// A real YAML backward ANCHOR/ALIAS on an available ENTRY referenced by the curated list,
    /// routed through <see cref="ConfigRepoManager.ParseConfig"/> AND the disk round trip
    /// (WriteConfigAsync → read file → ParseConfig). Every field of BOTH catalogs is asserted in
    /// order. Subsequent owner isolation is proven against values retained BEFORE any owner
    /// update: parsed results are mutated (list, entry, fields) and the owner is unaffected, then
    /// owner mutations are made and the retained parsed results are unaffected.
    /// <para>
    /// MECHANISM-OBSERVABLE: <see cref="EntryAnchorDuplicatedYaml"/> is the same document with the
    /// alias replaced by a duplicated mapping. The alias document binds ONE shared CLR instance
    /// across the two catalogs while the duplicated document binds two distinct instances — the
    /// assertions below distinguish the two, so this is genuine alias binding, not merely equal
    /// content. Both documents yield identical VALUES (no data is lost through the alias).
    /// </para>
    /// </summary>
    [Fact]
    public async Task ParseConfig_ModelsEntryAnchorAlias_OrderedFieldValues_AndOwnerIsolation()
    {
        var config = ConfigRepoManager.ParseConfig(EntryAnchorYaml);

        // ── Complete ordered field values for BOTH catalogs, straight from ParseConfig. ─────
        AssertOrderedEntries(
            config.GetAvailableModelsSnapshot(),
            ("shared-model", 976000, "medium", "shared-desc", true),
            ("solo-avail", 4096, "low", "solo-desc", false));
        AssertOrderedEntries(
            config.GetSubAgentModelsSnapshot(),
            // The aliased entry carries EVERY field across the alias — nothing is lost.
            ("shared-model", 976000, "medium", "shared-desc", true),
            ("curated-solo", 2048, "high", "curated-desc", false));
        Assert.Equal("compactor", config.GetCompactionModel());

        // ── MECHANISM OBSERVABILITY: the alias really binds, and is not merely equal content. ──
        // (i) At the YAML representation layer: the alias document makes the first curated entry
        // THE SAME NODE as the first available entry; the duplicated-content control does not.
        Assert.True(CuratedFirstEntryIsAvailableFirstEntryNode(EntryAnchorYaml),
            "The anchor document did not bind the curated entry to the available entry — the alias is not a real YAML alias node.");
        Assert.False(CuratedFirstEntryIsAvailableFirstEntryNode(EntryAnchorDuplicatedYaml),
            "The duplicated-content control unexpectedly shares a node — the scratch copy is not a plain duplication.");
        // (ii) At the object-binding layer, into a plain DTO (no ownership boundary in the way):
        // the alias yields ONE shared ModelEntry instance across the two catalogs, while the
        // duplicated control yields two distinct instances. A test that only compared values
        // could not tell these apart.
        var aliasBound = RawDeserializeModels(EntryAnchorModelsYaml);
        var duplicatedBound = RawDeserializeModels(EntryAnchorDuplicatedModelsYaml);
        Assert.Same(aliasBound.AvailableModels![0], aliasBound.SubAgentModels![0]);
        Assert.NotSame(duplicatedBound.AvailableModels![0], duplicatedBound.SubAgentModels![0]);
        // ...and both bindings carry identical VALUES (the alias loses no data).
        AssertOrderedEntries(
            duplicatedBound.SubAgentModels,
            ("shared-model", 976000, "medium", "shared-desc", true),
            ("curated-solo", 2048, "high", "curated-desc", false));
        AssertOrderedEntries(
            aliasBound.SubAgentModels,
            ("shared-model", 976000, "medium", "shared-desc", true),
            ("curated-solo", 2048, "high", "curated-desc", false));

        // ── Disk round trip: write the parsed config, read the file back, re-parse. ─────────
        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        await manager.WriteConfigAsync(config, TestContext.Current.CancellationToken);
        var disk = await ReadWrittenYamlAsync(manager);
        var fromDisk = ConfigRepoManager.ParseConfig(disk);
        AssertOrderedEntries(
            fromDisk.GetAvailableModelsSnapshot(),
            ("shared-model", 976000, "medium", "shared-desc", true),
            ("solo-avail", 4096, "low", "solo-desc", false));
        AssertOrderedEntries(
            fromDisk.GetSubAgentModelsSnapshot(),
            ("shared-model", 976000, "medium", "shared-desc", true),
            ("curated-solo", 2048, "high", "curated-desc", false));
        Assert.Equal("compactor", fromDisk.GetCompactionModel());

        // ── Owner isolation, verified against retained PRE-UPDATE values. ───────────────────
        // Retain actual parsed results BEFORE any owner update.
        var retainedModels = config.Models!;
        var retainedAvailableList = retainedModels.AvailableModels!;
        var retainedAvailableEntry = retainedAvailableList[0];
        var retainedCuratedList = retainedModels.SubAgentModels!;
        var retainedCuratedEntry = retainedCuratedList[0];

        // (1) Mutating the parsed results (list, entry, fields) must NOT reach the owner.
        retainedAvailableList.Add(new ModelEntry { Name = "parsed-attack", ContextWindow = 1 });
        retainedAvailableList.RemoveAt(1);
        retainedAvailableEntry.ContextWindow = -1;
        retainedAvailableEntry.Description = "PARSED-ATTACK";
        retainedCuratedList.Clear();
        retainedCuratedEntry.ReasoningEffort = "PARSED-ATTACK-EFFORT";
        retainedModels.CompactionModel = "PARSED-ATTACK-CM";

        AssertOrderedEntries(
            config.GetAvailableModelsSnapshot(),
            ("shared-model", 976000, "medium", "shared-desc", true),
            ("solo-avail", 4096, "low", "solo-desc", false));
        AssertOrderedEntries(
            config.GetSubAgentModelsSnapshot(),
            ("shared-model", 976000, "medium", "shared-desc", true),
            ("curated-solo", 2048, "high", "curated-desc", false));
        Assert.Equal("compactor", config.GetCompactionModel());

        // (2) Owner mutations must NOT reach the retained parsed results. The retained values
        // are compared against what they held BEFORE the owner update (i.e. the attack values
        // from step (1) — proving nothing new leaked in from the owner side either).
        Assert.True(config.TryUpdateAvailableModel(
            "shared-model", new AvailableModelRequest("shared-model", 111, "OWNER-DESC", false)));
        Assert.True(config.TryUpdateSubAgentModel(
            "curated-solo",
            new SubAgentModelRequest(
                "curated-solo", 333, Microsoft.Extensions.AI.ReasoningEffort.None, "OWNER-CURATED", true)));
        config.SetCompactionModel("owner-cm");

        // Fresh owner reads carry the update on every field, in order.
        AssertOrderedEntries(
            config.GetAvailableModelsSnapshot(),
            ("shared-model", 111, "medium", "OWNER-DESC", false),
            ("solo-avail", 4096, "low", "solo-desc", false));
        AssertOrderedEntries(
            config.GetSubAgentModelsSnapshot(),
            ("shared-model", 976000, "medium", "shared-desc", true),
            ("curated-solo", 333, "none", "OWNER-CURATED", true));
        Assert.Equal("owner-cm", config.GetCompactionModel());

        // The retained parsed graph is frozen at its own (attacked) generation — no owner value
        // leaked in.
        Assert.Equal("PARSED-ATTACK-CM", retainedModels.CompactionModel);
        Assert.Equal(2, retainedAvailableList.Count);
        Assert.Equal("shared-model", retainedAvailableEntry.Name);
        Assert.Equal(-1, retainedAvailableEntry.ContextWindow);
        Assert.Equal("PARSED-ATTACK", retainedAvailableEntry.Description);
        Assert.Equal("parsed-attack", retainedAvailableList[1].Name);
        Assert.Empty(retainedCuratedList);
        Assert.Equal("PARSED-ATTACK-EFFORT", retainedCuratedEntry.ReasoningEffort);
        // The disk-parsed instance is its own owner too: unaffected by the other instance.
        AssertOrderedEntries(
            fromDisk.GetAvailableModelsSnapshot(),
            ("shared-model", 976000, "medium", "shared-desc", true),
            ("solo-avail", 4096, "low", "solo-desc", false));
    }

    /// <summary>
    /// The second required alias shape: one whole catalog LIST anchored and referenced by the
    /// other catalog through a real <c>*alias</c>, routed through
    /// <see cref="ConfigRepoManager.ParseConfig"/> and the disk round trip, with complete ordered
    /// field values for both catalogs and subsequent owner isolation against retained pre-update
    /// values.
    /// <para>
    /// MECHANISM-OBSERVABLE: the raw deserializer binds the SAME list instance to both catalogs
    /// under the alias. The owner boundary must nevertheless produce independent catalogs — a
    /// synchronized mutation of one catalog must not appear in the other, which is exactly what a
    /// shared-list alias would cause if the ownership boundary did not clone.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ParseConfig_ModelsListAnchorAlias_OrderedFieldValues_AndOwnerIsolation()
    {
        var config = ConfigRepoManager.ParseConfig(ListAnchorYaml);

        // Complete ordered field values: the aliased list carries every field into BOTH catalogs.
        AssertOrderedEntries(
            config.GetAvailableModelsSnapshot(),
            ("list-a", 111, "low", "list-a-desc", true),
            ("list-b", 222, "high", "list-b-desc", false));
        AssertOrderedEntries(
            config.GetSubAgentModelsSnapshot(),
            ("list-a", 111, "low", "list-a-desc", true),
            ("list-b", 222, "high", "list-b-desc", false));
        Assert.Equal("list-compactor", config.GetCompactionModel());

        // MECHANISM OBSERVABILITY: this is a real whole-LIST alias — the two catalog list nodes
        // are the SAME node, and a plain DTO bind shares one list instance (and its entries)
        // between the catalogs. The entry-anchor document, by contrast, shares only an entry.
        Assert.True(CatalogListNodesAreSame(ListAnchorYaml),
            "The list-anchor document did not bind both catalogs to one list node.");
        Assert.False(CatalogListNodesAreSame(EntryAnchorYaml),
            "The entry-anchor document unexpectedly shares whole list nodes.");
        var rawBound = RawDeserializeModels(ListAnchorModelsYaml);
        Assert.Same(rawBound.AvailableModels, rawBound.SubAgentModels);
        Assert.Same(rawBound.AvailableModels![0], rawBound.SubAgentModels![0]);

        // Disk round trip.
        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        await manager.WriteConfigAsync(config, TestContext.Current.CancellationToken);
        var fromDisk = ConfigRepoManager.ParseConfig(await ReadWrittenYamlAsync(manager));
        AssertOrderedEntries(
            fromDisk.GetAvailableModelsSnapshot(),
            ("list-a", 111, "low", "list-a-desc", true),
            ("list-b", 222, "high", "list-b-desc", false));
        AssertOrderedEntries(
            fromDisk.GetSubAgentModelsSnapshot(),
            ("list-a", 111, "low", "list-a-desc", true),
            ("list-b", 222, "high", "list-b-desc", false));

        // ── Owner isolation against retained PRE-UPDATE values. ────────────────────────────
        var retainedModels = config.Models!;
        var retainedAvailable = retainedModels.AvailableModels!;
        var retainedCurated = retainedModels.SubAgentModels!;

        // The owner's two catalogs must be INDEPENDENT despite the shared-list alias in YAML:
        // updating the available catalog must not change the curated catalog.
        Assert.True(config.TryUpdateAvailableModel(
            "list-a", new AvailableModelRequest("list-a", 999, "AVAIL-ONLY", false)));

        AssertOrderedEntries(
            config.GetAvailableModelsSnapshot(),
            ("list-a", 999, "low", "AVAIL-ONLY", false),
            ("list-b", 222, "high", "list-b-desc", false));
        // The curated catalog is untouched by the available-catalog update.
        AssertOrderedEntries(
            config.GetSubAgentModelsSnapshot(),
            ("list-a", 111, "low", "list-a-desc", true),
            ("list-b", 222, "high", "list-b-desc", false));

        // Retained pre-update parsed results are frozen at their original values.
        AssertOrderedEntries(
            retainedAvailable,
            ("list-a", 111, "low", "list-a-desc", true),
            ("list-b", 222, "high", "list-b-desc", false));
        AssertOrderedEntries(
            retainedCurated,
            ("list-a", 111, "low", "list-a-desc", true),
            ("list-b", 222, "high", "list-b-desc", false));

        // Mutating the retained parsed lists/entries does not reach the owner either.
        retainedAvailable[0].ContextWindow = -5;
        retainedCurated.Clear();
        AssertOrderedEntries(
            config.GetAvailableModelsSnapshot(),
            ("list-a", 999, "low", "AVAIL-ONLY", false),
            ("list-b", 222, "high", "list-b-desc", false));
        AssertOrderedEntries(
            config.GetSubAgentModelsSnapshot(),
            ("list-a", 111, "low", "list-a-desc", true),
            ("list-b", 222, "high", "list-b-desc", false));
    }

    /// <summary>
    /// The production deserializer settings, used ONLY to observe YamlDotNet's raw alias binding
    /// into a plain DTO (no ownership boundary in the way). Mirrors
    /// <see cref="ConfigRepoManager"/>'s configuration (underscored naming, ignore unmatched).
    /// </summary>
    private static ModelsConfig RawDeserializeModels(string modelsYaml) =>
        new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<ModelsConfig>(modelsYaml)!;

    /// <summary>Loads the <c>models:</c> mapping of a document as a raw YAML representation node.</summary>
    private static YamlMappingNode ModelsNode(string yaml)
    {
        using var reader = new StringReader(yaml);
        var stream = new YamlStream();
        stream.Load(reader);
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        return (YamlMappingNode)root[new YamlScalarNode("models")];
    }

    /// <summary>
    /// Whether the first curated entry node IS the first available entry node — true only when
    /// the document binds them through a real YAML alias, false for duplicated content.
    /// </summary>
    private static bool CuratedFirstEntryIsAvailableFirstEntryNode(string yaml)
    {
        var models = ModelsNode(yaml);
        var available = (YamlSequenceNode)models[new YamlScalarNode("available_models")];
        var curated = (YamlSequenceNode)models[new YamlScalarNode("sub_agent_models")];
        return ReferenceEquals(available.Children[0], curated.Children[0]);
    }

    /// <summary>
    /// Whether the two catalog LIST nodes are the same node — true only for a real whole-list
    /// alias.
    /// </summary>
    private static bool CatalogListNodesAreSame(string yaml)
    {
        var models = ModelsNode(yaml);
        return ReferenceEquals(
            models[new YamlScalarNode("available_models")],
            models[new YamlScalarNode("sub_agent_models")]);
    }

    // ── IsRepositoryAllowed ──────────────────────────────────────────────────

    [Fact]
    public async Task IsRepositoryAllowed_AllowedUrl_ReturnsTrue()
    {
        var manager = await CreateManagerWithConfigAsync("""
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
            """);

        Assert.True(manager.IsRepositoryAllowed("https://github.com/org/my-app.git"));
    }

    [Fact]
    public async Task IsRepositoryAllowed_DisallowedUrl_ReturnsFalse()
    {
        var manager = await CreateManagerWithConfigAsync("""
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
            """);

        Assert.False(manager.IsRepositoryAllowed("https://github.com/other/repo.git"));
    }

    [Fact]
    public async Task IsRepositoryAllowed_TrailingSlashVariation_Matches()
    {
        var manager = await CreateManagerWithConfigAsync("""
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
            """);

        Assert.True(manager.IsRepositoryAllowed("https://github.com/org/my-app.git/"));
    }

    [Fact]
    public async Task IsRepositoryAllowed_CaseInsensitive_Matches()
    {
        var manager = await CreateManagerWithConfigAsync("""
            repositories:
              - name: my-app
                url: https://github.com/Org/My-App.git
            """);

        Assert.True(manager.IsRepositoryAllowed("https://github.com/org/my-app.git"));
    }

    [Fact]
    public async Task IsRepositoryAllowed_WhitespaceVariation_Matches()
    {
        var manager = await CreateManagerWithConfigAsync("""
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
            """);

        Assert.True(manager.IsRepositoryAllowed("  https://github.com/org/my-app.git  "));
    }

    [Fact]
    public void IsRepositoryAllowed_NoConfigLoaded_ReturnsFalse()
    {
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);

        Assert.False(manager.IsRepositoryAllowed("https://github.com/org/my-app.git"));
    }

    // ── AGENTS.md loading ────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAgentsMdAsync_ExistingRole_ReturnsContent()
    {
        var agentsDir = Path.Combine(_tempDir, "agents");
        Directory.CreateDirectory(agentsDir);
        await File.WriteAllTextAsync(
            Path.Combine(agentsDir, "coder.agents.md"),
            "# Coder\nYou write great code.",
            TestContext.Current.CancellationToken);

        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);

        var content = await manager.LoadAgentsMdAsync(WorkerRole.Coder, TestContext.Current.CancellationToken);

        Assert.NotNull(content);
        Assert.Contains("You write great code.", content);
    }

    [Fact]
    public async Task LoadAgentsMdAsync_NonexistentRole_ReturnsNull()
    {
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);

        var content = await manager.LoadAgentsMdAsync(WorkerRole.MergeWorker, TestContext.Current.CancellationToken);

        Assert.Null(content);
    }

    [Fact]
    public async Task LoadAgentsMdAsync_CaseInsensitiveRole()
    {
        var agentsDir = Path.Combine(_tempDir, "agents");
        Directory.CreateDirectory(agentsDir);
        await File.WriteAllTextAsync(
            Path.Combine(agentsDir, "tester.agents.md"),
            "# Tester instructions",
            TestContext.Current.CancellationToken);

        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);

        var content = await manager.LoadAgentsMdAsync(WorkerRole.Tester, TestContext.Current.CancellationToken);

        Assert.NotNull(content);
        Assert.Contains("Tester instructions", content);
    }

    // ── LoadConfigAsync from file ────────────────────────────────────────────

    [Fact]
    public async Task LoadConfigAsync_ReadsFromDisk()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"),
            """
            version: "1.0"
            repositories:
              - name: test-repo
                url: https://github.com/test/repo.git
            """,
            TestContext.Current.CancellationToken);

        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        var config = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);

        Assert.Equal("1.0", config.Version);
        Assert.Single(config.Repositories);
        Assert.Equal("test-repo", config.Repositories[0].Name);
    }

    [Fact]
    public async Task LoadConfigAsync_MissingFile_ThrowsFileNotFound()
    {
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);

        await Assert.ThrowsAsync<FileNotFoundException>(() => manager.LoadConfigAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadConfigAsync_CachesResult()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"),
            """
            version: "1.0"
            repositories:
              - name: cached-repo
                url: https://github.com/test/cached.git
            """,
            TestContext.Current.CancellationToken);

        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        var first = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        var second = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);

        // Detached identities: each load returns its own instance and graph (the private
        // cache is never aliased to callers)...
        Assert.NotSame(first, second);
        Assert.NotSame(first.Repositories, second.Repositories);
        Assert.NotSame(first.Repositories[0], second.Repositories[0]);
        // ...with equal cached values.
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(first.Repositories[0].Name, second.Repositories[0].Name);
        Assert.Equal(first.Repositories[0].Url, second.Repositories[0].Url);

        // A cache hit must NOT reparse the YAML: an external disk edit is invisible until
        // an invalidation (SyncRepoAsync) drops the cache.
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"),
            """
            version: "2.0"
            repositories:
              - name: edited-repo
                url: https://github.com/test/edited.git
            """,
            TestContext.Current.CancellationToken);
        var third = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.Equal("1.0", third.Version);
        Assert.Equal("cached-repo", third.Repositories[0].Name);
        Assert.True(manager.IsRepositoryAllowed("https://github.com/test/cached.git"));
        Assert.False(manager.IsRepositoryAllowed("https://github.com/test/edited.git"));
    }

    // ── Cache ownership: mutation isolation ──────────────────────────────────

    /// <summary>
    /// Compact representative disk fixture for cache-ownership regressions: covers
    /// repositories with a nested release section, workers, orchestrator, the global
    /// available-model catalog plus the curated sub-agent catalog, and Composer settings
    /// (including a nested event-notifications list) — one fixture, not a case matrix.
    /// </summary>
    private const string RepresentativeConfigYaml = """
        version: "1.0"
        repositories:
          - name: my-app
            url: https://github.com/org/my-app.git
            default_branch: develop
            release:
              merge_to: main
              tag_branch: v1
          - name: bare-repo
            url: https://github.com/org/bare.git
        workers:
          coder:
            model: coder-model
            premium_model: coder-premium
          tester:
            model: tester-model
        orchestrator:
          model: brain-model
          max_iterations: 7
        models:
          compaction_model: compactor
          available_models:
            - name: model-a
              context_window: 200000
            - name: model-b
              context_window: 1000
          sub_agent_models:
            - name: sub-1
              context_window: 500
        composer:
          model: composer-model
          max_steps: 7
          event_notifications:
            mode: active
            active_events:
              - goal_completed
              - ci_failed
            throttle_seconds: 45
        """;

    /// <summary>
    /// A caller mutating a returned config — every reachable collection, entry, and nested
    /// section — must not affect another load of the SAME cached generation, nor the cached
    /// allow-list membership.
    /// </summary>
    [Fact]
    public async Task LoadConfigAsync_MutatingReturnedConfig_DoesNotAffectCachedGeneration()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"),
            RepresentativeConfigYaml,
            TestContext.Current.CancellationToken);

        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        var first = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.True(first.IsConfigured);

        // Mutate everything reachable on the returned copy.
        first.Repositories[0].Url = "https://github.com/evil/mutated.git";
        first.Repositories[0].Release!.MergeTo = "evil";
        first.Repositories[0].Release!.TagBranch = "evil";
        first.Repositories[1].Url = "https://github.com/evil/bare.git";
        first.Workers["coder"].Model = "evil";
        first.Workers["coder"].PremiumModel = "evil";
        first.Orchestrator.Model = "evil";
        first.Composer!.Model = "evil";
        first.Composer!.EventNotifications!.ActiveEvents!.Clear();
        first.Composer!.EventNotifications!.ThrottleSeconds = -1;
        // In-place alias probe: the returned config is itself an owner, so the compaction model
        // and the entry field edits go through its synchronized APIs and land on its ACTUAL
        // storage under either Models property contract. Unrelated stored fields are preserved
        // (model-b carries no description/vision in the fixture YAML).
        first.SetCompactionModel("evil");
        Assert.True(first.TryUpdateAvailableModel("model-b", new AvailableModelRequest("model-b", -999)));

        // Positive control: the in-place edits really landed on the mutated returned owner.
        Assert.Equal("evil", first.GetCompactionModel());
        var firstAvailableAfterInPlace = first.GetAvailableModelsSnapshot()!;
        Assert.Equal(-999, firstAvailableAfterInPlace[1].ContextWindow);

        // Pre-replacement isolation checkpoint: with ONLY the in-place owner edits applied (no
        // Models reassignment yet), a load of the SAME cached generation is already unaffected.
        var preReplacementCached = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.NotSame(first, preReplacementCached);
        Assert.Equal("compactor", preReplacementCached.Models!.CompactionModel);
        Assert.Equal("model-a", preReplacementCached.Models!.AvailableModels![0].Name);
        Assert.Equal(200000, preReplacementCached.Models!.AvailableModels![0].ContextWindow);
        Assert.Equal("model-b", preReplacementCached.Models!.AvailableModels![1].Name);
        Assert.Equal(1000, preReplacementCached.Models!.AvailableModels![1].ContextWindow);
        Assert.Equal("sub-1", preReplacementCached.Models!.SubAgentModels![0].Name);
        Assert.Equal(500, preReplacementCached.Models!.SubAgentModels![0].ContextWindow);

        // Generation-replacement probe: the raw renames cannot be expressed by the update APIs,
        // so capture Models, edit the local, and reassign — AFTER the complete in-place phase
        // above (owner mutation + fresh owner reads + cached-generation checkpoint).
        var firstModels = first.Models;
        firstModels!.AvailableModels![0].Name = "evil";
        firstModels.SubAgentModels![0].Name = "evil";
        first.Models = firstModels;

        // Positive control: the mutated returned owner really changed.
        Assert.Equal("evil", first.GetCompactionModel());
        var firstAvailable = first.GetAvailableModelsSnapshot()!;
        Assert.Equal("evil", firstAvailable[0].Name);
        Assert.Equal(-999, firstAvailable[1].ContextWindow);
        Assert.Equal("evil", Assert.Single(first.GetSubAgentModelsSnapshot()!).Name);

        // Another load of the SAME cached generation is unaffected.
        var second = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.NotSame(first, second);
        Assert.Equal("1.0", second.Version);
        Assert.Equal("my-app", second.Repositories[0].Name);
        Assert.Equal("https://github.com/org/my-app.git", second.Repositories[0].Url);
        Assert.Equal("main", second.Repositories[0].Release!.MergeTo);
        Assert.Equal("v1", second.Repositories[0].Release!.TagBranch);
        Assert.Equal("https://github.com/org/bare.git", second.Repositories[1].Url);
        Assert.Equal("coder-model", second.Workers["coder"].Model);
        Assert.Equal("coder-premium", second.Workers["coder"].PremiumModel);
        Assert.Equal("brain-model", second.Orchestrator.Model);
        Assert.Equal("compactor", second.Models!.CompactionModel);
        Assert.Equal("model-a", second.Models!.AvailableModels![0].Name);
        Assert.Equal(200000, second.Models!.AvailableModels![0].ContextWindow);
        Assert.Equal("model-b", second.Models!.AvailableModels![1].Name);
        Assert.Equal(1000, second.Models!.AvailableModels![1].ContextWindow);
        Assert.Equal("sub-1", second.Models!.SubAgentModels![0].Name);
        Assert.Equal(500, second.Models!.SubAgentModels![0].ContextWindow);
        Assert.Equal("composer-model", second.Composer!.Model);
        Assert.Equal(
            ["goal_completed", "ci_failed"],
            second.Composer!.EventNotifications!.ActiveEvents);
        Assert.Equal(45, second.Composer!.EventNotifications!.ThrottleSeconds);

        // Mutate collection containers and entries on a CACHE-HIT return too, then prove a
        // third load of the same generation remains detached and complete.
        second.Repositories.Clear();
        second.Workers.Clear();
        second.Orchestrator.Model = "cache-hit-mutated";
        // In-place alias probe on the cache-hit owner: emptying the available catalog and
        // shrinking the curated entry go through the synchronized APIs, so both land on the
        // returned owner's ACTUAL storage. The curated update preserves the entry's other
        // stored values (sub-1 has no reasoning effort/description/vision in the fixture).
        Assert.True(second.TryRemoveAvailableModel("model-a"));
        Assert.True(second.TryRemoveAvailableModel("model-b"));
        Assert.True(second.TryUpdateSubAgentModel("sub-1", new SubAgentModelRequest("sub-1", -1, null)));
        second.Composer!.EventNotifications!.ActiveEvents!.Clear();

        // Positive control: the cache-hit copy really changed before the next load.
        Assert.Empty(second.GetAvailableModelsSnapshot()!);
        Assert.Equal(-1, Assert.Single(second.GetSubAgentModelsSnapshot()!).ContextWindow);

        var third = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, third.Repositories.Count);
        Assert.Equal("my-app", third.Repositories[0].Name);
        Assert.Equal("coder-model", third.Workers["coder"].Model);
        Assert.Equal("brain-model", third.Orchestrator.Model);
        Assert.Equal(["model-a", "model-b"], third.Models!.AvailableModels!.Select(m => m.Name));
        Assert.Equal(500, third.Models!.SubAgentModels![0].ContextWindow);
        Assert.Equal(
            ["goal_completed", "ci_failed"],
            third.Composer!.EventNotifications!.ActiveEvents);

        // Cached allow-list membership is unchanged by either mutated copy.
        Assert.True(manager.IsRepositoryAllowed("https://github.com/org/my-app.git"));
        Assert.True(manager.IsRepositoryAllowed("https://github.com/org/bare.git"));
        Assert.False(manager.IsRepositoryAllowed("https://github.com/evil/mutated.git"));
        Assert.False(manager.IsRepositoryAllowed("https://github.com/evil/bare.git"));
    }

    /// <summary>
    /// After a successful write, mutating the ORIGINAL input must not affect subsequent
    /// loads, the persisted disk content, or the allow-list membership — all stay at the
    /// written state.
    /// </summary>
    [Fact]
    public async Task WriteConfigAsync_MutatingInputAfterWrite_DoesNotChangeCacheOrDisk()
    {
        var config = new HiveConfigFile
        {
            Version = "1.0",
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "written-repo",
                    Url = "https://github.com/org/written.git",
                    Release = new ReleaseRepoConfig { MergeTo = "main", TagBranch = "v1" }
                }
            ],
            Workers = new Dictionary<string, WorkerConfig>
            {
                ["coder"] = new() { Model = "written-coder" }
            },
            Orchestrator = new OrchestratorConfig { Model = "written-brain" },
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "written-model", ContextWindow = 123 }]
            },
            Composer = new ComposerConfig { Model = "written-composer" }
        };

        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        await manager.WriteConfigAsync(config, TestContext.Current.CancellationToken);

        // Mutate the original input after the write succeeded.
        config.Repositories[0].Url = "https://github.com/evil/mutated.git";
        config.Repositories[0].Release!.MergeTo = "evil";
        config.Workers["coder"].Model = "evil";
        config.Orchestrator.Model = "evil";
        config.Composer!.Model = "evil";
        // In-place alias probe: the input config is an owner, so this entry edit lands on its
        // ACTUAL storage — if the write had retained the same ModelsConfig/entry instances, the
        // cache and disk assertions below would observe it. Unrelated fields stay as written.
        Assert.True(config.TryUpdateAvailableModel("written-model", new AvailableModelRequest("written-model", -777)));

        // Fresh source control: the in-place edit really landed on the input owner.
        var inPlaceMutatedInput = Assert.Single(config.GetAvailableModelsSnapshot()!);
        Assert.Equal("written-model", inPlaceMutatedInput.Name);
        Assert.Equal(-777, inPlaceMutatedInput.ContextWindow);

        // Pre-replacement isolation checkpoint: with ONLY the in-place owner edit applied (no
        // Models reassignment yet), a load of the post-write cache is already unaffected.
        var preReplacementLoaded = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.Equal("written-model", preReplacementLoaded.Models!.AvailableModels![0].Name);
        Assert.Equal(123, preReplacementLoaded.Models!.AvailableModels![0].ContextWindow);

        // Generation-replacement probe: the rename cannot be expressed by the update API, so
        // capture Models, edit the local, and reassign — AFTER the complete in-place phase above
        // (owner mutation + fresh source read + cached-generation checkpoint).
        var inputModels = config.Models;
        inputModels!.AvailableModels![0].Name = "evil";
        config.Models = inputModels;

        // Fresh source control: the input owner really carries the mutated model state.
        var mutatedInput = Assert.Single(config.GetAvailableModelsSnapshot()!);
        Assert.Equal("evil", mutatedInput.Name);
        Assert.Equal(-777, mutatedInput.ContextWindow);

        // Subsequent loads stay at the written state.
        var loaded = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.Equal("written-brain", loaded.Orchestrator.Model);
        Assert.Equal("https://github.com/org/written.git", loaded.Repositories[0].Url);
        Assert.Equal("main", loaded.Repositories[0].Release!.MergeTo);
        Assert.Equal("written-coder", loaded.Workers["coder"].Model);
        Assert.Equal("written-model", loaded.Models!.AvailableModels![0].Name);
        Assert.Equal(123, loaded.Models!.AvailableModels![0].ContextWindow);
        Assert.Equal("written-composer", loaded.Composer!.Model);

        // Disk content is at the written state too.
        var disk = await ReadWrittenYamlAsync(manager);
        var parsed = ConfigRepoManager.ParseConfig(disk);
        Assert.Equal("written-brain", parsed.Orchestrator.Model);
        Assert.Equal("https://github.com/org/written.git", parsed.Repositories[0].Url);
        Assert.Equal("written-model", parsed.Models!.AvailableModels![0].Name);
        Assert.Equal(123, parsed.Models!.AvailableModels![0].ContextWindow);

        // Allow-list membership is at the written state.
        Assert.True(manager.IsRepositoryAllowed("https://github.com/org/written.git"));
        Assert.False(manager.IsRepositoryAllowed("https://github.com/evil/mutated.git"));

        // A returned copy from the post-write cache is detached too.
        loaded.Repositories.Clear();
        loaded.Workers.Clear();
        // List-clear attack through the synchronized removal API, so it empties the returned
        // owner's ACTUAL catalog storage.
        Assert.True(loaded.TryRemoveAvailableModel("written-model"));
        Assert.Empty(loaded.GetAvailableModelsSnapshot()!);

        var reloaded = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.Single(reloaded.Repositories);
        Assert.Equal("https://github.com/org/written.git", reloaded.Repositories[0].Url);
        Assert.Equal("written-coder", reloaded.Workers["coder"].Model);
        Assert.Equal("written-model", Assert.Single(reloaded.Models!.AvailableModels!).Name);
        Assert.Equal(123, Assert.Single(reloaded.Models!.AvailableModels!).ContextWindow);
        Assert.True(manager.IsRepositoryAllowed("https://github.com/org/written.git"));
    }

    /// <summary>
    /// Null/missing sections and runtime nulls survive the detached copy on BOTH the disk
    /// load path and repeated cache-hit loads.
    /// </summary>
    [Fact]
    public async Task LoadConfigAsync_NullAndMissingSections_SurviveTheCopy()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"),
            """
            version: "1.0"
            repositories:
              - name: only-repo
                url: https://github.com/org/only.git
            """,
            TestContext.Current.CancellationToken);

        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        var first = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.Null(first.Models);
        Assert.Null(first.Composer);
        Assert.Null(first.Repositories[0].Release);
        Assert.True(first.IsConfigured);

        var second = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.Null(second.Models);
        Assert.Null(second.Composer);
        Assert.Null(second.Repositories[0].Release);
        Assert.True(second.IsConfigured);

        // Runtime nulls on nominally non-null top-level members and nested catalog lists are
        // preserved by the post-write cache materialization as well.
        var runtimeNulls = new HiveConfigFile
        {
            Version = null!,
            Repositories = null!,
            Workers = null!,
            Orchestrator = null!,
            Models = new ModelsConfig { AvailableModels = null, SubAgentModels = null },
            Composer = null,
            IsConfigured = false
        };
        await manager.WriteConfigAsync(runtimeNulls, TestContext.Current.CancellationToken);

        var postWrite = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.Null(postWrite.Version);
        Assert.Null(postWrite.Repositories);
        Assert.Null(postWrite.Workers);
        Assert.Null(postWrite.Orchestrator);
        Assert.NotNull(postWrite.Models);
        Assert.Null(postWrite.Models.AvailableModels);
        Assert.Null(postWrite.Models.SubAgentModels);
        Assert.Null(postWrite.Composer);
        Assert.False(postWrite.IsConfigured);
    }

    /// <summary>
    /// Post-write cached values preserve the CALLER's raw fields — including raw blank
    /// model values (no ParseConfig re-normalization) and IsConfigured both true and false.
    /// </summary>
    [Fact]
    public async Task WriteConfigAsync_CachesRawCallerValues_IncludingBlankModelsAndIsConfigured()
    {
        var configured = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "   " },
            Workers = new Dictionary<string, WorkerConfig>
            {
                ["coder"] = new() { Model = "\t" }
            },
            IsConfigured = true
        };
        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        await manager.WriteConfigAsync(configured, TestContext.Current.CancellationToken);

        var first = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.True(first.IsConfigured);
        Assert.Equal("   ", first.Orchestrator.Model);
        Assert.Equal("\t", first.Workers["coder"].Model);

        // IsConfigured = false is preserved explicitly from the caller too.
        var unconfigured = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "raw-model" }
        };
        await manager.WriteConfigAsync(unconfigured, TestContext.Current.CancellationToken);

        var second = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.False(second.IsConfigured);
        Assert.Equal("raw-model", second.Orchestrator.Model);
    }

    // ── Cache refresh semantics (sync invalidation, failed retention) ────────

    /// <summary>
    /// A successful sync invalidates the cache: allow-list membership is DENIED in the
    /// temporary unloaded window, the next load reads the FRESH disk content, and the new
    /// generation is detached from the previous one.
    /// </summary>
    [Fact]
    public async Task SyncRepoAsync_Success_InvalidatesCache_AndDeniesUntilReload()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"),
            """
            version: "1.0"
            repositories:
              - name: old-repo
                url: https://github.com/org/old.git
            """,
            TestContext.Current.CancellationToken);

        // A .git directory routes SyncRepoAsync through the pull path; the GitRunner seam
        // stubs the git invocation (no real network, no credentials).
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir)
        {
            GitRunner = (_, _, _) => Task.FromResult(new ConfigRepoManager.GitRunResult(0, "", ""))
        };

        var first = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.True(manager.IsRepositoryAllowed("https://github.com/org/old.git"));

        // The remote "pulled" newer disk content.
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"),
            """
            version: "2.0"
            repositories:
              - name: new-repo
                url: https://github.com/org/new.git
            """,
            TestContext.Current.CancellationToken);

        await manager.SyncRepoAsync(TestContext.Current.CancellationToken);

        // Temporary unloaded-denial window: the cache was invalidated, so membership is
        // denied until the next load repopulates it.
        Assert.False(manager.IsRepositoryAllowed("https://github.com/org/old.git"));
        Assert.False(manager.IsRepositoryAllowed("https://github.com/org/new.git"));

        var second = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.NotSame(first, second);
        Assert.Equal("2.0", second.Version);
        Assert.Equal("new-repo", second.Repositories[0].Name);
        Assert.True(manager.IsRepositoryAllowed("https://github.com/org/new.git"));
        Assert.False(manager.IsRepositoryAllowed("https://github.com/org/old.git"));
    }

    /// <summary>
    /// A failed sync retains the existing cache: membership and loads keep serving the
    /// previously cached generation.
    /// </summary>
    [Fact]
    public async Task SyncRepoAsync_Failure_RetainsExistingCache()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"),
            """
            version: "1.0"
            repositories:
              - name: retained-repo
                url: https://github.com/org/retained.git
            """,
            TestContext.Current.CancellationToken);

        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir)
        {
            // Every git invocation fails (exit 1) — the pull fails, the best-effort
            // merge --abort fails too, and SyncRepoAsync rethrows.
            GitRunner = (_, _, _) => Task.FromResult(new ConfigRepoManager.GitRunResult(1, "", "boom"))
        };

        var first = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.True(manager.IsRepositoryAllowed("https://github.com/org/retained.git"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SyncRepoAsync(TestContext.Current.CancellationToken));

        // Failed-sync retention: the cache is still loaded and serving the old generation.
        Assert.True(manager.IsRepositoryAllowed("https://github.com/org/retained.git"));
        var second = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.Equal("retained-repo", second.Repositories[0].Name);
        Assert.NotSame(first.Repositories, second.Repositories);
    }

    /// <summary>
    /// A failed (here: directory-missing) write must not replace an existing cache — the
    /// previously cached generation keeps serving loads and allow-list membership.
    /// </summary>
    [Fact]
    public async Task WriteConfigAsync_FailedWrite_RetainsExistingCache()
    {
        var configDir = Path.Combine(_tempDir, "cfg");
        Directory.CreateDirectory(configDir);
        await File.WriteAllTextAsync(
            Path.Combine(configDir, "hive-config.yaml"),
            """
            version: "1.0"
            repositories:
              - name: kept-repo
                url: https://github.com/org/kept.git
            """,
            TestContext.Current.CancellationToken);

        var manager = new ConfigRepoManager("https://example.com/config.git", configDir);
        _ = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.True(manager.IsRepositoryAllowed("https://github.com/org/kept.git"));

        // Remove the directory so the write's File.WriteAllTextAsync fails.
        Directory.Delete(configDir, recursive: true);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => manager.WriteConfigAsync(
                new HiveConfigFile
                {
                    Repositories = [new RepositoryConfig { Name = "evil", Url = "https://github.com/org/evil.git" }]
                },
                TestContext.Current.CancellationToken));

        // Failed-write retention: the old cache is untouched.
        Assert.True(manager.IsRepositoryAllowed("https://github.com/org/kept.git"));
        Assert.False(manager.IsRepositoryAllowed("https://github.com/org/evil.git"));
        var loaded = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.Equal("kept-repo", loaded.Repositories[0].Name);
    }

    // ── WriteConfigAsync tests ────────────────────────────────────────────────

    [Fact]
    public async Task WriteConfigAsync_SerializesWithSnakeCaseKeys()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "test-model" },
            Models = new ModelsConfig { CompactionModel = "mini-model" },
        };
        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);

        await manager.WriteConfigAsync(config, TestContext.Current.CancellationToken);

        var yaml = await File.ReadAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"), TestContext.Current.CancellationToken);
        Assert.Contains("compaction_model:", yaml);
    }

    [Fact]
    public async Task WriteConfigAsync_OmitsNullDefaultValues()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "my-model" },
        };
        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);

        await manager.WriteConfigAsync(config, TestContext.Current.CancellationToken);

        var yaml = await File.ReadAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("available_models", yaml);
        Assert.DoesNotContain("compaction_model", yaml);
        Assert.DoesNotContain("composer", yaml);
    }

    [Fact]
    public async Task WriteConfigAsync_UpdatesCachedConfig()
    {
        // Write initial config to disk so LoadConfigAsync can read it
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"),
            "version: \"1.0\"",
            TestContext.Current.CancellationToken);

        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);

        // Load once to populate cache
        var first = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.Equal("1.0", first.Version);

        // Write new config
        var updated = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "updated-model" },
        };
        await manager.WriteConfigAsync(updated, TestContext.Current.CancellationToken);

        // Load again — should return the updated config from cache
        var second = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.Equal("updated-model", second.Orchestrator.Model);
    }

    // ── WriteConfigAsync snapshot serialization fidelity tests ───────────────

    /// <summary>
    /// Builds the STABLE representative live configuration used as the fidelity baseline.
    /// It must never be mutated by the tests: <see cref="ConfigRepoManager.WriteConfigAsync"/>
    /// serializes a detached snapshot of it, so the expected YAML is derived from this exact
    /// instance and any mutation inside the writer would change the bytes.
    /// </summary>
    private static HiveConfigFile MakeRepresentativeConfig()
    {
        var config = new HiveConfigFile
        {
            Version = "1.0",
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "my-app",
                    Url = "https://github.com/org/my-app.git",
                    DefaultBranch = "develop",
                    MonitorCi = true,
                    CiTimeoutMinutes = 45,
                    Release = new ReleaseRepoConfig { MergeTo = "main", TagBranch = "v1" },
                    PublishNuGet = new NuGetPublishConfig
                    {
                        Packages = [new NuGetPackageEntry { PackageId = "My.Library" }]
                    }
                },
                new RepositoryConfig
                {
                    Name = "bare-repo",
                    Url = "https://github.com/org/bare.git"
                }
            ],
            Workers = new Dictionary<string, WorkerConfig>
            {
                ["coder"] = new()
                {
                    Model = "coder-model",
                    PremiumModel = "coder-premium",
                    ContextWindow = 128000,
                    ReasoningEffort = "high",
                    PremiumReasoningEffort = "extra_high"
                },
                ["tester"] = new() { Model = "tester-model" }
            },
            Orchestrator = new OrchestratorConfig
            {
                Model = "brain-model",
                MaxIterations = 7,
                MaxRetriesPerTask = 2,
                MaxParallelGoals = 3,
                VerboseLogging = true,
                BrainMaxSteps = 25,
                BranchCleanupDelayHours = 12,
                WorkerTaskTimeoutMinutes = 90,
                ReasoningEffort = "medium"
            },
            Models = new ModelsConfig
            {
                CompactionModel = "compactor",
                // Raw catalog values: untrimmed names, a duplicate, every ModelEntry field
                // populated, nullable and non-positive context windows, nullable vision flags,
                // raw reasoning strings — WriteConfigAsync must persist them WITHOUT the
                // normalization that ParseConfig applies on read.
                AvailableModels =
                [
                    new ModelEntry { Name = "model-a", ContextWindow = 200000, ReasoningEffort = "high", Description = "primary", SupportsVision = true },
                    new ModelEntry { Name = "  model-b  ", ContextWindow = null, ReasoningEffort = null, Description = null, SupportsVision = null },
                    new ModelEntry { Name = "model-a", ContextWindow = -5, ReasoningEffort = "  low  ", Description = "dup", SupportsVision = false },
                    new ModelEntry { Name = "model-c", ContextWindow = 0 }
                ],
                SubAgentModels =
                [
                    new ModelEntry { Name = "sub-1", ContextWindow = 1000, SupportsVision = null }
                ]
            },
            Composer = new ComposerConfig
            {
                Model = "composer-model",
                MaxSteps = 7,
                ReasoningEffort = "low",
                EventNotifications = new EventNotificationsConfig
                {
                    Mode = "active",
                    ActiveEvents = ["goal_completed", "ci_failed"],
                    ThrottleSeconds = 45
                }
            }
        };
        return config;
    }

    /// <summary>
    /// The expected YAML: what the PRODUCTION serializer settings produce for the stable
    /// representative config. The reference is built with the identical builder chain the
    /// production <c>YamlSerializer</c> field uses (underscored naming, omit defaults/nulls).
    /// </summary>
    private static string ExpectedRepresentativeYaml() =>
        new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitNull)
            .Build()
            .Serialize(MakeRepresentativeConfig());

    /// <summary>Reads the persisted hive-config.yaml from disk (not the manager cache).</summary>
    private async Task<string> ReadWrittenYamlAsync(FakeConfigRepoManager manager)
    {
        var path = Path.Combine(_tempDir, "hive-config.yaml");
        Assert.True(File.Exists(path), "WriteConfigAsync did not create hive-config.yaml.");
        return await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Byte-wise fidelity: the file WriteConfigAsync persists must equal what the existing
    /// serializer settings produce for the stable representative LIVE config — proving the
    /// snapshot path is schema-, naming-, order-, and omission-compatible with the previous
    /// live serialization.
    /// </summary>
    [Fact]
    public async Task WriteConfigAsync_PersistedYaml_MatchesLiveSerializationByteForByte()
    {
        var config = MakeRepresentativeConfig();
        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);

        await manager.WriteConfigAsync(config, TestContext.Current.CancellationToken);

        var disk = await ReadWrittenYamlAsync(manager);
        Assert.Equal(ExpectedRepresentativeYaml(), disk);
    }

    /// <summary>
    /// Field-level round-trip for the populated catalog and config sections of the persisted
    /// YAML: every populated top-level section, every <see cref="ModelEntry"/> field, and the
    /// raw catalog values (duplicate and untrimmed names, nullable/nonpositive windows,
    /// nullable vision flags, raw reasoning strings) must survive to disk unchanged.
    /// </summary>
    [Fact]
    public async Task WriteConfigAsync_PersistedYaml_RoundTripsPopulatedSectionsFieldByField()
    {
        var config = MakeRepresentativeConfig();
        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);

        await manager.WriteConfigAsync(config, TestContext.Current.CancellationToken);

        var disk = await ReadWrittenYamlAsync(manager);
        var parsed = ConfigRepoManager.ParseConfig(disk);

        // Top-level sections all present.
        Assert.NotNull(parsed.Orchestrator);
        Assert.NotNull(parsed.Models);
        Assert.NotNull(parsed.Composer);
        Assert.Equal(2, parsed.Repositories.Count);
        Assert.Equal(2, parsed.Workers.Count);

        // Orchestrator.
        Assert.Equal("brain-model", parsed.Orchestrator.Model);
        Assert.Equal(7, parsed.Orchestrator.MaxIterations);
        Assert.Equal("medium", parsed.Orchestrator.ReasoningEffort);

        // Workers.
        Assert.Equal("coder-model", parsed.Workers["coder"].Model);
        Assert.Equal("coder-premium", parsed.Workers["coder"].PremiumModel);
        Assert.Equal(128000, parsed.Workers["coder"].ContextWindow);
        Assert.Equal("extra_high", parsed.Workers["coder"].PremiumReasoningEffort);

        // Composer.
        Assert.Equal("composer-model", parsed.Composer.Model);
        Assert.Equal(7, parsed.Composer.MaxSteps);
        Assert.Equal("low", parsed.Composer.ReasoningEffort);
        Assert.NotNull(parsed.Composer.EventNotifications);
        Assert.Equal("active", parsed.Composer.EventNotifications.Mode);
        Assert.Equal(["goal_completed", "ci_failed"], parsed.Composer.EventNotifications.ActiveEvents);

        // Catalog: every entry, RAW (no trimming, no dedup, no window clamping).
        var available = parsed.Models.AvailableModels!;
        Assert.Equal(4, available.Count);
        Assert.Equal("model-a", available[0].Name);
        Assert.Equal(200000, available[0].ContextWindow);
        Assert.Equal("high", available[0].ReasoningEffort);
        Assert.Equal("primary", available[0].Description);
        Assert.True(available[0].SupportsVision);
        // Untrimmed name preserved verbatim (serialization fidelity, NOT parser normalization —
        // ParseConfig never trims entry names, so the raw value round-trips).
        Assert.Equal("  model-b  ", available[1].Name);
        Assert.Null(available[1].ContextWindow);
        Assert.Null(available[1].ReasoningEffort);
        Assert.Null(available[1].Description);
        Assert.Null(available[1].SupportsVision);
        // Duplicate name preserved verbatim.
        Assert.Equal("model-a", available[2].Name);
        Assert.Equal(-5, available[2].ContextWindow);
        Assert.Equal("  low  ", available[2].ReasoningEffort);
        Assert.Equal("dup", available[2].Description);
        Assert.False(available[2].SupportsVision);
        // Non-positive window preserved.
        Assert.Equal("model-c", available[3].Name);
        Assert.Equal(0, available[3].ContextWindow);
        // Sub-agent list preserved separately.
        var sub = parsed.Models.SubAgentModels!;
        var entry = Assert.Single(sub);
        Assert.Equal("sub-1", entry.Name);
        Assert.Equal(1000, entry.ContextWindow);
        Assert.Null(entry.SupportsVision);
        // Compaction model round-trips.
        Assert.Equal("compactor", parsed.Models.CompactionModel);
    }

    /// <summary>
    /// A config WITHOUT a Models section serializes without one — the snapshot must not
    /// materialize an empty catalog section.
    /// </summary>
    [Fact]
    public async Task WriteConfigAsync_MissingModelsSection_OmitsModelsFromDisk()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "only-model" },
        };
        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);

        await manager.WriteConfigAsync(config, TestContext.Current.CancellationToken);

        var disk = await ReadWrittenYamlAsync(manager);
        Assert.Equal(
            new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitNull)
                .Build()
                .Serialize(config),
            disk);
        Assert.DoesNotContain("models", disk);
        Assert.DoesNotContain("available_models", disk);
        Assert.DoesNotContain("sub_agent_models", disk);
    }

    /// <summary>
    /// NULL catalog lists stay ABSENT from the persisted YAML while EMPTY lists serialize as
    /// empty sequences — the null/default-omission behavior is preserved through the snapshot.
    /// </summary>
    [Fact]
    public async Task WriteConfigAsync_NullVersusEmptyCatalogLists_DistinguishedOnDisk()
    {
        var withNull = new HiveConfigFile
        {
            Models = new ModelsConfig { CompactionModel = "m" }
        };
        var withEmpty = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                CompactionModel = "m",
                AvailableModels = [],
                SubAgentModels = []
            }
        };

        var nullDir = Path.Combine(_tempDir, "null-lists");
        var emptyDir = Path.Combine(_tempDir, "empty-lists");
        Directory.CreateDirectory(nullDir);
        Directory.CreateDirectory(emptyDir);
        var nullManager = new FakeConfigRepoManager("https://example.com/config.git", nullDir);
        var emptyManager = new FakeConfigRepoManager("https://example.com/config.git", emptyDir);

        await nullManager.WriteConfigAsync(withNull, TestContext.Current.CancellationToken);
        await emptyManager.WriteConfigAsync(withEmpty, TestContext.Current.CancellationToken);

        var nullYaml = await File.ReadAllTextAsync(Path.Combine(nullDir, "hive-config.yaml"), TestContext.Current.CancellationToken);
        var emptyYaml = await File.ReadAllTextAsync(Path.Combine(emptyDir, "hive-config.yaml"), TestContext.Current.CancellationToken);

        Assert.Equal(
            new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitNull)
                .Build()
                .Serialize(withNull),
            nullYaml);
        Assert.Equal(
            new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitNull)
                .Build()
                .Serialize(withEmpty),
            emptyYaml);
        Assert.DoesNotContain("available_models", nullYaml);
        Assert.DoesNotContain("sub_agent_models", nullYaml);
        Assert.Contains("available_models:", emptyYaml);
        Assert.Contains("sub_agent_models:", emptyYaml);
    }

    /// <summary>
    /// <see cref="HiveConfigFile.IsConfigured"/> must never reach the persisted YAML — even when
    /// set on the live instance. The snapshot excludes it by construction.
    /// </summary>
    [Fact]
    public async Task WriteConfigAsync_IsConfiguredSet_StaysAbsentFromDisk()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "m" },
            IsConfigured = true
        };
        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);

        await manager.WriteConfigAsync(config, TestContext.Current.CancellationToken);

        var disk = await ReadWrittenYamlAsync(manager);
        Assert.Equal(
            new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitNull)
                .Build()
                .Serialize(config),
            disk);
        Assert.DoesNotContain("is_configured", disk);
    }

    // ── WriteConfigAsync catalog-monitor contention regression ───────────────

    /// <summary>
    /// Reflection handle for <see cref="HiveConfigFile"/>'s private catalog monitor — the same
    /// test-only access pattern used by <c>HiveConfigFileCatalogSafetyTests</c>. Resolved once;
    /// a stale field name fails here at setup rather than silently passing.
    /// </summary>
    private static readonly FieldInfo CatalogLockField = typeof(HiveConfigFile)
        .GetField("_catalogLock", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("HiveConfigFile._catalogLock field not found — test setup is stale.");

    /// <summary>Positive-observation bound for monitor contention (must SUCCEED within it).</summary>
    private static readonly TimeSpan WriteContentionObservationBound = TimeSpan.FromSeconds(30);

    /// <summary>Bound for each bounded thread join attempt, including on the failure path.</summary>
    private static readonly TimeSpan WriteThreadJoinBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Bound for the write-Task observation. TIMEOUT-ONLY diagnostic bound: an unresponsive write
    /// fails fast with a <see cref="TimeoutException"/> instead of hanging the run. It can never
    /// fire in the passing flow — a contended write completes promptly once the monitor is released.
    /// <para>
    /// Expiry bounds the OBSERVATION ONLY: <see cref="Task.WaitAsync(TimeSpan)"/> does not stop the
    /// underlying write, so a timeout is explicitly NOT termination and must never authorize file
    /// or directory deletion.
    /// </para>
    /// </summary>
    private static readonly TimeSpan WriteTaskObservationBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A dedicated synchronous thread whose body invokes <see cref="ConfigRepoManager.WriteConfigAsync"/>
    /// and hands the returned <see cref="Task"/> back WITHOUT blocking on it — the task is observed
    /// (bounded, timeout-only) only AFTER the monitor is released and the thread has been boundedly
    /// joined, so asynchronous file completion can never masquerade as the monitor wait.
    /// <para>
    /// TERMINATION vs ATTEMPTED DRAINAGE are tracked separately: <see cref="EnsureTerminatedAsync"/>
    /// reports <c>Terminated</c> only when the thread was actually joined AND the write Task ran to
    /// completion (or faulted, or provably never existed). A bound expiry reports NOT terminated.
    /// Faults are CAPTURED and returned rather than thrown mid-cleanup, so cleanup always completes
    /// before anything is surfaced.
    /// </para>
    /// </summary>
    private sealed class WriteCapturedThread
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _done = new(false);
        private readonly ConfigRepoManager _manager;
        private readonly HiveConfigFile _config;
        private Task? _writeTask;
        private Exception? _fault;

        public WriteCapturedThread(string name, ConfigRepoManager manager, HiveConfigFile config)
        {
            _manager = manager;
            _config = config;
            _thread = new Thread(() =>
            {
                try
                {
                    // Capture the Task; deliberately NOT awaited on this thread. Published with a
                    // release write so a failed join followed by a re-read still sees a late publication.
                    Volatile.Write(ref _writeTask, _manager.WriteConfigAsync(_config, TestContext.Current.CancellationToken));
                }
                catch (Exception ex)
                {
                    _fault = ex;
                }
                finally
                {
                    _done.Set();
                }
            })
            {
                IsBackground = true,
                Name = name
            };
        }

        public bool Started { get; private set; }

        /// <summary>Thread name for diagnostics in fault messages.</summary>
        public string Name => _thread.Name!;

        /// <summary>The synchronous body's captured fault, or null when the body completed cleanly.</summary>
        public Exception? BodyFault => _fault;

        public void Start()
        {
            _thread.Start();
            Started = true;
        }

        /// <summary>
        /// Bounded POSITIVE observation that the thread is actually blocked on the catalog
        /// monitor: the thread must be seen in <see cref="System.Threading.ThreadState.WaitSleepJoin"/> — the state
        /// the CLR assigns a thread parked inside <c>Monitor.Enter</c>. A write that completes
        /// without contending fails immediately; an expired deadline fails too.
        /// </summary>
        public string? WaitUntilBlockedOnMonitor(TimeSpan bound)
        {
            var deadline = Environment.TickCount64 + (long)bound.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                if (_done.IsSet)
                    return $"Write thread '{_thread.Name}' COMPLETED while the catalog monitor was held — WriteConfigAsync's snapshot read never contended for _catalogLock.";

                if ((_thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0)
                    return null;   // positively observed blocked on the monitor

                Thread.Yield();
            }

            return $"Write thread '{_thread.Name}' was never observed blocked on the catalog monitor within {bound}.";
        }

        /// <summary>
        /// Drives BOTH the invocation thread and the returned write Task to OBSERVED termination
        /// outside the monitor, and reports termination separately from any fault.
        /// <para>
        /// <c>Terminated</c> is <c>true</c> ONLY when the thread was actually joined AND the write
        /// Task completed or faulted (or the body faulted before publishing one, so no async work
        /// exists). It is NEVER set on a join-bound or observation-bound expiry, and never when the
        /// Task was unpublished at a failed join — a failed join is followed by a re-read (late
        /// publication is drained too) and a retried join. Only a <c>true</c> result may authorize
        /// deleting files the write could still touch.
        /// </para>
        /// The Task is observed with the timeout-only <see cref="Task.WaitAsync(TimeSpan)"/> overload,
        /// never a bare unbounded await; a <see cref="TimeoutException"/> is returned as a diagnostic
        /// fault WITHOUT claiming termination, because the observation timing out does not stop the write.
        /// </summary>
        public async Task<(bool Terminated, Exception? Fault)> EnsureTerminatedAsync(
            TimeSpan joinBound, TimeSpan taskBound)
        {
            if (!Started)
                return (true, null);   // nothing ever ran — no thread, no Task, nothing to drain

            var joined = _thread.Join(joinBound);

            // The body publishes _writeTask BEFORE signalling completion, so a successful join makes
            // the publication (or the body fault) definitively visible. After a FAILED join the field
            // may still be unpublished, so whatever is visible is observed and the join is retried.
            var task = Volatile.Read(ref _writeTask);
            var (taskTerminated, fault) = await ObserveTaskAsync(task, taskBound);

            if (!joined)
            {
                // Retry the join; the body may simply have been slow. A Task published AFTER the
                // first failed join is observed here rather than left running.
                joined = _thread.Join(joinBound);
                var late = Volatile.Read(ref _writeTask);
                if (!ReferenceEquals(late, task))
                {
                    var (lateTerminated, lateFault) = await ObserveTaskAsync(late, taskBound);
                    taskTerminated = lateTerminated;
                    fault ??= lateFault;
                }
                else if (joined && late is null)
                {
                    taskTerminated = true;   // joined, nothing was ever published: the body faulted
                }
            }

            return (joined && taskTerminated, fault);
        }

        /// <summary>
        /// Observes one Task under the timeout-only bound. Returns whether it actually terminated
        /// (completed or faulted) plus any fault; a null Task terminated trivially. A bound expiry
        /// returns <c>false</c> — the write is still running.
        /// </summary>
        private async Task<(bool Terminated, Exception? Fault)> ObserveTaskAsync(Task? task, TimeSpan bound)
        {
            if (task is null)
                return (true, null);

            try
            {
                await task.WaitAsync(bound);
                return (true, null);
            }
            catch (TimeoutException)
            {
                return (false, new TimeoutException(
                    $"Write Task '{_thread.Name}' did not complete within {bound}. WaitAsync bounds the " +
                    "OBSERVATION only — the write is still running, so nothing it may touch was deleted."));
            }
            catch (Exception ex)
            {
                // The write Task itself faulted: it DID terminate; the fault is surfaced after cleanup.
                return (true, ex);
            }
        }
    }

    /// <summary>
    /// DETERMINISTIC contention regression: the snapshot read inside
    /// <see cref="ConfigRepoManager.WriteConfigAsync"/> must participate in the instance's catalog
    /// monitor. Evidence chain:
    /// (1) fixtures are created and warmed BEFORE the contention window;
    /// (2) the test thread enters the catalog monitor;
    /// (3) a dedicated synchronous thread starts <see cref="ConfigRepoManager.WriteConfigAsync"/>
    ///     and is POSITIVELY observed BLOCKED on that monitor (a snapshot-free write completes
    ///     instead and fails the observation immediately — no timed non-completion proxy);
    /// (4) while the write is provably parked, a synchronized catalog update commits a
    ///     distinguishing model entry BEHIND it;
    /// (5) the monitor is released BEFORE any await, then the invocation thread and the returned
    ///     write Task are driven to OBSERVED termination outside the monitor (bounded join plus the
    ///     timeout-only <c>WaitAsync</c> observation, with a re-read for late publication) — on
    ///     success AND on every failure path after startup — and the persisted file must carry the
    ///     distinguishing values, only possible if the snapshot was captured after acquiring the
    ///     monitor.
    /// <para>
    /// Deletion ordering: the worker writes into its OWN directory (never the shared fixture
    /// directory that Dispose deletes unconditionally), and that directory is removed ONLY after
    /// termination was positively observed. A bound expiry is an OBSERVATION timeout, not
    /// termination — it fails the test and deliberately leaves the directory in place.
    /// Fault marshalling: the primary (step/assertion) exception is captured BEFORE cleanup and is
    /// emitted afterwards as the authoritative fault, always accompanied by every cleanup fault
    /// (join/observation timeouts, body faults, write-Task faults, helper faults, and any
    /// directory-deletion failure) — none is discarded. Emission happens LAST, after the guarded
    /// deletion attempt, so a failing <c>Directory.Delete</c> can never replace a pending fault.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WriteConfigAsync_SnapshotRead_WaitsForHeldCatalogMonitor()
    {
        // The contention worker gets its OWN directory, deliberately OUTSIDE the shared fixture
        // directory: the fixture's Dispose deletes `_tempDir` unconditionally, which must never be
        // able to race a write that has not been observed as terminated. This directory is deleted
        // by this test ONLY after positive termination (see the final finally).
        var contentionDir = Path.Combine(
            Path.GetTempPath(), $"copilothive-cfgtest-contention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contentionDir);

        // Fixtures created and warmed BEFORE contention: one write settles the file/cache path.
        var warmConfig = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "warm", ContextWindow = 1 }]
            }
        };
        var manager = new FakeConfigRepoManager("https://example.com/config.git", contentionDir);
        await manager.WriteConfigAsync(warmConfig, TestContext.Current.CancellationToken);
        _ = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);

        // The live config the contending write will serialize: WITHOUT the distinguishing entry.
        var liveConfig = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "pre", ContextWindow = 10 }]
            }
        };

        // The distinguishing synchronized mutation committed BEHIND the blocked write, directly
        // on the live config object whose snapshot the write will capture.
        var writerSucceeded = false;

        var writeThread = new WriteCapturedThread("writeconfig-catalog-monitor-writer", manager, liveConfig);

        string? contentionFailure = null;
        var committed = false;
        var monitorEntered = false;

        // The PRIMARY exception (any step failure inside the contention region, e.g. the
        // distinguishing update throwing) is captured BEFORE cleanup runs, so cleanup always
        // executes and the primary is still emitted afterwards as the authoritative fault —
        // together with EVERY cleanup fault, none of which is ever discarded.
        Exception? primaryFault = null;
        var cleanupFaults = new List<Exception>();

        try
        {
            var monitor = CatalogLockField.GetValue(liveConfig)!;
            Monitor.Enter(monitor);
            monitorEntered = true;
            writeThread.Start();

            contentionFailure = writeThread.WaitUntilBlockedOnMonitor(WriteContentionObservationBound);
            if (contentionFailure is null)
            {
                writerSucceeded = liveConfig.TryAddAvailableModel(
                    new AvailableModelRequest("post", 99, null, null));
                committed = true;
            }
        }
        catch (Exception ex)
        {
            primaryFault = ex;
        }
        finally
        {
            // ALWAYS release the monitor — and release it BEFORE any await, so the monitor is
            // never held across the asynchronous termination wait below.
            if (monitorEntered && Monitor.IsEntered(CatalogLockField.GetValue(liveConfig)!))
                Monitor.Exit(CatalogLockField.GetValue(liveConfig)!);
        }

        // Cleanup OUTSIDE the monitor, on the success path AND on every failure path after
        // startup: drive the invocation thread and the returned write Task to OBSERVED
        // termination. `terminated` is the only authority for deleting anything the write could
        // still touch — a bound expiry is an observation timeout, NOT termination.
        var terminated = false;
        try
        {
            (terminated, var terminationFault) = await writeThread.EnsureTerminatedAsync(
                WriteThreadJoinBound, WriteTaskObservationBound);
            if (terminationFault is not null)
                cleanupFaults.Add(terminationFault);
        }
        catch (Exception ex)
        {
            // Never discard a cleanup fault, even one thrown by the termination helper itself.
            cleanupFaults.Add(ex);
        }

        if (writeThread.BodyFault is not null)
            cleanupFaults.Add(writeThread.BodyFault);

        if (!terminated)
            cleanupFaults.Add(new TimeoutException(
                $"Write worker '{writeThread.Name}' was NOT observed terminated within its bounds " +
                $"(join {WriteThreadJoinBound}, task observation {WriteTaskObservationBound}). The test " +
                $"fails WITHOUT deleting '{contentionDir}', which the still-running write may touch."));

        // Assertions and disk verification run only when no worker fault was accumulated (an
        // accumulated fault is authoritative and would make them noise). Any failure here is
        // CAPTURED, never thrown, so the guarded deletion below still runs BEFORE emission —
        // a delete failure can therefore never replace this fault.
        if (primaryFault is null && cleanupFaults.Count == 0)
        {
            try
            {
                Assert.True(contentionFailure is null, contentionFailure);
                Assert.True(committed, "The distinguishing catalog update was never committed behind the blocked write.");
                Assert.True(writerSucceeded, "TryAddAvailableModel behind the blocked write did not succeed.");

                // Verify the distinguishing values FROM DISK (not the manager cache): the write must
                // have snapshotted the catalog AFTER acquiring the monitor, so the committed entry and
                // its values are in the persisted YAML.
                var disk = await File.ReadAllTextAsync(
                    Path.Combine(contentionDir, "hive-config.yaml"), TestContext.Current.CancellationToken);
                var parsed = ConfigRepoManager.ParseConfig(disk);
                var available = parsed.Models!.AvailableModels!;
                Assert.Equal(2, available.Count);
                Assert.Equal("pre", available[0].Name);
                Assert.Equal(10, available[0].ContextWindow);
                Assert.Equal("post", available[1].Name);
                Assert.Equal(99, available[1].ContextWindow);
            }
            catch (Exception ex)
            {
                // The assertion/read failure is the authoritative primary fault.
                primaryFault = ex;
            }
        }

        // GUARDED deletion, performed BEFORE any fault is emitted. The contention worker writes
        // into its OWN directory (never the shared fixture directory that Dispose deletes
        // unconditionally), and that directory is deleted ONLY after termination was positively
        // observed. On an unconfirmed termination it is deliberately left in place: no deletion may
        // race a write that is still running. A deletion failure is COLLECTED as a cleanup fault —
        // never swallowed, and never allowed to replace the pending authoritative fault.
        try
        {
            if (terminated && Directory.Exists(contentionDir))
                Directory.Delete(contentionDir, recursive: true);
        }
        catch (Exception ex)
        {
            cleanupFaults.Add(ex);
        }

        // Emission LAST: the primary fault stays authoritative and is always accompanied by every
        // cleanup fault (termination timeout, body fault, write-Task fault, helper fault, and any
        // directory-deletion failure).
        if (primaryFault is not null && cleanupFaults.Count == 0)
            ExceptionDispatchInfo.Capture(primaryFault).Throw();

        if (primaryFault is not null || cleanupFaults.Count > 0)
        {
            var all = new List<Exception>();
            if (primaryFault is not null)
                all.Add(primaryFault);
            all.AddRange(cleanupFaults);
            throw new AggregateException(
                "Contention test failed. Authoritative (first) fault: " +
                (primaryFault?.Message ?? cleanupFaults[0].Message),
                all);
        }
    }

    // ── Release config parsing ───────────────────────────────────────────────

    [Fact]
    public void ParseConfig_ReleaseSection_ParsesFields()
    {
        const string yaml = """
            version: "1.0"
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
                default_branch: main
                release:
                  merge_to: main
                  tag_branch: main
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        var repo = Assert.Single(config.Repositories);
        Assert.NotNull(repo.Release);
        Assert.Equal("main", repo.Release!.MergeTo);
        Assert.Equal("main", repo.Release!.TagBranch);
    }

    [Fact]
    public void ParseConfig_NoReleaseSection_ReleaseIsNull()
    {
        const string yaml = """
            version: "1.0"
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
                default_branch: main
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        var repo = Assert.Single(config.Repositories);
        Assert.Null(repo.Release);
    }

    [Fact]
    public void ParseConfig_EmptyReleaseObject_NormalizesToNull()
    {
        const string yaml = """
            version: "1.0"
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
                default_branch: main
                release: {}
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        var repo = Assert.Single(config.Repositories);
        Assert.Null(repo.Release);
    }

    [Fact]
    public void ParseConfig_ReleaseWithWhitespaceOnly_NormalizesToNull()
    {
        const string yaml = """
            version: "1.0"
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
                default_branch: main
                release:
                  merge_to: ""
                  tag_branch: "  "
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        var repo = Assert.Single(config.Repositories);
        Assert.Null(repo.Release);
    }

    [Fact]
    public void ParseConfig_ReleaseWithOnlyMergeTo_PreservesRelease()
    {
        const string yaml = """
            version: "1.0"
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
                default_branch: main
                release:
                  merge_to: main
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        var repo = Assert.Single(config.Repositories);
        Assert.NotNull(repo.Release);
        Assert.Equal("main", repo.Release!.MergeTo);
        Assert.Null(repo.Release!.TagBranch);
    }

    [Fact]
    public async Task WriteConfigAsync_SerializesReleaseWithSnakeCaseKeys()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "test-model" },
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "my-app",
                    Url = "https://github.com/org/my-app.git",
                    DefaultBranch = "main",
                    Release = new ReleaseRepoConfig { MergeTo = "main", TagBranch = "main" }
                }
            ]
        };
        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);

        await manager.WriteConfigAsync(config, TestContext.Current.CancellationToken);

        var yaml = await File.ReadAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"), TestContext.Current.CancellationToken);
        Assert.Contains("release:", yaml);
        Assert.Contains("merge_to: main", yaml);
        Assert.Contains("tag_branch: main", yaml);
    }

    [Fact]
    public async Task WriteConfigAsync_NullRelease_OmitsReleaseSection()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "test-model" },
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "my-app",
                    Url = "https://github.com/org/my-app.git",
                    DefaultBranch = "main"
                }
            ]
        };
        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);

        await manager.WriteConfigAsync(config, TestContext.Current.CancellationToken);

        var yaml = await File.ReadAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("release:", yaml);
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private async Task<ConfigRepoManager> CreateManagerWithConfigAsync(string yaml)
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "hive-config.yaml"), yaml, TestContext.Current.CancellationToken);
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        return manager;
    }

    // ── Semaphore serialization ───────────────────────────────────────────────

    [Fact]
    public async Task SyncRepoAsync_ConcurrentCalls_AreSerializedBySemaphore()
    {
        // Arrange — grab the _gitLock semaphore via reflection and hold it.
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        var gitLock = GetGitLock(manager);

        // Pre-acquire the semaphore to block any git operation.
        await gitLock.WaitAsync(TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Start a SyncRepoAsync that will block waiting for the semaphore.
        var blockingTask = Task.Run(async () =>
        {
            started.TrySetResult(true);
            // This will block on WaitAsync until we release the semaphore below.
            await manager.SyncRepoAsync(ct);
        }, ct);

        // Wait until the task has at least started.
        await started.Task;

        // The task should NOT complete while the semaphore is held.
        var completedEarly = await Task.WhenAny(blockingTask, Task.Delay(100, ct)) == blockingTask;
        Assert.False(completedEarly, "SyncRepoAsync should be blocked while the semaphore is held");

        // Release the semaphore — note: the call will fail (no .git, no real remote),
        // but it will at least proceed past the lock.
        gitLock.Release();

        // The task should now run and eventually throw (no real git repo), but it must
        // have been unblocked by the semaphore release.
        await Assert.ThrowsAsync<InvalidOperationException>(() => blockingTask);
    }

    [Fact]
    public async Task CommitAllChangesAsync_ConcurrentCalls_AreSerializedBySemaphore()
    {
        // Arrange — grab the _gitLock semaphore via reflection and hold it.
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        var gitLock = GetGitLock(manager);

        await gitLock.WaitAsync(TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var blockingTask = Task.Run(async () =>
        {
            started.TrySetResult(true);
            await manager.CommitAllChangesAsync("test commit", ct);
        }, ct);

        await started.Task;

        var completedEarly = await Task.WhenAny(blockingTask, Task.Delay(100, ct)) == blockingTask;
        Assert.False(completedEarly, "CommitAllChangesAsync should be blocked while the semaphore is held");

        gitLock.Release();

        await Assert.ThrowsAsync<InvalidOperationException>(() => blockingTask);
    }

    [Fact]
    public async Task CommitFileAsync_ConcurrentCalls_AreSerializedBySemaphore()
    {
        // Arrange — grab the _gitLock semaphore via reflection and hold it.
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        var gitLock = GetGitLock(manager);

        await gitLock.WaitAsync(TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var blockingTask = Task.Run(async () =>
        {
            started.TrySetResult(true);
            await manager.CommitFileAsync("config.txt", "update config", ct);
        }, ct);

        await started.Task;

        var completedEarly = await Task.WhenAny(blockingTask, Task.Delay(100, ct)) == blockingTask;
        Assert.False(completedEarly, "CommitFileAsync should be blocked while the semaphore is held");

        gitLock.Release();

        await Assert.ThrowsAsync<InvalidOperationException>(() => blockingTask);
    }

    // ── Merge conflict abort ──────────────────────────────────────────────────

    [Fact]
    public async Task SyncRepoAsync_WhenPullFails_AttemptsMergeAbortAndRethrows()
    {
        // Arrange — set up a local bare remote and two clones that create a conflict.
        var bareDir = Path.Combine(Path.GetTempPath(), $"cfgtest-bare-{Guid.NewGuid():N}");
        var clone1Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone1-{Guid.NewGuid():N}");
        var clone2Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone2-{Guid.NewGuid():N}");

        try
        {
            // Create bare repo and initial commit.
            Directory.CreateDirectory(bareDir);
            await RunGitCommandAsync(bareDir, ["init", "--bare"]);
            Directory.CreateDirectory(clone1Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone1Dir)!,
                ["clone", bareDir, Path.GetFileName(clone1Dir)]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.email", "test@test.com"]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.name", "Test"]);

            // Write initial file and push.
            var filePath = Path.Combine(clone1Dir, "conflict.txt");
            await File.WriteAllTextAsync(filePath, "line1\n", TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "conflict.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "initial"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: clone from bare, modify conflict.txt, commit but DON'T push yet.
            Directory.CreateDirectory(clone2Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone2Dir)!,
                ["clone", bareDir, Path.GetFileName(clone2Dir)]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.email", "test2@test.com"]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.name", "Test2"]);
            await File.WriteAllTextAsync(Path.Combine(clone2Dir, "conflict.txt"), "clone2 change\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone2Dir, ["add", "conflict.txt"]);
            await RunGitCommandAsync(clone2Dir, ["commit", "-m", "clone2 local commit"]);

            // Clone1: push a conflicting change to the same file.
            await File.WriteAllTextAsync(filePath, "clone1 different change\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "conflict.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "clone1 conflict"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Now clone2 has a local commit that conflicts with the remote.
            // Set merge strategy to always merge (not fast-forward) so a conflict occurs.
            await RunGitCommandAsync(clone2Dir, ["config", "pull.rebase", "false"]);

            // Act — SyncRepoAsync on clone2 should fail (merge conflict) and
            // attempt git merge --abort before rethrowing.
            var manager = new ConfigRepoManager(bareDir, clone2Dir);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.SyncRepoAsync(TestContext.Current.CancellationToken));

            // Assert — the exception comes from the pull failure (not abort).
            Assert.Contains("git exited with code", ex.Message);

            // After TryAbortMergeAsync the repo should be in a clean non-merging state.
            // git status should not show "MERGING".
            var (statusOutput, _) = await RunGitCommandRawAsync(clone2Dir, ["status"]);
            Assert.DoesNotContain("MERGING", statusOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var dir in new[] { bareDir, clone1Dir, clone2Dir })
                if (Directory.Exists(dir))
                    try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Conflict recovery (PushWithConflictRecoveryAsync via CommitFileAsync) ──

    [Fact]
    public async Task CommitFileAsync_WhenPullConflicts_RebasesAndPushes()
    {
        var bareDir = Path.Combine(Path.GetTempPath(), $"cfgtest-bare-{Guid.NewGuid():N}");
        var clone1Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone1-{Guid.NewGuid():N}");
        var clone2Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone2-{Guid.NewGuid():N}");

        try
        {
            // Create bare repo and initial commit in clone1.
            Directory.CreateDirectory(bareDir);
            await RunGitCommandAsync(bareDir, ["init", "--bare"]);
            Directory.CreateDirectory(clone1Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone1Dir)!,
                ["clone", bareDir, Path.GetFileName(clone1Dir)]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.email", "test@test.com"]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.name", "Test1"]);

            // Write initial file and push.
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "config.txt"), "base content\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "initial"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: clone from bare.
            Directory.CreateDirectory(clone2Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone2Dir)!,
                ["clone", bareDir, Path.GetFileName(clone2Dir)]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.email", "test2@test.com"]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.name", "Test2"]);

            // Clone1: push a conflicting change to the same file (different line, rebase-friendly).
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "config.txt"), "base content\nremote line\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "remote change"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: write a local change to a DIFFERENT line so rebase can succeed.
            await File.WriteAllTextAsync(Path.Combine(clone2Dir, "config.txt"), "local content\nbase content\n",
                TestContext.Current.CancellationToken);

            // Set pull.rebase false so plain pull attempts a merge (which will conflict).
            await RunGitCommandAsync(clone2Dir, ["config", "pull.rebase", "false"]);

            // Act — CommitFileAsync should detect the conflict, abort merge, reset,
            // rebase onto remote, and push. No exception should be thrown.
            var manager = new ConfigRepoManager(bareDir, clone2Dir);
            await manager.CommitFileAsync("config.txt", "local change", TestContext.Current.CancellationToken);

            // Assert — the remote now has both commits (rebase succeeded).
            // Clone the remote to a fresh clone to verify state.
            var verifyDir = Path.Combine(Path.GetTempPath(), $"cfgtest-verify-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(verifyDir);
                await RunGitCommandAsync(Path.GetDirectoryName(verifyDir)!,
                    ["clone", bareDir, Path.GetFileName(verifyDir)]);
                var remoteContent = await File.ReadAllTextAsync(
                    Path.Combine(verifyDir, "config.txt"), TestContext.Current.CancellationToken);

                // After a successful rebase, both lines should be present since they
                // modified different parts of the file.
                Assert.Contains("base content", remoteContent);
                Assert.Contains("local content", remoteContent);
                Assert.Contains("remote line", remoteContent);

                // Verify both commits exist on the remote.
                var (logOutput, _) = await RunGitCommandRawAsync(verifyDir, ["log", "--oneline"]);
                Assert.Contains("local change", logOutput);
                Assert.Contains("remote change", logOutput);
            }
            finally
            {
                if (Directory.Exists(verifyDir))
                    try { Directory.Delete(verifyDir, recursive: true); } catch { }
            }

            // Clone2 should be in a clean state (not mid-merge or mid-rebase).
            var (statusOutput, _) = await RunGitCommandRawAsync(clone2Dir, ["status"]);
            Assert.DoesNotContain("MERGING", statusOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("REBASE", statusOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("working tree clean", statusOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var dir in new[] { bareDir, clone1Dir, clone2Dir })
                if (Directory.Exists(dir))
                    try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CommitFileAsync_WhenPullAndRebaseBothFail_ResetsAndPushes()
    {
        var bareDir = Path.Combine(Path.GetTempPath(), $"cfgtest-bare-{Guid.NewGuid():N}");
        var clone1Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone1-{Guid.NewGuid():N}");
        var clone2Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone2-{Guid.NewGuid():N}");

        try
        {
            // Create bare repo and initial commit in clone1.
            Directory.CreateDirectory(bareDir);
            await RunGitCommandAsync(bareDir, ["init", "--bare"]);
            Directory.CreateDirectory(clone1Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone1Dir)!,
                ["clone", bareDir, Path.GetFileName(clone1Dir)]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.email", "test@test.com"]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.name", "Test1"]);

            // Write initial file and push.
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "config.txt"), "line1\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "initial"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: clone from bare.
            Directory.CreateDirectory(clone2Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone2Dir)!,
                ["clone", bareDir, Path.GetFileName(clone2Dir)]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.email", "test2@test.com"]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.name", "Test2"]);

            // Clone1: push a conflicting change to the SAME line.
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "config.txt"), "remote-only-content\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "remote change"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: write a local change to the SAME line (will conflict on rebase too).
            await File.WriteAllTextAsync(Path.Combine(clone2Dir, "config.txt"), "local-only-content\n",
                TestContext.Current.CancellationToken);

            // Set pull.rebase false so plain pull attempts a merge (which will conflict).
            await RunGitCommandAsync(clone2Dir, ["config", "pull.rebase", "false"]);

            // Act — CommitFileAsync should: pull fails → abort merge → reset →
            // pull --rebase fails → abort rebase → reset hard → push local commit.
            // The push will also fail (non-fast-forward) since the local commit diverged.
            // The exception from git push propagates, but the repo must be in a clean state.
            var manager = new ConfigRepoManager(bareDir, clone2Dir);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.CommitFileAsync("config.txt", "local change", TestContext.Current.CancellationToken));

            // The exception should come from the push failure (not a merge/rebase conflict).
            Assert.Contains("git exited with code", ex.Message);

            // The repo should be in a clean state — not mid-merge or mid-rebase.
            // This is the key assertion: even though the push failed, the recovery
            // logic (abort merge, reset hard, abort rebase, reset hard) ensured the
            // repo is not stuck in a conflicted state.
            var (statusOutput, _) = await RunGitCommandRawAsync(clone2Dir, ["status"]);
            Assert.DoesNotContain("MERGING", statusOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("REBASE", statusOutput, StringComparison.OrdinalIgnoreCase);

            // Verify the local file still contains the local commit's content
            // (reset --hard HEAD preserved the local commit).
            var localContent = await File.ReadAllTextAsync(
                Path.Combine(clone2Dir, "config.txt"), TestContext.Current.CancellationToken);
            Assert.Contains("local-only-content", localContent);

            // Verify the local commit exists in the local log.
            var (logOutput, _) = await RunGitCommandRawAsync(clone2Dir, ["log", "--oneline"]);
            Assert.Contains("local change", logOutput);
        }
        finally
        {
            foreach (var dir in new[] { bareDir, clone1Dir, clone2Dir })
                if (Directory.Exists(dir))
                    try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CommitAllChangesAsync_WhenPullConflicts_RebasesAndPushes()
    {
        var bareDir = Path.Combine(Path.GetTempPath(), $"cfgtest-bare-{Guid.NewGuid():N}");
        var clone1Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone1-{Guid.NewGuid():N}");
        var clone2Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone2-{Guid.NewGuid():N}");

        try
        {
            // Create bare repo and initial commit in clone1.
            Directory.CreateDirectory(bareDir);
            await RunGitCommandAsync(bareDir, ["init", "--bare"]);
            Directory.CreateDirectory(clone1Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone1Dir)!,
                ["clone", bareDir, Path.GetFileName(clone1Dir)]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.email", "test@test.com"]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.name", "Test1"]);

            // Write initial file and push.
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "data.txt"), "base\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "data.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "initial"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: clone from bare.
            Directory.CreateDirectory(clone2Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone2Dir)!,
                ["clone", bareDir, Path.GetFileName(clone2Dir)]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.email", "test2@test.com"]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.name", "Test2"]);

            // Clone1: push a conflicting change to the same file (different line for rebase success).
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "data.txt"), "base\nremote line\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "data.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "remote change"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: write a local change to a DIFFERENT line so rebase can succeed.
            await File.WriteAllTextAsync(Path.Combine(clone2Dir, "data.txt"), "local line\nbase\n",
                TestContext.Current.CancellationToken);

            // Set pull.rebase false so plain pull attempts a merge (which will conflict).
            await RunGitCommandAsync(clone2Dir, ["config", "pull.rebase", "false"]);

            // Act — CommitAllChangesAsync should detect the conflict, abort merge,
            // reset, rebase onto remote, and push. No exception should be thrown.
            var manager = new ConfigRepoManager(bareDir, clone2Dir);
            await manager.CommitAllChangesAsync("local change via commit-all", TestContext.Current.CancellationToken);

            // Assert — the remote now has both commits (rebase succeeded).
            var verifyDir = Path.Combine(Path.GetTempPath(), $"cfgtest-verify-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(verifyDir);
                await RunGitCommandAsync(Path.GetDirectoryName(verifyDir)!,
                    ["clone", bareDir, Path.GetFileName(verifyDir)]);
                var remoteContent = await File.ReadAllTextAsync(
                    Path.Combine(verifyDir, "data.txt"), TestContext.Current.CancellationToken);

                // After a successful rebase, both lines should be present.
                Assert.Contains("base", remoteContent);
                Assert.Contains("local line", remoteContent);
                Assert.Contains("remote line", remoteContent);

                // Verify the local commit exists on the remote.
                var (logOutput, _) = await RunGitCommandRawAsync(verifyDir, ["log", "--oneline"]);
                Assert.Contains("local change via commit-all", logOutput);
                Assert.Contains("remote change", logOutput);
            }
            finally
            {
                if (Directory.Exists(verifyDir))
                    try { Directory.Delete(verifyDir, recursive: true); } catch { }
            }

            // Clone2 should be in a clean state.
            var (statusOutput, _) = await RunGitCommandRawAsync(clone2Dir, ["status"]);
            Assert.DoesNotContain("MERGING", statusOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("REBASE", statusOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var dir in new[] { bareDir, clone1Dir, clone2Dir })
                if (Directory.Exists(dir))
                    try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── ResetToRemoteAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task ResetToRemoteAsync_ResetsToRemoteState()
    {
        var bareDir = Path.Combine(Path.GetTempPath(), $"cfgtest-bare-{Guid.NewGuid():N}");
        var clone1Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone1-{Guid.NewGuid():N}");
        var clone2Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone2-{Guid.NewGuid():N}");

        try
        {
            // Create bare repo and initial commit in clone1.
            Directory.CreateDirectory(bareDir);
            await RunGitCommandAsync(bareDir, ["init", "--bare"]);
            Directory.CreateDirectory(clone1Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone1Dir)!,
                ["clone", bareDir, Path.GetFileName(clone1Dir)]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.email", "test@test.com"]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.name", "Test1"]);

            // Write initial file and push.
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "config.txt"), "remote content\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "remote commit"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: clone from bare, then make local commits.
            Directory.CreateDirectory(clone2Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone2Dir)!,
                ["clone", bareDir, Path.GetFileName(clone2Dir)]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.email", "test2@test.com"]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.name", "Test2"]);

            // Make a local commit that diverges from remote.
            await File.WriteAllTextAsync(Path.Combine(clone2Dir, "config.txt"), "local content\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone2Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone2Dir, ["commit", "-m", "local commit"]);

            // Act — ResetToRemoteAsync should discard local commits and match remote.
            var manager = new ConfigRepoManager(bareDir, clone2Dir);
            await manager.ResetToRemoteAsync(TestContext.Current.CancellationToken);

            // Assert — local file matches remote content.
            var localContent = await File.ReadAllTextAsync(
                Path.Combine(clone2Dir, "config.txt"), TestContext.Current.CancellationToken);
            Assert.Equal("remote content\n", localContent);

            // Git status should be clean (no local changes, working tree clean).
            var (statusOutput, _) = await RunGitCommandRawAsync(clone2Dir, ["status"]);
            Assert.Contains("working tree clean", statusOutput, StringComparison.OrdinalIgnoreCase);

            // Local log should match remote log — no "local commit".
            var (logOutput, _) = await RunGitCommandRawAsync(clone2Dir, ["log", "--oneline"]);
            Assert.Contains("remote commit", logOutput);
            Assert.DoesNotContain("local commit", logOutput);
        }
        finally
        {
            foreach (var dir in new[] { bareDir, clone1Dir, clone2Dir })
                if (Directory.Exists(dir))
                    try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ResetToRemoteAsync_AbortsActiveMerge()
    {
        var bareDir = Path.Combine(Path.GetTempPath(), $"cfgtest-bare-{Guid.NewGuid():N}");
        var clone1Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone1-{Guid.NewGuid():N}");
        var clone2Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone2-{Guid.NewGuid():N}");

        try
        {
            // Create bare repo and initial commit in clone1.
            Directory.CreateDirectory(bareDir);
            await RunGitCommandAsync(bareDir, ["init", "--bare"]);
            Directory.CreateDirectory(clone1Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone1Dir)!,
                ["clone", bareDir, Path.GetFileName(clone1Dir)]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.email", "test@test.com"]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.name", "Test1"]);

            // Write initial file and push.
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "config.txt"), "base\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "initial"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: clone from bare (at "initial" commit), then make a local conflicting commit.
            Directory.CreateDirectory(clone2Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone2Dir)!,
                ["clone", bareDir, Path.GetFileName(clone2Dir)]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.email", "test2@test.com"]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.name", "Test2"]);

            await File.WriteAllTextAsync(Path.Combine(clone2Dir, "config.txt"), "local change\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone2Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone2Dir, ["commit", "-m", "local change"]);

            // Clone1: push a conflicting change to the SAME line (diverges from clone2).
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "config.txt"), "remote change\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "remote change"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: fetch the remote change, then start a merge that will conflict.
            await RunGitCommandAsync(clone2Dir, ["fetch", "origin"]);
            // merge will fail with a conflict — this puts the repo in a merging state.
            var (_, _) = await RunGitCommandRawAsync(clone2Dir, ["merge", "origin/HEAD"]);
            // Verify merge is in progress (MERGE_HEAD exists) and config.txt is unmerged.
            var (mergeHead, _) = await RunGitCommandRawAsync(clone2Dir, ["rev-parse", "--verify", "MERGE_HEAD"]);
            Assert.False(string.IsNullOrWhiteSpace(mergeHead), "Expected MERGE_HEAD to exist after conflicting merge");

            var (unmergedFiles, _) = await RunGitCommandRawAsync(clone2Dir, ["diff", "--name-only", "--diff-filter=U"]);
            Assert.Contains("config.txt", unmergedFiles.Trim(), StringComparison.OrdinalIgnoreCase);

            // Act — ResetToRemoteAsync should abort the merge and reset to remote.
            var manager = new ConfigRepoManager(bareDir, clone2Dir);
            await manager.ResetToRemoteAsync(TestContext.Current.CancellationToken);

            // Assert — repo is no longer in a merging state.
            var (mergeHeadAfter, _) = await RunGitCommandRawAsync(clone2Dir, ["rev-parse", "--verify", "MERGE_HEAD"]);
            Assert.True(string.IsNullOrWhiteSpace(mergeHeadAfter), "MERGE_HEAD should not exist after reset");

            var (unmergedAfter, _) = await RunGitCommandRawAsync(clone2Dir, ["diff", "--name-only", "--diff-filter=U"]);
            Assert.Equal("", unmergedAfter.Trim());

            // The local content should now match the remote.
            var localContent = await File.ReadAllTextAsync(
                Path.Combine(clone2Dir, "config.txt"), TestContext.Current.CancellationToken);
            Assert.Equal("remote change\n", localContent);

            // Status should be clean.
            var (porcelainAfter, _) = await RunGitCommandRawAsync(clone2Dir, ["status", "--porcelain"]);
            Assert.Equal("", porcelainAfter.Trim());
        }
        finally
        {
            foreach (var dir in new[] { bareDir, clone1Dir, clone2Dir })
                if (Directory.Exists(dir))
                    try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── PushOnlyAsync (no-diff fast path) ─────────────────────────────────────

    /// <summary>
    /// Creates a bare remote plus a clone that tracks it, with one initial commit.
    /// Returns (bareDir, cloneDir).
    /// </summary>
    private static async Task<(string BareDir, string CloneDir)> CreateRemoteAndCloneAsync()
    {
        var bareDir = Path.Combine(Path.GetTempPath(), $"cfgtest-bare-{Guid.NewGuid():N}");
        var seedDir = Path.Combine(Path.GetTempPath(), $"cfgtest-seed-{Guid.NewGuid():N}");
        var cloneDir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone-{Guid.NewGuid():N}");

        Directory.CreateDirectory(bareDir);
        await RunGitCommandAsync(bareDir, ["init", "--bare"]);

        Directory.CreateDirectory(seedDir);
        await RunGitCommandAsync(Path.GetDirectoryName(seedDir)!, ["clone", bareDir, Path.GetFileName(seedDir)]);
        await RunGitCommandAsync(seedDir, ["config", "user.email", "seed@test.com"]);
        await RunGitCommandAsync(seedDir, ["config", "user.name", "Seed"]);
        await File.WriteAllTextAsync(Path.Combine(seedDir, "tracked.txt"), "tracked\n");
        await File.WriteAllTextAsync(Path.Combine(seedDir, "other.txt"), "other\n");
        await RunGitCommandAsync(seedDir, ["add", "."]);
        await RunGitCommandAsync(seedDir, ["commit", "-m", "initial"]);
        await RunGitCommandAsync(seedDir, ["push", "origin", "HEAD"]);
        try { Directory.Delete(seedDir, recursive: true); } catch { /* best-effort */ }

        Directory.CreateDirectory(cloneDir);
        await RunGitCommandAsync(Path.GetDirectoryName(cloneDir)!, ["clone", bareDir, Path.GetFileName(cloneDir)]);
        await RunGitCommandAsync(cloneDir, ["config", "user.email", "clone@test.com"]);
        await RunGitCommandAsync(cloneDir, ["config", "user.name", "Clone"]);

        return (bareDir, cloneDir);
    }

    /// <summary>Creates a standalone git repo with one commit and NO remote configured.</summary>
    private static async Task<string> CreateRepoWithoutRemoteAsync()
    {
        var repoDir = Path.Combine(Path.GetTempPath(), $"cfgtest-noremote-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoDir);
        await RunGitCommandAsync(repoDir, ["init"]);
        await RunGitCommandAsync(repoDir, ["config", "user.email", "local@test.com"]);
        await RunGitCommandAsync(repoDir, ["config", "user.name", "Local"]);
        await File.WriteAllTextAsync(Path.Combine(repoDir, "tracked.txt"), "tracked\n");
        await File.WriteAllTextAsync(Path.Combine(repoDir, "other.txt"), "other\n");
        await RunGitCommandAsync(repoDir, ["add", "."]);
        await RunGitCommandAsync(repoDir, ["commit", "-m", "initial"]);
        return repoDir;
    }

    private static void CleanupDirs(params string[] dirs)
    {
        foreach (var dir in dirs)
            if (Directory.Exists(dir))
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task DeleteFileAsync_FileAlreadyRemovedFromIndex_DoesNotThrowAndStillPushes()
    {
        var (bareDir, cloneDir) = await CreateRemoteAndCloneAsync();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // Remove the file and commit the removal — the index no longer knows about it.
            File.Delete(Path.Combine(cloneDir, "tracked.txt"));
            await RunGitCommandAsync(cloneDir, ["rm", "--cached", "tracked.txt"]);
            await RunGitCommandAsync(cloneDir, ["commit", "-m", "already removed"]);

            var manager = new ConfigRepoManager(bareDir, cloneDir);

            // Act — a retry of the same deletion must not fail thanks to --ignore-unmatch.
            await manager.DeleteFileAsync("tracked.txt", "retry deletion", ct);

            // Assert — the pending local commit was pushed by PushOnlyAsync.
            var (remoteLog, _) = await RunGitCommandRawAsync(bareDir, ["log", "--oneline"]);
            Assert.Contains("already removed", remoteLog);
        }
        finally
        {
            CleanupDirs(bareDir, cloneDir);
        }
    }

    [Fact]
    public async Task DeleteFileAsync_NothingToCommit_PushesPendingCommits()
    {
        var (bareDir, cloneDir) = await CreateRemoteAndCloneAsync();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // A local commit that has not yet been pushed.
            await File.WriteAllTextAsync(Path.Combine(cloneDir, "other.txt"), "changed\n", ct);
            await RunGitCommandAsync(cloneDir, ["add", "other.txt"]);
            await RunGitCommandAsync(cloneDir, ["commit", "-m", "unpushed local commit"]);

            var manager = new ConfigRepoManager(bareDir, cloneDir);

            // Act — nothing staged for "missing.txt" → no diff → PushOnlyAsync.
            await manager.DeleteFileAsync("missing.txt", "no-op deletion", ct);

            // Assert — the push happened.
            var (remoteLog, _) = await RunGitCommandRawAsync(bareDir, ["log", "--oneline"]);
            Assert.Contains("unpushed local commit", remoteLog);
        }
        finally
        {
            CleanupDirs(bareDir, cloneDir);
        }
    }

    [Fact]
    public async Task DeleteFileAsync_PushFails_PropagatesAndPreservesWorkingTree()
    {
        var repoDir = await CreateRepoWithoutRemoteAsync();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // An unrelated unstaged change that must survive a failed push.
            await File.WriteAllTextAsync(Path.Combine(repoDir, "other.txt"), "unstaged edit\n", ct);

            var manager = new ConfigRepoManager("https://example.com/config.git", repoDir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.DeleteFileAsync("missing.txt", "no-op deletion", ct));
            Assert.Contains("git exited with code", ex.Message);

            // No reset --hard was performed — the unstaged change is intact.
            var content = await File.ReadAllTextAsync(Path.Combine(repoDir, "other.txt"), ct);
            Assert.Equal("unstaged edit\n", content);
        }
        finally
        {
            CleanupDirs(repoDir);
        }
    }

    [Fact]
    public async Task CommitFileAsync_NothingToCommit_PushesPendingCommits()
    {
        var (bareDir, cloneDir) = await CreateRemoteAndCloneAsync();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            await File.WriteAllTextAsync(Path.Combine(cloneDir, "other.txt"), "changed\n", ct);
            await RunGitCommandAsync(cloneDir, ["add", "other.txt"]);
            await RunGitCommandAsync(cloneDir, ["commit", "-m", "unpushed commit for commitfile"]);

            var manager = new ConfigRepoManager(bareDir, cloneDir);

            // Act — tracked.txt is unchanged → nothing staged → PushOnlyAsync.
            await manager.CommitFileAsync("tracked.txt", "no-op commit", ct);

            var (remoteLog, _) = await RunGitCommandRawAsync(bareDir, ["log", "--oneline"]);
            Assert.Contains("unpushed commit for commitfile", remoteLog);
        }
        finally
        {
            CleanupDirs(bareDir, cloneDir);
        }
    }

    [Fact]
    public async Task CommitFileAsync_NothingToCommitAndPushFails_Propagates()
    {
        var repoDir = await CreateRepoWithoutRemoteAsync();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            var manager = new ConfigRepoManager("https://example.com/config.git", repoDir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.CommitFileAsync("tracked.txt", "no-op commit", ct));
            Assert.Contains("git exited with code", ex.Message);
        }
        finally
        {
            CleanupDirs(repoDir);
        }
    }

    [Fact]
    public async Task DeleteAndCommitFileAsync_PreCancelledToken_ThrowsOperationCanceled()
    {
        var (bareDir, cloneDir) = await CreateRemoteAndCloneAsync();
        try
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var manager = new ConfigRepoManager(bareDir, cloneDir);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => manager.DeleteFileAsync("missing.txt", "no-op deletion", cts.Token));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => manager.CommitFileAsync("tracked.txt", "no-op commit", cts.Token));
        }
        finally
        {
            CleanupDirs(bareDir, cloneDir);
        }
    }

    [Fact]
    public async Task CommitFileAsync_NoDiffPushOnly_DoesNotDiscardUnrelatedWorkingTreeChanges()
    {
        var (bareDir, cloneDir) = await CreateRemoteAndCloneAsync();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // Unrelated unstaged change.
            await File.WriteAllTextAsync(Path.Combine(cloneDir, "other.txt"), "local scratch\n", ct);

            var manager = new ConfigRepoManager(bareDir, cloneDir);

            // Act — tracked.txt is unchanged → no diff → PushOnlyAsync (non-destructive).
            await manager.CommitFileAsync("tracked.txt", "no-op commit", ct);

            // Assert — the unrelated change survived.
            var content = await File.ReadAllTextAsync(Path.Combine(cloneDir, "other.txt"), ct);
            Assert.Equal("local scratch\n", content);
        }
        finally
        {
            CleanupDirs(bareDir, cloneDir);
        }
    }

    // ── DeleteFilesAsync (batch delete, single commit) ────────────────────────

    [Fact]
    public async Task DeleteFileAsync_TrackedFile_CommitsAndPushesTheRemoval()
    {
        var (bareDir, cloneDir) = await CreateRemoteAndCloneAsync();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // The caller removes the file from the working tree first.
            File.Delete(Path.Combine(cloneDir, "tracked.txt"));

            var manager = new ConfigRepoManager(bareDir, cloneDir);

            await manager.DeleteFileAsync("tracked.txt", "single deletion", ct);

            // The removal was committed locally…
            var (tracked, _) = await RunGitCommandRawAsync(cloneDir, ["ls-files"]);
            Assert.DoesNotContain("tracked.txt", tracked, StringComparison.Ordinal);
            Assert.Contains("other.txt", tracked, StringComparison.Ordinal);

            // …and pushed to the remote.
            var (remoteLog, _) = await RunGitCommandRawAsync(bareDir, ["log", "--oneline"]);
            Assert.Contains("single deletion", remoteLog);
        }
        finally
        {
            CleanupDirs(bareDir, cloneDir);
        }
    }

    [Fact]
    public async Task DeleteFilesAsync_NullPaths_ThrowsArgumentNullException()
    {
        var (bareDir, cloneDir) = await CreateRemoteAndCloneAsync();
        try
        {
            var manager = new ConfigRepoManager(bareDir, cloneDir);

            await Assert.ThrowsAsync<ArgumentNullException>(
                () => manager.DeleteFilesAsync(null!, "batch deletion", TestContext.Current.CancellationToken));
        }
        finally
        {
            CleanupDirs(bareDir, cloneDir);
        }
    }

    [Fact]
    public async Task DeleteFilesAsync_EmptyPaths_ReturnsWithoutCommittingOrPushing()
    {
        // A repo without a remote makes any push fail — the early return must avoid git entirely.
        var repoDir = await CreateRepoWithoutRemoteAsync();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var (logBefore, _) = await RunGitCommandRawAsync(repoDir, ["rev-list", "--count", "HEAD"]);

            var manager = new ConfigRepoManager("https://example.com/config.git", repoDir);

            // Act — an empty batch is a no-op; it must not throw despite the missing remote.
            await manager.DeleteFilesAsync([], "batch deletion", ct);

            // Assert — no commit was created and the working tree is untouched.
            var (logAfter, _) = await RunGitCommandRawAsync(repoDir, ["rev-list", "--count", "HEAD"]);
            Assert.Equal(logBefore.Trim(), logAfter.Trim());
            Assert.True(File.Exists(Path.Combine(repoDir, "tracked.txt")));
            Assert.True(File.Exists(Path.Combine(repoDir, "other.txt")));
        }
        finally
        {
            CleanupDirs(repoDir);
        }
    }

    [Fact]
    public async Task DeleteFilesAsync_NothingToCommit_PushesPendingCommits()
    {
        var (bareDir, cloneDir) = await CreateRemoteAndCloneAsync();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // A local commit that has not yet been pushed.
            await File.WriteAllTextAsync(Path.Combine(cloneDir, "other.txt"), "changed\n", ct);
            await RunGitCommandAsync(cloneDir, ["add", "other.txt"]);
            await RunGitCommandAsync(cloneDir, ["commit", "-m", "unpushed batch local commit"]);

            var logBefore = (await RunGitCommandRawAsync(cloneDir, ["rev-list", "--count", "HEAD"])).output.Trim();

            var manager = new ConfigRepoManager(bareDir, cloneDir);

            // Act — none of the paths are tracked → nothing staged → PushOnlyAsync.
            await manager.DeleteFilesAsync(["missing-a.txt", "missing-b.txt"], "no-op batch deletion", ct);

            // Assert — the pending commit was pushed and no new commit was created.
            var (remoteLog, _) = await RunGitCommandRawAsync(bareDir, ["log", "--oneline"]);
            Assert.Contains("unpushed batch local commit", remoteLog);
            var logAfter = (await RunGitCommandRawAsync(cloneDir, ["rev-list", "--count", "HEAD"])).output.Trim();
            Assert.Equal(logBefore, logAfter);
        }
        finally
        {
            CleanupDirs(bareDir, cloneDir);
        }
    }

    [Fact]
    public async Task DeleteFilesAsync_MultipleTrackedFiles_RemovesAllInASingleCommit()
    {
        var (bareDir, cloneDir) = await CreateRemoteAndCloneAsync();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // The caller removes the files from the working tree first.
            File.Delete(Path.Combine(cloneDir, "tracked.txt"));
            File.Delete(Path.Combine(cloneDir, "other.txt"));

            var logBefore = int.Parse(
                (await RunGitCommandRawAsync(cloneDir, ["rev-list", "--count", "HEAD"])).output.Trim());

            var manager = new ConfigRepoManager(bareDir, cloneDir);

            await manager.DeleteFilesAsync(["tracked.txt", "other.txt"], "batch deletion", ct);

            // Exactly ONE commit for both removals.
            var logAfter = int.Parse(
                (await RunGitCommandRawAsync(cloneDir, ["rev-list", "--count", "HEAD"])).output.Trim());
            Assert.Equal(1, logAfter - logBefore);

            // Both paths are gone from the index.
            var (tracked, _) = await RunGitCommandRawAsync(cloneDir, ["ls-files"]);
            Assert.DoesNotContain("tracked.txt", tracked, StringComparison.Ordinal);
            Assert.DoesNotContain("other.txt", tracked, StringComparison.Ordinal);

            // And the single commit was pushed to the remote.
            var (remoteLog, _) = await RunGitCommandRawAsync(bareDir, ["log", "--oneline"]);
            Assert.Contains("batch deletion", remoteLog);
        }
        finally
        {
            CleanupDirs(bareDir, cloneDir);
        }
    }

    [Fact]
    public async Task DeleteFilesAsync_PreCancelledToken_ThrowsOperationCanceled()
    {
        var (bareDir, cloneDir) = await CreateRemoteAndCloneAsync();
        try
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var manager = new ConfigRepoManager(bareDir, cloneDir);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => manager.DeleteFilesAsync(["missing.txt"], "no-op batch deletion", cts.Token));
        }
        finally
        {
            CleanupDirs(bareDir, cloneDir);
        }
    }

    // ── Git helper for tests ──────────────────────────────────────────────────

    private static SemaphoreSlim GetGitLock(ConfigRepoManager manager)
    {
        var field = typeof(ConfigRepoManager).GetField(
            "_gitLock", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var semaphore = field!.GetValue(manager) as SemaphoreSlim;
        Assert.NotNull(semaphore);
        return semaphore!;
    }

    private static async Task RunGitCommandAsync(string workingDir, string[] args)
    {
        var (_, error) = await RunGitCommandRawAsync(workingDir, args);
        _ = error; // errors allowed during test setup
    }

    private static async Task<(string output, string error)> RunGitCommandRawAsync(
        string workingDir, string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Force LF line endings regardless of the host's global/system git config
        // (e.g. Windows installs commonly default core.autocrlf=true) so these tests
        // produce identical file contents on any OS.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.autocrlf=false");
        // Some machines set safe.bareRepository=explicit globally, which blocks
        // running git commands directly against a bare repo directory (as these
        // tests do to inspect the "remote" side). Override so tests work regardless
        // of the host's global git config.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("safe.bareRepository=all");
        // Disable commit signing: a host with commit.gpgsign=true globally configured can
        // make many concurrent `git commit` calls (under high xUnit parallelism) contend for
        // the GPG agent and intermittently fail with "gpg: signing failed: Not enough space"
        // — these test commits don't need to be signed.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("commit.gpgsign=false");
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdoutTask, stderrTask);
        await proc.WaitForExitAsync();
        return (stdoutTask.Result, stderrTask.Result);
    }
}

// Fake subclass that no-ops CommitFileAsync to avoid real git calls in WriteConfigAsync tests
internal sealed class FakeConfigRepoManager(string url, string path) : ConfigRepoManager(url, path)
{
    public List<(string File, string Message)> Commits { get; } = [];

    public override Task CommitFileAsync(string filePath, string commitMessage, CancellationToken ct = default)
    {
        Commits.Add((filePath, commitMessage));
        return Task.CompletedTask;
    }
}

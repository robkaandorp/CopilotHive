using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Orchestration;

using Microsoft.Extensions.AI;

namespace CopilotHive.Services;

/// <summary>
/// Describes a batch of model configuration changes to apply atomically.
/// </summary>
/// <param name="OrchestratorModel">New orchestrator model, or <c>null</c> to leave unchanged.</param>
/// <param name="ComposerModel">New Composer model, or <c>null</c> to leave unchanged.</param>
/// <param name="WorkerModels">Per-role model overrides, keyed by role name. <c>null</c> to leave unchanged.</param>
/// <param name="PremiumWorkerModels">Per-role premium model overrides, keyed by role name. <c>null</c> to leave unchanged.</param>
/// <param name="CompactionModel">New compaction model, or <c>null</c> to leave unchanged.</param>
/// <param name="OrchestratorReasoningEffort">
/// New orchestrator reasoning effort. <c>null</c> leaves the persisted value unchanged.
/// Empty string clears the persisted config. The running Brain retains its reasoning until
/// restart (UpdateModelAsync null = retain).
/// </param>
/// <param name="ComposerReasoningEffort">
/// New Composer reasoning effort. <c>null</c> leaves the persisted value unchanged; an empty
/// string clears it. Persistence-only. The running Composer picks up on restart.
/// </param>
/// <param name="WorkerReasoningEffort">
/// Per-role reasoning effort keyed by role name (case-insensitive). <c>null</c> leaves everything
/// unchanged. A present key with a <c>null</c> value clears that role's reasoning effort; a present
/// key with a non-empty value sets it. Unknown role keys are ignored entirely.
/// </param>
/// <param name="WorkerPremiumReasoningEffort">
/// Per-role premium reasoning effort keyed by role name (case-insensitive). Same semantics as
/// <paramref name="WorkerReasoningEffort"/>, mapped to <see cref="WorkerConfig.PremiumReasoningEffort"/>.
/// </param>
/// <param name="SubAgentModelReasoning">
/// Per-sub-agent-model reasoning effort keyed by model name (case-insensitive). Only model names
/// present in the current <c>sub_agent_models</c> list are applied; unknown keys are ignored entirely.
/// </param>
public sealed record ModelConfigUpdate(
    string? OrchestratorModel,
    string? ComposerModel,
    Dictionary<string, string>? WorkerModels,
    Dictionary<string, string>? PremiumWorkerModels,
    string? CompactionModel,
    string? OrchestratorReasoningEffort = null,
    string? ComposerReasoningEffort = null,
    Dictionary<string, string?>? WorkerReasoningEffort = null,
    Dictionary<string, string?>? WorkerPremiumReasoningEffort = null,
    Dictionary<string, string?>? SubAgentModelReasoning = null)
{
    private static string Show(string? value) => string.IsNullOrEmpty(value) ? "(cleared)" : value;

    /// <summary>
    /// Human-readable summary of the changes in this update (used as the git commit message body).
    /// </summary>
    public string Description => string.Join(", ", new[]
    {
        OrchestratorModel is not null ? $"orchestrator→{OrchestratorModel}" : null,
        ComposerModel      is not null ? $"composer→{ComposerModel}" : null,
        CompactionModel    is not null ? $"compaction→{CompactionModel}" : null,
        WorkerModels?.Count > 0
            ? "workers: " + string.Join(", ", WorkerModels.Select(kv => $"{kv.Key}→{kv.Value}"))
            : null,
        PremiumWorkerModels?.Count > 0
            ? "premium: " + string.Join(", ", PremiumWorkerModels.Select(kv => $"{kv.Key}→{kv.Value}"))
            : null,
        OrchestratorReasoningEffort is not null ? $"orchestrator reasoning→{Show(OrchestratorReasoningEffort)}" : null,
        ComposerReasoningEffort     is not null ? $"composer reasoning→{Show(ComposerReasoningEffort)}" : null,
        WorkerReasoningEffort?.Count > 0
            ? "worker reasoning: " + string.Join(", ", WorkerReasoningEffort.Select(kv => $"{kv.Key}→{Show(kv.Value)}"))
            : null,
        WorkerPremiumReasoningEffort?.Count > 0
            ? "premium reasoning: " + string.Join(", ", WorkerPremiumReasoningEffort.Select(kv => $"{kv.Key}→{Show(kv.Value)}"))
            : null,
        SubAgentModelReasoning?.Count > 0
            ? "sub-agent reasoning: " + string.Join(", ", SubAgentModelReasoning.Select(kv => $"{kv.Key}→{Show(kv.Value)}"))
            : null,
    }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>
/// Describes a batch of orchestrator-level setting changes to apply. Each field is
/// applied only when non-null, leaving the existing value unchanged otherwise.
/// </summary>
/// <param name="MaxIterations">New maximum number of goal iterations.</param>
/// <param name="MaxRetriesPerTask">New maximum retries per task.</param>
/// <param name="MaxParallelGoals">New maximum number of parallel goals.</param>
/// <param name="VerboseLogging">Whether verbose logging is enabled.</param>
/// <param name="BrainMaxSteps">New maximum Brain tool-call steps.</param>
/// <param name="BranchCleanupDelayHours">New branch cleanup delay in hours.</param>
public sealed record OrchestratorSettingsUpdate(
    int? MaxIterations, int? MaxRetriesPerTask, int? MaxParallelGoals,
    bool? VerboseLogging,
    int? BrainMaxSteps,
    int? BranchCleanupDelayHours);

/// <summary>
/// Request body for adding or updating a repository.
/// </summary>
/// <param name="Name">Short name used to identify the repository.</param>
/// <param name="Url">Remote clone URL of the repository.</param>
/// <param name="DefaultBranch">Default branch to use (e.g. "main").</param>
/// <param name="Release">Optional release automation configuration.</param>
public sealed record RepositoryRequest(string Name, string Url, string DefaultBranch, ReleaseRepoConfig? Release = null);

/// <summary>
/// Describes Composer setting changes to apply. Each field is applied only when non-null.
/// </summary>
/// <param name="MaxSteps">New maximum Composer tool-call steps.</param>
public sealed record ComposerSettingsUpdate(int? MaxSteps);

/// <summary>
/// Applies model configuration changes in-memory, writes the config file,
/// and commits the result to the config repository.
/// </summary>
public sealed class ConfigModelService
{
    private readonly HiveConfigFile _config;
    private readonly ConfigRepoManager _configRepo;
    private readonly ILogger<ConfigModelService> _logger;
    private readonly IDistributedBrain? _brain;
    private readonly IBrainRepoManager? _repoManager;

    /// <summary>
    /// Serialises the authoritative read-modify-write-commit transaction in
    /// <see cref="SaveModelConfigAsync"/>. The shared <see cref="HiveConfigFile"/> singleton is the
    /// runtime source of truth, so concurrent PATCH requests must not interleave validation,
    /// mutation, file writes, commits or the live Brain update.
    /// </summary>
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    /// <summary>
    /// Initialises a new <see cref="ConfigModelService"/>.
    /// </summary>
    /// <param name="config">The live <see cref="HiveConfigFile"/> singleton.</param>
    /// <param name="configRepo">The config repository manager.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="brain">Optional distributed brain to update when the orchestrator model changes.</param>
    /// <param name="repoManager">Optional brain repo manager used to clone newly added repositories.</param>
    public ConfigModelService(
        HiveConfigFile config,
        ConfigRepoManager configRepo,
        ILogger<ConfigModelService> logger,
        IDistributedBrain? brain = null,
        IBrainRepoManager? repoManager = null)
    {
        _config = config;
        _configRepo = configRepo;
        _logger = logger;
        _brain = brain;
        _repoManager = repoManager;
    }

    /// <summary>
    /// The fixed set of worker role names for which per-role reasoning effort can be configured.
    /// Keys outside this set are ignored entirely (neither validated nor applied).
    /// </summary>
    private static readonly string[] KnownWorkerRoleKeys =
        ["coder", "tester", "reviewer", "improver", "docwriter"];

    /// <summary>
    /// Looks up a dictionary entry using case-insensitive key matching. Callers must first
    /// reject case-insensitive duplicates (see <see cref="RejectCaseInsensitiveDuplicates"/>)
    /// so at most one entry can ever match and the result is order-independent.
    /// </summary>
    /// <param name="dict">Dictionary to search (may be <c>null</c>).</param>
    /// <param name="key">Key to look for, compared case-insensitively.</param>
    /// <param name="value">The matched value when found.</param>
    /// <returns><c>true</c> when a matching key exists.</returns>
    private static bool TryGetIgnoreCase(Dictionary<string, string?>? dict, string key, out string? value)
    {
        value = null;
        if (dict is null)
            return false;
        foreach (var kv in dict)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = kv.Value;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Canonicalises a reasoning-effort value for persistence. Null, empty and whitespace-only
    /// values all mean "clear" and map to <c>null</c>; every other (already validated) value is
    /// normalised to its canonical lowercase wire form (e.g. <c>"High"</c> → <c>"high"</c>).
    /// </summary>
    /// <param name="value">Raw reasoning-effort value from the request.</param>
    /// <returns>The canonical value, or <c>null</c> when the assignment clears the setting.</returns>
    private static string? CanonicalizeReasoning(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return ReasoningEffortConverter.Format(ReasoningEffortConverter.Parse(value));
    }

    /// <summary>
    /// Parses a reasoning-effort value leniently for non-validating paths: an unrecognised
    /// value degrades to <c>null</c> (unset) instead of throwing. Used after persistence has
    /// already succeeded, where an exception would surface as a misleading client error.
    /// </summary>
    /// <param name="value">Raw reasoning-effort value.</param>
    /// <returns>The parsed effort, or <c>null</c> when unset or invalid.</returns>
    private ReasoningEffort? TryParseReasoning(string? value)
    {
        try
        {
            return ReasoningEffortConverter.Parse(value);
        }
        catch (ArgumentException)
        {
            _logger.LogWarning(
                "Invalid reasoning effort '{Effort}' in configuration; treating it as unset.", value);
            return null;
        }
    }

    /// <summary>
    /// Throws when the dictionary contains two or more keys that differ only by case and map to
    /// the same known key. A plain <see cref="Dictionary{TKey,TValue}"/> uses ordinal comparison,
    /// so <c>{"Coder":…,"coder":…}</c> are distinct entries; accepting them would make the applied
    /// value depend on JSON property order and could leave one value unvalidated.
    /// </summary>
    /// <param name="dict">Dictionary from the request (may be <c>null</c>).</param>
    /// <param name="knownKeys">The set of keys this dictionary is allowed to target.</param>
    /// <param name="label">Field name used in the error message.</param>
    private static void RejectCaseInsensitiveDuplicates(
        Dictionary<string, string?>? dict, IEnumerable<string> knownKeys, string label)
    {
        if (dict is null || dict.Count < 2)
            return;

        foreach (var known in knownKeys)
        {
            var matches = dict.Keys
                .Where(k => string.Equals(k, known, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count > 1)
                throw new ArgumentException(
                    $"{label} contains duplicate case-insensitive key: " +
                    string.Join("/", matches.Select(k => $"'{k}'")) + ".");
        }
    }

    /// <summary>
    /// Validates every reasoning-effort value carried by the update that targets a known key.
    /// Case-insensitive duplicate keys are rejected first; remaining failures are collected and
    /// reported together in a single <see cref="ArgumentException"/>, so no mutation happens
    /// when any value is invalid.
    /// </summary>
    /// <param name="update">The pending model configuration update.</param>
    private void ValidateReasoningEfforts(ModelConfigUpdate update)
    {
        var knownSubAgentNames = _config.Models?.SubAgentModels?
            .Select(m => m.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList() ?? [];

        RejectCaseInsensitiveDuplicates(
            update.WorkerReasoningEffort, KnownWorkerRoleKeys, "workerReasoningEffort");
        RejectCaseInsensitiveDuplicates(
            update.WorkerPremiumReasoningEffort, KnownWorkerRoleKeys, "workerPremiumReasoningEffort");
        RejectCaseInsensitiveDuplicates(
            update.SubAgentModelReasoning, knownSubAgentNames, "subAgentModelReasoning");

        var invalid = new List<string>();

        void Check(string? value, string label)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            try
            {
                ReasoningEffortConverter.Parse(value);
            }
            catch (ArgumentException)
            {
                invalid.Add($"{label}='{value}'");
            }
        }

        Check(update.OrchestratorReasoningEffort, "orchestrator.reasoning_effort");
        Check(update.ComposerReasoningEffort, "composer.reasoning_effort");

        foreach (var role in KnownWorkerRoleKeys)
        {
            if (TryGetIgnoreCase(update.WorkerReasoningEffort, role, out var value))
                Check(value, $"workers.{role}.reasoning_effort");
            if (TryGetIgnoreCase(update.WorkerPremiumReasoningEffort, role, out var premium))
                Check(premium, $"workers.{role}.premium_reasoning_effort");
        }

        if (update.SubAgentModelReasoning is not null)
        {
            foreach (var name in knownSubAgentNames)
            {
                if (TryGetIgnoreCase(update.SubAgentModelReasoning, name, out var value))
                    Check(value, $"sub_agent_models.{name}.reasoning_effort");
            }
        }

        if (invalid.Count > 0)
            throw new ArgumentException(
                $"Invalid reasoning effort value(s): {string.Join(", ", invalid)}. " +
                "Allowed values are: none, low, medium, high, extra_high (or empty to clear).");
    }

    /// <summary>
    /// Returns the <see cref="WorkerConfig"/> for a role, creating it when absent.
    /// </summary>
    /// <param name="role">Role name (normalized to lowercase).</param>
    private WorkerConfig GetOrCreateWorker(string role)
    {
        var key = role.ToLowerInvariant();
        if (!_config.Workers.TryGetValue(key, out var wc))
        {
            wc = new WorkerConfig();
            _config.Workers[key] = wc;
        }
        return wc;
    }

    /// <summary>
    /// Applies the given model changes to the in-memory config, writes <c>hive-config.yaml</c>,
    /// and commits the file to the config repository. The whole validate → mutate → write →
    /// commit → live-update sequence is serialised so concurrent callers cannot interleave.
    /// </summary>
    /// <param name="update">The model changes to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SaveModelConfigAsync(ModelConfigUpdate update, CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct);
        try
        {
            // ── Step 1: validate everything before mutating anything ────────
            ValidateReasoningEfforts(update);

            // ── Step 2: mutate the in-memory config ─────────────────────────
            if (update.OrchestratorModel is not null)
                _config.Orchestrator.Model = update.OrchestratorModel;

            if (update.ComposerModel is not null)
            {
                _config.Composer ??= new ComposerConfig();
                _config.Composer.Model = update.ComposerModel;
            }

            if (update.WorkerModels is not null)
            {
                foreach (var (role, model) in update.WorkerModels)
                    GetOrCreateWorker(role).Model = model;
            }

            if (update.PremiumWorkerModels is not null)
            {
                foreach (var (role, model) in update.PremiumWorkerModels)
                    GetOrCreateWorker(role).PremiumModel = model;
            }

            if (update.CompactionModel is not null)
            {
                _config.Models ??= new ModelsConfig();
                _config.Models.CompactionModel = update.CompactionModel;
            }

            if (update.OrchestratorReasoningEffort is not null)
                _config.Orchestrator.ReasoningEffort = CanonicalizeReasoning(update.OrchestratorReasoningEffort);

            if (update.ComposerReasoningEffort is not null)
            {
                _config.Composer ??= new ComposerConfig();
                _config.Composer.ReasoningEffort = CanonicalizeReasoning(update.ComposerReasoningEffort);
            }

            if (update.WorkerReasoningEffort is not null)
            {
                foreach (var role in KnownWorkerRoleKeys)
                {
                    if (!TryGetIgnoreCase(update.WorkerReasoningEffort, role, out var value))
                        continue;
                    GetOrCreateWorker(role).ReasoningEffort = CanonicalizeReasoning(value);
                }
            }

            if (update.WorkerPremiumReasoningEffort is not null)
            {
                foreach (var role in KnownWorkerRoleKeys)
                {
                    if (!TryGetIgnoreCase(update.WorkerPremiumReasoningEffort, role, out var value))
                        continue;
                    GetOrCreateWorker(role).PremiumReasoningEffort = CanonicalizeReasoning(value);
                }
            }

            if (update.SubAgentModelReasoning is not null && _config.Models?.SubAgentModels is { } subAgentModels)
            {
                foreach (var entry in subAgentModels)
                {
                    if (!TryGetIgnoreCase(update.SubAgentModelReasoning, entry.Name, out var value))
                        continue;
                    entry.ReasoningEffort = CanonicalizeReasoning(value);
                }
            }

            // ── Step 3: persist (write + commit) ────────────────────────────
            var message = $"chore: update model configuration — {update.Description}";
            _logger.LogInformation("Saving model config changes: {Description}", update.Description);

            try
            {
                await _configRepo.WriteConfigAsync(_config, ct);
                await _configRepo.CommitFileAsync("hive-config.yaml", message, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist model configuration changes");
                throw;
            }

            // ── Step 4: live Brain update (only after persistence succeeded) ─
            var brainUpdateNeeded =
                update.OrchestratorReasoningEffort is not null || update.OrchestratorModel is not null;

            if (brainUpdateNeeded && _brain is not null)
            {
                var finalModel = update.OrchestratorModel ?? _config.Orchestrator.Model;

                var finalContextWindow = _config.TryGetContextWindowForModel(finalModel);
                if (finalContextWindow is null or <= 0)
                    finalContextWindow = Constants.DefaultBrainContextWindow;

                // Persistence already succeeded, so nothing here may throw back to the caller —
                // an ArgumentException would surface as a misleading 400 for a saved config.
                // The update value was validated in step 1; the fallback reads persisted config,
                // which a dynamic reload can leave unvalidated, so parse it leniently.
                var finalReasoning = TryParseReasoning(
                    update.OrchestratorReasoningEffort ?? _config.Orchestrator.ReasoningEffort);

                try
                {
                    await _brain.UpdateModelAsync(finalModel, finalContextWindow, finalReasoning, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Live Brain model update failed after successful config persistence");
                }
            }
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Adds a model to the available_models list. Throws <see cref="InvalidOperationException"/>
    /// if a model with the same name already exists.
    /// </summary>
    /// <param name="name">Model name.</param>
    /// <param name="contextWindow">Optional context window in tokens.</param>
    /// <param name="reasoningEffort">Ignored — available models no longer carry a reasoning effort.</param>
    /// <param name="description">Optional human-readable description.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task AddAvailableModelAsync(string name, int? contextWindow, string? reasoningEffort, string? description = null, CancellationToken ct = default)
        => AddAvailableModelAsync(name, contextWindow, reasoningEffort, description, supportsVision: null, ct);

    /// <summary>
    /// Adds a model to the available_models list, including the informational vision flag.
    /// Throws <see cref="InvalidOperationException"/> if a model with the same name already exists.
    /// </summary>
    /// <param name="name">Model name.</param>
    /// <param name="contextWindow">Optional context window in tokens.</param>
    /// <param name="reasoningEffort">Ignored — available models no longer carry a reasoning effort.</param>
    /// <param name="description">Optional human-readable description.</param>
    /// <param name="supportsVision">Vision flag: <c>true</c>, <c>false</c>, or <c>null</c> for unset.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task AddAvailableModelAsync(string name, int? contextWindow, string? reasoningEffort, string? description, bool? supportsVision, CancellationToken ct = default)
    {
        _config.Models ??= new ModelsConfig();
        _config.Models.AvailableModels ??= new List<ModelEntry>();

        if (_config.Models.AvailableModels.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Model '{name}' already exists in available_models");

        _config.Models.AvailableModels.Add(new ModelEntry
        {
            Name = name,
            ContextWindow = contextWindow,
            ReasoningEffort = null,
            Description = description,
            SupportsVision = supportsVision
        });

        var message = $"chore: add available model '{name}'";
        _logger.LogInformation("Adding available model: {Name}", name);

        await _configRepo.WriteConfigAsync(_config, ct);
        await _configRepo.CommitFileAsync("hive-config.yaml", message, ct);
    }

    /// <summary>
    /// Updates an existing model's context window, description and vision flag.
    /// Throws <see cref="InvalidOperationException"/> if the model is not found.
    /// </summary>
    /// <param name="name">Model name to update.</param>
    /// <param name="contextWindow">New context window (null clears it).</param>
    /// <param name="reasoningEffort">Ignored — the existing reasoning effort is preserved.</param>
    /// <param name="description">New description (null clears it).</param>
    /// <param name="ct">Cancellation token.</param>
    public Task UpdateAvailableModelAsync(string name, int? contextWindow, string? reasoningEffort, string? description = null, CancellationToken ct = default)
        => UpdateAvailableModelAsync(name, contextWindow, reasoningEffort, description, supportsVision: null, ct);

    /// <summary>
    /// Updates an existing model, including the informational vision flag.
    /// Throws <see cref="InvalidOperationException"/> if the model is not found.
    /// </summary>
    /// <param name="name">Model name to update.</param>
    /// <param name="contextWindow">New context window (null clears it).</param>
    /// <param name="reasoningEffort">Ignored — the existing reasoning effort is preserved.</param>
    /// <param name="description">New description (null clears it).</param>
    /// <param name="supportsVision">Vision flag: <c>true</c>, <c>false</c>, or <c>null</c> to clear/unset.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateAvailableModelAsync(string name, int? contextWindow, string? reasoningEffort, string? description, bool? supportsVision, CancellationToken ct = default)
    {
        var model = _config.Models?.AvailableModels?
            .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (model is null)
            throw new InvalidOperationException($"Model '{name}' not found in available_models");

        model.ContextWindow = contextWindow;
        model.Description = description;
        model.SupportsVision = supportsVision;

        var message = $"chore: update available model '{name}'";
        _logger.LogInformation("Updating available model: {Name}", name);

        await _configRepo.WriteConfigAsync(_config, ct);
        await _configRepo.CommitFileAsync("hive-config.yaml", message, ct);
    }

    /// <summary>
    /// Removes a model from the available_models list. Returns <c>false</c> if not found.
    /// </summary>
    /// <param name="name">Model name to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> RemoveAvailableModelAsync(string name, CancellationToken ct = default)
    {
        var model = _config.Models?.AvailableModels?
            .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (model is null)
            return false;

        _config.Models!.AvailableModels!.Remove(model);

        var message = $"chore: remove available model '{name}'";
        _logger.LogInformation("Removing available model: {Name}", name);

        await _configRepo.WriteConfigAsync(_config, ct);
        await _configRepo.CommitFileAsync("hive-config.yaml", message, ct);
        return true;
    }

    /// <summary>
    /// Adds a model to the sub_agent_models curated list. Throws <see cref="InvalidOperationException"/>
    /// if a model with the same name already exists.
    /// </summary>
    /// <param name="name">Model name.</param>
    /// <param name="contextWindow">Optional context window in tokens.</param>
    /// <param name="reasoningEffort">Optional default reasoning effort.</param>
    /// <param name="description">Optional human-readable description.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task AddSubAgentModelAsync(string name, int? contextWindow, string? reasoningEffort, string? description = null, CancellationToken ct = default)
        => AddSubAgentModelAsync(name, contextWindow, reasoningEffort, description, supportsVision: null, ct);

    /// <summary>
    /// Adds a model to the sub_agent_models curated list, including the informational vision flag.
    /// Throws <see cref="InvalidOperationException"/> if a model with the same name already exists.
    /// </summary>
    /// <param name="name">Model name.</param>
    /// <param name="contextWindow">Optional context window in tokens.</param>
    /// <param name="reasoningEffort">Optional default reasoning effort.</param>
    /// <param name="description">Optional human-readable description.</param>
    /// <param name="supportsVision">Vision flag: <c>true</c>, <c>false</c>, or <c>null</c> for unset (inherit).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task AddSubAgentModelAsync(string name, int? contextWindow, string? reasoningEffort, string? description, bool? supportsVision, CancellationToken ct = default)
    {
        _config.Models ??= new ModelsConfig();
        _config.Models.SubAgentModels ??= new List<ModelEntry>();

        // Reasoning effort comes exclusively from the explicit request field; the model
        // name is stored plain. An unrecognised value is a client error, so it is reported
        // as an ArgumentException the endpoint turns into a 400 (never an unhandled 500).
        string? effectiveReasoningEffort;
        try
        {
            effectiveReasoningEffort = ReasoningEffortConverter.Format(
                ReasoningEffortConverter.Parse(reasoningEffort));
        }
        catch (ArgumentException)
        {
            throw new ArgumentException(
                $"Invalid reasoning effort value: '{reasoningEffort}'. " +
                "Allowed values are: none, low, medium, high, extra_high (or empty to clear).",
                nameof(reasoningEffort));
        }

        if (_config.Models.SubAgentModels.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Model '{name}' already exists in sub_agent_models");

        _config.Models.SubAgentModels.Add(new ModelEntry
        {
            Name = name,
            ContextWindow = contextWindow,
            ReasoningEffort = effectiveReasoningEffort,
            Description = description,
            SupportsVision = supportsVision
        });

        var message = $"chore: add sub-agent model '{name}'";
        _logger.LogInformation("Adding sub-agent model: {Name}", name);

        await _configRepo.WriteConfigAsync(_config, ct);
        await _configRepo.CommitFileAsync("hive-config.yaml", message, ct);
    }

    /// <summary>
    /// Updates an existing sub-agent model. Throws <see cref="InvalidOperationException"/> if not found.
    /// </summary>
    /// <param name="name">Model name to update.</param>
    /// <param name="contextWindow">New context window (null clears it).</param>
    /// <param name="reasoningEffort">New reasoning effort (null clears it).</param>
    /// <param name="description">New description (null clears it).</param>
    /// <param name="ct">Cancellation token.</param>
    public Task UpdateSubAgentModelAsync(string name, int? contextWindow, string? reasoningEffort, string? description = null, CancellationToken ct = default)
        => UpdateSubAgentModelAsync(name, contextWindow, reasoningEffort, description, supportsVision: null, ct);

    /// <summary>
    /// Updates an existing sub-agent model, including the informational vision flag.
    /// Throws <see cref="InvalidOperationException"/> if not found.
    /// </summary>
    /// <param name="name">Model name to update.</param>
    /// <param name="contextWindow">New context window (null clears it).</param>
    /// <param name="reasoningEffort">New reasoning effort (null clears it).</param>
    /// <param name="description">New description (null clears it).</param>
    /// <param name="supportsVision">Vision flag: <c>true</c>, <c>false</c>, or <c>null</c> to clear/unset (inherit).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateSubAgentModelAsync(string name, int? contextWindow, string? reasoningEffort, string? description, bool? supportsVision, CancellationToken ct = default)
    {
        var model = _config.Models?.SubAgentModels?
            .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (model is null)
            throw new InvalidOperationException($"Model '{name}' not found in sub_agent_models");

        model.ContextWindow = contextWindow;
        model.ReasoningEffort = reasoningEffort;
        model.Description = description;
        model.SupportsVision = supportsVision;

        var message = $"chore: update sub-agent model '{name}'";
        _logger.LogInformation("Updating sub-agent model: {Name}", name);

        await _configRepo.WriteConfigAsync(_config, ct);
        await _configRepo.CommitFileAsync("hive-config.yaml", message, ct);
    }

    /// <summary>
    /// Removes a model from the sub_agent_models curated list. Returns <c>false</c> if not found.
    /// </summary>
    /// <param name="name">Model name to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> RemoveSubAgentModelAsync(string name, CancellationToken ct = default)
    {
        var model = _config.Models?.SubAgentModels?
            .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (model is null)
            return false;

        _config.Models!.SubAgentModels!.Remove(model);

        var message = $"chore: remove sub-agent model '{name}'";
        _logger.LogInformation("Removing sub-agent model: {Name}", name);

        await _configRepo.WriteConfigAsync(_config, ct);
        await _configRepo.CommitFileAsync("hive-config.yaml", message, ct);
        return true;
    }

    /// <summary>
    /// Normalizes a release configuration value. If both fields are null or whitespace,
    /// the result is <c>null</c> so empty release sections do not persist in YAML.
    /// </summary>
    private static ReleaseRepoConfig? NormalizeRelease(ReleaseRepoConfig? release)
    {
        if (release is null)
            return null;
        if (string.IsNullOrWhiteSpace(release.MergeTo) && string.IsNullOrWhiteSpace(release.TagBranch))
            return null;
        return release;
    }

    /// <summary>
    /// Validates a repository name to prevent path traversal. The name is used as a
    /// filesystem path segment when cloning, so it must not contain path separators or "..".
    /// </summary>
    /// <param name="name">Repository name to validate.</param>
    private static void ValidateRepositoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Repository name cannot be null or empty.", nameof(name));
        if (name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException($"Repository name '{name}' contains path separators which are not allowed.", nameof(name));
        if (name.Contains(".."))
            throw new ArgumentException($"Repository name '{name}' contains '..' which is not allowed.", nameof(name));
    }

    /// <summary>
    /// Adds a new repository to the config. Throws <see cref="InvalidOperationException"/>
    /// if a repository with the same name already exists. After persisting the config,
    /// triggers a clone of the new repository via the brain repo manager (when configured).
    /// </summary>
    /// <param name="name">Short repository name.</param>
    /// <param name="url">Remote clone URL.</param>
    /// <param name="defaultBranch">Default branch (falls back to "main" when empty).</param>
    /// <param name="release">Optional release automation configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task AddRepositoryAsync(string name, string url, string defaultBranch, ReleaseRepoConfig? release = null, CancellationToken ct = default)
    {
        ValidateRepositoryName(name);
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Repository URL cannot be null or empty.", nameof(url));

        if (_config.Repositories.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Repository '{name}' already exists");

        var branch = string.IsNullOrEmpty(defaultBranch) ? "main" : defaultBranch;
        _config.Repositories.Add(new RepositoryConfig
        {
            Name = name,
            Url = url,
            DefaultBranch = branch,
            Release = NormalizeRelease(release)
        });

        var message = $"chore: add repository '{name}'";
        _logger.LogInformation("Adding repository: {Name} ({Url})", name, url);

        await _configRepo.WriteConfigAsync(_config, ct);
        await _configRepo.CommitFileAsync("hive-config.yaml", message, ct);

        if (_repoManager is not null)
            await _repoManager.EnsureCloneAsync(name, url, branch, ct);
    }

    /// <summary>
    /// Updates an existing repository's URL, default branch, and optional release
    /// configuration. Throws <see cref="InvalidOperationException"/> if the repository
    /// is not found.
    /// </summary>
    /// <param name="name">Repository name to update.</param>
    /// <param name="url">New remote clone URL.</param>
    /// <param name="defaultBranch">New default branch.</param>
    /// <param name="release">New release configuration, or <c>null</c> to leave unchanged.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateRepositoryAsync(string name, string url, string defaultBranch, ReleaseRepoConfig? release = null, CancellationToken ct = default)
    {
        ValidateRepositoryName(name);
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Repository URL cannot be null or empty.", nameof(url));

        var repo = _config.Repositories
            .FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (repo is null)
            throw new InvalidOperationException($"Repository '{name}' not found");

        repo.Url = url;
        repo.DefaultBranch = string.IsNullOrEmpty(defaultBranch) ? "main" : defaultBranch;
        if (release is not null)
            repo.Release = NormalizeRelease(release);

        var message = $"chore: update repository '{name}'";
        _logger.LogInformation("Updating repository: {Name} ({Url})", name, url);

        await _configRepo.WriteConfigAsync(_config, ct);
        await _configRepo.CommitFileAsync("hive-config.yaml", message, ct);

        if (_repoManager is not null)
            await _repoManager.EnsureCloneAsync(repo.Name, repo.Url, repo.DefaultBranch, ct);
    }

    /// <summary>
    /// Removes a repository from the config. Returns <c>false</c> if not found.
    /// </summary>
    /// <param name="name">Repository name to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> RemoveRepositoryAsync(string name, CancellationToken ct = default)
    {
        var repo = _config.Repositories
            .FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (repo is null)
            return false;

        _config.Repositories.Remove(repo);

        var message = $"chore: remove repository '{name}'";
        _logger.LogInformation("Removing repository: {Name}", name);

        await _configRepo.WriteConfigAsync(_config, ct);
        await _configRepo.CommitFileAsync("hive-config.yaml", message, ct);
        return true;
    }

    /// <summary>
    /// Applies orchestrator-level setting changes. Only non-null fields are applied.
    /// </summary>
    /// <param name="update">The orchestrator settings to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateOrchestratorSettingsAsync(OrchestratorSettingsUpdate update, CancellationToken ct = default)
    {
        if (update.MaxIterations is not null)
            _config.Orchestrator.MaxIterations = update.MaxIterations.Value;
        if (update.MaxRetriesPerTask is not null)
            _config.Orchestrator.MaxRetriesPerTask = update.MaxRetriesPerTask.Value;
        if (update.MaxParallelGoals is not null)
            _config.Orchestrator.MaxParallelGoals = update.MaxParallelGoals.Value;
        if (update.VerboseLogging is not null)
            _config.Orchestrator.VerboseLogging = update.VerboseLogging.Value;
        if (update.BrainMaxSteps is not null)
            _config.Orchestrator.BrainMaxSteps = update.BrainMaxSteps.Value;
        if (update.BranchCleanupDelayHours is not null)
            _config.Orchestrator.BranchCleanupDelayHours = update.BranchCleanupDelayHours.Value;

        var message = "chore: update orchestrator settings";
        _logger.LogInformation("Updating orchestrator settings");

        await _configRepo.WriteConfigAsync(_config, ct);
        await _configRepo.CommitFileAsync("hive-config.yaml", message, ct);
    }

    /// <summary>
    /// Sets per-role worker context windows. Keys are normalized to lowercase, creating
    /// <see cref="WorkerConfig"/> entries as needed.
    /// </summary>
    /// <param name="contextWindows">Role → context window size mapping.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateWorkerContextWindowsAsync(Dictionary<string, int> contextWindows, CancellationToken ct = default)
    {
        foreach (var (role, contextWindow) in contextWindows)
        {
            var key = role.ToLowerInvariant();
            if (!_config.Workers.TryGetValue(key, out var wc))
            {
                wc = new WorkerConfig();
                _config.Workers[key] = wc;
            }
            wc.ContextWindow = contextWindow;
        }

        var message = "chore: update worker context windows";
        _logger.LogInformation("Updating worker context windows");

        await _configRepo.WriteConfigAsync(_config, ct);
        await _configRepo.CommitFileAsync("hive-config.yaml", message, ct);
    }

    /// <summary>
    /// Applies Composer setting changes. Only non-null fields are applied.
    /// Creates a <see cref="ComposerConfig"/> if none exists.
    /// </summary>
    /// <param name="maxSteps">New maximum tool-call steps, or <c>null</c> to leave unchanged.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateComposerSettingsAsync(int? maxSteps, CancellationToken ct = default)
    {
        _config.Composer ??= new ComposerConfig();

        if (maxSteps is not null)
            _config.Composer.MaxSteps = maxSteps.Value;

        var message = "chore: update composer settings";
        _logger.LogInformation("Updating composer settings");

        await _configRepo.WriteConfigAsync(_config, ct);
        await _configRepo.CommitFileAsync("hive-config.yaml", message, ct);
    }
}

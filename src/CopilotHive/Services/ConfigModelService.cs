using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Orchestration;

using Microsoft.Extensions.AI;

namespace CopilotHive.Services;

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
    private static bool TryGetIgnoreCase(
        Dictionary<string, ReasoningEffort?>? dict, string key, out ReasoningEffort? value)
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
    /// Parses a reasoning-effort value leniently for non-validating paths: an unrecognised
    /// value degrades to <c>null</c> (unset) instead of throwing. Used when projecting the
    /// YAML-bound <c>string?</c> configuration into the enum-typed API surface, where a
    /// dynamic reload can leave an unvalidated value behind.
    /// </summary>
    /// <param name="value">Raw reasoning-effort value from YAML.</param>
    /// <returns>The parsed effort, or <c>null</c> when unset, empty, whitespace or invalid.</returns>
    internal static ReasoningEffort? ParseLenient(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            return ReasoningEffortConverter.Parse(value);
        }
        catch (ArgumentException)
        {
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
        Dictionary<string, ReasoningEffort?>? dict, IEnumerable<string> knownKeys, string label)
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
    /// Validates the reasoning-effort assignments carried by the update. The values are already
    /// strongly typed <see cref="ReasoningEffort"/> enums (the JSON layer rejects unknown wire
    /// values with a 400), so the only remaining structural failure is a case-insensitive
    /// duplicate key, which would make the applied value depend on JSON property order.
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

            // Reasoning effort travels the API as a strongly typed enum but the YAML-bound config
            // classes remain string?, so each assignment is formatted to its canonical wire form.
            // A null (absent) value is always a no-op — including a present dictionary key whose
            // value is null — so an assignment can only ever set a level, never silently clear one.
            if (update.OrchestratorReasoningEffort is not null)
                _config.Orchestrator.ReasoningEffort =
                    ReasoningEffortConverter.Format(update.OrchestratorReasoningEffort);

            if (update.ComposerReasoningEffort is not null)
            {
                _config.Composer ??= new ComposerConfig();
                _config.Composer.ReasoningEffort =
                    ReasoningEffortConverter.Format(update.ComposerReasoningEffort);
            }

            if (update.WorkerReasoningEffort is not null)
            {
                foreach (var role in KnownWorkerRoleKeys)
                {
                    if (!TryGetIgnoreCase(update.WorkerReasoningEffort, role, out var value) || value is null)
                        continue;
                    GetOrCreateWorker(role).ReasoningEffort = ReasoningEffortConverter.Format(value);
                }
            }

            if (update.WorkerPremiumReasoningEffort is not null)
            {
                foreach (var role in KnownWorkerRoleKeys)
                {
                    if (!TryGetIgnoreCase(update.WorkerPremiumReasoningEffort, role, out var value) || value is null)
                        continue;
                    GetOrCreateWorker(role).PremiumReasoningEffort = ReasoningEffortConverter.Format(value);
                }
            }

            if (update.SubAgentModelReasoning is not null && _config.Models?.SubAgentModels is { } subAgentModels)
            {
                foreach (var entry in subAgentModels)
                {
                    if (!TryGetIgnoreCase(update.SubAgentModelReasoning, entry.Name, out var value) || value is null)
                        continue;
                    entry.ReasoningEffort = ReasoningEffortConverter.Format(value);
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

                // Persistence already succeeded, so nothing here may throw back to the caller.
                // The update value is a validated enum; the fallback reads persisted config,
                // which a dynamic reload can leave unvalidated, so parse it leniently.
                var finalReasoning = update.OrchestratorReasoningEffort
                    ?? ParseLenient(_config.Orchestrator.ReasoningEffort);

                try
                {
                    // The Brain is only registered when a model is configured (Slice 2 gate), so
                    // finalModel is non-blank whenever _brain is non-null; the guard is a
                    // compile-safe, behavior-preserving assertion of that invariant.
                    if (!string.IsNullOrWhiteSpace(finalModel))
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
    /// <param name="description">Optional human-readable description.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task AddAvailableModelAsync(string name, int? contextWindow, string? description = null, CancellationToken ct = default)
        => AddAvailableModelAsync(name, contextWindow, description, supportsVision: null, ct);

    /// <summary>
    /// Adds a model to the available_models list, including the informational vision flag.
    /// Throws <see cref="InvalidOperationException"/> if a model with the same name already exists.
    /// </summary>
    /// <param name="name">Model name.</param>
    /// <param name="contextWindow">Optional context window in tokens.</param>
    /// <param name="description">Optional human-readable description.</param>
    /// <param name="supportsVision">Vision flag: <c>true</c>, <c>false</c>, or <c>null</c> for unset.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task AddAvailableModelAsync(string name, int? contextWindow, string? description, bool? supportsVision, CancellationToken ct = default)
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
    /// <param name="description">New description (null clears it).</param>
    /// <param name="ct">Cancellation token.</param>
    public Task UpdateAvailableModelAsync(string name, int? contextWindow, string? description = null, CancellationToken ct = default)
        => UpdateAvailableModelAsync(name, contextWindow, description, supportsVision: null, ct);

    /// <summary>
    /// Updates an existing model, including the informational vision flag.
    /// Throws <see cref="InvalidOperationException"/> if the model is not found.
    /// </summary>
    /// <param name="name">Model name to update.</param>
    /// <param name="contextWindow">New context window (null clears it).</param>
    /// <param name="description">New description (null clears it).</param>
    /// <param name="supportsVision">Vision flag: <c>true</c>, <c>false</c>, or <c>null</c> to clear/unset.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateAvailableModelAsync(string name, int? contextWindow, string? description, bool? supportsVision, CancellationToken ct = default)
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
    /// <param name="reasoningEffort">Optional default reasoning effort (<c>null</c> for unset).</param>
    /// <param name="description">Optional human-readable description.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task AddSubAgentModelAsync(string name, int? contextWindow, ReasoningEffort? reasoningEffort, string? description = null, CancellationToken ct = default)
        => AddSubAgentModelAsync(name, contextWindow, reasoningEffort, description, supportsVision: null, ct);

    /// <summary>
    /// Adds a model to the sub_agent_models curated list, including the informational vision flag.
    /// Throws <see cref="InvalidOperationException"/> if a model with the same name already exists.
    /// </summary>
    /// <param name="name">Model name.</param>
    /// <param name="contextWindow">Optional context window in tokens.</param>
    /// <param name="reasoningEffort">Optional default reasoning effort (<c>null</c> for unset).</param>
    /// <param name="description">Optional human-readable description.</param>
    /// <param name="supportsVision">Vision flag: <c>true</c>, <c>false</c>, or <c>null</c> for unset (inherit).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task AddSubAgentModelAsync(string name, int? contextWindow, ReasoningEffort? reasoningEffort, string? description, bool? supportsVision, CancellationToken ct = default)
    {
        _config.Models ??= new ModelsConfig();
        _config.Models.SubAgentModels ??= new List<ModelEntry>();

        // Reasoning effort comes exclusively from the explicit request field (already a validated
        // enum — the JSON layer rejects unknown wire values); the model name is stored plain.
        // ModelEntry is YAML-bound and stays string?, so the enum is formatted to its wire form.
        var effectiveReasoningEffort = ReasoningEffortConverter.Format(reasoningEffort);

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
    public Task UpdateSubAgentModelAsync(string name, int? contextWindow, ReasoningEffort? reasoningEffort, string? description = null, CancellationToken ct = default)
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
    public async Task UpdateSubAgentModelAsync(string name, int? contextWindow, ReasoningEffort? reasoningEffort, string? description, bool? supportsVision, CancellationToken ct = default)
    {
        var model = _config.Models?.SubAgentModels?
            .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (model is null)
            throw new InvalidOperationException($"Model '{name}' not found in sub_agent_models");

        model.ContextWindow = contextWindow;
        // ModelEntry stays YAML-bound (string?): format the enum to its canonical wire form.
        model.ReasoningEffort = ReasoningEffortConverter.Format(reasoningEffort);
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
    /// <param name="monitorCi">Whether CI monitoring is enabled, or <c>null</c> to use the default (<c>false</c>).</param>
    /// <param name="ciTimeoutMinutes">CI timeout in minutes, or <c>null</c> to use the default (<c>30</c>). Must be 1-120 when provided.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task AddRepositoryAsync(string name, string url, string defaultBranch, ReleaseRepoConfig? release = null, bool? monitorCi = null, int? ciTimeoutMinutes = null, CancellationToken ct = default)
    {
        ValidateRepositoryName(name);
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Repository URL cannot be null or empty.", nameof(url));

        if (ciTimeoutMinutes is < 1 or > 120)
            throw new ArgumentException("CI timeout must be between 1 and 120 minutes.");

        if (_config.Repositories.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Repository '{name}' already exists");

        var branch = string.IsNullOrEmpty(defaultBranch) ? "main" : defaultBranch;
        var newRepo = new RepositoryConfig
        {
            Name = name,
            Url = url,
            DefaultBranch = branch,
            Release = NormalizeRelease(release)
        };
        if (monitorCi.HasValue) newRepo.MonitorCi = monitorCi.Value;
        if (ciTimeoutMinutes.HasValue) newRepo.CiTimeoutMinutes = ciTimeoutMinutes.Value;
        _config.Repositories.Add(newRepo);

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
    /// <param name="monitorCi">Whether CI monitoring is enabled, or <c>null</c> to leave unchanged.</param>
    /// <param name="ciTimeoutMinutes">CI timeout in minutes, or <c>null</c> to leave unchanged. Must be 1-120 when provided.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateRepositoryAsync(string name, string url, string defaultBranch, ReleaseRepoConfig? release = null, bool? monitorCi = null, int? ciTimeoutMinutes = null, CancellationToken ct = default)
    {
        ValidateRepositoryName(name);
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Repository URL cannot be null or empty.", nameof(url));

        if (ciTimeoutMinutes is < 1 or > 120)
            throw new ArgumentException("CI timeout must be between 1 and 120 minutes.");

        var repo = _config.Repositories
            .FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (repo is null)
            throw new InvalidOperationException($"Repository '{name}' not found");

        repo.Url = url;
        repo.DefaultBranch = string.IsNullOrEmpty(defaultBranch) ? "main" : defaultBranch;
        if (release is not null)
            repo.Release = NormalizeRelease(release);
        if (monitorCi.HasValue) repo.MonitorCi = monitorCi.Value;
        if (ciTimeoutMinutes.HasValue) repo.CiTimeoutMinutes = ciTimeoutMinutes.Value;

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
    /// The canonical wire forms accepted for an active event name. Each whitelisted event has
    /// exactly two canonical spellings — snake_case (the persisted form) and PascalCase (the
    /// <see cref="EventType"/> member name) — and an input must equal one of them
    /// case-insensitively. No further normalization is applied: underscores are never stripped
    /// or collapsed, so malformed spellings such as <c>goal__completed</c>,
    /// <c>_goal_completed</c> and <c>goal_completed_</c> are rejected rather than canonicalized.
    /// </summary>
    private static readonly (string Canonical, string Pascal)[] KnownActiveEvents =
    [
        ("goal_completed",     "GoalCompleted"),
        ("goal_failed",        "GoalFailed"),
        ("ci_failed",          "CiFailed"),
        ("issue_raised",       "IssueRaised"),
        ("package_published",  "PackagePublished"),
        ("ci_succeeded",       "CiSucceeded"),
        ("release_completed",  "ReleaseCompleted"),
        ("goal_dispatched",    "GoalDispatched"),
        ("issue_resolved",     "IssueResolved"),
    ];

    /// <summary>
    /// The canonical snake_case active event names, used in validation error messages.
    /// </summary>
    private static readonly string[] KnownActiveEventNames =
        [.. KnownActiveEvents.Select(e => e.Canonical)];

    /// <summary>
    /// Resolves an active event name to its canonical snake_case form. The input must equal one
    /// of the eighteen canonical spellings (nine snake_case, nine PascalCase) case-insensitively;
    /// anything else — including near-matches that differ only in underscore placement — returns
    /// <c>null</c> so the caller can reject it.
    /// </summary>
    /// <param name="name">Raw active event name from the request.</param>
    /// <returns>The canonical snake_case name, or <c>null</c> when the input is not whitelisted.</returns>
    private static string? TryResolveActiveEventName(string name)
    {
        foreach (var (canonical, pascal) in KnownActiveEvents)
        {
            if (string.Equals(canonical, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pascal, name, StringComparison.OrdinalIgnoreCase))
            {
                return canonical;
            }
        }
        return null;
    }

    /// <summary>
    /// Validates the event-notification fields carried by a <see cref="ComposerSettingsUpdate"/>.
    /// All supplied fields are checked BEFORE any config mutation so a failed update never
    /// leaves a partially-applied state behind.
    /// </summary>
    /// <param name="update">The pending composer settings update.</param>
    /// <returns>
    /// The normalized active event names (canonical snake_case, deduplicated), or <c>null</c>
    /// when the update does not carry an active-events field.
    /// </returns>
    private static List<string>? ValidateEventNotifications(ComposerSettingsUpdate update)
    {
        if (update.EventNotificationsMode is not null)
        {
            var mode = update.EventNotificationsMode.Trim().ToLowerInvariant();
            if (mode is not ("passive" or "active" or "off"))
                throw new ArgumentException(
                    $"Invalid event notification mode '{update.EventNotificationsMode}'. Valid values: passive, active, off.");
        }

        if (update.EventNotificationsThrottleSeconds is not null)
        {
            // Clamped to [1, 300]; no validation failure for out-of-range values.
            _ = Math.Clamp(update.EventNotificationsThrottleSeconds.Value, 1, 300);
        }

        if (update.EventNotificationsActiveEvents is null)
            return null;

        if (update.EventNotificationsActiveEvents.Count == 0)
            throw new ArgumentException("At least one active event is required.");

        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in update.EventNotificationsActiveEvents)
        {
            if (entry is null)
                throw new ArgumentException("Active event names cannot be null.");

            // Strict: a case-insensitive exact match against a canonical spelling, nothing more.
            var match = TryResolveActiveEventName(entry);
            if (match is null)
                throw new ArgumentException(
                    $"Invalid active event '{entry}'. Valid values: {string.Join(", ", KnownActiveEventNames)}.");

            if (seen.Add(match))
                normalized.Add(match);
        }

        return normalized;
    }

    /// <summary>
    /// Applies Composer setting changes. Only non-null fields are applied.
    /// Creates a <see cref="ComposerConfig"/> if none exists.
    /// </summary>
    /// <param name="update">The composer settings to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateComposerSettingsAsync(ComposerSettingsUpdate update, CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct);
        try
        {
            // ── Step 1: validate everything before mutating anything ────────
            var normalizedActiveEvents = ValidateEventNotifications(update);

            // ── Step 2: mutate the in-memory config ─────────────────────────
            _config.Composer ??= new ComposerConfig();

            if (update.MaxSteps is not null)
                _config.Composer.MaxSteps = update.MaxSteps.Value;

            if (update.EventNotificationsMode is not null
                || update.EventNotificationsActiveEvents is not null
                || update.EventNotificationsThrottleSeconds is not null)
            {
                _config.Composer.EventNotifications ??= new EventNotificationsConfig();

                if (update.EventNotificationsMode is not null)
                    _config.Composer.EventNotifications.Mode = update.EventNotificationsMode.Trim().ToLowerInvariant();

                if (normalizedActiveEvents is not null)
                    _config.Composer.EventNotifications.ActiveEvents = normalizedActiveEvents;

                if (update.EventNotificationsThrottleSeconds is not null)
                    _config.Composer.EventNotifications.ThrottleSeconds =
                        Math.Clamp(update.EventNotificationsThrottleSeconds.Value, 1, 300);
            }

            // ── Step 3: persist (write + commit) ────────────────────────────
            var message = "chore: update composer settings";
            _logger.LogInformation("Updating composer settings");

            await _configRepo.WriteConfigAsync(_config, ct);
            await _configRepo.CommitFileAsync("hive-config.yaml", message, ct);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}

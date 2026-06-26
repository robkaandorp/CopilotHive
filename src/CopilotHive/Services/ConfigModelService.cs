using CopilotHive.Configuration;
using CopilotHive.Orchestration;

namespace CopilotHive.Services;

/// <summary>
/// Describes a batch of model configuration changes to apply atomically.
/// </summary>
/// <param name="OrchestratorModel">New orchestrator model, or <c>null</c> to leave unchanged.</param>
/// <param name="ComposerModel">New Composer model, or <c>null</c> to leave unchanged.</param>
/// <param name="WorkerModels">Per-role model overrides, keyed by role name. <c>null</c> to leave unchanged.</param>
/// <param name="PremiumWorkerModels">Per-role premium model overrides, keyed by role name. <c>null</c> to leave unchanged.</param>
/// <param name="CompactionModel">New compaction model, or <c>null</c> to leave unchanged.</param>
public sealed record ModelConfigUpdate(
    string? OrchestratorModel,
    string? ComposerModel,
    Dictionary<string, string>? WorkerModels,
    Dictionary<string, string>? PremiumWorkerModels,
    string? CompactionModel)
{
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
    }.Where(s => !string.IsNullOrEmpty(s)));
}

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

    /// <summary>
    /// Initialises a new <see cref="ConfigModelService"/>.
    /// </summary>
    /// <param name="config">The live <see cref="HiveConfigFile"/> singleton.</param>
    /// <param name="configRepo">The config repository manager.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="brain">Optional distributed brain to update when the orchestrator model changes.</param>
    public ConfigModelService(
        HiveConfigFile config,
        ConfigRepoManager configRepo,
        ILogger<ConfigModelService> logger,
        IDistributedBrain? brain = null)
    {
        _config = config;
        _configRepo = configRepo;
        _logger = logger;
        _brain = brain;
    }

    /// <summary>
    /// Applies the given model changes to the in-memory config, writes <c>hive-config.yaml</c>,
    /// and commits the file to the config repository.
    /// </summary>
    /// <param name="update">The model changes to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SaveModelConfigAsync(ModelConfigUpdate update, CancellationToken ct = default)
    {
        if (update.OrchestratorModel is not null)
        {
            _config.Orchestrator.Model = update.OrchestratorModel;

            var model = update.OrchestratorModel;
            var contextWindow = _config.TryGetContextWindowForModel(model);
            if (contextWindow is null or <= 0)
                contextWindow = _config.Orchestrator.BrainContextWindow > 0
                    ? _config.Orchestrator.BrainContextWindow
                    : Constants.DefaultBrainContextWindow;

            if (_brain is not null)
            {
                var reasoningEffort = _config.TryGetReasoningEffortForModel(model);
                var modelWithReasoning = HiveConfigFile.ApplyReasoningSuffix(model, reasoningEffort);
                await _brain.UpdateModelAsync(modelWithReasoning, contextWindow, ct);
            }
        }

        if (update.ComposerModel is not null)
        {
            _config.Composer ??= new ComposerConfig();
            _config.Composer.Model = update.ComposerModel;
        }

        if (update.WorkerModels is not null)
        {
            foreach (var (role, model) in update.WorkerModels)
            {
                var key = role.ToLowerInvariant();
                if (!_config.Workers.TryGetValue(key, out var wc))
                {
                    wc = new WorkerConfig();
                    _config.Workers[key] = wc;
                }
                wc.Model = model;
            }
        }

        if (update.PremiumWorkerModels is not null)
        {
            foreach (var (role, model) in update.PremiumWorkerModels)
            {
                var key = role.ToLowerInvariant();
                if (!_config.Workers.TryGetValue(key, out var wc))
                {
                    wc = new WorkerConfig();
                    _config.Workers[key] = wc;
                }
                wc.PremiumModel = model;
            }
        }

        if (update.CompactionModel is not null)
        {
            _config.Models ??= new ModelsConfig();
            _config.Models.CompactionModel = update.CompactionModel;
        }

        var message = $"chore: update model configuration — {update.Description}";
        _logger.LogInformation("Saving model config changes: {Description}", update.Description);

        await _configRepo.WriteConfigAsync(_config, ct);
        await _configRepo.CommitFileAsync("hive-config.yaml", message, ct);
    }

    /// <summary>
    /// Adds a model to the available_models list. Throws <see cref="InvalidOperationException"/>
    /// if a model with the same name already exists.
    /// </summary>
    /// <param name="name">Model name.</param>
    /// <param name="contextWindow">Optional context window in tokens.</param>
    /// <param name="reasoningEffort">Optional default reasoning effort.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task AddAvailableModelAsync(string name, int? contextWindow, string? reasoningEffort, CancellationToken ct = default)
    {
        _config.Models ??= new ModelsConfig();
        _config.Models.AvailableModels ??= new List<ModelEntry>();

        if (_config.Models.AvailableModels.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Model '{name}' already exists in available_models");

        _config.Models.AvailableModels.Add(new ModelEntry
        {
            Name = name,
            ContextWindow = contextWindow,
            ReasoningEffort = reasoningEffort
        });

        var message = $"chore: add available model '{name}'";
        _logger.LogInformation("Adding available model: {Name}", name);

        await _configRepo.WriteConfigAsync(_config, ct);
        await _configRepo.CommitFileAsync("hive-config.yaml", message, ct);
    }

    /// <summary>
    /// Updates an existing model's context window and/or reasoning effort.
    /// Throws <see cref="InvalidOperationException"/> if the model is not found.
    /// </summary>
    /// <param name="name">Model name to update.</param>
    /// <param name="contextWindow">New context window (null clears it).</param>
    /// <param name="reasoningEffort">New reasoning effort (null clears it).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateAvailableModelAsync(string name, int? contextWindow, string? reasoningEffort, CancellationToken ct = default)
    {
        var model = _config.Models?.AvailableModels?
            .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (model is null)
            throw new InvalidOperationException($"Model '{name}' not found in available_models");

        model.ContextWindow = contextWindow;
        model.ReasoningEffort = reasoningEffort;

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
}

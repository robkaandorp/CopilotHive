using CopilotHive.Configuration;

namespace CopilotHive.Services;

/// <summary>
/// Describes a batch of model configuration changes to apply atomically.
/// </summary>
/// <param name="OrchestratorModel">New orchestrator model, or <c>null</c> to leave unchanged.</param>
/// <param name="ComposerModel">New Composer model, or <c>null</c> to leave unchanged.</param>
/// <param name="WorkerModels">Per-role model overrides, keyed by role name. <c>null</c> to leave unchanged.</param>
/// <param name="CompactionModel">New compaction model, or <c>null</c> to leave unchanged.</param>
public sealed record ModelConfigUpdate(
    string? OrchestratorModel,
    string? ComposerModel,
    Dictionary<string, string>? WorkerModels,
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

    /// <summary>
    /// Initialises a new <see cref="ConfigModelService"/>.
    /// </summary>
    /// <param name="config">The live <see cref="HiveConfigFile"/> singleton.</param>
    /// <param name="configRepo">The config repository manager.</param>
    /// <param name="logger">Logger instance.</param>
    public ConfigModelService(
        HiveConfigFile config,
        ConfigRepoManager configRepo,
        ILogger<ConfigModelService> logger)
    {
        _config = config;
        _configRepo = configRepo;
        _logger = logger;
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
            _config.Orchestrator.Model = update.OrchestratorModel;

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
}

using CopilotHive.Configuration;
using CopilotHive.Git;
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
/// Describes a batch of orchestrator-level setting changes to apply. Each field is
/// applied only when non-null, leaving the existing value unchanged otherwise.
/// </summary>
/// <param name="MaxIterations">New maximum number of goal iterations.</param>
/// <param name="MaxRetriesPerTask">New maximum retries per task.</param>
/// <param name="MaxParallelGoals">New maximum number of parallel goals.</param>
/// <param name="AlwaysImprove">Whether the improver runs after every iteration.</param>
/// <param name="VerboseLogging">Whether verbose logging is enabled.</param>
/// <param name="BrainContextWindow">New Brain context window in tokens.</param>
/// <param name="BrainMaxSteps">New maximum Brain tool-call steps.</param>
/// <param name="WorkerContextWindow">New default worker context window in tokens.</param>
/// <param name="BranchCleanupDelayHours">New branch cleanup delay in hours.</param>
public sealed record OrchestratorSettingsUpdate(
    int? MaxIterations, int? MaxRetriesPerTask, int? MaxParallelGoals,
    bool? AlwaysImprove, bool? VerboseLogging,
    int? BrainContextWindow, int? BrainMaxSteps,
    int? WorkerContextWindow, int? BranchCleanupDelayHours);

/// <summary>
/// Request body for adding or updating a repository.
/// </summary>
/// <param name="Name">Short name used to identify the repository.</param>
/// <param name="Url">Remote clone URL of the repository.</param>
/// <param name="DefaultBranch">Default branch to use (e.g. "main").</param>
public sealed record RepositoryRequest(string Name, string Url, string DefaultBranch);

/// <summary>
/// Describes Composer setting changes to apply. Each field is applied only when non-null.
/// </summary>
/// <param name="ContextWindow">New Composer context window in tokens.</param>
/// <param name="MaxSteps">New maximum Composer tool-call steps.</param>
public sealed record ComposerSettingsUpdate(int? ContextWindow, int? MaxSteps);

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
    /// <param name="ct">Cancellation token.</param>
    public async Task AddRepositoryAsync(string name, string url, string defaultBranch, CancellationToken ct = default)
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
            DefaultBranch = branch
        });

        var message = $"chore: add repository '{name}'";
        _logger.LogInformation("Adding repository: {Name} ({Url})", name, url);

        await _configRepo.WriteConfigAsync(_config, ct);
        await _configRepo.CommitFileAsync("hive-config.yaml", message, ct);

        if (_repoManager is not null)
            await _repoManager.EnsureCloneAsync(name, url, branch, ct);
    }

    /// <summary>
    /// Updates an existing repository's URL and default branch. Throws
    /// <see cref="InvalidOperationException"/> if the repository is not found.
    /// </summary>
    /// <param name="name">Repository name to update.</param>
    /// <param name="url">New remote clone URL.</param>
    /// <param name="defaultBranch">New default branch.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateRepositoryAsync(string name, string url, string defaultBranch, CancellationToken ct = default)
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
        if (update.AlwaysImprove is not null)
            _config.Orchestrator.AlwaysImprove = update.AlwaysImprove.Value;
        if (update.VerboseLogging is not null)
            _config.Orchestrator.VerboseLogging = update.VerboseLogging.Value;
        if (update.BrainContextWindow is not null)
            _config.Orchestrator.BrainContextWindow = update.BrainContextWindow.Value;
        if (update.BrainMaxSteps is not null)
            _config.Orchestrator.BrainMaxSteps = update.BrainMaxSteps.Value;
        if (update.WorkerContextWindow is not null)
            _config.Orchestrator.WorkerContextWindow = update.WorkerContextWindow.Value;
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
    /// <param name="contextWindow">New context window in tokens, or <c>null</c> to leave unchanged.</param>
    /// <param name="maxSteps">New maximum tool-call steps, or <c>null</c> to leave unchanged.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateComposerSettingsAsync(int? contextWindow, int? maxSteps, CancellationToken ct = default)
    {
        _config.Composer ??= new ComposerConfig();

        if (contextWindow is not null)
            _config.Composer.ContextWindow = contextWindow.Value;
        if (maxSteps is not null)
            _config.Composer.MaxSteps = maxSteps.Value;

        var message = "chore: update composer settings";
        _logger.LogInformation("Updating composer settings");

        await _configRepo.WriteConfigAsync(_config, ct);
        await _configRepo.CommitFileAsync("hive-config.yaml", message, ct);
    }
}

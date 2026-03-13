using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CopilotHive.Goals;

/// <summary>
/// Goal source that reads and persists goals from a YAML file on disk.
/// </summary>
public sealed class FileGoalSource : IGoalSource
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Initialises a new <see cref="FileGoalSource"/> backed by the specified YAML file.
    /// </summary>
    /// <param name="filePath">Path to the YAML goals file.</param>
    public FileGoalSource(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <summary>Unique name identifying this goal source.</summary>
    public string Name => "file";

    /// <summary>
    /// Reads the YAML file and returns all goals with <see cref="GoalStatus.Pending"/> status.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Read-only list of pending goals.</returns>
    public async Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default)
    {
        var goals = await ReadGoalsAsync(ct);
        return goals.Where(g => g.Status == GoalStatus.Pending).ToList().AsReadOnly();
    }

    /// <summary>
    /// Updates the status of a goal in the backing YAML file. Thread-safe.
    /// </summary>
    /// <param name="goalId">Identifier of the goal to update.</param>
    /// <param name="status">New status to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateGoalStatusAsync(string goalId, GoalStatus status, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var goals = await ReadGoalsAsync(ct);
            var goal = goals.FirstOrDefault(g => g.Id == goalId)
                ?? throw new KeyNotFoundException($"Goal '{goalId}' not found in file source.");
            goal.Status = status;
            await WriteGoalsAsync(goals, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    internal async Task<List<Goal>> ReadGoalsAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
            return [];

        var yaml = await File.ReadAllTextAsync(_filePath, ct);
        if (string.IsNullOrWhiteSpace(yaml))
            return [];

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var doc = deserializer.Deserialize<GoalFileDocument?>(yaml);
        if (doc?.Goals is null)
            return [];

        return doc.Goals.Select(MapToGoal).ToList();
    }

    private async Task WriteGoalsAsync(List<Goal> goals, CancellationToken ct)
    {
        var doc = new GoalFileDocument
        {
            Goals = goals.Select(g => new GoalFileEntry
            {
                Id = g.Id,
                Description = g.Description,
                Priority = g.Priority.ToString().ToLowerInvariant(),
                Status = g.Status.ToString().ToLowerInvariant(),
                Repositories = g.RepositoryNames,
            }).ToList(),
        };

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var yaml = serializer.Serialize(doc);
        await File.WriteAllTextAsync(_filePath, yaml, ct);
    }

    private static Goal MapToGoal(GoalFileEntry entry) => new()
    {
        Id = entry.Id ?? throw new InvalidOperationException("Goal entry missing 'id' field."),
        Description = entry.Description ?? string.Empty,
        Priority = ParsePriority(entry.Priority),
        Status = ParseStatus(entry.Status),
        RepositoryNames = entry.Repositories ?? [],
    };

    private static GoalPriority ParsePriority(string? value) => value?.ToLowerInvariant() switch
    {
        "low" => GoalPriority.Low,
        "high" => GoalPriority.High,
        "critical" => GoalPriority.Critical,
        _ => GoalPriority.Normal,
    };

    private static GoalStatus ParseStatus(string? value) => value?.ToLowerInvariant().Replace("_", "") switch
    {
        "inprogress" => GoalStatus.InProgress,
        "completed" => GoalStatus.Completed,
        "failed" => GoalStatus.Failed,
        "cancelled" => GoalStatus.Cancelled,
        _ => GoalStatus.Pending,
    };

    internal sealed class GoalFileDocument
    {
        public List<GoalFileEntry> Goals { get; set; } = [];
    }

    internal sealed class GoalFileEntry
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Priority { get; set; }
        public string? Status { get; set; }
        public List<string>? Repositories { get; set; }
    }
}

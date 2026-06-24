using System.Globalization;
using System.Text.Json;

using CopilotHive.Goals;
using CopilotHive.Persistence.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CopilotHive.Persistence;

/// <summary>
/// Entity Framework Core DbContext for the CopilotHive persistence layer.
/// Provides EF Core persistence for goals, releases, iteration summaries, and pipeline
/// state via <see cref="GoalStore"/> and <see cref="PipelineStore"/>.
/// </summary>
public sealed class CopilotHiveDbContext : DbContext
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>Goals table.</summary>
    public DbSet<Goal> Goals { get; set; } = null!;

    /// <summary>Releases table.</summary>
    public DbSet<Release> Releases { get; set; } = null!;

    /// <summary>Iteration summaries table.</summary>
    public DbSet<IterationSummaryEntity> IterationSummaries { get; set; } = null!;

    /// <summary>Pipeline state table.</summary>
    public DbSet<PipelineEntity> Pipelines { get; set; } = null!;

    /// <summary>Conversation entries table.</summary>
    public DbSet<ConversationEntryEntity> ConversationEntries { get; set; } = null!;

    /// <summary>Task-to-goal mappings table.</summary>
    public DbSet<TaskMappingEntity> TaskMappings { get; set; } = null!;

    /// <summary>
    /// Creates a new context instance for dependency injection.
    /// </summary>
    public CopilotHiveDbContext(DbContextOptions<CopilotHiveDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Creates an in-memory SQLite context for testing. The caller owns the returned
    /// instance and should dispose it when done.
    /// </summary>
    internal static CopilotHiveDbContext CreateInMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<CopilotHiveDbContext>()
            .UseSqlite(connection)
            .Options;

        var context = new CopilotHiveDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureGoal(modelBuilder.Entity<Goal>());
        ConfigureRelease(modelBuilder.Entity<Release>());
        ConfigureIterationSummary(modelBuilder.Entity<IterationSummaryEntity>());
        ConfigurePipeline(modelBuilder.Entity<PipelineEntity>());
        ConfigureConversationEntry(modelBuilder.Entity<ConversationEntryEntity>());
        ConfigureTaskMapping(modelBuilder.Entity<TaskMappingEntity>());
    }

    private static void ConfigureTaskMapping(EntityTypeBuilder<TaskMappingEntity> entity)
    {
        entity.ToTable("task_mappings");

        entity.HasKey(e => e.TaskId);
        entity.Property(e => e.TaskId).HasColumnName("task_id");
        entity.Property(e => e.GoalId).HasColumnName("goal_id").IsRequired();
    }

    private static void ConfigureGoal(EntityTypeBuilder<Goal> entity)
    {
        entity.ToTable("goals");

        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.Description).HasColumnName("description").IsRequired();
        entity.Property(e => e.Status).HasColumnName("status").IsRequired().HasConversion(LowercaseEnumConverter<GoalStatus>());
        entity.Property(e => e.Priority).HasColumnName("priority").IsRequired().HasConversion(LowercaseEnumConverter<GoalPriority>());
        entity.Property(e => e.Scope).HasColumnName("scope").IsRequired().HasConversion(LowercaseEnumConverter<GoalScope>());
        entity.Property(e => e.RepositoryNames).HasColumnName("repositories").HasJsonConversion<List<string>>();
        entity.Property(e => e.Metadata).HasColumnName("metadata").HasJsonConversion<Dictionary<string, string>>();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired().HasConversion(DateTimeToIsoConverter);
        entity.Property(e => e.StartedAt).HasColumnName("started_at").HasConversion(DateTimeToIsoConverter);
        entity.Property(e => e.CompletedAt).HasColumnName("completed_at").HasConversion(DateTimeToIsoConverter);
        entity.Property(e => e.Iterations).HasColumnName("iterations");
        entity.Property(e => e.FailureReason).HasColumnName("failure_reason");
        entity.Property(e => e.Notes).HasColumnName("notes").HasJsonConversion<List<string>>();
        entity.Property(e => e.PhaseDurations).HasColumnName("phase_durations").HasJsonConversion<Dictionary<string, double>?>();
        entity.Property(e => e.TotalDurationSeconds).HasColumnName("total_duration_seconds");
        entity.Property(e => e.DependsOn).HasColumnName("depends_on").HasJsonConversion<List<string>>();
        entity.Property(e => e.Documents).HasColumnName("documents").HasJsonConversion<List<string>>();
        entity.Property(e => e.BranchCleanedUp).HasColumnName("branch_cleaned_up").IsRequired();
        entity.Property(e => e.MergeCommitHash).HasColumnName("merge_commit_hash");
        entity.Property(e => e.ReleaseId).HasColumnName("release_id");

        // Derived collection loaded separately by GoalStore.
        entity.Ignore(e => e.IterationSummaries);

        // Shadow properties for columns the domain model doesn't expose.
        entity.Property<string?>("Title").HasColumnName("title");
        entity.Property<string?>("SourceConversationId").HasColumnName("source_conversation_id");
    }

    private static void ConfigureRelease(EntityTypeBuilder<Release> entity)
    {
        entity.ToTable("releases");

        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.Tag).HasColumnName("tag").IsRequired();
        entity.Property(e => e.Status).HasColumnName("status").IsRequired().HasConversion(LowercaseEnumConverter<ReleaseStatus>());
        entity.Property(e => e.Notes).HasColumnName("notes");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired().HasConversion(DateTimeToIsoConverter);
        entity.Property(e => e.ReleasedAt).HasColumnName("released_at").HasConversion(DateTimeToIsoConverter);
        entity.Property(e => e.RepositoryNames).HasColumnName("repositories").HasJsonConversion<List<string>>();
    }

    private static void ConfigureIterationSummary(EntityTypeBuilder<IterationSummaryEntity> entity)
    {
        entity.ToTable("goal_iterations");

        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.GoalId).HasColumnName("goal_id").IsRequired();
        entity.Property(e => e.Iteration).HasColumnName("iteration").IsRequired();
        entity.Property(e => e.PhasesJson).HasColumnName("phases_json");
        entity.Property(e => e.TestTotal).HasColumnName("test_total");
        entity.Property(e => e.TestPassed).HasColumnName("test_passed");
        entity.Property(e => e.TestFailed).HasColumnName("test_failed");
        entity.Property(e => e.ReviewVerdict).HasColumnName("review_verdict");
        entity.Property(e => e.NotesJson).HasColumnName("notes_json");
        entity.Property(e => e.PhaseOutputsJson).HasColumnName("phase_outputs_json");
        entity.Property(e => e.ClarificationsJson).HasColumnName("clarifications_json");
        entity.Property(e => e.BuildSuccess).HasColumnName("build_success").IsRequired();
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        entity.HasOne<Goal>()
            .WithMany()
            .HasForeignKey(e => e.GoalId)
            .HasPrincipalKey(g => g.Id)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_goal_iterations_goals_GoalId");

        entity.HasIndex(e => e.GoalId).HasDatabaseName("idx_goal_iterations_goal");
        entity.HasIndex(e => new { e.GoalId, e.Iteration })
            .HasDatabaseName("idx_goal_iterations_goal_iteration")
            .IsUnique();
    }

    private static void ConfigurePipeline(EntityTypeBuilder<PipelineEntity> entity)
    {
        entity.ToTable("pipelines");

        entity.HasKey(e => e.GoalId);
        entity.Property(e => e.GoalId).HasColumnName("goal_id");
        entity.Property(e => e.Description).HasColumnName("description").IsRequired();
        entity.Property(e => e.GoalJson).HasColumnName("goal_json").IsRequired();
        entity.Property(e => e.Phase).HasColumnName("phase").IsRequired().HasDefaultValue("Planning");
        entity.Property(e => e.Iteration).HasColumnName("iteration").IsRequired().HasDefaultValue(1);
        entity.Property(e => e.ReviewRetries).HasColumnName("review_retries").IsRequired().HasDefaultValue(0);
        entity.Property(e => e.TestRetries).HasColumnName("test_retries").IsRequired().HasDefaultValue(0);
        entity.Property(e => e.ImproverRetries).HasColumnName("improver_retries").IsRequired().HasDefaultValue(0);
        entity.Property(e => e.MaxRetries).HasColumnName("max_retries").IsRequired().HasDefaultValue(3);
        entity.Property(e => e.MaxIterations).HasColumnName("max_iterations").IsRequired().HasDefaultValue(10);
        entity.Property(e => e.ActiveTaskId).HasColumnName("active_task_id");
        entity.Property(e => e.CoderBranch).HasColumnName("coder_branch");
        entity.Property(e => e.PlanJson).HasColumnName("plan_json");
        entity.Property(e => e.PhaseOutputs).HasColumnName("phase_outputs").IsRequired().HasDefaultValue("{}");
        entity.Property(e => e.MetricsJson).HasColumnName("metrics_json").IsRequired().HasDefaultValue("{}");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
        entity.Property(e => e.GoalStartedAt).HasColumnName("goal_started_at");
        entity.Property(e => e.MergeCommitHash).HasColumnName("merge_commit_hash");
        entity.Property(e => e.RoleSessionsJson).HasColumnName("role_sessions_json").IsRequired().HasDefaultValue("{}");
        entity.Property(e => e.IterationStartSha).HasColumnName("iteration_start_sha");
        entity.Property(e => e.PhaseOccurrence).HasColumnName("phase_occurrence").IsRequired().HasDefaultValue(1);
        entity.Property(e => e.PhaseLogJson).HasColumnName("phase_log_json");
    }

    private static void ConfigureConversationEntry(EntityTypeBuilder<ConversationEntryEntity> entity)
    {
        entity.ToTable("conversation_entries");

        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.GoalId).HasColumnName("goal_id").IsRequired();
        entity.Property(e => e.Seq).HasColumnName("seq").IsRequired();
        entity.Property(e => e.Role).HasColumnName("role").IsRequired();
        entity.Property(e => e.Content).HasColumnName("content").IsRequired();
        entity.Property(e => e.Iteration).HasColumnName("iteration");
        entity.Property(e => e.Purpose).HasColumnName("purpose");

        entity.HasOne<PipelineEntity>()
            .WithMany()
            .HasForeignKey(e => e.GoalId)
            .HasPrincipalKey(p => p.GoalId);

        entity.HasIndex(e => new { e.GoalId, e.Seq }).HasDatabaseName("idx_conversation_goal");
    }

    private static ValueConverter<T, string?> JsonConverter<T>()
    {
        return new ValueConverter<T, string?>(
            v => ReferenceEquals(v, null) ? null : JsonSerializer.Serialize(v, JsonOptions),
            s => string.IsNullOrEmpty(s) ? default! : JsonSerializer.Deserialize<T>(s, JsonOptions)!);
    }

    private static ValueConverter LowercaseEnumConverter<TEnum>()
        where TEnum : struct, Enum
    {
        return new ValueConverter<TEnum, string>(
            e => e.ToString().ToLowerInvariant(),
            s => Enum.Parse<TEnum>(s, true));
    }

    private static readonly ValueConverter DateTimeToIsoConverter = new ValueConverter<DateTime, string>(
        d => d.ToString("O", CultureInfo.InvariantCulture),
        s => DateTime.ParseExact(s, "O", CultureInfo.InvariantCulture));
}

/// <summary>
/// Extension helpers for applying JSON value converters in EF Core model configuration.
/// </summary>
file static class ConversionExtensions
{
    public static PropertyBuilder<TProperty> HasJsonConversion<TProperty>(this PropertyBuilder<TProperty> propertyBuilder)
    {
        var converter = typeof(CopilotHiveDbContext)
            .GetMethod("JsonConverter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.MakeGenericMethod(typeof(TProperty))
            .Invoke(null, null);

        if (converter is null)
            throw new InvalidOperationException($"Unable to create JSON converter for {typeof(TProperty).Name}");

        // Use reflection to call HasConversion with the untyped converter instance.
        var hasConversion = typeof(PropertyBuilder<>)
            .MakeGenericType(typeof(TProperty))
            .GetMethods()
            .First(m => m.Name == "HasConversion" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(ValueConverter));

        hasConversion.Invoke(propertyBuilder, [converter]);
        return propertyBuilder;
    }
}

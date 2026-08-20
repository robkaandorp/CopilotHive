using System.Globalization;
using System.Text.Json;

using CopilotHive.Goals;
using CopilotHive.Persistence.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CopilotHive.Persistence;

/// <summary>
/// Entity Framework Core DbContext for the CopilotHive persistence layer.
/// Provides EF Core persistence for goals, releases, iteration summaries, and pipeline
/// state via <see cref="GoalStore"/> and <see cref="PipelineStore"/>.
/// </summary>
// To add a new migration:
//   dotnet ef migrations add <MigrationName> --project src/CopilotHive --startup-project src/CopilotHive
// Migrations are applied automatically at startup via Database.MigrateAsync().
// The Microsoft.EntityFrameworkCore.Design package is referenced in the test project for tooling.
public sealed class CopilotHiveDbContext : DbContext
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
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

    /// <summary>Users table (single-user admin model).</summary>
    public DbSet<UserEntity> Users { get; set; } = null!;

    /// <summary>Issues table.</summary>
    public DbSet<Issue> Issues { get; set; } = null!;

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
        ConfigureUser(modelBuilder.Entity<UserEntity>());
        ConfigureIssue(modelBuilder.Entity<Issue>());
    }

    private static void ConfigureUser(EntityTypeBuilder<UserEntity> entity)
    {
        entity.ToTable("users");

        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
        entity.Property(e => e.GitHubId).HasColumnName("github_id").IsRequired();
        entity.Property(e => e.Username).HasColumnName("username").IsRequired();
        entity.Property(e => e.DisplayName).HasColumnName("display_name");
        entity.Property(e => e.AvatarUrl).HasColumnName("avatar_url");
        entity.Property(e => e.Email).HasColumnName("email");
        entity.Property(e => e.AccessToken).HasColumnName("access_token").IsRequired();
        entity.Property(e => e.RefreshToken).HasColumnName("refresh_token");
        entity.Property(e => e.TokenExpiresAt).HasColumnName("token_expires_at");
        entity.Property(e => e.Role).HasColumnName("role").IsRequired().HasDefaultValue("admin");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.LastLoginAt).HasColumnName("last_login_at");

        entity.HasIndex(e => e.GitHubId).IsUnique();
    }

    private static void ConfigureIssue(EntityTypeBuilder<Issue> entity)
    {
        entity.ToTable("issues");

        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.Type).HasColumnName("type").IsRequired().HasConversion(LowercaseEnumConverter<IssueType>());
        entity.Property(e => e.Title).HasColumnName("title").IsRequired();
        entity.Property(e => e.Description).HasColumnName("description").IsRequired();
        entity.Property(e => e.Severity).HasColumnName("severity").IsRequired().HasConversion(LowercaseEnumConverter<IssueSeverity>()).HasDefaultValue(IssueSeverity.Low);
        entity.Property(e => e.Status).HasColumnName("status").IsRequired().HasConversion(LowercaseEnumConverter<IssueStatus>()).HasDefaultValue(IssueStatus.Open);
        entity.Property(e => e.RepositoryNames).HasColumnName("repository_names").HasJsonConversion<List<string>>();
        entity.Property(e => e.SourceGoalId).HasColumnName("source_goal_id");
        entity.Property(e => e.SourceRole).HasColumnName("source_role");
        entity.Property(e => e.SourceIteration).HasColumnName("source_iteration");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired().HasConversion(DateTimeToIsoConverter);
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasConversion(DateTimeToIsoConverter);
        entity.Property(e => e.ResolvedAt).HasColumnName("resolved_at").HasConversion(DateTimeToIsoConverter);
        entity.Property(e => e.LinkedGoalId).HasColumnName("linked_goal_id");

        entity.HasIndex(e => e.Status).HasDatabaseName("idx_issues_status");
        entity.HasIndex(e => e.SourceGoalId).HasDatabaseName("idx_issues_source_goal");
        entity.HasIndex(e => e.CreatedAt).HasDatabaseName("idx_issues_created_at").IsDescending();
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
        entity.Property(e => e.ReviewStatus).HasColumnName("review_status").IsRequired().HasConversion(LowercaseEnumConverter<ReviewStatus>()).HasDefaultValue(ReviewStatus.None);
        entity.Property(e => e.RepositoryNames).HasColumnName("repositories").HasJsonConversion<List<string>>();
        entity.Property(e => e.TargetRepositoryNames).HasColumnName("target_repositories");
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
        entity.Property(e => e.ExecutionState).HasColumnName("execution_state").IsRequired().HasConversion(LowercaseEnumConverter<ReleaseExecutionState>()).HasDefaultValue(ReleaseExecutionState.None);
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

    internal static ValueConverter<T, string?> JsonConverter<T>()
    {
        return new ValueConverter<T, string?>(
            v => ReferenceEquals(v, null) ? null : JsonSerializer.Serialize(v, JsonOptions),
            s => string.IsNullOrEmpty(s) ? default! : JsonSerializer.Deserialize<T>(s, JsonOptions)!);
    }

    internal static ValueComparer<T> JsonComparer<T>()
    {
        return new ValueComparer<T>(
            (a, b) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions),
            v => JsonSerializer.Serialize(v, JsonOptions).GetHashCode(),
            v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, JsonOptions), JsonOptions)!);
    }

    private static ValueConverter LowercaseEnumConverter<TEnum>()
        where TEnum : struct, Enum
    {
        return new ValueConverter<TEnum, string>(
            e => e.ToString().ToLowerInvariant(),
            s => Enum.Parse<TEnum>(s, true));
    }

    /// <summary>
    /// Normalizes a <see cref="DateTime"/> to UTC regardless of the machine's local timezone,
    /// so persisted timestamps are always canonical (no timezone-dependent drift on round-trip).
    /// </summary>
    private static DateTime NormalizeToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    // Uses DateTimeStyles.RoundtripKind so a "Z"-suffixed value parses back with
    // DateTimeKind.Utc instead of being silently converted to the local timezone
    // offset (the default DateTime.ParseExact behavior without RoundtripKind).
    private static readonly ValueConverter DateTimeToIsoConverter = new ValueConverter<DateTime, string>(
        d => NormalizeToUtc(d).ToString("O", CultureInfo.InvariantCulture),
        s => NormalizeToUtc(DateTime.ParseExact(s, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
}

/// <summary>
/// Extension helpers for applying JSON value converters in EF Core model configuration.
/// </summary>
file static class ConversionExtensions
{
    public static PropertyBuilder<TProperty> HasJsonConversion<TProperty>(this PropertyBuilder<TProperty> propertyBuilder)
    {
        var converter = CopilotHiveDbContext.JsonConverter<TProperty>();
        var comparer = CopilotHiveDbContext.JsonComparer<TProperty>();
        return propertyBuilder.HasConversion(converter, comparer);
    }
}

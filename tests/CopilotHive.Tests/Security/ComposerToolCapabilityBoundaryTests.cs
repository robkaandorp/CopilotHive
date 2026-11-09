using CopilotHive.Persistence;
using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Security;

/// <summary>
/// THE POLICY BOUNDARIES of the Composer tool surface, asserted over the REAL capability map and the
/// REAL policy evaluator — not over a hand-written copy of the matrix.
/// <para>
/// One activated account per role is seeded into a real SQLite directory, so every answer is produced
/// by the production resolver + production policy over current stored state. A capability boundary
/// that only holds for a hand-written table would not survive a change to the production map.
/// </para>
/// </summary>
public sealed class ComposerToolCapabilityBoundaryTests
{
    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>
    /// THE FULL MATRIX: every tool name on the Composer surface is mapped to exactly one capability,
    /// and each capability's threshold role holds it while the role immediately below does not.
    /// <para>
    /// This is a COMPLETE enumeration of the production map: a tool that is added without a mapping
    /// fails this test (it has no capability at all), and a tool whose mapping is moved to the wrong
    /// capability fails it too.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EveryMappedToolName_IsHeldAtItsCapabilityThresholdAndDeniedOneRoleBelow()
    {
        using var directory = new CapabilityDirectory();
        var helper = directory.Helper();

        // The map is complete over the whole surface: every mapped name has a capability.
        Assert.NotEmpty(ComposerToolAuthorization.MappedToolNames);

        foreach (var toolName in ComposerToolAuthorization.MappedToolNames)
        {
            var capability = ComposerToolAuthorization.CapabilityFor(toolName);
            Assert.NotNull(capability);

            var threshold = HivePolicy.MinimumRole(capability!.Value);
            Assert.True(
                await directory.AuthorizeAsRoleAsync(helper, threshold, toolName),
                $"'{toolName}' must be allowed for {threshold} (its capability {capability}).");

            var below = RoleBelow(threshold);
            if (below is { } lower)
            {
                Assert.False(
                    await directory.AuthorizeAsRoleAsync(helper, lower, toolName),
                    $"'{toolName}' must be denied for {lower} (one role below {threshold}).");
            }
        }
    }

    /// <summary>
    /// THE ABSOLUTE MAP: every tool name on the Composer surface is pinned to EXACTLY the capability
    /// the design matrix assigns it — stated here literally, NOT derived from the production map.
    /// <para>
    /// This is the test that makes the production map load-bearing: the threshold test above derives
    /// its expectations from the map, so a mapping that moved a tool to another capability would move
    /// the expectation with it. Here the expectation is FIXED, so a tool moved to the wrong capability
    /// fails.
    /// </para>
    /// </summary>
    [Fact]
    public void TheToolMap_IsExactlyTheDesignMatrix()
    {
        var expected = new Dictionary<string, HiveCapability>(StringComparer.Ordinal)
        {
            // ── SharedRead ──
            ["get_goal"] = HiveCapability.SharedRead,
            ["get_phase_output"] = HiveCapability.SharedRead,
            ["list_goals"] = HiveCapability.SharedRead,
            ["search_goals"] = HiveCapability.SharedRead,
            ["get_release"] = HiveCapability.SharedRead,
            ["list_releases"] = HiveCapability.SharedRead,
            ["list_issues"] = HiveCapability.SharedRead,
            ["get_issue"] = HiveCapability.SharedRead,
            ["read_document"] = HiveCapability.SharedRead,
            ["search_knowledge"] = HiveCapability.SharedRead,
            ["list_documents"] = HiveCapability.SharedRead,
            ["traverse_graph"] = HiveCapability.SharedRead,
            ["list_config_files"] = HiveCapability.SharedRead,
            ["read_config_file"] = HiveCapability.SharedRead,
            ["list_repositories"] = HiveCapability.SharedRead,
            ["git_log"] = HiveCapability.SharedRead,
            ["git_diff"] = HiveCapability.SharedRead,
            ["git_show"] = HiveCapability.SharedRead,
            ["git_branch"] = HiveCapability.SharedRead,
            ["git_blame"] = HiveCapability.SharedRead,
            ["get_recent_logs"] = HiveCapability.SharedRead,
            ["read_file"] = HiveCapability.SharedRead,
            ["glob"] = HiveCapability.SharedRead,
            ["grep"] = HiveCapability.SharedRead,

            // ── InteractiveMember ──
            ["ask_user"] = HiveCapability.InteractiveMember,
            ["get_current_time"] = HiveCapability.InteractiveMember,
            ["create_issue"] = HiveCapability.InteractiveMember,
            ["create_goal"] = HiveCapability.InteractiveMember,
            ["web_search"] = HiveCapability.InteractiveMember,
            ["web_fetch"] = HiveCapability.InteractiveMember,
            ["start_sub_agent"] = HiveCapability.InteractiveMember,
            ["await_sub_agents"] = HiveCapability.InteractiveMember,
            ["get_sub_agent_status"] = HiveCapability.InteractiveMember,
            ["list_sub_agent_models"] = HiveCapability.InteractiveMember,

            // ── ExecutionOperator ──
            ["approve_goal"] = HiveCapability.ExecutionOperator,
            ["cancel_goal"] = HiveCapability.ExecutionOperator,
            ["extend_goal_iterations"] = HiveCapability.ExecutionOperator,
            ["delete_goal"] = HiveCapability.ExecutionOperator,
            ["create_release"] = HiveCapability.ExecutionOperator,
            ["update_release"] = HiveCapability.ExecutionOperator,
            ["review_goal"] = HiveCapability.ExecutionOperator,
            ["update_issue"] = HiveCapability.ExecutionOperator,
            ["git_fetch"] = HiveCapability.ExecutionOperator,
            ["update_goal"] = HiveCapability.ExecutionOperator,

            // ── GlobalAdmin ──
            ["update_agents_md"] = HiveCapability.GlobalAdmin,
            ["edit_agents_md"] = HiveCapability.GlobalAdmin,
            ["commit_config_changes"] = HiveCapability.GlobalAdmin,
            ["create_document"] = HiveCapability.GlobalAdmin,
            ["update_document"] = HiveCapability.GlobalAdmin,
            ["delete_document"] = HiveCapability.GlobalAdmin,
            ["link_document"] = HiveCapability.GlobalAdmin,
            ["unlink_document"] = HiveCapability.GlobalAdmin,
        };

        // Every expected name maps to EXACTLY the expected capability.
        foreach (var (toolName, capability) in expected)
        {
            Assert.True(
                ComposerToolAuthorization.CapabilityFor(toolName) == capability,
                $"'{toolName}' must require {capability}, but the production map says " +
                $"{ComposerToolAuthorization.CapabilityFor(toolName)?.ToString() ?? "<unmapped>"}.");
        }

        // …and the production map contains NOTHING beyond the design matrix: a tool added later
        // without an explicit mapping fails here instead of silently becoming an extra capability.
        var extra = ComposerToolAuthorization.MappedToolNames
            .Where(name => !expected.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.True(extra.Length == 0,
            "These tool names are mapped but not part of the design matrix: " + string.Join(", ", extra));

        // The four DISABLED tools are not in the map at all.
        foreach (var disabled in new[] { "write_file", "edit_file", "execute_bash_command", "load_skill", "list_skills" })
        {
            Assert.Null(ComposerToolAuthorization.CapabilityFor(disabled));
        }
    }

    /// <summary>
    /// THE ROLE BANDS ARE ABSOLUTE, NOT DERIVED: a Viewer is allowed the SharedRead tools and NOTHING
    /// else, a Member adds the InteractiveMember tools, an Operator adds the ExecutionOperator tools,
    /// and an Admin adds the GlobalAdmin tools — each band pinned to the literal tool names.
    /// </summary>
    [Fact]
    public async Task TheRoleBands_AreAbsolute()
    {
        using var directory = new CapabilityDirectory();
        var helper = directory.Helper();

        // A Viewer: the SharedRead band only.
        foreach (var toolName in new[]
        {
            "get_goal", "get_phase_output", "list_goals", "search_goals", "get_release", "list_releases",
            "list_issues", "get_issue", "read_document", "search_knowledge", "list_documents", "traverse_graph",
            "list_config_files", "read_config_file", "list_repositories", "git_log", "git_diff", "git_show",
            "git_branch", "git_blame", "get_recent_logs", "read_file", "glob", "grep",
        })
        {
            Assert.True(
                await directory.AuthorizeAsRoleAsync(helper, UserRole.Viewer, toolName),
                $"A Viewer must hold '{toolName}' (SharedRead).");
        }

        // A Member adds exactly the InteractiveMember band.
        foreach (var toolName in new[]
        {
            "ask_user", "get_current_time", "create_issue", "create_goal", "web_search", "web_fetch",
            "start_sub_agent", "await_sub_agents", "get_sub_agent_status", "list_sub_agent_models",
        })
        {
            Assert.True(
                await directory.AuthorizeAsRoleAsync(helper, UserRole.Member, toolName),
                $"A Member must hold '{toolName}' (InteractiveMember).");
            Assert.False(
                await directory.AuthorizeAsRoleAsync(helper, UserRole.Viewer, toolName),
                $"A Viewer must NOT hold '{toolName}' (InteractiveMember).");
        }

        // An Operator adds exactly the ExecutionOperator band.
        foreach (var toolName in new[]
        {
            "approve_goal", "cancel_goal", "extend_goal_iterations", "delete_goal", "create_release",
            "update_release", "review_goal", "update_issue", "git_fetch",
        })
        {
            Assert.True(
                await directory.AuthorizeAsRoleAsync(helper, UserRole.Operator, toolName),
                $"An Operator must hold '{toolName}' (ExecutionOperator).");
            Assert.False(
                await directory.AuthorizeAsRoleAsync(helper, UserRole.Member, toolName),
                $"A Member must NOT hold '{toolName}' (ExecutionOperator).");
        }

        // An Admin adds exactly the GlobalAdmin band.
        foreach (var toolName in new[]
        {
            "update_agents_md", "edit_agents_md", "commit_config_changes", "create_document",
            "update_document", "delete_document", "link_document", "unlink_document",
        })
        {
            Assert.True(
                await directory.AuthorizeAsRoleAsync(helper, UserRole.Admin, toolName),
                $"An Admin must hold '{toolName}' (GlobalAdmin).");
            Assert.False(
                await directory.AuthorizeAsRoleAsync(helper, UserRole.Operator, toolName),
                $"An Operator must NOT hold '{toolName}' (GlobalAdmin).");
        }
    }

    /// <summary>
    /// A VIEWER holds ONLY SharedRead: every read tool is allowed and every interactive, execution and
    /// global-admin tool is denied.
    /// </summary>
    [Fact]
    public async Task AViewer_HoldsOnlySharedRead()
    {
        using var directory = new CapabilityDirectory();
        var helper = directory.Helper();

        foreach (var toolName in ComposerToolAuthorization.MappedToolNames)
        {
            var capability = ComposerToolAuthorization.CapabilityFor(toolName)!.Value;
            var allowed = await directory.AuthorizeAsRoleAsync(helper, UserRole.Viewer, toolName);

            Assert.True(
                allowed == (capability == HiveCapability.SharedRead),
                $"'{toolName}' ({capability}) must be allowed for a Viewer only when its capability is SharedRead.");
        }
    }

    /// <summary>
    /// A MEMBER adds InteractiveMember to SharedRead: every interactive tool is allowed and every
    /// execution and global-admin tool is denied.
    /// </summary>
    [Fact]
    public async Task AMember_AddsInteractiveMember()
    {
        using var directory = new CapabilityDirectory();
        var helper = directory.Helper();

        foreach (var toolName in ComposerToolAuthorization.MappedToolNames)
        {
            var capability = ComposerToolAuthorization.CapabilityFor(toolName)!.Value;
            var allowed = await directory.AuthorizeAsRoleAsync(helper, UserRole.Member, toolName);

            Assert.True(
                allowed == (capability is HiveCapability.SharedRead or HiveCapability.InteractiveMember),
                $"'{toolName}' ({capability}) must be allowed for a Member only for SharedRead/InteractiveMember.");
        }
    }

    /// <summary>
    /// An OPERATOR adds ExecutionOperator to the Member band: every execution tool is allowed and the
    /// global-admin tools are still denied.
    /// </summary>
    [Fact]
    public async Task AnOperator_AddsExecutionOperator()
    {
        using var directory = new CapabilityDirectory();
        var helper = directory.Helper();

        foreach (var toolName in ComposerToolAuthorization.MappedToolNames)
        {
            var capability = ComposerToolAuthorization.CapabilityFor(toolName)!.Value;
            var allowed = await directory.AuthorizeAsRoleAsync(helper, UserRole.Operator, toolName);

            Assert.True(
                allowed == (capability != HiveCapability.GlobalAdmin),
                $"'{toolName}' ({capability}) must be allowed for an Operator for every capability but GlobalAdmin.");
        }
    }

    /// <summary>
    /// An ADMIN adds GlobalAdmin: every mapped tool is allowed.
    /// </summary>
    [Fact]
    public async Task AnAdmin_AddsGlobalAdmin()
    {
        using var directory = new CapabilityDirectory();
        var helper = directory.Helper();

        foreach (var toolName in ComposerToolAuthorization.MappedToolNames)
        {
            Assert.True(
                await directory.AuthorizeAsRoleAsync(helper, UserRole.Admin, toolName),
                $"'{toolName}' must be allowed for an Admin.");
        }
    }

    /// <summary>
    /// A DISABLED ACCOUNT HOLDS NOTHING: even an Admin, once disabled, is refused every capability —
    /// including SharedRead, which every enabled role holds.
    /// </summary>
    [Fact]
    public async Task ADisabledAccount_HoldsNoCapabilityAtAll()
    {
        using var directory = new CapabilityDirectory();
        var helper = directory.Helper();

        await directory.SetEnabledAsync(UserRole.Admin, enabled: false);

        foreach (var toolName in ComposerToolAuthorization.MappedToolNames)
        {
            Assert.False(
                await directory.AuthorizeAsRoleAsync(helper, UserRole.Admin, toolName),
                $"'{toolName}' must be denied for a DISABLED Admin.");
        }
    }

    /// <summary>
    /// THE CHILD READ TOOLS MAP TO SharedRead and the FOUR SUB-AGENT TOOLS MAP TO InteractiveMember —
    /// with NO ESCALATION PATH: none of them may require (or be granted at) a higher capability, so a
    /// child agent can never reach a capability its parent lacks.
    /// </summary>
    /// <param name="toolName">The child read tool name.</param>
    [Theory]
    [InlineData("read_file")]
    [InlineData("glob")]
    [InlineData("grep")]
    public async Task TheChildReadTools_MapToSharedRead(string toolName)
    {
        Assert.Equal(HiveCapability.SharedRead, ComposerToolAuthorization.CapabilityFor(toolName));

        using var directory = new CapabilityDirectory();
        var helper = directory.Helper();

        Assert.True(await directory.AuthorizeAsRoleAsync(helper, UserRole.Viewer, toolName));
    }

    /// <summary>
    /// The four sub-agent tools map to InteractiveMember: a Member may delegate, a Viewer may not, and
    /// none of them escalates to an execution or admin capability.
    /// </summary>
    /// <param name="toolName">The sub-agent tool name.</param>
    [Theory]
    [InlineData("start_sub_agent")]
    [InlineData("await_sub_agents")]
    [InlineData("get_sub_agent_status")]
    [InlineData("list_sub_agent_models")]
    public async Task TheFourSubAgentTools_MapToInteractiveMember(string toolName)
    {
        Assert.Equal(HiveCapability.InteractiveMember, ComposerToolAuthorization.CapabilityFor(toolName));

        using var directory = new CapabilityDirectory();
        var helper = directory.Helper();

        Assert.True(await directory.AuthorizeAsRoleAsync(helper, UserRole.Member, toolName));
        Assert.False(await directory.AuthorizeAsRoleAsync(helper, UserRole.Viewer, toolName));
    }

    /// <summary>
    /// THE DISABLED SHAPE: write_file, edit_file, execute_bash_command, load_skill and list_skills are
    /// NOT part of the Composer surface, so they have NO capability mapping and every call naming them
    /// is refused — for an Admin too.
    /// </summary>
    /// <param name="toolName">A tool outside the Composer surface.</param>
    [Theory]
    [InlineData("write_file")]
    [InlineData("edit_file")]
    [InlineData("execute_bash_command")]
    [InlineData("load_skill")]
    [InlineData("list_skills")]
    public async Task TheDisabledTools_HaveNoMappingAndAreRefusedForEveryRole(string toolName)
    {
        Assert.Null(ComposerToolAuthorization.CapabilityFor(toolName));

        using var directory = new CapabilityDirectory();
        var helper = directory.Helper();

        foreach (var role in new[] { UserRole.Viewer, UserRole.Member, UserRole.Operator, UserRole.Admin })
        {
            var result = await directory.AuthorizeResultAsRoleAsync(helper, role, toolName);
            Assert.False(result.IsAllowed);
            Assert.Contains("not an available Composer tool", result.DenialReason!, StringComparison.Ordinal);
        }
    }

    /// <summary>The role immediately below <paramref name="role"/>, or <c>null</c> when there is none.</summary>
    private static UserRole? RoleBelow(UserRole role) => role switch
    {
        UserRole.Member => UserRole.Viewer,
        UserRole.Operator => UserRole.Member,
        UserRole.Admin => UserRole.Operator,
        _ => null,
    };

    /// <summary>
    /// A REAL user directory with ONE activated account per role (plus a way to disable one), over
    /// which the production authorization helper is exercised.
    /// </summary>
    private sealed class CapabilityDirectory : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly UserService _users;
        private readonly HivePrincipalResolver _resolver;
        private readonly Dictionary<UserRole, int> _ids = [];

        internal CapabilityDirectory()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            var options = new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(_connection).Options;
            using (var ctx = new CopilotHiveDbContext(options))
            {
                ctx.Database.EnsureCreated();
            }

            _users = new UserService(new FactoryImpl(options), NullLogger<UserService>.Instance);
            _resolver = new HivePrincipalResolver(
                _users, HiveAuthMode.AuthenticationEnabled, NullLogger<HivePrincipalResolver>.Instance);

            var next = 1;
            foreach (var role in new[] { UserRole.Viewer, UserRole.Member, UserRole.Operator, UserRole.Admin })
            {
                using var db = new CopilotHiveDbContext(options);
                var entity = new CopilotHive.Persistence.Entities.UserEntity
                {
                    GitHubId = (900 + next).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Username = $"role-{role}",
                    AccessToken = "token",
                    Role = role,
                    Enabled = true,
                    CreatedAt = "2024-01-01T00:00:00.0000000Z",
                };
                db.Users.Add(entity);
                db.SaveChanges();
                _ids[role] = entity.Id;
                next++;
            }
        }

        internal ComposerToolAuthorization Helper() =>
            new(_resolver, new HivePolicy(), _users, HiveAuthMode.AuthenticationEnabled,
                NullLogger<ComposerToolAuthorization>.Instance);

        internal async Task<bool> AuthorizeAsRoleAsync(
            ComposerToolAuthorization helper, UserRole role, string toolName) =>
            (await helper.AuthorizeAsync(_ids[role], toolName, ArgumentsFor(toolName), Ct)).IsAllowed;

        internal async Task<ComposerToolAuthorizationResult> AuthorizeResultAsRoleAsync(
            ComposerToolAuthorization helper, UserRole role, string toolName) =>
            await helper.AuthorizeAsync(_ids[role], toolName, ArgumentsFor(toolName), Ct);

        /// <summary>
        /// The argument snapshot a call of this tool carries. <c>update_goal</c> is FIELD-SENSITIVE: its
        /// argument inspection is fail-closed, so a call without a valid <c>field</c> argument is refused
        /// outright — before the capability is even considered. The generic capability matrix therefore
        /// passes a SUPPORTED field, so it measures the capability boundary and not the field rule (the
        /// field rule has its own dedicated tests).
        /// </summary>
        private static IReadOnlyDictionary<string, object?>? ArgumentsFor(string toolName) =>
            string.Equals(toolName, "update_goal", StringComparison.Ordinal)
                ? new Dictionary<string, object?> { ["id"] = "goal-a", ["field"] = "description", ["value"] = "x" }
                : null;

        /// <summary>Disables (or enables) the account holding <paramref name="role"/>.</summary>
        internal async Task SetEnabledAsync(UserRole role, bool enabled)
        {
            await using var db = new CopilotHiveDbContext(
                new DbContextOptionsBuilder<CopilotHiveDbContext>().UseSqlite(_connection).Options);
            var entity = await db.Users.SingleAsync(u => u.Id == _ids[role], Ct);
            entity.Enabled = enabled;
            await db.SaveChangesAsync(Ct);
        }

        public void Dispose() => _connection.Dispose();

        private sealed class FactoryImpl(DbContextOptions<CopilotHiveDbContext> options)
            : IDbContextFactory<CopilotHiveDbContext>
        {
            public CopilotHiveDbContext CreateDbContext() => new(options);
        }
    }
}

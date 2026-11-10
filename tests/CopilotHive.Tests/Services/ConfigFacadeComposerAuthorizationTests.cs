using CopilotHive.Configuration;
using CopilotHive.Security;
using CopilotHive.Services;
using CopilotHive.Tests.Security;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// THE GLOBAL-CONFIG AUTHORIZATION CONTRACT of <see cref="IConfigFacade.SaveComposerAsync"/>: the
/// ENTIRE global Composer write — every update shape, including an EMPTY update and a
/// notification-only update — requires a FRESH <see cref="HiveCapability.GlobalAdmin"/> resolution
/// of the caller's CURRENT stored state.
/// <para>
/// The shapes are enumerated deliberately: an implementation that authorized only "settings"
/// writes would still let a non-admin change the shared event-notification mode, so empty,
/// MaxSteps-only and notification-only each have their own refusal row. The Admin/open-mode rows
/// then prove the write really persists (a discriminating success control, not a dispatch claim).
/// </para>
/// </summary>
public sealed class ConfigFacadeComposerAuthorizationTests : IDisposable
{
    private readonly FacadeAuthorizationHarness _auth = new();

    private const string ViewerId = "cfg-viewer";
    private const string MemberId = "cfg-member";
    private const string OperatorId = "cfg-operator";
    private const string AdminId = "cfg-admin";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static System.Security.Claims.ClaimsPrincipal Principal(string gitHubId) =>
        FacadeAuthorizationHarness.Principal(gitHubId);

    /// <summary>
    /// EVERY update shape the endpoint can send, including the two "empty-ish" forms — so no shape
    /// escapes the admin requirement.
    /// </summary>
    public static TheoryData<string> UpdateShapes => new()
    {
        "empty",
        "max-steps-only",
        "notification-only",
        "notification-mode-only",
        "throttle-only",
        "full",
    };

    private static ComposerSettingsUpdate BuildUpdate(string shape) => shape switch
    {
        "empty" => new ComposerSettingsUpdate(),
        "max-steps-only" => new ComposerSettingsUpdate(MaxSteps: 75),
        "notification-only" => new ComposerSettingsUpdate(
            EventNotificationsMode: "active",
            EventNotificationsActiveEvents: ["goal_completed", "ci_failed"]),
        "notification-mode-only" => new ComposerSettingsUpdate(EventNotificationsMode: "off"),
        "throttle-only" => new ComposerSettingsUpdate(EventNotificationsThrottleSeconds: 90),
        "full" => new ComposerSettingsUpdate(
            MaxSteps: 90,
            EventNotificationsMode: "active",
            EventNotificationsActiveEvents: ["goal_completed"],
            EventNotificationsThrottleSeconds: 45),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown update shape."),
    };

    /// <summary>A real config + real <see cref="ConfigModelService"/> over a fake config repo.</summary>
    private (HiveConfigFile Config, ConfigModelService Service, string Dir) CreateRealService()
    {
        var (config, service, dir) = ConfigFacadeTests.CreateRealService();
        return (config, service, dir);
    }

    private ConfigFacade Facade(
        HiveConfigFile config, ConfigModelService? service, HiveAuthMode mode) =>
        new(config, service, discovery: null, NullLogger<ConfigFacade>.Instance,
            _auth.Resolver(mode), _auth.Policy);

    // ── Non-admin refused for EVERY update shape ────────────────────────────

    /// <summary>
    /// A Viewer, a Member and an Operator are all refused with
    /// <see cref="FacadeErrorKind.Forbidden"/> for EVERY update shape, and NOTHING is persisted
    /// (a denial must run before the validate → mutate → write → commit pipeline).
    /// </summary>
    [Theory]
    [MemberData(nameof(UpdateShapes))]
    public async Task NonAdmin_IsRefusedForbidden_ForEveryUpdateShape(string shape)
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);
        await _auth.SeedAsync(MemberId, UserRole.Member);
        await _auth.SeedAsync(OperatorId, UserRole.Operator);
        await _auth.SeedAsync(AdminId, UserRole.Admin);

        foreach (var nonAdmin in new[] { ViewerId, MemberId, OperatorId })
        {
            var (config, service, dir) = CreateRealService();
            try
            {
                var result = await Facade(config, service, HiveAuthMode.AuthenticationEnabled)
                    .SaveComposerAsync(BuildUpdate(shape), Principal(nonAdmin), Ct);

                Assert.False(result.Success);
                Assert.Null(result.Value);
                Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
                Assert.Equal("The acting account is not authorized to perform this operation.", result.Error);
                Assert.Null(config.Composer);
            }
            finally
            {
                ConfigFacadeTests.CleanupDir(dir);
            }
        }
    }

    /// <summary>
    /// An UNAUTHENTICATED caller and an authenticated identity with no activated account are also
    /// refused for every update shape — there is no unauthenticated overload in auth-enabled mode.
    /// </summary>
    [Theory]
    [MemberData(nameof(UpdateShapes))]
    public async Task UnauthenticatedAndUnknownAccounts_AreRefusedForEveryUpdateShape(string shape)
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);

        var (config, service, dir) = CreateRealService();
        try
        {
            var facade = Facade(config, service, HiveAuthMode.AuthenticationEnabled);

            var anonymous = await facade.SaveComposerAsync(BuildUpdate(shape), actingUser: null, Ct);
            Assert.Equal(FacadeErrorKind.Forbidden, anonymous.Kind);

            var unknown = await facade.SaveComposerAsync(
                BuildUpdate(shape), Principal("never-invited"), Ct);
            Assert.Equal(FacadeErrorKind.Forbidden, unknown.Kind);

            Assert.Null(config.Composer);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    // ── Admin and the open-mode local operator succeed ──────────────────────

    /// <summary>
    /// THE ADMIN MATRIX: an Admin succeeds for EVERY update shape — empty, MaxSteps-only,
    /// notification-only, notification-mode-only, throttle-only and full — and the persisted
    /// values are asserted for each shape.
    /// <para>
    /// The refusal rows below enumerate the same shapes for the non-admin roles, so the two
    /// halves together prove that the gate is EXACTLY the admin requirement and not something
    /// narrower (a gate that only refused the empty update would fail the refusal rows) nor
    /// something broader (a gate that refused a notification-only write would fail this row).
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(UpdateShapes))]
    public async Task Admin_Succeeds_ForEveryUpdateShape_AndPersistsIt(string shape)
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);

        var (config, service, dir) = CreateRealService();
        try
        {
            var result = await Facade(config, service, HiveAuthMode.AuthenticationEnabled)
                .SaveComposerAsync(BuildUpdate(shape), Principal(AdminId), Ct);

            Assert.True(result.Success, result.Error ?? $"admin {shape} write failed");
            Assert.Equal(FacadeErrorKind.None, result.Kind);
            Assert.True(result.Value!.Saved);

            // The write really happened: the stored values carry the shape's own content.
            var composer = config.Composer!;
            switch (shape)
            {
                case "empty":
                    // An empty update is a no-op persistence: it creates the section but changes no
                    // stored setting (MaxSteps keeps its default, no notification section appears).
                    Assert.Equal(Constants.DefaultBrainMaxSteps, composer.MaxSteps);
                    Assert.Null(composer.EventNotifications);
                    break;

                case "max-steps-only":
                    Assert.Equal(75, composer.MaxSteps);
                    Assert.Null(composer.EventNotifications);
                    break;

                case "notification-only":
                    Assert.Equal("active", composer.EventNotifications!.Mode);
                    Assert.Equal(["goal_completed", "ci_failed"], composer.EventNotifications.ActiveEvents);
                    break;

                case "notification-mode-only":
                    Assert.Equal("off", composer.EventNotifications!.Mode);
                    break;

                case "throttle-only":
                    Assert.Equal(90, composer.EventNotifications!.ThrottleSeconds);
                    break;

                case "full":
                    Assert.Equal(90, composer.MaxSteps);
                    Assert.Equal("active", composer.EventNotifications!.Mode);
                    Assert.Equal(["goal_completed"], composer.EventNotifications.ActiveEvents);
                    Assert.Equal(45, composer.EventNotifications.ThrottleSeconds);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown update shape.");
            }
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// THE DISCRIMINATING SUCCESS CONTROL: an Admin really persists every update shape (the exact
    /// stored values are asserted), so the refusal rows above cannot be satisfied by an
    /// implementation that simply rejects everything.
    /// </summary>
    [Fact]
    public async Task Admin_PersistsEveryUpdateShape()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);

        var (config, service, dir) = CreateRealService();
        try
        {
            var facade = Facade(config, service, HiveAuthMode.AuthenticationEnabled);
            var admin = Principal(AdminId);

            var maxSteps = await facade.SaveComposerAsync(
                new ComposerSettingsUpdate(MaxSteps: 75), admin, Ct);
            Assert.True(maxSteps.Success, maxSteps.Error ?? "MaxSteps-only admin write failed");
            Assert.Equal(FacadeErrorKind.None, maxSteps.Kind);
            Assert.True(maxSteps.Value!.Saved);
            Assert.Equal(75, config.Composer!.MaxSteps);

            var notifications = await facade.SaveComposerAsync(
                new ComposerSettingsUpdate(
                    EventNotificationsMode: "active",
                    EventNotificationsActiveEvents: ["goal_completed", "ci_failed"],
                    EventNotificationsThrottleSeconds: 60),
                admin, Ct);
            Assert.True(notifications.Success, notifications.Error ?? "notification-only admin write failed");
            Assert.Equal("active", config.Composer!.EventNotifications!.Mode);
            Assert.Equal(["goal_completed", "ci_failed"], config.Composer.EventNotifications.ActiveEvents);
            Assert.Equal(60, config.Composer.EventNotifications.ThrottleSeconds);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// An EMPTY update by an Admin is accepted (it is a no-op persistence, exactly as before) — the
    /// admin requirement must not change the operation's own semantics for an authorized caller.
    /// </summary>
    [Fact]
    public async Task Admin_EmptyUpdate_IsAccepted()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);

        var (config, service, dir) = CreateRealService();
        try
        {
            var result = await Facade(config, service, HiveAuthMode.AuthenticationEnabled)
                .SaveComposerAsync(new ComposerSettingsUpdate(), Principal(AdminId), Ct);

            Assert.True(result.Success, result.Error ?? "empty admin write failed");
            Assert.True(result.Value!.Saved);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// OPEN MODE: the resolver yields the legacy local operator, so a null identity still persists
    /// every update shape — backward compatibility for the single-operator installation.
    /// </summary>
    [Theory]
    [MemberData(nameof(UpdateShapes))]
    public async Task OpenMode_LocalOperator_SucceedsForEveryUpdateShape(string shape)
    {
        var (config, service, dir) = CreateRealService();
        try
        {
            var result = await Facade(config, service, HiveAuthMode.Open)
                .SaveComposerAsync(BuildUpdate(shape), actingUser: null, Ct);

            Assert.True(result.Success, result.Error ?? $"open-mode {shape} write failed");
            Assert.Equal(FacadeErrorKind.None, result.Kind);
            Assert.True(result.Value!.Saved);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    // ── Fresh resolution: demotion between two calls ────────────────────────

    /// <summary>
    /// An Admin who demoted themselves (or was demoted) is refused on the VERY NEXT call with the
    /// SAME principal object: the write re-reads current stored state every time.
    /// </summary>
    [Fact]
    public async Task AdminDemotedBetweenTwoCalls_IsRefusedOnTheSecondCall()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);

        var (config, service, dir) = CreateRealService();
        try
        {
            var facade = Facade(config, service, HiveAuthMode.AuthenticationEnabled);
            var admin = Principal(AdminId);

            var first = await facade.SaveComposerAsync(new ComposerSettingsUpdate(MaxSteps: 60), admin, Ct);
            Assert.True(first.Success, first.Error ?? "first admin write failed");
            Assert.Equal(60, config.Composer!.MaxSteps);

            await _auth.SetRoleAsync(AdminId, UserRole.Member);

            var second = await facade.SaveComposerAsync(new ComposerSettingsUpdate(MaxSteps: 99), admin, Ct);
            Assert.False(second.Success);
            Assert.Equal(FacadeErrorKind.Forbidden, second.Kind);
            // The refused write really did not persist.
            Assert.Equal(60, config.Composer!.MaxSteps);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    // ── The authorization answer is never the invalid-input BadRequest ──────

    /// <summary>
    /// Authorization runs BEFORE the update's own validation: a Viewer sending an INVALID mode is
    /// refused <see cref="FacadeErrorKind.Forbidden"/>, never the service's
    /// <see cref="FacadeErrorKind.BadRequest"/> message — so an unauthorized caller cannot use the
    /// validation messages to probe the schema, and the two answers stay distinguishable.
    /// </summary>
    [Fact]
    public async Task Viewer_SendingAnInvalidUpdate_GetsForbidden_NotBadRequest()
    {
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        var (config, service, dir) = CreateRealService();
        try
        {
            var result = await Facade(config, service, HiveAuthMode.AuthenticationEnabled)
                .SaveComposerAsync(
                    new ComposerSettingsUpdate(EventNotificationsMode: "bogus"), Principal(ViewerId), Ct);

            Assert.Equal(FacadeErrorKind.Forbidden, result.Kind);
            Assert.NotEqual(FacadeErrorKind.BadRequest, result.Kind);
            Assert.DoesNotContain("bogus", result.Error!, StringComparison.Ordinal);
            Assert.DoesNotContain("Invalid event notification mode", result.Error!, StringComparison.Ordinal);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// Cancellation propagates out of the authorized path (it is never converted into a result), and
    /// an unauthorized caller cannot turn a cancelled request into an authorization answer either.
    /// </summary>
    [Fact]
    public async Task CancelledToken_Propagates_ForAdminAndForViewer()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);
        await _auth.SeedAsync(ViewerId, UserRole.Viewer);

        var (config, service, dir) = CreateRealService();
        try
        {
            var facade = Facade(config, service, HiveAuthMode.AuthenticationEnabled);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                facade.SaveComposerAsync(new ComposerSettingsUpdate(MaxSteps: 75), Principal(AdminId), cts.Token));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                facade.SaveComposerAsync(new ComposerSettingsUpdate(MaxSteps: 75), Principal(ViewerId), cts.Token));
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// THE OPERATIONAL CONTRACT FOR AN AUTHORIZED CALLER: an Admin sending an INVALID mode or an
    /// UNKNOWN event still gets <see cref="FacadeErrorKind.BadRequest"/> carrying the SERVICE's own
    /// exact message — the pre-facade endpoint's 400 contract, unchanged by the authorization gate.
    /// <para>
    /// This row and the refusal row above are the discriminating pair: the same invalid inputs
    /// produce a BadRequest for an authorized caller and a Forbidden for an unauthorized one, so
    /// the two answers can never be confused (and an unauthorized caller can never learn the
    /// validation messages).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Admin_SendingAnInvalidUpdate_KeepsTheBadRequestContractWithTheServiceMessage()
    {
        await _auth.SeedAsync(AdminId, UserRole.Admin);

        var (config, service, dir) = CreateRealService();
        try
        {
            var facade = Facade(config, service, HiveAuthMode.AuthenticationEnabled);

            var badMode = await facade.SaveComposerAsync(
                new ComposerSettingsUpdate(EventNotificationsMode: "bogus"), Principal(AdminId), Ct);

            Assert.False(badMode.Success);
            Assert.Equal(FacadeErrorKind.BadRequest, badMode.Kind);
            Assert.Equal(
                "Invalid event notification mode 'bogus'. Valid values: passive, active, off.",
                badMode.Error);

            var badEvent = await facade.SaveComposerAsync(
                new ComposerSettingsUpdate(EventNotificationsActiveEvents: ["not_an_event"]),
                Principal(AdminId), Ct);

            Assert.False(badEvent.Success);
            Assert.Equal(FacadeErrorKind.BadRequest, badEvent.Kind);
            Assert.Contains("Invalid active event 'not_an_event'.", badEvent.Error!, StringComparison.Ordinal);

            // Validation runs before mutation: nothing was written.
            Assert.Null(config.Composer);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// THE NO-BYPASS SURFACE CONTRACT — THE WHOLE SURFACE, NOT ONE METHOD: EVERY method declared
    /// by <see cref="IConfigFacade"/> that performs a WRITE declares at most ONE overload, so a
    /// second (identity-free) overload cannot appear unnoticed.
    /// <para>
    /// The slice only requires the global Composer write to be admin-gated, so this test asserts
    /// the SHAPE of the surface rather than the policy of each method: a re-added identity-free
    /// <c>SaveComposerAsync</c> overload fails here, and so does any future identity-free write
    /// overload. (The other write methods' own policies are unchanged by this slice and remain
    /// covered by their existing tests.)
    /// </para>
    /// </summary>
    [Fact]
    public void NoConfigFacadeWriteMethod_OffersASecondOverloadThatCouldBeAnIdentityFreeBypass()
    {
        var writes = typeof(IConfigFacade)
            .GetMethods()
            .Where(m => m.Name.StartsWith("Save", StringComparison.Ordinal)
                || m.Name.StartsWith("Add", StringComparison.Ordinal)
                || m.Name.StartsWith("Update", StringComparison.Ordinal)
                || m.Name.StartsWith("Remove", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(writes);

        // At most ONE overload may exist per write method name: a second overload is exactly the
        // shape an identity-free bypass would take.
        foreach (var group in writes.GroupBy(m => m.Name, StringComparer.Ordinal))
        {
            Assert.True(
                group.Count() == 1,
                $"{group.Key} declares {group.Count()} overloads; a second overload could be an identity-free bypass.");
        }

        // The global Composer write — the one this slice gates — carries the REQUIRED identity.
        var saveComposer = Assert.Single(
            typeof(IConfigFacade).GetMethods(),
            m => m.Name == nameof(IConfigFacade.SaveComposerAsync));
        var identity = Assert.Single(
            saveComposer.GetParameters(),
            p => p.ParameterType == typeof(System.Security.Claims.ClaimsPrincipal));
        Assert.False(identity.IsOptional, "the acting identity must be REQUIRED, never defaulted.");
    }

    /// <summary>
    /// THE NO-BYPASS SURFACE CONTRACT: <see cref="IConfigFacade.SaveComposerAsync"/> declares EXACTLY
    /// one overload, and both the acting identity and the cancellation token are REQUIRED — so a
    /// re-added identity-free overload (or a defaulted identity) fails here before it can ship an
    /// unauthenticated global write.
    /// </summary>
    [Fact]
    public void SaveComposerAsync_RequiresAnIdentityAndACancellationToken_WithNoBypassOverload()
    {
        var methods = typeof(IConfigFacade).GetMethods()
            .Where(m => m.Name == nameof(IConfigFacade.SaveComposerAsync))
            .ToArray();

        var method = Assert.Single(methods);
        var parameters = method.GetParameters();

        Assert.Equal(
            [typeof(ComposerSettingsUpdate), typeof(System.Security.Claims.ClaimsPrincipal), typeof(CancellationToken)],
            parameters.Select(p => p.ParameterType).ToArray());
        Assert.All(parameters, p => Assert.False(p.IsOptional, $"{p.Name} must be required."));
    }

    public void Dispose() => _auth.Dispose();
}

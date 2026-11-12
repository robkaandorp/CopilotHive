using CopilotHive.Security;
using CopilotHive.Services;

using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests.Security;

/// <summary>
/// Covers the ONE <c>Program.cs</c> change this slice makes: the DI REGISTRATION of the authorization
/// primitives (auth-mode input, policy catalog, resolver, scoped circuit accessor) in a real host.
/// <para>
/// Nothing else about <c>Program.cs</c> is re-verified here — the fallback policy and the OAuth
/// admission path are unchanged and are covered by their existing tests. What this file proves is
/// that the primitives resolve, that their lifetimes match the security contract (stateless
/// singletons + one accessor per circuit scope), and that in open mode the resolver reports the
/// legacy full local operator so backward compatibility is preserved.
/// </para>
/// </summary>
[Collection("HiveIntegration")]
public sealed class SecurityPrimitivesRegistrationTests
{
    private readonly HiveTestFactory _factory;

    /// <summary>Receives the shared host fixture for this test class.</summary>
    /// <param name="factory">The shared <see cref="HiveTestFactory"/>.</param>
    public SecurityPrimitivesRegistrationTests(HiveTestFactory factory) => _factory = factory;

    /// <summary>
    /// The registered auth mode is the EXPLICIT host input that mirrors <c>Program.cs</c>'s OAuth
    /// predicate, and the resolver carries that exact instance (so a resolver can never disagree with
    /// the OAuth registration about whether authentication is enabled).
    /// </summary>
    [Fact]
    public void AuthMode_IsRegisteredAsTheProgramPredicate_AndSharedWithTheResolver()
    {
        var mode = _factory.Services.GetRequiredService<HiveAuthMode>();
        var expected = HiveAuthMode.FromValues(
            Environment.GetEnvironmentVariable(HiveAuthMode.ClientIdEnvironmentVariable),
            Environment.GetEnvironmentVariable(HiveAuthMode.ClientSecretEnvironmentVariable));

        Assert.Equal(expected.AuthEnabled, mode.AuthEnabled);

        var resolver = _factory.Services.GetRequiredService<HivePrincipalResolver>();
        Assert.Same(mode, resolver.Mode);
    }

    /// <summary>
    /// The policy catalog and the resolver are SINGLETONS: one instance per host, because neither
    /// holds any per-user state.
    /// </summary>
    [Fact]
    public void PolicyAndResolver_AreSingletons()
    {
        Assert.Same(
            _factory.Services.GetRequiredService<HivePolicy>(),
            _factory.Services.GetRequiredService<HivePolicy>());

        Assert.Same(
            _factory.Services.GetRequiredService<HivePrincipalResolver>(),
            _factory.Services.GetRequiredService<HivePrincipalResolver>());
    }

    /// <summary>
    /// The circuit accessor is SCOPED: two scopes get DIFFERENT instances (one per circuit), so no
    /// user principal is ever shared between circuits by construction.
    /// </summary>
    [Fact]
    public void CircuitAccessor_IsScoped_SoTwoScopesNeverShareOne()
    {
        var scopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();

        using var firstScope = scopeFactory.CreateScope();
        using var secondScope = scopeFactory.CreateScope();

        var first = firstScope.ServiceProvider.GetRequiredService<CircuitUserPrincipalAccessor>();
        var second = secondScope.ServiceProvider.GetRequiredService<CircuitUserPrincipalAccessor>();

        Assert.NotSame(first, second);

        // ...and each scope keeps its OWN instance.
        Assert.Same(first, firstScope.ServiceProvider.GetRequiredService<CircuitUserPrincipalAccessor>());
    }

    /// <summary>
    /// The registered resolver's ANONYMOUS behaviour is consistent with the registered mode, whichever
    /// it is: in open mode the caller gets the legacy full local operator (backward compatibility),
    /// and in auth-enabled mode the same call FAILS CLOSED with an explicit refusal — never an
    /// open-mode principal. The test therefore proves the anti-escalation property in either host
    /// mode instead of assuming the environment.
    /// </summary>
    [Fact]
    public async Task ResolverBehaviourForAnAnonymousCaller_MatchesTheRegisteredAuthMode()
    {
        var mode = _factory.Services.GetRequiredService<HiveAuthMode>();
        var resolver = _factory.Services.GetRequiredService<HivePrincipalResolver>();
        var policy = _factory.Services.GetRequiredService<HivePolicy>();

        var resolution = await resolver.ResolveAsync(
            claimsPrincipal: null, TestContext.Current.CancellationToken);

        if (mode.AuthEnabled)
        {
            Assert.False(resolution.IsResolved);
            Assert.Null(resolution.Principal);
            Assert.Equal(PrincipalRefusalReason.Unauthenticated, resolution.RefusalReason);
        }
        else
        {
            var principal = Assert.IsType<ActingUserPrincipal>(resolution.Principal);
            Assert.True(principal.IsLocalOperator);
            Assert.True(policy.IsAllowed(principal, HiveCapability.GlobalAdmin));
        }
    }

    /// <summary>
    /// THE SINGLETON RESOLVER NEVER CAPTURES A PRINCIPAL: it holds no <see cref="ActingUserPrincipal"/>
    /// and no credential-bearing entity field, so nothing per-user can be retained across requests.
    /// </summary>
    [Fact]
    public void Resolver_HoldsNoPerUserStateFields()
    {
        var fieldTypes = typeof(HivePrincipalResolver)
            .GetFields(System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public)
            .Select(f => f.FieldType)
            .ToArray();

        Assert.DoesNotContain(typeof(ActingUserPrincipal), fieldTypes);
        Assert.DoesNotContain(typeof(CopilotHive.Persistence.Entities.UserEntity), fieldTypes);
    }

    /// <summary>
    /// ENFORCEMENT IS CONFINED TO THE BOUNDARIES THE AUTHORIZATION SLICES WIRE: the Composer tool
    /// surface, the composer/config facades and hubs, the circuit-side identity acquisition helper,
    /// the two component call sites — and, as of the GOAL/ISSUE OPERATION AUTHORIZATION slice, the
    /// goal and issue facades plus the goal/issue routes in <c>ApiEndpoints.cs</c> (which now forward
    /// the request's <c>HttpContext.User</c> to those facades instead of reading the stores).
    /// <para>
    /// Every OTHER boundary (the release/backup facades, the router, the nav menu, the Composer
    /// actor, the user directory and the credential entity) still references none of the primitives,
    /// so no boundary OUTSIDE the declared surface gained enforcement. <see cref="UserService"/> and
    /// <see cref="CopilotHive.Persistence.Entities.UserEntity"/> remain untouched contracts the
    /// primitives consume rather than modify, and <c>Program.cs</c> remains the DI registration site.
    /// </para>
    /// </summary>
    [Fact]
    public void BoundariesOutsideTheAuthorizedSurface_ReferenceNoneOfTheNewPrimitives()
    {
        var repoRoot = TestRepoRoot.Find();
        var src = Path.Combine(repoRoot, "src", "CopilotHive");

        // The boundaries still deliberately LEFT unchanged. Wiring any of them is a new enforcement
        // decision that must come with its own contract tests, so it fails here first.
        //
        // REMOVED FROM THIS LIST BY THE GOAL/ISSUE OPERATION AUTHORIZATION SLICE (and therefore now
        // covered by TheAuthorizedFacadesAndHubs_ReferenceTheResolverAndPolicy below):
        // "ApiEndpoints.cs" (only its GOAL/ISSUE routes are covered — release/config/backup/
        // clarification/session routes stay unchanged), "Services/GoalFacade.cs" and
        // "Services/IssueFacade.cs".
        var boundaries = new[]
        {
            "Services/ReleaseFacade.cs",
            "Services/BackupFacade.cs",
            "Services/UserService.cs",
            "Components/Routes.razor",
            "Components/Layout/NavMenu.razor",
            "Actors/ComposerActor.cs",
            "Persistence/Entities/UserEntity.cs",
        };

        string[] primitiveNames =
        [
            "HivePrincipalResolver",
            "CircuitUserPrincipalAccessor",
            "HiveCapability",
            "ActingUserPrincipal",
            "HivePolicy",
            "PrincipalResolution",
            "ComposerToolAuthorization",
            "CopilotHive.Security",
        ];

        foreach (var relativePath in boundaries)
        {
            var path = Path.Combine(src, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Boundary file not found at {path}");

            var body = TestSourceScanner.StripNonCode(
                File.ReadAllText(path).ReplaceLineEndings("\n"));

            foreach (var primitive in primitiveNames)
            {
                Assert.DoesNotContain(primitive, body, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// THE DECLARED SERVER-SIDE SURFACE REALLY ENFORCES: the facades resolve the acting principal
    /// through <see cref="HivePrincipalResolver"/> and consult <see cref="HivePolicy"/> on every
    /// operation (through the single <c>FacadeAuthorization</c> seam), the goal/issue REST routes in
    /// <c>ApiEndpoints.cs</c> carry the REQUEST identity to them, and both hubs carry the request's
    /// identity to their facades. A facade or hub that dropped the enforcement — or a re-introduced
    /// identity-free overload — fails this test.
    /// </summary>
    [Fact]
    public void TheAuthorizedFacadesAndHubs_ReferenceTheResolverAndPolicy()
    {
        var repoRoot = TestRepoRoot.Find();
        var src = Path.Combine(repoRoot, "src", "CopilotHive");

        foreach (var relativePath in new[]
        {
            "Services/ComposerFacade.cs",
            "Services/ConfigFacade.cs",
            // The GOAL/ISSUE OPERATION AUTHORIZATION slice: every goal and issue operation is
            // authorized before any store work.
            "Services/GoalFacade.cs",
            "Services/IssueFacade.cs",
        })
        {
            var body = TestSourceScanner.StripNonCode(
                File.ReadAllText(Path.Combine(src, relativePath.Replace('/', Path.DirectorySeparatorChar)))
                    .ReplaceLineEndings("\n"));

            Assert.Contains("HivePrincipalResolver", body, StringComparison.Ordinal);
            Assert.Contains("HivePolicy", body, StringComparison.Ordinal);
            Assert.Contains("FacadeAuthorization", body, StringComparison.Ordinal);
            // The SCOPED circuit accessor must never be captured by a facade.
            Assert.DoesNotContain("CircuitUserPrincipalAccessor", body, StringComparison.Ordinal);
        }

        // THE GOAL/ISSUE ROUTES FORWARD THE REQUEST IDENTITY: every covered handler takes the
        // HttpContext and passes its User to the facade, so the facade can re-resolve current state.
        // The collapsed (whitespace-free) form is used so a reformatted parameter list can neither
        // hide nor fake the wiring.
        var api = TestSourceScanner.StripNonCode(
            File.ReadAllText(Path.Combine(src, "ApiEndpoints.cs")).ReplaceLineEndings("\n"));
        var apiCollapsed = new string(api.Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Contains("HttpContextcontext", apiCollapsed, StringComparison.Ordinal);
        Assert.Contains("context.User", apiCollapsed, StringComparison.Ordinal);

        // No covered goal/issue read or mutation reaches a store directly any more: the routes bind
        // the FACADE, not IGoalStore/IIssueStore, except goal creation (a separate dependent goal).
        Assert.DoesNotContain(
            "MapGet(\"/\",async(IGoalStorestore)", apiCollapsed, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "MapGet(\"/{id}\",async(stringid,IGoalStorestore)", apiCollapsed, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "MapGet(\"/search\",async(stringq,string?status,IGoalStorestore)", apiCollapsed, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "review-status\",async(stringid,GoalReviewStatusUpdateupdate,IGoalStorestore", apiCollapsed, StringComparison.Ordinal);

        foreach (var relativePath in new[]
        {
            "Orchestration/ComposerHub.cs",
            "Orchestration/ConfigHub.cs",
        })
        {
            // Line endings are normalised to "\n" (never removed): the scanner needs line starts to
            // recognise line comments, otherwise everything after the first "//" would be masked.
            var body = TestSourceScanner.StripNonCode(
                File.ReadAllText(Path.Combine(src, relativePath.Replace('/', Path.DirectorySeparatorChar)))
                    .ReplaceLineEndings("\n"));

            // Identity is taken PER REQUEST from the HttpContext and forwarded to the facade.
            // ALL whitespace is removed so a reformatted parameter list can neither hide nor fake the
            // wiring (the fragments below are written whitespace-free for the same reason).
            var collapsed = new string(body.Where(c => !char.IsWhiteSpace(c)).ToArray());
            Assert.Contains("HttpContextcontext", collapsed, StringComparison.Ordinal);
            Assert.Contains("context.User", collapsed, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// THE CIRCUIT CALLERS ARE MIGRATED (goal scope 6 — MINIMAL circuit identity acquisition): both
    /// pages obtain the circuit identity and initialise the SCOPED accessor through the single
    /// <c>ComponentActingUser</c> helper, forward that identity to EVERY identity-aware facade call,
    /// and use the accessor ONLY for a capability question. The pages never reach into the resolver,
    /// the policy or the principal type directly, and nothing registers the scoped accessor with a
    /// singleton.
    /// </summary>
    [Fact]
    public void TheCircuitCallers_AcquireIdentityThroughTheSingleHelper()
    {
        var repoRoot = TestRepoRoot.Find();
        var src = Path.Combine(repoRoot, "src", "CopilotHive");

        foreach (var relativePath in new[]
        {
            "Components/Pages/ComposerChat.razor",
            "Components/Pages/Configuration.razor",
        })
        {
            var raw = File.ReadAllText(
                Path.Combine(src, relativePath.Replace('/', Path.DirectorySeparatorChar)))
                .ReplaceLineEndings("\n");
            var body = TestSourceScanner.StripNonCode(raw);

            // The scoped accessor is INJECTED per circuit and initialised through the helper.
            Assert.Contains("CircuitUserPrincipalAccessor", raw, StringComparison.Ordinal);
            Assert.Contains("ComponentActingUser", body, StringComparison.Ordinal);

            // The identity is acquired through that ONE helper — never a second resolution path and
            // never the principal/policy types directly.
            Assert.DoesNotContain("HivePrincipalResolver", body, StringComparison.Ordinal);
            Assert.DoesNotContain("HivePolicy", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ActingUserPrincipal ", body, StringComparison.Ordinal);
            Assert.DoesNotContain(".ResolveAsync(", body, StringComparison.Ordinal);
        }

        // The pages hand an identity + cancellation token to every migrated facade call: no
        // identity-free call site survives anywhere in the component tree.
        foreach (var page in new[] { "Components/Pages/ComposerChat.razor", "Components/Pages/Configuration.razor" })
        {
            var body = TestSourceScanner.StripNonCode(
                File.ReadAllText(Path.Combine(src, page.Replace('/', Path.DirectorySeparatorChar)))
                    .ReplaceLineEndings("\n"));

            foreach (var operation in new[]
            {
                "ComposerFacade.GetModelsAsync(", "ComposerFacade.GetCurrentModelAsync(",
                "ComposerFacade.SwitchModelAsync(", "ComposerFacade.CompactAsync(",
                "ComposerFacade.CompactPartialAsync(", "ConfigFacade.SaveComposerAsync(",
            })
            {
                foreach (var statement in StatementsContaining(body, operation))
                {
                    Assert.True(
                        statement.Contains("ActingUserAsync", StringComparison.Ordinal)
                        || statement.Contains("AcquireAsync", StringComparison.Ordinal),
                        $"'{operation}' in {page} must pass the circuit identity acquired through ComponentActingUser. "
                        + $"Offending statement: {statement}");
                }
            }
        }

        // ADMIN GATING IS DERIVED FROM THE CIRCUIT IDENTITY, not assumed: each page asks the
        // accessor for GlobalAdmin through the helper, and the GLOBAL notification UI is rendered from
        // the resulting flag. Deleting the question (or hardcoding the flag true) fails here.
        foreach (var page in new[] { "Components/Pages/ComposerChat.razor", "Components/Pages/Configuration.razor" })
        {
            var body = TestSourceScanner.StripNonCode(
                File.ReadAllText(Path.Combine(src, page.Replace('/', Path.DirectorySeparatorChar)))
                    .ReplaceLineEndings("\n"));

            var question = Assert.Single(
                StatementsContaining(body, "ComponentActingUser.IsAllowedAsync(").ToList(),
                statement => true);
            Assert.Contains("GlobalAdmin", question, StringComparison.Ordinal);
            Assert.Contains("CircuitPrincipal", question, StringComparison.Ordinal);

        }

        // The Configuration page renders the notification controls behind the flag and offers the
        // non-admin a GLOBAL/system label instead of the personal control. Markup assertions read the
        // RAW source (the same way the existing UI markup tests do): the C#-oriented scanner treats a
        // prose apostrophe as a char literal, so it cannot be used to look for razor markup.
        var configurationRaw = File.ReadAllText(
            Path.Combine(src, "Components", "Pages", "Configuration.razor"))
            .ReplaceLineEndings("\n");
        Assert.Contains("@if (_composerAdminAllowed)", configurationRaw, StringComparison.Ordinal);
        Assert.Contains("Global / system-wide setting", configurationRaw, StringComparison.Ordinal);
        Assert.Contains("Only an administrator can change", configurationRaw, StringComparison.Ordinal);

        // The ComposerChat toggle is gated the same way, and its non-admin rendering is labelled as a
        // global setting rather than a personal preference.
        var composerChatRaw = File.ReadAllText(
            Path.Combine(src, "Components", "Pages", "ComposerChat.razor"))
            .ReplaceLineEndings("\n");
        Assert.Contains("@if (_notifToggleAdminAllowed)", composerChatRaw, StringComparison.Ordinal);
        Assert.Contains("Notifications (global setting)", composerChatRaw, StringComparison.Ordinal);

        // SINGLETON SAFETY: no SINGLETON registration statement closes over the scoped accessor.
        // Statements are semicolon-delimited so the assertion is scoped to one registration and
        // cannot be satisfied (or broken) by neighbouring text.
        var program = TestSourceScanner.StripNonCode(
            File.ReadAllText(Path.Combine(src, "Program.cs")).ReplaceLineEndings("\n"));
        foreach (var statement in StatementsContaining(program, "AddSingleton"))
        {
            Assert.DoesNotContain("CircuitUserPrincipalAccessor", statement, StringComparison.Ordinal);
        }

        // ...and the accessor's own registration is SCOPED, one instance per circuit.
        var accessorRegistration = Assert.Single(StatementsContaining(program, "CircuitUserPrincipalAccessor("));
        Assert.Contains("AddScoped", accessorRegistration, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every semicolon-delimited STATEMENT containing <paramref name="fragment"/>. Blanked non-code
    /// characters (spaces) keep the split honest: a ';' inside a string literal or comment was already
    /// replaced, so it can never end a statement early.
    /// </summary>
    private static IEnumerable<string> StatementsContaining(string source, string fragment)
    {
        var start = 0;
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] != ';')
                continue;

            var statement = source.Substring(start, i - start + 1);
            if (statement.Contains(fragment, StringComparison.Ordinal))
                yield return statement.Length > 400 ? statement[^400..] : statement;

            start = i + 1;
        }
    }

    /// <summary>
    /// THE COMPOSER TOOL SURFACE IS THE ONLY PLACE THE PRIMITIVES ARE ENFORCED, and the central helper is a
    /// genuine single seam: the capability map, the fresh resolution and the policy evaluation live in ONE
    /// type, which the tool partials consume rather than re-deriving.
    /// </summary>
    [Fact]
    public void TheComposerToolSurface_EnforcesThroughOneCentralHelper()
    {
        var repoRoot = TestRepoRoot.Find();
        var composerRoot = Path.Combine(repoRoot, "src", "CopilotHive", "Orchestration");

        // The helper exists and is where the map lives.
        var helperPath = Path.Combine(repoRoot, "src", "CopilotHive", "Security", "ComposerToolAuthorization.cs");
        Assert.True(File.Exists(helperPath), $"Central helper not found at {helperPath}");
        var helperBody = TestSourceScanner.StripNonCode(File.ReadAllText(helperPath).ReplaceLineEndings("\n"));
        Assert.Contains("HiveCapability", helperBody, StringComparison.Ordinal);
        Assert.Contains("HivePolicy", helperBody, StringComparison.Ordinal);
        Assert.Contains("ResolveByUserIdAsync", helperBody, StringComparison.Ordinal);

        // The mutating tool partials consult the ONE helper's gate, never the resolver/policy directly, so
        // no second, divergent authorization path can appear next to the hook.
        foreach (var file in new[]
        {
            "ComposerGoalTools.cs",
            "ComposerConfigTools.cs",
            "ComposerIssueTools.cs",
            "ComposerKnowledgeTools.cs",
            "ComposerGitTools.cs",
        })
        {
            var body = TestSourceScanner.StripNonCode(
                File.ReadAllText(Path.Combine(composerRoot, file)).ReplaceLineEndings("\n"));

            Assert.DoesNotContain("HivePolicy", body, StringComparison.Ordinal);
            Assert.DoesNotContain("HivePrincipalResolver", body, StringComparison.Ordinal);
            Assert.Contains("AuthorizationDenialAsync", body, StringComparison.Ordinal);
        }
    }
}

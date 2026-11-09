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
    /// ENFORCEMENT STOPS AT THE COMPOSER TOOL SURFACE: the authorization primitives are consumed ONLY by
    /// the Composer tool surface this goal wires (the Composer, its tool partials and its agent service) and
    /// by the new central Composer tool-authorization helper. Every OTHER boundary — endpoints, hubs,
    /// facades, routes, the user directory and the credential entity — still references none of them, so
    /// nothing outside the Composer tools gained enforcement.
    /// <para>
    /// The Composer files are deliberately NOT in the pinned list: wiring them is exactly what the goal
    /// requires. <see cref="UserService"/> and
    /// <see cref="CopilotHive.Persistence.Entities.UserEntity"/> remain untouched contracts the primitives
    /// consume rather than modify, and <c>Program.cs</c> remains the DI registration site.
    /// </para>
    /// </summary>
    [Fact]
    public void BoundariesOutsideTheComposerToolSurface_ReferenceNoneOfTheNewPrimitives()
    {
        var repoRoot = TestRepoRoot.Find();
        var src = Path.Combine(repoRoot, "src", "CopilotHive");

        var boundaries = new[]
        {
            "ApiEndpoints.cs",
            "Orchestration/ConfigHub.cs",
            "Orchestration/ComposerHub.cs",
            "Services/ConfigFacade.cs",
            "Services/GoalFacade.cs",
            "Services/ReleaseFacade.cs",
            "Services/IssueFacade.cs",
            "Services/BackupFacade.cs",
            "Services/ComposerFacade.cs",
            "Services/UserService.cs",
            "Components/Routes.razor",
            "Components/Pages/ComposerChat.razor",
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

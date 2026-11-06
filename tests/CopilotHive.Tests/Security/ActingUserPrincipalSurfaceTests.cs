using System.Reflection;
using System.Runtime.CompilerServices;

using CopilotHive.Security;
using CopilotHive.Services;

namespace CopilotHive.Tests.Security;

/// <summary>
/// THE PUBLIC-SURFACE REGRESSION for <see cref="ActingUserPrincipal"/>: the principal type is public
/// so later boundaries can CONSUME it, but its public API must never be able to CREATE one.
/// <para>
/// The security property pinned here: an activated or local-operator principal may only come from
/// <see cref="HivePrincipalResolver"/> (directory-backed activated-account resolution, or explicit
/// open mode). Both factories are <c>internal</c> and the constructor is private, so no caller
/// outside the production assembly can fabricate an enabled <see cref="UserRole.Admin"/> identity
/// with no directory provenance. If either factory — or any principal-returning public member, or a
/// public constructor, or a publicly writable property — is ever exposed again, these tests fail.
/// </para>
/// <para>
/// These are PUBLIC-SURFACE assertions only (reflection over the type's API): they deliberately do
/// not re-test resolver behaviour or policy outcomes, which are covered by
/// <see cref="HivePrincipalResolverTests"/>, <see cref="HivePolicyTests"/> and
/// <see cref="HiveAuthorizationAcceptanceMatrixTests"/> through <c>InternalsVisibleTo</c>.
/// </para>
/// </summary>
public sealed class ActingUserPrincipalSurfaceTests
{
    /// <summary>The public instance members of the principal type, including inherited ones.</summary>
    private static readonly BindingFlags PublicInstance =
        BindingFlags.Public | BindingFlags.Instance;

    /// <summary>The public instance members of the principal type, including inherited ones.</summary>
    private static readonly BindingFlags PublicInstanceFlat =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

    /// <summary>The public members of the principal type, static and instance, declared or inherited.</summary>
    private static readonly BindingFlags PublicStaticOrInstance =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

    /// <summary>Non-public members, used only to prove the pinned assertions are non-vacuous.</summary>
    private static readonly BindingFlags NonPublic =
        BindingFlags.NonPublic | BindingFlags.Static;

    /// <summary>
    /// Whether a method is the COMPILER-SYNTHESISED record clone (<c>&lt;Clone&gt;$</c>). It copies an
    /// already-resolved instance verbatim, so it cannot mint a principal from chosen values and is
    /// the one principal-returning member legitimately present on the public surface. The exclusion
    /// is deliberately narrow — it requires BOTH the compiler-generated name shape AND the compiler
    /// attribute — so a hand-written member can never hide behind it.
    /// </summary>
    private static bool IsCompilerSynthesisedClone(MethodInfo method) =>
        method.Name.Contains('<', StringComparison.Ordinal)
        && method.GetCustomAttribute<CompilerGeneratedAttribute>() is not null;

    /// <summary>
    /// The type itself STAYS PUBLIC — that is the whole point of the correction: later
    /// endpoint/facade/Composer boundaries must be able to consume a principal even though they can
    /// never build one.
    /// </summary>
    [Fact]
    public void ActingUserPrincipal_RemainsAPublicType()
    {
        Assert.True(typeof(ActingUserPrincipal).IsPublic);
        Assert.True(typeof(ActingUserPrincipal).IsSealed);
    }

    /// <summary>
    /// NO PUBLIC CONSTRUCTOR: every instance constructor is non-public, so a caller cannot create a
    /// principal with chosen role/enabled/id values.
    /// </summary>
    [Fact]
    public void ActingUserPrincipal_ExposesNoPublicConstructor()
    {
        var publicConstructors = typeof(ActingUserPrincipal).GetConstructors(PublicInstance);

        Assert.Empty(publicConstructors);

        // Non-vacuity: the type does have constructors, they are simply all non-public. Without this
        // the empty-public assertion above could pass for a type with no constructors at all.
        Assert.NotEmpty(typeof(ActingUserPrincipal).GetConstructors(
            BindingFlags.NonPublic | BindingFlags.Instance));

        // A public static (type-initializer) constructor is likewise not part of the surface.
        var typeInitializer = typeof(ActingUserPrincipal).TypeInitializer;
        Assert.True(typeInitializer is null || !typeInitializer.IsPublic);
    }

    /// <summary>
    /// NEITHER FACTORY IS PUBLIC: <c>ForActivatedAccount</c> and <c>LocalOperator</c> are named
    /// explicitly because they are the two shapes of forgery that mattered — an enabled activated
    /// account and the full local operator.
    /// </summary>
    /// <param name="factoryName">The factory method that must not be public.</param>
    [Theory]
    [InlineData("ForActivatedAccount")]
    [InlineData("LocalOperator")]
    public void ActingUserPrincipal_ExposesNoPublicFactoryNamed(string factoryName)
    {
        var publicNames = typeof(ActingUserPrincipal)
            .GetMethods(PublicStaticOrInstance)
            .Select(method => method.Name)
            .ToArray();

        Assert.DoesNotContain(factoryName, publicNames, StringComparer.Ordinal);
    }

    /// <summary>
    /// NON-VACUITY FOR THE FACTORY ASSERTIONS: both factories still EXIST and are still reachable
    /// from the production assembly (that is how <see cref="HivePrincipalResolver"/> uses them), but
    /// they are <c>internal</c> — visible to this test assembly only through
    /// <c>InternalsVisibleTo</c>, never through the public API.
    /// </summary>
    /// <param name="factoryName">The factory method expected to exist internally.</param>
    [Theory]
    [InlineData("ForActivatedAccount")]
    [InlineData("LocalOperator")]
    public void ActingUserPrincipal_InternalFactoryStillExistsAndIsNotPublic(string factoryName)
    {
        var internalFactory = typeof(ActingUserPrincipal)
            .GetMethods(NonPublic)
            .SingleOrDefault(method => string.Equals(method.Name, factoryName, StringComparison.Ordinal));

        Assert.NotNull(internalFactory);
        Assert.True(internalFactory.IsAssembly, $"'{factoryName}' must be internal, not public or private.");
        Assert.False(internalFactory.IsPublic);

        // Each is a factory for THIS principal type, so the assertions above pin the real thing.
        Assert.Equal(typeof(ActingUserPrincipal), internalFactory.ReturnType);
    }

    /// <summary>
    /// THE GENERAL FORM OF THE SAME INVARIANT: no public static member of the principal type can
    /// produce a principal at all — under any future name, not just the two pinned above. This is the
    /// failure the correction exists to prevent, so a differently-named public factory fails here too.
    /// Compiler-synthesised record members (special-named, e.g. <c>&lt;Clone&gt;$</c>) are excluded: they
    /// copy an already-resolved instance and cannot mint one from arbitrary values.
    /// </summary>
    [Fact]
    public void ActingUserPrincipal_ExposesNoPublicStaticMemberProducingAPrincipal()
    {
        var principalProducingMembers = typeof(ActingUserPrincipal)
            .GetMethods(PublicStaticOrInstance)
            .Where(method => method.IsStatic)
            .Where(method => !IsCompilerSynthesisedClone(method))
            .Where(method => method.ReturnType == typeof(ActingUserPrincipal))
            .Select(method => method.Name)
            .ToArray();

        Assert.Empty(principalProducingMembers);

        // Non-vacuity: the type DOES expose public static members (the record's equality operators
        // and the LocalOperatorUserId constant), they are simply never principal-producing. The
        // filter above is deliberately narrow — it excludes only the compiler's own clone, so a
        // hand-written public factory cannot hide behind a compiler-generated-looking name.
        var publicStaticMembers = typeof(ActingUserPrincipal)
            .GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .ToArray();

        Assert.NotEmpty(publicStaticMembers);
    }

    /// <summary>
    /// THE INSTANCE COUNTERPART OF THE INVARIANT ABOVE: a public INSTANCE method that returns a
    /// principal is just as forgeable as a public static one, and it is the shape a future
    /// convenience API is most likely to take — for example
    /// <c>public ActingUserPrincipal WithRole(UserRole role)</c>, which would hand any caller an
    /// enabled Administrator by calling one method on a principal it already holds. The static-only
    /// assertion above cannot see such a member, so this one closes that channel.
    /// </summary>
    [Fact]
    public void ActingUserPrincipal_ExposesNoPublicInstanceMemberProducingAPrincipal()
    {
        var principalProducingMembers = typeof(ActingUserPrincipal)
            .GetMethods(PublicInstanceFlat)
            .Where(method => !IsCompilerSynthesisedClone(method))
            .Where(method => method.ReturnType == typeof(ActingUserPrincipal))
            .Select(method => method.Name)
            .ToArray();

        Assert.Empty(principalProducingMembers);

        // Non-vacuity, part 1: the filter is not vacuous because public instance methods DO exist.
        Assert.NotEmpty(typeof(ActingUserPrincipal).GetMethods(PublicInstanceFlat));

        // Non-vacuity, part 2: the ONLY principal-returning instance member is the compiler's own
        // clone, which copies an existing principal verbatim. If even that one disappeared, the
        // assertion above would be passing for the wrong reason, so its presence is pinned too.
        var clone = typeof(ActingUserPrincipal)
            .GetMethods(PublicInstanceFlat)
            .SingleOrDefault(method =>
                IsCompilerSynthesisedClone(method) && method.ReturnType == typeof(ActingUserPrincipal));

        Assert.NotNull(clone);
    }

    /// <summary>
    /// NO PUBLIC MEMBER MAY HOLD A PRINCIPAL: neither a public field nor a public property may be
    /// typed as the principal. A public property returning a principal would be a factory by
    /// another name (a lazily-created one), and the static/instance method assertions above would
    /// not see it.
    /// </summary>
    [Fact]
    public void ActingUserPrincipal_ExposesNoPublicMemberHoldingAPrincipal()
    {
        var principalTypedMembers = typeof(ActingUserPrincipal)
            .GetMembers(PublicStaticOrInstance)
            .Where(member => member switch
            {
                PropertyInfo property => property.PropertyType == typeof(ActingUserPrincipal),
                FieldInfo field => field.FieldType == typeof(ActingUserPrincipal),
                _ => false,
            })
            .Select(member => member.Name)
            .ToArray();

        Assert.Empty(principalTypedMembers);

        // Non-vacuity: the type DOES expose public members (its read-only properties and the
        // LocalOperatorUserId constant), they are simply never principal-typed.
        Assert.NotEmpty(typeof(ActingUserPrincipal).GetMembers(PublicStaticOrInstance));
    }

    /// <summary>
    /// NO PUBLICLY WRITABLE PROPERTY — THE <c>with</c>-EXPRESSION CHANNEL. A record's <c>with</c>
    /// expression is callable from OUTSIDE the declaring assembly (it goes through the compiler's
    /// clone method), but it can only assign a property that has an accessible <c>init</c> or
    /// <c>set</c> accessor. If any property ever became publicly writable, then
    /// <c>principal with { Role = UserRole.Admin }</c> would compile in a later endpoint/facade
    /// assembly and forge an enabled Administrator out of a resolved Viewer principal — a forgery
    /// that neither the factory nor the constructor assertions above can observe.
    /// </summary>
    [Fact]
    public void ActingUserPrincipal_ExposesNoPublicPropertyWithAnExternallyCallableSetter()
    {
        var externallyWritableProperties = typeof(ActingUserPrincipal)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(property => property.SetMethod is not null)
            .Where(property =>
                property.SetMethod!.IsPublic
                || property.SetMethod.IsFamily
                || property.SetMethod.IsFamilyOrAssembly)
            .Select(property => property.Name)
            .ToArray();

        Assert.Empty(externallyWritableProperties);

        // Non-vacuity: the type DOES expose public properties (its six read-only state properties);
        // they are simply all read-only from an external assembly's point of view.
        var publicProperties = typeof(ActingUserPrincipal)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .ToArray();

        Assert.NotEmpty(publicProperties);
        Assert.All(publicProperties, property => Assert.NotEqual(typeof(ActingUserPrincipal), property.PropertyType));
    }

    /// <summary>
    /// THE ASSEMBLY-WIDE FORM OF THE SAME INVARIANT: the restriction must hold for the WHOLE
    /// production assembly's public API, not just for the principal type's own members. A principal
    /// must be reachable from outside the assembly only as something ALREADY RESOLVED — handed over
    /// by <see cref="HivePrincipalResolver"/> through
    /// <see cref="PrincipalResolution.Principal"/>, or consumed by
    /// <see cref="HivePolicy.IsAllowed"/> — never CREATED by a public API.
    /// <para>
    /// So exactly two public members may mention the principal as a result type: the record's
    /// compiler-synthesised clone (which copies an existing instance verbatim) and the read-only
    /// <see cref="PrincipalResolution.Principal"/> accessor (which only READS one). Anything else —
    /// a public factory on another type, a public builder, a public conversion operator — would be a
    /// back door around the two internal factories, and this test names it.
    /// </para>
    /// </summary>
    [Fact]
    public void NoPublicApiInTheProductionAssemblyCanCreateAPrincipal()
    {
        var principalProducers = typeof(ActingUserPrincipal)
            .Assembly
            .GetTypes()
            .Where(type => type.IsPublic || type.IsNestedPublic)
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.ReturnType == typeof(ActingUserPrincipal))
            .Select(method => $"{method.DeclaringType!.FullName}.{method.Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // The compiler-synthesised record clone (copies an existing principal) and the read-only
        // PrincipalResolution.Principal accessor (reads one) are the ONLY permitted mentions.
        Assert.Equal(
            new[]
            {
                "CopilotHive.Security.ActingUserPrincipal.<Clone>$",
                "CopilotHive.Security.PrincipalResolution.get_Principal",
            },
            principalProducers);
    }
}

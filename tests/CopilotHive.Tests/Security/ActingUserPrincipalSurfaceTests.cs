using System.Reflection;

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
/// with no directory provenance. If either factory — or any principal-returning public static
/// member, or a public constructor — is ever made public again, these tests fail.
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

    /// <summary>The public members of the principal type, static and instance, declared or inherited.</summary>
    private static readonly BindingFlags PublicStaticOrInstance =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

    /// <summary>Non-public members, used only to prove the pinned assertions are non-vacuous.</summary>
    private static readonly BindingFlags NonPublic =
        BindingFlags.NonPublic | BindingFlags.Static;

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
            .GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(member => !member.Name.Contains('<', StringComparison.Ordinal))
            .OfType<MethodInfo>()
            .Where(method => method.ReturnType == typeof(ActingUserPrincipal))
            .Select(method => method.Name)
            .ToArray();

        Assert.Empty(principalProducingMembers);
    }
}

using System.Reflection;

using CopilotHive.Configuration;

namespace CopilotHive.Tests.Configuration;

/// <summary>
/// Tests for the relocation of <see cref="ConfigRepoUrlSanitizer"/> from the orchestrator project
/// (<c>CopilotHive</c>) to the shared project (<c>CopilotHive.Shared</c>), made <c>public</c> so both
/// the orchestrator and the worker use the same sanitizer.
/// <para>
/// The namespace stays <c>CopilotHive.Configuration</c> so <c>Program.cs</c> compiles without a
/// reference change. These tests assert the public visibility, the namespace stability, and the
/// assembly placement. The slice-1 behavior is covered by <see cref="ConfigRepoUrlSanitizerTests"/>
/// and must pass UNCHANGED — these tests do not re-assert behavior, only the structural move.
/// </para>
/// <para>
/// Every assertion is removal-proof: if the class were moved back, made internal, or the members
/// made non-public, the exact <see cref="Type.IsPublic"/> and type checks fail.
/// </para>
/// </summary>
public sealed class ConfigRepoUrlSanitizerRelocationTests
{
    private static readonly Type Sanitizer = typeof(ConfigRepoUrlSanitizer);

    // ── Class visibility + assembly placement ─────────────────────────────────

    [Fact]
    public void Type_IsPublic()
    {
        Assert.True(Sanitizer.IsPublic,
            "ConfigRepoUrlSanitizer must be public so CopilotHive.Shared can expose it to the worker.");
    }

    [Fact]
    public void Type_IsInTheSharedAssembly()
    {
        // The assembly name defaults to the project name when no AssemblyName is set.
        Assert.Equal("CopilotHive.Shared", Sanitizer.Assembly.GetName().Name);
    }

    [Fact]
    public void Type_NamespaceIsConfiguration()
    {
        // Namespace stability: Program.cs compiles without a reference change.
        Assert.Equal("CopilotHive.Configuration", Sanitizer.Namespace);
    }

    [Fact]
    public void Type_IsStaticClass()
    {
        Assert.True(Sanitizer.IsAbstract && Sanitizer.IsSealed,
            "ConfigRepoUrlSanitizer is a static class and must remain so.");
    }

    // ── Public entry points ────────────────────────────────────────────────────

    [Fact]
    public void Sanitize_IsPublic()
    {
        var m = Sanitizer.GetMethod("Sanitize", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(m);
        Assert.True(m!.IsPublic, "Sanitize must be public.");
    }

    [Fact]
    public void SanitizeArgs_IsPublic()
    {
        var m = Sanitizer.GetMethod("SanitizeArgs", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(m);
        Assert.True(m!.IsPublic, "SanitizeArgs must be public.");
    }

    [Fact]
    public void NormalizeScpStyle_IsPublic()
    {
        var m = Sanitizer.GetMethod("NormalizeScpStyle", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(m);
        Assert.True(m!.IsPublic, "NormalizeScpStyle must be public.");
    }

    // ── Rejected exception types ───────────────────────────────────────────────

    [Fact]
    public void RejectedException_IsPublicNestedType()
    {
        var rejected = Sanitizer.GetNestedType("RejectedException", BindingFlags.Public);
        Assert.NotNull(rejected);
        Assert.True(rejected!.IsNestedPublic, "RejectedException must be a public nested type.");
    }

    [Fact]
    public void RejectedException_InheritsArgumentException()
    {
        var rejected = Sanitizer.GetNestedType("RejectedException", BindingFlags.Public);
        Assert.NotNull(rejected);
        Assert.True(typeof(ArgumentException).IsAssignableFrom(rejected),
            "RejectedException must derive from ArgumentException.");
    }
}
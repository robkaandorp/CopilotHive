using System.Security.Claims;

using CopilotHive.Components.Layout;

namespace CopilotHive.Tests;

/// <summary>
/// Tests the avatar fragment emitted by <see cref="NavMenu"/> through the same production-owned
/// rendering helper that the component invokes. Only claim and username input extraction is
/// performed here; no test-local code computes either the render decision or fallback initial.
/// </summary>
public sealed class NavMenuAvatarFallbackTests
{
    private const string AvatarClaimType = "urn:github:avatar";

    private static NavMenu.NavUserAvatarRender RenderAvatar(ClaimsPrincipal user) =>
        NavMenu.ComputeNavUserAvatar(user.FindFirst(AvatarClaimType)?.Value, user.Identity?.Name);

    private static ClaimsPrincipal CreateUser(string? username, params (string Type, string Value)[] claims)
    {
        var identity = new ClaimsIdentity(
            authenticationType: username is null ? null : "Test.GitHub",
            nameType: username is null ? null : ClaimTypes.Name);

        if (username is not null)
        {
            identity.AddClaim(new Claim(ClaimTypes.Name, username));
        }

        foreach (var (type, value) in claims)
        {
            identity.AddClaim(new Claim(type, value));
        }

        return new ClaimsPrincipal(identity);
    }

    // ── avatar present → image with the claim URL ─────────────────────────────

    [Fact]
    public void NavUserAvatar_WithNonEmptyAvatarClaim_RendersImgWithClaimUrl()
    {
        var result = RenderAvatar(CreateUser(
            "octo",
            (AvatarClaimType, "https://avatars.githubusercontent.com/u/1")));

        Assert.True(result.HasAvatar);
        Assert.Equal("<img class=\"nav-user-avatar\" src=\"https://avatars.githubusercontent.com/u/1\" alt=\"avatar\" />", result.Markup);
        Assert.DoesNotContain("nav-user-avatar-fallback", result.Markup);
    }

    [Fact]
    public void NavUserAvatar_WithAvatarClaim_EscapesUrlInEmittedImage()
    {
        // NavMenu casts this fragment to MarkupString, so an attribute delimiter must be encoded.
        var result = RenderAvatar(CreateUser("octo", (AvatarClaimType, "https://example.test/u/1?size=48&format=png\"x")));

        Assert.True(result.HasAvatar);
        Assert.Equal("<img class=\"nav-user-avatar\" src=\"https://example.test/u/1?size=48&amp;format=png&quot;x\" alt=\"avatar\" />", result.Markup);
    }

    // ── avatar missing/empty → fallback element, no broken image ──────────────

    [Fact]
    public void NavUserAvatar_WithMissingAvatarClaim_RendersFallbackInitialAndNoImage()
    {
        var result = RenderAvatar(CreateUser("octo"));

        Assert.False(result.HasAvatar);
        Assert.Equal("<span class=\"nav-user-avatar nav-user-avatar-fallback\">O</span>", result.Markup);
        Assert.DoesNotContain("<img", result.Markup);
        Assert.DoesNotContain("src=", result.Markup);
    }

    [Fact]
    public void NavUserAvatar_WithEmptyAvatarClaim_RendersFallbackInitialAndNoImage()
    {
        var result = RenderAvatar(CreateUser("octo", (AvatarClaimType, "")));

        Assert.False(result.HasAvatar);
        Assert.Equal("<span class=\"nav-user-avatar nav-user-avatar-fallback\">O</span>", result.Markup);
        Assert.DoesNotContain("<img", result.Markup);
        Assert.DoesNotContain("src=", result.Markup);
    }

    [Fact]
    public void NavUserAvatar_WithWhitespaceAvatarClaim_RendersFallbackInitialAndNoImage()
    {
        var result = RenderAvatar(CreateUser("octo", (AvatarClaimType, "   ")));

        Assert.False(result.HasAvatar);
        Assert.Equal("<span class=\"nav-user-avatar nav-user-avatar-fallback\">O</span>", result.Markup);
        Assert.DoesNotContain("<img", result.Markup);
        Assert.DoesNotContain("src=", result.Markup);
    }

    // ── no username either → neutral "?" placeholder, never a broken image ────

    [Fact]
    public void NavUserAvatar_WithMissingUsernameAndNoAvatarClaim_RendersNeutralQuestionMarkPlaceholder()
    {
        var result = RenderAvatar(CreateUser(username: null));

        Assert.False(result.HasAvatar);
        Assert.Equal("<span class=\"nav-user-avatar nav-user-avatar-fallback\">?</span>", result.Markup);
        Assert.DoesNotContain("<img", result.Markup);
        Assert.DoesNotContain("src=", result.Markup);
    }

    [Fact]
    public void NavUserAvatar_WithBlankUsernameAndNoAvatarClaim_RendersNeutralQuestionMarkPlaceholder()
    {
        var result = RenderAvatar(CreateUser("   "));

        Assert.False(result.HasAvatar);
        Assert.Equal("<span class=\"nav-user-avatar nav-user-avatar-fallback\">?</span>", result.Markup);
        Assert.DoesNotContain("<img", result.Markup);
        Assert.DoesNotContain("src=", result.Markup);
    }

    // ── fallback initial derivation ───────────────────────────────────────────

    [Theory]
    [InlineData("octo", "O")]
    [InlineData("Alice", "A")]
    [InlineData("123", "1")]
    [InlineData(" octo", "O")]
    [InlineData(null, "?")]
    [InlineData("", "?")]
    [InlineData("   ", "?")]
    public void AvatarFallbackInitial_UsesUppercasedTrimmedFirstCharacterOrQuestionMark(string? username, string expected)
    {
        Assert.Equal(expected, NavMenu.AvatarFallbackInitial(username));
        var result = NavMenu.ComputeNavUserAvatar(avatarUrl: null, username);
        Assert.False(result.HasAvatar);
        Assert.Equal($"<span class=\"nav-user-avatar nav-user-avatar-fallback\">{expected}</span>", result.Markup);
    }

    [Fact]
    public void NavUserAvatar_WithLowercaseUsername_RendersUppercaseInitial()
    {
        var result = RenderAvatar(CreateUser("octo"));

        Assert.False(result.HasAvatar);
        Assert.Equal("<span class=\"nav-user-avatar nav-user-avatar-fallback\">O</span>", result.Markup);
        Assert.DoesNotContain(">o<", result.Markup);
    }
}
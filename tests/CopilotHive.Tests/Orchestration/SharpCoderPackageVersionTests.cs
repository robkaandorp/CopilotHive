namespace CopilotHive.Tests.Orchestration;

/// <summary>
/// Verifies the API-availability-surface contract of the pinned SharpCoder.Providers assembly:
/// the restored package must actually expose <c>ChatClientFactory.IsTokenAvailable</c> — the
/// single source of truth for GitHub Copilot token availability used by
/// <see cref="CopilotHive.Orchestration.LlmConnectionCoordinator"/> to gate the Composer's
/// startup connect. A version bump that silently dropped this API must fail here.
/// </summary>
public sealed class SharpCoderPackageVersionTests
{
    /// <summary>
    /// The restored <c>SharpCoder.Providers</c> assembly actually exposes the availability API the
    /// coordinator depends on — a version bump that silently lost it must fail here.
    /// </summary>
    [Fact]
    public void ChatClientFactory_ExposesIsTokenAvailable()
    {
        var method = typeof(SharpCoder.Providers.ChatClientFactory)
            .GetMethod("IsTokenAvailable", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, Type.EmptyTypes);

        Assert.NotNull(method);
        Assert.Equal(typeof(bool), method!.ReturnType);
    }
}
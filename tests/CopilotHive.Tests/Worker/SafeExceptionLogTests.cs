using CopilotHive.Worker;

using Grpc.Core;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Tests for <see cref="SafeExceptionLog"/> — the secret-free exception renderer used by
/// worker retry, recovery and fatal log paths. It must NEVER emit an exception message,
/// stack trace, or response body: only type names and protocol status codes.
/// </summary>
public sealed class SafeExceptionLogTests
{
    // ── Null handling ─────────────────────────────────────────────────────────

    [Fact]
    public void Describe_Null_ReturnsNoneDescription()
    {
        Assert.Equal(SafeExceptionLog.NoneDescription, SafeExceptionLog.Describe(null));
    }

    // ── RpcException: status code only, never message ──────────────────────────

    [Fact]
    public void Describe_RpcException_ContainsStatusButNeverMessage()
    {
        const string Secret = "ghp_supersecret_token_in_message";
        var status = new Status(StatusCode.Unavailable, Secret);
        var ex = new RpcException(status, message: Secret);

        var rendered = SafeExceptionLog.Describe(ex);

        Assert.Contains("RpcException", rendered);
        Assert.Contains("Unavailable", rendered);
        Assert.DoesNotContain(Secret, rendered);
    }

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.PermissionDenied)]
    public void Describe_RpcException_ContainsStatusCode(StatusCode code)
    {
        var ex = new RpcException(new Status(code, "detail-that-must-not-appear"));

        var rendered = SafeExceptionLog.Describe(ex);

        Assert.Contains(code.ToString(), rendered);
        Assert.DoesNotContain("detail-that-must-not-appear", rendered);
    }

    // ── HttpRequestException: status code only, never message ──────────────────

    [Fact]
    public void Describe_HttpRequestExceptionWithStatus_ContainsHttpStatusButNeverMessage()
    {
        const string Secret = "ollama-key-in-message";
        var inner = new InvalidOperationException(Secret);
        var ex = new HttpRequestException(Secret, inner, System.Net.HttpStatusCode.Unauthorized);

        var rendered = SafeExceptionLog.Describe(ex);

        Assert.Contains("HttpRequestException", rendered);
        Assert.Contains("401", rendered);
        Assert.DoesNotContain(Secret, rendered);
    }

    [Fact]
    public void Describe_HttpRequestExceptionWithoutStatusCode_ContainsTypeOnly()
    {
        const string Secret = "ghp_token_in_http_message";
        var ex = new HttpRequestException(Secret);

        var rendered = SafeExceptionLog.Describe(ex);

        Assert.Contains("HttpRequestException", rendered);
        Assert.DoesNotContain(Secret, rendered);
        // No httpStatus= segment when StatusCode is null
        Assert.DoesNotContain("httpStatus=", rendered);
    }

    // ── Inner-exception chain: type names only ─────────────────────────────────

    [Fact]
    public void Describe_InnerExceptionChain_NeverEmitsAnyMessage()
    {
        const string OuterSecret = "ghp_outer_secret";
        const string InnerSecret = "ollama_inner_key";
        var inner = new InvalidOperationException(InnerSecret);
        var outer = new Exception(OuterSecret, inner);

        var rendered = SafeExceptionLog.Describe(outer);

        Assert.Contains("Exception", rendered);
        Assert.Contains("InvalidOperationException", rendered);
        Assert.Contains(" <- ", rendered);
        Assert.DoesNotContain(OuterSecret, rendered);
        Assert.DoesNotContain(InnerSecret, rendered);
    }

    [Fact]
    public void Describe_DeepInnerChain_RendersUpToMaxDepth()
    {
        // Build a chain deeper than MaxChainDepth to verify it still renders safely
        const string Secret = "secret_at_every_level";
        Exception deepest = new InvalidOperationException(Secret);
        for (var i = 0; i < 5; i++)
            deepest = new InvalidOperationException(Secret, deepest);

        var rendered = SafeExceptionLog.Describe(deepest);

        // Must contain the join separator for the chain, and never the secret
        Assert.Contains(" <- ", rendered);
        Assert.DoesNotContain(Secret, rendered);
    }

    // ── Generic exception: type name only ──────────────────────────────────────

    [Fact]
    public void Describe_GenericException_ContainsTypeNameOnly()
    {
        const string Secret = "token_value_in_generic_exception";
        var ex = new InvalidOperationException(Secret);

        var rendered = SafeExceptionLog.Describe(ex);

        Assert.Contains("InvalidOperationException", rendered);
        Assert.DoesNotContain(Secret, rendered);
    }

    // ── Composite: a provisioned secret in ex.Message must never survive ───────

    [Fact]
    public void Describe_ProvisionedSecretInExceptionMessage_NeverRendered()
    {
        const string ProvisionedToken = "ghp_provisioned_by_orchestrator_abc123";
        const string ProvisionedApiKey = "ollama-cloud-key-xyz789";

        // Simulate the real scenario: an exception whose message quotes a provisioned value
        var rpcEx = new RpcException(
            new Status(StatusCode.Unauthenticated, $"token={ProvisionedToken} key={ProvisionedApiKey}"));

        var rendered = SafeExceptionLog.Describe(rpcEx);

        Assert.DoesNotContain(ProvisionedToken, rendered);
        Assert.DoesNotContain(ProvisionedApiKey, rendered);
    }
}
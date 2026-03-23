extern alias WorkerAssembly;

using WorkerAssembly::CopilotHive.SDK;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Unit tests for <see cref="ChatClientFactory.ParseProviderAndModel"/>: verifies
/// that provider prefixes are extracted correctly for every known provider, that
/// plain model names and edge-case inputs are handled without throwing, and that
/// the returned tuple always contains the exact expected provider and model values.
/// </summary>
public sealed class ChatClientFactoryTests
{
    // ── Known provider prefixes ──────────────────────────────────────────────

    #region copilot/ prefix — extracts "copilot" provider and bare model name

    /// <summary>
    /// When the model string begins with the "copilot/" prefix,
    /// <see cref="ChatClientFactory.ParseProviderAndModel"/> must return
    /// provider "copilot" and the model name that follows the slash.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_CopilotPrefix_ReturnsCopilotProviderAndModel()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("copilot/claude-sonnet-4.6");

        Assert.Equal("copilot", provider);
        Assert.Equal("claude-sonnet-4.6", model);
    }

    #endregion

    #region ollama-cloud/ prefix — extracts "ollama-cloud" provider and bare model name

    /// <summary>
    /// When the model string begins with the "ollama-cloud/" prefix,
    /// <see cref="ChatClientFactory.ParseProviderAndModel"/> must return
    /// provider "ollama-cloud" and the model name that follows the slash.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_OllamaCloudPrefix_ReturnsOllamaCloudProviderAndModel()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("ollama-cloud/gpt-oss:120b");

        Assert.Equal("ollama-cloud", provider);
        Assert.Equal("gpt-oss:120b", model);
    }

    #endregion

    #region ollama-local/ prefix — extracts "ollama-local" provider and bare model name

    /// <summary>
    /// When the model string begins with the "ollama-local/" prefix,
    /// <see cref="ChatClientFactory.ParseProviderAndModel"/> must return
    /// provider "ollama-local" and the model name that follows the slash.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_OllamaLocalPrefix_ReturnsOllamaLocalProviderAndModel()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("ollama-local/llama3");

        Assert.Equal("ollama-local", provider);
        Assert.Equal("llama3", model);
    }

    #endregion

    #region github/ prefix — extracts "github" provider and bare model name

    /// <summary>
    /// When the model string begins with the "github/" prefix,
    /// <see cref="ChatClientFactory.ParseProviderAndModel"/> must return
    /// provider "github" and the model name that follows the slash.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_GithubPrefix_ReturnsGithubProviderAndModel()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("github/openai/gpt-4.1");

        Assert.Equal("github", provider);
        Assert.Equal("openai/gpt-4.1", model);
    }

    #endregion

    // ── No prefix / plain model name ────────────────────────────────────────

    #region No prefix — returns default provider and the full model string as model

    /// <summary>
    /// When the model string contains no recognised provider prefix (no slash,
    /// or an unrecognised prefix), <see cref="ChatClientFactory.ParseProviderAndModel"/>
    /// must return the default provider from the <c>LLM_PROVIDER</c> environment
    /// variable (defaulting to "copilot") and the original model string as the model.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_PlainModelName_ReturnsDefaultProviderAndFullModelName()
    {
        var expectedProvider = Environment.GetEnvironmentVariable("LLM_PROVIDER")?.ToLowerInvariant() ?? "copilot";

        var (provider, model) = ChatClientFactory.ParseProviderAndModel("gpt-4o");

        Assert.Equal(expectedProvider, provider);
        Assert.Equal("gpt-4o", model);
    }

    #endregion

    // ── Edge cases ───────────────────────────────────────────────────────────

    #region Empty string — returns default provider and null model without throwing

    /// <summary>
    /// When the input is an empty string, <see cref="ChatClientFactory.ParseProviderAndModel"/>
    /// must not throw and must return the default provider (from <c>LLM_PROVIDER</c> or
    /// "copilot") with a <see langword="null"/> model, identical to the behaviour for
    /// a <see langword="null"/> input.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_EmptyString_ReturnsDefaultProviderAndNullModel()
    {
        var expectedProvider = Environment.GetEnvironmentVariable("LLM_PROVIDER")?.ToLowerInvariant() ?? "copilot";

        var (provider, model) = ChatClientFactory.ParseProviderAndModel(string.Empty);

        Assert.Equal(expectedProvider, provider);
        Assert.Null(model);
    }

    #endregion

    #region Prefix with no model after slash — returns known provider and empty model string

    /// <summary>
    /// When the input is a known provider prefix followed immediately by a slash but no
    /// model name (e.g. "copilot/"), <see cref="ChatClientFactory.ParseProviderAndModel"/>
    /// must extract the provider correctly and return an empty string as the model, because
    /// <c>Substring(slashIndex + 1)</c> on "copilot/" yields "".
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_KnownPrefixWithNoModel_ReturnsProviderAndEmptyModel()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("copilot/");

        Assert.Equal("copilot", provider);
        Assert.Equal(string.Empty, model);
    }

    #endregion

    #region Double slash — returns known provider and the remainder including leading slash

    /// <summary>
    /// When the input contains a double slash (e.g. "copilot//model"), the first slash is
    /// used to split off the prefix. <see cref="ChatClientFactory.ParseProviderAndModel"/>
    /// must return the known provider and the remainder after the first slash as the model,
    /// which will begin with an additional slash character.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_DoubleSlash_ReturnsProviderAndRemainderAfterFirstSlash()
    {
        var (provider, model) = ChatClientFactory.ParseProviderAndModel("copilot//model");

        Assert.Equal("copilot", provider);
        Assert.Equal("/model", model);
    }

    #endregion

    // ── Null input ───────────────────────────────────────────────────────────

    #region Null input — returns default provider and null model without throwing

    /// <summary>
    /// When the input is <see langword="null"/>, <see cref="ChatClientFactory.ParseProviderAndModel"/>
    /// must not throw and must return the default provider (from <c>LLM_PROVIDER</c> or
    /// "copilot") paired with a <see langword="null"/> model.
    /// </summary>
    [Fact]
    public void ParseProviderAndModel_NullInput_ReturnsDefaultProviderAndNullModel()
    {
        var expectedProvider = Environment.GetEnvironmentVariable("LLM_PROVIDER")?.ToLowerInvariant() ?? "copilot";

        var (provider, model) = ChatClientFactory.ParseProviderAndModel(null);

        Assert.Equal(expectedProvider, provider);
        Assert.Null(model);
    }

    #endregion
}

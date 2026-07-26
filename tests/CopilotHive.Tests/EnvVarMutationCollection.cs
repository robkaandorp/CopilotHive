using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Serializes tests that mutate process-wide environment variables (e.g. GH_TOKEN /
/// GITHUB_TOKEN) so they cannot corrupt the environment observed by parallel tests.
/// </summary>
[CollectionDefinition("EnvVarMutation", DisableParallelization = true)]
public sealed class EnvVarMutationCollection { }

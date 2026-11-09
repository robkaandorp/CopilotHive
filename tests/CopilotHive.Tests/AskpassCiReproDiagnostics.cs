using CopilotHive.Git;

namespace CopilotHive.Tests;

/// <summary>
/// CI-repro diagnostic (evidence capture only, no assertions about git behaviour): when
/// <c>HIVE_ASKPASS_DIAG_DIR</c> is set, exports the production helper script and the exact
/// <c>BuildChildEnvironment</c> output (synthetic token) so an external harness can replay real
/// <c>git credential fill</c> under any git version. Without the variable it does nothing.
/// </summary>
[Collection("EnvVarMutation")]
public sealed class AskpassCiReproDiagnostics
{
    private const string SyntheticToken = "ghp_host_askpass_secret";

    [Fact]
    public void ExportProductionHelperAndChildEnvironment_WhenRequested()
    {
        var dir = Environment.GetEnvironmentVariable("HIVE_ASKPASS_DIAG_DIR");
        if (string.IsNullOrEmpty(dir)) return;

        Directory.CreateDirectory(dir);
        using var helper = HostGitAskpassHelper.TryCreate();
        Assert.NotNull(helper);
        File.Copy(helper.ScriptPath, Path.Combine(dir, "production-askpass.sh"), overwrite: true);
        var env = helper.BuildChildEnvironment(SyntheticToken);
        var lines = new List<string>();
        foreach (var (key, value) in env.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (value is not null && System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z_][A-Za-z0-9_]*$"))
                lines.Add($"export {key}='{value.Replace("'", "'\\''")}'");
        File.WriteAllLines(Path.Combine(dir, "child-env.sh"), lines);
        File.WriteAllText(Path.Combine(dir, "helper-path.txt"), helper.ScriptPath);
    }
}

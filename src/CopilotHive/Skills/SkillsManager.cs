namespace CopilotHive.Skills;

/// <summary>
/// Manages skill files on the local filesystem. Skills define framework-specific
/// commands (build, test, install-sdk) that are injected into worker prompts instead
/// of hardcoding tool names like "dotnet" or "xUnit".
/// </summary>
public sealed class SkillsManager
{
    private readonly string _skillsPath;

    /// <summary>
    /// Well-known skill names used by the orchestrator.
    /// </summary>
    public static class Names
    {
        /// <summary>Build skill — how to compile the project.</summary>
        public const string Build = "build";
        /// <summary>Test skill — how to run tests and read results.</summary>
        public const string Test = "test";
        /// <summary>Install SDK skill — how to install the language runtime.</summary>
        public const string InstallSdk = "install-sdk";
    }

    /// <summary>
    /// Initialises a new <see cref="SkillsManager"/> rooted at the given path.
    /// </summary>
    /// <param name="skillsPath">Root directory where skill .md files are stored.</param>
    public SkillsManager(string skillsPath)
    {
        _skillsPath = Path.GetFullPath(skillsPath);
    }

    /// <summary>
    /// Returns the full path to the skill file for the given skill name.
    /// </summary>
    public string GetSkillPath(string skillName) =>
        Path.Combine(_skillsPath, $"{skillName}.md");

    /// <summary>
    /// Reads and returns the skill content for the specified skill name.
    /// Returns <c>null</c> when the skill file does not exist.
    /// </summary>
    public string? GetSkill(string skillName)
    {
        var path = GetSkillPath(skillName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>
    /// Returns the names of all available skills (without extension).
    /// </summary>
    public string[] ListSkills()
    {
        if (!Directory.Exists(_skillsPath))
            return [];

        return Directory.GetFiles(_skillsPath, "*.md")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n)
            .ToArray();
    }

    /// <summary>
    /// Builds a prompt fragment that tells a worker how to build and test,
    /// using the skill files if available.
    /// </summary>
    public string GetBuildAndTestInstructions()
    {
        var build = GetSkill(Names.Build);
        var test = GetSkill(Names.Test);

        if (build is null && test is null)
            return "Build the project and run the tests. Check for a build skill or test skill in the skills/ directory.";

        var parts = new List<string>();
        if (build is not null)
            parts.Add($"To build the project, follow these instructions:\n{build}");
        if (test is not null)
            parts.Add($"To run the tests, follow these instructions:\n{test}");

        return string.Join("\n\n", parts);
    }
}

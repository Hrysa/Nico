using Editor;
using Xunit;

namespace Editor.Tests;

public class ProjectSolutionScaffolderTests
{
    /// <summary>Verifies the editor creates a solution containing a buildable script project.</summary>
    [Fact]
    public void Ensure_MissingWorkspace_CreatesSolutionAndScriptProject()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var engineAssemblyPath = Path.Combine(directory, "engine", "Engine.Core.dll");

            var workspace = ProjectSolutionScaffolder.Ensure(directory, engineAssemblyPath);

            Assert.True(File.Exists(workspace.SolutionPath));
            Assert.True(File.Exists(workspace.ScriptProjectPath));
            Assert.Contains("Scripts/", File.ReadAllText(workspace.SolutionPath));
            var projectContents = File.ReadAllText(workspace.ScriptProjectPath);
            Assert.Contains(Path.GetFullPath(engineAssemblyPath), projectContents);
            Assert.Contains("Engine.Graphics.dll", projectContents);
            Assert.Contains("Engine.Scripting.dll", projectContents);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>Verifies reopening preserves custom files that are not valid generated projects.</summary>
    [Fact]
    public void Ensure_ExistingWorkspace_PreservesFiles()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var first = ProjectSolutionScaffolder.Ensure(directory, Path.Combine(directory, "Engine.Core.dll"));
            File.WriteAllText(first.SolutionPath, "custom solution");
            File.WriteAllText(first.ScriptProjectPath, "custom project");

            var second = ProjectSolutionScaffolder.Ensure(directory, Path.Combine(directory, "other", "Engine.Core.dll"));

            Assert.Equal("custom solution", File.ReadAllText(second.SolutionPath));
            Assert.Equal("custom project", File.ReadAllText(second.ScriptProjectPath));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>Verifies reopening upgrades engine references while preserving user project properties.</summary>
    [Fact]
    public void Ensure_ExistingScriptProject_RefreshesEngineReferences()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var first = ProjectSolutionScaffolder.Ensure(directory,
                Path.Combine(directory, "old", "Engine.Core.dll"));
            var contents = File.ReadAllText(first.ScriptProjectPath)
                .Replace("</PropertyGroup>", "  <LangVersion>preview</LangVersion>\n  </PropertyGroup>");
            File.WriteAllText(first.ScriptProjectPath, contents);

            ProjectSolutionScaffolder.Ensure(directory,
                Path.Combine(directory, "new", "Engine.Core.dll"));

            var upgraded = File.ReadAllText(first.ScriptProjectPath);
            Assert.Contains("<LangVersion>preview</LangVersion>", upgraded);
            Assert.Contains(Path.Combine(directory, "new", "Engine.Scripting.dll"), upgraded);
            Assert.Contains("Engine.Graphics", upgraded);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Creates an isolated game-project directory for a test.
    /// </summary>
    /// <returns>The absolute temporary directory path.</returns>
    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"editor-solution-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}

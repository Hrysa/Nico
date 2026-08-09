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
            Assert.DoesNotContain(Path.GetFullPath(engineAssemblyPath), projectContents);
            Assert.DoesNotContain("HintPath", projectContents);
            var userContents = File.ReadAllText(workspace.ScriptProjectPath + ".user");
            Assert.Contains(Path.GetFullPath(engineAssemblyPath), userContents);
            Assert.Contains("Engine.Graphics.dll", userContents);
            Assert.Contains("Engine.Scripting.dll", userContents);
            Assert.Contains("Engine.UI.dll", userContents);
            Assert.Contains("Engine.Script.Generator.dll", userContents);
            Assert.Contains("<Analyzer", userContents);
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

    /// <summary>Verifies reopening refreshes local references without changing the committed project.</summary>
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
            File.WriteAllText(first.ScriptProjectPath + ".user", """
                <Project>
                  <PropertyGroup>
                    <UserSetting>preserved</UserSetting>
                  </PropertyGroup>
                </Project>
                """);

            ProjectSolutionScaffolder.Ensure(directory,
                Path.Combine(directory, "new", "Engine.Core.dll"));

            var upgraded = File.ReadAllText(first.ScriptProjectPath);
            Assert.Contains("<LangVersion>preview</LangVersion>", upgraded);
            Assert.DoesNotContain("Engine.Scripting", upgraded);
            var userContents = File.ReadAllText(first.ScriptProjectPath + ".user");
            Assert.Contains("<UserSetting>preserved</UserSetting>", userContents);
            Assert.Contains(Path.Combine(directory, "new", "Engine.Scripting.dll"), userContents);
            Assert.Contains("Engine.Graphics", userContents);
            Assert.Contains("Engine.UI", userContents);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>Verifies legacy absolute references migrate out of the committed project.</summary>
    [Fact]
    public void Ensure_LegacyEngineReferences_MovesThemToUserProject()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var first = ProjectSolutionScaffolder.Ensure(directory,
                Path.Combine(directory, "old", "Engine.Core.dll"));
            File.WriteAllText(first.ScriptProjectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Reference Include="Engine.Core"><HintPath>C:/old/Engine.Core.dll</HintPath></Reference>
                    <Reference Include="User.Library"><HintPath>lib/User.Library.dll</HintPath></Reference>
                  </ItemGroup>
                </Project>
                """);

            ProjectSolutionScaffolder.Ensure(directory,
                Path.Combine(directory, "new", "Engine.Core.dll"));

            var projectContents = File.ReadAllText(first.ScriptProjectPath);
            Assert.DoesNotContain("C:/old/Engine.Core.dll", projectContents);
            Assert.Contains("User.Library", projectContents);
            var userContents = File.ReadAllText(first.ScriptProjectPath + ".user");
            Assert.Contains(Path.Combine(directory, "new", "Engine.Core.dll"), userContents);
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

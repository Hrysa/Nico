using System.Security;
using System.Xml.Linq;

namespace Editor;

/// <summary>
/// Creates the .NET solution and script project owned by a game project.
/// </summary>
public static class ProjectSolutionScaffolder
{
    /// <summary>
    /// Creates missing scripting workspace files and refreshes engine-managed references.
    /// </summary>
    /// <param name="projectRoot">Absolute game-project root.</param>
    /// <param name="engineCoreAssemblyPath">Path to the Engine.Core assembly beside the other scripting API assemblies.</param>
    /// <returns>The paths that identify the project's scripting workspace.</returns>
    public static ScriptingWorkspace Ensure(string projectRoot, string engineCoreAssemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineCoreAssemblyPath);

        var projectName = CreateProjectName(projectRoot);
        var scriptsDirectory = Path.Combine(projectRoot, "Scripts");
        var scriptProjectPath = Path.Combine(scriptsDirectory, $"{projectName}.Scripts.csproj");
        var solutionPath = Path.Combine(projectRoot, $"{projectName}.slnx");

        Directory.CreateDirectory(scriptsDirectory);
        if (!File.Exists(scriptProjectPath))
            File.WriteAllText(scriptProjectPath, CreateScriptProject(engineCoreAssemblyPath));
        else
            RefreshEngineReferences(scriptProjectPath, engineCoreAssemblyPath);
        if (!File.Exists(solutionPath))
            File.WriteAllText(solutionPath, CreateSolution(projectName));

        return new ScriptingWorkspace(solutionPath, scriptProjectPath);
    }

    /// <summary>
    /// Produces a filesystem-safe project name from the game-project directory.
    /// </summary>
    /// <param name="projectRoot">Game-project root.</param>
    /// <returns>A non-empty filesystem-safe project name.</returns>
    private static string CreateProjectName(string projectRoot)
    {
        var directoryName = new DirectoryInfo(Path.GetFullPath(projectRoot)).Name;
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(directoryName
            .Select(character => invalidCharacters.Contains(character) ? '-' : character)
            .ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Game" : sanitized;
    }

    /// <summary>
    /// Creates the XML for the game script class-library project.
    /// </summary>
    /// <param name="engineCoreAssemblyPath">Path to Engine.Core beside the other scripting API assemblies.</param>
    /// <returns>The script project XML.</returns>
    private static string CreateScriptProject(string engineCoreAssemblyPath)
    {
        var assemblyDirectory = Path.GetDirectoryName(Path.GetFullPath(engineCoreAssemblyPath))
            ?? throw new ArgumentException("The engine assembly path has no parent directory.",
                nameof(engineCoreAssemblyPath));
        var corePath = SecurityElement.Escape(Path.Combine(assemblyDirectory, "Engine.Core.dll"));
        var graphicsPath = SecurityElement.Escape(Path.Combine(assemblyDirectory, "Engine.Graphics.dll"));
        var scriptingPath = SecurityElement.Escape(Path.Combine(assemblyDirectory, "Engine.Scripting.dll"));
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net11.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="Engine.Core">
                  <HintPath>{corePath}</HintPath>
                  <Private>false</Private>
                </Reference>
                <Reference Include="Engine.Graphics">
                  <HintPath>{graphicsPath}</HintPath>
                  <Private>false</Private>
                </Reference>
                <Reference Include="Engine.Scripting">
                  <HintPath>{scriptingPath}</HintPath>
                  <Private>false</Private>
                </Reference>
              </ItemGroup>
            </Project>
            """;
    }

    /// <summary>
    /// Updates only engine-managed references in an existing generated script project.
    /// </summary>
    /// <param name="scriptProjectPath">Existing script project path.</param>
    /// <param name="engineCoreAssemblyPath">Path to Engine.Core beside the other scripting API assemblies.</param>
    private static void RefreshEngineReferences(
        string scriptProjectPath,
        string engineCoreAssemblyPath)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(scriptProjectPath, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            return;
        }
        var project = document.Root;
        if (project?.Name.LocalName != "Project")
            return;
        var assemblyDirectory = Path.GetDirectoryName(Path.GetFullPath(engineCoreAssemblyPath))
            ?? throw new ArgumentException("The engine assembly path has no parent directory.",
                nameof(engineCoreAssemblyPath));
        var itemGroup = project.Elements().FirstOrDefault(element =>
            element.Name.LocalName == "ItemGroup"
            && element.Elements().Any(child => child.Name.LocalName == "Reference"));
        if (itemGroup is null)
        {
            itemGroup = new XElement("ItemGroup");
            project.Add(itemGroup);
        }
        EnsureReference(itemGroup, "Engine.Core", Path.Combine(assemblyDirectory, "Engine.Core.dll"));
        EnsureReference(itemGroup, "Engine.Graphics",
            Path.Combine(assemblyDirectory, "Engine.Graphics.dll"));
        EnsureReference(itemGroup, "Engine.Scripting",
            Path.Combine(assemblyDirectory, "Engine.Scripting.dll"));
        document.Save(scriptProjectPath);
    }

    /// <summary>
    /// Adds or updates one engine assembly reference in a script project item group.
    /// </summary>
    /// <param name="itemGroup">Item group that owns engine references.</param>
    /// <param name="assemblyName">Referenced assembly name.</param>
    /// <param name="assemblyPath">Current absolute assembly path.</param>
    private static void EnsureReference(XElement itemGroup, string assemblyName, string assemblyPath)
    {
        var reference = itemGroup.Elements().FirstOrDefault(element =>
            element.Name.LocalName == "Reference"
            && string.Equals((string?)element.Attribute("Include"), assemblyName,
                StringComparison.Ordinal));
        if (reference is null)
        {
            reference = new XElement("Reference", new XAttribute("Include", assemblyName));
            itemGroup.Add(reference);
        }
        var hintPath = reference.Elements().FirstOrDefault(element =>
            element.Name.LocalName == "HintPath");
        if (hintPath is null)
        {
            hintPath = new XElement("HintPath");
            reference.Add(hintPath);
        }
        hintPath.Value = Path.GetFullPath(assemblyPath);
        var privateElement = reference.Elements().FirstOrDefault(element =>
            element.Name.LocalName == "Private");
        if (privateElement is null)
        {
            privateElement = new XElement("Private");
            reference.Add(privateElement);
        }
        privateElement.Value = "false";
    }

    /// <summary>
    /// Creates the XML for a solution containing the game script project.
    /// </summary>
    /// <param name="projectName">Filesystem-safe game project name.</param>
    /// <returns>The solution XML.</returns>
    private static string CreateSolution(string projectName)
    {
        var projectPath = SecurityElement.Escape($"Scripts/{projectName}.Scripts.csproj");
        return $"""
            <Solution>
              <Project Path="{projectPath}" />
            </Solution>
            """;
    }
}

/// <summary>
/// Identifies the generated .NET workspace files for game scripts.
/// </summary>
/// <param name="SolutionPath">Absolute path to the generated .slnx solution.</param>
/// <param name="ScriptProjectPath">Absolute path to the generated script project.</param>
public sealed record ScriptingWorkspace(string SolutionPath, string ScriptProjectPath);

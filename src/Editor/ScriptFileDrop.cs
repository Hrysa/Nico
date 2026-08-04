using System.Text.RegularExpressions;
using Engine.UI;

namespace Editor;

/// <summary>Resolves game script source files dropped onto Inspector script fields.</summary>
public static partial class ScriptFileDrop
{
    /// <summary>Reads a C# source file and attaches its first declared scene-script type.</summary>
    /// <param name="source">Dragged filesystem entry.</param>
    /// <param name="target">UI element under the drop pointer.</param>
    /// <param name="inspector">Inspector receiving the script attachment.</param>
    /// <returns>True when a scene-script type was resolved and attached.</returns>
    public static bool TryAttach(
        FileSystemNode source,
        UIElement? target,
        SceneInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(inspector);
        if (source.IsDirectory ||
            !string.Equals(Path.GetExtension(source.FullPath), ".cs", StringComparison.OrdinalIgnoreCase) ||
            target is not TextField { Name: "ScriptTypeField" } ||
            !File.Exists(source.FullPath))
        {
            return false;
        }

        var sourceText = File.ReadAllText(source.FullPath);
        var classMatch = SceneScriptClassPattern().Match(sourceText);
        if (!classMatch.Success)
            return false;
        var namespaceMatch = NamespacePattern().Match(sourceText);
        var className = classMatch.Groups["name"].Value;
        var typeName = namespaceMatch.Success
            ? $"{namespaceMatch.Groups["name"].Value}.{className}"
            : className;
        return inspector.AttachScriptType(typeName);
    }

    /// <summary>Matches a file-scoped or block-scoped C# namespace declaration.</summary>
    /// <returns>The generated namespace regular expression.</returns>
    [GeneratedRegex(@"\bnamespace\s+(?<name>[A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)*)\s*[;{]")]
    private static partial Regex NamespacePattern();

    /// <summary>Matches a concrete class whose first base type is SceneScript.</summary>
    /// <returns>The generated scene-script class regular expression.</returns>
    [GeneratedRegex(@"\b(?:public\s+)?(?:sealed\s+)?class\s+(?<name>[A-Za-z_][\w]*)\s*:\s*(?:Engine\.Scripting\.)?SceneScript\b")]
    private static partial Regex SceneScriptClassPattern();
}

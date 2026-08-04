using Engine.Assets;
using Engine.UI;

namespace Editor;

/// <summary>Resolves game script source files dropped onto Inspector script fields.</summary>
public static class ScriptFileDrop
{
    /// <summary>Reads a C# source file and attaches its first declared scene-script type.</summary>
    /// <param name="source">Dragged filesystem entry.</param>
    /// <param name="target">UI element under the drop pointer.</param>
    /// <param name="inspector">Inspector receiving the script attachment.</param>
    /// <param name="database">Asset database resolving the source identity.</param>
    /// <returns>True when a scene-script type was resolved and attached.</returns>
    public static bool TryAttach(
        FileSystemNode source,
        UIElement? target,
        SceneInspector inspector,
        AssetDatabase database)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(database);
        if (source.IsDirectory ||
            !string.Equals(Path.GetExtension(source.FullPath), ".cs", StringComparison.OrdinalIgnoreCase) ||
            target is not TextField { Name: "ScriptAssetField" })
        {
            return false;
        }

        var record = database.FindByPath(source.FullPath);
        return record is { Importer: "csharp-script" } && inspector.AttachScript(record.Id);
    }
}

using Engine.UI;

namespace Editor;

/// <summary>Defines stable panel identities and the default persisted Editor dock layout.</summary>
public static class EditorDockWorkspace
{
    private const string SettingsDirectory = ".nico";
    private const string WorkspaceFileName = "editor-workspace.json";

    /// <summary>Stable hierarchy panel identifier.</summary>
    public const string HierarchyId = "editor.hierarchy";

    /// <summary>Stable project-files panel identifier.</summary>
    public const string FileSystemId = "editor.filesystem";

    /// <summary>Stable Scene viewport identifier.</summary>
    public const string SceneId = "editor.scene";

    /// <summary>Stable Game viewport identifier.</summary>
    public const string GameId = "editor.game";

    /// <summary>Stable Inspector panel identifier.</summary>
    public const string InspectorId = "editor.inspector";

    /// <summary>Stable Profiler panel identifier.</summary>
    public const string ProfilerId = "editor.profiler";

    /// <summary>Stable animation-set editor panel identifier.</summary>
    public const string AnimationSetId = "editor.animation-set";

    /// <summary>Creates the safe default workspace used when no compatible persisted layout exists.</summary>
    /// <returns>Complete default Editor dock workspace.</returns>
    public static DockWorkspace CreateDefault()
    {
        var leftTools = new DockSplit(
            DockSplitOrientation.Vertical,
            Group(HierarchyId, "Hierarchy"),
            Group(FileSystemId, "File System"),
            0.58f);
        var viewports = new DockSplit(
            DockSplitOrientation.Vertical,
            Group(SceneId, "Scene"),
            new DockTabGroup([
                new DockTab(GameId, "Game"),
                new DockTab(ProfilerId, "Profiler")
            ], GameId),
            0.73f);
        var primary = new DockSplit(
            DockSplitOrientation.Horizontal,
            leftTools,
            new DockSplit(
                DockSplitOrientation.Horizontal,
                viewports,
                Group(InspectorId, "Inspector"),
                0.70f),
            0.20f);
        return new DockWorkspace { Root = primary };
    }

    /// <summary>Gets the project-scoped Editor workspace persistence path.</summary>
    /// <param name="projectRoot">Absolute or relative game-project root.</param>
    /// <returns>Normalized workspace JSON path.</returns>
    public static string GetStoragePath(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return Path.Combine(Path.GetFullPath(projectRoot), SettingsDirectory, WorkspaceFileName);
    }

    /// <summary>Restores a project workspace or creates the complete safe default.</summary>
    /// <param name="projectRoot">Game-project root.</param>
    /// <param name="error">Restoration error when fallback was required.</param>
    /// <returns>Restored or default workspace.</returns>
    public static DockWorkspace Load(string projectRoot, out Exception? error) =>
        DockWorkspaceStore.LoadOrDefault(GetStoragePath(projectRoot), CreateDefault, out error);

    /// <summary>Persists a project workspace atomically.</summary>
    /// <param name="projectRoot">Game-project root.</param>
    /// <param name="workspace">Workspace to persist.</param>
    public static void Save(string projectRoot, DockWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        DockWorkspaceStore.Save(GetStoragePath(projectRoot), workspace);
    }

    /// <summary>Checks whether a panel currently belongs to any floating root.</summary>
    /// <param name="workspace">Workspace to inspect.</param>
    /// <param name="panelId">Stable panel identifier.</param>
    /// <returns>True when found below a floating root.</returns>
    public static bool IsFloating(DockWorkspace workspace, string panelId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(panelId);
        for (var index = 0; index < workspace.FloatingRoots.Count; index++)
        {
            if (Contains(workspace.FloatingRoots[index].Root, panelId))
                return true;
        }
        return false;
    }

    /// <summary>Registers the existing retained Editor panels under their persistence identifiers.</summary>
    /// <param name="view">Built Editor view containing retained panel instances.</param>
    /// <param name="allowViewportFloating">Whether Scene and Game have native transfer hooks.</param>
    /// <returns>Registry resolving every default workspace panel.</returns>
    public static DockPanelRegistry CreateRegistry(
        EditorView view,
        bool allowViewportFloating = false)
    {
        ArgumentNullException.ThrowIfNull(view);
        var registry = new DockPanelRegistry();
        registry.Register(HierarchyId, "Hierarchy", () => view.HierarchyTree);
        registry.Register(FileSystemId, "File System", () => view.FileSystemTree);
        registry.Register(
            SceneId, "Scene", () => view.SceneSlot, canFloat: allowViewportFloating);
        registry.Register(
            GameId, "Game", () => view.GameSlot, canFloat: allowViewportFloating);
        registry.Register(InspectorId, "Inspector", () => view.Inspector);
        registry.Register(ProfilerId, "Profiler", () => view.ProfilerContent);
        return registry;
    }

    /// <summary>Replaces the declarative Editor workspace content with a retained dock-session host.</summary>
    /// <param name="view">Built Editor view.</param>
    /// <param name="session">Dock session owning the replacement host.</param>
    public static void Mount(EditorView view, DockSession session)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(session);
        var workspaceHost = view.WorkspaceHost;
        if (ReferenceEquals(session.MainHost.Parent, workspaceHost))
            return;
        workspaceHost.Content = session.MainHost;
        workspaceHost.InvalidateMeasure();
    }

    /// <summary>Creates one selected single-tab group.</summary>
    /// <param name="id">Stable panel identifier.</param>
    /// <param name="title">Displayed title.</param>
    /// <returns>Selected tab group.</returns>
    private static DockTabGroup Group(string id, string title) =>
        new([new DockTab(id, title)], id);

    /// <summary>Checks a dock subtree for one panel identifier.</summary>
    /// <param name="node">Dock subtree.</param>
    /// <param name="panelId">Stable panel identifier.</param>
    /// <returns>True when found.</returns>
    private static bool Contains(DockNode node, string panelId)
    {
        if (node is DockTabGroup group)
        {
            for (var index = 0; index < group.Tabs.Count; index++)
            {
                if (string.Equals(group.Tabs[index].Id, panelId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
        var split = (DockSplit)node;
        return Contains(split.First, panelId) || Contains(split.Second, panelId);
    }
}

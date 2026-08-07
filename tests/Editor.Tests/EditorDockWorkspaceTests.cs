using Editor;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

/// <summary>Verifies the Editor-specific dock workspace migration boundary.</summary>
public sealed class EditorDockWorkspaceTests
{
    /// <summary>Verifies the default tree contains every stable Editor panel exactly once.</summary>
    [Fact]
    public void CreateDefault_ContainsEveryEditorPanelOnce()
    {
        var workspace = EditorDockWorkspace.CreateDefault();
        var identifiers = new List<string>();

        Collect(workspace.Root, identifiers);

        Assert.Equal(6, identifiers.Count);
        Assert.Equal(6, identifiers.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(EditorDockWorkspace.HierarchyId, identifiers);
        Assert.Contains(EditorDockWorkspace.FileSystemId, identifiers);
        Assert.Contains(EditorDockWorkspace.SceneId, identifiers);
        Assert.Contains(EditorDockWorkspace.GameId, identifiers);
        Assert.Contains(EditorDockWorkspace.InspectorId, identifiers);
        Assert.Contains(EditorDockWorkspace.ProfilerId, identifiers);
        Assert.False(workspace.IsTabSelected(EditorDockWorkspace.ProfilerId));
        Assert.True(workspace.IsTabSelected(EditorDockWorkspace.GameId));
    }

    /// <summary>Verifies registry resolution preserves the existing retained panel instances.</summary>
    [Fact]
    public void CreateRegistry_ResolvesBuiltViewInstances()
    {
        var view = EditorUI.BuildView(1280f, 720f);
        var registry = EditorDockWorkspace.CreateRegistry(view);

        Assert.Same(view.HierarchyTree, registry.Resolve(EditorDockWorkspace.HierarchyId));
        Assert.Same(view.FileSystemTree, registry.Resolve(EditorDockWorkspace.FileSystemId));
        Assert.Same(view.SceneSlot, registry.Resolve(EditorDockWorkspace.SceneId));
        Assert.Same(view.GameSlot, registry.Resolve(EditorDockWorkspace.GameId));
        Assert.Same(view.Inspector, registry.Resolve(EditorDockWorkspace.InspectorId));
        Assert.Same(view.ProfilerContent, registry.Resolve(EditorDockWorkspace.ProfilerId));
        Assert.False(registry.CanFloat(EditorDockWorkspace.SceneId));
        Assert.False(registry.CanFloat(EditorDockWorkspace.GameId));
        Assert.True(registry.CanFloat(EditorDockWorkspace.HierarchyId));
    }

    /// <summary>Verifies project-scoped persistence round-trips the complete Editor workspace.</summary>
    [Fact]
    public void SaveAndLoad_ProjectSettings_RoundTripsWorkspace()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"nico-editor-dock-{Guid.NewGuid():N}");
        try
        {
            var workspace = EditorDockWorkspace.CreateDefault();
            var floating = workspace.FloatTab(
                EditorDockWorkspace.ProfilerId, 40f, 50f, 500f, 300f);
            Assert.NotNull(floating);
            floating.Id = "floating-profiler";

            EditorDockWorkspace.Save(projectRoot, workspace);
            var restored = EditorDockWorkspace.Load(projectRoot, out var error);

            Assert.Null(error);
            Assert.Single(restored.FloatingRoots);
            Assert.Equal("floating-profiler", restored.FloatingRoots[0].Id);
            Assert.EndsWith(
                Path.Combine(".nico", "editor-workspace.json"),
                EditorDockWorkspace.GetStoragePath(projectRoot),
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
                Directory.Delete(projectRoot, recursive: true);
        }
    }

    /// <summary>Verifies corrupt project state falls back without deleting the invalid evidence.</summary>
    [Fact]
    public void Load_CorruptProjectState_ReturnsDefaultAndPreservesFile()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"nico-editor-dock-{Guid.NewGuid():N}");
        var path = EditorDockWorkspace.GetStoragePath(projectRoot);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{invalid-json");

            var restored = EditorDockWorkspace.Load(projectRoot, out var error);
            var identifiers = new List<string>();
            Collect(restored.Root, identifiers);

            Assert.NotNull(error);
            Assert.Equal(6, identifiers.Count);
            Assert.Equal("{invalid-json", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(projectRoot))
                Directory.Delete(projectRoot, recursive: true);
        }
    }

    /// <summary>Verifies mounting replaces the legacy workspace while retaining every panel instance.</summary>
    [Fact]
    public void Mount_ReplacesLegacyWorkspaceWithDockHost()
    {
        var view = EditorUI.BuildView(1280f, 720f);
        var registry = EditorDockWorkspace.CreateRegistry(view);
        using var session = new DockSession(
            EditorDockWorkspace.CreateDefault(), registry, new RejectFloatingFactory());

        EditorDockWorkspace.Mount(view, session);
        view.Root.BuildDrawList();

        Assert.Null(view.InitialDockHost.Parent);
        Assert.Same(view.Root, session.MainHost.Parent);
        Assert.True(IsDescendant(session.MainHost, view.HierarchyTree));
        Assert.True(IsDescendant(session.MainHost, view.FileSystemTree));
        Assert.True(IsDescendant(session.MainHost, view.SceneSlot));
        Assert.True(IsDescendant(session.MainHost, view.GameSlot));
        Assert.True(IsDescendant(session.MainHost, view.Inspector));
        Assert.True(IsDescendant(session.MainHost, view.ProfilerContent));
    }

    /// <summary>Checks whether an element belongs to a retained ancestor subtree.</summary>
    /// <param name="ancestor">Expected ancestor.</param>
    /// <param name="element">Candidate descendant.</param>
    /// <returns>True when the ancestor occurs in the parent chain.</returns>
    private static bool IsDescendant(UIElement ancestor, UIElement element)
    {
        for (var current = element.Parent; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }
        return false;
    }

    /// <summary>Collects tab identifiers from a dock subtree.</summary>
    /// <param name="node">Current dock node.</param>
    /// <param name="identifiers">Destination identifier list.</param>
    private static void Collect(DockNode node, List<string> identifiers)
    {
        if (node is DockTabGroup group)
        {
            for (var index = 0; index < group.Tabs.Count; index++)
                identifiers.Add(group.Tabs[index].Id);
            return;
        }
        var split = Assert.IsType<DockSplit>(node);
        Collect(split.First, identifiers);
        Collect(split.Second, identifiers);
    }

    /// <summary>Rejects unexpected floating roots in mount-only tests.</summary>
    private sealed class RejectFloatingFactory : IDockFloatingWindowFactory
    {
        /// <inheritdoc/>
        public IDockFloatingWindow Create(FloatingDockRoot model, DockHost content) =>
            throw new InvalidOperationException("The default workspace must not contain floating roots.");
    }
}

using System.Numerics;
using Editor;
using Engine.Core;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public sealed class TerrainDocumentTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("nico-terrain-document-").FullName;

    /// <summary>Sculpts, persists, undoes, and reapplies one shared terrain stroke.</summary>
    [Fact]
    public void RaiseStroke_EndAndHistory_PersistsUndoableHeightGrid()
    {
        var path = Path.Combine(_directory, "Ground.nterrain");
        TerrainAuthoring.SaveFlat(path, 5, 5, 0.2f);
        var saved = 0;
        var document = CreateDocument(path, _ => saved++);

        document.BeginStroke();
        var dirty = document.ApplyBrush(0.5f, 0.5f, 0.3f, 0.3f, 0.25f,
            TerrainBrushMode.Raise, 0f);
        var raisedHeight = document.Value.GetHeight(2, 2);
        Assert.NotNull(dirty);
        Assert.True(raisedHeight > 0.2f);

        Assert.True(document.EndStroke());
        Assert.Equal(1, saved);
        using (var stream = File.OpenRead(path))
            Assert.Equal(raisedHeight, TerrainResource.Load(stream).GetHeight(2, 2));

        Assert.True(document.Undo());
        Assert.Equal(0.2f, document.Value.GetHeight(2, 2), 5);
        Assert.True(document.Redo());
        Assert.Equal(raisedHeight, document.Value.GetHeight(2, 2), 5);
    }

    /// <summary>Cancelling a stroke restores both samples and the prior dirty state.</summary>
    [Fact]
    public void CancelStroke_ChangedCleanDocument_RestoresOriginalState()
    {
        var path = Path.Combine(_directory, "Cancel.nterrain");
        TerrainAuthoring.SaveFlat(path, 3, 3, 0.4f);
        var document = CreateDocument(path, _ => { });

        document.BeginStroke();
        document.ApplyBrush(0.5f, 0.5f, 1f, 1f, 0.2f,
            TerrainBrushMode.Lower, 0f);
        Assert.True(document.IsDirty);

        Assert.True(document.CancelStroke());
        Assert.False(document.IsDirty);
        Assert.Equal(0.4f, document.Value.GetHeight(1, 1), 5);
    }

    /// <summary>Keeps repeated in-stroke height dabs allocation-free after warmup.</summary>
    [Fact]
    public void ApplyBrush_RepeatedRaiseDabs_DoesNotAllocate()
    {
        var path = Path.Combine(_directory, "Allocation.nterrain");
        TerrainAuthoring.SaveFlat(path, 17, 17, 0.2f);
        var document = CreateDocument(path, _ => { });
        document.BeginStroke();
        document.ApplyBrush(0.5f, 0.5f, 0.2f, 0.2f, 0.0001f,
            TerrainBrushMode.Raise, 0f);

        var before = GC.GetAllocatedBytesForCurrentThread();
        TerrainEditRegion? latest = null;
        for (var index = 0; index < 100; index++)
        {
            latest = document.ApplyBrush(0.5f, 0.5f, 0.2f, 0.2f, 0.0001f,
                TerrainBrushMode.Raise, 0f);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(latest);
        Assert.Equal(0, allocated);
        document.CancelStroke();
    }

    /// <summary>Routes a center-screen pointer stroke into selected terrain and saves it.</summary>
    [Fact]
    public void BrushController_SelectedTerrain_PrimaryStrokeEditsAndPublishes()
    {
        var path = Path.Combine(_directory, "Brush.nterrain");
        TerrainAuthoring.SaveFlat(path, 5, 5, 0.2f);
        var document = CreateDocument(path, _ => { });
        var reference = document.Reference;
        var instance = new MeshInstance3D { Mesh = reference };
        var collider = new TerrainColliderComponent
        {
            TerrainData = reference,
            HorizontalSize = new Vector2(8f, 8f),
            HeightScale = 4f
        };
        instance.AddComponent(collider);
        var camera = new PerspectiveCamera(MathF.PI / 4f, 4f / 3f, 0.1f, 100f)
        {
            Position = new Vector3(0f, 8f, 8f)
        };
        camera.LookAt(new Vector3(0f, 0.8f, 0f));
        camera.UpdateViewport(800f, 600f);
        var editCount = 0;
        TerrainBrushPreview? preview = null;
        var settings = new TerrainBrushSettings
        {
            IsEnabled = true,
            Radius = 2f,
            Strength = 0.5f
        };
        var controller = new TerrainBrushController(
            camera,
            () => new GizmoViewport(0f, 0f, 800f, 600f),
            () => instance,
            _ => document,
            (_, _, _, _) => editCount++,
            value => preview = value,
            settings);

        Assert.True(controller.PrimaryDown(new Vector2(400f, 300f)));
        Assert.True(controller.PrimaryUp());

        Assert.True(document.Value.GetHeight(2, 2) > 0.2f);
        Assert.True(editCount > 0);
        Assert.NotNull(preview);
        Assert.False(document.IsDirty);
    }

    /// <summary>Exposes all sculpt modes and shared numeric settings in reusable Inspector content.</summary>
    [Fact]
    public void InspectorContent_EditableTerrain_ExposesCompleteSculptToolset()
    {
        var path = Path.Combine(_directory, "Inspector.nterrain");
        TerrainAuthoring.SaveFlat(path);
        var document = CreateDocument(path, _ => { });
        var settings = new TerrainBrushSettings();

        var content = new TerrainInspectorContent(300f, document, settings);

        Assert.Contains(content.Children.OfType<ToggleButton>(),
            control => control.Name == "TerrainSculptEnabled");
        foreach (var mode in Enum.GetValues<TerrainBrushMode>())
        {
            Assert.Contains(content.Children.OfType<ToggleButton>(),
                control => control.Name == $"TerrainBrush{mode}");
        }
        Assert.Contains(content.Children.OfType<TextField>(),
            control => control.Name == "TerrainBrushRadius");
        Assert.Contains(content.Children.OfType<TextField>(),
            control => control.Name == "TerrainBrushStrength");
        Assert.Contains(content.Children.OfType<Button>(),
            control => control.Name == "TerrainUndo");
        Assert.Contains(content.Children.OfType<Button>(),
            control => control.Name == "TerrainRedo");
        Assert.Contains(content.Children.OfType<Button>(),
            control => control.Name == "TerrainSave");
        Assert.Contains(content.Children.OfType<Button>(),
            control => control.Name == "TerrainReload");
    }

    /// <summary>Creates the shared document directly from one editable source path.</summary>
    /// <param name="path">Terrain source path.</param>
    /// <param name="saved">Persistence callback.</param>
    /// <returns>Loaded terrain document.</returns>
    private static TerrainDocument CreateDocument(
        string path,
        Action<AssetReference> saved)
    {
        var reference = new AssetReference(AssetId.New(), "main");
        using var stream = File.OpenRead(path);
        var value = TerrainResource.Load(stream);
        return new TerrainDocument(new AssetDocumentLocation(
            reference,
            () => Path.GetFileName(path),
            () => File.OpenRead(path),
            write => AssetDocumentStorage.WriteAtomic(path, write)), value, saved);
    }

    /// <summary>Removes temporary terrain sources.</summary>
    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}

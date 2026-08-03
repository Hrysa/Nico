using Editor;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class CompilationProgressDialogTests
{
    /// <summary>Verifies compilation progress is modal and animates its indicator.</summary>
    [Fact]
    public void Update_ProgressDialog_MovesOverlayIndicator()
    {
        var dialog = new CompilationProgressDialog(1280f, 720f);
        dialog.BuildDrawList();
        var track = Assert.Single(dialog.Dialog.Descendants().OfType<Surface>(),
            child => child.Name == "CompilationProgressTrack");
        var indicator = Assert.Single(track.Descendants().OfType<Surface>(),
            child => child.Name == "CompilationProgressIndicator");
        var initialPosition = indicator.Position;

        dialog.Update(0.35);
        dialog.BuildDrawList();

        Assert.True(dialog.IsOverlay);
        Assert.NotEqual(initialPosition, indicator.Position);
        Assert.All(dialog.BuildDrawList().Commands,
            command => Assert.Equal(UIDrawLayer.Overlay, command.Layer));
    }
}

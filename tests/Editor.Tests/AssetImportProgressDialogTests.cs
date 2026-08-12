using Editor;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public sealed class AssetImportProgressDialogTests
{
    /// <summary>Updates the determinate bar and current asset without rebuilding the dialog.</summary>
    [Fact]
    public void SetProgress_CompletedAsset_UpdatesDialogContent()
    {
        var dialog = new AssetImportProgressDialog(1280f, 720f, 4);

        dialog.SetProgress(2, 4, "models/world.glb");

        var progress = Assert.Single(dialog.Dialog.Descendants().OfType<ProgressBar>());
        Assert.Equal(2f, progress.Value);
        Assert.Equal(4f, progress.Maximum);
        Assert.Contains(dialog.Dialog.Descendants().OfType<Label>(),
            label => label.Text == "models/world.glb");
        Assert.Contains(dialog.Dialog.Descendants().OfType<Label>(),
            label => label.Text == "2 of 4");
    }
}

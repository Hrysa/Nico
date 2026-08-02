using Editor;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class ConfirmationDialogTests
{
    /// <summary>Verifies the destructive action is raised only from the confirmation button.</summary>
    [Fact]
    public void ConfirmButton_Click_RaisesConfirmed()
    {
        var dialog = new ConfirmationDialog(800f, 600f, "Delete", "Delete item?", "Delete");
        var confirm = dialog.Dialog.Children.OfType<Button>()
            .Single(button => button.Name == "Confirm");
        var router = new UIEventRouter(dialog, () => { });
        var confirmed = false;
        dialog.Confirmed += () => confirmed = true;

        router.MovePointer(new(confirm.Left + 5f, confirm.Top + 5f));
        router.Press();
        router.Release(invokeClick: true);

        Assert.True(confirmed);
    }
}

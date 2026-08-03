using Editor;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class FileSystemCreateDialogTests
{
    /// <summary>Verifies a valid leaf name is trimmed and reported.</summary>
    [Fact]
    public void Create_ValidName_RaisesCreateRequested()
    {
        var dialog = new FileSystemCreateDialog(800f, 600f, "Folder", "project");
        dialog.BuildDrawList();
        var field = Assert.Single(dialog.Dialog.Descendants().OfType<TextField>());
        var create = dialog.Dialog.Descendants().OfType<Button>().Single(button => button.Name == "Create");
        var router = new UIEventRouter(dialog, () => { });
        Click(router, field.Left + 10f, field.Top + 10f);
        foreach (var character in "  assets  ")
            router.TextInput(character);
        string? requestedName = null;
        dialog.CreateRequested += name => requestedName = name;

        Click(router, create.Left + 10f, create.Top + 10f);

        Assert.Equal("assets", requestedName);
    }

    /// <summary>Verifies path-like input remains in the dialog and displays an error.</summary>
    [Fact]
    public void Create_PathName_ShowsValidationError()
    {
        var dialog = new FileSystemCreateDialog(800f, 600f, "File", "project");
        dialog.BuildDrawList();
        var field = Assert.Single(dialog.Dialog.Descendants().OfType<TextField>());
        var create = dialog.Dialog.Descendants().OfType<Button>().Single(button => button.Name == "Create");
        var error = dialog.Dialog.Descendants().OfType<Label>()
            .Single(label => label.Name == "ValidationError");
        var router = new UIEventRouter(dialog, () => { });
        Click(router, field.Left + 10f, field.Top + 10f);
        foreach (var character in "nested/file.txt")
            router.TextInput(character);
        var raised = false;
        dialog.CreateRequested += _ => raised = true;

        Click(router, create.Left + 10f, create.Top + 10f);

        Assert.False(raised);
        Assert.NotEmpty(error.Text);
    }

    /// <summary>Performs one routed pointer click.</summary>
    /// <param name="router">Router receiving the click.</param>
    /// <param name="x">Screen X coordinate.</param>
    /// <param name="y">Screen Y coordinate.</param>
    private static void Click(UIEventRouter router, float x, float y)
    {
        router.MovePointer(new(x, y));
        router.Press();
        router.Release(invokeClick: true);
    }
}

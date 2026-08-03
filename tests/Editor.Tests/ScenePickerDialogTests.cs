using Editor;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class ScenePickerDialogTests
{
    /// <summary>Verifies scene search filters visible rows and Open reports the selected absolute path.</summary>
    [Fact]
    public void SearchAndOpen_SelectedScene_RaisesOpenRequested()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "picker-project"));
        var first = Path.Combine(root, "first.node");
        var second = Path.Combine(root, "levels", "second.node");
        var picker = new ScenePickerDialog(800f, 600f, root, [first, second]);
        picker.BuildDrawList();
        var search = Assert.Single(picker.Dialog.Descendants().OfType<TextField>());
        var list = Assert.Single(picker.Dialog.Descendants().OfType<ListView>());
        var open = picker.Dialog.Descendants().OfType<Button>().Single(button => button.Name == "Open");
        var router = new UIEventRouter(picker, () => { });

        Click(router, search.Left + 10f, search.Top + 10f);
        foreach (var character in "second")
            router.TextInput(character);
        Assert.Single(list.Children);
        Click(router, list.Left + 10f, list.Top + 10f);
        string? openedPath = null;
        picker.OpenRequested += path => openedPath = path;

        Click(router, open.Left + 10f, open.Top + 10f);

        Assert.Equal(second, openedPath);
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

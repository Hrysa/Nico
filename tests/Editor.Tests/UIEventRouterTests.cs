using System.Numerics;
using Editor;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class UIEventRouterTests
{
    /// <summary>Verifies pointer routing to an unchanged hover target allocates no managed memory.</summary>
    [Fact]
    public void PointerMove_UnchangedTarget_DoesNotAllocate()
    {
        var root = new Panel(Color.Black, 500f, 500f);
        for (var index = 0; index < 100; index++)
            root.AddChild(new Panel(Color.Gray, 10f, 10f));
        root.BuildDrawList();
        var router = new UIEventRouter(root, () => { });
        router.MovePointer(new Vector2(1f, 1f));
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 1_000; index++)
            router.MovePointer(new Vector2(1f, 1f));

        Assert.Equal(allocationStart, GC.GetAllocatedBytesForCurrentThread());
    }

    /// <summary>Verifies IME pre-edit text remains transient until routed composition completion.</summary>
    [Fact]
    public void TextBox_Composition_SeparatesPreEditFromCommittedText()
    {
        var field = new TextField(180f, 30f) { Text = "A" };
        var router = new UIEventRouter(field, () => { });
        router.Focus(field);

        router.RouteTextComposition(new TextCompositionEvent(
            TextCompositionKind.Started, string.Empty, 0));
        Assert.True(field.IsComposing);
        router.RouteTextComposition(new TextCompositionEvent(
            TextCompositionKind.Updated, "日本", 2, 1, 99));

        Assert.Equal("A", field.Text);
        Assert.True(field.IsComposing);
        Assert.Equal("日本", field.CompositionText);
        Assert.Equal(1, field.CompositionSelectionStart);
        Assert.Equal(1, field.CompositionSelectionLength);
        var commands = field.BuildDrawList().Commands;
        Assert.Contains(commands,
            command => command.Type == UIDrawCommandType.Text && command.Text == "日本");
        Assert.Contains(commands,
            command => command.Type == UIDrawCommandType.Rectangle &&
                command.Color == UITheme.Dark.AccentPressed);

        router.RouteTextComposition(new TextCompositionEvent(
            TextCompositionKind.Completed, "日本", 2));

        Assert.Equal("A日本", field.Text);
        Assert.False(field.IsComposing);
    }

    /// <summary>Verifies canceled composition clears pre-edit state without mutating stored text.</summary>
    [Fact]
    public void TextBox_CompositionCanceled_DiscardsPreEditText()
    {
        var field = new TextField(180f, 30f) { Text = "base" };
        var router = new UIEventRouter(field, () => { });
        router.Focus(field);
        router.RouteTextComposition(new TextCompositionEvent(
            TextCompositionKind.Updated, "候補", 1));

        router.RouteTextComposition(new TextCompositionEvent(
            TextCompositionKind.Canceled, string.Empty, 0));

        Assert.Equal("base", field.Text);
        Assert.False(field.IsComposing);
    }

    /// <summary>Verifies asynchronous validation blocks commit and ignores a canceled stale completion.</summary>
    [Fact]
    public async Task TextField_AsyncValidation_TracksPendingAndCancelsOnEdit()
    {
        var completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var field = new TextField(180f, 30f)
        {
            Text = "base",
            AsyncValidator = async (_, token) => await completion.Task.WaitAsync(token)
        };
        var router = new UIEventRouter(field, () => { });
        router.Focus(field);
        router.RouteText("x");

        var validation = field.ValidateAsync().AsTask();
        Assert.True(field.IsValidationPending);
        Assert.True(field.GetSemanticInfo().IsBusy);
        Assert.False(router.ExecuteCommand(UIEditingCommands.CommitEdit, target: field));

        router.RouteText("y");
        Assert.False(await validation);
        Assert.False(field.IsValidationPending);
        Assert.Null(field.ValidationMessage);

        completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        validation = field.ValidateAsync().AsTask();
        completion.SetResult("Name already exists.");
        Assert.False(await validation);
        Assert.Equal("Name already exists.", field.ValidationMessage);
    }

    /// <summary>Verifies commit and explicit update triggers publish model updates at their configured boundary.</summary>
    [Fact]
    public void TextField_UpdateTrigger_ControlsValueUpdateRequests()
    {
        var field = new TextField(180f, 30f) { UpdateTrigger = TextUpdateTrigger.Commit };
        var updates = new List<string>();
        field.ValueUpdateRequested += updates.Add;
        var router = new UIEventRouter(field, () => { });
        router.Focus(field);

        router.RouteText("pending");
        Assert.Empty(updates);
        router.RouteKey(new KeyInputEvent(InputKey.Enter, true, false, InputModifiers.None));
        Assert.Equal(new[] { "pending" }, updates);

        field.UpdateTrigger = TextUpdateTrigger.Explicit;
        router.RouteText("x");
        Assert.Single(updates);
        Assert.True(field.RequestValueUpdate());
        Assert.Equal(new[] { "pending", "pendingx" }, updates);
    }

    /// <summary>Verifies a form command validates all editors before committing their model updates.</summary>
    [Fact]
    public void UIEditForm_CommitCommand_AggregatesValidationAndDirtyState()
    {
        var root = new Panel(Color.Black, 200f, 80f);
        var first = new TextField(180f, 30f) { UpdateTrigger = TextUpdateTrigger.Commit };
        var second = new TextField(180f, 30f)
        {
            UpdateTrigger = TextUpdateTrigger.Commit,
            Validator = text => text.Length < 2 ? "Too short" : null
        };
        root.AddChild(first);
        root.AddChild(second);
        using var form = new UIEditForm(root);
        form.Register(first);
        form.Register(second);
        var commit = new Button(80f, 30f, "Apply");
        var cancel = new Button(80f, 30f, "Revert");
        form.BindCommitButton(commit);
        form.BindCancelButton(cancel);
        using var summary = new UIValidationSummary(form, 180f, 40f);
        Assert.True(summary.IsVisible);
        Assert.Equal("Too short", summary.Text);
        var updates = new List<string>();
        first.ValueUpdateRequested += updates.Add;
        second.ValueUpdateRequested += updates.Add;
        var router = new UIEventRouter(root, () => { });
        router.Focus(first);
        router.RouteText("one");
        router.Focus(second);
        router.RouteText("x");

        Assert.False(router.ExecuteCommand(UIEditingCommands.CommitForm, target: second));
        Assert.Empty(updates);
        Assert.False(commit.IsEnabled);
        Assert.True(cancel.IsEnabled);
        router.RouteText("y");
        Assert.True(commit.IsEnabled);
        Assert.False(summary.IsVisible);
        commit.InvokeClick();

        Assert.Equal(new[] { "one", "xy" }, updates);
        Assert.False(form.IsDirty);
        Assert.True(form.IsValid);
        Assert.False(commit.IsEnabled);
        Assert.False(cancel.IsEnabled);
    }

    /// <summary>Verifies form validation focuses the first invalid editor in registration order.</summary>
    [Fact]
    public void UIEditForm_FocusFirstInvalid_UsesRegistrationOrder()
    {
        var root = new Panel(Color.Black, 200f, 80f);
        var first = new TextField(180f, 30f) { Validator = _ => "First error" };
        var second = new TextField(180f, 30f) { Validator = _ => "Second error" };
        root.AddChild(first);
        root.AddChild(second);
        using var form = new UIEditForm(root);
        form.Register(first);
        form.Register(second);
        var router = new UIEventRouter(root, () => { });
        router.Focus(second);

        Assert.True(form.FocusFirstInvalid(router));

        Assert.Same(first, router.FocusedElement);
        Assert.Same(first, form.FirstInvalidEditor);
    }

    /// <summary>Verifies Enter commits valid pending text and Escape restores the committed baseline.</summary>
    [Fact]
    public void TextField_CommitAndCancel_RouteEditingTransactions()
    {
        var field = new TextField(180f, 30f)
        {
            Text = "original",
            Validator = text => text.Length < 3 ? "Too short" : null
        };
        var commits = new List<string>();
        var canceled = 0;
        field.EditCommitted += commits.Add;
        field.EditCanceled += () => canceled++;
        var router = new UIEventRouter(field, () => { });
        router.Focus(field);
        router.RouteKey(new KeyInputEvent(InputKey.A, true, false, InputModifiers.Control));
        router.RouteText("changed");

        router.RouteKey(new KeyInputEvent(InputKey.Enter, true, false, InputModifiers.None));
        Assert.Equal(new[] { "changed" }, commits);
        Assert.False(field.IsDirty);

        router.RouteKey(new KeyInputEvent(InputKey.A, true, false, InputModifiers.Control));
        router.RouteText("x");
        Assert.False(router.ExecuteCommand(UIEditingCommands.CommitEdit, target: field));
        router.RouteKey(new KeyInputEvent(InputKey.Escape, true, false, InputModifiers.None));

        Assert.Equal("changed", field.Text);
        Assert.Equal(1, canceled);
        Assert.False(field.HasValidationError);
    }

    /// <summary>Verifies external form errors block commit until a subsequent text mutation clears them.</summary>
    [Fact]
    public void TextField_ExternalValidationError_BlocksCommitUntilEdit()
    {
        var field = new TextField(180f, 30f) { Text = "base" };
        var router = new UIEventRouter(field, () => { });
        router.Focus(field);
        router.RouteText("x");
        field.SetValidationError("Already exists.");

        Assert.False(router.ExecuteCommand(UIEditingCommands.CommitEdit, target: field));
        Assert.Equal("Already exists.", field.ValidationMessage);

        router.RouteText("y");
        Assert.Null(field.ValidationMessage);
        Assert.True(router.ExecuteCommand(UIEditingCommands.CommitEdit, target: field));
    }

    /// <summary>Verifies maximum length counts Unicode graphemes rather than UTF-16 code units.</summary>
    [Fact]
    public void TextBox_MaxLength_TruncatesCommittedGraphemes()
    {
        var textBox = new TextBox(180f, 80f) { MaxLength = 3 };
        var router = new UIEventRouter(textBox, () => { });
        router.Focus(textBox);

        router.RouteText("a😀bc");

        Assert.Equal("a😀b", textBox.Text);
        Assert.Equal(4, textBox.CaretIndex);
    }

    /// <summary>Verifies one Unicode scalar filter applies consistently to typing and paste.</summary>
    [Fact]
    public void TextField_InputFilter_AppliesToTypingAndPaste()
    {
        var clipboard = new TestClipboard { Text = "2b3" };
        var field = new TextField(180f, 30f)
        {
            InputFilter = rune => System.Text.Rune.IsDigit(rune)
        };
        var router = new UIEventRouter(field, () => { }, clipboard);
        router.Focus(field);

        router.RouteText("a1");
        router.RouteKey(new KeyInputEvent(InputKey.V, true, false, InputModifiers.Control));

        Assert.Equal("123", field.Text);
    }

    /// <summary>Verifies captured single-line selection scrolls horizontally beyond the right edge.</summary>
    [Fact]
    public void TextField_PointerSelectionOutsideBounds_AutoScrollsHorizontally()
    {
        var field = new TextField(60f, 30f) { Text = "abcdefghijklmnop" };
        field.BuildDrawList();
        var router = new UIEventRouter(field, () => { });
        router.MovePointer(new Vector2(5f, 15f));
        router.Press();

        router.RoutePointerMove(new PointerMoveEvent(
            0, new Vector2(90f, 15f), new Vector2(85f, 0f), PointerDeviceKind.Mouse,
            InputModifiers.None, PointerButtons.Primary));

        Assert.True(field.FirstVisibleTextIndex > 0);
        Assert.True(field.SelectionLength > 0);
    }

    /// <summary>Verifies the horizontal editing window never starts inside a surrogate pair.</summary>
    [Fact]
    public void TextField_NarrowEditingWindow_PreservesGraphemeBoundaries()
    {
        var field = new TextField(28f, 30f)
        {
            Text = "A😀B",
            TextLayoutOverride = new CodeUnitTextLayoutService()
        };
        var router = new UIEventRouter(field, () => { });
        router.Focus(field);

        field.BuildDrawList();

        Assert.Equal(3, field.FirstVisibleTextIndex);
    }

    /// <summary>Verifies opt-in Tab and Shift+Tab indent every selected logical line as one edit.</summary>
    [Fact]
    public void TextBox_TabIndentation_IndentsAndUnindentsSelection()
    {
        var textBox = new TextBox(180f, 80f)
        {
            Text = "a\nb",
            AcceptsTab = true,
            IndentationText = "  "
        };
        var router = new UIEventRouter(textBox, () => { });
        router.Focus(textBox);
        router.RouteKey(new KeyInputEvent(InputKey.A, true, false, InputModifiers.Control));

        router.RouteKey(new KeyInputEvent(InputKey.Tab, true, false, InputModifiers.None));
        Assert.Equal("  a\n  b", textBox.Text);
        router.RouteKey(new KeyInputEvent(InputKey.Tab, true, false, InputModifiers.Shift));

        Assert.Equal("a\nb", textBox.Text);
        Assert.Equal(3, textBox.SelectionLength);
    }

    /// <summary>Verifies ordinary Tab traversal remains active when indentation is not accepted.</summary>
    [Fact]
    public void TextField_TabWithoutIndentation_MovesFocus()
    {
        var root = new StackPanel(100f, 60f, Color.Black);
        var first = new TextField(100f, 30f);
        var second = new TextField(100f, 30f);
        root.AddItem(first);
        root.AddItem(second);
        var router = new UIEventRouter(root, () => { });
        router.Focus(first);

        router.RouteKey(new KeyInputEvent(InputKey.Tab, true, false, InputModifiers.None));

        Assert.Same(second, router.FocusedElement);
    }

    /// <summary>Verifies PageUp moves by the complete visible-line count and supports Shift extension.</summary>
    [Fact]
    public void TextBox_PageNavigation_MovesByVisibleLines()
    {
        var textBox = new TextBox(180f, 44f) { Text = "0\n1\n2\n3\n4" };
        var router = new UIEventRouter(textBox, () => { });
        router.Focus(textBox);

        router.RouteKey(new KeyInputEvent(InputKey.PageUp, true, false, InputModifiers.None));
        Assert.Equal(5, textBox.CaretIndex);
        router.RouteKey(new KeyInputEvent(
            InputKey.PageDown, true, false, InputModifiers.Shift));

        Assert.Equal(4, textBox.SelectionLength);
        Assert.Equal(3, textBox.FirstVisibleLine);
    }

    /// <summary>Verifies captured selection movement below the editor advances its vertical line window.</summary>
    [Fact]
    public void TextBox_PointerSelectionOutsideBounds_AutoScrolls()
    {
        var textBox = new TextBox(180f, 44f) { Text = "0\n1\n2\n3\n4" };
        textBox.BuildDrawList();
        var router = new UIEventRouter(textBox, () => { });
        router.MovePointer(new Vector2(6f, 8f));
        router.Press();

        router.RoutePointerMove(new PointerMoveEvent(
            0, new Vector2(6f, 60f), new Vector2(0f, 52f), PointerDeviceKind.Mouse,
            InputModifiers.None, PointerButtons.Primary));

        Assert.Equal(1, textBox.FirstVisibleLine);
        Assert.True(textBox.SelectionLength > 0);
    }

    /// <summary>Verifies repeated vertical movement retains the original grapheme column across short lines.</summary>
    [Fact]
    public void TextBox_VerticalNavigation_RetainsPreferredColumn()
    {
        var textBox = new TextBox(180f, 90f) { Text = "abcd\nx\nwxyz" };
        var router = new UIEventRouter(textBox, () => { });
        router.Focus(textBox);

        router.RouteKey(new KeyInputEvent(InputKey.Up, true, false, InputModifiers.None));
        Assert.Equal(6, textBox.CaretIndex);
        router.RouteKey(new KeyInputEvent(InputKey.Up, true, false, InputModifiers.None));

        Assert.Equal(4, textBox.CaretIndex);
    }

    /// <summary>Verifies a triple click selects one logical line including its terminating newline.</summary>
    [Fact]
    public void TextBox_TripleClick_SelectsLogicalLine()
    {
        var textBox = new TextBox(180f, 90f) { Text = "first\nsecond\nthird" };
        textBox.BuildDrawList();
        var router = new UIEventRouter(textBox, () => { });
        router.MovePointer(new Vector2(8f, 25f));

        router.DoubleClick(new PointerButtonEvent(
            0, new Vector2(8f, 25f), InputPointerButton.Primary, true, 3,
            PointerDeviceKind.Mouse, InputModifiers.None, PointerButtons.Primary));

        Assert.Equal(6, textBox.SelectionStart);
        Assert.Equal(7, textBox.SelectionLength);
    }

    /// <summary>Verifies bounded history evicts oldest edit groups first.</summary>
    [Fact]
    public void TextBox_HistoryCapacity_EvictsOldestGroups()
    {
        var textBox = new TextBox(180f, 80f) { HistoryCapacity = 2 };
        var router = new UIEventRouter(textBox, () => { });
        router.Focus(textBox);
        router.RouteText("a");
        router.RouteKey(new KeyInputEvent(InputKey.Left, true, false, InputModifiers.None));
        router.RouteKey(new KeyInputEvent(InputKey.Right, true, false, InputModifiers.None));
        router.RouteText("b");
        router.RouteKey(new KeyInputEvent(InputKey.Left, true, false, InputModifiers.None));
        router.RouteKey(new KeyInputEvent(InputKey.Right, true, false, InputModifiers.None));
        router.RouteText("c");

        router.RouteKey(new KeyInputEvent(InputKey.Z, true, false, InputModifiers.Control));
        router.RouteKey(new KeyInputEvent(InputKey.Z, true, false, InputModifiers.Control));

        Assert.Equal("a", textBox.Text);
        Assert.False(textBox.CanUndo);
    }

    /// <summary>Verifies caret movement and deletion do not split a combining-character grapheme.</summary>
    [Fact]
    public void TextBox_GraphemeNavigationAndDeletion_PreserveTextElements()
    {
        var textBox = new TextBox(180f, 80f) { Text = "Ae\u0301B" };
        var router = new UIEventRouter(textBox, () => { });
        router.Focus(textBox);

        router.RouteKey(new KeyInputEvent(InputKey.Left, true, false, InputModifiers.None));
        Assert.Equal(3, textBox.CaretIndex);
        router.RouteKey(new KeyInputEvent(InputKey.Left, true, false, InputModifiers.None));
        Assert.Equal(1, textBox.CaretIndex);
        router.RouteKey(new KeyInputEvent(InputKey.Delete, true, false, InputModifiers.None));

        Assert.Equal("AB", textBox.Text);
    }

    /// <summary>Verifies Control navigation moves and extends selection by identifier-like words.</summary>
    [Fact]
    public void TextBox_ControlArrow_NavigatesByWord()
    {
        var textBox = new TextBox(180f, 80f) { Text = "one two" };
        var router = new UIEventRouter(textBox, () => { });
        router.Focus(textBox);

        router.RouteKey(new KeyInputEvent(InputKey.Left, true, false, InputModifiers.Control));
        Assert.Equal(4, textBox.CaretIndex);
        router.RouteKey(new KeyInputEvent(
            InputKey.Left, true, false, InputModifiers.Control | InputModifiers.Shift));

        Assert.Equal(0, textBox.SelectionStart);
        Assert.Equal(4, textBox.SelectionLength);
    }

    /// <summary>Verifies word navigation classifies supplementary Unicode letters as letters.</summary>
    [Fact]
    public void TextBox_ControlArrow_TreatsSupplementaryLetterAsWordText()
    {
        var textBox = new TextBox(180f, 80f) { Text = "𐐀A x" };
        var router = new UIEventRouter(textBox, () => { });
        router.Focus(textBox);

        router.RouteKey(new KeyInputEvent(InputKey.Left, true, false, InputModifiers.Control));
        router.RouteKey(new KeyInputEvent(InputKey.Left, true, false, InputModifiers.Control));

        Assert.Equal(0, textBox.CaretIndex);
    }

    /// <summary>Verifies double click selects the complete word under the pointer.</summary>
    [Fact]
    public void TextBox_DoubleClick_SelectsWord()
    {
        var textBox = new TextBox(180f, 80f) { Text = "hello world" };
        textBox.BuildDrawList();
        var router = new UIEventRouter(textBox, () => { });
        router.MovePointer(new Vector2(8f, 10f));

        router.DoubleClick();

        Assert.Equal(0, textBox.SelectionStart);
        Assert.Equal(5, textBox.SelectionLength);
    }

    /// <summary>Verifies the release ending a double click preserves its committed word selection.</summary>
    [Fact]
    public void TextBox_DoubleClickRelease_PreservesWordSelection()
    {
        var textBox = new TextBox(180f, 80f) { Text = "hello world" };
        textBox.BuildDrawList();
        var router = new UIEventRouter(textBox, () => { });
        router.MovePointer(new Vector2(8f, 10f));

        router.Press();
        router.DoubleClick();
        router.Release(invokeClick: true);

        Assert.Equal(0, textBox.SelectionStart);
        Assert.Equal(5, textBox.SelectionLength);
    }

    /// <summary>Verifies adjacent committed text is undone and redone as one edit group.</summary>
    [Fact]
    public void TextBox_AdjacentTyping_CoalescesUndoHistory()
    {
        var textBox = new TextBox(180f, 80f);
        var router = new UIEventRouter(textBox, () => { });
        router.Focus(textBox);
        router.RouteText("abc");

        router.RouteKey(new KeyInputEvent(InputKey.Z, true, false, InputModifiers.Control));
        Assert.Equal(string.Empty, textBox.Text);
        Assert.False(textBox.CanUndo);
        router.RouteKey(new KeyInputEvent(InputKey.Y, true, false, InputModifiers.Control));
        Assert.Equal("abc", textBox.Text);
    }

    /// <summary>Verifies Shift navigation extends and contracts selection around a stable anchor.</summary>
    [Fact]
    public void TextBox_ShiftNavigation_ExtendsSelection()
    {
        var textBox = new TextBox(180f, 80f) { Text = "abc\ndef" };
        var router = new UIEventRouter(textBox, () => { });
        router.Focus(textBox);

        router.RouteKey(new KeyInputEvent(InputKey.Left, true, false, InputModifiers.Shift));
        router.RouteKey(new KeyInputEvent(InputKey.Left, true, false, InputModifiers.Shift));

        Assert.Equal(5, textBox.SelectionStart);
        Assert.Equal(2, textBox.SelectionLength);
        router.RouteKey(new KeyInputEvent(InputKey.Right, true, false, InputModifiers.Shift));
        Assert.Equal(1, textBox.SelectionLength);
    }

    /// <summary>Verifies routed undo and redo restore text, caret, and selection state.</summary>
    [Fact]
    public void TextBox_UndoRedo_RestoresEditingState()
    {
        var textBox = new TextBox(180f, 80f) { Text = "one" };
        var router = new UIEventRouter(textBox, () => { });
        router.Focus(textBox);
        router.RouteText("X");

        router.RouteKey(new KeyInputEvent(InputKey.Z, true, false, InputModifiers.Control));
        Assert.Equal("one", textBox.Text);
        Assert.True(textBox.CanRedo);

        router.RouteKey(new KeyInputEvent(InputKey.Y, true, false, InputModifiers.Control));
        Assert.Equal("oneX", textBox.Text);
        Assert.Equal(4, textBox.CaretIndex);
    }

    /// <summary>Verifies primary-pointer capture selects text from press through release.</summary>
    [Fact]
    public void TextBox_PointerDrag_SelectsHitTestedRange()
    {
        var textBox = new TextBox(180f, 80f) { Text = "abcdef" };
        textBox.BuildDrawList();
        var router = new UIEventRouter(textBox, () => { });
        router.MovePointer(new Vector2(5f, 10f));
        router.Press();
        router.RoutePointerMove(new PointerMoveEvent(
            0, new Vector2(170f, 10f), new Vector2(165f, 0f), PointerDeviceKind.Mouse,
            InputModifiers.None, PointerButtons.Primary));
        router.Release(new PointerButtonEvent(
            0, new Vector2(170f, 10f), InputPointerButton.Primary, false, 1,
            PointerDeviceKind.Mouse, InputModifiers.None, PointerButtons.None), false);

        Assert.Equal(0, textBox.SelectionStart);
        Assert.Equal(6, textBox.SelectionLength);
    }

    /// <summary>Verifies captured text selection repaints during movement rather than only on release.</summary>
    [Fact]
    public void TextField_PointerDrag_InvalidatesAndRepaintsBeforeRelease()
    {
        var invalidations = 0;
        var field = new PaintCountingTextField(180f, 30f) { Text = "abcdef" };
        field.BuildDrawList();
        var router = new UIEventRouter(field, () => invalidations++);
        router.MovePointer(new Vector2(5f, 15f));
        router.Press();
        field.BuildDrawList();
        var paintCountBeforeMove = field.PaintCount;
        var invalidationsBeforeMove = invalidations;

        router.RoutePointerMove(new PointerMoveEvent(
            0, new Vector2(170f, 15f), new Vector2(165f, 0f), PointerDeviceKind.Mouse,
            InputModifiers.None, PointerButtons.Primary));
        field.BuildDrawList();

        Assert.True(field.SelectionLength > 0);
        Assert.True(invalidations > invalidationsBeforeMove);
        Assert.True(field.PaintCount > paintCountBeforeMove);
    }

    /// <summary>Verifies multiline editors insert Enter and preserve normalized clipboard line breaks.</summary>
    [Fact]
    public void TextBox_EnterAndPaste_PreserveLogicalLines()
    {
        var clipboard = new TestClipboard { Text = "second\r\nthird" };
        var textBox = new TextBox(180f, 80f) { Text = "first" };
        var router = new UIEventRouter(textBox, () => { }, clipboard);
        router.Focus(textBox);

        router.RouteKey(new KeyInputEvent(InputKey.Enter, true, false, InputModifiers.None));
        router.RouteKey(new KeyInputEvent(InputKey.V, true, false, InputModifiers.Control));

        Assert.Equal("first\nsecond\nthird", textBox.Text);
        Assert.Equal(textBox.Text.Length, textBox.CaretIndex);
    }

    /// <summary>Verifies multiline vertical and line-boundary caret navigation.</summary>
    [Fact]
    public void TextBox_ArrowAndBoundaryKeys_NavigateLogicalLines()
    {
        var textBox = new TextBox(180f, 80f) { Text = "abcd\nxy" };
        var router = new UIEventRouter(textBox, () => { });
        router.Focus(textBox);

        router.RouteKey(new KeyInputEvent(InputKey.Up, true, false, InputModifiers.None));
        Assert.Equal(2, textBox.CaretIndex);
        router.RouteKey(new KeyInputEvent(InputKey.End, true, false, InputModifiers.None));
        Assert.Equal(4, textBox.CaretIndex);
        router.RouteKey(new KeyInputEvent(InputKey.Down, true, false, InputModifiers.None));
        Assert.Equal(7, textBox.CaretIndex);
        router.RouteKey(new KeyInputEvent(InputKey.Home, true, false, InputModifiers.None));
        Assert.Equal(5, textBox.CaretIndex);
    }

    /// <summary>Verifies clipboard gestures copy and replace the selected range.</summary>
    [Fact]
    public void TextField_ClipboardGestures_CopyAndPasteSelection()
    {
        var clipboard = new TestClipboard();
        var field = new TextField(160f, 30f) { Text = "hello" };
        var router = new UIEventRouter(field, () => { }, clipboard);
        router.Focus(field);
        router.RouteKey(new KeyInputEvent(InputKey.A, true, false, InputModifiers.Control));

        router.RouteKey(new KeyInputEvent(InputKey.C, true, false, InputModifiers.Control));
        Assert.Equal("hello", clipboard.Text);

        clipboard.Text = "world\nline";
        router.RouteKey(new KeyInputEvent(InputKey.V, true, false, InputModifiers.Control));

        Assert.Equal("world line", field.Text);
    }

    /// <summary>Verifies paste preserves non-ASCII clipboard text as UTF-16.</summary>
    [Fact]
    public void TextField_Paste_PreservesChineseText()
    {
        var clipboard = new TestClipboard { Text = "中文输入" };
        var field = new TextField(160f, 30f);
        var router = new UIEventRouter(field, () => { }, clipboard);
        router.Focus(field);

        router.RouteKey(new KeyInputEvent(InputKey.V, true, false, InputModifiers.Control));

        Assert.Equal("中文输入", field.Text);
    }

    /// <summary>Verifies Cut mutates editable fields while read-only fields permit Copy only.</summary>
    [Fact]
    public void TextField_ClipboardGestures_RespectReadOnlyState()
    {
        var clipboard = new TestClipboard();
        var field = new TextField(160f, 30f) { Text = "locked" };
        var router = new UIEventRouter(field, () => { }, clipboard);
        router.Focus(field);
        router.RouteKey(new KeyInputEvent(InputKey.A, true, false, InputModifiers.Control));
        field.IsReadOnly = true;

        router.RouteKey(new KeyInputEvent(InputKey.C, true, false, InputModifiers.Control));
        router.RouteKey(new KeyInputEvent(InputKey.X, true, false, InputModifiers.Control));
        clipboard.Text = "replacement";
        router.RouteKey(new KeyInputEvent(InputKey.V, true, false, InputModifiers.Control));

        Assert.Equal("locked", field.Text);
    }

    /// <summary>Verifies ClipToBounds excludes overflowing descendants from hit testing.</summary>
    [Fact]
    public void MovePointer_OverflowingChild_RespectsAncestorClipToBounds()
    {
        var root = new Canvas { Width = 100f, Height = 100f };
        var child = new Button(50f, 30f, "Overflow", Color.Black)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        root.Add(child, new Vector2(80f, 10f));
        root.BuildDrawList();
        var router = new UIEventRouter(root, () => { });

        router.MovePointer(new Vector2(110f, 20f));
        Assert.Same(child, router.HoveredElement);

        root.ClipToBounds = true;
        router.MovePointer(new Vector2(110f, 20f));
        Assert.Null(router.HoveredElement);
    }

    /// <summary>Verifies editing gestures execute before ordinary routed key handlers.</summary>
    [Fact]
    public void RouteKey_SelectAllGesture_ReplacesSelectionAndSuppressesKeyRoute()
    {
        var field = new TextField(100f, 30f) { Text = "hello" };
        var routedKeys = 0;
        field.Key += (_, _) => routedKeys++;
        var router = new UIEventRouter(field, () => { });
        router.Focus(field);

        router.RouteKey(new KeyInputEvent(InputKey.A, true, false, InputModifiers.Control));
        Assert.Equal(5, field.SelectionLength);
        Assert.Equal(0, routedKeys);

        router.RouteText("X");

        Assert.Equal("X", field.Text);
        Assert.Equal(0, field.SelectionLength);
    }

    /// <summary>Verifies built-in deletion gestures use routed command eligibility.</summary>
    [Fact]
    public void RouteKey_BackspaceGesture_DeletesThroughEditingCommand()
    {
        var field = new TextField(100f, 30f) { Text = "abc" };
        var router = new UIEventRouter(field, () => { });
        router.Focus(field);

        router.RouteKey(new KeyInputEvent(InputKey.Backspace, true, false, InputModifiers.None));

        Assert.Equal("ab", field.Text);
    }

    /// <summary>Verifies repeat transitions bypass gestures and retain ordinary key routing.</summary>
    [Fact]
    public void RouteKey_RepeatedGestureKey_UsesOrdinaryKeyRoute()
    {
        var field = new TextField(100f, 30f) { Text = "abc" };
        var routedKeys = 0;
        field.Key += (_, _) => routedKeys++;
        var router = new UIEventRouter(field, () => { });
        router.Focus(field);

        router.RouteKey(new KeyInputEvent(InputKey.A, true, true, InputModifiers.Control));

        Assert.Equal(1, routedKeys);
        Assert.Equal(0, field.SelectionLength);
    }

    /// <summary>Verifies a disabled ancestor removes its subtree from pointer and tab input.</summary>
    [Fact]
    public void DisabledAncestor_SuppressesPointerAndTabInput()
    {
        var root = new Panel(Color.Black, 100f, 100f);
        var disabled = new Panel(Color.Black, 100f, 100f) { IsEnabled = false };
        var button = new Button(100f, 100f, "Disabled", Color.Black);
        var clicks = 0;
        button.Click += () => clicks++;
        root.AddChild(disabled);
        disabled.AddChild(button);
        var router = new UIEventRouter(root, () => { });

        router.MovePointer(new Vector2(50f, 50f));
        router.Press();
        router.Release(invokeClick: true);
        router.RouteKey(new KeyInputEvent(InputKey.Tab, true, false, InputModifiers.None));

        Assert.Same(root, router.HoveredElement);
        Assert.Equal(0, clicks);
        Assert.Same(root, router.FocusedElement);
    }

    /// <summary>Verifies the topmost modal confines pointer and sequential focus input.</summary>
    [Fact]
    public void VisibleModal_ConfinesPointerAndTabFocusToModalSubtree()
    {
        var root = new Panel(Color.Black, 200f, 100f);
        var underlying = new Button(200f, 100f, "Underlying", Color.Black);
        var modal = new Modal(200f, 100f, 100f, 60f);
        var modalButton = new Button(40f, 20f, "Modal", Color.Black);
        var underlyingClicks = 0;
        underlying.Click += () => underlyingClicks++;
        modal.Dialog.AddChild(modalButton);
        root.AddChild(underlying);
        root.AddChild(modal);
        var router = new UIEventRouter(root, () => { });

        router.MovePointer(new Vector2(10f, 10f));
        router.Press();
        router.Release(invokeClick: true);
        router.RouteKey(new KeyInputEvent(InputKey.Tab, true, false, InputModifiers.None));

        Assert.Equal(0, underlyingClicks);
        Assert.Same(modalButton, router.FocusedElement);
    }

    /// <summary>Verifies commands route from their target toward the active scope root.</summary>
    [Fact]
    public void ExecuteCommand_UsesFirstEnabledBindingOnBubbleRoute()
    {
        var command = new UICommand("Save");
        var root = new Panel(Color.Black, 100f, 100f);
        var parent = new Panel(Color.Black, 100f, 100f);
        var target = new TextField(100f, 30f);
        var executed = string.Empty;
        target.CommandBindings.Add(new UICommandBinding(
            command, _ => executed = "target", args => args.CanExecute = false));
        parent.CommandBindings.Add(new UICommandBinding(
            command, args => executed = $"parent:{args.Parameter}"));
        root.AddChild(parent);
        parent.AddChild(target);
        var router = new UIEventRouter(root, () => { });
        router.Focus(target);

        var handled = router.ExecuteCommand(command, "scene");

        Assert.True(handled);
        Assert.Equal("parent:scene", executed);
    }

    /// <summary>Verifies root accelerators execute even when the host has no focused child.</summary>
    [Fact]
    public void RouteKey_UnfocusedRoot_ExecutesGlobalKeyBinding()
    {
        var command = new UICommand("Save");
        var root = new Panel(Color.Black, 100f, 100f);
        var executions = 0;
        root.KeyBindings.Add(new UIKeyBinding(
            new UIKeyGesture(InputKey.S, InputModifiers.Control), command));
        root.CommandBindings.Add(new UICommandBinding(command, _ => executions++));
        var router = new UIEventRouter(root, () => { });

        router.RouteKey(new KeyInputEvent(
            InputKey.S, true, false, InputModifiers.Control));

        Assert.Equal(1, executions);
    }

    /// <summary>Verifies keyboard input follows preview, target, and bubble routing.</summary>
    [Fact]
    public void RouteKey_FocusedChild_RunsStableRouteInOrder()
    {
        var root = new Panel(Color.Black, 100f, 100f) { Name = "Root" };
        var target = new TextField(100f, 30f) { Name = "Target" };
        var calls = new List<string>();
        root.AddChild(target);
        root.PreviewKey += (sender, keyEvent) => calls.Add($"{sender.Name}:{keyEvent.RoutePhase}");
        target.PreviewKey += (sender, keyEvent) => calls.Add($"{sender.Name}:{keyEvent.RoutePhase}");
        target.Key += (sender, keyEvent) => calls.Add($"{sender.Name}:{keyEvent.RoutePhase}");
        root.Key += (sender, keyEvent) => calls.Add($"{sender.Name}:{keyEvent.RoutePhase}");
        var router = new UIEventRouter(root, () => { });
        router.Focus(target);

        router.RouteKey(new KeyInputEvent(InputKey.A, true, false, InputModifiers.Control));

        Assert.Equal(
            ["Root:Preview", "Target:Preview", "Target:Target", "Root:Bubble"],
            calls);
    }

    /// <summary>Verifies handled routed text suppresses legacy text editing.</summary>
    [Fact]
    public void RouteText_WhenHandled_DoesNotInvokeCompatibilityTextInput()
    {
        var root = new Panel(Color.Black, 100f, 100f);
        var target = new TextField(100f, 30f);
        root.AddChild(target);
        root.PreviewTextInput += (_, textEvent) =>
        {
            Assert.Equal("hello", textEvent.Text);
            textEvent.Handled = true;
        };
        var router = new UIEventRouter(root, () => { });
        router.Focus(target);

        router.RouteText("hello");

        Assert.Equal(string.Empty, target.Text);
    }

    /// <summary>Verifies Tab and Shift+Tab traverse visible tab stops by tab index.</summary>
    [Fact]
    public void RouteKey_Tab_TraversesTabStopsInBothDirections()
    {
        var root = new Panel(Color.Black, 100f, 100f);
        var second = new Button(20f, 20f, "Second", Color.Black) { TabIndex = 2 };
        var first = new Button(20f, 20f, "First", Color.Black) { TabIndex = 1 };
        var hidden = new Button(20f, 20f, "Hidden", Color.Black) { TabIndex = 0, IsVisible = false };
        root.AddChild(second);
        root.AddChild(first);
        root.AddChild(hidden);
        var router = new UIEventRouter(root, () => { });

        router.RouteKey(new KeyInputEvent(InputKey.Tab, true, false, InputModifiers.None));
        Assert.Same(first, router.FocusedElement);

        router.RouteKey(new KeyInputEvent(InputKey.Tab, true, false, InputModifiers.None));
        Assert.Same(second, router.FocusedElement);

        router.RouteKey(new KeyInputEvent(InputKey.Tab, true, false, InputModifiers.Shift));
        Assert.Same(first, router.FocusedElement);
    }

    /// <summary>Verifies preview, target, and bubble handlers run in routed order.</summary>
    [Fact]
    public void Press_RoutedHandlers_RunInPreviewTargetBubbleOrder()
    {
        var root = new Panel(Color.Black, 100f, 100f) { Name = "Root" };
        var parent = new Panel(Color.Black, 100f, 100f) { Name = "Parent" };
        var target = new Button(100f, 100f, "Target", Color.Black) { Name = "Target" };
        var calls = new List<string>();
        root.AddChild(parent);
        parent.AddChild(target);
        root.PreviewPointer += (sender, pointerEvent) => calls.Add($"{sender.Name}:{pointerEvent.RoutePhase}");
        parent.PreviewPointer += (sender, pointerEvent) => calls.Add($"{sender.Name}:{pointerEvent.RoutePhase}");
        target.PreviewPointer += (sender, pointerEvent) => calls.Add($"{sender.Name}:{pointerEvent.RoutePhase}");
        target.Pointer += (sender, pointerEvent) => calls.Add($"{sender.Name}:{pointerEvent.RoutePhase}");
        parent.Pointer += (sender, pointerEvent) => calls.Add($"{sender.Name}:{pointerEvent.RoutePhase}");
        root.Pointer += (sender, pointerEvent) => calls.Add($"{sender.Name}:{pointerEvent.RoutePhase}");
        var router = new UIEventRouter(root, () => { });

        router.Press(new PointerButtonEvent(
            7, new Vector2(50f, 50f), InputPointerButton.Primary, true, 1,
            PointerDeviceKind.Mouse, InputModifiers.Control, PointerButtons.Primary));

        Assert.Equal(
            ["Root:Preview", "Parent:Preview", "Target:Preview", "Target:Target", "Parent:Bubble", "Root:Bubble"],
            calls);
    }

    /// <summary>Verifies handled preview input stops later routing and compatible press behavior.</summary>
    [Fact]
    public void Press_WhenPreviewHandled_StopsRouteAndCompatibilityBehavior()
    {
        var root = new Panel(Color.Black, 100f, 100f);
        var target = new Button(100f, 100f, "Target", Color.Black);
        var targetCalls = 0;
        root.AddChild(target);
        root.PreviewPointer += (_, pointerEvent) => pointerEvent.Handled = true;
        target.Pointer += (_, _) => targetCalls++;
        var router = new UIEventRouter(root, () => { });

        router.MovePointer(new Vector2(50f, 50f));
        router.Press();

        Assert.Equal(0, targetCalls);
        Assert.False(target.IsPressed);
    }

    /// <summary>Verifies elements detached by an earlier handler are skipped from a snapshotted route.</summary>
    [Fact]
    public void Press_WhenPreviewDetachesBranch_SkipsDetachedRouteElements()
    {
        var root = new Panel(Color.Black, 100f, 100f);
        var parent = new Panel(Color.Black, 100f, 100f);
        var target = new Button(100f, 100f, "Target", Color.Black);
        var calls = new List<string>();
        root.AddChild(parent);
        parent.AddChild(target);
        root.PreviewPointer += (_, _) =>
        {
            calls.Add("root");
            root.RemoveChild(parent);
        };
        parent.PreviewPointer += (_, _) => calls.Add("parent");
        target.Pointer += (_, _) => calls.Add("target");
        var router = new UIEventRouter(root, () => { });

        router.Press(new PointerButtonEvent(
            0, new Vector2(50f, 50f), InputPointerButton.Primary, true, 1,
            PointerDeviceKind.Mouse, InputModifiers.None, PointerButtons.Primary));

        Assert.Equal(["root"], calls);
    }

    /// <summary>Verifies nested dispatch uses independent reusable event arguments.</summary>
    [Fact]
    public void Press_WhenHandlerDispatchesNestedEvent_PreservesOuterEventData()
    {
        var root = new Panel(Color.Black, 100f, 100f);
        var router = new UIEventRouter(root, () => { });
        var kinds = new List<UIPointerEventKind>();
        var nested = false;
        root.PreviewPointer += (_, pointerEvent) =>
        {
            if (nested)
                return;
            nested = true;
            kinds.Add(pointerEvent.Kind);
            router.Scroll(new PointerWheelEvent(
                0, new Vector2(50f, 50f), new Vector2(0f, 1f), InputModifiers.None));
            kinds.Add(pointerEvent.Kind);
        };
        root.Pointer += (_, pointerEvent) => kinds.Add(pointerEvent.Kind);

        router.Press(new PointerButtonEvent(
            0, new Vector2(50f, 50f), InputPointerButton.Primary, true, 1,
            PointerDeviceKind.Mouse, InputModifiers.None, PointerButtons.Primary));

        Assert.Equal(
            [UIPointerEventKind.Press, UIPointerEventKind.Wheel, UIPointerEventKind.Press, UIPointerEventKind.Press],
            kinds);
    }

    /// <summary>Verifies that the topmost child receives pointer state.</summary>
    [Fact]
    public void MovePointer_OverlappingChildren_HoversTopmostChild()
    {
        var root = new Panel(Color.Black, 100f, 100f);
        var first = new Panel(Color.Black, 100f, 100f);
        var second = new Panel(Color.Black, 100f, 100f);
        root.AddChild(first);
        root.AddChild(second);
        var router = new UIEventRouter(root, () => { });

        router.MovePointer(new(50f, 50f));

        Assert.Same(second, router.HoveredElement);
        Assert.True(second.IsHovered);
        Assert.False(first.IsHovered);
    }

    /// <summary>Verifies that a press and release dispatches one click.</summary>
    [Fact]
    public void Release_AfterPress_InvokesClick()
    {
        var button = new Button(100f, 100f, "Test", Color.Black);
        var clicks = 0;
        button.Click += () => clicks++;
        var router = new UIEventRouter(button, () => { });
        router.MovePointer(new(50f, 50f));
        router.Press();

        router.Release(invokeClick: true);

        Assert.Equal(1, clicks);
        Assert.False(button.IsPressed);
    }

    /// <summary>Verifies releasing outside a pressed element clears its captured press state.</summary>
    [Fact]
    public void Release_AfterPointerLeavesPressedElement_ClearsOriginalPressWithoutClick()
    {
        var root = new Panel(Color.Black, 200f, 100f);
        var button = new Button(100f, 100f, "Test", Color.Black);
        var clicks = 0;
        button.Click += () => clicks++;
        root.AddChild(button);
        var router = new UIEventRouter(root, () => { });
        router.MovePointer(new(50f, 50f));
        router.Press();

        router.MovePointer(new(150f, 50f));
        router.Release(invokeClick: true);

        Assert.False(button.IsPressed);
        Assert.Equal(0, clicks);

        router.MovePointer(new(50f, 50f));
        router.Press();
        Assert.True(button.IsPressed);
    }

    /// <summary>Verifies explicit capture keeps pointer actions targeted outside the element bounds.</summary>
    [Fact]
    public void Release_WithExplicitCapture_ClicksCapturedElement()
    {
        var root = new Panel(Color.Black, 200f, 100f);
        var button = new Button(100f, 100f, "Test", Color.Black);
        root.AddChild(button);
        var clicks = 0;
        button.Click += () => clicks++;
        var router = new UIEventRouter(root, () => { });
        router.MovePointer(new(50f, 50f));
        router.CapturePointer(button);
        router.Press();

        router.MovePointer(new(150f, 50f));
        router.Release(invokeClick: true);

        Assert.Equal(1, clicks);
        Assert.Same(button, router.CapturedElement);
    }

    /// <summary>Verifies removing a captured element releases capture on the next pointer event.</summary>
    [Fact]
    public void MovePointer_AfterCapturedElementRemoved_RaisesCaptureLost()
    {
        var root = new Panel(Color.Black, 200f, 100f);
        var button = new Button(100f, 100f, "Test", Color.Black);
        root.AddChild(button);
        var captureLost = 0;
        button.PointerCaptureLost += () => captureLost++;
        var router = new UIEventRouter(root, () => { });
        router.CapturePointer(button);

        root.RemoveChild(button);
        router.MovePointer(new(150f, 50f));

        Assert.Null(router.CapturedElement);
        Assert.Equal(1, captureLost);
    }

    /// <summary>Counts retained paint rebuilds for one text field.</summary>
    private sealed class PaintCountingTextField : TextField
    {
        /// <summary>Creates the test field.</summary>
        /// <param name="width">Field width.</param>
        /// <param name="height">Field height.</param>
        internal PaintCountingTextField(float width, float height) : base(width, height)
        {
        }

        /// <summary>Gets the number of local paint rebuilds.</summary>
        internal int PaintCount { get; private set; }

        /// <inheritdoc/>
        protected override void Paint(UIDrawList drawList)
        {
            PaintCount++;
            base.Paint(drawList);
        }
    }

    /// <summary>Provides deterministic UTF-16-unit widths for editing-window tests.</summary>
    private sealed class CodeUnitTextLayoutService : ITextLayoutService
    {
        /// <inheritdoc/>
        public float MeasureWidth(ReadOnlySpan<char> text, float fontSize) => text.Length * 10f;

        /// <inheritdoc/>
        public int HitTestCaret(
            ReadOnlySpan<char> text,
            float fontSize,
            float horizontalPosition) =>
            Math.Clamp((int)MathF.Round(horizontalPosition / 10f), 0, text.Length);
    }
}

/// <summary>Stores clipboard text in memory for routed editing tests.</summary>
internal sealed class TestClipboard : IClipboardService
{
    /// <summary>Gets or sets retained test text.</summary>
    public string? Text { get; set; }

    /// <inheritdoc/>
    public string? GetText() => Text;

    /// <inheritdoc/>
    public void SetText(string text) => Text = text;
}

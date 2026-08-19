using System.ComponentModel;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class UIFoundationTests
{
    /// <summary>Verifies data context inherits, respects local overrides, and follows detachment.</summary>
    [Fact]
    public void DataContext_Inheritance_PropagatesEffectiveChanges()
    {
        var first = new ObservableModel("First");
        var second = new ObservableModel("Second");
        var local = new ObservableModel("Local");
        var root = new Panel(Color.Black) { DataContext = first };
        var child = new Panel(Color.Black);
        var grandchild = new Label("Value");
        child.AddChild(grandchild);
        var changes = 0;
        grandchild.DataContextChanged += _ => changes++;

        root.AddChild(child);
        Assert.Same(first, grandchild.DataContext);
        child.DataContext = local;
        root.DataContext = second;
        Assert.Same(local, grandchild.DataContext);

        child.ClearDataContext();
        Assert.Same(second, grandchild.DataContext);
        root.RemoveChild(child);

        Assert.Null(grandchild.DataContext);
        Assert.Equal(4, changes);
    }

    /// <summary>Verifies nearest resource scope wins and typed style lookup includes named variants.</summary>
    [Fact]
    public void Resources_AndTypedStyle_ResolveThroughRetainedAncestry()
    {
        var root = new Panel(Color.Black);
        root.Resources.Set("spacing", new ResourceBox(4f));
        root.Resources.SetStyle(new UIStyle<Button>()
            .Add(button => button.Padding = new Thickness(12f, 3f))
            .Add(button => button.ForegroundColor = Color.Yellow), "primary");
        root.Resources.SetStyle(new UIStyle<Label>()
            .Add(label => label.TextStyle = new UITextStyle(13f, Color.Cyan)), "caption");
        var branch = new Panel(Color.Black);
        branch.Resources.Set("spacing", new ResourceBox(8f));
        var button = new Button(100f, 30f, "Apply") { StyleKey = "primary" };
        var label = new Label("Status") { StyleKey = "caption" };
        root.AddChild(branch);
        branch.AddChild(button);
        branch.AddChild(label);

        Assert.True(button.TryFindResource("spacing", out ResourceBox? resource));
        Assert.Equal(8f, resource!.Value);
        Assert.True(button.ApplyStyle());
        Assert.Equal(new Thickness(12f, 3f), button.Padding);
        Assert.Equal(Color.Yellow, button.ForegroundColor);
        Assert.True(label.ApplyStyle());
        Assert.Equal(new UITextStyle(13f, Color.Cyan), label.TextStyle);
    }

    /// <summary>Verifies control templates replace their owned visual root deterministically.</summary>
    [Fact]
    public void ControlTemplate_Reapply_ReplacesOwnedVisualRoot()
    {
        var control = new Control(100f, 30f);
        var template = new UIControlTemplate<Control>(_ => new Label("Presentation", 100f, 30f));
        control.Template = template;
        var first = Assert.IsType<Label>(control.TemplateRoot);

        Assert.True(control.ApplyTemplate());
        var second = Assert.IsType<Label>(control.TemplateRoot);
        control.BuildDrawList();

        Assert.NotSame(first, second);
        Assert.Null(first.Parent);
        Assert.Null(first.LogicalParent);
        Assert.Same(control, second.Parent);
        Assert.Same(control, second.VisualParent);
        Assert.Null(second.LogicalParent);
        Assert.Empty(control.LogicalChildren);
        Assert.Equal(100f, second.Width);
        Assert.Contains(control.BuildDrawList().Commands,
            command => command.Type == UIDrawCommandType.Text && command.Text == "Presentation");
    }

    /// <summary>Verifies ordinary child attachment establishes both ancestry relations atomically.</summary>
    [Fact]
    public void Child_Reparenting_TransfersVisualAndLogicalOwnership()
    {
        var first = new Panel(Color.Black);
        var second = new Panel(Color.Black);
        var child = new Label("Owned");
        first.AddChild(child);

        Assert.Same(first, child.VisualParent);
        Assert.Same(first, child.LogicalParent);
        Assert.Contains(child, first.VisualChildren);
        Assert.Contains(child, first.LogicalChildren);

        second.AddChild(child);

        Assert.DoesNotContain(child, first.VisualChildren);
        Assert.DoesNotContain(child, first.LogicalChildren);
        Assert.Same(second, child.VisualParent);
        Assert.Same(second, child.LogicalParent);
    }

    /// <summary>Verifies scene-graph references preserve UI ownership through virtual child mutation.</summary>
    [Fact]
    public void Child_Attachment_ThroughNodeReference_PreservesBothTrees()
    {
        Engine.Core.Node owner = new Panel(Color.Black);
        var child = new Label("Polymorphic");

        owner.AddChild(child);

        Assert.Same(owner, child.VisualParent);
        Assert.Same(owner, child.LogicalParent);
    }

    /// <summary>Verifies detached logical content inherits data and resources from its owner.</summary>
    [Fact]
    public void LogicalOnlyChild_InheritsOwnerStateWithoutEnteringVisualTree()
    {
        var model = new ObservableModel("Logical");
        var owner = new OwnershipProbe { DataContext = model };
        owner.Resources.Set("token", new ResourceBox(9f));
        var child = new Label("Detached");

        owner.AttachLogical(child);

        Assert.Null(child.VisualParent);
        Assert.Same(owner, child.LogicalParent);
        Assert.Empty(owner.VisualChildren);
        Assert.Same(model, child.DataContext);
        Assert.True(child.TryFindResource("token", out ResourceBox? resource));
        Assert.Equal(9f, resource!.Value);
    }

    /// <summary>Verifies generated item containers are logical content presented by a visual-only panel.</summary>
    [Fact]
    public void ItemsControl_SeparatesPresenterAndContainerOwnership()
    {
        var items = new TestItemsControl();
        items.SetItems(["One", "Two"]);
        var first = items.Containers[0];

        Assert.Same(items, items.Presenter.VisualParent);
        Assert.Null(items.Presenter.LogicalParent);
        Assert.Same(items.Presenter, first.VisualParent);
        Assert.Same(items, first.LogicalParent);
        Assert.Equal(2, items.LogicalChildren.Count);
        Assert.Single(items.VisualChildren);
    }

    /// <summary>Verifies typed data templates reject mismatched data and build retained content.</summary>
    [Fact]
    public void DataTemplate_BuildsTypedRetainedContent()
    {
        IUIDataTemplate template = new UIDataTemplate<ObservableModel>(
            model => new Label(model.Value));

        var label = Assert.IsType<Label>(template.Build(new ObservableModel("Scene")));

        Assert.Equal("Scene", label.Text);
        Assert.Throws<ArgumentException>(() => template.Build("wrong"));
    }

    /// <summary>Verifies one-way binding filters properties and releases model subscription on disposal.</summary>
    [Fact]
    public void Binding_OneWay_UpdatesAndDetaches()
    {
        var model = new ObservableModel("Initial");
        var label = new Label(string.Empty);
        using var binding = UIBinding.Bind(
            model, label, nameof(ObservableModel.Value),
            source => source.Value, value => label.Text = value);
        Assert.Equal("Initial", label.Text);

        model.Value = "Updated";
        Assert.Equal("Updated", label.Text);
        binding.Dispose();
        model.Value = "Detached";

        Assert.Equal("Updated", label.Text);
    }

    /// <summary>Verifies inherited-context binding detaches an old model and attaches the replacement.</summary>
    [Fact]
    public void Binding_DataContext_RebindsWithoutLeakingOldSource()
    {
        var first = new ObservableModel("First");
        var second = new ObservableModel("Second");
        var root = new Panel(Color.Black) { DataContext = first };
        var label = new Label(string.Empty);
        root.AddChild(label);
        using var binding = UIBinding.BindDataContext<ObservableModel, string>(
            label, nameof(ObservableModel.Value), source => source.Value,
            value => label.Text = value);
        Assert.Equal("First", label.Text);

        root.DataContext = second;
        first.Value = "Stale";
        Assert.Equal("Second", label.Text);
        second.Value = "Current";

        Assert.Equal("Current", label.Text);
    }

    /// <summary>Verifies two-way binding suppresses feedback while updating both endpoints.</summary>
    [Fact]
    public void Binding_TwoWay_UpdatesBothDirectionsWithoutFeedback()
    {
        var model = new ObservableModel("Model");
        var target = new ObservableTarget();
        using var binding = UIBinding.Bind(
            model, target, nameof(ObservableModel.Value), source => source.Value,
            target.SetFromBinding, UIBindingMode.TwoWay,
            (source, value) => source.Value = value,
            handler => target.ValueChanged += handler,
            handler => target.ValueChanged -= handler);
        Assert.Equal("Model", target.Value);

        target.SetFromUser("Target");
        Assert.Equal("Target", model.Value);
        model.Value = "ModelAgain";

        Assert.Equal("ModelAgain", target.Value);
        Assert.Equal(2, target.BindingWriteCount);
    }

    /// <summary>Stores one resource value for typed lookup assertions.</summary>
    /// <param name="Value">Resource scalar.</param>
    private sealed record ResourceBox(float Value);

    /// <summary>Minimal observable application model used by binding tests.</summary>
    private sealed class ObservableModel : INotifyPropertyChanged
    {
        private string _value;

        /// <summary>Creates a model with one value.</summary>
        /// <param name="value">Initial value.</param>
        public ObservableModel(string value) => _value = value;

        /// <summary>Gets or sets the observable value.</summary>
        public string Value
        {
            get => _value;
            set
            {
                if (_value == value)
                    return;
                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        /// <inheritdoc/>
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Retained target exposing a value-change event for two-way binding.</summary>
    private sealed class ObservableTarget : UIElement
    {
        /// <summary>Gets the current target value.</summary>
        public string Value { get; private set; } = string.Empty;

        /// <summary>Gets the number of writes originating from the binding.</summary>
        public int BindingWriteCount { get; private set; }

        /// <summary>Occurs when a user changes the target value.</summary>
        public event Action<string>? ValueChanged;

        /// <summary>Applies one value from the binding.</summary>
        /// <param name="value">Bound model value.</param>
        public void SetFromBinding(string value)
        {
            Value = value;
            BindingWriteCount++;
            ValueChanged?.Invoke(value);
        }

        /// <summary>Simulates one user-originated target change.</summary>
        /// <param name="value">New target value.</param>
        public void SetFromUser(string value)
        {
            Value = value;
            ValueChanged?.Invoke(value);
        }
    }

    /// <summary>Exposes protected ownership composition for ancestry contract tests.</summary>
    private sealed class OwnershipProbe : UIElement
    {
        /// <summary>Attaches one logical-only child.</summary>
        /// <param name="child">Child to own without rendering.</param>
        public void AttachLogical(UIElement child) => AddLogicalChild(child);
    }

    /// <summary>Exposes the generated-items presenter for ownership assertions.</summary>
    private sealed class TestItemsControl : ItemsControl<string>
    {
        /// <summary>Gets the visual-only generated-items presenter.</summary>
        public UIElement Presenter => ItemsPresenter;
    }
}

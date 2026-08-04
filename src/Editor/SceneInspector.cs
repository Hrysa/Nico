using System.Globalization;
using System.Numerics;
using Engine.Core;
using Engine.Graphics;
using Engine.UI;

namespace Editor;

/// <summary>
/// Displays and edits properties of the selected authored scene node.
/// </summary>
public sealed class SceneInspector : Panel
{
    private readonly UITheme _theme;
    private readonly List<Func<bool>> _refreshBindings = new();

    /// <summary>Gets the node currently displayed by the Inspector.</summary>
    public Node? InspectedNode { get; private set; }

    /// <summary>Occurs after an Inspector field changes the selected node.</summary>
    public event Action<Node>? NodeChanged;

    /// <summary>Occurs after the Inspector changes the selected node's displayed name.</summary>
    public event Action<Node>? NodeNameChanged;

    /// <summary>
    /// Creates an empty scene Inspector.
    /// </summary>
    /// <param name="width">Inspector content width.</param>
    /// <param name="height">Inspector content height.</param>
    /// <param name="theme">Theme supplying Inspector visuals.</param>
    public SceneInspector(float width, float height, UITheme? theme = null)
        : base((theme ?? UITheme.Dark).Surface, width, height)
    {
        _theme = theme ?? UITheme.Dark;
        PaintBackground = false;
        Bind(null);
    }

    /// <summary>
    /// Rebuilds Inspector fields for a selected scene node.
    /// </summary>
    /// <param name="node">Selected authored node, or null.</param>
    public void Bind(Node? node)
    {
        InspectedNode = node;
        ClearChildren();
        _refreshBindings.Clear();
        if (node is null)
        {
            AddChild(CreateLabel(12f, 12f, Width - 24f, 28f,
                "Select an object to inspect", _theme.TextMuted));
            return;
        }

        AddChild(CreateLabel(12f, 8f, Width - 24f, 24f,
            node.GetType().Name, _theme.TextSecondary));
        AddChild(CreateLabel(12f, 40f, 58f, 30f, "Name", _theme.TextSecondary));
        var nameField = new TextField(Width - 84f, 30f, _theme)
        {
            Name = "NameField",
            Text = node.Name,
            Margin = new Thickness(72f, 40f, 0f, 0f)
        };
        nameField.TextChanged += value =>
        {
            node.Name = value;
            NodeChanged?.Invoke(node);
            NodeNameChanged?.Invoke(node);
        };
        RegisterRefresh(nameField, () => node.Name);
        AddChild(nameField);

        AddChild(CreateLabel(12f, 82f, Width - 24f, 26f,
            "Transform", _theme.TextPrimary));
        AddVectorRow("Position", "Position", 112f, () => node.Position,
            value => node.Position = value, radiansAsDegrees: false);
        AddVectorRow("Rotation", "Rotation", 150f, () => node.Rotation,
            value => node.Rotation = value, radiansAsDegrees: true);
        AddVectorRow("Scale", "Scale", 188f, () => node.Scale,
            value => node.Scale = value, radiansAsDegrees: false);

        AddChild(CreateLabel(12f, 236f, Width - 24f, 26f,
            "Script", _theme.TextPrimary));
        var scriptField = new TextField(Width - 24f, 30f, _theme)
        {
            Name = "ScriptTypeField",
            Text = node.ScriptType ?? string.Empty,
            Placeholder = "No script attached",
            Margin = new Thickness(12f, 266f, 0f, 0f)
        };
        scriptField.TextChanged += value =>
        {
            node.ScriptType = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            NodeChanged?.Invoke(node);
        };
        RegisterRefresh(scriptField, () => node.ScriptType ?? string.Empty);
        AddChild(scriptField);
    }

    /// <summary>
    /// Refreshes non-focused fields from the latest selected-node state.
    /// </summary>
    /// <returns>True when at least one displayed value changed.</returns>
    public bool RefreshValues()
    {
        var changed = false;
        foreach (var refresh in _refreshBindings)
            changed |= refresh();
        return changed;
    }

    /// <summary>Attaches a resolved game-script type to the currently inspected node.</summary>
    /// <param name="scriptType">Fully qualified game-script type name.</param>
    /// <returns>True when an inspected node received the script type.</returns>
    public bool AttachScriptType(string scriptType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptType);
        if (InspectedNode is not { } node)
            return false;
        var resolvedType = scriptType.Trim();
        node.ScriptType = resolvedType;
        var field = Children.OfType<TextField>()
            .FirstOrDefault(element => element.Name == "ScriptTypeField");
        if (field is not null)
            field.Text = resolvedType;
        NodeChanged?.Invoke(node);
        return true;
    }

    /// <summary>
    /// Adds a three-component vector editor row.
    /// </summary>
    /// <param name="label">Row label.</param>
    /// <param name="namePrefix">Prefix assigned to field names.</param>
    /// <param name="y">Local row position.</param>
    /// <param name="read">Callback returning the latest vector value.</param>
    /// <param name="apply">Callback receiving valid edited values.</param>
    /// <param name="radiansAsDegrees">Whether displayed values convert radians to degrees.</param>
    private void AddVectorRow(
        string label,
        string namePrefix,
        float y,
        Func<Vector3> read,
        Action<Vector3> apply,
        bool radiansAsDegrees)
    {
        const float labelWidth = 66f;
        const float spacing = 4f;
        var availableWidth = MathF.Max(0f, Width - 24f - labelWidth);
        var fieldWidth = MathF.Floor((availableWidth - spacing * 2f) / 3f);
        var initialValue = read();
        var displayValue = radiansAsDegrees
            ? initialValue * (180f / MathF.PI) : initialValue;
        AddChild(CreateLabel(12f, y, labelWidth, 30f, label, _theme.TextSecondary));

        var fields = new TextField[3];
        for (var index = 0; index < fields.Length; index++)
        {
            var componentIndex = index;
            var field = new TextField(fieldWidth, 30f, _theme)
            {
                Name = $"{namePrefix}{"XYZ"[index]}",
                Text = Format(GetComponent(displayValue, index)),
                Margin = new Thickness(12f + labelWidth + index * (fieldWidth + spacing),
                    y, 0f, 0f)
            };
            field.TextChanged += text =>
            {
                if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var component))
                    return;
                var edited = read();
                var internalComponent = radiansAsDegrees
                    ? component * MathF.PI / 180f : component;
                edited = WithComponent(edited, componentIndex, internalComponent);
                apply(edited);
                if (InspectedNode is { } inspectedNode)
                    NodeChanged?.Invoke(inspectedNode);
            };
            RegisterRefresh(field, () =>
            {
                var latest = GetComponent(read(), componentIndex);
                if (radiansAsDegrees)
                    latest *= 180f / MathF.PI;
                return Format(latest);
            });
            fields[index] = field;
            AddChild(field);
        }
    }

    /// <summary>Adds a non-destructive field refresh binding.</summary>
    /// <param name="field">Text field to update while it is not focused.</param>
    /// <param name="read">Callback returning current display text.</param>
    private void RegisterRefresh(TextField field, Func<string> read)
    {
        _refreshBindings.Add(() =>
        {
            if (field.IsFocused)
                return false;
            var latest = read();
            if (field.Text == latest)
                return false;
            field.Text = latest;
            return true;
        });
    }

    /// <summary>Reads one vector component by zero-based index.</summary>
    /// <param name="value">Source vector.</param>
    /// <param name="index">Component index.</param>
    /// <returns>The selected component.</returns>
    private static float GetComponent(Vector3 value, int index)
    {
        return index switch
        {
            0 => value.X,
            1 => value.Y,
            2 => value.Z,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    /// <summary>Returns a vector with one component replaced.</summary>
    /// <param name="value">Source vector.</param>
    /// <param name="index">Component index.</param>
    /// <param name="component">Replacement component.</param>
    /// <returns>The edited vector.</returns>
    private static Vector3 WithComponent(Vector3 value, int index, float component)
    {
        return index switch
        {
            0 => value with { X = component },
            1 => value with { Y = component },
            2 => value with { Z = component },
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    /// <summary>Formats a component compactly using culture-independent text.</summary>
    /// <param name="value">Component value.</param>
    /// <returns>Editable numeric text.</returns>
    private static string Format(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Creates one consistently styled Inspector label.</summary>
    /// <param name="x">Local X position.</param>
    /// <param name="y">Local Y position.</param>
    /// <param name="width">Label width.</param>
    /// <param name="height">Label height.</param>
    /// <param name="text">Displayed text.</param>
    /// <param name="color">Text color.</param>
    /// <returns>The configured label.</returns>
    private Label CreateLabel(
        float x,
        float y,
        float width,
        float height,
        string text,
        Color color)
    {
        return new Label(text, width, height)
        {
            FontSize = _theme.FontSize,
            ForegroundColor = color,
            PaddingLeft = 0f,
            Margin = new Thickness(x, y, 0f, 0f)
        };
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        foreach (var child in Children.OfType<UIElement>())
        {
            child.Measure(contentSize);
            child.Arrange(Vector2.Zero, child.DesiredSize);
        }
    }
}

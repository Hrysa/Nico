namespace Engine.UI;

/// <summary>Identifies the accessible purpose of a retained UI element.</summary>
public enum UISemanticRole
{
    /// <summary>Element without a more specific accessible purpose.</summary>
    Generic,

    /// <summary>Static text content.</summary>
    Text,

    /// <summary>Invokable command button.</summary>
    Button,

    /// <summary>Persistent two-state button.</summary>
    ToggleButton,

    /// <summary>Independent boolean check box.</summary>
    CheckBox,

    /// <summary>Mutually exclusive radio option.</summary>
    RadioButton,

    /// <summary>Boolean switch.</summary>
    Switch,

    /// <summary>Bounded numeric slider.</summary>
    Slider,

    /// <summary>Read-only progress indicator.</summary>
    ProgressBar,

    /// <summary>Expandable single-selection choice control.</summary>
    ComboBox,

    /// <summary>Selectable list.</summary>
    List,

    /// <summary>Selectable list row.</summary>
    ListItem,

    /// <summary>Hierarchical selectable tree.</summary>
    Tree,

    /// <summary>Expandable hierarchical row.</summary>
    TreeItem,

    /// <summary>Tab collection.</summary>
    TabList,

    /// <summary>Menu surface.</summary>
    Menu,

    /// <summary>Invokable menu row.</summary>
    MenuItem,

    /// <summary>Decorative or informative image.</summary>
    Image,

    /// <summary>Modal dialog surface.</summary>
    Dialog,

    /// <summary>Grouped command toolbar.</summary>
    ToolBar,

    /// <summary>Visual or semantic group separator.</summary>
    Separator,

    /// <summary>Editable text field.</summary>
    TextField,

    /// <summary>Protected editable text field.</summary>
    PasswordField
}

/// <summary>Identifies actions an accessibility adapter can request from a retained element.</summary>
[Flags]
public enum UISemanticAction
{
    /// <summary>No semantic action is supported.</summary>
    None = 0,

    /// <summary>Invokes the element's primary action.</summary>
    Invoke = 1 << 0,

    /// <summary>Changes a boolean checked state.</summary>
    Toggle = 1 << 1,

    /// <summary>Moves to the next numeric or selectable value.</summary>
    Increment = 1 << 2,

    /// <summary>Moves to the previous numeric or selectable value.</summary>
    Decrement = 1 << 3,

    /// <summary>Expands or collapses owned content.</summary>
    ExpandCollapse = 1 << 4,

    /// <summary>Selects the represented item.</summary>
    Select = 1 << 5,

    /// <summary>Sets a numeric value supplied by the adapter.</summary>
    SetValue = 1 << 6
}

/// <summary>Describes allocation-free accessibility state for one retained element.</summary>
/// <param name="Role">Element purpose.</param>
/// <param name="Name">Accessible name.</param>
/// <param name="Value">Exposed value, or null for protected content.</param>
/// <param name="IsEnabled">Whether interaction is enabled.</param>
/// <param name="IsReadOnly">Whether the value can be edited.</param>
/// <param name="IsInvalid">Whether validation currently fails.</param>
/// <param name="ValidationMessage">Current validation error.</param>
/// <param name="IsBusy">Whether an asynchronous operation is pending.</param>
/// <param name="Actions">Actions supported by the element.</param>
/// <param name="IsChecked">Optional checked state.</param>
/// <param name="IsSelected">Whether the represented item is selected.</param>
/// <param name="IsExpanded">Optional expanded state.</param>
/// <param name="NumericValue">Optional current numeric value.</param>
/// <param name="Minimum">Optional numeric lower bound.</param>
/// <param name="Maximum">Optional numeric upper bound.</param>
/// <param name="Description">Optional longer accessible description.</param>
public readonly record struct UISemanticInfo(
    UISemanticRole Role,
    string? Name,
    string? Value,
    bool IsEnabled,
    bool IsReadOnly,
    bool IsInvalid,
    string? ValidationMessage,
    bool IsBusy = false,
    UISemanticAction Actions = UISemanticAction.None,
    bool? IsChecked = null,
    bool IsSelected = false,
    bool? IsExpanded = null,
    double? NumericValue = null,
    double? Minimum = null,
    double? Maximum = null,
    string? Description = null);

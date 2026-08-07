using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Identifies the semantic emphasis of a transient notification.</summary>
public enum ToastSeverity
{
    /// <summary>Informational message.</summary>
    Information,
    /// <summary>Successful outcome.</summary>
    Success,
    /// <summary>Warning condition.</summary>
    Warning,
    /// <summary>Error condition.</summary>
    Error
}

/// <summary>Displays one transient notification message.</summary>
public sealed class ToastNotification : Surface
{
    private readonly Label _message;
    private readonly ProgressBar _progressBar;

    /// <summary>Gets the displayed notification text.</summary>
    public string Text { get; private set; }

    /// <summary>Gets the notification severity.</summary>
    public ToastSeverity Severity { get; private set; }

    /// <summary>Gets the optional action button.</summary>
    public Button? ActionButton { get; }

    /// <summary>Gets the button that dismisses this notification.</summary>
    public Button CloseButton { get; }

    /// <summary>Gets the current normalized progress, or null when hidden.</summary>
    public float? Progress { get; private set; }

    /// <summary>Gets whether an animated progress segment is displayed.</summary>
    public bool IsProgressIndeterminate => _progressBar.IsVisible && _progressBar.IsIndeterminate;

    /// <summary>Occurs when an action or close button requests removal.</summary>
    internal event Action? DismissRequested;

    /// <summary>Creates a fixed-width notification surface.</summary>
    /// <param name="text">Displayed message.</param>
    /// <param name="severity">Semantic emphasis.</param>
    /// <param name="theme">Theme supplying surface and text colors.</param>
    /// <param name="actionLabel">Optional action label.</param>
    /// <param name="action">Optional action callback.</param>
    /// <param name="progress">Optional normalized progress.</param>
    /// <param name="isProgressIndeterminate">Whether progress is animated.</param>
    internal ToastNotification(string text, ToastSeverity severity, UITheme theme,
        string? actionLabel, Action? action, float? progress, bool isProgressIndeterminate)
        : base(GetBackground(severity, theme), theme.BorderStrong, 320f, 46f)
    {
        Text = text;
        Severity = severity;
        IsOverlay = true;
        IsHitTestVisible = true;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Padding = new Thickness(10f, 0f);
        _message = new Label(text)
        {
            ForegroundColor = theme.TextPrimary,
            FontSize = theme.CaptionFontSize,
            IsHitTestVisible = false
        };
        AddChild(_message);
        if (action is not null && !string.IsNullOrWhiteSpace(actionLabel))
        {
            ActionButton = new Button(72f, 30f, actionLabel, theme, ButtonStyle.Subtle);
            ActionButton.Click += () =>
            {
                action();
                DismissRequested?.Invoke();
            };
            AddChild(ActionButton);
        }
        CloseButton = new Button(30f, 30f, "×", theme, ButtonStyle.Subtle);
        CloseButton.Click += () => DismissRequested?.Invoke();
        AddChild(CloseButton);
        _progressBar = new ProgressBar(0f, 4f, theme)
        {
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        AddChild(_progressBar);
        SetProgress(progress, isProgressIndeterminate);
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        var progressInset = _progressBar.IsVisible ? 6f : 0f;
        var rowHeight = MathF.Max(0f, contentSize.Y - progressInset);
        var closeX = MathF.Max(0f, contentSize.X - CloseButton.Width);
        CloseButton.Measure(new Vector2(CloseButton.Width, 30f));
        CloseButton.Arrange(new Vector2(closeX, MathF.Max(0f, (rowHeight - 30f) * 0.5f)),
            new Vector2(CloseButton.Width, 30f));
        var actionWidth = ActionButton?.Width ?? 0f;
        if (ActionButton is not null)
        {
            ActionButton.Measure(new Vector2(actionWidth, 30f));
            ActionButton.Arrange(new Vector2(MathF.Max(0f, closeX - actionWidth - 6f),
                MathF.Max(0f, (rowHeight - 30f) * 0.5f)), new Vector2(actionWidth, 30f));
        }
        var messageWidth = MathF.Max(0f, closeX - actionWidth - (ActionButton is null ? 8f : 14f));
        _message.Measure(new Vector2(messageWidth, rowHeight));
        _message.Arrange(Vector2.Zero, new Vector2(messageWidth, rowHeight));
        if (_progressBar.IsVisible)
        {
            _progressBar.Measure(new Vector2(contentSize.X, 4f));
            _progressBar.Arrange(new Vector2(0f, MathF.Max(0f, contentSize.Y - 4f)),
                new Vector2(contentSize.X, 4f));
        }
    }

    /// <summary>Selects a restrained themed background for one severity.</summary>
    /// <param name="severity">Notification severity.</param>
    /// <param name="theme">Active theme.</param>
    /// <returns>Notification background color.</returns>
    private static Color GetBackground(ToastSeverity severity, UITheme theme) => severity switch
    {
        ToastSeverity.Success => Color.Lerp(theme.SurfaceRaised, Color.Green, 0.18f),
        ToastSeverity.Warning => Color.Lerp(theme.SurfaceRaised, Color.Yellow, 0.18f),
        ToastSeverity.Error => Color.Lerp(theme.SurfaceRaised, Color.Red, 0.2f),
        _ => Color.Lerp(theme.SurfaceRaised, theme.Accent, 0.14f)
    };

    /// <summary>Updates retained notification content without replacing its element.</summary>
    /// <param name="text">New message.</param>
    /// <param name="severity">New severity.</param>
    /// <param name="theme">Active theme.</param>
    internal void UpdateContent(string text, ToastSeverity severity, UITheme theme)
    {
        Text = text;
        Severity = severity;
        _message.Text = text;
        BackgroundColor = GetBackground(severity, theme);
        InvalidateMeasure();
    }

    /// <summary>Updates normalized or indeterminate progress presentation.</summary>
    /// <param name="progress">Normalized progress, or null to hide determinate progress.</param>
    /// <param name="isIndeterminate">Whether an animated segment should be displayed.</param>
    internal void SetProgress(float? progress, bool isIndeterminate)
    {
        Progress = progress is { } value ? Math.Clamp(value, 0f, 1f) : null;
        _progressBar.IsIndeterminate = isIndeterminate;
        _progressBar.Value = Progress ?? 0f;
        _progressBar.IsVisible = isIndeterminate || Progress is not null;
        Height = _progressBar.IsVisible ? 58f : 46f;
        InvalidateMeasure();
    }
}

/// <summary>Stacks and expires transient notifications in one host-local overlay.</summary>
public sealed class ToastHost : UIElement
{
    private const float Gap = 8f;
    private const float EdgeInset = 12f;
    private readonly List<ToastEntry> _entries = [];
    private readonly UITheme _theme;
    private int _maxVisible = 5;

    /// <summary>Gets the number of visible notifications.</summary>
    public int Count => _entries.Count;

    /// <summary>Gets or sets the maximum number of retained notifications; oldest entries are discarded first.</summary>
    public int MaxVisible
    {
        get => _maxVisible;
        set
        {
            if (value < 1)
                throw new ArgumentOutOfRangeException(nameof(value));
            _maxVisible = value;
            while (_entries.Count > _maxVisible)
                Dismiss(_entries[0].Notification);
        }
    }

    /// <summary>Gets or sets whether a notification's lifetime pauses while it or a child is hovered.</summary>
    public bool PauseOnHover { get; set; } = true;

    /// <summary>Creates an empty notification host.</summary>
    /// <param name="theme">Theme supplying notification visuals.</param>
    public ToastHost(UITheme? theme = null)
    {
        _theme = theme ?? UITheme.Dark;
        IsOverlay = true;
        IsHitTestVisible = false;
        BackgroundColor = Color.Black;
    }

    /// <summary>Shows a notification for a bounded duration.</summary>
    /// <param name="text">Displayed message.</param>
    /// <param name="severity">Semantic emphasis.</param>
    /// <param name="duration">Visible duration in seconds.</param>
    /// <param name="actionLabel">Optional action-button label.</param>
    /// <param name="action">Optional action callback; ignored when no label is supplied.</param>
    /// <param name="key">Optional deduplication key scoped to this host.</param>
    /// <param name="progress">Optional normalized progress.</param>
    /// <param name="isProgressIndeterminate">Whether progress is animated.</param>
    /// <returns>The created notification, which can be dismissed early.</returns>
    public ToastNotification Show(string text, ToastSeverity severity = ToastSeverity.Information,
        double duration = 4d, string? actionLabel = null, Action? action = null, string? key = null,
        float? progress = null, bool isProgressIndeterminate = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (key is not null && FindByKey(key) is { } existing)
        {
            existing.Notification.UpdateContent(text, severity, _theme);
            existing.Notification.SetProgress(progress, isProgressIndeterminate);
            existing.Remaining = Math.Max(0.05d, duration);
            InvalidateMeasure();
            return existing.Notification;
        }
        while (_entries.Count >= MaxVisible)
            Dismiss(_entries[0].Notification);
        var notification = new ToastNotification(
            text, severity, _theme, actionLabel, action, progress, isProgressIndeterminate);
        notification.DismissRequested += () => Dismiss(notification);
        _entries.Add(new ToastEntry(notification, Math.Max(0.05d, duration), key));
        AddChild(notification);
        InvalidateMeasure();
        return notification;
    }

    /// <summary>Advances notification lifetimes for standalone hosts that do not traverse a UI root.</summary>
    /// <param name="deltaTime">Elapsed time in seconds.</param>
    /// <returns>True when one or more notifications expired.</returns>
    public bool Advance(double deltaTime) => AdvanceTime(deltaTime);

    /// <summary>Updates a visible notification and optionally resets its remaining lifetime.</summary>
    /// <param name="notification">Visible notification to update.</param>
    /// <param name="text">New message.</param>
    /// <param name="severity">New severity.</param>
    /// <param name="duration">Optional replacement lifetime.</param>
    /// <param name="progress">Optional normalized progress.</param>
    /// <param name="isProgressIndeterminate">Whether progress is animated.</param>
    /// <returns>True when the notification belongs to this host.</returns>
    public bool Update(ToastNotification notification, string text, ToastSeverity severity,
        double? duration = null, float? progress = null, bool isProgressIndeterminate = false)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        for (var index = 0; index < _entries.Count; index++)
        {
            var entry = _entries[index];
            if (!ReferenceEquals(entry.Notification, notification))
                continue;
            notification.UpdateContent(text, severity, _theme);
            notification.SetProgress(progress, isProgressIndeterminate);
            if (duration is { } seconds)
                entry.Remaining = Math.Max(0.05d, seconds);
            InvalidateMeasure();
            return true;
        }
        return false;
    }

    /// <summary>Dismisses a visible notification immediately.</summary>
    /// <param name="notification">Notification returned by <see cref="Show"/>.</param>
    /// <returns>True when the notification was visible.</returns>
    public bool Dismiss(ToastNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        for (var index = 0; index < _entries.Count; index++)
        {
            if (!ReferenceEquals(_entries[index].Notification, notification))
                continue;
            _entries.RemoveAt(index);
            RemoveChild(notification);
            InvalidateMeasure();
            return true;
        }
        return false;
    }

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        for (var index = 0; index < _entries.Count; index++)
            _entries[index].Notification.Measure(availableSize);
        return availableSize;
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        var y = EdgeInset;
        for (var index = 0; index < _entries.Count; index++)
        {
            var notification = _entries[index].Notification;
            var size = notification.DesiredSize;
            notification.Arrange(new Vector2(MathF.Max(0f, contentSize.X - size.X - EdgeInset), y), size);
            y += size.Y + Gap;
        }
    }

    /// <inheritdoc/>
    protected override bool UpdateElement(double deltaTime)
    {
        if (deltaTime <= 0d)
            return false;
        var changed = false;
        for (var index = _entries.Count - 1; index >= 0; index--)
        {
            var entry = _entries[index];
            if (PauseOnHover && IsPointerOver(entry.Notification))
                continue;
            entry.Remaining -= deltaTime;
            if (entry.Remaining > 0d)
                continue;
            _entries.RemoveAt(index);
            RemoveChild(entry.Notification);
            changed = true;
        }
        if (changed)
            InvalidateMeasure();
        return changed;
    }

    /// <inheritdoc/>
    protected override bool IsTimeUpdateActive => _entries.Count > 0;

    /// <summary>Checks hover state on an element and its descendants without iterator allocation.</summary>
    /// <param name="element">Subtree to inspect.</param>
    /// <returns>True when the pointer currently hovers any element in the subtree.</returns>
    private static bool IsPointerOver(UIElement element)
    {
        if (element.IsHovered)
            return true;
        var children = element.Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is UIElement child && IsPointerOver(child))
                return true;
        }
        return false;
    }

    /// <summary>Finds a retained notification by its optional deduplication key.</summary>
    /// <param name="key">Exact host-local key.</param>
    /// <returns>Matching mutable entry, or null.</returns>
    private ToastEntry? FindByKey(string key)
    {
        for (var index = 0; index < _entries.Count; index++)
        {
            if (string.Equals(_entries[index].Key, key, StringComparison.Ordinal))
                return _entries[index];
        }
        return null;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
    }

    /// <summary>Stores one notification and its remaining lifetime.</summary>
    private sealed class ToastEntry
    {
        /// <summary>Gets the retained notification element.</summary>
        public ToastNotification Notification { get; }

        /// <summary>Gets or sets remaining visible time in seconds.</summary>
        public double Remaining { get; set; }

        /// <summary>Gets the optional host-local deduplication key.</summary>
        public string? Key { get; }

        /// <summary>Creates a timed notification entry.</summary>
        /// <param name="notification">Retained notification.</param>
        /// <param name="remaining">Initial lifetime.</param>
        /// <param name="key">Optional deduplication key.</param>
        public ToastEntry(ToastNotification notification, double remaining, string? key)
        {
            Notification = notification;
            Remaining = remaining;
            Key = key;
        }
    }
}

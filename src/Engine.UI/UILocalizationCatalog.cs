using System.Globalization;
using System.Numerics;

namespace Engine.UI;

/// <summary>Stores culture-specific UI strings with parent-culture and invariant fallback.</summary>
public sealed class UILocalizationCatalog
{
    private readonly Dictionary<string, Dictionary<string, string>> _resources =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds or replaces one localized string.</summary>
    /// <param name="culture">Culture owning the translation, or invariant culture for fallback.</param>
    /// <param name="key">Stable resource key.</param>
    /// <param name="value">Localized display value.</param>
    public void Set(CultureInfo culture, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        if (!_resources.TryGetValue(culture.Name, out var cultureResources))
        {
            cultureResources = new Dictionary<string, string>(StringComparer.Ordinal);
            _resources.Add(culture.Name, cultureResources);
        }
        cultureResources[key] = value;
    }

    /// <summary>Resolves a localized string through exact, parent, and invariant cultures.</summary>
    /// <param name="culture">Requested runtime UI culture.</param>
    /// <param name="key">Stable resource key.</param>
    /// <param name="fallback">Caller fallback, or the key when omitted.</param>
    /// <returns>Best matching localized string.</returns>
    public string Get(CultureInfo culture, string key, string? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        for (var current = culture; current != CultureInfo.InvariantCulture; current = current.Parent)
        {
            if (TryGet(current.Name, key, out var value))
                return value;
        }
        return TryGet(string.Empty, key, out var invariant)
            ? invariant
            : fallback ?? key;
    }

    /// <summary>Looks up one key in one culture bucket.</summary>
    /// <param name="cultureName">Culture bucket name.</param>
    /// <param name="key">Resource key.</param>
    /// <param name="value">Resolved value when present.</param>
    /// <returns>True when the bucket contains the key.</returns>
    private bool TryGet(string cultureName, string key, out string value)
    {
        if (_resources.TryGetValue(cultureName, out var cultureResources)
            && cultureResources.TryGetValue(key, out var resolved))
        {
            value = resolved;
            return true;
        }
        value = string.Empty;
        return false;
    }
}

/// <summary>Displays a catalog string resolved from the inherited runtime UI culture.</summary>
public sealed class UILocalizedLabel : Label
{
    private string _resourceKey;
    private string? _fallback;

    /// <summary>Gets the localization catalog used by this label.</summary>
    public UILocalizationCatalog Catalog { get; }

    /// <summary>Gets or sets the stable localized resource key.</summary>
    public string ResourceKey
    {
        get => _resourceKey;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (_resourceKey == value)
                return;
            _resourceKey = value;
            InvalidateMeasure();
        }
    }

    /// <summary>Gets or sets text used when no catalog entry exists.</summary>
    public string? Fallback
    {
        get => _fallback;
        set
        {
            if (_fallback == value)
                return;
            _fallback = value;
            InvalidateMeasure();
        }
    }

    /// <summary>Creates a culture-aware retained label.</summary>
    /// <param name="catalog">Catalog containing translations.</param>
    /// <param name="resourceKey">Stable localized resource key.</param>
    /// <param name="fallback">Optional fallback display text.</param>
    /// <param name="width">Optional explicit width.</param>
    /// <param name="height">Optional explicit height.</param>
    public UILocalizedLabel(
        UILocalizationCatalog catalog,
        string resourceKey,
        string? fallback = null,
        float width = 0f,
        float height = 0f)
        : base(string.Empty, width, height)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        Catalog = catalog;
        _resourceKey = resourceKey;
        _fallback = fallback;
    }

    /// <summary>Invalidates the label after its mutable catalog is updated.</summary>
    public void RefreshLocalization() => InvalidateMeasure();

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var resolved = Catalog.Get(Culture, ResourceKey, Fallback);
        if (Text != resolved)
            Text = resolved;
        return base.MeasureOverride(availableSize);
    }
}

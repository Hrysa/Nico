using Engine.Core;
using Engine.UI;

namespace Editor;

/// <summary>Common typed asset-reference field used by Inspector content.</summary>
public sealed class AssetReferenceField : TextField
{
    /// <summary>Gets the runtime content type accepted by this field.</summary>
    public string AcceptedContentType { get; }

    /// <summary>Gets the callback used to assign a resolved reference.</summary>
    public Func<AssetReference, bool> Assign { get; }

    /// <summary>Creates a read-only typed asset-reference field.</summary>
    /// <param name="width">Field width.</param>
    /// <param name="height">Field height.</param>
    /// <param name="contentType">Accepted runtime content type.</param>
    /// <param name="assign">Assignment callback.</param>
    /// <param name="theme">Theme supplying field visuals.</param>
    public AssetReferenceField(
        float width,
        float height,
        string contentType,
        Func<AssetReference, bool> assign,
        UITheme? theme = null)
        : base(width, height, theme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        AcceptedContentType = contentType;
        Assign = assign ?? throw new ArgumentNullException(nameof(assign));
        IsReadOnly = true;
        AllowDrop = true;
    }
}

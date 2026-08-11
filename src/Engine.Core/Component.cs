using System.Numerics;

namespace Engine.Core;

/// <summary>Base class for attachable node behavior and authored component data.</summary>
public abstract class Component
{
    private bool _enabled = true;

    /// <summary>Gets the node that owns this component, or null before attachment.</summary>
    public Node? Owner { get; internal set; }

    /// <summary>Gets or sets whether the component participates in runtime behavior.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            Owner?.NotifyComponentChanged(NodeChangeKind.Components);
        }
    }

    /// <summary>Notifies the owning node after one authored component value changes.</summary>
    protected void NotifyValueChanged()
    {
        Owner?.NotifyComponentChanged(NodeChangeKind.ComponentValues);
    }
}

/// <summary>Stores one persistent game-script attachment and its authored property overrides.</summary>
public sealed class ScriptComponent : Component
{
    private readonly List<ScriptPropertyOverride> _propertyOverrides = new();
    private AssetId _scriptId;

    /// <summary>Gets or sets the persistent script asset implemented by this component.</summary>
    public AssetId ScriptId
    {
        get => _scriptId;
        set
        {
            if (value.Value == Guid.Empty)
                throw new ArgumentException("A script component requires a valid asset identifier.",
                    nameof(value));
            if (_scriptId == value)
                return;
            _scriptId = value;
            Owner?.NotifyComponentChanged(NodeChangeKind.Components);
        }
    }

    /// <summary>Gets authored values keyed by generated stable property identifier.</summary>
    public IReadOnlyList<ScriptPropertyOverride> PropertyOverrides => _propertyOverrides;

    /// <summary>Creates a script component for one persistent script asset.</summary>
    /// <param name="scriptId">Script asset identifier.</param>
    public ScriptComponent(AssetId scriptId)
    {
        if (scriptId.Value == Guid.Empty)
            throw new ArgumentException("A script component requires a valid asset identifier.",
                nameof(scriptId));
        _scriptId = scriptId;
    }

    /// <summary>Adds or replaces one authored property override.</summary>
    /// <param name="propertyId">Generated stable property identifier.</param>
    /// <param name="value">Persistent property value.</param>
    public void SetPropertyOverride(int propertyId, SerializedPropertyValue value)
    {
        for (var index = 0; index < _propertyOverrides.Count; index++)
        {
            if (_propertyOverrides[index].PropertyId != propertyId)
                continue;
            _propertyOverrides[index] = new ScriptPropertyOverride(propertyId, value);
            Owner?.NotifyComponentChanged(NodeChangeKind.ComponentValues);
            return;
        }
        _propertyOverrides.Add(new ScriptPropertyOverride(propertyId, value));
        Owner?.NotifyComponentChanged(NodeChangeKind.ComponentValues);
    }

    /// <summary>Removes one authored property override.</summary>
    /// <param name="propertyId">Generated stable property identifier.</param>
    /// <returns>True when an override was removed.</returns>
    public bool RemovePropertyOverride(int propertyId)
    {
        for (var index = 0; index < _propertyOverrides.Count; index++)
        {
            if (_propertyOverrides[index].PropertyId == propertyId)
            {
                _propertyOverrides.RemoveAt(index);
                Owner?.NotifyComponentChanged(NodeChangeKind.Components);
                return true;
            }
        }
        return false;
    }

    /// <summary>Reads one authored property override.</summary>
    /// <param name="propertyId">Generated stable property identifier.</param>
    /// <param name="value">Stored value when found.</param>
    /// <returns>True when the component contains the requested override.</returns>
    public bool TryGetPropertyOverride(int propertyId, out SerializedPropertyValue value)
    {
        for (var index = 0; index < _propertyOverrides.Count; index++)
        {
            if (_propertyOverrides[index].PropertyId != propertyId)
                continue;
            value = _propertyOverrides[index].Value;
            return true;
        }
        value = default;
        return false;
    }
}

/// <summary>Associates one generated property identifier with its authored persistent value.</summary>
/// <param name="PropertyId">Generated stable property identifier.</param>
/// <param name="Value">Persistent property value.</param>
public readonly record struct ScriptPropertyOverride(
    int PropertyId,
    SerializedPropertyValue Value);

/// <summary>Identifies the stored type of a persistent component property value.</summary>
public enum SerializedPropertyValueKind
{
    /// <summary>No supported value.</summary>
    None,
    /// <summary>Boolean value.</summary>
    Boolean,
    /// <summary>Signed 64-bit integer value.</summary>
    SignedInteger,
    /// <summary>Unsigned 64-bit integer value.</summary>
    UnsignedInteger,
    /// <summary>64-bit floating-point value.</summary>
    Number,
    /// <summary>Nullable string value.</summary>
    String,
    /// <summary>Two-component vector value.</summary>
    Vector2,
    /// <summary>Three-component vector value.</summary>
    Vector3,
    /// <summary>Four-component vector value.</summary>
    Vector4
}

/// <summary>Stores one serializable component property without boxing value types.</summary>
public readonly struct SerializedPropertyValue : IEquatable<SerializedPropertyValue>
{
    private readonly long _signed;
    private readonly ulong _unsigned;
    private readonly double _number;
    private readonly Vector4 _vector;
    private readonly string? _text;

    /// <summary>Gets the stored value category.</summary>
    public SerializedPropertyValueKind Kind { get; }

    /// <summary>Creates a Boolean value.</summary>
    /// <param name="value">Value to store.</param>
    /// <returns>Persistent representation.</returns>
    public static SerializedPropertyValue From(bool value) =>
        new(SerializedPropertyValueKind.Boolean, value ? 1L : 0L, 0UL, 0d, default, null);

    /// <summary>Creates a signed-integer value.</summary>
    /// <param name="value">Value to store.</param>
    /// <returns>Persistent representation.</returns>
    public static SerializedPropertyValue From(long value) =>
        new(SerializedPropertyValueKind.SignedInteger, value, 0UL, 0d, default, null);

    /// <summary>Creates an unsigned-integer value.</summary>
    /// <param name="value">Value to store.</param>
    /// <returns>Persistent representation.</returns>
    public static SerializedPropertyValue From(ulong value) =>
        new(SerializedPropertyValueKind.UnsignedInteger, 0L, value, 0d, default, null);

    /// <summary>Creates a numeric value.</summary>
    /// <param name="value">Value to store.</param>
    /// <returns>Persistent representation.</returns>
    public static SerializedPropertyValue From(double value) =>
        new(SerializedPropertyValueKind.Number, 0L, 0UL, value, default, null);

    /// <summary>Creates a nullable string value.</summary>
    /// <param name="value">Value to store.</param>
    /// <returns>Persistent representation.</returns>
    public static SerializedPropertyValue From(string? value) =>
        new(SerializedPropertyValueKind.String, 0L, 0UL, 0d, default, value);

    /// <summary>Creates a two-component vector value.</summary>
    /// <param name="value">Value to store.</param>
    /// <returns>Persistent representation.</returns>
    public static SerializedPropertyValue From(Vector2 value) =>
        new(SerializedPropertyValueKind.Vector2, 0L, 0UL, 0d,
            new Vector4(value, 0f, 0f), null);

    /// <summary>Creates a three-component vector value.</summary>
    /// <param name="value">Value to store.</param>
    /// <returns>Persistent representation.</returns>
    public static SerializedPropertyValue From(Vector3 value) =>
        new(SerializedPropertyValueKind.Vector3, 0L, 0UL, 0d, new Vector4(value, 0f), null);

    /// <summary>Creates a four-component vector value.</summary>
    /// <param name="value">Value to store.</param>
    /// <returns>Persistent representation.</returns>
    public static SerializedPropertyValue From(Vector4 value) =>
        new(SerializedPropertyValueKind.Vector4, 0L, 0UL, 0d, value, null);

    /// <summary>Reads a Boolean value.</summary>
    /// <param name="value">Stored value when the kind matches.</param>
    /// <returns>True when the stored kind is Boolean.</returns>
    public bool TryGetBoolean(out bool value)
    {
        value = _signed != 0L;
        return Kind == SerializedPropertyValueKind.Boolean;
    }

    /// <summary>Reads a signed integer value.</summary>
    /// <param name="value">Stored value when the kind matches.</param>
    /// <returns>True when the stored kind is signed integer.</returns>
    public bool TryGetSignedInteger(out long value)
    {
        value = _signed;
        return Kind == SerializedPropertyValueKind.SignedInteger;
    }

    /// <summary>Reads an unsigned integer value.</summary>
    /// <param name="value">Stored value when the kind matches.</param>
    /// <returns>True when the stored kind is unsigned integer.</returns>
    public bool TryGetUnsignedInteger(out ulong value)
    {
        value = _unsigned;
        return Kind == SerializedPropertyValueKind.UnsignedInteger;
    }

    /// <summary>Reads a numeric value.</summary>
    /// <param name="value">Stored value when the kind matches.</param>
    /// <returns>True when the stored kind is numeric.</returns>
    public bool TryGetNumber(out double value)
    {
        value = _number;
        return Kind == SerializedPropertyValueKind.Number;
    }

    /// <summary>Reads a nullable string value.</summary>
    /// <param name="value">Stored value when the kind matches.</param>
    /// <returns>True when the stored kind is string.</returns>
    public bool TryGetString(out string? value)
    {
        value = _text;
        return Kind == SerializedPropertyValueKind.String;
    }

    /// <summary>Reads a two-component vector value.</summary>
    /// <param name="value">Stored value when the kind matches.</param>
    /// <returns>True when the stored kind is Vector2.</returns>
    public bool TryGetVector2(out Vector2 value)
    {
        value = new Vector2(_vector.X, _vector.Y);
        return Kind == SerializedPropertyValueKind.Vector2;
    }

    /// <summary>Reads a three-component vector value.</summary>
    /// <param name="value">Stored value when the kind matches.</param>
    /// <returns>True when the stored kind is Vector3.</returns>
    public bool TryGetVector3(out Vector3 value)
    {
        value = new Vector3(_vector.X, _vector.Y, _vector.Z);
        return Kind == SerializedPropertyValueKind.Vector3;
    }

    /// <summary>Reads a four-component vector value.</summary>
    /// <param name="value">Stored value when the kind matches.</param>
    /// <returns>True when the stored kind is Vector4.</returns>
    public bool TryGetVector4(out Vector4 value)
    {
        value = _vector;
        return Kind == SerializedPropertyValueKind.Vector4;
    }

    /// <inheritdoc/>
    public bool Equals(SerializedPropertyValue other) =>
        Kind == other.Kind && _signed == other._signed && _unsigned == other._unsigned &&
        _number.Equals(other._number) && _vector.Equals(other._vector) &&
        string.Equals(_text, other._text, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SerializedPropertyValue other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(Kind, _signed, _unsigned, _number, _vector, _text);

    /// <summary>Creates one discriminated persistent property value.</summary>
    private SerializedPropertyValue(
        SerializedPropertyValueKind kind,
        long signed,
        ulong unsigned,
        double number,
        Vector4 vector,
        string? text)
    {
        Kind = kind;
        _signed = signed;
        _unsigned = unsigned;
        _number = number;
        _vector = vector;
        _text = text;
    }
}

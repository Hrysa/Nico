using System.Numerics;
using Engine.Core;

namespace Engine.Scripting;

/// <summary>Identifies consumers that receive generated script-property changes.</summary>
[Flags]
public enum ObserveScope
{
    /// <summary>Disables observation.</summary>
    None = 0,

    /// <summary>Exposes, serializes, and refreshes the property through Editor tooling.</summary>
    Editor = 1,

    /// <summary>Publishes changes to runtime subscribers.</summary>
    Runtime = 2
}

/// <summary>Requests generated storage, metadata, and change notification for a partial property.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ObserveAttribute : Attribute
{
    /// <summary>Gets the combined consumers requested by the property declaration.</summary>
    public ObserveScope Scope { get; }

    /// <summary>Creates an observation declaration from one or more consumer scopes.</summary>
    /// <param name="scopes">Consumers that should receive generated changes.</param>
    public ObserveAttribute(params ObserveScope[] scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var scope = ObserveScope.None;
        for (var index = 0; index < scopes.Length; index++)
            scope |= scopes[index];
        Scope = scope;
    }
}

/// <summary>Identifies the allocation-free value representation of an observed property.</summary>
public enum ObservedValueKind
{
    /// <summary>No supported value is stored.</summary>
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

/// <summary>Stores one supported observed value without boxing scalar or vector types.</summary>
public readonly struct ObservedValue : IEquatable<ObservedValue>
{
    private readonly long _signed;
    private readonly ulong _unsigned;
    private readonly double _number;
    private readonly Vector4 _vector;
    private readonly string? _text;

    /// <summary>Gets the stored value category.</summary>
    public ObservedValueKind Kind { get; }

    /// <summary>Creates a Boolean observed value.</summary>
    /// <param name="value">Value to store.</param>
    /// <returns>Typed observed representation.</returns>
    public static ObservedValue From(bool value) =>
        new(ObservedValueKind.Boolean, value ? 1L : 0L, 0UL, 0d, default, null);

    /// <summary>Creates a signed-integer observed value.</summary>
    /// <param name="value">Value to store.</param>
    /// <returns>Typed observed representation.</returns>
    public static ObservedValue From(long value) =>
        new(ObservedValueKind.SignedInteger, value, 0UL, 0d, default, null);

    /// <summary>Creates an unsigned-integer observed value.</summary>
    /// <param name="value">Value to store.</param>
    /// <returns>Typed observed representation.</returns>
    public static ObservedValue From(ulong value) =>
        new(ObservedValueKind.UnsignedInteger, 0L, value, 0d, default, null);

    /// <summary>Creates a numeric observed value.</summary>
    /// <param name="value">Value to store.</param>
    /// <returns>Typed observed representation.</returns>
    public static ObservedValue From(double value) =>
        new(ObservedValueKind.Number, 0L, 0UL, value, default, null);

    /// <summary>Creates a nullable-string observed value.</summary>
    /// <param name="value">Value to store.</param>
    /// <returns>Typed observed representation.</returns>
    public static ObservedValue From(string? value) =>
        new(ObservedValueKind.String, 0L, 0UL, 0d, default, value);

    /// <summary>Creates a two-component vector observed value.</summary>
    /// <param name="value">Value to store.</param>
    /// <returns>Typed observed representation.</returns>
    public static ObservedValue From(Vector2 value) =>
        new(ObservedValueKind.Vector2, 0L, 0UL, 0d, new Vector4(value, 0f, 0f), null);

    /// <summary>Creates a three-component vector observed value.</summary>
    /// <param name="value">Value to store.</param>
    /// <returns>Typed observed representation.</returns>
    public static ObservedValue From(Vector3 value) =>
        new(ObservedValueKind.Vector3, 0L, 0UL, 0d, new Vector4(value, 0f), null);

    /// <summary>Creates a four-component vector observed value.</summary>
    /// <param name="value">Value to store.</param>
    /// <returns>Typed observed representation.</returns>
    public static ObservedValue From(Vector4 value) =>
        new(ObservedValueKind.Vector4, 0L, 0UL, 0d, value, null);

    /// <summary>Converts persistent component data to the generated runtime value contract.</summary>
    /// <param name="source">Persistent property value.</param>
    /// <param name="value">Converted observed value when supported.</param>
    /// <returns>True when the persistent kind is supported.</returns>
    public static bool TryFromSerialized(
        SerializedPropertyValue source,
        out ObservedValue value)
    {
        switch (source.Kind)
        {
            case SerializedPropertyValueKind.Boolean when source.TryGetBoolean(out var boolean):
                value = From(boolean);
                return true;
            case SerializedPropertyValueKind.SignedInteger
                when source.TryGetSignedInteger(out var signed):
                value = From(signed);
                return true;
            case SerializedPropertyValueKind.UnsignedInteger
                when source.TryGetUnsignedInteger(out var unsigned):
                value = From(unsigned);
                return true;
            case SerializedPropertyValueKind.Number when source.TryGetNumber(out var number):
                value = From(number);
                return true;
            case SerializedPropertyValueKind.String when source.TryGetString(out var text):
                value = From(text);
                return true;
            case SerializedPropertyValueKind.Vector2 when source.TryGetVector2(out var vector2):
                value = From(vector2);
                return true;
            case SerializedPropertyValueKind.Vector3 when source.TryGetVector3(out var vector3):
                value = From(vector3);
                return true;
            case SerializedPropertyValueKind.Vector4 when source.TryGetVector4(out var vector4):
                value = From(vector4);
                return true;
            default:
                value = default;
                return false;
        }
    }

    /// <summary>Converts this observed value to persistent component data.</summary>
    /// <param name="value">Converted persistent value when supported.</param>
    /// <returns>True when the observed kind is supported.</returns>
    public bool TryToSerialized(out SerializedPropertyValue value)
    {
        switch (Kind)
        {
            case ObservedValueKind.Boolean when TryGetBoolean(out var boolean):
                value = SerializedPropertyValue.From(boolean);
                return true;
            case ObservedValueKind.SignedInteger when TryGetSignedInteger(out var signed):
                value = SerializedPropertyValue.From(signed);
                return true;
            case ObservedValueKind.UnsignedInteger when TryGetUnsignedInteger(out var unsigned):
                value = SerializedPropertyValue.From(unsigned);
                return true;
            case ObservedValueKind.Number when TryGetNumber(out var number):
                value = SerializedPropertyValue.From(number);
                return true;
            case ObservedValueKind.String when TryGetString(out var text):
                value = SerializedPropertyValue.From(text);
                return true;
            case ObservedValueKind.Vector2 when TryGetVector2(out var vector2):
                value = SerializedPropertyValue.From(vector2);
                return true;
            case ObservedValueKind.Vector3 when TryGetVector3(out var vector3):
                value = SerializedPropertyValue.From(vector3);
                return true;
            case ObservedValueKind.Vector4 when TryGetVector4(out var vector4):
                value = SerializedPropertyValue.From(vector4);
                return true;
            default:
                value = default;
                return false;
        }
    }

    /// <summary>Reads a Boolean value.</summary>
    /// <param name="value">Stored value when the kind matches.</param>
    /// <returns>True when this value contains a Boolean.</returns>
    public bool TryGetBoolean(out bool value)
    {
        value = _signed != 0L;
        return Kind == ObservedValueKind.Boolean;
    }

    /// <summary>Reads a signed integer value.</summary>
    /// <param name="value">Stored value when the kind matches.</param>
    /// <returns>True when this value contains a signed integer.</returns>
    public bool TryGetSignedInteger(out long value)
    {
        value = _signed;
        return Kind == ObservedValueKind.SignedInteger;
    }

    /// <summary>Reads an unsigned integer value.</summary>
    /// <param name="value">Stored value when the kind matches.</param>
    /// <returns>True when this value contains an unsigned integer.</returns>
    public bool TryGetUnsignedInteger(out ulong value)
    {
        value = _unsigned;
        return Kind == ObservedValueKind.UnsignedInteger;
    }

    /// <summary>Reads a numeric value.</summary>
    /// <param name="value">Stored value when the kind matches.</param>
    /// <returns>True when this value contains a number.</returns>
    public bool TryGetNumber(out double value)
    {
        value = _number;
        return Kind == ObservedValueKind.Number;
    }

    /// <summary>Reads a nullable string value.</summary>
    /// <param name="value">Stored value when the kind matches.</param>
    /// <returns>True when this value contains a string.</returns>
    public bool TryGetString(out string? value)
    {
        value = _text;
        return Kind == ObservedValueKind.String;
    }

    /// <summary>Reads a two-component vector value.</summary>
    /// <param name="value">Stored value when the kind matches.</param>
    /// <returns>True when this value contains a two-component vector.</returns>
    public bool TryGetVector2(out Vector2 value)
    {
        value = new Vector2(_vector.X, _vector.Y);
        return Kind == ObservedValueKind.Vector2;
    }

    /// <summary>Reads a three-component vector value.</summary>
    /// <param name="value">Stored value when the kind matches.</param>
    /// <returns>True when this value contains a three-component vector.</returns>
    public bool TryGetVector3(out Vector3 value)
    {
        value = new Vector3(_vector.X, _vector.Y, _vector.Z);
        return Kind == ObservedValueKind.Vector3;
    }

    /// <summary>Reads a four-component vector value.</summary>
    /// <param name="value">Stored value when the kind matches.</param>
    /// <returns>True when this value contains a four-component vector.</returns>
    public bool TryGetVector4(out Vector4 value)
    {
        value = _vector;
        return Kind == ObservedValueKind.Vector4;
    }

    /// <inheritdoc/>
    public bool Equals(ObservedValue other) =>
        Kind == other.Kind && _signed == other._signed && _unsigned == other._unsigned &&
        _number.Equals(other._number) && _vector.Equals(other._vector) &&
        string.Equals(_text, other._text, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ObservedValue other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(Kind, _signed, _unsigned, _number, _vector, _text);

    /// <summary>Creates one discriminated observed value.</summary>
    private ObservedValue(
        ObservedValueKind kind,
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

/// <summary>Describes one generated observable script property.</summary>
/// <param name="Id">Stable generated property identifier.</param>
/// <param name="Name">Source property name.</param>
/// <param name="Kind">Allocation-free value representation.</param>
/// <param name="Scope">Consumers receiving changes.</param>
public readonly record struct ObservedPropertyDescriptor(
    int Id,
    string Name,
    ObservedValueKind Kind,
    ObserveScope Scope);

/// <summary>Identifies one changed generated script property.</summary>
/// <param name="PropertyId">Stable generated property identifier.</param>
/// <param name="Scope">Consumers receiving the change.</param>
public readonly record struct ObservedPropertyChange(int PropertyId, ObserveScope Scope);

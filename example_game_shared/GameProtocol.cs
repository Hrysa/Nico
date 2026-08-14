using System.Buffers.Binary;
using System.Numerics;

namespace ExampleGame.Networking;

/// <summary>Identifies one datagram in the example game's versioned UDP protocol.</summary>
public enum GameMessageType : byte
{
    ClientHello = 1,
    ServerWelcome = 2,
    ClientInput = 3,
    ServerSnapshot = 4,
    ClientDisconnect = 5
}

/// <summary>Input intent sent by an authenticated UDP client.</summary>
/// <param name="ClientId">Server-assigned player identity.</param>
/// <param name="SessionToken">Unpredictable session proof.</param>
/// <param name="Sequence">Monotonically increasing input sequence.</param>
/// <param name="Movement">Normalized XZ movement intent.</param>
/// <param name="FacingYaw">World-space facing angle in radians.</param>
/// <param name="Jump">Whether this input requests a jump.</param>
public readonly record struct ClientInputMessage(
    uint ClientId,
    ulong SessionToken,
    uint Sequence,
    Vector2 Movement,
    float FacingYaw,
    bool Jump);

/// <summary>Initial server response assigning an authenticated player session.</summary>
/// <param name="ClientNonce">Nonce copied from the triggering hello.</param>
/// <param name="ClientId">Server-assigned player identity.</param>
/// <param name="SessionToken">Unpredictable session proof required by later messages.</param>
/// <param name="TickRate">Authoritative ticks per second.</param>
/// <param name="ServerTick">Current authoritative tick.</param>
/// <param name="SpawnPosition">Authoritative spawn position.</param>
public readonly record struct ServerWelcomeMessage(
    uint ClientNonce,
    uint ClientId,
    ulong SessionToken,
    ushort TickRate,
    long ServerTick,
    Vector3 SpawnPosition);

/// <summary>Authoritative state returned to one UDP client.</summary>
/// <param name="ClientId">Server-assigned player identity.</param>
/// <param name="SessionToken">Session proof matching the receiver.</param>
/// <param name="AcknowledgedInput">Latest accepted input sequence.</param>
/// <param name="ServerTick">Tick that produced this state.</param>
/// <param name="Position">Authoritative world-space foot position.</param>
/// <param name="Velocity">Authoritative world-space velocity.</param>
/// <param name="FacingYaw">Accepted world-space facing angle in radians.</param>
/// <param name="Grounded">Whether terrain currently supports the player.</param>
public readonly record struct ServerSnapshotMessage(
    uint ClientId,
    ulong SessionToken,
    uint AcknowledgedInput,
    long ServerTick,
    Vector3 Position,
    Vector3 Velocity,
    float FacingYaw,
    bool Grounded);

/// <summary>Encodes and validates the example game's allocation-free UDP datagrams.</summary>
public static class GameProtocol
{
    private const uint Magic = 0x4F43494Eu;
    private const byte Version = 1;
    private const int HeaderSize = 6;
    public const int ClientHelloSize = HeaderSize + sizeof(uint);
    public const int ServerWelcomeSize = HeaderSize + sizeof(uint) * 2 + sizeof(ulong) +
        sizeof(ushort) + sizeof(long) + sizeof(float) * 3;
    public const int ClientInputSize = HeaderSize + sizeof(uint) + sizeof(ulong) +
        sizeof(uint) + sizeof(float) * 3 + sizeof(byte);
    public const int ClientDisconnectSize = HeaderSize + sizeof(uint) + sizeof(ulong);
    public const int ServerSnapshotSize = HeaderSize + sizeof(uint) + sizeof(ulong) +
        sizeof(uint) + sizeof(long) + sizeof(float) * 7 + sizeof(byte);

    /// <summary>Writes a client discovery/session request.</summary>
    /// <param name="destination">Exact-sized datagram destination.</param>
    /// <param name="nonce">Client-generated handshake nonce.</param>
    public static void WriteClientHello(Span<byte> destination, uint nonce)
    {
        RequireSize(destination, ClientHelloSize);
        WriteHeader(destination, GameMessageType.ClientHello);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[HeaderSize..], nonce);
    }

    /// <summary>Reads a valid client discovery/session request.</summary>
    /// <param name="source">Received datagram.</param>
    /// <param name="nonce">Decoded client nonce.</param>
    /// <returns>True when the datagram is an exact valid hello.</returns>
    public static bool TryReadClientHello(ReadOnlySpan<byte> source, out uint nonce)
    {
        if (!HasHeader(source, GameMessageType.ClientHello, ClientHelloSize))
        {
            nonce = 0;
            return false;
        }
        nonce = BinaryPrimitives.ReadUInt32LittleEndian(source[HeaderSize..]);
        return true;
    }

    /// <summary>Writes one server session assignment.</summary>
    /// <param name="destination">Exact-sized datagram destination.</param>
    /// <param name="message">Assignment to encode.</param>
    public static void WriteServerWelcome(
        Span<byte> destination,
        in ServerWelcomeMessage message)
    {
        RequireSize(destination, ServerWelcomeSize);
        WriteHeader(destination, GameMessageType.ServerWelcome);
        var offset = HeaderSize;
        WriteUInt32(destination, ref offset, message.ClientNonce);
        WriteUInt32(destination, ref offset, message.ClientId);
        WriteUInt64(destination, ref offset, message.SessionToken);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..], message.TickRate);
        offset += sizeof(ushort);
        WriteInt64(destination, ref offset, message.ServerTick);
        WriteVector3(destination, ref offset, message.SpawnPosition);
    }

    /// <summary>Reads one valid server session assignment.</summary>
    /// <param name="source">Received datagram.</param>
    /// <param name="message">Decoded assignment.</param>
    /// <returns>True when the datagram is exact and valid.</returns>
    public static bool TryReadServerWelcome(
        ReadOnlySpan<byte> source,
        out ServerWelcomeMessage message)
    {
        if (!HasHeader(source, GameMessageType.ServerWelcome, ServerWelcomeSize))
        {
            message = default;
            return false;
        }
        var offset = HeaderSize;
        var nonce = ReadUInt32(source, ref offset);
        var clientId = ReadUInt32(source, ref offset);
        var token = ReadUInt64(source, ref offset);
        var tickRate = BinaryPrimitives.ReadUInt16LittleEndian(source[offset..]);
        offset += sizeof(ushort);
        var tick = ReadInt64(source, ref offset);
        message = new ServerWelcomeMessage(
            nonce, clientId, token, tickRate, tick, ReadVector3(source, ref offset));
        return true;
    }

    /// <summary>Writes one authenticated player-input datagram.</summary>
    /// <param name="destination">Exact-sized datagram destination.</param>
    /// <param name="message">Input intent to encode.</param>
    public static void WriteClientInput(Span<byte> destination, in ClientInputMessage message)
    {
        RequireSize(destination, ClientInputSize);
        WriteHeader(destination, GameMessageType.ClientInput);
        var offset = HeaderSize;
        WriteUInt32(destination, ref offset, message.ClientId);
        WriteUInt64(destination, ref offset, message.SessionToken);
        WriteUInt32(destination, ref offset, message.Sequence);
        WriteSingle(destination, ref offset, message.Movement.X);
        WriteSingle(destination, ref offset, message.Movement.Y);
        WriteSingle(destination, ref offset, message.FacingYaw);
        destination[offset] = message.Jump ? (byte)1 : (byte)0;
    }

    /// <summary>Reads one finite authenticated player-input datagram.</summary>
    /// <param name="source">Received datagram.</param>
    /// <param name="message">Decoded input intent.</param>
    /// <returns>True when the datagram is exact and contains finite values.</returns>
    public static bool TryReadClientInput(
        ReadOnlySpan<byte> source,
        out ClientInputMessage message)
    {
        if (!HasHeader(source, GameMessageType.ClientInput, ClientInputSize))
        {
            message = default;
            return false;
        }
        var offset = HeaderSize;
        var clientId = ReadUInt32(source, ref offset);
        var token = ReadUInt64(source, ref offset);
        var sequence = ReadUInt32(source, ref offset);
        var movement = new Vector2(
            ReadSingle(source, ref offset), ReadSingle(source, ref offset));
        var yaw = ReadSingle(source, ref offset);
        var flags = source[offset];
        if (!float.IsFinite(movement.X) || !float.IsFinite(movement.Y) ||
            !float.IsFinite(yaw) || (flags & ~1) != 0)
        {
            message = default;
            return false;
        }
        message = new ClientInputMessage(
            clientId, token, sequence, movement, yaw, (flags & 1) != 0);
        return true;
    }

    /// <summary>Writes one authenticated voluntary disconnect.</summary>
    /// <param name="destination">Exact-sized datagram destination.</param>
    /// <param name="clientId">Server-assigned player identity.</param>
    /// <param name="sessionToken">Session proof.</param>
    public static void WriteClientDisconnect(
        Span<byte> destination,
        uint clientId,
        ulong sessionToken)
    {
        RequireSize(destination, ClientDisconnectSize);
        WriteHeader(destination, GameMessageType.ClientDisconnect);
        var offset = HeaderSize;
        WriteUInt32(destination, ref offset, clientId);
        WriteUInt64(destination, ref offset, sessionToken);
    }

    /// <summary>Reads one authenticated voluntary disconnect.</summary>
    /// <param name="source">Received datagram.</param>
    /// <param name="clientId">Decoded player identity.</param>
    /// <param name="sessionToken">Decoded session proof.</param>
    /// <returns>True when the datagram is exact and valid.</returns>
    public static bool TryReadClientDisconnect(
        ReadOnlySpan<byte> source,
        out uint clientId,
        out ulong sessionToken)
    {
        if (!HasHeader(source, GameMessageType.ClientDisconnect, ClientDisconnectSize))
        {
            clientId = 0;
            sessionToken = 0;
            return false;
        }
        var offset = HeaderSize;
        clientId = ReadUInt32(source, ref offset);
        sessionToken = ReadUInt64(source, ref offset);
        return true;
    }

    /// <summary>Writes one authoritative player snapshot.</summary>
    /// <param name="destination">Exact-sized datagram destination.</param>
    /// <param name="message">Snapshot to encode.</param>
    public static void WriteServerSnapshot(
        Span<byte> destination,
        in ServerSnapshotMessage message)
    {
        RequireSize(destination, ServerSnapshotSize);
        WriteHeader(destination, GameMessageType.ServerSnapshot);
        var offset = HeaderSize;
        WriteUInt32(destination, ref offset, message.ClientId);
        WriteUInt64(destination, ref offset, message.SessionToken);
        WriteUInt32(destination, ref offset, message.AcknowledgedInput);
        WriteInt64(destination, ref offset, message.ServerTick);
        WriteVector3(destination, ref offset, message.Position);
        WriteVector3(destination, ref offset, message.Velocity);
        WriteSingle(destination, ref offset, message.FacingYaw);
        destination[offset] = message.Grounded ? (byte)1 : (byte)0;
    }

    /// <summary>Reads one valid authoritative player snapshot.</summary>
    /// <param name="source">Received datagram.</param>
    /// <param name="message">Decoded snapshot.</param>
    /// <returns>True when the datagram is exact and valid.</returns>
    public static bool TryReadServerSnapshot(
        ReadOnlySpan<byte> source,
        out ServerSnapshotMessage message)
    {
        if (!HasHeader(source, GameMessageType.ServerSnapshot, ServerSnapshotSize))
        {
            message = default;
            return false;
        }
        var offset = HeaderSize;
        var clientId = ReadUInt32(source, ref offset);
        var token = ReadUInt64(source, ref offset);
        var sequence = ReadUInt32(source, ref offset);
        var tick = ReadInt64(source, ref offset);
        var position = ReadVector3(source, ref offset);
        var velocity = ReadVector3(source, ref offset);
        var facingYaw = ReadSingle(source, ref offset);
        var grounded = source[offset] == 1;
        if (!IsFinite(position) || !IsFinite(velocity) || !float.IsFinite(facingYaw) ||
            source[offset] > 1)
        {
            message = default;
            return false;
        }
        message = new ServerSnapshotMessage(
            clientId, token, sequence, tick, position, velocity, facingYaw, grounded);
        return true;
    }

    /// <summary>Checks an exact datagram header and expected message type.</summary>
    /// <param name="source">Received datagram.</param>
    /// <param name="type">Expected message type.</param>
    /// <param name="size">Expected exact byte count.</param>
    /// <returns>True when magic, version, type, and size match.</returns>
    private static bool HasHeader(ReadOnlySpan<byte> source, GameMessageType type, int size) =>
        source.Length == size &&
        BinaryPrimitives.ReadUInt32LittleEndian(source) == Magic &&
        source[4] == Version && source[5] == (byte)type;

    /// <summary>Writes the common protocol header.</summary>
    /// <param name="destination">Exact-sized datagram destination.</param>
    /// <param name="type">Datagram type.</param>
    private static void WriteHeader(Span<byte> destination, GameMessageType type)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, Magic);
        destination[4] = Version;
        destination[5] = (byte)type;
    }

    /// <summary>Requires an exact destination size.</summary>
    /// <param name="destination">Caller-provided destination.</param>
    /// <param name="size">Required size.</param>
    private static void RequireSize(Span<byte> destination, int size)
    {
        if (destination.Length != size)
            throw new ArgumentException($"Protocol destination must contain exactly {size} bytes.",
                nameof(destination));
    }

    /// <summary>Writes one little-endian unsigned integer and advances the offset.</summary>
    /// <param name="destination">Datagram destination.</param>
    /// <param name="offset">Current byte offset.</param>
    /// <param name="value">Value to encode.</param>
    private static void WriteUInt32(Span<byte> destination, ref int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], value);
        offset += sizeof(uint);
    }

    /// <summary>Writes one little-endian unsigned long and advances the offset.</summary>
    /// <param name="destination">Datagram destination.</param>
    /// <param name="offset">Current byte offset.</param>
    /// <param name="value">Value to encode.</param>
    private static void WriteUInt64(Span<byte> destination, ref int offset, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination[offset..], value);
        offset += sizeof(ulong);
    }

    /// <summary>Writes one little-endian signed long and advances the offset.</summary>
    /// <param name="destination">Datagram destination.</param>
    /// <param name="offset">Current byte offset.</param>
    /// <param name="value">Value to encode.</param>
    private static void WriteInt64(Span<byte> destination, ref int offset, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination[offset..], value);
        offset += sizeof(long);
    }

    /// <summary>Writes one IEEE-754 single and advances the offset.</summary>
    /// <param name="destination">Datagram destination.</param>
    /// <param name="offset">Current byte offset.</param>
    /// <param name="value">Value to encode.</param>
    private static void WriteSingle(Span<byte> destination, ref int offset, float value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[offset..], BitConverter.SingleToInt32Bits(value));
        offset += sizeof(float);
    }

    /// <summary>Writes three IEEE-754 singles and advances the offset.</summary>
    /// <param name="destination">Datagram destination.</param>
    /// <param name="offset">Current byte offset.</param>
    /// <param name="value">Vector to encode.</param>
    private static void WriteVector3(Span<byte> destination, ref int offset, Vector3 value)
    {
        WriteSingle(destination, ref offset, value.X);
        WriteSingle(destination, ref offset, value.Y);
        WriteSingle(destination, ref offset, value.Z);
    }

    /// <summary>Reads one little-endian unsigned integer and advances the offset.</summary>
    /// <param name="source">Received datagram.</param>
    /// <param name="offset">Current byte offset.</param>
    /// <returns>Decoded value.</returns>
    private static uint ReadUInt32(ReadOnlySpan<byte> source, ref int offset)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
        offset += sizeof(uint);
        return value;
    }

    /// <summary>Reads one little-endian unsigned long and advances the offset.</summary>
    /// <param name="source">Received datagram.</param>
    /// <param name="offset">Current byte offset.</param>
    /// <returns>Decoded value.</returns>
    private static ulong ReadUInt64(ReadOnlySpan<byte> source, ref int offset)
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(source[offset..]);
        offset += sizeof(ulong);
        return value;
    }

    /// <summary>Reads one little-endian signed long and advances the offset.</summary>
    /// <param name="source">Received datagram.</param>
    /// <param name="offset">Current byte offset.</param>
    /// <returns>Decoded value.</returns>
    private static long ReadInt64(ReadOnlySpan<byte> source, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt64LittleEndian(source[offset..]);
        offset += sizeof(long);
        return value;
    }

    /// <summary>Reads one IEEE-754 single and advances the offset.</summary>
    /// <param name="source">Received datagram.</param>
    /// <param name="offset">Current byte offset.</param>
    /// <returns>Decoded value.</returns>
    private static float ReadSingle(ReadOnlySpan<byte> source, ref int offset)
    {
        var value = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(source[offset..]));
        offset += sizeof(float);
        return value;
    }

    /// <summary>Reads three IEEE-754 singles and advances the offset.</summary>
    /// <param name="source">Received datagram.</param>
    /// <param name="offset">Current byte offset.</param>
    /// <returns>Decoded vector.</returns>
    private static Vector3 ReadVector3(ReadOnlySpan<byte> source, ref int offset) =>
        new(ReadSingle(source, ref offset), ReadSingle(source, ref offset),
            ReadSingle(source, ref offset));

    /// <summary>Checks whether every vector component is finite.</summary>
    /// <param name="value">Candidate vector.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

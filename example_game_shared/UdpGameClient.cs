using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;

namespace ExampleGame.Networking;

/// <summary>Owns one authenticated UDP session with the authoritative example-game server.</summary>
public sealed class UdpGameClient : IDisposable
{
    private const int HelloRetryMilliseconds = 100;
    private readonly Socket _socket;
    private long _lastServerMessageTimestamp;
    private bool _disposed;

    /// <summary>Gets the server-assigned client identity.</summary>
    public uint ClientId { get; }

    /// <summary>Gets the authoritative simulation tick rate.</summary>
    public ushort TickRate { get; }

    /// <summary>Gets the initial authoritative player position.</summary>
    public Vector3 SpawnPosition { get; }

    /// <summary>Gets the server tick observed during the handshake.</summary>
    public long InitialServerTick { get; }

    /// <summary>Connects to an authoritative server and completes its challenge handshake.</summary>
    /// <param name="host">IPv4 host name or address.</param>
    /// <param name="port">UDP server port.</param>
    /// <param name="timeout">Maximum time allowed for the handshake.</param>
    /// <param name="cancellationToken">Cancels the pending handshake.</param>
    /// <returns>An authenticated game client.</returns>
    public static UdpGameClient Connect(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, IPEndPoint.MinPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, IPEndPoint.MaxPort);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var address = ResolveIpv4Address(host);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Connect(new IPEndPoint(address, port));
            var nonce = unchecked((uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue));
            Span<byte> hello = stackalloc byte[GameProtocol.ClientHelloSize];
            Span<byte> response = stackalloc byte[GameProtocol.ServerWelcomeSize];
            GameProtocol.WriteClientHello(hello, nonce);
            var stopwatch = Stopwatch.StartNew();
            var nextHello = TimeSpan.Zero;
            while (stopwatch.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stopwatch.Elapsed >= nextHello)
                {
                    try
                    {
                        socket.Send(hello, SocketFlags.None);
                    }
                    catch (SocketException exception) when (IsPeerUnavailable(exception))
                    {
                    }
                    nextHello = stopwatch.Elapsed + TimeSpan.FromMilliseconds(HelloRetryMilliseconds);
                }
                if (!socket.Poll(10_000, SelectMode.SelectRead))
                    continue;
                int received;
                try
                {
                    received = socket.Receive(response, SocketFlags.None);
                }
                catch (SocketException exception) when (IsPeerUnavailable(exception))
                {
                    continue;
                }
                if (!GameProtocol.TryReadServerWelcome(response[..received], out var welcome) ||
                    welcome.ClientNonce != nonce || welcome.ClientId == 0 ||
                    welcome.SessionToken == 0 || welcome.TickRate == 0)
                {
                    continue;
                }
                return new UdpGameClient(socket, welcome);
            }
            throw new TimeoutException(
                $"Could not connect to authoritative game server at {host}:{port} within {timeout.TotalSeconds:0.##} seconds.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Sends the latest player intent to the authoritative server.</summary>
    /// <param name="sequence">Monotonically increasing local input sequence.</param>
    /// <param name="movement">Normalized world-space XZ movement intent.</param>
    /// <param name="facingYaw">World-space facing angle in radians.</param>
    /// <param name="jump">Whether this input requests a jump.</param>
    /// <param name="attack">Whether this input requests a primary attack.</param>
    public void SendInput(
        uint sequence,
        Vector2 movement,
        float facingYaw,
        bool jump,
        bool attack)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Span<byte> datagram = stackalloc byte[GameProtocol.ClientInputSize];
        var message = new ClientInputMessage(
            ClientId, _sessionToken, sequence, movement, facingYaw, jump, attack);
        GameProtocol.WriteClientInput(datagram, message);
        _socket.Send(datagram, SocketFlags.None);
    }

    /// <summary>Drains pending datagrams and returns the newest authenticated snapshot.</summary>
    /// <param name="snapshot">Newest accepted snapshot when available.</param>
    /// <returns>True when at least one new snapshot was accepted.</returns>
    public bool TryReceiveLatestSnapshot(out ServerSnapshotMessage snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        snapshot = default;
        var found = false;
        Span<byte> datagram = stackalloc byte[GameProtocol.ServerSnapshotSize];
        while (_socket.Available > 0)
        {
            var received = _socket.Receive(datagram, SocketFlags.None);
            if (!GameProtocol.TryReadServerSnapshot(datagram[..received], out var candidate) ||
                candidate.ClientId != ClientId || candidate.SessionToken != _sessionToken ||
                (found && candidate.ServerTick <= snapshot.ServerTick))
            {
                continue;
            }
            snapshot = candidate;
            found = true;
        }
        if (found)
            _lastServerMessageTimestamp = Stopwatch.GetTimestamp();
        return found;
    }

    /// <summary>Checks whether the server has stopped producing authenticated state.</summary>
    /// <param name="timeout">Maximum allowed silence duration.</param>
    /// <returns>True when no server state arrived within the requested duration.</returns>
    public bool HasTimedOut(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        return Stopwatch.GetElapsedTime(_lastServerMessageTimestamp) > timeout;
    }

    /// <summary>Sends a best-effort disconnect and releases the UDP socket.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        try
        {
            Span<byte> datagram = stackalloc byte[GameProtocol.ClientDisconnectSize];
            GameProtocol.WriteClientDisconnect(datagram, ClientId, _sessionToken);
            _socket.Send(datagram, SocketFlags.None);
        }
        catch (SocketException)
        {
        }
        finally
        {
            _socket.Dispose();
            _disposed = true;
        }
    }

    private readonly ulong _sessionToken;

    /// <summary>Creates a client from a validated server welcome.</summary>
    /// <param name="socket">Connected UDP socket.</param>
    /// <param name="welcome">Validated session assignment.</param>
    private UdpGameClient(Socket socket, in ServerWelcomeMessage welcome)
    {
        _socket = socket;
        ClientId = welcome.ClientId;
        _sessionToken = welcome.SessionToken;
        TickRate = welcome.TickRate;
        SpawnPosition = welcome.SpawnPosition;
        InitialServerTick = welcome.ServerTick;
        _lastServerMessageTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>Resolves a host to the first usable IPv4 address.</summary>
    /// <param name="host">Host name or address.</param>
    /// <returns>Resolved IPv4 address.</returns>
    private static IPAddress ResolveIpv4Address(string host)
    {
        if (IPAddress.TryParse(host, out var parsed))
        {
            if (parsed.AddressFamily != AddressFamily.InterNetwork)
                throw new NotSupportedException("The example-game UDP client currently requires IPv4.");
            return parsed;
        }
        var addresses = Dns.GetHostAddresses(host, AddressFamily.InterNetwork);
        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);
        return addresses[0];
    }

    /// <summary>Checks whether an ICMP response only reports that no UDP server is listening yet.</summary>
    /// <param name="exception">Socket failure raised by the connected UDP endpoint.</param>
    /// <returns>True when the handshake should continue retrying until its deadline.</returns>
    private static bool IsPeerUnavailable(SocketException exception)
    {
        return exception.SocketErrorCode is SocketError.ConnectionReset or
            SocketError.ConnectionRefused or SocketError.HostUnreachable or
            SocketError.NetworkUnreachable;
    }
}

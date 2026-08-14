using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using ExampleGame.Networking;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace ExampleGame.Server;

/// <summary>Receives player intent and publishes authoritative snapshots over UDP.</summary>
internal sealed class UdpGameServer : IDisposable
{
    private const int MaximumDatagramsPerTick = 256;
    private readonly Socket _socket;
    private readonly SocketAddress _receiveAddress = new(AddressFamily.InterNetwork, 16);
    private readonly ServerTerrainCollision _terrain;
    private readonly ILogger _logger;
    private readonly Dictionary<UdpEndpoint, ClientSession> _sessions = [];
    private readonly List<UdpEndpoint> _expiredEndpoints = [];
    private readonly long _timeoutTimestampTicks;
    private readonly int _snapshotInterval;
    private readonly int _tickRate;
    private uint _nextClientId = 1;
    private bool _disposed;

    /// <summary>Creates and binds one nonblocking authoritative UDP endpoint.</summary>
    /// <param name="port">IPv4 UDP port, or zero to request an ephemeral port.</param>
    /// <param name="tickRate">Authoritative fixed ticks per second.</param>
    /// <param name="snapshotRate">Authoritative snapshots per second.</param>
    /// <param name="clientTimeout">Silence duration after which a session expires.</param>
    /// <param name="terrain">Shared authored terrain collision.</param>
    /// <param name="logger">Server network logger.</param>
    internal UdpGameServer(
        int port,
        int tickRate,
        int snapshotRate,
        TimeSpan clientTimeout,
        ServerTerrainCollision terrain,
        ILogger logger)
    {
        if ((uint)port > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(port));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tickRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(snapshotRate);
        if (snapshotRate > tickRate)
            throw new ArgumentOutOfRangeException(nameof(snapshotRate));
        if (clientTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(clientTimeout));
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(logger);
        _terrain = terrain;
        _logger = logger;
        _tickRate = tickRate;
        _snapshotInterval = Math.Max(1, tickRate / snapshotRate);
        _timeoutTimestampTicks = checked((long)(clientTimeout.TotalSeconds * Stopwatch.Frequency));
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            Blocking = false
        };
        _socket.Bind(new IPEndPoint(IPAddress.Any, port));
    }

    /// <summary>Gets the actual bound UDP port.</summary>
    internal int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;

    /// <summary>Gets the number of authenticated live client sessions.</summary>
    internal int ClientCount => _sessions.Count;

    /// <summary>Processes pending inputs, advances players, and emits due snapshots.</summary>
    /// <param name="serverTick">Tick being simulated.</param>
    /// <param name="deltaTime">Fixed simulation duration.</param>
    internal void Step(long serverTick, float deltaTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ReceivePending(serverTick);
        foreach (var session in _sessions.Values)
            session.Motor.Simulate(_terrain, deltaTime);
        if (serverTick % _snapshotInterval == 0)
            SendSnapshots(serverTick);
        ExpireSilentSessions();
    }

    /// <summary>Drains a bounded number of datagrams without blocking the fixed tick.</summary>
    /// <param name="serverTick">Current authoritative tick used by welcome messages.</param>
    private void ReceivePending(long serverTick)
    {
        Span<byte> buffer = stackalloc byte[256];
        for (var datagram = 0;
             datagram < MaximumDatagramsPerTick && _socket.Poll(0, SelectMode.SelectRead);
             datagram++)
        {
            int length;
            try
            {
                length = _socket.ReceiveFrom(buffer, SocketFlags.None, _receiveAddress);
            }
            catch (SocketException exception) when (
                exception.SocketErrorCode is SocketError.WouldBlock or SocketError.TryAgain)
            {
                return;
            }
            ProcessDatagram(buffer[..length], UdpEndpoint.From(_receiveAddress), serverTick);
        }
    }

    /// <summary>Validates and applies one received protocol datagram.</summary>
    /// <param name="datagram">Received bytes.</param>
    /// <param name="endpoint">Claimed network source.</param>
    /// <param name="serverTick">Current authoritative tick.</param>
    private void ProcessDatagram(
        ReadOnlySpan<byte> datagram,
        UdpEndpoint endpoint,
        long serverTick)
    {
        if (GameProtocol.TryReadClientHello(datagram, out var nonce))
        {
            AcceptOrRefresh(endpoint, nonce, serverTick);
            return;
        }
        if (!_sessions.TryGetValue(endpoint, out var session))
            return;
        if (GameProtocol.TryReadClientInput(datagram, out var input))
        {
            if (input.ClientId != session.ClientId || input.SessionToken != session.Token ||
                session.HasInput && !IsNewerSequence(input.Sequence, session.LastInputSequence))
            {
                return;
            }
            session.HasInput = true;
            session.LastInputSequence = input.Sequence;
            session.LastReceiveTimestamp = Stopwatch.GetTimestamp();
            session.FacingYaw = MathF.IEEERemainder(input.FacingYaw, MathF.Tau);
            session.Motor.SetInput(input.Movement, input.Jump);
            return;
        }
        if (GameProtocol.TryReadClientDisconnect(datagram, out var clientId, out var token) &&
            clientId == session.ClientId && token == session.Token)
        {
            _sessions.Remove(endpoint);
            _logger.LogInformation("UDP client {ClientId} disconnected from {Address}:{Port}",
                session.ClientId, endpoint.Address, endpoint.Port);
        }
    }

    /// <summary>Creates a session or repeats its welcome after a lost response.</summary>
    /// <param name="endpoint">Client UDP endpoint.</param>
    /// <param name="nonce">Client nonce to echo for response correlation.</param>
    /// <param name="serverTick">Current authoritative tick.</param>
    private void AcceptOrRefresh(UdpEndpoint endpoint, uint nonce, long serverTick)
    {
        if (!_sessions.TryGetValue(endpoint, out var session))
        {
            Span<byte> tokenBytes = stackalloc byte[sizeof(ulong)];
            RandomNumberGenerator.Fill(tokenBytes);
            var token = BitConverter.ToUInt64(tokenBytes);
            if (token == 0)
                token = 1;
            session = new ClientSession(
                _nextClientId++, token, endpoint.CopyAddress(_receiveAddress),
                _terrain.GetDefaultSpawnPosition());
            _sessions.Add(endpoint, session);
            _logger.LogInformation("UDP client {ClientId} connected from {Address}:{Port}",
                session.ClientId, endpoint.Address, endpoint.Port);
        }
        session.LastReceiveTimestamp = Stopwatch.GetTimestamp();
        Span<byte> response = stackalloc byte[GameProtocol.ServerWelcomeSize];
        var welcome = new ServerWelcomeMessage(
            nonce,
            session.ClientId,
            session.Token,
            checked((ushort)_tickRate),
            serverTick,
            session.Motor.Position);
        GameProtocol.WriteServerWelcome(response, welcome);
        Send(response, session.Endpoint);
    }

    /// <summary>Sends the latest authoritative state to every live session.</summary>
    /// <param name="serverTick">Tick that produced the state.</param>
    private void SendSnapshots(long serverTick)
    {
        Span<byte> datagram = stackalloc byte[GameProtocol.ServerSnapshotSize];
        foreach (var session in _sessions.Values)
        {
            var snapshot = new ServerSnapshotMessage(
                session.ClientId,
                session.Token,
                session.LastInputSequence,
                serverTick,
                session.Motor.Position,
                session.Motor.Velocity,
                session.FacingYaw,
                session.Motor.IsGrounded);
            GameProtocol.WriteServerSnapshot(datagram, snapshot);
            Send(datagram, session.Endpoint);
        }
    }

    /// <summary>Removes sessions that stopped sending authenticated datagrams.</summary>
    private void ExpireSilentSessions()
    {
        var now = Stopwatch.GetTimestamp();
        _expiredEndpoints.Clear();
        foreach (var pair in _sessions)
        {
            if (now - pair.Value.LastReceiveTimestamp >= _timeoutTimestampTicks)
                _expiredEndpoints.Add(pair.Key);
        }
        for (var index = 0; index < _expiredEndpoints.Count; index++)
        {
            var endpoint = _expiredEndpoints[index];
            var clientId = _sessions[endpoint].ClientId;
            _sessions.Remove(endpoint);
            _logger.LogInformation("UDP client {ClientId} timed out at {Address}:{Port}",
                clientId, endpoint.Address, endpoint.Port);
        }
    }

    /// <summary>Sends one datagram while tolerating transient UDP backpressure.</summary>
    /// <param name="datagram">Complete protocol datagram.</param>
    /// <param name="endpoint">Remote client endpoint.</param>
    private void Send(ReadOnlySpan<byte> datagram, SocketAddress endpoint)
    {
        try
        {
            _socket.SendTo(datagram, SocketFlags.None, endpoint);
        }
        catch (SocketException exception) when (
            exception.SocketErrorCode is SocketError.WouldBlock or SocketError.TryAgain or
                SocketError.ConnectionReset)
        {
        }
    }

    /// <summary>Compares wrapping unsigned input sequence numbers.</summary>
    /// <param name="candidate">Newly received sequence.</param>
    /// <param name="current">Latest accepted sequence.</param>
    /// <returns>True when the candidate follows the current value.</returns>
    private static bool IsNewerSequence(uint candidate, uint current) =>
        unchecked((int)(candidate - current)) > 0;

    /// <summary>Closes the UDP socket and releases all sessions.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _socket.Dispose();
        _sessions.Clear();
        _expiredEndpoints.Clear();
    }

    /// <summary>Allocation-free dictionary identity decoded from an IPv4 socket address.</summary>
    /// <param name="Address">IPv4 address in network byte order.</param>
    /// <param name="Port">UDP port in host order.</param>
    private readonly record struct UdpEndpoint(uint Address, ushort Port)
    {
        /// <summary>Decodes one IPv4 socket address.</summary>
        /// <param name="address">Received socket address.</param>
        /// <returns>Stable value identity.</returns>
        internal static UdpEndpoint From(SocketAddress address)
        {
            if (address.Family != AddressFamily.InterNetwork || address.Size < 8)
                throw new InvalidDataException("The UDP server received a non-IPv4 address.");
            var port = (ushort)((address[2] << 8) | address[3]);
            var ipv4 = (uint)(address[4] << 24 | address[5] << 16 |
                address[6] << 8 | address[7]);
            return new UdpEndpoint(ipv4, port);
        }

        /// <summary>Copies the current receive address for retained asynchronous sends.</summary>
        /// <param name="source">Reusable receive address containing this endpoint.</param>
        /// <returns>Independently retained socket address.</returns>
        internal SocketAddress CopyAddress(SocketAddress source)
        {
            var copy = new SocketAddress(source.Family, source.Size);
            for (var index = 0; index < source.Size; index++)
                copy[index] = source[index];
            return copy;
        }
    }

    /// <summary>Stores authenticated network identity and authoritative movement state.</summary>
    private sealed class ClientSession
    {
        /// <summary>Creates one authenticated session.</summary>
        /// <param name="clientId">Server-assigned player identity.</param>
        /// <param name="token">Unpredictable session proof.</param>
        /// <param name="endpoint">Current UDP endpoint.</param>
        /// <param name="spawnPosition">Authoritative foot spawn.</param>
        internal ClientSession(
            uint clientId,
            ulong token,
            SocketAddress endpoint,
            Vector3 spawnPosition)
        {
            ClientId = clientId;
            Token = token;
            Endpoint = endpoint;
            Motor = new ServerCharacterMotor(spawnPosition);
            LastReceiveTimestamp = Stopwatch.GetTimestamp();
        }

        internal uint ClientId { get; }
        internal ulong Token { get; }
        internal SocketAddress Endpoint { get; }
        internal ServerCharacterMotor Motor { get; }
        internal bool HasInput { get; set; }
        internal uint LastInputSequence { get; set; }
        internal long LastReceiveTimestamp { get; set; }
        internal float FacingYaw { get; set; }
    }
}

using System.Net;
using System.Net.Sockets;
using System.Numerics;
using ExampleGame.Networking;
using Engine.Core;
using Engine.Graphics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ExampleGame.Server.Tests;

public sealed class GameProtocolTests
{
    /// <summary>Round-trips authenticated intent through the exact binary protocol.</summary>
    [Fact]
    public void ClientInput_RoundTrips()
    {
        var expected = new ClientInputMessage(
            7, 123456789ul, 42, new Vector2(0.25f, -0.75f), 1.5f, true);
        Span<byte> datagram = stackalloc byte[GameProtocol.ClientInputSize];

        GameProtocol.WriteClientInput(datagram, expected);

        Assert.True(GameProtocol.TryReadClientInput(datagram, out var actual));
        Assert.Equal(expected, actual);
    }

    /// <summary>Rejects malformed or nonfinite input before it reaches simulation state.</summary>
    [Fact]
    public void ClientInput_InvalidPayload_IsRejected()
    {
        var input = new ClientInputMessage(
            1, 2, 3, new Vector2(float.NaN, 0f), 0f, false);
        Span<byte> datagram = stackalloc byte[GameProtocol.ClientInputSize];
        GameProtocol.WriteClientInput(datagram, input);

        Assert.False(GameProtocol.TryReadClientInput(datagram, out _));
        Assert.False(GameProtocol.TryReadClientInput(datagram[..^1], out _));
    }

    /// <summary>Loads the scene-authored island dimensions and exact terrain height payload.</summary>
    [Fact]
    public void TerrainCollision_SceneIsland_UsesSharedTerrainResource()
    {
        var projectRoot = FindExampleGameRoot();
        var scene = SceneFileStore.Load(Path.Combine(projectRoot, "scenes", "scene.node"),
            ServerSceneNodeFactory.Instance);
        using var terrainStream = File.OpenRead(
            Path.Combine(projectRoot, "maps", "island.nterrain"));
        var resource = TerrainResource.Load(terrainStream);
        var expectedId = AssetId.Parse("019ffabc-d53b-792b-b6c9-8847cf62d626");
        var collision = new ServerTerrainCollision(scene.Root, reference =>
        {
            Assert.Equal(expectedId, reference.Asset);
            Assert.Equal("main", reference.SubAsset);
            return resource;
        });

        Assert.True(collision.TrySample(Vector3.Zero, out var sample));
        Assert.Equal(resource.Sample(0.5f, 0.5f) * 5f, sample.Height, 5);
        Assert.True(Vector3.Dot(sample.Normal, Vector3.UnitY) > 0f);
        Assert.Equal(1, collision.SurfaceCount);
    }

    /// <summary>Completes hello, authenticated input, simulation, and snapshot over loopback UDP.</summary>
    [Fact]
    public void UdpServer_LoopbackInput_ProducesAuthoritativeTerrainSnapshot()
    {
        var collision = CreateTerrainCollision();
        using var server = new UdpGameServer(
            0, 60, 60, TimeSpan.FromSeconds(5), collision, NullLogger.Instance);
        using var client = new Socket(
            AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        client.Connect(IPAddress.Loopback, server.Port);
        Span<byte> hello = stackalloc byte[GameProtocol.ClientHelloSize];
        GameProtocol.WriteClientHello(hello, 99);
        client.Send(hello);

        var welcomeBytes = ReceiveAfterTicks(client, server, 1,
            GameProtocol.ServerWelcomeSize);
        Assert.True(GameProtocol.TryReadServerWelcome(welcomeBytes, out var welcome));
        Assert.Equal(99u, welcome.ClientNonce);
        Drain(client);
        var input = new ClientInputMessage(
            welcome.ClientId, welcome.SessionToken, 1, Vector2.UnitX, 0f, false);
        Span<byte> inputBytes = stackalloc byte[GameProtocol.ClientInputSize];
        GameProtocol.WriteClientInput(inputBytes, input);
        client.Send(inputBytes);

        var snapshotBytes = ReceiveAfterTicks(client, server, 20,
            GameProtocol.ServerSnapshotSize);
        Assert.True(GameProtocol.TryReadServerSnapshot(snapshotBytes, out var snapshot));
        Assert.Equal(welcome.ClientId, snapshot.ClientId);
        Assert.Equal(1u, snapshot.AcknowledgedInput);
        Assert.True(snapshot.Position.X > welcome.SpawnPosition.X);
        Assert.True(snapshot.Grounded);
        Assert.Equal(1, server.ClientCount);
    }

    /// <summary>Uses the production client to authenticate, submit intent, and consume state.</summary>
    [Fact]
    public async Task UdpGameClient_LoopbackSession_ReceivesAuthoritativeState()
    {
        var collision = CreateTerrainCollision();
        using var server = new UdpGameServer(
            0, 60, 60, TimeSpan.FromSeconds(5), collision, NullLogger.Instance);
        var connectTask = Task.Run(() => UdpGameClient.Connect(
            "127.0.0.1", server.Port, TimeSpan.FromSeconds(2)));
        for (var tick = 1L; !connectTask.IsCompleted && tick <= 200; tick++)
        {
            server.Step(tick, 1f / 60f);
            await Task.Delay(1);
        }
        using var client = await connectTask;
        client.SendInput(1, Vector2.UnitX, 0.5f, false);

        ServerSnapshotMessage snapshot = default;
        var received = false;
        for (var tick = 201L; !received && tick <= 400; tick++)
        {
            server.Step(tick, 1f / 60f);
            received = client.TryReceiveLatestSnapshot(out snapshot) &&
                snapshot.AcknowledgedInput == 1;
            await Task.Delay(1);
        }

        Assert.True(received);
        Assert.Equal(client.ClientId, snapshot.ClientId);
        Assert.True(snapshot.Position.X > client.SpawnPosition.X);
        Assert.False(client.HasTimedOut(TimeSpan.FromSeconds(1)));
    }

    /// <summary>Reports a stable timeout when Windows returns UDP port-unreachable resets.</summary>
    [Fact]
    public void UdpGameClient_NoListeningServer_ThrowsTimeout()
    {
        int unusedPort;
        using (var reservation = new Socket(
                   AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            unusedPort = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        }

        Assert.Throws<TimeoutException>(() => UdpGameClient.Connect(
            "127.0.0.1", unusedPort, TimeSpan.FromMilliseconds(100)));
    }

    /// <summary>Discards snapshots queued alongside a handshake response.</summary>
    /// <param name="client">Connected loopback UDP client.</param>
    private static void Drain(Socket client)
    {
        Span<byte> discard = stackalloc byte[256];
        while (client.Poll(0, SelectMode.SelectRead))
            client.Receive(discard);
    }

    /// <summary>Creates the scene-authored shared terrain collision used by server tests.</summary>
    /// <returns>Terrain collision initialized from the example project.</returns>
    private static ServerTerrainCollision CreateTerrainCollision()
    {
        var projectRoot = FindExampleGameRoot();
        var scene = SceneFileStore.Load(Path.Combine(projectRoot, "scenes", "scene.node"),
            ServerSceneNodeFactory.Instance);
        using var terrainStream = File.OpenRead(
            Path.Combine(projectRoot, "maps", "island.nterrain"));
        var resource = TerrainResource.Load(terrainStream);
        return new ServerTerrainCollision(scene.Root, _ => resource);
    }

    /// <summary>Runs fixed ticks until one exact-sized UDP response is available.</summary>
    /// <param name="client">Connected loopback UDP client.</param>
    /// <param name="server">In-process authoritative server.</param>
    /// <param name="firstTick">First authoritative tick number.</param>
    /// <param name="expectedSize">Expected response byte count.</param>
    /// <returns>Received exact protocol datagram.</returns>
    private static byte[] ReceiveAfterTicks(
        Socket client,
        UdpGameServer server,
        long firstTick,
        int expectedSize)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            server.Step(firstTick + attempt, 1f / 60f);
            if (client.Poll(10_000, SelectMode.SelectRead))
            {
                var result = new byte[expectedSize];
                var received = client.Receive(result);
                Assert.Equal(expectedSize, received);
                return result;
            }
            Thread.Sleep(1);
        }
        throw new TimeoutException("The loopback UDP server did not respond.");
    }

    /// <summary>Finds the checked-out example game from test output or repository execution.</summary>
    /// <returns>Absolute example-game project directory.</returns>
    private static string FindExampleGameRoot()
    {
        var workingDirectoryCandidate = Path.Combine(
            Directory.GetCurrentDirectory(), "example_game");
        if (File.Exists(Path.Combine(
                workingDirectoryCandidate, "scenes", "scene.node")))
        {
            return workingDirectoryCandidate;
        }
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "example_game");
            if (File.Exists(Path.Combine(candidate, "scenes", "scene.node")))
                return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the example_game project.");
    }
}

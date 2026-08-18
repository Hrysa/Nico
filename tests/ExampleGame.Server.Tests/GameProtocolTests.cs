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
            7, 123456789ul, 42, new Vector2(0.25f, -0.75f), 1.5f, true, true);
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
            1, 2, 3, new Vector2(float.NaN, 0f), 0f, false, false);
        Span<byte> datagram = stackalloc byte[GameProtocol.ClientInputSize];
        GameProtocol.WriteClientInput(datagram, input);

        Assert.False(GameProtocol.TryReadClientInput(datagram, out _));
        Assert.False(GameProtocol.TryReadClientInput(datagram[..^1], out _));
    }

    /// <summary>Round-trips authoritative attacks and both fixed monster slots.</summary>
    [Fact]
    public void ServerSnapshot_CombatState_RoundTrips()
    {
        var expected = new ServerSnapshotMessage(
            4, 9, 7, 123,
            new Vector3(1f, 2f, 3f),
            new Vector3(4f, 5f, 6f),
            0.75f,
            true,
            12,
            new MonsterSnapshotMessage(new Vector3(7f, 8f, 9f), 1.25f, 50),
            new MonsterSnapshotMessage(new Vector3(-1f, 3f, 2f), -0.5f, 0));
        Span<byte> datagram = stackalloc byte[GameProtocol.ServerSnapshotSize];

        GameProtocol.WriteServerSnapshot(datagram, expected);

        Assert.True(GameProtocol.TryReadServerSnapshot(datagram, out var actual));
        Assert.Equal(expected, actual);
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

    /// <summary>Authors two scripted monsters from the skinned mesh contained in animations.</summary>
    [Fact]
    public void CombatScene_ContainsTwoAnimationMeshMonsters()
    {
        var projectRoot = FindExampleGameRoot();
        var scene = SceneFileStore.Load(Path.Combine(projectRoot, "scenes", "scene.node"),
            ServerSceneNodeFactory.Instance);
        var monsterAsset = AssetId.Parse("019ff631-d738-7c7e-b986-bc383379cb20");
        var monsterScript = AssetId.Parse("d636b268-c90d-46d8-9728-300866a90e50");
        var monsters = scene.MeshInstances
            .Where(instance => instance.Name.StartsWith("Monster ", StringComparison.Ordinal))
            .OrderBy(instance => instance.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(2, monsters.Length);
        Assert.Equal(["Monster 1", "Monster 2"], monsters.Select(monster => monster.Name));
        for (var index = 0; index < monsters.Length; index++)
        {
            Assert.Equal(new AssetReference(monsterAsset, "mesh/Mesh/0"), monsters[index].Mesh);
            var script = Assert.Single(monsters[index].Components.OfType<ScriptComponent>());
            Assert.Equal(monsterScript, script.ScriptId);
        }
    }

    /// <summary>Replaces the server ground surface when a terrain resource is reimported.</summary>
    [Fact]
    public void TerrainCollision_Reload_UsesUpdatedHeightResource()
    {
        var reference = new AssetReference(AssetId.New(), "main");
        var root = new Node3D();
        var terrainNode = new Node3D();
        terrainNode.AddComponent(new TerrainColliderComponent
        {
            TerrainData = reference,
            HorizontalSize = new Vector2(10f),
            HeightScale = 2f
        });
        root.AddChild(terrainNode);
        var resource = new TerrainResource(2, 2, new float[4]);
        var collision = new ServerTerrainCollision(root, _ => resource);
        Assert.True(collision.TrySample(Vector3.Zero, out var initial));

        resource = new TerrainResource(2, 2, [1f, 1f, 1f, 1f]);
        collision.Reload(root, _ => resource);

        Assert.True(collision.TrySample(Vector3.Zero, out var updated));
        Assert.Equal(0f, initial.Height);
        Assert.Equal(2f, updated.Height);
        Assert.Equal(1, collision.SurfaceCount);
    }

    /// <summary>Blocks a grounded character at a steep terrain face without losing support.</summary>
    [Fact]
    public void CharacterMotor_SteepTerrain_RemainsGrounded()
    {
        var reference = new AssetReference(AssetId.New(), "main");
        var root = new Node3D();
        var terrainNode = new Node3D();
        terrainNode.AddComponent(new TerrainColliderComponent
        {
            TerrainData = reference,
            HorizontalSize = new Vector2(3f, 1f),
            HeightScale = 2f
        });
        root.AddChild(terrainNode);
        var resource = new TerrainResource(4, 2,
        [
            0f, 0f, 2f, 2f,
            0f, 0f, 2f, 2f
        ]);
        var collision = new ServerTerrainCollision(root, _ => resource);
        var motor = new ServerCharacterMotor(new Vector3(-1.25f, 0f, 0f));
        motor.SetInput(Vector2.UnitX, jump: false);

        for (var tick = 0; tick < 120; tick++)
        {
            motor.Simulate(collision, 1f / 60f);
            Assert.True(motor.IsGrounded);
        }

        Assert.True(motor.Position.X < 0.5f);
        Assert.True(motor.Position.Y >= 0f);
    }

    /// <summary>Does not turn terrain-following height correction into upward launch velocity.</summary>
    [Fact]
    public void CharacterMotor_WalksOffRisingTerrain_WithoutLaunching()
    {
        var reference = new AssetReference(AssetId.New(), "main");
        var root = new Node3D();
        var terrainNode = new Node3D();
        terrainNode.AddComponent(new TerrainColliderComponent
        {
            TerrainData = reference,
            HorizontalSize = new Vector2(1.1f, 1f),
            HeightScale = 1f
        });
        root.AddChild(terrainNode);
        var resource = new TerrainResource(2, 2,
        [
            0f, 1f,
            0f, 1f
        ]);
        var collision = new ServerTerrainCollision(root, _ => resource);
        var start = new Vector3(-0.5f, 0f, 0f);
        Assert.True(collision.TrySample(start, out var ground));
        start.Y = ground.Height;
        var motor = new ServerCharacterMotor(start);
        motor.SetInput(Vector2.UnitX, jump: false);

        for (var tick = 0; tick < 120 && motor.IsGrounded; tick++)
            motor.Simulate(collision, 1f / 60f);

        Assert.False(motor.IsGrounded);
        Assert.True(motor.Velocity.Y <= 0.001f);
    }

    /// <summary>Returns an airborne character to walkable ground after contacting a steep slope.</summary>
    [Fact]
    public void CharacterMotor_JumpsIntoSteepSlope_LandsOnWalkableGround()
    {
        var reference = new AssetReference(AssetId.New(), "main");
        var root = new Node3D();
        var terrainNode = new Node3D();
        terrainNode.AddComponent(new TerrainColliderComponent
        {
            TerrainData = reference,
            HorizontalSize = new Vector2(4f, 1f),
            HeightScale = 1f
        });
        root.AddChild(terrainNode);
        var resource = new TerrainResource(9, 2,
        [
            0f, 0f, 0.75f, 1.5f, 2.25f, 3f, 3.75f, 4.5f, 5.25f,
            0f, 0f, 0.75f, 1.5f, 2.25f, 3f, 3.75f, 4.5f, 5.25f
        ]);
        var collision = new ServerTerrainCollision(root, _ => resource);
        var start = new Vector3(-1.75f, 0f, 0f);
        var motor = new ServerCharacterMotor(start);
        motor.SetInput(Vector2.UnitX, jump: true);

        for (var tick = 0; tick < 240; tick++)
            motor.Simulate(collision, 1f / 60f);

        Assert.True(motor.IsGrounded);
        Assert.True(motor.Position.X < -1f);
        Assert.True(collision.TrySample(motor.Position, out var landedGround));
        Assert.Equal(landedGround.Height, motor.Position.Y, 4);
    }

    /// <summary>Completes hello, authenticated input, simulation, and snapshot over loopback UDP.</summary>
    [Fact]
    public void UdpServer_LoopbackInput_ProducesAuthoritativeTerrainSnapshot()
    {
        var collision = CreateTerrainCollision();
        using var server = new UdpGameServer(
            0, 60, 60, TimeSpan.FromSeconds(5), collision,
            CreateMonsterSpawns(collision), NullLogger.Instance);
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
            welcome.ClientId, welcome.SessionToken, 1, Vector2.UnitX, 0f, false, false);
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

    /// <summary>Accepts an authenticated attack and damages a monster inside its facing arc.</summary>
    [Fact]
    public void UdpServer_Attack_DamagesAuthoritativeMonster()
    {
        var collision = CreateTerrainCollision();
        var spawn = collision.GetDefaultSpawnPosition();
        var monsterSpawns = new[]
        {
            spawn + new Vector3(0f, 0f, 1.5f),
            spawn + new Vector3(10f, 0f, 10f)
        };
        using var server = new UdpGameServer(
            0, 60, 60, TimeSpan.FromSeconds(5), collision,
            monsterSpawns, NullLogger.Instance);
        using var client = new Socket(
            AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        client.Connect(IPAddress.Loopback, server.Port);
        Span<byte> hello = stackalloc byte[GameProtocol.ClientHelloSize];
        GameProtocol.WriteClientHello(hello, 100);
        client.Send(hello);
        var welcomeBytes = ReceiveAfterTicks(client, server, 1,
            GameProtocol.ServerWelcomeSize);
        Assert.True(GameProtocol.TryReadServerWelcome(welcomeBytes, out var welcome));
        Drain(client);
        var input = new ClientInputMessage(
            welcome.ClientId, welcome.SessionToken, 1, Vector2.Zero, 0f, false, true);
        Span<byte> inputBytes = stackalloc byte[GameProtocol.ClientInputSize];
        GameProtocol.WriteClientInput(inputBytes, input);
        client.Send(inputBytes);

        var snapshotBytes = ReceiveAfterTicks(client, server, 2,
            GameProtocol.ServerSnapshotSize);
        Assert.True(GameProtocol.TryReadServerSnapshot(snapshotBytes, out var snapshot));

        Assert.Equal(1u, snapshot.AttackSequence);
        Assert.Equal(50, snapshot.Monster0.Health);
        Assert.Equal(100, snapshot.Monster1.Health);

        Drain(client);
        input = input with { Sequence = 2 };
        GameProtocol.WriteClientInput(inputBytes, input);
        client.Send(inputBytes);
        snapshotBytes = ReceiveAfterTicks(client, server, 31,
            GameProtocol.ServerSnapshotSize);
        Assert.True(GameProtocol.TryReadServerSnapshot(snapshotBytes, out snapshot));

        Assert.Equal(2u, snapshot.AttackSequence);
        Assert.Equal(0, snapshot.Monster0.Health);
    }

    /// <summary>Keeps converging monsters apart to prevent coplanar model z-fighting.</summary>
    [Fact]
    public void UdpServer_OverlappingMonsters_AreSeparatedAuthoritatively()
    {
        var collision = CreateTerrainCollision();
        var spawn = collision.GetDefaultSpawnPosition();
        var monsterSpawn = spawn + new Vector3(0f, 0f, 4f);
        using var server = new UdpGameServer(
            0, 60, 60, TimeSpan.FromSeconds(5), collision,
            [monsterSpawn, monsterSpawn], NullLogger.Instance);
        using var client = new Socket(
            AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        client.Connect(IPAddress.Loopback, server.Port);
        Span<byte> hello = stackalloc byte[GameProtocol.ClientHelloSize];
        GameProtocol.WriteClientHello(hello, 101);
        client.Send(hello);

        var welcomeBytes = ReceiveAfterTicks(client, server, 1,
            GameProtocol.ServerWelcomeSize);
        Assert.True(GameProtocol.TryReadServerWelcome(welcomeBytes, out _));
        Drain(client);
        var snapshotBytes = ReceiveAfterTicks(client, server, 2,
            GameProtocol.ServerSnapshotSize);
        Assert.True(GameProtocol.TryReadServerSnapshot(snapshotBytes, out var snapshot));

        var separation = new Vector2(
            snapshot.Monster1.Position.X - snapshot.Monster0.Position.X,
            snapshot.Monster1.Position.Z - snapshot.Monster0.Position.Z);
        Assert.True(separation.Length() >= 0.99f);
    }

    /// <summary>Uses the production client to authenticate, submit intent, and consume state.</summary>
    [Fact]
    public async Task UdpGameClient_LoopbackSession_ReceivesAuthoritativeState()
    {
        var collision = CreateTerrainCollision();
        using var server = new UdpGameServer(
            0, 60, 60, TimeSpan.FromSeconds(5), collision,
            CreateMonsterSpawns(collision), NullLogger.Instance);
        var connectTask = Task.Run(() => UdpGameClient.Connect(
            "127.0.0.1", server.Port, TimeSpan.FromSeconds(2)));
        for (var tick = 1L; !connectTask.IsCompleted && tick <= 200; tick++)
        {
            server.Step(tick, 1f / 60f);
            await Task.Delay(1);
        }
        using var client = await connectTask;
        client.SendInput(1, Vector2.UnitX, 0.5f, false, false);

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

    /// <summary>Creates two deterministic monster positions on the shared test terrain.</summary>
    /// <param name="collision">Terrain used to find the player spawn.</param>
    /// <returns>Two fixed monster spawn positions.</returns>
    private static Vector3[] CreateMonsterSpawns(ServerTerrainCollision collision)
    {
        var spawn = collision.GetDefaultSpawnPosition();
        return
        [
            spawn + new Vector3(6f, 0f, 6f),
            spawn + new Vector3(-6f, 0f, 6f)
        ];
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

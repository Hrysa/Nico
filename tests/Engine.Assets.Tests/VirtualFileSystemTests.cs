using System.Text;
using Engine.Assets;
using Xunit;

namespace Engine.Assets.Tests;

public class VirtualFileSystemTests
{
    /// <summary>Verifies directory mounts read contained files and reject parent traversal.</summary>
    [Fact]
    public void DirectoryFileSystem_ReadAndEnumerate_StayInsideRoot()
    {
        using var temporary = new TemporaryDirectory();
        var directory = Path.Combine(temporary.Path, "materials");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "stone.bin"), "stone");
        var filesystem = new DirectoryVirtualFileSystem(temporary.Path);

        using var stream = filesystem.OpenRead("materials/stone.bin");
        using var reader = new StreamReader(stream);

        Assert.Equal("stone", reader.ReadToEnd());
        Assert.Equal(["materials/stone.bin"], filesystem.Enumerate("materials"));
        Assert.Throws<ArgumentException>(() => filesystem.OpenRead("../outside.bin"));
    }

    /// <summary>Verifies in-memory files copy input bytes and enumerate immediate children.</summary>
    [Fact]
    public void MemoryFileSystem_Set_CopiesAndEnumeratesContent()
    {
        var bytes = Encoding.UTF8.GetBytes("shader");
        var filesystem = new MemoryVirtualFileSystem();
        filesystem.Set("shaders/ui/main.spv", bytes);
        bytes[0] = 0;

        using var stream = filesystem.OpenRead("shaders/ui/main.spv");
        using var reader = new StreamReader(stream);

        Assert.Equal("shader", reader.ReadToEnd());
        Assert.Equal(["shaders/ui"], filesystem.Enumerate("shaders"));
    }

    /// <summary>Verifies higher-priority mount layers override files while enumeration merges layers.</summary>
    [Fact]
    public void MountedFileSystem_Priority_OverridesAndMergesLayers()
    {
        var engine = new MemoryVirtualFileSystem();
        engine.Set("config.txt", "engine"u8);
        engine.Set("shared.txt", "shared"u8);
        var project = new MemoryVirtualFileSystem();
        project.Set("config.txt", "project"u8);
        project.Set("game.txt", "game"u8);
        var mounted = new MountedVirtualFileSystem();
        mounted.Mount("game", engine, priority: 0);
        mounted.Mount("game", project, priority: 100);

        using var stream = mounted.OpenRead("game/config.txt");
        using var reader = new StreamReader(stream);

        Assert.Equal("project", reader.ReadToEnd());
        Assert.Equal(["game/config.txt", "game/game.txt", "game/shared.txt"],
            mounted.Enumerate("game"));
        Assert.Equal(["game"], mounted.Enumerate(string.Empty));
    }

    /// <summary>Verifies storage routing hides loose, virtual, and package location differences.</summary>
    [Fact]
    public void AssetStorageRouter_AllLocationKinds_OpenEquivalentStreams()
    {
        using var temporary = new TemporaryDirectory();
        var loosePath = Path.Combine(temporary.Path, "loose.bin");
        File.WriteAllText(loosePath, "loose");
        var memory = new MemoryVirtualFileSystem();
        memory.Set("virtual.bin", "virtual"u8);
        var mounted = new MountedVirtualFileSystem();
        mounted.Mount("game", memory);
        var storage = new AssetStorageRouter(mounted, [new TestPackageReader()]);

        Assert.Equal("loose", Read(storage.OpenRead(new LooseFileAssetLocation(loosePath))));
        Assert.Equal("virtual", Read(storage.OpenRead(
            new VirtualFileAssetLocation("game/virtual.bin"))));
        Assert.Equal("package:entry-1", Read(storage.OpenRead(
            new PackageEntryAssetLocation("content", "entry-1"))));
    }

    /// <summary>Reads one complete UTF-8 stream and disposes it.</summary>
    /// <param name="stream">Readable stream.</param>
    /// <returns>Complete decoded stream text.</returns>
    private static string Read(Stream stream)
    {
        using (stream)
        using (var reader = new StreamReader(stream))
            return reader.ReadToEnd();
    }

    /// <summary>Provides logical package entries for routing tests.</summary>
    private sealed class TestPackageReader : IPackageReader
    {
        /// <inheritdoc/>
        public string Id => "content";

        /// <inheritdoc/>
        public Stream OpenRead(string entry)
        {
            return new MemoryStream(Encoding.UTF8.GetBytes($"package:{entry}"), writable: false);
        }
    }

    /// <summary>Owns an isolated test directory and removes it after use.</summary>
    private sealed class TemporaryDirectory : IDisposable
    {
        /// <summary>Gets the absolute isolated directory path.</summary>
        public string Path { get; }

        /// <summary>Creates an isolated directory.</summary>
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("nico-vfs-").FullName;
        }

        /// <summary>Removes the isolated directory and its contents.</summary>
        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

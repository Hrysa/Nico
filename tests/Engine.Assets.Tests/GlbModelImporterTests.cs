using System.Numerics;
using System.Text;
using System.Text.Json;
using Engine.Core;
using Xunit;

namespace Engine.Assets.Tests;

public sealed class GlbModelImporterTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("nico-glb-").FullName;

    /// <summary>Imports indexed geometry and generates missing normals.</summary>
    [Fact]
    public void Import_MinimalTriangle_WritesVersionedMeshArtifact()
    {
        var sourcePath = Path.Combine(_directory, "triangle.glb");
        WriteMinimalGlb(sourcePath);
        var staging = Path.Combine(_directory, "staging");
        var settings = JsonDocument.Parse("{}").RootElement.Clone();
        var metadata = new AssetMetadata(1, AssetId.New(), "gltf-model", settings);
        var context = new AssetImportContext(sourcePath, metadata, "editor", staging,
            CancellationToken.None);

        var result = new GlbModelImporter().Import(context);

        Assert.Equal(3, result.Artifacts.Count);
        var artifact = Assert.Single(result.Artifacts, item =>
            item.ContentType == "nico/static-mesh");
        Assert.Equal("mesh/Triangle/0", artifact.Key);
        Assert.Equal("nico/static-mesh", artifact.ContentType);
        using var reader = new BinaryReader(File.OpenRead(Path.Combine(staging,
            artifact.RelativePath)));
        Assert.Equal("NMESH001", Encoding.ASCII.GetString(reader.ReadBytes(8)));
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(3u, reader.ReadUInt32());
        Assert.Equal(3u, reader.ReadUInt32());
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal(Vector3.Zero, ReadVector3(reader));
        Assert.Equal(Vector3.UnitZ, ReadVector3(reader));
        var materialArtifact = Assert.Single(result.Artifacts, item =>
            item.ContentType == "nico/standard-material");
        using var materialReader = new BinaryReader(File.OpenRead(Path.Combine(staging,
            materialArtifact.RelativePath)));
        Assert.Equal("NMATL001", Encoding.ASCII.GetString(materialReader.ReadBytes(8)));
        Assert.Equal(1u, materialReader.ReadUInt32());
        Assert.Equal(new[] { 0.25f, 0.5f, 0.75f, 1f },
            Enumerable.Range(0, 4).Select(_ => materialReader.ReadSingle()));
        Assert.Equal(0.2f, materialReader.ReadSingle());
        Assert.Equal(0.6f, materialReader.ReadSingle());
        Assert.True(materialReader.ReadBoolean());
        Assert.Equal(0, materialReader.ReadInt32());
        var textureArtifact = Assert.Single(result.Artifacts, item =>
            item.ContentType == "nico/texture2d");
        Assert.Equal("texture/0", textureArtifact.Key);
        using var textureReader = new BinaryReader(File.OpenRead(Path.Combine(staging,
            textureArtifact.RelativePath)));
        Assert.Equal("NTEX0001", Encoding.ASCII.GetString(textureReader.ReadBytes(8)));
        Assert.Equal(1u, textureReader.ReadUInt32());
        Assert.Equal(1u, textureReader.ReadUInt32());
        Assert.Equal(1u, textureReader.ReadUInt32());
        Assert.Equal(1, textureReader.ReadByte());
        Assert.Equal(4, textureReader.ReadBytes(4).Length);
    }

    /// <summary>Removes temporary test data.</summary>
    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Reads one vector from the mesh artifact.</summary>
    /// <param name="reader">Artifact reader.</param>
    /// <returns>The decoded vector.</returns>
    private static Vector3 ReadVector3(BinaryReader reader)
    {
        return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    /// <summary>Writes a GLB containing one indexed triangle without normals.</summary>
    /// <param name="path">Destination path.</param>
    private static void WriteMinimalGlb(string path)
    {
        using var binaryStream = new MemoryStream();
        using (var binary = new BinaryWriter(binaryStream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var value in new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f })
                binary.Write(value);
            binary.Write((ushort)0);
            binary.Write((ushort)1);
            binary.Write((ushort)2);
            binary.Write((ushort)0);
            binary.Write(Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        }
        var json = """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":112}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},
                            {"buffer":0,"byteOffset":36,"byteLength":6},
                            {"buffer":0,"byteOffset":44,"byteLength":68}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},
                          {"bufferView":1,"componentType":5123,"count":3,"type":"SCALAR"}],
             "materials":[{"name":"Blue","doubleSided":true,
               "pbrMetallicRoughness":{"baseColorFactor":[0.25,0.5,0.75,1],
               "metallicFactor":0.2,"roughnessFactor":0.6,
               "baseColorTexture":{"index":0}}}],
             "images":[{"bufferView":2,"mimeType":"image/png"}],
             "textures":[{"source":0}],
             "meshes":[{"name":"Triangle","primitives":[{"attributes":{"POSITION":0},
               "indices":1,"material":0}]}]}
            """;
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        Array.Resize(ref jsonBytes, (jsonBytes.Length + 3) & ~3);
        for (var index = Encoding.UTF8.GetByteCount(json); index < jsonBytes.Length; index++)
            jsonBytes[index] = 0x20;
        var binaryBytes = binaryStream.ToArray();
        using var output = new BinaryWriter(File.Create(path));
        output.Write(0x46546C67u);
        output.Write(2u);
        output.Write(checked((uint)(12 + 8 + jsonBytes.Length + 8 + binaryBytes.Length)));
        output.Write(checked((uint)jsonBytes.Length));
        output.Write(0x4E4F534Au);
        output.Write(jsonBytes);
        output.Write(checked((uint)binaryBytes.Length));
        output.Write(0x004E4942u);
        output.Write(binaryBytes);
    }
}

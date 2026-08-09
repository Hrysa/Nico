using System.Numerics;
using System.Text;
using System.Text.Json;
using Engine.Core;
using Engine.Graphics;
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
        Assert.Equal(2u, reader.ReadUInt32());
        Assert.Equal(3u, reader.ReadUInt32());
        Assert.Equal(3u, reader.ReadUInt32());
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal(new Vector3(5f, 6f, 7f), ReadVector3(reader));
        Assert.Equal(Vector3.UnitZ, ReadVector3(reader));
        reader.BaseStream.Position += sizeof(float) * 6;
        Assert.Equal(new Vector4(0.2f, 0.4f, 0.6f, 1f), ReadVector4(reader));
        Assert.Equal(new Vector3(7f, 6f, 7f), ReadVector3(reader));
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

    /// <summary>Imports joint weights, inverse binds, and animation channels.</summary>
    [Fact]
    public void Import_SkinnedTriangle_WritesPlayableSkinnedMesh()
    {
        var sourcePath = Path.Combine(_directory, "skinned.glb");
        WriteSkinnedGlb(sourcePath);
        var staging = Path.Combine(_directory, "skinned-staging");
        var settings = JsonDocument.Parse("{}").RootElement.Clone();
        var context = new AssetImportContext(sourcePath,
            new AssetMetadata(1, AssetId.New(), "gltf-model", settings),
            "editor", staging, CancellationToken.None);

        var result = new GlbModelImporter().Import(context);

        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal("nico/skinned-mesh", artifact.ContentType);
        Assert.NotNull(result.Objects);
        var armatureNodes = result.Objects!.Where(item => item.Kind == "node").ToArray();
        Assert.Equal(4, armatureNodes.Length);
        Assert.Equal("node/0", Assert.Single(armatureNodes,
            item => item.Name == "Helper").ParentKey);
        Assert.Equal("Rig", Assert.Single(result.Objects,
            item => item.Kind == "skeleton").Name);
        Assert.Equal("Move", Assert.Single(result.Objects,
            item => item.Kind == "animation").Name);
        using var stream = File.OpenRead(Path.Combine(staging, artifact.RelativePath));
        var resource = SkinnedMeshResource.Load(stream);
        Assert.Equal(2, resource.Skeleton.JointCount);
        Assert.Equal(1u, resource.Influences[1].Joint0);
        var animation = Assert.Single(resource.Animations);
        Assert.Equal("Move", animation.Name);
        var player = new AnimationPlayer(resource);
        player.Play();
        player.Update(0.5d);
        Assert.Equal(0.5f, player.Pose.SkinMatrices[1].M41, 5);
    }

    /// <summary>Preserves the source mesh transform that cancels inverse-bind coordinates.</summary>
    [Fact]
    public void Import_TransformedArmature_ComposesToIdentityAtBindPose()
    {
        var sourcePath = Path.Combine(_directory, "transformed-armature.glb");
        WriteTransformedArmatureGlb(sourcePath);
        var staging = Path.Combine(_directory, "transformed-staging");
        var settings = JsonDocument.Parse("{}").RootElement.Clone();
        var context = new AssetImportContext(sourcePath,
            new AssetMetadata(1, AssetId.New(), "gltf-model", settings),
            "editor", staging, CancellationToken.None);

        var result = new GlbModelImporter().Import(context);
        var artifact = Assert.Single(result.Artifacts);
        using var stream = File.OpenRead(Path.Combine(staging, artifact.RelativePath));
        var resource = SkinnedMeshResource.Load(stream);
        var pose = new SkeletonPose(resource.Skeleton);

        Assert.Equal(0.01f, resource.MeshNodeTransform.M11, 5);
        AssertMatrixNearlyIdentity(
            pose.SkinMatrices[0] * resource.MeshNodeTransform);
    }

    /// <summary>Imports a mesh-free skin and clip as standalone skeletal animation.</summary>
    [Fact]
    public void Import_AnimationOnlyGlb_WritesBindableAnimationArtifact()
    {
        var sourcePath = Path.Combine(_directory, "idle.glb");
        WriteAnimationOnlyGlb(sourcePath);
        var staging = Path.Combine(_directory, "animation-staging");
        var settings = JsonDocument.Parse("{}").RootElement.Clone();
        var context = new AssetImportContext(sourcePath,
            new AssetMetadata(1, AssetId.New(), "gltf-model", settings),
            "editor", staging, CancellationToken.None);

        var result = new GlbModelImporter().Import(context);

        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal("animation/0", artifact.Key);
        Assert.Equal("nico/skeletal-animation", artifact.ContentType);
        var animationObject = Assert.Single(result.Objects!, item => item.Kind == "animation");
        Assert.Equal(artifact.Key, animationObject.ArtifactKey);
        using var stream = File.OpenRead(Path.Combine(staging, artifact.RelativePath));
        var resource = SkeletalAnimationResource.Load(stream);
        Assert.Equal("Idle", Assert.Single(resource.Animations).Name);
        var target = new SkeletonResource(
        [
            new SkeletonJoint("Hips", -1, JointTransform.Identity, Matrix4x4.Identity)
        ]);
        var bound = Assert.Single(resource.BindTo(target));
        Assert.Equal(1f, bound.Tracks[0]!.Translation!.Sample(1f).Y, 5);
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

    /// <summary>Reads one four-component vector from the mesh artifact.</summary>
    /// <param name="reader">Artifact reader.</param>
    /// <returns>The decoded vector.</returns>
    private static Vector4 ReadVector4(BinaryReader reader)
    {
        return new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle());
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
            foreach (var value in new[]
                     {
                         0.2f, 0.4f, 0.6f,
                         0.2f, 0.4f, 0.6f,
                         0.2f, 0.4f, 0.6f
                     })
                binary.Write(value);
            binary.Write((ushort)0);
            binary.Write((ushort)1);
            binary.Write((ushort)2);
            binary.Write((ushort)0);
            binary.Write(Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        }
        var json = """
            {"asset":{"version":"2.0"},"scene":0,
             "scenes":[{"nodes":[0]}],
             "nodes":[{"mesh":0,"translation":[5,6,7],"scale":[2,3,4]}],
             "buffers":[{"byteLength":148}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},
                            {"buffer":0,"byteOffset":36,"byteLength":36},
                            {"buffer":0,"byteOffset":72,"byteLength":6},
                            {"buffer":0,"byteOffset":80,"byteLength":68}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},
                          {"bufferView":2,"componentType":5123,"count":3,"type":"SCALAR"},
                          {"bufferView":1,"componentType":5126,"count":3,"type":"VEC3"}],
             "materials":[{"name":"Blue","doubleSided":true,
               "pbrMetallicRoughness":{"baseColorFactor":[0.25,0.5,0.75,1],
               "metallicFactor":0.2,"roughnessFactor":0.6,
               "baseColorTexture":{"index":0}}}],
             "images":[{"bufferView":3,"mimeType":"image/png"}],
             "textures":[{"source":0}],
             "meshes":[{"name":"Triangle","primitives":[{"attributes":{"POSITION":0,"COLOR_0":2},
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

    /// <summary>Writes a GLB containing a two-joint skinned triangle and one clip.</summary>
    /// <param name="path">Destination path.</param>
    private static void WriteSkinnedGlb(string path)
    {
        using var binaryStream = new MemoryStream();
        using (var binary = new BinaryWriter(binaryStream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var value in new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f })
                binary.Write(value);
            binary.Write(new byte[] { 0, 1, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 });
            foreach (var value in new[]
                     {
                         0.5f, 0.5f, 0f, 0f,
                         1f, 0f, 0f, 0f,
                         1f, 0f, 0f, 0f
                     })
                binary.Write(value);
            binary.Write((ushort)0);
            binary.Write((ushort)1);
            binary.Write((ushort)2);
            binary.Write((ushort)0);
            WriteMatrix(binary, Matrix4x4.Identity);
            WriteMatrix(binary, Matrix4x4.CreateTranslation(-Vector3.UnitX));
            binary.Write(0f);
            binary.Write(1f);
            foreach (var value in new[] { 0.5f, 0f, 0f, 1.5f, 0f, 0f })
                binary.Write(value);
        }
        var json = """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":264}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},
                            {"buffer":0,"byteOffset":36,"byteLength":12},
                            {"buffer":0,"byteOffset":48,"byteLength":48},
                            {"buffer":0,"byteOffset":96,"byteLength":6},
                            {"buffer":0,"byteOffset":104,"byteLength":128},
                            {"buffer":0,"byteOffset":232,"byteLength":8},
                            {"buffer":0,"byteOffset":240,"byteLength":24}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},
                          {"bufferView":1,"componentType":5121,"count":3,"type":"VEC4"},
                          {"bufferView":2,"componentType":5126,"count":3,"type":"VEC4"},
                          {"bufferView":3,"componentType":5123,"count":3,"type":"SCALAR"},
                          {"bufferView":4,"componentType":5126,"count":2,"type":"MAT4"},
                          {"bufferView":5,"componentType":5126,"count":2,"type":"SCALAR"},
                          {"bufferView":6,"componentType":5126,"count":2,"type":"VEC3"}],
             "nodes":[{"name":"Root","children":[1]},
                      {"name":"Helper","translation":[0.5,0,0],"children":[2]},
                      {"name":"Child","translation":[0.5,0,0]},
                      {"name":"Character","mesh":0,"skin":0}],
             "skins":[{"name":"Rig","joints":[0,2],"inverseBindMatrices":4,"skeleton":0}],
             "meshes":[{"name":"Character","primitives":[{"attributes":{"POSITION":0,
               "JOINTS_0":1,"WEIGHTS_0":2},"indices":3}]}],
             "animations":[{"name":"Move","samplers":[{"input":5,"output":6,
               "interpolation":"LINEAR"}],"channels":[{"sampler":0,
               "target":{"node":1,"path":"translation"}}]}]}
            """;
        WriteGlb(path, json, binaryStream.ToArray());
    }

    /// <summary>Writes a mesh-free GLB containing one skeleton and animation clip.</summary>
    /// <param name="path">Destination path.</param>
    private static void WriteAnimationOnlyGlb(string path)
    {
        using var binaryStream = new MemoryStream();
        using (var binary = new BinaryWriter(binaryStream, Encoding.UTF8, leaveOpen: true))
        {
            binary.Write(0f);
            binary.Write(1f);
            foreach (var value in new[] { 0f, 0f, 0f, 0f, 1f, 0f })
                binary.Write(value);
        }
        var json = """
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],
             "buffers":[{"byteLength":32}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":8},
                            {"buffer":0,"byteOffset":8,"byteLength":24}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":2,"type":"SCALAR"},
                          {"bufferView":1,"componentType":5126,"count":2,"type":"VEC3"}],
             "nodes":[{"name":"Armature","children":[1]},{"name":"Hips"}],
             "skins":[{"name":"Rig","joints":[1],"skeleton":1}],
             "animations":[{"name":"Idle","samplers":[{"input":0,"output":1,
               "interpolation":"LINEAR"}],"channels":[{"sampler":0,
               "target":{"node":1,"path":"translation"}}]}]}
            """;
        WriteGlb(path, json, binaryStream.ToArray());
    }

    /// <summary>Writes a GLB whose armature supplies unit conversion and axis correction.</summary>
    /// <param name="path">Destination path.</param>
    private static void WriteTransformedArmatureGlb(string path)
    {
        var armature = Matrix4x4.CreateScale(0.01f) *
            Matrix4x4.CreateRotationX(MathF.PI / 2f);
        Assert.True(Matrix4x4.Invert(armature, out var inverseArmature));
        using var binaryStream = new MemoryStream();
        using (var binary = new BinaryWriter(binaryStream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var value in new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f })
                binary.Write(value);
            binary.Write(new byte[12]);
            for (var index = 0; index < 3; index++)
            {
                binary.Write(1f);
                binary.Write(0f);
                binary.Write(0f);
                binary.Write(0f);
            }
            binary.Write((ushort)0);
            binary.Write((ushort)1);
            binary.Write((ushort)2);
            binary.Write((ushort)0);
            WriteMatrix(binary, inverseArmature);
        }
        var json = """
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[2]}],
             "buffers":[{"byteLength":168}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},
                            {"buffer":0,"byteOffset":36,"byteLength":12},
                            {"buffer":0,"byteOffset":48,"byteLength":48},
                            {"buffer":0,"byteOffset":96,"byteLength":6},
                            {"buffer":0,"byteOffset":104,"byteLength":64}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},
                          {"bufferView":1,"componentType":5121,"count":3,"type":"VEC4"},
                          {"bufferView":2,"componentType":5126,"count":3,"type":"VEC4"},
                          {"bufferView":3,"componentType":5123,"count":3,"type":"SCALAR"},
                          {"bufferView":4,"componentType":5126,"count":1,"type":"MAT4"}],
             "nodes":[{"name":"Root"},{"name":"Character","mesh":0,"skin":0},
                      {"name":"Armature","rotation":[0.70710678,0,0,0.70710678],
                       "scale":[0.01,0.01,0.01],"children":[0,1]}],
             "skins":[{"name":"Armature","joints":[0],"inverseBindMatrices":4,"skeleton":0}],
             "meshes":[{"name":"Character","primitives":[{"attributes":{"POSITION":0,
               "JOINTS_0":1,"WEIGHTS_0":2},"indices":3}]}]}
            """;
        WriteGlb(path, json, binaryStream.ToArray());
    }

    /// <summary>Asserts a matrix is identity within import precision.</summary>
    /// <param name="matrix">Matrix to verify.</param>
    private static void AssertMatrixNearlyIdentity(Matrix4x4 matrix)
    {
        var expected = Matrix4x4.Identity;
        var actualValues = new[]
        {
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44
        };
        var expectedValues = new[]
        {
            expected.M11, expected.M12, expected.M13, expected.M14,
            expected.M21, expected.M22, expected.M23, expected.M24,
            expected.M31, expected.M32, expected.M33, expected.M34,
            expected.M41, expected.M42, expected.M43, expected.M44
        };
        for (var index = 0; index < actualValues.Length; index++)
            Assert.Equal(expectedValues[index], actualValues[index], 4);
    }

    /// <summary>Writes one row-vector matrix using glTF's equivalent column-major sequence.</summary>
    /// <param name="writer">Binary output.</param>
    /// <param name="matrix">Row-vector matrix.</param>
    private static void WriteMatrix(BinaryWriter writer, Matrix4x4 matrix)
    {
        writer.Write(matrix.M11); writer.Write(matrix.M12); writer.Write(matrix.M13); writer.Write(matrix.M14);
        writer.Write(matrix.M21); writer.Write(matrix.M22); writer.Write(matrix.M23); writer.Write(matrix.M24);
        writer.Write(matrix.M31); writer.Write(matrix.M32); writer.Write(matrix.M33); writer.Write(matrix.M34);
        writer.Write(matrix.M41); writer.Write(matrix.M42); writer.Write(matrix.M43); writer.Write(matrix.M44);
    }

    /// <summary>Writes padded JSON and binary chunks into a GLB container.</summary>
    /// <param name="path">Destination path.</param>
    /// <param name="json">glTF JSON.</param>
    /// <param name="binary">Binary buffer.</param>
    private static void WriteGlb(string path, string json, byte[] binary)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        Array.Resize(ref jsonBytes, (jsonBytes.Length + 3) & ~3);
        for (var index = Encoding.UTF8.GetByteCount(json); index < jsonBytes.Length; index++)
            jsonBytes[index] = 0x20;
        Array.Resize(ref binary, (binary.Length + 3) & ~3);
        using var output = new BinaryWriter(File.Create(path));
        output.Write(0x46546C67u);
        output.Write(2u);
        output.Write(checked((uint)(12 + 8 + jsonBytes.Length + 8 + binary.Length)));
        output.Write(checked((uint)jsonBytes.Length));
        output.Write(0x4E4F534Au);
        output.Write(jsonBytes);
        output.Write(checked((uint)binary.Length));
        output.Write(0x004E4942u);
        output.Write(binary);
    }
}

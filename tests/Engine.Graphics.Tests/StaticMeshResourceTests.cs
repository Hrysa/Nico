using System.Numerics;
using System.Text;
using Xunit;

namespace Engine.Graphics.Tests;

public class StaticMeshResourceTests
{
    /// <summary>Loads a cooked RGBA8 texture and its sample color space.</summary>
    [Fact]
    public void LoadTexture_ValidArtifact_ReturnsPixels()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("NTEX0001"u8);
            writer.Write(1u);
            writer.Write(1u);
            writer.Write(1u);
            writer.Write((byte)1);
            writer.Write(new byte[] { 10, 20, 30, 255 });
        }
        stream.Position = 0;

        var texture = TextureResource.Load(stream);

        Assert.Equal(1u, texture.Width);
        Assert.Equal(1u, texture.Height);
        Assert.Equal(TextureColorSpace.Srgb, texture.ColorSpace);
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, texture.Pixels);
    }

    /// <summary>Loads standard material factors while retaining the unresolved texture slot.</summary>
    [Fact]
    public void LoadMaterial_ValidArtifact_ReturnsFactorsAndTextureSlot()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("NMATL001"u8);
            writer.Write(1u);
            foreach (var value in new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f })
                writer.Write(value);
            writer.Write(true);
            writer.Write(7);
        }
        stream.Position = 0;

        var (material, textureSlot) = StandardMaterialResource.Load(stream);

        Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 0.4f), material.BaseColor);
        Assert.Equal(0.5f, material.Metallic);
        Assert.Equal(0.6f, material.Roughness);
        Assert.True(material.DoubleSided);
        Assert.Equal(7, textureSlot);
    }

    /// <summary>Loads the importer artifact contract into typed model geometry.</summary>
    [Fact]
    public void Load_ValidArtifact_ReturnsGeometryAndBounds()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("NMESH001"u8);
            writer.Write(1u);
            writer.Write(3u);
            writer.Write(3u);
            writer.Write(2);
            WriteVertex(writer, Vector3.Zero);
            WriteVertex(writer, Vector3.UnitX);
            WriteVertex(writer, Vector3.UnitY);
            writer.Write(0u);
            writer.Write(1u);
            writer.Write(2u);
        }
        stream.Position = 0;

        var mesh = StaticMeshResource.Load(stream);

        Assert.Equal(3, mesh.Vertices.Length);
        Assert.Equal(new uint[] { 0, 1, 2 }, mesh.Indices);
        Assert.Equal(Vector3.Zero, mesh.BoundsMinimum);
        Assert.Equal(new Vector3(1f, 1f, 0f), mesh.BoundsMaximum);
        Assert.Equal(new Submesh(0, 3, 2), Assert.Single(mesh.Submeshes));
    }

    /// <summary>Writes one complete artifact vertex.</summary>
    /// <param name="writer">Artifact writer.</param>
    /// <param name="position">Vertex position.</param>
    private static void WriteVertex(BinaryWriter writer, Vector3 position)
    {
        writer.Write(position.X);
        writer.Write(position.Y);
        writer.Write(position.Z);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(1f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(1f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(1f);
    }
}

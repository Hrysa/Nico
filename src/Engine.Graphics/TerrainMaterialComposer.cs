using System.Numerics;
using Engine.Core;

namespace Engine.Graphics;

/// <summary>Pairs one authored terrain layer with its decoded texture resources.</summary>
/// <param name="Layer">Authored terrain-layer values.</param>
/// <param name="BaseColorTexture">Optional decoded base-color map.</param>
/// <param name="NormalTexture">Optional decoded normal map.</param>
/// <param name="MetallicRoughnessTexture">Optional decoded metallic-roughness map.</param>
public sealed record ResolvedTerrainLayer(
    TerrainLayerAsset Layer,
    TextureResource? BaseColorTexture,
    TextureResource? NormalTexture,
    TextureResource? MetallicRoughnessTexture);

/// <summary>Contains PBR maps composed from one painted terrain material.</summary>
/// <param name="Material">Standard factors consumed by the built-in PBR path.</param>
/// <param name="BaseColorTexture">Composed sRGB base-color map.</param>
/// <param name="NormalTexture">Composed linear normal map.</param>
/// <param name="MetallicRoughnessTexture">Composed linear metallic-roughness map.</param>
public sealed record ComposedTerrainMaterial(
    StandardMaterialAsset Material,
    TextureResource? BaseColorTexture,
    TextureResource? NormalTexture,
    TextureResource? MetallicRoughnessTexture);

/// <summary>Composes painted tileable terrain layers into the standard PBR texture contract.</summary>
public static class TerrainMaterialComposer
{
    /// <summary>Builds PBR textures from up to four painted terrain layers.</summary>
    /// <param name="material">Layer order and RGBA paint weights.</param>
    /// <param name="layers">Resolved layers in material order.</param>
    /// <returns>Composed renderer-ready material textures.</returns>
    public static ComposedTerrainMaterial Compose(
        TerrainMaterialAsset material,
        IReadOnlyList<ResolvedTerrainLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(layers);
        if (layers.Count != material.Layers.Count || layers.Count > TerrainMaterialAsset.MaximumLayers)
            throw new ArgumentException("Resolved terrain layers must match material layer order.",
                nameof(layers));
        if (layers.Count == 0)
            return new ComposedTerrainMaterial(new StandardMaterialAsset(), null, null, null);

        var width = checked((uint)material.Width);
        var height = checked((uint)material.Depth);
        var basePixels = new byte[checked(material.Width * material.Depth * 4)];
        var normalPixels = new byte[basePixels.Length];
        var metallicRoughnessPixels = new byte[basePixels.Length];
        for (var z = 0; z < material.Depth; z++)
        {
            var v = z / (float)(material.Depth - 1);
            for (var x = 0; x < material.Width; x++)
            {
                var u = x / (float)(material.Width - 1);
                var baseColor = Vector4.Zero;
                var normal = Vector3.Zero;
                var metallic = 0f;
                var roughness = 0f;
                var totalWeight = 0f;
                for (var layerIndex = 0; layerIndex < layers.Count; layerIndex++)
                {
                    var weight = material.GetWeight(x, z, layerIndex);
                    if (weight <= 0f)
                        continue;
                    var resolved = layers[layerIndex];
                    var authored = resolved.Layer;
                    var layerU = u * authored.Tiling.X;
                    var layerV = v * authored.Tiling.Y;
                    var sampledBase = SampleBaseColor(resolved.BaseColorTexture, layerU, layerV) *
                        authored.BaseColor;
                    var sampledNormal = SampleNormal(resolved.NormalTexture, layerU, layerV);
                    var sampledMetallicRoughness = SampleLinear(
                        resolved.MetallicRoughnessTexture, layerU, layerV, Vector4.One);
                    baseColor += sampledBase * weight;
                    normal += sampledNormal * weight;
                    metallic += authored.Metallic * sampledMetallicRoughness.Z * weight;
                    roughness += authored.Roughness * sampledMetallicRoughness.Y * weight;
                    totalWeight += weight;
                }
                if (totalWeight <= 0f)
                {
                    baseColor = Vector4.One;
                    normal = Vector3.UnitZ;
                    roughness = 1f;
                }
                else
                {
                    var inverseWeight = 1f / totalWeight;
                    baseColor *= inverseWeight;
                    normal *= inverseWeight;
                    metallic *= inverseWeight;
                    roughness *= inverseWeight;
                }
                normal = normal.LengthSquared() > 0.000001f
                    ? Vector3.Normalize(normal) : Vector3.UnitZ;
                var offset = (z * material.Width + x) * 4;
                WriteSrgb(basePixels, offset, baseColor);
                WriteNormal(normalPixels, offset, normal);
                metallicRoughnessPixels[offset] = 255;
                metallicRoughnessPixels[offset + 1] = ToByte(roughness);
                metallicRoughnessPixels[offset + 2] = ToByte(metallic);
                metallicRoughnessPixels[offset + 3] = 255;
            }
        }
        return new ComposedTerrainMaterial(
            new StandardMaterialAsset
            {
                BaseColor = Vector4.One,
                Metallic = 1f,
                Roughness = 1f,
                DoubleSided = false
            },
            new TextureResource(width, height, basePixels, TextureColorSpace.Srgb),
            new TextureResource(width, height, normalPixels, TextureColorSpace.Linear),
            new TextureResource(width, height, metallicRoughnessPixels, TextureColorSpace.Linear));
    }

    /// <summary>Samples a visible color texture and converts it to linear space.</summary>
    /// <param name="texture">Optional texture.</param>
    /// <param name="u">Repeating U coordinate.</param>
    /// <param name="v">Repeating V coordinate.</param>
    /// <returns>Linear RGBA sample.</returns>
    private static Vector4 SampleBaseColor(TextureResource? texture, float u, float v)
    {
        var sample = SampleLinear(texture, u, v, Vector4.One);
        if (texture?.ColorSpace != TextureColorSpace.Srgb)
            return sample;
        return new Vector4(ToLinear(sample.X), ToLinear(sample.Y), ToLinear(sample.Z), sample.W);
    }

    /// <summary>Samples and decodes a tangent-space normal.</summary>
    /// <param name="texture">Optional normal texture.</param>
    /// <param name="u">Repeating U coordinate.</param>
    /// <param name="v">Repeating V coordinate.</param>
    /// <returns>Decoded tangent-space vector.</returns>
    private static Vector3 SampleNormal(TextureResource? texture, float u, float v)
    {
        var sample = SampleLinear(texture, u, v, new Vector4(0.5f, 0.5f, 1f, 1f));
        var normal = new Vector3(sample.X * 2f - 1f, sample.Y * 2f - 1f,
            sample.Z * 2f - 1f);
        return normal.LengthSquared() > 0.000001f ? Vector3.Normalize(normal) : Vector3.UnitZ;
    }

    /// <summary>Samples one repeating RGBA8 texture with nearest filtering.</summary>
    /// <param name="texture">Optional texture.</param>
    /// <param name="u">Repeating U coordinate.</param>
    /// <param name="v">Repeating V coordinate.</param>
    /// <param name="fallback">Value returned when the texture is absent.</param>
    /// <returns>Normalized RGBA sample.</returns>
    private static Vector4 SampleLinear(
        TextureResource? texture,
        float u,
        float v,
        Vector4 fallback)
    {
        if (texture is null || texture.Width == 0 || texture.Height == 0 ||
            texture.Pixels.Length != checked((long)texture.Width * texture.Height * 4))
            return fallback;
        u -= MathF.Floor(u);
        v -= MathF.Floor(v);
        var x = Math.Min((uint)(u * texture.Width), texture.Width - 1);
        var y = Math.Min((uint)(v * texture.Height), texture.Height - 1);
        var offset = checked((int)((y * texture.Width + x) * 4));
        return new Vector4(texture.Pixels[offset], texture.Pixels[offset + 1],
            texture.Pixels[offset + 2], texture.Pixels[offset + 3]) / 255f;
    }

    /// <summary>Writes one linear color into an sRGB RGBA8 destination.</summary>
    /// <param name="pixels">Destination pixels.</param>
    /// <param name="offset">RGBA byte offset.</param>
    /// <param name="color">Linear color.</param>
    private static void WriteSrgb(byte[] pixels, int offset, Vector4 color)
    {
        pixels[offset] = ToByte(ToSrgb(color.X));
        pixels[offset + 1] = ToByte(ToSrgb(color.Y));
        pixels[offset + 2] = ToByte(ToSrgb(color.Z));
        pixels[offset + 3] = ToByte(color.W);
    }

    /// <summary>Writes one tangent-space vector into an RGBA8 destination.</summary>
    /// <param name="pixels">Destination pixels.</param>
    /// <param name="offset">RGBA byte offset.</param>
    /// <param name="normal">Normalized tangent-space vector.</param>
    private static void WriteNormal(byte[] pixels, int offset, Vector3 normal)
    {
        pixels[offset] = ToByte(normal.X * 0.5f + 0.5f);
        pixels[offset + 1] = ToByte(normal.Y * 0.5f + 0.5f);
        pixels[offset + 2] = ToByte(normal.Z * 0.5f + 0.5f);
        pixels[offset + 3] = 255;
    }

    /// <summary>Converts a linear scalar to sRGB.</summary>
    /// <param name="value">Linear scalar.</param>
    /// <returns>sRGB scalar.</returns>
    private static float ToSrgb(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value <= 0.0031308f ? value * 12.92f :
            1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;
    }

    /// <summary>Converts an sRGB scalar to linear space.</summary>
    /// <param name="value">sRGB scalar.</param>
    /// <returns>Linear scalar.</returns>
    private static float ToLinear(float value) => value <= 0.04045f
        ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

    /// <summary>Quantizes one normalized scalar.</summary>
    /// <param name="value">Normalized scalar.</param>
    /// <returns>Rounded byte.</returns>
    private static byte ToByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}

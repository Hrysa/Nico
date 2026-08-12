using System.Text;
using StbImageSharp;

namespace Engine.Assets;

/// <summary>Decodes a standalone PNG or JPEG into one runtime RGBA8 texture artifact.</summary>
public sealed class ImageTextureAssetImporter : IAssetImporter
{
    /// <inheritdoc/>
    public string Id => "image-texture";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public AssetImportResult Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.CancellationToken.ThrowIfCancellationRequested();
        ImageResult decoded;
        try
        {
            using var source = context.OpenSource();
            decoded = ImageResult.FromStream(source, ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception exception)
        {
            throw new InvalidDataException("Image texture could not be decoded.", exception);
        }

        const string relativePath = "texture.ntexture";
        using (var output = context.CreateArtifact(relativePath))
        using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("NTEX0001"u8);
            writer.Write(1u);
            writer.Write(checked((uint)decoded.Width));
            writer.Write(checked((uint)decoded.Height));
            writer.Write((byte)1);
            writer.Write(decoded.Data);
        }
        return new AssetImportResult(
            [new AssetArtifact("main", "nico/texture2d", relativePath)], [], []);
    }
}

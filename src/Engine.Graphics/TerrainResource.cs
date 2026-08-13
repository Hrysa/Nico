namespace Engine.Graphics;

/// <summary>Identifies a bounded quad region within a terrain sample grid.</summary>
/// <param name="StartX">First quad column.</param><param name="StartZ">First quad row.</param>
/// <param name="QuadCountX">Number of quad columns.</param><param name="QuadCountZ">Number of quad rows.</param>
public readonly record struct TerrainChunkRegion(
    int StartX, int StartZ, int QuadCountX, int QuadCountZ);

/// <summary>Stores normalized height samples for explicit terrain render and collision assets.</summary>
public sealed class TerrainResource
{
    private const string Magic = "NTERR001";
    private readonly float[] _heights;

    /// <summary>Creates a row-major height grid.</summary>
    /// <param name="width">Sample columns along local X.</param>
    /// <param name="depth">Sample rows along local Z.</param>
    /// <param name="heights">Normalized row-major samples.</param>
    public TerrainResource(int width, int depth, float[] heights)
    {
        if (width < 2)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (depth < 2)
            throw new ArgumentOutOfRangeException(nameof(depth));
        ArgumentNullException.ThrowIfNull(heights);
        if (heights.Length != checked(width * depth))
            throw new ArgumentException("Height count must equal width times depth.", nameof(heights));
        _heights = (float[])heights.Clone();
        for (var index = 0; index < _heights.Length; index++)
        {
            if (!float.IsFinite(_heights[index]))
                throw new ArgumentOutOfRangeException(nameof(heights));
        }
        Width = width;
        Depth = depth;
    }

    /// <summary>Gets the number of sample columns.</summary>
    public int Width { get; }

    /// <summary>Gets the number of sample rows.</summary>
    public int Depth { get; }

    /// <summary>Gets one normalized sample.</summary>
    /// <param name="x">Column index.</param><param name="z">Row index.</param>
    /// <returns>The stored height.</returns>
    public float GetHeight(int x, int z)
    {
        if ((uint)x >= (uint)Width)
            throw new ArgumentOutOfRangeException(nameof(x));
        if ((uint)z >= (uint)Depth)
            throw new ArgumentOutOfRangeException(nameof(z));
        return _heights[z * Width + x];
    }

    /// <summary>Copies the complete row-major sample payload for editor-side mutation.</summary>
    /// <returns>An independently owned height array.</returns>
    public float[] CopyHeights() => (float[])_heights.Clone();

    /// <summary>Replaces every sample in place while preserving grid dimensions and identity.</summary>
    /// <param name="heights">Complete row-major replacement payload.</param>
    public void UpdateHeights(ReadOnlySpan<float> heights)
    {
        if (heights.Length != _heights.Length)
            throw new ArgumentException(
                "Height count must equal width times depth.", nameof(heights));
        for (var index = 0; index < heights.Length; index++)
        {
            if (!float.IsFinite(heights[index]))
                throw new ArgumentOutOfRangeException(nameof(heights));
        }
        heights.CopyTo(_heights);
    }

    /// <summary>Samples the grid bilinearly using normalized coordinates.</summary>
    /// <param name="u">Horizontal coordinate from zero through one.</param>
    /// <param name="v">Depth coordinate from zero through one.</param>
    /// <returns>Interpolated normalized height.</returns>
    public float Sample(float u, float v)
    {
        u = Math.Clamp(u, 0f, 1f);
        v = Math.Clamp(v, 0f, 1f);
        var x = u * (Width - 1);
        var z = v * (Depth - 1);
        var x0 = (int)MathF.Floor(x);
        var z0 = (int)MathF.Floor(z);
        var x1 = Math.Min(x0 + 1, Width - 1);
        var z1 = Math.Min(z0 + 1, Depth - 1);
        var tx = x - x0;
        var tz = z - z0;
        var first = Interpolate(GetHeight(x0, z0), GetHeight(x1, z0), tx);
        var second = Interpolate(GetHeight(x0, z1), GetHeight(x1, z1), tx);
        return Interpolate(first, second, tz);
    }

    /// <summary>Writes one versioned Nico terrain artifact.</summary>
    /// <param name="stream">Writable artifact stream.</param>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(System.Text.Encoding.ASCII.GetBytes(Magic));
        writer.Write(1u);
        writer.Write(Width);
        writer.Write(Depth);
        for (var index = 0; index < _heights.Length; index++)
            writer.Write(_heights[index]);
    }

    /// <summary>Reads one versioned Nico terrain artifact.</summary>
    /// <param name="stream">Readable artifact stream.</param>
    /// <returns>Decoded immutable terrain grid.</returns>
    public static TerrainResource Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) != Magic)
            throw new InvalidDataException("Terrain artifact has an invalid signature.");
        if (reader.ReadUInt32() != 1u)
            throw new InvalidDataException("Terrain artifact version is unsupported.");
        var width = reader.ReadInt32();
        var depth = reader.ReadInt32();
        if (width < 2 || depth < 2)
            throw new InvalidDataException("Terrain artifact dimensions are invalid.");
        var count = checked(width * depth);
        if (!stream.CanSeek || stream.Length - stream.Position != (long)count * sizeof(float))
            throw new InvalidDataException("Terrain artifact payload length is invalid.");
        var heights = new float[count];
        for (var index = 0; index < heights.Length; index++)
            heights[index] = reader.ReadSingle();
        return new TerrainResource(width, depth, heights);
    }

    /// <summary>Describes all bounded native-shape chunks for this terrain.</summary>
    /// <param name="maximumQuadsPerAxis">Maximum quads along either chunk axis.</param>
    /// <returns>Deterministically ordered chunk regions.</returns>
    public TerrainChunkRegion[] GetChunkRegions(int maximumQuadsPerAxis = 64)
    {
        if (maximumQuadsPerAxis <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumQuadsPerAxis));
        var countX = (Width - 2 + maximumQuadsPerAxis) / maximumQuadsPerAxis;
        var countZ = (Depth - 2 + maximumQuadsPerAxis) / maximumQuadsPerAxis;
        var result = new TerrainChunkRegion[checked(countX * countZ)];
        var resultIndex = 0;
        for (var startZ = 0; startZ < Depth - 1; startZ += maximumQuadsPerAxis)
        {
            for (var startX = 0; startX < Width - 1; startX += maximumQuadsPerAxis)
            {
                result[resultIndex++] = new TerrainChunkRegion(startX, startZ,
                    Math.Min(maximumQuadsPerAxis, Width - 1 - startX),
                    Math.Min(maximumQuadsPerAxis, Depth - 1 - startZ));
            }
        }
        return result;
    }

    /// <summary>Maps an edited inclusive sample rectangle to chunks requiring native rebuild.</summary>
    /// <param name="minimumSampleX">First edited sample column.</param>
    /// <param name="minimumSampleZ">First edited sample row.</param>
    /// <param name="maximumSampleX">Last edited sample column.</param>
    /// <param name="maximumSampleZ">Last edited sample row.</param>
    /// <param name="maximumQuadsPerAxis">Chunk dimension used by physics.</param>
    /// <returns>Only chunk regions whose quads touch an edited sample.</returns>
    public TerrainChunkRegion[] GetDirtyChunkRegions(int minimumSampleX, int minimumSampleZ,
        int maximumSampleX, int maximumSampleZ, int maximumQuadsPerAxis = 64)
    {
        if (maximumQuadsPerAxis <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumQuadsPerAxis));
        if ((uint)minimumSampleX >= (uint)Width || (uint)maximumSampleX >= (uint)Width ||
            (uint)minimumSampleZ >= (uint)Depth || (uint)maximumSampleZ >= (uint)Depth ||
            minimumSampleX > maximumSampleX || minimumSampleZ > maximumSampleZ)
            throw new ArgumentOutOfRangeException(nameof(minimumSampleX));
        var minimumQuadX = Math.Max(0, minimumSampleX - 1);
        var minimumQuadZ = Math.Max(0, minimumSampleZ - 1);
        var maximumQuadX = Math.Min(Width - 2, maximumSampleX);
        var maximumQuadZ = Math.Min(Depth - 2, maximumSampleZ);
        var firstChunkX = minimumQuadX / maximumQuadsPerAxis;
        var firstChunkZ = minimumQuadZ / maximumQuadsPerAxis;
        var lastChunkX = maximumQuadX / maximumQuadsPerAxis;
        var lastChunkZ = maximumQuadZ / maximumQuadsPerAxis;
        var result = new TerrainChunkRegion[
            checked((lastChunkX - firstChunkX + 1) * (lastChunkZ - firstChunkZ + 1))];
        var resultIndex = 0;
        for (var chunkZ = firstChunkZ; chunkZ <= lastChunkZ; chunkZ++)
        {
            for (var chunkX = firstChunkX; chunkX <= lastChunkX; chunkX++)
            {
                var startX = chunkX * maximumQuadsPerAxis;
                var startZ = chunkZ * maximumQuadsPerAxis;
                result[resultIndex++] = new TerrainChunkRegion(startX, startZ,
                    Math.Min(maximumQuadsPerAxis, Width - 1 - startX),
                    Math.Min(maximumQuadsPerAxis, Depth - 1 - startZ));
            }
        }
        return result;
    }

    /// <summary>Interpolates between two scalar samples.</summary>
    /// <param name="a">Value at zero.</param><param name="b">Value at one.</param>
    /// <param name="amount">Interpolation amount.</param><returns>Interpolated value.</returns>
    private static float Interpolate(float a, float b, float amount) =>
        a + (b - a) * amount;
}

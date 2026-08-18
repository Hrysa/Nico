namespace Engine.Graphics;

/// <summary>Identifies a bounded quad region within a terrain sample grid.</summary>
/// <param name="StartX">First quad column.</param><param name="StartZ">First quad row.</param>
/// <param name="QuadCountX">Number of quad columns.</param><param name="QuadCountZ">Number of quad rows.</param>
public readonly record struct TerrainChunkRegion(
    int StartX, int StartZ, int QuadCountX, int QuadCountZ);

/// <summary>Identifies one active terrain sample on the half-cell detail lattice.</summary>
/// <param name="FineX">Horizontal coordinate where base samples occupy even values.</param>
/// <param name="FineZ">Depth coordinate where base samples occupy even values.</param>
public readonly record struct TerrainSamplePoint(int FineX, int FineZ);

/// <summary>Stores finite height samples for explicit terrain render and collision assets.</summary>
public sealed class TerrainResource
{
    private const string Magic = "NTERR001";
    private readonly float[] _heights;
    private readonly bool[] _refinedQuads;
    private readonly Dictionary<int, float> _detailHeights;
    private TerrainSamplePoint[]? _activeSamples;

    /// <summary>Creates a row-major height grid.</summary>
    /// <param name="width">Sample columns along local X.</param>
    /// <param name="depth">Sample rows along local Z.</param>
    /// <param name="heights">Finite row-major height samples.</param>
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
        _refinedQuads = new bool[checked((width - 1) * (depth - 1))];
        _detailHeights = [];
    }

    /// <summary>Creates a terrain clone or decoded adaptive terrain.</summary>
    /// <param name="width">Base sample columns.</param>
    /// <param name="depth">Base sample rows.</param>
    /// <param name="heights">Owned base height samples.</param>
    /// <param name="refinedQuads">Owned local refinement flags.</param>
    /// <param name="detailHeights">Owned half-cell detail samples.</param>
    private TerrainResource(
        int width,
        int depth,
        float[] heights,
        bool[] refinedQuads,
        Dictionary<int, float> detailHeights)
    {
        Width = width;
        Depth = depth;
        _heights = heights;
        _refinedQuads = refinedQuads;
        _detailHeights = detailHeights;
    }

    /// <summary>Gets the number of sample columns.</summary>
    public int Width { get; }

    /// <summary>Gets the number of sample rows.</summary>
    public int Depth { get; }

    /// <summary>Gets the number of currently active locally refined base quads.</summary>
    public int RefinedQuadCount
    {
        get
        {
            var count = 0;
            for (var index = 0; index < _refinedQuads.Length; index++)
            {
                if (_refinedQuads[index])
                    count++;
            }
            return count;
        }
    }

    /// <summary>Gets a runtime revision incremented whenever local topology changes.</summary>
    public int TopologyVersion { get; private set; }

    /// <summary>Gets the half-cell lattice width used by local detail samples.</summary>
    public int FineWidth => checked((Width - 1) * 2 + 1);

    /// <summary>Gets the half-cell lattice depth used by local detail samples.</summary>
    public int FineDepth => checked((Depth - 1) * 2 + 1);

    /// <summary>Gets one finite height sample.</summary>
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

    /// <summary>Creates an independent copy including adaptive topology and detail heights.</summary>
    /// <returns>A mutable terrain copy.</returns>
    public TerrainResource Clone() => new(
        Width,
        Depth,
        (float[])_heights.Clone(),
        (bool[])_refinedQuads.Clone(),
        new Dictionary<int, float>(_detailHeights))
    {
        TopologyVersion = this.TopologyVersion
    };

    /// <summary>Gets whether one base quad currently uses half-cell samples.</summary>
    /// <param name="x">Base quad column.</param>
    /// <param name="z">Base quad row.</param>
    /// <returns>True when the quad is locally refined.</returns>
    public bool IsQuadRefined(int x, int z)
    {
        ValidateQuad(x, z);
        return _refinedQuads[z * (Width - 1) + x];
    }

    /// <summary>Changes one base quad between its original and half-cell sample density.</summary>
    /// <param name="x">Base quad column.</param>
    /// <param name="z">Base quad row.</param>
    /// <param name="refined">Whether half-cell samples should be active.</param>
    /// <returns>True when local topology changed.</returns>
    public bool SetQuadRefined(int x, int z, bool refined)
    {
        ValidateQuad(x, z);
        var index = z * (Width - 1) + x;
        if (_refinedQuads[index] == refined)
            return false;
        if (refined)
        {
            for (var fineZ = z * 2; fineZ <= z * 2 + 2; fineZ++)
            {
                for (var fineX = x * 2; fineX <= x * 2 + 2; fineX++)
                    EnsureDetailHeight(fineX, fineZ);
            }
        }
        _refinedQuads[index] = refined;
        _activeSamples = null;
        TopologyVersion++;
        return true;
    }

    /// <summary>Gets all samples participating in the current crack-free topology.</summary>
    /// <returns>Deterministically ordered active sample coordinates.</returns>
    public ReadOnlySpan<TerrainSamplePoint> GetActiveSamples()
    {
        EnsureActiveSamples();
        return _activeSamples;
    }

    /// <summary>Gets one active or retained detail height from the half-cell lattice.</summary>
    /// <param name="sample">Half-cell sample coordinate.</param>
    /// <returns>Finite height value.</returns>
    public float GetSampleHeight(TerrainSamplePoint sample) =>
        GetFineHeight(sample.FineX, sample.FineZ);

    /// <summary>Updates one active terrain sample height.</summary>
    /// <param name="sample">Half-cell sample coordinate.</param>
    /// <param name="height">Finite replacement height.</param>
    public void SetSampleHeight(TerrainSamplePoint sample, float height)
    {
        if (!float.IsFinite(height))
            throw new ArgumentOutOfRangeException(nameof(height));
        ValidateFineSample(sample.FineX, sample.FineZ);
        if ((sample.FineX & 1) == 0 && (sample.FineZ & 1) == 0)
        {
            _heights[(sample.FineZ / 2) * Width + sample.FineX / 2] = height;
            return;
        }
        _detailHeights[GetFineKey(sample.FineX, sample.FineZ)] = height;
    }

    /// <summary>Samples the grid bilinearly using normalized coordinates.</summary>
    /// <param name="u">Horizontal coordinate from zero through one.</param>
    /// <param name="v">Depth coordinate from zero through one.</param>
    /// <returns>Interpolated height sample.</returns>
    public float Sample(float u, float v)
    {
        u = Math.Clamp(u, 0f, 1f);
        v = Math.Clamp(v, 0f, 1f);
        var x = u * (Width - 1);
        var z = v * (Depth - 1);
        var x0 = (int)MathF.Floor(x);
        var z0 = (int)MathF.Floor(z);
        if (x0 < Width - 1 && z0 < Depth - 1 && IsQuadRefined(x0, z0))
        {
            var localX = (x - x0) * 2f;
            var localZ = (z - z0) * 2f;
            var fineX0 = x0 * 2 + Math.Min(1, (int)MathF.Floor(localX));
            var fineZ0 = z0 * 2 + Math.Min(1, (int)MathF.Floor(localZ));
            var fineX1 = fineX0 + 1;
            var fineZ1 = fineZ0 + 1;
            var fineTx = localX - MathF.Floor(localX);
            var fineTz = localZ - MathF.Floor(localZ);
            var fineFirst = Interpolate(
                GetFineHeight(fineX0, fineZ0), GetFineHeight(fineX1, fineZ0), fineTx);
            var fineSecond = Interpolate(
                GetFineHeight(fineX0, fineZ1), GetFineHeight(fineX1, fineZ1), fineTx);
            return Interpolate(fineFirst, fineSecond, fineTz);
        }
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
        writer.Write(2u);
        writer.Write(Width);
        writer.Write(Depth);
        for (var index = 0; index < _heights.Length; index++)
            writer.Write(_heights[index]);
        writer.Write(_refinedQuads.Length);
        for (var index = 0; index < _refinedQuads.Length; index++)
            writer.Write(_refinedQuads[index]);
        writer.Write(_detailHeights.Count);
        var detailKeys = _detailHeights.Keys.ToArray();
        Array.Sort(detailKeys);
        for (var index = 0; index < detailKeys.Length; index++)
        {
            var key = detailKeys[index];
            writer.Write(key);
            writer.Write(_detailHeights[key]);
        }
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
        if (reader.ReadUInt32() != 2u)
            throw new InvalidDataException("Terrain artifact version is unsupported.");
        var width = reader.ReadInt32();
        var depth = reader.ReadInt32();
        if (width < 2 || depth < 2)
            throw new InvalidDataException("Terrain artifact dimensions are invalid.");
        var count = checked(width * depth);
        var heights = new float[count];
        for (var index = 0; index < heights.Length; index++)
        {
            heights[index] = reader.ReadSingle();
            if (!float.IsFinite(heights[index]))
                throw new InvalidDataException("Terrain height samples are invalid.");
        }
        var quadCount = checked((width - 1) * (depth - 1));
        if (reader.ReadInt32() != quadCount)
            throw new InvalidDataException("Terrain refinement payload has invalid dimensions.");
        var refinedQuads = new bool[quadCount];
        for (var index = 0; index < refinedQuads.Length; index++)
            refinedQuads[index] = reader.ReadBoolean();
        var detailCount = reader.ReadInt32();
        if (detailCount < 0 || detailCount > checked((width * 2 - 1) * (depth * 2 - 1)))
            throw new InvalidDataException("Terrain detail sample count is invalid.");
        var detailHeights = new Dictionary<int, float>(detailCount);
        for (var index = 0; index < detailCount; index++)
        {
            var key = reader.ReadInt32();
            var height = reader.ReadSingle();
            if (!float.IsFinite(height) || !detailHeights.TryAdd(key, height))
                throw new InvalidDataException("Terrain detail samples are invalid.");
        }
        if (!stream.CanSeek || stream.Position != stream.Length)
            throw new InvalidDataException("Terrain artifact payload length is invalid.");
        var terrain = new TerrainResource(
            width, depth, heights, refinedQuads, detailHeights);
        terrain.ValidateDetailSamples();
        terrain.EnsureActiveSamples();
        return terrain;
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

    /// <summary>Gets a half-cell lattice height, interpolating inactive detail samples.</summary>
    /// <param name="fineX">Half-cell horizontal coordinate.</param>
    /// <param name="fineZ">Half-cell depth coordinate.</param>
    /// <returns>Finite height value.</returns>
    private float GetFineHeight(int fineX, int fineZ)
    {
        ValidateFineSample(fineX, fineZ);
        if ((fineX & 1) == 0 && (fineZ & 1) == 0)
            return GetHeight(fineX / 2, fineZ / 2);
        var key = GetFineKey(fineX, fineZ);
        if (_detailHeights.TryGetValue(key, out var height))
            return height;
        var u = fineX / (float)(FineWidth - 1);
        var v = fineZ / (float)(FineDepth - 1);
        return SampleBase(u, v);
    }

    /// <summary>Creates retained height storage for one non-base lattice sample.</summary>
    /// <param name="fineX">Half-cell horizontal coordinate.</param>
    /// <param name="fineZ">Half-cell depth coordinate.</param>
    private void EnsureDetailHeight(int fineX, int fineZ)
    {
        if ((fineX & 1) == 0 && (fineZ & 1) == 0)
            return;
        var key = GetFineKey(fineX, fineZ);
        if (!_detailHeights.ContainsKey(key))
            _detailHeights.Add(key, SampleBase(
                fineX / (float)(FineWidth - 1),
                fineZ / (float)(FineDepth - 1)));
    }

    /// <summary>Builds the deterministic active sample set after topology changes.</summary>
    private void EnsureActiveSamples()
    {
        if (_activeSamples is not null)
            return;
        var active = new bool[checked(FineWidth * FineDepth)];
        for (var z = 0; z < Depth; z++)
        {
            for (var x = 0; x < Width; x++)
                active[(z * 2) * FineWidth + x * 2] = true;
        }
        for (var z = 0; z < Depth - 1; z++)
        {
            for (var x = 0; x < Width - 1; x++)
            {
                if (!IsQuadRefinedUnchecked(x, z))
                    continue;
                for (var localZ = 0; localZ <= 2; localZ++)
                {
                    for (var localX = 0; localX <= 2; localX++)
                    {
                        var fineX = x * 2 + localX;
                        var fineZ = z * 2 + localZ;
                        active[fineZ * FineWidth + fineX] = true;
                        EnsureDetailHeight(fineX, fineZ);
                    }
                }
            }
        }
        for (var z = 0; z < Depth - 1; z++)
        {
            for (var x = 0; x < Width - 1; x++)
            {
                if (IsQuadRefinedUnchecked(x, z) || !HasRefinedNeighbor(x, z))
                    continue;
                var fineX = x * 2 + 1;
                var fineZ = z * 2 + 1;
                active[fineZ * FineWidth + fineX] = true;
                EnsureDetailHeight(fineX, fineZ);
            }
        }
        var count = 0;
        for (var index = 0; index < active.Length; index++)
        {
            if (active[index])
                count++;
        }
        var samples = new TerrainSamplePoint[count];
        var sampleIndex = 0;
        for (var fineZ = 0; fineZ < FineDepth; fineZ++)
        {
            for (var fineX = 0; fineX < FineWidth; fineX++)
            {
                if (active[fineZ * FineWidth + fineX])
                    samples[sampleIndex++] = new TerrainSamplePoint(fineX, fineZ);
            }
        }
        _activeSamples = samples;
    }

    /// <summary>Gets whether one coarse quad touches a locally refined neighbor.</summary>
    /// <param name="x">Base quad column.</param>
    /// <param name="z">Base quad row.</param>
    /// <returns>True when an edge neighbor is refined.</returns>
    private bool HasRefinedNeighbor(int x, int z) =>
        x > 0 && IsQuadRefinedUnchecked(x - 1, z) ||
        x + 1 < Width - 1 && IsQuadRefinedUnchecked(x + 1, z) ||
        z > 0 && IsQuadRefinedUnchecked(x, z - 1) ||
        z + 1 < Depth - 1 && IsQuadRefinedUnchecked(x, z + 1);

    /// <summary>Gets a refinement flag after callers establish valid coordinates.</summary>
    /// <param name="x">Base quad column.</param>
    /// <param name="z">Base quad row.</param>
    /// <returns>Stored refinement state.</returns>
    private bool IsQuadRefinedUnchecked(int x, int z) =>
        _refinedQuads[z * (Width - 1) + x];

    /// <summary>Samples only the original base grid bilinearly.</summary>
    /// <param name="u">Normalized horizontal coordinate.</param>
    /// <param name="v">Normalized depth coordinate.</param>
    /// <returns>Interpolated base height.</returns>
    private float SampleBase(float u, float v)
    {
        var x = Math.Clamp(u, 0f, 1f) * (Width - 1);
        var z = Math.Clamp(v, 0f, 1f) * (Depth - 1);
        var x0 = (int)MathF.Floor(x);
        var z0 = (int)MathF.Floor(z);
        var x1 = Math.Min(x0 + 1, Width - 1);
        var z1 = Math.Min(z0 + 1, Depth - 1);
        var first = Interpolate(GetHeight(x0, z0), GetHeight(x1, z0), x - x0);
        var second = Interpolate(GetHeight(x0, z1), GetHeight(x1, z1), x - x0);
        return Interpolate(first, second, z - z0);
    }

    /// <summary>Validates decoded retained detail sample coordinates.</summary>
    private void ValidateDetailSamples()
    {
        foreach (var pair in _detailHeights)
        {
            var fineX = pair.Key % FineWidth;
            var fineZ = pair.Key / FineWidth;
            if (pair.Key < 0 || fineX >= FineWidth || fineZ >= FineDepth ||
                ((fineX & 1) == 0 && (fineZ & 1) == 0))
                throw new InvalidDataException("Terrain detail sample coordinates are invalid.");
        }
    }

    /// <summary>Gets one row-major half-cell lattice key.</summary>
    /// <param name="fineX">Horizontal coordinate.</param>
    /// <param name="fineZ">Depth coordinate.</param>
    /// <returns>Stable row-major key.</returns>
    private int GetFineKey(int fineX, int fineZ) => checked(fineZ * FineWidth + fineX);

    /// <summary>Validates one base quad coordinate.</summary>
    /// <param name="x">Base quad column.</param>
    /// <param name="z">Base quad row.</param>
    private void ValidateQuad(int x, int z)
    {
        if ((uint)x >= (uint)(Width - 1))
            throw new ArgumentOutOfRangeException(nameof(x));
        if ((uint)z >= (uint)(Depth - 1))
            throw new ArgumentOutOfRangeException(nameof(z));
    }

    /// <summary>Validates one half-cell sample coordinate.</summary>
    /// <param name="fineX">Horizontal coordinate.</param>
    /// <param name="fineZ">Depth coordinate.</param>
    private void ValidateFineSample(int fineX, int fineZ)
    {
        if ((uint)fineX >= (uint)FineWidth)
            throw new ArgumentOutOfRangeException(nameof(fineX));
        if ((uint)fineZ >= (uint)FineDepth)
            throw new ArgumentOutOfRangeException(nameof(fineZ));
    }

    /// <summary>Interpolates between two scalar samples.</summary>
    /// <param name="a">Value at zero.</param><param name="b">Value at one.</param>
    /// <param name="amount">Interpolation amount.</param><returns>Interpolated value.</returns>
    private static float Interpolate(float a, float b, float amount) =>
        a + (b - a) * amount;
}

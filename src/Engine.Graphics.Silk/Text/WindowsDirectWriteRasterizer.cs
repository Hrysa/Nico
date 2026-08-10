using Vortice.Win32.Graphics.DirectWrite;
using Vortice.Win32.Numerics;
using static Vortice.Win32.Graphics.DirectWrite.Apis;

namespace Engine.Graphics;

/// <summary>Rasterizes installed Windows font faces through native DirectWrite hinting.</summary>
internal unsafe sealed class WindowsDirectWriteRasterizer : IDisposable
{
    private IDWriteFactory2* _factory;
    private bool _disposed;

    /// <summary>Creates the native backend when DirectWrite is available on Windows.</summary>
    /// <returns>An initialized backend, or null when native initialization fails.</returns>
    internal static WindowsDirectWriteRasterizer? TryCreate()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        IDWriteFactory2* factory = null;
        var iid = IDWriteFactory2.IID_IDWriteFactory2;
        var result = DWriteCreateFactory(FactoryType.Shared, &iid, (void**)&factory);
        return result.Success && factory is not null
            ? new WindowsDirectWriteRasterizer(factory)
            : null;
    }

    /// <summary>Stores an initialized DirectWrite factory.</summary>
    /// <param name="factory">Owned factory interface.</param>
    private WindowsDirectWriteRasterizer(IDWriteFactory2* factory)
    {
        _factory = factory;
    }

    /// <summary>Opens an installed font file as a native DirectWrite face.</summary>
    /// <param name="path">Absolute font-file path.</param>
    /// <param name="faceIndex">Face index inside a collection.</param>
    /// <returns>An owned face, or null when DirectWrite rejects the source.</returns>
    internal FontFace? TryCreateFontFace(string path, int faceIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IDWriteFontFile* file = null;
        fixed (char* pathPointer = path)
        {
            if (_factory->CreateFontFileReference(pathPointer, null, &file).Failure || file is null)
                return null;
        }
        try
        {
            Vortice.Win32.Bool32 supported = default;
            FontFileType fileType = default;
            FontFaceType faceType = default;
            uint faceCount = 0;
            if (file->Analyze(&supported, &fileType, &faceType, &faceCount).Failure ||
                !supported || faceIndex < 0 || (uint)faceIndex >= faceCount)
                return null;
            IDWriteFontFace* face = null;
            if (_factory->CreateFontFace(faceType, 1, &file, (uint)faceIndex,
                    FontSimulations.None, &face).Failure || face is null)
                return null;
            return new FontFace(face);
        }
        finally
        {
            file->Release();
        }
    }

    /// <summary>Rasterizes one glyph with DirectWrite's symmetric natural grid fitting.</summary>
    /// <param name="face">Native font face.</param>
    /// <param name="glyphIndex">Shaped glyph identifier.</param>
    /// <param name="pixelHeight">Requested physical pixel height.</param>
    /// <param name="glyph">Rasterized coverage and bitmap offset.</param>
    /// <returns>True when DirectWrite produced a bitmap.</returns>
    internal bool TryRasterize(FontFace face, int glyphIndex, int pixelHeight, out NativeGlyph glyph)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        glyph = default;
        if ((uint)glyphIndex > ushort.MaxValue || pixelHeight <= 0)
            return false;
        FontMetrics metrics = default;
        face.Pointer->GetMetrics(&metrics);
        var bodyUnits = metrics.ascent + metrics.descent;
        if (bodyUnits == 0)
            return false;
        var emSize = pixelHeight * metrics.designUnitsPerEm / (float)bodyUnits;
        var index = (ushort)glyphIndex;
        var run = new GlyphRun
        {
            fontFace = face.Pointer,
            fontEmSize = emSize,
            glyphCount = 1,
            glyphIndices = &index,
            glyphAdvances = null,
            glyphOffsets = null,
            isSideways = false,
            bidiLevel = 0
        };
        IDWriteGlyphRunAnalysis* analysis = null;
        if (_factory->CreateGlyphRunAnalysis(&run, null,
                RenderingMode.NaturalSymmetric, MeasuringMode.Natural,
                GridFitMode.Enabled, TextAntialiasMode.Cleartype,
                0f, 0f, &analysis).Failure || analysis is null)
            return false;
        try
        {
            Rect bounds = default;
            if (analysis->GetAlphaTextureBounds(
                    TextureType.DWRITE_TEXTURE_CLEARTYPE_3x1, &bounds).Failure)
                return false;
            var width = bounds.Width;
            var height = bounds.Height;
            if (width <= 0 || height <= 0)
            {
                glyph = new NativeGlyph(0, 0, bounds.Left, bounds.Top,
                    new GlyphCoverage(GlyphCoverageFormat.RgbSubpixel, []));
                return true;
            }
            var coverage = new byte[checked(width * height * 3)];
            fixed (byte* coveragePointer = coverage)
            {
                if (analysis->CreateAlphaTexture(TextureType.DWRITE_TEXTURE_CLEARTYPE_3x1, &bounds,
                        coveragePointer, (uint)coverage.Length).Failure)
                    return false;
            }
            glyph = new NativeGlyph(width, height, bounds.Left, bounds.Top,
                new GlyphCoverage(GlyphCoverageFormat.RgbSubpixel, coverage));
            return true;
        }
        finally
        {
            analysis->Release();
        }
    }

    /// <summary>Releases the DirectWrite factory after all child faces are gone.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_factory is not null)
            _factory->Release();
        _factory = null;
    }

    /// <summary>Owns one native DirectWrite font face.</summary>
    internal sealed class FontFace : IDisposable
    {
        private IDWriteFontFace* _pointer;

        /// <summary>Gets the native interface pointer.</summary>
        internal IDWriteFontFace* Pointer => _pointer;

        /// <summary>Stores an owned native font face.</summary>
        /// <param name="pointer">Owned face interface.</param>
        internal FontFace(IDWriteFontFace* pointer)
        {
            _pointer = pointer;
        }

        /// <summary>Releases the native font face.</summary>
        public void Dispose()
        {
            if (_pointer is not null)
                _pointer->Release();
            _pointer = null;
        }
    }

    /// <summary>Stores native glyph coverage and its baseline-relative bitmap rectangle.</summary>
    /// <param name="Width">Bitmap width.</param>
    /// <param name="Height">Bitmap height.</param>
    /// <param name="XOffset">Baseline-relative left coordinate.</param>
    /// <param name="YOffset">Baseline-relative top coordinate.</param>
    /// <param name="Coverage">Format-tagged native coverage.</param>
    internal readonly record struct NativeGlyph(
        int Width, int Height, int XOffset, int YOffset, GlyphCoverage Coverage);
}

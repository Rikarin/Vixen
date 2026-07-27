// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using HarfBuzzSharp;

namespace Vixen.Ui.Text;

/// <summary>A font's horizontal metrics, in design units.</summary>
/// <param name="Ascender">How far above the baseline the font reaches. Positive.</param>
/// <param name="Descender">How far below it. Negative, as the font tables give it.</param>
/// <param name="LineGap">The leading the designer asks for between two lines.</param>
public readonly record struct FontMetrics(int Ascender, int Descender, int LineGap) {
    /// <summary>The distance from one baseline to the next.</summary>
    public int LineHeight => Ascender - Descender + LineGap;
}

/// <summary>One loaded font, ready to shape with.</summary>
/// <remarks>
///     <para>
///         <b>The font is held at design-unit scale and never at a pixel size.</b> That is a
///         decision worth stating: HarfBuzz's OpenType path has no hinting and no size-specific
///         behaviour, so shaping the same string at 12pt and at 48pt produces the same glyphs at
///         proportional positions. Keeping the scale at units-per-em makes that identity explicit,
///         and it makes the shaping cache size-independent — one entry serves every size the same
///         string is drawn at, which for a UI redrawing at several DPI scales is most of them.
///     </para>
///     <para>
///         The caller multiplies by <c>size / UnitsPerEm</c> at layout time. Anything that later
///         needs size-dependent shaping — optical sizing through a variable axis, say — sets the
///         axis rather than the scale, and would want a separate face.
///     </para>
///     <para>
///         Not thread-safe. HarfBuzz's <c>hb_font_t</c> is mutable and shaping writes to it, so a
///         face is used from one thread at a time; parallel layout gets a face per worker.
///     </para>
/// </remarks>
public sealed class FontFace : IDisposable {
    readonly Blob blob;
    readonly Face face;
    readonly Font font;
    bool disposed;

    FontFace(Blob blob, Face face, Font font, string name) {
        this.blob = blob;
        this.face = face;
        this.font = font;

        Name = name;
        UnitsPerEm = face.UnitsPerEm;
        Metrics = font.TryGetHorizontalFontExtents(out var extents)
            ? new FontMetrics(extents.Ascender, extents.Descender, extents.LineGap)
            : new FontMetrics(UnitsPerEm, 0, 0);
    }

    /// <summary>What to call this face in a diagnostic.</summary>
    public string Name { get; }

    /// <summary>The font's design grid — the unit every position out of shaping is in.</summary>
    public int UnitsPerEm { get; }

    /// <summary>How many glyphs the font has.</summary>
    public int GlyphCount => face.GlyphCount;

    /// <summary>The horizontal metrics.</summary>
    public FontMetrics Metrics { get; }

    internal Font Shaper => font;

    /// <summary>Loads a font from the bytes of an <c>sfnt</c> file.</summary>
    /// <param name="data">The file's contents. A <c>.ttf</c>, <c>.otf</c>, <c>.ttc</c> or <c>.otc</c>.</param>
    /// <param name="index">Which face, for a collection. Zero for a single-face file.</param>
    /// <param name="name">What to call it in a diagnostic.</param>
    /// <returns>The loaded face.</returns>
    public static FontFace Load(byte[] data, int index = 0, string? name = null) {
        ArgumentNullException.ThrowIfNull(data);

        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        var blob = new Blob(handle.AddrOfPinnedObject(), data.Length, MemoryMode.Duplicate, handle.Free);

        var face = new Face(blob, index);
        var font = new Font(face);

        // Design units, for the reason in the type's remarks.
        font.SetScale(face.UnitsPerEm, face.UnitsPerEm);

        // ⚠ Without this the font has no glyph functions at all and every advance comes back zero —
        // which reads as a shaping bug rather than as a missing line of setup.
        font.SetFunctionsOpenType();

        return new FontFace(blob, face, font, name ?? "font");
    }

    /// <summary>The glyph a code point maps to through the font's <c>cmap</c>.</summary>
    /// <param name="codePoint">The code point.</param>
    /// <returns>The glyph id, or zero — <c>.notdef</c> — if the font has no glyph for it.</returns>
    public ushort GlyphFor(int codePoint) => font.TryGetNominalGlyph(codePoint, out var glyph) ? (ushort)glyph : (ushort)0;

    /// <summary>Whether the font can draw a code point at all.</summary>
    /// <param name="codePoint">The code point.</param>
    /// <returns>Whether it maps to anything but <c>.notdef</c>.</returns>
    public bool Supports(int codePoint) => GlyphFor(codePoint) != 0;

    /// <summary>The name of a glyph, for a diagnostic or a golden test.</summary>
    /// <param name="glyphId">The glyph.</param>
    /// <returns>Its <c>post</c>-table name, or <c>gidN</c> if the font does not name it.</returns>
    public string GlyphName(ushort glyphId) => font.GlyphToString(glyphId);

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        font.Dispose();
        face.Dispose();
        blob.Dispose();
    }
}

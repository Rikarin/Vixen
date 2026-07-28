// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Runtime.InteropServices;
using HarfBuzzSharp;
using Vixen.Ui.Text.Outlines;

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
    GlyphOutlineSource? outlines;
    ImmutableArray<FontAxis>? axes;
    ImmutableArray<AxisSegmentMap>? maps;
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

        // The pin is released here rather than through a callback: `Duplicate` means HarfBuzz takes
        // its own copy, so the array only has to stay put for the length of the constructor. Handing
        // the release to HarfBuzz instead would make the lifetime of a pinned managed array depend
        // on when a native library decides to call back, which is a question not worth having.
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        Blob blob;

        try {
            blob = new Blob(handle.AddrOfPinnedObject(), data.Length, MemoryMode.Duplicate);
        } finally {
            handle.Free();
        }

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

    /// <summary>The box a glyph occupies, as the font itself reports it.</summary>
    /// <param name="glyphId">The glyph.</param>
    /// <param name="bounds">Its extent in design units, y up.</param>
    /// <returns>Whether the font had an answer.</returns>
    /// <remarks>
    ///     ⚠ <b>Not the same as measuring <see cref="GetOutline" />.</b> For a <c>glyf</c> font this
    ///     is the box the font *stores*, which a font compiler writes from the control points and
    ///     which is occasionally simply wrong — three glyphs in the Microsoft core fonts claim an
    ///     <c>xMax</c> their own components do not reach. It is the cheap answer and the right one
    ///     for laying out a line; a distance field measures the contours.
    /// </remarks>
    public bool TryGetGlyphExtents(ushort glyphId, out GlyphBounds bounds) {
        if (!font.TryGetGlyphExtents(glyphId, out var extents)) {
            bounds = default;
            return false;
        }

        // HarfBuzz gives a bearing and a signed size, with height running down from the top.
        bounds = new GlyphBounds(
            extents.XBearing,
            extents.YBearing + extents.Height,
            extents.XBearing + extents.Width,
            extents.YBearing
        );

        return true;
    }

    /// <summary>Whether this face stores outlines in a format Vixen can read.</summary>
    /// <remarks>
    ///     False for a bitmap-only or colour-only font, which shapes and measures perfectly well and
    ///     has no contours to build a distance field from.
    /// </remarks>
    public bool HasOutlines => Outlines.HasOutlines;

    /// <summary>A glyph's contours, in design units.</summary>
    /// <param name="glyphId">The glyph.</param>
    /// <param name="variation">Where along the font's axes, from <see cref="Variation" />, or null.</param>
    /// <returns>Its outline, or an empty one for a space or a glyph the font does not draw.</returns>
    /// <remarks>
    ///     <para>
    ///         Design units, like everything else here, so one outline serves every size — which is
    ///         the same property that makes the shaping cache size-independent.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Positioned to agree with <c>TryGetGlyphExtents</c>, not to match the raw table.</b>
    ///         See <see cref="Outlines.GlyphOutlineSource" />: a <c>glyf</c> glyph whose stored
    ///         <c>xMin</c> disagrees with its left side bearing is rasterised shifted, and every
    ///         other number in this assembly comes from HarfBuzz.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A position built against another font is not detected and will read as garbage.</b>
    ///         Coordinates are per axis by position, so handing a two-axis font a three-axis position
    ///         reads the second font's second axis against the first's — <see cref="Variation" /> is
    ///         what produces one that belongs to this face.
    ///     </para>
    /// </remarks>
    public GlyphOutline GetOutline(ushort glyphId, FontVariation? variation = null) =>
        Outlines.Read(glyphId, variation);

    /// <summary>The axes this font can be instanced along, in its own order.</summary>
    /// <remarks>Empty for a font that is not variable, which is most of them.</remarks>
    public ImmutableArray<FontAxis> Axes => axes ??= VariationTables.ReadAxes(Table("fvar"));

    /// <summary>Whether the font has axes at all.</summary>
    public bool IsVariable => !Axes.IsEmpty;

    /// <summary>Which instance the shaper is currently set to.</summary>
    /// <remarks>
    ///     ⚠ <b>This is state on the face, and it is the shaper's, not the outline reader's.</b>
    ///     <see cref="GetOutline" /> takes its instance as an argument and reads nothing from here;
    ///     everything that goes through HarfBuzz — advances, extents, the glyph a code point maps to
    ///     — answers for whatever was set last. <see cref="TextShaper" /> sets it on every call for
    ///     that reason.
    /// </remarks>
    public FontVariation Instance { get; private set; } = FontVariation.None;

    /// <summary>Points the shaper at one instance of a variable font.</summary>
    /// <param name="variation">Where along the axes, or null for the font's own defaults.</param>
    /// <remarks>
    ///     Normalised coordinates go straight across, because that is what HarfBuzz stores: the
    ///     design-coordinate entry point would re-derive the normalisation and the <c>avar</c> warp
    ///     inside the library, which is a second implementation of arithmetic this assembly has
    ///     already done and would let the outline and the advance disagree about the same instance.
    /// </remarks>
    public void SetInstance(FontVariation? variation) {
        var wanted = variation ?? FontVariation.None;

        // A native call per paragraph is not free, and most documents never move an axis at all.
        if (wanted.Equals(Instance)) {
            return;
        }

        Instance = wanted;

        Span<int> coordinates = wanted.Coordinates.Length <= 16
            ? stackalloc int[wanted.Coordinates.Length]
            : new int[wanted.Coordinates.Length];

        for (var i = 0; i < wanted.Coordinates.Length; i++) {
            // 2.14 fixed point, the same encoding the tables use. Rounded rather than truncated so
            // that an axis at its own maximum arrives as 1.0 and not as one step below it.
            coordinates[i] = (int)MathF.Round(wanted.Coordinates[i] * 16384f);
        }

        font.SetVariationCoordsNormalized(coordinates);
    }

    /// <summary>Turns a set of user-space axis values into a position this face can be read at.</summary>
    /// <param name="values">By tag — <c>wght</c>, <c>wdth</c>. Axes it does not name keep their default.</param>
    /// <returns>The position, or <see cref="FontVariation.None" /> for a font with no axes.</returns>
    /// <remarks>
    ///     Normalisation and the <c>avar</c> warp both happen here rather than per glyph, because the
    ///     result is a cache key and a key that is recomputed is a key that can disagree with itself.
    /// </remarks>
    public FontVariation Variation(IReadOnlyDictionary<string, float> values) =>
        FontVariation.Create(Axes, SegmentMaps, values);

    /// <summary>Built on first use: a face is loaded to shape with far more often than to draw from.</summary>
    GlyphOutlineSource Outlines => outlines ??= GlyphOutlineSource.Create(Table, UnitsPerEm);

    ImmutableArray<AxisSegmentMap> SegmentMaps =>
        maps ??= VariationTables.ReadSegmentMaps(Table("avar"), Axes.Length);

    byte[] Table(string tag) {
        using var table = face.ReferenceTable(new Tag(tag[0], tag[1], tag[2], tag[3]));
        return table.Length == 0 ? [] : table.AsSpan().ToArray();
    }

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

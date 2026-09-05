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

/// <summary>Where a font wants a decoration line drawn, in design units.</summary>
/// <remarks>
///     <para>
///         <b>Every number here comes out of the face's own tables and none of them is a constant.</b>
///         That is the whole point of the type. Across the twenty-two faces this repository ships,
///         the underline thickness ranges from 20 design units at 2048 per em — under one hundredth
///         of an em — to 184 at the same grid, which is nine per cent of it. A hardcoded hairline
///         looks deliberate in Open Sans and looks like a rendering fault in the Nastaliq face beside
///         it, and no test of a single font can tell the two apart.
///     </para>
///     <para>
///         ⚠ <b>Positive is <i>upwards</i>, as the font grid has it, so an underline offset is
///         negative.</b> The draw list is y-down; <see cref="Vixen.Ui" />'s <c>TextRun</c> is where
///         the sign flips, in the same place and for the same reason <c>Place</c> flips a glyph's.
///     </para>
///     <para>
///         ⚠ <b>An offset is the <i>centre</i> of the stem, not its top.</b> The two readings of
///         <c>post.underlinePosition</c> are a genuine disagreement in the wild — the OpenType
///         specification says the top, Apple's says the centre, and FreeType and Skia both take the
///         centre, so the overwhelming majority of shipped fonts were drawn against that reading.
///         Following the majority is what makes a face look like it does everywhere else.
///     </para>
/// </remarks>
/// <param name="UnderlineOffset">The centre of the underline stem, relative to the baseline.</param>
/// <param name="UnderlineThickness">How thick that stem is. Always positive.</param>
/// <param name="StrikeoutOffset">The centre of the line-through stem. Positive: it crosses the glyphs.</param>
/// <param name="StrikeoutThickness">How thick <i>that</i> stem is. Always positive.</param>
public readonly record struct DecorationMetrics(
    int UnderlineOffset,
    int UnderlineThickness,
    int StrikeoutOffset,
    int StrikeoutThickness
);

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

    /// <summary>Where this face wants an underline and a line-through, in design units.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Read on every access rather than cached beside <see cref="Metrics" />, and the
    ///         difference is variable fonts.</b> <c>MVAR</c> moves all four of these along an axis, and
    ///         HarfBuzz answers for whatever instance <see cref="SetInstance" /> left the shaper at —
    ///         so a cached copy would be the default instance's numbers wearing the current one's
    ///         name. It is one native call per <i>decorated run</i>, not per glyph, against four that
    ///         shaping the run already made.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A face that reports nothing is synthesised from, and a face that reports zero is
    ///         treated as reporting nothing.</b> Both happen: <c>TestGSUBOne.otf</c> in this
    ///         repository's own font set carries a <c>post</c> table whose underline position and
    ///         thickness are both literally zero. Taken at face value that is a zero-height line on
    ///         the baseline — an underline that is invisible <i>and</i> in the wrong place — which
    ///         would read as a broken feature rather than as a broken font.
    ///     </para>
    ///     <para>
    ///         The synthesised numbers are FreeType's, because they are the ones every other
    ///         toolkit's fallback text has been measured against: a twentieth of an em thick, half a
    ///         tenth of an em below the baseline. The strikeout falls back to the underline's
    ///         thickness — which is what the face itself says in nineteen of the twenty-two here —
    ///         and to half the x-height for its position, since crossing the lowercase is the only
    ///         thing a line-through has to get right.
    ///     </para>
    /// </remarks>
    public DecorationMetrics Decoration {
        get {
            var underlineThickness = Positive(OpenTypeMetricsTag.UnderlineSize) ?? UnitsPerEm / 20;
            var underlineOffset = Metric(OpenTypeMetricsTag.UnderlineOffset) is { } offset and not 0
                ? offset
                : -(UnitsPerEm / 10);

            // The x-height is itself optional — two of the faces here report zero for it — so the
            // last resort is a quarter of the ascender, which lands within a few per cent of what
            // every face that *does* answer says.
            var strikeoutOffset = Positive(OpenTypeMetricsTag.StrikeoutOffset)
                ?? (Positive(OpenTypeMetricsTag.XHeight) is { } height ? height / 2 : Metrics.Ascender / 4);

            return new DecorationMetrics(
                underlineOffset,
                underlineThickness,
                strikeoutOffset,
                Positive(OpenTypeMetricsTag.StrikeoutSize) ?? underlineThickness
            );
        }
    }

    /// <summary>The height of a lowercase <c>x</c>, in design units.</summary>
    /// <remarks>
    ///     ⚠ <b>Optional, and two of the twenty-two faces in this repository report zero for it</b> —
    ///     so a face with no opinion is synthesised from rather than believed. Half the ascender lands
    ///     within a few per cent of what every face that <i>does</i> answer says, and the number's one
    ///     consumer is <c>vertical-align: middle</c>, which is a half of it: an x-height read as zero
    ///     would centre a box on the baseline itself and look like the alignment had been ignored.
    /// </remarks>
    public int XHeight => Positive(OpenTypeMetricsTag.XHeight) ?? Metrics.Ascender / 2;

    /// <summary>How far below the baseline this face sets a subscript, in design units.</summary>
    /// <remarks>
    ///     The fallback is a fifth of an em, which is what browsers use for a face that carries no
    ///     <c>OS/2</c> subscript offset. Positive is downwards here, unlike
    ///     <see cref="SuperscriptOffset" />, because each is the distance in the direction its own
    ///     name goes.
    /// </remarks>
    public int SubscriptOffset => Positive(OpenTypeMetricsTag.SubScriptEmYOffset) ?? UnitsPerEm / 5;

    /// <summary>How far above the baseline this face sets a superscript, in design units.</summary>
    /// <remarks>The fallback is a third of an em, for the reason on <see cref="SubscriptOffset" />.</remarks>
    public int SuperscriptOffset => Positive(OpenTypeMetricsTag.SuperScriptEmYOffset) ?? UnitsPerEm / 3;

    /// <summary>One OpenType metric, or null where the face does not carry the table it lives in.</summary>
    int? Metric(OpenTypeMetricsTag tag) =>
        font.OpenTypeMetrics.TryGetPosition(tag, out var value) ? value : null;

    /// <summary>The same, treating a zero or a negative as "the face has no opinion".</summary>
    /// <remarks>
    ///     Only for the three metrics whose meaning requires a positive number — a thickness, and a
    ///     distance above the baseline. <see cref="Decoration" />'s underline offset is the one that
    ///     is legitimately negative and is tested against zero on its own.
    /// </remarks>
    int? Positive(OpenTypeMetricsTag tag) => Metric(tag) is { } value and > 0 ? value : null;

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

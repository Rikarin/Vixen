// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;

namespace Vixen.Ui.Text.Rasterizing;

/// <summary>Where a glyph's field is in the atlas, and the quad to draw it in.</summary>
/// <remarks>
///     ⚠ Not <c>GlyphPlacement</c>, which this assembly already has and which means something else
///     — where a <i>shaped</i> glyph sits in a run. Two types of one name in one assembly is a
///     mistake nobody makes twice and everybody has to read around.
/// </remarks>
/// <param name="Region">Its rectangle in the atlas texture, in pixels.</param>
/// <param name="Left">The quad's left edge, in ems from the pen position.</param>
/// <param name="Top">Its top edge, in ems above the baseline. Positive is up.</param>
/// <param name="Right">Its right edge, in ems from the pen.</param>
/// <param name="Bottom">Its bottom edge, in ems. Negative below the baseline.</param>
/// <param name="ScreenPixelRange">
///     How many screen pixels one unit of the field's range covers, at a font size of one em. A
///     shader multiplies this by the size it is drawing at to know how hard to threshold.
/// </param>
/// <remarks>
///     ⚠ <b>Ems, not pixels.</b> The atlas is size-independent on purpose, so its metadata has to be
///     too — a placement in pixels would be right for one font size and wrong for the next, which is
///     the whole failure the size-free key exists to avoid. A caller multiplies by the size it is
///     drawing at.
/// </remarks>
public readonly record struct AtlasGlyph(
    AtlasRegion Region,
    float Left,
    float Top,
    float Right,
    float Bottom,
    float ScreenPixelRange
) {
    /// <summary>Whether the glyph draws nothing, as a space does.</summary>
    public bool IsEmpty => Region.Width == 0 || Region.Height == 0;
}

/// <summary>
///     Turns a glyph into an atlas entry, generating its field the first time it is asked for.
/// </summary>
/// <remarks>
///     <para>
///         The join between the three pieces below it: <c>FontFace</c> reads the outline,
///         <see cref="DistanceField" /> encodes it, <see cref="GlyphAtlas" /> holds it. A renderer
///         asks one question — where is this glyph — and never learns that any of them exist.
///     </para>
///     <para>
///         ⚠ <b>The placement is remembered separately from the atlas entry, and outlives it.</b>
///         Eviction takes the pixels; it does not change where the glyph sits relative to the pen,
///         because that came from the font. Recomputing it on every miss would mean reading the
///         outline again to learn something that cannot have changed.
///     </para>
/// </remarks>
public sealed class GlyphFieldCache {
    readonly Dictionary<GlyphKey, AtlasGlyph> placements = [];

    /// <summary>Creates a cache over an atlas.</summary>
    /// <param name="atlas">Where the fields go.</param>
    /// <param name="resolution">How many pixels the glyph's longer side is encoded at.</param>
    /// <param name="range">How many pixels either side of the edge the field spans.</param>
    /// <remarks>
    ///     ⚠ <b>The resolution is the quality knob and the memory knob at once</b>, and it is a
    ///     property of the atlas rather than of the text: every glyph is encoded at the same one, so
    ///     that a page mixing sizes shares its entries. Too low and a tight counter closes up; too
    ///     high and a few hundred glyphs fill the texture.
    /// </remarks>
    public GlyphFieldCache(GlyphAtlas atlas, int resolution = 32, float range = 4f) {
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentOutOfRangeException.ThrowIfLessThan(resolution, 4);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(range);

        Atlas = atlas;
        Resolution = resolution;
        Range = range;
    }

    /// <summary>The atlas the fields live in.</summary>
    public GlyphAtlas Atlas { get; }

    /// <summary>How many pixels a glyph's longer side is encoded at.</summary>
    public int Resolution { get; }

    /// <summary>How many pixels either side of the edge the field spans.</summary>
    public float Range { get; }

    /// <summary>How many glyphs have had a field generated for them.</summary>
    public long Generated { get; private set; }

    /// <summary>How many lookups had to go back to the font for an outline.</summary>
    /// <remarks>
    ///     Counts the glyphs that draw nothing as well as the ones that draw something, which is the
    ///     point of having it: a space is answered from the placements and never read twice, and
    ///     nothing about the atlas or <see cref="Generated" /> can show that.
    /// </remarks>
    public long Reads { get; private set; }

    /// <summary>Finds a glyph in the atlas, encoding it first if it is not there.</summary>
    /// <param name="font">The face.</param>
    /// <param name="fontIndex">How the caller identifies that face.</param>
    /// <param name="glyph">The glyph id.</param>
    /// <param name="placement">Where it is and where to draw it.</param>
    /// <returns>
    ///     Whether the glyph can be drawn. False for one that draws nothing — a space — and for one
    ///     whose field will not fit the atlas at all.
    /// </returns>
    public bool TryGet(FontFace font, int fontIndex, ushort glyph, out AtlasGlyph placement) {
        ArgumentNullException.ThrowIfNull(font);

        var key = new GlyphKey(fontIndex, glyph, Resolution);

        if (placements.TryGetValue(key, out placement)) {
            // The placement is still true even if the pixels have gone; only the region can change.
            if (placement.IsEmpty) {
                return false;
            }

            if (Atlas.TryGet(key, out var region)) {
                placement = placement with { Region = region };
                return true;
            }
        }

        return Generate(font, key, glyph, out placement);
    }

    bool Generate(FontFace font, GlyphKey key, ushort glyph, out AtlasGlyph placement) {
        placement = default;
        Reads++;

        var outline = font.GetOutline(glyph);
        if (outline.IsEmpty) {
            placements[key] = placement;
            return false;
        }

        var bounds = outline.Bounds();
        if (bounds.Width <= 0 || bounds.Height <= 0) {
            placements[key] = placement;
            return false;
        }

        // The field is padded by its own range, so the distance still falls off outside the glyph
        // rather than being cut at the edge of the cell — a glyph drawn with an outline or a glow
        // reads past its own silhouette, and a cell cropped to the silhouette has nothing there.
        var padding = (int)MathF.Ceiling(Range);
        var scale = Resolution / MathF.Max(bounds.Width, bounds.Height);
        var width = (int)MathF.Ceiling(bounds.Width * scale) + (2 * padding);
        var height = (int)MathF.Ceiling(bounds.Height * scale) + (2 * padding);

        var origin = new Vector2(bounds.MinX - (padding / scale), bounds.MinY - (padding / scale));
        var field = DistanceField.Generate(outline, width, height, scale, origin, Range);

        if (!Atlas.Add(key, field, out var region)) {
            placements[key] = placement;
            return false;
        }

        Generated++;

        // In ems, so one entry serves every size. The quad covers the padded cell rather than the
        // glyph, because that is what the texture holds.
        var em = font.UnitsPerEm;
        placement = new AtlasGlyph(
            region,
            origin.X / em,
            (origin.Y + (height / scale)) / em,
            (origin.X + (width / scale)) / em,
            origin.Y / em,
            scale * em / Range
        );

        placements[key] = placement;
        return true;
    }
}

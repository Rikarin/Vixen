// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Text.Outlines;
using Vixen.Ui.Text.Rasterizing;

namespace Vixen.Ui.Rendering;

/// <summary>Where a path's field is in the atlas, and the quad to draw it in.</summary>
/// <param name="Region">Its rectangle in the atlas texture, in pixels.</param>
/// <param name="Quad">Where it goes, in the same document pixels the path was in.</param>
/// <param name="ScreenPixelRange">
///     How many screen pixels one unit of the field's range covers — what the shader thresholds with.
/// </param>
/// <remarks>
///     ⚠ <b>The quad is in document pixels and not in a view box, which is the opposite of what
///     <c>AtlasGlyph</c> does.</b> A glyph's placement is in ems because a font's outline is
///     size-independent and one entry serves every size; a path arrives here <i>already scaled</i> to
///     where it is going, so its entry is per size. It is not per <i>position</i> — see
///     <see cref="IconAtlas" />, where that distinction is the whole design.
/// </remarks>
public readonly record struct IconField(AtlasRegion Region, Rectangle Quad, float ScreenPixelRange) {
    /// <summary>Whether this draws nothing.</summary>
    public bool IsEmpty => Region.Width == 0 || Region.Height == 0;
}

/// <summary>
///     Turns a filled path into a distance field in the glyph atlas, so that an icon costs four
///     vertices instead of a tessellation.
/// </summary>
/// <remarks>
///     <para>
///         <b>An icon is a glyph that is not in a font.</b> Both are small vector art drawn at
///         whatever size a layout gives them, both are asked for again every frame, and both are
///         wanted in one colour at a time. Text has been drawn from a distance field since there was
///         text here; icons were tessellated, and the difference is not small — a hand-drawn editor
///         glyph whose strokes are pre-expanded into quads measured <b>13,419 vertices</b>, where the
///         same picture out of the atlas is <b>four</b>.
///     </para>
///     <para>
///         ⚠ <b>The same texture as the glyphs, and it has to be.</b> <c>UiRenderer</c> binds one
///         atlas descriptor for the whole frame; a second texture would be a second set, a second
///         pipeline and a batch break between every icon and the label beside it. Sharing means an
///         icon draws through the pipeline that is <i>already bound</i> — so a row of an outliner is
///         one draw for its glyph and its text together rather than three.
///     </para>
///     <para>
///         ⚠ <b>A path must arrive in its own local space, and this is the invariant the whole thing
///         rests on.</b> Encoding a field costs milliseconds; a key that separated two instances of
///         one icon would re-encode per icon per frame and be far slower than the tessellation it
///         replaces. A draw list is rebuilt every frame from wherever things are, so a tree scrolled
///         by a fraction of a pixel moves every icon in it — and the answer is the one
///         <c>DrawCommandKind.Text</c> already uses: a glyph run carries where the line starts and
///         each glyph's offset <i>along</i> it, so two identical labels in different places hold
///         identical runs. A field path carries its position on the command and its geometry from its
///         own corner, so two identical icons in different places hold identical segments and hash to
///         one entry.
///     </para>
///     <para>
///         ⚠ <b>Rounding the coordinates instead does not work, and it is worth saying why so nobody
///         tries it again.</b> Quantising to a fraction of a pixel looks like it should absorb the
///         float error between one scrolled frame and the next, and it does — for one coordinate. An
///         icon has of the order of a thousand of them, so on any given frame one of them is within
///         its own error of a bucket boundary and flips; measured, that was 199 re-encodings in 200
///         scrolled frames. A tolerance cannot fix a problem that is a thousand independent chances
///         to differ.
///     </para>
///     <para>
///         ⚠ <b>An entry is still per size.</b> A glyph's field is size-independent because a font's
///         outline arrives in design units; a path reaches the draw list already fitted to the box the
///         layout produced. That costs an entry per size actually used, which for an editor is a few
///         dozen — and it is the one way this is weaker than the glyph cache.
///     </para>
///     <para>
///         ⚠ <b>Non-zero winding only.</b> <c>GlyphRasterizer</c> fills by non-zero — a counter in an
///         <c>o</c> is a contour wound the other way and every font relies on it — so an even-odd
///         path would be encoded as the shape it is not. Refused here rather than approximated, and
///         the caller tessellates it instead.
///     </para>
/// </remarks>
public sealed class IconAtlas {
    /// <summary>What <c>Font</c> an icon's key carries, which no real font index is.</summary>
    /// <remarks>
    ///     ⚠ <b>What actually keeps the two namespaces apart.</b> <c>GlyphKey.Path</c> is zero for a
    ///     glyph and a hash for an icon, which would be enough if a hash could not be zero — it can,
    ///     and glyph zero is <c>.notdef</c>, so the one icon that hashed to nothing would draw the
    ///     missing-glyph box. A negative font index cannot be produced by <c>DrawList.AddFont</c>,
    ///     which hands out positions in a list.
    /// </remarks>
    const int IconFont = -1;

    /// <summary>How many field texels one document pixel gets, when the size allows it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two, and going higher was measured rather than assumed.</b> Three and four were
    ///         tried against the golden images: three moved one pixel of four controls, four made two
    ///         of them slightly worse, and both cost field area — and so encoding time — as the square.
    ///         What is left at two is not resolution-limited, which is why more of it does nothing:
    ///         the residue is at the sharp tip of a pre-expanded stroke, where a median of three
    ///         distances and an exact scanline fill genuinely disagree about a corner narrower than a
    ///         texel.
    ///     </para>
    ///     <para>
    ///         There <i>is</i> a ceiling, and it is far above this. The encoding clamps at
    ///         <see cref="Range" /> texels either side of the edge, so full coverage is only reachable
    ///         while <c>scale ≤ Range / Feather</c> — eight, for the usual pair. Past that an icon's
    ///         interior never reaches full opacity and it draws washed out inside a faintly visible
    ///         rectangle, which is its own cell.
    ///     </para>
    /// </remarks>
    const float Supersample = 2f;

    readonly Dictionary<ulong, Entry> entries = [];
    readonly List<PathSegment> shape = [];
    readonly List<OutlineSegment> converted = [];

    /// <summary>Creates a cache over an atlas.</summary>
    /// <param name="atlas">Where the fields go — the same one the glyphs are in.</param>
    /// <param name="resolution">The most texels a path's longer side may be encoded at.</param>
    /// <param name="range">
    ///     How many texels either side of the edge the field spans. Four, which is what
    ///     <c>GlyphFieldCache</c> uses and what msdfgen ships. ⚠ Three and two were both measured
    ///     against the golden images and neither is better: three moves fifty pixels of three quarters
    ///     of a million, and two is twice as far out — the dilation that matches the tessellator's
    ///     fringe is a fixed fraction of the range, so a narrow one leaves it no room before the
    ///     encoding clamps.
    /// </param>
    public IconAtlas(GlyphAtlas atlas, int resolution = 48, float range = 4f) {
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentOutOfRangeException.ThrowIfLessThan(resolution, 4);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(range);

        Atlas = atlas;
        Resolution = resolution;
        Range = range;
    }

    /// <summary>The atlas the fields live in.</summary>
    public GlyphAtlas Atlas { get; }

    /// <summary>The most texels a path's longer side may be encoded at.</summary>
    /// <remarks>
    ///     ⚠ <b>A ceiling, not the size.</b> A small icon is encoded at <see cref="Supersample" />
    ///     texels per document pixel and never reaches this; a large one is capped here and drawn
    ///     magnified, which is what a multi-channel field is good at. Encoding everything at one size
    ///     would minify the small ones — see the scale in <c>Generate</c> for what that costs.
    /// </remarks>
    public int Resolution { get; }

    /// <summary>How many pixels either side of the edge the field spans.</summary>
    public float Range { get; }

    /// <summary>The largest a path may be, in document pixels, and still be worth a field.</summary>
    /// <remarks>
    ///     ⚠ <b>A quality ceiling, not a memory one.</b> The field is <see cref="Resolution" /> on its
    ///     longer side whatever the path's size, so a path twice that is a field magnified two-to-one
    ///     — which a multi-channel field carries well for edges and corners and less well for the
    ///     detail of a curve. Past this the caller tessellates, which is exact at any size and is what
    ///     a large path should have been doing anyway: the saving here is per <i>icon</i>, and a
    ///     drawing that fills a panel is one path rather than the hundreds a tree of rows is.
    /// </remarks>
    public float MaxSize { get; set; } = 96f;

    /// <summary>How wide the coverage ramp along an edge is, in document pixels.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The tessellator's fringe, and matching it is a consistency requirement rather than
    ///         a matter of taste.</b> <c>PathTessellator.Feather</c> puts <i>full</i> coverage on the
    ///         outline itself and ramps to nothing over the fringe <i>outside</i> it — so every
    ///         tessellated path in this engine is already half a pixel fatter than the shape it was
    ///         given, with a half-pixel edge rather than a one-pixel one. A field thresholded the
    ///         natural way, at half coverage on the true edge, is visibly lighter and softer than the
    ///         identical path drawn the other way; and the two ways sit inside one glyph, because an
    ///         <c>IconArt</c> path with a fill and a stroke draws the fill from here and the stroke
    ///         through the tessellator. Half a pixel of disagreement between the two halves of one
    ///         icon is a drawing fault, not a nuance.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One number, and it decides two things that have to agree.</b> The ramp is this
    ///         wide, which sets <see cref="IconField.ScreenPixelRange" />; and it lies wholly outside
    ///         the shape, which is a dilation of <i>half</i> of it. Setting either alone gets a weight
    ///         that is right and an edge that is not, or the reverse — the first attempt dilated by a
    ///         whole fringe and drew icons a pixel too fat.
    ///     </para>
    ///     <para>
    ///         The dilation is baked into the field rather than applied by the shader, because the
    ///         shader is shared with text and text has no fringe to match. A constant added to all
    ///         three channels moves their median by the same amount, which is exactly a dilation.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is also the amplifier, and that is worth knowing before anything else here is
    ///         blamed for a misshapen icon.</b> A dilation moves every value towards covered, so a
    ///         texel the encoding got <i>wrong by magnitude alone</i> — right about which side of the
    ///         shape it is on, wrong about how far — crosses the threshold and becomes wrong about the
    ///         side too. That is exactly what happened: <c>DistanceField</c> extended a horizontal
    ///         edge's line past its own corner, which reads as half a texel outside the shape all the
    ///         way across the cell, and half a texel is precisely what this adds. The transport's two
    ///         pause bars gained a bar across the top joining them and became an I-beam. Neither
    ///         <see cref="Resolution" />, <see cref="Range" /> nor the supersample can reach a defect
    ///         of that shape — the sweeps recorded on them were measuring the wrong thing, not
    ///         measuring badly — and a plain square had it too, and only escaped notice because its
    ///         phantom band had nothing to bridge.
    ///     </para>
    /// </remarks>
    public float Feather { get; set; } = 0.5f;

    /// <summary>The narrowest ramp, so that a caller who turned the fringe off does not divide by it.</summary>
    /// <remarks>
    ///     A surface that multisamples sets the fringe to zero and wants a hard edge from the
    ///     tessellator; a fifth of a pixel is as hard an edge as a field usefully gives, and is a
    ///     number rather than a special case.
    /// </remarks>
    const float NarrowestFeather = 0.2f;

    /// <summary>How many paths have had a field generated for them.</summary>
    /// <remarks>
    ///     ⚠ <b>The number that says the key is working.</b> Encoding a field is milliseconds; a
    ///     figure that keeps climbing while the interface is merely scrolling means the key is
    ///     separating instances of one icon, which is the failure this cache is most able to have
    ///     while appearing to work.
    /// </remarks>
    public long Generated { get; private set; }

    /// <summary>How many distinct paths have an entry, whether or not the atlas still holds one.</summary>
    public int Count => entries.Count;

    /// <summary>How many paths were refused — too large, the wrong rule, or bigger than the atlas.</summary>
    /// <remarks>
    ///     ⚠ Counted rather than thrown on, and it is not a fault: a refusal means the caller
    ///     tessellates, which draws the same picture more slowly. What it must not be is silent,
    ///     because a figure that stays high is the whole saving quietly not happening.
    /// </remarks>
    public long Refused { get; private set; }

    /// <summary>Finds a path's field, encoding it first if it is not there.</summary>
    /// <param name="segments">Where the path lives.</param>
    /// <param name="offset">Where it starts.</param>
    /// <param name="length">How many segments.</param>
    /// <param name="rule">How its inside is decided.</param>
    /// <param name="field">
    ///     Where it is in the atlas, and its quad <b>in the path's own coordinates</b> — which the
    ///     caller offsets by wherever it is putting the path.
    /// </param>
    /// <returns>Whether it can be drawn from the atlas at all.</returns>
    public bool TryGet(
        IReadOnlyList<PathSegment> segments,
        int offset,
        int length,
        PathFillRule rule,
        out IconField field
    ) {
        ArgumentNullException.ThrowIfNull(segments);

        field = default;

        if (length <= 0 || (uint) offset + (uint) length > (uint) segments.Count) {
            return false;
        }

        var key = Collect(segments, offset, length, rule);

        // ⚠ Confirmed against the stored geometry rather than trusted from the hash, exactly as the
        // tessellation cache is. A collision here would draw one icon in another's place — a wrong
        // picture rather than a slow one, and the kind that appears on one machine and not the next.
        if (entries.TryGetValue(key, out var entry) && entry.Matches(shape, rule)) {
            if (entry.Field.IsEmpty) {
                // Refused once is refused for good: nothing about the shape has changed, so a second
                // attempt would rasterise it again to reach the same answer.
                return false;
            }

            // ⚠ The quad is still true even if the pixels have gone. Only the region can move, and
            // only compaction moves it — the same split `GlyphFieldCache` keeps for the same reason.
            if (Atlas.TryGet(Key(key), out var region)) {
                field = entry.Field with { Region = region };
                return true;
            }
        }

        return Generate(key, rule, out field);
    }

    static GlyphKey Key(ulong path) => new(IconFont, 0, 0) { Path = path };

    /// <summary>Copies the path into <see cref="shape" />, hashing it on the way.</summary>
    /// <remarks>
    ///     Copied rather than read twice because the segments are what confirms a hit, and because
    ///     everything below here reads one contiguous list rather than a range of somebody else's.
    /// </remarks>
    ulong Collect(IReadOnlyList<PathSegment> segments, int offset, int length, PathFillRule rule) {
        shape.Clear();

        var hash = 14695981039346656037UL;

        Mix(ref hash, (ulong) rule);
        Mix(ref hash, (ulong) length);

        for (var i = 0; i < length; i++) {
            var segment = segments[offset + i];

            shape.Add(segment);

            Mix(ref hash, (ulong) segment.Verb);
            Mix(ref hash, BitConverter.SingleToUInt32Bits(segment.P0.X));
            Mix(ref hash, BitConverter.SingleToUInt32Bits(segment.P0.Y));
            Mix(ref hash, BitConverter.SingleToUInt32Bits(segment.P1.X));
            Mix(ref hash, BitConverter.SingleToUInt32Bits(segment.P1.Y));
            Mix(ref hash, BitConverter.SingleToUInt32Bits(segment.P2.X));
            Mix(ref hash, BitConverter.SingleToUInt32Bits(segment.P2.Y));
        }

        return hash;
    }

    static void Mix(ref ulong hash, ulong value) {
        hash = (hash ^ value) * 1099511628211UL;
    }

    /// <summary>Encodes <see cref="shape" /> and puts it in the atlas.</summary>
    bool Generate(ulong key, PathFillRule rule, out IconField field) {
        field = default;

        if (rule != PathFillRule.NonZero) {
            return Refuse(key, rule, out field);
        }

        var outline = Outline();
        if (outline.IsEmpty) {
            return Refuse(key, rule, out field);
        }

        var bounds = outline.Bounds();
        if (bounds.Width <= 0f || bounds.Height <= 0f || MathF.Max(bounds.Width, bounds.Height) > MaxSize) {
            return Refuse(key, rule, out field);
        }

        // Padded by the field's own range, so the distance still falls off outside the shape rather
        // than being cut at the edge of the cell — the same reason a glyph's cell is padded.
        var padding = (int) MathF.Ceiling(Range);

        // ⚠ <b>Texels per document pixel, capped — not a fixed field size, and the difference is the
        // whole picture.</b> `DistanceField.Encode` clamps to ±`Range` <i>texels</i>, so the field can
        // only describe distances within `Range / scale` document pixels of the edge; past that every
        // texel reads the same. The shader turns that into `saturate(d + 0.5)`, so a coverage of one
        // is reached only `0.5 * Range / scale` pixels inside the shape — and with a fixed field over
        // a small icon that number goes below a half and <b>coverage never reaches nought or one at
        // all</b>. What draws then is a washed-out glyph inside a faintly visible rectangle: the cell.
        // Keeping the scale at or under `Range / 2` is what guarantees a solid interior and a
        // transparent outside, and taking the smaller of the two is what stops a large path from
        // being encoded at a size nobody wants to rasterise.
        var scale = MathF.Min(Supersample, Resolution / MathF.Max(bounds.Width, bounds.Height));
        var width = (int) MathF.Ceiling(bounds.Width * scale) + (2 * padding);
        var height = (int) MathF.Ceiling(bounds.Height * scale) + (2 * padding);

        var origin = new Vector2(bounds.MinX - (padding / scale), bounds.MinY - (padding / scale));
        var bitmap = DistanceField.Generate(outline, width, height, scale, origin, Range);

        var feather = MathF.Max(Feather, NarrowestFeather);

        // Half the ramp, because the ramp lies outside the shape rather than across it. One unit of
        // the encoding is `Range` texels, which is what turns document pixels into the field's terms.
        var bias = feather * 0.5f * scale / Range;

        for (var i = 0; i < bitmap.Channels.Length; i++) {
            bitmap.Channels[i] = Math.Clamp(bitmap.Channels[i] + bias, 0f, 1f);
        }

        if (!Atlas.Add(Key(key), bitmap, out var region)) {
            return Refuse(key, rule, out field);
        }

        Generated++;

        // ⚠ Back into the document's y-down space, which is what `Outline` negated on the way in.
        // The cell's *top* is the outline's largest y, so the quad's origin is a negation of the far
        // edge rather than of the near one — reading it the other way puts every icon exactly its own
        // height out of place, which looks like a layout fault rather than a sign one.
        var cell = new Rectangle(origin.X, -(origin.Y + (height / scale)), width / scale, height / scale);

        // One unit of the encoded value is `Range` texels and a texel is `1/scale` document pixels,
        // so `Range / scale` is how far a full unit reaches on screen — and dividing by the ramp's
        // width is what makes the shader's `saturate(d + 0.5)` span exactly that ramp. Nothing
        // multiplies it later, because the path arrived already at the size it is drawn at.
        field = new IconField(region, cell, Range / scale / feather);
        entries[key] = new Entry(field, shape, rule);

        return true;
    }

    bool Refuse(ulong key, PathFillRule rule, out IconField field) {
        field = default;
        entries[key] = new Entry(field, shape, rule);
        Refused++;

        return false;
    }

    /// <summary>
    ///     <see cref="shape" /> as an outline a distance field can read, with y turned the other way
    ///     up.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Negated, because the two layers disagree about which way y runs and both are right.</b>
    ///     A document's y grows downwards; an outline's grows up, because that is how a font is
    ///     written and how <c>DistanceField</c> reads a pixel's centre back into outline units.
    ///     Feeding document coordinates through unchanged encodes the shape upside down — and it is
    ///     upside down about its own centre, so a symmetric icon looks perfect and an arrow points the
    ///     wrong way.
    /// </remarks>
    GlyphOutline Outline() {
        converted.Clear();

        foreach (var segment in shape) {
            switch (segment.Verb) {
                case PathVerb.Move:
                    converted.Add(new OutlineSegment(OutlineVerb.Move, segment.P2.X, -segment.P2.Y));
                    break;

                case PathVerb.Line:
                    converted.Add(new OutlineSegment(OutlineVerb.Line, segment.P2.X, -segment.P2.Y));
                    break;

                case PathVerb.Quadratic:
                    converted.Add(
                        new OutlineSegment(
                            OutlineVerb.Quadratic,
                            segment.P0.X,
                            -segment.P0.Y,
                            segment.P2.X,
                            -segment.P2.Y
                        )
                    );

                    break;

                case PathVerb.Cubic:
                    converted.Add(
                        new OutlineSegment(
                            OutlineVerb.Cubic,
                            segment.P0.X,
                            -segment.P0.Y,
                            segment.P1.X,
                            -segment.P1.Y,
                            segment.P2.X,
                            -segment.P2.Y
                        )
                    );

                    break;

                default:
                    converted.Add(new OutlineSegment(OutlineVerb.Close, 0f, 0f));
                    break;
            }
        }

        return GlyphOutline.FromSegments(converted);
    }

    /// <summary>One path's field, and the normalised geometry that confirms a hit is really one.</summary>
    sealed class Entry {
        readonly PathSegment[] segments;
        readonly PathFillRule rule;

        public Entry(IconField field, List<PathSegment> segments, PathFillRule rule) {
            Field = field;
            this.segments = [.. segments];
            this.rule = rule;
        }

        public IconField Field { get; }

        public bool Matches(List<PathSegment> candidate, PathFillRule candidateRule) {
            if (candidateRule != rule || candidate.Count != segments.Length) {
                return false;
            }

            for (var i = 0; i < segments.Length; i++) {
                if (candidate[i] != segments[i]) {
                    return false;
                }
            }

            return true;
        }
    }
}

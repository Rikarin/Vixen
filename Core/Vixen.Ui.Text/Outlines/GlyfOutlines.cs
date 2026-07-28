// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Text.Outlines;

/// <summary>Reads TrueType outlines out of <c>glyf</c>, indexed through <c>loca</c>.</summary>
/// <remarks>
///     ⚠ <b>Point-matched composites are not implemented.</b> A component may position itself by
///     naming a point index in what has been placed so far instead of carrying an offset. Not one
///     glyph in the 242 fonts the spike read used it — see
///     <c>docs/plan/spikes/text-glyph-outlines/RESULT.md</c> — so it is a known omission rather than
///     a gap, and such a component is placed at the origin rather than dropped.
/// </remarks>
internal sealed class GlyfOutlines {
    /// <summary>How deep a composite may nest before this stops following it.</summary>
    /// <remarks>A font whose composites cycle would otherwise recurse until the stack ran out.</remarks>
    const int MaxDepth = 8;

    /// <summary>The four points every glyph has past its own, holding the metrics a font can vary.</summary>
    /// <remarks>
    ///     They are allocated whether or not the font is variable, because <c>gvar</c> numbers points
    ///     including them: see <see cref="GlyphVariations" />.
    /// </remarks>
    const int PhantomPoints = 4;

    readonly byte[] loca;
    readonly byte[] glyf;
    readonly GlyphVariations? variations;
    readonly bool longLoca;

    public GlyfOutlines(byte[] head, byte[] maxp, byte[] loca, byte[] glyf, GlyphVariations? variations) {
        this.loca = loca;
        this.glyf = glyf;
        this.variations = variations;

        longLoca = new SfntReader(head) { Position = 50 }.S16() != 0;
        GlyphCount = new SfntReader(maxp) { Position = 4 }.U16();
    }

    public int GlyphCount { get; }

    /// <summary>The box the font stores in the glyph's header, which is not always the truth.</summary>
    /// <param name="glyph">The glyph.</param>
    /// <returns>The box, or null when the glyph draws nothing.</returns>
    public GlyphBounds? StoredBounds(int glyph) {
        if (!Range(glyph, out var start, out _)) {
            return null;
        }

        var reader = new SfntReader(glyf) { Position = start + 2 };
        var minX = reader.S16();
        var minY = reader.S16();
        var maxX = reader.S16();
        var maxY = reader.S16();
        return new GlyphBounds(minX, minY, maxX, maxY);
    }

    /// <summary>Builds a glyph's contours, optionally at a variable instance.</summary>
    /// <param name="glyph">The glyph.</param>
    /// <param name="variation">Where along the font's axes, or null for the font as it is stored.</param>
    public GlyphOutline Read(int glyph, FontVariation? variation = null) {
        var builder = new OutlineBuilder();
        Append(builder, glyph, Varied(variation), 1, 0, 0, 1, 0, 0, 0);
        return builder.Build();
    }

    /// <summary>The instance to read at, or null when reading it would be a no-op.</summary>
    FontVariation? Varied(FontVariation? variation) =>
        variations is not null && variation is { IsNone: false } ? variation : null;

    bool Range(int glyph, out int start, out int end) {
        start = end = 0;
        if (glyph < 0 || glyph >= GlyphCount) {
            return false;
        }

        start = LocaAt(glyph);
        end = LocaAt(glyph + 1);

        // end == start is an empty glyph — a space — and legitimate rather than an error.
        return end > start && end <= glyf.Length;
    }

    int LocaAt(int index) {
        var reader = new SfntReader(loca);
        if (longLoca) {
            reader.Position = index * 4;
            return reader.Has(4) ? (int)reader.U32() : 0;
        }

        reader.Position = index * 2;
        return reader.Has(2) ? reader.U16() * 2 : 0;
    }

    void Append(
        OutlineBuilder builder,
        int glyph,
        FontVariation? variation,
        float a,
        float b,
        float c,
        float d,
        float e,
        float f,
        int depth
    ) {
        if (depth > MaxDepth || !Range(glyph, out var start, out _)) {
            return;
        }

        var reader = new SfntReader(glyf) { Position = start };
        var contours = reader.S16();
        reader.Position += 8;                              // the stored bounding box

        if (contours >= 0) {
            Simple(builder, ref reader, glyph, variation, contours, a, b, c, d, e, f);
            return;
        }

        Composite(builder, ref reader, glyph, variation, a, b, c, d, e, f, depth);
    }

    /// <summary>One component of a composite, as the table stores it and before it is placed.</summary>
    readonly record struct Component(int Glyph, float Dx, float Dy, float A, float B, float C, float D);

    void Composite(
        OutlineBuilder builder,
        ref SfntReader reader,
        int glyph,
        FontVariation? variation,
        float a,
        float b,
        float c,
        float d,
        float e,
        float f,
        int depth
    ) {
        var components = new List<Component>();

        while (reader.Has(4)) {
            var flags = reader.U16();
            var component = reader.U16();

            float dx;
            float dy;
            if ((flags & 0x0001) != 0) {                   // ARG_1_AND_2_ARE_WORDS
                dx = reader.S16();
                dy = reader.S16();
            } else {
                dx = reader.S8();
                dy = reader.S8();
            }

            if ((flags & 0x0002) == 0) {                   // ARGS_ARE_XY_VALUES unset: point matching
                dx = dy = 0;
            }

            float ca = 1, cb = 0, cc = 0, cd = 1;
            if ((flags & 0x0008) != 0) {                   // WE_HAVE_A_SCALE
                ca = cd = reader.F2Dot14();
            } else if ((flags & 0x0040) != 0) {            // WE_HAVE_AN_X_AND_Y_SCALE
                ca = reader.F2Dot14();
                cd = reader.F2Dot14();
            } else if ((flags & 0x0080) != 0) {            // WE_HAVE_A_TWO_BY_TWO
                ca = reader.F2Dot14();
                cb = reader.F2Dot14();
                cc = reader.F2Dot14();
                cd = reader.F2Dot14();
            }

            components.Add(new Component(component, dx, dy, ca, cb, cc, cd));

            if ((flags & 0x0020) == 0) {                   // MORE_COMPONENTS
                break;
            }
        }

        Vary(glyph, variation, components);

        foreach (var component in components) {
            // ⚠ The offset is in the *parent's* space, so it is carried through the parent matrix
            // and not through the component's own. Scaling it by the component's transform is what
            // SCALED_COMPONENT_OFFSET asks for and is not the default; getting it the wrong way
            // round moves every accent on every scaled composite.
            Append(
                builder,
                component.Glyph,
                variation,
                (component.A * a) + (component.B * c),
                (component.A * b) + (component.B * d),
                (component.C * a) + (component.D * c),
                (component.C * b) + (component.D * d),
                (component.Dx * a) + (component.Dy * c) + e,
                (component.Dx * b) + (component.Dy * d) + f,
                depth + 1
            );
        }
    }

    /// <summary>Moves a composite's components, which is the only thing <c>gvar</c> can vary about one.</summary>
    /// <remarks>
    ///     ⚠ <b>A composite's points are its components' offsets, one point each.</b> Not the points
    ///     of the glyphs it is made of — those vary on their own account when they are read, and
    ///     applying the parent's deltas to them as well would double every delta on every accented
    ///     letter in a variable font. There are no contours here, so nothing is interpolated: a
    ///     component the table does not name simply does not move.
    /// </remarks>
    void Vary(int glyph, FontVariation? variation, List<Component> components) {
        if (variations is null || variation is null || components.Count == 0) {
            return;
        }

        var xs = new float[components.Count + PhantomPoints];
        var ys = new float[components.Count + PhantomPoints];

        for (var i = 0; i < components.Count; i++) {
            xs[i] = components[i].Dx;
            ys[i] = components[i].Dy;
        }

        if (!variations.Apply(glyph, variation, xs, ys, [])) {
            return;
        }

        for (var i = 0; i < components.Count; i++) {
            components[i] = components[i] with { Dx = xs[i], Dy = ys[i] };
        }
    }

    void Simple(
        OutlineBuilder builder,
        ref SfntReader reader,
        int glyph,
        FontVariation? variation,
        int contours,
        float a,
        float b,
        float c,
        float d,
        float e,
        float f
    ) {
        if (contours == 0) {
            return;
        }

        var ends = new int[contours];
        for (var i = 0; i < contours; i++) {
            ends[i] = reader.U16();
        }

        var points = ends[^1] + 1;
        if (points <= 0) {
            return;
        }

        // ⚠ A local, not `reader.Position += reader.U16()`. See SfntReader's remarks.
        var instructions = reader.U16();
        reader.Position += instructions;

        var flags = new byte[points];
        for (var i = 0; i < points && reader.Has(1);) {
            var flag = reader.U8();
            flags[i++] = flag;

            if ((flag & 0x08) == 0) {                      // REPEAT_FLAG
                continue;
            }

            var repeat = reader.Has(1) ? reader.U8() : 0;
            while (repeat-- > 0 && i < points) {
                flags[i++] = flag;
            }
        }

        var xs = Coordinates(ref reader, flags, points, 0x02, 0x10);
        var ys = Coordinates(ref reader, flags, points, 0x04, 0x20);

        if (variation is not null) {
            variations?.Apply(glyph, variation, xs, ys, ends);
        }

        var first = 0;
        foreach (var last in ends) {
            if (last >= first && last < points) {
                Contour(builder, flags, xs, ys, first, last, a, b, c, d, e, f);
            }

            first = last + 1;
        }
    }

    /// <summary>One coordinate axis, stored as deltas with three encodings chosen by two flag bits.</summary>
    /// <remarks>
    ///     The array is four longer than the glyph has points, and the tail is left at zero: those
    ///     are the phantom points, which carry no contour and exist so that a <c>gvar</c> point
    ///     number that names one lands somewhere rather than out of range.
    /// </remarks>
    static float[] Coordinates(ref SfntReader reader, byte[] flags, int points, byte shortBit, byte sameBit) {
        var values = new float[points + PhantomPoints];
        var value = 0;

        for (var i = 0; i < points; i++) {
            if ((flags[i] & shortBit) != 0) {
                var delta = reader.Has(1) ? reader.U8() : 0;
                value += (flags[i] & sameBit) != 0 ? delta : -delta;
            } else if ((flags[i] & sameBit) == 0) {
                value += reader.Has(2) ? reader.S16() : 0;
            }

            values[i] = value;
        }

        return values;
    }

    /// <summary>One contour, from points that may not start on the curve and may not be on it at all.</summary>
    /// <remarks>
    ///     ⚠ <b>Two consecutive off-curve points imply an on-curve point midway between them</b>, and
    ///     a contour may begin off-curve. Both are ordinary in real fonts and both produce a visibly
    ///     wrong shape rather than an error when missed.
    /// </remarks>
    static void Contour(
        OutlineBuilder builder,
        byte[] flags,
        float[] xs,
        float[] ys,
        int first,
        int last,
        float a,
        float b,
        float c,
        float d,
        float e,
        float f
    ) {
        var n = last - first + 1;
        if (n <= 0) {
            return;
        }

        int Wrap(int i) => first + (((i % n) + n) % n);

        (float X, float Y) At(int i) {
            var k = Wrap(i);
            var px = xs[k];
            var py = ys[k];
            return ((a * px) + (c * py) + e, (b * px) + (d * py) + f);
        }

        bool On(int i) => (flags[Wrap(i)] & 0x01) != 0;

        static (float X, float Y) Mid((float X, float Y) p, (float X, float Y) q) =>
            ((p.X + q.X) / 2, (p.Y + q.Y) / 2);

        int index;
        (float X, float Y) start;

        if (On(0)) {
            start = At(0);
            index = 1;
        } else if (On(n - 1)) {
            start = At(n - 1);
            index = 0;
        } else {
            start = Mid(At(n - 1), At(0));
            index = 0;
        }

        builder.Move(start.X, start.Y);

        (float X, float Y)? pending = null;
        for (var seen = 0; seen < n; seen++, index++) {
            var point = At(index);

            if (On(index)) {
                if (pending is { } control) {
                    builder.Quadratic(control.X, control.Y, point.X, point.Y);
                    pending = null;
                } else {
                    builder.Line(point.X, point.Y);
                }

                continue;
            }

            if (pending is { } previous) {
                var implied = Mid(previous, point);
                builder.Quadratic(previous.X, previous.Y, implied.X, implied.Y);
            }

            pending = point;
        }

        if (pending is { } tail) {
            builder.Quadratic(tail.X, tail.Y, start.X, start.Y);
        }

        builder.Close();
    }
}

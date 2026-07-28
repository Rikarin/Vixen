// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Ui.Text.Outlines;

/// <summary>The <c>gvar</c> table: how a glyph's points move as the axes move.</summary>
/// <remarks>
///     <para>
///         <b>A glyph's variation data is a set of tuples, and each tuple is a region plus a delta per
///         point.</b> The region is a peak coordinate per axis — optionally with its own start and end,
///         which is what makes a delta that only applies between two intermediate positions possible —
///         and it produces a scalar between zero and one for wherever the instance sits. Every tuple
///         whose scalar is non-zero contributes its deltas, scaled, and the contributions add.
///     </para>
///     <para>
///         ⚠ <b>Four phantom points sit past the end of every glyph, and they are numbered.</b> A
///         <c>gvar</c> point set indexes them — they carry the deltas that vary the advance and the
///         side bearings — so an array sized to the contour points alone puts every point number past
///         the last contour out of range, and a font that varies its metrics in the same tuple as its
///         outline loses the outline deltas that follow. They are allocated here and ignored: this
///         class draws, and <c>HVAR</c> is what a metrics-aware caller would read.
///     </para>
///     <para>
///         ⚠ <b>Deltas are rounded once, at the end, and not per tuple.</b> Two tuples each
///         contributing 0.5 of a unit make one unit, not zero — rounding as they arrive throws away
///         exactly the intermediate instances the table exists to describe, and does it in a way that
///         looks like a slightly under-weighted font rather than like a bug.
///     </para>
/// </remarks>
internal sealed class GlyphVariations {
    /// <summary>Where the per-glyph offsets start: straight after the fixed header.</summary>
    const int OffsetsPosition = 20;

    readonly byte[] gvar;
    readonly int glyphCount;
    readonly int sharedTupleCount;
    readonly int sharedTuplesOffset;
    readonly int dataOffset;
    readonly bool longOffsets;

    GlyphVariations(
        byte[] gvar,
        int axisCount,
        int glyphCount,
        int sharedTupleCount,
        int sharedTuplesOffset,
        int dataOffset,
        bool longOffsets
    ) {
        this.gvar = gvar;
        this.glyphCount = glyphCount;
        this.sharedTupleCount = sharedTupleCount;
        this.sharedTuplesOffset = sharedTuplesOffset;
        this.dataOffset = dataOffset;
        this.longOffsets = longOffsets;

        AxisCount = axisCount;
    }

    /// <summary>How many axes the table's tuples are written against.</summary>
    public int AxisCount { get; }

    /// <summary>Reads the table's header, or answers null for a font that has no usable one.</summary>
    /// <param name="gvar">The table, or an empty array.</param>
    /// <returns>The reader, or null.</returns>
    public static GlyphVariations? Create(byte[] gvar) {
        if (gvar.Length < OffsetsPosition) {
            return null;
        }

        var reader = new SfntReader(gvar) { Position = 4 };
        var axisCount = reader.U16();
        var sharedTupleCount = reader.U16();
        var sharedTuplesOffset = (int)reader.U32();
        var glyphCount = reader.U16();
        var flags = reader.U16();
        var dataOffset = (int)reader.U32();

        return axisCount == 0 || glyphCount == 0
            ? null
            : new GlyphVariations(
                gvar,
                axisCount,
                glyphCount,
                sharedTupleCount,
                sharedTuplesOffset,
                dataOffset,
                (flags & 0x0001) != 0
            );
    }

    /// <summary>Moves a glyph's points to where the given instance puts them.</summary>
    /// <param name="glyph">The glyph id.</param>
    /// <param name="variation">Where along the axes to read, in normalised coordinates.</param>
    /// <param name="xs">The glyph's x coordinates, contour points then four phantoms. Written in place.</param>
    /// <param name="ys">Its y coordinates, same length.</param>
    /// <param name="ends">The last point index of each contour. Empty for a composite.</param>
    /// <returns>Whether anything moved.</returns>
    public bool Apply(int glyph, FontVariation variation, float[] xs, float[] ys, int[] ends) {
        if (variation.IsNone || glyph < 0 || glyph >= glyphCount || xs.Length != ys.Length) {
            return false;
        }

        var (from, to) = Range(glyph);

        // A glyph with no variation data is the ordinary case for a font that only varies a few of
        // them, not an error.
        if (to <= from) {
            return false;
        }

        var header = new SfntReader(gvar) { Position = dataOffset + from };
        if (!header.Has(4)) {
            return false;
        }

        var counts = header.U16();
        var serialized = dataOffset + from + header.U16();
        var tuples = counts & 0x0FFF;

        var cursor = new SfntReader(gvar) { Position = serialized };
        var shared = (counts & 0x8000) != 0 ? ReadPointNumbers(ref cursor) : null;

        var points = xs.Length;
        var originalX = (float[])xs.Clone();
        var originalY = (float[])ys.Clone();
        var accumulatedX = new float[points];
        var accumulatedY = new float[points];
        var workX = new float[points];
        var workY = new float[points];
        var touched = new bool[points];

        var peak = new float[AxisCount];
        var start = new float[AxisCount];
        var end = new float[AxisCount];
        var moved = false;

        for (var tuple = 0; tuple < tuples; tuple++) {
            if (!header.Has(4)) {
                break;
            }

            var size = header.U16();
            var index = header.U16();
            var intermediate = (index & 0x4000) != 0;

            if ((index & 0x8000) != 0) {                   // EMBEDDED_PEAK_TUPLE
                for (var axis = 0; axis < AxisCount; axis++) {
                    peak[axis] = header.Has(2) ? header.F2Dot14() : 0f;
                }
            } else {
                ReadSharedTuple(index & 0x0FFF, peak);
            }

            if (intermediate) {
                for (var axis = 0; axis < AxisCount; axis++) {
                    start[axis] = header.Has(2) ? header.F2Dot14() : 0f;
                }

                for (var axis = 0; axis < AxisCount; axis++) {
                    end[axis] = header.Has(2) ? header.F2Dot14() : 0f;
                }
            }

            // ⚠ Fixed before the tuple is read rather than after: the size is the authority on where
            // the next tuple's data begins, and a reader that trusted its own parse to have consumed
            // exactly that much would desynchronise the whole glyph on the first delta run it
            // misjudged — producing a mangled glyph rather than a missing one.
            var next = cursor.Position + size;

            var scalar = Scalar(
                variation.Coordinates,
                peak,
                intermediate ? start : null,
                intermediate ? end : null
            );

            if (scalar != 0f) {
                var numbers = (index & 0x2000) != 0 ? ReadPointNumbers(ref cursor) : shared;
                var count = numbers?.Length ?? points;
                var deltaX = ReadPackedDeltas(ref cursor, count);
                var deltaY = ReadPackedDeltas(ref cursor, count);

                moved |= Accumulate(
                    numbers,
                    scalar,
                    deltaX,
                    deltaY,
                    originalX,
                    originalY,
                    accumulatedX,
                    accumulatedY,
                    workX,
                    workY,
                    touched,
                    ends
                );
            }

            cursor.Position = next;
        }

        if (!moved) {
            return false;
        }

        for (var i = 0; i < points; i++) {
            xs[i] = originalX[i] + Round(accumulatedX[i]);
            ys[i] = originalY[i] + Round(accumulatedY[i]);
        }

        return true;
    }

    /// <summary>Half-up, which is what a renderer of these tables does with an accumulated delta.</summary>
    /// <remarks>
    ///     Not <see cref="MathF.Round(float)" />, whose default is half-to-even: a font whose deltas
    ///     land on halves — an axis read exactly half way, which is what a slider at its midpoint
    ///     produces — would round half its points one way and half the other, and the shape would
    ///     shimmer as the slider passed through.
    /// </remarks>
    static float Round(float value) => MathF.Floor(value + 0.5f);

    /// <summary>Adds one tuple's contribution, interpolating the points it does not name.</summary>
    /// <returns>Whether it contributed anything.</returns>
    static bool Accumulate(
        int[]? numbers,
        float scalar,
        float[] deltaX,
        float[] deltaY,
        float[] originalX,
        float[] originalY,
        float[] accumulatedX,
        float[] accumulatedY,
        float[] workX,
        float[] workY,
        bool[] touched,
        int[] ends
    ) {
        var points = originalX.Length;

        // A tuple that names no points names all of them, and then there is nothing to infer.
        if (numbers is null) {
            for (var i = 0; i < points && i < deltaX.Length; i++) {
                accumulatedX[i] += scalar * deltaX[i];
                accumulatedY[i] += scalar * deltaY[i];
            }

            return true;
        }

        Array.Copy(originalX, workX, points);
        Array.Copy(originalY, workY, points);
        Array.Clear(touched);

        var any = false;

        for (var i = 0; i < numbers.Length && i < deltaX.Length; i++) {
            var point = numbers[i];
            if (point < 0 || point >= points) {
                continue;
            }

            workX[point] = originalX[point] + (scalar * deltaX[i]);
            workY[point] = originalY[point] + (scalar * deltaY[i]);
            touched[point] = true;
            any = true;
        }

        if (!any) {
            return false;
        }

        Infer(ends, originalX, workX, touched);
        Infer(ends, originalY, workY, touched);

        for (var i = 0; i < points; i++) {
            accumulatedX[i] += workX[i] - originalX[i];
            accumulatedY[i] += workY[i] - originalY[i];
        }

        return true;
    }

    /// <summary>Gives the points a tuple did not name a delta, contour by contour.</summary>
    /// <remarks>
    ///     ⚠ <b>Per contour, and cyclically within it.</b> A contour is a loop, so the run of
    ///     untouched points after the last touched one is bracketed by that point and the <i>first</i>
    ///     touched one rather than left to the nearest end. Interpolating linearly across the whole
    ///     glyph instead — or leaving untouched points where they were — is what turns a font that
    ///     varies four points of a stem into one whose stem tears.
    /// </remarks>
    static void Infer(int[] ends, float[] original, float[] work, bool[] touched) {
        var first = 0;

        foreach (var last in ends) {
            if (last >= first && last < original.Length) {
                InferContour(first, last, original, work, touched);
            }

            first = last + 1;
        }
    }

    static void InferContour(int first, int last, float[] original, float[] work, bool[] touched) {
        var point = first;
        while (point <= last && !touched[point]) {
            point++;
        }

        // A contour nothing in this tuple touched keeps its shape exactly, which is the whole point
        // of a tuple naming a subset.
        if (point > last) {
            return;
        }

        var firstTouched = point;
        var current = point;

        while (true) {
            point++;
            while (point <= last && !touched[point]) {
                point++;
            }

            if (point > last) {
                break;
            }

            Interpolate(current + 1, point - 1, current, point, original, work);
            current = point;
        }

        if (current == firstTouched) {
            // One touched point in the whole contour: there is nothing to interpolate between, so the
            // contour moves rigidly rather than deforming around a single anchor.
            Shift(first, last, current, original, work);
            return;
        }

        Interpolate(current + 1, last, current, firstTouched, original, work);

        if (firstTouched > first) {
            Interpolate(first, firstTouched - 1, current, firstTouched, original, work);
        }
    }

    /// <summary>Places a run of untouched points between two touched ones.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The two references are ordered by their <i>original</i> coordinate, not by
    ///         index.</b> A point outside the pair's range takes the nearer one's delta whole; one
    ///         inside is placed at the same fraction of the moved span as it sat at of the original.
    ///         Ordering by index instead inverts the fraction on every contour that runs backwards
    ///         along the axis, which is half of them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two references at the same coordinate pulling different ways infer nothing.</b>
    ///         There is no fraction to place a point at when the span is zero, and taking either
    ///         reference's delta would drag the whole run after whichever of them happened to come
    ///         first. This is the rule <c>GVAR-9</c> in the Consortium's suite exists for, and it
    ///         makes the difference deliberately small — the two references move by 100 and by 99 —
    ///         so that an implementation which takes one of them looks almost right.
    ///     </para>
    /// </remarks>
    static void Interpolate(int from, int to, int first, int second, float[] original, float[] work) {
        if (from > to) {
            return;
        }

        var lower = original[first];
        var upper = original[second];
        var lowerMoved = work[first];
        var upperMoved = work[second];

        if (lower > upper) {
            (lower, upper) = (upper, lower);
            (lowerMoved, upperMoved) = (upperMoved, lowerMoved);
        }

        var below = lowerMoved - lower;
        var above = upperMoved - upper;

        if (lower == upper && below != above) {
            for (var point = from; point <= to; point++) {
                work[point] = original[point];
            }

            return;
        }

        // When the two references share a coordinate every point is on one side or the other, so the
        // scale is never read — and computing it would divide by zero.
        var scale = lower < upper ? (upperMoved - lowerMoved) / (upper - lower) : 0f;

        for (var point = from; point <= to; point++) {
            var value = original[point];

            work[point] = value <= lower ? value + below
                : value >= upper ? value + above
                : lowerMoved + ((value - lower) * scale);
        }
    }

    static void Shift(int from, int to, int reference, float[] original, float[] work) {
        var delta = work[reference] - original[reference];

        if (delta == 0f) {
            return;
        }

        for (var point = from; point <= to; point++) {
            if (point != reference) {
                work[point] = original[point] + delta;
            }
        }
    }

    /// <summary>How much of a tuple's deltas an instance gets: one at its peak, nothing outside it.</summary>
    /// <remarks>
    ///     ⚠ <b>An axis whose peak is zero is skipped, and an instance sitting at zero on an axis the
    ///     tuple does peak on contributes nothing.</b> Those two lines are the difference between "this
    ///     delta is about the weight axis" and "this delta is about the weight axis being at its
    ///     default", and reversing them makes the default instance carry every tuple in the font.
    /// </remarks>
    float Scalar(ImmutableArray<float> coordinates, float[] peak, float[]? start, float[]? end) {
        var scalar = 1f;

        for (var axis = 0; axis < AxisCount; axis++) {
            var at = peak[axis];
            if (at == 0f) {
                continue;
            }

            var coordinate = axis < coordinates.Length ? coordinates[axis] : 0f;
            if (coordinate == 0f) {
                return 0f;
            }

            if (coordinate == at) {
                continue;
            }

            if (start is null || end is null) {
                if (coordinate < MathF.Min(at, 0f) || coordinate > MathF.Max(at, 0f)) {
                    return 0f;
                }

                scalar *= coordinate / at;
                continue;
            }

            if (coordinate <= start[axis] || coordinate >= end[axis]) {
                return 0f;
            }

            scalar *= coordinate < at
                ? (coordinate - start[axis]) / (at - start[axis])
                : (end[axis] - coordinate) / (end[axis] - at);
        }

        return scalar;
    }

    void ReadSharedTuple(int index, float[] peak) {
        Array.Clear(peak);

        if (index >= sharedTupleCount) {
            return;
        }

        var reader = new SfntReader(gvar) { Position = sharedTuplesOffset + (index * AxisCount * 2) };

        for (var axis = 0; axis < AxisCount; axis++) {
            peak[axis] = reader.Has(2) ? reader.F2Dot14() : 0f;
        }
    }

    (int From, int To) Range(int glyph) {
        var reader = new SfntReader(gvar);

        if (longOffsets) {
            reader.Position = OffsetsPosition + (glyph * 4);
            return reader.Has(8) ? ((int)reader.U32(), (int)reader.U32()) : (0, 0);
        }

        // ⚠ The short form stores halves, so it is doubled. A table read as bytes is off by a factor
        // of two everywhere past the first glyph, which reads as one font in four being scrambled.
        reader.Position = OffsetsPosition + (glyph * 2);
        return reader.Has(4) ? (reader.U16() * 2, reader.U16() * 2) : (0, 0);
    }

    /// <summary>Which points a tuple applies to, run-length coded as deltas.</summary>
    /// <returns>The point numbers, or null for "all of them".</returns>
    /// <remarks>
    ///     ⚠ <b>A count of zero means every point, not no points.</b> It is the table's compact
    ///     encoding for the common case, and reading it as an empty set drops the tuple — which is
    ///     what <c>GVAR-1</c> in the Consortium's suite exists to catch, and it is called <i>Sharing
    ///     All Points</i>.
    /// </remarks>
    static int[]? ReadPointNumbers(ref SfntReader reader) {
        if (!reader.Has(1)) {
            return null;
        }

        int count = reader.U8();

        if ((count & 0x80) != 0) {
            if (!reader.Has(1)) {
                return null;
            }

            count = ((count & 0x7F) << 8) | reader.U8();
        }

        if (count == 0) {
            return null;
        }

        var numbers = new int[count];
        var value = 0;
        var at = 0;

        while (at < count && reader.Has(1)) {
            var control = reader.U8();
            var run = (control & 0x7F) + 1;
            var words = (control & 0x80) != 0;

            for (var i = 0; i < run && at < count; i++) {
                if (!reader.Has(words ? 2 : 1)) {
                    return numbers[..at];
                }

                value += words ? reader.U16() : reader.U8();
                numbers[at++] = value;
            }
        }

        return at == count ? numbers : numbers[..at];
    }

    /// <summary>One axis's deltas, run-length coded with a zero run, a byte run and a word run.</summary>
    static float[] ReadPackedDeltas(ref SfntReader reader, int count) {
        var deltas = new float[count];
        var at = 0;

        while (at < count && reader.Has(1)) {
            var control = reader.U8();
            var run = (control & 0x3F) + 1;

            // ⚠ The zero flag is checked first and wins: a control byte with both bits set is a run
            // of zeros with no bytes following it, and reading it as a run of words consumes twice
            // its length out of the next tuple's data.
            if ((control & 0x80) != 0) {
                at = Math.Min(count, at + run);
                continue;
            }

            if ((control & 0x40) != 0) {
                for (var i = 0; i < run && at < count; i++) {
                    if (!reader.Has(2)) {
                        return deltas;
                    }

                    deltas[at++] = reader.S16();
                }

                continue;
            }

            for (var i = 0; i < run && at < count; i++) {
                if (!reader.Has(1)) {
                    return deltas;
                }

                deltas[at++] = reader.S8();
            }
        }

        return deltas;
    }
}

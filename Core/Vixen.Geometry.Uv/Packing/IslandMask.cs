// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Uv.Packing;

/// <summary>One island rasterized to texels, in one orientation, beside the same shape grown by the margin.</summary>
/// <remarks>
///     <para>
///         docs/plan/42 § D7. ⚠ <b>Rasterized masks rather than no-fit polygons, and the reason is
///         rotation.</b> An NFP needs a unique polygon per <i>pair</i> per <i>orientation</i>, so
///         sixteen orientations of a thousand islands is a quarter of a billion polygons. A bitmask
///         overlap test is a word-wise <c>AND</c>, it is trivially parallel, it is trivially
///         deterministic, and it gets <i>more</i> accurate as the atlas grows — which is the direction
///         the problem actually goes.
///     </para>
///     <para>
///         ⚠ <b>The margin is grown into the mask once, here, and never applied again.</b> The grid
///         holds grown shapes and a candidate is tested with its <i>raw</i> one, so the empty gap
///         between any two islands is exactly <c>Margin</c> — not half of it on each side, and not
///         twice it because both of them padded. docs/plan/42 § D8, and the factor-of-two error it
///         describes is the one this arrangement makes unrepresentable.
///     </para>
///     <para>
///         ⚠ <b>Rotation rotates the bitmap rather than re-rasterizing the rotated island.</b> A
///         quarter turn maps the texel grid onto itself, so the rotated mask is exact; re-rasterizing
///         would let a boundary texel appear under one orientation and not another, and the placement
///         would then be describing a shape the coordinates do not have.
///     </para>
/// </remarks>
sealed class IslandMask {
    /// <summary>How far a texel index has to move to cross a word.</summary>
    const int WordShift = 6;

    IslandMask(int width, int height, int margin, ulong[] raw, ulong[] grown, int[] bottom, int[] grownTop, int area) {
        Width = width;
        Height = height;
        Margin = margin;
        Raw = raw;
        Grown = grown;
        Bottom = bottom;
        GrownTop = grownTop;
        Area = area;
        RawStride = StrideOf(width);
        GrownStride = StrideOf(GrownWidth);
    }

    /// <summary>The raw shape's width in texels.</summary>
    public int Width { get; }

    /// <summary>Its height.</summary>
    public int Height { get; }

    /// <summary>How many texels the margin adds on every side.</summary>
    public int Margin { get; }

    /// <summary>The grown shape's width — the raw one plus a margin on each side.</summary>
    public int GrownWidth => Width + (2 * Margin);

    /// <summary>Its height.</summary>
    public int GrownHeight => Height + (2 * Margin);

    /// <summary>The raw shape, one bit per texel, row-major.</summary>
    public ulong[] Raw { get; }

    /// <summary>The same shape grown by <see cref="Margin" /> under the Chebyshev metric.</summary>
    public ulong[] Grown { get; }

    /// <summary>Words per row of <see cref="Raw" />.</summary>
    public int RawStride { get; }

    /// <summary>Words per row of <see cref="Grown" />.</summary>
    public int GrownStride { get; }

    /// <summary>The lowest occupied row per raw column, or <c>-1</c> where the column is empty.</summary>
    public int[] Bottom { get; }

    /// <summary>One past the highest occupied row per grown column, or <c>0</c> where the column is empty.</summary>
    public int[] GrownTop { get; }

    /// <summary>How many texels the raw shape covers. The numerator of the packing efficiency.</summary>
    public int Area { get; }

    /// <summary>A hash of the shape itself, which is what breaks a tie between two equal areas.</summary>
    /// <remarks>
    ///     ⚠ <b>Ordering by area with the input index as the only tie-break is not order-independent,
    ///     and that is a trap rather than a subtlety.</b> Two islands of the same texel area and
    ///     different shapes swap places when the caller hands them over in a different order, and every
    ///     placement after them moves. docs/plan/42 § D7 asks for the same islands to pack the same way;
    ///     a key taken from the shape gets that, and the index stays as the final tie-break for two
    ///     shapes that really are identical — where swapping them changes nothing anybody can see.
    /// </remarks>
    public ulong Signature { get; private set; }

    /// <summary>Words needed for one row of <paramref name="width" /> texels, plus the slack a shifted test reads.</summary>
    /// <param name="width">The row's width in texels.</param>
    /// <returns>The stride, in 64-bit words.</returns>
    public static int StrideOf(int width) => ((width + 63) >> WordShift) + 1;

    /// <summary>Rasterizes an island at a texel scale, conservatively, into one byte per texel.</summary>
    /// <param name="island">The island.</param>
    /// <param name="scale">Texels per island coordinate.</param>
    /// <param name="limit">The largest edge a mask may have, so a nonsense scale cannot allocate the machine.</param>
    /// <param name="width">The grid's width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="clamped">
    ///     Whether <paramref name="limit" /> truncated the island. ⚠ A truncated mask describes a
    ///     shape smaller than the coordinates it stands for, so the attempt that produced it has to
    ///     fail rather than place it.
    /// </param>
    /// <returns>The coverage grid.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Conservative, not centre-sampled.</b> A texel the island touches at all is a texel
    ///         a bake writes to, so a half-covered boundary texel that the mask calls empty is a texel
    ///         the neighbour is allowed to sit on — which is bleeding, arriving at mip 0.
    ///     </para>
    ///     <para>
    ///         An island carrying no triangles rasterizes as its bounding box. That is a defined
    ///         answer rather than an accident: a caller who has bounds and no topology — the remesher's
    ///         patch layout, an importer reading a layout back — can still pack.
    ///     </para>
    /// </remarks>
    public static byte[] Rasterize(
        in UvIsland island,
        float scale,
        int limit,
        out int width,
        out int height,
        out bool clamped
    ) {
        var size = island.Size;

        width = Extent(size.X, scale, limit, out var clampedX);
        height = Extent(size.Y, scale, limit, out var clampedY);
        clamped = clampedX || clampedY;

        var coverage = new byte[width * height];
        var coordinates = island.Coordinates;
        var triangles = coordinates is null ? 0 : coordinates.Count / 3;

        if (triangles == 0) {
            Array.Fill(coverage, (byte)1);

            return coverage;
        }

        for (var triangle = 0; triangle < triangles; triangle++) {
            var a = (coordinates![(triangle * 3) + 0] - island.Minimum) * scale;
            var b = (coordinates[(triangle * 3) + 1] - island.Minimum) * scale;
            var c = (coordinates[(triangle * 3) + 2] - island.Minimum) * scale;

            Fill(coverage, width, height, a, b, c);
        }

        return coverage;
    }

    /// <summary>Builds a mask from a coverage grid, growing the margin into it.</summary>
    /// <param name="coverage">One byte per texel, non-zero where the island is.</param>
    /// <param name="width">The grid's width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="margin">How many texels to grow by.</param>
    /// <returns>The mask.</returns>
    public static IslandMask Build(byte[] coverage, int width, int height, int margin) {
        var area = 0;

        for (var index = 0; index < coverage.Length; index++) {
            if (coverage[index] != 0) {
                area++;
            }
        }

        // A shape with no texels at all still has to be somewhere, or the placement it gets back is a
        // lie about where its coordinates went. One texel is the smallest honest answer.
        if (area == 0) {
            coverage[0] = 1;
            area = 1;
        }

        var grown = Grow(coverage, width, height, margin);
        var grownWidth = width + (2 * margin);
        var grownHeight = height + (2 * margin);
        var bottom = new int[width];
        var grownTop = new int[grownWidth];

        for (var x = 0; x < width; x++) {
            bottom[x] = -1;

            for (var y = 0; y < height; y++) {
                if (coverage[(y * width) + x] != 0) {
                    bottom[x] = y;

                    break;
                }
            }
        }

        for (var x = 0; x < grownWidth; x++) {
            for (var y = grownHeight - 1; y >= 0; y--) {
                if (grown[(y * grownWidth) + x] != 0) {
                    grownTop[x] = y + 1;

                    break;
                }
            }
        }

        var raw = Pack(coverage, width, height);
        var mask = new IslandMask(
            width,
            height,
            margin,
            raw,
            Pack(grown, grownWidth, grownHeight),
            bottom,
            grownTop,
            area
        );

        var signature = 14695981039346656037UL;

        foreach (var word in (ulong[])[(ulong)width, (ulong)height, .. raw]) {
            signature = (signature ^ word) * 1099511628211UL;
        }

        mask.Signature = signature;

        return mask;
    }

    /// <summary>Turns the mask a quarter turn counter-clockwise, exactly.</summary>
    /// <param name="coverage">The coverage grid to turn.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The turned grid, whose width is <paramref name="height" />.</returns>
    public static byte[] Turn(byte[] coverage, int width, int height) {
        var turned = new byte[coverage.Length];

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                // (x, y) → (h - 1 - y, x), which is the discrete form of the coordinate turn
                // `UvPlacement.Apply` performs. The two have to agree or the mask describes a shape
                // the coordinates never take.
                turned[(x * height) + (height - 1 - y)] = coverage[(y * width) + x];
            }
        }

        return turned;
    }

    /// <summary>Unpacks a mask back to one byte per texel, which is what a turn and a blit work on.</summary>
    /// <returns>The coverage grid.</returns>
    public byte[] ToCoverage() {
        var coverage = new byte[Width * Height];

        for (var y = 0; y < Height; y++) {
            for (var x = 0; x < Width; x++) {
                var word = Raw[(y * RawStride) + (x >> WordShift)];

                if ((word & (1UL << (x & 63))) != 0) {
                    coverage[(y * Width) + x] = 1;
                }
            }
        }

        return coverage;
    }

    static int Extent(float size, float scale, int limit, out bool clamped) {
        var texels = size * scale;

        clamped = false;

        if (!float.IsFinite(texels) || texels <= 0f) {
            return 1;
        }

        var edge = MathF.Ceiling(texels);

        if (edge > limit) {
            clamped = true;

            return limit;
        }

        return Math.Max(1, (int)edge);
    }

    static void Fill(byte[] coverage, int width, int height, Vector2 a, Vector2 b, Vector2 c) {
        var twice = ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

        if (!float.IsFinite(twice)) {
            return;
        }

        // A zero-area triangle covers no texels and still has to stop the neighbour sitting on it, so
        // it marks the texels its corners land in rather than being dropped. docs/plan/42 § B5: the
        // generated input this runs on is full of them.
        if (twice == 0f) {
            Mark(coverage, width, height, a);
            Mark(coverage, width, height, b);
            Mark(coverage, width, height, c);

            return;
        }

        if (twice < 0f) {
            (b, c) = (c, b);
        }

        var minimumX = Math.Clamp((int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))), 0, width - 1);
        var maximumX = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))), 0, width - 1);
        var minimumY = Math.Clamp((int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))), 0, height - 1);
        var maximumY = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))), 0, height - 1);

        // Each edge function is widened by half the texel's projection onto its normal, which is what
        // makes the test conservative: a texel whose *square* meets the triangle passes, not only one
        // whose centre does.
        var biasA = 0.5f * (MathF.Abs(b.X - a.X) + MathF.Abs(b.Y - a.Y));
        var biasB = 0.5f * (MathF.Abs(c.X - b.X) + MathF.Abs(c.Y - b.Y));
        var biasC = 0.5f * (MathF.Abs(a.X - c.X) + MathF.Abs(a.Y - c.Y));

        for (var y = minimumY; y <= maximumY; y++) {
            var centreY = y + 0.5f;

            for (var x = minimumX; x <= maximumX; x++) {
                var centreX = x + 0.5f;

                var edgeA = ((b.X - a.X) * (centreY - a.Y)) - ((b.Y - a.Y) * (centreX - a.X));

                if (edgeA + biasA < 0f) {
                    continue;
                }

                var edgeB = ((c.X - b.X) * (centreY - b.Y)) - ((c.Y - b.Y) * (centreX - b.X));

                if (edgeB + biasB < 0f) {
                    continue;
                }

                var edgeC = ((a.X - c.X) * (centreY - c.Y)) - ((a.Y - c.Y) * (centreX - c.X));

                if (edgeC + biasC < 0f) {
                    continue;
                }

                coverage[(y * width) + x] = 1;
            }
        }
    }

    static void Mark(byte[] coverage, int width, int height, Vector2 point) {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) {
            return;
        }

        var x = Math.Clamp((int)MathF.Floor(point.X), 0, width - 1);
        var y = Math.Clamp((int)MathF.Floor(point.Y), 0, height - 1);

        coverage[(y * width) + x] = 1;
    }

    /// <summary>Chebyshev dilation, separably, because a filter footprint is a box and not a disc.</summary>
    static byte[] Grow(byte[] coverage, int width, int height, int margin) {
        var grownWidth = width + (2 * margin);
        var grownHeight = height + (2 * margin);
        var grown = new byte[grownWidth * grownHeight];

        if (margin == 0) {
            Array.Copy(coverage, grown, coverage.Length);

            return grown;
        }

        var window = (2 * margin) + 1;
        var horizontal = new byte[grownWidth * height];

        for (var y = 0; y < height; y++) {
            var count = 0;

            for (var x = 0; x < grownWidth; x++) {
                // The window ending at x covers source columns [x - 2m, x], which is exactly the set
                // within Chebyshev distance m of the source column x - m.
                if (x < width && coverage[(y * width) + x] != 0) {
                    count++;
                }

                var leaving = x - window;

                if (leaving >= 0 && leaving < width && coverage[(y * width) + leaving] != 0) {
                    count--;
                }

                if (count > 0) {
                    horizontal[(y * grownWidth) + x] = 1;
                }
            }
        }

        for (var x = 0; x < grownWidth; x++) {
            var count = 0;

            for (var y = 0; y < grownHeight; y++) {
                if (y < height && horizontal[(y * grownWidth) + x] != 0) {
                    count++;
                }

                var leaving = y - window;

                if (leaving >= 0 && leaving < height && horizontal[(leaving * grownWidth) + x] != 0) {
                    count--;
                }

                if (count > 0) {
                    grown[(y * grownWidth) + x] = 1;
                }
            }
        }

        return grown;
    }

    static ulong[] Pack(byte[] coverage, int width, int height) {
        var stride = StrideOf(width);
        var bits = new ulong[stride * height];

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                if (coverage[(y * width) + x] != 0) {
                    bits[(y * stride) + (x >> WordShift)] |= 1UL << (x & 63);
                }
            }
        }

        return bits;
    }
}

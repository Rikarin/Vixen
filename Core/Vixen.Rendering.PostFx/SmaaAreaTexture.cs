// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Rendering.PostFx;

/// <summary>
///     SMAA's coverage table: how much of a pixel a reconstructed silhouette covers, for every
///     pattern and every pair of distances the blending-weight pass can find.
/// </summary>
/// <remarks>
///     <para>
///         <strong>Generated, not shipped.</strong> The reference SMAA distribution carries this as a
///         179 KB byte array in a header, which is a binary blob in source control that nobody can
///         review and nothing can regenerate. It is an <em>analytic</em> function — the area under a
///         straight line, clipped to one pixel column — so it is written here as the arithmetic that
///         produces it, and <c>SmaaAreaTextureTests</c> pins the values a pencil can check.
///     </para>
///     <para>
///         <strong>What the table is.</strong> The weight pass finds a run of edge texels along one
///         boundary, measures how far the run extends to each side of the pixel it is shading, and
///         reads the crossing edges at the two ends. Those two crossings — four bits, since each end
///         may be crossed above the line, below it, both or neither — say which silhouette the
///         staircase came from, and the two distances say where in that silhouette this pixel sits.
///         The table holds the answer for all sixteen patterns.
///     </para>
///     <para>
///         <strong>The layout is the reference's, minus what SMAA 1x cannot use.</strong> Sixteen
///         patterns are placed in a 5×5 grid of <see cref="MaxDistance" />-square blocks — five slots
///         per axis because the pass indexes them with <c>3·below + above</c>, which takes the values
///         0, 1, 3 and 4 and never 2. The reference texture is 160 wide because its right half holds
///         the diagonal patterns, and 560 tall because it stacks seven sub-sample offsets for SMAA
///         S2x and 4x. This engine has neither: diagonal detection is not implemented and there is no
///         multi-sample resolve to offset for, so those 83 200 texels would be zeroes with nothing to
///         read them. What is generated is the 80×80 that SMAA 1x addresses.
///     </para>
///     <para>
///         ⚠ <strong>The distance axis is quadratic.</strong> Texel <c>i</c> holds the answer for a
///         run reaching <c>i²</c> pixels, and the shader indexes it with <c>sqrt(d)</c>. That is the
///         reference's compression and it is worth keeping: resolution matters at a distance of one
///         or two, where the coverage changes fastest, and not at sixteen, where it barely changes at
///         all.
///     </para>
/// </remarks>
public static class SmaaAreaTexture {
    /// <summary>How many texels one pattern's block spans on each axis.</summary>
    public const int MaxDistance = 16;

    /// <summary>How many pattern slots there are per axis.</summary>
    /// <remarks>
    ///     Five, and only four of them are ever addressed. The weight pass forms the index as
    ///     <c>3·below + above</c> from two bits, which yields 0, 1, 3 and 4 — the reference's own
    ///     arithmetic, kept so that a shader written against SMAA's published constants reads this
    ///     table correctly.
    /// </remarks>
    public const int Patterns = 5;

    /// <summary>The table's width and height in texels.</summary>
    public const int Side = MaxDistance * Patterns;

    /// <summary>How many bytes one texel is. Two: coverage on each side of the line.</summary>
    public const int BytesPerTexel = 2;

    /// <summary>The whole table's size in bytes.</summary>
    public const int ByteCount = Side * Side * BytesPerTexel;

    /// <summary>
    ///     The run length past which a U-shape is no longer smoothed.
    /// </summary>
    /// <remarks>
    ///     A short run bounded at both ends is a bump rather than a staircase, and blending it with
    ///     the plain coverage leaves a visible notch. The square root pulls the coverage toward a
    ///     rounder profile, faded out over this distance so that a long run — which really is a
    ///     staircase — keeps the exact area.
    /// </remarks>
    public const double SmoothingMaxDistance = 32.0;

    /// <summary>
    ///     The table, RG8, row-major from the top: red is the coverage below the line, green above.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         "Below" is the side the pixel being shaded is on — the pixel that owns the edge sits
    ///         under the horizontal boundary it found — so red is how much of it belongs to the
    ///         surface on the other side, which is exactly how far it should reach across to sample.
    ///         Green is the same number for the pixel on the far side, which reads it from here.
    ///     </para>
    ///     <para>
    ///         The x axis is the distance to the run's left end and the y axis the distance to its
    ///         right, both quadratically compressed; the block a texel lives in is the pattern.
    ///     </para>
    /// </remarks>
    public static byte[] Generate() {
        var texels = new byte[ByteCount];

        for (var pattern = 0; pattern < 16; pattern++) {
            var (blockX, blockY) = Block(pattern);

            for (var y = 0; y < MaxDistance; y++) {
                for (var x = 0; x < MaxDistance; x++) {
                    // Quadratic: the shader indexes with sqrt(distance), so texel i is distance i².
                    var (below, above) = Coverage(pattern, x * x, y * y);

                    var at = BytesPerTexel
                        * ((((blockY * MaxDistance) + y) * Side) + (blockX * MaxDistance) + x);

                    texels[at] = Quantise(below);
                    texels[at + 1] = Quantise(above);
                }
            }
        }

        return texels;
    }

    /// <summary>Where a pattern's block sits in the 5×5 grid.</summary>
    /// <remarks>
    ///     The four bits are: 1 crossed below the line at the left end, 2 crossed below at the right
    ///     end, 4 crossed above at the left, 8 crossed above at the right. The left end's two bits
    ///     make the x block and the right end's make the y block, each as <c>3·below + above</c> —
    ///     which is what the weight pass computes, and why slot 2 is never used.
    /// </remarks>
    public static (int X, int Y) Block(int pattern) {
        ArgumentOutOfRangeException.ThrowIfNegative(pattern);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pattern, 15);

        return ((3 * (pattern & 1)) + ((pattern >> 2) & 1), (3 * ((pattern >> 1) & 1)) + ((pattern >> 3) & 1));
    }

    /// <summary>
    ///     The coverage the pixel at <paramref name="left" /> gets from one pattern, in a run that
    ///     reaches <paramref name="left" /> pixels to its left and <paramref name="right" /> to its
    ///     right.
    /// </summary>
    /// <param name="pattern">Which of the sixteen crossing-edge combinations was found.</param>
    /// <param name="left">How far the run reaches left of the pixel, in pixels.</param>
    /// <param name="right">How far it reaches right, in pixels.</param>
    /// <returns>The area below the reconstructed line, and the area above it.</returns>
    /// <remarks>
    ///     <para>
    ///         The run is laid out along <c>x</c> from 0 to <c>left + right + 1</c> with the pixel
    ///         being shaded occupying <c>[left, left + 1]</c>, and the boundary the edge sits on is
    ///         <c>y = 0</c>. A crossing below the line at one end means the silhouette leaves the
    ///         boundary and dives to that pixel's centre, half a pixel down; a crossing above means it
    ///         rises half a pixel.
    ///     </para>
    ///     <para>
    ///         ⚠ <strong>A pattern crossed both above and below at the same end contributes
    ///         nothing.</strong> Two crossings at one end is a T or a cross junction, and there is no
    ///         single line through it — blending one in is how a morphological filter puts a smear
    ///         across a corner. Seven of the sixteen patterns are that shape.
    ///     </para>
    /// </remarks>
    public static (double Below, double Above) Coverage(int pattern, double left, double right) {
        ArgumentOutOfRangeException.ThrowIfNegative(pattern);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pattern, 15);

        var d = left + right + 1.0;

        // Where the silhouette lands when it leaves the boundary: the centre of the pixel above it,
        // or of the one below.
        const double Above = 0.5;
        const double Below = -0.5;

        switch (pattern) {
            case 0:
                //  ------
                return (0.0, 0.0);

            case 1:
                // .------
                // |
                //
                // Only the half of the run the crossing is on: an L is filtered on its own side, so
                // that it converges with the unfiltered pattern 0 rather than stepping away from it.
                return left <= right ? Area((0.0, Below), (d / 2.0, 0.0), left) : (0.0, 0.0);

            case 2:
                //  ------.
                //        |
                return left >= right ? Area((d / 2.0, 0.0), (d, Below), left) : (0.0, 0.0);

            case 3:
                // .------.
                // |      |
                return Ushape(d, Area((0.0, Below), (d / 2.0, 0.0), left), Area((d / 2.0, 0.0), (d, Below), left));

            case 4:
                // |
                // `------
                return left <= right ? Area((0.0, Above), (d / 2.0, 0.0), left) : (0.0, 0.0);

            case 6:
                // |
                // `------.
                //        |
                //
                // A Z: one line the whole length of the run, from one pixel centre to the other.
                return Area((0.0, Above), (d, Below), left);

            case 8:
                //        |
                //  ------´
                return left >= right ? Area((d / 2.0, 0.0), (d, Above), left) : (0.0, 0.0);

            case 9:
                // .------´
                // |
                return Area((0.0, Below), (d, Above), left);

            case 12:
                // |      |
                // `------´
                return Ushape(d, Area((0.0, Above), (d / 2.0, 0.0), left), Area((d / 2.0, 0.0), (d, Above), left));

            // 5, 7, 10, 11, 13, 14 and 15 all have both crossings at one end. See the remarks.
            default:
                return (0.0, 0.0);
        }
    }

    /// <summary>The two halves of a U, blended toward a rounder profile while the U is short.</summary>
    static (double Below, double Above) Ushape(double d, (double Below, double Above) a, (double Below, double Above) b) {
        var blend = Math.Clamp(d / SmoothingMaxDistance, 0.0, 1.0);

        return (
            Smooth(a.Below, blend) + Smooth(b.Below, blend),
            Smooth(a.Above, blend) + Smooth(b.Above, blend)
        );
    }

    static double Smooth(double area, double blend) => Lerp(Math.Sqrt(area * 2.0) * 0.5, area, blend);

    static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    /// <summary>
    ///     The area between the line <paramref name="p1" />–<paramref name="p2" /> and <c>y = 0</c>,
    ///     over the pixel column <c>[x, x + 1]</c>, split by which side of the boundary it is on.
    /// </summary>
    /// <remarks>
    ///     Three cases. Outside the line's span there is nothing. Inside, if the line stays on one
    ///     side of the boundary across the column the region is a trapezoid and its area is the mean
    ///     of the two heights. If it crosses, the region is two triangles on opposite sides, and both
    ///     are returned — which is the case that makes this two numbers rather than one.
    /// </remarks>
    static (double Below, double Above) Area((double X, double Y) p1, (double X, double Y) p2, double x) {
        var dx = p2.X - p1.X;
        var dy = p2.Y - p1.Y;

        var x1 = x;
        var x2 = x + 1.0;

        var y1 = p1.Y + (dy * (x1 - p1.X) / dx);
        var y2 = p1.Y + (dy * (x2 - p1.X) / dx);

        var inside = (x1 >= p1.X && x1 < p2.X) || (x2 > p1.X && x2 <= p2.X);

        if (!inside) {
            return (0.0, 0.0);
        }

        // A crossing this shallow is the line touching the boundary rather than passing through it,
        // and splitting it into two triangles there divides by something near zero.
        const double Flat = 1e-4;

        if (Math.Sign(y1) == Math.Sign(y2) || Math.Abs(y1) < Flat || Math.Abs(y2) < Flat) {
            var mean = (y1 + y2) / 2.0;
            return mean < 0.0 ? (Math.Abs(mean), 0.0) : (0.0, Math.Abs(mean));
        }

        var crossing = p1.X - (p1.Y * dx / dy);

        var a1 = crossing > p1.X ? y1 * (crossing - x1) / 2.0 : 0.0;
        var a2 = crossing < p2.X ? y2 * (x2 - crossing) / 2.0 : 0.0;

        // The first triangle is the one on the y1 side, so which of the two is "below" is decided by
        // y1's sign.
        return y1 < 0.0 ? (Math.Abs(a1), Math.Abs(a2)) : (Math.Abs(a2), Math.Abs(a1));
    }

    /// <summary>One coverage as an eight-bit unorm.</summary>
    /// <remarks>
    ///     Coverage never exceeds a half — the line leaves the boundary by half a pixel at most — so
    ///     the top half of the range is unreachable and that is fine: what the shader does with the
    ///     number is offset a bilinear tap by it, and an offset above half a texel would reach past
    ///     the neighbour it is blending with.
    /// </remarks>
    static byte Quantise(double area) => (byte)Math.Clamp(Math.Round(area * 255.0), 0.0, 255.0);
}

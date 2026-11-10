// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.TextureGraph.Drawing;

/// <summary>A rectangle of texels a shape is filled into before it is laid onto the picture.</summary>
/// <param name="Left">Its left column. May be outside the picture on an axis that wraps.</param>
/// <param name="Top">Its top row. The same.</param>
/// <param name="Width">How many columns.</param>
/// <param name="Height">How many rows.</param>
internal readonly record struct TextureBox(int Left, int Top, int Width, int Height);

/// <summary>The picture a drawing is laid onto: its size, and whether each axis wraps.</summary>
/// <param name="Width">Its width in texels.</param>
/// <param name="Height">Its height.</param>
/// <param name="WrapX">Whether ink past the left or right edge comes back in at the other.</param>
/// <param name="WrapY">The same, top and bottom.</param>
/// <remarks>
///     <para>
///         <b>Every shape is filled into a box its own size and then laid on</b>, because the scanline fill walks
///         every row of the bitmap it is given against every edge of the outline: filling a 4K picture once per
///         letter or per path would be the drawing's length times the picture's area.
///     </para>
///     <para>
///         ⚠ <b>On an axis that wraps, a box is folded rather than clipped</b>, so ink that overhangs one edge comes
///         back in at the other and a graph that tiles still tiles with a word across its seam. The plan says
///         whether it wraps (<c>TexturePlan.WrapX</c>), so the drawing does as every kernel does.
///     </para>
/// </remarks>
internal readonly record struct TextureCanvas(int Width, int Height, bool WrapX, bool WrapY) {
    /// <summary>The box a shape with these bounds is filled into, or null when none of it lands on the picture.</summary>
    /// <param name="left">Its left edge, in texels.</param>
    /// <param name="top">Its top edge.</param>
    /// <param name="right">Its right edge.</param>
    /// <param name="bottom">Its bottom edge.</param>
    /// <param name="pad">Texels of margin on every side, for a curve's extreme a sampled bound can miss.</param>
    /// <returns>The box.</returns>
    public TextureBox? Box(float left, float top, float right, float bottom, int pad = 1) {
        if (!(float.IsFinite(left) && float.IsFinite(top) && float.IsFinite(right) && float.IsFinite(bottom))) {
            return null;
        }

        var (x0, x1) = Span(left, right, Width, WrapX, pad);
        var (y0, y1) = Span(top, bottom, Height, WrapY, pad);

        return x1 > x0 && y1 > y0 ? new TextureBox(x0, y0, x1 - x0, y1 - y0) : null;
    }

    /// <summary>Lays a coverage box onto a coverage picture, keeping the larger of the two at every texel.</summary>
    /// <param name="coverage">The picture, row-major, top row first.</param>
    /// <param name="box">Where the box goes.</param>
    /// <param name="values">The box's coverage, row-major.</param>
    public void Union(float[] coverage, TextureBox box, float[] values) {
        for (var row = 0; row < box.Height; row++) {
            var y = Fold(box.Top + row, Height);

            for (var column = 0; column < box.Width; column++) {
                var at = (y * Width) + Fold(box.Left + column, Width);

                coverage[at] = Math.Max(coverage[at], values[(row * box.Width) + column]);
            }
        }
    }

    /// <summary>Lays one colour over a premultiplied picture through a coverage box.</summary>
    /// <param name="picture">Four floats per texel, premultiplied, row-major.</param>
    /// <param name="box">Where the box goes.</param>
    /// <param name="values">The box's coverage, row-major.</param>
    /// <param name="red">The colour's red, as written in the file.</param>
    /// <param name="green">Its green.</param>
    /// <param name="blue">Its blue.</param>
    /// <param name="opacity">How much of it a fully covered texel takes.</param>
    public void Over(float[] picture, TextureBox box, float[] values, float red, float green, float blue, float opacity) {
        for (var row = 0; row < box.Height; row++) {
            var y = Fold(box.Top + row, Height);

            for (var column = 0; column < box.Width; column++) {
                var alpha = values[(row * box.Width) + column] * opacity;

                if (alpha <= 0f) {
                    continue;
                }

                var at = ((y * Width) + Fold(box.Left + column, Width)) * 4;
                var keep = 1f - alpha;

                picture[at] = (red * alpha) + (picture[at] * keep);
                picture[at + 1] = (green * alpha) + (picture[at + 1] * keep);
                picture[at + 2] = (blue * alpha) + (picture[at + 2] * keep);
                picture[at + 3] = alpha + (picture[at + 3] * keep);
            }
        }
    }

    /// <summary>One axis of a box: clipped to the picture, or, wrapping, no wider than it.</summary>
    /// <remarks>
    ///     ⚠ <b>On a wrapping axis the margin is what gives way</b>, not the shape: a shape exactly as wide as the
    ///     picture plus a margin each side would otherwise be cut to the picture's width from its padded left edge,
    ///     and lose its last row — which is what a rectangle filling the frame of a tiling graph did.
    /// </remarks>
    static (int From, int To) Span(float from, float to, int extent, bool wraps, int pad) {
        var low = (int)MathF.Floor(Math.Clamp(Math.Min(from, to), -1e7f, 1e7f));
        var high = (int)MathF.Ceiling(Math.Clamp(Math.Max(from, to), -1e7f, 1e7f));

        if (wraps) {
            var margin = Math.Min(pad, Math.Max(extent - (high - low), 0) / 2);

            low -= margin;
            high += margin;

            // Wider than the picture folds onto itself; one picture's width of it is all there is to see.
            return (low, Math.Min(high, low + extent));
        }

        return (Math.Max(low - pad, 0), Math.Min(high + pad, extent));
    }

    static int Fold(int at, int extent) => ((at % extent) + extent) % extent;
}

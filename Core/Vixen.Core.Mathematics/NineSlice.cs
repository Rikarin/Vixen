// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Core.Mathematics;

/// <summary>
///     Four inset edges, and the cut of a rectangle into the nine they describe.
/// </summary>
/// <remarks>
///     <para>
///         <b>A nine-slice is rectangle arithmetic, which is why it is here and not beside either of
///         its callers.</b> Two consumers need exactly the same nine pairs of rectangles — a user
///         interface stretching a panel's background and a renderer stretching a sprite — and they
///         cannot reference each other: <c>Vixen.Ui</c> describes a frame without a device and
///         <c>Vixen.Rendering</c> draws without knowing what an element tree is. The alternative was
///         the same twenty lines in both, which is two places for the corner convention to disagree.
///     </para>
///     <para>
///         ⚠ <b>Unitless on purpose.</b> The same four numbers are texels when they cut a texture
///         region, document pixels when they cut a destination box, and zero-to-one when they cut a
///         UV rectangle — and <see cref="Scaled" /> is what moves between them. A type that named its
///         unit would need three of itself, and the split is identical in all three.
///     </para>
///     <para>
///         The order is CSS's: left, top, right, bottom is not what a reader expects, but it is what
///         <c>border-image-slice</c>, <c>Thickness</c> and every stylesheet in the repository already
///         write, and a fifth ordering in the same codebase is a mistake nobody will see in a
///         diff — the numbers are all floats and swapping two of them still compiles.
///     </para>
/// </remarks>
/// <param name="Left">How far the left column reaches in from the left edge.</param>
/// <param name="Top">How far the top row reaches down from the top edge.</param>
/// <param name="Right">How far the right column reaches in from the right edge.</param>
/// <param name="Bottom">How far the bottom row reaches up from the bottom edge.</param>
[DataContract]
[StructLayout(LayoutKind.Sequential)]
public readonly record struct NineSlice(float Left, float Top, float Right, float Bottom) {
    /// <summary>How many cells <see cref="Split" /> writes.</summary>
    public const int CellCount = 9;

    /// <summary>Where the middle cell lands in a split, for a caller that draws it separately.</summary>
    /// <remarks>
    ///     The cells are row-major from the top left, so 0, 2, 6 and 8 are the corners, 1, 3, 5 and 7
    ///     are the edges, and this is what is between them. Named because "the fifth rectangle" is
    ///     the kind of number a reader has to re-derive and a writer can get wrong by one.
    /// </remarks>
    public const int Centre = 4;

    /// <summary>No inset at all: one cell covering the whole rectangle.</summary>
    public static NineSlice None => default;

    /// <summary>The same inset on all four edges.</summary>
    /// <param name="amount">How far in.</param>
    public static NineSlice Uniform(float amount) => new(amount, amount, amount, amount);

    /// <summary>Whether there is nothing to cut, so the rectangle is its own single cell.</summary>
    /// <remarks>
    ///     Negative counts as nothing rather than as an error, for the reason <see cref="Split" />
    ///     clamps: an inset comes from authored data often enough that refusing one would turn a
    ///     mistyped sprite into an exception in the frame path.
    /// </remarks>
    public bool IsEmpty => Left <= 0f && Top <= 0f && Right <= 0f && Bottom <= 0f;

    /// <summary>What the two columns of border take up together.</summary>
    public float Horizontal => MathF.Max(Left, 0f) + MathF.Max(Right, 0f);

    /// <summary>What the two rows of border take up together.</summary>
    public float Vertical => MathF.Max(Top, 0f) + MathF.Max(Bottom, 0f);

    /// <summary>The same inset measured in different units.</summary>
    /// <param name="x">What to multiply the horizontal edges by.</param>
    /// <param name="y">What to multiply the vertical edges by.</param>
    /// <returns>The scaled inset.</returns>
    /// <remarks>
    ///     What turns a border authored in texels into one in UVs — the two axes scale by different
    ///     amounts, because a texture is not square and its two reciprocals are not the same number.
    /// </remarks>
    public NineSlice Scaled(float x, float y) => new(Left * x, Top * y, Right * x, Bottom * y);

    /// <summary>
    ///     The inset shrunk until its borders fit inside a box, preserving their proportions.
    /// </summary>
    /// <param name="width">The box's width.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The inset, or a scaled-down copy of it when the borders would not fit.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One factor for both axes, not one per axis</b> — which is CSS's
    ///         <c>border-image-width</c> rule and not an approximation of it. Scaling the axes
    ///         independently keeps each pair of borders inside the box and squashes the corners into
    ///         a different aspect ratio than they were drawn at, which is the one thing a nine-slice
    ///         exists to prevent: the whole arrangement is a promise that a corner never distorts.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The destination is fitted and the source never is.</b> A button drawn narrower
    ///         than its own corners should show its corner art compressed, not a different part of
    ///         the texture — so this is applied to the box being filled, and the region being read
    ///         from is split by the inset as authored. Fitting both would leave the corners
    ///         undistorted and quietly change which texels they are.
    ///     </para>
    /// </remarks>
    public NineSlice Fit(float width, float height) {
        var horizontal = Horizontal;
        var vertical = Vertical;

        var scale = 1f;

        if (horizontal > width && horizontal > 0f) {
            scale = MathF.Min(scale, MathF.Max(width, 0f) / horizontal);
        }

        if (vertical > height && vertical > 0f) {
            scale = MathF.Min(scale, MathF.Max(height, 0f) / vertical);
        }

        return scale >= 1f ? this : Scaled(scale, scale);
    }

    /// <summary>Cuts a rectangle into the nine this inset describes.</summary>
    /// <param name="box">What to cut.</param>
    /// <param name="into">
    ///     Where the cells go, row-major from the top left. At least <see cref="CellCount" /> long.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="into" /> is too short.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Nine rectangles that tile the box exactly</b>, which is the property everything
    ///         downstream leans on: adjacent cells share an edge to the bit, so a stretched panel has
    ///         no seam between its corner and the edge beside it. That is why the columns are computed
    ///         as three positions and differenced, rather than as three widths that are summed —
    ///         summing accumulates a rounding error that shows up as a hairline at the right edge.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Cells may be empty and are not dropped.</b> An inset with no top border makes the
    ///         first row zero-high and the count is still nine, because a caller reads cell 4 for the
    ///         middle and cell 8 for the bottom-right corner — compacting the empties would move
    ///         them. Whoever emits geometry skips the empty ones; whoever indexes them does not have
    ///         to know which are empty.
    ///     </para>
    ///     <para>
    ///         Negative edges are clamped to zero, and edges that overflow the box are <i>not</i>
    ///         clamped here — <see cref="Fit" /> is what does that, and it is a separate call because
    ///         it is the destination's answer and not the source's.
    ///     </para>
    /// </remarks>
    public void Split(Rectangle box, Span<Rectangle> into) {
        if (into.Length < CellCount) {
            throw new ArgumentException(
                $"A nine-slice writes {CellCount} cells, so the span has to be at least that long — not {into.Length}.",
                nameof(into)
            );
        }

        var left = MathF.Max(Left, 0f);
        var top = MathF.Max(Top, 0f);
        var right = MathF.Max(Right, 0f);
        var bottom = MathF.Max(Bottom, 0f);

        Span<float> columns = [box.X, box.X + left, box.X + box.Width - right, box.X + box.Width];
        Span<float> rows = [box.Y, box.Y + top, box.Y + box.Height - bottom, box.Y + box.Height];

        for (var row = 0; row < 3; row++) {
            for (var column = 0; column < 3; column++) {
                into[(row * 3) + column] = new(
                    columns[column],
                    rows[row],
                    MathF.Max(columns[column + 1] - columns[column], 0f),
                    MathF.Max(rows[row + 1] - rows[row], 0f)
                );
            }
        }
    }

}

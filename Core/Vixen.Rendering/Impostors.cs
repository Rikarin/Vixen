// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering;

/// <summary>One view of a baked mesh: which cell of the grid, and where it is in the atlas.</summary>
/// <param name="X">The cell's column.</param>
/// <param name="Z">Its row.</param>
public readonly record struct ImpostorCell(int X, int Z);

/// <summary>How much of a cell a direction takes, when three of them are blended.</summary>
/// <param name="Cell">Which cell.</param>
/// <param name="Weight">How much of it, 0…1. The three weights sum to one.</param>
public readonly record struct ImpostorSample(ImpostorCell Cell, float Weight);

/// <summary>
///     The grid of directions a mesh is photographed from, folded onto a hemi-octahedron.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T7], and [docs/plan/06]'s "impostors / billboards" row for its only real
///         consumer.</b> A tree seen from four hundred metres is a few pixels of silhouette, and
///         drawing forty thousand triangles for it is the cost the far field is made of. An impostor
///         is that tree photographed from a grid of directions once, offline, and drawn as two
///         triangles for ever after.
///     </para>
///     <para>
///         ⚠ <b>A <em>hemi</em>-octahedron, and it is a different fold rather than half of
///         <see cref="ScreenProbes.OctahedralMap" />'s.</b> Nobody looks at a tree from underneath —
///         and a full-sphere grid spends half its atlas on the views a forest never shows, which at
///         the resolutions an impostor is worth having is the difference between an eight-by-eight
///         grid and a twelve-by-twelve one. The full-sphere fold exists, is used for probe radiance,
///         and is not this.
///     </para>
///     <para>
///         ⚠ <b>Three cells are blended, not one.</b> Snapping to the nearest view makes the
///         impostor rotate in visible steps as the camera moves — the classic impostor artefact, and
///         it is worse for a forest than for one object because every tree pops on a different frame.
///         The three that share the direction's triangle sum to one, so the blend is continuous
///         everywhere including across a cell boundary.
///     </para>
///     <para>
///         ⚠ <b>The grid is odd-sided so a cell sits exactly overhead.</b> Straight down is the
///         direction a top-down view spends its whole time in, and an even grid puts a seam there.
///     </para>
/// </remarks>
public readonly record struct ImpostorGrid {
    /// <summary>The fewest cells a side that is still a grid.</summary>
    public const int MinimumSide = 2;

    /// <summary>The most, so an atlas of any sane resolution has texels left per cell.</summary>
    public const int MaximumSide = 32;

    /// <summary>Creates a grid.</summary>
    /// <param name="side">How many cells along each axis.</param>
    /// <exception cref="ArgumentOutOfRangeException">The side is outside the supported range.</exception>
    public ImpostorGrid(int side = 9) {
        ArgumentOutOfRangeException.ThrowIfLessThan(side, MinimumSide);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(side, MaximumSide);

        Side = side;
    }

    /// <summary>How many cells along each axis.</summary>
    public int Side { get; }

    /// <summary>How many views the bake renders.</summary>
    public int CellCount => Side * Side;

    /// <summary>Folds a direction into the unit square.</summary>
    /// <param name="direction">Which way the camera is looking <em>from</em>. Need not be normalised.</param>
    /// <returns>A point in [0, 1]².</returns>
    /// <remarks>
    ///     ⚠ <b>The absolute value of Y, not a clamp.</b> A direction from below is folded onto its
    ///     mirror rather than pinned to the horizon: a camera that dips a degree under a hillside
    ///     tree keeps the view it had, where a clamp would slide it round the equator.
    /// </remarks>
    public static Vector2 Encode(Vector3 direction) {
        var scale = MathF.Abs(direction.X) + MathF.Abs(direction.Y) + MathF.Abs(direction.Z);

        if (!(scale > 0f)) {
            return new(0.5f, 0.5f);
        }

        var n = direction / scale;

        return new(((n.X + n.Z) * 0.5f) + 0.5f, ((n.X - n.Z) * 0.5f) + 0.5f);
    }

    /// <summary>Unfolds a point of the square back into a direction.</summary>
    /// <param name="square">A point in [0, 1]².</param>
    /// <returns>The direction, normalised, in the upper hemisphere.</returns>
    public static Vector3 Decode(Vector2 square) {
        var p = (square * 2f) - new Vector2(1f, 1f);

        var x = (p.X + p.Y) * 0.5f;
        var z = (p.X - p.Y) * 0.5f;
        var y = 1f - MathF.Abs(x) - MathF.Abs(z);

        var direction = new Vector3(x, y, z);

        return direction.IsZero ? Vector3.UnitY : Vector3.Normalize(direction);
    }

    /// <summary>Which direction a cell was photographed from.</summary>
    /// <param name="cell">The cell.</param>
    /// <returns>The direction, normalised.</returns>
    public Vector3 DirectionOf(ImpostorCell cell) =>
        Decode(new(cell.X / (float)(Side - 1), cell.Z / (float)(Side - 1)));

    /// <summary>The three cells whose views a direction is made of, and how much of each.</summary>
    /// <param name="direction">Which way the camera is looking from.</param>
    /// <param name="samples">Where the three go.</param>
    /// <exception cref="ArgumentException">There is not room for three.</exception>
    /// <remarks>
    ///     The direction lands in one grid quad; the quad is split on its diagonal and the triangle
    ///     the point falls in supplies the three corners, weighted barycentrically. Splitting rather
    ///     than bilinear because four views of a tree averaged together is a blur — three that share
    ///     a triangle are the smallest set whose weights still sum to one everywhere.
    /// </remarks>
    public void Blend(Vector3 direction, Span<ImpostorSample> samples) {
        if (samples.Length < 3) {
            throw new ArgumentException(
                $"An impostor blends three views and there is room for {samples.Length}.",
                nameof(samples)
            );
        }

        var square = Encode(direction);
        var last = Side - 1;

        var gx = Math.Clamp(square.X * last, 0f, last);
        var gz = Math.Clamp(square.Y * last, 0f, last);

        var x0 = Math.Min((int)MathF.Floor(gx), last - 1);
        var z0 = Math.Min((int)MathF.Floor(gz), last - 1);

        var fx = gx - x0;
        var fz = gz - z0;

        if (fx + fz <= 1f) {
            samples[0] = new(new(x0, z0), 1f - fx - fz);
            samples[1] = new(new(x0 + 1, z0), fx);
            samples[2] = new(new(x0, z0 + 1), fz);
        } else {
            samples[0] = new(new(x0 + 1, z0 + 1), fx + fz - 1f);
            samples[1] = new(new(x0, z0 + 1), 1f - fx);
            samples[2] = new(new(x0 + 1, z0), 1f - fz);
        }
    }

    /// <summary>The cell nearest a direction, for a caller that wants one view.</summary>
    /// <param name="direction">Which way the camera is looking from.</param>
    /// <returns>The cell.</returns>
    public ImpostorCell NearestTo(Vector3 direction) {
        var square = Encode(direction);
        var last = Side - 1;

        return new(
            Math.Clamp((int)MathF.Round(square.X * last), 0, last),
            Math.Clamp((int)MathF.Round(square.Y * last), 0, last)
        );
    }
}

/// <summary>Where each of a grid's views lives in the baked texture.</summary>
/// <remarks>
///     <para>
///         <b>One atlas per foliage type, and one texture fetch per channel per blended view.</b>
///         The alternative — a texture array with a layer per cell — is the same memory and one more
///         binding, and it makes the three-way blend three array reads whose layers are decided per
///         pixel, which is exactly the access pattern a texture array is worst at.
///     </para>
///     <para>
///         ⚠ <b>Every cell is padded, and the padding is not optional.</b> A bilinear tap near the
///         edge of a cell reaches into its neighbour, which at a distance of four hundred metres is a
///         tree wearing a stripe of the tree next to it. The gutter is what the bake dilates into.
///     </para>
///     <para>
///         ⚠ <b>The atlas has no mips below the cell size, and a caller building them has to stop.</b>
///         A mip that mixes two cells is the same bleed the padding exists to stop, arriving through a
///         different door — so <see cref="MipLevels" /> is how many are safe rather than how many fit.
///     </para>
/// </remarks>
public readonly record struct ImpostorAtlas {
    /// <summary>Creates a layout.</summary>
    /// <param name="grid">The view grid it holds.</param>
    /// <param name="cellSize">How many texels a cell is, gutter included.</param>
    /// <param name="padding">How many of those are gutter, on each side.</param>
    /// <exception cref="ArgumentOutOfRangeException">The cell is too small to hold its own gutter.</exception>
    public ImpostorAtlas(ImpostorGrid grid, int cellSize = 128, int padding = 4) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellSize);
        ArgumentOutOfRangeException.ThrowIfNegative(padding);

        if (cellSize <= padding * 2) {
            throw new ArgumentOutOfRangeException(
                nameof(padding),
                $"A {cellSize}-texel cell with {padding} texels of gutter on each side has nothing left "
                + "to draw the mesh into."
            );
        }

        Grid = grid;
        CellSize = cellSize;
        Padding = padding;
    }

    /// <summary>The view grid.</summary>
    public ImpostorGrid Grid { get; }

    /// <summary>How many texels a cell is, gutter included.</summary>
    public int CellSize { get; }

    /// <summary>How many of those are gutter, on each side.</summary>
    public int Padding { get; }

    /// <summary>How many texels the whole atlas is, on a side.</summary>
    public int Resolution => Grid.Side * CellSize;

    /// <summary>How many texels of a cell the mesh is drawn into.</summary>
    public int DrawSize => CellSize - (Padding * 2);

    /// <summary>How many mip levels can be built before two cells share a texel.</summary>
    /// <remarks>
    ///     Level 0 plus every halving that keeps a cell at least one texel wide — so a 9×9 grid of
    ///     128-texel cells stops at eight levels rather than the eleven the atlas's own size allows.
    /// </remarks>
    public int MipLevels {
        get {
            var levels = 1;

            for (var size = CellSize; size > 1; size /= 2) {
                levels++;
            }

            return levels;
        }
    }

    /// <summary>How many bytes the atlas is, at four channels a texel.</summary>
    public long ByteCount => (long)Resolution * Resolution * 4;

    /// <summary>Where a cell's drawable area is, in texels.</summary>
    /// <param name="cell">The cell.</param>
    /// <returns>The rect: low corner and size, gutter excluded.</returns>
    public (int X, int Y, int Width, int Height) RectOf(ImpostorCell cell) =>
        ((cell.X * CellSize) + Padding, (cell.Z * CellSize) + Padding, DrawSize, DrawSize);

    /// <summary>Where a point of a cell's own view lands in the atlas.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="uv">Where in that view, 0…1.</param>
    /// <returns>The atlas coordinate, 0…1.</returns>
    public Vector2 UvOf(ImpostorCell cell, Vector2 uv) {
        var (x, y, width, height) = RectOf(cell);

        return new(
            (x + (Math.Clamp(uv.X, 0f, 1f) * width)) / Resolution,
            (y + (Math.Clamp(uv.Y, 0f, 1f) * height)) / Resolution
        );
    }
}

/// <summary>The camera one cell of an impostor is baked from.</summary>
/// <param name="Direction">Which way it looks from, normalised.</param>
/// <param name="View">The view matrix.</param>
/// <param name="Projection">The orthographic projection.</param>
/// <param name="Radius">How far the mesh reaches from its centre, which is the ortho half-extent.</param>
/// <remarks>
///     <para>
///         ⚠ <b>Orthographic, and that is the whole reason an impostor works at all.</b> A
///         perspective bake fixes the distance the mesh was photographed from into the texture, so an
///         impostor drawn nearer or further shows the wrong parallax. An orthographic one is
///         direction-only, which is what a billboard replays.
///     </para>
///     <para>
///         ⚠ <b>One radius for every cell, taken from the bounding sphere.</b> Fitting each view's
///         own extent would pack the atlas better and would make the impostor breathe as the blend
///         moves between cells, because the same vertex would be a different number of texels from
///         the centre in each.
///     </para>
/// </remarks>
public readonly record struct ImpostorView(
    Vector3 Direction,
    Matrix4x4 View,
    Matrix4x4 Projection,
    float Radius
) {
    /// <summary>The camera for one cell of a grid, around a mesh's bounding sphere.</summary>
    /// <param name="grid">The grid.</param>
    /// <param name="cell">Which cell.</param>
    /// <param name="centre">The mesh's centre, in its own space.</param>
    /// <param name="radius">How far it reaches from there.</param>
    /// <returns>The view.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The radius is not positive.</exception>
    public static ImpostorView For(ImpostorGrid grid, ImpostorCell cell, Vector3 centre, float radius) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);

        var direction = grid.DirectionOf(cell);
        var eye = centre + (direction * radius * 2f);

        // Straight down has no side, so the up vector falls back to an axis in the horizontal plane.
        // Every cell of the top row is that direction, and a bake that produced a NaN there would
        // produce it for the one view a top-down camera spends all its time in.
        var up = MathF.Abs(direction.Y) > 0.999f ? Vector3.UnitZ : Vector3.UnitY;

        return new(
            direction,
            Matrix4x4.LookAt(eye, centre, up),
            Matrix4x4.Orthographic(radius * 2f, radius * 2f, 0.01f, radius * 4f),
            radius
        );
    }
}

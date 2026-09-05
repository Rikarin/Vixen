// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.Texturing.Painting;

/// <summary>
///     Which texels of the atlas a UV island actually covers.
/// </summary>
/// <remarks>
///     <para>
///         <b>The thing seam dilation needs and nothing else in this folder does.</b> A stamp writes
///         where the surface is; the texels between two islands belong to no triangle and are never
///         sampled by the renderer at mip 0 — which is exactly why they are the ones that ruin mip 3.
///         This is the map that says which is which.
///     </para>
///     <para>
///         ⚠ <b>It is a raster of the same shape <c>MapBaker</c> already produces</b>, and where a
///         mesh has been baked, <c>BakedMaps.Coverage</c> <em>is</em> this array —
///         <see cref="FromRaster" /> takes it directly rather than rasterising a second opinion about
///         the same triangles. <see cref="FromTriangles" /> exists for the case where nothing has been
///         baked yet, which is every stack the moment an artist creates it.
///     </para>
///     <para>
///         ⚠ <b>Conservative, and that is the safe direction here.</b> A texel a triangle only
///         clips a corner of is marked covered, because the cost of over-marking is that the
///         dilation starts one texel further out, and the cost of under-marking is a texel the
///         renderer samples and the brush refused to paint — a hole rather than a halo.
///     </para>
/// </remarks>
sealed class PaintCoverage {
    readonly bool[] covered;

    PaintCoverage(int width, int height, bool[] covered) {
        Width = width;
        Height = height;
        this.covered = covered;
    }

    /// <summary>The atlas width in texels.</summary>
    public int Width { get; }

    /// <summary>The atlas height in texels.</summary>
    public int Height { get; }

    /// <summary>How many texels belong to an island.</summary>
    public int CoveredTexels {
        get {
            var total = 0;

            foreach (var texel in covered) {
                if (texel) {
                    total++;
                }
            }

            return total;
        }
    }

    /// <summary>Whether a texel belongs to an island.</summary>
    /// <param name="index">Its index, row-major.</param>
    /// <returns>Whether it does.</returns>
    public bool IsCovered(int index) => covered[index];

    /// <summary>Whether a texel belongs to an island.</summary>
    /// <param name="x">Its column.</param>
    /// <param name="y">Its row.</param>
    /// <returns>Whether it does, and <see langword="false" /> outside the atlas.</returns>
    public bool IsCovered(int x, int y) =>
        x >= 0 && y >= 0 && x < Width && y < Height && covered[(y * Width) + x];

    /// <summary>A coverage map where every texel is surface. The 2D view's flat case.</summary>
    /// <param name="width">The atlas width.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The map.</returns>
    /// <remarks>
    ///     ⚠ <b>Dilation over this map does nothing, by construction, and that is the point.</b> A
    ///     stack with no islands has no seams, so the dilation pass must find no texel to write —
    ///     which is what makes "the dilation only ever writes outside an island" testable without a
    ///     mesh.
    /// </remarks>
    public static PaintCoverage Everywhere(int width, int height) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var covered = new bool[width * height];

        Array.Fill(covered, true);

        return new(width, height, covered);
    }

    /// <summary>A coverage map from a raster somebody else produced.</summary>
    /// <param name="width">The atlas width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="raster">One flag per texel, row-major. <c>BakedMaps.Coverage</c>'s shape.</param>
    /// <returns>The map.</returns>
    /// <exception cref="ArgumentException">The raster is not the size the dimensions describe.</exception>
    public static PaintCoverage FromRaster(int width, int height, bool[] raster) {
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (raster.Length != width * height) {
            throw new ArgumentException(
                $"A {width}×{height} coverage raster is {width * height} flags and this one is "
                + $"{raster.Length}. A mismatched raster paints the wrong texels rather than failing.",
                nameof(raster)
            );
        }

        return new(width, height, (bool[])raster.Clone());
    }

    /// <summary>A coverage map rasterised from UV triangles.</summary>
    /// <param name="width">The atlas width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="coordinates">Three UV coordinates per triangle, in the unit square.</param>
    /// <returns>The map.</returns>
    /// <exception cref="ArgumentException">The coordinate count is not a multiple of three.</exception>
    /// <remarks>
    ///     A scanline over each triangle's texel bounding box with a half-space test at the texel
    ///     centre, plus the triangle's own edges walked so a sliver thinner than a texel still marks
    ///     the texels it crosses. Both halves matter: the centre test alone drops slivers, which are
    ///     precisely the strips along an island's border.
    /// </remarks>
    public static PaintCoverage FromTriangles(int width, int height, IReadOnlyList<Vector2> coordinates) {
        ArgumentNullException.ThrowIfNull(coordinates);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (coordinates.Count % 3 != 0) {
            throw new ArgumentException(
                $"UV triangles come three coordinates at a time and this is {coordinates.Count}.",
                nameof(coordinates)
            );
        }

        var covered = new bool[width * height];

        for (var triangle = 0; triangle < coordinates.Count; triangle += 3) {
            var a = Scale(coordinates[triangle], width, height);
            var b = Scale(coordinates[triangle + 1], width, height);
            var c = Scale(coordinates[triangle + 2], width, height);

            Fill(covered, width, height, a, b, c);
            Walk(covered, width, height, a, b);
            Walk(covered, width, height, b, c);
            Walk(covered, width, height, c, a);
        }

        return new(width, height, covered);
    }

    static Vector2 Scale(Vector2 uv, int width, int height) => new(uv.X * width, uv.Y * height);

    static void Fill(bool[] covered, int width, int height, Vector2 a, Vector2 b, Vector2 c) {
        var minimum = Vector2.Min(a, Vector2.Min(b, c));
        var maximum = Vector2.Max(a, Vector2.Max(b, c));
        var rect = PaintRect.Covering(minimum, maximum).Clip(width, height);

        if (rect.IsEmpty) {
            return;
        }

        var area = Edge(a, b, c);

        if (area == 0f) {
            return;
        }

        var sign = area > 0f ? 1f : -1f;

        for (var y = rect.Y; y < rect.EndY; y++) {
            for (var x = rect.X; x < rect.EndX; x++) {
                Vector2 point = new(x + 0.5f, y + 0.5f);

                if (Edge(a, b, point) * sign < 0f
                    || Edge(b, c, point) * sign < 0f
                    || Edge(c, a, point) * sign < 0f) {
                    continue;
                }

                covered[(y * width) + x] = true;
            }
        }
    }

    /// <summary>Marks the texels an edge crosses, so a sliver narrower than a texel is not lost.</summary>
    static void Walk(bool[] covered, int width, int height, Vector2 from, Vector2 to) {
        var delta = to - from;
        var steps = (int)MathF.Ceiling(Math.Max(MathF.Abs(delta.X), MathF.Abs(delta.Y))) + 1;

        for (var step = 0; step <= steps; step++) {
            var point = from + (delta * (step / (float)steps));
            var x = (int)MathF.Floor(point.X);
            var y = (int)MathF.Floor(point.Y);

            if (x >= 0 && y >= 0 && x < width && y < height) {
                covered[(y * width) + x] = true;
            }
        }
    }

    static float Edge(Vector2 a, Vector2 b, Vector2 point) =>
        ((b.X - a.X) * (point.Y - a.Y)) - ((b.Y - a.Y) * (point.X - a.X));
}

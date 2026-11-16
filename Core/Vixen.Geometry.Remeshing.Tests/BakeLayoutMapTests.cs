// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>
///     The bounds and density maps, each against an answer worked out first on two flat islands: a unit square laid
///     out over 0.4 of the atlas, and a two-by-one rectangle laid out over 0.4 by 0.2 of it, so half as dense.
/// </summary>
public sealed class BakeLayoutMapTests {
    const int Size = 128;

    /// <summary>The two islands, one face each, baked onto themselves.</summary>
    static BakedMaps Bake(MeshMaps maps, int gutter = 2) {
        var mesh = new EditMesh();

        Square(mesh, 0f, 1f);
        Square(mesh, 2f, 4f);
        mesh.SetTexCoords(
            [
                new(0.05f, 0.05f), new(0.45f, 0.05f), new(0.45f, 0.45f), new(0.05f, 0.45f),
                new(0.5f, 0.5f), new(0.9f, 0.5f), new(0.9f, 0.7f), new(0.5f, 0.7f)
            ]
        );

        return MapBaker.Bake(
            mesh,
            mesh,
            new() {
                Resolution = Size,
                Gutter = gutter,
                Units = BakeUnits.Absolute,
                SearchRadius = 0.01f,
                Maps = maps
            }
        );
    }

    static void Square(EditMesh mesh, float x0, float x1) {
        Span<int> loop = [
            mesh.AddPosition(new(x0, 0f, 0f)),
            mesh.AddPosition(new(x1, 0f, 0f)),
            mesh.AddPosition(new(x1, 1f, 0f)),
            mesh.AddPosition(new(x0, 1f, 0f))
        ];

        mesh.AddFace(loop);
    }

    static int Texel(float u, float v) => (int)(v * Size) * Size + (int)(u * Size);

    /// <summary>
    ///     Each island has a frame of its own: the middle of the square island is the middle of its frame, the middle of
    ///     the wide one is halfway across and a quarter down, because the frame is square over its longer side, and both
    ///     are 0.4 of the atlas wide. A step across is one over the island's side in texels, the gutter carries the frame
    ///     on held within it, and off every island the map is nought.
    /// </summary>
    [Fact]
    public void Each_island_has_a_square_frame_of_its_own() {
        var maps = Bake(MeshMaps.Bounds);
        var bounds = Assert.IsAssignableFrom<IReadOnlyList<Vector3>>(maps.Bounds);
        var square = bounds[Texel(0.25f, 0.25f)];
        var wide = bounds[Texel(0.7f, 0.6f)];
        var row = (int)(0.25f * Size);
        var claimed = Enumerable.Range(0, Size * Size)
            .Where(index => maps.Coverage[index] && index % Size < 60)
            .ToArray();
        var (left, right) = (claimed.Min(static index => index % Size), claimed.Max(static index => index % Size));
        var (top, bottom) = (claimed.Min(static index => index / Size), claimed.Max(static index => index / Size));
        var side = Math.Max(right - left, bottom - top) + 1f;

        Assert.Null(maps.Islands);
        Assert.Equal(0.5f, square.X, 0.03f);
        Assert.Equal(0.5f, square.Y, 0.03f);
        Assert.Equal(0.5f, wide.X, 0.03f);
        Assert.Equal(0.25f, wide.Y, 0.03f);
        Assert.Equal(0.4f, square.Z, 0.02f);
        // A texel apart at most: the wide island's left side lies on a texel's edge, so the claim takes the texel left of it.
        Assert.Equal(square.Z, wide.Z, 1.01f / Size);
        Assert.Equal(side / Size, square.Z, 1e-6f);
        Assert.Equal(0.5f / side, bounds[row * Size + left].X, 1e-6f);

        for (var step = 1; step < 8; step++) {
            var gap = bounds[row * Size + left + step].X - bounds[row * Size + left + step - 1].X;

            Assert.Equal(1f / side, gap, 1e-5f);
        }

        Assert.Equal(0f, bounds[row * Size + left - 1].X);
        Assert.Equal(bounds[row * Size + left].Y, bounds[row * Size + left - 1].Y, 1e-6f);
        Assert.Equal(Vector3.Zero, bounds[0]);
        Assert.Equal(Vector3.Zero, bounds[Texel(0.95f, 0.95f)]);
        Assert.Single(MeshMapPlanes.Of(maps), static plane => plane is { Kind: MeshMapKind.Bounds, Channels: 3 });
    }

    /// <summary>
    ///     The square island is twice as dense as the wide one, so it reads a quarter of the map above it, the log of
    ///     two over four; the layout's density lies between theirs, near the closed form 0.4·√(1.5/3)·128 texels per
    ///     unit; and off every island the map is nought.
    /// </summary>
    [Fact]
    public void Density_reads_the_log_of_each_islands_density_against_the_layouts() {
        var maps = Bake(MeshMaps.Density);
        var density = Assert.IsAssignableFrom<IReadOnlyList<float>>(maps.Density);
        var square = density[Texel(0.25f, 0.25f)];
        var wide = density[Texel(0.7f, 0.6f)];
        var closed = MathF.Sqrt((0.16f + 0.08f) / 3f) * Size;

        Assert.Equal(0.25f, square - wide, 1e-4f);
        Assert.True(square > 0.5f && wide < 0.5f, $"The square reads {square} and the wide one {wide}.");
        Assert.InRange(maps.MeanDensity, 0.2f * Size, 0.4f * Size);
        Assert.Equal(closed, maps.MeanDensity, closed * 0.05f);
        Assert.Equal(0.5f + MathF.Log2(0.4f * Size / maps.MeanDensity) / 4f, square, 1e-4f);
        Assert.Equal(0f, density[0]);
        Assert.Single(MeshMapPlanes.Of(maps), static plane => plane is { Kind: MeshMapKind.Density, Channels: 1 });
    }

    /// <summary>A layout with one density everywhere reads a half everywhere it covers.</summary>
    [Fact]
    public void One_density_everywhere_reads_a_half() {
        var maps = MapBaker.Bake(
            HardEdgeBakeTests.HardBox(),
            HardEdgeBakeTests.HardBox(),
            new() {
                Resolution = Size,
                Gutter = 0,
                Units = BakeUnits.Absolute,
                SearchRadius = 0.01f,
                Maps = MeshMaps.Density
            }
        );
        var density = maps.Density!;

        for (var index = 0; index < density.Count; index++) {
            Assert.Equal(maps.Coverage[index] ? 0.5f : 0f, density[index], 1e-3f);
        }
    }
}

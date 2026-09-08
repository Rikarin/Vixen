// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Texturing.Painting;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>
///     The ray, the coordinate under it, and the density of the triangle it landed on.
/// </summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D13's first front end, and issue
///         <a href="https://github.com/Rikarin/Vixen/issues/574">#574</a>'s largest owed box.</b>
///         Every fixture here is a plane whose <em>layout is the arithmetic</em> — the texture
///         coordinate of a point equals its position, scaled by a constant this file names — so the
///         expected coordinate of any hit is computed rather than measured, and a wrong
///         interpolation cannot agree with it by luck.
///     </para>
///     <para>
///         ⚠ <b>The stretched fixture is the whole reason for the density half.</b> A conversion
///         sized off the hit texel alone, or off the chart's average, is exactly right on a cube and
///         on the isometric plane below — and both are what anybody writing this by hand would test
///         with. <see cref="Stretched" /> squashes one axis four to one, which is a layout an
///         ordinary unwrap produces on the side of any cylinder, and is where the two answers part.
///     </para>
/// </remarks>
public class PaintProjectionTests {
    /// <summary>The atlas every case here measures in.</summary>
    const int Size = 64;

    /// <summary>How far the camera sits off the plane, which is far enough to matter.</summary>
    /// <remarks>
    ///     ⚠ <b>Ten units, and the number is load-bearing.</b> <c>TriangleTree.Raycast</c> bounds its
    ///     search at the <em>length of the direction</em>, so a projection that passed a viewport's
    ///     unit direction straight through would find nothing further off than one unit — a raycast
    ///     that works on a model the size of a room and misses one the size of a house, with no
    ///     error anywhere. Every ray in this file is cast from further away than that bound.
    /// </remarks>
    const float Away = 10f;

    /// <summary>A plane over the unit square whose coordinates are its own X and Y.</summary>
    /// <remarks>
    ///     One triangle rather than two, so that no case here lands on a shared diagonal where the
    ///     answer depends on which triangle the traversal reached first — which is well defined and
    ///     is not what any of these assertions is about.
    /// </remarks>
    internal static PaintProjection Plane() => Stretched(1f);

    /// <summary>The same plane with its second coordinate axis squashed by a factor.</summary>
    /// <param name="squash">How much of the coordinate range one unit of surface buys, vertically.</param>
    /// <returns>The projection.</returns>
    internal static PaintProjection Stretched(float squash) =>
        PaintProjection.Over(
            [new(-1f, -1f, 0f), new(3f, -1f, 0f), new(-1f, 3f, 0f)],
            [new(-1f, -1f * squash), new(3f, -1f * squash), new(-1f, 3f * squash)],
            [0, 1, 2]
        );

    /// <summary>A ray aimed straight down at a point of the plane.</summary>
    /// <param name="x">Where, along the plane's first axis.</param>
    /// <param name="y">Along its second.</param>
    /// <returns>The ray, with a <b>unit</b> direction — which is what a viewport hands over.</returns>
    internal static Ray Down(float x, float y) => new(new(x, y, Away), new(0f, 0f, -1f));

    /// <summary>A hit reports the coordinate the layout puts under the ray, and its distance.</summary>
    [Theory]
    [InlineData(0.25f, 0.5f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(0.75f, 0.125f)]
    public void The_coordinate_under_a_ray_is_the_one_the_layout_puts_there(float x, float y) {
        Assert.True(Plane().TryHit(Down(x, y), out var hit));

        Assert.Equal(x, hit.Coordinate.X, 4);
        Assert.Equal(y, hit.Coordinate.Y, 4);

        // The distance is in the mesh's units and not a fraction of the direction, which is what the
        // tree answers with. Ten units away, ten units of travel.
        Assert.Equal(Away, hit.Distance, 3);
    }

    /// <summary>A ray cast from further away than one unit still finds the mesh.</summary>
    /// <remarks>
    ///     ⚠ <b>The instrument, twice.</b> The near case cannot fail for the reason the far one can,
    ///     so a projection that never lengthened the direction would pass one of these and fail the
    ///     other — which is the shape that tells the reader the far case is measuring the reach and
    ///     not the raycast.
    /// </remarks>
    [Theory]
    [InlineData(0.5f)]
    [InlineData(Away)]
    [InlineData(400f)]
    public void A_ray_finds_the_mesh_from_any_distance(float away) {
        Assert.True(Plane().TryHit(new(new(0.5f, 0.5f, away), new(0f, 0f, -1f)), out var hit));

        Assert.Equal(0.5f, hit.Coordinate.X, 4);
        Assert.Equal(away, hit.Distance, 2);
    }

    /// <summary>A ray that meets nothing says so rather than answering with the origin.</summary>
    [Fact]
    public void A_ray_past_the_mesh_is_a_miss() {
        Assert.False(Plane().TryHit(new(new(0.5f, 0.5f, Away), new(0f, 0f, 1f)), out var hit));
        Assert.False(hit.Found);
    }

    /// <summary>An isometric layout reads the same density along both axes.</summary>
    /// <remarks>
    ///     The layout puts one unit of coordinate on one unit of surface and the atlas is 64 texels
    ///     across, so one unit of surface is sixty-four texels — computed, not measured.
    /// </remarks>
    [Fact]
    public void An_isometric_layout_has_one_density() {
        Assert.True(Plane().TryHit(Down(0.5f, 0.5f), out var hit));

        var density = Plane().Density(hit.Triangle, Size, Size);

        Assert.Equal(Size, density.Major, 3);
        Assert.Equal(Size, density.Minor, 3);
        Assert.Equal(1f, density.Anisotropy, 3);
        Assert.True(density.IsMeasurable);
    }

    /// <summary>A squashed layout reads two, and their ratio is the squash.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the assertion a stamp sized from the hit texel alone cannot make.</b> The
    ///     texel under the pointer is the same texel whichever of these two layouts is loaded; what
    ///     differs is how much surface the next texel along is worth, which is a derivative and not
    ///     a sample.
    /// </remarks>
    [Theory]
    [InlineData(2f)]
    [InlineData(4f)]
    [InlineData(0.25f)]
    public void A_squashed_layout_reads_its_squash_as_anisotropy(float squash) {
        var projection = Stretched(squash);

        Assert.True(projection.TryHit(Down(0.5f, 0.5f), out var hit));

        var density = projection.Density(hit.Triangle, Size, Size);
        var expected = squash > 1f ? squash : 1f / squash;

        Assert.Equal(expected, density.Anisotropy, 3);

        // And the area-equivalent radius is the geometric mean of the two, which is the number
        // `PaintFootprint` multiplies by: √(64 · 64·squash) = 64·√squash, either side of one.
        Assert.Equal(Size * MathF.Sqrt(squash), density.Area, 2);
    }

    /// <summary>⚠ A model a thousandth the size still reports a normal, and a brush of the same size.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>This repository's most-repeated arithmetic defect, in the one file that boasts
    ///         about doc 41's scale rule.</b> <c>Vector3.Normalize</c> gives up below an
    ///         <em>absolute</em> 1e-6, and a cross product is twice the triangle's area — so it
    ///         scales as the <em>square</em> of the model. A mesh whose triangles are a millimetre
    ///         across has a cross product under the tolerance and the normal comes back
    ///         <c>Zero</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And a zero normal is not a crash, it is a wider brush.</b>
    ///         <c>PaintFootprint</c> reads the grazing cosine off it, a zero cosine falls to
    ///         <c>GrazingFloor</c>, and the radius comes out 1/√floor ≈ 3.2× too large — face-on, on
    ///         small models only, which is the shape nobody notices until an artist says the brush is
    ///         wrong on one asset.
    ///     </para>
    ///     <para>
    ///         So the assertion is the <em>ratio</em>: the same view of the same model at three
    ///         scales must give one answer. A test that only checked "the normal is not zero" would
    ///         pass on a normal pointing anywhere.
    ///     </para>
    /// </remarks>
    /// <param name="scale">How big the model is.</param>
    [Theory]
    [InlineData(1f)]
    [InlineData(1024f)]
    [InlineData(1f / 1024f)]
    // ⚠ The one that actually trips it, and picking it took arithmetic rather than intuition. The
    // cross product is 2·area, so it falls as the *square*: this fixture's edges are 4·scale, and
    // 1/1024 gives |cross| ≈ 1.5e-5 — fifteen times the tolerance, comfortably safe. The threshold
    // is 4·scale < ~1.07e-3, so a case that only went down to a thousandth would have been green
    // against the defect and would have read as coverage.
    [InlineData(1f / 16384f)]
    public void A_models_scale_changes_neither_its_normal_nor_the_texels_a_brush_covers(float scale) {
        var projection = PaintProjection.Over(
            [new(-1f * scale, -1f * scale, 0f), new(3f * scale, -1f * scale, 0f), new(-1f * scale, 3f * scale, 0f)],
            [new(-1f, -1f), new(3f, -1f), new(-1f, 3f)],
            [0, 1, 2]
        );

        var ray = new Ray(new(0.5f * scale, 0.5f * scale, Away * scale), new(0f, 0f, -1f));

        Assert.True(projection.TryHit(ray, out var hit));

        // The normal is a unit vector whatever the model measures.
        Assert.Equal(1f, hit.Normal.Length(), 3);
        Assert.Equal(1f, MathF.Abs(hit.Normal.Z), 3);

        // ⚠ And the footprint follows the scale exactly: at a thousandth the size, a screen pixel
        // buys a thousandth of the world, and the coordinates did not move — so the same view of the
        // same model covers the same texels. Under the defect this is 3.2× at one scale and not the
        // others, which no single-scale case can see.
        var eye = PaintEye.Orthographic(ray.Origin, new(0f, 0f, -1f), 4f * scale, Size);
        var radius = PaintFootprint.Radius(eye, ray, hit, projection.Density(hit.Triangle, Size, Size), 4f);

        Assert.True(radius > 0f, "the footprint collapsed, which is what a zero normal does to it.");
        Assert.Equal(Unscaled(), radius, 2);
    }

    /// <summary>The same view of the same layout at scale one, as the number every scale must match.</summary>
    /// <returns>The radius, in texels.</returns>
    static float Unscaled() {
        var projection = Plane();
        var ray = Down(0.5f, 0.5f);

        Assert.True(projection.TryHit(ray, out var hit));

        return PaintFootprint.Radius(
            PaintEye.Orthographic(ray.Origin, new(0f, 0f, -1f), 4f, Size),
            ray,
            hit,
            projection.Density(hit.Triangle, Size, Size),
            4f
        );
    }

    /// <summary>Two charts of the same size on one mesh get two densities, by which one was hit.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>⚠ The case that refutes "the density is the mesh's" and "the density is the
    ///         chart's" in one fixture.</b> Both triangles occupy the same area of surface; one
    ///         carries four times the coordinate range of the other. A conversion that measured
    ///         anything but the triangle it hit would answer the same number twice, and would be a
    ///         brush that is four times too small on half of a perfectly ordinary model.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And <c>UvDensity.Measure</c> is the specific thing this rules out</b>, which
    ///         #574's own correction warns about: it answers texels per square unit <em>per
    ///         island</em>, so a mesh whose two islands are these would give one number per island
    ///         and a mesh whose one island contains both would give a single average of them.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_density_is_the_hit_triangles_own_and_not_the_meshs() {
        // Two coplanar triangles the same size, side by side, with layouts that differ four to one.
        PaintProjection projection = PaintProjection.Over(
            [
                new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f),
                new(2f, 0f, 0f), new(3f, 0f, 0f), new(2f, 1f, 0f)
            ],
            [
                new(0f, 0f), new(0.25f, 0f), new(0f, 0.25f),
                new(0.5f, 0f), new(0.5f + 1f, 0f), new(0.5f, 1f)
            ],
            [0, 1, 2, 3, 4, 5]
        );

        Assert.True(projection.TryHit(new(new(0.2f, 0.2f, Away), new(0f, 0f, -1f)), out var thin));
        Assert.True(projection.TryHit(new(new(2.2f, 0.2f, Away), new(0f, 0f, -1f)), out var wide));

        var quiet = projection.Density(thin.Triangle, Size, Size);
        var loud = projection.Density(wide.Triangle, Size, Size);

        Assert.Equal(Size * 0.25f, quiet.Area, 3);
        Assert.Equal(Size * 1f, loud.Area, 3);
        Assert.Equal(4f, loud.Area / quiet.Area, 3);
    }

    /// <summary>A triangle with no area in the atlas is not measurable, rather than very dense.</summary>
    [Fact]
    public void A_triangle_with_a_collapsed_layout_is_not_measurable() {
        PaintProjection projection = PaintProjection.Over(
            [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)],
            [new(0.5f, 0.5f), new(0.5f, 0.5f), new(0.5f, 0.5f)],
            [0, 1, 2]
        );

        Assert.True(projection.TryHit(new(new(0.2f, 0.2f, Away), new(0f, 0f, -1f)), out var hit));

        var density = projection.Density(hit.Triangle, Size, Size);

        Assert.False(density.IsMeasurable);
        Assert.Equal(0f, density.Area, 6);
    }

    /// <summary>An island's last covered column is the last column the projection puts a hit in.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The two halves of painting on a model share one convention or the brush is
    ///         refused where the pointer is.</b> <c>PaintCoverage.FromTriangles</c> rasterises a
    ///         coordinate at <c>u · width</c> with no half-texel anywhere, and
    ///         <c>PaintProjection.Texel</c> has to agree exactly — a half-texel in either puts a hit
    ///         on the far side of an island edge from the triangle that produced it, which is a
    ///         stroke dilated at a boundary the artist is nowhere near.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The island stops short of a texel boundary, and that is what makes this able to
    ///         fail.</b> The first version of this case was a layout covering the whole atlas, where
    ///         every texel is covered and a hit displaced by half a texel is still covered — it
    ///         stayed green against exactly the drift it was written for, which is this
    ///         repository's own "verify the instrument" rule catching a new test rather than old
    ///         code. A layout whose last column is 31 of 64 has somewhere for a displaced hit to
    ///         land, and the column is asserted rather than only its coverage.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_islands_last_column_is_where_a_hit_at_its_edge_lands() {
        // The unit square, laid out over a little less than half the atlas: the largest coordinate
        // is 0.499, which is 31.936 texels, so column 31 is the last covered one and 32 is outside
        // the island altogether.
        Vector2[] layout = [new(0f, 0f), new(0.499f, 0f), new(0.499f, 0.499f), new(0f, 0.499f)];

        var projection = PaintProjection.Over(
            [new(0f, 0f, 0f), new(1f, 0f, 0f), new(1f, 1f, 0f), new(0f, 1f, 0f)],
            layout,
            [0, 1, 2, 0, 2, 3]
        );

        // One layout, two consumers — the same four coordinates, in the flat triangle form the
        // coverage rasteriser takes.
        var coverage = PaintCoverage.FromTriangles(
            Size,
            Size,
            [layout[0], layout[1], layout[2], layout[0], layout[2], layout[3]]
        );

        Assert.False(coverage.IsCovered(32, 16));

        Assert.True(projection.TryHit(Down(0.999f, 0.5f), out var hit));

        var texel = PaintProjection.Texel(hit.Coordinate, Size, Size);

        Assert.Equal(31, (int)texel.X);
        Assert.True(coverage.IsCovered((int)texel.X, (int)texel.Y));
    }

    /// <summary>And a hit anywhere inside an island lands on a texel that island covers.</summary>
    [Theory]
    [InlineData(0.02f, 0.5f)]
    [InlineData(0.5f, 0.02f)]
    [InlineData(0.98f, 0.98f)]
    public void A_hits_texel_is_covered_by_the_map_the_same_layout_rasterises(float x, float y) {
        var coverage = PaintCoverage.FromTriangles(
            Size,
            Size,
            [new(-1f, -1f), new(3f, -1f), new(-1f, 3f)]
        );

        Assert.True(Plane().TryHit(Down(x, y), out var hit));

        var texel = PaintProjection.Texel(hit.Coordinate, Size, Size);

        Assert.True(coverage.IsCovered((int)texel.X, (int)texel.Y));
    }

    /// <summary>A mesh whose triangles have no coordinates is refused with a sentence.</summary>
    /// <remarks>
    ///     ⚠ <b>The refusal is about the layout and never about the geometry.</b> A model with no
    ///     atlas has perfectly good positions, so a check on those alone would build a tree that
    ///     raycasts and answers <c>(0, 0)</c> for every hit — a brush that paints one corner of the
    ///     atlas whatever the artist aims at, which looks like a broken brush and is a missing
    ///     unwrap.
    /// </remarks>
    [Fact]
    public void A_mesh_with_no_coordinates_is_refused_rather_than_projected() {
        MeshData mesh = new() {
            Positions = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)],
            Indices = [0, 1, 2]
        };

        Assert.Null(PaintProjection.Open([mesh], out var refusal));
        Assert.Contains("texture coordinates", refusal, StringComparison.Ordinal);
    }

    /// <summary>Several meshes become one soup, with each one's indices moved along.</summary>
    /// <remarks>
    ///     ⚠ <b>A set may name every mesh of a model, and the offset is what stops the second one
    ///     being read through the first one's vertices.</b> Without it the tree is built over
    ///     triangles that exist nowhere, and every one of them raycasts perfectly well.
    /// </remarks>
    [Fact]
    public void Two_meshes_project_as_one_soup() {
        MeshData first = new() {
            Positions = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)],
            TexCoords = [new(0f, 0f), new(1f, 0f), new(0f, 1f)],
            Indices = [0, 1, 2]
        };

        MeshData second = new() {
            Positions = [new(4f, 0f, 0f), new(5f, 0f, 0f), new(4f, 1f, 0f)],
            TexCoords = [new(0f, 0f), new(1f, 0f), new(0f, 1f)],
            Indices = [0, 1, 2]
        };

        var projection = PaintProjection.Open([first, second], out var refusal);

        Assert.NotNull(projection);
        Assert.Empty(refusal);
        Assert.Equal(2, projection.Triangles);

        // The second mesh is where its own positions say it is, which is the offset's whole job.
        Assert.True(projection.TryHit(new(new(4.2f, 0.2f, Away), new(0f, 0f, -1f)), out var hit));
        Assert.Equal(1, hit.Triangle);
    }
}

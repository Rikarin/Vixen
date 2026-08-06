// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>docs/plan/41 § D12's bake, and the two ways a bake is quietly wrong.</summary>
/// <remarks>
///     <b>The bake is where the pipeline's arithmetic closes</b> — five thousand quads plus a normal
///     map against four million triangles of noise — and the two failures that make it useless are
///     both invisible in a screenshot of the atlas. A pixel-centre coverage rule loses the outer row
///     of every chart, so the gutter has nothing to grow from and the hole survives; and a dilation
///     that runs per chart bleeds one chart's gutter over the next chart's content, which shows up as
///     a wrong-coloured stripe at mip 3 and gets blamed on the sampler.
/// </remarks>
public class MapBakerTests {
    /// <summary>A chart thinner than a texel covers texels, which a pixel-centre rule does not.</summary>
    /// <remarks>
    ///     ⚠ <b><c>SoftwareRaster</c>'s half-space rule is the reusable part and it is pixel-centre
    ///     only.</b> That is the correct rule for a framebuffer, where a triangle covering no centre
    ///     covers no pixel by definition, and it is the wrong one for an atlas, where the outermost
    ///     row of texels along every chart is exactly the row whose centres the chart misses. This
    ///     asserts the difference rather than describing it: the same sliver, both rules.
    /// </remarks>
    [Fact]
    public void A_sliver_thinner_than_a_texel_is_covered_conservatively_and_not_by_pixel_centres() {
        Vector2 a = new(0.1f, 0.1f);
        Vector2 b = new(3.9f, 0.15f);
        Vector2 c = new(0.1f, 0.2f);

        var conservative = 0;
        var centres = 0;

        for (var x = 0; x < 4; x++) {
            for (var y = 0; y < 4; y++) {
                if (AtlasRaster.Overlaps(a, b, c, new(x, y), new(x + 1, y + 1))) {
                    conservative++;
                }

                if (Contains(a, b, c, new(x + 0.5f, y + 0.5f))) {
                    centres++;
                }
            }
        }

        Assert.Equal(0, centres);
        Assert.True(conservative >= 4, $"Conservative coverage found only {conservative} texels for the sliver.");
    }

    /// <summary>A degenerate chart triangle covers nothing and produces no <c>NaN</c>.</summary>
    [Fact]
    public void A_chart_triangle_with_no_area_covers_nothing() {
        Vector2 point = new(2f, 2f);

        Assert.False(AtlasRaster.Overlaps(point, point, point, new(0f, 0f), new(1f, 1f)));

        var weights = AtlasRaster.Barycentric(new(0.5f, 0.5f), point, point, point);

        Assert.Equal(1f, weights.X + weights.Y + weights.Z, 4);
    }

    /// <summary>One chart's gutter does not overwrite the chart abutting it.</summary>
    /// <remarks>
    ///     ⚠ <b>Two charts whose texels abut is the common case, not the exotic one</b> — the
    ///     packer's whole job is to make it common. The two charts here face opposite ways, so a
    ///     bleed is not a subtle shade difference but a sign flip, and it is asserted on every
    ///     covered texel rather than sampled.
    /// </remarks>
    [Fact]
    public void A_gutter_never_writes_over_the_chart_beside_it() {
        var source = Facing();
        var target = Facing();

        Halves(target);

        var maps = MapBaker.Bake(source, target, new() { Resolution = 64, Gutter = 6, Space = BakeSpace.Object });

        Assert.True(maps.Covered > 0, string.Join(" · ", maps.Warnings));
        Assert.True(maps.Dilated > 0, "Nothing was dilated, so the test proves nothing about the gutter.");

        for (var y = 0; y < maps.Resolution; y++) {
            for (var x = 0; x < maps.Resolution; x++) {
                var index = (y * maps.Resolution) + x;

                if (!maps.Coverage[index]) {
                    continue;
                }

                // The left half is the +Z sheet and the right half is the −Z one, so a bleed across
                // the join reads as a normal pointing the wrong way rather than as a shade.
                var wanted = x < maps.Resolution / 2 ? 1f : -1f;

                Assert.True(
                    maps.Normals[index].Z * wanted > 0.5f,
                    $"Texel ({x}, {y}) is chart content and carries {maps.Normals[index]}, "
                    + "which belongs to the chart next to it."
                );
            }
        }
    }

    /// <summary>Baking a surface onto a copy of itself gives the identity normal and no height.</summary>
    /// <remarks>
    ///     The target sits a little proud of the source, so the rays actually travel — a target
    ///     exactly coincident with the source has every ray rejected at its own origin, which is
    ///     correct and would make this assert the fallback rather than the bake.
    /// </remarks>
    [Fact]
    public void A_flat_bake_of_the_same_surface_is_the_identity_normal() {
        var source = TransferFixtures.Grid(16, 2f, _ => 0);
        var target = Lifted(TransferFixtures.Grid(4, 2f, _ => 0), 0.02f);

        Halves(target, whole: true);

        var maps = MapBaker.Bake(source, target, new() { Resolution = 32, Gutter = 2 });

        Assert.True(maps.Covered > 0, string.Join(" · ", maps.Warnings));

        // ⚠ Not zero, and that is conservative coverage behaving as designed rather than a defect.
        // A texel whose centre lies just outside the chart is still covered — that is the whole
        // point of the rule — and along the sheet's outer edge the ray from such a centre starts
        // beyond where the source ends and finds nothing. Those texels take the closest-point
        // fallback, which is the right answer measured from slightly the wrong place. Measured: 55
        // of 900 on this fixture, which is about half of the chart's 112-texel outline — the half
        // whose centres fall outside it.
        Assert.True(
            maps.Missed * 8 < maps.Covered,
            $"{maps.Missed} of {maps.Covered} texels found no source along the normal, which is more "
            + "than the outline can account for."
        );

        for (var index = 0; index < maps.Normals.Count; index++) {
            if (!maps.Coverage[index]) {
                continue;
            }

            Assert.InRange(maps.Normals[index].Z, 0.99f, 1.01f);
            Assert.InRange(maps.Displacement[index], -0.031f, -0.009f);
        }
    }

    /// <summary>Displacement reads the bump the output smoothed over.</summary>
    /// <remarks>
    ///     <b>The half of § D12 that makes a five-thousand-quad cage as good as four million
    ///     triangles.</b> The source carries a cone the flat target has no vertices for, and the bake
    ///     is what puts it back.
    /// </remarks>
    [Fact]
    public void The_displacement_map_carries_a_feature_the_cage_has_no_vertices_for() {
        var source = Cone(32, 2f, 0.25f);
        var target = TransferFixtures.Grid(4, 2f, _ => 0);

        Halves(target, whole: true);

        var maps = MapBaker.Bake(source, target, new() { Resolution = 64, Gutter = 2, SearchRadius = 0.4f });

        Assert.True(maps.Covered > 0, string.Join(" · ", maps.Warnings));
        Assert.True(maps.DisplacementRange > 0.1f, $"The cone is 0.25 tall and the bake found {maps.DisplacementRange}.");

        // The middle of the atlas is the middle of the sheet, which is the top of the cone.
        var centre = ((maps.Resolution / 2) * maps.Resolution) + (maps.Resolution / 2);

        Assert.True(
            maps.Displacement[centre] > 0.15f,
            $"The cone's apex baked to {maps.Displacement[centre]} rather than about 0.25."
        );
    }

    /// <summary>The bake answers the same at a thousandth and a thousand times scale.</summary>
    /// <remarks>
    ///     ⚠ <b>The search radius is a fraction of the source's diagonal for exactly this
    ///     reason.</b> A cage measured in metres finds nothing on the same model exported in
    ///     centimetres, and the failure is silent — every texel takes the closest-point fallback and
    ///     the map looks plausible.
    /// </remarks>
    [Fact]
    public void The_bake_answers_the_same_at_a_thousandth_and_a_thousand_times_scale() {
        var baseline = Baked(1f);

        foreach (var scale in (float[]) [1e-3f, 1e+3f]) {
            var scaled = Baked(scale);

            // Coverage is a function of the coordinates and the resolution alone, so it is exact.
            Assert.Equal(baseline.Covered, scaled.Covered);

            // ⚠ The miss count is *not* asserted exact, and refusing to pretend otherwise is the
            // point. Every tolerance in the ray test is already relative — that was fixed in
            // `TriangleTree` before this phase started — and what is left is ordinary float
            // rounding: a ray grazing the sheet's outline within an ulp of its edge hits at one
            // exponent and misses at another. Measured across a million-to-one span of scales, 1
            // against 3 of 467 texels, all on the outline, and each one lands on the closest-point
            // fallback whose answer differs from the cast's by less than the assertion below. A
            // tolerance cannot fix that and a golden would only record it.
            Assert.True(
                scaled.Missed * 50 < scaled.Covered,
                $"{scaled.Missed} of {scaled.Covered} texels missed at {scale}×, against "
                + $"{baseline.Missed} at unit scale — that is more than the outline."
            );

            // ⚠ A hundredth of a unit normal across a million-to-one span of model scales, and the
            // threshold is where it is on evidence rather than on taste. An absolute tolerance
            // firing does not look like this — it gives a zero vector, or the normal of a different
            // facet, both of which are order-one errors and both of which this test caught while it
            // was being written. What is left is the ray-triangle intersection's own barycentric
            // rounding, which moves the sample a few ulps along a surface whose normal is turning:
            // measured worst case 0.0058 at a thousandth scale, on the steepest part of the cone.
            foreach (var index in Enumerable.Range(0, baseline.Normals.Count)) {
                Assert.True(
                    Vector3.Distance(baseline.Normals[index], scaled.Normals[index]) < 1e-2f,
                    $"Texel {index}'s normal moved from {baseline.Normals[index]} to "
                    + $"{scaled.Normals[index]} at {scale}×."
                );
            }

            // The height is a length, so it scales with the model — the fraction of the diagonal is
            // what must not move.
            var wanted = baseline.DisplacementRange * scale;

            Assert.InRange(scaled.DisplacementRange, wanted * 0.99f, wanted * 1.01f);
        }
    }

    /// <summary>A target with no coordinates is refused rather than baked into nothing.</summary>
    [Fact]
    public void A_target_with_no_atlas_is_refused() {
        var source = TransferFixtures.Grid(4, 2f, _ => 0);
        var target = TransferFixtures.Grid(2, 2f, _ => 0);

        Assert.Throws<ArgumentException>(() => MapBaker.Bake(source, target, new() { Resolution = 8 }));
    }

    /// <summary>One bake of the cone fixture at a given scale.</summary>
    /// <remarks>
    ///     ⚠ <b>The coordinates are assigned before the scaling and carried through it, so the two
    ///     runs are handed the same atlas rather than one computed twice.</b> Deriving them on each
    ///     scaled copy instead made the two disagree about one texel out of 293 — the division by a
    ///     thousand-times extent rounds elsewhere — and a coverage test that measures its own
    ///     fixture's rounding cannot say anything about the baker's.
    /// </remarks>
    static BakedMaps Baked(float scale) {
        // ⚠ Lifted clear of the source, because a cage lying exactly *on* the surface has every ray
        // rejected at its own origin — correctly, since a hit at zero distance is the origin — and
        // the bake then measures the closest-point fallback rather than the cast. Unlifted, 293 of
        // this fixture's 467 texels took the fallback, and two of the three scales disagreed about
        // one of them.
        var target = Lifted(TransferFixtures.Grid(4, 2f, _ => 0), 0.05f);

        Halves(target, whole: true);

        return MapBaker.Bake(
            TransferFixtures.Scaled(Cone(24, 2f, 0.25f), scale),
            TransferFixtures.Scaled(target, scale),
            new() { Resolution = 24, Gutter = 2, SearchRadius = 0.4f }
        );
    }

    /// <summary>Gives a sheet coordinates: the whole unit square, or two charts side by side.</summary>
    /// <remarks>
    ///     ⚠ <b>Derived from the mesh's own bounds and never from the size it was built at.</b> The
    ///     first version divided by a literal 2, so the scale-invariance test handed a thousand-times
    ///     model coordinates running to ±500 and measured a bake of four texels against one of 576 —
    ///     which looked exactly like the scale bug it was written to find, and was the fixture's.
    /// </remarks>
    static void Halves(EditMesh mesh, bool whole = false) {
        var coordinates = new Vector2[mesh.CornerCount];
        var bounds = mesh.Bounds;
        var size = bounds.Maximum - bounds.Minimum;
        var wide = size.X > 0f ? size.X : 1f;
        var tall = size.Y > 0f ? size.Y : 1f;

        for (var face = 0; face < mesh.FaceCount; face++) {
            var entry = mesh.Faces[face];
            var loop = mesh.CornersOf(face);

            for (var index = 0; index < loop.Length; index++) {
                var point = mesh.Positions[loop[index]];
                var u = (point.X - bounds.Minimum.X) / wide;
                var v = (point.Y - bounds.Minimum.Y) / tall;

                // Two charts with a two-texel gap between them at a 64-texel resolution, which is
                // narrower than the gutter — so one chart's dilation runs into the other's content
                // and the test is about whether it overwrites it.
                coordinates[entry.Start + index] = whole
                    ? new((u * 0.9f) + 0.05f, (v * 0.9f) + 0.05f)
                    : new(
                        point.Z > (bounds.Minimum.Z + bounds.Maximum.Z) * 0.5f
                            ? 0.52f + (u * 0.46f)
                            : 0.02f + (u * 0.46f),
                        (v * 0.9f) + 0.05f
                    );
            }
        }

        mesh.SetTexCoords(coordinates);
    }

    /// <summary>Two sheets, one at <c>z = 0</c> facing up and one at <c>z = 1</c> facing down.</summary>
    static EditMesh Facing() {
        var mesh = new EditMesh();

        for (var sheet = 0; sheet < 2; sheet++) {
            var z = sheet;
            var indices = new int[5, 5];

            for (var i = 0; i < 5; i++) {
                for (var j = 0; j < 5; j++) {
                    indices[i, j] = mesh.AddPosition(new((i / 2f) - 1f, (j / 2f) - 1f, z));
                }
            }

            for (var i = 0; i < 4; i++) {
                for (var j = 0; j < 4; j++) {
                    Span<int> loop = sheet == 0
                        ? [indices[i, j], indices[i + 1, j], indices[i + 1, j + 1], indices[i, j + 1]]
                        : [indices[i, j], indices[i, j + 1], indices[i + 1, j + 1], indices[i + 1, j]];

                    mesh.AddFace(loop, sheet);
                }
            }
        }

        return mesh;
    }

    /// <summary>A sheet moved along <c>z</c>, so a bake's rays have somewhere to travel.</summary>
    static EditMesh Lifted(EditMesh mesh, float height) {
        for (var vertex = 0; vertex < mesh.PositionCount; vertex++) {
            mesh.MovePosition(vertex, mesh.Positions[vertex] + new Vector3(0f, 0f, height));
        }

        return mesh;
    }

    /// <summary>A sheet with a cone standing on it, which a flat cage cannot represent.</summary>
    static EditMesh Cone(int cells, float size, float height) =>
        Smoothed(
            Raised(
                TransferFixtures.Grid(cells, size, _ => 0),
                point => MathF.Max(0f, height * (1f - (new Vector2(point.X, point.Y).Length() / (size * 0.35f))))
            )
        );

    /// <summary>Area-weighted vertex normals, written into the per-corner layer.</summary>
    /// <remarks>
    ///     ⚠ <b>Without these the fixture is faceted, and a faceted source makes the
    ///     scale-invariance test measure the wrong thing.</b> With no normal layer the transfer
    ///     falls back to the struck triangle's own flat normal, so a ray grazing the join between two
    ///     facets of the cone returns one of two visibly different answers — measured, a 0.08 swing
    ///     at one texel out of 467, which reads exactly like an absolute tolerance and is a faceted
    ///     cone. A continuous normal field makes the facet a ray lands on stop mattering.
    /// </remarks>
    static EditMesh Smoothed(EditMesh mesh) {
        var accumulated = new Vector3[mesh.PositionCount];

        for (var face = 0; face < mesh.FaceCount; face++) {
            var normal = mesh.Normal(face) * mesh.Area(face);

            foreach (var corner in mesh.CornersOf(face)) {
                accumulated[corner] += normal;
            }
        }

        var normals = new Vector3[mesh.CornerCount];

        for (var face = 0; face < mesh.FaceCount; face++) {
            var entry = mesh.Faces[face];
            var loop = mesh.CornersOf(face);

            for (var index = 0; index < loop.Length; index++) {
                normals[entry.Start + index] = Vector3.Normalize(accumulated[loop[index]]);
            }
        }

        mesh.SetNormals(normals);

        return mesh;
    }

    /// <summary>Displaces a sheet along <c>z</c> by a function of its position.</summary>
    static EditMesh Raised(EditMesh mesh, Func<Vector3, float> height) {
        for (var vertex = 0; vertex < mesh.PositionCount; vertex++) {
            var point = mesh.Positions[vertex];

            mesh.MovePosition(vertex, new(point.X, point.Y, point.Z + height(point)));
        }

        return mesh;
    }

    /// <summary>The pixel-centre rule, for the comparison the first test makes.</summary>
    static bool Contains(Vector2 a, Vector2 b, Vector2 c, Vector2 point) {
        var one = Edge(a, b, point);
        var two = Edge(b, c, point);
        var three = Edge(c, a, point);

        return (one >= 0f && two >= 0f && three >= 0f) || (one <= 0f && two <= 0f && three <= 0f);
    }

    /// <summary>Twice the signed area of a triangle, which is what a half-space rule tests.</summary>
    static float Edge(Vector2 a, Vector2 b, Vector2 point) =>
        ((b.X - a.X) * (point.Y - a.Y)) - ((b.Y - a.Y) * (point.X - a.X));
}

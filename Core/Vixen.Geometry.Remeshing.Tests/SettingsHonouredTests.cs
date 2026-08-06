// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>One test per setting that is supposed to do something, comparing two outputs.</summary>
/// <remarks>
///     <para>
///         <b>The failure class docs/plan/41's R7 went looking for: a setting that is read and not
///         honoured.</b> Every other test in this project fixes the settings and asserts something
///         about one result, and a field that is declared, bound, serialized, shown in a panel and
///         then never consulted passes all of them. The only assertion that catches it is "change this
///         one thing and the output changes", so each of <see cref="RemeshSettings.Guides" />,
///         <see cref="RemeshSettings.DensityMask" />, <see cref="RemeshSettings.Symmetry" /> and
///         <see cref="RemeshSettings.KeepUvSeams" /> gets one.
///     </para>
///     <para>
///         ⚠ <b>Symmetry's is in <see cref="SymmetryTests" /> rather than here</b>, because it has a
///         criterion of its own to meet and the comparison is only the weakest of the four things that
///         file asserts.
///     </para>
///     <para>
///         ⚠ <b>Two settings are still declared and not honoured, and they are named rather than
///         quietly untested:</b> <see cref="RemeshSettings.TransferAttributes" /> is R5's and
///         <see cref="RemeshSettings.GenerateUvs" /> is R6's. Nothing in the pipeline reads either.
///     </para>
/// </remarks>
public class SettingsHonouredTests {
    /// <summary>A guide curve pulls the field, so the mesh that comes out is a different mesh.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A guide only claims an edge of the <i>conditioned</i> surface, and the tolerance it
    ///         claims within is a fraction of the bounding-box diagonal while the thing it has to reach
    ///         is a fraction of the target edge length.</b> <see cref="FeatureDetector.CurveTolerance" />
    ///         is one percent of the diagonal; the pre-remesh rebuilds the surface at
    ///         <c>√(area / quads)</c>, which on any model coarse enough to be worth remeshing is
    ///         several times larger. The two only meet when the target is dense.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is why this fixture turns the pre-remesh off, and why that is a finding
    ///         rather than a convenience.</b> With
    ///         <see cref="ConditioningSettings.PreRemeshIterations" /> at its default of five, a guide
    ///         authored against the source's own vertices is resolved onto no edge at all and does
    ///         nothing, silently — measured on an 8×8 plate: 8 edges claimed at zero iterations, none
    ///         at five, and byte-identical output in the second case. The mechanism below the detector
    ///         works: those 8 edges move 64 vertices of the solved field. What is wrong is the
    ///         tolerance's unit, and that belongs to docs/plan/41's R2 rather than to this phase.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_guide_changes_the_output() {
        var settings = Base with { Conditioning = new() { PreRemeshIterations = 0 } };
        var without = Remesher.Remesh(FieldFixtures.Plate(8, 8, []), settings, out _);

        var with = Remesher.Remesh(
            FieldFixtures.Plate(8, 8, []),
            settings with { Guides = [new(Across(), 1f)] },
            out var report
        );

        Assert.True(with.FaceCount > 0, string.Join(" · ", report.Warnings));
        Assert.NotEqual(Signature(without), Signature(with));
    }

    /// <summary>A guide that lies on no edge is dropped, and the result is the ungiuded one.</summary>
    /// <remarks>
    ///     <b>The other half of the finding, asserted so it cannot regress into a crash.</b> A curve
    ///     the detector cannot resolve is not an error and must not become one — an artist's guide
    ///     outlives the mesh it was drawn against, which is § D10's whole argument for guides being an
    ///     asset, and a source that has since moved is the ordinary case rather than the broken one.
    /// </remarks>
    [Fact]
    public void A_guide_nowhere_near_the_surface_is_dropped_rather_than_refused() {
        var settings = Base with { Conditioning = new() { PreRemeshIterations = 0 } };
        var without = Remesher.Remesh(FieldFixtures.Plate(8, 8, []), settings, out _);

        var with = Remesher.Remesh(
            FieldFixtures.Plate(8, 8, []),
            settings with { Guides = [new([new Vector3(0f, 40f, 0f), new Vector3(8f, 40f, 8f)], 1f)] },
            out var report
        );

        Assert.True(with.FaceCount > 0, string.Join(" · ", report.Warnings));
        Assert.Equal(Signature(without), Signature(with));
    }

    /// <summary>A line of the plate's own vertices, straight across its top face.</summary>
    static Vector3[] Across() {
        var points = new Vector3[9];

        for (var index = 0; index < points.Length; index++) {
            points[index] = new(index, 1f, 4f);
        }

        return points;
    }

    /// <summary>A density mask that varies over the source changes where the quads go.</summary>
    /// <remarks>
    ///     ⚠ <b>The mask has to be exactly one entry per source position or it is ignored in
    ///     silence</b> — <c>Remesher</c> compares its length against <c>PositionCount</c> and passes
    ///     null on a mismatch. That is the right behaviour for a mask authored against a mesh that has
    ///     since changed, and it is also the thing that would make this test pass while proving
    ///     nothing, so the length is asserted first.
    /// </remarks>
    [Fact]
    public void A_density_mask_changes_the_output() {
        var source = RemesherTests.Fixture("sphere");
        var without = Remesher.Remesh(RemesherTests.Fixture("sphere"), Base, out _);

        var mask = new float[source.PositionCount];

        for (var index = 0; index < mask.Length; index++) {
            mask[index] = source.Positions[index].Y > 0f ? 2.5f : 0.4f;
        }

        Assert.Equal(source.PositionCount, mask.Length);

        var with = Remesher.Remesh(source, Base with { DensityMask = mask }, out var report);

        Assert.True(with.FaceCount > 0, string.Join(" · ", report.Warnings));
        Assert.NotEqual(Signature(without), Signature(with));
    }

    /// <summary>An existing coordinate seam becomes a feature, so the layout is cut on it.</summary>
    /// <remarks>
    ///     <b>docs/plan/41 § D4's "so that a retexture-then-remesh round trip does not shred an
    ///     atlas".</b> The fixture's coordinates jump across one band of the cylinder, which is what an
    ///     atlas cut looks like from the remesher's side.
    /// </remarks>
    [Fact]
    public void Keeping_uv_seams_changes_the_output() {
        // The same trap the guide test documents: a seam is a carried-in curve and is resolved by the
        // same tolerance, so the pre-remesh is off here for the same reason and not for another one.
        var settings = Base with { Conditioning = new() { PreRemeshIterations = 0 } };
        var source = Seamed();
        var without = Remesher.Remesh(Seamed(), settings with { KeepUvSeams = false }, out _);
        var with = Remesher.Remesh(source, settings with { KeepUvSeams = true }, out var report);

        Assert.True(with.FaceCount > 0, string.Join(" · ", report.Warnings));
        Assert.NotEmpty(FeatureCurves.FromUvSeams(source));
        Assert.NotEqual(Signature(without), Signature(with));
    }

    /// <summary>A mask of the wrong length is ignored rather than thrown at or half-applied.</summary>
    [Fact]
    public void A_density_mask_of_the_wrong_length_is_ignored() {
        var without = Remesher.Remesh(RemesherTests.Fixture("box"), Base, out _);
        var with = Remesher.Remesh(RemesherTests.Fixture("box"), Base with { DensityMask = [0.2f, 3f] }, out _);

        Assert.Equal(Signature(without), Signature(with));
    }

    static RemeshSettings Base => new() { TargetQuads = 200 };

    /// <summary>A cylinder whose coordinates jump across one column of edges.</summary>
    static EditMesh Seamed() {
        var mesh = MeshShapes.Create(ShapeParameters.Default(ShapeKind.Cylinder) with { Sides = 16 });
        var coordinates = new Vector2[mesh.CornerCount];

        for (var face = 0; face < mesh.FaceCount; face++) {
            var entry = mesh.Faces[face];
            var corners = mesh.CornersOf(face);

            // ⚠ The offset is a function of the *face* and not of the position, and that is the whole
            // fixture. A coordinate derived from the position alone is one both faces on a shared edge
            // agree about, so it has no seams at all however discontinuous it looks — which is what a
            // first attempt at this test produced, and it proved nothing.
            var side = mesh.Normal(face).Z >= 0f ? 0f : 1f;

            for (var corner = 0; corner < corners.Length; corner++) {
                var position = mesh.Positions[corners[corner]];

                coordinates[entry.Start + corner] = new(position.X + side, position.Y);
            }
        }

        mesh.SetTexCoords(coordinates);

        return mesh;
    }

    /// <summary>Enough of a result to tell two of them apart, and cheap enough to compare.</summary>
    static (int Positions, int Faces, int Hash) Signature(EditMesh mesh) {
        var hash = new HashCode();

        foreach (var position in mesh.Positions) {
            hash.Add(position);
        }

        return (mesh.PositionCount, mesh.FaceCount, hash.ToHashCode());
    }
}

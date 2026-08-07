// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>docs/plan/41 § D4's structural promise, asserted over the partition rather than argued.</summary>
/// <remarks>
///     <para>
///         <b>§ D4: "the polylines are then boundaries of the layout, not a post-process snap", and the
///         whole hard-surface claim rests on it.</b> The promise has an exact form that can be counted:
///         for every feature edge of the conditioned mesh, the two triangles either side of it must
///         belong to <i>different</i> patches. A feature edge with the same patch on both sides is a
///         crease running through a patch's interior, which is the promise broken and — since the grid
///         inside a patch knows nothing about a crease — a folded quad waiting to happen.
///     </para>
///     <para>
///         ⚠⚠ <b>This exists because the opposite was believed, on evidence, and was wrong.</b> The
///         inverted quads of <see cref="QuadQualityTests" /> cluster hard on 40° output edges — a
///         factor of 29 on the box — and the residual folds are near-planar bow-ties inside patches a
///         provable embedding had filled, which together read as a patch region that doubles back
///         across a crease. Counted directly the number is <b>0</b> on every fixture, and it was 0
///         before any of this branch's changes: the layout was never the defect. The correlation is
///         real and its arrow is the other way round, because a crease <i>is</i> a patch boundary and
///         so every quad in a grid's boundary row has one.
///     </para>
///     <para>
///         ⚠ <b>Counted over the manifold view's half-edges rather than over the polylines.</b> A
///         polyline is a chain of vertex indices and asking whether a <i>vertex</i> is on a boundary
///         would pass for a patch that merely touches the crease at a point. The edge is the thing
///         § D4 makes a cut, so the edge is what gets counted — and both of its half-edges are checked,
///         because <c>PatchLayout.Flood</c> reads one side of an edge at a time and an asymmetric flag
///         would let the fill cross a crease in one direction only.
///     </para>
/// </remarks>
public class CreasesBoundPatchesTests {
    /// <summary>No feature edge has the same patch on both sides.</summary>
    /// <param name="name">Which fixture.</param>
    [Theory]
    [InlineData("box")]
    [InlineData("cylinder")]
    [InlineData("stairs")]
    [InlineData("plate")]
    [InlineData("union")]
    [InlineData("difference")]
    public void NoPatchInteriorCrossesAFeatureEdge(string name) {
        var layout = RemesherTests.Layout(name, out var mesh, out var features);
        var patchOf = new int[mesh.TriangleCount];

        Array.Fill(patchOf, -1);

        for (var patch = 0; patch < layout.Patches.Count; patch++) {
            foreach (var triangle in layout.Patches[patch].Triangles) {
                patchOf[triangle] = patch;
            }
        }

        var edges = 0;
        var inside = 0;
        var asymmetric = 0;

        for (var half = 0; half < mesh.Triangles.Length; half++) {
            var twin = mesh.Twin(half);

            // Once per edge, and only for an edge that has two sides — an open rim is a cut for a
            // different reason and has no second patch to be equal to.
            if (twin < half || twin < 0) {
                continue;
            }

            if (!features.IsFeatureEdge(half) && !features.IsFeatureEdge(twin)) {
                continue;
            }

            edges++;

            if (features.IsFeatureEdge(half) != features.IsFeatureEdge(twin)) {
                asymmetric++;
            }

            if (patchOf[half / 3] >= 0 && patchOf[half / 3] == patchOf[twin / 3]) {
                inside++;
            }
        }

        Assert.True(edges > 0, $"{name}: the fixture has no feature edge, so it cannot make this point.");

        Assert.True(
            asymmetric == 0,
            $"{name}: {asymmetric} of {edges} feature edges are flagged on one half-edge and not the "
            + "other. PatchLayout.Flood tests one side of an edge at a time, so the fill would cross "
            + "such a crease in one direction and the patch would close round it."
        );

        Assert.True(
            inside == 0,
            $"{name}: {inside} of {edges} feature edges have the same patch on both sides, so a crease "
            + "runs through a patch's interior. docs/plan/41 § D4 makes a feature polyline a boundary "
            + "of the layout by construction, and a grid laid inside such a patch has no way to know "
            + "the crease is there."
        );
    }

    /// <summary>A fixture with no hard edge is the control, and it has no feature edge to bound.</summary>
    /// <remarks>
    ///     ⚠ It is asserted rather than assumed because the sphere carries the other half of every
    ///     comparison in <see cref="QuadQualityTests" />: if it ever acquired a detected crease, those
    ///     control figures would quietly stop meaning what they say.
    /// </remarks>
    [Fact]
    public void TheSphereHasNoFeatureEdgeAtAll() {
        RemesherTests.Layout("sphere", out var mesh, out var features);

        var found = 0;

        for (var half = 0; half < mesh.Triangles.Length; half++) {
            if (features.IsFeatureEdge(half)) {
                found++;
            }
        }

        Assert.True(
            found == 0,
            $"the sphere now has {found} feature half-edges. It is the control for every crease "
            + "comparison in QuadQualityTests, and those read as arguments only while it has none."
        );
    }
}

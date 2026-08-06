// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>The same mesh a millionfold apart in size must condition to the same topology.</summary>
/// <remarks>
///     <para>
///         <b>An absolute tolerance where there should be a relative one is the single most likely
///         bug in this stage, and this repository has been bitten by it twice.</b>
///         <c>EditMesh.DefaultWeldTolerance</c>'s remarks record it once — "relative to the mesh's own
///         size… a generator's seam is <c>cos 0</c> against <c>cos 2π</c>" — and doc 24 § P1 records
///         it again for the capsule's poles, where a fixed epsilon declared sixty-four real triangles
///         degenerate and "a primitive built at a tenth of the scale would have had all of them
///         declared so".
///     </para>
///     <para>
///         ⚠ <b>Every step is run, not just the weld.</b> An absolute number can hide in the sliver
///         bound, in the hole threshold, in the pre-remesh's target length, in the projector's cell
///         size and in the shrinkwrap's voxel size, and each of those is a place where somebody could
///         reasonably have written a constant. A test that only scaled the weld would find one sixth
///         of them.
///     </para>
///     <para>
///         The two factors are a thousandth and a thousand, so the same fixture is run six orders of
///         magnitude apart. Neither is an exact binary fraction, which is deliberate: a test that
///         scaled by powers of two would pass on a build where every threshold comparison sat exactly
///         on its boundary.
///     </para>
/// </remarks>
public class ScaleInvarianceTests {
    /// <summary>How much of the answer two scales of the same mesh have to agree on.</summary>
    /// <remarks>
    ///     ⚠ <b>Three strengths rather than one, and which one applies where is measured rather than
    ///     assumed.</b> Steps one to five are decisions about topology and area <i>ratios</i>, so
    ///     they agree to the last index. The pre-remesh's four operators are threshold comparisons on
    ///     lengths, so one round agrees on every count and five rounds do not — see
    ///     <see cref="FiveRoundsOfThePreRemeshStayWithinARateOfEachOther" /> for the numbers and for
    ///     why that is the fixture rather than the code.
    /// </remarks>
    public enum Agreement {
        /// <summary>Every count, every index, and every position scaled back.</summary>
        Exact,

        /// <summary>Every count in the report, and the triangle and vertex counts.</summary>
        Counts,

        /// <summary>The triangle and vertex counts, within a fraction of each other.</summary>
        Rate
    }

    const float Small = 1e-3f;
    const float Large = 1e+3f;

    /// <summary>Steps one to five, over the whole corpus, at both scales.</summary>
    [Fact]
    public void TheFirstFiveStepsGiveTheSameAnswerAtBothScales() {
        var settings = new ConditioningSettings {
            PreRemeshIterations = 0,
            FillHoles = true,
            HoleSize = 0.3f
        };

        foreach (var (name, mesh) in BrokenMeshes.Corpus()) {
            Compare(name, mesh, settings);
        }
    }

    /// <summary>Step six, whose four operators are all comparisons against a length. One round, exact.</summary>
    /// <remarks>
    ///     ⚠ <b>This is where an absolute constant would do the most damage and be hardest to see.</b>
    ///     Split above four thirds of the target and collapse below four fifths are the two
    ///     comparisons, and the target itself is derived from the mesh's own mean edge length — so
    ///     the whole loop is scale-free or none of it is. A hard-coded target would turn the
    ///     thousandth-scale mesh into one triangle and the thousand-scale one into a million, and one
    ///     round is enough to see that.
    /// </remarks>
    [Theory]
    [InlineData("staircase-sphere")]
    [InlineData("open-surface")]
    [InlineData("disconnected-pile")]
    [InlineData("non-manifold-edge")]
    [InlineData("two-components-40-percent")]
    public void OneRoundOfThePreRemeshGivesTheSameCountsAtBothScales(string name) {
        Compare(name, Find(name), new() { PreRemeshIterations = 1 }, Agreement.Counts);
    }

    /// <summary>Five rounds, where exact agreement is not achievable and the reason is not a constant.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Measured, and reported rather than papered over.</b> One round agrees on every
    ///         count at every scale, and on the whole index list for four of the five fixtures — the
    ///         disconnected pile differs in one index of three and a half thousand. From the second
    ///         round the counts separate too: on <see cref="BrokenMeshes.StaircaseSphere" />, five
    ///         rounds give 348 triangles at a thousandth scale against 394 at unit scale and 394 at a
    ///         thousand times — and on a smooth sphere, 434 against 418 and 418.
    ///     </para>
    ///     <para>
    ///         <b>That is float sensitivity in the fixture rather than an absolute tolerance in the
    ///         code.</b> The thousandth-scale mesh is not geometrically similar to the original:
    ///         <c>0.001f</c> is not a binary fraction, so each coordinate is perturbed by up to an
    ///         ulp in a direction that varies per coordinate. A staircase mesh has thousands of edges
    ///         of <i>exactly</i> equal length, so a threshold that any of them sits on breaks the tie
    ///         differently, and five rounds of split-collapse-flip amplify one different decision
    ///         into a different mesh. The unit and thousand-times runs agree exactly, which is the
    ///         tell: an absolute constant would not care which of the three it was given.
    ///     </para>
    ///     <para>
    ///         So the assertion here is a rate. An absolute tolerance in the pre-remesh is an
    ///         order-of-magnitude failure, not a twelve-percent one, and a bound of a quarter catches
    ///         it while leaving room for the tie-breaking that is genuinely there.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("staircase-sphere")]
    [InlineData("open-surface")]
    [InlineData("disconnected-pile")]
    public void FiveRoundsOfThePreRemeshStayWithinARateOfEachOther(string name) {
        Compare(name, Find(name), new() { PreRemeshIterations = 5 }, Agreement.Rate, 0.25f);
    }

    /// <summary>Step seven, whose grid resolution is derived from the model's own extent.</summary>
    /// <remarks>
    ///     Same reasoning as the five-round case, one stage further along: the winding number is a
    ///     sum of arc-tangents, and a cell whose value lands within an ulp of one half is a cell that
    ///     changes side under a change of scale. What must not move is which of the three the
    ///     shrinkwrap fires on and whether the answer is a closed surface.
    /// </remarks>
    [Fact]
    public void TheShrinkwrapGivesTheSameAnswerAtBothScales() {
        Compare(
            "self-intersecting",
            Find("self-intersecting"),
            new() { PreRemeshIterations = 0, Shrinkwrap = true },
            Agreement.Rate,
            0.05f
        );
    }

    /// <summary>
    ///     ⚠ And the guard on the guard: an absolute tolerance is only caught if the fixture actually
    ///     changes size.
    /// </summary>
    [Fact]
    public void TheFixturesReallyAreSixOrdersOfMagnitudeApart() {
        foreach (var (name, mesh) in BrokenMeshes.Corpus()) {
            if (mesh.PositionCount == 0) {
                continue;
            }

            var small = Diagonal(BrokenMeshes.Scaled(mesh, Small));
            var large = Diagonal(BrokenMeshes.Scaled(mesh, Large));

            if (small <= 0f) {
                continue;
            }

            Assert.True(large / small > 1e5f, $"{name} only grew by {large / small:E2}.");
        }
    }

    /// <summary>Conditions the same fixture at both scales and asserts they agree.</summary>
    /// <param name="name">Which fixture, for the failure message.</param>
    /// <param name="mesh">The fixture.</param>
    /// <param name="settings">What conditioning may do.</param>
    /// <param name="strength">How much has to agree. See <see cref="Agreement" />.</param>
    /// <param name="rate">The fraction the counts may differ by, for <see cref="Agreement.Rate" />.</param>
    static void Compare(
        string name,
        EditMesh mesh,
        ConditioningSettings settings,
        Agreement strength = Agreement.Exact,
        float rate = 0f
    ) {
        // ⚠ Triangulated once and scaled afterwards. Ear clipping is itself scale-sensitive — see
        // BrokenMeshes.Triangulated — so scaling first would make this a test of EditMesh.
        var flat = BrokenMeshes.Triangulated(mesh);

        var small = MeshConditioner.Condition(BrokenMeshes.Scaled(flat, Small), settings, out var one);
        var large = MeshConditioner.Condition(BrokenMeshes.Scaled(flat, Large), settings, out var two);

        // These four never move, at any scale and after any number of rounds: they are counts of
        // topological facts, and nothing about them is a length.
        Assert.True(
            one.Unorientable == two.Unorientable,
            $"{name}: unorientable {one.Unorientable} against {two.Unorientable}."
        );

        Assert.True(
            one.Shrinkwrapped == two.Shrinkwrapped,
            $"{name}: shrinkwrapped {one.Shrinkwrapped} against {two.Shrinkwrapped}."
        );

        Assert.True(
            one.Mesh.IsManifold && two.Mesh.IsManifold,
            $"{name}: manifold {one.Mesh.Describe()} against {two.Mesh.Describe()}."
        );

        Assert.True(
            one.Mesh.Boundary.Count == 0 == (two.Mesh.Boundary.Count == 0),
            $"{name}: one scale came out closed and the other did not."
        );

        if (strength == Agreement.Rate) {
            Near(name, "triangles", one.Triangles, two.Triangles, rate);
            Near(name, "vertices", small.VertexCount, large.VertexCount, rate);

            return;
        }

        Assert.True(one.Welded == two.Welded, $"{name}: welded {one.Welded} against {two.Welded}.");

        Assert.True(
            one.Reoriented == two.Reoriented,
            $"{name}: reoriented {one.Reoriented} against {two.Reoriented}."
        );

        Assert.True(
            one.Despecked == two.Despecked,
            $"{name}: de-specked {one.Despecked} against {two.Despecked}."
        );

        Assert.True(one.Cut == two.Cut, $"{name}: cut {one.Cut} against {two.Cut}.");
        Assert.True(one.Filled == two.Filled, $"{name}: filled {one.Filled} against {two.Filled}.");

        Assert.True(
            one.Triangles == two.Triangles,
            $"{name}: {one.Triangles} triangles against {two.Triangles}."
        );

        Assert.True(
            small.VertexCount == large.VertexCount,
            $"{name}: {small.VertexCount} vertices against {large.VertexCount}."
        );

        if (strength == Agreement.Counts) {
            return;
        }

        // The same indices in the same order, which is a stronger claim than the same counts: it
        // says the two runs took the same decisions in the same sequence and not merely the same
        // number of them.
        for (var index = 0; index < small.Triangles.Length; index++) {
            Assert.True(
                small.Triangles[index] == large.Triangles[index],
                $"{name}: index {index} is {small.Triangles[index]} against {large.Triangles[index]}."
            );
        }

        // And the geometry itself, scaled back. The tolerance is a fraction of the diagonal because
        // that is the only kind of tolerance this file is allowed to have.
        var ratio = Large / Small;
        var slack = MathF.Max(large.Diagonal, float.Epsilon) * 1e-4f;

        for (var index = 0; index < small.VertexCount; index++) {
            var expected = small.Positions[index] * ratio;
            var actual = large.Positions[index];

            Assert.True(
                Vector3.Distance(expected, actual) <= slack,
                $"{name}: vertex {index} is {expected} scaled up against {actual}."
            );
        }
    }

    static void Near(string name, string what, int one, int two, float rate) {
        var span = Math.Abs(one - two);
        var bound = Math.Max(one, two) * rate;

        Assert.True(
            span <= bound,
            $"{name}: {one} {what} against {two}, which is {span / MathF.Max(Math.Max(one, two), 1):P1} "
            + $"apart against a bound of {rate:P0}."
        );
    }

    static EditMesh Find(string name) => BrokenMeshes.Corpus().First(entry => entry.Name == name).Mesh;

    static float Diagonal(EditMesh mesh) {
        var box = mesh.Bounds;

        return (box.Maximum - box.Minimum).Length();
    }
}

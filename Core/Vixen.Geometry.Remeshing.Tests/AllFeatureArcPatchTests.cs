// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>The hole a cylinder keeps, and which repair is actually refusing to close it.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § D7's merge, characterised at the one input where it declines.</b> A patch
///         bounded by fewer than four arcs is divided if it can be and dissolved into a neighbour if it
///         cannot, and a cylinder has exactly one patch that gets neither: a single source triangle
///         whose three bounding arcs are one mesh edge each and all three of them feature arcs.
///     </para>
///     <para>
///         ⚠ <b><c>MergeTriangles</c> is not what stops it, and that was the recorded attribution.</b>
///         The patch is one triangle against a cap of four, so the cap passes; <c>Merge</c> returns
///         false because it will not dissolve a feature arc and every arc it is offered is one.
///         Raising the cap to sixteen was measured and changes nothing — the hole is the same six
///         boundary edges either way. Which is the correct refusal in isolation, since dissolving a
///         feature arc is deleting a crease; what is missing is the other answer, extracting a
///         three-sided patch as three quads round a centre point.
///     </para>
///     <para>
///         ⚠ <b>These are characterisation tests: the numbers are a defect, not a target.</b> When the
///         three-sided extraction lands, <see cref="TheCylindersHoleIsGone" /> starts failing and is
///         what to delete.
///     </para>
/// </remarks>
public class AllFeatureArcPatchTests {
    /// <summary>The hole is real, is made here rather than inherited, and is budget-dependent.</summary>
    /// <remarks>
    ///     ⚠ <b>Read off <c>Validate</c> rather than off a report field.</b> The whole class of bug
    ///     doc 41 § Part 4 exists to prevent is a result that is not watertight and says nothing, so a
    ///     test that asked the report whether it was solid would be assuming the thing under test.
    /// </remarks>
    [Theory]
    [InlineData(200, false)]
    [InlineData(400, false)]
    [InlineData(800, true)]
    [InlineData(2000, true)]
    public void TheCylindersHoleIsGone(int budget, bool solid) {
        var output = Remesher.Remesh(RemesherTests.Fixture("cylinder"), new() { TargetQuads = budget }, out _);
        var validated = output.Validate();

        Assert.Equal(solid, validated.IsSolid);

        // Six, and always six: one dropped triangle patch, whose neighbours' grid boundaries make the
        // rim. Pinned so that a change which makes the hole *bigger* is not read as the same defect.
        Assert.Equal(solid ? 0 : 6, validated.Boundary.Count);

        // ⚠ The half that is not a defect. A cylinder's input is closed, so every one of these edges
        // was made here — which is exactly the distinction the two warnings draw, and the reason this
        // fixture is worth a test where fifteen of the sixteen real meshes are not.
        Assert.Empty(validated.NonManifold);
    }

    /// <summary>The patch is dropped because its arcs are creases, not because it is too big to merge.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The refutation, read off the layout rather than asserted.</b> The recorded cause was
    ///         that <c>MergeTriangles</c> caps the merge. It does not: the cylinder's surviving
    ///         three-arc patch is a <i>single triangle</i>, a quarter of the cap, so the cap is not
    ///         what refuses it. What refuses it is that all three of its arcs are feature arcs and
    ///         <c>Merge</c> has nothing it is allowed to dissolve.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Looks at the layout the last repair round produced, which is the one whose patches
    ///         are dropped.</b> Earlier rounds still have the same patch and still fail to repair it —
    ///         instrumenting the drop site showed it on all six.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheDroppedPatchIsAllFeatureArcsAndFarUnderTheMergeCap() {
        var layout = RemesherTests.Layout("cylinder", out _, out _);

        // Every patch that came out usable has four sides. The one that did not is not in `Patches` —
        // so what this looks for is the arc that no usable patch claims from both directions, which is
        // the rim of the hole.
        var claimed = new Dictionary<int, int>();

        foreach (var patch in layout.Patches) {
            foreach (var side in patch.Sides) {
                foreach (var use in side) {
                    claimed[use.Arc] = claimed.GetValueOrDefault(use.Arc) + 1;
                }
            }
        }

        var orphaned = claimed.Where(pair => pair.Value < 2).Select(pair => pair.Key).ToList();

        Assert.NotEmpty(orphaned);

        // ⚠ The whole of the refutation: every arc bounding the missing patch is a feature arc. An arc
        // that was not one would have been dissolved, whatever the patch's triangle count.
        Assert.All(orphaned, arc => Assert.True(
            layout.Arcs[arc].IsFeature,
            $"arc {arc} bounds the dropped patch and is not a feature arc, so Merge could have "
            + "dissolved it and the recorded MergeTriangles attribution would be back in play."
        ));

        // And each is a single mesh edge, which is why Divide cannot add a fourth corner either.
        Assert.All(orphaned, arc => Assert.Equal(1, layout.Arcs[arc].EdgeCount));
    }
}

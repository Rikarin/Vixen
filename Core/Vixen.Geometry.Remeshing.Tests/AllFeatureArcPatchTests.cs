// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>The hole a cylinder used to keep, and the third repair that closes it.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § D7's merge, characterised at the one input where it declines.</b> A patch
///         bounded by fewer than four arcs is divided if it can be and dissolved into a neighbour if it
///         cannot, and a cylinder has exactly one patch that gets neither: a single source triangle
///         whose three bounding arcs are one mesh edge each and all three of them feature arcs.
///     </para>
///     <para>
///         ⚠ <b><c>MergeTriangles</c> was never what stopped it, and that was the recorded
///         attribution.</b> The patch is one triangle against a cap of four, so the cap passes;
///         <c>Merge</c> returned false because it will not dissolve a feature arc and every arc it is
///         offered is one. Raising the cap to sixteen was measured and changed nothing. Which is the
///         correct refusal in isolation, since dissolving a feature arc is deleting a crease; what was
///         missing is the other answer, and it has landed — <c>PatchLayout</c> carries a three-arc patch
///         as a <i>fan</i> and <c>PatchExtractor</c> fills it as three quads round a centre.
///     </para>
///     <para>
///         ⚠ <b>These were characterisation tests of the defect and are now assertions about the
///         repair.</b> <see cref="TheCylindersHoleIsGone" /> asserted six boundary edges at the low
///         budgets; it asserts none, at every budget, and the fan is what changed.
///     </para>
/// </remarks>
public class AllFeatureArcPatchTests {
    /// <summary>The cylinder comes back watertight at every budget, including the two that did not.</summary>
    /// <remarks>
    ///     ⚠ <b>Read off <c>Validate</c> rather than off a report field.</b> The whole class of bug
    ///     doc 41 § Part 4 exists to prevent is a result that is not watertight and says nothing, so a
    ///     test that asked the report whether it was solid would be assuming the thing under test.
    /// </remarks>
    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(800)]
    [InlineData(2000)]
    public void TheCylindersHoleIsGone(int budget) {
        var output = Remesher.Remesh(RemesherTests.Fixture("cylinder"), new() { TargetQuads = budget }, out _);
        var validated = output.Validate();

        Assert.True(validated.IsSolid);

        // The two low budgets used to keep exactly six of these: one dropped triangle patch, whose
        // neighbours' grid boundaries made the rim. Pinned at zero so a fan that fills only part of
        // its patch is not read as a pass.
        Assert.Empty(validated.Boundary);

        // ⚠ The half that was never a defect. A cylinder's input is closed, so every one of those
        // edges was made here — which is exactly the distinction the two warnings draw, and the reason
        // this fixture is worth a test where fifteen of the sixteen real meshes are not.
        Assert.Empty(validated.NonManifold);
    }

    /// <summary>The patch is still all creases and still one triangle — and is now in the layout.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The refutation of the <c>MergeTriangles</c> attribution, kept because it is what
    ///         pointed at the fan.</b> The cylinder's surviving three-arc patch is a <i>single
    ///         triangle</i>, a quarter of the cap, so the cap is not what refuses it; what refuses it is
    ///         that all three of its arcs are feature arcs and <c>Merge</c> has nothing it is allowed to
    ///         dissolve. Both facts are asserted off the layout rather than argued.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the patch is no longer missing, which is the assertion the old version could
    ///         not make.</b> It looked for the arc that no usable patch claimed from both directions —
    ///         the rim of the hole — and found three. There are none now: the fan claims its three arcs
    ///         exactly as any other patch claims its sides, so every arc in the layout is claimed twice.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheThreeCreasedArcsAreCarriedAsAFanRatherThanDropped() {
        var layout = RemesherTests.Layout("cylinder", out _, out _);
        var fans = layout.Patches.Where(patch => patch.IsFan).ToList();

        Assert.NotEmpty(fans);

        foreach (var fan in fans) {
            // One triangle, a quarter of `MergeTriangles` — so the cap is not what left it here.
            Assert.Single(fan.Triangles);
            Assert.Equal(3, fan.Sides.Length);

            foreach (var side in fan.Sides) {
                var use = Assert.Single(side);

                // ⚠ The whole of the refutation: every arc bounding it is a feature arc. An arc that
                // was not one would have been dissolved, whatever the patch's triangle count.
                Assert.True(
                    layout.Arcs[use.Arc].IsFeature,
                    $"arc {use.Arc} bounds the fan and is not a feature arc, so Merge could have "
                    + "dissolved it and the recorded MergeTriangles attribution would be back in play."
                );

                // And each is a single mesh edge, which is why Divide cannot add a fourth corner either.
                Assert.Equal(1, layout.Arcs[use.Arc].EdgeCount);
            }
        }

        var claimed = new Dictionary<int, int>();

        foreach (var patch in layout.Patches) {
            foreach (var side in patch.Sides) {
                foreach (var use in side) {
                    claimed[use.Arc] = claimed.GetValueOrDefault(use.Arc) + 1;
                }
            }
        }

        Assert.Empty(claimed.Where(pair => pair.Value < 2).Select(pair => pair.Key));
    }

    /// <summary>The fan's three spokes are what make its three sides quadrangulable at all.</summary>
    /// <remarks>
    ///     ⚠ <b>Parity is not a preference and no all-quad filling exists without it.</b> A quad mesh
    ///     of a disc has an even number of boundary edges, so a three-sided patch whose sides come to an
    ///     odd total cannot be filled however cleverly — and the counts the router would have chosen for
    ///     the cylinder's patch without the fan in the system are 1, 1 and 1. Solving for the three
    ///     spokes instead makes the sides <c>a + c</c>, <c>a + b</c> and <c>b + c</c>, whose total is
    ///     <c>2(a + b + c)</c> and so even by construction, with the strict triangle inequality falling
    ///     out of the spokes' floor of one.
    /// </remarks>
    [Fact]
    public void EveryFanSideIsTheSumOfTheTwoSpokesItRunsBetween() {
        var layout = RemesherTests.Layout("cylinder", out _, out _);
        var quantization = Quantizer.Solve(layout);

        Assert.True(quantization.IsFeasible, string.Join(" · ", quantization.Warnings));

        var fans = 0;

        for (var patch = 0; patch < layout.Patches.Count; patch++) {
            if (!layout.Patches[patch].IsFan) {
                continue;
            }

            fans++;

            var (a, b, c) = quantization.Spokes[patch];

            Assert.True(a > 0 && b > 0 && c > 0, $"the fan quantized to spokes {a}, {b}, {c}.");

            int[] wanted = [a + c, a + b, b + c];

            for (var at = 0; at < 3; at++) {
                Assert.Equal(
                    wanted[at],
                    layout.Patches[patch].Sides[at].Sum(use => quantization.Counts[use.Arc])
                );
            }
        }

        Assert.True(fans > 0, "the cylinder's layout has no fan in it, so this asserts nothing.");
    }
}

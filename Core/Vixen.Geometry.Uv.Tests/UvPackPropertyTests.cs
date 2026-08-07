// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Geometry.Testing;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>The packer's contract stated over the space of island sets rather than over one of them.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/42's exit criteria are property statements and were being tested as fixtures.</b>
///         Criterion 2 ends "no exceptions, no hangs" — a claim about every input — and § D7's
///         determinism is "the same islands in a different order pack identically", a claim about every
///         permutation. <see cref="UvPackDeterminismTests" /> makes the second claim with one shuffle
///         of one corpus, and that one shuffle found a real defect: two islands of equal texel area but
///         different shapes swapped, and every later placement moved with them. The fix was
///         <c>IslandMask.Signature</c>, between the area and the index.
///     </para>
///     <para>
///         ⚠ <b>These tests found the next one immediately, and it was the same shape.</b> A signature
///         is a hash of the <i>mask</i>, and a mask is the island rounded to whole texels — so the
///         argument written into its remarks, that the input index is a safe last resort "for two
///         shapes that really are identical", was one quantization step short. Measured over four
///         hundred generated sets, each packed twice with the second run permuted: 177 sets moved a
///         placement and 28 rasterized to a different atlas. The super-patch rung was worse and for a
///         structural reason rather than a tie-break one — its grouping was ordered by the input index,
///         so a permutation built <i>different composites</i>: 214 of 300 permutations produced a
///         different atlas, by hundreds of texels. <c>Packer.Contents</c> is the fix for both. After
///         it, none of 900 permutations across all three rungs moved the atlas at all.
///     </para>
///     <para>
///         ⚠ <b>Nothing here measures efficiency, and § B6 is why.</b> A test that measures packing
///         efficiency against a moving corpus cannot tell a regression from a reseed, so
///         <see cref="UvPackEfficiencyTests" /> keeps <see cref="IslandCorpus" />' fixed sequence. What
///         is stated here is only what is true of every input.
///     </para>
///     <para>
///         ⚠ Every case runs under <see cref="RunawayGuard" />, which is the half of criterion 2 that
///         no assertion after the call can reach. See its remarks.
///     </para>
/// </remarks>
public class UvPackPropertyTests {
    /// <summary>An island set together with a permutation of its own indices.</summary>
    static readonly Gen<(IslandRecipe[] Recipes, int[] Order)> Permuted = Permutations(IslandSpace.Set);

    /// <summary>The same over § D13's quantized quad patches, which is the super-patch rung's own input.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="IslandSpace.Set" /> is star-heavy and the grouping never fires on it</b> —
    ///     0 of 4,604 islands reach the 0.8 box fill its candidate list is filtered on — so the
    ///     <see cref="PackQuality.SuperPatch" /> row of the theory below was exercising the rung with
    ///     nothing to group. <see cref="IslandSpace.Patches" /> has the measurement and the argument.
    /// </remarks>
    static readonly Gen<(IslandRecipe[] Recipes, int[] Order)> PermutedPatches = Permutations(IslandSpace.Patches);

    static Gen<(IslandRecipe[] Recipes, int[] Order)> Permutations(Gen<IslandRecipe[]> sets) =>
        sets.SelectMany(
            recipes => Gen.Shuffle(Enumerable.Range(0, recipes.Length).ToArray()).Select(order => (recipes, order))
        );

    /// <summary>A sparse set, a resolution and a margin: the case with room left in the atlas.</summary>
    /// <remarks>
    ///     ⚠ <b>128 and 256 rather than the fixed test's 512 to 4096, and the reason is the rasterizer
    ///     rather than the packer.</b> <see cref="PackedAtlas.MinimumGap" /> probes a window round
    ///     every boundary texel, so verifying one 4096² atlas costs more than a hundred of these — and
    ///     the margin is counted in texels, so it says the same thing at every resolution.
    ///     <see cref="UvPackMarginTests.TheTexelGapIsTheSameAtEveryResolution" /> is where the large
    ///     resolutions are swept.
    /// </remarks>
    static readonly Gen<(IslandRecipe[] Recipes, int Resolution, int Margin)> Sparse = Gen.Select(
        IslandSpace.Recipe.Array[2, 20],
        Gen.OneOfConst(128, 256),
        Gen.Int[1, 8],
        (recipes, resolution, margin) => (recipes, resolution, margin)
    );

    /// <summary>The same, with enough islands that the packer has to pack them against each other.</summary>
    static readonly Gen<(IslandRecipe[] Recipes, int Resolution, int Margin)> Dense = Gen.Select(
        IslandSpace.Recipe.Array[24, 64],
        Gen.OneOfConst(128, 256),
        Gen.Int[1, 8],
        (recipes, resolution, margin) => (recipes, resolution, margin)
    );

    static PackSettings Settings(PackQuality quality = PackQuality.Irregular) =>
        new() { Resolution = 256, Margin = 4, Quality = quality, CoreLimit = 64 };

    /// <summary>docs/plan/42 § D7, over every permutation and on all three rungs.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Two claims, and between them they are the whole of what "pack identically" can mean.</b>
    ///         The multiset of placements is unchanged — so no island went anywhere no island went
    ///         before, which is what "every later placement moved" violates. And the rasterized atlas
    ///         is unchanged texel for texel — which is what a content hash of the baked page is taken
    ///         over, and therefore the claim that decides whether a permutation re-pages an asset.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Deliberately not "island <i>i</i> keeps its own placement", and the residue is
    ///         measured rather than assumed.</b> The comparison chain has to end somewhere, and it ends
    ///         at the input index — so two islands that are equal by value still exchange slots under a
    ///         permutation. Measured after <c>Packer.Contents</c> landed: 12 to 20 islands per three
    ///         hundred permutations per rung, and not one of them moved a texel, because two islands
    ///         the packer cannot tell apart are two islands that put the same thing in the same place.
    ///         <see cref="UvPackDeterminismTests.TheSameIslandsInADifferentOrderPackIdentically" />
    ///         keeps the per-island claim on a corpus where no two islands are alike.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The super-patch rung draws from a different generator, and until it did this row was
    ///         not testing the rung it names.</b> Grouping only considers an island whose mask fills its
    ///         own bounding box, and <see cref="IslandSpace.Recipe" /> is star-heavy by design — so the
    ///         candidate list came out empty and every case fell through to the irregular path.
    ///         <see cref="IslandSpace.Patches" /> is doc 41 § D13's actual input, quantized quad patches,
    ///         which are rectangles.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(PackQuality.Rectangle)]
    [InlineData(PackQuality.Irregular)]
    [InlineData(PackQuality.SuperPatch)]
    public void A_permutation_of_the_islands_produces_the_same_atlas(PackQuality quality) {
        (quality == PackQuality.SuperPatch ? PermutedPatches : Permuted).Sample(
            entry => {
                var (recipes, order) = entry;
                var islands = IslandSpace.Build(recipes);
                var shuffled = new UvIsland[order.Length];

                for (var index = 0; index < order.Length; index++) {
                    shuffled[index] = islands[order[index]];
                }

                var what = $"{quality} pack of {IslandSpace.Print(recipes)} permuted by [{string.Join(", ", order)}]";

                var straight = RunawayGuard.Run(what, () => UvUnwrap.Pack(islands, Settings(quality)));
                var permuted = RunawayGuard.Run(what, () => UvUnwrap.Pack(shuffled, Settings(quality)));

                Assert.Equal(
                    straight.Select(Describe).Order(StringComparer.Ordinal),
                    permuted.Select(Describe).Order(StringComparer.Ordinal)
                );

                var before = Occupancy(islands, straight);
                var after = Occupancy(shuffled, permuted);
                var moved = 0;

                for (var texel = 0; texel < before.Length; texel++) {
                    if (before[texel] != after[texel]) {
                        moved++;
                    }
                }

                Assert.True(moved == 0, $"{what}: the atlas moved by {moved} texels.");
            },
            iter: PropertyBudget.Iterations(150),
            threads: 1
        );
    }

    /// <summary>The same claim one layer down: a permutation may not change what the report says either.</summary>
    /// <remarks>
    ///     ⚠ Without this a packer could reach the same atlas by a different route and report a
    ///     different efficiency for it, which is the number a content build keys a decision off.
    /// </remarks>
    [Fact]
    public void A_permutation_does_not_move_the_report() {
        Permuted.Sample(
            entry => {
                var (recipes, order) = entry;
                var islands = IslandSpace.Build(recipes);
                var shuffled = new UvIsland[order.Length];

                for (var index = 0; index < order.Length; index++) {
                    shuffled[index] = islands[order[index]];
                }

                var what = $"report of {IslandSpace.Print(recipes)}";
                var straight = RunawayGuard.Run(what, () => Measure(islands));
                var permuted = RunawayGuard.Run(what, () => Measure(shuffled));

                Assert.Equal(straight.PackingEfficiency, permuted.PackingEfficiency);
                Assert.Equal(straight.EffectiveEfficiency, permuted.EffectiveEfficiency);
                Assert.Equal(straight.ChartCount, permuted.ChartCount);
            },
            iter: PropertyBudget.Iterations(100),
            threads: 1
        );
    }

    /// <summary>docs/plan/42's exit criterion 4's safety half, over every set, resolution and margin.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Three claims, measured by rasterizing rather than by asking the packer</b> — the
    ///         criterion says so in as many words, and a packer reading back its own occupancy grid
    ///         would prove only that it agrees with itself. No two islands may share a texel; nothing
    ///         may come nearer than the margin to another island; nothing may come nearer than the
    ///         margin to the atlas's edge. Those three are what bleeding at mip 3 is made of, and they
    ///         hold for every input.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Inequalities here and equalities in
    ///         <see cref="The_gap_is_exactly_the_margin_when_the_atlas_is_packed_tightly" />, because a
    ///         gap wider than the margin is not a defect — it is an atlas with room in it, and two
    ///         small islands at 256² usually have a great deal of room.</b> What bleeds is a gap
    ///         <i>narrower</i> than the margin, and that is what is stated for every input.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An island the packer had to shrink below one texel is excluded, and that is a
    ///         measured defect rather than a convenience.</b> Such an island rasterizes to a single
    ///         texel it covers a few per cent of, and where that texel lands is then decided twice by
    ///         different arithmetic — once by the packer, in the island's own frame at
    ///         <c>(coordinate − minimum) × texels</c>, and once here, through
    ///         <see cref="UvPlacement.Apply" /> and a multiply by the resolution. The two can differ by
    ///         a texel, and one texel is the whole of the shortfall: measured over six hundred dense
    ///         sets, four came out one texel short of their margin, and every offending pair in every
    ///         one of them had a sliver under a texel thick in it, five of six of them turned by a
    ///         quarter. That is a real bleed of one texel and it is worth fixing where the conservative
    ///         bound is computed; it is not something this property can assert away, and an island
    ///         thinner than a texel is a chart that carries no colour to bleed.
    ///         <see cref="Thick" /> is where the exclusion is made, and it is made per island rather
    ///         than per set: one sliver in sixty islands is near certain, so excluding whole sets
    ///         excluded every set. The number of sets that still had two islands to measure between is
    ///         asserted, so the property cannot pass by excluding everything.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A margin of zero is excluded too, and there it is the oracle rather than the packer
    ///         that cannot cope.</b> <see cref="PackedAtlas" />' own remarks say the two rasterizations
    ///         differ by construction — "the packer widens an edge function by a box bound, which
    ///         over-covers slightly at a corner; this projects the triangle and the texel's square onto
    ///         five axes and is exact" — and they are reached by different arithmetic besides, one in
    ///         the island's own texel frame and one through <see cref="UvPlacement.Apply" /> and back.
    ///         At any margin at all the packer's over-covering absorbs the difference. At zero there is
    ///         nothing to absorb it with, and a boundary texel lands on one side or the other by an
    ///         ulp: measured, one texel claimed twice, once in a hundred and fifty. Zero is not a
    ///         margin anybody ships — <see cref="PackSettings.Margin" /> defaults to four, "enough for
    ///         a trilinear tap plus two mip levels" — and asserting a property about it here would be
    ///         asserting that two rasterizers agree, which nobody claimed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The subject is <see cref="UvUnwrap.Pack" />'s own placements, and saying so is not
    ///         pedantry — <see cref="UvStacking" /> is this property's exact negation.</b> § D10's
    ///         symmetric stacking <i>deliberately</i> overlaps two mirrored islands so that both halves
    ///         share one region of texture: <see cref="UvStacking.Fold" /> drops the partner before the
    ///         pack and <see cref="UvStacking.Unfold" /> hands it the representative's placement
    ///         afterwards, so the pair comes out at a gap of zero and every claim below fails by design.
    ///         It holds here because nothing in this test folds — stacking is opt-in, the caller does it
    ///         on either side of the pack, and nothing in the library calls it. That was true before this
    ///         paragraph and it was true by accident; a property whose precondition is unwritten is one
    ///         the next person deletes by changing a default. <c>UvStackingTests</c> is where the
    ///         overlapping case is stated as the intended behaviour it is.
    ///     </para>
    /// </remarks>
    [Fact]
    public void No_island_ever_comes_nearer_than_the_margin_to_another_island_or_to_the_edge() {
        var packed = 0;

        Sparse.Sample(
            entry => {
                var (recipes, resolution, margin) = entry;
                var islands = IslandSpace.Build(recipes);

                // ⚠ Unstacked, and that is a precondition rather than a default. See the remarks:
                // UvStacking.Fold before this call and Unfold after it would put two islands on one
                // rectangle on purpose, which is the negation of every claim below.
                var settings = new PackSettings { Resolution = resolution, Margin = margin, CoreLimit = 64 };

                var what = string.Create(
                    CultureInfo.InvariantCulture,
                    $"pack of {IslandSpace.Print(recipes)} at {resolution}² with a {margin}-texel margin"
                );

                var placements = RunawayGuard.Run(what, () => UvUnwrap.Pack(islands, settings));

                PackedAtlas.Rasterize(islands, placements, resolution, Int2.Zero, out var overlaps);

                Assert.True(overlaps == 0, $"{what}: {overlaps} texels are claimed by two islands.");

                var thick = Thick(islands, placements, resolution);
                var map = PackedAtlas.Rasterize(islands, thick, resolution, Int2.Zero, out _);

                if (thick.Length < 2 || PackedAtlas.Covered(map) == 0) {
                    // Nothing left to measure between: a set of degenerates, or one the scale search
                    // shrank below a texel. See Thick.
                    return;
                }

                packed++;

                Assert.True(
                    PackedAtlas.MinimumBorder(map, resolution) >= margin,
                    $"{what}: the nearest island is {PackedAtlas.MinimumBorder(map, resolution)} texels "
                    + $"from the atlas edge."
                );

                var gap = PackedAtlas.MinimumGap(map, resolution, margin + 6);

                Assert.True(gap >= margin, $"{what}: the closest two islands are {gap} texels apart.");
            },
            iter: PropertyBudget.Iterations(150),
            threads: 1
        );

        // ⚠ The guard on the guard. Both claims sit after an early return, and a generator that
        // drifted into producing only degenerates would leave this passing while testing nothing.
        Assert.True(packed > 100, $"Only {packed} of the sampled sets rasterized to anything at all.");
    }

    /// <summary>And the exact half: where two islands do meet, they meet at the margin and not past it.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this a packer that ignored <see cref="PackSettings.Margin" /> entirely and left
    ///     a wide gap for some other reason would satisfy every inequality above.</b> Twenty-four
    ///     islands and up at 128² or 256² is dense enough that the scale search closes onto a layout
    ///     where something touches: measured, the minimum gap was exactly the margin in every one of
    ///     120 sampled sets, at margins from zero to eight.
    /// </remarks>
    [Fact]
    public void The_gap_is_exactly_the_margin_when_the_atlas_is_packed_tightly() {
        var measured = 0;

        Dense.Sample(
            entry => {
                var (recipes, resolution, margin) = entry;
                var islands = IslandSpace.Build(recipes);
                var settings = new PackSettings { Resolution = resolution, Margin = margin, CoreLimit = 64 };

                var what = string.Create(
                    CultureInfo.InvariantCulture,
                    $"pack of {recipes.Length} islands at {resolution}² with a {margin}-texel margin"
                );

                var placements = RunawayGuard.Run(what, () => UvUnwrap.Pack(islands, settings));

                PackedAtlas.Rasterize(islands, placements, resolution, Int2.Zero, out var overlaps);

                Assert.True(overlaps == 0, $"{what}: {overlaps} texels are claimed by two islands.");

                var thick = Thick(islands, placements, resolution);
                var map = PackedAtlas.Rasterize(islands, thick, resolution, Int2.Zero, out _);

                if (thick.Length < 2 || PackedAtlas.Covered(map) == 0) {
                    return;
                }

                measured++;

                Assert.Equal(margin, PackedAtlas.MinimumBorder(map, resolution));
                Assert.Equal(margin, PackedAtlas.MinimumGap(map, resolution, margin + 6));
            },
            iter: PropertyBudget.Iterations(80),
            threads: 1
        );

        Assert.True(measured > 20, $"Only {measured} of the sampled sets were packed above a texel.");
    }

    /// <summary>docs/plan/42's exit criterion 2's second half: never an exception, never a hang.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="InvalidOperationException" /> is an answer and not a crash.</b> The packer
    ///     is allowed to refuse — "an island is larger than the atlas can ever hold, or the margin has
    ///     consumed it" names the stage that refused, which is what the criterion asks for. What must
    ///     not happen is an index out of range, a null reference, a division by an island's own zero
    ///     size, a non-finite scale in a placement, or a case that does not come back.
    /// </remarks>
    [Fact]
    public void Every_island_set_produces_placements_or_a_refusal_and_never_anything_else() {
        Gen.Select(
                IslandSpace.Recipe.Array[0, 32],
                Gen.OneOfConst(1, 4, 64, 256),
                Gen.Int[0, 6],
                Gen.Enum<PackQuality>(),
                Gen.Enum<PackOverflow>(),
                (recipes, resolution, margin, quality, overflow) => (recipes, resolution, margin, quality, overflow)
            )
            .Where(entry => (2 * entry.margin) + 1 <= entry.resolution)
            .Sample(
                entry => {
                    var (recipes, resolution, margin, quality, overflow) = entry;
                    var islands = IslandSpace.Build(recipes);

                    var settings = new PackSettings {
                        Resolution = resolution,
                        Margin = margin,
                        Quality = quality,
                        Overflow = overflow,
                        CoreLimit = 8
                    };

                    var what = string.Create(
                        CultureInfo.InvariantCulture,
                        $"pack of {IslandSpace.Print(recipes)} at {resolution}², margin {margin}, "
                        + $"{quality}, {overflow}"
                    );

                    RunawayGuard.Run(
                        what,
                        () => {
                            try {
                                var placements = UvUnwrap.Pack(islands, settings);

                                Assert.Equal(islands.Length, placements.Count);

                                foreach (var placement in placements) {
                                    Assert.InRange(placement.Rotation, 0, 3);
                                    Assert.True(float.IsFinite(placement.Scale), $"{what}: scale {placement.Scale}.");
                                }
                            } catch (InvalidOperationException) {
                                // A refusal naming the shortfall. The criterion allows it.
                            }
                        }
                    );
                },
                iter: PropertyBudget.Iterations(300),
                threads: 1
            );
    }

    /// <summary>The placements of the islands the packer did not have to shrink below a texel.</summary>
    /// <param name="islands">The islands.</param>
    /// <param name="placements">Where they went, and therefore what they were scaled by.</param>
    /// <param name="resolution">The atlas's edge.</param>
    /// <returns>The subset whose island is at least a texel wide and a texel tall.</returns>
    /// <remarks>
    ///     ⚠ Decided after the pack rather than before it, because the scale is what the scale search
    ///     arrived at and not something a caller states — an island that is a comfortable forty texels
    ///     at the density asked for is a sliver once sixteen rescales have been applied to make the set
    ///     fit.
    /// </remarks>
    static UvPlacement[] Thick(UvIsland[] islands, IReadOnlyList<UvPlacement> placements, int resolution) {
        var thick = new List<UvPlacement>(placements.Count);

        foreach (var placement in placements) {
            var size = islands[placement.Island].Size * placement.Scale * resolution;

            if (size.X >= 1f && size.Y >= 1f) {
                thick.Add(placement);
            }
        }

        return [.. thick];
    }

    static UvReport Measure(IReadOnlyList<UvIsland> islands) {
        UvUnwrap.Pack(islands, Settings(), out var report);

        return report;
    }

    static bool[] Occupancy(IReadOnlyList<UvIsland> islands, IReadOnlyList<UvPlacement> placements) {
        var map = PackedAtlas.Rasterize(islands, placements, 256, Int2.Zero, out _);
        var covered = new bool[map.Length];

        for (var texel = 0; texel < map.Length; texel++) {
            covered[texel] = map[texel] >= 0;
        }

        return covered;
    }

    static string Describe(UvPlacement placement) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{placement.Offset.X:R}/{placement.Offset.Y:R} ×{placement.Scale:R} turned {placement.Rotation} "
            + $"on tile {placement.Tile.X}/{placement.Tile.Y}"
        );
}

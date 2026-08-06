// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>docs/plan/42 § D10: symmetric stacking, opt-in, offered rather than applied.</summary>
/// <remarks>
///     <para>
///         <b>Two islands the same shape can share one region of texture, halving what they cost.</b>
///         ⚠ <b>It is off by default and the tests are as much about that as about the matching</b> —
///         stacking forbids asymmetric detail, and a library that stacked a character's two boots
///         because they happened to match would be discovered by the artist who scuffed one of them.
///     </para>
///     <para>
///         ⚠ <b>Doc 41 § D11's exact mirror is what makes the match an equality.</b> The fixtures here
///         are built the way a symmetry-preserving remesh produces them — the partner's corners in the
///         same order as the representative's — because that is the case § D10 says detection is
///         reliable for, and the limitation is asserted as well as the capability.
///     </para>
/// </remarks>
public class UvStackingTests {
    /// <summary>Nothing is stacked unless somebody asks, which is what "opt-in" means in code.</summary>
    /// <remarks>
    ///     ⚠ <b>The packer never calls the detector.</b> A pack of two identical islands places them
    ///     side by side, at two offsets, exactly as it would place two different ones — so the only way
    ///     to get stacking is <see cref="UvStacking.Fold" />, which a caller has to type.
    /// </remarks>
    [Fact]
    public void ThePackerDoesNotStackAnythingOnItsOwn() {
        var island = IslandCorpus.Square(0.25f, 8f);
        var placements = UvUnwrap.Pack([island, island], new() { Resolution = 256, Margin = 4 });

        Assert.Equal(2, placements.Count);
        Assert.NotEqual(placements[0].Offset, placements[1].Offset);
    }

    /// <summary>Two copies of one island are offered as a pair, exactly.</summary>
    [Fact]
    public void AnExactDuplicateIsOfferedAtZeroResidual() {
        var island = IslandCorpus.Square(0.25f, 8f);
        var offers = UvStacking.Detect([island, IslandCorpus.Square(0.5f, 8f), island]);

        var offer = Assert.Single(offers);

        Assert.Equal(0, offer.Representative);
        Assert.Equal(2, offer.Partner);
        Assert.Equal(0f, offer.Residual);
    }

    /// <summary>A mirror image is offered as a mirror, and nothing else is.</summary>
    /// <remarks>
    ///     ⚠ <b>The mirror is in <c>u</c> and the residual is taken after both islands are put in their
    ///     own lower corner</b> — which is the same normalization <see cref="UvPlacement.Apply" />
    ///     makes. Comparing raw coordinates would measure where the flattener left the gauge, and a
    ///     conformal map's gauge is arbitrary.
    /// </remarks>
    [Fact]
    public void AMirrorImageIsOfferedAsOne() {
        var island = Wedge(false);
        var mirrored = Wedge(true);
        var offers = UvStacking.Detect([island, mirrored]);
        var offer = Assert.Single(offers);

        Assert.True(offer.Mirrored, $"the pair matched straight at {offer.Residual:E3} rather than mirrored.");
        Assert.Equal(0f, offer.Residual, 5);

        // ⚠ And the reflection is load-bearing rather than incidental: the same island against itself
        // is offered *without* the mirror, so the flag tracks which comparison actually won. A wedge
        // symmetric about its own centre would match either way and prove neither.
        Assert.False(Assert.Single(UvStacking.Detect([island, island])).Mirrored);
    }

    /// <summary>Islands that are not the same shape are not offered, whatever the tolerance.</summary>
    [Fact]
    public void DistinctIslandsAreNotOffered() {
        var islands = IslandCorpus.Trellis(48);

        Assert.Empty(UvStacking.Detect(islands));
        Assert.Empty(UvStacking.Detect(islands, 1e-2f));
    }

    /// <summary>A folded pair shares one region of the atlas, exactly.</summary>
    /// <remarks>
    ///     <b>What stacking <i>is</i>, in one assertion:</b> the partner gets the representative's
    ///     offset, scale, rotation and tile, so both islands' coordinates land on the same rectangle.
    ///     ⚠ A bake then has to write one of them and not both, which is the cost § D10 says is paid at
    ///     texturing time rather than at packing time.
    /// </remarks>
    [Fact]
    public void AFoldedPairSharesOneRegion() {
        var island = Wedge(false);
        var islands = new[] { island, IslandCorpus.Square(0.4f, 8f), Wedge(true), IslandCorpus.Square(0.2f, 8f) };
        var offers = UvStacking.Detect(islands);

        Assert.Single(offers);

        var folded = UvStacking.Fold(islands, offers, out var source);

        Assert.Equal(3, folded.Count);
        Assert.Equal(source[0], source[2]);
        Assert.NotEqual(source[0], source[1]);

        var settings = new PackSettings { Resolution = 256, Margin = 4 };
        var placements = UvStacking.Unfold(UvUnwrap.Pack(folded, settings), source);

        Assert.Equal(islands.Length, placements.Count);

        for (var index = 0; index < placements.Count; index++) {
            Assert.Equal(index, placements[index].Island);
        }

        Assert.Equal(placements[0].Offset, placements[2].Offset);
        Assert.Equal(placements[0].Scale, placements[2].Scale);
        Assert.Equal(placements[0].Rotation, placements[2].Rotation);
        Assert.Equal(placements[0].Tile, placements[2].Tile);
    }

    /// <summary>Stacking buys atlas, which is the entire reason to accept its cost.</summary>
    [Fact]
    public void StackingLeavesRoomTheUnstackedPackDoesNotHave() {
        var islands = new List<UvIsland>();

        for (var pair = 0; pair < 12; pair++) {
            var wedge = Wedge(false, 0.1f + (0.02f * pair));

            islands.Add(wedge);
            islands.Add(Wedge(true, 0.1f + (0.02f * pair)));
        }

        // ⚠ A pinned density, and that is what makes the comparison mean anything. Left at zero the
        // packer searches a global scale and grows whatever it is given until the sheet is full, so a
        // pack of half the islands comes back at the same efficiency and a larger scale — which reads
        // as "stacking bought nothing" and is really "the metric measured the search".
        var settings = new PackSettings { Resolution = 512, Margin = 4, TexelDensity = 2400f };
        var offers = UvStacking.Detect(islands);

        Assert.Equal(12, offers.Count);

        UvUnwrap.Pack(islands, settings, out var flat);

        var folded = UvStacking.Fold(islands, offers, out _);

        UvUnwrap.Pack(folded, settings, out var stacked);

        Assert.Equal(12, folded.Count);
        Assert.Empty(flat.Warnings);
        Assert.Empty(stacked.Warnings);

        Assert.Equal(0.5f, stacked.PackingEfficiency / flat.PackingEfficiency, 2);
    }

    /// <summary>Detection is order-stable and never enumerates an unordered collection.</summary>
    /// <remarks>
    ///     ⚠ <b>An <i>offer</i> that moved between runtimes would be worse than no offer at all,</b>
    ///     because a human accepts it once and the acceptance is recorded against island indices. The
    ///     pairing is a scan in index order taking the lowest-index free partner, which is a total
    ///     order with no ties left in it.
    /// </remarks>
    [Fact]
    public void DetectionIsTheSameListEveryTime() {
        var islands = new List<UvIsland>();

        for (var pair = 0; pair < 8; pair++) {
            islands.Add(Wedge(false, 0.12f + (0.03f * pair)));
            islands.Add(IslandCorpus.Square(0.05f + (0.01f * pair), 8f));
            islands.Add(Wedge(true, 0.12f + (0.03f * pair)));
        }

        var first = UvStacking.Detect(islands);

        for (var run = 0; run < 9; run++) {
            Assert.Equal(first, UvStacking.Detect(islands));
        }

        Assert.Equal(8, first.Count(offer => offer.Mirrored));

        // Ascending by representative, so the list itself is the artefact rather than its contents.
        for (var index = 1; index < first.Count; index++) {
            Assert.True(first[index].Representative > first[index - 1].Representative);
        }
    }

    /// <summary>An island cannot be stacked onto two representatives, and says so.</summary>
    [Fact]
    public void AChainOfFoldsIsRefusedByName() {
        var island = IslandCorpus.Square(0.25f, 8f);
        var islands = new[] { island, island, island };

        Assert.Throws<ArgumentException>(
            () => UvStacking.Fold(islands, [new(0, 1, false, 0f), new(2, 1, false, 0f)], out _)
        );

        Assert.Throws<ArgumentException>(
            () => UvStacking.Fold(islands, [new(0, 1, false, 0f), new(1, 2, false, 0f)], out _)
        );
    }

    /// <summary>A right-angled wedge, optionally reflected in <c>u</c> with its corners in the same order.</summary>
    /// <remarks>
    ///     ⚠ <b>The same order is what makes this docs/plan/41 § D11's case rather than a shape-matching
    ///     problem.</b> A symmetry-preserving remesh emits vertex <i>k</i> and its mirror as exact
    ///     negations, so the two charts come out with corresponding corners at corresponding indices —
    ///     which is exactly the correspondence a detector cannot recover on an arbitrary mesh, and the
    ///     limitation <see cref="UvStacking" /> names rather than papers over.
    /// </remarks>
    static UvIsland Wedge(bool mirrored, float side = 0.25f) {
        var points = new[] {
            new Vector2(0f, 0f),
            new Vector2(side, 0f),
            new Vector2(side, side * 0.5f),
            new Vector2(side * 0.25f, side)
        };

        if (mirrored) {
            for (var index = 0; index < points.Length; index++) {
                points[index] = new(side - points[index].X, points[index].Y);
            }
        }

        var coordinates = new[] { points[0], points[1], points[2], points[0], points[2], points[3] };
        var minimum = coordinates[0];
        var maximum = coordinates[0];

        foreach (var coordinate in coordinates) {
            minimum = Vector2.Min(minimum, coordinate);
            maximum = Vector2.Max(maximum, coordinate);
        }

        return new(coordinates, [0, 1, 2, 3, 4, 5], minimum, maximum, 8f);
    }
}

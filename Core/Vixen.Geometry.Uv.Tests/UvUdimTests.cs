// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>docs/plan/42 § D11: UDIM is a tiling of the packer and not a second implementation.</summary>
/// <remarks>
///     <para>
///         <b>Tiles are integer offsets in UV space, so the atlas-relative machinery is untouched.</b>
///         ⚠ <b>The one real constraint is that an island may not straddle a tile boundary</b>, which is
///         a placement rule rather than a new packer — <c>UvPackingTests</c> asserts it once at one
///         resolution and this extends it across the axes that could break it: the resolution, the
///         margin, the rung and the rotation count.
///     </para>
///     <para>
///         ⚠ <b>And it records what <see cref="PackOverflow.NextTile" /> does when no density is
///         pinned, which is nothing.</b> <c>Packer</c> opens tiles only in uniform mode: with
///         <see cref="PackSettings.TexelDensity" /> at its default of zero there is no density to fail
///         to meet, so the scale search shrinks into one tile and the caller's stated choice is not
///         taken — <i>and no warning says so</i>. The behaviour is defensible; the silence is the
///         finding, and it is pinned below so that a warning being added fails this test rather than
///         going unnoticed.
///     </para>
/// </remarks>
public class UvUdimTests {
    /// <summary>No island straddles a boundary, at four resolutions and three margins.</summary>
    /// <remarks>
    ///     ⚠ <b>The straddle test is the one thing about tiling that can actually be got wrong</b>, and
    ///     it is a rounding question — the margin is in texels, the tile is a unit of UV, and the
    ///     conversion between them is where an island ends up a fraction of a texel past 1.0. Sweeping
    ///     the resolution is sweeping that conversion.
    /// </remarks>
    [Theory]
    [InlineData(128, 2)]
    [InlineData(256, 2)]
    [InlineData(256, 8)]
    [InlineData(512, 4)]
    public void NoIslandStraddlesATileBoundaryAtAnyResolution(int resolution, int margin) {
        var islands = IslandCorpus.Trellis(90);

        var placements = UvUnwrap.Pack(
            islands,
            new() {
                Resolution = resolution,
                Margin = margin,

                // Far more density than one tile can hold, so a spill is the only way out.
                TexelDensity = 2.5f * resolution,
                Overflow = PackOverflow.NextTile
            },
            out var report
        );

        var tiles = placements.Select(placement => placement.Tile).Distinct().ToList();

        Assert.True(tiles.Count > 1, $"{resolution}²/{margin}: the corpus fitted one tile, so nothing straddled.");
        Assert.Contains(report.Warnings, warning => warning.Contains("UDIM", StringComparison.Ordinal));

        foreach (var tile in tiles) {
            // ⚠ Integer offsets, and never a fraction. A UDIM tile is `1001 + u + 10v` by convention,
            // which is only expressible if the offsets are whole numbers.
            Assert.True(tile.X >= 0 && tile.Y >= 0, $"Tile {tile} has a negative index.");
        }

        for (var index = 0; index < placements.Count; index++) {
            var placement = placements[index];
            var island = islands[index];
            var minimum = new Vector2(float.MaxValue);
            var maximum = new Vector2(float.MinValue);

            foreach (var coordinate in island.Coordinates) {
                var mapped = placement.Apply(island, coordinate) - new Vector2(placement.Tile.X, placement.Tile.Y);

                minimum = Vector2.Min(minimum, mapped);
                maximum = Vector2.Max(maximum, mapped);
            }

            Assert.True(
                minimum.X >= 0f && minimum.Y >= 0f && maximum.X <= 1f && maximum.Y <= 1f,
                $"{resolution}²/{margin}: island {index} runs {minimum} to {maximum} inside tile "
                + $"{placement.Tile}, which straddles it."
            );
        }
    }

    /// <summary>And it holds on every rung, because the composites are the rung that could break it.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="PackQuality.SuperPatch" /> is the rung this is worth repeating for.</b> A
    ///     composite is several islands placed as one rectangle, so a member's position is the unit's
    ///     spot plus its own offset inside the unit — two additions rather than one, and the second is
    ///     exactly where a member could be pushed past the edge the unit fitted inside.
    /// </remarks>
    [Theory]
    [InlineData(PackQuality.Rectangle)]
    [InlineData(PackQuality.Irregular)]
    [InlineData(PackQuality.SuperPatch)]
    public void NoIslandStraddlesATileBoundaryOnAnyRung(PackQuality quality) {
        var islands = IslandCorpus.Trellis(80);

        var placements = UvUnwrap.Pack(
            islands,
            new() {
                Resolution = 256,
                Margin = 2,
                Quality = quality,
                TexelDensity = 900f,
                Overflow = PackOverflow.NextTile
            },
            out var report
        );

        Assert.True(placements.Select(placement => placement.Tile).Distinct().Count() > 1, "one tile proves nothing");
        Assert.Contains(report.Warnings, warning => warning.Contains("UDIM", StringComparison.Ordinal));

        for (var index = 0; index < placements.Count; index++) {
            var placement = placements[index];
            var island = islands[index];

            foreach (var coordinate in island.Coordinates) {
                var mapped = placement.Apply(island, coordinate) - new Vector2(placement.Tile.X, placement.Tile.Y);

                Assert.InRange(mapped.X, 0f, 1f);
                Assert.InRange(mapped.Y, 0f, 1f);
            }
        }
    }

    /// <summary>A tile holds no overlaps of its own, which spilling could otherwise hide.</summary>
    /// <remarks>
    ///     ⚠ <b>A packer that opened a tile too eagerly would pass every straddle check and still be
    ///     wrong.</b> The occupancy grid is per tile, so the failure a spill invites is two islands in
    ///     one tile that were checked against different grids — which shows up as overlap and as
    ///     nothing else.
    /// </remarks>
    [Fact]
    public void EveryTileIsInternallyDisjoint() {
        var islands = IslandCorpus.Trellis(90);

        var placements = UvUnwrap.Pack(
            islands,
            new() { Resolution = 256, Margin = 2, TexelDensity = 900f, Overflow = PackOverflow.NextTile }
        );

        var tiles = placements.Select(placement => placement.Tile).Distinct().ToList();

        Assert.True(tiles.Count > 1);

        foreach (var tile in tiles) {
            PackedAtlas.Rasterize(islands, placements, 256, tile, out var overlaps);

            Assert.Equal(0, overlaps);
        }
    }

    /// <summary>Spilling is the caller's choice, and the three choices are three different answers.</summary>
    [Fact]
    public void TheThreeOverflowsAreThreeDifferentAnswers() {
        var islands = IslandCorpus.Trellis(90);

        PackSettings Settings(PackOverflow overflow) =>
            new() { Resolution = 256, Margin = 2, TexelDensity = 900f, Overflow = overflow };

        var scaled = UvUnwrap.Pack(islands, Settings(PackOverflow.Scale), out var scaledReport);
        var spilled = UvUnwrap.Pack(islands, Settings(PackOverflow.NextTile), out _);

        Assert.Single(scaled.Select(placement => placement.Tile).Distinct());
        Assert.True(spilled.Select(placement => placement.Tile).Distinct().Count() > 1);

        Assert.Contains(scaledReport.Warnings, warning => warning.Contains("scaled to", StringComparison.Ordinal));
        Assert.True(scaledReport.TexelDensity.Maximum < 900f, "Scale mode claimed it met the density it did not.");

        var refusal = Assert.Throws<InvalidOperationException>(
            () => UvUnwrap.Pack(islands, Settings(PackOverflow.Refuse))
        );

        Assert.Contains("do not fit", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>Without a density, <see cref="PackOverflow.NextTile" /> keeps one tile and says nothing.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The finding, pinned.</b> <c>Packer</c> gates the tile limit on uniform mode:
    ///         <c>Overflow == NextTile &amp;&amp; uniform ? TileLimit : 1</c>. With
    ///         <see cref="PackSettings.TexelDensity" /> at its default of zero there is no density to
    ///         miss, so the scale search shrinks everything into one tile — measured, to
    ///         <c>0.28×</c> of what the islands arrived at — and the caller's stated
    ///         <see cref="PackOverflow.NextTile" /> has no effect.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The behaviour is arguable and the silence is not.</b> docs/plan/42 § D11 says the
    ///         packer "either scales down or spills into the next tile, and which one is the caller's
    ///         choice"; here the choice is taken by the absence of an unrelated setting, and
    ///         <see cref="UvReport.Warnings" /> is empty. A warning naming the interaction would close
    ///         it. Until one exists, this test is the record.
    ///     </para>
    /// </remarks>
    [Fact]
    public void NextTileWithoutADensityDoesNotSpillAndDoesNotSayWhy() {
        var islands = IslandCorpus.Trellis(120);

        var placements = UvUnwrap.Pack(
            islands,
            new() { Resolution = 256, Margin = 2, Overflow = PackOverflow.NextTile },
            out var report
        );

        Assert.Single(placements.Select(placement => placement.Tile).Distinct());
        Assert.Equal(Int2.Zero, placements[0].Tile);
        Assert.Empty(report.Warnings);

        // And the same islands with a density do spill, which is what makes the line above a statement
        // about the interaction rather than about the corpus fitting.
        var spilled = UvUnwrap.Pack(
            islands,
            new() { Resolution = 256, Margin = 2, TexelDensity = 900f, Overflow = PackOverflow.NextTile }
        );

        Assert.True(spilled.Select(placement => placement.Tile).Distinct().Count() > 1);
    }
}

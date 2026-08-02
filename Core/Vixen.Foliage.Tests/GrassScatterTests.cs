// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Foliage.Tests;

/// <summary>
///     The derived scatter: what it produces, what refuses it, and what makes it reproducible.
/// </summary>
public sealed class GrassScatterTests {
    static GrassType Meadow =>
        GrassType.Of("Meadow") with {
            Mesh = "Meshes/grass",
            Layer = "Grass",
            Density = 4f,
            MinWeight = 0.2f,
            MaxWeight = 0.8f
        };

    static FoliageCellGrid Grid => new(32f);

    [Fact]
    public void ACellScattersTheSameBladesTwice() {
        var first = new List<GrassBlade>();
        var second = new List<GrassBlade>();

        GrassScatter.Scatter(Meadow, new(3, -2), Grid, Ground.Flat, first);
        GrassScatter.Scatter(Meadow, new(3, -2), Grid, Ground.Flat, second);

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    /// <summary>
    ///     A cell that left range and came back is scattered from nothing and is identical.
    /// </summary>
    /// <remarks>
    ///     The property the whole feature rests on. Grass is never persisted, so "the same field"
    ///     means "the same arithmetic from the same coordinate" — and a counter-based identity would
    ///     make a field flicker every time the camera walked away and back.
    /// </remarks>
    [Fact]
    public void TheHashDependsOnTheCellAndTheSlotAndNothingElse() {
        var here = new List<GrassBlade>();
        var there = new List<GrassBlade>();

        GrassScatter.Scatter(Meadow, new(0, 0), Grid, Ground.Flat, here);
        GrassScatter.Scatter(Meadow, new(0, 1), Grid, Ground.Flat, there);

        Assert.NotEmpty(here);
        Assert.NotEmpty(there);
        Assert.NotEqual(
            here.Select(blade => blade.Instance.Rotation).ToArray(),
            there.Select(blade => blade.Instance.Rotation).ToArray()
        );

        // And two cells reached in the other order agree with the first pass.
        var again = new List<GrassBlade>();

        GrassScatter.Scatter(Meadow, new(0, 0), Grid, Ground.Flat, again);
        Assert.Equal(here, again);
    }

    /// <summary>The four cells around the origin are four different fields.</summary>
    /// <remarks>
    ///     ⚠ <b>The quadrant the cast of a negative coordinate decides.</b> A hash that took an
    ///     absolute value would make (−1, −1) and (0, 0) the same field mirrored, which is a seam
    ///     through the middle of every level built around zero — and the shader's <c>uint(int)</c>
    ///     has to reinterpret the same bits for the same reason.
    /// </remarks>
    [Fact]
    public void TheFourCellsAroundTheOriginDisagree() {
        var hashes = new[] {
            GrassScatter.Hash(new(0, 0), 7),
            GrassScatter.Hash(new(-1, 0), 7),
            GrassScatter.Hash(new(0, -1), 7),
            GrassScatter.Hash(new(-1, -1), 7)
        };

        Assert.Equal(4, hashes.Distinct().Count());
    }

    [Fact]
    public void ACandidateStaysInsideItsOwnSlot() {
        var type = Meadow with { Jitter = 1f };
        var grid = Grid;
        var side = type.GridOf(grid.CellSize);
        var step = grid.CellSize / side;

        for (var index = 0; index < side * side; index++) {
            var at = GrassScatter.CandidateAt(type, new(2, 5), grid, index);
            var origin = grid.OriginOf(new(2, 5));

            var slotX = index % side;
            var slotZ = index / side;

            Assert.InRange(at.X, origin.X + (slotX * step), origin.X + ((slotX + 1) * step));
            Assert.InRange(at.Y, origin.Z + (slotZ * step), origin.Z + ((slotZ + 1) * step));
        }
    }

    /// <summary>Grass follows the layer it is bound to.</summary>
    /// <remarks>
    ///     [docs/plan/31 § T6]'s first exit criterion. Painted on one half of the cell and nowhere on
    ///     the other, the blades land on the painted half.
    /// </remarks>
    [Fact]
    public void GrassFollowsTheLayerItIsBoundTo() {
        var grid = Grid;
        var painted = new Ground(weight: at => at.X < 32f ? 1f : 0f);
        var blades = new List<GrassBlade>();

        GrassScatter.Scatter(Meadow, new(0, 0), grid, painted, blades);
        Assert.NotEmpty(blades);
        Assert.All(blades, blade => Assert.True(blade.Instance.Position.X < 32f));

        var bare = new List<GrassBlade>();

        GrassScatter.Scatter(Meadow, new(1, 0), grid, painted, bare);
        Assert.Empty(bare);
    }

    /// <summary>And the curve is a curve, not a threshold.</summary>
    [Fact]
    public void HalfPaintedGroundGrowsSomeOfIt() {
        var full = new List<GrassBlade>();
        var half = new List<GrassBlade>();

        GrassScatter.Scatter(Meadow, new(0, 0), Grid, new Ground(weight: _ => 1f), full);
        GrassScatter.Scatter(Meadow, new(0, 0), Grid, new Ground(weight: _ => 0.5f), half);

        Assert.NotEmpty(half);
        Assert.True(
            half.Count < full.Count,
            $"a weight of 0.5 grew {half.Count} blades and a weight of 1 grew {full.Count}; the "
            + "density curve is doing nothing."
        );
    }

    [Fact]
    public void SteepGroundGrowsNothing() {
        var blades = new List<GrassBlade>();

        GrassScatter.Scatter(Meadow, new(0, 0), Grid, Ground.Sloped(MathF.PI / 3f), blades);

        Assert.Empty(blades);
    }

    [Fact]
    public void NoGroundIsRefusedForSayingSo() {
        var refusal = GrassScatter.Consider(
            Meadow,
            new(0, 0),
            Grid,
            new Ground(hit: _ => false),
            0,
            1f,
            out _
        );

        Assert.Equal(GrassScatter.Refusal.NoSurface, refusal);
    }

    /// <summary>A density scalar thins the field and moves nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>The blades that survive at half density are a subset of the ones that survive at
    ///     full.</b> A scalar that re-drew instead would make a quality slider look like a different
    ///     level, which is the thing every reference engine got wrong once.
    /// </remarks>
    [Fact]
    public void LoweringTheDensityRemovesASubsetAndMovesNothing() {
        var full = new List<GrassBlade>();
        var thinned = new List<GrassBlade>();

        GrassScatter.Scatter(Meadow, new(4, 4), Grid, Ground.Flat, full);
        GrassScatter.Scatter(Meadow, new(4, 4), Grid, Ground.Flat, thinned, 0.5f);

        Assert.True(thinned.Count < full.Count);
        Assert.NotEmpty(thinned);

        foreach (var blade in thinned) {
            Assert.Contains(blade, full);
        }
    }

    /// <summary>The streams are independent: a blade's scale does not predict its heading.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure this catches is slicing one hash's bits rather than re-hashing per
    ///     stream</b>, which correlates the low bits — and shows up as every tall blade facing the
    ///     same way. Measured through the forward vector, because a quaternion's Y term is
    ///     <c>sin(θ/2)</c> and never negative over a full turn.
    /// </remarks>
    [Fact]
    public void ABladesScaleDoesNotPredictItsHeading() {
        var blades = new List<GrassBlade>();

        GrassScatter.Scatter(Meadow with { AlignToNormal = 0f }, new(0, 0), Grid, Ground.Flat, blades);

        var large = blades.Where(blade => blade.Instance.Scale > 1f).ToArray();

        Assert.True(large.Length > 20, $"only {large.Length} blades to measure.");

        var east = large.Count(blade => Quaternion.Transform(Vector3.UnitZ, blade.Instance.Rotation).X > 0f);
        var fraction = east / (float)large.Length;

        Assert.InRange(fraction, 0.3f, 0.7f);
    }

    [Fact]
    public void ABladeCarriesItsOwnTintAndPhase() {
        var blades = new List<GrassBlade>();

        GrassScatter.Scatter(Meadow, new(0, 0), Grid, Ground.Flat, blades);

        Assert.All(blades, blade => Assert.InRange(blade.Tint, 0f, 1f));
        Assert.All(blades, blade => Assert.InRange(blade.WindPhase, 0f, MathF.Tau));
        Assert.True(blades.Select(blade => blade.WindPhase).Distinct().Count() > blades.Count / 2);
    }

    /// <summary>An unbound type grows everywhere rather than nowhere.</summary>
    [Fact]
    public void ATypeWithNoLayerGrowsOnAllOfIt() {
        var blades = new List<GrassBlade>();

        GrassScatter.Scatter(Meadow with { Layer = "" }, new(0, 0), Grid, new Ground(weight: _ => 0f), blades);

        Assert.Equal(Meadow.CandidatesFor(Grid.CellSize), blades.Count);
    }
}

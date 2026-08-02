// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Foliage.Tests;

/// <summary>The offline ecology — [docs/plan/31 § T9].</summary>
public sealed class FoliageGrowthTests {
    static FoliageType Pine =>
        FoliageType.Of("Pine") with {
            Mesh = "Meshes/pine",
            Radius = 2f,
            MinScale = 1f,
            MaxScale = 1f,
            Ecology = FoliageEcology.Tree with { SeedDensity = 0.004f, SpreadDistance = 10f }
        };

    static FoliageGrowthSettings Field =>
        FoliageGrowthSettings.Over(new(0f, 0f), new(200f, 200f)) with { Steps = 6 };

    static (FoliageVolume Volume, int Type) Sown(FoliageType? type = null) {
        var volume = new FoliageVolume(new(32f));

        return (volume, volume.AddType(type ?? Pine));
    }

    [Fact]
    public void AForestGrows() {
        var (volume, type) = Sown();
        var result = FoliageGrowth.Simulate(volume, Ground.Flat, Field);

        Assert.True(result.Placed > 0, "the simulation grew nothing at all.");
        Assert.Equal(result.Placed, volume.CountOf(type));
        Assert.True(result.Sprouted > result.Sown, "nothing spread; the forest is only its sowing.");
    }

    /// <summary>The same seed grows the same forest.</summary>
    /// <remarks>
    ///     [docs/plan/31 § T9]'s exit criterion, and the property the whole thing rests on: a plant's
    ///     identity is hashed at birth and each step's candidates resolve in hash order, so nothing
    ///     depends on which parent happened to be walked first.
    /// </remarks>
    [Fact]
    public void TheSameSeedGrowsTheSameForest() {
        var (first, _) = Sown();
        var (second, _) = Sown();

        var a = FoliageGrowth.Simulate(first, Ground.Flat, Field);
        var b = FoliageGrowth.Simulate(second, Ground.Flat, Field);

        Assert.Equal(a, b);
        Assert.Equal(Positions(first), Positions(second));
    }

    /// <summary>And a different seed grows a different one.</summary>
    [Fact]
    public void ADifferentSeedGrowsADifferentForest() {
        var (first, _) = Sown();
        var (second, _) = Sown();

        FoliageGrowth.Simulate(first, Ground.Flat, Field);
        FoliageGrowth.Simulate(second, Ground.Flat, Field with { Seed = 0x1234_5678u });

        Assert.NotEqual(Positions(first), Positions(second));
        Assert.True(first.InstanceCount > 0 && second.InstanceCount > 0);
    }

    /// <summary>Re-running replaces rather than accumulates.</summary>
    /// <remarks>
    ///     ⚠ <b>[§ D4]'s reserved layer, in this kernel's vocabulary.</b> A simulation is
    ///     re-runnable, which means it regenerates its instances wholesale — and the destination
    ///     being its own volume is what stops re-rolling the seed from deleting an afternoon of
    ///     hand-placed trees.
    /// </remarks>
    [Fact]
    public void ReRunningRegeneratesRatherThanAccumulates() {
        var (volume, _) = Sown();

        var first = FoliageGrowth.Simulate(volume, Ground.Flat, Field);
        var again = FoliageGrowth.Simulate(volume, Ground.Flat, Field);

        Assert.Equal(first.Placed, again.Placed);
        Assert.Equal(first.Placed, volume.InstanceCount);
    }

    [Fact]
    public void HandPlacedTreesAreInAnotherVolumeAndAreNotTouched() {
        var (grown, type) = Sown();
        var placed = new FoliageVolume(new(32f));

        placed.AddType(Pine);
        placed.Add(0, new(new(10f, 0f, 10f), Quaternion.Identity, 1f));

        FoliageGrowth.Simulate(grown, Ground.Flat, Field);

        Assert.Equal(1, placed.InstanceCount);
        Assert.True(grown.CountOf(type) > 1);
    }

    /// <summary>Spread clumps a forest, which is what makes it read as one.</summary>
    /// <remarks>
    ///     <para>
    ///         Measured at the <em>spread</em> scale, with the variance-to-mean ratio of counts in
    ///         cells one spread distance across: one for a Poisson scatter, above one for clumped.
    ///         Comparing a spread forest against a pure sowing of the same species isolates the
    ///         mechanism — every other rule is identical between the two.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not nearest-neighbour distance, which measures the spacing radius instead.</b> A
    ///         hard minimum spacing dominates that statistic and gets stronger as the field fills, so
    ///         a spread forest scores as <em>more</em> evenly spaced than the sowing it grew from —
    ///         which is true at two metres and says nothing about what happens at ten.
    ///     </para>
    ///     <para>
    ///         Shade is off for the comparison, deliberately: it pushes the other way. See
    ///         <see cref="ShadeMakesAForestMoreEvenlySpacedThanChance" />.
    ///     </para>
    /// </remarks>
    [Fact]
    public void SpreadClumpsAForestAndASowingAloneDoesNot() {
        var open = Pine.Ecology with { ShadeTolerance = 1f };

        var (sowingOnly, _) = Sown(Pine with { Ecology = open with { SeedsPerStep = 0 } });
        var (spread, _) = Sown(Pine with { Ecology = open });

        FoliageGrowth.Simulate(sowingOnly, Ground.Flat, Field with { Steps = 8 });
        FoliageGrowth.Simulate(spread, Ground.Flat, Field with { Steps = 8 });

        var sownPoints = Positions(sowingOnly);
        var grownPoints = Positions(spread);

        Assert.True(sownPoints.Length > 40 && grownPoints.Length > 40);

        var sownRatio = VarianceToMean(sownPoints, Field, Pine.Ecology.SpreadDistance);
        var grownRatio = VarianceToMean(grownPoints, Field, Pine.Ecology.SpreadDistance);

        Assert.True(
            grownRatio > sownRatio,
            $"a sowing scored {sownRatio:0.00} and a spread forest {grownRatio:0.00}; spread is "
            + "not clumping anything at the scale it works over."
        );

        Assert.True(grownRatio > 1f, $"a spread forest scored {grownRatio:0.00}, which is not clumped.");
    }

    /// <summary>And shade spaces it out, which is the opposite and is also correct.</summary>
    /// <remarks>
    ///     ⚠ <b>A forest under competition is <em>more</em> evenly spaced than chance, not less.</b>
    ///     Shade suppresses exactly the near neighbours that clumping produces, so the two mechanisms
    ///     pull in opposite directions and the shade one wins at the tolerances a forest is authored
    ///     with. Reading "clumped" as the goal and asserting it unconditionally is a test that fails
    ///     on correct output — this pair is what says which mechanism is being measured.
    /// </remarks>
    [Fact]
    public void ShadeMakesAForestMoreEvenlySpacedThanChance() {
        var (grown, _) = Sown();

        FoliageGrowth.Simulate(grown, Ground.Flat, Field with { Steps = 8 });

        var points = Positions(grown);

        Assert.True(points.Length > 40, $"only {points.Length} trees to measure.");

        var ratio = ClarkEvans(points, Field.Area);

        Assert.True(
            ratio > 0.5f,
            $"a shaded forest scored {ratio:0.000}; competition should space it out past chance."
        );
    }

    /// <summary>A shade-intolerant species thins itself out; a tolerant one does not.</summary>
    [Fact]
    public void ShadeToleranceDecidesHowDenseAForestGets() {
        var intolerant = Pine with { Ecology = Pine.Ecology with { ShadeTolerance = 0f } };
        var tolerant = Pine with { Ecology = Pine.Ecology with { ShadeTolerance = 1f } };

        var (dark, _) = Sown(intolerant);
        var (light, _) = Sown(tolerant);

        var shaded = FoliageGrowth.Simulate(dark, Ground.Flat, Field);
        var open = FoliageGrowth.Simulate(light, Ground.Flat, Field);

        Assert.True(shaded.Shaded > 0, "nothing was shaded out at zero tolerance.");
        Assert.Equal(0, open.Shaded);
        Assert.True(
            open.Placed > shaded.Placed,
            $"a shade-tolerant species grew {open.Placed} and an intolerant one {shaded.Placed}."
        );
    }

    /// <summary>And the canopy grows, so a forest fills in rather than freezing at first contact.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure this catches is shading from the mature radius.</b> That produces one
    ///     tree per shade radius, evenly spaced, everywhere — the pattern that makes a procedural
    ///     forest read as procedural, and it would pass every other test here.
    /// </remarks>
    [Fact]
    public void TheCanopyGrowsWithThePlant() {
        var ecology = FoliageEcology.Tree with { ShadeRadius = 8f, MaxAge = 4f };

        Assert.Equal(0f, ecology.ShadeAt(0f));
        Assert.Equal(4f, ecology.ShadeAt(2f), 3);
        Assert.Equal(8f, ecology.ShadeAt(4f), 3);
        Assert.Equal(8f, ecology.ShadeAt(40f), 3);
    }

    /// <summary>A higher-priority species displaces a lower one it lands on.</summary>
    [Fact]
    public void PriorityDisplacesRatherThanTies() {
        var scrub = FoliageType.Of("Scrub") with {
            Radius = 3f,
            Ecology = FoliageEcology.Tree with { SeedDensity = 0.01f, Priority = 1, ShadeTolerance = 1f }
        };

        var oak = FoliageType.Of("Oak") with {
            Radius = 3f,
            Ecology = FoliageEcology.Tree with { SeedDensity = 0.004f, Priority = 20, ShadeTolerance = 1f }
        };

        var volume = new FoliageVolume(new(32f));
        var scrubType = volume.AddType(scrub);
        var oakType = volume.AddType(oak);

        var result = FoliageGrowth.Simulate(volume, Ground.Flat, Field);

        Assert.True(result.Displaced > 0, "no scrub was ever displaced by an oak.");
        Assert.True(volume.CountOf(oakType) > 0);
        Assert.True(volume.CountOf(scrubType) > 0);
    }

    /// <summary>A blocking volume clears its footprint.</summary>
    [Fact]
    public void NothingGrowsInsideABlocker() {
        var (volume, _) = Sown();
        var clearing = FoliageBlocker.Around(new(100f, 100f), 30f);

        var result = FoliageGrowth.Simulate(volume, Ground.Flat, Field, [clearing]);

        Assert.True(result.Blocked > 0, "the blocker refused nothing.");

        foreach (var position in Positions(volume)) {
            Assert.False(
                clearing.Contains(new(position.X, 0f, position.Y)),
                $"a tree at {position} is standing inside the clearing."
            );
        }
    }

    /// <summary>And moving the blocker regrows what it used to cover.</summary>
    /// <remarks>
    ///     ⚠ <b>A blocker refuses rather than deletes.</b> A blocker that removed would make its own
    ///     removal irreversible, which is the opposite of what a re-runnable simulation is for.
    /// </remarks>
    [Fact]
    public void MovingABlockerRegrowsWhatItCovered() {
        var (volume, _) = Sown();

        FoliageGrowth.Simulate(volume, Ground.Flat, Field, [FoliageBlocker.Around(new(100f, 100f), 30f)]);

        var withClearing = volume.InstanceCount;

        FoliageGrowth.Simulate(volume, Ground.Flat, Field);

        Assert.True(volume.InstanceCount > withClearing);
        Assert.Contains(Positions(volume), at => Vector2.Distance(at, new(100f, 100f)) < 30f);
    }

    [Fact]
    public void SteepGroundGrowsNothing() {
        var (volume, _) = Sown();
        var result = FoliageGrowth.Simulate(volume, Ground.Sloped(MathF.PI / 3f), Field);

        Assert.Equal(0, result.Placed);
        Assert.True(result.NoSurface > 0);
        Assert.Equal(0, volume.InstanceCount);
    }

    /// <summary>A plant's size follows its age.</summary>
    [Fact]
    public void SaplingsAreSmallerThanTrees() {
        var (volume, _) = Sown();

        // Past the maximum age, so the sowing is grown and the last step's seedlings are not. At or
        // below it every plant is one cohort and every scale is the same, which is correct and
        // measures nothing.
        FoliageGrowth.Simulate(volume, Ground.Flat, Field with { Steps = 6 });

        var scales = volume.Chunks.SelectMany(chunk => chunk.Instances).Select(i => i.Scale).ToArray();

        Assert.True(scales.Length > 10);
        Assert.True(scales.Min() < scales.Max(), "every plant is the same size; age is not scaling.");
        Assert.All(scales, scale => Assert.InRange(scale, 0.01f, 1.01f));
    }

    /// <summary>A species that never matures never spreads, and the counters say so.</summary>
    [Fact]
    public void ASpeciesThatOutlivesTheSimulationNeverSpreads() {
        var slow = Pine with { Ecology = Pine.Ecology with { MaxAge = 100f } };
        var (volume, _) = Sown(slow);

        var result = FoliageGrowth.Simulate(volume, Ground.Flat, Field);

        Assert.Equal(0, result.Sprouted);
        Assert.True(result.Sown > 0);
        Assert.True(result.Placed > 0);
    }

    [Fact]
    public void TheCapIsAnnouncedRatherThanSilent() {
        var (volume, _) = Sown();
        var result = FoliageGrowth.Simulate(volume, Ground.Flat, Field with { MaxPlants = 20 });

        Assert.True(result.Capped > 0, "a twenty-plant cap over forty thousand square metres refused nothing.");
        Assert.True(result.Placed <= 20);
    }

    [Fact]
    public void ARegionWithNoAreaIsRefused() {
        var (volume, _) = Sown();

        var thrown = Assert.Throws<ArgumentException>(
            () => FoliageGrowth.Simulate(volume, Ground.Flat, Field with { Size = new(0f, 100f) })
        );

        Assert.Contains("no area", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATypeThatDoesNotSowTakesNoPart() {
        var (volume, type) = Sown(Types.Tree);

        var result = FoliageGrowth.Simulate(volume, Ground.Flat, Field);

        Assert.Equal(0, result.Sown);
        Assert.Equal(0, volume.CountOf(type));
    }

    static Vector2[] Positions(FoliageVolume volume) =>
        [.. volume.Chunks
            .SelectMany(chunk => chunk.Instances)
            .Select(instance => new Vector2(instance.Position.X, instance.Position.Z))
            .OrderBy(at => at.X)
            .ThenBy(at => at.Y)];

    /// <summary>Clark and Evans's ratio: below 0.5 is clumped, above it is over-dispersed.</summary>
    static float ClarkEvans(Vector2[] points, float area) =>
        MeanNearestNeighbour(points) * MathF.Sqrt(points.Length / area);

    /// <summary>Variance over mean of counts in square cells: 1 is Poisson, above 1 is clumped.</summary>
    static float VarianceToMean(Vector2[] points, in FoliageGrowthSettings region, float cellSize) {
        var across = Math.Max(1, (int)MathF.Floor(region.Size.X / cellSize));
        var down = Math.Max(1, (int)MathF.Floor(region.Size.Y / cellSize));
        var counts = new int[across * down];

        foreach (var point in points) {
            var x = Math.Clamp((int)((point.X - region.Origin.X) / cellSize), 0, across - 1);
            var z = Math.Clamp((int)((point.Y - region.Origin.Y) / cellSize), 0, down - 1);

            counts[(z * across) + x]++;
        }

        var mean = (float)counts.Average();
        var variance = counts.Sum(count => (count - mean) * (count - mean)) / counts.Length;

        return variance / MathF.Max(mean, 1e-6f);
    }

    static float MeanNearestNeighbour(Vector2[] points) {
        var total = 0f;

        foreach (var point in points) {
            var nearest = float.PositiveInfinity;

            foreach (var other in points) {
                if (other == point) {
                    continue;
                }

                nearest = MathF.Min(nearest, Vector2.Distance(point, other));
            }

            total += nearest;
        }

        return total / points.Length;
    }
}

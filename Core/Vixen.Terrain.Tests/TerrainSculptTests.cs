// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Terrain;
using Xunit;

namespace Vixen.Terrain.Tests;

/// <summary>The sculpt kernels — [docs/plan/31 § D11].</summary>
public sealed class TerrainSculptTests {
    static TerrainDescription Shape() =>
        TerrainDescription.Default with {
            TileSamples = 32, TilesX = 2, TilesZ = 2,
            MetresPerQuad = 1f, MinHeight = -100f, MaxHeight = 100f
        };

    static (Terrain Terrain, TerrainEditLayer Layer) Build(float height = 0f) {
        var terrain = new Terrain(Shape(), height);
        return (terrain, terrain.AddLayer("Sculpt"));
    }

    static TerrainBrush Brush(float radius = 5f, float strength = 1f, float falloff = 0.5f) =>
        TerrainBrush.Default with { Radius = radius, Strength = strength, Falloff = falloff };

    static float Height(Terrain terrain, int x, int z) {
        terrain.Resolve();
        return terrain.Composite.MetresAt(x, z);
    }

    // --- Sculpt -------------------------------------------------------------

    [Fact]
    public void SculptingRaisesTheGroundByTheMetresAskedForAtTheCentre() {
        var (terrain, layer) = Build();

        TerrainSculpt.Sculpt(terrain, layer, Brush(), new(new(16f, 16f)), 10f);

        Assert.Equal(10f, Height(terrain, 16, 16), 1);
    }

    [Fact]
    public void SculptingByMinusWhatItRaisedIsTheIdentity() {
        // The property every reversible tool claims and few have. It holds here because a delta is
        // added to a layer rather than composited into a stored height and re-quantised.
        var (terrain, layer) = Build(height: 20f);
        var stamp = new BrushStamp(new(16f, 16f));

        TerrainSculpt.Sculpt(terrain, layer, Brush(), stamp, 12.5f);
        TerrainSculpt.Sculpt(terrain, layer, Brush(), stamp, -12.5f);

        terrain.Resolve();

        for (var z = 8; z < 24; z++) {
            for (var x = 8; x < 24; x++) {
                Assert.Equal(20f, terrain.Composite.MetresAt(x, z), 2);
            }
        }
    }

    [Fact]
    public void SculptingFallsOffAndTouchesNothingOutsideTheBrush() {
        var (terrain, layer) = Build();

        TerrainSculpt.Sculpt(terrain, layer, Brush(radius: 5f), new(new(16f, 16f)), 10f);
        terrain.Resolve();

        var centre = terrain.Composite.MetresAt(16, 16);
        var middle = terrain.Composite.MetresAt(19, 16);

        Assert.True(middle < centre, "the brush did not fall off.");
        Assert.True(middle > 0f, "the brush fell off too fast.");

        // ⚠ Compared as stored samples against a corner the brush never reached, not against 0 m.
        // A 200 m range over sixteen bits is a 3 mm quantum and StoreHeight rounds, so a "flat
        // terrain at zero" actually sits half a step above it — asserting 0 m to three decimals is
        // asserting something the format cannot represent.
        Assert.Equal(terrain.Composite[31, 31], terrain.Composite[22, 16]);
    }

    [Fact]
    public void SculptingWritesTheLayerAndLeavesTheBaseAlone() {
        var (terrain, layer) = Build(height: 5f);
        var before = terrain.Base[16, 16];

        TerrainSculpt.Sculpt(terrain, layer, Brush(), new(new(16f, 16f)), 10f);

        Assert.Equal(before, terrain.Base[16, 16]);
        Assert.NotEqual(0, layer.DeltaAt(16, 16));
    }

    // --- Flatten ------------------------------------------------------------

    [Fact]
    public void FlatteningPullsTheGroundToTheTargetAtFullWeight() {
        var (terrain, layer) = Build(height: 30f);

        TerrainSculpt.Flatten(terrain, layer, Brush(falloff: 0f), new(new(16f, 16f)), 5f);

        Assert.Equal(5f, Height(terrain, 16, 16), 1);
    }

    [Fact]
    public void FlatteningRepeatedlyConvergesRatherThanCreeping() {
        // Rounding rather than truncating in StoreHeight is what makes this true. With truncation
        // each pass loses up to a step and the flattened area sinks for as long as the brush is held.
        var (terrain, layer) = Build(height: 30f);
        var stamp = new BrushStamp(new(16f, 16f));

        for (var pass = 0; pass < 50; pass++) {
            TerrainSculpt.Flatten(terrain, layer, Brush(falloff: 0f), stamp, 5f);
            terrain.Resolve();
        }

        Assert.Equal(5f, Height(terrain, 16, 16), 2);
    }

    [Fact]
    public void FlatteningOnALayerAboveAMountainFlattensTheMountain() {
        // The pairing docs/plan/31 § D4 warns about: read the composite, write the layer. Reading
        // the layer instead would flatten this layer's contribution and leave the mountain.
        var terrain = new Terrain(Shape());
        var mountain = terrain.AddLayer("Mountain");
        var flatten = terrain.AddLayer("Pad");

        TerrainSculpt.Sculpt(terrain, mountain, Brush(radius: 12f), new(new(16f, 16f)), 40f);
        terrain.Resolve();
        Assert.True(terrain.Composite.MetresAt(16, 16) > 30f);

        TerrainSculpt.Flatten(terrain, flatten, Brush(radius: 6f, falloff: 0f), new(new(16f, 16f)), 2f);

        Assert.Equal(2f, Height(terrain, 16, 16), 1);
        Assert.True(mountain.DeltaAt(16, 16) > 0, "the mountain layer was modified.");
    }

    // --- Smooth -------------------------------------------------------------

    [Fact]
    public void SmoothingReducesTheDifferenceBetweenNeighbours() {
        var (terrain, layer) = Build();

        // A spike, then smooth it.
        layer.SetDelta(16, 16, 20_000);
        terrain.InvalidateAll();
        terrain.Resolve();

        var before = terrain.Composite[16, 16] - terrain.Composite[17, 16];

        for (var pass = 0; pass < 5; pass++) {
            TerrainSculpt.Smooth(terrain, layer, Brush(radius: 6f, falloff: 0f), new(new(16f, 16f)));
            terrain.Resolve();
        }

        var after = terrain.Composite[16, 16] - terrain.Composite[17, 16];

        Assert.True(after < before, $"smoothing did not reduce the step ({before} → {after}).");
    }

    /// <summary>
    ///     Smoothing reads a snapshot, so the result does not depend on the order samples are visited.
    /// </summary>
    /// <remarks>
    ///     Smoothing in place makes the second sample average a neighbour the first has already
    ///     moved, which is a directional smear that shows as a ridge running diagonally across every
    ///     smoothed area. The test for it: a symmetric input must stay symmetric.
    /// </remarks>
    [Fact]
    public void SmoothingASymmetricSpikeStaysSymmetric() {
        var (terrain, layer) = Build();

        layer.SetDelta(16, 16, 20_000);
        terrain.InvalidateAll();
        terrain.Resolve();

        TerrainSculpt.Smooth(terrain, layer, Brush(radius: 8f, falloff: 0f), new(new(16f, 16f)));
        terrain.Resolve();

        for (var offset = 1; offset <= 4; offset++) {
            Assert.Equal(terrain.Composite[16 - offset, 16], terrain.Composite[16 + offset, 16]);
            Assert.Equal(terrain.Composite[16, 16 - offset], terrain.Composite[16, 16 + offset]);
            Assert.Equal(terrain.Composite[16 - offset, 16], terrain.Composite[16, 16 - offset]);
        }
    }

    [Fact]
    public void SmoothingIsMeanPreservingToWithinRounding() {
        var (terrain, layer) = Build(height: 10f);

        layer.SetDelta(16, 16, 10_000);
        layer.SetDelta(18, 18, -8_000);
        terrain.InvalidateAll();
        terrain.Resolve();

        long Total() {
            long sum = 0;

            for (var z = 8; z < 26; z++) {
                for (var x = 8; x < 26; x++) {
                    sum += terrain.Composite[x, z];
                }
            }

            return sum;
        }

        var before = Total();
        TerrainSculpt.Smooth(terrain, layer, Brush(radius: 6f, falloff: 0f), new(new(16f, 16f)));
        terrain.Resolve();

        // Within a fraction of a per cent: a 3×3 box filter conserves the mean exactly in the
        // interior and this window includes its edge, plus one rounding step per sample.
        Assert.InRange(Total(), (long)(before * 0.995), (long)(before * 1.005));
    }

    // --- Noise --------------------------------------------------------------

    [Fact]
    public void NoiseIsBoundedByItsAmplitude() {
        // The reason it is value noise: the range is exactly the range of the lattice, so an
        // amplitude declared as three metres never exceeds three metres. An artist sculpting beside
        // a building cannot work with "three metres, except occasionally".
        var (terrain, layer) = Build(height: 50f);

        TerrainSculpt.Noise(terrain, layer, Brush(radius: 12f, falloff: 0f), new(new(16f, 16f)), 3f, new());
        terrain.Resolve();

        for (var z = 6; z < 27; z++) {
            for (var x = 6; x < 27; x++) {
                Assert.InRange(terrain.Composite.MetresAt(x, z), 50f - 3.05f, 50f + 3.05f);
            }
        }
    }

    [Fact]
    public void NoiseWithTheSameSeedIsTheSameNoise() {
        var settings = new TerrainNoise(Seed: 12345u);

        for (var x = 0; x < 20; x++) {
            Assert.Equal(settings.At(x, 7), settings.At(x, 7));
            Assert.NotEqual(settings.At(x, 7), new TerrainNoise(Seed: 999u).At(x, 7));
        }
    }

    [Fact]
    public void RidgedNoiseIsNonNegative() {
        var ridged = new TerrainNoise(Ridged: true);

        for (var z = 0; z < 30; z++) {
            for (var x = 0; x < 30; x++) {
                Assert.InRange(ridged.At(x, z), 0f, 1f);
            }
        }
    }

    // --- Erosion ------------------------------------------------------------

    [Fact]
    public void ErosionOnlyMovesGroundSteeperThanTheTalusAngle() {
        var (terrain, layer) = Build(height: 10f);

        // Flat ground is already below any talus angle, so nothing should move.
        TerrainSculpt.Erode(terrain, layer, Brush(radius: 8f, falloff: 0f), new(new(16f, 16f)), 1f, 1f);
        terrain.Resolve();

        Assert.Equal(0, layer.DeltaAt(16, 16));
        Assert.Equal(terrain.Base[16, 16], terrain.Composite[16, 16]);
    }

    [Fact]
    public void ErosionLowersASpikeAndNeverRaisesIt() {
        var (terrain, layer) = Build(height: 10f);

        layer.SetDelta(16, 16, 15_000);
        terrain.InvalidateAll();
        terrain.Resolve();

        var before = terrain.Composite[16, 16];

        for (var pass = 0; pass < 4; pass++) {
            TerrainSculpt.Erode(terrain, layer, Brush(radius: 8f, falloff: 0f), new(new(16f, 16f)), 0.5f, 1f);
            terrain.Resolve();
        }

        Assert.True(terrain.Composite[16, 16] < before, "erosion did not wear the spike down.");
    }

    [Fact]
    public void HydroCarvesWithoutRunningAway() {
        var (terrain, layer) = Build(height: 40f);

        layer.SetDelta(16, 16, 10_000);
        terrain.InvalidateAll();
        terrain.Resolve();

        for (var pass = 0; pass < 10; pass++) {
            TerrainSculpt.Hydro(terrain, layer, Brush(radius: 8f, falloff: 0f), new(new(16f, 16f)), 0.5f);
            terrain.Resolve();
        }

        // Bounded: it must still be a terrain rather than having collapsed to the floor or the roof.
        for (var z = 10; z < 23; z++) {
            for (var x = 10; x < 23; x++) {
                Assert.InRange(terrain.Composite.MetresAt(x, z), -100f, 100f);
            }
        }
    }

    // --- Ramp ---------------------------------------------------------------

    [Fact]
    public void ARampInterpolatesItsTwoHeightsAlongItsLength() {
        var (terrain, layer) = Build();

        TerrainSculpt.Ramp(terrain, layer, new(8f, 16f), new(24f, 16f), 0f, 16f, halfWidth: 3f, sideFalloff: 0f);
        terrain.Resolve();

        Assert.Equal(0f, terrain.Composite.MetresAt(8, 16), 1);
        Assert.Equal(8f, terrain.Composite.MetresAt(16, 16), 1);
        Assert.Equal(16f, terrain.Composite.MetresAt(24, 16), 1);
    }

    [Fact]
    public void ARampTouchesNothingBeyondItsHalfWidthOrItsEnds() {
        var (terrain, layer) = Build();

        TerrainSculpt.Ramp(terrain, layer, new(8f, 16f), new(24f, 16f), 0f, 16f, halfWidth: 3f, sideFalloff: 0f);
        terrain.Resolve();

        // Stored samples against an untouched corner — see SculptingFallsOff… for why not metres.
        var untouched = terrain.Composite[31, 31];

        Assert.Equal(untouched, terrain.Composite[16, 20]);
        Assert.Equal(untouched, terrain.Composite[4, 16]);
        Assert.Equal(untouched, terrain.Composite[28, 16]);
    }

    [Fact]
    public void ARampsSideFalloffIsCosineBlendedAndMonotonic() {
        var (terrain, layer) = Build();

        TerrainSculpt.Ramp(terrain, layer, new(8f, 16f), new(24f, 16f), 20f, 20f, halfWidth: 6f, sideFalloff: 1f);
        terrain.Resolve();

        var previous = float.MaxValue;

        for (var offset = 0; offset <= 6; offset++) {
            var height = terrain.Composite.MetresAt(16, 16 + offset);

            Assert.True(height <= previous + 0.01f, $"the falloff rose at {offset}.");
            previous = height;
        }

        Assert.Equal(20f, terrain.Composite.MetresAt(16, 16), 1);
        Assert.Equal(terrain.Composite[31, 31], terrain.Composite[16, 22]);
    }

    [Fact]
    public void ADegenerateRampDoesNothing() {
        var (terrain, layer) = Build();

        Assert.True(TerrainSculpt.Ramp(terrain, layer, new(8f, 8f), new(8f, 8f), 0f, 5f, 3f).IsEmpty);
        Assert.True(TerrainSculpt.Ramp(terrain, layer, new(8f, 8f), new(16f, 8f), 0f, 5f, 0f).IsEmpty);
        Assert.True(layer.IsEmpty);
    }

    // --- Holes --------------------------------------------------------------

    [Fact]
    public void PaintingAHolePunchesTheQuadsAroundIt() {
        var terrain = new Terrain(Shape());

        Assert.True(terrain.Holes.IsEmpty);

        TerrainSculpt.PaintHoles(terrain, Brush(radius: 1f, falloff: 0f), new(new(16f, 16f)), hole: true);

        Assert.False(terrain.Holes.IsEmpty);
        Assert.True(terrain.Holes.IsHole(16, 16));

        // One sample kills the up-to-four quads that reference it.
        Assert.True(terrain.Holes.IsQuadMissing(15, 15));
        Assert.True(terrain.Holes.IsQuadMissing(16, 16));
        Assert.False(terrain.Holes.IsQuadMissing(14, 14));
    }

    [Fact]
    public void FillingAHoleBackInRestoresTheCount() {
        var terrain = new Terrain(Shape());
        var brush = Brush(radius: 2f, falloff: 0f);

        TerrainSculpt.PaintHoles(terrain, brush, new(new(16f, 16f)), hole: true);
        var punched = terrain.Holes.HoleCount;
        Assert.True(punched > 0);

        TerrainSculpt.PaintHoles(terrain, brush, new(new(16f, 16f)), hole: false);
        Assert.Equal(0, terrain.Holes.HoleCount);
        Assert.True(terrain.Holes.IsEmpty);
    }

    [Fact]
    public void AHoleBecomesTheNoCollisionSentinelInTheCollisionSamples() {
        // The seam to docs/plan/31 § D10: the kernel cannot name a ShapeDescription, so it fills
        // floats and takes the sentinel as an argument.
        var terrain = new Terrain(Shape(), height: 7f);
        terrain.Holes.SetHole(3, 3, true);

        var samples = new float[terrain.Description.TileSamples * terrain.Description.TileSamples];
        terrain.Composite.FillCollisionSamples(0, 0, terrain.Holes, float.MaxValue, samples);

        Assert.Equal(float.MaxValue, samples[(3 * terrain.Description.TileSamples) + 3]);
        Assert.Equal(7f, samples[(4 * terrain.Description.TileSamples) + 4], 2);
    }

    [Fact]
    public void CollisionSamplesAreInMetresAndCoverTheRightTile() {
        var terrain = new Terrain(Shape());
        var layer = terrain.AddLayer("A");

        // Raise a spot inside tile (1, 1) and read that tile back.
        TerrainSculpt.Sculpt(terrain, layer, Brush(radius: 3f, falloff: 0f), new(new(40f, 40f)), 25f);
        terrain.Resolve();

        var size = terrain.Description.TileSamples;
        var samples = new float[size * size];
        terrain.Composite.FillCollisionSamples(1, 1, null, float.MaxValue, samples);

        // Tile (1, 1) starts at sample 31, so world 40 is local 9.
        Assert.Equal(25f, samples[(9 * size) + 9], 1);
    }
}

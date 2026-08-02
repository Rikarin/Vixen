// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Terrain.Tests;

/// <summary>A ray against the ground — [docs/plan/31 § T3], the half of a pointer that is arithmetic.</summary>
public sealed class TerrainPickTests {
    static TerrainDescription Shape() =>
        TerrainDescription.Default with {
            TileSamples = 32, TilesX = 2, TilesZ = 2,
            MetresPerQuad = 1f, MinHeight = -100f, MaxHeight = 100f
        };

    static Terrain Flat(float metres) {
        var terrain = new Terrain(Shape());
        var stored = terrain.Description.StoreHeight(metres);

        terrain.Base.Span.Fill(stored);
        terrain.InvalidateAll();
        terrain.Resolve();

        return terrain;
    }

    [Fact]
    public void ARayStraightDownLandsOnTheGroundUnderIt() {
        var terrain = Flat(12f);

        Assert.True(TerrainPick.Cast(terrain, new(20f, 500f, 30f), -Vector3.UnitY, out var hit));

        Assert.Equal(20f, hit.Position.X, 3);
        Assert.Equal(30f, hit.Position.Z, 3);
        Assert.Equal(12f, hit.Position.Y, 2);
        Assert.Equal(488f, hit.Distance, 2);
    }

    [Fact]
    public void TheGroundPointIsTheStampsCentre() {
        var terrain = Flat(0f);

        Assert.True(TerrainPick.Cast(terrain, new(11f, 40f, 17f), -Vector3.UnitY, out var hit));
        Assert.Equal(new Vector2(11f, 17f), hit.Ground);
    }

    /// <summary>A slanted ray lands on the slope facing it, not on the plane under it.</summary>
    /// <remarks>
    ///     <para>
    ///         The property the marcher exists for. A ray coming in at 45° at a ridge meets the
    ///         hillside well before the XZ position it would reach at ground level, and a pick that
    ///         intersected a plane at height zero would put the brush ten metres past the ridge —
    ///         which reads as a brush that lags behind the pointer on slopes and nowhere else.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it lands on the ramp rather than on the plateau, because a heightfield has no
    ///         vertical faces.</b> "Everything past sample 30 is twenty metres up" is one quad of 45°
    ///         ground between samples 29 and 30, not a cliff — so the ray meets it partway up, at the
    ///         intersection of two lines rather than at the top. An expectation of the plateau's
    ///         height is an expectation of geometry the storage cannot hold.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ASlantedRayMeetsTheSlopeRatherThanThePlaneUnderIt() {
        var terrain = new Terrain(Shape());
        var layer = terrain.AddLayer("Hill");

        // A ridge running along Z: everything past x = 30 is 20 m up.
        for (var z = 0; z < terrain.Description.SamplesZ; z++) {
            for (var x = 30; x < terrain.Description.SamplesX; x++) {
                layer.SetDelta(x, z, (short)(20f / terrain.Description.MetresPerStep));
            }
        }

        terrain.InvalidateAll();
        terrain.Resolve();

        // Aimed from low ground, downwards at 45°, so it would reach x = 40 at height zero.
        Assert.True(
            TerrainPick.Cast(terrain, new(10f, 30f, 20f), Vector3.Normalize(new(1f, -1f, 0f)), out var hit)
        );

        // Where the ray y = 40 − x meets the ramp y = 20 (x − 29): x = 620/21, a little under 29.6.
        Assert.Equal(620f / 21f, hit.Position.X, 2);
        Assert.Equal(40f - (620f / 21f), hit.Position.Y, 2);

        // And nowhere near where a plane at height zero would have answered.
        Assert.True(hit.Position.X < 30f, $"the pick ran past the ridge to {hit.Position.X}.");
    }

    [Fact]
    public void ARayPointingAwayFromTheTerrainMissesIt() {
        var terrain = Flat(0f);

        Assert.False(TerrainPick.Cast(terrain, new(20f, 100f, 20f), Vector3.UnitY, out _));
    }

    [Fact]
    public void ARayBesideTheTerrainMissesIt() {
        var terrain = Flat(0f);

        Assert.False(TerrainPick.Cast(terrain, new(-50f, 100f, 20f), -Vector3.UnitY, out _));
    }

    [Fact]
    public void ARayThatRunsOutOfLengthBeforeTheGroundMissesIt() {
        var terrain = Flat(0f);

        Assert.False(TerrainPick.Cast(terrain, new(20f, 100f, 20f), -Vector3.UnitY, out _, maximum: 50f));
        Assert.True(TerrainPick.Cast(terrain, new(20f, 100f, 20f), -Vector3.UnitY, out _, maximum: 150f));
    }

    /// <summary>A ray that begins underground answers at once, at its own origin.</summary>
    /// <remarks>
    ///     ⚠ The alternative — marching until it comes out again — aims the brush at whatever is on
    ///     the far side of the hill the camera is inside, which is a pointer that means somewhere
    ///     else entirely.
    /// </remarks>
    [Fact]
    public void ARayStartingUnderTheGroundHitsWhereItStarted() {
        var terrain = Flat(30f);

        Assert.True(TerrainPick.Cast(terrain, new(20f, 5f, 20f), new(1f, 0f, 0f), out var hit));

        Assert.Equal(0f, hit.Distance, 3);
        Assert.Equal(20f, hit.Position.X, 3);
    }

    [Fact]
    public void ADegenerateRayIsRefusedRatherThanLoopingForEver() {
        var terrain = Flat(0f);

        Assert.False(TerrainPick.Cast(terrain, new(20f, 50f, 20f), Vector3.Zero, out _));
        Assert.False(TerrainPick.Cast(terrain, new(20f, 50f, 20f), -Vector3.UnitY, out _, maximum: 0f));
    }

    // --- The height under a point -------------------------------------------

    [Fact]
    public void TheHeightBetweenTwoSamplesIsInterpolated() {
        var terrain = new Terrain(Shape());
        var layer = terrain.AddLayer("Step");

        layer.SetDelta(10, 10, (short)(10f / terrain.Description.MetresPerStep));
        terrain.InvalidateAll();

        // The sample itself, and halfway to its neighbour.
        Assert.Equal(10f, TerrainPick.HeightAt(terrain, 10f, 10f), 2);
        Assert.Equal(5f, TerrainPick.HeightAt(terrain, 10.5f, 10f), 2);
        Assert.Equal(2.5f, TerrainPick.HeightAt(terrain, 10.5f, 10.5f), 2);
    }

    /// <summary>The height is the composite as it is <em>now</em>, not as the cache last saw it.</summary>
    /// <remarks>
    ///     ⚠ <b>The one that would go wrong mid-drag.</b> A stamp invalidates the tiles it touched and
    ///     <c>Resolve</c> runs once per frame, so a pick reading the cache would aim every stamp of a
    ///     stroke at the ground the stroke started from — a brush that digs a hole and then stops
    ///     following the surface down it.
    /// </remarks>
    [Fact]
    public void TheHeightIsReadFromTheDefinitionRatherThanTheStaleCache() {
        var terrain = Flat(0f);
        var layer = terrain.AddLayer("Sculpt");

        layer.SetDelta(20, 20, (short)(40f / terrain.Description.MetresPerStep));
        terrain.Invalidate(new(20, 20, 1, 1));

        // Deliberately not resolved: this is exactly the state a second stamp of a drag arrives in.
        Assert.True(terrain.IsTileDirty(0, 0));
        Assert.Equal(40f, TerrainPick.HeightAt(terrain, 20f, 20f), 1);
    }

    [Fact]
    public void APointOutsideTheTerrainClampsToItsEdge() {
        var terrain = Flat(7f);

        Assert.Equal(7f, TerrainPick.HeightAt(terrain, -100f, -100f), 2);
        Assert.Equal(7f, TerrainPick.HeightAt(terrain, 10_000f, 10_000f), 2);
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Foliage;
using Vixen.Terrain;
using Xunit;

namespace Vixen.Rendering.Terrain.Tests;

using TerrainMap = Vixen.Terrain.Terrain;

/// <summary>The adapter that lets a scatter stand on a heightfield.</summary>
public sealed class TerrainSurfaceTests {
    static TerrainMap Flat(float height = 10f) =>
        new(
            TerrainDescription.Default with { TilesX = 1, TilesZ = 1, TileSamples = 32 },
            height
        );

    [Fact]
    public void TheGroundIsWhereTheHeightfieldSaysItIs() {
        var terrain = Flat(12f);
        var surface = new TerrainSurface(terrain);

        var ground = surface.SampleAt(new(20f, 20f), "");

        Assert.True(ground.Hit);
        Assert.Equal(12f, ground.Position.Y, 1);
        Assert.Equal(20f, ground.Position.X, 3);
        Assert.Equal(20f, ground.Position.Z, 3);
    }

    /// <summary>Off the terrain is nothing, rather than the border repeated for ever.</summary>
    /// <remarks>
    ///     ⚠ <b><c>TerrainPick.HeightAt</c> clamps rather than refusing</b>, which is right for a
    ///     brush that has to aim somewhere and wrong here — unguarded it answers for every position
    ///     in the world, so a field would stretch to the horizon at the height of the edge.
    /// </remarks>
    [Fact]
    public void OffTheTerrainIsMissed() {
        var surface = new TerrainSurface(Flat());

        Assert.False(surface.SampleAt(new(-1f, 20f), "").Hit);
        Assert.False(surface.SampleAt(new(20f, -1f), "").Hit);
        Assert.False(surface.SampleAt(new(100000f, 20f), "").Hit);
        Assert.True(surface.SampleAt(new(1f, 1f), "").Hit);
    }

    [Fact]
    public void FlatGroundFacesUp() {
        var surface = new TerrainSurface(Flat());
        var ground = surface.SampleAt(new(20f, 20f), "");

        Assert.Equal(0f, ground.Slope, 3);
        Assert.Equal(1f, ground.Normal.Y, 3);
    }

    [Fact]
    public void ASlopeReadsAsASlope() {
        var terrain = Flat(0f);
        var description = terrain.Description;
        var layer = terrain.AddLayer("Ramp");

        // A ramp along X: one metre of rise per metre of run is forty-five degrees.
        var rest = description.StoreHeight(0f);

        for (var z = 0; z < description.SamplesZ; z++) {
            for (var x = 0; x < description.SamplesX; x++) {
                var wanted = description.StoreHeight(x * description.MetresPerQuad);

                layer.SetDelta(x, z, (short)(wanted - rest));
            }
        }

        terrain.InvalidateAll();
        terrain.Resolve();

        var surface = new TerrainSurface(terrain);
        var ground = surface.SampleAt(new(20f, 20f), "");

        Assert.Equal(MathF.PI / 4f, ground.Slope, 2);
    }

    [Fact]
    public void APaintedLayerReadsBackByName() {
        var terrain = Flat();
        var grass = terrain.Weights.AddLayer("Grass");

        terrain.Weights.AddLayer("Rock");

        for (var z = 0; z < terrain.Description.SamplesZ; z++) {
            for (var x = 0; x < terrain.Description.SamplesX; x++) {
                terrain.Weights.SetWeight(grass, x, z, x < 8 ? (byte)255 : (byte)0);
            }
        }

        var surface = new TerrainSurface(terrain);

        Assert.Equal(1f, surface.SampleAt(new(4f, 20f), "Grass").Weight, 2);
        Assert.Equal(0f, surface.SampleAt(new(40f, 20f), "Grass").Weight, 2);
    }

    /// <summary>A name nobody painted grows nothing rather than everything.</summary>
    [Fact]
    public void AnUnknownLayerNameAnswersZero() {
        var terrain = Flat();

        terrain.Weights.AddLayer("Grass");

        var surface = new TerrainSurface(terrain);

        Assert.Equal(0f, surface.SampleAt(new(20f, 20f), "Gras").Weight);
        Assert.Equal(1f, surface.SampleAt(new(20f, 20f), "").Weight);
    }

    /// <summary>A hole is not ground.</summary>
    [Fact]
    public void AHoleIsMissed() {
        var terrain = Flat();

        terrain.Holes.SetHole(4, 4, true);

        var surface = new TerrainSurface(terrain);
        var scale = terrain.Description.MetresPerQuad;

        Assert.False(surface.SampleAt(new(4.5f * scale, 4.5f * scale), "").Hit);
        Assert.True(surface.SampleAt(new(20f * scale, 20f * scale), "").Hit);
    }

    /// <summary>A terrain placed away from the origin still answers in world space.</summary>
    [Fact]
    public void AnOriginOffsetsTheWholeAnswer() {
        var surface = new TerrainSurface(Flat(5f), new(1000f, 3f, -500f));

        Assert.False(surface.SampleAt(new(20f, 20f), "").Hit);

        var ground = surface.SampleAt(new(1020f, -480f), "");

        Assert.True(ground.Hit);
        Assert.Equal(8f, ground.Position.Y, 1);
        Assert.Equal(1020f, ground.Position.X, 3);
    }

    /// <summary>And a scatter over it grows grass where the layer is painted.</summary>
    /// <remarks>
    ///     [docs/plan/31 § T6]'s first exit criterion, end to end: a real heightfield, a real
    ///     weightmap, and a field that follows it.
    /// </remarks>
    [Fact]
    public void GrassGrowsOnTheLayerItIsBoundTo() {
        var terrain = Flat();
        var grass = terrain.Weights.AddLayer("Grass");

        terrain.Weights.AddLayer("Rock");

        for (var z = 0; z < terrain.Description.SamplesZ; z++) {
            for (var x = 0; x < terrain.Description.SamplesX; x++) {
                terrain.Weights.SetWeight(grass, x, z, x < 16 ? (byte)255 : (byte)0);
            }
        }

        var surface = new TerrainSurface(terrain);
        var type = GrassType.Of("Meadow") with { Layer = "Grass", Density = 4f };
        var blades = new List<GrassBlade>();

        GrassScatter.Scatter(type, new(0, 0), new(32f), surface, blades);

        var edge = 15f * terrain.Description.MetresPerQuad;

        Assert.NotEmpty(blades);
        Assert.All(blades, blade => Assert.True(blade.Instance.Position.X < edge + 1f));
    }
}

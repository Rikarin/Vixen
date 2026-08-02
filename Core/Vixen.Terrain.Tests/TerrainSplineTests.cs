// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Terrain.Tests;

/// <summary>Roads across a terrain — [docs/plan/31 § T8]'s terrain half.</summary>
public sealed class TerrainSplineTests {
    static TerrainDescription Description =>
        TerrainDescription.Default with { TilesX = 1, TilesZ = 1, TileSamples = 64 };

    static Terrain Flat(float height = 20f) => new(Description, height);

    /// <summary>A road straight down the middle of the terrain.</summary>
    static Spline Straight(float z = 30f, float height = 20f) =>
        new([
            SplinePoint.Smooth(new(2f, height, z), new(10f, 0f, 0f)),
            SplinePoint.Smooth(new(60f, height, z), new(10f, 0f, 0f))
        ]);

    static float HeightAt(Terrain terrain, int x, int z) =>
        terrain.Description.HeightOf(terrain.CompositeAt(x, z));

    [Fact]
    public void TheReservedLayerIsFoundRatherThanAdded() {
        var terrain = Flat();

        var first = TerrainSpline.LayerOf(terrain);
        var again = TerrainSpline.LayerOf(terrain);

        Assert.Same(first, again);
        Assert.Equal(TerrainLayerKind.Splines, first.Kind);
        Assert.Single(terrain.Layers);
    }

    /// <summary>A road flattens the ground under it and leaves the rest alone.</summary>
    [Fact]
    public void ARoadFlattensItsCarriagewayAndNothingBeyondItsShoulder() {
        var terrain = Flat(0f);
        var layer = TerrainSpline.LayerOf(terrain);

        var profile = TerrainSplineProfile.Road with { HalfWidth = 3f, FalloffLeft = 4f, FalloffRight = 4f };

        TerrainSpline.Deform(terrain, layer, Straight(30f, 12f), profile);
        terrain.Resolve();

        // On the centreline and inside the carriageway: at the road's height.
        Assert.Equal(12f, HeightAt(terrain, 30, 30), 1);
        Assert.Equal(12f, HeightAt(terrain, 30, 32), 1);

        // Past the shoulder: untouched.
        Assert.Equal(0f, HeightAt(terrain, 30, 30 + 8), 1);
        Assert.Equal(0f, HeightAt(terrain, 30, 30 - 8), 1);
    }

    /// <summary>The shoulder falls off smoothly rather than in a step.</summary>
    [Fact]
    public void TheShoulderIsMonotonicAndReachesBothEnds() {
        var profile = TerrainSplineProfile.Road with { HalfWidth = 2f, FalloffLeft = 6f, FalloffRight = 6f };

        Assert.Equal(1f, profile.WeightAt(0f), 4);
        Assert.Equal(1f, profile.WeightAt(2f), 4);
        Assert.Equal(0f, profile.WeightAt(8f), 4);
        Assert.Equal(0f, profile.WeightAt(-8f), 4);

        var previous = 1f;

        for (var offset = 2f; offset <= 8f; offset += 0.25f) {
            var weight = profile.WeightAt(offset);

            Assert.True(weight <= previous + 1e-4f, $"the shoulder rose again at {offset} m.");
            previous = weight;
        }
    }

    /// <summary>Left and right are independent, which is what a road cut into a hillside needs.</summary>
    [Fact]
    public void TheTwoSidesFallOffIndependently() {
        var profile = TerrainSplineProfile.Road with { HalfWidth = 1f, FalloffLeft = 8f, FalloffRight = 2f };

        Assert.True(profile.WeightAt(2.5f) > 0.5f, "the wide side fell off like the narrow one.");
        Assert.Equal(0f, profile.WeightAt(-3f), 4);
        Assert.Equal(9f, profile.Reach, 4);
    }

    /// <summary>Moving the road takes the old one with it — but only through Regenerate.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="TerrainSpline.Deform" /> clears its own rect, which is not enough when a
    ///     road moves out of it.</b> A centreline dragged twenty metres leaves its old rect untouched,
    ///     because the new one no longer covers it — so the operation an editor calls is
    ///     <see cref="TerrainSpline.Regenerate" />, which empties the layer and lays every road down
    ///     again. Leaving the old road behind is the failure that makes people stop using
    ///     non-destructive tools, and it is the one this pair pins down in both directions.
    /// </remarks>
    [Fact]
    public void MovingARoadDoesNotLeaveTheOldOneBehind() {
        var terrain = Flat(0f);
        var layer = TerrainSpline.LayerOf(terrain);
        var profile = TerrainSplineProfile.Road;

        TerrainSpline.Regenerate(terrain, layer, [(Straight(20f, 12f), profile)]);
        terrain.Resolve();

        Assert.Equal(12f, HeightAt(terrain, 30, 20), 1);

        TerrainSpline.Regenerate(terrain, layer, [(Straight(40f, 12f), profile)]);
        terrain.Resolve();

        Assert.Equal(12f, HeightAt(terrain, 30, 40), 1);
        Assert.Equal(0f, HeightAt(terrain, 30, 20), 1);

        // ⚠ And Deform on its own does *not* do this, which is why Regenerate exists: a road's own
        // rect no longer covers where it used to be.
        TerrainSpline.Deform(terrain, layer, Straight(20f, 12f), profile);
        terrain.Resolve();

        Assert.Equal(12f, HeightAt(terrain, 30, 20), 1);
        Assert.Equal(12f, HeightAt(terrain, 30, 40), 1);
    }

    /// <summary>And the author's own sculpting underneath survives it.</summary>
    /// <remarks>[§ D4]'s reserved layer is the whole mechanism the non-destructiveness rests on.</remarks>
    [Fact]
    public void TheAuthorsOwnLayerIsUntouched() {
        var terrain = Flat(0f);
        var hand = terrain.AddLayer("Hand");
        var road = TerrainSpline.LayerOf(terrain);

        // A hill somewhere the road does not go.
        for (var z = 50; z < 60; z++) {
            for (var x = 10; x < 20; x++) {
                hand.SetDelta(x, z, 4000);
            }
        }

        TerrainSpline.Deform(terrain, road, Straight(20f, 6f), TerrainSplineProfile.Road);
        terrain.Resolve();

        Assert.Equal(4000, hand.DeltaAt(15, 55));
        Assert.True(HeightAt(terrain, 15, 55) > 5f, "the hand-sculpted hill was flattened.");
    }

    /// <summary>A road follows the ground it is cut into rather than the base heightfield.</summary>
    [Fact]
    public void AShoulderBlendsIntoWhateverTheGroundWasDoing() {
        var terrain = Flat(0f);
        var hand = terrain.AddLayer("Hand");
        var road = TerrainSpline.LayerOf(terrain);

        // Raise the whole terrain by a constant through an author's layer.
        for (var z = 0; z < terrain.Description.SamplesZ; z++) {
            for (var x = 0; x < terrain.Description.SamplesX; x++) {
                hand.SetDelta(x, z, 3277);
            }
        }

        terrain.Resolve();

        var raised = HeightAt(terrain, 30, 45);

        TerrainSpline.Deform(terrain, road, Straight(20f, 4f), TerrainSplineProfile.Road);
        terrain.Resolve();

        Assert.Equal(4f, HeightAt(terrain, 30, 20), 1);
        Assert.Equal(raised, HeightAt(terrain, 30, 45), 1);
    }

    /// <summary>Painting along the width keeps the sum-to-one invariant.</summary>
    [Fact]
    public void PaintingAlongARoadKeepsTheWeightsSummingToOne() {
        var terrain = Flat();
        var grass = terrain.Weights.AddLayer("Grass");
        var gravel = terrain.Weights.AddLayer("Gravel");

        for (var z = 0; z < terrain.Description.SamplesZ; z++) {
            for (var x = 0; x < terrain.Description.SamplesX; x++) {
                terrain.Weights.SetWeight(grass, x, z, 255);
            }
        }

        TerrainSpline.PaintAlong(terrain, Straight(), gravel, TerrainSplineProfile.Road);

        Assert.Null(terrain.Weights.Verify());
        Assert.True(terrain.Weights.WeightAt(gravel, 30, 30) > 200, "the carriageway was not painted.");
        Assert.Equal(0, terrain.Weights.WeightAt(gravel, 30, 45));
    }

    /// <summary>Meshes are spaced by distance along the curve, not by parameter.</summary>
    /// <remarks>
    ///     ⚠ <b>Spacing by parameter bunches everything up in the tight segments and strings it out
    ///     in the wide ones</b>, which is exactly wrong for a fence.
    /// </remarks>
    [Fact]
    public void MeshesAreEvenlySpacedAlongTheCurve() {
        var spline = new Spline([
            SplinePoint.Smooth(new(0f, 0f, 0f), new(40f, 0f, 0f)),
            SplinePoint.Smooth(new(20f, 0f, 0f), new(2f, 0f, 0f)),
            SplinePoint.Smooth(new(60f, 0f, 0f), new(40f, 0f, 0f))
        ]);

        var placed = TerrainSpline.PlaceAlong(spline, ["Meshes/post"], 5f);

        Assert.True(placed.Count > 4);

        for (var index = 1; index < placed.Count; index++) {
            var gap = Vector3.Distance(placed[index].Position, placed[index - 1].Position);

            Assert.InRange(gap, 4f, 6f);
        }
    }

    /// <summary>The choice of mesh is hashed, so re-running does not re-roll the fence.</summary>
    [Fact]
    public void TheMeshChoiceIsStableAcrossRuns() {
        var spline = Straight();
        string[] meshes = ["Meshes/a", "Meshes/b", "Meshes/c"];

        var first = TerrainSpline.PlaceAlong(spline, meshes, 4f);
        var again = TerrainSpline.PlaceAlong(spline, meshes, 4f);

        Assert.Equal(first, again);
        Assert.True(first.Select(placement => placement.Mesh).Distinct().Count() > 1);
    }

    [Fact]
    public void AMeshFacesAlongTheCurve() {
        var spline = Straight();
        var placed = TerrainSpline.PlaceAlong(spline, ["Meshes/post"], 6f);

        Assert.NotEmpty(placed);

        foreach (var placement in placed) {
            // An entity faces its local −Z, so the forward vector should lie along +X here.
            var forward = Quaternion.Transform(new Vector3(0f, 0f, -1f), placement.Rotation);

            Assert.True(forward.X > 0.99f, $"a post at {placement.Distance} m faces {forward}.");
        }
    }

    [Fact]
    public void AProfileThatTouchesNothingIsRefused() {
        var terrain = Flat();
        var layer = TerrainSpline.LayerOf(terrain);

        var thrown = Assert.Throws<ArgumentException>(
            () => TerrainSpline.Deform(
                terrain,
                layer,
                Straight(),
                new TerrainSplineProfile { HalfWidth = 0f, FalloffLeft = 0f, FalloffRight = 0f }
            )
        );

        Assert.Contains("touches nothing", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A strength below one is a hint rather than a road.</summary>
    [Fact]
    public void AWeakProfileFollowsTheGroundItWasDrawnIn() {
        var terrain = Flat(0f);
        var layer = TerrainSpline.LayerOf(terrain);

        TerrainSpline.Deform(terrain, layer, Straight(30f, 20f), TerrainSplineProfile.Road with { Strength = 0.5f });
        terrain.Resolve();

        Assert.Equal(10f, HeightAt(terrain, 30, 30), 1);
    }

    /// <summary>The bounding rect covers the bends, not only the control points.</summary>
    /// <remarks>
    ///     ⚠ <b>A Hermite segment leaves the hull of its two endpoints whenever the tangents are
    ///     long</b>, so a rect that stopped at the control points would cut the bends off a road.
    /// </remarks>
    [Fact]
    public void TheAffectedRectCoversTheBendsAndNotOnlyThePoints() {
        var terrain = Flat(0f);
        var layer = TerrainSpline.LayerOf(terrain);

        // Two points on one line with tangents that bow the curve well off it.
        var bowed = new Spline([
            SplinePoint.Smooth(new(10f, 8f, 30f), new(0f, 0f, 40f)),
            SplinePoint.Smooth(new(50f, 8f, 30f), new(0f, 0f, -40f))
        ]);

        var rect = TerrainSpline.Deform(terrain, layer, bowed, TerrainSplineProfile.Road);
        terrain.Resolve();

        Assert.True(rect.Height > 20, $"the rect is only {rect.Height} samples tall; the bow was cut off.");
        Assert.True(HeightAt(terrain, 30, 38) > 4f, "the crown of the bend was never deformed.");
    }
}

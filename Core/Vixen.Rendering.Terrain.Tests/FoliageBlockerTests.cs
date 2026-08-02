// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Foliage;
using Xunit;

namespace Vixen.Rendering.Terrain.Tests;

/// <summary>
///     Blocking volumes as scene objects — [docs/plan/31 § T9]'s owed item, and the bounds query the
///     growth kernel deliberately cannot name.
/// </summary>
public sealed class FoliageBlockerTests {
    static Entity Blocker(World world, Vector3 at, Vector3 extent, Vector3 scale = default, bool enabled = true) {
        var entity = world.Create();

        world.Add(entity, LocalTransform.Identity with { Position = at, Scale = scale == default ? Vector3.One : scale });
        world.Add(entity, new FoliageBlockerComponent { Extent = extent, IsEnabled = enabled });

        return entity;
    }

    [Fact]
    public void EveryEnabledBlockerIsGathered() {
        using var world = new World();

        Blocker(world, new(10f, 0f, 10f), new(4f, 4f, 4f));
        Blocker(world, new(-20f, 0f, 5f), new(2f, 8f, 2f));

        var blockers = FoliageBlockers.Gather(world);

        Assert.Equal(2, blockers.Count);
        Assert.Contains(blockers, blocker => blocker.Centre == new Vector3(10f, 0f, 10f));
    }

    /// <summary>And a disabled one is not.</summary>
    /// <remarks>
    ///     ⚠ <b>A switch rather than deleting the entity</b>, because a blocker is scenery an artist
    ///     turns off to see what a hillside looks like without it — and an entity deleted to answer
    ///     that question has to be drawn again from memory.
    /// </remarks>
    [Fact]
    public void ADisabledBlockerIsNotGathered() {
        using var world = new World();

        Blocker(world, Vector3.Zero, new(4f), enabled: false);

        Assert.Empty(FoliageBlockers.Gather(world));
    }

    /// <summary>The entity's transform is where it is, and its scale is how big.</summary>
    /// <remarks>
    ///     ⚠ <b>Giving the component a position of its own would be two answers to where it is.</b> A
    ///     blocker where a building will be is a person dragging a box in the viewport, so its
    ///     placement is a <c>LocalTransform</c> like everything else's.
    /// </remarks>
    [Fact]
    public void TheTransformPlacesItAndScalesIt() {
        using var world = new World();

        Blocker(world, new(5f, 1f, -5f), new(2f, 3f, 4f), scale: new(2f, 1f, 0.5f));

        var blocker = Assert.Single(FoliageBlockers.Gather(world));

        Assert.Equal(new Vector3(5f, 1f, -5f), blocker.Centre);
        Assert.Equal(new Vector3(4f, 3f, 2f), blocker.Extent);
    }

    /// <summary>A negative scale mirrors the box rather than turning it inside out.</summary>
    /// <remarks>
    ///     ⚠ <b>A blocker whose extent went negative would contain nothing</b>, which reads as the
    ///     volume having stopped working rather than as a sign on a number.
    /// </remarks>
    [Fact]
    public void ANegativeScaleStillBlocks() {
        using var world = new World();

        Blocker(world, Vector3.Zero, new(3f), scale: new(-1f, 1f, -1f));

        var blocker = Assert.Single(FoliageBlockers.Gather(world));

        Assert.Equal(new Vector3(3f), blocker.Extent);
        Assert.True(blocker.Contains(new(1f, 0f, 1f)));
    }

    /// <summary>What the kernel does with them: nothing grows inside one.</summary>
    /// <remarks>
    ///     The seam end to end — a world of entities becomes a list the simulation takes, and the
    ///     simulation never asks where the list came from.
    /// </remarks>
    [Fact]
    public void NothingGrowsInsideAGatheredBlocker() {
        using var world = new World();

        Blocker(world, new(50f, 0f, 50f), new(40f, 10f, 40f));

        var volume = new FoliageVolume(new(32f));

        volume.AddType(
            FoliageType.Of("Pine") with {
                Mesh = "Meshes/pine",
                Radius = 2f,
                Ecology = FoliageEcology.Tree with { SeedDensity = 0.01f }
            }
        );

        var settings = FoliageGrowthSettings.Over(new(0f, 0f), new(100f, 100f), seed: 4242u) with { Steps = 4 };
        var blockers = FoliageBlockers.Gather(world);

        var result = FoliageGrowth.Simulate(volume, new Flat(), settings, blockers);

        Assert.True(result.Placed > 0, "nothing grew at all, so the blocker proves nothing.");
        Assert.True(result.Blocked > 0, "the blocker refused nothing.");

        foreach (var chunk in volume.Chunks) {
            foreach (var instance in chunk.Instances) {
                Assert.False(
                    blockers[0].Contains(instance.Position),
                    $"a plant grew at {instance.Position}, inside the blocker."
                );
            }
        }
    }

    /// <summary>Gathering into a list clears it, so a second run is not a second forest's worth.</summary>
    [Fact]
    public void GatheringClearsWhatItIsGiven() {
        using var world = new World();

        Blocker(world, Vector3.Zero, new(4f));

        var into = new List<FoliageBlocker> { new(new(999f, 0f, 999f), new(1f)) };

        Assert.Equal(1, FoliageBlockers.Gather(world, into));
        Assert.Single(into);
        Assert.Equal(Vector3.Zero, into[0].Centre);
    }

    /// <summary>Flat ground everywhere, so the simulation has somewhere to sow.</summary>
    sealed class Flat : IFoliageSurface {
        public FoliageSurface SampleAt(Vector2 position, string layer) =>
            new(new(position.X, 0f, position.Y), Vector3.UnitY, 1f, true);
    }
}

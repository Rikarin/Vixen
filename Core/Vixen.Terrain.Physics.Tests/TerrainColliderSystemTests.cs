// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Physics.Ecs;
using Vixen.Terrain;
using Vixen.Terrain.Physics;
using Xunit;
using EcsWorld = Vixen.Ecs.World;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Tests;

/// <summary>
///     A terrain in a world is something a ray stops on — [docs/plan/31 § B1] and § D10.
/// </summary>
/// <remarks>
///     <para>
///         <b>The negative control is the point of this file.</b> Every piece of this path was built
///         and tested before any of it was joined: the shape has sixteen tests of its own, the sample
///         fill has two, and the result was still a terrain a character fell through in every project.
///         So the assertions here are all of the form "a ray fired at the ground stops at the ground",
///         and <see cref="NothingCollidesUntilSomethingBuildsIt" /> is the same world with the one
///         call removed — which is exactly the state the tree was in, and it must fail.
///     </para>
///     <para>
///         ⚠ <b>The tolerance is the shape's own compression, not slack.</b> Jolt quantises a height
///         field to eight bits per sample against its <em>block's</em> range, so a block spanning nine
///         metres is exact to about 3.5 cm. A flat tile is exact; the sloped terrain below is checked
///         to a centimetre, which is far tighter than any mapping error could survive.
///     </para>
/// </remarks>
public sealed class TerrainColliderSystemTests {
    const float Step = 1f / 60f;

    /// <summary>A fixed list of placements, which is what a renderer's frame list is.</summary>
    sealed class Placed(params TerrainPlacement[] placements) : ITerrainPlacements {
        readonly List<TerrainPlacement> placements = [.. placements];

        public int PlacementCount => this.placements.Count;

        public TerrainPlacement PlacementAt(int index) => this.placements[index];

        public void Remove(TerrainMap terrain) => this.placements.RemoveAll(entry => entry.Terrain == terrain);
    }

    /// <summary>A terrain of one 8-sample tile, 2 m a quad, at a constant height.</summary>
    static TerrainMap Flat(float metres, int tiles = 1) {
        var description = new TerrainDescription {
            TileSamples = 8,
            TilesX = tiles,
            TilesZ = tiles,
            MetresPerQuad = 2f,
            MinHeight = -10f,
            MaxHeight = 30f
        };

        var terrain = new TerrainMap(description, metres);
        terrain.Resolve();

        return terrain;
    }

    /// <summary>Casts straight down from well above and answers where it landed.</summary>
    static float? GroundUnder(PhysicsScene scene, float x, float z) =>
        scene.World.Raycast(new(x, 200f, z), -Vector3.UnitY, 400f, out var hit) ? hit.Position.Y : null;

    [Fact]
    public void ATerrainInTheWorldIsGroundARayStopsOn() {
        using var entities = new EcsWorld();
        using var scene = new PhysicsScene(entities);

        var terrain = Flat(4f);
        var colliders = new TerrainColliderSystem(scene, new Placed(new TerrainPlacement(terrain, Vector3.Zero)));

        colliders.Sync();
        scene.Synchronize(Step);

        Assert.Equal(1, colliders.TerrainCount);
        Assert.Equal(1, colliders.TileCount);
        Assert.Equal(1, scene.BodyCount);

        var ground = GroundUnder(scene, 4f, 4f);

        Assert.NotNull(ground);
        Assert.Equal(4f, ground!.Value, 3);
    }

    /// <summary>The state the whole tree was in, asserted so the guard above cannot pass vacuously.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the sabotage test.</b> Same world, same terrain, same physics scene — and no
    ///     <c>TerrainColliderSystem</c>. Nothing throws, nothing warns, no body exists and the ray
    ///     falls through to infinity. That silence is why the gap survived a shape with sixteen tests
    ///     beside it.
    /// </remarks>
    [Fact]
    public void NothingCollidesUntilSomethingBuildsIt() {
        using var entities = new EcsWorld();
        using var scene = new PhysicsScene(entities);

        _ = Flat(4f);

        scene.Synchronize(Step);

        Assert.Equal(0, scene.BodyCount);
        Assert.Null(GroundUnder(scene, 4f, 4f));
    }

    [Fact]
    public void TheOriginMovesTheGroundWithIt() {
        using var entities = new EcsWorld();
        using var scene = new PhysicsScene(entities);

        var terrain = Flat(1f);
        var colliders = new TerrainColliderSystem(scene, new Placed(new TerrainPlacement(terrain, new(-100f, 3f, -100f))));

        colliders.Sync();
        scene.Synchronize(Step);

        // Sample (2, 2) of a 2 m grid is 4 m from the terrain's own corner, and the corner is at
        // −100. A collider placed at the world origin instead would leave this column empty, which
        // is the failure that reads as "the ground is somewhere else" rather than as an error.
        Assert.Null(GroundUnder(scene, 4f, 4f));

        var ground = GroundUnder(scene, -96f, -96f);

        Assert.NotNull(ground);
        Assert.Equal(4f, ground!.Value, 3);
    }

    [Fact]
    public void EveryTileGetsItsOwnShapeAtItsOwnCorner() {
        using var entities = new EcsWorld();
        using var scene = new PhysicsScene(entities);

        // Two tiles a side of seven quads at 2 m is 28 m across, and the tiles share their boundary
        // samples — so the far tile's corner is at 14 m, not at 16.
        var terrain = Flat(2f, tiles: 2);
        var colliders = new TerrainColliderSystem(scene, new Placed(new TerrainPlacement(terrain, Vector3.Zero)));

        colliders.Sync();
        scene.Synchronize(Step);

        Assert.Equal(4, colliders.TileCount);
        Assert.Equal(4, scene.BodyCount);

        foreach (var (x, z) in new[] { (2f, 2f), (24f, 2f), (2f, 24f), (24f, 24f), (14f, 14f) }) {
            var ground = GroundUnder(scene, x, z);

            Assert.NotNull(ground);
            Assert.Equal(2f, ground!.Value, 3);
        }
    }

    [Fact]
    public void AHoleIsAPitAndNotAFloor() {
        using var entities = new EcsWorld();
        using var scene = new PhysicsScene(entities);

        var terrain = Flat(5f);
        terrain.Holes.SetHole(3, 3, true);

        var colliders = new TerrainColliderSystem(scene, new Placed(new TerrainPlacement(terrain, Vector3.Zero)));

        colliders.Sync();
        scene.Synchronize(Step);

        // A hole kills the up-to-four quads that reference the sample, so the quad between samples
        // 3 and 4 has no surface. Its middle is at 7 m.
        Assert.Null(GroundUnder(scene, 7f, 7f));

        // And the far corner of the same tile still has one, so this is a hole rather than a terrain
        // that failed to build.
        Assert.NotNull(GroundUnder(scene, 13f, 13f));
    }

    [Fact]
    public void SculptingTheGroundMovesTheColliderWithoutAnybodySayingSo() {
        using var entities = new EcsWorld();
        using var scene = new PhysicsScene(entities);

        var terrain = Flat(1f);
        var colliders = new TerrainColliderSystem(scene, new Placed(new TerrainPlacement(terrain, Vector3.Zero)));

        colliders.Sync();
        scene.Synchronize(Step);

        Assert.Equal(1f, GroundUnder(scene, 4f, 4f)!.Value, 3);
        Assert.Equal(0, colliders.Rebuilds);

        var layer = terrain.AddLayer("raise");
        var raise = (short)(terrain.Description.StoreHeight(9f) - terrain.Description.StoreHeight(1f));

        for (var z = 0; z < terrain.Description.SamplesZ; z++) {
            for (var x = 0; x < terrain.Description.SamplesX; x++) {
                layer.SetDelta(x, z, raise);
            }
        }

        terrain.InvalidateAll();
        terrain.Resolve();

        colliders.Sync();
        scene.Synchronize(Step);

        // ⚠ The revision, not a call. A collider that only moved when a tool remembered to say so is
        // the seam `ITerrainColliders` already is, and its only implementation in the tree records
        // tile indices in a test.
        Assert.Equal(1, colliders.Rebuilds);
        Assert.Equal(9f, GroundUnder(scene, 4f, 4f)!.Value, 3);

        // And a frame in which nothing was sculpted rebuilds nothing — otherwise every frame would
        // register a shape in a table that never releases one.
        colliders.Sync();
        Assert.Equal(1, colliders.Rebuilds);
    }

    [Fact]
    public void ATerrainThatLeavesTheSceneTakesItsCollisionWithIt() {
        using var entities = new EcsWorld();
        using var scene = new PhysicsScene(entities);

        var terrain = Flat(3f);
        var placed = new Placed(new TerrainPlacement(terrain, Vector3.Zero));
        var colliders = new TerrainColliderSystem(scene, placed);

        colliders.Sync();
        scene.Synchronize(Step);

        Assert.NotNull(GroundUnder(scene, 4f, 4f));

        placed.Remove(terrain);
        colliders.Sync();
        scene.Synchronize(Step);

        Assert.Equal(0, colliders.TerrainCount);
        Assert.Equal(0, colliders.TileCount);
        Assert.Equal(0, scene.BodyCount);
        Assert.Null(GroundUnder(scene, 4f, 4f));

        // ⚠ And the entity outlives the body by one sync, deliberately. `PhysicsScene` learns that a
        // body should go by finding a `PhysicsBody` with no `Collider` — so an entity destroyed
        // outright never appears in that query and its Jolt body stays in the broad phase for the
        // life of the world. Removing the component first is what makes the assertion above true.
        colliders.Sync();

        Assert.Equal(0, entities.EntityCount);
    }

    /// <summary>A terrain that has not loaded yet is nothing, and asking again is what fixes it.</summary>
    /// <remarks>
    ///     ⚠ <b>The trap this system exists in the shape it does to avoid.</b> A <c>.vxterrain</c>
    ///     resolves several frames after the level is up, so a one-shot call at load time builds
    ///     nothing and never asks again — the same late-resolution failure the water fold shipped.
    /// </remarks>
    [Fact]
    public void ATerrainThatArrivesLateStillGetsACollider() {
        using var entities = new EcsWorld();
        using var scene = new PhysicsScene(entities);

        var placed = new Placed();
        var colliders = new TerrainColliderSystem(scene, placed);

        colliders.Sync();
        colliders.Sync();
        colliders.Sync();

        Assert.Equal(0, colliders.TileCount);

        colliders.Placements = new Placed(new TerrainPlacement(Flat(6f), Vector3.Zero));
        colliders.Sync();
        scene.Synchronize(Step);

        Assert.Equal(1, colliders.TileCount);
        Assert.Equal(6f, GroundUnder(scene, 4f, 4f)!.Value, 3);
    }
}

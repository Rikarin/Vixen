// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Terrain;
using Vixen.Editor.Terrain.Physics;
using Vixen.Engine.Transforms;
using Vixen.Physics.Ecs;
using Vixen.Terrain;
using Vixen.Terrain.Physics;
using Xunit;
using EcsWorld = Vixen.Ecs.World;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Tests;

/// <summary>
///     A sculpt stroke changes what a body rests on — <c>docs/plan/31</c> § D10's last owed half.
/// </summary>
/// <remarks>
///     <para>
///         <b>The assertion is a dropped body, not a call count.</b> The editor already had a test
///         that a stroke names the tiles it touched: <c>TerrainEditTests</c> asserts it against
///         <c>RecordingColliders</c>, a double that records indices and builds nothing. That test
///         passed for a year while a stroke rebuilt no collision anywhere, because the only thing on
///         the other side of the seam was the double. So everything here settles a rigid body onto
///         real Jolt geometry and reads its height back.
///     </para>
///     <para>
///         ⚠ <b><see cref="SculptingWithNoAdapterLeavesTheBodyOnGroundThatIsNoLongerThere" /> is the
///         sabotage, and it is the state the tree was actually in.</b> Same terrain, same stroke,
///         same physics world, and <c>TerrainEdit.Colliders</c> left null — the ground moves five
///         metres, the crate goes on resting in mid-air where the ground used to be, and nothing
///         throws or warns.
///     </para>
///     <para>
///         ⚠ <b>The tolerances are Jolt's height-field compression, not slack.</b> A height field is
///         eight bits per sample against its <em>block's</em> range, so a raised dome quantises to
///         centimetres; a resting box also sits a collision margin above the surface. Every
///         comparison here is to a decimetre, which no mapping error survives.
///     </para>
/// </remarks>
public sealed class SculptedGroundTests {
    const float Step = 1f / 60f;

    /// <summary>Where the crate is dropped from and where the stroke lands, in terrain metres.</summary>
    const float Spot = 20f;

    /// <summary>Half the crate's side, which is how far its centre rests above what holds it.</summary>
    const float HalfExtent = 0.5f;

    /// <summary>A fixed list of placements, which is what a renderer's frame list is.</summary>
    sealed class Placed(params TerrainPlacement[] placements) : ITerrainPlacements {
        readonly List<TerrainPlacement> placements = [.. placements];

        public int PlacementCount => this.placements.Count;

        public TerrainPlacement PlacementAt(int index) => this.placements[index];
    }

    /// <summary>
    ///     Four tiles of 32 samples at a metre a quad: 63 × 63 samples over 62 × 62 metres, flat.
    /// </summary>
    /// <remarks>
    ///     <c>Vixen.Editor.Terrain.Tests</c>' own <c>Ground.Shape</c>, so a stroke that behaves one way
    ///     in the toolset's tests behaves the same way here.
    /// </remarks>
    static TerrainMap Flat() {
        var terrain = new TerrainMap(
            TerrainDescription.Default with {
                TileSamples = 32, TilesX = 2, TilesZ = 2,
                MetresPerQuad = 1f, MinHeight = -100f, MaxHeight = 100f
            }
        );

        terrain.AddLayer("Sculpt");
        terrain.Resolve();

        return terrain;
    }

    /// <summary>The editing state a stroke is driven through, aimed and loaded.</summary>
    /// <remarks>
    ///     ⚠ <b>A twelve-metre brush, so the dome is flat over the crate.</b> A sculpt stroke has a
    ///     falloff; a box half a metre wide balanced on the tip of a sharp one would settle by
    ///     tipping, and the number this file reads back would be a rotation rather than a height.
    /// </remarks>
    static TerrainEdit Editing(TerrainMap terrain) {
        var edit = new TerrainEdit { Terrain = terrain };

        edit.Brush.Radius = 12f;
        edit.Brush.Strength = 1f;
        edit.Tools.Metres = 5f;

        return edit;
    }

    /// <summary>Raises the ground under <see cref="Spot" /> by one stroke of the sculpt tool.</summary>
    static void Raise(TerrainEdit edit) {
        edit.Begin(new(Spot, Spot));
        edit.Commit();
    }

    /// <summary>Drops a crate from well above <see cref="Spot" /> and answers where it stopped.</summary>
    /// <returns>The height of its centre at rest.</returns>
    /// <remarks>
    ///     ⚠ <b>A new crate every time rather than the same one moved back up, and the difference is
    ///     not tidiness.</b> Writing <c>LocalTransform</c> on an entity that already has a body does
    ///     not teleport it — <c>PhysicsScene.Writeback</c> puts Jolt's own position back over
    ///     whatever was written — so a second measurement taken that way reads the first one's
    ///     answer, and reads it whether or not the ground moved. That is a harness which proves the
    ///     defect it was built to prove, in both directions.
    /// </remarks>
    static float RestHeight(PhysicsScene scene) {
        var crate = scene.Entities.Create(LocalTransform.At(new(Spot, 40f, Spot)));

        scene.Entities.Add(crate, Collider.Of(scene.Shapes.Box(HalfExtent)));
        scene.Entities.Add(crate, RigidBody.Dynamic());

        for (var step = 0; step < 400; step++) {
            scene.Synchronize(Step);
            scene.Step(Step);
            scene.Writeback();
        }

        var rest = scene.Entities.Read<LocalTransform>(crate).Position.Y;

        // ⚠ The collider first and the entity after a sync, which is `TerrainColliderSystem.Forget`'s
        // own order and for its reason: the scene finds a body to destroy by querying for a
        // `PhysicsBody` with no `Collider` beside it, so an entity destroyed outright leaves its Jolt
        // body standing in the broad phase — and the next drop would land on the crate before it.
        scene.Entities.Remove<Collider>(crate);
        scene.Synchronize(Step);
        scene.Entities.Destroy(crate);

        return rest;
    }

    /// <summary>What the composite says the ground is under the crate, after recompositing.</summary>
    static float GroundAt(TerrainMap terrain) {
        terrain.Resolve();

        return terrain.Composite.MetresAt((int)Spot, (int)Spot);
    }

    /// <summary>
    ///     The whole of what this assembly is for: a stroke moves the ground and the body follows.
    /// </summary>
    [Fact]
    public void ASculptStrokeMovesWhatABodyRestsOn() {
        using var entities = new EcsWorld();
        using var scene = new PhysicsScene(entities);

        var terrain = Flat();
        var system = new TerrainColliderSystem(scene, new Placed(new TerrainPlacement(terrain, Vector3.Zero)));
        var edit = Editing(terrain);

        edit.Colliders = new TerrainColliders(system);

        system.Sync();

        var before = RestHeight(scene);

        // Flat ground at zero, so the crate rests a half-extent up.
        Assert.Equal(HalfExtent, before, 1);

        Raise(edit);

        // ⚠ No Sync between the stroke and the drop, deliberately. The per-frame poll would rebuild
        // this tile anyway — that is what makes a terrain in a *game* follow sculpting — and if it
        // ran here the test would pass with the adapter deleted. What is being asserted is that the
        // stroke itself did it, on the frame it landed.
        var after = RestHeight(scene);
        var ground = GroundAt(terrain);

        Assert.True(ground > 4f, $"the stroke should have raised the ground; it is at {ground} m.");
        Assert.True(
            after - before > 4f,
            $"the crate rested at {before} m before the stroke and {after} m after it."
        );

        Assert.Equal(ground + HalfExtent, after, 1);
    }

    /// <summary>The state the tree was in: the seam exists, nothing implements it, nothing moves.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the sabotage test.</b> Identical to the one above with the one assignment
    ///     removed. It must fail if <c>TerrainEdit.Commit</c> ever stops calling the seam, and it is
    ///     what stops the test above from passing on the poll.
    /// </remarks>
    [Fact]
    public void SculptingWithNoAdapterLeavesTheBodyOnGroundThatIsNoLongerThere() {
        using var entities = new EcsWorld();
        using var scene = new PhysicsScene(entities);

        var terrain = Flat();
        var system = new TerrainColliderSystem(scene, new Placed(new TerrainPlacement(terrain, Vector3.Zero)));
        var edit = Editing(terrain);

        // The unfed seam, which is what `TerrainEdit.Colliders` was everywhere outside a test.
        edit.Colliders = null;

        system.Sync();

        var before = RestHeight(scene);

        Raise(edit);

        var after = RestHeight(scene);

        Assert.True(GroundAt(terrain) > 4f, "the stroke should still have moved the ground itself.");

        // And the collision did not move with it: the crate rests five metres inside the hill.
        Assert.Equal(before, after, 1);
    }

    /// <summary>The poll is still there, and it agrees with the push rather than fighting it.</summary>
    /// <remarks>
    ///     ⚠ <b>Doing both is the failure mode worth a test.</b> A push that did not stamp the tile's
    ///     revision would be rebuilt again by the very next <c>Sync</c> — one Jolt shape per frame in
    ///     a table <c>PhysicsShapes</c> never releases. <see cref="TerrainColliderSystem.Rebuilds" />
    ///     is what makes that visible.
    /// </remarks>
    [Fact]
    public void APushedRebuildIsNotDoneAgainByTheNextPoll() {
        using var entities = new EcsWorld();
        using var scene = new PhysicsScene(entities);

        var terrain = Flat();
        var system = new TerrainColliderSystem(scene, new Placed(new TerrainPlacement(terrain, Vector3.Zero)));
        var edit = Editing(terrain);

        edit.Colliders = new TerrainColliders(system);

        system.Sync();
        Assert.Equal(0, system.Rebuilds);

        Raise(edit);

        // The stroke rebuilt through the adapter, which is not counted — `Rebuilds` counts what the
        // poll found stale.
        Assert.Equal(0, system.Rebuilds);

        system.Sync();
        system.Sync();

        Assert.Equal(0, system.Rebuilds);
    }

    /// <summary>Without the push, the poll catches up — one frame later, which is the difference.</summary>
    [Fact]
    public void WithoutThePushThePollGetsThereOnTheNextFrame() {
        using var entities = new EcsWorld();
        using var scene = new PhysicsScene(entities);

        var terrain = Flat();
        var system = new TerrainColliderSystem(scene, new Placed(new TerrainPlacement(terrain, Vector3.Zero)));
        var edit = Editing(terrain);

        system.Sync();

        Assert.Equal(HalfExtent, RestHeight(scene), 1);

        Raise(edit);
        system.Sync();

        Assert.True(system.Rebuilds > 0, "the poll should have found the sculpted tile stale.");
        Assert.Equal(GroundAt(terrain) + HalfExtent, RestHeight(scene), 1);
    }

    /// <summary>
    ///     A terrain the physics world was never told about is counted rather than silently skipped.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The line the "three lines" claim was missing.</b> Both <c>Rebuild</c> overloads on
    ///     <see cref="TerrainColliderSystem" /> return <see langword="bool" /> and
    ///     <see cref="ITerrainColliders" /> returns <see langword="void" />, so a forwarding wrapper
    ///     that discarded the value would report success for every stroke while rebuilding nothing —
    ///     which is the failure this whole task is about, one layer in.
    /// </remarks>
    [Fact]
    public void AStrokeOnATerrainNothingPlacedIsCountedAsMissed() {
        using var entities = new EcsWorld();
        using var scene = new PhysicsScene(entities);

        var terrain = Flat();

        // A physics world that has been told about no terrain at all, which is a scene being sculpted
        // before anything placed it — `ITerrainColliders`' own "null is a terrain with no collision".
        var system = new TerrainColliderSystem(scene, new Placed());
        var adapter = new TerrainColliders(system);
        var edit = Editing(terrain);

        edit.Colliders = adapter;

        system.Sync();
        Raise(edit);

        Assert.Equal(1, adapter.Missed);
        Assert.Equal(0, system.TileCount);
    }

    /// <summary>And a terrain placed but not yet built is picked up rather than missed twice.</summary>
    /// <remarks>
    ///     The late-arrival case from the other side: the stroke lands before anything ticked the
    ///     system, so the first rebuild finds nothing, syncs, and the second one has bodies to move.
    /// </remarks>
    [Fact]
    public void AStrokeBeforeTheFirstSyncBuildsTheTerrainRatherThanMissingIt() {
        using var entities = new EcsWorld();
        using var scene = new PhysicsScene(entities);

        var terrain = Flat();
        var system = new TerrainColliderSystem(scene, new Placed(new TerrainPlacement(terrain, Vector3.Zero)));
        var adapter = new TerrainColliders(system);
        var edit = Editing(terrain);

        edit.Colliders = adapter;

        // No Sync at all: nothing has ever built a body for this terrain.
        Assert.Equal(0, system.TileCount);

        Raise(edit);

        Assert.Equal(1, adapter.Missed);
        Assert.Equal(4, system.TileCount);

        Assert.Equal(GroundAt(terrain) + HalfExtent, RestHeight(scene), 1);
    }
}

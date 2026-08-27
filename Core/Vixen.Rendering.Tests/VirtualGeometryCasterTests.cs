// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Graphics.Null;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Features;
using Xunit;

namespace Vixen.Rendering.Tests;

/// <summary>A virtualized mesh casts a shadow, and it is a shadow of the mesh that is drawn.</summary>
/// <remarks>
///     <para>
///         <b>What <c>docs/plan/22-virtualized-geometry.md</c> phase 7 names as owed, in the half that
///         was buildable.</b> The cluster traversal appends every view's cut to one visible list with no
///         view tag on an entry, so a shadow view cannot have a cut of its own until phase 3 grows one.
///         Until it does, a virtualized mesh casts through <c>MeshletMesh.Fallback</c> — which phase 1
///         generates for exactly this and which nothing had ever read.
///     </para>
///     <para>
///         ⚠ <b>The failure this closes reported nothing.</b> <see cref="MeshExtractionSystem" /> stamped
///         a virtualized object with the caster stages, and the virtualized feature draws no per-object
///         command at all — so the stage node walked past it. Every counter in the frame was healthy, the
///         mesh was pixel-correct, and its shadow was absent. That is why these assert on the second
///         object's existence and its stage mask rather than on a number of draws.
///     </para>
/// </remarks>
public sealed class VirtualGeometryCasterTests : IDisposable {
    readonly NullDevice device = new(new());
    readonly GeometryBuffer buffer;
    readonly GeometryResidency residency;
    readonly RenderSystem system = new();
    readonly MeshRenderFeature meshes = new();
    readonly TransformRenderFeature transforms = new();
    readonly MaterialRenderFeature materials = new();
    readonly VirtualGeometryRenderFeature virtualized = new();
    readonly MeshExtractionSystem extraction;
    readonly RenderStage opaque;
    readonly RenderStage casters;

    static readonly AssetReference Rock = new(new AssetId(new("77777777-7777-7777-7777-777777777777")));

    /// <summary>How many triangles the source mesh has, so the fallback can have fewer.</summary>
    const int SourceTriangles = 4;

    /// <summary>How many the fallback keeps. Two, so "it drew the whole mesh" is a different number.</summary>
    static readonly int[] Fallback = [0, 1, 2, 1, 2, 3];

    public VirtualGeometryCasterTests() {
        buffer = new(device, SurfaceVertex.SizeInBytes, vertexCapacity: 4096, indexCapacity: 8192);
        residency = new(buffer);

        opaque = system.AddStage(new("Opaque"));
        casters = system.AddStage(new("Casters"));

        meshes.Add(transforms);
        meshes.Add(materials);

        system.AddFeature(meshes);
        system.AddFeature(virtualized);

        extraction = new(system, meshes, transforms, materials, residency) {
            Stages = opaque.Mask | casters.Mask,
            CasterStages = casters.Mask,
            Virtualized = virtualized,
            Clusters = new Clustered(Fallback),
            Meshes = new Quad()
        };
    }

    /// <summary>
    ///     The drawn object leaves the caster stages and a second object takes its place in them.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both halves, because either alone is a defect.</b> An object left in the caster stages
    ///     is the frame this fixes — a stage full of instances no feature draws. A caster added without
    ///     taking it out would be right today and would double every silhouette the day the traversal
    ///     learns to draw per view.
    /// </remarks>
    [Fact]
    public void TheDrawnObjectLeavesTheCasterStagesAndACasterTakesItsPlace() {
        using var world = new World();

        var entity = Placed(world, Matrix4x4.Identity);

        extraction.Extract(world);

        var handle = world.Read<RenderHandle>(entity);

        Assert.True(handle.HasCaster);
        Assert.Equal(1, extraction.CasterCount);
        Assert.Equal(0, extraction.CastersMissing);

        // The drawn object: the traversal's, and in the camera stage only.
        Assert.Equal(virtualized.Index, system.Objects[handle.Object].FeatureIndex);
        Assert.Equal(opaque.Mask, system.Objects[handle.Object].Stages);

        // The caster: the ordinary feature's, and in the caster stage only.
        Assert.Equal(meshes.Index, system.Objects[handle.Caster].FeatureIndex);
        Assert.Equal(casters.Mask, system.Objects[handle.Caster].Stages);

        // Two distinct objects, which is the whole arrangement: one render object carries one feature
        // index, so the two paths cannot be the same slot.
        Assert.NotEqual(handle.Object, handle.Caster);
    }

    /// <summary>The caster draws the fallback's triangles, not the whole mesh's.</summary>
    /// <remarks>
    ///     ⚠ <b>The number is the point.</b> A caster built from the source index buffer would be a
    ///     correct shadow costing what virtualization exists to avoid, and it would pass every other
    ///     assertion here. The fixture's fallback keeps two of the source's four triangles so the two
    ///     answers are different integers.
    /// </remarks>
    [Fact]
    public void TheCasterDrawsTheFallbackRatherThanTheWholeMesh() {
        using var world = new World();

        var entity = Placed(world, Matrix4x4.Identity);

        extraction.Extract(world);

        var handle = world.Read<RenderHandle>(entity);
        var draw = system.Objects.Data.Data(meshes.Draws)[handle.Caster.Index];

        Assert.Equal(Fallback.Length, draw.Count);
        Assert.NotEqual(SourceTriangles * 3, draw.Count);
        Assert.Equal(1, draw.InstanceCount);

        // And it is a real slice of the shared buffer rather than a zeroed record, which is what a
        // draw of the right length pointing nowhere would be.
        Assert.True(draw.IndexBuffer.IsValid);
        Assert.True(draw.VertexBuffer.IsValid);
    }

    /// <summary>The caster is placed from the entity's matrix, every frame, with the object it stands in for.</summary>
    /// <remarks>
    ///     ⚠ <b>A shadow left behind is the failure this prevents, and it would read as a shadow bug.</b>
    ///     The caster is a separate render object with a separate world matrix and separate bounds, so
    ///     an extraction that placed only the drawn one would leave a character's shadow at the door it
    ///     walked in through.
    /// </remarks>
    [Fact]
    public void TheCasterFollowsTheEntityItStandsInFor() {
        using var world = new World();

        var entity = Placed(world, Matrix4x4.Identity);

        extraction.Extract(world);

        var handle = world.Read<RenderHandle>(entity);

        world.Get<WorldTransform>(entity).Value = Matrix4x4.FromTranslation(new(12f, 0f, 0f));
        extraction.Extract(world);

        var matrices = system.Objects.Data.Data(transforms.World);

        Assert.Equal(12f, matrices[handle.Caster.Index].M41, 4);
        Assert.Equal(12f, system.Objects[handle.Caster].Bounds.Center.X, 4);

        // The same bound as the object it stands in for, exactly: a caster culled on different numbers
        // is a shadow that pops at a frustum edge the mesh is still inside.
        Assert.Equal(system.Objects[handle.Object].Bounds.Center, system.Objects[handle.Caster].Bounds.Center);
        Assert.Equal(system.Objects[handle.Object].Bounds.Radius, system.Objects[handle.Caster].Bounds.Radius, 4);
    }

    /// <summary>An entity that casts no shadow gets no caster.</summary>
    /// <remarks>
    ///     <c>MeshRenderable.CastsShadows</c> is what takes an object out of the caster stages, so an
    ///     entity with the flag clear has no caster stages left to want one for — and a caster built
    ///     anyway would be an object drawn into a shadow map the author asked it to stay out of.
    /// </remarks>
    [Fact]
    public void AnEntityThatCastsNoShadowGetsNoCaster() {
        using var world = new World();

        var entity = world.Create();

        world.Add(entity, new WorldTransform { Value = Matrix4x4.Identity });
        world.Add(entity, new MeshRenderable { Mesh = Rock, CastsShadows = false });

        extraction.Extract(world);

        var handle = world.Read<RenderHandle>(entity);

        Assert.False(handle.HasCaster);
        Assert.Equal(0, extraction.CasterCount);
        Assert.Equal(RenderObjectId.Invalid, handle.Caster);

        // And nothing was uploaded for it: a caster nobody wanted still costs a slice.
        Assert.Equal(0, residency.Count);
    }

    /// <summary>A host that never named its caster stages gets the frame it already had.</summary>
    /// <remarks>
    ///     ⚠ <b>The default of <see cref="MeshExtractionSystem.CasterStages" /> is none, and it means
    ///     "ignore the flag" rather than "cast no shadows".</b> So this asks for no caster and, crucially,
    ///     leaves the drawn object's mask alone — a host whose one stage is both camera and shadow would
    ///     otherwise lose its geometry to an <c>Except</c> that took everything.
    /// </remarks>
    [Fact]
    public void WithNoCasterStagesNamedNothingIsTakenAway() {
        using var world = new World();

        extraction.CasterStages = RenderStageMask.None;

        var entity = Placed(world, Matrix4x4.Identity);

        extraction.Extract(world);

        var handle = world.Read<RenderHandle>(entity);

        Assert.False(handle.HasCaster);
        Assert.Equal(opaque.Mask | casters.Mask, system.Objects[handle.Object].Stages);
    }

    /// <summary>An entity waits rather than settling without the caster it is going to want.</summary>
    /// <remarks>
    ///     ⚠ <b>The assertion the three-valued answer exists for, one level down.</b> A settled entity is
    ///     never re-extracted, so a virtualized entity that took its <c>RenderHandle</c> while its caster
    ///     geometry was still in flight would draw correctly and cast nothing for the rest of the level —
    ///     which is the exact failure this whole path closes, reintroduced by a race.
    /// </remarks>
    [Fact]
    public void AnEntityWaitsForTheGeometryItsCasterNeeds() {
        using var world = new World();

        var deferred = new DeferredQuad();

        extraction.Meshes = deferred;

        var entity = Placed(world, Matrix4x4.Identity);

        extraction.Extract(world);

        Assert.Equal(1, extraction.Waiting);
        Assert.Equal(0, extraction.VirtualizedCount);
        Assert.False(world.Has<RenderHandle>(entity));

        deferred.Deliver();
        extraction.Extract(world);

        Assert.Equal(1, extraction.VirtualizedCount);
        Assert.True(world.Read<RenderHandle>(entity).HasCaster);
    }

    /// <summary>Content with no fallback is counted, not papered over with the whole mesh.</summary>
    /// <remarks>
    ///     A mesh built by a version that wrote no <c>MeshletMesh.Fallback</c> has nothing to cast
    ///     through, and drawing the full-resolution index buffer instead would be a shadow whose cost is
    ///     the thing virtualization exists to avoid, arrived at silently. So it draws, casts nothing, and
    ///     says so in a number.
    /// </remarks>
    [Fact]
    public void ContentWithNoFallbackIsCountedRatherThanDrawnWhole() {
        using var world = new World();

        extraction.Clusters = new Clustered([]);

        var entity = Placed(world, Matrix4x4.Identity);

        extraction.Extract(world);

        Assert.Equal(1, extraction.VirtualizedCount);
        Assert.False(world.Read<RenderHandle>(entity).HasCaster);
        Assert.Equal(0, extraction.CasterCount);
        Assert.Equal(1, extraction.CastersMissing);
        Assert.Equal(0, residency.Count);
    }

    /// <summary>Retiring an entity gives back its caster's object and its claim.</summary>
    /// <remarks>
    ///     ⚠ <b>Three things go with a caster, not one.</b> The render object, the morph attachment and
    ///     the residency claim — and a retirement that dropped the object alone leaks a slice per
    ///     virtualized entity per level, which is the leak <see cref="MeshExtractionSystem.Forget" />
    ///     already exists for in the other half.
    /// </remarks>
    [Fact]
    public void RetiringAnEntityGivesBackItsCaster() {
        using var world = new World();

        var entity = Placed(world, Matrix4x4.Identity);

        extraction.Extract(world);

        var handle = world.Read<RenderHandle>(entity);

        Assert.Equal(1, residency.Count);
        Assert.Equal(1, residency.ClaimsOn(handle.CasterGeometry));

        world.Remove<MeshRenderable>(entity);
        extraction.Extract(world);

        Assert.Equal(0, extraction.CasterCount);
        Assert.Equal(0, residency.ClaimsOn(handle.CasterGeometry));
        Assert.False(system.Objects[handle.Caster].IsAlive);
    }

    /// <summary>Unsettling an entity gives its caster back too, and the next pass builds a fresh one.</summary>
    /// <remarks>
    ///     <c>Resettle</c> is what a toggled <c>CastsShadows</c> or a swapped frame document takes effect
    ///     through, and it is the path on which a caster is likeliest to be forgotten: the handle it
    ///     hangs off is removed by name and the second object is not the one the loop is holding.
    /// </remarks>
    [Fact]
    public void UnsettlingAnEntityGivesItsCasterBack() {
        using var world = new World();

        var entity = Placed(world, Matrix4x4.Identity);

        extraction.Extract(world);

        var first = world.Read<RenderHandle>(entity).Caster;

        extraction.Resettle(world);

        Assert.Equal(0, extraction.CasterCount);
        Assert.False(system.Objects[first].IsAlive);

        extraction.Extract(world);

        Assert.Equal(1, extraction.CasterCount);
        Assert.True(world.Read<RenderHandle>(entity).HasCaster);

        // One claim, not two: a resettle that released nothing would leave the count climbing every
        // time the editor swapped a frame document.
        Assert.Equal(1, residency.ClaimsOn(GeometryKey.Caster(Rock)));
    }

    /// <summary>Two entities sharing one mesh share one caster slice.</summary>
    /// <remarks>
    ///     The sharing the residency cache exists for, asserted on the caster's key because it is a new
    ///     key space: a caster keyed by entity rather than by asset would upload a crowd's fallback once
    ///     per character.
    /// </remarks>
    [Fact]
    public void TwoEntitiesSharingAMeshShareOneCasterSlice() {
        using var world = new World();

        Placed(world, Matrix4x4.Identity);
        Placed(world, Matrix4x4.FromTranslation(new(5f, 0f, 0f)));

        extraction.Extract(world);

        Assert.Equal(2, extraction.CasterCount);
        Assert.Equal(1, residency.Count);
        Assert.Equal(2, residency.ClaimsOn(GeometryKey.Caster(Rock)));
    }

    /// <summary>The caster's key is not the mesh's own, so the two can be resident at once.</summary>
    /// <remarks>
    ///     ⚠ <b>One asset, two index buffers.</b> The caster is the source vertices with the fallback's
    ///     triangles and the ordinary path is the same vertices with all of them, so a single key would
    ///     let whichever was acquired first decide which triangles the other one drew.
    /// </remarks>
    [Fact]
    public void TheCasterKeyIsNotTheMeshKey() {
        Assert.NotEqual(GeometryKey.Of(Rock), GeometryKey.Caster(Rock));
        Assert.True(GeometryKey.Caster(Rock).IsCaster);
        Assert.False(GeometryKey.Of(Rock).IsCaster);

        // ⚠ And a zeroed handle names no caster. `RenderObjectId`'s default is index zero, which is a
        // real object — the first the store hands out — so a bare id in this field would make every
        // unextracted entity claim the scene's first render object as its shadow.
        Assert.False(default(RenderHandle).HasCaster);
        Assert.Equal(RenderObjectId.Invalid, default(RenderHandle).Caster);
    }

    static Entity Placed(World world, Matrix4x4 matrix) {
        var entity = world.Create();

        world.Add(entity, new WorldTransform { Value = matrix });
        world.Add(entity, MeshRenderables.Default(Rock));

        return entity;
    }

    /// <inheritdoc />
    public void Dispose() {
        extraction.Clear();
        system.Dispose();
        buffer.Dispose();
        device.Dispose();
    }

    /// <summary>A cluster source that is always ready and carries the fallback it is given.</summary>
    sealed class Clustered(int[] fallback) : IVirtualGeometrySource {
        public ClusterState TryGet(AssetReference reference, out int index, out BoundingSphere bounds) {
            index = 0;
            bounds = new(Vector3.Zero, 1f);

            return ClusterState.Ready;
        }

        public bool TryGetCaster(AssetReference reference, out int[] triangles) {
            triangles = fallback;

            return fallback.Length >= 3;
        }
    }

    /// <summary>The source mesh the fallback's indices point into: four triangles over four vertices.</summary>
    static MeshData Source() =>
        new() {
            Name = "quad",
            Positions = [new(-1f, -1f, 0f), new(1f, -1f, 0f), new(-1f, 1f, 0f), new(1f, 1f, 0f)],
            Normals = [new(0f, 0f, 1f), new(0f, 0f, 1f), new(0f, 0f, 1f), new(0f, 0f, 1f)],
            TexCoords = [new(0f, 0f), new(1f, 0f), new(0f, 1f), new(1f, 1f)],
            Indices = [0, 1, 2, 1, 3, 2, 0, 2, 1, 1, 2, 3]
        };

    /// <summary>A mesh source that answers every reference at once.</summary>
    sealed class Quad : IMeshSource {
        public bool TryGet(AssetReference reference, out MeshData mesh) {
            mesh = Source();

            return true;
        }
    }

    /// <summary>The same, withheld until it is told to answer.</summary>
    sealed class DeferredQuad : IMeshSource {
        bool ready;

        public void Deliver() => ready = true;

        public bool TryGet(AssetReference reference, out MeshData mesh) {
            mesh = Source();

            return ready;
        }
    }
}

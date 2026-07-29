// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;
using Vixen.Rendering.Features;

namespace Vixen.Rendering.Ecs;

/// <summary>Turns the scene's drawable entities into render objects.</summary>
/// <remarks>
///     <para>
///         <b>The counterpart to <see cref="LightExtractionSystem" />, and the opposite trade.</b> A
///         light is rebuilt every frame because it has no handle worth keeping; a mesh holds a render
///         object, a residency claim and a slot in every feature's parallel array, all of which are
///         expensive to create and cheap to keep. So this reconciles: create for what appeared, destroy
///         for what went, and update the transform of what stayed.
///     </para>
///     <para>
///         <b>In <see cref="SystemPhase.PreRender" />, ordered by its declared access.</b>
///         <c>TransformSystem</c> writes <see cref="WorldTransform" /> in the same phase and this reads
///         it, so the graph puts this second — an object culled against last frame's bounds pops in and
///         out at the edge of the frustum.
///     </para>
///     <para>
///         ⚠ <b>One material for everything, and that is the piece still owed.</b>
///         <see cref="MeshRenderable.Material" /> is authored, compiled and loaded, and turning one into
///         a <see cref="Material" /> needs a material asset format resolved to an effect — which does not
///         exist yet. Until it does, every object is assigned <see cref="Material" />, which is what a
///         block-out wants anyway: geometry that draws in something neutral before anybody has made a
///         material for it. What this is *not* is doc 06's "a mesh with three materials is three render
///         objects"; that follows the same day per-entity materials do.
///     </para>
///     <para>
///         ⚠ <b>Every live object's transform is rewritten every frame.</b> Doc 06 wants only what moved
///         re-extracted, and the change-version filter that would do it is owed — as is the assertion
///         that a settled frame extracts nothing. What is here is correct and pays a matrix store per
///         drawable per frame, which is the wrong cost and not a wrong picture.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.PreRender)]
public sealed class MeshExtractionSystem : SystemBase, IDeclaredAccess {
    readonly RenderSystem system;
    readonly MeshRenderFeature meshes;
    readonly TransformRenderFeature transforms;
    readonly MaterialRenderFeature materials;
    readonly GeometryResidency residency;

    readonly QueryDescription appearedMeshes = new QueryDescription()
        .WithAll<MeshRenderable, WorldTransform>()
        .WithNone<RenderHandle>();

    // ⚠ `WithNone<MeshRenderable>` is what makes "the mesh wins" an archetype fact rather than a branch
    // that runs twice and extracts an entity as both — the rule `AudioSystem` applies to an event beside a
    // clip. An entity carrying both therefore draws nothing at all while mesh loading is unwired, which is
    // deliberate: a shape used as a fallback would be a level that looks different depending on what is on
    // disk.
    readonly QueryDescription appearedShapes = new QueryDescription()
        .WithAll<PrimitiveShape, WorldTransform>()
        .WithNone<RenderHandle, MeshRenderable>();

    readonly QueryDescription live = new QueryDescription().WithAll<RenderHandle, WorldTransform>();

    // Extracted, and no longer drawable: the component was removed while the handle stayed. A destroyed
    // entity does not appear here at all, which is why `Forget` exists.
    readonly QueryDescription orphaned = new QueryDescription()
        .WithAll<RenderHandle>()
        .WithNone<MeshRenderable, PrimitiveShape>();

    readonly List<Entity> pending = [];
    readonly Dictionary<Entity, GeometryKey> claimed = [];

    /// <summary>Builds the bridge.</summary>
    /// <param name="system">The render system whose store the objects go in.</param>
    /// <param name="meshes">The feature that draws them.</param>
    /// <param name="transforms">The feature holding their world matrices.</param>
    /// <param name="materials">The feature holding their material indices.</param>
    /// <param name="residency">The shared geometry the draws are suballocated from.</param>
    public MeshExtractionSystem(
        RenderSystem system,
        MeshRenderFeature meshes,
        TransformRenderFeature transforms,
        MaterialRenderFeature materials,
        GeometryResidency residency
    ) {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(transforms);
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(residency);

        this.system = system;
        this.meshes = meshes;
        this.transforms = transforms;
        this.materials = materials;
        this.residency = residency;
    }

    /// <summary>Which stages an extracted object appears in.</summary>
    /// <remarks>
    ///     Set by the host from the stages it created, because a stage's index is assigned by the render
    ///     system and a bridge cannot know which one is "the opaque one". Zero draws nothing, which is a
    ///     host that has not finished wiring rather than a state worth supporting.
    /// </remarks>
    public RenderStageMask Stages { get; set; }

    /// <summary>What every extracted object is drawn with.</summary>
    /// <inheritdoc cref="MeshExtractionSystem" path="/remarks/para[3]" />
    public Material? Material { get; set; }

    /// <summary>How many entities are extracted.</summary>
    public int ObjectCount => claimed.Count;

    /// <summary>How many entities wanted geometry that did not fit in the buffer.</summary>
    /// <remarks>
    ///     Counted rather than thrown for, and worth looking at: a level that silently stops drawing past
    ///     a certain number of meshes is the symptom, and this is the number that says so.
    /// </remarks>
    public int Dropped { get; private set; }

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<WorldTransform>()
        .Read<MeshRenderable>()
        .Read<PrimitiveShape>()
        .Write<RenderHandle>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Extract(context.World);
        return dependency;
    }

    /// <summary>Reconciles the render store with the world.</summary>
    /// <param name="world">The world.</param>
    /// <remarks>Public so a test, a tool or an editor can draw a scene without standing up a runner.</remarks>
    public void Extract(World world) {
        ArgumentNullException.ThrowIfNull(world);

        Retire(world);
        Appear(world);
        Place(world);
    }

    /// <summary>Drops an entity's object and claim without needing the entity to be alive.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether it had one.</returns>
    /// <remarks>
    ///     ⚠ <b>A destroyed entity cannot be found by a query, so somebody has to say.</b> Its
    ///     <see cref="RenderHandle" /> went with it, and the render object and the residency claim it held
    ///     did not — so a scene unload with nothing calling this leaks a slice per entity. This is what
    ///     <c>PhysicsScene</c> avoids by destroying bodies from a removal hook; the ECS event flag doc 04
    ///     describes is what would let this be automatic, and it is behind a compile-time switch.
    /// </remarks>
    public bool Forget(Entity entity) {
        if (!claimed.Remove(entity, out var key)) {
            return false;
        }

        residency.Release(key);

        return true;
    }

    /// <summary>Drops everything, for a world being torn down.</summary>
    public void Clear() {
        foreach (var key in claimed.Values) {
            residency.Release(key);
        }

        claimed.Clear();
    }

    void Retire(World world) {
        pending.Clear();

        foreach (var chunk in world.Chunks(orphaned)) {
            pending.AddRange(chunk.Entities[..chunk.Count]);
        }

        foreach (var entity in pending) {
            var handle = world.Read<RenderHandle>(entity);

            system.Objects.Remove(handle.Object);
            residency.Release(handle.Geometry);
            claimed.Remove(entity);

            world.Remove<RenderHandle>(entity);
        }
    }

    void Appear(World world) {
        pending.Clear();

        foreach (var chunk in world.Chunks(appearedMeshes)) {
            pending.AddRange(chunk.Entities[..chunk.Count]);
        }

        foreach (var entity in pending) {
            var renderable = world.Read<MeshRenderable>(entity);
            Add(world, entity, GeometryKey.Of(renderable.Mesh), () => MeshOf(renderable.Mesh));
        }

        pending.Clear();

        foreach (var chunk in world.Chunks(appearedShapes)) {
            pending.AddRange(chunk.Entities[..chunk.Count]);
        }

        foreach (var entity in pending) {
            var kind = world.Read<PrimitiveShape>(entity).Kind;
            Add(world, entity, GeometryKey.Of(kind), () => MeshPrimitives.Create(kind));
        }
    }

    void Add(World world, Entity entity, GeometryKey key, Func<MeshData> build) {
        if (!residency.Acquire(key, build, out var slice, out var local)) {
            Dropped++;
            return;
        }

        var draw = new MeshDraw { InstanceCount = 1 };
        residency.Buffer.Apply(ref draw, slice);

        var id = system.Objects.Add(
            new() {
                Bounds = Transformed(local, world.Read<WorldTransform>(entity).Value),
                Stages = Stages,
                FeatureIndex = meshes.Index
            }
        );

        system.Objects.Data.Data(meshes.Draws)[id.Index] = draw;

        if (Material is { } material) {
            materials.Assign(system, id, material);
        }

        world.Add(entity, new RenderHandle { Object = id, Geometry = key, Local = local });
        claimed[entity] = key;
    }

    void Place(World world) {
        var world_ = system.Objects.Data.Data(transforms.World);

        foreach (var chunk in world.Chunks(live)) {
            var handles = chunk.ReadValues<RenderHandle>();
            var placements = chunk.ReadValues<WorldTransform>();

            for (var i = 0; i < chunk.Count; i++) {
                var matrix = placements[i].Value;
                var id = handles[i].Object;

                world_[id.Index] = matrix;
                system.Objects[id].Bounds = Transformed(handles[i].Local, matrix);
            }
        }
    }

    /// <summary>A mesh-space bounding sphere in world space.</summary>
    /// <param name="local">The sphere the mesh has in its own space.</param>
    /// <param name="matrix">Its local-to-world transform.</param>
    /// <returns>The sphere the culling loop tests.</returns>
    /// <remarks>
    ///     ⚠ <b>The radius takes the largest of the three axis scales, not their average.</b> A sphere is
    ///     the only bound the culling loop reads, so a non-uniform scale has to be over-estimated in every
    ///     direction — under-estimating one axis culls an object that is still on screen, and a
    ///     disappearing wall is worse than a wall drawn one frame too long.
    /// </remarks>
    public static BoundingSphere Transformed(in BoundingSphere local, in Matrix4x4 matrix) {
        var scale = MathF.Sqrt(
            MathF.Max(
                Row(matrix.M11, matrix.M12, matrix.M13),
                MathF.Max(Row(matrix.M21, matrix.M22, matrix.M23), Row(matrix.M31, matrix.M32, matrix.M33))
            )
        );

        return new(Matrix4x4.TransformPosition(local.Center, matrix), local.Radius * scale);

        static float Row(float x, float y, float z) => (x * x) + (y * y) + (z * z);
    }

    /// <summary>The geometry a mesh reference names.</summary>
    /// <param name="reference">The reference.</param>
    /// <returns>The mesh.</returns>
    /// <remarks>
    ///     ⚠ <b>An empty mesh, because loading is not wired in yet.</b> Every piece needed to do it
    ///     properly exists — <c>ContentCatalog.TryGetAddress</c> resolves the reference and
    ///     <c>AssetManager.LoadAsync&lt;MeshData&gt;</c> loads it — and what is missing is the decision
    ///     about what an extraction system does while an asynchronous load is in flight. A synchronous
    ///     load here would stall the frame that first sees a mesh, which is the frame a level starts.
    ///     Until that is answered, an entity with a mesh reference is counted in
    ///     <see cref="Dropped" /> rather than drawn as something wrong.
    /// </remarks>
    static MeshData MeshOf(AssetReference reference) => new() { Name = reference.ToString() };
}

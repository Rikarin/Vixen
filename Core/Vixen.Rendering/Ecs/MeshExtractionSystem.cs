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
///         <b>Each drawable takes the material it names, and <see cref="Material" /> is what a
///         drawable that names none gets.</b> That fallback is not a stopgap: a block-out mesh dropped
///         into a level before anybody has made a material for it has to draw in something neutral, and
///         <see cref="MeshRenderable.Material" /> being null is how an author says so. A reference that
///         names a material this frame cannot supply yet is waited for exactly as a mesh is — see
///         <see cref="Materials" />.
///     </para>
///     <para>
///         ⚠ <b>What this is still not is doc 06's "a mesh with three materials is three render
///         objects".</b> One entity is one render object with one material; a model whose sub-meshes
///         want different materials needs one entity each, which is what a prefab already produces.
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
    readonly List<ResolveMaterial> resolved = [];

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

    /// <summary>What a drawable that names no material of its own is drawn with.</summary>
    /// <inheritdoc cref="MeshExtractionSystem" path="/remarks/para[3]" />
    public Material? Material { get; set; }

    /// <summary>Where the material a reference names comes from. Null draws everything in
    /// <see cref="Material" />.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Until this was settable, every object in the scene was drawn with one material</b> —
    ///         <see cref="MeshRenderable.Material" /> was authored, compiled, loaded and never resolved,
    ///         and this system said so in its own remarks. What was missing was never the compiling;
    ///         <see cref="Materials.MaterialCompiler" /> has always been a pure function. It was the
    ///         decision about what an extraction does while a material's textures are still in flight,
    ///         which <see cref="IMaterialSource" /> answers the way <see cref="IMeshSource" /> does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A null source is not the same as a null reference.</b> With no source, an entity
    ///         naming a material still draws — in <see cref="Material" /> — because a host that has not
    ///         mounted content should show geometry rather than nothing. A reference that a source
    ///         cannot supply <em>yet</em> is a different thing and is waited for.
    ///     </para>
    /// </remarks>
    public IMaterialSource? Materials { get; set; }

    /// <summary>The feature virtualized meshes are drawn through, or null to draw none that way.</summary>
    /// <remarks>
    ///     Set with <see cref="Clusters" /> or neither. With both, a mesh built with a cluster hierarchy
    ///     takes the virtualized path and everything else takes the ordinary one; with either missing,
    ///     every mesh takes the ordinary one and a virtualized model draws through its fallback mesh —
    ///     which is a correct picture and the reason this needs saying: nothing looks wrong.
    /// </remarks>
    public Features.VirtualGeometryRenderFeature? Virtualized { get; set; }

    /// <summary>Where the cluster hierarchy a mesh reference names comes from.</summary>
    /// <inheritdoc cref="Virtualized" path="/remarks" />
    public IVirtualGeometrySource? Clusters { get; set; }

    /// <summary>How many entities are extracted.</summary>
    public int ObjectCount => claimed.Count;

    /// <summary>How many of them took the virtualized path.</summary>
    /// <remarks>
    ///     The number that says the route is live. A scene of virtualized models with this at zero is a
    ///     host that did not set <see cref="Clusters" />, and it looks exactly like a scene that is
    ///     drawing correctly — because it is, through every fallback mesh in it.
    /// </remarks>
    public int VirtualizedCount { get; private set; }

    /// <summary>The materials the resolve pass has to dispatch for, in material-index order.</summary>
    /// <remarks>
    ///     What a host hands to <c>GpuClusterResolve.Materials</c>. Built here because it is the scene
    ///     that knows which materials its virtualized objects wear, and read rather than pushed because
    ///     a resolve is set up once per frame by whoever owns the pass.
    /// </remarks>
    public IReadOnlyList<ResolveMaterial> ResolveMaterials => resolved;

    /// <summary>Where the geometry a mesh reference names comes from. Null draws no referenced mesh.</summary>
    /// <remarks>
    ///     <b>Until this was settable, every mesh reference resolved to an empty mesh</b> — the
    ///     component was authored, saved, compiled, loaded and invisible, and the placeholder said so in
    ///     its own remarks. What was missing was never the loading; it was the decision about what an
    ///     extraction system does while a load is in flight, which <see cref="IMeshSource" /> answers by
    ///     letting it ask instead of wait.
    /// </remarks>
    public IMeshSource? Meshes { get; set; }

    /// <summary>How many entities are waiting for geometry that has not loaded yet.</summary>
    /// <remarks>
    ///     Counted per frame rather than accumulated, and expected to fall to zero a few frames after a
    ///     level starts. A number that stays up is a reference nothing can resolve — a misspelled
    ///     address, a mesh left out of the build — which otherwise looks exactly like a mesh that is
    ///     simply not there.
    /// </remarks>
    public int Waiting { get; private set; }

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
        Waiting = 0;
        pending.Clear();

        foreach (var chunk in world.Chunks(appearedMeshes)) {
            pending.AddRange(chunk.Entities[..chunk.Count]);
        }

        foreach (var entity in pending) {
            var renderable = world.Read<MeshRenderable>(entity);

            // The clusters first, and that order is load-bearing: a virtualized model also has a
            // fallback mesh, which resolves through the ordinary source and draws a correct picture of
            // the same object — so asking that one first would route every virtualized model through
            // the vertex buffer and nothing would ever look wrong enough to notice.
            if (Clustered(world, entity, renderable) is { } clustered) {
                if (!clustered) {
                    Waiting++;
                }

                continue;
            }

            // Asked rather than waited for. A mesh that has not arrived leaves the entity without a
            // RenderHandle, so it matches `appearedMeshes` again next frame and is asked about again —
            // which is the whole of the asynchronous story, and is why it needs no queue of its own.
            if (Meshes is null || !Meshes.TryGet(renderable.Mesh, out var mesh)) {
                Waiting++;
                continue;
            }

            // The material on the same terms, and after the mesh rather than before: a drawable whose
            // geometry has not arrived is going to be asked about again anyway, so asking for its
            // material first would start a load for something that may never be drawn.
            if (!Painted(renderable.Material, out var material)) {
                Waiting++;
                continue;
            }

            Add(world, entity, GeometryKey.Of(renderable.Mesh), () => mesh, material);
        }

        pending.Clear();

        foreach (var chunk in world.Chunks(appearedShapes)) {
            pending.AddRange(chunk.Entities[..chunk.Count]);
        }

        foreach (var entity in pending) {
            var shape = world.Read<PrimitiveShape>(entity);
            var kind = shape.Kind;

            if (!Painted(shape.Material, out var material)) {
                Waiting++;
                continue;
            }

            Add(world, entity, GeometryKey.Of(kind), () => MeshPrimitives.Create(kind), material);
        }
    }

    /// <summary>Takes an entity down the virtualized path, if that is where it belongs.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="renderable">What it draws.</param>
    /// <returns>
    ///     Null when this mesh has no cluster hierarchy and the caller should carry on down the ordinary
    ///     path; true when it was extracted here; false when it belongs here and is not ready.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>A three-valued answer, and the alternative was worse.</b> The caller has to distinguish
    ///     "not mine" from "mine and not yet", and folding the second into the first draws a virtualized
    ///     model through its fallback mesh permanently — a correct picture of the same object, arrived at
    ///     by the wrong path, which is the kind of wrong nothing reports.
    /// </remarks>
    bool? Clustered(World world, Entity entity, in MeshRenderable renderable) {
        if (Clusters is null || Virtualized is null) {
            return null;
        }

        switch (Clusters.TryGet(renderable.Mesh, out var cluster, out var local)) {
            case ClusterState.None:
                return null;

            case ClusterState.Waiting:
                return false;
        }

        if (!Painted(renderable.Material, out var material)) {
            return false;
        }

        var matrix = world.Read<WorldTransform>(entity).Value;
        var bounds = Transformed(local, matrix);

        var id = system.Objects.Add(
            new() {
                Bounds = bounds,
                Stages = Stages,
                FeatureIndex = Virtualized.Index
            }
        );

        // A position and a scale rather than a matrix, because that is all the traversal reads: every
        // test it makes wants a world-space sphere and the factor an object-space length scales by, and
        // a rotation enters into neither.
        system.Objects.Data.Data(Virtualized.Draws)[id.Index] = new() {
            Mesh = cluster,
            Position = bounds.Center,
            Scale = local.Radius > MathUtil.ZeroTolerance ? bounds.Radius / local.Radius : 1f
        };

        if (material is not null) {
            materials.Assign(system, id, material);
            Resolved(material);
        }

        // A default key rather than the mesh's: a virtualized object holds no residency claim, because
        // its geometry is paged rather than suballocated. Releasing one nothing acquired is harmless by
        // design — see `GeometryResidency.Release`.
        world.Add(entity, new RenderHandle { Object = id, Local = local });
        claimed[entity] = default;
        VirtualizedCount++;

        return true;
    }

    /// <summary>Records a material the resolve pass will have to dispatch for.</summary>
    /// <remarks>
    ///     ⚠ <b>Index zero, for every virtualized object in the scene, and that is the honest state of
    ///     the format rather than a shortcut here.</b> A cluster carries the material index its meshlet
    ///     was built with, and nothing maps that index back to an asset reference — the page format does
    ///     not carry one — so a scene's virtualized geometry resolves with the first material assigned to
    ///     any of it. A model with one material, which is nearly all of them, is drawn correctly; a model
    ///     with three draws with one of the three.
    /// </remarks>
    void Resolved(Material material) {
        if (resolved.Count == 0) {
            resolved.Add(new(material, 0));
        }
    }

    /// <summary>What a drawable is painted with, or false while its material is still coming.</summary>
    /// <param name="reference">What it named, or <see cref="AssetReference.Null" /> for none.</param>
    /// <param name="material">The material, which may be null when nothing supplied one.</param>
    /// <returns>Whether the answer is final.</returns>
    /// <remarks>
    ///     ⚠ <b>Three outcomes and only two return values, which is the part to read carefully.</b>
    ///     "It named none" and "it named one and there is no source" both come back true with the
    ///     fallback, because both are states a frame should draw. Only "it named one and the source has
    ///     not got it yet" is false — the one case where drawing now would draw the wrong thing and
    ///     keep drawing it, since a settled entity is never re-extracted.
    /// </remarks>
    bool Painted(AssetReference reference, out Material? material) {
        material = Material;

        if (reference.IsNull || Materials is null) {
            return true;
        }

        if (!Materials.TryGet(reference, out var found)) {
            return false;
        }

        material = found;
        return true;
    }

    void Add(World world, Entity entity, GeometryKey key, Func<MeshData> build, Material? material) {
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

        if (material is not null) {
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


}

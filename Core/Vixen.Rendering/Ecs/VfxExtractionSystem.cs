// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;
using Vixen.Rendering.Features;
using Vixen.Vfx;

namespace Vixen.Rendering.Ecs;

/// <summary>Turns the scene's <see cref="VfxEmitter" />s into running, drawn particle systems.</summary>
/// <remarks>
///     <para>
///         <b><see cref="MeshExtractionSystem" />'s shape, and one more job.</b> It reconciles the same
///         way — create for what appeared, destroy for what went, place what stayed — and then does
///         the thing no mesh needs: it <em>steps</em> the simulations. Nothing else in the engine
///         does. <see cref="ParticleRenderFeature" /> draws whatever it has been handed and never
///         advances it, deliberately, because a renderer that simulated would simulate once per view.
///     </para>
///     <para>
///         <b>The emitter's transform is the effect's origin.</b> Every opcode that writes a position
///         writes a world-space one, so without this a graph would be nailed to the coordinates its
///         author typed and the same <c>.vxvfx</c> on twenty entities would be twenty identical piles
///         of particles at one point. <see cref="VfxSystem.Origin" /> is read at spawn, so moving an
///         emitter moves where the next particles appear and leaves the live ones where they are —
///         which is what a torch carried across a room does to its smoke.
///     </para>
///     <para>
///         ⚠ <b>In <see cref="SystemPhase.PreRender" />, after <c>TransformSystem</c>.</b> The order
///         comes from the declared access, exactly as the mesh bridge's does: this reads
///         <see cref="WorldTransform" /> and that writes it, so an emitter extracted first would spawn
///         a frame behind the entity it is attached to.
///     </para>
///     <para>
///         ⚠ <b>One expansion serves every view, so an emitter must not be in a shadow stage.</b> That
///         is <see cref="ParticleRenderFeature" />'s constraint rather than this one's, and it is why
///         <see cref="Stages" /> is a single mask a host sets from the one transparent stage its
///         document declares rather than something derived from what a mesh is extracted into.
///     </para>
///     <para>
///         ⚠ <b>After <see cref="LightExtractionSystem" />, and that is not an aesthetic ordering.</b>
///         An effect whose output is <c>Vfx/Output/Light</c> contributes to the very list that system
///         <em>clears</em> and refills, and the two touch no component in common — so nothing but the
///         declared order keeps a shower of sparks from being wiped by the scene's own lamps every
///         frame. See <see cref="ParticleRenderFeature.CollectLights" />.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.PreRender)]
[UpdateAfter(typeof(LightExtractionSystem))]
public sealed class VfxExtractionSystem : SystemBase, IDeclaredAccess {
    readonly RenderSystem system;
    readonly ParticleRenderFeature particles;
    readonly MaterialRenderFeature materials;

    readonly QueryDescription appeared = new QueryDescription()
        .WithAll<VfxEmitter, WorldTransform>()
        .WithNone<VfxHandle>();

    readonly QueryDescription live = new QueryDescription().WithAll<VfxHandle, VfxEmitter, WorldTransform>();

    // Extracted, and no longer an emitter: the component was removed while the handle stayed. A
    // destroyed entity does not appear here at all, which is why `Forget` exists.
    readonly QueryDescription orphaned = new QueryDescription().WithAll<VfxHandle>().WithNone<VfxEmitter>();

    readonly List<Entity> pending = [];
    readonly Dictionary<Entity, VfxSystem> running = [];

    bool disposed;

    /// <summary>Builds the bridge.</summary>
    /// <param name="system">The render system whose store the objects go in.</param>
    /// <param name="particles">The feature that expands and draws them.</param>
    /// <param name="materials">
    ///     The feature holding their material indices. ⚠ <c>ParticleRenderFeature</c>'s own, not the
    ///     mesh path's — a sub-feature has exactly one owner, and a material assigned through the
    ///     other one is a material the drawing side has never heard of.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public VfxExtractionSystem(
        RenderSystem system,
        ParticleRenderFeature particles,
        MaterialRenderFeature materials
    ) {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(particles);
        ArgumentNullException.ThrowIfNull(materials);

        this.system = system;
        this.particles = particles;
        this.materials = materials;
    }

    /// <summary>Which stages an extracted emitter appears in.</summary>
    /// <remarks>
    ///     Set by the host from the stage its document declared, because a stage's index is assigned by
    ///     the render system and a bridge cannot know which one is "the transparent one". Zero draws
    ///     nothing, which is a host that has not finished wiring.
    /// </remarks>
    public RenderStageMask Stages { get; set; }

    /// <summary>Where the effect a reference names comes from. Null emits nothing.</summary>
    public IVfxEffectSource? Effects { get; set; }

    /// <summary>What every emitter is drawn with.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The project's rather than the effect's, and that is a real difference from a mesh.</b>
    ///         A <c>.vxvfx</c> says how particles <em>move</em>; it says nothing about which shader
    ///         draws them, because the node library has no material node. So the sprite material is a
    ///         decision the host makes once — <c>ParticleSprite</c> for the shipped one — and every
    ///         emitter in the scene shares it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null draws nothing at all.</b> <c>ParticleRenderFeature.Draw</c> asks which variant
    ///         each object resolves to and skips any with no answer, so an unset material is an effect
    ///         that simulates every frame, uploads its quads, and never appears.
    ///     </para>
    /// </remarks>
    public Material? Material { get; set; }

    /// <summary>How many emitters are running.</summary>
    public int Running => running.Count;

    /// <summary>How many emitters are waiting for an effect that has not arrived.</summary>
    /// <remarks>
    ///     Falls to zero a few frames after a level starts. A number that stays up is a reference
    ///     nothing can resolve, which otherwise looks exactly like an emitter somebody forgot to
    ///     assign an effect to.
    /// </remarks>
    public int Waiting { get; private set; }

    /// <summary>How many particles the running emitters hold between them.</summary>
    public int ParticleCount {
        get {
            var total = 0;

            foreach (var effect in running.Values) {
                total += effect.Count;
            }

            return total;
        }
    }

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<WorldTransform>()
        .Read<VfxEmitter>()
        .Write<VfxHandle>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Extract(context.World, context.Time.DeltaSeconds);

        return dependency;
    }

    /// <summary>Reconciles the render store with the world, and advances what is running.</summary>
    /// <param name="world">The world.</param>
    /// <param name="deltaSeconds">The step.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>Public so a test, a tool or an editor can run a scene's effects without a runner.</remarks>
    public void Extract(World world, float deltaSeconds) {
        ArgumentNullException.ThrowIfNull(world);

        Retire(world);
        Appear(world);
        Advance(world, deltaSeconds);

        // ⚠ Last, and after the step. A light is where its particle is *now*, and collecting before
        // Advance would light the scene from where the sparks were last frame — which on a fast
        // emitter is a pool of light trailing the effect that made it.
        particles.CollectLights();
    }

    /// <summary>Drops an entity's effect without needing the entity to be alive.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether it had one.</returns>
    /// <remarks>
    ///     ⚠ <b>A destroyed entity cannot be found by a query, so somebody has to say</b> — the same
    ///     gap <see cref="MeshExtractionSystem.Forget" /> exists for, and here it leaks native memory
    ///     rather than a buffer slice: a <c>ParticleBuffer</c> holds pooled arrays for its whole
    ///     capacity whether or not anything is alive in it.
    /// </remarks>
    public bool Forget(Entity entity) {
        if (!running.Remove(entity, out var effect)) {
            return false;
        }

        effect.Dispose();

        return true;
    }

    /// <summary>Drops everything, for a world being torn down.</summary>
    public void Clear() {
        foreach (var effect in running.Values) {
            effect.Dispose();
        }

        running.Clear();
    }

    void Retire(World world) {
        pending.Clear();

        foreach (var chunk in world.Chunks(orphaned)) {
            pending.AddRange(chunk.Entities[..chunk.Count]);
        }

        foreach (var entity in pending) {
            var handle = world.Read<VfxHandle>(entity);

            system.Objects.Remove(handle.Object);
            Forget(entity);

            world.Remove<VfxHandle>(entity);
        }
    }

    void Appear(World world) {
        Waiting = 0;
        pending.Clear();

        foreach (var chunk in world.Chunks(appeared)) {
            pending.AddRange(chunk.Entities[..chunk.Count]);
        }

        foreach (var entity in pending) {
            var emitter = world.Read<VfxEmitter>(entity);

            // Asked rather than waited for, on the mesh bridge's terms: an effect that has not arrived
            // leaves the entity without a VfxHandle, so it matches `appeared` again next frame and is
            // asked about again. That is the whole of the asynchronous story.
            if (Effects is null || Material is null || !Effects.TryGet(emitter.Effect, out var graph)) {
                Waiting++;

                continue;
            }

            var placement = world.Read<WorldTransform>(entity).Value;
            var reach = MathF.Max(emitter.Reach, VfxEmitters.DefaultReach);

            // ⚠ Derived from the entity when the emitter says nothing, and this is what stops fifteen
            // lamps drifting identical embers. Two systems of one graph with one seed produce the same
            // particles, which at a distance reads as a repeated texture rather than as fifteen fires.
            var effect = new VfxSystem(graph, emitter.Seed != 0 ? emitter.Seed : Seed(entity)) {
                Emitting = emitter.Playing,
                Origin = placement.Translation
            };

            var id = system.Objects.Add(
                new() {
                    Bounds = Bound(placement.Translation, emitter),
                    Stages = Stages,
                    FeatureIndex = particles.Index
                }
            );

            particles.SetSystem(id, effect);
            materials.Assign(system, id, Material);

            running[entity] = effect;
            world.Add(entity, new VfxHandle { Object = id });
        }
    }

    void Advance(World world, float deltaSeconds) {
        foreach (var chunk in world.Chunks(live)) {
            var handles = chunk.ReadValues<VfxHandle>();
            var emitters = chunk.ReadValues<VfxEmitter>();
            var placements = chunk.ReadValues<WorldTransform>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                if (!running.TryGetValue(entities[index], out var effect)) {
                    continue;
                }

                var at = placements[index].Value.Translation;

                effect.Origin = at;
                effect.Emitting = emitters[index].Playing;
                effect.Step(deltaSeconds);

                // The bound follows the emitter rather than the particles, and is deliberately not
                // fitted to them: a bound recomputed from what is alive would shrink as an effect died
                // out and pop as it restarted, and it would cost a sweep over every particle to
                // produce a number the frustum test uses once.
                system.Objects[handles[index].Object].Bounds = Bound(at, emitters[index]);
            }
        }
    }

    /// <summary>The sphere the culling loop tests an emitter against.</summary>
    static BoundingSphere Bound(in Vector3 at, in VfxEmitter emitter) {
        var reach = MathF.Max(emitter.Reach, VfxEmitters.DefaultReach);

        return new(at + new Vector3(0f, emitter.Rise, 0f), reach);
    }

    /// <summary>A seed for an emitter that named none.</summary>
    /// <remarks>
    ///     ⚠ <b>From the entity, never from a clock.</b> A level loaded from a scene hands out its
    ///     entities in file order, so this is stable across runs — which is what lets a project assert
    ///     that two runs of the same length produce the same frames. Never zero, because zero is what
    ///     <see cref="VfxEmitter.Seed" /> uses to mean "decide for me".
    /// </remarks>
    static uint Seed(Entity entity) => (uint)entity.Id + 1u;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The particle buffers are native memory the collector does not free on its own timetable
    ///     — they are pooled arrays a <c>ParticleBuffer</c> holds for its whole capacity.</b> A render
    ///     system torn down and rebuilt without this walks the pool up by a scene's worth of effects
    ///     every time, which reads as a slow leak rather than as anything anybody would attribute to
    ///     particles.
    /// </remarks>
    public override void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        Clear();
        base.Dispose();
    }
}

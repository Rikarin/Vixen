// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;

namespace Vixen.Engine.Cameras;

/// <summary>
///     Picks the live shot for every <see cref="CameraDirector" />, blends when the pick changes, and
///     moves the real camera there.
/// </summary>
/// <remarks>
///     <para>
///         <b>Ties go to the shot enabled most recently.</b> Priority decides, and when two shots
///         share a priority the one whose <see cref="VirtualCamera.Enabled" /> most recently became
///         true wins — which is what a designer means when they wire two triggers to two cameras and
///         give neither of them a number. Breaking the tie by entity id instead would be
///         deterministic and useless: the same shot would win for ever, and the second trigger would
///         appear to do nothing.
///     </para>
///     <para>
///         <b>An interrupted blend freezes rather than nesting.</b> When the pick changes during a
///         blend, the state the director had produced <i>that frame</i> becomes the outgoing side of
///         the next one — one snapshot, no stack, and no pop at the moment of the interruption.
///         Cinemachine keeps the whole chain alive and evaluates it recursively, which is smoother
///         under a rapid series of cuts and unbounded in cost; this is the trade, and the visible
///         cost of it is that a handheld shake on the outgoing shot stops moving for the length of
///         the second blend. A blend that is <i>not</i> interrupted keeps evaluating both shots
///         live, so the common case pays nothing for this.
///     </para>
///     <para>
///         It runs after <see cref="VirtualCameraSystem" /> in <see cref="SystemPhase.LateUpdate" />,
///         and it writes the camera entity's <c>LocalTransform</c> rather than its
///         <c>WorldTransform</c> — so the transform pass in <c>PreRender</c> resolves the camera and
///         anything parented to it, in the same frame, by the ordinary route. A camera rig with a
///         weapon model hanging off it therefore does not lag the camera by a frame.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.LateUpdate)]
[UpdateAfter(typeof(VirtualCameraSystem))]
public sealed class CameraDirectorSystem : SystemBase, IDeclaredAccess {
    /// <summary>What the director has to remember between frames.</summary>
    struct DirectorState {
        public Entity Live;
        public Entity From;
        public bool Blending;
        public bool Frozen;
        public CameraBlend Blend;
        public float Elapsed;
        public Vector3 FrozenPosition;
        public Quaternion FrozenRotation;
        public CameraLens FrozenLens;
        public Vector3 Position;
        public Quaternion Rotation;
        public CameraLens Lens;
    }

    /// <summary>When a shot was last switched on, and whether it was on last frame.</summary>
    struct Activation {
        public bool Enabled;
        public long Stamp;
        public long Frame;
    }

    /// <summary>The best shot on one channel so far.</summary>
    readonly record struct Candidate(Entity Entity, int Priority, long Stamp);

    readonly QueryDescription shots = new QueryDescription().WithAll<VirtualCamera, CameraShot>();

    readonly QueryDescription directors = new QueryDescription()
        .WithAll<CameraDirector, Camera, LocalTransform>();

    readonly Dictionary<Entity, DirectorState> states = [];
    readonly Dictionary<Entity, Activation> activations = [];
    readonly Dictionary<int, Candidate> best = [];
    readonly List<Entity> stale = [];

    long sequence;
    long frame;

    /// <summary>Per-pair blend rules, or <see langword="null" /> for the director's default only.</summary>
    public CameraBlendTable? Blends { get; set; }

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<VirtualCamera>()
        .Read<CameraShot>()
        .Read<CameraDirector>()
        .Read<Parent>()
        .Write<LocalTransform>()
        .Write<Camera>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Direct(context.World, context.Time);
        return dependency;
    }

    /// <summary>The shot a director is currently taking, or <see cref="Entity.Null" />.</summary>
    /// <param name="director">The director's entity.</param>
    /// <returns>The live shot.</returns>
    public Entity LiveCameraOf(Entity director) =>
        states.TryGetValue(director, out var state) ? state.Live : Entity.Null;

    /// <summary>The shot a director is blending out of, or <see cref="Entity.Null" />.</summary>
    /// <param name="director">The director's entity.</param>
    /// <returns>The outgoing shot.</returns>
    public Entity BlendingFrom(Entity director) =>
        states.TryGetValue(director, out var state) && state.Blending ? state.From : Entity.Null;

    /// <summary>How far through a blend a director is: 0 while not blending, 1 the moment it ends.</summary>
    /// <param name="director">The director's entity.</param>
    /// <returns>The eased progress.</returns>
    public float BlendProgressOf(Entity director) =>
        states.TryGetValue(director, out var state) && state.Blending
            ? state.Blend.Evaluate(state.Elapsed)
            : 0f;

    /// <summary>Forgets everything about a director, so its next frame is a cut rather than a blend.</summary>
    /// <param name="director">The director's entity.</param>
    /// <remarks>
    ///     What a scene change wants. Without it the first frame of the new level blends the camera
    ///     across the map from wherever the last one left it.
    /// </remarks>
    public void Reset(Entity director) => states.Remove(director);

    /// <summary>Chooses, blends and writes, for every director in the world.</summary>
    /// <param name="world">The world.</param>
    /// <param name="time">The clock.</param>
    /// <remarks>Public for the same reason <c>VirtualCameraSystem.Evaluate</c> is.</remarks>
    public void Direct(World world, GameTime time) {
        ArgumentNullException.ThrowIfNull(world);

        frame++;
        Nominate(world);

        foreach (var chunk in world.Chunks(directors)) {
            var settings = chunk.ReadValues<CameraDirector>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                Drive(world, entities[index], settings[index], time.DeltaSeconds);
            }
        }
    }

    /// <summary>Finds the winning shot on each channel, and stamps anything newly switched on.</summary>
    void Nominate(World world) {
        best.Clear();
        var seen = 0;

        foreach (var chunk in world.Chunks(shots)) {
            var cameras = chunk.ReadValues<VirtualCamera>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                var entity = entities[index];
                var camera = cameras[index];
                seen++;

                activations.TryGetValue(entity, out var record);

                if (camera.Enabled && !record.Enabled) {
                    record.Stamp = ++sequence;
                }

                record.Enabled = camera.Enabled;
                record.Frame = frame;
                activations[entity] = record;

                if (!camera.Enabled) {
                    continue;
                }

                var candidate = new Candidate(entity, camera.Priority, record.Stamp);

                if (!best.TryGetValue(camera.Channel, out var standing) || Beats(candidate, standing)) {
                    best[camera.Channel] = candidate;
                }
            }
        }

        // Entities are recycled, so an activation record for a shot that has gone away would
        // eventually be inherited by an unrelated one that happened to take its slot. The scan is
        // only paid for on the frame a shot is destroyed, which is the frame the count disagrees.
        if (activations.Count <= seen) {
            return;
        }

        stale.Clear();

        foreach (var (entity, record) in activations) {
            if (record.Frame != frame) {
                stale.Add(entity);
            }
        }

        foreach (var entity in stale) {
            activations.Remove(entity);
        }
    }

    /// <summary>Higher priority wins; equal priority goes to whichever was switched on last.</summary>
    static bool Beats(in Candidate candidate, in Candidate standing) =>
        candidate.Priority != standing.Priority
            ? candidate.Priority > standing.Priority
            : candidate.Stamp > standing.Stamp;

    void Drive(World world, Entity entity, CameraDirector director, float deltaTime) {
        if (!best.TryGetValue(director.Channel, out var chosen)) {
            // No shot on this channel wants the camera. Leave it exactly where it is: a director
            // that snapped its camera to the origin the moment the last shot was disabled would make
            // disabling one a thing nobody could do safely.
            return;
        }

        states.TryGetValue(entity, out var state);

        if (state.Live != chosen.Entity) {
            var blend = Blends?.Resolve(state.Live, chosen.Entity, director.DefaultBlend) ?? director.DefaultBlend;

            if (state.Blending) {
                // Interrupted: this frame's output becomes the outgoing side. See the class remarks.
                state.Frozen = true;
                state.FrozenPosition = state.Position;
                state.FrozenRotation = state.Rotation;
                state.FrozenLens = state.Lens;
            } else {
                state.Frozen = false;
                state.From = state.Live;
            }

            state.Blend = blend;
            state.Elapsed = 0f;
            state.Blending = !blend.IsCut && (state.Frozen || !state.From.IsNull);
            state.Live = chosen.Entity;
        }

        if (!Compose(world, state.Live, out var position, out var rotation, out var lens)) {
            return;
        }

        if (state.Blending) {
            state.Elapsed += deltaTime;

            var amount = state.Blend.Evaluate(state.Elapsed);
            var fromPosition = state.FrozenPosition;
            var fromRotation = state.FrozenRotation;
            var fromLens = state.FrozenLens;

            // A blend that was not interrupted keeps evaluating its outgoing shot, so a camera being
            // left behind goes on following its subject for the length of the transition.
            var hasSource = state.Frozen
                || Compose(world, state.From, out fromPosition, out fromRotation, out fromLens);

            if (hasSource) {
                CameraBlend.Mix(
                    fromPosition,
                    fromRotation,
                    position,
                    rotation,
                    amount,
                    out position,
                    out rotation
                );

                lens = CameraLens.Blend(in fromLens, in lens, amount);
            }

            if (state.Elapsed >= state.Blend.Duration) {
                state.Blending = false;
                state.Frozen = false;
                state.From = Entity.Null;
            }
        }

        state.Position = position;
        state.Rotation = rotation;
        state.Lens = lens;
        states[entity] = state;

        Place(world, entity, position, rotation);

        if (director.WriteLens && world.Has<Camera>(entity)) {
            lens.ApplyTo(ref world.Get<Camera>(entity));
        }
    }

    /// <summary>A shot's composed state, or nothing if the shot has gone away.</summary>
    static bool Compose(
        World world,
        Entity shot,
        out Vector3 position,
        out Quaternion rotation,
        out CameraLens lens
    ) {
        if (shot.IsNull || !world.IsAlive(shot) || !world.TryGet<CameraShot>(shot, out var value)) {
            position = Vector3.Zero;
            rotation = Quaternion.Identity;
            lens = CameraLens.Default;
            return false;
        }

        value.Composed(out position, out rotation);
        lens = value.Lens;
        return true;
    }

    /// <summary>Writes a world-space pose onto the camera entity, through its parent if it has one.</summary>
    /// <remarks>
    ///     The local rotation is <c>world * conjugate(parent)</c> and the order is not
    ///     interchangeable: composition here reads left to right, so the child's own rotation is
    ///     applied <i>first</i> and the parent's second — which is what <c>TransformSystem</c>'s
    ///     <c>local * parentWorld</c> says in matrices. Writing the conjugate on the left instead
    ///     produces a rotation that is right only while the two commute.
    /// </remarks>
    static void Place(World world, Entity entity, Vector3 position, Quaternion rotation) {
        var parent = Hierarchy.ParentOf(world, entity);

        if (!parent.IsNull) {
            var matrix = Hierarchy.ResolveWorldMatrix(world, parent);

            if (Matrix4x4.Invert(matrix, out var inverse)) {
                position = Matrix4x4.TransformPosition(position, inverse);

                if (Matrix4x4.Decompose(matrix, out _, out var parentRotation, out _)) {
                    rotation = rotation * Quaternion.Conjugate(parentRotation);
                }
            }
        }

        ref var local = ref world.Get<LocalTransform>(entity);
        local.Position = position;
        local.Rotation = rotation;
    }
}

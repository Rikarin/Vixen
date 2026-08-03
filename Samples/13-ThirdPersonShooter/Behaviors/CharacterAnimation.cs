// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Behaviors;
using Vixen.Engine.Transforms;
using Vixen.Physics.Characters;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>Turns the body to face where it is going, and swings its limbs while it does.</summary>
/// <remarks>
///     <para>
///         <b>Why this is a behaviour and not a system.</b> It reads one entity's components and
///         writes six others', all of them named at construction — that is a per-entity script with
///         private references, which is exactly the shape <c>docs/plan/04</c> says an <c>ISystem</c>
///         is the wrong tool for. A system would have to find these six entities from the world every
///         frame, and the only thing that knows which six they are is the code that made them.
///     </para>
///     <para>
///         <b>The pose is sampled from the <c>.vxanim</c> clips beside this file</b>, loaded by
///         address like any other content. What this behaviour still decides is <i>when</i> and
///         <i>how much</i> — the phase, driven by distance travelled, and the amplitude, scaled by
///         speed — because those are gameplay and the clip is art.
///     </para>
///     <para>
///         ⚠ <b>Sampled by target name rather than through a <c>Skeleton</c>, because this rig has
///         no skeleton.</b> It is seven boxes on seven entities, and
///         <c>AnimationClipContent.TrySample</c> exists for exactly that shape — a hand-keyed clip
///         driving something that is not a skinned character. A rig with a real skeleton would take
///         <c>AnimationClipCache.Get</c> and the baked path instead.
///     </para>
///     <para>
///         <b>The visuals turn and the capsule does not.</b> A capsule that rotated would change what
///         its sweep hits, which is a physical consequence of a decision that is entirely cosmetic.
///     </para>
/// </remarks>
public sealed class CharacterAnimation : Behavior {
    float phase;
    bool wasAirborne;
    bool leftWasDown = true;

    /// <summary>The entity every visible part hangs from.</summary>
    public Entity Visuals { get; init; }

    /// <summary>The torso, which bobs.</summary>
    public Entity Hips { get; init; }

    /// <summary>The left arm, which swings against the left leg.</summary>
    public Entity ArmLeft { get; init; }

    /// <summary>The right arm, which holds the weapon.</summary>
    public Entity ArmRight { get; init; }

    /// <summary>The left leg.</summary>
    public Entity LegLeft { get; init; }

    /// <summary>The right leg.</summary>
    public Entity LegRight { get; init; }

    /// <summary>Where the footsteps and the landing come from.</summary>
    public GameSounds Sounds { get; init; } = GameSounds.Silent;

    /// <summary>The walk cycle, loaded by address, or <see langword="null" /> in a build with no content.</summary>
    /// <remarks>
    ///     ⚠ <b>Nullable because a headless run has no catalog</b>, not because the pose is optional.
    ///     <see cref="Pose" /> leaves a part alone when there is no clip, so a content-less run draws
    ///     a character standing still rather than throwing on the first frame — the same choice
    ///     <c>GameContent.Reference</c> makes for a mesh nobody published.
    /// </remarks>
    public AnimationClipContent? Walk { get; init; }

    /// <summary>The tuck, played while off the ground.</summary>
    public AnimationClipContent? Jump { get; init; }

    /// <summary>How fast the legs swing per metre travelled, in radians.</summary>
    public float Stride { get; init; } = 1.05f;

    /// <summary>How far a leg swings at a full stride, in radians.</summary>
    public float Swing { get; init; } = 0.7f;

    /// <inheritdoc />
    protected override void Update() {
        // Everything below reads the pawn's state and writes the visuals', so both have to be there.
        // A pawn whose visuals were destroyed — the frame after a death, before the rig is rebuilt —
        // is an ordinary state and not one to throw about.
        if (!Has<CharacterState>()
            || !World.IsAlive(Visuals)
            || !World.Has<LocalTransform>(Visuals)
            || !World.Has<CharacterVisuals>(Visuals)) {
            return;
        }

        ref readonly var state = ref Read<CharacterState>();

        var planar = new Vector2(state.Velocity.X, state.Velocity.Z);
        var speed = planar.Length();
        var delta = Time.DeltaSeconds;

        Face(planar, speed, delta);
        Step(state, speed, delta);
    }

    /// <summary>Turns the visuals towards the direction of travel, and no faster than they can turn.</summary>
    void Face(Vector2 planar, float speed, float delta) {
        if (speed < 0.15f) {
            return;
        }

        // ⚠ On the visuals and not on this entity. `Get<T>()` is shorthand for "this behaviour's own
        // entity", which is the pawn — and the pawn deliberately does not carry CharacterVisuals,
        // because the thing that turns is the child the meshes hang from. Reading it from the wrong
        // entity threw ComponentNotFoundException the first time somebody moved.
        ref var facing = ref World.Get<CharacterVisuals>(Visuals);

        // ⚠ Both components negated, and the reason is the engine's forward. A yaw of zero looks down
        // −Z (`Conventions.md`, and `MoveIntent.WorldDirection` builds the same basis), so the yaw
        // that points at a velocity of (x, z) is atan2(−x, −z). `atan2(x, z)` is the same angle half a
        // turn away — a character who faces exactly away from wherever they are running, which reads
        // as a model moon-walking rather than as a sign error, and which the symmetry of a body made
        // of boxes hides everywhere except the weapon.
        var wanted = MathF.Atan2(-planar.X, -planar.Y);

        // The shortest way round. Without the wrap a character crossing from +π to −π spins the long
        // way, which reads as the model doing a pirouette every time it runs west.
        var difference = Wrap(wanted - facing.Facing);

        facing.Facing += Math.Clamp(difference, -12f * delta, 12f * delta);

        ref var transform = ref World.Get<LocalTransform>(Visuals);
        transform.Rotation = Quaternion.FromAxisAngle(Vector3.UnitY, facing.Facing);
    }

    /// <summary>Advances the walk cycle, swings the limbs, and plays what the cycle passes.</summary>
    void Step(in CharacterState state, float speed, float delta) {
        var airborne = state.Mode is not CharacterMoveMode.Walking;

        if (airborne) {
            // Tucked, and the cycle is held where it was so landing does not start mid-stride. The
            // clip is sampled at its end, which is where the tuck settles — this is a pose and not a
            // playback, because how long a character is airborne is the physics' business.
            Pose(Jump, "LegLeft", LegLeft, 1f);
            Pose(Jump, "LegRight", LegRight, 1f);
            Pose(Jump, "Torso", ArmLeft, 1f);

            if (!wasAirborne) {
                Sounds.Play(Sounds.Jump, 0.7f);
            }

            wasAirborne = true;
            return;
        }

        if (wasAirborne) {
            Sounds.Play(Sounds.Land, 0.8f);
            wasAirborne = false;
        }

        // Driven by distance rather than by time, so a character walking slowly takes slow steps
        // rather than small ones — which is the difference between animation and a treadmill.
        phase = Wrap(phase + (speed * Stride * delta));

        // ⚠ The clip supplies the *shape* of the stride and the speed supplies its *size*, which is
        // why the sampled rotation is scaled towards the identity rather than used as it stands. A
        // walk cycle authored at one amplitude played flat would give a character strolling at
        // 0.5 m/s the stride of one at 4.5, and the feet would skate — the thing driving the phase by
        // distance was there to prevent.
        var reach = MathF.Min(speed / 4.5f, 1.4f);

        // Phase runs over a turn and the clip over its own duration, so the cycle maps onto the clip
        // rather than onto seconds. A clip re-timed by its author therefore changes how the stride
        // *looks* and not how far it carries anybody.
        var normalised = ((phase / MathF.Tau) + 1f) % 1f;

        Pose(Walk, "LegLeft", LegLeft, reach, normalised);
        Pose(Walk, "LegRight", LegRight, reach, normalised);
        Pose(Walk, "ArmLeft", ArmLeft, reach, normalised);
        Pose(Walk, "ArmRight", ArmRight, reach, normalised);

        var left = MathF.Sin(phase);

        if (World.Has<LocalTransform>(Hips)) {
            ref var hips = ref World.Get<LocalTransform>(Hips);
            hips.Position = new(hips.Position.X, 0.98f + (MathF.Abs(MathF.Sin(phase)) * 0.03f), hips.Position.Z);
        }

        // A footstep on each crossing of the bottom of the swing, which is where a foot is on the
        // ground. Edge-triggered rather than threshold-tested, so standing still on the boundary does
        // not machine-gun.
        var leftIsDown = left < 0f;

        if (leftIsDown != leftWasDown && speed > 0.4f) {
            Sounds.Play(leftIsDown ? Sounds.FootstepLeft : Sounds.FootstepRight, 0.5f);
            leftWasDown = leftIsDown;
        }
    }

    /// <summary>Poses one part from one of the clip's targets.</summary>
    /// <param name="clip">The clip, or <see langword="null" /> in a build with no content.</param>
    /// <param name="target">What the clip calls the part.</param>
    /// <param name="part">The entity to pose.</param>
    /// <param name="weight">How much of the sampled rotation to apply, from the rest pose.</param>
    /// <param name="normalised">Where in the clip, in <c>[0, 1]</c>. One is the end.</param>
    /// <remarks>
    ///     ⚠ <b>Nothing happens when there is no clip, rather than a fallback pose.</b> A silent
    ///     fallback would make a content build that failed to publish these clips look like a working
    ///     one with stiff legs, which is the failure that takes an afternoon to find. A character that
    ///     does not move at all is a question somebody asks in the first minute.
    /// </remarks>
    void Pose(AnimationClipContent? clip, string target, Entity part, float weight, float normalised = 1f) {
        if (clip is null || !World.Has<LocalTransform>(part)) {
            return;
        }

        if (!clip.TrySample(target, normalised * clip.Data.Duration, out var sampled)) {
            return;
        }

        ref var transform = ref World.Get<LocalTransform>(part);

        transform.Rotation = weight >= 1f
            ? sampled.Rotation
            : Quaternion.Slerp(Quaternion.Identity, sampled.Rotation, weight);
    }

    static float Wrap(float radians) {
        while (radians > MathF.PI) {
            radians -= MathF.Tau;
        }

        while (radians < -MathF.PI) {
            radians += MathF.Tau;
        }

        return radians;
    }
}

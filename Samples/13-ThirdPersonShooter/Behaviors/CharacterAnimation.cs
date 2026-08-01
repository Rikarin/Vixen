// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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
///         ⚠ <b>The pose is computed here rather than sampled from the <c>.vxanim</c> clips, and that
///         is a gap rather than a preference.</b> A <c>.vxanim</c> is imported as the authored YAML
///         under the type name <c>AnimationClip</c>, and nothing compiles it into the
///         <c>AnimationClipData</c> that <c>AnimationClip.Create</c> bakes against a skeleton — so
///         there is no way for a game to load one by address today. The clips beside this file hold
///         the same swing this computes, at the same rate, so they become the source the moment that
///         path exists. <c>docs/overview.md</c> records it.
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
        var wanted = MathF.Atan2(planar.X, planar.Y);

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
            // Tucked, and the cycle is held where it was so landing does not start mid-stride.
            Rotate(LegLeft, -0.5f);
            Rotate(LegRight, -0.2f);
            Rotate(ArmLeft, 0.4f);

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

        var reach = Swing * MathF.Min(speed / 4.5f, 1.4f);
        var left = MathF.Sin(phase) * reach;

        Rotate(LegLeft, left);
        Rotate(LegRight, -left);
        Rotate(ArmLeft, -left * 0.6f);
        Rotate(ArmRight, left * 0.25f);

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

    void Rotate(Entity joint, float pitch) {
        if (!World.Has<LocalTransform>(joint)) {
            return;
        }

        ref var transform = ref World.Get<LocalTransform>(joint);
        transform.Rotation = Quaternion.FromAxisAngle(Vector3.UnitX, pitch);
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

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Players;

namespace Vixen.Physics.Characters;

/// <summary>Turns a character's intent into the velocity its controller is asked to move by.</summary>
/// <remarks>
///     <para>
///         <b>A pure function, and that is the requirement rather than the style.</b> Everything it
///         reads is an argument and everything it changes is the state it is handed; it touches no
///         clock, no random source and no field of its own. That is what
///         [16](../../../docs/plan/16-networking.md)'s <c>PredictedStep</c> demands — the same tick is
///         simulated twice whenever a snapshot disagrees, and a step that is not reproducible does not
///         merely predict badly, it makes the correction itself wrong. It is also why this is a static
///         class beside the bridge rather than a method on it: a rule that cannot reach a field cannot
///         accidentally depend on one.
///     </para>
///     <para>
///         <b>It knows nothing about Jolt.</b> The sweep, the stairs and the slope cancellation are
///         the <see cref="CharacterController" />'s; this decides only what the character is
///         <i>trying</i> to do. So every rule below is testable with no native library, on any runner,
///         which is what makes the determinism assertion cheap enough to run everywhere.
///     </para>
/// </remarks>
public static class CharacterMotion {
    /// <summary>Advances one character's motion by one fixed step.</summary>
    /// <param name="settings">How the character walks.</param>
    /// <param name="state">Where it is in its motion. Updated in place.</param>
    /// <param name="intent">What it is being asked to do.</param>
    /// <param name="ground">What it was standing on at the end of the last step.</param>
    /// <param name="deltaTime">How long the step is, in seconds.</param>
    /// <remarks>
    ///     The order is the design and it is not arbitrary. Ground state decides the mode; the mode
    ///     decides what gravity does; the jump is resolved before gravity so that the step a character
    ///     jumps on is a step it rises through rather than one gravity has already eaten; the
    ///     horizontal velocity is last because nothing above it depends on where the character is
    ///     going.
    /// </remarks>
    public static void Step(
        in CharacterMovement settings,
        ref CharacterState state,
        in MoveIntent intent,
        CharacterGround ground,
        float deltaTime
    ) {
        state.Ground = ground;

        Crouch(settings, ref state, intent);
        Mode(ref state);
        Jump(settings, ref state, intent, deltaTime);
        Fall(settings, ref state, deltaTime);
        Steer(settings, ref state, intent, deltaTime);
    }

    /// <summary>Whether the character is crouched.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the intent's answer and the bridge may overrule it.</b> Standing up is a shape
    ///     swap that fails under a low ceiling, and when it does the bridge puts
    ///     <see cref="CharacterState.IsCrouching" /> back — so a character trapped under a shelf keeps
    ///     crouch speed with nothing here having to know a ceiling exists. Deciding it here anyway is
    ///     what keeps the rule self-contained, and therefore testable with no world at all.
    /// </remarks>
    static void Crouch(in CharacterMovement settings, ref CharacterState state, in MoveIntent intent) =>
        state.IsCrouching = !settings.CrouchShape.IsNone && intent.IsHeld(MoveButtons.Crouch);

    static void Mode(ref CharacterState state) {
        if (state.Mode == CharacterMoveMode.Flying) {
            // Flight is entered and left by whatever granted it, never by the ground: a drone that
            // landed and silently became a walker is a drone that cannot take off again.
            return;
        }

        // The upward guard is what stops the step a character jumps on from being read as a landing.
        // The controller still reports the ground it has not left yet, and without this the jump
        // velocity would be zeroed by Fall on the very step it was given.
        state.Mode = state.IsGrounded && state.Velocity.Y <= 0f
            ? CharacterMoveMode.Walking
            : CharacterMoveMode.Falling;
    }

    static void Jump(
        in CharacterMovement settings,
        ref CharacterState state,
        in MoveIntent intent,
        float deltaTime
    ) {
        var held = intent.IsHeld(MoveButtons.Jump);

        // The press is an edge. A level would refill the buffer every step, and a player holding the
        // button would bounce the instant they landed, for ever.
        if (held && !state.JumpHeld) {
            state.JumpBufferRemaining = settings.JumpBufferTime;
        }

        state.CoyoteRemaining = state.Mode == CharacterMoveMode.Walking
            ? settings.CoyoteTime
            : MathF.Max(0f, state.CoyoteRemaining - deltaTime);

        state.JumpBufferRemaining = MathF.Max(0f, state.JumpBufferRemaining - deltaTime);

        if (state.Mode != CharacterMoveMode.Flying
            && state.JumpBufferRemaining > 0f
            && state.CoyoteRemaining > 0f) {
            state.Velocity = WithY(state.Velocity, settings.JumpSpeed);
            state.Mode = CharacterMoveMode.Falling;

            // Both spent. Leaving the coyote window open would let one press become two jumps on the
            // step after the first, which reads as an intermittent double jump.
            state.CoyoteRemaining = 0f;
            state.JumpBufferRemaining = 0f;
        } else if (!held && state.Velocity.Y > settings.JumpCutSpeed) {
            // Variable height, as a clamp rather than a multiplier: a multiplier applied once a step
            // makes the apex depend on how many steps the release took to notice, so the same tap
            // jumps differently at 60 Hz and at 120.
            state.Velocity = WithY(state.Velocity, settings.JumpCutSpeed);
        }

        state.JumpHeld = held;
    }

    static void Fall(in CharacterMovement settings, ref CharacterState state, float deltaTime) {
        switch (state.Mode) {
            case CharacterMoveMode.Walking:
                // Zero rather than a small downward push: the controller's own StickToFloorDistance
                // is what follows the ground over a lip, and a velocity fighting it would make a
                // character that walks down a ramp chatter.
                state.Velocity = WithY(state.Velocity, 0f);
                break;

            case CharacterMoveMode.Falling:
                state.Velocity = WithY(state.Velocity, state.Velocity.Y + (settings.Gravity * deltaTime));
                break;

            case CharacterMoveMode.Flying:
                // Nothing. Zeroing the climb here looks harmless and is not: Steer runs next and can
                // only put back one step's worth of acceleration, so a flier asking to climb at 4 m/s
                // would hold 0.67 for ever and be slower the faster the simulation ran.
                break;

            default:
                break;
        }
    }

    static void Steer(
        in CharacterMovement settings,
        ref CharacterState state,
        in MoveIntent intent,
        float deltaTime
    ) {
        var wanted = WantedVelocity(settings, in state, intent);

        var acceleration = state.Mode switch {
            CharacterMoveMode.Walking => settings.Acceleration,
            CharacterMoveMode.Flying => settings.Acceleration,
            _ => settings.AirAcceleration
        };

        var step = acceleration * deltaTime;

        if (state.Mode == CharacterMoveMode.Flying) {
            state.Velocity = MoveTowards(state.Velocity, wanted, step);
            return;
        }

        // The horizontal plane only. Folding Y in would make gravity something the character
        // accelerates *towards* at the movement acceleration, which caps the fall at the walk speed
        // and turns every drop into a float.
        var horizontal = MoveTowards(
            new Vector3(state.Velocity.X, 0f, state.Velocity.Z),
            new Vector3(wanted.X, 0f, wanted.Z),
            step
        );

        state.Velocity = new(horizontal.X, state.Velocity.Y, horizontal.Z);
    }

    /// <summary>How fast the character is asking to go, in world space.</summary>
    /// <param name="settings">How it walks.</param>
    /// <param name="state">Where it is in its motion.</param>
    /// <param name="intent">What it is being asked to do.</param>
    /// <returns>The wanted velocity.</returns>
    /// <remarks>
    ///     Public because it is the number a debug overlay and a test both want, and because an
    ///     animation graph choosing a locomotion blend wants the target rather than the current
    ///     velocity — which lags it by design.
    /// </remarks>
    public static Vector3 WantedVelocity(
        in CharacterMovement settings,
        in CharacterState state,
        in MoveIntent intent
    ) {
        var speed = TopSpeed(settings, in state, intent);

        if (state.Mode != CharacterMoveMode.Flying) {
            // MoveIntent.WorldDirection is the yaw's frame and never the pitch's, so a character
            // walking forward while looking at the sky walks along the ground.
            return intent.WorldDirection() * speed;
        }

        var direction = intent.WorldDirection();

        if (direction.LengthSquared() <= 0f) {
            return Vector3.Zero;
        }

        // Flying is the one mode the pitch steers, which is what makes a drone able to climb without
        // a second axis nobody has bound.
        var pitched = new Vector3(direction.X, MathF.Sin(intent.Pitch), direction.Z);
        var length = pitched.Length();

        return length <= 0f ? Vector3.Zero : pitched * (speed / length);
    }

    /// <summary>The top speed the character's current state and intent allow.</summary>
    /// <param name="settings">How it walks.</param>
    /// <param name="state">Where it is in its motion.</param>
    /// <param name="intent">What it is being asked to do.</param>
    /// <returns>The speed, in metres a second.</returns>
    /// <remarks>
    ///     Crouching beats sprinting rather than the other way about. A player holding both is
    ///     crouch-walking, which is what every game means and what the alternative — sprinting at
    ///     knee height — plainly does not.
    /// </remarks>
    public static float TopSpeed(in CharacterMovement settings, in CharacterState state, in MoveIntent intent) {
        if (state.IsCrouching) {
            return settings.CrouchSpeed;
        }

        return intent.IsHeld(MoveButtons.Sprint) ? settings.SprintSpeed : settings.WalkSpeed;
    }

    /// <summary>The same vector with a different vertical component.</summary>
    /// <remarks>
    ///     <c>Vector3</c>'s components are readonly fields, so every vertical rule below rebuilds the
    ///     vector rather than assigning into it. One named helper rather than five constructor calls,
    ///     because the two that would have read <c>new(v.X, v.Y + g * dt, v.Z)</c> are exactly where a
    ///     transposed component would hide.
    /// </remarks>
    static Vector3 WithY(Vector3 value, float y) => new(value.X, y, value.Z);

    /// <summary>Moves one vector towards another by at most a fixed distance.</summary>
    /// <param name="from">Where it is.</param>
    /// <param name="to">Where it is going.</param>
    /// <param name="maximum">The furthest it may travel.</param>
    /// <returns>The new value, which is exactly <paramref name="to" /> once it is within reach.</returns>
    /// <remarks>
    ///     <b>Exact arrival is the property that matters.</b> An exponential approach — the form the
    ///     cameras use, for reasons that are theirs — never quite reaches its target, so a character
    ///     tuned to walk at 4 m/s walks at 3.98 and the number in the inspector is a number it never
    ///     has. On a fixed step, linear is also frame-rate independent by construction.
    /// </remarks>
    public static Vector3 MoveTowards(Vector3 from, Vector3 to, float maximum) {
        var delta = to - from;
        var distanceSquared = delta.LengthSquared();

        if (maximum <= 0f || distanceSquared <= 0f) {
            return maximum <= 0f ? from : to;
        }

        return distanceSquared <= maximum * maximum ? to : from + (delta * (maximum / MathF.Sqrt(distanceSquared)));
    }
}

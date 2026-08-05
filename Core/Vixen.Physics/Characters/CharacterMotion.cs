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
        Mode(settings, ref state);
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

    /// <summary>
    ///     Which mode the character is in, with the water thresholds resolved before the ground.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two thresholds with a gap, and the gap is the whole mechanism</b>
    ///         ([35 § D11](../../../docs/plan/35-water.md#d11-swimming-is-a-fourth-move-mode-and-immersion-is-the-only-new-number)).
    ///         Entering needs <see cref="CharacterMovement.SwimThreshold" /> and leaving needs the
    ///         immersion to fall all the way back to <see cref="CharacterMovement.WadeThreshold" />, so
    ///         a character standing in chest-deep water with a swell has to actually rise or fall
    ///         rather than be caught by a wave crossing one line. With a single threshold the symptom
    ///         is an animation state machine stuttering between wade and swim twice a second.
    ///     </para>
    ///     <para>
    ///         <b>Water beats the ground, and that is the right precedence.</b> A character wading out
    ///         of its depth is still standing on the bed at the moment it starts to swim; asking the
    ///         ground first would keep it walking along the bottom of a lake. Coming back the other
    ///         way, an immersion below the wade threshold falls through to the ordinary ground test —
    ///         which is § D11's "exit if there is ground within a step height", answered by the probe
    ///         the walk mode already does rather than by a second one.
    ///     </para>
    /// </remarks>
    static void Mode(in CharacterMovement settings, ref CharacterState state) {
        if (state.Mode == CharacterMoveMode.Flying) {
            // Flight is entered and left by whatever granted it, never by the ground: a drone that
            // landed and silently became a walker is a drone that cannot take off again.
            return;
        }

        var swim = MathF.Max(settings.SwimThreshold, 0f);
        var wade = Math.Clamp(settings.WadeThreshold, 0f, swim);

        if (swim > 0f
            && (state.Immersion >= swim || (state.Mode == CharacterMoveMode.Swimming && state.Immersion > wade))) {
            state.Mode = CharacterMoveMode.Swimming;

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

        // ⚠ Swimming joins flying in having no jump. There is nothing to push off, and a buffered
        // press that fired the moment a swimmer's feet found the bed would launch them out of the
        // lake — which is the bug the buffer exists to avoid at the other end.
        if (state.Mode is not (CharacterMoveMode.Flying or CharacterMoveMode.Swimming)
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

            case CharacterMoveMode.Swimming:
                Float(settings, ref state, deltaTime);
                break;

            default:
                break;
        }
    }

    /// <summary>Buoyancy and drag, which is what replaces gravity for a swimmer.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Archimedes as a lerp on one number.</b> The lift exactly cancels gravity at
    ///         <see cref="CharacterMovement.SwimRestImmersion" />, is stronger below it and weaker
    ///         above — so a character dropped into a lake settles at a stated immersion instead of
    ///         bobbing about a spring somebody had to tune a stiffness and a damping for together.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The drag is what makes the rest a rest rather than an oscillation.</b> A restoring
    ///         force with no losses is a pendulum: a character dropped in from a height would rise
    ///         above the surface, fall back through it and keep going, for ever. Applied as a linear
    ///         per-step fraction and clamped, so a step at 60 Hz and one at 120 agree and neither can
    ///         overshoot into a reversal.
    ///     </para>
    /// </remarks>
    static void Float(in CharacterMovement settings, ref CharacterState state, float deltaTime) {
        var rest = MathF.Max(settings.SwimRestImmersion, 1e-3f);
        var lift = -settings.Gravity * (state.Immersion / rest);

        var vertical = state.Velocity.Y + ((settings.Gravity + lift) * deltaTime);

        vertical -= vertical * Math.Clamp(settings.SwimDrag * deltaTime, 0f, 1f);

        state.Velocity = WithY(state.Velocity, vertical);
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
            CharacterMoveMode.Swimming => settings.SwimAcceleration,
            _ => settings.AirAcceleration
        };

        var step = acceleration * deltaTime;

        if (state.Mode == CharacterMoveMode.Flying) {
            state.Velocity = MoveTowards(state.Velocity, wanted, step);
            return;
        }

        if (state.Mode == CharacterMoveMode.Swimming) {
            // ⚠ **The vertical is steered only when it is asked for, and left to Float otherwise.**
            // Moving the whole vector towards a wanted of zero — which is what no stick input is —
            // would damp the buoyant rise as well, and a swimmer who let go of the controls would
            // hang wherever they were instead of surfacing. Diving is the existing vertical axis:
            // WantedVelocity pitches the direction, exactly as it does for a flier.
            var swum = MoveTowards(
                new Vector3(state.Velocity.X, 0f, state.Velocity.Z),
                new Vector3(wanted.X, 0f, wanted.Z),
                step
            );

            var climb = wanted.Y != 0f
                ? MoveTowards(new Vector3(0f, state.Velocity.Y, 0f), new Vector3(0f, wanted.Y, 0f), step).Y
                : state.Velocity.Y;

            state.Velocity = new(swum.X, climb, swum.Z);

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

        if (state.Mode is not (CharacterMoveMode.Flying or CharacterMoveMode.Swimming)) {
            // MoveIntent.WorldDirection is the yaw's frame and never the pitch's, so a character
            // walking forward while looking at the sky walks along the ground.
            return intent.WorldDirection() * speed;
        }

        var direction = intent.WorldDirection();

        if (direction.LengthSquared() <= 0f) {
            return Vector3.Zero;
        }

        // Flying and swimming are the two modes the pitch steers, which is what makes a drone able to
        // climb and a swimmer able to dive without a second axis nobody has bound.
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
        if (state.Mode == CharacterMoveMode.Swimming) {
            return settings.SwimSpeed;
        }

        var ground = state.IsCrouching
            ? settings.CrouchSpeed
            : intent.IsHeld(MoveButtons.Sprint) ? settings.SprintSpeed : settings.WalkSpeed;

        return ground * WadeScale(settings, state.Immersion);
    }

    /// <summary>How much of its speed a character keeps at a given immersion, 0…1.</summary>
    /// <param name="settings">How the character walks.</param>
    /// <param name="immersion">How much of the capsule is under the surface, 0…1.</param>
    /// <returns>The multiplier.</returns>
    /// <remarks>
    ///     ⚠ <b>It reaches its slowest exactly where swimming starts, which is why there is no step in
    ///     speed at the transition.</b> § D11 asks for "walking, with a speed multiplier from the
    ///     depth"; a multiplier that bottomed out anywhere else would make the moment a character
    ///     begins to swim a moment it also visibly changes pace, and the two would be blamed on each
    ///     other.
    /// </remarks>
    public static float WadeScale(in CharacterMovement settings, float immersion) {
        var swim = MathF.Max(settings.SwimThreshold, 0f);

        if (!(swim > 0f) || immersion <= 0f) {
            return 1f;
        }

        // ⚠ Zero takes the default rather than meaning "stops dead in the shallows". A component is
        // a struct in a zeroed column, so a scene that names SwimThreshold and nothing else would
        // otherwise have characters that cannot move at chest depth — a bug nobody typed.
        var slowest = settings.WadeSpeedScale == 0f
            ? CharacterMovement.Default.WadeSpeedScale
            : Math.Clamp(settings.WadeSpeedScale, 0f, 1f);

        return float.Lerp(1f, slowest, Math.Clamp(immersion / swim, 0f, 1f));
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

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Players;
using Vixen.Physics.Characters;
using Vixen.Physics.Shapes;
using Xunit;

namespace Vixen.Physics.Tests;

/// <summary>
///     The movement rules, with no Jolt anywhere. Everything <see cref="CharacterMotion" /> reads is
///     an argument, so the whole of it is testable on a runner with no native library — which is what
///     makes the determinism assertion at the bottom cheap enough to run everywhere.
/// </summary>
public sealed class CharacterMotionTests {
    const float Step = 1f / 60f;

    static CharacterMovement Human => CharacterMovement.Default with { Shape = new ShapeId(1) };

    static CharacterMovement Croucher => Human with { CrouchShape = new ShapeId(2) };

    static MoveIntent Forward => new() { Move = new(0f, 1f) };

    static void Advance(
        in CharacterMovement settings,
        ref CharacterState state,
        in MoveIntent intent,
        CharacterGround ground,
        int steps
    ) {
        for (var step = 0; step < steps; step++) {
            CharacterMotion.Step(settings, ref state, intent, ground, Step);
        }
    }

    [Fact]
    public void AGroundedCharacterIsWalkingAndHasNoVerticalVelocity() {
        var state = default(CharacterState);

        CharacterMotion.Step(Human, ref state, default, CharacterGround.Grounded, Step);

        Assert.Equal(CharacterMoveMode.Walking, state.Mode);
        Assert.Equal(0f, state.Velocity.Y);
    }

    [Fact]
    public void AnAirborneCharacterAccumulatesGravity() {
        var state = default(CharacterState);

        Advance(Human, ref state, default, CharacterGround.Airborne, 30);

        Assert.Equal(CharacterMoveMode.Falling, state.Mode);
        Assert.Equal(Human.Gravity * 0.5f, state.Velocity.Y, 3);
    }

    [Fact]
    public void WalkingReachesExactlyTheAuthoredTopSpeed() {
        var state = default(CharacterState);

        Advance(Human, ref state, Forward, CharacterGround.Grounded, 60);

        // Exactly, and that is the argument for a linear approach over an exponential one: a
        // character tuned to 4 m/s should walk at 4 m/s rather than at 3.98 for ever.
        Assert.Equal(Human.WalkSpeed, state.Velocity.Length(), 5);
        Assert.Equal(-Human.WalkSpeed, state.Velocity.Z, 5);
    }

    [Fact]
    public void SprintingIsFasterAndCrouchingIsSlower() {
        var sprinting = default(CharacterState);
        var crouching = default(CharacterState);

        Advance(Croucher, ref sprinting, Forward with { Buttons = MoveButtons.Sprint }, CharacterGround.Grounded, 60);
        Advance(Croucher, ref crouching, Forward with { Buttons = MoveButtons.Crouch }, CharacterGround.Grounded, 60);

        Assert.Equal(Croucher.SprintSpeed, sprinting.Velocity.Length(), 4);
        Assert.Equal(Croucher.CrouchSpeed, crouching.Velocity.Length(), 4);
        Assert.True(crouching.IsCrouching);
    }

    /// <summary>A player holding both is crouch-walking, not sprinting at knee height.</summary>
    [Fact]
    public void CrouchBeatsSprint() {
        var state = default(CharacterState);
        var both = Forward with { Buttons = MoveButtons.Crouch | MoveButtons.Sprint };

        Advance(Croucher, ref state, both, CharacterGround.Grounded, 60);

        Assert.Equal(Croucher.CrouchSpeed, state.Velocity.Length(), 4);
    }

    [Fact]
    public void CrouchingDoesNothingWithoutACrouchShape() {
        var state = default(CharacterState);

        Advance(Human, ref state, Forward with { Buttons = MoveButtons.Crouch }, CharacterGround.Grounded, 60);

        Assert.False(state.IsCrouching);
        Assert.Equal(Human.WalkSpeed, state.Velocity.Length(), 4);
    }

    [Fact]
    public void AGroundedCharacterJumpsAndLeavesTheGround() {
        var state = default(CharacterState);

        CharacterMotion.Step(Human, ref state, default, CharacterGround.Grounded, Step);
        CharacterMotion.Step(Human, ref state, new() { Buttons = MoveButtons.Jump }, CharacterGround.Grounded, Step);

        Assert.Equal(CharacterMoveMode.Falling, state.Mode);
        Assert.True(state.Velocity.Y > 0f);

        // The jump speed minus the one step of gravity the same step applies — semi-implicit Euler,
        // and the alternative is a jump whose height depends on where in the pass gravity happens.
        Assert.Equal(Human.JumpSpeed + (Human.Gravity * Step), state.Velocity.Y, 4);
    }

    /// <summary>
    ///     Holding jump must not make a character bounce for ever. The press is an edge, not a level.
    /// </summary>
    [Fact]
    public void HoldingJumpDoesNotJumpTwice() {
        var state = default(CharacterState);
        var held = new MoveIntent { Buttons = MoveButtons.Jump };

        CharacterMotion.Step(Human, ref state, default, CharacterGround.Grounded, Step);
        CharacterMotion.Step(Human, ref state, held, CharacterGround.Grounded, Step);

        var afterFirst = state.Velocity.Y;

        // Still holding, and back on the ground: the buffer must be empty.
        Advance(Human, ref state, held, CharacterGround.Grounded, 30);

        Assert.True(afterFirst > 0f);
        Assert.Equal(0f, state.Velocity.Y);
        Assert.Equal(CharacterMoveMode.Walking, state.Mode);
    }

    [Fact]
    public void JumpingWorksJustAfterWalkingOffALedge() {
        var state = default(CharacterState);

        CharacterMotion.Step(Human, ref state, default, CharacterGround.Grounded, Step);

        // Airborne, but inside the coyote window.
        Advance(Human, ref state, default, CharacterGround.Airborne, 3);
        CharacterMotion.Step(Human, ref state, new() { Buttons = MoveButtons.Jump }, CharacterGround.Airborne, Step);

        Assert.True(state.Velocity.Y > 0f);
    }

    [Fact]
    public void JumpingDoesNotWorkOnceTheCoyoteWindowHasClosed() {
        var state = default(CharacterState);

        CharacterMotion.Step(Human, ref state, default, CharacterGround.Grounded, Step);
        Advance(Human, ref state, default, CharacterGround.Airborne, 30);

        var before = state.Velocity.Y;
        CharacterMotion.Step(Human, ref state, new() { Buttons = MoveButtons.Jump }, CharacterGround.Airborne, Step);

        Assert.True(state.Velocity.Y < before);
    }

    /// <summary>
    ///     The other half of the same forgiveness: a press just before landing is remembered.
    /// </summary>
    [Fact]
    public void JumpingJustBeforeLandingIsRemembered() {
        var state = default(CharacterState);

        Advance(Human, ref state, default, CharacterGround.Airborne, 30);
        CharacterMotion.Step(Human, ref state, new() { Buttons = MoveButtons.Jump }, CharacterGround.Airborne, Step);

        Assert.True(state.Velocity.Y < 0f);

        // Landed, still within the buffer, and the button is no longer held — which is the case a
        // level-triggered jump would miss entirely.
        CharacterMotion.Step(Human, ref state, default, CharacterGround.Grounded, Step);

        Assert.True(state.Velocity.Y > 0f);
    }

    [Fact]
    public void ReleasingJumpEarlyCutsTheRise() {
        var full = default(CharacterState);
        var cut = default(CharacterState);
        var held = new MoveIntent { Buttons = MoveButtons.Jump };

        CharacterMotion.Step(Human, ref full, default, CharacterGround.Grounded, Step);
        CharacterMotion.Step(Human, ref cut, default, CharacterGround.Grounded, Step);

        CharacterMotion.Step(Human, ref full, held, CharacterGround.Grounded, Step);
        CharacterMotion.Step(Human, ref cut, held, CharacterGround.Grounded, Step);

        Advance(Human, ref full, held, CharacterGround.Airborne, 2);
        Advance(Human, ref cut, default, CharacterGround.Airborne, 2);

        Assert.True(cut.Velocity.Y < full.Velocity.Y);
        Assert.True(cut.Velocity.Y <= Human.JumpCutSpeed);
    }

    /// <summary>How high a jump gets before it starts coming down, simulated at a given step rate.</summary>
    static float Apex(in CharacterMovement settings, float step, bool holdJump) {
        var state = default(CharacterState);
        var held = new MoveIntent { Buttons = MoveButtons.Jump };

        CharacterMotion.Step(settings, ref state, default, CharacterGround.Grounded, step);
        CharacterMotion.Step(settings, ref state, held, CharacterGround.Grounded, step);

        var height = state.Velocity.Y * step;
        var after = holdJump ? held : default;

        while (state.Velocity.Y > 0f) {
            CharacterMotion.Step(settings, ref state, after, CharacterGround.Airborne, step);
            height += MathF.Max(0f, state.Velocity.Y) * step;
        }

        return height;
    }

    /// <summary>
    ///     The cut is a clamp, so the speed a released jump is left with is
    ///     <see cref="CharacterMovement.JumpCutSpeed" /> at any step rate. A multiplier applied once a
    ///     step would leave a different speed at every rate, and the apex would halve between 60 Hz
    ///     and 120.
    /// </summary>
    [Theory]
    [InlineData(1f / 30f)]
    [InlineData(1f / 60f)]
    [InlineData(1f / 240f)]
    public void TheCutLeavesTheSameSpeedAtEveryStepRate(float step) {
        var state = default(CharacterState);

        CharacterMotion.Step(Human, ref state, default, CharacterGround.Grounded, step);
        CharacterMotion.Step(Human, ref state, new() { Buttons = MoveButtons.Jump }, CharacterGround.Grounded, step);

        // Released. The clamp lands on JumpCutSpeed and the same step's gravity takes its bite;
        // adding that back is what isolates the cut from the integrator.
        CharacterMotion.Step(Human, ref state, default, CharacterGround.Airborne, step);

        Assert.Equal(Human.JumpCutSpeed, state.Velocity.Y - (Human.Gravity * step), 4);
    }

    /// <summary>
    ///     And it stops cutting once it has cut. A multiplier would keep shrinking the fall as well
    ///     as the rise, which reads as a character that hangs in the air after a tap.
    /// </summary>
    [Fact]
    public void TheCutAppliesOnceAndThenLeavesGravityAlone() {
        var state = default(CharacterState);

        CharacterMotion.Step(Human, ref state, default, CharacterGround.Grounded, Step);
        CharacterMotion.Step(Human, ref state, new() { Buttons = MoveButtons.Jump }, CharacterGround.Grounded, Step);
        CharacterMotion.Step(Human, ref state, default, CharacterGround.Airborne, Step);

        var afterCut = state.Velocity.Y;

        for (var index = 0; index < 10; index++) {
            var before = state.Velocity.Y;
            CharacterMotion.Step(Human, ref state, default, CharacterGround.Airborne, Step);

            Assert.Equal(before + (Human.Gravity * Step), state.Velocity.Y, 4);
        }

        Assert.True(afterCut > state.Velocity.Y);
    }

    [Fact]
    public void FlyingIgnoresGravityAndIsSteeredByThePitch() {
        var state = new CharacterState { Mode = CharacterMoveMode.Flying };
        var climbing = Forward with { Pitch = MathUtil.PiOverTwo * 0.5f };

        Advance(Human, ref state, climbing, CharacterGround.Airborne, 120);

        Assert.Equal(CharacterMoveMode.Flying, state.Mode);
        Assert.True(state.Velocity.Y > 0f);
        Assert.Equal(Human.WalkSpeed, state.Velocity.Length(), 3);
    }

    /// <summary>Landing must not silently ground a flier, or it could never take off again.</summary>
    [Fact]
    public void FlyingSurvivesTouchingTheGround() {
        var state = new CharacterState { Mode = CharacterMoveMode.Flying };

        Advance(Human, ref state, Forward, CharacterGround.Grounded, 10);

        Assert.Equal(CharacterMoveMode.Flying, state.Mode);
    }

    [Fact]
    public void AnIdleCharacterComesToACompleteStop() {
        var state = default(CharacterState);

        Advance(Human, ref state, Forward, CharacterGround.Grounded, 60);
        Advance(Human, ref state, default, CharacterGround.Grounded, 60);

        Assert.Equal(Vector3.Zero, state.Velocity);
    }

    /// <summary>
    ///     The property [16](../../../docs/plan/16-networking.md)'s prediction rests on: the same
    ///     inputs from the same state produce the same state, bit for bit. A rule that reads a clock,
    ///     an unseeded random source or a field of its own fails this — and the symptom in a game is
    ///     a player who twitches on a connection that is behaving perfectly.
    /// </summary>
    [Fact]
    public void ReplayingTheSameInputsProducesTheSameState() {
        static CharacterState Run() {
            var state = default(CharacterState);
            var settings = Croucher;

            for (var tick = 0; tick < 600; tick++) {
                var buttons = MoveButtons.None;

                if (tick % 37 == 0) {
                    buttons |= MoveButtons.Jump;
                }

                if (tick % 53 < 20) {
                    buttons |= MoveButtons.Sprint;
                }

                if (tick % 91 < 15) {
                    buttons |= MoveButtons.Crouch;
                }

                var intent = new MoveIntent {
                    Move = new(MathF.Sin(tick * 0.1f), MathF.Cos(tick * 0.07f)),
                    Yaw = tick * 0.013f,
                    Buttons = buttons
                };

                var ground = tick % 11 < 8 ? CharacterGround.Grounded : CharacterGround.Airborne;
                CharacterMotion.Step(settings, ref state, intent, ground, Step);
            }

            return state;
        }

        var first = Run();
        var second = Run();

        Assert.Equal(first.Velocity, second.Velocity);
        Assert.Equal(first.Mode, second.Mode);
        Assert.Equal(first.CoyoteRemaining, second.CoyoteRemaining);
        Assert.Equal(first.JumpBufferRemaining, second.JumpBufferRemaining);
        Assert.Equal(first.IsCrouching, second.IsCrouching);
        Assert.Equal(first.JumpHeld, second.JumpHeld);
    }

    [Fact]
    public void AJumpSpeedCanBeDerivedFromAHeight() {
        var gravity = CharacterMovement.Default.Gravity;
        var speed = CharacterMovement.JumpSpeedForHeight(1.5f, gravity);

        // The closed form, exactly: v² = 2gh is what the helper inverts, and a test that only checked
        // a simulation would pass with a formula that was wrong by whatever the integrator's error is.
        Assert.Equal(1.5f, speed * speed / (2f * -gravity), 4);

        var simulated = Apex(Human with { JumpSpeed = speed }, Step, holdJump: true);

        // Semi-implicit Euler undershoots by about v·dt/2 — 6 cm at 7.7 m/s and 60 Hz. Asserted
        // loosely on purpose: tightening this would be asserting the integrator rather than the jump.
        Assert.InRange(simulated, 1.35f, 1.55f);
    }

    [Fact]
    public void AJumpSpeedForAnImpossibleHeightIsZero() {
        Assert.Equal(0f, CharacterMovement.JumpSpeedForHeight(0f, -9.81f));
        Assert.Equal(0f, CharacterMovement.JumpSpeedForHeight(1f, 0f));
        Assert.Equal(0f, CharacterMovement.JumpSpeedForHeight(1f, 9.81f));
    }

    [Fact]
    public void MoveTowardsArrivesExactlyRatherThanApproaching() {
        var target = new Vector3(3f, 0f, 4f);

        Assert.Equal(target, CharacterMotion.MoveTowards(Vector3.Zero, target, 5f));
        Assert.Equal(target, CharacterMotion.MoveTowards(Vector3.Zero, target, 100f));
        Assert.Equal(new Vector3(1.5f, 0f, 2f), CharacterMotion.MoveTowards(Vector3.Zero, target, 2.5f));
    }
}

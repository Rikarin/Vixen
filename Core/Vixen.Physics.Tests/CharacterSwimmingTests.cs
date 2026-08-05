// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Players;
using Vixen.Physics.Characters;
using Xunit;

namespace Tests;

/// <summary>
///     The fourth move mode — [docs/plan/35 § D11], and the row [docs/plan/29] left open.
/// </summary>
/// <remarks>
///     <para>
///         Doc 29 said "no swimming — it needs water volumes, which do not exist, and a mode that
///         could never be entered would be a promise in an enum". What closes it is one number on
///         <see cref="CharacterState" /> and two thresholds on it; everything here is a test of that
///         claim rather than of a swimming system.
///     </para>
///     <para>
///         ⚠ <b>The hysteresis test is the one that fails without the gap</b>, and it is the one whose
///         absence is felt as an animation state machine stuttering rather than as a physics bug.
///     </para>
/// </remarks>
public sealed class CharacterSwimmingTests {
    const float Step = 1f / 60f;

    static CharacterMovement Settings => CharacterMovement.Default;

    static CharacterState At(float immersion, CharacterMoveMode mode = CharacterMoveMode.Walking) =>
        new() { Mode = mode, Immersion = immersion };

    static void Advance(ref CharacterState state, CharacterGround ground = CharacterGround.Grounded, int steps = 1) {
        for (var index = 0; index < steps; index++) {
            CharacterMotion.Step(Settings, ref state, default, ground, Step);
        }
    }

    // --- Entering and leaving -----------------------------------------------

    [Fact]
    public void A_character_out_of_its_depth_swims_even_while_standing_on_the_bed() {
        var state = At(0.9f);

        Advance(ref state);

        // ⚠ Grounded, and swimming anyway. A character wading out of its depth is still standing on
        // the bed at the moment it starts to swim; asking the ground first would keep it walking
        // along the bottom of the lake.
        Assert.Equal(CharacterMoveMode.Swimming, state.Mode);
        Assert.True(state.IsSwimming);
    }

    [Fact]
    public void A_swimmer_who_finds_the_shallows_walks_out() {
        var state = At(0.9f);

        Advance(ref state);
        Assert.Equal(CharacterMoveMode.Swimming, state.Mode);

        state.Immersion = 0.3f;

        // ⚠ Not on the first step, and that is right rather than a lag to fix. The swimmer was rising
        // — buoyancy had it above its rest immersion — so the step it becomes shallow it is still
        // moving upward, and the ground test's own upward guard reads that as leaving the ground
        // rather than landing on it. Gravity takes it back in a frame or two, which is exactly what
        // walking out of a lake looks like.
        Advance(ref state);
        Assert.Equal(CharacterMoveMode.Falling, state.Mode);

        Advance(ref state, steps: 10);
        Assert.Equal(CharacterMoveMode.Walking, state.Mode);
    }

    [Fact]
    public void A_swimmer_with_no_ground_under_it_falls_rather_than_walking() {
        var state = At(0.9f);

        Advance(ref state);

        state.Immersion = 0f;
        Advance(ref state, CharacterGround.Airborne);

        Assert.Equal(CharacterMoveMode.Falling, state.Mode);
    }

    /// <summary>A dry component never swims, which is what makes this change invisible to old scenes.</summary>
    /// <remarks>
    ///     ⚠ A component is a struct in a zeroed column, so a scene saved before swimming existed
    ///     deserialises with every water field at zero. A threshold of zero has to mean "this
    ///     character does not swim" rather than "this character always swims".
    /// </remarks>
    [Fact]
    public void A_character_with_no_swim_thresholds_never_swims() {
        var settings = new CharacterMovement();
        var state = At(1f);

        CharacterMotion.Step(settings, ref state, default, CharacterGround.Grounded, Step);

        Assert.Equal(CharacterMoveMode.Walking, state.Mode);
        Assert.Equal(1f, CharacterMotion.WadeScale(settings, 1f), 5);
    }

    // --- The hysteresis, which is the mechanism -----------------------------

    /// <summary>
    ///     A character held at the swim threshold in a swell changes mode at most once a second.
    /// </summary>
    /// <remarks>
    ///     [§ Part 4]'s stutter test. The swell here is ±0.09 of the capsule at 1 Hz, which straddles
    ///     the 0.8 threshold on every cycle — with a single threshold the mode would change twice per
    ///     cycle, and the animation graph would flicker between wade and swim for as long as somebody
    ///     stood there.
    /// </remarks>
    [Fact]
    public void A_character_at_the_threshold_in_a_swell_does_not_stutter() {
        var state = At(0.8f);
        var changes = 0;

        for (var index = 0; index < 600; index++) {
            var before = state.Mode;

            // A swell about the swim threshold, entirely inside the gap between the two.
            state.Immersion = 0.8f + (0.09f * MathF.Sin(index * Step * MathF.Tau));

            Advance(ref state);

            if (state.Mode != before) {
                changes++;
            }
        }

        // Ten seconds of swell, and the only change allowed is the first entry.
        Assert.True(changes <= 1, $"the mode changed {changes} times in ten seconds of swell.");
        Assert.Equal(CharacterMoveMode.Swimming, state.Mode);
    }

    /// <summary>And the negative control: one threshold, and the same swell stutters.</summary>
    /// <remarks>
    ///     ⚠ Without this the test above cannot fail — a mode that never changes passes it. Collapsing
    ///     the gap is the only difference, and it is what turns one change into twenty.
    /// </remarks>
    [Fact]
    public void The_same_swell_stutters_when_the_gap_is_closed() {
        var settings = Settings with { WadeThreshold = 0.8f };
        var state = At(0.8f);
        var changes = 0;

        for (var index = 0; index < 600; index++) {
            var before = state.Mode;

            state.Immersion = 0.8f + (0.09f * MathF.Sin(index * Step * MathF.Tau));

            CharacterMotion.Step(settings, ref state, default, CharacterGround.Grounded, Step);

            if (state.Mode != before) {
                changes++;
            }
        }

        Assert.True(changes >= 10, $"the collapsed gap only stuttered {changes} times, so the control proves nothing.");
    }

    // --- Buoyancy -----------------------------------------------------------

    /// <summary>A swimmer settles at the rest immersion rather than sinking or launching.</summary>
    [Fact]
    public void Buoyancy_settles_a_swimmer_at_its_rest_immersion() {
        var state = At(1f, CharacterMoveMode.Swimming);

        // Fully submerged: buoyancy is stronger than gravity, so the character rises.
        Advance(ref state, CharacterGround.Airborne, 10);
        Assert.True(state.Velocity.Y > 0f, "a fully submerged character did not rise.");

        // At the rest immersion exactly, nothing pushes — so whatever motion is left only damps out.
        state = At(Settings.SwimRestImmersion, CharacterMoveMode.Swimming);
        Advance(ref state, CharacterGround.Airborne, 240);

        Assert.Equal(0f, state.Velocity.Y, 3);

        // And above it, the character sinks back down.
        state = At(0.3f, CharacterMoveMode.Swimming) with { Mode = CharacterMoveMode.Swimming };
        Advance(ref state, CharacterGround.Airborne, 10);

        Assert.True(state.Velocity.Y < 0f, "a barely submerged character did not sink.");
    }

    /// <summary>The rest is a rest and not an oscillation, which is what the drag is for.</summary>
    [Fact]
    public void A_swimmer_dropped_in_stops_bobbing() {
        var state = At(1f, CharacterMoveMode.Swimming) with { Velocity = new(0f, -6f, 0f) };

        for (var index = 0; index < 600; index++) {
            CharacterMotion.Step(Settings, ref state, default, CharacterGround.Airborne, Step);

            // The immersion a real bridge would write back: deeper when moving down, and clamped.
            state.Immersion = Math.Clamp(state.Immersion - (state.Velocity.Y * Step * 0.5f), 0f, 1f);
        }

        Assert.True(MathF.Abs(state.Velocity.Y) < 0.05f, $"it was still moving at {state.Velocity.Y} m/s.");
    }

    // --- Speed and steering -------------------------------------------------

    /// <summary>Wading slows a walker, and it is slowest exactly where swimming starts.</summary>
    [Fact]
    public void Wading_slows_the_walk_and_meets_the_swim_without_a_step() {
        Assert.Equal(1f, CharacterMotion.WadeScale(Settings, 0f), 5);
        Assert.Equal(Settings.WadeSpeedScale, CharacterMotion.WadeScale(Settings, Settings.SwimThreshold), 5);

        // Monotone across the whole range, so there is no depth at which wading gets faster.
        var previous = 2f;

        for (var at = 0; at <= 100; at++) {
            var scale = CharacterMotion.WadeScale(Settings, at / 100f);

            Assert.True(scale <= previous + 1e-6f, $"wading got faster at {at / 100f}.");
            previous = scale;
        }
    }

    /// <summary>Diving is the existing vertical axis, and nothing about the intent changes.</summary>
    /// <remarks>
    ///     § D11's claim about the seam: a swimming character produces the same
    ///     <see cref="MoveIntent" /> a walking one does, so nothing on the network path changes.
    /// </remarks>
    [Fact]
    public void The_pitch_steers_a_dive() {
        var state = At(1f, CharacterMoveMode.Swimming);

        var diving = new MoveIntent { Move = new(0f, 1f), Yaw = 0f, Pitch = -MathF.PI / 4f };

        for (var index = 0; index < 60; index++) {
            CharacterMotion.Step(Settings, ref state, diving, CharacterGround.Airborne, Step);
        }

        Assert.True(state.Velocity.Y < 0f, "looking down and swimming forward did not dive.");
    }

    /// <summary>There is nothing to jump off, so a swimmer does not.</summary>
    [Fact]
    public void A_swimmer_cannot_jump() {
        var state = At(0.9f);

        Advance(ref state);
        Assert.Equal(CharacterMoveMode.Swimming, state.Mode);

        var jumping = new MoveIntent { Buttons = MoveButtons.Jump };

        for (var index = 0; index < 30; index++) {
            CharacterMotion.Step(Settings, ref state, jumping, CharacterGround.Grounded, Step);
        }

        Assert.Equal(CharacterMoveMode.Swimming, state.Mode);
        Assert.True(state.Velocity.Y < Settings.JumpSpeed * 0.5f, "a swimmer jumped.");
    }
}

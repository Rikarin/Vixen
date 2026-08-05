// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Water;
using Xunit;

namespace Vixen.Water.Tests;

/// <summary>
///     The sliding-window height field — [docs/plan/35 § D12], and W8's host half.
/// </summary>
/// <remarks>
///     <para>
///         § Risks calls this "an unbounded feature wearing a bounded name", so most of what is here is
///         a bound: the field stays finite, the boundary does not reflect, the window scrolls without
///         blurring, and the injection budget is <em>reported</em> rather than silently spent.
///     </para>
///     <para>
///         ⚠ <b>The stability test is the one whose absence is a NaN rather than a wrong picture.</b>
///         An explicit wave equation past its Courant limit does not look wrong — it grows without
///         bound in a few dozen steps, and everything downstream reads a NaN.
///     </para>
/// </remarks>
public sealed class WaterRippleTests {
    const float Step = 1f / 60f;

    static WaterRippleSettings Settings =>
        WaterRippleSettings.Default with { Extent = 32f, Resolution = 65, EdgeFade = 4f };

    static WaterRipples Ripples() => new(Settings, new(-16f, -16f));

    // --- It stays finite -----------------------------------------------------

    /// <summary>A disturbance spreads, damps and dies rather than growing.</summary>
    [Fact]
    public void A_disturbance_spreads_and_dies_away() {
        var ripples = Ripples();

        Assert.True(ripples.Inject(Vector2.Zero, 1.5f, 4f));
        ripples.Step(Step);

        var early = ripples.Peak;

        Assert.True(early > 0f, "the injection did nothing.");

        // It reaches out from where it was put.
        for (var index = 0; index < 30; index++) {
            ripples.Step(Step);
        }

        Assert.True(ripples.TryDisplacement(new(3f, 0f), out var away));
        Assert.True(MathF.Abs(away) > 1e-4f, "the ripple never left its own texel.");

        // And it is gone within a few seconds rather than ringing for ever.
        for (var index = 0; index < 900; index++) {
            ripples.Step(Step);
        }

        Assert.True(ripples.Peak < early * 0.05f, $"it was still {ripples.Peak} m after fifteen seconds.");
    }

    /// <summary>The Courant limit is refused rather than discovered.</summary>
    /// <remarks>
    ///     ⚠ Past it an explicit wave equation grows without bound in a few dozen steps. Refusing at
    ///     the seam is what turns a NaN in a boat's position into a message an author can read.
    /// </remarks>
    [Fact]
    public void A_speed_above_the_courant_limit_is_refused() {
        var settings = Settings;
        var limit = settings.MaximumSpeed(Step);

        Assert.Null((settings with { Speed = limit * 0.9f }).Validate(Step));
        Assert.NotNull((settings with { Speed = limit * 1.1f }).Validate(Step));

        var ripples = new WaterRipples(settings with { Speed = limit * 1.1f });

        Assert.Throws<ArgumentException>(() => ripples.Step(Step));
    }

    /// <summary>And below it the field stays bounded however long it runs.</summary>
    [Fact]
    public void A_stable_field_does_not_grow() {
        var ripples = Ripples();

        for (var index = 0; index < 2000; index++) {
            if (index % 20 == 0) {
                ripples.Inject(new(MathF.Sin(index * 0.1f) * 6f, MathF.Cos(index * 0.1f) * 6f), 1f, 3f);
            }

            ripples.Step(Step);

            Assert.True(float.IsFinite(ripples.Peak), $"the field went non-finite at step {index}.");
            Assert.True(ripples.Peak < 100f, $"the field grew to {ripples.Peak} m at step {index}.");
        }
    }

    // --- The boundary does not reflect ---------------------------------------

    /// <summary>A wake reaching the edge is absorbed rather than mirrored.</summary>
    /// <remarks>
    ///     ⚠ Without the fade the boundary is a mirror, and a wake returns to meet itself a second
    ///     later — which reads as the lake having invisible walls exactly as far away as the window is
    ///     wide. The control below is what makes this measurable.
    /// </remarks>
    [Fact]
    public void The_edge_absorbs_rather_than_reflecting() {
        var faded = Run(Settings);
        var mirrored = Run(Settings with { EdgeFade = 0f, Damping = 0f });

        Assert.True(
            faded < mirrored * 0.5f,
            $"the faded edge returned {faded} m against the mirror's {mirrored} m, so it is not absorbing."
        );

        static float Run(in WaterRippleSettings settings) {
            var ripples = new WaterRipples(settings, new(-16f, -16f));

            ripples.Inject(Vector2.Zero, 1f, 6f);

            var returned = 0f;

            // Long enough for a disturbance to reach the edge and come back if it is going to.
            for (var index = 0; index < 400; index++) {
                ripples.Step(Step);

                if (index > 200) {
                    ripples.TryDisplacement(Vector2.Zero, out var middle);
                    returned = MathF.Max(returned, MathF.Abs(middle));
                }
            }

            return returned;
        }
    }

    // --- The budget is reported ----------------------------------------------

    /// <summary>Over budget, the overflow is a number rather than a silence.</summary>
    /// <remarks>
    ///     § D12's own wording. A budget nobody can see is one nobody raises before shipping, and a
    ///     scene over it has <em>arbitrary</em> ripples rather than merely fewer — whichever sources
    ///     happened to be walked first got them.
    /// </remarks>
    [Fact]
    public void The_injection_budget_is_reported_rather_than_dropped_silently() {
        var ripples = new WaterRipples(Settings with { InjectionBudget = 4 }, new(-16f, -16f));

        for (var index = 0; index < 10; index++) {
            var accepted = ripples.Inject(new(index * 0.5f, 0f), 0.5f, 1f);

            Assert.Equal(index < 4, accepted);
        }

        Assert.Equal(4, ripples.Injections);
        Assert.Equal(6, ripples.Overflowed);

        // And the budget is per step, so the next step starts fresh.
        ripples.Step(Step);

        Assert.Equal(0, ripples.Injections);
        Assert.Equal(0, ripples.Overflowed);
        Assert.True(ripples.Inject(Vector2.Zero, 0.5f, 1f));
    }

    // --- The window scrolls --------------------------------------------------

    /// <summary>A wake survives the window scrolling under it.</summary>
    /// <remarks>
    ///     ⚠ <b>The one place this differs from <see cref="WaterField" />, which forgets its contents
    ///     when it moves.</b> A field is a function of bodies and ground that can be recomputed; a
    ///     simulation's state <em>is</em> its history, and throwing it away when the camera walks would
    ///     delete every wake in the scene.
    /// </remarks>
    [Fact]
    public void A_wake_survives_the_window_scrolling() {
        var ripples = Ripples();

        ripples.Inject(new(4f, 0f), 2f, 8f);

        for (var index = 0; index < 10; index++) {
            ripples.Step(Step);
        }

        ripples.TryDisplacement(new(4f, 0f), out var before);
        Assert.True(MathF.Abs(before) > 1e-3f, "there was no wake to carry.");

        // Walk the window four metres. The wake is at the same world position afterwards.
        Assert.True(ripples.Follow(new(4f, 0f)));
        ripples.TryDisplacement(new(4f, 0f), out var after);

        Assert.Equal(before, after, 4);
    }

    /// <summary>The window's origin lands on a whole texel, always.</summary>
    /// <remarks>
    ///     ⚠ A window that moved by a fraction of a texel would resample its own state every step, and
    ///     a resampling filter applied repeatedly is a low-pass filter — the wake would blur away over
    ///     a few seconds of walking and be sharp again the moment the camera stopped.
    /// </remarks>
    [Fact]
    public void The_window_snaps_to_a_texel() {
        var ripples = Ripples();
        var step = Settings.MetresPerTexel;

        for (var index = 0; index < 200; index++) {
            ripples.Follow(new(index * 0.137f, index * -0.211f));

            AssertOnGrid(ripples.Origin.X, step);
            AssertOnGrid(ripples.Origin.Y, step);
        }

        static void AssertOnGrid(float value, float step) {
            var steps = value / step;

            Assert.True(
                MathF.Abs(steps - MathF.Round(steps)) < 1e-3f,
                $"{value} is {steps} texels, which is not a whole number of them."
            );
        }
    }

    /// <summary>And it never steps backwards while the view moves forward.</summary>
    [Fact]
    public void The_window_never_steps_backwards() {
        var ripples = Ripples();
        var previous = ripples.Origin.X;

        for (var index = 0; index < 400; index++) {
            ripples.Follow(new(index * 0.05f, 0f));

            Assert.True(ripples.Origin.X >= previous - 1e-4f, $"the window went backwards at step {index}.");
            previous = ripples.Origin.X;
        }
    }

    /// <summary>A window that jumps clean out of its own extent starts again rather than smearing.</summary>
    [Fact]
    public void A_teleport_clears_the_field() {
        var ripples = Ripples();

        ripples.Inject(Vector2.Zero, 2f, 8f);
        ripples.Step(Step);

        Assert.True(ripples.Peak > 0f);

        ripples.Follow(new(4000f, 4000f));

        Assert.Equal(0f, ripples.Peak, 6);
    }

    // --- What the evaluator does with it -------------------------------------

    /// <summary>The evaluator adds the ripple, and a caller that passes none gets the closed form.</summary>
    /// <remarks>
    ///     ⚠ <b>The asymmetry § D12 is built on.</b> The closed-form part is exact and answerable at
    ///     any past time; the simulated part is neither. So the network path — which rolls back six
    ///     ticks — asks for the closed form alone, by passing no ripples at all, and the signature is
    ///     what enforces that rather than a comment asking for it.
    /// </remarks>
    [Fact]
    public void The_evaluator_adds_a_ripple_only_when_it_is_given_one() {
        var ripples = Ripples();

        ripples.Inject(Vector2.Zero, 2f, 10f);
        ripples.Step(Step);

        var evaluator = new WaterEvaluator(null, [], WaterAttenuation.Default);

        var closed = evaluator.Height(Vector2.Zero, 0f);
        var simulated = evaluator.Height(Vector2.Zero, 0f, ripples);

        Assert.NotEqual(closed, simulated);
        Assert.Equal(0f, closed, 6);

        ripples.TryDisplacement(Vector2.Zero, out var lift);
        Assert.Equal(lift, simulated - closed, 5);
    }
}

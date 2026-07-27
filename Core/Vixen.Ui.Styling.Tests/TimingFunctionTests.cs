// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>The easing curves, and the physics behind the one CSS does not have.</summary>
public class TimingFunctionTests {
    const float Tolerance = 1e-3f;

    [Fact]
    public void Every_easing_starts_at_nothing_and_ends_at_everything() {
        // The one property every timing function has to have, whatever it does in between. A curve
        // that does not reach 1 leaves a transition permanently short of its target, which reads as
        // a rendering bug rather than an easing one.
        foreach (var easing in new[] {
            TimingFunction.Linear,
            TimingFunction.Ease,
            TimingFunction.EaseIn,
            TimingFunction.EaseOut,
            TimingFunction.EaseInOut,
            TimingFunction.Bezier(0.68f, -0.55f, 0.27f, 1.55f),
            TimingFunction.Step(4, StepPosition.End),
            TimingFunction.Spring(1f, 100f, 10f)
        }) {
            Assert.Equal(0f, easing.Evaluate(0f), Tolerance);
            Assert.Equal(1f, easing.Evaluate(1f), 1e-2f);
        }
    }

    [Fact]
    public void Linear_is_the_identity() {
        for (var i = 0; i <= 10; i++) {
            var t = i / 10f;
            Assert.Equal(t, TimingFunction.Linear.Evaluate(t), Tolerance);
        }
    }

    [Fact]
    public void Solving_for_x_inverts_evaluating_it() {
        // The Bézier solver's own oracle, and a real one: evaluating the curve at a parameter and
        // solving for that parameter are opposite directions through different code. If the solver
        // is wrong, feeding it the x it produced does not give the y that came with it.
        //
        // Stated as: for the curve's own parametric points, `Evaluate(x(t))` must equal `y(t)`.
        Gen.Select(Gen.Float[0f, 1f], Gen.Float[-0.5f, 1.5f], Gen.Float[0f, 1f], Gen.Float[-0.5f, 1.5f], Gen.Float[0f, 1f])
            .Sample(sample => {
                    var (x1, y1, x2, y2, t) = sample;
                    var easing = TimingFunction.Bezier(x1, y1, x2, y2);

                    var x = Cubic(t, easing.X1, easing.X2);
                    var y = Cubic(t, easing.Y1, easing.Y2);

                    Assert.Equal(y, easing.Evaluate(x), 1e-2f);
                }, iter: 1000
            );
    }

    [Fact]
    public void A_control_point_above_one_overshoots_and_comes_back() {
        // Which is why the y coordinates are unclamped. Every "bounce" easing anybody has pasted
        // into a stylesheet works this way, and clamping would silently flatten it.
        var overshooting = TimingFunction.Bezier(0.34f, 1.56f, 0.64f, 1f);
        var peak = 0f;

        for (var i = 0; i <= 100; i++) {
            peak = MathF.Max(peak, overshooting.Evaluate(i / 100f));
        }

        Assert.True(peak > 1f, $"expected an overshoot, peaked at {peak}");
        Assert.Equal(1f, overshooting.Evaluate(1f), Tolerance);
    }

    [Theory]
    [InlineData(0.0f, 0.00f)]
    [InlineData(0.2f, 0.00f)]
    [InlineData(0.3f, 0.25f)]
    [InlineData(0.6f, 0.50f)]
    [InlineData(0.9f, 0.75f)]
    [InlineData(1.0f, 1.00f)]
    public void Steps_hold_and_jump(float progress, float expected) {
        Assert.Equal(expected, TimingFunction.Step(4, StepPosition.End).Evaluate(progress), Tolerance);
    }

    [Fact]
    public void Steps_at_the_start_jump_immediately() {
        var atStart = TimingFunction.Step(4, StepPosition.Start);

        Assert.Equal(0.25f, atStart.Evaluate(0.01f), Tolerance);
        Assert.Equal(1f, atStart.Evaluate(1f), Tolerance);
    }

    [Fact]
    public void An_underdamped_spring_overshoots_its_target() {
        // What anyone writing `spring()` is after. A settle that never crosses 1 is just a slow
        // ease-out, and if this stopped being true nobody would report it as a bug — they would
        // report that the UI felt lifeless.
        var spring = TimingFunction.Spring(1f, 180f, 12f);
        var peak = 0f;

        for (var i = 0; i <= 400; i++) {
            peak = MathF.Max(peak, spring.Evaluate(i / 400f));
        }

        Assert.True(peak > 1.02f, $"expected an overshoot, peaked at {peak}");
    }

    [Fact]
    public void A_critically_damped_spring_never_overshoots() {
        // Critical damping is the boundary: the fastest approach with no overshoot at all, at
        // damping = 2·sqrt(stiffness·mass). Getting the three-branch split wrong shows up here
        // first, because the critical branch is the one that is easiest to leave out and hardest
        // to notice — it differs from the underdamped one only in the limit.
        var spring = TimingFunction.Spring(1f, 100f, 2f * MathF.Sqrt(100f));

        for (var i = 0; i <= 200; i++) {
            var value = spring.Evaluate(i / 200f);
            Assert.True(value <= 1f + Tolerance, $"overshot to {value} at {i / 200f}");
        }
    }

    [Fact]
    public void An_overdamped_spring_crawls_in_without_oscillating() {
        var spring = TimingFunction.Spring(1f, 100f, 60f);
        var previous = -1f;

        for (var i = 0; i <= 200; i++) {
            var value = spring.Evaluate(i / 200f);

            Assert.True(value >= previous - Tolerance, $"went backwards at {i / 200f}");
            Assert.True(value <= 1f + Tolerance, $"overshot to {value}");
            previous = value;
        }
    }

    [Fact]
    public void The_spring_matches_the_analytic_oscillator_it_claims_to_be() {
        // The oracle. A damped harmonic oscillator released from rest has a closed-form solution,
        // and the one used here is only *a* closed form — it still has to be the right one. So this
        // integrates the differential equation numerically, from the physics rather than from the
        // formula, and checks the two agree.
        //
        // m·x¨ + c·x˙ + k·x = 0, with x(0) = 1 and x˙(0) = 0. Two completely separate routes to
        // the same curve, which is what makes disagreement mean something.
        foreach (var (mass, stiffness, damping) in new[] {
            (1f, 100f, 10f),   // underdamped
            (1f, 100f, 20f),   // critically damped
            (1f, 100f, 45f),   // overdamped
            (2f, 250f, 15f)    // underdamped, heavier
        }) {
            var spring = TimingFunction.Spring(mass, stiffness, damping);
            var duration = spring.SettlingDuration();

            // Fine enough that the integrator's own error is well under the tolerance.
            const int Steps = 200_000;
            var dt = duration / Steps;
            var position = 1f;
            var velocity = 0f;
            var checkpoint = 1;

            for (var i = 1; i <= Steps; i++) {
                var acceleration = ((-stiffness * position) - (damping * velocity)) / mass;
                velocity += acceleration * dt;
                position += velocity * dt;

                if (i * 20 / Steps < checkpoint) {
                    continue;
                }

                var t = (float) i / Steps;
                Assert.Equal(1f - position, spring.Evaluate(t), 5e-3f);
                checkpoint++;
            }
        }
    }

    [Fact]
    public void Stiffness_alone_changes_how_much_a_spring_rings_and_not_how_long_it_takes() {
        // Not what anyone expects, and worth pinning because someone tuning `spring()` will hit it.
        // The envelope of an underdamped oscillation decays at c/2m — the damping coefficient and
        // the mass, with no k in it — so raising stiffness at a fixed damping coefficient makes the
        // spring oscillate *faster* inside an envelope of exactly the same length. It rings more; it
        // does not arrive sooner.
        var slack = TimingFunction.Spring(1f, 40f, 10f);
        var stiff = TimingFunction.Spring(1f, 400f, 10f);

        Assert.Equal(slack.SettlingDuration(), stiff.SettlingDuration(), Tolerance);

        Assert.True(Crossings(stiff) > Crossings(slack), "a stiffer spring at the same damping should ring more");
    }

    [Fact]
    public void A_stiffer_spring_settles_sooner_when_the_damping_ratio_is_held() {
        // The intuition people actually have, stated in the variable that carries it. The damping
        // *ratio* ζ = c / 2√(km) is what "how bouncy" means; hold it and stiffness becomes speed,
        // because the envelope decays at ζ·√(k/m) and the k is finally in there.
        var slack = AtRatio(stiffness: 40f, ratio: 0.5f);
        var stiff = AtRatio(stiffness: 400f, ratio: 0.5f);

        Assert.True(
            stiff.SettlingDuration() < slack.SettlingDuration(),
            $"stiff {stiff.SettlingDuration()} vs slack {slack.SettlingDuration()}"
        );

        static TimingFunction AtRatio(float stiffness, float ratio) =>
            TimingFunction.Spring(1f, stiffness, ratio * 2f * MathF.Sqrt(stiffness));
    }

    static int Crossings(TimingFunction spring) {
        var count = 0;
        var above = false;

        for (var i = 0; i <= 2000; i++) {
            var nowAbove = spring.Evaluate(i / 2000f) > 1f;
            if (nowAbove != above) {
                count++;
            }

            above = nowAbove;
        }

        return count;
    }

    [Fact]
    public void An_undamped_spring_is_given_a_duration_rather_than_being_left_to_ring_forever() {
        // `spring(1, 100, 0)` is legal to write and never settles. Refusing to produce a duration
        // would leave a transition running for the life of the process.
        Assert.True(TimingFunction.Spring(1f, 100f, 0f).SettlingDuration() > 0f);
    }

    static float Cubic(float t, float a, float b) {
        var inverse = 1f - t;
        return (3f * inverse * inverse * t * a) + (3f * inverse * t * t * b) + (t * t * t);
    }
}

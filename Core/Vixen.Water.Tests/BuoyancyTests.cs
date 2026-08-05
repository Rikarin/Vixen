// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Water;
using Xunit;

namespace Vixen.Water.Tests;

/// <summary>
///     Pontoons on the one surface — [docs/plan/35 § D10], and W7's exit criteria.
/// </summary>
/// <remarks>
///     <para>
///         The headline claim is the convergence one: a body released above the water settles to a
///         rest height matching the <em>analytic</em> displacement of its pontoons. Everything else —
///         the spherical cap, the flow drag, the force cap — is what makes that claim checkable rather
///         than a number somebody tuned until it looked right.
///     </para>
///     <para>
///         ⚠ <b>The forces are measured against the same evaluator the surface is drawn from</b>, which
///         is [§ D2](../../docs/plan/35-water.md#d2-one-evaluator-two-hosts-and-the-seam-is-a-test)'s
///         whole point applied to physics: a boat hovering a hand's width above the crests is what a
///         second definition looks like.
///     </para>
/// </remarks>
public sealed class BuoyancyTests {
    const float Gravity = -9.80665f;
    const float Step = 1f / 60f;

    /// <summary>Open water everywhere at height zero, with no field to bound it.</summary>
    static GerstnerWave[] Still => [];

    // --- The spherical cap ---------------------------------------------------

    [Fact]
    public void A_sphere_is_empty_above_the_water_and_full_below_it() {
        Assert.Equal(0f, Buoyancy.SubmergedFraction(1f, 5f, 0f), 6);
        Assert.Equal(0f, Buoyancy.SubmergedFraction(1f, 1f, 0f), 6);
        Assert.Equal(1f, Buoyancy.SubmergedFraction(1f, -1f, 0f), 6);
        Assert.Equal(1f, Buoyancy.SubmergedFraction(1f, -5f, 0f), 6);

        // Half in, half out — the case a linear ramp on the depth also gets right, and the only one.
        Assert.Equal(0.5f, Buoyancy.SubmergedFraction(1f, 0f, 0f), 6);
    }

    /// <summary>It is monotone in the depth, which the solver has no way back from if it is not.</summary>
    [Fact]
    public void The_submerged_fraction_is_monotone() {
        Gen.Select(Gen.Float[0.05f, 5f], Gen.Float[-20f, 20f])
            .Sample(
                pair => {
                    var (radius, surface) = pair;
                    var previous = -1f;

                    for (var step = 0; step <= 100; step++) {
                        // Sinking the centre from just clear of the water to just under it: the
                        // fraction must only ever rise.
                        var centre = surface + radius - (step / 100f * 2f * radius);
                        var fraction = Buoyancy.SubmergedFraction(radius, centre, surface);

                        Assert.True(fraction >= previous - 1e-5f, $"the fraction went backwards at {centre}.");
                        Assert.InRange(fraction, 0f, 1f);

                        previous = fraction;
                    }
                },
                iter: 200
            );
    }

    /// <summary>Halfway is exactly a half, whatever the radius — the cap's own symmetry.</summary>
    [Fact]
    public void A_sphere_centred_on_the_surface_is_exactly_half_submerged() {
        Gen.Float[0.01f, 50f]
            .Sample(radius => Assert.Equal(0.5f, Buoyancy.SubmergedFraction(radius, 0f, 0f), 5), iter: 200);
    }

    // --- The convergence, which is the exit criterion ------------------------

    /// <summary>
    ///     A body released above the water settles at the analytic displacement of its pontoons.
    /// </summary>
    /// <remarks>
    ///     [§ Part 4]'s buoyancy-convergence row, with both bounds stated: within a centimetre of
    ///     volume, inside five seconds of simulation. The integrator here is a plain semi-implicit
    ///     Euler because that is what a fixed-step physics engine is; if the solver only converged
    ///     under something cleverer, it would not converge in a game.
    /// </remarks>
    [Fact]
    public void A_body_released_above_the_water_settles_at_its_analytic_displacement() {
        BuoyancyPontoon[] pontoons = [
            new(new(-1f, 0f, -0.5f), 0.5f),
            new(new(1f, 0f, -0.5f), 0.5f),
            new(new(-1f, 0f, 0.5f), 0.5f),
            new(new(1f, 0f, 0.5f), 0.5f)
        ];

        const float mass = 260f;

        var settings = BuoyancySettings.Default;
        var evaluator = new WaterEvaluator(null, Still, WaterAttenuation.Default);

        var height = 4f;
        var velocity = Vector3.Zero;
        var forces = new BuoyancyForce[pontoons.Length];

        for (var index = 0; index < 300; index++) {
            var placement = Matrix4x4.FromTranslation(new(0f, height, 0f));

            Buoyancy.Solve(in evaluator, pontoons, in placement, velocity, Gravity, in settings, 0f, forces);

            var total = new Vector3(0f, mass * Gravity, 0f);

            foreach (var force in forces) {
                total += force.Force;
            }

            velocity += total / mass * Step;
            height += velocity.Y * Step;
        }

        // What the arithmetic says it should be displacing at rest, and what it actually is.
        var wanted = Buoyancy.RestDisplacement(pontoons, mass, in settings);
        var settled = 0f;

        foreach (var pontoon in pontoons) {
            settled += pontoon.Volume * Buoyancy.SubmergedFraction(pontoon.Radius, height, 0f);
        }

        Assert.Equal(wanted, settled, 2);
        Assert.True(MathF.Abs(velocity.Y) < 0.01f, $"it was still moving at {velocity.Y} m/s after five seconds.");
    }

    /// <summary>And the negative control: without the damping it never settles.</summary>
    /// <remarks>
    ///     ⚠ A restoring force with no losses is a pendulum, and the test above would pass on the one
    ///     frame the oscillation happened to cross its rest. This is what says the damping is what
    ///     makes it a rest.
    /// </remarks>
    [Fact]
    public void Without_damping_it_never_settles() {
        BuoyancyPontoon[] pontoons = [new(Vector3.Zero, 0.5f)];

        const float mass = 260f;

        var settings = BuoyancySettings.Default with { Damping = 0f, QuadraticDamping = 0f };
        var evaluator = new WaterEvaluator(null, Still, WaterAttenuation.Default);

        var height = 4f;
        var velocity = Vector3.Zero;
        var forces = new BuoyancyForce[1];
        var fastest = 0f;

        for (var index = 0; index < 300; index++) {
            var placement = Matrix4x4.FromTranslation(new(0f, height, 0f));

            Buoyancy.Solve(in evaluator, pontoons, in placement, velocity, Gravity, in settings, 0f, forces);

            velocity += (forces[0].Force + new Vector3(0f, mass * Gravity, 0f)) / mass * Step;
            height += velocity.Y * Step;

            if (index > 240) {
                fastest = MathF.Max(fastest, MathF.Abs(velocity.Y));
            }
        }

        Assert.True(fastest > 0.5f, $"the undamped body settled anyway, at {fastest} m/s — the control proves nothing.");
    }

    // --- The stated properties ----------------------------------------------

    /// <summary>A fully submerged pontoon produces exactly the maximum force and no more.</summary>
    /// <remarks>
    ///     [§ Part 4]'s row. Without the cap a pontoon a metre under produces the force of a metre of
    ///     water, and a crate dropped from a height leaves the lake faster than it arrived.
    /// </remarks>
    [Fact]
    public void The_force_cap_is_exact() {
        var evaluator = new WaterEvaluator(null, Still, WaterAttenuation.Default);
        var settings = BuoyancySettings.Default with { MaximumForce = 500f, Damping = 0f, QuadraticDamping = 0f };
        var pontoon = new BuoyancyPontoon(Vector3.Zero, 1f);

        var shallow = Buoyancy.Evaluate(in evaluator, in pontoon, new(0f, -1f, 0f), Vector3.Zero, Gravity, in settings, 0f);
        var deep = Buoyancy.Evaluate(in evaluator, in pontoon, new(0f, -50f, 0f), Vector3.Zero, Gravity, in settings, 0f);

        Assert.Equal(500f, shallow.Force.Y, 3);
        Assert.Equal(500f, deep.Force.Y, 3);
    }

    /// <summary>A pontoon in the air does nothing at all, drag included.</summary>
    /// <remarks>
    ///     ⚠ A pontoon breaking the surface that kept its full drag would brake a boat in mid-air,
    ///     which reads as a wake that grips.
    /// </remarks>
    [Fact]
    public void A_pontoon_in_the_air_does_nothing() {
        var evaluator = new WaterEvaluator(null, Still, WaterAttenuation.Default);
        var pontoon = new BuoyancyPontoon(Vector3.Zero, 0.5f);

        var force = Buoyancy.Evaluate(
            in evaluator,
            in pontoon,
            new(0f, 12f, 0f),
            new(0f, -30f, 0f),
            Gravity,
            BuoyancySettings.Default,
            0f
        );

        Assert.Equal(Vector3.Zero, force.Force);
        Assert.Equal(0f, force.Submerged);
    }

    /// <summary>A raft in a river reaches the water's own speed and stops there.</summary>
    /// <remarks>
    ///     W7's second exit criterion, in the form the kernel can answer. ⚠ A constant push in the
    ///     flow's direction would accelerate a raft for ever and it would end the river faster than
    ///     the water; a drag towards the flow brings it to the water's speed and leaves it there.
    /// </remarks>
    [Fact]
    public void A_raft_in_a_current_reaches_the_waters_own_speed() {
        var field = new WaterField(new() { Origin = new(-64f, -64f), Extent = 128f, Resolution = 65 });

        var river = new WaterBody(
            WaterBodyKind.River,
            new Spline([
                SplinePoint.Smooth(new(-64f, 0f, 0f), new(64f, 0f, 0f)),
                SplinePoint.Smooth(new(0f, 0f, 0f), new(64f, 0f, 0f)),
                SplinePoint.Smooth(new(64f, 0f, 0f), new(64f, 0f, 0f))
            ]),
            defaults: new() { HalfWidth = 20f, Depth = 4f, Velocity = 3f }
        ) { ShoreFalloff = 4f, BedRamp = 4f };

        field.Rasterize([river], new FlatWaterGround(-4f));

        var evaluator = new WaterEvaluator(field, Still, WaterAttenuation.Default);
        var settings = BuoyancySettings.Default;

        BuoyancyPontoon[] pontoons = [new(Vector3.Zero, 0.6f)];

        const float mass = 400f;

        var position = new Vector3(-20f, -0.2f, 0f);
        var velocity = Vector3.Zero;
        var forces = new BuoyancyForce[1];

        for (var index = 0; index < 1200; index++) {
            var placement = Matrix4x4.FromTranslation(position);

            Buoyancy.Solve(in evaluator, pontoons, in placement, velocity, Gravity, in settings, 0f, forces);

            velocity += (forces[0].Force + new Vector3(0f, mass * Gravity, 0f)) / mass * Step;
            position += velocity * Step;
        }

        // It is going downstream, at about the river's own speed rather than faster.
        Assert.True(velocity.X > 2.5f, $"the raft only reached {velocity.X} m/s in a 3 m/s river.");
        Assert.True(velocity.X < 3.2f, $"the raft overtook the river at {velocity.X} m/s.");
        Assert.True(position.X > -20f, "the raft did not move downstream at all.");
    }

    /// <summary>A body heavier than its pontoons can displace does not float.</summary>
    /// <remarks>
    ///     ⚠ Clamped rather than reported as the volume it <em>would</em> have needed, because a number
    ///     larger than the pontoons hold reads as a body that floats and is a body that sinks.
    /// </remarks>
    [Fact]
    public void A_body_too_heavy_for_its_pontoons_displaces_only_what_it_has() {
        BuoyancyPontoon[] pontoons = [new(Vector3.Zero, 0.25f)];

        var total = pontoons[0].Volume;

        Assert.Equal(total, Buoyancy.RestDisplacement(pontoons, 10_000f, BuoyancySettings.Default), 5);
        Assert.True(Buoyancy.RestDisplacement(pontoons, 10f, BuoyancySettings.Default) < total);
    }

    /// <summary>Ten thousand pontoon evaluations allocate nothing.</summary>
    /// <remarks>
    ///     § D10's "as many floating crates as you like", asserted rather than intended. A solver that
    ///     allocated would allocate once per pontoon per fixed step, which is a collection a second in
    ///     a busy river.
    /// </remarks>
    [Fact]
    public void The_solver_allocates_nothing() {
        var evaluator = new WaterEvaluator(null, Still, WaterAttenuation.Default);
        var pontoon = new BuoyancyPontoon(Vector3.Zero, 0.5f);
        var settings = BuoyancySettings.Default;

        // Warm up, so the measurement is the loop rather than the first call's statics.
        for (var index = 0; index < 100; index++) {
            _ = Buoyancy.Evaluate(in evaluator, in pontoon, new(0f, -0.1f, 0f), Vector3.One, Gravity, in settings, 0f);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 10_000; index++) {
            _ = Buoyancy.Evaluate(in evaluator, in pontoon, new(0f, -0.1f, 0f), Vector3.One, Gravity, in settings, index * Step);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}

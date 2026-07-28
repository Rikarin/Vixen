// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Vfx;
using Xunit;

namespace Tests;

/// <summary>
///     Force fields: the updaters whose value depends on <em>where</em> a particle is — doc 06
///     § VFX pipeline.
/// </summary>
/// <remarks>
///     Gravity is the same everywhere and needs to know nothing. An attractor, a vortex and a
///     turbulence field each read the position, which is what makes them fields and what makes
///     <c>Compile</c> refuse a graph that never places its particles: a field acting on a thousand
///     particles all at the origin accelerates every one of them identically, which is gravity
///     spelled expensively.
/// </remarks>
public class VfxForceTests {
    /// <summary>A graph that scatters particles and then does one thing to them.</summary>
    static VfxSystem Scattered(VfxOperation force, int count = 256, uint seed = 7) {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(count)],
            [
                new(VfxOpcode.PositionInBox, new Vector4(-4f, -4f, -4f, 0f)) { B = new(4f, 4f, 4f, 0f) },
                new(VfxOpcode.SetVelocity, Vector4.Zero),
                new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))
            ],
            [force],
            1024
        );

        var system = new VfxSystem(graph, seed);

        // Twice. `Step` updates before it spawns, so after one step the particles exist and no
        // updater has seen them — which is the rule that stops a particle being aged on the step it
        // was born, and which made the first draft of every test here pass against a field that did
        // nothing at all.
        system.Step(1f / 60f);
        system.Step(1f / 60f);

        return system;
    }

    // --- Attraction --------------------------------------------------------

    /// <summary>Every particle ends up moving towards the point, and none moves away from it.</summary>
    [Fact]
    public void An_attractor_pulls_every_particle_towards_it() {
        using var system = Scattered(new(VfxOpcode.Attract, new Vector4(0f, 0f, 0f, 10f)));

        var positions = system.Particles.Position;
        var velocities = system.Particles.Velocity;

        for (var index = 0; index < system.Count; index++) {
            // The dot of the velocity with the direction of the centre is positive exactly when the
            // particle is now heading that way, whatever the geometry of where it started.
            Assert.True(Vector3.Dot(velocities[index], Vector3.Normalize(-positions[index])) > 0f);
        }
    }

    /// <summary>A negative strength is a repulsor, which is the same field read the other way.</summary>
    [Fact]
    public void A_negative_strength_pushes_instead() {
        using var system = Scattered(new(VfxOpcode.Attract, new Vector4(0f, 0f, 0f, -10f)));

        var positions = system.Particles.Position;
        var velocities = system.Particles.Velocity;

        for (var index = 0; index < system.Count; index++) {
            Assert.True(Vector3.Dot(velocities[index], Vector3.Normalize(positions[index])) > 0f);
        }
    }

    /// <summary>
    ///     Outside the radius nothing happens at all, which is what makes a field a region rather
    ///     than a law.
    /// </summary>
    /// <remarks>
    ///     The alternative — inverse-square, unbounded — has a strength that goes to infinity at the
    ///     centre, so a particle that wanders close enough leaves the scene in one step. An effect
    ///     wants a region it can reason about.
    /// </remarks>
    [Fact]
    public void Beyond_the_radius_a_field_does_nothing() {
        using var system = Scattered(new(VfxOpcode.Attract, new Vector4(0f, 0f, 0f, 50f)) { B = new(2f, 0f, 0f, 0f) });

        var positions = system.Particles.Position;
        var velocities = system.Particles.Velocity;
        var outside = 0;

        for (var index = 0; index < system.Count; index++) {
            if (positions[index].Length() < 2f) {
                continue;
            }

            outside++;
            Assert.Equal(Vector3.Zero, velocities[index]);
        }

        // A box of side eight around a sphere of radius two: most of the particles are outside it,
        // and a test that asserted nothing about zero particles would pass on a broken falloff.
        Assert.True(outside > 100, $"Only {outside} particles were outside the radius.");
    }

    /// <summary>A particle exactly on the centre has no direction to be pulled in, and stays finite.</summary>
    /// <remarks>
    ///     One NaN in a position is a quad the rasteriser silently drops and a bounding box that
    ///     swallows the scene, so the guard is worth its own test rather than a comment.
    /// </remarks>
    [Fact]
    public void A_particle_on_the_centre_does_not_become_nan() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(4)],
            [new(VfxOpcode.SetPosition, Vector4.Zero), new(VfxOpcode.SetVelocity, Vector4.Zero)],
            [new(VfxOpcode.Attract, new Vector4(0f, 0f, 0f, 10f)), new(VfxOpcode.Integrate)],
            16
        );

        using var system = new VfxSystem(graph);

        for (var step = 0; step < 10; step++) {
            system.Step(1f / 60f);
        }

        foreach (var position in system.Particles.Position[..system.Count]) {
            Assert.True(float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z));
        }
    }

    // --- Vorticity ---------------------------------------------------------

    /// <summary>
    ///     A vortex turns particles about its axis without pushing them along it or towards it.
    /// </summary>
    [Fact]
    public void A_vortex_adds_no_motion_along_its_axis() {
        using var system = Scattered(
            new(VfxOpcode.Vortex, new Vector4(0f, 0f, 0f, 5f)) { B = new(0f, 1f, 0f, 0f) }
        );

        var positions = system.Particles.Position;
        var velocities = system.Particles.Velocity;

        for (var index = 0; index < system.Count; index++) {
            // A cross product with the axis is perpendicular to it by construction, so this is the
            // arithmetic asserting itself — worth it because the tempting simplification, crossing
            // with the raw offset rather than the radial part, breaks it.
            Assert.Equal(0f, velocities[index].Y, 5);

            // And perpendicular to the radial direction: it goes round, not in or out.
            var radial = new Vector3(positions[index].X, 0f, positions[index].Z);

            if (radial.Length() > 0.01f) {
                Assert.Equal(0f, Vector3.Dot(velocities[index], Vector3.Normalize(radial)), 4);
            }
        }
    }

    /// <summary>
    ///     Two particles at the same distance from the axis are turned equally hard, whatever their
    ///     height.
    /// </summary>
    /// <remarks>
    ///     This is the one the tempting version gets wrong. Crossing the axis with the whole offset
    ///     rather than its radial part gives a swirl that grows with height above the centre, which
    ///     reads as a vortex that leans.
    /// </remarks>
    [Fact]
    public void A_vortex_does_not_care_how_high_a_particle_is() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(2)],
            [new(VfxOpcode.SetVelocity, Vector4.Zero), new(VfxOpcode.SetPosition, Vector4.Zero)],
            [new(VfxOpcode.Vortex, new Vector4(0f, 0f, 0f, 5f)) { B = new(0f, 1f, 0f, 0f) }],
            8
        );

        using var system = new VfxSystem(graph);
        system.Step(1f / 60f);

        // Placed by hand: the initializers put both at the origin, and what is being compared is two
        // heights at one radius.
        var positions = system.Particles.Position;
        positions[0] = new(2f, 0f, 0f);
        positions[1] = new(2f, 9f, 0f);
        system.Particles.Velocity.Clear();

        system.Step(1f / 60f);

        Assert.Equal(system.Particles.Velocity[0].Length(), system.Particles.Velocity[1].Length(), 5);
    }

    // --- Curl noise --------------------------------------------------------

    /// <summary>
    ///     The curl of any field has zero divergence, and that is the whole reason for taking one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Sampling noise straight into a velocity gives a field with sources and sinks: particles
    ///         pile up wherever it points inward and thin out where it points out. No fluid does that,
    ///         and the eye knows. A curl cannot, identically — so this is the property to check, and
    ///         it is checked as a numerical divergence rather than trusted from the algebra, because
    ///         the algebra is exact and the finite differences are not.
    ///     </para>
    ///     <para>
    ///         The tolerance is loose on purpose: the differences are taken at one epsilon and the
    ///         divergence is measured at another, so what is being asserted is "small compared to the
    ///         field" rather than "zero".
    /// </para>
    /// </remarks>
    [Fact]
    public void Curl_noise_has_almost_no_divergence() {
        const float Step = 0.01f;

        var worst = 0f;
        var magnitude = 0f;

        for (var i = 0; i < 200; i++) {
            var point = new Vector3(i * 0.37f, i * -0.19f, i * 0.61f);

            var divergence =
                (VfxNoise.Curl(point + new Vector3(Step, 0f, 0f), 3).X - VfxNoise.Curl(point - new Vector3(Step, 0f, 0f), 3).X)
                + (VfxNoise.Curl(point + new Vector3(0f, Step, 0f), 3).Y - VfxNoise.Curl(point - new Vector3(0f, Step, 0f), 3).Y)
                + (VfxNoise.Curl(point + new Vector3(0f, 0f, Step), 3).Z - VfxNoise.Curl(point - new Vector3(0f, 0f, Step), 3).Z);

            worst = MathF.Max(worst, MathF.Abs(divergence / (2f * Step)));
            magnitude = MathF.Max(magnitude, VfxNoise.Curl(point, 3).Length());
        }

        Assert.True(magnitude > 0.1f, $"The field is flat ({magnitude}), so the divergence proves nothing.");
        Assert.True(worst < magnitude, $"Divergence {worst} is not small against a field of {magnitude}.");
    }

    /// <summary>The noise is smooth: two nearby points give nearly the same value.</summary>
    /// <remarks>
    ///     What this really tests is the smoothstep. Interpolating the lattice linearly gives a value
    ///     that is continuous and a <i>derivative</i> that is not, and the crease at every cell
    ///     boundary is visible in the motion long before it is visible in the noise.
    /// </remarks>
    [Fact]
    public void The_noise_is_smooth_across_a_cell_boundary() {
        var worst = 0f;

        // Straddling an integer coordinate, which is exactly where a linear interpolant creases.
        for (var i = -20; i <= 20; i++) {
            var x = 3f + (i * 0.005f);
            var here = VfxNoise.Value(new(x, 0.5f, 0.5f), 1);
            var next = VfxNoise.Value(new(x + 0.005f, 0.5f, 0.5f), 1);

            worst = MathF.Max(worst, MathF.Abs(next - here));
        }

        Assert.True(worst < 0.02f, $"The field jumps by {worst} over a hundredth of a cell.");
    }

    /// <summary>Turbulence pushes particles about, and identically for the same seed.</summary>
    [Fact]
    public void Turbulence_moves_particles_and_stays_reproducible() {
        var force = new VfxOperation(VfxOpcode.Turbulence, new Vector4(0.5f, 0.5f, 0.5f, 20f)) { B = new(1f, 3f, 0f, 0f) };

        using var one = Scattered(force);
        using var other = Scattered(force);

        var moved = 0;

        for (var index = 0; index < one.Count; index++) {
            Assert.Equal(one.Particles.Velocity[index], other.Particles.Velocity[index]);

            if (one.Particles.Velocity[index].LengthSquared() > 0f) {
                moved++;
            }
        }

        Assert.Equal(one.Count, moved);
    }

    /// <summary>
    ///     Two turbulence operations in one graph are two different winds, not the same one twice.
    /// </summary>
    /// <remarks>
    ///     They get distinct salts from <c>Compile</c> for the same reason two random initializers do,
    ///     which is why the salt rule had to grow: a noise field needs a salt without hashing the
    ///     particle's identifier, and those had been one question.
    /// </remarks>
    [Fact]
    public void Two_turbulence_fields_are_not_the_same_field() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [new(VfxOpcode.SetPosition, Vector4.Zero), new(VfxOpcode.SetVelocity, Vector4.Zero)],
            [
                new(VfxOpcode.Turbulence, new Vector4(1f, 1f, 1f, 1f)) { B = new(0f, 1f, 0f, 0f) },
                new(VfxOpcode.Turbulence, new Vector4(1f, 1f, 1f, 1f)) { B = new(0f, 1f, 0f, 0f) }
            ],
            8
        );

        Assert.NotEqual(graph.Updaters[0].Salt, graph.Updaters[1].Salt);
        Assert.NotEqual(0u, graph.Updaters[0].Salt);
    }

    /// <summary>The field drifts, so a particle standing still is still pushed differently over time.</summary>
    [Fact]
    public void A_drifting_field_changes_with_the_clock() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [
                new(VfxOpcode.SetPosition, new Vector4(1f, 2f, 3f, 0f)),
                new(VfxOpcode.SetVelocity, Vector4.Zero),
                new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))
            ],
            [new(VfxOpcode.Turbulence, new Vector4(1f, 1f, 1f, 10f)) { B = new(4f, 2f, 0f, 0f) }],
            8
        );

        using var system = new VfxSystem(graph);
        system.Step(1f / 60f);

        var first = system.Particles.Velocity[0];

        // Held in place, so the only thing that changed between the two samples is the clock.
        system.Particles.Velocity[0] = Vector3.Zero;

        for (var step = 0; step < 60; step++) {
            system.Step(1f / 60f);
            system.Particles.Position[0] = new(1f, 2f, 3f);
        }

        Assert.NotEqual(first, system.Particles.Velocity[0]);
    }

    /// <summary>
    ///     A field reads position, so a graph that never places its particles is refused.
    /// </summary>
    [Fact]
    public void A_field_with_nothing_to_act_on_is_refused() {
        var error = Assert.Throws<ArgumentException>(
            () => VfxCompiledGraph.Compile(
                [VfxSpawner.Burst(1)],
                [new(VfxOpcode.SetVelocity, Vector4.Zero)],
                [new(VfxOpcode.Vortex, new Vector4(0f, 0f, 0f, 1f)) { B = new(0f, 1f, 0f, 0f) }],
                8
            )
        );

        Assert.Contains("Position", error.Message, StringComparison.Ordinal);
    }
}

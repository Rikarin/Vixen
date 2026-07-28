// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Vfx;
using Xunit;

namespace Tests;

/// <summary>
///     Particles that emit particles: sub-emitters and trails — doc 06 § VFX pipeline.
/// </summary>
/// <remarks>
///     The interesting claims are about <i>when</i> a child appears and <i>where</i>, because those
///     are the two an author can see and the two nothing else in the module checks. A trail that
///     starts an interval late leaves a visible gap behind its parent; a burst that appears at the
///     origin instead of at the shell is a firework going off in the wrong place.
/// </remarks>
public class VfxSubEmitterTests {
    const float Dt = 1f / 60f;

    /// <summary>A parent that lives for exactly two steps and then dies where it was put.</summary>
    static VfxSystem Shell(Vector3 where, Vector3 velocity, float lifetime) =>
        new(
            VfxCompiledGraph.Compile(
                [VfxSpawner.Burst(1)],
                [
                    new(VfxOpcode.SetPosition, new Vector4(where, 0f)),
                    new(VfxOpcode.SetVelocity, new Vector4(velocity, 0f)),
                    new(VfxOpcode.SetLifetime, new Vector4(lifetime, lifetime, 0f, 0f))
                ],
                [],
                4
            )
        );

    /// <summary>Children that are born at the origin, so that any offset is the parent's.</summary>
    static VfxSystem Sparks(int capacity = 64) =>
        new(
            VfxCompiledGraph.Compile(
                [],
                [
                    new(VfxOpcode.SetPosition, Vector4.Zero),
                    new(VfxOpcode.SetVelocity, Vector4.Zero),
                    new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))
                ],
                [],
                capacity
            )
        );

    [Fact]
    public void A_dying_particle_bursts_where_it_died() {
        using var shell = Shell(new(3f, 4f, 5f), Vector3.Zero, Dt * 1.5f);
        using var sparks = Sparks();

        shell.RecordDeaths = true;

        var burst = new VfxSubEmitter(shell, sparks, VfxEmitEvent.Death, 5);

        // It takes three steps, and the reason is the order inside one. A step updates and then
        // spawns, so the shell comes into existence at the end of the first with an age of zero; the
        // second ages it by one step, which is not yet its lifetime; the third takes it past.
        shell.Step(Dt);
        sparks.Step(Dt);
        burst.Step(Dt);

        Assert.Equal(0, burst.LastEmitted);

        shell.Step(Dt);
        sparks.Step(Dt);
        burst.Step(Dt);

        Assert.Equal(0, burst.LastEmitted);

        shell.Step(Dt);
        sparks.Step(Dt);
        burst.Step(Dt);

        Assert.Equal(5, burst.LastEmitted);
        Assert.Equal(5, sparks.Count);
        Assert.All(
            sparks.Particles.Position[..5].ToArray(),
            position => Assert.Equal(new Vector3(3f, 4f, 5f), position)
        );
    }

    /// <summary>
    ///     Without the recording switched on there is nothing to burst from, and the sub-emitter says
    ///     so by emitting nothing rather than by guessing.
    /// </summary>
    [Fact]
    public void A_death_burst_needs_the_deaths_to_have_been_recorded() {
        using var shell = Shell(new(3f, 4f, 5f), Vector3.Zero, Dt * 1.5f);
        using var sparks = Sparks();

        var burst = new VfxSubEmitter(shell, sparks, VfxEmitEvent.Death, 5);

        for (var step = 0; step < 4; step++) {
            shell.Step(Dt);
            sparks.Step(Dt);
            burst.Step(Dt);
        }

        Assert.Equal(0, shell.Count);
        Assert.Equal(0, sparks.Count);
    }

    [Fact]
    public void A_newborn_particle_emits_at_once_when_the_trigger_is_birth() {
        using var shell = Shell(new(1f, 0f, 0f), Vector3.Zero, 100f);
        using var sparks = Sparks();

        var burst = new VfxSubEmitter(shell, sparks, VfxEmitEvent.Birth, 3);

        shell.Step(Dt);
        sparks.Step(Dt);
        burst.Step(Dt);

        Assert.Equal(3, burst.LastEmitted);
        Assert.All(
            sparks.Particles.Position[..3].ToArray(),
            position => Assert.Equal(new Vector3(1f, 0f, 0f), position)
        );

        // And only once: the second step spawns nothing, so there is nothing born to emit from.
        shell.Step(Dt);
        sparks.Step(Dt);
        burst.Step(Dt);

        Assert.Equal(0, burst.LastEmitted);
    }

    /// <summary>
    ///     One child per interval per particle, counted off the parent's own age.
    /// </summary>
    /// <remarks>
    ///     Sixty-five steps of a sixtieth of a second is an age of about 1.07 seconds, which crosses
    ///     ten tenth-of-a-second boundaries; the first child is shed at birth, so eleven. The
    ///     duration is deliberately not a whole number of intervals: an age accumulated by repeated
    ///     addition lands a hair either side of the boundary it is nominally on, so a test that
    ///     stopped exactly at 1.0 second would be asserting which side of it a float sum came down —
    ///     and this one did, before it was measured.
    /// </remarks>
    [Fact]
    public void A_trail_sheds_one_child_every_interval_starting_at_birth() {
        using var shell = Shell(Vector3.Zero, Vector3.Zero, 100f);
        using var sparks = Sparks(256);

        var trail = new VfxSubEmitter(shell, sparks, VfxEmitEvent.Trail, 1, 0.1f);
        var emitted = 0;

        for (var step = 0; step < 65; step++) {
            shell.Step(Dt);
            sparks.Step(Dt);
            trail.Step(Dt);

            emitted += trail.LastEmitted;
        }

        Assert.Equal(11, emitted);
    }

    /// <summary>An interval of zero or less is a child every step, which is the densest trail there is.</summary>
    [Fact]
    public void A_trail_with_no_interval_sheds_every_step() {
        using var shell = Shell(Vector3.Zero, Vector3.Zero, 100f);
        using var sparks = Sparks(256);

        var trail = new VfxSubEmitter(shell, sparks, VfxEmitEvent.Trail, 1, 0f);

        for (var step = 0; step < 10; step++) {
            shell.Step(Dt);
            sparks.Step(Dt);
            trail.Step(Dt);
        }

        // All ten: the parent is spawned by the first step's own emission, which happens before the
        // sub-emitter runs, so there is a particle to shed from on every iteration including the
        // first.
        Assert.Equal(10, sparks.Count);
    }

    /// <summary>The child's own initializers place it relative to its parent, not instead of it.</summary>
    [Fact]
    public void A_childs_initializers_are_an_offset_from_its_parent() {
        using var shell = Shell(new(10f, 0f, 0f), Vector3.Zero, 100f);

        using var sparks = new VfxSystem(
            VfxCompiledGraph.Compile(
                [],
                [
                    new(VfxOpcode.SetPosition, new Vector4(0f, 2f, 0f, 0f)),
                    new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))
                ],
                [],
                8
            )
        );

        var burst = new VfxSubEmitter(shell, sparks, VfxEmitEvent.Birth, 1);

        shell.Step(Dt);
        sparks.Step(Dt);
        burst.Step(Dt);

        Assert.Equal(new Vector3(10f, 2f, 0f), sparks.Particles.Position[0]);
    }

    [Fact]
    public void An_inherited_velocity_is_added_to_the_childs_own() {
        using var shell = Shell(Vector3.Zero, new(0f, 20f, 0f), 100f);
        using var sparks = Sparks();

        var burst = new VfxSubEmitter(shell, sparks, VfxEmitEvent.Birth, 1) { InheritVelocity = 0.5f };

        shell.Step(Dt);
        sparks.Step(Dt);
        burst.Step(Dt);

        Assert.Equal(new Vector3(0f, 10f, 0f), sparks.Particles.Velocity[0]);
    }

    /// <summary>A full target refuses and reports, exactly as a full system does.</summary>
    [Fact]
    public void What_the_target_cannot_hold_is_reported() {
        using var shell = Shell(Vector3.Zero, Vector3.Zero, 100f);
        using var sparks = Sparks(2);

        var burst = new VfxSubEmitter(shell, sparks, VfxEmitEvent.Birth, 5);

        shell.Step(Dt);
        sparks.Step(Dt);
        burst.Step(Dt);

        Assert.Equal(2, burst.LastEmitted);
        Assert.Equal(3, burst.LastRefused);
    }

    /// <summary>
    ///     A system emitting into itself would be walking the array it is appending to.
    /// </summary>
    [Fact]
    public void A_system_cannot_be_its_own_target() {
        using var system = Shell(Vector3.Zero, Vector3.Zero, 100f);

        Assert.Throws<ArgumentException>(() => new VfxSubEmitter(system, system));
    }

    /// <summary>Two runs of the same steps produce the same children, down to their identifiers.</summary>
    /// <remarks>
    ///     The property the whole module is arranged around, extended to a thing that spawns from
    ///     inside a step rather than from a spawner. A child's randomness comes from the identifier
    ///     the target hands it, and the target hands them out in order.
    /// </remarks>
    [Fact]
    public void The_same_steps_produce_the_same_children() {
        static uint[] Run() {
            using var shell = Shell(new(1f, 2f, 3f), Vector3.Zero, 100f);
            using var sparks = Sparks(256);

            var trail = new VfxSubEmitter(shell, sparks, VfxEmitEvent.Trail, 2, 0.05f);

            for (var step = 0; step < 30; step++) {
                shell.Step(Dt);
                sparks.Step(Dt);
                trail.Step(Dt);
            }

            return sparks.Particles.Identifier[..sparks.Count].ToArray();
        }

        Assert.Equal(Run(), Run());
    }
}

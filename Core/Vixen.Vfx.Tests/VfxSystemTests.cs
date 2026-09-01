// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Testing;
using Vixen.Vfx;
using Xunit;

namespace Vixen.Vfx.Tests;

/// <summary>
///     A running effect: what it spawns, what happens to it, and what it costs.
/// </summary>
public sealed class VfxSystemTests {
    /// <summary>A plain fountain: a steady stream, thrown upwards, pulled down, with a finite life.</summary>
    static VfxCompiledGraph Fountain(int capacity = 256) => VfxCompiledGraph.Compile(
        [VfxSpawner.AtRate(60f)],
        [
            new(VfxOpcode.SetPosition, new Vector4(0f, 0f, 0f, 0f)),
            new(VfxOpcode.VelocityRandomDirection, new Vector4(2f, 4f, 0f, 0f)),
            new(VfxOpcode.SetLifetime, new Vector4(1f, 2f, 0f, 0f)),
            new(VfxOpcode.SetSize, new Vector4(0.1f, 0.2f, 0f, 0f))
        ],
        [
            new(VfxOpcode.Gravity, new Vector4(0f, -9.81f, 0f, 0f)),
            new(VfxOpcode.Integrate)
        ],
        capacity
    );

    /// <summary>Steps a system for a while.</summary>
    /// <remarks>
    ///     Rounded rather than truncated: <c>1f / (1f / 60f)</c> is 59.999996, and truncating it runs
    ///     a "one second" for fifty-nine frames — which is a whole frame of drift hidden inside a test
    ///     helper, and exactly the kind of thing that makes a timing assertion mysteriously loose.
    /// </remarks>
    static void Run(VfxSystem system, float seconds, float step = 1f / 60f) {
        for (var frame = 0; frame < (int)MathF.Round(seconds / step); frame++) {
            system.Step(step);
        }
    }

    [Fact]
    public void ARateSpawnerEmitsWhatItPromised() {
        using var system = new VfxSystem(Fountain(1024));

        Run(system, 1f);

        // Sixty a second for a second, less whatever has died — nothing has, because the shortest
        // lifetime is a second and the first particle was born a frame in.
        Assert.InRange(system.Count, 58, 60);
    }

    [Fact]
    public void AFractionalRateStillEmits() {
        // Two and a half a second at sixty hertz is 0.0417 particles a frame. Dropping the fraction
        // each frame would emit nothing at all, for ever, which is how a low-rate emitter goes
        // mysteriously silent.
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.AtRate(2.5f)],
            [new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))],
            [],
            64
        );

        using var system = new VfxSystem(graph);

        Run(system, 4f);

        Assert.InRange(system.Count, 9, 11);
    }

    [Fact]
    public void ABurstHappensOnceAndAtItsTime() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(50, 0.5f)],
            [new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))],
            [],
            256
        );

        using var system = new VfxSystem(graph);

        Run(system, 0.4f);
        Assert.Equal(0, system.Count);

        Run(system, 0.2f);
        Assert.Equal(50, system.Count);

        Run(system, 2f);
        Assert.Equal(50, system.Count);
    }

    [Fact]
    public void ALongStepDoesNotSwallowRepeatedBursts() {
        // A step longer than the interval covers several bursts. Testing "does one fall inside this
        // step" would emit one, and an effect would lose its shape the first time a frame hitched.
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Repeating(10, 0.1f)],
            [new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))],
            [],
            256
        );

        using var system = new VfxSystem(graph);

        system.Step(0.55f);

        Assert.Equal(60, system.Count);
    }

    [Fact]
    public void ParticlesDieWhenTheirLifetimeIsUp() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(20)],
            [new(VfxOpcode.SetLifetime, new Vector4(0.5f, 0.5f, 0f, 0f))],
            [],
            64
        );

        using var system = new VfxSystem(graph);

        Run(system, 0.3f);
        Assert.Equal(20, system.Count);

        Run(system, 0.4f);
        Assert.Equal(0, system.Count);
    }

    [Fact]
    public void AFullBufferRefusesRatherThanGrows() {
        using var system = new VfxSystem(Fountain(16));

        Run(system, 1f);

        Assert.Equal(16, system.Count);
        Assert.True(system.LastRefused > 0, "A system at capacity should say it turned particles away.");
    }

    [Fact]
    public void GravityPullsAndIntegrationMoves() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [
                new(VfxOpcode.SetPosition, Vector4.Zero),
                new(VfxOpcode.SetVelocity, Vector4.Zero),
                new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))
            ],
            [
                new(VfxOpcode.Gravity, new Vector4(0f, -10f, 0f, 0f)),
                new(VfxOpcode.Integrate)
            ],
            8
        );

        using var system = new VfxSystem(graph);

        // Sixty-one steps for sixty steps of falling. A particle is born at the *end* of the step
        // that spawns it — updating before emitting is what stops a particle being aged on the step
        // it appears — so it is integrated on each of the sixty steps that follow and not on its own.
        for (var frame = 0; frame < 61; frame++) {
            system.Step(1f / 60f);
        }

        // Semi-implicit Euler: the velocity is exact, and the position trails the analytic -5 by
        // half a step's worth of it, which is what this integrator is rather than an error in it.
        Assert.Equal(-10f, system.Particles.Velocity[0].Y, 0.001f);
        Assert.Equal(-5.0833f, system.Particles.Position[0].Y, 0.001f);
    }

    [Fact]
    public void SizeFollowsAge() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(1)],
            [new(VfxOpcode.SetLifetime, new Vector4(1f, 1f, 0f, 0f))],
            [new(VfxOpcode.SizeOverLife, new Vector4(0f, 1f, 0f, 0f))],
            8
        );

        using var system = new VfxSystem(graph);

        Run(system, 0.5f);

        Assert.Equal(0.5f, system.Particles.Size[0], 0.05f);
    }

    [Fact]
    public void TwoSystemsWithOneSeedAreIdenticalParticleForParticle() {
        using var first = new VfxSystem(Fountain(), 12345);
        using var second = new VfxSystem(Fountain(), 12345);

        Run(first, 2f);
        Run(second, 2f);

        Assert.Equal(first.Count, second.Count);

        for (var index = 0; index < first.Count; index++) {
            Assert.Equal(first.Particles.Position[index], second.Particles.Position[index]);
            Assert.Equal(first.Particles.Velocity[index], second.Particles.Velocity[index]);
            Assert.Equal(first.Particles.Lifetime[index], second.Particles.Lifetime[index]);
        }
    }

    /// <summary>
    ///     The origin moves where particles are born, and one graph serves two emitters.
    /// </summary>
    /// <remarks>
    ///     <b>What makes an effect an asset rather than a place.</b> Every opcode that writes a
    ///     position writes a world-space one, so without an origin a graph is nailed to the
    ///     coordinates its author typed and a second emitter is a second graph. The two systems here
    ///     share one graph and one seed, so every particle is the same particle — displaced by exactly
    ///     the offset between the emitters, and by nothing else.
    /// </remarks>
    [Fact]
    public void TheOriginMovesWhereParticlesAreBorn() {
        var graph = Fountain();
        var offset = new Vector3(10f, -3f, 4f);

        using var here = new VfxSystem(graph, 7);
        using var there = new VfxSystem(graph, 7) { Origin = offset };

        Run(here, 0.5f);
        Run(there, 0.5f);

        Assert.Equal(here.Count, there.Count);
        Assert.True(here.Count > 0, "the fountain spawned nothing, so the comparison is vacuous");

        for (var index = 0; index < here.Count; index++) {
            // The offset and nothing else: the origin is added once, at spawn, so the gravity and the
            // integration that ran afterwards moved both particles identically.
            //
            // ⚠ To four places rather than exactly, and the reason is float arithmetic rather than
            // slack in the claim. The displaced particle accumulated a hundred integration steps
            // starting from ten metres out; adding the offset to the other one afterwards accumulates
            // them starting from zero, and the two orders differ in the last bit of a float — which is
            // what an exact comparison here was measuring.
            Assert.Equal(0f, (here.Particles.Position[index] + offset - there.Particles.Position[index]).Length(), 4);

            // And nothing else moved. An origin added to a velocity would be adding a length to a
            // rate, and one added in the update would drag every live particle along every frame.
            Assert.Equal(here.Particles.Velocity[index], there.Particles.Velocity[index]);
        }
    }

    /// <summary>
    ///     Moving the origin leaves the particles already alive where they are.
    /// </summary>
    /// <remarks>
    ///     A torch carried across a room leaves its smoke behind rather than dragging it, which is
    ///     what smoke does — and it is the whole reason the origin is read at spawn rather than
    ///     applied per frame. An effect that should follow its emitter wants a transform at draw time,
    ///     which nothing in this module has.
    /// </remarks>
    [Fact]
    public void MovingTheOriginLeavesTheLiveParticlesAlone() {
        using var system = new VfxSystem(Fountain(), 3);

        Run(system, 0.2f);

        var settled = system.Particles.Position[..system.Count].ToArray();

        Assert.NotEmpty(settled);

        system.Origin = new(100f, 0f, 0f);
        system.Emitting = false;
        system.Step(1f / 60f);

        // Nothing was born, so nothing moved by the origin — what movement there is is the fountain's
        // own, and it is the same movement it would have made had the origin never changed.
        for (var index = 0; index < settled.Length; index++) {
            Assert.True(system.Particles.Position[index].X < 50f, "a live particle was dragged by the origin");
        }
    }

    [Fact]
    public void TwoSystemsWithDifferentSeedsAreNot() {
        using var first = new VfxSystem(Fountain(), 1);
        using var second = new VfxSystem(Fountain(), 2);

        Run(first, 1f);
        Run(second, 1f);

        var same = 0;

        for (var index = 0; index < Math.Min(first.Count, second.Count); index++) {
            if (first.Particles.Velocity[index] == second.Particles.Velocity[index]) {
                same++;
            }
        }

        Assert.Equal(0, same);
    }

    [Fact]
    public void RecyclingASlotDoesNotRerollTheParticleInIt() {
        // A particle's randomness follows its identifier, not its slot. If it followed the slot, a
        // particle would silently change size and lifetime the moment something ahead of it died.
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.AtRate(120f)],
            [new(VfxOpcode.SetLifetime, new Vector4(0.05f, 0.4f, 0f, 0f))],
            [],
            256
        );

        using var system = new VfxSystem(graph);

        Run(system, 0.2f);

        var identifiers = new Dictionary<uint, float>();

        for (var frame = 0; frame < 60; frame++) {
            system.Step(1f / 60f);

            for (var index = 0; index < system.Count; index++) {
                var identifier = system.Particles.Identifier[index];
                var lifetime = system.Particles.Lifetime[index];

                if (identifiers.TryGetValue(identifier, out var previous)) {
                    Assert.Equal(previous, lifetime);
                } else {
                    identifiers[identifier] = lifetime;
                }
            }
        }

        Assert.True(identifiers.Count > 50, $"Only {identifiers.Count} particles were seen, which is not enough recycling to prove anything.");
    }

    [Fact]
    public void StoppingEmissionLetsTheLastParticlesFinish() {
        using var system = new VfxSystem(Fountain());

        Run(system, 0.5f);
        var alive = system.Count;

        system.Emitting = false;
        system.Step(1f / 60f);

        Assert.Equal(alive, system.Count);
        Assert.Equal(0, system.LastSpawned);

        Run(system, 3f);
        Assert.Equal(0, system.Count);
    }

    [Fact]
    public void ResettingPutsTheClockBack() {
        using var system = new VfxSystem(Fountain());

        Run(system, 1f);
        Assert.True(system.Count > 0);

        system.Reset();

        Assert.Equal(0, system.Count);
        Assert.Equal(0f, system.Time);
    }

    [Fact]
    public void SteppingAllocatesNothing() {
        using var system = new VfxSystem(Fountain(1024));

        // Warmed until the buffer is at its working population, so what is measured is a frame of a
        // running effect rather than one of a starting one: two seconds of warm-up at a sixtieth
        // each, then five seconds measured.
        Measured.NothingAllocated(
            Frame,
            warmUp: 120,
            passes: 300,
            because: "Particle storage is native and the graph is read-only, so the only right answer is none."
        );

        return;

        void Frame() => system.Step(1f / 60f);
    }

    /// <summary>And a frame with force fields in it, which is the most arithmetic per particle.</summary>
    /// <remarks>
    ///     Curl noise samples the field eighteen times per octave per particle and builds a vector
    ///     for each. Every one of them is a struct on the stack, which is a thing that is true until
    ///     somebody makes one of the helpers return a tuple or take a lambda — so it is pinned here
    ///     rather than assumed from the shape of the code.
    /// </remarks>
    [Fact]
    public void SteppingWithForceFieldsAllocatesNothing() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.AtRate(240f)],
            [
                new(VfxOpcode.PositionInSphere, new Vector4(0f, 0f, 0f, 1f)),
                new(VfxOpcode.SetVelocity, Vector4.Zero),
                new(VfxOpcode.SetLifetime, new Vector4(2f, 3f, 0f, 0f))
            ],
            [
                new(VfxOpcode.Attract, new Vector4(0f, 4f, 0f, 3f)) { B = new(6f, 0f, 0f, 0f) },
                new(VfxOpcode.Vortex, new Vector4(0f, 0f, 0f, 2f)) { B = new(0f, 1f, 0f, 8f) },
                new(VfxOpcode.Turbulence, new Vector4(0.3f, 0.3f, 0.3f, 4f)) { B = new(0.7f, 3f, 0f, 0f) },
                new(VfxOpcode.Integrate)
            ],
            2048
        );

        using var system = new VfxSystem(graph);

        Measured.NothingAllocated(
            Frame,
            warmUp: 120,
            passes: 300,
            because: "A field-driven effect reads the field rather than rebuilding it."
        );

        return;

        void Frame() => system.Step(1f / 60f);
    }
}

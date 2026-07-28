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
        var allocated = Measured.Bytes(Frame, warmUp: 120, passes: 300);

        Assert.True(
            allocated == 0,
            $"Three hundred frames of a running effect allocated {allocated} bytes. Particle storage is native and the "
            + "graph is read-only, so the only right answer is none."
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

        var allocated = Measured.Bytes(Frame, warmUp: 120, passes: 300);

        Assert.True(allocated == 0, $"Three hundred frames of a field-driven effect allocated {allocated} bytes.");

        return;

        void Frame() => system.Step(1f / 60f);
    }
}

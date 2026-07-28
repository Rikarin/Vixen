// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Vfx;
using Xunit;

namespace Tests;

/// <summary>
///     The sweeps, across threads.
/// </summary>
/// <remarks>
///     <para>
///         The claim that has to hold is not "it is faster" — it is <b>identical</b>. Two systems with
///         one seed and the same steps are the same particles, and putting the work on four threads
///         must not change that by a bit. It does not, and the reason is worth stating: no operation
///         reads another particle, so the order between particles was never observable and only the
///         order <em>within</em> one had to be kept.
///     </para>
///     <para>
///         That is also the property that makes a batch run the whole updater list rather than one
///         dispatch per operation — which would be six barriers a frame for work that finishes before
///         a barrier does.
///     </para>
/// </remarks>
public class VfxParallelTests : IDisposable {
    readonly JobScheduler scheduler = new();

    /// <summary>A graph heavy enough that threads have something to do.</summary>
    static VfxCompiledGraph Storm(int capacity) =>
        VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(capacity)],
            [
                new(VfxOpcode.PositionInBox, new Vector4(-8f, -8f, -8f, 0f)) { B = new(8f, 8f, 8f, 0f) },
                new(VfxOpcode.VelocityRandomDirection, new Vector4(1f, 3f, 0f, 0f)),
                new(VfxOpcode.SetSize, new Vector4(0.1f, 0.4f, 0f, 0f)),
                new(VfxOpcode.SetColour, Vector4.One),
                new(VfxOpcode.SetLifetime, new Vector4(4f, 8f, 0f, 0f))
            ],
            [
                new(VfxOpcode.Gravity, new Vector4(0f, -9.81f, 0f, 0f)),
                new(VfxOpcode.Drag, new Vector4(0.2f, 0f, 0f, 0f)),
                new(VfxOpcode.Attract, new Vector4(0f, 5f, 0f, 4f)) { B = new(12f, 0f, 0f, 0f) },
                new(VfxOpcode.Vortex, new Vector4(0f, 0f, 0f, 3f)) { B = new(0f, 1f, 0f, 12f) },
                new(VfxOpcode.Turbulence, new Vector4(0.3f, 0.3f, 0.3f, 6f)) { B = new(0.5f, 3f, 0f, 0f) },
                new(VfxOpcode.Integrate),
                new(VfxOpcode.SizeOverLife, new Vector4(0.4f, 0f, 0f, 0f)),
                new(VfxOpcode.ColourOverLife, Vector4.One) { B = new(1f, 0.2f, 0f, 0f) }
            ],
            capacity
        );

    /// <inheritdoc />
    public void Dispose() {
        scheduler.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Across threads and on one, the particles are the same — not close, the same.</summary>
    [Fact]
    public void The_parallel_sweep_is_the_serial_one() {
        const int Count = 8192;

        using var serial = new VfxSystem(Storm(Count), seed: 31);
        using var parallel = new VfxSystem(Storm(Count), seed: 31) { Scheduler = scheduler, ParallelThreshold = 1 };

        for (var step = 0; step < 40; step++) {
            serial.Step(1f / 60f);
            parallel.Step(1f / 60f);
        }

        Assert.True(serial.Count > Count / 2, $"Only {serial.Count} particles survived, which proves little.");
        Assert.Equal(serial.Count, parallel.Count);

        for (var index = 0; index < serial.Count; index++) {
            Assert.Equal(serial.Particles.Identifier[index], parallel.Particles.Identifier[index]);
            Assert.Equal(serial.Particles.Position[index], parallel.Particles.Position[index]);
            Assert.Equal(serial.Particles.Velocity[index], parallel.Particles.Velocity[index]);
            Assert.Equal(serial.Particles.Size[index], parallel.Particles.Size[index]);
            Assert.Equal(serial.Particles.Colour[index], parallel.Particles.Colour[index]);
            Assert.Equal(serial.Particles.Age[index], parallel.Particles.Age[index]);
        }
    }

    /// <summary>
    ///     Every particle is swept exactly once, whatever the batch size divides into.
    /// </summary>
    /// <remarks>
    ///     The index a batch job is given is a <em>batch</em> and not a particle, so the arithmetic
    ///     that turns one into a range is this module's rather than the scheduler's — and a tail batch
    ///     that ran past the end, or a count that rounded the last few away, is the failure that shape
    ///     invites. Ageing is what it is checked with, since one step of it is exactly one delta.
    /// </remarks>
    [Theory]
    [InlineData(1000, 64)]
    [InlineData(1000, 999)]
    [InlineData(1000, 1000)]
    [InlineData(1000, 1024)]
    [InlineData(1, 64)]
    public void Every_particle_is_swept_once(int count, int batch) {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(count)],
            [new(VfxOpcode.SetPosition, Vector4.Zero), new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))],
            [],
            count
        );

        using var system = new VfxSystem(graph);
        system.Step(1f / 60f);

        Assert.Equal(count, system.Count);

        VfxSimulation.Update(system.Particles, graph.Updaters, 0.5f, 0f, scheduler, batch);

        for (var index = 0; index < system.Count; index++) {
            Assert.Equal(0.5f, system.Particles.Age[index]);
        }
    }

    /// <summary>Below the threshold the scheduler is not touched at all.</summary>
    /// <remarks>
    ///     A dispatch and a barrier cost a few microseconds whatever they carry, and a hundred
    ///     particles through two updaters is over well inside that — so a threshold that let them
    ///     through would make a small effect measurably slower for nothing.
    /// </remarks>
    [Fact]
    public void A_small_effect_does_not_reach_for_a_thread() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(100)],
            [
                new(VfxOpcode.SetPosition, Vector4.Zero),
                new(VfxOpcode.SetVelocity, new Vector4(0f, 1f, 0f, 0f)),
                new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))
            ],
            [new(VfxOpcode.Integrate)],
            128
        );

        using var system = new VfxSystem(graph) { Scheduler = scheduler, ParallelThreshold = 4096 };

        for (var step = 0; step < 10; step++) {
            system.Step(1f / 60f);
        }

        Assert.Equal(100, system.Count);
        Assert.False(system.LastStepWasParallel);
    }

    /// <summary>And above it, the sweeps do go through the scheduler.</summary>
    [Fact]
    public void A_large_effect_does() {
        using var system = new VfxSystem(Storm(8192), seed: 3) { Scheduler = scheduler, ParallelThreshold = 1024 };

        system.Step(1f / 60f);
        Assert.False(system.LastStepWasParallel);

        // The first step spawns and the second is the first with particles to sweep, which is the
        // same "update before spawn" rule every other test here has to know about.
        system.Step(1f / 60f);
        Assert.True(system.LastStepWasParallel);
    }
}

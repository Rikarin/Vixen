// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Vfx.Gpu.Tests;

/// <summary>
///     The claim the whole dual-target design rests on: one graph, two backends, the same particles.
/// </summary>
/// <remarks>
///     <para>
///         Everything up to here can be checked without a device — that the emitted source compiles,
///         that it binds what it touches, that the two backends agree about what an initializer is.
///         None of it can check the one thing that matters, which is whether the arithmetic comes out
///         the same. That needs the shader to actually run, so this project exists and skips itself
///         where there is no driver.
///     </para>
///     <para>
///         <b>Two claims, asserted at two different tolerances, and neither of them is exactness.</b>
///         The hash a particle's randomness comes from is integer arithmetic throughout — 32-bit
///         multiplies, xors and shifts, defined to the bit in SPIR-V and in C# — so it agrees
///         exactly. What it feeds does not: <c>min + (max - min) t</c> is a multiply-add, and a
///         target is free to contract one into a fused instruction that rounds once instead of
///         twice. Measured here, that is what happens, and a particle is born one or two ulps from
///         where the CPU put it.
///     </para>
///     <para>
///         So the birth comparison uses a tolerance rather than exact equality — a few ulps for the
///         values that are a multiply-add over the hash, and ten times that for the one that goes
///         through a sine and a cosine, because how accurately a library evaluates a transcendental
///         is that library's own decision. It is still the test that proves the hash agrees: a hash
///         differing by a single bit does not move a particle by an ulp, it draws an unrelated
///         number and moves it across the box. The stepped comparison is looser again, because forty
///         steps of gravity, drag and two interpolations accumulate those ulps.
///     </para>
///     <para>
///         <b>Nothing dies during the comparison.</b> Reaping is the CPU backend's — the GPU kernels
///         age a particle and stop there, because compaction on a device is an atomic append and the
///         dispatch that would drive it does not exist yet. The graph therefore gives every particle
///         a lifetime far longer than the run, so the two buffers hold the same particles in the same
///         slots and a mismatch means the arithmetic differed rather than the bookkeeping.
///     </para>
/// </remarks>
public sealed class VfxAgreementTests {
    const int Count = 512;
    const float Dt = 1f / 60f;
    const uint Seed = 7;

    /// <summary>
    ///     Enough of the opcode set to be worth running: two random initializers, a force, a damping,
    ///     an integration, and two things that interpolate over a particle's life.
    /// </summary>
    static VfxCompiledGraph Graph() =>
        VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(Count)],
            [
                new(VfxOpcode.PositionInBox, new Vector4(-2f, -2f, -2f, 0f)) { B = new(2f, 2f, 2f, 0f) },
                new(VfxOpcode.VelocityRandomDirection, new Vector4(1f, 3f, 0f, 0f)),
                new(VfxOpcode.SetLifetime, new Vector4(8f, 12f, 0f, 0f)),
                new(VfxOpcode.SetSize, new Vector4(0.1f, 0.4f, 0f, 0f)),
                new(VfxOpcode.SetColour, Vector4.One)
            ],
            [
                new(VfxOpcode.Gravity, new Vector4(0f, -9.81f, 0f, 0f)),
                new(VfxOpcode.Drag, new Vector4(0.5f, 0f, 0f, 0f)),
                new(VfxOpcode.Integrate),
                new(VfxOpcode.SizeOverLife, new Vector4(0.4f, 0f, 0f, 0f)),
                new(VfxOpcode.ColourOverLife, Vector4.One) { B = new(1f, 0.2f, 0f, 0f) }
            ],
            Count
        );

    /// <summary>Particles dropped onto a floor, with enough bounce to hit it more than once.</summary>
    static VfxCompiledGraph Bouncing() =>
        VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(Count)],
            [
                new(VfxOpcode.PositionInBox, new Vector4(-2f, 0.5f, -2f, 0f)) { B = new(2f, 3f, 2f, 0f) },
                new(VfxOpcode.SetVelocity, Vector4.Zero),
                new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))
            ],
            [
                new(VfxOpcode.Gravity, new Vector4(0f, -9.81f, 0f, 0f)),
                new(VfxOpcode.Integrate),
                new(VfxOpcode.CollidePlane, new Vector4(0f, 1f, 0f, 0f)) { B = new(0.6f, 0.2f, 0f, 0f) },
                new(VfxOpcode.CollideSphere, new Vector4(0f, 0.5f, 0f, 1f)) { B = new(0.5f, 0.3f, 0f, 0f) }
            ],
            Count
        );

    [Fact]
    public void A_particle_is_born_with_the_same_values_on_both_backends() {
        using var run = Run(0);

        // Eight ulps, for the values that are a multiply-add over the hash and nothing else: a
        // position in a box, a lifetime, a size. A contracted multiply-add costs one or two of
        // these; a hash that disagreed would cost the width of the emitter's box, which is four
        // metres.
        const float Rounding = 1e-6f;

        // Eighty, for the one value that goes through a sine and a cosine. How accurately a library
        // evaluates a transcendental is the library's own decision, and the two here measurably
        // differ — which is not a disagreement about the effect, only about the last few bits of an
        // angle. Held apart from the number above rather than folded into it, so that a real
        // regression in the arithmetic cannot hide inside a tolerance sized for trigonometry.
        const float Transcendental = 1e-5f;

        for (var index = 0; index < Count; index++) {
            Assert.Equal(run.Cpu.Identifier[index], run.Gpu.Identifier[index]);

            Close(run.Cpu.Position[index], run.Gpu.Position[index], Rounding, "position", index);
            Close(run.Cpu.Velocity[index], run.Gpu.Velocity[index], Transcendental, "velocity", index);
            Close(run.Cpu.Size[index], run.Gpu.Size[index], Rounding, "size", index);
            Close(run.Cpu.Lifetime[index], run.Gpu.Lifetime[index], Rounding, "lifetime", index);

            // These two are written rather than computed — a constant colour and a zeroed age — so
            // there is no arithmetic to round and exactness is the right assertion.
            Assert.Equal(run.Cpu.Colour[index], run.Gpu.Colour[index]);
            Assert.Equal(run.Cpu.Age[index], run.Gpu.Age[index]);
        }
    }

    [Fact]
    public void Forty_steps_later_the_two_backends_still_agree() {
        const int Steps = 40;

        using var run = Run(Steps);

        // Loose enough to survive a differently-rounded multiply-add forty times over, tight enough
        // that a particle in the wrong place fails: the positions here are metres and the velocities
        // several metres a second, so a millimetre is a thousandth of the quantity being checked.
        const float Tolerance = 1e-3f;

        for (var index = 0; index < Count; index++) {
            Assert.Equal(run.Cpu.Identifier[index], run.Gpu.Identifier[index]);

            Close(run.Cpu.Position[index], run.Gpu.Position[index], Tolerance, "position", index);
            Close(run.Cpu.Velocity[index], run.Gpu.Velocity[index], Tolerance, "velocity", index);
            Close(run.Cpu.Size[index], run.Gpu.Size[index], Tolerance, "size", index);
            Close(run.Cpu.Age[index], run.Gpu.Age[index], Tolerance, "age", index);

            var expected = run.Cpu.Colour[index];
            var actual = run.Gpu.Colour[index];

            Close(expected.X, actual.X, Tolerance, "colour.r", index);
            Close(expected.Y, actual.Y, Tolerance, "colour.g", index);
            Close(expected.Z, actual.Z, Tolerance, "colour.b", index);
            Close(expected.W, actual.W, Tolerance, "colour.a", index);
        }
    }

    /// <summary>
    ///     Both backends put the emitter's origin in the same place.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The origin is added in three arms of a switch, twice.</b> <c>VfxSimulation</c> adds
    ///         it to <c>SetPosition</c>, <c>PositionInSphere</c> and <c>PositionInBox</c>, and
    ///         <c>VfxShaderEmitter</c> emits the same addition into the same three — so the ways for
    ///         the two to disagree are an arm one of them missed, and a push-constant offset the host
    ///         writes at the wrong place. Both come out here as particles born in the wrong world.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not covered by the tests above, because they all pass a zero origin</b> — which is
    ///         exactly the value that makes a missing addition invisible. That is the same trap
    ///         <c>VfxShaderUniforms</c>'s layout has and the same answer: pick a value where every
    ///         component is different and none of them is the identity.
    ///     </para>
    ///     <para>
    ///         Forty steps rather than zero, so the answer is not only "born in the right place" but
    ///         "born in the right place and integrated from there" — a graph whose gravity or drag
    ///         read the position would be a fourth way to disagree, and this is where that would show.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_two_backends_agree_about_where_the_emitter_is() {
        const int Steps = 40;
        const float Tolerance = 1e-3f;

        var origin = new Vector3(12.5f, -4.25f, 7.75f);

        using var displaced = Run(Steps, origin: origin);
        using var about = Run(Steps);

        for (var index = 0; index < Count; index++) {
            Assert.Equal(displaced.Cpu.Identifier[index], displaced.Gpu.Identifier[index]);

            // The two backends agree with each other at the offset...
            Close(displaced.Cpu.Position[index], displaced.Gpu.Position[index], Tolerance, "position", index);

            // ...and both moved by it, which is what says the addition happened at all rather than
            // being dropped identically on both sides.
            Close(
                displaced.Cpu.Position[index] - origin,
                about.Cpu.Position[index],
                Tolerance,
                "displacement",
                index
            );
        }
    }

    /// <summary>
    ///     The two backends bounce the same way, which is the branchiest thing either of them does.
    /// </summary>
    /// <remarks>
    ///     Worth its own graph rather than folding a collider into the one above. A collider is the
    ///     first operation here whose result depends on a <i>test</i> — is the particle through the
    ///     floor, is it already leaving — and a transcription that got a comparison backwards would
    ///     agree perfectly on every particle that never hit anything. So this one drops every
    ///     particle onto a plane and keeps stepping until they have all bounced several times.
    /// </remarks>
    [Fact]
    public void The_two_backends_bounce_the_same_way() {
        const int Steps = 90;
        const float Tolerance = 1e-3f;

        using var run = Run(Steps, Bouncing());

        for (var index = 0; index < Count; index++) {
            Close(run.Cpu.Position[index], run.Gpu.Position[index], Tolerance, "position", index);
            Close(run.Cpu.Velocity[index], run.Gpu.Velocity[index], Tolerance, "velocity", index);

            // Ninety steps of gravity is a metre and a half of falling, and every particle started
            // within three metres of the floor. So a particle still at or above y = 0 was stopped by
            // the collider, and one below it was not — which is what makes the comparison above a
            // comparison of two colliders rather than of two free falls that happen to match.
            Assert.True(
                run.Gpu.Position[index].Y >= -Tolerance,
                $"Particle {index} is at y = {run.Gpu.Position[index].Y}, which is through the floor."
            );
        }
    }

    /// <summary>The particles never left the slots they were spawned into.</summary>
    /// <remarks>
    ///     Worth its own assertion because the previous two would pass just as well if both backends
    ///     had shuffled identically. The identifiers are the spawn order, and this says they are
    ///     still in it — which is what makes "particle <i>n</i> on one side is particle <i>n</i> on
    ///     the other" a fact rather than an assumption the comparison is built on.
    /// </remarks>
    [Fact]
    public void The_comparison_is_between_the_same_particles() {
        using var run = Run(8);

        for (var index = 0; index < Count; index++) {
            Assert.Equal((uint)index, run.Cpu.Identifier[index]);
            Assert.Equal((uint)index, run.Gpu.Identifier[index]);
        }
    }

    static void Close(Vector3 expected, Vector3 actual, float tolerance, string what, int index) {
        Close(expected.X, actual.X, tolerance, what + ".x", index);
        Close(expected.Y, actual.Y, tolerance, what + ".y", index);
        Close(expected.Z, actual.Z, tolerance, what + ".z", index);
    }

    static void Close(float expected, float actual, float tolerance, string what, int index) {
        // Relative to the magnitude, so that a velocity of forty is held to the same standard as a
        // size of a tenth rather than a four-hundred-times looser one.
        var allowed = tolerance * Math.Max(1f, Math.Abs(expected));

        Assert.True(
            Math.Abs(expected - actual) <= allowed,
            $"Particle {index}'s {what}: the CPU said {expected} and the GPU said {actual}, "
            + $"which is further apart than {allowed}."
        );
    }

    /// <summary>Runs one graph through both backends and hands back what each produced.</summary>
    static Comparison Run(int steps, VfxCompiledGraph? subject = null, Vector3 origin = default) {
        VulkanRequirement.Available(
            VulkanDevice.TryCreate(new(), out var device, out var reason),
            reason
        );

        using var owned = device!;
        VulkanDiagnostics.Reset();

        var graph = subject ?? Graph();
        var shader = VfxShaderEmitter.Emit(graph, "Agreement");

        var cpu = new ParticleBuffer(graph.Attributes, Count, graph.Customs);
        var gpu = new ParticleBuffer(graph.Attributes, Count, graph.Customs);

        try {
            // Spawned on both sides rather than copied, so that the identifiers the GPU hashes are
            // the ones its own buffer handed out. They agree because the counter is per buffer and
            // both start empty — which is itself asserted, above.
            cpu.Spawn(Count, out var first);
            gpu.Spawn(Count, out _);

            VfxSimulation.Initialize(cpu, graph.Initializers, first, Count, Seed, origin);

            var clock = 0f;

            for (var step = 0; step < steps; step++) {
                VfxSimulation.Update(cpu, graph.Updaters, Dt, clock);
                clock += Dt;
            }

            Device(owned, shader, gpu, steps, origin);

            Assert.True(
                VulkanDiagnostics.ErrorCount == 0,
                "The dispatch produced validation errors: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );

            return new(cpu, gpu);
        } catch {
            cpu.Dispose();
            gpu.Dispose();

            throw;
        }
    }

    /// <summary>The GPU half: compile, dispatch, read back into <paramref name="particles" />.</summary>
    static void Device(VulkanDevice device, VfxShader shader, ParticleBuffer particles, int steps, Vector3 origin) {
        var kernels = RavenKernels.Compile(shader.Source);

        using var simulation = new VfxGpuSimulation(device, shader, Count);

        var initializeModule = device.CreateShader(
            ShaderStage.Compute,
            RavenKernels.Of(kernels, shader.InitializeShader),
            shader.InitializeShader
        );

        var updateModule = device.CreateShader(
            ShaderStage.Compute,
            RavenKernels.Of(kernels, shader.UpdateShader),
            shader.UpdateShader
        );

        var initialize = device.CreateComputePipeline(
            new(initializeModule, simulation.Layout, shader.InitializeShader)
        );

        var update = device.CreateComputePipeline(new(updateModule, simulation.Layout, shader.UpdateShader));

        device.BeginFrame();

        using (var list = device.BeginCommandList(QueueKind.Compute, "agreement")) {
            simulation.Upload(list, particles, Count);
            simulation.Initialize(list, initialize, 0, Count, Seed, 0f, origin);

            var clock = 0f;

            for (var step = 0; step < steps; step++) {
                simulation.Update(list, update, Count, Dt, Seed, clock);
                clock += Dt;
            }

            simulation.Download(list, Count);
            list.Finish();
            device.ComputeQueue.Submit([list]);
        }

        device.EndFrame();
        device.WaitIdle();

        simulation.Read(particles, Count);

        device.Destroy(update);
        device.Destroy(initialize);
        device.Destroy(updateModule);
        device.Destroy(initializeModule);
    }

    /// <summary>What the two backends produced, for a test to compare.</summary>
    sealed class Comparison(ParticleBuffer cpu, ParticleBuffer gpu) : IDisposable {
        /// <summary>What <see cref="VfxSimulation" /> produced.</summary>
        public ParticleBuffer Cpu => cpu;

        /// <summary>What the dispatch produced, read back.</summary>
        public ParticleBuffer Gpu => gpu;

        public void Dispose() {
            cpu.Dispose();
            gpu.Dispose();
        }
    }
}

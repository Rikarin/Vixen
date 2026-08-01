// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Vfx.Gpu.Tests;

/// <summary>The last thing the CPU still did for a device effect: removing the finished particles.</summary>
/// <remarks>
///     <para>
///         <b>The comparison is between <i>sets</i>, not between slots, and that is the whole
///         difference from <c>VfxAgreementTests</c>.</b> The CPU fills each hole from the tail, so a
///         survivor's slot depends on which particles ahead of it died; the GPU hands out slots in
///         whatever order the invocations reached the atomic, which is not reproducible between two
///         runs of one frame. Neither order is promised anywhere. So these sort by identifier before
///         comparing — and the identifier is exactly the right key, because a particle's randomness
///         follows it rather than its slot, which is the property <c>VfxRandom</c> exists to give.
///     </para>
///     <para>
///         ⚠ <b>Which means the comparison would pass on a kernel that shuffled the survivors and
///         also on one that kept the wrong ones</b> — the first is allowed and the second is the bug.
///         What separates them is that the identifiers themselves are compared as a set: a kernel
///         that kept a dead particle or dropped a live one produces a different set, whatever order
///         it is in.
///     </para>
/// </remarks>
public sealed class VfxReapTests {
    const int Count = 512;
    const float Dt = 1f / 60f;
    const uint Seed = 11;

    /// <summary>
    ///     Lifetimes spread across a range the run crosses, so a reap has both survivors and dead.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The spread matters more than the values.</b> A graph where everything dies at once
    ///     would be reaped correctly by a kernel that emptied the buffer, and one where nothing dies
    ///     would be reaped correctly by a kernel that did nothing. Half and half is the only shape
    ///     that catches either.
    /// </remarks>
    static VfxCompiledGraph Graph() =>
        VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(Count)],
            [
                new(VfxOpcode.PositionInBox, new Vector4(-2f, -2f, -2f, 0f)) { B = new(2f, 2f, 2f, 0f) },
                new(VfxOpcode.SetVelocity, new Vector4(0f, 1f, 0f, 0f)),
                new(VfxOpcode.SetLifetime, new Vector4(0.1f, 0.9f, 0f, 0f))
            ],
            [new(VfxOpcode.Integrate)],
            Count
        );

    [Fact]
    public void The_survivors_are_the_same_particles_on_both_backends() {
        // Thirty steps is half a second, which lands inside the lifetime range — so roughly half of
        // them are finished and the assertion below has something to be wrong about.
        using var run = Run(30);

        Assert.NotEqual(0, run.CpuCount);
        Assert.NotEqual(Count, run.CpuCount);

        Assert.Equal(run.CpuCount, run.GpuCount);
        Assert.Equal(Identifiers(run.Cpu, run.CpuCount), Identifiers(run.Gpu, run.GpuCount));
    }

    /// <summary>A survivor arrives whole, not merely counted.</summary>
    /// <remarks>
    ///     ⚠ <b>The identifier is the one that would go unnoticed.</b> Nothing in the update kernel
    ///     writes it, so a reap that forgot to carry it would leave each survivor holding whatever
    ///     was in its new slot — and the effect would look perfectly correct until the next spawn
    ///     re-rolled every survivor's size and colour from somebody else's number.
    /// </remarks>
    [Fact]
    public void A_survivor_carries_every_attribute_to_its_new_slot() {
        using var run = Run(30);

        var cpu = Ordered(run.Cpu, run.CpuCount);
        var gpu = Ordered(run.Gpu, run.GpuCount);

        const float Tolerance = 1e-3f;

        for (var index = 0; index < cpu.Count; index++) {
            var (identifier, left) = cpu[index];
            var (mirror, right) = gpu[index];

            Assert.Equal(identifier, mirror);

            Close(run.Cpu.Position[left], run.Gpu.Position[right], Tolerance, "position", identifier);
            Close(run.Cpu.Velocity[left], run.Gpu.Velocity[right], Tolerance, "velocity", identifier);
            Close(run.Cpu.Lifetime[left], run.Gpu.Lifetime[right], Tolerance, "lifetime", identifier);
            Close(run.Cpu.Age[left], run.Gpu.Age[right], Tolerance, "age", identifier);
        }
    }

    /// <summary>Every survivor is unfinished, which is the property being compacted for.</summary>
    [Fact]
    public void Nothing_that_survived_had_reached_its_lifetime() {
        using var run = Run(30);

        for (var index = 0; index < run.GpuCount; index++) {
            Assert.True(
                run.Gpu.Age[index] < run.Gpu.Lifetime[index],
                $"Slot {index} survived at age {run.Gpu.Age[index]} with a lifetime of {run.Gpu.Lifetime[index]}."
            );
        }
    }

    /// <summary>
    ///     Reaping twice keeps working, which is what says the buffers really do swap rather than one
    ///     of them being the answer by luck.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The first reap alone passes on an implementation that writes to a second buffer and
    ///     never reads it back.</b> The second reads what the first wrote — as the source this time —
    ///     so a swap that did not happen shows up as a count that stops changing, and a swap that
    ///     happened twice as a set that reverts.
    /// </remarks>
    [Fact]
    public void A_second_reap_compacts_what_the_first_one_kept() {
        using var run = Run(30, reaps: 2);

        Assert.Equal(run.CpuCount, run.GpuCount);
        Assert.Equal(Identifiers(run.Cpu, run.CpuCount), Identifiers(run.Gpu, run.GpuCount));
    }

    /// <summary>The indirect command carries the survivor count without anybody reading it back.</summary>
    /// <remarks>
    ///     The point of putting the compaction on a device at all: a draw that reads its instance
    ///     count out of a buffer never has to wait for the CPU to be told the number. This test does
    ///     read it back — to check it — which is exactly what a frame would not do.
    /// </remarks>
    [Fact]
    public void The_draw_command_gets_the_count_the_reap_produced() {
        using var run = Run(30, arguments: true);

        Assert.Equal((uint)6, run.Arguments[0]);
        Assert.Equal((uint)run.GpuCount, run.Arguments[1]);

        // The other three are the template's, untouched by the count copy — a first index, a vertex
        // offset and a first instance, all zero for one instanced quad per particle.
        Assert.Equal((uint)0, run.Arguments[2]);
        Assert.Equal((uint)0, run.Arguments[3]);
        Assert.Equal((uint)0, run.Arguments[4]);
    }

    static IReadOnlyList<uint> Identifiers(ParticleBuffer particles, int count) =>
        [.. Enumerable.Range(0, count).Select(index => particles.Identifier[index]).Order()];

    /// <summary>The live particles as (identifier, slot), in identifier order.</summary>
    static List<(uint Identifier, int Slot)> Ordered(ParticleBuffer particles, int count) => [
        .. Enumerable.Range(0, count)
            .Select(index => (particles.Identifier[index], index))
            .OrderBy(entry => entry.Item1)
    ];

    static void Close(Vector3 expected, Vector3 actual, float tolerance, string what, uint identifier) {
        Close(expected.X, actual.X, tolerance, what + ".x", identifier);
        Close(expected.Y, actual.Y, tolerance, what + ".y", identifier);
        Close(expected.Z, actual.Z, tolerance, what + ".z", identifier);
    }

    static void Close(float expected, float actual, float tolerance, string what, uint identifier) {
        var allowed = tolerance * Math.Max(1f, Math.Abs(expected));

        Assert.True(
            Math.Abs(expected - actual) <= allowed,
            $"Particle {identifier}'s {what}: the CPU said {expected} and the GPU said {actual}, "
            + $"which is further apart than {allowed}."
        );
    }

    static Comparison Run(int steps, int reaps = 1, bool arguments = false) {
        VulkanRequirement.Available(
            VulkanDevice.TryCreate(new(), out var device, out var reason),
            reason
        );

        using var owned = device!;
        VulkanDiagnostics.Reset();

        var graph = Graph();
        var shader = VfxShaderEmitter.Emit(graph, "Reaping");

        Assert.True(shader.HasReap);

        var cpu = new ParticleBuffer(graph.Attributes, Count, graph.Customs);
        var gpu = new ParticleBuffer(graph.Attributes, Count, graph.Customs);

        try {
            cpu.Spawn(Count, out var first);
            gpu.Spawn(Count, out _);

            VfxSimulation.Initialize(cpu, graph.Initializers, first, Count, Seed);

            var clock = 0f;
            var cpuCount = Count;

            for (var reap = 0; reap < reaps; reap++) {
                for (var step = 0; step < steps; step++) {
                    VfxSimulation.Update(cpu, graph.Updaters, Dt, clock);
                    clock += Dt;
                }

                cpu.Reap();
                cpuCount = cpu.Count;
            }

            var (gpuCount, command) = Device(owned, shader, gpu, steps, reaps, arguments);

            Assert.True(
                VulkanDiagnostics.ErrorCount == 0,
                "The dispatch produced validation errors: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );

            return new(cpu, gpu, cpuCount, gpuCount, command);
        } catch {
            cpu.Dispose();
            gpu.Dispose();

            throw;
        }
    }

    /// <summary>Steps and reaps on the device, then reads back only the survivors.</summary>
    static (int Count, uint[] Arguments) Device(
        VulkanDevice device,
        VfxShader shader,
        ParticleBuffer particles,
        int steps,
        int reaps,
        bool arguments
    ) {
        var kernels = RavenKernels.Compile(shader.Source);

        using var simulation = new VfxGpuSimulation(device, shader, Count);

        var modules = new[] { shader.InitializeShader, shader.UpdateShader, shader.ReapShader }
            .Select(name => (Name: name, Module: device.CreateShader(ShaderStage.Compute, RavenKernels.Of(kernels, name), name)))
            .ToArray();

        var pipelines = modules
            .ToDictionary(
                entry => entry.Name,
                entry => device.CreateComputePipeline(new(entry.Module, simulation.Layout, entry.Name))
            );

        var survivors = Count;
        var command = new uint[VfxGpuSimulation.DrawArgumentsSize / sizeof(uint)];

        device.BeginFrame();

        // ⚠ One submission per reap, because the survivor count is only knowable to the host after
        // the list that produced it has completed — and the next round has to dispatch over exactly
        // that many particles. A frame that draws indirectly never does this; a test that checks the
        // count against the CPU's has to.
        for (var reap = 0; reap < reaps; reap++) {
            using (var list = device.BeginCommandList(QueueKind.Compute, $"reap{reap}")) {
                if (reap == 0) {
                    simulation.Upload(list, particles, Count);
                    simulation.Initialize(list, pipelines[shader.InitializeShader], 0, Count, Seed, 0f);
                }

                var clock = Dt * steps * reap;

                for (var step = 0; step < steps; step++) {
                    simulation.Update(list, pipelines[shader.UpdateShader], survivors, Dt, Seed, clock);
                    clock += Dt;
                }

                simulation.Reap(list, pipelines[shader.ReapShader], survivors);

                if (arguments && reap == reaps - 1) {
                    simulation.WriteDrawArguments(list);
                }

                list.Finish();
                device.ComputeQueue.Submit([list]);
            }

            device.WaitIdle();
            survivors = simulation.ReadSurvivors();
        }

        if (arguments) {
            using var list = device.BeginCommandList(QueueKind.Compute, "arguments");

            list.Barrier(new(
                [new(simulation.DrawArguments, ResourceState.IndirectArgument, ResourceState.CopySource)],
                []
            ));

            var staging = device.CreateBuffer(new(
                VfxGpuSimulation.DrawArgumentsSize,
                BufferUsage.CopyDestination,
                MemoryAccess.HostReadback,
                "arguments.readback"
            ));

            list.CopyBuffer(simulation.DrawArguments, 0, staging, 0, VfxGpuSimulation.DrawArgumentsSize);
            list.Finish();
            device.ComputeQueue.Submit([list]);
            device.WaitIdle();

            Span<byte> bytes = stackalloc byte[VfxGpuSimulation.DrawArgumentsSize];

            device.Read(staging, 0, bytes);

            for (var index = 0; index < command.Length; index++) {
                command[index] = BitConverter.ToUInt32(bytes[(index * sizeof(uint))..]);
            }

            device.Destroy(staging);
        }

        using (var list = device.BeginCommandList(QueueKind.Compute, "download")) {
            simulation.Download(list, survivors);
            list.Finish();
            device.ComputeQueue.Submit([list]);
        }

        device.EndFrame();
        device.WaitIdle();

        simulation.Read(particles, survivors);

        foreach (var pipeline in pipelines.Values) {
            device.Destroy(pipeline);
        }

        foreach (var (_, module) in modules) {
            device.Destroy(module);
        }

        return (survivors, command);
    }

    sealed class Comparison(
        ParticleBuffer cpu,
        ParticleBuffer gpu,
        int cpuCount,
        int gpuCount,
        uint[] arguments
    ) : IDisposable {
        public ParticleBuffer Cpu => cpu;

        public ParticleBuffer Gpu => gpu;

        public int CpuCount => cpuCount;

        public int GpuCount => gpuCount;

        public uint[] Arguments => arguments;

        public void Dispose() {
            cpu.Dispose();
            gpu.Dispose();
        }
    }
}

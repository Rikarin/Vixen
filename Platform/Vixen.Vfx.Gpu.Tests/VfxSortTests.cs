// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Vixen.Raven;
using Vixen.Raven.CodeGen;
using Vixen.Raven.IR;
using Vixen.Raven.Lowering;
using Vixen.Raven.Syntax;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Vfx.Gpu.Tests;

/// <summary>The order an alpha-blended effect is drawn in, produced on the device.</summary>
/// <remarks>
///     <para>
///         <b>What is asserted is the <i>order</i>, against the one the CPU builder produces.</b>
///         Both sides sort on the same key — negated squared distance, or negated age — so the
///         sequence of slots has to match position for position, not merely be sorted. A network that
///         produced a correctly-ordered permutation of the wrong particles would satisfy "is sorted"
///         perfectly.
///     </para>
///     <para>
///         ⚠ <b>Ties are the one thing the two backends may legitimately disagree about</b>, and the
///         fixtures avoid them rather than the assertion tolerating them: <c>Array.Sort</c> is an
///         introsort and is not stable, and a bitonic network is not stable either, so two particles
///         at exactly the same distance may come out either way round on either side. Every fixture
///         here gives each particle a distinct key, which is also what a real effect has.
///     </para>
/// </remarks>
public sealed class VfxSortTests {
    const int Count = 300;

    /// <summary>The shipped sort kernels, compiled from the library rather than from a copy.</summary>
    static Dictionary<string, byte[]> Kernels() {
        var root = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Raven", "Library")
        );

        var trees = new[] { "Core/Math.rvn", "Vfx/ParticleSort.rvn" }
            .Select(name => Path.Combine(root, name))
            .Select(path => SyntaxTree.ParseText(File.ReadAllText(path), path: Path.GetFileName(path)))
            .ToArray();

        foreach (var tree in trees) {
            Assert.True(tree.Diagnostics.Count == 0, string.Join("\n", tree.Diagnostics));
        }

        var compilation = Compilation.Create("Sort", trees);

        Assert.Empty(compilation.GetDiagnostics());

        var bag = new DiagnosticBag();
        var module = Lowerer.Lower(compilation, bag);

        IrVerifier.Verify(module, bag);
        Assert.True(bag.IsEmpty, string.Join("\n", bag.ToArray().Select(d => d.ToString())));

        var backend = TargetBackends.Create("spirv");

        Assert.NotNull(backend);

        var generated = backend.Generate(module, bag);

        Assert.True(bag.IsEmpty, string.Join("\n", bag.ToArray().Select(d => d.ToString())));

        Dictionary<string, byte[]> kernels = [];

        foreach (var unit in generated) {
            if (unit is { Binary: { } binary }) {
                kernels[unit.Name] = binary;
            }
        }

        return kernels;
    }

    static byte[] Of(Dictionary<string, byte[]> kernels, string declaration) {
        foreach (var (name, binary) in kernels) {
            if (name.StartsWith(declaration, StringComparison.Ordinal)) {
                return binary;
            }
        }

        Assert.Fail($"No module for '{declaration}'. Got: {string.Join(", ", kernels.Keys)}.");

        return [];
    }

    /// <summary>A graph whose particles land at distinct distances, so the order is total.</summary>
    static VfxCompiledGraph Graph() =>
        VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(Count)],
            [
                new(VfxOpcode.PositionInBox, new Vector4(-8f, -8f, -8f, 0f)) { B = new(8f, 8f, 8f, 0f) },
                new(VfxOpcode.SetVelocity, Vector4.Zero),
                new(VfxOpcode.SetLifetime, new Vector4(4f, 9f, 0f, 0f))
            ],
            [new(VfxOpcode.Integrate)],
            Count
        );

    [Fact]
    public void The_device_orders_the_particles_the_way_the_cpu_builder_does() {
        var camera = new Vector3(3f, 2f, -14f);

        using var run = Run(VfxSortMode.ByDepth, camera);

        Assert.Equal(run.Expected, run.Actual);
    }

    /// <summary>The same claim for the other key, which the CPU builder also offers.</summary>
    [Fact]
    public void Sorting_by_age_agrees_as_well() {
        using var run = Run(VfxSortMode.ByAge, default);

        Assert.Equal(run.Expected, run.Actual);
    }

    /// <summary>
    ///     ⚠ <b>The slots above the live count sort past every real one</b>, which is what makes a
    ///     network sized to a power of two usable on a count that is not one.
    /// </summary>
    [Fact]
    public void The_padding_sorts_behind_every_live_particle() {
        using var run = Run(VfxSortMode.ByDepth, new Vector3(3f, 2f, -14f));

        // Everything the draw reads is a live slot, and nothing live is missing from it.
        Assert.Equal(Count, run.Actual.Length);
        Assert.All(run.Actual, slot => Assert.True(slot < Count, $"slot {slot} is padding and is inside the draw"));
        Assert.Equal(Count, run.Actual.Distinct().Count());
    }

    /// <summary>And the network is the size the pass count says it is.</summary>
    /// <remarks>
    ///     A cheap gate on the arithmetic being the triangular number rather than something that
    ///     happens to work at one capacity: 512 slots is nine stages, so forty-five passes.
    /// </remarks>
    [Fact]
    public void The_pass_count_is_the_triangular_number_of_the_stages() {
        Assert.Equal(45, VfxGpuSort.Passes(300));
        Assert.Equal(45, VfxGpuSort.Passes(512));
        Assert.Equal(55, VfxGpuSort.Passes(513));
        Assert.Equal(1, VfxGpuSort.Passes(2));
    }

    /// <summary>Runs one graph on both sides and hands back the two orders.</summary>
    static Comparison Run(VfxSortMode mode, Vector3 camera) {
        VulkanRequirement.Available(VulkanDevice.TryCreate(new(), out var device, out var reason), reason);

        using var owned = device!;
        VulkanDiagnostics.Reset();

        var graph = Graph();
        var shader = VfxShaderEmitter.Emit(graph, "Sorted");

        var particles = new ParticleBuffer(graph.Attributes, Count, graph.Customs);

        try {
            particles.Spawn(Count, out var first);
            VfxSimulation.Initialize(particles, graph.Initializers, first, Count, 5u);

            VfxSimulation.Update(particles, graph.Updaters, 1f / 60f, 0f);

            // ⚠ **Ages spread by hand, and a step is not enough to do it.** A burst spawns every
            // particle at once, so one update leaves them all at exactly the same age — which is a
            // tie for every pair, and neither `Array.Sort` nor a bitonic network is stable, so the
            // two sides would be comparing two arbitrary permutations of one equivalence class. A
            // real effect spawns over time and has the distinct ages this writes in.
            var ages = particles.Age;

            for (var index = 0; index < Count; index++) {
                ages[index] = 0.5f + (index * 0.01f);
            }

            var expected = Expected(particles, mode, camera);
            var actual = Device(owned, shader, particles, mode, camera);

            Assert.True(
                VulkanDiagnostics.ErrorCount == 0,
                "The dispatch produced validation errors: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );

            return new(particles, expected, actual);
        } catch {
            particles.Dispose();

            throw;
        }
    }

    /// <summary>
    ///     The CPU order, built the way <c>VfxGeometryBuilder.Sort</c> builds it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Written here rather than reached through the builder</b>, because the builder produces
    ///     vertices and what is being compared is the permutation. The keys are the builder's own —
    ///     negated squared distance and negated age — and the comparison is the assertion that the two
    ///     agree, so a change to either side's key shows up as a failure rather than as two files
    ///     drifting quietly apart.
    /// </remarks>
    static uint[] Expected(ParticleBuffer particles, VfxSortMode mode, Vector3 camera) {
        var keys = new float[Count];
        var order = new int[Count];

        for (var index = 0; index < Count; index++) {
            order[index] = index;

            keys[index] = mode == VfxSortMode.ByAge
                ? -particles.Age[index]
                : -Vector3.DistanceSquared(particles.Position[index], camera);
        }

        Array.Sort(keys, order, 0, Count);

        return [.. order.Select(slot => (uint) slot)];
    }

    static uint[] Device(
        VulkanDevice device,
        VfxShader shader,
        ParticleBuffer particles,
        VfxSortMode mode,
        Vector3 camera
    ) {
        var emitted = RavenKernels.Compile(shader.Source);
        var sorters = Kernels();

        using var simulation = new VfxGpuSimulation(device, shader, Count);
        using var sort = new VfxGpuSort(device, simulation, mode);

        var seedModule = device.CreateShader(ShaderStage.Compute, Of(sorters, "ParticleSortSeed"), "seed");
        var stepModule = device.CreateShader(ShaderStage.Compute, Of(sorters, "ParticleSortStep"), "step");

        var seed = device.CreateComputePipeline(new(seedModule, sort.SeedLayout, "seed"));
        var step = device.CreateComputePipeline(new(stepModule, sort.StepLayout, "step"));

        device.BeginFrame();

        using (var list = device.BeginCommandList(QueueKind.Compute, "sort")) {
            // The particles are uploaded rather than initialised on the device, because what is under
            // test is the order and not the arithmetic that produced the positions — and uploading
            // makes both sides sort the identical numbers.
            simulation.Upload(list, particles, Count);
            sort.Record(list, seed, step, Count, camera);
            sort.Download(list);

            list.Finish();
            device.ComputeQueue.Submit([list]);
        }

        device.EndFrame();
        device.WaitIdle();

        var order = new uint[Count];

        sort.Read(order);

        device.Destroy(step);
        device.Destroy(seed);
        device.Destroy(stepModule);
        device.Destroy(seedModule);

        _ = emitted;

        return order;
    }

    sealed class Comparison(ParticleBuffer particles, uint[] expected, uint[] actual) : IDisposable {
        public uint[] Expected => expected;

        public uint[] Actual => actual;

        public void Dispose() => particles.Dispose();
    }
}

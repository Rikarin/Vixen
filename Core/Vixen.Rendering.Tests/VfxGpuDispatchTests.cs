// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Vfx;
using Xunit;

namespace Vixen.Rendering.Tests;

/// <summary>
///     What the device particle backend recorded, counted against the command stream itself.
/// </summary>
/// <remarks>
///     <para>
///         <b>The failure this exists for is a backend nothing drove.</b> A host that constructs a
///         <see cref="VfxGpuSimulation" />, records no dispatch and draws the CPU expansion produces
///         the same frame at the same cost as one whose device path works — no validation error, no
///         log line, nothing to grep for. <see cref="VfxGpuSimulation.Dispatches" /> is what tells
///         those apart, so the counter itself has to be worth believing.
///     </para>
///     <para>
///         ⚠ <b>Checked against the recorder rather than against itself.</b> A test asserting that
///         two <c>Update</c> calls leave <c>Dispatches</c> at two is a test of arithmetic this file
///         wrote down twice. <see cref="NullDevice" />'s recorder counts
///         <see cref="RecordedCommandKind.Dispatch" /> from the command stream, which is a different
///         source of truth — so a counter incremented in the wrong place, incremented twice, or not
///         incremented on the reap fails here rather than agreeing with the assertion.
///     </para>
///     <para>
///         <b>No device and no compiler, deliberately.</b> Whether the kernels are correct is
///         <c>Platform/Vixen.Vfx.Gpu.Tests</c>'s question and needs all three assemblies in one
///         process. Whether the host recorded what it says it recorded is this one's, and a recording
///         backend answers it everywhere — including the legs with no driver.
///     </para>
/// </remarks>
public class VfxGpuDispatchTests : IDisposable {
    const int Count = 256;

    /// <summary>
    ///     ⚠ <b>The recorder is filled at <i>submit</i>, not as the list records.</b>
    /// </summary>
    /// <remarks>
    ///     <see cref="NullCommandList" /> buffers its commands and flushes them into the
    ///     <see cref="CommandRecorder" /> when the queue takes it, which is what makes an abandoned
    ///     list invisible — so every case here submits, and a test that forgot to would compare a
    ///     rising counter against an empty stream and read as a broken counter.
    /// </remarks>
    readonly NullDevice device = new(new() { Record = true });

    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>An initialize and a run of updates are one dispatch each, and the counter says so.</summary>
    [Fact]
    public void The_dispatch_count_is_the_command_stream_s_count() {
        const int Steps = 7;

        var shader = VfxShaderEmitter.Emit(Graph(), "Counted");

        using var simulation = new VfxGpuSimulation(device, shader, Count);

        Assert.Equal(0, simulation.Dispatches);

        using (var list = device.BeginCommandList(QueueKind.Compute, "counted")) {
            simulation.Initialize(list, Kernel("initialize"), 0, Count, seed: 3, time: 0f);

            var clock = 0f;

            for (var step = 0; step < Steps; step++) {
                simulation.Update(list, Kernel("update"), Count, 1f / 60f, seed: 3, clock);
                clock += 1f / 60f;
            }

            list.Finish();
            device.ComputeQueue.Submit([list]);
        }

        Assert.Equal(Steps + 1, simulation.Dispatches);
        Assert.Equal(simulation.Dispatches, device.Recorder!.CountOf(RecordedCommandKind.Dispatch));
    }

    /// <summary>
    ///     A dispatch over no particles is not recorded, and does not count.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The counter has to agree with the early return rather than with the call.</b> A
    ///     dispatch of zero groups is a validation error on Vulkan rather than a no-op, so
    ///     <see cref="VfxGpuSimulation" /> refuses one — and a counter that rose anyway would report a
    ///     device path running on a frame where the effect had died out, which is exactly the frame
    ///     somebody would be reading the counter to explain.
    /// </remarks>
    [Fact]
    public void An_empty_effect_records_nothing_and_counts_nothing() {
        var shader = VfxShaderEmitter.Emit(Graph(), "Empty");

        using var simulation = new VfxGpuSimulation(device, shader, Count);

        using (var list = device.BeginCommandList(QueueKind.Compute, "empty")) {
            simulation.Update(list, Kernel("update"), 0, 1f / 60f, seed: 3, time: 0f);
            list.Finish();
            device.ComputeQueue.Submit([list]);
        }

        Assert.Equal(0, simulation.Dispatches);
        Assert.Equal(0, device.Recorder!.CountOf(RecordedCommandKind.Dispatch));
    }

    /// <summary>The reap is a dispatch like the others, and is counted like one.</summary>
    /// <remarks>
    ///     Its own test because <see cref="VfxGpuSimulation.Reap" /> does not go through the private
    ///     <c>Dispatch</c> the other two share — it zeroes the counter, dispatches, and flips the live
    ///     set — so it is the one place the increment could be forgotten while every other assertion
    ///     here still passed.
    /// </remarks>
    [Fact]
    public void The_reap_is_counted_as_a_dispatch() {
        var shader = VfxShaderEmitter.Emit(Reaping(), "Reaped");

        Assert.True(shader.HasReap, "the graph has an age and a lifetime, so it should reap");

        using var simulation = new VfxGpuSimulation(device, shader, Count);

        using (var list = device.BeginCommandList(QueueKind.Compute, "reaped")) {
            simulation.Update(list, Kernel("update"), Count, 1f / 60f, seed: 3, time: 0f);
            simulation.Reap(list, Kernel("reap"), Count);
            list.Finish();
            device.ComputeQueue.Submit([list]);
        }

        Assert.Equal(2, simulation.Dispatches);
        Assert.Equal(simulation.Dispatches, device.Recorder!.CountOf(RecordedCommandKind.Dispatch));
    }

    /// <summary>
    ///     One <see cref="VfxGpuSort.Record" /> is the network's passes plus its seed.
    /// </summary>
    /// <remarks>
    ///     <see cref="VfxGpuSort.Passes" /> is a promise about cost that a caller is invited to read
    ///     instead of taking a capture. This is what makes it a promise: the recorded stream is
    ///     counted, and the number the class advertises is compared against it rather than against
    ///     the loop that produced both.
    /// </remarks>
    [Fact]
    public void The_sort_records_the_passes_it_advertises() {
        var shader = VfxShaderEmitter.Emit(Graph(), "Sorted");

        using var simulation = new VfxGpuSimulation(device, shader, Count);
        using var sort = new VfxGpuSort(device, simulation, VfxSortMode.ByDepth);

        Assert.Equal(0, sort.Dispatches);

        using (var list = device.BeginCommandList(QueueKind.Compute, "sorted")) {
            sort.Record(list, Kernel("seed"), Kernel("step"), Count, new Vector3(0f, 0f, 5f));
            list.Finish();
            device.ComputeQueue.Submit([list]);
        }

        Assert.Equal(VfxGpuSort.Passes(sort.Capacity) + 1, sort.Dispatches);
        Assert.Equal(sort.Dispatches, device.Recorder!.CountOf(RecordedCommandKind.Dispatch));
    }

    /// <summary>
    ///     ⚠ <b>The sort follows the reap's flip instead of ordering the generation it was built on.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The defect this is the fixture for.</b> A reaping
    ///         <see cref="VfxGpuSimulation" /> double-buffers its attribute buffers and flips at the
    ///         end of every <see cref="VfxGpuSimulation.Reap" />, so
    ///         <see cref="VfxGpuSimulation.Storage" /> answers with a different handle after one than
    ///         before it. <see cref="VfxGpuSort" /> wrote its seed descriptor set once, in its
    ///         constructor, from whichever generation was current then — and then bound that set
    ///         every frame for the rest of its life. The order it produced was therefore right on the
    ///         first frame and computed from the dead half of the buffer pair on every frame after,
    ///         which draws as alpha blended in the wrong order and raises nothing a validation layer
    ///         would mention.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The third leg is what makes this more than "the handle changed".</b> A sort that
    ///         allocated a fresh set per frame would satisfy the first two assertions and leak one
    ///         set per frame. Two reaps put the simulation back on the generation it started on, so
    ///         the set bound then has to be the <i>same</i> set bound at the start — the binding
    ///         tracks the parity rather than the frame.
    ///     </para>
    ///     <para>
    ///         <b>The instrument is checked first.</b> If the simulation stopped flipping, every
    ///         assertion below would pass for the wrong reason, so the flip itself is asserted
    ///         before anything is concluded from it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_sort_seeds_from_the_generation_the_reap_left_current() {
        var shader = VfxShaderEmitter.Emit(Reaping(), "SortedAndReaped");

        Assert.True(shader.HasReap, "the graph has an age and a lifetime, so it should reap");

        using var simulation = new VfxGpuSimulation(device, shader, Count);
        using var sort = new VfxGpuSort(device, simulation, VfxSortMode.ByDepth);

        var first = simulation.Storage(VfxAttribute.Position);

        Assert.True(first.IsValid);
        Assert.Contains(device.RecordedWrites!, write => write.Buffer == first);

        var startSet = SortSeedSet(sort);

        Reap(simulation, shader);

        var second = simulation.Storage(VfxAttribute.Position);

        // The instrument: everything below is a claim about following the flip, so a simulation that
        // did not flip would make all of it vacuous.
        Assert.True(second.IsValid);
        Assert.NotEqual(first, second);

        var written = device.RecordedWrites!.Count;
        var reapedSet = SortSeedSet(sort);

        Assert.NotEqual(startSet, reapedSet);
        Assert.Contains(device.RecordedWrites!.Skip(written), write => write.Buffer == second);

        Reap(simulation, shader);

        Assert.Equal(first, simulation.Storage(VfxAttribute.Position));

        written = device.RecordedWrites!.Count;

        Assert.Equal(startSet, SortSeedSet(sort));
        Assert.Equal(written, device.RecordedWrites!.Count);
    }

    /// <summary>Records one sort and returns the descriptor set its seed dispatch bound.</summary>
    long SortSeedSet(VfxGpuSort sort) {
        var before = device.Recorder!.CountOf(RecordedCommandKind.BindDescriptorSet);

        using (var list = device.BeginCommandList(QueueKind.Compute, "seeded")) {
            sort.Record(list, Kernel("seed"), Kernel("step"), Count, new Vector3(0f, 0f, 5f));
            list.Finish();
            device.ComputeQueue.Submit([list]);
        }

        // Record binds the seed set first and the step set second, so the seed is the first bind
        // this call added.
        return device.Recorder!.OfKind(RecordedCommandKind.BindDescriptorSet)[before].B;
    }

    /// <summary>Runs one reap, which is what flips the simulation's live attribute set.</summary>
    void Reap(VfxGpuSimulation simulation, VfxShader shader) {
        using var list = device.BeginCommandList(QueueKind.Compute, "reaping");

        simulation.Reap(list, Kernel(shader.Name + ".reap"), Count);
        list.Finish();
        device.ComputeQueue.Submit([list]);
    }

    /// <summary>A pipeline handle for a recorder to bind.</summary>
    /// <remarks>
    ///     ⚠ <b>A real handle rather than <c>default</c>, because binding a null pipeline throws</b> —
    ///     <see cref="NullDevice" /> checks that much even though it runs nothing, which is the whole
    ///     point of recording against it rather than against a mock this file wrote. The module's
    ///     bytes are never read: what is being counted is the command, not the kernel.
    /// </remarks>
    PipelineHandle Kernel(string name) =>
        device.CreateComputePipeline(
            new(device.CreateShader(ShaderStage.Compute, new byte[4], name), default, name)
        );

    /// <summary>A graph whose particles never finish: positions, a velocity, and an integration.</summary>
    static VfxCompiledGraph Graph() =>
        VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(Count)],
            [
                new(VfxOpcode.PositionInBox, new Vector4(-1f, -1f, -1f, 0f)) { B = new(1f, 1f, 1f, 0f) },
                new(VfxOpcode.VelocityRandomDirection, new Vector4(1f, 2f, 0f, 0f))
            ],
            [new(VfxOpcode.Gravity, new Vector4(0f, -9.81f, 0f, 0f)), new(VfxOpcode.Integrate)],
            Count
        );

    /// <summary>The same, with the lifetime that makes a reap kernel exist.</summary>
    static VfxCompiledGraph Reaping() =>
        VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(Count)],
            [
                new(VfxOpcode.PositionInBox, new Vector4(-1f, -1f, -1f, 0f)) { B = new(1f, 1f, 1f, 0f) },
                new(VfxOpcode.VelocityRandomDirection, new Vector4(1f, 2f, 0f, 0f)),
                new(VfxOpcode.SetLifetime, new Vector4(1f, 2f, 0f, 0f))
            ],
            [new(VfxOpcode.Gravity, new Vector4(0f, -9.81f, 0f, 0f)), new(VfxOpcode.Integrate)],
            Count
        );
}

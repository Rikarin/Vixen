// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Graphics.RenderGraph.Tests;

/// <summary>One declared use in a generated graph.</summary>
/// <param name="Resource">Which resource, from 0.</param>
/// <param name="State">What the pass needs it to be in.</param>
/// <param name="IsWrite">Whether it writes it.</param>
public readonly record struct UseSpec(int Resource, ResourceState State, bool IsWrite);

/// <summary>One generated pass.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Uses">What it touches.</param>
/// <param name="SideEffect">Whether it must survive culling.</param>
public readonly record struct PassSpec(string Name, UseSpec[] Uses, bool SideEffect);

/// <summary>A whole generated frame.</summary>
/// <param name="ResourceCount">How many transients it declares.</param>
/// <param name="Passes">Its passes, in declaration order.</param>
public readonly record struct GraphSpec(int ResourceCount, PassSpec[] Passes);

/// <summary>
///     Random pass graphs, checked against an independent tracker.
/// </summary>
/// <remarks>
///     <para>
///         What [05](../../docs/plan/05-graphics-rhi.md) § Testing asks for. The check is deliberately
///         <em>not</em> a second implementation of the graph's barrier planner — that would agree with
///         it for the same reasons it is wrong. It replays the command stream the graph actually
///         emitted, maintains its own idea of what state each resource is in from the barriers alone,
///         and asserts the property that matters: <b>at the moment a pass runs, every resource it
///         declared is in the state it declared.</b>
///     </para>
///     <para>
///         That is the invariant a renderer depends on and the one a driver punishes. It cannot be
///         satisfied by a planner that agrees with a buggy reference, because the reference here is
///         the declaration itself.
///     </para>
/// </remarks>
public sealed class RenderGraphPropertyTests {
    static readonly ResourceState[] WriteStates = [
        ResourceState.ColourTarget,
        ResourceState.ShaderWrite,
        ResourceState.CopyDestination,
        ResourceState.DepthStencilWrite
    ];

    static readonly ResourceState[] ReadStates = [
        ResourceState.ShaderRead,
        ResourceState.CopySource,
        ResourceState.UniformRead,
        ResourceState.VertexInput
    ];

    /// <summary>
    ///     Generates graphs that are legal by construction: a read only ever names a resource an
    ///     earlier pass wrote, because reading undeclared memory is a validation error and the
    ///     generator should be exercising the planner, not the validator.
    /// </summary>
    static Gen<GraphSpec> Graphs =>
        from resourceCount in Gen.Int[2, 6]
        from passCount in Gen.Int[1, 8]
        from seed in Gen.Int[0, int.MaxValue]
        select Build(resourceCount, passCount, seed);

    static GraphSpec Build(int resourceCount, int passCount, int seed) {
        // A small deterministic PRNG rather than Random: the seed is part of the generated case, so a
        // failure shrinks to a seed that reproduces exactly.
        var state = (uint)seed | 1u;

        uint Next() {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        var written = new bool[resourceCount];
        var passes = new List<PassSpec>();

        for (var index = 0; index < passCount; index++) {
            var uses = new List<UseSpec>();
            var writes = (int)(Next() % 2) + 1;

            for (var write = 0; write < writes; write++) {
                var resource = (int)(Next() % (uint)resourceCount);

                if (uses.Any(use => use.Resource == resource)) {
                    continue;
                }

                uses.Add(new(resource, WriteStates[Next() % WriteStates.Length], true));
                written[resource] = true;
            }

            var available = Enumerable.Range(0, resourceCount)
                .Where(resource => written[resource] && uses.All(use => use.Resource != resource))
                .ToArray();

            if (available.Length > 0) {
                var reads = (int)(Next() % 2);

                for (var read = 0; read < reads; read++) {
                    var resource = available[Next() % (uint)available.Length];

                    if (uses.All(use => use.Resource != resource)) {
                        uses.Add(new(resource, ReadStates[Next() % ReadStates.Length], false));
                    }
                }
            }

            // The last pass always has a side effect, so the graph is not culled to nothing — which
            // is a legal outcome and a boring one to generate over and over.
            passes.Add(new($"p{index}", [.. uses], index == passCount - 1));
        }

        return new(resourceCount, [.. passes]);
    }

    /// <summary>
    ///     Every surviving pass sees every resource it declared in the state it declared.
    /// </summary>
    [Fact]
    public void EveryPassSeesTheStateItAskedFor() =>
        Graphs.Sample(spec => {
                using var device = new NullDevice();
                using var pool = new TransientResourcePool(device);
                var graph = new RenderGraph(device, pool);
                var list = new TrackingCommandList();

                var (textures, order) = Run(graph, pool, spec, list);

                // Replay: the tracker knows nothing about the graph's internals, only what the
                // command stream said.
                var tracked = new Dictionary<TextureHandle, ResourceState>();
                var barrier = 0;

                foreach (var step in order) {
                    if (step.IsBarrierGroup) {
                        while (barrier < list.Barriers.Count && list.Barriers[barrier].Group == step.Group) {
                            var observed = list.Barriers[barrier];
                            var before = tracked.GetValueOrDefault(observed.Texture, ResourceState.Undefined);

                            // Undefined as the *old* state is always legal and means "discard the
                            // contents", which is exactly what a resource taking over aliased memory
                            // wants. Demanding the true previous state there would ask the driver to
                            // preserve garbage.
                            if (observed.Before != ResourceState.Undefined && before != observed.Before) {
                                return false;
                            }

                            tracked[observed.Texture] = observed.After;
                            barrier++;
                        }

                        continue;
                    }

                    foreach (var use in spec.Passes[step.Pass].Uses) {
                        var texture = textures[use.Resource];

                        if (tracked.GetValueOrDefault(texture, ResourceState.Undefined) != use.State) {
                            return false;
                        }
                    }
                }

                return true;
            },
            iter: 2000
        );

    /// <summary>
    ///     Two resources given the same memory never coexist. An overlap here is memory corruption
    ///     that produces a plausible picture, which is the worst kind.
    /// </summary>
    [Fact]
    public void AliasedResourcesNeverOverlapInTime() =>
        Graphs.Sample(spec => {
                using var device = new NullDevice();
                using var pool = new TransientResourcePool(device);
                var graph = new RenderGraph(device, pool);
                var list = new TrackingCommandList();

                var (textures, order) = Run(graph, pool, spec, list);

                // Lifetime of each resource, in surviving-pass order, taken from the executed stream
                // rather than from the graph.
                var first = new Dictionary<int, int>();
                var last = new Dictionary<int, int>();
                var step = 0;

                foreach (var entry in order) {
                    if (entry.IsBarrierGroup) {
                        continue;
                    }

                    foreach (var use in spec.Passes[entry.Pass].Uses) {
                        first.TryAdd(use.Resource, step);
                        last[use.Resource] = step;
                    }

                    step++;
                }

                foreach (var (left, leftTexture) in textures) {
                    foreach (var (right, rightTexture) in textures) {
                        if (left >= right || leftTexture != rightTexture) {
                            continue;
                        }

                        if (!first.ContainsKey(left) || !first.ContainsKey(right)) {
                            continue;
                        }

                        // Sharing memory means the lifetimes are disjoint. Touching at a boundary is
                        // still an overlap: the pass that ends one is the pass that starts the other.
                        if (last[left] >= first[right] && last[right] >= first[left]) {
                            return false;
                        }
                    }
                }

                return true;
            },
            iter: 2000
        );

    /// <summary>
    ///     A culled pass never runs, and a surviving one always does — checked against culling worked
    ///     out independently, backwards from what leaves the graph.
    /// </summary>
    [Fact]
    public void CullingKeepsExactlyWhatIsReachableFromAnOutput() =>
        Graphs.Sample(spec => {
                using var device = new NullDevice();
                using var pool = new TransientResourcePool(device);
                var graph = new RenderGraph(device, pool);
                var list = new TrackingCommandList();

                var (_, order) = Run(graph, pool, spec, list);
                var ran = order.Where(step => !step.IsBarrierGroup).Select(step => step.Pass).ToHashSet();

                // Independently: walk backwards from every side-effect pass, keeping whatever wrote
                // something a kept pass reads.
                var needed = new HashSet<int>();

                for (var index = spec.Passes.Length - 1; index >= 0; index--) {
                    if (spec.Passes[index].SideEffect) {
                        needed.Add(index);
                    }
                }

                var changed = true;

                while (changed) {
                    changed = false;

                    for (var index = spec.Passes.Length - 1; index >= 0; index--) {
                        if (needed.Contains(index)) {
                            continue;
                        }

                        foreach (var use in spec.Passes[index].Uses) {
                            if (!use.IsWrite) {
                                continue;
                            }

                            for (var later = index + 1; later < spec.Passes.Length; later++) {
                                if (!needed.Contains(later)) {
                                    continue;
                                }

                                if (spec.Passes[later].Uses.Any(
                                        other => !other.IsWrite && other.Resource == use.Resource
                                    )) {
                                    needed.Add(index);
                                    changed = true;
                                    break;
                                }
                            }

                            if (needed.Contains(index)) {
                                break;
                            }
                        }
                    }
                }

                return ran.SetEquals(needed);
            },
            iter: 2000
        );

    /// <summary>
    ///     Whatever the graph, no barrier ever claims a resource was in a state it was not. A lying
    ///     <c>Before</c> is undefined behaviour on Vulkan and a wrong layout on D3D12.
    /// </summary>
    [Fact]
    public void NoBarrierMisstatesWhatItIsTransitioningFrom() =>
        Graphs.Sample(spec => {
                using var device = new NullDevice();
                using var pool = new TransientResourcePool(device);
                var graph = new RenderGraph(device, pool);
                var list = new TrackingCommandList();

                Run(graph, pool, spec, list);

                var tracked = new Dictionary<TextureHandle, ResourceState>();

                foreach (var barrier in list.Barriers) {
                    // Undefined is the discard, and is legal from any state. Anything else is a claim
                    // about the past, and a false one is undefined behaviour on Vulkan and a wrong
                    // layout on D3D12.
                    if (barrier.Before != ResourceState.Undefined
                        && tracked.GetValueOrDefault(barrier.Texture, ResourceState.Undefined) != barrier.Before) {
                        return false;
                    }

                    tracked[barrier.Texture] = barrier.After;
                }

                return true;
            },
            iter: 2000
        );

    /// <summary>One step of the executed stream: a barrier group, or a pass.</summary>
    readonly record struct Step(bool IsBarrierGroup, int Group, int Pass);

    /// <summary>Builds and runs a generated graph, returning what actually happened.</summary>
    static (Dictionary<int, TextureHandle> Textures, List<Step> Order) Run(
        RenderGraph graph,
        TransientResourcePool pool,
        in GraphSpec spec,
        TrackingCommandList list
    ) {
        var handles = new GraphTexture[spec.ResourceCount];

        for (var index = 0; index < spec.ResourceCount; index++) {
            handles[index] = graph.CreateTexture(new(
                PixelFormat.Rgba8UNorm,
                64,
                64,
                TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.Storage
                | TextureUsage.CopySource | TextureUsage.CopyDestination,
                Name: $"r{index}"
            ));
        }

        var order = new List<Step>();

        for (var index = 0; index < spec.Passes.Length; index++) {
            var pass = spec.Passes[index];
            var passIndex = index;

            graph.AddPass(pass.Name, builder => {
                foreach (var use in pass.Uses) {
                    if (use.IsWrite) {
                        builder.Writes(handles[use.Resource], use.State);
                    } else {
                        builder.Reads(handles[use.Resource], use.State);
                    }
                }

                if (pass.SideEffect) {
                    builder.SideEffect();
                }

                builder.Execute(_ => order.Add(new(false, -1, passIndex)));
            });
        }

        var groups = 0;
        graph.Compile();

        // The order the tracker sees is barrier groups interleaved with passes, which is what the
        // replay needs and what the command list alone cannot say.
        var probe = new OrderProbe(list, () => order.Add(new(true, groups++, -1)));
        graph.Execute(probe);

        var textures = new Dictionary<int, TextureHandle>();

        for (var index = 0; index < spec.ResourceCount; index++) {
            var handle = graph.TextureOf(handles[index]);

            if (handle.IsValid) {
                textures[index] = handle;
            }
        }

        return (textures, order);
    }

    /// <summary>Forwards to the tracker and notes where each barrier group fell in the stream.</summary>
    sealed class OrderProbe(TrackingCommandList inner, Action onBarrier) : ICommandList {
        public QueueKind Kind => inner.Kind;

        public bool IsRecorded => inner.IsRecorded;

        public void Finish() => inner.Finish();

        public void Barrier(in BarrierGroup barriers) {
            if (barriers.IsEmpty) {
                return;
            }

            onBarrier();
            inner.Barrier(barriers);
        }

        public void BeginRenderPass(in RenderPassDescription description) => inner.BeginRenderPass(description);

        public void EndRenderPass() => inner.EndRenderPass();

        public void SetViewport(in Core.Mathematics.Viewport viewport) { }

        public void SetScissor(in ScissorRect scissor) { }

        public void SetBlendConstant(in Core.Mathematics.Color4 colour) { }

        public void SetStencilReference(uint reference) { }

        public void BindPipeline(PipelineHandle pipeline) { }

        public void BindDescriptorSet(
            DescriptorSetSlot slot,
            DescriptorSetHandle descriptors,
            ReadOnlySpan<uint> dynamicOffsets = default
        ) { }

        public void PushConstants(ShaderStage stages, int offset, ReadOnlySpan<byte> data) { }

        public void BindVertexBuffer(int slot, BufferHandle buffer, long offset = 0) { }

        public void BindIndexBuffer(
            BufferHandle buffer,
            IndexFormat format = IndexFormat.UInt16,
            long offset = 0
        ) { }

        public void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0) { }

        public void DrawIndexed(
            int indexCount,
            int instanceCount = 1,
            int firstIndex = 0,
            int vertexOffset = 0,
            int firstInstance = 0
        ) { }

        public void DrawIndexedIndirect(
            BufferHandle arguments,
            long offset = 0,
            int drawCount = 1,
            int stride = 20
        ) { }

        public void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1) { }

        public void DispatchIndirect(BufferHandle arguments, long offset = 0) { }

        public void CopyBuffer(
            BufferHandle source,
            long sourceOffset,
            BufferHandle destination,
            long destinationOffset,
            long size
        ) { }

        public void CopyBufferToTexture(
            BufferHandle source,
            long sourceOffset,
            in TextureRegion destination,
            Core.Mathematics.Int3 size
        ) { }

        public void CopyTextureToBuffer(
            in TextureRegion source,
            Core.Mathematics.Int3 size,
            BufferHandle destination,
            long destinationOffset
        ) { }

        public void CopyTexture(
            in TextureRegion source,
            in TextureRegion destination,
            Core.Mathematics.Int3 size
        ) { }

        public void PushDebugGroup(string name) { }

        public void PopDebugGroup() { }

        public void InsertDebugMarker(string name) { }

        public void Dispose() { }
    }
}

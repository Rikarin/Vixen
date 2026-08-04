// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering;

/// <summary>
///     One draw for every cluster the traversal chose: phase 4's hardware-raster visibility buffer.
/// </summary>
/// <remarks>
///     <para>
///         <b>One command, and the host never learns how many clusters it covers.</b> The draw is a
///         single instanced <c>DrawIndexedIndirect</c> whose instance count is copied out of the visible
///         list's own count word — so a frame that drew a million clusters and a frame that drew none
///         are the same command with the same arguments, differing in four bytes the device wrote.
///     </para>
///     <para>
///         <b>Indirect, and not <see cref="ICommandList.DrawIndexedIndirectCount" />.</b> That entry
///         point exists for a <em>list</em> of argument structures whose length the device decides, which
///         is what a compacted per-object draw needs. Here there is one structure and the device decides
///         its <c>instanceCount</c> — so a four-byte copy into the argument buffer does the whole job,
///         and the pass needs neither <c>HasDrawIndirectCount</c> nor <c>HasMultiDrawIndirect</c>. That
///         is worth having: it makes the visibility buffer reachable on every target the RHI supports
///         rather than on the ones with the newer feature.
///     </para>
///     <para>
///         <b>The index buffer is a straight run and holds no mesh's indices.</b> Every cluster is drawn
///         with the same index count — three times the most triangles any registered cluster holds — and
///         the indices are <c>0, 1, 2, …</c>, so <c>SV_VertexID</c> reaches the vertex stage unchanged.
///         The real corners are bytes inside a page, which the vertex stage fetches once it knows which
///         cluster it is drawing. A cluster with fewer triangles has corners left over and they collapse
///         to a degenerate triangle; the alternative is one argument structure per cluster and a
///         multi-draw, which is the thing this arrangement exists to avoid.
///     </para>
///     <para>
///         <b>The output is one <c>uint</c> per pixel, not two.</b>
///         <c>docs/plan/22-virtualized-geometry.md</c> phase 4 says <c>RG32_UINT</c> — a cluster index and a
///         triangle index in separate channels — and this packs both into
///         <see cref="PixelFormat.R32UInt" />: twenty-five bits of visible-list slot and seven of
///         triangle. Half the bandwidth of a full-screen target that every resolve pass reads, and the
///         bit budget is not tight: a cluster holds a hundred and twenty-eight triangles by
///         construction, and thirty-three million drawn clusters is far beyond what a frame reaches. It
///         also means no new pixel format in three backends. <see cref="MaximumSlots" /> is the bound,
///         and it is checked rather than assumed.
///     </para>
/// </remarks>
public sealed class GpuClusterRaster : IDisposable {
    /// <summary>How many bits of the identity word name the triangle within its cluster.</summary>
    /// <remarks>
    ///     Seven, for the hundred and twenty-eight triangles a cluster is built to hold — see
    ///     <c>MeshletBuildSettings.MaxTriangles</c>. <c>Raster.TriangleBits</c> in the shader.
    /// </remarks>
    public const int TriangleBits = 7;

    /// <summary>The most triangles a cluster may hold and still be addressable by a pixel.</summary>
    public const uint MaximumTriangles = 1u << TriangleBits;

    /// <summary>The most entries the visible list may hold and still be addressable by a pixel.</summary>
    /// <remarks>
    ///     ⚠ A bound on the <em>list</em> and not on the accepted count, since phase 6 put the software
    ///     raster's entries at the far end of it — so the highest slot a frame packs is the buffer's
    ///     length rather than what the traversal found. <see cref="GpuClusterVisibility" /> caps the
    ///     allocation here rather than letting a slot wrap into a triangle index.
    /// </remarks>
    public const int MaximumSlots = (1 << (32 - TriangleBits)) - 1;

    /// <summary>What a pixel nothing covered holds.</summary>
    /// <remarks>
    ///     Zero, and the visible-list slot is stored one higher than it is so that zero can mean this.
    ///     Clearing to all ones would be the obvious alternative and is not expressible: a clear colour
    ///     is four floats in every API the RHI wraps, and no float's bits are <c>0xFFFFFFFF</c> in a way
    ///     that survives being read as one. The bias costs an increment where a pixel is written and a
    ///     decrement where it is read. <c>Raster.Nothing</c> in the shader.
    /// </remarks>
    public const uint Nothing = 0u;

    /// <summary>One pixel's identity, as the shader packs it.</summary>
    /// <param name="slot">Which visible-list slot.</param>
    /// <param name="triangle">Which of that cluster's triangles.</param>
    /// <remarks>
    ///     The host mirror of <c>Raster.Pack</c>, so a resolve written in C# — or a test reading a
    ///     rendered frame back — agrees with the raster about what a pixel means rather than deriving the
    ///     packing a second time.
    /// </remarks>
    public static uint Pack(uint slot, uint triangle) =>
        ((slot + 1u) << TriangleBits) | (triangle & (MaximumTriangles - 1u));

    /// <summary>Whether anything drew to a pixel at all.</summary>
    public static bool Covered(uint identity) => identity != Nothing;

    /// <summary>Which visible-list slot a pixel names. Meaningless unless <see cref="Covered" />.</summary>
    public static uint Slot(uint identity) => (identity >> TriangleBits) - 1u;

    /// <summary>Which triangle of that cluster it names.</summary>
    public static uint Triangle(uint identity) => identity & (MaximumTriangles - 1u);

    /// <summary>The format the visibility buffer is.</summary>
    public const PixelFormat Format = PixelFormat.R32UInt;

    readonly IGraphicsDevice device;
    readonly Dictionary<(Effect, PixelFormat, PixelFormat), PipelineHandle> pipelines = [];
    readonly DescriptorWrite[] writes = new DescriptorWrite[8];

    DescriptorSetHandle[] descriptors = [];
    int ring;

    BufferHandle indices;
    BufferHandle arguments;
    BufferHandle staging;
    BufferHandle seed;
    bool pendingIndices;
    int indexCount;

    Effect? allocatedFor;
    bool disposed;

    const DescriptorSetSlot SetSlot = (DescriptorSetSlot)ClusterRasterKeys.VisibleSet;

    /// <summary>Creates a raster that draws on a device.</summary>
    /// <param name="device">The device.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    public GpuClusterRaster(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);
        this.device = device;
    }

    /// <summary>Where the raster variant is resolved from. Null draws nothing.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Where the shader modules come from. Null draws nothing.</summary>
    public Compositor.EffectPipelineDescriber? Modules { get; set; }

    /// <summary>The traversal whose answer to draw. Null draws nothing.</summary>
    public GpuClusterVisibility? Visibility { get; set; }

    /// <summary>The pool the geometry is read out of. Null draws nothing.</summary>
    public MeshletPagePool? Pages { get; set; }

    /// <summary>Whether the last <see cref="Record" /> issued the draw.</summary>
    public bool Drawn { get; private set; }

    /// <summary>How many indices one cluster's draw covers.</summary>
    /// <remarks>
    ///     Three times the most triangles any registered cluster holds, which is what makes the whole
    ///     frame one command. Zero before the first mesh is registered.
    /// </remarks>
    public int IndexCount => indexCount;

    /// <summary>The effect key selecting the raster.</summary>
    /// <remarks>
    ///     No permutations, and that is the shape of the pass: it writes an identity, so it has nothing
    ///     to be conditional about. Every variation phase 5 will need is a variation of the
    ///     <em>resolve</em>, which is a different shader reading this one's output.
    /// </remarks>
    public static EffectKey Key =>
        EffectKey.Of(ClusterRasterKeys.ShaderName, [], Materials.MaterialCompiler.PassComposition());

    /// <summary>
    ///     Records the one draw that covers every cluster the traversal accepted.
    /// </summary>
    /// <param name="list">An open command list, inside a render pass with a colour and a depth target.</param>
    /// <param name="viewProjection">The camera the clusters are projected with.</param>
    /// <param name="colour">The visibility buffer's format.</param>
    /// <param name="depth">The depth target's format.</param>
    /// <returns>Whether the draw was recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         The count copy is recorded here rather than by the traversal, because it is the reader's
    ///         business: what the traversal produced is a list with its own length, and turning that
    ///         length into an <em>instance count</em> is a fact about how this pass chose to draw it.
    ///     </para>
    ///     <para>
    ///         ⚠ This runs <em>inside</em> the caller's render pass, so the copy has to have happened
    ///         before it — a buffer copy is not legal between <c>BeginRenderPass</c> and
    ///         <c>EndRenderPass</c>. <see cref="Prepare" /> is where it goes, it is a separate call for
    ///         exactly that reason, and <see cref="Compositor.ClusterCullingRenderer" /> is what calls it
    ///         at the one point in the frame that qualifies.
    ///     </para>
    /// </remarks>
    public bool Record(ICommandList list, in Matrix4x4 viewProjection, PixelFormat colour, PixelFormat depth) {
        ArgumentNullException.ThrowIfNull(list);
        ObjectDisposedException.ThrowIf(disposed, this);

        Drawn = false;

        if (!Ready(out var visibility, out var pages, out var effect) || indexCount == 0) {
            return false;
        }

        var pipeline = PipelineFor(effect, colour, depth);

        if (!pipeline.IsValid) {
            return false;
        }

        Bind(visibility, pages, viewProjection);

        list.BindPipeline(pipeline);
        list.BindDescriptorSet(SetSlot, descriptors[ring]);
        list.BindIndexBuffer(indices, IndexFormat.UInt32);
        list.DrawIndexedIndirect(arguments);

        Drawn = true;

        return true;
    }

    /// <summary>
    ///     Makes the buffers, and copies this frame's cluster count into the draw's arguments.
    /// </summary>
    /// <param name="list">An open command list, outside a render pass.</param>
    /// <returns>Whether there is anything for <see cref="Record" /> to draw.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> is null.</exception>
    /// <remarks>
    ///     Two calls rather than one because a buffer copy is not legal inside a render pass and a draw
    ///     is not legal outside one — see <see cref="Record" />. Nothing here reads the count on the
    ///     host: the copy is device-to-device and the number stays where the traversal wrote it.
    /// </remarks>
    public bool Prepare(ICommandList list) {
        ArgumentNullException.ThrowIfNull(list);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!Ready(out var visibility, out _, out var effect) || !EnsureBuffers(visibility) || !EnsureDescriptors(effect)) {
            return false;
        }

        // The index run and the argument template, staged into their device-local homes. Both are
        // written by the host and read by the device, and a device-local buffer cannot be host-written —
        // which no recording backend ever says, so this arrived as an exception the first time the frame
        // ran on Vulkan. See GpuClusterVisibility's mesh records, which are staged for the same reason.
        Upload(list);

        ring = (ring + 1) % Math.Max(descriptors.Length, 1);

        // Element zero of the visible list is the count the traversal appended to, and the instance
        // count is the second word of an indirect draw's arguments. So the whole of "draw what the
        // device decided" is a four-byte copy between two device-local buffers.
        //
        // The source is transitioned here and not by the traversal — its Record leaves the list in
        // ShaderRead and says the reader of the count is the side that knows. This is that reader:
        // without the round trip through CopySource the copy is not ordered after the dispatch's
        // writes, and an instance count read a hair early is this frame's draw issued with last
        // frame's count — which on a first frame is zero, for ever, and reads as a raster that works
        // and covers nothing.
        list.Barrier(
            new(
                [
                    new(arguments, ResourceState.IndirectArgument, ResourceState.CopyDestination),
                    new(visibility.Visible, ResourceState.ShaderRead, ResourceState.CopySource)
                ],
                []
            )
        );

        list.CopyBuffer(visibility.Visible, 0, arguments, sizeof(uint), sizeof(uint));

        list.Barrier(
            new(
                [
                    new(arguments, ResourceState.CopyDestination, ResourceState.IndirectArgument),
                    new(visibility.Visible, ResourceState.CopySource, ResourceState.ShaderRead)
                ],
                []
            )
        );

        return true;
    }

    bool Ready(
        out GpuClusterVisibility visibility,
        out MeshletPagePool pages,
        out Effect effect
    ) {
        visibility = null!;
        pages = null!;
        effect = null!;

        if (Visibility is not { MeshCount: > 0 } candidate || Pages is null || Effects is null || Modules is null) {
            return false;
        }

        // The traversal's own buffers, which exist once it has prepared a frame. A raster asked to draw
        // before the traversal ever ran has nothing to copy a count out of — which is the first frame of
        // an application whose meshes registered after the compositor was built, and is a frame that
        // draws nothing rather than an error.
        if (!candidate.Visible.IsValid) {
            return false;
        }

        if (Effects.Resolve(Key) is not { IsPlaceholder: false } resolved) {
            return false;
        }

        visibility = candidate;
        pages = Pages;
        effect = resolved;

        return true;
    }

    void Bind(GpuClusterVisibility visibility, MeshletPagePool pages, in Matrix4x4 viewProjection) {
        writes[0] = DescriptorWrite.Storage(ClusterRasterKeys.VisibleBinding, visibility.Visible);

        // Bound whole and indexed from a base, rather than bound at this frame's offset — which the
        // raster could do and the resolve cannot, because EffectSetWriter binds a storage buffer whole
        // and has nowhere to put an offset. Two passes that have to agree about a transform must reach
        // it the same way, and a descriptor offset here would have been right in the raster and a frame
        // stale in the resolve. See GpuClusterVisibility.InstanceBase.
        writes[1] = DescriptorWrite.Storage(ClusterRasterKeys.InstancesBinding, visibility.Instances);
        writes[2] = DescriptorWrite.Storage(ClusterRasterKeys.GeometryBinding, visibility.Geometry);
        writes[3] = DescriptorWrite.Storage(ClusterRasterKeys.MeshesBinding, visibility.Grids);
        writes[4] = DescriptorWrite.Storage(ClusterRasterKeys.ResidencyBinding, visibility.Slots);
        writes[5] = DescriptorWrite.Storage(ClusterRasterKeys.PagesBinding, pages.Pages);
        writes[6] = DescriptorWrite.Storage(ClusterRasterKeys.BonesBinding, visibility.Bones);

        // The uniform block, which holds the page size and the camera. Written into its own ring region
        // for the reason every per-frame block is: the previous frame may still be reading the last one.
        constants ??= new(device, "ClusterRaster.Constants");

        var parameters = new ParameterCollection();
        parameters.Set(ClusterRasterKeys.PageSize, (uint)visibility.PageSize);
        parameters.Set(ClusterRasterKeys.InstanceBase, (uint)visibility.InstanceBase);
        parameters.Set(ClusterRasterKeys.BoneBase, (uint)visibility.BoneBase);
        parameters.Set(ClusterRasterKeys.ResidencyBase, (uint)visibility.SlotBase);
        parameters.Set(ClusterRasterKeys.ViewProjection, viewProjection);

        var updated = constants.Update(EffectOf(), parameters);

        writes[7] = updated
            ? DescriptorWrite.Uniform(
                ClusterRasterKeys.ConstantBufferBinding,
                constants.Buffer,
                constants.Offset,
                constants.Size
            )
            : default;

        device.UpdateDescriptorSet(descriptors[ring], updated ? writes : writes.AsSpan(0, 7));
    }

    EffectConstants? constants;

    Effect EffectOf() => allocatedFor!;

    /// <summary>The straight index run and the one argument structure the draw reads.</summary>
    /// <remarks>
    ///     <para>
    ///         Rebuilt only when the largest cluster changes, which happens when a mesh is registered.
    ///         Every other frame binds what is already there.
    ///     </para>
    ///     <para>
    ///         The static fields of the argument structure are written once from the host and the
    ///         instance count is left at zero, so a frame recorded before any <see cref="Prepare" />
    ///         draws nothing rather than drawing whatever the memory happened to hold.
    ///     </para>
    /// </remarks>
    bool EnsureBuffers(GpuClusterVisibility visibility) {
        var wanted = Math.Max(visibility.MaximumTriangles, 1) * 3;

        if (wanted > MaximumTriangles * 3) {
            throw new InvalidOperationException(
                $"A registered cluster holds {visibility.MaximumTriangles} triangles and a pixel can name "
                + $"{MaximumTriangles}. Rebuild the meshes with a smaller MeshletBuildSettings.MaxTriangles, "
                + "or widen GpuClusterRaster.TriangleBits and Raster.TriangleBits together."
            );
        }

        if (indices.IsValid && indexCount == wanted) {
            return true;
        }

        if (indices.IsValid) {
            device.Destroy(indices);
        }

        var run = new uint[wanted];

        for (var i = 0; i < run.Length; i++) {
            run[i] = (uint)i;
        }

        indices = device.CreateBuffer(
            new(
                (long)run.Length * sizeof(uint),
                BufferUsage.Index | BufferUsage.CopyDestination,
                MemoryAccess.DeviceLocal,
                "ClusterRaster.Indices"
            )
        );

        // Written into host memory now and copied on the next Prepare, because the index buffer is
        // device-local: an input assembler reads it once per vertex of every cluster in the frame, which
        // is the one buffer here that has any business being in device memory.
        if (staging.IsValid) {
            device.Destroy(staging);
        }

        staging = device.CreateBuffer(
            new(
                (long)run.Length * sizeof(uint),
                BufferUsage.CopySource,
                MemoryAccess.HostUpload,
                "ClusterRaster.Staging"
            )
        );

        if (staging.IsValid) {
            device.Write(staging, 0, MemoryMarshal.AsBytes<uint>(run));
        }

        pendingIndices = true;

        if (!arguments.IsValid) {
            arguments = device.CreateBuffer(
                new(
                    Unsafe.SizeOf<DrawCommand>(),
                    BufferUsage.Indirect | BufferUsage.CopyDestination,
                    MemoryAccess.DeviceLocal,
                    "ClusterRaster.Arguments"
                )
            );
        }

        // The argument template, on the same terms: device-local because DrawIndexedIndirect reads it,
        // and seeded through host memory rather than written into.
        if (seed.IsValid) {
            device.Destroy(seed);
        }

        seed = device.CreateBuffer(
            new(
                Unsafe.SizeOf<DrawCommand>(),
                BufferUsage.CopySource,
                MemoryAccess.HostUpload,
                "ClusterRaster.ArgumentSeed"
            )
        );

        if (seed.IsValid) {
            device.Write(
                seed,
                0,
                MemoryMarshal.AsBytes<DrawCommand>([new() { IndexCount = (uint)wanted, InstanceCount = 0 }])
            );
        }

        indexCount = wanted;

        return indices.IsValid && arguments.IsValid;
    }

    /// <summary>Copies the index run and the argument template into their device-local buffers.</summary>
    /// <remarks>
    ///     Once per registration rather than once per frame: the run is <c>0, 1, 2, …</c> and the
    ///     template's index count both change only when a mesh with larger clusters is registered.
    /// </remarks>
    void Upload(ICommandList list) {
        if (!pendingIndices) {
            return;
        }

        pendingIndices = false;

        if (staging.IsValid && indices.IsValid) {
            list.Barrier(new([new(indices, ResourceState.Undefined, ResourceState.CopyDestination)], []));
            list.CopyBuffer(staging, 0, indices, 0, (long)indexCount * sizeof(uint));
            list.Barrier(new([new(indices, ResourceState.CopyDestination, ResourceState.VertexInput)], []));
        }

        if (seed.IsValid && arguments.IsValid) {
            list.Barrier(new([new(arguments, ResourceState.Undefined, ResourceState.CopyDestination)], []));
            list.CopyBuffer(seed, 0, arguments, 0, Unsafe.SizeOf<DrawCommand>());
            list.Barrier(new([new(arguments, ResourceState.CopyDestination, ResourceState.IndirectArgument)], []));
        }
    }

    bool EnsureDescriptors(Effect effect) {
        if (ReferenceEquals(allocatedFor, effect) && descriptors.Length > 0) {
            return true;
        }

        foreach (var set in descriptors) {
            if (set.IsValid) {
                device.Destroy(set);
            }
        }

        var count = Math.Max(device.FramesInFlight, 1);
        descriptors = new DescriptorSetHandle[count];

        for (var index = 0; index < count; index++) {
            descriptors[index] = device.CreateDescriptorSet(effect.SetLayouts[(int)SetSlot], "ClusterRaster");

            if (!descriptors[index].IsValid) {
                return false;
            }
        }

        allocatedFor = effect;
        ring = 0;

        return true;
    }

    /// <summary>The pipeline: no vertex buffers, ordinary depth, and a single integer target.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Ordinary depth test and depth write</b>, which is the whole reason phase 4 needs no
    ///         atomics: the hardware resolves which cluster is nearest, and the identity riding along
    ///         with it is whatever survived the test. Phase 6's software raster has to do that itself
    ///         with a sixty-four-bit <c>atomicMax</c>, which is why it is the accelerator and this is the
    ///         baseline.
    ///     </para>
    ///     <para>
    ///         <b>Nothing is culled by winding.</b> A cluster's triangles keep the source mesh's winding,
    ///         and the normal cone has already rejected the clusters that face away as a whole — but a
    ///         cluster that survived may still contain back-facing triangles, and the resolve reads the
    ///         nearest surface either way. Culling here would be a second opinion about facing, disagreeing
    ///         with the cone at the silhouette.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Creating this pipeline on MoltenVK logs a blend warning that is not real.</b>
    ///         <i>"Blending is enabled for attachment with format VK_FORMAT_R32_UINT, which does not
    ///         support it"</i> — but blending is not enabled; MoltenVK's guard warns for every
    ///         unblendable colour format without reading <c>blendEnable</c> (its
    ///         <c>MVKPipeline.mm</c>, at least through v1.4.2). Verified against the create-info and
    ///         bisected in <c>docs/rhi-backend-mapping.md</c> § 5, which is the long form of this note.
    ///     </para>
    /// </remarks>
    PipelineHandle PipelineFor(Effect effect, PixelFormat colour, PixelFormat depth) {
        var key = (effect, colour, depth);

        if (pipelines.TryGetValue(key, out var existing)) {
            return existing;
        }

        var vertex = Modules!.ModuleOf(effect, ShaderStage.Vertex);

        if (!vertex.IsValid) {
            pipelines[key] = PipelineHandle.Null;
            return PipelineHandle.Null;
        }

        var created = device.CreateGraphicsPipeline(
            new(
                vertex,
                Modules.ModuleOf(effect, ShaderStage.Fragment),
                effect.Layout,
                [new(colour, BlendState.Opaque)],
                null,
                PrimitiveTopology.TriangleList,
                RasterizerState.TwoSided,
                DepthStencilState.Default,
                depth,
                1,
                "ClusterRaster"
            )
        );

        pipelines[key] = created;

        return created;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var pipeline in pipelines.Values) {
            if (pipeline.IsValid) {
                device.Destroy(pipeline);
            }
        }

        pipelines.Clear();

        foreach (var set in descriptors) {
            if (set.IsValid) {
                device.Destroy(set);
            }
        }

        descriptors = [];

        if (indices.IsValid) {
            device.Destroy(indices);
            indices = default;
        }

        if (arguments.IsValid) {
            device.Destroy(arguments);
            arguments = default;
        }

        if (staging.IsValid) {
            device.Destroy(staging);
            staging = default;
        }

        if (seed.IsValid) {
            device.Destroy(seed);
            seed = default;
        }

        constants?.Dispose();
        constants = null;
    }
}

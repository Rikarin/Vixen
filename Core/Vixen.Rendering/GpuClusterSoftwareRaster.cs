// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering;

/// <summary>
///     Phase 6: the clusters the traversal thought too small for the hardware, rasterized in compute.
/// </summary>
/// <remarks>
///     <para>
///         <b>An accelerator and not a path.</b> A hardware rasterizer shades in quads, so a triangle
///         covering less than one wastes roughly four fifths of the fragments it launches — and a
///         virtualized cut is <em>chosen</em> to put a cluster's error under a pixel, which is exactly
///         the regime where that happens. Above a few pixels a triangle the fixed-function path wins by
///         a lot, which is why the routing threshold is a number a host measures and why zero is its
///         default. <c>docs/plan/22-virtualized-geometry.md</c> phase 6 says as much: gated on a
///         measurement, not on a plan.
///     </para>
///     <para>
///         <b>Capability-gated, and the gate is upstream of this class.</b> Resolving which surface is
///         nearest without a depth attachment means a 64-bit <c>atomicMax</c>, which is optional on
///         Vulkan, SM6.6 on D3D12 and absent from WebGPU. <see cref="GpuClusterVisibility" /> forces
///         every view's threshold to zero on a device without
///         <see cref="GraphicsDeviceFeatures.HasInt64Atomics" />, so the traversal routes nothing here
///         and this records nothing — one answer to "is the software path on", rather than two that
///         have to agree.
///     </para>
///     <para>
///         <b>Two dispatches, and the second is what makes one picture.</b> The raster resolves among
///         <em>itself</em> in a packed depth-above-identity buffer, because a compute pass cannot write
///         the depth attachment the hardware raster used. The merge then asks, per pixel, whether what
///         it found is nearer than what that draw left behind — so the ordering between the two rasters
///         comes out of a real depth comparison rather than out of which one ran last. It also clears
///         the packed buffer as it reads it, which is the whole cost of clearing it: a screen of 64-bit
///         words is sixteen megabytes at 1080p, and copying that in every frame would cost more than
///         the raster it serves.
///     </para>
///     <para>
///         ⚠ <b>It must be recorded after the hardware raster's render pass and before the binning.</b>
///         The first because it reads that pass's depth and overwrites its identities; the second
///         because <see cref="GpuVisibilityTiles" /> bins what is in the identity buffer. Nothing here
///         can check either — an open command list does not say what has been recorded into it — which
///         is why <see cref="Compositor.VisibilityBufferRenderer" /> owns the ordering, exactly as it
///         owns the binning's.
///     </para>
/// </remarks>
public sealed class GpuClusterSoftwareRaster : IDisposable {
    /// <summary>How many pixels one merge workgroup covers.</summary>
    public const int MergeGroupSize = 64;

    /// <summary>What a depth is multiplied by to become the top half of the packed word.</summary>
    /// <remarks>
    ///     <c>Software.DepthScale</c>. Two to the thirty-two less two hundred and fifty-six, which is
    ///     the largest multiple of 256 below <c>2^32</c> and therefore exactly representable as a
    ///     <c>float</c> — the obvious <c>4294967295</c> is not, rounds up to <c>2^32</c>, and converting
    ///     that to a <c>uint</c> is undefined in both targets.
    /// </remarks>
    public const float DepthScale = 4294967040f;

    /// <summary>How many bytes one pixel of the packed buffer occupies.</summary>
    public const int PixelBytes = sizeof(ulong);

    /// <summary>One pixel's depth and identity as the word an <c>atomicMax</c> resolves.</summary>
    /// <param name="depth">Its device depth, reverse-Z, in <c>[0, 1]</c>.</param>
    /// <param name="identity">What <see cref="GpuClusterRaster.Pack" /> produced.</param>
    /// <remarks>
    ///     The host mirror of <c>Software.Key</c>, so a reference rasterizer written in C# — and the
    ///     tests that compare one against the definition — resolve visibility by the arithmetic the
    ///     device will, rather than by a second derivation of it.
    /// </remarks>
    public static ulong Key(float depth, uint identity) =>
        ((ulong)(uint)(Math.Clamp(depth, 0f, 1f) * DepthScale) << 32) | identity;

    /// <summary>The depth a key carries, back in the range a depth buffer holds.</summary>
    public static float DepthOf(ulong key) => (key >> 32) / DepthScale;

    /// <summary>The identity it carries.</summary>
    public static uint IdentityOf(ulong key) => (uint)key;

    readonly IGraphicsDevice device;
    readonly DescriptorWrite[] writes = new DescriptorWrite[13];

    DescriptorSetHandle[] descriptors = [];
    int ring;

    BufferHandle depths;
    BufferHandle arguments;
    BufferHandle seed;
    BufferHandle blank;
    Int2 sized;
    bool pendingClear;

    Effect? allocatedFor;
    EffectConstants? constants;
    bool disposed;

    const DescriptorSetSlot Slot = (DescriptorSetSlot)ClusterSoftwareRasterKeys.VisibleSet;

    /// <summary>Creates a software raster that runs on a device.</summary>
    /// <param name="device">The device.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    public GpuClusterSoftwareRaster(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);
        this.device = device;
    }

    /// <summary>Where the two variants are resolved from. Null does nothing.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Where the compute pipelines come from. Null does nothing.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>The traversal whose software-routed clusters to draw. Null does nothing.</summary>
    public GpuClusterVisibility? Visibility { get; set; }

    /// <summary>The pool the geometry is read out of. Null does nothing.</summary>
    public MeshletPagePool? Pages { get; set; }

    /// <summary>Whether this device can run the software raster at all.</summary>
    /// <remarks>
    ///     The same bit <see cref="GpuClusterVisibility" /> consults to zero a view's threshold, read
    ///     here so a host can report why a frame drew everything the hardware way — which is otherwise
    ///     indistinguishable from a threshold nobody set.
    /// </remarks>
    public bool Supported => device.Features.HasInt64Atomics;

    /// <summary>Whether the last <see cref="Record" /> dispatched the raster and the merge.</summary>
    public bool Rastered { get; private set; }

    /// <summary>How many pixels the packed buffer currently covers.</summary>
    public Int2 Size => sized;

    /// <summary>The effect key selecting one of the two variants.</summary>
    /// <param name="merge">Whether the merge rather than the raster.</param>
    /// <remarks>
    ///     One shader and a permutation, which is Raven's rule rather than a preference — a shader has
    ///     one compute stage. The fold is what keeps the raster free of the merge's full-screen indexing
    ///     and the merge free of the raster's page decode.
    /// </remarks>
    public static EffectKey Key(bool merge) =>
        EffectKey.Of(
            ClusterSoftwareRasterKeys.ShaderName,
            [new(MergeKey, merge ? "true" : "false")],
            Materials.MaterialCompiler.PassComposition()
        );

    /// <summary>The permutation selecting the merge.</summary>
    public static string MergeKey => "Merge";

    /// <summary>
    ///     Records the raster and the merge that lands it.
    /// </summary>
    /// <param name="list">An open command list, outside a render pass.</param>
    /// <param name="identities">The visibility buffer, as a storage image.</param>
    /// <param name="depth">The depth the hardware raster left, as a sampled texture.</param>
    /// <param name="viewProjection">The camera the clusters are projected with — the raster's own.</param>
    /// <param name="size">The screen, in pixels.</param>
    /// <returns>Whether anything was recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The raster's dispatch is indirect and the merge's is not</b>, which is the honest shape
    ///         of the two: how many clusters went to software is the device's answer and the host never
    ///         learns it, and how many pixels there are is the host's own and there is nothing to hide.
    ///     </para>
    ///     <para>
    ///         The count copy is a four-byte read out of the visible list's second counter word — the
    ///         same trade <see cref="GpuClusterRaster.Prepare" /> makes with the first.
    ///     </para>
    /// </remarks>
    public bool Record(
        ICommandList list,
        TextureViewHandle identities,
        TextureViewHandle depth,
        in Matrix4x4 viewProjection,
        Int2 size
    ) {
        ArgumentNullException.ThrowIfNull(list);
        ObjectDisposedException.ThrowIf(disposed, this);

        Rastered = false;

        if (!Supported
            || Effects is null
            || Pipelines is null
            || Visibility is not { MeshCount: > 0 } visibility
            || Pages is not { } pages
            || !visibility.Visible.IsValid
            || !identities.IsValid
            || !depth.IsValid
            || size.X <= 0
            || size.Y <= 0) {
            return false;
        }

        if (Effects.Resolve(Key(false)) is not { IsPlaceholder: false } raster
            || Effects.Resolve(Key(true)) is not { IsPlaceholder: false } merge) {
            return false;
        }

        var rasterPipeline = Pipelines.GetOrCreate(raster);
        var mergePipeline = Pipelines.GetOrCreate(merge);

        if (!rasterPipeline.IsValid || !mergePipeline.IsValid || !EnsureBuffers(size) || !EnsureDescriptors(raster)) {
            return false;
        }

        ring = (ring + 1) % Math.Max(descriptors.Length, 1);
        Bind(raster, visibility, pages, identities, depth, viewProjection, size);

        Clear(list);

        // Word one of the visible list is the software count, and the first word of a DispatchIndirect's
        // arguments is its x extent — so "dispatch over what the traversal routed here" is a four-byte
        // copy between two device-local buffers, exactly as the hardware raster's instance count is.
        list.Barrier(
            new(
                [
                    new(arguments, ResourceState.IndirectArgument, ResourceState.CopyDestination),
                    new(visibility.Visible, ResourceState.ShaderRead, ResourceState.CopySource)
                ],
                []
            )
        );

        list.CopyBuffer(visibility.Visible, sizeof(uint), arguments, 0, sizeof(uint));

        list.Barrier(
            new(
                [
                    new(arguments, ResourceState.CopyDestination, ResourceState.IndirectArgument),
                    new(visibility.Visible, ResourceState.CopySource, ResourceState.ShaderRead)
                ],
                []
            )
        );

        list.BindPipeline(rasterPipeline);
        list.BindDescriptorSet(Slot, descriptors[ring]);
        list.DispatchIndirect(arguments, 0);

        // The merge reads every word the raster's atomics wrote, so the two dispatches are ordered by a
        // barrier on the buffer rather than by being in the same queue — which orders them not at all.
        list.Barrier(new([new(depths, ResourceState.ShaderWrite, ResourceState.ShaderWrite)], []));

        list.BindPipeline(mergePipeline);
        list.BindDescriptorSet(Slot, descriptors[ring]);
        list.Dispatch(((size.X * size.Y) + MergeGroupSize - 1) / MergeGroupSize);

        Rastered = true;

        return true;
    }

    /// <summary>Zeroes the packed buffer, once, for the frame that is about to read it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Once per allocation and never per frame</b>, because the merge clears as it reads — it
    ///         visits every pixel anyway. What is left is the one frame nothing has merged yet, where
    ///         uninitialised device memory would resolve as a surface at whatever depth its bits
    ///         happened to name.
    ///     </para>
    ///     <para>
    ///         Copied from a blank of one megabyte rather than from a blank the size of the buffer: a
    ///         1080p screen of 64-bit words is sixteen of them, and sixteen copies out of a megabyte
    ///         once is cheaper in every way than sixteen megabytes of host memory that exists for one
    ///         frame in the life of a window size.
    ///     </para>
    /// </remarks>
    void Clear(ICommandList list) {
        if (!pendingClear || !blank.IsValid) {
            return;
        }

        pendingClear = false;

        var bytes = (long)sized.X * sized.Y * PixelBytes;

        list.Barrier(new([new(depths, ResourceState.Undefined, ResourceState.CopyDestination)], []));

        for (var at = 0L; at < bytes; at += BlankBytes) {
            list.CopyBuffer(blank, 0, depths, at, Math.Min(BlankBytes, bytes - at));
        }

        list.Barrier(new([new(depths, ResourceState.CopyDestination, ResourceState.ShaderWrite)], []));
    }

    /// <summary>How large the one-off blank is.</summary>
    const long BlankBytes = 1024 * 1024;

    void Bind(
        Effect effect,
        GpuClusterVisibility visibility,
        MeshletPagePool pages,
        TextureViewHandle identities,
        TextureViewHandle depth,
        in Matrix4x4 viewProjection,
        Int2 size
    ) {
        // Bound whole and indexed from a base, which is the arrangement the raster and the resolve
        // already share — see GpuClusterVisibility.InstanceBase. Three passes reading the same records
        // have to reach them the same way, and a descriptor offset in one of the three is that one
        // reading another frame's transforms.
        writes[0] = DescriptorWrite.Storage(ClusterSoftwareRasterKeys.VisibleBinding, visibility.Visible);
        writes[1] = DescriptorWrite.Storage(ClusterSoftwareRasterKeys.InstancesBinding, visibility.Instances);
        writes[2] = DescriptorWrite.Storage(ClusterSoftwareRasterKeys.GeometryBinding, visibility.Geometry);
        writes[3] = DescriptorWrite.Storage(ClusterSoftwareRasterKeys.MeshesBinding, visibility.Grids);
        writes[4] = DescriptorWrite.Storage(ClusterSoftwareRasterKeys.ResidencyBinding, visibility.Slots);
        writes[5] = DescriptorWrite.Storage(ClusterSoftwareRasterKeys.PagesBinding, pages.Pages);
        writes[6] = DescriptorWrite.Storage(ClusterSoftwareRasterKeys.BonesBinding, visibility.Bones);
        writes[7] = DescriptorWrite.Storage(ClusterSoftwareRasterKeys.DepthsBinding, depths);
        writes[8] = DescriptorWrite.Texture(ClusterSoftwareRasterKeys.HardwareDepthBinding, depth);

        // ⚠ A storage image and not a texture, which is a distinct DescriptorWrite: the two map to the
        // same Vulkan write only by accident, and no driver checks which one the shader was compiled
        // for. Phase 5 found that the hard way — see EffectSetWriter.
        writes[9] = DescriptorWrite.StorageImage(ClusterSoftwareRasterKeys.IdentitiesBinding, identities);

        // The blend shapes, on the raster's terms: the tables are per mesh and written once at
        // registration, the weights per instance and rewritten every frame. This raster places the same
        // vertex the hardware one does, so it morphs it by the same call on the same bytes.
        writes[10] = DescriptorWrite.Storage(ClusterSoftwareRasterKeys.MorphsBinding, visibility.Morphs);
        writes[11] = DescriptorWrite.Storage(ClusterSoftwareRasterKeys.MorphWeightsBinding, visibility.MorphWeights);

        constants ??= new(device, "ClusterSoftwareRaster.Constants");

        var parameters = new ParameterCollection();
        parameters.Set(ClusterSoftwareRasterKeys.PageSize, (uint)visibility.PageSize);
        parameters.Set(ClusterSoftwareRasterKeys.InstanceBase, (uint)visibility.InstanceBase);
        parameters.Set(ClusterSoftwareRasterKeys.BoneBase, (uint)visibility.BoneBase);
        parameters.Set(ClusterSoftwareRasterKeys.ResidencyBase, (uint)visibility.SlotBase);
        parameters.Set(ClusterSoftwareRasterKeys.MorphWeightBase, (uint)visibility.MorphWeightBase);
        parameters.Set(ClusterSoftwareRasterKeys.Screen, size);
        parameters.Set(ClusterSoftwareRasterKeys.ViewProjection, viewProjection);

        var updated = constants.Update(effect, parameters);

        writes[12] = updated
            ? DescriptorWrite.Uniform(
                ClusterSoftwareRasterKeys.ConstantBufferBinding,
                constants.Buffer,
                constants.Offset,
                constants.Size
            )
            : default;

        device.UpdateDescriptorSet(descriptors[ring], updated ? writes : writes.AsSpan(0, 12));
    }

    /// <summary>The packed buffer, the dispatch arguments and the blank that zeroes the first.</summary>
    bool EnsureBuffers(Int2 size) {
        if (depths.IsValid && sized == size) {
            return arguments.IsValid;
        }

        if (depths.IsValid) {
            device.Destroy(depths);
        }

        depths = device.CreateBuffer(
            new(
                (long)size.X * size.Y * PixelBytes,
                BufferUsage.Storage | BufferUsage.CopyDestination,
                MemoryAccess.DeviceLocal,
                "ClusterSoftwareRaster.Depths"
            )
        );

        sized = size;
        pendingClear = true;

        if (!blank.IsValid) {
            blank = device.CreateBuffer(
                new(BlankBytes, BufferUsage.CopySource, MemoryAccess.HostUpload, "ClusterSoftwareRaster.Blank")
            );

            if (blank.IsValid) {
                device.Write(blank, 0, new byte[BlankBytes]);
            }
        }

        if (arguments.IsValid) {
            return depths.IsValid && blank.IsValid;
        }

        arguments = device.CreateBuffer(
            new(
                DispatchWords * sizeof(uint),
                BufferUsage.Indirect | BufferUsage.CopyDestination,
                MemoryAccess.DeviceLocal,
                "ClusterSoftwareRaster.Arguments"
            )
        );

        // (0, 1, 1): a cleared x extent the traversal's count is copied over, and the two the dispatch
        // always has. Seeded through host memory because the buffer is device-local and a device-local
        // buffer is not host-writable — which no recording backend ever says, and which phase 5 found
        // three separate times.
        seed = device.CreateBuffer(
            new(
                DispatchWords * sizeof(uint),
                BufferUsage.CopySource,
                MemoryAccess.HostUpload,
                "ClusterSoftwareRaster.ArgumentSeed"
            )
        );

        if (seed.IsValid) {
            device.Write(seed, 0, MemoryMarshal.AsBytes<uint>([0u, 1u, 1u]));
        }

        return depths.IsValid && arguments.IsValid && seed.IsValid && blank.IsValid;
    }

    /// <summary>How many words a <c>DispatchIndirect</c> reads.</summary>
    const int DispatchWords = 3;

    /// <summary>
    ///     Copies the y and z extents into the argument buffer, once.
    /// </summary>
    /// <param name="list">An open command list, outside a render pass.</param>
    /// <returns>Whether there was anything to record.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> is null.</exception>
    /// <remarks>
    ///     Separate from <see cref="Record" /> for the reason <see cref="GpuClusterRaster.Prepare" /> is
    ///     separate from its own draw: a buffer copy is not legal inside a render pass, and the frame's
    ///     one point that is outside every render pass and before every draw is where
    ///     <see cref="Compositor.ClusterCullingRenderer" /> already puts the other one.
    /// </remarks>
    public bool Prepare(ICommandList list) {
        ArgumentNullException.ThrowIfNull(list);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!Supported || !seed.IsValid || !arguments.IsValid) {
            return false;
        }

        list.Barrier(new([new(arguments, ResourceState.Undefined, ResourceState.CopyDestination)], []));
        list.CopyBuffer(seed, 0, arguments, 0, DispatchWords * sizeof(uint));
        list.Barrier(new([new(arguments, ResourceState.CopyDestination, ResourceState.IndirectArgument)], []));

        return true;
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
            descriptors[index] = device.CreateDescriptorSet(effect.SetLayouts[(int)Slot], "ClusterSoftwareRaster");

            if (!descriptors[index].IsValid) {
                return false;
            }
        }

        allocatedFor = effect;
        ring = 0;

        return true;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var buffer in (BufferHandle[])[depths, arguments, seed, blank]) {
            if (buffer.IsValid) {
                device.Destroy(buffer);
            }
        }

        depths = default;
        arguments = default;
        seed = default;
        blank = default;

        foreach (var set in descriptors) {
            if (set.IsValid) {
                device.Destroy(set);
            }
        }

        descriptors = [];

        constants?.Dispose();
        constants = null;
    }
}

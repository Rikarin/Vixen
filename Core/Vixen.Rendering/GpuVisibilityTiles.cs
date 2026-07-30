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
///     Binning the visibility buffer by material, so a resolve dispatches over the tiles a material is
///     actually in.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is where improvement 2 of <c>docs/virtualized-geometry.md</c> is paid for.</b> Nanite
///         resolves its visibility buffer into a GBuffer, which is a large part of why Unreal is
///         deferred-first. Binning by material and dispatching the existing clustered forward shading
///         over each material's tiles keeps one shading path, one material tree and mobile bandwidth —
///         and a material covering one per cent of the screen dispatches over one per cent of the tiles
///         rather than rasterizing a depth-tested full-screen quad.
///     </para>
///     <para>
///         <b>The counters are the dispatch arguments, so there is no compaction pass.</b> Each material
///         gets three words: the tile count, and two ones. The atomic that appends a tile to a material's
///         list is the same write that tells <see cref="ICommandList.DispatchIndirect" /> how many
///         workgroups to launch, so the host never learns how much of the screen a material covers — the
///         same trade the visible-cluster list makes with the raster's instance count.
///     </para>
///     <para>
///         <b>The worst case is real and is stated rather than hidden.</b> A screen where every tile holds
///         every material degenerates to the same work UE does, with the bookkeeping on top. Materials
///         are spatially coherent in practice; that is the assumption, and <see cref="Overflowed" /> is
///         how a frame that broke it says so.
///     </para>
/// </remarks>
public sealed class GpuVisibilityTiles : IDisposable {
    /// <summary>How many pixels a tile is on a side.</summary>
    /// <remarks><c>VisibilityTile.Size</c>, and a tile is one workgroup of sixty-four invocations.</remarks>
    public const int TileSize = 8;

    /// <summary>How many tiles one material's list holds.</summary>
    /// <remarks>
    ///     <c>VisibilityTile.Capacity</c>. A 1440p screen is 43 200 tiles, so this covers a material over
    ///     about a quarter of it. Past that the tile is dropped, which is a hole — see
    ///     <see cref="Overflowed" />.
    /// </remarks>
    public const int TileCapacity = 12288;

    /// <summary>How many materials the binning covers.</summary>
    public const int MaxMaterials = 64;

    /// <summary>How many words one material's dispatch arguments occupy.</summary>
    public const int ArgumentWords = 3;

    readonly IGraphicsDevice device;
    readonly DescriptorWrite[] writes = new DescriptorWrite[7];

    DescriptorSetHandle[] descriptors = [];
    int ring;

    BufferHandle tiles;
    BufferHandle arguments;
    BufferHandle seed;
    BufferHandle readback;
    uint[] counts = [];

    Effect? allocatedFor;
    EffectConstants? constants;
    bool disposed;

    const DescriptorSetSlot Slot = (DescriptorSetSlot)VisibilityTilesKeys.IdentitiesSet;

    /// <summary>Creates a binner that runs on a device.</summary>
    /// <param name="device">The device.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    public GpuVisibilityTiles(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);
        this.device = device;
    }

    /// <summary>Where the binning variant is resolved from. Null does nothing.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Where the compute pipeline comes from. Null does nothing.</summary>
    public ComputePipelineCache? Pipelines { get; set; }

    /// <summary>The traversal whose answer is being binned. Null does nothing.</summary>
    public GpuClusterVisibility? Visibility { get; set; }

    /// <summary>How many tiles covered the screen the last time this ran.</summary>
    public Int2 TileCount { get; private set; }

    /// <summary>How many materials the last run binned.</summary>
    public int MaterialCount { get; private set; }

    /// <summary>How many tiles each material's list held, as of the last <see cref="ReadCounts" />.</summary>
    /// <remarks>
    ///     A frame late, like every other counter that comes back from a dispatch nothing waited for.
    ///     Zero-length before the first read.
    /// </remarks>
    public ReadOnlySpan<uint> Counts => counts.AsSpan(0, Math.Min(MaterialCount, counts.Length));

    /// <summary>
    ///     Whether any material wanted more tiles than its list holds.
    /// </summary>
    /// <remarks>
    ///     A frame with this set drew a hole: the dropped tiles are pixels no resolve dispatch covers.
    ///     Worth a flag rather than a log because the fix is a larger <see cref="TileCapacity" /> or a
    ///     smaller <see cref="TileSize" />, and neither is something to discover from a screenshot.
    /// </remarks>
    public bool Overflowed { get; private set; }

    /// <summary>Each material's tile list, <see cref="TileCapacity" /> words apart.</summary>
    public BufferHandle Tiles => tiles;

    /// <summary>Each material's dispatch arguments, <see cref="ArgumentWords" /> words apart.</summary>
    public BufferHandle Arguments => arguments;

    /// <summary>How many tiles cover a screen of this many pixels.</summary>
    public static Int2 TilesFor(Int2 size) =>
        new((size.X + TileSize - 1) / TileSize, (size.Y + TileSize - 1) / TileSize);

    /// <summary>Where a material's dispatch arguments start, in bytes.</summary>
    public static long ArgumentOffset(int material) => (long)material * ArgumentWords * sizeof(uint);

    /// <summary>Where a material's tile list starts, as a word index.</summary>
    public static long TileBase(int material) => (long)material * TileCapacity;

    /// <summary>
    ///     Records the binning pass, and clears the counters it is about to fill.
    /// </summary>
    /// <param name="list">An open command list, outside a render pass.</param>
    /// <param name="identities">The visibility buffer the raster wrote.</param>
    /// <param name="size">Its size, in pixels.</param>
    /// <returns>Whether the pass was recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="list" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         The counters are cleared by a copy out of a seed buffer rather than by a host write,
    ///         because they are device-local — every append to them is an atomic. The seed holds
    ///         <c>(0, 1, 1)</c> per material, so clearing the count and writing the two fixed workgroup
    ///         extents is one copy rather than a clear plus a fill.
    ///     </para>
    ///     <para>
    ///         ⚠ Must be recorded after the raster's draw and before any resolve dispatch. Nothing here
    ///         can check that — an open command list does not say what has been recorded into it — which
    ///         is why <see cref="Compositor.VisibilityBufferRenderer" /> owns the ordering.
    ///     </para>
    /// </remarks>
    public bool Record(ICommandList list, TextureViewHandle identities, Int2 size) {
        ArgumentNullException.ThrowIfNull(list);
        ObjectDisposedException.ThrowIf(disposed, this);

        Overflowed = false;

        if (Effects is null
            || Pipelines is null
            || Visibility is not { MeshCount: > 0 } visibility
            || !identities.IsValid
            || size.X <= 0
            || size.Y <= 0) {
            return false;
        }

        var materials = Math.Min(visibility.MaterialCount, MaxMaterials);

        if (materials <= 0) {
            return false;
        }

        if (Effects.Resolve(Key) is not { IsPlaceholder: false } effect) {
            return false;
        }

        var pipeline = Pipelines.GetOrCreate(effect);

        if (!pipeline.IsValid || !EnsureBuffers() || !EnsureDescriptors(effect)) {
            return false;
        }

        TileCount = TilesFor(size);
        MaterialCount = materials;

        ring = (ring + 1) % Math.Max(descriptors.Length, 1);
        Bind(effect, visibility, identities, materials);

        list.Barrier(new([new(arguments, ResourceState.ShaderWrite, ResourceState.CopyDestination)], []));
        list.CopyBuffer(seed, 0, arguments, 0, (long)materials * ArgumentWords * sizeof(uint));

        list.Barrier(
            new(
                [
                    new(arguments, ResourceState.CopyDestination, ResourceState.ShaderWrite),
                    new(tiles, ResourceState.Undefined, ResourceState.ShaderWrite)
                ],
                []
            )
        );

        list.BindPipeline(pipeline);
        list.BindDescriptorSet(Slot, descriptors[ring]);
        list.Dispatch(TileCount.X, TileCount.Y);

        // The arguments become an indirect read for the resolve and a copy source for the counters; the
        // tile lists become a shader read. Both of those are the resolve's business, and it is the next
        // thing recorded.
        list.Barrier(
            new(
                [
                    new(arguments, ResourceState.ShaderWrite, ResourceState.CopySource),
                    new(tiles, ResourceState.ShaderWrite, ResourceState.ShaderRead)
                ],
                []
            )
        );

        list.CopyBuffer(arguments, 0, readback, 0, (long)materials * ArgumentWords * sizeof(uint));

        return true;
    }

    /// <summary>
    ///     Reads back what the previous frame's binning counted.
    /// </summary>
    /// <returns>How many tiles were binned in total.</returns>
    /// <remarks>
    ///     A frame late, because nothing waited for the dispatch — the same trade
    ///     <see cref="GpuClusterVisibility.ServiceRequests" /> makes, and for the same reason: a wait here
    ///     would be a stall in a pipeline arranged to have none. What the counts are for is reporting, and
    ///     a report about the previous frame is a report.
    /// </remarks>
    public int ReadCounts() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!readback.IsValid || MaterialCount <= 0) {
            return 0;
        }

        if (counts.Length < MaterialCount) {
            counts = new uint[MaxMaterials];
        }

        var words = new uint[MaterialCount * ArgumentWords];
        device.Read(readback, 0, MemoryMarshal.AsBytes(words.AsSpan()));

        var total = 0;
        Overflowed = false;

        for (var material = 0; material < MaterialCount; material++) {
            var wanted = words[material * ArgumentWords];

            counts[material] = wanted;
            total += (int)Math.Min(wanted, TileCapacity);

            if (wanted > TileCapacity) {
                Overflowed = true;
            }
        }

        return total;
    }

    /// <summary>The effect key selecting the binning pass.</summary>
    public static EffectKey Key => EffectKey.Of(VisibilityTilesKeys.ShaderName, []);

    void Bind(Effect effect, GpuClusterVisibility visibility, TextureViewHandle identities, int materials) {
        writes[0] = DescriptorWrite.Texture(VisibilityTilesKeys.IdentitiesBinding, identities);

        writes[1] = DescriptorWrite.Storage(
            VisibilityTilesKeys.InstancesBinding,
            visibility.Instances,
            visibility.InstancesOffset,
            (long)visibility.InstanceCount * Unsafe.SizeOf<CullInstance>()
        );

        writes[2] = DescriptorWrite.Storage(VisibilityTilesKeys.ClusterMaterialsBinding, visibility.Materials);
        writes[3] = DescriptorWrite.Storage(VisibilityTilesKeys.VisibleBinding, visibility.Visible);
        writes[4] = DescriptorWrite.Storage(VisibilityTilesKeys.TilesBinding, tiles);
        writes[5] = DescriptorWrite.Storage(VisibilityTilesKeys.ArgumentsBinding, arguments);

        constants ??= new(device, "VisibilityTiles.Constants");

        var parameters = new ParameterCollection();
        parameters.Set(VisibilityTilesKeys.TileCount, TileCount);
        parameters.Set(VisibilityTilesKeys.MaterialCount, materials);

        var count = constants.Update(effect, parameters) ? 7 : 6;

        if (count == 7) {
            writes[6] = DescriptorWrite.Uniform(
                VisibilityTilesKeys.ConstantBufferBinding,
                constants.Buffer,
                constants.Offset,
                constants.Size
            );
        }

        device.UpdateDescriptorSet(descriptors[ring], writes.AsSpan(0, count));
    }

    bool EnsureBuffers() {
        if (tiles.IsValid) {
            return true;
        }

        tiles = device.CreateBuffer(
            new(
                (long)MaxMaterials * TileCapacity * sizeof(uint),
                BufferUsage.Storage,
                MemoryAccess.DeviceLocal,
                "VisibilityTiles.Tiles"
            )
        );

        arguments = device.CreateBuffer(
            new(
                (long)MaxMaterials * ArgumentWords * sizeof(uint),
                BufferUsage.Storage | BufferUsage.Indirect | BufferUsage.CopySource | BufferUsage.CopyDestination,
                MemoryAccess.DeviceLocal,
                "VisibilityTiles.Arguments"
            )
        );

        // (0, 1, 1) per material: a cleared count, and the two workgroup extents a tile list dispatch
        // always has. One copy per frame rather than a clear and a fill.
        var template = new uint[MaxMaterials * ArgumentWords];

        for (var material = 0; material < MaxMaterials; material++) {
            template[(material * ArgumentWords) + 1] = 1u;
            template[(material * ArgumentWords) + 2] = 1u;
        }

        seed = device.CreateBuffer(
            new(
                (long)template.Length * sizeof(uint),
                BufferUsage.CopySource,
                MemoryAccess.DeviceLocal,
                "VisibilityTiles.Seed"
            )
        );

        if (seed.IsValid) {
            device.Write(seed, 0, MemoryMarshal.AsBytes<uint>(template));
        }

        readback = device.CreateBuffer(
            new(
                (long)MaxMaterials * ArgumentWords * sizeof(uint),
                BufferUsage.CopyDestination,
                MemoryAccess.HostReadback,
                "VisibilityTiles.Readback"
            )
        );

        counts = new uint[MaxMaterials];

        return tiles.IsValid && arguments.IsValid && seed.IsValid && readback.IsValid;
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
            descriptors[index] = device.CreateDescriptorSet(effect.SetLayouts[(int)Slot], "VisibilityTiles");

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

        foreach (var buffer in (BufferHandle[])[tiles, arguments, seed, readback]) {
            if (buffer.IsValid) {
                device.Destroy(buffer);
            }
        }

        tiles = default;
        arguments = default;
        seed = default;
        readback = default;

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

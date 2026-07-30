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
///         <b>This is where improvement 2 of <c>docs/plan/22-virtualized-geometry.md</c> is paid for.</b> Nanite
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

    /// <summary>How many tiles one material's list holds to begin with.</summary>
    /// <remarks>
    ///     <c>VisibilityTile.Capacity</c>. A 1440p screen is 43 200 tiles, so this covers a material over
    ///     about a quarter of it — which is the assumption that materials are spatially coherent, sized.
    ///     A frame that breaks it raises <see cref="TileCapacity" /> rather than dropping tiles forever.
    /// </remarks>
    public const int DefaultTileCapacity = 12288;

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
    ///     Whether any material wanted more tiles than its list held.
    /// </summary>
    /// <remarks>
    ///     A frame with this set drew a hole: the dropped tiles are pixels no resolve dispatch covered.
    ///     Still reported now that <see cref="TileCapacity" /> grows in response, because the growth is a
    ///     frame late by construction — the counts come back from a dispatch nothing waited for — so this
    ///     is how a host learns that a frame was wrong, rather than how it learns that frames will be.
    /// </remarks>
    public bool Overflowed { get; private set; }

    /// <summary>
    ///     How many tiles one material's list holds, which grows when one of them wanted more.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A number the shader reads rather than a constant it was compiled with</b>, so that
    ///         <see cref="Overflowed" /> can be answered instead of merely reported. A dropped tile is a
    ///         hole in the picture; a hole that reports itself and then happens again every frame is a
    ///         diagnostic pretending to be a policy.
    ///     </para>
    ///     <para>
    ///         It only ever rises, and it stops at the number of tiles on the screen — a material cannot
    ///         want more list than there are tiles to put in it, so the growth terminates at a capacity
    ///         where overflow is impossible rather than at an arbitrary ceiling. The cost at that point
    ///         is <see cref="MaxMaterials" /> lists of the whole screen, which is eleven megabytes at
    ///         1440p and is only reached by a frame where sixty-four materials each cover all of it.
    ///     </para>
    /// </remarks>
    public int TileCapacity { get; private set; } = DefaultTileCapacity;

    /// <summary>How many times the lists have been grown.</summary>
    /// <remarks>
    ///     Expected to settle at zero or one for a given scene. A number that keeps rising is a frame
    ///     whose materials are genuinely scattered across the screen, which is the case the whole binning
    ///     assumption is about — and at that point the honest answer is a smaller
    ///     <see cref="TileSize" /> rather than a bigger list.
    /// </remarks>
    public int Growths { get; private set; }

    /// <summary>Each material's tile list, <see cref="TileCapacity" /> words apart.</summary>
    public BufferHandle Tiles => tiles;

    /// <summary>Each material's dispatch arguments, <see cref="ArgumentWords" /> words apart.</summary>
    public BufferHandle Arguments => arguments;

    /// <summary>How many tiles cover a screen of this many pixels.</summary>
    public static Int2 TilesFor(Int2 size) =>
        new((size.X + TileSize - 1) / TileSize, (size.Y + TileSize - 1) / TileSize);

    /// <summary>Where a material's dispatch arguments start, in bytes.</summary>
    public static long ArgumentOffset(int material) => (long)material * ArgumentWords * sizeof(uint);

    /// <summary>
    ///     What the capacity becomes after a frame where a material wanted more than it had.
    /// </summary>
    /// <param name="current">The capacity that was too small.</param>
    /// <param name="wanted">The largest count any material reported.</param>
    /// <param name="tileCount">How many tiles cover the screen, which is the ceiling.</param>
    /// <returns>The new capacity, never below <paramref name="current" />.</returns>
    /// <remarks>
    ///     <para>
    ///         Separate from <see cref="ReadCounts" /> because it is the policy and the rest is
    ///         plumbing: what a frame is allowed to do about a hole in the picture is a decision worth
    ///         being able to read, and worth being able to test without a device that can execute a
    ///         copy.
    ///     </para>
    ///     <para>
    ///         Doubling <em>or</em> the count, whichever is larger, so a frame that overflowed by a
    ///         factor of ten settles in one step rather than four — and capped at the screen's own tile
    ///         count, which is what makes it terminate: a material's list holds tiles that exist, so
    ///         past that there is nothing left to drop.
    ///     </para>
    /// </remarks>
    public static int NextCapacity(int current, int wanted, Int2 tileCount) {
        var ceiling = Math.Max((long)tileCount.X * tileCount.Y, DefaultTileCapacity);

        // A screen that shrank leaves the capacity above its own ceiling, which is not a reason to give
        // any of it back: the buffer exists, and lowering it would be a second allocation to save memory
        // a frame is not short of.
        if (ceiling <= current) {
            return current;
        }

        return (int)Math.Min(Math.Max(wanted, (long)current * 2), ceiling);
    }

    /// <summary>Where a material's tile list starts, as a word index.</summary>
    /// <remarks>
    ///     An instance member rather than a static one since the capacity became a frame's decision. A
    ///     static answer would be the base for a list the shader is no longer using, which reads a
    ///     neighbouring material's tiles rather than failing.
    /// </remarks>
    public long TileBase(int material) => (long)material * TileCapacity;

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
        var largest = 0;
        Overflowed = false;

        for (var material = 0; material < MaterialCount; material++) {
            var wanted = words[material * ArgumentWords];

            counts[material] = wanted;
            total += (int)Math.Min(wanted, TileCapacity);
            largest = Math.Max(largest, (int)Math.Min(wanted, int.MaxValue));

            if (wanted > TileCapacity) {
                Overflowed = true;
            }
        }

        if (Overflowed) {
            Grow(largest);
        }

        return total;
    }

    /// <summary>
    ///     Raises the capacity so the next frame's lists hold what this one wanted, and drops the buffer
    ///     that was too small.
    /// </summary>
    /// <param name="wanted">The largest count any material reported.</param>
    /// <remarks>
    ///     <para>
    ///         Capped at the screen's own tile count, which is the number that makes this terminate: a
    ///         material's list is a list of tiles on the screen, so past that there is nothing left to
    ///         drop. A frame at that capacity cannot overflow at all.
    ///     </para>
    ///     <para>
    ///         <b>Destroyed here rather than at the next <see cref="Record" />, because here is where
    ///         nothing is reading it.</b> <see cref="ReadCounts" /> is called between frames, after the
    ///         readback the previous frame's copy filled — see <see cref="Compositor.ClusterCullingRenderer" />
    ///         for where the same argument places the page flush. Destroying it inside a frame would free
    ///         a buffer the resolve's own dispatch is still bound to.
    ///     </para>
    /// </remarks>
    void Grow(int wanted) {
        var capacity = NextCapacity(TileCapacity, wanted, TileCount);

        if (capacity <= TileCapacity) {
            // Already as large as the screen can ask for. The overflow is then a frame with more than
            // MaxMaterials-worth of scattered materials, which a bigger list cannot fix.
            return;
        }

        TileCapacity = capacity;
        Growths++;

        if (tiles.IsValid) {
            device.Destroy(tiles);
            tiles = default;
        }

        // The descriptor sets are not touched: Bind rewrites every one of them each frame, so the new
        // buffer reaches the set the way the old one did.
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
        parameters.Set(VisibilityTilesKeys.TileCapacity, (uint)TileCapacity);

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
        // The tile lists alone, because they are the only ones the capacity sizes — Grow destroys this
        // one and leaves the rest, and recreating all four here would leak three every time it did.
        if (!tiles.IsValid) {
            tiles = device.CreateBuffer(
                new(
                    (long)MaxMaterials * TileCapacity * sizeof(uint),
                    BufferUsage.Storage,
                    MemoryAccess.DeviceLocal,
                    "VisibilityTiles.Tiles"
                )
            );
        }

        if (arguments.IsValid) {
            return tiles.IsValid;
        }

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

        // Host-upload, because the host writes it once and nothing but a copy ever reads it. Device-local
        // would be the faster memory for a buffer a shader read, and this is not one — it is a template
        // that exists so the per-frame clear is a copy instead of a map.
        seed = device.CreateBuffer(
            new(
                (long)template.Length * sizeof(uint),
                BufferUsage.CopySource,
                MemoryAccess.HostUpload,
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

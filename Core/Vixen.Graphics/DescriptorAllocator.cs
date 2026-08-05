// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics;

/// <summary>
///     Descriptor sets for one frame's worth of transient bindings, recycled rather than recreated.
/// </summary>
/// <remarks>
///     <para>
///         <strong>The piece every frame-graph renderer needs and few write down.</strong> A pass that
///         samples the shadow atlas cannot own a descriptor set: the atlas is a graph resource, so its
///         texture handle does not exist until the graph has allocated it, and next frame the same
///         name may alias different memory. The set therefore has to be written after the graph
///         compiles and thrown away when the frame ends — which is a lifetime nothing else in the RHI
///         has.
///     </para>
///     <para>
///         <strong>Sets are recycled, not destroyed.</strong> Creating and destroying a set per pass
///         per frame is the allocation churn the handle-based RHI exists to avoid, and on Vulkan it is
///         worse than it looks: <c>vkResetDescriptorPool</c> is cheap but
///         <c>vkAllocateDescriptorSets</c> is not, and a pool that grows to fit the worst frame never
///         shrinks. Here a retired set goes back to a free list keyed by its layout and is rewritten
///         next time that layout is asked for.
///     </para>
///     <para>
///         <strong>The ring is <see cref="FramesInFlight" /> deep, and that is not a tuning knob.</strong>
///         A set written for frame <c>f</c> is still being read by the GPU while the CPU records
///         <c>f + 1</c>. Rewriting it before frame <c>f</c> retires points a descriptor the GPU is
///         reading at something else — a use-after-free that most drivers execute without a word, and
///         which the validation layers only catch with synchronisation validation switched on. The
///         device already blocks in <see cref="IGraphicsDevice.BeginFrame" /> until frame
///         <c>f - FramesInFlight</c> has completed, so a ring of exactly that depth is both necessary
///         and sufficient.
///     </para>
///     <para>
///         <strong>Identical writes within a frame return the same set.</strong> The cache is
///         content-addressed: the layout plus the exact sequence of
///         <see cref="DescriptorWrite" />s. Every pass that reads the same shadow atlas and cluster
///         buffer shares one set rather than getting one each, which is the difference between a set
///         per pass and a set per <em>distinct combination</em> — in a real frame, two orders of
///         magnitude. It also means <strong>the set you are handed may be shared, so never call
///         <see cref="IGraphicsDevice.UpdateDescriptorSet" /> on it yourself</strong>; ask for another
///         one with the writes you wanted.
///     </para>
///     <para>
///         The cache does not survive the frame, and that is the point rather than an oversight: the
///         handles inside it name transient memory that the next frame's graph is free to give to
///         something else. A cache that persisted would be correct exactly until two frames' graphs
///         differed.
///     </para>
///     <para>
///         ⚠ <b>Which is why a missed <see cref="BeginFrame" /> throws rather than merely leaking.</b>
///         Everything above is a contract this type could state and not check, and a host that never
///         called it was indistinguishable from one that did — the sets came out, the draws recorded,
///         nothing complained, and the frame was wrong only in a way that depends on whether the graph
///         happened to hand the same memory back. <see cref="IGraphicsDevice.FrameCount" /> is the
///         clock that makes the contract checkable; <see cref="Allocate" /> refuses once the device
///         has moved on without this being told.
///     </para>
/// </remarks>
public sealed class DescriptorAllocator : IDisposable {
    readonly IGraphicsDevice device;
    readonly Dictionary<DescriptorSetLayoutHandle, Stack<DescriptorSetHandle>> free = [];
    readonly List<Retired>[] retiring;
    readonly List<Retired> created = [];
    readonly Dictionary<SetKey, DescriptorSetHandle> cache = new(KeyComparer.Instance);
    readonly Dictionary<SetKey, DescriptorSetHandle>.AlternateLookup<SetProbe> lookup;
    readonly Lock gate = new();
    readonly string name;

    int slot;
    bool disposed;
    long begun;

    /// <summary>Creates an allocator for a device.</summary>
    /// <param name="device">The device its sets come from.</param>
    /// <param name="name">A prefix for the sets' debug names.</param>
    public DescriptorAllocator(IGraphicsDevice device, string name = "Frame") {
        ArgumentNullException.ThrowIfNull(device);

        this.device = device;
        this.name = name;
        begun = device.FrameCount;
        FramesInFlight = Math.Max(1, device.FramesInFlight);
        retiring = new List<Retired>[FramesInFlight];

        for (var i = 0; i < retiring.Length; i++) {
            retiring[i] = [];
        }

        lookup = cache.GetAlternateLookup<SetProbe>();
    }

    /// <summary>How deep the recycling ring is, which is the device's frames in flight.</summary>
    public int FramesInFlight { get; }

    /// <summary>How many sets exist, across every layout and every ring slot.</summary>
    /// <remarks>
    ///     The number a leak test wants: run a hundred frames of the same workload and this stops
    ///     growing after <see cref="FramesInFlight" /> of them, or something is allocating a set it
    ///     never gives back.
    /// </remarks>
    public int SetCount {
        get {
            lock (gate) {
                return created.Count;
            }
        }
    }

    /// <summary>How many sets this frame had to write, which is how many distinct binding
    /// combinations it used.</summary>
    public int WriteCount { get; private set; }

    /// <summary>How many requests this frame were answered out of the cache.</summary>
    public int ReuseCount { get; private set; }

    /// <summary>Starts a frame, taking back what the frame <see cref="FramesInFlight" /> ago used.</summary>
    /// <remarks>
    ///     <para>
    ///         Call after <see cref="IGraphicsDevice.BeginFrame" /> and before the frame's first
    ///         <see cref="Allocate" />. Calling it late is not a subtle bug: the sets recycled by it
    ///         are the ones the frame is about to hand out, so a missed call leaks a frame's worth of
    ///         sets and an early one recycles sets the GPU is still reading.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not calling it at all is the one this is checked for</b>, because it is the only
    ///         mistake here that looks like health. See <see cref="Allocate" />.
    ///     </para>
    /// </remarks>
    public void BeginFrame() {
        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);

            begun = device.FrameCount;
            slot = (slot + 1) % FramesInFlight;
            Recycle(retiring[slot]);

            cache.Clear();
            WriteCount = 0;
            ReuseCount = 0;
        }
    }

    /// <summary>A set of the given shape pointing at the given resources, for this frame only.</summary>
    /// <param name="layout">The set's shape, which is what a pipeline's layout was built from.</param>
    /// <param name="writes">What to bind where.</param>
    /// <returns>
    ///     A set valid until <see cref="FramesInFlight" /> further calls to
    ///     <see cref="BeginFrame" />. It may be shared with another caller that asked for the same
    ///     thing, so treat it as read-only.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    ///     The device has begun a frame since this allocator last did, which means nothing is calling
    ///     <see cref="BeginFrame" />.
    /// </exception>
    /// <remarks>
    ///     ⚠ <b>Refuses outright where the frame boundary went by unannounced, and that is the whole
    ///     point of it being loud.</b> A missed <see cref="BeginFrame" /> is invisible from every
    ///     direction that matters: the sets are handed out, the pipelines bind them, the draws record,
    ///     the validation layers say nothing — and the frame is wrong only because the cache is
    ///     content-addressed over handles that name transient graph memory, so <c>f + 1</c> gets back
    ///     a set still pointing at <c>f</c>'s attachments. Whether that is visible depends on whether
    ///     the graph happened to hand the same memory back, which means it survives every test and
    ///     appears the day an allocation order changes. Correct code calls <see cref="BeginFrame" />
    ///     once per device frame and never sees this.
    /// </remarks>
    public DescriptorSetHandle Allocate(DescriptorSetLayoutHandle layout, ReadOnlySpan<DescriptorWrite> writes) {
        if (!layout.IsValid) {
            throw new ArgumentException("A descriptor set cannot be allocated without a layout.", nameof(layout));
        }

        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);

            // Compared against the device rather than counted here, because an allocator has no clock
            // of its own — its ring is defined against the device's frame and it could not see one.
            if (device.FrameCount != begun) {
                throw new InvalidOperationException(
                    $"The descriptor allocator '{name}' was last told a frame began at device frame "
                    + $"{begun} and the device is now on {device.FrameCount}, so nothing is calling "
                    + "BeginFrame. Its cache is keyed on handles naming transient graph memory the "
                    + "frames since have been free to reuse, so a set returned now may point at an "
                    + "attachment that is no longer there. Call BeginFrame once per frame, after "
                    + "IGraphicsDevice.BeginFrame and before the frame's first allocation."
                );
            }

            var probe = new SetProbe(layout, writes);

            if (lookup.TryGetValue(probe, out var cached)) {
                ReuseCount++;
                return cached;
            }

            var set = Take(layout);
            device.UpdateDescriptorSet(set, writes);

            lookup[probe] = set;
            retiring[slot].Add(new(layout, set));
            WriteCount++;
            return set;
        }
    }

    /// <summary>Takes every set back at once, whichever frame it belonged to.</summary>
    /// <remarks>
    ///     For after <see cref="IGraphicsDevice.WaitIdle" /> — a swapchain resize, a device reset, a
    ///     level teardown. It is only safe because the caller has established that nothing is in
    ///     flight; the per-frame path deliberately has no way to say that.
    /// </remarks>
    public void Reset() {
        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);

            // Re-synchronised, because this *is* a frame boundary — a harder one than BeginFrame's.
            // A resize or a level teardown that took everything back and then tripped the guard on
            // the next allocation would be reporting the one case that cannot be stale.
            begun = device.FrameCount;

            foreach (var frame in retiring) {
                Recycle(frame);
            }

            cache.Clear();
            WriteCount = 0;
            ReuseCount = 0;
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        lock (gate) {
            if (disposed) {
                return;
            }

            disposed = true;

            foreach (var entry in created) {
                device.Destroy(entry.Set);
            }

            created.Clear();
            free.Clear();
            cache.Clear();

            foreach (var frame in retiring) {
                frame.Clear();
            }
        }
    }

    DescriptorSetHandle Take(DescriptorSetLayoutHandle layout) {
        if (free.TryGetValue(layout, out var pool) && pool.TryPop(out var reused)) {
            return reused;
        }

        var set = device.CreateDescriptorSet(layout, $"{name}.Set{created.Count}");
        created.Add(new(layout, set));
        return set;
    }

    void Recycle(List<Retired> frame) {
        foreach (var entry in frame) {
            if (!free.TryGetValue(entry.Layout, out var pool)) {
                free[entry.Layout] = pool = new();
            }

            pool.Push(entry.Set);
        }

        frame.Clear();
    }

    /// <summary>A set and the layout it has to go back to.</summary>
    readonly record struct Retired(DescriptorSetLayoutHandle Layout, DescriptorSetHandle Set);

    /// <summary>What the cache is keyed by, once a key is worth keeping.</summary>
    readonly record struct SetKey(DescriptorSetLayoutHandle Layout, DescriptorWrite[] Writes);

    /// <summary>The same key before it has been copied, so a hit costs no allocation.</summary>
    readonly ref struct SetProbe(DescriptorSetLayoutHandle layout, ReadOnlySpan<DescriptorWrite> writes) {
        public DescriptorSetLayoutHandle Layout { get; } = layout;

        public ReadOnlySpan<DescriptorWrite> Writes { get; } = writes;
    }

    /// <summary>
    ///     Content equality over the writes, and the span-shaped view of it a lookup probes with.
    /// </summary>
    /// <remarks>
    ///     The alternate lookup is what keeps the cache from defeating itself. Probing a
    ///     <c>Dictionary&lt;SetKey, …&gt;</c> would mean building a <c>SetKey</c> — and therefore
    ///     copying the writes into an array — on every request including the hits, so the allocation
    ///     the cache exists to avoid would happen anyway, one per lookup instead of one per set.
    /// </remarks>
    sealed class KeyComparer : IEqualityComparer<SetKey>, IAlternateEqualityComparer<SetProbe, SetKey> {
        public static readonly KeyComparer Instance = new();

        public bool Equals(SetKey x, SetKey y) =>
            x.Layout == y.Layout && x.Writes.AsSpan().SequenceEqual(y.Writes);

        public int GetHashCode(SetKey key) => Hash(key.Layout, key.Writes);

        public bool Equals(SetProbe alternate, SetKey other) =>
            alternate.Layout == other.Layout && alternate.Writes.SequenceEqual(other.Writes);

        public int GetHashCode(SetProbe alternate) => Hash(alternate.Layout, alternate.Writes);

        public SetKey Create(SetProbe alternate) => new(alternate.Layout, alternate.Writes.ToArray());

        static int Hash(DescriptorSetLayoutHandle layout, ReadOnlySpan<DescriptorWrite> writes) {
            var hash = new HashCode();
            hash.Add(layout);

            foreach (var write in writes) {
                hash.Add(write);
            }

            return hash.ToHashCode();
        }
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Graphics;

/// <summary>
///     One unbounded descriptor array, and the <c>TextureViewHandle → uint</c> table that names its
///     slots.
/// </summary>
/// <remarks>
///     <para>
///         <strong>What bindless actually is, once the extension names are set aside.</strong> A
///         descriptor set stops being a thing a draw <em>binds</em> and becomes a thing a shader
///         <em>indexes</em>: one set, bound once for the frame, holding every texture the frame could
///         possibly sample, and a draw carries a number rather than a set. That is the whole
///         mechanism, and every consequence follows from it — draws that differ only in their
///         textures stop differing at all, so they can be sorted together, merged into one indirect
///         command, and issued from a list the device wrote for itself.
///     </para>
///     <para>
///         <strong>An index, once handed out, does not move.</strong> Not because moving would be
///         hard but because the index is written into data the host has already given away — a
///         material's record, a per-object block, a buffer the device filled last frame — and none of
///         those can be found again to be renumbered. So the table is an allocator with a free list
///         and never a compactor, and <see cref="Capacity" /> is a real ceiling rather than a hint.
///     </para>
///     <para>
///         <strong>The same view twice is the same index.</strong> Forty materials over one albedo
///         atlas is one descriptor, and the reference count is what lets the fortieth material be
///         released without the other thirty-nine losing their texture. A table that deduplicated
///         without counting would be worse than one that did not deduplicate at all: the failure is
///         not a wasted slot, it is a live material sampling whatever arrives at the index it was
///         given.
///     </para>
///     <para>
///         <strong>A freed index is retired, not reused.</strong> This is
///         <see cref="DescriptorAllocator" />'s hazard in its sharpest form. A material record
///         written for frame <c>f</c> holds an index the GPU reads while the CPU records
///         <c>f + 1</c>; handing that index straight back means the next texture to want a slot takes
///         one an unfinished frame is still sampling, and the picture is one frame of the wrong
///         texture on an object nobody touched. So a released index goes into a ring
///         <see cref="FramesInFlight" /> deep and only becomes available again once the frame that
///         could have named it has retired — which makes <see cref="BeginFrame" /> mandatory rather
///         than an optimisation.
///     </para>
///     <para>
///         <strong>Writes go to the device immediately, and that is only safe because of the
///         flag.</strong> Updating a descriptor set that a recorded-but-unsubmitted command buffer
///         binds is invalid usage on Vulkan — unless the set was created with update-after-bind,
///         which is exactly what an unbounded binding is created with. It is also why this can write
///         one descriptor at a time without buffering: a distinct view is written once for the life
///         of the table, not once per frame, so the steady state of a settled scene is no writes at
///         all.
///     </para>
///     <para>
///         <strong>It does not exist without the capability.</strong> There is no emulated path here
///         and there should not be one: a table faked as a bounded array of the largest size the
///         device allows is a different shader, a different limit and a different failure, and
///         pretending otherwise would put the discovery of it in a driver's error rather than in a
///         host's <c>if</c>. Ask <see cref="IsSupportedBy" /> and take the per-material set instead.
///     </para>
/// </remarks>
public sealed class BindlessTable : IDisposable {
    /// <summary>The capacity a table and the layouts that index it agree on, absent a reason to differ.</summary>
    /// <remarks>
    ///     <para>
    ///         <strong>A number is only correct here if both sides say the same one.</strong> A pipeline
    ///         declares set <see cref="DescriptorSetSlot.Bindless" /> with a length of its own — an
    ///         unbounded binding has to be given one, because the layout is what a descriptor pool is
    ///         sized from — and the set bound against it was allocated from
    ///         <see cref="Layout" />, which states the length this table was built with. Two different
    ///         numbers is two incompatible layouts, which is undefined behaviour that a forgiving
    ///         driver renders correctly right up until one does not.
    ///     </para>
    ///     <para>
    ///         So the number lives once, here, where the table that hands out the indices is. The
    ///         effect path reads it as its own default (<c>EffectLoader.BindlessCapacity</c>) and the
    ///         standard renderer builds its table with it; a project that outgrows four thousand
    ///         textures changes both together and neither alone.
    ///     </para>
    ///     <para>
    ///         ⚠ Not the constructor's default, which is the device's ceiling — deliberately, because
    ///         a table on its own has no opinion about how large a scene is and a caller who says
    ///         nothing is asking for whatever the device allows. This is the engine's opinion, and it
    ///         is four thousand rather than a million because a desktop driver reports a million and
    ///         reserving a million descriptors for a scene's worth of textures is hundreds of
    ///         megabytes of descriptor memory nothing will ever index.
    ///     </para>
    /// </remarks>
    public const int ConventionalCapacity = 4096;

    readonly IGraphicsDevice device;
    readonly Dictionary<TextureViewHandle, Slot> live = [];
    readonly Stack<uint> free = new();
    readonly List<uint>[] retiring;
    readonly Lock gate = new();
    readonly TextureViewHandle fallback;
    readonly uint binding;
    readonly DescriptorKind kind;

    uint next;
    int slot;
    bool disposed;

    /// <summary>Creates a table, its set layout and its one set.</summary>
    /// <param name="device">The device the set comes from.</param>
    /// <param name="stages">Which stages index the array.</param>
    /// <param name="capacity">
    ///     How many slots, or <c>0</c> for the device's
    ///     <see cref="GraphicsDeviceFeatures.MaxBindlessDescriptors" />.
    /// </param>
    /// <param name="binding">Which binding within the set the array is, matching the shader.</param>
    /// <param name="slot">
    ///     Which set it is, matching the shader. <see cref="DescriptorSetSlot.Bindless" /> unless a
    ///     caller has a reason — see that member for why a table wants a set nothing else is in.
    /// </param>
    /// <param name="kind">Whether the array is sampled or storage textures.</param>
    /// <param name="fallback">
    ///     A view to leave in a slot nobody occupies, or none. Worth supplying: an index that outlives
    ///     the texture it named is a bug either way, but one that samples a magenta 1×1 is a bug
    ///     somebody can see, where one that reads a destroyed image is whatever the driver left there.
    /// </param>
    /// <param name="name">A name for the debugger and the validation layers.</param>
    /// <exception cref="NotSupportedException">The device has no descriptor indexing.</exception>
    public BindlessTable(
        IGraphicsDevice device,
        ShaderStage stages = ShaderStage.Fragment,
        int capacity = 0,
        uint binding = 0,
        DescriptorSetSlot slot = DescriptorSetSlot.Bindless,
        DescriptorKind kind = DescriptorKind.SampledTexture,
        TextureViewHandle fallback = default,
        string name = "Bindless"
    ) {
        ArgumentNullException.ThrowIfNull(device);

        if (!IsSupportedBy(device)) {
            throw new NotSupportedException(
                $"'{name}' needs an unbounded descriptor array, which this device does not have — "
                + "GraphicsDeviceFeatures.HasBindless is false. There is no emulated path; the "
                + "non-bindless one binds a descriptor set per material instead."
            );
        }

        if (kind is not (DescriptorKind.SampledTexture or DescriptorKind.StorageTexture)) {
            throw new ArgumentException(
                $"'{name}' was asked for a table of {kind}. A table holds texture views, so it is one "
                + "of the two texture kinds.",
                nameof(kind)
            );
        }

        var limit = device.Features.MaxBindlessDescriptors;

        if (capacity < 0 || capacity > limit) {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                capacity,
                $"'{name}' asked for {capacity} slots; this device allows {limit}."
            );
        }

        this.device = device;
        this.binding = binding;
        this.kind = kind;
        this.fallback = fallback;

        Capacity = capacity > 0 ? capacity : limit;
        FramesInFlight = Math.Max(1, device.FramesInFlight);
        retiring = new List<uint>[FramesInFlight];

        for (var index = 0; index < retiring.Length; index++) {
            retiring[index] = [];
        }

        // Count 0 is what makes it the unbounded binding rather than an array of one; the capacity
        // beside it is how many descriptors that comes to. Both, because they answer different
        // questions — the count says the *shader* states no length, and a backend that took only
        // that would size every table at the device's ceiling and reserve a pool to match.
        // Everything else about the layout is the shader's to state, which is why all of it is a
        // parameter.
        Layout = device.CreateDescriptorSetLayout(
            new(slot, [new(binding, kind, stages, 0)], name, Capacity)
        );

        Set = device.CreateDescriptorSet(Layout, name);
    }

    /// <summary>The layout a pipeline declares to be able to index this table.</summary>
    public DescriptorSetLayoutHandle Layout { get; }

    /// <summary>The one set, bound once a frame and never rebound.</summary>
    public DescriptorSetHandle Set { get; }

    /// <summary>How many slots it has.</summary>
    public int Capacity { get; }

    /// <summary>How deep the retirement ring is, which is the device's frames in flight.</summary>
    public int FramesInFlight { get; }

    /// <summary>How many distinct views currently hold a slot.</summary>
    public int Count {
        get {
            lock (gate) {
                return live.Count;
            }
        }
    }

    /// <summary>The highest slot ever handed out, which is what the free list works below.</summary>
    /// <remarks>
    ///     Distinct from <see cref="Count" />, and the difference is the fragmentation: a table that
    ///     has churned through a level's worth of textures has a high-water mark far above what it
    ///     holds. It never falls except at <see cref="Reset" />, because an index is not moved.
    /// </remarks>
    public int HighWaterMark {
        get {
            lock (gate) {
                return (int)next;
            }
        }
    }

    /// <summary>How many descriptor writes have gone to the device over this table's life.</summary>
    /// <remarks>
    ///     The number a test asserts against, and the one that says whether deduplication is working:
    ///     a settled scene adds nothing and writes nothing, however many times its materials ask.
    /// </remarks>
    public int WriteCount { get; private set; }

    /// <summary>Whether a device can hold one of these at all.</summary>
    /// <param name="device">The device.</param>
    public static bool IsSupportedBy(IGraphicsDevice device) {
        ArgumentNullException.ThrowIfNull(device);
        return device.Features is { HasBindless: true, MaxBindlessDescriptors: > 0 };
    }

    /// <summary>Starts a frame, making available the slots released
    /// <see cref="FramesInFlight" /> frames ago.</summary>
    /// <remarks>
    ///     <para>
    ///         Call after <see cref="IGraphicsDevice.BeginFrame" />. A missed call does not leak — the
    ///         indices stay in the ring and come back on the next one — but a table that is never told
    ///         about frames never reuses a slot at all, so a level that streams textures in and out
    ///         walks the high-water mark up to <see cref="Capacity" /> and then refuses.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Deliberately not guarded the way <see cref="DescriptorAllocator.Allocate" /> is,
    ///         and the asymmetry is the difference between the two misses.</b> The allocator's is a
    ///         correctness bug that draws a plausible frame, so it has to be refused where it happens.
    ///         This one costs no correctness and no memory: nothing here is ever handed back stale,
    ///         because a slot is only reused once <see cref="FramesInFlight" /> visits have retired
    ///         the frame that could still name it. What it costs is the high-water mark, and that
    ///         already ends in <see cref="Add" />'s exhaustion throw — which names this call as one of
    ///         its two causes. A throw here would fire on the first texture of a long-lived table
    ///         whose owner has no per-frame tick at all, which is a legitimate way to use one.
    ///     </para>
    /// </remarks>
    public void BeginFrame() {
        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);

            slot = (slot + 1) % FramesInFlight;

            foreach (var index in retiring[slot]) {
                free.Push(index);
            }

            retiring[slot].Clear();
        }
    }

    /// <summary>Gives a view a slot, or returns the one it already has.</summary>
    /// <param name="view">The view.</param>
    /// <returns>The index a shader reads it at.</returns>
    /// <exception cref="InvalidOperationException">The table is full.</exception>
    public uint Add(TextureViewHandle view) {
        if (!view.IsValid) {
            throw new ArgumentException("A bindless slot cannot hold a null view.", nameof(view));
        }

        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (live.TryGetValue(view, out var existing)) {
                live[view] = existing with { References = existing.References + 1 };
                return existing.Index;
            }

            if (!free.TryPop(out var index)) {
                if (next >= (uint)Capacity) {
                    throw new InvalidOperationException(
                        $"The bindless table is full at {Capacity} slots. Either the device's "
                        + "MaxBindlessDescriptors is the ceiling, or views are being added and never "
                        + "released."
                    );
                }

                index = next++;
            }

            live[view] = new(index, 1);
            Write(index, view);
            return index;
        }
    }

    /// <summary>The slot a view already holds, if it holds one.</summary>
    /// <param name="view">The view.</param>
    /// <param name="index">Where a shader reads it.</param>
    public bool TryGetIndex(TextureViewHandle view, out uint index) {
        lock (gate) {
            if (live.TryGetValue(view, out var found)) {
                index = found.Index;
                return true;
            }

            index = 0;
            return false;
        }
    }

    /// <summary>Gives back one reference to a view's slot.</summary>
    /// <param name="view">The view.</param>
    /// <returns>True when that was the last reference and the slot began retiring.</returns>
    /// <remarks>
    ///     Once per <see cref="Add" />, and a view nobody added is not an error — a caller tearing
    ///     down a material it never finished building would otherwise have to remember which of its
    ///     textures reached the table.
    /// </remarks>
    public bool Remove(TextureViewHandle view) {
        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (!live.TryGetValue(view, out var existing)) {
                return false;
            }

            if (existing.References > 1) {
                live[view] = existing with { References = existing.References - 1 };
                return false;
            }

            live.Remove(view);

            // Into the slot the ring is currently *on*, so it comes back at the next visit to it —
            // FramesInFlight calls to BeginFrame away, which is exactly when the frame that could
            // still be reading this index has retired.
            retiring[slot].Add(existing.Index);

            if (fallback.IsValid) {
                Write(existing.Index, fallback);
            }

            return true;
        }
    }

    /// <summary>Empties the table, high-water mark included.</summary>
    /// <remarks>
    ///     For after <see cref="IGraphicsDevice.WaitIdle" /> — a level teardown, a device reset. It is
    ///     only safe because the caller has established that no frame holds an index; the per-frame
    ///     path deliberately has no way to say that. Descriptors are left as they were, which is legal
    ///     for a partially-bound array and irrelevant for one nothing indexes yet.
    /// </remarks>
    public void Reset() {
        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);

            live.Clear();
            free.Clear();
            next = 0;

            foreach (var frame in retiring) {
                frame.Clear();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        lock (gate) {
            if (disposed) {
                return;
            }

            disposed = true;
            live.Clear();
            free.Clear();

            foreach (var frame in retiring) {
                frame.Clear();
            }

            device.Destroy(Set);
            device.Destroy(Layout);
        }
    }

    void Write(uint index, TextureViewHandle view) {
        Span<DescriptorWrite> one = [
            new(binding, kind, TextureView: view, ArrayIndex: (int)index)
        ];

        device.UpdateDescriptorSet(Set, one);
        WriteCount++;
    }

    /// <summary>What a view holds: where it is, and how many callers asked for it.</summary>
    readonly record struct Slot(uint Index, int References);
}

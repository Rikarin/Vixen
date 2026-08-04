// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.Vulkan;

namespace Vixen.Graphics.Vulkan;

/// <summary>Where one resource's memory lives.</summary>
/// <param name="Memory">The device allocation it is part of.</param>
/// <param name="Offset">Where in that allocation it starts.</param>
/// <param name="Size">How many bytes it occupies.</param>
/// <param name="TypeIndex">Which memory type it came from.</param>
/// <param name="BlockIndex">Which block within that type's pool, or <c>-1</c> for a dedicated one.</param>
/// <param name="Linear">Whether it is linear (a buffer) rather than optimally tiled (an image).</param>
/// <param name="Mapped">A pointer to its first byte, when the block is host-visible.</param>
/// <param name="DeviceAddress">
///     Whether its block was allocated for GPU addressing. Part of the allocation rather than
///     re-derived at free time, because it is a third dimension of the pool key and a free that
///     guessed it would hand the space back to the wrong pool.
/// </param>
readonly record struct VulkanAllocation(
    DeviceMemory Memory,
    long Offset,
    long Size,
    int TypeIndex,
    int BlockIndex,
    bool Linear,
    nint Mapped,
    bool DeviceAddress = false
) {
    /// <summary>Whether the CPU can write to it without a staging copy.</summary>
    public bool IsMapped => Mapped != 0;
}

/// <summary>Choosing a memory type, which is where "it runs but it is slow" is usually decided.</summary>
/// <remarks>
///     Pure, and separated because the interesting configurations are other people's hardware: a
///     discrete card with a small host-visible device-local window, an integrated part where every
///     heap is both, and a driver that lists eleven types of which two are usable. The
///     required/preferred split is what lets an upload buffer land in device-local host-visible
///     memory where that exists and fall back to plain host memory where it does not, without a
///     vendor table.
/// </remarks>
static class MemoryTypeSelection {
    /// <summary>Picks a memory type.</summary>
    /// <param name="memory">What the device reported.</param>
    /// <param name="typeBits">Which types the resource may use, from its memory requirements.</param>
    /// <param name="required">Properties without which the type is unusable.</param>
    /// <param name="preferred">Properties worth having, tried first.</param>
    /// <returns>The type index, or <c>-1</c> if nothing qualifies.</returns>
    public static int Select(
        in PhysicalDeviceMemoryProperties memory,
        uint typeBits,
        MemoryPropertyFlags required,
        MemoryPropertyFlags preferred
    ) {
        var best = Find(memory, typeBits, required | preferred);
        return best >= 0 ? best : Find(memory, typeBits, required);
    }

    /// <summary>What each access pattern needs, and what it would like.</summary>
    /// <param name="access">Where the caller said the memory should live.</param>
    public static (MemoryPropertyFlags Required, MemoryPropertyFlags Preferred) For(MemoryAccess access) =>
        access switch {
            // Upload memory has to be coherent, because the alternative is an explicit flush on every
            // write and a class of bug where the data arrives eventually. Device-local as well where
            // the hardware offers it: that is the resizable-BAR window on a discrete card, and
            // writing straight into it removes the staging copy entirely.
            MemoryAccess.HostUpload => (
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                MemoryPropertyFlags.DeviceLocalBit
            ),

            // Readback wants cached memory. Uncached host memory is write-combined on most hardware,
            // and reading it back is roughly an order of magnitude slower — which turns a
            // golden-image comparison from milliseconds into a visible pause.
            MemoryAccess.HostReadback => (
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                MemoryPropertyFlags.HostCachedBit
            ),

            _ => (MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.None)
        };

    static int Find(in PhysicalDeviceMemoryProperties memory, uint typeBits, MemoryPropertyFlags wanted) {
        for (var index = 0; index < memory.MemoryTypeCount; index++) {
            if ((typeBits & (1u << index)) == 0) {
                continue;
            }

            if ((memory.MemoryTypes[index].PropertyFlags & wanted) == wanted) {
                return index;
            }
        }

        return -1;
    }
}

/// <summary>Device memory: allocated in blocks, handed out in pieces.</summary>
/// <remarks>
///     <para>
///         One <c>vkAllocateMemory</c> per resource is the obvious implementation and the wrong one:
///         drivers cap the number of live allocations — <c>maxMemoryAllocationCount</c> is 4096 on a
///         great many of them — and a scene with more textures than that simply cannot be loaded.
///         So: large blocks, suballocated by <see cref="Suballocator" />.
///     </para>
///     <para>
///         Buffers and images are kept in separate blocks rather than sharing one. Vulkan's
///         <c>bufferImageGranularity</c> says a linear and an optimally-tiled resource may not share a
///         granularity page, and honouring that within a block means tracking the tiling of every
///         neighbour of every free run. Separate pools make the rule impossible to violate and cost
///         at most one extra part-used block per memory type — a trade the specification's own usage
///         guide recommends.
///     </para>
///     <para>
///         Host-visible blocks are mapped once, at creation, and stay mapped. Repeated
///         map/unmap around each write is a driver call pair per upload and, on some drivers, a real
///         cost; persistent mapping is legal, universal, and what every production allocator does.
///     </para>
/// </remarks>
sealed unsafe class VulkanAllocator : IDisposable {
    /// <summary>How large a block is.</summary>
    /// <remarks>
    ///     256 MiB is a common choice and too large for a first backend: a device with 1 GiB of
    ///     device-local memory would lose a quarter of it to the first texture. 64 MiB keeps waste
    ///     bounded on small devices and still keeps the allocation count in the dozens.
    /// </remarks>
    public const long BlockSize = 64L * 1024 * 1024;

    readonly Vk api;
    readonly Device device;
    readonly PhysicalDeviceMemoryProperties memory;
    // Device-address blocks are their own pools, not a flag on the shared ones: the allocate flag
    // applies to a whole vkAllocateMemory, so one addressed buffer in an ordinary block would need
    // every block to carry the flag — which is legal from 1.2 but disables suballocation-friendly
    // fast paths on some drivers, and pays for a niche feature on every texture in the scene.
    readonly Dictionary<(int Type, bool Linear, bool DeviceAddress), List<Block>> pools = [];
    readonly Lock gate = new();

    bool disposed;

    public VulkanAllocator(Vk api, Device device, PhysicalDeviceMemoryProperties memory) {
        this.api = api;
        this.device = device;
        this.memory = memory;
    }

    /// <summary>How many <c>vkAllocateMemory</c> calls are live.</summary>
    /// <remarks>
    ///     Exposed because it is the number the driver caps, and a backend that cannot say what it is
    ///     cannot explain the failure when it is reached.
    /// </remarks>
    public int LiveBlockCount {
        get {
            lock (gate) {
                var count = 0;

                foreach (var pool in pools.Values) {
                    count += pool.Count;
                }

                return count;
            }
        }
    }

    /// <summary>Finds room for a resource.</summary>
    /// <param name="requirements">What <c>vkGetBufferMemoryRequirements</c> and friends said.</param>
    /// <param name="access">Where the caller said the memory should live.</param>
    /// <param name="linear">Whether the resource is a buffer rather than an optimally-tiled image.</param>
    /// <param name="deviceAddress">
    ///     Whether the resource's GPU address will be taken. A property of the <em>allocation</em>,
    ///     not the buffer: <c>vkGetBufferDeviceAddress</c> on memory allocated without
    ///     <c>VK_MEMORY_ALLOCATE_DEVICE_ADDRESS_BIT</c> is invalid usage the layers report and a
    ///     release driver answers with garbage an acceleration-structure build then walks.
    /// </param>
    /// <exception cref="InvalidOperationException">No memory type qualifies, or the device is out.</exception>
    public VulkanAllocation Allocate(
        in MemoryRequirements requirements,
        MemoryAccess access,
        bool linear,
        bool deviceAddress = false
    ) {
        var (required, preferred) = MemoryTypeSelection.For(access);
        var type = MemoryTypeSelection.Select(memory, requirements.MemoryTypeBits, required, preferred);

        if (type < 0) {
            throw new InvalidOperationException(
                $"No memory type on this device satisfies {access} for a resource that accepts types "
                + $"0x{requirements.MemoryTypeBits:X}. Required properties: {required}."
            );
        }

        var size = (long)requirements.Size;
        var alignment = (long)requirements.Alignment;

        lock (gate) {
            // Too large to share. A dedicated allocation is what the driver would have done anyway,
            // and rounding up to a block would waste more than it saves.
            if (size > BlockSize) {
                var (dedicated, mapped) = CreateBlock(type, size, deviceAddress);
                return new(dedicated.Memory, 0, size, type, -1, linear, mapped, deviceAddress);
            }

            var pool = Pool(type, linear, deviceAddress);

            for (var index = 0; index < pool.Count; index++) {
                if (pool[index].Space.TryAllocate(size, alignment, out var offset)) {
                    return new(
                        pool[index].Memory,
                        offset,
                        size,
                        type,
                        index,
                        linear,
                        Advance(pool[index].Mapped, offset),
                        deviceAddress
                    );
                }
            }

            var (block, blockMapped) = CreateBlock(type, BlockSize, deviceAddress);
            pool.Add(block);

            if (!block.Space.TryAllocate(size, alignment, out var fresh)) {
                throw new InvalidOperationException(
                    $"A {size}-byte allocation did not fit in a freshly created {BlockSize}-byte block, "
                    + $"which should be impossible; alignment was {alignment}."
                );
            }

            return new(
                block.Memory,
                fresh,
                size,
                type,
                pool.Count - 1,
                linear,
                Advance(blockMapped, fresh),
                deviceAddress
            );
        }
    }

    /// <summary>Gives memory back.</summary>
    /// <param name="allocation">What was handed out.</param>
    /// <remarks>
    ///     An emptied block is freed rather than kept. Keeping it would make a level transition — free
    ///     everything, allocate everything — cost nothing, and would also mean a process that
    ///     allocated a gigabyte once holds a gigabyte forever. The block is cheap to recreate.
    /// </remarks>
    public void Free(in VulkanAllocation allocation) {
        if (allocation.Memory.Handle == 0) {
            return;
        }

        lock (gate) {
            if (disposed) {
                return;
            }

            if (allocation.BlockIndex < 0) {
                api.FreeMemory(device, allocation.Memory, null);
                return;
            }

            var pool = Pool(allocation.TypeIndex, allocation.Linear, allocation.DeviceAddress);

            if (allocation.BlockIndex >= pool.Count) {
                return;
            }

            var block = pool[allocation.BlockIndex];
            block.Space.Free(allocation.Offset, allocation.Size);

            if (!block.Space.IsEmpty) {
                return;
            }

            // Only the last block may be dropped: every live allocation holds an index into this
            // list, and removing from the middle would silently repoint all of them.
            if (allocation.BlockIndex == pool.Count - 1) {
                Destroy(block);
                pool.RemoveAt(allocation.BlockIndex);

                while (pool.Count > 0 && pool[^1].Space.IsEmpty) {
                    Destroy(pool[^1]);
                    pool.RemoveAt(pool.Count - 1);
                }
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

            foreach (var pool in pools.Values) {
                foreach (var block in pool) {
                    Destroy(block);
                }
            }

            pools.Clear();
        }
    }

    static nint Advance(nint pointer, long offset) => pointer == 0 ? 0 : pointer + (nint)offset;

    List<Block> Pool(int type, bool linear, bool deviceAddress) {
        if (!pools.TryGetValue((type, linear, deviceAddress), out var pool)) {
            pool = [];
            pools[(type, linear, deviceAddress)] = pool;
        }

        return pool;
    }

    (Block Block, nint Mapped) CreateBlock(int type, long size, bool deviceAddress) {
        // On the WHOLE block, which is why addressed blocks pool separately: the flag belongs to
        // the vkAllocateMemory, and taking an address out of a block allocated without it is
        // invalid usage that reads as a working pointer right up until a build dereferences it.
        var flags = new MemoryAllocateFlagsInfo {
            SType = StructureType.MemoryAllocateFlagsInfo,
            Flags = MemoryAllocateFlags.DeviceAddressBit
        };

        var info = new MemoryAllocateInfo {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = (ulong)size,
            MemoryTypeIndex = (uint)type,
            PNext = deviceAddress ? &flags : null
        };

        DeviceMemory handle;
        var result = api.AllocateMemory(device, &info, null, &handle);

        if (result != Result.Success) {
            throw new InvalidOperationException(
                $"vkAllocateMemory failed with {result} for {size} bytes of memory type {type}. "
                + $"{LiveBlockCount} blocks are already live."
            );
        }

        nint mapped = 0;

        if ((memory.MemoryTypes[type].PropertyFlags & MemoryPropertyFlags.HostVisibleBit) != 0) {
            void* address;

            if (api.MapMemory(device, handle, 0, (ulong)size, 0, &address) == Result.Success) {
                mapped = (nint)address;
            }
        }

        return (new(handle, new(size), mapped), mapped);
    }

    void Destroy(Block block) {
        if (block.Mapped != 0) {
            api.UnmapMemory(device, block.Memory);
        }

        api.FreeMemory(device, block.Memory, null);
    }

    sealed record Block(DeviceMemory Memory, Suballocator Space, nint Mapped);
}

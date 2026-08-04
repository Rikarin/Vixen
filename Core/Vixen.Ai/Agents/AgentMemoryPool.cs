// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ai;

/// <summary>One agent's block of state, as the eight bytes a component holds.</summary>
/// <param name="Index">Its block.</param>
/// <param name="Generation">Which rental that block is holding.</param>
/// <remarks>
///     Generational, so that a handle that arrived on a prefab, a save file or a destroyed entity
///     names nothing rather than naming somebody else's memory.
/// </remarks>
public readonly record struct AgentMemoryHandle(int Index, uint Generation) {
    /// <summary>The handle that names no block.</summary>
    public static AgentMemoryHandle Null => new(-1, 0);

    /// <summary>Whether this names no block.</summary>
    public bool IsNull => Index < 0;
}

/// <summary>
///     Fixed-size blocks of per-agent state, carved out of pages and handed back on a free list.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is what makes "one asset, a thousand agents" cost one allocation each, at load.</b>
///         The asset — a behaviour-tree template, a utility set, a plan — is immutable and shared and
///         has no per-agent field anywhere; everything that varies per agent is in a block here, and
///         a node is handed a window into it. Unreal's behaviour-tree component holds the same
///         <c>TArray&lt;uint8&gt;</c> for the same reason, and it is the correct design for an ECS
///         engine rather than merely a workable one.
///     </para>
///     <para>
///         ⚠ <b>Pages, not one growable array, and the reason is a dangling span.</b> A single arena
///         that doubled would move every byte in it, quietly invalidating every
///         <see cref="Span{T}" /> a caller was holding — which for a system that resolves a block and
///         then ticks an action is a use-after-free with no symptom until it has one. A page is
///         allocated once and never moves, so growth is a new page and every outstanding span stays
///         valid.
///     </para>
///     <para>
///         <b>The free list is per size.</b> A thousand agents on one tree all want the same number
///         of bytes, so a rental is a pop and a return is a push, with no search and no
///         fragmentation to manage. A pool whose agents run twenty different templates holds twenty
///         lists, which is twenty integers.
///     </para>
///     <para>
///         ⚠ <b>A returned block is zeroed on rental, not on return.</b> Zeroing on return means
///         paying for a block nobody rents again; zeroing on rental means an action's
///         <c>Start</c> always sees a clean span, which is the guarantee <c>IAgentAction</c> makes.
///     </para>
/// </remarks>
public sealed class AgentMemoryPool {
    /// <summary>Blocks are eight-byte aligned, so a struct with a long or a double in it fits.</summary>
    const int Alignment = 8;

    readonly List<byte[]> pages = [];
    readonly Dictionary<int, int> freeBySize = [];
    readonly int pageSize;

    Block[] blocks = [];
    int blockCount;
    int pageUsed;
    uint nextGeneration = 1;

    /// <summary>Creates a pool.</summary>
    /// <param name="pageSize">
    ///     How many bytes each page holds. A block may not be larger than this, and the default is
    ///     large enough for any state an authored asset produces.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageSize" /> is not positive.</exception>
    public AgentMemoryPool(int pageSize = 64 * 1024) {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, Alignment);

        this.pageSize = pageSize;
    }

    /// <summary>How many blocks have ever been carved, rented or not.</summary>
    public int BlockCount => blockCount;

    /// <summary>How many blocks are rented out right now.</summary>
    public int RentedCount { get; private set; }

    /// <summary>How many bytes the pool has allocated in total.</summary>
    public long Capacity => (long)pages.Count * pageSize;

    /// <summary>Takes a zeroed block.</summary>
    /// <param name="size">How many bytes. Zero is legal and gives an empty block.</param>
    /// <returns>A handle to it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="size" /> is negative, or larger than a page.
    /// </exception>
    public AgentMemoryHandle Rent(int size) {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, pageSize);

        var rounded = (size + Alignment - 1) / Alignment * Alignment;

        if (freeBySize.TryGetValue(rounded, out var head) && head >= 0) {
            freeBySize[rounded] = blocks[head].NextFree;

            return Take(head, size);
        }

        return Take(Carve(rounded), size);
    }

    /// <summary>Gives a block back.</summary>
    /// <param name="handle">The handle.</param>
    /// <returns>Whether it was a live rental.</returns>
    public bool Return(AgentMemoryHandle handle) {
        if (!Live(handle, out var index)) {
            return false;
        }

        var rounded = blocks[index].Capacity;

        blocks[index].Rented = false;
        blocks[index].NextFree = freeBySize.TryGetValue(rounded, out var head) ? head : -1;
        freeBySize[rounded] = index;
        RentedCount--;

        return true;
    }

    /// <summary>The bytes a handle names.</summary>
    /// <param name="handle">The handle.</param>
    /// <param name="state">Where to put them.</param>
    /// <returns>Whether it was a live rental.</returns>
    /// <remarks>
    ///     The span is valid until the block is returned. It survives further rentals, which is the
    ///     property the paged arena exists to give.
    /// </remarks>
    public bool TryResolve(AgentMemoryHandle handle, out Span<byte> state) {
        if (!Live(handle, out var index)) {
            state = default;

            return false;
        }

        state = pages[blocks[index].Page].AsSpan(blocks[index].Offset, blocks[index].Size);

        return true;
    }

    /// <summary>The bytes a handle names, or nothing.</summary>
    /// <param name="handle">The handle.</param>
    /// <returns>The block, or an empty span if the handle is stale.</returns>
    /// <remarks>
    ///     An empty span rather than an exception, because a stale handle is what a system sees when
    ///     an entity was recreated from a save or a prefab, and refusing to run that agent is more
    ///     useful than stopping the frame.
    /// </remarks>
    public Span<byte> Resolve(AgentMemoryHandle handle) => TryResolve(handle, out var state) ? state : default;

    AgentMemoryHandle Take(int index, int size) {
        blocks[index].Size = size;
        blocks[index].Rented = true;
        blocks[index].Generation = nextGeneration++;
        blocks[index].NextFree = -1;
        RentedCount++;

        pages[blocks[index].Page].AsSpan(blocks[index].Offset, blocks[index].Capacity).Clear();

        return new(index, blocks[index].Generation);
    }

    int Carve(int rounded) {
        if (pages.Count == 0 || pageUsed + rounded > pageSize) {
            pages.Add(new byte[pageSize]);
            pageUsed = 0;
        }

        if (blockCount == blocks.Length) {
            Array.Resize(ref blocks, Math.Max(16, blocks.Length * 2));
        }

        blocks[blockCount] = new() {
            Page = pages.Count - 1,
            Offset = pageUsed,
            Capacity = rounded,
            NextFree = -1
        };

        pageUsed += rounded;

        return blockCount++;
    }

    bool Live(AgentMemoryHandle handle, out int index) {
        index = handle.Index;

        return (uint)index < (uint)blockCount
            && blocks[index].Rented
            && blocks[index].Generation == handle.Generation;
    }

    /// <summary>One block: where it is, how big it is, and whether anybody has it.</summary>
    struct Block {
        public int Page;
        public int Offset;

        /// <summary>How many bytes were carved, rounded up to <see cref="Alignment" />.</summary>
        public int Capacity;

        /// <summary>How many bytes the current rental asked for.</summary>
        public int Size;

        public bool Rented;
        public uint Generation;
        public int NextFree;
    }
}

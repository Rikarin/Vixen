// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace Vixen.Core.Memory;

/// <summary>
///     A bump allocator: hand out memory by advancing a pointer, and reclaim all of it at once by
///     moving the pointer back.
/// </summary>
/// <remarks>
///     <para>
///         Allocation is an add and a compare. There is no free list, no size classes, no
///         fragmentation, and — the point — <b>no per-allocation release</b>. That is exactly right
///         for memory whose lifetime is a frame or a scope: render command payloads, culling
///         results, layout scratch, the intermediate arrays of a single pass.
///     </para>
///     <para>
///         It is exactly wrong for anything that outlives the reset. A pointer handed out before a
///         <see cref="Reset" /> points into memory that is about to be handed to somebody else, and
///         nothing here will tell you. Scope the arena tightly and the property becomes a guarantee
///         rather than a hazard.
///     </para>
///     <para>
///         Memory comes in blocks that are kept and reused across resets, so a steady workload stops
///         calling the system allocator entirely after the first few frames. Not thread-safe: each
///         thread gets its own — see <see cref="FrameArena" />.
///     </para>
/// </remarks>
public sealed unsafe class ArenaAllocator : IDisposable {
    /// <summary>The block size used when none is given: 1 MiB.</summary>
    public const int DefaultBlockSize = 1 << 20;

    readonly int blockSize;
    readonly List<Block> blocks = [];
    readonly long trackingHandle;

    int currentBlock = -1;
    nuint offset;
    bool disposed;

    /// <summary>How many bytes have been handed out since the last reset.</summary>
    public long BytesAllocated { get; private set; }

    /// <summary>How many bytes the arena holds across all its blocks.</summary>
    public long BytesReserved { get; private set; }

    /// <summary>How many blocks the arena has taken from the system allocator.</summary>
    public int BlockCount => blocks.Count;

    /// <summary>
    ///     The high-water mark of <see cref="BytesAllocated" /> across every reset, which is the
    ///     number to size the arena from.
    /// </summary>
    public long PeakBytesAllocated { get; private set; }

    /// <summary>Creates an arena that takes memory in blocks of a given size.</summary>
    /// <param name="blockSize">
    ///     Bytes per block. An allocation larger than this gets a block of its own, so the size is a
    ///     tuning knob and not a limit.
    /// </param>
    /// <param name="name">A debug name, recorded with the allocation when leak tracking is on.</param>
    public ArenaAllocator(int blockSize = DefaultBlockSize, string? name = null) {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 64);
        this.blockSize = blockSize;
        trackingHandle = LeakTracker.Track("ArenaAllocator", name ?? $"{blockSize} byte blocks");
    }

    /// <summary>Hands out <paramref name="byteCount" /> bytes of uninitialised memory.</summary>
    /// <param name="byteCount">How many bytes.</param>
    /// <param name="alignment">The byte alignment. Must be a power of two.</param>
    /// <returns>A pointer valid until the next <see cref="Reset" />.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    /// <exception cref="ObjectDisposedException">The arena has been disposed.</exception>
    public void* Allocate(nuint byteCount, int alignment = 16) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);

        if ((alignment & (alignment - 1)) != 0) {
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment, "Alignment must be a power of two.");
        }

        if (byteCount == 0) {
            return null;
        }

        var aligned = Align(offset, (nuint)alignment);

        if (currentBlock < 0 || aligned + byteCount > blocks[currentBlock].Size) {
            AllocateBlock(byteCount, (nuint)alignment);
            aligned = Align(offset, (nuint)alignment);
        }

        var result = (byte*)blocks[currentBlock].Pointer + aligned;
        offset = aligned + byteCount;

        BytesAllocated += (long)byteCount;
        PeakBytesAllocated = Math.Max(PeakBytesAllocated, BytesAllocated);
        return result;
    }

    /// <summary>Hands out room for <paramref name="count" /> elements, as a span.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="count">How many elements.</param>
    /// <returns>A span valid until the next <see cref="Reset" />. Its contents are undefined.</returns>
    public Span<T> Allocate<T>(int count) where T : unmanaged {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        return count == 0
            ? default
            : new(Allocate((nuint)((long)count * sizeof(T)), Math.Max(sizeof(T), 1)), count);
    }

    /// <summary>Hands out room for <paramref name="count" /> elements, zeroed.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="count">How many elements.</param>
    /// <returns>A zeroed span valid until the next <see cref="Reset" />.</returns>
    public Span<T> AllocateZeroed<T>(int count) where T : unmanaged {
        var span = Allocate<T>(count);
        span.Clear();
        return span;
    }

    /// <summary>
    ///     Reclaims everything at once. The blocks are kept, so the next frame allocates out of
    ///     memory that is already warm.
    /// </summary>
    /// <remarks>
    ///     <b>Every pointer this arena has handed out becomes dangling.</b> That is the contract, not
    ///     a caveat: the whole reason a bump allocator is this cheap is that it does not track what
    ///     is still in use.
    /// </remarks>
    public void Reset() {
        currentBlock = blocks.Count > 0 ? 0 : -1;
        offset = 0;
        BytesAllocated = 0;
    }

    /// <summary>
    ///     Opens a nested scope that rewinds to where it started when disposed.
    /// </summary>
    /// <returns>A scope to <c>using</c>.</returns>
    /// <remarks>
    ///     For scratch inside a frame, without waiting for the frame's own reset. Scopes have to
    ///     close in the order they opened — they are a stack, and disposing an outer one first
    ///     silently strands the inner one's memory.
    /// </remarks>
    public Scope Push() {
        ObjectDisposedException.ThrowIf(disposed, this);
        return new(this, currentBlock, offset, BytesAllocated);
    }

    /// <summary>Frees every block. The arena is unusable afterwards.</summary>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        LeakTracker.Untrack(trackingHandle);

        foreach (var block in blocks) {
            NativeMemory.AlignedFree(block.Pointer);
        }

        blocks.Clear();
        BytesReserved = 0;
    }

    void AllocateBlock(nuint byteCount, nuint alignment) {
        // A block already exists past this one from a previous cycle, and it is big enough: reuse
        // it. This is what makes a steady workload stop calling the system allocator.
        for (var next = currentBlock + 1; next < blocks.Count; next++) {
            if (blocks[next].Size >= Align(byteCount, alignment)) {
                currentBlock = next;
                offset = 0;
                return;
            }
        }

        // An allocation larger than the block size gets a block sized to it, so the block size stays
        // a tuning knob rather than a ceiling.
        var size = (nuint)Math.Max((long)byteCount + (long)alignment, blockSize);
        var pointer = NativeMemory.AlignedAlloc(size, (nuint)Math.Max((int)alignment, 64));

        blocks.Add(new(pointer, size));
        currentBlock = blocks.Count - 1;
        offset = 0;
        BytesReserved += (long)size;
    }

    static nuint Align(nuint value, nuint alignment) => (value + alignment - 1) & ~(alignment - 1);

    readonly struct Block(void* pointer, nuint size) {
        public readonly void* Pointer = pointer;
        public readonly nuint Size = size;
    }

    /// <summary>
    ///     A nested allocation scope. Disposing it rewinds the arena to where the scope began.
    /// </summary>
    public readonly struct Scope : IDisposable {
        readonly ArenaAllocator? arena;
        readonly int block;
        readonly nuint offset;
        readonly long allocated;

        internal Scope(ArenaAllocator arena, int block, nuint offset, long allocated) {
            this.arena = arena;
            this.block = block;
            this.offset = offset;
            this.allocated = allocated;
        }

        /// <summary>Rewinds the arena to where this scope began.</summary>
        public void Dispose() {
            if (arena is null || arena.disposed) {
                return;
            }

            arena.currentBlock = block;
            arena.offset = offset;
            arena.BytesAllocated = allocated;
        }
    }
}

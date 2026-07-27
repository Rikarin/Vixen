// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;

namespace Vixen.Ui.Reactive;

/// <summary>
///     One end of a dependency edge, stored in the arrays <see cref="EdgePool" /> hands out.
/// </summary>
/// <remarks>
///     Every edge is recorded twice — once in the consumer's producer list and once in the producer's
///     live-consumer list — and each copy carries the index of its twin. That is what makes removing
///     an edge O(1) from either side, which matters because a computed that stops reading a signal
///     has to be unhooked from it without either of them scanning a list.
/// </remarks>
struct Edge {
    /// <summary>The node at the other end. Null in a slot that is not in use.</summary>
    public ReactiveNode? Node;

    /// <summary>
    ///     The producer's <see cref="ReactiveNode.Version" /> when this consumer last read it. Only
    ///     meaningful in a producer list; a live-consumer edge leaves it zero.
    /// </summary>
    public uint SeenVersion;

    /// <summary>The index of the twin edge in the other node's list.</summary>
    public int TwinIndex;
}

/// <summary>
///     Free lists of <see cref="Edge" /> arrays, bucketed by power-of-two length, so that building
///     and tearing down parts of a UI does not churn the GC heap.
/// </summary>
/// <remarks>
///     <para>
///         Doc 09 specifies "slices of a shared <c>ChunkedArray&lt;Edge&gt;</c> with free lists".
///         This is that idea with the arena taken out, for a reason found while writing it: a slice
///         has to be one contiguous <c>Span</c>, chunks are not contiguous with each other, and so an
///         arena needs either a hard cap on edges per node at the chunk size or a second allocation
///         path for the nodes that exceed it. Pooling whole arrays gives the property that was
///         actually wanted — no per-node allocation once the graph is warm, and storage reused across
///         nodes that come and go — with no cap and no special case.
///     </para>
///     <para>
///         The pool is per-thread. Signals are single-threaded by contract
///         (<see cref="ReactiveGraph.OwningThread" />), and a lock on the hottest structural
///         operation in the UI would be paying for a guarantee the design does not need. An array
///         rented on one thread and returned on another is simply reused there, which is harmless.
///     </para>
/// </remarks>
static class EdgePool {
    /// <summary>The shortest array handed out. Most nodes have one or two dependencies.</summary>
    internal const int MinimumLength = 4;

    const int MinimumLengthLog2 = 2;

    /// <summary>
    ///     How many arrays are kept per bucket. Past this the array is dropped and collected —
    ///     a pool that never lets go turns a one-off spike into a permanent footprint.
    /// </summary>
    const int MaximumRetainedPerBucket = 32;

    /// <summary>Buckets for lengths 4 … 2²⁵. Anything longer is allocated and not pooled.</summary>
    const int BucketCount = 24;

    [ThreadStatic] static Edge[][][]? free;
    [ThreadStatic] static int[]? freeCount;

    /// <summary>An array of at least <paramref name="minimumLength" /> zeroed edges.</summary>
    /// <param name="minimumLength">The number of edges the caller needs to store.</param>
    /// <returns>A pooled or newly allocated array, every slot of which is <see langword="default" />.</returns>
    public static Edge[] Rent(int minimumLength) {
        var length = int.Max(MinimumLength, (int) BitOperations.RoundUpToPowerOf2((uint) minimumLength));
        var bucket = BucketOf(length);

        if (bucket < BucketCount && freeCount is not null && freeCount[bucket] > 0) {
            // The slot keeps its reference rather than being nulled out. It holds a cleared array,
            // so it retains nothing, and `freeCount` is the single source of truth for what is
            // available — the next Return overwrites this same slot.
            return free![bucket][--freeCount[bucket]];
        }

        return new Edge[length];
    }

    /// <summary>Gives an array back, cleared so it retains nothing.</summary>
    /// <param name="array">An array previously returned by <see cref="Rent" />.</param>
    /// <remarks>
    ///     The clear is not hygiene: an edge holds a strong reference to a <see cref="ReactiveNode" />,
    ///     and a pooled array that kept those alive would make the pool a leak proportional to the
    ///     largest UI ever built.
    /// </remarks>
    public static void Return(Edge[] array) {
        var bucket = BucketOf(array.Length);
        if (array.Length < MinimumLength || bucket >= BucketCount) {
            return;
        }

        Array.Clear(array);

        var lists = free;
        if (lists is null) {
            lists = free = new Edge[BucketCount][][];
            freeCount = new int[BucketCount];
            for (var i = 0; i < BucketCount; i++) {
                lists[i] = new Edge[MaximumRetainedPerBucket][];
            }
        }

        var count = freeCount![bucket];
        if (count == MaximumRetainedPerBucket) {
            return;
        }

        lists[bucket][count] = array;
        freeCount[bucket] = count + 1;
    }

    static int BucketOf(int length) => BitOperations.TrailingZeroCount((uint) length) - MinimumLengthLog2;
}

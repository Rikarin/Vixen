// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Navigation.Agents;

/// <summary>A search somebody asked for.</summary>
/// <param name="Index">Its slot.</param>
/// <param name="Generation">Which request that slot is holding.</param>
public readonly record struct NavPathRequest(int Index, uint Generation) {
    /// <summary>The request that names no search.</summary>
    public static NavPathRequest Null => new(-1, 0);

    /// <summary>Whether this names no search.</summary>
    public bool IsNull => Index < 0;
}

/// <summary>How a request is getting on.</summary>
public enum NavPathRequestState {
    /// <summary>The queue has never heard of it, or it has been collected already.</summary>
    Unknown,

    /// <summary>Waiting for a query to be free.</summary>
    Queued,

    /// <summary>Being searched, a slice at a time.</summary>
    Working,

    /// <summary>Finished. The path is waiting to be taken.</summary>
    Ready
}

/// <summary>
///     Searches, run a slice at a time against a fixed budget, so that a crowd changing its mind all
///     at once costs a frame what one search costs rather than what all of them do.
/// </summary>
/// <remarks>
///     <para>
///         The problem is measured rather than imagined: a search across an eighty-metre level is
///         about thirteen microseconds, so two hundred and fifty-six agents given a new destination in
///         the same update is three and a half milliseconds of pathfinding — more than the whole rest
///         of the crowd, and all of it in one frame. Spread over ten frames nobody notices; taken at
///         once it is a visible hitch, and it happens exactly when something interesting has happened
///         in the game.
///     </para>
///     <para>
///         <b>The queue is not asynchronous and does not use threads.</b> It is a budget: every
///         <see cref="Update" /> runs at most <c>iterations</c> polygon expansions in total, shared
///         between however many searches are in flight. That is enough to fix the spike, it keeps the
///         determinism the content build and the tests rely on, and it leaves the door open for a job
///         per query later without changing what a caller sees.
///     </para>
///     <para>
///         A request whose result is never taken holds its slot until the queue runs out and starts
///         refusing new ones. <see cref="Cancel" /> is how an agent that changed its mind again gives
///         the slot back.
///     </para>
/// </remarks>
public sealed class NavPathQueue {
    readonly NavMeshQuery[] queries;
    readonly int[] assignments;
    readonly Slot[] slots;
    readonly Queue<int> waiting = new();

    uint nextGeneration = 1;

    /// <summary>Creates a queue over a mesh.</summary>
    /// <param name="mesh">The mesh to search.</param>
    /// <param name="capacity">How many requests may be outstanding at once.</param>
    /// <param name="maximumPathLength">The longest path a result may hold.</param>
    /// <param name="parallelSearches">
    ///     How many searches may be part-way at once. Each is a query of its own, which is a node pool
    ///     and an open list — a handful is plenty, because the budget is what limits throughput.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="mesh" /> is null.</exception>
    public NavPathQueue(NavMesh mesh, int capacity = 64, int maximumPathLength = 256, int parallelSearches = 4) {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPathLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelSearches, 1);

        Mesh = mesh;
        queries = new NavMeshQuery[parallelSearches];
        assignments = new int[parallelSearches];
        slots = new Slot[capacity];

        for (var index = 0; index < parallelSearches; index++) {
            queries[index] = new(mesh);
            assignments[index] = -1;
        }

        for (var index = 0; index < capacity; index++) {
            slots[index] = new() { Path = new NavPolyRef[maximumPathLength] };
        }
    }

    /// <summary>The mesh being searched.</summary>
    public NavMesh Mesh { get; }

    /// <summary>How many requests are waiting or being worked on.</summary>
    public int PendingCount { get; private set; }

    /// <summary>How many polygons the last <see cref="Update" /> expanded, across every search.</summary>
    public int LastIterations { get; private set; }

    /// <summary>Asks for a path.</summary>
    /// <param name="start">The polygon to start on.</param>
    /// <param name="end">The polygon to reach.</param>
    /// <param name="startPosition">Where on the start polygon.</param>
    /// <param name="endPosition">Where on the end polygon.</param>
    /// <param name="filter">Which polygons may be crossed.</param>
    /// <returns>The request, or <see cref="NavPathRequest.Null" /> if the queue is full.</returns>
    /// <remarks>
    ///     A full queue is a refusal rather than a wait, so that the caller decides what to do about
    ///     it — an agent whose request was refused keeps walking its old corridor and asks again next
    ///     update, which is the behaviour anybody would have written anyway.
    /// </remarks>
    public NavPathRequest Submit(
        NavPolyRef start,
        NavPolyRef end,
        Vector3 startPosition,
        Vector3 endPosition,
        NavQueryFilter filter
    ) {
        ArgumentNullException.ThrowIfNull(filter);

        for (var index = 0; index < slots.Length; index++) {
            if (slots[index].State != NavPathRequestState.Unknown) {
                continue;
            }

            slots[index].Generation = nextGeneration++;
            slots[index].State = NavPathRequestState.Queued;
            slots[index].Start = start;
            slots[index].End = end;
            slots[index].StartPosition = startPosition;
            slots[index].EndPosition = endPosition;
            slots[index].Filter = filter;
            slots[index].Count = 0;
            slots[index].Status = NavPathStatus.Failed;

            waiting.Enqueue(index);
            PendingCount++;

            return new(index, slots[index].Generation);
        }

        return NavPathRequest.Null;
    }

    /// <summary>Gives a request's slot back, whether or not it has finished.</summary>
    /// <param name="request">The request.</param>
    /// <returns><see langword="false" /> if it was not a live request.</returns>
    public bool Cancel(NavPathRequest request) {
        if (!TryResolve(request, out var index)) {
            return false;
        }

        if (slots[index].State != NavPathRequestState.Ready) {
            PendingCount--;
        }

        for (var query = 0; query < assignments.Length; query++) {
            if (assignments[query] == index) {
                assignments[query] = -1;
            }
        }

        slots[index].State = NavPathRequestState.Unknown;
        slots[index].Filter = null;

        return true;
    }

    /// <summary>How a request is getting on.</summary>
    /// <param name="request">The request.</param>
    /// <returns>Its state.</returns>
    public NavPathRequestState GetState(NavPathRequest request) =>
        TryResolve(request, out var index) ? slots[index].State : NavPathRequestState.Unknown;

    /// <summary>Runs the searches for a while.</summary>
    /// <param name="iterations">
    ///     How many polygon expansions the whole queue may do. Shared between the searches in flight,
    ///     so the cost of an update is bounded whatever the crowd is doing.
    /// </param>
    public void Update(int iterations = 128) {
        LastIterations = 0;

        if (iterations <= 0) {
            return;
        }

        // Divided rather than given to the first search, so that one long search cannot starve the
        // rest — an agent whose destination is across the level should not hold up everybody who only
        // wants to walk to the next room.
        var share = Math.Max(1, iterations / queries.Length);

        for (var query = 0; query < queries.Length && LastIterations < iterations; query++) {
            if (assignments[query] < 0 && !TryStart(query)) {
                continue;
            }

            var index = assignments[query];
            var status = queries[query].UpdateSlicedFindPath(share, out var performed);

            LastIterations += performed;

            if (status == NavPathStatus.Partial) {
                continue;
            }

            slots[index].Status = queries[query].FinalizeSlicedFindPath(slots[index].Path, out var count);
            slots[index].Count = count;
            slots[index].State = NavPathRequestState.Ready;
            assignments[query] = -1;
            PendingCount--;

            // The query is free again, so let it pick up whatever is waiting rather than idling until
            // the next update.
            if (LastIterations < iterations) {
                query--;
            }
        }
    }

    /// <summary>Takes a finished path, and gives the slot back.</summary>
    /// <param name="request">The request.</param>
    /// <param name="path">Where to write the polygons.</param>
    /// <param name="count">How many were written.</param>
    /// <param name="status">How much of the path was found.</param>
    /// <returns><see langword="false" /> if the search has not finished.</returns>
    public bool TryTakeResult(NavPathRequest request, Span<NavPolyRef> path, out int count, out NavPathStatus status) {
        count = 0;
        status = NavPathStatus.Failed;

        if (!TryResolve(request, out var index) || slots[index].State != NavPathRequestState.Ready) {
            return false;
        }

        count = Math.Min(slots[index].Count, path.Length);
        slots[index].Path.AsSpan(0, count).CopyTo(path);

        // Truncating on the way out is the same answer the search gives when its own buffer is too
        // small: the front of the path is what matters, and the agent will ask again later.
        status = count < slots[index].Count ? NavPathStatus.Partial : slots[index].Status;

        slots[index].State = NavPathRequestState.Unknown;
        slots[index].Filter = null;

        return true;
    }

    bool TryStart(int query) {
        while (waiting.Count > 0) {
            var index = waiting.Dequeue();

            if (slots[index].State != NavPathRequestState.Queued) {
                continue;
            }

            var status = queries[query].InitSlicedFindPath(
                slots[index].Start,
                slots[index].End,
                slots[index].StartPosition,
                slots[index].EndPosition,
                slots[index].Filter!
            );

            if (status == NavPathStatus.Failed) {
                slots[index].Status = NavPathStatus.Failed;
                slots[index].Count = 0;
                slots[index].State = NavPathRequestState.Ready;
                PendingCount--;

                continue;
            }

            slots[index].State = NavPathRequestState.Working;
            assignments[query] = index;

            return true;
        }

        return false;
    }

    bool TryResolve(NavPathRequest request, out int index) {
        index = request.Index;

        return (uint)index < (uint)slots.Length
            && slots[index].Generation == request.Generation
            && slots[index].State != NavPathRequestState.Unknown;
    }

    /// <summary>One outstanding request, and the path it will produce.</summary>
    struct Slot {
        public uint Generation;
        public NavPathRequestState State;
        public NavPolyRef Start;
        public NavPolyRef End;
        public Vector3 StartPosition;
        public Vector3 EndPosition;
        public NavQueryFilter? Filter;
        public NavPolyRef[] Path;
        public int Count;
        public NavPathStatus Status;
    }
}

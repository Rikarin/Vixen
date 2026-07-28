// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;

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
///         <b>It is a budget first and threads second.</b> Every <see cref="Update" /> runs at most
///         <c>iterations</c> polygon expansions in total, shared between however many searches are in
///         flight, and that alone is enough to fix the spike. Setting <see cref="Scheduler" /> then
///         runs each search's slice on a job instead of on the caller's thread — the searches are
///         already independent, one query object each.
///     </para>
///     <para>
///         <b>The two run the same rounds and give the same answers in the same updates.</b> An update
///         is a sequence of rounds: assign every free query from the waiting list, advance every
///         assigned query by its share, collect whatever finished. Nothing in that depends on which
///         thread ran what or how fast, so a scheduler changes where the work happens and nothing
///         else — a test asserts it, the same way the sliced search is asserted to agree with the
///         whole one.
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
    readonly NavPathStatus[] statuses;
    readonly int[] performed;
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
        statuses = new NavPathStatus[parallelSearches];
        performed = new int[parallelSearches];
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

    /// <summary>Where to run the searches, or <see langword="null" /> to run them on the caller's thread.</summary>
    /// <remarks>
    ///     <para>
    ///         Off by default, because a scheduler is a process-wide resource with worker threads
    ///         attached and a navigation queue is not the thing that should decide to create one. A
    ///         game that has one already hands it over; a tool that does not is not made to.
    ///     </para>
    ///     <para>
    ///         <b>The mesh must not be changed while an update is in flight.</b> The searches only read
    ///         it, so any number of them are safe together — but <see cref="NavMesh.AddTile" />,
    ///         <see cref="NavMesh.RemoveTile" /> and therefore
    ///         <see cref="Baking.NavTileCache.Update" /> write to it, and this is the one place in the
    ///         assembly where that would be a race rather than a mistake with an obvious symptom.
    ///     </para>
    /// </remarks>
    public JobScheduler? Scheduler { get; set; }

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

        // Rounds, rather than one query at a time. Every free query is filled, every assigned query
        // advances by the same share, and whatever finished is collected — which is a shape that can
        // be run on one thread or on several without the answers depending on which.
        while (LastIterations < iterations) {
            if (!Assign()) {
                return;
            }

            Advance(share);

            if (!Collect()) {
                return;
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

    /// <summary>Gives every free query something to search. False when none of them has anything.</summary>
    bool Assign() {
        var working = 0;

        for (var query = 0; query < queries.Length; query++) {
            if (assignments[query] < 0) {
                TryStart(query);
            }

            if (assignments[query] >= 0) {
                working++;
            }
        }

        return working > 0;
    }

    /// <summary>Advances every assigned query by one share, here or on the workers.</summary>
    void Advance(int share) {
        if (Scheduler is null) {
            for (var query = 0; query < queries.Length; query++) {
                Slice(queries, assignments, statuses, performed, query, share);
            }

            return;
        }

        // One index per query, one query per batch. There are a handful of queries and each slice is
        // microseconds of work, so letting the scheduler group them would put two searches on one
        // thread and leave another idle.
        Scheduler.ParallelFor(new SliceJob(queries, assignments, statuses, performed, share), queries.Length, 1);
    }

    /// <summary>Takes the paths off whichever searches finished. False when none did.</summary>
    bool Collect() {
        var finished = false;

        for (var query = 0; query < queries.Length; query++) {
            var index = assignments[query];

            if (index < 0) {
                continue;
            }

            LastIterations += performed[query];

            if (statuses[query] == NavPathStatus.Partial) {
                continue;
            }

            slots[index].Status = queries[query].FinalizeSlicedFindPath(slots[index].Path, out var count);
            slots[index].Count = count;
            slots[index].State = NavPathRequestState.Ready;
            assignments[query] = -1;
            PendingCount--;
            finished = true;
        }

        return finished;
    }

    /// <summary>One query's share of one round. The only thing a worker thread runs.</summary>
    static void Slice(NavMeshQuery[] queries, int[] assignments, NavPathStatus[] statuses, int[] performed, int query, int share) {
        if (assignments[query] < 0) {
            performed[query] = 0;

            return;
        }

        statuses[query] = queries[query].UpdateSlicedFindPath(share, out performed[query]);
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

    /// <summary>One round's slices, as a job.</summary>
    /// <remarks>
    ///     Every index touches its own query, its own status and its own count, and reads a mesh
    ///     nothing is writing. That is the whole argument for this being safe, and it is the reason the
    ///     searches were separate query objects long before there was a scheduler to run them on.
    /// </remarks>
    readonly struct SliceJob(
        NavMeshQuery[] queries,
        int[] assignments,
        NavPathStatus[] statuses,
        int[] performed,
        int share
    ) : IJobParallelFor {
        public void Execute(int index) => Slice(queries, assignments, statuses, performed, index, share);
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

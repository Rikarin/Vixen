// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Core.Collections;

/// <summary>
///     A min-heap keyed by a caller-supplied integer id, which can therefore find an entry it has
///     already queued and change its priority.
/// </summary>
/// <typeparam name="TPriority">The priority. Smallest comes out first.</typeparam>
/// <remarks>
///     <para>
///         The BCL's <c>PriorityQueue</c> cannot do this. Once an element is in it there is no way to
///         reach it again, so the usual workaround is to enqueue a second copy at the new priority
///         and skip the stale one on the way out — which unbounds the queue and makes
///         <c>Count</c> a lie. That matters for the three places this exists for: a job graph whose
///         successors become ready as dependencies complete, animation events being rescheduled, and
///         a timeline being scrubbed.
///     </para>
///     <para>
///         An id maps to a heap position through a flat array, so ids should be dense and small —
///         they are indices into whatever the caller already has, not arbitrary keys.
///     </para>
/// </remarks>
public sealed class IndexedPriorityQueue<TPriority> where TPriority : IComparable<TPriority> {
    const int Absent = -1;

    (int Id, TPriority Priority)[] heap;
    int[] positions;

    /// <summary>How many entries are queued.</summary>
    public int Count { get; private set; }

    /// <summary>Whether the queue is empty.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>The largest id the queue currently has room for, plus one.</summary>
    public int IdCapacity => positions.Length;

    /// <summary>Creates a queue sized for a given id range and entry count.</summary>
    /// <param name="idCapacity">The largest id expected, plus one.</param>
    /// <param name="capacity">The expected number of queued entries.</param>
    public IndexedPriorityQueue(int idCapacity = 64, int capacity = 16) {
        ArgumentOutOfRangeException.ThrowIfNegative(idCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);

        positions = new int[idCapacity];
        positions.AsSpan().Fill(Absent);
        heap = capacity == 0 ? [] : new (int, TPriority)[capacity];
    }

    /// <summary>Whether an id is queued.</summary>
    /// <param name="id">The id.</param>
    /// <returns><see langword="true" /> if it is in the queue.</returns>
    public bool Contains(int id) => (uint)id < (uint)positions.Length && positions[id] != Absent;

    /// <summary>Queues an id at a priority.</summary>
    /// <param name="id">The id. Must not be negative, and must not already be queued.</param>
    /// <param name="priority">The priority.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id" /> is negative.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="id" /> is already queued.</exception>
    public void Enqueue(int id, TPriority priority) {
        ArgumentOutOfRangeException.ThrowIfNegative(id);

        if (id >= positions.Length) {
            GrowPositions(id + 1);
        }

        if (positions[id] != Absent) {
            throw new InvalidOperationException($"Id {id} is already queued. Use {nameof(SetPriority)} to move it.");
        }

        if (Count == heap.Length) {
            Array.Resize(ref heap, Math.Max(4, heap.Length * 2));
        }

        heap[Count] = (id, priority);
        positions[id] = Count;
        SiftUp(Count);
        Count++;
    }

    /// <summary>Queues an id, or moves it if it is already queued.</summary>
    /// <param name="id">The id.</param>
    /// <param name="priority">The new priority.</param>
    public void SetPriority(int id, TPriority priority) {
        if (!Contains(id)) {
            Enqueue(id, priority);
            return;
        }

        var position = positions[id];
        var comparison = priority.CompareTo(heap[position].Priority);
        heap[position] = (id, priority);

        // Only one direction can be wrong, so only one of the two walks can move anything.
        if (comparison < 0) {
            SiftUp(position);
        } else if (comparison > 0) {
            SiftDown(position);
        }
    }

    /// <summary>
    ///     Lowers an id's priority, ignoring the call if the new value is not an improvement.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="priority">The candidate priority.</param>
    /// <returns><see langword="false" /> if the id was absent or already at least this urgent.</returns>
    /// <remarks>
    ///     The shape a shortest-path relaxation and a job-graph readiness update both want: propose a
    ///     better value, keep whichever is better, and never accidentally make something less urgent
    ///     because two paths reached it in an unlucky order.
    /// </remarks>
    public bool TryDecreasePriority(int id, TPriority priority) {
        if (!Contains(id)) {
            return false;
        }

        var position = positions[id];
        if (priority.CompareTo(heap[position].Priority) >= 0) {
            return false;
        }

        heap[position] = (id, priority);
        SiftUp(position);
        return true;
    }

    /// <summary>Reads the lowest-priority entry without removing it.</summary>
    /// <param name="id">The id at the front.</param>
    /// <param name="priority">Its priority.</param>
    /// <returns><see langword="false" /> if the queue was empty.</returns>
    public bool TryPeek(out int id, [MaybeNullWhen(false)] out TPriority priority) {
        if (Count == 0) {
            id = Absent;
            priority = default;
            return false;
        }

        (id, priority) = heap[0];
        return true;
    }

    /// <summary>Removes and returns the lowest-priority entry.</summary>
    /// <param name="id">The id that came out.</param>
    /// <param name="priority">Its priority.</param>
    /// <returns><see langword="false" /> if the queue was empty.</returns>
    public bool TryDequeue(out int id, [MaybeNullWhen(false)] out TPriority priority) {
        if (!TryPeek(out id, out priority)) {
            return false;
        }

        RemoveAt(0);
        return true;
    }

    /// <summary>Removes an id wherever it sits in the queue.</summary>
    /// <param name="id">The id.</param>
    /// <returns><see langword="false" /> if the id was not queued.</returns>
    public bool Remove(int id) {
        if (!Contains(id)) {
            return false;
        }

        RemoveAt(positions[id]);
        return true;
    }

    /// <summary>Empties the queue, keeping the buffers.</summary>
    public void Clear() {
        for (var i = 0; i < Count; i++) {
            positions[heap[i].Id] = Absent;
        }

        Array.Clear(heap, 0, Count);
        Count = 0;
    }

    void RemoveAt(int position) {
        positions[heap[position].Id] = Absent;
        Count--;

        if (position == Count) {
            heap[Count] = default;
            return;
        }

        // Move the last entry into the hole, then let it find its level. One of the two walks does
        // nothing, and which one depends on how the replacement compares to its new neighbours.
        heap[position] = heap[Count];
        heap[Count] = default;
        positions[heap[position].Id] = position;

        SiftDown(position);
        SiftUp(position);
    }

    void SiftUp(int position) {
        var entry = heap[position];

        while (position > 0) {
            var parent = (position - 1) / 2;
            if (entry.Priority.CompareTo(heap[parent].Priority) >= 0) {
                break;
            }

            heap[position] = heap[parent];
            positions[heap[position].Id] = position;
            position = parent;
        }

        heap[position] = entry;
        positions[entry.Id] = position;
    }

    void SiftDown(int position) {
        var entry = heap[position];

        while (true) {
            var child = (position * 2) + 1;
            if (child >= Count) {
                break;
            }

            if (child + 1 < Count && heap[child + 1].Priority.CompareTo(heap[child].Priority) < 0) {
                child++;
            }

            if (entry.Priority.CompareTo(heap[child].Priority) <= 0) {
                break;
            }

            heap[position] = heap[child];
            positions[heap[position].Id] = position;
            position = child;
        }

        heap[position] = entry;
        positions[entry.Id] = position;
    }

    void GrowPositions(int required) {
        var size = Math.Max(required, Math.Max(4, positions.Length * 2));
        var previous = positions.Length;
        Array.Resize(ref positions, size);
        positions.AsSpan(previous).Fill(Absent);
    }
}

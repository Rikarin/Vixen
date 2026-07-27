// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using System.Runtime.InteropServices;

namespace Vixen.Ui.Reactive;

/// <summary>What happened to a collection.</summary>
public enum CollectionChangeKind {
    /// <summary>One item appeared at <see cref="CollectionChange.Index" />.</summary>
    Inserted,

    /// <summary>One item left <see cref="CollectionChange.Index" />.</summary>
    Removed,

    /// <summary>The item at <see cref="CollectionChange.Index" /> was replaced.</summary>
    Replaced,

    /// <summary>The item moved from <see cref="CollectionChange.Index" /> to <see cref="CollectionChange.Target" />.</summary>
    Moved
}

/// <summary>One entry in a collection's change log.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Index">Where it happened.</param>
/// <param name="Target">The destination of a <see cref="CollectionChangeKind.Moved" />; otherwise -1.</param>
public readonly record struct CollectionChange(CollectionChangeKind Kind, int Index, int Target = -1);

/// <summary>A list whose changes are reported one at a time rather than as "something changed".</summary>
/// <typeparam name="T">The item type.</typeparam>
/// <remarks>
///     <para>
///         This is what <c>@for</c> binds to. A plain <c>Signal&lt;List&lt;T&gt;&gt;</c> can say that
///         a list changed and nothing more, so the only correct response is to reconcile the whole
///         thing — which for the hierarchy panel of a scene with ten thousand entities is the
///         difference between appending one row and rebuilding ten thousand.
///     </para>
///     <para>
///         So mutations are recorded in a bounded log, and a reconciler reads the entries it has not
///         seen. The log being bounded is the interesting decision: a consumer that falls far enough
///         behind is told to resynchronise from scratch rather than the collection retaining an
///         unbounded history on its behalf. Falling behind means not having reconciled for
///         <see cref="ChangeLogCapacity" /> changes, which for a UI drained every frame does not
///         happen, and for one that has been off-screen for a minute is exactly when a full rebuild
///         is the cheaper answer anyway.
///     </para>
///     <para>
///         Reading <see cref="Count" /> or any item makes the reader depend on the collection as a
///         whole. That is the coarse path, and it is the right one for a binding that shows
///         "17 items"; the change log is the fine one.
///     </para>
/// </remarks>
public sealed class CollectionSignal<T> : ReactiveNode, IReadOnlyList<T> {
    readonly CollectionChange[] log;
    readonly List<T> items;
    CollectionChange[] scratch = [];

    /// <summary>Creates an empty collection.</summary>
    /// <param name="capacity">Initial item capacity.</param>
    /// <param name="changeLogCapacity">
    ///     How many changes are remembered for consumers that have not caught up.
    /// </param>
    public CollectionSignal(int capacity = 0, int changeLogCapacity = 64) {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentOutOfRangeException.ThrowIfLessThan(changeLogCapacity, 1);

        items = capacity == 0 ? [] : new List<T>(capacity);
        log = new CollectionChange[changeLogCapacity];
    }

    /// <summary>Creates a collection holding <paramref name="initial" />.</summary>
    /// <param name="initial">The starting items.</param>
    /// <param name="changeLogCapacity">
    ///     How many changes are remembered for consumers that have not caught up.
    /// </param>
    public CollectionSignal(IEnumerable<T> initial, int changeLogCapacity = 64) {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentOutOfRangeException.ThrowIfLessThan(changeLogCapacity, 1);

        items = [.. initial];
        log = new CollectionChange[changeLogCapacity];
    }

    /// <summary>How many changes the log remembers.</summary>
    public int ChangeLogCapacity => log.Length;

    /// <summary>How many changes this collection has ever recorded.</summary>
    /// <remarks>
    ///     A consumer keeps the value it last reconciled to and passes it back to
    ///     <see cref="TryGetChangesSince" />. It is a count and not a timestamp, so it never has to be
    ///     compared against anything but itself.
    /// </remarks>
    public long Revision { get; private set; }

    /// <summary>How many items there are.</summary>
    public int Count {
        get {
            ReactiveGraph.AssertOwningThread();
            ProducerAccessed(this);
            return items.Count;
        }
    }

    /// <summary>The item at <paramref name="index" />.</summary>
    /// <param name="index">A valid index.</param>
    public T this[int index] {
        get {
            ReactiveGraph.AssertOwningThread();
            ProducerAccessed(this);
            return items[index];
        }
        set {
            ReactiveGraph.AssertOwningThread();
            items[index] = value;
            Record(new CollectionChange(CollectionChangeKind.Replaced, index));
        }
    }

    /// <summary>The items, without recording a dependency.</summary>
    /// <returns>A view over the current contents.</returns>
    public ReadOnlySpan<T> Peek() => CollectionsMarshal.AsSpan(items);

    /// <summary>Appends an item.</summary>
    /// <param name="item">The item.</param>
    public void Add(T item) {
        ReactiveGraph.AssertOwningThread();
        items.Add(item);
        Record(new CollectionChange(CollectionChangeKind.Inserted, items.Count - 1));
    }

    /// <summary>Inserts an item.</summary>
    /// <param name="index">Where it goes.</param>
    /// <param name="item">The item.</param>
    public void Insert(int index, T item) {
        ReactiveGraph.AssertOwningThread();
        items.Insert(index, item);
        Record(new CollectionChange(CollectionChangeKind.Inserted, index));
    }

    /// <summary>Removes the item at <paramref name="index" />.</summary>
    /// <param name="index">A valid index.</param>
    public void RemoveAt(int index) {
        ReactiveGraph.AssertOwningThread();
        items.RemoveAt(index);
        Record(new CollectionChange(CollectionChangeKind.Removed, index));
    }

    /// <summary>Removes the first occurrence of <paramref name="item" />.</summary>
    /// <param name="item">The item to remove.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(T item) {
        ReactiveGraph.AssertOwningThread();
        var index = items.IndexOf(item);
        if (index < 0) {
            return false;
        }

        RemoveAt(index);
        return true;
    }

    /// <summary>Moves one item without removing and re-adding it.</summary>
    /// <param name="from">Where the item is.</param>
    /// <param name="to">Where it should be.</param>
    /// <remarks>
    ///     A distinct operation rather than a remove followed by an insert, because for the
    ///     reconciler they are not the same thing: a move keeps the element, its focus and its
    ///     scroll position, and a remove-then-insert destroys and rebuilds it.
    /// </remarks>
    public void Move(int from, int to) {
        ReactiveGraph.AssertOwningThread();
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(from, items.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(to);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(to, items.Count);

        if (from == to) {
            return;
        }

        var item = items[from];
        items.RemoveAt(from);
        items.Insert(to, item);
        Record(new CollectionChange(CollectionChangeKind.Moved, from, to));
    }

    /// <summary>Removes everything.</summary>
    /// <remarks>
    ///     Recorded as one removal per item, from the end, so that a consumer following the log does
    ///     not need a separate reset path for the common case of a small list being emptied.
    /// </remarks>
    public void Clear() {
        ReactiveGraph.AssertOwningThread();
        if (items.Count == 0) {
            return;
        }

        var count = items.Count;
        items.Clear();
        for (var index = count - 1; index >= 0; index--) {
            Append(new CollectionChange(CollectionChangeKind.Removed, index));
        }

        Changed();
    }

    /// <summary>The changes since <paramref name="revision" />, if the log still remembers them.</summary>
    /// <param name="revision">The <see cref="Revision" /> the caller last reconciled to.</param>
    /// <param name="changes">The changes, oldest first. Empty when there are none.</param>
    /// <returns>
    ///     <see langword="false" /> when the caller has fallen further behind than the log goes, in
    ///     which case it has to rebuild from the current contents rather than replay.
    /// </returns>
    public bool TryGetChangesSince(long revision, out ReadOnlySpan<CollectionChange> changes) {
        ReactiveGraph.AssertOwningThread();
        ProducerAccessed(this);

        var pending = Revision - revision;
        if (revision < 0 || pending < 0 || pending > log.Length) {
            changes = default;
            return false;
        }

        if (pending == 0) {
            changes = default;
            return true;
        }

        // The log is a ring. A run that does not wrap is handed back as one span; one that does is
        // compacted into the scratch buffer, which is reused rather than allocated per read.
        var start = (int) ((Revision - pending) % log.Length);
        var count = (int) pending;
        if (start + count <= log.Length) {
            changes = log.AsSpan(start, count);
            return true;
        }

        if (scratch.Length < count) {
            scratch = new CollectionChange[count];
        }

        var firstRun = log.Length - start;
        log.AsSpan(start, firstRun).CopyTo(scratch);
        log.AsSpan(0, count - firstRun).CopyTo(scratch.AsSpan(firstRun));
        changes = scratch.AsSpan(0, count);
        return true;
    }

    /// <summary>Enumerates the items, recording a dependency on the collection.</summary>
    /// <returns>An allocation-free enumerator.</returns>
    public List<T>.Enumerator GetEnumerator() {
        ReactiveGraph.AssertOwningThread();
        ProducerAccessed(this);
        return items.GetEnumerator();
    }

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    void Record(CollectionChange change) {
        Append(change);
        Changed();
    }

    void Append(CollectionChange change) {
        log[(int) (Revision % log.Length)] = change;
        Revision++;
    }

    void Changed() {
        Version++;
        ReactiveGraph.IncrementEpoch();
        NotifyConsumers();
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using Vixen.Net.Messaging;

namespace Vixen.Net.Engine;

/// <summary>What happened to a list.</summary>
public enum SyncListChange : byte {
    /// <summary>Something was appended.</summary>
    Added = 0,

    /// <summary>Something was put in the middle.</summary>
    Inserted = 1,

    /// <summary>Something was taken out.</summary>
    Removed = 2,

    /// <summary>Something was replaced.</summary>
    Replaced = 3,

    /// <summary>All of it went.</summary>
    Cleared = 4
}

/// <summary>A list the server changes and every client that can see the object ends up with.</summary>
/// <typeparam name="T">What is in it. A type the wire knows, as for <see cref="SyncVar{T}" />.</typeparam>
/// <remarks>
///     <para>
///         <b>This one does not go through the delta packer, and stretching it to would be wrong.</b>
///         That machinery rests on a fixed lane layout — the server checks that a component's declared
///         lanes add up to what it wrote, and falls back to whole records when they do not. A list is
///         variable-length, so it would fail that check on every send: correct, and useless. Worse,
///         lane-by-lane differencing is actively wrong for a list, because inserting at the front
///         shifts every element and a one-item insert would difference as "all of it changed".
///     </para>
///     <para>
///         So a list replicates as <b>what happened to it</b> rather than as what it now is: append,
///         insert, remove, replace, clear. One op is a byte, an index and an element, against a whole
///         list every time somebody picked something up.
///     </para>
///     <para>
///         <b>Corrected when it was wired up: the ops do not travel, the list does.</b> This said that
///         ops go on the wire and that the reliable channel's ordering makes per-connection
///         bookkeeping unnecessary — everyone receives every op exactly once. That is true of a
///         broadcast and false of a snapshot, which is why it was never wired up: a snapshot goes to
///         the connections an interest resolver returns, so somebody who was not observing has
///         received nothing at all, and an object crossing into their interest has to be told the
///         list rather than the last op. <see cref="SyncListReplicator{T}" /> sends
///         <see cref="WriteWhole" />, which makes a late joiner, a reconnect, a lost snapshot and an
///         interest change the same case.
///     </para>
///     <para>
///         <b>The op log is still what this type is for locally.</b> It drives
///         <see cref="Changed" /> — which is what a UI binds to, and where "one item was inserted at
///         index three" is exactly the notification a caller wants rather than "here is a list
///         again". What it is not is the wire format.
///     </para>
/// </remarks>
public sealed class SyncList<T> : IReadOnlyList<T>, ISyncList {
    readonly ISyncCodec<T> codec;
    readonly List<T> items = [];
    readonly List<Operation> pending = [];

    /// <summary>What it is called, for diagnostics.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <inheritdoc />
    public void Rename(string name) => Name = name;

    /// <summary>How many are in it.</summary>
    public int Count => items.Count;

    /// <summary>Whether there is anything to send.</summary>
    public bool HasPending => pending.Count > 0;

    /// <summary>Raised on whichever end receives a change, after it has been applied.</summary>
    public event Action<SyncListChange, int, T>? Changed;

    /// <summary>Reads one.</summary>
    /// <param name="index">Which.</param>
    public T this[int index] => items[index];

    /// <summary>Creates one.</summary>
    /// <exception cref="NotSupportedException">The wire does not know this type.</exception>
    public SyncList() => codec = SyncCodecs.For<T>();

    /// <summary>Appends.</summary>
    /// <param name="item">What.</param>
    public void Add(T item) {
        items.Add(item);
        pending.Add(new(SyncListChange.Added, items.Count - 1, item));
    }

    /// <summary>Puts something in the middle.</summary>
    /// <param name="index">Where.</param>
    /// <param name="item">What.</param>
    public void Insert(int index, T item) {
        items.Insert(index, item);
        pending.Add(new(SyncListChange.Inserted, index, item));
    }

    /// <summary>Takes something out.</summary>
    /// <param name="index">Which.</param>
    public void RemoveAt(int index) {
        var item = items[index];
        items.RemoveAt(index);
        pending.Add(new(SyncListChange.Removed, index, item));
    }

    /// <summary>Replaces something.</summary>
    /// <param name="index">Which.</param>
    /// <param name="item">What with.</param>
    public void Replace(int index, T item) {
        items[index] = item;
        pending.Add(new(SyncListChange.Replaced, index, item));
    }

    /// <summary>Empties it.</summary>
    /// <remarks>
    ///     One op rather than a remove each, and the pending ops before it are dropped: whatever they
    ///     were about is gone, and a receiver that applied them and then this would end up in the same
    ///     place having been told twice.
    /// </remarks>
    public void Clear() {
        items.Clear();
        pending.Clear();
        pending.Add(new(SyncListChange.Cleared, 0, default!));
    }

    /// <summary>Writes the ops that have not gone yet.</summary>
    /// <param name="writer">Where the bits go.</param>
    /// <returns>Whether they fit.</returns>
    public bool WritePending(ref BitWriter writer) {
        writer.WriteVariable((uint)pending.Count);

        foreach (var operation in pending) {
            Write(ref writer, in operation);
        }

        return !writer.Overflowed;
    }

    /// <summary>Writes the whole list, for somebody who has never seen it.</summary>
    /// <param name="writer">Where the bits go.</param>
    /// <returns>Whether it fit.</returns>
    /// <remarks>
    ///     As a clear followed by an append each, so a receiver has one code path rather than two and
    ///     a late joiner and a reset are the same thing to it.
    /// </remarks>
    public bool WriteWhole(ref BitWriter writer) {
        writer.WriteVariable((uint)(items.Count + 1));
        Write(ref writer, new(SyncListChange.Cleared, 0, default!));

        for (var i = 0; i < items.Count; i++) {
            Write(ref writer, new(SyncListChange.Added, i, items[i]));
        }

        return !writer.Overflowed;
    }

    /// <summary>Applies ops as they arrived.</summary>
    /// <param name="reader">Where the bits come from.</param>
    /// <returns>Whether they were well-formed and every index was one this list has.</returns>
    public bool Apply(ref BitReader reader) {
        if (!reader.TryReadVariable(out var count)) {
            return false;
        }

        for (var i = 0u; i < count; i++) {
            if (!reader.TryRead(3, out var raw) || !reader.TryReadVariable(out var index)) {
                return false;
            }

            var change = (SyncListChange)raw;
            var item = default(T)!;

            if (change is SyncListChange.Added or SyncListChange.Inserted or SyncListChange.Replaced
                && !codec.Read(ref reader, out item)) {
                return false;
            }

            // An index from the wire is an index somebody else chose, so it is checked rather than
            // trusted: a malformed one is a refused snapshot, not an exception out of a decoder.
            if (!TryApply(change, (int)index, ref item)) {
                return false;
            }

            Changed?.Invoke(change, (int)index, item);
        }

        return true;
    }

    /// <summary>Marks the pending ops as sent.</summary>
    public void ClearPending() => pending.Clear();

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Applies one op, and fills in what a removal removed.</summary>
    /// <remarks>
    ///     A removal does not carry its item on the wire — the receiver already has it — but a
    ///     handler asking "what was taken out" deserves an answer, so it is read back here rather
    ///     than reported as a default nobody can use.
    /// </remarks>
    bool TryApply(SyncListChange change, int index, ref T item) {
        switch (change) {
            case SyncListChange.Cleared:
                items.Clear();

                return true;

            case SyncListChange.Added when index == items.Count:
                items.Add(item);

                return true;

            case SyncListChange.Inserted when index >= 0 && index <= items.Count:
                items.Insert(index, item);

                return true;

            case SyncListChange.Removed when index >= 0 && index < items.Count:
                item = items[index];
                items.RemoveAt(index);

                return true;

            case SyncListChange.Replaced when index >= 0 && index < items.Count:
                items[index] = item;

                return true;

            default:
                return false;
        }
    }

    void Write(ref BitWriter writer, in Operation operation) {
        writer.Write((uint)operation.Change, 3);
        writer.WriteVariable((uint)operation.Index);

        if (operation.Change is SyncListChange.Added or SyncListChange.Inserted or SyncListChange.Replaced) {
            var item = operation.Item;
            codec.Write(ref writer, in item);
        }
    }

    readonly record struct Operation(SyncListChange Change, int Index, T Item);
}

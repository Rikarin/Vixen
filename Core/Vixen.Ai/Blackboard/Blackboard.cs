// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Ai;

/// <summary>
///     One agent's data: a byte range over a compiled <see cref="BlackboardLayout" />, a bit per key
///     saying whether it has been set, a version per key, and the observers watching each one.
/// </summary>
/// <remarks>
///     <para>
///         <b>A read is a span slice and a write is a store.</b> There is no hashing, no boxing and
///         no allocation once the instance exists — a thousand agents on one layout is a thousand
///         small arrays, sized at load and never resized.
///     </para>
///     <para>
///         ⚠ <b>A key is <i>set</i> or <i>unset</i> independently of its value.</b> "Is Set" is the
///         single commonest decorator in every behaviour-tree implementation there is, and
///         <see cref="Entity.Null" />, <c>0</c> and <see cref="Vector3.Zero" /> are all legal values
///         somebody means. So the set-ness is a bit in a mask beside the values, and
///         <see cref="Clear" /> is a write like any other — it bumps the version and notifies.
///     </para>
///     <para>
///         <b>Both a version and an observer list, because they answer different questions.</b>
///         Observers drive aborts: a decorator that must interrupt a running branch cannot poll.
///         Versions drive everything that only wants to recompute when something moved — a service
///         on an interval, a cached path, a scorer — and they let it answer "has this changed since I
///         last looked" without keeping a copy of the value.
///     </para>
///     <para>
///         ⚠ <b>A version bumps only when the value actually changes.</b> Writing the same number is
///         not a change, and treating it as one would make every service that writes its result
///         every tick abort every decorator observing it, for ever. That is the difference between
///         an event-driven tree and a tree that ticks itself to death.
///     </para>
///     <para>
///         <b>One agent owns one blackboard.</b> That is what makes a tree step parallelisable over
///         chunks — a step touches this agent's memory and this agent's board and nothing else. Data
///         two agents share is a <see cref="SharedBlackboard" />, which is a distinct type precisely
///         so that sharing is a decision somebody made rather than something that happened.
///     </para>
/// </remarks>
public sealed class Blackboard {
    readonly byte[] values;
    readonly uint[] versions;
    readonly ulong[] assigned;

    // Per-key intrusive lists into one slot array, rather than a List<T> per key. A decorator
    // registers on branch entry and unregisters on exit, which for a busy tree is several times a
    // second per agent — so registration has to be a pointer swap out of a free list rather than an
    // allocation, or the "zero steady-state allocation" claim only holds for trees nobody enters.
    readonly int[] heads;

    ObserverSlot[] slots = [];
    int firstFree = -1;
    uint nextGeneration = 1;

    // Set only by SharedBlackboard. A per-agent board never gates, so this is one predictable branch
    // on a write path that is otherwise a store.
    internal bool WritesGated;
    internal bool WritesOpen;
    internal int WriterThread = -1;

    /// <summary>Creates an instance of a layout, with every key unset.</summary>
    /// <param name="layout">Its shape.</param>
    /// <exception cref="ArgumentNullException"><paramref name="layout" /> is null.</exception>
    public Blackboard(BlackboardLayout layout) {
        ArgumentNullException.ThrowIfNull(layout);

        Layout = layout;
        values = new byte[layout.Size];
        versions = new uint[layout.Count];
        assigned = new ulong[(layout.Count + 63) / 64];
        heads = new int[layout.Count];
        Array.Fill(heads, -1);
    }

    /// <summary>The shape this is an instance of.</summary>
    public BlackboardLayout Layout { get; }

    /// <summary>How many times anything on this board has changed.</summary>
    /// <remarks>
    ///     The cheap "has anything at all moved" test, for a caller that would otherwise walk every
    ///     key's version to find out that none of them had.
    /// </remarks>
    public uint Version { get; private set; }

    /// <summary>Whether a key has ever been written and not since cleared.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Whether it holds a value somebody meant.</returns>
    public bool IsSet(BlackboardKey key) {
        Check(key);

        return (assigned[key.Index >> 6] & (1UL << (key.Index & 63))) != 0;
    }

    /// <summary>How many times this key has changed.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Its version, which only ever increases.</returns>
    public uint VersionOf(BlackboardKey key) {
        Check(key);

        return versions[key.Index];
    }

    /// <summary>Unsets a key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Whether it had been set.</returns>
    /// <remarks>
    ///     The bytes are zeroed as well as the bit, so that a key cleared and read anyway gives a
    ///     predictable answer rather than whatever was there before — a stale entity id read through
    ///     a missing <c>Is Set</c> check is the kind of bug that only shows up in the build somebody
    ///     is demonstrating.
    /// </remarks>
    public bool Clear(BlackboardKey key) {
        Check(key);
        Gate();

        if (!IsSet(key)) {
            return false;
        }

        var definition = Layout[key];

        values.AsSpan(definition.Offset, definition.Size).Clear();
        assigned[key.Index >> 6] &= ~(1UL << (key.Index & 63));
        Changed(key);

        return true;
    }

    /// <summary>Unsets every key.</summary>
    /// <remarks>
    ///     Notifies for each key that was set, because a reset is a change like any other — an agent
    ///     recycled out of a pool must not keep a decorator that believes it still has a target.
    /// </remarks>
    public void Reset() {
        for (var index = 0; index < Layout.Count; index++) {
            Clear(new((ushort)index));
        }
    }

    /// <summary>Reads a boolean.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Its value, or <see langword="false" /> if it is unset.</returns>
    public bool GetBool(BlackboardKey key) => Read<byte>(key, BlackboardValueType.Bool) != 0;

    /// <summary>Reads an integer.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Its value, or zero if it is unset.</returns>
    public int GetInt(BlackboardKey key) => Read<int>(key, BlackboardValueType.Int);

    /// <summary>Reads a float.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Its value, or zero if it is unset.</returns>
    public float GetFloat(BlackboardKey key) => Read<float>(key, BlackboardValueType.Float);

    /// <summary>Reads a vector.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Its value, or the zero vector if it is unset.</returns>
    public Vector3 GetVector3(BlackboardKey key) => Read<Vector3>(key, BlackboardValueType.Vector3);

    /// <summary>Reads an entity.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Its value, or <see cref="Entity.Null" /> if it is unset.</returns>
    public Entity GetEntity(BlackboardKey key) => Read<Entity>(key, BlackboardValueType.Entity);

    /// <summary>Reads a symbol.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Its value, or <see cref="Symbol.None" /> if it is unset.</returns>
    public Symbol GetSymbol(BlackboardKey key) => Read<Symbol>(key, BlackboardValueType.Symbol);

    /// <summary>Writes a boolean.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">What to write.</param>
    /// <returns>Whether anything changed.</returns>
    public bool SetBool(BlackboardKey key, bool value) =>
        Write<byte>(key, BlackboardValueType.Bool, value ? (byte)1 : (byte)0);

    /// <summary>Writes an integer.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">What to write.</param>
    /// <returns>Whether anything changed.</returns>
    public bool SetInt(BlackboardKey key, int value) => Write(key, BlackboardValueType.Int, value);

    /// <summary>Writes a float.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">What to write.</param>
    /// <returns>Whether anything changed.</returns>
    public bool SetFloat(BlackboardKey key, float value) => Write(key, BlackboardValueType.Float, value);

    /// <summary>Writes a vector.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">What to write.</param>
    /// <returns>Whether anything changed.</returns>
    public bool SetVector3(BlackboardKey key, Vector3 value) => Write(key, BlackboardValueType.Vector3, value);

    /// <summary>Writes an entity.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">What to write.</param>
    /// <returns>Whether anything changed.</returns>
    public bool SetEntity(BlackboardKey key, Entity value) => Write(key, BlackboardValueType.Entity, value);

    /// <summary>Writes a symbol.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">What to write.</param>
    /// <returns>Whether anything changed.</returns>
    public bool SetSymbol(BlackboardKey key, Symbol value) => Write(key, BlackboardValueType.Symbol, value);

    /// <summary>Starts telling something when a key changes.</summary>
    /// <param name="key">The key to watch.</param>
    /// <param name="observer">What to tell.</param>
    /// <returns>The registration, for <see cref="RemoveObserver" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="observer" /> is null.</exception>
    public BlackboardObserverHandle AddObserver(BlackboardKey key, IBlackboardObserver observer) {
        ArgumentNullException.ThrowIfNull(observer);
        Check(key);

        if (firstFree < 0) {
            Grow();
        }

        var index = firstFree;

        firstFree = slots[index].NextFree;
        slots[index].Observer = observer;
        slots[index].Generation = nextGeneration++;
        slots[index].Key = key.Index;
        slots[index].Next = heads[key.Index];
        slots[index].NextFree = -1;
        heads[key.Index] = index;

        return new(index, slots[index].Generation);
    }

    /// <summary>Stops telling it.</summary>
    /// <param name="handle">The registration.</param>
    /// <returns>Whether it was a live registration.</returns>
    public bool RemoveObserver(BlackboardObserverHandle handle) {
        if (handle.IsNull
            || (uint)handle.Index >= (uint)slots.Length
            || slots[handle.Index].Generation != handle.Generation
            || slots[handle.Index].Observer is null) {
            return false;
        }

        var key = slots[handle.Index].Key;
        var previous = -1;

        for (var current = heads[key]; current >= 0; current = slots[current].Next) {
            if (current == handle.Index) {
                if (previous < 0) {
                    heads[key] = slots[current].Next;
                } else {
                    slots[previous].Next = slots[current].Next;
                }

                break;
            }

            previous = current;
        }

        slots[handle.Index].Observer = null;
        slots[handle.Index].Next = -1;
        slots[handle.Index].NextFree = firstFree;
        firstFree = handle.Index;

        return true;
    }

    /// <summary>How many observers are registered on a key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>How many.</returns>
    /// <remarks>For the debugger and for tests. Nothing in a frame asks.</remarks>
    public int ObserverCount(BlackboardKey key) {
        Check(key);

        var count = 0;

        for (var current = heads[key.Index]; current >= 0; current = slots[current].Next) {
            count++;
        }

        return count;
    }

    /// <summary>The raw bytes, for a compiler, a serialiser or a test.</summary>
    /// <returns>The whole value block.</returns>
    /// <remarks>
    ///     ⚠ Bypasses versions and observers entirely. Writing through this is how a snapshot is
    ///     restored, and it is not how an agent writes a key.
    /// </remarks>
    public Span<byte> AsBytes() => values;

    void Grow() {
        var previous = slots.Length;
        var grown = new ObserverSlot[Math.Max(8, previous * 2)];

        slots.AsSpan().CopyTo(grown);
        slots = grown;

        // Threaded back to front so that the free list hands out ascending indices, which makes a
        // dump of a board's observers readable rather than a permutation nobody can diff.
        for (var index = grown.Length - 1; index >= previous; index--) {
            slots[index].NextFree = firstFree;
            slots[index].Next = -1;
            firstFree = index;
        }
    }

    T Read<T>(BlackboardKey key, BlackboardValueType type) where T : struct {
        Check(key, type);

        var definition = Layout[key];

        return MemoryMarshal.Read<T>(values.AsSpan(definition.Offset, definition.Size));
    }

    bool Write<T>(BlackboardKey key, BlackboardValueType type, T value) where T : struct {
        Check(key, type);
        Gate();

        var definition = Layout[key];
        var target = values.AsSpan(definition.Offset, definition.Size);
        var was = IsSet(key);

        if (was && MemoryMarshal.Read<T>(target).Equals(value)) {
            return false;
        }

        MemoryMarshal.Write(target, in value);
        assigned[key.Index >> 6] |= 1UL << (key.Index & 63);
        Changed(key);

        return true;
    }

    void Changed(BlackboardKey key) {
        versions[key.Index]++;
        Version++;

        // The next link is taken before the observer runs, so that an observer which unregisters
        // itself — the ordinary case for a decorator whose branch just ended — does not saw off the
        // list this loop is standing on.
        for (var current = heads[key.Index]; current >= 0;) {
            var next = slots[current].Next;

            slots[current].Observer?.OnBlackboardChanged(this, key);
            current = next;
        }
    }

    void Gate() {
        if (WritesGated && (!WritesOpen || WriterThread != Environment.CurrentManagedThreadId)) {
            throw new InvalidOperationException(
                "A shared blackboard may only be written inside a write scope, on the thread that opened it. "
                + "See docs/plan/37 § D16."
            );
        }
    }

    void Check(BlackboardKey key) {
        if (!key.IsValid || key.Index >= Layout.Count) {
            throw new ArgumentOutOfRangeException(nameof(key), key, "Not a key of this blackboard's layout.");
        }
    }

    void Check(BlackboardKey key, BlackboardValueType type) {
        Check(key);

        // Checked rather than trusted, because the failure it prevents is silent: reading a Vector3
        // key as an int gives four bytes of a float, which is a number, and a scorer that consumed
        // it would produce a plausible ranking off the wrong data.
        if (Layout[key].Type != type) {
            throw new InvalidOperationException(
                $"'{Layout[key].Name}' is a {Layout[key].Type} key, not a {type} one."
            );
        }
    }

    /// <summary>One registration: who to tell, on which key, and where the next one is.</summary>
    struct ObserverSlot {
        public IBlackboardObserver? Observer;
        public uint Generation;
        public ushort Key;
        public int Next;
        public int NextFree;
    }
}

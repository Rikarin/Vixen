// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections;
using System.Runtime.InteropServices;

namespace Vixen.Ui.Reactive;

/// <summary>A map that is written into rather than replaced, and notifies either way.</summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
/// <remarks>
///     <para>
///         <see cref="CollectionSignal{T}" />'s sibling for the keyed shape, and it exists for one
///         measured reason. Without it a live map is a <c>Signal&lt;ImmutableDictionary&lt;K,
///         V&gt;&gt;</c> — correct, because replacing the map <i>is</i> the notification, and paid for
///         with a rebalanced spine of tree nodes on every single write. <c>RemoteInspectorClient</c>'s
///         counters are the case that asked for this: a build reports its frame rate every frame, and
///         saying that one <see langword="double" /> moved allocated a handful of objects. An in-place
///         write allocates nothing, and this type is that write with the notification still attached.
///     </para>
///     <para>
///         ⚠ <b>One reactive node for the whole map, not one per key — which is
///         <see cref="CollectionSignal{T}" />'s choice, taken here for the same reasons and one
///         more.</b> Every read subscribes to the map as a whole, so a binding that reads
///         <c>map["fps"]</c> is woken when <c>map["draws"]</c> is written. That over-approximates the
///         dependency and never under-approximates it: the cost of the coarse edge is a re-run, never
///         a stale answer, and a binding that re-runs and computes the same string writes nothing
///         further because equality stops the propagation one level up. Both dependency shapes —
///         reading one key, and enumerating the lot — are therefore supported and neither is silently
///         wrong.
///     </para>
///     <para>
///         The per-key alternative is worse than it sounds. A binding that reads a key which is
///         <i>not there yet</i> must still be woken when it appears, so a per-key node would have to
///         be created on read as well as on write, and kept after removal — which turns a map of
///         twelve counters into an unbounded set of nodes keyed by whatever strings the callers
///         happened to ask about. And the fine-grained path a UI actually wants from a map is not
///         "this key moved" in the first place: <c>@for</c> cannot bind to a dictionary at all,
///         because a dictionary has no order, so a pane of live numbers binds to a <i>sorted
///         projection</i> of it and the reconciler's keys come from there.
///     </para>
///     <para>
///         ⚠ <b>Which is also why there is no change log here, and that is the one half of
///         <see cref="CollectionSignal{T}" /> deliberately not carried across.</b> A list's log earns
///         its per-write cost because a keyed reconciler reads it and turns "inserted at 3" into one
///         appended row instead of ten thousand rebuilt ones. Nothing reads a map's, for the reason
///         in the paragraph above — the projection is what gets reconciled — so a log here would be
///         a ring buffer written on every counter update and read by nobody, which is the cost this
///         type was built to remove, reintroduced under a different name.
///     </para>
///     <para>
///         ⚠ <b>Equality still stops propagation, and it is checked per key.</b> Writing a value the
///         comparer already agrees with does nothing at all — no version bump, no notification, no
///         effect run — which is <see cref="Signal{T}" />'s central property and matters more here
///         than anywhere: the counters map is written from a poll that runs every frame, and a build
///         reporting an unchanged number has to cost the panel nothing. ⚠ For a mutable reference
///         value the default comparer is the wrong answer for exactly the reason
///         <see cref="Signal{T}" /> gives, and the answer is the same: hold an immutable value, or
///         pass <see cref="SignalComparer.Never{T}" /> and accept that every write propagates.
///     </para>
///     <para>
///         Reads and writes assert <see cref="ReactiveGraph.OwningThread" />, and a write only ever
///         queues: it marks dependents dirty and evaluates nothing, so the effects it wakes run when
///         <see cref="EffectScheduler.Flush" /> says so and not on the line that wrote the key. Both
///         are ADR-007's contract and neither is different here.
///     </para>
///     <para>
///         ⚠ <b>The name is the wrong way round from <see cref="CollectionSignal{T}" />'s, and that
///         is the analyzer's call rather than a slip.</b> <c>DictionarySignal</c> is what symmetry
///         asks for and CA1710 refuses it: a type implementing
///         <see cref="IReadOnlyDictionary{TKey,TValue}" /> has to end in <c>Dictionary</c> or
///         <c>Collection</c>, and <see cref="CollectionSignal{T}" /> escapes the same rule only
///         because <see cref="IReadOnlyList{T}" /> is not on its list. Suppressing it was the other
///         option and is not worth it — the suffix is what tells a caller that <c>foreach</c> over
///         this yields <see cref="KeyValuePair{TKey,TValue}" /> — so the words swap and the sibling
///         is found by its namespace instead.
///     </para>
/// </remarks>
public sealed class SignalDictionary<TKey, TValue> : ReactiveNode, IReadOnlyDictionary<TKey, TValue>
    where TKey : notnull {
    readonly Dictionary<TKey, TValue> entries;
    readonly IEqualityComparer<TValue> valueComparer;

    /// <summary>Creates an empty map.</summary>
    /// <param name="keyComparer">How keys are compared. Defaults to the key type's own equality.</param>
    /// <param name="valueComparer">
    ///     How to decide a write changed nothing. Defaults to the value type's own equality.
    /// </param>
    public SignalDictionary(
        IEqualityComparer<TKey>? keyComparer = null,
        IEqualityComparer<TValue>? valueComparer = null
    ) {
        entries = new(keyComparer);
        this.valueComparer = valueComparer ?? EqualityComparer<TValue>.Default;
    }

    /// <summary>Creates a map holding <paramref name="initial" />.</summary>
    /// <param name="initial">The starting entries.</param>
    /// <param name="keyComparer">How keys are compared. Defaults to the key type's own equality.</param>
    /// <param name="valueComparer">
    ///     How to decide a write changed nothing. Defaults to the value type's own equality.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="initial" /> is null.</exception>
    public SignalDictionary(
        IEnumerable<KeyValuePair<TKey, TValue>> initial,
        IEqualityComparer<TKey>? keyComparer = null,
        IEqualityComparer<TValue>? valueComparer = null
    ) {
        ArgumentNullException.ThrowIfNull(initial);

        entries = new(initial, keyComparer);
        this.valueComparer = valueComparer ?? EqualityComparer<TValue>.Default;
    }

    /// <summary>How many entries there are.</summary>
    public int Count {
        get {
            ReactiveGraph.AssertOwningThread();
            ProducerAccessed(this);
            return entries.Count;
        }
    }

    /// <summary>The keys, in the map's own arbitrary order.</summary>
    /// <remarks>
    ///     ⚠ <b>A live view, not a copy.</b> Reading the property records the dependency; enumerating
    ///     what it returned some frames later reads whatever the map holds then, and throws if the
    ///     map was written in between. Project it — sorted, because a dictionary's order is its
    ///     hashing — rather than storing it.
    /// </remarks>
    public Dictionary<TKey, TValue>.KeyCollection Keys {
        get {
            ReactiveGraph.AssertOwningThread();
            ProducerAccessed(this);
            return entries.Keys;
        }
    }

    /// <summary>The values, in the same arbitrary order as <see cref="Keys" />.</summary>
    /// <inheritdoc cref="Keys" select="remarks" />
    public Dictionary<TKey, TValue>.ValueCollection Values {
        get {
            ReactiveGraph.AssertOwningThread();
            ProducerAccessed(this);
            return entries.Values;
        }
    }

    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;

    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

    /// <summary>The value stored under <paramref name="key" />.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The value.</returns>
    /// <remarks>
    ///     Reading records a dependency on the map; writing an unequal value — or any value under a
    ///     key that is not there yet — invalidates every dependent. The setter adds as well as
    ///     replaces, so there is no separate <c>Add</c>: a map that threw on a duplicate key would be
    ///     the wrong shape for the thing this is for, which is a value arriving again and again.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">There is nothing under <paramref name="key" />.</exception>
    public TValue this[TKey key] {
        get {
            ReactiveGraph.AssertOwningThread();
            ProducerAccessed(this);
            return entries[key];
        }
        set {
            ReactiveGraph.AssertOwningThread();

            // One hash lookup for "is it there" and "put it there" together, and no allocation for
            // either — which is the whole of what this type buys over replacing an immutable map.
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(entries, key, out var existed);
            if (existed && valueComparer.Equals(slot!, value)) {
                return;
            }

            slot = value;
            Changed();
        }
    }

    /// <summary>Whether there is an entry under <paramref name="key" />.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Whether it is there.</returns>
    /// <remarks>
    ///     ⚠ Records a dependency, and it has to: a binding that asks whether a counter has arrived
    ///     yet is asking a question whose answer changes, and the write that changes it is the one
    ///     that adds the key.
    /// </remarks>
    public bool ContainsKey(TKey key) {
        ReactiveGraph.AssertOwningThread();
        ProducerAccessed(this);
        return entries.ContainsKey(key);
    }

    /// <summary>Reads an entry if it is there.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value, or the default when there is none.</param>
    /// <returns>Whether it was there.</returns>
    /// <inheritdoc cref="ContainsKey" select="remarks" />
    public bool TryGetValue(TKey key, out TValue value) {
        ReactiveGraph.AssertOwningThread();
        ProducerAccessed(this);
        return entries.TryGetValue(key, out value!);
    }

    /// <summary>The entries, without recording a dependency.</summary>
    /// <returns>A live view over the current contents.</returns>
    /// <remarks>
    ///     The map itself rather than a copy, so this costs nothing — but enumerating it through the
    ///     interface boxes an enumerator, which <see cref="TryPeek" /> and
    ///     <see cref="GetEnumerator" /> both avoid.
    /// </remarks>
    public IReadOnlyDictionary<TKey, TValue> Peek() => entries;

    /// <summary>Reads one entry without recording a dependency.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value, or the default when there is none.</param>
    /// <returns>Whether it was there.</returns>
    /// <remarks>
    ///     What an effect calls when it wants the current reading of a counter but does not want to
    ///     be woken by it — the same bargain <see cref="Signal{T}.Peek" /> offers, one key at a time.
    /// </remarks>
    public bool TryPeek(TKey key, out TValue value) => entries.TryGetValue(key, out value!);

    /// <summary>Removes an entry.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Whether it was there.</returns>
    /// <remarks>Removing a key that is not there notifies nobody; nothing changed.</remarks>
    public bool Remove(TKey key) {
        ReactiveGraph.AssertOwningThread();

        if (!entries.Remove(key)) {
            return false;
        }

        Changed();
        return true;
    }

    /// <summary>Removes everything.</summary>
    /// <remarks>Clearing an empty map notifies nobody, for the same reason as <see cref="Remove" />.</remarks>
    public void Clear() {
        ReactiveGraph.AssertOwningThread();

        if (entries.Count == 0) {
            return;
        }

        entries.Clear();
        Changed();
    }

    /// <summary>Enumerates the entries, recording a dependency on the map.</summary>
    /// <returns>An allocation-free enumerator.</returns>
    public Dictionary<TKey, TValue>.Enumerator GetEnumerator() {
        ReactiveGraph.AssertOwningThread();
        ProducerAccessed(this);
        return entries.GetEnumerator();
    }

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() =>
        GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    void Changed() {
        Version++;
        ReactiveGraph.IncrementEpoch();
        NotifyConsumers();
    }
}

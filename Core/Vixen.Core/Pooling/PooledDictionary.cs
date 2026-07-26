// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Core.Pooling;

/// <summary>
///     A scratch dictionary rented from a pool and cleared back into it on disposal, for the
///     grouping and de-duplication passes a frame does and then throws away.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
/// <remarks>
///     <para>
///         What is pooled is the <see cref="Dictionary{TKey,TValue}" /> instance, buckets and all —
///         not a hand-written map over rented arrays. A recycled dictionary that has already grown
///         to the size the workload needs allocates nothing on reuse, and the alternative means
///         owning a second hash table implementation forever to save one object header.
///         <c>Vixen.Core.Collections</c> has purpose-built maps for the cases where the BCL's layout
///         is genuinely the problem; this is not one of them.
///     </para>
///     <para>
///         Like <see cref="PooledList{T}" /> this is a mutable struct: copying it produces a second
///         handle to the same dictionary, and disposing either returns it.
///     </para>
/// </remarks>
public struct PooledDictionary<TKey, TValue> : IDisposable where TKey : notnull {
    static readonly ObjectPool<Dictionary<TKey, TValue>> Pool =
        new(static () => new(), static dictionary => dictionary.Clear(), 32);

    // Read-only stand-in so that a `default` instance answers queries instead of throwing. Never
    // mutated: every write goes through Rented, which rents a real one first.
    static readonly Dictionary<TKey, TValue> EmptyMap = new();

    Dictionary<TKey, TValue>? map;

    readonly Dictionary<TKey, TValue> Map => map ?? EmptyMap;

    Dictionary<TKey, TValue> Rented => map ??= Pool.Rent();

    /// <summary>How many entries the dictionary holds.</summary>
    public readonly int Count => map?.Count ?? 0;

    /// <summary>The keys, in the dictionary's own order.</summary>
    public readonly Dictionary<TKey, TValue>.KeyCollection Keys => Map.Keys;

    /// <summary>The values, in the dictionary's own order.</summary>
    public readonly Dictionary<TKey, TValue>.ValueCollection Values => Map.Values;

    /// <summary>Rents a dictionary from the pool.</summary>
    public PooledDictionary() => map = Pool.Rent();

    /// <summary>Rents a dictionary and grows it to hold <paramref name="capacity" /> entries.</summary>
    /// <param name="capacity">How many entries to make room for up front.</param>
    public PooledDictionary(int capacity) {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        map = Pool.Rent();
        map.EnsureCapacity(capacity);
    }

    /// <summary>Gets or sets the value stored under <paramref name="key" />.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The stored value.</returns>
    /// <exception cref="KeyNotFoundException">Nothing is stored under <paramref name="key" />.</exception>
    public TValue this[TKey key] {
        readonly get => Map[key];
        set => Rented[key] = value;
    }

    /// <summary>Adds an entry.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <exception cref="ArgumentException"><paramref name="key" /> is already present.</exception>
    public void Add(TKey key, TValue value) => Rented.Add(key, value);

    /// <summary>Adds an entry unless the key is already present.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true" /> if the entry was added.</returns>
    public bool TryAdd(TKey key, TValue value) => Rented.TryAdd(key, value);

    /// <summary>Looks up <paramref name="key" />.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The stored value, or the default.</param>
    /// <returns><see langword="true" /> if the key was present.</returns>
    public readonly bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) =>
        Map.TryGetValue(key, out value);

    /// <summary>Whether <paramref name="key" /> is present.</summary>
    /// <param name="key">The key.</param>
    /// <returns><see langword="true" /> if the key was present.</returns>
    public readonly bool ContainsKey(TKey key) => Map.ContainsKey(key);

    /// <summary>Removes the entry under <paramref name="key" />.</summary>
    /// <param name="key">The key.</param>
    /// <returns><see langword="true" /> if an entry was removed.</returns>
    public readonly bool Remove(TKey key) => Map.Remove(key);

    /// <summary>Empties the dictionary, keeping the rental.</summary>
    public readonly void Clear() => Map.Clear();

    /// <summary>
    ///     The underlying dictionary, for handing to code that takes an
    ///     <see cref="IDictionary{TKey,TValue}" />. The reference must not outlive the rental: after
    ///     <see cref="Dispose" /> it belongs to the pool and someone else will be writing to it.
    /// </summary>
    /// <returns>The rented dictionary.</returns>
    public Dictionary<TKey, TValue> AsDictionary() => Rented;

    /// <summary>Clears the dictionary and returns it to the pool.</summary>
    public void Dispose() {
        var rented = map;
        map = null;

        if (rented is not null) {
            Pool.Return(rented);
        }
    }

    /// <summary>Enumerates the entries.</summary>
    /// <returns>An enumerator over the entries.</returns>
    public readonly Dictionary<TKey, TValue>.Enumerator GetEnumerator() => Map.GetEnumerator();
}

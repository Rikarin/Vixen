// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Pooling;

/// <summary>
///     A bounded, lock-free pool of reusable reference-type instances — string builders, command
///     payload objects, per-frame scratch objects — for the places where an allocation per frame
///     would be an allocation per frame too many.
/// </summary>
/// <typeparam name="T">The pooled type.</typeparam>
/// <remarks>
///     <para>
///         <b>The pool is a cache, not a ledger.</b> Nothing tracks what has been rented, and
///         <see cref="Return" /> is advisory: failing to return an instance costs a future
///         allocation and nothing else, and the pool never grows past its capacity. Returning an
///         instance twice, or using one after returning it, is a bug the pool cannot detect —
///         prefer <see cref="RentScoped" />, which cannot get it wrong.
///     </para>
///     <para>
///         <b>On the shape.</b> The design here is a single lock-free fast slot in front of a fixed
///         array of slots, which is what Roslyn's object pool does. The plan (doc 03) described a
///         thread-local free list with shared overflow; that needs either a <see cref="ThreadLocal{T}" />
///         per pool — allocating per thread per pool, and the engine has many pools — or a
///         <c>[ThreadStatic]</c> field, which is per type and so cannot serve two pools of the same
///         type. This achieves the same end, an uncontended fast path with bounded retention, and
///         costs one field.
///     </para>
/// </remarks>
public sealed class ObjectPool<T> where T : class {
    /// <summary>Slots are wrapped so the array is of a struct: one indirection fewer per probe.</summary>
    struct Slot {
        internal T? Value;
    }

    readonly Func<T> factory;
    readonly Action<T>? reset;
    readonly Slot[] slots;

    // Kept out of the array so the common rent/return pair is one field access and one CAS.
    T? first;

    /// <summary>How many instances the pool retains at most.</summary>
    public int Capacity => slots.Length + 1;

    /// <summary>Creates a pool that builds instances with <paramref name="factory" />.</summary>
    /// <param name="factory">Builds a new instance when the pool is empty.</param>
    /// <param name="reset">
    ///     Returns an instance to a reusable state. Runs on <see cref="Return" />, not on
    ///     <see cref="Rent" />, so an instance sitting in the pool is not holding on to whatever it
    ///     last referenced.
    /// </param>
    /// <param name="capacity">How many instances to retain. At least 1.</param>
    public ObjectPool(Func<T> factory, Action<T>? reset = null, int capacity = 32) {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        this.factory = factory;
        this.reset = reset;
        slots = new Slot[capacity - 1];
    }

    /// <summary>Takes an instance from the pool, or builds one.</summary>
    /// <returns>An instance that is not in the pool and is nobody else's.</returns>
    public T Rent() {
        // Read once. If the CAS loses, another thread took this instance and we look further
        // rather than retry — under contention, finding a different slot beats fighting for one.
        var instance = first;
        if (instance is not null && instance == Interlocked.CompareExchange(ref first, null, instance)) {
            return instance;
        }

        return RentSlow();
    }

    T RentSlow() {
        var local = slots;
        for (var i = 0; i < local.Length; i++) {
            var instance = local[i].Value;
            if (instance is not null && instance == Interlocked.CompareExchange(ref local[i].Value, null, instance)) {
                return instance;
            }
        }

        return factory();
    }

    /// <summary>
    ///     Offers an instance back to the pool. Resets it first; drops it on the floor for the GC if
    ///     the pool is full.
    /// </summary>
    /// <param name="instance">The instance to return. Must not be used afterwards.</param>
    public void Return(T instance) {
        ArgumentNullException.ThrowIfNull(instance);
        reset?.Invoke(instance);

        if (first is null) {
            // Deliberately not a CAS. Two threads racing here means one instance is dropped and
            // later re-allocated, which is the cheapest possible outcome of a rare race.
            first = instance;
            return;
        }

        ReturnSlow(instance);
    }

    void ReturnSlow(T instance) {
        var local = slots;
        for (var i = 0; i < local.Length; i++) {
            if (local[i].Value is null) {
                local[i].Value = instance;
                return;
            }
        }
    }

    /// <summary>
    ///     Rents an instance tied to a <c>using</c> scope, so it is returned however the scope ends.
    /// </summary>
    /// <returns>A scope whose <see cref="PooledObject{T}.Value" /> is the rented instance.</returns>
    public PooledObject<T> RentScoped() => new(this, Rent());

    /// <summary>Empties the pool, so the instances it holds can be collected.</summary>
    public void Clear() {
        first = null;
        var local = slots;
        for (var i = 0; i < local.Length; i++) {
            local[i].Value = null;
        }
    }
}

/// <summary>
///     A rented instance that returns itself to its pool when disposed. Obtained from
///     <see cref="ObjectPool{T}.RentScoped" />.
/// </summary>
/// <typeparam name="T">The pooled type.</typeparam>
public readonly struct PooledObject<T> : IDisposable, IEquatable<PooledObject<T>> where T : class {
    readonly ObjectPool<T>? pool;

    /// <summary>The rented instance.</summary>
    public T Value { get; }

    internal PooledObject(ObjectPool<T> pool, T value) {
        this.pool = pool;
        Value = value;
    }

    /// <summary>Returns the instance to its pool.</summary>
    public void Dispose() => pool?.Return(Value);

    /// <summary>Whether two scopes hold the same instance from the same pool.</summary>
    /// <param name="other">The scope to compare with.</param>
    /// <returns><see langword="true" /> if they are the same rental.</returns>
    public bool Equals(PooledObject<T> other) => ReferenceEquals(pool, other.pool) && ReferenceEquals(Value, other.Value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PooledObject<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(pool, Value);

    /// <summary>Whether two scopes hold the same instance from the same pool.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true" /> if they are the same rental.</returns>
    public static bool operator ==(PooledObject<T> left, PooledObject<T> right) => left.Equals(right);

    /// <summary>Whether two scopes hold different instances.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true" /> if they are not the same rental.</returns>
    public static bool operator !=(PooledObject<T> left, PooledObject<T> right) => !left.Equals(right);
}

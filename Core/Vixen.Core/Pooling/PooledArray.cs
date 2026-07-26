// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Runtime.CompilerServices;

namespace Vixen.Core.Pooling;

/// <summary>
///     Rents scratch arrays from <see cref="ArrayPool{T}" /> with the engine's clearing policy
///     applied, so callers never have to decide it.
/// </summary>
/// <remarks>
///     <b>The policy.</b> An array of a type that contains references is cleared when returned; an
///     array of unmanaged elements is not. The reason is not tidiness — a returned array keeps its
///     contents alive, so an uncleared <c>Entity[]</c> in a pool roots every entity it last held
///     until the array is rented again and overwritten. Unmanaged elements root nothing, so
///     clearing them is pure cost, and it is the case that runs every frame.
/// </remarks>
public static class PooledArray {
    /// <summary>Rents an array of at least <paramref name="minimumLength" /> elements.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="minimumLength">How many elements the caller needs.</param>
    /// <returns>A rental whose <see cref="PooledArray{T}.Span" /> is exactly that long.</returns>
    public static PooledArray<T> Rent<T>(int minimumLength) {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
        return new(ArrayPool<T>.Shared.Rent(minimumLength), minimumLength);
    }

    /// <summary>Rents an array of at least <paramref name="minimumLength" /> elements, zeroed.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="minimumLength">How many elements the caller needs.</param>
    /// <returns>A rental whose span is zeroed and exactly <paramref name="minimumLength" /> long.</returns>
    public static PooledArray<T> RentCleared<T>(int minimumLength) {
        var rental = Rent<T>(minimumLength);
        rental.Span.Clear();
        return rental;
    }

    /// <summary>Whether returning a <typeparamref name="T" /> array clears it first.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns><see langword="true" /> for element types that contain references.</returns>
    public static bool ClearsOnReturn<T>() => RuntimeHelpers.IsReferenceOrContainsReferences<T>();
}

/// <summary>
///     A rented array, sized to what the caller asked for and returned to the pool on
///     <see cref="Dispose" />. Obtained from <see cref="PooledArray.Rent{T}" />.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
/// <remarks>
///     Immutable by design: nothing about the rental changes while it is held, only the contents of
///     the span it hands out. That is what makes <c>using var buffer = PooledArray.Rent&lt;int&gt;(n);</c>
///     behave, since a <c>using</c> declaration is read-only.
/// </remarks>
public readonly struct PooledArray<T> : IDisposable, IEquatable<PooledArray<T>> {
    readonly T[]? array;
    readonly int length;

    /// <summary>The requested elements. Never longer than asked for, even though the array is.</summary>
    public Span<T> Span => array is null ? default : array.AsSpan(0, length);

    /// <summary>How many elements were requested.</summary>
    public int Length => length;

    /// <summary>
    ///     The underlying array, which is at least <see cref="Length" /> long and usually longer.
    ///     For handing to APIs that take <c>T[]</c>; prefer <see cref="Span" /> everywhere else.
    /// </summary>
    public T[] Array => array ?? [];

    internal PooledArray(T[] array, int length) {
        this.array = array;
        this.length = length;
    }

    /// <summary>A reference to the element at <paramref name="index" />.</summary>
    /// <param name="index">The index, checked against <see cref="Length" />.</param>
    /// <returns>A reference to the element, so callers can mutate large structs in place.</returns>
    public ref T this[int index] {
        get {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, length);
            return ref Array[index];
        }
    }

    /// <summary>Returns the array to the pool, clearing it first if the policy says so.</summary>
    public void Dispose() {
        if (array is not null) {
            ArrayPool<T>.Shared.Return(array, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        }
    }

    /// <summary>Enumerates the requested elements.</summary>
    /// <returns>An enumerator over <see cref="Span" />.</returns>
    public Span<T>.Enumerator GetEnumerator() => Span.GetEnumerator();

    /// <summary>Whether two rentals name the same array and length.</summary>
    /// <param name="other">The rental to compare with.</param>
    /// <returns><see langword="true" /> if they are the same rental.</returns>
    public bool Equals(PooledArray<T> other) => ReferenceEquals(array, other.array) && length == other.length;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PooledArray<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(array), length);

    /// <summary>Whether two rentals name the same array and length.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true" /> if they are the same rental.</returns>
    public static bool operator ==(PooledArray<T> left, PooledArray<T> right) => left.Equals(right);

    /// <summary>Whether two rentals name different arrays or lengths.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true" /> if they are not the same rental.</returns>
    public static bool operator !=(PooledArray<T> left, PooledArray<T> right) => !left.Equals(right);
}

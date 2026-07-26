// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Runtime.CompilerServices;

namespace Vixen.Core.Pooling;

/// <summary>
///     A growable list over a pooled array, for the case a frame does constantly: collect an
///     unknown number of things, use them, throw the buffer away.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
/// <remarks>
///     <para>
///         Meant to be used as <c>using var list = new PooledList&lt;T&gt;(64);</c>. Disposal returns
///         the buffer, and the same clearing policy as <see cref="PooledArray" /> applies.
///     </para>
///     <para>
///         <b>It is a mutable struct, which brings the usual caveats.</b> Copying one — assigning it,
///         passing it by value, capturing it in a lambda — produces a second list over the same
///         buffer whose <see cref="Count" /> then diverges, and disposing either one invalidates
///         both. Pass it by <c>ref</c>, or hand out <see cref="Span" /> instead.
///     </para>
/// </remarks>
public struct PooledList<T> : IDisposable {
    // Nullable so that `default(PooledList<T>)` — which nothing can stop a caller from writing —
    // behaves as an empty list rather than throwing on first use.
    T[]? array;
    int count;

    /// <summary>How many elements the list holds.</summary>
    public readonly int Count => count;

    /// <summary>How many elements it can hold before the buffer is replaced.</summary>
    public readonly int Capacity => array?.Length ?? 0;

    /// <summary>Whether the list is empty.</summary>
    public readonly bool IsEmpty => count == 0;

    /// <summary>The elements, as a span. Invalidated by anything that grows the list.</summary>
    public readonly Span<T> Span => array is null ? default : array.AsSpan(0, count);

    /// <summary>Creates a list with room for at least <paramref name="capacity" /> elements.</summary>
    /// <param name="capacity">The initial capacity.</param>
    public PooledList(int capacity) {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        array = capacity == 0 ? [] : ArrayPool<T>.Shared.Rent(capacity);
        count = 0;
    }

    /// <summary>A reference to the element at <paramref name="index" />.</summary>
    /// <param name="index">The index, checked against <see cref="Count" />.</param>
    /// <returns>A reference, so large struct elements can be mutated in place.</returns>
    public readonly ref T this[int index] {
        get {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
            return ref array![index];
        }
    }

    /// <summary>Appends an element.</summary>
    /// <param name="item">The element to append.</param>
    public void Add(T item) {
        if (array is null || count == array.Length) {
            Grow(count + 1);
        }

        array![count++] = item;
    }

    /// <summary>Appends a run of elements.</summary>
    /// <param name="items">The elements to append.</param>
    public void AddRange(ReadOnlySpan<T> items) {
        if (array is null || count + items.Length > array.Length) {
            Grow(count + items.Length);
        }

        items.CopyTo(array!.AsSpan(count));
        count += items.Length;
    }

    /// <summary>
    ///     Extends the list by <paramref name="length" /> elements and returns them for the caller to
    ///     fill, skipping the copy an <see cref="AddRange" /> from a temporary would cost.
    /// </summary>
    /// <param name="length">How many elements to append.</param>
    /// <returns>The appended elements, whose contents are undefined until written.</returns>
    public Span<T> AppendSpan(int length) {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (array is null || count + length > array.Length) {
            Grow(count + length);
        }

        var appended = array!.AsSpan(count, length);
        count += length;
        return appended;
    }

    /// <summary>Removes the element at <paramref name="index" />, keeping the order of the rest.</summary>
    /// <param name="index">The index to remove.</param>
    public void RemoveAt(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);

        array!.AsSpan(index + 1, count - index - 1).CopyTo(array.AsSpan(index));
        count--;
        ClearSlot(count);
    }

    /// <summary>
    ///     Removes the element at <paramref name="index" /> by moving the last one into its place.
    ///     O(1), and the right choice whenever order does not matter.
    /// </summary>
    /// <param name="index">The index to remove.</param>
    public void RemoveAtSwapBack(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);

        array![index] = array[--count];
        ClearSlot(count);
    }

    /// <summary>Empties the list, keeping the buffer.</summary>
    public void Clear() {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) {
            Span.Clear();
        }

        count = 0;
    }

    /// <summary>Copies the elements into a new array — the one place this type allocates.</summary>
    /// <returns>An array holding the elements.</returns>
    public readonly T[] ToArray() => Span.ToArray();

    /// <summary>Returns the buffer to the pool. The list is empty and reusable afterwards.</summary>
    public void Dispose() {
        var buffer = array;
        array = [];
        count = 0;

        if (buffer is { Length: > 0 }) {
            ArrayPool<T>.Shared.Return(buffer, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        }
    }

    /// <summary>Enumerates the elements.</summary>
    /// <returns>An enumerator over <see cref="Span" />.</returns>
    public readonly Span<T>.Enumerator GetEnumerator() => Span.GetEnumerator();

    void Grow(int required) {
        // Double, so appending n elements one at a time stays amortised O(n) rather than
        // re-renting on every call. ArrayPool rounds up to a power of two anyway.
        var replacement = ArrayPool<T>.Shared.Rent(Math.Max(required, Math.Max(Capacity * 2, 4)));
        Span.CopyTo(replacement);

        var previous = array;
        array = replacement;

        if (previous is { Length: > 0 }) {
            ArrayPool<T>.Shared.Return(previous, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        }
    }

    readonly void ClearSlot(int index) {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) {
            array![index] = default!;
        }
    }
}

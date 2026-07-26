// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vixen.Core.Collections;

/// <summary>
///     A list that keeps its first <c>TBuffer.Capacity</c> elements inside itself and only reaches
///     for the heap beyond that.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
/// <typeparam name="TBuffer">The inline buffer, and with it the inline capacity.</typeparam>
/// <remarks>
///     <para>
///         For the shape of data the engine is full of: descriptor slots, the children of a UI node,
///         the bones affecting a vertex, the render passes a resource is used in. Almost always
///         small, occasionally not, and allocating for the common case is the difference between a
///         frame that allocates nothing and one that allocates thousands of times.
///     </para>
///     <para>
///         <b>A mutable struct, with the usual caveats.</b> Copying one copies the inline elements
///         and shares the spill buffer, so the two diverge in a way that is hard to see. Pass it by
///         <c>ref</c>, or hand out <see cref="Span" />. Dispose it when it might have spilled —
///         forgetting only forfeits the pooled array, it does not corrupt anything.
///     </para>
/// </remarks>
public struct SmallList<T, TBuffer> : IDisposable where TBuffer : struct, IInlineBuffer<T> {
    TBuffer inline;
    T[]? spilled;
    int count;

    /// <summary>How many elements fit before the list reaches for the heap.</summary>
    public static int InlineCapacity => TBuffer.Capacity;

    /// <summary>How many elements the list holds.</summary>
    public readonly int Count => count;

    /// <summary>Whether the list has outgrown its inline buffer.</summary>
    public readonly bool HasSpilled => spilled is not null;

    /// <summary>Whether the list is empty.</summary>
    public readonly bool IsEmpty => count == 0;

    /// <summary>The elements, wherever they currently live.</summary>
    /// <remarks>
    ///     <c>[UnscopedRef]</c>: while the list is inline, this span points into the struct itself,
    ///     so it lives exactly as long as the list does and the compiler enforces that rather than
    ///     trusting the caller. Adding an element can move the elements to the heap and invalidate it.
    /// </remarks>
    [UnscopedRef]
    public Span<T> Span => Storage[..count];

    // The whole capacity, not just the live part. Add writes here; everything public sees Span.
    [UnscopedRef]
    Span<T> Storage =>
        spilled is not null
            ? spilled.AsSpan()
            : MemoryMarshal.CreateSpan(ref Unsafe.As<TBuffer, T>(ref inline), TBuffer.Capacity);

    /// <summary>A reference to the element at <paramref name="index" />.</summary>
    /// <param name="index">The index, checked against <see cref="Count" />.</param>
    /// <returns>A reference, so large struct elements can be mutated in place.</returns>
    [UnscopedRef]
    public ref T this[int index] {
        get {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
            return ref Span[index];
        }
    }

    /// <summary>Appends an element, spilling to the heap if the inline buffer is full.</summary>
    /// <param name="item">The element to append.</param>
    public void Add(T item) {
        if (count == Capacity()) {
            Grow(count + 1);
        }

        Storage[count] = item;
        count++;
    }

    /// <summary>Appends a run of elements.</summary>
    /// <param name="items">The elements to append.</param>
    public void AddRange(ReadOnlySpan<T> items) {
        if (count + items.Length > Capacity()) {
            Grow(count + items.Length);
        }

        items.CopyTo(Storage[count..]);
        count += items.Length;
    }

    /// <summary>Whether the list holds an element equal to <paramref name="item" />.</summary>
    /// <param name="item">The element to look for.</param>
    /// <returns><see langword="true" /> if it is present.</returns>
    [UnscopedRef]
    public bool Contains(T item) => Span.IndexOf(item) >= 0;

    /// <summary>Removes the element at <paramref name="index" />, keeping the order of the rest.</summary>
    /// <param name="index">The index to remove.</param>
    [UnscopedRef]
    public void RemoveAt(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);

        var span = Span;
        span[(index + 1)..].CopyTo(span[index..]);
        count--;
        ClearSlot(count);
    }

    /// <summary>
    ///     Removes the element at <paramref name="index" /> by moving the last one into its place.
    ///     O(1), and the right choice whenever order does not matter.
    /// </summary>
    /// <param name="index">The index to remove.</param>
    [UnscopedRef]
    public void RemoveAtSwapBack(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);

        var span = Span;
        span[index] = span[count - 1];
        count--;
        ClearSlot(count);
    }

    /// <summary>Empties the list. Keeps the spill buffer if there is one.</summary>
    [UnscopedRef]
    public void Clear() {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) {
            Span.Clear();
        }

        count = 0;
    }

    /// <summary>Copies the elements into a new array.</summary>
    /// <returns>An array holding the elements.</returns>
    [UnscopedRef]
    public T[] ToArray() => Span.ToArray();

    /// <summary>Returns the spill buffer to the pool, if there is one.</summary>
    public void Dispose() {
        var buffer = spilled;
        spilled = null;
        count = 0;

        if (buffer is not null) {
            ArrayPool<T>.Shared.Return(buffer, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        }
    }

    /// <summary>Enumerates the elements.</summary>
    /// <returns>An enumerator over <see cref="Span" />.</returns>
    [UnscopedRef]
    public Span<T>.Enumerator GetEnumerator() => Span.GetEnumerator();

    readonly int Capacity() => spilled?.Length ?? TBuffer.Capacity;

    void Grow(int required) {
        var replacement = ArrayPool<T>.Shared.Rent(Math.Max(required, Capacity() * 2));

        // Copies out of the inline buffer on the first spill and out of the old rental afterwards,
        // which Span already distinguishes for us.
        Span.CopyTo(replacement);

        var previous = spilled;
        spilled = replacement;

        if (previous is not null) {
            ArrayPool<T>.Shared.Return(previous, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        }
    }

    [UnscopedRef]
    void ClearSlot(int index) {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) {
            Storage[index] = default!;
        }
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vixen.Core.Memory;

/// <summary>
///     A block of unmanaged memory holding <typeparamref name="T" /> elements, aligned to a chosen
///     boundary and freed by hand. The storage primitive for ECS chunks, vertex staging and layout
///     node arrays.
/// </summary>
/// <typeparam name="T">The element type. Unmanaged, so the block holds no references.</typeparam>
/// <remarks>
///     <para>
///         <b>Why not a managed array.</b> Nothing here moves, so a pointer into it stays valid
///         without pinning and can be handed to a GPU driver directly. It costs nothing at collection
///         time, however large it is, because the GC never walks it. And the alignment is a promise
///         rather than a hope: a <c>T[]</c> is aligned to the object header's requirements and
///         nothing more, which is not enough for AVX loads or for a mapped buffer's requirements.
///     </para>
///     <para>
///         <b>It is not garbage collected.</b> Forgetting to dispose one leaks memory the profiler
///         will not attribute to anything. Under a debug build every allocation is registered with
///         <see cref="LeakTracker" />, which is how the leak becomes a stack trace instead of a
///         mystery; in release that machinery compiles away and the discipline is the caller's.
///     </para>
/// </remarks>
public readonly unsafe struct NativeArray<T> : IDisposable, IEquatable<NativeArray<T>> where T : unmanaged {
    /// <summary>
    ///     The default alignment: 64 bytes, one cache line on every architecture Vixen targets.
    ///     Enough for AVX-512 loads, and it keeps two arrays from sharing a line and forcing the
    ///     cores writing to them to fight over it.
    /// </summary>
    public const int DefaultAlignment = 64;

    readonly T* pointer;
    readonly int length;
    readonly long trackingHandle;

    /// <summary>An array with no memory behind it.</summary>
    public static NativeArray<T> Empty => default;

    /// <summary>How many elements the array holds.</summary>
    public int Length => length;

    /// <summary>How many bytes it occupies.</summary>
    public long ByteLength => (long)length * sizeof(T);

    /// <summary>Whether the array holds no memory.</summary>
    public bool IsEmpty => pointer is null;

    /// <summary>The address of the first element, for interop and for passing to a driver.</summary>
    public T* Pointer => pointer;

    /// <summary>Allocates <paramref name="length" /> elements of uninitialised memory.</summary>
    /// <param name="length">How many elements. Zero yields <see cref="Empty" />.</param>
    /// <param name="alignment">The byte alignment. Must be a power of two.</param>
    /// <param name="name">A debug name, recorded with the allocation when leak tracking is on.</param>
    /// <exception cref="ArgumentOutOfRangeException">A length or alignment is out of range.</exception>
    /// <remarks>
    ///     <b>The contents are whatever was in that memory before.</b> Use
    ///     <see cref="Zeroed(int,int,string?)" /> where that matters — which is most places, and the
    ///     reason the zeroing version is the one with the friendlier name.
    /// </remarks>
    public NativeArray(int length, int alignment = DefaultAlignment, string? name = null) {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);

        if ((alignment & (alignment - 1)) != 0) {
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment, "Alignment must be a power of two.");
        }

        if (length == 0) {
            this = default;
            return;
        }

        this.length = length;
        pointer = (T*)NativeMemory.AlignedAlloc((nuint)((long)length * sizeof(T)), (nuint)alignment);
        trackingHandle = LeakTracker.Track(
            $"NativeArray<{typeof(T).Name}>",
            name ?? $"{length} elements, {(long)length * sizeof(T)} bytes"
        );
    }

    /// <summary>Allocates <paramref name="length" /> elements and zeroes them.</summary>
    /// <param name="length">How many elements.</param>
    /// <param name="alignment">The byte alignment. Must be a power of two.</param>
    /// <param name="name">A debug name, recorded with the allocation when leak tracking is on.</param>
    /// <returns>The zeroed array.</returns>
    public static NativeArray<T> Zeroed(int length, int alignment = DefaultAlignment, string? name = null) {
        var array = new NativeArray<T>(length, alignment, name);
        array.AsSpan().Clear();
        return array;
    }

    /// <summary>Allocates an array holding a copy of <paramref name="source" />.</summary>
    /// <param name="source">The elements to copy.</param>
    /// <param name="alignment">The byte alignment. Must be a power of two.</param>
    /// <param name="name">A debug name, recorded with the allocation when leak tracking is on.</param>
    /// <returns>The populated array.</returns>
    public static NativeArray<T> From(
        ReadOnlySpan<T> source,
        int alignment = DefaultAlignment,
        string? name = null
    ) {
        var array = new NativeArray<T>(source.Length, alignment, name);
        source.CopyTo(array.AsSpan());
        return array;
    }

    /// <summary>The elements as a span.</summary>
    /// <returns>A span over the whole array.</returns>
    public Span<T> AsSpan() => pointer is null ? default : new(pointer, length);

    /// <summary>A slice of the elements as a span.</summary>
    /// <param name="start">Where the slice starts.</param>
    /// <param name="count">How many elements.</param>
    /// <returns>A span over the slice.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The slice falls outside the array.</exception>
    public Span<T> AsSpan(int start, int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + count, length);
        return AsSpan().Slice(start, count);
    }

    /// <summary>The raw bytes, for uploads and hashing.</summary>
    /// <returns>A span over the whole allocation.</returns>
    public Span<byte> AsBytes() => MemoryMarshal.AsBytes(AsSpan());

    /// <summary>A reference to the element at <paramref name="index" />.</summary>
    /// <param name="index">The index.</param>
    /// <returns>A reference to the element.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="index" /> is out of range. <b>Only under <c>DEBUG</c></b> — a release build
    ///     reads past the end, exactly as the raw pointer this wraps would, because a bounds check on
    ///     every ECS chunk access is precisely the cost this type exists to avoid. Use
    ///     <see cref="AsSpan()" /> where the check is wanted in release too.
    /// </exception>
    public ref T this[int index] {
        get {
#if DEBUG
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, length);
#endif
            return ref Unsafe.AsRef<T>(pointer + index);
        }
    }

    /// <summary>Frees the memory.</summary>
    /// <remarks>
    ///     Freeing twice through two copies of the same struct is a double free, and this type cannot
    ///     detect it — a <see cref="NativeArray{T}" /> is a value, and copying one copies the pointer.
    ///     Whoever allocates it owns it; everything else takes a <see cref="AsSpan()" />.
    /// </remarks>
    public void Dispose() {
        if (pointer is null) {
            return;
        }

        LeakTracker.Untrack(trackingHandle);
        NativeMemory.AlignedFree(pointer);
    }

    /// <summary>Whether two arrays name the same allocation.</summary>
    /// <param name="other">The array to compare with.</param>
    /// <returns><see langword="true" /> if they point at the same memory.</returns>
    public bool Equals(NativeArray<T> other) => pointer == other.pointer && length == other.length;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is NativeArray<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine((nint)pointer, length);

    /// <summary>Whether two arrays name the same allocation.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true" /> if they point at the same memory.</returns>
    public static bool operator ==(NativeArray<T> left, NativeArray<T> right) => left.Equals(right);

    /// <summary>Whether two arrays name different allocations.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns><see langword="true" /> if they differ.</returns>
    public static bool operator !=(NativeArray<T> left, NativeArray<T> right) => !left.Equals(right);

    /// <summary>Enumerates the elements.</summary>
    /// <returns>An enumerator over <see cref="AsSpan()" />.</returns>
    public Span<T>.Enumerator GetEnumerator() => AsSpan().GetEnumerator();

    /// <inheritdoc />
    public override string ToString() =>
        IsEmpty ? $"NativeArray<{typeof(T).Name}>(empty)" : $"NativeArray<{typeof(T).Name}>({length})";
}

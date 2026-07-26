// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Core.Collections;

/// <summary>
///     A typed, generation-checked reference to something living in a <see cref="HandlePool{T}" />:
///     a slot index plus the version that slot was on when the handle was taken.
/// </summary>
/// <typeparam name="T">What the handle refers to. Never dereferenced through the handle itself.</typeparam>
/// <remarks>
///     <para>
///         <b>This is why the RHI exposes no reference types for GPU resources.</b> A buffer that has
///         been destroyed and whose slot has been reused fails a generation check and reports a
///         use-after-free, where a raw pointer or a stale object reference would either crash in
///         native code or, worse, quietly address whatever now lives there. The resource tables stay
///         contiguous and cache-friendly as a side effect.
///     </para>
///     <para>
///         Generations start at 1, so a zeroed handle refers to nothing. Eight bytes, blittable, and
///         cheap to copy into a command list.
///     </para>
/// </remarks>
[DataContract]
public readonly record struct Handle<T>(uint Index, uint Generation) : IComparable<Handle<T>>, IFormattable {
    /// <summary>The handle that refers to nothing.</summary>
    public static Handle<T> Null => default;

    /// <summary>Whether this refers to nothing. True for a zeroed handle.</summary>
    public bool IsNull => Generation == 0;

    /// <summary>Both halves in one word, for hashing and for packing into a sort key.</summary>
    public ulong Packed => ((ulong)Generation << 32) | Index;

    /// <summary>Unpacks a handle produced by <see cref="Packed" />.</summary>
    /// <param name="packed">The packed form.</param>
    /// <returns>The handle it encodes.</returns>
    public static Handle<T> FromPacked(ulong packed) => new((uint)packed, (uint)(packed >> 32));

    /// <inheritdoc />
    public int CompareTo(Handle<T> other) => Packed.CompareTo(other.Packed);

    /// <summary>Whether <paramref name="left" /> sorts before <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator <(Handle<T> left, Handle<T> right) => left.Packed < right.Packed;

    /// <summary>Whether <paramref name="left" /> sorts before or equal to <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator <=(Handle<T> left, Handle<T> right) => left.Packed <= right.Packed;

    /// <summary>Whether <paramref name="left" /> sorts after <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator >(Handle<T> left, Handle<T> right) => left.Packed > right.Packed;

    /// <summary>Whether <paramref name="left" /> sorts after or equal to <paramref name="right" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>The comparison result.</returns>
    public static bool operator >=(Handle<T> left, Handle<T> right) => left.Packed >= right.Packed;

    /// <inheritdoc />
    public override int GetHashCode() => Packed.GetHashCode();

    /// <summary>Renders the handle as <c>Type#index:generation</c>, or <c>Type#null</c>.</summary>
    /// <returns>The handle in text.</returns>
    public override string ToString() =>
        IsNull
            ? $"{typeof(T).Name}#null"
            : string.Create(CultureInfo.InvariantCulture, $"{typeof(T).Name}#{Index}:{Generation}");

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();
}

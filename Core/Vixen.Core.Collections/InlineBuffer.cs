// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

namespace Vixen.Core.Collections;

/// <summary>
///     A fixed-capacity inline buffer, so that a collection can carry its first N elements inside
///     itself rather than in a separate heap allocation.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
/// <remarks>
///     <para>
///         The capacity is a <b>static abstract</b> member, which is what lets
///         <see cref="SmallList{T,TBuffer}" /> be generic over the buffer size without a runtime
///         lookup: the JIT specialises the generic per buffer type and <c>TBuffer.Capacity</c> folds
///         to a constant. Before static abstract interface members this needed either a code
///         generator or a virtual call in the indexer.
///     </para>
///     <para>
///         Implementations are <c>[InlineArray]</c> structs, which the runtime lays out as N
///         adjacent elements with no header and no indirection.
///     </para>
/// </remarks>
public interface IInlineBuffer<T> {
    /// <summary>How many elements the buffer holds. A compile-time constant per implementation.</summary>
    static abstract int Capacity { get; }
}

/// <summary>Four elements inline.</summary>
/// <typeparam name="T">The element type.</typeparam>
[InlineArray(4)]
public struct Buffer4<T> : IInlineBuffer<T> {
    T element;

    /// <inheritdoc />
    public static int Capacity => 4;
}

/// <summary>Eight elements inline.</summary>
/// <typeparam name="T">The element type.</typeparam>
[InlineArray(8)]
public struct Buffer8<T> : IInlineBuffer<T> {
    T element;

    /// <inheritdoc />
    public static int Capacity => 8;
}

/// <summary>Sixteen elements inline.</summary>
/// <typeparam name="T">The element type.</typeparam>
[InlineArray(16)]
public struct Buffer16<T> : IInlineBuffer<T> {
    T element;

    /// <inheritdoc />
    public static int Capacity => 16;
}

/// <summary>Thirty-two elements inline.</summary>
/// <typeparam name="T">The element type.</typeparam>
[InlineArray(32)]
public struct Buffer32<T> : IInlineBuffer<T> {
    T element;

    /// <inheritdoc />
    public static int Capacity => 32;
}

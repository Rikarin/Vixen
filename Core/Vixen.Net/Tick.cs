// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Net;

/// <summary>
///     The unit of network time: one fixed simulation step, counted from when the server started.
/// </summary>
/// <remarks>
///     <para>
///         Every packet is stamped with one, and <b>wall-clock time never appears in a packet</b>.
///         Snapshots, input, interpolation targets and history rings are all keyed by tick, so two
///         machines that disagree about what time it is still agree about what happened when.
///     </para>
///     <para>
///         <b>It wraps, and the comparisons know it.</b> A <see cref="uint" /> at 60 Hz lasts a bit
///         over two years of continuous uptime, which is long enough that no one will ever see it and
///         short enough that somebody would eventually. Comparison is therefore by signed distance —
///         <c>(int)(a - b)</c> — which gives the right answer across the wrap as long as the two
///         ticks are within about 2^31 of each other, and they always are.
///     </para>
///     <para>
///         <b>There is deliberately no <c>&lt;</c> operator and no <see cref="IComparable{T}" />.</b>
///         Modular comparison is not a total order: with three ticks spread far enough apart, A is
///         after B, B is after C, and C is after A. Sorting by it would be undefined behaviour, so
///         the type refuses to look sortable and says <see cref="IsAfter" /> instead.
///     </para>
/// </remarks>
/// <param name="Value">The count. Wraps at <see cref="uint.MaxValue" />.</param>
public readonly record struct Tick(uint Value) {
    /// <summary>The first tick.</summary>
    public static Tick Zero => default;

    /// <summary>The tick after this one.</summary>
    public Tick Next => new(Value + 1);

    /// <summary>The tick before this one.</summary>
    public Tick Previous => new(Value - 1);

    /// <summary>Whether this tick happened after <paramref name="other" />.</summary>
    /// <param name="other">The tick to compare against.</param>
    /// <returns><see langword="true" /> if this one is later.</returns>
    public bool IsAfter(Tick other) => Subtract(other) > 0;

    /// <summary>Whether this tick happened before <paramref name="other" />.</summary>
    /// <param name="other">The tick to compare against.</param>
    /// <returns><see langword="true" /> if this one is earlier.</returns>
    public bool IsBefore(Tick other) => Subtract(other) < 0;

    /// <summary>Moves forward, or backward for a negative count.</summary>
    /// <param name="ticks">How many ticks to move.</param>
    /// <returns>The tick that many later.</returns>
    public Tick Add(int ticks) => new((uint)(Value + (uint)ticks));

    /// <summary>Moves backward, or forward for a negative count.</summary>
    /// <param name="ticks">How many ticks to move back.</param>
    /// <returns>The tick that many earlier.</returns>
    public Tick Subtract(int ticks) => Add(-ticks);

    /// <summary>How many ticks this one is after <paramref name="earlier" />, across the wrap.</summary>
    /// <param name="earlier">The tick to measure from.</param>
    /// <returns>Positive if this one is later, negative if it is earlier.</returns>
    public int Subtract(Tick earlier) => (int)(Value - earlier.Value);

    /// <summary>Moves forward.</summary>
    /// <param name="tick">The tick.</param>
    /// <param name="ticks">How many to move.</param>
    /// <returns>The tick that many later.</returns>
    public static Tick operator +(Tick tick, int ticks) => tick.Add(ticks);

    /// <summary>Moves backward.</summary>
    /// <param name="tick">The tick.</param>
    /// <param name="ticks">How many to move back.</param>
    /// <returns>The tick that many earlier.</returns>
    public static Tick operator -(Tick tick, int ticks) => tick.Subtract(ticks);

    /// <summary>The signed distance between two ticks.</summary>
    /// <param name="later">The tick measured.</param>
    /// <param name="earlier">The tick measured from.</param>
    /// <returns>Positive if <paramref name="later" /> is later.</returns>
    public static int operator -(Tick later, Tick earlier) => later.Subtract(earlier);

    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

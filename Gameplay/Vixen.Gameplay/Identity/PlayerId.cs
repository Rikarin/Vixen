// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Gameplay;

/// <summary>Who somebody is, in whatever numbering a game gives its players.</summary>
/// <remarks>
///     <para>
///         <b>In the kernel because two libraries that may not reference each other both need it.</b>
///         Doc 28's dependency spine allows <c>Items</c> and <c>Combat</c> to be depended on and
///         nothing else — so <c>Chat</c> cannot reference <c>Social</c>, and chat needs to name a
///         sender while social needs to name a member. A type below both is the only place that can
///         be, and the kernel's event bus was already carrying the same number untyped.
///     </para>
///     <para>
///         <b>An opaque number this library never mints.</b> Whether it is a database row, a snowflake
///         or an account hash is a game's decision and doc 27's persistence layer's; what matters here
///         is that it is stable for as long as a party invite or a mute list has to be. Zero means
///         nobody, which is what an event posted by the world rather than by a player carries.
///     </para>
/// </remarks>
/// <param name="Value">The number.</param>
public readonly record struct PlayerId(ulong Value) : IComparable<PlayerId> {
    /// <summary>Nobody. What the world itself does something as.</summary>
    public static PlayerId None => default;

    /// <summary>Whether this names anybody.</summary>
    public bool IsSome => Value != 0;

    /// <inheritdoc />
    public int CompareTo(PlayerId other) => Value.CompareTo(other.Value);

    /// <summary>Compares two.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether the first sorts first.</returns>
    public static bool operator <(PlayerId left, PlayerId right) => left.Value < right.Value;

    /// <summary>Compares two.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether the first sorts first or the same.</returns>
    public static bool operator <=(PlayerId left, PlayerId right) => left.Value <= right.Value;

    /// <summary>Compares two.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether the first sorts last.</returns>
    public static bool operator >(PlayerId left, PlayerId right) => left.Value > right.Value;

    /// <summary>Compares two.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether the first sorts last or the same.</returns>
    public static bool operator >=(PlayerId left, PlayerId right) => left.Value >= right.Value;

    /// <inheritdoc />
    public override string ToString() =>
        IsSome ? Value.ToString(CultureInfo.InvariantCulture) : "nobody";
}

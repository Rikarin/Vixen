// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Gameplay;

/// <summary>One hierarchical, interned gameplay tag — <c>Damage.Fire.Burn</c>, in four bytes.</summary>
/// <remarks>
///     <para>
///         <b>The highest-leverage type in the gameplay libraries</b>
///         (<a href="../../../docs/plan/28-gameplay-framework.md">doc 28</a> § Tags): requirements,
///         immunities, loot conditions, quest objectives, effect stacking, chat gating, matchmaking
///         eligibility, achievement criteria and interaction filters are all tag queries, so a game
///         adds a rule at the altitude it means it by writing a tag rather than a class.
///     </para>
///     <para>
///         <b>The value is a pre-order index into a <see cref="GameplayTagTable" />, not a hash.</b>
///         That is the whole reason a prefix test is two integer comparisons: a pre-order walk gives
///         every tag's descendants a contiguous range of indices, so "is this tag under
///         <c>Damage.Fire</c>" is <c>index >= start &amp;&amp; index &lt; end</c> — see
///         <see cref="GameplayTagRange" />. A hash would make the id independent of any table and
///         make every prefix test a string operation, which is the trade the other way round and the
///         wrong one for a value compared on the damage path.
///     </para>
///     <para>
///         ⚠ <b>An index is only meaningful against the table that assigned it, and adding a tag
///         renumbers.</b> Two consequences, both deliberate and both stated here because getting
///         either wrong is silent. On the wire: both ends must hold the same table, which they do
///         because the session handshake compares the catalog's build hash before anything is
///         dispatched ([16](../../../docs/plan/16-networking.md) § Security) and
///         <see cref="GameplayTagTable.BuildHash" /> is what a table contributes to it. In durable
///         state: <b>never persist an index.</b> Persist <see cref="GameplayTagTable.SymbolOf" />'s
///         symbol, which is a hash of the name and survives every renumbering; a saved character
///         holding indices would be a saved character whose immunities changed meaning the next time
///         somebody added a tag.
///     </para>
/// </remarks>
/// <param name="Index">
///     The tag's position in the pre-order walk of its table, counting from one. Zero is
///     <see cref="None" />.
/// </param>
public readonly record struct GameplayTag(uint Index) : IComparable<GameplayTag> {
    /// <summary>Not a tag. Matches nothing, and nothing matches it.</summary>
    public static GameplayTag None => default;

    /// <summary>Whether this names one.</summary>
    public bool IsSome => Index != 0;

    /// <inheritdoc />
    public int CompareTo(GameplayTag other) => Index.CompareTo(other.Index);

    /// <summary>Orders by index, which is the pre-order walk — a parent sorts before its children.</summary>
    /// <param name="left">The left tag.</param>
    /// <param name="right">The right tag.</param>
    /// <returns>Whether <paramref name="left" /> sorts first.</returns>
    public static bool operator <(GameplayTag left, GameplayTag right) => left.Index < right.Index;

    /// <summary>Orders by index.</summary>
    /// <param name="left">The left tag.</param>
    /// <param name="right">The right tag.</param>
    /// <returns>Whether <paramref name="left" /> sorts first or the two are equal.</returns>
    public static bool operator <=(GameplayTag left, GameplayTag right) => left.Index <= right.Index;

    /// <summary>Orders by index.</summary>
    /// <param name="left">The left tag.</param>
    /// <param name="right">The right tag.</param>
    /// <returns>Whether <paramref name="right" /> sorts first.</returns>
    public static bool operator >(GameplayTag left, GameplayTag right) => left.Index > right.Index;

    /// <summary>Orders by index.</summary>
    /// <param name="left">The left tag.</param>
    /// <param name="right">The right tag.</param>
    /// <returns>Whether <paramref name="right" /> sorts first or the two are equal.</returns>
    public static bool operator >=(GameplayTag left, GameplayTag right) => left.Index >= right.Index;

    /// <inheritdoc />
    /// <remarks>
    ///     The index, not the name. A tag does not know its table and a <c>ToString</c> that reached
    ///     for a process-wide one would be a second, ambient source of truth about content — which is
    ///     the thing this type is arranged to avoid. <see cref="GameplayTagTable.NameOf" /> is where a
    ///     name comes from, and a debugger display or a log line is expected to have the table.
    /// </remarks>
    public override string ToString() =>
        Index == 0 ? "no tag" : string.Create(CultureInfo.InvariantCulture, $"tag #{Index}");
}

/// <summary>A resolved tag prefix: one tag and everything beneath it, as a half-open range.</summary>
/// <remarks>
///     <para>
///         <b>This is the authored half of a tag comparison and the reason the dynamic half is four
///         bytes.</b> A rule says <em>fire resistance reduces <c>Damage.Fire.*</c></em>; the prefix
///         comes out of a definition and is resolved against the table exactly once, at load, into
///         the two numbers that make the test. What is compared a thousand times a frame is a
///         <see cref="GameplayTag" /> against one of these, with no table read and no string.
///     </para>
///     <para>
///         Half-open, so an empty range is <c>Start == End</c> and a leaf tag's range is exactly one
///         wide. <see cref="Empty" /> contains nothing, which is what an unresolvable prefix becomes —
///         a rule about a tag this content does not have matches nothing rather than everything, and
///         the difference between those two is a boss that is immune to all damage.
///     </para>
/// </remarks>
/// <param name="Start">The prefix's own index — the first of the range.</param>
/// <param name="End">One past the last index in the prefix's subtree.</param>
public readonly record struct GameplayTagRange(uint Start, uint End) {
    /// <summary>The range that contains nothing.</summary>
    public static GameplayTagRange Empty => default;

    /// <summary>Whether it can contain anything at all.</summary>
    public bool IsSome => End > Start;

    /// <summary>How many tags fall in it, the prefix itself included.</summary>
    public int Count => (int)(End - Start);

    /// <summary>The tag the range is the subtree of.</summary>
    public GameplayTag Tag => new(Start);

    /// <summary>Whether a tag is this prefix or beneath it.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>Whether it matches.</returns>
    /// <remarks>
    ///     The two integer comparisons doc 28 promises. <see cref="GameplayTag.None" /> has index
    ///     zero and no range starts there, so it matches nothing without a special case.
    /// </remarks>
    public bool Contains(GameplayTag tag) => tag.Index >= Start && tag.Index < End;

    /// <inheritdoc />
    public override string ToString() =>
        IsSome ? string.Create(CultureInfo.InvariantCulture, $"tags [{Start}, {End})") : "no tags";
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay;

/// <summary>A rule about tags: all of these, any of these, none of these.</summary>
/// <remarks>
///     <para>
///         <b>Three lists and nothing else, deliberately.</b> An arbitrary boolean expression over
///         tags is a thing a generated inspector cannot draw, a thing a designer reads wrong, and a
///         thing whose evaluation order becomes a question. <em>All of · any of · none of</em> covers
///         every rule doc 28 asks for — an ability's target filter, an effect's immunity list, a
///         vendor's stock condition, a quest objective's kill filter, a chat channel's gate — and it
///         composes, because a rule needing more than one of these is more than one rule.
///     </para>
///     <para>
///         <b>Resolved once, against a table.</b> The authored form is dotted strings; what is kept is
///         <see cref="GameplayTagRange" />s, so matching never touches the table or a string. A prefix
///         the content does not have resolves to an empty range, which matches nothing — so an
///         <c>any</c> list of unknown tags fails and a <c>none</c> list of unknown tags passes, both
///         of which are the safe reading.
///     </para>
/// </remarks>
public sealed class GameplayTagQuery {
    static readonly GameplayTagRange[] Nothing = [];

    readonly GameplayTagRange[] all;
    readonly GameplayTagRange[] any;
    readonly GameplayTagRange[] none;

    GameplayTagQuery(GameplayTagRange[] all, GameplayTagRange[] any, GameplayTagRange[] none) {
        this.all = all;
        this.any = any;
        this.none = none;
    }

    /// <summary>The query every set satisfies. What an unconditional rule resolves to.</summary>
    public static GameplayTagQuery Always { get; } = new(Nothing, Nothing, Nothing);

    /// <summary>Whether this query asks anything at all.</summary>
    public bool IsSome => all.Length > 0 || any.Length > 0 || none.Length > 0;

    /// <summary>The prefixes a set must all match.</summary>
    public ReadOnlySpan<GameplayTagRange> All => all;

    /// <summary>The prefixes a set must match at least one of.</summary>
    public ReadOnlySpan<GameplayTagRange> Any => any;

    /// <summary>The prefixes a set must match none of.</summary>
    public ReadOnlySpan<GameplayTagRange> None => none;

    /// <summary>Resolves an authored query against a tag table.</summary>
    /// <param name="table">The table the names are resolved through.</param>
    /// <param name="all">Prefixes the subject must all match, or null for none.</param>
    /// <param name="any">Prefixes the subject must match at least one of, or null for none.</param>
    /// <param name="none">Prefixes the subject must match none of, or null for none.</param>
    /// <returns>The query.</returns>
    public static GameplayTagQuery Resolve(
        GameplayTagTable table,
        IEnumerable<string>? all = null,
        IEnumerable<string>? any = null,
        IEnumerable<string>? none = null
    ) {
        ArgumentNullException.ThrowIfNull(table);

        var resolvedAll = Resolve(table, all);
        var resolvedAny = Resolve(table, any);
        var resolvedNone = Resolve(table, none);

        return resolvedAll.Length == 0 && resolvedAny.Length == 0 && resolvedNone.Length == 0
            ? Always
            : new(resolvedAll, resolvedAny, resolvedNone);
    }

    /// <summary>Whether a set satisfies the query.</summary>
    /// <param name="tags">The subject's tags.</param>
    /// <returns>Whether it matches.</returns>
    /// <remarks>
    ///     A null set is treated as an empty one, because "the target has no tag container" and "the
    ///     target has no tags" are the same fact to a rule and making the caller build an empty set to
    ///     ask is how a null check ends up inside the damage path.
    /// </remarks>
    public bool Matches(GameplayTagSet? tags) {
        foreach (var range in none) {
            if (tags?.ContainsAny(range) == true) {
                return false;
            }
        }

        foreach (var range in all) {
            if (tags?.ContainsAny(range) != true) {
                return false;
            }
        }

        if (any.Length == 0) {
            return true;
        }

        foreach (var range in any) {
            if (tags?.ContainsAny(range) == true) {
                return true;
            }
        }

        return false;
    }

    static GameplayTagRange[] Resolve(GameplayTagTable table, IEnumerable<string>? names) {
        if (names is null) {
            return Nothing;
        }

        List<GameplayTagRange>? resolved = null;

        foreach (var name in names) {
            (resolved ??= []).Add(table.RangeOf(name));
        }

        return resolved is null ? Nothing : [.. resolved];
    }
}

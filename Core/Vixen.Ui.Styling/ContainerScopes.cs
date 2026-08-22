// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>The container chains the document's elements are inside, and what they answer.</summary>
/// <remarks>
///     <para>
///         <b>What <see cref="MediaScopes" /> is for a surface, this is for a box.</b> An element
///         carries a scope on its <c>StyleTree</c> slot, the cascade reads one integer before the
///         matcher, and a group's verdict is cached against
///         <see cref="ContainerConditions.Revision" />. The difference is what a scope <i>is</i>: a
///         surface is a place the document is shown and there are a handful, a container is any box
///         with a <c>container-type</c> and there can be one per row of a list.
///     </para>
///     <para>
///         ⚠ <b>Interned on the whole chain by value, and that is a decision about the sharing cache
///         rather than about memory.</b> The obvious design is a scope per container <i>element</i>,
///         which is what <see cref="MediaScopes" /> does — and it would give every row of a thousand-row
///         list a distinct scope id. <c>StyleSharingKey</c> carries the scope, so distinct ids mean no
///         two rows ever share a computed style: a document that used one container query would lose
///         the sharing cache entirely, silently, and only on the documents big enough to need it.
///         Interning on <c>(parent, name, box)</c> collapses a thousand identical rows to one scope,
///         so sharing works exactly as well as it did before the query was written.
///     </para>
///     <para>
///         ⚠ <b>The cost of interning by value is churn while a box is moving, and it is not paid
///         for yet.</b> A drag that resizes a container by a pixel a frame interns a new scope each
///         frame, and nothing evicts the old ones — the table grows for the length of the drag.
///         <see cref="Reset" /> is the whole of the eviction policy today and the caller is expected
///         to use it when it rebuilds. A generation stamp per scope, swept after a pass that moved
///         nothing, is the shape of the answer and is deliberately not built here: it wants the
///         layout wiring in front of it to say how often a rebuild actually happens.
///     </para>
///     <para>
///         ⚠ <b>A container does not answer its own query.</b> CSS Containment 3 § 5.1 scopes a
///         container query to the elements <i>inside</i> the container, so the scope an element is in
///         is its ancestors' and never includes itself. That is a property of how the caller assigns
///         scopes — <see cref="Root" /> for an element with no container above it, and a container's
///         own scope handed to its children rather than to itself — and it is the single easiest thing
///         to get wrong, because getting it wrong produces a query that matches slightly too often
///         rather than one that never matches.
///     </para>
/// </remarks>
public sealed class ContainerScopes {
    /// <summary>The scope of an element with no query container above it.</summary>
    public const int Root = 0;

    readonly ContainerConditions conditions;
    readonly List<Entry> scopes = [];
    readonly Dictionary<Key, int> interned = [];
    readonly List<ContainerScope> chain = [];

    /// <summary>Creates a registry over a condition table, with the root scope in it.</summary>
    /// <param name="conditions">The groups verdicts are about.</param>
    public ContainerScopes(ContainerConditions conditions) {
        ArgumentNullException.ThrowIfNull(conditions);

        this.conditions = conditions;
        scopes.Add(new Entry(-1, string.Empty, default, default));
    }

    /// <summary>How many distinct chains have been interned, the root included.</summary>
    public int Count => scopes.Count;

    /// <summary>The scope inside a container, given the scope that container is itself in.</summary>
    /// <param name="parent">The scope the container element is in, or <see cref="Root" />.</param>
    /// <param name="name">The container's <c>container-name</c>, or empty.</param>
    /// <param name="box">Its measured box and which axes it may be asked about.</param>
    /// <returns>The scope its children are in.</returns>
    /// <remarks>
    ///     The return value is what the container's <i>descendants</i> carry, never what the container
    ///     carries. See the remarks on the class.
    /// </remarks>
    public int Enter(int parent, string? name, ContainerBox box) {
        ArgumentOutOfRangeException.ThrowIfNegative(parent);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(parent, scopes.Count);

        var key = new Key(parent, name ?? string.Empty, box);

        if (interned.TryGetValue(key, out var existing)) {
            return existing;
        }

        scopes.Add(new Entry(parent, key.Name, box, default));
        interned[key] = scopes.Count - 1;

        return scopes.Count - 1;
    }

    /// <summary>The container a scope's innermost box is, for a caller that needs to read it back.</summary>
    /// <param name="scope">The scope.</param>
    /// <returns>Its innermost container, or a <see cref="ContainerKind.Normal" /> one for the root.</returns>
    public ContainerScope InnermostOf(int scope) =>
        (uint) scope >= (uint) scopes.Count
            ? default
            : new ContainerScope(scopes[scope].Name, scopes[scope].Box);

    /// <summary>Which container groups hold for an element in a scope.</summary>
    /// <param name="scope">The scope, or anything out of range for the conservative answer.</param>
    /// <returns>The verdicts.</returns>
    /// <remarks>
    ///     Out of range answers <c>default</c> — the unconditional group and nothing else — rather
    ///     than throwing, for the reason <see cref="MediaScopes.VerdictsOf" /> gives: the caller is
    ///     the cascade, and a node carrying a stale scope should show as missing <c>@container</c>
    ///     rules rather than as a crash mid-frame.
    /// </remarks>
    public ContainerVerdicts VerdictsOf(int scope) {
        if ((uint) scope >= (uint) scopes.Count) {
            return default;
        }

        var entry = scopes[scope];

        if (entry.Verdicts.Revision == conditions.Revision) {
            return entry.Verdicts;
        }

        chain.Clear();

        // Nearest first, which is the order the name walk wants and the order the chain is built in
        // by following parent links outwards.
        for (var at = scope; at > Root; at = scopes[at].Parent) {
            chain.Add(new ContainerScope(scopes[at].Name, scopes[at].Box));
        }

        var evaluated = conditions.Evaluate(chain);
        scopes[scope] = entry with { Verdicts = evaluated };

        return evaluated;
    }

    /// <summary>Whether any scope's verdicts have moved since they were last read.</summary>
    /// <returns>Whether a caller holding resolved styles has to forget them.</returns>
    /// <remarks>What a reload owes its consumers, for the reason <see cref="MediaScopes.Refresh" /> is.</remarks>
    public bool Refresh() {
        var moved = false;

        for (var i = 0; i < scopes.Count; i++) {
            var before = scopes[i].Verdicts;

            if (before.Revision == conditions.Revision) {
                continue;
            }

            moved |= VerdictsOf(i) != before;
        }

        return moved;
    }

    /// <summary>Forgets every scope but the root.</summary>
    /// <remarks>
    ///     ⚠ <b>Every element carrying a scope is left pointing at one that no longer exists</b>, which
    ///     <see cref="VerdictsOf" /> answers conservatively rather than throwing for. A caller that
    ///     resets is a caller that is about to re-assign every scope, and the only safe order is reset
    ///     then re-assign then re-cascade.
    /// </remarks>
    public void Reset() {
        scopes.RemoveRange(1, scopes.Count - 1);
        interned.Clear();
    }

    readonly record struct Key(int Parent, string Name, ContainerBox Box);

    readonly record struct Entry(int Parent, string Name, ContainerBox Box, ContainerVerdicts Verdicts);
}

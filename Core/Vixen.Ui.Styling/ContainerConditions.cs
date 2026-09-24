// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>The <c>@container</c> groups a stylesheet declared, as a tree of conjunctions.</summary>
/// <remarks>
///     <para>
///         <b>The same shape as <see cref="MediaConditions" /> and a harder question.</b> A
///         <c>@media</c> group is answered once per <i>surface</i>, because that is the largest thing
///         its condition can be about. A <c>@container</c> group is answered once per <i>container</i>
///         — the same rule applies inside one panel and not inside its neighbour — so what a verdict
///         is about is not the window but a box, and boxes are a layout result rather than a document
///         property.
///     </para>
///     <para>
///         ⚠ <b>A group carries a name as well as a condition, and the name is not part of the
///         condition.</b> <c>@container card (min-width: 400px)</c> asks the nearest ancestor
///         <i>called</i> <c>card</c>, which may be several boxes above the nearest container of any
///         kind — so the name selects <i>which</i> box the condition is evaluated against, and
///         evaluating it against the wrong one is a query that answers confidently and wrongly.
///         <see cref="ContainerScopes" /> does that walk; this only records what to walk for.
///     </para>
///     <para>
///         ⚠ <b>Nesting conjoins through the parent link, exactly as <c>@media</c>'s does — and the
///         two nest through each other.</b> <c>@media (min-width: 900px) { @container (min-width:
///         400px) { … } }</c> is two questions of two different subjects, and a rule inside it carries
///         one id from each table. Keeping them in separate tables rather than one is what makes that
///         work without a tagged union: a rule has a <c>Conditions</c> and a <c>Containers</c>, and
///         both have to hold.
///     </para>
///     <para>
///         ⚠ <b>A condition that cannot be read never becomes a group</b>, for the reason
///         <see cref="MediaConditions" /> gives: <see cref="ContainerQuery.TryEvaluate" /> refuses on
///         the text alone and never on the box, so unreadability is decided once, at load, where the
///         diagnostic has somewhere to go.
///     </para>
/// </remarks>
public sealed class ContainerConditions {
    /// <summary>The group a rule outside every <c>@container</c> belongs to, which always holds.</summary>
    public const int Unconditional = 0;

    readonly List<Group> groups = [new(-1, string.Empty, string.Empty)];
    readonly Dictionary<Group, int> interned = [];

    // Parallel to `groups`, null for every size group. Kept out of `Group` so the record stays a
    // value the interning dictionary can compare — an array field would compare by reference.
    readonly List<StyleCondition?> styles = [null];

    // Parallel to `groups`: the least containment a box needs to be asked the group's size features,
    // `ContainerQuery.Requires` read once at registration rather than per element per walk. A
    // style-only group asks no box, so it needs none and holds `Normal`.
    readonly List<ContainerKind> requires = [ContainerKind.Normal];

    // Parallel to `groups`: whether this group or any group it is nested in is a style group that
    // asks above the parent, so a rule carrying it needs the element's ancestors' styles (#1421).
    readonly List<bool> asksAncestors = [false];

    // Parallel to `groups`: for a style group whose size half is `or`-joined, the size group that
    // answers that half, whose verdict is a disjunct; `Unconditional` for every other group.
    readonly List<int> disjuncts = [Unconditional];

    /// <summary>Whether any registered group is a <c>style()</c> query.</summary>
    /// <remarks>
    ///     The cascade's fast path: every stylesheet this repository ships has none, and the resolver
    ///     asks this once per element before it would walk a group's enclosing chain per candidate.
    /// </remarks>
    internal bool HasStyleQueries { get; private set; }

    /// <summary>
    ///     Whether any registered group is a <c>style()</c> query that can ask above the parent: a
    ///     named one, or one mixed with a size feature.
    /// </summary>
    /// <remarks>
    ///     What turns on the two costs those forms have and the unnamed style-only one does not. The
    ///     resolver collects an element's ancestors' styles, and <see cref="StyleUpdater" />
    ///     re-resolves the whole subtree of an element such a query can ask whose style moved. See
    ///     <see cref="StyleQuery" />. ⚠ For the resolver this is only the fast path's gate: the
    ///     collection itself waits for a candidate whose group <see cref="AsksAncestors" /> (#1421).
    /// </remarks>
    internal bool HasAncestorStyleQueries { get; private set; }

    /// <summary>Whether any registered group is a <c>style()</c> query mixed with a size feature.</summary>
    /// <remarks>
    ///     ⚠ Such a query asks the nearest size container, <i>named or not</i>. So once a sheet has one,
    ///     every size container is an element a query can ask, and <see cref="StyleUpdater" />'s
    ///     whole-subtree edge has to cover the unnamed ones too.
    /// </remarks>
    internal bool HasSizedStyleQueries { get; private set; }

    /// <summary>How many groups there are, the unconditional one included.</summary>
    public int Count => groups.Count;

    /// <summary>Bumped whenever a group is added, so a cached evaluation can tell it is stale.</summary>
    public int Revision { get; private set; }

    /// <summary>Registers a container group, or finds the one already registered.</summary>
    /// <param name="within">The group this one is nested in, or <see cref="Unconditional" />.</param>
    /// <param name="name">The container name it asks for, or empty for the nearest of any name.</param>
    /// <param name="condition">The size condition.</param>
    /// <returns>The group's id, which a rule carries.</returns>
    /// <remarks>
    ///     Interned on the triple, which keeps a generated sheet from growing a group per class for
    ///     the reason <see cref="MediaConditions.Register" /> gives.
    /// </remarks>
    public int Register(int within, string? name, string? condition) {
        ArgumentOutOfRangeException.ThrowIfNegative(within);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(within, groups.Count);

        var key = new Group(within, name ?? string.Empty, condition ?? string.Empty);

        if (interned.TryGetValue(key, out var existing)) {
            return existing;
        }

        groups.Add(key);
        styles.Add(null);
        requires.Add(ContainerQuery.Requires(key.Condition));
        asksAncestors.Add(asksAncestors[within]);
        disjuncts.Add(Unconditional);
        interned[key] = groups.Count - 1;
        Revision++;

        return groups.Count - 1;
    }

    /// <summary>Registers a <c>style()</c> group, or finds the one already registered.</summary>
    /// <param name="within">The group this one is nested in, or <see cref="Unconditional" />.</param>
    /// <param name="prelude">The prelude as written, which is what diagnostics and interning use.</param>
    /// <param name="condition">The condition it was read into.</param>
    /// <param name="orSize">
    ///     For <c>(min-width: …) or style(…)</c>, the size group registered beside this one for the size
    ///     half, whose verdict <see cref="StyleHolds" /> reads as a disjunct; otherwise
    ///     <see cref="Unconditional" />. The <c>and</c> form nests in its size group instead.
    /// </param>
    /// <returns>The group's id, which a rule carries.</returns>
    /// <remarks>
    ///     ⚠ <b>Its verdict per container chain is always "holds"</b>, because a chain is boxes and this
    ///     asks nothing of a box: <see cref="Evaluate" /> passes it through and the cascade answers the
    ///     condition against the parent's style, or the named ancestor's. See <see cref="StyleQuery" />
    ///     for why there.
    /// </remarks>
    internal int RegisterStyle(int within, string prelude, StyleCondition condition, int orSize = Unconditional) {
        ArgumentOutOfRangeException.ThrowIfNegative(within);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(within, groups.Count);

        // A name no size query can carry, so a style group and a size group never intern together.
        // The prelude carries the container name, so two names never intern together either.
        var key = new Group(within, "\0style", prelude);

        if (interned.TryGetValue(key, out var existing)) {
            return existing;
        }

        groups.Add(key);
        styles.Add(condition);

        // A mixed group's style half asks the element its size half asks, so it needs what the size
        // half needs; a style-only group asks any element, `normal` included.
        requires.Add(condition.Size is { } size ? ContainerQuery.Requires(size) : ContainerKind.Normal);
        asksAncestors.Add(condition.AsksAncestors || asksAncestors[within]);
        disjuncts.Add(orSize);
        interned[key] = groups.Count - 1;
        HasStyleQueries = true;
        HasAncestorStyleQueries |= condition.AsksAncestors;
        HasSizedStyleQueries |= condition.Size is not null;
        Revision++;

        return groups.Count - 1;
    }

    /// <summary>Whether a rule carrying a group needs the element's ancestors' styles to be answered.</summary>
    /// <param name="group">The group a rule carries.</param>
    /// <returns>
    ///     Whether the group, or any group it is nested in, is a named or mixed <c>style()</c> query:
    ///     the per-rule form of <see cref="HasAncestorStyleQueries" />, which is per document.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>What makes the resolver's ancestor collection lazy (#1421).</b> The document-wide flag
    ///     turned it on for every element once any sheet declared one such query, including the
    ///     elements none of those rules could reach. The resolver asks this of each candidate instead.
    /// </remarks>
    internal bool AsksAncestors(int group) => asksAncestors[group];

    /// <summary>Whether every <c>style()</c> group in a group's stack holds for one element.</summary>
    /// <param name="group">The group a rule carries.</param>
    /// <param name="parent">The parent's resolved style, or null for a root.</param>
    /// <param name="ancestors">
    ///     The element's ancestors' resolved styles, nearest first, which a named group searches. Only
    ///     collected when <see cref="AsksAncestors" /> is true of this group.
    /// </param>
    /// <param name="contained">
    ///     The element's container verdicts, which answer the size half of an <c>or</c>-joined mixed
    ///     group. An <c>and</c>-joined one is answered by nesting and never reads them here.
    /// </param>
    /// <param name="properties">The table property names are interned in.</param>
    /// <param name="values">The table values are interned in.</param>
    /// <returns>Whether the style half of the stack holds; the size half is the verdicts' question.</returns>
    internal bool StyleHolds(
        int group,
        ComputedStyle? parent,
        ReadOnlySpan<ComputedStyle> ancestors,
        ContainerVerdicts contained,
        NameTable properties,
        NameTable values
    ) {
        for (var at = group; at > Unconditional; at = groups[at].Within) {
            if (styles[at] is not { } condition) {
                continue;
            }

            // ⚠ The size half of `(min-width: …) or style(…)`, answered off the box like any size
            // group, and enough on its own. Both halves ask one element — the size group resolves
            // with `requires[at]`'s rule and `Nearest` below with the same — so when no container is
            // eligible both are false and the query is, as CSS says an unknown one is.
            if (disjuncts[at] != Unconditional && contained.Holds(disjuncts[at])) {
                continue;
            }

            var container = condition.AsksAncestors
                ? Nearest(ancestors, condition.Name, requires[at], properties, values)
                : parent;

            if (!StyleQuery.Holds(condition, container, properties, values)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>The nearest ancestor style a named or mixed condition asks, or null when none is eligible.</summary>
    /// <param name="ancestors">The ancestors' styles, nearest first.</param>
    /// <param name="name">The container name asked for, or empty for any.</param>
    /// <param name="required">
    ///     The least containment an eligible element has: <see cref="ContainerKind.Normal" /> for a
    ///     style-only condition, which any element answers, and for a mixed one what its size half
    ///     needs, <see cref="ContainerKind.Size" /> if that reads the block axis. That is the rule
    ///     <see cref="TryResolve" /> applies to the same query's size half, and it is what makes the
    ///     two halves ask one element.
    /// </param>
    /// <param name="properties">The table property names are interned in.</param>
    /// <param name="values">The table values are interned in.</param>
    static ComputedStyle? Nearest(
        ReadOnlySpan<ComputedStyle> ancestors,
        string name,
        ContainerKind required,
        NameTable properties,
        NameTable values
    ) {
        foreach (var style in ancestors) {
            if (required != ContainerKind.Normal && StyleQuery.KindOf(style, properties, values) < required) {
                continue;
            }

            if (name.Length == 0 || StyleQuery.Names(style, properties, values, name)) {
                return style;
            }
        }

        return null;
    }

    /// <summary>Forgets every group, as a reload does.</summary>
    public void Reset() {
        groups.RemoveRange(1, groups.Count - 1);
        styles.RemoveRange(1, styles.Count - 1);
        requires.RemoveRange(1, requires.Count - 1);
        asksAncestors.RemoveRange(1, asksAncestors.Count - 1);
        disjuncts.RemoveRange(1, disjuncts.Count - 1);
        interned.Clear();
        HasStyleQueries = false;
        HasAncestorStyleQueries = false;
        HasSizedStyleQueries = false;
        Revision++;
    }

    /// <summary>The container name a group asks for, or empty for the nearest container.</summary>
    /// <param name="group">The group.</param>
    /// <returns>Its name.</returns>
    public string NameOf(int group) => groups[group].Name;

    /// <summary>The condition text a group was registered with.</summary>
    /// <param name="group">The group.</param>
    /// <returns>Its own condition, without its enclosing groups'.</returns>
    public string ConditionOf(int group) => groups[group].Condition;

    /// <summary>The group a group is nested in, or -1 for <see cref="Unconditional" />.</summary>
    /// <param name="group">The group.</param>
    /// <returns>Its enclosing group.</returns>
    public int EnclosingOf(int group) => groups[group].Within;

    /// <summary>Asks every group whether it holds for an element in a given container chain.</summary>
    /// <param name="chain">The containers above the element, nearest first.</param>
    /// <returns>The verdicts, indexed by group.</returns>
    /// <remarks>
    ///     ⚠ <b>Ascending, and that is what makes the conjunction work in one pass</b> —
    ///     <see cref="Register" /> can only nest inside a group that already exists, so an enclosing
    ///     group always has a lower id and has already been answered.
    /// </remarks>
    public ContainerVerdicts Evaluate(IReadOnlyList<ContainerScope> chain) {
        ArgumentNullException.ThrowIfNull(chain);

        var holds = new bool[groups.Count];
        holds[Unconditional] = true;

        for (var i = 1; i < groups.Count; i++) {
            var group = groups[i];

            if (!holds[group.Within]) {
                // Sealed behind a group that does not hold, so the condition is never asked and the
                // name is never walked for.
                continue;
            }

            if (styles[i] is not null) {
                // A style group asks the parent's style and no box, so the chain has nothing to say
                // about it; the cascade answers it per element. See `RegisterStyle`.
                holds[i] = true;
                continue;
            }

            if (!TryResolve(chain, group.Name, requires[i], out var box)) {
                // No eligible container above this element. CSS Containment 3 § 5.1: a query with no
                // container to ask resolves to false rather than to an error.
                continue;
            }

            holds[i] = ContainerQuery.TryEvaluate(group.Condition, box, out var matches, out _) && matches;
        }

        return new ContainerVerdicts(holds, Revision);
    }

    /// <summary>Finds the container a named query is about.</summary>
    /// <param name="chain">The containers above the element, nearest first.</param>
    /// <param name="name">The name asked for, or empty for the nearest of any name.</param>
    /// <param name="required">
    ///     The least containment that can answer every feature, <see cref="ContainerQuery.Requires" />.
    /// </param>
    /// <param name="box">Receives its box.</param>
    /// <returns>Whether there is one.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Nearest wins, and an unnamed query does not skip a named container.</b> A name is a
    ///         label a container carries, not a category it belongs to, so <c>@container (min-width:
    ///         …)</c> asks whatever box is closest whether or not that box was given a name. Skipping
    ///         named ones would make adding a name to a container silently retarget every unnamed
    ///         query below it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>But a container that cannot answer every feature is skipped, before the name is
    ///         looked at (#1429).</b> CSS Containment 3 § 5.1: the container a query asks is the
    ///         nearest ancestor that is a valid query container for <i>every</i> feature in it. This
    ///         used to stop at the first non-<c>normal</c> box, so <c>(min-height: 200px)</c> under an
    ///         <c>inline-size</c> container asked it, got no height and resolved <c>false</c> however
    ///         tall a <c>size</c> container above it was. A name does not change that: a box named
    ///         <c>card</c> that is only <c>inline-size</c> is not the <c>card</c> a height query asks.
    ///     </para>
    /// </remarks>
    static bool TryResolve(IReadOnlyList<ContainerScope> chain, string name, ContainerKind required, out ContainerBox box) {
        for (var i = 0; i < chain.Count; i++) {
            var candidate = chain[i];

            if (candidate.Box.Kind == ContainerKind.Normal || candidate.Box.Kind < required) {
                continue;
            }

            if (name.Length != 0 && !Carries(candidate.Name, name)) {
                continue;
            }

            box = candidate.Box;
            return true;
        }

        box = default;
        return false;
    }

    /// <summary>Whether a written <c>container-name</c> list includes one name.</summary>
    /// <param name="names">The list as the container wrote it, <c>card side</c>.</param>
    /// <param name="name">The one name a query asks for.</param>
    /// <returns>Whether any entry is that name, compared whole and case-sensitively.</returns>
    /// <remarks>
    ///     ⚠ <b>A list, CSS Containment 3 § 3.1, and this used to compare it whole (#273).</b> A box
    ///     named <c>card side</c> was found by neither <c>@container card</c> nor <c>@container
    ///     side</c>, while a <c>style()</c> query found it by either — <c>StyleQuery.Names</c> splits
    ///     the list. A mixed query asks both halves of one box, so the two have to agree on what a
    ///     name is.
    /// </remarks>
    internal static bool Carries(ReadOnlySpan<char> names, string name) {
        foreach (var range in names.SplitAny(' ', '\t', '\n')) {
            if (names[range].Equals(name, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    readonly record struct Group(int Within, string Name, string Condition);
}

/// <summary>One container in an element's ancestry.</summary>
/// <param name="Name">Its <c>container-name</c> list as written, <c>card side</c>, or empty. A query asks for one entry.</param>
/// <param name="Box">Its measured box and which axes it may be asked about.</param>
public readonly record struct ContainerScope(string Name, ContainerBox Box);

/// <summary>Which container groups hold for one element's container chain.</summary>
/// <remarks>
///     ⚠ <b><c>default</c> is "only the unconditional group", which is the conservative answer.</b>
///     An element whose chain nobody has evaluated shows the rules that were never inside a
///     <c>@container</c> at all — so the failure mode of forgetting to evaluate is a document that
///     ignores its container queries, never one that applies all of them at once.
/// </remarks>
public readonly struct ContainerVerdicts : IEquatable<ContainerVerdicts> {
    readonly bool[]? holds;

    internal ContainerVerdicts(bool[] holds, int revision) {
        this.holds = holds;
        Revision = revision;
    }

    /// <summary>Which <see cref="ContainerConditions.Revision" /> these were computed against.</summary>
    public int Revision { get; }

    /// <summary>Whether a group's rules apply here.</summary>
    /// <param name="group">The group a rule carries.</param>
    /// <returns>Whether every condition in its stack holds.</returns>
    public bool Holds(int group) =>
        group == ContainerConditions.Unconditional
        || (holds is not null && (uint) group < (uint) holds.Length && holds[group]);

    /// <inheritdoc />
    public bool Equals(ContainerVerdicts other) {
        if (ReferenceEquals(holds, other.holds)) {
            return true;
        }

        if (holds is null || other.holds is null) {
            // One is the conservative default, so they agree only if nothing but the unconditional
            // group held in the other.
            var evaluated = holds ?? other.holds!;

            for (var i = 1; i < evaluated.Length; i++) {
                if (evaluated[i]) {
                    return false;
                }
            }

            return true;
        }

        return holds.AsSpan().SequenceEqual(other.holds);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ContainerVerdicts other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() {
        // Coarse for the reason `MediaVerdicts.GetHashCode` is: these are compared for "did the
        // answer move" and never used as a key.
        var hash = new HashCode();
        hash.Add(holds?.Length ?? 0);

        return hash.ToHashCode();
    }

    /// <summary>Whether two sets of verdicts agree.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they agree.</returns>
    public static bool operator ==(ContainerVerdicts left, ContainerVerdicts right) => left.Equals(right);

    /// <summary>Whether two sets of verdicts disagree.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they disagree.</returns>
    public static bool operator !=(ContainerVerdicts left, ContainerVerdicts right) => !left.Equals(right);
}

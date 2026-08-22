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
        interned[key] = groups.Count - 1;
        Revision++;

        return groups.Count - 1;
    }

    /// <summary>Forgets every group, as a reload does.</summary>
    public void Reset() {
        groups.RemoveRange(1, groups.Count - 1);
        interned.Clear();
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

            if (!TryResolve(chain, group.Name, out var box)) {
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
    /// <param name="box">Receives its box.</param>
    /// <returns>Whether there is one.</returns>
    /// <remarks>
    ///     ⚠ <b>Nearest wins, and an unnamed query does not skip a named container.</b> A name is a
    ///     label a container carries, not a category it belongs to, so <c>@container (min-width: …)</c>
    ///     asks whatever box is closest whether or not that box was given a name. Skipping named ones
    ///     would make adding a name to a container silently retarget every unnamed query below it.
    /// </remarks>
    static bool TryResolve(IReadOnlyList<ContainerScope> chain, string name, out ContainerBox box) {
        for (var i = 0; i < chain.Count; i++) {
            var candidate = chain[i];

            if (candidate.Box.Kind == ContainerKind.Normal) {
                continue;
            }

            if (name.Length != 0 && !string.Equals(candidate.Name, name, StringComparison.Ordinal)) {
                continue;
            }

            box = candidate.Box;
            return true;
        }

        box = default;
        return false;
    }

    readonly record struct Group(int Within, string Name, string Condition);
}

/// <summary>One container in an element's ancestry.</summary>
/// <param name="Name">Its <c>container-name</c>, or empty.</param>
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

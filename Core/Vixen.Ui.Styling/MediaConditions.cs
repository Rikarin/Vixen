// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>The conditional groups a stylesheet declared, as a tree of conjunctions.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>What replaced deciding <c>@media</c> at load, and the reason two windows of one
///         document can now answer <c>max-width</c> differently.</b> The loader used to evaluate a
///         condition and either emit the block's rules or drop them, which put the answer <i>in</i>
///         the rule set — and a rule set is shared by every surface of a document, because that is
///         what keeps one theme across a torn-off window. So the question could only be asked once,
///         and it was asked of the primary surface.
///     </para>
///     <para>
///         Now the block's rules are always emitted, each tagged with the group it came from, and
///         the verdict is a <see cref="MediaVerdicts" /> a surface carries. The cost is one integer
///         test per candidate rule that is <i>in</i> a group — <see cref="Unconditional" /> is
///         checked first and is what every rule in every stylesheet this repository ships is — and
///         the saving is the whole of <c>StyleEngine.Reload</c> on a resize: crossing a breakpoint
///         was measured at 42 ms of ExCSS re-parsing for the editor's twelve sheets, per surface,
///         and it also restarted every fade in the window.
///     </para>
///     <para>
///         ⚠ <b>A group is a conjunction and it is stored as a link to its parent rather than as a
///         flattened condition.</b> CSS Conditional 5 § 3 lets a conditional group rule contain
///         another and the conditions conjoin, so <c>@media (min-width: 640px) { @media
///         (min-width: 900px) { … } }</c> is two questions, not one — and 700 px answers them
///         differently. Flattening to the outermost or to the innermost passes two of the three
///         widths and fails the middle one. The parent link keeps both questions and evaluates them
///         in order, which is also what makes an inner condition inside a false outer one cost one
///         boolean test rather than an evaluation.
///     </para>
///     <para>
///         ⚠ <b>A condition that cannot be read never becomes a group.</b>
///         <see cref="MediaQuery.TryEvaluate" /> fails only on the <i>text</i> — every one of its
///         refusals names a feature, a value or a length it could not parse, and none of them
///         consults the context it was handed — so unreadability is decided once, at load, where the
///         diagnostic already has somewhere to go. Deferring it would mean a refusal per surface per
///         evaluation, arriving in a list nothing drains.
///     </para>
/// </remarks>
public sealed class MediaConditions {
    /// <summary>The group a rule outside every <c>@media</c> belongs to, which always holds.</summary>
    public const int Unconditional = 0;

    readonly List<Group> groups = [new(-1, string.Empty)];
    readonly Dictionary<Group, int> interned = [];

    /// <summary>How many groups there are, the unconditional one included.</summary>
    public int Count => groups.Count;

    /// <summary>Bumped whenever a group is added, so a cached evaluation can tell it is stale.</summary>
    /// <remarks>
    ///     A counter rather than an event. The table is rebuilt from scratch by
    ///     <c>StyleEngine.Reload</c> and grows during a load, and the thing that caches verdicts is
    ///     asked for them far more often than they change — so "is what I computed still about this
    ///     table" wants to be an integer compare.
    /// </remarks>
    public int Revision { get; private set; }

    /// <summary>Registers a conditional group, or finds the one already registered.</summary>
    /// <param name="within">The group this one is nested in, or <see cref="Unconditional" />.</param>
    /// <param name="condition">The text between <c>@media</c> and the block.</param>
    /// <returns>The group's id, which a rule carries.</returns>
    /// <remarks>
    ///     ⚠ <b>Interned on the pair, which is what keeps a generated utility sheet from growing a
    ///     group per class.</b> One theme's <c>md:</c> utilities repeat <c>(min-width: 768px)</c>
    ///     once per class in the project — hundreds of copies of one question — and a group each
    ///     would be hundreds of identical evaluations on every resize and a wider rule tag.
    /// </remarks>
    public int Register(int within, string? condition) {
        ArgumentOutOfRangeException.ThrowIfNegative(within);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(within, groups.Count);

        var key = new Group(within, condition ?? string.Empty);

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

    /// <summary>The condition text a group was registered with.</summary>
    /// <param name="group">The group.</param>
    /// <returns>Its own condition, without its enclosing groups'.</returns>
    public string ConditionOf(int group) => groups[group].Condition;

    /// <summary>The group a group is nested in, or -1 for <see cref="Unconditional" />.</summary>
    /// <param name="group">The group.</param>
    /// <returns>Its enclosing group.</returns>
    public int EnclosingOf(int group) => groups[group].Within;

    /// <summary>Asks every group whether it holds on a surface.</summary>
    /// <param name="context">What the surface is.</param>
    /// <returns>The verdicts, indexed by group.</returns>
    /// <remarks>
    ///     ⚠ <b>Ascending, and that is not an ordering convenience — it is what makes the conjunction
    ///     work in one pass.</b> <see cref="Register" /> can only nest a group inside one that
    ///     already exists, so a group's enclosing group always has a lower id and has already been
    ///     answered by the time this reaches it.
    /// </remarks>
    public MediaVerdicts Evaluate(MediaContext context) {
        var holds = new bool[groups.Count];
        holds[Unconditional] = true;

        for (var i = 1; i < groups.Count; i++) {
            var group = groups[i];

            // The enclosing group first, so a condition sealed behind a false one is never asked.
            // That is the same saving the loader used to get from not recursing, kept.
            holds[i] = holds[group.Within]
                && MediaQuery.TryEvaluate(group.Condition, context, out var matches, out _)
                && matches;
        }

        return new MediaVerdicts(holds, Revision);
    }

    readonly record struct Group(int Within, string Condition);
}

/// <summary>Which conditional groups hold on one surface.</summary>
/// <remarks>
///     ⚠ <b><c>default</c> is "only the unconditional group", and that is the conservative answer
///     rather than an empty one.</b> A scope nobody has evaluated shows the rules that were never in
///     a <c>@media</c> at all, which is every rule in every stylesheet this repository ships — so the
///     failure mode of forgetting to evaluate is a document that ignores its <c>@media</c> blocks,
///     not one that applies all of them at once.
/// </remarks>
public readonly struct MediaVerdicts : IEquatable<MediaVerdicts> {
    readonly bool[]? holds;

    internal MediaVerdicts(bool[] holds, int revision) {
        this.holds = holds;
        Revision = revision;
    }

    /// <summary>Which <see cref="MediaConditions.Revision" /> these were computed against.</summary>
    public int Revision { get; }

    /// <summary>Whether a group's rules apply here.</summary>
    /// <param name="group">The group a rule carries.</param>
    /// <returns>Whether every condition in its stack holds.</returns>
    public bool Holds(int group) =>
        group == MediaConditions.Unconditional
        || (holds is not null && (uint) group < (uint) holds.Length && holds[group]);

    /// <inheritdoc />
    public bool Equals(MediaVerdicts other) {
        if (ReferenceEquals(holds, other.holds)) {
            return true;
        }

        if (holds is null || other.holds is null) {
            // One of them is the conservative default. They agree only if the other says the same,
            // which for an evaluated set means nothing but the unconditional group held.
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
    public override bool Equals(object? obj) => obj is MediaVerdicts other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() {
        // Deliberately coarse. These are compared for "did the answer move", never used as a key,
        // and hashing the whole vector would walk it on every comparison that a reference test or a
        // length mismatch was about to settle in one instruction.
        var hash = new HashCode();
        hash.Add(holds?.Length ?? 0);

        return hash.ToHashCode();
    }

    /// <summary>Whether two sets of verdicts agree.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they agree.</returns>
    public static bool operator ==(MediaVerdicts left, MediaVerdicts right) => left.Equals(right);

    /// <summary>Whether two sets of verdicts disagree.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they disagree.</returns>
    public static bool operator !=(MediaVerdicts left, MediaVerdicts right) => !left.Equals(right);
}

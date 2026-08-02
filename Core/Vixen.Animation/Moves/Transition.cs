// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Animation.Moves;

/// <summary>How the incoming move's phase is chosen when a transition starts.</summary>
public enum SyncMode {
    /// <summary>It starts where it starts. Right for a gesture, wrong for a cycle.</summary>
    None,

    /// <summary>
    ///     It continues the outgoing move's fraction, so a cycle carries across the change.
    /// </summary>
    Phase,

    /// <summary>
    ///     Its nearest footfall is aligned to the outgoing move's, so contacts line up rather than
    ///     fractions.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The one that matters, and the reason a move carries a foot phase at all.</b> A walk
    ///     that plants at 0 and 0.5 and a carry cycle that plants at 0.25 and 0.75 are aligned by
    ///     <see cref="Phase" /> and visibly wrong: the fractions match and the feet do not. Contacts
    ///     are what an eye reads, so contacts are what this matches.
    /// </remarks>
    ClosestFoot
}

/// <summary>How a transition is shaped over its duration.</summary>
public enum BlendEasing {
    /// <summary>Straight. Correct for a short crossfade and cheapest.</summary>
    Linear,

    /// <summary>Slow at both ends. What a longer, more deliberate change wants.</summary>
    SmoothStep
}

/// <summary>What happens between two moves.</summary>
/// <param name="Duration">How long the crossfade takes, in seconds. Zero is a cut.</param>
/// <param name="Easing">Its shape.</param>
/// <param name="Sync">How the incoming move's phase is chosen.</param>
/// <param name="Mask">
///     Which joints cross over, or <see langword="null" /> for all of them. A mask is what makes an
///     upper body change instantly while the legs take their time.
/// </param>
/// <param name="Allowed">Whether the transition may happen at all.</param>
public readonly record struct TransitionSpec(
    float Duration,
    BlendEasing Easing = BlendEasing.Linear,
    SyncMode Sync = SyncMode.Phase,
    BoneMask? Mask = null,
    bool Allowed = true
) {
    /// <summary>The transition taken when nothing else matches.</summary>
    public static TransitionSpec Default => new(0.25f);

    /// <summary>A transition that may not be taken.</summary>
    public static TransitionSpec Forbidden => new(0f, Allowed: false);

    /// <summary>The blend weight partway through.</summary>
    /// <param name="elapsed">How long the transition has been running, in seconds.</param>
    /// <returns>How much of the incoming move to use, in <c>[0, 1]</c>.</returns>
    public float WeightAt(float elapsed) {
        if (Duration <= 0f) {
            return 1f;
        }

        var t = MathUtil.Saturate(elapsed / Duration);
        return Easing is BlendEasing.SmoothStep ? t * t * (3f - (2f * t)) : t;
    }
}

/// <summary>A test over a move's facets, with wildcards.</summary>
/// <remarks>
///     ⚠ <b>An empty predicate matches everything</b>, which is what makes the last rule in a list a
///     default rather than a special case the evaluator has to know about.
/// </remarks>
public readonly record struct FacetPredicate(FacetSet Facets) {
    /// <summary>The predicate that matches any move.</summary>
    public static FacetPredicate Any => new(FacetSet.Empty);

    /// <summary>A predicate over <c>key=value</c> pairs.</summary>
    /// <param name="pairs">What a move must say.</param>
    /// <returns>The predicate.</returns>
    public static FacetPredicate Of(params ReadOnlySpan<(string Key, string Value)> pairs) =>
        new(FacetSet.Of(pairs));

    /// <summary>Whether a move matches.</summary>
    /// <param name="entry">The move, or <see langword="null" /> for "nothing was playing".</param>
    /// <returns>Whether it does.</returns>
    /// <remarks>
    ///     <b>Nothing playing matches only the wildcard.</b> The first move a character makes has no
    ///     outgoing side, and a rule naming what it comes <i>from</i> cannot be about that.
    /// </remarks>
    public bool Matches(MoveEntry? entry) =>
        Facets.Count == 0 || (entry is not null && entry.Facets.ContainsAll(Facets));
}

/// <summary>One line of the transition table: what it applies to, and what happens.</summary>
/// <param name="From">What the outgoing move must say.</param>
/// <param name="To">What the incoming move must say.</param>
/// <param name="Spec">What happens.</param>
public readonly record struct TransitionRule(FacetPredicate From, FacetPredicate To, TransitionSpec Spec);

/// <summary>How a transition is decided. The policy behind the rules.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This exists because <see cref="RuleTransitionPolicy" /> was the one policy in the
///         design reachable without an interface.</b> Everything else — arbitration, scheduling,
///         selection, scoring — is behind a seam, and the plan says plainly that a default nobody is
///         forced through rots. Transitions were the exception by omission rather than by argument:
///         the rules were called data as though that settled it. <i>Which rule matched</i> is data;
///         <i>first match wins</i> is a policy, and a project whose transitions are decided some
///         other way — a pairwise table, a learned model, a question put to its combat system — had
///         no way in.
///     </para>
/// </remarks>
public interface ITransitionPolicy {
    /// <summary>Decides what happens between two moves.</summary>
    /// <param name="from">What is playing, or <see langword="null" /> if nothing is.</param>
    /// <param name="into">What is about to play.</param>
    /// <param name="spec">What happens.</param>
    /// <returns>Whether the transition is permitted.</returns>
    bool TryResolve(MoveEntry? from, MoveEntry into, out TransitionSpec spec);
}

/// <summary>The shipped policy: an ordered list of rules, first match wins.</summary>
/// <remarks>
///     <para>
///         <b>A rule list rather than a table of pairs.</b> A pairwise table over N moves is N² cells
///         that nobody fills in; a rule list is a dozen sentences somebody can read:
///     </para>
///     <code>
///     run → *          : 0.20 s, phase-synced
///     *   → stop_*     : 0.12 s
///     injured:* → *    : 0.35 s      # everything an injured character does starts slowly
///     *   → *          : 0.25 s      # default
///     </code>
///     <para>
///         Same first-match-wins shape as a VCSS selector, and the authoring tool shows which rule
///         matched for a chosen pair the way a style inspector does.
///     </para>
/// </remarks>
public sealed class RuleTransitionPolicy : ITransitionPolicy {
    readonly TransitionRule[] rules;

    /// <summary>Creates a policy from an ordered list of rules.</summary>
    /// <param name="rules">The rules, most specific first.</param>
    public RuleTransitionPolicy(params ReadOnlySpan<TransitionRule> rules) => this.rules = rules.ToArray();

    /// <summary>A policy with no rules, which crossfades everything over a quarter of a second.</summary>
    public static RuleTransitionPolicy Shared { get; } = new();

    /// <summary>The rules, in order.</summary>
    /// <returns>The rules.</returns>
    public ReadOnlySpan<TransitionRule> Rules => rules;

    /// <inheritdoc />
    public bool TryResolve(MoveEntry? from, MoveEntry into, out TransitionSpec spec) {
        ArgumentNullException.ThrowIfNull(into);

        foreach (var rule in rules) {
            if (!rule.From.Matches(from) || !rule.To.Matches(into)) {
                continue;
            }

            spec = rule.Spec;
            return spec.Allowed;
        }

        spec = TransitionSpec.Default;
        return true;
    }

    /// <summary>Which rule decides a pair, for an editor that has to explain itself.</summary>
    /// <param name="from">The outgoing move, or <see langword="null" />.</param>
    /// <param name="into">The incoming move.</param>
    /// <returns>The rule's index, or −1 when the default applies.</returns>
    /// <remarks>
    ///     <b>Not a debug aid.</b> "Why did this transition take 0.35 s?" is unanswerable in a rule
    ///     list without it, and an unanswerable authoring question is how a project ends up back at a
    ///     pairwise table.
    /// </remarks>
    public int RuleFor(MoveEntry? from, MoveEntry into) {
        ArgumentNullException.ThrowIfNull(into);

        for (var index = 0; index < rules.Length; index++) {
            if (rules[index].From.Matches(from) && rules[index].To.Matches(into)) {
                return index;
            }
        }

        return -1;
    }
}

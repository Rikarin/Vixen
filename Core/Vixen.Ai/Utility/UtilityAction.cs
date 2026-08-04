// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ai;

/// <summary>One normalised input put through one curve.</summary>
/// <param name="Name">What it is called, for the editor and the debug record.</param>
/// <param name="Input">Where its number comes from.</param>
/// <param name="Curve">The shape it goes through.</param>
/// <remarks>
///     doc 37 § D8's axis. An action is a list of these and nothing else — there is no condition, no
///     priority and no ordering among them, because <see cref="UtilityScoring" /> makes the count
///     irrelevant and a zero makes any one of them a veto.
/// </remarks>
public readonly record struct UtilityConsideration(Symbol Name, IUtilityInput Input, IResponseCurve Curve) {
    /// <summary>Reads the world and puts it through the curve.</summary>
    /// <param name="context">The agent.</param>
    /// <returns>Its score, in <c>[0,1]</c>.</returns>
    public float Score(in AgentContext context) => Curve.Evaluate(Input.Read(in context));
}

/// <summary>How a set of considerations becomes one number.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A weighted geometric mean, and the naive product is what everybody writes first.</b>
///         With every term in <c>[0,1]</c>, a plain product makes an action with six considerations
///         <i>structurally</i> worse than an identical action with three — so adding a consideration
///         to tune an action quietly demotes it, and the demotion is invisible because every
///         individual number looks right. Taking the <c>n</c>th root is the standard compensation and
///         it makes the count irrelevant.
///     </para>
///     <para>
///         ⚠ <b>The zero rule survives the mean, and that is the point of using a product at all.</b>
///         A single zero factor makes the whole thing zero, which is how "never, under any
///         circumstances" is expressed. A weighted <i>sum</i> cannot say that: a veto is outvoted by
///         enough enthusiasm elsewhere, which is how an agent ends up drinking coffee while on fire.
///     </para>
/// </remarks>
public static class UtilityScoring {
    /// <summary>Combines a set of consideration scores.</summary>
    /// <param name="scores">Each consideration's score, in <c>[0,1]</c>.</param>
    /// <param name="weight">The action's bucket multiplier.</param>
    /// <returns>The action's score.</returns>
    /// <remarks>
    ///     <para>
    ///         An empty list scores <paramref name="weight" />: an action with no considerations is one
    ///         that is always as good as its weight says, which is what a fallback wants to be.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It forwards to <see cref="CandidateScoring.Combine" /> and does not repeat it.</b>
    ///         doc 37 § D14 says a utility set and an environment query are the same machine; the way
    ///         to make that false over time is to have two copies of the mean that agree today.
    ///     </para>
    /// </remarks>
    public static float Combine(ReadOnlySpan<float> scores, float weight = 1f) =>
        CandidateScoring.Combine(scores, weight);
}

/// <summary>One thing an agent might do, and how good it would be.</summary>
/// <remarks>
///     <para>
///         doc 37 § D2's whole point: what this chooses is an <see cref="IAgentAction" />, named by
///         index the way a behaviour-tree task is. A project writes <c>MoveToTask</c> once and gets it
///         in a tree, in a utility set and in a GOAP plan.
///     </para>
///     <para>
///         <see cref="Weight" /> is the bucket — 1 for ambient, 2–3 for important, 5 for emergency —
///         and it is a multiplier rather than a hard ordering. ⚠ A hard ordering means an emergency
///         action with one zero-scoring consideration <b>blocks everything below it</b>; a multiplier
///         degrades instead.
///     </para>
/// </remarks>
public sealed class UtilityAction {
    /// <summary>Creates an action.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="action">Its index in the world's <c>AgentActionRegistry</c>.</param>
    /// <param name="considerations">What decides how good it is.</param>
    /// <exception cref="ArgumentNullException"><paramref name="considerations" /> is null.</exception>
    public UtilityAction(Symbol name, ushort action, params UtilityConsideration[] considerations) {
        ArgumentNullException.ThrowIfNull(considerations);

        Name = name;
        Action = action;
        Considerations = considerations;
    }

    /// <summary>What it is called.</summary>
    public Symbol Name { get; }

    /// <summary>Which action it runs.</summary>
    public ushort Action { get; }

    /// <summary>What decides how good it is.</summary>
    public UtilityConsideration[] Considerations { get; }

    /// <summary>Its multiplier. 1 for ambient, 2–3 for important, 5 for emergency.</summary>
    public float Weight { get; init; } = 1f;

    /// <summary>How long after it ends before it may be chosen again, in seconds.</summary>
    public float Cooldown { get; init; }

    /// <summary>Which group it is in. <b>Higher wins</b>, under <c>UtilitySelectors.Bucketed</c>.</summary>
    /// <remarks>
    ///     Ignored by every other selector, and zero is a perfectly good answer for all of them. Under
    ///     the bucketed one it is a rank: the highest bucket with <i>anything</i> scoring above zero
    ///     wins outright, and only then is the best inside it chosen.
    /// </remarks>
    public int Bucket { get; init; }

    /// <summary>How good it would be right now.</summary>
    /// <param name="context">The agent.</param>
    /// <param name="detail">Where to put each consideration's own score, or empty for none.</param>
    /// <returns>The score.</returns>
    /// <remarks>
    ///     ⚠ <b>It stops at the first zero unless somebody asked for the detail.</b> A veto makes the
    ///     rest of the list irrelevant, and the rest of the list is where the reads of the world are —
    ///     so an agent whose emergency action is vetoed does not pay for its four other inputs. The
    ///     editor and the debug overlay pass a span and get every number, because "why is this
    ///     scoring zero" is the question they exist to answer.
    /// </remarks>
    public float Score(in AgentContext context, Span<float> detail = default) {
        var factors = new Factors(Considerations, in context);

        return CandidateScoring.Score(in factors, Weight, detail);
    }

    /// <summary>This action's considerations, as the shared scorer reads them.</summary>
    /// <remarks>
    ///     ⚠ <b>A <c>ref struct</c> holding the context by reference, so scoring an action copies
    ///     nothing.</b> It exists so that an action and an environment-query point go through
    ///     <see cref="CandidateScoring.Score" /> — one implementation of the mean and the veto — rather
    ///     than through two loops that agree today; doc 37 § D14 is only true if it is checkable.
    /// </remarks>
    readonly ref struct Factors(UtilityConsideration[] considerations, ref readonly AgentContext context)
        : IFactorSource {
        readonly ref readonly AgentContext context = ref context;

        /// <inheritdoc />
        public int Count => considerations.Length;

        /// <inheritdoc />
        public float Factor(int index) => considerations[index].Score(in context);
    }
}

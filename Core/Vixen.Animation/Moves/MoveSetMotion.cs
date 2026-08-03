// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Motions;

namespace Vixen.Animation.Moves;

/// <summary>A move set, playing. It is a <see cref="Motion" />, so everything above it is unchanged.</summary>
/// <remarks>
///     <para>
///         <b>The cheapest possible integration, and that is the point of the type.</b> A state holds
///         one of these exactly as it holds a clip or a blend tree, and every layer, mask, event and
///         root-motion path above it works with no change — because from up there this <i>is</i> a
///         motion. A game with no state machine at all can hold one directly, which is the common
///         case for a background character.
///     </para>
///     <para>
///         ⚠ <b>The query is re-asked only when it changes.</b> A selection pass is cheap and is not
///         free, and re-running it every frame would also mean re-deciding every frame — which is how
///         a character standing on the boundary between two equally good moves flickers between them.
///         The query is a value; comparing this frame's to last frame's is comparing a few words.
///     </para>
///     <para>
///         <b>Two moves are evaluated during a transition and blended, and the phase runs through
///         both.</b> Which phase the incoming move starts at is the <see cref="SyncMode" />'s
///         business, and the answer that matters is <see cref="SyncMode.ClosestFoot" /> — matching
///         contacts rather than fractions, because contacts are what an eye reads.
///     </para>
///     <para>
///         <b>It is also an <see cref="IPhaseSource" /></b>, so an upper-body set on a masked layer
///         above can hang off this one's cycle instead of free-running against it.
///     </para>
/// </remarks>
public sealed class MoveSetMotion : Motion, IPhaseSource {
    readonly IMoveSelector selector;
    readonly IMoveScorer scorer;
    readonly ITransitionPolicy transitions;

    MoveQuery current;
    bool asked;

    // Where the outgoing and incoming moves are sampled, relative to the layer's own normalised
    // time. A move brought in mid-cycle keeps its offset for as long as it plays, which is what
    // makes a phase carried across a transition stay carried.
    float outgoingOffset;
    float incomingOffset;

    TransitionSpec transition;
    float elapsed;
    float phase;

    /// <summary>Creates a motion over a set.</summary>
    /// <param name="set">The vocabulary.</param>
    /// <param name="selector">How a move is picked, or <see langword="null" /> for the shipped one.</param>
    /// <param name="scorer">How candidates rank, or <see langword="null" /> for the shipped one.</param>
    /// <param name="transitions">
    ///     How a change of move is shaped, or <see langword="null" /> for the shipped rule list.
    /// </param>
    public MoveSetMotion(
        MoveSet set,
        IMoveSelector? selector = null,
        IMoveScorer? scorer = null,
        ITransitionPolicy? transitions = null
    ) {
        ArgumentNullException.ThrowIfNull(set);

        Set = set;
        this.selector = selector ?? QueryMoveSelector.Shared;
        this.scorer = scorer ?? DefaultMoveScorer.Shared;
        this.transitions = transitions ?? RuleTransitionPolicy.Shared;
        Name = set.Name;
    }

    /// <summary>The vocabulary being chosen from.</summary>
    public MoveSet Set { get; }

    /// <summary>What is playing, or <see langword="null" /> before the first query.</summary>
    public MoveEntry? Current { get; private set; }

    /// <summary>What is blending out, or <see langword="null" /> when nothing is.</summary>
    public MoveEntry? Outgoing { get; private set; }

    /// <summary>What the last selection decided.</summary>
    public MoveSelection Selection { get; private set; } = MoveSelection.None;

    /// <summary>The rate the current move is playing at, from the retime.</summary>
    public float PlaybackRate => Selection.HasMove ? Selection.PlaybackRate : 1f;

    /// <summary>Where the cycle it is driven by comes from.</summary>
    /// <remarks>
    ///     Settable rather than constructor-only because the thing an upper body follows is the
    ///     lower body's motion, and both are built before either can be handed to the other.
    /// </remarks>
    public PhaseSource Phase { get; set; } = PhaseSource.Own;

    /// <summary>How far through the current transition, in <c>[0, 1]</c>. One when settled.</summary>
    public float TransitionWeight => Outgoing is null ? 1f : transition.WeightAt(elapsed);

    /// <summary>Asks for a move, re-selecting only if the question changed.</summary>
    /// <param name="query">What is wanted.</param>
    /// <returns>Whether the answer changed.</returns>
    /// <remarks>
    ///     Called by whatever owns the character's intent — a behaviour, a system, a state's own
    ///     update — rather than from inside <see cref="Evaluate" />, because evaluation happens once
    ///     per layer per frame and the question is asked once per character.
    /// </remarks>
    public bool Ask(in MoveQuery query) {
        if (asked && query.Equals(current)) {
            return false;
        }

        current = query;
        asked = true;

        var chosen = selector.Choose(Set, query, scorer);

        if (chosen.Index == Selection.Index) {
            // The same move, possibly at a different rate. Nothing to blend and nothing to re-phase:
            // re-entering a transition into the move already playing is how a character that keeps
            // asking the same question never finishes changing.
            Selection = chosen;
            return false;
        }

        var incoming = chosen.HasMove ? Set[chosen.Index] : null;

        if (incoming is not null && !transitions.TryResolve(Current, incoming, out transition)) {
            // Forbidden. The answer stands as it is rather than being forced through, which is what
            // a rule saying "not from here" is for.
            return false;
        }

        Begin(incoming);
        Selection = chosen;

        return true;
    }

    /// <summary>Forces the next <see cref="Ask" /> to re-select even if the query is unchanged.</summary>
    /// <remarks>
    ///     For the cases where the answer can change without the question doing so — the set was
    ///     rebuilt by a hot reload, or a game wants the repeat penalty applied to a move that has now
    ///     finished a pass.
    /// </remarks>
    public void Invalidate() => asked = false;

    /// <inheritdoc />
    public bool TryGetPhase(out float phase, out float footPhase) {
        if (Current is null) {
            phase = 0f;
            footPhase = float.NaN;

            return false;
        }

        phase = Wrap(this.phase + incomingOffset);
        footPhase = Current.Traits.FootPhase;

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Divided by the playback rate, for <c>ClipMotion</c>'s reason: a move retimed to 1.2× is
    ///     five sixths as long, and a tree blending it against something else has to agree.
    /// </remarks>
    public override float Length(AnimationParameters parameters) {
        if (Current is not { } entry) {
            return 1f;
        }

        var rate = MathF.Max(MathF.Abs(PlaybackRate), 1e-4f);
        return MathF.Max(entry.Motion.Length(parameters) / rate, 1e-4f);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The context's normalised time is passed through to each move plus that move's own
    ///         offset, and is not rescaled by the playback rate.</b> The retime is already in
    ///         <see cref="Length" /> and the layer advances against that, so applying it here as well
    ///         would square it and leave a character's feet moving at the wrong speed in a way that
    ///         looks like a content bug.
    ///     </para>
    ///     <para>
    ///         <b>Events come from the incoming move only, weighted by the blend.</b> Both clips
    ///         crossing a footstep key during a crossfade are both real, and the buffer already
    ///         carries a weight for a handler to filter on — but emitting the outgoing move's events
    ///         as well would double every footstep of every gait change, which is worse than missing
    ///         the last step of a move that is on its way out.
    ///     </para>
    /// </remarks>
    public override RootMotionDelta Evaluate(in MotionContext context, Span<BoneTransform> destination) {
        if (Current is not { } entry) {
            // A set with nothing selectable poses the bind pose rather than leaving the buffer as it
            // was. Whatever was in it belongs to another character or to last frame, and either is a
            // worse answer than a T-pose somebody will notice and report.
            destination.Clear();
            return RootMotionDelta.None;
        }

        Advance(context);

        var driven = Phase.Resolve(context.NormalizedTime, entry.Traits.FootPhase);
        var weight = TransitionWeight;

        if (Outgoing is not { } outgoing || weight >= 1f) {
            Settle();
            return entry.Motion.Evaluate(At(context, driven, incomingOffset, context.Weight), destination);
        }

        var motion = outgoing.Motion.Evaluate(
            At(context, driven, outgoingOffset, context.Weight * (1f - weight)),
            destination
        );

        using var lease = context.Scratch.Rent();

        var incoming = entry.Motion.Evaluate(
            At(context, driven, incomingOffset, context.Weight * weight),
            lease.Pose
        );

        if (transition.Mask is { } mask) {
            PoseBlend.LerpMasked(destination, destination, lease.Pose, mask, weight);
        } else {
            PoseBlend.Lerp(destination, destination, lease.Pose, weight);
        }

        return context.WantsRootMotion
            ? RootMotionDelta.Lerp(motion, incoming, weight)
            : RootMotionDelta.None;
    }

    /// <summary>Starts a transition into a move, choosing where it comes in.</summary>
    void Begin(MoveEntry? incoming) {
        var previous = Current;

        Outgoing = previous is not null && transition.Duration > 0f && incoming is not null ? previous : null;
        outgoingOffset = incomingOffset;
        elapsed = 0f;

        Current = incoming;

        if (incoming is null || previous is null) {
            incomingOffset = 0f;
            Outgoing = null;

            return;
        }

        incomingOffset = transition.Sync switch {
            // The outgoing move's contact is where the incoming move's contact should be, so the
            // offset is the difference between the two — not between the two phases, which is a fact
            // about how somebody trimmed the clips rather than about the character.
            SyncMode.ClosestFoot when !float.IsNaN(previous.Traits.FootPhase)
                && !float.IsNaN(incoming.Traits.FootPhase) =>
                Wrap(outgoingOffset - previous.Traits.FootPhase + incoming.Traits.FootPhase),

            SyncMode.Phase or SyncMode.ClosestFoot => outgoingOffset,
            _ => 0f
        };
    }

    void Advance(in MotionContext context) {
        phase = context.NormalizedTime;

        if (Outgoing is null) {
            return;
        }

        // The transition runs on wall time and the pose runs on normalised time, deliberately: how
        // long a crossfade takes is an authored duration in seconds, and a move retimed to 1.2× must
        // not also take five sixths as long to blend into.
        var step = context.NormalizedTime - context.PreviousNormalizedTime;

        if (step < 0f) {
            step += 1f;
        }

        elapsed += step * MathF.Max(Length(context.Parameters), 1e-4f);
    }

    void Settle() {
        Outgoing = null;
        elapsed = transition.Duration;
    }

    static MotionContext At(in MotionContext context, float driven, float offset, float weight) =>
        context with {
            NormalizedTime = Wrap(driven + offset),
            PreviousNormalizedTime = Wrap(context.PreviousNormalizedTime + offset),
            Weight = weight
        };

    static float Wrap(float phase) {
        var wrapped = phase % 1f;
        return wrapped < 0f ? wrapped + 1f : wrapped;
    }
}

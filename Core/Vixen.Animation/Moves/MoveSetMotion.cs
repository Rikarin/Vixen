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
///         ⚠ <b>What this does <i>not</i> do yet is blend.</b> A change of move takes effect at once.
///         Transitions — their durations, their curves, their masks, the phase carried across one —
///         are the next phase's, and the seam they arrive through is <c>ITransitionPolicy</c>.
///         Until then a set whose moves differ sharply will pop, and that is a known and scheduled
///         gap rather than a defect in the selection.
///     </para>
/// </remarks>
public sealed class MoveSetMotion : Motion {
    readonly IMoveSelector selector;
    readonly IMoveScorer scorer;

    MoveQuery current;
    bool asked;

    /// <summary>Creates a motion over a set.</summary>
    /// <param name="set">The vocabulary.</param>
    /// <param name="selector">How a move is picked, or <see langword="null" /> for the shipped one.</param>
    /// <param name="scorer">How candidates rank, or <see langword="null" /> for the shipped one.</param>
    public MoveSetMotion(MoveSet set, IMoveSelector? selector = null, IMoveScorer? scorer = null) {
        ArgumentNullException.ThrowIfNull(set);

        Set = set;
        this.selector = selector ?? QueryMoveSelector.Shared;
        this.scorer = scorer ?? DefaultMoveScorer.Shared;
        Name = set.Name;
    }

    /// <summary>The vocabulary being chosen from.</summary>
    public MoveSet Set { get; }

    /// <summary>What is playing, or <see langword="null" /> before the first query.</summary>
    public MoveEntry? Current { get; private set; }

    /// <summary>What the last selection decided.</summary>
    public MoveSelection Selection { get; private set; } = MoveSelection.None;

    /// <summary>The rate the current move is playing at, from the retime.</summary>
    public float PlaybackRate => Selection.HasMove ? Selection.PlaybackRate : 1f;

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
        var changed = chosen.Index != Selection.Index;

        Selection = chosen;
        Current = chosen.HasMove ? Set[chosen.Index] : null;

        return changed;
    }

    /// <summary>Forces the next <see cref="Ask" /> to re-select even if the query is unchanged.</summary>
    /// <remarks>
    ///     For the cases where the answer can change without the question doing so — the set was
    ///     rebuilt by a hot reload, or a game wants the repeat penalty applied to a move that has now
    ///     finished a pass.
    /// </remarks>
    public void Invalidate() => asked = false;

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
    ///     ⚠ <b>The context is passed through untouched, including its normalised time.</b> The
    ///     retime is already in <see cref="Length" />, and the layer above advances normalised time
    ///     against that length — so scaling the time here as well would apply the rate twice and
    ///     leave a character's feet moving at the square of the speed they should.
    /// </remarks>
    public override RootMotionDelta Evaluate(in MotionContext context, Span<BoneTransform> destination) {
        if (Current is not { } entry) {
            // A set with nothing selectable poses the bind pose rather than leaving the buffer as it
            // was. Whatever was in it belongs to another character or to last frame, and either is a
            // worse answer than a T-pose somebody will notice and report.
            destination.Clear();
            return RootMotionDelta.None;
        }

        return entry.Motion.Evaluate(context, destination);
    }
}

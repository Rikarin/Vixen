// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Motions;
using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.Tests;

/// <summary>A selector that answers from a table it was handed, ignoring the score entirely.</summary>
/// <remarks>
///     <para>
///         Doc 34's Part 4 names "a table-driven chooser" as a thing a project should be able to build
///         on <see cref="IMoveSelector" /> without forking, and this is that — deliberately as far
///         from <see cref="QueryMoveSelector" /> as the interface allows. It never calls the scorer,
///         never reads the numeric targets and never retimes; it looks the required facets up in a
///         dictionary and answers.
///     </para>
///     <para>
///         ⚠ <b>Which is the point of it being here.</b> An interface whose only implementation is the
///         default is an interface shaped like the default, and the way that shows up is a signature
///         that quietly assumes something only the default does — here, that the scorer is consulted
///         at all.
///     </para>
/// </remarks>
sealed class TableTestSelector : IMoveSelector {
    readonly Dictionary<string, string> table;

    public TableTestSelector(params (string Facets, string Move)[] rows) =>
        table = rows.ToDictionary(row => row.Facets, row => row.Move, StringComparer.Ordinal);

    public int ScorerCalls { get; private set; }

    public MoveSelection Choose(MoveSet moves, in MoveQuery query, IMoveScorer scorer) {
        ArgumentNullException.ThrowIfNull(moves);

        var key = string.Join(' ', query.Required.Facets.ToArray().Select(static facet => facet.ToString()));

        if (!table.TryGetValue(key, out var wanted)) {
            return MoveSelection.None;
        }

        for (var index = 0; index < moves.Count; index++) {
            if (string.Equals(moves[index].Name, wanted, StringComparison.Ordinal)) {
                return new(index, 1f, 1f);
            }
        }

        return MoveSelection.None;
    }
}

/// <summary>A scorer that prefers the move whose name sorts first, and nothing else.</summary>
/// <remarks>
///     Nonsense as a policy and exactly right as a second implementation: it proves that
///     <see cref="IMoveScorer" /> really is "rank a candidate" and not "the default's arithmetic with
///     a hook in it". A project's real second scorer adds a cooldown or a term its combat system
///     supplies; the shape is the same.
/// </remarks>
sealed class AlphabeticalTestScorer : IMoveScorer {
    public float Score(in MoveCandidate candidate, in MoveQuery query) =>
        -candidate.Entry.Name[0];
}

/// <summary>A gait model for something with wheels: it never turns on the spot and never strafes.</summary>
/// <remarks>
///     ⚠ <b>The second implementation the interface's own doc comment says should exist.</b> A
///     vehicle's speed is signed — reverse is a different clip and not a walk played backwards — and
///     its turn rate is a function of speed rather than an input of its own, which is precisely the
///     thing a biped model gets to ignore.
/// </remarks>
sealed class WheeledTestGaitModel(float wheelbase = 2.4f) : IGaitModel {
    public void Describe(in MoveState state, ref MoveTargets targets) {
        var forward = new Vector2(MathF.Sin(state.Facing), MathF.Cos(state.Facing));
        var along = Vector2.Dot(state.Velocity, forward);

        targets = targets with {
            // Signed: reversing is its own move, and a set that scored it as "slow forwards" would
            // pick a crawl and play it backwards.
            Speed = along,

            // A wheeled body cannot turn faster than its speed and its wheelbase allow, so the
            // animation target is the turn it is *able* to be making rather than the one asked for.
            TurnRate = wheelbase <= 0f ? 0f : Math.Clamp(state.TurnRate, -MathF.Abs(along) / wheelbase, MathF.Abs(along) / wheelbase)
        };
    }
}

/// <summary>A transition policy that asks a delegate, the way a game system would be asked.</summary>
/// <remarks>
///     Part 4 names "a policy that asks another game system" as the case the seam exists for — a
///     combat system that refuses to interrupt a committed swing, say. The shape that proves it is a
///     policy with no rules at all.
/// </remarks>
sealed class AskingTestPolicy(Func<MoveEntry?, MoveEntry, TransitionSpec?> ask) : ITransitionPolicy {
    public int Asked { get; private set; }

    public bool TryResolve(MoveEntry? from, MoveEntry into, out TransitionSpec spec) {
        Asked++;

        if (ask(from, into) is { } answer) {
            spec = answer;
            return answer.Allowed;
        }

        spec = TransitionSpec.Forbidden;
        return false;
    }
}

/// <summary>A motion that poses nothing, for a set whose clips are beside the point.</summary>
sealed class StillTestMotion : Motion {
    public static StillTestMotion Shared { get; } = new();

    public override float Length(AnimationParameters parameters) => 1f;

    public override RootMotionDelta Evaluate(in MotionContext context, Span<BoneTransform> destination) =>
        RootMotionDelta.None;
}

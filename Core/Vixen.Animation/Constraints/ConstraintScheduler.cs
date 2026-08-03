// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Animation.Constraints;

/// <summary>Characters that have to be solved together, because their goals refer to each other.</summary>
/// <remarks>
///     A window over the system's own array rather than a list of its own, so planning a frame's
///     groups allocates nothing. A group of one — which is what the default plans — is the ordinary
///     per-character solve, and nothing about it is special-cased.
/// </remarks>
public readonly struct ConstraintGroup {
    readonly ConstraintStack[]? members;
    readonly int start;

    internal ConstraintGroup(ConstraintStack[] members, int start, int count) {
        this.members = members;
        this.start = start;
        Count = count;
    }

    /// <summary>How many characters are in it.</summary>
    public int Count { get; }

    /// <summary>One of them.</summary>
    /// <param name="index">Which.</param>
    /// <returns>The stack.</returns>
    public ConstraintStack this[int index] =>
        members is null || (uint)index >= (uint)Count
            ? throw new ArgumentOutOfRangeException(nameof(index))
            : members[start + index];
}

/// <summary>Where a scheduler puts the groups it decided on.</summary>
/// <remarks>
///     A sink rather than a returned list, so a scheduler that decides one group per character
///     allocates nothing at all and one that discovers a dependency graph allocates only what its own
///     bookkeeping needs.
/// </remarks>
public interface IConstraintGroupSink {
    /// <summary>Declares that these characters are solved together, in this order.</summary>
    /// <param name="members">The characters. Copied; the span need not outlive the call.</param>
    void Add(ReadOnlySpan<ConstraintStack> members);
}

/// <summary>What is solved together, and when in the frame.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two entry points, because a one-stage seam could not host what this is for.</b> Two
///         characters whose goals reference each other cannot be solved independently without one of
///         them seeing last frame's pose of the other. The pose stage runs inside
///         <see cref="IPoseProcessor" />, which is <em>after</em> each animator has mixed its layers —
///         so a group solved there is a group of finished poses, every one of which was blended
///         against a stale view of the others. Grouping alone was the right idea at the wrong point
///         in the frame.
///     </para>
///     <para>
///         So <see cref="PlanPreEvaluation" /> runs before any animator evaluates. A group planned
///         there can solve, publish to <see cref="ConstraintStack.Published" />, and have every
///         member's own evaluation read that instead of reaching for a neighbour's live pose.
///     </para>
///     <para>
///         <b>The default plans nothing before evaluation</b>, so the whole stage costs a virtual
///         call on an empty span — which is the point of it shipping now rather than later. It is
///         here because it decides <see cref="ConstraintStack" />'s shape, and deciding afterwards
///         would mean opening up a type everything else already depends on.
///     </para>
/// </remarks>
public interface IConstraintScheduler {
    /// <summary>Decides what to solve before any character has been evaluated.</summary>
    /// <param name="stacks">Every constraint stack in the world, in no particular order.</param>
    /// <param name="sink">Where the groups go.</param>
    void PlanPreEvaluation(ReadOnlySpan<ConstraintStack> stacks, IConstraintGroupSink sink);

    /// <summary>Decides what to solve once every character has a pose.</summary>
    /// <param name="stacks">Every constraint stack in the world, in no particular order.</param>
    /// <param name="sink">Where the groups go.</param>
    /// <remarks>
    ///     A stack that no group claims solves itself in the animator's own processor pass, which is
    ///     the ordinary path and the only one a game without a scheduler ever takes.
    /// </remarks>
    void PlanPose(ReadOnlySpan<ConstraintStack> stacks, IConstraintGroupSink sink);
}

/// <summary>The shipped scheduler: nothing before, everybody on their own after.</summary>
/// <remarks>
///     Planning no pose groups is not the same as planning one group per character — it is what tells
///     <c>AnimationSystem</c> to leave every stack to solve itself inside its animator's own
///     processor pass, where it already runs across the job scheduler with everything else that
///     animator does. Claiming each one into a group of its own would move the same work onto a
///     second parallel pass for no gain.
/// </remarks>
public sealed class DefaultConstraintScheduler : IConstraintScheduler {
    /// <summary>The one every system uses unless it is given another.</summary>
    public static DefaultConstraintScheduler Shared { get; } = new();

    /// <inheritdoc />
    public void PlanPreEvaluation(ReadOnlySpan<ConstraintStack> stacks, IConstraintGroupSink sink) {
    }

    /// <inheritdoc />
    public void PlanPose(ReadOnlySpan<ConstraintStack> stacks, IConstraintGroupSink sink) {
    }
}

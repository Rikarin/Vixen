// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Motions;

namespace Vixen.Animation.StateMachine;

/// <summary>One state of a layer's graph: something to play, and the ways out of it.</summary>
/// <remarks>
///     A state is a name, a <see cref="Motion" /> and a list of transitions — and the motion is why
///     the type is this small. Whether the state plays one clip or a two-dimensional locomotion tree
///     is the motion's business, so "idle" and "the whole of ground movement" are the same kind of
///     thing and the graph does not grow a node type for each.
/// </remarks>
public sealed class AnimationState {
    readonly List<AnimationTransition> transitions = [];

    /// <summary>Creates a state.</summary>
    /// <param name="name">What it is called. Events fired from it are attributed to this.</param>
    /// <param name="motion">What it plays.</param>
    public AnimationState(string name, Motion motion) {
        ArgumentNullException.ThrowIfNull(motion);

        Name = name;
        Motion = motion;
        Index = -1;
    }

    /// <summary>What it is called.</summary>
    public string Name { get; }

    /// <summary>What it plays.</summary>
    public Motion Motion { get; }

    /// <summary>How fast, relative to the motion's own length. May be negative to play backwards.</summary>
    public float Speed { get; init; } = 1f;

    /// <summary>What happens when the motion reaches its end.</summary>
    public WrapMode Wrap { get; init; } = WrapMode.Loop;

    /// <summary>The ways out, tried in order.</summary>
    /// <remarks>
    ///     In order, and the first that fires wins. Priority is authoring order rather than a
    ///     separate number, because a number is one more thing to keep consistent and because "move
    ///     it up the list" is what a person means when they say one transition should win.
    /// </remarks>
    public IReadOnlyList<AnimationTransition> Transitions => transitions;

    /// <summary>Where this state sits in its machine, or −1 before it is added to one.</summary>
    public int Index { get; internal set; }

    /// <summary>Adds a way out.</summary>
    /// <param name="transition">The transition.</param>
    /// <returns>The state, for chaining.</returns>
    public AnimationState AddTransition(AnimationTransition transition) {
        ArgumentNullException.ThrowIfNull(transition);

        transitions.Add(transition);
        return this;
    }

    /// <summary>Adds a way out, and returns it so conditions can be added to it.</summary>
    /// <param name="destination">Where it goes.</param>
    /// <param name="duration">How long the crossfade takes, in seconds.</param>
    /// <returns>The new transition.</returns>
    public AnimationTransition TransitionTo(AnimationState destination, float duration = 0.2f) {
        var transition = new AnimationTransition(destination, duration);
        transitions.Add(transition);

        return transition;
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}

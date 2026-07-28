// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Animation.StateMachine;

/// <summary>
///     A layer's graph: the states, which one it starts in, and the transitions that can fire from
///     anywhere.
/// </summary>
/// <remarks>
///     <para>
///         The definition, not the playback. It is immutable once built and is shared by every
///         character running it; where a particular character is in it lives in
///         <see cref="StateMachineInstance" />.
///     </para>
///     <para>
///         <b>Any-state transitions are separate and are tried first.</b> "Die from anywhere",
///         "get hit from anywhere" — the alternative is the same transition copied onto forty
///         states, which is forty places for it to be forgotten when a forty-first is added.
///         They are tried before the current state's own, because a transition that is supposed to
///         fire from anywhere is not supposed to lose to a local one.
///     </para>
///     <para>
///         <b>No sub-state machines.</b> They are a layout convenience in an editor and they change
///         nothing about evaluation: a sub-machine's states are states, and its entry and exit nodes
///         are transitions. The editor (<c>Vixen.Editor.AnimationGraph</c>, doc 14 Phase 8) is where
///         the grouping belongs, and it flattens on the way out.
///     </para>
/// </remarks>
public sealed class AnimationStateMachine {
    readonly AnimationState[] states;
    readonly List<AnimationTransition> anyState = [];

    /// <summary>Builds a machine from its states.</summary>
    /// <param name="states">The states. The first is the default unless one is named.</param>
    /// <param name="defaultState">Which one to start in, or <see langword="null" /> for the first.</param>
    /// <exception cref="ArgumentException">
    ///     There are no states, a state appears twice, or a transition leaves the machine.
    /// </exception>
    public AnimationStateMachine(IEnumerable<AnimationState> states, AnimationState? defaultState = null) {
        ArgumentNullException.ThrowIfNull(states);

        this.states = [.. states];

        if (this.states.Length == 0) {
            throw new ArgumentException("A state machine needs at least one state.", nameof(states));
        }

        for (var index = 0; index < this.states.Length; index++) {
            var state = this.states[index];

            if (state.Index >= 0) {
                throw new ArgumentException(
                    $"State '{state.Name}' is already in a state machine. States belong to one graph.",
                    nameof(states)
                );
            }

            state.Index = index;
        }

        DefaultState = defaultState?.Index ?? 0;

        if (DefaultState < 0 || DefaultState >= this.states.Length) {
            throw new ArgumentException("The default state is not in this machine.", nameof(defaultState));
        }
    }

    /// <summary>The states, in the order they were given.</summary>
    public ReadOnlySpan<AnimationState> States => states;

    /// <summary>Which state playback starts in.</summary>
    public int DefaultState { get; }

    /// <summary>Transitions that may fire whatever the current state is.</summary>
    public IReadOnlyList<AnimationTransition> AnyStateTransitions => anyState;

    /// <summary>Adds a transition that may fire from any state.</summary>
    /// <param name="destination">Where it goes.</param>
    /// <param name="duration">How long the crossfade takes, in seconds.</param>
    /// <returns>The new transition, so conditions can be added to it.</returns>
    public AnimationTransition TransitionFromAnyState(AnimationState destination, float duration = 0.2f) {
        var transition = new AnimationTransition(destination, duration);
        anyState.Add(transition);

        return transition;
    }

    /// <summary>The index of a state by name, or −1.</summary>
    /// <param name="name">The state's name.</param>
    /// <returns>Its index, or −1.</returns>
    /// <remarks>
    ///     Linear, and deliberately not a dictionary. It is called by <c>Play("Idle")</c> from game
    ///     code and by an editor, both of which happen at human rates over graphs of a few dozen
    ///     states; the graph's own evaluation never looks a state up by name.
    /// </remarks>
    public int IndexOf(string name) {
        for (var index = 0; index < states.Length; index++) {
            if (string.Equals(states[index].Name, name, StringComparison.Ordinal)) {
                return index;
            }
        }

        return -1;
    }

    /// <summary>A state by index.</summary>
    /// <param name="index">Its index.</param>
    /// <returns>The state.</returns>
    public AnimationState this[int index] => states[index];
}

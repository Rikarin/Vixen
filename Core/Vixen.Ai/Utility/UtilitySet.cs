// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Core;

namespace Vixen.Ai;

/// <summary>What one agent remembers about a set between decisions.</summary>
/// <param name="Current">Which action is running, or <c>-1</c> for none.</param>
/// <param name="Elapsed">Seconds since the last decision.</param>
/// <param name="Clock">The agent's own clock, which cooldowns are stamped against.</param>
/// <param name="Decisions">How many decisions it has made. Salts the random stream and reads well in a log.</param>
/// <remarks>
///     <para>
///         ⚠ <b>Every field here is inertia, and none of it is optional.</b> An agent re-scoring every
///         frame with two actions at 0.51 and 0.49 oscillates, and oscillation is the single most
///         visible failure mode of a utility agent — it is what makes the technique look broken to
///         anybody watching.
///     </para>
///     <para>
///         <b>A plain struct, so that it fits in a span as easily as in a field.</b> A utility set has
///         two hosts: <c>AiSystem</c>, where it is the agent's planner and the memory is a managed
///         object beside the slot, and <c>RunUtilitySetTask</c>, where it is a leaf of a behaviour tree
///         and everything has to live in the <c>Span&lt;byte&gt;</c> that task was given. One shape for
///         both is what stops the two of them growing different inertia.
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct UtilityState(int Current, float Elapsed, float Clock, int Decisions) {
    /// <summary>Which action is running, or <c>-1</c> for none.</summary>
    public int Current = Current;

    /// <summary>Seconds since the last decision.</summary>
    public float Elapsed = Elapsed;

    /// <summary>The agent's own clock, which cooldowns are stamped against.</summary>
    public float Clock = Clock;

    /// <summary>How many decisions it has made.</summary>
    public int Decisions = Decisions;

    /// <summary>Nothing chosen, no time passed. ⚠ <b>Not <c>default</c></b>, which would mean action zero.</summary>
    public static UtilityState Fresh => new(-1, 0f, 0f, 0);
}

/// <summary>The managed half of what an agent remembers, for a set that is an agent's whole planner.</summary>
/// <remarks>
///     A thin holder over a <see cref="UtilityState" /> and a cooldown array. It exists so that
///     <c>AiSystem</c> can keep one beside an agent's slot the way it keeps a
///     <see cref="BehaviorTreeInstance" />, without the arithmetic being written twice.
/// </remarks>
public sealed class UtilityMemory {
    float[] cooldowns = [];
    UtilityState state = UtilityState.Fresh;

    /// <summary>The state the set reads and writes.</summary>
    /// <remarks>
    ///     By reference, because <see cref="UtilitySet.Choose" /> takes it by <c>ref</c> — a property
    ///     returning a copy would let a caller decide and then throw the decision away.
    /// </remarks>
    public ref UtilityState State => ref state;

    /// <summary>Which action is running, or <c>-1</c> for none.</summary>
    public int Current => state.Current;

    /// <summary>How many decisions it has made.</summary>
    public int Decisions => state.Decisions;

    /// <summary>When each action last ended.</summary>
    public Span<float> Cooldowns => cooldowns;

    /// <summary>Forgets everything, which is what a recycled agent slot needs.</summary>
    public void Reset() {
        state = UtilityState.Fresh;
        Array.Clear(cooldowns);
    }

    /// <summary>Makes room for a set's actions.</summary>
    /// <param name="count">How many there are.</param>
    public void Fit(int count) {
        if (cooldowns.Length < count) {
            Array.Resize(ref cooldowns, count);
        }
    }
}

/// <summary>A list of things an agent might do, and the rules for choosing between them.</summary>
/// <remarks>
///     <para>
///         doc 37 § D2's second planner. What it produces is an <see cref="IAgentAction" /> index —
///         the same thing a behaviour-tree task names and the same thing a GOAP plan's head names — so
///         everything around the decision is written once, in <c>AiSystem</c>.
///     </para>
///     <para>
///         <b>Immutable and shared by every agent running it</b>, exactly like a
///         <see cref="BehaviorTreeTemplate" />. The per-agent half is a <see cref="UtilityState" /> and
///         a span of cooldown stamps.
///     </para>
///     <para>
///         ⚠ <b>The defaults have inertia turned on</b>, because a default that oscillates is a
///         default that makes the feature look broken. A commitment bonus of 0.15 and a decision
///         interval of 0.2 s are what P5's exit criterion measures.
///     </para>
/// </remarks>
public sealed class UtilitySet {
    readonly UtilityAction[] actions;

    /// <summary>Creates a set.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="actions">What the agent might do.</param>
    /// <exception cref="ArgumentNullException"><paramref name="actions" /> is null.</exception>
    public UtilitySet(Symbol name, params UtilityAction[] actions) {
        ArgumentNullException.ThrowIfNull(actions);

        Name = name;
        this.actions = actions;
    }

    /// <summary>What it is called.</summary>
    public Symbol Name { get; }

    /// <summary>How many actions it holds.</summary>
    public int Count => actions.Length;

    /// <summary>The action at an index.</summary>
    /// <param name="index">Its index.</param>
    public UtilityAction this[int index] => actions[index];

    /// <summary>Which of the scored actions wins.</summary>
    public IUtilitySelector Selector { get; init; } = UtilitySelectors.Highest;

    /// <summary>How much is added to the running action's score, to stop it being nudged out.</summary>
    /// <remarks>
    ///     ⚠ Added rather than multiplied, so that it also protects an action whose score has dipped
    ///     near zero. Multiplying would give an action at 0.02 a bonus of nothing, which is exactly
    ///     the region where the flapping happens.
    /// </remarks>
    public float CommitmentBonus { get; init; } = 0.15f;

    /// <summary>Seconds between decisions. Scoring does not happen on the frames in between.</summary>
    public float DecisionInterval { get; init; } = 0.2f;

    /// <summary>Everything it holds, in order.</summary>
    public ReadOnlySpan<UtilityAction> Actions => actions;

    /// <summary>Scores every action, with inertia applied.</summary>
    /// <param name="context">The agent.</param>
    /// <param name="state">What it remembers.</param>
    /// <param name="cooldowns">When each action last ended. May be empty when no action has one.</param>
    /// <param name="scores">Where to put the scores. Must be at least <see cref="Count" /> long.</param>
    /// <exception cref="ArgumentException"><paramref name="scores" /> is too short.</exception>
    public void Score(
        in AgentContext context,
        ref readonly UtilityState state,
        ReadOnlySpan<float> cooldowns,
        Span<float> scores
    ) {
        if (scores.Length < actions.Length) {
            throw new ArgumentException(
                $"A set of {actions.Length} needs somewhere to put {actions.Length} scores.",
                nameof(scores)
            );
        }

        for (var index = 0; index < actions.Length; index++) {
            scores[index] = Available(in state, cooldowns, index) ? actions[index].Score(in context) : 0f;

            // ⚠ The bonus is applied *after* the veto rather than before it, so an action whose
            // condition has genuinely gone false cannot hold on to itself. Commitment is for a score
            // that wobbled, not for one that stopped being true.
            if (index == state.Current && scores[index] > 0f) {
                scores[index] += CommitmentBonus;
            }
        }
    }

    /// <summary>Decides what the agent should do now.</summary>
    /// <param name="context">The agent.</param>
    /// <param name="state">What it remembers. Updated.</param>
    /// <param name="cooldowns">When each action last ended. Updated.</param>
    /// <param name="delta">Seconds since this agent last thought.</param>
    /// <param name="scores">Somewhere to score into, at least <see cref="Count" /> long.</param>
    /// <returns>The index of the chosen action, or <c>-1</c> when nothing is worth doing.</returns>
    /// <remarks>
    ///     ⚠ <b>Between decisions it returns what it already chose without scoring at all.</b> That is
    ///     the third inertia mechanism and the cheapest one: at the shipped 0.2 s an agent scores five
    ///     times a second rather than sixty, so four fifths of the reads of the world never happen.
    /// </remarks>
    public int Choose(
        in AgentContext context,
        ref UtilityState state,
        Span<float> cooldowns,
        float delta,
        Span<float> scores
    ) {
        state.Clock += delta;
        state.Elapsed += delta;

        if (state.Current >= 0 && state.Elapsed < DecisionInterval) {
            return state.Current;
        }

        state.Elapsed = 0f;
        state.Decisions++;

        Score(in context, in state, cooldowns, scores);

        var chosen = Selector.Pick(in context, this, scores[..actions.Length]);

        if (chosen != state.Current) {
            Ended(ref state, cooldowns);
            state.Current = chosen;
        }

        return chosen;
    }

    /// <summary>Tells the set that the running action finished, so its cooldown starts.</summary>
    /// <param name="state">What the agent remembers.</param>
    /// <param name="cooldowns">When each action last ended.</param>
    /// <remarks>
    ///     ⚠ <b>An action that finished is re-decided immediately rather than at the next interval.</b>
    ///     The interval exists to stop an agent changing its mind, and an action that is over is not a
    ///     change of mind — waiting a fifth of a second to notice would be a visible stall after every
    ///     short action.
    /// </remarks>
    public void Finished(ref UtilityState state, Span<float> cooldowns) {
        Ended(ref state, cooldowns);
        state.Current = -1;
        state.Elapsed = DecisionInterval;
    }

    /// <summary>How many bytes a caller needs to run this set out of a span.</summary>
    /// <param name="actionState">How much the widest action in it needs.</param>
    /// <returns>The size in bytes.</returns>
    /// <remarks>
    ///     ⚠ <b>The widest action, not the first.</b> A utility agent changes which action it runs
    ///     without changing its block, so a block sized for its first choice would be too small for
    ///     its second — and the overflow would be a span into somebody else's state.
    /// </remarks>
    public int RequiredState(int actionState) => HeaderSize + (actions.Length * sizeof(float)) + actionState;

    /// <summary>How many bytes the header takes, which is where the cooldowns start.</summary>
    internal static int HeaderSize => Unsafe.SizeOf<UtilityState>();

    /// <summary>Stamps the running action as having just ended.</summary>
    static void Ended(ref UtilityState state, Span<float> cooldowns) {
        if ((uint)state.Current < (uint)cooldowns.Length) {
            // Nudged off zero, because zero is "never ran" and an agent whose very first action ends
            // on the very first frame would otherwise have no cooldown at all.
            cooldowns[state.Current] = state.Clock == 0f ? float.Epsilon : state.Clock;
        }
    }

    /// <summary>Whether an action's cooldown has elapsed.</summary>
    bool Available(ref readonly UtilityState state, ReadOnlySpan<float> cooldowns, int index) {
        var cooldown = actions[index].Cooldown;

        if (cooldown <= 0f || index == state.Current || index >= cooldowns.Length || cooldowns[index] == 0f) {
            return true;
        }

        return state.Clock - cooldowns[index] >= cooldown;
    }
}

/// <summary>The sets a world's agents may name, by index.</summary>
/// <remarks>The same arrangement <c>BehaviorTreeLibrary</c> has, and for its reason.</remarks>
public sealed class UtilitySetLibrary {
    readonly Dictionary<Symbol, UtilitySet> byName = [];
    readonly List<UtilitySet> ordered = [];

    /// <summary>How many sets it holds.</summary>
    public int Count => ordered.Count;

    /// <summary>The set at an index, which is what an <c>AiAgent</c> names.</summary>
    /// <param name="index">Its index.</param>
    public UtilitySet this[int index] => ordered[index];

    /// <summary>Adds a set.</summary>
    /// <param name="set">The set.</param>
    /// <returns>Its index.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="set" /> is null.</exception>
    /// <exception cref="InvalidOperationException">A set of that name is already in it.</exception>
    public int Add(UtilitySet set) {
        ArgumentNullException.ThrowIfNull(set);

        if (set.Name != Symbol.None && !byName.TryAdd(set.Name, set)) {
            throw new InvalidOperationException($"'{set.Name}' is already in this library.");
        }

        ordered.Add(set);

        return ordered.Count - 1;
    }

    /// <summary>Looks a set up by name.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="set">Where to put it.</param>
    /// <returns>Whether the library has it.</returns>
    public bool TryGet(Symbol name, out UtilitySet? set) => byName.TryGetValue(name, out set);

    /// <summary>Looks a set's index up by name.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>Its index, or <c>-1</c>.</returns>
    public int IndexOf(Symbol name) => byName.TryGetValue(name, out var set) ? ordered.IndexOf(set) : -1;
}

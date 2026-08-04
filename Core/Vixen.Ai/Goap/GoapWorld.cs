// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ai;

/// <summary>One thing a plan reasons about, by index.</summary>
/// <remarks>
///     ⚠ <b>An index, not a name, for the reason <see cref="BlackboardKey" /> is.</b> A resolve reads
///     every key of every condition of every action it expands, so a name lookup would be a dictionary
///     probe inside the innermost loop of an A* search. The names exist in the domain's declaration and
///     in a diagnostic, and nowhere on the hot path.
/// </remarks>
public readonly record struct GoapWorldKey(ushort Index) {
    /// <summary>The key that names nothing.</summary>
    public static GoapWorldKey Invalid => new(ushort.MaxValue);

    /// <summary>Whether it names anything.</summary>
    public bool IsValid => Index != ushort.MaxValue;
}

/// <summary>How a condition compares a world key to its value.</summary>
/// <remarks>
///     ⚠ <b>Four, and no equality.</b> doc 37 § D10: the matching rule is that a condition wanting a
///     key <i>greater</i> is served by an action with a <i>positive</i> effect on it, and one wanting
///     it smaller by a negative one. An equality has no direction, so nothing could ever be said to
///     serve it — a condition the resolver could match only by accident, and a graph edge it could
///     never build.
/// </remarks>
public enum GoapComparison : byte {
    /// <summary>Below the value.</summary>
    Less,

    /// <summary>At or below it.</summary>
    LessOrEqual,

    /// <summary>Above it.</summary>
    Greater,

    /// <summary>At or above it.</summary>
    GreaterOrEqual
}

/// <summary>Something that has to be true.</summary>
/// <param name="Key">Which world key.</param>
/// <param name="Comparison">How it is compared.</param>
/// <param name="Value">To what.</param>
public readonly record struct GoapCondition(GoapWorldKey Key, GoapComparison Comparison, int Value) {
    /// <summary>Whether a world state satisfies it.</summary>
    /// <param name="state">The projected world.</param>
    /// <returns>Whether it holds.</returns>
    public bool Holds(ReadOnlySpan<int> state) {
        if (!Key.IsValid || Key.Index >= state.Length) {
            return false;
        }

        var value = state[Key.Index];

        return Comparison switch {
            GoapComparison.Less => value < Value,
            GoapComparison.LessOrEqual => value <= Value,
            GoapComparison.Greater => value > Value,
            _ => value >= Value
        };
    }

    /// <summary>Whether this condition wants the key to go up.</summary>
    /// <remarks>The whole of doc 37 § D10's matching rule, and it is one bit.</remarks>
    public bool WantsIncrease => Comparison is GoapComparison.Greater or GoapComparison.GreaterOrEqual;
}

/// <summary>What an action does to a world key.</summary>
/// <param name="Key">Which key.</param>
/// <param name="Increases">Whether it goes up. Down otherwise.</param>
/// <remarks>
///     ⚠ <b>A direction and not an amount, and that is what makes GOAP authorable.</b> Saying "eating
///     reduces hunger by 40" makes every plan a simulation of arithmetic nobody can predict, and makes
///     the graph depend on numbers a designer tunes. Saying "eating reduces hunger" is a fact about the
///     action that stays true while the numbers move, and it is all the resolver needs to know which
///     action can serve which condition.
/// </remarks>
public readonly record struct GoapEffect(GoapWorldKey Key, bool Increases);

/// <summary>Where a world key's value comes from.</summary>
/// <remarks>
///     doc 37 § P6's world-key projection. It is a seam because "how much ammo do I have" is a game's
///     own question — the shipped implementations cover the case where the answer is already on the
///     blackboard, which is where a sensor or a perception binding will have put it.
/// </remarks>
public interface IGoapWorldSource {
    /// <summary>Reads the world.</summary>
    /// <param name="context">The agent.</param>
    /// <returns>The key's value.</returns>
    int Read(in AgentContext context);
}

/// <summary>A world source written as a lambda.</summary>
/// <param name="context">The agent.</param>
/// <returns>The key's value.</returns>
public delegate int GoapReading(in AgentContext context);

/// <summary>The world sources that ship.</summary>
public static class GoapWorldSources {
    /// <summary>A source from a lambda.</summary>
    /// <param name="reading">What it does.</param>
    /// <returns>The source.</returns>
    public static IGoapWorldSource From(GoapReading reading) => new DelegateWorldSource(reading);

    /// <summary>Always the same number, for a key a game has not wired up yet.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The source.</returns>
    public static IGoapWorldSource Constant(int value) => new ConstantWorldSource(value);

    /// <summary>A numeric blackboard key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The source.</returns>
    /// <remarks>
    ///     ⚠ <b>The usual answer, and it is what makes the three planners one system.</b> A perception
    ///     binding, a tree service and a GOAP world key all reach the same blackboard, so an agent that
    ///     switches planner keeps everything it knew — which is the arrangement doc 37 § D1 and § D13
    ///     exist to produce.
    /// </remarks>
    public static IGoapWorldSource Blackboard(BlackboardKey key) => new BlackboardWorldSource(key);
}

/// <summary>One world key, as a domain declares it.</summary>
/// <param name="Name">What it is called.</param>
/// <param name="Source">Where its value comes from.</param>
public readonly record struct GoapKeyDefinition(Symbol Name, IGoapWorldSource Source);

/// <summary>The world keys a domain reasons about, and how to read them.</summary>
/// <remarks>
///     ⚠ <b>Projected once per resolve, not read per condition.</b> An A* over the action graph
///     evaluates a condition many times per search — the same condition on every partial plan that
///     reaches it — so reading the world through the source each time would be a component lookup per
///     graph node. The projection is a span of ints taken once, and the search is arithmetic over it.
/// </remarks>
public sealed class GoapWorldKeys {
    readonly GoapKeyDefinition[] keys;
    readonly Dictionary<Symbol, GoapWorldKey> byName = [];

    /// <summary>Creates a key table.</summary>
    /// <param name="keys">The keys, in index order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="keys" /> is null.</exception>
    public GoapWorldKeys(params GoapKeyDefinition[] keys) {
        ArgumentNullException.ThrowIfNull(keys);

        this.keys = keys;

        for (var index = 0; index < keys.Length; index++) {
            byName[keys[index].Name] = new((ushort)index);
        }
    }

    /// <summary>How many there are.</summary>
    public int Count => keys.Length;

    /// <summary>The key at an index.</summary>
    /// <param name="key">The key.</param>
    public GoapKeyDefinition this[GoapWorldKey key] => keys[key.Index];

    /// <summary>Looks a key up by name.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="key">Where to put it.</param>
    /// <returns>Whether the table has it.</returns>
    public bool TryGetKey(Symbol name, out GoapWorldKey key) => byName.TryGetValue(name, out key);

    /// <summary>What a key is called.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Its name.</returns>
    public Symbol NameOf(GoapWorldKey key) => key.IsValid && key.Index < keys.Length ? keys[key.Index].Name : Symbol.None;

    /// <summary>Reads every key into a span.</summary>
    /// <param name="context">The agent.</param>
    /// <param name="state">Where to put them. Must be at least <see cref="Count" /> long.</param>
    /// <exception cref="ArgumentException"><paramref name="state" /> is too short.</exception>
    public void Project(in AgentContext context, Span<int> state) {
        if (state.Length < keys.Length) {
            throw new ArgumentException($"A world of {keys.Length} keys needs {keys.Length} ints.", nameof(state));
        }

        for (var index = 0; index < keys.Length; index++) {
            state[index] = keys[index].Source.Read(in context);
        }
    }
}

sealed class ConstantWorldSource(int value) : IGoapWorldSource {
    public int Read(in AgentContext context) => value;
}

sealed class DelegateWorldSource(GoapReading reading) : IGoapWorldSource {
    readonly GoapReading reading = reading ?? throw new ArgumentNullException(nameof(reading));

    public int Read(in AgentContext context) => reading(in context);
}

/// <summary>A numeric blackboard key, read as an int.</summary>
/// <param name="key">The key.</param>
/// <remarks>
///     ⚠ A float key is <b>truncated</b>, and an unset key reads as zero. GOAP reasons about counts
///     and thresholds — "at least one pear", "hunger below forty" — and a plan whose search depended on
///     the fractional part of a health value would be a plan that changed on every frame the health
///     drifted, for no difference anybody could see.
/// </remarks>
public sealed class BlackboardWorldSource(BlackboardKey key) : IGoapWorldSource {
    /// <inheritdoc />
    public int Read(in AgentContext context) {
        var blackboard = context.Blackboard;

        if (!key.IsValid || key.Index >= blackboard.Layout.Count || !blackboard.IsSet(key)) {
            return 0;
        }

        return blackboard.Layout[key].Type switch {
            BlackboardValueType.Bool => blackboard.GetBool(key) ? 1 : 0,
            BlackboardValueType.Int => blackboard.GetInt(key),
            BlackboardValueType.Float => (int)blackboard.GetFloat(key),
            _ => 0
        };
    }
}

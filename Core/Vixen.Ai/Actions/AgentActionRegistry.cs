// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ai;

/// <summary>
///     Every action a world's agents can run, by index, with the memory each one needs.
/// </summary>
/// <remarks>
///     <para>
///         <b>A compiled asset names an action by index, not by type.</b> A behaviour tree's task
///         node, a utility set's row and a GOAP action all resolve to a <see cref="ushort" /> here at
///         load, which is what keeps a node sixteen bytes and a dispatch an array index rather than
///         a dictionary lookup on a string.
///     </para>
///     <para>
///         ⚠ <b>The state size belongs to the registration, not to the action.</b> An action object
///         is shared by every agent running it, so the only thing that can safely say how much room
///         one costs is the table that hands the rooms out. Registering the same action twice with
///         different sizes is legal and occasionally useful — the same <c>Wait</c> with and without
///         a recorded start time — and each registration is its own index.
///     </para>
///     <para>
///         Built once, at load, and read-only afterwards: a registry that could grow during a frame
///         would be a table an agent's index could outlive.
///     </para>
/// </remarks>
public sealed class AgentActionRegistry {
    readonly List<Entry> entries = [];
    readonly Dictionary<Symbol, ushort> byName = [];

    /// <summary>How many actions are registered.</summary>
    public int Count => entries.Count;

    /// <summary>The largest state any registered action asks for.</summary>
    /// <remarks>What a pool sizes its blocks by when an agent may run any of them.</remarks>
    public int MaximumStateSize { get; private set; }

    /// <summary>The action at an index.</summary>
    /// <param name="index">Its index.</param>
    /// <exception cref="ArgumentOutOfRangeException">Nothing is registered there.</exception>
    public IAgentAction this[ushort index] => entries[Check(index)].Action;

    /// <summary>Adds an action.</summary>
    /// <param name="name">What it is called, for diagnostics and for an asset to resolve.</param>
    /// <param name="action">The action.</param>
    /// <param name="stateSize">How many bytes of per-agent memory it needs. May be zero.</param>
    /// <returns>Its index.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="action" /> is null.</exception>
    /// <exception cref="InvalidOperationException">The name is taken, or the registry is full.</exception>
    public ushort Register(string name, IAgentAction action, int stateSize = 0) =>
        Register(Symbol.Intern(name), action, stateSize);

    /// <summary>Adds an action under a name that is already interned.</summary>
    /// <param name="name">Its symbol.</param>
    /// <param name="action">The action.</param>
    /// <param name="stateSize">How many bytes of per-agent memory it needs.</param>
    /// <returns>Its index.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="action" /> is null.</exception>
    /// <exception cref="InvalidOperationException">The name is taken, or the registry is full.</exception>
    public ushort Register(Symbol name, IAgentAction action, int stateSize = 0) {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfNegative(stateSize);

        if (!name.IsSome) {
            throw new InvalidOperationException("An action must have a name.");
        }

        if (byName.ContainsKey(name)) {
            throw new InvalidOperationException($"'{name}' is already a registered action.");
        }

        if (entries.Count >= ushort.MaxValue) {
            throw new InvalidOperationException("A registry may hold at most 65 535 actions.");
        }

        var index = (ushort)entries.Count;

        byName[name] = index;
        entries.Add(new(name, action, stateSize));
        MaximumStateSize = Math.Max(MaximumStateSize, stateSize);

        return index;
    }

    /// <summary>Looks an action up by name.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="index">Where to put its index.</param>
    /// <returns>Whether the registry has it.</returns>
    public bool TryGetIndex(Symbol name, out ushort index) => byName.TryGetValue(name, out index);

    /// <summary>How much memory an action needs.</summary>
    /// <param name="index">Its index.</param>
    /// <returns>Its state size in bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Nothing is registered there.</exception>
    public int StateSize(ushort index) => entries[Check(index)].StateSize;

    /// <summary>What an action is called.</summary>
    /// <param name="index">Its index.</param>
    /// <returns>Its name.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Nothing is registered there.</exception>
    public Symbol NameOf(ushort index) => entries[Check(index)].Name;

    int Check(ushort index) =>
        index < entries.Count
            ? index
            : throw new ArgumentOutOfRangeException(nameof(index), index, "No action is registered at that index.");

    readonly record struct Entry(Symbol Name, IAgentAction Action, int StateSize);
}

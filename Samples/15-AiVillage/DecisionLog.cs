// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ai.Diagnostics;
using Vixen.Ai.Ecs;
using Vixen.Ai.Nodes;
using Vixen.Core;
using Vixen.Ecs;

namespace Vixen.Samples.AiVillage;

/// <summary>What each agent decided, and when it changed its mind.</summary>
/// <remarks>
///     <para>
///         <b>The sample's evidence, and it is a transition rather than a state.</b> "The guard is
///         patrolling" is true of a guard that has never done anything else and of one that has just
///         given up a chase; only the change says the decision is a function of the world. So this
///         records the moment an agent's action differs from the one it had last frame, with where
///         the intruder was standing when it happened.
///     </para>
///     <para>
///         ⚠ <b>Read off <c>AiAgent.Action</c> rather than out of <c>AgentDebugRecorder</c>, on
///         purpose.</b> The recorder is doc 37 § P7's ring and is off by default — a sample whose
///         only evidence came from a diagnostic that a shipped game turns off would be evidence
///         about the diagnostic. <c>AiAgent</c> is the component the runtime writes whatever anybody
///         is watching, so this reads what the game itself acts on.
///     </para>
///     <para>
///         ⚠ <b><c>Started</c> and not a non-zero action.</b> <c>AiAgent.Action</c> is zero until a
///         planner sets it and zero is a valid registry index, so an agent that has decided nothing
///         and an agent running the first-registered action are indistinguishable by the index
///         alone. Doc 37 § P6 records the runtime side of the same trap: <i>a planner that has
///         chosen nothing must run nothing</i>, and both P5 and P0 had it the other way.
///     </para>
/// </remarks>
public sealed class DecisionLog {
    /// <summary>One agent changing its mind.</summary>
    /// <param name="Frame">Which frame it happened on.</param>
    /// <param name="Seconds">How far into the intruder's script that was.</param>
    /// <param name="Agent">Which agent, by the name this sample gave it.</param>
    /// <param name="Planner">Which of the three decided it.</param>
    /// <param name="From">What it had been doing, or <see cref="Symbol.None" /> for its first choice.</param>
    /// <param name="To">What it is doing now.</param>
    /// <param name="Distance">How far the intruder was, flat, when it changed.</param>
    public readonly record struct Change(
        long Frame,
        double Seconds,
        string Agent,
        AiPlanner Planner,
        Symbol From,
        Symbol To,
        float Distance
    ) {
        /// <inheritdoc />
        public override string ToString() =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"frame {Frame,5} · {Seconds,6:0.00}s · {Agent,-9} ({Planner,-12}) {From} → {To}, intruder {Distance,5:0.0} m"
            );
    }

    readonly Village village;
    readonly (string Name, Entity Entity)[] watched;
    readonly Symbol[] last;
    readonly List<Change> changes = [];

    /// <summary>Watches a village's three agents.</summary>
    /// <param name="village">The village.</param>
    /// <exception cref="ArgumentNullException"><paramref name="village" /> is null.</exception>
    public DecisionLog(Village village) {
        ArgumentNullException.ThrowIfNull(village);

        this.village = village;

        watched = [
            ("guard", village.Guard),
            ("villager", village.Villager),
            ("scavenger", village.Scavenger)
        ];

        last = new Symbol[watched.Length];
    }

    /// <summary>Every change of mind, oldest first.</summary>
    public IReadOnlyList<Change> Changes => changes;

    /// <summary>Reads the three agents and records whatever moved.</summary>
    /// <param name="frame">Which frame this is.</param>
    /// <param name="seconds">How far into the script.</param>
    /// <returns>How many agents changed their minds this frame.</returns>
    public int Observe(long frame, double seconds) {
        var moved = 0;
        var intruder = village.Where(village.Intruder);

        for (var index = 0; index < watched.Length; index++) {
            var (name, entity) = watched[index];

            if (!village.World.IsAlive(entity) || !village.World.Has<AiAgent>(entity)) {
                continue;
            }

            var doing = village.Doing(entity);

            // ⚠ An agent between two tasks has not changed its mind, it is between minds. A
            // behaviour tree reports no action for the frame in which a leaf has succeeded and the
            // root has not yet picked the next one, and logging that as a decision would double
            // every line and say nothing — "chase → nothing → chase" is one guard still chasing.
            if (doing == Symbol.None || doing == last[index]) {
                continue;
            }

            changes.Add(
                new Change(
                    frame,
                    seconds,
                    name,
                    village.World.Read<AiAgent>(entity).Planner,
                    last[index],
                    doing,
                    AgentTarget.FlatDistance(village.Where(entity), intruder)
                )
            );

            last[index] = doing;
            moved++;
        }

        return moved;
    }

    /// <summary>Every change one agent made.</summary>
    /// <param name="agent">Its name in this sample.</param>
    /// <returns>The changes.</returns>
    public IEnumerable<Change> For(string agent) =>
        changes.Where(change => string.Equals(change.Agent, agent, StringComparison.Ordinal));

    /// <summary>The whole log as text, one line each — what two runs are compared on.</summary>
    /// <returns>The log.</returns>
    public string Transcript() => string.Join('\n', changes.Select(change => change.ToString()));
}

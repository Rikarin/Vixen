// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;

namespace Vixen.Ai.Diagnostics;

/// <summary>Which of the three decided this.</summary>
public enum AiPlanner : byte {
    /// <summary>No planner: an agent running one action, which is what a test and a spawner use.</summary>
    None,

    /// <summary>A behaviour tree.</summary>
    BehaviorTree,

    /// <summary>A utility set.</summary>
    Utility,

    /// <summary>A GOAP plan.</summary>
    Goap
}

/// <summary>
///     What an agent decided, and why, in the one shape all three planners fill.
/// </summary>
/// <param name="Entity">The agent.</param>
/// <param name="Tick">When.</param>
/// <param name="Planner">Which planner decided.</param>
/// <param name="Action">What it is doing — the task, the utility action, the plan's current step.</param>
/// <param name="Status">How that action is getting on.</param>
/// <param name="Index">
///     Where the decision sits: a tree node's execution index, the chosen action's index in a
///     utility set, or the step's position in a plan.
/// </param>
/// <param name="Count">
///     What it was chosen from: the composite's child count, the number of candidates scored, or the
///     plan's length.
/// </param>
/// <param name="Reason">
///     Why, named: the decorator that decided, the consideration that dominated, or the condition
///     still unmet.
/// </param>
/// <param name="Score">
///     The number behind the reason: one or zero for a decorator, the utility score, the plan's cost.
/// </param>
/// <remarks>
///     <para>
///         <b>One record for three planners, because doc 37 § D2 made the agent one shape.</b> The
///         debug overlay, the editor's panels and a headless test's log are three views over this and
///         not three implementations — which is what makes "an agent misbehaving in a headless test
///         is diagnosed from the recorded log alone" a criterion rather than an aspiration.
///     </para>
///     <para>
///         Deliberately flat and value-typed: it goes in a ring buffer, it is compared in tests, and
///         it must be cheap enough that recording is a decision about noise rather than about cost.
///     </para>
/// </remarks>
public readonly record struct AgentDebugRecord(
    Entity Entity,
    long Tick,
    AiPlanner Planner,
    Symbol Action,
    ActionStatus Status,
    int Index,
    int Count,
    Symbol Reason,
    float Score
) {
    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"[{Tick}] {Entity} {Planner}: {Action} ({Index}/{Count}) {Status}, because {Reason} = {Score:0.###}"
    );
}

/// <summary>A ring of the last few decisions, off by default.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Off by default, and not because of the cost.</b> Doc 37 § D17 makes AI the
///         authority's alone — nothing about a tree, a board or a plan crosses the wire — and the
///         one channel that must, for the editor to debug a running dedicated server, is gated
///         behind the same switch doc 13's remote inspector uses and is never in a shipping server
///         build. A recorder that was on by default would be that channel's data, sitting in memory,
///         waiting for somebody to add the transport.
///     </para>
///     <para>
///         The ring is allocated when it is turned on and never again, so recording costs a store
///         and an index bump. It is a ring rather than a list because the interesting frames are the
///         last ones before the thing went wrong, and a list that grew for an hour is a memory leak
///         with a diagnostic label on it.
///     </para>
/// </remarks>
public sealed class AgentDebugRecorder {
    AgentDebugRecord[] ring = [];
    int next;
    int filled;
    int capacity = 1024;

    /// <summary>Whether anything is being recorded.</summary>
    public bool Enabled { get; set; }

    /// <summary>How many records to keep.</summary>
    /// <remarks>Changing it drops what has been recorded, which is what a caller wants: a ring
    /// resized mid-session would interleave two windows and read as one.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Set to less than one.</exception>
    public int Capacity {
        get => capacity;
        set {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);

            capacity = value;
            ring = [];
            next = 0;
            filled = 0;
        }
    }

    /// <summary>How many records are held.</summary>
    public int Count => filled;

    /// <summary>Adds a record, if recording is on.</summary>
    /// <param name="record">What happened.</param>
    public void Record(in AgentDebugRecord record) {
        if (!Enabled) {
            return;
        }

        if (ring.Length != capacity) {
            ring = new AgentDebugRecord[capacity];
            next = 0;
            filled = 0;
        }

        ring[next] = record;
        next = next + 1 == ring.Length ? 0 : next + 1;
        filled = Math.Min(filled + 1, ring.Length);
    }

    /// <summary>Copies what has been recorded, oldest first.</summary>
    /// <param name="destination">Where to put it.</param>
    /// <returns>How many were written.</returns>
    public int CopyTo(Span<AgentDebugRecord> destination) {
        var written = 0;
        var start = filled == ring.Length ? next : 0;

        for (var index = 0; index < filled && written < destination.Length; index++) {
            destination[written++] = ring[(start + index) % ring.Length];
        }

        return written;
    }

    /// <summary>The most recent record for an agent.</summary>
    /// <param name="entity">The agent.</param>
    /// <param name="record">Where to put it.</param>
    /// <returns>Whether there was one.</returns>
    public bool TryGetLatest(Entity entity, out AgentDebugRecord record) {
        for (var step = 1; step <= filled; step++) {
            var index = ((next - step) % ring.Length + ring.Length) % ring.Length;

            if (ring[index].Entity == entity) {
                record = ring[index];

                return true;
            }
        }

        record = default;

        return false;
    }

    /// <summary>Forgets everything recorded so far.</summary>
    public void Clear() {
        next = 0;
        filled = 0;
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;

namespace Vixen.Ai.Diagnostics;

/// <summary>Which part of an agent a row belongs to.</summary>
/// <remarks>
///     Carried on the row rather than implied by which list it came out of, because the overlay, the
///     editor panel and the wire all flatten the lists at some point and a row that had lost its
///     section would have to be re-derived from its position.
/// </remarks>
public enum AiDebugSection : byte {
    /// <summary>What the agent is doing: the active path, the chosen action, the plan.</summary>
    Doing,

    /// <summary>Why: a decorator's last result, a consideration's factor, an unmet condition.</summary>
    Why,

    /// <summary>Its data: a blackboard key, or a projected world key.</summary>
    Data,

    /// <summary>Its senses: one perceived target.</summary>
    Senses
}

/// <summary>One line of an agent's state: a name, what it says, and a number behind it.</summary>
/// <param name="Section">Which part of the agent it belongs to.</param>
/// <param name="Name">What it is called — a node, a key, a consideration, a source entity.</param>
/// <param name="Value">What it says, already formatted, because a viewer must not need the type.</param>
/// <param name="Number">The number behind it: a score, a cost, a distance, an age. Zero when there is none.</param>
/// <param name="Active">Whether this is the one that is running, chosen, or current.</param>
/// <remarks>
///     ⚠ <b>One row type for five lists, and that is doc 37 § D2 arriving at the debugger.</b> An
///     active tree path, a table of scored candidates, a plan's steps, a blackboard and a perceived
///     list are all "a name, a reading and whether it is the live one" — so the overlay draws one
///     kind of line, the panel builds one kind of table, and the wire writes one kind of record. Five
///     shapes would have been five of each.
/// </remarks>
public readonly record struct AiDebugRow(
    AiDebugSection Section,
    string Name,
    string Value,
    float Number,
    bool Active
) {
    /// <summary>A row with no number.</summary>
    /// <param name="section">Which part of the agent.</param>
    /// <param name="name">What it is called.</param>
    /// <param name="value">What it says.</param>
    /// <param name="active">Whether it is the live one.</param>
    /// <returns>The row.</returns>
    public static AiDebugRow Of(AiDebugSection section, string name, string value, bool active = false) =>
        new(section, name, value, 0f, active);

    /// <summary>A row whose value is its own number.</summary>
    /// <param name="section">Which part of the agent.</param>
    /// <param name="name">What it is called.</param>
    /// <param name="number">The reading.</param>
    /// <param name="active">Whether it is the live one.</param>
    /// <returns>The row.</returns>
    public static AiDebugRow Of(AiDebugSection section, string name, float number, bool active = false) =>
        new(section, name, number.ToString("0.###", CultureInfo.InvariantCulture), number, active);

    /// <inheritdoc />
    public override string ToString() => $"{(Active ? "> " : "  ")}{Name}: {Value}";
}

/// <summary>
///     One agent's state, at one instant, in the one shape all three planners fill.
/// </summary>
/// <remarks>
///     <para>
///         <b>The overlay draws this, the editor panel tabulates it, and the remote channel writes
///         it.</b> Doc 37 § D20 asks for one debug surface rather than three, and the way to get one
///         is to have the three planners agree on a shape before anything looks at them — otherwise
///         "one surface" means one class with three branches in every method.
///     </para>
///     <para>
///         ⚠ <b>Strings, and taken on the owning thread.</b> It is a picture rather than a view: it
///         holds no <see cref="World" />, no <c>Blackboard</c> and no template, so it can be handed to
///         a panel that redraws next frame, put in a list to compare against, or written to a socket,
///         without any of those touching live data. That costs a formatting pass per agent per
///         capture, which is why nothing captures unless something asked.
///     </para>
///     <para>
///         Reused rather than allocated: <see cref="Clear" /> keeps the lists, so a panel polling one
///         agent every frame allocates the strings and nothing else.
///     </para>
/// </remarks>
public sealed class AiAgentSnapshot {
    readonly List<AiDebugRow> rows = [];

    /// <summary>Which agent.</summary>
    public Entity Entity { get; set; }

    /// <summary>When, on the system's own tick count.</summary>
    public long Tick { get; set; }

    /// <summary>Which planner decided.</summary>
    public AiPlanner Planner { get; set; }

    /// <summary>What it is running: the tree, the set, or the domain.</summary>
    public Symbol Asset { get; set; }

    /// <summary>What it is doing — the task, the utility action, the plan's current step.</summary>
    public Symbol Action { get; set; }

    /// <summary>How that is getting on.</summary>
    public ActionStatus Status { get; set; }

    /// <summary>Why, in a sentence: the goal, the failure, the dominating consideration.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Where the agent is, when whoever took the snapshot knew.</summary>
    /// <remarks>
    ///     ⚠ Filled by the caller and not by <see cref="AiSnapshots.Take" />, because a position is a
    ///     <c>LocalTransform</c> and that is <c>Vixen.Engine</c>'s — which this assembly deliberately
    ///     does not reference. The overlay knows; a headless diagnosis does not need to.
    /// </remarks>
    public Vector3 Position { get; set; }

    /// <summary>Whether <see cref="Position" /> means anything.</summary>
    public bool Located { get; set; }

    /// <summary>Everything there is to show, in the order a viewer draws it.</summary>
    public IReadOnlyList<AiDebugRow> Rows => rows;

    /// <summary>How many rows there are.</summary>
    public int Count => rows.Count;

    /// <summary>Adds a row.</summary>
    /// <param name="row">The row.</param>
    public void Add(in AiDebugRow row) => rows.Add(row);

    /// <summary>How many rows belong to one section.</summary>
    /// <param name="section">The section.</param>
    /// <returns>The count.</returns>
    public int CountOf(AiDebugSection section) {
        var count = 0;

        foreach (var row in rows) {
            if (row.Section == section) {
                count++;
            }
        }

        return count;
    }

    /// <summary>The rows of one section, in order.</summary>
    /// <param name="section">The section.</param>
    /// <returns>Them.</returns>
    public IEnumerable<AiDebugRow> Section(AiDebugSection section) {
        foreach (var row in rows) {
            if (row.Section == section) {
                yield return row;
            }
        }
    }

    /// <summary>Forgets everything, keeping the room it was in.</summary>
    public void Clear() {
        rows.Clear();
        Entity = Entity.Null;
        Tick = 0;
        Planner = AiPlanner.None;
        Asset = Symbol.None;
        Action = Symbol.None;
        Status = ActionStatus.Running;
        Reason = string.Empty;
        Position = Vector3.Zero;
        Located = false;
    }

    /// <summary>The whole picture as text, which is what a headless failure prints.</summary>
    /// <returns>It.</returns>
    public override string ToString() {
        var text = new System.Text.StringBuilder();

        text.Append(CultureInfo.InvariantCulture, $"[{Tick}] {Entity} {Planner} {Asset}: {Action} {Status}");

        if (Reason.Length > 0) {
            text.Append(CultureInfo.InvariantCulture, $" — {Reason}");
        }

        foreach (var row in rows) {
            text.Append(CultureInfo.InvariantCulture, $"\n  {row.Section,-6} {row}");
        }

        return text.ToString();
    }
}

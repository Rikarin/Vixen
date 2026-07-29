// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ecs;

namespace Vixen.Editor.Profiler;

/// <summary>How a counted thing compares with what somebody said it should be.</summary>
public enum BudgetState : byte {
    /// <summary>Under budget, or no budget was set.</summary>
    Fine,

    /// <summary>Within a tenth of the ceiling.</summary>
    Near,

    /// <summary>Over.</summary>
    Over
}

/// <summary>One counted thing, and the ceiling it was measured against.</summary>
/// <param name="Label">What is being counted.</param>
/// <param name="Value">How many there are.</param>
/// <param name="Budget">The ceiling, or <see langword="null" /> for a figure with no opinion.</param>
/// <param name="Detail">A sentence about it, or <see langword="null" />.</param>
public readonly record struct StatisticRow(string Label, long Value, long? Budget = null, string? Detail = null) {
    /// <summary>How the value compares with the budget.</summary>
    /// <remarks>
    ///     ⚠ <b>A "near" band rather than a boolean, because a budget crossed is a budget crossed
    ///     too late.</b> The value of a statistics panel is that somebody sees the count climbing
    ///     towards the ceiling during the week they are adding content, not on the day the frame
    ///     rate drops.
    /// </remarks>
    public BudgetState State => Budget is not { } ceiling || ceiling <= 0
        ? BudgetState.Fine
        : Value > ceiling
            ? BudgetState.Over
            : Value >= ceiling - (ceiling / 10)
                ? BudgetState.Near
                : BudgetState.Fine;

    /// <summary>How full the budget is, from zero to one, clamped. Zero without a budget.</summary>
    public float Fill => Budget is { } ceiling and > 0 ? (float)Math.Clamp(Value / (double)ceiling, 0d, 1d) : 0f;
}

/// <summary>What a scene is allowed to contain before somebody wants to know.</summary>
/// <remarks>
///     ⚠ <b>Defaults that are deliberately generous, and a project should overwrite them.</b> A
///     ceiling nobody chose is a ceiling everybody ignores, and a panel whose warnings are wrong on
///     the first scene it is opened on is one nobody opens twice. What the defaults are for is that
///     the column exists at all, so a project setting has somewhere to land.
/// </remarks>
public sealed record StatisticsBudget {
    /// <summary>How many entities a scene may hold.</summary>
    public long Entities { get; init; } = 100_000;

    /// <summary>How many archetypes it may spread across.</summary>
    /// <remarks>
    ///     The figure worth watching that nobody thinks to watch. Archetype count is what decides how
    ///     many chunks a query walks, and a scene that grew from forty archetypes to four hundred is
    ///     one where somebody added a tag component per entity — which costs nothing to store and
    ///     fragments every query in the game.
    /// </remarks>
    public long Archetypes { get; init; } = 256;

    /// <summary>How deep the hierarchy may go.</summary>
    public long HierarchyDepth { get; init; } = 32;

    /// <summary>How many bytes of chunk storage the world may hold.</summary>
    public long ChunkBytes { get; init; } = 256L * 1024 * 1024;
}

/// <summary>A scene's counts, budgets and warnings.</summary>
/// <remarks>
///     <para>
///         <b>Doc 20's B4 says this depends on "scene traversal only", and it does.</b> There is no
///         renderer here, no device and no asset database — what a statistics panel can honestly say
///         at this point in the engine's life is how much is in the world and how it is arranged,
///         and saying that well is better than a draw-call count that would be a guess.
///     </para>
///     <para>
///         ⚠ <b>Chunk bytes are reserved rather than used, and the row says so.</b> An archetype
///         allocates whole chunks, so a world with one entity in each of forty archetypes has forty
///         chunks reserved — which is the honest number to show, because it is the memory the world
///         is holding. A "used" figure computed from entity counts would read far lower and would
///         not be what the process has committed.
///     </para>
/// </remarks>
public sealed class SceneStatistics {
    SceneStatistics(IReadOnlyList<StatisticRow> rows, IReadOnlyList<string> warnings) {
        Rows = rows;
        Warnings = warnings;
    }

    /// <summary>The counted things.</summary>
    public IReadOnlyList<StatisticRow> Rows { get; }

    /// <summary>What is worth saying out loud, in the order it was noticed.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Whether anything is over or near its ceiling.</summary>
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>Walks a world and counts what is in it.</summary>
    /// <param name="world">The world.</param>
    /// <param name="budget">The ceilings, or <see langword="null" /> for the defaults.</param>
    /// <param name="depth">
    ///     How deep the hierarchy goes, which this assembly cannot work out for itself — the parent
    ///     relation lives above the ECS. <see langword="null" /> leaves the row out rather than
    ///     showing a zero somebody would read as "flat".
    /// </param>
    /// <returns>The statistics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public static SceneStatistics Collect(World world, StatisticsBudget? budget = null, int? depth = null) {
        ArgumentNullException.ThrowIfNull(world);

        var ceilings = budget ?? new StatisticsBudget();

        List<StatisticRow> rows = [];
        List<string> warnings = [];

        var populated = 0;
        var chunks = 0;
        var reserved = 0L;
        var componentUses = 0;
        var emptyArchetypes = 0;

        foreach (var archetype in world.Archetypes) {
            if (archetype.EntityCount > 0) {
                populated++;
                componentUses += archetype.Signature.Count * archetype.EntityCount;
            } else if (archetype.Chunks.Count > 0) {
                emptyArchetypes++;
            }

            chunks += archetype.Chunks.Count;
            reserved += (long)archetype.Chunks.Count * archetype.ChunkBytes;
        }

        rows.Add(new("Entities", world.EntityCount, ceilings.Entities));
        rows.Add(new("Archetypes", populated, ceilings.Archetypes, "distinct component combinations in use"));
        rows.Add(new("Chunks", chunks, Detail: "allocated across every archetype"));
        rows.Add(new("Chunk memory", reserved, ceilings.ChunkBytes, "reserved, not used — a chunk is whole"));
        rows.Add(new("Component instances", componentUses));

        if (depth is { } deepest) {
            rows.Add(new("Hierarchy depth", deepest, ceilings.HierarchyDepth, "the longest chain of parents"));
        }

        foreach (var row in rows) {
            switch (row.State) {
                case BudgetState.Over:
                    warnings.Add($"{row.Label} is over budget: {row.Value:N0} against {row.Budget:N0}.");
                    break;

                case BudgetState.Near:
                    warnings.Add($"{row.Label} is within a tenth of its budget of {row.Budget:N0}.");
                    break;

                default:
                    break;
            }
        }

        // ⚠ Not a budget, because there is no sensible ceiling — but worth a line, because an
        // archetype that emptied keeps its chunks and a scene that churns component sets ends up
        // walking hundreds of empty chunks per query with nothing in the counts to show why.
        if (emptyArchetypes > 0) {
            warnings.Add(
                $"{emptyArchetypes} archetype(s) hold chunks and no entities. Queries still walk them."
            );
        }

        return new(rows, warnings);
    }
}

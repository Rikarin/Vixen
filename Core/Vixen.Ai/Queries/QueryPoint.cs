// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.InteropServices;
using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Ai;

/// <summary>One candidate answer to "where should I stand".</summary>
/// <param name="Position">Where it is, in world space.</param>
/// <param name="Entity">What it came from, when a generator made it out of one. Otherwise null.</param>
/// <remarks>
///     ⚠ <b>A position <i>and</i> an entity, not one or the other.</b> "Stand behind that crate" and
///     "shoot at that guard" are the same query with different tests over it, and a candidate that
///     could only be a point would make the second one a second machine. A generator that made it out
///     of nothing leaves the entity null, and every test that needs one fails such a point rather
///     than guessing.
/// </remarks>
public readonly record struct QueryPoint(Vector3 Position, Entity Entity = default) {
    /// <inheritdoc />
    public override string ToString() => Entity.IsNull
        ? Position.ToString()
        : string.Create(CultureInfo.InvariantCulture, $"{Position} ({Entity})");
}

/// <summary>A generated point, what it scored, and whether a filter threw it away.</summary>
/// <param name="Point">The candidate.</param>
/// <param name="Score">Its combined score, in <c>[0, weight]</c>. Zero when filtered.</param>
/// <param name="Filtered">Whether a test with a filtering purpose rejected it.</param>
public readonly record struct ScoredQueryPoint(QueryPoint Point, float Score, bool Filtered) {
    /// <summary>Where it is.</summary>
    public Vector3 Position => Point.Position;

    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Point} {(Filtered ? "filtered" : $"{Score:0.###}")}"
    );
}

/// <summary>
///     The working set of one query run: the points, their scores, and every factor behind them.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Reused between runs rather than allocated per run</b>, which is the difference between
///         an environment query being affordable on a schedule and being a thing projects turn off. A
///         query generating four hundred points over a dozen agents is a list of four hundred structs,
///         cleared and refilled; a run that allocated would allocate that much per agent per interval.
///     </para>
///     <para>
///         The per-factor detail is optional and off unless <see cref="Detailed" /> is set. It is what
///         the editor's preview table and the debug overlay read — <i>why</i> is this point worse than
///         that one — and it is <c>points × tests</c> floats, which is exactly the cost nobody wants
///         to pay in a shipped build.
///     </para>
/// </remarks>
public sealed class QueryResults {
    readonly List<ScoredQueryPoint> points = [];
    readonly List<float> detail = [];

    /// <summary>How many points there are.</summary>
    public int Count => points.Count;

    /// <summary>Every point, in the order the generators made them.</summary>
    public ReadOnlySpan<ScoredQueryPoint> Points => CollectionsMarshal.AsSpan(points);

    /// <summary>How many factors each point recorded, or zero when nothing was recorded.</summary>
    public int Factors { get; private set; }

    /// <summary>Whether the per-factor detail is kept.</summary>
    public bool Detailed { get; set; }

    /// <summary>Which query produced this.</summary>
    public Symbol Query { get; internal set; }

    /// <summary>How many points a generator made before anything was filtered.</summary>
    public int Generated { get; internal set; }

    /// <summary>How many survived every filtering test.</summary>
    public int Surviving { get; private set; }

    /// <summary>The index of the best point, or <c>-1</c> when nothing survived.</summary>
    public int Best { get; private set; } = -1;

    /// <summary>The best point, if there is one.</summary>
    /// <param name="point">Where to put it.</param>
    /// <returns>Whether anything survived.</returns>
    public bool TryBest(out QueryPoint point) {
        if (Best < 0) {
            point = default;

            return false;
        }

        point = points[Best].Point;

        return true;
    }

    /// <summary>What one point's factors read, when the detail was kept.</summary>
    /// <param name="index">Which point.</param>
    /// <returns>Its factors, or empty.</returns>
    public ReadOnlySpan<float> DetailOf(int index) =>
        Factors == 0 || (uint)index >= (uint)points.Count
            ? default
            : CollectionsMarshal.AsSpan(detail).Slice(index * Factors, Factors);

    /// <summary>Forgets everything, keeping the room it was in.</summary>
    public void Clear() {
        points.Clear();
        detail.Clear();
        Factors = 0;
        Generated = 0;
        Surviving = 0;
        Best = -1;
        Query = Symbol.None;
    }

    /// <summary>Makes room for a run, and says how wide the detail rows are.</summary>
    internal void Begin(Symbol query, int factors) {
        Clear();
        Query = query;
        Factors = Detailed ? factors : 0;
    }

    /// <summary>Adds a scored point.</summary>
    internal void Add(in QueryPoint point, float score, bool filtered, ReadOnlySpan<float> factors) {
        points.Add(new(point, filtered ? 0f : score, filtered));

        if (Factors > 0) {
            for (var index = 0; index < Factors; index++) {
                detail.Add(index < factors.Length ? factors[index] : 0f);
            }
        }

        if (filtered) {
            return;
        }

        Surviving++;

        // ⚠ Strictly greater, so a tie keeps the earlier point. Doc 37 § D18: a tie breaks on the
        // index, never on which one a float comparison happened to prefer, or the same tick replayed
        // picks a different corner to stand in.
        if (Best < 0 || score > points[Best].Score) {
            Best = points.Count - 1;
        }
    }
}

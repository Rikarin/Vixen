// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering.SurfaceCache;

/// <summary>Which cards could contain a point — the spatial index the linear scan was the baseline for.</summary>
/// <remarks>
///     <para>
///         <b>A uniform grid of card lists, and a query is one cell.</b> A card registers into every
///         cell its box overlaps, so any card whose box contains a point necessarily overlaps the
///         cell the point falls in — one dictionary lookup gives a superset of the containing cards,
///         and the containment, depth and facing tests stay exactly where they were. The index
///         narrows the scan; it never answers for it, which is what makes "the index agrees with the
///         linear scan" a property a test can hold on random cards rather than an argument.
///     </para>
///     <para>
///         <b>Candidates come back in the order the cards arrived.</b> Cards are only ever appended,
///         so every cell's list is ascending by index — and the sampling's tie-break (equal facing
///         goes to the earlier card) survives the index without knowing it exists.
///     </para>
/// </remarks>
public sealed class SurfaceCardIndex {
    readonly Dictionary<Int3, List<int>> cells = [];

    /// <summary>Builds an empty index.</summary>
    /// <param name="cellSize">How wide a grid cell is, in world units.</param>
    /// <exception cref="ArgumentOutOfRangeException">A cell of no size.</exception>
    /// <remarks>
    ///     The size trades registration against narrowing: cells much smaller than a card register it
    ///     many times, cells much larger than the scene put every card in one list — which is the
    ///     linear scan again, correct and unimproved. A few times a typical card's extent is right.
    /// </remarks>
    public SurfaceCardIndex(float cellSize = 4f) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellSize);

        CellSize = cellSize;
    }

    /// <summary>How wide a grid cell is, in world units.</summary>
    public float CellSize { get; }

    /// <summary>How many card registrations the grid holds, across all cells.</summary>
    public int Registrations { get; private set; }

    /// <summary>Registers a card under every cell its box overlaps.</summary>
    /// <param name="card">The card's index in whatever list the caller keeps.</param>
    /// <param name="shape">Its box.</param>
    public void Add(int card, SurfaceCard shape) {
        var low = Cell(shape.Centre - shape.HalfSize);
        var high = Cell(shape.Centre + shape.HalfSize);

        for (var z = low.Z; z <= high.Z; z++) {
            for (var y = low.Y; y <= high.Y; y++) {
                for (var x = low.X; x <= high.X; x++) {
                    ref var cell = ref CollectionsMarshal.GetValueRefOrAddDefault(cells, new(x, y, z), out _);

                    (cell ??= []).Add(card);
                    Registrations++;
                }
            }
        }
    }

    /// <summary>The cards whose boxes could contain a point, ascending, possibly with false positives.</summary>
    /// <param name="position">The point.</param>
    public ReadOnlySpan<int> Candidates(Vector3 position) =>
        cells.TryGetValue(Cell(position), out var cell) ? CollectionsMarshal.AsSpan(cell) : [];

    /// <summary>Forgets every card.</summary>
    public void Clear() {
        cells.Clear();
        Registrations = 0;
    }

    Int3 Cell(Vector3 position) =>
        new(
            (int)MathF.Floor(position.X / CellSize),
            (int)MathF.Floor(position.Y / CellSize),
            (int)MathF.Floor(position.Z / CellSize)
        );
}

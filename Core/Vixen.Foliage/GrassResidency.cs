// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Foliage;

/// <summary>One cell and the ring slot it was given.</summary>
/// <param name="Cell">Which cell.</param>
/// <param name="Slot">Which of the ring's fixed allocations it holds.</param>
public readonly record struct GrassSlot(FoliageCellKey Cell, int Slot);

/// <summary>What one update of the ring changed.</summary>
/// <param name="Created">Cells that entered range and were given a slot.</param>
/// <param name="Evicted">Cells that left it and gave one up.</param>
/// <remarks>
///     ⚠ <b>A difference and not a set</b> — <c>FoliageCollision.Update</c>'s reason, and the reason
///     is stronger here. What the caller does with a created cell is scatter it and upload a buffer,
///     and what it does with an evicted one is return that buffer to the pool; handing back the whole
///     resident set would make it diff a few thousand cells every frame to find the two that moved.
/// </remarks>
public readonly record struct GrassResidencyChange(
    IReadOnlyList<GrassSlot> Created,
    IReadOnlyList<GrassSlot> Evicted
) {
    /// <summary>Whether nothing moved.</summary>
    public bool IsEmpty => Created.Count == 0 && Evicted.Count == 0;
}

/// <summary>
///     Which cells are close enough to hold a scattered buffer, and which slot each of them is in.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T6]'s ring, and the reason grass costs a fixed amount of memory.</b> A
///         cell entering range is given one of a fixed number of pooled allocations and scattered
///         into it; a cell leaving hands its slot back. Nothing about it is persisted, so eviction is
///         free and re-entry is a re-scatter that produces the identical blades —
///         <see cref="GrassScatter.Hash" /> is what makes that true.
///     </para>
///     <para>
///         ⚠ <b>Eviction happens further out than creation, and the gap is not decoration.</b> A
///         camera standing on the boundary of a cell that is created and evicted at the same distance
///         re-scatters it every frame, which is the whole cost of the feature paid for nothing. The
///         hysteresis makes the two ranges different, so standing still is stable.
///     </para>
///     <para>
///         ⚠ <b>Distance is measured to the nearest point of the cell, not to its centre.</b> A 32 m
///         cell whose near edge is under the camera has its centre 22 m away, so a centre test with a
///         20 m range would leave the ground the camera is standing on bare.
///     </para>
///     <para>
///         ⚠ <b>A ring too small for its range drops the far cells.</b> The wanted set is filled
///         nearest first, so what a capacity that ran out loses is the horizon rather than a hole
///         under the camera — and <see cref="Refused" /> says it happened, because the alternative is
///         a level that quietly stops having grass at a distance nobody chose.
///     </para>
/// </remarks>
public sealed class GrassResidency {
    readonly Dictionary<FoliageCellKey, int> resident = [];
    readonly Stack<int> free = [];
    readonly List<GrassSlot> created = [];
    readonly List<GrassSlot> evicted = [];

    /// <summary>Creates an empty ring.</summary>
    /// <param name="grid">The cell grid it addresses.</param>
    /// <param name="capacity">How many cells may be resident at once.</param>
    /// <exception cref="ArgumentOutOfRangeException">The capacity is not positive.</exception>
    public GrassResidency(FoliageCellGrid grid, int capacity) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        Grid = grid;
        Capacity = capacity;

        for (var slot = capacity - 1; slot >= 0; slot--) {
            free.Push(slot);
        }
    }

    /// <summary>The grid its cells are of.</summary>
    public FoliageCellGrid Grid { get; }

    /// <summary>How many cells may be resident at once.</summary>
    public int Capacity { get; }

    /// <summary>How many are.</summary>
    public int Count => resident.Count;

    /// <summary>How far past the creation range a cell survives before it is evicted, as a fraction.</summary>
    /// <remarks>
    ///     Fifteen per cent, which at a 40 m range is six metres of dead band — comfortably more than
    ///     a camera drifts in a frame and comfortably less than a cell.
    /// </remarks>
    public float Hysteresis { get; set; } = 0.15f;

    /// <summary>How many cells the last update wanted and had no slot for.</summary>
    public int Refused { get; private set; }

    /// <summary>Every resident cell and its slot.</summary>
    public IEnumerable<GrassSlot> Resident =>
        resident.Select(entry => new GrassSlot(entry.Key, entry.Value));

    /// <summary>Which slot a cell is in, if it is resident.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="slot">Its slot.</param>
    /// <returns>Whether it is resident.</returns>
    public bool TryGetSlot(FoliageCellKey cell, out int slot) => resident.TryGetValue(cell, out slot);

    /// <summary>Brings the ring up to date with where the view is.</summary>
    /// <param name="viewPosition">Where the view is.</param>
    /// <param name="range">How far grass is scattered, in metres.</param>
    /// <returns>What changed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The range is negative.</exception>
    /// <remarks>
    ///     The returned lists are reused between calls, which is what makes an update that changes
    ///     nothing allocate nothing — a caller keeping one has to copy it.
    /// </remarks>
    public GrassResidencyChange Update(Vector3 viewPosition, float range) {
        ArgumentOutOfRangeException.ThrowIfNegative(range);

        created.Clear();
        evicted.Clear();
        Refused = 0;

        var keep = range * (1f + MathF.Max(Hysteresis, 0f));
        var at = new Vector2(viewPosition.X, viewPosition.Z);

        // Out first, so a camera that jumped frees the slots its new neighbourhood is about to want.
        foreach (var entry in resident) {
            if (DistanceTo(entry.Key, at) > keep) {
                evicted.Add(new(entry.Key, entry.Value));
            }
        }

        foreach (var gone in evicted) {
            resident.Remove(gone.Cell);
            free.Push(gone.Slot);
        }

        // Nearest first, because that is what a capacity that runs out has to be spending its last
        // slots on. A dictionary's order would spend them wherever it happened to walk.
        var wanted = Grid.Touching(at, range)
            .Where(cell => !resident.ContainsKey(cell) && DistanceTo(cell, at) <= range)
            .OrderBy(cell => DistanceTo(cell, at))
            .ToArray();

        foreach (var cell in wanted) {
            if (free.Count == 0 && !Reclaim(cell, at)) {
                Refused = wanted.Length - created.Count;
                break;
            }

            var slot = free.Pop();

            resident[cell] = slot;
            created.Add(new(cell, slot));
        }

        return new(created, evicted);
    }

    /// <summary>Drops every cell, returning every slot.</summary>
    /// <remarks>What a change of grass types or a teleport does: nothing resident is still right.</remarks>
    public void Clear() {
        foreach (var entry in resident) {
            free.Push(entry.Value);
        }

        resident.Clear();
        created.Clear();
        evicted.Clear();
        Refused = 0;
    }

    /// <summary>How far a cell's nearest point is from a horizontal position.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="position">Where the view is, in world XZ.</param>
    /// <returns>The distance in metres, zero inside the cell.</returns>
    public float DistanceTo(FoliageCellKey cell, Vector2 position) {
        var size = Grid.CellSize;
        var low = new Vector2(cell.X * size, cell.Z * size);
        var high = low + new Vector2(size, size);

        var dx = MathF.Max(MathF.Max(low.X - position.X, 0f), position.X - high.X);
        var dz = MathF.Max(MathF.Max(low.Y - position.Y, 0f), position.Y - high.Y);

        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>
    ///     Takes a slot from the furthest resident cell, if it is further away than the candidate.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>What a camera that moved faster than the hysteresis needs.</b> Every resident cell
    ///     can be inside the eviction range and still be the wrong set — a view that swung round has
    ///     a whole new neighbourhood nearer than the one behind it. Without this the ring stays full
    ///     of what is behind the camera until the old cells drift past the eviction range, which is
    ///     grass arriving several seconds after the camera did.
    /// </remarks>
    bool Reclaim(FoliageCellKey candidate, Vector2 at) {
        var wanted = DistanceTo(candidate, at);
        var furthest = default(FoliageCellKey);
        var distance = wanted;
        var found = false;

        foreach (var entry in resident) {
            var d = DistanceTo(entry.Key, at);

            if (d > distance) {
                distance = d;
                furthest = entry.Key;
                found = true;
            }
        }

        if (!found) {
            return false;
        }

        var slot = resident[furthest];

        resident.Remove(furthest);
        free.Push(slot);
        evicted.Add(new(furthest, slot));

        return true;
    }
}

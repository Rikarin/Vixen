// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.IrradianceFields;

/// <summary>Which brick, if any, covers each cell of a world-space grid.</summary>
/// <remarks>
///     <para>
///         <b>The whole lookup is: divide, floor, fetch.</b> A world position becomes a cell, the cell
///         becomes a pool slot, and the fractional part becomes a coordinate inside the brick. Two
///         fetches and integer arithmetic, and on a GPU it is a point-sampled index texture followed
///         by a linearly-filtered pool fetch.
///     </para>
///     <para>
///         <b>This is the shape doc 06's tetrahedral light probes failed at, chosen because it cannot
///         fail the same way.</b> A Delaunay tetrahedralisation needs robust predicates, degenerates
///         on co-planar probes, and answers a "which cell am I in" question with a walk. Every one of
///         those is a way to be wrong that a grid does not have. Doc 19 § 3 makes that explicit: no
///         Delaunay, no predicates, no repeat.
///     </para>
///     <para>
///         <b>A cell here is a box, not a grid point</b> — unlike <c>MeshDistanceField</c>, where
///         samples sit <i>on</i> the lattice and the cell count is one less than the sample count.
///         Both conventions are right for what they hold and mixing them up is an off-by-half-a-cell
///         everywhere, so: <see cref="CellSize" /> divides by <see cref="Resolution" />, and the probe
///         lattice that lives <i>inside</i> a cell is the one with the grid-point convention.
///     </para>
///     <para>
///         Every cell is one brick, at one size. Refinement — bricks subdividing near geometry, which
///         doc 19 § 3 asks for — is not here yet, and when it arrives it arrives as a size stored
///         beside the slot, the way Epic's does.
///     </para>
/// </remarks>
public sealed class IrradianceIndirection {
    /// <summary>What a cell holds when no brick covers it.</summary>
    public const int Empty = -1;

    readonly int[] slots;

    /// <summary>Builds an indirection grid covering a box, with nothing in it.</summary>
    /// <param name="bounds">The box the grid covers.</param>
    /// <param name="resolution">How many cells along each axis.</param>
    /// <exception cref="ArgumentOutOfRangeException">An axis holds no cells.</exception>
    /// <exception cref="ArgumentException">The box is empty.</exception>
    public IrradianceIndirection(BoundingBox bounds, Int3 resolution) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolution.X);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolution.Y);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolution.Z);

        if (bounds.IsEmpty) {
            throw new ArgumentException("A grid over an empty box covers nothing.", nameof(bounds));
        }

        Bounds = bounds;
        Resolution = resolution;
        slots = new int[resolution.Volume];

        Array.Fill(slots, Empty);
    }

    /// <summary>The box the grid covers.</summary>
    public BoundingBox Bounds { get; }

    /// <summary>How many cells along each axis.</summary>
    public Int3 Resolution { get; }

    /// <summary>How big one cell is, in world units.</summary>
    public Vector3 CellSize =>
        Bounds.Size / new Vector3(Resolution.X, Resolution.Y, Resolution.Z);

    /// <summary>Every cell's slot, in the order a volume copy wants them.</summary>
    public ReadOnlySpan<int> Slots => slots;

    /// <summary>How many cells hold a brick.</summary>
    public int Occupancy {
        get {
            var count = 0;

            foreach (var slot in slots) {
                if (slot != Empty) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>The brick covering one cell, or <see cref="Empty" />.</summary>
    /// <param name="cell">Which cell.</param>
    /// <returns>The slot.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such cell.</exception>
    public int this[Int3 cell] {
        get => slots[Index(cell)];
        set => slots[Index(cell)] = value;
    }

    /// <summary>Whether a cell is inside the grid.</summary>
    /// <param name="cell">The cell.</param>
    /// <returns>Whether it is.</returns>
    public bool Holds(Int3 cell) =>
        cell.X >= 0 && cell.X < Resolution.X
        && cell.Y >= 0 && cell.Y < Resolution.Y
        && cell.Z >= 0 && cell.Z < Resolution.Z;

    /// <summary>The box one cell covers.</summary>
    /// <param name="cell">The cell.</param>
    /// <returns>The box.</returns>
    public BoundingBox CellBounds(Int3 cell) {
        var minimum = Bounds.Minimum + (CellSize * new Vector3(cell.X, cell.Y, cell.Z));

        return new(minimum, minimum + CellSize);
    }

    /// <summary>Which cell a world position falls in, and where in it.</summary>
    /// <param name="world">The position.</param>
    /// <param name="cell">The cell.</param>
    /// <param name="local">Where in the cell, 0 to 1 along each axis.</param>
    /// <returns>Whether the position is inside the grid at all.</returns>
    /// <remarks>
    ///     A position exactly on the far face belongs to the last cell at a local of one, rather than
    ///     to a cell that does not exist. That is not a special case so much as the reason the check
    ///     is written as a range test on the continuous coordinate and not on the floored one.
    /// </remarks>
    public bool TryCell(Vector3 world, out Int3 cell, out Vector3 local) {
        var size = CellSize;
        var offset = world - Bounds.Minimum;
        var continuous = new Vector3(
            size.X > 0 ? offset.X / size.X : 0,
            size.Y > 0 ? offset.Y / size.Y : 0,
            size.Z > 0 ? offset.Z / size.Z : 0
        );

        cell = Int3.Zero;
        local = Vector3.Zero;

        if (continuous.X < 0 || continuous.X > Resolution.X
            || continuous.Y < 0 || continuous.Y > Resolution.Y
            || continuous.Z < 0 || continuous.Z > Resolution.Z) {
            return false;
        }

        var x = Math.Clamp((int)MathF.Floor(continuous.X), 0, Resolution.X - 1);
        var y = Math.Clamp((int)MathF.Floor(continuous.Y), 0, Resolution.Y - 1);
        var z = Math.Clamp((int)MathF.Floor(continuous.Z), 0, Resolution.Z - 1);

        cell = new(x, y, z);
        local = new(
            Math.Clamp(continuous.X - x, 0f, 1f),
            Math.Clamp(continuous.Y - y, 0f, 1f),
            Math.Clamp(continuous.Z - z, 0f, 1f)
        );

        return true;
    }

    /// <summary>Which brick covers a world position, and where in it.</summary>
    /// <param name="world">The position.</param>
    /// <param name="slot">The brick.</param>
    /// <param name="local">Where in the brick, 0 to 1 along each axis.</param>
    /// <returns>Whether a brick covers it.</returns>
    public bool TryLocate(Vector3 world, out int slot, out Vector3 local) {
        slot = Empty;

        if (!TryCell(world, out var cell, out local)) {
            return false;
        }

        slot = this[cell];

        return slot != Empty;
    }

    /// <summary>Gives every cell back to nothing.</summary>
    public void Clear() => Array.Fill(slots, Empty);

    /// <summary>Where a cell lives in <see cref="Slots" />.</summary>
    /// <param name="cell">The cell.</param>
    /// <returns>The flat index.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such cell.</exception>
    internal int Index(Int3 cell) {
        if (!Holds(cell)) {
            throw new ArgumentOutOfRangeException(nameof(cell), cell, $"The grid is {Resolution} cells.");
        }

        return cell.X + (Resolution.X * (cell.Y + (Resolution.Y * cell.Z)));
    }
}

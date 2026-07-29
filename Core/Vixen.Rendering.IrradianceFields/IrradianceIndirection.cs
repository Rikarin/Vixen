// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.IrradianceFields;

/// <summary>Which brick, if any, covers each cell of a world-space grid — and how big it is.</summary>
/// <remarks>
///     <para>
///         <b>The whole lookup is: divide, floor, fetch, divide again.</b> A world position becomes a
///         cell, the cell becomes a brick and that brick's size, and the position within the brick
///         follows from the size. Two fetches and integer arithmetic, and on a GPU it is a
///         point-sampled index texture followed by a linearly-filtered pool fetch.
///     </para>
///     <para>
///         <b>This is the shape doc 06's tetrahedral light probes failed at, chosen because it cannot
///         fail the same way.</b> A Delaunay tetrahedralisation needs robust predicates, degenerates
///         on co-planar probes, and answers a "which cell am I in" question with a walk. Every one of
///         those is a way to be wrong that a grid does not have. Doc 19 § 3 makes that explicit: no
///         Delaunay, no predicates, no repeat.
///     </para>
///     <para>
///         <b>The grid is at the <i>finest</i> resolution, and a coarse brick repeats itself.</b> A
///         brick of size four writes its slot into all sixty-four cells it covers, so no lookup ever
///         searches or climbs a tree — the cost of a coarse brick is memory in this grid, which is one
///         integer pair a cell, and the saving is sixty-four probes instead of four thousand. Epic's
///         volumetric lightmap stores it exactly this way and for exactly this reason.
///     </para>
///     <para>
///         <b>A cell here is a box, not a grid point</b> — unlike <c>MeshDistanceField</c>, where
///         samples sit <i>on</i> the lattice and the cell count is one less than the sample count.
///         Both conventions are right for what they hold and mixing them up is an off-by-half-a-cell
///         everywhere, so: <see cref="CellSize" /> divides by <see cref="Resolution" />, and the probe
///         lattice that lives <i>inside</i> a brick is the one with the grid-point convention.
///     </para>
/// </remarks>
public sealed class IrradianceIndirection {
    /// <summary>What a cell holds when no brick covers it.</summary>
    public const int Empty = -1;

    readonly IrradianceCell[] cells;

    /// <summary>Builds an indirection grid covering a box, with nothing in it.</summary>
    /// <param name="bounds">The box the grid covers.</param>
    /// <param name="resolution">How many cells along each axis, at the finest brick size.</param>
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
        cells = new IrradianceCell[resolution.Volume];

        Clear();
    }

    /// <summary>The box the grid covers.</summary>
    public BoundingBox Bounds { get; }

    /// <summary>How many cells along each axis, at the finest brick size.</summary>
    public Int3 Resolution { get; }

    /// <summary>How big one finest cell is, in world units.</summary>
    public Vector3 CellSize =>
        Bounds.Size / new Vector3(Resolution.X, Resolution.Y, Resolution.Z);

    /// <summary>Every cell, in the order a volume copy wants them.</summary>
    public ReadOnlySpan<IrradianceCell> Cells => cells;

    /// <summary>How many cells a brick covers.</summary>
    public int Covered {
        get {
            var count = 0;

            foreach (var cell in cells) {
                if (cell.HasBrick) {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>How many bricks there are, counting a coarse one once.</summary>
    public int BrickCount {
        get {
            var count = 0;

            for (var z = 0; z < Resolution.Z; z++) {
                for (var y = 0; y < Resolution.Y; y++) {
                    for (var x = 0; x < Resolution.X; x++) {
                        if (IsOrigin(new(x, y, z))) {
                            count++;
                        }
                    }
                }
            }

            return count;
        }
    }

    /// <summary>What one cell holds.</summary>
    /// <param name="cell">Which cell.</param>
    /// <returns>Its brick and that brick's size.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such cell.</exception>
    public IrradianceCell this[Int3 cell] {
        get => cells[Index(cell)];
        set => cells[Index(cell)] = value;
    }

    /// <summary>Whether a cell is the origin of the brick covering it.</summary>
    /// <param name="cell">The cell.</param>
    /// <returns>Whether it is.</returns>
    /// <remarks>
    ///     How a brick is counted or enumerated exactly once when every cell it covers names it. The
    ///     alternative — a separate list of bricks — is a second source of truth about the same thing,
    ///     and the two disagree the first time an allocation fails halfway.
    /// </remarks>
    public bool IsOrigin(Int3 cell) {
        var entry = this[cell];

        return entry.HasBrick && Origin(cell, entry.Size) == cell;
    }

    /// <summary>The brick covering a cell, if one does.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="brick">The brick.</param>
    /// <returns>Whether one covers it.</returns>
    public bool TryBrick(Int3 cell, out IrradianceBrick brick) {
        var entry = this[cell];

        brick = entry.HasBrick
            ? new(entry.Slot, Origin(cell, entry.Size), entry.Size)
            : default;

        return entry.HasBrick;
    }

    /// <summary>Writes a brick into every cell it covers.</summary>
    /// <param name="brick">The brick, already aligned to its size.</param>
    /// <exception cref="ArgumentOutOfRangeException">The size is not a positive power of two.</exception>
    /// <exception cref="ArgumentException">The brick is not aligned to its own size.</exception>
    /// <remarks>
    ///     Cells beyond the edge of the grid are skipped rather than refused, so a grid whose
    ///     resolution is not a multiple of the brick size still works — the brick hangs over, and the
    ///     part hanging over is outside <see cref="Bounds" />, where nothing samples.
    /// </remarks>
    public void Assign(IrradianceBrick brick) {
        Aligned(brick.Cell, brick.Size);

        var entry = new IrradianceCell(brick.Slot, brick.Size);

        Stamp(brick.Cell, brick.Size, entry);
    }

    /// <summary>Empties every cell a brick covers.</summary>
    /// <param name="brick">The brick.</param>
    /// <exception cref="ArgumentOutOfRangeException">The size is not a positive power of two.</exception>
    /// <exception cref="ArgumentException">The brick is not aligned to its own size.</exception>
    public void Revoke(IrradianceBrick brick) {
        Aligned(brick.Cell, brick.Size);
        Stamp(brick.Cell, brick.Size, IrradianceCell.Empty);
    }

    /// <summary>Whether a cell is inside the grid.</summary>
    /// <param name="cell">The cell.</param>
    /// <returns>Whether it is.</returns>
    public bool Holds(Int3 cell) =>
        cell.X >= 0 && cell.X < Resolution.X
        && cell.Y >= 0 && cell.Y < Resolution.Y
        && cell.Z >= 0 && cell.Z < Resolution.Z;

    /// <summary>The box one finest cell covers.</summary>
    /// <param name="cell">The cell.</param>
    /// <returns>The box.</returns>
    public BoundingBox CellBounds(Int3 cell) {
        var minimum = Bounds.Minimum + (CellSize * new Vector3(cell.X, cell.Y, cell.Z));

        return new(minimum, minimum + CellSize);
    }

    /// <summary>Where a world position sits in the grid, measured in finest cells.</summary>
    /// <param name="world">The position.</param>
    /// <param name="voxel">The continuous coordinate, in cells.</param>
    /// <returns>Whether the position is inside the grid at all.</returns>
    /// <remarks>
    ///     A position exactly on the far face is inside, at a coordinate of exactly the resolution.
    ///     That is not a special case so much as the reason the test is written on the continuous
    ///     coordinate and not on the floored one.
    /// </remarks>
    public bool TryVoxel(Vector3 world, out Vector3 voxel) {
        var size = CellSize;
        var offset = world - Bounds.Minimum;

        voxel = new(
            size.X > 0 ? offset.X / size.X : 0,
            size.Y > 0 ? offset.Y / size.Y : 0,
            size.Z > 0 ? offset.Z / size.Z : 0
        );

        return voxel.X >= 0 && voxel.X <= Resolution.X
            && voxel.Y >= 0 && voxel.Y <= Resolution.Y
            && voxel.Z >= 0 && voxel.Z <= Resolution.Z;
    }

    /// <summary>Which cell a world position falls in.</summary>
    /// <param name="world">The position.</param>
    /// <param name="cell">The cell.</param>
    /// <returns>Whether the position is inside the grid at all.</returns>
    public bool TryCell(Vector3 world, out Int3 cell) {
        cell = Int3.Zero;

        if (!TryVoxel(world, out var voxel)) {
            return false;
        }

        cell = Clamped(voxel);

        return true;
    }

    /// <summary>Which brick covers a world position, and where in it.</summary>
    /// <param name="world">The position.</param>
    /// <param name="brick">The brick.</param>
    /// <param name="local">Where in the brick, 0 to 1 along each axis.</param>
    /// <returns>Whether a brick covers it.</returns>
    /// <remarks>
    ///     The position within the brick is measured from the brick's own origin rather than taken as
    ///     the fractional part of a division. The two agree everywhere they are both defined, and the
    ///     explicit form also answers correctly on the grid's far face — where the coordinate is a
    ///     whole cell count and a fractional part would be zero, putting a sample at the near face of a
    ///     brick it is at the far face of.
    /// </remarks>
    public bool TryLocate(Vector3 world, out IrradianceBrick brick, out Vector3 local) {
        brick = default;
        local = Vector3.Zero;

        if (!TryVoxel(world, out var voxel)) {
            return false;
        }

        if (!TryBrick(Clamped(voxel), out brick)) {
            return false;
        }

        var origin = new Vector3(brick.Cell.X, brick.Cell.Y, brick.Cell.Z);

        local = Vector3.Clamp((voxel - origin) / brick.Size, Vector3.Zero, Vector3.One);

        return true;
    }

    /// <summary>Gives every cell back to nothing.</summary>
    public void Clear() => Array.Fill(cells, IrradianceCell.Empty);

    /// <summary>The origin of the brick of a given size covering a cell.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="size">The brick's size.</param>
    /// <returns>The origin.</returns>
    public static Int3 Origin(Int3 cell, int size) =>
        new(cell.X / size * size, cell.Y / size * size, cell.Z / size * size);

    /// <summary>Where a cell lives in <see cref="Cells" />.</summary>
    /// <param name="cell">The cell.</param>
    /// <returns>The flat index.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such cell.</exception>
    internal int Index(Int3 cell) {
        if (!Holds(cell)) {
            throw new ArgumentOutOfRangeException(nameof(cell), cell, $"The grid is {Resolution} cells.");
        }

        return cell.X + (Resolution.X * (cell.Y + (Resolution.Y * cell.Z)));
    }

    /// <summary>Writes one entry into every cell of a cube, skipping what falls outside the grid.</summary>
    void Stamp(Int3 origin, int size, IrradianceCell entry) {
        for (var z = origin.Z; z < origin.Z + size; z++) {
            for (var y = origin.Y; y < origin.Y + size; y++) {
                for (var x = origin.X; x < origin.X + size; x++) {
                    var cell = new Int3(x, y, z);

                    if (Holds(cell)) {
                        cells[Index(cell)] = entry;
                    }
                }
            }
        }
    }

    /// <summary>Throws unless a brick of that size may start at that cell.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The size is not a positive power of two.</exception>
    /// <exception cref="ArgumentException">The cell is not a multiple of the size.</exception>
    static void Aligned(Int3 cell, int size) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        if ((size & (size - 1)) != 0) {
            throw new ArgumentOutOfRangeException(nameof(size), size, "A brick's size has to be a power of two.");
        }

        if (Origin(cell, size) != cell) {
            throw new ArgumentException(
                $"A brick of size {size} cannot start at {cell} — dividing a cell coordinate by the "
                + "size only gives a position inside the brick when the brick is aligned to it.",
                nameof(cell)
            );
        }
    }

    /// <summary>A continuous coordinate as a cell of the grid, with the far face inside.</summary>
    Int3 Clamped(Vector3 voxel) =>
        new(
            Math.Clamp((int)MathF.Floor(voxel.X), 0, Resolution.X - 1),
            Math.Clamp((int)MathF.Floor(voxel.Y), 0, Resolution.Y - 1),
            Math.Clamp((int)MathF.Floor(voxel.Z), 0, Resolution.Z - 1)
        );
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.IrradianceFields;

/// <summary>An indirection grid, a pool of bricks, and the one way to read them.</summary>
/// <remarks>
///     <para>
///         <b>This is the storage and sampling layer of <c>docs/plan/19</c> § 3, and its defining
///         property is that it does not know what filled it.</b> A brick is a brick whether a compute
///         shader traced rays into it this frame or an offline cube capture wrote it at build time.
///         That is what lets doc 19 § 7 promise a phone and a desktop the same lighting model at
///         different update rates: nothing above this branches on which filler ran.
///     </para>
///     <para>
///         <b>Borders are maintained, not written.</b> A filler fills the sixty-four probes a brick
///         owns; <see cref="SyncBorders" /> copies each neighbour's first probe into the border plane
///         that stands in for it. The border is not data — it is a second copy of somebody else's
///         data, which is why <see cref="SetProbe" /> refuses to address it.
///     </para>
///     <para>
///         <b>Probes sit on a lattice that spans the whole field, not one per brick.</b> Brick
///         <c>c</c>'s probe 4 and brick <c>c+1</c>'s probe 0 are the same world position, so a sample
///         walking across the boundary reads the same two numbers from either side and the
///         interpolation is continuous. A test says so by reproducing a linear function <i>exactly</i>
///         across a boundary, which trilinear interpolation does and a seam does not.
///     </para>
///     <para>
///         Not here yet, and named so their absence is a decision: refinement (bricks subdividing near
///         geometry), dilation into invalid probes, and the normal and view biases. All three are
///         leak mitigation, doc 19 carries leaks as risk G3, and all three land in this phase.
///     </para>
/// </remarks>
public sealed class IrradianceField {
    /// <summary>Builds a field over a box, with an indirection grid and a pool for it.</summary>
    /// <param name="bounds">The box the field covers.</param>
    /// <param name="cells">How many bricks along each axis.</param>
    /// <param name="pool">Where the bricks live. One sized to hold them all by default.</param>
    public IrradianceField(BoundingBox bounds, Int3 cells, IrradianceBrickPool? pool = null) {
        Indirection = new(bounds, cells);
        Pool = pool ?? IrradianceBrickPool.OfCapacity(checked((int)cells.Volume));
    }

    /// <summary>Which brick covers each cell.</summary>
    public IrradianceIndirection Indirection { get; }

    /// <summary>Where the bricks live.</summary>
    public IrradianceBrickPool Pool { get; }

    /// <summary>The box the field covers.</summary>
    public BoundingBox Bounds => Indirection.Bounds;

    /// <summary>How far apart two neighbouring probes are, in world units.</summary>
    /// <remarks>
    ///     A quarter of a cell, not a fifth. The fifth plane of a brick is the neighbour's first
    ///     probe rather than one of its own, so a brick spans four gaps and not five — and this is the
    ///     number a filler needs when it decides how far to push a sample off a surface.
    /// </remarks>
    public Vector3 ProbeSpacing => Indirection.CellSize / IrradianceBrickPool.BrickResolution;

    /// <summary>Gives a cell a brick, or answers with the one it already has.</summary>
    /// <param name="cell">Which cell.</param>
    /// <param name="slot">The brick covering it.</param>
    /// <returns>Whether it has one — false only when the pool is full.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such cell.</exception>
    public bool TryAllocate(Int3 cell, out int slot) {
        slot = Indirection[cell];

        if (slot != IrradianceIndirection.Empty) {
            return true;
        }

        if (!Pool.TryAllocate(out slot)) {
            slot = IrradianceIndirection.Empty;

            return false;
        }

        Indirection[cell] = slot;

        return true;
    }

    /// <summary>Gives every cell a brick, as far as the pool goes.</summary>
    /// <returns>How many cells got one.</returns>
    /// <remarks>
    ///     What a field with no refinement wants, and what a test wants. A real scene allocates from
    ///     renderer bounds so empty air costs nothing — which is the refinement this does not do yet.
    /// </remarks>
    public int AllocateAll() {
        var resolution = Indirection.Resolution;
        var allocated = 0;

        for (var z = 0; z < resolution.Z; z++) {
            for (var y = 0; y < resolution.Y; y++) {
                for (var x = 0; x < resolution.X; x++) {
                    if (TryAllocate(new(x, y, z), out _)) {
                        allocated++;
                    }
                }
            }
        }

        return allocated;
    }

    /// <summary>Gives every cell overlapping a box a brick.</summary>
    /// <param name="region">The box, in world space.</param>
    /// <returns>How many cells got one.</returns>
    /// <remarks>
    ///     The whole allocation policy for now: cover what something is in, leave the rest empty. A
    ///     caller passes a renderer's world bounds grown by a probe spacing or two, because a surface
    ///     needs probes on <i>both</i> sides of it to be interpolated between.
    /// </remarks>
    public int Allocate(BoundingBox region) {
        if (!Indirection.Bounds.Intersects(region)) {
            return 0;
        }

        var size = Indirection.CellSize;
        var offset = Indirection.Bounds.Minimum;
        var resolution = Indirection.Resolution;
        var allocated = 0;

        var low = (region.Minimum - offset) / size;
        var high = (region.Maximum - offset) / size;

        var x0 = Math.Clamp((int)MathF.Floor(low.X), 0, resolution.X - 1);
        var y0 = Math.Clamp((int)MathF.Floor(low.Y), 0, resolution.Y - 1);
        var z0 = Math.Clamp((int)MathF.Floor(low.Z), 0, resolution.Z - 1);
        var x1 = Math.Clamp((int)MathF.Ceiling(high.X) - 1, x0, resolution.X - 1);
        var y1 = Math.Clamp((int)MathF.Ceiling(high.Y) - 1, y0, resolution.Y - 1);
        var z1 = Math.Clamp((int)MathF.Ceiling(high.Z) - 1, z0, resolution.Z - 1);

        for (var z = z0; z <= z1; z++) {
            for (var y = y0; y <= y1; y++) {
                for (var x = x0; x <= x1; x++) {
                    if (TryAllocate(new(x, y, z), out _)) {
                        allocated++;
                    }
                }
            }
        }

        return allocated;
    }

    /// <summary>Takes a cell's brick away and gives the slot back.</summary>
    /// <param name="cell">Which cell.</param>
    /// <returns>Whether it had one.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such cell.</exception>
    public bool Release(Int3 cell) {
        var slot = Indirection[cell];

        if (slot == IrradianceIndirection.Empty) {
            return false;
        }

        Indirection[cell] = IrradianceIndirection.Empty;
        Pool.Release(slot);

        return true;
    }

    /// <summary>Empties the field of every brick.</summary>
    public void Clear() {
        Indirection.Clear();
        Pool.Clear();
    }

    /// <summary>Where one probe of one brick is, in world space.</summary>
    /// <param name="cell">Which brick.</param>
    /// <param name="x">The probe's index along X, 0 to 4.</param>
    /// <param name="y">Along Y.</param>
    /// <param name="z">Along Z.</param>
    /// <returns>The position.</returns>
    /// <remarks>
    ///     Four is allowed and is the point: it is the same world position as the next brick's zero,
    ///     which is what makes the border a copy rather than an estimate. A test compares the two and
    ///     expects them equal.
    /// </remarks>
    public Vector3 ProbePosition(Int3 cell, int x, int y, int z) =>
        Indirection.Bounds.Minimum
        + (Indirection.CellSize * new Vector3(cell.X, cell.Y, cell.Z))
        + (ProbeSpacing * new Vector3(x, y, z));

    /// <summary>Writes one of the sixty-four probes a brick owns.</summary>
    /// <param name="cell">Which brick.</param>
    /// <param name="x">The probe's index along X, 0 to 3.</param>
    /// <param name="y">Along Y.</param>
    /// <param name="z">Along Z.</param>
    /// <param name="probe">What it saw.</param>
    /// <exception cref="ArgumentOutOfRangeException">The cell or a coordinate is out of range.</exception>
    /// <exception cref="InvalidOperationException">No brick covers that cell.</exception>
    /// <remarks>
    ///     <b>Three, not four.</b> The fourth plane is a border, borders belong to
    ///     <see cref="SyncBorders" />, and a filler that wrote one would be writing an answer where a
    ///     copy of the neighbour's answer has to go — a seam that appears only where two bricks
    ///     disagree, which is exactly where a seam is visible.
    /// </remarks>
    public void SetProbe(Int3 cell, int x, int y, int z, IrradianceProbe probe) {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, IrradianceBrickPool.BrickResolution);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, IrradianceBrickPool.BrickResolution);
        ArgumentOutOfRangeException.ThrowIfNegative(z);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(z, IrradianceBrickPool.BrickResolution);

        Pool[SlotOf(cell), x, y, z] = probe;
    }

    /// <summary>Reads one texel of a brick, border planes included.</summary>
    /// <param name="cell">Which brick.</param>
    /// <param name="x">The texel's index along X, 0 to 4.</param>
    /// <param name="y">Along Y.</param>
    /// <param name="z">Along Z.</param>
    /// <returns>The probe.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The cell or a coordinate is out of range.</exception>
    /// <exception cref="InvalidOperationException">No brick covers that cell.</exception>
    public IrradianceProbe GetProbe(Int3 cell, int x, int y, int z) => Pool[SlotOf(cell), x, y, z];

    /// <summary>Fills every brick's border planes from its neighbours.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Run this after a filler and before a sample, or the field has seams in it.</b> Each
    ///         of the three positive faces of a brick — and the edges and the corner where they meet —
    ///         holds the probe of the brick beyond it, at the same world position. Copying it is what
    ///         makes a single trilinear fetch legal across a boundary, and it is a copy rather than an
    ///         interpolation because the two positions are identical.
    ///     </para>
    ///     <para>
    ///         <b>At the edge of the field, and beside a cell with no brick, a border repeats the
    ///         brick's own last probe.</b> There is nothing beyond to copy, and repeating is a constant
    ///         extrapolation — the lighting stops changing rather than falling to black, which is what
    ///         the alternative looks like: a dark rind one probe thick around everything.
    ///     </para>
    /// </remarks>
    public void SyncBorders() {
        var resolution = Indirection.Resolution;
        const int last = IrradianceBrickPool.BrickResolution;

        for (var cz = 0; cz < resolution.Z; cz++) {
            for (var cy = 0; cy < resolution.Y; cy++) {
                for (var cx = 0; cx < resolution.X; cx++) {
                    var cell = new Int3(cx, cy, cz);
                    var slot = Indirection[cell];

                    if (slot == IrradianceIndirection.Empty) {
                        continue;
                    }

                    for (var z = 0; z <= last; z++) {
                        for (var y = 0; y <= last; y++) {
                            for (var x = 0; x <= last; x++) {
                                if (x < last && y < last && z < last) {
                                    continue;
                                }

                                Pool[slot, x, y, z] = Borrowed(cell, slot, x, y, z);
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>The probe a field holds at a world position, or nothing where no brick does.</summary>
    /// <param name="world">The position.</param>
    /// <param name="probe">What is there.</param>
    /// <returns>Whether a brick covers it.</returns>
    public bool TrySample(Vector3 world, out IrradianceProbe probe) {
        if (!Indirection.TryLocate(world, out var slot, out var local)) {
            probe = IrradianceProbe.Empty;

            return false;
        }

        probe = Pool.Sample(slot, local);

        return true;
    }

    /// <summary>The diffuse lighting a surface at a position receives, divided by π.</summary>
    /// <param name="world">Where the surface is.</param>
    /// <param name="normal">Which way it faces, normalised.</param>
    /// <returns>The irradiance over π, or zero where the field has nothing.</returns>
    /// <remarks>
    ///     Zero outside the field rather than the nearest brick's answer. A field that quietly
    ///     extrapolates hides the fact that it did not cover the scene, and covering the scene is the
    ///     caller's decision to get right — a sky light or a fallback ambient is what fills the gap,
    ///     and it should be visible that it is doing so.
    /// </remarks>
    public Vector3 Irradiance(Vector3 world, Vector3 normal) =>
        TrySample(world, out var probe) ? probe.Irradiance(normal) : Vector3.Zero;

    /// <summary>What a border texel should hold.</summary>
    /// <param name="cell">The brick's cell.</param>
    /// <param name="slot">The brick.</param>
    /// <param name="x">The texel's index along X.</param>
    /// <param name="y">Along Y.</param>
    /// <param name="z">Along Z.</param>
    /// <returns>The neighbour's probe, or this brick's own last one where there is no neighbour.</returns>
    IrradianceProbe Borrowed(Int3 cell, int slot, int x, int y, int z) {
        const int last = IrradianceBrickPool.BrickResolution;

        var neighbour = new Int3(
            cell.X + (x == last ? 1 : 0),
            cell.Y + (y == last ? 1 : 0),
            cell.Z + (z == last ? 1 : 0)
        );

        if (Indirection.Holds(neighbour)) {
            var beyond = Indirection[neighbour];

            if (beyond != IrradianceIndirection.Empty) {
                return Pool[beyond, x == last ? 0 : x, y == last ? 0 : y, z == last ? 0 : z];
            }
        }

        return Pool[slot, Math.Min(x, last - 1), Math.Min(y, last - 1), Math.Min(z, last - 1)];
    }

    /// <summary>The brick covering a cell.</summary>
    /// <param name="cell">The cell.</param>
    /// <returns>The slot.</returns>
    /// <exception cref="InvalidOperationException">No brick covers it.</exception>
    int SlotOf(Int3 cell) {
        var slot = Indirection[cell];

        if (slot == IrradianceIndirection.Empty) {
            throw new InvalidOperationException($"Cell {cell} has no brick. Allocate one first.");
        }

        return slot;
    }
}

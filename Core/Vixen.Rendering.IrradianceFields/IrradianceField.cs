// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
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

    /// <summary>How far off a surface a shading lookup moves, in probe spacings.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A surface stands exactly where the leak is.</b> Its own position is on the boundary
    ///         between the probes that saw the room and the probes that are inside the wall it is part
    ///         of, so a lookup there splits the difference between them. Pushing the lookup along the
    ///         normal — a quarter of a probe spacing, which is Unity's and Epic's number too — puts it
    ///         on the side of the wall the surface faces, where the answer is unambiguous.
    ///     </para>
    ///     <para>
    ///         <b>It is a tuning constant that has to match the shader's</b>, which is why it lives on
    ///         the field rather than in a caller. Too little and the rind comes back; too much and a
    ///         surface reads the lighting of somewhere it is not, which shows on anything thinner than
    ///         the bias. It does not fix a wall thinner than a probe spacing, and nothing at this end
    ///         does — see the README.
    ///     </para>
    /// </remarks>
    public float NormalBias { get; set; } = 0.25f;

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

    /// <summary>The probe a surface sees, biased off the surface it stands on.</summary>
    /// <param name="world">Where the surface is.</param>
    /// <param name="normal">Which way it faces, normalised.</param>
    /// <param name="probe">What is there.</param>
    /// <returns>Whether a brick covers the biased position.</returns>
    /// <remarks>
    ///     What shading calls, where <see cref="TrySample(Vector3, out IrradianceProbe)" /> is the raw
    ///     lookup. See <see cref="NormalBias" /> for why the two are different functions.
    /// </remarks>
    public bool TrySample(Vector3 world, Vector3 normal, out IrradianceProbe probe) =>
        TrySample(world + (normal * ProbeSpacing * NormalBias), out probe);

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
        TrySample(world, normal, out var probe) ? probe.Irradiance(normal) : Vector3.Zero;

    /// <summary>Replaces probes nothing could fill with what their neighbours saw.</summary>
    /// <param name="passes">How far the replacement may travel, in probes.</param>
    /// <returns>How many probes were repaired.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A negative number of passes.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>A probe inside a wall holds nothing, and nothing is a colour.</b> Trilinear
    ///         interpolation does not know it should skip that probe, so every surface within a probe
    ///         spacing of a wall reads part of it and comes out dark — a rind one probe thick around
    ///         everything, which is the artefact people describe as "the GI looks dirty". Filling
    ///         invalid probes from their valid neighbours is what removes it, and it is a fill rather
    ///         than a sample-time weighting for a reason: doc 19 § 3 commits to <i>one</i> trilinear
    ///         fetch, and a weighted skip needs eight taps and cannot be one.
    ///     </para>
    ///     <para>
    ///         <b>Only invalid probes are written, and that — not the pass count — is what stops light
    ///         walking through a wall.</b> A probe repaired in one pass is valid in the next and is
    ///         never revisited, so each face of a wall repairs inward from its own side and the two
    ///         meet without mixing. More passes reach further into solid geometry, where nothing
    ///         samples anyway; they do not carry the outside's light any further in.
    ///     </para>
    ///     <para>
    ///         <b>The failure case is a wall exactly one probe thick.</b> Then a single plane of
    ///         invalid probes touches the room on one side and the outside on the other, and its
    ///         repair is the average of the two — which is a leak, at full strength, in one pass. A
    ///         wall thinner than the probe spacing is worse still: it holds no invalid probes at all,
    ///         so there is nothing here to repair and nothing to notice. Both are doc 19's risk G3,
    ///         and the fix for both is refinement — finer bricks near geometry, so a wall is more
    ///         probes thick.
    ///     </para>
    ///     <para>
    ///         Run it before <see cref="SyncBorders" />: borders are copies, and copying before the
    ///         original is repaired copies the hole.
    ///     </para>
    /// </remarks>
    public int Dilate(int passes = 1) {
        ArgumentOutOfRangeException.ThrowIfNegative(passes);

        var lattice = LatticeResolution;
        var pending = new List<(Int3 At, IrradianceProbe Probe)>();
        var repaired = 0;

        for (var pass = 0; pass < passes; pass++) {
            pending.Clear();

            for (var z = 0; z < lattice.Z; z++) {
                for (var y = 0; y < lattice.Y; y++) {
                    for (var x = 0; x < lattice.X; x++) {
                        var at = new Int3(x, y, z);

                        if (!TryGetLattice(at, out var probe) || probe.Validity > 0f) {
                            continue;
                        }

                        if (TryBorrowFromNeighbours(at, out var repair)) {
                            pending.Add((at, repair));
                        }
                    }
                }
            }

            if (pending.Count == 0) {
                break;
            }

            // Applied after the whole pass, not during it, so a repair cannot feed the probe next to
            // it in the same sweep — which would make the result depend on which way the loops run.
            foreach (var (at, probe) in pending) {
                SetLattice(at, probe);
            }

            repaired += pending.Count;
        }

        return repaired;
    }

    /// <summary>How many probes the field holds along each axis, over every brick.</summary>
    /// <remarks>
    ///     Four per brick, not five: the fifth plane is the next brick's first probe, so counting it
    ///     would count every interior probe twice. A filler walks this rather than walking bricks —
    ///     probes are what it fills, and which brick one lives in is storage's business.
    /// </remarks>
    public Int3 LatticeResolution => Indirection.Resolution * IrradianceBrickPool.BrickResolution;

    /// <summary>Where one probe of the whole field's lattice stands.</summary>
    /// <param name="lattice">Its coordinate.</param>
    /// <returns>The position.</returns>
    public Vector3 LatticePosition(Int3 lattice) =>
        Indirection.Bounds.Minimum + (ProbeSpacing * new Vector3(lattice.X, lattice.Y, lattice.Z));

    /// <summary>Whether a lattice coordinate is one the field has.</summary>
    /// <param name="lattice">The coordinate.</param>
    /// <returns>Whether it is.</returns>
    public bool HoldsLattice(Int3 lattice) {
        var resolution = LatticeResolution;

        return lattice.X >= 0 && lattice.X < resolution.X
            && lattice.Y >= 0 && lattice.Y < resolution.Y
            && lattice.Z >= 0 && lattice.Z < resolution.Z;
    }

    /// <summary>Reads one probe of the whole field's lattice.</summary>
    /// <param name="lattice">Its coordinate.</param>
    /// <param name="probe">What it holds.</param>
    /// <returns>Whether the field has that probe and a brick to hold it.</returns>
    public bool TryGetLattice(Int3 lattice, out IrradianceProbe probe) {
        probe = IrradianceProbe.Empty;

        if (!HoldsLattice(lattice)) {
            return false;
        }

        var slot = Indirection[CellOf(lattice)];

        if (slot == IrradianceIndirection.Empty) {
            return false;
        }

        probe = Pool[slot, Within(lattice.X), Within(lattice.Y), Within(lattice.Z)];

        return true;
    }

    /// <summary>Writes one probe of the whole field's lattice.</summary>
    /// <param name="lattice">Its coordinate.</param>
    /// <param name="probe">What it saw.</param>
    /// <exception cref="ArgumentOutOfRangeException">The field has no such probe.</exception>
    /// <exception cref="InvalidOperationException">No brick covers it.</exception>
    public void SetLattice(Int3 lattice, IrradianceProbe probe) {
        if (!HoldsLattice(lattice)) {
            throw new ArgumentOutOfRangeException(
                nameof(lattice),
                lattice,
                $"The field's lattice is {LatticeResolution} probes."
            );
        }

        SetProbe(CellOf(lattice), Within(lattice.X), Within(lattice.Y), Within(lattice.Z), probe);
    }

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

    /// <summary>The average of a probe's valid neighbours, if it has any.</summary>
    /// <param name="at">Where the probe is.</param>
    /// <param name="repair">What it should hold instead.</param>
    /// <returns>Whether anything nearby was worth copying.</returns>
    /// <remarks>
    ///     The six face neighbours, not all twenty-six. A diagonal neighbour is further away and its
    ///     path to here passes through the two faces between them, so a corner probe buried in a wall
    ///     would pull from a room it has no straight line to. Six is also what makes a pass mean "one
    ///     probe of travel", which is what makes the pass count a distance and therefore a knob
    ///     somebody can reason about.
    /// </remarks>
    bool TryBorrowFromNeighbours(Int3 at, out IrradianceProbe repair) {
        ReadOnlySpan<Int3> directions = [
            new(1, 0, 0), new(-1, 0, 0),
            new(0, 1, 0), new(0, -1, 0),
            new(0, 0, 1), new(0, 0, -1)
        ];

        var radiance = SphericalHarmonicsL1.Zero;
        var shadow = 0f;
        var weight = 0f;
        var contributors = 0;

        foreach (var direction in directions) {
            if (!TryGetLattice(at + direction, out var other) || other.Validity <= 0f) {
                continue;
            }

            radiance = Sum(radiance, other.Radiance.Scaled(other.Validity));
            shadow += other.SunShadow * other.Validity;
            weight += other.Validity;
            contributors++;
        }

        if (contributors == 0) {
            repair = IrradianceProbe.Empty;

            return false;
        }

        // Divided by the weight rather than by the count, so a half-believed neighbour contributes
        // half as much light and not half as much darkness. Validity itself is the plain mean, which
        // is what makes a probe repaired from one uncertain neighbour stay uncertain.
        repair = new(radiance.Scaled(1f / weight), weight / contributors, shadow / weight);

        return true;
    }

    /// <summary>Which brick a lattice coordinate belongs to.</summary>
    /// <param name="lattice">The coordinate.</param>
    /// <returns>The cell.</returns>
    static Int3 CellOf(Int3 lattice) => lattice / IrradianceBrickPool.BrickResolution;

    /// <summary>Where a lattice coordinate sits inside its own brick.</summary>
    /// <param name="coordinate">One axis of it.</param>
    /// <returns>The probe index, 0 to 3.</returns>
    static int Within(int coordinate) => coordinate % IrradianceBrickPool.BrickResolution;

    /// <summary>Two projections added, which is the projection of the two together.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>The sum.</returns>
    static SphericalHarmonicsL1 Sum(SphericalHarmonicsL1 left, SphericalHarmonicsL1 right) =>
        new(
            left.L00 + right.L00,
            left.L1m1 + right.L1m1,
            left.L10 + right.L10,
            left.L11 + right.L11
        );

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

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
///         <b>Bricks come in sizes, and every one of them holds sixty-four probes.</b> A brick of size
///         eight covers five hundred and twelve times the volume of a brick of size one for the same
///         memory, and its probes are eight times further apart. Refining near geometry — coarse where
///         there is nothing to shade, fine where a wall is — is what makes the memory affordable
///         <i>and</i> what makes a wall more than one probe thick, which is the only real defence
///         against a leak.
///     </para>
///     <para>
///         <b>Borders are maintained, not written.</b> A filler fills the sixty-four probes a brick
///         owns; <see cref="SyncBorders" /> fills the plane that stands in for its neighbour. The
///         border is not data — it is a second copy of somebody else's data, which is why
///         <see cref="SetProbe" /> refuses to address it.
///     </para>
///     <para>
///         <b>There is no field-wide probe lattice, and there cannot be one.</b> Two bricks of
///         different sizes have probes at different spacings, so "the probe next door" is a question
///         about world positions rather than about indices. Everything that walks probes — dilation,
///         a filler — walks bricks and asks the field where a position lands.
///     </para>
/// </remarks>
public sealed class IrradianceField {
    /// <summary>Border values computed but not yet written, so a sync cannot read its own output.</summary>
    readonly List<(int Slot, int X, int Y, int Z, IrradianceProbe Probe)> deferred = [];

    /// <summary>Builds a field over a box, with an indirection grid and a pool for it.</summary>
    /// <param name="bounds">The box the field covers.</param>
    /// <param name="cells">How many cells along each axis, at the finest brick size.</param>
    /// <param name="pool">Where the bricks live. One sized for an entirely fine field by default.</param>
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

    /// <summary>How many bricks there are, counting a coarse one once.</summary>
    public int BrickCount => Indirection.BrickCount;

    /// <summary>Every brick, once each, in the order the indirection grid holds them.</summary>
    /// <remarks>
    ///     Enumerated off the grid rather than kept in a list beside it. A list is a second source of
    ///     truth about the same thing, and the two disagree the first time an allocation fails
    ///     halfway through a split.
    /// </remarks>
    public IEnumerable<IrradianceBrick> Bricks {
        get {
            var resolution = Indirection.Resolution;

            for (var z = 0; z < resolution.Z; z++) {
                for (var y = 0; y < resolution.Y; y++) {
                    for (var x = 0; x < resolution.X; x++) {
                        var cell = new Int3(x, y, z);

                        if (Indirection.IsOrigin(cell) && Indirection.TryBrick(cell, out var brick)) {
                            yield return brick;
                        }
                    }
                }
            }
        }
    }

    /// <summary>How far apart two neighbouring probes of a brick of a given size are.</summary>
    /// <param name="size">The brick's size, in finest cells.</param>
    /// <returns>The spacing, in world units.</returns>
    /// <remarks>
    ///     A quarter of the brick, not a fifth. The fifth plane holds the neighbour's first probe
    ///     rather than one of its own, so a brick spans four gaps and not five — and this is the
    ///     number a filler needs when it decides how far to push a sample off a surface.
    /// </remarks>
    public Vector3 ProbeSpacingOf(int size) =>
        Indirection.CellSize * size / IrradianceBrickPool.BrickResolution;

    /// <summary>How far apart two probes of the finest brick there could be are.</summary>
    public Vector3 FinestProbeSpacing => ProbeSpacingOf(1);

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
    ///         <b>In probe spacings, not world units, and the spacing is the brick's own.</b> A coarse
    ///         brick's probes are further apart and its ambiguity is correspondingly wider, so a fixed
    ///         distance would be too small out there and too large in a refined region. That costs one
    ///         extra indirection fetch: where the surface is, to learn the size, then where the biased
    ///         lookup lands.
    ///     </para>
    ///     <para>
    ///         It is a tuning constant that has to match the shader's, which is why it lives on the
    ///         field rather than in a caller. It does not fix a wall thinner than a probe spacing, and
    ///         nothing at this end does — see the README.
    ///     </para>
    /// </remarks>
    public float NormalBias { get; set; } = 0.25f;

    /// <summary>How far toward the camera a shading lookup moves as well, in probe spacings.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The half the normal cannot do.</b> At a grazing angle the surface normal is nearly
    ///         perpendicular to the view ray, so a step along it barely leaves the surface it is trying
    ///         to leave — and a grazing angle is exactly where a floor's lookup slides under the wall
    ///         beside it. Stepping toward the camera does leave, because <b>the space between a visible
    ///         surface and the eye looking at it is empty by construction</b>: something opaque in it
    ///         would be what was shaded instead.
    ///     </para>
    ///     <para>
    ///         That argument is the whole justification, and it is why this is not simply a larger
    ///         normal bias. It is also why the two are separate numbers rather than one: they answer
    ///         different geometry, and a scene that leaks at grazing angles and not head-on is telling
    ///         you which one to raise.
    ///     </para>
    ///     <para>
    ///         In probe spacings and the brick's own, like <see cref="NormalBias" />, and a quarter for
    ///         the same reason — far enough to cross the ambiguity, near enough not to reach the probe
    ///         after next. ⚠ It is a tuning constant matched to the shader's, not a derived quantity;
    ///         doc 19 § G3 lists it as one of four leak mitigations rather than as a fix.
    ///     </para>
    ///     <para>
    ///         Applied only where a caller passes a view direction — see
    ///         <see cref="TrySample(Vector3, Vector3, Vector3, out IrradianceProbe)" />. A bake has no
    ///         camera and asks for none of it.
    ///     </para>
    /// </remarks>
    public float ViewBias { get; set; } = 0.25f;

    /// <summary>Gives a cell a brick of a given size, or answers with the one already covering it.</summary>
    /// <param name="cell">A cell the brick should cover.</param>
    /// <param name="size">How many finest cells across it should be. A power of two.</param>
    /// <param name="brick">The brick covering that cell.</param>
    /// <returns>Whether it has one — false only when the pool is full.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such cell, or the size is not a power of two.</exception>
    public bool TryAllocate(Int3 cell, int size, out IrradianceBrick brick) {
        if (Indirection.TryBrick(cell, out brick)) {
            return true;
        }

        var origin = IrradianceIndirection.Origin(cell, size);

        if (!Free(origin, size) || !Pool.TryAllocate(out var slot)) {
            brick = default;

            return false;
        }

        brick = new(slot, origin, size);
        Indirection.Assign(brick);

        return true;
    }

    /// <summary>Gives every empty cell overlapping a box a brick of a given size.</summary>
    /// <param name="region">The box, in world space.</param>
    /// <param name="size">How many finest cells across each brick should be. A power of two.</param>
    /// <returns>How many bricks were made.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The size is not a positive power of two.</exception>
    /// <remarks>
    ///     <para>
    ///         Cells that already have a brick are left alone whatever size that brick is, so this
    ///         only ever fills gaps. The usual shape is coarse first and then
    ///         <see cref="Refine" />: one call to cover the world cheaply, one to subdivide where
    ///         geometry is.
    ///     </para>
    ///     <para>
    ///         A caller passes a renderer's world bounds grown by a probe spacing or two, because a
    ///         surface needs probes on <i>both</i> sides of it to be interpolated between.
    ///     </para>
    /// </remarks>
    public int Allocate(BoundingBox region, int size = 1) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        if ((size & (size - 1)) != 0) {
            throw new ArgumentOutOfRangeException(nameof(size), size, "A brick's size has to be a power of two.");
        }

        if (!Indirection.Bounds.Intersects(region)) {
            return 0;
        }

        var resolution = Indirection.Resolution;
        var cellSize = Indirection.CellSize;
        var low = (region.Minimum - Indirection.Bounds.Minimum) / cellSize;
        var high = (region.Maximum - Indirection.Bounds.Minimum) / cellSize;

        var first = IrradianceIndirection.Origin(
            new(
                Math.Clamp((int)MathF.Floor(low.X), 0, resolution.X - 1),
                Math.Clamp((int)MathF.Floor(low.Y), 0, resolution.Y - 1),
                Math.Clamp((int)MathF.Floor(low.Z), 0, resolution.Z - 1)
            ),
            size
        );

        var last = new Int3(
            Math.Clamp((int)MathF.Ceiling(high.X) - 1, first.X, resolution.X - 1),
            Math.Clamp((int)MathF.Ceiling(high.Y) - 1, first.Y, resolution.Y - 1),
            Math.Clamp((int)MathF.Ceiling(high.Z) - 1, first.Z, resolution.Z - 1)
        );

        var made = 0;

        for (var z = first.Z; z <= last.Z; z += size) {
            for (var y = first.Y; y <= last.Y; y += size) {
                for (var x = first.X; x <= last.X; x += size) {
                    var origin = new Int3(x, y, z);

                    if (!Free(origin, size) || !Pool.TryAllocate(out var slot)) {
                        continue;
                    }

                    Indirection.Assign(new(slot, origin, size));
                    made++;
                }
            }
        }

        return made;
    }

    /// <summary>Gives every empty cell of the field a brick of a given size.</summary>
    /// <param name="size">How many finest cells across each brick should be.</param>
    /// <returns>How many bricks were made.</returns>
    public int AllocateAll(int size = 1) => Allocate(Indirection.Bounds, size);

    /// <summary>Subdivides every brick overlapping a box until it is no coarser than a given size.</summary>
    /// <param name="region">The box, in world space.</param>
    /// <param name="size">The coarsest a brick there may be. A power of two.</param>
    /// <returns>How many bricks were made.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The size is not a positive power of two.</exception>
    /// <remarks>
    ///     <b>Refinement is a leak fix before it is a memory optimisation.</b> A leak is light crossing
    ///     a wall, and whether it does is decided by how thick the wall is <i>in probes</i> — a wall
    ///     thinner than the probe spacing holds no probes at all and a trilinear stencil spans straight
    ///     through it. Halving the spacing near geometry is the only thing that changes that number,
    ///     which is why this exists and why <see cref="Dilate" /> cannot substitute for it.
    /// </remarks>
    public int Refine(BoundingBox region, int size = 1) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        if ((size & (size - 1)) != 0) {
            throw new ArgumentOutOfRangeException(nameof(size), size, "A brick's size has to be a power of two.");
        }

        var pending = new List<Int3>();
        var made = 0;

        while (true) {
            pending.Clear();

            foreach (var brick in Bricks) {
                if (brick.Size > size && BrickBounds(brick).Intersects(region)) {
                    pending.Add(brick.Cell);
                }
            }

            if (pending.Count == 0) {
                return made;
            }

            foreach (var cell in pending) {
                made += Split(cell);
            }
        }
    }

    /// <summary>Replaces the brick covering a cell with eight of half its size.</summary>
    /// <param name="cell">A cell the brick covers.</param>
    /// <returns>How many children were made.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such cell.</exception>
    /// <remarks>
    ///     <b>The parent's probes are discarded rather than interpolated down.</b> Interpolating would
    ///     make eight children that agree with each other and with a coarser answer than any of them
    ///     should hold, and a filler would then be blending toward the truth from something that looks
    ///     converged. Empty is honest: the children are invalid until something fills them, and
    ///     <see cref="Dilate" /> treats them as the holes they are.
    /// </remarks>
    public int Split(Int3 cell) {
        if (!Indirection.TryBrick(cell, out var parent) || parent.Size == 1) {
            return 0;
        }

        var half = parent.Size / 2;

        Indirection.Revoke(parent);
        Pool.Release(parent.Slot);

        var made = 0;

        for (var z = 0; z < 2; z++) {
            for (var y = 0; y < 2; y++) {
                for (var x = 0; x < 2; x++) {
                    var origin = parent.Cell + (new Int3(x, y, z) * half);

                    // A child entirely past the edge of the grid is a child of a brick that was
                    // hanging over — there is nothing out there to cover.
                    if (!Indirection.Holds(origin) || !Pool.TryAllocate(out var slot)) {
                        continue;
                    }

                    Indirection.Assign(new(slot, origin, half));
                    made++;
                }
            }
        }

        return made;
    }

    /// <summary>Replaces an aligned group of equal-size bricks with one of twice the size.</summary>
    /// <param name="cell">The merged brick's origin — a cell aligned to twice the children's size.</param>
    /// <returns>Whether the merge happened.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The inverse of <see cref="Split" />, and the unit coarsening is built from.</b> It
    ///         refuses rather than guesses: the origin must hold a brick starting there, aligned to
    ///         the doubled size, and every sibling position inside the grid must hold a brick of
    ///         exactly the children's size — a mixed octet has a grandchild whose detail somebody
    ///         asked for, and merging over it would be refinement undone at a distance.
    ///     </para>
    ///     <para>
    ///         <b>The parent's probes are subsampled, not fabricated.</b> A parent probe stands at
    ///         exactly every other child probe's position — the lattices nest — so its value is a
    ///         <i>copy</i> of a probe something really filled, validity included. That is the mirror
    ///         of <see cref="Split" /> discarding: splitting invents detail that does not exist, and
    ///         is honest to start empty; merging summarises detail that does, and throwing it away
    ///         would make every coarsened region flicker dark for no reason. Probes covering space
    ///         past the grid's edge (a parent overhanging like its children did) stay invalid, and
    ///         the pool clears released slots, so nothing stale shines through.
    ///     </para>
    ///     ⚠ On a device-filled field the CPU pool is not what the shader reads, so a merged brick's
    ///     device probes are whatever its slot last held until the round robin refills them — the
    ///     same window a split's empty children already have, and the same repair covers both.
    /// </remarks>
    public bool TryMerge(Int3 cell) {
        if (!Indirection.TryBrick(cell, out var first) || first.Cell != cell) {
            return false;
        }

        var size = first.Size;
        var merged = size * 2;

        if (cell.X % merged != 0 || cell.Y % merged != 0 || cell.Z % merged != 0) {
            return false;
        }

        Span<IrradianceBrick> children = stackalloc IrradianceBrick[8];
        var count = 0;

        for (var z = 0; z < 2; z++) {
            for (var y = 0; y < 2; y++) {
                for (var x = 0; x < 2; x++) {
                    var origin = cell + (new Int3(x, y, z) * size);

                    if (!Indirection.Holds(origin)) {
                        continue;
                    }

                    if (!Indirection.TryBrick(origin, out var child) || child.Cell != origin || child.Size != size) {
                        return false;
                    }

                    children[count++] = child;
                }
            }
        }

        // One child is the brick itself — every sibling hangs past the grid, and "merging" would
        // rename it to double the size, cover not one cell more, and invite the next call to double
        // it again. A merge that adds no coverage is refused, which is also what caps a brick's
        // size at the grid it lives in.
        if (count < 2) {
            return false;
        }

        // Read before anything is revoked: the subsample is of the children as they stand. The
        // owned probes are 0..3 per axis — the fourth plane is the border, and borders belong to
        // SyncBorders on the merged brick exactly as on any other.
        const int Interior = IrradianceBrickPool.BrickResolution;

        Span<IrradianceProbe> kept = new IrradianceProbe[Interior * Interior * Interior];
        Span<bool> written = new bool[Interior * Interior * Interior];

        for (var index = 0; index < count; index++) {
            var child = children[index];
            var offset = (child.Cell - cell) / size;

            for (var z = offset.Z * 2; z < (offset.Z * 2) + 2; z++) {
                for (var y = offset.Y * 2; y < (offset.Y * 2) + 2; y++) {
                    for (var x = offset.X * 2; x < (offset.X * 2) + 2; x++) {
                        // Parent probe (x, y, z) stands exactly on child probe (2x − 4·offset, …).
                        var at = ((z * Interior) + y) * Interior + x;

                        kept[at] = GetProbe(
                            child,
                            (2 * x) - (4 * offset.X),
                            (2 * y) - (4 * offset.Y),
                            (2 * z) - (4 * offset.Z)
                        );

                        written[at] = true;
                    }
                }
            }
        }

        for (var index = 0; index < count; index++) {
            Indirection.Revoke(children[index]);
            Pool.Release(children[index].Slot);
        }

        // Cannot fail: at least one slot was just released.
        Pool.TryAllocate(out var slot);

        var parent = new IrradianceBrick(slot, cell, merged);

        Indirection.Assign(parent);

        for (var z = 0; z < Interior; z++) {
            for (var y = 0; y < Interior; y++) {
                for (var x = 0; x < Interior; x++) {
                    var at = ((z * Interior) + y) * Interior + x;

                    if (written[at]) {
                        SetProbe(parent, x, y, z, kept[at]);
                    }
                }
            }
        }

        return true;
    }

    /// <summary>Takes a cell's brick away and gives the slot back.</summary>
    /// <param name="cell">A cell the brick covers.</param>
    /// <returns>Whether it had one.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such cell.</exception>
    public bool Release(Int3 cell) {
        if (!Indirection.TryBrick(cell, out var brick)) {
            return false;
        }

        Indirection.Revoke(brick);
        Pool.Release(brick.Slot);

        return true;
    }

    /// <summary>Empties the field of every brick.</summary>
    public void Clear() {
        Indirection.Clear();
        Pool.Clear();
    }

    /// <summary>The box a brick covers.</summary>
    /// <param name="brick">The brick.</param>
    /// <returns>The box.</returns>
    public BoundingBox BrickBounds(IrradianceBrick brick) {
        var cellSize = Indirection.CellSize;
        var minimum = Indirection.Bounds.Minimum + (cellSize * new Vector3(brick.Cell.X, brick.Cell.Y, brick.Cell.Z));

        return new(minimum, minimum + (cellSize * brick.Size));
    }

    /// <summary>Where one probe of one brick is, in world space.</summary>
    /// <param name="brick">The brick.</param>
    /// <param name="x">The probe's index along X, 0 to 4.</param>
    /// <param name="y">Along Y.</param>
    /// <param name="z">Along Z.</param>
    /// <returns>The position.</returns>
    /// <remarks>
    ///     Four is allowed and is the point: for a neighbour of the same size it is the same world
    ///     position as that neighbour's zero, which is what makes the border a copy rather than an
    ///     estimate.
    /// </remarks>
    public Vector3 ProbePosition(IrradianceBrick brick, int x, int y, int z) =>
        Indirection.Bounds.Minimum
        + (Indirection.CellSize * new Vector3(brick.Cell.X, brick.Cell.Y, brick.Cell.Z))
        + (ProbeSpacingOf(brick.Size) * new Vector3(x, y, z));

    /// <summary>Writes one of the sixty-four probes a brick owns.</summary>
    /// <param name="brick">The brick.</param>
    /// <param name="x">The probe's index along X, 0 to 3.</param>
    /// <param name="y">Along Y.</param>
    /// <param name="z">Along Z.</param>
    /// <param name="probe">What it saw.</param>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is out of range.</exception>
    /// <remarks>
    ///     <b>Three, not four.</b> The fourth plane is a border, borders belong to
    ///     <see cref="SyncBorders" />, and a filler that wrote one would be writing an answer where a
    ///     copy of the neighbour's answer has to go — a seam that appears only where two bricks
    ///     disagree, which is exactly where a seam is visible.
    /// </remarks>
    public void SetProbe(IrradianceBrick brick, int x, int y, int z, IrradianceProbe probe) {
        const int owned = IrradianceBrickPool.BrickResolution;

        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, owned);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, owned);
        ArgumentOutOfRangeException.ThrowIfNegative(z);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(z, owned);

        Pool[brick.Slot, x, y, z] = probe;
    }

    /// <summary>Reads one texel of a brick, border planes included.</summary>
    /// <param name="brick">The brick.</param>
    /// <param name="x">The texel's index along X, 0 to 4.</param>
    /// <param name="y">Along Y.</param>
    /// <param name="z">Along Z.</param>
    /// <returns>The probe.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is out of range.</exception>
    public IrradianceProbe GetProbe(IrradianceBrick brick, int x, int y, int z) => Pool[brick.Slot, x, y, z];

    /// <summary>Fills every brick's border planes from whatever is beyond them.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Run this after a filler and before a sample, or the field has seams in it.</b> Each
    ///         of the three positive faces of a brick — and the edges and the corner where they meet —
    ///         stands in for what is beyond it, so that a single trilinear fetch can cross a boundary
    ///         without knowing there was one.
    ///     </para>
    ///     <para>
    ///         <b>Between two bricks of the same size it is a copy, exactly.</b> The border probe and
    ///         the neighbour's first probe are the same world position, so the value is fetched by
    ///         index and no arithmetic happens to it. <b>Across a change of size it is a sample</b>,
    ///         because there is no probe of the neighbour at that position to copy — a coarse brick's
    ///         border plane spans several finer bricks, and a fine brick's border lands between a
    ///         coarse one's probes. Interpolating the neighbour's own field is the answer that makes
    ///         the two agree at the boundary; anything else puts a seam exactly where the refinement
    ///         changes, which is next to geometry.
    ///     </para>
    ///     <para>
    ///         <b>At the edge of the field, and beside a cell with no brick, a border repeats the
    ///         brick's own last probe.</b> There is nothing beyond to copy, and repeating is a constant
    ///         extrapolation — the lighting stops changing rather than falling to black, which is what
    ///         the alternative looks like: a dark rind one probe thick around everything.
    ///     </para>
    ///     <para>
    ///         <b>Coarsest first, and that ordering is forced rather than tidy.</b> A fine brick
    ///         borrowing from a coarse neighbour interpolates that neighbour's field, and the position
    ///         it wants can fall in the coarse brick's <i>own</i> border plane — so the coarse brick
    ///         has to be finished first. The reverse never happens: a coarse brick's border lands on
    ///         the near face of whichever fine brick covers it, which needs only probes that brick
    ///         owns.
    ///     </para>
    ///     <para>
    ///         <b>Within a size: faces, then edges, then the corner — and that ordering is forced
    ///         too.</b> Two bricks of the same size do not only copy each other's owned probes. At the
    ///         grid's outer face a border position clamps back inside, so the lookup lands in a
    ///         <i>face</i> neighbour with a local coordinate of exactly one, and the copy reaches that
    ///         neighbour's own border plane. Copying a border texel before it is written this pass
    ///         copies whatever the pool held before — zero, on a field synced once, which is a dark
    ///         seam along the grid's edges. The read target always has strictly fewer border-plane
    ///         coordinates than the reader (an axis at four means a shared boundary plane, and a brick
    ///         matching on every such axis is this brick), so committing rank by rank makes every read
    ///         a read of something finished. It is also the order the device repair dispatches, and
    ///         the two have to agree because the device is checked against this.
    ///     </para>
    /// </remarks>
    public void SyncBorders() {
        const int last = IrradianceBrickPool.BrickResolution;

        var sizes = new List<int>();

        foreach (var brick in Bricks) {
            if (!sizes.Contains(brick.Size)) {
                sizes.Add(brick.Size);
            }
        }

        sizes.Sort(static (left, right) => right.CompareTo(left));

        foreach (var size in sizes) {
            for (var rank = 1; rank <= 3; rank++) {
                deferred.Clear();

                foreach (var brick in Bricks) {
                    if (brick.Size != size) {
                        continue;
                    }

                    for (var z = 0; z <= last; z++) {
                        for (var y = 0; y <= last; y++) {
                            for (var x = 0; x <= last; x++) {
                                var planes = (x == last ? 1 : 0) + (y == last ? 1 : 0) + (z == last ? 1 : 0);

                                if (planes != rank) {
                                    continue;
                                }

                                deferred.Add((brick.Slot, x, y, z, Borrowed(brick, x, y, z)));
                            }
                        }
                    }
                }

                foreach (var (slot, x, y, z, probe) in deferred) {
                    Pool[slot, x, y, z] = probe;
                }
            }
        }

        deferred.Clear();
    }

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
    ///         and the fix for both is <see cref="Refine" />.
    ///     </para>
    ///     <para>
    ///         <b>A neighbour is a world position, not an index.</b> Two bricks of different sizes
    ///         have probes at different spacings, so "one probe that way" is asked of the field and
    ///         answered by whichever brick covers the answer. Run this before
    ///         <see cref="SyncBorders" />: borders are copies, and copying before the original is
    ///         repaired copies the hole.
    ///     </para>
    /// </remarks>
    public int Dilate(int passes = 1) {
        ArgumentOutOfRangeException.ThrowIfNegative(passes);

        const int owned = IrradianceBrickPool.BrickResolution;

        var repaired = 0;

        for (var pass = 0; pass < passes; pass++) {
            deferred.Clear();

            foreach (var brick in Bricks) {
                for (var z = 0; z < owned; z++) {
                    for (var y = 0; y < owned; y++) {
                        for (var x = 0; x < owned; x++) {
                            if (Pool[brick.Slot, x, y, z].Validity > 0f) {
                                continue;
                            }

                            if (TryBorrowFromNeighbours(brick, x, y, z, out var repair)) {
                                deferred.Add((brick.Slot, x, y, z, repair));
                            }
                        }
                    }
                }
            }

            if (deferred.Count == 0) {
                break;
            }

            // Applied after the whole pass, not during it, so a repair cannot feed the probe next to
            // it in the same sweep — which would make the result depend on which way the loops run.
            foreach (var (slot, x, y, z, probe) in deferred) {
                Pool[slot, x, y, z] = probe;
            }

            repaired += deferred.Count;
        }

        deferred.Clear();

        return repaired;
    }

    /// <summary>The probe a field holds at a world position, or nothing where no brick does.</summary>
    /// <param name="world">The position.</param>
    /// <param name="probe">What is there.</param>
    /// <returns>Whether a brick covers it.</returns>
    public bool TrySample(Vector3 world, out IrradianceProbe probe) {
        if (!Indirection.TryLocate(world, out var brick, out var local)) {
            probe = IrradianceProbe.Empty;

            return false;
        }

        probe = Pool.Sample(brick.Slot, local);

        return true;
    }

    /// <summary>The probe a surface sees, biased off the surface it stands on.</summary>
    /// <param name="world">Where the surface is.</param>
    /// <param name="normal">Which way it faces, normalised.</param>
    /// <param name="probe">What is there.</param>
    /// <returns>Whether a brick covers the biased position.</returns>
    /// <remarks>
    ///     The normal bias alone, because a caller with no view direction has none to give — and a
    ///     view bias applied toward a camera that is not there would push the lookup at whatever
    ///     <see cref="Vector3.Zero" /> happens to mean. See the overload that takes one.
    /// </remarks>
    public bool TrySample(Vector3 world, Vector3 normal, out IrradianceProbe probe) =>
        TrySample(world, normal, Vector3.Zero, out probe);

    /// <summary>The probe a surface sees, biased off the surface and toward the eye.</summary>
    /// <param name="world">Where the surface is.</param>
    /// <param name="normal">Which way it faces, normalised.</param>
    /// <param name="view">Which way the camera is, from the surface, normalised. Zero for none.</param>
    /// <param name="probe">What is there.</param>
    /// <returns>Whether a brick covers the biased position.</returns>
    /// <remarks>
    ///     <para>
    ///         Two lookups: the first to learn how big the brick under the surface is, because both
    ///         <see cref="NormalBias" /> and <see cref="ViewBias" /> are measured in that brick's probe
    ///         spacings. See their remarks for why they are not world distances, and why the second
    ///         exists at all when the first already moves the lookup.
    ///     </para>
    ///     <para>
    ///         The two offsets are summed rather than applied in turn, so a surface seen head-on — the
    ///         case where <c>view</c> and <c>normal</c> agree — is biased by their sum along one
    ///         direction rather than a step and then a turn. That matches the shader, which is the
    ///         constraint that decides it: <c>IrradianceField.Bias</c> in <c>IrradianceField.rvn</c>.
    ///     </para>
    /// </remarks>
    public bool TrySample(Vector3 world, Vector3 normal, Vector3 view, out IrradianceProbe probe) {
        if (!Indirection.TryLocate(world, out var brick, out _)) {
            probe = IrradianceProbe.Empty;

            return false;
        }

        var offset = (normal * NormalBias) + (view * ViewBias);

        return TrySample(world + (offset * ProbeSpacingOf(brick.Size)), out probe);
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
    public Vector3 Irradiance(Vector3 world, Vector3 normal) => Irradiance(world, normal, Vector3.Zero);

    /// <summary>The same, for a caller that knows where the camera is.</summary>
    /// <param name="world">Where the surface is.</param>
    /// <param name="normal">Which way it faces, normalised.</param>
    /// <param name="view">Which way the camera is, from the surface, normalised. Zero for none.</param>
    /// <returns>The irradiance over π, or zero where the field has nothing.</returns>
    /// <remarks>
    ///     <para>See <see cref="ViewBias" /> for why the second direction is worth a second overload.</para>
    ///     <para>
    ///         <b>Clamped at zero, and an L1 band genuinely needs it.</b> Four coefficients cannot
    ///         represent a hemisphere sharply, so a probe lit entirely from one side evaluates
    ///         <i>negative</i> for a normal pointing the other way — the linear band subtracts more
    ///         than the constant band has. That is not a small error: it is light with the wrong sign,
    ///         and a shading pass given it makes a surface darker than an unlit one.
    ///     </para>
    ///     <para>
    ///         ⚠ Found by doc 19 § L2's bounce, which is the first thing that ever fed a field back
    ///         into the scene that filled it. A sunlit floor produces a field whose light all arrives
    ///         from below; feeding that back as the floor's own ambient evaluated to −0.047 and made
    ///         the second pass <i>dimmer</i> than the first. Every earlier test used a near-uniform
    ///         environment, where the linear band is small and this never shows.
    ///     </para>
    ///     <para>
    ///         Here rather than in <see cref="IrradianceProbe.Irradiance" />, which is arithmetic over
    ///         a basis and has every right to answer what the basis says — and which several tests use
    ///         as an exact readout of what a brick holds. This is where the number stops being
    ///         arithmetic and becomes light. <c>IrradianceFieldProbes.Sample</c> clamps in the matching
    ///         place, because the two are held against each other.
    ///     </para>
    /// </remarks>
    public Vector3 Irradiance(Vector3 world, Vector3 normal, Vector3 view) =>
        TrySample(world, normal, view, out var probe)
            ? Vector3.Max(probe.Irradiance(normal), Vector3.Zero)
            : Vector3.Zero;

    /// <summary>The probe nearest a world position, out of whichever brick covers it.</summary>
    /// <param name="world">The position.</param>
    /// <param name="slot">Which brick it came from.</param>
    /// <param name="index">Where in that brick.</param>
    /// <param name="probe">The probe.</param>
    /// <returns>Whether a brick covers the position.</returns>
    /// <remarks>
    ///     Clamped to the sixty-four a brick owns, never a border — so a dilation reading its
    ///     neighbours cannot read a border plane that is about to be rewritten from the very probe
    ///     being repaired.
    /// </remarks>
    bool TryProbeAt(Vector3 world, out int slot, out Int3 index, out IrradianceProbe probe) {
        slot = IrradianceIndirection.Empty;
        index = Int3.Zero;
        probe = IrradianceProbe.Empty;

        if (!Indirection.TryLocate(world, out var brick, out var local)) {
            return false;
        }

        const int owned = IrradianceBrickPool.BrickResolution;

        index = new(
            Nearest(local.X, owned, owned - 1),
            Nearest(local.Y, owned, owned - 1),
            Nearest(local.Z, owned, owned - 1)
        );

        slot = brick.Slot;
        probe = Pool[slot, index.X, index.Y, index.Z];

        return true;
    }

    /// <summary>The average of a probe's valid neighbours, if it has any.</summary>
    /// <param name="brick">The brick the probe belongs to.</param>
    /// <param name="x">The probe's index along X.</param>
    /// <param name="y">Along Y.</param>
    /// <param name="z">Along Z.</param>
    /// <param name="repair">What it should hold instead.</param>
    /// <returns>Whether anything nearby was worth copying.</returns>
    /// <remarks>
    ///     The six face neighbours, not all twenty-six. A diagonal neighbour is further away and its
    ///     path to here passes through the two faces between them, so a corner probe buried in a wall
    ///     would pull from a room it has no straight line to. Six is also what makes a pass mean "one
    ///     probe of travel", which is what makes the pass count a distance and therefore a knob
    ///     somebody can reason about.
    /// </remarks>
    bool TryBorrowFromNeighbours(IrradianceBrick brick, int x, int y, int z, out IrradianceProbe repair) {
        ReadOnlySpan<Int3> directions = [
            new(1, 0, 0), new(-1, 0, 0),
            new(0, 1, 0), new(0, -1, 0),
            new(0, 0, 1), new(0, 0, -1)
        ];

        var position = ProbePosition(brick, x, y, z);
        var spacing = ProbeSpacingOf(brick.Size);
        var self = new Int3(x, y, z);

        var radiance = SphericalHarmonicsL1.Zero;
        var shadow = 0f;
        var weight = 0f;
        var contributors = 0;

        foreach (var direction in directions) {
            var step = spacing * new Vector3(direction.X, direction.Y, direction.Z);

            if (!TryProbeAt(position + step, out var slot, out var index, out var other)) {
                continue;
            }

            // A coarse neighbour's nearest probe can round back to this one, and averaging a probe
            // into itself repairs nothing while looking as though it did.
            if (other.Validity <= 0f || (slot == brick.Slot && index == self)) {
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

    /// <summary>What a border texel should hold.</summary>
    /// <param name="brick">The brick the border belongs to.</param>
    /// <param name="x">The texel's index along X.</param>
    /// <param name="y">Along Y.</param>
    /// <param name="z">Along Z.</param>
    /// <returns>The neighbour's answer, or this brick's own last probe where there is no neighbour.</returns>
    IrradianceProbe Borrowed(IrradianceBrick brick, int x, int y, int z) {
        const int last = IrradianceBrickPool.BrickResolution;

        // Asked as a world position rather than as a neighbouring cell, because which brick is beyond
        // a border texel is a different answer for different texels of the same plane: a coarse
        // brick's +X face can span four finer neighbours, and the cell at the plane's own corner
        // names only the first of them.
        if (Indirection.TryLocate(ProbePosition(brick, x, y, z), out var beyond, out var local)
            && beyond.Slot != brick.Slot) {
            // Same size, same lattice: the border probe and one of the neighbour's own probes are the
            // same world position, so this is a copy by index and nothing happens to the value.
            if (beyond.Size == brick.Size) {
                return Pool[
                    beyond.Slot,
                    Nearest(local.X, last, last),
                    Nearest(local.Y, last, last),
                    Nearest(local.Z, last, last)
                ];
            }

            // Different sizes, so there is no probe of the neighbour at this position to copy —
            // interpolate its own field instead, which is what makes the two agree at the boundary.
            return Pool.Sample(beyond.Slot, local);
        }

        return Pool[brick.Slot, Math.Min(x, last - 1), Math.Min(y, last - 1), Math.Min(z, last - 1)];
    }

    /// <summary>
    ///     Which probe of a brick a local coordinate is nearest, along one axis.
    /// </summary>
    /// <param name="local">Where along the axis, 0 to 1.</param>
    /// <param name="scale">How many gaps the axis spans — the probes a brick owns.</param>
    /// <param name="last">The largest index the answer may be.</param>
    /// <returns>The probe's index.</returns>
    /// <remarks>
    ///     <b><c>floor(x + ½)</c> rather than <see cref="MathF.Round(float)" />, and the difference is
    ///     not pedantry.</b> <c>MathF.Round</c> breaks a tie to the nearest EVEN integer, so a
    ///     coordinate of exactly 0.5 rounds down and 2.5 rounds up. GLSL's <c>round</c> does not
    ///     promise that and in practice rounds halves away from zero, so the two disagree on every
    ///     tie — and ties are not rare here, they are structural: a fine brick's probes sit exactly
    ///     halfway between a coarse neighbour's, so <i>every</i> lookup across a refinement boundary
    ///     lands on one.
    /// </remarks>
    /// <seealso cref="IrradianceBrickPool.BrickResolution" />
    static int Nearest(float local, int scale, int last) =>
        Math.Clamp((int)MathF.Floor((local * scale) + 0.5f), 0, last);

    /// <summary>Whether every cell a brick of a size would cover is empty.</summary>
    bool Free(Int3 origin, int size) {
        for (var z = origin.Z; z < origin.Z + size; z++) {
            for (var y = origin.Y; y < origin.Y + size; y++) {
                for (var x = origin.X; x < origin.X + size; x++) {
                    var cell = new Int3(x, y, z);

                    if (Indirection.Holds(cell) && Indirection[cell].HasBrick) {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>Two projections added, which is the projection of the two together.</summary>
    static SphericalHarmonicsL1 Sum(SphericalHarmonicsL1 left, SphericalHarmonicsL1 right) =>
        new(
            left.L00 + right.L00,
            left.L1m1 + right.L1m1,
            left.L10 + right.L10,
            left.L11 + right.L11
        );
}

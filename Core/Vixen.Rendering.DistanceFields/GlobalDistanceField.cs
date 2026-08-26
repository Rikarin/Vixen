// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.DistanceFields;

/// <summary>
///     Every instance's field composited into a few nested volumes that follow the camera.
/// </summary>
/// <remarks>
///     <para>
///         A tracer cannot walk a list of instances — that is the cost the field was built to avoid.
///         It walks one volume, and this is the volume: a clipmap of <see cref="LevelCount" /> cubes
///         sharing a resolution and doubling in extent, so detail is fine where the camera is and
///         coarse where it is far. Level zero might be sixteen metres across at a quarter of a metre
///         a cell, and level three a hundred and twenty-eight at two metres, for the same memory
///         each.
///     </para>
///     <para>
///         <b>Every level is snapped to its own cell grid.</b> A level centred exactly on the camera
///         slides its sampling grid under static geometry as the camera moves, and the same fixed
///         wall shimmers between two rows of cells. This is the same defect
///         <c>Vixen.Rendering.ShadowCascades</c> fixes by snapping a cascade to whole texels, it has
///         the same cause, and it has the same fix — which is worth saying because the two look
///         unrelated until both are written down.
///     </para>
///     <para>
///         <b>A level clamps to what it can know.</b> Nothing outside a level's own cube was
///         consulted when it was built, so a cell that found nothing reports
///         <see cref="MaxDistanceOf" /> rather than infinity. A tracer reading it may step that far
///         and no further, which is exactly right: the answer is "at least this much", and a step of
///         "at least this much" is always safe.
///     </para>
///     <para>
///         Each level <i>is</i> a <see cref="MeshDistanceField" />. The type is a sampled distance
///         volume with a trilinear read, which is what a clipmap level is — over a whole scene rather
///         than over one mesh. Reusing it means one interpolation, tested once.
///     </para>
/// </remarks>
public sealed class GlobalDistanceField : IDistanceField {
    readonly MeshDistanceField[] levels;

    /// <summary>The other half of each level's double buffer, so a scroll can read while it writes.</summary>
    readonly MeshDistanceField[] spare;

    /// <summary>Whether each level holds anything a scroll could reuse.</summary>
    readonly bool[] filled;

    ClipmapRefresh? refresh;
    long slices;

    /// <summary>How many samples along each axis, in every level.</summary>
    public int Resolution { get; }

    /// <summary>How many nested cubes there are.</summary>
    public int LevelCount => levels.Length;

    /// <summary>Where the clipmap was last centred, before snapping.</summary>
    public Vector3 ViewPosition { get; private set; }

    /// <summary>Whether <see cref="Update" /> has run.</summary>
    public bool HasContent { get; private set; }

    /// <summary>Whether a <see cref="BeginUpdate" /> has been started and not yet published.</summary>
    /// <remarks>
    ///     While this is set, everything a reader can see — <see cref="LevelData" />,
    ///     <see cref="BoundsOf" />, <see cref="ViewPosition" />, <see cref="Reused" /> — is still the
    ///     <i>previous</i> composite, and stays that way until <see cref="ClipmapRefresh.Publish" />.
    ///     That is the whole of the double buffer: a refresh writes the other half.
    /// </remarks>
    public bool IsRefreshing => refresh is not null;

    /// <summary>How many level slices have ever been computed, across every composite.</summary>
    /// <remarks>
    ///     ⚠ <b>Counted from inside the work, which is what makes deferral checkable.</b> A caller
    ///     asking whether a background refresh has actually run yet cannot tell from
    ///     <see cref="HasContent" /> or from a clock — the first is true from the frame before and the
    ///     second measures how busy the machine is. This goes up once per slice, on whichever thread
    ///     ran it, so "no slices ran while the frame work drained" is an assertion about work rather
    ///     than about time.
    /// </remarks>
    public long SlicesComposited => Interlocked.Read(ref slices);

    /// <summary>How many cells the last update kept rather than recomputed.</summary>
    /// <remarks>
    ///     What makes the scroll checkable rather than claimed. A camera crossing one cell of the
    ///     finest level keeps almost all of it — and a change that quietly stopped reusing anything
    ///     would still produce a correct field, only a slow one, which is the kind of regression
    ///     nothing else here would notice.
    /// </remarks>
    public long Reused { get; private set; }

    /// <summary>Half the width of the finest level.</summary>
    public float FinestExtent { get; }

    /// <summary>Builds an empty clipmap.</summary>
    /// <param name="resolution">How many samples along each axis, per level.</param>
    /// <param name="finestExtent">Half the width of level zero, in world units.</param>
    /// <param name="levelCount">How many levels, each twice the extent of the one below.</param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public GlobalDistanceField(int resolution = 32, float finestExtent = 8f, int levelCount = 4) {
        ArgumentOutOfRangeException.ThrowIfLessThan(resolution, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(finestExtent);
        ArgumentOutOfRangeException.ThrowIfLessThan(levelCount, 1);

        Resolution = resolution;
        FinestExtent = finestExtent;
        levels = new MeshDistanceField[levelCount];
        spare = new MeshDistanceField[levelCount];
        filled = new bool[levelCount];

        for (var level = 0; level < levelCount; level++) {
            var extent = ExtentOf(level);
            var box = new BoundingBox(new(-extent), new(extent));
            var count = (long)resolution * resolution * resolution;

            levels[level] = new(box, new(resolution), new float[count]);
            spare[level] = new(box, new(resolution), new float[count]);
        }
    }

    /// <summary>Half the width of one level, in world units.</summary>
    /// <param name="level">Which level.</param>
    /// <returns>The half-width.</returns>
    public float ExtentOf(int level) => FinestExtent * (1 << level);

    /// <summary>How far apart one level's samples are, in world units.</summary>
    /// <param name="level">Which level.</param>
    /// <returns>The spacing.</returns>
    public float CellSizeOf(int level) => 2f * ExtentOf(level) / (Resolution - 1);

    /// <summary>The furthest a level is willing to claim anything is.</summary>
    /// <param name="level">Which level.</param>
    /// <returns>The clamp, in world units.</returns>
    /// <remarks>
    ///     The level's own half-width. A cell that found nothing within its own cube has learned that
    ///     the nearest surface is at least that far, and saying so is more useful than saying
    ///     nothing: it is the step a tracer is allowed to take.
    /// </remarks>
    public float MaxDistanceOf(int level) => ExtentOf(level);

    /// <summary>The box one level covers, as it was last snapped.</summary>
    /// <param name="level">Which level.</param>
    /// <returns>The box.</returns>
    public BoundingBox BoundsOf(int level) => levels[level].Bounds;

    /// <summary>The signed distance at one of a level's grid points.</summary>
    /// <param name="level">Which level.</param>
    /// <param name="x">The sample's index along X.</param>
    /// <param name="y">Along Y.</param>
    /// <param name="z">Along Z.</param>
    /// <returns>The distance.</returns>
    public float this[int level, int x, int y, int z] => levels[level][x, y, z];

    /// <summary>One level's samples, in the order a volume texture wants them.</summary>
    /// <param name="level">Which level.</param>
    /// <returns>The samples, X fastest then Y then Z.</returns>
    /// <remarks>
    ///     For whatever copies a level to a device. Read-only because the composite owns these — a
    ///     caller that wrote here would be editing a field nothing recomputed the rest of.
    /// </remarks>
    public ReadOnlySpan<float> LevelData(int level) => levels[level].Distances;

    /// <summary>Recomposites every level around a view position.</summary>
    /// <param name="viewPosition">Where the camera is.</param>
    /// <param name="instances">Everything that might be in the volume.</param>
    /// <param name="parallel">Whether the composite may use more than one thread.</param>
    /// <param name="scroll">
    ///     Whether the levels may keep what a movement did not invalidate. Only safe when nothing
    ///     about the instances changed — see the remarks.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         <b>Scrolling is the whole reason the levels are snapped to their own grids.</b> A
    ///         camera that crossed one cell moves a level by exactly one cell, so every cell of the
    ///         overlap is the same world position looking at the same instances and already holds its
    ///         answer. Only the slabs the level moved off the end of have to be computed. On a 32³
    ///         level a one-cell step keeps about 97 per cent of it.
    ///     </para>
    ///     <para>
    ///         <b>It is opt-in because this cannot tell whether the instances changed.</b> Comparing
    ///         them every update would cost more than the scroll saves, so the caller says — and the
    ///         caller knows, because it is the thing that changed them. Passing it when something
    ///         moved keeps stale cells, which is geometry left behind where it used to be.
    ///     </para>
    ///     <para>
    ///         Instances are rejected against their own world bounds before being sampled, and the
    ///         rejection is a comparison against the best distance so far — so the cost is not the
    ///         instance count but the number of instances that could still win. A tree over the
    ///         instances is what replaces the linear scan when that stops being enough.
    ///     </para>
    /// </remarks>
    public void Update(
        Vector3 viewPosition,
        ReadOnlySpan<DistanceFieldInstance> instances,
        bool parallel = true,
        bool scroll = false
    ) {
        var pending = BeginUpdate(viewPosition, instances, scroll);

        // One code path with the deferred one, deliberately: the arithmetic every closed-form test
        // in this assembly checks is the arithmetic a background refresh runs, rather than a second
        // copy of it that could drift.
        if (parallel && pending.SliceCount > 1) {
            System.Threading.Tasks.Parallel.For(0, pending.SliceCount, pending.Composite);
        } else {
            for (var slice = 0; slice < pending.SliceCount; slice++) {
                pending.Composite(slice);
            }
        }

        pending.Publish();
    }

    /// <summary>Starts a recomposite without running any of it.</summary>
    /// <param name="viewPosition">Where the camera is.</param>
    /// <param name="instances">Everything that might be in the volume.</param>
    /// <param name="scroll">Whether the levels may keep what a movement did not invalidate.</param>
    /// <returns>The refresh, whose slices somebody has to run and which somebody has to publish.</returns>
    /// <exception cref="InvalidOperationException">A refresh is already outstanding.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>What makes the composite deferrable.</b> <see cref="Update" /> is the whole thing
    ///         between one statement's braces, so a caller that must not block cannot have it. This
    ///         hands back <see cref="ClipmapRefresh.SliceCount" /> pieces of it instead — one Z slice
    ///         of one level each — and every one of them writes into the half of the double buffer
    ///         that nothing is reading. Nothing a reader can see moves until
    ///         <see cref="ClipmapRefresh.Publish" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The instances are copied here, not held.</b> A refresh that ran a frame later
    ///         against a list its owner had since edited would composite half of one scene and half
    ///         of another, and nothing downstream could tell.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One at a time.</b> The second buffer is one buffer. Two refreshes in flight would
    ///         both write it, and the survivor would be whichever finished last with the other's cells
    ///         still in it.
    ///     </para>
    /// </remarks>
    public ClipmapRefresh BeginUpdate(
        Vector3 viewPosition,
        ReadOnlySpan<DistanceFieldInstance> instances,
        bool scroll = false
    ) {
        if (refresh is not null) {
            throw new InvalidOperationException(
                "This clipmap already has a refresh outstanding. There is one spare buffer per level, "
                + "so a second refresh would write the one the first is writing. Publish or abandon "
                + "the first."
            );
        }

        // Bounds are eight transforms and a merge each, and every cell of every level would
        // otherwise pay for them again.
        var bounds = new BoundingBox[instances.Length];
        var placed = instances.ToArray();

        for (var index = 0; index < placed.Length; index++) {
            placed[index].Validate();
            bounds[index] = placed[index].WorldBounds;
        }

        return refresh = new(this, viewPosition, placed, bounds, scroll);
    }

    /// <summary>The signed distance at a world-space point, off the finest level that has it.</summary>
    /// <param name="position">The point.</param>
    /// <returns>The distance, negative inside.</returns>
    /// <remarks>
    ///     A point outside every level gets the coarsest level's clamp — "at least this far", which
    ///     is all anything knows about somewhere nobody sampled.
    /// </remarks>
    public float Sample(Vector3 position) {
        if (!TryLevelFor(position, out var level)) {
            return MaxDistanceOf(levels.Length - 1);
        }

        return levels[level].Sample(position);
    }

    /// <summary>Which way the distance grows fastest at a point.</summary>
    /// <param name="position">The point.</param>
    /// <returns>The normalised gradient, or zero where the field is flat.</returns>
    /// <remarks>
    ///     Differenced over the cell of whichever level answers, because a difference finer than a
    ///     cell reads that cell's own constant gradient and learns nothing. Across a level boundary
    ///     the offsets can land in different levels and the gradient takes a small step; a normal is
    ///     wanted near surfaces, which is where the finest level is, so it does not show.
    /// </remarks>
    public Vector3 SampleGradient(Vector3 position) {
        var step = CellSizeOf(TryLevelFor(position, out var level) ? level : levels.Length - 1);

        var gradient = new Vector3(
            Sample(position + new Vector3(step, 0, 0)) - Sample(position - new Vector3(step, 0, 0)),
            Sample(position + new Vector3(0, step, 0)) - Sample(position - new Vector3(0, step, 0)),
            Sample(position + new Vector3(0, 0, step)) - Sample(position - new Vector3(0, 0, step))
        );

        var length = gradient.Length();

        return length > MathUtil.ZeroTolerance ? gradient / length : Vector3.Zero;
    }

    /// <summary>Which is the finest level covering a point.</summary>
    /// <param name="world">The point.</param>
    /// <param name="level">The level, if one covers it.</param>
    /// <returns>Whether any does.</returns>
    public bool TryLevelFor(Vector3 world, out int level) {
        for (level = 0; level < levels.Length; level++) {
            if (levels[level].Bounds.Contains(world)) {
                return true;
            }
        }

        level = -1;

        return false;
    }

    /// <summary>Moves a point onto a whole multiple of a cell.</summary>
    /// <param name="position">The point.</param>
    /// <param name="cell">The cell size to snap to.</param>
    /// <returns>The snapped point.</returns>
    /// <remarks>
    ///     What stops the sampling grid sliding under static geometry. Without it a camera moving a
    ///     tenth of a cell resamples every surface at a tenth-of-a-cell offset, and a wall that has
    ///     not moved changes its distance every frame.
    /// </remarks>
    public static Vector3 Snap(Vector3 position, float cell) =>
        new(
            MathF.Round(position.X / cell) * cell,
            MathF.Round(position.Y / cell) * cell,
            MathF.Round(position.Z / cell) * cell
        );

    /// <summary>Works out where one level is going and into which buffer, without filling it.</summary>
    /// <param name="level">Which level.</param>
    /// <param name="viewPosition">Where the camera is, before this level's own snap.</param>
    /// <param name="scroll">Whether cells the movement did not invalidate may be kept.</param>
    /// <returns>Everything a slice of that level needs.</returns>
    internal LevelPlan PlanLevel(int level, Vector3 viewPosition, bool scroll) {
        var centre = Snap(viewPosition, CellSizeOf(level));
        var extent = ExtentOf(level);
        var box = new BoundingBox(centre - new Vector3(extent), centre + new Vector3(extent));
        var previous = levels[level];

        // Written into the other buffer, not over the one being read. A scroll copies from the old
        // level into the new one, and doing that in place would overwrite a cell another cell is
        // about to want — which is a field that is right along one axis and smeared along the rest.
        // It is also what lets the frame keep uploading the old level while this one is being filled.
        var slot = new MeshDistanceField(box, new(Resolution), spare[level].Distances);
        var cell = slot.CellSize;

        // A cell's world position is computed from its position on the GLOBAL grid, not from this
        // box's corner, and that is what makes a scrolled level bit-identical to a recomputed one.
        // `minimum + index * cell` and `minimum' + index' * cell` are the same point written two
        // ways, and float addition is not associative — so the two differ by an ULP, the distances
        // computed from them differ, and "identical" becomes "close", which is not a property worth
        // having. The snapped centre is a whole number of cells from the origin, so this expression
        // depends only on which cell of the world it is.
        var half = (Resolution - 1) * 0.5f;
        var origin = new Vector3(
            MathF.Round(centre.X / cell.X) - half,
            MathF.Round(centre.Y / cell.Y) - half,
            MathF.Round(centre.Z / cell.Z) - half
        );
        var shift = Int3.Zero;
        var reuse = false;

        if (scroll && filled[level]) {
            reuse = TryShift(previous.Bounds.Minimum, box.Minimum, cell, out shift);
        }

        return new(slot, previous, origin, shift, reuse, MaxDistanceOf(level));
    }

    /// <summary>Fills one Z slice of one planned level.</summary>
    /// <param name="plan">The level's plan.</param>
    /// <param name="z">Which slice.</param>
    /// <param name="instances">Everything that might be in it.</param>
    /// <param name="bounds">Each instance's world bounds, in the same order.</param>
    internal void CompositeSlice(
        in LevelPlan plan,
        int z,
        DistanceFieldInstance[] instances,
        BoundingBox[] bounds
    ) {
        var slot = plan.Slot;
        var previous = plan.Previous;
        var distances = slot.Distances;
        var cell = slot.CellSize;
        var maximum = plan.Maximum;

        for (var y = 0; y < Resolution; y++) {
            for (var x = 0; x < Resolution; x++) {
                // A cell of the new level is a world position, and the same world position in the
                // old level is that cell plus the shift — which is why the levels are snapped to
                // their own grids: without that the shift is not a whole number of cells and
                // nothing lines up to be kept.
                if (plan.Reuse) {
                    var sx = x + plan.Shift.X;
                    var sy = y + plan.Shift.Y;
                    var sz = z + plan.Shift.Z;

                    if (sx >= 0 && sx < Resolution && sy >= 0 && sy < Resolution && sz >= 0 && sz < Resolution) {
                        distances[slot.Index(x, y, z)] = previous[sx, sy, sz];

                        continue;
                    }
                }

                var point = cell * (plan.Origin + new Vector3(x, y, z));

                distances[slot.Index(x, y, z)] =
                    Math.Clamp(Nearest(point, instances, bounds, maximum), -maximum, maximum);
            }
        }

        Interlocked.Increment(ref slices);
    }

    /// <summary>Swaps every filled buffer in, and makes what a reader sees the new composite.</summary>
    /// <param name="pending">The refresh being published.</param>
    /// <param name="plans">Its per-level plans.</param>
    /// <param name="viewPosition">Where it was centred.</param>
    /// <param name="reused">How many cells it kept.</param>
    /// <remarks>
    ///     ⚠ <b>Everything a reader can see moves here and nowhere else.</b>
    ///     <see cref="ViewPosition" />, <see cref="BoundsOf" /> and <see cref="LevelData" /> are what
    ///     an upload and a shader's names are both derived from, so a field that advanced its bounds
    ///     before its cells would name the new box over the old texels — geometry reported at an
    ///     offset from where it is, on every frame of the gap.
    /// </remarks>
    internal void PublishRefresh(ClipmapRefresh pending, LevelPlan[] plans, Vector3 viewPosition, long reused) {
        if (!ReferenceEquals(refresh, pending)) {
            throw new InvalidOperationException("That refresh is not this clipmap's outstanding one.");
        }

        for (var level = 0; level < plans.Length; level++) {
            // The new level becomes the level, and the one just read becomes the spare.
            spare[level] = levels[level];
            levels[level] = plans[level].Slot;
            filled[level] = true;
        }

        ViewPosition = viewPosition;
        Reused = reused;
        HasContent = true;
        refresh = null;
    }

    /// <summary>Forgets an outstanding refresh without swapping anything in.</summary>
    /// <param name="pending">The refresh being abandoned.</param>
    internal void AbandonRefresh(ClipmapRefresh pending) {
        if (ReferenceEquals(refresh, pending)) {
            refresh = null;
        }
    }

    /// <summary>How many cells of a level survive a shift.</summary>
    /// <param name="shift">The movement, in cells.</param>
    /// <returns>The count.</returns>
    internal long Kept(Int3 shift) {
        long Overlap(int offset) => Math.Max(0, Resolution - Math.Abs(offset));

        return Overlap(shift.X) * Overlap(shift.Y) * Overlap(shift.Z);
    }

    /// <summary>How far one level moved, in whole cells, or false when it did not move by whole ones.</summary>
    /// <param name="from">Where the level was.</param>
    /// <param name="to">Where it is now.</param>
    /// <param name="cell">Its cell size.</param>
    /// <param name="shift">The movement, in cells.</param>
    /// <returns>Whether anything can be kept.</returns>
    /// <remarks>
    ///     It always is a whole number of cells, because <see cref="Snap" /> is what places the box —
    ///     but this checks rather than trusting it. A level whose extent or resolution changed under a
    ///     running clipmap would produce a fractional shift, and keeping cells across that is a field
    ///     offset from itself by a fraction of a cell everywhere, which is far harder to recognise
    ///     than a slow recomposite.
    /// </remarks>
    static bool TryShift(Vector3 from, Vector3 to, Vector3 cell, out Int3 shift) {
        var exact = (to - from) / cell;

        shift = new(
            (int)MathF.Round(exact.X),
            (int)MathF.Round(exact.Y),
            (int)MathF.Round(exact.Z)
        );

        var error = exact - new Vector3(shift.X, shift.Y, shift.Z);

        return MathF.Abs(error.X) < 0.001f && MathF.Abs(error.Y) < 0.001f && MathF.Abs(error.Z) < 0.001f;
    }

    /// <summary>The nearest surface to a point, over every instance that could hold one.</summary>
    /// <param name="point">The point.</param>
    /// <param name="instances">The instances.</param>
    /// <param name="bounds">Their world bounds, in the same order.</param>
    /// <param name="limit">The furthest answer worth having.</param>
    /// <returns>The signed distance.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Outside an instance's bounds, the answer is a bound rather than a reading.</b> The
    ///         field knows nothing out there — <see cref="MeshDistanceField.Sample" /> clamps, so it
    ///         reports the distance from the nearest point <i>of the box</i> to the mesh. Call that
    ///         <i>f</i> and the distance from the point to the box <i>t</i>: because every mesh point
    ///         is inside the box, the true distance is at least <c>√(f² + t²)</c>. That is both safe
    ///         and tight, and it is continuous across the boundary — at <c>t = 0</c> it is exactly
    ///         <i>f</i>, so an instance does not step down as a query crosses into it.
    ///     </para>
    ///     <para>
    ///         Under-reporting is the direction that is survivable: a tracer that thinks the surface
    ///         is nearer than it is takes an extra step, and one that thinks it is further walks
    ///         through it.
    ///     </para>
    /// </remarks>
    static float Nearest(
        Vector3 point,
        DistanceFieldInstance[] instances,
        BoundingBox[] bounds,
        float limit
    ) {
        var best = limit;

        for (var index = 0; index < instances.Length; index++) {
            var toBox = DistanceToBox(point, bounds[index]);

            // Nothing inside this box can be nearer than the box is, so an instance that is already
            // further away than the best answer cannot change it.
            if (toBox >= best) {
                continue;
            }

            var inner = instances[index].Sample(point);
            var distance = toBox > 0
                ? MathF.Sqrt((MathF.Max(inner, 0) * MathF.Max(inner, 0)) + (toBox * toBox))
                : inner;

            if (distance < best) {
                best = distance;
            }
        }

        return best;
    }

    /// <summary>The distance from a point to a box, zero inside it.</summary>
    /// <param name="point">The point.</param>
    /// <param name="bounds">The box.</param>
    /// <returns>The distance.</returns>
    static float DistanceToBox(Vector3 point, BoundingBox bounds) {
        var clamped = Vector3.Min(Vector3.Max(point, bounds.Minimum), bounds.Maximum);

        return Vector3.Distance(point, clamped);
    }
}

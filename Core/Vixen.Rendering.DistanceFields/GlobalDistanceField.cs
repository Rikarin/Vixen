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

    /// <summary>How many samples along each axis, in every level.</summary>
    public int Resolution { get; }

    /// <summary>How many nested cubes there are.</summary>
    public int LevelCount => levels.Length;

    /// <summary>Where the clipmap was last centred, before snapping.</summary>
    public Vector3 ViewPosition { get; private set; }

    /// <summary>Whether <see cref="Update" /> has run.</summary>
    public bool HasContent { get; private set; }

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

        for (var level = 0; level < levelCount; level++) {
            var extent = ExtentOf(level);

            levels[level] = new(
                new(new(-extent), new(extent)),
                new(resolution),
                new float[(long)resolution * resolution * resolution]
            );
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
    /// <remarks>
    ///     <para>
    ///         Every cell of every level, every time. A real implementation scrolls instead — a
    ///         camera that moved one cell invalidates one slab per axis and nothing else, which is
    ///         the whole reason a clipmap is snapped to its own grid in the first place. That is the
    ///         next thing to build here and it changes no result, only how long this takes; the
    ///         snapping it depends on is already done.
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
        bool parallel = true
    ) {
        ViewPosition = viewPosition;

        // Bounds are eight transforms and a merge each, and every cell of every level would
        // otherwise pay for them again.
        var bounds = new BoundingBox[instances.Length];
        var placed = instances.ToArray();

        for (var index = 0; index < placed.Length; index++) {
            placed[index].Validate();
            bounds[index] = placed[index].WorldBounds;
        }

        for (var level = 0; level < levels.Length; level++) {
            Composite(level, Snap(viewPosition, CellSizeOf(level)), placed, bounds, parallel);
        }

        HasContent = true;
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

    /// <summary>Fills one level around a snapped centre.</summary>
    /// <param name="level">Which level.</param>
    /// <param name="centre">Where it is centred, already snapped.</param>
    /// <param name="instances">Everything that might be in it.</param>
    /// <param name="bounds">Each instance's world bounds, in the same order.</param>
    /// <param name="parallel">Whether the composite may use more than one thread.</param>
    void Composite(
        int level,
        Vector3 centre,
        DistanceFieldInstance[] instances,
        BoundingBox[] bounds,
        bool parallel
    ) {
        var extent = ExtentOf(level);
        var maximum = MaxDistanceOf(level);
        var box = new BoundingBox(centre - new Vector3(extent), centre + new Vector3(extent));

        // The level's box moved, so the field describing it is a new one over the same storage.
        var slot = new MeshDistanceField(box, new(Resolution), levels[level].Distances);
        levels[level] = slot;

        var cell = slot.CellSize;
        var distances = slot.Distances;

        void Slice(int z) {
            for (var y = 0; y < Resolution; y++) {
                for (var x = 0; x < Resolution; x++) {
                    var point = box.Minimum + (cell * new Vector3(x, y, z));

                    distances[slot.Index(x, y, z)] =
                        Math.Clamp(Nearest(point, instances, bounds, maximum), -maximum, maximum);
                }
            }
        }

        if (parallel && Resolution > 1) {
            System.Threading.Tasks.Parallel.For(0, Resolution, Slice);
        } else {
            for (var z = 0; z < Resolution; z++) {
                Slice(z);
            }
        }
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

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Foliage;

/// <summary>Where one instance stands.</summary>
/// <param name="Position">In world space.</param>
/// <param name="Rotation">Which way it faces.</param>
/// <param name="Scale">How big it is, uniformly.</param>
/// <remarks>
///     ⚠ <b>A uniform scale, not a vector, and that is a decision rather than an omission.</b> A
///     non-uniform scale on a tree is a tree somebody has squashed, and it costs a transform that is
///     no longer a similarity — which means its bounding sphere is no longer a sphere scaled by one
///     number, and the per-instance cull's radius test stops being exact. Every reference toolset
///     offers uniform scale for foliage for the same reason.
/// </remarks>
public readonly record struct FoliageInstance(Vector3 Position, Quaternion Rotation, float Scale) {
    /// <summary>The instance as a transform, which is what an instancing feature uploads.</summary>
    public Matrix4x4 Transform => Matrix4x4.Compose(new(Scale), Rotation, Position);
}

/// <summary>Which cell of which type's grid.</summary>
/// <param name="X">The cell's X coordinate.</param>
/// <param name="Z">Its Z coordinate.</param>
/// <remarks>
///     Signed, because a world has no origin corner: a level built around zero has cells on all four
///     sides of it, and an unsigned coordinate would make the origin a wall.
/// </remarks>
public readonly record struct FoliageCellKey(int X, int Z);

/// <summary>
///     A fixed-size square of world, and the unit of everything foliage does.
/// </summary>
/// <param name="Size">How many metres a cell spans on a side.</param>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § D9]: the cell is the batch.</b> It holds every instance of one type
///         within it, its bounds are tight, and it is what the instancing feature sees as one object —
///         which is what makes [§ B2]'s contract satisfiable. A forest is a few thousand cells, each
///         culled by the existing frustum and Hi-Z path.
///     </para>
///     <para>
///         It is also the unit of <em>streaming</em> — <c>PageKey(Source, Index)</c> addresses a
///         foliage cell exactly as it addresses a terrain tile — and of collision, where the same
///         grid maintains which instances are near enough to have a body.
///     </para>
///     <para>
///         ⚠ <b>Thirty-two metres by default, and it is a compromise with two failure modes.</b>
///         Larger cells cull worse — the far half of a big cell of grass is behind a hill and is
///         drawn anyway — and smaller ones cost a draw each. Per type-group rather than global,
///         because a cell of trees and a cell of grass want different answers.
///     </para>
/// </remarks>
public readonly record struct FoliageCellGrid(float Size = 32f) {
    /// <summary>The smallest cell that is a cell.</summary>
    public const float MinimumSize = 1f;

    /// <summary>The size, guarded.</summary>
    public float CellSize => Size > MinimumSize ? Size : MinimumSize;

    /// <summary>Which cell a world position is in.</summary>
    /// <param name="position">The position.</param>
    /// <returns>The cell.</returns>
    /// <remarks>
    ///     ⚠ <b>Floor, not truncate.</b> Truncation folds −0.5 and +0.5 into the same cell, so the
    ///     four cells around the origin become two — which draws as a seam through the middle of
    ///     every level built around zero.
    /// </remarks>
    public FoliageCellKey CellOf(Vector3 position) =>
        new((int)MathF.Floor(position.X / CellSize), (int)MathF.Floor(position.Z / CellSize));

    /// <summary>Where a cell's low corner is.</summary>
    /// <param name="cell">The cell.</param>
    /// <returns>The corner, at height zero.</returns>
    public Vector3 OriginOf(FoliageCellKey cell) => new(cell.X * CellSize, 0f, cell.Z * CellSize);

    /// <summary>Every cell a circle touches.</summary>
    /// <param name="centre">Its centre, in world XZ.</param>
    /// <param name="radius">Its radius, in metres.</param>
    /// <returns>The cells, in row-major order.</returns>
    /// <remarks>
    ///     ⚠ <b>Every cell the circle's <em>square</em> touches, which is a few too many at the
    ///     corners.</b> The alternative is a per-cell distance test that rejects four cells out of a
    ///     few dozen and costs a square root each; what the caller does with a cell is test its
    ///     instances anyway, and an empty corner cell costs nothing.
    /// </remarks>
    public IEnumerable<FoliageCellKey> Touching(Vector2 centre, float radius) {
        var low = CellOf(new(centre.X - radius, 0f, centre.Y - radius));
        var high = CellOf(new(centre.X + radius, 0f, centre.Y + radius));

        for (var z = low.Z; z <= high.Z; z++) {
            for (var x = low.X; x <= high.X; x++) {
                yield return new(x, z);
            }
        }
    }
}

/// <summary>
///     Every instance of one type inside one cell, and the bounds they occupy.
/// </summary>
/// <remarks>
///     <para>
///         <b>What the instancing feature draws as one object.</b> The bounds are maintained as
///         instances arrive and are recomputed when one leaves, because a cull is only as good as the
///         box it tests — and a cell whose bounds never shrank would keep a whole forest's worth of
///         extent after an artist erased all but one tree.
///     </para>
///     <para>
///         ⚠ <b>One chunk per type per cell, not one per cell.</b> A cell holding three kinds of tree
///         is three batches, because a batch is one mesh — folding them together would mean a draw
///         that has to switch mesh mid-instance, which is not a thing.
///     </para>
/// </remarks>
public sealed class FoliageChunk {
    readonly List<FoliageInstance> instances = [];

    /// <summary>Creates an empty chunk.</summary>
    /// <param name="cell">Which cell it is in.</param>
    /// <param name="type">Which type's instances it holds, by index into the volume's palette.</param>
    public FoliageChunk(FoliageCellKey cell, int type) {
        Cell = cell;
        Type = type;
    }

    /// <summary>Which cell it is in.</summary>
    public FoliageCellKey Cell { get; }

    /// <summary>Which type's instances it holds.</summary>
    public int Type { get; }

    /// <summary>The instances.</summary>
    public IReadOnlyList<FoliageInstance> Instances => instances;

    /// <summary>How many there are.</summary>
    public int Count => instances.Count;

    /// <summary>Whether there are none.</summary>
    public bool IsEmpty => instances.Count == 0;

    /// <summary>Where they are, as one box.</summary>
    /// <remarks>
    ///     ⚠ <b>Extended by the instance's scaled reach rather than by its position.</b> A box built
    ///     from positions alone is a box that ends at the trunks, so every tree at the edge of a cell
    ///     pops out of existence while half of it is still on screen.
    /// </remarks>
    public BoundingBox Bounds { get; private set; } = new(
        new(float.PositiveInfinity),
        new(float.NegativeInfinity)
    );

    /// <summary>Adds an instance.</summary>
    /// <param name="instance">The instance.</param>
    /// <param name="reach">How far the mesh extends from its origin at unit scale, in metres.</param>
    public void Add(in FoliageInstance instance, float reach = 1f) {
        instances.Add(instance);
        Bounds = Grown(Bounds, instance, reach);
    }

    /// <summary>Removes the instance at an index.</summary>
    /// <param name="index">Which one.</param>
    /// <param name="reach">How far the mesh extends from its origin at unit scale.</param>
    /// <remarks>
    ///     ⚠ <b>The bounds are recomputed rather than shrunk in place, because a box cannot be
    ///     un-grown.</b> Removing is rare — an erase stroke does it in bulk and then the caller asks
    ///     once — so the honest cost is a pass over the survivors instead of a wrong box for ever.
    /// </remarks>
    public void RemoveAt(int index, float reach = 1f) {
        instances.RemoveAt(index);
        Recompute(reach);
    }

    /// <summary>Removes every instance a predicate accepts.</summary>
    /// <param name="accept">What to remove.</param>
    /// <param name="reach">How far the mesh extends from its origin at unit scale.</param>
    /// <returns>How many went.</returns>
    public int RemoveAll(Predicate<FoliageInstance> accept, float reach = 1f) {
        ArgumentNullException.ThrowIfNull(accept);

        var removed = instances.RemoveAll(accept);

        if (removed > 0) {
            Recompute(reach);
        }

        return removed;
    }

    /// <summary>Replaces the instance at an index.</summary>
    /// <param name="index">Which one.</param>
    /// <param name="instance">What it becomes.</param>
    /// <param name="reach">How far the mesh extends from its origin at unit scale.</param>
    /// <remarks>What a gizmo drag on a selected tree does, and what Reapply does to every instance
    ///     of a type at once.</remarks>
    public void Replace(int index, in FoliageInstance instance, float reach = 1f) {
        instances[index] = instance;
        Recompute(reach);
    }

    /// <summary>Empties it.</summary>
    public void Clear() {
        instances.Clear();
        Bounds = new(new(float.PositiveInfinity), new(float.NegativeInfinity));
    }

    void Recompute(float reach) {
        Bounds = new(new(float.PositiveInfinity), new(float.NegativeInfinity));

        foreach (var instance in instances) {
            Bounds = Grown(Bounds, instance, reach);
        }
    }

    static BoundingBox Grown(BoundingBox bounds, in FoliageInstance instance, float reach) {
        var extent = new Vector3(MathF.Max(reach, 0f) * instance.Scale);

        return new(
            Vector3.Min(bounds.Minimum, instance.Position - extent),
            Vector3.Max(bounds.Maximum, instance.Position + extent)
        );
    }
}

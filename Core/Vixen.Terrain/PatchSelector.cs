// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Terrain;

/// <summary>What a quadtree descent asks about a square of ground it is considering.</summary>
/// <remarks>
///     <para>
///         <b>The two questions, and only the two</b> — how tall is it, and is there anything there.
///         Everything else a descent needs is arithmetic on the node's own extent, which is why
///         <see cref="PatchSelector" /> can be shared between a terrain and a water zone
///         ([35 § D4](../../docs/plan/35-water.md#d4-the-surface-is-the-terrains-quadtree-with-a-different-height-source)):
///         the only difference between the two consumers is what the vertex stage samples for height.
///     </para>
///     <para>
///         <b>Implement it as a <c>readonly struct</c>.</b> <see cref="PatchSelector.Select" /> takes
///         it as a generic parameter rather than as an interface reference, so a source that is a
///         struct is called through a constrained call and a selection allocates nothing — which
///         matters because this runs once per frame per zone over a few hundred nodes.
///     </para>
/// </remarks>
public interface IPatchSource {
    /// <summary>Everything a node occupies, in world units.</summary>
    /// <param name="x">The node's low X, in quads from the root's origin.</param>
    /// <param name="z">The node's low Z.</param>
    /// <param name="quads">How many quads it spans along each axis.</param>
    /// <returns>The box, whose Y range is what the frustum test and the LOD distance both use.</returns>
    /// <remarks>
    ///     ⚠ <b>The Y range has to contain everything the vertex stage can produce.</b> For a terrain
    ///     that is the heightmap's range over the node; for water it is the surface plus the sea
    ///     state's maximum amplitude, because a node bounded by its rest height is a node culled away
    ///     while a crest is still in front of the camera.
    /// </remarks>
    BoundingBox BoundsOf(int x, int z, int quads);

    /// <summary>Whether a node has anything in it worth drawing.</summary>
    /// <param name="x">The node's low X, in quads from the root's origin.</param>
    /// <param name="z">The node's low Z.</param>
    /// <param name="quads">How many quads it spans.</param>
    /// <returns>False to prune the node and everything under it.</returns>
    /// <remarks>
    ///     <para>
    ///         A terrain answers "is this square inside the terrain at all" — the root covers a square
    ///         of whole patches large enough to hold it, so a terrain that is not a square power of
    ///         two leaves nodes hanging off the far edge. Water answers § D4's coverage predicate:
    ///         a lake is a small part of a zone, and descending into open ground to draw nothing is
    ///         most of the tree.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It must be conservative — true whenever <em>any</em> part of the node is covered.</b>
    ///         Pruning happens before the children are visited, so a source that answers from the
    ///         node's centre removes a shoreline that runs through a corner and the water ends in a
    ///         straight edge halfway across a tile.
    ///     </para>
    /// </remarks>
    bool Covers(int x, int z, int quads);
}

/// <summary>
///     The CDLOD descent, over any square of ground that can answer <see cref="IPatchSource" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>One quadtree, not two</b>
///         ([35 § D4](../../docs/plan/35-water.md#d4-the-surface-is-the-terrains-quadtree-with-a-different-height-source),
///         and [31 § D3](../../docs/plan/31-terrain-grass-and-trees.md#d3-a-quadtree-with-a-morph-not-a-clipmap)
///         for the scheme itself). Unreal has a landscape LOD system and a water LOD system, with two
///         morphs, two sets of bias cvars and two ways to get a crack. The selection, the morph, the
///         no-crack property and the continuity property are all functions of the node's extent and
///         the view's distance, and none of them is a function of what the height came from — so
///         there is one implementation and the tests are written once.
///     </para>
///     <para>
///         <b>Arithmetic, deliberately, with no device.</b> The two properties that matter are exactly
///         the ones a device cannot be asked about: whether the shared edge of two adjacent nodes
///         agrees, and whether a vertex moves continuously as the camera does. Both are functions, so
///         both are unit tests, and [35 § Part 4] says they are written before the renderer.
///     </para>
///     <para>
///         ⚠ <b>The node coordinates are quads from the root's origin, and the source places them.</b>
///         A terrain's origin is its own corner and a water zone's is a sliding window that moved this
///         frame; making the selector carry a world origin would give it an opinion about which, and
///         the two answers differ by whether the origin snaps. <see cref="IPatchSource.BoundsOf" /> is
///         where that is decided.
///     </para>
/// </remarks>
public sealed class PatchSelector {
    /// <summary>How many quads the shared grid patch spans, by default.</summary>
    /// <remarks>
    ///     32, so the patch is 33² vertices — about 2 KB of positions shared by every node of every
    ///     terrain and every water zone in the world. Large enough that a frame is a few hundred nodes
    ///     rather than a few thousand, small enough that a node is a useful cull unit.
    /// </remarks>
    public const int DefaultGridQuads = 32;

    /// <summary>Builds a selector over a square root node.</summary>
    /// <param name="rootQuads">How many quads the root spans along each axis.</param>
    /// <param name="depthCount">How many levels the descent may use. At least one.</param>
    /// <param name="ranges">Where each level takes over, and where each one morphs.</param>
    /// <exception cref="ArgumentException">The ranges or the extents cannot be used.</exception>
    public PatchSelector(int rootQuads, int depthCount, TerrainLodRanges ranges) {
        if (ranges.Validate() is { } why) {
            throw new ArgumentException(why, nameof(ranges));
        }

        if (rootQuads < 1) {
            throw new ArgumentException(
                $"The root has to span at least one quad; it spanned {rootQuads}.",
                nameof(rootQuads)
            );
        }

        if (depthCount < 1) {
            throw new ArgumentException(
                $"A descent has at least one level; it was given {depthCount}.",
                nameof(depthCount)
            );
        }

        RootQuads = rootQuads;
        DepthCount = depthCount;
        Ranges = ranges;
    }

    /// <summary>How many quads the root node spans.</summary>
    public int RootQuads { get; }

    /// <summary>How deep the descent goes, which may be fewer levels than the ranges describe.</summary>
    /// <remarks>
    ///     A small terrain runs out of ground before it runs out of levels, and a range list with more
    ///     levels than the ground has is not an error — it is a project setting shared by terrains and
    ///     zones of different sizes.
    /// </remarks>
    public int DepthCount { get; }

    /// <summary>Where each level takes over.</summary>
    public TerrainLodRanges Ranges { get; }

    /// <summary>How many quads a root has to span to hold a given number, at a given patch size.</summary>
    /// <param name="quads">How many quads the ground actually spans.</param>
    /// <param name="gridQuads">How many quads the shared grid patch spans.</param>
    /// <returns>The root extent, and how many levels it took to get there.</returns>
    /// <remarks>
    ///     Doubling from one patch until it fits, so the root is a whole number of patches and every
    ///     level below it is too. A root sized to the ground instead would put a fractional patch at
    ///     the finest level, which is the one place a shared grid patch cannot be shared.
    /// </remarks>
    public static (int RootQuads, int LevelCount) RootFor(int quads, int gridQuads = DefaultGridQuads) {
        if (gridQuads < 2 || (gridQuads & (gridQuads - 1)) != 0) {
            throw new ArgumentException(
                $"The grid patch must span a power of two quads of at least two; it was {gridQuads}.",
                nameof(gridQuads)
            );
        }

        var patches = 1;
        var levels = 1;

        while (patches * gridQuads < quads) {
            patches *= 2;
            levels++;
        }

        return (patches * gridQuads, levels);
    }

    /// <summary>Chooses the patches a view draws.</summary>
    /// <typeparam name="TSource">What answers for the ground. A struct, so this allocates nothing.</typeparam>
    /// <param name="viewPosition">Where the view is, in the same space <typeparamref name="TSource" /> reports bounds in.</param>
    /// <param name="frustum">What it can see.</param>
    /// <param name="source">The ground.</param>
    /// <param name="nodes">Where the chosen patches go. Appended to, not cleared.</param>
    /// <returns>How many were chosen.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="nodes" /> is null.</exception>
    /// <remarks>
    ///     The selected nodes tile the visible, covered ground exactly once: each step either takes the
    ///     node or takes its four children, which are disjoint and cover it. That is the invariant
    ///     worth asserting, because a tiling with a gap is a hole in the ground and a tiling with an
    ///     overlap is z-fighting.
    /// </remarks>
    public int Select<TSource>(
        Vector3 viewPosition,
        in BoundingFrustum frustum,
        TSource source,
        ICollection<TerrainLodNode> nodes
    )
        where TSource : IPatchSource {
        ArgumentNullException.ThrowIfNull(nodes);

        var before = nodes.Count;
        Descend(0, 0, RootQuads, DepthCount - 1, viewPosition, in frustum, source, nodes);

        return nodes.Count - before;
    }

    /// <summary>A grid index after morphing: odd indices slide back onto their even neighbour.</summary>
    /// <param name="index">The vertex's index in the patch.</param>
    /// <param name="morph">How far morphed, 0…1.</param>
    /// <returns>The morphed index, which is fractional in between.</returns>
    /// <remarks>
    ///     ⚠ <b>At a morph of one the patch has exactly half its resolution along each axis</b> — which
    ///     is its parent's, which is why the boundary between the two has nothing to leak through. The
    ///     C# form of what the vertex stage does, and the reason it is here: the shader cannot be asked
    ///     whether two nodes agree, and this can.
    /// </remarks>
    public static float MorphIndex(int index, float morph) =>
        index - ((index & 1) * Math.Clamp(morph, 0f, 1f));

    /// <summary>How far a point is from a box, and zero inside it.</summary>
    /// <param name="bounds">The box.</param>
    /// <param name="point">The point.</param>
    /// <returns>The distance, in the box's own units.</returns>
    public static float DistanceTo(in BoundingBox bounds, Vector3 point) {
        var dx = MathF.Max(0f, MathF.Max(bounds.Minimum.X - point.X, point.X - bounds.Maximum.X));
        var dy = MathF.Max(0f, MathF.Max(bounds.Minimum.Y - point.Y, point.Y - bounds.Maximum.Y));
        var dz = MathF.Max(0f, MathF.Max(bounds.Minimum.Z - point.Z, point.Z - bounds.Maximum.Z));

        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    void Descend<TSource>(
        int x,
        int z,
        int quads,
        int level,
        Vector3 viewPosition,
        in BoundingFrustum frustum,
        TSource source,
        ICollection<TerrainLodNode> nodes
    )
        where TSource : IPatchSource {
        // Pruned before the bounds are asked for, because the cheap question is the one that removes
        // most of the tree: a lake is a small part of a zone, and a terrain that is not a square power
        // of two patches leaves nodes hanging off its far edge.
        if (!source.Covers(x, z, quads)) {
            return;
        }

        var bounds = source.BoundsOf(x, z, quads);

        if (!frustum.Intersects(bounds)) {
            return;
        }

        var distance = DistanceTo(in bounds, viewPosition);

        // This level is coarse enough if the finer one's range does not reach here. Level 0 has no
        // finer one and is always the answer.
        if (level == 0 || distance > Ranges.RangeOf(level - 1)) {
            nodes.Add(new(x, z, quads, level, Ranges.MorphOf(level, distance)));

            return;
        }

        var half = quads / 2;

        Descend(x, z, half, level - 1, viewPosition, in frustum, source, nodes);
        Descend(x + half, z, half, level - 1, viewPosition, in frustum, source, nodes);
        Descend(x, z + half, half, level - 1, viewPosition, in frustum, source, nodes);
        Descend(x + half, z + half, half, level - 1, viewPosition, in frustum, source, nodes);
    }
}

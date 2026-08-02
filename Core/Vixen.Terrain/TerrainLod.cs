// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Terrain;

/// <summary>
///     Where each level of detail takes over, and where each one morphs.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § D3].</b> A level's range doubles with the level, which is what makes the
///         screen-space size of a quad roughly constant: twice as far away, twice as coarse.
///     </para>
///     <para>
///         ⚠ <b>The morph band of a level ends exactly where the next level begins, and that identity
///         is the whole no-crack argument.</b> A node is fully morphed into its parent's grid by the
///         time the parent takes over, so the two agree along the boundary they share. Any other
///         arrangement — a global morph distance, a band that stops short, a ratio applied to the
///         wrong end — leaves the finer node's shared edge somewhere the coarser node has no vertex,
///         which is a crack that daylight comes through.
///     </para>
/// </remarks>
[DataContract]
public readonly record struct TerrainLodRanges {
    /// <summary>How far level 0 reaches, in metres.</summary>
    public float NearRange { get; init; }

    /// <summary>How many levels there are.</summary>
    public int LevelCount { get; init; }

    /// <summary>
    ///     Where in a level's band the morph starts, 0…1 of the way from the previous range to this
    ///     one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         0 morphs across the whole band, which is smoothest and means a node is never drawn at
    ///         its own full detail. 1 morphs nowhere, which pops. Strugar's default is around 0.6 and
    ///         that is what <see cref="Default" /> uses: most of the band at full detail, the last
    ///         two fifths degenerating into the parent.
    ///     </para>
    ///     <para>
    ///         ⚠ It cannot be 1. A band with no width has no morph, so the finer node reaches the
    ///         transition undegenerate and every level boundary is a crack — which is why
    ///         <see cref="Validate" /> refuses it rather than letting it produce a terrain that looks
    ///         right until the camera moves.
    ///     </para>
    /// </remarks>
    public float MorphStartRatio { get; init; }

    /// <summary>Five levels from 64 m, morphing over the last two fifths of each band.</summary>
    public static TerrainLodRanges Default =>
        new() { NearRange = 64f, LevelCount = 5, MorphStartRatio = 0.6f };

    /// <summary>How far this level reaches, in metres. Beyond it, the next level takes over.</summary>
    /// <param name="level">Which level. 0 is the finest.</param>
    /// <returns>The distance.</returns>
    public float RangeOf(int level) => NearRange * (1 << Math.Clamp(level, 0, 30));

    /// <summary>Where this level's band begins — the previous level's range, or zero.</summary>
    /// <param name="level">Which level.</param>
    /// <returns>The distance.</returns>
    public float BandStartOf(int level) => level <= 0 ? 0f : RangeOf(level - 1);

    /// <summary>Where this level begins morphing towards its parent.</summary>
    /// <param name="level">Which level.</param>
    /// <returns>The distance.</returns>
    public float MorphStartOf(int level) {
        var start = BandStartOf(level);
        return start + ((RangeOf(level) - start) * Math.Clamp(MorphStartRatio, 0f, 0.99f));
    }

    /// <summary>Where this level is fully morphed into its parent. Its range, exactly.</summary>
    /// <param name="level">Which level.</param>
    /// <returns>The distance.</returns>
    public float MorphEndOf(int level) => RangeOf(level);

    /// <summary>How far a node of this level has morphed at a distance, 0…1.</summary>
    /// <param name="level">Which level.</param>
    /// <param name="distance">How far the view is from the node.</param>
    /// <returns>The morph, 0 at full detail and 1 fully degenerated into the parent grid.</returns>
    /// <remarks>
    ///     ⚠ <b>Computed from this level's own band, never from a global distance.</b> The tempting
    ///     simplification — one morph band for the whole terrain — makes two adjacent nodes at
    ///     different levels disagree about how far morphed their shared edge is, which produces
    ///     exactly the crack the morph exists to prevent. [docs/plan/31 § D3] names this as one of
    ///     the two classic bugs in the subsystem.
    /// </remarks>
    public float MorphOf(int level, float distance) {
        var start = MorphStartOf(level);
        var end = MorphEndOf(level);

        if (!(end > start)) {
            return 1f;
        }

        return Math.Clamp((distance - start) / (end - start), 0f, 1f);
    }

    /// <summary>Why these ranges cannot be used, or null if they can.</summary>
    /// <returns>The reason, in words a settings panel can show.</returns>
    public string? Validate() {
        if (!(NearRange > 0f) || !float.IsFinite(NearRange)) {
            return $"The near range must be positive and finite; it was {NearRange}.";
        }

        if (LevelCount is < 1 or > 24) {
            return $"There must be between one and twenty-four levels; there were {LevelCount}.";
        }

        if (!(MorphStartRatio >= 0f) || MorphStartRatio >= 1f) {
            return $"The morph must start somewhere inside the band, so the ratio must be at least "
                + $"zero and less than one; it was {MorphStartRatio}. A ratio of one leaves every "
                + "level boundary undegenerate, which is a crack at every transition.";
        }

        return null;
    }
}

/// <summary>One patch of terrain a frame draws, at one level of detail.</summary>
/// <param name="X">The low X of the samples it covers.</param>
/// <param name="Z">The low Z of the samples it covers.</param>
/// <param name="Quads">How many quads it spans along each axis.</param>
/// <param name="Level">Which level of detail it is. 0 is the finest.</param>
/// <param name="Morph">How far it has morphed towards its parent's grid, 0…1.</param>
/// <remarks>
///     Every node is drawn from the <em>same</em> instanced grid patch, scaled and placed — which is
///     what makes a terrain one draw call. This is the per-instance record that says where.
/// </remarks>
[DataContract]
public readonly record struct TerrainLodNode(int X, int Z, int Quads, int Level, float Morph) {
    /// <summary>The samples it covers, its far edge included.</summary>
    public TerrainRect Rect => new(X, Z, Quads + 1, Quads + 1);
}

/// <summary>
///     The quadtree a frame descends to decide which patches to draw, and the morph that joins them.
/// </summary>
/// <remarks>
///     <para>
///         <b>CDLOD</b> (Strugar, 2010), for the reasons in [docs/plan/31 § D3]: the node is
///         simultaneously the LOD unit, the cull unit and the draw, and it aligns with the tile — a
///         clipmap's rings align with nothing, because they are centred on the camera and everything
///         else is a rectangle in terrain space.
///     </para>
///     <para>
///         <b>No skirts, and that is the return on the morph.</b> A finer node has degenerated onto
///         its parent's grid by the time the parent takes over, so a boundary between two levels has
///         no gap to hide. Skirts are what a renderer grows when the morph is wrong, and then keeps.
///     </para>
///     <para>
///         <b>Arithmetic, deliberately.</b> This assembly has no device — [§ D1] — and the two
///         properties that matter are exactly the ones a device cannot be asked about: whether the
///         shared edge of two adjacent nodes agrees, and whether a vertex moves continuously as the
///         camera does. Both are functions, so both are unit tests. [§ Part 4] says the no-crack test
///         must be written before the renderer, not after it.
///     </para>
/// </remarks>
public sealed class TerrainLodTree {
    /// <summary>How many quads the shared grid patch spans, by default.</summary>
    /// <remarks>
    ///     32, so the patch is 33² vertices — about 2 KB of positions shared by every node of every
    ///     terrain in the world. Large enough that a frame is a few hundred nodes rather than a few
    ///     thousand, small enough that a node is a useful cull unit.
    /// </remarks>
    public const int DefaultGridQuads = 32;

    readonly TerrainDescription description;

    /// <summary>Builds a tree over a terrain's shape.</summary>
    /// <param name="description">The terrain's shape.</param>
    /// <param name="ranges">Where each level takes over.</param>
    /// <param name="gridQuads">How many quads the shared patch spans. A power of two.</param>
    /// <exception cref="ArgumentException">The shape or the ranges cannot be used.</exception>
    public TerrainLodTree(
        TerrainDescription description,
        TerrainLodRanges ranges,
        int gridQuads = DefaultGridQuads
    ) {
        if (description.Validate() is { } shapeReason) {
            throw new ArgumentException(shapeReason, nameof(description));
        }

        if (ranges.Validate() is { } rangeReason) {
            throw new ArgumentException(rangeReason, nameof(ranges));
        }

        if (gridQuads < 2 || (gridQuads & (gridQuads - 1)) != 0) {
            throw new ArgumentException(
                $"The grid patch must span a power of two quads of at least two; it was {gridQuads}.",
                nameof(gridQuads)
            );
        }

        this.description = description;
        Ranges = ranges;
        GridQuads = gridQuads;

        // The root covers a square of whole patches large enough to hold the terrain. A terrain that
        // is not square, or not a power of two patches across, leaves nodes hanging off the far edge;
        // Select skips them rather than clamping, because a clamped node would draw one patch's
        // worth of terrain twice.
        var quads = Math.Max(description.TilesX, description.TilesZ) * description.TileQuads;
        var patches = 1;
        var levels = 1;

        while (patches * gridQuads < quads) {
            patches *= 2;
            levels++;
        }

        RootQuads = patches * gridQuads;
        DepthCount = Math.Min(levels, ranges.LevelCount);
    }

    /// <summary>Where each level takes over.</summary>
    public TerrainLodRanges Ranges { get; }

    /// <summary>How many quads the shared grid patch spans.</summary>
    public int GridQuads { get; }

    /// <summary>How many quads the root node spans.</summary>
    public int RootQuads { get; }

    /// <summary>How deep the tree is, which may be fewer levels than the ranges describe.</summary>
    /// <remarks>
    ///     A small terrain runs out of terrain before it runs out of levels, and a range list with
    ///     more levels than the terrain has is not an error — it is a project setting shared by
    ///     terrains of different sizes.
    /// </remarks>
    public int DepthCount { get; }

    /// <summary>Chooses the patches a view draws.</summary>
    /// <param name="viewPosition">Where the view is, in the terrain's own space.</param>
    /// <param name="frustum">What it can see.</param>
    /// <param name="terrain">The terrain, for the height range a node's bounds need.</param>
    /// <param name="nodes">Where to put the chosen patches.</param>
    /// <returns>How many were chosen.</returns>
    /// <remarks>
    ///     The selected nodes tile the visible terrain exactly once: each step either takes the node
    ///     or takes its four children, which are disjoint and cover it. That is the invariant worth
    ///     asserting, because a tiling with a gap is a hole in the ground and a tiling with an overlap
    ///     is z-fighting.
    /// </remarks>
    public int Select(
        Vector3 viewPosition,
        in BoundingFrustum frustum,
        Terrain terrain,
        ICollection<TerrainLodNode> nodes
    ) {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(nodes);

        var before = nodes.Count;
        Descend(0, 0, RootQuads, DepthCount - 1, viewPosition, in frustum, terrain, nodes);
        return nodes.Count - before;
    }

    /// <summary>Everything a node occupies, in the terrain's own space.</summary>
    /// <param name="x">The node's low X in samples.</param>
    /// <param name="z">The node's low Z in samples.</param>
    /// <param name="quads">How many quads it spans.</param>
    /// <param name="terrain">The terrain, for the height range.</param>
    /// <returns>The box.</returns>
    public BoundingBox BoundsOf(int x, int z, int quads, Terrain terrain) {
        ArgumentNullException.ThrowIfNull(terrain);

        var (minimum, maximum) = terrain.HeightRangeOf(new(x, z, quads + 1, quads + 1));
        var scale = description.MetresPerQuad;

        return new(
            new(x * scale, minimum, z * scale),
            new((x + quads) * scale, maximum, (z + quads) * scale)
        );
    }

    /// <summary>
    ///     Where one vertex of the shared grid patch lands, in sample coordinates, after morphing.
    /// </summary>
    /// <param name="node">Which patch.</param>
    /// <param name="gridX">The vertex's X index in the patch, 0…<see cref="GridQuads" />.</param>
    /// <param name="gridZ">Its Z index.</param>
    /// <returns>The sample coordinate, which the vertex stage samples the heightmap at.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The morph snaps odd grid indices back onto even ones.</b> At
    ///         <see cref="TerrainLodNode.Morph" /> 1 every odd vertex has arrived at its even
    ///         neighbour, so the patch has exactly half its resolution along each axis — which is its
    ///         parent's, which is why the boundary between the two has nothing to leak through.
    ///     </para>
    ///     <para>
    ///         The C# form of what the vertex stage will do, and the reason it is here: the shader
    ///         cannot be asked whether two nodes agree, and this can.
    ///     </para>
    /// </remarks>
    public Vector2 SampleOf(in TerrainLodNode node, int gridX, int gridZ) {
        var step = node.Quads / (float)GridQuads;

        return new(
            node.X + (MorphIndex(gridX, node.Morph) * step),
            node.Z + (MorphIndex(gridZ, node.Morph) * step)
        );
    }

    /// <summary>Where one vertex of a patch lands in the terrain's own space, after morphing.</summary>
    /// <param name="node">Which patch.</param>
    /// <param name="gridX">The vertex's X index in the patch.</param>
    /// <param name="gridZ">Its Z index.</param>
    /// <param name="terrain">The terrain, for the height there.</param>
    /// <returns>The position, with Y in metres.</returns>
    /// <remarks>
    ///     Bilinear in the heightmap, because a morphed vertex lands between samples for every morph
    ///     but zero and one. Snapping to the nearest sample instead would make the surface jump by up
    ///     to one sample's height as the camera moves, which is the pop the morph exists to remove.
    /// </remarks>
    public Vector3 PositionOf(in TerrainLodNode node, int gridX, int gridZ, Terrain terrain) {
        ArgumentNullException.ThrowIfNull(terrain);

        var sample = SampleOf(node, gridX, gridZ);
        var scale = description.MetresPerQuad;

        return new(sample.X * scale, HeightAt(terrain, sample.X, sample.Y), sample.Y * scale);
    }

    /// <summary>A grid index after morphing: odd indices slide back onto their even neighbour.</summary>
    /// <param name="index">The vertex's index in the patch.</param>
    /// <param name="morph">How far morphed, 0…1.</param>
    /// <returns>The morphed index, which is fractional in between.</returns>
    public static float MorphIndex(int index, float morph) =>
        index - ((index & 1) * Math.Clamp(morph, 0f, 1f));

    static float HeightAt(Terrain terrain, float x, float z) {
        var x0 = (int)MathF.Floor(x);
        var z0 = (int)MathF.Floor(z);
        var fx = x - x0;
        var fz = z - z0;

        var a = terrain.Composite.MetresAt(x0, z0);
        var b = terrain.Composite.MetresAt(x0 + 1, z0);
        var c = terrain.Composite.MetresAt(x0, z0 + 1);
        var d = terrain.Composite.MetresAt(x0 + 1, z0 + 1);

        return (((a * (1f - fx)) + (b * fx)) * (1f - fz)) + (((c * (1f - fx)) + (d * fx)) * fz);
    }

    void Descend(
        int x,
        int z,
        int quads,
        int level,
        Vector3 viewPosition,
        in BoundingFrustum frustum,
        Terrain terrain,
        ICollection<TerrainLodNode> nodes
    ) {
        // Off the edge of a terrain that is not a square power of two patches. Skipped rather than
        // clamped, because a clamped node would draw one patch's worth of ground twice.
        if (x >= description.SamplesX - 1 || z >= description.SamplesZ - 1) {
            return;
        }

        var bounds = BoundsOf(x, z, quads, terrain);

        if (!frustum.Intersects(bounds)) {
            return;
        }

        var distance = DistanceTo(bounds, viewPosition);

        // This level is coarse enough if the finer one's range does not reach here. Level 0 has no
        // finer one and is always the answer.
        if (level == 0 || distance > Ranges.RangeOf(level - 1)) {
            nodes.Add(new(x, z, quads, level, Ranges.MorphOf(level, distance)));
            return;
        }

        var half = quads / 2;

        Descend(x, z, half, level - 1, viewPosition, in frustum, terrain, nodes);
        Descend(x + half, z, half, level - 1, viewPosition, in frustum, terrain, nodes);
        Descend(x, z + half, half, level - 1, viewPosition, in frustum, terrain, nodes);
        Descend(x + half, z + half, half, level - 1, viewPosition, in frustum, terrain, nodes);
    }

    static float DistanceTo(in BoundingBox bounds, Vector3 point) {
        var dx = MathF.Max(0f, MathF.Max(bounds.Minimum.X - point.X, point.X - bounds.Maximum.X));
        var dy = MathF.Max(0f, MathF.Max(bounds.Minimum.Y - point.Y, point.Y - bounds.Maximum.Y));
        var dz = MathF.Max(0f, MathF.Max(bounds.Minimum.Z - point.Z, point.Z - bounds.Maximum.Z));

        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }
}

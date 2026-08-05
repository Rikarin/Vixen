// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Terrain;

namespace Vixen.Water;

/// <summary>
///     Which patches of water a view draws, and where each of their vertices lands.
/// </summary>
/// <remarks>
///     <para>
///         <b>The terrain's quadtree with a different height source</b>
///         ([35 § D4](../../docs/plan/35-water.md#d4-the-surface-is-the-terrains-quadtree-with-a-different-height-source)).
///         <see cref="PatchSelector" /> selects the nodes, morphs them and guarantees no cracks; every
///         word of the terrain's CDLOD argument is true of water, and the only difference is what the
///         vertex stage samples for height — a heightmap mip chain there, the info field plus the wave
///         sum here. Unreal has two quadtrees, two morph implementations and two sets of debug
///         commands; this is the other answer.
///     </para>
///     <para>
///         <b>The finest node's vertex spacing is the info field's texel spacing, by construction.</b>
///         The root spans the window's <c>Resolution − 1</c> quads rounded up to whole patches, so a
///         512 m window at 257 texels gives 256 quads at two metres — one vertex per texel at level
///         zero. Any other choice makes the surface either carry detail the field cannot supply or
///         throw away detail it can.
///     </para>
///     <para>
///         ⚠ <b>The node's error metric includes the sea state's maximum amplitude, not just the
///         height range.</b> § D4 asks for "density where the waves are, not where the ground is", and
///         the bound is the harder half of it: a node bounded by its rest height is a node culled away
///         while a crest is still in front of the camera, and the symptom is a strip of missing sea
///         that appears only in a swell.
///     </para>
/// </remarks>
public sealed class WaterSurfaceMesh {
    readonly PatchSelector selector;

    WaterFieldPyramid? pyramid;

    /// <summary>Builds a mesh over a window's shape.</summary>
    /// <param name="window">The window the field covers. Its origin moves; its shape does not.</param>
    /// <param name="ranges">Where each level takes over, and where each one morphs.</param>
    /// <param name="gridQuads">How many quads the shared grid patch spans. A power of two.</param>
    /// <exception cref="ArgumentException">The window, the ranges or the patch cannot be used.</exception>
    public WaterSurfaceMesh(
        in WaterFieldDescription window,
        TerrainLodRanges ranges,
        int gridQuads = PatchSelector.DefaultGridQuads
    ) {
        if (window.Validate() is { } why) {
            throw new ArgumentException(why, nameof(window));
        }

        Window = window;
        GridQuads = gridQuads;

        var (rootQuads, levels) = PatchSelector.RootFor(window.Resolution - 1, gridQuads);

        selector = new(rootQuads, Math.Min(levels, ranges.LevelCount), ranges);
        MetresPerQuad = window.Extent / rootQuads;
    }

    /// <summary>The window the mesh covers. Its origin follows the field's.</summary>
    public WaterFieldDescription Window { get; private set; }

    /// <summary>How many quads the shared grid patch spans.</summary>
    public int GridQuads { get; }

    /// <summary>How many metres one quad of the finest level covers.</summary>
    public float MetresPerQuad { get; }

    /// <summary>The descent, shared with the terrain.</summary>
    public PatchSelector Selector => selector;

    /// <summary>Where each level takes over.</summary>
    public TerrainLodRanges Ranges => selector.Ranges;

    /// <summary>The tallest the sea state can be above its rest height, in metres.</summary>
    /// <remarks><see cref="WaterWaveSpectrum.MaximumAmplitude" />, which is what a node's box is grown by.</remarks>
    public float MaximumAmplitude { get; private set; }

    /// <summary>How far past the window the far mesh reaches, in metres.</summary>
    /// <remarks>
    ///     § D4's far mesh: "one more node level past the selector's range, drawn with a permutation
    ///     that skips the wave sum and the flow". It is a low-vertex skirt to the horizon, and it is
    ///     what makes an ocean an ocean rather than a square of water with an edge.
    /// </remarks>
    public float FarDistance { get; set; } = 8000f;

    /// <summary>How wide the band is over which the waves fade out at the window's edge, in metres.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The seam between the near mesh and the far mesh, and the one place the drawn
    ///         surface is deliberately not the queried one.</b> The far mesh has no field under it and
    ///         so no waves; the near mesh at the window's edge has full waves. Meeting them directly
    ///         puts a step the height of a crest along a straight line at the horizon, in every frame.
    ///         So the wave amplitude ramps to zero across this band and the two agree exactly where
    ///         they meet.
    ///     </para>
    ///     <para>
    ///         <b>It is in the mesh rather than in <see cref="WaterEvaluator" />, and that is the
    ///         narrower divergence of the two available.</b> Putting it in the evaluator would make
    ///         every buoyancy query and every immersion test depend on how far the <em>camera</em> is
    ///         from the boat, which is § D2's seam broken in the worst way — a raft that rides
    ///         differently depending on where somebody is looking. Here the disagreement is confined to
    ///         a band the view is by construction half a window away from, and it is stated rather than
    ///         discovered.
    ///     </para>
    /// </remarks>
    public float EdgeFade { get; set; } = 32f;

    /// <summary>Points the mesh at a field, and tells it how tall the sea is.</summary>
    /// <param name="reduced">The field's coverage and surface range, reduced for the descent.</param>
    /// <param name="maximumAmplitude">The sea state's maximum amplitude, in metres.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reduced" /> is null.</exception>
    /// <exception cref="ArgumentException">Its resolution is not the window's.</exception>
    public void Update(WaterFieldPyramid reduced, float maximumAmplitude) {
        ArgumentNullException.ThrowIfNull(reduced);

        if (reduced.Resolution != Window.Resolution) {
            throw new ArgumentException(
                $"The mesh spans {Window.Resolution}² texels and the reduction covers "
                + $"{reduced.Resolution}².",
                nameof(reduced)
            );
        }

        pyramid = reduced;
        Window = reduced.Description;
        MaximumAmplitude = MathF.Max(maximumAmplitude, 0f);
    }

    /// <summary>Chooses the patches inside the window a view draws.</summary>
    /// <param name="viewPosition">Where the view is, in world units.</param>
    /// <param name="frustum">What it can see.</param>
    /// <param name="nodes">Where the chosen patches go. Appended to, not cleared.</param>
    /// <returns>How many were chosen, and zero before <see cref="Update" /> has been called.</returns>
    public int Select(Vector3 viewPosition, in BoundingFrustum frustum, ICollection<TerrainLodNode> nodes) {
        ArgumentNullException.ThrowIfNull(nodes);

        if (pyramid is null) {
            return 0;
        }

        return selector.Select(
            viewPosition,
            in frustum,
            new WaterPatchSource(pyramid, Window.Origin, MetresPerQuad, MaximumAmplitude),
            nodes
        );
    }

    /// <summary>Chooses the skirt patches beyond the window — the far mesh.</summary>
    /// <param name="frustum">What the view can see.</param>
    /// <param name="restHeight">Where the open surface sits, in world units.</param>
    /// <param name="nodes">Where the chosen patches go. Appended to, not cleared.</param>
    /// <returns>How many were chosen.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Window-sized cells tiling a ring, at one level past the descent's coarsest.</b> There
    ///         is no field out here and so nothing to descend on: every cell is the same flat rest
    ///         height, so subdividing one buys nothing but vertices. The permutation the renderer
    ///         compiles for these skips the wave sum and the flow, which is § D4's own wording.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Morph zero, and the join is <see cref="EdgeFade" />'s rather than the morph's.</b> A
    ///         morph joins two levels of the <em>same</em> height source; these two have different ones,
    ///         and a morph cannot reconcile a wave with a plane.
    ///     </para>
    /// </remarks>
    public int SelectFar(in BoundingFrustum frustum, float restHeight, ICollection<TerrainLodNode> nodes) {
        ArgumentNullException.ThrowIfNull(nodes);

        var cells = (int)MathF.Ceiling(FarDistance / Window.Extent);

        if (cells <= 0) {
            return 0;
        }

        var before = nodes.Count;
        var level = selector.DepthCount;

        for (var z = -cells; z <= cells; z++) {
            for (var x = -cells; x <= cells; x++) {
                if (x == 0 && z == 0) {
                    // The window itself, which the near selection already tiled.
                    continue;
                }

                var lowX = Window.Origin.X + (x * Window.Extent);
                var lowZ = Window.Origin.Y + (z * Window.Extent);

                var bounds = new BoundingBox(
                    new(lowX, restHeight, lowZ),
                    new(lowX + Window.Extent, restHeight, lowZ + Window.Extent)
                );

                if (!frustum.Intersects(bounds)) {
                    continue;
                }

                nodes.Add(new(x * selector.RootQuads, z * selector.RootQuads, selector.RootQuads, level, 0f));
            }
        }

        return nodes.Count - before;
    }

    /// <summary>Where one vertex of a patch lands on the ground plane, after morphing.</summary>
    /// <param name="node">Which patch.</param>
    /// <param name="gridX">The vertex's X index in the patch, 0…<see cref="GridQuads" />.</param>
    /// <param name="gridZ">Its Z index.</param>
    /// <returns>The world X and Z, before the waves displace it.</returns>
    /// <remarks>
    ///     The C# form of the first half of what the vertex stage does, and the reason it is here: the
    ///     shader cannot be asked whether two adjacent nodes agree along their shared edge, and this
    ///     can. [§ Part 4] says the no-crack test is written before the renderer.
    /// </remarks>
    public Vector2 GroundOf(in TerrainLodNode node, int gridX, int gridZ) {
        var step = node.Quads / (float)GridQuads * MetresPerQuad;

        return new(
            Window.Origin.X + (node.X * MetresPerQuad) + (PatchSelector.MorphIndex(gridX, node.Morph) * step),
            Window.Origin.Y + (node.Z * MetresPerQuad) + (PatchSelector.MorphIndex(gridZ, node.Morph) * step)
        );
    }

    /// <summary>How much of the sea state survives at a place, before the depth attenuation.</summary>
    /// <param name="position">Where, on the ground plane.</param>
    /// <returns>The factor, 0…1 — one well inside the window, zero at its edge.</returns>
    /// <remarks>See <see cref="EdgeFade" /> for why this exists and what it costs.</remarks>
    public float EdgeDamping(Vector2 position) {
        if (!(EdgeFade > 0f)) {
            return 1f;
        }

        var low = position - Window.Origin;
        var high = new Vector2(Window.Extent) - low;

        var nearest = MathF.Min(MathF.Min(low.X, low.Y), MathF.Min(high.X, high.Y));

        return WaterMath.SmoothStep(0f, EdgeFade, nearest);
    }

    /// <summary>Where one vertex of a patch lands in the world, waves included.</summary>
    /// <param name="node">Which patch.</param>
    /// <param name="gridX">The vertex's X index in the patch.</param>
    /// <param name="gridZ">Its Z index.</param>
    /// <param name="evaluator">The one definition of where the surface is.</param>
    /// <param name="waterTime">The simulation's water time, in seconds.</param>
    /// <returns>The position, and the surface normal there.</returns>
    /// <remarks>
    ///     <b>Displacement, not a height lookup</b>, because a Gerstner wave moves a vertex sideways as
    ///     well as up — which is what makes a crest sharpen instead of a sine wave rolling past. The
    ///     horizontal terms are why the mesh cannot be a heightfield and why the no-crack property has
    ///     to survive them: two adjacent nodes agree along their shared edge only if they agree about
    ///     the <em>undisplaced</em> position first, which <see cref="GroundOf" /> is.
    /// </remarks>
    public (Vector3 Position, Vector3 Normal) PositionOf(
        in TerrainLodNode node,
        int gridX,
        int gridZ,
        in WaterEvaluator evaluator,
        float waterTime
    ) {
        var ground = GroundOf(in node, gridX, gridZ);
        var basis = evaluator.Field?.Sample(ground) ?? WaterFieldSample.None;

        var damping = evaluator.Field is null
            ? EdgeDamping(ground)
            : evaluator.Attenuation.At(basis.Depth) * EdgeDamping(ground);

        evaluator.Displace(ground, waterTime, damping, out var offset, out var normal);

        return (
            new Vector3(ground.X + offset.X, basis.SurfaceHeight + offset.Y, ground.Y + offset.Z),
            normal
        );
    }

    /// <summary>A water field, as the shared descent asks about it.</summary>
    readonly struct WaterPatchSource(
        WaterFieldPyramid pyramid,
        Vector2 origin,
        float metresPerQuad,
        float amplitude
    ) : IPatchSource {
        public BoundingBox BoundsOf(int x, int z, int quads) {
            var (low, high) = Corners(x, z, quads);
            var (least, most) = pyramid.SurfaceRange(low, high);

            // An empty range means the node missed the window. Answering a degenerate box at the
            // origin would put it in front of any camera near zero; answering one at the node's own
            // place with no height keeps the frustum test honest.
            if (!(most >= least)) {
                least = 0f;
                most = 0f;
            }

            return new(
                new(low.X, least - amplitude, low.Y),
                new(high.X, most + amplitude, high.Y)
            );
        }

        public bool Covers(int x, int z, int quads) {
            var (low, high) = Corners(x, z, quads);

            return pyramid.AnyCoverage(low, high);
        }

        (Vector2 Low, Vector2 High) Corners(int x, int z, int quads) {
            var low = origin + new Vector2(x * metresPerQuad, z * metresPerQuad);

            return (low, low + new Vector2(quads * metresPerQuad));
        }
    }
}

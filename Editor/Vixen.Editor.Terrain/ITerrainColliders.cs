// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Terrain;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.Terrain;

/// <summary>
///     What rebuilds a terrain's collision when its ground moves.
/// </summary>
/// <remarks>
///     <para>
///         <b>An interface for the reason <c>IMeshBaker</c> is one: this assembly does not reference
///         <c>Vixen.Physics</c>, and should not.</b> Nothing in the editor does — physics is a leaf
///         of the graph, reached by the runtime and by the play session rather than by the tools —
///         so the editor says <em>which tiles moved</em> and whatever holds a physics world turns
///         that into shapes. The pieces on the other side already exist:
///         <c>TerrainSamples.FillCollisionSamples</c> fills a tile's heights in metres with holes
///         written as the caller's sentinel, and <c>PhysicsShapes.HeightField</c> takes exactly that.
///     </para>
///     <para>
///         ✅ <b>Implemented by <c>Vixen.Editor.Terrain.Physics.TerrainColliders</c></b>, over
///         <c>Vixen.Terrain.Physics.TerrainColliderSystem</c> — one height-field shape per tile, over
///         an <c>ITerrainPlacements</c>. It is a third assembly because it has to be: a runtime
///         assembly may not reference <c>Editor/</c>, and this one may not link physics.
///     </para>
///     <para>
///         ⚠ <b>The remark here used to say an adapter was "three lines", and that was nearly true in
///         the way that costs a day.</b> <c>TerrainColliderSystem</c>'s two <c>Rebuild</c> overloads
///         return <see langword="bool" /> where these return <see langword="void" />, and
///         <see langword="false" /> means <em>that system has never heard of that terrain</em> — so a
///         wrapper that forwarded and discarded would succeed, loudly, at rebuilding nothing.
///     </para>
///     <para>
///         ⚠ <b>Who sets <c>TerrainEdit.Colliders</c> was the other half, and it was the half nobody
///         had counted.</b> For a year this was assigned in five test files and nowhere else, so
///         every assertion about a stroke naming the right tiles was one double talking to another.
///         <c>TerrainModule</c> now takes an implementation from <c>PluginServices</c> when the host
///         published one.
///     </para>
///     <para>
///         ⚠ <b>Per tile, and that is [§ D2]'s whole argument rather than an optimisation.</b> A tile
///         is one Jolt height-field shape; a stroke touches one or two of them out of sixteen, and
///         rebuilding the terrain's collision because somebody smoothed a ridge is a pause an artist
///         feels every time they let go of the mouse.
///     </para>
///     <para>
///         ⚠ <b>Null is a terrain with no collision, not an error.</b> A scene being sculpted before
///         anybody has pressed play has no physics world, and a mode that refused to work without one
///         would be a mode that cannot be used until the game runs.
///     </para>
///     <para>
///         ⚠ <b>And what the shipped editor publishes is a <em>switch</em> rather than a rebuilder,
///         because the two ends have different lifetimes.</b> <c>BindColliders</c> below resolves the
///         service once and keeps the answer; the physics world it would rebuild in exists only while
///         a play session does. <c>Vixen.Editor.Terrain.Physics</c>' module publishes one long-lived
///         implementation that forwards to whatever is simulating and counts the strokes that arrived
///         when nothing was — which is the paragraph above, with a number attached.
///     </para>
/// </remarks>
public interface ITerrainColliders {
    /// <summary>Rebuilds one tile's collision shape.</summary>
    /// <param name="terrain">The terrain, already recomposited.</param>
    /// <param name="tileX">The tile's X index.</param>
    /// <param name="tileZ">Its Z index.</param>
    /// <remarks>
    ///     Called after <see cref="TerrainMap.Resolve" />, so the composite it reads is the one the
    ///     artist can see. A collider built from a stale composite is ground the player falls through
    ///     in exactly the places that were most recently edited.
    /// </remarks>
    void Rebuild(TerrainMap terrain, int tileX, int tileZ);

    /// <summary>Rebuilds every tile a rectangle of samples touches.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="rect">Which samples moved.</param>
    /// <remarks>
    ///     ⚠ <b>Through <see cref="TerrainDescription.TilesOf" />, which answers with <em>both</em>
    ///     tiles for a sample on a boundary.</b> A stroke along a tile edge that rebuilt only one
    ///     side leaves a strip of collision that disagrees with the ground beside it by whatever the
    ///     stroke moved — a lip the player trips on, on a seam nothing draws.
    /// </remarks>
    void Rebuild(TerrainMap terrain, TerrainRect rect) {
        ArgumentNullException.ThrowIfNull(terrain);

        var tiles = terrain.Description.TilesOf(rect);

        for (var tileZ = tiles.Z; tileZ < tiles.EndZ; tileZ++) {
            for (var tileX = tiles.X; tileX < tiles.EndX; tileX++) {
                Rebuild(terrain, tileX, tileZ);
            }
        }
    }
}

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
///         ⚠ <b>Nothing in the tree implements this, and that is still true.</b> The only
///         implementation is a test double that records tile indices. The runtime half of the job
///         landed as <c>Vixen.Terrain.Physics.TerrainColliderSystem</c> — one height-field shape per
///         tile, over an <c>ITerrainPlacements</c> — and its
///         <c>Rebuild(Terrain, TerrainRect)</c> deliberately carries this interface's signature, so
///         an adapter is three lines. It has to live on <em>this</em> side of the line, because a
///         runtime assembly may not reference <c>Editor/</c>; until somebody writes it, a sculpt
///         stroke in the editor moves the ground and rebuilds no collision.
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

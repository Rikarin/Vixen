// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Foliage;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Rendering.Terrain;

/// <summary>Turns the names a scene carries into the objects a frame draws from.</summary>
/// <remarks>
///     <para>
///         <b><see cref="ITerrainScene" />'s runtime sibling, one seam earlier.</b> That interface
///         answers "what terrains should this viewport draw" with heightfields already in hand,
///         which is the editor's shape — it owns a project directory and reads files. A game world
///         holds only names, and turning a name into a heightfield means an asset manager, a cache
///         and a "not yet" answer while bytes are in flight — none of which belongs beside an ECS
///         query. <c>Vixen.Engine.Renderer</c>'s <c>AssetTerrainSource</c> is the implementation,
///         for the reason <c>AssetTerrainTextures</c> lives there: it is the assembly where the
///         asset system and the render system meet.
///     </para>
///     <para>
///         ⚠ <b>Nothing here waits.</b> A load is started on the first ask and the answer is null
///         until it lands; the extraction system leaves the entity out of the frame and asks again
///         next frame — <c>AssetMeshSource</c>'s contract, for the same reason: a synchronous read
///         inside extraction stalls the first frame of a level on every terrain in it.
///     </para>
/// </remarks>
public interface ITerrainAssetSource {
    /// <summary>The terrain a reference names, or null while it is loading or unloadable.</summary>
    /// <param name="reference">The component's reference.</param>
    /// <returns>The terrain, resolved and cached, or null.</returns>
    TerrainMap? Terrain(string reference);

    /// <summary>The grass type a reference names, or null on the same terms.</summary>
    /// <param name="reference">The component's reference.</param>
    /// <returns>The type, or null.</returns>
    GrassType? Grass(string reference);
}

/// <summary>One terrain the frame draws: the heightfield, its placement, and its rules.</summary>
/// <param name="Terrain">The heightfield.</param>
/// <param name="Origin">Where its low corner is, in world space — the entity's translation.</param>
/// <param name="NearRange">How far level 0 reaches, or zero for the node's quality default.</param>
/// <param name="LodBias">Levels added to every pick, positive for coarser. ⚠ Carried, not yet consumed.</param>
/// <param name="CastShadows">Whether it casts. ⚠ Carried, not yet consumed — casters are a tracked task.</param>
/// <param name="Grass">The grass rule, or null for a terrain with none.</param>
/// <param name="GrassRange">How far grass cells stay resident, in metres. Zero takes the default.</param>
public readonly record struct TerrainSceneEntry(
    TerrainMap Terrain,
    Vector3 Origin,
    float NearRange,
    int LodBias,
    bool CastShadows,
    GrassType? Grass,
    float GrassRange
);

/// <summary>The frame's terrains, written by the extraction system and read by the compositor node.</summary>
/// <remarks>
///     <para>
///         <b>The stable object between two things with different lifetimes.</b> The extraction
///         system is registered once when the loop is assembled; the compositor node is rebuilt on
///         every document load. Neither can hold the other, so both hold this —
///         <c>ForwardLightingRenderFeature.Lights</c> is the same arrangement for the same reason,
///         and <c>TerrainFactory.Scene</c> is how the node's end gets wired.
///     </para>
///     <para>
///         ⚠ <b>Rebuilt every frame rather than mirrored.</b> A terrain has no handle to reconcile:
///         the list is cleared and refilled, which costs a walk over the handful of entities that
///         carry ground and removes the class of bug where a destroyed entity leaves a landscape
///         standing.
///     </para>
/// </remarks>
public sealed class TerrainSceneSource {
    /// <summary>This frame's terrains, in extraction order.</summary>
    public List<TerrainSceneEntry> Terrains { get; } = [];

    /// <summary>What turns a layer's texture reference into something to sample, or null.</summary>
    /// <remarks>
    ///     Here rather than on the node, because it exists only once content is mounted and the
    ///     node is built from a document that may load first. Null draws every layer as the
    ///     renderer's white default — <see cref="TerrainRenderer.Textures" />'s own contract.
    /// </remarks>
    public ITerrainTextures? Textures { get; set; }
}

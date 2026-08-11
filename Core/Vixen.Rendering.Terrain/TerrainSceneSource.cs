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

    /// <summary>The foliage type a <c>.vxfoliage</c> reference names, or null on the same terms.</summary>
    /// <param name="reference">The component's reference.</param>
    /// <returns>The type, or null.</returns>
    FoliageType? Foliage(string reference);

    /// <summary>The instance volume a <c>.vxfol</c> reference names, or null on the same terms.</summary>
    /// <param name="reference">The component's reference.</param>
    /// <param name="palette">The resolved palette, in the component's order.</param>
    /// <returns>The volume, with the palette applied, or null.</returns>
    /// <remarks>
    ///     ⚠ <b>The palette rides the ask, because the bytes cannot be read without it.</b>
    ///     <see cref="Vixen.Foliage.FoliageStore.Read" /> drops any chunk whose type index is past
    ///     the palette — deliberately — so a volume read first and dressed later would arrive
    ///     empty. The implementation caches the built volume per reference and rebuilds only when
    ///     the palette's content changes.
    /// </remarks>
    FoliageVolume? Volume(string reference, IReadOnlyList<FoliageType> palette);
}

/// <summary>One terrain the frame draws: the heightfield, its placement, and its rules.</summary>
/// <param name="Terrain">The heightfield.</param>
/// <param name="Origin">Where its low corner is, in world space — the entity's translation.</param>
/// <param name="NearRange">How far level 0 reaches, or zero for the node's quality default.</param>
/// <param name="LodBias">Levels added to every pick, positive for coarser. ⚠ Carried, not yet consumed.</param>
/// <param name="CastShadows">
///     Whether it draws into the sun's cascade atlas — <see cref="TerrainCasterRenderer" /> is the
///     consumer, and a frame whose every terrain says false declares no caster pass at all.
/// </param>
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

/// <summary>One foliage volume the frame draws: the instances, their placement, and their reach.</summary>
/// <param name="Volume">The instances, palette applied — the same object frame over frame.</param>
/// <param name="Origin">Where the volume's origin stands, in world space — the entity's translation.</param>
/// <param name="Range">How far cells stay uploaded, in metres. Zero takes the node's default.</param>
/// <remarks>
///     ⚠ <b>The volume's identity is the entry's identity.</b> The compositor node keys its device
///     state — the cull pass, the streamer, the uploaded meshes — by the volume object, so an asset
///     source that rebuilt the volume every frame would rebuild all of it every frame.
///     <c>CastShadows</c> is not carried here: it is per type, readable off
///     <see cref="FoliageVolume.Palette" />, which is where the shadow caster task consumes it.
/// </remarks>
public readonly record struct FoliageSceneEntry(FoliageVolume Volume, Vector3 Origin, float Range);

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

    /// <summary>What second the wind is at, from the frame's clock.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The frame's clock, not the node's own.</b> <c>TerrainSceneRenderer</c> used to
    ///         hold a <c>Stopwatch</c> started when it was constructed, which made the grass's sway a
    ///         function of how long the <em>process</em> had been alive — content load, shader
    ///         compile and pipeline warm-up included. Two headless runs at the same
    ///         <c>--vixen-frames</c> therefore drew every blade at a different phase, and no flag
    ///         could make them agree because nothing about that clock was reachable.
    ///     </para>
    ///     <para>
    ///         Negative for a source nobody has extracted, which the node reads as "use zero" rather
    ///         than as a wind that has run backwards. <c>GrassDrawPass.Prepare</c> already takes the
    ///         time as a parameter, and its remarks already say why — so that two views of one field
    ///         agree about where the blades are — which is the same reason this is here.
    ///     </para>
    /// </remarks>
    public float Time { get; set; } = -1f;

    /// <summary>And its foliage volumes, on the same terms.</summary>
    public List<FoliageSceneEntry> Foliage { get; } = [];

    /// <summary>What turns a foliage type's mesh reference into geometry, or null.</summary>
    /// <remarks>
    ///     On <see cref="Textures" />' terms: it exists once content is mounted, and the node built
    ///     from a document that loaded first asks every frame until it does. Null draws no foliage —
    ///     a mesh has no honest white default the way a texture does — and
    ///     <c>TerrainSceneRenderer</c>'s counters are where a person sees the difference between
    ///     "not mounted" and "not loaded yet".
    /// </remarks>
    public Vixen.Rendering.Ecs.IMeshSource? Meshes { get; set; }

    /// <summary>What turns a layer's texture reference into something to sample, or null.</summary>
    /// <remarks>
    ///     Here rather than on the node, because it exists only once content is mounted and the
    ///     node is built from a document that may load first. Null draws every layer as the
    ///     renderer's white default — <see cref="TerrainRenderer.Textures" />'s own contract.
    /// </remarks>
    public ITerrainTextures? Textures { get; set; }
}

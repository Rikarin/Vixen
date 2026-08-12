// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Terrain;

/// <summary>One terrain standing in a world: the heightfield, and where its low corner is.</summary>
/// <param name="Terrain">The heightfield.</param>
/// <param name="Origin">
///     Where sample (0, 0) sits in world metres — the terrain entity's translation.
/// </param>
/// <remarks>
///     ⚠ <b>The terrain's identity is the placement's identity.</b> Whoever resolves a scene's
///     references is expected to hand back the same <see cref="Terrain" /> object frame after frame,
///     which is what lets a consumer keep per-terrain state — a set of collision bodies, say — keyed
///     by it. A source that rebuilt the map every frame would make every consumer rebuild everything
///     every frame, and nothing would say so.
/// </remarks>
public readonly record struct TerrainPlacement(Terrain Terrain, Vector3 Origin);

/// <summary>Where the terrains in a world are, for everything that is not drawing them.</summary>
/// <remarks>
///     <para>
///         <b><c>IWaterSurface</c>'s counterpart for the ground, and it exists for that interface's
///         reason</b> — [35 § D1](../../docs/plan/35-water.md#d1-three-assemblies-and-the-kernel-touches-no-device),
///         which is [31 § D1](../../docs/plan/31-terrain-grass-and-trees.md#d1-two-runtime-assemblies-and-one-editor-assembly-and-the-kernel-touches-no-device)
///         one subsystem over.
///         Turning a <c>TerrainComponent</c>'s asset name into a loaded heightfield and placing it at
///         its entity's transform is work only the render stack does today, because that is where the
///         asset source and the extraction pass live. Everything <em>else</em> that needs to know
///         where the ground is — a collider, a spawner, a navmesh bake, a dedicated server — would
///         otherwise have to reference the renderer to find out, which puts a graphics device in a
///         headless build.
///     </para>
///     <para>
///         So the renderer implements this and the rest of the engine reads it. Nothing on this
///         interface is a device type: a <see cref="Terrain" /> is arrays and a
///         <see cref="TerrainPlacement.Origin" /> is three floats.
///     </para>
///     <para>
///         ⚠ <b>An empty answer is a world whose terrains have not loaded yet, not a world without
///         ground.</b> A <c>.vxterrain</c> is tens of megabytes and resolves several frames after the
///         level is up — <c>ITerrainAssetSource</c> starts a load and answers null meanwhile — so a
///         consumer that asks once at load time gets nothing, silently, and never asks again. Ask
///         every frame; the list is cheap and a scene has a handful of terrains.
///     </para>
/// </remarks>
public interface ITerrainPlacements {
    /// <summary>How many terrains are placed.</summary>
    int PlacementCount { get; }

    /// <summary>The placement at an index.</summary>
    /// <param name="index">Which one, below <see cref="PlacementCount" />.</param>
    /// <returns>The terrain and its origin.</returns>
    /// <remarks>
    ///     An indexer rather than a list, so an implementation whose own storage is a list of
    ///     something richer — the renderer's is — can answer without projecting a new collection
    ///     every frame.
    /// </remarks>
    TerrainPlacement PlacementAt(int index);
}

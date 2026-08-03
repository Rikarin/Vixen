// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Rendering.Terrain;

/// <summary>What terrains a viewport should draw, and where each one is.</summary>
/// <remarks>
///     <para>
///         <b>The seam between a presenter with no asset database and a toolset with no
///         device</b> — the same division <c>SceneMeshes.Meshes</c> and <c>IMeshBaker</c> already
///         make. A <c>TerrainComponent</c> names a <c>.vxterrain</c>; turning that name into a
///         heightfield means reading a file, caching it and reporting when it is not there, none of
///         which belongs beside a draw call.
///     </para>
///     <para>
///         ⚠ <b>Here rather than in the editor, because the two ends are in different assemblies
///         now.</b> The implementation is <c>Vixen.Editor.Terrain</c>'s — it is the thing that reads
///         a <c>.vxterrain</c> and caches it — and the consumer is the editor's scene presenter. Doc
///         36 § P3 moved the first out of the application, and a contract owned by one end would have
///         been a reference back to it.
///     </para>
///     <para>
///         ⚠ <b>The origin is the entity's translation and there is no rotation.</b>
///         <c>TerrainComponent</c> offers none on purpose: a terrain's samples are its space, so a
///         rotated one would make every tool that maps a world position to a sample carry an inverse.
///         A presenter that took a full transform would be offering something no tool could honour.
///     </para>
/// </remarks>
public interface ITerrainScene {
    /// <summary>Every terrain in the scene, and where its low corner is in world space.</summary>
    /// <returns>The list, which may be empty.</returns>
    /// <remarks>
    ///     ⚠ <b>A list rather than an enumerable, and it is read once per pane per frame.</b> A
    ///     four-pane layout asks four times, so an implementation that walked the world lazily would
    ///     walk it four times — and would be walking it while a pane's upload is in progress.
    /// </remarks>
    IReadOnlyList<(TerrainMap Terrain, Vector3 Origin)> Terrains();
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Assets.MeshMaps;
using Vixen.Geometry;
using Vixen.Geometry.Remeshing;

namespace Vixen.Editor.App.Tests;

/// <summary>Both halves of a bake in one call, for a test that has no reason to separate them.</summary>
/// <remarks>
///     <para>
///         <b>This used to be <c>IMeshMapBaker.Bake</c>, and it is here because it was never anything
///         else</b> — <a href="https://github.com/Rikarin/Vixen/issues/972">#972</a>. That member had
///         three callers and all three were test classes: the editor bakes through
///         <c>EditorParity.BakeSelectedMeshMaps</c> → <c>ContentTasks.BakeMeshMaps</c> →
///         <c>MapBaker.Bake</c> on the pool → <c>IMeshMapBaker.Write</c> on the frame thread, and the
///         split is deliberate — the arithmetic takes the minutes and the write is what touches
///         <c>Assets/</c>.
///     </para>
///     <para>
///         ⚠ <b>Published on a plugin contract it was a trap on.</b> <c>IMeshMapBaker</c> is what
///         <c>PluginServices</c> hands a third party, and a plugin calling the synchronous both-halves
///         form from the frame thread stalls the editor for the length of a 4K bake with nothing in
///         the signature saying so. <c>ContentTasks</c> exists because of that. Keeping a
///         convenience with that shape on the contract, for the sake of callers that are all in this
///         assembly, is how a seam becomes a hazard nobody chose.
///     </para>
///     <para>
///         ⚠ <b>One helper rather than one per test class.</b> Three transcriptions of two lines is
///         how two of them come to disagree about whether the warnings are carried into the set —
///         which is the difference between a set that reports a clipped ray budget and one that does
///         not.
///     </para>
/// </remarks>
static class MeshMapBakes {
    /// <summary>Measures a mesh's maps and writes them into the project, in one call.</summary>
    /// <param name="baker">Where the files land.</param>
    /// <param name="model">The model asset the mesh was read out of.</param>
    /// <param name="mesh">What to call the set. Sanitised by <c>Write</c>, not here.</param>
    /// <param name="source">The high-resolution surface.</param>
    /// <param name="target">The mesh with the atlas the maps land in.</param>
    /// <param name="settings">The size, the gutter, the search radius and which maps to measure.</param>
    /// <returns>What each usage became, and what the bake could not do.</returns>
    public static MeshMapSet Bake(
        this IMeshMapBaker baker,
        AssetId model,
        string mesh,
        EditMesh source,
        EditMesh target,
        BakeSettings settings
    ) {
        ArgumentNullException.ThrowIfNull(baker);

        var maps = MapBaker.Bake(source, target, settings);

        return baker.Write(model, mesh, MeshMapBake.Encode(maps), maps.Warnings);
    }
}

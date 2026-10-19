// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets;

namespace Vixen.Editor.Terrain;

/// <summary>The importers this assembly brings to a host that links it.</summary>
/// <remarks>
///     <para>
///         <b>#1300's option A, the terrain's half.</b> <see cref="TerrainAssetImporter" /> and
///         <see cref="HeightmapImporter" /> lived in <c>Vixen.Editor.Assets</c>'s hand list, which made the
///         import pipeline spell five terrain extensions. They live beside the toolset that authors those
///         files now, and reach a registry because every host that links this assembly — the editor
///         (<c>EditorModules</c>), <c>vixen</c> (<c>VixenCommand</c>) and the compiler worker
///         (<c>WorkerHost</c>) — calls <see cref="Link" /> at start-up.
///     </para>
///     <para>
///         ⚠ <b>Into <see cref="ImporterContributions.Linked" />, never <see cref="ImporterContributions.Default" /></b>,
///         for <c>VfxGraphImporters</c>' reason: a worker handed this assembly as a plugin and linking it
///         too would have two importers claiming <c>.vxgrass</c>.
///     </para>
/// </remarks>
public static class TerrainImporters {
    /// <summary>Links this assembly's importers into a set.</summary>
    /// <param name="linked">The set — <see cref="ImporterContributions.Linked" />, or a test's own.</param>
    public static void Link(ImporterContributions linked) {
        ArgumentNullException.ThrowIfNull(linked);

        linked.Link(new TerrainAssetImporter());
        linked.Link(new HeightmapImporter());
    }
}

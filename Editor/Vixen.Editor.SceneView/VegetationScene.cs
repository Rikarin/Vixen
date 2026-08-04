// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Foliage;

namespace Vixen.Editor.SceneView;

/// <summary>What vegetation a viewport should draw: the painted volume, and how to name its meshes.</summary>
/// <remarks>
///     <para>
///         <b><c>ITerrainScene</c>'s sibling, for the things that grow on the ground rather than the
///         ground itself.</b> The foliage tools paint into a <see cref="FoliageVolume" /> the terrain
///         module owns, and the presenter that could draw those instances had no way to reach it — so
///         the very editor that authors foliage showed none. This is the seam: the module answers
///         "what is painted", the presenter draws what it is handed.
///     </para>
///     <para>
///         ⚠ <b>Here rather than in <c>Vixen.Rendering.Terrain</c>, because both ends are editor
///         assemblies.</b> <c>ITerrainScene</c> sits in Core because a shipping renderer consumes it;
///         what this answers — a palette entry's mesh <em>name</em> turned into an asset reference the
///         viewport's own mesh source understands — is the editor's project database speaking, and a
///         Core contract would drag that concept into an assembly whose job is a draw call.
///     </para>
///     <para>
///         ⚠ <b>The volume is the live one, not a copy.</b> A stroke lands between two frames and the
///         next frame draws it, which is the whole point of previewing in the authoring viewport. The
///         presenter must therefore read it inside its own upload rather than caching chunks across
///         frames.
///     </para>
/// </remarks>
public interface IVegetationScene {
    /// <summary>The scene's painted foliage, or null while nothing supplies one.</summary>
    /// <returns>The volume — palette and instances — which may be empty.</returns>
    FoliageVolume? Foliage();

    /// <summary>Turns a palette entry's mesh name into a reference the viewport's mesh source takes.</summary>
    /// <param name="reference">What the type named. Empty means the type declares no mesh.</param>
    /// <returns>The reference, or <see cref="AssetReference.Null" /> when the name resolves to nothing.</returns>
    AssetReference MeshOf(string reference);
}

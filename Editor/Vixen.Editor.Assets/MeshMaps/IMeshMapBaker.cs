// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Geometry;
using Vixen.Geometry.Remeshing;

namespace Vixen.Editor.Assets.MeshMaps;

/// <summary>Something that can bake a mesh's maps into the project and say what assets they became.</summary>
/// <remarks>
///     <para>
///         <b>The seam doc 24's <c>IMeshBaker</c> established, for doc 48 § D12's maps.</b> Same
///         bargain and deliberately not a second arrangement: the thing that wants a bake says what
///         it wants, and the application, which owns the asset database, answers. A panel or a plugin
///         that knew how to mint a GUID would be the coupling doc 11's layering exists to prevent,
///         and there is exactly one implementation of this because there is exactly one asset
///         database.
///     </para>
///     <para>
///         ⚠ <b>It returns references rather than paths, keyed by usage.</b> § 4.8's Mesh Map Input
///         binds by usage — that is what makes one generator compound work on every mesh — so the
///         answer is shaped like the question. A path would be a fact about today, which is doc 08's
///         whole argument.
///     </para>
///     <para>
///         ⚠ <b>What comes back is an ordinary project asset and not a cache.</b> § D12 is explicit:
///         an artist opens the curvature map when a generator misbehaves, and a build wants not to
///         re-bake it. Both of those need a file in <c>Assets/</c> with a sidecar beside it, which is
///         the difference between this and writing the same pixels into <c>Library/</c>.
///     </para>
/// </remarks>
public interface IMeshMapBaker {
    /// <summary>Bakes a mesh's maps and puts them in the project.</summary>
    /// <param name="mesh">What to call the set. Every file in it is named from this.</param>
    /// <param name="source">The high-resolution surface. May be the same mesh as the target.</param>
    /// <param name="target">The mesh with the atlas the maps land in.</param>
    /// <param name="settings">The size, the gutter, the search radius and which maps to measure.</param>
    /// <returns>What each usage became, and what the bake could not do.</returns>
    MeshMapSet Bake(string mesh, EditMesh source, EditMesh target, BakeSettings settings);

    /// <summary>Puts maps that have already been baked into the project.</summary>
    /// <param name="mesh">What to call the set.</param>
    /// <param name="images">The files, as <see cref="MeshMapBake.Encode" /> produced them.</param>
    /// <param name="warnings">What the bake could not do, to be carried into the set.</param>
    /// <returns>What each usage became.</returns>
    /// <remarks>
    ///     ⚠ <b>The half that must run where the asset database lives, and the reason the two halves
    ///     are separable at all.</b> A bake of a 2K atlas with a hemisphere at every texel is seconds
    ///     to minutes of arithmetic and belongs on a pool thread; a scan is a directory walk that
    ///     rewrites the index every panel in the editor is reading. Splitting them is what lets the
    ///     editor bake without freezing and without a second thread touching the database — see
    ///     <c>ContentTasks</c>, which does exactly that. A caller with no such problem uses
    ///     <see cref="Bake" /> and never sees this.
    /// </remarks>
    MeshMapSet Write(string mesh, IReadOnlyList<MeshMapImage> images, IReadOnlyList<string> warnings);
}

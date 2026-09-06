// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
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
///         ⚠ <b>One method, and there used to be two</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/972">#972</a>. A <c>Bake</c> that measured
///         and wrote in one call sat here for four batches with three callers, all of them test
///         classes: the editor's own path is <c>ContentTasks.BakeMeshMaps</c>, which runs
///         <c>MapBaker.Bake</c> on the pool and calls <see cref="Write" /> on the frame thread,
///         because the arithmetic takes the minutes and the write is what touches <c>Assets/</c>. On
///         a contract handed to a third party the convenience was a hazard rather than a kindness: a
///         plugin calling the synchronous both-halves form from the frame thread stalls the editor
///         for the length of a 4K bake and nothing in its signature said so. It lives in the test
///         assembly now, which is where all of its callers already were.
///     </para>
///     <para>
///         ⚠ <b>What comes back is an ordinary project asset and not a cache.</b> § D12 is explicit:
///         an artist opens the curvature map when a generator misbehaves, and a build wants not to
///         re-bake it. Both of those need a file in <c>Assets/</c> with a sidecar beside it, which is
///         the difference between this and writing the same pixels into <c>Library/</c>.
///     </para>
/// </remarks>
public interface IMeshMapBaker {
    /// <summary>Puts maps that have already been baked into the project.</summary>
    /// <param name="model">
    ///     The model asset the mesh was read out of, or <see cref="AssetId.Empty" /> where the caller
    ///     has none.
    /// </param>
    /// <param name="mesh">What to call the set.</param>
    /// <param name="images">The files, as <see cref="MeshMapBake.Encode" /> produced them.</param>
    /// <param name="warnings">What the bake could not do, to be carried into the set.</param>
    /// <returns>What each usage became.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The name is this method's to choose and the caller's only to suggest.</b>
    ///         <paramref name="mesh" /> is a person's typing — Assimp hands back whatever the artist
    ///         called the object — so it is made safe here, where the folder it lands in is known,
    ///         rather than at encode time where it is not. A mesh called <c>../Wall</c> used to write
    ///         nine PNGs outside <c>Assets/</c> altogether.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><paramref name="model" /> is what separates a re-bake from a collision</b>, and
    ///         they are not distinguishable from the name: two models whose meshes are both called
    ///         <c>Cube</c> produce the same nine file names. A re-bake of the same model's same mesh
    ///         overwrites and keeps its GUIDs — an artist raising the ray count has to change the
    ///         maps their generators are already reading. A different model's set is never
    ///         overwritten; it is written beside, and the set says so in its warnings. See
    ///         <see cref="MeshMapNaming.ModelKey" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The half that must run where the asset database lives, and the reason the two
    ///         halves are separable at all.</b> A bake of a 2K atlas with a hemisphere at every texel
    ///         is seconds to minutes of arithmetic and belongs on a pool thread; a scan is a
    ///         directory walk that rewrites the index every panel in the editor is reading. Splitting
    ///         them is what lets the editor bake without freezing and without a second thread
    ///         touching the database — see <c>ContentTasks</c>, which does exactly that. A caller with
    ///         no such problem runs <c>MapBaker.Bake</c> and <c>MeshMapBake.Encode</c> itself and then
    ///         calls this, which is two lines and says plainly which of the two is the slow one.
    ///     </para>
    /// </remarks>
    MeshMapSet Write(
        AssetId model,
        string mesh,
        IReadOnlyList<MeshMapImage> images,
        IReadOnlyList<string> warnings
    );
}

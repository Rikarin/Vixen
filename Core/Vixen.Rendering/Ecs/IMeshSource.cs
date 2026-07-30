// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Rendering.Ecs;

/// <summary>Where the geometry a <see cref="MeshRenderable" /> names comes from.</summary>
/// <remarks>
///     <para>
///         <b>An interface here rather than an <c>AssetManager</c>, because this assembly must not know
///         what a bundle is.</b> A renderer's business is a vertex buffer; catalogs, addresses and
///         claims belong to <c>Vixen.Assets</c>, and a project that loads its meshes from a file, a
///         test that hands over an array, and an editor that has the mesh in memory already are all
///         legitimate. <c>Vixen.Engine.Renderer</c> is where the two meet — see <c>AssetMeshSource</c>.
///     </para>
///     <para>
///         <b>The whole design is in the return value.</b> Loading is asynchronous and extraction runs
///         inside a frame, so this asks rather than waits: false means "not yet", the entity is left
///         without a <see cref="RenderHandle" />, and next frame's reconciliation asks again. A
///         synchronous load here would stall the frame a level starts on every mesh in it, which is the
///         worst frame in the run to spend I/O in.
///     </para>
/// </remarks>
public interface IMeshSource {
    /// <summary>The mesh a reference names, if it is here yet.</summary>
    /// <param name="reference">Which mesh.</param>
    /// <param name="mesh">The geometry, when this returns true.</param>
    /// <returns>Whether it is loaded.</returns>
    /// <remarks>
    ///     <b>Asking is what starts the load.</b> An implementation is expected to begin one on the
    ///     first miss and to answer false until it lands, so a caller never has to say "and also fetch
    ///     this" — an extraction system that had to do both would have two chances to forget one.
    /// </remarks>
    bool TryGet(AssetReference reference, out MeshData mesh);
}

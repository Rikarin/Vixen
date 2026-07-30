// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Rendering.Ecs;

/// <summary>What a mesh reference turned out to be, when asked for its clusters.</summary>
/// <remarks>
///     <b>Three states and not two, which is the whole reason this is not a <c>bool</c>.</b> "This mesh
///     has no cluster hierarchy" and "it has one and it is not here yet" lead to opposite decisions: the
///     first falls through to the ordinary vertex-buffer path immediately, and the second must wait, or
///     the mesh is drawn the wrong way for the rest of the level. A single false would collapse them.
/// </remarks>
public enum ClusterState {
    /// <summary>This mesh was not built with a cluster hierarchy. Draw it the ordinary way.</summary>
    /// <remarks>
    ///     What a mesh imported with <c>GenerateMeshlets</c> off is, and what the fallback of one is not:
    ///     the fallback mesh exists precisely so that a virtualized model still has something for the
    ///     paths the virtualized one does not reach.
    /// </remarks>
    None,

    /// <summary>It has one and it has not arrived. Ask again next frame.</summary>
    Waiting,

    /// <summary>It has one and it is registered.</summary>
    Ready
}

/// <summary>Where the cluster hierarchy a <see cref="MeshRenderable" /> names comes from.</summary>
/// <remarks>
///     <para>
///         <b>The counterpart <c>VirtualGeometrySystem</c> was missing.</b> Every piece of the
///         virtualized path was finished from import to shaded pixel, and nothing looked at whether a
///         model had a hierarchy and routed it — so the whole system was reachable from code and not
///         from a scene. This is what <see cref="MeshExtractionSystem" /> asks before it falls back to
///         the ordinary path.
///     </para>
///     <para>
///         <b>Asked first and not second.</b> A mesh with clusters also has a fallback mesh, and both
///         resolve; asking the ordinary source first would draw every virtualized model through the
///         vertex buffer and nothing would ever notice, because the fallback is a correct picture of the
///         same object.
///     </para>
///     <para>
///         The same ask-don't-wait protocol as <see cref="IMeshSource" />, for the same reason: an
///         entity whose hierarchy has not arrived keeps no <see cref="RenderHandle" />, so the next
///         reconciliation asks again.
///     </para>
/// </remarks>
public interface IVirtualGeometrySource {
    /// <summary>The registration a reference names, if it has clusters and they are here.</summary>
    /// <param name="reference">Which mesh.</param>
    /// <param name="mesh">
    ///     The index to put in <c>VirtualGeometryDraw.Mesh</c>, when this returns
    ///     <see cref="ClusterState.Ready" />.
    /// </param>
    /// <param name="bounds">Its bind-pose bound in object space, for the culling loop.</param>
    /// <returns>What the reference turned out to be.</returns>
    ClusterState TryGet(AssetReference reference, out int mesh, out BoundingSphere bounds);
}

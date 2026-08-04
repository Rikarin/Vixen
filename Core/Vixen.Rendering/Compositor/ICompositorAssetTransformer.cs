// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Rendering.Compositor;

/// <summary>
///     Rewrites a compositor document before the builder reads it.
/// </summary>
/// <remarks>
///     <para>
///         The document-level half of <see cref="ISceneRendererFactory" />, and it exists for the same
///         reason: a node kind that expands into other node kinds — <c>!StandardFrame</c> into the
///         graph it stands for — has to do its expanding where those kinds are known, and the effect
///         set is downstream of this assembly. A factory can build one node from one asset; it cannot
///         add resources or stages to the document around it, which is exactly what an expansion has
///         to do. So a factory that also implements this is asked first, with the whole document in
///         its hands.
///     </para>
///     <para>
///         <see cref="CompositorBuilder.Build(GraphicsCompositorAsset)" /> applies every registered
///         transformer once, in
///         registration order, before stages, resources or nodes are read — so everything downstream
///         of the transform sees only the expanded document and nothing ever meets the node that was
///         expanded away. A transform must be pure: the same document in, the same document out,
///         because a build may run again from the same asset on any reload.
///     </para>
/// </remarks>
public interface ICompositorAssetTransformer {
    /// <summary>Returns the document to build — the input itself when there is nothing to expand.</summary>
    /// <param name="document">The authored document.</param>
    /// <param name="builder">
    ///     The builder the expansion is for, on <see cref="ISceneRendererFactory.Create" />'s exact
    ///     terms: the environment a file cannot carry — <see cref="CompositorBuilder.Quality" /> is
    ///     the entry a preset expansion reads. A transform must stay pure in it: the same document
    ///     and the same builder state in, the same document out.
    /// </param>
    GraphicsCompositorAsset Transform(GraphicsCompositorAsset document, CompositorBuilder builder);
}

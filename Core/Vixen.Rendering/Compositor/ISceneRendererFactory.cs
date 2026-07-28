// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Rendering.Compositor;

/// <summary>
///     Builds compositor nodes this assembly does not know about.
/// </summary>
/// <remarks>
///     <para>
///         A compositor is a document, and a document names node kinds — <c>!SingleStage</c>,
///         <c>!RenderPass</c>, <c>!Bloom</c>. <see cref="CompositorBuilder" /> can turn the kinds it
///         defines into renderers by switching on the asset's type, and it cannot do that for a kind
///         defined anywhere downstream of it: the effect set is a project this one does not reference,
///         and a game's own effect is a project neither of them has heard of.
///     </para>
///     <para>
///         So the switch has a default: whoever defines a node kind supplies the factory that builds
///         it, and registers it on the builder. That is the whole extension point — the asset carries
///         the values, the factory carries the knowledge of what to make of them, and the builder
///         carries the device, the module cache and the allocators that neither of the other two can.
///     </para>
/// </remarks>
public interface ISceneRendererFactory {
    /// <summary>Builds the node for an asset, or null when this factory does not recognise it.</summary>
    /// <param name="declared">The authored node.</param>
    /// <param name="builder">
    ///     The build in progress, for the four things a document cannot carry: the device, the module
    ///     cache, the descriptor allocator and the sampler cache. Null on any of them is not an error
    ///     — a node built without a device declines to draw, which is what an editor opening a
    ///     document before it has a window wants.
    /// </param>
    /// <returns>The node, or null.</returns>
    SceneRenderer? Create(ISceneRendererAsset declared, CompositorBuilder builder);
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Rendering.Ecs;

/// <summary>Where the material a <see cref="MeshRenderable" /> names comes from.</summary>
/// <remarks>
///     <para>
///         <b><see cref="IMeshSource" />'s counterpart, and the same protocol for the same reason.</b>
///         A material is loaded, compiled and — where a feature samples one — has textures of its own to
///         fetch, none of which can happen inside the frame that first asks for it. So this asks rather
///         than waits: false means "not yet", the entity keeps no <see cref="RenderHandle" />, and next
///         frame's reconciliation asks again.
///     </para>
///     <para>
///         <b>An interface here rather than an <c>AssetManager</c>, because this assembly must not know
///         what a bundle is.</b> A renderer's business is a descriptor set; catalogs, addresses and
///         claims belong to <c>Vixen.Assets</c>. <c>Vixen.Engine.Renderer</c> is where the two meet —
///         see <c>AssetMaterialSource</c>.
///     </para>
///     <para>
///         ⚠ <b>What comes back is a shared <see cref="Material" />, not a copy.</b> Two entities
///         naming one material get one object and therefore one descriptor set, one uniform block and
///         one slot in <see cref="Features.MaterialRenderFeature.Materials" /> — which is what makes a
///         level of a thousand crates cost one material rather than a thousand. An implementation that
///         compiled per ask would be correct and would multiply the frame's per-material work by the
///         instance count.
///     </para>
/// </remarks>
public interface IMaterialSource {
    /// <summary>The material a reference names, if it is ready yet.</summary>
    /// <param name="reference">Which material.</param>
    /// <param name="material">The material, when this returns true.</param>
    /// <returns>Whether it is ready.</returns>
    /// <remarks>
    ///     <b>Asking is what starts the load</b>, exactly as it is for a mesh: an implementation begins
    ///     one on the first miss and answers false until it lands, so a caller never has to say "and
    ///     also fetch this".
    /// </remarks>
    bool TryGet(AssetReference reference, out Material material);
}

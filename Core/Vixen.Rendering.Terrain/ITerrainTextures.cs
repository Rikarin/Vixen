// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;

namespace Vixen.Rendering.Terrain;

/// <summary>
///     What turns a layer's texture reference into something the splat loop can sample.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T4]'s owed seam.</b> A <c>.vxlayer</c> names its albedo, its normal and
///         its surface map as strings, because a layer is content and a reference in content is a
///         name. Turning a name into a <see cref="TextureViewHandle" /> is the asset database's job,
///         and a renderer that did it would need one in a class whose job is a draw call — the same
///         division <see cref="FoliageDraw" /> makes for meshes.
///     </para>
///     <para>
///         ⚠ <b>It answers with a handle or with nothing, and never blocks.</b> A layer's texture is
///         resolved during <c>Upload</c>, which is on the frame's critical path; a source that loaded
///         from disk on demand would make the first frame after a layer was assigned drop whatever
///         the load took. Returning <see langword="default" /> for something not yet resident is what
///         lets the renderer bind its own default and try again next frame.
///     </para>
///     <para>
///         ⚠ <b>And it is asked again whenever the layer list changes, not once.</b> A layer's
///         texture is assigned in a panel, and a source that was consulted at construction would show
///         the default for ever.
///     </para>
/// </remarks>
public interface ITerrainTextures {
    /// <summary>The view a reference names, or <see langword="default" /> if there is not one.</summary>
    /// <param name="reference">What the layer named. Empty means the layer declares none.</param>
    /// <returns>The view, or <see langword="default" />.</returns>
    TextureViewHandle Resolve(string reference);
}

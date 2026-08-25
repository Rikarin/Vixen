// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors.Shading;
using Vixen.Editor.ShaderGraph;
using Vixen.Graphics;
using Vixen.Ui.Renderer;

namespace Vixen.Editor.App;

/// <summary>Where a shader-graph preview's target becomes a number the interface draws.</summary>
/// <remarks>
///     <para>
///         <b>The host's half of <see cref="IPreviewImages" />, and the same seam
///         <see cref="ThumbnailSurface" /> is.</b> The shader graph knows how to compile a node's
///         expression and draw it; what it cannot do is name a texture, because that is
///         <c>UiRenderer.RegisterImage</c>'s and the renderer belongs to a window.
///     </para>
///     <para>
///         ⚠ <b>Its numbers start far above every other range in this editor.</b>
///         <c>ScenePresenter</c> takes 1 and the panes take the next few; <c>ThumbnailCache</c> takes
///         <c>0x1000</c> upwards and never reuses one. A collision would draw a node's thumbnail in
///         the viewport, or the viewport in a node's thumbnail — so this range is chosen to be one
///         nothing else can climb into.
///     </para>
/// </remarks>
sealed class UiPreviewImages(UiRenderer renderer) : IPreviewImages {
    /// <summary>Where a preview's image numbers start.</summary>
    public const ulong FirstImage = 0x0001_0000_0000_0000;

    readonly UiRenderer renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));

    ulong next = FirstImage;

    /// <inheritdoc />
    public ulong Register(TextureViewHandle view) {
        var image = next++;

        renderer.RegisterImage(image, view);

        return image;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Nothing to do, and that is not an oversight.</b> <c>UiRenderer</c> has no
    ///     unregister — an image number's descriptor sets are made once and rewritten, because its
    ///     pools are created without <c>FreeDescriptorSetBit</c> on purpose. What makes releasing safe
    ///     is that the renderer idles the device before destroying a target and never hands the same
    ///     number out twice, so no draw list can still carry one.
    /// </remarks>
    public void Release(ulong image) { }
}

sealed partial class EditorApplication {
    ShaderGraphPreviewRenderer? previews;

    /// <summary>What draws a shader graph's preview thumbnails, once the host has a device.</summary>
    /// <remarks>
    ///     ⚠ <b>Owned by the host and only held here.</b> It is made when the device and the main
    ///     window's renderer both exist and destroyed before the device goes, which is
    ///     <c>EditorHost</c>'s ordering and not this class's. Setting it reaches every shader graph
    ///     that is <i>already</i> open as well as the ones opened afterwards: a session restore opens
    ///     documents before the first frame, so the ones that matter most are always the early ones.
    /// </remarks>
    public ShaderGraphPreviewRenderer? ShaderGraphPreviews {
        get => previews;
        set {
            previews = value;

            foreach (var document in project.Documents) {
                if (document is ShaderGraphDocument shader) {
                    shader.PreviewSource = value;
                }
            }
        }
    }

    /// <summary>Hands a newly opened shader graph whatever renders its previews.</summary>
    void Preview(ShaderGraphDocument shader) => shader.PreviewSource = previews;
}

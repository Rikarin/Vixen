// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Platform;
using Vixen.Ui;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;

namespace Vixen.Editor.App;

/// <summary>One window's half of a frame: a swapchain, a renderer and the geometry between them.</summary>
/// <remarks>
///     <para>
///         <b>One of these per surface, which is one per window.</b> The document lays out and draws
///         every surface in one pass — see <c>UiSurface</c> — and what cannot be shared below that is
///         everything with a swapchain image in it: the window's own extent, its own scale, its own
///         vertex data.
///     </para>
///     <para>
///         ⚠ <b>A <see cref="UiRenderer" /> each, rather than one shared between the windows.</b> The
///         renderer rings its vertex and box buffers across the device's frames in flight and
///         advances a region per <c>Upload</c> — so two uploads in one device frame consume two
///         regions, and after as many frames as there are regions the second window would be writing
///         over geometry the GPU is still reading. Sharing it is a validation-clean way to draw
///         yesterday's frame. Two renderers cost two sets of pipelines, which an editor with a
///         torn-off panel or two can afford and a corrupted frame is not.
///     </para>
///     <para>
///         ⚠ <b>It exists without a device, and that is what keeps <c>--frames</c> honest.</b> On a
///         machine with no Vulkan the surface is still laid out, still drawn and still turned into
///         vertices; only the presenting is missing. A pane that came into existence with a
///         swapchain would make the headless run prove nothing about the window it never opened.
///     </para>
/// </remarks>
sealed class EditorPane : IDisposable {
    /// <summary>The framebuffer size the swapchain was last built for.</summary>
    /// <remarks>
    ///     ⚠ <b>What was asked for, not what came back.</b> The surface decides its own extent —
    ///     <c>VkSurfaceCapabilities.currentExtent</c> overrides the request — so a swapchain built
    ///     for a 3840×2160 window can legitimately report a different size, and comparing against
    ///     <c>SwapChain.Size</c> would find a difference that rebuilding cannot remove. That is a
    ///     rebuild every frame, for ever.
    /// </remarks>
    Int2 built;

    public EditorPane(UiSurface surface, IWindow window) {
        Surface = surface;
        Window = window;
    }

    /// <summary>The part of the document it shows.</summary>
    public UiSurface Surface { get; }

    /// <summary>The window it presents to.</summary>
    public IWindow Window { get; }

    /// <summary>Whether this is the editor's main window.</summary>
    /// <remarks>The scene viewport is drawn into this one and nowhere else, because there is one
    ///     scene and one camera and a second copy of it would be a second thing to keep in step.</remarks>
    public bool IsPrimary => Surface.IsPrimary;

    /// <summary>What the swapchain presents through, once there is something to present to.</summary>
    public ISwapChain? SwapChain { get; private set; }

    /// <summary>This window's own vertex buffers, pipelines and atlas texture.</summary>
    public UiRenderer? Renderer { get; private set; }

    /// <summary>Turns this window's draw list into vertices.</summary>
    public UiGeometryBuilder Geometry { get; } = new();

    /// <summary>This frame's vertices.</summary>
    public UiGeometry Frame { get; set; }

    /// <summary>The draw list's version and the extent the last <see cref="Frame" /> was built from.</summary>
    /// <remarks>
    ///     ⚠ <b>What lets a still frame skip tessellation entirely.</b> <c>UiGeometryBuilder.Build</c>
    ///     flattens and tessellates every path in the list from scratch, and an editor's chrome is
    ///     mostly paths — every icon is a filled outline whose strokes were pre-expanded into quads,
    ///     so one twenty-pixel glyph is a couple of hundred segments. Rebuilding all of it sixty times
    ///     a second for a window where nothing moved is the whole of what made the outliner feel
    ///     expensive: the cost scaled with how many rows were on screen, not with what was happening.
    ///     <para>
    ///         <c>DrawList.Version</c> was written for exactly this — its own remark says it changes
    ///         when the *drawing* changes and not when the drawing is rebuilt — and nothing read it.
    ///     </para>
    /// </remarks>
    public (int Version, Rectangle Extent) Built { get; set; } = (-1, default);

    /// <summary>The image this frame is being drawn into, while there is one.</summary>
    public TextureViewHandle Acquired { get; set; }

    /// <summary>Whether this pane has an image and has recorded into it.</summary>
    public bool IsDrawing { get; set; }

    /// <summary>How many physical pixels one device-independent one is here, never zero.</summary>
    public float Scale => Window.DpiScale <= 0f ? 1f : Window.DpiScale;

    /// <summary>The window's client area in the units the document is laid out in.</summary>
    public Rectangle Extent => new(0f, 0f, Window.FramebufferSize.X / Scale, Window.FramebufferSize.Y / Scale);

    /// <summary>Builds the swapchain and the renderer, once there is a surface to present to.</summary>
    /// <param name="device">The device.</param>
    /// <param name="shaders">The modules, compiled once and shared by every window.</param>
    /// <returns>Whether there is somewhere to present.</returns>
    public bool Ensure(IGraphicsDevice device, UiShaders shaders) {
        if (SwapChain is not null) {
            return true;
        }

        if (!Window.Surface.Handle.CanPresent) {
            return false;
        }

        built = new Int2(Window.FramebufferSize.X, Window.FramebufferSize.Y);
        SwapChain = device.CreateSwapChain(new(Window.Surface.Handle, built, PixelFormat.Bgra8UNormSrgb));

        Renderer = new UiRenderer(device, shaders, new Rendering.RenderOutput([SwapChain.Format]));

        return true;
    }

    /// <summary>Rebuilds the swapchain for the window's current size.</summary>
    /// <param name="device">The device, which has to go idle before the images are replaced.</param>
    /// <param name="force">
    ///     Whether to rebuild even at the same size. True only for <c>OutOfDate</c>, which is the
    ///     one status that says the swapchain may no longer be used at all.
    /// </param>
    public void Recreate(IGraphicsDevice device, bool force = false) {
        if (SwapChain is null) {
            return;
        }

        var target = new Int2(Window.FramebufferSize.X, Window.FramebufferSize.Y);

        if (!force && target == built) {
            return;
        }

        device.WaitIdle();
        SwapChain.Resize(target);

        built = target;
    }

    /// <inheritdoc />
    public void Dispose() {
        Renderer?.Dispose();
        SwapChain?.Dispose();

        Renderer = null;
        SwapChain = null;

        // Or the swapchain the next Ensure builds would be compared against the one this just
        // destroyed, and a resume at the same size would skip the rebuild it needs.
        built = default;
    }
}

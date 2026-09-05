// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Platform;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text.Rasterizing;

namespace Vixen.Ui.Desktop;

/// <summary>One window's half of a frame: a swapchain, a renderer and the geometry between them.</summary>
/// <remarks>
///     <para>
///         <b>One of these per surface, which is one per window.</b> The document lays out and draws
///         every surface in one pass — see <see cref="UiSurface" /> — and what cannot be shared below
///         that is everything with a swapchain image in it: the window's own extent, its own scale,
///         its own vertex data.
///     </para>
///     <para>
///         ⚠ <b>A <see cref="UiRenderer" /> each, rather than one shared between the windows.</b> The
///         renderer rings its vertex and box buffers across the device's frames in flight and
///         advances a region per <c>Upload</c> — so two uploads in one device frame consume two
///         regions, and after as many frames as there are regions the second window would be writing
///         over geometry the GPU is still reading. Sharing it is a validation-clean way to draw
///         yesterday's frame. Two renderers cost two sets of pipelines, which an application with a
///         torn-off panel or two can afford and a corrupted frame is not.
///     </para>
///     <para>
///         ⚠ <b>It exists without a device, and that is what keeps <c>--frames</c> honest.</b> On a
///         machine with no Vulkan the surface is still laid out, still drawn and still turned into
///         vertices; only the presenting is missing. A surface that came into existence with a
///         swapchain would make a headless run prove nothing about the window it never opened.
///     </para>
///     <para>
///         This is <c>Vixen.Editor.App</c>'s <c>EditorPane</c>, which was the only one of the three
///         copies of the frame loop that had all of the above. The other two — <c>Samples/02</c> and
///         the <c>vixen-app</c> template — held the swapchain in the host itself, so neither could
///         open a second window and neither cached its tessellation.
///     </para>
/// </remarks>
public sealed class UiWindowSurface : IDisposable {
    /// <summary>The framebuffer size the swapchain was last built for.</summary>
    /// <remarks>
    ///     ⚠ <b>What was asked for, not what came back.</b> The surface decides its own extent —
    ///     <c>VkSurfaceCapabilities.currentExtent</c> overrides the request — so a swapchain built
    ///     for a 3840×2160 window can legitimately report a different size, and comparing against
    ///     <c>SwapChain.Size</c> would find a difference that rebuilding cannot remove. That is a
    ///     rebuild every frame, for ever.
    /// </remarks>
    Int2 built;

    /// <summary>Joins a document's surface to the window that presents it.</summary>
    /// <param name="surface">The part of the document this window shows.</param>
    /// <param name="window">The window.</param>
    public UiWindowSurface(UiSurface surface, IWindow window) {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(window);

        Surface = surface;
        Window = window;
    }

    /// <summary>The part of the document it shows.</summary>
    public UiSurface Surface { get; }

    /// <summary>The window it presents to.</summary>
    public IWindow Window { get; }

    /// <summary>Whether this is the application's main window.</summary>
    public bool IsPrimary => Surface.IsPrimary;

    /// <summary>What the swapchain presents through, once there is something to present to.</summary>
    public ISwapChain? SwapChain { get; private set; }

    /// <summary>This window's own vertex buffers, pipelines and atlas texture.</summary>
    public UiRenderer? Renderer { get; private set; }

    /// <summary>Turns this window's draw list into vertices.</summary>
    public UiGeometryBuilder Geometry { get; } = new();

    /// <summary>This frame's vertices.</summary>
    public UiGeometry Frame { get; set; }

    /// <summary>The image this frame is being drawn into, while there is one.</summary>
    public TextureViewHandle Acquired { get; set; }

    /// <summary>Whether this surface has an image and has recorded into it.</summary>
    public bool IsDrawing { get; set; }

    /// <summary>How many physical pixels one device-independent one is here, never zero.</summary>
    public float Scale => Window.DpiScale <= 0f ? 1f : Window.DpiScale;

    /// <summary>The window's client area in the units the document is laid out in.</summary>
    /// <remarks>
    ///     ⚠ Derived from <c>FramebufferSize</c> rather than read from <c>ClientSize</c>, because the
    ///     framebuffer is what the swapchain is sized to and the two can disagree by a pixel of
    ///     platform rounding. Deriving keeps the geometry, the projection and the scissor consistent
    ///     with each other even when all three are slightly wrong about the window.
    /// </remarks>
    public Rectangle Extent => new(0f, 0f, Window.FramebufferSize.X / Scale, Window.FramebufferSize.Y / Scale);

    /// <summary>The extent in the whole pixels a projection and a scissor are computed from.</summary>
    public Int2 Viewport => new((int) MathF.Round(Extent.Width), (int) MathF.Round(Extent.Height));

    /// <summary>Builds the swapchain and the renderer, once there is a surface to present to.</summary>
    /// <param name="device">The device.</param>
    /// <param name="shaders">The modules, compiled once and shared by every window.</param>
    /// <returns>Whether there is somewhere to present.</returns>
    public bool Ensure(IGraphicsDevice device, UiShaders shaders) {
        ArgumentNullException.ThrowIfNull(device);

        if (SwapChain is not null) {
            return true;
        }

        if (!Window.Surface.Handle.CanPresent) {
            return false;
        }

        built = new Int2(Window.FramebufferSize.X, Window.FramebufferSize.Y);
        SwapChain = device.CreateSwapChain(new(Window.Surface.Handle, built, PixelFormat.Bgra8UNormSrgb));

        Renderer = new UiRenderer(device, shaders, new Vixen.Rendering.RenderOutput([SwapChain.Format]));

        // ⚠ Read back, not passed forward. The swapchain reports the gamut it *granted*, which is
        // not always the one it was asked for — a surface offering no wide colour space with enough
        // precision behind it stays in sRGB — and telling the builder to map to P3 for a surface
        // that stayed sRGB over-saturates every colour on an ordinary display. Per window, because
        // two windows of one application can be on two monitors and only one of them wide.
        Adopt();
        Publish();

        return true;
    }

    /// <summary>Turns this window's draw list into vertices, or keeps the ones it already has.</summary>
    /// <param name="glyphs">The shared glyph atlas, which every window rasterises into.</param>
    /// <returns>Whether anything was rebuilt.</returns>
    /// <remarks>
    ///     ⚠ <b>The key that decides is <see cref="UiGeometryBuilder.TryBuild" />'s and no longer
    ///     this method's.</b> It used to be written out here — draw-list version, extent, atlas — and
    ///     written out a second time, verbatim, in <c>EditorHost.Build</c>, which never called this
    ///     method at all. Two copies in the two hosts is exactly the arrangement where a fix reaches
    ///     one renderer and not the other, and it was also what made the saving unmeasurable: neither
    ///     copy can be reached from a test without a window, while the builder's can.
    /// </remarks>
    public bool Tessellate(GlyphFieldCache glyphs) {
        ArgumentNullException.ThrowIfNull(glyphs);

        var frame = Frame;
        var built = Geometry.TryBuild(Surface.Drawing, glyphs, Extent, ref frame);

        Frame = frame;

        return built;
    }

    /// <summary>Rebuilds the swapchain for the window's current size.</summary>
    /// <param name="device">The device, which has to go idle before the images are replaced.</param>
    /// <param name="force">
    ///     Whether to rebuild even at the same size. True only for <c>OutOfDate</c>, which is the
    ///     one status that says the swapchain may no longer be used at all.
    /// </param>
    public void Recreate(IGraphicsDevice device, bool force = false) {
        ArgumentNullException.ThrowIfNull(device);

        if (SwapChain is null) {
            return;
        }

        var target = new Int2(Window.FramebufferSize.X, Window.FramebufferSize.Y);

        if (!force && target == built) {
            return;
        }

        device.WaitIdle();
        SwapChain.Resize(target);

        // A resize renegotiates the surface format, so the granted gamut can move — dragging a
        // window onto a wide display is exactly a resize-and-recreate — and a builder still holding
        // the old one would map to a gamut the surface no longer has.
        Adopt();
        Publish();

        built = target;
    }

    /// <summary>Takes the next image, rebuilding once if the swapchain has gone stale.</summary>
    /// <param name="device">The device, for the rebuild.</param>
    /// <param name="view">The image to draw into.</param>
    /// <returns>Whether there is one, or <see langword="null" /> when the device is lost.</returns>
    /// <remarks>
    ///     ⚠ <b>It retries rather than dropping the frame.</b> <c>OutOfDate</c> arrives on the first
    ///     acquire after every resize, and returning here would present nothing that frame — the
    ///     compositor shows whatever was there before, which during a maximise or a drag is the
    ///     window visibly blinking.
    /// </remarks>
    public bool? Acquire(IGraphicsDevice device, out TextureViewHandle view) {
        ArgumentNullException.ThrowIfNull(device);

        view = default;

        if (SwapChain is null) {
            return false;
        }

        var status = SwapChain.AcquireNextImage(out view);

        if (status is SwapChainStatus.OutOfDate) {
            Recreate(device, force: true);
            status = SwapChain.AcquireNextImage(out view);
        }

        if (status is SwapChainStatus.DeviceLost) {
            return null;
        }

        return status is not SwapChainStatus.OutOfDate;
    }

    /// <summary>BT.2408's reference white: what an SDR interface is worth in an HDR frame.</summary>
    const float ReferenceWhite = 203f;

    /// <summary>Hands the geometry builder what the swapchain granted — its gamut and its white.</summary>
    /// <remarks>
    ///     ⚠ <b>The white level is stated here rather than left at its default, and one is a
    ///     statement.</b> A swapchain's white <i>is</i> the display's, so an authored 0–1 colour is
    ///     already in the units this surface wants; a float target is scene-referred and in cd/m²,
    ///     where that same white is about one candela — black beside anything the renderer lit, and
    ///     pixel-identical to a pass that never ran. See <c>UiGeometryBuilder.WhiteLevel</c> and
    ///     #670. Both callers renegotiate the surface format, which is why this is one method: a
    ///     resize onto another display can move the gamut, and it can move the format with it.
    /// </remarks>
    void Adopt() {
        Geometry.Gamut = SwapChain!.Gamut;

        Geometry.WhiteLevel =
            SwapChain.Format is PixelFormat.Rgba16Float or PixelFormat.Rgba32Float or PixelFormat.Rg11B10Float
                ? ReferenceWhite
                : 1f;
    }

    /// <summary>Tells the cascade what this surface was granted, so <c>@media</c> can ask.</summary>
    /// <remarks>
    ///     ⚠ <b>The same fact the caller hands the geometry builder.</b> <c>@media (color-gamut: p3)</c>
    ///     is evaluated against the surface's own verdict, so a palette dragged onto a wide display
    ///     picks the wider gamut up and the main window keeps its own.
    /// </remarks>
    void Publish() {
        if (SwapChain is { } chain) {
            Surface.Gamut = chain.Gamut;
        }
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

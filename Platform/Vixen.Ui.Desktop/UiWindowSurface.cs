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

    /// <summary>The draw list's version and the extent the last <see cref="Frame" /> was built from.</summary>
    /// <remarks>
    ///     ⚠ <b>What lets a still frame skip tessellation entirely.</b> <c>UiGeometryBuilder.Build</c>
    ///     flattens and tessellates every path in the list from scratch, and an interface's chrome is
    ///     mostly paths — every icon is a filled outline whose strokes were pre-expanded into quads,
    ///     so one twenty-pixel glyph is a couple of hundred segments. Rebuilding all of it sixty times
    ///     a second for a window where nothing moved is pure waste: the cost scales with how many rows
    ///     are on screen, not with what is happening.
    ///     <para>
    ///         <c>DrawList.Version</c> was written for exactly this — its own remark says it changes
    ///         when the *drawing* changes and not when the drawing is rebuilt.
    ///     </para>
    /// </remarks>
    public (int Version, Rectangle Extent) Built { get; set; } = (-1, default);

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
        Geometry.Gamut = SwapChain.Gamut;
        Publish();

        return true;
    }

    /// <summary>Turns this window's draw list into vertices, or keeps the ones it already has.</summary>
    /// <param name="glyphs">The shared glyph atlas, which every window rasterises into.</param>
    /// <returns>Whether anything was rebuilt.</returns>
    /// <remarks>
    ///     ⚠ <b>The extent is half of the key and it is not redundant.</b> A window resized without
    ///     its contents changing keeps the draw list's version — the same commands at the same
    ///     coordinates — and the vertices still have to be rebuilt, because the builder is what turns
    ///     a command's clip into a scissor in the new extent.
    ///     <para>
    ///         ⚠ <b>And the glyph atlas is a third input, which is why <c>AtlasChanged</c> is asked
    ///         as well.</b> A label that brought a new glyph in can repack the texture, which moves
    ///         every region already baked into last frame's vertices — so a frame that skipped after
    ///         a repack would draw the right letters read out of the wrong places. It is the one part
    ///         of the key that is not a property of this window, because the atlas is shared.
    ///     </para>
    /// </remarks>
    public bool Tessellate(GlyphFieldCache glyphs) {
        ArgumentNullException.ThrowIfNull(glyphs);

        var extent = Extent;
        var version = Surface.Drawing.Version;

        if (Built == (version, extent) && !Geometry.AtlasChanged) {
            return false;
        }

        Frame = Geometry.Build(Surface.Drawing, glyphs, extent);
        Built = (version, extent);

        return true;
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
        Geometry.Gamut = SwapChain.Gamut;
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

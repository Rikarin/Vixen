// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Graphics.Vulkan;
using Vixen.Platform;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text;
using Vixen.Ui.Text.Rasterizing;

namespace Vixen.Editor.App;

/// <summary>The window, the device, and the four steps of a frame.</summary>
/// <remarks>
///     <para>
///         The loop is four steps and they are worth naming: pump the platform's events into the
///         document, run the layout and draw passes, turn the draw list into geometry, and record
///         that geometry into a frame. Only the last of the four knows what a GPU is — which is why
///         <c>--frames N</c> means something on a machine with no Vulkan at all.
///     </para>
///     <para>
///         ⚠ <b>It draws every frame rather than when something changes.</b> An editor that redrew
///         only on input is the right end state and is not free: it needs every animation, every
///         toast expiry and every background task's progress to say so, and one that forgets leaves
///         a progress bar frozen at forty per cent. Said out loud rather than left to be discovered
///         on a laptop battery.
///     </para>
/// </remarks>
sealed class EditorHost : IDisposable {
    readonly IPlatform platform;
    readonly IWindow window;
    readonly EditorApplication editor;

    readonly UiGeometryBuilder geometry = new();
    readonly GlyphFieldCache glyphs = new(new GlyphAtlas(1024, 1024));

    VulkanDevice? device;
    TransientResourcePool? pool;
    RenderGraph? graph;
    ISwapChain? swapChain;
    UiRenderer? renderer;

    /// <summary>The framebuffer size the swapchain was last built for.</summary>
    /// <remarks>
    ///     ⚠ <b>What was asked for, not what came back.</b> The surface decides its own extent —
    ///     <c>VkSurfaceCapabilities.currentExtent</c> overrides the request — so a swapchain built
    ///     for a 3840×2160 window can legitimately report a different size, and comparing against
    ///     <c>swapChain.Size</c> would find a difference that rebuilding cannot remove. That is a
    ///     rebuild every frame, for ever.
    /// </remarks>
    Int2 built;

    bool running = true;
    bool lost;
    bool resized;

    public EditorHost(IPlatform platform, IWindow window, string? projectRoot = null) {
        this.platform = platform;
        this.window = window;

        editor = new EditorApplication(
            window.FramebufferSize.X / Scale,
            window.FramebufferSize.Y / Scale,
            platform.FileSystem.DataDirectory,
            projectRoot
        ) {
            RenderScale = Scale
        };

        Fonts.Install(editor.Shell.Document);
    }

    /// <summary>Runs until the window closes, or for a fixed number of frames.</summary>
    /// <param name="frames">How many, or zero for as many as it takes.</param>
    /// <returns>A process exit code.</returns>
    public int Run(int frames) {
        var clock = Stopwatch.StartNew();
        var previous = TimeSpan.Zero;
        var drawn = 0;

        while (running && (frames == 0 || drawn < frames)) {
            var now = clock.Elapsed;
            var delta = now - previous;
            previous = now;

            Pump();

            if (!running || editor.IsClosing) {
                break;
            }

            // ⚠ Once per frame, however many resize events arrived. A window opened maximised on a
            // 4K display produces a burst of them, and handling each one where it arrives means a
            // `vkDeviceWaitIdle` and a full swapchain rebuild several times before a single frame is
            // drawn — every rebuild handing the compositor images whose contents are undefined,
            // which is what the flicker is. Coalescing also keeps the layout and the geometry in
            // step: both are read from the same framebuffer size, once, before anything uses it.
            if (resized) {
                resized = false;

                editor.Shell.Resize(window.FramebufferSize.X / Scale, window.FramebufferSize.Y / Scale);
                editor.RenderScale = Scale;
                Recreate();
            }

            editor.Shell.Tick(now, delta);

            editor.Shell.Document.Update();

            // ⚠ Between the two, and it is not arbitrary. A viewport measures itself in render pixels
            // from a box the layout pass is what produces, and the axis cross it draws comes from the
            // camera this brings up to date — so either side of this pair puts the picture a frame
            // behind whatever the user just did with the mouse.
            editor.Update();

            editor.Shell.Document.Draw();

            Present(Build());
            drawn++;
        }

        device?.WaitIdle();

        // ⚠ Before the document goes, and it is the reason this is not in `Dispose`. Persisting
        // reads the arrangement out of the docking host, and a host that had already been disposed
        // would write an empty layout over the one the user spent the afternoon arranging.
        editor.Persist();

        return 0;
    }

    /// <inheritdoc />
    public void Dispose() {
        Release();
        editor.Dispose();
    }

    void Pump() {
        foreach (var platformEvent in platform.PumpEvents()) {
            switch (platformEvent.Kind) {
                case PlatformEventKind.Quit:
                case PlatformEventKind.WindowCloseRequested:
                    running = false;
                    return;

                case PlatformEventKind.WindowResized:
                    // Recorded rather than acted on: the window is the authority on its own size and
                    // the frame reads it once, above.
                    resized = true;
                    break;

                case PlatformEventKind.Suspending:
                    Release();
                    break;

                default:
                    PlatformInput.Dispatch(editor.Shell.Document, platformEvent);
                    break;
            }
        }
    }

    /// <summary>Turns this frame's draw list into vertices.</summary>
    /// <remarks>
    ///     ⚠ <b>Built whether or not there is a device.</b> On a headless run — no surface, no
    ///     Vulkan — everything above the RHI still executes, which is what makes <c>--frames</c> a
    ///     smoke test of the editor rather than only of the backend.
    /// </remarks>
    UiGeometry Build() => geometry.Build(editor.Shell.Document.Drawing, glyphs, Surface());

    /// <summary>How many physical pixels one device-independent one is, never zero.</summary>
    float Scale => window.DpiScale <= 0f ? 1f : window.DpiScale;

    /// <summary>The window's client area in the units the document is laid out in.</summary>
    Rectangle Surface() => new(0f, 0f, window.FramebufferSize.X / Scale, window.FramebufferSize.Y / Scale);

    void Present(UiGeometry frame) {
        var scale = Scale;
        var surface = Surface();

        if (lost || !EnsureDevice()) {
            return;
        }

        device!.BeginFrame();

        if (!Acquire(out var view)) {
            // ⚠ Ended even though nothing was drawn, and this is not tidiness. `BeginFrame` waits on
            // this slot's fence and resets it; `EndFrame` is what submits the signal that makes the
            // wait return. Leaving without it means the frame counter never advances, so the next
            // frame waits on the same reset fence with no submission behind it — `vkWaitForFences`
            // with no timeout, which is a hang rather than a dropped frame.
            device.EndFrame();
            return;
        }

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "ui")) {
            var backbuffer = graph!.ImportTexture(
                swapChain!.CurrentTexture,
                view,
                new(
                    swapChain.Format,
                    swapChain.Size.X,
                    swapChain.Size.Y,
                    TextureUsage.ColourTarget,
                    Name: "backbuffer"
                ),
                ResourceState.Undefined,
                ResourceState.Present
            );

            // ⚠ Before the pass, not inside it. The atlas upload is a transfer and a layout
            // transition, and a render pass is the one place a Vulkan command list may not do
            // either.
            renderer!.Upload(commands, frame, glyphs.Atlas);

            graph.AddPass(
                "ui",
                pass => {
                    pass.ColourAttachment(backbuffer, LoadAction.Clear, new Color4(0.06f, 0.07f, 0.09f, 1f));
                    pass.SideEffect();

                    // ⚠ The logical surface and the DPI scale, not the swapchain's size. The
                    // geometry is in device-independent units and the scissor comes out in
                    // framebuffer pixels; passing the framebuffer for both draws the whole
                    // interface into the top-left quarter of the window.
                    pass.Execute(
                        context => renderer.Record(
                            context.CommandList,
                            frame,
                            new Int2((int) MathF.Round(surface.Width), (int) MathF.Round(surface.Height)),
                            scale
                        )
                    );
                }
            );

            graph.Execute(commands);
            graph.Reset();

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();

        switch (swapChain.Present()) {
            case SwapChainStatus.OutOfDate:
                Recreate(force: true);
                break;

            // ⚠ Suboptimal is a hint, and rebuilding on it unconditionally is the flicker. It means
            // "this still presents correctly, but the surface would prefer other parameters" — and
            // a compositor that keeps saying so, which a scaled 4K surface does, then gets a
            // `vkDeviceWaitIdle` and a fresh set of undefined images every single frame. Honoured
            // only when the window has actually changed size, which `Recreate` is what decides.
            case SwapChainStatus.Suboptimal:
                Recreate();
                break;

            default:
                break;
        }
    }

    /// <summary>Takes the next image, rebuilding once if the swapchain has gone stale.</summary>
    /// <returns>Whether there is an image to draw into.</returns>
    /// <remarks>
    ///     ⚠ <b>It retries rather than dropping the frame.</b> `OutOfDate` arrives on the first
    ///     acquire after every resize, and returning here would present nothing that frame — the
    ///     compositor shows whatever was there before, which during a maximise or a drag is the
    ///     window visibly blinking. Rebuilding and asking again costs one stall and puts a correct
    ///     frame on the screen.
    /// </remarks>
    bool Acquire(out TextureViewHandle view) {
        var status = swapChain!.AcquireNextImage(out view);

        if (status is SwapChainStatus.OutOfDate) {
            Recreate(force: true);
            status = swapChain.AcquireNextImage(out view);
        }

        if (status is SwapChainStatus.DeviceLost) {
            lost = true;
            return false;
        }

        return status is not SwapChainStatus.OutOfDate;
    }

    /// <summary>Builds everything GPU-shaped, once there is a surface to present to.</summary>
    /// <returns>Whether there is one.</returns>
    bool EnsureDevice() {
        if (device is not null) {
            return true;
        }

        if (!window.Surface.Handle.CanPresent) {
            return false;
        }

        device = VulkanDevice.Create(new() { Surface = window.Surface.Handle });

        pool = new TransientResourcePool(device);
        graph = new RenderGraph(device, pool);

        built = new Int2(window.FramebufferSize.X, window.FramebufferSize.Y);
        swapChain = device.CreateSwapChain(new(window.Surface.Handle, built, PixelFormat.Bgra8UNormSrgb));

        renderer = new UiRenderer(
            device,
            new UiShaders(
                device.CreateShader(ShaderStage.Vertex, Module("ui.vert.spv"), "ui vertex"),
                device.CreateShader(ShaderStage.Fragment, Module("ui-box.frag.spv"), "ui box"),
                device.CreateShader(ShaderStage.Fragment, Module("ui-text.frag.spv"), "ui text"),
                device.CreateShader(ShaderStage.Fragment, Module("ui-solid.frag.spv"), "ui solid")
            ) {
                // The stage a viewport's render target is drawn through. Supplied here rather than
                // assumed by the renderer, for the reason UiShaders gives: turning source into
                // modules is Raven's job and this host hands over what it has.
                Image = device.CreateShader(ShaderStage.Fragment, Module("ui-image.frag.spv"), "ui image")
            },
            new Rendering.RenderOutput([swapChain.Format])
        );

        return true;
    }

    /// <summary>Rebuilds the swapchain for the window's current size.</summary>
    /// <param name="force">
    ///     Whether to rebuild even at the same size. True only for <c>OutOfDate</c>, which is the
    ///     one status that says the swapchain may no longer be used at all.
    /// </param>
    void Recreate(bool force = false) {
        if (device is null || swapChain is null) {
            return;
        }

        var target = new Int2(window.FramebufferSize.X, window.FramebufferSize.Y);

        if (!force && target == built) {
            return;
        }

        device.WaitIdle();
        swapChain.Resize(target);

        built = target;
    }

    void Release() {
        device?.WaitIdle();

        renderer?.Dispose();
        swapChain?.Dispose();
        pool?.Dispose();
        device?.Dispose();

        renderer = null;
        swapChain = null;
        graph = null;
        pool = null;
        device = null;

        // Or the swapchain the next EnsureDevice builds would be compared against the one this
        // just destroyed, and a resume at the same size would skip the rebuild it needs.
        built = default;
    }

    /// <summary>Reads an embedded SPIR-V module.</summary>
    /// <remarks>
    ///     ⚠ Found by suffix rather than named outright: the manifest name is the root namespace
    ///     plus the folder plus the file, so it is
    ///     <c>Vixen.Editor.App.Shaders.ui.vert.spv</c> rather than anything a reader would guess —
    ///     and it changes if the assembly is renamed.
    /// </remarks>
    static byte[] Module(string name) {
        var assembly = Assembly.GetExecutingAssembly();

        var resource = assembly.GetManifestResourceNames()
                .SingleOrDefault(entry => entry.EndsWith(name, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"'{name}' is not embedded in this assembly.");

        using var stream = assembly.GetManifestResourceStream(resource)!;

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return memory.ToArray();
    }
}

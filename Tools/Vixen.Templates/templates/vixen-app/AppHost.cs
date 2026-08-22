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

namespace VixenApp1;

/// <summary>The device, and the four steps of a frame.</summary>
/// <remarks>
///     <para>
///         The loop is four steps and they are worth naming: pump the platform's events into the
///         document, run the layout and draw passes, turn the draw list into geometry, and record
///         that geometry into a frame. Only the last of the four knows what a GPU is — which is why
///         <c>--frames</c> means something on a machine with no Vulkan at all.
///     </para>
///     <para>
///         ⚠ <b>It draws every frame rather than when something changes.</b> Redrawing only on input
///         is the right end state for a desktop application and it is not free: every animation,
///         every timer and every background task's progress has to say that it moved, and one that
///         forgets leaves a progress bar frozen at forty per cent. Said out loud here rather than
///         left to be discovered on a laptop battery.
///     </para>
/// </remarks>
sealed class AppHost : IDisposable {
    readonly IPlatform platform;
    readonly IWindow window;
    readonly AppDocument ui;

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
    ///     <c>VkSurfaceCapabilities.currentExtent</c> overrides the request — so comparing against
    ///     <c>swapChain.Size</c> would find a difference that rebuilding cannot remove, which is a
    ///     rebuild every frame for ever.
    /// </remarks>
    Int2 built;

    bool running = true;
    bool lost;
    bool resized;

    public AppHost(IPlatform platform, IWindow window) {
        this.platform = platform;
        this.window = window;

        ui = new AppDocument(window.FramebufferSize.X / Scale, window.FramebufferSize.Y / Scale);

        AppFonts.Install(ui.Document);
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

            if (!running) {
                break;
            }

            // ⚠ Once per frame, however many resize events arrived. A window opened maximised on a
            // 4K display produces a burst of them, and handling each one where it arrives means a
            // `vkDeviceWaitIdle` and a full swapchain rebuild several times before a single frame is
            // drawn — every rebuild handing the compositor images whose contents are undefined,
            // which is what the flicker is.
            if (resized) {
                resized = false;

                ui.Resize(window.FramebufferSize.X / Scale, window.FramebufferSize.Y / Scale);
                Recreate();
            }

            ui.Tick(now, delta);

            ui.Document.Update();
            ui.Document.Draw();

            Present(Build());
            drawn++;
        }

        device?.WaitIdle();

        return 0;
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
                    AppInput.Dispatch(ui.Document, platformEvent);
                    break;
            }
        }
    }

    /// <summary>Turns this frame's draw list into vertices.</summary>
    /// <remarks>
    ///     ⚠ <b>Built whether or not there is a device.</b> On a headless run — no surface, no
    ///     Vulkan — everything above the RHI still executes, which is what makes <c>--frames</c> a
    ///     smoke test of the whole application rather than only of the backend.
    /// </remarks>
    UiGeometry Build() => geometry.Build(ui.Document.Drawing, glyphs, Surface());

    /// <summary>How many physical pixels one device-independent one is, never zero.</summary>
    float Scale => window.DpiScale <= 0f ? 1f : window.DpiScale;

    /// <summary>The window's client area in the units the document is laid out in.</summary>
    /// <remarks>
    ///     ⚠ Derived from <c>FramebufferSize</c> rather than read from <c>ClientSize</c>, because the
    ///     framebuffer is what the swapchain is sized to and the two can disagree by a pixel of
    ///     platform rounding. Deriving keeps the geometry, the projection and the scissor consistent
    ///     with each other even when all three are slightly wrong about the window.
    /// </remarks>
    Rectangle Surface() =>
        new(0f, 0f, window.FramebufferSize.X / Scale, window.FramebufferSize.Y / Scale);

    /// <summary>Puts a frame of geometry on the screen.</summary>
    /// <remarks>
    ///     ⚠ Taken by value rather than by <c>in</c>, which for a struct this size is the wrong
    ///     default everywhere except here: the render pass closes over it, and C# will not let a
    ///     lambda capture a by-reference parameter.
    /// </remarks>
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
            // frame waits on the same reset fence with no submission behind it — which is a hang
            // rather than a dropped frame.
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
            // either — which is why `UiRenderer` splits `Upload` from `Record` at all.
            renderer!.Upload(commands, frame, glyphs.Atlas);

            // ⚠ <b>After `Upload` and outside the pass, for both of the reasons above and one more.</b>
            // A translucent subtree that draws more than one thing is rendered into a surface of its
            // own and blended once — CSS Compositing 1 § 3 — and this is what renders those surfaces.
            // It opens a render pass per group, so it cannot be inside one; and it draws from the
            // vertices `Upload` just wrote, so it cannot be before it. Recording it onto `commands`
            // here puts it ahead of `graph.Execute` on the same list, which is the order the
            // dependency runs in: the interface's pass samples what this wrote.
            //
            // ⚠ The same surface and scale as `Record` below. A group's surface is viewport-sized and
            // drawn with the frame's own projection, so a different number here would place the
            // subtree somewhere its composite quad does not look for it.
            renderer.Compose(
                commands,
                frame,
                new Int2((int) MathF.Round(surface.Width), (int) MathF.Round(surface.Height)),
                scale
            );

            graph.AddPass("ui", pass => {
                pass.ColourAttachment(backbuffer, LoadAction.Clear, new Color4(0.06f, 0.07f, 0.09f, 1f));
                pass.SideEffect();

                // ⚠ The *logical* surface and the DPI scale, not the swapchain's size. The geometry
                // is in device-independent units — the document is 1280×800 on a display whose
                // framebuffer is 2560×1600 — and the projection has to map those units, while the
                // scissor has to come out in framebuffer pixels. Passing the framebuffer for both
                // draws the whole interface into the top-left quarter of the window.
                pass.Execute(
                    context => renderer.Record(
                        context.CommandList,
                        frame,
                        new Int2((int) MathF.Round(surface.Width), (int) MathF.Round(surface.Height)),
                        scale
                    )
                );
            });

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
            // "this still presents correctly, but the surface would prefer other parameters" — and a
            // compositor that keeps saying so, which a scaled 4K surface does, then gets a
            // `vkDeviceWaitIdle` and a fresh set of undefined images every single frame.
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
    ///     ⚠ <b>It retries rather than dropping the frame.</b> <c>OutOfDate</c> arrives on the first
    ///     acquire after every resize, and returning here would present nothing that frame — the
    ///     compositor shows whatever was there before, which during a drag is the window blinking.
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
    /// <remarks>
    ///     Lazy on purpose: a headless run never gets a surface, and the answer to that is to draw
    ///     nothing rather than to fail. It is also what lets <c>--frames</c> mean something on a
    ///     machine with no GPU at all.
    /// </remarks>
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

        // ⚠ Read back, not assumed. Every colour the builder emits is brought into this gamut — see
        // `UiGeometryBuilder.Gamut` — and the swapchain reports what the surface *granted*, which is
        // not always what it was asked for. Ask for Display P3 through `GraphicsOptions.Gamut` and a
        // surface that could not offer it stays sRGB; mapping to P3 anyway over-saturates it.
        geometry.Gamut = swapChain.Gamut;

        renderer = new UiRenderer(
            device,
            new UiShaders(
                device.CreateShader(ShaderStage.Vertex, Module("ui.vert.spv"), "ui vertex"),
                device.CreateShader(ShaderStage.Fragment, Module("ui-box.frag.spv"), "ui box"),
                device.CreateShader(ShaderStage.Fragment, Module("ui-text.frag.spv"), "ui text"),
                device.CreateShader(ShaderStage.Fragment, Module("ui-solid.frag.spv"), "ui solid")
            ) {
                // ⚠ <b>Not only for drawing images, which is what its name suggests and why it was
                // left out.</b> This is also the stage `UiRenderer.Compose` composites a group's
                // surface back with, and an `opacity` on anything that draws more than one thing
                // makes a group — `ControlTheme.vcss` puts one on every disabled control. Without
                // this shader `Compose` has nothing to composite with and returns having done
                // nothing, and the group's contents are then drawn in place at *full* strength: a
                // disabled button comes out opaque rather than faded. So it ships whether or not the
                // application ever draws an image.
                Image = device.CreateShader(ShaderStage.Fragment, Module("ui-image.frag.spv"), "ui image"),

                // ⚠ <b>The other half of the same story, one step further along.</b> This is what
                // `Compose` runs over a group whose stylesheet asked for `filter: blur()`. Leaving it
                // out is milder than leaving out the image stage — the group still composites, at the
                // right opacity, merely sharp — but the symptom is the same shape: a class that
                // resolves, cascades, and appears not to work.
                Blur = device.CreateShader(ShaderStage.Fragment, Module("ui-blur.frag.spv"), "ui blur")
            },
            new Vixen.Rendering.RenderOutput([swapChain.Format])
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

        // Or the swapchain the next EnsureDevice builds would be compared against the one this just
        // destroyed, and a resume at the same size would skip the rebuild it needs.
        built = default;
    }

    /// <summary>Reads an embedded SPIR-V module.</summary>
    /// <remarks>
    ///     ⚠ Found by suffix rather than named outright: the manifest name is the root namespace
    ///     plus the folder plus the file, so it changes if the assembly is renamed.
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

    public void Dispose() {
        Release();
        ui.Dispose();
    }
}

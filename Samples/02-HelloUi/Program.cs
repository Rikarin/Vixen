// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Graphics.Vulkan;
using Vixen.Platform;
using Vixen.Platform.Desktop;
using Vixen.Platform.Ui;
using Vixen.Ui;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Text;
using Vixen.Ui.Text.Rasterizing;

namespace Vixen.Samples.HelloUi;

/// <summary>A user interface, on a window, with no engine underneath it.</summary>
/// <remarks>
///     <para>
///         <b>What this proves is an absence.</b> docs/plan/02 § Samples describes 02-HelloUi as
///         "Vixen.Ui only, no engine — proves the UI/Engine boundary", and doc 15 makes it the thing
///         that proves the framework standalone before the editor is allowed to depend on it. So
///         there is no <c>Vixen.App</c> here: the host that assembly would have provided is the
///         hundred lines below, because using it would have pulled <c>Vixen.Engine</c> in and the
///         sample would have proved nothing.
///     </para>
///     <para>
///         The loop is four steps and they are worth naming: pump the platform's events into the
///         document, run the layout and draw passes, turn the draw list into geometry, and record
///         that geometry into a frame. Only the last of the four knows what a GPU is.
///     </para>
///     <para>
///         <c>--frames N</c> runs exactly N frames and exits, which is how CI proves the whole stack
///         starts, presents and stops without a validation error or a hang — the same argument
///         Samples/01 makes for the flag it introduced.
///     </para>
/// </remarks>
static class Program {
    static int Main(string[] arguments) {
        var frames = Frames(arguments);

        // ⚠ The surface has to be asked for at creation. SDL needs the Vulkan window flag when the
        // window is made, and one made without it has nothing to present to — the same trap
        // Samples/01 documents, and the reason the platform is built here rather than defaulted.
        using var platform = new DesktopPlatform(
            new() { Organisation = "Vixen", Application = "HelloUi", RequestGpuSurface = true }
        );

        using var window = platform.CreateWindow(
            new WindowOptions {
                Title = "Vixen — Hello UI",
                Size = new Int2(1280, 800),
                IsVisible = true,
                IsResizable = true
            }
        );

        using var host = new UiHost(platform, window);
        return host.Run(frames);
    }

    /// <summary>Reads <c>--frames N</c>, or zero for "until the window is closed".</summary>
    static int Frames(ReadOnlySpan<string> arguments) {
        for (var i = 0; i + 1 < arguments.Length; i++) {
            if (arguments[i] is "--frames" or "--vixen-frames"
                && int.TryParse(arguments[i + 1], CultureInfo.InvariantCulture, out var count)) {
                return Math.Max(0, count);
            }
        }

        return 0;
    }
}

/// <summary>The window, the device, and the four steps of a frame.</summary>
sealed class UiHost : IDisposable {
    readonly IPlatform platform;
    readonly IWindow window;
    readonly Shell ui;

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

    public UiHost(IPlatform platform, IWindow window) {
        this.platform = platform;
        this.window = window;

        ui = new Shell(window.FramebufferSize.X / Scale, window.FramebufferSize.Y / Scale);
        Fonts.Install(ui.Document);
    }

    /// <summary>Runs until the window closes, or for a fixed number of frames.</summary>
    /// <param name="frames">How many, or zero for as many as it takes.</param>
    /// <returns>A process exit code.</returns>
    public int Run(int frames) {
        var clock = Stopwatch.StartNew();
        var previous = TimeSpan.Zero;
        var drawn = 0;

        var frameTicks = 0L;
        var worst = 0L;
        var first = 0L;
        var measured = 0;

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
            // which is what the flicker is. It also keeps the layout and the geometry in step: both
            // are read from the same framebuffer size, once, before anything uses it.
            if (resized) {
                resized = false;

                ui.Resize(window.FramebufferSize.X / Scale, window.FramebufferSize.Y / Scale);
                Recreate();
            }

            ui.Tick(now, delta);

            // ⚠ Timed around the two passes and the geometry build, and deliberately not around the
            // present. What doc 14 budgets at two milliseconds is the *UI frame* — the cascade, the
            // font sizes, the layout style, flexbox, the draw-list walk and the vertex build — and
            // including the swapchain would measure the display's refresh rate instead.
            var started = Stopwatch.GetTimestamp();

            ui.Document.Update();
            ui.Document.Draw();

            var frame = Build();

            var cost = Stopwatch.GetTimestamp() - started;

            // ⚠ The first frames are thrown away, and saying so is the point of reporting the first
            // one on its own. It carries the JIT, the font load and the rasterisation of every glyph
            // the interface uses into the atlas — half a second of work that happens exactly once —
            // and folding it into a mean over three hundred frames triples the answer while hiding
            // what the answer is about.
            if (drawn == 0) {
                first = cost;
            } else if (drawn >= Warmup) {
                frameTicks += cost;
                worst = Math.Max(worst, cost);
                measured++;
            }

            Present(frame);
            drawn++;
        }

        device?.WaitIdle();
        Report(measured, frameTicks, worst, first);

        // The arrangement the user left it in, written where an application would persist it. Printed
        // rather than saved, because a sample that writes to somebody's home directory is a sample
        // that has to be cleaned up — and what is being demonstrated is that the round trip exists.
        Console.WriteLine(ui.Docking.Save());

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
                    PlatformInput.Dispatch(ui.Document, platformEvent);
                    break;
            }
        }
    }

    /// <summary>Turns this frame's draw list into vertices.</summary>
    /// <remarks>
    ///     ⚠ <b>Built whether or not there is a device.</b> On a headless run — no surface, no
    ///     Vulkan — everything above the RHI still executes, which is what makes <c>--frames</c> a
    ///     smoke test of the framework rather than only of the backend.
    /// </remarks>
    UiGeometry Build() => geometry.Build(ui.Document.Drawing, glyphs, Surface());

    /// <summary>How many physical pixels one device-independent one is, never zero.</summary>
    float Scale => window.DpiScale <= 0f ? 1f : window.DpiScale;

    /// <summary>The window's client area in the units the document is laid out in.</summary>
    /// <remarks>
    ///     ⚠ Derived from <c>FramebufferSize</c> rather than read from <c>ClientSize</c>, because the
    ///     framebuffer is what the swapchain is sized to and the two can disagree by a pixel of
    ///     platform rounding. Deriving keeps the geometry, the projection and the scissor consistent
    ///     with each other even when they are all slightly wrong about the window.
    /// </remarks>
    Rectangle Surface() =>
        new(0f, 0f, window.FramebufferSize.X / Scale, window.FramebufferSize.Y / Scale);

    /// <summary>Puts a frame of geometry on the screen.</summary>
    /// <remarks>
    ///     ⚠ Taken by value rather than by <c>in</c>, which for a struct this size is the wrong
    ///     default everywhere except here: the render pass closes over it, and C# will not let a
    ///     lambda capture a by-reference parameter — because the reference may outlive the call and
    ///     the compiler cannot prove it does not.
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
            // either — which is why `UiRenderer` splits `Upload` from `Record` at all.
            renderer!.Upload(commands, frame, glyphs.Atlas);

            graph.AddPass("ui", pass => {
                pass.ColourAttachment(backbuffer, LoadAction.Clear, new Color4(0.06f, 0.07f, 0.09f, 1f));
                pass.SideEffect();
                // ⚠ The *logical* surface and the DPI scale, not the swapchain's size. The
                // geometry is in device-independent units — the document is 1280×800 on a display
                // whose framebuffer is 2560×1600 — and the projection has to map those units, while
                // the scissor has to come out in framebuffer pixels. Passing the framebuffer for
                // both draws the whole interface into the top-left quarter of the window.
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
    ///     window visibly blinking.
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
    ///     Lazy for the reason Samples/01 is lazy: a headless run never gets a surface, and the
    ///     answer to that is to draw nothing rather than to fail. It is also what lets
    ///     <c>--frames</c> mean something on a machine with no GPU at all.
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

        renderer = new UiRenderer(
            device,
            new UiShaders(
                device.CreateShader(ShaderStage.Vertex, Module("ui.vert.spv"), "ui vertex"),
                device.CreateShader(ShaderStage.Fragment, Module("ui-box.frag.spv"), "ui box"),
                device.CreateShader(ShaderStage.Fragment, Module("ui-text.frag.spv"), "ui text"),
                device.CreateShader(ShaderStage.Fragment, Module("ui-solid.frag.spv"), "ui solid")
            ),
            new Rendering.RenderOutput([swapChain.Format])
        );

        return true;
    }

    /// <summary>Says what the frame cost and how big it was.</summary>
    /// <remarks>
    ///     The numbers doc 14 sets Phase 4's exit criterion in, printed by the thing the criterion is
    ///     about. A benchmark measures this more carefully — see <c>Vixen.Benchmarks.Ui</c>'s
    ///     <c>DocumentBenchmarks</c> — and a sample that could not say what it cost would be one
    ///     nobody could use to find out.
    /// </remarks>
    void Report(int frames, long total, long worst, long first) {
        if (frames == 0) {
            return;
        }

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{Elements(ui.Document.Root)} elements · {ui.Document.Drawing.Commands.Count} commands · "
                + $"first frame {Stopwatch.GetElapsedTime(0, first).TotalMilliseconds:0.0} ms · "
                + $"then over {frames} frames: mean "
                + $"{Stopwatch.GetElapsedTime(0, total / frames).TotalMilliseconds:0.000} ms, worst "
                + $"{Stopwatch.GetElapsedTime(0, worst).TotalMilliseconds:0.000} ms"
            )
        );
    }

    /// <summary>How many frames are discarded before the mean starts.</summary>
    const int Warmup = 30;

    static int Elements(UiElement element) {
        var count = 1;

        foreach (var child in element.Children) {
            count += Elements(child);
        }

        return count;
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
    ///     ⚠ Found by suffix rather than named outright, which is what Samples/01 does and for a
    ///     reason worth copying: the manifest name is the root namespace plus the folder plus the
    ///     file, so it is <c>Vixen.Samples.HelloUi.Shaders.ui.vert.spv</c> rather than anything a
    ///     reader would guess — and it changes if the assembly is renamed.
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

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

    bool running = true;
    bool lost;

    public EditorHost(IPlatform platform, IWindow window) {
        this.platform = platform;
        this.window = window;

        editor = new EditorApplication(
            window.FramebufferSize.X / Scale,
            window.FramebufferSize.Y / Scale,
            platform.FileSystem.DataDirectory
        );

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

            editor.Shell.Tick(now, delta);

            editor.Shell.Document.Update();
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
                    editor.Shell.Resize(platformEvent.PixelSize.X / Scale, platformEvent.PixelSize.Y / Scale);
                    Recreate();

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

        var status = swapChain!.AcquireNextImage(out var view);

        if (status is SwapChainStatus.OutOfDate) {
            Recreate();
            return;
        }

        if (status is SwapChainStatus.DeviceLost) {
            lost = true;
            return;
        }

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "ui")) {
            var backbuffer = graph!.ImportTexture(
                swapChain.CurrentTexture,
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

        if (swapChain.Present() is SwapChainStatus.OutOfDate or SwapChainStatus.Suboptimal) {
            Recreate();
        }
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

        swapChain = device.CreateSwapChain(
            new(
                window.Surface.Handle,
                new Int2(window.FramebufferSize.X, window.FramebufferSize.Y),
                PixelFormat.Bgra8UNormSrgb
            )
        );

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

    void Recreate() {
        if (device is null || swapChain is null) {
            return;
        }

        device.WaitIdle();
        swapChain.Resize(new Int2(window.FramebufferSize.X, window.FramebufferSize.Y));
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

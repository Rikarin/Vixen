// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Microsoft.Extensions.Logging;
using Vixen.App;
using Vixen.Core;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Graphics.Vulkan;
using Vixen.Platform;

namespace Vixen.Samples.HelloTriangle;

/// <summary>Clears to a colour and draws one triangle, through the graph.</summary>
/// <remarks>
///     <para>
///         Shared by all three heads — desktop, iOS and Android — which is what makes it worth the
///         care below. On a desktop the surface exists before the first frame and never goes away.
///         On a phone neither is true, and a game written against the desktop's assumptions does not
///         merely misbehave there: it renders nothing, forever, with no error.
///     </para>
///     <para>
///         <b>So the device is built lazily and rebuilt after loss.</b> Nothing GPU-shaped happens in
///         <see cref="OnInitialise" />; the first <see cref="OnRender" /> that finds a presentable
///         surface builds everything, and <see cref="PlatformEventKind.Suspending" /> tears it back
///         down. On Android the surface arrives some frames after the activity is created — a
///         <c>SurfaceView</c> gets its window when it is laid out — so the eager version would find
///         nothing and give up.
///     </para>
///     <para>
///         <b>The whole device goes, not just the swapchain, and that is the RHI's shape rather than
///         a choice.</b> <c>VulkanDevice</c> takes a <c>SurfaceHandle</c> at creation and holds the
///         <c>VkSurfaceKHR</c> it makes from it, because that is what picking a present-capable queue
///         family needs. When Android destroys the <c>ANativeWindow</c>, that surface is invalid, so
///         the device built on it is too. Worth knowing before someone tries to make suspend cheap:
///         it costs a full device recreation today, and making it cost less means moving the surface
///         out of the device and into the swapchain.
///     </para>
/// </remarks>
public sealed class TriangleGame : Game {
    VulkanDevice? device;
    TransientResourcePool? pool;
    RenderGraph? graph;
    ISwapChain? swapChain;
    ILogger? log;

    ShaderHandle vertexShader;
    ShaderHandle fragmentShader;
    PipelineLayoutHandle layout;
    PipelineHandle pipeline;

    bool lost;
    bool waiting;

    /// <inheritdoc />
    protected override void OnConfigure(AppConfig config) {
        ArgumentNullException.ThrowIfNull(config);

        config.Name = "Hello Triangle";

        // ⚠ `IsVisible` follows `Headless`, because `AppConfig.Apply` has already read the command
        // line by the time this runs — deliberately, so a game can override an operator, which makes
        // an unconditional `true` an override nobody meant to write.
        config.Window = new() {
            Title = "Vixen — Hello Triangle",
            Size = new(1280, 720),
            IsVisible = !config.Headless
        };

        // This sample opens its own device and presents its own swapchain, which is the whole point
        // of it — so the host must not open a second one on the same surface. Off is one line, and
        // the line is what says "the stack below is being shown rather than used".
        config.Graphics.Enabled = false;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Takes a logger and nothing else. Everything that needs a surface waits for one.
    /// </remarks>
    protected override void OnInitialise() => log = Services.LoggerFactory.CreateLogger("HelloTriangle");

    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="PlatformEventKind.Suspending" /> means the surface is going away — on Android
    ///     literally, on iOS in the sense that the GPU may not be touched until the application is
    ///     frontmost again. Either way everything built on it is released here, while the surface is
    ///     still valid, because after the event it is not.
    /// </remarks>
    protected override bool OnEvent(in PlatformEvent platformEvent) {
        if (platformEvent.Kind is PlatformEventKind.Suspending) {
            Release();
        }

        // Never handled: the host still needs to see these.
        return false;
    }

    /// <inheritdoc />
    protected override void OnRender(GameTime time) {
        if (lost || !EnsureDevice()) {
            return;
        }

        // BeginFrame first: it waits for the frame slot this is about to reuse, and acquiring before
        // that would ask the presentation engine for an image while the GPU may still be reading the
        // one from two frames ago.
        device!.BeginFrame();

        var status = swapChain!.AcquireNextImage(out var view);

        if (status is SwapChainStatus.OutOfDate) {
            // Not an error. It happens every time a window edge is dragged and every time a phone is
            // rotated, which is why the RHI returns it rather than throwing.
            Recreate();
            return;
        }

        if (status is SwapChainStatus.DeviceLost) {
            if (log is not null) {
                SampleLog.DeviceLost(log);
            }

            lost = true;
            return;
        }

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "frame")) {
            var backbuffer = graph!.ImportTexture(
                swapChain.CurrentTexture,
                view,
                new(swapChain.Format, swapChain.Size.X, swapChain.Size.Y, TextureUsage.ColourTarget, Name: "backbuffer"),

                // Undefined on entry: the image's previous contents are the frame before last's, and
                // nothing here reads them. Present on exit, or the presentation engine is handed an
                // image in the wrong layout.
                ResourceState.Undefined,
                ResourceState.Present
            );

            var hue = (float)((Math.Sin(time.Total.TotalSeconds) * 0.5) + 0.5);

            graph.AddPass("triangle", pass => {
                pass.ColourAttachment(backbuffer, LoadAction.Clear, new(0.05f, hue * 0.1f, 0.15f, 1f));

                pass.Execute(context => {
                    context.CommandList.BindPipeline(pipeline);
                    context.CommandList.Draw(3);
                });
            });

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

    /// <inheritdoc />
    protected override void OnShutdown() => Release();

    /// <summary>
    ///     Builds the device and everything on it, once there is something to present to.
    /// </summary>
    /// <returns><see langword="false" /> if there is still no surface.</returns>
    /// <remarks>
    ///     The "no surface yet" case is logged once rather than every frame. On Android it is the
    ///     normal state for the first few frames and on a headless run it is the state forever, and
    ///     sixty lines a second either way helps nobody.
    /// </remarks>
    bool EnsureDevice() {
        if (device is not null) {
            return true;
        }

        if (Services.Window is not { } window || !window.Surface.Handle.CanPresent) {
            if (!waiting && log is not null) {
                SampleLog.NoWindow(log);
                waiting = true;
            }

            return false;
        }

        waiting = false;

        device = VulkanDevice.Create(new() {
            Surface = window.Surface.Handle,
            Logger = Services.LoggerFactory.CreateLogger("Vulkan")
        });

        pool = new(device);
        graph = new(device, pool);

        swapChain = device.CreateSwapChain(new(
            window.Surface.Handle,
            new(window.FramebufferSize.X, window.FramebufferSize.Y),
            PixelFormat.Bgra8UNormSrgb
        ));

        vertexShader = device.CreateShader(ShaderStage.Vertex, Load("triangle.vert.spv"), "triangle vertex");
        fragmentShader = device.CreateShader(ShaderStage.Fragment, Load("triangle.frag.spv"), "triangle fragment");
        layout = device.CreatePipelineLayout(new([], null, "triangle layout"));

        pipeline = device.CreateGraphicsPipeline(new(
            vertexShader,
            fragmentShader,
            layout,
            [new(swapChain.Format)],
            Rasterizer: RasterizerState.TwoSided,
            DepthStencil: DepthStencilState.Disabled,
            Name: "triangle"
        ));

        if (log is not null) {
            SampleLog.DeviceReady(
                log,
                device.Adapter.Name,
                device.Adapter.Kind,
                swapChain.Format,
                swapChain.Size.X,
                swapChain.Size.Y,
                swapChain.ImageCount
            );
        }

        return true;
    }

    /// <summary>Tears the device down, in the reverse of the order it was built.</summary>
    /// <remarks>
    ///     Idempotent, because it is reached from both a suspend and a shutdown and a suspend is
    ///     routinely followed by one.
    /// </remarks>
    void Release() {
        if (device is null) {
            return;
        }

        device.WaitIdle();
        swapChain?.Dispose();

        device.Destroy(pipeline);
        device.Destroy(layout);
        device.Destroy(fragmentShader);
        device.Destroy(vertexShader);

        pool?.Dispose();
        device.Dispose();

        swapChain = null;
        graph = null;
        pool = null;
        device = null;
    }

    void Recreate() {
        if (device is null || swapChain is null || Services.Window is not { } window) {
            return;
        }

        device.WaitIdle();
        swapChain.Resize(new(window.FramebufferSize.X, window.FramebufferSize.Y));

        if (log is not null) {
            SampleLog.SwapChainRebuilt(log, swapChain.Size.X, swapChain.Size.Y);
        }
    }

    static byte[] Load(string name) {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(entry => entry.EndsWith(name, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

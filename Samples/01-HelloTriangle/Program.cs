// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Microsoft.Extensions.Logging;
using Vixen.App;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Graphics.Vulkan;
using Vixen.Platform;
using Vixen.Platform.Desktop;

namespace Vixen.Samples.HelloTriangle;

/// <summary>The first triangle, and the first time every layer runs at once.</summary>
/// <remarks>
///     <para>
///         Deliberately the whole stack and nothing else: the app host opens a window, the desktop
///         platform hands over its native surface, the Vulkan backend builds a device and a
///         swapchain from it, and the render graph places the barriers. There is no engine, no ECS,
///         no asset pipeline — those arrive in Phase 2, and this staying small is what makes it a
///         platform smoke test rather than a demo.
///     </para>
///     <para>
///         It is also the only thing that exercises acquire and present. Those cannot be tested
///         automatically: presenting needs a window, and AppKit aborts when one is created off the
///         process's main thread, which is why the desktop tests force SDL's dummy video driver on
///         macOS ([10](../../docs/plan/10-platforms.md)). So this is where that path is verified, by
///         hand. <c>--vixen-frames N</c> — which this sample needed and which therefore belongs to the
///         host rather than to it — lets CI at least prove the whole stack starts, presents and stops
///         without a validation error or a hang.
///     </para>
/// </remarks>
static class Program {
    static int Main(string[] arguments) {
        // The platform is built here rather than left to the host's default because a Vulkan surface
        // has to be asked for before the window exists: SDL needs the VULKAN window flag at creation
        // time, and a window made without it has no surface to present to.
        var platform = new DesktopPlatform(new() {
            Organisation = "Vixen",
            Application = "HelloTriangle",
            RequestGpuSurface = true
        });

        using var application = VixenApp.Create(arguments)
            .WithPlatform(platform)
            .WithServices(services => services.LoggerFactory.AddProvider(new ConsoleLogProvider()))
            .Build(new TriangleGame());

        return application.Run();
    }
}

/// <summary>Clears to a colour and draws one triangle, through the graph.</summary>
sealed class TriangleGame : Game {
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

    /// <inheritdoc />
    protected override void OnConfigure(AppConfig config) {
        config.Name = "Hello Triangle";
        config.Window = new() { Title = "Vixen — Hello Triangle", Size = new(1280, 720), IsVisible = true };

    }

    /// <inheritdoc />
    protected override void OnInitialise() {
        log = Services.LoggerFactory.CreateLogger("HelloTriangle");
        var window = Services.Window;

        if (window is null || !window.Surface.Handle.CanPresent) {
            SampleLog.NoWindow(log);
            return;
        }

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

    /// <inheritdoc />
    protected override void OnRender(GameTime time) {
        if (device is null || swapChain is null || graph is null || lost) {
            return;
        }

        // BeginFrame first: it waits for the frame slot this is about to reuse, and acquiring before
        // that would ask the presentation engine for an image while the GPU may still be reading the
        // one from two frames ago.
        device.BeginFrame();

        var status = swapChain.AcquireNextImage(out var view);

        if (status is SwapChainStatus.OutOfDate) {
            // Not an error. It happens every time a window edge is dragged, which is why the RHI
            // returns it rather than throwing — an exception per frame for the duration of a drag.
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
            var backbuffer = graph.ImportTexture(
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
    protected override void OnShutdown() {
        device?.WaitIdle();
        swapChain?.Dispose();

        if (device is not null) {
            device.Destroy(pipeline);
            device.Destroy(layout);
            device.Destroy(fragmentShader);
            device.Destroy(vertexShader);
        }

        pool?.Dispose();
        device?.Dispose();
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

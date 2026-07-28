// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Vixen.App;
using Vixen.Core;
using Vixen.Graphics;
using Vixen.Graphics.RenderGraph;
using Vixen.Graphics.Vulkan;
using Vixen.Platform;
using Vixen.Video;
using Vixen.Video.Gpu;
using Vixen.Video.Playback;

namespace Vixen.Samples.VideoPlayback;

/// <summary>A video on the screen: demuxed, decoded, uploaded as three planes, converted in the sampler.</summary>
/// <remarks>
///     <para>
///         What this proves that the unit tests cannot. <c>Vixen.Video</c>'s suite asserts the
///         container, the codec, the clock and the upload calls; none of it puts a picture in front
///         of a person. The half that only a running frame exercises is here — the planes reaching
///         the GPU in the right order, the coefficients matching the shader that consumes them, and
///         the clock choosing frames at the rate the file was written at.
///     </para>
///     <para>
///         <b>The upload happens before the render pass, not inside one.</b> Copying into a texture
///         needs barriers either side of it and a barrier inside a pass is invalid on every API — the
///         transitions a pass needs are declared by its attachments. So <c>VideoTexture.Upload</c>
///         records onto the frame's command list first, and the graph's pass runs after it on the
///         same list.
///     </para>
///     <para>
///         <b>The swapchain is deliberately not sRGB here.</b> A decoded video's RGB is already
///         gamma-encoded — that is what the BT.709 transfer function is — so writing it to an sRGB
///         target would encode it a second time and show as a picture that is far too bright in the
///         mid-tones. A renderer that lit the video as a texture in a scene would want the opposite;
///         a player that shows it directly wants the bytes to arrive as they are.
///     </para>
/// </remarks>
public sealed class VideoGame : Game {
    /// <summary>Twelve floats: the letterbox fit, and the six numbers that convert YUV to RGB.</summary>
    const int PushConstantSize = 12 * sizeof(float);

    VulkanDevice? device;
    TransientResourcePool? pool;
    RenderGraph? graph;
    ISwapChain? swapChain;
    ILogger? log;

    VideoPlayer? player;
    VideoTexture? texture;

    ShaderHandle vertexShader;
    ShaderHandle fragmentShader;
    DescriptorSetLayoutHandle setLayout;
    DescriptorSetHandle descriptors;
    PipelineLayoutHandle layout;
    PipelineHandle pipeline;

    TextureViewHandle boundLuma;
    bool lost;
    bool waiting;

    /// <inheritdoc />
    protected override void OnConfigure(AppConfig config) {
        config.Name = "Video Playback";
        config.Window = new() { Title = "Vixen — Video Playback", Size = new(1280, 720), IsVisible = true };
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The player is built here and not in <c>EnsureDevice</c>: it owns a decoder, a thread and a
    ///     pool of frames, none of which is GPU-shaped, so it survives the device being lost and
    ///     rebuilt — and a cutscene that restarted every time a phone was rotated would be a bug of
    ///     exactly that shape.
    /// </remarks>
    protected override void OnInitialise() {
        log = Services.LoggerFactory.CreateLogger("VideoPlayback");

        var bytes = GeneratedVideo.Build();

        player = new VideoPlayer(
            new WebMVideoStreamDecoder(new MemoryStream(bytes, writable: false)),
            new VideoPlayerOptions { Loop = true }
        );

        player.Play();

        if (log is not null) {
            SampleLog.VideoOpened(
                log,
                player.Decoder.Format.Width,
                player.Decoder.Format.Height,
                player.Decoder.Format.FrameRate.Hz,
                player.Duration.TotalSeconds,
                bytes.Length / (1024 * 1024)
            );
        }
    }

    /// <inheritdoc />
    protected override bool OnEvent(in PlatformEvent platformEvent) {
        if (platformEvent.Kind is PlatformEventKind.Suspending) {
            Release();
        }

        return false;
    }

    /// <inheritdoc />
    protected override void OnRender(GameTime time) {
        // Advanced whether or not there is anywhere to draw it. A video that stopped while the
        // window was away would resume three seconds behind wherever its audio had got to.
        player?.Update(time.Elapsed);

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
            if (log is not null) {
                SampleLog.DeviceLost(log);
            }

            lost = true;

            return;
        }

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "frame")) {
            // Version-checked inside: a 25 fps video in a 144 fps window costs one upload in six
            // frames rather than one per frame.
            if (texture!.Upload(commands, player!)) {
                Rebind();
            }

            var backbuffer = graph!.ImportTexture(
                swapChain.CurrentTexture,
                view,
                new(swapChain.Format, swapChain.Size.X, swapChain.Size.Y, TextureUsage.ColourTarget, Name: "backbuffer"),
                ResourceState.Undefined,
                ResourceState.Present
            );

            graph.AddPass("video", pass => {
                pass.ColourAttachment(backbuffer, LoadAction.Clear, new(0f, 0f, 0f, 1f));

                pass.Execute(context => {
                    if (!descriptors.IsValid || !boundLuma.IsValid) {
                        // Nothing has been decoded yet, so there is nothing to sample. The clear is
                        // the whole frame, which is what the first few milliseconds of any video is.
                        return;
                    }

                    Span<float> constants = stackalloc float[12];

                    Fit(constants);
                    Coefficients(constants);

                    context.CommandList.BindPipeline(pipeline);
                    context.CommandList.BindDescriptorSet(DescriptorSetSlot.PerFrame, descriptors);
                    context.CommandList.PushConstants(
                        ShaderStage.Fragment,
                        0,
                        MemoryMarshal.AsBytes(constants)
                    );

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
        Release();

        player?.Dispose();
        player = null;
    }

    /// <summary>The scale and offset that letterbox the video into the window.</summary>
    /// <remarks>
    ///     In the shader rather than in a viewport, because a viewport would leave the bars
    ///     untouched by the clear and full of whatever the last frame put there. The bars are drawn
    ///     black by the same triangle.
    /// </remarks>
    void Fit(Span<float> constants) {
        var format = texture!.Format;
        var window = (float)swapChain!.Size.X / Math.Max(1, swapChain.Size.Y);
        var video = format.Height > 0 ? (float)format.Width / format.Height : window;

        var horizontal = window > video ? video / window : 1f;
        var vertical = window > video ? 1f : window / video;

        constants[0] = 1f / horizontal;
        constants[1] = 1f / vertical;
        constants[2] = (1f - horizontal) * 0.5f;
        constants[3] = (1f - vertical) * 0.5f;
    }

    /// <summary>The six numbers the shader multiplies by, taken from the frame's own metadata.</summary>
    void Coefficients(Span<float> constants) {
        var coefficients = texture!.Coefficients;

        constants[4] = coefficients.LumaOffset;
        constants[5] = coefficients.LumaScale;
        constants[6] = coefficients.RedV;
        constants[7] = coefficients.BlueU;
        constants[8] = coefficients.GreenU;
        constants[9] = coefficients.GreenV;
    }

    /// <summary>Points the descriptor set at the planes, when they are new ones.</summary>
    /// <remarks>
    ///     Only when they change, which is the first upload and any resolution change — and never
    ///     per frame, because the planes are reused. The wait is what makes it safe: a descriptor set
    ///     a frame still in flight is reading must not be rewritten, and the alternative to waiting
    ///     is a set per frame in flight for something that happens twice in a run.
    /// </remarks>
    void Rebind() {
        if (texture!.PlaneView(0) == boundLuma) {
            return;
        }

        device!.WaitIdle();

        device.UpdateDescriptorSet(
            descriptors,
            [
                DescriptorWrite.Texture(0, texture.PlaneView(0)),
                DescriptorWrite.Texture(1, texture.PlaneView(1)),
                DescriptorWrite.Texture(2, texture.PlaneView(2)),
                DescriptorWrite.SamplerAt(3, texture.Sampler)
            ]
        );

        boundLuma = texture.PlaneView(0);

        if (log is not null) {
            SampleLog.PlanesBound(log, texture.Format.Width, texture.Format.Height, texture.PlaneCount);
        }
    }

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

            // Not sRGB. See the remarks on this class: a decoded video is already gamma-encoded.
            PixelFormat.Bgra8UNorm
        ));

        texture = new VideoTexture(device, "video");

        vertexShader = device.CreateShader(ShaderStage.Vertex, Load("video.vert.spv"), "video vertex");
        fragmentShader = device.CreateShader(ShaderStage.Fragment, Load("video.frag.spv"), "video fragment");

        setLayout = device.CreateDescriptorSetLayout(
            new(
                // Set 0, which the convention calls the per-frame set. A video pass has no per-frame
                // or per-view set at all — the fit and the coefficients are twelve floats in a push
                // constant — so the planes are the only set there is, and two empty sets in front of
                // them would cost two bind points to honour a naming convention. `UiRenderer` makes
                // the same call for the same reason.
                DescriptorSetSlot.PerFrame,
                [
                    new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                    new(1, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                    new(2, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                    new(3, DescriptorKind.Sampler, ShaderStage.Fragment)
                ],
                "video planes"
            )
        );

        descriptors = device.CreateDescriptorSet(setLayout, "video planes");

        layout = device.CreatePipelineLayout(
            new(
                [setLayout],
                [new(ShaderStage.Fragment, 0, PushConstantSize)],
                "video layout"
            )
        );

        pipeline = device.CreateGraphicsPipeline(new(
            vertexShader,
            fragmentShader,
            layout,
            [new(swapChain.Format)],
            Rasterizer: RasterizerState.TwoSided,
            DepthStencil: DepthStencilState.Disabled,
            Name: "video"
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

    void Release() {
        if (device is null) {
            return;
        }

        device.WaitIdle();
        swapChain?.Dispose();
        texture?.Dispose();

        device.Destroy(pipeline);
        device.Destroy(layout);
        device.Destroy(descriptors);
        device.Destroy(setLayout);
        device.Destroy(fragmentShader);
        device.Destroy(vertexShader);

        pool?.Dispose();
        device.Dispose();

        boundLuma = default;
        descriptors = default;
        texture = null;
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
    }

    static byte[] Load(string name) {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .Single(entry => entry.EndsWith(name, StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var memory = new MemoryStream();

        stream.CopyTo(memory);

        return memory.ToArray();
    }
}

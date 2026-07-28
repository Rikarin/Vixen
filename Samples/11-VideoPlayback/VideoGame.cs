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
using Vixen.Video.Audio;
using Vixen.Video.Codecs;
using Vixen.Video.Containers;
using Vixen.Video.Gpu;
using Vixen.Video.Playback;
using Vixen.Audio;
using Vixen.Audio.Backend.OpenAL;
using Vixen.Audio.Devices;
using Vixen.Audio.Mixing;
using Vixen.Audio.Streaming;

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

    byte[] container = [];

    readonly System.Diagnostics.Stopwatch started = System.Diagnostics.Stopwatch.StartNew();

    OpenALBackend? audioBackend;
    IAudioDevice? audioDevice;
    AudioEngine? audio;
    StreamingSampleProvider? sound;

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

        container = GeneratedVideo.Build();

        player = new VideoPlayer(
            new WebMVideoStreamDecoder(new MemoryStream(container, writable: false)),
            new VideoPlayerOptions { Loop = true }
        );

        if (log is not null) {
            SampleLog.VideoOpened(
                log,
                player.Decoder.Format.Width,
                player.Decoder.Format.Height,
                player.Decoder.Format.FrameRate.Hz,
                player.Duration.TotalSeconds,
                container.Length / (1024 * 1024)
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
        audio?.Update();
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

        // Printed because "it did not crash" is not the same as "it played". A position still at
        // zero after a run means the master clock never advanced — which is what a silent audio
        // device, or a provider whose position was read from the wrong end, looks like.
        StopAudio();

        if (log is not null && player is { } finished) {
            SampleLog.PlaybackSummary(
                log,
                finished.Position.TotalSeconds,
                started.Elapsed.TotalSeconds,
                finished.FramesShown,
                finished.FramesDropped,
                finished.DecodeStalls,
                sound is null ? 0 : (double)sound.Position / Math.Max(1, sound.Format.SampleRate),
                sound?.Underruns ?? 0,
                audioDevice?.Underruns ?? 0
            );
        }

        player?.Dispose();
        player = null;

        // The device before the engine: the engine's render runs on the device's thread, and
        // disposing it from under one that is still pulling is the classic shutdown crash.
        audioDevice?.Stop();
        audio?.Dispose();
        audioDevice?.Dispose();
        audioBackend?.Dispose();
        sound = null;
        audio = null;
        audioDevice = null;
        audioBackend = null;
    }

    /// <summary>Takes the stream off the pump before anything disposes what it is reading.</summary>
    /// <remarks>
    ///     Unregistering rather than merely stopping the voice: the pump fills the ring on its own
    ///     thread whether or not anything is listening, and disposing the decoder under it is a read
    ///     of a stream that has gone.
    /// </remarks>
    void StopAudio() {
        audioDevice?.Stop();

        if (audio is not null && sound is not null) {
            audio.Streams.Unregister(sound);
        }
    }

    /// <summary>Opens the video's own audio track and makes it the clock.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the whole of A/V sync, and it is three calls.</b> The sound is in the same
    ///         segment as the picture and is read by the same demuxer; it is played as an ordinary
    ///         stream, so the mixer knows nothing about video; and the video's clock is pointed at
    ///         the provider's position, which is frames <em>delivered to the mixer</em> rather than
    ///         frames decoded.
    ///     </para>
    ///     <para>
    ///         <b>No device is not an error.</b> A CI runner has no sound card, and the right
    ///         behaviour there is the one a video with no audio track gets anyway: the clock
    ///         integrates the frame delta. Saying which happened is what stops "the video runs at the
    ///         wrong speed on the build machine" being a mystery.
    ///     </para>
    /// </remarks>
    void StartAudio() {
        if (player is null) {
            return;
        }

        // Referencing Vixen.Video.Codecs is not enough on its own, deliberately: a module that
        // altered global state merely by being linked would behave differently under a trimmer.
        VideoAudioCodecs.RegisterOpus();

        // ⚠ A second demuxer over the same bytes, not the video's. Both this and the picture loop,
        // and a loop is a seek — one reader with two things seeking it yanks the file back to the
        // start under whichever of them did not ask, over and over. Two readers cost one more
        // position and a few hundred kilobytes of buffering; sharing one costs correctness. See
        // MatroskaDemuxer's remarks.
        var demuxer = new MatroskaDemuxer(new MemoryStream(container, writable: false));

        if (!MatroskaAudioStreamDecoder.TryOpen(demuxer, out var track) || track is null) {
            demuxer.Dispose();

            return;
        }

        audioBackend = new OpenALBackend();

        if (!audioBackend.IsAvailable) {
            if (log is not null) {
                SampleLog.NoAudio(log, "no OpenAL device on this machine");
            }

            track.Dispose();
            demuxer.Dispose();

            return;
        }

        try {
            audioDevice = audioBackend.OpenDevice(new AudioDeviceOptions { Format = new AudioFormat(48_000, 2) });
        } catch (AudioDeviceException failure) {
            if (log is not null) {
                SampleLog.NoAudio(log, failure.Message);
            }

            track.Dispose();
            demuxer.Dispose();

            return;
        }

        audio = new AudioEngine(audioDevice, new AudioEngineOptions(), Services.LoggerFactory.CreateLogger("Audio"));

        // The provider is built here rather than through PlayStream, because the clock needs the
        // provider itself — PlayStream keeps it, and what it hands back is a voice.
        sound = new StreamingSampleProvider(track, loop: true);
        audio.Streams.Register(sound);
        audio.Play(sound, new PlaybackSettings());

        player.FollowAudio(sound);

        if (log is not null) {
            SampleLog.AudioReady(log, audioDevice.Info.Name, audioDevice.Format.SampleRate, track.Track.CodecId);
        }
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

        // Started here rather than in OnInitialise, and it is not a detail. The clock is the sound,
        // and building a Vulkan device takes the better part of a second — so a video that began
        // playing before there was anywhere to draw it would spend its first second correctly
        // skipping frames nobody could have seen. A game starts a cutscene when the scene is ready.
        StartAudio();
        player!.Play();

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

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Microsoft.Extensions.Logging;
using Vixen.App;
using Vixen.Audio;
using Vixen.Audio.Backend.OpenAL;
using Vixen.Audio.Devices;
using Vixen.Audio.Mixing;
using Vixen.Audio.Streaming;
using Vixen.Core;
using Vixen.Core.Mathematics;
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
using Vixen.Video.Rendering;

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
    VideoRenderer? renderer;

    bool announced;
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
            if (texture!.Upload(commands, player!) && !announced && log is not null) {
                announced = true;
                SampleLog.PlanesBound(log, texture.Format.Width, texture.Format.Height, texture.PlaneCount);
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

                pass.Execute(context => Draw(context.CommandList));
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

    /// <summary>The frame's two videos: the picture, and the same picture in a corner.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Two draws of one texture, and that is what this sample is for now.</b> Before
    ///         <c>VideoRenderer</c> existed, this file built its own pipeline, its own descriptor set
    ///         and its own twelve-float push block, and drew a full-screen triangle — which meant a
    ///         video could only ever be the whole screen. It is now four lines that say where, and
    ///         "where" can be a corner.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The letterboxing has moved out of the shader.</b> It used to be a discard in the
    ///         fragment stage that painted the bars black, which is correct for a player showing a
    ///         video on nothing and wrong for everything else — opaque black over whatever the video
    ///         was laid over. <c>VideoFit</c> shrinks the rectangle instead, so the bars are simply
    ///         not drawn on, and the pass's clear is what fills them.
    ///     </para>
    ///     <para>
    ///         The inset is deliberately <see cref="VideoScaling.Cover" />: it crops rather than
    ///         letterboxes, so the two rectangles between them exercise both halves of
    ///         <c>VideoPlacement</c> — the one that moves the rectangle and the one that moves the
    ///         texture coordinates.
    ///     </para>
    /// </remarks>
    void Draw(ICommandList commands) {
        if (renderer is null || texture is null || player is null || texture.PlaneCount == 0) {
            // Nothing decoded yet, so there is nothing to sample. The clear is the whole frame, which
            // is what the first few milliseconds of any video is.
            return;
        }

        var surface = swapChain!.Size;
        var whole = new Rectangle(0, 0, surface.X, surface.Y);

        renderer.Begin();

        renderer.Record(
            commands,
            VideoDraw.From(texture, VideoFit.Place(VideoScaling.Contain, player, whole)),
            surface
        );

        // A sixth of the width, in the bottom-right corner, one twentieth in from each edge.
        var inset = surface.X / 6f;

        renderer.Record(
            commands,
            VideoDraw.From(
                texture,
                VideoFit.Place(
                    VideoScaling.Cover,
                    player,
                    new Rectangle(
                        surface.X - inset - (surface.X / 20f),
                        surface.Y - (inset * 0.75f) - (surface.Y / 20f),
                        inset,
                        inset * 0.75f
                    )
                )
            ),
            surface
        );
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

        // ⚠ The pipeline, the layout, the descriptor set and the sixty-four-byte push block all live
        // in `VideoRenderer` now. What is left here is the two things only an application knows: the
        // shader modules — which nothing compiles yet, so a caller supplies them — and the formats of
        // the pass they will be drawn in.
        renderer = new VideoRenderer(
            device,
            new VideoShaders(vertexShader, fragmentShader),
            new Rendering.RenderOutput([swapChain.Format]),
            "video"
        );

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

        // ⚠ The renderer before the texture: it holds a descriptor set pointing at the texture's
        // plane views, and destroying a view a live set still names is the one ordering the
        // validation layers are strictest about.
        renderer?.Dispose();
        texture?.Dispose();

        device.Destroy(fragmentShader);
        device.Destroy(vertexShader);

        pool?.Dispose();
        device.Dispose();

        announced = false;
        renderer = null;
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

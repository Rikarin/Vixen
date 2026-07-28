// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.EXT;
using Vixen.Audio.Devices;
using Vixen.Audio.Streaming;
using AudioDeviceException = Vixen.Audio.Devices.AudioDeviceException;

namespace Vixen.Audio.Backend.OpenAL;

/// <summary>A microphone, through <c>ALC_EXT_CAPTURE</c>.</summary>
/// <remarks>
///     <para>
///         <b>OpenAL capture is a poll, so there is a thread.</b> There is no callback: the only way
///         to know how much audio has arrived is to ask <c>alcGetIntegerv</c> and then take it. A
///         thread doing that every few milliseconds is the whole of the design, and it is the same
///         shape as <c>AudioStreamPump</c> — a thread that may block, a ring, and a reader that never
///         does.
///     </para>
///     <para>
///         <b>Sixteen-bit, converted on the way in.</b> Float capture needs <c>AL_EXT_FLOAT32</c> on
///         the capture device, which is far less widely present than on the playback side, and a
///         microphone is a 16-bit converter in every consumer machine — so the conversion is free of
///         information and the format is the one that always works.
///     </para>
///     <para>
///         <b>The device's own buffer is asked to be large.</b> OpenAL drops what does not fit
///         between polls with no way to report it, so the ring here is where a slow reader is
///         supposed to lose audio — there it is counted.
///     </para>
/// </remarks>
sealed class OpenALCaptureDevice : IAudioCaptureDevice {
    readonly Capture capture;
    readonly AudioRingBuffer ring;
    readonly short[] scratch;
    readonly float[] converted;
    readonly Lock gate = new();

    unsafe Device* handle;
    Thread? pump;
    volatile bool running;
    long overruns;

    public unsafe OpenALCaptureDevice(Capture capture, Device* handle, in AudioDeviceInfo info, in AudioFormat format) {
        this.capture = capture;
        this.handle = handle;
        Info = info;
        Format = format;

        // A quarter of the ring, so a poll that finds a lot waiting still takes it in a few passes
        // rather than in one enormous copy.
        var chunk = Math.Max(PollFrames, 1) * format.Channels;
        scratch = new short[chunk];
        converted = new float[chunk];
        ring = new(BufferedFrames * format.Channels);
    }

    // Twenty milliseconds at 48 kHz. Long enough that the poll is cheap against the work, short
    // enough that a voice-chat packet is not held for longer than it takes to send one.
    const int PollFrames = 960;
    const int BufferedFrames = 9_600;

    public AudioDeviceInfo Info { get; }

    public AudioFormat Format { get; }

    public bool IsRunning => running;

    public int Available => ring.Count / Format.Channels;

    public long Overruns => Interlocked.Read(ref overruns);

    public unsafe void Start() {
        lock (gate) {
            if (running || handle is null) {
                return;
            }

            capture.CaptureStart(handle);
            running = true;

            pump = new Thread(Pump) {
                IsBackground = true,
                Name = "Vixen Audio Capture",

                // Above normal, because the whole job is to drain a fixed-size driver buffer before
                // it wraps. Below the audio thread's, because dropping a microphone frame is a
                // syllable and missing an output deadline is the whole mix.
                Priority = ThreadPriority.AboveNormal
            };

            pump.Start();
        }
    }

    public unsafe void Stop() {
        Thread? joining;

        lock (gate) {
            if (!running) {
                return;
            }

            running = false;
            joining = pump;
            pump = null;
        }

        joining?.Join();

        lock (gate) {
            if (handle is not null) {
                capture.CaptureStop(handle);
            }
        }
    }

    public int Read(Span<float> destination, int frameCount) {
        var channels = Format.Channels;
        var wanted = Math.Min(frameCount, destination.Length / channels) * channels;
        return wanted <= 0 ? 0 : ring.Read(destination[..wanted]) / channels;
    }

    public unsafe void Dispose() {
        Stop();

        lock (gate) {
            if (handle is null) {
                return;
            }

            capture.CaptureCloseDevice(handle);
            handle = null;
        }
    }

    unsafe void Pump() {
        while (running) {
            var moved = false;

            lock (gate) {
                if (handle is null) {
                    break;
                }

                var available = capture.GetAvailableSamples(handle);

                if (available > 0) {
                    var frames = Math.Min(available, PollFrames);

                    fixed (short* target = scratch) {
                        capture.CaptureSamples(handle, target, frames);
                    }

                    var samples = frames * Format.Channels;

                    for (var i = 0; i < samples; i++) {
                        // The asymmetric divisor is deliberate: −32768 maps to exactly −1 and +32767
                        // to a hair under +1, which is what keeps a full-scale input from wrapping
                        // when something downstream negates it.
                        converted[i] = scratch[i] / 32_768f;
                    }

                    var written = ring.Write(converted.AsSpan(0, samples));

                    if (written < samples) {
                        Interlocked.Add(ref overruns, (samples - written) / Format.Channels);
                    }

                    moved = true;
                }
            }

            if (!moved) {
                // Half a poll's worth. Sleeping the whole of one would mean a poll that just missed
                // the arrival waits a full period with the driver's buffer filling behind it.
                Thread.Sleep(PollFrames * 500 / Format.SampleRate);
            }
        }
    }

    /// <summary>Opens a capture device, or says why not.</summary>
    public static unsafe OpenALCaptureDevice Open(Capture capture, in AudioCaptureOptions options) {
        var requested = options.Format.IsValid ? options.Format : AudioFormat.Mono48k;

        // Mono or stereo, and 16-bit either way. Everything else is an extension on the capture side
        // that a driver may simply not have.
        var channels = Math.Clamp(requested.Channels, 1, 2);
        var format = new AudioFormat(requested.SampleRate, channels);
        var bufferFormat = channels == 1 ? BufferFormat.Mono16 : BufferFormat.Stereo16;

        var handle = capture.CaptureOpenDevice(
            options.DeviceId ?? string.Empty,
            (uint)format.SampleRate,
            bufferFormat,
            BufferedFrames
        );

        if (handle is null) {
            throw new AudioDeviceException(
                options.DeviceId is null
                    ? "alcCaptureOpenDevice returned nothing for the default microphone."
                    : $"alcCaptureOpenDevice returned nothing for '{options.DeviceId}'."
            );
        }

        var info = new AudioDeviceInfo(
            options.DeviceId ?? string.Empty,
            string.IsNullOrEmpty(options.DeviceId) ? "Default microphone" : options.DeviceId,
            string.IsNullOrEmpty(options.DeviceId),
            format
        );

        return new(capture, handle, info, format);
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vixen.Audio.Devices;
using AudioDeviceException = Vixen.Audio.Devices.AudioDeviceException;

namespace Vixen.Audio.Backend.WebAudio;

/// <summary>A microphone, through <c>getUserMedia</c>.</summary>
/// <remarks>
///     <para>
///         <b><see cref="Start" /> returns before anything is running, and cannot do otherwise.</b>
///         <c>getUserMedia</c> resolves a promise, and what it is waiting on is a human deciding
///         whether to grant permission. <see cref="IsRunning" /> is the thing to watch; there is no
///         arrangement of this API in which a browser answers synchronously.
///     </para>
///     <para>
///         <b>It also has to be started from a user gesture.</b> Every browser refuses a microphone
///         request that did not originate in a click or a key press, so a game that asks at load
///         will be refused without a prompt ever appearing. The button that turns voice chat on is
///         not a nicety of the interface; it is the mechanism.
///     </para>
///     <para>
///         The buffering lives in JavaScript, because a <c>ScriptProcessorNode</c> callback cannot
///         reach into the runtime to append to a .NET ring — the same single-threaded constraint that
///         shapes the output side.
///     </para>
/// </remarks>
[SupportedOSPlatform("browser")]
sealed class WebAudioCaptureDevice : IAudioCaptureDevice {
    readonly int handle;
    readonly byte[] transfer;
    bool disposed;

    WebAudioCaptureDevice(int handle, in AudioDeviceInfo info, in AudioFormat format, int transferFrames) {
        this.handle = handle;
        Info = info;
        Format = format;
        transfer = new byte[transferFrames * format.Channels * sizeof(float)];
    }

    /// <inheritdoc />
    public AudioDeviceInfo Info { get; }

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <inheritdoc />
    public bool IsRunning => !disposed && WebAudioInterop.CaptureIsRunning(handle);

    /// <inheritdoc />
    public int Available => disposed ? 0 : WebAudioInterop.CaptureAvailable(handle);

    /// <inheritdoc />
    public long Overruns => disposed ? 0 : WebAudioInterop.CaptureOverruns(handle);

    /// <inheritdoc />
    public void Start() {
        if (!disposed) {
            WebAudioInterop.CaptureStart(handle);
        }
    }

    /// <inheritdoc />
    public void Stop() {
        if (!disposed) {
            WebAudioInterop.CaptureStop(handle);
        }
    }

    /// <inheritdoc />
    public int Read(Span<float> destination, int frameCount) {
        var channels = Format.Channels;
        var wanted = Math.Min(frameCount, destination.Length / channels);

        if (disposed || wanted <= 0) {
            return 0;
        }

        // In transfer-sized bites, because the byte buffer is fixed and a caller draining a long
        // silence may ask for more than one crossing's worth.
        var capacity = transfer.Length / (channels * sizeof(float));
        var taken = 0;

        while (taken < wanted) {
            var chunk = Math.Min(capacity, wanted - taken);
            var got = WebAudioInterop.CaptureRead(handle, transfer.AsSpan(), chunk);

            if (got <= 0) {
                break;
            }

            MemoryMarshal.Cast<byte, float>(transfer.AsSpan(0, got * channels * sizeof(float)))
                .CopyTo(destination[(taken * channels)..]);

            taken += got;

            if (got < chunk) {
                break;
            }
        }

        return taken;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        WebAudioInterop.CaptureClose(handle);
    }

    /// <summary>Opens a capture slot, or says why not.</summary>
    public static WebAudioCaptureDevice Open(in AudioCaptureOptions options) {
        var requested = options.Format.IsValid ? options.Format : AudioFormat.Mono48k;
        var channels = Math.Clamp(requested.Channels, 1, 2);
        var buffered = Math.Max(options.BufferedFrames, 1_024);
        var handle = WebAudioInterop.CaptureCreate(requested.SampleRate, channels, buffered);

        if (handle == 0) {
            throw new AudioDeviceException(
                "This browser has neither an AudioContext nor navigator.mediaDevices.getUserMedia. "
                + "A secure context — https, or localhost — is required for the second."
            );
        }

        // The browser decides the rate and will not be argued with, exactly as on the output side.
        var granted = WebAudioInterop.CaptureSampleRate(handle);
        var format = new AudioFormat(granted > 0 ? granted : requested.SampleRate, channels);

        var info = new AudioDeviceInfo(
            options.DeviceId ?? string.Empty,
            "Default microphone",
            true,
            format
        );

        // A tenth of a second per crossing, which keeps the interop call rate low without making the
        // buffer large enough to matter.
        return new(handle, info, format, Math.Min(buffered, format.SampleRate / 10));
    }
}

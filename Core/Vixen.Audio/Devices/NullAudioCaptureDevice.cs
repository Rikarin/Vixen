// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Streaming;

namespace Vixen.Audio.Devices;

/// <summary>A microphone with nobody in front of it, which something has to write into.</summary>
/// <remarks>
///     <para>
///         <b>The same argument as <see cref="NullAudioBackend" />, pointed the other way.</b> A
///         dedicated server has no microphone and still runs the code that reads one, and a CI
///         machine has none either — so "no input" has to be an ordinary state rather than a null
///         reference somewhere in the voice path.
///     </para>
///     <para>
///         <b><see cref="Push" /> is what makes it a test double rather than a stub.</b> Every claim
///         about the capture path — that a reader gets what the device produced, that a slow reader
///         loses the oldest audio and says so, that monitoring plays back what came in — is a claim
///         about buffering and hand-off, and none of it needs a real microphone to be true. What a
///         real microphone adds is a driver, and a driver is not what those assertions are about.
///     </para>
/// </remarks>
public sealed class NullAudioCaptureDevice : IAudioCaptureDevice {
    readonly AudioRingBuffer ring;
    readonly int channels;
    long overruns;

    /// <summary>A device that captures whatever is pushed into it.</summary>
    /// <param name="options">What to pretend to have opened.</param>
    public NullAudioCaptureDevice(in AudioCaptureOptions options) {
        Format = options.Format.IsValid ? options.Format : AudioFormat.Mono48k;
        channels = Format.Channels;
        ring = new(Math.Max(options.BufferedFrames, 1) * channels);

        Info = new(
            options.DeviceId ?? "null",
            "No microphone",
            true,
            Format
        );
    }

    /// <inheritdoc />
    public AudioDeviceInfo Info { get; }

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <inheritdoc />
    public bool IsRunning { get; private set; }

    /// <inheritdoc />
    public int Available => ring.Count / channels;

    /// <inheritdoc />
    public long Overruns => Interlocked.Read(ref overruns);

    /// <inheritdoc />
    public void Start() => IsRunning = true;

    /// <inheritdoc />
    public void Stop() => IsRunning = false;

    /// <summary>Pretends the microphone heard something.</summary>
    /// <param name="samples">Interleaved frames, in this device's format.</param>
    /// <returns>How many floats were taken. Fewer than offered means the buffer is full.</returns>
    /// <remarks>Ignored while stopped, because a stopped microphone hears nothing.</remarks>
    public int Push(ReadOnlySpan<float> samples) {
        if (!IsRunning) {
            return 0;
        }

        var written = ring.Write(samples);

        if (written < samples.Length) {
            Interlocked.Add(ref overruns, (samples.Length - written) / channels);
        }

        return written;
    }

    /// <inheritdoc />
    public int Read(Span<float> destination, int frameCount) {
        var wanted = Math.Min(frameCount, destination.Length / channels) * channels;
        return wanted <= 0 ? 0 : ring.Read(destination[..wanted]) / channels;
    }

    /// <inheritdoc />
    public void Dispose() => IsRunning = false;
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Devices;

/// <summary>What to open a microphone with.</summary>
public readonly record struct AudioCaptureOptions() {
    /// <summary>Which device, or <see langword="null" /> for whichever one is default.</summary>
    public string? DeviceId { get; init; }

    /// <summary>The format to capture in.</summary>
    /// <remarks>
    ///     <b>Mono, and at 48 kHz.</b> A microphone is one point in space, so a second channel is
    ///     either a duplicate or a second microphone nobody asked for — and every voice codec worth
    ///     using takes mono. The rate matches the output device's so that nothing has to resample on
    ///     the way to the mixer.
    /// </remarks>
    public AudioFormat Format { get; init; } = AudioFormat.Mono48k;

    /// <summary>How much captured audio the device holds before a reader is considered too slow.</summary>
    /// <remarks>
    ///     Two hundred milliseconds at 48 kHz. Generous, because the cost of being generous is a few
    ///     tens of kilobytes and the cost of being tight is dropped speech every time a frame runs
    ///     long. What it must not be is unbounded: a reader that stopped reading should lose the
    ///     oldest audio rather than accumulate a minute of it.
    /// </remarks>
    public int BufferedFrames { get; init; } = 9_600;
}

/// <summary>An open microphone.</summary>
/// <remarks>
///     <para>
///         <b>Pull, like everything else here, and for the opposite reason.</b> An output device pulls
///         because the hardware asks; a capture device is pulled because the thing that wants the
///         audio — an encoder, a voice-chat client, a recorder — is on the game thread and knows when
///         it can take some. Between the two sits a ring the platform fills, so a game thread that
///         runs long loses nothing.
///     </para>
///     <para>
///         <b>It is not an <see cref="IAudioDevice" />.</b> The two share almost nothing: one has a
///         render source and buffer counts, the other has an overrun counter and a read. Merging them
///         would produce a type half of whose members throw on any given instance.
///     </para>
///     <para>
///         Wrap one in <c>CaptureSampleProvider</c> to hear it — which is monitoring, and also the
///         only way to test the path end to end.
///     </para>
/// </remarks>
public interface IAudioCaptureDevice : IDisposable {
    /// <summary>Which device this is.</summary>
    AudioDeviceInfo Info { get; }

    /// <summary>The format it was actually opened in.</summary>
    AudioFormat Format { get; }

    /// <summary>Whether it is currently capturing.</summary>
    bool IsRunning { get; }

    /// <summary>How many frames are waiting to be read.</summary>
    int Available { get; }

    /// <summary>How many frames were thrown away because nobody read them in time.</summary>
    /// <remarks>
    ///     The number that says a reader is too slow, and the counterpart of
    ///     <see cref="IAudioDevice.Underruns" />. Non-zero and rising means speech is being lost.
    /// </remarks>
    long Overruns { get; }

    /// <summary>Starts capturing. Anything captured before the first <see cref="Read" /> is buffered.</summary>
    /// <remarks>
    ///     <b>It may not have started when this returns.</b> A browser asks the user for permission
    ///     and will not answer synchronously, so <see cref="IsRunning" /> is the thing to watch rather
    ///     than the absence of an exception — and a caller that treats "no audio yet" as a failure
    ///     will be wrong on the one platform where it matters most.
    /// </remarks>
    /// <exception cref="AudioDeviceException">It would not start.</exception>
    void Start();

    /// <summary>Stops capturing. Starting again is legal.</summary>
    void Stop();

    /// <summary>Takes what has been captured.</summary>
    /// <param name="destination">
    ///     Interleaved floats, at least <c>frameCount × channels</c> long. Only what was available is
    ///     written; the rest is untouched.
    /// </param>
    /// <param name="frameCount">The most frames to take.</param>
    /// <returns>How many frames were taken. Zero means nothing has been captured yet.</returns>
    /// <remarks>
    ///     <b>Fewer than asked for is the normal case</b>, not an error — a microphone produces audio
    ///     at its own rate and a caller asks at the frame rate. Reading in a loop until it returns
    ///     zero is how a caller drains it.
    /// </remarks>
    int Read(Span<float> destination, int frameCount);
}

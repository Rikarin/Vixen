// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Devices;

namespace Vixen.Audio.Sources;

/// <summary>A microphone, as something the mixer can play.</summary>
/// <remarks>
///     <para>
///         Monitoring: <c>engine.Play(new CaptureSampleProvider(microphone), settings)</c> and the
///         player hears themselves, through whatever bus that is routed to and therefore through
///         whatever gate, compressor and filter is on it. Which is how somebody sets a gate threshold
///         without a second machine and a friend.
///     </para>
///     <para>
///         <b>An empty microphone is silence and a counter, not the end of the sound.</b> Exactly as
///         <see cref="LiveSampleProvider" /> treats a late packet — somebody who has stopped talking
///         has not left, and a voice that ended on every gap would be rebuilt, with its bus and its
///         spatialisation, several times a sentence.
///     </para>
///     <para>
///         <b>It does not own the device.</b> A microphone usually outlives any one thing listening to
///         it, and the voice-chat encoder is generally reading the same device — so disposing it here
///         because a monitoring voice ended would take the session's input with it.
///     </para>
/// </remarks>
/// <param name="device">The microphone to read.</param>
public sealed class CaptureSampleProvider(IAudioCaptureDevice device) : IAudioSampleProvider {
    long delivered;
    long starved;

    /// <inheritdoc />
    public AudioFormat Format => device.Format;

    /// <inheritdoc />
    /// <remarks>Unknown, because a microphone does not end.</remarks>
    public long FrameCount => -1;

    /// <inheritdoc />
    public long Position => Interlocked.Read(ref delivered);

    /// <inheritdoc />
    public bool IsLooping => false;

    /// <summary>How many frames of silence were produced because the microphone had nothing.</summary>
    /// <remarks>
    ///     Rising steadily is not a fault — a microphone at 48 kHz and a mixer at 48 kHz drift, and
    ///     the drift lands here. Rising fast means the device stopped.
    /// </remarks>
    public long Starved => Interlocked.Read(ref starved);

    /// <inheritdoc />
    public int Read(Span<float> destination, int frameCount) {
        var channels = device.Format.Channels;
        var wanted = Math.Min(frameCount, destination.Length / channels);

        if (wanted <= 0) {
            return 0;
        }

        var taken = device.Read(destination, wanted);

        if (taken < wanted) {
            destination.Slice(taken * channels, (wanted - taken) * channels).Clear();
            Interlocked.Add(ref starved, wanted - taken);
        }

        // The full count either way, so the voice keeps running: what was not captured is silence
        // that has been delivered, not audio still to come.
        Interlocked.Add(ref delivered, wanted);
        return wanted;
    }

    /// <inheritdoc />
    /// <remarks>Ignored. There is nowhere to seek to in something that has not happened yet.</remarks>
    public void Seek(long frame) { }
}

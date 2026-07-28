// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;

namespace Vixen.Audio.Diagnostics;

/// <summary>What the audio engine logs, with the ids from docs/manual/log-events.md.</summary>
/// <remarks>
///     Nothing here is called from the audio thread. A log call takes locks, formats strings and may
///     write to a file, and an audio callback that did any of those would drop out — so the render
///     path counts, and <c>AudioEngine.Update</c> reports what the counters say once a frame.
/// </remarks>
static partial class AudioLog {
    [LoggerMessage(
        EventId = 9100,
        Level = LogLevel.Information,
        Message = "Audio on {Backend}: {Device}, {SampleRate} Hz, {Channels} ch, {BufferFrames}-frame blocks."
    )]
    public static partial void DeviceOpened(
        ILogger logger,
        string backend,
        string device,
        int sampleRate,
        int channels,
        int bufferFrames
    );

    [LoggerMessage(
        EventId = 9101,
        Level = LogLevel.Warning,
        Message = "No audio device on {Backend} ({Reason}) — the mixer is running against nothing. Voices "
            + "still start and finish, so gameplay keyed off them behaves; nobody hears anything."
    )]
    public static partial void DeviceUnavailable(ILogger logger, string backend, string reason);

    [LoggerMessage(
        EventId = 9102,
        Level = LogLevel.Warning,
        Message = "{Dropped} play requests were dropped: all {Capacity} voices were busy. Either sounds are "
            + "being started and never finishing, or the scene wants a bigger pool."
    )]
    public static partial void VoicePoolExhausted(ILogger logger, long dropped, int capacity);

    [LoggerMessage(
        EventId = 9103,
        Level = LogLevel.Warning,
        Message = "Audio streaming fell behind {Underruns} times — a track played silence while its decoder "
            + "caught up. The pump is not getting scheduled, or the source is slower than real time."
    )]
    public static partial void StreamUnderrun(ILogger logger, long underruns);

    [LoggerMessage(
        EventId = 9104,
        Level = LogLevel.Warning,
        Message = "The audio device reported {Underruns} underruns. The render is not finishing inside its "
            + "block; check the mixer load before blaming the driver."
    )]
    public static partial void DeviceUnderrun(ILogger logger, long underruns);

    [LoggerMessage(
        EventId = 9105,
        Level = LogLevel.Error,
        Message = "The audio render threw and the block was silenced. The engine keeps running; an exception "
            + "escaping onto a driver's callback thread would take the process with it."
    )]
    public static partial void RenderFailed(ILogger logger, Exception exception);
}

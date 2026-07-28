// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Diagnostics;

/// <summary>What the audio subsystem was doing, as of the last <c>AudioEngine.Update</c>.</summary>
/// <remarks>
///     The numbers behind <c>docs/plan/13</c>'s audio overlay — active voices and mixer levels — plus
///     the two that say whether audio is keeping up at all. Taken as one value rather than read a
///     property at a time, so an overlay never shows a voice count from this frame beside a load
///     from the last one.
/// </remarks>
public readonly record struct AudioStatistics {
    /// <summary>How many voices were producing sound in the last block.</summary>
    public int ActiveVoices { get; init; }

    /// <summary>How many there are in total.</summary>
    public int VoiceCapacity { get; init; }

    /// <summary>The loudest sample the master bus produced. Above one is clipping.</summary>
    public float MasterPeak { get; init; }

    /// <summary>How many frames have been rendered since the engine started.</summary>
    public long RenderedFrames { get; init; }

    /// <summary>How long the last block took to render.</summary>
    public TimeSpan LastRenderTime { get; init; }

    /// <summary>
    ///     What fraction of the time it had the last block took. Above one means the device did not
    ///     get its samples in time.
    /// </summary>
    /// <remarks>
    ///     <b>The number to watch.</b> Everything else is a symptom of this being close to one:
    ///     rendering a ten-millisecond block has ten milliseconds to happen in, and a mixer at 0.8
    ///     will start dropping out the first time the operating system schedules something else.
    /// </remarks>
    public double Load { get; init; }

    /// <summary>How many times the device wanted frames that were not ready.</summary>
    public long DeviceUnderruns { get; init; }

    /// <summary>How many times a streaming voice was starved by its decoder.</summary>
    public long StreamUnderruns { get; init; }

    /// <summary>How many streams the pump is servicing.</summary>
    public int StreamCount { get; init; }

    /// <summary>How many play requests found every voice busy and nothing worth displacing.</summary>
    /// <remarks>
    ///     A request is only dropped when every voice in the pool outranks it. Anything else steals,
    ///     so a non-zero count here means either a leak — sounds started and never finishing — or a
    ///     scene whose priorities say everything is important, which is the same as saying nothing is.
    /// </remarks>
    public long DroppedRequests { get; init; }

    /// <summary>How many sounds have been displaced to make room for a newer one.</summary>
    /// <remarks>
    ///     Steady growth is normal in a busy scene and is the pool doing its job. Growth in a quiet
    ///     one means the pool is smaller than the scene needs, and the cheapest fix is a bigger pool
    ///     rather than more careful priorities.
    /// </remarks>
    public long StolenVoices { get; init; }
}

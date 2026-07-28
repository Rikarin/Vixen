// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Music;

/// <summary>Where the music is, counted in samples the device actually produced.</summary>
/// <remarks>
///     <para>
///         <b>A window onto the device's own clock, not a clock of its own.</b> Everything musical is
///         a position on <c>AudioEngine.RenderedFrames</c>: the transport does not tick, it reads.
///         A separate timer — a stopwatch, a frame accumulator — would drift against the samples
///         being played, and a bar line computed from a drifting clock is a bar line in the wrong
///         place. There is exactly one clock and the hardware owns it.
///     </para>
///     <para>
///         <b>The origin moves; the clock does not.</b> Starting a segment sets an origin, and
///         position is measured from there — so "bar three" means bar three of this segment rather
///         than of the session, and a transport that has been running for an hour has the same
///         arithmetic in it as one that has just started.
///     </para>
/// </remarks>
/// <param name="sampleRate">The device's rate. Every frame count here is in its terms.</param>
public sealed class MusicTransport(int sampleRate) {
    /// <summary>The device's rate.</summary>
    public int SampleRate { get; } = sampleRate;

    /// <summary>The device frame the current segment began at.</summary>
    public long Origin { get; private set; }

    /// <summary>How fast the music is, and how it is counted.</summary>
    public MusicTempo Tempo { get; set; } = new();

    /// <summary>Puts the origin at a frame, so positions are measured from there.</summary>
    /// <param name="frame">The device frame the segment starts at.</param>
    /// <param name="tempo">Its tempo.</param>
    public void Start(long frame, in MusicTempo tempo) {
        Origin = frame;
        Tempo = tempo;
    }

    /// <summary>How far into the segment a device frame is, in frames.</summary>
    /// <param name="frame">The device frame.</param>
    /// <returns>The offset from the origin. Negative before the segment starts.</returns>
    public long PositionAt(long frame) => frame - Origin;

    /// <summary>Which beat a device frame falls on, counting from zero at the origin.</summary>
    /// <param name="frame">The device frame.</param>
    /// <returns>The beat.</returns>
    public long BeatAt(long frame) {
        var perBeat = Tempo.FramesPerBeat(SampleRate);
        return perBeat > 0 ? FloorDivide(PositionAt(frame), perBeat) : 0;
    }

    /// <summary>Which bar a device frame falls in, counting from zero at the origin.</summary>
    /// <param name="frame">The device frame.</param>
    /// <returns>The bar.</returns>
    public long BarAt(long frame) {
        var perBar = Tempo.FramesPerBar(SampleRate);
        return perBar > 0 ? FloorDivide(PositionAt(frame), perBar) : 0;
    }

    /// <summary>The first frame at or after a given one that a change is allowed to land on.</summary>
    /// <param name="frame">The frame the request arrived at.</param>
    /// <param name="quantize">What it must land on.</param>
    /// <param name="segmentFrames">How long the current segment is, for <see cref="MusicQuantize.Segment" />.</param>
    /// <returns>The frame to schedule for.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>At or after, and "at" matters.</b> A request that arrives exactly on a bar line
    ///         should land on that bar line rather than waiting for the next one — otherwise a
    ///         transition asked for on the beat is a whole bar late, which is the one case anybody
    ///         will actually notice because it is the case where they timed it.
    ///     </para>
    ///     <para>
    ///         A segment with no length falls back to the bar, because "when this ends" has no answer
    ///         for something that does not.
    ///     </para>
    /// </remarks>
    public long NextBoundary(long frame, MusicQuantize quantize, long segmentFrames = 0) {
        switch (quantize) {
            case MusicQuantize.Beat: {
                var perBeat = Tempo.FramesPerBeat(SampleRate);
                return perBeat > 0 ? Align(frame, perBeat) : frame;
            }

            case MusicQuantize.Bar: {
                var perBar = Tempo.FramesPerBar(SampleRate);
                return perBar > 0 ? Align(frame, perBar) : frame;
            }

            case MusicQuantize.Segment: {
                if (segmentFrames <= 0) {
                    goto case MusicQuantize.Bar;
                }

                var end = Origin + segmentFrames;
                return end >= frame ? end : Align(frame, segmentFrames);
            }

            default:
                return frame;
        }
    }

    /// <summary>The first multiple of a grid, measured from the origin, at or after a frame.</summary>
    long Align(long frame, long grid) {
        var offset = frame - Origin;
        var steps = FloorDivide(offset, grid);

        if (steps * grid < offset) {
            steps++;
        }

        return Origin + (steps * grid);
    }

    /// <summary>Integer division that rounds towards negative infinity rather than towards zero.</summary>
    /// <remarks>
    ///     C#'s <c>/</c> truncates, so −1 / 4 is 0 and beat −1 would be reported as beat 0. Positions
    ///     before the origin are ordinary — a segment scheduled a bar ahead is queried for a frame
    ///     before it starts on every frame until it does.
    /// </remarks>
    static long FloorDivide(long value, long divisor) {
        var quotient = value / divisor;
        return value % divisor != 0 && (value < 0) != (divisor < 0) ? quotient - 1 : quotient;
    }
}

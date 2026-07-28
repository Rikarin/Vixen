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

    /// <summary>How its frames, beats and bars relate, across however many tempi it has.</summary>
    public MusicTempoMap Map { get; private set; } = new(new MusicTempo(), [], sampleRate);

    /// <summary>The tempo it started at. A segment that changes tempo has more than this one.</summary>
    public MusicTempo Tempo => Map.Tempo;

    /// <summary>Puts the origin at a frame, so positions are measured from there.</summary>
    /// <param name="frame">The device frame the segment starts at.</param>
    /// <param name="map">How its beats and bars are laid out.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map" /> is null.</exception>
    public void Start(long frame, MusicTempoMap map) {
        ArgumentNullException.ThrowIfNull(map);
        Origin = frame;
        Map = map;
    }

    /// <summary>Puts the origin at a frame, at one unchanging tempo.</summary>
    /// <param name="frame">The device frame the segment starts at.</param>
    /// <param name="tempo">Its tempo.</param>
    public void Start(long frame, in MusicTempo tempo) => Start(frame, new MusicTempoMap(tempo, [], SampleRate));

    /// <summary>How far into the segment a device frame is, in frames.</summary>
    /// <param name="frame">The device frame.</param>
    /// <returns>The offset from the origin. Negative before the segment starts.</returns>
    public long PositionAt(long frame) => frame - Origin;

    /// <summary>Which beat a device frame falls on, counting from zero at the origin.</summary>
    /// <param name="frame">The device frame.</param>
    /// <returns>The beat.</returns>
    public long BeatAt(long frame) => Map.BeatAt(PositionAt(frame));

    /// <summary>Which bar a device frame falls in, counting from zero at the origin.</summary>
    /// <param name="frame">The device frame.</param>
    /// <returns>The bar.</returns>
    public long BarAt(long frame) => Map.BarAt(PositionAt(frame));

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
            case MusicQuantize.Beat:
                return Origin + Map.NextBeat(PositionAt(frame));

            case MusicQuantize.Bar:
                return Origin + Map.NextBar(PositionAt(frame));

            case MusicQuantize.Segment: {
                if (segmentFrames <= 0) {
                    goto case MusicQuantize.Bar;
                }

                var end = Origin + segmentFrames;

                if (end >= frame) {
                    return end;
                }

                // Past its first pass, so the next whole one — a looping segment has as many ends as
                // it has times round.
                var offset = frame - Origin;
                var steps = MusicTempoMap.FloorDivide(offset, segmentFrames);

                if (steps * segmentFrames < offset) {
                    steps++;
                }

                return Origin + (steps * segmentFrames);
            }

            default:
                return frame;
        }
    }
}

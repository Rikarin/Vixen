// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Music;

/// <summary>A change of tempo or metre part way through a segment.</summary>
/// <param name="Beat">Where it happens, in beats from the segment's start.</param>
/// <param name="Tempo">What it changes to.</param>
/// <remarks>
///     <b>A change starts a new bar</b>, which is what notation does and the only rule that makes
///     "the next bar line" answerable across one. A change written half way through a bar truncates
///     that bar, and the bar it truncated is the composer's problem rather than something this can
///     guess its way out of.
/// </remarks>
public readonly record struct MusicTempoChange(float Beat, MusicTempo Tempo);

/// <summary>How a segment's frames, beats and bars relate, across however many tempi it has.</summary>
/// <remarks>
///     <para>
///         <b>Precomputed sections, and integer frames throughout.</b> Every change is turned into a
///         section with a start frame, a start beat and a start bar worked out once; after that,
///         asking where the next bar line is means finding a section and doing integer arithmetic
///         inside it. Walking the changes on every query would be the same answer more slowly, and
///         doing it in seconds would drift — a bar at 128 in four is 90 000 frames at 48 kHz exactly
///         and 1.8749999 seconds inexactly.
///     </para>
///     <para>
///         <b>One section is the ordinary case and costs nothing extra.</b> Most game music is one
///         tempo, which is why this was a single value for as long as it was; a map with no changes
///         behaves identically and is one array of one.
///     </para>
///     <para>
///         Immutable once built, and built once per segment rather than per frame.
///     </para>
/// </remarks>
public sealed class MusicTempoMap {
    readonly struct Section {
        public long StartFrame { get; init; }
        public long StartBeat { get; init; }
        public long StartBar { get; init; }
        public long FramesPerBeat { get; init; }
        public int BeatsPerBar { get; init; }
        public MusicTempo Tempo { get; init; }

        public long FramesPerBar => FramesPerBeat * BeatsPerBar;
    }

    readonly Section[] sections;

    /// <summary>The device rate every frame count here is in terms of.</summary>
    public int SampleRate { get; }

    /// <summary>The tempo it starts at.</summary>
    public MusicTempo Tempo => sections[0].Tempo;

    /// <summary>How many tempi it has.</summary>
    public int SectionCount => sections.Length;

    /// <summary>A map over a segment.</summary>
    /// <param name="tempo">What it starts at.</param>
    /// <param name="changes">What it changes to, and where. Sorted here; anything at or before zero is ignored.</param>
    /// <param name="sampleRate">The device's rate.</param>
    public MusicTempoMap(in MusicTempo tempo, ReadOnlySpan<MusicTempoChange> changes, int sampleRate) {
        SampleRate = sampleRate;

        var ordered = changes.ToArray();
        Array.Sort(ordered, static (a, b) => a.Beat.CompareTo(b.Beat));

        var built = new List<Section>(ordered.Length + 1) {
            new() {
                StartFrame = 0,
                StartBeat = 0,
                StartBar = 0,
                FramesPerBeat = Math.Max(tempo.FramesPerBeat(sampleRate), 1),
                BeatsPerBar = Math.Max(tempo.BeatsPerBar, 1),
                Tempo = tempo
            }
        };

        foreach (var change in ordered) {
            var previous = built[^1];
            var beat = (long)MathF.Round(change.Beat);

            // At or before the section it would follow is not a change, it is a mistake — and the
            // reading that loses the least is to keep what is already there.
            if (beat <= previous.StartBeat) {
                continue;
            }

            var elapsed = beat - previous.StartBeat;

            built.Add(new Section {
                StartFrame = previous.StartFrame + (elapsed * previous.FramesPerBeat),
                StartBeat = beat,

                // Rounded up, because a change starts a new bar: a change three and a half bars in
                // ends that bar early and begins the fourth.
                StartBar = previous.StartBar + ((elapsed + previous.BeatsPerBar - 1) / previous.BeatsPerBar),
                FramesPerBeat = Math.Max(change.Tempo.FramesPerBeat(sampleRate), 1),
                BeatsPerBar = Math.Max(change.Tempo.BeatsPerBar, 1),
                Tempo = change.Tempo
            });
        }

        sections = [.. built];
    }

    /// <summary>Which beat an offset into the segment falls on.</summary>
    /// <param name="offset">Frames from the segment's start. Negative counts backwards.</param>
    /// <returns>The beat.</returns>
    public long BeatAt(long offset) {
        var section = SectionAt(offset);
        return section.StartBeat + FloorDivide(offset - section.StartFrame, section.FramesPerBeat);
    }

    /// <summary>Which bar an offset into the segment falls in.</summary>
    /// <param name="offset">Frames from the segment's start.</param>
    /// <returns>The bar.</returns>
    public long BarAt(long offset) {
        var section = SectionAt(offset);
        return section.StartBar + FloorDivide(offset - section.StartFrame, section.FramesPerBar);
    }

    /// <summary>Where a beat is, in frames from the segment's start.</summary>
    /// <param name="beat">Which beat.</param>
    /// <returns>The offset.</returns>
    public long FrameAtBeat(long beat) {
        var section = sections[0];

        foreach (var candidate in sections) {
            if (candidate.StartBeat > beat) {
                break;
            }

            section = candidate;
        }

        return section.StartFrame + ((beat - section.StartBeat) * section.FramesPerBeat);
    }

    /// <summary>The first beat line at or after an offset.</summary>
    /// <param name="offset">Frames from the segment's start.</param>
    /// <returns>The offset of that line.</returns>
    public long NextBeat(long offset) => NextGrid(offset, bars: false);

    /// <summary>The first bar line at or after an offset.</summary>
    /// <param name="offset">Frames from the segment's start.</param>
    /// <returns>The offset of that line.</returns>
    public long NextBar(long offset) => NextGrid(offset, bars: true);

    /// <summary>The first line of a grid at or after an offset, respecting where the tempo changes.</summary>
    /// <remarks>
    ///     A line that would fall past the end of its section does not exist: the section ended, and
    ///     a change starts both a beat and a bar, so the answer is where the next section begins. That
    ///     is the whole of what a tempo map adds to this arithmetic.
    /// </remarks>
    long NextGrid(long offset, bool bars) {
        for (var i = 0; i < sections.Length; i++) {
            var section = sections[i];
            var end = i + 1 < sections.Length ? sections[i + 1].StartFrame : long.MaxValue;

            if (offset > end) {
                continue;
            }

            var grid = bars ? section.FramesPerBar : section.FramesPerBeat;
            var within = offset - section.StartFrame;
            var steps = FloorDivide(within, grid);

            if (steps * grid < within) {
                steps++;
            }

            var candidate = section.StartFrame + (steps * grid);

            if (candidate <= end) {
                return candidate;
            }
        }

        return offset;
    }

    Section SectionAt(long offset) {
        var found = sections[0];

        foreach (var section in sections) {
            if (section.StartFrame > offset) {
                break;
            }

            found = section;
        }

        return found;
    }

    /// <summary>Integer division that rounds towards negative infinity rather than towards zero.</summary>
    /// <remarks>
    ///     Positions before the start are ordinary: a segment scheduled a bar ahead is asked about
    ///     frames before it begins on every frame until it does, and beat −1 must not be reported as
    ///     beat 0.
    /// </remarks>
    internal static long FloorDivide(long value, long divisor) {
        var quotient = value / divisor;
        return value % divisor != 0 && (value < 0) != (divisor < 0) ? quotient - 1 : quotient;
    }
}

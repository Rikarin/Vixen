// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Parameters;

/// <summary>What a curve drives on the sound it is attached to.</summary>
/// <remarks>
///     <b>Four, and all of them per voice.</b> These are the things a single playing sound owns —
///     which is what makes a parameter per-instance and therefore what makes "this player is
///     underwater and that one is not" expressible at all. Bus gains, sends and effect properties are
///     shared by everything routed through them, so they are driven by
///     <c>AudioBusParameterTarget</c> from an engine-wide parameter instead.
/// </remarks>
public enum AudioParameterTarget {
    /// <summary>A level, in decibels, added to the sound's own.</summary>
    GainDb = 0,

    /// <summary>A transposition, in semitones, on top of the sound's own pitch.</summary>
    PitchSemitones = 1,

    /// <summary>A low-pass cutoff in hertz. The muffling one.</summary>
    /// <remarks>
    ///     Zero and anything at or above Nyquist mean no filter. Where two parameters both ask for
    ///     one, the <em>lowest</em> wins — two things muffling a sound do not multiply, and summing
    ///     hertz is meaningless.
    /// </remarks>
    LowPassHz = 2,

    /// <summary>A high-pass cutoff in hertz — thinning rather than muffling.</summary>
    /// <remarks>The mirror of <see cref="LowPassHz" />: the highest of the asks wins. A telephone is both.</remarks>
    HighPassHz = 3
}

/// <summary>One mapping from a parameter's range onto something audible.</summary>
/// <param name="Target">What it drives.</param>
/// <param name="Curve">How the parameter's range maps onto that target's unit.</param>
public sealed record AudioAutomation(AudioParameterTarget Target, AudioCurve Curve);

/// <summary>Something the engine already knows, offered as a parameter.</summary>
/// <remarks>
///     <para>
///         <b>The point is that gameplay does not set these.</b> The spatialiser works all four out
///         every block anyway; a parameter marked with one of them has its value written by the engine
///         each frame, so a designer can draw a curve against distance without a programmer plumbing
///         distance anywhere. Setting one by hand is refused rather than ignored.
///     </para>
///     <para>
///         They are ordinary parameters otherwise — the range, the curves and the seek time all mean
///         what they always did, and a sheet may mix built-in and gameplay-driven ones freely.
///     </para>
/// </remarks>
public enum AudioBuiltinParameter {
    /// <summary>Not one. The value is whatever gameplay last set.</summary>
    None = 0,

    /// <summary>How far the sound is from the listener, in world units.</summary>
    /// <remarks>
    ///     The one most curves want. A range of 0 to whatever the sound carries, and a low-pass
    ///     closing across it, is a better distance filter than the air-absorption model because the
    ///     shape is drawn rather than derived.
    /// </remarks>
    Distance = 1,

    /// <summary>Which way round the listener it is, in degrees: 0 ahead, +90 right, ±180 behind.</summary>
    /// <remarks>What a front-back filter is drawn against, which is the cheapest thing that sounds like an HRTF.</remarks>
    Direction = 2,

    /// <summary>How far above or below the listener it is, in degrees: +90 overhead, −90 underfoot.</summary>
    Elevation = 3,

    /// <summary>How fast the source is moving, in units a second.</summary>
    /// <remarks>An engine that opens up as it accelerates, without the vehicle code knowing what a bus is.</remarks>
    Speed = 4,

    /// <summary>How much solid geometry is in the way: 0 for a clear path, 1 for a blocked one.</summary>
    /// <remarks>
    ///     <para>
    ///         The only built-in that is not free. The other four fall out of arithmetic the
    ///         spatialiser was doing anyway; this one is a raycast, so it produces nothing at all
    ///         until something is given to <c>AudioEngine.OcclusionProvider</c>. A curve drawn
    ///         against it on a game with no provider sits at zero, which is "nothing in the way" —
    ///         the right answer to have when nobody can say otherwise.
    ///     </para>
    ///     <para>
    ///         <b>What it should be drawn onto is a cutoff more than a level.</b> A wall does not
    ///         make a sound quieter so much as it makes it dull: the low frequencies go through and
    ///         the high ones do not. A curve that only pulls the gain down sounds like the source
    ///         moved away rather than like something got between.
    ///     </para>
    /// </remarks>
    Occlusion = 5
}

/// <summary>A named value a sound reads, and what moving it does.</summary>
/// <remarks>
///     <para>
///         The indirection that separates a sound designer from a programmer. Gameplay writes
///         <c>submersion = 0.8</c> and knows nothing else; the parameter's curves decide that this
///         means a low-pass closing to 500 Hz, three decibels down, and a semitone flat. Changing any
///         of that is an asset edit.
///     </para>
///     <para>
///         <b><see cref="SeekSeconds" /> is what stops it clicking.</b> A parameter driven by a
///         gameplay boolean jumps from 0 to 1 in one frame, and a filter cutoff that jumps two octaves
///         is a click. The value moves towards what was asked for at a limited rate, so the thing
///         gameplay sets is a target rather than a position.
///     </para>
/// </remarks>
public sealed record AudioParameterDefinition {
    /// <summary>What gameplay calls it.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The bottom of its range, which maps to position 0 on every curve.</summary>
    public float Minimum { get; init; }

    /// <summary>The top of its range, which maps to position 1.</summary>
    public float Maximum { get; init; } = 1f;

    /// <summary>Where it sits before anything sets it.</summary>
    public float Default { get; init; }

    /// <summary>How long it takes to cross its whole range. Zero arrives at once.</summary>
    /// <remarks>
    ///     A rate and not a duration, so a small change is quick and a large one is not — which is
    ///     what "the filter closes as you go under" should do, and what a fixed duration would get
    ///     backwards for every change smaller than the whole range.
    /// </remarks>
    public float SeekSeconds { get; init; }

    /// <summary>Something the engine works out, instead of something gameplay sets.</summary>
    public AudioBuiltinParameter Builtin { get; init; }

    /// <summary>What moving it does.</summary>
    public AudioAutomation[] Automation { get; init; } = [];
}

/// <summary>Everything the automation of one set of parameters worked out, in one value.</summary>
/// <param name="GainDb">A level, in decibels, to add.</param>
/// <param name="PitchSemitones">A transposition to add.</param>
/// <param name="LowPassHz">A cutoff, or zero for no filter.</param>
/// <param name="HighPassHz">A cutoff, or zero for no filter.</param>
public readonly record struct AudioParameterResult(
    float GainDb,
    float PitchSemitones,
    float LowPassHz,
    float HighPassHz
) {
    /// <summary>Nothing changed: unity gain, unaltered pitch, no filters.</summary>
    public static AudioParameterResult None => new(0f, 0f, 0f, 0f);
}

/// <summary>A set of parameters, and the automation that turns their values into four numbers.</summary>
/// <remarks>
///     <para>
///         <b>Shared and immutable.</b> One sheet describes every instance of a sound; the
///         <em>values</em> are per voice and live in the engine. So ten players talking through one
///         underwater description is one sheet and ten sets of floats, rather than ten copies of a
///         curve.
///     </para>
///     <para>
///         <b>Evaluated on the game thread, once a frame.</b> <c>AudioEngine.Update</c> steps each
///         voice's values towards their targets, calls <see cref="Evaluate" />, and writes the four
///         results onto the voice as plain floats — which the audio thread was already reading. No
///         curve, no name lookup and no allocation goes anywhere near a device callback.
///     </para>
/// </remarks>
public sealed class AudioParameterSheet {
    /// <summary>The most parameters one sound may have.</summary>
    /// <remarks>
    ///     A fixed cap because the values are a flat array in the engine, sized once for the whole
    ///     voice pool. Eight is well past what a sound has ever needed — a typical one has two — and
    ///     the whole table is a couple of kilobytes.
    /// </remarks>
    public const int MaxParameters = 8;

    readonly AudioParameterDefinition[] parameters;
    readonly float[] inverseRanges;

    /// <summary>How many parameters it has.</summary>
    public int Count => parameters.Length;

    /// <summary>One of them.</summary>
    /// <param name="index">Which.</param>
    public AudioParameterDefinition this[int index] => parameters[index];

    /// <summary>A sheet over some parameters.</summary>
    /// <param name="parameters">Them. Anything past <see cref="MaxParameters" /> is dropped.</param>
    /// <exception cref="ArgumentNullException"><paramref name="parameters" /> is null.</exception>
    public AudioParameterSheet(IReadOnlyList<AudioParameterDefinition> parameters) {
        ArgumentNullException.ThrowIfNull(parameters);

        var kept = Math.Min(parameters.Count, MaxParameters);
        this.parameters = new AudioParameterDefinition[kept];
        inverseRanges = new float[kept];

        for (var i = 0; i < kept; i++) {
            var parameter = parameters[i];
            this.parameters[i] = parameter;
            var range = parameter.Maximum - parameter.Minimum;

            // A zero-width range would divide by zero and a reversed one would run the curve
            // backwards; both are content mistakes, and pinning the position to 0 is the reading that
            // leaves the sound at the start of its curve rather than at an arbitrary point on it.
            inverseRanges[i] = range > 0f ? 1f / range : 0f;
            HasBuiltins |= parameter.Builtin is not AudioBuiltinParameter.None;
        }
    }

    /// <summary>Whether any of its parameters is one the engine fills in.</summary>
    /// <remarks>Checked once so that the per-frame pass can skip a sheet that has none.</remarks>
    public bool HasBuiltins { get; }

    /// <summary>Finds a parameter by name.</summary>
    /// <param name="name">What gameplay calls it.</param>
    /// <returns>Its index, or −1.</returns>
    /// <remarks>
    ///     Linear and ordinal. A caller setting a parameter every frame should hold the index — this is
    ///     for the call that resolves it once.
    /// </remarks>
    public int IndexOf(string name) {
        for (var i = 0; i < parameters.Length; i++) {
            if (string.Equals(parameters[i].Name, name, StringComparison.Ordinal)) {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Where a value sits in a parameter's range, from 0 to 1.</summary>
    /// <param name="index">Which parameter.</param>
    /// <param name="value">The value.</param>
    /// <returns>Its position.</returns>
    public float Normalize(int index, float value) =>
        Math.Clamp((value - parameters[index].Minimum) * inverseRanges[index], 0f, 1f);

    /// <summary>Runs every curve and combines what they asked for.</summary>
    /// <param name="values">One current value per parameter, in order.</param>
    /// <returns>The four numbers a voice reads.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Gains and pitches add; cutoffs take the extreme.</b> Decibels and semitones are
    ///         already logarithmic, so adding them is multiplying the thing they describe — two
    ///         parameters each asking for −6 dB give −12, which is what anybody would expect. Hertz are
    ///         not: a sound that is both underwater and behind a door is muffled by whichever is
    ///         muffling it more, and there is no sense in which the two cutoffs combine.
    ///     </para>
    ///     <para>
    ///         A target no curve mentions is left neutral, so a sheet only pays for what it drives.
    ///     </para>
    /// </remarks>
    public AudioParameterResult Evaluate(ReadOnlySpan<float> values) {
        var gainDb = 0f;
        var semitones = 0f;
        var lowPass = 0f;
        var highPass = 0f;

        for (var i = 0; i < parameters.Length && i < values.Length; i++) {
            var automation = parameters[i].Automation;

            if (automation.Length == 0) {
                continue;
            }

            var position = Normalize(i, values[i]);

            foreach (var entry in automation) {
                var value = entry.Curve.Evaluate(position);

                switch (entry.Target) {
                    case AudioParameterTarget.GainDb:
                        gainDb += value;
                        break;

                    case AudioParameterTarget.PitchSemitones:
                        semitones += value;
                        break;

                    case AudioParameterTarget.LowPassHz:
                        lowPass = lowPass <= 0f ? value : MathF.Min(lowPass, value);
                        break;

                    case AudioParameterTarget.HighPassHz:
                        highPass = MathF.Max(highPass, value);
                        break;

                    default:
                        break;
                }
            }
        }

        return new(gainDb, semitones, MathF.Max(lowPass, 0f), MathF.Max(highPass, 0f));
    }

    /// <summary>Fills a span with every parameter's default.</summary>
    /// <param name="values">Where to write them. Anything past <see cref="Count" /> is zeroed.</param>
    public void CopyDefaultsTo(Span<float> values) {
        values.Clear();

        for (var i = 0; i < parameters.Length && i < values.Length; i++) {
            values[i] = parameters[i].Default;
        }
    }

    /// <summary>Moves a value towards a target at the rate its parameter asked for.</summary>
    /// <param name="index">Which parameter.</param>
    /// <param name="current">Where it is. Updated in place.</param>
    /// <param name="target">Where it is going.</param>
    /// <param name="deltaSeconds">How much game time has passed.</param>
    public void Seek(int index, ref float current, float target, float deltaSeconds) {
        var parameter = parameters[index];

        if (parameter.SeekSeconds <= 0f || deltaSeconds <= 0f) {
            current = target;
            return;
        }

        // Per second across the whole range, so a change of a tenth of the range takes a tenth of the
        // seek time. A fixed duration would make a small nudge as slow as a full sweep.
        var step = (parameter.Maximum - parameter.Minimum) * deltaSeconds / parameter.SeekSeconds;
        var difference = target - current;

        current = MathF.Abs(difference) <= step ? target : current + (MathF.Sign(difference) * step);
    }
}

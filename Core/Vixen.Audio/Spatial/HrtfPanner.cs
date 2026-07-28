// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Audio.Spatial;

/// <summary>Places a sound around a head, including behind it and above it.</summary>
/// <remarks>
///     <para>
///         <b>Amplitude panning has a left and a right and nothing else.</b> A sound directly behind
///         you produces the same two numbers as one directly in front, and one overhead produces the
///         same as one at ear level. Those are not subtle differences to a listener — they are the
///         difference between knowing where something is and guessing.
///     </para>
///     <para>
///         <b>This is a structural model and not a measured filter set, and the distinction
///         matters.</b> A real HRTF is a pair of impulse responses measured around somebody's actual
///         head, and it is several megabytes of content that has to be shipped and licensed. What is
///         here instead is the three physical mechanisms behind those measurements, each written down
///         as a filter: the path length difference to the two ears, the shadow the head casts, and
///         what the outer ear does to sound arriving from different directions. It is not as
///         convincing as a good measured set. It is convincing enough to tell front from back, which
///         is the thing panning cannot do at all, and it costs no content.
///     </para>
///     <para>
///         <b>Headphones only.</b> Over speakers each ear hears both channels, so the cues arrive
///         crossed and the result is worse than plain panning — which is why this is never the
///         default and why anything switching it on should be reading a headphone setting.
///     </para>
///     <para>
///         The three mechanisms, in the order the ear weighs them:
///     </para>
///     <para>
///         <b>Time.</b> Sound reaches the near ear before the far one, by up to about two thirds of a
///         millisecond. Below roughly 1.5 kHz this is the dominant cue and the brain reads the phase
///         difference directly. The delay follows Woodworth's spherical-head formula rather than a
///         straight-line difference, because sound bends around a head rather than passing through it.
///     </para>
///     <para>
///         <b>Level, and only at high frequencies.</b> A head is an obstacle roughly the size of a
///         wavelength at 2 kHz: below that sound diffracts around it almost undiminished, above it the
///         far ear is in shadow. So the shadow is a one-pole filter whose shape depends on angle, not
///         a gain — a plain level difference would be wrong at exactly the frequencies where the ear
///         is most sensitive to it being wrong.
///     </para>
///     <para>
///         <b>Shape.</b> The pinna reflects sound into the canal with a delay that depends on where it
///         came from, which puts a notch in the spectrum whose frequency moves with elevation and
///         which is largely absent from behind. That notch is how a listener knows a sound is above
///         them, and its absence is how they know it is behind.
///     </para>
/// </remarks>
public sealed class HrtfPanner {
    /// <summary>The radius of the head this is modelled on, in metres.</summary>
    /// <remarks>Roughly average. It sets both the maximum delay and where the shadow starts.</remarks>
    public const float HeadRadius = 0.0875f;

    /// <summary>How fast sound travels, in metres a second.</summary>
    public const float SpeedOfSound = 343f;

    // Two thirds of a millisecond at the widest, plus room for the fractional part.
    const int MaxDelaySamples = 64;

    readonly float[] delayLine;
    readonly int sampleRate;

    int writeCursor;

    // The head-shadow filter, one per ear: a one-pole one-zero whose zero moves with the angle.
    float leftShadowZ;
    float rightShadowZ;
    float leftShadowIn;
    float rightShadowIn;

    // The pinna notch, one biquad per ear.
    float leftNotchX1, leftNotchX2, leftNotchY1, leftNotchY2;
    float rightNotchX1, rightNotchX2, rightNotchY1, rightNotchY2;

    /// <summary>A panner for one voice at one sample rate.</summary>
    /// <param name="rate">The device's rate.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rate is not positive.</exception>
    public HrtfPanner(int rate) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        sampleRate = rate;
        delayLine = new float[MaxDelaySamples * 2];
    }

    /// <summary>Turns one mono sample into a left and a right one.</summary>
    /// <param name="sample">The sample.</param>
    /// <param name="azimuth">Where it is, in degrees: 0 ahead, +90 right, ±180 behind.</param>
    /// <param name="elevation">How high, in degrees: +90 overhead, −90 underfoot.</param>
    /// <param name="left">The left ear's sample.</param>
    /// <param name="right">The right ear's.</param>
    public void Process(float sample, float azimuth, float elevation, out float left, out float right) {
        delayLine[writeCursor] = sample;
        delayLine[writeCursor + MaxDelaySamples] = sample;

        var radians = azimuth * MathF.PI / 180f;

        // Woodworth: the extra distance to the far ear is a·(θ + sin θ) rather than a·sin θ, because
        // the sound travels around the head and not through it. The difference is a third at the
        // sides, which is the difference between an image that sits where it should and one that
        // collapses towards the near ear.
        var seconds = HeadRadius / SpeedOfSound * (MathF.Abs(radians) + MathF.Sin(MathF.Abs(radians)));
        var samples = MathF.Min(seconds * sampleRate, MaxDelaySamples - 2);

        // The far ear is the one the sound has to go around.
        var leftDelay = azimuth > 0f ? samples : 0f;
        var rightDelay = azimuth > 0f ? 0f : samples;

        var nearLeft = Read(0, leftDelay);
        var nearRight = Read(MaxDelaySamples, rightDelay);

        writeCursor = (writeCursor + 1) % MaxDelaySamples;

        // Each ear's own angle of incidence: the left ear faces −90°, the right faces +90°.
        left = Notch(
            Shadow(nearLeft, azimuth + 90f, ref leftShadowZ, ref leftShadowIn),
            elevation,
            azimuth,
            ref leftNotchX1,
            ref leftNotchX2,
            ref leftNotchY1,
            ref leftNotchY2
        );

        right = Notch(
            Shadow(nearRight, azimuth - 90f, ref rightShadowZ, ref rightShadowIn),
            elevation,
            azimuth,
            ref rightNotchX1,
            ref rightNotchX2,
            ref rightNotchY1,
            ref rightNotchY2
        );
    }

    /// <summary>Reads the delay line a fractional number of samples back.</summary>
    float Read(int offset, float delay) {
        var whole = (int)delay;
        var fraction = delay - whole;

        var first = ((writeCursor - whole) + MaxDelaySamples) % MaxDelaySamples;
        var second = ((first - 1) + MaxDelaySamples) % MaxDelaySamples;

        // Linear between the two, which at these delays is plenty: the cue is an arrival time, and an
        // error of a hundredth of a sample is an error of a fifth of a degree.
        return (delayLine[offset + first] * (1f - fraction)) + (delayLine[offset + second] * fraction);
    }

    /// <summary>The head's shadow at one ear, as a one-pole one-zero whose zero moves with the angle.</summary>
    /// <remarks>
    ///     Brown and Duda's model. The pole sits at the frequency whose wavelength is the head's
    ///     circumference — below it sound diffracts around and arrives almost undiminished; above it
    ///     the far ear is genuinely in shadow. <paramref name="incidence" /> is 0 when the sound is
    ///     straight at this ear and 180 when it is at the other.
    /// </remarks>
    float Shadow(float sample, float incidence, ref float state, ref float previous) {
        var angle = MathF.Abs(Wrap(incidence));

        // 1.05 facing the ear down to 0.1 in full shadow — the numbers are the model's, fitted to
        // measurements of a sphere.
        var alpha = 1.05f + (0.95f * MathF.Cos(angle / 180f * 150f * MathF.PI / 180f));

        // H(s) = (1 + α·s/2ω₀) / (1 + s/2ω₀), bilinear-transformed. Its gain is 1 at DC and α at
        // Nyquist, and that direction is the entire model: a head is transparent to a wavelength much
        // longer than it is, and opaque to a short one. Getting the two ends the wrong way round
        // produces a filter that shadows the bass and passes the treble, which sounds like a mistake
        // nobody can name and reads on a meter as the far ear being *louder*.
        var beta = sampleRate * HeadRadius / SpeedOfSound;
        var denominator = 1f + beta;

        var b0 = (1f + (alpha * beta)) / denominator;
        var b1 = (1f - (alpha * beta)) / denominator;
        var a1 = (1f - beta) / denominator;

        var output = (b0 * sample) + (b1 * previous) - (a1 * state);
        previous = sample;
        state = output;
        return output;
    }

    /// <summary>What the outer ear does, which is the only cue for above and behind.</summary>
    /// <remarks>
    ///     A notch whose frequency rises with elevation, plus a dulling from behind. The pinna faces
    ///     forward, so it reflects sound from in front into the canal — producing the notch — and
    ///     shadows sound from behind, taking the top off it. Between them they are what makes a sound
    ///     behind you sound behind you rather than in front.
    /// </remarks>
    float Notch(
        float sample,
        float elevation,
        float azimuth,
        ref float x1,
        ref float x2,
        ref float y1,
        ref float y2
    ) {
        var height = Math.Clamp(elevation, -90f, 90f);

        // Rising from about 6 kHz underfoot to 11 kHz overhead, which is the range the measurements
        // put it in and the range the ear reads as height.
        var centre = 6_000f + (5_000f * ((height + 90f) / 180f));
        var nyquist = sampleRate * 0.5f;

        if (centre >= nyquist * 0.95f) {
            return sample;
        }

        var behind = MathF.Abs(Wrap(azimuth)) > 90f;

        // Deeper from in front, where the pinna actually reflects, and shallower from behind where it
        // is in the way instead.
        var depth = behind ? 0.35f : 0.75f;
        var q = 2f;

        var omega = 2f * MathF.PI * centre / sampleRate;
        var sin = MathF.Sin(omega);
        var cos = MathF.Cos(omega);
        var alpha = sin / (2f * q);

        var b0 = 1f - (depth * alpha / (1f + alpha));
        var b1 = -2f * cos * (1f - (depth * alpha / (1f + alpha)));
        var b2 = b0;
        var a0 = 1f + alpha;
        var a1 = -2f * cos;
        var a2 = 1f - alpha;

        var output = ((b0 * sample) + (b1 * x1) + (b2 * x2) - (a1 * y1) - (a2 * y2)) / a0;

        x2 = x1;
        x1 = sample;
        y2 = y1;
        y1 = output;

        // And the shadow of the pinna itself: sound from behind loses its top, which is the cue a
        // notch alone does not give.
        return behind ? output * 0.85f : output;
    }

    /// <summary>Forgets the delay line and every filter.</summary>
    public void Reset() {
        Array.Clear(delayLine);
        writeCursor = 0;
        leftShadowZ = rightShadowZ = leftShadowIn = rightShadowIn = 0f;
        leftNotchX1 = leftNotchX2 = leftNotchY1 = leftNotchY2 = 0f;
        rightNotchX1 = rightNotchX2 = rightNotchY1 = rightNotchY2 = 0f;
    }

    /// <summary>An angle folded into ±180.</summary>
    static float Wrap(float degrees) {
        var wrapped = degrees % 360f;

        return wrapped switch {
            > 180f => wrapped - 360f,
            < -180f => wrapped + 360f,
            _ => wrapped
        };
    }
}

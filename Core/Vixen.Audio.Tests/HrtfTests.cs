// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Spatial;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>
///     Each of the three cues, measured on its own. The whole claim of an HRTF is that it produces
///     differences amplitude panning cannot, so every test here asks for a difference panning would
///     not have made.
/// </summary>
public sealed class HrtfTests {
    const int Rate = 48_000;

    /// <summary>Runs a signal through and hands back both ears.</summary>
    static (float[] Left, float[] Right) Render(float azimuth, float elevation, Func<int, float> signal, int frames = 4_096) {
        var panner = new HrtfPanner(Rate);
        var left = new float[frames];
        var right = new float[frames];

        for (var i = 0; i < frames; i++) {
            panner.Process(signal(i), azimuth, elevation, out left[i], out right[i]);
        }

        return (left, right);
    }

    static float Rms(ReadOnlySpan<float> signal, int from = 512) {
        var sum = 0.0;

        for (var i = from; i < signal.Length; i++) {
            sum += signal[i] * (double)signal[i];
        }

        return (float)Math.Sqrt(sum / (signal.Length - from));
    }

    static Func<int, float> Tone(float hertz) => i => 0.5f * MathF.Sin(2f * MathF.PI * hertz * i / Rate);

    /// <summary>Where the energy sits in the spectrum, as a single number: high against low.</summary>
    static float Brightness(ReadOnlySpan<float> signal) {
        var previous = 0f;
        var high = 0.0;
        var total = 0.0;

        for (var i = 512; i < signal.Length; i++) {
            var difference = signal[i] - previous;
            previous = signal[i];
            high += difference * (double)difference;
            total += signal[i] * (double)signal[i];
        }

        return total > 0 ? (float)(high / total) : 0f;
    }

    // ── Time ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The dominant cue below about 1.5 kHz, and the one panning does not have at all: the near
    ///     ear hears it first.
    /// </summary>
    [Fact]
    public void TheNearEarHearsItFirst() {
        // A click, so the arrival time is a thing that can be pointed at.
        var (left, right) = Render(90f, 0f, i => i == 64 ? 1f : 0f, 512);

        static int FirstMovement(ReadOnlySpan<float> signal) {
            for (var i = 0; i < signal.Length; i++) {
                if (MathF.Abs(signal[i]) > 0.01f) {
                    return i;
                }
            }

            return -1;
        }

        var toRight = FirstMovement(right);
        var toLeft = FirstMovement(left);

        Assert.True(toRight > 0 && toLeft > 0);
        Assert.True(toRight < toLeft, $"the right ear heard a sound on the right at {toRight} and the left at {toLeft}");

        // Two thirds of a millisecond at the widest, which is what a head is worth.
        var difference = (toLeft - toRight) / (float)Rate;
        Assert.True(difference is > 0.0004f and < 0.0009f, $"the delay was {difference * 1000f:F3} ms");
    }

    [Fact]
    public void StraightAheadReachesBothEarsTogether() {
        var (left, right) = Render(0f, 0f, Tone(500f));

        Assert.Equal(Rms(left), Rms(right), Rms(left) * 0.02f);
    }

    // ── Level ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheFarEarIsInShadowAndTheNearOneIsNot() {
        var (left, right) = Render(90f, 0f, Tone(6_000f));

        Assert.True(Rms(right) > Rms(left) * 1.5f, $"left {Rms(left):F4}, right {Rms(right):F4}");
    }

    /// <summary>
    ///     And the shadow is a filter rather than a gain: a head is transparent to a wavelength much
    ///     longer than it is, so a low tone reaches both ears at nearly the same level even from the
    ///     side. A model that used a plain level difference would get this wrong.
    /// </summary>
    [Fact]
    public void TheHeadIsTransparentToLowFrequenciesAndNotToHighOnes() {
        var (lowLeft, lowRight) = Render(90f, 0f, Tone(200f));
        var (highLeft, highRight) = Render(90f, 0f, Tone(8_000f));

        var lowShadow = Rms(lowRight) / Rms(lowLeft);
        var highShadow = Rms(highRight) / Rms(highLeft);

        Assert.True(highShadow > lowShadow * 1.5f, $"200 Hz shadowed by {lowShadow:F2}× and 8 kHz by {highShadow:F2}×");
    }

    // ── Shape ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The one amplitude panning cannot do at all. Directly ahead and directly behind are the
    ///     same two gains to a panner, and are two different sounds to a listener.
    /// </summary>
    [Fact]
    public void BehindSoundsDifferentFromInFront() {
        static float[] Noise(int count) {
            var random = new Random(1234);
            var samples = new float[count];

            for (var i = 0; i < count; i++) {
                samples[i] = (float)((random.NextDouble() * 2.0) - 1.0) * 0.4f;
            }

            return samples;
        }

        var noise = Noise(8_192);

        var (frontLeft, _) = Render(0f, 0f, i => noise[i], noise.Length);
        var (behindLeft, _) = Render(180f, 0f, i => noise[i], noise.Length);

        // Not merely different numbers — different in the specific way the pinna makes them
        // different: what comes from behind has less top on it.
        Assert.True(
            Brightness(behindLeft) < Brightness(frontLeft),
            $"behind was as bright as in front: {Brightness(behindLeft):F4} against {Brightness(frontLeft):F4}"
        );

        var difference = 0f;

        for (var i = 512; i < noise.Length; i++) {
            difference = MathF.Max(difference, MathF.Abs(frontLeft[i] - behindLeft[i]));
        }

        Assert.True(difference > 0.01f, "front and behind came out identical, which is what panning already does");
    }

    /// <summary>Elevation moves the pinna notch, which is the only thing that says a sound is above you.</summary>
    [Fact]
    public void AboveSoundsDifferentFromAtEarLevel() {
        var random = new Random(99);
        var noise = new float[8_192];

        for (var i = 0; i < noise.Length; i++) {
            noise[i] = (float)((random.NextDouble() * 2.0) - 1.0) * 0.4f;
        }

        var (level, _) = Render(0f, 0f, i => noise[i], noise.Length);
        var (above, _) = Render(0f, 75f, i => noise[i], noise.Length);

        var difference = 0f;

        for (var i = 512; i < noise.Length; i++) {
            difference = MathF.Max(difference, MathF.Abs(level[i] - above[i]));
        }

        Assert.True(difference > 0.005f, "overhead and at ear level came out the same");
    }

    // ── Housekeeping ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ItIsSymmetricLeftToRight() {
        var (leftOfLeft, rightOfLeft) = Render(-90f, 0f, Tone(3_000f));
        var (leftOfRight, rightOfRight) = Render(90f, 0f, Tone(3_000f));

        // A sound at 90° left should reach the left ear exactly as a sound at 90° right reaches the
        // right one. An asymmetry here is a sign error, and a sign error here puts every sound on the
        // wrong side.
        Assert.Equal(Rms(leftOfLeft), Rms(rightOfRight), Rms(leftOfLeft) * 0.02f);
        Assert.Equal(Rms(rightOfLeft), Rms(leftOfRight), Rms(leftOfRight) * 0.02f + 1e-5f);
    }

    [Fact]
    public void SilenceStaysSilent() {
        var (left, right) = Render(45f, 30f, _ => 0f, 1_024);

        Assert.Equal(0f, Rms(left, 0));
        Assert.Equal(0f, Rms(right, 0));
    }

    [Fact]
    public void ResetForgetsTheSignal() {
        var panner = new HrtfPanner(Rate);

        for (var i = 0; i < 1_000; i++) {
            panner.Process(0.8f, 60f, 0f, out _, out _);
        }

        panner.Reset();

        var loudest = 0f;

        for (var i = 0; i < 256; i++) {
            panner.Process(0f, 60f, 0f, out var left, out var right);
            loudest = MathF.Max(loudest, MathF.Max(MathF.Abs(left), MathF.Abs(right)));
        }

        Assert.Equal(0f, loudest);
    }

    [Fact]
    public void ARateItCannotWorkAtIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new HrtfPanner(0));

    /// <summary>It stays finite everywhere, which a filter whose coefficients move every sample might not.</summary>
    [Theory]
    [InlineData(-180f, -90f)]
    [InlineData(-90f, 0f)]
    [InlineData(0f, 90f)]
    [InlineData(90f, 45f)]
    [InlineData(180f, -45f)]
    [InlineData(359f, 89f)]
    public void ItIsFiniteInEveryDirection(float azimuth, float elevation) {
        var (left, right) = Render(azimuth, elevation, Tone(1_000f), 2_048);

        foreach (var sample in left) {
            Assert.True(float.IsFinite(sample) && MathF.Abs(sample) < 8f, $"left produced {sample}");
        }

        foreach (var sample in right) {
            Assert.True(float.IsFinite(sample) && MathF.Abs(sample) < 8f, $"right produced {sample}");
        }
    }

    // ── Through the engine ────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The whole point, at the level a game sees it: two sounds that amplitude panning would make
    ///     identical come out different.
    /// </summary>
    [Fact]
    public void ThroughTheEngineFrontAndBehindStopBeingTheSameSound() {
        static float[] Rendered(bool hrtf, float z) {
            var (engine, device) = AudioTestData.Engine(channels: 2);

            using (engine) {
                engine.UseHrtf = hrtf;
                engine.SetListener(new AudioListener());

                engine.Play(AudioTestData.Tone(4_000f, 48_000), new Mixing.PlaybackSettings {
                    IsSpatial = true,
                    Spatial = new SpatialSettings { Position = new Core.Mathematics.Vector3(0f, 0f, z), MaxDistance = 1_000f }
                });

                engine.Update(0f);
                return AudioTestData.Render(device, 2_048);
            }
        }

        // Amplitude panning puts both of these dead centre and cannot tell them apart.
        var pannedFront = Rendered(hrtf: false, z: 10f);
        var pannedBehind = Rendered(hrtf: false, z: -10f);

        var same = 0f;

        for (var i = 0; i < pannedFront.Length; i++) {
            same = MathF.Max(same, MathF.Abs(pannedFront[i] - pannedBehind[i]));
        }

        Assert.True(same < 1e-5f, $"panning already told them apart by {same:F6}, so this test proves nothing");

        var front = Rendered(hrtf: true, z: 10f);
        var behind = Rendered(hrtf: true, z: -10f);

        var difference = 0f;

        for (var i = 0; i < front.Length; i++) {
            difference = MathF.Max(difference, MathF.Abs(front[i] - behind[i]));
        }

        Assert.True(difference > 1e-3f, $"with the head model they still only differed by {difference:F6}");
    }

    [Fact]
    public void ItIsOffUnlessSomethingAsksForIt() {
        var (engine, _) = AudioTestData.Engine(channels: 2);

        using (engine) {
            Assert.False(engine.UseHrtf);

            engine.UseHrtf = true;
            Assert.True(engine.UseHrtf);
        }
    }

    /// <summary>There is no head model for five speakers, and a surround rig already has the cue.</summary>
    [Fact]
    public void ASurroundDeviceIgnoresIt() {
        var (engine, device) = AudioTestData.Engine(channels: 6);

        using (engine) {
            engine.UseHrtf = true;
            engine.SetListener(new AudioListener());

            engine.Play(AudioTestData.Constant(48_000, 1f), new Mixing.PlaybackSettings {
                IsSpatial = true,
                Spatial = new SpatialSettings { Position = new Core.Mathematics.Vector3(5f, 0f, 0f), MaxDistance = 1_000f }
            });

            engine.Update(0f);

            // Still produces sound rather than silence, which is what a head model applied to six
            // channels would have collapsed it to.
            Assert.True(AudioTestData.Peak(AudioTestData.Render(device, 512)) > 0.01f);
        }
    }
}

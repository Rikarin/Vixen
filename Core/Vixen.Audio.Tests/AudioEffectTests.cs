// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Effects;
using Vixen.Audio.Mixing;
using Xunit;

namespace Vixen.Audio.Tests;

public sealed class AudioEffectTests {
    const int Rate = 48_000;

    /// <summary>Runs a sine through an effect and returns the amplitude that came out.</summary>
    static float Response(BiquadFilterEffect effect, float frequency, int channels = 1, int frames = 8_192) {
        effect.Prepare(new AudioFormat(Rate, channels), frames);
        var buffer = new float[frames * channels];

        for (var frame = 0; frame < frames; frame++) {
            var value = MathF.Sin(2f * MathF.PI * frequency * frame / Rate);

            for (var channel = 0; channel < channels; channel++) {
                buffer[(frame * channels) + channel] = value;
            }
        }

        effect.Process(buffer, frames, channels);

        // The last quarter, so the filter has settled and the answer is the steady state rather than
        // the transient every filter has at the start.
        return AudioTestData.Peak(buffer.AsSpan(frames * channels * 3 / 4));
    }

    [Fact]
    public void ALowPassPassesBelowTheCutoffAndStopsAboveIt() {
        var filter = new BiquadFilterEffect { Kind = BiquadFilterKind.LowPass, Frequency = 1_000f };

        var below = Response(filter, 100f);
        filter.Reset();
        var above = Response(filter, 10_000f);

        Assert.Equal(1f, below, 0.05f);
        Assert.True(above < 0.05f);
    }

    [Fact]
    public void AHighPassDoesTheOpposite() {
        var filter = new BiquadFilterEffect { Kind = BiquadFilterKind.HighPass, Frequency = 1_000f };

        var below = Response(filter, 100f);
        filter.Reset();
        var above = Response(filter, 10_000f);

        Assert.True(below < 0.05f);
        Assert.Equal(1f, above, 0.05f);
    }

    /// <summary>
    ///     A Butterworth low-pass is 3 dB down at its cutoff, which is the definition of the cutoff.
    ///     If this drifts, the coefficients have stopped being the cookbook's.
    /// </summary>
    [Fact]
    public void TheCutoffIsWhereTheFilterIsThreeDecibelsDown() {
        var filter = new BiquadFilterEffect { Kind = BiquadFilterKind.LowPass, Frequency = 1_000f };

        Assert.Equal(0.7071f, Response(filter, 1_000f), 0.02f);
    }

    [Fact]
    public void ABandPassKeepsItsCentreAndDropsBothSides() {
        var filter = new BiquadFilterEffect { Kind = BiquadFilterKind.BandPass, Frequency = 1_000f, Q = 4f };

        var low = Response(filter, 50f);
        filter.Reset();
        var centre = Response(filter, 1_000f);
        filter.Reset();
        var high = Response(filter, 20_000f);

        Assert.Equal(1f, centre, 0.05f);
        Assert.True(low < 0.1f);
        Assert.True(high < 0.1f);
    }

    [Fact]
    public void APeakingFilterBoostsOnlyItsOwnBand() {
        var filter = new BiquadFilterEffect {
            Kind = BiquadFilterKind.Peaking,
            Frequency = 1_000f,
            Q = 2f,
            GainDb = 12f
        };

        var boosted = Response(filter, 1_000f);
        filter.Reset();
        var untouched = Response(filter, 100f);

        // +12 dB is a factor of four.
        Assert.Equal(4f, boosted, 0.15f);
        Assert.Equal(1f, untouched, 0.05f);
    }

    /// <summary>
    ///     A cutoff above Nyquist has no meaning, and the cookbook formulae produce an unstable
    ///     filter rather than saying so — one denormal-loud block of noise, which is a horrible way
    ///     to find out about a typo in a preset.
    /// </summary>
    [Fact]
    public void AFrequencyAboveNyquistIsClampedRatherThanExploding() {
        var filter = new BiquadFilterEffect { Kind = BiquadFilterKind.LowPass, Frequency = 200_000f };

        var response = Response(filter, 1_000f);

        Assert.True(float.IsFinite(response));
        Assert.True(response <= 1.1f);
    }

    [Fact]
    public void ADisabledEffectChangesNothing() {
        var filter = new BiquadFilterEffect {
            Kind = BiquadFilterKind.LowPass,
            Frequency = 100f,
            Enabled = false
        };

        Assert.Equal(1f, Response(filter, 10_000f), 0.001f);
    }

    [Fact]
    public void AReverbTurnsAnImpulseIntoATail() {
        var reverb = new ReverbEffect { Wet = 1f, Dry = 0f, RoomSize = 0.9f };
        reverb.Prepare(new AudioFormat(Rate, 2), 4_096);

        var buffer = new float[4_096 * 2];
        buffer[0] = 1f;
        buffer[1] = 1f;
        reverb.Process(buffer, 4_096, 2);

        // Nothing at the impulse itself — the shortest comb is over a thousand samples long — and
        // something well after it.
        Assert.True(AudioTestData.Peak(buffer.AsSpan(0, 200)) < 0.001f);
        Assert.True(AudioTestData.Peak(buffer.AsSpan(4_000)) > 0.0001f);
    }

    [Fact]
    public void AReverbLeavesTheDrySignalWhereItWas() {
        var reverb = new ReverbEffect { Wet = 0f, Dry = 1f };
        reverb.Prepare(new AudioFormat(Rate, 2), 64);

        var buffer = new float[64 * 2];
        Array.Fill(buffer, 0.5f);
        reverb.Process(buffer, 64, 2);

        Assert.Equal(0.5f, buffer[0], 0.0001f);
        Assert.Equal(0.5f, buffer[^1], 0.0001f);
    }

    /// <summary>What a scene change calls, so the tail of the last level does not arrive in the next.</summary>
    [Fact]
    public void ResettingAReverbThrowsAwayItsTail() {
        var reverb = new ReverbEffect { Wet = 1f, Dry = 0f, RoomSize = 1f };
        reverb.Prepare(new AudioFormat(Rate, 2), 4_096);

        var buffer = new float[4_096 * 2];
        buffer[0] = 1f;
        reverb.Process(buffer, 4_096, 2);
        Assert.True(AudioTestData.Peak(buffer) > 0f);

        reverb.Reset();
        Array.Clear(buffer);
        reverb.Process(buffer, 4_096, 2);

        Assert.Equal(0f, AudioTestData.Peak(buffer));
    }

    /// <summary>
    ///     The tuning constants are quoted at 44 100 Hz. A reverb that ignored the device rate would
    ///     be a tenth shorter at 48 kHz and audibly different on two machines.
    /// </summary>
    [Fact]
    public void AReverbDecaysOverTheSameTimeAtTwoSampleRates() {
        var atReference = DecaySeconds(44_100);
        var atDevice = DecaySeconds(48_000);

        Assert.Equal(atReference, atDevice, 0.02f);

        // Where 95 % of the tail's energy has arrived. A robust statistic, unlike "the last sample
        // above a threshold": a reverb tail is a dense sum of echoes and its last audible sample
        // moves around by tens of milliseconds for reasons that are not the decay time.
        static float DecaySeconds(int rate) {
            var reverb = new ReverbEffect { Wet = 1f, Dry = 0f, RoomSize = 0.5f, Damping = 0.5f };
            var frames = rate * 2;
            reverb.Prepare(new AudioFormat(rate, 1), frames);

            var buffer = new float[frames];
            buffer[0] = 1f;
            reverb.Process(buffer, frames, 1);

            var total = 0.0;

            foreach (var value in buffer) {
                total += value * (double)value;
            }

            var running = 0.0;

            for (var i = 0; i < frames; i++) {
                running += buffer[i] * (double)buffer[i];

                if (running >= total * 0.95) {
                    return i / (float)rate;
                }
            }

            return frames / (float)rate;
        }
    }

    [Fact]
    public void AnEffectOnABusProcessesEverythingRoutedIntoIt() {
        var (engine, device) = AudioTestData.Engine(channels: 1);
        using var _ = engine;

        var music = engine.CreateBus("Music");
        music.AddEffect(new BiquadFilterEffect { Kind = BiquadFilterKind.LowPass, Frequency = 20f });

        engine.Play(AudioTestData.Constant(4_800, 1f), new PlaybackSettings {
            Gain = 1f,
            Pitch = 1f,
            Bus = music.Index
        });

        // A step through a 20 Hz low-pass has barely started to rise after a millisecond.
        var rendered = AudioTestData.Render(device, 48);

        Assert.True(AudioTestData.Peak(rendered) < 0.05f);
        Assert.Single(music.Effects);
    }

    [Fact]
    public void AnEffectCannotBeAddedBeforeTheBusKnowsItsFormat() {
        var mixer = new AudioMixer();

        Assert.Throws<InvalidOperationException>(
            () => { mixer.Master.AddEffect(new BiquadFilterEffect()); }
        );
    }

    [Fact]
    public void AnEffectCanBeTakenOffAgain() {
        var (engine, _) = AudioTestData.Engine();
        using var __ = engine;

        var effect = new BiquadFilterEffect();
        engine.Master.AddEffect(effect);

        Assert.True(engine.Master.RemoveEffect(effect));
        Assert.False(engine.Master.RemoveEffect(effect));
        Assert.Empty(engine.Master.Effects);
    }

    [Fact]
    public void AnEqualiserIsItsBandsOneAfterAnother() {
        var equalizer = new EqualizerEffect();
        equalizer.AddBand(BiquadFilterKind.HighPass, 200f);
        equalizer.AddBand(BiquadFilterKind.Peaking, 1_000f, 2f, 12f);

        equalizer.Prepare(new AudioFormat(Rate, 1), 8_192);

        Assert.Equal(2, equalizer.Bands.Count);
        Assert.True(Through(equalizer, 50f) < 0.1f, "the high-pass did not take the rumble out");

        equalizer.Reset();
        Assert.Equal(4f, Through(equalizer, 1_000f), 0.2f);
    }

    [Fact]
    public void AnEqualiserBandCanBeChangedAndRemoved() {
        var equalizer = new EqualizerEffect();
        var band = equalizer.AddBand(BiquadFilterKind.LowPass, 100f);
        equalizer.Prepare(new AudioFormat(Rate, 1), 8_192);

        Assert.True(Through(equalizer, 10_000f) < 0.05f);

        band.Frequency = 20_000f;
        equalizer.Reset();
        Assert.Equal(1f, Through(equalizer, 10_000f), 0.1f);

        Assert.True(equalizer.RemoveBand(band));
        Assert.False(equalizer.RemoveBand(band));
        Assert.Empty(equalizer.Bands);
    }

    [Fact]
    public void AnEqualiserWithNoBandsChangesNothing() {
        var equalizer = new EqualizerEffect();
        equalizer.Prepare(new AudioFormat(Rate, 1), 512);

        var buffer = new float[512];
        Array.Fill(buffer, 0.5f);
        equalizer.Process(buffer, 512, 1);

        Assert.Equal(0.5f, buffer[^1]);
    }

    [Fact]
    public void ADelayRepeatsAfterTheTimeItWasGiven() {
        var delay = new DelayEffect {
            DelaySeconds = 0.01f,
            Feedback = 0f,
            Wet = 1f,
            Dry = 1f,
            DampingHz = 30_000f
        };

        delay.Prepare(new AudioFormat(Rate, 1), 4_096);

        var buffer = new float[4_096];
        buffer[0] = 1f;
        delay.Process(buffer, 4_096, 1);

        // 10 ms at 48 kHz is 480 samples.
        Assert.Equal(1f, buffer[0], 1e-5f);
        Assert.Equal(1f, buffer[480], 1e-5f);
        Assert.Equal(0f, buffer[479], 1e-5f);
        Assert.Equal(0f, buffer[960], 1e-5f);
    }

    [Fact]
    public void FeedbackMakesMoreRepeats() {
        var delay = new DelayEffect {
            DelaySeconds = 0.01f,
            Feedback = 0.5f,
            Wet = 1f,
            Dry = 0f,
            DampingHz = 30_000f
        };

        delay.Prepare(new AudioFormat(Rate, 1), 4_096);

        var buffer = new float[4_096];
        buffer[0] = 1f;
        delay.Process(buffer, 4_096, 1);

        Assert.Equal(1f, buffer[480], 1e-4f);
        Assert.Equal(0.5f, buffer[960], 1e-3f);
        Assert.Equal(0.25f, buffer[1_440], 1e-3f);
    }

    /// <summary>
    ///     An unfiltered delay repeats the same bright signal until it fades, which is what a digital
    ///     delay does and what nothing in the world does.
    /// </summary>
    [Fact]
    public void TheRepeatsGetDarkerWhenTheFeedbackIsDamped() {
        var bright = new float[8_192];

        for (var i = 0; i < bright.Length; i++) {
            bright[i] = i < 480 ? 0.5f * MathF.Sin(2f * MathF.PI * 8_000f * i / Rate) : 0f;
        }

        var damped = Tail(new DelayEffect {
            DelaySeconds = 0.01f, Feedback = 0.8f, Wet = 1f, Dry = 0f, DampingHz = 500f
        });

        var open = Tail(new DelayEffect {
            DelaySeconds = 0.01f, Feedback = 0.8f, Wet = 1f, Dry = 0f, DampingHz = 30_000f
        });

        Assert.True(damped < open * 0.5f, $"damped {damped:F4} against open {open:F4}");

        float Tail(DelayEffect delay) {
            delay.Prepare(new AudioFormat(Rate, 1), bright.Length);
            var buffer = (float[])bright.Clone();
            delay.Process(buffer, bright.Length, 1);
            return AudioTestData.Peak(buffer.AsSpan(4_096));
        }
    }

    /// <summary>
    ///     The dry signal always enters its own line; it is the feedback that crosses. Getting that
    ///     backwards puts the sound in both speakers at once instead of bouncing it between them.
    /// </summary>
    [Fact]
    public void PingPongPutsTheFirstRepeatOnTheOtherSide() {
        var delay = new DelayEffect {
            DelaySeconds = 0.01f,
            Feedback = 0.7f,
            Wet = 1f,
            Dry = 0f,
            DampingHz = 30_000f,
            PingPong = true
        };

        delay.Prepare(new AudioFormat(Rate, 2), 4_096);

        var buffer = new float[4_096 * 2];
        buffer[0] = 1f;
        delay.Process(buffer, 4_096, 2);

        // Struck on the left: the first repeat comes back on the left, and its feedback lands on the
        // right one delay later.
        Assert.Equal(1f, buffer[480 * 2], 1e-4f);
        Assert.Equal(0f, buffer[(480 * 2) + 1], 1e-4f);
        Assert.Equal(0f, buffer[960 * 2], 1e-4f);
        Assert.Equal(0.7f, buffer[(960 * 2) + 1], 1e-3f);
    }

    /// <summary>
    ///     At a feedback of one it never decays; above it the level doubles every repeat until the
    ///     limiter is the only thing between the player and a very loud noise.
    /// </summary>
    [Fact]
    public void FeedbackCannotRunAway() {
        var delay = new DelayEffect {
            DelaySeconds = 0.001f,
            Feedback = 4f,
            Wet = 1f,
            Dry = 0f,
            DampingHz = 30_000f
        };

        delay.Prepare(new AudioFormat(Rate, 1), 48_000);

        var buffer = new float[48_000];
        buffer[0] = 1f;
        delay.Process(buffer, 48_000, 1);

        Assert.True(AudioTestData.Peak(buffer) <= 1.001f, $"it reached {AudioTestData.Peak(buffer):F3}");
    }

    static float Through(EqualizerEffect effect, float frequency) {
        var frames = 8_192;
        var buffer = new float[frames];

        for (var i = 0; i < frames; i++) {
            buffer[i] = MathF.Sin(2f * MathF.PI * frequency * i / Rate);
        }

        effect.Process(buffer, frames, 1);
        return AudioTestData.Peak(buffer.AsSpan(frames * 3 / 4));
    }
}

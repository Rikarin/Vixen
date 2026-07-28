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
}

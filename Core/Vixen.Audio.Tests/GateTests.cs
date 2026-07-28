// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Effects;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>The gate: the compressor pointing the other way, and what an open microphone needs.</summary>
public sealed class GateTests {
    const int Rate = 48_000;

    static GateEffect Gate(float thresholdDb = -40f, float holdSeconds = 0f, float releaseSeconds = 0.001f) {
        var gate = new GateEffect {
            ThresholdDb = thresholdDb,
            HoldSeconds = holdSeconds,
            ReleaseSeconds = releaseSeconds,
            AttackSeconds = 0.0005f,
            RangeDb = -60f,
            KneeDb = 6f
        };

        gate.Prepare(AudioFormat.Mono48k, 4_800);
        return gate;
    }

    static float[] Level(float amplitude, int frames) {
        var buffer = new float[frames];

        for (var i = 0; i < frames; i++) {
            buffer[i] = amplitude * MathF.Sin(2f * MathF.PI * 400f * i / Rate);
        }

        return buffer;
    }

    /// <summary>The whole job: the wash between words goes and the words do not.</summary>
    [Fact]
    public void QuietSignalIsClosedDownAndLoudSignalIsNot() {
        var gate = Gate();

        // A tenth of a second of speech-level signal, which opens it.
        var loud = Level(0.5f, 4_800);
        gate.Process(loud, 4_800, 1);
        Assert.True(AudioTestData.Peak(loud.AsSpan(2_400)) > 0.4f, "it should be open by now");

        // Then room tone at −54 dB, which is well under the threshold.
        var quiet = Level(0.002f, 4_800);
        gate.Process(quiet, 4_800, 1);

        var remaining = AudioTestData.Peak(quiet.AsSpan(2_400));
        Assert.True(remaining < 0.0001f, $"the noise should be all but gone, peak was {remaining:E2}");
    }

    /// <summary>
    ///     A gate with no hold slams shut between syllables, which is the chattering that makes gated
    ///     dialogue sound worse than ungated.
    /// </summary>
    [Fact]
    public void TheHoldCarriesItThroughAGapInSpeech() {
        var chattering = Gate(holdSeconds: 0f, releaseSeconds: 0.02f);
        var held = Gate(holdSeconds: 0.15f, releaseSeconds: 0.02f);

        foreach (var gate in new[] { chattering, held }) {
            var opening = Level(0.5f, 4_800);
            gate.Process(opening, 4_800, 1);
        }

        // Fifty milliseconds of near-silence, as between two words.
        var gapA = Level(0.001f, 2_400);
        var gapB = Level(0.001f, 2_400);
        chattering.Process(gapA, 2_400, 1);
        held.Process(gapB, 2_400, 1);

        Assert.True(chattering.GainReductionDb < -20f, $"it should have closed, was {chattering.GainReductionDb:F1}");
        Assert.Equal(0f, held.GainReductionDb, 0.5f);
        Assert.True(held.IsOpen);
        Assert.False(chattering.IsOpen);
    }

    /// <summary>The next word has to arrive intact, which is what a fast attack is for.</summary>
    [Fact]
    public void ItOpensQuicklyEnoughNotToClipTheStartOfAWord() {
        var gate = Gate();

        var silence = new float[4_800];
        gate.Process(silence, 4_800, 1);
        Assert.False(gate.IsOpen);

        var word = Level(0.5f, 480);
        gate.Process(word, 480, 1);

        // Within ten milliseconds it is essentially all the way open.
        Assert.True(AudioTestData.Peak(word.AsSpan(240)) > 0.4f, "the consonant was cut off");
    }

    /// <summary>
    ///     A gate that closes to nothing is obvious: the room tone it was hiding stops dead the moment
    ///     somebody speaks and comes back when they finish.
    /// </summary>
    [Fact]
    public void TheRangeIsHowFarDownAClosedGateGoesRatherThanSilence() {
        var gate = Gate();
        gate.RangeDb = -12f;
        gate.Reset();

        // −60 dB of room tone, which is well under the threshold.
        var quiet = Level(0.001f, 9_600);
        gate.Process(quiet, 9_600, 1);

        // −12 dB is a quarter of it, not nothing.
        var remaining = AudioTestData.Peak(quiet.AsSpan(7_200));
        Assert.Equal(0.000251f, remaining, 4e-5f);
    }

    /// <summary>
    ///     Resetting open would pass one release-time of whatever is in the room the moment a scene
    ///     loads, which is exactly the sound it is there to remove.
    /// </summary>
    [Fact]
    public void ItResetsShutRatherThanOpen() {
        var gate = Gate();
        gate.Reset();

        Assert.False(gate.IsOpen);
        Assert.Equal(-60f, gate.GainReductionDb, 0.01f);
    }

    /// <summary>Which makes it a voice-activity flag as well as an effect.</summary>
    [Fact]
    public void IsOpenSaysWhetherAnybodyIsTalking() {
        var gate = Gate();

        gate.Process(new float[2_400], 2_400, 1);
        Assert.False(gate.IsOpen);

        var speech = Level(0.4f, 2_400);
        gate.Process(speech, 2_400, 1);
        Assert.True(gate.IsOpen);
    }

    /// <summary>A gate on one bus keyed by another is how a bed opens only while a channel transmits.</summary>
    [Fact]
    public void ItCanBeKeyedBySomethingOtherThanWhatItIsGating() {
        var gate = Gate();

        var bed = Level(0.5f, 4_800);
        var key = new float[4_800];
        gate.Process(bed, key, 4_800, 1);

        Assert.False(gate.IsOpen);
        Assert.True(AudioTestData.Peak(bed.AsSpan(2_400)) < 0.001f, "silence on the key should have shut it");

        var speaking = Level(0.5f, 4_800);
        var talking = Level(0.5f, 4_800);
        gate.Process(speaking, talking, 4_800, 1);

        Assert.True(gate.IsOpen);
        Assert.True(AudioTestData.Peak(speaking.AsSpan(2_400)) > 0.4f);
    }

    [Fact]
    public void ADisabledGateDoesNothing() {
        var gate = Gate();
        gate.Enabled = false;

        var quiet = Level(0.001f, 2_400);
        var expected = (float[])quiet.Clone();
        gate.Process(quiet, 2_400, 1);

        Assert.Equal(expected, quiet);
    }

    [Fact]
    public void ItsKnobsAreAutomatable() {
        var gate = new GateEffect();

        Assert.True(gate.TrySetProperty("ThresholdDb", -30f));
        Assert.Equal(-30f, gate.ThresholdDb);
        Assert.True(gate.TrySetProperty("HoldSeconds", 0.4f));
        Assert.Equal(0.4f, gate.HoldSeconds);
        Assert.False(gate.TrySetProperty("Nonsense", 1f));
    }
}

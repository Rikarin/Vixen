// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Devices;
using Vixen.Audio.Events;
using Vixen.Audio.Mixing;
using Vixen.Audio.Parameters;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>Curves on their own — no engine, no voice, no sound.</summary>
public sealed class AudioCurveTests {
    [Fact]
    public void AConstantIsTheSameEverywhere() {
        var curve = AudioCurve.Constant(-6f);

        Assert.Equal(-6f, curve.Evaluate(0f));
        Assert.Equal(-6f, curve.Evaluate(0.5f));
        Assert.Equal(-6f, curve.Evaluate(1f));
    }

    [Fact]
    public void ARampIsAStraightLineAcrossTheRange() {
        var curve = AudioCurve.Ramp(0f, 100f);

        Assert.Equal(0f, curve.Evaluate(0f));
        Assert.Equal(25f, curve.Evaluate(0.25f), 1e-4f);
        Assert.Equal(100f, curve.Evaluate(1f));
    }

    /// <summary>
    ///     Extrapolating instead is how a gain curve drawn between 0.2 and 0.8 reaches +40 dB at a
    ///     parameter value nobody drew.
    /// </summary>
    [Fact]
    public void ACurveIsFlatOutsideItsPointsRatherThanExtrapolated() {
        var curve = new AudioCurve([new(0.2f, 0f), new(0.8f, 12f)]);

        Assert.Equal(0f, curve.Evaluate(0f));
        Assert.Equal(0f, curve.Evaluate(-5f));
        Assert.Equal(12f, curve.Evaluate(1f));
        Assert.Equal(12f, curve.Evaluate(99f));
        Assert.Equal(6f, curve.Evaluate(0.5f), 1e-4f);
    }

    /// <summary>The order somebody drew the points in is not their mistake to fix.</summary>
    [Fact]
    public void PointsAreSortedOnTheWayIn() {
        var curve = new AudioCurve([new(1f, 10f), new(0f, 0f), new(0.5f, 5f)]);

        Assert.Equal(0f, curve.Evaluate(0f));
        Assert.Equal(5f, curve.Evaluate(0.5f), 1e-4f);
        Assert.Equal(10f, curve.Evaluate(1f));
    }

    [Fact]
    public void StepHoldsEachValueUntilTheNextPoint() {
        var curve = new AudioCurve(
            [new(0f, 1f), new(0.5f, 2f), new(1f, 3f)],
            AudioCurveInterpolation.Step
        );

        Assert.Equal(1f, curve.Evaluate(0.1f));
        Assert.Equal(1f, curve.Evaluate(0.49f));
        Assert.Equal(2f, curve.Evaluate(0.5f));
        Assert.Equal(2f, curve.Evaluate(0.99f));
        Assert.Equal(3f, curve.Evaluate(1f));
    }

    /// <summary>A corner in a gain is audible as a change of direction even though the gain never jumps.</summary>
    [Fact]
    public void SmoothArrivesAtTheSamePlacesButFlatAtEachEnd() {
        var linear = AudioCurve.Ramp(0f, 1f);
        var smooth = new AudioCurve([new(0f, 0f), new(1f, 1f)], AudioCurveInterpolation.Smooth);

        Assert.Equal(0f, smooth.Evaluate(0f));
        Assert.Equal(0.5f, smooth.Evaluate(0.5f), 1e-4f);
        Assert.Equal(1f, smooth.Evaluate(1f));

        // Flatter than the line near the ends, steeper in the middle.
        Assert.True(smooth.Evaluate(0.1f) < linear.Evaluate(0.1f));
        Assert.True(smooth.Evaluate(0.9f) > linear.Evaluate(0.9f));
    }

    [Fact]
    public void AnEmptyCurveIsZeroRatherThanAThrow() => Assert.Equal(0f, new AudioCurve([]).Evaluate(0.5f));
}

/// <summary>The sheet: normalising, combining and seeking.</summary>
public sealed class AudioParameterSheetTests {
    static AudioParameterSheet Sheet(params AudioParameterDefinition[] parameters) => new(parameters);

    static AudioParameterDefinition Parameter(
        string name,
        AudioParameterTarget target,
        float from,
        float to,
        float minimum = 0f,
        float maximum = 1f,
        float seekSeconds = 0f
    ) => new() {
        Name = name,
        Minimum = minimum,
        Maximum = maximum,
        SeekSeconds = seekSeconds,
        Automation = [new(target, AudioCurve.Ramp(from, to))]
    };

    [Fact]
    public void AValueIsNormalisedAgainstItsOwnRange() {
        var sheet = Sheet(Parameter("speed", AudioParameterTarget.GainDb, 0f, 0f, 0f, 200f));

        Assert.Equal(0f, sheet.Normalize(0, 0f));
        Assert.Equal(0.5f, sheet.Normalize(0, 100f), 1e-5f);
        Assert.Equal(1f, sheet.Normalize(0, 200f));
        Assert.Equal(1f, sheet.Normalize(0, 5_000f));
    }

    /// <summary>Decibels are already logarithmic, so adding them multiplies what they describe.</summary>
    [Fact]
    public void TwoParametersOnAGainAddTheirDecibels() {
        var sheet = Sheet(
            Parameter("a", AudioParameterTarget.GainDb, 0f, -6f),
            Parameter("b", AudioParameterTarget.GainDb, 0f, -6f)
        );

        Assert.Equal(-12f, sheet.Evaluate([1f, 1f]).GainDb, 1e-4f);
        Assert.Equal(-6f, sheet.Evaluate([1f, 0f]).GainDb, 1e-4f);
    }

    /// <summary>
    ///     A sound both underwater and behind a door is muffled by whichever is muffling it more.
    ///     There is no sense in which two cutoffs add.
    /// </summary>
    [Fact]
    public void TwoParametersOnALowPassTakeTheLowerAndOnAHighPassTheHigher() {
        var sheet = Sheet(
            Parameter("water", AudioParameterTarget.LowPassHz, 20_000f, 400f),
            Parameter("door", AudioParameterTarget.LowPassHz, 20_000f, 900f)
        );

        Assert.Equal(400f, sheet.Evaluate([1f, 1f]).LowPassHz, 1e-2f);
        Assert.Equal(900f, sheet.Evaluate([0f, 1f]).LowPassHz, 1e-2f);

        var thin = Sheet(
            Parameter("radio", AudioParameterTarget.HighPassHz, 0f, 300f),
            Parameter("phone", AudioParameterTarget.HighPassHz, 0f, 700f)
        );

        Assert.Equal(700f, thin.Evaluate([1f, 1f]).HighPassHz, 1e-2f);
    }

    [Fact]
    public void ATargetNobodyDrivesStaysNeutral() {
        var result = Sheet(Parameter("a", AudioParameterTarget.GainDb, 0f, -6f)).Evaluate([1f]);

        Assert.Equal(0f, result.PitchSemitones);
        Assert.Equal(0f, result.LowPassHz);
        Assert.Equal(0f, result.HighPassHz);
    }

    [Fact]
    public void DefaultsAreWhereTheValuesStart() {
        var sheet = Sheet(
            new AudioParameterDefinition { Name = "a", Default = 0.25f },
            new AudioParameterDefinition { Name = "b", Default = 0.75f }
        );

        var values = new float[AudioParameterSheet.MaxParameters];
        sheet.CopyDefaultsTo(values);

        Assert.Equal(0.25f, values[0]);
        Assert.Equal(0.75f, values[1]);
        Assert.Equal(0f, values[2]);
    }

    [Fact]
    public void NamesResolveToIndicesAndUnknownOnesToMinusOne() {
        var sheet = Sheet(
            new AudioParameterDefinition { Name = "wetness" },
            new AudioParameterDefinition { Name = "distance" }
        );

        Assert.Equal(0, sheet.IndexOf("wetness"));
        Assert.Equal(1, sheet.IndexOf("distance"));
        Assert.Equal(-1, sheet.IndexOf("Wetness"));
        Assert.Equal(-1, sheet.IndexOf("nothing"));
    }

    /// <summary>A gameplay boolean crosses a whole range in one frame, and a cutoff that does is a click.</summary>
    [Fact]
    public void SeekingLimitsHowFastAValueMoves() {
        var sheet = Sheet(Parameter("a", AudioParameterTarget.GainDb, 0f, -6f, seekSeconds: 1f));
        var value = 0f;

        // A tenth of a second across a range of one is a tenth of the way.
        sheet.Seek(0, ref value, 1f, 0.1f);
        Assert.Equal(0.1f, value, 1e-5f);

        sheet.Seek(0, ref value, 1f, 0.1f);
        Assert.Equal(0.2f, value, 1e-5f);
    }

    [Fact]
    public void SeekingArrivesRatherThanOvershooting() {
        var sheet = Sheet(Parameter("a", AudioParameterTarget.GainDb, 0f, -6f, seekSeconds: 1f));
        var value = 0f;

        sheet.Seek(0, ref value, 1f, 10f);
        Assert.Equal(1f, value);

        sheet.Seek(0, ref value, 0f, 10f);
        Assert.Equal(0f, value);
    }

    [Fact]
    public void NoSeekTimeArrivesAtOnce() {
        var sheet = Sheet(Parameter("a", AudioParameterTarget.GainDb, 0f, -6f));
        var value = 0f;

        sheet.Seek(0, ref value, 1f, 1f / 60f);
        Assert.Equal(1f, value);
    }

    /// <summary>The values are a flat table sized once for the pool, so the cap is real.</summary>
    [Fact]
    public void ASheetIsCappedAtTheNumberTheEngineHasRoomFor() {
        var many = new AudioParameterDefinition[AudioParameterSheet.MaxParameters + 4];

        for (var i = 0; i < many.Length; i++) {
            many[i] = new() { Name = $"p{i}" };
        }

        Assert.Equal(AudioParameterSheet.MaxParameters, new AudioParameterSheet(many).Count);
    }

    /// <summary>A reversed or zero-width range would otherwise divide by zero or run the curve backwards.</summary>
    [Fact]
    public void ARangeThatIsNotOneIsPinnedRatherThanUndefined() {
        var sheet = Sheet(Parameter("a", AudioParameterTarget.GainDb, 3f, 9f, minimum: 5f, maximum: 5f));

        Assert.Equal(0f, sheet.Normalize(0, 5f));
        Assert.Equal(3f, sheet.Evaluate([5f]).GainDb, 1e-4f);
    }
}

/// <summary>Parameters on an engine, ending in what came out of the mixer.</summary>
public sealed class VoiceParameterTests : IDisposable {
    readonly AudioEngine engine;
    readonly NullAudioDevice device;

    public VoiceParameterTests() => (engine, device) = AudioTestData.Engine(voices: 8);

    public void Dispose() => engine.Dispose();

    static AudioParameterSheet Submersion(float cutoff = 300f, float gainDb = 0f) => new([
        new AudioParameterDefinition {
            Name = "submersion",
            Automation = gainDb == 0f
                ? [new(AudioParameterTarget.LowPassHz, AudioCurve.Ramp(24_000f, cutoff))]
                : [
                    new(AudioParameterTarget.LowPassHz, AudioCurve.Ramp(24_000f, cutoff)),
                    new(AudioParameterTarget.GainDb, AudioCurve.Ramp(0f, gainDb))
                ]
        }
    ]);

    [Fact]
    public void AttachingGivesTheVoiceTheSheetAndItsDefaults() {
        var handle = engine.Play(AudioTestData.Constant(48_000, 1f));

        Assert.True(engine.AttachParameters(handle, Submersion()));
        Assert.NotNull(engine.ParametersOf(handle));
        Assert.Equal(0f, engine.ParameterOf(handle, 0));
    }

    [Fact]
    public void AStaleHandleAttachesNothing() =>
        Assert.False(engine.AttachParameters(new VoiceHandle(0, 999), Submersion()));

    [Fact]
    public void AParameterMovesTowardsWhatItWasPointedAt() {
        var sheet = new AudioParameterSheet([
            new AudioParameterDefinition { Name = "x", SeekSeconds = 1f }
        ]);

        var handle = engine.Play(AudioTestData.Constant(48_000, 1f));
        engine.AttachParameters(handle, sheet);

        Assert.True(engine.SetParameter(handle, "x", 1f));

        engine.Update(0.25f);
        Assert.Equal(0.25f, engine.ParameterOf(handle, 0), 1e-4f);

        engine.Update(0.25f);
        Assert.Equal(0.5f, engine.ParameterOf(handle, 0), 1e-4f);

        engine.Update(10f);
        Assert.Equal(1f, engine.ParameterOf(handle, 0), 1e-4f);
    }

    [Fact]
    public void SettingAnUnknownParameterSaysSoRatherThanGuessing() {
        var handle = engine.Play(AudioTestData.Constant(48_000, 1f));
        engine.AttachParameters(handle, Submersion());

        Assert.False(engine.SetParameter(handle, "nonsense", 1f));
        Assert.False(engine.SetParameter(handle, 7, 1f));
    }

    [Fact]
    public void AValueIsClampedToItsParametersRange() {
        var sheet = new AudioParameterSheet([
            new AudioParameterDefinition { Name = "x", Minimum = 0f, Maximum = 2f }
        ]);

        var handle = engine.Play(AudioTestData.Constant(48_000, 1f));
        engine.AttachParameters(handle, sheet);
        engine.SetParameter(handle, "x", 99f);
        engine.Update(0f);

        Assert.Equal(2f, engine.ParameterOf(handle, 0));
    }

    /// <summary>The whole point of a gain automation, measured where it lands.</summary>
    [Fact]
    public void AGainCurveScalesTheVoiceWithoutTouchingItsOwnGain() {
        var handle = engine.Play(AudioTestData.Constant(48_000, 1f), new PlaybackSettings { Gain = 0.5f });
        engine.AttachParameters(handle, Submersion(24_000f, -6f));
        engine.SetParameter(handle, "submersion", 1f);
        engine.Update(0f);

        // The voice's own gain is untouched; the automation is a separate multiplier on top.
        Assert.Equal(0.5f, engine.GainOf(handle));

        var rendered = AudioTestData.Render(device, 64);

        // 0.5 × −6 dB is about 0.25, and a mono source panned centre comes out at 0.707 of that.
        Assert.Equal(0.1772f, MathF.Abs(rendered[0]), 0.005f);
    }

    /// <summary>
    ///     The headline. Two plays of one description, one submerged and one not — which is the thing a
    ///     bus per environment cannot do, and the shape a voice-chat session actually has.
    /// </summary>
    [Fact]
    public void OneInstanceCanBeUnderwaterWhileAnotherIsNot() {
        var dry = engine.CreateBus("Dry");
        var wet = engine.CreateBus("Wet");
        var sheet = Submersion(300f);
        var tone = AudioTestData.Tone(6_000f, 48_000);

        var above = engine.Play(tone, new PlaybackSettings { Bus = dry.Index });
        var below = engine.Play(tone, new PlaybackSettings { Bus = wet.Index });

        engine.AttachParameters(above, sheet);
        engine.AttachParameters(below, sheet);
        engine.SetParameter(below, "submersion", 1f);
        engine.Update(0f);

        var (dryPeak, wetPeak) = (0f, 0f);

        // Several blocks, because the filter needs a moment to settle and PeakLevel is per block.
        for (var i = 0; i < 12; i++) {
            AudioTestData.Render(device, 64);
            dryPeak = MathF.Max(dryPeak, dry.PeakLevel);
            wetPeak = MathF.Max(wetPeak, wet.PeakLevel);
        }

        Assert.True(dryPeak > 0.5f, $"the unfiltered one should be near full scale, was {dryPeak:F3}");
        Assert.True(wetPeak < dryPeak * 0.2f, $"6 kHz through a 300 Hz low-pass: dry {dryPeak:F3}, wet {wetPeak:F3}");
    }

    /// <summary>A high-pass thins rather than muffles, which is the other half of a telephone.</summary>
    [Fact]
    public void AHighPassTakesTheBottomOut() {
        var sheet = new AudioParameterSheet([
            new AudioParameterDefinition {
                Name = "radio",
                Automation = [new(AudioParameterTarget.HighPassHz, AudioCurve.Ramp(0f, 2_000f))]
            }
        ]);

        var handle = engine.Play(AudioTestData.Tone(120f, 48_000));
        engine.AttachParameters(handle, sheet);
        engine.SetParameter(handle, "radio", 1f);
        engine.Update(0f);

        var peak = 0f;

        for (var i = 0; i < 12; i++) {
            peak = MathF.Max(peak, AudioTestData.Peak(AudioTestData.Render(device, 64)));
        }

        Assert.True(peak < 0.1f, $"120 Hz through a 2 kHz high-pass should be gone, peak was {peak:F3}");
    }

    /// <summary>Sweeping a cutoff up out of the way has to mean "off", not "design a filter at Nyquist".</summary>
    [Fact]
    public void ACutoffAtOrAboveNyquistIsNoFilterAtAll() {
        var handle = engine.Play(AudioTestData.Tone(6_000f, 48_000));
        engine.AttachParameters(handle, Submersion(300f));
        engine.Update(0f);

        var peak = 0f;

        for (var i = 0; i < 8; i++) {
            peak = MathF.Max(peak, AudioTestData.Peak(AudioTestData.Render(device, 64)));
        }

        Assert.True(peak > 0.5f, $"at submersion 0 the sound should be untouched, peak was {peak:F3}");
    }

    /// <summary>Otherwise a footstep inherits the low-pass of the submerged voice whose slot it took.</summary>
    [Fact]
    public void ASheetDoesNotSurviveIntoTheNextUseOfTheSlot() {
        var first = engine.Play(AudioTestData.Constant(32, 1f));
        engine.AttachParameters(first, Submersion(300f, -20f));
        engine.SetParameter(first, "submersion", 1f);
        engine.Update(0f);

        AudioTestData.Render(device, 512);
        engine.Update(0f);

        var second = engine.Play(AudioTestData.Constant(48_000, 1f));

        Assert.Equal(first.Index, second.Index);
        Assert.NotEqual(first.Generation, second.Generation);
        Assert.Null(engine.ParametersOf(second));

        engine.Update(0f);
        var rendered = AudioTestData.Render(device, 64);

        // Full scale rather than twenty decibels down, and unfiltered.
        Assert.Equal(0.7071f, MathF.Abs(rendered[0]), 0.01f);
    }

    /// <summary>
    ///     A stolen slot does not go through <c>Voice.Reset</c> — the audio thread picks the new source
    ///     up where it would have retired the old one — so nothing clears the automation unless the
    ///     play path does. Without that a footstep taking an underwater voice's slot is underwater.
    /// </summary>
    [Fact]
    public void AStolenSlotDoesNotInheritTheAutomationOfWhatItDisplaced() {
        var (small, tiny) = AudioTestData.Engine(voices: 1);

        using (small) {
            var submerged = small.Play(AudioTestData.Tone(6_000f, 48_000));
            small.AttachParameters(submerged, Submersion(300f, -30f));
            small.SetParameter(submerged, "submersion", 1f);
            small.Update(0f);
            AudioTestData.Render(tiny, 64);

            // The only slot there is, taken by something that knows nothing about parameters.
            var footstep = small.Play(AudioTestData.Tone(6_000f, 48_000));
            Assert.True(footstep.IsValid);
            Assert.NotEqual(submerged.Generation, footstep.Generation);

            var peak = 0f;

            for (var i = 0; i < 12; i++) {
                peak = MathF.Max(peak, AudioTestData.Peak(AudioTestData.Render(tiny, 64)));
            }

            // Full scale and unfiltered, not thirty decibels down through a 300 Hz low-pass.
            Assert.True(peak > 0.5f, $"it inherited the automation: peak was {peak:F3}");
            Assert.Null(small.ParametersOf(footstep));
        }
    }

    /// <summary>An event with parameters attaches them itself, so a caller only sets values.</summary>
    [Fact]
    public void AnEventAttachesItsSheetOnEveryPlay() {
        var sound = new AudioEvent(engine, new AudioEventDescription {
            Variants = [new(AudioTestData.Tone(6_000f, 48_000))],
            Parameters = Submersion(300f)
        });

        var handle = sound.Play();

        Assert.NotNull(engine.ParametersOf(handle));
        Assert.True(sound.SetParameter(handle, "submersion", 1f));

        engine.Update(0f);
        var peak = 0f;

        for (var i = 0; i < 12; i++) {
            peak = MathF.Max(peak, AudioTestData.Peak(AudioTestData.Render(device, 64)));
        }

        Assert.True(peak < 0.15f, $"peak was {peak:F3}");
    }
}

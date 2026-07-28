// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Assets;
using Vixen.Audio.Devices;
using Vixen.Audio.Effects;
using Vixen.Audio.Mixing;
using Vixen.Audio.Parameters;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>Engine-wide parameters: a dial on the mix rather than a state of it.</summary>
public sealed class MixerParameterTests : IDisposable {
    readonly AudioEngine engine;
    readonly NullAudioDevice device;

    public MixerParameterTests() => (engine, device) = AudioTestData.Engine(voices: 8);

    public void Dispose() => engine.Dispose();

    static AudioBusParameterDefinition Parameter(string name, float seekSeconds, params AudioBusAutomation[] automation)
        => new() { Name = name, SeekSeconds = seekSeconds, Automation = automation };

    [Fact]
    public void AParameterDrivesABusFaderWithoutOverwritingIt() {
        var music = engine.CreateBus("Music");
        music.Gain = 0.5f;

        engine.LoadParameters([
            Parameter("rain", 0f, new AudioBusAutomation {
                Bus = "Music",
                Target = AudioBusParameterTarget.GainDb,
                Curve = AudioCurve.Ramp(0f, -12f)
            })
        ], out var problems);

        Assert.Empty(problems);

        engine.Parameters!.Set("rain", 1f);
        engine.Update(0f);

        // The fader is where the mix left it; the parameter is a separate multiplier on top.
        Assert.Equal(0.5f, music.Gain);
        Assert.Equal(0.2512f, music.ParameterGain, 1e-3f);
    }

    /// <summary>Otherwise a bus driven to −20 dB and then released would simply stay there.</summary>
    [Fact]
    public void ReleasingAParameterPutsTheBusBackRatherThanLeavingItWhereItWas() {
        var music = engine.CreateBus("Music");

        engine.LoadParameters([
            Parameter("rain", 0f, new AudioBusAutomation {
                Bus = "Music",
                Target = AudioBusParameterTarget.GainDb,
                Curve = AudioCurve.Ramp(0f, -20f)
            })
        ], out _);

        engine.Parameters!.Set("rain", 1f);
        engine.Update(0f);
        Assert.Equal(0.1f, music.ParameterGain, 1e-3f);

        engine.Parameters.Set("rain", 0f);
        engine.Update(0f);
        Assert.Equal(1f, music.ParameterGain, 1e-4f);
    }

    [Fact]
    public void TwoParametersOnOneBusMultiplyRatherThanFight() {
        var music = engine.CreateBus("Music");

        engine.LoadParameters([
            Parameter("a", 0f, new AudioBusAutomation {
                Bus = "Music",
                Target = AudioBusParameterTarget.GainDb,
                Curve = AudioCurve.Ramp(0f, -6f)
            }),
            Parameter("b", 0f, new AudioBusAutomation {
                Bus = "Music",
                Target = AudioBusParameterTarget.GainDb,
                Curve = AudioCurve.Ramp(0f, -6f)
            })
        ], out _);

        engine.Parameters!.Set("a", 1f);
        engine.Parameters.Set("b", 1f);
        engine.Update(0f);

        // −12 dB, which is what two −6s mean.
        Assert.Equal(0.2512f, music.ParameterGain, 1e-3f);
    }

    [Fact]
    public void AParameterDrivesASendLevel() {
        var reverb = engine.CreateBus("Reverb");
        var ambience = engine.CreateBus("Ambience");
        var send = ambience.AddSend(reverb, 1f);

        engine.LoadParameters([
            Parameter("cave", 0f, new AudioBusAutomation {
                Bus = "Ambience",
                Target = AudioBusParameterTarget.SendDb,
                Send = "Reverb",
                Curve = AudioCurve.Ramp(-60f, 0f)
            })
        ], out var problems);

        Assert.Empty(problems);

        engine.Parameters!.Set("cave", 1f);
        engine.Update(0f);
        Assert.Equal(1f, send.ParameterLevel, 1e-3f);

        engine.Parameters.Set("cave", 0f);
        engine.Update(0f);
        Assert.Equal(0.001f, send.ParameterLevel, 1e-4f);
    }

    /// <summary>The one that reaches an effect's own knobs, which snapshots cannot.</summary>
    [Fact]
    public void AParameterDrivesANamedPropertyOnAnInsert() {
        var bus = engine.CreateBus("Voice");
        var filter = new BiquadFilterEffect { Kind = BiquadFilterKind.LowPass, Frequency = 20_000f };
        bus.AddEffect(filter);

        engine.LoadParameters([
            Parameter("submersion", 0f, new AudioBusAutomation {
                Bus = "Voice",
                Target = AudioBusParameterTarget.EffectProperty,
                Effect = 0,
                Property = "Frequency",
                Curve = AudioCurve.Ramp(20_000f, 400f)
            })
        ], out var problems);

        Assert.Empty(problems);

        // Resolved at load, so the property is already where the default says before the first frame.
        Assert.Equal(20_000f, filter.Frequency, 1f);

        engine.Parameters!.Set("submersion", 1f);
        engine.Update(0f);
        Assert.Equal(400f, filter.Frequency, 1f);

        engine.Parameters.Set("submersion", 0.5f);
        engine.Update(0f);
        Assert.Equal(10_200f, filter.Frequency, 50f);
    }

    /// <summary>Every effect worth automating declares its own knobs; the match is exact so a typo is found.</summary>
    [Fact]
    public void EachEffectKnowsWhichOfItsKnobsAreAutomatable() {
        var reverb = new ReverbEffect();
        Assert.True(reverb.TrySetProperty("Wet", 0.75f));
        Assert.Equal(0.75f, reverb.Wet);
        Assert.False(reverb.TrySetProperty("wet", 0.5f));
        Assert.False(reverb.TrySetProperty("Nonsense", 1f));

        var compressor = new CompressorEffect();
        Assert.True(compressor.TrySetProperty("ThresholdDb", -30f));
        Assert.Equal(-30f, compressor.ThresholdDb);

        var delay = new DelayEffect();
        Assert.True(delay.TrySetProperty("Feedback", 0.6f));
        Assert.Equal(0.6f, delay.Feedback);

        // Nothing was asked of the equaliser, so it is simply not automatable rather than broken.
        // Through the interface, because that is where the default implementation lives — a concrete
        // type that declares no knobs does not have the method at all.
        Assert.False(((IAudioEffect)new EqualizerEffect()).TrySetProperty("Wet", 1f));
    }

    [Fact]
    public void SeekingLimitsHowFastTheMixMoves() {
        var music = engine.CreateBus("Music");

        engine.LoadParameters([
            Parameter("rain", 1f, new AudioBusAutomation {
                Bus = "Music",
                Target = AudioBusParameterTarget.GainDb,
                Curve = AudioCurve.Ramp(0f, -20f)
            })
        ], out _);

        engine.Parameters!.Set("rain", 1f);

        engine.Update(0.25f);
        Assert.Equal(0.25f, engine.Parameters.ValueOf(0), 1e-4f);

        engine.Update(10f);
        Assert.Equal(1f, engine.Parameters.ValueOf(0), 1e-4f);
    }

    [Fact]
    public void EverythingThatDoesNotResolveIsReportedRatherThanThrown() {
        var bus = engine.CreateBus("Voice");
        bus.AddEffect(new ReverbEffect());

        engine.LoadParameters([
            Parameter("a", 0f, new AudioBusAutomation { Bus = "Nowhere" }),
            Parameter("b", 0f, new AudioBusAutomation {
                Bus = "Voice",
                Target = AudioBusParameterTarget.SendDb,
                Send = "Missing"
            }),
            Parameter("c", 0f, new AudioBusAutomation {
                Bus = "Voice",
                Target = AudioBusParameterTarget.EffectProperty,
                Effect = 4,
                Property = "Wet"
            }),
            Parameter("d", 0f, new AudioBusAutomation {
                Bus = "Voice",
                Target = AudioBusParameterTarget.EffectProperty,
                Effect = 0,
                Property = "Nonsense"
            })
        ], out var problems);

        Assert.Equal(4, problems.Count);
        Assert.Contains(problems, p => p.Contains("Nowhere", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Missing", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("which has 1", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("no such property", StringComparison.Ordinal));

        // And the parameters still exist, so a broken curve does not take the rest of the mix with it.
        Assert.Equal(4, engine.Parameters!.Count);
    }

    [Fact]
    public void AMixerAssetCarriesItsParameters() {
        var problems = engine.LoadMixer(new MixerAsset {
            Buses = [
                new() { Name = "Music", GainDb = -6f },
                new() { Name = "Reverb" }
            ],
            Parameters = [
                new() {
                    Name = "storm",
                    Automation = [
                        new() {
                            Bus = "Music",
                            Target = AudioBusParameterTarget.GainDb,
                            Curve = new() { Points = [new() { Position = 0f }, new() { Position = 1f, Value = -18f }] }
                        }
                    ]
                }
            ]
        });

        Assert.Empty(problems);
        Assert.NotNull(engine.Parameters);
        Assert.Equal(0, engine.Parameters.IndexOf("storm"));

        engine.Parameters.Set("storm", 1f);
        engine.Update(0f);

        Assert.Equal(0.1259f, engine.FindBus("Music")!.ParameterGain, 1e-3f);
    }

    /// <summary>All the way through to what came out, which is the only claim that really counts.</summary>
    [Fact]
    public void ADrivenBusIsActuallyQuieter() {
        var music = engine.CreateBus("Music");

        engine.LoadParameters([
            Parameter("storm", 0f, new AudioBusAutomation {
                Bus = "Music",
                Target = AudioBusParameterTarget.GainDb,
                Curve = AudioCurve.Ramp(0f, -20f)
            })
        ], out _);

        engine.Play(AudioTestData.Constant(48_000, 1f), new PlaybackSettings { Bus = music.Index });
        engine.Update(0f);

        var loud = AudioTestData.Peak(AudioTestData.Render(device, 64));

        engine.Parameters!.Set("storm", 1f);
        engine.Update(0f);

        var quiet = AudioTestData.Peak(AudioTestData.Render(device, 64));

        Assert.True(loud > 0.6f, $"was {loud:F3}");
        Assert.True(quiet < loud * 0.2f, $"loud {loud:F3}, quiet {quiet:F3}");
    }
}

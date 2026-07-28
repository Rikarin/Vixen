// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Assets;
using Vixen.Audio.Effects;
using Vixen.Audio.Mixing;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>
///     The authoring layer: a mix that lives in an asset can be changed without a programmer, and one
///     that lives in C# cannot.
/// </summary>
public sealed class MixerAssetTests {
    static MixerAsset Typical() => new() {
        Buses = [
            new MixerBusAsset { Name = "World" },
            new MixerBusAsset { Name = "Ambience", Parent = "World", GainDb = -6f },
            new MixerBusAsset { Name = "Dialogue", GainDb = 0f },
            new MixerBusAsset {
                Name = "Music",
                GainDb = -3f,
                Sidechain = "Dialogue",
                Effects = [
                    new CompressorEffectAsset { ThresholdDb = -40f, Ratio = 20f, KneeDb = 0f, AttackSeconds = 0f }
                ]
            },
            new MixerBusAsset {
                Name = "Reverb",
                Effects = [new ReverbEffectAsset { Wet = 1f, Dry = 0f }]
            }
        ],
        Snapshots = [
            new MixerSnapshotAsset {
                Name = "Underwater",
                Buses = [
                    new SnapshotBusAsset { Bus = "Music", GainDb = -24f },
                    new SnapshotBusAsset { Bus = "Ambience", GainDb = -40f }
                ],
                Sends = [new SnapshotSendAsset { Bus = "Ambience", Target = "Reverb", LevelDb = -3f }]
            }
        ]
    };

    [Fact]
    public void ItBuildsTheBusesTheAssetDeclares() {
        var (engine, _) = AudioTestData.Engine();
        using var __ = engine;

        var problems = engine.LoadMixer(Typical());

        Assert.Empty(problems);
        Assert.NotNull(engine.FindBus("World"));
        Assert.NotNull(engine.FindBus("Music"));
        Assert.Equal(Decibels.ToLinear(-3f), engine.FindBus("Music")!.Gain, 1e-5f);
    }

    /// <summary>
    ///     A file full of <c>0.7943282</c> where somebody meant −2 dB is a file nobody can edit.
    /// </summary>
    [Fact]
    public void GainsAreDecibelsInTheAssetAndLinearInTheMixer() {
        var (engine, _) = AudioTestData.Engine();
        using var __ = engine;

        engine.LoadMixer(new MixerAsset { Buses = [new MixerBusAsset { Name = "Quiet", GainDb = -6.0206f }] });

        Assert.Equal(0.5f, engine.FindBus("Quiet")!.Gain, 1e-4f);
    }

    [Fact]
    public void ABusIsParentedEvenWhenItsParentIsDeclaredAfterIt() {
        var (engine, _) = AudioTestData.Engine();
        using var __ = engine;

        var problems = engine.LoadMixer(new MixerAsset {
            Buses = [
                new MixerBusAsset { Name = "Child", Parent = "Parent" },
                new MixerBusAsset { Name = "Parent" }
            ]
        });

        Assert.Empty(problems);
        Assert.Equal("Parent", engine.FindBus("Child")!.Parent!.Name);
        Assert.Equal(2, engine.FindBus("Child")!.Depth);
    }

    [Fact]
    public void TheEffectsAreBuiltAndAttached() {
        var (engine, _) = AudioTestData.Engine();
        using var __ = engine;

        engine.LoadMixer(Typical());

        var music = engine.FindBus("Music")!;
        Assert.Single(music.Effects);
        Assert.IsType<CompressorEffect>(music.Effects[0]);
        Assert.Equal(-40f, ((CompressorEffect)music.Effects[0]).ThresholdDb);
        Assert.Equal("Dialogue", music.SidechainSource!.Name);
    }

    [Fact]
    public void EveryEffectAssetBuildsItsEffect() {
        var assets = new IAudioEffectAsset[] {
            new FilterEffectAsset(),
            new EqualizerEffectAsset { Bands = [new EqualizerBandAsset(), new EqualizerBandAsset()] },
            new ReverbEffectAsset(),
            new DelayEffectAsset(),
            new CompressorEffectAsset(),
            new LimiterEffectAsset(),
            new ModulatedDelayEffectAsset { Kind = ModulatedDelayKind.Flanger },
            new PhaserEffectAsset(),
            new DistortionEffectAsset { Curve = DistortionCurve.Foldback },
            new BitCrusherEffectAsset { Bits = 4f },
            new PitchShiftEffectAsset { Semitones = -5f },
            new SpectrumAnalyzerEffectAsset { Size = 512 }
        };

        var built = assets.Select(asset => asset.Create()).ToArray();

        Assert.IsType<BiquadFilterEffect>(built[0]);
        Assert.IsType<EqualizerEffect>(built[1]);
        Assert.Equal(2, ((EqualizerEffect)built[1]).Bands.Count);
        Assert.IsType<ReverbEffect>(built[2]);
        Assert.IsType<DelayEffect>(built[3]);
        Assert.IsType<CompressorEffect>(built[4]);
        Assert.IsType<LimiterEffect>(built[5]);
        Assert.Equal(ModulatedDelayKind.Flanger, Assert.IsType<ModulatedDelayEffect>(built[6]).Kind);
        Assert.IsType<PhaserEffect>(built[7]);
        Assert.Equal(DistortionCurve.Foldback, Assert.IsType<DistortionEffect>(built[8]).Curve);
        Assert.Equal(4f, Assert.IsType<BitCrusherEffect>(built[9]).Bits);
        Assert.Equal(-5f, Assert.IsType<PitchShiftEffect>(built[10]).Semitones);
        Assert.Equal(512, Assert.IsType<SpectrumAnalyzerEffect>(built[11]).Size);
    }

    /// <summary>
    ///     A mixer asset is content. A level whose ambience bus lost its reverb send should still be
    ///     playable while somebody works out why.
    /// </summary>
    [Fact]
    public void AnUnknownNameIsReportedRatherThanThrown() {
        var (engine, _) = AudioTestData.Engine();
        using var __ = engine;

        var problems = engine.LoadMixer(new MixerAsset {
            Buses = [
                new MixerBusAsset {
                    Name = "Ambience",
                    Sidechain = "NotThere",
                    Sends = [new MixerSendAsset { Target = "AlsoNotThere" }]
                }
            ],
            DefaultSnapshot = "Missing"
        });

        Assert.Equal(3, problems.Count);
        Assert.NotNull(engine.FindBus("Ambience"));
        Assert.Null(engine.FindBus("Ambience")!.SidechainSource);
    }

    [Fact]
    public void ADuplicateBusNameIsReportedAndTheExistingOneKept() {
        var (engine, _) = AudioTestData.Engine();
        using var __ = engine;

        engine.CreateBus("Music");
        var problems = engine.LoadMixer(new MixerAsset {
            Buses = [new MixerBusAsset { Name = "Music", GainDb = -20f }]
        });

        Assert.Single(problems);
        Assert.Equal(1f, engine.FindBus("Music")!.Gain);
    }

    [Fact]
    public void ASnapshotMovesTheBusesItNamesAndLeavesTheRest() {
        var (engine, _) = AudioTestData.Engine();
        using var __ = engine;

        engine.LoadMixer(Typical());
        var music = engine.FindBus("Music")!;
        var dialogue = engine.FindBus("Dialogue")!;

        Assert.True(engine.Snapshots!.TransitionTo("Underwater", TimeSpan.Zero));

        Assert.Equal(Decibels.ToLinear(-24f), music.Gain, 1e-4f);
        Assert.Equal(1f, dialogue.Gain, 1e-5f);
        Assert.Equal("Underwater", engine.Snapshots.Current);
    }

    [Fact]
    public void ASnapshotTransitionTakesTheTimeItIsGiven() {
        var (engine, _) = AudioTestData.Engine();
        using var __ = engine;

        engine.LoadMixer(Typical());
        var music = engine.FindBus("Music")!;

        engine.Snapshots!.TransitionTo("Underwater", TimeSpan.FromSeconds(1));
        Assert.True(engine.Snapshots.IsTransitioning);

        engine.Update(0.5f);

        // Halfway in decibels between −3 and −24, which is −13.5 — not halfway in amplitude.
        Assert.Equal(Decibels.ToLinear(-13.5f), music.Gain, 1e-3f);

        engine.Update(0.5f);
        Assert.Equal(Decibels.ToLinear(-24f), music.Gain, 1e-4f);
        Assert.False(engine.Snapshots.IsTransitioning);
    }

    [Fact]
    public void ASnapshotMovesSendLevelsToo() {
        var (engine, _) = AudioTestData.Engine();
        using var __ = engine;

        engine.LoadMixer(Typical());
        var ambience = engine.FindBus("Ambience")!;
        var send = ambience.AddSend(engine.FindBus("Reverb")!, 0f);

        engine.Snapshots!.TransitionTo("Underwater", TimeSpan.Zero);

        Assert.Equal(Decibels.ToLinear(-3f), send.Level, 1e-4f);
    }

    /// <summary>
    ///     A snapshot name comes from an asset somebody edited. A level that asks for one the mixer
    ///     was rebuilt without should keep playing with the mix it has.
    /// </summary>
    [Fact]
    public void AnUnknownSnapshotIsRefusedRatherThanThrown() {
        var (engine, _) = AudioTestData.Engine();
        using var __ = engine;

        engine.LoadMixer(Typical());

        Assert.False(engine.Snapshots!.TransitionTo("Nowhere", TimeSpan.Zero));
        Assert.Null(engine.Snapshots.Current);
        Assert.True(engine.Snapshots.Has("Underwater"));
    }

    [Fact]
    public void ATransitionStartsFromWhereThingsAreAndNotFromThePreviousSnapshot() {
        var (engine, _) = AudioTestData.Engine();
        using var __ = engine;

        engine.LoadMixer(new MixerAsset {
            Buses = [new MixerBusAsset { Name = "Music" }],
            Snapshots = [
                new MixerSnapshotAsset {
                    Name = "Quiet",
                    Buses = [new SnapshotBusAsset { Bus = "Music", GainDb = -60f }]
                },
                new MixerSnapshotAsset {
                    Name = "Loud",
                    Buses = [new SnapshotBusAsset { Bus = "Music", GainDb = 0f }]
                }
            ]
        });

        var music = engine.FindBus("Music")!;

        engine.Snapshots!.TransitionTo("Quiet", TimeSpan.FromSeconds(1));
        engine.Update(0.5f);
        var interrupted = music.Gain;

        engine.Snapshots.TransitionTo("Loud", TimeSpan.FromSeconds(1));
        engine.Update(0f);

        // No jump back to where "Quiet" would have ended.
        Assert.Equal(interrupted, music.Gain, 1e-5f);
    }

    [Fact]
    public void TheDefaultSnapshotIsAppliedOnLoad() {
        var (engine, _) = AudioTestData.Engine();
        using var __ = engine;

        var problems = engine.LoadMixer(Typical() with { DefaultSnapshot = "Underwater" });

        Assert.Empty(problems);
        Assert.Equal(Decibels.ToLinear(-24f), engine.FindBus("Music")!.Gain, 1e-4f);
    }

    /// <summary>Two things driving one gain is a fader that will not stay where it is put.</summary>
    [Fact]
    public void ASnapshotCancelsAManualFadeOnABusItNames() {
        var (engine, _) = AudioTestData.Engine();
        using var __ = engine;

        engine.LoadMixer(Typical());
        var music = engine.FindBus("Music")!;

        music.FadeTo(0f, TimeSpan.FromSeconds(5));
        Assert.True(music.IsFading);

        engine.Snapshots!.TransitionTo("Underwater", TimeSpan.Zero);

        Assert.False(music.IsFading);
        Assert.Equal(Decibels.ToLinear(-24f), music.Gain, 1e-4f);
    }

    [Fact]
    public void AMixerBuiltFromAnAssetStillDucks() {
        var (engine, device) = AudioTestData.Engine(channels: 1, voices: 4);
        using var _ = engine;

        engine.LoadMixer(Typical());
        var music = engine.FindBus("Music")!;
        var dialogue = engine.FindBus("Dialogue")!;

        engine.Play(AudioTestData.Constant(48_000, 0.5f), new PlaybackSettings {
            Gain = 1f,
            Pitch = 1f,
            Bus = music.Index
        });

        AudioTestData.Render(device, 256);
        var undisturbed = music.PeakLevel;

        engine.Play(AudioTestData.Constant(48_000, 0.9f), new PlaybackSettings {
            Gain = 1f,
            Pitch = 1f,
            Bus = dialogue.Index
        });

        AudioTestData.Render(device, 512);

        Assert.True(music.PeakLevel < undisturbed * 0.25f);
    }
}

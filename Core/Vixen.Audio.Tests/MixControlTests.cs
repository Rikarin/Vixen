// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Devices;
using Vixen.Audio.Diagnostics;
using Vixen.Audio.Effects;
using Vixen.Audio.Mixing;
using Vixen.Audio.Parameters;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>Every knob in the mix, by name — what a live-update session talks to.</summary>
public sealed class MixControlTests : IDisposable {
    readonly AudioEngine engine;
    readonly NullAudioDevice device;

    public MixControlTests() => (engine, device) = AudioTestData.Engine();

    public void Dispose() => engine.Dispose();

    [Fact]
    public void ABusFaderIsReadAndWrittenInDecibels() {
        var music = engine.CreateBus("Music");
        music.Gain = 0.5f;

        Assert.True(engine.Control.TryGet("bus/Music/gain", out var read));
        Assert.Equal(-6.02f, read, 0.05f);

        Assert.True(engine.Control.TrySet("bus/Music/gain", -12f));
        Assert.Equal(0.2512f, music.Gain, 1e-3f);
    }

    /// <summary>A fader has to bottom out somewhere; pretending otherwise gives an editor a −∞ to render.</summary>
    [Fact]
    public void ASilentBusReadsAtTheBottomStopAndSettingItThereIsSilence() {
        var music = engine.CreateBus("Music");
        music.Gain = 0f;

        Assert.True(engine.Control.TryGet("bus/Music/gain", out var read));
        Assert.Equal(MixControl.SilenceDb, read);

        music.Gain = 1f;
        Assert.True(engine.Control.TrySet("bus/Music/gain", MixControl.SilenceDb));
        Assert.Equal(0f, music.Gain);
    }

    [Fact]
    public void AMuteIsZeroOrOne() {
        var music = engine.CreateBus("Music");

        Assert.True(engine.Control.TryGet("bus/Music/mute", out var read));
        Assert.Equal(0f, read);

        Assert.True(engine.Control.TrySet("bus/Music/mute", 1f));
        Assert.True(music.Muted);

        Assert.True(engine.Control.TrySet("bus/Music/mute", 0f));
        Assert.False(music.Muted);
    }

    [Fact]
    public void ASendIsNamedByWhatItSendsTo() {
        var reverb = engine.CreateBus("Reverb");
        var ambience = engine.CreateBus("Ambience");
        var send = ambience.AddSend(reverb, 1f);

        Assert.True(engine.Control.TrySet("bus/Ambience/send/Reverb", -6f));
        Assert.Equal(0.5012f, send.Level, 1e-3f);

        Assert.True(engine.Control.TryGet("bus/Ambience/send/Reverb", out var read));
        Assert.Equal(-6f, read, 0.05f);
    }

    /// <summary>Two reverbs on one bus differ only by where they sit, so position is identity here.</summary>
    [Fact]
    public void AnEffectKnobIsNamedByItsSlotAndItsOwnName() {
        var voice = engine.CreateBus("Voice");
        voice.AddEffect(new GateEffect());
        var filter = new BiquadFilterEffect { Frequency = 1_000f };
        voice.AddEffect(filter);

        Assert.True(engine.Control.TrySet("bus/Voice/effect/1/Frequency", 400f));
        Assert.Equal(400f, filter.Frequency);

        Assert.True(engine.Control.TryGet("bus/Voice/effect/1/Frequency", out var read));
        Assert.Equal(400f, read);

        // And the gate in slot zero is untouched, which is the whole point of the slot.
        Assert.True(engine.Control.TryGet("bus/Voice/effect/0/ThresholdDb", out var threshold));
        Assert.Equal(-45f, threshold);
    }

    [Fact]
    public void AParameterIsReachableTooAndReadsWhereItActuallyIs() {
        var music = engine.CreateBus("Music");

        engine.LoadParameters([
            new AudioBusParameterDefinition {
                Name = "rain",
                SeekSeconds = 1f,
                Automation = [
                    new AudioBusAutomation {
                        Bus = "Music",
                        Target = AudioBusParameterTarget.GainDb,
                        Curve = AudioCurve.Ramp(0f, -20f)
                    }
                ]
            }
        ], out _);

        Assert.True(engine.Control.TrySet("parameter/rain", 1f));

        // Half way through its seek, which is where it is rather than where it was pointed.
        engine.Update(0.5f);
        Assert.True(engine.Control.TryGet("parameter/rain", out var read));
        Assert.Equal(0.5f, read, 1e-3f);
        Assert.True(music.ParameterGain > 0.1f);
    }

    [Fact]
    public void APathThatNamesNothingIsRefusedRatherThanThrowing() {
        engine.CreateBus("Music").AddEffect(new ReverbEffect());

        Assert.False(engine.Control.TryGet("bus/Nowhere/gain", out _));
        Assert.False(engine.Control.TrySet("bus/Nowhere/gain", 0f));
        Assert.False(engine.Control.TryGet("bus/Music/send/Nothing", out _));
        Assert.False(engine.Control.TryGet("bus/Music/effect/9/Wet", out _));
        Assert.False(engine.Control.TryGet("bus/Music/effect/0/Nonsense", out _));
        Assert.False(engine.Control.TryGet("bus/Music/effect/x/Wet", out _));
        Assert.False(engine.Control.TryGet("parameter/nothing", out _));
        Assert.False(engine.Control.TryGet("nonsense", out _));
        Assert.False(engine.Control.TryGet(string.Empty, out _));
        Assert.False(engine.Control.TrySet("bus/Music/gain/extra", 0f));
    }

    /// <summary>What an editor asks for when it connects, so it knows what to draw.</summary>
    [Fact]
    public void EverythingIsEnumerableWithItsCurrentValue() {
        var reverb = engine.CreateBus("Reverb");
        reverb.AddEffect(new ReverbEffect { Wet = 0.7f });
        var music = engine.CreateBus("Music");
        music.Gain = 0.5f;
        music.AddSend(reverb, 0.25f);

        engine.LoadParameters([
            new AudioBusParameterDefinition { Name = "rain", Minimum = 0f, Maximum = 2f, Default = 0.5f }
        ], out _);

        var controls = engine.Control.Enumerate();

        var fader = controls.Single(c => c.Path == "bus/Music/gain");
        Assert.Equal(MixControlKind.BusGain, fader.Kind);
        Assert.Equal(-6.02f, fader.Value, 0.05f);

        var send = controls.Single(c => c.Path == "bus/Music/send/Reverb");
        Assert.Equal(MixControlKind.SendLevel, send.Kind);
        Assert.Equal(-12.04f, send.Value, 0.05f);

        var wet = controls.Single(c => c.Path == "bus/Reverb/effect/0/Wet");
        Assert.Equal(MixControlKind.EffectProperty, wet.Kind);
        Assert.Equal(0.7f, wet.Value);

        var parameter = controls.Single(c => c.Path == "parameter/rain");
        Assert.Equal(MixControlKind.Parameter, parameter.Kind);
        Assert.Equal(0.5f, parameter.Value);
        Assert.Equal(0f, parameter.Minimum);
        Assert.Equal(2f, parameter.Maximum);

        // Including the master, which is a bus like any other.
        Assert.Contains(controls, c => c.Path == "bus/Master/gain");
    }

    /// <summary>Round-tripping every path an editor could have been given.</summary>
    [Fact]
    public void EveryEnumeratedPathCanBeReadBack() {
        var reverb = engine.CreateBus("Reverb");
        reverb.AddEffect(new ReverbEffect());
        reverb.AddEffect(new DelayEffect());
        var music = engine.CreateBus("Music");
        music.AddSend(reverb, 0.5f);
        music.AddEffect(new CompressorEffect());

        engine.LoadParameters([new AudioBusParameterDefinition { Name = "rain" }], out _);

        foreach (var control in engine.Control.Enumerate()) {
            Assert.True(engine.Control.TryGet(control.Path, out var read), control.Path);
            Assert.Equal(control.Value, read, 1e-4f);
        }
    }

    /// <summary>
    ///     The three hand-written switches on every effect have to agree, and this is what makes drift
    ///     a failure rather than a surprise.
    /// </summary>
    [Fact]
    public void EveryEffectsDeclaredKnobsAreAcceptedByBothAccessors() {
        IAudioEffect[] effects = [
            new BiquadFilterEffect(),
            new BitCrusherEffect(),
            new CompressorEffect(),
            new ConvolutionReverbEffect(AudioTestData.Impulse(256)),
            new DelayEffect(),
            new DistortionEffect(),
            new GateEffect(),
            new LimiterEffect(),
            ModulatedDelayEffect.Chorus(),
            new PhaserEffect(),
            new PitchShiftEffect(),
            new ReverbEffect(),
            new SpectrumAnalyzerEffect()
        ];

        foreach (var effect in effects) {
            Assert.NotEmpty(effect.Properties);

            foreach (var property in effect.Properties) {
                var name = $"{effect.GetType().Name}.{property}";

                Assert.True(effect.TryGetProperty(property, out var before), name);
                Assert.True(effect.TrySetProperty(property, before + 1f), name);
                Assert.True(effect.TryGetProperty(property, out var after), name);
                Assert.Equal(before + 1f, after, 1e-4f);
            }

            Assert.False(effect.TryGetProperty("NotAKnob", out _));
            Assert.False(effect.TrySetProperty("NotAKnob", 1f));
        }
    }

    /// <summary>Because a mix whose shape changes underneath a running game is a different problem.</summary>
    [Fact]
    public void NothingHereCreatesOrRoutesAnything() {
        var before = engine.Mixer.Buses.Count;

        Assert.False(engine.Control.TrySet("bus/New/gain", 0f));
        Assert.Equal(before, engine.Mixer.Buses.Count);
    }
}

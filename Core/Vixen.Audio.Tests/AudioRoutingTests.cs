// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Effects;
using Vixen.Audio.Mixing;
using Vixen.Audio.Sources;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>Sends, sidechains, and the order the graph has to be rendered in.</summary>
public sealed class AudioRoutingTests {
    [Fact]
    public void ASendPutsACopyOfTheSignalSomewhereElse() {
        var (engine, device) = AudioTestData.Engine(channels: 1);
        using var _ = engine;

        var music = engine.CreateBus("Music");
        var aux = engine.CreateBus("Aux");
        music.AddSend(aux, 0.5f);

        engine.Play(AudioTestData.Constant(4_800, 0.4f), new PlaybackSettings {
            Gain = 1f,
            Pitch = 1f,
            Bus = music.Index
        });

        AudioTestData.Render(device, 64);

        // The copy does not take the place of the original: both buses carry the signal, and the
        // master gets the sum.
        Assert.Equal(0.4f, music.PeakLevel, 0.001f);
        Assert.Equal(0.2f, aux.PeakLevel, 0.001f);
        Assert.Equal(0.6f, engine.Master.PeakLevel, 0.001f);
    }

    /// <summary>
    ///     Pulling a bus's fader down should take its reverb with it, or the tail of a bus nobody can
    ///     hear keeps playing.
    /// </summary>
    [Fact]
    public void APostFaderSendFollowsTheFader() {
        var (engine, device) = AudioTestData.Engine(channels: 1);
        using var _ = engine;

        var music = engine.CreateBus("Music");
        var aux = engine.CreateBus("Aux");
        music.AddSend(aux);
        music.Gain = 0.25f;

        engine.Play(AudioTestData.Constant(4_800, 0.8f), new PlaybackSettings {
            Gain = 1f,
            Pitch = 1f,
            Bus = music.Index
        });

        AudioTestData.Render(device, 64);

        Assert.Equal(0.2f, aux.PeakLevel, 0.001f);
    }

    [Fact]
    public void APreFaderSendIgnoresIt() {
        var (engine, device) = AudioTestData.Engine(channels: 1);
        using var _ = engine;

        var music = engine.CreateBus("Music");
        var aux = engine.CreateBus("Aux");
        music.AddSend(aux, 1f, preFader: true);
        music.Gain = 0f;

        engine.Play(AudioTestData.Constant(4_800, 0.8f), new PlaybackSettings {
            Gain = 1f,
            Pitch = 1f,
            Bus = music.Index
        });

        AudioTestData.Render(device, 64);

        Assert.Equal(0f, music.PeakLevel, 0.001f);
        Assert.Equal(0.8f, aux.PeakLevel, 0.001f);
    }

    /// <summary>
    ///     With only parent edges, "deepest first" is a correct order for free. A send is an edge
    ///     that does not follow the tree, and depth says nothing useful about it.
    /// </summary>
    [Fact]
    public void ASendFromADeepBusToAShallowOneStillArrivesInTime() {
        var (engine, device) = AudioTestData.Engine(channels: 1);
        using var _ = engine;

        var aux = engine.CreateBus("Aux");
        var world = engine.CreateBus("World");
        var room = engine.CreateBus("Room", world);
        var ambience = engine.CreateBus("Ambience", room);
        ambience.AddSend(aux, 0.5f);

        engine.Play(AudioTestData.Constant(4_800, 0.6f), new PlaybackSettings {
            Gain = 1f,
            Pitch = 1f,
            Bus = ambience.Index
        });

        AudioTestData.Render(device, 64);

        // Depth-ordered, Aux (depth 1) would have been finished before Ambience (depth 3) filled it,
        // and this would be zero.
        Assert.Equal(0.3f, aux.PeakLevel, 0.001f);
    }

    [Fact]
    public void ASendThatWouldLoopIsRefused() {
        var (engine, _) = AudioTestData.Engine();
        using var __ = engine;

        var a = engine.CreateBus("A");
        var b = engine.CreateBus("B");
        b.AddSend(a);

        Assert.Throws<ArgumentException>(() => a.AddSend(b));
        Assert.Throws<ArgumentException>(() => a.AddSend(a));

        // A child already reaches its parent, so this is the same loop written differently.
        var child = engine.CreateBus("Child", a);
        Assert.Throws<ArgumentException>(() => a.AddSend(child));
    }

    [Fact]
    public void ASendCanBeTakenOffAgain() {
        var (engine, device) = AudioTestData.Engine(channels: 1);
        using var _ = engine;

        var music = engine.CreateBus("Music");
        var aux = engine.CreateBus("Aux");
        var send = music.AddSend(aux);

        engine.Play(AudioTestData.Constant(4_800, 0.5f), new PlaybackSettings {
            Gain = 1f,
            Pitch = 1f,
            Bus = music.Index
        });

        AudioTestData.Render(device, 64);
        Assert.True(aux.PeakLevel > 0f);

        Assert.True(music.RemoveSend(send));
        Assert.False(music.RemoveSend(send));
        AudioTestData.Render(device, 64);

        Assert.Equal(0f, aux.PeakLevel);
    }

    /// <summary>
    ///     Ducking, which is the whole reason the sidechain exists: the music gets out of the way
    ///     whenever anybody speaks, without any gameplay code knowing the music bus exists.
    /// </summary>
    [Fact]
    public void MusicDucksUnderDialogue() {
        var (engine, device) = AudioTestData.Engine(channels: 1, voices: 4);
        using var _ = engine;

        var dialogue = engine.CreateBus("Dialogue");
        var music = engine.CreateBus("Music");

        music.SetSidechain(dialogue);
        music.AddEffect(new CompressorEffect {
            ThresholdDb = -40f,
            Ratio = 20f,
            KneeDb = 0f,
            AttackSeconds = 0f,
            ReleaseSeconds = 0.05f
        });

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
        var ducked = music.PeakLevel;

        Assert.Equal(0.5f, undisturbed, 0.01f);
        Assert.True(ducked < undisturbed * 0.25f, $"music went from {undisturbed:F3} to {ducked:F3}");
        Assert.True(dialogue.PeakLevel > 0.8f, "the dialogue itself must not be ducked");
    }

    [Fact]
    public void TheDuckingLetsGoWhenTheTalkingStops() {
        var (engine, device) = AudioTestData.Engine(channels: 1, voices: 4);
        using var _ = engine;

        var dialogue = engine.CreateBus("Dialogue");
        var music = engine.CreateBus("Music");
        music.SetSidechain(dialogue);
        music.AddEffect(new CompressorEffect {
            ThresholdDb = -40f,
            Ratio = 20f,
            KneeDb = 0f,
            AttackSeconds = 0f,
            ReleaseSeconds = 0.01f
        });

        engine.Play(AudioTestData.Constant(48_000, 0.5f), new PlaybackSettings {
            Gain = 1f,
            Pitch = 1f,
            Bus = music.Index
        });

        var speech = engine.Play(AudioTestData.Constant(48_000, 0.9f), new PlaybackSettings {
            Gain = 1f,
            Pitch = 1f,
            Bus = dialogue.Index
        });

        AudioTestData.Render(device, 512);
        Assert.True(music.PeakLevel < 0.2f);

        engine.Stop(speech);
        AudioTestData.Render(device, 4_800);

        Assert.Equal(0.5f, music.PeakLevel, 0.02f);
    }

    /// <summary>
    ///     The key has to have been rendered before the bus that listens to it, which is a constraint
    ///     on the order rather than a suggestion.
    /// </summary>
    [Fact]
    public void ASidechainFromDownstreamIsRefused() {
        var (engine, _) = AudioTestData.Engine();
        using var __ = engine;

        var music = engine.CreateBus("Music");

        // The master is where Music's own signal ends up, so keying off it would be keying off
        // something that has not been summed yet.
        Assert.Throws<ArgumentException>(() => music.SetSidechain(engine.Master));
        Assert.Throws<ArgumentException>(() => music.SetSidechain(music));
    }

    [Fact]
    public void AKeyedEffectOnAnUnkeyedBusIsAnOrdinaryOne() {
        var (engine, device) = AudioTestData.Engine(channels: 1);
        using var _ = engine;

        var music = engine.CreateBus("Music");
        music.AddEffect(new CompressorEffect {
            ThresholdDb = -40f,
            Ratio = 20f,
            KneeDb = 0f,
            AttackSeconds = 0f
        });

        engine.Play(AudioTestData.Constant(48_000, 0.9f), new PlaybackSettings {
            Gain = 1f,
            Pitch = 1f,
            Bus = music.Index
        });

        AudioTestData.Render(device, 512);

        // Keyed by its own signal, which is what a compressor with nothing to listen to should be.
        Assert.True(music.PeakLevel < 0.2f);
    }

    /// <summary>
    ///     The arrangement a voice-chat session actually needs. Effects live on buses, not on voices,
    ///     so "some players are underwater" is two buses rather than a per-player effect chain — one
    ///     bus per <em>environment</em>, which is how a mixer is meant to be used.
    /// </summary>
    [Fact]
    public void AnUnderwaterPlayerIsMuffledAndTheDryOneIsNot() {
        var (engine, device) = AudioTestData.Engine(channels: 1, voices: 4);
        using var _ = engine;

        var dry = engine.CreateBus("Voice");
        var submerged = engine.CreateBus("VoiceUnderwater");
        var wet = engine.CreateBus("UnderwaterVerb");

        submerged.AddEffect(new BiquadFilterEffect { Kind = BiquadFilterKind.LowPass, Frequency = 400f });
        submerged.AddSend(wet, 0.5f);
        wet.AddEffect(new ReverbEffect { Wet = 1f, Dry = 0f, RoomSize = 0.9f });

        var above = new LiveSampleProvider(AudioFormat.Mono48k);
        var below = new LiveSampleProvider(AudioFormat.Mono48k);

        // Both players say the same bright thing. Pushed before the voices start, which is what a
        // jitter buffer is for: a voice started against an empty one plays the silence it found.
        var packet = new float[4_096];

        for (var i = 0; i < packet.Length; i++) {
            packet[i] = 0.5f * MathF.Sin(2f * MathF.PI * 6_000f * i / 48_000f);
        }

        above.Write(packet);
        below.Write(packet);

        engine.Play(above, new PlaybackSettings { Gain = 1f, Pitch = 1f, Bus = dry.Index });
        engine.Play(below, new PlaybackSettings { Gain = 1f, Pitch = 1f, Bus = submerged.Index });
        AudioTestData.Render(device, 1_024);

        Assert.True(dry.PeakLevel > 0.4f, $"the dry voice came out at {dry.PeakLevel:F3}");
        Assert.True(
            submerged.PeakLevel < dry.PeakLevel * 0.2f,
            $"6 kHz through a 400 Hz low-pass came out at {submerged.PeakLevel:F3} against {dry.PeakLevel:F3}"
        );

        // The reverb's shortest comb is over 1 200 samples long at 48 kHz, so its first output
        // arrives well after the signal that caused it. Peak is a per-block figure, so the tail has
        // to be watched for rather than sampled once.
        var tail = 0f;

        for (var block = 0; block < 8; block++) {
            AudioTestData.Render(device, 512);
            tail = MathF.Max(tail, wet.PeakLevel);
        }

        Assert.True(tail > 0f, "the underwater reverb got nothing");
    }
}

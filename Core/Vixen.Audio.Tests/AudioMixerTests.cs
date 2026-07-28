// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Mixing;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>
///     docs/plan/12 says audio correctness is tested at buffer level. This is that: every claim here
///     is an assertion about numbers <c>AudioMixer.Render</c> produced.
/// </summary>
public sealed class AudioMixerTests {
    [Fact]
    public void AMonoClipReachesBothSpeakersAtEqualPower() {
        var (engine, device) = AudioTestData.Engine();
        using var _ = engine;

        engine.Play(AudioTestData.Constant(256, 1f));
        var rendered = AudioTestData.Render(device, 16);

        // Constant power: one source at unity across two speakers is 0.707 in each, not 0.5 and not
        // 1 — which is what stops a sound dipping as it crosses the centre.
        Assert.Equal(0.7071f, rendered[0], 0.001f);
        Assert.Equal(0.7071f, rendered[1], 0.001f);
    }

    [Fact]
    public void AStereoClipPlaysAtUnityWhenItIsNotPanned() {
        var (engine, device) = AudioTestData.Engine();
        using var _ = engine;

        engine.Play(AudioTestData.Constant(256, 0.5f, channels: 2));
        var rendered = AudioTestData.Render(device, 16);

        // A source whose channels already match the output is balanced, not panned. Equal-power
        // panning would put this at 0.354 — quieter than the same file with no pan control at all.
        Assert.Equal(0.5f, rendered[0], 0.001f);
        Assert.Equal(0.5f, rendered[1], 0.001f);
    }

    [Fact]
    public void ABusGainScalesEverythingRoutedIntoIt() {
        var (engine, device) = AudioTestData.Engine();
        using var _ = engine;

        var music = engine.CreateBus("Music");
        music.Gain = 0.25f;
        engine.Play(AudioTestData.Constant(256, 1f), new PlaybackSettings { Gain = 1f, Bus = music.Index });

        var rendered = AudioTestData.Render(device, 16);

        Assert.Equal(0.7071f * 0.25f, rendered[0], 0.001f);
    }

    [Fact]
    public void ABusGainCompoundsWithItsParents() {
        var (engine, device) = AudioTestData.Engine();
        using var _ = engine;

        var music = engine.CreateBus("Music");
        var stinger = engine.CreateBus("Stinger", music);
        music.Gain = 0.5f;
        stinger.Gain = 0.5f;
        engine.Master.Gain = 0.5f;

        engine.Play(AudioTestData.Constant(256, 1f), new PlaybackSettings { Gain = 1f, Bus = stinger.Index });
        var rendered = AudioTestData.Render(device, 16);

        Assert.Equal(0.7071f * 0.125f, rendered[0], 0.001f);
    }

    /// <summary>
    ///     Muting is not pausing. A game that muted the music during a cutscene expects the track to
    ///     be where it should be when the cutscene ends.
    /// </summary>
    [Fact]
    public void AMutedBusIsSilentAndItsVoicesKeepPlaying() {
        var (engine, device) = AudioTestData.Engine();
        using var _ = engine;

        var music = engine.CreateBus("Music");
        music.Muted = true;
        var handle = engine.Play(AudioTestData.Constant(4_800, 1f), new PlaybackSettings {
            Gain = 1f,
            Bus = music.Index
        });

        var rendered = AudioTestData.Render(device, 64);

        Assert.Equal(0f, AudioTestData.Peak(rendered));
        Assert.True(engine.IsPlaying(handle));
    }

    [Fact]
    public void TheMasterClampsRatherThanLettingABackendWrapIt() {
        var (engine, device) = AudioTestData.Engine();
        using var _ = engine;

        for (var i = 0; i < 6; i++) {
            engine.Play(AudioTestData.Constant(256, 1f), new PlaybackSettings { Gain = 1f });
        }

        var rendered = AudioTestData.Render(device, 16);

        Assert.Equal(1f, rendered[0], 0.0001f);
        Assert.Equal(1f, rendered[1], 0.0001f);
    }

    [Fact]
    public void AVoiceEndsWhenItsClipDoesAndComesBackToThePool() {
        var (engine, device) = AudioTestData.Engine(voices: 2);
        using var _ = engine;

        var handle = engine.Play(AudioTestData.Constant(32, 1f));
        Assert.True(engine.IsPlaying(handle));

        AudioTestData.Render(device, 64);
        engine.Update();

        Assert.False(engine.IsPlaying(handle));
        Assert.Equal(VoiceState.Free, engine.StateOf(handle));
    }

    [Fact]
    public void ALoopingClipDoesNotEnd() {
        var (engine, device) = AudioTestData.Engine();
        using var _ = engine;

        var handle = engine.Play(AudioTestData.Constant(32, 1f), new PlaybackSettings {
            Gain = 1f,
            Pitch = 1f,
            Loop = true
        });

        var rendered = AudioTestData.Render(device, 256);
        engine.Update();

        Assert.True(engine.IsPlaying(handle));
        Assert.Equal(0.7071f, rendered[^2], 0.001f);
    }

    /// <summary>
    ///     A handle is an index and a generation, so a footstep that has finished cannot stop the
    ///     explosion that got its slot.
    /// </summary>
    [Fact]
    public void AStaleHandleTouchesNothing() {
        var (engine, device) = AudioTestData.Engine(voices: 1);
        using var _ = engine;

        var first = engine.Play(AudioTestData.Constant(16, 1f));
        AudioTestData.Render(device, 64);
        engine.Update();

        var second = engine.Play(AudioTestData.Constant(4_800, 1f));
        Assert.Equal(first.Index, second.Index);
        Assert.NotEqual(first.Generation, second.Generation);

        engine.Stop(first);
        AudioTestData.Render(device, 16);

        Assert.True(engine.IsPlaying(second));
    }

    [Fact]
    public void APoolWithNothingFreeDropsTheRequestAndSaysSo() {
        var (engine, device) = AudioTestData.Engine(voices: 2);
        using var _ = engine;

        engine.Play(AudioTestData.Constant(4_800, 1f));
        engine.Play(AudioTestData.Constant(4_800, 1f));
        var dropped = engine.Play(AudioTestData.Constant(4_800, 1f));

        AudioTestData.Render(device, 16);
        engine.Update();

        Assert.False(dropped.IsValid);
        Assert.Equal(1, engine.Statistics.DroppedRequests);
        Assert.Equal(2, engine.Statistics.ActiveVoices);
    }

    /// <summary>
    ///     A stop that cut the waveform off mid-cycle would be a step, and a step is a click. One
    ///     block of ramp costs nothing and removes the whole class of complaint.
    /// </summary>
    [Fact]
    public void StoppingFadesOverOneBlockRatherThanCutting() {
        var (engine, device) = AudioTestData.Engine(channels: 1, bufferFrames: 16);
        using var _ = engine;

        var handle = engine.Play(AudioTestData.Constant(4_800, 1f));
        AudioTestData.Render(device, 16);
        engine.Stop(handle);

        var fading = AudioTestData.Render(device, 16);

        Assert.True(fading[0] > fading[8]);
        Assert.True(fading[8] > fading[15]);
        Assert.Equal(0f, fading[15], 0.1f);

        engine.Update();
        Assert.False(engine.IsPlaying(handle));
    }

    [Fact]
    public void APausedVoiceProducesSilenceAndKeepsItsPlace() {
        var (engine, device) = AudioTestData.Engine(channels: 1);
        using var _ = engine;

        var handle = engine.Play(AudioTestData.Ramp(4_800));
        AudioTestData.Render(device, 8);
        engine.Pause(handle);

        var paused = AudioTestData.Render(device, 8);
        Assert.Equal(0f, AudioTestData.Peak(paused));

        engine.Resume(handle);
        var resumed = AudioTestData.Render(device, 8);

        // It carried on from frame 8 rather than restarting or skipping the paused frames.
        Assert.Equal(8 * AudioTestData.RampStep, resumed[0], 1e-5f);
    }

    /// <summary>
    ///     A clip at a different rate to the device is resampled per voice, because two clips at two
    ///     rates can be playing at once and only the voice knows which is which.
    /// </summary>
    [Fact]
    public void AClipAtHalfTheDeviceRateTakesTwiceAsLong() {
        var (engine, device) = AudioTestData.Engine(channels: 1, sampleRate: 48_000);
        using var _ = engine;

        engine.Play(AudioTestData.Ramp(8, sampleRate: 24_000));
        var rendered = AudioTestData.Render(device, 16);

        // Frame n of the output is source frame n/2, interpolated: 0, 0.5, 1, 1.5, …
        Assert.Equal(0f, rendered[0], 1e-5f);
        Assert.Equal(0.5f * AudioTestData.RampStep, rendered[1], 1e-5f);
        Assert.Equal(1f * AudioTestData.RampStep, rendered[2], 1e-5f);
        Assert.Equal(3f * AudioTestData.RampStep, rendered[6], 1e-5f);
    }

    [Fact]
    public void PitchIsPlaybackRateAndNotTimeStretch() {
        var (engine, device) = AudioTestData.Engine(channels: 1);
        using var _ = engine;

        engine.Play(AudioTestData.Ramp(64), new PlaybackSettings { Gain = 1f, Pitch = 2f });
        var rendered = AudioTestData.Render(device, 8);

        Assert.Equal(0f, rendered[0], 1e-5f);
        Assert.Equal(2f * AudioTestData.RampStep, rendered[1], 1e-5f);
        Assert.Equal(4f * AudioTestData.RampStep, rendered[2], 1e-5f);
    }

    [Fact]
    public void ValidateFindsNothingWrongWithAWorkingEngine() {
        var (engine, device) = AudioTestData.Engine();
        using var _ = engine;

        engine.CreateBus("Music");
        engine.Play(AudioTestData.Constant(256, 1f));
        AudioTestData.Render(device, 16);

        Assert.Empty(engine.Validate());
    }

    [Fact]
    public void TwoBusesCannotShareAName() {
        var (engine, _) = AudioTestData.Engine();
        using var __ = engine;

        engine.CreateBus("Music");

        Assert.Throws<ArgumentException>(() => engine.CreateBus("Music"));
    }

    [Fact]
    public void TheDeviceIsNotStartedTwice() {
        var (engine, device) = AudioTestData.Engine();
        using var _ = engine;

        Assert.True(device.IsRunning);
        engine.Start();

        Assert.True(device.IsRunning);
    }
}

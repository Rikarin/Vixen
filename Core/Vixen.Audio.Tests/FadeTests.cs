// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Effects;
using Vixen.Audio.Mixing;
using Xunit;

namespace Vixen.Audio.Tests;

public sealed class FadeTests {
    static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    [Fact]
    public void ALinearFadeIsHalfwayInAmplitudeHalfwayThrough() {
        Assert.Equal(0.5f, AudioFade.Evaluate(1f, 0f, 0.5f, AudioFadeCurve.Linear), 1e-5f);
    }

    /// <summary>
    ///     Loudness is roughly logarithmic, so half the amplitude is nowhere near half as loud. A
    ///     linear fade-out sounds like nothing happening followed by the sound falling off a cliff.
    /// </summary>
    [Fact]
    public void ADecibelFadeIsHalfwayInDecibelsInstead() {
        // Unity to the −80 dB floor: halfway is −40 dB, which is a hundredth of the amplitude.
        Assert.Equal(Decibels.ToLinear(-40f), AudioFade.Evaluate(1f, 0f, 0.5f), 1e-4f);
    }

    [Fact]
    public void AFadeLandsExactlyOnItsTarget() {
        Assert.Equal(0f, AudioFade.Evaluate(1f, 0f, 1f));
        Assert.Equal(1f, AudioFade.Evaluate(0f, 1f, 1f));
        Assert.Equal(0.25f, AudioFade.Evaluate(1f, 0.25f, 1f, AudioFadeCurve.Linear));
    }

    [Fact]
    public void AVoiceFadeReachesItsTargetOverTheTimeItWasGiven() {
        var (engine, _) = AudioTestData.Engine(channels: 1);
        using var __ = engine;

        var handle = engine.Play(AudioTestData.Constant(48_000, 1f));
        engine.FadeTo(handle, 0.25f, OneSecond, AudioFadeCurve.Linear);

        engine.Update(0.5f);
        Assert.Equal(0.625f, GainOf(engine, handle), 0.01f);
        Assert.True(engine.IsFading(handle));

        engine.Update(0.5f);
        Assert.Equal(0.25f, GainOf(engine, handle), 1e-4f);
        Assert.False(engine.IsFading(handle));
    }

    [Fact]
    public void AFadeOutStopsTheSoundWhenItArrives() {
        var (engine, device) = AudioTestData.Engine(channels: 1);
        using var _ = engine;

        var handle = engine.Play(AudioTestData.Constant(48_000, 1f));
        engine.FadeOutAndStop(handle, TimeSpan.FromSeconds(0.5));

        engine.Update(0.25f);
        Assert.True(engine.IsPlaying(handle));

        engine.Update(0.25f);
        AudioTestData.Render(device, 128);
        engine.Update(1f / 60f);

        Assert.False(engine.IsPlaying(handle));
    }

    /// <summary>
    ///     A fade is a gain moving over hundreds of milliseconds, stepped once a frame and smoothed
    ///     across each block by the ramp the voice already applies. This is that, end to end.
    /// </summary>
    [Fact]
    public void TheFadeIsAudibleInTheBuffer() {
        var (engine, device) = AudioTestData.Engine(channels: 1, bufferFrames: 480);
        using var _ = engine;

        var handle = engine.Play(AudioTestData.Constant(48_000, 1f));
        engine.FadeOutAndStop(handle, TimeSpan.FromSeconds(0.1));

        // The last sample of each block rather than its peak: the voice ramps its gain across the
        // block it changes in, so a block that starts loud and ends quiet peaks at the loud end and
        // says nothing about where the fade got to.
        var loud = AudioTestData.Render(device, 480)[^1];
        engine.Update(0.05f);
        var middle = AudioTestData.Render(device, 480)[^1];
        engine.Update(0.04f);
        var quiet = AudioTestData.Render(device, 480)[^1];

        Assert.Equal(1f, loud, 0.01f);
        Assert.True(loud > middle, $"{loud:F4} then {middle:F4}");
        Assert.True(middle > quiet, $"{middle:F4} then {quiet:F4}");
    }

    [Fact]
    public void AZeroLengthFadeArrivesAtOnce() {
        var (engine, _) = AudioTestData.Engine(channels: 1);
        using var __ = engine;

        var handle = engine.Play(AudioTestData.Constant(48_000, 1f));
        engine.FadeTo(handle, 0.5f, TimeSpan.Zero);

        Assert.Equal(0.5f, GainOf(engine, handle), 1e-5f);
        Assert.False(engine.IsFading(handle));
    }

    /// <summary>
    ///     Left running, a fade whose voice has been reused would drive the gain of whatever took the
    ///     slot.
    /// </summary>
    [Fact]
    public void AFadeStopsWhenItsVoiceIsStolen() {
        var (engine, device) = AudioTestData.Engine(channels: 1, voices: 1);
        using var _ = engine;

        var victim = engine.Play(AudioTestData.Constant(48_000, 1f));
        engine.FadeTo(victim, 0f, OneSecond);

        var thief = engine.Play(AudioTestData.Constant(48_000, 1f), new PlaybackSettings {
            Gain = 0.8f,
            Pitch = 1f
        });

        AudioTestData.Render(device, 128);
        engine.Update(0.5f);

        Assert.False(engine.IsFading(victim));
        Assert.Equal(0.8f, GainOf(engine, thief), 1e-5f);
    }

    [Fact]
    public void ABusFadeMovesEverythingRoutedIntoIt() {
        var (engine, device) = AudioTestData.Engine(channels: 1);
        using var _ = engine;

        var music = engine.CreateBus("Music");
        engine.Play(AudioTestData.Constant(48_000, 0.8f), new PlaybackSettings {
            Gain = 1f,
            Pitch = 1f,
            Bus = music.Index
        });

        music.FadeTo(0f, TimeSpan.FromSeconds(0.5), AudioFadeCurve.Linear);
        AudioTestData.Render(device, 64);
        Assert.Equal(0.8f, music.PeakLevel, 0.01f);

        engine.Update(0.25f);
        AudioTestData.Render(device, 64);
        Assert.Equal(0.4f, music.PeakLevel, 0.02f);

        engine.Update(0.25f);
        AudioTestData.Render(device, 64);
        Assert.Equal(0f, music.PeakLevel, 1e-4f);
        Assert.False(music.IsFading);
    }

    [Fact]
    public void ASecondFadeTakesOverFromWhereTheFirstGotTo() {
        var (engine, _) = AudioTestData.Engine(channels: 1);
        using var __ = engine;

        var music = engine.CreateBus("Music");
        music.FadeTo(0f, OneSecond, AudioFadeCurve.Linear);
        engine.Update(0.5f);

        var halfway = music.Gain;
        music.FadeTo(1f, OneSecond, AudioFadeCurve.Linear);
        engine.Update(0f);

        // No jump: the new fade starts at whatever the old one had reached.
        Assert.Equal(halfway, music.Gain, 1e-5f);

        engine.Update(1f);
        Assert.Equal(1f, music.Gain, 1e-5f);
    }

    /// <summary>
    ///     Game time and not a wall clock: a fade that kept running under a pause menu, or ignored
    ///     slow motion, is a bug somebody spends an afternoon on.
    /// </summary>
    [Fact]
    public void AFadeRunsOnTheTimeItIsGivenAndNotOnAClock() {
        var (engine, _) = AudioTestData.Engine(channels: 1);
        using var __ = engine;

        var music = engine.CreateBus("Music");
        music.FadeTo(0f, OneSecond, AudioFadeCurve.Linear);

        // A paused game hands over no time, however long it stays paused for.
        for (var frame = 0; frame < 100; frame++) {
            engine.Update(0f);
        }

        Assert.Equal(1f, music.Gain, 1e-5f);
        Assert.True(music.IsFading);
    }

    static float GainOf(AudioEngine engine, VoiceHandle handle) => engine.GainOf(handle);
}

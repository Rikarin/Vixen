// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Mixing;
using Vixen.Audio.Spatial;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>
///     What a full pool does. Dropping the request — which is what it used to do — is the one answer
///     no shipping engine gives, because it loses whichever sound happened to ask last rather than
///     whichever sound matters least.
/// </summary>
public sealed class VoiceStealingTests {
    static PlaybackSettings Playing(float gain = 1f, int priority = 0) =>
        new() { Gain = gain, Pitch = 1f, Priority = priority };

    [Fact]
    public void AFullPoolMakesRoomInsteadOfRefusing() {
        var (engine, device) = AudioTestData.Engine(channels: 1, voices: 2);
        using var _ = engine;

        engine.Play(AudioTestData.Constant(48_000, 1f), Playing());
        engine.Play(AudioTestData.Constant(48_000, 1f), Playing());
        var third = engine.Play(AudioTestData.Constant(48_000, 1f), Playing());

        Assert.True(third.IsValid);

        AudioTestData.Render(device, 256);
        engine.Update();

        Assert.Equal(0, engine.Statistics.DroppedRequests);
        Assert.Equal(1, engine.Statistics.StolenVoices);
        Assert.True(engine.IsPlaying(third));
        Assert.Equal(2, engine.Statistics.ActiveVoices);
    }

    /// <summary>
    ///     Among sounds nobody ranked, the one nobody can hear is the one to lose.
    /// </summary>
    [Fact]
    public void TheQuietestVoiceIsTheOneThatGoes() {
        var (engine, device) = AudioTestData.Engine(channels: 1, voices: 3);
        using var _ = engine;

        var loud = engine.Play(AudioTestData.Constant(48_000, 1f), Playing(0.9f));
        var quiet = engine.Play(AudioTestData.Constant(48_000, 1f), Playing(0.05f));
        var middling = engine.Play(AudioTestData.Constant(48_000, 1f), Playing(0.5f));

        AudioTestData.Render(device, 64);
        engine.Play(AudioTestData.Constant(48_000, 1f), Playing());

        Assert.False(engine.IsPlaying(quiet));
        Assert.True(engine.IsPlaying(loud));
        Assert.True(engine.IsPlaying(middling));
    }

    /// <summary>
    ///     Distance counts. A sound at full gain two hundred units away is near-silence, and scoring
    ///     it on its gain alone would protect it over something audible.
    /// </summary>
    [Fact]
    public void ADistantSpatialVoiceLosesToANearOneAtTheSameGain() {
        var (engine, device) = AudioTestData.Engine(channels: 1, voices: 2);
        using var _ = engine;

        engine.SetListener(AudioListener.Default);

        var near = engine.Play(AudioTestData.Constant(48_000, 1f), Playing() with {
            IsSpatial = true,
            Spatial = new SpatialSettings { Position = new Vector3(0f, 0f, -1f) }
        });

        var far = engine.Play(AudioTestData.Constant(48_000, 1f), Playing() with {
            IsSpatial = true,
            Spatial = new SpatialSettings { Position = new Vector3(0f, 0f, -300f) }
        });

        // One block, so the spatialiser has worked out how far away each of them is.
        AudioTestData.Render(device, 64);
        engine.Play(AudioTestData.Constant(48_000, 1f), Playing());

        Assert.False(engine.IsPlaying(far));
        Assert.True(engine.IsPlaying(near));
    }

    [Fact]
    public void PriorityBeatsLoudness() {
        var (engine, device) = AudioTestData.Engine(channels: 1, voices: 2);
        using var _ = engine;

        var quietButImportant = engine.Play(AudioTestData.Constant(48_000, 1f), Playing(0.01f, priority: 10));
        var loudButNot = engine.Play(AudioTestData.Constant(48_000, 1f), Playing(1f));

        AudioTestData.Render(device, 64);
        engine.Play(AudioTestData.Constant(48_000, 1f), Playing());

        Assert.True(engine.IsPlaying(quietButImportant));
        Assert.False(engine.IsPlaying(loudButNot));
    }

    [Fact]
    public void ASoundCanDisplaceOneOfEqualPriority() {
        var (engine, device) = AudioTestData.Engine(channels: 1, voices: 1);
        using var _ = engine;

        var first = engine.Play(AudioTestData.Constant(48_000, 1f), Playing(priority: 5));
        var second = engine.Play(AudioTestData.Constant(48_000, 1f), Playing(priority: 5));

        AudioTestData.Render(device, 256);
        engine.Update();

        Assert.False(engine.IsPlaying(first));
        Assert.True(engine.IsPlaying(second));
    }

    /// <summary>
    ///     The handle a steal returns has to be live immediately — a caller that stops the sound in
    ///     the same frame it started it must reach the slot it was given and not the one it replaced.
    /// </summary>
    [Fact]
    public void TheStealInvalidatesTheDisplacedHandleAtOnce() {
        var (engine, device) = AudioTestData.Engine(channels: 1, voices: 1);
        using var _ = engine;

        var victim = engine.Play(AudioTestData.Constant(48_000, 1f), Playing());
        var thief = engine.Play(AudioTestData.Constant(48_000, 1f), Playing());

        Assert.Equal(victim.Index, thief.Index);
        Assert.NotEqual(victim.Generation, thief.Generation);
        Assert.Equal(VoiceState.Free, engine.StateOf(victim));

        // Stopping the displaced sound must not reach through to the one that took its slot.
        engine.Stop(victim);
        AudioTestData.Render(device, 256);
        engine.Update();

        Assert.True(engine.IsPlaying(thief));
    }

    /// <summary>
    ///     The displaced sound is faded rather than cut, and the new one starts from its own first
    ///     frame rather than from wherever the old one had got to.
    /// </summary>
    [Fact]
    public void TheNewSoundStartsFromItsOwnBeginning() {
        var (engine, device) = AudioTestData.Engine(channels: 1, voices: 1, bufferFrames: 32);
        using var _ = engine;

        engine.Play(AudioTestData.Constant(48_000, 1f), Playing());
        AudioTestData.Render(device, 512);

        engine.Play(AudioTestData.Ramp(48_000), Playing());

        // One block for the victim's fade, and then the ramp from frame zero.
        AudioTestData.Render(device, 32);
        var rendered = AudioTestData.Render(device, 8);

        Assert.Equal(0f, rendered[0], 1e-5f);
        Assert.Equal(AudioTestData.RampStep, rendered[1], 1e-5f);
        Assert.Equal(2 * AudioTestData.RampStep, rendered[2], 1e-5f);
    }

    [Fact]
    public void APausedVoiceCanBeStolenToo() {
        var (engine, device) = AudioTestData.Engine(channels: 1, voices: 1);
        using var _ = engine;

        var held = engine.Play(AudioTestData.Constant(48_000, 1f), Playing());
        engine.Pause(held);

        var thief = engine.Play(AudioTestData.Constant(48_000, 1f), Playing());
        AudioTestData.Render(device, 256);
        engine.Update();

        Assert.False(engine.IsPlaying(held));
        Assert.True(engine.IsPlaying(thief));
    }

    [Fact]
    public void StealingLeavesTheEngineConsistent() {
        var (engine, device) = AudioTestData.Engine(channels: 1, voices: 4);
        using var _ = engine;

        for (var i = 0; i < 40; i++) {
            engine.Play(AudioTestData.Constant(48_000, 0.2f), Playing(0.1f + (i % 5 * 0.1f)));
            AudioTestData.Render(device, 32);
            engine.Update();
        }

        Assert.Empty(engine.Validate());
        Assert.Equal(4, engine.Statistics.ActiveVoices);
        Assert.True(engine.Statistics.StolenVoices >= 36);
    }
}

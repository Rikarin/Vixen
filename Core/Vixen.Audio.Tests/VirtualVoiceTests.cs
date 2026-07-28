// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Devices;
using Vixen.Audio.Mixing;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>Voices that keep playing without being heard, and come back where they should be.</summary>
public sealed class VirtualVoiceTests : IDisposable {
    const int Block = 64;

    readonly AudioEngine engine;
    readonly NullAudioDevice device;

    public VirtualVoiceTests() {
        var backend = new NullAudioBackend();

        device = (NullAudioDevice)backend.OpenDevice(new AudioDeviceOptions {
            Format = new AudioFormat(48_000, 2),
            BufferFrames = Block
        });

        engine = new(device, new AudioEngineOptions {
            VoiceCapacity = 8,
            AudibleVoices = 1,
            StreamOnOwnThread = false,
            MasterLimiter = false
        });
    }

    public void Dispose() => engine.Dispose();

    /// <summary>Renders whole blocks and hands back the last one.</summary>
    float[] Advance(int blocks) {
        var rendered = Array.Empty<float>();

        for (var i = 0; i < blocks; i++) {
            rendered = AudioTestData.Render(device, Block);
        }

        return rendered;
    }

    [Fact]
    public void AnAudibleLimitAtOrAboveTheCapacityIsNoLimitAtAll() {
        using var everything = new AudioEngine(
            (NullAudioDevice)new NullAudioBackend().OpenDevice(new AudioDeviceOptions()),
            new AudioEngineOptions { VoiceCapacity = 4, AudibleVoices = 4, StreamOnOwnThread = false }
        );

        Assert.Equal(0, everything.AudibleVoices);
    }

    [Fact]
    public void TheQuietestVoicesAreTheOnesThatStopBeingHeard() {
        var clip = AudioTestData.Constant(48_000, 1f);
        var loud = engine.Play(clip, new PlaybackSettings { Gain = 1f });
        var quiet = engine.Play(clip, new PlaybackSettings { Gain = 0.05f });

        engine.Update(0f);
        Advance(4);

        Assert.Equal(1, engine.Statistics.VirtualVoices);

        // Both are still playing — that is the whole difference from a steal.
        Assert.Equal(VoiceState.Playing, engine.StateOf(loud));
        Assert.Equal(VoiceState.Playing, engine.StateOf(quiet));

        // And what came out is the loud one alone: 1.0 panned centre, not 1.05.
        Assert.Equal(0.7071f, MathF.Abs(Advance(1)[0]), 0.01f);
    }

    [Fact]
    public void PriorityIsTheFirstKeyAndAudibilityOnlyTheTieBreak() {
        var clip = AudioTestData.Constant(48_000, 1f);
        engine.Play(clip, new PlaybackSettings { Gain = 1f, Priority = 0 });
        var important = engine.Play(clip, new PlaybackSettings { Gain = 0.05f, Priority = 10 });

        engine.Update(0f);
        Advance(4);

        // The quiet one wins because somebody said it mattered, so what came out is 0.05.
        Assert.Equal(VoiceState.Playing, engine.StateOf(important));
        Assert.Equal(0.0354f, MathF.Abs(Advance(1)[0]), 0.005f);
    }

    /// <summary>
    ///     The claim the whole feature exists for. A ramp says exactly where its playhead is, so this
    ///     asserts the sample that came out is the one that would have come out had it never stopped
    ///     being heard — not the start of the clip, and not silence.
    /// </summary>
    [Fact]
    public void AVirtualVoiceComesBackWhereItWouldHaveBeen() {
        var clip = AudioTestData.Ramp(48_000);
        var heard = engine.Play(clip, new PlaybackSettings { Gain = 1f });
        var unheard = engine.Play(clip, new PlaybackSettings { Gain = 0.001f });

        engine.Update(0f);
        Advance(10);

        Assert.Equal(1, engine.Statistics.VirtualVoices);

        // Swap which one matters. The next ranking pass promotes the one that has been silent.
        engine.SetGain(heard, 0.001f);
        engine.SetGain(unheard, 1f);
        engine.Update(0f);

        // One block for the promoted voice to ramp in and the demoted one to ramp out, then a clean
        // block whose first frame is frame 704 of the clip.
        Advance(1);
        var rendered = Advance(1);

        // A mono source spread across two speakers is at constant power, so 0.7071 of its value.
        var expected = 704 * AudioTestData.RampStep * 0.7071f;
        Assert.Equal(expected, MathF.Abs(rendered[0]), 0.01f);
    }

    /// <summary>
    ///     A voice arriving at full gain in one sample is a step in the waveform, which is a click.
    ///     The one that holds the audible slot plays silence, so what comes out is the returning voice
    ///     alone — otherwise this measures the sum of a fade-in and a fade-out, which is flat by
    ///     construction and would pass whatever either half did.
    /// </summary>
    [Fact]
    public void AVoiceComingBackFadesInOverABlockRatherThanArriving() {
        var blocker = engine.Play(
            AudioTestData.Constant(48_000, 0f),
            new PlaybackSettings { Gain = 1f, Priority = 10 }
        );

        engine.Play(AudioTestData.Constant(48_000, 1f), new PlaybackSettings { Gain = 1f });

        engine.Update(0f);
        Advance(6);
        Assert.Equal(1, engine.Statistics.VirtualVoices);

        // Letting go of the audible slot promotes the other one on the next ranking pass.
        engine.Stop(blocker);
        engine.Update(0f);

        var arriving = Advance(1);

        Assert.True(MathF.Abs(arriving[0]) < 0.05f, $"it started at {arriving[0]:F3} instead of near zero");
        Assert.True(MathF.Abs(arriving[^2]) > 0.6f, $"it ended at {arriving[^2]:F3} instead of near full");
    }

    [Fact]
    public void AVirtualVoiceThatRunsOutStillFinishes() {
        engine.Play(AudioTestData.Constant(48_000, 1f), new PlaybackSettings { Gain = 1f });
        var brief = engine.Play(AudioTestData.Constant(128, 1f), new PlaybackSettings { Gain = 0.001f });

        engine.Update(0f);
        Advance(8);
        engine.Update(0f);

        Assert.False(engine.IsPlaying(brief));
    }

    [Fact]
    public void APausedVoiceIsNotCountedAgainstTheAudibleBudget() {
        var clip = AudioTestData.Constant(48_000, 1f);
        var held = engine.Play(clip, new PlaybackSettings { Gain = 1f, StartPaused = true });
        engine.Play(clip, new PlaybackSettings { Gain = 0.05f });

        engine.Update(0f);
        Advance(2);

        // The paused one renders nothing already, so silencing the quiet one on its behalf would be
        // trading something audible for something that is not.
        Assert.Equal(0, engine.Statistics.VirtualVoices);
        Assert.Equal(VoiceState.Paused, engine.StateOf(held));
        Assert.Equal(0.0354f, MathF.Abs(Advance(1)[0]), 0.005f);
    }

    /// <summary>What stealing does to the same situation, which is the comparison worth having.</summary>
    [Fact]
    public void WithoutVirtualisationTheDisplacedSoundIsSimplyGone() {
        var backend = new NullAudioBackend();
        var stealing = (NullAudioDevice)backend.OpenDevice(new AudioDeviceOptions {
            Format = new AudioFormat(48_000, 2),
            BufferFrames = Block
        });

        using var small = new AudioEngine(stealing, new AudioEngineOptions {
            VoiceCapacity = 1,
            StreamOnOwnThread = false,
            MasterLimiter = false
        });

        var clip = AudioTestData.Constant(48_000, 1f);
        var first = small.Play(clip);
        small.Play(clip);

        // Free rather than Stopping, because a steal bumps the slot's generation — the handle does not
        // name a sound any more, which is a stronger statement than "the sound is ending" and is
        // exactly what virtualisation avoids.
        Assert.Equal(VoiceState.Free, small.StateOf(first));
        Assert.False(small.IsPlaying(first));
    }
}

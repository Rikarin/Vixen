// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Devices;
using Vixen.Audio.Effects;
using Vixen.Audio.Mixing;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>The thing a bus send cannot do: two sources on one bus, wet by different amounts.</summary>
public sealed class PerVoiceSendTests {
    /// <summary>Reads the loudest sample passing through a bus, and changes nothing.</summary>
    /// <remarks>
    ///     A bus's buffer is not observable from outside the mixer, and it should not be — so the way
    ///     to ask what reached it is to stand in the chain and watch, which is what an effect is.
    /// </remarks>
    sealed class Tap : IAudioEffect {
        public bool Enabled { get; set; } = true;

        public float Peak { get; private set; }

        public void Prepare(in AudioFormat format, int maxFrames) { }

        public void Process(Span<float> buffer, int frameCount, int channels) {
            foreach (var sample in buffer[..(frameCount * channels)]) {
                Peak = MathF.Max(Peak, MathF.Abs(sample));
            }
        }

        public void Reset() => Peak = 0f;
    }

    /// <summary>What reached a bus over one rendered block.</summary>
    static float PeakOf(NullAudioDevice device, AudioBus bus) {
        var tap = new Tap();
        bus.AddEffect(tap);
        AudioTestData.Render(device, 128);
        bus.RemoveEffect(tap);
        return tap.Peak;
    }

    [Fact]
    public void ASoundWithNoSendReachesOnlyItsOwnBus() {
        var (engine, device) = AudioTestData.Engine();

        using (engine) {
            var dry = engine.CreateBus("Dry");
            var aux = engine.CreateBus("Aux");

            engine.Play(AudioTestData.Constant(48_000, 1f), new PlaybackSettings { Bus = dry.Index });
            engine.Update(0f);

            Assert.True(PeakOf(device, dry) > 0.5f);
            Assert.Equal(0f, PeakOf(device, aux));
        }
    }

    [Fact]
    public void ASoundWithASendReachesBoth() {
        var (engine, device) = AudioTestData.Engine();

        using (engine) {
            var dry = engine.CreateBus("Dry");
            var aux = engine.CreateBus("Aux");

            engine.Play(AudioTestData.Constant(48_000, 1f), new PlaybackSettings {
                Bus = dry.Index,
                SendBus = aux.Index,
                SendLevel = 0.5f
            });

            engine.Update(0f);

            // Against the dry path rather than against 1: a mono source in a stereo device is
            // centre-panned at constant power, so every absolute here would carry a stray 1/√2 that
            // has nothing to do with sends.
            var direct = PeakOf(device, dry);
            var sent = PeakOf(device, aux);

            Assert.True(direct > 0.5f, $"the dry path was only {direct:F3}");

            // And the dry path is untouched by the send — a copy is taken, not a split.
            Assert.Equal(0.5f, sent / direct, 0.02f);
        }
    }

    /// <summary>The whole point, in one test: same bus, different reverb amounts.</summary>
    [Fact]
    public void TwoSoundsOnOneBusCanBeWetByDifferentAmounts() {
        var (engine, device) = AudioTestData.Engine();

        using (engine) {
            var dry = engine.CreateBus("Dry");
            var near = engine.CreateBus("Near");
            var far = engine.CreateBus("Far");

            engine.Play(AudioTestData.Constant(48_000, 1f), new PlaybackSettings {
                Bus = dry.Index, SendBus = near.Index, SendLevel = 0.1f
            });

            engine.Play(AudioTestData.Constant(48_000, 1f), new PlaybackSettings {
                Bus = dry.Index, SendBus = far.Index, SendLevel = 0.8f
            });

            engine.Update(0f);

            var quiet = PeakOf(device, near);
            var loud = PeakOf(device, far);

            // Eight times as wet, on the same bus, from the same block.
            Assert.True(quiet > 0f && loud > 0f, $"near {quiet:F4}, far {loud:F4}");
            Assert.Equal(8f, loud / quiet, 0.1f);
        }
    }

    [Fact]
    public void TheLevelCanBeMovedWhileItIsPlaying() {
        var (engine, device) = AudioTestData.Engine();

        using (engine) {
            var dry = engine.CreateBus("Dry");
            var aux = engine.CreateBus("Aux");

            var handle = engine.Play(AudioTestData.Constant(48_000, 1f), new PlaybackSettings {
                Bus = dry.Index, SendBus = aux.Index, SendLevel = 0.2f
            });

            engine.Update(0f);
            var before = PeakOf(device, aux);

            Assert.True(engine.SetSend(handle, aux.Index, 0.9f));
            Assert.Equal(0.9f, engine.SendLevelOf(handle));

            var after = PeakOf(device, aux);

            Assert.True(before > 0f, "nothing reached the aux to begin with");
            Assert.Equal(4.5f, after / before, 0.1f);
        }
    }

    [Fact]
    public void ALevelOfZeroIsTheSameAsNoSend() {
        var (engine, device) = AudioTestData.Engine();

        using (engine) {
            var dry = engine.CreateBus("Dry");
            var aux = engine.CreateBus("Aux");

            var handle = engine.Play(AudioTestData.Constant(48_000, 1f), new PlaybackSettings {
                Bus = dry.Index, SendBus = aux.Index, SendLevel = 0f
            });

            engine.Update(0f);

            Assert.Equal(0f, PeakOf(device, aux));
            Assert.True(PeakOf(device, dry) > 0.5f, "and the dry path still plays");
            Assert.Equal(0f, engine.SendLevelOf(handle));
        }
    }

    /// <summary>
    ///     A send naming a bus that no longer exists routes to nothing rather than to the master:
    ///     losing an effect is a smaller mistake than a stale reverb send arriving at the output.
    /// </summary>
    [Fact]
    public void ASendToABusThatIsNotThereIsDroppedAndNotClamped() {
        var (engine, _) = AudioTestData.Engine();

        using (engine) {
            var dry = engine.CreateBus("Dry");

            var handle = engine.Play(AudioTestData.Constant(48_000, 1f), new PlaybackSettings {
                Bus = dry.Index, SendBus = 99, SendLevel = 1f
            });

            Assert.Equal(-1, engine.SendBusOf(handle));
            Assert.Equal(0f, engine.SendLevelOf(handle));
        }
    }

    /// <summary>The same bug class as the automation and the occlusion: a stolen slot inherits nothing.</summary>
    [Fact]
    public void ASoundThatTakesASendingVoicesSlotHasNoSend() {
        var (engine, _) = AudioTestData.Engine(voices: 1);

        using (engine) {
            var dry = engine.CreateBus("Dry");
            var aux = engine.CreateBus("Aux");

            var wet = engine.Play(AudioTestData.Constant(48_000, 1f), new PlaybackSettings {
                Bus = dry.Index, SendBus = aux.Index, SendLevel = 1f, Priority = 0
            });

            Assert.Equal(aux.Index, engine.SendBusOf(wet));

            var footstep = engine.Play(AudioTestData.Constant(48_000, 1f), new PlaybackSettings {
                Bus = dry.Index, Priority = 10
            });

            Assert.True(footstep.IsValid);
            Assert.Equal(-1, engine.SendBusOf(footstep));
            Assert.Equal(0f, engine.SendLevelOf(footstep));
        }
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Devices;
using Vixen.Audio.Effects;
using Vixen.Audio.Mixing;
using Vixen.Audio.Sources;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>The input half, which until now did not exist.</summary>
public sealed class CaptureTests {
    static NullAudioCaptureDevice Microphone(int bufferedFrames = 4_800) {
        var device = new NullAudioCaptureDevice(new AudioCaptureOptions { BufferedFrames = bufferedFrames });
        device.Start();
        return device;
    }

    static float[] Speech(int frames, float amplitude = 0.5f) {
        var buffer = new float[frames];

        for (var i = 0; i < frames; i++) {
            buffer[i] = amplitude * MathF.Sin(2f * MathF.PI * 220f * i / 48_000f);
        }

        return buffer;
    }

    [Fact]
    public void ABackendThatCannotCaptureSaysSoRatherThanThrowingLater() {
        // Through the interface, because that is where the default implementations live — a backend
        // written before capture existed does not have the members at all, which is exactly the
        // property that kept every one of them compiling when capture was added.
        IAudioBackend quiet = new SilentBackend();

        Assert.False(quiet.SupportsCapture);
        Assert.Empty(quiet.EnumerateCaptureDevices());
        Assert.Throws<AudioDeviceException>(() => quiet.OpenCaptureDevice(new AudioCaptureOptions()));
    }

    [Fact]
    public void TheNullBackendCapturesWhateverIsPushedIntoIt() {
        using var backend = new NullAudioBackend();

        Assert.True(backend.SupportsCapture);
        Assert.Single(backend.EnumerateCaptureDevices());

        using var device = backend.OpenCaptureDevice(new AudioCaptureOptions());
        device.Start();

        Assert.True(device.IsRunning);
        Assert.Equal(AudioFormat.Mono48k, device.Format);
    }

    [Fact]
    public void WhatWasPushedIsWhatComesBack() {
        using var device = Microphone();
        var spoken = Speech(480);

        Assert.Equal(480, device.Push(spoken));
        Assert.Equal(480, device.Available);

        var heard = new float[480];
        Assert.Equal(480, device.Read(heard, 480));
        Assert.Equal(spoken, heard);
        Assert.Equal(0, device.Available);
    }

    /// <summary>Which is the normal case, not an error: a microphone produces at its own rate.</summary>
    [Fact]
    public void ReadingMoreThanIsThereReturnsWhatIsThere() {
        using var device = Microphone();
        device.Push(Speech(100));

        var heard = new float[480];

        Assert.Equal(100, device.Read(heard, 480));
        Assert.Equal(0, device.Read(heard, 480));
    }

    /// <summary>A reader that stopped reading loses the newest audio and is told how much.</summary>
    [Fact]
    public void ASlowReaderLosesAudioAndItIsCounted() {
        using var device = Microphone(bufferedFrames: 480);

        Assert.Equal(480, device.Push(Speech(480)));
        Assert.Equal(0, device.Overruns);

        Assert.Equal(0, device.Push(Speech(240)));
        Assert.Equal(240, device.Overruns);
    }

    [Fact]
    public void AStoppedMicrophoneHearsNothing() {
        var device = new NullAudioCaptureDevice(new AudioCaptureOptions());

        Assert.False(device.IsRunning);
        Assert.Equal(0, device.Push(Speech(480)));
        Assert.Equal(0, device.Available);

        device.Dispose();
    }

    /// <summary>Monitoring, and the only way to prove the path end to end.</summary>
    [Fact]
    public void AMicrophoneCanBePlayedThroughTheMixer() {
        var (engine, output) = AudioTestData.Engine(channels: 2);

        using (engine) {
            using var microphone = Microphone();
            var provider = new CaptureSampleProvider(microphone);

            microphone.Push(Speech(4_800, 0.8f));
            engine.Play(provider, new PlaybackSettings());

            var peak = 0f;

            for (var i = 0; i < 8; i++) {
                peak = MathF.Max(peak, AudioTestData.Peak(AudioTestData.Render(output, 64)));
            }

            Assert.True(peak > 0.3f, $"nothing reached the speakers, peak was {peak:F3}");
        }
    }

    /// <summary>
    ///     Somebody who has stopped talking has not left. A voice that ended on every gap would be
    ///     rebuilt, with its bus and its spatialisation, several times a sentence.
    /// </summary>
    [Fact]
    public void AnEmptyMicrophoneIsSilenceAndACounterRatherThanTheEndOfTheSound() {
        var (engine, output) = AudioTestData.Engine(channels: 2);

        using (engine) {
            using var microphone = Microphone();
            var provider = new CaptureSampleProvider(microphone);
            var handle = engine.Play(provider, new PlaybackSettings());

            // Nothing was ever pushed, so every frame is starved.
            AudioTestData.Render(output, 256);
            engine.Update(0f);

            Assert.True(engine.IsPlaying(handle));
            Assert.True(provider.Starved > 0);
            Assert.Equal(0f, AudioTestData.Peak(AudioTestData.Render(output, 64)));
        }
    }

    /// <summary>The reason the gate exists, wired the way a session actually would be.</summary>
    [Fact]
    public void AGateOnTheVoiceBusRemovesTheRoomToneAndKeepsTheSpeech() {
        var (engine, output) = AudioTestData.Engine(channels: 2);

        using (engine) {
            using var microphone = Microphone();
            var voice = engine.CreateBus("Voice");

            voice.AddEffect(new GateEffect {
                ThresholdDb = -40f,
                HoldSeconds = 0f,
                ReleaseSeconds = 0.001f,
                AttackSeconds = 0.0005f
            });

            engine.Play(
                new CaptureSampleProvider(microphone),
                new PlaybackSettings { Bus = voice.Index }
            );

            // Exactly as much as the blocks below consume, so the second push is not queued behind
            // the remains of the first — a microphone is a queue, and the test is about what came out
            // of the gate rather than about how deep that queue is.
            const int Blocks = 20;
            const int Frames = Blocks * 64;

            // A fan and a keyboard, at −60 dB.
            microphone.Push(Speech(Frames, 0.001f));
            var noise = 0f;

            for (var i = 0; i < Blocks; i++) {
                noise = MathF.Max(noise, AudioTestData.Peak(AudioTestData.Render(output, 64)));
            }

            microphone.Push(Speech(Frames, 0.5f));
            var speech = 0f;

            for (var i = 0; i < Blocks; i++) {
                speech = MathF.Max(speech, AudioTestData.Peak(AudioTestData.Render(output, 64)));
            }

            Assert.True(noise < 0.0005f, $"the room tone came through at {noise:E2}");
            Assert.True(speech > 0.2f, $"the speech was cut off, peak was {speech:F3}");
        }
    }

    /// <summary>A backend from before capture existed, to prove the default implementations hold.</summary>
    sealed class SilentBackend : IAudioBackend {
        public string Name => "Silent";

        public bool IsAvailable => true;

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices() => [];

        public IAudioDevice OpenDevice(in AudioDeviceOptions options) =>
            throw new AudioDeviceException("No devices.");

        public void Dispose() { }
    }
}

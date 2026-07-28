// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Mixing;
using Vixen.Audio.Sources;
using Vixen.Audio.Streaming;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>A decoder a test can stall on purpose, so an underrun is a thing that can be asserted on.</summary>
sealed class StepDecoder(int frames, int channels = 1, int sampleRate = 48_000) : IAudioStreamDecoder {
    long position;

    /// <summary>How many times it was asked for frames.</summary>
    public int Calls { get; private set; }

    public AudioFormat Format { get; } = new(sampleRate, channels);

    public long FrameCount => frames;

    public long Position => position;

    public bool CanSeek { get; init; } = true;

    public int Decode(Span<float> destination, int frameCount) {
        Calls++;
        var wanted = (int)Math.Min(frameCount, frames - position);

        if (wanted <= 0) {
            return 0;
        }

        for (var frame = 0; frame < wanted; frame++) {
            for (var channel = 0; channel < channels; channel++) {
                destination[(frame * channels) + channel] = (position + frame) * AudioTestData.RampStep;
            }
        }

        position += wanted;
        return wanted;
    }

    public void Seek(long frame) {
        if (!CanSeek) {
            throw new NotSupportedException();
        }

        position = Math.Clamp(frame, 0, frames);
    }

    public void Dispose() { }
}

public sealed class AudioRingBufferTests {
    [Fact]
    public void ItTakesWhatFitsAndSaysHowMuchThatWas() {
        var ring = new AudioRingBuffer(4);

        Assert.Equal(4, ring.Write([1f, 2f, 3f, 4f, 5f, 6f]));
        Assert.Equal(4, ring.Count);
        Assert.Equal(0, ring.Free);
    }

    [Fact]
    public void ItReadsBackWhatWasWritten() {
        var ring = new AudioRingBuffer(8);
        ring.Write([1f, 2f, 3f]);

        var destination = new float[3];

        Assert.Equal(3, ring.Read(destination));
        Assert.Equal([1f, 2f, 3f], destination);
        Assert.Equal(0, ring.Count);
    }

    /// <summary>
    ///     The cursors only increase and the position is the counter modulo the capacity, so "full"
    ///     and "empty" are different states without sacrificing a slot — and a wrap is not a special
    ///     case anybody has to remember.
    /// </summary>
    [Fact]
    public void ItReadsCorrectlyAcrossAWrap() {
        var ring = new AudioRingBuffer(4);
        ring.Write([1f, 2f, 3f]);
        ring.Read(new float[3]);

        ring.Write([4f, 5f, 6f, 7f]);
        var destination = new float[4];

        Assert.Equal(4, ring.Read(destination));
        Assert.Equal([4f, 5f, 6f, 7f], destination);
    }

    [Fact]
    public void ReadingAnEmptyRingGivesNothingRatherThanBlocking() {
        var ring = new AudioRingBuffer(4);

        Assert.Equal(0, ring.Read(new float[4]));
    }
}

public sealed class StreamingTests {
    [Fact]
    public void ThePumpFillsTheRingBeforeAnythingPlays() {
        using var provider = new StreamingSampleProvider(new StepDecoder(10_000), bufferedFrames: 1_000);
        using var pump = new AudioStreamPump();

        pump.Register(provider);

        Assert.Equal(1_000, provider.BufferedFrames);
        Assert.Equal(1, pump.StreamCount);
    }

    [Fact]
    public void ItProducesWhatTheDecoderProduced() {
        using var provider = new StreamingSampleProvider(new StepDecoder(10_000), bufferedFrames: 1_000);
        provider.Fill();

        var destination = new float[4];

        Assert.Equal(4, provider.Read(destination, 4));
        Assert.Equal(0f, destination[0], 1e-6f);
        Assert.Equal(3 * AudioTestData.RampStep, destination[3], 1e-6f);
    }

    /// <summary>
    ///     Blocking the audio thread on a slow disk turns one late track into every sound in the game
    ///     stuttering. Silence and a counter is the only survivable answer.
    /// </summary>
    [Fact]
    public void AStarvedStreamPlaysSilenceAndCountsIt() {
        // A four-frame buffer against a sixteen-frame block: the decoder is not finished, it is
        // behind, and the difference is what the underrun counter exists to record.
        using var provider = new StreamingSampleProvider(new StepDecoder(10_000), bufferedFrames: 4);
        provider.Fill();

        var destination = new float[16];
        var frames = provider.Read(destination, 16);

        Assert.Equal(16, frames);
        Assert.Equal(3 * AudioTestData.RampStep, destination[3], 1e-6f);
        Assert.Equal(0f, destination[4]);
        Assert.Equal(0f, destination[15]);
        Assert.Equal(1, provider.Underruns);
        Assert.False(provider.IsExhausted);
    }

    [Fact]
    public void AStreamNobodyHasPumpedYetIsSilentRatherThanFinished() {
        using var provider = new StreamingSampleProvider(new StepDecoder(10_000), bufferedFrames: 1_000);

        var destination = new float[16];

        Assert.Equal(16, provider.Read(destination, 16));
        Assert.Equal(0f, AudioTestData.Peak(destination));
        Assert.Equal(1, provider.Underruns);

        provider.Fill();
        provider.Read(destination, 16);

        Assert.True(AudioTestData.Peak(destination) > 0f);
    }

    [Fact]
    public void AStreamThatHasGenuinelyEndedReadsShortRatherThanPaddingForever() {
        using var provider = new StreamingSampleProvider(new StepDecoder(6), bufferedFrames: 1_000);
        provider.Fill();

        Assert.True(provider.IsExhausted);
        Assert.Equal(6, provider.Read(new float[16], 16));
        Assert.Equal(0, provider.Read(new float[16], 16));
        Assert.Equal(0, provider.Underruns);
    }

    [Fact]
    public void ALoopingStreamNeverEnds() {
        using var provider = new StreamingSampleProvider(new StepDecoder(6), loop: true, bufferedFrames: 64);
        provider.Fill();

        var destination = new float[64];

        Assert.Equal(64, provider.Read(destination, 64));
        Assert.False(provider.IsExhausted);

        // Frame 6 is frame 0 again.
        Assert.Equal(destination[0], destination[6]);
        Assert.Equal(destination[1], destination[7]);
    }

    /// <summary>A loop is a seek to frame zero, and there is nothing else it could be.</summary>
    [Fact]
    public void AStreamThatCannotSeekCannotLoop() {
        var decoder = new StepDecoder(10) { CanSeek = false };

        Assert.Throws<ArgumentException>(() => new StreamingSampleProvider(decoder, loop: true));
    }

    /// <summary>
    ///     There is no side of the ring buffer that owns both cursors, so seeking a stream in flight
    ///     would lose or duplicate frames. Stopping and starting a second voice is what a cross-fade
    ///     is anyway.
    /// </summary>
    [Fact]
    public void APumpedStreamRefusesToSeek() {
        using var provider = new StreamingSampleProvider(new StepDecoder(10_000));
        using var pump = new AudioStreamPump();
        pump.Register(provider);

        Assert.Throws<NotSupportedException>(() => provider.Seek(100));

        pump.Unregister(provider);
        provider.Seek(100);

        Assert.Equal(100, provider.Position);
    }

    [Fact]
    public void AStreamPlaysThroughTheMixerAndFinishes() {
        var (engine, device) = AudioTestData.Engine(channels: 1);
        using var _ = engine;

        var handle = engine.PlayStream(new StepDecoder(64), new PlaybackSettings { Gain = 1f, Pitch = 1f });

        Assert.True(handle.IsValid);
        Assert.Equal(1, engine.Streams.StreamCount);

        var rendered = AudioTestData.Render(device, 32);
        Assert.Equal(0f, rendered[0], 1e-6f);
        Assert.Equal(4 * AudioTestData.RampStep, rendered[4], 1e-6f);

        AudioTestData.Render(device, 128);
        engine.Update();

        Assert.False(engine.IsPlaying(handle));

        // The engine built the provider out of the decoder, so it is the engine that hands it back.
        Assert.Equal(0, engine.Streams.StreamCount);
    }

    [Fact]
    public void APcmDecoderReadsTheBytesTheContentBuildWrote() {
        var clip = AudioTestData.Ramp(64);
        using var stream = new MemoryStream(clip.Samples);

        using var decoder = new PcmStreamDecoder(
            stream,
            new AudioFormat(clip.SampleRate, clip.Channels),
            AudioSampleFormat.Float32
        );

        Assert.Equal(64, decoder.FrameCount);

        var destination = new float[64];

        Assert.Equal(64, decoder.Decode(destination, 64));
        Assert.Equal(0f, destination[0], 1e-6f);
        Assert.Equal(63 * AudioTestData.RampStep, destination[63], 1e-6f);
        Assert.Equal(0, decoder.Decode(destination, 64));
    }

    [Fact]
    public void APcmDecoderWidensSixteenBitSamples() {
        var samples = new byte[4];
        BitConverter.TryWriteBytes(samples.AsSpan(0), short.MinValue);
        BitConverter.TryWriteBytes(samples.AsSpan(2), (short)16_384);

        using var stream = new MemoryStream(samples);
        using var decoder = new PcmStreamDecoder(stream, AudioFormat.Mono48k);

        var destination = new float[2];
        decoder.Decode(destination, 2);

        Assert.Equal(-1f, destination[0], 1e-6f);
        Assert.Equal(0.5f, destination[1], 1e-6f);
    }

    [Fact]
    public void APcmDecoderSeeksWithinItsRegion() {
        var clip = AudioTestData.Ramp(64);
        using var stream = new MemoryStream(clip.Samples);
        using var decoder = new PcmStreamDecoder(stream, new AudioFormat(48_000, 1), AudioSampleFormat.Float32);

        decoder.Seek(32);
        var destination = new float[4];
        decoder.Decode(destination, 4);

        Assert.Equal(32 * AudioTestData.RampStep, destination[0], 1e-6f);
    }
}

/// <summary>
///     The push side of the mixer, which is what a remote player's voice arrives on. Everything else
///     in the engine is pulled: a clip or a decoder is asked for frames and produces them. A packet
///     from the network lands when it lands.
/// </summary>
public sealed class LiveSampleProviderTests {
    [Fact]
    public void WhatIsPushedIsWhatIsPlayed() {
        var live = new LiveSampleProvider(AudioFormat.Mono48k);

        Assert.Equal(4, live.Write([0.1f, 0.2f, 0.3f, 0.4f]));
        Assert.Equal(4, live.BufferedFrames);

        var destination = new float[4];
        Assert.Equal(4, live.Read(destination, 4));
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f], destination);
    }

    /// <summary>
    ///     Somebody who has stopped talking is not somebody who has left. A voice that ended every
    ///     time a packet was late would be rebuilt — with its bus, its spatialisation and its effects
    ///     — several times a sentence.
    /// </summary>
    [Fact]
    public void SilenceBetweenPacketsIsNotTheEndOfTheVoice() {
        var live = new LiveSampleProvider(AudioFormat.Mono48k);
        live.Write([0.5f, 0.5f]);

        var destination = new float[8];

        Assert.Equal(8, live.Read(destination, 8));
        Assert.Equal(0.5f, destination[0]);
        Assert.Equal(0f, destination[2]);
        Assert.Equal(1, live.Underruns);
        Assert.False(live.IsCompleted);

        live.Write([0.25f]);
        Assert.Equal(8, live.Read(destination, 8));
        Assert.Equal(0.25f, destination[0]);
    }

    /// <summary>
    ///     The other failure, and the one that is easy to miss: a burst after a stall fills the ring
    ///     and the excess is thrown away. Growing the buffer to hide it adds latency to every word.
    /// </summary>
    [Fact]
    public void PushingFasterThanRealTimeDropsTheOverflowAndCountsIt() {
        var live = new LiveSampleProvider(AudioFormat.Mono48k, bufferedFrames: 4);

        Assert.Equal(4, live.Write([1f, 2f, 3f, 4f, 5f, 6f]));
        Assert.Equal(2, live.DroppedFrames);
    }

    [Fact]
    public void CompletingLetsTheVoiceFinishWhatWasAlreadyBuffered() {
        var live = new LiveSampleProvider(AudioFormat.Mono48k);
        live.Write([0.5f, 0.5f, 0.5f]);
        live.Complete();

        var destination = new float[8];

        Assert.Equal(3, live.Read(destination, 8));
        Assert.Equal(0, live.Read(destination, 8));
        Assert.Equal(0, live.Underruns);
    }

    [Fact]
    public void ALiveVoicePlaysThroughTheMixerAndEndsWhenTheSpeakerLeaves() {
        var (engine, device) = AudioTestData.Engine(channels: 1);
        using var _ = engine;

        var live = new LiveSampleProvider(AudioFormat.Mono48k);
        live.Write(Enumerable.Repeat(0.5f, 512).ToArray());

        var handle = engine.Play(live, new PlaybackSettings { Gain = 1f, Pitch = 1f });
        var rendered = AudioTestData.Render(device, 256);

        Assert.Equal(0.5f, AudioTestData.Peak(rendered), 0.001f);

        live.Complete();
        AudioTestData.Render(device, 1_024);
        engine.Update();

        Assert.False(engine.IsPlaying(handle));
    }

    [Fact]
    public void ALiveSourceCannotBeSeeked() {
        var live = new LiveSampleProvider(AudioFormat.Mono48k);

        Assert.Throws<NotSupportedException>(() => live.Seek(0));
    }
}

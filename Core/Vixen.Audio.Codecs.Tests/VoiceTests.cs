// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Codecs;
using Xunit;

namespace Vixen.Audio.Codecs.Tests;

/// <summary>
///     The voice path, driven by a test standing in for a network — which is the only way to
///     deliberately lose, reorder and delay a packet, and those are the cases the whole design is
///     about.
/// </summary>
public sealed class VoiceTests {
    const int Rate = 48_000;
    const int Frame = 960; // 20 ms
    const float ToneHz = 440f;

    /// <summary>A tone of a given amplitude, as the capture device would hand it over.</summary>
    static float[] Tone(int frames, float amplitude = 0.5f, int offset = 0) {
        var pcm = new float[frames];

        for (var i = 0; i < frames; i++) {
            pcm[i] = amplitude * MathF.Sin(2f * MathF.PI * ToneHz * (i + offset) / Rate);
        }

        return pcm;
    }

    /// <summary>How much of a given frequency is in a signal. A sine of peak A reads A/2.</summary>
    static float Correlation(ReadOnlySpan<float> mono, float hertz = ToneHz) {
        double real = 0, imaginary = 0;

        for (var i = 0; i < mono.Length; i++) {
            var angle = 2.0 * Math.PI * hertz * i / Rate;
            real += mono[i] * Math.Cos(angle);
            imaginary += mono[i] * Math.Sin(angle);
        }

        return (float)(Math.Sqrt((real * real) + (imaginary * imaginary)) / mono.Length);
    }

    static float Peak(ReadOnlySpan<float> pcm) {
        var peak = 0f;

        foreach (var sample in pcm) {
            peak = MathF.Max(peak, MathF.Abs(sample));
        }

        return peak;
    }

    /// <summary>Reads everything the receiver has buffered, and no further.</summary>
    /// <remarks>
    ///     Bounded by <c>BufferedFrames</c> rather than by a short read: the provider answers an
    ///     underrun with silence rather than with nothing, so a loop waiting for a short read never
    ///     gets one.
    /// </remarks>
    static float[] Drain(VoiceReceiver receiver) {
        var channels = receiver.Format.Channels;
        var collected = new List<float>();
        var block = new float[Frame * channels];

        while (receiver.Provider.BufferedFrames > 0) {
            var wanted = Math.Min(Frame, receiver.Provider.BufferedFrames);
            var frames = receiver.Provider.Read(block, wanted);

            if (frames <= 0) {
                break;
            }

            collected.AddRange(block.AsSpan(0, frames * channels).ToArray());
        }

        return [.. collected];
    }

    // ── The codec itself ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void APacketIsAFractionOfWhatTheFramePcmWouldBe() {
        using var encoder = new OpusPacketEncoder();
        var packet = new byte[OpusPacketEncoder.MaxPacketBytes];

        var written = encoder.Encode(Tone(Frame), packet);

        Assert.True(written > 0);

        // 20 ms of mono float PCM is 3 840 bytes. At 24 kbit a packet is about 60.
        Assert.True(written < 200, $"a packet came to {written} bytes");
    }

    [Fact]
    public void WhatComesOutOfTheDecoderIsWhatWentIntoTheEncoder() {
        using var encoder = new OpusPacketEncoder();
        using var decoder = new OpusPacketDecoder();
        var packet = new byte[OpusPacketEncoder.MaxPacketBytes];
        var pcm = new float[Frame];
        var collected = new List<float>();

        // Several frames: Opus has a warm-up, and the first packet out of any encoder is not
        // representative of the ones after it.
        for (var i = 0; i < 10; i++) {
            var written = encoder.Encode(Tone(Frame, 0.5f, i * Frame), packet);
            var frames = decoder.Decode(packet.AsSpan(0, written), pcm);
            Assert.Equal(Frame, frames);
            collected.AddRange(pcm);
        }

        // The last few frames, by which point the encoder has settled.
        var settled = collected.ToArray().AsSpan(Frame * 5);
        Assert.True(Correlation(settled) > 0.2f, $"the tone correlated at {Correlation(settled):F4}, wanted about 0.25");
    }

    /// <summary>
    ///     The claim concealment actually makes: something plausible and not a click, and not the
    ///     silence a naive decoder would leave.
    /// </summary>
    [Fact]
    public void ConcealmentInventsSomethingRatherThanLeavingAHole() {
        using var encoder = new OpusPacketEncoder();
        using var decoder = new OpusPacketDecoder();
        var packet = new byte[OpusPacketEncoder.MaxPacketBytes];
        var pcm = new float[Frame];

        for (var i = 0; i < 10; i++) {
            var written = encoder.Encode(Tone(Frame, 0.5f, i * Frame), packet);
            decoder.Decode(packet.AsSpan(0, written), pcm);
        }

        var concealed = new float[Frame];
        var frames = decoder.Conceal(concealed);

        Assert.Equal(Frame, frames);
        Assert.Equal(1, decoder.Concealed);

        // It carried the talker forward rather than going quiet.
        Assert.True(Peak(concealed) > 0.05f, $"concealment produced a peak of only {Peak(concealed):F4}");
        Assert.True(Correlation(concealed) > 0.05f, $"and correlated at {Correlation(concealed):F4}");
    }

    [Fact]
    public void ConcealingWithNothingToGoOnIsSilenceRatherThanNoise() {
        using var decoder = new OpusPacketDecoder();
        var pcm = new float[Frame];

        Assert.Equal(Frame, decoder.Conceal(pcm));
        Assert.Equal(0f, Peak(pcm));
    }

    [Fact]
    public void ADamagedPacketIsConcealedRatherThanThrown() {
        using var decoder = new OpusPacketDecoder();
        var rubbish = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        var pcm = new float[Frame];

        Assert.Equal(Frame, decoder.Decode(rubbish, pcm));
        Assert.Equal(1, decoder.Concealed);
    }

    [Fact]
    public void AFrameThatIsNotAWholeFrameIsRefused() {
        using var encoder = new OpusPacketEncoder();
        var packet = new byte[OpusPacketEncoder.MaxPacketBytes];

        Assert.Throws<ArgumentException>(() => encoder.Encode(new float[Frame - 1], packet));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(25)]
    public void AFrameLengthOpusDoesNotHaveIsRefused(int milliseconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusPacketEncoder(1, milliseconds));

    // ── Redundancy ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Redundancy costs bitrate, so asking for it should visibly buy something: the same speech
    ///     at the same bitrate settings comes out bigger, because each packet is now carrying a copy
    ///     of the one before it.
    /// </summary>
    [Fact]
    public void AskingForRedundancySpendsBitrateOnIt() {
        static int Bytes(int expectedLoss) {
            using var encoder = new OpusPacketEncoder(1, 20, 24_000) { ExpectedPacketLoss = expectedLoss };
            var packet = new byte[OpusPacketEncoder.MaxPacketBytes];
            var total = 0;

            for (var i = 0; i < 40; i++) {
                total += encoder.Encode(Tone(Frame, 0.5f, i * Frame), packet);
            }

            return total;
        }

        var without = Bytes(0);
        var with = Bytes(30);

        Assert.True(with > without, $"redundancy on came to {with} bytes, off came to {without}");
    }

    [Fact]
    public void AskingForRedundancyTurnsItOnAndOff() {
        using var encoder = new OpusPacketEncoder();

        Assert.Equal(0, encoder.ExpectedPacketLoss);

        encoder.ExpectedPacketLoss = 15;
        Assert.Equal(15, encoder.ExpectedPacketLoss);

        encoder.ExpectedPacketLoss = 0;
        Assert.Equal(0, encoder.ExpectedPacketLoss);
    }

    /// <summary>
    ///     Handing the successor over is never worse than not doing: with redundancy present it is
    ///     nearly the real frame, and without it the decoder falls back to the same extrapolation it
    ///     would have done anyway.
    /// </summary>
    [Fact]
    public void ConcealingWithTheSuccessorInHandStillProducesAFrame() {
        using var encoder = new OpusPacketEncoder { ExpectedPacketLoss = 20 };
        using var decoder = new OpusPacketDecoder();
        var packets = new List<byte[]>();
        var pcm = new float[Frame];

        for (var i = 0; i < 12; i++) {
            var buffer = new byte[OpusPacketEncoder.MaxPacketBytes];
            var written = encoder.Encode(Tone(Frame, 0.5f, i * Frame), buffer);
            packets.Add(buffer.AsSpan(0, written).ToArray());
        }

        // Play the first ten so the decoder has real state, then lose the eleventh and hand it the
        // twelfth, which is carrying a copy of what was lost.
        for (var i = 0; i < 10; i++) {
            decoder.Decode(packets[i], pcm);
        }

        var recovered = new float[Frame];
        var frames = decoder.Conceal(recovered, packets[11]);

        Assert.Equal(Frame, frames);
        Assert.Equal(1, decoder.Concealed);
        Assert.True(Peak(recovered) > 0.05f, $"it produced a peak of only {Peak(recovered):F4}");
        Assert.True(Correlation(recovered) > 0.05f, $"and correlated at {Correlation(recovered):F4}");
    }

    // ── The sender ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CaptureArrivesInWhateverBlockTheDeviceFeltLikeAndComesOutInFrames() {
        using var sender = new VoiceSender();
        var odd = Tone(441, 0.6f);
        var produced = 0;

        var packet = new byte[OpusPacketEncoder.MaxPacketBytes];
        var read = 0;

        // 441 samples at a time, which is what a 44.1 kHz-shaped device would do, into 960-frame
        // packets. Nothing about that ratio is whole. Drained as it goes, as a caller would.
        for (var i = 0; i < 20; i++) {
            produced += sender.Write(odd);

            while (sender.TryRead(packet, out var header, out var length)) {
                Assert.True(length > 0);
                Assert.Equal(read, header.Sequence);
                Assert.Equal((uint)(read * Frame), header.Timestamp);
                read++;
            }
        }

        // 8 820 samples is 9 whole frames and a remainder that is held.
        Assert.Equal(9, produced);
        Assert.Equal(produced, read);
        Assert.Equal(0, sender.Overrun);
    }

    /// <summary>The bandwidth claim: a player not talking costs nothing at all.</summary>
    [Fact]
    public void NobodyTalkingSendsNothing() {
        using var sender = new VoiceSender();
        var silence = new float[Frame];

        for (var i = 0; i < 30; i++) {
            sender.Write(silence);
        }

        Assert.Equal(0, sender.Available);
        Assert.Equal(0, sender.Sent);
        Assert.True(sender.Suppressed > 0);
        Assert.False(sender.IsTransmitting);
    }

    [Fact]
    public void SomebodyTalkingSends() {
        using var sender = new VoiceSender();

        for (var i = 0; i < 10; i++) {
            sender.Write(Tone(Frame, 0.6f, i * Frame));
        }

        Assert.True(sender.Sent > 0);
        Assert.True(sender.IsTransmitting);
    }

    /// <summary>
    ///     Push-to-talk: the player is holding the key and saying nothing, and the silence is
    ///     meaningful.
    /// </summary>
    [Fact]
    public void SendingWhileSilentIsAvailableForPushToTalk() {
        using var sender = new VoiceSender { SendWhileSilent = true };
        var silence = new float[Frame];

        for (var i = 0; i < 5; i++) {
            sender.Write(silence);
        }

        Assert.Equal(5, sender.Sent);
        Assert.Equal(0, sender.Suppressed);
    }

    /// <summary>The timestamp is the talker's clock, so it runs through a pause that sends nothing.</summary>
    [Fact]
    public void TheTimestampRunsThroughSilenceAndTheSequenceDoesNot() {
        using var sender = new VoiceSender();
        var packet = new byte[OpusPacketEncoder.MaxPacketBytes];
        var headers = new List<VoicePacketHeader>();

        int Speak(int frames, bool aloud) {
            var before = headers.Count;

            for (var i = 0; i < frames; i++) {
                sender.Write(aloud ? Tone(Frame, 0.6f, i * Frame) : new float[Frame]);

                // Drained every frame, as a caller would — otherwise the backlog caps what is
                // observable and the counts stop meaning anything.
                while (sender.TryRead(packet, out var header, out _)) {
                    headers.Add(header);
                }
            }

            return headers.Count - before;
        }

        Speak(6, aloud: true);
        var silent = Speak(40, aloud: false);
        Speak(6, aloud: true);

        var first = headers[0];
        var last = headers[^1];

        // 51 frames of the talker's clock separate the first packet from the last, and the timestamp
        // counted every one of them: it is the timeline, and it does not stop for a pause.
        Assert.Equal(51u * Frame, last.Timestamp - first.Timestamp);

        // The sequence counted only what was actually transmitted, which is fewer.
        Assert.True(
            last.Sequence - first.Sequence < 51,
            "every frame was transmitted, so the gate never shut and the two counters would agree"
        );

        // The gate holds for 150 ms and releases over 200 more, so it keeps sending briefly after the
        // talking stops — and then stops entirely, well short of all 40.
        Assert.True(silent is > 0 and < 30, $"{silent} of 40 silent frames were transmitted");
        Assert.True(sender.Suppressed > 0);
    }

    [Fact]
    public void ACallerThatNeverDrainsLosesTheOldestRatherThanTheNewest() {
        using var sender = new VoiceSender();

        for (var i = 0; i < VoiceSender.Backlog + 6; i++) {
            sender.Write(Tone(Frame, 0.6f, i * Frame));
        }

        Assert.Equal(VoiceSender.Backlog, sender.Available);
        Assert.True(sender.Overrun > 0);

        var packet = new byte[OpusPacketEncoder.MaxPacketBytes];
        Assert.True(sender.TryRead(packet, out var oldest, out _));

        // What is left starts after the ones that were dropped.
        Assert.True(oldest.Sequence > 0, "the oldest packet was still sequence 0, so the newest was dropped instead");
    }

    // ── The receiver ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AStraightRunThroughArrivesAsTheToneThatWasSpoken() {
        using var sender = new VoiceSender();
        using var receiver = new VoiceReceiver();
        var packet = new byte[OpusPacketEncoder.MaxPacketBytes];

        for (var i = 0; i < 25; i++) {
            sender.Write(Tone(Frame, 0.6f, i * Frame));

            while (sender.TryRead(packet, out var header, out var length)) {
                receiver.Receive(header, packet.AsSpan(0, length));
            }

            receiver.Pump();
        }

        var heard = Drain(receiver);

        Assert.True(heard.Length > Frame * 10, $"only {heard.Length} frames came out");
        Assert.Equal(0, receiver.Concealed);

        var settled = heard.AsSpan(Frame * 4, Frame * 8);
        Assert.True(Correlation(settled) > 0.15f, $"the tone correlated at {Correlation(settled):F4}");
    }

    /// <summary>
    ///     The distinction the two counters exist for. A pause is not concealed, because concealing it
    ///     would invent speech into a silence the talker chose.
    /// </summary>
    [Fact]
    public void APauseIsNotMistakenForLoss() {
        using var receiver = new VoiceReceiver(depth: 0);
        using var encoder = new OpusPacketEncoder();
        var packet = new byte[OpusPacketEncoder.MaxPacketBytes];

        // Two packets whose sequence is contiguous but whose timestamps are a second apart: exactly
        // what a sender's gate produces when somebody stops talking and starts again.
        var first = encoder.Encode(Tone(Frame, 0.6f), packet);
        receiver.Receive(new VoicePacketHeader(0, 0), packet.AsSpan(0, first));
        receiver.Pump();

        var second = encoder.Encode(Tone(Frame, 0.6f, Frame), packet);
        receiver.Receive(new VoicePacketHeader(1, Rate), packet.AsSpan(0, second));
        receiver.Pump();

        Assert.Equal(0, receiver.Concealed);
        Assert.True(receiver.Silences > 0);
    }

    /// <summary>And the other half of it: a real gap is concealed.</summary>
    [Fact]
    public void ALostPacketIsConcealed() {
        using var receiver = new VoiceReceiver(depth: 0);
        using var encoder = new OpusPacketEncoder();
        var packet = new byte[OpusPacketEncoder.MaxPacketBytes];

        var first = encoder.Encode(Tone(Frame, 0.6f), packet);
        receiver.Receive(new VoicePacketHeader(0, 0), packet.AsSpan(0, first));
        receiver.Pump();

        // Sequence 1 never arrives. Sequence 2 does, one frame further along the clock.
        encoder.Encode(Tone(Frame, 0.6f, Frame), packet);
        var third = encoder.Encode(Tone(Frame, 0.6f, 2 * Frame), packet);
        receiver.Receive(new VoicePacketHeader(2, 2 * Frame), packet.AsSpan(0, third));
        receiver.Pump();

        Assert.True(receiver.Concealed > 0, "the gap was played through as though nothing were missing");
        Assert.Equal(0, receiver.Silences);
    }

    [Fact]
    public void PacketsArrivingOutOfOrderArePutBackIntoIt() {
        using var receiver = new VoiceReceiver(depth: 2);
        using var encoder = new OpusPacketEncoder();

        var encoded = new List<byte[]>();

        for (var i = 0; i < 6; i++) {
            var buffer = new byte[OpusPacketEncoder.MaxPacketBytes];
            var written = encoder.Encode(Tone(Frame, 0.6f, i * Frame), buffer);
            encoded.Add(buffer.AsSpan(0, written).ToArray());
        }

        // 0, then 2 before 1, then 3, 5 before 4.
        foreach (var i in new[] { 0, 2, 1, 3, 5, 4 }) {
            receiver.Receive(new VoicePacketHeader((ushort)i, (uint)(i * Frame)), encoded[i]);
        }

        receiver.Flush();

        Assert.True(receiver.Reordered > 0, "nothing was noticed as out of order");
        Assert.Equal(0, receiver.Concealed);
        Assert.Equal(6, receiver.Received);
    }

    [Fact]
    public void APacketThatArrivesAfterItsMomentIsDropped() {
        using var receiver = new VoiceReceiver(depth: 0);
        using var encoder = new OpusPacketEncoder();
        var packet = new byte[OpusPacketEncoder.MaxPacketBytes];

        var first = encoder.Encode(Tone(Frame, 0.6f), packet);
        receiver.Receive(new VoicePacketHeader(0, 0), packet.AsSpan(0, first));
        receiver.Pump();

        var second = encoder.Encode(Tone(Frame, 0.6f, Frame), packet);
        receiver.Receive(new VoicePacketHeader(1, Frame), packet.AsSpan(0, second));
        receiver.Pump();

        // Now the one from the beginning turns up, long after it was played past.
        Assert.False(receiver.Receive(new VoicePacketHeader(0, 0), packet.AsSpan(0, first)));
        Assert.Equal(1, receiver.Late);
    }

    /// <summary>The buffer is the trade: nothing plays until the cushion is there.</summary>
    [Fact]
    public void NothingIsPlayedUntilTheCushionIsFull() {
        using var receiver = new VoiceReceiver(depth: 3);
        using var encoder = new OpusPacketEncoder();
        var packet = new byte[OpusPacketEncoder.MaxPacketBytes];

        for (ushort i = 0; i < 3; i++) {
            var written = encoder.Encode(Tone(Frame, 0.6f, i * Frame), packet);
            receiver.Receive(new VoicePacketHeader(i, (uint)(i * Frame)), packet.AsSpan(0, written));
            Assert.Equal(0, receiver.Pump());
        }

        var fourth = encoder.Encode(Tone(Frame, 0.6f, 3 * Frame), packet);
        receiver.Receive(new VoicePacketHeader(3, 3 * Frame), packet.AsSpan(0, fourth));

        Assert.True(receiver.Pump() > 0, "a fourth packet with a depth of three still played nothing");
    }

    /// <summary>Two talkers are two decoders, because Opus carries state between packets.</summary>
    [Fact]
    public void EachTalkerIsItsOwnReceiverAndItsOwnProvider() {
        using var one = new VoiceReceiver();
        using var two = new VoiceReceiver();

        Assert.NotSame(one.Provider, two.Provider);
        Assert.Equal(one.Format, two.Format);
    }
}

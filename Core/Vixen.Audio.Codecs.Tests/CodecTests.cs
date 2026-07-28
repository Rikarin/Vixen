// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio;
using Vixen.Audio.Codecs;
using Vixen.Audio.Streaming;
using Xunit;

namespace Vixen.Audio.Codecs.Tests;

/// <summary>
///     Vorbis and Opus, against streams a real encoder produced. Every fixture is one second of a
///     440 Hz sine at 48 kHz and an amplitude of 0.7, which is a signal whose every property is known
///     before it is decoded — and 0.7 rather than full scale so that neither encoder is asked to
///     represent something that clips.
/// </summary>
public sealed class CodecTests {
    const int Rate = 48_000;
    const float ToneHz = 440f;

    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>Drains a decoder into one buffer.</summary>
    static float[] DecodeAll(IAudioStreamDecoder decoder, int limit = Rate * 4) {
        var channels = decoder.Format.Channels;
        var collected = new List<float>();
        var block = new float[1_024 * channels];

        while (collected.Count / channels < limit) {
            var frames = decoder.Decode(block, 1_024);

            if (frames <= 0) {
                break;
            }

            collected.AddRange(block.AsSpan(0, frames * channels).ToArray());
        }

        return [.. collected];
    }

    /// <summary>The frequency with the most energy in a mono signal, found by correlation.</summary>
    /// <remarks>
    ///     A correlation rather than a transform, because the answer is known to within a few hertz
    ///     and testing it against a candidate is a dozen lines where a transform would be a
    ///     dependency this assembly does not otherwise have.
    /// </remarks>
    /// <summary>What a correlation reads for the fixture's own tone: half its amplitude.</summary>
    /// <remarks>
    ///     A sine of peak <c>A</c> correlated against its own frequency comes out at <c>A/2</c>, which
    ///     for the 0.7 the fixtures were made at is 0.35. Everything below is a fraction of that
    ///     rather than an absolute, so regenerating a fixture at a different level moves one constant.
    /// </remarks>
    const float Expected = 0.35f;

    static float Correlation(ReadOnlySpan<float> mono, float hertz) {
        var real = 0.0;
        var imaginary = 0.0;

        for (var i = 0; i < mono.Length; i++) {
            var angle = 2.0 * Math.PI * hertz * i / Rate;
            real += mono[i] * Math.Cos(angle);
            imaginary += mono[i] * Math.Sin(angle);
        }

        return (float)(Math.Sqrt((real * real) + (imaginary * imaginary)) / mono.Length);
    }

    static float[] Channel(float[] interleaved, int channels, int channel) {
        var mono = new float[interleaved.Length / channels];

        for (var frame = 0; frame < mono.Length; frame++) {
            mono[frame] = interleaved[(frame * channels) + channel];
        }

        return mono;
    }

    [Fact]
    public void VorbisReportsWhatTheContainerSays() {
        using var decoder = new VorbisStreamDecoder(Fixture("tone-stereo.ogg"));

        Assert.Equal(Rate, decoder.Format.SampleRate);
        Assert.Equal(2, decoder.Format.Channels);
        Assert.True(decoder.CanSeek);

        // One second, give or take whatever the encoder's own padding came to.
        Assert.InRange(decoder.FrameCount, Rate - 2_000, Rate + 2_000);
    }

    /// <summary>The claim that matters: what comes out is the tone that went in.</summary>
    [Fact]
    public void VorbisDecodesTheToneItWasGiven() {
        using var decoder = new VorbisStreamDecoder(Fixture("tone-stereo.ogg"));
        var decoded = DecodeAll(decoder);

        Assert.InRange(decoded.Length / 2, Rate - 2_000, Rate + 2_000);

        // Away from the ends, where an encoder's window has faded the signal.
        var mono = Channel(decoded, 2, 0).AsSpan(4_800, 24_000);

        Assert.True(Correlation(mono, ToneHz) > Expected * 0.8f, $"the tone correlated at only {Correlation(mono, ToneHz):F4}, wanted about {Expected:F2}");
        Assert.True(Correlation(mono, ToneHz * 3f) < Expected * 0.05f, $"a third harmonic at {Correlation(mono, ToneHz * 3f):F4}");
    }

    [Fact]
    public void VorbisSeeksAndDecodesTheSameThingFromThere() {
        using var decoder = new VorbisStreamDecoder(Fixture("tone-stereo.ogg"));

        decoder.Seek(24_000);
        Assert.InRange(decoder.Position, 23_000, 25_000);

        var buffer = new float[4_800 * 2];
        var frames = decoder.Decode(buffer, 4_800);

        Assert.Equal(4_800, frames);
        Assert.True(Correlation(Channel(buffer, 2, 0), ToneHz) > Expected * 0.8f, $"correlated at {Correlation(Channel(buffer, 2, 0), ToneHz):F4}");
    }

    [Fact]
    public void VorbisRefusesSomethingThatIsNotVorbis() {
        using var stream = new MemoryStream(new byte[4_096]);
        Assert.Throws<InvalidDataException>(() => new VorbisStreamDecoder(stream, leaveOpen: true));
    }

    /// <summary>Whatever the file's "input sample rate" says, Opus decodes at 48 and only at 48.</summary>
    [Fact]
    public void OpusAlwaysDecodesAtFortyEightThousand() {
        using var mono = new OpusStreamDecoder(Fixture("tone-mono.opus"));
        using var stereo = new OpusStreamDecoder(Fixture("tone-stereo.opus"));

        Assert.Equal(OpusStreamDecoder.DecodeRate, mono.Format.SampleRate);
        Assert.Equal(1, mono.Format.Channels);
        Assert.Equal(2, stereo.Format.Channels);
        Assert.True(mono.CanSeek);

        // The container does not carry a length without reading to the end of it.
        Assert.Equal(-1, mono.FrameCount);
    }

    [Fact]
    public void OpusDecodesTheToneItWasGiven() {
        using var decoder = new OpusStreamDecoder(Fixture("tone-mono.opus"));
        var decoded = DecodeAll(decoder);

        Assert.InRange(decoded.Length, Rate - 3_000, Rate + 3_000);

        var mono = decoded.AsSpan(4_800, 24_000);
        Assert.True(Correlation(mono, ToneHz) > Expected * 0.8f, $"the tone correlated at only {Correlation(mono, ToneHz):F4}, wanted about {Expected:F2}");
        Assert.True(Correlation(mono, ToneHz * 3f) < Expected * 0.05f, $"a third harmonic at {Correlation(mono, ToneHz * 3f):F4}");
    }

    [Fact]
    public void OpusDecodesStereoToo() {
        using var decoder = new OpusStreamDecoder(Fixture("tone-stereo.opus"));
        var decoded = DecodeAll(decoder);

        Assert.Equal(2, decoder.Format.Channels);

        foreach (var channel in new[] { 0, 1 }) {
            var mono = Channel(decoded, 2, channel).AsSpan(4_800, 24_000);
            Assert.True(Correlation(mono, ToneHz) > Expected * 0.8f, $"channel {channel} correlated at {Correlation(mono, ToneHz):F4}");
        }
    }

    /// <summary>
    ///     A decoder that ignores the pre-skip starts every track with a few milliseconds of the
    ///     encoder's priming, which is an artefact rather than music.
    /// </summary>
    [Fact]
    public void OpusDiscardsThePrimingSamples() {
        using var decoder = new OpusStreamDecoder(Fixture("tone-mono.opus"));

        // Position counts from after the pre-skip, so the first frame decoded is frame zero of the
        // audio rather than frame zero of the stream.
        Assert.Equal(0, decoder.Position);

        var buffer = new float[480];
        Assert.Equal(480, decoder.Decode(buffer, 480));
        Assert.Equal(480, decoder.Position);
    }

    [Fact]
    public void OpusSeeksAndDecodesTheSameThingFromThere() {
        using var decoder = new OpusStreamDecoder(Fixture("tone-mono.opus"));

        decoder.Seek(24_000);
        Assert.Equal(24_000, decoder.Position);

        var buffer = new float[4_800];
        Assert.Equal(4_800, decoder.Decode(buffer, 4_800));
        Assert.True(Correlation(buffer, ToneHz) > Expected * 0.8f, $"correlated at {Correlation(buffer, ToneHz):F4}");

        // And back to the beginning, which is the case a loop point actually uses.
        decoder.Seek(0);
        Assert.Equal(0, decoder.Position);
        Assert.Equal(4_800, decoder.Decode(buffer, 4_800));
    }

    [Fact]
    public void OpusRefusesSomethingThatIsNotOpus() {
        using var notOgg = new MemoryStream(new byte[4_096]);
        Assert.Throws<InvalidDataException>(() => new OpusStreamDecoder(notOgg, leaveOpen: true));

        // A real Ogg, but a Vorbis one — so the container parses and the first packet is not OpusHead.
        using var vorbis = File.OpenRead(Fixture("tone-stereo.ogg"));
        Assert.Throws<InvalidDataException>(() => new OpusStreamDecoder(vorbis, leaveOpen: true));
    }

    [Fact]
    public void DecodingPastTheEndReturnsNothingRatherThanRepeating() {
        using var decoder = new OpusStreamDecoder(Fixture("tone-mono.opus"));
        DecodeAll(decoder);

        var buffer = new float[480];
        Assert.Equal(0, decoder.Decode(buffer, 480));
        Assert.Equal(0, decoder.Decode(buffer, 480));
    }

    /// <summary>Which is the whole point of a codec, and the only number that justifies one.</summary>
    [Fact]
    public void TheEncodedFilesAreAFractionOfWhatThePcmWouldBe() {
        var pcm = Rate * 2 * sizeof(float);

        Assert.True(new FileInfo(Fixture("tone-stereo.ogg")).Length < pcm / 10);
        Assert.True(new FileInfo(Fixture("tone-stereo.opus")).Length < pcm / 10);
    }

    /// <summary>Both are decoders behind one seam, which is what lets the engine link neither.</summary>
    [Fact]
    public void BothAreOrdinaryStreamDecoders() {
        IAudioStreamDecoder[] decoders = [
            new VorbisStreamDecoder(Fixture("tone-stereo.ogg")),
            new OpusStreamDecoder(Fixture("tone-mono.opus"))
        ];

        foreach (var decoder in decoders) {
            using (decoder) {
                Assert.Equal(Rate, decoder.Format.SampleRate);
                Assert.True(decoder.Format.Channels is 1 or 2);

                var buffer = new float[1_024 * decoder.Format.Channels];
                Assert.True(decoder.Decode(buffer, 1_024) > 0);
            }
        }
    }
}

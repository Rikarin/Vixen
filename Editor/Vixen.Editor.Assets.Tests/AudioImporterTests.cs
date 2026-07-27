// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Audio;
using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Editor.Assets.Audio;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

public sealed class AudioImporterTests {
    [Fact]
    public void ItClaimsTheFormatsDoc08Lists() {
        var importer = new AudioImporter();

        Assert.Equal("AudioImporter", importer.Name);
        Assert.Equal([".wav", ".wave", ".ogg", ".mp3", ".flac"], importer.Extensions);
    }

    [Fact]
    public async Task AClipComesOutAsAChunkTheRuntimeCanRead() {
        var result = await Import("shot.wav", Int16(48_000, 1, [100, -100]));

        Assert.True(result.Succeeded);

        var clip = Serializer.Read<AudioClip>(Assert.Single(result.Artifacts).Content.Span.ToArray());

        Assert.Equal(48_000, clip.SampleRate);
        Assert.Equal(1, clip.Channels);
        Assert.Equal([100, -100], clip.AsInt16().ToArray());
    }

    /// <summary>
    ///     The setting that earns its place. A stereo clip already says which ear it is in, so
    ///     panning does nothing and the sound stays in the listener's head wherever its emitter is.
    ///     Every 3D sound has to be mono and the source usually is not.
    /// </summary>
    [Fact]
    public async Task ForceMonoAveragesTheChannelsRatherThanSummingThem() {
        // Two frames of stereo: (1000, 2000) and (−400, −800). Summing would give 3000 and −1200,
        // which is how a clip mastered near full scale ends up clipping when it is made mono.
        var result = await Import(
            "ambience.wav",
            Int16(44_100, 2, [1000, 2000, -400, -800]),
            new() { ForceMono = true }
        );

        var clip = Serializer.Read<AudioClip>(Assert.Single(result.Artifacts).Content.Span.ToArray());

        Assert.Equal(1, clip.Channels);
        Assert.Equal([1500, -600], clip.AsInt16().ToArray());
    }

    [Fact]
    public async Task AutomaticKeepsWhateverTheFileHeld() {
        var result = await Import("music.wav", Float32(44_100, 1, [0.5f, -0.5f]));

        var clip = Serializer.Read<AudioClip>(Assert.Single(result.Artifacts).Content.Span.ToArray());

        Assert.Equal(AudioSampleFormat.Float32, clip.Format);
    }

    /// <summary>
    ///     Scaled by 32 767 and clamped, not by 32 768. A float source is allowed to reach exactly
    ///     1.0 and 32 768 is not a <c>short</c>; scaling by the larger number would wrap the loudest
    ///     sample of every normalised clip to full-scale negative.
    /// </summary>
    [Fact]
    public async Task FullScaleFloatSurvivesTheConversionToSixteenBit() {
        var result = await Import(
            "music.wav",
            Float32(44_100, 1, [1f, -1f, 0f]),
            new() { Format = AudioFormatChoice.Int16 }
        );

        var clip = Serializer.Read<AudioClip>(Assert.Single(result.Artifacts).Content.Span.ToArray());

        Assert.Equal(AudioSampleFormat.Int16, clip.Format);
        Assert.Equal([32_767, -32_767, 0], clip.AsInt16().ToArray());
    }

    /// <summary>
    ///     Divided by 32 768 the other way, so the full negative range maps to exactly −1 and nothing
    ///     overshoots.
    /// </summary>
    [Fact]
    public async Task SixteenBitWidensToFloatWithoutOvershooting() {
        var result = await Import(
            "shot.wav",
            Int16(48_000, 1, [-32_768, 0]),
            new() { Format = AudioFormatChoice.Float32 }
        );

        var clip = Serializer.Read<AudioClip>(Assert.Single(result.Artifacts).Content.Span.ToArray());

        Assert.Equal([-1f, 0f], clip.AsFloat32().ToArray());
    }

    /// <summary>
    ///     A deviation from <c>TextureImporter</c>, which claims only what it decodes. An artist who
    ///     drops an <c>.ogg</c> in and finds it silently became an unplayable byte blob has learned
    ///     nothing; this says what is missing.
    /// </summary>
    [Fact]
    public async Task AFormatWithNoDecoderIsRefusedByNameRatherThanShippedAsABlob() {
        var result = await Import("music.ogg", [1, 2, 3, 4]);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Artifacts);
        Assert.Contains(".ogg", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedFileFailsThatAssetAndNothingElse() {
        var result = await Import("shot.wav", "this is not audio"u8.ToArray());

        Assert.False(result.Succeeded);
        Assert.Contains("RIFF", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileThatDecodesToNothingIsCarriedForwardWithAWarning() {
        var result = await Import("silence.wav", Int16(48_000, 1, []));

        Assert.True(result.Succeeded);
        Assert.Single(result.Artifacts);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == ImportSeverity.Warning);
    }

    static async Task<ImportResult> Import(string name, byte[] bytes, AudioImportSettings? settings = null) {
        var path = new VirtualPath("/Assets/" + name);
        var files = new MemoryFileProvider();
        files.Seed(path, bytes);

        var importer = new AudioImporter();

        var context = new ImportContext(
            AssetId.New(),
            path,
            settings ?? new AudioImportSettings(),
            files,
            importer.Name,
            "Windows"
        );

        return await importer.ImportAsync(context, TestContext.Current.CancellationToken);
    }

    static byte[] Int16(int rate, int channels, short[] samples) {
        var data = new byte[samples.Length * 2];

        for (var index = 0; index < samples.Length; index++) {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(index * 2), samples[index]);
        }

        return Riff(0x0001, rate, channels, 16, data);
    }

    static byte[] Float32(int rate, int channels, float[] samples) {
        var data = new byte[samples.Length * 4];

        for (var index = 0; index < samples.Length; index++) {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(index * 4), samples[index]);
        }

        return Riff(0x0003, rate, channels, 32, data);
    }

    static byte[] Riff(int tag, int rate, int channels, int bits, byte[] data) {
        var format = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(format, (ushort)tag);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteInt32LittleEndian(format.AsSpan(4), rate);
        BinaryPrimitives.WriteInt32LittleEndian(format.AsSpan(8), rate * channels * (bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(12), (ushort)(channels * (bits / 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(14), (ushort)bits);

        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(format.Length);
        writer.Write(format);
        writer.Write("data"u8);
        writer.Write(data.Length);
        writer.Write(data);
        writer.Flush();

        var bytes = buffer.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);

        return bytes;
    }
}

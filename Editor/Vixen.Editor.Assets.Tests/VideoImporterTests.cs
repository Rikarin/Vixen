// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Core.Serialization;
using Vixen.Editor.Assets.Video;
using Vixen.Video;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     The importer that decodes nothing: it reads the header, copies the container, and writes down
///     what a game needs to know before it opens the file.
/// </summary>
public sealed class VideoImporterTests {
    [Fact]
    public void ItClaimsTheFormatsDoc08Lists() {
        var importer = new VideoImporter();

        Assert.Equal("VideoImporter", importer.Name);
        Assert.Equal([".webm", ".mkv", ".mp4", ".m4v", ".mov"], importer.Extensions);
    }

    [Fact]
    public async Task AClipCarriesWhatTheContainerSaid() {
        var result = await Import("intro.webm", WebM(320, 180, frames: 5));

        Assert.True(result.Succeeded);

        var clip = Read(result);

        Assert.Equal(320, clip.Width);
        Assert.Equal(180, clip.Height);
        Assert.Equal("V_UNCOMPRESSED", clip.CodecId);
        Assert.Equal(TimeSpan.FromMilliseconds(200), clip.Duration);
        Assert.InRange(clip.FrameRate.Hz, 24.9, 25.1);
        Assert.False(clip.HasAudio);
    }

    [Fact]
    public async Task TheCodecIsRecordedSoAGameCanFindOutBeforeTheCutsceneStarts() {
        // The field that earns its place: a title with no VP9 decoder wants to know that at the menu,
        // not at the moment the cutscene was supposed to play.
        var result = await Import("intro.webm", WebM(64, 64, frames: 1, videoCodec: "V_VP9"));

        Assert.Equal("V_VP9", Read(result).CodecId);
    }

    [Fact]
    public async Task AnAudioTrackIsNoticedAndNamed() {
        var result = await Import("intro.webm", WebM(64, 64, frames: 2, audioCodec: "A_OPUS"));

        var clip = Read(result);

        Assert.True(clip.HasAudio);
        Assert.Equal("A_OPUS", clip.AudioCodecId);
    }

    [Fact]
    public async Task TheContainerIsCopiedBesideTheClip() {
        var bytes = WebM(64, 64, frames: 2);
        var result = await Import("intro.webm", bytes);

        Assert.Equal(2, result.Artifacts.Count);

        var container = result.Artifacts.Single(artifact => !artifact.SubAsset.IsMain);

        Assert.Equal(bytes, container.Content.Span.ToArray());
    }

    [Fact]
    public async Task NotCopyingItLeavesOnlyTheMetadata() {
        // What a title streaming its cutscenes from a CDN wants: the size, the duration and the codec
        // in the build, and the bytes fetched by something else.
        var result = await Import(
            "intro.webm",
            WebM(64, 64, frames: 2),
            new VideoImportSettings { EmbedContainer = false }
        );

        Assert.Single(result.Artifacts);
        Assert.True(result.Artifacts[0].SubAsset.IsMain);
    }

    [Fact]
    public async Task AnMp4SaysWhatIsMissingRatherThanShippingAByteBlob() {
        var result = await Import("intro.mp4", [0, 0, 0, 0x18, .. "ftypmp42"u8]);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("MP4", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task SomethingThatIsNotAVideoFailsAgainstTheAsset() {
        var result = await Import("intro.webm", [1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == ImportSeverity.Error);
    }

    [Fact]
    public async Task AFileWithNoCuesIsImportedAndWarnedAbout() {
        // Seeking without them rewinds and scans, which is correct and slow — and a remux fixes it
        // for a few kilobytes, which is worth telling somebody.
        var result = await Import("intro.webm", WebM(64, 64, frames: 3));

        Assert.True(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Severity == ImportSeverity.Warning
                && diagnostic.Message.Contains("cue index", StringComparison.Ordinal)
        );
    }

    static VideoClip Read(ImportResult result) =>
        Serializer.Read<VideoClip>(
            result.Artifacts.Single(artifact => artifact.SubAsset.IsMain).Content.Span.ToArray()
        );

    static async Task<ImportResult> Import(string name, byte[] bytes, VideoImportSettings? settings = null) {
        var path = new VirtualPath("/Assets/" + name);
        var files = new MemoryFileProvider();

        files.Seed(path, bytes);

        var importer = new VideoImporter();

        var context = new ImportContext(
            AssetId.New(),
            path,
            settings ?? new VideoImportSettings(),
            files,
            importer.Name,
            "Windows"
        );

        return await importer.ImportAsync(context, TestContext.Current.CancellationToken);
    }

    /// <summary>A WebM whose header says what the test wants to read back out of it.</summary>
    /// <remarks>
    ///     Written here rather than borrowed from <c>Vixen.Video.Tests</c>: what is under test is the
    ///     importer's reading of a header, so the file only has to be legal enough to open — and a
    ///     third copy of the muxer would be a third thing to keep in step for the sake of five
    ///     elements.
    /// </remarks>
    static byte[] WebM(int width, int height, int frames, string videoCodec = "V_UNCOMPRESSED", string? audioCodec = null) {
        using var body = new MemoryStream();

        using (var info = new MemoryStream()) {
            Element(info, 0x2AD7B1, Unsigned(1_000_000));
            Element(info, 0x4489, Float(frames * 40));
            Element(body, 0x1549A966, info.ToArray());
        }

        using (var tracks = new MemoryStream()) {
            using (var video = new MemoryStream()) {
                Element(video, 0xB0, Unsigned((ulong)width));
                Element(video, 0xBA, Unsigned((ulong)height));
                Element(video, 0x2EB524, "I420"u8.ToArray());

                using var entry = new MemoryStream();

                Element(entry, 0xD7, Unsigned(1));
                Element(entry, 0x83, Unsigned(1));
                Element(entry, 0x86, System.Text.Encoding.UTF8.GetBytes(videoCodec));
                Element(entry, 0x23E383, Unsigned(40_000_000));
                Element(entry, 0xE0, video.ToArray());
                Element(tracks, 0xAE, entry.ToArray());
            }

            if (audioCodec is not null) {
                using var audio = new MemoryStream();

                Element(audio, 0xB5, Float(48_000));
                Element(audio, 0x9F, Unsigned(2));

                using var entry = new MemoryStream();

                Element(entry, 0xD7, Unsigned(2));
                Element(entry, 0x83, Unsigned(2));
                Element(entry, 0x86, System.Text.Encoding.UTF8.GetBytes(audioCodec));
                Element(entry, 0xE1, audio.ToArray());
                Element(tracks, 0xAE, entry.ToArray());
            }

            Element(body, 0x1654AE6B, tracks.ToArray());
        }

        using var file = new MemoryStream();

        using (var header = new MemoryStream()) {
            Element(header, 0x4282, "webm"u8.ToArray());
            Element(file, 0x1A45DFA3, header.ToArray());
        }

        WriteId(file, 0x18538067);
        WriteSize(file, body.Length);
        body.Position = 0;
        body.CopyTo(file);

        return file.ToArray();
    }

    static void Element(Stream stream, uint id, ReadOnlySpan<byte> payload) {
        WriteId(stream, id);
        WriteSize(stream, payload.Length);
        stream.Write(payload);
    }

    static void WriteId(Stream stream, uint id) {
        Span<byte> bytes = stackalloc byte[4];

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, id);

        var start = 0;

        while (start < 3 && bytes[start] == 0) {
            start++;
        }

        stream.Write(bytes[start..]);
    }

    static void WriteSize(Stream stream, long value) {
        var length = 1;

        while (length < 8 && value >= (1L << (7 * length)) - 1) {
            length++;
        }

        Span<byte> bytes = stackalloc byte[8];

        for (var index = 0; index < length; index++) {
            bytes[length - 1 - index] = (byte)(value >> (8 * index));
        }

        bytes[0] |= (byte)(1 << (8 - length));
        stream.Write(bytes[..length]);
    }

    static byte[] Unsigned(ulong value) {
        Span<byte> bytes = stackalloc byte[8];

        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes, value);

        var start = 0;

        while (start < 7 && bytes[start] == 0) {
            start++;
        }

        return bytes[start..].ToArray();
    }

    static byte[] Float(double value) {
        var bytes = new byte[8];

        System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(bytes, value);

        return bytes;
    }
}

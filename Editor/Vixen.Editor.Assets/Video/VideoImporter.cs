// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Video;
using Vixen.Video.Containers;

namespace Vixen.Editor.Assets.Video;

/// <summary>Reads a video's header at build time, so a game does not have to open the file to plan.</summary>
/// <remarks>
///     <para>
///         <b>It does not decode, and it does not transcode.</b> Every other importer turns a source
///         format into a runtime one; this one copies the container and writes down what is in it.
///         The reason is the same one that shapes the whole module: the engine ships no video codec,
///         so there is no runtime format to turn anything into — a shipped video is the file, played
///         by whatever decoder the game registered.
///     </para>
///     <para>
///         <b>What the metadata is for.</b> Everything in <see cref="VideoClip" /> is readable from
///         the container, and reading it means opening the file — which is exactly what a game
///         choosing between three cutscenes, or an editor drawing a list of them, does not want to
///         do. More to the point, <c>CodecId</c> here is what lets a title find out it has no decoder
///         for a clip <em>before</em> the cutscene was supposed to start, which is the difference
///         between a fallback and a black screen.
///     </para>
///     <para>
///         <b>MP4 is claimed and refused, deliberately.</b> <c>AudioImporter</c> makes the same call
///         and states the same reason: doc 08's table promises mp4, and an artist who drops one in
///         and finds it silently became an unplayable byte blob has learned nothing. Claiming it and
///         failing with the name of what is missing is the more useful of the two silences.
///     </para>
/// </remarks>
[Importer(".webm", ".mkv", ".mp4", ".m4v", ".mov")]
public sealed class VideoImporter : AssetImporter<VideoImportSettings> {
    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        VideoImportSettings settings,
        CancellationToken cancellationToken
    ) {
        var extension = Path.GetExtension(context.SourcePath.ToString()).ToLowerInvariant();

        if (extension is ".mp4" or ".m4v" or ".mov") {
            context.Report(
                ImportSeverity.Error,
                "Nothing here reads MP4. Vixen.Video demuxes Matroska and WebM; an MP4 reader is a box "
                + "parser, sample tables and per-codec configuration in stsd, and doc 08's table is what "
                + "is owed. Remuxing to .webm loses nothing when the codec is already VP9 or AV1."
            );

            return context.Finish();
        }

        var bytes = await ReadAsync(context, cancellationToken).ConfigureAwait(false);

        VideoClip clip;

        try {
            clip = Describe(context, bytes);
        } catch (InvalidDataException failure) {
            // A malformed file is the author's problem and belongs against the asset. An I/O failure
            // is the machine's, and is deliberately not caught — it is not a fact about the file.
            context.Report(ImportSeverity.Error, failure.Message);

            return context.Finish();
        }

        context.Write(SubAssetId.Main, "VideoClip", Serializer.ToBytes(clip));

        if (settings.EmbedContainer) {
            // The container itself, under its own sub-asset, so that the clip is a small record an
            // editor can list and the bytes are something a player streams. Writing the two together
            // would make listing a hundred cutscenes read a hundred gigabytes.
            context.Write(context.DeclareSubAsset("Container", "container"), "VideoContainer", bytes);
        }

        return context.Finish();
    }

    static async ValueTask<byte[]> ReadAsync(ImportContext context, CancellationToken cancellationToken) {
        await using var source = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        return buffer.ToArray();
    }

    /// <summary>Opens the container far enough to describe it, and no further.</summary>
    static VideoClip Describe(ImportContext context, byte[] bytes) {
        using var demuxer = new MatroskaDemuxer(new MemoryStream(bytes, writable: false));

        var video = demuxer.FindTrack(MatroskaTrackKind.Video)
            ?? throw new InvalidDataException("The segment has no video track, so it is not a video.");

        var audio = demuxer.FindTrack(MatroskaTrackKind.Audio);
        var rate = RateOf(video);

        var clip = new VideoClip {
            Address = context.Target,
            Width = video.PixelWidth,
            Height = video.PixelHeight,
            Duration = demuxer.Duration,
            FrameRateNumerator = rate.Numerator,
            FrameRateDenominator = rate.Denominator,
            CodecId = video.CodecId,
            HasAudio = audio is not null,
            AudioCodecId = audio?.CodecId ?? string.Empty
        };

        context.Report(
            ImportSeverity.Information,
            $"{clip.Width}×{clip.Height} {clip.CodecId}"
            + (rate.IsKnown ? $" at {rate.Hz:0.##} Hz" : ", frame rate unstated")
            + (clip.Duration > TimeSpan.Zero ? $", {clip.Duration.TotalSeconds:0.##} s" : ", duration unstated")
            + (clip.HasAudio ? $", {clip.AudioCodecId}" : ", no audio")
            + "."
        );

        if (demuxer.Duration <= TimeSpan.Zero) {
            // Legal — a live muxer cannot know — and worth saying, because a game that shows a
            // progress bar has nothing to draw it against.
            context.Report(
                ImportSeverity.Warning,
                "The segment states no duration, so anything that wants to show progress through it "
                + "will have to count frames."
            );
        }

        if (!demuxer.HasCues) {
            context.Report(
                ImportSeverity.Warning,
                "The segment has no cue index, so seeking rewinds and scans forward. Remuxing with one "
                + "costs a few kilobytes and makes a scrub instant."
            );
        }

        return clip;
    }

    static VideoRational RateOf(MatroskaTrack track) =>
        track.DefaultDuration <= TimeSpan.Zero
            ? VideoRational.Unknown
            : new VideoRational(1_000_000_000, (int)Math.Min(int.MaxValue, track.DefaultDuration.Ticks * 100));
}

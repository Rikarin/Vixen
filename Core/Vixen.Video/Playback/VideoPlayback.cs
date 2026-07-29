// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Sources;
using Vixen.Video.Audio;
using Vixen.Video.Codecs;
using Vixen.Video.Containers;

namespace Vixen.Video.Playback;

/// <summary>How to open a clip.</summary>
/// <remarks>
///     A class rather than a record struct, deliberately. <c>VideoPlayerOptions</c> is a record struct
///     whose <c>default</c> skips its field initialisers and hands out a queue capacity of zero — a
///     trap that cost a test to find — and repeating the shape here would repeat the trap on a type
///     whose whole job is to be passed as <see langword="null" />.
/// </remarks>
public sealed record VideoPlaybackOptions {
    /// <summary>Whether the video restarts when it ends.</summary>
    public bool Loop { get; init; }

    /// <summary>Whether to start playing as soon as it is open.</summary>
    public bool AutoPlay { get; init; }

    /// <summary>Whether to open the audio track at all.</summary>
    /// <remarks>
    ///     Off is the right answer for a video drawn behind a menu, and it is not merely a saving: an
    ///     audio track nobody drains makes the demuxer hold every audio packet in the file, so
    ///     <em>opening</em> a track and then ignoring it is worse than never opening it.
    /// </remarks>
    public bool OpenAudio { get; init; } = true;

    /// <summary>How the picture's own decoding is configured.</summary>
    public VideoPlayerOptions Player { get; init; } = new();

    /// <summary>Whether the sound gets a reader of its own rather than sharing the picture's.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Defaulted from <see cref="Loop" />, because looping is what makes sharing wrong.</b>
    ///         The picture and the sound are in one file and one demuxer serves both — until something
    ///         seeks it. A loop is a seek, so a looping video sharing its reader with its own audio
    ///         provider yanks the file back to the start under whichever of the two did not ask, over
    ///         and over, and what comes out is neither stream.
    ///     </para>
    ///     <para>
    ///         Set explicitly to override: a cutscene played once, straight through, wants one reader
    ///         and one file position, and a scrubbable video wants two whether or not it loops.
    ///     </para>
    /// </remarks>
    public bool? SeparateAudioReader { get; init; }

    /// <summary>What <see cref="SeparateAudioReader" /> works out to.</summary>
    public bool AudioNeedsOwnReader => SeparateAudioReader ?? Loop;
}

/// <summary>A clip, opened: a player, its sound, and everything holding the file.</summary>
/// <remarks>
///     <para>
///         <b>The missing half of <see cref="VideoClip" />.</b> The importer writes down what is in a
///         video and where its bytes are; this is what turns that record into something that plays.
///         Without it a game has an address it cannot do anything with and has to know the file path
///         anyway, which is the whole of what an address exists to avoid.
///     </para>
///     <para>
///         <b>It owns what it opened and nothing else.</b> Disposing this closes the demuxers and the
///         streams under them and disposes the player; the <see cref="Clip" /> and the content source
///         belong to the caller. The audio provider is <em>not</em> registered with a mixer here —
///         that is the caller's, because a mixer is a thing a game has one of and this is not the
///         place to guess which.
///     </para>
/// </remarks>
public sealed class VideoPlayback : IDisposable {
    readonly List<IDisposable> owned = [];
    bool disposed;

    VideoPlayback(VideoClip clip, VideoPlayer player) {
        Clip = clip;
        Player = player;
    }

    /// <summary>What was opened.</summary>
    public VideoClip Clip { get; }

    /// <summary>The picture.</summary>
    public VideoPlayer Player { get; }

    /// <summary>The sound, or null if there is none or nothing could decode it.</summary>
    /// <remarks>
    ///     Null rather than an exception, because a video whose audio codec is not registered is a
    ///     video that should still play. <see cref="AudioUnavailableReason" /> is why.
    /// </remarks>
    public MatroskaAudioStreamDecoder? Audio { get; private set; }

    /// <summary>Why there is no <see cref="Audio" />, or empty if there is.</summary>
    public string AudioUnavailableReason { get; private set; } = string.Empty;

    /// <summary>Whether a codec is registered for this clip's picture.</summary>
    /// <param name="clip">The clip.</param>
    /// <returns>Whether it can be played.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clip" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The question <c>VideoClip.CodecId</c> exists to answer, and it is answered without
    ///     opening the file.</b> A title with no VP9 decoder wants to find that out while it is
    ///     drawing a menu, not when the cutscene was supposed to start — the difference between a
    ///     fallback and a black screen.
    /// </remarks>
    public static bool CanPlay(VideoClip clip) {
        ArgumentNullException.ThrowIfNull(clip);

        return VideoCodecRegistry.CanDecode(TrackOf(clip));
    }

    /// <summary>Opens a clip.</summary>
    /// <param name="clip">What to play.</param>
    /// <param name="content">Where its bytes come from.</param>
    /// <param name="options">How to open it, or null for the defaults.</param>
    /// <returns>The playback, which the caller disposes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clip" /> or <paramref name="content" /> is null.</exception>
    /// <exception cref="VideoContentMissingException">The bytes are not there.</exception>
    /// <exception cref="NotSupportedException">Nothing is registered that can decode the picture.</exception>
    public static VideoPlayback Open(
        VideoClip clip,
        IVideoContentSource content,
        VideoPlaybackOptions? options = null
    ) {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(content);

        options ??= new VideoPlaybackOptions();

        var address = clip.ContainerAddress.Length > 0 ? clip.ContainerAddress : clip.Address;

        if (address.Length == 0) {
            throw new VideoContentMissingException(
                "",
                "the clip names neither a container nor an address, so there is nothing to open. A clip "
                + "written by VideoImporter carries both."
            );
        }

        // ⚠ Before the file is opened, so a missing decoder is one exception naming the codec rather
        // than a demuxer that succeeds and a player that produces nothing.
        if (!VideoCodecRegistry.CanDecode(TrackOf(clip))) {
            throw new NotSupportedException(
                $"'{clip.Address}' is {clip.CodecId} and nothing registered in this process decodes it. "
                + $"The registry has {Registered()}. Vixen ships no compressed video codec — see "
                + "Core/Vixen.Video/README.md — so a game that needs one registers it before playing."
            );
        }

        var playback = new VideoPlayback(clip, OpenPicture(content, address, options));

        try {
            playback.owned.Add(playback.Player);
            playback.OpenSound(clip, content, address, options);

            if (options.AutoPlay) {
                playback.Player.Play();
            }

            return playback;
        } catch {
            // Everything opened so far goes back, or a failure halfway leaves a file handle and a
            // decode thread behind with nobody holding either.
            playback.Dispose();
            throw;
        }
    }

    /// <summary>Makes the picture follow the sound, if there is any.</summary>
    /// <param name="provider">The provider the mixer is actually pulling from.</param>
    /// <param name="offset">Added to the sound's position, for a track that is deliberately early.</param>
    /// <returns>Whether it did.</returns>
    /// <remarks>
    ///     ⚠ <b>The provider, not <see cref="Audio" />.</b> A streaming decoder is filled ahead of
    ///     playback by design, so its position is where the sound <em>will</em> be — half a second
    ///     out. Handing the decoder here would look correct in every line and be visibly wrong on
    ///     screen, which is why this takes what the mixer reads rather than what it reads from.
    /// </remarks>
    public bool FollowAudio(IAudioSampleProvider provider, TimeSpan offset = default) {
        ArgumentNullException.ThrowIfNull(provider);

        if (Audio is null) {
            return false;
        }

        Player.FollowAudio(provider, offset);

        return true;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        // In reverse: the player owns a thread that reads the demuxer, so it has to stop before the
        // demuxer under it closes, and the demuxer before the stream under it does.
        for (var index = owned.Count - 1; index >= 0; index--) {
            owned[index].Dispose();
        }

        owned.Clear();
    }

    /// <summary>What a clip says about its picture, in the shape a codec factory is asked in.</summary>
    /// <remarks>
    ///     The codec private data is deliberately absent: it is in the file and this is the path that
    ///     has not opened the file. No factory that ships here reads it to answer
    ///     <c>CanDecode</c>, and one that did would be answering a question it cannot be asked this
    ///     early.
    /// </remarks>
    static VideoTrackInfo TrackOf(VideoClip clip) =>
        new(clip.CodecId, clip.Width, clip.Height, default, clip.FourCc, clip.FrameRate);

    static string Registered() {
        var names = VideoCodecRegistry.RegisteredNames();

        return names.Count == 0 ? "nothing in it" : string.Join(", ", names);
    }

    static VideoPlayer OpenPicture(
        IVideoContentSource content,
        string address,
        VideoPlaybackOptions options
    ) {
        var demuxer = new MatroskaDemuxer(content.Open(address));

        try {
            var decoder = new WebMVideoStreamDecoder(demuxer, ownsDemuxer: true);

            return new VideoPlayer(decoder, options.Player with { Loop = options.Loop });
        } catch {
            demuxer.Dispose();
            throw;
        }
    }

    void OpenSound(
        VideoClip clip,
        IVideoContentSource content,
        string address,
        VideoPlaybackOptions options
    ) {
        if (!options.OpenAudio) {
            AudioUnavailableReason = "the caller asked for the picture only.";
            return;
        }

        if (!clip.HasAudio) {
            AudioUnavailableReason = "the file has no audio track.";
            return;
        }

        var container = ((WebMVideoStreamDecoder) Player.Decoder).Container;

        if (options.AudioNeedsOwnReader) {
            // A second reader over the same bytes: one more file position and a few hundred kilobytes
            // of buffering, against a shared one that both sides seek and neither gets right.
            var own = new MatroskaDemuxer(content.Open(address));
            owned.Add(own);
            container = own;
        }

        if (!MatroskaAudioStreamDecoder.TryOpen(container, out var audio)) {
            AudioUnavailableReason =
                $"the track is {clip.AudioCodecId} and nothing registered in this process decodes it. "
                + "Referencing Vixen.Video.Codecs and calling VideoAudioCodecs.RegisterOpus() adds the "
                + "codec WebM actually ships with.";

            return;
        }

        Audio = audio;
        owned.Add(audio!);
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Video.Playback;
using Xunit;

namespace Vixen.Video.Tests;

/// <summary>Turning a clip the content build wrote into something that plays.</summary>
/// <remarks>
///     The half that was missing: the importer wrote a record naming a video and nothing turned one
///     into a player, so a game holding a clip still had to know a file path — which is the whole of
///     what an address exists to avoid.
/// </remarks>
public sealed class VideoPlaybackTests {
    [Fact]
    public void AClipOpensThroughItsContainerAddress() {
        var content = Content(("cutscenes/intro#container", VideoTestContent.Video(64, 32, 3).Build()));

        using var playback = VideoPlayback.Open(Clip(container: "cutscenes/intro#container"), content);

        Assert.Equal(64, playback.Player.Decoder.Format.Width);
        Assert.Equal(VideoPlaybackState.Stopped, playback.Player.State);
    }

    [Fact]
    public void AClipWithNoContainerFallsBackToItsOwnAddress() {
        // What a video left loose beside the executable looks like: the importer embedded nothing, so
        // the clip's own address is the address of the file.
        var content = Content(("cutscenes/intro", VideoTestContent.Video(64, 32, 3).Build()));

        using var playback = VideoPlayback.Open(Clip(), content);

        Assert.Equal(64, playback.Player.Decoder.Format.Width);
    }

    [Fact]
    public void AMissingDecoderIsRefusedBeforeTheFileIsOpened() {
        var opened = 0;

        var content = new DelegatedVideoContentSource(_ => {
            opened++;
            return new MemoryStream();
        });

        var failure = Assert.Throws<NotSupportedException>(
            () => VideoPlayback.Open(Clip(codec: "V_VP9"), content)
        );

        // ⚠ The point of the check, and the reason it is where it is. The stream is never opened, the
        // message names the codec, and a title can ask the same question with CanPlay while it is
        // drawing a menu rather than when the cutscene was due.
        Assert.Equal(0, opened);
        Assert.Contains("V_VP9", failure.Message, StringComparison.Ordinal);
        Assert.False(VideoPlayback.CanPlay(Clip(codec: "V_VP9")));
        Assert.True(VideoPlayback.CanPlay(Clip()));
    }

    [Fact]
    public void ALoopingClipGetsAReaderOfItsOwnForTheSound() {
        // Both sides seek when either loops, and one reader with two things seeking it yanks the file
        // back to the start under whichever did not ask. The default follows Loop for that reason.
        var opens = 0;

        var bytes = VideoTestContent.Video(32, 16, 2)
            .AudioTrack(2, 48_000, 1, codecId: "A_PCM/FLOAT/IEEE")
            .Build();

        var content = new DelegatedVideoContentSource(_ => {
            opens++;
            return new MemoryStream(bytes, writable: false);
        });

        using (VideoPlayback.Open(Clip(audio: "A_PCM/FLOAT/IEEE"), content, new VideoPlaybackOptions { Loop = true })) {
            Assert.Equal(2, opens);
        }

        opens = 0;

        using (VideoPlayback.Open(Clip(audio: "A_PCM/FLOAT/IEEE"), content, new VideoPlaybackOptions())) {
            // Played once, straight through: one reader, one file position, and a seek in the picture
            // that the sound is meant to follow.
            Assert.Equal(1, opens);
        }
    }

    [Fact]
    public void AnUndecodableSoundtrackLeavesThePictureAloneAndSaysWhy() {
        var bytes = VideoTestContent.Video(32, 16, 2)
            .AudioTrack(2, 48_000, 2, codecId: "A_OPUS")
            .Build();

        var content = new DelegatedVideoContentSource(_ => new MemoryStream(bytes, writable: false));

        using var playback = VideoPlayback.Open(Clip(audio: "A_OPUS"), content);

        // Null rather than an exception: a video whose audio codec is not registered is a video that
        // should still play. Vixen.Video.Codecs is what makes this one work.
        Assert.Null(playback.Audio);
        Assert.Contains("A_OPUS", playback.AudioUnavailableReason, StringComparison.Ordinal);
        Assert.False(playback.FollowAudio(new SilentProvider()));
    }

    [Fact]
    public void DisposingClosesTheStreamsItOpened() {
        var streams = new List<TrackedStream>();

        var bytes = VideoTestContent.Video(32, 16, 2).Build();

        var content = new DelegatedVideoContentSource(_ => {
            var stream = new TrackedStream(bytes);
            streams.Add(stream);

            return stream;
        });

        var playback = VideoPlayback.Open(Clip(), content, new VideoPlaybackOptions { Loop = true });

        Assert.All(streams, stream => Assert.False(stream.Closed));

        playback.Dispose();

        // ⚠ In reverse: the player owns a thread reading the demuxer, so it stops before the demuxer
        // closes and the demuxer before the stream under it does. Getting that backwards is a read of
        // a disposed stream on a background thread, which is a crash nobody can attribute.
        Assert.All(streams, stream => Assert.True(stream.Closed));
    }

    [Fact]
    public void AForwardOnlyStreamIsRefusedWithTheReason() {
        var content = new DelegatedVideoContentSource(_ => new ForwardOnlyStream());

        var failure = Assert.Throws<VideoContentMissingException>(() => VideoPlayback.Open(Clip(), content));

        Assert.Contains("seek", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFileSourceRefusesAnAddressThatEscapesItsRoot() {
        var source = new FileVideoContentSource(Path.GetTempPath());

        Assert.Throws<VideoContentMissingException>(() => source.PathOf("../../etc/passwd"));
        Assert.False(source.Exists("../../etc/passwd"));
    }

    static VideoClip Clip(
        string container = "",
        string codec = "V_UNCOMPRESSED",
        string audio = ""
    ) =>
        new() {
            Address = "cutscenes/intro",
            ContainerAddress = container,
            Width = 64,
            Height = 32,
            CodecId = codec,
            FourCc = codec == "V_UNCOMPRESSED" ? "I420" : "",
            HasAudio = audio.Length > 0,
            AudioCodecId = audio
        };

    static DelegatedVideoContentSource Content(params (string Address, byte[] Bytes)[] entries) {
        var map = entries.ToDictionary(entry => entry.Address, entry => entry.Bytes, StringComparer.Ordinal);

        return new DelegatedVideoContentSource(
            address => new MemoryStream(map[address], writable: false),
            map.ContainsKey
        );
    }

    /// <summary>A stream that remembers being closed, which is the only thing under test.</summary>
    sealed class TrackedStream(byte[] bytes) : MemoryStream(bytes, writable: false) {
        public bool Closed { get; private set; }

        protected override void Dispose(bool disposing) {
            Closed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>What a network body looks like: readable, and going one way only.</summary>
    sealed class ForwardOnlyStream : MemoryStream {
        public override bool CanSeek => false;
    }

    /// <summary>A provider that delivers nothing, for the case where there is nothing to follow.</summary>
    sealed class SilentProvider : Vixen.Audio.Sources.IAudioSampleProvider {
        public Vixen.Audio.AudioFormat Format => new(48_000, 2);

        public long Position => 0;

        public long FrameCount => 0;

        public bool IsLooping => false;

        public int Read(Span<float> destination, int frameCount) => 0;

        public void Seek(long frame) { }
    }
}

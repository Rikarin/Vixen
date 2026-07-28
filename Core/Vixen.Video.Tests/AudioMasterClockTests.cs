// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio;
using Vixen.Audio.Sources;
using Vixen.Video.Playback;
using Xunit;

namespace Vixen.Video.Tests;

/// <summary>The picture following the sound, which is the whole of A/V sync.</summary>
public sealed class AudioMasterClockTests {
    [Fact]
    public void TheClockReportsWhereTheSoundHasGotTo() {
        var audio = new FakeProvider(48_000);
        using var player = Player();

        player.FollowAudio(audio);
        player.Play();

        audio.Position = 24_000;
        player.Update(TimeSpan.FromMilliseconds(16));

        Assert.Equal(TimeSpan.FromMilliseconds(500), player.Position);
    }

    [Fact]
    public void TheFrameDeltaIsIgnoredWhileTheSoundIsInCharge() {
        // The point of a master clock: a frame that took twice as long does not move the picture
        // twice as far, because the sound did not move twice as far.
        var audio = new FakeProvider(48_000);
        using var player = Player();

        player.FollowAudio(audio);
        player.Play();
        player.Update(TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.Zero, player.Position);
    }

    [Fact]
    public void AnOffsetShiftsThePictureWithoutMovingTheSound() {
        var audio = new FakeProvider(48_000) { Position = 48_000 };
        using var player = Player();

        player.FollowAudio(audio, TimeSpan.FromMilliseconds(-40));
        player.Play();
        player.Update(TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromMilliseconds(960), player.Position);
    }

    [Fact]
    public void HandingTheClockBackResumesFromWhereTheSoundReached() {
        // Muting mid-play must not jump the picture, which is what resuming from the clock's own
        // integrated time would do.
        var audio = new FakeProvider(48_000) { Position = 96_000 };
        using var player = Player();

        player.FollowAudio(audio);
        player.Play();
        player.Update(TimeSpan.Zero);

        player.Clock.Master = null;
        player.Update(TimeSpan.FromMilliseconds(100));

        Assert.Equal(TimeSpan.FromMilliseconds(2_100), player.Position);
    }

    [Fact]
    public void ASourceWithNoRateCannotBeAClock() {
        using var player = Player();

        Assert.Throws<ArgumentException>(() => player.FollowAudio(new FakeProvider(0)));
    }

    static VideoPlayer Player() =>
        new(
            new WebMVideoStreamDecoder(VideoTestContent.Video(16, 16, 200).Stream()),
            new VideoPlayerOptions { UseDecodeThread = false, QueueCapacity = 2 }
        );

    /// <summary>A provider whose position is whatever the test says it is.</summary>
    /// <remarks>
    ///     The distinction this pins down is the one that matters: a real
    ///     <c>StreamingSampleProvider</c> reports frames <em>delivered to the mixer</em>, not frames
    ///     decoded, and a player slaved to the latter runs half a second ahead of the sound with
    ///     every part of it looking correct.
    /// </remarks>
    sealed class FakeProvider(int rate) : IAudioSampleProvider {
        public AudioFormat Format { get; } = new(rate, 1);

        public long FrameCount => -1;

        public long Position { get; set; }

        public bool IsLooping => false;

        public int Read(Span<float> destination, int frameCount) => 0;

        public void Seek(long frame) => Position = frame;
    }
}

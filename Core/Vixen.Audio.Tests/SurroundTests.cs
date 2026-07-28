// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Spatial;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Audio.Tests;

/// <summary>Placing a sound on a ring of speakers rather than in the first two of them.</summary>
public sealed class SurroundTests {
    const int FrontLeft = 0;
    const int FrontRight = 1;
    const int Centre = 2;
    const int LowFrequency = 3;
    const int SideLeft = 4;
    const int SideRight = 5;
    const int BackLeft = 6;
    const int BackRight = 7;

    /// <summary>Places a sound at an angle round the listener and returns the speaker gains.</summary>
    /// <remarks>
    ///     Straight ahead is −Z, which is what <c>Conventions.md</c> says forward is, so an angle of
    ///     zero puts the source in front and ninety puts it to the right.
    /// </remarks>
    static float[] At(float degrees, int channels, float spread = 0f) {
        var radians = degrees * MathF.PI / 180f;

        var source = new SpatialSettings {
            Position = new Vector3(MathF.Sin(radians) * 10f, 0f, -MathF.Cos(radians) * 10f),
            MinDistance = 1f,
            MaxDistance = 1_000f,
            Attenuation = AttenuationModel.None,
            DopplerFactor = 0f,
            Spread = spread
        };

        var gains = new float[channels];
        Spatializer.Evaluate(AudioListener.Default, source, channels, gains);
        return gains;
    }

    static float Power(float[] gains) {
        var total = 0f;

        foreach (var gain in gains) {
            total += gain * gain;
        }

        return total;
    }

    [Fact]
    public void TheLayoutsAreTheOnesEveryConsumerApiAgreesOn() {
        Assert.True(SpeakerLayout.IsKnown(6));
        Assert.True(SpeakerLayout.IsKnown(8));
        Assert.False(SpeakerLayout.IsKnown(5));
        Assert.False(SpeakerLayout.IsKnown(7));

        var surround = SpeakerLayout.Angles(6);

        Assert.Equal(-30f, surround[FrontLeft]);
        Assert.Equal(30f, surround[FrontRight]);
        Assert.Equal(0f, surround[Centre]);
        Assert.Equal(-110f, surround[SideLeft]);
        Assert.Equal(110f, surround[SideRight]);

        Assert.Equal(LowFrequency, SpeakerLayout.LowFrequencyChannel(6));
        Assert.Equal(LowFrequency, SpeakerLayout.LowFrequencyChannel(8));
        Assert.Equal(-1, SpeakerLayout.LowFrequencyChannel(4));
    }

    /// <summary>The bug this exists to fix: everything behind the listener used to be silent.</summary>
    [Fact]
    public void ASoundBehindTheListenerReachesTheSurrounds() {
        var gains = At(180f, 6);

        Assert.Equal(gains[SideLeft], gains[SideRight], 1e-4f);
        Assert.True(gains[SideLeft] > 0.5f, $"the surrounds got {gains[SideLeft]:F3}");
        Assert.Equal(0f, gains[FrontLeft], 1e-5f);
        Assert.Equal(0f, gains[FrontRight], 1e-5f);
    }

    [Fact]
    public void ASoundDeadAheadGoesToTheCentreSpeaker() {
        var gains = At(0f, 6);

        Assert.Equal(1f, gains[Centre], 1e-4f);
        Assert.Equal(0f, gains[FrontLeft], 1e-5f);
        Assert.Equal(0f, gains[FrontRight], 1e-5f);
    }

    /// <summary>
    ///     Spreading across every speaker instead is what makes a point source sound like a wash: the
    ///     ear locates a sound by the difference between what two speakers are doing.
    /// </summary>
    [Fact]
    public void OnlyThePairTheSoundLiesBetweenGetsAnything() {
        var gains = At(15f, 6);

        Assert.True(gains[Centre] > 0f);
        Assert.True(gains[FrontRight] > 0f);
        Assert.Equal(0f, gains[FrontLeft], 1e-5f);
        Assert.Equal(0f, gains[SideLeft], 1e-5f);
        Assert.Equal(0f, gains[SideRight], 1e-5f);

        // Half way between the centre at 0 and the right at 30, so equal in both.
        Assert.Equal(gains[Centre], gains[FrontRight], 1e-4f);
    }

    /// <summary>The ".1" is a band, not a place. A source panned into it would go entirely to a subwoofer.</summary>
    [Fact]
    public void NothingIsEverPannedIntoTheLowFrequencyChannel() {
        foreach (var degrees in new[] { 0f, 45f, 90f, 135f, 180f, -90f }) {
            Assert.Equal(0f, At(degrees, 6)[LowFrequency], 1e-6f);
            Assert.Equal(0f, At(degrees, 8)[LowFrequency], 1e-6f);
        }
    }

    /// <summary>The same reason the stereo law uses a quarter-circle: crossing a speaker must not dip.</summary>
    [Fact]
    public void ThePowerIsTheSameWhicheverWayTheSoundIsFacing() {
        for (var degrees = -180f; degrees < 180f; degrees += 7f) {
            Assert.Equal(1f, Power(At(degrees, 6)), 1e-3f);
            Assert.Equal(1f, Power(At(degrees, 8)), 1e-3f);
            Assert.Equal(1f, Power(At(degrees, 4)), 1e-3f);
        }
    }

    [Fact]
    public void SevenPointOneUsesItsBackSpeakers() {
        // Directly behind, which in 7.1 is between the two backs at ±150 rather than the sides.
        var gains = At(180f, 8);

        Assert.True(gains[BackLeft] > 0.5f);
        Assert.Equal(gains[BackLeft], gains[BackRight], 1e-4f);
        Assert.Equal(0f, gains[SideLeft], 1e-5f);
    }

    [Fact]
    public void QuadHasNoCentreAndUsesItsFourCorners() {
        var ahead = At(0f, 4);

        // Nothing at 0, so a source dead ahead is shared by the two fronts at ±45.
        Assert.Equal(ahead[0], ahead[1], 1e-4f);
        Assert.True(ahead[0] > 0.5f);
        Assert.Equal(0f, ahead[2], 1e-5f);
    }

    /// <summary>What a source walking into its reference distance does, and what Spread asks for.</summary>
    [Fact]
    public void FullSpreadIsEqualPowerAcrossEveryPlacedSpeaker() {
        var gains = At(90f, 6, spread: 1f);

        // Five placed speakers in 5.1, so each is 1/√5 of full scale — and the LFE is still none.
        var expected = 1f / MathF.Sqrt(5f);

        Assert.Equal(expected, gains[FrontLeft], 1e-4f);
        Assert.Equal(expected, gains[Centre], 1e-4f);
        Assert.Equal(expected, gains[SideRight], 1e-4f);
        Assert.Equal(0f, gains[LowFrequency], 1e-6f);
        Assert.Equal(1f, Power(gains), 1e-3f);
    }

    [Fact]
    public void PartialSpreadIsSomewhereBetweenAPointAndEverywhere() {
        var point = At(90f, 6);
        var half = At(90f, 6, spread: 0.5f);
        var everywhere = At(90f, 6, spread: 1f);

        // The speaker it was in gives some up, and one it was not in picks some up.
        Assert.True(half[SideRight] < point[SideRight]);
        Assert.True(half[SideRight] > everywhere[SideRight]);
        Assert.True(half[FrontLeft] > point[FrontLeft]);
        Assert.True(half[FrontLeft] < everywhere[FrontLeft]);
    }

    /// <summary>
    ///     Two speakers are a pair to be balanced across, and a ring is a set of directions. Run
    ///     through the ring law, a source at 90° would land in the 300° gap behind a stereo pair and
    ///     come out barely right of centre.
    /// </summary>
    [Fact]
    public void StereoKeepsItsOwnLawRatherThanBecomingATwoSpeakerRing() {
        var gains = At(90f, 2);

        Assert.Equal(1f, gains[FrontRight], 1e-4f);
        Assert.Equal(0f, gains[FrontLeft], 1e-4f);
    }

    /// <summary>
    ///     Inventing an arrangement for an unknown count is how a sound ends up in a speaker that is
    ///     not where the layout thought it was.
    /// </summary>
    [Fact]
    public void AnUnknownChannelCountFallsBackToTheFirstTwo() {
        var gains = At(90f, 5);

        Assert.Equal(1f, gains[FrontRight], 1e-4f);
        Assert.Equal(0f, gains[FrontLeft], 1e-4f);
        Assert.Equal(0f, gains[2], 1e-6f);
    }

    [Fact]
    public void MonoIsStillJustTheOneSpeaker() {
        var gains = At(135f, 1);
        Assert.Equal(1f, gains[0], 1e-4f);
    }
}

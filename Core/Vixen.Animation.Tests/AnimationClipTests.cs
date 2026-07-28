// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Animation.Tests;

public class AnimationClipTests {
    readonly Skeleton skeleton = TestRigs.Chain();

    [Fact]
    public void Sample_BetweenKeys_InterpolatesLinearly() {
        var clip = AnimationClip.Create(
            TestRigs.Translate("Slide", "Mid", Vector3.Zero, new(0f, 0f, 4f)),
            skeleton
        );

        var pose = new BoneTransform[skeleton.JointCount];
        clip.Sample(0.25f, pose);

        TestRigs.Near(new(0f, 0f, 1f), pose[1].Translation);
    }

    [Fact]
    public void Sample_OutsideTheClip_ClampsToTheEndKeys() {
        var clip = AnimationClip.Create(
            TestRigs.Translate("Slide", "Mid", Vector3.Zero, new(0f, 0f, 4f)),
            skeleton
        );

        var pose = new BoneTransform[skeleton.JointCount];

        clip.Sample(-5f, pose);
        TestRigs.Near(Vector3.Zero, pose[1].Translation);

        clip.Sample(50f, pose);
        TestRigs.Near(new(0f, 0f, 4f), pose[1].Translation);
    }

    [Fact]
    public void Sample_JointWithNoChannel_GetsTheBindPose() {
        var clip = AnimationClip.Create(
            TestRigs.Translate("Slide", "Mid", Vector3.Zero, new(0f, 0f, 4f)),
            skeleton
        );

        var pose = new BoneTransform[skeleton.JointCount];
        clip.Sample(0.5f, pose);

        TestRigs.Near(skeleton.BindPose[2].Translation, pose[2].Translation);
    }

    [Fact]
    public void Create_ChannelForAMissingJoint_IsDroppedAndCounted() {
        var data = new AnimationClipData {
            Name = "Fingers",
            Duration = 1f,
            Channels = [
                new() { Target = "Mid", PositionTimes = [0f], Positions = [Vector3.Zero] },
                new() { Target = "IndexFinger", PositionTimes = [0f], Positions = [Vector3.Zero] }
            ]
        };

        var clip = AnimationClip.Create(data, skeleton);

        Assert.Equal(1, clip.TrackCount);
        Assert.Equal(1, clip.UnresolvedChannels);
    }

    [Fact]
    public void Create_ZeroDuration_FallsBackToTheLastKeyTime() {
        var data = TestRigs.Translate("Slide", "Mid", Vector3.Zero, Vector3.UnitZ, 2.5f);
        data.Duration = 0f;

        var clip = AnimationClip.Create(data, skeleton);

        Assert.Equal(2.5f, clip.Duration, TestRigs.Tolerance);
    }

    [Fact]
    public void Sample_ManyKeys_FindsTheRightSpanAtEveryPoint() {
        var times = new float[16];
        var values = new Vector3[16];

        for (var index = 0; index < times.Length; index++) {
            times[index] = index;
            values[index] = new(index, 0f, 0f);
        }

        var clip = AnimationClip.Create(
            new AnimationClipData {
                Name = "Ramp",
                Duration = 15f,
                Channels = [new() { Target = "Mid", PositionTimes = times, Positions = values }]
            },
            skeleton
        );

        var pose = new BoneTransform[skeleton.JointCount];

        // The value equals the time everywhere, so one assertion covers the search at every span.
        for (var step = 0; step <= 60; step++) {
            var time = step * 0.25f;
            clip.Sample(time, pose);

            Assert.Equal(time, pose[1].Translation.X, TestRigs.Tolerance);
        }
    }

    [Theory]
    [InlineData(WrapMode.Loop, 0.25f, 1)]
    [InlineData(WrapMode.Clamp, 1f, 0)]
    [InlineData(WrapMode.PingPong, 0.75f, 0)]
    public void Advance_PastTheEnd_WrapsAccordingToTheMode(WrapMode mode, float expected, int expectedLoops) {
        var time = AnimationClip.Advance(0.75f, 0.5f, mode, 1f, out var loops);

        Assert.Equal(expected, time, TestRigs.Tolerance);
        Assert.Equal(expectedLoops, loops);
    }

    [Fact]
    public void Advance_SeveralWholeLoopsInOneStep_CountsThemAll() {
        var time = AnimationClip.Advance(0f, 3.5f, WrapMode.Loop, 1f, out var loops);

        Assert.Equal(0.5f, time, TestRigs.Tolerance);
        Assert.Equal(3, loops);
    }

    [Fact]
    public void CollectEvents_WithinAStep_FiresOnceAndOnlyInTheHalfOpenRange() {
        var clip = AnimationClip.Create(
            TestRigs.Hold("Walk", "Mid", Vector3.Zero),
            skeleton,
            [new("Step", 0.5f), new("Step", 0.9f)]
        );

        var buffer = new AnimationEventBuffer();
        clip.CollectEvents(0.4f, 0.5f, 0, buffer, 0, "Walk", 1f);
        Assert.Equal(1, buffer.Count);

        // The same instant again: it fired last time, so it does not fire now.
        buffer.Clear();
        clip.CollectEvents(0.5f, 0.6f, 0, buffer, 0, "Walk", 1f);
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void CollectEvents_AcrossALoop_FiresTheTailThenTheHead() {
        var clip = AnimationClip.Create(
            TestRigs.Hold("Walk", "Mid", Vector3.Zero),
            skeleton,
            [new("Left", 0.2f), new("Right", 0.8f)]
        );

        var buffer = new AnimationEventBuffer();
        clip.CollectEvents(0.7f, 0.3f, 1, buffer, 0, "Walk", 1f);

        Assert.Equal(2, buffer.Count);
        Assert.Equal("Right", buffer[0].Event.Name);
        Assert.Equal("Left", buffer[1].Event.Name);
    }

    [Fact]
    public void CollectEvents_ManyLoopsInOneStep_FiresEveryPass() {
        var clip = AnimationClip.Create(
            TestRigs.Hold("Walk", "Mid", Vector3.Zero),
            skeleton,
            [new("Step", 0.5f)]
        );

        var buffer = new AnimationEventBuffer();
        clip.CollectEvents(0.1f, 0.9f, 3, buffer, 0, "Walk", 1f);

        // 0.1 → 3.9 is three and four fifths of a pass, and it crosses the half-way key at 0.5,
        // 1.5, 2.5 and 3.5. A frame long enough to contain four strides contains four footsteps.
        Assert.Equal(4, buffer.Count);
    }

    [Fact]
    public void CollectEvents_RecordsTheWeightSoAHandlerCanFilterACrossfade() {
        var clip = AnimationClip.Create(
            TestRigs.Hold("Walk", "Mid", Vector3.Zero),
            skeleton,
            [new("Step", 0.5f, Float: 0.75f, Int: 2, String: "left")]
        );

        var buffer = new AnimationEventBuffer();
        clip.CollectEvents(0.4f, 0.6f, 0, buffer, 3, "Walk", 0.05f);

        var fired = buffer[0];

        Assert.Equal(0.05f, fired.Weight);
        Assert.Equal(3, fired.Layer);
        Assert.Equal("Walk", fired.State);
        Assert.Equal(0.75f, fired.Event.Float);
        Assert.Equal(2, fired.Event.Int);
        Assert.Equal("left", fired.Event.String);
    }
}

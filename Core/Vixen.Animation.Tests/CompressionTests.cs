// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Animation.Tests;

public class CompressionTests {
    readonly Skeleton skeleton = TestRigs.Chain();

    static readonly Gen<Quaternion> AnyRotation =
        Gen.Select(
            Gen.Float[-1f, 1f],
            Gen.Float[-1f, 1f],
            Gen.Float[-1f, 1f],
            Gen.Float[-1f, 1f],
            (x, y, z, w) => new Quaternion(x, y, z, w)
        ).Where(q => q.LengthSquared() > 1e-3f).Select(Quaternion.Normalize);

    static AnimationClipData Ramp(string joint, int keys, Func<int, Vector3> position) {
        var times = new float[keys];
        var values = new Vector3[keys];

        for (var index = 0; index < keys; index++) {
            times[index] = index / (float)(keys - 1);
            values[index] = position(index);
        }

        return new() {
            Name = "Ramp",
            Duration = 1f,
            Channels = [new() { Target = joint, PositionTimes = times, Positions = values }]
        };
    }

    /// <summary>
    ///     The angle between two rotations, through the relative rotation rather than through
    ///     <c>acos</c> of the dot product — which loses every digit that matters when the two are
    ///     nearly the same, which here they always are.
    /// </summary>
    static float AngleBetween(Quaternion left, Quaternion right) {
        var relative = Quaternion.Concatenate(Quaternion.Conjugate(left), right);
        return 2f * MathF.Atan2(relative.Xyz.Length(), MathF.Abs(relative.W));
    }

    [Fact]
    public void PackedQuaternion_RoundTrips_ToWellUnderAThousandthOfADegree() {
        AnyRotation.Sample(
            rotation => {
                // q and −q are the same rotation, and packing may choose the other sign; the
                // relative-rotation measure is blind to that as well as being stable near zero.
                var angle = AngleBetween(rotation, PackedQuaternion.Pack(rotation).Unpack());

                Assert.True(angle < 1e-5f, $"{rotation} round-tripped {angle} rad away");
            },
            iter: 10_000
        );
    }

    [Fact]
    public void PackedQuaternion_IsEightBytes() =>
        Assert.Equal(8, System.Runtime.CompilerServices.Unsafe.SizeOf<PackedQuaternion>());

    [Fact]
    public void PackedQuaternion_Identity_RoundTripsExactly() =>
        TestRigs.Near(Quaternion.Identity, PackedQuaternion.Identity.Unpack());

    [Fact]
    public void Compress_AConstantTrack_CollapsesToOneKey() {
        var data = Ramp("Mid", 120, static _ => new Vector3(1f, 2f, 3f));
        var compressed = AnimationCurveCompressor.Compress(data, CurveCompressionSettings.Default, out var report);

        Assert.Single(compressed.Channels[0].PositionTimes);
        Assert.Equal(120, report.KeysBefore);
        Assert.Equal(1, report.KeysAfter);
    }

    [Fact]
    public void Compress_ALinearTrack_KeepsOnlyItsEnds() {
        var data = Ramp("Mid", 120, static index => new Vector3(index * 0.1f, 0f, 0f));
        var compressed = AnimationCurveCompressor.Compress(data, CurveCompressionSettings.Default, out _);

        Assert.Equal(2, compressed.Channels[0].PositionTimes.Length);
    }

    [Fact]
    public void Compress_ACurve_StaysWithinTheTolerance() {
        // A slow curve sampled once a frame — what an exporter emits, and where the reduction is.
        var data = Ramp("Mid", 240, static index => new Vector3(MathF.Sin(index * 0.01f), 0f, 0f));
        var settings = new CurveCompressionSettings(Position: 1e-3f);
        var compressed = AnimationCurveCompressor.Compress(data, settings, out var report);

        var original = AnimationClip.Create(data, skeleton);
        var reduced = AnimationClip.Create(compressed, skeleton);

        Assert.True(report.KeysAfter < report.KeysBefore / 4, $"kept {report.KeysAfter} of {report.KeysBefore}");

        var a = new BoneTransform[skeleton.JointCount];
        var b = new BoneTransform[skeleton.JointCount];

        for (var step = 0; step <= 400; step++) {
            var time = step / 400f;
            original.Sample(time, a);
            reduced.Sample(time, b);

            Assert.True(
                (a[1].Translation - b[1].Translation).Length() <= 1e-3f,
                $"at {time}: {a[1].Translation} vs {b[1].Translation}"
            );
        }
    }

    [Fact]
    public void Compress_ErrorIsMeasuredAgainstTheAnchor_NotTheNeighbour() {
        // A staircase whose every step is inside the tolerance and whose hundredth step is a long
        // way from the first. Fitting against the previous key would drop the lot.
        var data = Ramp("Mid", 101, static index => new Vector3(index * 0.001f, 0f, 0f));
        var compressed = AnimationCurveCompressor.Compress(data, new(Position: 0.002f), out _);

        var reduced = AnimationClip.Create(compressed, skeleton);
        var pose = new BoneTransform[skeleton.JointCount];

        reduced.Sample(1f, pose);
        Assert.Equal(0.1f, pose[1].Translation.X, 1e-3f);
    }

    [Fact]
    public void Compress_AChannelThatNeverMoves_IsDropped() {
        var data = new AnimationClipData {
            Name = "Idle",
            Duration = 1f,
            Channels = [
                new() { Target = "Mid", PositionTimes = [], Positions = [] },
                new() { Target = "Tip", PositionTimes = [0f, 1f], Positions = [Vector3.One, Vector3.One] }
            ]
        };

        var compressed = AnimationCurveCompressor.Compress(data, CurveCompressionSettings.Default, out var report);

        Assert.Single(compressed.Channels);
        Assert.Equal("Tip", compressed.Channels[0].Target);
        Assert.Equal(2, report.ChannelsBefore);
        Assert.Equal(1, report.ChannelsAfter);
    }

    [Fact]
    public void Compress_RotationTrack_UsesTheAngularTolerance() {
        var keys = 180;
        var times = new float[keys];
        var values = new Quaternion[keys];

        for (var index = 0; index < keys; index++) {
            times[index] = index / (float)(keys - 1);
            values[index] = Quaternion.FromAxisAngle(Vector3.UnitY, index * 0.01f);
        }

        var data = new AnimationClipData {
            Name = "Turn",
            Duration = 1f,
            Channels = [new() { Target = "Mid", RotationTimes = times, Rotations = values }]
        };

        var compressed = AnimationCurveCompressor.Compress(data, CurveCompressionSettings.Default, out var report);
        var reduced = AnimationClip.Create(compressed, skeleton);
        var original = AnimationClip.Create(data, skeleton);

        Assert.True(report.KeysAfter < report.KeysBefore, $"kept all {report.KeysAfter}");

        var a = new BoneTransform[skeleton.JointCount];
        var b = new BoneTransform[skeleton.JointCount];

        for (var step = 0; step <= 200; step++) {
            var time = step / 200f;
            original.Sample(time, a);
            reduced.Sample(time, b);

            Assert.True(Quaternion.SameRotation(a[1].Rotation, b[1].Rotation, 2e-3f), $"at {time}");
        }
    }

    [Fact]
    public void Compress_LeavesTheInputAlone() {
        var data = Ramp("Mid", 60, static _ => Vector3.Zero);
        AnimationCurveCompressor.Compress(data);

        Assert.Equal(60, data.Channels[0].PositionTimes.Length);
    }

    [Fact]
    public void Ratio_ReportsWhatSurvived() {
        var data = Ramp("Mid", 100, static _ => Vector3.Zero);
        AnimationCurveCompressor.Compress(data, CurveCompressionSettings.Default, out var report);

        Assert.Equal(0.01f, report.Ratio, 1e-4f);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(64)]
    [InlineData(1000)]
    public void Sample_AgreesWithLinearInterpolationAtEveryKeyCount(int keys) {
        // Crosses the threshold at which a track stops being searched and starts being indexed, so
        // the same assertion covers both lookup paths.
        var times = new float[keys];
        var values = new Vector3[keys];

        for (var index = 0; index < keys; index++) {
            times[index] = index / (float)(keys - 1);
            values[index] = new(index, 0f, 0f);
        }

        var clip = AnimationClip.Create(
            new AnimationClipData {
                Name = "Ramp",
                Duration = 1f,
                Channels = [new() { Target = "Mid", PositionTimes = times, Positions = values }]
            },
            skeleton
        );

        var pose = new BoneTransform[skeleton.JointCount];

        for (var step = 0; step <= 997; step++) {
            var time = step / 997f;
            clip.Sample(time, pose);

            Assert.Equal(time * (keys - 1), pose[1].Translation.X, 1e-3f);
        }
    }

    [Fact]
    public void Sample_UnevenlySpacedKeys_FindsTheRightSpan() {
        // Keys bunched at the start and sparse at the end, so a slice of the clip's duration holds
        // anything from no keys to twenty. The index has to be right at both extremes.
        var times = new List<float>();
        var values = new List<Vector3>();

        for (var index = 0; index < 40; index++) {
            times.Add(index * 0.001f);
            values.Add(new(index * 0.001f, 0f, 0f));
        }

        for (var index = 1; index <= 10; index++) {
            times.Add(0.04f + (index * 0.096f));
            values.Add(new(0.04f + (index * 0.096f), 0f, 0f));
        }

        var clip = AnimationClip.Create(
            new AnimationClipData {
                Name = "Uneven",
                Duration = 1f,
                Channels = [new() { Target = "Mid", PositionTimes = [.. times], Positions = [.. values] }]
            },
            skeleton
        );

        var pose = new BoneTransform[skeleton.JointCount];

        for (var step = 0; step <= 1000; step++) {
            var time = step / 1000f;
            clip.Sample(time, pose);

            // The value equals the time everywhere the track covers, and clamps past its last key.
            var expected = MathF.Min(time, times[^1]);
            Assert.Equal(expected, pose[1].Translation.X, 1e-4f);
        }
    }

    [Fact]
    public void Sample_ClipShorterThanItsKeys_StillIndexesCorrectly() {
        // A track whose keys stop a third of the way through the clip: most slices of the duration
        // map to its last key, which is exactly the case an off-by-one in the table would break.
        var times = new float[32];
        var values = new Vector3[32];

        for (var index = 0; index < times.Length; index++) {
            times[index] = index / (float)(times.Length - 1) * 0.3f;
            values[index] = new(index, 0f, 0f);
        }

        var clip = AnimationClip.Create(
            new AnimationClipData {
                Name = "Early",
                Duration = 1f,
                Channels = [new() { Target = "Mid", PositionTimes = times, Positions = values }]
            },
            skeleton
        );

        var pose = new BoneTransform[skeleton.JointCount];

        clip.Sample(0.15f, pose);
        Assert.Equal(15.5f, pose[1].Translation.X, 0.1f);

        clip.Sample(0.9f, pose);
        Assert.Equal(31f, pose[1].Translation.X, 1e-4f);
    }
}

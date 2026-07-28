// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net.Messaging;
using Vixen.Net.Motion;
using Vixen.Net.Replication;
using Vixen.Net.Transport;
using Xunit;

namespace Vixen.Net.Tests.Motion;

/// <summary>Interpolation, extrapolation, the snap threshold, and what a rotation costs.</summary>
public sealed class MotionTests {
    [Fact]
    public void BetweenTwoSamples_ThePositionIsInterpolated() {
        var buffer = new SnapshotBuffer();
        buffer.Add(new(new(10), new(0, 0, 0), Quaternion.Identity));
        buffer.Add(new(new(12), new(4, 0, 0), Quaternion.Identity));

        Assert.True(buffer.TrySample(new(11), 0f, out var half));
        Assert.Equal(2f, half.Position.X, 3);

        Assert.True(buffer.TrySample(new(10), 0.5f, out var quarter));
        Assert.Equal(1f, quarter.Position.X, 3);

        Assert.Equal(2, buffer.InterpolatedCount);
        Assert.Equal(0, buffer.ExtrapolatedCount);
    }

    [Fact]
    public void TheRotationIsInterpolatedTheLongWayRound_WhichIsToSayTheShortOne() {
        var buffer = new SnapshotBuffer();
        var quarter = Quaternion.FromAxisAngle(Vector3.UnitY, MathF.PI / 2f);

        buffer.Add(new(new(1), Vector3.Zero, Quaternion.Identity));
        buffer.Add(new(new(3), Vector3.Zero, quarter));

        Assert.True(buffer.TrySample(new(2), 0f, out var middle));

        // Half of a quarter turn, which slerp gets right and a component-wise lerp does not.
        var eighth = Quaternion.FromAxisAngle(Vector3.UnitY, MathF.PI / 4f);

        Assert.Equal(eighth.Y, middle.Rotation.Y, 3);
        Assert.Equal(eighth.W, middle.Rotation.W, 3);
    }

    [Fact]
    public void PastTheNewestSample_MotionIsGuessedAtFromTheLastTwo() {
        var buffer = new SnapshotBuffer();
        buffer.Add(new(new(10), new(0, 0, 0), Quaternion.Identity));
        buffer.Add(new(new(11), new(1, 0, 0), Quaternion.Identity));

        Assert.True(buffer.TrySample(new(13), 0f, out var ahead));

        // Two ticks past the last one it heard about, at one metre a tick.
        Assert.Equal(3f, ahead.Position.X, 3);
        Assert.Equal(1, buffer.ExtrapolatedCount);
    }

    [Fact]
    public void TheGuessStopsRatherThanRunningAwayWithItself() {
        var buffer = new SnapshotBuffer(options: new() { MaxExtrapolationTicks = 3 });
        buffer.Add(new(new(10), new(0, 0, 0), Quaternion.Identity));
        buffer.Add(new(new(11), new(1, 0, 0), Quaternion.Identity));

        Assert.True(buffer.TrySample(new(60), 0f, out var far));

        // Fifty ticks of silence is not fifty metres of walking: three ticks past the last position
        // it heard about, and no further. A player who stopped a second ago should not still be
        // crossing the map on everybody else's screen.
        Assert.Equal(4f, far.Position.X, 3);
        Assert.True(buffer.StarvedCount > 0);
    }

    [Fact]
    public void TwoSamplesTooFarApartAreATeleportRatherThanAVeryFastWalk() {
        var buffer = new SnapshotBuffer(options: new() { SnapDistance = 5f });
        buffer.Add(new(new(1), new(0, 0, 0), Quaternion.Identity));
        buffer.Add(new(new(2), new(100, 0, 0), Quaternion.Identity));

        Assert.True(buffer.TrySample(new(1), 0.5f, out var sample));

        // Not at fifty. Whatever happened, it happened.
        Assert.Equal(100f, sample.Position.X, 3);
        Assert.Equal(1, buffer.SnappedCount);
        Assert.Equal(0, buffer.InterpolatedCount);
    }

    [Fact]
    public void ASampleOlderThanOneAlreadyHeld_IsDropped() {
        var buffer = new SnapshotBuffer();

        Assert.True(buffer.Add(new(new(10), Vector3.Zero, Quaternion.Identity)));
        Assert.False(buffer.Add(new(new(9), Vector3.One, Quaternion.Identity)));
        Assert.False(buffer.Add(new(new(10), Vector3.One, Quaternion.Identity)));
        Assert.Equal(1, buffer.Count);
        Assert.Equal(2, buffer.StaleCount);
    }

    [Fact]
    public void TheOldestGoesWhenTheBufferIsFull() {
        var buffer = new SnapshotBuffer(capacity: 4);

        for (var i = 1u; i <= 10u; i++) {
            buffer.Add(new(new(i), new(i, 0, 0), Quaternion.Identity));
        }

        Assert.Equal(4, buffer.Count);
        Assert.Equal(new Tick(7), buffer.Oldest.Tick);
        Assert.Equal(new Tick(10), buffer.Newest.Tick);
    }

    [Fact]
    public void WithNothingToGoOn_TheBufferSaysSo() {
        var buffer = new SnapshotBuffer();

        Assert.False(buffer.TrySample(new(1), 0f, out _));
        Assert.Equal(1, buffer.StarvedCount);
        Assert.Throws<InvalidOperationException>(() => buffer.Newest);
    }

    [Fact]
    public void WithOneSample_ItIsHeld() {
        var buffer = new SnapshotBuffer();
        buffer.Add(new(new(5), new(1, 2, 3), Quaternion.Identity));

        Assert.True(buffer.TrySample(new(9), 0f, out var sample));
        Assert.Equal(new Vector3(1, 2, 3), sample.Position);
        Assert.Equal(new Tick(9), sample.Tick);
    }

    [Fact]
    public void BeforeTheOldestSample_TheOldestIsHeld() {
        var buffer = new SnapshotBuffer();
        buffer.Add(new(new(10), new(1, 0, 0), Quaternion.Identity));
        buffer.Add(new(new(12), new(2, 0, 0), Quaternion.Identity));

        Assert.True(buffer.TrySample(new(4), 0f, out var sample));
        Assert.Equal(1f, sample.Position.X, 3);
        Assert.True(buffer.StarvedCount > 0);
    }

    [Fact]
    public void ARotationCostsFourBytesAndComesBackAlmostExactly() {
        Span<byte> buffer = stackalloc byte[16];
        var writer = new BitWriter(buffer);
        var rotation = Quaternion.Normalize(new(0.3f, -0.5f, 0.2f, 0.78f));

        writer.WriteRotation(rotation);

        Assert.Equal(32, writer.BitsWritten);
        Assert.True(writer.TryFinish(out var packet));

        var reader = new BitReader(packet);

        Assert.True(reader.TryReadRotation(out var read));
        Assert.Equal(rotation.X, read.X, 2);
        Assert.Equal(rotation.Y, read.Y, 2);
        Assert.Equal(rotation.Z, read.Z, 2);
        Assert.Equal(rotation.W, read.W, 2);
    }

    [Fact]
    public void ARotationAndItsNegativeAreTheSameRotationAndSurviveAsOne() {
        // q and -q are the same rotation, and smallest-three sends whichever of them has a positive
        // largest component. What comes back must therefore still turn things the same way.
        var rotation = Quaternion.Normalize(new(0.1f, 0.2f, 0.3f, -0.9f));
        var round = RoundTrip(rotation);

        Assert.Equal(MathF.Abs(rotation.W), MathF.Abs(round.W), 2);
        Assert.Equal(rotation.X * MathF.Sign(rotation.W), round.X * MathF.Sign(round.W), 2);
    }

    [Theory]
    [InlineData(1f, 0f, 0f, 0f)]
    [InlineData(0f, 1f, 0f, 0f)]
    [InlineData(0f, 0f, 1f, 0f)]
    [InlineData(0f, 0f, 0f, 1f)]
    public void EachAxisOfTheDroppedComponentWorks(float x, float y, float z, float w) {
        var round = RoundTrip(Quaternion.Normalize(new(x, y, z, w)));

        Assert.Equal(1f, MathF.Abs(round.X) + MathF.Abs(round.Y) + MathF.Abs(round.Z) + MathF.Abs(round.W), 2);
    }

    [Fact]
    public void AQuantizedPositionCostsSixBytes() {
        Span<byte> buffer = stackalloc byte[32];
        var range = new QuantizeRange(-1000f, 1000f, 16);
        var writer = new BitWriter(buffer);

        writer.WriteVector3(new(12.5f, -400f, 3f), range);

        Assert.Equal(48, writer.BitsWritten);
        Assert.True(writer.TryFinish(out var packet));

        var reader = new BitReader(packet);

        Assert.True(reader.TryReadVector3(range, out var read));
        Assert.InRange(read.X, 12.5f - range.MaxError, 12.5f + range.MaxError);
        Assert.InRange(read.Y, -400f - range.MaxError, -400f + range.MaxError);
    }

    [Fact]
    public void AWholeNetworkTransformIsThirteenBytes() {
        using var world = new World();

        var entity = world.Create(
            new NetworkTransform {
                Position = new(1, 2, 3),
                Rotation = Quaternion.Identity,
                TeleportCount = 2
            }
        );

        Span<byte> buffer = stackalloc byte[64];
        var writer = new BitWriter(buffer);
        var replicator = new NetworkTransformReplicator();

        replicator.Write(world, entity, ref writer);

        // 48 for the position, 32 for the rotation, 8 for the teleport counter.
        Assert.Equal(88, writer.BitsWritten);
        Assert.True(writer.TryFinish(out var packet));
        Assert.Equal(11, packet.Length);
    }

    [Fact]
    public void ANetworkTransformSurvivesTheRoundTrip() {
        using var server = new World();
        using var client = new World();

        var sent = new NetworkTransform {
            Position = new(12.5f, -30f, 400f),
            Rotation = Quaternion.FromAxisAngle(Vector3.UnitY, 1.1f),
            TeleportCount = 7
        };

        var entity = server.Create(sent);
        var replicator = new NetworkTransformReplicator();
        Span<byte> buffer = stackalloc byte[64];
        var writer = new BitWriter(buffer);

        replicator.Write(server, entity, ref writer);

        Assert.True(writer.TryFinish(out var packet));

        var mirrored = client.Create();
        var reader = new BitReader(packet);

        Assert.True(replicator.Apply(client, mirrored, ref reader));

        ref readonly var got = ref client.Read<NetworkTransform>(mirrored);
        var error = NetworkTransformReplicator.PositionRange.MaxError;

        Assert.InRange(got.Position.X, sent.Position.X - error, sent.Position.X + error);
        Assert.InRange(got.Position.Z, sent.Position.Z - error, sent.Position.Z + error);
        Assert.Equal(sent.Rotation.Y, got.Rotation.Y, 2);
        Assert.Equal(7, got.TeleportCount);
    }

    [Fact]
    public void AMalformedTransformIsRefusedRatherThanApplied() {
        using var world = new World();
        var entity = world.Create();
        var replicator = new NetworkTransformReplicator();
        var reader = new BitReader([1, 2]);

        Assert.False(replicator.Apply(world, entity, ref reader));
        Assert.False(world.Has<NetworkTransform>(entity));
    }

    [Fact]
    public void ACorrectionMovesTheSimulationAtOnceAndTheCameraOverTime() {
        var smoothing = new OwnerSmoothing { HalfLife = TimeSpan.FromMilliseconds(100) };

        smoothing.Correct(new(1, 0, 0), Vector3.Zero);

        // The simulation is at zero. What is drawn starts where the player thought they were and
        // arrives, so nothing they see twitches while everything the server judges is already right.
        Assert.True(smoothing.IsSmoothing);
        Assert.Equal(1f, smoothing.Apply(Vector3.Zero, TimeSpan.Zero).X, 3);
        Assert.Equal(0.5f, smoothing.Apply(Vector3.Zero, TimeSpan.FromMilliseconds(100)).X, 2);
        Assert.Equal(0.25f, smoothing.Apply(Vector3.Zero, TimeSpan.FromMilliseconds(100)).X, 2);
    }

    [Fact]
    public void SmoothingFinishesRatherThanApproachingForEver() {
        var smoothing = new OwnerSmoothing { HalfLife = TimeSpan.FromMilliseconds(50) };
        smoothing.Correct(new(0.5f, 0, 0), Vector3.Zero);

        for (var i = 0; i < 40; i++) {
            smoothing.Apply(Vector3.Zero, TimeSpan.FromMilliseconds(16));
        }

        Assert.False(smoothing.IsSmoothing);
        Assert.Equal(Vector3.Zero, smoothing.Apply(Vector3.Zero, TimeSpan.FromMilliseconds(16)));
    }

    [Fact]
    public void ACorrectionTooLargeToHideIsNotHidden() {
        var smoothing = new OwnerSmoothing { SnapDistance = 3f };

        smoothing.Correct(new(100, 0, 0), Vector3.Zero);

        // A rubber-band across the map is not something to drag a camera through.
        Assert.False(smoothing.IsSmoothing);
        Assert.Equal(1, smoothing.SnapCount);
        Assert.Equal(Vector3.Zero, smoothing.Apply(Vector3.Zero, TimeSpan.FromMilliseconds(16)));
    }

    [Fact]
    public void ASecondCorrectionAddsToTheFirstRatherThanReplacingIt() {
        var smoothing = new OwnerSmoothing { HalfLife = TimeSpan.FromSeconds(10) };

        smoothing.Correct(new(1, 0, 0), Vector3.Zero);
        smoothing.Correct(new(1, 0, 0), Vector3.Zero);

        // Dropping the remainder would put the visual back where the first correction had already
        // decided it should not be.
        Assert.InRange(smoothing.Apply(Vector3.Zero, TimeSpan.Zero).X, 1.9f, 2f);
        Assert.Equal(2, smoothing.CorrectionCount);
    }

    static Quaternion RoundTrip(in Quaternion rotation) {
        Span<byte> buffer = stackalloc byte[8];
        var writer = new BitWriter(buffer);
        writer.WriteRotation(rotation);

        Assert.True(writer.TryFinish(out var packet));

        var reader = new BitReader(packet);

        Assert.True(reader.TryReadRotation(out var read));

        return read;
    }
}

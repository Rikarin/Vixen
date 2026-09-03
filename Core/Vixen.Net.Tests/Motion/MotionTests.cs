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
        // Opt-in now, and this test is the only place in the tree that opts in. See
        // ADistanceThatIsAlsoASpeedNoLongerSnapsAnythingByItself for why it is not the default.
        var buffer = new SnapshotBuffer(options: new() { SnapDistance = 5f });
        buffer.Add(new(new(1), new(0, 0, 0), Quaternion.Identity));
        buffer.Add(new(new(2), new(100, 0, 0), Quaternion.Identity));

        Assert.True(buffer.TrySample(new(1), 0.5f, out var sample));

        // Not at fifty. Whatever happened, it happened.
        Assert.Equal(100f, sample.Position.X, 3);
        Assert.Equal(1, buffer.SnappedCount);
        Assert.Equal(0, buffer.InterpolatedCount);
    }

    /// <summary>
    ///     ⚠ The counter was written, quantised, sent and decoded, and the buffer never looked at it.
    /// </summary>
    [Fact]
    public void AChangedTeleportCountSnaps_HoweverShortTheJump() {
        var buffer = new SnapshotBuffer();

        // Two metres: a door, a short blink. Far under any distance threshold that would not also
        // catch a projectile, which is exactly why the distance cannot answer this.
        buffer.Add(new(new(1), new(0, 0, 0), Quaternion.Identity, 4));
        buffer.Add(new(new(2), new(2, 0, 0), Quaternion.Identity, 5));

        Assert.True(buffer.TrySample(new(1), 0.5f, out var sample));

        Assert.Equal(2f, sample.Position.X, 3);
        Assert.Equal(1, buffer.SnappedCount);
        Assert.Equal(0, buffer.InterpolatedCount);
    }

    /// <summary>The other half: a counter that does not change is not a teleport, however far it went.</summary>
    [Fact]
    public void ADistanceThatIsAlsoASpeedNoLongerSnapsAnythingByItself() {
        var buffer = new SnapshotBuffer();

        // A hundred metres in one 20 Hz snapshot interval is 2 km/s — absurd for a player and
        // ordinary for a railgun round. The old five-metre default called it a teleport at 100 m/s.
        buffer.Add(new(new(1), new(0, 0, 0), Quaternion.Identity, 3));
        buffer.Add(new(new(2), new(100, 0, 0), Quaternion.Identity, 3));

        Assert.True(buffer.TrySample(new(1), 0.5f, out var sample));

        Assert.Equal(50f, sample.Position.X, 3);
        Assert.Equal(0, buffer.SnappedCount);
        Assert.Equal(1, buffer.InterpolatedCount);
    }

    /// <summary>
    ///     ⚠ Extrapolating across a teleport does not merely fail to snap — it takes the jump as this
    ///     tick's velocity and keeps travelling, so the object leaves the map rather than arriving.
    /// </summary>
    [Fact]
    public void ATeleportIsNotAVelocity() {
        var buffer = new SnapshotBuffer();
        buffer.Add(new(new(10), new(0, 0, 0), Quaternion.Identity, 1));
        buffer.Add(new(new(11), new(60, 0, 0), Quaternion.Identity, 2));

        // Two ticks past the newest sample. Extrapolated from the pair, that is 180 metres away.
        Assert.True(buffer.TrySample(new(13), 0f, out var ahead));

        Assert.Equal(60f, ahead.Position.X, 3);
        Assert.Equal(0, buffer.ExtrapolatedCount);
        Assert.Equal(1, buffer.SnappedCount);
    }

    /// <summary>Wrapping is not a break in the sequence: 255 → 0 is a change like any other.</summary>
    [Fact]
    public void TheCounterWrapsAndTheWrapIsStillATeleport() {
        var buffer = new SnapshotBuffer();
        buffer.Add(new(new(1), new(0, 0, 0), Quaternion.Identity, 255));
        buffer.Add(new(new(2), new(1, 0, 0), Quaternion.Identity, 0));

        Assert.True(buffer.TrySample(new(1), 0.5f, out var sample));

        Assert.Equal(1f, sample.Position.X, 3);
        Assert.Equal(1, buffer.SnappedCount);
    }

    /// <summary>Zero would snap on every movement, which is the fallback's opposite.</summary>
    [Fact]
    public void ASnapDistanceOfZeroIsRefusedRatherThanTakenAsOff() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotBufferOptions { SnapDistance = 0f });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotBufferOptions { SnapDistance = -1f });
        Assert.Null(new SnapshotBufferOptions().SnapDistance);
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

    /// <summary>A door that only rotates stops paying for a position.</summary>
    [Fact]
    public void ARotationOnlyTransformCostsFortyBitsRatherThanEightyEight() {
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
        var replicator = new NetworkTransformReplicator(NetworkTransformAxes.Rotation);

        replicator.Write(world, entity, ref writer);

        // 32 for the rotation and 8 for the teleport counter. The forty-eight the position cost are
        // the saving, and they are saved on every tick rather than only on the ones it did not move.
        Assert.Equal(40, writer.BitsWritten);
        Assert.Equal(40, DeltaCodec.TotalBits(replicator.Lanes));
    }

    /// <summary>
    ///     ⚠ An axis nobody sends keeps the value the receiver already had, and is emphatically not
    ///     zero.
    /// </summary>
    /// <remarks>
    ///     The whole risk of a narrowed replicator. A door replicating only its rotation has its
    ///     position from the prefab that built it; writing a fresh <c>NetworkTransform</c> would put
    ///     every door in the level at the world origin — a zeroed field whose zero is a perfectly
    ///     valid position, which is how this class of bug always looks.
    /// </remarks>
    [Fact]
    public void AnAxisThatIsNotSentKeepsWhatTheReceiverHad() {
        using var server = new World();
        using var client = new World();

        var replicator = new NetworkTransformReplicator(NetworkTransformAxes.Rotation);

        var sent = server.Create(
            new NetworkTransform {
                Position = new(500f, 0f, -250f),
                Rotation = Quaternion.FromAxisAngle(Vector3.UnitY, 1.1f)
            }
        );

        // The door, where the prefab put it.
        var door = client.Create(new NetworkTransform { Position = new(12f, 0f, 34f), Rotation = Quaternion.Identity });

        Span<byte> buffer = stackalloc byte[64];
        var writer = new BitWriter(buffer);
        replicator.Write(server, sent, ref writer);

        Assert.True(writer.TryFinish(out var packet));

        var reader = new BitReader(packet);
        Assert.True(replicator.Apply(client, door, ref reader));

        ref readonly var got = ref client.Read<NetworkTransform>(door);
        Assert.Equal(new Vector3(12f, 0f, 34f), got.Position);
        Assert.Equal(server.Read<NetworkTransform>(sent).Rotation.Y, got.Rotation.Y, 2);
    }

    /// <summary>
    ///     ⚠ A narrowed replicator has a different wire id, so two peers that disagree about the mask
    ///     fail the handshake rather than each other's transforms.
    /// </summary>
    /// <remarks>
    ///     The instrument, checked first. Two masks over one type id would decode each other's lanes
    ///     into plausible wrong numbers and nothing would say so; folding the mask into the type name
    ///     folds it into <c>ManifestHash</c>, which the handshake compares. The unmasked default
    ///     keeps the bare name, so nothing that ships today moves.
    /// </remarks>
    [Fact]
    public void ANarrowedReplicatorIsADifferentTypeOnTheWire() {
        var all = new NetworkTransformReplicator();
        var rotationOnly = new NetworkTransformReplicator(NetworkTransformAxes.Rotation);

        Assert.Equal("Vixen.Net.Motion.NetworkTransform", all.TypeName);
        Assert.Equal(ReplicationRegistry.HashTypeName("Vixen.Net.Motion.NetworkTransform"), all.TypeId);
        Assert.NotEqual(all.TypeId, rotationOnly.TypeId);

        var one = new ReplicationRegistry();
        var other = new ReplicationRegistry();
        one.Register(all);
        other.Register(rotationOnly);

        Assert.NotEqual(one.ManifestHash, other.ManifestHash);
    }

    /// <summary>A replicator that sends no axis is refused rather than registered.</summary>
    [Fact]
    public void AReplicatorWithNoAxesIsRefused() {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new NetworkTransformReplicator(NetworkTransformAxes.None)
        );
    }

    /// <summary>The frame a transform is quoted in survives the wire.</summary>
    [Fact]
    public void ANetworkParentSurvivesTheRoundTrip() {
        using var server = new World();
        using var client = new World();

        var entity = server.Create(new NetworkParent { Value = 41 });
        var replicator = new NetworkParentReplicator();
        Span<byte> buffer = stackalloc byte[16];
        var writer = new BitWriter(buffer);

        replicator.Write(server, entity, ref writer);

        Assert.True(writer.TryFinish(out var packet));

        var mirrored = client.Create();
        var reader = new BitReader(packet);

        Assert.True(replicator.Apply(client, mirrored, ref reader));
        Assert.Equal(41u, client.Read<NetworkParent>(mirrored).Value);

        // ⚠ And it outranks the transform, so within one snapshot the frame precedes what is quoted
        // in it. Records are written in descending priority.
        Assert.True(replicator.Priority > new NetworkTransformReplicator().Priority);
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

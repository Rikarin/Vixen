// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net.Messaging;
using Vixen.Net.Motion;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;
using Vixen.Net.Transport;
using Xunit;

namespace Vixen.Net.Tests.Replication;

/// <summary>Delta encoding: that it round-trips, that it saves, and that loss cannot corrupt it.</summary>
public sealed class DeltaTests : IDisposable {
    static readonly PlayerId Player = new(1);

    readonly World server = new("server");
    readonly World client = new("client");
    readonly ReplicationRegistry registry = new();
    readonly NetworkIdAllocator ids = new();
    readonly ReplicationServer sender;
    readonly ReplicationClient receiver;
    readonly byte[] buffer = new byte[8192];

    uint tick = 1;

    public DeltaTests() {
        registry.Register(new NetworkTransformReplicator());
        sender = new(registry);
        receiver = new(registry);
    }

    public void Dispose() {
        server.Dispose();
        client.Dispose();
    }

    /// <summary>
    ///     Delta then apply equals full state, over random layouts and random bits.
    /// </summary>
    /// <remarks>
    ///     The property <c>docs/plan/16</c> asks for, and the reason the codec was built as a
    ///     transform between bit streams rather than as generated per-component code: it can be
    ///     checked against arbitrary values without declaring a single component.
    /// </remarks>
    [Theory]
    [InlineData(1u)]
    [InlineData(7u)]
    [InlineData(99u)]
    [InlineData(20260728u)]
    public void ADifferenceAppliedToTheOldValueGivesTheNewOne(ulong seed) {
        var random = new DeterministicRandom(seed);

        for (var round = 0; round < 200; round++) {
            var lanes = RandomLanes(random);
            var previous = RandomEncoding(random, lanes);
            var current = RandomEncoding(random, lanes);

            Assert.True(TryRoundTrip(lanes, previous, current, out var rebuilt, out _));
            Assert.Equal(current, rebuilt);
        }
    }

    [Fact]
    public void AValueThatDidNotChange_CostsOneBitAField() {
        var lanes = new WireLane[] { new("x", 16, true), new("y", 16, true), new("z", 16, true) };
        var same = RandomEncoding(new(4), lanes);

        Assert.True(TryRoundTrip(lanes, same, same, out var rebuilt, out var bits));
        Assert.Equal(same, rebuilt);
        Assert.Equal(lanes.Length, bits);
    }

    [Fact]
    public void AValueThatMovedALittle_CostsFarLessThanSendingIt() {
        var lanes = new WireLane[] { new("x", 16, true), new("y", 16, true), new("z", 16, true) };
        var previous = Encoding(lanes, [30000, 30000, 30000]);
        var current = Encoding(lanes, [30003, 29997, 30000]);

        Assert.True(TryRoundTrip(lanes, previous, current, out var rebuilt, out var bits));
        Assert.Equal(current, rebuilt);

        // Two lanes moved by a few levels, one did not. Against the 48 bits of sending it whole.
        Assert.True(bits < 24, $"a nudge cost {bits} bits");
    }

    [Fact]
    public void AValueThatJumped_IsNeverWorseThanAFewBits() {
        var lanes = new WireLane[] { new("x", 16, true), new("y", 16, true), new("z", 16, true) };
        var previous = Encoding(lanes, [0, 0, 0]);
        var current = Encoding(lanes, [65535, 1, 40000]);

        Assert.True(TryRoundTrip(lanes, previous, current, out var rebuilt, out var bits));
        Assert.Equal(current, rebuilt);
        Assert.True(bits <= DeltaCodec.MaxBits(lanes), $"{bits} is past the worst case");
    }

    /// <summary>
    ///     The layout a replicator declares has to be the layout it writes.
    /// </summary>
    /// <remarks>
    ///     The one assumption the whole scheme rests on, and the one thing a hand-written replicator
    ///     can get wrong. The server checks the totals and silently sends whole records when they
    ///     disagree, so a mistake costs bandwidth rather than correctness — but the shipped
    ///     replicator should not be relying on that.
    /// </remarks>
    [Fact]
    public void NetworkTransformsDeclaredLayoutIsTheOneItWrites() {
        var replicator = new NetworkTransformReplicator();
        var entity = server.Create(new NetworkTransform { Position = new(1f, 2f, 3f), Rotation = Quaternion.Identity });
        var writer = new BitWriter(buffer);

        replicator.Write(server, entity, ref writer);

        Assert.Equal(writer.BitsWritten, DeltaCodec.TotalBits(replicator.Lanes));
    }

    [Fact]
    public void AClientConvergesWhileEverythingIsSentAsDifferences() {
        var entity = Spawn();

        for (var step = 0; step < 40; step++) {
            Move(entity, step * 0.25f);
            Assert.True(Replicate());
        }

        Assert.True(sender.DeltaRecordCount > 30, $"only {sender.DeltaRecordCount} differences");
        AssertAgrees(entity);
    }

    /// <summary>
    ///     A client that applied a value it never managed to acknowledge still converges.
    /// </summary>
    /// <remarks>
    ///     The case the capture history exists for. Without it the server measures from the value it
    ///     last heard about while the receiver holds a newer one, and the difference lands on the
    ///     wrong thing — silently, because both ends believe they agree.
    /// </remarks>
    [Fact]
    public void AcknowledgementsBeingLost_DoesNotCorruptAnything() {
        var entity = Spawn();

        for (var step = 0; step < 60; step++) {
            Move(entity, step * 0.3f);

            // Every third acknowledgement goes missing. The snapshots all arrive.
            Replicate(acknowledge: step % 3 == 0);
        }

        AssertAgrees(entity);
    }

    [Fact]
    public void SnapshotsBeingLost_DoesNotCorruptAnything() {
        var entity = Spawn();

        for (var step = 0; step < 60; step++) {
            Move(entity, step * 0.3f);
            Replicate(deliver: step % 4 != 0);
        }

        // Everything that was lost comes again, measured from wherever the receiver actually got to.
        Move(entity, 100f);
        Assert.True(Replicate());
        AssertAgrees(entity);
    }

    [Fact]
    public void ASnapshotArrivingTwice_IsNotAppliedTwice() {
        var entity = Spawn();

        Move(entity, 1f);
        sender.Capture(server, Tick());
        Assert.True(sender.TryWriteSnapshot(server, Player, Tick(), buffer, out var snapshot));

        var copy = snapshot.ToArray();
        Assert.True(receiver.TryApply(client, copy));
        Assert.True(receiver.TryApply(client, copy));

        Assert.Equal(1, receiver.StaleSnapshotCount);
        server.AdvanceVersion();
        AssertAgrees(entity);
    }

    /// <summary>
    ///     Acknowledging two ticks at once leaves the baseline holding the newer of the two.
    /// </summary>
    /// <remarks>
    ///     A regression. Folding ran newest-first, so an older tick's value overwrote a newer one and
    ///     the baseline then claimed the connection held something it had already replaced. That is a
    ///     redundant re-send while records go whole, and a corrupted value the moment one is a
    ///     difference measured from it.
    /// </remarks>
    [Fact]
    public void FoldingTwoTicksAtOnce_KeepsTheNewerValue() {
        var baseline = new ConnectionBaseline();
        var key = new BaselineKey(new(1), 42);

        baseline.RecordSent(new(10), key, hash: 111, capturedAt: new(10));
        baseline.RecordSent(new(11), key, hash: 222, capturedAt: new(11));

        Assert.True(baseline.Acknowledge(new(11)));

        Assert.True(baseline.IsCurrent(key, 222));
        Assert.False(baseline.IsCurrent(key, 111));
        Assert.True(baseline.TryGetBaseline(key, out var capturedAt, out var hash));
        Assert.Equal(new Tick(11), capturedAt);
        Assert.Equal(222u, hash);
    }

    /// <summary>An unacknowledged value is not repeated until it could have been answered.</summary>
    /// <remarks>
    ///     Without this a record goes out every tick until it is acknowledged, so a four-tick round
    ///     trip sends every change four times — which the soak measured as most of a connection's
    ///     bandwidth. The value's hash is part of the question, so this only ever suppresses repeating
    ///     oneself; a value that changed again goes out at once, which the second half asserts.
    /// </remarks>
    [Fact]
    public void AValueAlreadyOnItsWay_IsNotSentAgainUntilItCouldHaveBeenAnswered() {
        sender.ResendDelayTicks = 3;

        var entity = Spawn();
        Move(entity, 1f);

        Assert.True(Replicate(acknowledge: false), "the first send did not happen");

        var afterFirst = sender.SuppressedRecordCount;

        // Nothing changed and nothing was acknowledged: repeating it would be telling them something
        // that is still travelling.
        Assert.False(Replicate(acknowledge: false));
        Assert.False(Replicate(acknowledge: false));
        Assert.True(sender.SuppressedRecordCount > afterFirst, "nothing was suppressed");

        // Past the delay, an unacknowledged value is assumed lost and goes again.
        Assert.True(Replicate(acknowledge: false), "it was never re-sent");
    }

    [Fact]
    public void AValueThatChangedAgain_IsSentAtOnceDespiteTheDelay() {
        sender.ResendDelayTicks = 100;

        var entity = Spawn();
        Move(entity, 1f);
        Assert.True(Replicate(acknowledge: false));

        // A different value is not a repetition, so the timer has nothing to say about it.
        Move(entity, 2f);
        Assert.True(Replicate(acknowledge: false), "a changed value waited on the resend timer");
    }

    static bool TryRoundTrip(
        WireLane[] lanes,
        byte[] previous,
        byte[] current,
        out byte[] rebuilt,
        out int deltaBits
    ) {
        var deltaBuffer = new byte[(DeltaCodec.MaxBits(lanes) + 7) / 8];
        var was = new BitReader(previous);
        var now = new BitReader(current);
        var delta = new BitWriter(deltaBuffer);

        rebuilt = [];
        deltaBits = 0;

        if (!DeltaCodec.TryEncode(lanes, ref was, ref now, ref delta) || !delta.TryFinish(out var encoded)) {
            return false;
        }

        deltaBits = delta.BitsWritten;

        var rebuiltBuffer = new byte[current.Length];
        var baseline = new BitReader(previous);
        var reading = new BitReader(encoded);
        var writing = new BitWriter(rebuiltBuffer);

        if (!DeltaCodec.TryDecode(lanes, ref baseline, ref reading, ref writing) || !writing.TryFinish(out var bits)) {
            return false;
        }

        rebuilt = bits.ToArray();

        return true;
    }

    static WireLane[] RandomLanes(DeterministicRandom random) {
        var lanes = new WireLane[1 + (int)((uint)random.NextUInt64() % 8)];

        for (var i = 0; i < lanes.Length; i++) {
            lanes[i] = new($"lane{i}", 1 + (int)((uint)random.NextUInt64() % 32), (uint)random.NextUInt64() % 2 == 0);
        }

        return lanes;
    }

    static byte[] RandomEncoding(DeterministicRandom random, WireLane[] lanes) {
        var values = new uint[lanes.Length];

        for (var i = 0; i < lanes.Length; i++) {
            var span = lanes[i].Bits >= 32 ? uint.MaxValue : (1u << lanes[i].Bits) - 1;

            // Mostly near the previous value, sometimes anywhere: both paths through the selector
            // matter, and only drawing uniformly would almost never exercise the short ones.
            values[i] = (uint)random.NextUInt64() % 4 == 0 ? (uint)random.NextUInt64() & span : (uint)random.NextUInt64() % 64 & span;
        }

        return Encoding(lanes, values);
    }

    static byte[] Encoding(WireLane[] lanes, uint[] values) {
        var bytes = new byte[(DeltaCodec.TotalBits(lanes) + 7) / 8];
        var writer = new BitWriter(bytes);

        for (var i = 0; i < lanes.Length; i++) {
            writer.Write(values[i], lanes[i].Bits);
        }

        Assert.True(writer.TryFinish(out _));

        return bytes;
    }

    Entity Spawn() =>
        server.Create(
            ids.Next(),
            new NetworkTransform { Position = Vector3.Zero, Rotation = Quaternion.Identity }
        );

    void Move(Entity entity, float along) {
        ref var transform = ref server.Get<NetworkTransform>(entity);
        transform.Position = new(along, 0f, along * 0.5f);
        transform.Rotation = Quaternion.FromAxisAngle(Vector3.UnitY, along * 0.02f);
    }

    bool Replicate(bool deliver = true, bool acknowledge = true) {
        var at = Tick();
        sender.Capture(server, at);

        var wrote = sender.TryWriteSnapshot(server, Player, at, buffer, out var snapshot);

        if (wrote && deliver) {
            Assert.True(receiver.TryApply(client, snapshot));

            if (acknowledge) {
                sender.Acknowledge(Player, receiver.AppliedTick);
            }
        }

        server.AdvanceVersion();
        tick++;

        return wrote;
    }

    Tick Tick() => new(tick);

    void AssertAgrees(Entity entity) {
        var truth = server.Read<NetworkTransform>(entity);
        var id = server.Read<NetworkId>(entity);

        Assert.True(receiver.TryGetEntity(id, out var mirror));

        var held = client.Read<NetworkTransform>(mirror);

        Assert.True(
            Vector3.Distance(truth.Position, held.Position) <= NetworkTransformReplicator.PositionRange.MaxError * 2f,
            $"{truth.Position} against {held.Position}"
        );
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Net.Messaging;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Net.Tests.Replication;

/// <summary>Replication: that a client converges on the server, and what it costs to get there.</summary>
public sealed class ReplicationTests : IDisposable {
    static readonly PlayerId Player = new(1);
    static readonly PlayerId Other = new(2);

    readonly World server = new();
    readonly World client = new();
    readonly ReplicationRegistry registry = new();
    readonly NetworkIdAllocator ids = new();
    readonly byte[] buffer = new byte[8192];

    ReplicationServer sender;
    ReplicationClient receiver;
    uint tick;

    public ReplicationTests() {
        registry.Register(new PositionReplicator());
        registry.Register(new HealthReplicator());
        sender = new(registry);
        receiver = new(registry);
    }

    public void Dispose() {
        server.Dispose();
        client.Dispose();
    }

    [Fact]
    public void AClientConvergesOnTheServersState() {
        var first = Spawn(1, 2, 3, health: 50);
        var second = Spawn(-4, -5, -6, health: 10);

        Assert.True(Replicate(Player));

        Assert.Equal(2, receiver.EntityCount);
        AssertMirrors(first);
        AssertMirrors(second);
    }

    [Fact]
    public void NothingChanging_MeansNothingToSend() {
        Spawn(1, 2, 3, health: 50);
        Assert.True(Replicate(Player));

        // A tick in which nothing moved costs one thing: the decision not to send.
        Assert.False(Replicate(Player));
        Assert.False(Replicate(Player));
    }

    [Fact]
    public void OnlyWhatChangedIsSentAgain() {
        var entity = Spawn(1, 2, 3, health: 50);
        Assert.True(Replicate(Player));

        server.Set(entity, new ReplicatedHealth { Value = 25 });

        var size = ReplicateAndMeasure(Player);

        Assert.True(size > 0);
        Assert.Equal(25, client.Read<ReplicatedHealth>(Mirror(entity)).Value);

        // Health is a tick, an id, a type id and four bytes. If the position had gone too this would
        // be half as big again, and the whole of delta replication is that it did not.
        Assert.InRange(size, 1, 12);
    }

    [Fact]
    public void WithoutAnAcknowledgement_TheStateIsSentAgain() {
        var entity = Spawn(1, 2, 3, health: 50);
        sender.Capture(server);

        // Sent, and lost: no ack, so the server may not assume the client has it.
        Assert.True(sender.TryWriteSnapshot(server, Player, Tick(), buffer, out _));

        server.AdvanceVersion();
        server.Set(entity, new ReplicatedHealth { Value = 25 });
        sender.Capture(server);

        Assert.True(sender.TryWriteSnapshot(server, Player, Tick(), buffer, out var snapshot));
        Assert.True(receiver.TryApply(client, snapshot));

        // The position was in the lost packet and is in this one too — and the health arrives at its
        // current value rather than the one that was lost, which is both cheaper and more correct.
        AssertMirrors(entity);
        Assert.Equal(25, client.Read<ReplicatedHealth>(Mirror(entity)).Value);
    }

    [Fact]
    public void AnEntityThatLeavesInterest_IsRemovedFromTheClient() {
        var visible = new HashSet<uint>();
        sender = new(registry, new PickyResolver(visible));

        var first = Spawn(1, 2, 3, health: 50);
        var second = Spawn(4, 5, 6, health: 60);
        visible.Add(server.Read<NetworkId>(first).Value);
        visible.Add(server.Read<NetworkId>(second).Value);

        Assert.True(Replicate(Player));
        Assert.Equal(2, receiver.EntityCount);

        visible.Remove(server.Read<NetworkId>(second).Value);

        Assert.True(Replicate(Player));
        Assert.Equal(1, receiver.EntityCount);
        Assert.False(receiver.TryGetEntity(server.Read<NetworkId>(second), out _));

        // And it comes back whole when it comes back into view, rather than being remembered as
        // something the client already has.
        visible.Add(server.Read<NetworkId>(second).Value);
        Assert.True(Replicate(Player));
        AssertMirrors(second);
    }

    [Fact]
    public void AnEntityDestroyedOnTheServer_IsDestroyedOnTheClient() {
        var entity = Spawn(1, 2, 3, health: 50);
        Assert.True(Replicate(Player));

        var id = server.Read<NetworkId>(entity);
        server.Destroy(entity);
        sender.Despawn(id);

        Assert.True(Replicate(Player));
        Assert.Equal(0, receiver.EntityCount);
        Assert.False(receiver.TryGetEntity(id, out _));
    }

    [Fact]
    public void ARemovalIsRepeatedUntilItIsAcknowledged() {
        var visible = new HashSet<uint>();
        sender = new(registry, new PickyResolver(visible));

        var entity = Spawn(1, 2, 3, health: 50);
        var id = server.Read<NetworkId>(entity);
        visible.Add(id.Value);
        Assert.True(Replicate(Player));

        visible.Remove(id.Value);

        // The removal goes out and is lost. It has to go again, or the entity is a ghost that only
        // this player can see.
        Assert.True(sender.TryWriteSnapshot(server, Player, Tick(), buffer, out _));
        Assert.True(sender.TryWriteSnapshot(server, Player, Tick(), buffer, out var second));
        Assert.True(receiver.TryApply(client, second));
        Assert.Equal(0, receiver.EntityCount);

        // Acknowledged now, so it stops being repeated.
        sender.Acknowledge(Player, receiver.AppliedTick);
        Assert.False(sender.TryWriteSnapshot(server, Player, Tick(), buffer, out _));
    }

    [Fact]
    public void QuantizationCostsWhatItSaidItWould() {
        var entity = Spawn(123.456f, -78.9f, 0.001f, health: 1);
        Assert.True(Replicate(Player));

        var range = new QuantizeRange(-1000f, 1000f, 16);
        ref readonly var sent = ref server.Read<ReplicatedPosition>(entity);
        ref readonly var got = ref client.Read<ReplicatedPosition>(Mirror(entity));

        Assert.InRange(got.X, sent.X - range.MaxError, sent.X + range.MaxError);
        Assert.InRange(got.Y, sent.Y - range.MaxError, sent.Y + range.MaxError);
        Assert.InRange(got.Z, sent.Z - range.MaxError, sent.Z + range.MaxError);
        Assert.NotEqual(sent.X, got.X);
    }

    [Fact]
    public void TwoClientsAtDifferentPointsGetDifferentSnapshots() {
        var entity = Spawn(1, 2, 3, health: 50);

        Assert.True(Replicate(Player));

        // The other client has not been sent anything yet, so it gets the whole state while the
        // first gets nothing.
        sender.Capture(server);

        Assert.False(sender.TryWriteSnapshot(server, Player, Tick(), buffer, out _));
        Assert.True(sender.TryWriteSnapshot(server, Other, Tick(), buffer, out var snapshot));
        Assert.True(snapshot.Length > 4);
        Assert.Equal(0, sender.BaselineOf(Player).PendingTickCount);
    }

    [Fact]
    public void TheBudgetShedsRatherThanTruncating() {
        for (var i = 0; i < 200; i++) {
            Spawn(i, i, i, health: i);
        }

        sender.Budget = new() { BytesPerSnapshot = 64 };

        var ticks = 0;

        while (Replicate(Player)) {
            ticks++;

            Assert.True(ticks < 500, "Shedding should converge, not stall.");
        }

        // Two hundred entities, two components each, through a 64-byte window: it took a while, and
        // it got there, and nothing was lost on the way.
        Assert.True(ticks > 20);
        Assert.Equal(200, receiver.EntityCount);
    }

    [Fact]
    public void EverySnapshotStaysInsideTheBudget() {
        for (var i = 0; i < 50; i++) {
            Spawn(i, i, i, health: i);
        }

        sender.Budget = new() { BytesPerSnapshot = 100 };
        sender.Capture(server);

        var sent = 0;

        while (sender.TryWriteSnapshot(server, Player, Tick(), buffer, out var snapshot)) {
            Assert.InRange(snapshot.Length, 1, 101); // the terminating bit may round up one byte
            Assert.True(receiver.TryApply(client, snapshot));
            sender.Acknowledge(Player, receiver.AppliedTick);
            sent++;

            Assert.True(sent < 500, "Shedding should converge, not stall.");
        }

        Assert.Equal(50, receiver.EntityCount);
    }

    [Fact]
    public void ThePriorityDecidesWhatIsShedFirst() {
        for (var i = 0; i < 20; i++) {
            Spawn(i, i, i, health: i);
        }

        sender.Budget = new() { BytesPerSnapshot = 40 };
        sender.Capture(server);
        Assert.True(sender.TryWriteSnapshot(server, Player, Tick(), buffer, out var snapshot));
        Assert.True(receiver.TryApply(client, snapshot));

        // Position is priority 10 and health is 0, so a snapshot that cannot hold both holds
        // positions. Nothing has any health yet.
        var positions = 0;
        var healths = 0;

        foreach (var entity in Networked(client)) {
            if (client.Has<ReplicatedPosition>(entity)) {
                positions++;
            }

            if (client.Has<ReplicatedHealth>(entity)) {
                healths++;
            }
        }

        Assert.True(positions > 0);
        Assert.Equal(0, healths);
    }

    [Fact]
    public void ATypeIdTheRegistryDoesNotKnow_IsRefused() {
        var writer = new BitWriter(buffer);
        writer.WriteUInt32(7);
        writer.WriteBool(false); // no removals
        writer.WriteBool(true);
        writer.WriteVariable(1); // entity
        writer.WriteVariable(0xDEAD); // a type nobody registered
        writer.WriteUInt32(0);

        Assert.True(writer.TryFinish(out var snapshot));
        Assert.False(receiver.TryApply(client, snapshot));
        Assert.Equal(1, receiver.RejectedSnapshotCount);
        Assert.False(receiver.HasApplied);
    }

    [Fact]
    public void ATruncatedSnapshotIsRefusedRatherThanHalfBelieved() {
        Spawn(1, 2, 3, health: 50);
        sender.Capture(server);

        Assert.True(sender.TryWriteSnapshot(server, Player, Tick(), buffer, out var snapshot));

        Assert.False(receiver.TryApply(client, snapshot[..(snapshot.Length / 2)]));
        Assert.Equal(1, receiver.RejectedSnapshotCount);

        // Not acknowledged, so the server has not advanced its baseline and sends it all again.
        Assert.True(sender.TryWriteSnapshot(server, Player, Tick(), buffer, out var again));
        Assert.True(receiver.TryApply(client, again));
    }

    [Fact]
    public void TwoTypesThatHashTheSame_AreRefusedAtRegistration() {
        var collided = new ReplicationRegistry();
        collided.Register(new PositionReplicator());

        var duplicate = Assert.Throws<ArgumentException>(() => collided.Register(new PositionReplicator()));

        Assert.Contains("registered twice", duplicate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheManifestIsOrderedByIdRatherThanByRegistrationOrder() {
        var forwards = new ReplicationRegistry();
        forwards.Register(new PositionReplicator());
        forwards.Register(new HealthReplicator());

        var backwards = new ReplicationRegistry();
        backwards.Register(new HealthReplicator());
        backwards.Register(new PositionReplicator());

        // Two builds cannot promise the same start-up order across assemblies, so the wire order is
        // the hash order and the manifest hash is the same either way.
        Assert.Equal(forwards.ManifestHash, backwards.ManifestHash);

        var position = ReplicationRegistry.HashTypeName(typeof(ReplicatedPosition).FullName!);

        Assert.Equal(forwards.IndexOf(position), backwards.IndexOf(position));
        Assert.NotEqual(-1, forwards.IndexOf(position));
        Assert.Equal(-1, forwards.IndexOf(0xDEAD));
    }

    [Fact]
    public void AManifestWithADifferentSetOfTypes_HashesDifferently() {
        var full = new ReplicationRegistry();
        full.Register(new PositionReplicator());
        full.Register(new HealthReplicator());

        var partial = new ReplicationRegistry();
        partial.Register(new PositionReplicator());

        Assert.NotEqual(full.ManifestHash, partial.ManifestHash);
    }

    [Fact]
    public void AWireIdIsAHashOfTheNameAndIsNeverZero() {
        var position = ReplicationRegistry.HashTypeName(typeof(ReplicatedPosition).FullName!);

        Assert.Equal(position, ReplicationRegistry.HashTypeName(typeof(ReplicatedPosition).FullName!));
        Assert.NotEqual(position, ReplicationRegistry.HashTypeName(typeof(ReplicatedHealth).FullName!));
        Assert.NotEqual(0u, position);
    }

    [Fact]
    public void AnAcknowledgementOlderThanTheLastOne_ChangesNothing() {
        var baseline = new ConnectionBaseline();
        var key = new BaselineKey(new(1), 42);

        baseline.RecordSent(new(10), key, 99, new(10));
        Assert.True(baseline.Acknowledge(new(10)));
        Assert.True(baseline.IsCurrent(key, 99));

        // Acks come on an unreliable channel and may arrive out of order. An older one says nothing.
        Assert.False(baseline.Acknowledge(new(5)));
        Assert.Equal(new Tick(10), baseline.AcknowledgedTick);
    }

    [Fact]
    public void UnacknowledgedTicksAreBounded() {
        var baseline = new ConnectionBaseline();

        for (var i = 0u; i < ConnectionBaseline.MaxPendingTicks * 2; i++) {
            baseline.RecordSent(new(i), new(new(1), i), i, new(i));
        }

        Assert.Equal(ConnectionBaseline.MaxPendingTicks, baseline.PendingTickCount);
        Assert.Equal(0, baseline.BaselineCount);
    }

    Entity Spawn(float x, float y, float z, int health) =>
        server.Create(
            ids.Next(),
            new ReplicatedPosition { X = x, Y = y, Z = z },
            new ReplicatedHealth { Value = health }
        );

    Tick Tick() => new(tick++);

    bool Replicate(PlayerId player) {
        // Capture, then close the tick. Advancing between a write and its capture is what loses an
        // update silently, so the harness does it in the order the scheduler does.
        sender.Capture(server);
        var wrote = sender.TryWriteSnapshot(server, player, Tick(), buffer, out var snapshot);

        if (wrote) {
            Assert.True(receiver.TryApply(client, snapshot));
            sender.Acknowledge(player, receiver.AppliedTick);
        }

        server.AdvanceVersion();

        return wrote;
    }

    int ReplicateAndMeasure(PlayerId player) {
        sender.Capture(server);
        var length = 0;

        if (sender.TryWriteSnapshot(server, player, Tick(), buffer, out var snapshot)) {
            Assert.True(receiver.TryApply(client, snapshot));
            sender.Acknowledge(player, receiver.AppliedTick);
            length = snapshot.Length;
        }

        server.AdvanceVersion();

        return length;
    }

    Entity Mirror(Entity serverEntity) {
        Assert.True(receiver.TryGetEntity(server.Read<NetworkId>(serverEntity), out var mirrored));

        return mirrored;
    }

    void AssertMirrors(Entity serverEntity) {
        var mirrored = Mirror(serverEntity);
        var range = new QuantizeRange(-1000f, 1000f, 16);

        ref readonly var sent = ref server.Read<ReplicatedPosition>(serverEntity);
        ref readonly var got = ref client.Read<ReplicatedPosition>(mirrored);

        Assert.InRange(got.X, sent.X - range.MaxError, sent.X + range.MaxError);
        Assert.InRange(got.Y, sent.Y - range.MaxError, sent.Y + range.MaxError);
        Assert.InRange(got.Z, sent.Z - range.MaxError, sent.Z + range.MaxError);
        Assert.Equal(server.Read<ReplicatedHealth>(serverEntity).Value, client.Read<ReplicatedHealth>(mirrored).Value);
    }

    static List<Entity> Networked(World world) {
        var found = new List<Entity>();
        var description = new QueryDescription().RequireAll([ComponentType<NetworkId>.Id]);

        foreach (var chunk in world.Chunks(description)) {
            found.AddRange(chunk.Entities);
        }

        return found;
    }

    /// <summary>An interest resolver a test drives by hand.</summary>
    sealed class PickyResolver(HashSet<uint> visible) : IInterestResolver {
        public void Resolve(World world, PlayerId player, List<Entity> observed) {
            foreach (var entity in Networked(world)) {
                if (visible.Contains(world.Read<NetworkId>(entity).Value)) {
                    observed.Add(entity);
                }
            }
        }
    }
}

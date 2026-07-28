// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Behaviors;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Net.Engine.Tests;

/// <summary>A behaviour with lists rather than fields, which is a different record.</summary>
public sealed class InventoryBehaviour : NetworkBehaviour {
    public SyncList<int> Items { get; }

    public SyncList<int> Tags { get; }

    public InventoryBehaviour() {
        Items = DeclareList(new SyncList<int>(), nameof(Items));
        Tags = DeclareList(new SyncList<int>(), nameof(Tags));
    }

    /// <summary>Declares one too late, which is the mistake the guard is there for.</summary>
    public void DeclareLate() => DeclareList(new SyncList<int>(), "Late");

    /// <inheritdoc />
    protected override NetworkModule Build() => new Empty();

    sealed class Empty : NetworkModule;
}

/// <summary>SyncList on the wire, which it was not until now.</summary>
public sealed class SyncListReplicationTests : IDisposable {
    static readonly PlayerId Player = new(1);
    static readonly PlayerId Latecomer = new(2);

    readonly World server = new("list-server");
    readonly World client = new("list-client");
    readonly BehaviorStore serverStore;
    readonly BehaviorStore clientStore;
    readonly ReplicationServer sender;
    readonly ReplicationClient receiver;
    readonly NetworkIdAllocator ids = new();
    readonly byte[] buffer = new byte[8192];

    uint tick = 1;

    public SyncListReplicationTests() {
        serverStore = new(server);
        clientStore = new(client);

        var serverRegistry = new ReplicationRegistry();
        serverRegistry.Register(new SyncListReplicator<InventoryBehaviour>(serverStore));
        sender = new(serverRegistry);

        var clientRegistry = new ReplicationRegistry();
        clientRegistry.Register(new SyncListReplicator<InventoryBehaviour>(clientStore));
        receiver = new(clientRegistry);
    }

    public void Dispose() {
        server.Dispose();
        client.Dispose();
    }

    /// <summary>A list a server changed turns up on a client.</summary>
    /// <remarks>
    ///     The thing that did not happen before this. The op log was built and tested and nothing
    ///     carried it, so a <c>SyncList</c> was a local collection with a wire format nobody called.
    /// </remarks>
    [Fact]
    public void AListReachesAClient() {
        var (entity, behaviour) = Spawn();

        behaviour.Items.Add(7);
        behaviour.Items.Add(9);
        behaviour.Tags.Add(1);
        behaviour.MarkListsChanged();

        Assert.True(Replicate(Player));

        var mirrored = Mirror(entity);

        Assert.Equal([7, 9], mirrored.Items);
        Assert.Equal([1], mirrored.Tags);
    }

    /// <summary>Every kind of change ends with the two lists agreeing.</summary>
    /// <remarks>
    ///     Insert and remove are the two the op log exists for and the two that whole-list sending
    ///     makes trivial — an insert at the front shifts every element, which is exactly why
    ///     differencing a list lane by lane would be wrong.
    /// </remarks>
    [Fact]
    public void EveryKindOfChangeConverges() {
        var (entity, behaviour) = Spawn();

        behaviour.Items.Add(1);
        behaviour.Items.Add(2);
        behaviour.Items.Add(3);
        behaviour.MarkListsChanged();
        Assert.True(Replicate(Player));

        behaviour.Items.Insert(0, 0);
        behaviour.Items.RemoveAt(3);
        behaviour.Items.Replace(1, 99);
        behaviour.MarkListsChanged();
        Assert.True(Replicate(Player));

        Assert.Equal([0, 99, 2], behaviour.Items);
        Assert.Equal([0, 99, 2], Mirror(entity).Items);

        behaviour.Items.Clear();
        behaviour.MarkListsChanged();
        Assert.True(Replicate(Player));

        Assert.Empty(Mirror(entity).Items);
    }

    /// <summary>A player who was not there gets the list, not the last thing that happened to it.</summary>
    /// <remarks>
    ///     <para>
    ///         The case that decided the design. This package used to say a list's <i>operations</i>
    ///         travel, and that the reliable channel's ordering makes per-connection bookkeeping
    ///         unnecessary because everyone receives every op exactly once. That is true of a
    ///         broadcast and false of a snapshot: a snapshot goes to the connections an interest
    ///         resolver returns, so somebody who was not observing has received nothing at all.
    ///     </para>
    ///     <para>
    ///         Sending the state makes a late joiner, a reconnect, a lost snapshot and an object
    ///         crossing an interest boundary the same case — here is the list — which is why nothing
    ///         had to be added to the wire for it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ALateJoinerGetsTheListRatherThanTheLastOp() {
        var (entity, behaviour) = Spawn();

        for (var item = 1; item <= 5; item++) {
            behaviour.Items.Add(item);
            behaviour.MarkListsChanged();
            Assert.True(Replicate(Player));
        }

        // Somebody who has seen none of that, arriving five ops in.
        Assert.True(Replicate(Latecomer));

        Assert.Equal([1, 2, 3, 4, 5], Mirror(entity).Items);
    }

    /// <summary>A list that did not change costs nothing.</summary>
    /// <remarks>
    ///     The record is compared by the hash of its bits like every other, so a list re-encoded to
    ///     the same bytes is a record the connection already has. What makes this worth asserting is
    ///     that whole-list sending would otherwise be a whole list every tick.
    /// </remarks>
    [Fact]
    public void AListThatDidNotChangeIsNotSentAgain() {
        var (_, behaviour) = Spawn();

        behaviour.Items.Add(1);
        behaviour.MarkListsChanged();
        Assert.True(Replicate(Player));

        // Marked as changed without anything changing — the sync system is a counter, not a diff.
        behaviour.MarkListsChanged();
        Assert.False(Replicate(Player));
        Assert.False(Replicate(Player));
    }

    /// <summary>The lists are read in the order they were declared, which is the whole wire format.</summary>
    /// <remarks>
    ///     Nothing about a list's identity is sent: the record's type index names the behaviour and
    ///     its lists are a property of the type. So the second list's contents must not land in the
    ///     first, which is what this asserts by giving them different lengths.
    /// </remarks>
    [Fact]
    public void ListsAreReadInDeclarationOrder() {
        var (entity, behaviour) = Spawn();

        behaviour.Items.Add(11);
        behaviour.Tags.Add(21);
        behaviour.Tags.Add(22);
        behaviour.Tags.Add(23);
        behaviour.MarkListsChanged();

        Assert.True(Replicate(Player));

        var mirrored = Mirror(entity);

        Assert.Equal([11], mirrored.Items);
        Assert.Equal([21, 22, 23], mirrored.Tags);
    }

    /// <summary>A list declared after the behaviour is attached is refused.</summary>
    /// <remarks>
    ///     It would shift every list after it, and both ends would read each other's — the same
    ///     failure a <c>SyncVar</c> declared late produces, refused in the same way.
    /// </remarks>
    [Fact]
    public void AListDeclaredAfterAttachingIsRefused() {
        var (_, behaviour) = Spawn();

        Assert.Throws<InvalidOperationException>(() => behaviour.DeclareLate());
    }

    (Entity Entity, InventoryBehaviour Behaviour) Spawn() {
        var entity = server.Create(ids.Next(), new SyncListVersion());
        var behaviour = serverStore.Add<InventoryBehaviour>(entity);

        return (entity, behaviour);
    }

    InventoryBehaviour Mirror(Entity entity) {
        Assert.True(receiver.TryGetEntity(server.Read<NetworkId>(entity), out var mirrored));

        var behaviour = clientStore.Get<InventoryBehaviour>(mirrored);
        Assert.NotNull(behaviour);

        return behaviour;
    }

    bool Replicate(PlayerId player) {
        sender.Capture(server);
        var wrote = sender.TryWriteSnapshot(server, player, new(tick++), buffer, out var snapshot);

        if (wrote) {
            Assert.True(receiver.TryApply(client, snapshot));
            sender.Acknowledge(player, receiver.AppliedTick);
        }

        server.AdvanceVersion();

        return wrote;
    }
}

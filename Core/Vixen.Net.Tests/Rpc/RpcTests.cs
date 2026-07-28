// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Messaging;
using Vixen.Net.Replication;
using Vixen.Net.Rpc;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Net.Tests.Rpc;

/// <summary>The RPC router: what it sends, what it refuses, and what it refuses to send.</summary>
public sealed class RpcTests {
    static readonly PlayerId Owner = new(1);
    static readonly PlayerId Stranger = new(2);
    static readonly NetworkId Object = new(10);

    readonly RpcManifest manifest = new();
    readonly RecordingTransport transport = new();
    readonly RpcRouter router;
    readonly Turret turret;

    public RpcTests() {
        manifest.Register(Turret.RpcMethodTable);
        router = new(manifest, transport, RpcRole.Server);
        turret = new(Object, router);
        router.Register(Object, turret);
        router.Ownership.SetOwner(Object, Owner);
    }

    [Fact]
    public void AServerCallFromItsOwnerRuns() {
        var sent = Send(Turret.Fire, (ref BitWriter writer) => writer.WriteInt32(7));

        Assert.True(router.Receive(Owner, sent));
        Assert.Equal([7], turret.Fired);
        Assert.Equal(Owner, turret.LastSender);
        Assert.Equal(1, router.AcceptedCount);
    }

    [Fact]
    public void AServerCallFromSomebodyElse_IsRefused() {
        var sent = Send(Turret.Fire, (ref BitWriter writer) => writer.WriteInt32(7));

        Assert.False(router.Receive(Stranger, sent));
        Assert.Empty(turret.Fired);
        Assert.Equal(1, router.RefusedByOwnershipCount);
    }

    [Fact]
    public void ACallThatDoesNotAskForOwnership_TakesAnybody() {
        var sent = Send(Turret.Chat, (ref BitWriter writer) => writer.WriteVariable(42));

        Assert.True(router.Receive(Stranger, sent));
        Assert.Equal([42u], turret.Chatted);
    }

    [Fact]
    public void AServerCallArrivingAtAClient_IsRefused() {
        var client = new RpcRouter(manifest, transport, RpcRole.Client);
        client.Register(Object, turret);

        var sent = Send(Turret.Fire, (ref BitWriter writer) => writer.WriteInt32(7));

        // Wrong way round, and the packet says nothing about that — the direction is the manifest's
        // and the role is ours.
        Assert.False(client.Receive(Owner, sent));
        Assert.Equal(1, client.RefusedByDirectionCount);
    }

    [Fact]
    public void AClientCallArrivingWithASenderIsRefused() {
        var client = new RpcRouter(manifest, transport, RpcRole.Client);
        client.Register(Object, turret);

        var sent = Send(Turret.Explode, (ref BitWriter writer) => writer.WriteSingle(1.5f));

        // A client call comes from the server, which is not a player. One that claims to be from a
        // player came from a player, and a player is not the server.
        Assert.False(client.Receive(Stranger, sent));
        Assert.True(client.Receive(PlayerId.None, sent));
        Assert.Equal([1.5f], turret.Exploded);
    }

    [Fact]
    public void AClientCallGoesToTheOwnerWhenThatIsWhatItAsksFor() {
        var writer = router.BeginCall(Turret.Reload, Object);
        Assert.True(router.EndCall(Turret.Reload, Object, ref writer));

        var packet = Assert.Single(transport.ToPlayer);

        Assert.Equal(Owner, packet.Player);
        Assert.Equal(Channel.Reliable, packet.Channel);
        Assert.Empty(transport.ToAll);
    }

    [Fact]
    public void AClientCallWithNoObserverSourceGoesToEverybody() {
        var writer = router.BeginCall(Turret.Explode, Object);
        writer.WriteSingle(2f);

        Assert.True(router.EndCall(Turret.Explode, Object, ref writer));
        Assert.Single(transport.ToAll);
    }

    [Fact]
    public void AClientCallGoesToTheObserversWhenThereAreSome() {
        var observers = new FixedObservers([Owner, Stranger]);
        var server = new RpcRouter(manifest, transport, RpcRole.Server, router.Ownership, observers);

        var writer = server.BeginCall(Turret.Explode, Object);
        writer.WriteSingle(2f);

        Assert.True(server.EndCall(Turret.Explode, Object, ref writer));
        Assert.Equal(2, transport.ToPlayer.Count);
        Assert.Empty(transport.ToAll);
    }

    [Fact]
    public void AClientRefusesToSendAClientCall() {
        var client = new RpcRouter(manifest, transport, RpcRole.Client);
        var writer = client.BeginCall(Turret.Explode, Object);
        writer.WriteSingle(1f);

        Assert.False(client.EndCall(Turret.Explode, Object, ref writer));
        Assert.Equal(1, client.RefusedToSendCount);
        Assert.Empty(transport.ToAll);
    }

    [Fact]
    public void AServerRefusesToSendAServerCall() {
        var writer = router.BeginCall(Turret.Fire, Object);
        writer.WriteInt32(1);

        // A dedicated server calling its own server RPC is a mistake worth noticing rather than
        // quietly becoming a local method call.
        Assert.False(router.EndCall(Turret.Fire, Object, ref writer));
        Assert.Equal(1, router.RefusedToSendCount);
    }

    [Fact]
    public void AHostSendsBothWays() {
        var host = new RpcRouter(manifest, transport, RpcRole.Host, router.Ownership);
        var server = host.BeginCall(Turret.Explode, Object);
        server.WriteSingle(1f);

        Assert.True(host.EndCall(Turret.Explode, Object, ref server));

        var client = host.BeginCall(Turret.Fire, Object);
        client.WriteInt32(2);

        Assert.True(host.EndCall(Turret.Fire, Object, ref client));
        Assert.Single(transport.ToAll);
        Assert.Single(transport.ToServer);
    }

    [Fact]
    public void AnIndexThatNamesNothing_IsRefused() {
        Span<byte> buffer = stackalloc byte[16];
        var writer = new BitWriter(buffer);
        writer.WriteVariable(Object.Value);
        writer.WriteVariable(99); // no such type
        writer.WriteVariable(0);

        Assert.True(writer.TryFinish(out var packet));
        Assert.False(router.Receive(Owner, packet));
        Assert.Equal(1, router.RefusedByManifestCount);
    }

    [Fact]
    public void ACallAboutAnObjectNobodyRegistered_IsRefused() {
        var writer = router.BeginCall(Turret.Chat, new(999));
        writer.WriteVariable(1);

        Assert.True(writer.TryFinish(out var packet));
        Assert.False(router.Receive(Stranger, packet));
        Assert.Equal(1, router.RefusedByUnknownObjectCount);
    }

    [Fact]
    public void ArgumentsThatDoNotDecode_AreRefused() {
        var sent = Send(Turret.Fire, (ref BitWriter writer) => { });

        Assert.False(router.Receive(Owner, sent));
        Assert.Empty(turret.Fired);
        Assert.Equal(1, router.RefusedByArgumentsCount);
    }

    [Fact]
    public void ArgumentsWithBitsLeftOver_AreRefused() {
        var sent = Send(
            Turret.Fire,
            (ref BitWriter writer) => {
                writer.WriteInt32(7);
                writer.WriteInt32(8); // one argument too many
            }
        );

        Assert.False(router.Receive(Owner, sent));
        Assert.Equal(1, router.RefusedByArgumentsCount);
    }

    [Fact]
    public void ACallerMakingTooManyCalls_IsCutOff() {
        var limited = new RpcRouter(
            manifest,
            transport,
            RpcRole.Server,
            router.Ownership,
            limits: new() { CallsPerSecond = 10, Burst = 3 }
        );

        limited.Register(Object, turret);
        var sent = Send(Turret.Fire, (ref BitWriter writer) => writer.WriteInt32(1)).ToArray();

        Assert.True(limited.Receive(Owner, sent));
        Assert.True(limited.Receive(Owner, sent));
        Assert.True(limited.Receive(Owner, sent));
        Assert.False(limited.Receive(Owner, sent));
        Assert.Equal(1, limited.RefusedByRateLimitCount);

        // And it comes back at the rate it said, not all at once: ten a second means one and a half
        // tokens for a hundred and fifty milliseconds, which buys one call and not two.
        limited.Advance(TimeSpan.FromSeconds(0.15));

        Assert.True(limited.Receive(Owner, sent));
        Assert.False(limited.Receive(Owner, sent));
    }

    [Fact]
    public void ForgettingAnObjectDropsItsHandlersAndItsOwner() {
        router.Forget(Object);

        var sent = Send(Turret.Chat, (ref BitWriter writer) => writer.WriteVariable(1));

        Assert.False(router.Receive(Stranger, sent));
        Assert.Equal(0, router.RegisteredCount);
        Assert.False(router.Ownership.TryGetOwner(Object, out _));
    }

    [Fact]
    public void OwnershipTransfersAreEvents() {
        var changes = new List<(NetworkId Id, PlayerId From, PlayerId To)>();
        var ownership = new NetworkOwnership();
        ownership.OwnerChanged += (id, from, to) => changes.Add((id, from, to));

        Assert.True(ownership.SetOwner(Object, Owner));
        Assert.False(ownership.SetOwner(Object, Owner));
        Assert.True(ownership.SetOwner(Object, Stranger));
        Assert.True(ownership.IsOwnedBy(Object, Stranger));

        Assert.Equal(
            [(Object, PlayerId.None, Owner), (Object, Owner, Stranger)],
            changes
        );
    }

    [Fact]
    public void WhenAPlayerLeavesTheirObjectsGoBackToTheServer() {
        var ownership = new NetworkOwnership();
        ownership.SetOwner(new(1), Owner);
        ownership.SetOwner(new(2), Owner);
        ownership.SetOwner(new(3), Stranger);

        // Not to nobody-in-particular: an object owned by a player who is gone obeys nothing, where
        // one the server holds still obeys the server.
        Assert.Equal(2, ownership.TransferAll(Owner));
        Assert.Equal(1, ownership.Count);
        Assert.True(ownership.IsOwnedBy(new(3), Stranger));
    }

    [Fact]
    public void AManifestRefusesATableThatIsNotOrdered() {
        var out_of_order = new RpcManifest();

        var table = new[] {
            new RpcMethod("Thing", "B()", RpcKind.Server, false, Channel.Reliable, RpcTarget.Observers),
            new RpcMethod("Thing", "A()", RpcKind.Server, false, Channel.Reliable, RpcTarget.Observers)
        };

        Array.Sort(table, (left, right) => right.MethodId.CompareTo(left.MethodId));

        Assert.Throws<ArgumentException>(() => out_of_order.Register(table));
    }

    [Fact]
    public void AManifestRefusesTheSameTypeTwice() {
        var twice = new RpcManifest();
        twice.Register(Turret.RpcMethodTable);

        Assert.Throws<ArgumentException>(() => twice.Register(Turret.RpcMethodTable));
    }

    [Fact]
    public void AManifestHashesTheSameWhateverOrderTypesRegisterIn() {
        var first = new RpcManifest();
        var second = new RpcManifest();

        var other = new[] {
            new RpcMethod("Other", "Ping()", RpcKind.Server, false, Channel.Reliable, RpcTarget.Observers)
        };

        first.Register(Turret.RpcMethodTable);
        first.Register(other);

        second.Register(other);
        second.Register(Turret.RpcMethodTable);

        Assert.Equal(first.ManifestHash, second.ManifestHash);
        Assert.Equal(first.IndexOf(Turret.RpcMethodTable[0].TypeId), second.IndexOf(Turret.RpcMethodTable[0].TypeId));
        Assert.Equal(2, first.TypeCount);
        Assert.Equal(Turret.RpcMethodTable.Length + 1, first.MethodCount);
    }

    [Fact]
    public void TheTwoRegistersHashTheSameWay() {
        // Replication and RPC both need a stable id for a name, and both compute it. This is what
        // keeps them one function.
        Assert.Equal(ReplicationRegistry.HashTypeName("Some.Type"), RpcMethod.Hash("Some.Type"));
    }

    delegate void WriteArguments(ref BitWriter writer);

    ReadOnlySpan<byte> Send(RpcMethod method, WriteArguments arguments) {
        var writer = router.BeginCall(method, Object);
        arguments(ref writer);

        Assert.True(writer.TryFinish(out var packet));

        return packet;
    }

    /// <summary>Records what was sent instead of sending it.</summary>
    sealed class RecordingTransport : IRpcTransport {
        public List<byte[]> ToServer { get; } = [];

        public List<(PlayerId Player, byte[] Payload, Channel Channel)> ToPlayer { get; } = [];

        public List<byte[]> ToAll { get; } = [];

        public void SendToServer(ReadOnlySpan<byte> payload, Channel channel) => ToServer.Add(payload.ToArray());

        public void SendToPlayer(PlayerId player, ReadOnlySpan<byte> payload, Channel channel) =>
            ToPlayer.Add((player, payload.ToArray(), channel));

        public void SendToAll(ReadOnlySpan<byte> payload, Channel channel) => ToAll.Add(payload.ToArray());
    }

    /// <summary>An observer source a test drives by hand.</summary>
    sealed class FixedObservers(PlayerId[] players) : IRpcObservers {
        public void Resolve(NetworkId target, List<PlayerId> into) => into.AddRange(players);
    }

    /// <summary>
    ///     What the generator emits for a type with four calls, written by hand.
    /// </summary>
    /// <remarks>
    ///     The table is ordered by method id, which is what the manifest insists on and what the
    ///     generator does at build time.
    /// </remarks>
    sealed class Turret(NetworkId id, RpcRouter router) : IRpcObject, IRpcInvoker {
        public static readonly RpcMethod[] RpcMethodTable = Order(
            [
                new("Vixen.Net.Tests.Rpc.Turret", "Fire(int)", RpcKind.Server, true, Channel.Reliable, RpcTarget.Observers),
                new("Vixen.Net.Tests.Rpc.Turret", "Chat(uint)", RpcKind.Server, false, Channel.Reliable, RpcTarget.Observers),
                new("Vixen.Net.Tests.Rpc.Turret", "Explode(float)", RpcKind.Client, false, Channel.Unreliable, RpcTarget.Observers),
                new("Vixen.Net.Tests.Rpc.Turret", "Reload()", RpcKind.Client, false, Channel.Reliable, RpcTarget.Owner)
            ]
        );

        public static RpcMethod Fire => Find("Fire(int)");

        public static RpcMethod Chat => Find("Chat(uint)");

        public static RpcMethod Explode => Find("Explode(float)");

        public static RpcMethod Reload => Find("Reload()");

        public List<int> Fired { get; } = [];

        public List<uint> Chatted { get; } = [];

        public List<float> Exploded { get; } = [];

        public PlayerId LastSender { get; private set; }

        public NetworkId NetworkId => id;

        public RpcRouter? RpcRouter => router;

        public uint RpcTypeId => RpcMethodTable[0].TypeId;

        public bool Invoke(uint methodIndex, in RpcContext context, ref BitReader reader) {
            LastSender = context.Sender;
            var method = RpcMethodTable[(int)methodIndex];

            switch (method.Signature) {
                case "Fire(int)":
                    if (!reader.TryReadInt32(out var damage)) {
                        return false;
                    }

                    Fired.Add(damage);

                    return true;

                case "Chat(uint)":
                    if (!reader.TryReadVariable(out var line)) {
                        return false;
                    }

                    Chatted.Add(line);

                    return true;

                case "Explode(float)":
                    if (!reader.TryReadSingle(out var force)) {
                        return false;
                    }

                    Exploded.Add(force);

                    return true;

                default:
                    return true;
            }
        }

        static RpcMethod Find(string signature) {
            foreach (var method in RpcMethodTable) {
                if (method.Signature == signature) {
                    return method;
                }
            }

            throw new InvalidOperationException(signature);
        }

        static RpcMethod[] Order(RpcMethod[] methods) {
            Array.Sort(methods, (left, right) => left.MethodId.CompareTo(right.MethodId));

            return methods;
        }
    }
}

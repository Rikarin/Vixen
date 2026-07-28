// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Messaging;
using Vixen.Net.Replication;
using Vixen.Net.Rpc;
using Vixen.Net.Sessions;
using Vixen.Net.Transport.Local;
using Xunit;

namespace Vixen.Net.Tests.Rpc;

/// <summary>A call made on a client, over a real session, running on the server.</summary>
/// <remarks>
///     The transport tests prove the wire, the session tests prove the handshake and the router tests
///     prove the checks. This is the one that proves they join up, which is the thing none of them
///     can say on its own.
/// </remarks>
public sealed class RpcOverSessionTests : IDisposable {
    static readonly NetworkId Object = new(3);
    static readonly TimeSpan Step = TimeSpan.FromMilliseconds(16);

    readonly LocalNetwork network = new();
    readonly NetworkSession serverSession;
    readonly NetworkSession clientSession;
    readonly RpcManifest manifest = new();
    readonly RpcRouter serverRouter;
    readonly RpcRouter clientRouter;
    readonly Counter serverCounter;
    readonly Counter clientCounter;

    public RpcOverSessionTests() {
        manifest.Register(Counter.RpcMethodTable);

        serverSession = new(new LocalTransport(network), ownsTransport: true);
        clientSession = new(new LocalTransport(network), ownsTransport: true);

        serverRouter = new(manifest, new SessionRpcTransport(serverSession), RpcRole.Server);
        clientRouter = new(manifest, new SessionRpcTransport(clientSession), RpcRole.Client);

        serverCounter = new(Object, serverRouter);
        clientCounter = new(Object, clientRouter);
        serverRouter.Register(Object, serverCounter);
        clientRouter.Register(Object, clientCounter);

        serverSession.StartServer();
        clientSession.StartClient();
        Pump();
    }

    public void Dispose() {
        serverSession.Dispose();
        clientSession.Dispose();
    }

    [Fact]
    public void ACallMadeOnTheClientRunsOnTheServer() {
        serverRouter.Ownership.SetOwner(Object, clientSession.LocalPlayer!.Id);

        clientCounter.Rpc.Add(5);
        Pump();

        Assert.Equal(5, serverCounter.Total);
        Assert.Equal(0, clientCounter.Total);
        Assert.Equal(1, serverRouter.AcceptedCount);
    }

    [Fact]
    public void ACallMadeOnTheServerRunsOnTheClient() {
        serverCounter.Rpc.Announce(9);
        Pump();

        Assert.Equal(9, clientCounter.Announced);
        Assert.Equal(1, clientRouter.AcceptedCount);
    }

    [Fact]
    public void ACallFromAPlayerWhoDoesNotOwnTheObject_NeverRuns() {
        // Nobody owns it, so nobody may call anything that asks for ownership — including the only
        // player connected.
        clientCounter.Rpc.Add(5);
        Pump();

        Assert.Equal(0, serverCounter.Total);
        Assert.Equal(1, serverRouter.RefusedByOwnershipCount);
    }

    [Fact]
    public void AGamesOwnPayloadsAreNotMistakenForCalls() {
        var recorder = new PayloadRecorder(serverRouter);

        clientSession.SendToServer(Marked(PayloadKind.Game, [1, 2, 3]), Channel.Reliable);
        Pump(recorder);

        Assert.Equal([[1, 2, 3]], recorder.Game);
        Assert.Equal(0, serverRouter.AcceptedCount);
        Assert.Equal(0, serverRouter.RefusedByManifestCount);
    }

    static byte[] Marked(PayloadKind kind, byte[] payload) {
        var buffer = new byte[payload.Length + 1];

        Assert.True(NetworkPayload.TryWrap(kind, payload, buffer, out _));

        return buffer;
    }

    void Pump(PayloadRecorder? recorder = null) {
        var server = recorder ?? new PayloadRecorder(serverRouter);
        var client = new PayloadRecorder(clientRouter);

        for (var round = 0; round < 6; round++) {
            serverSession.Update(Step, server);
            clientSession.Update(Step, client);
        }
    }

    /// <summary>Sends what is marked as a call to the router, and keeps the rest.</summary>
    /// <remarks>What a game's own message handler does, in the three lines it takes.</remarks>
    sealed class PayloadRecorder(RpcRouter router) : ISessionMessageHandler {
        public List<byte[]> Game { get; } = [];

        public void OnMessage(PlayerId from, Channel channel, ReadOnlySpan<byte> payload) {
            if (!NetworkPayload.TryUnwrap(payload, out var kind, out var inner)) {
                return;
            }

            if (kind == PayloadKind.Rpc) {
                router.Receive(from, inner);

                return;
            }

            Game.Add(inner.ToArray());
        }
    }

    /// <summary>What the generator emits, written by hand — see the generator's own tests.</summary>
    sealed class Counter(NetworkId id, RpcRouter router) : IRpcObject, IRpcInvoker {
        public static readonly RpcMethod[] RpcMethodTable = Order(
            [
                new("Vixen.Net.Tests.Rpc.Counter", "Add(int)", RpcKind.Server, true, Channel.Reliable, RpcTarget.Observers),
                new("Vixen.Net.Tests.Rpc.Counter", "Announce(int)", RpcKind.Client, false, Channel.Reliable, RpcTarget.All)
            ]
        );

        public int Total { get; private set; }

        public int Announced { get; private set; }

        public NetworkId NetworkId => id;

        public RpcRouter? RpcRouter => router;

        public uint RpcTypeId => RpcMethodTable[0].TypeId;

        public Senders Rpc => new(this);

        public bool Invoke(uint methodIndex, in RpcContext context, ref BitReader reader) {
            if (!reader.TryReadInt32(out var value)) {
                return false;
            }

            if (RpcMethodTable[(int)methodIndex].Signature == "Add(int)") {
                Total += value;
            } else {
                Announced = value;
            }

            return true;
        }

        static RpcMethod[] Order(RpcMethod[] methods) {
            Array.Sort(methods, (left, right) => left.MethodId.CompareTo(right.MethodId));

            return methods;
        }

        static RpcMethod Find(string signature) {
            foreach (var method in RpcMethodTable) {
                if (method.Signature == signature) {
                    return method;
                }
            }

            throw new InvalidOperationException(signature);
        }

        public readonly struct Senders(Counter target) {
            public void Add(int amount) => Send(Find("Add(int)"), amount);

            public void Announce(int value) => Send(Find("Announce(int)"), value);

            void Send(RpcMethod method, int value) {
                var router = target.RpcRouter;

                if (router is null) {
                    return;
                }

                var writer = router.BeginCall(method, target.NetworkId);
                writer.WriteInt32(value);
                router.EndCall(method, target.NetworkId, ref writer);
            }
        }
    }
}

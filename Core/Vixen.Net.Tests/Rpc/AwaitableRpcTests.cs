// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Messaging;
using Vixen.Net.Rpc;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Net.Tests.Rpc;

/// <summary>Calls with an answer: "may I buy this", "what is in the chest", "am I allowed to start".</summary>
public sealed class AwaitableRpcTests {
    static readonly RpcMethod Ask = new(
        "Shop",
        "Buy(int)",
        RpcKind.Server,
        requireOwnership: false,
        Channel.Unreliable,
        RpcTarget.All,
        expectsReply: true
    );

    static readonly RpcMethod Tell = new(
        "Shop",
        "Restock()",
        RpcKind.Server,
        requireOwnership: false,
        Channel.Reliable,
        RpcTarget.All
    );

    static readonly RpcMethod Confirm = new(
        "Shop",
        "Confirm()",
        RpcKind.Client,
        requireOwnership: false,
        Channel.Reliable,
        RpcTarget.Owner,
        expectsReply: true
    );

    [Fact]
    public async Task AQuestionGetsItsAnswer() {
        var wire = new Loopback();
        var caller = Build(wire, RpcRole.Client, out _);
        var answerer = Build(wire, RpcRole.Server, out var handler);

        wire.Server = answerer;
        wire.Client = caller;
        handler.Answer = 42;

        var asking = caller.CallAsync<int>(Ask, new(1), Write(7), ReadInt);
        wire.Pump();

        Assert.Equal(42, await asking);
        Assert.Equal(1, caller.AnsweredCount);
        Assert.Equal(0, caller.PendingCallCount);
    }

    /// <summary>Several questions at once each get their own answer, in whatever order.</summary>
    /// <remarks>
    ///     The reason a correlation id exists rather than matching on the method: answers come back
    ///     when they are ready, and two calls about the same object would otherwise be
    ///     indistinguishable.
    /// </remarks>
    [Fact]
    public async Task AnswersFindTheRightAwaitEvenOutOfOrder() {
        var wire = new Loopback { Reverse = true };
        var caller = Build(wire, RpcRole.Client, out _);
        var answerer = Build(wire, RpcRole.Server, out var handler);

        wire.Server = answerer;
        wire.Client = caller;
        handler.Echo = true;

        var first = caller.CallAsync<int>(Ask, new(1), Write(1), ReadInt);
        var second = caller.CallAsync<int>(Ask, new(1), Write(2), ReadInt);
        var third = caller.CallAsync<int>(Ask, new(1), Write(3), ReadInt);

        wire.Pump();

        Assert.Equal(1, await first);
        Assert.Equal(2, await second);
        Assert.Equal(3, await third);
    }

    /// <summary>Nobody answering is a failure with a reason, not a hang.</summary>
    [Fact]
    public async Task ACallNobodyAnswersTimesOut() {
        var wire = new Loopback();
        var caller = Build(wire, RpcRole.Client, out _);

        // No answerer on the other end at all.
        var asking = caller.CallAsync<int>(Ask, new(1), Write(7), ReadInt, TimeSpan.FromSeconds(1));

        Assert.Equal(1, caller.PendingCallCount);

        caller.Advance(TimeSpan.FromSeconds(2));

        var failure = await Assert.ThrowsAsync<RpcFailedException>(async () => await asking);

        Assert.Equal(RpcFailure.TimedOut, failure.Failure);
        Assert.Equal(1, caller.TimedOutCount);
        Assert.Equal(0, caller.PendingCallCount);
    }

    /// <summary>A peer that goes away fails its calls at once rather than after the timeout.</summary>
    [Fact]
    public async Task APeerThatLeavesFailsWhatWasWaitingOnIt() {
        var wire = new Loopback();
        var server = Build(wire, RpcRole.Server, out _);

        server.Ownership.SetOwner(new(1), new(9));

        var asking = server.CallAsync<int>(Confirm, new(1), null, ReadInt, TimeSpan.FromMinutes(1));

        Assert.Equal(1, server.CancelPending(new(9)));

        var failure = await Assert.ThrowsAsync<RpcFailedException>(async () => await asking);
        Assert.Equal(RpcFailure.Disconnected, failure.Failure);
    }

    /// <summary>An answer that does not decode fails the call rather than being ignored.</summary>
    [Fact]
    public async Task AnAnswerThatDoesNotDecodeFailsTheCall() {
        var wire = new Loopback();
        var caller = Build(wire, RpcRole.Client, out _);

        var asking = caller.CallAsync<int>(Ask, new(1), Write(7), ReadInt, TimeSpan.FromMinutes(1));

        // A reply addressed to the call, carrying nothing it can read.
        var writer = new BitWriter(new byte[16]);
        writer.WriteVariable(1);
        Assert.True(writer.TryFinish(out var bits));

        Assert.False(caller.ReceiveReply(PlayerId.None, bits.ToArray()));

        var failure = await Assert.ThrowsAsync<RpcFailedException>(async () => await asking);
        Assert.Equal(RpcFailure.Malformed, failure.Failure);
    }

    /// <summary>A reply nobody is waiting for is counted, not an error.</summary>
    /// <remarks>
    ///     An answer that arrives after its own timeout looks exactly like this, and so does a
    ///     duplicate. Both are normal on a real network.
    /// </remarks>
    [Fact]
    public void AReplyForNothingIsCountedRatherThanThrown() {
        var wire = new Loopback();
        var caller = Build(wire, RpcRole.Client, out _);

        var writer = new BitWriter(new byte[16]);
        writer.WriteVariable(999);
        writer.WriteVariable(1);
        Assert.True(writer.TryFinish(out var bits));

        Assert.False(caller.ReceiveReply(PlayerId.None, bits.ToArray()));
        Assert.Equal(1, caller.UnmatchedReplyCount);
    }

    /// <summary>Awaiting a call nobody declared awaitable is refused at the call site.</summary>
    /// <remarks>
    ///     It would otherwise wait out the full timeout for a reply no handler was ever going to
    ///     send — a bug that presents as latency rather than as a mistake.
    /// </remarks>
    [Fact]
    public void AwaitingACallThatWasNotDeclaredAwaitable_Throws() {
        var wire = new Loopback();
        var caller = Build(wire, RpcRole.Client, out _);

        // An Action, so overload resolution cannot pick the async Throws and assert on a task
        // nobody awaited — the call throws before it ever produces one.
        var awaiting = new Action(() => _ = caller.CallAsync<int>(Tell, new(1), null, ReadInt));

        Assert.Throws<ArgumentException>(awaiting);
    }

    static RpcArguments Write(int value) => (ref BitWriter writer) => writer.WriteVariable((uint)value);

    static bool ReadInt(ref BitReader reader, out int value) {
        var read = reader.TryReadVariable(out var raw);
        value = (int)raw;

        return read;
    }

    static RpcRouter Build(Loopback wire, RpcRole role, out Answering handler) {
        var manifest = new RpcManifest();

        // Ordered by method id, which is what a manifest requires and what the generator emits.
        var methods = new[] { Ask, Tell, Confirm };
        Array.Sort(methods, (left, right) => left.MethodId.CompareTo(right.MethodId));
        manifest.Register(methods);

        var router = new RpcRouter(manifest, wire, role);
        handler = new(router);
        router.Register(new(1), handler);

        return router;
    }

    /// <summary>A handler that answers, so the reply path is exercised rather than stubbed.</summary>
    sealed class Answering(RpcRouter router) : IRpcInvoker {
        public uint RpcTypeId { get; } = RpcMethod.Hash("Shop");

        public int Answer { get; set; }

        /// <summary>Whether to answer with the argument rather than with <see cref="Answer" />.</summary>
        public bool Echo { get; set; }

        public bool Invoke(uint methodIndex, in RpcContext context, ref BitReader reader) {
            if (!reader.TryReadVariable(out var asked)) {
                return false;
            }

            var answer = Echo ? (int)asked : Answer;

            return router.Reply(context, (ref BitWriter writer) => writer.WriteVariable((uint)answer));
        }
    }

    /// <summary>A wire that hands calls and replies straight to the other end.</summary>
    sealed class Loopback : IRpcTransport {
        readonly List<byte[]> toServer = [];
        readonly List<byte[]> toClient = [];

        public RpcRouter? Server { get; set; }

        public RpcRouter? Client { get; set; }

        /// <summary>Whether to deliver in reverse, so answers arrive out of order.</summary>
        public bool Reverse { get; set; }

        public void SendToServer(ReadOnlySpan<byte> payload, Channel channel) => toServer.Add(payload.ToArray());

        public void SendToPlayer(PlayerId player, ReadOnlySpan<byte> payload, Channel channel) =>
            toClient.Add(payload.ToArray());

        public void SendToAll(ReadOnlySpan<byte> payload, Channel channel) => toClient.Add(payload.ToArray());

        public void Pump() {
            for (var round = 0; round < 4; round++) {
                Deliver(toServer, packet => Server?.Receive(new(1), packet));
                Deliver(toClient, packet => Client?.ReceiveReply(PlayerId.None, packet));
            }
        }

        void Deliver(List<byte[]> queue, Action<byte[]> to) {
            if (queue.Count == 0) {
                return;
            }

            var packets = queue.ToArray();
            queue.Clear();

            if (Reverse) {
                Array.Reverse(packets);
            }

            foreach (var packet in packets) {
                to(packet);
            }
        }
    }
}

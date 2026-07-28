// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Messaging;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Net.Tests;

/// <summary>A message about nothing in particular: chat, a countdown, a round ending.</summary>
public sealed class BroadcastTests {
    [Fact]
    public void AMessageReachesItsHandler() {
        var router = new BroadcastRouter();
        var heard = new List<(PlayerId From, string Text)>();

        router.Subscribe<Chat>((from, message) => heard.Add((from, message.Text)));

        Assert.True(router.TryEncode(new Chat { Text = "hello" }, out var payload));
        Assert.True(router.Receive(new(7), payload.ToArray()));

        Assert.Equal([(new PlayerId(7), "hello")], heard);
        Assert.Equal(1, router.DeliveredCount);
    }

    /// <summary>Several subscribers each get the message, and none starves the next.</summary>
    /// <remarks>
    ///     Each handler reads from its own reader over the same bits. One that consumed the payload
    ///     would leave the second decoding whatever was left, which is a bug that only appears once
    ///     somebody adds a second subscriber — usually long after the first was written.
    /// </remarks>
    [Fact]
    public void EverySubscriberGetsIt() {
        var router = new BroadcastRouter();
        var first = 0;
        var second = 0;

        router.Subscribe<Countdown>((_, message) => first += message.Seconds);
        router.Subscribe<Countdown>((_, message) => second += message.Seconds);

        Assert.True(router.TryEncode(new Countdown { Seconds = 3 }, out var payload));
        Assert.True(router.Receive(PlayerId.None, payload.ToArray()));

        Assert.Equal(3, first);
        Assert.Equal(3, second);
    }

    /// <summary>An id outside the registry is refused, not constructed.</summary>
    /// <remarks>
    ///     The closed-set rule the whole design keeps: a packet names a position in a registry, never
    ///     a type. This is the classic remote-code-execution vector in game netcode and doc 16
    ///     excludes it by construction.
    /// </remarks>
    [Fact]
    public void AnIdThatIsNotRegistered_IsRefused() {
        var router = new BroadcastRouter();
        router.Subscribe<Chat>((_, _) => { });

        var writer = new BitWriter(new byte[64]);
        writer.WriteVariable(0xDEADBEEF);
        writer.WriteVariable(3);
        Assert.True(writer.TryFinish(out var bits));

        Assert.False(router.Receive(new(1), bits.ToArray()));
        Assert.Equal(1, router.RefusedByRegistryCount);
    }

    [Fact]
    public void AMessageThatDoesNotDecode_IsRefused() {
        var router = new BroadcastRouter();
        router.Subscribe<Chat>((_, _) => { });

        var writer = new BitWriter(new byte[64]);
        writer.WriteVariable(BroadcastRouter.Identify<Chat>());

        // A length that outruns what follows it.
        writer.WriteVariable(64);
        Assert.True(writer.TryFinish(out var bits));

        Assert.False(router.Receive(new(1), bits.ToArray()));
        Assert.Equal(1, router.RefusedByPayloadCount);
    }

    /// <summary>A message type nobody registered is refused by the registry, not silently dropped.</summary>
    /// <remarks>
    ///     There is no "registered and unhandled" state to test — Subscribe is the only way to
    ///     register and it always adds a handler — so not subscribing at all is the whole of what
    ///     "nobody is listening" means here.
    /// </remarks>
    [Fact]
    public void AMessageTypeNobodySubscribedTo_IsRefusedByTheRegistry() {
        var sender = new BroadcastRouter();
        sender.Subscribe<Chat>((_, _) => { });

        var receiver = new BroadcastRouter();
        receiver.Subscribe<Countdown>((_, _) => { });

        Assert.True(sender.TryEncode(new Chat { Text = "x" }, out var payload));

        Assert.False(receiver.Receive(new(1), payload.ToArray()));
        Assert.Equal(1, receiver.RefusedByRegistryCount);
    }

    /// <summary>A client sending too many is cut off; the server is never rate limited.</summary>
    /// <remarks>
    ///     A broadcast fans out, so one client's message is one packet per player in the session —
    ///     which makes it the cheapest thing a client has for making the server work for everybody
    ///     else. The server's own broadcasts carry <see cref="PlayerId.None" /> and are not counted,
    ///     because a server rate-limiting itself is a server with a bug.
    /// </remarks>
    [Fact]
    public void AClientSendingTooManyIsRefused() {
        var router = new BroadcastRouter { Limits = new() { PerSecond = 10, Burst = 3 } };
        router.Subscribe<Chat>((_, _) => { });

        Assert.True(router.TryEncode(new Chat { Text = "spam" }, out var payload));

        var bytes = payload.ToArray();

        Assert.True(router.Receive(new(1), bytes));
        Assert.True(router.Receive(new(1), bytes));
        Assert.True(router.Receive(new(1), bytes));
        Assert.False(router.Receive(new(1), bytes));
        Assert.Equal(1, router.RefusedByRateLimitCount);

        // The server is not a player and is not limited.
        Assert.True(router.Receive(PlayerId.None, bytes));

        // And the bucket refills.
        router.Advance(TimeSpan.FromSeconds(1));
        Assert.True(router.Receive(new(1), bytes));
    }

    [Fact]
    public void ARenamedMessageIsADifferentMessage() {
        Assert.NotEqual(BroadcastRouter.Identify<Chat>(), BroadcastRouter.Identify<Countdown>());
    }

    /// <summary>Chat: a string somebody typed, which is why it is capped.</summary>
    struct Chat : IBroadcast<Chat> {
        public string Text;

        public static string BroadcastName => "Vixen.Tests.Chat";

        public readonly void Write(ref BitWriter writer) {
            var bytes = System.Text.Encoding.UTF8.GetBytes(Text ?? string.Empty);
            writer.WriteVariable((uint)bytes.Length);
            writer.WriteBytes(bytes);
        }

        public static bool TryRead(ref BitReader reader, out Chat value) {
            value = default;

            // The cap is the reader's, stated here and never taken from the packet — the same rule
            // PacketReader keeps for every length it is given.
            if (!reader.TryReadVariable(out var length) || length > 256) {
                return false;
            }

            if (!reader.TryReadBytes((int)length, out var bytes)) {
                return false;
            }

            value = new() { Text = bytes.IsEmpty ? string.Empty : System.Text.Encoding.UTF8.GetString(bytes) };

            return true;
        }
    }

    struct Countdown : IBroadcast<Countdown> {
        public int Seconds;

        public static string BroadcastName => "Vixen.Tests.Countdown";

        public readonly void Write(ref BitWriter writer) => writer.WriteVariable((uint)Seconds);

        public static bool TryRead(ref BitReader reader, out Countdown value) {
            value = default;

            if (!reader.TryReadVariable(out var seconds)) {
                return false;
            }

            value = new() { Seconds = (int)seconds };

            return true;
        }
    }
}

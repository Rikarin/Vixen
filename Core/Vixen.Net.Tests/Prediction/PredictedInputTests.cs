// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Messaging;
using Vixen.Net.Prediction;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Net.Tests.Prediction;

/// <summary>The input pipeline: what a client did, reaching the server before the tick it is for.</summary>
public sealed class PredictedInputTests {
    readonly byte[] buffer = new byte[512];

    /// <summary>Every packet carries the last several ticks, so one lost packet costs nothing.</summary>
    /// <remarks>
    ///     <para>
    ///         The property the whole design turns on, and the one the obvious implementation gets
    ///         wrong. A lost input is not a lost update that the next packet supersedes: it is a tick
    ///         the server simulates differently from the client that predicted it, and nothing
    ///         afterwards repairs the divergence.
    ///     </para>
    ///     <para>
    ///         So this drops three packets in a row and asserts the server still has every tick. Only
    ///         redundancy can do that; a design sending one input per packet would have three holes.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ThreeLostPacketsCostNoInputs() {
        var log = new InputLog<Move> { Redundancy = 4 };
        var server = new InputBuffer<Move>();

        for (var tick = 1u; tick <= 8; tick++) {
            log.Record(new(tick), new Move { X = (short)tick });

            Assert.True(log.TryWrite(buffer, out var payload));

            // Packets 4, 5 and 6 never arrive.
            if (tick is >= 4 and <= 6) {
                continue;
            }

            Assert.True(server.TryReceive(payload, simulated: default));
        }

        for (var tick = 1u; tick <= 8; tick++) {
            Assert.True(server.TryTake(new(tick), out var input), $"Tick {tick} was missing.");
            Assert.Equal((short)tick, input.X);
        }

        Assert.Equal(0, server.StarvedCount);
    }

    /// <summary>A loss longer than the redundancy is what the redundancy is measured against.</summary>
    /// <remarks>
    ///     The other side of the property, asserted rather than assumed: four is a choice, and a
    ///     choice is only meaningful if exceeding it does what the number says it will. A test that
    ///     only showed the happy case would pass with the redundancy set to any value at all.
    /// </remarks>
    [Fact]
    public void ALossLongerThanTheRedundancyLosesInputs() {
        var log = new InputLog<Move> { Redundancy = 2 };
        var server = new InputBuffer<Move>();

        for (var tick = 1u; tick <= 8; tick++) {
            log.Record(new(tick), new Move { X = (short)tick });
            Assert.True(log.TryWrite(buffer, out var payload));

            if (tick is >= 3 and <= 6) {
                continue;
            }

            Assert.True(server.TryReceive(payload, simulated: default));
        }

        // Ticks 3 and 4 fell outside every surviving packet's window.
        Assert.False(server.TryTake(new(3), out _));
        Assert.True(server.TryTake(new(7), out _));
    }

    /// <summary>A starved tick repeats the last input rather than zeroing it.</summary>
    /// <remarks>
    ///     Zero is the worst available answer: a player holding forward stops dead for one tick on the
    ///     server while their own client predicts them still moving, which turns a dropped packet into
    ///     a guaranteed correction. Repeating is usually exactly what the client predicted, so most
    ///     starvation costs nothing — which is why the counter matters more than the behaviour.
    /// </remarks>
    [Fact]
    public void AStarvedTickRepeatsRatherThanZeroes() {
        var server = new InputBuffer<Move>();

        server.Offer(new(1), new Move { X = 7 }, simulated: default);

        Assert.True(server.TryTake(new(1), out var first));
        Assert.Equal(7, first.X);

        Assert.False(server.TryTake(new(2), out var starved));
        Assert.Equal(7, starved.X);
        Assert.Equal(1, server.StarvedCount);
    }

    /// <summary>An input for a tick already simulated is counted, not applied.</summary>
    /// <remarks>
    ///     <see cref="InputBuffer{T}.LateCount" /> is the number that says a client is not running far
    ///     enough ahead, which is the signal the whole jitter buffer is steered by.
    /// </remarks>
    [Fact]
    public void AnInputForATickAlreadySimulatedIsLate() {
        var server = new InputBuffer<Move>();

        server.Offer(new(5), new Move { X = 1 }, simulated: new(5));

        Assert.Equal(1, server.LateCount);
        Assert.Equal(0, server.Depth);

        server.Offer(new(6), new Move { X = 2 }, simulated: new(5));
        Assert.Equal(1, server.Depth);
    }

    /// <summary>Redundancy arriving twice is the mechanism working, not an error.</summary>
    [Fact]
    public void ARepeatedInputIsADuplicateAndNotALoss() {
        var log = new InputLog<Move> { Redundancy = 4 };
        var server = new InputBuffer<Move>();

        log.Record(new(1), new Move { X = 1 });
        Assert.True(log.TryWrite(buffer, out var first));
        Assert.True(server.TryReceive(first, simulated: default));

        log.Record(new(2), new Move { X = 2 });
        Assert.True(log.TryWrite(buffer, out var second));
        Assert.True(server.TryReceive(second, simulated: default));

        // The second packet carried tick 1 again.
        Assert.Equal(1, server.DuplicateCount);
        Assert.Equal(0, server.LateCount);
        Assert.Equal(2, server.Depth);
    }

    /// <summary>A client running far ahead is refused rather than remembered.</summary>
    /// <remarks>
    ///     A bound on memory a client can otherwise choose to consume. It is the difference between a
    ///     wrong clock costing that player some rejected inputs and costing the server a megabyte per
    ///     connection.
    /// </remarks>
    [Fact]
    public void AClientRunningTooFarAheadIsRefused() {
        var server = new InputBuffer<Move> { Capacity = 4 };

        for (var tick = 1u; tick <= 10; tick++) {
            server.Offer(new(tick), new Move { X = (short)tick }, simulated: default);
        }

        Assert.Equal(4, server.Depth);
        Assert.Equal(6, server.RefusedCount);
    }

    /// <summary>An acknowledgement is what trims the log, so a slow one keeps the replay's inputs.</summary>
    /// <remarks>
    ///     The log is two things at once — what goes on the wire and what a rollback replays — so
    ///     trimming by age would throw away exactly the inputs a slow acknowledgement still needs.
    /// </remarks>
    [Fact]
    public void TheLogIsTrimmedByAcknowledgementAndNotByAge() {
        var log = new InputLog<Move> { Redundancy = 2 };

        for (var tick = 1u; tick <= 20; tick++) {
            log.Record(new(tick), new Move { X = (short)tick });
        }

        // Nothing acknowledged, so everything a replay could want is still there.
        Assert.Equal(20, log.Count);
        Assert.True(log.TryGet(new(1), out _));

        log.Acknowledge(new(15));

        Assert.Equal(5, log.Count);
        Assert.False(log.TryGet(new(15), out _));
        Assert.True(log.TryGet(new(16), out _));
        Assert.Equal(0, log.OverflowCount);
    }

    /// <summary>A log whose acknowledgements stop arriving is bounded rather than unbounded.</summary>
    [Fact]
    public void ALogWithNoAcknowledgementsIsStillBounded() {
        var log = new InputLog<Move> { Capacity = 8 };

        for (var tick = 1u; tick <= 40; tick++) {
            log.Record(new(tick), new Move { X = (short)tick });
        }

        Assert.Equal(8, log.Count);
        Assert.Equal(32, log.OverflowCount);
    }

    /// <summary>Acknowledged inputs are not sent again.</summary>
    [Fact]
    public void AcknowledgedInputsAreNotSentAgain() {
        var log = new InputLog<Move> { Redundancy = 4 };

        for (var tick = 1u; tick <= 4; tick++) {
            log.Record(new(tick), new Move { X = (short)tick });
        }

        Assert.True(log.TryWrite(buffer, out var whole));
        log.Acknowledge(new(3));
        Assert.True(log.TryWrite(buffer, out var trimmed));

        Assert.True(trimmed.Length < whole.Length, "A settled question should not still be on the wire.");

        var server = new InputBuffer<Move>();
        Assert.True(server.TryReceive(trimmed, simulated: default));
        Assert.Equal(1, server.Depth);
    }

    /// <summary>A truncated payload is refused rather than half-applied.</summary>
    [Fact]
    public void ATruncatedPayloadIsRefused() {
        var log = new InputLog<Move> { Redundancy = 4 };

        for (var tick = 1u; tick <= 4; tick++) {
            log.Record(new(tick), new Move { X = (short)tick });
        }

        Assert.True(log.TryWrite(buffer, out var payload));

        var server = new InputBuffer<Move>();
        Assert.False(server.TryReceive(payload[..3], simulated: default));
        Assert.Equal(1, server.MalformedCount);
    }

    /// <summary>Every payload kind survives the session's wrapper.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>This exists because it did not, and something was silently broken.</b>
    ///         <c>TryUnwrap</c> checked against <c>PayloadKind.Rpc</c> by name, which was the largest
    ///         kind when it was written and stopped being so when broadcasts were added — so every
    ///         broadcast that went through the session layer was refused as malformed. The router's
    ///         own tests passed throughout, because they never went through the session layer.
    ///     </para>
    ///     <para>
    ///         Enumerating the enum rather than listing the kinds is the point: the next kind added
    ///         fails here rather than in a game.
    ///     </para>
    /// </remarks>
    [Fact]
    public void EveryPayloadKindSurvivesTheWrapper() {
        ReadOnlySpan<byte> body = [1, 2, 3];
        var wrapping = new byte[8];

        foreach (var kind in Enum.GetValues<PayloadKind>()) {
            Assert.True(NetworkPayload.TryWrap(kind, body, wrapping, out var wrapped), $"{kind} did not wrap.");
            Assert.True(NetworkPayload.TryUnwrap(wrapped, out var read, out var inner), $"{kind} did not unwrap.");
            Assert.Equal(kind, read);
            Assert.True(inner.SequenceEqual(body));
        }

        // And one above the largest is still refused, which is the half of the check that matters for
        // a packet somebody else wrote.
        Assert.False(NetworkPayload.TryUnwrap([(byte)PayloadKind.Last + 1, 0], out _, out _));
    }

    /// <summary>A movement input: two axes and a jump, which is all these need.</summary>
    readonly record struct Move : IPredictedInput<Move> {
        public short X { get; init; }
        public short Y { get; init; }
        public bool Jump { get; init; }

        public void Write(ref BitWriter writer) {
            writer.Write((uint)(ushort)X, 16);
            writer.Write((uint)(ushort)Y, 16);
            writer.WriteBool(Jump);
        }

        public static bool TryRead(ref BitReader reader, out Move value) {
            value = default;

            if (!reader.TryRead(16, out var x) || !reader.TryRead(16, out var y) || !reader.TryReadBool(out var jump)) {
                return false;
            }

            value = new() { X = (short)(ushort)x, Y = (short)(ushort)y, Jump = jump };

            return true;
        }
    }
}

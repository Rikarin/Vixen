// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Net.Tests.Sessions;

/// <summary>The session's own bandwidth budget, and what it sheds first.</summary>
/// <remarks>
///     <para>
///         <b>Nothing here asserts an elapsed time.</b> The bucket is refilled from the
///         <c>elapsed</c> handed to <see cref="NetworkSession.Update" />, so a "second" below is
///         sixty-two sixteen-millisecond steps and takes as long as sixty-two function calls take.
///         Every assertion is over a count of messages or of bytes.
///     </para>
///     <para>
///         ⚠ <b>The budgets are set absurdly small on purpose.</b> The default is nothing at all —
///         unmetered — so a test that took the defaults would pass whether the budget existed or not.
///     </para>
/// </remarks>
public sealed class SessionBandwidthTests : IDisposable {
    readonly SessionHarness harness = new();

    public void Dispose() => harness.Dispose();

    /// <summary>An unset budget sheds nothing, which is what every existing game relies on.</summary>
    /// <remarks>
    ///     The counterweight. A shedding counter that only ever rises says as little as one that never
    ///     does, and the default has to be that a game which never heard of this keeps working.
    /// </remarks>
    [Fact]
    public void WithNoBudgetNothingIsShed() {
        var server = harness.StartServer();
        var client = harness.StartClient();

        harness.Pump();

        for (var i = 0; i < 500; i++) {
            Assert.True(server.SendToPlayer(server.Players[0].Id, Payload(512), Channel.Unreliable));
        }

        Assert.Equal(0, server.ShedCount);
        Assert.Equal(0, server.ShedByteCount);
        Assert.NotNull(client.LocalPlayer);
    }

    /// <summary>Past the budget, a send is refused rather than sent or silently dropped.</summary>
    /// <remarks>
    ///     The refusal is the whole design: <c>false</c> is the answer the caller already gets for a
    ///     player who has gone, so a game that handles that handles this. What must never happen is
    ///     the third thing — <c>true</c>, and the bytes nowhere.
    /// </remarks>
    [Fact]
    public void PastTheBudgetASendIsRefusedAndCounted() {
        var server = harness.StartServer(new() { BytesPerSecondPerPlayer = 4096, BurstBytes = 4096, ReservedFraction = 0 });

        harness.StartClient();
        harness.Pump();

        var player = server.Players[0].Id;
        var sent = 0;

        // Eight 512-byte payloads fill a 4 KB bucket, and the framing byte means the eighth does not
        // quite fit. What matters is that the run stops and says so, not exactly where.
        for (var i = 0; i < 40; i++) {
            if (server.SendToPlayer(player, Payload(512), Channel.Unreliable)) {
                sent++;
            }
        }

        Assert.InRange(sent, 1, 8);
        Assert.Equal(40 - sent, server.ShedCount);
        Assert.True(server.ShedByteCount > 0, "bytes were shed and not counted");
    }

    /// <summary>Chatter stops while important traffic keeps going.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the shedding, and it is what a plain bucket does not do.</b> A bucket alone
    ///     would refuse whichever message happened to arrive when it ran out; the reserve means the
    ///     order the game cares about survives the order the sends happened in.
    /// </remarks>
    [Fact]
    public void ChatterIsShedWhileImportantTrafficStillGoes() {
        var server = harness.StartServer(
            new() { BytesPerSecondPerPlayer = 4096, BurstBytes = 4096, ReservedFraction = 0.5, ReservedPriority = 1 }
        );

        harness.StartClient();
        harness.Pump();

        var player = server.Players[0].Id;
        var chatter = 0;

        // Half the bucket is reserved, so priority zero may spend down to 2 KB and no further.
        while (server.SendToPlayer(player, Payload(256), Channel.Unreliable)) {
            chatter++;

            Assert.True(chatter < 100, "the reserve never stopped anything");
        }

        // And the half it could not touch is still there for something that matters.
        var important = 0;

        while (server.SendToPlayer(player, Payload(256), Channel.Unreliable, priority: 1)) {
            important++;

            Assert.True(important < 100, "the reserve was never exhausted either");
        }

        Assert.True(
            important > 0,
            $"chatter took the whole bucket: {chatter.ToString(CultureInfo.InvariantCulture)} low-priority sends left nothing reserved"
        );

        // Roughly half each, because the reserve is half. Stated as a band rather than a number: the
        // framing byte and the bucket's exact depth are not what is under test.
        Assert.InRange(important, chatter / 2, chatter * 2);
    }

    /// <summary>The bucket refills as the session runs, and stops at its depth.</summary>
    [Fact]
    public void TheBucketRefillsAndDoesNotOverfill() {
        var server = harness.StartServer(new() { BytesPerSecondPerPlayer = 8192, BurstBytes = 8192, ReservedFraction = 0 });

        harness.StartClient();
        harness.Pump();

        var player = server.Players[0].Id;

        Drain(server, player, 1024);

        var shedWhenEmpty = server.ShedCount;

        Assert.True(shedWhenEmpty > 0, "the bucket never emptied");
        Assert.False(server.SendToPlayer(player, Payload(1024), Channel.Unreliable));

        // 62 × 16 ms is a second of the session's clock, which is a whole bucket's worth.
        harness.Pump(62);

        Assert.True(server.SendToPlayer(player, Payload(1024), Channel.Unreliable), "the bucket never refilled");

        // And it is a bucket, not a meter: a minute of quiet does not buy a minute of burst.
        harness.Pump(620);

        var afterLongQuiet = 0;

        while (server.SendToPlayer(player, Payload(1024), Channel.Unreliable)) {
            afterLongQuiet++;

            Assert.True(afterLongQuiet < 100, "the bucket kept filling past its depth");
        }

        Assert.InRange(afterLongQuiet, 1, 8);
    }

    /// <summary>One player on a narrow budget is not a reason to stop talking to the others.</summary>
    [Fact]
    public void TheBudgetIsPerPlayerSoAFanOutReachesTheRest() {
        var server = harness.StartServer(new() { BytesPerSecondPerPlayer = 2048, BurstBytes = 2048, ReservedFraction = 0 });

        harness.StartClient();
        harness.StartClient();
        harness.Pump();

        Assert.Equal(2, server.Players.Count);

        // Drain exactly one player's bucket.
        Drain(server, server.Players[0].Id, 256);

        // The fan-out still reaches the other one, and says so.
        Assert.Equal(1, server.SendToAll(Payload(256), Channel.Unreliable));
    }

    static byte[] Payload(int bytes) => new byte[bytes];

    /// <summary>Sends until the budget refuses, and gives up long before a hang.</summary>
    /// <remarks>
    ///     ⚠ The ceiling is a hang check and not a bound — it says nothing about how many sends a
    ///     bucket should take, only that a bucket which never empties fails this suite in a second
    ///     instead of holding a CI agent until it is killed. Nothing asserts against it being close.
    /// </remarks>
    static int Drain(NetworkSession server, PlayerId player, int bytes) {
        var sent = 0;

        while (server.SendToPlayer(player, Payload(bytes), Channel.Unreliable)) {
            Assert.True(++sent < 10_000, "the budget never refused anything — this is a hang check, not a bound");
        }

        return sent;
    }
}

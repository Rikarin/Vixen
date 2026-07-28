// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Engine;
using Vixen.Net.Messaging;
using Xunit;

namespace Vixen.Net.Engine.Tests;

/// <summary>SyncList: that ops replay, that a late joiner catches up, and that indices are checked.</summary>
public sealed class SyncListTests {
    readonly byte[] buffer = new byte[4096];

    [Fact]
    public void OpsReplayIntoTheSameListOnTheOtherEnd() {
        var server = new SyncList<int>();
        var client = new SyncList<int>();

        server.Add(1);
        server.Add(2);
        server.Add(3);
        server.Insert(1, 99);
        server.RemoveAt(0);
        server.Replace(0, 50);

        Assert.True(Send(server, client));
        Assert.Equal([50, 2, 3], client.ToArray());
    }

    /// <summary>A change costs an op, not a list.</summary>
    /// <remarks>
    ///     The whole reason this is not a whole-state component. Appending one item to a list of a
    ///     hundred should cost one item, and it is the thing a bandwidth report would otherwise show
    ///     as a hundred every time somebody picked something up.
    /// </remarks>
    [Fact]
    public void AppendingToALongListCostsOneItem() {
        var server = new SyncList<int>();

        for (var i = 0; i < 100; i++) {
            server.Add(i);
        }

        var whole = new BitWriter(buffer);
        Assert.True(server.WriteWhole(ref whole));

        server.ClearPending();
        server.Add(100);

        var incremental = new BitWriter(new byte[4096]);
        Assert.True(server.WritePending(ref incremental));

        Assert.True(
            incremental.BitsWritten * 20 < whole.BitsWritten,
            $"an append cost {incremental.BitsWritten} bits against {whole.BitsWritten} for the list"
        );
    }

    [Fact]
    public void ALateJoinerGetsTheListWholeAndEndsUpInTheSamePlace() {
        var server = new SyncList<int>();
        server.Add(7);
        server.Add(8);
        server.ClearPending();

        // Never saw the ops that built it.
        var joiner = new SyncList<int>();
        var writer = new BitWriter(buffer);

        Assert.True(server.WriteWhole(ref writer));
        Assert.True(writer.TryFinish(out var bits));

        var reader = new BitReader(bits);

        Assert.True(joiner.Apply(ref reader));
        Assert.Equal([7, 8], joiner.ToArray());
    }

    [Fact]
    public void ClearingDropsWhatWasPending() {
        var server = new SyncList<int>();
        var client = new SyncList<int>();

        server.Add(1);
        server.Add(2);
        server.Clear();
        server.Add(3);

        Assert.True(Send(server, client));
        Assert.Equal([3], client.ToArray());
    }

    [Fact]
    public void AChangeThatArrivesRaisesChanged() {
        var server = new SyncList<int>();
        var client = new SyncList<int>();
        var seen = new List<(SyncListChange Change, int Index, int Item)>();

        client.Changed += (change, index, item) => seen.Add((change, index, item));

        server.Add(4);
        server.RemoveAt(0);
        Send(server, client);

        Assert.Equal([(SyncListChange.Added, 0, 4), (SyncListChange.Removed, 0, 4)], seen);
    }

    /// <summary>An index from the wire is somebody else's number, so it is checked.</summary>
    /// <remarks>
    ///     The same rule the packet reader keeps: inbound bytes come from a machine we do not
    ///     control, and a malformed one is a refused message rather than an exception out of a
    ///     decoder — which on a server is a denial of service.
    /// </remarks>
    [Fact]
    public void AnIndexThatIsNotThere_IsRefusedRatherThanThrowing() {
        var client = new SyncList<int>();
        var writer = new BitWriter(buffer);

        writer.WriteVariable(1);
        writer.Write((uint)SyncListChange.Removed, 3);
        writer.WriteVariable(7);

        Assert.True(writer.TryFinish(out var bits));

        var reader = new BitReader(bits);

        Assert.False(client.Apply(ref reader));
        Assert.Empty(client);
    }

    [Fact]
    public void ATypeTheWireDoesNotKnow_IsRefusedAtConstruction() =>
        Assert.Throws<NotSupportedException>(() => new SyncList<DateTime>());

    bool Send<T>(SyncList<T> from, SyncList<T> to) {
        var writer = new BitWriter(buffer);

        if (!from.WritePending(ref writer) || !writer.TryFinish(out var bits)) {
            return false;
        }

        from.ClearPending();

        var reader = new BitReader(bits);

        return to.Apply(ref reader);
    }
}

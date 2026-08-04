// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Live.Transfer.Tests;

/// <summary>The client's half: two sessions, one switch, and the cost stated rather than hidden.</summary>
public class ClientTransferTests {
    static readonly RealmVersion Running = new("0.1.0", 0xC0FFEE);
    static readonly ShardId Target = ShardId.New();

    [Fact]
    public void The_client_holds_two_sessions_and_switches_once() {
        var client = new ClientTransfer();

        Assert.True(client.Prepared(Prepare(), Running));
        Assert.Equal(ClientTransferState.Connecting, client.State);

        Assert.True(client.Connected(1_000));
        Assert.Equal(ClientTransferState.Loading, client.State);

        Assert.True(client.Loaded());
        Assert.Equal(ClientTransferState.Waiting, client.State);

        Assert.True(client.Committed(new(1_050, Target)));
        Assert.Equal(ClientTransferState.Switched, client.State);
        Assert.Equal(1, client.PredictionResets);
    }

    /// <summary>
    ///     ⚠ A client that stopped rendering at Connecting would turn a preload back into a loading
    ///     screen and give up everything the protocol is for.
    /// </summary>
    [Fact]
    public void The_source_stays_authoritative_until_the_switch() {
        var client = new ClientTransfer();

        Assert.True(client.SourceIsAuthoritative);

        client.Prepared(Prepare(), Running);
        Assert.True(client.SourceIsAuthoritative);

        client.Connected(1_000);
        Assert.True(client.SourceIsAuthoritative);

        client.Loaded();
        Assert.True(client.SourceIsAuthoritative);

        client.Committed(new(1_050, Target));
        Assert.False(client.SourceIsAuthoritative);
    }

    /// <summary>
    ///     The content check happens before the socket is opened. The handshake would reject a
    ///     mismatch anyway, but finding out here turns "connection refused" into "fetch the catalog".
    /// </summary>
    [Fact]
    public void A_client_on_the_wrong_content_does_not_open_the_second_session() {
        var client = new ClientTransfer();

        Assert.False(client.Prepared(Prepare(), new("0.1.0", 0xBADF00D)));
        Assert.Equal(ClientTransferState.Settled, client.State);
        Assert.Null(client.Prepare);
    }

    /// <summary>The two clocks are unrelated, and the offset is the whole of the relationship.</summary>
    [Fact]
    public void The_tick_rebase_converges_over_the_overlap() {
        var client = new ClientTransfer();

        client.Prepared(Prepare(targetTick: 900), Running);
        client.Connected(1_000);

        Assert.Equal(-100, client.Rebase.Offset);
        Assert.Equal(900, client.Rebase.ToTarget(1_000));

        // A snapshot during the overlap gives a better estimate; by t6 it has converged, so the
        // switch is a pointer change rather than a resync.
        client.Observed(1_060, 973);

        Assert.Equal(-87, client.Rebase.Offset);
        Assert.Equal(973, client.Rebase.ToTarget(1_060));
    }

    [Fact]
    public void A_rebase_of_nothing_is_nothing() {
        Assert.False(TickRebase.None.IsValid);
        Assert.Equal(0, TickRebase.None.Offset);
        Assert.Equal("no rebase", TickRebase.None.ToString());
    }

    /// <summary>
    ///     ⚠ A commit for a shard this client is not transferring to is a message from a transfer
    ///     that already aborted, and obeying it would send the player to a realm holding nothing.
    /// </summary>
    [Fact]
    public void A_commit_for_another_shard_is_ignored() {
        var client = new ClientTransfer();

        client.Prepared(Prepare(), Running);
        client.Connected(1_000);
        client.Loaded();

        Assert.False(client.Committed(new(1_050, ShardId.New())));
        Assert.Equal(ClientTransferState.Waiting, client.State);
        Assert.Equal(0, client.PredictionResets);
    }

    [Fact]
    public void A_commit_before_the_map_loaded_is_ignored() {
        var client = new ClientTransfer();

        client.Prepared(Prepare(), Running);
        client.Connected(1_000);

        Assert.False(client.Committed(new(1_050, Target)));
        Assert.Equal(ClientTransferState.Loading, client.State);
    }

    /// <summary>Abandoning is not a failure the player sees — the first session never closed.</summary>
    [Fact]
    public void Abandoning_leaves_the_client_where_it_already_was() {
        var client = new ClientTransfer();

        client.Prepared(Prepare(), Running);
        client.Connected(1_000);
        client.Loaded();

        Assert.True(client.Abandon());
        Assert.Equal(ClientTransferState.Abandoned, client.State);
        Assert.True(client.SourceIsAuthoritative);
        Assert.Null(client.Prepare);
        Assert.Equal(0, client.PredictionResets);
    }

    [Fact]
    public void A_switched_client_cannot_be_abandoned() {
        var client = new ClientTransfer();

        client.Prepared(Prepare(), Running);
        client.Connected(1_000);
        client.Loaded();
        client.Committed(new(1_050, Target));

        Assert.False(client.Abandon());
        Assert.Equal(ClientTransferState.Switched, client.State);
    }

    [Fact]
    public void An_abandoned_client_can_be_told_to_go_somewhere_else() {
        var client = new ClientTransfer();

        client.Prepared(Prepare(), Running);
        client.Abandon();

        Assert.True(client.Prepared(Prepare(), Running));
        Assert.Equal(ClientTransferState.Connecting, client.State);
    }

    /// <summary>
    ///     Exactly one reset per transfer, and it happens at the switch rather than at t2 — resetting
    ///     any earlier would throw away prediction the player is still using on the source.
    /// </summary>
    [Fact]
    public void Two_transfers_are_two_resets_and_no_more() {
        var client = new ClientTransfer();

        for (var index = 0; index < 2; index++) {
            client.Prepared(Prepare(), Running);
            client.Connected(1_000);
            client.Loaded();
            client.Committed(new(1_050, Target));
            client.Settle();
        }

        Assert.Equal(2, client.PredictionResets);
        Assert.Equal(ClientTransferState.Settled, client.State);
    }

    static TransferPrepare Prepare(long targetTick = 900) =>
        new("a ticket", new("realm.example", 30001), Target, Running, targetTick);
}

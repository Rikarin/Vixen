// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Xunit;

namespace Vixen.Live.Transfer.Tests;

/// <summary>The four numbers a transfer that degrades quietly would otherwise hide.</summary>
public class TransferMetricsTests {
    static readonly DateTimeOffset Noon = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    static readonly ShardId Target = ShardId.New();

    [Fact]
    public void An_empty_fleet_has_not_failed_at_anything() {
        var metrics = new TransferMetrics();

        Assert.Equal(0, metrics.Attempted);
        Assert.Equal(1, metrics.SuccessRate);
        Assert.Empty(metrics.AbortHistogram());
        Assert.Equal(0, metrics.OverlapAt(0.99));
    }

    [Fact]
    public void Committed_and_aborted_transfers_are_counted_apart() {
        var metrics = new TransferMetrics();

        metrics.Record(Committed(TimeSpan.FromSeconds(4)));
        metrics.Record(Committed(TimeSpan.FromSeconds(6)));
        metrics.Record(AbortedWith(TransferAbort.TargetNeverReady));

        Assert.Equal(2, metrics.Committed);
        Assert.Equal(1, metrics.Aborted);
        Assert.Equal(3, metrics.Attempted);
        Assert.Equal(2d / 3, metrics.SuccessRate, 6);
    }

    /// <summary>
    ///     The histogram is what says <em>which</em> thing broke: a new tallest bar is the answer,
    ///     and the reasons are not interchangeable.
    /// </summary>
    [Fact]
    public void The_abort_histogram_is_worst_first_and_omits_what_never_happened() {
        var metrics = new TransferMetrics();

        for (var index = 0; index < 3; index++) {
            metrics.Record(AbortedWith(TransferAbort.ClientNeverArrived));
        }

        metrics.Record(AbortedWith(TransferAbort.LeaseLost));
        metrics.Record(AbortedWith(TransferAbort.LeaseLost));
        metrics.Record(AbortedWith(TransferAbort.TicketExpired));

        var histogram = metrics.AbortHistogram();

        Assert.Equal(
            [
                (TransferAbort.ClientNeverArrived, 3),
                (TransferAbort.LeaseLost, 2),
                (TransferAbort.TicketExpired, 1)
            ],
            histogram
        );

        Assert.Equal(3, metrics.AbortsFor(TransferAbort.ClientNeverArrived));
        Assert.Equal(0, metrics.AbortsFor(TransferAbort.HandoffLost));
        Assert.DoesNotContain(histogram, entry => entry.Reason == TransferAbort.None);
    }

    /// <summary>
    ///     Nearest-rank, the same definition <c>RealmHealth</c> uses — so a fleet's two tail numbers
    ///     cannot mean two different things.
    /// </summary>
    [Fact]
    public void The_overlap_percentile_is_nearest_rank() {
        var metrics = new TransferMetrics();

        // 98 fast transfers and two slow ones: the p99 of a hundred samples is the 99th, which is the
        // first slow one — a mean would report 3.7 s and say nothing is wrong.
        for (var index = 0; index < 98; index++) {
            metrics.Record(Committed(TimeSpan.FromSeconds(3)));
        }

        metrics.Record(Committed(TimeSpan.FromSeconds(40)));
        metrics.Record(Committed(TimeSpan.FromSeconds(41)));

        Assert.Equal(40_000, metrics.OverlapAt(0.99));
        Assert.Equal(3_000, metrics.OverlapAt(0.5));
        Assert.Equal(41_000, metrics.OverlapAt(1));
    }

    [Fact]
    public void The_commit_latency_is_the_whole_transfer_not_only_the_overlap() {
        var metrics = new TransferMetrics();

        metrics.Record(Committed(TimeSpan.FromSeconds(4)));

        // Started at Noon, acknowledged four seconds after the overlap began one second in.
        Assert.Equal(5_000, metrics.CommitLatencyAt(0.99));
        Assert.Equal(4_000, metrics.OverlapAt(0.99));
    }

    [Fact]
    public void Prediction_resets_are_the_clients_own_report() {
        var metrics = new TransferMetrics();

        metrics.RecordResets(3);
        metrics.RecordResets(2);
        metrics.RecordResets(-1);

        Assert.Equal(5, metrics.PredictionResets);
    }

    [Fact]
    public void A_transfer_still_in_flight_is_recorded_as_neither() {
        var metrics = new TransferMetrics();

        metrics.Record(new(new(Guid.NewGuid(), Guid.NewGuid()), "maps/divinity", Noon));

        Assert.Equal(0, metrics.Attempted);
    }

    static SourceTransfer Committed(TimeSpan overlap) {
        var transfer = new SourceTransfer(new(Guid.NewGuid(), Guid.NewGuid()), "maps/divinity", Noon);

        transfer.Placed(Target, Prepare(), 5, Noon);
        transfer.TargetReady(Noon.AddSeconds(1));
        transfer.ClientReady(Noon.AddSeconds(1) + overlap, 4_200);
        transfer.LeaseTaken(5, Noon.AddSeconds(1) + overlap);
        transfer.HandoffAcknowledged(Noon.AddSeconds(1) + overlap);

        return transfer;
    }

    static SourceTransfer AbortedWith(TransferAbort reason) {
        var transfer = new SourceTransfer(new(Guid.NewGuid(), Guid.NewGuid()), "maps/divinity", Noon);

        transfer.Stop(reason, Noon.AddSeconds(1));

        return transfer;
    }

    static TransferPrepare Prepare() {
        using var signer = new TransferTicketSigner(Encoding.UTF8.GetBytes("a cluster key that is long enough."));

        var ticket = signer.Sign(
            new() {
                Player = new(Guid.NewGuid(), Guid.NewGuid()),
                Target = Target,
                Endpoint = new("realm.example", 30001),
                LeaseEpoch = 5,
                Expires = Noon.AddDays(1)
            }
        );

        return new(ticket.Encode(), ticket.Endpoint, Target, new("0.1.0", 0xC0FFEE), 900);
    }
}

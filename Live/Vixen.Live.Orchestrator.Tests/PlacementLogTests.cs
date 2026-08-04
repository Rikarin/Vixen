// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Live.Cluster;
using Xunit;

namespace Vixen.Live.Orchestration.Tests;

/// <summary>
///     Doc 27 § Diagnostics: <i>"why THIS player went to THAT shard … without it, placement
///     complaints are unanswerable."</i>
/// </summary>
public class PlacementLogTests {
    static readonly DateTimeOffset Noon = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_decision_is_kept_and_can_be_asked_about() {
        var log = new PlacementLog();
        var who = Somebody();
        var shard = ShardId.New();

        log.Record(new(who, PlaceStatus.Placed, shard, "placed on the one with your guild on it", Noon));

        Assert.True(log.TryGet(who, out var record));
        Assert.Equal(shard, record!.Shard);
        Assert.Contains("your guild", log.Explain(who), StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ "Nothing is held" and "they were refused" must not read the same: one sends an operator
    ///     looking for a different map, the other for a reason.
    /// </summary>
    [Fact]
    public void A_player_nobody_remembers_reads_differently_from_one_who_was_refused() {
        var log = new PlacementLog();
        var refused = Somebody();

        log.Record(new(refused, PlaceStatus.Refused, ShardId.None, "every shard is full", Noon));

        Assert.Contains("Refused", log.Explain(refused), StringComparison.Ordinal);
        Assert.Contains("every shard is full", log.Explain(refused), StringComparison.Ordinal);

        var stranger = log.Explain(Somebody());

        Assert.Contains("Nothing is held", stranger, StringComparison.Ordinal);
        Assert.DoesNotContain("Refused", stranger, StringComparison.Ordinal);
    }

    /// <summary>The question is always "why am I here now", never "where was I on Tuesday".</summary>
    [Fact]
    public void A_second_decision_replaces_the_first() {
        var log = new PlacementLog();
        var who = Somebody();

        log.Record(new(who, PlaceStatus.Refused, ShardId.None, "full", Noon));
        log.Record(new(who, PlaceStatus.Placed, ShardId.New(), "room now", Noon.AddMinutes(1)));

        Assert.Equal(1, log.Count);
        Assert.Contains("room now", log.Explain(who), StringComparison.Ordinal);
        Assert.DoesNotContain("full", log.Explain(who), StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ Unbounded would be a memory leak with a plausible excuse, worst on the busiest map — and
    ///     this lives in a grain in a process meant to run for weeks.
    /// </summary>
    [Fact]
    public void The_log_is_bounded_and_evicts_the_oldest() {
        var log = new PlacementLog { Capacity = 4 };
        var first = Somebody();

        log.Record(new(first, PlaceStatus.Placed, ShardId.New(), "first", Noon));

        for (var index = 0; index < 4; index++) {
            log.Record(new(Somebody(), PlaceStatus.Placed, ShardId.New(), "later", Noon.AddMinutes(index + 1)));
        }

        Assert.Equal(4, log.Count);
        Assert.False(log.TryGet(first, out _));
    }

    /// <summary>
    ///     ⚠ The case that would make the bound useless: a client retrying a `Starting` answer asks
    ///     several times a second, and each ask is the same player. Re-recording must not enqueue them
    ///     again, or one impatient client evicts the whole map's history in a minute.
    /// </summary>
    [Fact]
    public void One_player_asking_repeatedly_does_not_evict_everybody_else() {
        var log = new PlacementLog { Capacity = 4 };
        var kept = Somebody();
        var impatient = Somebody();

        log.Record(new(kept, PlaceStatus.Placed, ShardId.New(), "kept", Noon));

        for (var index = 0; index < 50; index++) {
            log.Record(new(impatient, PlaceStatus.Starting, ShardId.None, "a shard is starting", Noon.AddSeconds(index)));
        }

        Assert.Equal(2, log.Count);
        Assert.True(log.TryGet(kept, out _));
    }

    [Fact]
    public void Recent_comes_back_newest_first() {
        var log = new PlacementLog();

        var oldest = Somebody();
        var newest = Somebody();

        log.Record(new(oldest, PlaceStatus.Placed, ShardId.New(), "", Noon));
        log.Record(new(newest, PlaceStatus.Placed, ShardId.New(), "", Noon.AddMinutes(5)));

        Assert.Equal([newest, oldest], log.Recent().Select(record => record.Player));
    }

    /// <summary>
    ///     ⚠ Every answer is kept, not only the refusals. "Why am I on this shard rather than my
    ///     guild's" is a complaint about a placement that succeeded.
    /// </summary>
    [Fact]
    public void A_map_remembers_the_answer_it_gave_whichever_it_was() {
        var key = new ShardKey("maps/queensdale", "eu", new("0.1.0", 0xC0FFEE));
        var map = new MapCoordinator(key, PlacementWeights.Default, new());
        var who = Somebody();

        // No shards at all, so this is a refusal — and it is still remembered.
        map.Place(new(who, key, default, default, "en-GB", ShardId.None), Noon);

        Assert.True(map.Placements.TryGet(who, out var record));
        Assert.Equal(PlaceStatus.Refused, record!.Status);
        Assert.NotEmpty(record.Explanation);
    }

    static PlayerKey Somebody() => new(Guid.NewGuid(), Guid.NewGuid());
}

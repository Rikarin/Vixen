// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Xunit;

namespace Vixen.Live.Cluster.Tests;

/// <summary>Every value that crosses a grain call, through a real Orleans serializer.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the gate on <c>Surrogates.cs</c>, and without it the failure is a runtime one
///         on the first call that carries the type.</b> <c>Vixen.Live.Abstractions</c> cannot carry
///         <c>[GenerateSerializer]</c> — it is the assembly an iOS NativeAOT client transitively
///         references — so every type in the vocabulary needs a surrogate here, and nothing about
///         adding one to the vocabulary makes the compiler ask for it.
///     </para>
///     <para>
///         A real serializer rather than a check that a converter exists: the converter compiling is
///         not the question, and a surrogate that drops a field round-trips to something subtly
///         wrong rather than throwing.
///     </para>
/// </remarks>
public sealed class ClusterSerializationTests {
    static readonly Serializer Serializer = new ServiceCollection()
        .AddSerializer(builder => builder.AddAssembly(typeof(ShardIdConverter).Assembly))
        .BuildServiceProvider()
        .GetRequiredService<Serializer>();

    static T RoundTrip<T>(T value) => Serializer.Deserialize<T>(Serializer.SerializeToArray(value));

    [Fact]
    public void IdentitiesSurvive() {
        var shard = ShardId.New();
        var player = new PlayerKey(Guid.NewGuid(), Guid.NewGuid());
        var instance = new RealmInstanceId("realm-3-7f2a");

        Assert.Equal(shard, RoundTrip(shard));
        Assert.Equal(player, RoundTrip(player));
        Assert.Equal(instance, RoundTrip(instance));
    }

    [Fact]
    public void TheShardVocabularySurvives() {
        var key = new ShardKey("maps/queensdale", "eu-west", new("0.1.0", 0xC0FFEE));
        var endpoint = new RealmEndpoint("10.0.0.4", 30001);
        var capacity = new ShardCapacity(100, 120);

        Assert.Equal(key, RoundTrip(key));
        Assert.Equal(key.Version, RoundTrip(key.Version));
        Assert.Equal(endpoint, RoundTrip(endpoint));
        Assert.Equal(capacity, RoundTrip(capacity));
    }

    [Fact]
    public void TheEnumsSurvive() {
        Assert.Equal(ShardState.Draining, RoundTrip(ShardState.Draining));
        Assert.Equal(ShardKind.Persistent, RoundTrip(ShardKind.Persistent));
        Assert.Equal(TransferReadiness.Blocked, RoundTrip(TransferReadiness.Blocked));
    }

    [Fact]
    public void ADefaultValueSurvivesAsADefault() {
        // The case a surrogate gets wrong quietly: `default` carries a null string, and a converter
        // that assumed otherwise would round-trip nothing into something.
        Assert.Equal(default(ShardId), RoundTrip(default(ShardId)));
        Assert.Equal(default(RealmEndpoint), RoundTrip(default(RealmEndpoint)));
        Assert.Equal(default(ShardKey), RoundTrip(default(ShardKey)));
        Assert.Equal(default(RealmInstanceId), RoundTrip(default(RealmInstanceId)));
    }

    [Fact]
    public void TheRecordsAGrainExchangesSurvive() {
        var request = new PlaceRequest(
            new(Guid.NewGuid(), Guid.NewGuid()),
            new("maps/queensdale", "eu", new("0.1.0", 1)),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "en-GB",
            ShardId.New()
        );

        var result = new PlaceResult(PlaceStatus.Placed, ShardId.New(), new("10.0.0.4", 7777), "because");
        var beat = new ShardHeartbeat(42, 3.5, 1.2, 2, DateTimeOffset.UnixEpoch);
        var lease = new PlayerLease(true, 7, ShardId.New(), DateTimeOffset.UnixEpoch);

        var report = new ShardReport(
            ShardId.New(),
            request.Key,
            ShardState.Ready,
            new("10.0.0.4", 7777),
            new("realm-1"),
            42,
            new(100, 120),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch
        );

        Assert.Equal(request, RoundTrip(request));
        Assert.Equal(result, RoundTrip(result));
        Assert.Equal(beat, RoundTrip(beat));
        Assert.Equal(lease, RoundTrip(lease));
        Assert.Equal(report, RoundTrip(report));
    }

    /// <summary>
    ///     The account vocabulary G8 needed, round-tripped for the reason this file exists: a type
    ///     added to the contract and not to the surrogates fails at the first grain call that carries
    ///     it, not at compile time.
    /// </summary>
    /// <remarks>
    ///     ⚠ These two need no surrogate, and that is a property worth pinning rather than assuming.
    ///     They are strings, ints and immutable arrays — <c>IAccountGrain</c> deliberately knows
    ///     nothing about collectibles, so nothing from <c>Vixen.Live.Abstractions</c> crosses in them.
    /// </remarks>
    [Fact]
    public void AnAccountsHoldingsCrossAGrainCall() {
        var holdings = new AccountHoldings(
            [new("collect/mount/gryphon", "Loot", "boss/skarr", 1), new("collect/pet/cat", "Quest", "", 2)],
            ["achieve/stabled"],
            10,
            3
        );

        // ⚠ Compared whole, which only works because the equality is hand-written: a record's
        // generated Equals compares an ImmutableArray by *reference*, so this line passing is itself
        // the assertion that the trap doc 27 records for RealmEndpoint has been handled here too.
        Assert.Equal(holdings, RoundTrip(holdings));

        var guild = new GuildRecord(
            "guilds/charter",
            "The Fellowship",
            [new(new(Guid.NewGuid(), Guid.NewGuid()), 0, DateTimeOffset.UnixEpoch)],
            ImmutableDictionary<int, string>.Empty.Add(1, "Champion"),
            DateTimeOffset.UnixEpoch,
            4
        );

        Assert.Equal(guild, RoundTrip(guild));

        var saved = new InstanceRecord(
            "instances/barrowdeep",
            "heroic",
            [new(new(Guid.NewGuid(), Guid.NewGuid()), DateTimeOffset.UnixEpoch)],
            ["bosses/gravewarden"],
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddDays(7),
            false,
            2
        );

        Assert.Equal(saved, RoundTrip(saved));

        var formed = new QueueMatch(
            Guid.NewGuid(),
            [new(["1"]), new(["2"])],
            0.94,
            DateTimeOffset.UnixEpoch,
            false
        );

        Assert.Equal(formed, RoundTrip(formed));

        var ticket = new QueueTicket(
            "1",
            new([new(Guid.NewGuid(), Guid.NewGuid())], 1500d, 200d, ["role/tank"], DateTimeOffset.UnixEpoch),
            QueueTicketState.Waiting,
            Guid.Empty
        );

        Assert.Equal(ticket.Id, RoundTrip(ticket).Id);
        Assert.Equal(ticket.Entry.Players, RoundTrip(ticket).Entry.Players);
    }

    [Fact]
    public void AGrainKeyIsOneSpellingOfOneIdentity() {
        var key = new ShardKey("maps/queensdale", "eu", new("0.1.0", 0xC0FFEE));

        // Two spellings of one identity are two grains — two fleets for one map, each unaware of the
        // other, presenting as players who cannot find each other.
        Assert.Equal(Keys.ForMap(key), Keys.ForMap(RoundTrip(key)));
        Assert.Equal("maps/queensdale|eu|0.1.0+0000000000c0ffee", Keys.ForMap(key));
    }
}

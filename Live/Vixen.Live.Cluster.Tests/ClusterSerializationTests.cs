// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

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

    [Fact]
    public void AGrainKeyIsOneSpellingOfOneIdentity() {
        var key = new ShardKey("maps/queensdale", "eu", new("0.1.0", 0xC0FFEE));

        // Two spellings of one identity are two grains — two fleets for one map, each unaware of the
        // other, presenting as players who cannot find each other.
        Assert.Equal(Keys.ForMap(key), Keys.ForMap(RoundTrip(key)));
        Assert.Equal("maps/queensdale|eu|0.1.0+0000000000c0ffee", Keys.ForMap(key));
    }
}

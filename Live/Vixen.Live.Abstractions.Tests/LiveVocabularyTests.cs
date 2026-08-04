// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Live.Tests;

/// <summary>The small value types, and the defaults that have to be harmless.</summary>
/// <remarks>
///     Every one of these is a struct, so <c>default</c> exists whether or not anybody meant it —
///     which is doc 27's own hazard list in miniature. A <c>default</c> that read as "a valid shard
///     with an empty map" would place players onto nothing; each type answers <c>IsValid</c> instead.
/// </remarks>
public sealed class LiveVocabularyTests {
    [Fact]
    public void DefaultsAreInvalidRatherThanEmpty() {
        Assert.False(default(ShardId).IsValid);
        Assert.False(default(RealmInstanceId).IsValid);
        Assert.False(default(PlayerKey).IsValid);
        Assert.False(default(RealmVersion).IsValid);
        Assert.False(default(RealmEndpoint).IsValid);
        Assert.False(default(ShardKey).IsValid);
        Assert.False(default(ShardCapacity).IsValid);
        Assert.False(new RealmSpec().IsValid);
    }

    [Fact]
    public void DefaultsPrintSomethingAPersonCanRead() {
        // These end up in logs during exactly the incident where they are default, so "nobody" beats
        // "00000000-0000-0000-0000-000000000000".
        Assert.Equal("no shard", default(ShardId).ToString());
        Assert.Equal("no instance", default(RealmInstanceId).ToString());
        Assert.Equal("nobody", default(PlayerKey).ToString());
        Assert.Equal("no version", default(RealmVersion).ToString());
        Assert.Equal("nowhere", default(RealmEndpoint).ToString());
        Assert.Equal("no map", default(ShardKey).ToString());
    }

    [Fact]
    public void APlayerKeyNeedsBothHalves() {
        // A lease is per character: two characters on one account are two sets of inventory, and
        // keying by account alone would make playing the second a duplication bug (ADR-021).
        Assert.False(new PlayerKey(Guid.NewGuid(), Guid.Empty).IsValid);
        Assert.False(new PlayerKey(Guid.Empty, Guid.NewGuid()).IsValid);
        Assert.True(new PlayerKey(Guid.NewGuid(), Guid.NewGuid()).IsValid);
    }

    [Fact]
    public void APlayerKeySurvivesTheRoundTrip() {
        var player = new PlayerKey(Guid.NewGuid(), Guid.NewGuid());

        Assert.True(PlayerKey.TryParse(player.ToString(), out var read));
        Assert.Equal(player, read);

        Assert.False(PlayerKey.TryParse(null, out _));
        Assert.False(PlayerKey.TryParse("no-slash-here", out _));
        Assert.False(PlayerKey.TryParse("not-a-guid/also-not", out _));
    }

    [Fact]
    public void AVersionAdmitsOnlyItself() {
        var version = new RealmVersion("0.1.0", 0xC0FFEE);

        Assert.True(version.Admits(new("0.1.0", 0xC0FFEE)));

        // Both halves. A build that matches with a different catalog is the case ADR-022 exists for:
        // the client has not fetched the content update yet, and it is routed rather than refused.
        Assert.False(version.Admits(new("0.1.0", 0xC0FFEF)));
        Assert.False(version.Admits(new("0.1.1", 0xC0FFEE)));
    }

    [Fact]
    public void AVersionSurvivesTheRoundTrip() {
        var version = new RealmVersion("0.1.0-rc.2+meta", 0xDEADBEEFCAFEF00D);

        Assert.True(RealmVersion.TryParse(version.ToString(), out var read));
        Assert.Equal(version, read);
    }

    [Theory]
    [InlineData("10.0.0.4:7777", "10.0.0.4", 7777)]
    [InlineData("realm-3.eu.example.com:30001", "realm-3.eu.example.com", 30001)]
    [InlineData("[2001:db8::1]:7777", "[2001:db8::1]", 7777)]
    [InlineData("10.0.0.4:0", "10.0.0.4", 0)]
    public void AnEndpointParsesOnTheLastColon(string text, string host, int port) {
        Assert.True(RealmEndpoint.TryParse(text, out var endpoint));
        Assert.Equal(host, endpoint.Host);
        Assert.Equal(port, endpoint.Port);
        Assert.Equal(text, endpoint.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10.0.0.4")]
    [InlineData(":7777")]
    [InlineData("10.0.0.4:not-a-port")]
    [InlineData("10.0.0.4:70000")]
    [InlineData("10.0.0.4:-1")]
    public void AnythingElseIsNotAnEndpoint(string? text) {
        Assert.False(RealmEndpoint.TryParse(text, out var endpoint));
        Assert.Equal(RealmEndpoint.None, endpoint);
    }

    [Fact]
    public void AnUnboundEndpointIsBoundByTheBackend() {
        var unbound = new RealmEndpoint("10.0.0.4", 0);

        Assert.True(unbound.IsUnbound);
        Assert.False(unbound.IsValid);

        // And an endpoint with nothing in it at all is a request rather than nonsense: placing onto a
        // Kubernetes scheduler, the orchestrator cannot know the node, let alone its external address.
        Assert.True(default(RealmEndpoint).IsUnbound);

        var bound = unbound.On(30001);

        Assert.True(bound.IsValid);
        Assert.False(bound.IsUnbound);
        Assert.Equal("10.0.0.4", bound.Host);
    }

    [Fact]
    public void CapacityIsTwoQuestionsRatherThanOne() {
        var capacity = new ShardCapacity(100, 120);

        Assert.True(capacity.Admits(119));
        Assert.False(capacity.Admits(120));

        // The gap between soft and hard is what a party arriving together fits into.
        Assert.Equal(1.0, capacity.FillAt(100));
        Assert.Equal(1.2, capacity.FillAt(120), 6);
        Assert.Equal(0.0, capacity.FillAt(0));
    }

    [Fact]
    public void ReadySignalsCarryTheEndpointTheRealmActuallyBound() {
        var endpoint = new RealmEndpoint("10.0.0.4", 30001);

        Assert.True(RealmSignals.TryReadReady(RealmSignals.FormatReady(endpoint), out var read));
        Assert.Equal(endpoint, read);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("info: the realm loaded maps/queensdale")]
    [InlineData("vixen-realm ready")]
    [InlineData("vixen-realm ready nowhere")]
    [InlineData("vixen-realm ready 10.0.0.4:0")]
    public void OrdinaryOutputIsNotAReadySignal(string? line) {
        Assert.False(RealmSignals.TryReadReady(line, out _));
    }

    [Theory]
    [InlineData("vixen-realm drain", RealmSignals.Drain)]
    [InlineData("  vixen-realm stop  ", RealmSignals.Stop)]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("quit", "")]
    [InlineData("vixen-realm ready 10.0.0.4:1", "")]
    public void OnlyTheTwoCommandsAreCommands(string? line, string expected) =>
        Assert.Equal(expected, RealmSignals.ReadCommand(line));
}

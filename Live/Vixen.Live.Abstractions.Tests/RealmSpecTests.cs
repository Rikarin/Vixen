// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Live.Tests;

/// <summary>The one string that crosses a process boundary, and what happens when it is wrong.</summary>
/// <remarks>
///     A spec that round-trips is the whole contract between a placement backend and a realm, so the
///     interesting tests are the ones where somebody hands it something that is not a spec: that is
///     the path a launcher's bug takes, and every one of them has to end in a sentence an operator
///     can act on rather than in an exception with a stack trace from a parser.
/// </remarks>
public sealed class RealmSpecTests {
    static RealmSpec Sample() =>
        new() {
            Shard = new(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e")),
            Key = new("maps/queensdale", "eu-west", new("0.1.0", 0xC0FFEE)),
            Kind = ShardKind.Public,
            Endpoint = new("10.0.0.4", 7777),
            Capacity = new(100, 120),
            TickRate = 30,
            Seed = 4242,
            ClusterEndpoint = "orleans://cluster:30000"
        };

    [Fact]
    public void ASpecSurvivesTheRoundTrip() {
        var spec = Sample();

        Assert.True(RealmSpec.TryDecode(spec.Encode(), out var read, out var error));
        Assert.Equal("", error);
        Assert.Equal(spec, read);
    }

    [Fact]
    public void OptionsSurviveAndStayOutOfTheEngineNamespace() {
        var spec = Sample() with {
            Options = new Dictionary<string, string>(StringComparer.Ordinal) {
                ["difficulty"] = "veteran",
                ["event"] = "halloween"
            }
        };

        var encoded = spec.Encode();

        // Prefixed on the wire so a game's option can never collide with a field this record grows
        // later, and unprefixed again on the way back so the game never has to know that.
        Assert.Contains("x-difficulty=veteran", encoded, StringComparison.Ordinal);
        Assert.True(RealmSpec.TryDecode(encoded, out var read, out _));
        Assert.Equal("veteran", read!.Options["difficulty"]);
        Assert.Equal("halloween", read.Options["event"]);
    }

    [Theory]
    [InlineData("maps/the;semicolon")]
    [InlineData("maps/the=equals")]
    [InlineData("maps/the%percent")]
    [InlineData("maps/all%3Bof%3Dthem%25")]
    public void TheSeparatorsSurviveBeingInsideAValue(string map) {
        var spec = Sample() with { Key = new(map, "eu", new("0.1.0", 1)) };

        Assert.True(RealmSpec.TryDecode(spec.Encode(), out var read, out _));
        Assert.Equal(map, read!.Key.Map);
    }

    [Fact]
    public void AnUnboundEndpointIsAValidSpec() {
        // What the orchestrator hands the backend: it knows which node, the backend knows which
        // ports are free.
        var spec = Sample() with { Endpoint = new("10.0.0.4", 0) };

        Assert.True(spec.IsValid);
        Assert.True(spec.Endpoint.IsUnbound);
        Assert.True(RealmSpec.TryDecode(spec.Encode(), out var read, out _));
        Assert.True(read!.Endpoint.IsUnbound);
    }

    [Theory]
    [InlineData("", "it is empty")]
    [InlineData("nonsense", "not a key=value pair")]
    [InlineData("map=a;map=b", "appears twice")]
    public void MalformedInputIsRefusedWithAReason(string text, string expected) {
        Assert.False(RealmSpec.TryDecode(text, out var spec, out var error));
        Assert.Null(spec);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("shard", "`shard` is missing")]
    [InlineData("map", "`map` is missing")]
    [InlineData("content", "`content` is missing")]
    [InlineData("kind", "`kind` is missing")]
    [InlineData("port", "`port` is missing")]
    [InlineData("soft", "`soft` and `hard` are missing")]
    [InlineData("tick", "`tick` is missing")]
    [InlineData("seed", "`seed` is missing")]
    public void EveryRequiredFieldIsNamedWhenItIsMissing(string field, string expected) {
        var mangled = string.Join(
            ';',
            Sample().Encode().Split(';').Where(pair => !pair.StartsWith(field + "=", StringComparison.Ordinal))
        );

        Assert.False(RealmSpec.TryDecode(mangled, out _, out var error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void ACapacityThatCouldNotBeHonouredIsRefused() {
        var spec = Sample() with { Capacity = new(120, 100) };

        Assert.False(spec.IsValid);
        Assert.False(RealmSpec.TryDecode(spec.Encode(), out _, out var error));
        Assert.Contains("not one a shard could honour", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheArgumentWinsOverTheEnvironment() {
        var fromArgument = Sample();
        var fromEnvironment = Sample() with { Key = new("maps/stale", "eu", new("0.0.1", 1)) };

        Assert.True(
            RealmSpec.TryRead(
                fromArgument.ToCommandLine(),
                _ => fromEnvironment.Encode(),
                out var read,
                out _
            )
        );

        // A launcher that sets both meant the argument: the environment is what a pod template
        // inherits, and inheriting a stale one is the accident this order prevents.
        Assert.Equal("maps/queensdale", read!.Key.Map);
    }

    [Fact]
    public void TheEnvironmentIsReadWhenThereIsNoArgument() {
        var spec = Sample();

        Assert.True(
            RealmSpec.TryRead(
                ["--vixen-variant", "Server"],
                name => name == RealmSpec.EnvironmentVariable ? spec.Encode() : null,
                out var read,
                out _
            )
        );

        Assert.Equal(spec, read);
    }

    [Fact]
    public void AProcessThatIsNotARealmIsToldSo() {
        Assert.False(RealmSpec.TryRead(["--help"], _ => null, out var spec, out var error));
        Assert.Null(spec);
        Assert.Contains(RealmSpec.ArgumentName, error, StringComparison.Ordinal);
        Assert.Contains(RealmSpec.EnvironmentVariable, error, StringComparison.Ordinal);
    }

    [Fact]
    public void ATrailingArgumentNameWithNoValueIsRefusedRatherThanIndexedPast() {
        Assert.False(RealmSpec.TryRead([RealmSpec.ArgumentName], _ => null, out _, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void TheSceneNameIsTheAddressLeaf() {
        // Doc 27 § The scene-management join: the wire says a scene by the hash of its NAME, and the
        // name is the last segment of the address the content build published it under.
        Assert.Equal("queensdale", new ShardKey("maps/queensdale", "eu", default).SceneName);
        Assert.Equal("queensdale", new ShardKey("queensdale", "eu", default).SceneName);
        Assert.Equal("dale", new ShardKey("a/b/c/dale", "eu", default).SceneName);
    }
}

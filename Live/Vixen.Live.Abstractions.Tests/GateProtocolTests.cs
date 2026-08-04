// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Xunit;

namespace Vixen.Live.Tests;

/// <summary>
///     The service plane's wire shapes. One assembly holds them so that the gate and the client
///     cannot disagree — the failure this prevents presents as a client that cannot log in after a
///     server deploy.
/// </summary>
public class GateProtocolTests {
    static readonly DateTimeOffset Noon = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_play_response_round_trips() {
        var sent = new PlayResponse(
            PlayStatus.Placed,
            "realm.example:30000",
            "player=…;target=…",
            "9c1f…",
            "the only shard, and it has room",
            TimeSpan.Zero
        );

        var json = JsonSerializer.Serialize(sent, GateJson.Default.PlayResponse);

        Assert.Equal(sent, JsonSerializer.Deserialize(json, GateJson.Default.PlayResponse));
    }

    [Fact]
    public void A_sign_in_round_trips() {
        var sent = new SignInResponse("a.b.c", Guid.NewGuid(), Noon);
        var json = JsonSerializer.Serialize(sent, GateJson.Default.SignInResponse);

        Assert.Equal(sent, JsonSerializer.Deserialize(json, GateJson.Default.SignInResponse));
    }

    [Fact]
    public void A_character_list_round_trips_by_content() {
        var sent = new CharacterList([new(Guid.NewGuid(), "Bruna", "eu", "maps/queensdale", Noon)]);
        var json = JsonSerializer.Serialize(sent, GateJson.Default.CharacterList);
        var read = JsonSerializer.Deserialize(json, GateJson.Default.CharacterList);

        Assert.NotNull(read);
        Assert.Equal(sent.Characters, read.Characters);
    }

    /// <summary>
    ///     The version pair already has one canonical spelling that a command line, a log line and a
    ///     grain key all use. A second spelling as an object with two fields would be a second thing
    ///     to keep in step.
    /// </summary>
    [Fact]
    public void A_version_crosses_as_its_canonical_string() {
        var sent = new CatalogResponse(new("0.1.0", 0xC0FFEE), "https://content.example", ["maps/queensdale"]);
        var json = JsonSerializer.Serialize(sent, GateJson.Default.CatalogResponse);

        // ⚠ `+` arrives as `+`, and that is the strict encoder doing its job rather than a bug.
        // Relaxing it would make the wire prettier and make a version pasted into a web page an
        // injection surface; the client never reads this by eye, and the converter reads it back.
        Assert.Contains("\"0.1.0\\u002B0000000000c0ffee\"", json, StringComparison.Ordinal);

        var read = JsonSerializer.Deserialize(json, GateJson.Default.CatalogResponse);

        Assert.NotNull(read);
        Assert.Equal(sent.Version, read.Version);
        Assert.Equal(sent.Maps, read.Maps);
    }

    [Fact]
    public void A_version_that_is_nothing_reads_back_as_nothing() {
        var json = JsonSerializer.Serialize(
            new CatalogResponse(RealmVersion.None, "", []),
            GateJson.Default.CatalogResponse
        );

        var read = JsonSerializer.Deserialize(json, GateJson.Default.CatalogResponse);

        Assert.NotNull(read);
        Assert.Equal(RealmVersion.None, read.Version);
    }

    /// <summary>Camel case, because the other end of this is a web client as often as a game.</summary>
    [Fact]
    public void The_wire_is_camel_case() {
        var json = JsonSerializer.Serialize(
            new PlayRequest(Guid.Empty, "maps/queensdale", RealmVersion.None, "en-GB", default, default),
            GateJson.Default.PlayRequest
        );

        Assert.Contains("\"character\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Character\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ Every status a client may be told has to be one it can render. <c>UpdateRequired</c> is
    ///     the one that is easy to miss and the one ADR-022's whole upgrade story rests on: a client
    ///     that renders it as a failure turns a rolling upgrade back into a maintenance window.
    /// </summary>
    [Fact]
    public void Every_play_status_is_named() {
        Assert.Equal(
            [PlayStatus.Placed, PlayStatus.Starting, PlayStatus.Refused, PlayStatus.UpdateRequired],
            Enum.GetValues<PlayStatus>()
        );
    }

    [Fact]
    public void A_problem_round_trips() {
        var sent = new GateProblem("name-taken", "`Bruna` is already somebody.");
        var json = JsonSerializer.Serialize(sent, GateJson.Default.GateProblem);

        Assert.Equal(sent, JsonSerializer.Deserialize(json, GateJson.Default.GateProblem));
    }

    [Fact]
    public void A_pushed_event_round_trips() {
        var sent = new GateEvent("catalog", "0.1.1+deadbeef", Noon);
        var json = JsonSerializer.Serialize(sent, GateJson.Default.GateEvent);

        Assert.Equal(sent, JsonSerializer.Deserialize(json, GateJson.Default.GateEvent));
    }
}

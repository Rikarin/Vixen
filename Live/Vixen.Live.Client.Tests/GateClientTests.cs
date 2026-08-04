// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Xunit;

namespace Vixen.Live.Client.Tests;

/// <summary>The four calls, and the answers a client has to be able to tell apart.</summary>
public class GateClientTests {
    static readonly DateTimeOffset Noon = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    static readonly RealmVersion Running = new("0.1.0", 0xC0FFEE);

    [Fact]
    public async Task The_catalog_needs_no_session() {
        using var gate = new FakeGate().Answers(
            new CatalogResponse(Running, "https://content.example", ["maps/queensdale"]),
            GateJson.Default.CatalogResponse
        );

        using var http = gate.Client;
        var client = new GateClient(http);

        var answer = await client.CatalogAsync(TestContext.Current.CancellationToken);

        Assert.True(answer.Ok);
        Assert.Equal(Running, answer.Value!.Version);
        Assert.Null(Assert.Single(gate.Seen).Authorization);
    }

    [Fact]
    public async Task Signing_in_remembers_the_token_and_sends_it_afterwards() {
        using var gate = new FakeGate()
            .Answers(new SignInResponse("a.b.c", Guid.NewGuid(), Noon), GateJson.Default.SignInResponse)
            .Answers(new CharacterList([]), GateJson.Default.CharacterList);

        using var http = gate.Client;
        var client = new GateClient(http);

        Assert.False(client.SignedIn);

        await client.SignInAsync("development", "alice", TestContext.Current.CancellationToken);

        Assert.True(client.SignedIn);

        await client.CharactersAsync(TestContext.Current.CancellationToken);

        Assert.Null(gate.Seen[0].Authorization);
        Assert.Equal("Bearer a.b.c", gate.Seen[1].Authorization);
    }

    /// <summary>
    ///     A stateless token cannot be revoked, so forgetting it locally is all signing out can be —
    ///     and the client says so rather than implying the session is over.
    /// </summary>
    [Fact]
    public async Task Signing_out_forgets_the_token_locally() {
        using var gate = new FakeGate().Answers(
            new SignInResponse("a.b.c", Guid.NewGuid(), Noon),
            GateJson.Default.SignInResponse
        );

        using var http = gate.Client;
        var client = new GateClient(http);

        await client.SignInAsync("development", "alice", TestContext.Current.CancellationToken);
        client.SignOut();

        Assert.False(client.SignedIn);
        Assert.Null(client.Authorization);
    }

    /// <summary>
    ///     A refusal is an ordinary answer. Throwing would make the happy path the only path anybody
    ///     writes, and "that name is taken" is a sentence to show rather than a stack to log.
    /// </summary>
    [Fact]
    public async Task A_refusal_is_an_answer_rather_than_an_exception() {
        using var gate = new FakeGate().Refuses(HttpStatusCode.Conflict, "name-taken", "`Bruna` is already somebody.");

        using var http = gate.Client;
        var client = new GateClient(http);

        var answer = await client.CreateCharacterAsync(
            new("Bruna", "eu", "maps/queensdale"),
            TestContext.Current.CancellationToken
        );

        Assert.False(answer.Ok);
        Assert.False(answer.Unreachable);
        Assert.Equal(409, answer.Status);
        Assert.Equal("name-taken", answer.Problem!.Code);
    }

    /// <summary>
    ///     ⚠ "The gate said no" and "the gate did not answer" want different pixels: a sentence and a
    ///     spinner. A client that showed the first for the second sends people to a support forum
    ///     over a dropped connection.
    /// </summary>
    [Fact]
    public async Task A_gate_that_cannot_be_reached_is_distinguishable_from_one_that_refused() {
        using var gate = new FakeGate().Vanishes();

        using var http = gate.Client;
        var client = new GateClient(http);

        var answer = await client.CatalogAsync(TestContext.Current.CancellationToken);

        Assert.False(answer.Ok);
        Assert.True(answer.Unreachable);
        Assert.Equal(0, answer.Status);
        Assert.Equal("unreachable", answer.Problem!.Code);
    }

    /// <summary>
    ///     A gate always answers a refusal with a `GateProblem`. Anything else on these routes is an
    ///     intermediary, and saying so is more use than reporting its HTML.
    /// </summary>
    [Fact]
    public async Task Something_that_is_not_a_gate_is_named_as_such() {
        using var gate = new FakeGate().Interferes(HttpStatusCode.BadGateway, "<html>hotel wi-fi</html>");

        using var http = gate.Client;
        var client = new GateClient(http);

        var answer = await client.CatalogAsync(TestContext.Current.CancellationToken);

        Assert.Equal(502, answer.Status);
        Assert.Equal("unexplained", answer.Problem!.Code);
        Assert.False(answer.Unreachable);
    }

    [Fact]
    public async Task Playing_returns_the_endpoint_and_the_ticket_untouched() {
        using var gate = new FakeGate().Answers(
            new PlayResponse(PlayStatus.Placed, "realm.example:30000", "a ticket", "9c1f", "it has room", TimeSpan.Zero),
            GateJson.Default.PlayResponse
        );

        using var http = gate.Client;
        var client = new GateClient(http);

        var answer = await client.PlayAsync(Play(), TestContext.Current.CancellationToken);

        Assert.Equal(PlayStatus.Placed, answer.Value!.Status);
        Assert.Equal("realm.example:30000", answer.Value.Endpoint);
        Assert.Equal("a ticket", answer.Value.Ticket);
    }

    /// <summary>
    ///     A shard coming up needs nothing from the game but patience, and the wait is the gate's own
    ///     number: how long a shard takes is a property of the fleet, and a client that guessed would
    ///     either hammer it or feel slow.
    /// </summary>
    [Fact]
    public async Task Entering_waits_out_a_shard_that_is_starting() {
        using var gate = new FakeGate()
            .Answers(Starting(), GateJson.Default.PlayResponse)
            .Answers(Starting(), GateJson.Default.PlayResponse)
            .Answers(
                new PlayResponse(PlayStatus.Placed, "realm.example:30000", "a ticket", "9c1f", "", TimeSpan.Zero),
                GateJson.Default.PlayResponse
            );

        using var http = gate.Client;
        var client = new GateClient(http);

        var answer = await client.EnterAsync(Play(), 5, TestContext.Current.CancellationToken);

        Assert.Equal(PlayStatus.Placed, answer.Value!.Status);
        Assert.Equal(3, gate.Seen.Count);
    }

    [Fact]
    public async Task Entering_gives_up_after_the_attempts_it_was_given_and_says_what_it_last_heard() {
        using var gate = new FakeGate()
            .Answers(Starting(), GateJson.Default.PlayResponse)
            .Answers(Starting(), GateJson.Default.PlayResponse);

        using var http = gate.Client;
        var client = new GateClient(http);

        var answer = await client.EnterAsync(Play(), 2, TestContext.Current.CancellationToken);

        Assert.Equal(PlayStatus.Starting, answer.Value!.Status);
        Assert.Equal(2, gate.Seen.Count);
    }

    /// <summary>
    ///     ⚠ The asymmetry is the point. Fetching a catalog is the game's asset system doing work it
    ///     must decide to do, on a connection the player may be paying for — a helper that quietly
    ///     downloaded a gigabyte would be a helper nobody could trust.
    /// </summary>
    [Fact]
    public async Task Entering_does_not_retry_an_update_and_hands_it_straight_back() {
        using var gate = new FakeGate().Answers(
            new PlayResponse(PlayStatus.UpdateRequired, "", "", "", "fetch the catalog", TimeSpan.Zero),
            GateJson.Default.PlayResponse
        );

        using var http = gate.Client;
        var client = new GateClient(http);

        var answer = await client.EnterAsync(Play(), 5, TestContext.Current.CancellationToken);

        Assert.Equal(PlayStatus.UpdateRequired, answer.Value!.Status);
        Assert.Single(gate.Seen);
    }

    [Fact]
    public async Task Entering_stops_at_a_refusal_rather_than_retrying_it() {
        using var gate = new FakeGate().Answers(
            new PlayResponse(PlayStatus.Refused, "", "", "", "the map is at MaxShards", TimeSpan.Zero),
            GateJson.Default.PlayResponse
        );

        using var http = gate.Client;
        var client = new GateClient(http);

        var answer = await client.EnterAsync(Play(), 5, TestContext.Current.CancellationToken);

        Assert.Equal(PlayStatus.Refused, answer.Value!.Status);
        Assert.Single(gate.Seen);
    }

    [Fact]
    public async Task Entering_stops_at_an_unreachable_gate() {
        using var gate = new FakeGate().Vanishes().Vanishes();

        using var http = gate.Client;
        var client = new GateClient(http);

        var answer = await client.EnterAsync(Play(), 5, TestContext.Current.CancellationToken);

        Assert.True(answer.Unreachable);
        Assert.Single(gate.Seen);
    }

    static PlayResponse Starting() =>
        new(PlayStatus.Starting, "", "", "", "a shard is being started", TimeSpan.FromMilliseconds(1));

    static PlayRequest Play() =>
        new(Guid.NewGuid(), "maps/queensdale", Running, "en-GB", default, default);
}

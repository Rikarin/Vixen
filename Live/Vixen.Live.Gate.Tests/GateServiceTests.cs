// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Live.Cluster;
using Vixen.Live.Persistence;
using Xunit;

namespace Vixen.Live.Gate.Tests;

/// <summary>Everything the gate decides, with neither a web host nor a cluster in the room.</summary>
public sealed class GateServiceTests : IDisposable {
    static readonly DateTimeOffset Noon = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    static readonly RealmVersion Running = new("0.1.0", 0xC0FFEE);

    readonly MemoryPersistence store = new();
    readonly FakeFleet fleet = new();
    readonly TestClock clock = new(Noon);
    readonly TransferTicketSigner tickets = new(Encoding.UTF8.GetBytes("a cluster key that is long enough."));
    readonly GateTokenSigner sessions = new(Encoding.UTF8.GetBytes("a gate key that is also long enough."));
    readonly GateOptions options = new() {
        Version = Running,
        Content = "https://content.example/catalog",
        Region = "eu",
        Maps = { "maps/queensdale", "maps/divinity" }
    };

    GateService Gate =>
        new(options, store, fleet, tickets, sessions, [new DevelopmentAuthority()], clock);

    public void Dispose() {
        tickets.Dispose();
        sessions.Dispose();
    }

    // ── Signing in ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_first_sign_in_makes_an_account_and_a_token_that_reads_back() {
        var answer = await Gate.SignInAsync(new(DevelopmentAuthority.Name, "alice"), TestContext.Current.CancellationToken);

        Assert.True(answer.Ok);
        Assert.Equal(200, answer.Status);
        Assert.Equal(Noon + options.SessionLifetime, answer.Value!.Expires);
        Assert.Equal(TokenStatus.Valid, Gate.Authenticate(answer.Value.Token, out var session));
        Assert.Equal(answer.Value.Account, session!.Account);
    }

    [Fact]
    public async Task Signing_in_twice_is_one_account() {
        var first = await Gate.SignInAsync(new(DevelopmentAuthority.Name, "alice"), TestContext.Current.CancellationToken);
        var again = await Gate.SignInAsync(new(DevelopmentAuthority.Name, "alice"), TestContext.Current.CancellationToken);

        Assert.Equal(first.Value!.Account, again.Value!.Account);
    }

    /// <summary>
    ///     A gate with no authority refuses everybody, which is the loud failure a deployment that
    ///     forgot to configure one deserves. It also says so, rather than answering "unauthenticated".
    /// </summary>
    [Fact]
    public async Task A_gate_with_no_authority_says_so_rather_than_refusing_quietly() {
        var bare = new GateService(options, store, fleet, tickets, sessions, [], clock);

        var answer = await bare.SignInAsync(new(DevelopmentAuthority.Name, "alice"), TestContext.Current.CancellationToken);

        Assert.False(answer.Ok);
        Assert.Equal(400, answer.Status);
        Assert.Equal("unknown-scheme", answer.Problem!.Code);
        Assert.Contains("no sign-in authority", answer.Problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_scheme_names_the_ones_that_exist() {
        var answer = await Gate.SignInAsync(new("steam", "7656…"), TestContext.Current.CancellationToken);

        Assert.Equal("unknown-scheme", answer.Problem!.Code);
        Assert.Contains(DevelopmentAuthority.Name, answer.Problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_authority_that_refuses_is_a_401_carrying_its_own_sentence() {
        var answer = await Gate.SignInAsync(new(DevelopmentAuthority.Name, "   "), TestContext.Current.CancellationToken);

        Assert.Equal(401, answer.Status);
        Assert.Equal("unauthenticated", answer.Problem!.Code);
        Assert.Contains("handle", answer.Problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_suspended_account_cannot_sign_in() {
        var first = await Gate.SignInAsync(new(DevelopmentAuthority.Name, "alice"), TestContext.Current.CancellationToken);

        await store.Accounts.SetSuspendedAsync(first.Value!.Account, true, TestContext.Current.CancellationToken);

        var again = await Gate.SignInAsync(new(DevelopmentAuthority.Name, "alice"), TestContext.Current.CancellationToken);

        Assert.Equal(403, again.Status);
        Assert.Equal("suspended", again.Problem!.Code);
    }

    /// <summary>
    ///     The token is stateless, so its lifetime is the whole of its bound — which makes the
    ///     expiry the one thing that has to be right.
    /// </summary>
    [Fact]
    public async Task A_token_stops_working_when_it_expires() {
        var answer = await Gate.SignInAsync(new(DevelopmentAuthority.Name, "alice"), TestContext.Current.CancellationToken);

        clock.Advance(options.SessionLifetime + TimeSpan.FromSeconds(1));

        Assert.Equal(TokenStatus.Expired, Gate.Authenticate(answer.Value!.Token, out var none));
        Assert.Null(none);
    }

    [Fact]
    public async Task A_bearer_prefix_is_accepted_and_a_tampered_token_is_not() {
        var answer = await Gate.SignInAsync(new(DevelopmentAuthority.Name, "alice"), TestContext.Current.CancellationToken);
        var token = answer.Value!.Token;

        // ⚠ Tampered INSIDE the lowercase hex alphabet, and the reason is the second bug this line
        // has had. It was `token[..^2] + "00"`, which for a token already ending in "00" handed
        // `Authenticate` the untouched token and got `Valid`. The fix for that — pick a character
        // different from the one that is there — was `token[^1] == 'A' ? 'B' : 'A'`, and it had the
        // same defect one level down: the signature is `Convert.ToHexStringLower`, so the last
        // character is never 'A' and the branch always wrote uppercase 'A' — but `FromHexString` is
        // case-INSENSITIVE, so a token ending in 'a' decoded to the very same bytes and authenticated
        // as `Valid`. One run in sixteen, and it reads as a flaky signature check rather than as a
        // test that did not tamper with anything.
        //
        // A string that differs is not the property this test needs; bytes that differ is. So the
        // replacement stays in `0-9a-f`, and the precondition below compares the DECODED signature
        // rather than the text, so any future variant of this fails as a broken fixture rather than
        // as a forgery that was not caught.
        var tampered = token[..^1] + (token[^1] == '0' ? '1' : '0');
        Assert.NotEqual(token, tampered);

        var mark = token.LastIndexOf('.');
        Assert.NotEqual(
            Convert.FromHexString(token[(mark + 1)..]),
            Convert.FromHexString(tampered[(mark + 1)..]));

        Assert.Equal(TokenStatus.Valid, Gate.Authenticate("Bearer " + token, out _));
        Assert.Equal(TokenStatus.Forged, Gate.Authenticate(tampered, out _));
        Assert.Equal(TokenStatus.Malformed, Gate.Authenticate("not a token", out _));
        Assert.Equal(TokenStatus.Malformed, Gate.Authenticate(null, out _));
    }

    /// <summary>Another gate's key is a forgery, which is the property the whole plane rests on.</summary>
    [Fact]
    public async Task A_token_from_another_gate_is_forged() {
        var answer = await Gate.SignInAsync(new(DevelopmentAuthority.Name, "alice"), TestContext.Current.CancellationToken);

        using var stranger = new GateTokenSigner(Encoding.UTF8.GetBytes("a different key of sufficient size!!"));
        var other = new GateService(options, store, fleet, tickets, stranger, [new DevelopmentAuthority()], clock);

        Assert.Equal(TokenStatus.Forged, other.Authenticate(answer.Value!.Token, out _));
    }

    [Fact]
    public void A_short_signing_key_is_refused_at_construction() {
        Assert.Throws<ArgumentException>(() => new GateTokenSigner("short"u8));
    }

    // ── Characters ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_character_is_created_and_comes_back_in_the_list() {
        var session = await SignIn("alice");

        var made = await Gate.CreateCharacterAsync(
            session,
            new("Bruna", "", "maps/queensdale"),
            TestContext.Current.CancellationToken
        );

        Assert.True(made.Ok);
        Assert.Equal(201, made.Status);
        Assert.Equal("eu", made.Value!.Region);          // the gate's region, since none was asked for

        var list = await Gate.CharactersAsync(session, TestContext.Current.CancellationToken);

        Assert.Equal("Bruna", Assert.Single(list.Value!.Characters).Name);
    }

    [Fact]
    public async Task Another_accounts_characters_are_not_in_the_list() {
        var alice = await SignIn("alice");
        var bob = await SignIn("bob");

        await Gate.CreateCharacterAsync(alice, new("Bruna", "eu", "maps/queensdale"), TestContext.Current.CancellationToken);

        Assert.Empty((await Gate.CharactersAsync(bob, TestContext.Current.CancellationToken)).Value!.Characters);
    }

    [Fact]
    public async Task A_taken_name_is_refused_rather_than_adjusted() {
        var alice = await SignIn("alice");
        var bob = await SignIn("bob");

        await Gate.CreateCharacterAsync(alice, new("Bruna", "eu", "maps/queensdale"), TestContext.Current.CancellationToken);

        var clash = await Gate.CreateCharacterAsync(
            bob,
            new("bruna", "eu", "maps/queensdale"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(409, clash.Status);
        Assert.Equal("name-taken", clash.Problem!.Code);
    }

    /// <summary>
    ///     A map address arrives from a client and <c>IMapGrain</c> is keyed by it, so an unfiltered
    ///     gate lets a stranger create a fleet for whatever they type.
    /// </summary>
    [Fact]
    public async Task A_map_this_gate_does_not_serve_is_refused() {
        var session = await SignIn("alice");

        var made = await Gate.CreateCharacterAsync(
            session,
            new("Bruna", "eu", "maps/../../etc"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(400, made.Status);
        Assert.Equal("unknown-map", made.Problem!.Code);
    }

    [Fact]
    public async Task The_character_limit_is_a_number_the_deployment_chooses() {
        options.CharactersPerAccount = 2;

        var session = await SignIn("alice");

        for (var index = 0; index < 2; index++) {
            var made = await Gate.CreateCharacterAsync(
                session,
                new($"Bruna{index}", "eu", "maps/queensdale"),
                TestContext.Current.CancellationToken
            );

            Assert.True(made.Ok);
        }

        var third = await Gate.CreateCharacterAsync(
            session,
            new("Bruna2", "eu", "maps/queensdale"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(409, third.Status);
        Assert.Equal("too-many-characters", third.Problem!.Code);
    }

    [Fact]
    public async Task A_nameless_character_is_refused() {
        var session = await SignIn("alice");

        var made = await Gate.CreateCharacterAsync(session, new("  ", "eu", "maps/queensdale"), TestContext.Current.CancellationToken);

        Assert.Equal("name-required", made.Problem!.Code);
    }

    // ── Playing ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Playing_returns_an_endpoint_and_a_ticket_that_the_target_shard_accepts() {
        var (session, character) = await Character("alice", "Bruna");

        var answer = await Gate.PlayAsync(session, Play(character), TestContext.Current.CancellationToken);

        Assert.True(answer.Ok);
        Assert.Equal(PlayStatus.Placed, answer.Value!.Status);
        Assert.Equal(fleet.Answer.Endpoint.ToString(), answer.Value.Endpoint);
        Assert.Equal(TimeSpan.Zero, answer.Value.RetryAfter);

        Assert.True(TransferTicket.TryDecode(answer.Value.Ticket, out var ticket, out var why), why);
        Assert.NotNull(ticket);
        Assert.Equal(TicketStatus.Valid, tickets.Validate(ticket, fleet.Answer.Shard, Noon));
        Assert.Equal(new PlayerKey(session.Account, character), ticket.Player);
        Assert.Equal(Noon + options.TicketLifetime, ticket.Expires);
    }

    /// <summary>
    ///     The gate predicts the epoch rather than taking the lease: acquiring is the receiving
    ///     realm's call, and a gate that acquired would take the lease off whoever holds it for
    ///     everybody who merely opened the character screen.
    /// </summary>
    [Fact]
    public async Task The_ticket_names_one_epoch_past_whatever_is_held() {
        var (session, character) = await Character("alice", "Bruna");

        fleet.Epoch = 11;

        var answer = await Gate.PlayAsync(session, Play(character), TestContext.Current.CancellationToken);

        Assert.True(TransferTicket.TryDecode(answer.Value!.Ticket, out var ticket, out _));
        Assert.Equal(12, ticket!.LeaseEpoch);
    }

    /// <summary>ADR-022: a routing decision rather than a rejection.</summary>
    [Fact]
    public async Task A_client_on_the_wrong_content_is_told_to_update_rather_than_refused() {
        var (session, character) = await Character("alice", "Bruna");

        var answer = await Gate.PlayAsync(
            session,
            Play(character) with { Version = new("0.1.0", 0xBADF00D) },
            TestContext.Current.CancellationToken
        );

        Assert.True(answer.Ok);
        Assert.Equal(PlayStatus.UpdateRequired, answer.Value!.Status);
        Assert.Contains(options.Content, answer.Value.Reason, StringComparison.Ordinal);
        Assert.Empty(answer.Value.Ticket);

        // And the cluster was never asked, because a version mismatch is knowable here.
        Assert.Empty(fleet.Asked);
    }

    /// <summary>
    ///     A client told "refused" shows a failure and a client told "starting" shows a wait.
    ///     Conflating them is how an elastic fleet's ordinary behaviour becomes a support ticket.
    /// </summary>
    [Fact]
    public async Task A_shard_that_is_coming_up_is_a_wait_with_a_retry_and_no_ticket() {
        var (session, character) = await Character("alice", "Bruna");

        fleet.Answer = new(PlaceStatus.Starting, ShardId.None, default, "a shard is being started");

        var answer = await Gate.PlayAsync(session, Play(character), TestContext.Current.CancellationToken);

        Assert.Equal(PlayStatus.Starting, answer.Value!.Status);
        Assert.Equal(options.StartingRetry, answer.Value.RetryAfter);
        Assert.Empty(answer.Value.Ticket);
        Assert.Empty(answer.Value.Endpoint);
    }

    [Fact]
    public async Task A_refusal_carries_the_maps_own_explanation() {
        var (session, character) = await Character("alice", "Bruna");

        fleet.Answer = new(PlaceStatus.Refused, ShardId.None, default, "every shard is full and the map is at MaxShards");

        var answer = await Gate.PlayAsync(session, Play(character), TestContext.Current.CancellationToken);

        Assert.Equal(PlayStatus.Refused, answer.Value!.Status);
        Assert.Equal("every shard is full and the map is at MaxShards", answer.Value.Reason);
    }

    /// <summary>Probing character ids must tell a stranger nothing.</summary>
    [Fact]
    public async Task Another_accounts_character_is_not_found_rather_than_forbidden() {
        var (_, character) = await Character("alice", "Bruna");
        var bob = await SignIn("bob");

        var answer = await Gate.PlayAsync(bob, Play(character), TestContext.Current.CancellationToken);

        Assert.Equal(404, answer.Status);
        Assert.Equal("no-such-character", answer.Problem!.Code);
        Assert.Empty(fleet.Asked);
    }

    [Fact]
    public async Task A_suspended_account_never_costs_the_cluster_a_grain_call() {
        var (session, character) = await Character("alice", "Bruna");

        await store.Accounts.SetSuspendedAsync(session.Account, true, TestContext.Current.CancellationToken);

        var answer = await Gate.PlayAsync(session, Play(character), TestContext.Current.CancellationToken);

        Assert.Equal(403, answer.Status);
        Assert.Empty(fleet.Asked);
    }

    /// <summary>
    ///     The character's region, not the request's: latency zone is a property of where the
    ///     character lives rather than something a client asserts per call.
    /// </summary>
    [Fact]
    public async Task The_placement_request_carries_the_characters_region_and_the_fleets_version() {
        var (session, character) = await Character("alice", "Bruna");

        await Gate.PlayAsync(session, Play(character) with { Locale = "de-DE" }, TestContext.Current.CancellationToken);

        var asked = Assert.Single(fleet.Asked);

        Assert.Equal(new ShardKey("maps/queensdale", "eu", Running), asked.Key);
        Assert.Equal("de-DE", asked.Locale);
        Assert.Equal(new PlayerKey(session.Account, character), asked.Player);
    }

    [Fact]
    public void The_catalog_says_what_the_fleet_is_and_where_to_get_it() {
        var catalog = Gate.Catalog();

        Assert.Equal(Running, catalog.Version);
        Assert.Equal("https://content.example/catalog", catalog.Content);
        Assert.Equal(["maps/queensdale", "maps/divinity"], catalog.Maps);
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────────

    static PlayRequest Play(Guid character) => new(character, "maps/queensdale", Running, "en-GB", default, default);

    async Task<GateToken> SignIn(string handle) {
        var answer = await Gate.SignInAsync(new(DevelopmentAuthority.Name, handle), TestContext.Current.CancellationToken);

        Gate.Authenticate(answer.Value!.Token, out var session);

        return session!;
    }

    async Task<(GateToken Session, Guid Character)> Character(string handle, string name) {
        var session = await SignIn(handle);

        var made = await Gate.CreateCharacterAsync(
            session,
            new(name, "eu", "maps/queensdale"),
            TestContext.Current.CancellationToken
        );

        return (session, made.Value!.Character);
    }
}

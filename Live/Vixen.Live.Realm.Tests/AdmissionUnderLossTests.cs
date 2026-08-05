// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Transport;
using Xunit;

namespace Vixen.Live.Realms.Tests;

/// <summary>Admission over a wire that loses, delays, reorders and duplicates.</summary>
/// <remarks>
///     <para>
///         <b>Doc 27 § Testing asks for this leg by name</b> — <em>"end-to-end over
///         <c>Vixen.Net.Transport.Local</c> with <c>NetworkSimulation</c>"</em> — and until now every
///         admission test in this project ran on a perfect transport. A perfect transport cannot fail
///         the interesting way: admission is a handshake with a deadline, and a deadline only means
///         anything when something can be late.
///     </para>
///     <para>
///         ⚠ <b>The assertions are about the <em>outcome</em> and never about the number of steps.</b>
///         A lossy wire takes as many attempts as it takes; a test that asserted "admitted within four
///         pumps" would be asserting the loss rate, and it would go red on a profile change rather
///         than on a bug.
///     </para>
///     <para>
///         <b>This file is the handshake half.</b> The transfer oracle is the other, and it used to be
///         missing for a stated reason — that harness drove <c>SourceTransfer</c> and
///         <c>ClientTransfer</c> directly and never opened a session, so a loss profile changed
///         nothing there. It holds real sessions now and runs under the same three wires; see
///         <c>TransferOracleTests</c>.
///     </para>
/// </remarks>
public class AdmissionUnderLossTests {
    /// <summary>The three wires worth asserting against, and one that duplicates everything.</summary>
    /// <remarks>
    ///     <c>Awful</c> is the one that matters. A profile nobody would ship on is the profile a
    ///     player on a train has, and "it works on broadband" is not a claim about admission.
    /// </remarks>
    public static TheoryData<string> Wires => ["Mobile", "Awful", "Duplicating"];

    static NetworkSimulationProfile Profile(string name) =>
        name switch {
            "Mobile" => NetworkSimulationProfile.Mobile,
            "Awful" => NetworkSimulationProfile.Awful,

            // ⚠ Not a shipped profile, and it is here because duplication is the failure mode
            // admission is most likely to get wrong: the same handshake arriving twice must be one
            // player, not two, and not a refusal of the second copy that kicks the first.
            _ => NetworkSimulationProfile.Broadband with { DuplicateChance = 0.25 }
        };

    [Theory]
    [MemberData(nameof(Wires))]
    public void ATicketedClientIsStillAdmitted(string wire) {
        using var realm = new RealmFixture(wire: Profile(wire));
        var admitted = new List<RealmPlayer>();

        realm.Host.PlayerAdmitted += admitted.Add;
        realm.MapIsUp();

        var ticket = realm.Ticket();

        realm.Connect(ticket);

        // Generous, and deliberately not tuned: the point is that it gets there, not how fast.
        realm.Pump(400);

        var player = Assert.Single(admitted);

        Assert.Equal(ticket.Player, player.Key);
        Assert.Equal(1, realm.Host.Population);
    }

    [Theory]
    [MemberData(nameof(Wires))]
    public void AClientWithNoTicketIsStillRefused(string wire) {
        // ⚠ The direction that matters more than the one above. A dropped packet must never become
        // an admission — a handshake that gave up and let somebody in would be a fleet where the way
        // past the ticket check is a bad connection.
        using var realm = new RealmFixture(wire: Profile(wire));

        realm.MapIsUp();
        realm.Connect(ticket: null);
        realm.Pump(400);

        Assert.Equal(0, realm.Host.Population);
        Assert.Equal(AdmissionRefusal.NoTicket, realm.Host.Admission.LastRefusal);
    }

    [Theory]
    [MemberData(nameof(Wires))]
    public void ATicketForAnotherShardIsStillRefused(string wire) {
        using var realm = new RealmFixture(wire: Profile(wire));

        realm.MapIsUp();
        realm.Connect(realm.Ticket(target: ShardId.New()));
        realm.Pump(400);

        Assert.Equal(0, realm.Host.Population);
        Assert.Equal(AdmissionRefusal.BadTicket, realm.Host.Admission.LastRefusal);
    }

    [Fact]
    public void ADuplicatedHandshakeIsOnePlayer() {
        // Every packet has a one-in-four chance of arriving twice, which over a whole handshake means
        // the realm sees the authentication more than once with near certainty.
        using var realm = new RealmFixture(wire: Profile("Duplicating"));
        var admitted = new List<RealmPlayer>();

        realm.Host.PlayerAdmitted += admitted.Add;
        realm.MapIsUp();
        realm.Connect(realm.Ticket());
        realm.Pump(400);

        Assert.Single(admitted);
        Assert.Equal(1, realm.Host.Population);
    }

    [Fact]
    public void TwentyClientsOnAnAwfulWireAllGetIn() {
        // ⚠ One client succeeding is a handshake that works; twenty is one that works *concurrently*,
        // which is the case where a shared buffer or a shared sequence number shows up.
        using var realm = new RealmFixture(capacity: new(64, 64), wire: NetworkSimulationProfile.Awful);
        var admitted = new List<RealmPlayer>();

        realm.Host.PlayerAdmitted += admitted.Add;
        realm.MapIsUp();

        for (var index = 0; index < 20; index++) {
            realm.Connect(realm.Ticket());
        }

        realm.Pump(800);

        Assert.Equal(20, admitted.Count);
        Assert.Equal(20, realm.Host.Population);
        Assert.Equal(20, admitted.Select(player => player.Key).Distinct().Count());
    }

    [Fact]
    public void APerfectWireIsNotWrappedAtAll() {
        // The fixture's own contract, asserted because everything else in this project depends on it:
        // wrapping the default would re-time every existing test for no reason.
        using var realm = new RealmFixture();

        Assert.Equal(NetworkSimulationProfile.Perfect, realm.Wire);
    }
}

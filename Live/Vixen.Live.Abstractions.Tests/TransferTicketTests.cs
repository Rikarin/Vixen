// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Xunit;

namespace Vixen.Live.Tests;

/// <summary>What a courier can and cannot do with the envelope it is carrying.</summary>
/// <remarks>
///     ADR-020's ticket is the door to a realm, so the tests that matter are the adversarial ones: a
///     client that edits a field, a client that replays somebody else's, a client that presents one
///     for the shard next door. Every one of them has to end in a named refusal rather than in
///     admission.
/// </remarks>
public sealed class TransferTicketTests {
    static readonly byte[] ClusterKey = Encoding.UTF8.GetBytes("a-cluster-key-of-at-least-32-bytes!!");
    static readonly byte[] OtherClusterKey = Encoding.UTF8.GetBytes("a-different-cluster-key-32-bytes!!!!");

    static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    static readonly ShardId Target = new(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"));

    static TransferTicket Unsigned() =>
        new() {
            Player = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222")),
            Target = Target,
            Endpoint = new("10.0.0.4", 7777),
            LeaseEpoch = 41,
            Expires = Now + TimeSpan.FromSeconds(30)
        };

    [Fact]
    public void ASignedTicketAdmitsItsBearerToItsShard() {
        using var signer = new TransferTicketSigner(ClusterKey);

        var ticket = signer.Sign(Unsigned());

        Assert.Equal(TicketStatus.Valid, signer.Validate(ticket, Target, Now));
    }

    [Fact]
    public void ASignedTicketSurvivesTheRoundTripThroughTheClient() {
        using var signer = new TransferTicketSigner(ClusterKey);

        var ticket = signer.Sign(Unsigned());

        Assert.True(TransferTicket.TryDecode(ticket.Encode(), out var carried, out var error));
        Assert.Equal("", error);
        Assert.Equal(ticket, carried);
        Assert.Equal(TicketStatus.Valid, signer.Validate(carried!, Target, Now));
    }

    [Fact]
    public void AnUnsignedTicketIsRefusedBeforeAnythingElseIsChecked() {
        using var signer = new TransferTicketSigner(ClusterKey);

        Assert.Equal(TicketStatus.Unsigned, signer.Validate(Unsigned(), Target, Now));
    }

    [Fact]
    public void AnotherClustersTicketIsAForgeryHere() {
        using var theirs = new TransferTicketSigner(OtherClusterKey);
        using var ours = new TransferTicketSigner(ClusterKey);

        Assert.Equal(TicketStatus.Forged, ours.Validate(theirs.Sign(Unsigned()), Target, Now));
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("expires")]
    [InlineData("target")]
    [InlineData("player")]
    [InlineData("at")]
    public void EditingAnyFieldInvalidatesTheSignature(string field) {
        using var signer = new TransferTicketSigner(ClusterKey);

        var ticket = signer.Sign(Unsigned());

        // What a client would actually try: it holds the string, so it edits the string.
        var tampered = ticket.Encode()
            .Split(';')
            .Select(pair => pair.StartsWith(field + "=", StringComparison.Ordinal) ? Rewrite(pair) : pair);

        Assert.True(TransferTicket.TryDecode(string.Join(';', tampered), out var edited, out _));
        Assert.Equal(TicketStatus.Forged, signer.Validate(edited!, edited!.Target, Now));

        static string Rewrite(string pair) =>
            pair switch {
                _ when pair.StartsWith("epoch=", StringComparison.Ordinal) => "epoch=9999",
                _ when pair.StartsWith("expires=", StringComparison.Ordinal) => "expires=99999999999999",
                _ when pair.StartsWith("target=", StringComparison.Ordinal) => "target=" + Guid.NewGuid().ToString("D"),
                _ when pair.StartsWith("player=", StringComparison.Ordinal) =>
                    "player=" + Guid.NewGuid().ToString("D") + "/" + Guid.NewGuid().ToString("D"),
                _ => "at=10.0.0.9:7777"
            };
    }

    [Fact]
    public void AGenuineTicketStopsWorkingWhenItExpires() {
        using var signer = new TransferTicketSigner(ClusterKey);

        var ticket = signer.Sign(Unsigned());

        Assert.Equal(TicketStatus.Valid, signer.Validate(ticket, Target, ticket.Expires - TimeSpan.FromTicks(1)));
        Assert.Equal(TicketStatus.Expired, signer.Validate(ticket, Target, ticket.Expires));
        Assert.Equal(TicketStatus.Expired, signer.Validate(ticket, Target, ticket.Expires + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void AGenuineTicketForTheShardNextDoorIsRefusedByName() {
        using var signer = new TransferTicketSigner(ClusterKey);

        Assert.Equal(TicketStatus.WrongShard, signer.Validate(signer.Sign(Unsigned()), ShardId.New(), Now));
    }

    [Fact]
    public void AKeyShorterThanTheHashIsRefusedAtConstruction() {
        // Not a weaker configuration — a mistake. HMAC pads anything shorter, so a four-character key
        // looks like it works and is guessable.
        var failure = Assert.Throws<ArgumentException>(
            () => new TransferTicketSigner("too short"u8)
        );

        Assert.Contains("at least 32 bytes", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoTicketsDecodedFromOneStringAreEqual() {
        // The synthesized record equality would compare the signature by reference and answer no,
        // which is exactly the comparison a replay test makes.
        using var signer = new TransferTicketSigner(ClusterKey);

        var encoded = signer.Sign(Unsigned()).Encode();

        Assert.True(TransferTicket.TryDecode(encoded, out var first, out _));
        Assert.True(TransferTicket.TryDecode(encoded, out var second, out _));
        Assert.Equal(first, second);
        Assert.Equal(first!.GetHashCode(), second!.GetHashCode());
    }

    const string GoodPlayer = "player=11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222";

    [Theory]
    [InlineData("player=nonsense;target=x;at=y;epoch=1;expires=1;sig=", "`player` is missing")]
    [InlineData(GoodPlayer + ";target=nonsense;at=y;epoch=1;expires=1;sig=", "`target` is missing")]
    [InlineData(GoodPlayer + ";target=0f8fad5b-d9cb-469f-a165-70867728950e;at=nowhere;epoch=1;expires=1;sig=", "`at` is missing")]
    [InlineData(GoodPlayer + ";target=0f8fad5b-d9cb-469f-a165-70867728950e;at=h:1;epoch=x;expires=1;sig=", "`epoch` is missing")]
    [InlineData(GoodPlayer + ";target=0f8fad5b-d9cb-469f-a165-70867728950e;at=h:1;epoch=1;expires=x;sig=", "`expires` is missing")]
    public void MalformedTicketsAreRefusedWithAReason(string text, string expected) {
        Assert.False(TransferTicket.TryDecode(text, out var ticket, out var error));
        Assert.Null(ticket);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void ASignatureThatIsNotHexadecimalIsRefusedRatherThanThrowing() {
        var ticket = Unsigned();
        var text = ticket.Encode().Replace("sig=", "sig=zz", StringComparison.Ordinal);

        Assert.False(TransferTicket.TryDecode(text, out _, out var error));
        Assert.Contains("hexadecimal", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ADisposedSignerRefusesToSignOrValidate() {
        var signer = new TransferTicketSigner(ClusterKey);
        var ticket = signer.Sign(Unsigned());

        signer.Dispose();
        signer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => signer.Sign(Unsigned()));
        Assert.Throws<ObjectDisposedException>(() => signer.Validate(ticket, Target, Now));
    }
}

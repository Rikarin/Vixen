// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Replication;
using Vixen.Net.Rpc;
using Vixen.Net.Rules;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Net.Tests.Rules;

/// <summary>The policy: who may do what, and which of two opinions wins.</summary>
public sealed class NetworkRulesTests {
    static readonly PlayerId Owner = new(1);
    static readonly PlayerId Stranger = new(2);
    static readonly NetworkId Object = new(10);

    readonly NetworkOwnership ownership = new();
    readonly NetworkRulesRegistry rules;

    public NetworkRulesTests() {
        rules = new(ownership);
        ownership.SetOwner(Object, Owner);
    }

    [Fact]
    public void TheServerIsNeverRefused() {
        rules.Default = new() {
            CallServerRpc = RuleAudience.ServerOnly,
            ChangeOwner = RuleAudience.ServerOnly,
            Spawn = RuleAudience.ServerOnly
        };

        // A rule that could stop the authority would be a rule about nothing.
        Assert.True(rules.MayCallServerRpc(Object, PlayerId.None, requiresOwnership: true));
        Assert.True(rules.MayChangeOwner(Object, PlayerId.None));
        Assert.True(rules.MaySpawn(Object, PlayerId.None));
    }

    /// <summary>Who may take an object and when it may be taken are two questions.</summary>
    /// <remarks>
    ///     <para>
    ///         The pick-up rule cannot be spelled with an audience alone: <c>Everyone</c> lets anybody
    ///         take a weapon out of somebody else's hands, and <c>Owner</c> means nobody can ever pick
    ///         up a dropped one. Together with <see cref="OwnershipClaim.WhenUnowned" /> they say the
    ///         thing a game actually means.
    ///     </para>
    ///     <para>
    ///         This is why Vixen has no ownership-toggle component, which the reference implementation
    ///         does have. The trigger that decides *when* to try is the game's; the policy that decides
    ///         whether it is allowed is here, in the one record everything else already asks.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AClaimRuleIsTheOtherHalfOfTheOwnershipQuestion() {
        rules.Default = new() { ChangeOwner = RuleAudience.Everyone, Claim = OwnershipClaim.WhenUnowned };

        // Owned by somebody else: refused, however permissive the audience is.
        Assert.False(rules.MayChangeOwner(Object, Stranger));

        // Its own owner always may, which is what makes putting it down possible at all — releasing
        // is a transfer to nobody, and a rule that refused it would make a dropped weapon undroppable.
        Assert.True(rules.MayChangeOwner(Object, Owner));

        ownership.Forget(Object);
        Assert.True(rules.MayChangeOwner(Object, Stranger));

        // And the server is never refused: this constrains clients taking things from each other, not
        // a referee reassigning a vehicle.
        ownership.SetOwner(Object, Owner);
        Assert.True(rules.MayChangeOwner(Object, PlayerId.None));

        // The default is unchanged behaviour: an audience that admits you admits you.
        rules.Default = new() { ChangeOwner = RuleAudience.Everyone };
        Assert.True(rules.MayChangeOwner(Object, Stranger));
    }

    [Fact]
    public void AnOwnerAudienceAdmitsTheOwnerAndNobodyElse() {
        rules.Default = new() { ChangeOwner = RuleAudience.Owner };

        Assert.True(rules.MayChangeOwner(Object, Owner));
        Assert.False(rules.MayChangeOwner(Object, Stranger));
    }

    [Fact]
    public void AServerOnlyAudienceAdmitsNoClientAtAll() {
        rules.Default = new() { Despawn = RuleAudience.ServerOnly };

        Assert.False(rules.MayDespawn(Object, Owner));
        Assert.False(rules.MayDespawn(Object, Stranger));
    }

    [Fact]
    public void TheDefaultRulesAddNothingToWhatTheMethodsDeclare() {
        // Safety out of the box comes from the attribute, not from here: [ServerRpc] requires
        // ownership unless a method says otherwise.
        Assert.True(rules.MayCallServerRpc(Object, Owner, requiresOwnership: true));
        Assert.False(rules.MayCallServerRpc(Object, Stranger, requiresOwnership: true));
        Assert.True(rules.MayCallServerRpc(Object, Stranger, requiresOwnership: false));
    }

    [Fact]
    public void RulesTightenWhatAMethodDeclared() {
        rules.Set(Object, new() { CallServerRpc = RuleAudience.Owner });

        // The method said anybody may. The object says only its owner, and the object wins.
        Assert.False(rules.MayCallServerRpc(Object, Stranger, requiresOwnership: false));
        Assert.True(rules.MayCallServerRpc(Object, Owner, requiresOwnership: false));

        rules.Set(Object, new() { CallServerRpc = RuleAudience.ServerOnly });

        Assert.False(rules.MayCallServerRpc(Object, Owner, requiresOwnership: false));
    }

    [Fact]
    public void RulesCannotWidenWhatAMethodDeclared() {
        rules.Set(Object, new() { CallServerRpc = RuleAudience.Everyone });

        // A policy file quietly granting more than the code asked for is the thing this design
        // exists to avoid.
        Assert.False(rules.MayCallServerRpc(Object, Stranger, requiresOwnership: true));
    }

    [Fact]
    public void OneObjectsRulesDoNotBecomeEverybodys() {
        rules.Set(Object, new() { ChangeOwner = RuleAudience.Owner });

        Assert.True(rules.MayChangeOwner(Object, Owner));
        Assert.False(rules.MayChangeOwner(new(999), Owner));
        Assert.Equal(1, rules.OverrideCount);

        Assert.True(rules.Clear(Object));
        Assert.False(rules.MayChangeOwner(Object, Owner));
        Assert.False(rules.Clear(Object));
    }

    [Fact]
    public void TheTwoPresetsDifferWhereTheyShould() {
        Assert.Equal(RuleAudience.ServerOnly, NetworkRules.ServerAuthoritative.Write);
        Assert.Equal(RuleAudience.Owner, NetworkRules.OwnerAuthoritative.Write);
        Assert.Equal(RuleAudience.Owner, NetworkRules.OwnerAuthoritative.ChangeOwner);

        // Neither preset touches calls: that is the attribute's to declare and the rules' to narrow.
        Assert.Equal(RuleAudience.Everyone, NetworkRules.ServerAuthoritative.CallServerRpc);
        Assert.Equal(RuleAudience.Everyone, NetworkRules.OwnerAuthoritative.CallServerRpc);
    }

    [Fact]
    public void WhenAPlayerLeavesEachObjectGetsWhatItsRulesSay() {
        var avatar = new NetworkId(1);
        var vehicle = new NetworkId(2);
        var house = new NetworkId(3);

        ownership.SetOwner(avatar, Owner);
        ownership.SetOwner(vehicle, Owner);
        ownership.SetOwner(house, Owner);

        rules.Set(avatar, new() { OnOwnerDisconnect = DisconnectBehaviour.Destroy });
        rules.Set(vehicle, new() { OnOwnerDisconnect = DisconnectBehaviour.TransferToServer });
        rules.Set(house, new() { OnOwnerDisconnect = DisconnectBehaviour.Persist });

        var actions = new List<DisconnectAction>();

        Assert.Equal(4, rules.OnOwnerLeft(Owner, actions)); // three plus the fixture's object

        Assert.Contains(new DisconnectAction(avatar, DisconnectBehaviour.Destroy), actions);

        // The transfer is done; the destroy is a decision handed back, because destroying an entity
        // is not the policy's to do.
        Assert.False(ownership.TryGetOwner(vehicle, out _));
        Assert.True(ownership.IsOwnedBy(avatar, Owner));

        // And the one that persists keeps its owner, so the same player resumes it when they come
        // back inside the session's reconnect window.
        Assert.True(ownership.IsOwnedBy(house, Owner));
    }

    [Fact]
    public void ATransferAClientIsNotAllowedToMake_IsRefusedAndCounted() {
        var router = new RpcRouter(new(), new NullTransport(), RpcRole.Server, ownership, rules: rules);

        Assert.False(router.TryTransferOwnership(Object, Owner, Stranger));
        Assert.Equal(1, router.RefusedByRulesCount);
        Assert.True(ownership.IsOwnedBy(Object, Owner));

        // The server is always allowed, because it is the authority.
        Assert.True(router.TryTransferOwnership(Object, PlayerId.None, Stranger));
        Assert.True(ownership.IsOwnedBy(Object, Stranger));
    }

    [Fact]
    public void ATransferTheRulesAllow_GoesThrough() {
        rules.Set(Object, NetworkRules.OwnerAuthoritative);
        var router = new RpcRouter(new(), new NullTransport(), RpcRole.Server, ownership, rules: rules);

        Assert.True(router.TryTransferOwnership(Object, Owner, Stranger));
        Assert.True(ownership.IsOwnedBy(Object, Stranger));

        // And the previous owner cannot take it back, because they are not the owner any more.
        Assert.False(router.TryTransferOwnership(Object, Owner, Owner));
    }

    [Fact]
    public void ARouterWithNoRulesIsServerAuthoritative() {
        var router = new RpcRouter(new(), new NullTransport(), RpcRole.Server);

        Assert.Equal(RuleAudience.ServerOnly, router.Rules.Default.Write);
        Assert.Equal(DisconnectBehaviour.TransferToServer, router.Rules.Default.OnOwnerDisconnect);
    }

    /// <summary>A transport for a router that is never asked to send anything.</summary>
    sealed class NullTransport : IRpcTransport {
        public void SendToServer(ReadOnlySpan<byte> payload, Channel channel) {
        }

        public void SendToPlayer(PlayerId player, ReadOnlySpan<byte> payload, Channel channel) {
        }

        public void SendToAll(ReadOnlySpan<byte> payload, Channel channel) {
        }
    }
}

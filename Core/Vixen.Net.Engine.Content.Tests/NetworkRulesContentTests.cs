// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Net.Replication;
using Vixen.Net.Rpc;
using Vixen.Net.Rules;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Net.Engine.Content.Tests;

/// <summary>Filling the rules registry out of a build, rather than out of a start-up path.</summary>
/// <remarks>
///     <b>The half <c>NetworkRulesRegistry.Load</c> was written for and nothing supplied.</b> Its own
///     remarks say a policy arrives "by name and not by address … the name is what survives the
///     build" — and until <c>NetworkRulesContent</c> the only thing that could call it was a
///     hand-written line per file per peer. The suite is <c>NetworkPrefabContentTests</c>' shape,
///     over the same in-memory build.
/// </remarks>
public sealed class NetworkRulesContentTests {
    readonly NetworkOwnership ownership = new();
    readonly NetworkRulesRegistry registry;

    public NetworkRulesContentTests() {
        registry = new(ownership);
    }

    /// <summary>The worked example from doc 16: a dropped weapon anybody may take and nobody may steal.</summary>
    static NetworkRulesAsset Pickup(string name = "Pickup") =>
        new() {
            Name = name,
            Rules = new() { ChangeOwner = RuleAudience.Everyone, Claim = OwnershipClaim.WhenUnowned }
        };

    static NetworkRulesAsset Named(string name, NetworkRules rules) => new() { Name = name, Rules = rules };

    [Fact]
    public async Task EveryPolicyUnderTheLabelIsLoaded() {
        var shipped = new Shipped()
            .Rules("rules/pickup", Pickup(), NetworkRulesContent.Label)
            .Rules("rules/vehicle", Named("Vehicle", NetworkRules.OwnerAuthoritative), NetworkRulesContent.Label)
            .Rules("rules/turret", Named("Turret", NetworkRules.ServerAuthoritative), NetworkRulesContent.Label)
            .Build();

        var load = await NetworkRulesContent.LoadAsync(
            registry,
            shipped.Assets,
            TestContext.Current.CancellationToken
        );

        Assert.Empty(load.Problems);
        Assert.Equal(3, registry.NamedCount);

        // Address order, so a build that reports twice reports the same way.
        Assert.Equal(["Pickup", "Turret", "Vehicle"], load.Loaded);
    }

    /// <summary>
    ///     ⚠ Every field survives the chunk, which is the only half of a policy that can silently
    ///     become something else.
    /// </summary>
    /// <remarks>
    ///     <b>Not a round-trip for its own sake.</b> The importer's own remarks argue that a policy
    ///     needs no <c>MathScalars.Register</c> because "<c>NetworkRules</c> is six enums, and a policy
    ///     has no geometry in it at all" — and the failure that argument is about is silent and
    ///     partial: a field that reads back as its zero, which for every enum here is the
    ///     <i>most restrictive</i> value and therefore the one that looks like a deliberate default.
    ///     So this asserts a record in which no field is its zero, and reads each one back.
    /// </remarks>
    [Fact]
    public async Task EveryFieldOfAPolicySurvivesTheContentBuild() {
        var authored = new NetworkRules {
            Spawn = RuleAudience.Everyone,
            Despawn = RuleAudience.Owner,
            CallServerRpc = RuleAudience.Owner,
            Write = RuleAudience.Owner,
            ChangeOwner = RuleAudience.Everyone,
            Claim = OwnershipClaim.WhenUnowned,
            OnOwnerDisconnect = DisconnectBehaviour.Destroy
        };

        var shipped = new Shipped().Rules("rules/all", Named("All", authored), NetworkRulesContent.Label).Build();

        await NetworkRulesContent.LoadAsync(registry, shipped.Assets, TestContext.Current.CancellationToken);

        Assert.True(registry.TryGetNamed("All", out var read));
        Assert.Equal(authored, read);

        // Named individually as well as by record equality, because a record comparison that went
        // wrong would say "not equal" and not which field, and because every zero here reads as a
        // defensible default rather than as a failure.
        Assert.Equal(RuleAudience.Everyone, read.Spawn);
        Assert.Equal(RuleAudience.Owner, read.Despawn);
        Assert.Equal(RuleAudience.Owner, read.CallServerRpc);
        Assert.Equal(RuleAudience.Owner, read.Write);
        Assert.Equal(RuleAudience.Everyone, read.ChangeOwner);
        Assert.Equal(OwnershipClaim.WhenUnowned, read.Claim);
        Assert.Equal(DisconnectBehaviour.Destroy, read.OnOwnerDisconnect);
    }

    /// <summary>What is not labelled is not loaded, which is the whole opt-in.</summary>
    [Fact]
    public async Task WhatIsNotLabelledIsNotLoaded() {
        var shipped = new Shipped()
            .Rules("rules/pickup", Pickup(), NetworkRulesContent.Label)
            .Rules("rules/vehicle", Named("Vehicle", NetworkRules.OwnerAuthoritative))
            .Build();

        var load = await NetworkRulesContent.LoadAsync(
            registry,
            shipped.Assets,
            TestContext.Current.CancellationToken
        );

        Assert.Empty(load.Problems);
        Assert.Equal(["Pickup"], load.Loaded);
        Assert.False(registry.TryGetNamed("Vehicle", out _));
    }

    /// <summary>A label nothing carries is an empty registry rather than a failure.</summary>
    [Fact]
    public async Task ALabelNothingCarriesLoadsNothing() {
        var shipped = new Shipped().Rules("rules/pickup", Pickup()).Build();

        var load = await NetworkRulesContent.LoadAsync(
            registry,
            shipped.Assets,
            TestContext.Current.CancellationToken
        );

        Assert.Empty(load.Loaded);
        Assert.Empty(load.Problems);
        Assert.Equal(0, registry.NamedCount);
    }

    /// <summary>Two groups, one registry.</summary>
    [Fact]
    public async Task SeveralLabelsFillOneRegistry() {
        var shipped = new Shipped()
            .Rules("rules/pickup", Pickup(), "weapons")
            .Rules("rules/waggon", Named("Waggon", NetworkRules.OwnerAuthoritative), "vehicles")
            .Build();

        var load = await NetworkRulesContent.LoadAsync(
            registry,
            shipped.Assets,
            ["weapons", "vehicles"],
            TestContext.Current.CancellationToken
        );

        Assert.Empty(load.Problems);
        Assert.Equal(2, registry.NamedCount);
    }

    /// <summary>A .vxgroup broad enough to sweep up a texture: the rest loads and the one is named.</summary>
    [Fact]
    public async Task SomethingElseUnderTheLabelIsAProblemAndTheRestLoads() {
        var shipped = new Shipped()
            .Rules("rules/pickup", Pickup(), NetworkRulesContent.Label)
            .Raw("art/icon", [1, 2, 3, 4, 5, 6, 7, 8], NetworkRulesContent.Label)
            .Rules("rules/vehicle", Named("Vehicle", NetworkRules.OwnerAuthoritative), NetworkRulesContent.Label)
            .Build();

        var load = await NetworkRulesContent.LoadAsync(
            registry,
            shipped.Assets,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, registry.NamedCount);
        Assert.Equal(2, load.Loaded.Length);

        var problem = Assert.Single(load.Problems);

        Assert.Contains("art/icon", problem, StringComparison.Ordinal);
    }

    /// <summary>Address order, so a build that fails twice fails the same way.</summary>
    [Fact]
    public async Task ProblemsComeBackInAddressOrder() {
        var shipped = new Shipped()
            .Raw("z/second", [9, 9, 9, 9], NetworkRulesContent.Label)
            .Raw("a/first", [8, 8, 8, 8], NetworkRulesContent.Label)
            .Build();

        var load = await NetworkRulesContent.LoadAsync(
            registry,
            shipped.Assets,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, load.Problems.Length);
        Assert.Contains("a/first", load.Problems[0], StringComparison.Ordinal);
        Assert.Contains("z/second", load.Problems[1], StringComparison.Ordinal);
    }

    /// <summary>A hand-written list is the caller's, so a name that is not in the build throws.</summary>
    [Fact]
    public async Task AnAddressThatIsNotInTheCatalogThrows() {
        var shipped = new Shipped().Rules("rules/pickup", Pickup()).Build();

        await Assert.ThrowsAsync<AddressNotFoundException>(
            async () => await NetworkRulesContent.LoadFromAsync(
                registry,
                shipped.Assets,
                ["rules/pickup", "rules/typo"],
                TestContext.Current.CancellationToken
            )
        );
    }

    /// <summary>
    ///     ⚠ Two files claiming one name is a problem, because the registry's own answer would be
    ///     whichever came last.
    /// </summary>
    /// <remarks>
    ///     <b><c>NetworkRulesRegistry.Load</c> is a dictionary assignment.</b> Two policies called
    ///     <c>Pickup</c> would leave one of them governing every prefab that names it, chosen by
    ///     address order, with nothing said anywhere — the shape of
    ///     <c>NetworkPrefabRegistry.Register</c>'s hash collision one layer up, and refused for its
    ///     reason: this is the one place with both addresses in hand.
    /// </remarks>
    [Fact]
    public async Task TwoPoliciesUnderOneNameThatDisagreeAreAProblem() {
        var shipped = new Shipped()
            .Rules("rules/a-pickup", Pickup(), NetworkRulesContent.Label)
            .Rules("rules/z-pickup", Named("Pickup", NetworkRules.OwnerAuthoritative), NetworkRulesContent.Label)
            .Build();

        var load = await NetworkRulesContent.LoadAsync(
            registry,
            shipped.Assets,
            TestContext.Current.CancellationToken
        );

        var problem = Assert.Single(load.Problems);

        Assert.Contains("rules/a-pickup", problem, StringComparison.Ordinal);
        Assert.Contains("rules/z-pickup", problem, StringComparison.Ordinal);

        // The first by address wins, and it is the one reported as the incumbent — so a build that
        // ran twice named the same pair the same way round.
        Assert.Equal(["Pickup"], load.Loaded);
        Assert.True(registry.TryGetNamed("Pickup", out var kept));
        Assert.Equal(Pickup().Rules, kept);
    }

    /// <summary>Two copies of one policy are duplicated content, not a conflict.</summary>
    [Fact]
    public async Task TwoPoliciesUnderOneNameThatAgreeAreNotAProblem() {
        var shipped = new Shipped()
            .Rules("rules/a-pickup", Pickup(), NetworkRulesContent.Label)
            .Rules("rules/z-pickup", Pickup(), NetworkRulesContent.Label)
            .Build();

        var load = await NetworkRulesContent.LoadAsync(
            registry,
            shipped.Assets,
            TestContext.Current.CancellationToken
        );

        Assert.Empty(load.Problems);
        Assert.Equal(["Pickup"], load.Loaded);
    }

    /// <summary>A policy nothing could refer to is a problem rather than an exception.</summary>
    [Fact]
    public async Task APolicyWithNoNameIsAProblem() {
        var shipped = new Shipped()
            .Rules("rules/anonymous", Named(string.Empty, NetworkRules.OwnerAuthoritative), NetworkRulesContent.Label)
            .Build();

        var load = await NetworkRulesContent.LoadAsync(
            registry,
            shipped.Assets,
            TestContext.Current.CancellationToken
        );

        var problem = Assert.Single(load.Problems);

        Assert.Contains("rules/anonymous", problem, StringComparison.Ordinal);
        Assert.Equal(0, registry.NamedCount);
    }

    /// <summary>
    ///     The one combination <c>NetworkRulesAsset.Validate</c> refuses is refused here too, because
    ///     the importer that would have caught it is not this build's.
    /// </summary>
    /// <remarks>
    ///     <c>NetworkPrefabContent</c> catches <c>SceneCompiler</c>'s refusals at load for the same
    ///     reason — reaching them means content built by something else. Loading it anyway would put
    ///     a policy that decides nothing into the registry under a name a prefab is relying on.
    /// </remarks>
    [Fact]
    public async Task APolicyThisBuildsImporterWouldHaveRefusedIsNotLoaded() {
        var shipped = new Shipped()
            .Rules(
                "rules/unreachable",
                Named("Unreachable", new() { Claim = OwnershipClaim.WhenUnowned }),
                NetworkRulesContent.Label
            )
            .Build();

        var load = await NetworkRulesContent.LoadAsync(
            registry,
            shipped.Assets,
            TestContext.Current.CancellationToken
        );

        var problem = Assert.Single(load.Problems);

        Assert.Contains("rules/unreachable", problem, StringComparison.Ordinal);
        Assert.False(registry.TryGetNamed("Unreachable", out _));
    }

    /// <summary>
    ///     ⚠ The whole point: a policy nobody wrote a <c>Load</c> line for governs a spawned node.
    /// </summary>
    /// <remarks>
    ///     <b>The end the counters are about.</b> Every other case here asserts what went into the
    ///     registry; this one asserts that a prefab out of the same build, naming the policy by the
    ///     name the file gave itself, is governed by it — and that
    ///     <c>NetworkSpawner.UnresolvedRules</c>, the counter that exists because this failure is
    ///     otherwise invisible, is zero. Without the load it is one, and the sword cannot be picked
    ///     up.
    /// </remarks>
    [Fact]
    public async Task APolicyOutOfTheCatalogGovernsAPrefabOutOfTheCatalog() {
        using var authoring = new World("rules-content-authoring");
        using var world = new World("rules-content-world");

        var shipped = new Shipped()
            .Rules("rules/pickup", Pickup(), NetworkRulesContent.Label)
            .Prefab("prefabs/sword", Sword(authoring), NetworkPrefabContent.Label)
            .Build();

        var prefabs = new NetworkPrefabRegistry();

        await NetworkRulesContent.LoadAsync(registry, shipped.Assets, TestContext.Current.CancellationToken);

        var prefabLoad = await NetworkPrefabContent.LoadAsync(
            prefabs,
            shipped.Assets,
            TestContext.Current.CancellationToken
        );

        try {
            var spawner = new NetworkSpawner(prefabs, new()) { Ownership = ownership, Rules = registry };
            var root = spawner.Spawn(world, "prefabs/sword");
            var sword = world.Get<NetworkId>(root);

            Assert.Equal(0, spawner.UnresolvedRules);

            // Unowned, so any client may take it — and once somebody has it, nobody else may. Doc 16's
            // worked example, asked of the registry rather than read back off the record.
            Assert.True(registry.MayChangeOwner(sword, new(1)));
            ownership.SetOwner(sword, new(1));
            Assert.False(registry.MayChangeOwner(sword, new(2)));
        } finally {
            foreach (var entry in prefabLoad.Registered) {
                entry.Prefab.Dispose();
            }
        }
    }

    /// <summary>A one-node prefab whose root names the pickup policy.</summary>
    /// <remarks>
    ///     The root, deliberately: the root always gets an id whether or not anybody marked it, so
    ///     this asks nothing of <c>NetworkObject</c> that <c>NetworkPrefabContentTests</c> does not
    ///     already assert on its own.
    /// </remarks>
    static PrefabAsset Sword(World authoring) {
        var root = Hierarchy.CreateTransform(authoring, new() { Rotation = Quaternion.Identity });

        authoring.Add(root, new NetworkRulesReference { Asset = "Pickup" });

        return new() { Name = "sword", Content = SceneContent.Capture(authoring, [root]) };
    }
}

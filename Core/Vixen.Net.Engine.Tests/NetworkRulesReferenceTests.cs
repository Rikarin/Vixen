// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Scenes;
using Vixen.Engine.Transforms;
using Vixen.Net.Replication;
using Vixen.Net.Rpc;
using Vixen.Net.Rules;
using Vixen.Net.Sessions;
using Xunit;

namespace Vixen.Net.Engine.Tests;

/// <summary>
///     The authored half of a policy reaching the registry that answers with it.
/// </summary>
/// <remarks>
///     <b>The join doc 16 § Rules describes and nothing implemented.</b> A
///     <see cref="NetworkRulesRegistry" /> is keyed by <see cref="NetworkId" /> — a number a server
///     allocated — and a prefab is content, which cannot carry one. The name is what survives the
///     content build, and <c>NetworkSpawner</c> is the one place where the name and the freshly
///     allocated id both exist.
/// </remarks>
public sealed class NetworkRulesReferenceTests : IDisposable {
    const string Address = "gameplay/prefabs/sword";
    const string Policy = "Pickup";

    static readonly PlayerId Player = new(1);

    readonly World world = new("rules-world");
    readonly World authoring = new("rules-authoring");
    readonly NetworkIdAllocator ids = new();
    readonly NetworkPrefabRegistry prefabs = new();
    readonly NetworkOwnership ownership = new();
    readonly NetworkRulesRegistry rules;
    readonly NetworkSpawner spawner;
    readonly Prefab prefab;

    public NetworkRulesReferenceTests() {
        rules = new(ownership);
        prefab = BuildPrefab();
        prefabs.Register(Address, prefab);

        spawner = new(prefabs, ids) { Ownership = ownership, Rules = rules };
    }

    public void Dispose() {
        prefab.Dispose();
        authoring.Dispose();
        world.Dispose();
    }

    /// <summary>
    ///     A prefab that names a loaded policy is governed by it, from the first tick of its life.
    /// </summary>
    /// <remarks>
    ///     The rule spelled here is doc 16's worked example and the one neither field can express
    ///     alone: <c>ChangeOwner = Everyone</c> with <c>Claim = WhenUnowned</c> is a dropped weapon
    ///     anybody may take and nobody may steal. It is asserted through
    ///     <see cref="NetworkRulesRegistry.MayChangeOwner" /> rather than by reading the policy back,
    ///     because what a designer put the file there for is the answer, not the record.
    /// </remarks>
    [Fact]
    public void APrefabThatNamesAPolicyIsGovernedByIt() {
        rules.Load(Policy, new() { ChangeOwner = RuleAudience.Everyone, Claim = OwnershipClaim.WhenUnowned });

        var root = spawner.Spawn(world, Address);
        var sword = Sword(root);

        Assert.Equal(0, spawner.UnresolvedRules);
        Assert.Equal(1, rules.OverrideCount);

        // Unowned, so any client may take it.
        Assert.True(rules.MayChangeOwner(sword, Player));

        // And once somebody has it, nobody else may.
        ownership.SetOwner(sword, Player);
        Assert.False(rules.MayChangeOwner(sword, new(2)));

        // ⚠ And the root, which names no policy, is still on the default — so the reference governs
        // the node that carries it rather than the instance it is part of.
        Assert.False(rules.MayChangeOwner(new(1), Player));
    }

    /// <summary>
    ///     ⚠ <b>A name nothing loaded leaves the object on the default and counts.</b>
    /// </summary>
    /// <remarks>
    ///     The default is server-authoritative, so the failure is safe — and therefore invisible.
    ///     What an author sees is a weapon nobody can pick up, with a policy file in the project that
    ///     reads exactly right; the count is the only place that says the file never arrived. Same
    ///     counter, same class of bug, as <c>WaterZoneSystem.UnresolvedWaves</c>.
    /// </remarks>
    [Fact]
    public void APolicyNothingLoadedCountsRatherThanThrowing() {
        var root = spawner.Spawn(world, Address);

        Assert.Equal(1, spawner.UnresolvedRules);
        Assert.Equal(0, rules.OverrideCount);
        Assert.False(rules.MayChangeOwner(Sword(root), Player));
    }

    /// <summary>Despawning takes the policy back off, so a reused id is not governed by a ghost.</summary>
    /// <remarks>
    ///     <c>NetworkSpawner.Despawn</c> already cleared the by-object table; this is what makes that
    ///     line matter, because until now nothing put anything in it during a spawn.
    /// </remarks>
    [Fact]
    public void DespawningForgetsThePolicy() {
        rules.Load(Policy, new() { ChangeOwner = RuleAudience.Everyone });

        var root = spawner.Spawn(world, Address);

        Assert.Equal(1, rules.OverrideCount);

        spawner.Despawn(world, root);

        Assert.Equal(0, rules.OverrideCount);
    }

    /// <summary>A spawner with no registry is not a spawner that throws.</summary>
    /// <remarks>
    ///     <c>Rules</c> is optional — a single-player or trusted-LAN game may never build one — and a
    ///     prefab carrying a reference must not turn that into a crash. It is also what keeps the
    ///     unresolved count honest: nothing was unresolved, because nothing was asked.
    /// </remarks>
    [Fact]
    public void ASpawnerWithoutARegistryIgnoresTheReference() {
        var bare = new NetworkSpawner(prefabs, ids);

        var root = bare.Spawn(world, Address);

        Assert.True(world.IsAlive(root));
        Assert.Equal(0, bare.UnresolvedRules);
    }

    /// <summary>The sword's id — the second allocated, because the root is always first.</summary>
    NetworkId Sword(Entity root) {
        foreach (var child in Hierarchy.ChildrenOf(world, root)) {
            if (world.Has<NetworkRulesReference>(child)) {
                return world.Get<NetworkId>(child);
            }
        }

        throw new InvalidOperationException("The instance has no node carrying a rules reference.");
    }

    /// <summary>A two-entity prefab: a root, and a sword that names a policy.</summary>
    Prefab BuildPrefab() {
        var root = Hierarchy.CreateTransform(authoring, new() { Rotation = Quaternion.Identity });
        var sword = Hierarchy.CreateTransform(authoring, new() { Rotation = Quaternion.Identity });

        authoring.Add<NetworkObject>(sword);
        authoring.Add(sword, new NetworkRulesReference { Asset = Policy });

        Hierarchy.SetParent(authoring, sword, root);

        return Prefab.CaptureFrom(authoring, root, "Sword");
    }
}

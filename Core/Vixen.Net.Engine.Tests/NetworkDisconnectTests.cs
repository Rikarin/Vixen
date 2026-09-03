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

/// <summary>What becomes of a departing player's objects, which nothing used to ask.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>NetworkRulesRegistry.OnOwnerLeft</c> had exactly one caller in the repository, and
///         it was a test.</b> Its own remarks hand the <see cref="DisconnectBehaviour.Destroy" />
///         entries to "whoever owns spawning", and nothing was that — so a <c>.vxnetrules</c> could
///         say <c>onOwnerDisconnect: Destroy</c>, import cleanly, resolve onto a spawned node, and
///         then outlive the session owned by a player who had gone.
///         <see cref="NetworkSpawner.OnOwnerLeft" /> is the half that was missing.
///     </para>
///     <para>
///         The three behaviours are asserted by <i>what happened to the world and the ownership
///         table</i> rather than by the actions the registry returns, because a list of intentions is
///         what the defect already had.
///     </para>
/// </remarks>
public sealed class NetworkDisconnectTests : IDisposable {
    const string Address = "gameplay/prefabs/crate";

    static readonly PlayerId Leaver = new(1);
    static readonly PlayerId Stayer = new(2);

    readonly World world = new("disconnect-world");
    readonly World authoring = new("disconnect-authoring");
    readonly NetworkIdAllocator ids = new();
    readonly NetworkPrefabRegistry prefabs = new();
    readonly NetworkOwnership ownership = new();
    readonly NetworkRulesRegistry rules;
    readonly NetworkSpawner spawner;
    readonly Prefab prefab;

    public NetworkDisconnectTests() {
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

    /// <summary>A player's avatar goes with them, which is what the policy says and nothing did.</summary>
    [Fact]
    public void AnObjectWhosePolicySaysDestroyIsDestroyed() {
        rules.Default = new() { OnOwnerDisconnect = DisconnectBehaviour.Destroy };

        var first = spawner.Spawn(world, Address, owner: Leaver);
        var second = spawner.Spawn(world, Address, owner: Leaver);

        Assert.Equal(2, spawner.OnOwnerLeft(world, Leaver));

        Assert.False(world.IsAlive(first));
        Assert.False(world.IsAlive(second));
        Assert.Equal(0, spawner.UnresolvedDespawns);
    }

    /// <summary>Destroying an instance takes the whole subtree, and its members' ids with it.</summary>
    /// <remarks>
    ///     The crate has a lid the designer marked networked, so an instance costs two ids. Only the
    ///     root is owned — that is what <c>NetworkSpawner.Spawn</c> records — and destroying it has to
    ///     take the lid as well, or the object half-survives its owner.
    /// </remarks>
    [Fact]
    public void DestroyingAnObjectTakesItsWholeSubtree() {
        rules.Default = new() { OnOwnerDisconnect = DisconnectBehaviour.Destroy };

        var root = spawner.Spawn(world, Address, owner: Leaver);
        var lid = Lid(root);

        Assert.Equal(1, spawner.OnOwnerLeft(world, Leaver));

        Assert.False(world.IsAlive(root));
        Assert.False(world.IsAlive(lid));
    }

    /// <summary>The safe default: the object stays and the server takes it.</summary>
    /// <remarks>
    ///     An object nobody owns still obeys the server, where one owned by a player who is gone obeys
    ///     nothing. The transfer is <c>NetworkRulesRegistry</c>'s own work; what is asserted here is
    ///     that going through the spawner does not destroy it as well.
    /// </remarks>
    [Fact]
    public void AnObjectWhosePolicySaysTransferStaysAndChangesHands() {
        var root = spawner.Spawn(world, Address, owner: Leaver);
        var id = world.Get<NetworkId>(root);

        Assert.Equal(0, spawner.OnOwnerLeft(world, Leaver));

        Assert.True(world.IsAlive(root));

        // ⚠ The server owning something is the *absence* of an entry, not an entry saying PlayerId.None
        // — `SetOwner` removes rather than stores it — so "the server has it" and "nobody ever owned
        // it" are one state, which is what makes IsOwnedBy the question rather than TryGetOwner.
        Assert.False(ownership.IsOwnedBy(id, Leaver));
        Assert.Equal(0, ownership.Count);
    }

    /// <summary>Persist keeps the owner, so the same player resumes it if they come back.</summary>
    [Fact]
    public void AnObjectWhosePolicySaysPersistKeepsBothTheObjectAndTheOwner() {
        rules.Default = new() { OnOwnerDisconnect = DisconnectBehaviour.Persist };

        var root = spawner.Spawn(world, Address, owner: Leaver);
        var id = world.Get<NetworkId>(root);

        Assert.Equal(0, spawner.OnOwnerLeft(world, Leaver));

        Assert.True(world.IsAlive(root));
        Assert.True(ownership.TryGetOwner(id, out var owner));
        Assert.Equal(Leaver, owner);
    }

    /// <summary>
    ///     Each object by its own policy, which is the reason the question is per object at all.
    /// </summary>
    /// <remarks>
    ///     One player's avatar and the vehicle they were driving want different answers, and a game
    ///     that spells that in two <c>.vxnetrules</c> gets it here or nowhere.
    /// </remarks>
    [Fact]
    public void EachObjectIsAnsweredByItsOwnPolicy() {
        var avatar = spawner.Spawn(world, Address, owner: Leaver);
        var vehicle = spawner.Spawn(world, Address, owner: Leaver);

        rules.Set(world.Get<NetworkId>(avatar), new() { OnOwnerDisconnect = DisconnectBehaviour.Destroy });
        rules.Set(world.Get<NetworkId>(vehicle), new() { OnOwnerDisconnect = DisconnectBehaviour.TransferToServer });

        Assert.Equal(1, spawner.OnOwnerLeft(world, Leaver));

        Assert.False(world.IsAlive(avatar));
        Assert.True(world.IsAlive(vehicle));
    }

    /// <summary>Somebody else's objects are not touched, however emphatic the policy.</summary>
    [Fact]
    public void AnotherPlayersObjectsAreLeftAlone() {
        rules.Default = new() { OnOwnerDisconnect = DisconnectBehaviour.Destroy };

        var theirs = spawner.Spawn(world, Address, owner: Leaver);
        var mine = spawner.Spawn(world, Address, owner: Stayer);

        Assert.Equal(1, spawner.OnOwnerLeft(world, Leaver));

        Assert.False(world.IsAlive(theirs));
        Assert.True(world.IsAlive(mine));
        Assert.True(ownership.IsOwnedBy(world.Get<NetworkId>(mine), Stayer));
    }

    /// <summary>A player who owned nothing is not an error and costs no sweep.</summary>
    [Fact]
    public void APlayerWhoOwnedNothingChangesNothing() {
        rules.Default = new() { OnOwnerDisconnect = DisconnectBehaviour.Destroy };

        var mine = spawner.Spawn(world, Address, owner: Stayer);

        Assert.Equal(0, spawner.OnOwnerLeft(world, Leaver));

        Assert.True(world.IsAlive(mine));
        Assert.Equal(0, spawner.UnresolvedDespawns);
    }

    /// <summary>
    ///     ⚠ An id whose policy condemned it and which no entity answers to is counted, not shrugged
    ///     off.
    /// </summary>
    /// <remarks>
    ///     <b>The failure this counter exists for is a wiring mistake, and it is silent.</b> A spawner
    ///     and a registry built over two different <c>NetworkOwnership</c> tables, or a game that
    ///     destroyed the entity by hand without telling the spawner, both end here — and both look
    ///     exactly like a policy that decided nothing. <c>NetworkSpawner.UnresolvedRules</c> is the
    ///     same counter for the other end of the same story.
    /// </remarks>
    [Fact]
    public void AnIdThatNoEntityAnswersToIsCounted() {
        rules.Default = new() { OnOwnerDisconnect = DisconnectBehaviour.Destroy };

        // Never spawned: what a second ownership table, or a hand-destroyed entity, leaves behind.
        ownership.SetOwner(new(4242), Leaver);

        Assert.Equal(0, spawner.OnOwnerLeft(world, Leaver));
        Assert.Equal(1, spawner.UnresolvedDespawns);
    }

    /// <summary>
    ///     Without a registry there is no policy, and inventing one here would be a second one.
    /// </summary>
    /// <remarks>
    ///     A single-player or trusted-LAN game may never build a registry. The honest answer is that
    ///     nothing was decided, rather than a default disconnect behaviour beside the one in the file.
    /// </remarks>
    [Fact]
    public void ASpawnerWithoutARegistryDecidesNothing() {
        var bare = new NetworkSpawner(prefabs, ids) { Ownership = ownership };
        var root = bare.Spawn(world, Address, owner: Leaver);

        Assert.Equal(0, bare.OnOwnerLeft(world, Leaver));
        Assert.True(world.IsAlive(root));
        Assert.Equal(0, bare.UnresolvedDespawns);
    }

    /// <summary>Destroying tells the replicator, so the ids are not tracked for a session's life.</summary>
    [Fact]
    public void ADestroyedObjectIsForgottenByOwnershipAndTheRules() {
        rules.Default = new() { OnOwnerDisconnect = DisconnectBehaviour.Destroy };

        var root = spawner.Spawn(world, Address, owner: Leaver);
        var id = world.Get<NetworkId>(root);

        rules.Set(id, new() { OnOwnerDisconnect = DisconnectBehaviour.Destroy });

        Assert.Equal(1, rules.OverrideCount);
        Assert.Equal(1, spawner.OnOwnerLeft(world, Leaver));

        Assert.Equal(0, rules.OverrideCount);
        Assert.False(ownership.TryGetOwner(id, out _));
    }

    /// <summary>The lid's id — the second the instance was given, because the root is always first.</summary>
    Entity Lid(Entity root) {
        foreach (var child in Hierarchy.ChildrenOf(world, root)) {
            if (world.Has<NetworkId>(child)) {
                return child;
            }
        }

        throw new InvalidOperationException("The instance has no networked child.");
    }

    /// <summary>A two-entity prefab: a crate, and a lid the designer marked networked.</summary>
    Prefab BuildPrefab() {
        var root = Hierarchy.CreateTransform(authoring, new() { Rotation = Quaternion.Identity });
        var lid = Hierarchy.CreateTransform(authoring, new() { Rotation = Quaternion.Identity });

        authoring.Add<NetworkObject>(lid);
        Hierarchy.SetParent(authoring, lid, root);

        return Prefab.CaptureFrom(authoring, root, "Crate");
    }
}

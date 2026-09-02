// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Net.Replication;
using Vixen.Net.Rpc;
using Vixen.Net.Sessions;

namespace Vixen.Net.Rules;

/// <summary>What one object's rules say should happen to it when its owner leaves.</summary>
/// <param name="Object">The object.</param>
/// <param name="Behaviour">What its rules say.</param>
public readonly record struct DisconnectAction(NetworkId Object, DisconnectBehaviour Behaviour);

/// <summary>Which rules apply to which object.</summary>
/// <remarks>
///     <para>
///         One default and a per-object override, which is the shape the policy takes at run time.
///         The authoring shape is a <c>.vxnetrules</c> asset referenced by a prefab — a
///         <c>NetworkRulesAsset</c>, imported into the content build and named on a prefab node by
///         <c>NetworkRulesReference</c>, both in <c>Vixen.Net.Engine</c> because they need the
///         engine's generators.
///     </para>
///     <para>
///         ⚠ <b>Which is why there are two tables here and not one.</b> The by-object table is a
///         <see cref="NetworkId" />, allocated when something spawns; a named one is an asset, loaded
///         before anything has spawned at all. A registry with only the first would have nowhere to
///         put a policy file until the moment it was already too late to apply it.
///     </para>
///     <para>
///         The registry is also the only place that knows both the rules and the ownership, which is
///         why the questions live here rather than on <see cref="NetworkRules" />: a rule about the
///         owner is not a question a policy can answer on its own.
///     </para>
/// </remarks>
public sealed class NetworkRulesRegistry {
    readonly Dictionary<uint, NetworkRules> byObject = [];
    readonly Dictionary<string, NetworkRules> byName = new(StringComparer.Ordinal);
    readonly NetworkOwnership ownership;

    /// <summary>What applies to an object that has not been given anything more specific.</summary>
    public NetworkRules Default { get; set; } = NetworkRules.ServerAuthoritative;

    /// <summary>How many objects have rules of their own.</summary>
    public int OverrideCount => byObject.Count;

    /// <summary>How many policy files have been loaded.</summary>
    public int NamedCount => byName.Count;

    /// <summary>Creates a registry over an ownership table.</summary>
    /// <param name="ownership">Who owns what, which half of every rule depends on.</param>
    public NetworkRulesRegistry(NetworkOwnership ownership) {
        ArgumentNullException.ThrowIfNull(ownership);
        this.ownership = ownership;
    }

    /// <summary>Gives one object its own rules.</summary>
    /// <param name="id">The object.</param>
    /// <param name="rules">Its rules.</param>
    public void Set(NetworkId id, NetworkRules rules) {
        ArgumentNullException.ThrowIfNull(rules);
        byObject[id.Value] = rules;
    }

    /// <summary>Loads a policy file under the name a prefab refers to it by.</summary>
    /// <param name="name">What the asset calls itself.</param>
    /// <param name="rules">What it says.</param>
    /// <exception cref="ArgumentException"><paramref name="name" /> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="rules" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>By name and not by address, for <c>WaterZoneComponent.WaveAsset</c>'s reason.</b> The
    ///     thing that refers to it is authored content — a prefab node — and content cannot hold a
    ///     handle to something the content build has not loaded yet. The name is what survives the
    ///     build; resolving it is <c>NetworkSpawner</c>'s, at the moment an instance gets its id.
    /// </remarks>
    public void Load(string name, NetworkRules rules) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(rules);

        byName[name] = rules;
    }

    /// <summary>The policy loaded under a name, if one was.</summary>
    /// <param name="name">The name a prefab refers to.</param>
    /// <param name="rules">What it says, if anything does.</param>
    /// <returns>Whether it resolved.</returns>
    public bool TryGetNamed(string name, [NotNullWhen(true)] out NetworkRules? rules) {
        ArgumentNullException.ThrowIfNull(name);

        return byName.TryGetValue(name, out rules);
    }

    /// <summary>Puts an object back on the default rules.</summary>
    /// <param name="id">The object.</param>
    /// <returns>Whether it had any of its own.</returns>
    public bool Clear(NetworkId id) => byObject.Remove(id.Value);

    /// <summary>The rules that apply to an object.</summary>
    /// <param name="id">The object.</param>
    /// <returns>Its own, or the default.</returns>
    public NetworkRules For(NetworkId id) => byObject.GetValueOrDefault(id.Value, Default);

    /// <summary>Whether a client may invoke a server call on an object.</summary>
    /// <param name="id">The object.</param>
    /// <param name="requester">Who is asking.</param>
    /// <param name="requiresOwnership">What the call's own attribute asked for.</param>
    /// <returns>Whether they may.</returns>
    /// <remarks>
    ///     The stricter of the two wins. A policy file may narrow what a method declared about
    ///     itself and may not widen it, so a <c>RequireOwnership = true</c> call stays an owner's call
    ///     however permissive the object's rules are.
    /// </remarks>
    public bool MayCallServerRpc(NetworkId id, PlayerId requester, bool requiresOwnership) {
        var audience = For(id).CallServerRpc;

        if (requiresOwnership && audience == RuleAudience.Everyone) {
            audience = RuleAudience.Owner;
        }

        return NetworkRules.Allows(audience, requester, ownership.IsOwnedBy(id, requester));
    }

    /// <summary>Whether a client may hand an object to somebody else.</summary>
    /// <param name="id">The object.</param>
    /// <param name="requester">Who is asking.</param>
    /// <returns>Whether they may.</returns>
    /// <remarks>
    ///     <b>Two questions, and both have to say yes.</b> <see cref="NetworkRules.ChangeOwner" />
    ///     says who may ask and <see cref="NetworkRules.Claim" /> says when — which together spell the
    ///     pick-up rule that neither can on its own: <c>ChangeOwner = Everyone</c> with
    ///     <c>Claim = WhenUnowned</c> is a dropped weapon anybody may take and nobody may steal.
    /// </remarks>
    public bool MayChangeOwner(NetworkId id, PlayerId requester) {
        var rules = For(id);
        var isOwner = ownership.IsOwnedBy(id, requester);

        if (!NetworkRules.Allows(rules.ChangeOwner, requester, isOwner)) {
            return false;
        }

        // The server is the authority and is never refused. A claim rule is a constraint on clients
        // taking things from each other; a game that wants to move an owned object server-side — a
        // referee reassigning a vehicle — is not the case this protects against.
        if (!requester.IsValid || rules.Claim != OwnershipClaim.WhenUnowned) {
            return true;
        }

        // Its own owner always may, which is what makes giving one up possible: releasing is a
        // transfer to nobody, and a rule that refused it would make a dropped weapon undroppable.
        return isOwner || !ownership.TryGetOwner(id, out var owner) || !owner.IsValid;
    }

    /// <summary>Whether a client may ask the server to create one of these.</summary>
    /// <param name="id">The object, or the prefab standing in for one.</param>
    /// <param name="requester">Who is asking.</param>
    /// <returns>Whether they may.</returns>
    /// <remarks>Declared and answered; nothing calls it yet, because nothing can spawn from a client.</remarks>
    public bool MaySpawn(NetworkId id, PlayerId requester) =>
        NetworkRules.Allows(For(id).Spawn, requester, ownership.IsOwnedBy(id, requester));

    /// <summary>Whether a client may ask the server to destroy an object.</summary>
    /// <param name="id">The object.</param>
    /// <param name="requester">Who is asking.</param>
    /// <returns>Whether they may.</returns>
    /// <remarks>Declared and answered; nothing calls it yet, because nothing can despawn from a client.</remarks>
    public bool MayDespawn(NetworkId id, PlayerId requester) =>
        NetworkRules.Allows(For(id).Despawn, requester, ownership.IsOwnedBy(id, requester));

    /// <summary>Whether a client may write an object's replicated state.</summary>
    /// <param name="id">The object.</param>
    /// <param name="requester">Who is asking.</param>
    /// <returns>Whether they may.</returns>
    /// <remarks>
    ///     Declared and answered; nothing calls it yet, because replication is one-way and a client
    ///     has no path to write. When it has, this is the question it asks rather than a second
    ///     policy.
    /// </remarks>
    public bool MayWrite(NetworkId id, PlayerId requester) =>
        NetworkRules.Allows(For(id).Write, requester, ownership.IsOwnedBy(id, requester));

    /// <summary>
    ///     Works out what should happen to everything a departing player owned, and does the part of
    ///     it that is ownership's to do.
    /// </summary>
    /// <param name="player">Who left.</param>
    /// <param name="actions">
    ///     Filled with one entry per object they owned. Everything marked
    ///     <see cref="DisconnectBehaviour.TransferToServer" /> has already been transferred; the
    ///     <see cref="DisconnectBehaviour.Destroy" /> entries are for whoever owns spawning to act on,
    ///     because destroying an entity is not this type's to do.
    /// </param>
    /// <returns>How many objects were affected.</returns>
    public int OnOwnerLeft(PlayerId player, List<DisconnectAction> actions) {
        ArgumentNullException.ThrowIfNull(actions);

        var theirs = new List<NetworkId>();
        ownership.OwnedBy(player, theirs);

        foreach (var id in theirs) {
            var behaviour = For(id).OnOwnerDisconnect;
            actions.Add(new(id, behaviour));

            if (behaviour == DisconnectBehaviour.TransferToServer) {
                ownership.SetOwner(id, PlayerId.None);
            }

            // Destroy is left to the caller, and Persist is the absence of doing anything: the
            // player keeps the object for as long as the session keeps the player, which is the
            // reconnect window and is the session's decision rather than this one's.
        }

        return theirs.Count;
    }
}

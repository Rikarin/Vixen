// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Engine.Scenes;
using Vixen.Net.Replication;

namespace Vixen.Net.Engine;

/// <summary>A prefab as the network knows it: its id, its template, and which nodes want an id.</summary>
/// <param name="Address">What the asset pipeline calls it.</param>
/// <param name="Id">What the wire calls it.</param>
/// <param name="Prefab">The template.</param>
/// <param name="Networked">
///     The nodes that get a <see cref="NetworkId" />, in capture order. The root is always first.
/// </param>
public sealed record NetworkPrefab(string Address, NetworkPrefabId Id, Prefab Prefab, int[] Networked) {
    /// <summary>How many ids one instance consumes.</summary>
    public int IdCount => Networked.Length;
}

/// <summary>What may be spawned, on both ends of the wire.</summary>
/// <remarks>
///     <para>
///         <b>Both peers build this from the same content, so neither has to send it.</b> A prefab's
///         id is a function of its address ([08](../../../docs/plan/08-asset-pipeline-and-addressables.md)),
///         so a server that registers <c>gameplay/prefabs/crate</c> and a client that registers the
///         same address agree without a handshake. What the handshake is for is noticing that they
///         registered <i>different content</i> under it, which is the catalog's hash rather than this
///         registry's business.
///     </para>
///     <para>
///         <b>Which nodes get ids is decided here, once.</b> A prefab is a subtree and most of it is
///         scenery: a hundred-entity set piece where one turret rotates should cost one id and one
///         record, not a hundred of each. The rule is that a template node carrying a
///         <see cref="NetworkObject" /> wants one — so a designer opts an entity into being
///         addressable by putting the component on it — plus the root, which needs one whether or not
///         anybody remembered, because the root is what the spawn itself is addressed to.
///     </para>
///     <para>
///         ⚠ <b>A node already carrying a <see cref="NetworkId" /> counts too, and that is not the
///         authoring path.</b> A template captured from a live world with <c>Prefab.CaptureFrom</c>
///         has whatever the world had on it, and a world that has been in a session has ids; a
///         template out of a content build has only what the asset could carry, which is
///         <see cref="NetworkObject" /> and never <see cref="NetworkId" />. Reading both means the
///         same subtree registers the same way whichever door it came through — which is the property
///         <c>ANetworkedMarkerSurvivesTheContentBuild</c> exists to hold, and which was false while
///         the marker was the handle.
///     </para>
/// </remarks>
public sealed class NetworkPrefabRegistry {
    readonly Dictionary<uint, NetworkPrefab> byId = [];
    readonly Dictionary<string, NetworkPrefab> byAddress = new(StringComparer.Ordinal);

    /// <summary>How many are registered.</summary>
    public int Count => byId.Count;

    /// <summary>Everything registered, in no particular order.</summary>
    public IEnumerable<NetworkPrefab> Prefabs => byId.Values;

    /// <summary>Registers a prefab under an address.</summary>
    /// <param name="address">The addressable's address.</param>
    /// <param name="prefab">The template.</param>
    /// <returns>Its id.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="InvalidOperationException">
    ///     Another address already hashes to the same id, or this address is already registered to a
    ///     different template.
    /// </exception>
    /// <remarks>
    ///     <b>The collision is caught here or not at all.</b> Two addresses hashing alike are two
    ///     prefabs the wire cannot tell apart, and the failure downstream would be a client
    ///     instantiating the wrong object with no error anywhere — so this is the one place with both
    ///     names in hand, and it says both.
    /// </remarks>
    public NetworkPrefabId Register(string address, Prefab prefab) {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(prefab);

        var id = NetworkPrefabId.From(address);

        if (byId.TryGetValue(id.Value, out var existing)) {
            if (!string.Equals(existing.Address, address, StringComparison.Ordinal)) {
                throw new InvalidOperationException(
                    $"'{address}' and '{existing.Address}' both hash to {id}, so the wire could not tell them apart. "
                    + "Rename one of the two assets."
                );
            }

            if (!ReferenceEquals(existing.Prefab, prefab)) {
                throw new InvalidOperationException(
                    $"'{address}' is already registered to a different template. Two templates under one address "
                    + "would make the same spawn build different things depending on which peer applied it."
                );
            }

            return id;
        }

        var networked = new List<int> { 0 };

        for (var node = 1; node < prefab.EntityCount; node++) {
            if (prefab.NodeHas<NetworkObject>(node) || prefab.NodeHas<NetworkId>(node)) {
                networked.Add(node);
            }
        }

        var entry = new NetworkPrefab(address, id, prefab, [.. networked]);
        byId[id.Value] = entry;
        byAddress[address] = entry;

        return id;
    }

    /// <summary>Finds a prefab by id.</summary>
    /// <param name="id">The id.</param>
    /// <param name="prefab">The prefab, if it is registered.</param>
    /// <returns>Whether it is.</returns>
    public bool TryGet(NetworkPrefabId id, [NotNullWhen(true)] out NetworkPrefab? prefab) =>
        byId.TryGetValue(id.Value, out prefab);

    /// <summary>Finds a prefab by address.</summary>
    /// <param name="address">The address.</param>
    /// <param name="prefab">The prefab, if it is registered.</param>
    /// <returns>Whether it is.</returns>
    public bool TryGet(string address, [NotNullWhen(true)] out NetworkPrefab? prefab) {
        ArgumentNullException.ThrowIfNull(address);

        return byAddress.TryGetValue(address, out prefab);
    }

    /// <summary>Finds a prefab by address, or says why not.</summary>
    /// <param name="address">The address.</param>
    /// <returns>The prefab.</returns>
    /// <exception cref="ArgumentException">Nothing is registered under it.</exception>
    public NetworkPrefab Require(string address) {
        if (!TryGet(address, out var prefab)) {
            throw new ArgumentException(
                $"Nothing is registered under '{address}'. A prefab has to be registered on every peer that could "
                + "be told to spawn it, because the spawn carries the id and nothing else.",
                nameof(address)
            );
        }

        return prefab;
    }
}

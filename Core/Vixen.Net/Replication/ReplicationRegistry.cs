// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Replication;

/// <summary>Every component type that may arrive over the wire, and nothing else.</summary>
/// <remarks>
///     <para>
///         <b>A closed set, and that is the security property.</b> Nothing is ever deserialized into
///         a type named by a packet: a packet names a number, the number is looked up here, and a
///         number that is not here is a packet that is refused. Deserializing into a type the wire
///         chose is the classic remote-code-execution vector in game netcode, and it is excluded by
///         construction rather than by validation.
///     </para>
///     <para>
///         Populated by generated registration code at start-up. Two types whose hashes collide is a
///         build-time accident that would silently misroute state, so it is refused loudly here
///         instead.
///     </para>
/// </remarks>
public sealed class ReplicationRegistry {
    readonly Dictionary<uint, IComponentReplicator> byTypeId = [];
    readonly Dictionary<uint, int> indexByTypeId = [];
    readonly List<IComponentReplicator> ordered = [];

    /// <summary>
    ///     Everything registered, ordered by wire id rather than by when it was registered.
    /// </summary>
    /// <remarks>
    ///     The order is the manifest, and a position in it is what actually goes on the wire — see
    ///     <see cref="IndexOf" />. Sorting by the hash rather than by registration order means two
    ///     builds of the same game agree on it without having to agree on start-up order, which
    ///     generated registration code cannot promise across assemblies.
    /// </remarks>
    public IReadOnlyList<IComponentReplicator> Replicators => ordered;

    /// <summary>How many types are replicated.</summary>
    public int Count => ordered.Count;

    /// <summary>
    ///     A hash over every registered type's wire id, in order: one number that says whether two
    ///     peers have the same set of replicated types.
    /// </summary>
    /// <remarks>
    ///     Fold this into the session's content hash. Two builds that disagree here would disagree
    ///     about what every index on the wire means, and the handshake is a much better place to find
    ///     that out than a client's world is.
    /// </remarks>
    public uint ManifestHash {
        get {
            var hash = 2166136261u;

            foreach (var replicator in ordered) {
                var id = replicator.TypeId;

                for (var shift = 0; shift < 32; shift += 8) {
                    hash ^= (id >> shift) & 0xFF;
                    hash *= 16777619u;
                }
            }

            return hash;
        }
    }

    /// <summary>Adds a replicator.</summary>
    /// <param name="replicator">The replicator, usually a generated one.</param>
    /// <exception cref="ArgumentException">
    ///     Another type already has that id — either the same type twice, or two names that hash the
    ///     same.
    /// </exception>
    public void Register(IComponentReplicator replicator) {
        ArgumentNullException.ThrowIfNull(replicator);

        if (byTypeId.TryGetValue(replicator.TypeId, out var existing)) {
            throw new ArgumentException(
                existing.TypeName == replicator.TypeName
                    ? $"{replicator.TypeName} is registered twice."
                    : $"{replicator.TypeName} and {existing.TypeName} both hash to {replicator.TypeId}. "
                    + "Rename one: two types sharing a wire id would send each other's state.",
                nameof(replicator)
            );
        }

        byTypeId[replicator.TypeId] = replicator;

        var at = ordered.Count;

        while (at > 0 && ordered[at - 1].TypeId > replicator.TypeId) {
            at--;
        }

        ordered.Insert(at, replicator);
        Reindex();
    }

    /// <summary>The number this type has on the wire.</summary>
    /// <param name="typeId">The type's stable hashed id.</param>
    /// <returns>Its position in the manifest, or -1 if it is not registered.</returns>
    /// <remarks>
    ///     <b>The index is what a record carries, not the hash.</b> A 32-bit hash costs five bytes as
    ///     a variable-length integer, on every record, of which there are thousands a second — the
    ///     first version of this sent the hash and the size of a one-field update gave it away. The
    ///     hash is the stable identity, the index is the encoding of it, and
    ///     <see cref="ManifestHash" /> is what makes it safe to send the short one.
    /// </remarks>
    public int IndexOf(uint typeId) => indexByTypeId.GetValueOrDefault(typeId, -1);

    /// <summary>Finds the replicator a wire index names.</summary>
    /// <param name="index">The index off the wire.</param>
    /// <param name="replicator">The replicator, if the index is one we have.</param>
    /// <returns>Whether it is.</returns>
    public bool TryGetByIndex(uint index, out IComponentReplicator? replicator) {
        if (index >= (uint)ordered.Count) {
            replicator = null;

            return false;
        }

        replicator = ordered[(int)index];

        return true;
    }

    void Reindex() {
        indexByTypeId.Clear();

        for (var i = 0; i < ordered.Count; i++) {
            indexByTypeId[ordered[i].TypeId] = i;
        }
    }

    /// <summary>Finds the replicator a wire id names.</summary>
    /// <param name="typeId">The id off the wire.</param>
    /// <param name="replicator">The replicator, if the id is one we know.</param>
    /// <returns>Whether it is.</returns>
    public bool TryGet(uint typeId, out IComponentReplicator? replicator) => byTypeId.TryGetValue(typeId, out replicator);

    /// <summary>
    ///     The id a type name has on the wire: 32-bit FNV-1a over the full name.
    /// </summary>
    /// <param name="fullName">The namespace-qualified type name.</param>
    /// <returns>The id.</returns>
    /// <remarks>
    ///     Written here as well as in the generator so that a hand-written replicator computes the
    ///     same number the generator would have, and so that the two can be checked against each
    ///     other by a test rather than by inspection. FNV-1a because it is four lines, has no
    ///     dependencies, and the requirement is stability across builds and platforms rather than
    ///     cryptographic strength — a collision is a build error here, not an attack.
    /// </remarks>
    public static uint HashTypeName(string fullName) {
        ArgumentNullException.ThrowIfNull(fullName);

        var hash = 2166136261u;

        foreach (var character in fullName) {
            hash ^= character;
            hash *= 16777619u;
        }

        // Zero is reserved so that a default-initialised id is never a valid one.
        return hash == 0 ? 1u : hash;
    }
}

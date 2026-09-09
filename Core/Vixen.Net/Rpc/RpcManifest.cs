// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Rpc;

/// <summary>Every remote call this build knows how to make, and nothing else.</summary>
/// <remarks>
///     <para>
///         The same closed set <c>ReplicationRegistry</c> is, for the same reason: a packet names a
///         position in this manifest, and a position that is not here is a packet that is refused.
///         Nothing is ever dispatched to a method a packet named.
///     </para>
///     <para>
///         Types are ordered by their hashed id rather than by when they registered, so two builds of
///         the same game agree on the ordering without having to agree on start-up order — which
///         generated registration across several assemblies cannot promise. Methods within a type are
///         ordered the same way, by the generator, before they get here.
///     </para>
/// </remarks>
public sealed class RpcManifest {
    readonly Dictionary<uint, RpcMethod[]> byTypeId = [];
    readonly List<uint> typeOrder = [];
    readonly List<RpcMethod[]> methodsByIndex = [];

    /// <summary>How many types declare calls.</summary>
    public int TypeCount => typeOrder.Count;

    /// <summary>How many calls there are, across every type.</summary>
    public int MethodCount {
        get {
            var total = 0;

            foreach (var methods in methodsByIndex) {
                total += methods.Length;
            }

            return total;
        }
    }

    /// <summary>
    ///     A hash over every id in the manifest, in order: one number that says whether two peers
    ///     have the same set of calls.
    /// </summary>
    /// <remarks>
    ///     Fold it into the session's content hash. Two builds that disagree here disagree about what
    ///     every index on the wire means, and the handshake is a far better place to find that out
    ///     than a server's dispatch table is.
    /// </remarks>
    public uint ManifestHash {
        get {
            var hash = 2166136261u;

            foreach (var methods in methodsByIndex) {
                foreach (var method in methods) {
                    for (var shift = 0; shift < 32; shift += 8) {
                        hash ^= (method.MethodId >> shift) & 0xFF;
                        hash *= 16777619u;
                    }
                }
            }

            return hash;
        }
    }

    /// <summary>Adds a type's calls.</summary>
    /// <param name="methods">
    ///     The type's methods, already ordered by id — which is what the generator emits, and what a
    ///     hand-written table has to match.
    /// </param>
    /// <exception cref="ArgumentException">
    ///     The type is registered twice, or two types hash the same, or the methods are not all of
    ///     one type, or they are out of order.
    /// </exception>
    public void Register(RpcMethod[] methods) {
        ArgumentNullException.ThrowIfNull(methods);

        if (methods.Length == 0) {
            return;
        }

        var typeId = methods[0].TypeId;

        for (var i = 0; i < methods.Length; i++) {
            if (methods[i].TypeId != typeId) {
                throw new ArgumentException(
                    $"'{methods[i]}' is not declared by '{methods[0].DeclaringType}'. One table is one type's calls.",
                    nameof(methods)
                );
            }

            // ⚠ Equal and descending are the same refusal and not the same fault, so they are not
            // the same sentence. A descending pair is a table somebody sorted wrongly; an equal
            // pair is two calls that hash the same — which is reachable with nothing exotic, since
            // the id is a 32-bit hash of the signature and two ordinary method names can land on
            // one. Saying "out of order" about that sends the reader to sort a table that is
            // already sorted. `VXNET2008` is the build error that catches the generated case.
            if (i > 0 && methods[i].MethodId == methods[i - 1].MethodId) {
                throw new ArgumentException(
                    $"'{methods[i - 1]}' and '{methods[i]}' both hash to {methods[i].MethodId}. A packet "
                    + "carries a call's position in this table, so two calls with one id cannot be told "
                    + "apart. Rename one of them.",
                    nameof(methods)
                );
            }

            if (i > 0 && methods[i].MethodId < methods[i - 1].MethodId) {
                throw new ArgumentException(
                    $"'{methods[i]}' is out of order. A table is ordered by method id, so that two builds "
                    + "number the calls the same without having to agree on anything else.",
                    nameof(methods)
                );
            }
        }

        if (byTypeId.ContainsKey(typeId)) {
            throw new ArgumentException(
                $"'{methods[0].DeclaringType}' is already in the manifest, or another type hashes the same as it.",
                nameof(methods)
            );
        }

        byTypeId[typeId] = methods;

        var at = typeOrder.Count;

        while (at > 0 && typeOrder[at - 1] > typeId) {
            at--;
        }

        typeOrder.Insert(at, typeId);
        methodsByIndex.Insert(at, methods);
        Reindex();
    }

    /// <summary>The position a type has on the wire.</summary>
    /// <param name="typeId">The type's hashed id.</param>
    /// <returns>Its index, or -1 if it is not in the manifest.</returns>
    public int IndexOf(uint typeId) => byTypeId.TryGetValue(typeId, out var methods) ? methods[0].TypeIndex : -1;

    /// <summary>Finds the call a pair of wire indices names.</summary>
    /// <param name="typeIndex">The type's position.</param>
    /// <param name="methodIndex">The method's position within it.</param>
    /// <param name="method">The call, if both are ones we have.</param>
    /// <returns>Whether they are.</returns>
    public bool TryGet(uint typeIndex, uint methodIndex, out RpcMethod? method) {
        if (typeIndex >= (uint)methodsByIndex.Count) {
            method = null;

            return false;
        }

        var methods = methodsByIndex[(int)typeIndex];

        if (methodIndex >= (uint)methods.Length) {
            method = null;

            return false;
        }

        method = methods[(int)methodIndex];

        return true;
    }

    void Reindex() {
        for (var type = 0; type < methodsByIndex.Count; type++) {
            var methods = methodsByIndex[type];

            for (var method = 0; method < methods.Length; method++) {
                methods[method].TypeIndex = type;
                methods[method].MethodIndex = method;
            }
        }
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Rpc;

/// <summary>One remote call: what it is, who may make it, and how it travels.</summary>
/// <remarks>
///     <para>
///         <b>Identified by a hash of the declaring type and the signature, not by a counter.</b>
///         Adding a method therefore does not renumber the others, and two builds that disagree about
///         what a call is are detected rather than misrouted into the wrong handler with the wrong
///         arguments — which is the failure that corrupts a save file rather than throwing.
///     </para>
///     <para>
///         The hash is the identity; the index is the encoding. A packet carries the position in the
///         manifest, because a 32-bit hash costs five bytes as a variable-length integer and calls are
///         the small packets. <see cref="RpcManifest.ManifestHash" /> is what makes that safe.
///     </para>
/// </remarks>
public sealed class RpcMethod {
    /// <summary>The type that declares the handler.</summary>
    public string DeclaringType { get; }

    /// <summary>The signature — the name and the parameter types, as the generator wrote it.</summary>
    public string Signature { get; }

    /// <summary>The declaring type's stable id.</summary>
    public uint TypeId { get; }

    /// <summary>This method's stable id, within its type.</summary>
    public uint MethodId { get; }

    /// <summary>Which way it travels.</summary>
    public RpcKind Kind { get; }

    /// <summary>Whether the caller has to own the object. Meaningful for <see cref="RpcKind.Server" />.</summary>
    public bool RequireOwnership { get; }

    /// <summary>How it is sent.</summary>
    public Channel Channel { get; }

    /// <summary>Whether the caller awaits an answer.</summary>
    /// <remarks>
    ///     <para>
    ///         An awaitable call is <b>sent reliably whatever its channel says</b>, and so is its
    ///         reply. A dropped fire-and-forget call is a lost update that the next one supersedes;
    ///         a dropped awaitable one is a caller waiting for an answer that will never come, and
    ///         the only thing standing between that and a leak is the timeout. Paying for
    ///         reliability is cheaper than explaining the alternative.
    ///     </para>
    ///     <para>
    ///         Also why an awaitable call is not a good idea on a hot path: it costs a round trip by
    ///         definition. It is for "may I buy this", "what is in the chest", "am I allowed to
    ///         start" — questions with an answer, asked rarely.
    ///     </para>
    /// </remarks>
    public bool ExpectsReply { get; }

    /// <summary>Who a server-to-client call goes to.</summary>
    public RpcTarget Target { get; }

    /// <summary>The declaring type's position in the manifest, or -1 before it is registered.</summary>
    public int TypeIndex { get; internal set; } = -1;

    /// <summary>This method's position within its type, or -1 before it is registered.</summary>
    public int MethodIndex { get; internal set; } = -1;

    /// <summary>Describes a call.</summary>
    /// <param name="declaringType">The type that declares the handler.</param>
    /// <param name="signature">The name and parameter types.</param>
    /// <param name="kind">Which way it travels.</param>
    /// <param name="requireOwnership">Whether the caller has to own the object.</param>
    /// <param name="channel">How it is sent.</param>
    /// <param name="target">Who a server-to-client call goes to.</param>
    /// <param name="expectsReply">
    ///     Whether the caller awaits an answer. An awaitable call carries a correlation id and is
    ///     sent reliably whatever its declared channel says — see <see cref="ExpectsReply" />.
    /// </param>
    public RpcMethod(
        string declaringType,
        string signature,
        RpcKind kind,
        bool requireOwnership,
        Channel channel,
        RpcTarget target,
        bool expectsReply = false
    ) {
        ExpectsReply = expectsReply;
        DeclaringType = declaringType;
        Signature = signature;
        Kind = kind;
        RequireOwnership = requireOwnership;
        Channel = channel;
        Target = target;
        TypeId = Hash(declaringType);
        MethodId = Hash($"{declaringType}.{signature}");
    }

    /// <inheritdoc />
    public override string ToString() => $"{DeclaringType}.{Signature}";

    /// <summary>The stable id of a name: 32-bit FNV-1a.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The id, never zero.</returns>
    /// <remarks>
    ///     The same function <c>ReplicationRegistry.HashTypeName</c> computes, written here as well
    ///     so that the two registers cannot drift apart, and asserted equal by a test.
    /// </remarks>
    public static uint Hash(string name) {
        ArgumentNullException.ThrowIfNull(name);

        var hash = 2166136261u;

        foreach (var character in name) {
            hash ^= character;
            hash *= 16777619u;
        }

        return hash == 0 ? 1u : hash;
    }
}

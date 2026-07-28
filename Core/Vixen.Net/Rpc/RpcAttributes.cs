// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Rpc;

/// <summary>Which way a remote call travels.</summary>
public enum RpcKind : byte {
    /// <summary>Client to server.</summary>
    Server = 0,

    /// <summary>Server to clients.</summary>
    Client = 1
}

/// <summary>Who a server-to-client call goes to.</summary>
public enum RpcTarget : byte {
    /// <summary>Everybody who can see the object it is about.</summary>
    Observers = 0,

    /// <summary>Only the connection that owns the object.</summary>
    Owner = 1,

    /// <summary>Everybody in the session, whether or not they can see it.</summary>
    All = 2
}

/// <summary>
///     Marks a method as the handler for a call a client makes and a server runs.
/// </summary>
/// <remarks>
///     <para>
///         <b>The method is the handler, not the call.</b> A separate sender is generated beside it,
///         reached through the type's <c>Rpc</c> accessor, so the call site reads
///         <c>Rpc.TakeDamage(dmg)</c> and says out loud that a packet is being sent.
///         <c>docs/plan/16-networking.md</c> explains why: the reference implementation
///         rewrites the method's prologue so that one name means both things, we cannot do that
///         (ADR-002 bans IL post-processing, and it would not survive NativeAOT), and the ceremony we
///         are left with is one line better than what it replaced. Transparent RPC hides latency and
///         bandwidth at the call site; making it visible is a feature.
///     </para>
///     <para>
///         The handler runs <b>only on the server</b>. A packet naming it is checked against the
///         manifest, against <see cref="RequireOwnership" /> and against the connection's rate limit
///         before anything is invoked.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ServerRpcAttribute : Attribute {
    /// <summary>
    ///     Whether the caller must own the object the call is about. On by default, which is the
    ///     safe way round: an RPC that anybody may invoke on anybody's object is the shape of most
    ///     cheating, and it should be a decision somebody wrote down rather than a default.
    /// </summary>
    public bool RequireOwnership { get; set; } = true;

    /// <summary>How to send it. Reliable by default: a call that is lost did not happen.</summary>
    public Channel Channel { get; set; } = Channel.Reliable;
}

/// <summary>Marks a method as the handler for a call the server makes and clients run.</summary>
/// <remarks>
///     The mirror of <see cref="ServerRpcAttribute" />, and the same rule: the method is the handler
///     and the generated sender beside it is the call. Sending one from anything that is not the
///     server is refused rather than sent, and counted.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ClientRpcAttribute : Attribute {
    /// <summary>Who gets it.</summary>
    public RpcTarget Target { get; set; } = RpcTarget.Observers;

    /// <summary>
    ///     How to send it. Unreliable by default, because most of these are effects: a hit spark that
    ///     arrives late is worse than one that never arrives.
    /// </summary>
    public Channel Channel { get; set; } = Channel.Unreliable;
}

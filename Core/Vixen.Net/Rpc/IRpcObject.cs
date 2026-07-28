// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Messaging;
using Vixen.Net.Replication;
using Vixen.Net.Sessions;

namespace Vixen.Net.Rpc;

/// <summary>What a type declaring remote calls has to be able to say about itself.</summary>
/// <remarks>
///     Two things, and no base class: which networked object the calls are about, and where to send
///     them. An interface rather than a <c>NetworkBehaviour</c> to inherit from, because the type
///     that wants RPCs is usually already deriving from something — a <c>Behavior</c>, most of the
///     time — and single inheritance is not a budget to spend on this.
/// </remarks>
public interface IRpcObject {
    /// <summary>The networked object these calls are about.</summary>
    NetworkId NetworkId { get; }

    /// <summary>Where calls go. Null before the object has been attached to a session.</summary>
    RpcRouter? RpcRouter { get; }
}

/// <summary>The dispatch table for one type's remote calls. Implemented by generated code.</summary>
/// <remarks>
///     A generated <c>switch</c> over an index, and deliberately nothing cleverer. It is the only
///     thing standing between a packet and a method call, so it is code somebody can read: no
///     reflection, no delegates built at start-up, nothing that trimming can remove or that
///     NativeAOT cannot see through.
/// </remarks>
public interface IRpcInvoker {
    /// <summary>The declaring type's stable id.</summary>
    uint RpcTypeId { get; }

    /// <summary>Runs one of this type's handlers.</summary>
    /// <param name="methodIndex">Which one, as a position in this type's table.</param>
    /// <param name="context">Who is calling and about what.</param>
    /// <param name="reader">The arguments.</param>
    /// <returns>
    ///     Whether the call ran. False means the arguments did not decode or the index is not one of
    ///     ours — either way the packet is refused rather than half-applied.
    /// </returns>
    bool Invoke(uint methodIndex, in RpcContext context, ref BitReader reader);
}

/// <summary>Who is calling, and about what.</summary>
/// <param name="Sender">
///     The player whose packet this is, or <see cref="PlayerId.None" /> when the server is the one
///     that sent it.
/// </param>
/// <param name="Target">The networked object the call is about.</param>
/// <param name="Method">Which call it is.</param>
/// <remarks>
///     Handed to the handler so it can answer "who did this?" without the sender having to put the
///     caller's id in the arguments — where a client would be the one filling it in, which is the
///     whole problem.
/// </remarks>
public readonly record struct RpcContext(PlayerId Sender, NetworkId Target, RpcMethod Method);
